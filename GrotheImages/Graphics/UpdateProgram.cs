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

    /// <summary>
    /// Writes one User Image into one layer of the Grothe Image. <paramref name="userToGrothe"/> maps User
    /// Image coordinates to Grothe Image coordinates, which is the direction the public API documents.
    /// </summary>
    public void Execute(GrotheImage image, int layerIndex, nint source, int width, int height, int stride, PixelFormat sourceFormat, TransformMatrix userToGrothe)
    {
        _image = image;
        EnsureResources();
        // The compute shader runs over Grothe Image pixels and has to find the User Image pixel that feeds
        // each of them, so it needs the opposite direction of the public contract. The bounding box below
        // needs the documented direction, exactly like Load needs the inverse of its own matrix.
        if (!userToGrothe.TryInvert(out TransformMatrix grotheToUser))
            throw new ArgumentException("The transform matrix must be finite and invertible.", GpuImageProcessor.TransformParameterName);

        using (ID3D11Texture2D texture = CreateSource(source, width, height, stride, sourceFormat))
        using (ID3D11ShaderResourceView view = _image.Graphics.Device.CreateShaderResourceView(texture, null))
        {
            LayerStore store = _image.GetLayer(layerIndex);
            if (!TileGrid.TryGetCoveredRegion(userToGrothe, width, height, out CoveredRegion region))
                throw new ArgumentException("The transform matrix maps a corner of the User Image to a point at infinity.", GpuImageProcessor.TransformParameterName);

            region.GetStorageTileRange(_image.Info, out long firstRow, out long lastRow, out long firstColumn, out long lastColumn);
            for (long row = firstRow; row <= lastRow; row++)
            for (long column = firstColumn; column <= lastColumn; column++)
            {
                // A tile the User Image does not reach is skipped entirely: dispatching it would write nothing
                // but would still mark it as written, and seam blending would then mix in its empty storage.
                if (!region.IntersectsTile(_image.Info, row, column)) continue;
                DispatchTile(store, view, row, column, width, height, grotheToUser);
            }
            _image.Graphics.Context.Flush();
        }
    }

    /// <summary>
    /// Writes one tile from a User Image whose pixel grid already lines up with the tile grid, so the only
    /// transform involved is the translation from Grothe Image coordinates to the tile origin.
    /// </summary>
    public void ExecuteTile(GrotheImage image, int layerIndex, long row, long column, nint source, int width, int height, int stride, PixelFormat sourceFormat)
    {
        _image = image;
        EnsureResources();
        using (ID3D11Texture2D texture = CreateSource(source, width, height, stride, sourceFormat))
        using (ID3D11ShaderResourceView view = _image.Graphics.Device.CreateShaderResourceView(texture, null))
        {
            var grotheToUser = new TransformMatrix(
                1, 0, -column * (double)_image.Info.StepX,
                0, 1, -row * (double)_image.Info.StepY, 0, 0, 1);
            DispatchTile(_image.GetLayer(layerIndex), view, row, column, width, height, grotheToUser);
            _image.Graphics.Context.Flush();
        }
    }

    private ID3D11Texture2D CreateSource(nint source, int width, int height, int stride, PixelFormat sourceFormat)
    {
        DxgiFormat format = sourceFormat == PixelFormat.Gray8 ? DxgiFormat.R8_UNorm
            : sourceFormat == PixelFormat.Bgra32 ? DxgiFormat.B8G8R8A8_UNorm
            : sourceFormat == PixelFormat.Rgba32 ? DxgiFormat.R8G8B8A8_UNorm
            : throw new ArgumentException("Only Bgra32, Rgba32 and Gray8 support CPU image transfers.", nameof(sourceFormat));
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
        PixelFormat targetFormat = _image.Format;
        var include = new ShaderInclude(new[]
        {
            new KeyValuePair<string, string>("macros", ShaderSource.Macros(BuildMacros(targetFormat))),
        });
        using (var blob = ShaderCompiler.Compile(ShaderSource.Load("Update.hlsl"), "Update.hlsl", "CSMain", "cs_5_0", include))
            _shader = _image.Graphics.Device.CreateComputeShader(blob, null);
        _sampler = _image.Graphics.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        _constants = _image.Graphics.Device.CreateBuffer(
            new float[ShaderParameters.FloatCount], BindFlags.ConstantBuffer, ResourceUsage.Default,
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
        int sourceWidth, int sourceHeight, TransformMatrix grotheToUser)
    {
        TileArrayPage page = store.GetPageForTile(row, column, out int slice);
        GrotheImageInfo info = _image.Info;
        float[] values =
        {
            (float)grotheToUser.M00, (float)grotheToUser.M01, (float)grotheToUser.M02, 0,
            (float)grotheToUser.M10, (float)grotheToUser.M11, (float)grotheToUser.M12, 0,
            (float)grotheToUser.M20, (float)grotheToUser.M21, (float)grotheToUser.M22, 0,
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
            context.CSSetUnorderedAccessViews(0, new[] { page.Color.UnorderedAccessView, null });
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
