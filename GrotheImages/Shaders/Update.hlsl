// Update pass: one compute dispatch per tile. It samples the upload texture in tile space and writes the
// converted pixels straight into the tile array UAVs.
//
// Includes generated for each compilation (see UpdateProgram):
//   <macros>       STORAGE_RGBA / STORAGE_GRAY / STORAGE_YUV

#include <macros>

Texture2D<float4> Source : register(t0);
SamplerState LinearSampler : register(s0);
cbuffer Params : register(b0) { float4 M0; float4 M1; float4 M2; float4 Grid; float4 Tile; float4 SourceSize; };

#if defined(STORAGE_YUV)
RWTexture2DArray<float> YTarget : register(u0);
RWTexture2DArray<float2> UvTarget : register(u1);

// BT.709 limited range RGB -> YUV.
float3 ToYuv(float3 rgb)
{
    float y = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return float3(y, (rgb.b - y) * 0.5389 + 0.5, (rgb.r - y) * 0.6350 + 0.5);
}
#elif defined(STORAGE_GRAY)
RWTexture2DArray<float> Target : register(u0);
#else
RWTexture2DArray<float4> Target : register(u0);
#endif

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Tile.x || id.y >= Tile.y) return;

    float2 global = float2(Tile.z + id.x + 0.5, Tile.w + id.y + 0.5);
    float3 q = float3(dot(M0.xyz, float3(global, 1)), dot(M1.xyz, float3(global, 1)), dot(M2.xyz, float3(global, 1)));
    float2 source = q.xy / q.z;
    if (source.x < 0 || source.y < 0 || source.x >= SourceSize.x || source.y >= SourceSize.y) return;

    float4 value = Source.SampleLevel(LinearSampler, source / SourceSize.xy, 0);

#if defined(STORAGE_YUV)
    // SourceSize.w is the chroma subsampling mode: 0 = 4:4:4, 1 = 4:2:2, 2 = 4:2:0.
    float3 yuv = ToYuv(value.rgb);
    YTarget[uint3(id.xy, (uint)SourceSize.z)] = yuv.x;
    if ((SourceSize.w < 0.5 || (id.x & 1) == 0) && (SourceSize.w < 1.5 || (id.y & 1) == 0))
        UvTarget[uint3(SourceSize.w < 0.5 ? id.x : id.x / 2, SourceSize.w < 1.5 ? id.y : id.y / 2, (uint)SourceSize.z)] = yuv.yz;
#elif defined(STORAGE_GRAY)
    Target[uint3(id.xy, (uint)SourceSize.z)] = value.r;
#else
    Target[uint3(id.xy, (uint)SourceSize.z)] = value;
#endif
}
