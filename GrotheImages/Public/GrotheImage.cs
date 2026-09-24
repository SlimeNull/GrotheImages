using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GrotheImages;

/// <summary>
/// A tiled image, called the <b>Grothe Image</b> throughout this library. It owns the tile storage of one
/// or more layers and moves pixels between that storage and a <b>User Image</b>: a rectangle of pixels in
/// caller memory described by a pointer, a size and a stride.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Update"/> copies a User Image into the Grothe Image with a User Image to Grothe Image
/// transform; <see cref="Load(int, nint, int, int, int, PixelFormat, TransformMatrix)"/> renders the Grothe
/// Image into a User Image with a Grothe Image to User Image transform. <see cref="UpdateTile"/> and
/// <see cref="BlendSeams"/> work in Grothe Image coordinates only.
/// </para>
/// <para>
/// Public members are thread safe: commands are serialized on the shared Direct3D device. Dispose the
/// instance to release its tiles and shaders - the device is shared and only released with the last image.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Creates an empty Grothe Image of the requested geometry and storage format. Every layer starts at
    /// the zero value of <paramref name="format"/>; nothing is uploaded until the first write.
    /// </summary>
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

    /// <summary>Geometry of the Grothe Image: tile grid, overlap and logical size.</summary>
    public GrotheImageInfo Info { get; }

    /// <summary>Storage format of every tile plane of every layer.</summary>
    public PixelFormat Format { get; }

    /// <summary>Layer names in layer index order.</summary>
    public ReadOnlyCollection<string> LayerNames { get; }

    /// <summary>Writes the Grothe Image, including tile geometry, layers and every written tile, to a stream.</summary>
    public void Serialize(Stream stream)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_layers == null)
            {
                // Nothing was ever uploaded, so serialization does not need the device.
                GrotheImageSerializer.Write(this, stream);
                return;
            }
            lock (D3D11DeviceContext.CommandLock) GrotheImageSerializer.Write(this, stream);
        }
    }

    /// <summary>Reads a Grothe Image written by <see cref="Serialize"/>.</summary>
    public static GrotheImage Deserialize(Stream stream) => GrotheImageSerializer.Read(stream);

    /// <summary>Writes the Grothe Image to a file.</summary>
    public void Save(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) Serialize(stream);
    }

    /// <summary>Reads a Grothe Image from a file.</summary>
    public static GrotheImage Open(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return Deserialize(stream);
    }

    /// <summary>
    /// Copies a User Image into one layer of the Grothe Image. <paramref name="transformMatrix"/> maps User
    /// Image coordinates to Grothe Image coordinates, so <see cref="TransformMatrix.Identity"/> copies the
    /// pixels one to one. A Grothe Image pixel whose sample falls outside the User Image is clipped: it keeps
    /// the value the tile already stores, so an update only paints where the User Image lands and never
    /// clears anything.
    /// </summary>
    public void Update(int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            ValidateTransferArguments(scan0, width, height, stride, format, transformMatrix, matrixMapsUserImage: true);
            EnsureGraphics();
            lock (D3D11DeviceContext.CommandLock)
                GpuImageProcessor.Update(this, layerIndex, scan0, width, height, stride, format, transformMatrix);
        }
    }

    /// <summary>
    /// Renders one layer of the Grothe Image into a User Image. <paramref name="transformMatrix"/> maps
    /// Grothe Image coordinates to User Image coordinates; pixels that no Grothe Image pixel maps onto stay
    /// transparent.
    /// </summary>
    public void Load(int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            ValidateTransferArguments(scan0, width, height, stride, format, transformMatrix, matrixMapsUserImage: false);
            EnsureGraphics();
            lock (D3D11DeviceContext.CommandLock)
                GpuImageProcessor.Load(this, layerIndex, null, scan0, width, height, stride, format, transformMatrix);
        }
    }

    /// <summary>
    /// Compiles an expression over the layer names into a reusable composition. The composition reads the
    /// logical channels of the layers it mentions (RGB for RGB storage, v/v/v/1 for Gray8, Y/U/V/1 for YUV)
    /// and only those layers are bound when it is rendered.
    /// </summary>
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
            lock (D3D11DeviceContext.CommandLock) GpuImageProcessor.BlendSeams(this);
        }
    }

    /// <summary>Renders a composition of the layers into a User Image; the matrix maps Grothe Image coordinates to User Image coordinates.</summary>
    public void Load(LayerCompose layerCompose, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transformMatrix)
    {
        if (layerCompose == null) throw new ArgumentNullException(nameof(layerCompose));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_composes.Contains(layerCompose))
                throw new ArgumentException("The layer composition belongs to another image or has been disposed.", nameof(layerCompose));
            ValidateTransferArguments(scan0, width, height, stride, format, transformMatrix, matrixMapsUserImage: false);
            EnsureGraphics();
            lock (D3D11DeviceContext.CommandLock)
                GpuImageProcessor.Load(this, -1, layerCompose, scan0, width, height, stride, format, transformMatrix);
        }
    }

    /// <summary>
    /// Writes one whole tile of one layer from a User Image, without any geometric transform. The input has
    /// to be exactly <see cref="GrotheImageInfo.TileWidth"/> x <see cref="GrotheImageInfo.TileHeight"/>
    /// pixels; edge tiles use the same size, because the logical bounds come from the grid formula.
    /// </summary>
    public void UpdateTile(int layerIndex, long tileRow, long tileColumn, nint scan0, int width, int height, int stride, PixelFormat format)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateLayerIndex(layerIndex);
            ValidateTileArguments(tileRow: tileRow, tileColumn: tileColumn, scan0, width, height, stride, format);
            EnsureGraphics();
            lock (D3D11DeviceContext.CommandLock)
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

    /// <summary>
    /// Releases the tiles and shaders of this image. The Direct3D device is shared by every image and is
    /// released with the last one.
    /// </summary>
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
            if (_layers != null || _graphics != null)
            {
                lock (D3D11DeviceContext.CommandLock)
                {
                    if (_layers != null)
                    {
                        foreach (LayerStore layer in _layers) layer.Dispose();
                        _layers = null;
                    }
                    _graphics?.Dispose();
                    _graphics = null;
                }
            }
        }
    }

