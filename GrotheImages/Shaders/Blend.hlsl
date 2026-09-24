// Seam blending: cross-fades the overlapping border shared by two neighbouring tiles and writes the same
// blended pixels back into both tiles, so the duplicate storage of the overlap stays identical and the
// join becomes invisible.
//
// One dispatch handles one seam of one plane. Every thread owns one pair of texels (one in each tile at
// the same offset inside the overlap) and writes both of them, so no thread ever reads a texel another
// thread writes.
//
// Macros generated for each compilation (see BlendProgram):
//   STORAGE_RGBA / STORAGE_GRAY / STORAGE_YUV   storage format of the GrotheImage
//   PLANE_UV                                    the chroma plane of a YUV image, otherwise the luma plane
//   CROSS_ARRAY                                 the two tiles live in different texture array pages

#include <macros>

#if defined(STORAGE_RGBA)
#define ELEMENT float4
#elif defined(PLANE_UV)
#define ELEMENT float2
#else
#define ELEMENT float
#endif

#if defined(CROSS_ARRAY)
RWTexture2DArray<ELEMENT> TargetA : register(u0);
RWTexture2DArray<ELEMENT> TargetB : register(u1);
#else
// Both tiles of this seam live in the same page texture, so one UAV addresses them by slice.
RWTexture2DArray<ELEMENT> Target : register(u0);
#define TargetA Target
#define TargetB Target
#endif

cbuffer Params : register(b0)
{
    uint4 BandA;   // origin within the first tile: xy, array slice: z
    uint4 BandB;   // origin within the second tile: xy, array slice: z
    uint4 Shape;   // band size: xy, ramp axis (0 = x, 1 = y): z, overlap length: w
};

float Ramp(uint offset)
{
    // The ramp is centred on the midpoint, which is exactly where the load shader switches from one tile
    // to the other. Smoothstep keeps the derivative continuous at both ends of the band.
    float t = ((float)offset + 0.5) / (float)Shape.w;
    return smoothstep(0.0, 1.0, t);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Shape.x || id.y >= Shape.y) return;

    bool alongX = Shape.z == 0;
    uint offset = alongX ? id.x : id.y;
    uint2 within = alongX ? uint2(offset, id.y) : uint2(id.x, offset);

    uint2 left = BandA.xy + within;
    uint2 right = BandB.xy + within;

    ELEMENT a = TargetA[uint3(left, BandA.z)];
    ELEMENT b = TargetB[uint3(right, BandB.z)];
    ELEMENT blended = lerp(a, b, Ramp(offset));

    TargetA[uint3(left, BandA.z)] = blended;
    TargetB[uint3(right, BandB.z)] = blended;
}
