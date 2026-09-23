using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vortice.Direct3D11;

namespace GrotheImages;

internal interface IGrotheImageLoader
{
    int Version { get; }
    GrotheImage Read(BinaryReader reader);
}

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

            for (int layer = 0; image.HasTileStorage && layer < image.LayerNames.Count; layer++)
            {
                var written = new List<long>(image.GetLayer(layer).WrittenTiles);
                written.Sort();
                foreach (long index in written)
                {
                    long row = index / info.TileColumns;
                    long column = index % info.TileColumns;
                    TileArrayPage page = image.GetLayer(layer).GetPageForTile(row, column, out int slice);
                    writer.Write(layer);
                    writer.Write(row);
                    writer.Write(column);
                    if (page.Color != null)
                    {
                        writer.Write((byte)1);
                        WritePlane(writer, image.Graphics, page.Color, slice);
                    }
                    else
                    {
                        writer.Write((byte)2);
                        WritePlane(writer, image.Graphics, page.Y, slice);
                        WritePlane(writer, image.Graphics, page.Uv, slice);
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

    private static void WritePlane(BinaryWriter writer, D3D11DeviceContext graphics, TileArrayResource plane, int slice)
    {
        int rowBytes = checked(plane.Width * BytesPerTexel(plane.Format));
        int length = checked(rowBytes * plane.Height);
        writer.Write(plane.Width);
        writer.Write(plane.Height);
        writer.Write(length);

        using (ID3D11Texture2D staging = graphics.Device.CreateTexture2D(
            plane.Format, plane.Width, plane.Height, 1, 1, null,
            BindFlags.None, ResourceOptionFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read))
        {
            graphics.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, plane.Texture, slice, null);
            MappedSubresource mapped = graphics.Context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < plane.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(mapped.DataPointer, checked(y * mapped.RowPitch)), row, 0, rowBytes);
                    writer.Write(row);
                }
            }
            finally { graphics.Context.Unmap(staging, 0); }
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
