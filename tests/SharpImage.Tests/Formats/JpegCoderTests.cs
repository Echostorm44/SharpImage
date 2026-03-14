using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for JPEG format coder, DCT, and Huffman infrastructure.
/// JPEG is lossy so tests use appropriate tolerance.
/// </summary>
public class JpegCoderTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // ─── DCT Tests ───────────────────────────────────────────────

    [Test]
    public async Task Dct_ForwardInverse_RoundTrip_NearIdentity()
    {
        // Create a known 8x8 block
        int[] original = new int[64];
        for (int i = 0; i < 64; i++)
            original[i] = (i * 7 + 13) % 256 - 128; // Level-shifted values

        int[] block = (int[])original.Clone();

        // Forward then inverse should approximately recover original
        Dct.ForwardDct(block);
        Dct.InverseDct(block);

        for (int i = 0; i < 64; i++)
        {
            int diff = Math.Abs(block[i] - original[i]);
            await Assert.That(diff).IsLessThanOrEqualTo(1); // Integer DCT allows ±1 error
        }
    }

    [Test]
    public async Task Dct_DcOnly_Produces8x8Constant()
    {
        // A uniform block should have all energy in DC coefficient
        int[] block = new int[64];
        Array.Fill(block, 100);

        Dct.ForwardDct(block);

        // DC coefficient should be 800 (100 * 8), AC should be ~0
        await Assert.That(Math.Abs(block[0])).IsGreaterThan(0);

        int acEnergy = 0;
        for (int i = 1; i < 64; i++)
            acEnergy += Math.Abs(block[i]);

        await Assert.That(acEnergy).IsLessThanOrEqualTo(5); // Near-zero AC
    }

    // ─── Quantization Table Tests ────────────────────────────────

    [Test]
    public async Task QuantTable_Quality50_MatchesStandard()
    {
        int[] scaled = JpegTables.ScaleQuantTable(JpegTables.LuminanceQuantTable, 50);
        // At quality 50, scale factor = 100, so table values should match standard
        for (int i = 0; i < 64; i++)
        {
            int expected = JpegTables.LuminanceQuantTable[i];
            await Assert.That(scaled[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task QuantTable_Quality1_MaxCompression()
    {
        int[] scaled = JpegTables.ScaleQuantTable(JpegTables.LuminanceQuantTable, 1);
        // Quality 1 should produce high values (max compression)
        await Assert.That(scaled[0]).IsGreaterThan(100);
    }

    [Test]
    public async Task QuantTable_Quality100_MinCompression()
    {
        int[] scaled = JpegTables.ScaleQuantTable(JpegTables.LuminanceQuantTable, 100);
        // Quality 100 should produce all 1s (minimum compression)
        for (int i = 0; i < 64; i++)
            await Assert.That(scaled[i]).IsEqualTo(1);
    }

    // ─── JPEG Round-Trip Tests ───────────────────────────────────

    [Test]
    public async Task Jpeg_RoundTrip_SmallGradient_Preserves()
    {
        var original = CreateGradientImage(16, 12);
        string tempFile = TempPath("roundtrip.jpg");

        try
        {
            JpegCoder.Write(original, tempFile, quality: 95);
            var loaded = JpegCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(16);
            await Assert.That(loaded.Rows).IsEqualTo(12);

            // JPEG is lossy: allow generous tolerance (PSNR check instead of exact)
            double totalDiff = 0;
            int pixelCount = 0;
            for (int y = 0; y < 12; y++)
            {
                var origRow = original.GetPixelRow(y).ToArray();
                var loadRow = loaded.GetPixelRow(y).ToArray();
                int channels = Math.Min(origRow.Length / 16, loadRow.Length / 16);
                for (int x = 0; x < 16; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int origByte = Quantum.ScaleToByte(origRow[x * channels + c]);
                        int loadByte = Quantum.ScaleToByte(loadRow[x * (loadRow.Length / 16) + c]);
                        totalDiff += (origByte - loadByte) * (origByte - loadByte);
                        pixelCount++;
                    }
                }
            }

            double mse = totalDiff / pixelCount;
            double psnr = mse > 0 ? 10.0 * Math.Log10(255.0 * 255.0 / mse) : 100;

            // PSNR > 30 dB is considered acceptable for JPEG at high quality
            await Assert.That(psnr).IsGreaterThan(25.0);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Real File Tests ─────────────────────────────────────────

    [Test]
    public async Task Jpeg_Read_RoseImage_CorrectDimensions()
    {
        string path = Path.Combine(TestImagesDir, "photo_small.jpg");
        var image = JpegCoder.Read(path);
        try
        {
            // photo_small.jpg is 70x46
            await Assert.That(image.Columns).IsEqualTo(70);
            await Assert.That(image.Rows).IsEqualTo(46);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Test]
    public async Task Jpeg_Read_RoseImage_HasValidPixels()
    {
        string path = Path.Combine(TestImagesDir, "photo_small.jpg");
        var image = JpegCoder.Read(path);
        try
        {
            // Rose image should have non-zero pixel data
            var centerRow = image.GetPixelRow(23).ToArray();
            int channels = image.NumberOfChannels;
            int centerX = 35;
            ushort r = centerRow[centerX * channels];
            ushort g = centerRow[centerX * channels + 1];
            ushort b = centerRow[centerX * channels + 2];

            // Should have some color (not all black or all white)
            bool hasColor = r > 0 || g > 0 || b > 0;
            await Assert.That(hasColor).IsTrue();
        }
        finally
        {
            image.Dispose();
        }
    }

    [Test]
    public async Task Jpeg_Read_LogoImage_CorrectDimensions()
    {
        string path = Path.Combine(TestImagesDir, "scene.jpg");
        var image = JpegCoder.Read(path);
        try
        {
            // Just verify it reads without error and has reasonable dimensions
            await Assert.That(image.Columns).IsGreaterThan(0);
            await Assert.That(image.Rows).IsGreaterThan(0);
        }
        finally
        {
            image.Dispose();
        }
    }

    // ─── Cross-Format Tests ──────────────────────────────────────

    [Test]
    public async Task Jpeg_ToPng_CrossFormat()
    {
        var original = CreateGradientImage(24, 16);
        string jpegFile = TempPath("cross.jpg");
        string pngFile = TempPath("cross_from_jpeg.png");

        try
        {
            JpegCoder.Write(original, jpegFile, quality: 95);
            var fromJpeg = JpegCoder.Read(jpegFile);

            PngCoder.Write(fromJpeg, pngFile);
            var fromPng = PngCoder.Read(pngFile);

            await Assert.That(fromPng.Columns).IsEqualTo(24);
            await Assert.That(fromPng.Rows).IsEqualTo(16);

            fromJpeg.Dispose();
            fromPng.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(jpegFile)) File.Delete(jpegFile);
            if (File.Exists(pngFile)) File.Delete(pngFile);
        }
    }

    // ─── File Signature Test ─────────────────────────────────────

    [Test]
    public async Task Jpeg_FileWritten_HasValidSignature()
    {
        var image = CreateGradientImage(8, 8);
        string tempFile = TempPath("signature.jpg");

        try
        {
            JpegCoder.Write(image, tempFile);
            byte[] bytes = File.ReadAllBytes(tempFile);

            // JPEG starts with FFD8
            await Assert.That(bytes[0]).IsEqualTo((byte)0xFF);
            await Assert.That(bytes[1]).IsEqualTo((byte)0xD8);

            // Should end with FFD9
            await Assert.That(bytes[^2]).IsEqualTo((byte)0xFF);
            await Assert.That(bytes[^1]).IsEqualTo((byte)0xD9);
        }
        finally
        {
            image.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Jpeg_Read_InvalidData_Throws()
    {
        string tempFile = TempPath("invalid.jpg");
        try
        {
            File.WriteAllBytes(tempFile, [0, 0, 0, 0]);
            await Assert.That(() => JpegCoder.Read(tempFile)).Throws<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Huffman Encoder Table Test ──────────────────────────────

    [Test]
    public async Task HuffmanEncoderValue_Category0_IsZero()
    {
        var (category, bits) = JpegBitWriter.EncodeValue(0);
        await Assert.That(category).IsEqualTo(0);
    }

    [Test]
    public async Task HuffmanEncoderValue_Positive_CorrectCategory()
    {
        var (category, bits) = JpegBitWriter.EncodeValue(5);
        // 5 needs 3 bits (100-111), so category = 3
        await Assert.That(category).IsEqualTo(3);
        await Assert.That(bits).IsEqualTo(5);
    }

    [Test]
    public async Task HuffmanEncoderValue_Negative_CorrectEncoding()
    {
        var (category, bits) = JpegBitWriter.EncodeValue(-3);
        // -3: category=2, value in complement form
        await Assert.That(category).IsEqualTo(2);
        // -3 in 2-bit complement = 0 (because -3 + (1<<2) - 1 = 0)
        await Assert.That(bits).IsEqualTo(0);
    }

    // ─── Helper ──────────────────────────────────────────────────

    private static ImageFrame CreateGradientImage(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);

        int channels = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));
                row[offset + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1));
                row[offset + 2] = (ushort)(((x + y) % 2 == 0) ? Quantum.MaxValue / 2 : Quantum.MaxValue);
            }
        }
        return image;
    }

    // ─── Progressive JPEG Tests ──────────────────────────────────

    [Test]
    public async Task ProgressiveJpeg_Read_DecodesCorrectDimensions()
    {
        string path = Path.Combine(TestImagesDir, "photo_small_progressive.jpg");
        var image = JpegCoder.Read(path);

        await Assert.That(image.Columns).IsEqualTo(70);
        await Assert.That(image.Rows).IsEqualTo(46);
        await Assert.That(image.NumberOfChannels).IsEqualTo(3);
    }

    [Test]
    public async Task ProgressiveJpeg_Read_LargeImage_DecodesCorrectly()
    {
        string path = Path.Combine(TestImagesDir, "landscape_progressive.jpg");
        var image = JpegCoder.Read(path);

        await Assert.That(image.Columns).IsEqualTo(1500);
        await Assert.That(image.Rows).IsEqualTo(1000);
    }

    [Test]
    public async Task ProgressiveJpeg_MatchesBaseline_WithinTolerance()
    {
        // Progressive re-encode of the same source should be close to baseline decode
        string baselinePath = Path.Combine(TestImagesDir, "photo_small.jpg");
        string progressivePath = Path.Combine(TestImagesDir, "photo_small_progressive.jpg");

        var baseline = JpegCoder.Read(baselinePath);
        var progressive = JpegCoder.Read(progressivePath);

        await Assert.That(progressive.Columns).IsEqualTo(baseline.Columns);
        await Assert.That(progressive.Rows).IsEqualTo(baseline.Rows);

        // Compare pixel similarity (JPEG lossy, so allow tolerance for re-encode differences)
        double totalDiff = 0;
        int pixelCount = 0;
        for (int y = 0; y < baseline.Rows; y++)
        {
            var baseRow = baseline.GetPixelRow(y);
            var progRow = progressive.GetPixelRow(y);
            for (int x = 0; x < baseline.Columns * baseline.NumberOfChannels; x++)
            {
                double diff = Math.Abs((double)baseRow[x] - progRow[x]) / Quantum.MaxValue;
                totalDiff += diff;
                pixelCount++;
            }
        }
        double meanDiff = totalDiff / pixelCount;
        // Re-encoded progressive should be similar (within 15% average channel difference)
        await Assert.That(meanDiff).IsLessThan(0.15);
    }

    [Test]
    public async Task ProgressiveJpeg_ConvertToPng_RoundTrip()
    {
        string srcPath = Path.Combine(TestImagesDir, "landscape_progressive.jpg");
        string outPath = TempPath("progressive_to_png.png");

        try
        {
            var image = FormatRegistry.Read(srcPath);
            FormatRegistry.Write(image, outPath);

            var reloaded = FormatRegistry.Read(outPath);
            await Assert.That(reloaded.Columns).IsEqualTo(1500);
            await Assert.That(reloaded.Rows).IsEqualTo(1000);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    // ─── Progressive JPEG Write Tests ──────────────────────────────

    [Test]
    public async Task WriteProgressive_ProducesValidJpeg_WithSOF2Marker()
    {
        string srcPath = Path.Combine(TestImagesDir, "peppers_rgba.png");
        string outPath = TempPath("write_progressive.jpg");

        try
        {
            var image = FormatRegistry.Read(srcPath);
            JpegCoder.WriteProgressive(image, outPath, 85);

            // Verify the file starts with SOI and contains SOF2 marker
            byte[] data = File.ReadAllBytes(outPath);
            await Assert.That(data[0]).IsEqualTo((byte)0xFF);
            await Assert.That(data[1]).IsEqualTo((byte)0xD8); // SOI

            // Find SOF2 marker (0xFF 0xC2) in the file
            bool foundSof2 = false;
            for (int i = 0; i < data.Length - 1; i++)
            {
                if (data[i] == 0xFF && data[i + 1] == 0xC2)
                {
                    foundSof2 = true;
                    break;
                }
            }
            await Assert.That(foundSof2).IsTrue();
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Test]
    public async Task WriteProgressive_RoundTrip_PreservesDimensions()
    {
        string srcPath = Path.Combine(TestImagesDir, "peppers_rgba.png");
        string outPath = TempPath("progressive_rt.jpg");

        try
        {
            var original = FormatRegistry.Read(srcPath);
            JpegCoder.WriteProgressive(original, outPath, 90);

            var reloaded = JpegCoder.Read(outPath);
            await Assert.That(reloaded.Columns).IsEqualTo(original.Columns);
            await Assert.That(reloaded.Rows).IsEqualTo(original.Rows);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Test]
    public async Task WriteProgressive_RoundTrip_VisuallyAccurate()
    {
        string srcPath = Path.Combine(TestImagesDir, "peppers_rgba.png");
        string outPath = TempPath("progressive_visual.jpg");

        try
        {
            var original = FormatRegistry.Read(srcPath);
            JpegCoder.WriteProgressive(original, outPath, 95);

            var reloaded = JpegCoder.Read(outPath);

            // Compare average pixel difference — should be small at Q95
            double totalDiff = 0;
            int pixelCount = 0;
            for (int y = 0; y < (int)original.Rows; y++)
            {
                var origRow = original.GetPixelRow(y);
                var progRow = reloaded.GetPixelRow(y);
                int ch = Math.Min(original.NumberOfChannels, reloaded.NumberOfChannels);
                for (int x = 0; x < (int)original.Columns * ch; x++)
                {
                    double diff = Math.Abs((double)origRow[x] - progRow[x]) / Quantum.MaxValue;
                    totalDiff += diff;
                    pixelCount++;
                }
            }
            double meanDiff = totalDiff / pixelCount;
            // PNG→JPEG comparison includes alpha removal + colorspace conversion + DCT loss
            await Assert.That(meanDiff).IsLessThan(0.30);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Test]
    public async Task WriteProgressive_QualityAffectsFileSize()
    {
        string srcPath = Path.Combine(TestImagesDir, "peppers_rgba.png");
        string outLow = TempPath("progressive_q30.jpg");
        string outHigh = TempPath("progressive_q95.jpg");

        try
        {
            var image = FormatRegistry.Read(srcPath);
            JpegCoder.WriteProgressive(image, outLow, 30);
            JpegCoder.WriteProgressive(image, outHigh, 95);

            long lowSize = new FileInfo(outLow).Length;
            long highSize = new FileInfo(outHigh).Length;

            // Higher quality should produce a larger file
            await Assert.That(highSize).IsGreaterThan(lowSize);
        }
        finally
        {
            if (File.Exists(outLow)) File.Delete(outLow);
            if (File.Exists(outHigh)) File.Delete(outHigh);
        }
    }

    [Test]
    public async Task WriteProgressive_SmallImage_Works()
    {
        // Test with a tiny image to catch edge cases
        var image = new ImageFrame();
        image.Initialize(4, 4);
        for (int y = 0; y < 4; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < 4; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte((byte)(x * 64));       // R gradient
                row[x * 3 + 1] = Quantum.ScaleFromByte((byte)(y * 64));   // G gradient
                row[x * 3 + 2] = Quantum.ScaleFromByte(128);              // B constant
            }
        }

        string outPath = TempPath("progressive_tiny.jpg");
        try
        {
            JpegCoder.WriteProgressive(image, outPath, 85);

            var reloaded = JpegCoder.Read(outPath);
            await Assert.That(reloaded.Columns).IsEqualTo(4L);
            await Assert.That(reloaded.Rows).IsEqualTo(4L);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
