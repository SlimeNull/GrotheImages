using System;
using System.IO;
using Xunit;

namespace GrotheImages.Tests;

/// <summary>
/// Lifetime contracts: what every entry point does after <see cref="GrotheImage.Dispose"/>, how compositions
/// follow their image, and how the stream/file entry points validate their arguments. None of these tests
/// needs a Direct3D device unless they actually render.
/// </summary>
public sealed class LifecycleTests
{
    [Fact]
    public void DisposeNeedsNoDeviceAndIsIdempotent()
    {
        // Constructing and disposing must not create a device: only the first GPU operation does.
        var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        Assert.Null(image.Graphics);
        image.Dispose();
        image.Dispose();
    }

    [Fact]
    public void EveryEntryPointThrowsAfterDispose()
    {
        var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        using (var compose = image.CreateLayerCompose("a.r, a.r, a.r, 1"))
        {
            image.Dispose();

            Assert.Throws<ObjectDisposedException>(() => image.Update(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
            Assert.Throws<ObjectDisposedException>(() => image.Load(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
            Assert.Throws<ObjectDisposedException>(() => image.Load(compose, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
            Assert.Throws<ObjectDisposedException>(() => image.UpdateTile(0, 0, 0, (nint)1, 4, 4, 16, PixelFormat.Rgba32));
            Assert.Throws<ObjectDisposedException>(() => image.BlendSeams());
            Assert.Throws<ObjectDisposedException>(() => image.CreateLayerCompose("a.r"));
            Assert.Throws<ObjectDisposedException>(() => image.Serialize(new MemoryStream()));
        }
    }

    [Fact]
    public void DisposingTheImageDisposesItsCompositionsProgram()
    {
        var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        var compose = image.CreateLayerCompose("a.r, a.r, a.r, 1");
        image.Dispose();

        // The composition is released with its image, so reading the program it owns now fails.
        Assert.Throws<ObjectDisposedException>(() => compose.GetLoadProgram());
        compose.Dispose();
        compose.Dispose();
    }

    [Fact]
    public void AReleasedCompositionIsNoLongerAcceptedByItsImage()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        var compose = image.CreateLayerCompose("a.r, a.r, a.r, 1");
        compose.Dispose();

        ArgumentException rejected = Assert.Throws<ArgumentException>(() =>
            image.Load(compose, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Equal("layerCompose", rejected.ParamName);
    }

    [Fact]
    public void CreateLayerComposeValidatesItsExpression()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        Assert.Throws<ArgumentException>(() => image.CreateLayerCompose(null));
        Assert.Throws<ArgumentException>(() => image.CreateLayerCompose(string.Empty));
        Assert.Throws<ArgumentException>(() => image.CreateLayerCompose("   "));
    }

    [Fact]
    public void SaveAndOpenRoundTripThroughAFile()
    {
        TestImages.RequireHardware();
        var info = GrotheImageInfo.FromTiles(4, 2, 1, 2);
        string path = Path.Combine(Path.GetTempPath(), "grothe-" + Guid.NewGuid().ToString("N") + ".gim");
        try
        {
            using (var image = TestImages.Create(info, PixelFormat.Gray8))
            {
                TestImages.WriteTile(image, 0, 0, 0, 11);
                TestImages.WriteTile(image, 0, 0, 1, 222);
                image.Save(path);
            }

            using (GrotheImage reopened = GrotheImage.Open(path))
            {
                Assert.Equal(8, reopened.Info.Width);
                Assert.Equal(PixelFormat.Gray8, reopened.Format);
                Assert.Equal("a", Assert.Single(reopened.LayerNames));
                byte[] pixels = TestImages.Load(reopened, 0, 8, 2, PixelFormat.Gray8);
                Assert.Equal(11, pixels[0]);
                Assert.Equal(222, pixels[4]);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAndOpenRejectANullPath()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        Assert.Throws<ArgumentNullException>(() => image.Save(null));
        Assert.Throws<ArgumentNullException>(() => GrotheImage.Open(null));
    }

    [Fact]
    public void SerializationValidatesTheStream()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        Assert.Throws<ArgumentNullException>(() => image.Serialize(null));
        Assert.Throws<ArgumentNullException>(() => GrotheImage.Deserialize(null));

        using (var readOnly = new MemoryStream(new byte[8], false))
            Assert.Throws<ArgumentException>(() => image.Serialize(readOnly));

        using (var writeOnly = new WriteOnlyStream())
            Assert.Throws<ArgumentException>(() => GrotheImage.Deserialize(writeOnly));
    }

    [Fact]
    public void AnImageThatWasNeverWrittenStoresNoTileRecords()
    {
        // The header of a version 1 file is 52 bytes up to the layer count, then every layer name as a 7 bit
        // length plus its bytes, then the 64 bit tile record count.
        using var image = new GrotheImage(GrotheImageInfo.FromTiles(4, 2, 1, 2), PixelFormat.Gray8, "a", "b");
        using var stream = new MemoryStream();
        image.Serialize(stream);

        byte[] bytes = stream.ToArray();
        int offset = 52 + 2 + 2;   // "a" and "b" are one byte each plus their length byte
        Assert.Equal(0L, BitConverter.ToInt64(bytes, offset));
        Assert.Equal(bytes.Length, offset + 8);
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
