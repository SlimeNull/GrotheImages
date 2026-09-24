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
            new float[28],
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
    }

    public void Execute(int layerIndex, nint output, int width, int height, int stride, PixelFormat format, TransformMatrix inverse)
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
            try
            {
                image.Graphics.Context.OMSetRenderTargets(target, null);
                image.Graphics.Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
                image.Graphics.Context.RSSetViewports(new[] { new Viewport(0, 0, width, height) });
                image.Graphics.Context.VSSetShader(_vertexShader);
                image.Graphics.Context.PSSetShader(_pixelShader);
                image.Graphics.Context.PSSetSamplers(0, new[] { _sampler });
                image.Graphics.Context.PSSetConstantBuffers(0, new[] { _constants });
                image.Graphics.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                foreach (long pageIndex in GetRequiredPages(image.Info, inverse, width, height))
                {
                    int firstLayer = _compose == null ? layerIndex : 0;
                    int layerCount = _compose == null ? 1 : image.LayerNames.Count;
                    var pages = new TileArrayPage[layerCount];
                    for (int i = 0; i < layerCount; i++)
                        pages[i] = image.GetLayer(firstLayer + i).GetOrCreatePage(pageIndex);

                    // The shader binds the layers as resource arrays: color and gray layers occupy
                    // t0..t(n-1), YUV storage puts every luma plane first and every chroma plane after it.
                    var resources = new List<ID3D11ShaderResourceView>(layerCount * 2);
                    foreach (TileArrayPage page in pages)
                        if (page.Color != null) resources.Add(page.Color.ShaderResourceView);
                    foreach (TileArrayPage page in pages)
                        if (page.Y != null) resources.Add(page.Y.ShaderResourceView);
                    foreach (TileArrayPage page in pages)
                        if (page.Uv != null) resources.Add(page.Uv.ShaderResourceView);

                    float[] values = CreateConstants(inverse, image.Info, pageIndex * LayerStore.MaxArraySlices, pages[0].ArraySize);
                    image.Graphics.Context.UpdateSubresource(values, _constants, 0, 0, 0, null);
                    image.Graphics.Context.PSSetShaderResources(0, resources.ToArray());
                    image.Graphics.Context.Draw(3, 0);
                    image.Graphics.Context.PSSetShaderResources(0, new ID3D11ShaderResourceView[resources.Count]);
                }
                image.Graphics.Context.CopyResource(staging, render);
                image.Graphics.Context.Flush();
                Readback(image.Graphics.Context, staging, output, width, height, stride, format);
            }
            finally
            {
                staging.Dispose();
                target.Dispose();
                render.Dispose();
            }
    }

    public void Dispose()
    {
        _constants.Dispose();
        _sampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
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
        // A plain load binds one layer and passes it through, a compose binds every layer of the image.
        yield return new KeyValuePair<string, string>("LAYER_COUNT", (compose == null ? 1 : image.LayerNames.Count).ToString(CultureInfo.InvariantCulture));
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

    private static float[] CreateConstants(TransformMatrix inverse, GrotheImageInfo info, long pageBase, int pageSize)
    {
        return new[]
        {
            (float)inverse.M00, (float)inverse.M01, (float)inverse.M02, 0f,
            (float)inverse.M10, (float)inverse.M11, (float)inverse.M12, 0f,
            (float)inverse.M20, (float)inverse.M21, (float)inverse.M22, 0f,
            (float)info.StepX, (float)info.StepY, info.TileColumns, info.TileRows,
            (float)info.TileWidth, (float)info.TileHeight, info.TileOverlapX, info.TileOverlapY,
            info.Width, info.Height, pageBase, pageSize,
        };
    }

    private static IEnumerable<long> GetRequiredPages(GrotheImageInfo info, TransformMatrix inverse, int outputWidth, int outputHeight)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        foreach (var point in new[]
        {
            inverse.TransformPoint(0, 0),
            inverse.TransformPoint(outputWidth, 0),
            inverse.TransformPoint(0, outputHeight),
            inverse.TransformPoint(outputWidth, outputHeight),
        })
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        long firstColumn = TileGrid.Clamp((long)Math.Floor(minX / info.StepX) - 1, 0, info.TileColumns - 1);
        long lastColumn = TileGrid.Clamp((long)Math.Floor(maxX / info.StepX) + 1, 0, info.TileColumns - 1);
        long firstRow = TileGrid.Clamp((long)Math.Floor(minY / info.StepY) - 1, 0, info.TileRows - 1);
        long lastRow = TileGrid.Clamp((long)Math.Floor(maxY / info.StepY) + 1, 0, info.TileRows - 1);
        var pages = new HashSet<long>();
        for (long row = firstRow; row <= lastRow; row++)
        for (long column = firstColumn; column <= lastColumn; column++)
            pages.Add(TileGrid.GetLinearIndex(info, row, column) / LayerStore.MaxArraySlices);
        return pages;
    }

    private static DxgiFormat GetRenderFormat(PixelFormat format)
    {
        switch (format)
        {
            case PixelFormat.Gray8: return DxgiFormat.R8_UNorm;
            case PixelFormat.Bgra32: return DxgiFormat.B8G8R8A8_UNorm;
            default: return DxgiFormat.R8G8B8A8_UNorm;
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
