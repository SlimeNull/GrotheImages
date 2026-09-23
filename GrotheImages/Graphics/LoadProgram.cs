using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

namespace GrotheImages;

internal sealed class LoadProgram : IDisposable
{
    private readonly GrotheImage _image;
    private readonly LayerCompose _compose;
    private readonly PixelFormat _outputFormat;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _constants;

    public LoadProgram(GrotheImage image, LayerCompose compose, PixelFormat outputFormat)
    {
        _image = image;
        _compose = compose;
        _outputFormat = outputFormat;
        if (compose != null) ValidateComposeOutput(compose, outputFormat);

        string source = BuildShaderSource(image, compose, outputFormat);
        using (Blob vsBlob = ShaderCompiler.Compile(source, "VSMain", "vs_5_0"))
        using (Blob psBlob = ShaderCompiler.Compile(source, "PSMain", "ps_5_0"))
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

    public void Execute(GrotheImage image, int layerIndex, nint output, int width, int height, int stride, TransformMatrix inverse)
    {
        DxgiFormat renderFormat = GetRenderFormat(_outputFormat);
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
                    var resources = new List<ID3D11ShaderResourceView>();
                    int layerCount = _compose == null ? 1 : image.LayerNames.Count;
                    for (int i = 0; i < layerCount; i++)
                    {
                        LayerStore layer = image.GetLayer(_compose == null ? layerIndex : i);
                        TileArrayPage page = layer.GetOrCreatePage(pageIndex);
                        if (page.Color != null) resources.Add(page.Color.ShaderResourceView);
                        if (page.Y != null) resources.Add(page.Y.ShaderResourceView);
                        if (page.Uv != null) resources.Add(page.Uv.ShaderResourceView);
                    }
                    TileArrayPage firstPage = image.GetLayer(_compose == null ? layerIndex : 0).GetOrCreatePage(pageIndex);
                    float[] values = CreateConstants(inverse, image.Info, pageIndex * LayerStore.MaxArraySlices, firstPage.ArraySize);
                    image.Graphics.Context.UpdateSubresource(values, _constants, 0, 0, 0, null);
                    image.Graphics.Context.PSSetShaderResources(0, resources.ToArray());
                    image.Graphics.Context.Draw(3, 0);
                    image.Graphics.Context.PSSetShaderResources(0, new ID3D11ShaderResourceView[resources.Count]);
                }
                image.Graphics.Context.CopyResource(staging, render);
                image.Graphics.Context.Flush();
                Readback(image.Graphics.Context, staging, output, width, height, stride, _outputFormat);
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

    private static void ValidateComposeOutput(LayerCompose compose, PixelFormat format)
    {
        int expected = format == PixelFormat.Gray8 ? 1 : (format == PixelFormat.Bgra32 || format == PixelFormat.Rgba32 ? 4 : 3);
        if (compose.OutputChannelCount != expected)
            throw new ArgumentException("The compose expression channel count does not match the requested output format.", nameof(compose));
    }

    private static string BuildShaderSource(GrotheImage image, LayerCompose compose, PixelFormat outputFormat)
    {
        int layerCount = compose == null ? 1 : image.LayerNames.Count;
        bool yuv = image.Format == PixelFormat.Yuv444 || image.Format == PixelFormat.Yuv422 || image.Format == PixelFormat.Yuv420;
        bool gray = image.Format == PixelFormat.Gray8;
        var b = new StringBuilder();
        for (int i = 0; i < layerCount; i++)
        {
            if (yuv)
            {
                b.AppendLine("Texture2DArray<float> Y" + i + " : register(t" + (i * 2) + ");");
                b.AppendLine("Texture2DArray<float2> Uv" + i + " : register(t" + (i * 2 + 1) + ");");
            }
            else if (gray)
                b.AppendLine("Texture2DArray<float> Tiles" + i + " : register(t" + i + ");");
            else
                b.AppendLine("Texture2DArray<float4> Tiles" + i + " : register(t" + i + ");");
        }
        b.AppendLine("SamplerState LinearSampler : register(s0);");
        b.AppendLine("cbuffer Params : register(b0) { float4 M0; float4 M1; float4 M2; float4 Grid; float4 Tile; float4 Image; };");
        b.AppendLine("struct VSOut { float4 Position : SV_Position; };");
        b.AppendLine("VSOut VSMain(uint id : SV_VertexID) { VSOut o; float2 p = float2((id << 1) & 2, id & 2); o.Position = float4(p * float2(2, -2) + float2(-1, 1), 0, 1); return o; }");
        b.AppendLine("float2 Source(float2 p) { float3 q = float3(dot(M0.xyz, float3(p,1)), dot(M1.xyz, float3(p,1)), dot(M2.xyz, float3(p,1))); return q.xy / q.z; }");
        for (int i = 0; i < layerCount; i++)
        {
            b.Append("float4 ReadLayer").Append(i).AppendLine("(float2 uv, int slice) {");
            if (yuv)
                b.Append("float y = Y").Append(i).Append(".SampleLevel(LinearSampler, float3(uv, slice), 0); float2 uvv = Uv").Append(i).AppendLine(".SampleLevel(LinearSampler, float3(uv, slice), 0); return float4(y, uvv, 1);");
            else if (gray)
                b.Append("float v = Tiles").Append(i).AppendLine(".SampleLevel(LinearSampler, float3(uv, slice), 0); return float4(v,v,v,1);");
            else
                b.Append("return Tiles").Append(i).AppendLine(".SampleLevel(LinearSampler, float3(uv, slice), 0);");
            b.AppendLine("}");
        }
        b.AppendLine("float4 ToOutput(float4 value) {");
        if (yuv && outputFormat != PixelFormat.Yuv444)
            b.AppendLine("float yy = (value.x - 0.0625) * 1.1643836; float uu = value.y - 0.5; float vv = value.z - 0.5; return float4(yy + 1.7927415*vv, yy - 0.2132486*uu - 0.5329093*vv, yy + 2.1124018*uu, 1);");
        else if (!yuv && outputFormat == PixelFormat.Yuv444)
            b.AppendLine("float yy = dot(value.rgb, float3(0.2126,0.7152,0.0722)); return float4(yy, (value.b-yy)*0.5389+0.5, (value.r-yy)*0.6350+0.5, 1);");
        else
            b.AppendLine("return value;");
        b.AppendLine("}");
        b.AppendLine("float4 PSMain(VSOut input) : SV_Target {");
        b.AppendLine("float2 source = Source(input.Position.xy); if (source.x < 0 || source.y < 0 || source.x >= Image.x || source.y >= Image.y) return float4(0,0,0,0);");
        b.AppendLine("int column = clamp((int)floor((source.x - 0.5 * Tile.z) / Grid.x), 0, (int)Grid.z - 1); int row = clamp((int)floor((source.y - 0.5 * Tile.w) / Grid.y), 0, (int)Grid.w - 1); int globalSlice = row * (int)Grid.z + column; if (globalSlice < (int)Image.z || globalSlice >= (int)(Image.z + Image.w)) discard; int slice = globalSlice - (int)Image.z; float2 local = source - float2(column,row) * Grid.xy; float2 uv = local / Tile.xy;");
        if (compose == null)
            b.AppendLine("float4 value = ReadLayer0(uv, slice);");
        else
        {
            for (int i = 0; i < layerCount; i++) b.Append("float4 Layer").Append(i).AppendLine(" = ReadLayer" + i + "(uv, slice);");
            string expression = compose.ToHlsl();
            if (compose.OutputChannelCount == 1) expression = expression + "," + expression + "," + expression + ",1";
            else if (compose.OutputChannelCount == 3) expression += ",1";
            b.AppendLine("float4 value = float4(" + expression + ");");
        }
        b.AppendLine("return ToOutput(value);");
        b.AppendLine("}");
        return b.ToString();
    }

    private static void Readback(ID3D11DeviceContext context, ID3D11Texture2D staging, nint destination, int width, int height, int stride, PixelFormat format)
    {
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            int sourceBytes = format == PixelFormat.Gray8 ? width : width * 4;
            byte[] row = new byte[sourceBytes];
            byte[] output = new byte[format == PixelFormat.Gray8 ? width : width * PixelFormatRules.BytesPerPixel(format)];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(mapped.DataPointer + y * mapped.RowPitch, row, 0, row.Length);
                if (format == PixelFormat.Bgr24 || format == PixelFormat.Rgb24)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int si = x * 4;
                        int di = x * 3;
                        if (format == PixelFormat.Bgr24) { output[di] = row[si + 2]; output[di + 1] = row[si + 1]; output[di + 2] = row[si]; }
                        else { output[di] = row[si]; output[di + 1] = row[si + 1]; output[di + 2] = row[si + 2]; }
                    }
                }
                else if (format == PixelFormat.Yuv444)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int si = x * 4;
                        int di = x * 3;
                        output[di] = row[si]; output[di + 1] = row[si + 1]; output[di + 2] = row[si + 2];
                    }
                }
                else Buffer.BlockCopy(row, 0, output, 0, output.Length);
                Marshal.Copy(output, 0, destination + y * stride, output.Length);
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
