using System;
using System.IO;
using System.Runtime.InteropServices;
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

    private static void WithPinned(byte[] bytes, Action<IntPtr> action)
    {
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { action(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }
}
