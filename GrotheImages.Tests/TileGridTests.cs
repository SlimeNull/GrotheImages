using Xunit;

namespace GrotheImages.Tests;

public sealed class TileGridTests
{
    private static readonly GrotheImageInfo Info = GrotheImageInfo.FromTiles(100, 100, 2, 4, 10, 10);

    private static void AssertSamplingRange(TransformMatrix matrix, double width, double height,
        long expectedFirstRow, long expectedLastRow, long expectedFirstColumn, long expectedLastColumn)
    {
        Assert.True(TileGrid.TryGetCoveredRegion(matrix, width, height, out CoveredRegion region));
        region.GetSamplingTileRange(Info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn);
        Assert.Equal(expectedFirstRow, firstRow);
        Assert.Equal(expectedLastRow, lastRow);
        Assert.Equal(expectedFirstColumn, firstColumn);
        Assert.Equal(expectedLastColumn, lastColumn);
    }

    [Fact]
    public void IdentityCoversEveryTileOfTheImage()
    {
        AssertSamplingRange(TransformMatrix.Identity, Info.Width, Info.Height, 0, Info.TileRows - 1, 0, Info.TileColumns - 1);
    }

    [Fact]
    public void TheSamplingRangeFollowsTheOwnershipBandBoundaries()
    {
        // A 8 x 8 User Image at x = 183..191 crosses the ownership boundary at 185, so both tiles 1 and 2 are
        // needed even though the rectangle only just touches tile 2.
        var matrix = new TransformMatrix(1, 0, 183, 0, 1, 0, 0, 0, 1);
        AssertSamplingRange(matrix, 8, 8, 0, 0, 1, 2);
    }

    [Fact]
    public void ARectangleOutsideTheImageIsClampedToTheLastTile()
    {
        // The rectangle lands far past the bottom right corner, so the range saturates instead of wrapping.
        var matrix = new TransformMatrix(1, 0, 10000, 0, 1, 10000, 0, 0, 1);
        AssertSamplingRange(matrix, 10, 10, Info.TileRows - 1, Info.TileRows - 1, Info.TileColumns - 1, Info.TileColumns - 1);
    }

    [Fact]
    public void ACornerWithoutAnImageIsReportedInsteadOfThrowing()
    {
        // The corner (0, 0) has a homogeneous divisor of zero, which is what a perspective transform does on
        // its horizon. The matrix stays invertible, so only the corner check can catch it.
        var degenerate = new TransformMatrix(1, 0, 5, 0, 1, 0, 1, 0, 0);
        Assert.True(degenerate.TryInvert(out _));
        Assert.False(degenerate.TryTransformPoint(0, 0, out _, out _));

        Assert.False(TileGrid.TryGetCoveredRegion(degenerate, 10, 10, out _));
    }

    [Fact]
    public void InfinityAndNaNCoordinatesSaturateInsteadOfOverflowing()
    {
        var enormous = new TransformMatrix(1, 0, 1e300, 0, 1, 1e300, 0, 0, 1);
        Assert.True(TileGrid.TryGetCoveredRegion(enormous, 10, 10, out CoveredRegion region));
        region.GetSamplingTileRange(Info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn);

        Assert.InRange(firstRow, 0, Info.TileRows - 1);
        Assert.InRange(lastRow, 0, Info.TileRows - 1);
        Assert.InRange(firstColumn, 0, Info.TileColumns - 1);
        Assert.InRange(lastColumn, 0, Info.TileColumns - 1);

        region.GetStorageTileRange(Info, out firstRow, out lastRow, out firstColumn, out lastColumn);
        Assert.InRange(firstRow, 0, Info.TileRows - 1);
        Assert.InRange(lastRow, 0, Info.TileRows - 1);
        Assert.InRange(firstColumn, 0, Info.TileColumns - 1);
        Assert.InRange(lastColumn, 0, Info.TileColumns - 1);
    }

