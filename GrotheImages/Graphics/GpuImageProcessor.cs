using System;

namespace GrotheImages;

internal static class GpuImageProcessor
{
    public static void Update(GrotheImage image, int layerIndex, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transform)
    {
        image.GetUpdateProgram().Execute(image, layerIndex, scan0, width, height, stride, format, transform);
    }

    public static void Load(GrotheImage image, int layerIndex, LayerCompose compose, nint scan0, int width, int height, int stride, PixelFormat format, TransformMatrix transform)
    {
        if (!transform.TryInvert(out var inverse))
            throw new ArgumentException("The transform matrix is not invertible.", nameof(transform));
        (compose == null ? image.GetLoadProgram() : compose.GetLoadProgram())
            .Execute(layerIndex, scan0, width, height, stride, format, inverse);
    }

    public static void BlendSeams(GrotheImage image)
    {
        image.GetBlendProgram().Execute();
    }

    public static void UpdateTile(GrotheImage image, int layerIndex, long tileRow, long tileColumn, nint scan0, int width, int height, int stride, PixelFormat format)
    {
        if (format == image.Format && (format == PixelFormat.Bgra32 || format == PixelFormat.Rgba32 || format == PixelFormat.Gray8))
        {
            LayerStore store = image.GetLayer(layerIndex);
            TileArrayPage page = store.GetPageForTile(tileRow, tileColumn, out int slice);
            image.Graphics.Context.UpdateSubresource(page.Color.Texture, slice, null, scan0, stride, 0);
            store.MarkWritten(tileRow, tileColumn);
            return;
        }

        image.GetUpdateProgram().ExecuteTile(image, layerIndex, tileRow, tileColumn, scan0, width, height, stride, format);
    }

}
