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
}
