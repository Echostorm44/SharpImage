// Tests for PDF output coder.
// Validates PDF structure, single/multi-page, compression modes,
// grayscale/alpha handling, and round-trip format conversion.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for the PDF image coder (write-only).
/// Covers: PDF structure, Deflate/JPEG compression, grayscale, alpha/SMask,
/// multi-page, DPI options, metadata, and format registry integration.
/// </summary>
public class PdfCoderTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_pdf_test_{name}");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Helpers ──

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

    private static ImageFrame CreateRgbaImage(int width, int height)
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
                row[offset + 2] = (ushort)(Quantum.MaxValue / 2);
                row[offset + 3] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1));
            }
        }
        return frame;
    }

    // ── PDF structure tests ──

    [Test]
    public async Task Write_SinglePage_ProducesValidPdfStructure()
    {
        var image = CreateTestImage(100, 80);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).StartsWith("%PDF-1.4");
        await Assert.That(text).Contains("/Type /Catalog");
        await Assert.That(text).Contains("/Type /Pages");
        await Assert.That(text).Contains("/Type /Page");
        await Assert.That(text).Contains("/Type /XObject");
        await Assert.That(text).Contains("/Subtype /Image");
        await Assert.That(text).Contains("%%EOF");
    }

    [Test]
    public async Task Write_SinglePage_ContainsXrefTable()
    {
        var image = CreateTestImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("xref");
        await Assert.That(text).Contains("trailer");
        await Assert.That(text).Contains("startxref");
    }

    [Test]
    public async Task Write_SinglePage_ContainsDrawingCommands()
    {
        var image = CreateTestImage(100, 80);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        // Should have graphics state save/restore and image paint
        await Assert.That(text).Contains("/Im0 Do");
        await Assert.That(text).Contains("cm");
    }

    [Test]
    public async Task Write_SinglePage_HasCorrectMediaBox()
    {
        var image = CreateTestImage(200, 150);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        // At 72 DPI, 200 pixels = 200 points, 150 pixels = 150 points
        await Assert.That(text).Contains("/MediaBox [0 0 200.0000 150.0000]");
    }

    [Test]
    public async Task Write_CustomDpi_ScalesMediaBox()
    {
        var image = CreateTestImage(200, 150);
        var options = new PdfWriteOptions { Dpi = 144.0 };
        byte[] pdf = PdfCoder.Encode(image, options);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        // At 144 DPI: 200px * 72/144 = 100pt, 150px * 72/144 = 75pt
        await Assert.That(text).Contains("/MediaBox [0 0 100.0000 75.0000]");
    }

    [Test]
    public async Task Write_WithTitle_IncludesMetadata()
    {
        var image = CreateTestImage(50, 50);
        var options = new PdfWriteOptions { Title = "Test Document" };
        byte[] pdf = PdfCoder.Encode(image, options);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/Producer (SharpImage)");
        await Assert.That(text).Contains("/Title (Test Document)");
    }

    [Test]
    public async Task Write_ProducerAlwaysPresent()
    {
        var image = CreateTestImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/Producer (SharpImage)");
    }

    // ── Compression tests ──

    [Test]
    public async Task Write_DeflateCompression_UsesFlateDecode()
    {
        var image = CreateTestImage(100, 80);
        var options = new PdfWriteOptions { Compression = PdfCompression.Deflate };
        byte[] pdf = PdfCoder.Encode(image, options);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/Filter /FlateDecode");
    }

    [Test]
    public async Task Write_JpegCompression_UsesDctDecode()
    {
        var image = CreateTestImage(100, 80);
        var options = new PdfWriteOptions { Compression = PdfCompression.Jpeg };
        byte[] pdf = PdfCoder.Encode(image, options);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/Filter /DCTDecode");
    }

    [Test]
    public async Task Write_JpegCompression_SmallerThanDeflateForPhotos()
    {
        // Natural images compress better with JPEG
        var image = CreateTestImage(200, 200);
        byte[] deflate = PdfCoder.Encode(image, new PdfWriteOptions { Compression = PdfCompression.Deflate });
        byte[] jpeg = PdfCoder.Encode(image, new PdfWriteOptions { Compression = PdfCompression.Jpeg });

        await Assert.That(jpeg.Length).IsLessThan(deflate.Length);
    }

    [Test]
    public async Task Write_JpegQuality_AffectsFileSize()
    {
        var image = CreateTestImage(200, 200);
        byte[] highQ = PdfCoder.Encode(image, new PdfWriteOptions { Compression = PdfCompression.Jpeg, JpegQuality = 95 });
        byte[] lowQ = PdfCoder.Encode(image, new PdfWriteOptions { Compression = PdfCompression.Jpeg, JpegQuality = 30 });

        await Assert.That(lowQ.Length).IsLessThan(highQ.Length);
    }

    // ── Color space tests ──

    [Test]
    public async Task Write_RgbImage_UsesDeviceRgb()
    {
        var image = CreateTestImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/ColorSpace /DeviceRGB");
    }

    [Test]
    public async Task Write_GrayscaleImage_UsesDeviceGray()
    {
        var image = CreateGrayscaleImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/ColorSpace /DeviceGray");
    }

    [Test]
    public async Task Write_ImageDimensions_InXObject()
    {
        var image = CreateTestImage(320, 240);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/Width 320");
        await Assert.That(text).Contains("/Height 240");
        await Assert.That(text).Contains("/BitsPerComponent 8");
    }

    // ── Alpha / SMask tests ──

    [Test]
    public async Task Write_RgbaImage_CreatesSMask()
    {
        var image = CreateRgbaImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/SMask");
    }

    [Test]
    public async Task Write_RgbaImage_SMaskIsDeviceGray()
    {
        var image = CreateRgbaImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        // SMask object should be a grayscale XObject
        // Count occurrences of /DeviceGray — should appear for SMask
        int grayCount = CountOccurrences(text, "/DeviceGray");
        await Assert.That(grayCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Write_NoAlpha_NoSMask()
    {
        var image = CreateTestImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).DoesNotContain("/SMask");
    }

    // ── Multi-page tests ──

    [Test]
    public async Task Write_MultiplePages_AllPagesPresent()
    {
        var images = new[]
        {
            CreateTestImage(100, 80),
            CreateTestImage(200, 150),
            CreateTestImage(50, 50)
        };
        using var ms = new MemoryStream();
        PdfCoder.Write(images, ms);
        string text = System.Text.Encoding.ASCII.GetString(ms.ToArray());

        await Assert.That(text).Contains("/Count 3");
        // Three Page objects
        int pageCount = CountOccurrences(text, "/Type /Page\n");
        // Pages dict also says /Type /Pages so avoid counting that
        // Count /Type /Page / (with space after Page and before /)
        int pageObjCount = CountOccurrences(text, "/Type /Page /Parent");
        await Assert.That(pageObjCount).IsEqualTo(3);
    }

    [Test]
    public async Task Write_MultiplePages_DifferentSizedMediaBoxes()
    {
        var images = new[]
        {
            CreateTestImage(100, 80),
            CreateTestImage(200, 150)
        };
        using var ms = new MemoryStream();
        PdfCoder.Write(images, ms);
        string text = System.Text.Encoding.ASCII.GetString(ms.ToArray());

        await Assert.That(text).Contains("/MediaBox [0 0 100.0000 80.0000]");
        await Assert.That(text).Contains("/MediaBox [0 0 200.0000 150.0000]");
    }

    [Test]
    public async Task Write_MultiplePages_MixedAlpha()
    {
        var images = new[]
        {
            CreateTestImage(50, 50),   // no alpha
            CreateRgbaImage(50, 50)    // with alpha
        };
        using var ms = new MemoryStream();
        PdfCoder.Write(images, ms);
        string text = System.Text.Encoding.ASCII.GetString(ms.ToArray());

        // Should have exactly one SMask reference
        int smaskCount = CountOccurrences(text, "/SMask");
        await Assert.That(smaskCount).IsEqualTo(1);
    }

    // ── File I/O tests ──

    [Test]
    public async Task Write_ToFile_CreatesValidPdf()
    {
        string path = TempPath("file_write.pdf");
        try
        {
            var image = CreateTestImage(100, 80);
            PdfCoder.Write(image, path);

            await Assert.That(File.Exists(path)).IsTrue();
            byte[] data = File.ReadAllBytes(path);
            string text = System.Text.Encoding.ASCII.GetString(data);
            await Assert.That(text).StartsWith("%PDF-1.4");
            await Assert.That(text).Contains("%%EOF");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Test]
    public async Task Write_ViaFormatRegistry_ProducesValidPdf()
    {
        string path = TempPath("registry_write.pdf");
        try
        {
            var image = CreateTestImage(100, 80);
            FormatRegistry.Write(image, path);

            byte[] data = File.ReadAllBytes(path);
            string text = System.Text.Encoding.ASCII.GetString(data);
            await Assert.That(text).StartsWith("%PDF-1.4");
            await Assert.That(text).Contains("/Type /Catalog");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Test]
    public async Task DetectFromExtension_Pdf_ReturnsPdf()
    {
        var format = FormatRegistry.DetectFromExtension("test.pdf");
        await Assert.That(format).IsEqualTo(ImageFileFormat.Pdf);
    }

    // ── Edge case / validation tests ──

    [Test]
    public async Task Write_EmptyArray_ThrowsArgumentException()
    {
        await Assert.That(() =>
        {
            using var ms = new MemoryStream();
            PdfCoder.Write(Array.Empty<ImageFrame>(), ms);
        }).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Write_NullArray_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
        {
            using var ms = new MemoryStream();
            PdfCoder.Write((ImageFrame[])null!, ms);
        }).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task Write_SmallImage_1x1_Succeeds()
    {
        var image = CreateTestImage(1, 1);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).StartsWith("%PDF-1.4");
        await Assert.That(text).Contains("/Width 1");
        await Assert.That(text).Contains("/Height 1");
    }

    [Test]
    public async Task Write_LargerImage_Succeeds()
    {
        var image = CreateTestImage(1024, 768);
        byte[] pdf = PdfCoder.Encode(image);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("/Width 1024");
        await Assert.That(text).Contains("/Height 768");
    }

    [Test]
    public async Task Write_TitleWithSpecialChars_Escaped()
    {
        var options = new PdfWriteOptions { Title = "Test (with) parens \\ backslash" };
        var image = CreateTestImage(50, 50);
        byte[] pdf = PdfCoder.Encode(image, options);
        string text = System.Text.Encoding.ASCII.GetString(pdf);

        await Assert.That(text).Contains("Test \\(with\\) parens \\\\ backslash");
    }

    // ── Real image conversion tests ──

    [Test]
    public async Task Write_RealPng_ToPdf()
    {
        string pngPath = Path.Combine(TestImagesDir, "photo_small.png");
        if (!File.Exists(pngPath)) return;

        string pdfPath = TempPath("rose_from_png.pdf");
        try
        {
            var image = FormatRegistry.Read(pngPath);
            PdfCoder.Write(image, pdfPath);

            byte[] data = File.ReadAllBytes(pdfPath);
            string text = System.Text.Encoding.ASCII.GetString(data);
            await Assert.That(text).StartsWith("%PDF-1.4");
            await Assert.That(data.Length).IsGreaterThan(100);
        }
        finally
        {
            TryDelete(pdfPath);
        }
    }

    [Test]
    public async Task Write_RealJpeg_ToPdfJpeg()
    {
        string jpgPath = Path.Combine(TestImagesDir, "photo_small.jpg");
        if (!File.Exists(jpgPath)) return;

        string pdfPath = TempPath("rose_jpeg_in_pdf.pdf");
        try
        {
            var image = FormatRegistry.Read(jpgPath);
            PdfCoder.Write(image, pdfPath, new PdfWriteOptions { Compression = PdfCompression.Jpeg });

            byte[] data = File.ReadAllBytes(pdfPath);
            string text = System.Text.Encoding.ASCII.GetString(data);
            await Assert.That(text).Contains("/Filter /DCTDecode");
        }
        finally
        {
            TryDelete(pdfPath);
        }
    }

    // ── Default options tests ──

    [Test]
    public async Task DefaultOptions_DeflateCompression()
    {
        var opts = new PdfWriteOptions();
        await Assert.That(opts.Compression).IsEqualTo(PdfCompression.Deflate);
    }

    [Test]
    public async Task DefaultOptions_72Dpi()
    {
        var opts = new PdfWriteOptions();
        await Assert.That(opts.Dpi).IsEqualTo(72.0);
    }

    [Test]
    public async Task DefaultOptions_JpegQuality85()
    {
        var opts = new PdfWriteOptions();
        await Assert.That(opts.JpegQuality).IsEqualTo(85);
    }

    [Test]
    public async Task DefaultOptions_NoTitle()
    {
        var opts = new PdfWriteOptions();
        await Assert.That(opts.Title).IsNull();
    }

    // ── Utilities ──

    private static int CountOccurrences(string text, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
