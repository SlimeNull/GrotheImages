// Load pass: one full screen triangle that reads every bound layer of a Grothe Image and writes the
// requested User Image pixel. Compiled for both the vertex and the pixel stage.
//
// Macros generated for each compilation (see LoadProgram):
//   LAYER_COUNT                     layers bound for this draw, 1 for a plain load
//   STORAGE_RGBA/GRAY/YUV           storage format of the Grothe Image, exactly one is defined
//   MEMBER_LIBRARY                  defined when the expression calls into Shaders/Common.hlsl
//   COMPOSE_PIXEL                   the compose expression, already padded to four channels
//
// Constants layout (cbuffer Params, six float4 = ShaderParameters.FloatCount floats):
//   M0..M2      User Image -> Grothe Image transform (the inverse of the public Load matrix)
//   Grid        StepX, StepY, TileColumns, TileRows
//   Tile        TileWidth, TileHeight, TileOverlapX, TileOverlapY
//   Image       Width, Height, page base slice, page slice count
//
// A Gray8 render target keeps red, so an expression with fewer than four channels is padded by
// LoadProgram instead of by this shader.

#include <macros>

#ifdef MEMBER_LIBRARY
#include "Common.hlsl"
#endif

// Token pasting lets the chroma array start right after the luma array, whatever LAYER_COUNT is.
#define GI_PASTE_(a, b) a##b
#define GI_PASTE(a, b) GI_PASTE_(a, b)

#if defined(STORAGE_YUV)
Texture2DArray<float> Y[LAYER_COUNT] : register(t0);
Texture2DArray<float2> Uv[LAYER_COUNT] : register(GI_PASTE(t, LAYER_COUNT));
#elif defined(STORAGE_GRAY)
Texture2DArray<float> Tiles[LAYER_COUNT] : register(t0);
#else
Texture2DArray<float4> Tiles[LAYER_COUNT] : register(t0);
#endif

SamplerState LinearSampler : register(s0);
cbuffer Params : register(b0) { float4 M0; float4 M1; float4 M2; float4 Grid; float4 Tile; float4 Image; };

struct VSOut { float4 Position : SV_Position; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.Position = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// Turns a User Image pixel centre into a Grothe Image coordinate. False when the pixel has no image,
// which happens on the horizon of a perspective transform.
bool TryGrotheCoordinate(float2 user, out float2 grothe)
{
    float3 q = float3(dot(M0.xyz, float3(user, 1)), dot(M1.xyz, float3(user, 1)), dot(M2.xyz, float3(user, 1)));
    grothe = float2(0, 0);
    // A homogeneous divisor at (or next to) zero has no image, and a NaN divisor slips through every
    // comparison below, so both are rejected here. This matches TransformMatrix.TryTransformPoint.
    if (!(abs(q.z) > 1e-15)) return false;
    float2 candidate = q.xy / q.z;
    if (any(isnan(candidate))) return false;
    grothe = candidate;
    return true;
}

// The logical channels of one layer: RGB storage keeps R/G/B/A, Gray8 returns v/v/v/1 and YUV returns
// Y/U/V/1, which is the channel order the expression language documents.
float4 ReadLayer(int index, float2 uv, int slice)
{
#if defined(STORAGE_YUV)
    float y = Y[index].SampleLevel(LinearSampler, float3(uv, slice), 0);
    float2 chroma = Uv[index].SampleLevel(LinearSampler, float3(uv, slice), 0);
    return float4(y, chroma, 1);
#elif defined(STORAGE_GRAY)
    float v = Tiles[index].SampleLevel(LinearSampler, float3(uv, slice), 0);
    return float4(v, v, v, 1);
#else
    return Tiles[index].SampleLevel(LinearSampler, float3(uv, slice), 0);
#endif
}

float4 ToOutput(float4 value)
{
#if defined(STORAGE_YUV)
    // BT.709 limited range YUV -> RGB, the inverse of Update.hlsl's ToYuv. The requested output formats are
    // always RGB or Gray, so the result gets an opaque alpha.
    float yy = (value.x - 0.0627451) * 1.1643836;
    float uu = value.y - 0.5019608;
    float vv = value.z - 0.5019608;
    return float4(yy + 1.7927415 * vv, yy - 0.2132486 * uu - 0.5329093 * vv, yy + 2.1124018 * uu, 1);
#else
    return value;
#endif
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 source;
    if (!TryGrotheCoordinate(input.Position.xy, source)) return float4(0, 0, 0, 0);
    if (!all(source >= 0) || !all(source < Image.xy)) return float4(0, 0, 0, 0);

    int column = clamp((int)floor((source.x - 0.5 * Tile.z) / Grid.x), 0, (int)Grid.z - 1);
    int row = clamp((int)floor((source.y - 0.5 * Tile.w) / Grid.y), 0, (int)Grid.w - 1);
    int globalSlice = row * (int)Grid.z + column;
    if (globalSlice < (int)Image.z || globalSlice >= (int)(Image.z + Image.w)) discard;
    int slice = globalSlice - (int)Image.z;
    float2 local = source - float2(column, row) * Grid.xy;
    float2 uv = local / Tile.xy;

    // Every layer is sampled once and COMPOSE_PIXEL indexes the array with literal indices, so the whole
    // loop unrolls into plain register reads.
    float4 layers[LAYER_COUNT];
    [unroll]
    for (int i = 0; i < LAYER_COUNT; i++) layers[i] = ReadLayer(i, uv, slice);

    return ToOutput(float4(COMPOSE_PIXEL));
}