#if DEBUG
    /// <summary>
    /// Debug builds report an image that was garbage collected without <see cref="Dispose"/>: the tiles and
    /// the load/update/blend programs of that image are still alive at that point. The body deliberately
    /// touches no field, because a constructor that threw still gets finalized.
    /// </summary>
    ~GrotheImage()
    {
        if (!_disposed) Debug.WriteLine("GrotheImage was finalized without being disposed.");
    }
#endif

    private void EnsureGraphics()
    {
        if (_graphics != null) return;
        _graphics = new D3D11DeviceContext();
        _layers = new LayerStore[LayerNames.Count];
        for (int i = 0; i < _layers.Length; i++) _layers[i] = new LayerStore(_graphics, Info, Format);
    }

    private const string TransformParameter = "transformMatrix";

    /// <summary>
    /// Validates the arguments shared by <c>Update</c> and <c>Load</c>. <paramref name="matrixMapsUserImage"/>
    /// tells the direction of <paramref name="matrix"/>: <c>true</c> for Update (User Image to Grothe Image),
    /// <c>false</c> for Load (Grothe Image to User Image). Either way the User Image rectangle has to land
    /// in Grothe Image space with an image at every corner, which is the same range the render pass computes
    /// later, so a perspective that puts a corner at infinity fails here instead of inside the shader.
    /// </summary>
    private void ValidateTransferArguments(nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix matrix, bool matrixMapsUserImage)
    {
        if (scan0 == 0) throw new ArgumentNullException(nameof(scan0));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        PixelFormatRules.ValidateTransferFormat(format, nameof(format));
        int bytes = PixelFormatRules.BytesPerPixel(format);
        if (stride < checked(width * bytes)) throw new ArgumentOutOfRangeException(nameof(stride));
        if (!matrix.IsFinite || !matrix.TryInvert(out TransformMatrix inverse))
            throw new ArgumentException("The transform matrix must be finite and invertible.", TransformParameter);

        TransformMatrix userToGrothe = matrixMapsUserImage ? matrix : inverse;
        if (!TileGrid.TryGetCoveredRegion(userToGrothe, width, height, out _))
            throw new ArgumentException("The transform matrix maps a corner of the User Image to a point at infinity.", TransformParameter);
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
