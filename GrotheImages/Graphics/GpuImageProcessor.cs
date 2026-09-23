using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;

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
            UpdateResource(image.Graphics, page.Y.Texture, subresource, scan0, stride, image.Info.TileHeight);
            UpdateResource(image.Graphics, page.Uv.Texture, subresource, secondScan0, secondStride,
                format == PixelFormat.Yuv420 ? image.Info.TileHeight / 2 : image.Info.TileHeight);
            store.MarkWritten(tileRow, tileColumn);
            return;
        }

        switch (format)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
            case PixelFormat.Gray8:
                UpdateResource(image.Graphics, page.Color.Texture, subresource, scan0, stride, height);
                store.MarkWritten(tileRow, tileColumn);
                return;
            case PixelFormat.Bgr24:
            case PixelFormat.Rgb24:
                UpdatePacked24(image.Graphics, page.Color.Texture, subresource, scan0, width, height, stride, format);
                store.MarkWritten(tileRow, tileColumn);
                return;
            case PixelFormat.Yuv444:
                UpdateYuv444(image.Graphics, page, subresource, scan0, width, height, stride);
                store.MarkWritten(tileRow, tileColumn);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static void UpdateResource(D3D11DeviceContext graphics, ID3D11Resource resource, int subresource, nint data, int rowPitch, int height)
    {
        graphics.Context.UpdateSubresource(resource, subresource, null, data, rowPitch, 0);
    }

    private static void UpdatePacked24(D3D11DeviceContext graphics, ID3D11Resource resource, int subresource, nint source, int width, int height, int stride, PixelFormat format)
    {
        byte[] packed = new byte[checked(width * height * 4)];
        byte[] row = new byte[checked(width * 3)];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(source + y * stride, row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                int si = x * 3;
                int di = (y * width + x) * 4;
                byte c0 = row[si];
                byte c1 = row[si + 1];
                byte c2 = row[si + 2];
                if (format == PixelFormat.Bgr24)
                {
                    packed[di] = c2;
                    packed[di + 1] = c1;
                    packed[di + 2] = c0;
                }
                else
                {
                    packed[di] = c0;
                    packed[di + 1] = c1;
                    packed[di + 2] = c2;
                }
                packed[di + 3] = 255;
            }
        }
        UpdatePinned(graphics, resource, subresource, packed, width * 4);
    }

    private static void UpdateYuv444(D3D11DeviceContext graphics, TileArrayPage page, int subresource, nint source, int width, int height, int stride)
    {
        byte[] y = new byte[checked(width * height)];
        byte[] uv = new byte[checked(width * height * 2)];
        byte[] row = new byte[checked(width * 3)];
        for (int rowIndex = 0; rowIndex < height; rowIndex++)
        {
            Marshal.Copy(source + rowIndex * stride, row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                int si = x * 3;
                int yi = rowIndex * width + x;
                y[yi] = row[si];
                uv[yi * 2] = row[si + 1];
                uv[yi * 2 + 1] = row[si + 2];
            }
        }
        UpdatePinned(graphics, page.Y.Texture, subresource, y, width);
        UpdatePinned(graphics, page.Uv.Texture, subresource, uv, width * 2);
    }

    private static void UpdatePinned(D3D11DeviceContext graphics, ID3D11Resource resource, int subresource, byte[] data, int rowPitch)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            graphics.Context.UpdateSubresource(resource, subresource, null, handle.AddrOfPinnedObject(), rowPitch, 0);
        }
        finally
        {
            handle.Free();
        }
    }
}
