using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for TIFF format coder.
/// </summary>
public class TiffCoderTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // ─── Round-Trip Tests ────────────────────────────────────────

    [Test]
    public async Task Tiff_RoundTrip_Uncompressed()
    {
        var original = CreateTestImage(16, 12);
        string tempFile = TempPath("roundtrip_none.tiff");

        try
        {
            TiffCoder.Write(original, tempFile, TiffCompression.None);
            var loaded = TiffCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(16);
            await Assert.That(loaded.Rows).IsEqualTo(12);
            AssertPixelsMatch(original, loaded);
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_RoundTrip_Deflate()
    {
        var original = CreateTestImage(20, 15);
        string tempFile = TempPath("roundtrip_deflate.tiff");

        try
        {
            TiffCoder.Write(original, tempFile, TiffCompression.Deflate);
            var loaded = TiffCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(20);
            await Assert.That(loaded.Rows).IsEqualTo(15);
            AssertPixelsMatch(original, loaded);
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_RoundTrip_Lzw()
    {
        var original = CreateTestImage(10, 8);
        string tempFile = TempPath("roundtrip_lzw.tiff");

        try
        {
            TiffCoder.Write(original, tempFile, TiffCompression.Lzw);
            var loaded = TiffCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(10);
            await Assert.That(loaded.Rows).IsEqualTo(8);
            AssertPixelsMatch(original, loaded);
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_RoundTrip_PackBits()
    {
        var original = CreateTestImage(12, 10);
        string tempFile = TempPath("roundtrip_packbits.tiff");

        try
        {
            TiffCoder.Write(original, tempFile, TiffCompression.PackBits);
            var loaded = TiffCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(12);
            await Assert.That(loaded.Rows).IsEqualTo(10);
            AssertPixelsMatch(original, loaded);
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_RoundTrip_LargerImage()
    {
        var original = CreateTestImage(128, 96);
        string tempFile = TempPath("roundtrip_large.tiff");

        try
        {
            TiffCoder.Write(original, tempFile, TiffCompression.Deflate);
            var loaded = TiffCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(128);
            await Assert.That(loaded.Rows).IsEqualTo(96);
            AssertPixelsMatch(original, loaded);
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Signature Tests ─────────────────────────────────────────

    [Test]
    public async Task Tiff_HasValidHeader_LittleEndian()
    {
        var image = CreateTestImage(4, 4);
        string tempFile = TempPath("header.tiff");

        try
        {
            TiffCoder.Write(image, tempFile);
            byte[] bytes = File.ReadAllBytes(tempFile);

            // Little-endian: II, magic 42
            await Assert.That(bytes[0]).IsEqualTo((byte)0x49);
            await Assert.That(bytes[1]).IsEqualTo((byte)0x49);
            await Assert.That(bytes[2]).IsEqualTo((byte)42);
            await Assert.That(bytes[3]).IsEqualTo((byte)0);
        }
        finally
        {
            image.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_InvalidData_Throws()
    {
        string tempFile = TempPath("invalid.tiff");
        try
        {
            File.WriteAllBytes(tempFile, [0, 0, 0, 0, 0, 0, 0, 0]);
            await Assert.That(() => TiffCoder.Read(tempFile)).Throws<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Cross-Format Tests ──────────────────────────────────────

    [Test]
    public async Task Tiff_ToPng_CrossFormat()
    {
        var original = CreateTestImage(24, 16);
        string tiffFile = TempPath("cross.tiff");
        string pngFile = TempPath("from_tiff.png");

        try
        {
            TiffCoder.Write(original, tiffFile);
            var fromTiff = TiffCoder.Read(tiffFile);

            PngCoder.Write(fromTiff, pngFile);
            var fromPng = PngCoder.Read(pngFile);

            await Assert.That(fromPng.Columns).IsEqualTo(24);
            await Assert.That(fromPng.Rows).IsEqualTo(16);

            fromTiff.Dispose();
            fromPng.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tiffFile)) File.Delete(tiffFile);
            if (File.Exists(pngFile)) File.Delete(pngFile);
        }
    }

    [Test]
    public async Task Tiff_FromPng_RoseImage()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");
        string tiffFile = TempPath("rose.tiff");
        string backPng = TempPath("rose_back.png");

        try
        {
            var roseImage = PngCoder.Read(rosePng);
            long origWidth = roseImage.Columns;
            long origHeight = roseImage.Rows;

            TiffCoder.Write(roseImage, tiffFile);
            var fromTiff = TiffCoder.Read(tiffFile);

            await Assert.That(fromTiff.Columns).IsEqualTo(origWidth);
            await Assert.That(fromTiff.Rows).IsEqualTo(origHeight);

            // Verify non-black content
            var midRow = fromTiff.GetPixelRow(fromTiff.Rows / 2).ToArray();
            bool hasContent = false;
            for (int i = 0; i < midRow.Length; i++)
                if (midRow[i] > 0) { hasContent = true; break; }
            await Assert.That(hasContent).IsTrue();

            roseImage.Dispose();
            fromTiff.Dispose();
        }
        finally
        {
            if (File.Exists(tiffFile)) File.Delete(tiffFile);
            if (File.Exists(backPng)) File.Delete(backPng);
        }
    }

    // ─── Compression Ratio Tests ─────────────────────────────────

    [Test]
    public async Task Tiff_Deflate_SmallerThanUncompressed()
    {
        var image = CreateSolidColorImage(64, 64, 255, 0, 0); // Solid red → very compressible
        string uncompFile = TempPath("uncomp.tiff");
        string deflateFile = TempPath("deflate.tiff");

        try
        {
            TiffCoder.Write(image, uncompFile, TiffCompression.None);
            TiffCoder.Write(image, deflateFile, TiffCompression.Deflate);

            long uncompSize = new FileInfo(uncompFile).Length;
            long deflateSize = new FileInfo(deflateFile).Length;

            await Assert.That(deflateSize).IsLessThan(uncompSize);
        }
        finally
        {
            image.Dispose();
            if (File.Exists(uncompFile)) File.Delete(uncompFile);
            if (File.Exists(deflateFile)) File.Delete(deflateFile);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateTestImage(int width, int height)
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
                row[offset] = Quantum.ScaleFromByte((byte)((x * 17) % 256));
                row[offset + 1] = Quantum.ScaleFromByte((byte)((y * 23) % 256));
                row[offset + 2] = Quantum.ScaleFromByte((byte)(((x + y) * 37) % 256));
            }
        }
        return image;
    }

    private static ImageFrame CreateSolidColorImage(int width, int height, byte r, byte g, byte b)
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
                row[offset] = Quantum.ScaleFromByte(r);
                row[offset + 1] = Quantum.ScaleFromByte(g);
                row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return image;
    }

    /// <summary>Asserts that two images have identical pixel data.</summary>
    private static void AssertPixelsMatch(ImageFrame expected, ImageFrame actual)
    {
        if (expected.Columns != actual.Columns || expected.Rows != actual.Rows)
            throw new Exception($"Dimension mismatch: {expected.Columns}x{expected.Rows} vs {actual.Columns}x{actual.Rows}");

        int channels = Math.Min(expected.NumberOfChannels, actual.NumberOfChannels);
        for (int y = 0; y < expected.Rows; y++)
        {
            var expRow = expected.GetPixelRow(y);
            var actRow = actual.GetPixelRow(y);
            for (int x = 0; x < expected.Columns; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int expIdx = (int)(x * expected.NumberOfChannels + c);
                    int actIdx = (int)(x * actual.NumberOfChannels + c);
                    if (expRow[expIdx] != actRow[actIdx])
                        throw new Exception($"Pixel mismatch at ({x},{y}) channel {c}: expected {expRow[expIdx]}, got {actRow[actIdx]}");
                }
            }
        }
    }
}
