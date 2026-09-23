using System;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

namespace GrotheImages;

internal sealed class UpdateProgram : IDisposable
{
    private readonly GrotheImage _image;
    private readonly PixelFormat _sourceFormat;
    private readonly PixelFormat _targetFormat;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11ShaderResourceView _sourceView;
    private readonly ID3D11Resource _sourceResource;
    private readonly byte[] _sourceBytes;
    private readonly GCHandle _sourceHandle;
    private readonly int _sourceRowPitch;

    public UpdateProgram(GrotheImage image, nint source, int width, int height, int stride, PixelFormat sourceFormat, TransformMatrix transform)
    {
        _image = image;
        _sourceFormat = sourceFormat;
        _targetFormat = image.Format;
        _sourceBytes = PrepareSource(source, width, height, stride, sourceFormat, out DxgiFormat dxgiFormat, out _sourceRowPitch);
        _sourceHandle = GCHandle.Alloc(_sourceBytes, GCHandleType.Pinned);
        _sourceResource = image.Graphics.Device.CreateTexture2D(
            dxgiFormat, width, height, 1, 1, null,
            BindFlags.ShaderResource,
            ResourceOptionFlags.None,
            ResourceUsage.Default,
            CpuAccessFlags.None);
        image.Graphics.Context.UpdateSubresource(_sourceResource, 0, null, _sourceHandle.AddrOfPinnedObject(), _sourceRowPitch, 0);
        _sourceView = image.Graphics.Device.CreateShaderResourceView(_sourceResource, null);

        string shaderSource = BuildShaderSource(sourceFormat, image.Format);
        using (var blob = ShaderCompiler.Compile(shaderSource, "CSMain", "cs_5_0"))
            _shader = image.Graphics.Device.CreateComputeShader(blob, null);
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
            new float[24],
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
    }

