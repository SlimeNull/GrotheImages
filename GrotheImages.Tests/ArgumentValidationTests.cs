using System;
using System.Collections.Generic;
using Xunit;

namespace GrotheImages.Tests;

public sealed class ArgumentValidationTests
{
    [Fact]
    public void LayerNamesAreFrozenAndIndexable()
    {
        using var image = new GrotheImage(new GrotheImageInfo(16, 16, 16, 16), PixelFormat.Gray8, "a", "b");
        Assert.Equal(1, image.LayerNames.IndexOf("b"));
        var names = (IList<string>)image.LayerNames;
        Assert.Throws<NotSupportedException>(() => names.Add("c"));
    }

    [Fact]
    public void InvalidLayerNamesAreRejected()
    {
        var info = new GrotheImageInfo(16, 16, 16, 16);
        Assert.Throws<ArgumentException>(() => new GrotheImage(info, PixelFormat.Gray8, "a.b"));
        Assert.Throws<ArgumentException>(() => new GrotheImage(info, PixelFormat.Gray8, "a", "a"));
    }

    [Theory]
    [InlineData(PixelFormat.Yuv444)]
    [InlineData(PixelFormat.Yuv422)]
    [InlineData(PixelFormat.Yuv420)]
    public void UnsupportedTransferFormatsAreRejected(PixelFormat format)
    {
        using var image = new GrotheImage(new GrotheImageInfo(16, 16, 16, 16), PixelFormat.Rgba32, "a");
        using var compose = image.CreateLayerCompose("a.r, a.g, a.b, 1");
        Assert.Throws<ArgumentException>(() => image.Update(0, (nint)1, 1, 1, 4, format, TransformMatrix.Identity));
        Assert.Throws<ArgumentException>(() => image.Load(0, (nint)1, 1, 1, 4, format, TransformMatrix.Identity));
        Assert.Throws<ArgumentException>(() => image.Load(compose, (nint)1, 1, 1, 4, format, TransformMatrix.Identity));
        Assert.Throws<ArgumentException>(() => image.UpdateTile(0, 0, 0, (nint)1, 16, 16, 64, format));
    }

