// Update pass: one compute dispatch per tile. It walks the Grothe Image pixels of one tile, finds the User
// Image pixel that feeds each of them and writes the converted value straight into the tile array UAVs.
//
// Clipping rule: a Grothe Image pixel whose sample does not land inside the User Image is *clipped* - the
// shader abandons the pixel without writing, so the tile keeps whatever it already stored (the zero value it
// was initialised with, or the result of an earlier write). Update only ever paints where the User Image
// actually lands; it never clears anything.
//
// All constants come from the generated <macros> include (see UpdateProgram):
//   STORAGE_RGBA / STORAGE_GRAY / STORAGE_YUV   storage format of the tile planes
//
// Constants layout (cbuffer Params, six float4 = ShaderParameters.FloatCount floats):
//   M0..M2      Grothe Image -> User Image transform (the inverse of the public Update matrix)
//   Grid        StepX, StepY, TileColumns, TileRows
//   Tile        TileWidth, TileHeight, tile origin x, tile origin y
//   SourceSize  User Image width, height, array slice, chroma subsampling mode (0 = 4:4:4, 1 = 4:2:2, 2 = 4:2:0)

#include <macros>

Texture2D<float4> Source : register(t0);
SamplerState LinearSampler : register(s0);
cbuffer Params : register(b0) { float4 M0; float4 M1; float4 M2; float4 Grid; float4 Tile; float4 SourceSize; };

#if defined(STORAGE_YUV)
RWTexture2DArray<float> YTarget : register(u0);
RWTexture2DArray<float2> UvTarget : register(u1);
#elif defined(STORAGE_GRAY)
RWTexture2DArray<float> Target : register(u0);
#else
RWTexture2DArray<float4> Target : register(u0);
#endif

// BT.709 limited range ("studio swing"): Y' is stored in [16, 235] and Cb/Cr in [16, 240] of 8 bit units.
// Load.hlsl decodes exactly this range, so the encoder has to compress luma the same way it compresses
// chroma. The chroma differences stay defined against the uncompressed luma Y'.
float3 ToYuv(float3 rgb)
{
    float y = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return float3(0.0627451 + 0.8588235 * y,
                  0.5019608 + 0.4733944 * (rgb.b - y),
                  0.5019608 + 0.5651875 * (rgb.r - y));
}

#if defined(STORAGE_YUV)
void Store(uint3 id, float4 value)
{
    float3 yuv = ToYuv(value.rgb);
    YTarget[uint3(id.xy, (uint)SourceSize.z)] = yuv.x;
    // Yuv422/Yuv420 only carry chroma on the even pixel of each subsampled axis.
    if ((SourceSize.w < 0.5 || (id.x & 1) == 0) && (SourceSize.w < 1.5 || (id.y & 1) == 0))
        UvTarget[uint3(SourceSize.w < 0.5 ? id.x : id.x / 2, SourceSize.w < 1.5 ? id.y : id.y / 2, (uint)SourceSize.z)] = yuv.yz;
}
#elif defined(STORAGE_GRAY)
void Store(uint3 id, float4 value)
{
    Target[uint3(id.xy, (uint)SourceSize.z)] = value.r;
}
#else
void Store(uint3 id, float4 value)
{
    Target[uint3(id.xy, (uint)SourceSize.z)] = value;
}
#endif

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Tile.x || id.y >= Tile.y) return;

    // Pixel centres, so a scale or a rotation does not shift the image by half a pixel.
    float2 grothe = float2(Tile.z + id.x + 0.5, Tile.w + id.y + 0.5);
    float3 q = float3(dot(M0.xyz, float3(grothe, 1)), dot(M1.xyz, float3(grothe, 1)), dot(M2.xyz, float3(grothe, 1)));
    // A homogeneous divisor at (or next to) zero has no User Image position, and a NaN divisor slips
    // through every comparison below. This matches TransformMatrix.TryTransformPoint.
    if (!(abs(q.z) > 1e-15)) return;

    float2 source = q.xy / q.z;
    // Clip: outside the User Image the tile keeps its current value.
    if (any(isnan(source)) || !all(source >= 0) || !all(source < SourceSize.xy)) return;

    Store(id, Source.SampleLevel(LinearSampler, source / SourceSize.xy, 0));
}
