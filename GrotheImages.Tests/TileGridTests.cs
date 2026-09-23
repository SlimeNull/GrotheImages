using Xunit;

namespace GrotheImages.Tests;

public sealed class TileGridTests
{
    [Theory]
    [InlineData(0.5, 0, 0, 0.5)]
    [InlineData(94.5, 0, 0, 94.5)]
    [InlineData(95.0, 0, 1, 5.0)]
    [InlineData(185.0, 0, 2, 5.0)]
    [InlineData(275.0, 0, 3, 5.0)]
    public void SamplingOwnershipSplitsOverlapAtItsMidpoint(double x, long expectedRow, long expectedColumn, double expectedLocalX)
    {
        var info = GrotheImageInfo.FromTiles(100, 100, 2, 4, 10, 10);
        Assert.True(TileGrid.TryGetSamplingAddress(info, x, 0.5, out var sample));
        Assert.Equal(expectedRow, sample.Row);
        Assert.Equal(expectedColumn, sample.Column);
        Assert.Equal(expectedLocalX, sample.LocalX, 8);
    }

    [Fact]
    public void CoordinatesOutsideImageAreTransparent()
    {
        var info = GrotheImageInfo.FromTiles(100, 100, 2, 4, 10, 10);
        Assert.False(TileGrid.TryGetSamplingAddress(info, -0.1, 0.5, out _));
        Assert.False(TileGrid.TryGetSamplingAddress(info, info.Width, 0.5, out _));
    }
}
