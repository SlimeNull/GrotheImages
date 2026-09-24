using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;

namespace GrotheImages;

internal sealed class GrotheImageLoaderV1 : IGrotheImageLoader
{
    private const int MaxLayerNameLength = 256;
    private const int MaxLayerCount = 1024;

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
            if (layerCount < 1 || layerCount > MaxLayerCount) throw new InvalidDataException("Invalid layer count.");
            var names = new string[layerCount];
            for (int i = 0; i < layerCount; i++) names[i] = ReadLayerName(reader);
            long recordCount = reader.ReadInt64();
            if (recordCount < 0 || recordCount > checked(rows * columns * layerCount))
                throw new InvalidDataException("Invalid tile record count.");

            try
            {
                image = new GrotheImage(info, format, names);
            }
            catch (ArgumentException ex)
            {
                // The geometry and the names come from the file, so a rejected combination is a corrupt file
                // rather than a bad argument of the caller.
                throw new InvalidDataException("The stored geometry or layer names describe no valid GrotheImage: " + ex.Message, ex);
            }

            var seen = new HashSet<Tuple<int, long, long>>();
            // An image with no written tiles needs no device at all; anything else uploads through the
            // process-wide immediate context, which is shared with every other image.
            if (recordCount > 0)
            {
                lock (D3D11DeviceContext.CommandLock)
                {
                    image.EnsureGraphicsForSerialization();
                    for (long record = 0; record < recordCount; record++)
                    {
                        int layer = reader.ReadInt32();
                        long row = reader.ReadInt64();
                        long column = reader.ReadInt64();
                        if (layer < 0 || layer >= layerCount || row < 0 || row >= rows || column < 0 || column >= columns || !seen.Add(Tuple.Create(layer, row, column)))
                            throw new InvalidDataException("Invalid or repeated tile coordinate.");
                        int planes = reader.ReadByte();
                        int expectedPlanes = PixelFormatRules.IsYuv(format) ? 2 : 1;
                        if (planes != expectedPlanes) throw new InvalidDataException("Unexpected tile plane count.");
                        var page = image.GetLayer(layer).GetPageForTile(row, column, out int slice);
                        if (planes == 1) ReadPlane(reader, image.Graphics, page.Color, slice);
                        else
                        {
                            ReadPlane(reader, image.Graphics, page.Y, slice);
                            ReadPlane(reader, image.Graphics, page.Uv, slice);
                        }
                        image.GetLayer(layer).MarkWritten(row, column);
                    }
                }
            }
            return image;
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one layer name. The length is decoded by hand so a corrupt length prefix fails the size check
    /// before anything is allocated for it.
    /// </summary>
    private static string ReadLayerName(BinaryReader reader)
    {
        int length = 0;
        int shift = 0;
        while (true)
        {
            if (shift > 28) throw new InvalidDataException("Layer name length is malformed.");
            byte value = reader.ReadByte();
            length |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0) break;
            shift += 7;
        }
        if (length <= 0 || length > MaxLayerNameLength) throw new InvalidDataException("Invalid layer name length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Layer name is truncated.");
        return Encoding.UTF8.GetString(bytes);
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
