using System;
using Vortice.Direct3D11;

namespace GrotheImages;

internal static class GpuImageProcessor
{
    public static void Update(GrotheImage image, int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transform)
    {
        using (var program = new UpdateProgram(image, scan0, width, height, stride, format, transform))
            program.Execute(layerIndex, transform, width, height);
    }

    public static void Load(GrotheImage image, int layerIndex, LayerCompose compose, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transform)
    {
        if (!transform.TryInvert(out var inverse))
            throw new ArgumentException("The transform matrix is not invertible.", nameof(transform));
        using (var program = new LoadProgram(image, compose, format))
            program.Execute(image, layerIndex, scan0, width, height, stride, inverse);
    }

    public static void UpdateTile(GrotheImage image, int layerIndex, long tileRow, long tileColumn, nint scan0, int width, int height, int stride, PixelFormat format, nint secondScan0, int secondWidth, int secondStride, PixelFormat secondFormat)
    {
        LayerStore store = image.GetLayer(layerIndex);
        TileArrayPage page = store.GetPageForTile(tileRow, tileColumn, out int slice);
        int subresource = slice;

        if (format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420)
        {
            if (format == image.Format)
            {
                UpdateResource(image.Graphics, page.Y.Texture, subresource, scan0, stride, image.Info.TileHeight);
                UpdateResource(image.Graphics, page.Uv.Texture, subresource, secondScan0, secondStride,
                    format == PixelFormat.Yuv420 ? image.Info.TileHeight / 2 : image.Info.TileHeight);
                store.MarkWritten(tileRow, tileColumn);
                return;
            }

            using (var program = new UpdateProgram(image, scan0, stride, secondScan0, secondStride, width, height, format))
                program.ExecuteTile(layerIndex, tileRow, tileColumn);
            return;
        }

        if (format == image.Format && (format == PixelFormat.Bgra32 || format == PixelFormat.Rgba32 || format == PixelFormat.Gray8))
        {
            UpdateResource(image.Graphics, page.Color.Texture, subresource, scan0, stride, height);
            store.MarkWritten(tileRow, tileColumn);
            return;
        }

        using (var program = new UpdateProgram(image, scan0, width, height, stride, format, TransformMatrix.Identity))
            program.ExecuteTile(layerIndex, tileRow, tileColumn);
    }

    private static void UpdateResource(D3D11DeviceContext graphics, ID3D11Resource resource, int subresource, nint data, int rowPitch, int height)
    {
        graphics.Context.UpdateSubresource(resource, subresource, null, data, rowPitch, 0);
    }

}
