using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.IO;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for BMP format coder.
/// Uses round-trip write→read validation.
/// </summary>
public class BmpCoderTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // --- 24-bit round-trip ---

    [Test]
    public async Task Bmp24_RoundTrip_PreservesPixels()
    {
        var original = CreateGradientImage(20, 15);
        string tempFile = TempPath("roundtrip24.bmp");

        try
        {
            BmpCoder.Write(original, tempFile);
            var loaded = BmpCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(20);
            await Assert.That(rows).IsEqualTo(15);

            // BMP uses 8-bit per channel, so tolerance = 1 byte worth of quantum
            double tolerance = Quantum.MaxValue / 255.0 + 1.0;
            await AssertPixelsMatch(original, loaded, tolerance);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- 32-bit with alpha round-trip ---

    [Test]
    public async Task Bmp32_RoundTrip_PreservesAlpha()
    {
        var original = CreateGradientImageWithAlpha(16, 12);
        string tempFile = TempPath("roundtrip32.bmp");

        try
        {
            BmpCoder.Write(original, tempFile, includeAlpha: true);
            var loaded = BmpCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(16);
            await Assert.That(rows).IsEqualTo(12);

            // Check alpha is preserved
            bool hasAlpha = loaded.HasAlpha;
            await Assert.That(hasAlpha).IsTrue();

            double tolerance = Quantum.MaxValue / 255.0 + 1.0;
            await AssertPixelsMatch(original, loaded, tolerance);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- Row padding (non-multiple-of-4 width) ---

    [Test]
    public async Task Bmp24_OddWidth_RowPaddingHandledCorrectly()
    {
        var original = CreateGradientImage(13, 7); // 13*3 = 39 bytes, padded to 40
        string tempFile = TempPath("padding.bmp");

        try
        {
            BmpCoder.Write(original, tempFile);
            var loaded = BmpCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(13);
            await Assert.That(rows).IsEqualTo(7);

            double tolerance = Quantum.MaxValue / 255.0 + 1.0;
            await AssertPixelsMatch(original, loaded, tolerance);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- Single pixel ---

    [Test]
    public async Task Bmp_SinglePixel_RoundTrips()
    {
        var original = new ImageFrame();
        original.Initialize(1, 1, ColorspaceType.SRGB, false);
        var row = original.GetPixelRowForWrite(0);
        row[0] = 32768; row[1] = 16384; row[2] = 49152; // ~128, 64, 192

        string tempFile = TempPath("single.bmp");
        try
        {
            BmpCoder.Write(original, tempFile);
            var loaded = BmpCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(1);
            await Assert.That(rows).IsEqualTo(1);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- Pure black / pure white ---

    [Test]
    public async Task Bmp_PureBlack_RoundTrips()
    {
        var original = CreateSolidImage(8, 8, 0, 0, 0);
        string tempFile = TempPath("black.bmp");

        try
        {
            BmpCoder.Write(original, tempFile);
            var loaded = BmpCoder.Read(tempFile);

            ushort r = loaded.GetPixelChannel(0, 0, 0);
            ushort g = loaded.GetPixelChannel(0, 0, 1);
            ushort b = loaded.GetPixelChannel(0, 0, 2);
            await Assert.That((int)r).IsEqualTo(0);
            await Assert.That((int)g).IsEqualTo(0);
            await Assert.That((int)b).IsEqualTo(0);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    [Test]
    public async Task Bmp_PureWhite_RoundTrips()
    {
        var original = CreateSolidImage(8, 8, Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue);
        string tempFile = TempPath("white.bmp");

        try
        {
            BmpCoder.Write(original, tempFile);
            var loaded = BmpCoder.Read(tempFile);

            ushort r = loaded.GetPixelChannel(0, 0, 0);
            ushort g = loaded.GetPixelChannel(0, 0, 1);
            ushort b = loaded.GetPixelChannel(0, 0, 2);
            await Assert.That((int)r).IsEqualTo(Quantum.MaxValue);
            await Assert.That((int)g).IsEqualTo(Quantum.MaxValue);
            await Assert.That((int)b).IsEqualTo(Quantum.MaxValue);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- Format detection ---

    [Test]
    public async Task FormatDetector_DetectsBmp()
    {
        byte[] bmpHeader = [0x42, 0x4D, 0x00, 0x00, 0x00, 0x00]; // "BM"
        var format = FormatDetector.Detect(bmpHeader);
        await Assert.That(format).IsEqualTo(FormatDetector.ImageFormat.Bmp);
    }

    [Test]
    public async Task FormatDetector_DetectsBmpExtension()
    {
        await Assert.That(FormatDetector.DetectFromExtension("test.bmp")).IsEqualTo(FormatDetector.ImageFormat.Bmp);
        await Assert.That(FormatDetector.DetectFromExtension("test.dib")).IsEqualTo(FormatDetector.ImageFormat.Bmp);
    }

    // --- File header validation ---

    [Test]
    public async Task Bmp_WrittenFile_HasCorrectMagic()
    {
        var original = CreateGradientImage(4, 4);
        string tempFile = TempPath("magic.bmp");

        try
        {
            BmpCoder.Write(original, tempFile);
            byte[] bytes = File.ReadAllBytes(tempFile);

            await Assert.That((int)bytes[0]).IsEqualTo(0x42); // 'B'
            await Assert.That((int)bytes[1]).IsEqualTo(0x4D); // 'M'

            // Check pixel data offset is reasonable
            uint dataOffset = BitConverter.ToUInt32(bytes, 10);
            await Assert.That((long)dataOffset).IsGreaterThanOrEqualTo(FileHeaderAndDibSize());
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- Helpers ---

    private static long FileHeaderAndDibSize() => 14 + 40; // minimum

    private static ImageFrame CreateGradientImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 3;
                row[offset] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));
                row[offset + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1));
                row[offset + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientImageWithAlpha(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                row[offset] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));
                row[offset + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1));
                row[offset + 2] = (ushort)(Quantum.MaxValue / 3);
                row[offset + 3] = (ushort)((x + y) * Quantum.MaxValue / Math.Max(1, width + height - 2));
            }
        }
        return frame;
    }

    private static ImageFrame CreateSolidImage(int width, int height, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 3;
                row[offset] = r; row[offset + 1] = g; row[offset + 2] = b;
            }
        }
        return frame;
    }

    private static async Task AssertPixelsMatch(ImageFrame a, ImageFrame b, double tolerance)
    {
        int chA = a.NumberOfChannels;
        int chB = b.NumberOfChannels;
        int channels = Math.Min(chA, chB);

        for (int y = 0; y < Math.Min(a.Rows, b.Rows); y++)
        {
            var rowAArr = a.GetPixelRow(y).ToArray();
            var rowBArr = b.GetPixelRow(y).ToArray();
            for (int x = 0; x < Math.Min(a.Columns, b.Columns); x++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    ushort valA = rowAArr[x * chA + ch];
                    ushort valB = rowBArr[x * chB + ch];
                    double diff = Math.Abs((double)valA - valB);
                    await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
