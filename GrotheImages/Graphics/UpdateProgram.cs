using System;
using System.Collections.Generic;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

namespace GrotheImages;

internal sealed class UpdateProgram : IDisposable
{
    private ID3D11ComputeShader _shader;
    private ID3D11SamplerState _sampler;
    private ID3D11Buffer _constants;

    private GrotheImage _image;
    private PixelFormat _targetFormat;

    public UpdateProgram()
    {
        _shader = null;
        _sampler = null;
        _constants = null;
    }

    public void Execute(GrotheImage image, int layerIndex, nint source, int width, int height, int stride, PixelFormat sourceFormat, TransformMatrix transform)
    {
        _image = image;
        EnsureResources();
        using (ID3D11Texture2D texture = CreateSource(source, width, height, stride, sourceFormat))
        using (ID3D11ShaderResourceView view = _image.Graphics.Device.CreateShaderResourceView(texture, null))
        {
            LayerStore store = _image.GetLayer(layerIndex);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var point in new[]
            {
                transform.TransformPoint(0, 0), transform.TransformPoint(width, 0),
                transform.TransformPoint(0, height), transform.TransformPoint(width, height)
            })
            {
                minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
            }
            GrotheImageInfo info = _image.Info;
            long firstColumn = TileGrid.Clamp((long)Math.Floor(minX / info.StepX) - 1, 0, info.TileColumns - 1);
            long lastColumn = TileGrid.Clamp((long)Math.Floor(maxX / info.StepX) + 1, 0, info.TileColumns - 1);
            long firstRow = TileGrid.Clamp((long)Math.Floor(minY / info.StepY) - 1, 0, info.TileRows - 1);
            long lastRow = TileGrid.Clamp((long)Math.Floor(maxY / info.StepY) + 1, 0, info.TileRows - 1);
            for (long row = firstRow; row <= lastRow; row++)
            for (long column = firstColumn; column <= lastColumn; column++)
                DispatchTile(store, view, row, column, width, height, transform);
            _image.Graphics.Context.Flush();
        }
    }

    public void ExecuteTile(GrotheImage image, int layerIndex, long row, long column, nint source, int width, int height, int stride, PixelFormat sourceFormat)
    {
        _image = image;
        EnsureResources();
        using (ID3D11Texture2D texture = CreateSource(source, width, height, stride, sourceFormat))
        using (ID3D11ShaderResourceView view = _image.Graphics.Device.CreateShaderResourceView(texture, null))
        {
            var transform = new TransformMatrix(
                1, 0, -column * (double)_image.Info.StepX,
                0, 1, -row * (double)_image.Info.StepY, 0, 0, 1);
            DispatchTile(_image.GetLayer(layerIndex), view, row, column, width, height, transform);
            _image.Graphics.Context.Flush();
        }
    }

    private ID3D11Texture2D CreateSource(nint source, int width, int height, int stride, PixelFormat sourceFormat)
    {
        DxgiFormat format = sourceFormat == PixelFormat.Gray8 ? DxgiFormat.R8_UNorm
            : sourceFormat == PixelFormat.Bgra32 ? DxgiFormat.B8G8R8A8_UNorm : DxgiFormat.R8G8B8A8_UNorm;
        ID3D11Texture2D texture = _image.Graphics.Device.CreateTexture2D(
            format, width, height, 1, 1, null, BindFlags.ShaderResource,
            ResourceOptionFlags.None, ResourceUsage.Default, CpuAccessFlags.None);
        try
        {
            _image.Graphics.Context.UpdateSubresource(texture, 0, null, source, stride, 0);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private void EnsureResources()
    {
        if (_shader != null) return;
        _targetFormat = _image.Format;
        var include = new ShaderInclude(new[]
        {
            new KeyValuePair<string, string>("macros", ShaderSource.Macros(BuildMacros(_targetFormat))),
        });
        using (var blob = ShaderCompiler.Compile(ShaderSource.Load("Update.hlsl"), "Update.hlsl", "CSMain", "cs_5_0", include))
            _shader = _image.Graphics.Device.CreateComputeShader(blob, null);
        _sampler = _image.Graphics.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        _constants = _image.Graphics.Device.CreateBuffer(
            new float[24], BindFlags.ConstantBuffer, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0, 0);
    }

    /// <summary>The generated <c>macros</c> include: which storage planes the compute shader writes.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> BuildMacros(PixelFormat targetFormat)
    {
        if (PixelFormatRules.IsYuv(targetFormat)) yield return new KeyValuePair<string, string>("STORAGE_YUV", null);
        else if (targetFormat == PixelFormat.Gray8) yield return new KeyValuePair<string, string>("STORAGE_GRAY", null);
        else yield return new KeyValuePair<string, string>("STORAGE_RGBA", null);
    }

    private void DispatchTile(LayerStore store, ID3D11ShaderResourceView view, long row, long column,
        int sourceWidth, int sourceHeight, TransformMatrix transform)
    {
        TileArrayPage page = store.GetPageForTile(row, column, out int slice);
        GrotheImageInfo info = _image.Info;
        float[] values =
        {
            (float)transform.M00, (float)transform.M01, (float)transform.M02, 0,
            (float)transform.M10, (float)transform.M11, (float)transform.M12, 0,
            (float)transform.M20, (float)transform.M21, (float)transform.M22, 0,
            (float)info.StepX, (float)info.StepY, info.TileColumns, info.TileRows,
            info.TileWidth, info.TileHeight, column * (float)info.StepX, row * (float)info.StepY,
            sourceWidth, sourceHeight, slice,
            _image.Format == PixelFormat.Yuv420 ? 2f : _image.Format == PixelFormat.Yuv422 ? 1f : 0f
        };
        var context = _image.Graphics.Context;
        context.UpdateSubresource(values, _constants, 0, 0, 0, null);
        context.CSSetShader(_shader);
        context.CSSetShaderResources(0, new[] { view });
        context.CSSetSamplers(0, new[] { _sampler });
        context.CSSetConstantBuffers(0, new[] { _constants });
        if (page.Color != null)
            context.CSSetUnorderedAccessViews(0, new[] { page.Color.UnorderedAccessView });
        else
            context.CSSetUnorderedAccessViews(0, new[] { page.Y.UnorderedAccessView, page.Uv.UnorderedAccessView });
        try
        {
            context.Dispatch((info.TileWidth + 7) / 8, (info.TileHeight + 7) / 8, 1);
            store.MarkWritten(row, column);
        }
        finally
        {
            context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null, null });
            context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null });
        }
    }

    public void Dispose()
    {
        _constants?.Dispose();
        _sampler?.Dispose();
        _shader?.Dispose();
        _constants = null;
        _sampler = null;
        _shader = null;
        _image = null;
    }
}
