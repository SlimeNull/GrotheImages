using System;
using Xunit;

namespace GrotheImages.Tests;

public sealed class LayerComposeTests
{
    private static GrotheImage CreateImage()
    {
        return new GrotheImage(new GrotheImageInfo(64, 64, 64, 64), PixelFormat.Rgba32, "a", "b");
    }

    private static string GeneratedIncludes(GrotheImage image, string expression)
    {
        return LoadProgram.BuildInclude(image, expression == null ? null : image.CreateLayerCompose(expression)).GeneratedSource;
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
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.q"));
        // A swizzle may not select channels the operand does not have, nor more than four channels.
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rgb.a"));
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rrrrr"));
        // A swizzle is not a function, so it cannot be called.
        FormatException swizzleCall = Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rgb(0.5)"));
        Assert.Contains("selects channels and cannot be called with arguments", swizzleCall.Message);
    }

    [Fact]
    public void SwizzlesAcceptAnyChannelCombinationLikeTheReferenceEngine()
    {
        using var image = CreateImage();

        // Holly.Imaging.DX formats swizzles from a ^(r|g|b|a)+$ name, so 'bgr' and 'rr' are valid there.
        Assert.Equal("layers[0].bgr", image.CreateLayerCompose("a.bgr").ToHlsl());
        Assert.Equal(3, image.CreateLayerCompose("a.bgr").OutputChannelCount);
        Assert.Equal(2, image.CreateLayerCompose("a.rr").OutputChannelCount);
        Assert.Equal(1, image.CreateLayerCompose("a.a").OutputChannelCount);
        Assert.Equal(4, image.CreateLayerCompose("a.rgba").OutputChannelCount);
    }

    [Fact]
    public void ABareLayerIsTheWholeLayer()
    {
        using var image = CreateImage();

        // The reference engine allows a bare source name; ours is the four logical channels.
        Assert.Equal(4, image.CreateLayerCompose("a").OutputChannelCount);
        Assert.Equal("((layers[0] * 1.2) - 0.1)", image.CreateLayerCompose("a*1.2-0.1").ToHlsl());
    }

    [Fact]
    public void CompositionIsAnOperatorThatCanBeGroupedAndPipedIntoAMember()
    {
        using var image = CreateImage();

        // ',' concatenates channels, like the reference engine's '|'.
        Assert.Equal("float3(layers[0].r, layers[1].gb)", image.CreateLayerCompose("a.r, b.gb").ToHlsl());
        Assert.Equal(3, image.CreateLayerCompose("a.r, b.gb").OutputChannelCount);
        // Grouped composition is a value, so it can feed a member.
        var grouped = image.CreateLayerCompose("(a.lum, b.lum).avg");
        Assert.Equal(1, grouped.OutputChannelCount);
        Assert.Equal("avg(float2(lum(layers[0]), lum(layers[1])))", grouped.ToHlsl());
        Assert.Throws<FormatException>(() => image.CreateLayerCompose("a.rgba, b.rgba"));
    }

    [Fact]
    public void IntrinsicFunctionsBehaveLikeTheReferenceEngine()
    {
        using var image = CreateImage();

        Assert.Equal("sqrt(layers[0])", image.CreateLayerCompose("a.sqrt").ToHlsl());
        Assert.Equal(3, image.CreateLayerCompose("a.rgb.abs").OutputChannelCount);
        Assert.Equal(1, image.CreateLayerCompose("a.length").OutputChannelCount);
        Assert.Equal("length(layers[0].rgb)", image.CreateLayerCompose("a.rgb.length").ToHlsl());
        // Intrinsics are HLSL builtins, so they do not pull in Shaders/Common.hlsl.
        Assert.False(image.CreateLayerCompose("a.r.sqrt, a.r.abs, a.r.sign, a.r.length").UsesMemberExpression);
        Assert.True(image.CreateLayerCompose("a.lum").UsesMemberExpression);
    }

    [Fact]
    public void EveryReferenceEngineFunctionIsAvailable()
    {
        using var image = CreateImage();

        // IHLManager.RegisterFilters / RegisterIntrinsicFunctions in Holly.Imaging.DX.
        string[] filters =
        {
            "lum", "distance", "hhh", "bin", "bin2", "color", "hsv", "min", "max", "product", "sum", "avg",
            "sort", "mul_rgb_to_a", "rgb_to_xyz", "xyz_to_rgb", "rgb_to_luv", "luv_to_rgb", "xyz_to_luv",
            "luv_to_xyz", "sharpen",
        };
        string[] intrinsics = { "abs", "length", "saturate", "sqrt", "sign" };

        foreach (string name in filters)
            Assert.True(ShaderLibrary.TryGetMember(name, out _), "missing filter: " + name);
        foreach (string name in intrinsics)
            Assert.True(ShaderLibrary.TryGetMember(name, out _), "missing intrinsic: " + name);

        // Unlike the reference engine, the threshold functions can actually be called with arguments there.
        Assert.Equal("bin2(layers[0], (float4)(0.2), (float4)(0.8))", image.CreateLayerCompose("a.bin2(0.2, 0.8)").ToHlsl());
    }

