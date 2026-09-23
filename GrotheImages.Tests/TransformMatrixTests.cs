using Xunit;

namespace GrotheImages.Tests;

public sealed class TransformMatrixTests
{
    [Fact]
    public void TranslationUsesThirdColumn()
    {
        var matrix = new TransformMatrix(1, 0, -100, 0, 1, -50, 0, 0, 1);
        var point = matrix.TransformPoint(100, 50);

        Assert.Equal(0, point.X, 12);
        Assert.Equal(0, point.Y, 12);
    }

    [Fact]
    public void MatrixInverseRoundTripsPoint()
    {
        var matrix = new TransformMatrix(2, 0.2, 5, 0.1, 3, -7, 0.001, 0.002, 1);
        Assert.True(matrix.TryInvert(out var inverse));

        var point = matrix.TransformPoint(13, 17);
        var original = inverse.TransformPoint(point.X, point.Y);
        Assert.Equal(13, original.X, 8);
        Assert.Equal(17, original.Y, 8);
    }

    [Fact]
    public void SingularMatrixIsRejected()
    {
        var matrix = new TransformMatrix(1, 2, 3, 2, 4, 6, 0, 0, 0);
        Assert.False(matrix.TryInvert(out _));
    }
}
