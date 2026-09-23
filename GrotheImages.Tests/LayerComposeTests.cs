using System;
using Xunit;

namespace GrotheImages.Tests;

public sealed class LayerComposeTests
{
    private static GrotheImage CreateImage()
    {
        return new GrotheImage(new GrotheImageInfo(64, 64, 64, 64), PixelFormat.Rgba32, "a", "b");
    }

    [Fact]
    public void ScalarAndVectorExpressionsAreTypedAndFlattened()
    {
        using var image = CreateImage();
        var compose = image.CreateLayerCompose("a.r, b.gb * 0.5");

        Assert.Equal(3, compose.OutputChannelCount);
        Assert.Equal("a.r, b.gb * 0.5", compose.Expression);
    }

    [Fact]
    public void VectorOperandsMustHaveEqualWidth()
    {
        using var image = CreateImage();
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rgb + b.gb"));
    }

    [Fact]
    public void UnknownLayerAndSwizzleAreRejected()
    {
        using var image = CreateImage();
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("c.r"));
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rr"));
    }

    [Fact]
    public void ParenthesesAndUnaryOperatorsAreAccepted()
    {
        using var image = CreateImage();
        var compose = image.CreateLayerCompose("-(a.r - 0.2) * 3");

        Assert.Equal(1, compose.OutputChannelCount);
    }
}
