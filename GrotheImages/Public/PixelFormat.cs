using System;

namespace GrotheImages;

public enum PixelFormat
{
    Bgra32,
    Rgba32,
    Gray8,
    Yuv444,
    Yuv422,
    Yuv420,
}

internal static class PixelFormatRules
{
    public static bool IsSubsampledYuv(PixelFormat format)
    {
        return format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420;
    }

    public static bool IsYuv(PixelFormat format)
    {
        return format == PixelFormat.Yuv444 || format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420;
    }

    /// <summary>Horizontal subsampling factor of the chroma plane (1 for 4:4:4 and non-YUV formats).</summary>
    public static int ChromaSubsampleX(PixelFormat format)
    {
        return format == PixelFormat.Yuv422 || format == PixelFormat.Yuv420 ? 2 : 1;
    }

    /// <summary>Vertical subsampling factor of the chroma plane (2 only for 4:2:0).</summary>
    public static int ChromaSubsampleY(PixelFormat format)
    {
        return format == PixelFormat.Yuv420 ? 2 : 1;
    }

    public static bool IsTransferFormat(PixelFormat format) =>
        format == PixelFormat.Bgra32 || format == PixelFormat.Rgba32 || format == PixelFormat.Gray8;

    public static int BytesPerPixel(PixelFormat format)
    {
        switch (format)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
                return 4;
            case PixelFormat.Gray8:
                return 1;
            default:
                throw new ArgumentException("Only Bgra32, Rgba32 and Gray8 support CPU image transfers.", nameof(format));
        }
    }

    public static void ValidateTransferFormat(PixelFormat format, string parameterName)
    {
        if (!IsTransferFormat(format))
            throw new ArgumentException("Only Bgra32, Rgba32 and Gray8 are supported as input or output image formats.", parameterName);
    }
}
