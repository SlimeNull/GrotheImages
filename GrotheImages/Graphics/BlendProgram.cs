using System;
using System.Collections.Generic;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace GrotheImages;

/// <summary>
/// One tile seam of one plane: the band that two neighbouring tiles share. Both tiles store the same
/// logical pixels there, so the band is described in tile-local coordinates for each side.
/// </summary>
internal readonly struct BlendSeam
{
    public BlendSeam(
        long rowA, long columnA, long rowB, long columnB,
        int originAX, int originAY, int originBX, int originBY,
        int width, int height, bool alongX, int overlap)
    {
        RowA = rowA;
        ColumnA = columnA;
        RowB = rowB;
        ColumnB = columnB;
        OriginAX = originAX;
        OriginAY = originAY;
        OriginBX = originBX;
        OriginBY = originBY;
        Width = width;
        Height = height;
        AlongX = alongX;
        Overlap = overlap;
    }

    public long RowA { get; }
    public long ColumnA { get; }
    public long RowB { get; }
    public long ColumnB { get; }
    public int OriginAX { get; }
    public int OriginAY { get; }
    public int OriginBX { get; }
    public int OriginBY { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>True when the fade runs along x (a vertical join), false when it runs along y.</summary>
    public bool AlongX { get; }
    public int Overlap { get; }
}

/// <summary>One plane of an image: the color plane, or the luma and chroma planes of a YUV image.</summary>
internal readonly struct BlendPlane
{
    public BlendPlane(bool chroma, int width, int height, int overlapX, int overlapY)
    {
        Chroma = chroma;
        Width = width;
        Height = height;
        OverlapX = overlapX;
        OverlapY = overlapY;
    }

    public bool Chroma { get; }
    public int Width { get; }
    public int Height { get; }
    public int OverlapX { get; }
    public int OverlapY { get; }
}

/// <summary>
/// Blends every seam of an image: for each pair of neighbouring tiles the overlapping band is cross faded
/// and written back into both tiles. The pass is a compute dispatch per seam and plane, so the stored
/// tiles themselves become seamless and later loads (and serialization) see the blended data.
/// </summary>
internal sealed class BlendProgram : IDisposable
{
    private readonly GrotheImage _image;
    private readonly Dictionary<string, ID3D11ComputeShader> _shaders = new Dictionary<string, ID3D11ComputeShader>(StringComparer.Ordinal);
    private ID3D11Buffer _constants;

    public BlendProgram(GrotheImage image)
    {
        _image = image;
    }

    /// <summary>
    /// The planes of an image together with their own tile geometry. A subsampled chroma plane is smaller
    /// than the tile, so its step and overlap shrink with it.
    /// </summary>
    internal static IEnumerable<BlendPlane> EnumeratePlanes(PixelFormat format, GrotheImageInfo info)
    {
        if (!PixelFormatRules.IsYuv(format))
        {
            yield return new BlendPlane(false, info.TileWidth, info.TileHeight, info.TileOverlapX, info.TileOverlapY);
            yield break;
        }

        int subsampleX = PixelFormatRules.ChromaSubsampleX(format);
        int subsampleY = PixelFormatRules.ChromaSubsampleY(format);
        yield return new BlendPlane(false, info.TileWidth, info.TileHeight, info.TileOverlapX, info.TileOverlapY);
        yield return new BlendPlane(true, info.TileWidth / subsampleX, info.TileHeight / subsampleY, info.TileOverlapX / subsampleX, info.TileOverlapY / subsampleY);
    }

    /// <summary>
    /// Enumerates the seams of one plane. The plane passes its own size, because the chroma plane of a
    /// subsampled YUV image has its own tile geometry.
    /// </summary>
    internal static IEnumerable<BlendSeam> EnumerateSeams(GrotheImageInfo info, int planeWidth, int planeHeight, int overlapX, int overlapY)
    {
        if (overlapX > 0)
        {
            int stepX = planeWidth - overlapX;
            for (long row = 0; row < info.TileRows; row++)
                for (long column = 0; column + 1 < info.TileColumns; column++)
                    yield return new BlendSeam(row, column, row, column + 1, stepX, 0, 0, 0, overlapX, planeHeight, true, overlapX);
        }

        if (overlapY > 0)
        {
            int stepY = planeHeight - overlapY;
            for (long row = 0; row + 1 < info.TileRows; row++)
                for (long column = 0; column < info.TileColumns; column++)
                    yield return new BlendSeam(row, column, row + 1, column, 0, stepY, 0, 0, planeWidth, overlapY, false, overlapY);
        }
    }

    public void Execute()
    {
        GrotheImage image = _image;
        GrotheImageInfo info = image.Info;
        EnsureConstants();

        for (int layer = 0; layer < image.LayerNames.Count; layer++)
        {
            LayerStore store = image.GetLayer(layer);
            foreach (BlendPlane plane in EnumeratePlanes(image.Format, info))
                Blend(image, store, plane);
        }

        image.Graphics.Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null, null });
        image.Graphics.Context.Flush();
    }

    public void Dispose()
    {
        foreach (ID3D11ComputeShader shader in _shaders.Values) shader.Dispose();
        _shaders.Clear();
        _constants?.Dispose();
        _constants = null;
    }

    private void Blend(GrotheImage image, LayerStore store, BlendPlane plane)
    {
        foreach (BlendSeam seam in EnumerateSeams(image.Info, plane.Width, plane.Height, plane.OverlapX, plane.OverlapY))
        {
            // Only join tiles that both hold data: blending an untouched tile would smear its zeros.
            if (!store.IsWritten(seam.RowA, seam.ColumnA) || !store.IsWritten(seam.RowB, seam.ColumnB)) continue;

            TileArrayPage pageA = store.GetPageForTile(seam.RowA, seam.ColumnA, out int sliceA);
            TileArrayPage pageB = store.GetPageForTile(seam.RowB, seam.ColumnB, out int sliceB);
            TileArrayResource resourceA = plane.Chroma ? pageA.Uv : (PixelFormatRules.IsYuv(image.Format) ? pageA.Y : pageA.Color);
            TileArrayResource resourceB = plane.Chroma ? pageB.Uv : (PixelFormatRules.IsYuv(image.Format) ? pageB.Y : pageB.Color);
            bool crossArray = !ReferenceEquals(resourceA, resourceB);

            var values = new uint[12];
            Write(values, 0, seam.OriginAX, seam.OriginAY, sliceA);
            Write(values, 4, seam.OriginBX, seam.OriginBY, sliceB);
            Write(values, 8, seam.Width, seam.Height, seam.AlongX ? 0 : 1, seam.Overlap);

            var context = image.Graphics.Context;
            context.CSSetShader(GetShader(plane.Chroma, crossArray));
            context.CSSetConstantBuffers(0, new[] { _constants });
            context.CSSetUnorderedAccessViews(0, crossArray
                ? new[] { resourceA.UnorderedAccessView, resourceB.UnorderedAccessView }
                : new[] { resourceA.UnorderedAccessView });
            context.UpdateSubresource(values, _constants, 0, 0, 0, null);
            context.Dispatch((seam.Width + 7) / 8, (seam.Height + 7) / 8, 1);
        }
    }

    private static void Write(uint[] values, int offset, int a, int b, int c, int d = 0)
    {
        values[offset] = (uint)a;
        values[offset + 1] = (uint)b;
        values[offset + 2] = (uint)c;
        values[offset + 3] = (uint)d;
    }

    private ID3D11ComputeShader GetShader(bool chroma, bool crossArray)
    {
        string key = (chroma ? "uv" : "plane") + (crossArray ? "+cross" : string.Empty);
        ID3D11ComputeShader shader;
        if (_shaders.TryGetValue(key, out shader)) return shader;

        var macros = new List<KeyValuePair<string, string>>();
        if (PixelFormatRules.IsYuv(_image.Format)) macros.Add(new KeyValuePair<string, string>("STORAGE_YUV", null));
        else if (_image.Format == PixelFormat.Gray8) macros.Add(new KeyValuePair<string, string>("STORAGE_GRAY", null));
        else macros.Add(new KeyValuePair<string, string>("STORAGE_RGBA", null));
        if (chroma) macros.Add(new KeyValuePair<string, string>("PLANE_UV", null));
        if (crossArray) macros.Add(new KeyValuePair<string, string>("CROSS_ARRAY", null));

        var include = new ShaderInclude(new[]
        {
            new KeyValuePair<string, string>("macros", ShaderSource.Macros(macros)),
        });
        using (Blob blob = ShaderCompiler.Compile(ShaderSource.Load("Blend.hlsl"), "Blend.hlsl", "CSMain", "cs_5_0", include))
            shader = _image.Graphics.Device.CreateComputeShader(blob, null);
        _shaders.Add(key, shader);
        return shader;
    }

    private void EnsureConstants()
    {
        if (_constants != null) return;
        _constants = _image.Graphics.Device.CreateBuffer(
            new uint[12],
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
    }
}
