using System;
using Xunit;

namespace GrotheImages.Tests;

public sealed class GrotheImageInfoTests
{
    [Fact]
    public void ImageInfoIsAValueTypeWithIndependentCopies()
    {
        Assert.True(typeof(GrotheImageInfo).IsValueType);
        var info = GrotheImageInfo.FromTiles(100, 100, 2, 3, 10, 10);
        GrotheImageInfo copy = info;
        Assert.Equal(info.TileWidth, copy.TileWidth);
        Assert.Equal(info.Width, copy.Width);
    }

    [Fact]
    public void EqualSizeTilesUseStepAndOverlapFormula()
    {
        var info = GrotheImageInfo.FromTiles(100, 100, 2, 3, 10, 10);

        Assert.Equal(280, info.Width);
        Assert.Equal(190, info.Height);
        Assert.Equal(100, info.TileWidth);
        Assert.Equal(100, info.TileHeight);
    }

    [Fact]
    public void AutoLayoutChoosesExactEqualSizeTiles()
    {
        var info = new GrotheImageInfo(280, 190, 100, 100, 10, 10);

        Assert.Equal(3, info.TileColumns);
        Assert.Equal(2, info.TileRows);
        Assert.Equal(100, info.TileWidth);
        Assert.Equal(100, info.TileHeight);
        Assert.Equal(280, info.Width);
        Assert.Equal(190, info.Height);
    }

    [Fact]
    public void InvalidOverlapIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrotheImageInfo(100, 100, 100, 100, 100, 0));
    }

    [Fact]
    public void Yuv422RequiresHorizontalEvenAlignment()
    {
        var oddWidth = new GrotheImageInfo(101, 100, 101, 100);
        Assert.Throws<ArgumentException>(() => new GrotheImage(oddWidth, PixelFormat.Yuv422, "a"));

        var oddTileAlignment = GrotheImageInfo.FromTiles(101, 100, 1, 2, 0, 0);
        Assert.Throws<ArgumentException>(() => new GrotheImage(oddTileAlignment, PixelFormat.Yuv422, "a"));
    }

    [Fact]
    public void Yuv420RequiresEvenImageAndTileAlignment()
    {
        var oddHeight = new GrotheImageInfo(100, 101, 100, 101);
        Assert.Throws<ArgumentException>(() => new GrotheImage(oddHeight, PixelFormat.Yuv420, "a"));

        var oddOverlap = GrotheImageInfo.FromTiles(100, 100, 2, 2, 10, 9);
        Assert.Throws<ArgumentException>(() => new GrotheImage(oddOverlap, PixelFormat.Yuv420, "a"));
    }
}
