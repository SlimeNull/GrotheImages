using System;
using System.Collections.Generic;
using System.Globalization;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

namespace GrotheImages;

internal sealed class LoadProgram : IDisposable
{
    private readonly GrotheImage _image;
    private readonly LayerCompose _compose;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _constants;

    public LoadProgram(GrotheImage image, LayerCompose compose)
    {
        _image = image;
        _compose = compose;
        ShaderInclude include = BuildInclude(image, compose);
        string source = ShaderSource.Load("Load.hlsl");
        using (Blob vsBlob = ShaderCompiler.Compile(source, "Load.hlsl", "VSMain", "vs_5_0", include))
        using (Blob psBlob = ShaderCompiler.Compile(source, "Load.hlsl", "PSMain", "ps_5_0", include))
        {
            _vertexShader = image.Graphics.Device.CreateVertexShader(vsBlob, null);
            _pixelShader = image.Graphics.Device.CreatePixelShader(psBlob, null);
        }

        _sampler = image.Graphics.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            0,
            1,
            ComparisonFunction.Never,
            0,
            float.MaxValue));
        _constants = image.Graphics.Device.CreateBuffer(
            new float[ShaderParameters.FloatCount],
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
    }

    /// <summary>
    /// Reads a rectangle of the Grothe Image into a User Image. <paramref name="userToGrothe"/> is the
    /// inverse of the matrix the public API takes: the pixel shader turns a User Image pixel into a Grothe
    /// Image coordinate with it, and the page selection turns the User Image rectangle back into tiles.
    /// </summary>
    public void Execute(int layerIndex, nint output, int width, int height, int stride, PixelFormat format, TransformMatrix userToGrothe)
    {
        GrotheImage image = _image;
        DxgiFormat renderFormat = GetRenderFormat(format);
        ID3D11Texture2D render = image.Graphics.Device.CreateTexture2D(
            renderFormat, width, height, 1, 1, null,
            BindFlags.RenderTarget,
            ResourceOptionFlags.None,
            ResourceUsage.Default,
            CpuAccessFlags.None);
        ID3D11RenderTargetView target = image.Graphics.Device.CreateRenderTargetView(render, null);
        ID3D11Texture2D staging = image.Graphics.Device.CreateTexture2D(
            renderFormat, width, height, 1, 1, null,
            BindFlags.None,
            ResourceOptionFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read);
        ID3D11DeviceContext context = image.Graphics.Context;
        int boundResourceCount = 0;
        try
        {
            context.OMSetRenderTargets(target, null);
            context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
            context.RSSetViewports(new[] { new Viewport(0, 0, width, height) });
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetSamplers(0, new[] { _sampler });
            context.PSSetConstantBuffers(0, new[] { _constants });
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            if (!TryGetRequiredPages(image, userToGrothe, width, height, out List<long> requiredPages))
                throw new ArgumentException("The transform matrix maps a corner of the rectangle to a point at infinity.", GpuImageProcessor.TransformParameterName);

            // A compose binds only the layers its expression reads; the generated HLSL numbers them from 0.
            IReadOnlyList<int> boundLayers = _compose == null ? new[] { layerIndex } : _compose.BoundLayerIndices;
            var pages = new TileArrayPage[boundLayers.Count];
            foreach (long pageIndex in requiredPages)
            {
                for (int i = 0; i < boundLayers.Count; i++)
                    pages[i] = image.GetLayer(boundLayers[i]).GetOrCreatePage(pageIndex);

                // The shader binds the layers as resource arrays: color and gray layers occupy
                // t0..t(n-1), YUV storage puts every luma plane first and every chroma plane after it.
                var resources = new List<ID3D11ShaderResourceView>(boundLayers.Count * 2);
                foreach (TileArrayPage page in pages)
                    if (page.Color != null) resources.Add(page.Color.ShaderResourceView);
                foreach (TileArrayPage page in pages)
                    if (page.Y != null) resources.Add(page.Y.ShaderResourceView);
                foreach (TileArrayPage page in pages)
                    if (page.Uv != null) resources.Add(page.Uv.ShaderResourceView);

                float[] values = CreateConstants(userToGrothe, image.Info, pageIndex * LayerStore.MaxArraySlices, pages[0].ArraySize);
                context.UpdateSubresource(values, _constants, 0, 0, 0, null);
                context.PSSetShaderResources(0, resources.ToArray());
                boundResourceCount = Math.Max(boundResourceCount, resources.Count);
                context.Draw(3, 0);
            }
            context.CopyResource(staging, render);
            context.Flush();
            Readback(context, staging, output, width, height, stride, format);
        }
        finally
        {
            // Nothing may stay bound to a resource that is about to be released.
            if (boundResourceCount > 0)
                context.PSSetShaderResources(0, new ID3D11ShaderResourceView[boundResourceCount]);
            context.OMSetRenderTargets((ID3D11RenderTargetView)null, null);
            staging.Dispose();
            target.Dispose();
            render.Dispose();
        }
    }

    public void Dispose()
    {
        _constants?.Dispose();
        _sampler?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
    }

    /// <summary>
    /// The <c>macros</c> include generated for one Load compilation: the image, the draw and the pixel the
    /// expression produces.
    /// </summary>
    internal static ShaderInclude BuildInclude(GrotheImage image, LayerCompose compose)
    {
        return new ShaderInclude(new[]
        {
            new KeyValuePair<string, string>("macros", ShaderSource.Macros(BuildMacros(image, compose))),
        });
    }

    internal static IEnumerable<KeyValuePair<string, string>> BuildMacros(GrotheImage image, LayerCompose compose)
    {
        // A plain load binds one layer and passes it through. A compose binds only the layers its
        // expression actually reads, which are renumbered to 0..n-1 in the generated HLSL.
        yield return new KeyValuePair<string, string>("LAYER_COUNT",
            (compose == null ? 1 : compose.BoundLayerIndices.Count).ToString(CultureInfo.InvariantCulture));
        if (PixelFormatRules.IsYuv(image.Format)) yield return new KeyValuePair<string, string>("STORAGE_YUV", null);
        else if (image.Format == PixelFormat.Gray8) yield return new KeyValuePair<string, string>("STORAGE_GRAY", null);
        else yield return new KeyValuePair<string, string>("STORAGE_RGBA", null);
        // Shaders/Common.hlsl is only compiled in when the expression actually calls into it.
        if (compose != null && compose.UsesMemberExpression) yield return new KeyValuePair<string, string>("MEMBER_LIBRARY", null);
        yield return new KeyValuePair<string, string>("COMPOSE_PIXEL", compose == null ? "layers[0]" : BuildExpression(compose));
    }

    /// <summary>
    /// An expression with fewer than four channels is padded into a complete pixel: 3 -> (x, y, z, 1),
    /// 2 -> (x, y, 0, 1) and 1 -> (x, x, x, 1). Missing color channels become zero, a missing alpha
    /// becomes one. Channels only have positional meaning and a Gray8 render target keeps red.
    /// </summary>
    internal static string BuildExpression(LayerCompose compose)
    {
        string expression = compose.ToHlsl();
        switch (compose.OutputChannelCount)
        {
            case 1: return expression + ", " + expression + ", " + expression + ", 1";
            case 2: return expression + ", 0, 1";
            case 3: return expression + ", 1";
            default: return expression;
        }
    }

    private static float[] CreateConstants(TransformMatrix userToGrothe, GrotheImageInfo info, long pageBase, int pageSize)
    {
        return new[]
        {
            (float)userToGrothe.M00, (float)userToGrothe.M01, (float)userToGrothe.M02, 0f,
            (float)userToGrothe.M10, (float)userToGrothe.M11, (float)userToGrothe.M12, 0f,
            (float)userToGrothe.M20, (float)userToGrothe.M21, (float)userToGrothe.M22, 0f,
            (float)info.StepX, (float)info.StepY, info.TileColumns, info.TileRows,
            (float)info.TileWidth, (float)info.TileHeight, info.TileOverlapX, info.TileOverlapY,
            info.Width, info.Height, pageBase, pageSize,
        };
    }

    /// <summary>
    /// The texture array pages this draw can reach: the User Image rectangle mapped back into Grothe Image
    /// space, reduced to the tiles the sampling ownership rule can select. Pages are visited in ascending
    /// order so the draw order does not depend on hashing.
    /// </summary>
    private static bool TryGetRequiredPages(GrotheImage image, TransformMatrix userToGrothe, int outputWidth, int outputHeight, out List<long> pages)
    {
        if (!TileGrid.TryGetCoveredRegion(userToGrothe, outputWidth, outputHeight, out CoveredRegion region))
        {
            pages = null;
            return false;
        }

        region.GetSamplingTileRange(image.Info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn);
        var pageSet = new HashSet<long>();
        for (long row = firstRow; row <= lastRow; row++)
        for (long column = firstColumn; column <= lastColumn; column++)
            pageSet.Add(TileGrid.GetLinearIndex(image.Info, row, column) / LayerStore.MaxArraySlices);
        var required = new List<long>(pageSet);
        required.Sort();
        pages = required;
        return true;
    }

    private static DxgiFormat GetRenderFormat(PixelFormat format)
    {
        switch (format)
        {
            case PixelFormat.Gray8: return DxgiFormat.R8_UNorm;
            case PixelFormat.Bgra32: return DxgiFormat.B8G8R8A8_UNorm;
            case PixelFormat.Rgba32: return DxgiFormat.R8G8B8A8_UNorm;
            default: throw new ArgumentException("Only Bgra32, Rgba32 and Gray8 are supported as output image formats.", nameof(format));
        }
    }

    private static unsafe void Readback(ID3D11DeviceContext context, ID3D11Texture2D staging, nint destination, int width, int height, int stride, PixelFormat format)
    {
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            int rowBytes = checked(width * PixelFormatRules.BytesPerPixel(format));
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy((void*)(mapped.DataPointer + y * mapped.RowPitch), (void*)(destination + y * stride), stride, rowBytes);
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
