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

    [Fact]
    public void PackedSubsampledFormatsAreRejectedByUpdateAndLoad()
    {
        using var image = new GrotheImage(new GrotheImageInfo(16, 16, 16, 16), PixelFormat.Rgba32, "a");
        Assert.Throws<ArgumentException>(() => image.Update(0, (nint)1, 1, 1, 2, PixelFormat.Yuv422, TransformMatrix.Identity));
        Assert.Throws<ArgumentException>(() => image.Load(0, (nint)1, 1, 1, 4, PixelFormat.Yuv420, TransformMatrix.Identity));
    }

    [Fact]
    public void PackedTileUpdateCannotBeUsedForSubsampledStorage()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 2, 4, 2), PixelFormat.Yuv422, "a");
        Assert.Throws<ArgumentException>(() => image.UpdateTile(0, 0, 0, (nint)1, 4, 2, 8, PixelFormat.Yuv422));
    }
}