    [Fact]
    public void TheStorageRangeCoversEveryTileTheUserImageReaches()
    {
        // tileWidth 100 with overlap 10: the storage rectangle of a tile starts overlap pixels before its
        // ownership band, so a User Image that only touches the overlap still needs the neighbouring tile.
        Assert.True(TileGrid.TryGetCoveredRegion(TransformMatrix.Identity, 4, 4, out CoveredRegion region));
        region.GetStorageTileRange(Info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn);

        // x in [0, 4) only reaches tile 0, whose storage is [0, 100).
        Assert.Equal(0, firstColumn);
        Assert.Equal(0, lastColumn);
        Assert.Equal(0, firstRow);
        Assert.Equal(0, lastRow);
        Assert.True(region.IntersectsTile(Info, 0, 0));

        // A rectangle at x = 95..99 still only reaches tile 0, but one at 97..101 also needs tile 1, whose
        // storage starts at 90.
        Assert.True(TileGrid.TryGetCoveredRegion(new TransformMatrix(1, 0, 97, 0, 1, 0, 0, 0, 1), 4, 4, out CoveredRegion spanning));
        spanning.GetStorageTileRange(Info, out _, out _, out firstColumn, out lastColumn);
        Assert.Equal(0, firstColumn);
        Assert.Equal(1, lastColumn);
        Assert.True(spanning.IntersectsTile(Info, 0, 0));
        Assert.True(spanning.IntersectsTile(Info, 0, 1));
    }

    [Fact]
    public void TilesInsideTheBoundingBoxButOutsideTheQuadAreRejected()
    {
        // A thin rectangle rotated by 45 degrees runs along the diagonal of its bounding box, so the corner
        // tiles of the box hold no covered pixel at all.
        double diagonal = System.Math.Sqrt(0.5);
        var rotation = new TransformMatrix(diagonal, -diagonal, 0, diagonal, diagonal, 0, 0, 0, 1);
        Assert.True(TileGrid.TryGetCoveredRegion(rotation, 300, 10, out CoveredRegion region));
        region.GetStorageTileRange(Info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn);

        Assert.True(region.IntersectsTile(Info, 0, 0));
        Assert.True(region.IntersectsTile(Info, 1, 1));
        Assert.False(region.IntersectsTile(Info, 2, 0), "the diagonal never reaches the top right cell");
        Assert.False(region.IntersectsTile(Info, 0, 2), "the diagonal never reaches the bottom left cell");

        bool anyRejected = false;
        for (long row = firstRow; row <= lastRow; row++)
        for (long column = firstColumn; column <= lastColumn; column++)
            if (!region.IntersectsTile(Info, row, column)) anyRejected = true;
        Assert.True(anyRejected, "a rotated region must not accept every tile of its bounding box");
    }

    [Fact]
    public void ARegionAcrossTheHorizonFallsBackToItsBoundingBox()
    {
        // The homogeneous divisor changes sign along the rectangle, so the preimage is unbounded: the region
        // cannot claim to be a bounded quad and has to accept whatever its bounding box covers.
        var acrossHorizon = new TransformMatrix(1, 0, 0, 0, 1, 0, 1, 0, -50);
        Assert.True(TileGrid.TryGetCoveredRegion(acrossHorizon, 100, 100, out CoveredRegion region));
        Assert.False(region.IsBounded);
        Assert.True(region.IntersectsTile(Info, 0, 0));
        Assert.False(region.IntersectsTile(Info, 0, 1), "the bounding box stays inside tile column 0");
    }

    [Fact]
    public void LinearIndexIsRowMajor()
    {
        Assert.Equal(0, TileGrid.GetLinearIndex(Info, 0, 0));
        Assert.Equal(Info.TileColumns, TileGrid.GetLinearIndex(Info, 1, 0));
        Assert.Equal(Info.TileColumns + 3, TileGrid.GetLinearIndex(Info, 1, 3));
    }
}
