namespace GrotheImages;

/// <summary>
/// Layout of the <c>Params</c> constant buffer shared by the compute shaders. The HLSL side declares the
/// same layout as six <c>float4</c> fields; both sizes are checked by the tests.
/// </summary>
internal static class ShaderParameters
{
    /// <summary>Number of <c>float</c> values a <c>Params</c> constant buffer holds (six <c>float4</c>).</summary>
    public const int FloatCount = 24;

    /// <summary>Buffer size in bytes of a <c>Params</c> constant buffer.</summary>
    public const int ByteCount = FloatCount * sizeof(float);
}
