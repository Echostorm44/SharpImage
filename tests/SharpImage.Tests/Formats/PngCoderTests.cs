using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.IO;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for PNG format coder and CRC32/Adler32 checksums.
/// Covers: round-trip, real-file reading, alpha channels, cross-format, checksums.
/// </summary>
public class PngCoderTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // ─── CRC32 Tests ─────────────────────────────────────────────

    [Test]
    public async Task Crc32_KnownVector_MatchesExpected()
    {
        // CRC-32 of "123456789" = 0xCBF43926 (well-known test vector)
        byte[] data = "123456789"u8.ToArray();
        uint crc = Crc32.Compute(data);
        await Assert.That(crc).IsEqualTo(0xCBF43926u);
    }

    [Test]
    public async Task Crc32_IncrementalUpdate_MatchesFull()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        uint fullCrc = Crc32.Compute(data);

        // Compute incrementally: "Hello, " then "World!"
        uint partial = Crc32.Compute(data.AsSpan(0, 7));
        uint incremental = Crc32.Update(partial, data.AsSpan(7));

        await Assert.That(incremental).IsEqualTo(fullCrc);
    }

    [Test]
    public async Task Adler32_KnownVector_MatchesExpected()
    {
        // Adler-32 of "Wikipedia" = 0x11E60398
        byte[] data = "Wikipedia"u8.ToArray();
        uint adler = Adler32.Compute(data);
        await Assert.That(adler).IsEqualTo(0x11E60398u);
    }

    // ─── PNG Round-Trip Tests ────────────────────────────────────

    [Test]
    public async Task Png_RoundTrip_SmallGradient_PreservesPixels()
    {
        var original = CreateGradientImage(16, 12, hasAlpha: false);
        string tempFile = TempPath("roundtrip_rgb.png");

        try
        {
            PngCoder.Write(original, tempFile);
            var loaded = PngCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(16);
            await Assert.That(loaded.Rows).IsEqualTo(12);

            // PNG 8-bit write: tolerance = 1 byte of quantum range (257)
            ushort tolerance = 257;
            for (int y = 0; y < 12; y++)
            {
                var origRow = original.GetPixelRow(y).ToArray();
                var loadRow = loaded.GetPixelRow(y).ToArray();
                for (int i = 0; i < origRow.Length; i++)
                {
                    int diff = Math.Abs(origRow[i] - loadRow[i]);
                    await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                }
            }
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Png_RoundTrip_WithAlpha_PreservesAlpha()
    {
        var original = CreateGradientImage(10, 8, hasAlpha: true);
        string tempFile = TempPath("roundtrip_rgba.png");

        try
        {
            PngCoder.Write(original, tempFile);
            var loaded = PngCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(10);
            await Assert.That(loaded.Rows).IsEqualTo(8);
            await Assert.That(loaded.HasAlpha).IsTrue();
            await Assert.That(loaded.NumberOfChannels).IsEqualTo(4);

            // Verify alpha channel survives round-trip
            ushort tolerance = 257;
            for (int y = 0; y < 8; y++)
            {
                var origRow = original.GetPixelRow(y).ToArray();
                var loadRow = loaded.GetPixelRow(y).ToArray();
                for (int i = 0; i < origRow.Length; i++)
                {
                    int diff = Math.Abs(origRow[i] - loadRow[i]);
                    await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                }
            }
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
    public async Task Png_Read_RoseImage_CorrectDimensions()
    {
        string rosePath = Path.Combine(TestImagesDir, "photo_small.png");
        var image = PngCoder.Read(rosePath);
        try
        {
            // photo_small.png is 70x46 (SIPI peppers downscaled)
            await Assert.That(image.Columns).IsEqualTo(70);
            await Assert.That(image.Rows).IsEqualTo(46);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Test]
    public async Task Png_RoundTrip_RoseImage_PreservesContent()
    {
        string rosePath = Path.Combine(TestImagesDir, "photo_small.png");
        string tempFile = TempPath("rose_roundtrip.png");

        var original = PngCoder.Read(rosePath);
        try
        {
            PngCoder.Write(original, tempFile);
            var reloaded = PngCoder.Read(tempFile);

            await Assert.That(reloaded.Columns).IsEqualTo(original.Columns);
            await Assert.That(reloaded.Rows).IsEqualTo(original.Rows);

            // Verify pixels match within 8-bit quantization tolerance
            ushort tolerance = 257;
            for (int y = 0; y < (int)original.Rows; y++)
            {
                var origRow = original.GetPixelRow(y).ToArray();
                var loadRow = reloaded.GetPixelRow(y).ToArray();
                for (int i = 0; i < origRow.Length; i++)
                {
                    int diff = Math.Abs(origRow[i] - loadRow[i]);
                    await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                }
            }
            reloaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Png_Read_GraniteImage_CorrectDimensions()
    {
        string path = Path.Combine(TestImagesDir, "texture_pattern.png");
        var image = PngCoder.Read(path);
        try
        {
            // texture_pattern.png is 128x128
            await Assert.That(image.Columns).IsEqualTo(128);
            await Assert.That(image.Rows).IsEqualTo(128);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Test]
    public async Task Png_Read_WizardWithAlpha_HasAlphaChannel()
    {
        string path = Path.Combine(TestImagesDir, "peppers_rgba.png");
        var image = PngCoder.Read(path);
        try
        {
            // peppers_rgba.png has transparency (RGBA)
            await Assert.That(image.HasAlpha).IsTrue();
            await Assert.That(image.NumberOfChannels).IsEqualTo(4);
        }
        finally
        {
            image.Dispose();
        }
    }

    // ─── Cross-Format Tests ──────────────────────────────────────

    [Test]
    public async Task Png_WriteThenReadAsBmp_CrossFormat()
    {
        var original = CreateGradientImage(20, 15, hasAlpha: false);
        string pngFile = TempPath("cross_format.png");
        string bmpFile = TempPath("cross_format.bmp");

        try
        {
            PngCoder.Write(original, pngFile);
            var fromPng = PngCoder.Read(pngFile);

            BmpCoder.Write(fromPng, bmpFile);
            var fromBmp = BmpCoder.Read(bmpFile);

            await Assert.That(fromBmp.Columns).IsEqualTo(20);
            await Assert.That(fromBmp.Rows).IsEqualTo(15);

            // BMP also quantizes to 8-bit, so tolerance applies
            ushort tolerance = 257;
            for (int y = 0; y < 15; y++)
            {
                var origRow = original.GetPixelRow(y).ToArray();
                var bmpRow = fromBmp.GetPixelRow(y).ToArray();
                int channels = Math.Min(origRow.Length / 20, bmpRow.Length / 20);
                for (int x = 0; x < 20; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int diff = Math.Abs(origRow[x * (origRow.Length / 20) + c] - bmpRow[x * (bmpRow.Length / 20) + c]);
                        await Assert.That(diff).IsLessThanOrEqualTo(tolerance);
                    }
                }
            }
            fromPng.Dispose();
            fromBmp.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(pngFile)) File.Delete(pngFile);
            if (File.Exists(bmpFile)) File.Delete(bmpFile);
        }
    }

    // ─── Format Detection ────────────────────────────────────────

    [Test]
    public async Task FormatDetector_IdentifiesPngFile()
    {
        string rosePath = Path.Combine(TestImagesDir, "photo_small.png");
        byte[] header = new byte[16];
        using (var fs = File.OpenRead(rosePath))
            fs.ReadExactly(header);
        var format = FormatDetector.Detect(header);
        await Assert.That(format).IsEqualTo(FormatDetector.ImageFormat.Png);
    }

    // ─── PNG Signature & CRC Validation ──────────────────────────

    [Test]
    public async Task Png_Read_InvalidSignature_Throws()
    {
        string tempFile = TempPath("invalid.png");
        try
        {
            File.WriteAllBytes(tempFile, [0, 0, 0, 0, 0, 0, 0, 0]);
            await Assert.That(() => PngCoder.Read(tempFile)).Throws<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Png_FileWritten_HasValidSignature()
    {
        var image = CreateGradientImage(4, 4, hasAlpha: false);
        string tempFile = TempPath("signature_check.png");

        try
        {
            PngCoder.Write(image, tempFile);
            byte[] bytes = File.ReadAllBytes(tempFile);

            // PNG signature: 137 80 78 71 13 10 26 10
            await Assert.That(bytes[0]).IsEqualTo((byte)137);
            await Assert.That(bytes[1]).IsEqualTo((byte)80);  // 'P'
            await Assert.That(bytes[2]).IsEqualTo((byte)78);  // 'N'
            await Assert.That(bytes[3]).IsEqualTo((byte)71);  // 'G'
            await Assert.That(bytes[4]).IsEqualTo((byte)13);
            await Assert.That(bytes[5]).IsEqualTo((byte)10);
            await Assert.That(bytes[6]).IsEqualTo((byte)26);
            await Assert.That(bytes[7]).IsEqualTo((byte)10);
        }
        finally
        {
            image.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Helper ──────────────────────────────────────────────────

    private static ImageFrame CreateGradientImage(int width, int height, bool hasAlpha)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);

        int channels = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));     // R gradient
                row[offset + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1)); // G gradient
                row[offset + 2] = (ushort)(((x + y) % 2 == 0) ? Quantum.MaxValue : 0);     // B checkerboard
                if (channels > 3)
                    row[offset + 3] = (ushort)((x + y) * Quantum.MaxValue / (width + height - 2)); // Alpha gradient
            }
        }
        return image;
    }
}
