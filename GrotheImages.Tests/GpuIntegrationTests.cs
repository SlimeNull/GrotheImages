using System;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Sdk;

namespace GrotheImages.Tests;

public sealed class GpuIntegrationTests
{
    [Theory]
    [InlineData(PixelFormat.Gray8, 1)]
    [InlineData(PixelFormat.Bgra32, 4)]
    [InlineData(PixelFormat.Rgba32, 4)]
    public void TransferFormatsRoundTripThroughGpuUpdateAndLoad(PixelFormat format, int bytesPerPixel)
    {
        const int width = 2;
        const int height = 2;
        byte[] source = new byte[width * height * bytesPerPixel];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)(i * 19 + 7);
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(format, width, height);
            image.Update(0, sourcePtr, width, height, width * bytesPerPixel, format, TransformMatrix.Identity);
            image.Load(0, outputPtr, width, height, width * bytesPerPixel, format, TransformMatrix.Identity);

            byte[] result = new byte[source.Length];
            Marshal.Copy(outputPtr, result, 0, result.Length);
            for (int i = 0; i < source.Length; i++) Assert.InRange(result[i], (byte)Math.Max(0, source[i] - 2), (byte)Math.Min(255, source[i] + 2));
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void RgbaTileUpdateAndIdentityLoadRoundTrip()
    {
        const int width = 4;
        const int height = 4;
        byte[] source = new byte[width * height * 4];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)(i * 7);
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(PixelFormat.Rgba32, width, height);
            image.UpdateTile(0, 0, 0, sourcePtr, width, height, width * 4, PixelFormat.Rgba32);
            image.Load(0, outputPtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] result = new byte[source.Length];
            Marshal.Copy(outputPtr, result, 0, result.Length);
            Assert.Equal(source, result);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void TranslationLoadUsesInverseMatrixAndTileOwnership()
    {
        const int width = 4;
        const int height = 1;
        byte[] source = { 10, 0, 0, 255, 20, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255 };
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(PixelFormat.Rgba32, width, height);
            image.UpdateTile(0, 0, 0, sourcePtr, width, height, width * 4, PixelFormat.Rgba32);
            var load = new TransformMatrix(1, 0, -1, 0, 1, 0, 0, 0, 1);
            image.Load(0, outputPtr, width, height, width * 4, PixelFormat.Rgba32, load);

            byte[] result = new byte[source.Length];
            Marshal.Copy(outputPtr, result, 0, result.Length);
            Assert.Equal(20, result[0]);
            Assert.Equal(30, result[4]);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void RgbaUpdateUsesGpuWarpPath()
    {
        const int width = 4;
        const int height = 4;
        byte[] source = new byte[width * height * 4];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)(255 - i);
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(PixelFormat.Rgba32, width, height);
            image.Update(0, sourcePtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            image.Load(0, outputPtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] result = new byte[source.Length];
            Marshal.Copy(outputPtr, result, 0, result.Length);
            Assert.Equal(source, result);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void Yuv422UsesSeparatePlanesAndRgbTransfer()
    {
        const int width = 4;
        const int height = 2;
        byte[] source = new byte[width * height * 4];
        for (int i = 0; i < source.Length; i += 4)
        {
            source[i] = 80;
            source[i + 1] = 100;
            source[i + 2] = 120;
            source[i + 3] = 255;
        }
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(PixelFormat.Yuv422, width, height);
            image.Update(0, sourcePtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            image.Load(0, outputPtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] result = new byte[source.Length];
            Marshal.Copy(outputPtr, result, 0, result.Length);
            Assert.InRange(result[0], (byte)65, (byte)95);
            Assert.InRange(result[1], (byte)85, (byte)115);
            Assert.InRange(result[2], (byte)105, (byte)135);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void Yuv420UsesHalfResolutionUvPlane()
    {
        const int width = 4;
        const int height = 2;
        byte[] source = new byte[width * height * 4];
        for (int i = 0; i < source.Length; i += 4)
        {
            source[i] = 90;
            source[i + 1] = 110;
            source[i + 2] = 130;
            source[i + 3] = 255;
        }
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(PixelFormat.Yuv420, width, height);
            image.Update(0, sourcePtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            image.Load(0, outputPtr, width, height, width * 4, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] result = new byte[source.Length];
            Marshal.Copy(outputPtr, result, 0, result.Length);
            Assert.InRange(result[0], (byte)75, (byte)105);
            Assert.InRange(result[1], (byte)95, (byte)125);
            Assert.InRange(result[2], (byte)115, (byte)145);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void PackedTileFormatConversionUsesGpuForYuv420Storage()
    {
        const int width = 4;
        const int height = 2;
        byte[] source = new byte[width * height * 4];
        for (int i = 0; i < source.Length; i += 4)
        {
            source[i] = 130;
            source[i + 1] = 120;
            source[i + 2] = 110;
            source[i + 3] = 255;
        }
        byte[] output = new byte[source.Length];
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(output.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            using var image = CreateGpuImage(PixelFormat.Yuv420, width, height);
            image.UpdateTile(0, 0, 0, sourcePtr, width, height, width * 4, PixelFormat.Bgra32);
            image.Load(0, outputPtr, width, height, width * 4, PixelFormat.Bgra32, TransformMatrix.Identity);
            Marshal.Copy(outputPtr, output, 0, output.Length);
            Assert.InRange(output[0], (byte)95, (byte)150);
            Assert.InRange(output[1], (byte)95, (byte)150);
            Assert.InRange(output[2], (byte)95, (byte)150);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void UpdateProgramIsReusedAcrossInputFormatsAndOperations()
    {
        using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1);
        IntPtr output = Marshal.AllocHGlobal(4);
        IntPtr gray = Marshal.AllocHGlobal(1);
        IntPtr rgba = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteByte(gray, 75);
            Marshal.Copy(new byte[] { 20, 30, 40, 255 }, 0, rgba, 4);
            image.Update(0, gray, 1, 1, 1, PixelFormat.Gray8, TransformMatrix.Identity);
            var program = image.GetUpdateProgram();
            image.Load(0, output, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            byte[] first = new byte[4];
            Marshal.Copy(output, first, 0, 4);
            Assert.Equal(new byte[] { 75, 0, 0, 255 }, first);

            image.UpdateTile(0, 0, 0, rgba, 1, 1, 4, PixelFormat.Rgba32);
            Assert.Same(program, image.GetUpdateProgram());
            image.Update(0, rgba, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            Assert.Same(program, image.GetUpdateProgram());
            image.Load(0, output, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            byte[] second = new byte[4];
            Marshal.Copy(output, second, 0, 4);
            Assert.Equal(new byte[] { 20, 30, 40, 255 }, second);
        }
        finally
        {
            Marshal.FreeHGlobal(output);
            Marshal.FreeHGlobal(gray);
            Marshal.FreeHGlobal(rgba);
        }
    }

    [Fact]
    public void LoadProgramIsReusedAcrossOutputFormatsAndComposeOwnsItsProgram()
    {
        using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1);
        IntPtr rgba = Marshal.AllocHGlobal(4);
        IntPtr output = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 90, 70, 50, 255 }, 0, rgba, 4);
            image.UpdateTile(0, 0, 0, rgba, 1, 1, 4, PixelFormat.Rgba32);
            image.Load(0, output, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            var program = image.GetLoadProgram();
            image.Load(0, output, 1, 1, 4, PixelFormat.Bgra32, TransformMatrix.Identity);
            Assert.Same(program, image.GetLoadProgram());

            using var compose = image.CreateLayerCompose("a.r, a.g, a.b, 1");
            image.Load(compose, output, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            var composeProgram = compose.GetLoadProgram();
            Assert.NotSame(program, composeProgram);
            image.Load(compose, output, 1, 1, 4, PixelFormat.Bgra32, TransformMatrix.Identity);
            Assert.Same(composeProgram, compose.GetLoadProgram());
            compose.Dispose();
            Assert.Throws<ArgumentException>(() => image.Load(compose, output, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity));
        }
        finally
        {
            Marshal.FreeHGlobal(rgba);
            Marshal.FreeHGlobal(output);
        }
    }

    [Fact]
    public void OverlapSamplingUsesTheRightHalfBoundaryRule()
    {
        const int tileWidth = 100;
        const int columns = 4;
        const int imageWidth = 370;
        byte[] output = new byte[imageWidth * 4];
        IntPtr outputPtr = Marshal.AllocHGlobal(output.Length);
        try
        {
            using var image = new GrotheImage(GrotheImageInfo.FromTiles(tileWidth, 1, 1, columns, 10, 0), PixelFormat.Rgba32, "a");
            for (int column = 0; column < columns; column++)
            {
                byte[] tile = new byte[tileWidth * 4];
                for (int x = 0; x < tileWidth; x++)
                {
                    tile[x * 4] = (byte)(10 + column * 20);
                    tile[x * 4 + 3] = 255;
                }
                IntPtr tilePtr = Marshal.AllocHGlobal(tile.Length);
                try
                {
                    Marshal.Copy(tile, 0, tilePtr, tile.Length);
                    image.UpdateTile(0, 0, column, tilePtr, tileWidth, 1, tileWidth * 4, PixelFormat.Rgba32);
                }
                finally
                {
                    Marshal.FreeHGlobal(tilePtr);
                }
            }

            image.Load(0, outputPtr, imageWidth, 1, imageWidth * 4, PixelFormat.Rgba32, TransformMatrix.Identity);
            Marshal.Copy(outputPtr, output, 0, output.Length);
            Assert.Equal(10, output[94 * 4]);
            Assert.Equal(30, output[95 * 4]);
            Assert.Equal(30, output[184 * 4]);
            Assert.Equal(50, output[185 * 4]);
            Assert.Equal(50, output[274 * 4]);
            Assert.Equal(70, output[275 * 4]);
            Assert.Equal(70, output[369 * 4]);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void ComposeLoadRunsMultipleLayerChannelsInOnePixelShader()
    {
        IntPtr outputPtr = Marshal.AllocHGlobal(4);
        IntPtr aPtr = Marshal.AllocHGlobal(4);
        IntPtr bPtr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 50, 0, 0, 255 }, 0, aPtr, 4);
            Marshal.Copy(new byte[] { 0, 100, 150, 255 }, 0, bPtr, 4);
            using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1);
            image.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);
            using var twoLayer = new GrotheImage(new GrotheImageInfo(1, 1, 1, 1), PixelFormat.Rgba32, "a", "b");
            twoLayer.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);
            twoLayer.UpdateTile(1, 0, 0, bPtr, 1, 1, 4, PixelFormat.Rgba32);
            var compose = twoLayer.CreateLayerCompose("a.r, b.gb * 0.5, 1");
            twoLayer.Load(compose, outputPtr, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] result = new byte[4];
            Marshal.Copy(outputPtr, result, 0, 4);
            Assert.InRange(result[0], (byte)48, (byte)52);
            Assert.InRange(result[1], (byte)48, (byte)52);
            Assert.InRange(result[2], (byte)73, (byte)77);
            Assert.Equal(255, result[3]);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPtr);
            Marshal.FreeHGlobal(aPtr);
            Marshal.FreeHGlobal(bPtr);
        }
    }

    [Fact]
    public void ComposeMemberExpressionRunsInThePixelShader()
    {
        IntPtr outputPtr = Marshal.AllocHGlobal(4);
        IntPtr aPtr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 50, 100, 150, 255 }, 0, aPtr, 4);
            using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1);
            image.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);

            using (var compose = image.CreateLayerCompose("a.lum"))
            {
                image.Load(compose, outputPtr, 1, 1, 1, PixelFormat.Gray8, TransformMatrix.Identity);
                byte[] gray = new byte[1];
                Marshal.Copy(outputPtr, gray, 0, 1);
                // 50 * 0.299 + 100 * 0.587 + 150 * 0.114 = 90.75
                Assert.InRange(gray[0], (byte)89, (byte)92);
            }

            // The scalar threshold is splatted to float4 in the generated HLSL.
            Marshal.Copy(new byte[] { 0, 100, 200, 255 }, 0, aPtr, 4);
            image.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);
            using (var compose = image.CreateLayerCompose("a.bin(0.5)"))
            {
                image.Load(compose, outputPtr, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
                byte[] binarized = new byte[4];
                Marshal.Copy(outputPtr, binarized, 0, 4);
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, binarized);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(outputPtr);
            Marshal.FreeHGlobal(aPtr);
        }
    }

    [Fact]
    public void ComposeExpressionsWithFewerThanFourChannelsArePadded()
    {
        IntPtr outputPtr = Marshal.AllocHGlobal(4);
        IntPtr aPtr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 10, 20, 30, 255 }, 0, aPtr, 4);
            using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1);
            image.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);

            void AssertPixel(string expression, params byte[] expected)
            {
                using (var compose = image.CreateLayerCompose(expression))
                    image.Load(compose, outputPtr, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
                byte[] result = new byte[4];
                Marshal.Copy(outputPtr, result, 0, 4);
                for (int i = 0; i < 4; i++) Assert.InRange(result[i], (byte)Math.Max(0, expected[i] - 2), (byte)Math.Min(255, expected[i] + 2));
            }

            void AssertGray(string expression, byte expected)
            {
                using (var compose = image.CreateLayerCompose(expression))
                    image.Load(compose, outputPtr, 1, 1, 1, PixelFormat.Gray8, TransformMatrix.Identity);
                byte[] result = new byte[1];
                Marshal.Copy(outputPtr, result, 0, 1);
                Assert.InRange(result[0], (byte)Math.Max(0, expected - 2), (byte)Math.Min(255, expected + 2));
            }

            AssertPixel("a.rgba", 10, 20, 30, 255);
            // Three channels: alpha is padded with one.
            AssertPixel("a.rgb", 10, 20, 30, 255);
            // Two channels: blue becomes zero, alpha is padded with one.
            AssertPixel("a.gb", 20, 30, 0, 255);
            // One channel: red, green and blue all carry the expression result.
            AssertPixel("a.lum", 18, 18, 18, 255);
            // Channels carry no meaning, only the position in the list does.
            AssertPixel("a.a, a.r, a.g, a.b", 255, 10, 20, 30);

            // A Gray8 target stores one channel and keeps red, whatever the expression produced.
            AssertGray("a.lum", 18);
            AssertGray("a.r", 10);
            AssertGray("a.rgb", 10);
            AssertGray("a.a, a.r, a.g, a.b", 255);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPtr);
            Marshal.FreeHGlobal(aPtr);
        }
    }

    [Fact]
    public void TextureArrayPagesAreSelectedByLoadShader()
    {
        const int columns = 2050;
        byte[] first = { 77 };
        byte[] last = { 99 };
        byte[] output = new byte[columns];
        IntPtr firstPtr = Marshal.AllocHGlobal(1);
        IntPtr lastPtr = Marshal.AllocHGlobal(1);
        IntPtr outputPtr = Marshal.AllocHGlobal(output.Length);
        try
        {
            Marshal.Copy(first, 0, firstPtr, 1);
            Marshal.Copy(last, 0, lastPtr, 1);
            using var image = new GrotheImage(GrotheImageInfo.FromTiles(1, 1, 1, columns), PixelFormat.Gray8, "a");
            image.UpdateTile(0, 0, 0, firstPtr, 1, 1, 1, PixelFormat.Gray8);
            image.UpdateTile(0, 0, columns - 1, lastPtr, 1, 1, 1, PixelFormat.Gray8);
            image.Load(0, outputPtr, columns, 1, columns, PixelFormat.Gray8, TransformMatrix.Identity);

            Marshal.Copy(outputPtr, output, 0, output.Length);
            Assert.Equal(77, output[0]);
            Assert.Equal(99, output[columns - 1]);
        }
        finally
        {
            Marshal.FreeHGlobal(firstPtr);
            Marshal.FreeHGlobal(lastPtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void ReferenceStyleExpressionsRunInThePixelShader()
    {
        IntPtr outputPtr = Marshal.AllocHGlobal(4);
        IntPtr aPtr = Marshal.AllocHGlobal(4);
        IntPtr bPtr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 50, 100, 150, 255 }, 0, aPtr, 4);
            Marshal.Copy(new byte[] { 100, 0, 0, 255 }, 0, bPtr, 4);
            using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1, "a", "b");
            image.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);
            image.UpdateTile(1, 0, 0, bPtr, 1, 1, 4, PixelFormat.Rgba32);

            void AssertPixel(string expression, params byte[] expected)
            {
                using (var compose = image.CreateLayerCompose(expression))
                    image.Load(compose, outputPtr, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
                byte[] result = new byte[4];
                Marshal.Copy(outputPtr, result, 0, 4);
                for (int i = 0; i < 4; i++) Assert.InRange(result[i], (byte)Math.Max(0, expected[i] - 2), (byte)Math.Min(255, expected[i] + 2));
            }

            // A replicated swizzle composes into the channels in order.
            AssertPixel("a.rr, a.rr", 50, 50, 50, 50);
            // The reference engine's top.lum|side.lum|(top.lum+side.lum)/2, with '|' written as ','.
            // a.lum = 90.75, b.lum = 29.9, average = 60.3
            AssertPixel("a.lum, b.lum, (a.lum+b.lum)/2", 91, 30, 60, 255);
            // A grouped expression can feed a member.
            AssertPixel("(a.rgb).lum, (a.rgb).lum, (a.rgb).lum, 1", 91, 91, 91, 255);
            // HLSL intrinsics such as abs/sqrt/length are available like in the reference engine.
            AssertPixel("a.rgb.abs", 50, 100, 150, 255);
            AssertPixel("a.rgb.sqrt", 113, 160, 196, 255);
            AssertPixel("a.rgb.length", 187, 187, 187, 255);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPtr);
            Marshal.FreeHGlobal(aPtr);
            Marshal.FreeHGlobal(bPtr);
        }
    }

    private static GrotheImage CreateGpuImage(PixelFormat format, int width, int height, params string[] layers)
    {
        try
        {
            return new GrotheImage(new GrotheImageInfo(width, height, width, height), format, layers.Length == 0 ? new[] { "a" } : layers);
        }
        catch (Exception ex) when (ex is GrotheImageException || ex is DllNotFoundException || ex is TypeInitializationException)
        {
            throw SkipException.ForSkip("D3D11 hardware is unavailable: " + ex.Message);
        }
    }
}
