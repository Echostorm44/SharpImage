// Round-trip correctness tests for modern format codecs: Cineon, JPEG 2000, JPEG XL, AVIF, HEIC.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

public class ModernFormatTests
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
                row[offset + 1] = Quantum.ScaleFromByte(g);
                row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }

        return frame;
    }

    private static void AssertPixelsEqual(ImageFrame expected, ImageFrame actual, int tolerance = 0)
    {
        if (actual.Columns != expected.Columns || actual.Rows != expected.Rows)
            throw new Exception($"Dimension mismatch: expected {expected.Columns}x{expected.Rows}, got {actual.Columns}x{actual.Rows}");

        int channels = Math.Min(expected.NumberOfChannels, actual.NumberOfChannels);
        for (int y = 0; y < (int)expected.Rows; y++)
        {
            var expectedRow = expected.GetPixelRow(y);
            var actualRow = actual.GetPixelRow(y);
            for (int x = 0; x < (int)expected.Columns; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int idx = x * expected.NumberOfChannels + c;
                    int aIdx = x * actual.NumberOfChannels + c;
                    int diff = Math.Abs(expectedRow[idx] - actualRow[aIdx]);
                    if (diff > tolerance)
                        throw new Exception(
                            $"Pixel mismatch at ({x},{y}) ch{c}: expected {expectedRow[idx]}, got {actualRow[aIdx]} (diff {diff} > tol {tolerance})");
                }
            }
        }
    }

    // ======================= CINEON =======================

    [Test]
    public async Task Cineon_RoundTrip_PreservesDimensions()
    {
        using var original = CreateTestFrame(24, 16);
        byte[] encoded = CinCoder.Encode(original);
        using var decoded = CinCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        await Assert.That(decoded.NumberOfChannels).IsEqualTo(original.NumberOfChannels);
    }

    [Test]
    public async Task Cineon_RoundTrip_PixelData_Within10BitPrecision()
    {
        // Cineon is 10-bit, so 16-bit values lose low 6 bits: max error ~64
        using var original = CreateSolidFrame(8, 8, 200, 100, 50);
        byte[] encoded = CinCoder.Encode(original);
        using var decoded = CinCoder.Decode(encoded);

        AssertPixelsEqual(original, decoded, tolerance: 128);
        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
    }

    [Test]
    public async Task Cineon_RoundTrip_SmallImage()
    {
        using var original = CreateTestFrame(2, 2);
        byte[] encoded = CinCoder.Encode(original);
        using var decoded = CinCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(2);
        await Assert.That(decoded.Rows).IsEqualTo(2);
    }

    [Test]
    public async Task Cineon_EncodedData_HasValidSize()
    {
        using var frame = CreateTestFrame(16, 12);
        byte[] data = CinCoder.Encode(frame);

        await Assert.That(data.Length).IsGreaterThan(712); // minimum Cineon header
    }

    // ======================= JPEG 2000 =======================

    [Test]
    public async Task Jpeg2000_RoundTrip_Lossless_RGB()
    {
        using var original = CreateTestFrame(16, 12);
        byte[] encoded = Jpeg2000Coder.Encode(original);
        using var decoded = Jpeg2000Coder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        AssertPixelsEqual(original, decoded, tolerance: 0);
    }

    [Test]
    public async Task Jpeg2000_RoundTrip_Lossless_RGBA()
    {
        using var original = CreateTestFrame(12, 8, hasAlpha: true);
        byte[] encoded = Jpeg2000Coder.Encode(original);
        using var decoded = Jpeg2000Coder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        AssertPixelsEqual(original, decoded, tolerance: 0);
    }

    [Test]
    public async Task Jpeg2000_RoundTrip_SolidColor()
    {
        using var original = CreateSolidFrame(8, 8, 255, 0, 128);
        byte[] encoded = Jpeg2000Coder.Encode(original);
        using var decoded = Jpeg2000Coder.Decode(encoded);

        AssertPixelsEqual(original, decoded, tolerance: 0);
        await Assert.That(decoded.Columns).IsEqualTo(8);
    }

    [Test]
    public async Task Jpeg2000_RoundTrip_LargerImage()
    {
        using var original = CreateTestFrame(64, 48);
        byte[] encoded = Jpeg2000Coder.Encode(original);
        using var decoded = Jpeg2000Coder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(64);
        await Assert.That(decoded.Rows).IsEqualTo(48);
        // With correct bit-depth encoding, 5/3 DWT is mathematically lossless
        AssertPixelsEqual(original, decoded, tolerance: 0);
    }

    [Test]
    public async Task Jpeg2000_EncodedData_HasJp2Signature()
    {
        using var frame = CreateTestFrame(8, 8);
        byte[] data = Jpeg2000Coder.Encode(frame);

        await Assert.That(data.Length).IsGreaterThan(12);
        // JP2 signature box at offset 4: 'jP'
        await Assert.That(data[4]).IsEqualTo((byte)'j');
        await Assert.That(data[5]).IsEqualTo((byte)'P');
    }

    // ======================= JPEG XL =======================

    [Test]
    public async Task JpegXl_RoundTrip_Lossless_RGB()
    {
        using var original = CreateTestFrame(16, 12);
        byte[] encoded = JxlCoder.Encode(original);
        using var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        AssertPixelsEqual(original, decoded, tolerance: 0);
    }

    [Test]
    public async Task JpegXl_RoundTrip_Lossless_RGBA()
    {
        using var original = CreateTestFrame(12, 10, hasAlpha: true);
        byte[] encoded = JxlCoder.Encode(original);
        using var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
        AssertPixelsEqual(original, decoded, tolerance: 0);
    }

    [Test]
    public async Task JpegXl_Lossy_PreservesDimensions()
    {
        using var original = CreateTestFrame(32, 24);
        byte[] encoded = JxlCoder.EncodeLossy(original, quality: 90);
        using var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
    }

    [Test]
    public async Task JpegXl_Lossy_HighQuality_CloseToOriginal()
    {
        using var original = CreateSolidFrame(16, 16, 180, 90, 45);
        byte[] encoded = JxlCoder.EncodeLossy(original, quality: 95);
        using var decoded = JxlCoder.Decode(encoded);

        // High quality lossy: within 10% of quantum range
        AssertPixelsEqual(original, decoded, tolerance: Quantum.MaxValue / 10);
        await Assert.That(decoded.Columns).IsEqualTo(16);
    }

    [Test]
    public async Task JpegXl_EncodedData_HasCorrectSignature()
    {
        using var frame = CreateTestFrame(8, 8);
        byte[] data = JxlCoder.Encode(frame);

        await Assert.That(data.Length).IsGreaterThan(2);
        // JXL bare codestream: 0xFF 0x0A
        await Assert.That(data[0]).IsEqualTo((byte)0xFF);
        await Assert.That(data[1]).IsEqualTo((byte)0x0A);
    }

    [Test]
    public async Task JpegXl_RoundTrip_SmallImage()
    {
        using var original = CreateSolidFrame(2, 2, 100, 200, 50);
        byte[] encoded = JxlCoder.Encode(original);
        using var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(2);
        await Assert.That(decoded.Rows).IsEqualTo(2);
        AssertPixelsEqual(original, decoded, tolerance: 0);
    }

    // ======================= AVIF =======================

    [Test]
    public async Task Avif_RoundTrip_PreservesDimensions()
    {
        using var original = CreateTestFrame(32, 24);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Avif);
        using var decoded = HeifCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
    }

    [Test]
    public async Task Avif_RoundTrip_SolidColor_ReasonableAccuracy()
    {
        using var original = CreateSolidFrame(16, 16, 200, 100, 50);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Avif);
        using var decoded = HeifCoder.Decode(encoded);

        // Lossy: AVIF uses DC-only intra coding with significant quantization
        AssertPixelsEqual(original, decoded, tolerance: Quantum.MaxValue / 2);
        await Assert.That(decoded.Columns).IsEqualTo(16);
    }

    [Test]
    public async Task Avif_EncodedData_HasFtypBox()
    {
        using var frame = CreateTestFrame(16, 16);
        byte[] data = HeifCoder.Encode(frame, HeifContainerType.Avif);

        await Assert.That(data.Length).IsGreaterThan(12);
        // ISOBMFF: "ftyp" at offset 4
        await Assert.That(data[4]).IsEqualTo((byte)'f');
        await Assert.That(data[5]).IsEqualTo((byte)'t');
        await Assert.That(data[6]).IsEqualTo((byte)'y');
        await Assert.That(data[7]).IsEqualTo((byte)'p');
    }

    // ======================= HEIC =======================

    [Test]
    public async Task Heic_RoundTrip_PreservesDimensions()
    {
        using var original = CreateTestFrame(32, 24);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Heic);
        using var decoded = HeifCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);
    }

    [Test]
    public async Task Heic_RoundTrip_SolidColor_ReasonableAccuracy()
    {
        using var original = CreateSolidFrame(16, 16, 100, 200, 150);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Heic);
        using var decoded = HeifCoder.Decode(encoded);

        AssertPixelsEqual(original, decoded, tolerance: Quantum.MaxValue / 2);
        await Assert.That(decoded.Columns).IsEqualTo(16);
    }

    [Test]
    public async Task Heic_EncodedData_HasFtypBox()
    {
        using var frame = CreateTestFrame(16, 16);
        byte[] data = HeifCoder.Encode(frame, HeifContainerType.Heic);

        await Assert.That(data.Length).IsGreaterThan(12);
        await Assert.That(data[4]).IsEqualTo((byte)'f');
        await Assert.That(data[5]).IsEqualTo((byte)'t');
        await Assert.That(data[6]).IsEqualTo((byte)'y');
        await Assert.That(data[7]).IsEqualTo((byte)'p');
    }

    // ======================= Cross-Format =======================

    [Test]
    public async Task Jpeg2000_ToJpegXl_Lossless_RoundTrip()
    {
        using var original = CreateTestFrame(16, 12);

        byte[] jp2Data = Jpeg2000Coder.Encode(original);
        using var fromJp2 = Jpeg2000Coder.Decode(jp2Data);
        byte[] jxlData = JxlCoder.Encode(fromJp2);
        using var fromJxl = JxlCoder.Decode(jxlData);

        AssertPixelsEqual(original, fromJxl, tolerance: 0);
        await Assert.That(fromJxl.Columns).IsEqualTo(original.Columns);
    }

    [Test]
    public async Task Cineon_ToDpx_SimilarPrecision()
    {
        using var original = CreateSolidFrame(8, 8, 128, 64, 192);

        byte[] cinData = CinCoder.Encode(original);
        using var fromCin = CinCoder.Decode(cinData);

        byte[] dpxData = DpxCoder.Encode(original);
        using var fromDpx = DpxCoder.Decode(dpxData);

        // Both 10-bit film formats — similar precision
        AssertPixelsEqual(fromCin, fromDpx, tolerance: 256);
        await Assert.That(fromCin.Columns).IsEqualTo(fromDpx.Columns);
    }
}
