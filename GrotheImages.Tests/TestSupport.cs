using System;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace GrotheImages.Tests;

/// <summary>
/// Helpers shared by the tests that need a real Direct3D device. Everything a test does before touching the
/// device is plain argument validation, so those tests run on machines without a GPU and only the ones that
/// really render call <see cref="RequireHardware"/> or <see cref="Create"/>.
/// </summary>
internal static class TestImages
{
    /// <summary>Turns "no Direct3D hardware" into a skipped test instead of a failure.</summary>
    public static void RequireHardware()
    {
        using (GrotheImage image = Create(new GrotheImageInfo(1, 1, 1, 1), PixelFormat.Gray8))
        {
            byte[] pixel = { 0 };
            WithPinned(pixel, pointer => image.UpdateTile(0, 0, 0, pointer, 1, 1, 1, PixelFormat.Gray8));
        }
    }

    public static GrotheImage Create(GrotheImageInfo info, PixelFormat format, params string[] layers)
    {
        try
        {
            return new GrotheImage(info, format, layers.Length == 0 ? new[] { "a" } : layers);
        }
        catch (Exception ex) when (ex is GrotheImageException || ex is DllNotFoundException || ex is TypeInitializationException)
        {
            throw SkipException.ForSkip("D3D11 hardware is unavailable: " + ex.Message);
        }
    }

    public static GrotheImage Create(PixelFormat format, int width, int height, params string[] layers)
        => Create(new GrotheImageInfo(width, height, width, height), format, layers);

    public static void WithPinned(byte[] data, Action<IntPtr> action)
    {
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { action(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    /// <summary>Renders a layer with the identity transform into a fresh buffer.</summary>
    public static byte[] Load(GrotheImage image, int layer, int width, int height, PixelFormat format)
    {
        int stride = checked(width * (format == PixelFormat.Gray8 ? 1 : 4));
        IntPtr output = Marshal.AllocHGlobal(checked(stride * height));
        try
        {
            image.Load(layer, output, width, height, stride, format, TransformMatrix.Identity);
            byte[] result = new byte[stride * height];
            Marshal.Copy(output, result, 0, result.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    public static void WriteTile(GrotheImage image, int layer, long row, long column, byte value)
    {
        int width = image.Info.TileWidth;
        int height = image.Info.TileHeight;
        byte[] data = new byte[width * height];
        for (int i = 0; i < data.Length; i++) data[i] = value;
        WithPinned(data, pointer => image.UpdateTile(layer, row, column, pointer, width, height, width, PixelFormat.Gray8));
    }
}
