using System;
using Vortice.Direct3D;
using Xunit;
using Xunit.Sdk;

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

    [Fact]
    public void MembersFromTheShaderLibraryAreAppliedToALayer()
    {
        using var image = CreateImage();

        Assert.Equal(1, image.CreateLayerCompose("a.lum").OutputChannelCount);
        Assert.Equal(1, image.CreateLayerCompose("a.rgb.lum").OutputChannelCount);
        Assert.Equal(4, image.CreateLayerCompose("a.lum, b.rgb").OutputChannelCount);
        Assert.Equal(3, image.CreateLayerCompose("a.hsv.rgb").OutputChannelCount);
        Assert.Equal(1, image.CreateLayerCompose("a.gb.lum").OutputChannelCount);
    }

    [Fact]
    public void MemberCallsAreEmittedAsHlslCalls()
    {
        using var image = CreateImage();

        Assert.Equal("lum(Layer0)", image.CreateLayerCompose("a.lum").ToHlsl());
        Assert.Equal("lum(Layer0.rgb)", image.CreateLayerCompose("a.rgb.lum").ToHlsl());
        Assert.Equal("(hsv(Layer0)).rgb", image.CreateLayerCompose("a.hsv.rgb").ToHlsl());
        Assert.Equal("bin2(Layer0, (float4)(0.2), (float4)(0.8))", image.CreateLayerCompose("a.bin2(0.2, 0.8)").ToHlsl());
        // A scalar argument of a vector parameter is splatted so the HLSL overload is unambiguous.
        Assert.Equal("bin(Layer0.rgb, (float3)(0.5))", image.CreateLayerCompose("a.rgb.bin(0.5)").ToHlsl());
        Assert.Equal("bin(Layer0, (float4)(Layer1.r))", image.CreateLayerCompose("a.bin(b.r)").ToHlsl());
    }

    [Fact]
    public void TheShaderLibraryIsOnlyCompiledIntoMemberExpressions()
    {
        using var image = CreateImage();

        Assert.False(image.CreateLayerCompose("a.rgb").UsesMemberExpression);
        Assert.True(image.CreateLayerCompose("a.lum, a.rgba.r, a.rgba.g, a.rgba.b").UsesMemberExpression);
    }

    [Fact]
    public void OnlyMembersDeclaredInTheShaderLibraryAreAccepted()
    {
        using var image = CreateImage();

        Assert.True(ShaderLibrary.TryGetMember("lum", out var lum));
        Assert.Equal(4, lum.Count);
        Assert.False(ShaderLibrary.TryGetMember("not_a_member", out _));
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.not_a_member"));
    }

    [Fact]
    public void InvalidMemberUsageIsRejectedWithTheAvailableOverloads()
    {
        using var image = CreateImage();

        // hsv has no two channel overload.
        FormatException width = Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.gb.hsv"));
        Assert.Contains("Overloads:", width.Message);
        // bin takes one argument, bin2 takes two.
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.bin"));
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.bin(0.5, 0.5)"));
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.bin2(0.5)"));
        // A member name is not a layer name.
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("lum(a.rgb)"));
        // Swizzles cannot reach channels the operand does not have.
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rgb.a"));
    }

    [Fact]
    public void TheEmbeddedShaderLibraryCompiles()
    {
        const string call =
            "float4 PSMain() : SV_Target { float scalar = lum(float3(0.25, 0.5, 0.75)); return float4(scalar.rrr, hsv(float3(0.1, 0.2, 0.3)).r + min(float4(1, 2, 3, 4)) + sum(bin2(float2(0.4, 0.6), 0.2, 0.8))); }";
        try
        {
            using (Blob blob = ShaderCompiler.Compile(ShaderLibrary.Source + Environment.NewLine + call, "PSMain", "ps_5_0"))
                Assert.NotNull(blob);
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is TypeInitializationException)
        {
            throw SkipException.ForSkip("D3DCompiler is unavailable: " + ex.Message);
        }
    }
}
