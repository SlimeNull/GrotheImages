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

    [Fact]
    public void TheShaderFilesCompileWithTheirEmbeddedDefaults()
    {
        // No injected macros: <macros> resolves to the default file, which is exactly what an editor or
        // an offline fxc invocation sees.
        var include = new ShaderInclude(new KeyValuePair<string, string>[0]);

        using (Blob vs = Compile(ShaderSource.Load("Load.hlsl"), "VSMain", "vs_5_0", include))
        using (Blob ps = Compile(ShaderSource.Load("Load.hlsl"), "PSMain", "ps_5_0", include))
        using (Blob cs = Compile(ShaderSource.Load("Update.hlsl"), "CSMain", "cs_5_0", include))
        {
            Assert.NotNull(vs);
            Assert.NotNull(ps);
            Assert.NotNull(cs);
        }
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
        foreach (string name in new[] { "Common.hlsl", "Load.hlsl", "Update.hlsl", "macros" })
            Assert.False(string.IsNullOrWhiteSpace(ShaderSource.Load(name)));
    }
}
