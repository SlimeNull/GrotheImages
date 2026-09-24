using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;

namespace GrotheImages;

internal interface IGrotheImageLoader
{
    int Version { get; }
    GrotheImage Read(BinaryReader reader);
}

/// <summary>
/// The versioned binary format of a Grothe Image: header, layer names and one record per written tile, each
/// record holding the raw bytes of every storage plane of that tile.
/// </summary>
internal static class GrotheImageSerializer
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GROTHEIM");
    private static readonly IReadOnlyDictionary<int, IGrotheImageLoader> Loaders =
        new Dictionary<int, IGrotheImageLoader> { { 1, new GrotheImageLoaderV1() } };

    public static void Write(GrotheImage image, Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite) throw new ArgumentException("The destination stream is not writable.", nameof(stream));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(1);
            var info = image.Info;
            writer.Write(info.TileWidth);
            writer.Write(info.TileHeight);
            writer.Write(info.TileRows);
            writer.Write(info.TileColumns);
            writer.Write(info.TileOverlapX);
            writer.Write(info.TileOverlapY);
            writer.Write((int)image.Format);
            writer.Write(image.LayerNames.Count);
            foreach (string name in image.LayerNames) writer.Write(name);

            long count = 0;
            if (image.HasTileStorage)
                for (int layer = 0; layer < image.LayerNames.Count; layer++)
                    count = checked(count + image.GetLayer(layer).WrittenTiles.Count);
            writer.Write(count);

            if (!image.HasTileStorage)
            {
                writer.Flush();
                return;
            }

            // One staging texture per plane shape is reused for every tile instead of allocating one per
            // tile, which matters for images with thousands of tiles.
            using (var staging = new StagingPool(image.Graphics))
            {
                for (int layer = 0; layer < image.LayerNames.Count; layer++)
                {
                    var written = new List<long>(image.GetLayer(layer).WrittenTiles);
                    written.Sort();
                    foreach (long index in written)
                    {
                        long row = index / info.TileColumns;
                        long column = index % info.TileColumns;
                        if (!image.GetLayer(layer).TryGetPageForTile(row, column, out TileArrayPage page, out int slice))
                            throw new GrotheImageException("A tile is marked as written but its texture page is missing.");
                        writer.Write(layer);
                        writer.Write(row);
                        writer.Write(column);
                        if (page.Color != null)
                        {
                            writer.Write((byte)1);
                            WritePlane(writer, image.Graphics, staging, page.Color, slice);
                        }
                        else
                        {
                            writer.Write((byte)2);
                            WritePlane(writer, image.Graphics, staging, page.Y, slice);
                            WritePlane(writer, image.Graphics, staging, page.Uv, slice);
                        }
                    }
                }
            }
            writer.Flush();
        }
    }

    public static GrotheImage Read(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("The source stream is not readable.", nameof(stream));
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !SameBytes(magic, Magic))
                throw new InvalidDataException("Not a GrotheImage file (invalid magic).");
            int version = reader.ReadInt32();
            if (!Loaders.TryGetValue(version, out IGrotheImageLoader loader))
                throw new NotSupportedException("Unsupported GrotheImage file version: " + version + ".");
            return loader.Read(reader);
        }
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void WritePlane(BinaryWriter writer, D3D11DeviceContext graphics, StagingPool staging, TileArrayResource plane, int slice)
    {
        int rowBytes = checked(plane.Width * BytesPerTexel(plane.Format));
        int length = checked(rowBytes * plane.Height);
        writer.Write(plane.Width);
        writer.Write(plane.Height);
        writer.Write(length);

        ID3D11Texture2D texture = staging.Get(plane.Format, plane.Width, plane.Height);
        graphics.Context.CopySubresourceRegion(texture, 0, 0, 0, 0, plane.Texture, slice, null);
        MappedSubresource mapped = graphics.Context.Map(texture, 0, MapMode.Read, MapFlags.None);
        try
        {
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < plane.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * mapped.RowPitch)), row, 0, rowBytes);
                writer.Write(row);
            }
        }
        finally { graphics.Context.Unmap(texture, 0); }
    }

    /// <summary>Staging textures of a serialization, one per distinct plane shape, released with the write.</summary>
    private sealed class StagingPool : IDisposable
    {
        private readonly D3D11DeviceContext _graphics;
        private readonly Dictionary<string, ID3D11Texture2D> _textures = new Dictionary<string, ID3D11Texture2D>(StringComparer.Ordinal);

        public StagingPool(D3D11DeviceContext graphics)
        {
            _graphics = graphics;
        }

        public ID3D11Texture2D Get(Vortice.DXGI.Format format, int width, int height)
        {
            string key = format + ":" + width + "x" + height;
            ID3D11Texture2D texture;
            if (_textures.TryGetValue(key, out texture)) return texture;
            texture = _graphics.Device.CreateTexture2D(
                format, width, height, 1, 1, null,
                BindFlags.None, ResourceOptionFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            _textures.Add(key, texture);
            return texture;
        }

        public void Dispose()
        {
            foreach (ID3D11Texture2D texture in _textures.Values) texture.Dispose();
            _textures.Clear();
        }
    }

    internal static int BytesPerTexel(Vortice.DXGI.Format format)
    {
        switch (format)
        {
            case Vortice.DXGI.Format.R8_UNorm: return 1;
            case Vortice.DXGI.Format.R8G8_UNorm: return 2;
            case Vortice.DXGI.Format.R8G8B8A8_UNorm:
            case Vortice.DXGI.Format.B8G8R8A8_UNorm: return 4;
            default: throw new InvalidDataException("Unsupported tile plane format: " + format + ".");
        }
    }
}
