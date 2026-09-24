using System;
using System.IO;
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
            // A flat colour round trips through the BT.709 limited range pair within a couple of levels.
            Assert.InRange(result[0], (byte)77, (byte)83);
            Assert.InRange(result[1], (byte)97, (byte)103);
            Assert.InRange(result[2], (byte)118, (byte)124);
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
            Assert.InRange(result[0], (byte)87, (byte)93);
            Assert.InRange(result[1], (byte)107, (byte)113);
            Assert.InRange(result[2], (byte)127, (byte)133);
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

    [Fact]
    public void BlendSeamsCrossFadesTheOverlapBetweenTiles()
    {
        const int tileWidth = 16;
        const int tileHeight = 4;
        const int overlap = 8;
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(tileWidth, tileHeight, 1, 2, overlap, 0), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, 0, tileWidth, tileHeight, 0);
        UpdateGrayTile(image, 0, 1, tileWidth, tileHeight, 255);

        int width = (int)image.Info.Width;
        byte[] before = LoadGrayRow(image, width, tileHeight, 0);
        // Before blending the join is a hard step in the middle of the overlap.
        Assert.Equal(0, before[11]);
        Assert.Equal(255, before[12]);

        image.BlendSeams();
        byte[] after = LoadGrayRow(image, width, tileHeight, 0);

        // Outside the overlap nothing changes.
        Assert.Equal(0, after[0]);
        Assert.Equal(0, after[7]);
        Assert.Equal(255, after[16]);
        Assert.Equal(255, after[width - 1]);

        // Inside the overlap the two tiles are cross faded with a smoothstep ramp: the first tile loses
        // weight while the second gains it, and the midpoint of the band is the 50% point.
        byte[] ramp = { 3, 24, 59, 104, 151, 196, 231, 252 };
        for (int i = 0; i < ramp.Length; i++)
            Assert.InRange(after[8 + i], (byte)Math.Max(0, ramp[i] - 2), (byte)Math.Min(255, ramp[i] + 2));

        // Blending again is a no-op, because both tiles now hold the same blended pixels.
        image.BlendSeams();
        Assert.Equal(after, LoadGrayRow(image, width, tileHeight, 0));
    }

    [Fact]
    public void BlendSeamsCrossFadesRowsAsWell()
    {
        const int tileWidth = 4;
        const int tileHeight = 16;
        const int overlap = 8;
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(tileWidth, tileHeight, 2, 1, 0, overlap), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, 0, tileWidth, tileHeight, 0);
        UpdateGrayTile(image, 1, 0, tileWidth, tileHeight, 255);

        image.BlendSeams();

        int width = (int)image.Info.Width;
        int height = (int)image.Info.Height;
        byte[] pixels = LoadGray(image, width, height);
        byte[] ramp = { 3, 24, 59, 104, 151, 196, 231, 252 };
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, pixels[0 * width + 0]);
            Assert.Equal(0, pixels[7 * width + 0]);
            Assert.InRange(pixels[(8 + i) * width + 0], (byte)Math.Max(0, ramp[i] - 2), (byte)Math.Min(255, ramp[i] + 2));
        }
        Assert.Equal(255, pixels[16 * width]);
        Assert.Equal(255, pixels[(height - 1) * width]);
    }

    [Fact]
    public void BlendSeamsBlendsTheFourTileCorner()
    {
        const int tileWidth = 16;
        const int tileHeight = 16;
        const int overlap = 8;
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(tileWidth, tileHeight, 2, 2, overlap, overlap), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, 0, tileWidth, tileHeight, 0);
        UpdateGrayTile(image, 0, 1, tileWidth, tileHeight, 100);
        UpdateGrayTile(image, 1, 0, tileWidth, tileHeight, 200);
        UpdateGrayTile(image, 1, 1, tileWidth, tileHeight, 255);

        image.BlendSeams();

        int width = (int)image.Info.Width;
        int height = (int)image.Info.Height;
        byte[] pixels = LoadGray(image, width, height);

        // Tile interiors keep their own value.
        Assert.Equal(0, pixels[4 * width + 4]);
        Assert.Equal(100, pixels[4 * width + 20]);
        Assert.Equal(200, pixels[20 * width + 4]);
        Assert.Equal(255, pixels[20 * width + 20]);

        // The four tile corner fades both ways: 0->100 across x and 0->200 down y, so the centre of the
        // corner holds the separable blend of all four tiles.
        Assert.InRange(pixels[12 * width + 12], (byte)159, (byte)165);
        // Away from the corner each band still fades between the two tiles it joins. Row 4 is inside the
        // top tiles, so it only fades across x; column 4 only fades down y.
        Assert.InRange(pixels[4 * width + 12], (byte)56, (byte)62);
        Assert.InRange(pixels[20 * width + 12], (byte)230, (byte)236);
        Assert.InRange(pixels[12 * width + 4], (byte)116, (byte)122);
        Assert.InRange(pixels[12 * width + 20], (byte)189, (byte)195);
    }

    [Fact]
    public void BlendSeamsLeavesUntouchedTilesAlone()
    {
        const int tileWidth = 16;
        const int tileHeight = 4;
        const int overlap = 8;
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(tileWidth, tileHeight, 1, 2, overlap, 0), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, 0, tileWidth, tileHeight, 40);

        int width = (int)image.Info.Width;
        byte[] before = LoadGrayRow(image, width, tileHeight, 0);
        image.BlendSeams();

        // The right tile was never written, so its seam is skipped and its zeros are not smeared.
        Assert.Equal(before, LoadGrayRow(image, width, tileHeight, 0));
    }

    [Fact]
    public void BlendSeamsHandlesTilesStoredInDifferentTexturePages()
    {
        // Pages hold LayerStore.MaxArraySlices slices, so the seam between tile 2047 and 2048 crosses two
        // page textures and needs the two UAV shader variant.
        const int columns = LayerStore.MaxArraySlices + 1;
        const int tileWidth = 4;
        const int tileHeight = 1;
        const int overlap = 2;
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(tileWidth, tileHeight, 1, columns, overlap, 0), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, LayerStore.MaxArraySlices - 1, tileWidth, tileHeight, 0);
        UpdateGrayTile(image, 0, LayerStore.MaxArraySlices, tileWidth, tileHeight, 255);

        image.BlendSeams();

        int width = (int)image.Info.Width;
        byte[] row = LoadGrayRow(image, width, tileHeight, 0);
        int step = tileWidth - overlap;
        int seam = (int)(LayerStore.MaxArraySlices - 1) * step + step;
        // The band is two pixels wide, so the two tiles meet at 25% and 75% of the ramp.
        Assert.InRange(row[seam], (byte)38, (byte)42);
        Assert.InRange(row[seam + 1], (byte)213, (byte)217);
        Assert.Equal(0, row[seam - 1]);
        Assert.Equal(255, row[seam + 2]);
    }

    [Fact]
    public void BlendSeamsFadesTheChromaPlaneOfYuvImagesToo()
    {
        const int tileWidth = 16;
        const int tileHeight = 8;
        const int overlap = 8;
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(tileWidth, tileHeight, 1, 2, overlap, 0), PixelFormat.Yuv420);
        UpdateGrayTile(image, 0, 0, tileWidth, tileHeight, 0);
        UpdateGrayTile(image, 0, 1, tileWidth, tileHeight, 255);

        image.BlendSeams();

        int width = (int)image.Info.Width;
        byte[] row = LoadGrayRow(image, width, tileHeight, 0);
        // Y and UV are both faded, so the luma rises across the whole band instead of jumping at the
        // middle of it, and it still reaches both tile interiors.
        Assert.InRange(row[0], (byte)0, (byte)4);
        Assert.InRange(row[width - 1], (byte)251, (byte)255);
        for (int x = 1; x < width; x++) Assert.True(row[x] >= row[x - 1] - 2, "luma must not step back at x=" + x);
        // Without blending the join would jump straight from black to the second tile; with both planes
        // faded no neighbouring pixels differ by more than a fraction of the ramp.
        for (int x = 1; x < width; x++) Assert.True(row[x] - row[x - 1] <= 60, "luma still steps at x=" + x);
    }

    [Fact]
    public void BlendSeamsWithoutOverlapChangesNothing()
    {
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(4, 4, 1, 2), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, 0, 4, 4, 10);
        UpdateGrayTile(image, 0, 1, 4, 4, 200);

        byte[] before = LoadGray(image, (int)image.Info.Width, (int)image.Info.Height);
        image.BlendSeams();
        Assert.Equal(before, LoadGray(image, (int)image.Info.Width, (int)image.Info.Height));
    }

    [Fact]
    public void UpdateMapsUserImageCoordinatesIntoTheGrotheImage()
    {
        // The matrix maps User Image coordinates to Grothe Image coordinates, so a 2x scale makes a 2x2 User
        // Image cover the whole 4x4 Grothe Image. Reading the matrix in the other direction would instead
        // magnify the Grothe Image into the User Image and leave almost every pixel untouched.
        using var image = CreateGpuImage(PixelFormat.Rgba32, 4, 4);
        byte[] source = new byte[2 * 2 * 4];
        for (int i = 0; i < source.Length; i += 4) { source[i] = 200; source[i + 1] = 10; source[i + 2] = 30; source[i + 3] = 255; }
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(4 * 4 * 4);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            image.Update(0, sourcePtr, 2, 2, 8, PixelFormat.Rgba32, new TransformMatrix(2, 0, 0, 0, 2, 0, 0, 0, 1));
            image.Load(0, outputPtr, 4, 4, 16, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] output = new byte[4 * 4 * 4];
            Marshal.Copy(outputPtr, output, 0, output.Length);
            for (int i = 0; i < output.Length; i += 4)
            {
                Assert.Equal(200, output[i]);
                Assert.Equal(10, output[i + 1]);
                Assert.Equal(30, output[i + 2]);
                Assert.Equal(255, output[i + 3]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void UpdateClipsPixelsOutsideTheUserImage()
    {
        // A 0.5 scale puts the User Image over Grothe Image pixel (0, 0) only. Every other pixel is clipped,
        // so it keeps the value an earlier write left there instead of being cleared.
        using var image = CreateGpuImage(PixelFormat.Rgba32, 4, 4);
        byte[] fill = new byte[4 * 4 * 4];
        for (int i = 0; i < fill.Length; i += 4) { fill[i] = 100; fill[i + 3] = 255; }
        byte[] source = new byte[2 * 2 * 4];
        for (int i = 0; i < source.Length; i += 4) { source[i] = 7; source[i + 3] = 255; }
        IntPtr fillPtr = Marshal.AllocHGlobal(fill.Length);
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr outputPtr = Marshal.AllocHGlobal(fill.Length);
        try
        {
            Marshal.Copy(fill, 0, fillPtr, fill.Length);
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            image.UpdateTile(0, 0, 0, fillPtr, 4, 4, 16, PixelFormat.Rgba32);
            image.Update(0, sourcePtr, 2, 2, 8, PixelFormat.Rgba32, new TransformMatrix(0.5, 0, 0, 0, 0.5, 0, 0, 0, 1));
            image.Load(0, outputPtr, 4, 4, 16, PixelFormat.Rgba32, TransformMatrix.Identity);

            byte[] output = new byte[fill.Length];
            Marshal.Copy(outputPtr, output, 0, output.Length);
            // The covered pixel holds the User Image value, every clipped pixel still holds the fill.
            Assert.Equal(7, output[0]);
            Assert.Equal(100, output[4]);
            Assert.Equal(100, output[4 * 4]);
            Assert.Equal(100, output[(15 * 4) + 0]);
            Assert.Equal(255, output[(15 * 4) + 3]);
        }
        finally
        {
            Marshal.FreeHGlobal(fillPtr);
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void ComposeBindsOnlyTheLayersTheExpressionNames()
    {
        // Only the layers the expression reads are bound, renumbered from zero in image order. A wrong
        // binding would return another layer's red channel instead.
        IntPtr aPtr = Marshal.AllocHGlobal(4);
        IntPtr bPtr = Marshal.AllocHGlobal(4);
        IntPtr cPtr = Marshal.AllocHGlobal(4);
        IntPtr outputPtr = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 11, 0, 0, 255 }, 0, aPtr, 4);
            Marshal.Copy(new byte[] { 222, 0, 0, 255 }, 0, bPtr, 4);
            Marshal.Copy(new byte[] { 33, 0, 0, 255 }, 0, cPtr, 4);
            using var image = CreateGpuImage(PixelFormat.Rgba32, 1, 1, "a", "b", "c");
            image.UpdateTile(0, 0, 0, aPtr, 1, 1, 4, PixelFormat.Rgba32);
            image.UpdateTile(1, 0, 0, bPtr, 1, 1, 4, PixelFormat.Rgba32);
            image.UpdateTile(2, 0, 0, cPtr, 1, 1, 4, PixelFormat.Rgba32);

            AssertComposition(image, "b.r, b.r, b.r, 1", new[] { 1 }, 222, outputPtr);
            AssertComposition(image, "c.r, c.r, c.r, 1", new[] { 2 }, 33, outputPtr);
            // Reading two of the three layers binds them in image order, not in expression order.
            AssertComposition(image, "c.r, a.r, c.r, 1", new[] { 0, 2 }, 33, outputPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(aPtr);
            Marshal.FreeHGlobal(bPtr);
            Marshal.FreeHGlobal(cPtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    private static void AssertComposition(GrotheImage image, string expression, int[] boundLayers, byte expectedRed, IntPtr outputPtr)
    {
        using var compose = image.CreateLayerCompose(expression);
        Assert.Equal(boundLayers, compose.BoundLayerIndices);
        image.Load(compose, outputPtr, 1, 1, 4, PixelFormat.Rgba32, TransformMatrix.Identity);
        byte[] result = new byte[4];
        Marshal.Copy(outputPtr, result, 0, 4);
        Assert.Equal(expectedRed, result[0]);
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void EveryImageSharesOneDirect3DDevice()
    {
        using var first = CreateGpuImage(PixelFormat.Gray8, 4, 4);
        using var second = CreateGpuImage(PixelFormat.Gray8, 4, 4);
        UpdateGrayTile(first, 0, 0, 4, 4, 42);
        UpdateGrayTile(second, 0, 0, 4, 4, 7);

        Assert.Same(first.Graphics.Device, second.Graphics.Device);
        Assert.Same(first.Graphics.Context, second.Graphics.Context);
        Assert.True(first.Graphics.FeatureLevel >= Vortice.Direct3D.FeatureLevel.Level_11_0);
    }

    [Fact]
    public void YuvRoundTripKeepsDarkAndBrightGrays()
    {
        // Limited range storage compresses luma to 16..235; encoding luma without that compression (while
        // still decoding it as limited range) crushes dark values and clips bright ones.
        using var image = CreateGpuImage(PixelFormat.Yuv420, 4, 2);
        IntPtr sourcePtr = Marshal.AllocHGlobal(4 * 2 * 4);
        IntPtr outputPtr = Marshal.AllocHGlobal(4 * 2 * 4);
        try
        {
            foreach (byte value in new byte[] { 8, 32, 64, 96, 128, 192, 250 })
            {
                byte[] source = new byte[4 * 2 * 4];
                for (int i = 0; i < source.Length; i += 4) { source[i] = value; source[i + 1] = value; source[i + 2] = value; source[i + 3] = 255; }
                Marshal.Copy(source, 0, sourcePtr, source.Length);
                image.Update(0, sourcePtr, 4, 2, 16, PixelFormat.Rgba32, TransformMatrix.Identity);
                image.Load(0, outputPtr, 4, 2, 16, PixelFormat.Rgba32, TransformMatrix.Identity);

                byte[] output = new byte[source.Length];
                Marshal.Copy(outputPtr, output, 0, output.Length);
                // A neutral gray has neutral chroma, so all three channels come back equal.
                Assert.InRange(output[0], (byte)(value - 2), (byte)(value + 2));
                Assert.InRange(output[1], (byte)(value - 2), (byte)(value + 2));
                Assert.InRange(output[2], (byte)(value - 2), (byte)(value + 2));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    [Fact]
    public void UpdateDoesNotMarkTilesTheUserImageNeverReaches()
    {
        // Two 16 pixel tiles whose storage rectangles step 8 pixels apart, so they overlap. A User Image that
        // only covers x in [0, 4) reaches tile 0 alone: tile 1 has to stay unwritten, which shows both in the
        // serialized record count and in tile 1 still reading back as the empty value.
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(16, 4, 1, 2, 8, 0), PixelFormat.Gray8);
        UpdateGrayTile(image, 0, 0, 16, 4, 200);

        byte[] source = new byte[4 * 4];
        for (int i = 0; i < source.Length; i++) source[i] = 7;
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            image.Update(0, sourcePtr, 4, 4, 4, PixelFormat.Gray8, TransformMatrix.Identity);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
        }

        // 0..3 are the update, 4..11 are the clipped part of tile 0, 12..23 are owned by the unwritten tile 1.
        byte[] row = LoadGrayRow(image, (int)image.Info.Width, 4, 0);
        Assert.Equal(7, row[0]);
        Assert.Equal(200, row[4]);
        Assert.Equal(200, row[11]);
        Assert.Equal(0, row[12]);

        using var stream = new MemoryStream();
        image.Serialize(stream);
        byte[] bytes = stream.ToArray();
        // Header: magic(8) version(4) tileWidth(4) tileHeight(4) rows(8) columns(8) overlapX(4) overlapY(4)
        // format(4) layerCount(4) = 52 bytes, then the single layer name "a" as a length byte plus one byte,
        // then the 64 bit record count.
        const int recordCountOffset = 54;
        Assert.Equal(1L, BitConverter.ToInt64(bytes, recordCountOffset));
    }

    [Fact]
    public void UnwrittenTilesLoadAsTheZeroValueOfTheirFormat()
    {
        // Only tile (0, 0) is written; the rest of the image keeps the value the page was initialised with
        // instead of uninitialised memory.
        using (var image = CreateGpuImage(GrotheImageInfo.FromTiles(4, 2, 1, 2), PixelFormat.Rgba32))
        {
            byte[] source = new byte[4 * 2 * 4];
            for (int i = 0; i < source.Length; i += 4) { source[i] = 9; source[i + 3] = 255; }
            IntPtr pointer = Marshal.AllocHGlobal(source.Length);
            try
            {
                Marshal.Copy(source, 0, pointer, source.Length);
                image.UpdateTile(0, 0, 0, pointer, 4, 2, 16, PixelFormat.Rgba32);
            }
            finally { Marshal.FreeHGlobal(pointer); }

            byte[] pixels = LoadRgba(image, 8, 2, 32);
            for (int y = 0; y < 2; y++)
            for (int x = 4; x < 8; x++)
            {
                int pixel = y * 8 + x;
                Assert.Equal(0, pixels[pixel * 4 + 0]);
                Assert.Equal(0, pixels[pixel * 4 + 1]);
                Assert.Equal(0, pixels[pixel * 4 + 2]);
                Assert.Equal(0, pixels[pixel * 4 + 3]);
            }
            // The written tile keeps its own value on both rows.
            Assert.Equal(9, pixels[0]);
            Assert.Equal(9, pixels[(8 + 3) * 4]);
        }

        // A YUV tile starts at luma zero and neutral chroma, which decodes to opaque black.
        using (var image = CreateGpuImage(GrotheImageInfo.FromTiles(4, 2, 1, 2), PixelFormat.Yuv420))
        {
            byte[] pixels = LoadRgba(image, 8, 2, 32);
            for (int pixel = 0; pixel < 16; pixel++)
            {
                Assert.InRange(pixels[pixel * 4 + 0], (byte)0, (byte)2);
                Assert.InRange(pixels[pixel * 4 + 1], (byte)0, (byte)2);
                Assert.InRange(pixels[pixel * 4 + 2], (byte)0, (byte)2);
                Assert.Equal(255, pixels[pixel * 4 + 3]);
            }
        }
    }

    [Fact]
    public void OverlapWiderThanTwiceTheStepStillSamplesEveryTile()
    {
        // tileWidth 10 with overlap 8 leaves a step of 2, so a tile owns a 2 pixel wide band while storing
        // 10 pixels. The sampling ownership rule still has to select each tile across the whole image.
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(10, 4, 1, 4, 8, 0), PixelFormat.Gray8);
        Assert.Equal(2, image.Info.StepX);
        for (int column = 0; column < 4; column++) UpdateGrayTile(image, 0, column, 10, 4, (byte)(column * 60));

        // Ownership: floor((x + 0.5 - overlap / 2) / step), so the bands are 0..5, 6..7, 8..9 and 10..15.
        byte[] pixels = LoadGray(image, (int)image.Info.Width, 4);
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[5]);
        Assert.Equal(60, pixels[6]);
        Assert.Equal(60, pixels[7]);
        Assert.Equal(120, pixels[8]);
        Assert.Equal(120, pixels[9]);
        Assert.Equal(180, pixels[10]);
        Assert.Equal(180, pixels[15]);
    }

    [Fact]
    public void SamplingBindsThePageOfTheTileTheOwnershipRuleSelects()
    {
        // 2049 tiles of 10 x 4 pixels with a step of 2: tile 2047 is the last slice of page 0 and tile 2048 is
        // the only slice of page 1. A load that maps onto the low end of that boundary has to bind page 0,
        // which a range computed from the storage rectangle instead of the ownership band would miss.
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(10, 4, 1, 2049, 8, 0), PixelFormat.Gray8);
        long lastOfFirstPage = LayerStore.MaxArraySlices - 1;
        UpdateGrayTile(image, 0, lastOfFirstPage, 10, 4, 111);
        UpdateGrayTile(image, 0, lastOfFirstPage + 1, 10, 4, 222);

        // Grothe Image x = 4098 + u lands on User Image x = u for u in [0, 2). Both output pixels are owned by
        // tile 2047, the last slice of page 0, even though their storage rectangle reaches into page 1.
        var matrix = new TransformMatrix(1, 0, -4098, 0, 1, 0, 0, 0, 1);
        IntPtr output = Marshal.AllocHGlobal(2 * 4);
        try
        {
            image.Load(0, output, 2, 4, 2, PixelFormat.Gray8, matrix);
            byte[] pixels = new byte[2 * 4];
            Marshal.Copy(output, pixels, 0, pixels.Length);
            Assert.Equal(111, pixels[0]);
            Assert.Equal(111, pixels[1]);
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    [Fact]
    public void ATranslationCanTargetASingleTile()
    {
        // Two tiles of 4 x 2: the User Image maps to Grothe Image x in [4, 8), which is tile 1 alone. Tile 0 is
        // never dispatched, so it stays at the empty value and only one tile record is serialized.
        using var image = CreateGpuImage(GrotheImageInfo.FromTiles(4, 2, 1, 2), PixelFormat.Gray8);
        byte[] source = new byte[4 * 2];
        for (int i = 0; i < source.Length; i++) source[i] = 77;
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            image.Update(0, sourcePtr, 4, 2, 4, PixelFormat.Gray8, new TransformMatrix(1, 0, 4, 0, 1, 0, 0, 0, 1));
        }
        finally { Marshal.FreeHGlobal(sourcePtr); }

        byte[] pixels = LoadGray(image, 8, 2);
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[3]);
        Assert.Equal(77, pixels[4]);
        Assert.Equal(77, pixels[7]);
        Assert.Equal(77, pixels[8 + 4]);

        using var stream = new MemoryStream();
        image.Serialize(stream);
        byte[] bytes = stream.ToArray();
        Assert.Equal(1L, BitConverter.ToInt64(bytes, 54));
    }

    [Fact]
    public void RowPaddingIsRespectedOnEveryPath()
    {
        // A User Image row may be padded, so the source stride is wider than the row itself.
        using (var image = CreateGpuImage(GrotheImageInfo.FromTiles(4, 2, 1, 1), PixelFormat.Gray8))
        {
            const int stride = 7;
            byte[] source = new byte[stride * 2];
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++) source[y * stride + x] = (byte)(10 + y * 10 + x);
            for (int y = 0; y < 2; y++)
            for (int x = 4; x < stride; x++) source[y * stride + x] = 200;   // padding must be ignored

            IntPtr pointer = Marshal.AllocHGlobal(source.Length);
            try
            {
                Marshal.Copy(source, 0, pointer, source.Length);
                image.UpdateTile(0, 0, 0, pointer, 4, 2, stride, PixelFormat.Gray8);
            }
            finally { Marshal.FreeHGlobal(pointer); }

            byte[] pixels = LoadGray(image, 4, 2);
            Assert.Equal(10, pixels[0]);
            Assert.Equal(13, pixels[3]);
            Assert.Equal(20, pixels[4]);
            Assert.Equal(23, pixels[7]);
        }

        // The warp path uploads the padded rows too, then samples in User Image coordinates.
        using (var image = CreateGpuImage(PixelFormat.Bgra32, 4, 2))
        {
            const int stride = 20;
            byte[] source = new byte[stride * 2];
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                int index = y * stride + x * 4;
                source[index] = (byte)(40 + y);
                source[index + 3] = 255;
            }
            for (int y = 0; y < 2; y++)
            for (int x = 16; x < stride; x++) source[y * stride + x] = 200;

            IntPtr pointer = Marshal.AllocHGlobal(source.Length);
            try
            {
                Marshal.Copy(source, 0, pointer, source.Length);
                image.Update(0, pointer, 4, 2, stride, PixelFormat.Bgra32, TransformMatrix.Identity);
            }
            finally { Marshal.FreeHGlobal(pointer); }

            byte[] pixels = TestImages.Load(image, 0, 4, 2, PixelFormat.Bgra32);
            // Row 0 was written with 40 and row 1 with 41; the padded bytes never became pixels.
            Assert.Equal(40, pixels[0]);
            Assert.Equal(40, pixels[3 * 4]);
            Assert.Equal(255, pixels[3 * 4 + 3]);
            Assert.Equal(41, pixels[4 * 4]);
            Assert.Equal(41, pixels[(4 + 3) * 4]);
        }
    }

    [Fact]
    public void LoadWritesRowsAtTheCallersStride()
    {
        using var image = CreateGpuImage(PixelFormat.Rgba32, 2, 2);
        byte[] source = new byte[2 * 2 * 4];
        for (int i = 0; i < source.Length; i += 4) { source[i] = 60; source[i + 3] = 255; }
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        int stride = 2 * 4 + 8;
        IntPtr outputPtr = Marshal.AllocHGlobal(stride * 2);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            image.UpdateTile(0, 0, 0, sourcePtr, 2, 2, 8, PixelFormat.Rgba32);

            // The gap between the rows must stay untouched, whatever the caller put there.
            byte[] output = new byte[stride * 2];
            for (int i = 0; i < output.Length; i++) output[i] = 0xAB;
            Marshal.Copy(output, 0, outputPtr, output.Length);
            image.Load(0, outputPtr, 2, 2, stride, PixelFormat.Rgba32, TransformMatrix.Identity);
            Marshal.Copy(outputPtr, output, 0, output.Length);

            Assert.Equal(60, output[0]);
            Assert.Equal(60, output[4]);
            Assert.Equal(0xAB, output[8]);
            Assert.Equal(0xAB, output[15]);
            Assert.Equal(60, output[stride + 0]);
            Assert.Equal(60, output[stride + 4]);
            Assert.Equal(0xAB, output[stride + 8]);
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(outputPtr);
        }
    }

    private static byte[] LoadRgba(GrotheImage image, int width, int height, int stride)
    {
        IntPtr output = Marshal.AllocHGlobal(stride * height);
        try
        {
            image.Load(0, output, width, height, stride, PixelFormat.Rgba32, TransformMatrix.Identity);
            byte[] result = new byte[stride * height];
            Marshal.Copy(output, result, 0, result.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    private static void UpdateGrayTile(GrotheImage image, long row, long column, int width, int height, byte value)
    {
        byte[] source = new byte[width * height];
        for (int i = 0; i < source.Length; i++) source[i] = value;
        IntPtr pointer = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, pointer, source.Length);
            image.UpdateTile(0, row, column, pointer, width, height, width, PixelFormat.Gray8);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static byte[] LoadGray(GrotheImage image, int width, int height)
    {
        IntPtr output = Marshal.AllocHGlobal(width * height);
        try
        {
            image.Load(0, output, width, height, width, PixelFormat.Gray8, TransformMatrix.Identity);
            byte[] result = new byte[width * height];
            Marshal.Copy(output, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    private static byte[] LoadGrayRow(GrotheImage image, int width, int height, int row)
    {
        byte[] all = LoadGray(image, width, height);
        var result = new byte[width];
        Array.Copy(all, row * width, result, 0, width);
        return result;
    }

    private static GrotheImage CreateGpuImage(PixelFormat format, int width, int height, params string[] layers)
    {
        return CreateGpuImage(new GrotheImageInfo(width, height, width, height), format, layers);
    }

    private static GrotheImage CreateGpuImage(GrotheImageInfo info, PixelFormat format, params string[] layers)
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
}
