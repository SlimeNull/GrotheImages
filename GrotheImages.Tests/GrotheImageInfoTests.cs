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
    public void AutoLayoutPicksTheLargestEqualTileWithinTheMaximum()
    {
        var large = new GrotheImageInfo(100000, 100000, 2048, 2048);
        Assert.Equal(50, large.TileColumns);
        Assert.Equal(50, large.TileRows);
        Assert.Equal(2000, large.TileWidth);
        Assert.Equal(2000, large.TileHeight);
        Assert.True(large.TileWidth <= 2048);

        var small = new GrotheImageInfo(1000, 1000, 100, 100);
        Assert.Equal(10, small.TileColumns);
        Assert.Equal(100, small.TileWidth);
    }

    [Fact]
    public void AutoLayoutRejectsSizesThatWouldDegenerateIntoOnePixelTiles()
    {
        // 4093 is prime, so the only equal split below the 2048 maximum is 4093 tiles of one pixel. The
        // layout must fail instead of silently producing 16.7 million tiles.
        ArgumentException prime = Assert.Throws<ArgumentException>(() => new GrotheImageInfo(4093, 4093, 2048, 2048));
        Assert.Equal("width", prime.ParamName);

        // 1021 with a 64 pixel maximum has the same problem on the width axis.
        Assert.Throws<ArgumentException>(() => new GrotheImageInfo(1021, 100, 64, 64));

        // An odd span with a generous maximum is fine: the single tile covers the whole image.
        var single = new GrotheImageInfo(4093, 4093, 4096, 4096);
        Assert.Equal(1, single.TileColumns);
        Assert.Equal(4093, single.TileWidth);
    }

    [Fact]
    public void FromTilesSkipsTheSearchAndAcceptsAnyGrid()
    {
        // A grid the automatic layout would reject is still expressible when the caller supplies it.
        var info = GrotheImageInfo.FromTiles(4093, 4093, 1, 1);
        Assert.Equal(1, info.TileColumns);
        Assert.Equal(1, info.TileRows);
        Assert.Equal(4093, info.TileWidth);
        Assert.Equal(4093, info.Width);
        Assert.Equal(4093, info.Height);
    }

    [Fact]
    public void InvalidOverlapIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrotheImageInfo(100, 100, 100, 100, 100, 0));

        // The explicit grid constructor applies the same rule: the overlap has to stay below the tile size.
        Assert.Throws<ArgumentOutOfRangeException>(() => GrotheImageInfo.FromTiles(100, 100, 1, 1, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrotheImageInfo.FromTiles(100, 100, 1, 1, 0, 120));
    }

    [Fact]
    public void InvalidGridValuesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GrotheImageInfo.FromTiles(0, 10, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrotheImageInfo.FromTiles(10, 10, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrotheImageInfo.FromTiles(10, 10, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrotheImageInfo(0, 10, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrotheImageInfo(10, 10, 0, 10));
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
