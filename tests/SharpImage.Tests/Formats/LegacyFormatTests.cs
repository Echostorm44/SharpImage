// Tests for legacy format codecs: SGI, PIX, SUN.
// All tests use programmatically generated images since no test assets exist.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

public class LegacyFormatTests
{
    private static ImageFrame CreateTestFrame(int width, int height, bool hasAlpha = false)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte((byte)((x * 7 + y * 13) % 256));
                if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte((byte)((x * 11 + y * 3) % 256));
                if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte((byte)((x * 5 + y * 17) % 256));
                if (hasAlpha) row[offset + channels - 1] = Quantum.ScaleFromByte((byte)((x * 3 + y * 7 + 128) % 256));
            }
        }

        return frame;
    }

    private static ImageFrame CreateSolidFrame(int width, int height, byte r, byte g, byte b)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte(r);
                if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte(g);
                if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }

        return frame;
    }

    private static ImageFrame CreateGradientFrame(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            byte val = (byte)(y * 255 / Math.Max(height - 1, 1));
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte(val);
                if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte(val);
                if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte(val);
            }
        }

        return frame;
    }

    private static async Task AssertFramesEqual(ImageFrame expected, ImageFrame actual, int tolerance = 0)
    {
        await Assert.That(actual.Columns).IsEqualTo(expected.Columns);
        await Assert.That(actual.Rows).IsEqualTo(expected.Rows);
        await Assert.That(actual.NumberOfChannels).IsEqualTo(expected.NumberOfChannels);

        int channels = expected.NumberOfChannels;
        for (int y = 0; y < (int)expected.Rows; y++)
        {
            var expectedRow = expected.GetPixelRow(y);
            var actualRow = actual.GetPixelRow(y);
            for (int x = 0; x < (int)expected.Columns; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int idx = x * channels + c;
                    int diff = Math.Abs(expectedRow[idx] - actualRow[idx]);
                    if (diff > tolerance)
                        throw new Exception(
                            $"Pixel mismatch at ({x},{y}) channel {c}: expected {expectedRow[idx]}, got {actualRow[idx]} (diff {diff} > tolerance {tolerance})");
                }
            }
        }
    }

    // ========== SGI Tests ==========

    [Test]
    public async Task Sgi_CanDecode_ValidHeader_ReturnsTrue()
    {
        var data = new byte[512];
        data[0] = 0x01;
        data[1] = 0xDA;
        await Assert.That(SgiCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task Sgi_CanDecode_InvalidHeader_ReturnsFalse()
    {
        var data = new byte[512];
        data[0] = 0xFF;
        data[1] = 0xFF;
        await Assert.That(SgiCoder.CanDecode(data)).IsFalse();
    }

    [Test]
    public async Task Sgi_CanDecode_TooShort_ReturnsFalse()
    {
        var data = new byte[10];
        await Assert.That(SgiCoder.CanDecode(data)).IsFalse();
    }

    [Test]
    public async Task Sgi_RoundTrip_Rgb()
    {
        var original = CreateTestFrame(32, 24);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_RoundTrip_Rgba()
    {
        var original = CreateTestFrame(16, 16, hasAlpha: true);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        await Assert.That(decoded.HasAlpha).IsTrue();
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_RoundTrip_SolidColor()
    {
        var original = CreateSolidFrame(20, 15, 200, 100, 50);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_RoundTrip_Gradient()
    {
        var original = CreateGradientFrame(64, 64);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_RoundTrip_SmallImage()
    {
        var original = CreateTestFrame(1, 1);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(1u);
        await Assert.That(decoded.Rows).IsEqualTo(1u);
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_RoundTrip_LargeImage()
    {
        var original = CreateTestFrame(256, 256);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_Header_HasCorrectMagic()
    {
        var frame = CreateTestFrame(10, 10);
        byte[] encoded = SgiCoder.Encode(frame);

        await Assert.That(encoded[0]).IsEqualTo((byte)0x01);
        await Assert.That(encoded[1]).IsEqualTo((byte)0xDA);
    }

    [Test]
    public async Task Sgi_FormatRegistry_DetectsFormat()
    {
        var frame = CreateTestFrame(10, 10);
        byte[] encoded = SgiCoder.Encode(frame);

        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Sgi);
    }

    [Test]
    public async Task Sgi_FormatRegistry_DetectsExtension()
    {
        await Assert.That(FormatRegistry.DetectFromExtension("test.sgi")).IsEqualTo(ImageFileFormat.Sgi);
        await Assert.That(FormatRegistry.DetectFromExtension("test.rgb")).IsEqualTo(ImageFileFormat.Sgi);
        await Assert.That(FormatRegistry.DetectFromExtension("test.rgba")).IsEqualTo(ImageFileFormat.Sgi);
        await Assert.That(FormatRegistry.DetectFromExtension("test.bw")).IsEqualTo(ImageFileFormat.Sgi);
    }

    [Test]
    public async Task Sgi_FormatRegistry_RoundTrip()
    {
        var original = CreateTestFrame(20, 20);
        byte[] encoded = FormatRegistry.Encode(original, ImageFileFormat.Sgi);
        var decoded = FormatRegistry.Decode(encoded, ImageFileFormat.Sgi);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sgi_Decode_InvalidData_Throws()
    {
        var badData = new byte[] { 0xFF, 0xFF, 0, 0 };
        await Assert.That(() => SgiCoder.Decode(badData)).Throws<InvalidDataException>();
    }

    // ========== PIX Tests ==========

    [Test]
    public async Task Pix_CanDecode_AlwaysFalse()
    {
        // PIX has no magic bytes, detection by extension only
        var data = new byte[100];
        await Assert.That(PixCoder.CanDecode(data)).IsFalse();
    }

    [Test]
    public async Task Pix_RoundTrip_Rgb()
    {
        var original = CreateTestFrame(32, 24);
        byte[] encoded = PixCoder.Encode(original);
        var decoded = PixCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_RoundTrip_SolidColor()
    {
        // Solid colors should compress very well with RLE
        var original = CreateSolidFrame(100, 50, 128, 64, 200);
        byte[] encoded = PixCoder.Encode(original);
        var decoded = PixCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_RoundTrip_SmallImage()
    {
        var original = CreateTestFrame(1, 1);
        byte[] encoded = PixCoder.Encode(original);
        var decoded = PixCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(1u);
        await Assert.That(decoded.Rows).IsEqualTo(1u);
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_RoundTrip_Gradient()
    {
        var original = CreateGradientFrame(64, 64);
        byte[] encoded = PixCoder.Encode(original);
        var decoded = PixCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_RoundTrip_LargeImage()
    {
        var original = CreateTestFrame(200, 150);
        byte[] encoded = PixCoder.Encode(original);
        var decoded = PixCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_SolidColor_CompressesWell()
    {
        // A 100x50 solid image = 5000 pixels = 15000 raw bytes + header
        // With RLE, solid should compress to much less
        var original = CreateSolidFrame(100, 50, 255, 0, 0);
        byte[] encoded = PixCoder.Encode(original);

        // Each row of 100 identical pixels → 1 RLE packet (count=100, BGR=3 bytes) = 4 bytes × ceil(100/255)
        // So encoded size should be much less than raw
        await Assert.That(encoded.Length).IsLessThan(15000);
    }

    [Test]
    public async Task Pix_FormatRegistry_DetectsExtension()
    {
        await Assert.That(FormatRegistry.DetectFromExtension("test.pix")).IsEqualTo(ImageFileFormat.Pix);
        await Assert.That(FormatRegistry.DetectFromExtension("test.alias")).IsEqualTo(ImageFileFormat.Pix);
    }

    [Test]
    public async Task Pix_FormatRegistry_RoundTrip()
    {
        var original = CreateTestFrame(20, 20);
        byte[] encoded = FormatRegistry.Encode(original, ImageFileFormat.Pix);
        var decoded = FormatRegistry.Decode(encoded, ImageFileFormat.Pix);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_Decode_TooShort_Throws()
    {
        var badData = new byte[] { 0, 0, 0 };
        await Assert.That(() => PixCoder.Decode(badData)).Throws<InvalidDataException>();
    }

    // ========== SUN Tests ==========

    [Test]
    public async Task Sun_CanDecode_ValidHeader_ReturnsTrue()
    {
        var data = new byte[32];
        data[0] = 0x59; data[1] = 0xa6; data[2] = 0x6a; data[3] = 0x95;
        await Assert.That(SunCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task Sun_CanDecode_InvalidHeader_ReturnsFalse()
    {
        var data = new byte[32];
        await Assert.That(SunCoder.CanDecode(data)).IsFalse();
    }

    [Test]
    public async Task Sun_CanDecode_TooShort_ReturnsFalse()
    {
        var data = new byte[10];
        await Assert.That(SunCoder.CanDecode(data)).IsFalse();
    }

    [Test]
    public async Task Sun_RoundTrip_Rgb()
    {
        var original = CreateTestFrame(32, 24);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_RoundTrip_Rgba()
    {
        var original = CreateTestFrame(16, 16, hasAlpha: true);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        await Assert.That(decoded.HasAlpha).IsTrue();
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_RoundTrip_SolidColor()
    {
        var original = CreateSolidFrame(20, 15, 100, 200, 50);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_RoundTrip_Gradient()
    {
        var original = CreateGradientFrame(64, 64);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_RoundTrip_SmallImage()
    {
        var original = CreateTestFrame(1, 1);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(1u);
        await Assert.That(decoded.Rows).IsEqualTo(1u);
        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_RoundTrip_LargeImage()
    {
        var original = CreateTestFrame(256, 256);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_Header_HasCorrectMagic()
    {
        var frame = CreateTestFrame(10, 10);
        byte[] encoded = SunCoder.Encode(frame);

        await Assert.That(encoded[0]).IsEqualTo((byte)0x59);
        await Assert.That(encoded[1]).IsEqualTo((byte)0xa6);
        await Assert.That(encoded[2]).IsEqualTo((byte)0x6a);
        await Assert.That(encoded[3]).IsEqualTo((byte)0x95);
    }

    [Test]
    public async Task Sun_FormatRegistry_DetectsFormat()
    {
        var frame = CreateTestFrame(10, 10);
        byte[] encoded = SunCoder.Encode(frame);

        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Sun);
    }

    [Test]
    public async Task Sun_FormatRegistry_DetectsExtension()
    {
        await Assert.That(FormatRegistry.DetectFromExtension("test.sun")).IsEqualTo(ImageFileFormat.Sun);
        await Assert.That(FormatRegistry.DetectFromExtension("test.ras")).IsEqualTo(ImageFileFormat.Sun);
    }

    [Test]
    public async Task Sun_FormatRegistry_RoundTrip()
    {
        var original = CreateTestFrame(20, 20);
        byte[] encoded = FormatRegistry.Encode(original, ImageFileFormat.Sun);
        var decoded = FormatRegistry.Decode(encoded, ImageFileFormat.Sun);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_Decode_InvalidData_Throws()
    {
        var badData = new byte[] { 0, 0, 0, 0 };
        await Assert.That(() => SunCoder.Decode(badData)).Throws<InvalidDataException>();
    }

    // ========== Cross-format interop tests ==========

    [Test]
    public async Task CrossFormat_Sgi_Pix_Sun_SamePixels()
    {
        var original = CreateTestFrame(40, 30);

        byte[] sgiData = SgiCoder.Encode(original);
        byte[] pixData = PixCoder.Encode(original);
        byte[] sunData = SunCoder.Encode(original);

        var fromSgi = SgiCoder.Decode(sgiData);
        var fromPix = PixCoder.Decode(pixData);
        var fromSun = SunCoder.Decode(sunData);

        // All three should produce identical results (within tolerance for 8-bit quantization)
        await AssertFramesEqual(fromSgi, fromPix, tolerance: 1);
        await AssertFramesEqual(fromSgi, fromSun, tolerance: 1);
        await AssertFramesEqual(fromPix, fromSun, tolerance: 1);
    }

    [Test]
    public async Task Sgi_RoundTrip_OddDimensions()
    {
        var original = CreateTestFrame(17, 13);
        byte[] encoded = SgiCoder.Encode(original);
        var decoded = SgiCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Pix_RoundTrip_OddDimensions()
    {
        var original = CreateTestFrame(17, 13);
        byte[] encoded = PixCoder.Encode(original);
        var decoded = PixCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }

    [Test]
    public async Task Sun_RoundTrip_OddWidth_PaddingCorrect()
    {
        // Odd width × 3 bytes = odd scanline → padding byte required
        var original = CreateTestFrame(17, 13);
        byte[] encoded = SunCoder.Encode(original);
        var decoded = SunCoder.Decode(encoded);

        await AssertFramesEqual(original, decoded, tolerance: 1);
    }
}
