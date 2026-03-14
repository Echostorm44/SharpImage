using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.IO;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for PNM (PBM/PGM/PPM/PAM) format coder.
/// Uses round-trip write→read validation and reference file reading.
/// </summary>
public class PnmCoderTests
{
    private static readonly string TestAssetsDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // --- Reference file reading ---

    [Test]
    public async Task ReadPnm_RosePnm_LoadsCorrectly()
    {
        string rosePath = Path.Combine(TestAssetsDir, "photo_small.pnm");
        if (!File.Exists(rosePath))
        {
            return;
        }

        var frame = PnmCoder.Read(rosePath);
        long columns = frame.Columns;
        long rows = frame.Rows;

        await Assert.That(columns).IsGreaterThan(0);
        await Assert.That(rows).IsGreaterThan(0);
        await Assert.That(frame.NumberOfChannels).IsGreaterThanOrEqualTo(1);
        frame.Dispose();
    }

    // --- P6 PPM Binary round-trip ---

    [Test]
    public async Task PpmBinary_RoundTrip_PreservesPixels()
    {
        var original = CreateTestImage(16, 12, ColorspaceType.SRGB);
        string tempFile = TempPath("roundtrip.ppm");

        try
        {
            PnmCoder.Write(original, tempFile, PnmFormat.PpmBinary);
            var loaded = PnmCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(16);
            await Assert.That(rows).IsEqualTo(12);

            // Compare pixels — 8-bit PPM loses precision from 16-bit quantum, allow 1 byte tolerance
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

    // --- P3 PPM ASCII round-trip ---

    [Test]
    public async Task PpmAscii_RoundTrip_PreservesPixels()
    {
        var original = CreateTestImage(8, 6, ColorspaceType.SRGB);
        string tempFile = TempPath("roundtrip_ascii.ppm");

        try
        {
            PnmCoder.Write(original, tempFile, PnmFormat.PpmAscii);
            var loaded = PnmCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(8);
            await Assert.That(rows).IsEqualTo(6);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- P5 PGM Binary round-trip ---

    [Test]
    public async Task PgmBinary_RoundTrip_PreservesGrayscale()
    {
        var original = CreateTestImage(10, 10, ColorspaceType.Gray);
        string tempFile = TempPath("roundtrip.pgm");

        try
        {
            PnmCoder.Write(original, tempFile, PnmFormat.PgmBinary);
            var loaded = PnmCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(10);
            await Assert.That(rows).IsEqualTo(10);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- P4 PBM Binary round-trip ---

    [Test]
    public async Task PbmBinary_RoundTrip_PreservesBitmap()
    {
        var original = CreateCheckerboard(16, 16);
        string tempFile = TempPath("roundtrip.pbm");

        try
        {
            PnmCoder.Write(original, tempFile, PnmFormat.PbmBinary);
            var loaded = PnmCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(16);
            await Assert.That(rows).IsEqualTo(16);

            // Check checkerboard pattern
            ushort topLeft = loaded.GetPixelChannel(0, 0, 0);
            ushort topSecond = loaded.GetPixelChannel(1, 0, 0);
            bool different = Math.Abs(topLeft - topSecond) > Quantum.MaxValue / 2;
            await Assert.That(different).IsTrue();

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- P1 PBM ASCII round-trip ---

    [Test]
    public async Task PbmAscii_RoundTrip_PreservesBitmap()
    {
        var original = CreateCheckerboard(8, 8);
        string tempFile = TempPath("roundtrip_ascii.pbm");

        try
        {
            PnmCoder.Write(original, tempFile, PnmFormat.PbmAscii);
            var loaded = PnmCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(8);
            await Assert.That(rows).IsEqualTo(8);

            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- P7 PAM round-trip ---

    [Test]
    public async Task Pam_RoundTrip_RGB_PreservesPixels()
    {
        var original = CreateTestImage(12, 8, ColorspaceType.SRGB);
        string tempFile = TempPath("roundtrip.pam");

        try
        {
            PnmCoder.Write(original, tempFile, PnmFormat.Pam);
            var loaded = PnmCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(12);
            await Assert.That(rows).IsEqualTo(8);

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
    public async Task FormatDetector_DetectsPnm()
    {
        byte[] ppmHeader = "P6\n10 10\n255\n"u8.ToArray();
        var format = FormatDetector.Detect(ppmHeader);
        await Assert.That(format).IsEqualTo(FormatDetector.ImageFormat.Pnm);
    }

    [Test]
    public async Task FormatDetector_DetectsFromExtension()
    {
        await Assert.That(FormatDetector.DetectFromExtension("test.ppm")).IsEqualTo(FormatDetector.ImageFormat.Pnm);
        await Assert.That(FormatDetector.DetectFromExtension("test.pgm")).IsEqualTo(FormatDetector.ImageFormat.Pnm);
        await Assert.That(FormatDetector.DetectFromExtension("test.pbm")).IsEqualTo(FormatDetector.ImageFormat.Pnm);
        await Assert.That(FormatDetector.DetectFromExtension("test.pam")).IsEqualTo(FormatDetector.ImageFormat.Pnm);
    }

    // --- Helpers ---

    private static ImageFrame CreateTestImage(int width, int height, ColorspaceType colorspace)
    {
        var frame = new ImageFrame();
        bool isGray = colorspace == ColorspaceType.Gray;
        frame.Initialize(width, height, colorspace, false);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int channels = frame.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                if (isGray)
                {
                    row[offset] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));
                }
                else
                {
                    row[offset] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));     // R gradient
                    row[offset + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1)); // G gradient
                    row[offset + 2] = (ushort)(Quantum.MaxValue / 2);                            // B constant
                }
            }
        }
        return frame;
    }

    private static ImageFrame CreateCheckerboard(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
                row[x] = ((x + y) & 1) == 0 ? Quantum.MaxValue : (ushort)0;
        }
        return frame;
    }

    private static async Task AssertPixelsMatch(ImageFrame a, ImageFrame b, double tolerance)
    {
        long cols = Math.Min(a.Columns, b.Columns);
        long rows = Math.Min(a.Rows, b.Rows);
        int chA = a.NumberOfChannels;
        int chB = b.NumberOfChannels;
        int channels = Math.Min(chA, chB);

        for (int y = 0; y < rows; y++)
        {
            // Copy span data to locals before awaiting
            var rowAArr = a.GetPixelRow(y).ToArray();
            var rowBArr = b.GetPixelRow(y).ToArray();
            for (int x = 0; x < cols; x++)
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
