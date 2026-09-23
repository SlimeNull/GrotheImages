using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace GrotheImages;

internal sealed class GrotheImageLoaderV1 : IGrotheImageLoader
{
    public int Version => 1;

    public GrotheImage Read(BinaryReader reader)
    {
        GrotheImage image = null;
        try
        {
            int tileWidth = reader.ReadInt32();
            int tileHeight = reader.ReadInt32();
            long rows = reader.ReadInt64();
            long columns = reader.ReadInt64();
            int overlapX = reader.ReadInt32();
            int overlapY = reader.ReadInt32();
            PixelFormat format = (PixelFormat)reader.ReadInt32();
            if (!Enum.IsDefined(typeof(PixelFormat), format)) throw new InvalidDataException("Unknown storage pixel format.");
            var info = GrotheImageInfo.FromTiles(tileWidth, tileHeight, rows, columns, overlapX, overlapY);
            int layerCount = reader.ReadInt32();
            if (layerCount < 1 || layerCount > 1024) throw new InvalidDataException("Invalid layer count.");
            var names = new string[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                names[i] = reader.ReadString();
                if (names[i].Length > 256) throw new InvalidDataException("Layer name is too long.");
            }
            long recordCount = reader.ReadInt64();
            if (recordCount < 0 || recordCount > checked(rows * columns * layerCount))
                throw new InvalidDataException("Invalid tile record count.");

            image = new GrotheImage(info, format, names);
            var seen = new HashSet<Tuple<int, long, long>>();
            for (long record = 0; record < recordCount; record++)
            {
                int layer = reader.ReadInt32();
                long row = reader.ReadInt64();
                long column = reader.ReadInt64();
                if (layer < 0 || layer >= layerCount || row < 0 || row >= rows || column < 0 || column >= columns || !seen.Add(Tuple.Create(layer, row, column)))
                    throw new InvalidDataException("Invalid or repeated tile coordinate.");
                int planes = reader.ReadByte();
                int expectedPlanes = format == PixelFormat.Yuv444 || format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420 ? 2 : 1;
                if (planes != expectedPlanes) throw new InvalidDataException("Unexpected tile plane count.");
                image.EnsureGraphicsForSerialization();
                var page = image.GetLayer(layer).GetPageForTile(row, column, out int slice);
                if (planes == 1) ReadPlane(reader, image.Graphics, page.Color, slice);
                else
                {
                    ReadPlane(reader, image.Graphics, page.Y, slice);
                    ReadPlane(reader, image.Graphics, page.Uv, slice);
                }
                image.GetLayer(layer).MarkWritten(row, column);
            }
            return image;
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    private static void ReadPlane(BinaryReader reader, D3D11DeviceContext graphics, TileArrayResource plane, int slice)
    {
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        int length = reader.ReadInt32();
        int rowBytes = checked(plane.Width * GrotheImageSerializer.BytesPerTexel(plane.Format));
        if (width != plane.Width || height != plane.Height || length != checked(rowBytes * plane.Height))
            throw new InvalidDataException("Tile plane dimensions or byte count do not match its storage format.");
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException("Tile plane data is truncated.");
        GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { graphics.Context.UpdateSubresource(plane.Texture, slice, null, pinned.AddrOfPinnedObject(), rowBytes, 0); }
        finally { pinned.Free(); }
    }
}
