using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.IO;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for TGA format coder.
/// Uses round-trip write→read validation.
/// </summary>
public class TgaCoderTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // --- 24-bit round-trip ---

    [Test]
    public async Task Tga24_RoundTrip_PreservesPixels()
    {
        var original = CreateGradientImage(16, 12);
        string tempFile = TempPath("roundtrip24.tga");

        try
        {
            TgaCoder.Write(original, tempFile);
            var loaded = TgaCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(16);
            await Assert.That(rows).IsEqualTo(12);

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
    public async Task Tga32_RoundTrip_PreservesAlpha()
    {
        var original = CreateGradientImageWithAlpha(12, 10);
        string tempFile = TempPath("roundtrip32.tga");

        try
        {
            TgaCoder.Write(original, tempFile, includeAlpha: true);
            var loaded = TgaCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(12);
            await Assert.That(rows).IsEqualTo(10);

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

    // --- Grayscale round-trip ---

    [Test]
    public async Task TgaGrayscale_RoundTrip_PreservesPixels()
    {
        var original = CreateGrayscaleImage(10, 10);
        string tempFile = TempPath("roundtrip_gray.tga");

        try
        {
            TgaCoder.Write(original, tempFile);
            var loaded = TgaCoder.Read(tempFile);

            long cols = loaded.Columns;
            long rows = loaded.Rows;
            await Assert.That(cols).IsEqualTo(10);
            await Assert.That(rows).IsEqualTo(10);

            // Grayscale — compare first channel
            double tolerance = Quantum.MaxValue / 255.0 + 1.0;
            for (int y = 0; y < 10; y++)
            {
                var rowOrigArr = original.GetPixelRow(y).ToArray();
                var rowLoadArr = loaded.GetPixelRow(y).ToArray();
                for (int x = 0; x < 10; x++)
                {
                    double diff = Math.Abs((double)rowOrigArr[x] - rowLoadArr[x]);
                    await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                }
            }

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
    public async Task Tga_SinglePixel_RoundTrips()
    {
        var original = new ImageFrame();
        original.Initialize(1, 1, ColorspaceType.SRGB, false);
        var row = original.GetPixelRowForWrite(0);
        row[0] = 32768; row[1] = 16384; row[2] = 49152;

        string tempFile = TempPath("single.tga");
        try
        {
            TgaCoder.Write(original, tempFile);
            var loaded = TgaCoder.Read(tempFile);

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

    // --- Pure colors ---

    [Test]
    public async Task Tga_PureRed_PreservesColor()
    {
        var original = CreateSolidImage(4, 4, Quantum.MaxValue, 0, 0);
        string tempFile = TempPath("red.tga");

        try
        {
            TgaCoder.Write(original, tempFile);
            var loaded = TgaCoder.Read(tempFile);

            ushort r = loaded.GetPixelChannel(0, 0, 0);
            ushort g = loaded.GetPixelChannel(0, 0, 1);
            ushort b = loaded.GetPixelChannel(0, 0, 2);
            await Assert.That((int)r).IsEqualTo(Quantum.MaxValue);
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

    // --- Footer validation ---

    [Test]
    public async Task Tga_WrittenFile_HasTga2Footer()
    {
        var original = CreateGradientImage(4, 4);
        string tempFile = TempPath("footer.tga");

        try
        {
            TgaCoder.Write(original, tempFile);
            byte[] bytes = File.ReadAllBytes(tempFile);

            // Last 18 bytes should be "TRUEVISION-XFILE."
            string footer = System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 18, 18);
            await Assert.That(footer).IsEqualTo("TRUEVISION-XFILE.\0");
        }
        finally
        {
            original.Dispose();
            TryDelete(tempFile);
        }
    }

    // --- Format detection ---

    [Test]
    public async Task FormatDetector_DetectsTgaExtension()
    {
        await Assert.That(FormatDetector.DetectFromExtension("test.tga")).IsEqualTo(FormatDetector.ImageFormat.Tga);
    }

    // --- Helpers ---

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

    private static ImageFrame CreateGrayscaleImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
                row[x] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));
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
