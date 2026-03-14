using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.IO;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Cross-format tests: write in one format, read back, write in another, verify pixels survive.
/// Also tests FormatDetector with real files.
/// </summary>
public class CrossFormatTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_xformat_{name}");

    [Test]
    public async Task Ppm_To_Bmp_To_Tga_PreservesPixels()
    {
        string ppmFile = TempPath("cross.ppm");
        string bmpFile = TempPath("cross.bmp");
        string tgaFile = TempPath("cross.tga");

        var original = CreateTestImage(10, 8);
        try
        {
            // Write PPM → Read → Write BMP → Read → Write TGA → Read
            PnmCoder.Write(original, ppmFile, PnmFormat.PpmBinary);
            var fromPpm = PnmCoder.Read(ppmFile);

            BmpCoder.Write(fromPpm, bmpFile);
            fromPpm.Dispose();
            var fromBmp = BmpCoder.Read(bmpFile);

            TgaCoder.Write(fromBmp, tgaFile);
            fromBmp.Dispose();
            var fromTga = TgaCoder.Read(tgaFile);

            // All three formats use 8-bit per channel, so double quantization error
            double tolerance = 2.0 * Quantum.MaxValue / 255.0 + 2.0;

            long cols = fromTga.Columns;
            long rows = fromTga.Rows;
            await Assert.That(cols).IsEqualTo(10);
            await Assert.That(rows).IsEqualTo(8);

            int chOrig = original.NumberOfChannels;
            int chTga = fromTga.NumberOfChannels;
            int channels = Math.Min(chOrig, chTga);

            for (int y = 0; y < Math.Min(original.Rows, fromTga.Rows); y++)
            {
                var rowOrig = original.GetPixelRow(y).ToArray();
                var rowTga = fromTga.GetPixelRow(y).ToArray();
                for (int x = 0; x < Math.Min(original.Columns, fromTga.Columns); x++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        ushort valOrig = rowOrig[x * chOrig + ch];
                        ushort valTga = rowTga[x * chTga + ch];
                        double diff = Math.Abs((double)valOrig - valTga);
                        await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                    }
                }
            }

            fromTga.Dispose();
        }
        finally
        {
            original.Dispose();
            TryDelete(ppmFile);
            TryDelete(bmpFile);
            TryDelete(tgaFile);
        }
    }

    [Test]
    public async Task FormatDetector_DetectsWrittenBmpFile()
    {
        var image = CreateTestImage(4, 4);
        string path = TempPath("detect.bmp");
        try
        {
            BmpCoder.Write(image, path);
            byte[] header = new byte[16];
            using (var fs = File.OpenRead(path))
                fs.ReadExactly(header);

            var format = FormatDetector.Detect(header);
            await Assert.That(format).IsEqualTo(FormatDetector.ImageFormat.Bmp);
        }
        finally
        {
            image.Dispose();
            TryDelete(path);
        }
    }

    [Test]
    public async Task FormatDetector_DetectsWrittenPpmFile()
    {
        var image = CreateTestImage(4, 4);
        string path = TempPath("detect.ppm");
        try
        {
            PnmCoder.Write(image, path, PnmFormat.PpmBinary);
            byte[] header = new byte[16];
            using (var fs = File.OpenRead(path))
                fs.ReadExactly(header);

            var format = FormatDetector.Detect(header);
            await Assert.That(format).IsEqualTo(FormatDetector.ImageFormat.Pnm);
        }
        finally
        {
            image.Dispose();
            TryDelete(path);
        }
    }

    [Test]
    public async Task FormatDetector_VariousFormats()
    {
        // PNG
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        await Assert.That(FormatDetector.Detect(png)).IsEqualTo(FormatDetector.ImageFormat.Png);

        // JPEG
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0];
        await Assert.That(FormatDetector.Detect(jpg)).IsEqualTo(FormatDetector.ImageFormat.Jpeg);

        // GIF
        byte[] gif = "GIF89a"u8.ToArray();
        await Assert.That(FormatDetector.Detect(gif)).IsEqualTo(FormatDetector.ImageFormat.Gif);

        // TIFF LE
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00];
        await Assert.That(FormatDetector.Detect(tiff)).IsEqualTo(FormatDetector.ImageFormat.Tiff);

        // Unknown
        byte[] unknown = [0x00, 0x00, 0x00, 0x00];
        await Assert.That(FormatDetector.Detect(unknown)).IsEqualTo(FormatDetector.ImageFormat.Unknown);
    }

    // --- Helpers ---

    private static ImageFrame CreateTestImage(int width, int height)
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