    [Fact]
    public void ReferenceExpressionsWorkAfterReplacingThePipeOperatorWithAComma()
    {
        using var image = CreateImage();

        // Production expressions from Holly.AOI (InspectionWindow.ImageSourceOldToNew), with '|' written
        // as ',' and the reference source names top/side mapped onto our layers.
        string[] expressions =
        {
            "a", "b",
            "a.lum", "1-a.lum", "a.lum*2-1", "a.lum*2",
            "a.lum, b.lum", "a.lum, b.lum, (a.lum+b.lum)/2",
            "(a.lum-b.lum)/2+0.5", "(b.lum-a.lum)/2+0.5",
            "(a.lum+b.lum)/2", "1-(a.lum+b.lum)/2", "1-b.lum", "b.lum",
            "b", "b*1.2-0.1", "a*1.2-0.1",
            "a.bgr", "(a.rgb).lum",
        };
        int[] channels = { 4, 4, 1, 1, 1, 1, 2, 3, 1, 1, 1, 1, 1, 1, 4, 4, 4, 3, 1 };

        for (int i = 0; i < expressions.Length; i++)
        {
            var compose = image.CreateLayerCompose(expressions[i]);
            Assert.Equal(channels[i], compose.OutputChannelCount);
        }

        // Spot check the generated HLSL of a composed expression.
        Assert.Equal(
            "float3(lum(layers[0]), lum(layers[1]), ((lum(layers[0]) + lum(layers[1])) / 2))",
            image.CreateLayerCompose("a.lum, b.lum, (a.lum+b.lum)/2").ToHlsl());
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

        Assert.Equal("lum(layers[0])", image.CreateLayerCompose("a.lum").ToHlsl());
        Assert.Equal("lum(layers[0].rgb)", image.CreateLayerCompose("a.rgb.lum").ToHlsl());
        Assert.Equal("(hsv(layers[0])).rgb", image.CreateLayerCompose("a.hsv.rgb").ToHlsl());
        Assert.Equal("bin2(layers[0], (float4)(0.2), (float4)(0.8))", image.CreateLayerCompose("a.bin2(0.2, 0.8)").ToHlsl());
        // A scalar argument of a vector parameter is splatted so the HLSL overload is unambiguous.
        Assert.Equal("bin(layers[0].rgb, (float3)(0.5))", image.CreateLayerCompose("a.rgb.bin(0.5)").ToHlsl());
        Assert.Equal("bin(layers[0], (float4)(layers[1].r))", image.CreateLayerCompose("a.bin(b.r)").ToHlsl());
    }

    [Fact]
    public void TheShaderLibraryIsOnlyCompiledIntoMemberExpressions()
    {
        using var image = CreateImage();

        Assert.False(image.CreateLayerCompose("a.rgb").UsesMemberExpression);
        Assert.True(image.CreateLayerCompose("a.lum, a.rgba.r, a.rgba.g, a.rgba.b").UsesMemberExpression);

        // The flag decides whether the generated macros include pulls in Shaders/Common.hlsl.
        Assert.DoesNotContain("MEMBER_LIBRARY", GeneratedIncludes(image, "a.rgb"));
        Assert.Contains("#define MEMBER_LIBRARY", GeneratedIncludes(image, "a.lum, a.rgba.r, a.rgba.g, a.rgba.b"));
    }

    [Fact]
    public void TheGeneratedMacrosDescribeTheDraw()
    {
        using var image = CreateImage();

        string macros = GeneratedIncludes(image, "a.rgb");
        Assert.Contains("#define LAYER_COUNT 2", macros);
        Assert.Contains("#define STORAGE_RGBA", macros);
        Assert.Contains("#define COMPOSE_PIXEL layers[0].rgb, 1", macros);

        using var gray = new GrotheImage(new GrotheImageInfo(64, 64, 64, 64), PixelFormat.Gray8, "a");
        Assert.Contains("#define STORAGE_GRAY", GeneratedIncludes(gray, "a.r"));
        Assert.Contains("#define LAYER_COUNT 1", GeneratedIncludes(gray, "a.r"));
        // A plain load passes the single bound layer through.
        Assert.Contains("#define COMPOSE_PIXEL layers[0]", GeneratedIncludes(gray, null));
    }

    [Fact]
    public void MissingExpressionChannelsArePaddedIntoTheGeneratedPixel()
    {
        using var image = CreateImage();

        string Expression(string expression) => LoadProgram.BuildExpression(image.CreateLayerCompose(expression));

        Assert.Equal("layers[0].rgba", Expression("a.rgba"));
        // Three channels keep their order, alpha becomes one.
        Assert.Equal("layers[0].rgb, 1", Expression("a.rgb"));
        // Two channels keep their order, blue becomes zero and alpha becomes one.
        Assert.Equal("layers[0].gb, 0, 1", Expression("a.gb"));
        // One channel is replicated across red, green and blue with alpha of one.
        Assert.Equal("lum(layers[0]), lum(layers[0]), lum(layers[0]), 1", Expression("a.lum"));
        // A composition already produces a complete value, so only the missing channels are added.
        Assert.Equal("float4(layers[0].a, layers[0].r, layers[0].g, layers[0].b)", Expression("a.a, a.r, a.g, a.b"));
        Assert.Equal("float2(layers[0].r, layers[1].g), 0, 1", Expression("a.r, b.g"));
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
}
