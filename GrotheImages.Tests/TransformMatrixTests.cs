using System;
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

    [Fact]
    public void TryTransformPointReportsTheHorizonInsteadOfThrowing()
    {
        // The corner (0, 0) has a homogeneous divisor of zero, while the matrix itself stays invertible.
        var degenerate = new TransformMatrix(1, 0, 5, 0, 1, 0, 1, 0, 0);
        Assert.True(degenerate.TryInvert(out _));

        Assert.False(degenerate.TryTransformPoint(0, 0, out double x, out double y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Throws<InvalidOperationException>(() => degenerate.TransformPoint(0, 0));

        Assert.True(degenerate.TryTransformPoint(1, 0, out double farX, out _));
        Assert.Equal(6, farX, 12);
    }

    [Fact]
    public void TryTransformPointRejectsNonFiniteResults()
    {
        var nan = new TransformMatrix(double.NaN, 0, 0, 0, 1, 0, 0, 0, 1);
        Assert.False(nan.TryTransformPoint(1, 1, out _, out _));

        var infinite = new TransformMatrix(1, 0, double.PositiveInfinity, 0, 1, 1, 0, 0, 1);
        Assert.False(infinite.TryTransformPoint(0, 0, out _, out _));
    }

    [Fact]
    public void NonFiniteMatricesAreRejectedByTheTransferArguments()
    {
        using var image = new GrotheImage(new GrotheImageInfo(4, 4, 4, 4), PixelFormat.Rgba32, "a");
        var nan = new TransformMatrix(double.NaN, 0, 0, 0, 1, 0, 0, 0, 1);
        Assert.Throws<ArgumentException>(() => image.Load(0, (nint)1, 1, 1, 4, PixelFormat.Rgba32, nan));
    }

    [Fact]
    public void IsFiniteCoversEveryElement()
    {
        Assert.True(TransformMatrix.Identity.IsFinite);
        Assert.False(new TransformMatrix(double.NaN, 0, 0, 0, 1, 0, 0, 0, 1).IsFinite);
        Assert.False(new TransformMatrix(1, 0, 0, 0, 1, 0, 0, 0, double.PositiveInfinity).IsFinite);
        Assert.False(new TransformMatrix(1, 0, 0, 0, double.NegativeInfinity, 0, 0, 0, 1).IsFinite);
    }

    [Fact]
    public void ToStringUsesTheInvariantCultureAndRowOrder()
    {
        var matrix = new TransformMatrix(1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.Equal("[1, 2, 3; 4, 5, 6; 7, 8, 9]", matrix.ToString());
        Assert.Equal("[1.5, 0, 0; 0, 1.5, 0; 0, 0, 1]", new TransformMatrix(1.5, 0, 0, 0, 1.5, 0, 0, 0, 1).ToString());
    }

    [Fact]
    public void ARecordStructComparesByValue()
    {
        var first = new TransformMatrix(1, 2, 3, 4, 5, 6, 7, 8, 9);
        var second = new TransformMatrix(1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, first with { M00 = 2 });
    }
}
