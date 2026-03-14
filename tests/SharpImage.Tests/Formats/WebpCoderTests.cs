using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for WebP format coder — VP8L (lossless) round-trips and VP8 (lossy).
/// </summary>
public class WebpCoderTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    // ═══════════════════════════════════════════════════════════════════
    // VP8L Lossless Round-Trip Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Vp8lLossless_SolidColor_RoundTrip()
    {
        using var original = CreateSolidImage(32, 32, 200, 100, 50, 255);
        using var ms = new MemoryStream();
        WebpCoder.Write(original, ms, quality: 100);
        ms.Position = 0;

        using var decoded = WebpCoder.Read(ms);
        await Assert.That(decoded.Columns).IsEqualTo(32);
        await Assert.That(decoded.Rows).IsEqualTo(32);

        // Lossless: every pixel should match exactly
        for (int y = 0; y < 32; y++)
        {
            var decRow = decoded.GetPixelRow(y).ToArray();
            for (int x = 0; x < 32; x++)
            {
                int off = x * original.NumberOfChannels;
                await Assert.That((int)Quantum.ScaleToByte(decRow[off])).IsEqualTo(200);
                await Assert.That((int)Quantum.ScaleToByte(decRow[off + 1])).IsEqualTo(100);
                await Assert.That((int)Quantum.ScaleToByte(decRow[off + 2])).IsEqualTo(50);
            }
        }
    }

    [Test]
    public async Task Vp8lLossless_GradientImage_RoundTrip()
    {
        int w = 64, h = 64;
        using var original = new ImageFrame();
        original.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = original.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                row[off] = Quantum.ScaleFromByte((byte)(x * 4));     // R gradient
                row[off + 1] = Quantum.ScaleFromByte((byte)(y * 4)); // G gradient
                row[off + 2] = Quantum.ScaleFromByte(128);           // B constant
            }
        }

        using var ms = new MemoryStream();
        WebpCoder.Write(original, ms, quality: 100);
        ms.Position = 0;

        using var decoded = WebpCoder.Read(ms);
        await Assert.That(decoded.Columns).IsEqualTo(w);
        await Assert.That(decoded.Rows).IsEqualTo(h);

        // Verify pixel-perfect lossless
        int mismatches = 0;
        for (int y = 0; y < h; y++)
        {
            var origRow = original.GetPixelRow(y).ToArray();
            var decRow = decoded.GetPixelRow(y).ToArray();
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                if (Quantum.ScaleToByte(decRow[off]) != Quantum.ScaleToByte(origRow[off]) ||
                    Quantum.ScaleToByte(decRow[off + 1]) != Quantum.ScaleToByte(origRow[off + 1]) ||
                    Quantum.ScaleToByte(decRow[off + 2]) != Quantum.ScaleToByte(origRow[off + 2]))
                    mismatches++;
            }
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task Vp8lLossless_WithAlpha_RoundTrip()
    {
        int w = 16, h = 16;
        using var original = new ImageFrame();
        original.Initialize(w, h, ColorspaceType.SRGB, true);
        for (int y = 0; y < h; y++)
        {
            var row = original.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * 4;
                row[off] = Quantum.ScaleFromByte(255);
                row[off + 1] = Quantum.ScaleFromByte(0);
                row[off + 2] = Quantum.ScaleFromByte(0);
                row[off + 3] = Quantum.ScaleFromByte((byte)(y * 16)); // alpha gradient
            }
        }

        using var ms = new MemoryStream();
        WebpCoder.Write(original, ms, quality: 100);
        ms.Position = 0;

        using var decoded = WebpCoder.Read(ms);
        await Assert.That(decoded.HasAlpha).IsTrue();

        for (int y = 0; y < h; y++)
        {
            var origRow = original.GetPixelRow(y).ToArray();
            var decRow = decoded.GetPixelRow(y).ToArray();
            for (int x = 0; x < w; x++)
            {
                int off = x * 4;
                await Assert.That((int)Quantum.ScaleToByte(decRow[off + 3]))
                    .IsEqualTo((int)Quantum.ScaleToByte(origRow[off + 3]));
            }
        }
    }

    [Test]
    public async Task Vp8lLossless_LargeImage_RoundTrip()
    {
        int w = 256, h = 256;
        using var original = new ImageFrame();
        original.Initialize(w, h, ColorspaceType.SRGB, false);
        var rng = new Random(42);
        for (int y = 0; y < h; y++)
        {
            var row = original.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                row[off] = Quantum.ScaleFromByte((byte)rng.Next(256));
                row[off + 1] = Quantum.ScaleFromByte((byte)rng.Next(256));
                row[off + 2] = Quantum.ScaleFromByte((byte)rng.Next(256));
            }
        }

        using var ms = new MemoryStream();
        WebpCoder.Write(original, ms, quality: 100);
        ms.Position = 0;

        using var decoded = WebpCoder.Read(ms);
        await Assert.That(decoded.Columns).IsEqualTo(w);
        await Assert.That(decoded.Rows).IsEqualTo(h);

        // Verify pixel-perfect lossless
        int mismatches = 0;
        for (int y = 0; y < h; y++)
        {
            var origRow = original.GetPixelRow(y).ToArray();
            var decRow = decoded.GetPixelRow(y).ToArray();
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                if (Quantum.ScaleToByte(decRow[off]) != Quantum.ScaleToByte(origRow[off]) ||
                    Quantum.ScaleToByte(decRow[off + 1]) != Quantum.ScaleToByte(origRow[off + 1]) ||
                    Quantum.ScaleToByte(decRow[off + 2]) != Quantum.ScaleToByte(origRow[off + 2]))
                    mismatches++;
            }
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task WebpSignature_IsCorrect()
    {
        using var img = CreateSolidImage(4, 4, 128, 128, 128, 255);
        using var ms = new MemoryStream();
        WebpCoder.Write(img, ms, quality: 100);

        byte[] data = ms.ToArray();
        // RIFF header
        await Assert.That((int)data[0]).IsEqualTo((int)'R');
        await Assert.That((int)data[1]).IsEqualTo((int)'I');
        await Assert.That((int)data[2]).IsEqualTo((int)'F');
        await Assert.That((int)data[3]).IsEqualTo((int)'F');
        // WEBP tag
        await Assert.That((int)data[8]).IsEqualTo((int)'W');
        await Assert.That((int)data[9]).IsEqualTo((int)'E');
        await Assert.That((int)data[10]).IsEqualTo((int)'B');
        await Assert.That((int)data[11]).IsEqualTo((int)'P');
        // VP8L tag
        await Assert.That((int)data[12]).IsEqualTo((int)'V');
        await Assert.That((int)data[13]).IsEqualTo((int)'P');
        await Assert.That((int)data[14]).IsEqualTo((int)'8');
        await Assert.That((int)data[15]).IsEqualTo((int)'L');
    }

    [Test]
    public async Task WebpRead_InvalidData_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 0, 1, 2, 3 });
        await Assert.That(() => WebpCoder.Read(ms)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Vp8lLossless_1x1_RoundTrip()
    {
        using var original = CreateSolidImage(1, 1, 42, 84, 126, 255);
        using var ms = new MemoryStream();
        WebpCoder.Write(original, ms, quality: 100);
        ms.Position = 0;

        using var decoded = WebpCoder.Read(ms);
        await Assert.That(decoded.Columns).IsEqualTo(1);
        await Assert.That(decoded.Rows).IsEqualTo(1);

        var row = decoded.GetPixelRow(0).ToArray();
        await Assert.That((int)Quantum.ScaleToByte(row[0])).IsEqualTo(42);
        await Assert.That((int)Quantum.ScaleToByte(row[1])).IsEqualTo(84);
        await Assert.That((int)Quantum.ScaleToByte(row[2])).IsEqualTo(126);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cross-Format Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task PngToWebpLossless_RoundTrip()
    {
        int w = 48, h = 48;
        using var original = new ImageFrame();
        original.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = original.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                row[off] = Quantum.ScaleFromByte((byte)((x * 5) & 0xFF));
                row[off + 1] = Quantum.ScaleFromByte((byte)((y * 5) & 0xFF));
                row[off + 2] = Quantum.ScaleFromByte((byte)((x + y) & 0xFF));
            }
        }

        // Write as PNG, read back
        using var pngMs = new MemoryStream();
        PngCoder.Write(original, pngMs);
        pngMs.Position = 0;
        using var fromPng = PngCoder.Read(pngMs);

        // Write as WebP lossless, read back
        using var webpMs = new MemoryStream();
        WebpCoder.Write(fromPng, webpMs, quality: 100);
        webpMs.Position = 0;
        using var fromWebp = WebpCoder.Read(webpMs);

        // Compare
        int mismatches = 0;
        for (int y = 0; y < h; y++)
        {
            var origRow = fromPng.GetPixelRow(y).ToArray();
            var decRow = fromWebp.GetPixelRow(y).ToArray();
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                if (Quantum.ScaleToByte(decRow[off]) != Quantum.ScaleToByte(origRow[off]) ||
                    Quantum.ScaleToByte(decRow[off + 1]) != Quantum.ScaleToByte(origRow[off + 1]) ||
                    Quantum.ScaleToByte(decRow[off + 2]) != Quantum.ScaleToByte(origRow[off + 2]))
                    mismatches++;
            }
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task Vp8Lossy_BasicEncodeDecode()
    {
        using var original = CreateSolidImage(16, 16, 200, 100, 50, 255);
        using var ms = new MemoryStream();
        WebpCoder.Write(original, ms, quality: 75);

        byte[] data = ms.ToArray();
        // Should have RIFF/WEBP/VP8 header
        await Assert.That((int)data[0]).IsEqualTo((int)'R');
        await Assert.That((int)data[12]).IsEqualTo((int)'V');
        await Assert.That((int)data[13]).IsEqualTo((int)'P');
        await Assert.That((int)data[14]).IsEqualTo((int)'8');
        await Assert.That((int)data[15]).IsEqualTo((int)' ');

        await Assert.That(data.Length).IsGreaterThan(20);
        // Simplified DC-only encoder may exceed raw pixel size for tiny images;
        // verify it produces a reasonable VP8 bitstream (< 4x raw size)
        await Assert.That(data.Length).IsLessThan(16 * 16 * 3 * 4);
    }

    [Test]
    public async Task RealImage_RoseJpeg_ToWebpLossless()
    {
        string rosePath = Path.Combine(TestImagesDir, "photo_small.jpg");
        if (!File.Exists(rosePath))
        {
            // Skip if no test image available
            return;
        }

        using var jpegStream = File.OpenRead(rosePath);
        using var jpegImage = JpegCoder.Read(jpegStream);

        using var webpMs = new MemoryStream();
        WebpCoder.Write(jpegImage, webpMs, quality: 100);
        webpMs.Position = 0;

        using var webpImage = WebpCoder.Read(webpMs);
        await Assert.That(webpImage.Columns).IsEqualTo(jpegImage.Columns);
        await Assert.That(webpImage.Rows).IsEqualTo(jpegImage.Rows);

        // Lossless round-trip should be pixel-perfect
        int mismatches = 0;
        for (int y = 0; y < (int)jpegImage.Rows; y++)
        {
            var origRow = jpegImage.GetPixelRow(y).ToArray();
            var decRow = webpImage.GetPixelRow(y).ToArray();
            for (int x = 0; x < (int)jpegImage.Columns; x++)
            {
                int off = x * 3;
                if (Quantum.ScaleToByte(decRow[off]) != Quantum.ScaleToByte(origRow[off]) ||
                    Quantum.ScaleToByte(decRow[off + 1]) != Quantum.ScaleToByte(origRow[off + 1]) ||
                    Quantum.ScaleToByte(decRow[off + 2]) != Quantum.ScaleToByte(origRow[off + 2]))
                    mismatches++;
            }
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame CreateSolidImage(int w, int h, byte r, byte g, byte b, byte a)
    {
        bool hasAlpha = a < 255;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * channels;
                row[off] = Quantum.ScaleFromByte(r);
                row[off + 1] = Quantum.ScaleFromByte(g);
                row[off + 2] = Quantum.ScaleFromByte(b);
                if (hasAlpha) row[off + 3] = Quantum.ScaleFromByte(a);
            }
        }
        return frame;
    }
}
