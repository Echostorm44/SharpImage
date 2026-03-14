using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for JPEG encoder improvements: chroma subsampling, grayscale encoding,
/// and optimized Huffman tables.
/// </summary>
public class JpegEncoderTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_jpegenc_{name}");

    /// <summary>Creates a test image with a gradient pattern for compression testing.</summary>
    private static ImageFrame CreateGradientImage(int width, int height, bool grayscale = false)
    {
        var frame = new ImageFrame();
        if (grayscale)
        {
            frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    byte lum = (byte)((x * 255 / Math.Max(width - 1, 1) + y * 127 / Math.Max(height - 1, 1)) / 2);
                    row[x] = Quantum.ScaleFromByte(lum);
                }
            }
        }
        else
        {
            frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    byte r = (byte)(x * 255 / Math.Max(width - 1, 1));
                    byte g = (byte)(y * 255 / Math.Max(height - 1, 1));
                    byte b = (byte)((x + y) * 127 / Math.Max(width + height - 2, 1));
                    int offset = x * 3;
                    row[offset] = Quantum.ScaleFromByte(r);
                    row[offset + 1] = Quantum.ScaleFromByte(g);
                    row[offset + 2] = Quantum.ScaleFromByte(b);
                }
            }
        }
        return frame;
    }

    // ─── Chroma Subsampling Tests ────────────────────────────────

    [Test]
    public async Task Write_Yuv420_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("yuv420.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv422_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("yuv422.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv422);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv444_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("yuv444.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv444);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv420_SmallerThanYuv444()
    {
        var image = CreateGradientImage(128, 128);
        string path444 = TempPath("cmp_444.jpg");
        string path420 = TempPath("cmp_420.jpg");
        try
        {
            JpegCoder.Write(image, path444, 85, JpegSubsampling.Yuv444);
            JpegCoder.Write(image, path420, 85, JpegSubsampling.Yuv420);
            long size444 = new FileInfo(path444).Length;
            long size420 = new FileInfo(path420).Length;
            await Assert.That(size420).IsLessThan(size444);
        }
        finally
        {
            if (File.Exists(path444)) File.Delete(path444);
            if (File.Exists(path420)) File.Delete(path420);
        }
    }

    [Test]
    public async Task Write_Yuv422_SmallerThanYuv444()
    {
        var image = CreateGradientImage(128, 128);
        string path444 = TempPath("cmp422_444.jpg");
        string path422 = TempPath("cmp422_422.jpg");
        try
        {
            JpegCoder.Write(image, path444, 85, JpegSubsampling.Yuv444);
            JpegCoder.Write(image, path422, 85, JpegSubsampling.Yuv422);
            long size444 = new FileInfo(path444).Length;
            long size422 = new FileInfo(path422).Length;
            await Assert.That(size422).IsLessThan(size444);
        }
        finally
        {
            if (File.Exists(path444)) File.Delete(path444);
            if (File.Exists(path422)) File.Delete(path422);
        }
    }

    [Test]
    public async Task Write_Yuv420_RoundTrip_AcceptableQuality()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("rt420.jpg");
        try
        {
            JpegCoder.Write(image, path, 95, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            // JPEG is lossy — verify average pixel difference is small
            double avgDiff = ComputeAveragePixelDifference(image, decoded);
            await Assert.That(avgDiff).IsLessThan(15.0); // Generous tolerance for lossy + subsampling
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv420_NonAligned_Dimensions()
    {
        // Test with dimensions not divisible by 16 (MCU size for 4:2:0)
        var image = CreateGradientImage(37, 23);
        string path = TempPath("yuv420_nonaligned.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(37u);
            await Assert.That(decoded.Rows).IsEqualTo(23u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv422_NonAligned_Dimensions()
    {
        // Dimensions not divisible by 16 (MCU width for 4:2:2)
        var image = CreateGradientImage(41, 29);
        string path = TempPath("yuv422_nonaligned.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv422);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(41u);
            await Assert.That(decoded.Rows).IsEqualTo(29u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ─── Grayscale Encoding Tests ────────────────────────────────

    [Test]
    public async Task Write_Grayscale_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64, grayscale: true);
        string path = TempPath("gray.jpg");
        try
        {
            JpegCoder.Write(image, path, 85);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Grayscale_SingleComponent_SmallerFile()
    {
        // A 1-channel image should produce a smaller JPEG than a 3-channel image
        var grayImage = CreateGradientImage(128, 128, grayscale: true);
        var colorImage = CreateGradientImage(128, 128, grayscale: false);
        string pathGray = TempPath("gray_size.jpg");
        string pathColor = TempPath("color_size.jpg");
        try
        {
            JpegCoder.Write(grayImage, pathGray, 85);
            JpegCoder.Write(colorImage, pathColor, 85);
            long sizeGray = new FileInfo(pathGray).Length;
            long sizeColor = new FileInfo(pathColor).Length;
            await Assert.That(sizeGray).IsLessThan(sizeColor);
        }
        finally
        {
            if (File.Exists(pathGray)) File.Delete(pathGray);
            if (File.Exists(pathColor)) File.Delete(pathColor);
        }
    }

    [Test]
    public async Task Write_Grayscale_RoundTrip_AcceptableQuality()
    {
        var image = CreateGradientImage(64, 64, grayscale: true);
        string path = TempPath("gray_rt.jpg");
        try
        {
            JpegCoder.Write(image, path, 95);
            var decoded = JpegCoder.Read(path);
            // Compare luminance values
            double avgDiff = ComputeAverageGrayDifference(image, decoded);
            await Assert.That(avgDiff).IsLessThan(5.0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Grayscale_TinyImage()
    {
        // Edge case: very small grayscale image
        var image = CreateGradientImage(4, 4, grayscale: true);
        string path = TempPath("gray_tiny.jpg");
        try
        {
            JpegCoder.Write(image, path, 85);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(4u);
            await Assert.That(decoded.Rows).IsEqualTo(4u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ─── Optimized Huffman Tests ─────────────────────────────────

    [Test]
    public async Task Write_OptimizedHuffman_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("opthuff.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv444, optimizeHuffman: true);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_OptimizedHuffman_SmallerOrEqual()
    {
        var image = CreateGradientImage(128, 128);
        string pathStd = TempPath("huffstd.jpg");
        string pathOpt = TempPath("huffopt.jpg");
        try
        {
            JpegCoder.Write(image, pathStd, 85, JpegSubsampling.Yuv444, optimizeHuffman: false);
            JpegCoder.Write(image, pathOpt, 85, JpegSubsampling.Yuv444, optimizeHuffman: true);
            long sizeStd = new FileInfo(pathStd).Length;
            long sizeOpt = new FileInfo(pathOpt).Length;
            // Optimized should be <= standard (often smaller, rarely exactly equal)
            await Assert.That(sizeOpt).IsLessThanOrEqualTo(sizeStd);
        }
        finally
        {
            if (File.Exists(pathStd)) File.Delete(pathStd);
            if (File.Exists(pathOpt)) File.Delete(pathOpt);
        }
    }

    [Test]
    public async Task Write_OptimizedHuffman_RoundTrip_AcceptableQuality()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("opthuff_rt.jpg");
        try
        {
            JpegCoder.Write(image, path, 95, JpegSubsampling.Yuv444, optimizeHuffman: true);
            var decoded = JpegCoder.Read(path);
            double avgDiff = ComputeAveragePixelDifference(image, decoded);
            await Assert.That(avgDiff).IsLessThan(5.0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ─── Combined Feature Tests ──────────────────────────────────

    [Test]
    public async Task Write_Yuv420_OptimizedHuffman_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("yuv420_opthuff.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420, optimizeHuffman: true);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv422_OptimizedHuffman_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("yuv422_opthuff.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv422, optimizeHuffman: true);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Grayscale_OptimizedHuffman_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64, grayscale: true);
        string path = TempPath("gray_opthuff.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, optimizeHuffman: true);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_Yuv420_OptimizedHuffman_Smallest()
    {
        // The combination of subsampling + optimized Huffman should produce the smallest file
        var image = CreateGradientImage(128, 128);
        string path444 = TempPath("smallest_444.jpg");
        string path420opt = TempPath("smallest_420opt.jpg");
        try
        {
            JpegCoder.Write(image, path444, 85, JpegSubsampling.Yuv444, false);
            JpegCoder.Write(image, path420opt, 85, JpegSubsampling.Yuv420, true);
            long size444 = new FileInfo(path444).Length;
            long size420opt = new FileInfo(path420opt).Length;
            await Assert.That(size420opt).IsLessThan(size444);
        }
        finally
        {
            if (File.Exists(path444)) File.Delete(path444);
            if (File.Exists(path420opt)) File.Delete(path420opt);
        }
    }

    // ─── Progressive Encoder Tests ───────────────────────────────

    [Test]
    public async Task WriteProgressive_Yuv420_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("prog_yuv420.jpg");
        try
        {
            JpegCoder.WriteProgressive(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task WriteProgressive_Grayscale_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64, grayscale: true);
        string path = TempPath("prog_gray.jpg");
        try
        {
            JpegCoder.WriteProgressive(image, path, 85);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task WriteProgressive_OptimizedHuffman_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("prog_opthuff.jpg");
        try
        {
            JpegCoder.WriteProgressive(image, path, 85, optimizeHuffman: true);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task WriteProgressive_Yuv420_OptimizedHuffman_ProducesValidJpeg()
    {
        var image = CreateGradientImage(64, 64);
        string path = TempPath("prog_420opt.jpg");
        try
        {
            JpegCoder.WriteProgressive(image, path, 85, JpegSubsampling.Yuv420, true);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(64u);
            await Assert.That(decoded.Rows).IsEqualTo(64u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ─── Edge Case Tests ─────────────────────────────────────────

    [Test]
    public async Task Write_TinyImage_1x1()
    {
        var image = CreateGradientImage(1, 1);
        string path = TempPath("tiny_1x1.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(1u);
            await Assert.That(decoded.Rows).IsEqualTo(1u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_TinyImage_8x8()
    {
        var image = CreateGradientImage(8, 8);
        string path = TempPath("tiny_8x8.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(8u);
            await Assert.That(decoded.Rows).IsEqualTo(8u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_NonSquare_Image()
    {
        var image = CreateGradientImage(200, 50);
        string path = TempPath("nonsquare.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(200u);
            await Assert.That(decoded.Rows).IsEqualTo(50u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_LargeImage_Yuv420()
    {
        var image = CreateGradientImage(512, 512);
        string path = TempPath("large_420.jpg");
        try
        {
            JpegCoder.Write(image, path, 85, JpegSubsampling.Yuv420);
            var decoded = JpegCoder.Read(path);
            await Assert.That(decoded.Columns).IsEqualTo(512u);
            await Assert.That(decoded.Rows).IsEqualTo(512u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task Write_AllQualityLevels_SubsamplingCombinations()
    {
        var image = CreateGradientImage(32, 32);
        var subsamplings = new[] { JpegSubsampling.Yuv444, JpegSubsampling.Yuv422, JpegSubsampling.Yuv420 };
        int[] qualities = [10, 50, 100];

        foreach (var sub in subsamplings)
        {
            foreach (int q in qualities)
            {
                string path = TempPath($"combo_{sub}_{q}.jpg");
                try
                {
                    JpegCoder.Write(image, path, q, sub);
                    var decoded = JpegCoder.Read(path);
                    await Assert.That(decoded.Columns).IsEqualTo(32u);
                    await Assert.That(decoded.Rows).IsEqualTo(32u);
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static double ComputeAveragePixelDifference(ImageFrame original, ImageFrame decoded)
    {
        int width = (int)Math.Min(original.Columns, decoded.Columns);
        int height = (int)Math.Min(original.Rows, decoded.Rows);
        int origChannels = original.NumberOfChannels;
        int decChannels = decoded.NumberOfChannels;
        long totalDiff = 0;
        long pixelCount = 0;

        for (int y = 0; y < height; y++)
        {
            var origRow = original.GetPixelRow(y);
            var decRow = decoded.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < Math.Min(origChannels, decChannels); c++)
                {
                    byte origVal = Quantum.ScaleToByte(origRow[x * origChannels + c]);
                    byte decVal = Quantum.ScaleToByte(decRow[x * decChannels + c]);
                    totalDiff += Math.Abs(origVal - decVal);
                    pixelCount++;
                }
            }
        }

        return pixelCount > 0 ? (double)totalDiff / pixelCount : 0;
    }

    private static double ComputeAverageGrayDifference(ImageFrame original, ImageFrame decoded)
    {
        int width = (int)Math.Min(original.Columns, decoded.Columns);
        int height = (int)Math.Min(original.Rows, decoded.Rows);
        long totalDiff = 0;
        int pixelCount = 0;

        for (int y = 0; y < height; y++)
        {
            var origRow = original.GetPixelRow(y);
            var decRow = decoded.GetPixelRow(y);
            int origCh = original.NumberOfChannels;
            int decCh = decoded.NumberOfChannels;

            for (int x = 0; x < width; x++)
            {
                byte origVal = Quantum.ScaleToByte(origRow[x * origCh]);
                byte decVal = Quantum.ScaleToByte(decRow[x * decCh]);
                totalDiff += Math.Abs(origVal - decVal);
                pixelCount++;
            }
        }

        return pixelCount > 0 ? (double)totalDiff / pixelCount : 0;
    }
}
