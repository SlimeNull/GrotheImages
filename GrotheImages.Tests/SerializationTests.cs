using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace GrotheImages.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void EmptyImageRoundTripsWithoutInitializingGpu()
    {
        using var image = new GrotheImage(GrotheImageInfo.FromTiles(4, 2, 2, 3, 0, 0), PixelFormat.Yuv420, "a", "b");
        using var stream = new MemoryStream();
        image.Serialize(stream);
        Assert.True(stream.CanWrite);
        Assert.Equal(1, BitConverter.ToInt32(stream.ToArray(), 8));
        stream.Position = 0;
        using var copy = GrotheImage.Deserialize(stream);
        Assert.True(stream.CanRead);
        Assert.Equal(image.Info.Width, copy.Info.Width);
        Assert.Equal(image.Info.Height, copy.Info.Height);
        Assert.Equal(image.Format, copy.Format);
        Assert.Equal(image.LayerNames, copy.LayerNames);
    }

    [Fact]
    public void UnknownVersionAndWrongMagicAreRejected()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        using var stream = new MemoryStream();
        image.Serialize(stream);
        byte[] bytes = stream.ToArray();
        bytes[8] = 99;
        using (var unknown = new MemoryStream(bytes))
            Assert.Throws<NotSupportedException>(() => GrotheImage.Deserialize(unknown));
        bytes[0] = 0;
        using (var invalid = new MemoryStream(bytes))
            Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(invalid));
    }

    [Fact]
    public void AllWrittenLayersAndTilesRoundTripTheirPixels()
    {
        var info = GrotheImageInfo.FromTiles(4, 2, 1, 2);
        using var image = new GrotheImage(info, PixelFormat.Rgba32, "a", "b");
        for (int layer = 0; layer < 2; layer++)
        for (int col = 0; col < 2; col++)
        {
            byte[] pixels = new byte[32];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(30 + layer * 80 + col * 20); pixels[i + 3] = 255; }
            WithPinned(pixels, ptr => image.UpdateTile(layer, 0, col, ptr, 4, 2, 16, PixelFormat.Rgba32));
        }

        using var stream = new MemoryStream();
        image.Serialize(stream);
        stream.Position = 0;
        using var restored = GrotheImage.Deserialize(stream);
        Assert.Equal(2, restored.LayerNames.Count);
        Assert.Equal(PixelFormat.Rgba32, restored.Format);
        byte[] output = new byte[64];
        WithPinned(output, ptr =>
        {
            restored.Load(1, ptr, 8, 2, 32, PixelFormat.Rgba32, TransformMatrix.Identity);
            Marshal.Copy(ptr, output, 0, output.Length);
        });
        Assert.Equal(110, output[0]);
        Assert.Equal(130, output[4 * 4]);
    }

    [Theory]
    [InlineData(PixelFormat.Yuv444)]
    [InlineData(PixelFormat.Yuv422)]
    [InlineData(PixelFormat.Yuv420)]
    public void NativeYuvPlanesSurviveSerialization(PixelFormat format)
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 2, 4, 2), format, "yuv");
        byte[] rgb = new byte[32];
        for (int i = 0; i < rgb.Length; i += 4)
        {
            rgb[i] = rgb[i + 1] = rgb[i + 2] = 96;
            rgb[i + 3] = 255;
        }
        WithPinned(rgb, ptr => image.UpdateTile(0, 0, 0, ptr, 4, 2, 16, PixelFormat.Rgba32));

        using var stream = new MemoryStream();
        image.Serialize(stream);
        stream.Position = 0;
        using var copy = GrotheImage.Deserialize(stream);
        byte[] output = new byte[32];
        WithPinned(output, ptr =>
        {
            copy.Load(0, ptr, 4, 2, 16, PixelFormat.Rgba32, TransformMatrix.Identity);
            Marshal.Copy(ptr, output, 0, output.Length);
        });
        Assert.InRange(output[0], (byte)75, (byte)140);
    }

    [Fact]
    public void TruncatedPayloadIsRejected()
    {
        using var image = new GrotheImage(new GrotheImageInfo(2, 2, 2, 2), PixelFormat.Gray8, "a");
        WithPinned(new byte[] { 1, 2, 3, 4 }, ptr => image.UpdateTile(0, 0, 0, ptr, 2, 2, 2, PixelFormat.Gray8));
        using var full = new MemoryStream();
        image.Serialize(full);
        byte[] data = full.ToArray();
        using var partial = new MemoryStream(data, 0, data.Length - 1);
        Assert.Throws<EndOfStreamException>(() => GrotheImage.Deserialize(partial));
    }

    /// <summary>Offset of the first layer name length prefix in a version 1 header.</summary>
    private const int FirstLayerNameOffset = 8 + 4 + 4 + 4 + 8 + 8 + 4 + 4 + 4 + 4;

    [Fact]
    public void ACorruptLayerNameLengthIsRejectedBeforeItIsAllocated()
    {
        using var image = new GrotheImage(new GrotheImageInfo(2, 2, 2, 2), PixelFormat.Gray8, "a");
        using var stream = new MemoryStream();
        image.Serialize(stream);
        byte[] bytes = stream.ToArray();

        // A length prefix of 300 is longer than any layer name may be.
        byte[] tooLong = (byte[])bytes.Clone();
        tooLong[FirstLayerNameOffset] = 0xAC;      // 300 & 0x7F, with the continuation bit set
        tooLong[FirstLayerNameOffset + 1] = 0x02;  // 300 >> 7
        using (var corrupt = new MemoryStream(tooLong))
            Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(corrupt));

        // A prefix that never ends must not be read as an unbounded length either.
        byte[] endless = (byte[])bytes.Clone();
        for (int i = 0; i < 5; i++) endless[FirstLayerNameOffset + i] = 0xFF;
        using (var corrupt = new MemoryStream(endless))
            Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(corrupt));
    }

    [Fact]
    public void AGeometryThatTheImageWouldRejectIsReportedAsCorruptData()
    {
        // The writer cannot produce this file: Yuv422 with an odd width is rejected by the GrotheImage
        // constructor, and the loader has to report that as corrupt data rather than as a bad argument.
        using var stream = CraftFile((int)PixelFormat.Yuv422, 101, 2, 1, 1, new[] { "a" }, 0);
        Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(stream));
    }

    [Fact]
    public void AnUnknownStorageFormatIsRejected()
    {
        using var stream = CraftFile(99, 2, 2, 1, 1, new[] { "a" }, 0);
        Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(stream));
    }

    [Fact]
    public void ATileRecordWithTheWrongPlaneCountIsRejected()
    {
        TestImages.RequireHardware();
        using var stream = CraftFile((int)PixelFormat.Gray8, 2, 2, 1, 1, new[] { "a" }, 1,
            writer => { writer.Write(0); writer.Write(0L); writer.Write(0L); writer.Write((byte)2); });
        Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(stream));
    }

    [Fact]
    public void ARepeatedTileCoordinateIsRejected()
    {
        TestImages.RequireHardware();
        using var stream = CraftFile((int)PixelFormat.Gray8, 2, 2, 1, 1, new[] { "a" }, 2, writer =>
        {
            for (int record = 0; record < 2; record++)
            {
                writer.Write(0);
                writer.Write(0L);
                writer.Write(0L);
                writer.Write((byte)1);
                writer.Write(2);            // plane width
                writer.Write(2);            // plane height
                writer.Write(4);            // plane byte count
                writer.Write(new byte[4]);
            }
        });
        Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(stream));
    }

    [Fact]
    public void ATileCoordinateOutsideTheGridIsRejected()
    {
        TestImages.RequireHardware();
        using var stream = CraftFile((int)PixelFormat.Gray8, 2, 2, 1, 1, new[] { "a" }, 1,
            writer => { writer.Write(0); writer.Write(0L); writer.Write(1L); writer.Write((byte)1); });
        Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(stream));
    }

    [Fact]
    public void AnImpossibleTileRecordCountIsRejected()
    {
        // The count is checked against the grid size before any device is created.
        using var stream = CraftFile((int)PixelFormat.Gray8, 2, 2, 1, 1, new[] { "a" }, 5);
        Assert.Throws<InvalidDataException>(() => GrotheImage.Deserialize(stream));
    }

    /// <summary>
    /// Builds a version 1 file by hand so the loader can be fed headers and records the writer would never
    /// produce. <paramref name="writeRecords"/> appends the tile records.
    /// </summary>
    private static MemoryStream CraftFile(
        int format,
        int tileWidth,
        int tileHeight,
        long tileRows,
        long tileColumns,
        string[] layerNames,
        long recordCount,
        Action<BinaryWriter> writeRecords = null)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("GROTHEIM"));
            writer.Write(1);
            writer.Write(tileWidth);
            writer.Write(tileHeight);
            writer.Write(tileRows);
            writer.Write(tileColumns);
            writer.Write(0);
            writer.Write(0);
            writer.Write(format);
            writer.Write(layerNames.Length);
            foreach (string name in layerNames) writer.Write(name);
            writer.Write(recordCount);
            if (writeRecords != null) writeRecords(writer);
            writer.Flush();
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void WrittenTilesOfEveryLayerSurviveSerializationInAnyOrder()
    {
        // Tiles are stored in ascending order and reloaded into the same layer and slice, whatever order the
        // caller wrote them in.
        var info = GrotheImageInfo.FromTiles(4, 2, 1, 3);
        using var image = new GrotheImage(info, PixelFormat.Gray8, "a");
        for (int column = 2; column >= 0; column--)
        {
            byte[] pixels = new byte[8];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(10 + column * 20);
            WithPinned(pixels, ptr => image.UpdateTile(0, 0, column, ptr, 4, 2, 4, PixelFormat.Gray8));
        }

        using var stream = new MemoryStream();
        image.Serialize(stream);
        stream.Position = 0;
        using var restored = GrotheImage.Deserialize(stream);
        byte[] output = new byte[28];
        WithPinned(output, ptr =>
        {
            restored.Load(0, ptr, 12, 2, 12, PixelFormat.Gray8, TransformMatrix.Identity);
            Marshal.Copy(ptr, output, 0, output.Length);
        });
        Assert.Equal(10, output[0]);
        Assert.Equal(30, output[4]);
        Assert.Equal(50, output[8]);
    }

    private static void WithPinned(byte[] bytes, Action<IntPtr> action)
    {
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { action(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }
}
