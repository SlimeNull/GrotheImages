using System;

namespace GrotheImages;

public enum PixelFormat
{
    Bgra32,
    Rgba32,
    Bgr24,
    Rgb24,
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

    public static bool IsTransferFormat(PixelFormat format)
    {
        return !IsSubsampledYuv(format);
    }

    public static int BytesPerPixel(PixelFormat format)
    {
        switch (format)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
                return 4;
            case PixelFormat.Bgr24:
            case PixelFormat.Rgb24:
                return 3;
            case PixelFormat.Gray8:
                return 1;
            case PixelFormat.Yuv444:
                return 3;
            default:
                throw new ArgumentException("The format is planar and has no single packed pixel size.", nameof(format));
        }
    }

    public static void ValidateTransferFormat(PixelFormat format, string parameterName)
    {
        if (!IsTransferFormat(format))
            throw new ArgumentException("Yuv422 and Yuv420 are only valid as GrotheImage storage formats.", parameterName);
    }
}
