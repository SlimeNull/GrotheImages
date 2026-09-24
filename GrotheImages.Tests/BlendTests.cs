using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GrotheImages.Tests;

public sealed class BlendTests
{
    private static readonly GrotheImageInfo TwoByTwo = GrotheImageInfo.FromTiles(16, 16, 2, 2, 8, 8);

    [Fact]
    public void WithoutOverlapThereIsNothingToBlend()
    {
        var info = GrotheImageInfo.FromTiles(16, 16, 2, 2);
        Assert.Empty(BlendProgram.EnumerateSeams(info, info.TileWidth, info.TileHeight, info.TileOverlapX, info.TileOverlapY));
        Assert.Empty(Seams(info, 0, 0));
    }

    [Fact]
    public void EveryNeighbouringTilePairProducesExactlyOneSeam()
    {
        // Two seams per internal column edge and one per internal row edge.
        Assert.Equal(4, Seams(TwoByTwo, TwoByTwo.TileOverlapX, TwoByTwo.TileOverlapY).Count());
        Assert.Equal(2, Seams(TwoByTwo, TwoByTwo.TileOverlapX, 0).Count());
        Assert.Equal(2, Seams(TwoByTwo, 0, TwoByTwo.TileOverlapY).Count());

        var three = GrotheImageInfo.FromTiles(16, 16, 3, 2, 8, 8);
        Assert.Equal((2 - 1) * 3 + (3 - 1) * 2, Seams(three, 8, 8).Count());
    }

    [Fact]
    public void HorizontalSeamBandsAreCentredOnTheOverlap()
    {
        var info = GrotheImageInfo.FromTiles(16, 8, 1, 2, 6, 0);
        BlendSeam seam = Assert.Single(Seams(info, info.TileOverlapX, 0));

        Assert.Equal(0, seam.RowA);
        Assert.Equal(0, seam.ColumnA);
        Assert.Equal(0, seam.RowB);
        Assert.Equal(1, seam.ColumnB);
        // The first tile keeps the band in its right border, the second in its left border.
        Assert.Equal(10, seam.OriginAX);
        Assert.Equal(0, seam.OriginBX);
        Assert.Equal(6, seam.Width);
        Assert.Equal(8, seam.Height);
        Assert.True(seam.AlongX);
        Assert.Equal(6, seam.Overlap);
    }

    [Fact]
    public void VerticalSeamBandsAreCentredOnTheOverlap()
    {
        var info = GrotheImageInfo.FromTiles(8, 16, 2, 1, 0, 6);
        BlendSeam seam = Assert.Single(Seams(info, 0, info.TileOverlapY));

        Assert.Equal(0, seam.RowA);
        Assert.Equal(1, seam.RowB);
        Assert.Equal(0, seam.ColumnA);
        Assert.Equal(0, seam.ColumnB);
        // The first tile keeps the band in its bottom border, the second in its top border.
        Assert.Equal(0, seam.OriginAX);
        Assert.Equal(16 - 6, seam.OriginAY);
        Assert.Equal(0, seam.OriginBX);
        Assert.Equal(0, seam.OriginBY);
        Assert.Equal(8, seam.Width);
        Assert.Equal(6, seam.Height);
        Assert.False(seam.AlongX);
        Assert.Equal(6, seam.Overlap);
    }

    [Fact]
    public void ChromaPlanesCarryTheirOwnGeometry()
    {
        var info = GrotheImageInfo.FromTiles(16, 16, 2, 2, 8, 8);

        BlendPlane color = BlendProgram.EnumeratePlanes(PixelFormat.Rgba32, info).Single();
        Assert.False(color.Chroma);
        Assert.Equal(16, color.Width);
        Assert.Equal(16, color.Height);

        List<BlendPlane> yuv444 = BlendProgram.EnumeratePlanes(PixelFormat.Yuv444, info).ToList();
        Assert.Equal(2, yuv444.Count);
        Assert.True(yuv444[1].Chroma);
        Assert.Equal(16, yuv444[1].Width);
        Assert.Equal(16, yuv444[1].Height);
        Assert.Equal(8, yuv444[1].OverlapX);
        Assert.Equal(8, yuv444[1].OverlapY);

        List<BlendPlane> yuv422 = BlendProgram.EnumeratePlanes(PixelFormat.Yuv422, info).ToList();
        Assert.Equal(8, yuv422[1].Width);
        Assert.Equal(16, yuv422[1].Height);
        Assert.Equal(4, yuv422[1].OverlapX);
        Assert.Equal(8, yuv422[1].OverlapY);

        List<BlendPlane> yuv420 = BlendProgram.EnumeratePlanes(PixelFormat.Yuv420, info).ToList();
        Assert.Equal(8, yuv420[1].Width);
        Assert.Equal(8, yuv420[1].Height);
        Assert.Equal(4, yuv420[1].OverlapX);
        Assert.Equal(4, yuv420[1].OverlapY);

        // The chroma seam of a subsampled plane is half as wide and half as tall.
        BlendPlane channel = yuv420[1];
        BlendSeam chroma = BlendProgram.EnumerateSeams(info, channel.Width, channel.Height, channel.OverlapX, channel.OverlapY).First();
        Assert.Equal(4, chroma.Width);
        Assert.Equal(8, chroma.Height);
        Assert.Equal(4, chroma.OriginAX);
    }

    private static IEnumerable<BlendSeam> Seams(GrotheImageInfo info, int overlapX, int overlapY)
    {
        return BlendProgram.EnumerateSeams(info, info.TileWidth, info.TileHeight, overlapX, overlapY);
    }
}