    [Fact]
    public void PackedTileUpdateCannotBeUsedForSubsampledStorage()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 2, 4, 2), PixelFormat.Yuv422, "a");
        Assert.Throws<ArgumentException>(() => image.UpdateTile(0, 0, 0, (nint)1, 4, 2, 8, PixelFormat.Yuv422));
    }

    [Fact]
    public void DefaultImageInfoCannotCreateAnImage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrotheImage(default(GrotheImageInfo), PixelFormat.Gray8, "a"));
    }

    [Fact]
    public void ATransformThatPutsACornerAtInfinityIsRejectedBeforeAnyGpuWork()
    {
        // These matrices are finite and invertible, so only the corner check can reject them, and they must
        // fail on the CPU without touching the GPU (these tests run without a Direct3D device too).
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        using var compose = image.CreateLayerCompose("a.r, a.r, a.r, 1");

        // User Image corner (0, 0) has a zero homogeneous divisor under this User Image -> Grothe Image map.
        var userCornerAtInfinity = new TransformMatrix(1, 0, 5, 0, 1, 0, 1, 0, 0);
        Assert.True(userCornerAtInfinity.TryInvert(out _));
        ArgumentException update = Assert.Throws<ArgumentException>(() =>
            image.Update(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, userCornerAtInfinity));
        Assert.Equal("transformMatrix", update.ParamName);

        // Grothe Image -> User Image maps the User Image corner (0, 0) to infinity through its inverse.
        var loadCornerAtInfinity = new TransformMatrix(1, 0, 0, 0, 0, 1, 0, 1, 1);
        Assert.True(loadCornerAtInfinity.TryInvert(out _));
        Assert.Throws<ArgumentException>(() => image.Load(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, loadCornerAtInfinity));
        Assert.Throws<ArgumentException>(() => image.Load(compose, (nint)1, 1, 1, 4, PixelFormat.Rgba32, loadCornerAtInfinity));
    }

    [Fact]
    public void ACompositionOfAnotherImageIsRejected()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        using var other = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        using var compose = other.CreateLayerCompose("a.r, a.r, a.r, 1");

        Assert.Throws<ArgumentException>(() => image.Load(compose, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
    }

    [Fact]
    public void ANullCompositionIsRejected()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        Assert.Throws<ArgumentNullException>(() => image.Load(null, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
    }

    [Fact]
    public void LayerIndexIsValidatedBeforeAnyGpuWork()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        using var compose = image.CreateLayerCompose("a.r, a.r, a.r, 1");

        Assert.Throws<ArgumentOutOfRangeException>(() => image.Update(1, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Update(-1, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Load(1, (nint)1, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.UpdateTile(1, 0, 0, (nint)1, 4, 4, 16, PixelFormat.Rgba32));
        // A composition carries its own layer indices, so it has no layer argument; its other arguments are
        // still validated.
        Assert.Throws<ArgumentException>(() => image.Load(compose, (nint)1, 1, 1, 4, PixelFormat.Yuv420, TransformMatrix.Identity));
    }

    [Theory]
    [InlineData(1, 1, 4, true)]     // null pointer
    [InlineData(0, 1, 4, false)]    // zero width
    [InlineData(1, 0, 4, false)]    // zero height
    [InlineData(2, 1, 7, false)]    // stride below the row size
    public void TransferArgumentsAreValidatedBeforeAnyGpuWork(int width, int height, int stride, bool nullPointer)
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        nint scan0 = nullPointer ? 0 : 1;
        Assert.ThrowsAny<ArgumentException>(() => image.Update(0, scan0, width, height, stride, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.ThrowsAny<ArgumentException>(() => image.Load(0, scan0, width, height, stride, PixelFormat.Rgba32, TransformMatrix.Identity));
    }

    [Fact]
    public void ArgumentFailuresUseTheExpectedExceptionTypes()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");

        Assert.Throws<ArgumentNullException>(() => image.Update(0, 0, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Update(0, (nint)1, 0, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Update(0, (nint)1, 1, 0, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Update(0, (nint)1, 2, 1, 7, PixelFormat.Rgba32, TransformMatrix.Identity));
        Assert.Throws<ArgumentException>(() => image.Update(0, (nint)1, 1, 1, 4, PixelFormat.Yuv420, TransformMatrix.Identity));
    }

    [Fact]
    public void ASingularOrNonFiniteTransformIsRejectedBeforeAnyGpuWork()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        var singular = new TransformMatrix(1, 2, 3, 2, 4, 6, 0, 0, 0);
        var notFinite = new TransformMatrix(double.NaN, 0, 0, 0, 1, 0, 0, 0, 1);

        Assert.Throws<ArgumentException>(() => image.Update(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, singular));
        Assert.Throws<ArgumentException>(() => image.Load(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, singular));
        Assert.Throws<ArgumentException>(() => image.Update(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, notFinite));
        Assert.Throws<ArgumentException>(() => image.Load(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, notFinite));
    }

    [Fact]
    public void TileArgumentsAreValidatedBeforeAnyGpuWork()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");

        // The input has to be exactly one tile, whatever the tile grid looks like.
        Assert.Throws<ArgumentException>(() => image.UpdateTile(0, 0, 0, (nint)1, 2, 2, 8, PixelFormat.Rgba32));
        // Coordinates outside the grid.
        Assert.Throws<ArgumentOutOfRangeException>(() => image.UpdateTile(0, 1, 0, (nint)1, 4, 4, 16, PixelFormat.Rgba32));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.UpdateTile(0, 0, -1, (nint)1, 4, 4, 16, PixelFormat.Rgba32));
        // A null pointer and a stride below the row size.
        Assert.Throws<ArgumentNullException>(() => image.UpdateTile(0, 0, 0, 0, 4, 4, 16, PixelFormat.Rgba32));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.UpdateTile(0, 0, 0, (nint)1, 4, 4, 15, PixelFormat.Rgba32));
    }
}