    public void Execute(int layerIndex, TransformMatrix transform, int sourceWidth, int sourceHeight)
    {
        LayerStore store = _image.GetLayer(layerIndex);
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        foreach (var point in new[]
        {
            transform.TransformPoint(0, 0),
            transform.TransformPoint(sourceWidth, 0),
            transform.TransformPoint(0, sourceHeight),
            transform.TransformPoint(sourceWidth, sourceHeight),
        })
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        long firstColumn = TileGrid.Clamp((long)Math.Floor(minX / _image.Info.StepX) - 1, 0, _image.Info.TileColumns - 1);
        long lastColumn = TileGrid.Clamp((long)Math.Floor(maxX / _image.Info.StepX) + 1, 0, _image.Info.TileColumns - 1);
        long firstRow = TileGrid.Clamp((long)Math.Floor(minY / _image.Info.StepY) - 1, 0, _image.Info.TileRows - 1);
        long lastRow = TileGrid.Clamp((long)Math.Floor(maxY / _image.Info.StepY) + 1, 0, _image.Info.TileRows - 1);
        for (long row = firstRow; row <= lastRow; row++)
        for (long column = firstColumn; column <= lastColumn; column++)
        {
            TileArrayPage page = store.GetPageForTile(row, column, out int slice);
            float[] values = CreateConstants(transform, sourceWidth, sourceHeight, row, column, slice);
            _image.Graphics.Context.UpdateSubresource(values, _constants, 0, 0, 0, null);
            _image.Graphics.Context.CSSetShader(_shader);
            _image.Graphics.Context.CSSetShaderResources(0, new[] { _sourceView });
            _image.Graphics.Context.CSSetSamplers(0, new[] { _sampler });
            _image.Graphics.Context.CSSetConstantBuffers(0, new[] { _constants });
            if (_targetFormat == PixelFormat.Yuv444 || _targetFormat == PixelFormat.Yuv422 || _targetFormat == PixelFormat.Yuv420)
                _image.Graphics.Context.CSSetUnorderedAccessViews(0, new[] { page.Y.UnorderedAccessView, page.Uv.UnorderedAccessView });
            else
                _image.Graphics.Context.CSSetUnorderedAccessViews(0, new[] { page.Color.UnorderedAccessView });
            _image.Graphics.Context.Dispatch((_image.Info.TileWidth + 7) / 8, (_image.Info.TileHeight + 7) / 8, 1);
            _image.Graphics.Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null, null });
            _image.Graphics.Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null });
        }
        _image.Graphics.Context.Flush();
    }

    public void Dispose()
    {
        _constants.Dispose();
        _sampler.Dispose();
        _shader.Dispose();
        _sourceView.Dispose();
        _sourceResource.Dispose();
        _sourceHandle.Free();
    }

    private float[] CreateConstants(TransformMatrix transform, int sourceWidth, int sourceHeight, long row, long column, int slice)
    {
        return new[]
        {
            (float)transform.M00, (float)transform.M01, (float)transform.M02, 0f,
            (float)transform.M10, (float)transform.M11, (float)transform.M12, 0f,
            (float)transform.M20, (float)transform.M21, (float)transform.M22, 0f,
            (float)_image.Info.StepX, (float)_image.Info.StepY, _image.Info.TileColumns, _image.Info.TileRows,
            (float)_image.Info.TileWidth, (float)_image.Info.TileHeight, column * (float)_image.Info.StepX, row * (float)_image.Info.StepY,
            sourceWidth, sourceHeight, slice, _targetFormat == PixelFormat.Yuv420 ? 2f : (_targetFormat == PixelFormat.Yuv422 ? 1f : 0f),
        };
    }

    private static byte[] PrepareSource(nint source, int width, int height, int stride, PixelFormat format, out DxgiFormat dxgiFormat, out int rowPitch)
    {
        bool gray = format == PixelFormat.Gray8;
        dxgiFormat = gray ? DxgiFormat.R8_UNorm : (format == PixelFormat.Bgra32 ? DxgiFormat.B8G8R8A8_UNorm : DxgiFormat.R8G8B8A8_UNorm);
        rowPitch = gray ? width : width * 4;
        byte[] result = new byte[checked(rowPitch * height)];
        byte[] row = new byte[checked(width * PixelFormatRules.BytesPerPixel(format))];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(source + y * stride, row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                if (gray)
                {
                    result[y * rowPitch + x] = row[x];
                    continue;
                }
                int si = x * PixelFormatRules.BytesPerPixel(format);
                int di = y * rowPitch + x * 4;
                if (format == PixelFormat.Yuv444)
                {
                    result[di] = row[si]; result[di + 1] = row[si + 1]; result[di + 2] = row[si + 2];
                }
                else if (format == PixelFormat.Bgr24)
                {
                    result[di] = row[si + 2]; result[di + 1] = row[si + 1]; result[di + 2] = row[si];
                }
                else if (format == PixelFormat.Rgb24)
                {
                    result[di] = row[si]; result[di + 1] = row[si + 1]; result[di + 2] = row[si + 2];
                }
                else
                {
                    result[di] = row[si]; result[di + 1] = row[si + 1]; result[di + 2] = row[si + 2]; result[di + 3] = row[si + 3];
                }
                if (format != PixelFormat.Bgra32 && format != PixelFormat.Rgba32) result[di + 3] = 255;
            }
        }
        return result;
    }

    private static string BuildShaderSource(PixelFormat sourceFormat, PixelFormat targetFormat)
    {
        bool sourceGray = sourceFormat == PixelFormat.Gray8;
        bool sourceYuv = sourceFormat == PixelFormat.Yuv444;
        bool targetYuv = targetFormat == PixelFormat.Yuv444 || targetFormat == PixelFormat.Yuv422 || targetFormat == PixelFormat.Yuv420;
        bool targetGray = targetFormat == PixelFormat.Gray8;
        var b = new StringBuilder();
        b.AppendLine(sourceGray ? "Texture2D<float> Source : register(t0);" : "Texture2D<float4> Source : register(t0);");
        b.AppendLine("SamplerState LinearSampler : register(s0);");
        if (targetYuv)
            b.AppendLine("RWTexture2DArray<float> YTarget : register(u0); RWTexture2DArray<float2> UvTarget : register(u1);");
        else if (targetGray)
            b.AppendLine("RWTexture2DArray<float> Target : register(u0);");
        else
            b.AppendLine("RWTexture2DArray<float4> Target : register(u0);");
        b.AppendLine("cbuffer Params : register(b0) { float4 M0; float4 M1; float4 M2; float4 Grid; float4 Tile; float4 SourceSize; };");
        b.AppendLine("float4 ReadSource(float2 uv) {");
        if (sourceGray) b.AppendLine("float v = Source.SampleLevel(LinearSampler, uv, 0); return float4(v,v,v,1);");
        else if (sourceYuv) b.AppendLine("float4 v = Source.SampleLevel(LinearSampler, uv, 0); return float4(v.xyz, 1);");
        else b.AppendLine("return Source.SampleLevel(LinearSampler, uv, 0);");
        b.AppendLine("}");
        if (targetYuv)
            b.AppendLine("float3 ToYuv(float3 rgb) { float y=dot(rgb,float3(0.2126,0.7152,0.0722)); return float3(y, (rgb.b-y)*0.5389+0.5, (rgb.r-y)*0.6350+0.5); }");
        b.AppendLine("[numthreads(8,8,1)] void CSMain(uint3 id : SV_DispatchThreadID) {");
        b.AppendLine("if (id.x >= Tile.x || id.y >= Tile.y) return; float2 global=float2(Tile.z+id.x+0.5, Tile.w+id.y+0.5); float3 q=float3(dot(M0.xyz,float3(global,1)),dot(M1.xyz,float3(global,1)),dot(M2.xyz,float3(global,1))); float2 source=q.xy/q.z; if(source.x<0||source.y<0||source.x>=SourceSize.x||source.y>=SourceSize.y)return; float4 value=ReadSource(source/SourceSize.xy);");
        if (sourceYuv && !targetYuv)
            b.AppendLine("float yy=(value.x-0.0625)*1.1643836; float uu=value.y-0.5; float vv=value.z-0.5; value=float4(yy+1.7927415*vv, yy-0.2132486*uu-0.5329093*vv, yy+2.1124018*uu, 1);");
        if (targetYuv)
        {
            b.AppendLine(sourceYuv ? "float3 yuv=value.rgb;" : "float3 yuv=ToYuv(value.rgb);");
            b.AppendLine("YTarget[uint3(id.xy, (uint)SourceSize.z)] = yuv.x;");
            b.AppendLine("if ((SourceSize.w < 0.5 || (id.x & 1)==0) && (SourceSize.w < 1.5 || (id.y & 1)==0)) UvTarget[uint3(SourceSize.w < 0.5 ? id.x : id.x/2, SourceSize.w < 1.5 ? id.y : id.y/2, (uint)SourceSize.z)] = yuv.yz;");
        }
        else if (targetGray) b.AppendLine("Target[uint3(id.xy, (uint)SourceSize.z)] = value.r;");
        else b.AppendLine("Target[uint3(id.xy, (uint)SourceSize.z)] = value;");
        b.AppendLine("}");
        return b.ToString();
    }
}
