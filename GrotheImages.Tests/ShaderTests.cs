using System;
using System.Collections.Generic;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Xunit;
using Xunit.Sdk;

namespace GrotheImages.Tests;

public sealed class ShaderTests
{
    private static Blob Compile(string source, string entryPoint, string target, Include include)
    {
        try
        {
            return ShaderCompiler.Compile(source, "test.hlsl", entryPoint, target, include);
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is TypeInitializationException)
        {
            throw SkipException.ForSkip("D3DCompiler is unavailable: " + ex.Message);
        }
    }

    /// <summary>Compiles a shader file with the macros a real draw would generate, so every #if branch runs.</summary>
    private static Blob CompileWithMacros(string file, string entryPoint, string target, params string[] macros)
    {
        var pairs = new List<KeyValuePair<string, string>>(macros.Length);
        foreach (string macro in macros)
        {
            int separator = macro.IndexOf('=');
            pairs.Add(separator < 0
                ? new KeyValuePair<string, string>(macro, null)
                : new KeyValuePair<string, string>(macro.Substring(0, separator), macro.Substring(separator + 1)));
        }
        return Compile(ShaderSource.Load(file), entryPoint, target, new ShaderInclude(pairs));
    }

    [Fact]
    public void TheShaderFilesCompileWithTheirEmbeddedDefaults()
    {
        // No injected macros: <macros> resolves to the default file, which is exactly what an editor or
        // an offline fxc invocation sees.
        var include = new ShaderInclude(new KeyValuePair<string, string>[0]);

        using (Blob vs = Compile(ShaderSource.Load("Load.hlsl"), "VSMain", "vs_5_0", include))
        using (Blob ps = Compile(ShaderSource.Load("Load.hlsl"), "PSMain", "ps_5_0", include))
        using (Blob cs = Compile(ShaderSource.Load("Update.hlsl"), "CSMain", "cs_5_0", include))
        using (Blob blend = Compile(ShaderSource.Load("Blend.hlsl"), "CSMain", "cs_5_0", include))
        {
            Assert.NotNull(vs);
            Assert.NotNull(ps);
            Assert.NotNull(cs);
            Assert.NotNull(blend);
        }
    }

    [Theory]
    [InlineData("STORAGE_RGBA")]
    [InlineData("STORAGE_GRAY")]
    [InlineData("STORAGE_YUV")]
    public void LoadCompilesForEveryStorageFormat(string storage)
    {
        using (Blob ps = CompileWithMacros("Load.hlsl", "PSMain", "ps_5_0", "LAYER_COUNT=2", storage, "COMPOSE_PIXEL=layers[0] + layers[1]"))
            Assert.NotNull(ps);
        // The member library is only included for expressions that call into Common.hlsl.
        using (Blob withMembers = CompileWithMacros("Load.hlsl", "PSMain", "ps_5_0", "LAYER_COUNT=1", storage, "MEMBER_LIBRARY", "COMPOSE_PIXEL=lum(layers[0])"))
            Assert.NotNull(withMembers);
    }

    [Theory]
    [InlineData("STORAGE_RGBA")]
    [InlineData("STORAGE_GRAY")]
    [InlineData("STORAGE_YUV")]
    public void UpdateCompilesForEveryStorageFormat(string storage)
    {
        using (Blob cs = CompileWithMacros("Update.hlsl", "CSMain", "cs_5_0", storage))
            Assert.NotNull(cs);
    }

    [Theory]
    [InlineData("STORAGE_RGBA")]
    [InlineData("STORAGE_YUV")]
    [InlineData("STORAGE_YUV", "PLANE_UV")]
    [InlineData("STORAGE_YUV", "PLANE_UV", "CROSS_ARRAY")]
    [InlineData("STORAGE_GRAY")]
    public void BlendCompilesForEveryPlaneAndPageVariant(params string[] macros)
    {
        using (Blob cs = CompileWithMacros("Blend.hlsl", "CSMain", "cs_5_0", macros))
            Assert.NotNull(cs);
    }

    [Fact]
    public void TheShaderLibraryCompiles()
    {
        const string call =
            "float4 PSMain() : SV_Target { float scalar = lum(float3(0.25, 0.5, 0.75)); return float4(scalar.rrr, hsv(float3(0.1, 0.2, 0.3)).r + min(float4(1, 2, 3, 4)) + sum(bin2(float2(0.4, 0.6), 0.2, 0.8))); }";

        using (Blob blob = Compile(ShaderLibrary.Source + Environment.NewLine + call, "PSMain", "ps_5_0", null))
            Assert.NotNull(blob);
    }

    [Fact]
    public void EveryShaderFileIsEmbedded()
    {
        foreach (string name in new[] { "Common.hlsl", "Load.hlsl", "Update.hlsl", "Blend.hlsl", "macros" })
            Assert.False(string.IsNullOrWhiteSpace(ShaderSource.Load(name)));
    }

    [Fact]
    public void AMissingShaderResourceIsReportedWithItsName()
    {
        GrotheImageException missing = Assert.Throws<GrotheImageException>(() => ShaderSource.Load("DoesNotExist.hlsl"));
        Assert.Contains("DoesNotExist.hlsl", missing.Message);
        Assert.Contains("GrotheImages.Shaders.DoesNotExist.hlsl", missing.Message);
    }

    [Fact]
    public void AFailedCompilationReportsTheEntryPointAndTheInjectedMacros()
    {
        // The injected COMPOSE_PIXEL is not valid HLSL, so the compiler fails and the message has to carry the
        // generated macros: that is what makes a bad expression diagnosable.
        var include = new ShaderInclude(new[]
        {
            new KeyValuePair<string, string>("macros", ShaderSource.Macros(new[]
            {
                new KeyValuePair<string, string>("LAYER_COUNT", "1"),
                new KeyValuePair<string, string>("STORAGE_RGBA", null),
                new KeyValuePair<string, string>("COMPOSE_PIXEL", "not valid hlsl"),
            })),
        });

        GrotheImageException failure = Assert.Throws<GrotheImageException>(() =>
            Compile(ShaderSource.Load("Load.hlsl"), "PSMain", "ps_5_0", include));

        // The source name comes from the caller of ShaderCompiler, the rest describes this compilation.
        Assert.Contains("test.hlsl", failure.Message);
        Assert.Contains("PSMain", failure.Message);
        Assert.Contains("ps_5_0", failure.Message);
        Assert.Contains("COMPOSE_PIXEL", failure.Message);
    }
}
