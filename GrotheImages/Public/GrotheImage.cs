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
    private bool _disposed;

    public GrotheImage(GrotheImageInfo info, PixelFormat format, params string[] layerNames)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
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
            return ExpressionCompiler.Compile(expression, LayerNames);
        }
    }

    public void Load(LayerCompose layerCompose, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        if (layerCompose == null) throw new ArgumentNullException(nameof(layerCompose));
        lock (_sync)
        {
            ThrowIfDisposed();
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
            GpuImageProcessor.UpdateTile(this, layerIndex, tileRow, tileColumn, scan0, width, height, stride, format, IntPtr.Zero, 0, 0, format);
        }
    }

    public void UpdateTile(int layerIndex, long tileRow, long tileColumn, nint yScan0, int yStride, nint uvScan0, int uvStride, PixelFormat format)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            if (format != PixelFormat.Yuv422 && format != PixelFormat.Yuv420)
                throw new ArgumentException("The two-plane UpdateTile overload is only valid for Yuv422 or Yuv420.", nameof(format));
            if (format != Format) throw new ArgumentException("UpdateTile format must match GrotheImage.Format.", nameof(format));
            if (yScan0 == 0 || uvScan0 == 0) throw new ArgumentNullException(nameof(yScan0));
            if (yStride <= 0 || uvStride <= 0) throw new ArgumentOutOfRangeException(nameof(yStride));
            TileGrid.GetLinearIndex(Info, tileRow, tileColumn);
            EnsureGraphics();
            GpuImageProcessor.UpdateTile(this, layerIndex, tileRow, tileColumn, yScan0, Info.TileWidth, Info.TileHeight, yStride, format, uvScan0, UvTileWidth, uvStride, format);
        }
    }

    internal D3D11DeviceContext Graphics => _graphics;
    internal LayerStore GetLayer(int index) => _layers[index];
    internal bool HasTileStorage => _layers != null;
    internal void EnsureGraphicsForSerialization() => EnsureGraphics();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_layers != null)
            {
                foreach (LayerStore layer in _layers) layer.Dispose();
                _layers = null;
            }
            _graphics?.Dispose();
            _graphics = null;
        }
    }

    private int UvTileWidth => Format == PixelFormat.Yuv422 || Format == PixelFormat.Yuv420 ? Info.TileWidth / 2 : Info.TileWidth;

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
        if (format != Format) throw new ArgumentException("UpdateTile format must match GrotheImage.Format.", nameof(format));
        if (width != Info.TileWidth || height != Info.TileHeight) throw new ArgumentException("Every tile has the same full storage dimensions.");
        if (format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420) throw new ArgumentException("Use the Y/UV UpdateTile overload for subsampled YUV.", nameof(format));
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
