using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace GrotheImages;

public sealed class GrotheImage : IDisposable
{
    private readonly object _sync = new object();
    private D3D11DeviceContext _graphics;
    private LayerStore[] _layers;
    private LoadProgram _loadProgram;
    private UpdateProgram _updateProgram;
    private readonly HashSet<LayerCompose> _composes = new HashSet<LayerCompose>();
    private BlendProgram _blendProgram;
    private bool _disposed;

    public GrotheImage(GrotheImageInfo info, PixelFormat format, params string[] layerNames)
    {
        info.ValidateComputedValues();
        Info = info;
        Info.ValidateForFormat(format);
        Format = format;
        if (layerNames == null || layerNames.Length == 0) throw new ArgumentException("At least one layer is required.", nameof(layerNames));

        var names = new List<string>(layerNames.Length);
        foreach (string name in layerNames)
        {
            ValidateLayerName(name);
            if (names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Layer names must be unique.", nameof(layerNames));
            names.Add(name);
        }
        LayerNames = new ReadOnlyCollection<string>(names);
    }

    public GrotheImageInfo Info { get; }
    public PixelFormat Format { get; }
    public ReadOnlyCollection<string> LayerNames { get; }

    public void Serialize(Stream stream)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            GrotheImageSerializer.Write(this, stream);
        }
    }

    public static GrotheImage Deserialize(Stream stream) => GrotheImageSerializer.Read(stream);

    public void Save(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) Serialize(stream);
    }

    public static GrotheImage Open(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return Deserialize(stream);
    }

    public void Update(int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            ValidateTransferArguments(scan0, width, height, stride, format, transformMatrix);
            EnsureGraphics();
            GpuImageProcessor.Update(this, layerIndex, scan0, width, height, stride, format, transformMatrix);
        }
    }

    public void Load(int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            ValidateTransferArguments(scan0, width, height, stride, format, transformMatrix);
            EnsureGraphics();
            GpuImageProcessor.Load(this, layerIndex, null, scan0, width, height, stride, format, transformMatrix);
        }
    }

    public LayerCompose CreateLayerCompose(string expression)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var compose = ExpressionCompiler.Compile(expression, LayerNames);
            compose.Attach(this);
            _composes.Add(compose);
            return compose;
        }
    }

    /// <summary>
    /// Cross fades the overlapping border of every pair of neighbouring tiles, in every layer, so the joins
    /// between tiles disappear. Tiles are photographed separately and never match exactly, so calling this
    /// once after every tile has been written replaces the hard switch in the overlap with a smooth ramp.
    /// Tiles that were never written are left alone, and blending a second time changes nothing.
    /// </summary>
    public void BlendSeams()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Info.TileOverlapX == 0 && Info.TileOverlapY == 0) return;
            EnsureGraphics();
            GpuImageProcessor.BlendSeams(this);
        }
    }

    public void Load(LayerCompose layerCompose, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        if (layerCompose == null) throw new ArgumentNullException(nameof(layerCompose));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(layerCompose.Owner, this)) throw new ArgumentException("The layer composition belongs to another image or has been disposed.", nameof(layerCompose));
            ValidateTransferArguments(scan0, width, height, stride, format, transformMatrix);
            EnsureGraphics();
            GpuImageProcessor.Load(this, -1, layerCompose, scan0, width, height, stride, format, transformMatrix);
        }
    }

    public void UpdateTile(int layerIndex, long tileRow, long tileColumn, nint scan0, int width, int height, int stride, PixelFormat format)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            ValidateTileArguments(tileRow: tileRow, tileColumn: tileColumn, scan0, width, height, stride, format);
            EnsureGraphics();
            GpuImageProcessor.UpdateTile(this, layerIndex, tileRow, tileColumn, scan0, width, height, stride, format);
        }
    }

    internal D3D11DeviceContext Graphics => _graphics;
    internal LayerStore GetLayer(int index) => _layers[index];
    internal bool HasTileStorage => _layers != null;
    internal void EnsureGraphicsForSerialization() => EnsureGraphics();
    internal object SyncRoot => _sync;
    internal LoadProgram GetLoadProgram() => _loadProgram ?? (_loadProgram = new LoadProgram(this, null));
    internal UpdateProgram GetUpdateProgram() => _updateProgram ?? (_updateProgram = new UpdateProgram());
    internal BlendProgram GetBlendProgram() => _blendProgram ?? (_blendProgram = new BlendProgram(this));
    internal void ReleaseCompose(LayerCompose compose) => _composes.Remove(compose);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (LayerCompose compose in _composes) compose.DisposeProgram();
            _composes.Clear();
            _loadProgram?.Dispose();
            _loadProgram = null;
            _updateProgram?.Dispose();
            _updateProgram = null;
            _blendProgram?.Dispose();
            _blendProgram = null;
            if (_layers != null)
            {
                foreach (LayerStore layer in _layers) layer.Dispose();
                _layers = null;
            }
            _graphics?.Dispose();
            _graphics = null;
        }
    }

    private void EnsureGraphics()
    {
        if (_graphics != null) return;
        _graphics = new D3D11DeviceContext();
        _layers = new LayerStore[LayerNames.Count];
        for (int i = 0; i < _layers.Length; i++) _layers[i] = new LayerStore(_graphics, Info, Format);
    }

    private void ValidateTransferArguments(nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix matrix)
    {
        if (scan0 == 0) throw new ArgumentNullException(nameof(scan0));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        PixelFormatRules.ValidateTransferFormat(format, nameof(format));
        int bytes = PixelFormatRules.BytesPerPixel(format);
        if (stride < checked(width * bytes)) throw new ArgumentOutOfRangeException(nameof(stride));
        if (!matrix.IsFinite || !matrix.TryInvert(out _)) throw new ArgumentException("The transform matrix must be finite and invertible.", nameof(matrix));
    }

    private void ValidateTileArguments(long tileRow, long tileColumn, nint scan0, int width, int height, int stride, PixelFormat format)
    {
        TileGrid.GetLinearIndex(Info, tileRow, tileColumn);
        if (scan0 == 0) throw new ArgumentNullException(nameof(scan0));
        if (width != Info.TileWidth || height != Info.TileHeight) throw new ArgumentException("Every tile has the same full storage dimensions.");
        PixelFormatRules.ValidateTransferFormat(format, nameof(format));
        if (stride <= 0 || stride < checked(width * PixelFormatRules.BytesPerPixel(format))) throw new ArgumentOutOfRangeException(nameof(stride));
    }

    private void ValidateLayerIndex(int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= LayerNames.Count) throw new ArgumentOutOfRangeException(nameof(layerIndex));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GrotheImage));
    }

    private static void ValidateLayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Layer name cannot be empty.", nameof(name));
        if (!(char.IsLetter(name[0]) || name[0] == '_')) throw new ArgumentException("Layer names must start with a letter or underscore.", nameof(name));
        for (int i = 0; i < name.Length; i++)
            if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) throw new ArgumentException("Layer names may contain only letters, digits and underscores.", nameof(name));
    }
}
