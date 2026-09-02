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
    // JPEG 2000 is intentionally NOT implemented: the previous "codec" only round-tripped its
    // own non-standard codestream and produced garbage (~6 dB PSNR) for real .jp2 files, so
    // the public API now fails loudly instead of lying. See Jpeg2000Coder.

    [Test]
    public async Task Jpeg2000_Decode_ThrowsNotSupported()
    {
        await Assert.That(() => Jpeg2000Coder.Decode(new byte[64])).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Jpeg2000_Encode_ThrowsNotSupported()
    {
        using var frame = CreateTestFrame(8, 8);
        await Assert.That(() => Jpeg2000Coder.Encode(frame)).Throws<NotSupportedException>();
    }

    // ======================= JPEG XL =======================

    // JPEG XL decoding is real (from-scratch pure-C# lossless Modular decoder). Encoding is not
    // implemented, so it fails loudly rather than emitting a non-standard bitstream. Real-file
    // decode is verified bit-exactly in ExtendedFormatTests against libjxl-produced assets.

    [Test]
    public async Task JpegXl_DecodesRealFile()
    {
        // jxl_gradient.jxl: 16x16 lossless Modular produced by libjxl.
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "TestAssets", "jxl_gradient.jxl");
        using var decoded = JxlCoder.Decode(System.IO.File.ReadAllBytes(path));
        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);
        int fc = decoded.NumberOfChannels;
        ushort[] row5 = decoded.GetPixelRow(5).ToArray();
        // Pixel (5,5): R=min(255,5*17)=85, G=min(255,5*17)=85, B=min(255,10*8)=80.
        await Assert.That(row5[(5 * fc) + 0]).IsEqualTo(Quantum.ScaleFromByte(85));
        await Assert.That(row5[(5 * fc) + 1]).IsEqualTo(Quantum.ScaleFromByte(85));
        await Assert.That(row5[(5 * fc) + 2]).IsEqualTo(Quantum.ScaleFromByte(80));
    }

    [Test]
    public async Task JpegXl_Encode_RoundTrips_Losslessly()
    {
        // JPEG XL lossless (Modular) encoding is implemented; the round-trip must be pixel-exact.
        using var frame = CreateTestFrame(8, 8);
        byte[] jxl = JxlCoder.Encode(frame);
        await Assert.That(JxlCoder.CanDecode(jxl)).IsTrue();

        using var decoded = JxlCoder.Decode(jxl);
        int fc = frame.NumberOfChannels;
        int dc = decoded.NumberOfChannels;
        long maxDiff = 0;
        for (int y = 0; y < frame.Rows; y++)
        {
            ushort[] rf = frame.GetPixelRow(y).ToArray();
            ushort[] rd = decoded.GetPixelRow(y).ToArray();
            for (int x = 0; x < frame.Columns; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int a = Quantum.ScaleToByte(rf[(x * fc) + Math.Min(c, fc - 1)]);
                    int b = Quantum.ScaleToByte(rd[(x * dc) + Math.Min(c, dc - 1)]);
                    maxDiff = Math.Max(maxDiff, Math.Abs(a - b));
                }
            }
        }

        await Assert.That(maxDiff).IsEqualTo(0L);
    }

    // ======================= AVIF =======================
    // AVIF decoding is real (vendored AV1 intra decoder). Encoding is not implemented
    // (no AV1 encoder). HEIC decoding is pending an HEVC decoder. See HeifCoder.

    // Reference RGB sampled from libavif's own decode of TestAssets/avif_sample.avif.
    // Tolerance absorbs the chroma-upsampling difference (box here vs libavif's bilinear).
    private static readonly (int Y, int X, int R, int G, int B)[] AvifReference =
    [
        (0, 0, 1, 1, 124), (0, 63, 250, 3, 128), (47, 0, 2, 254, 129), (47, 63, 255, 255, 130),
        (12, 20, 220, 41, 29), (34, 45, 19, 81, 219), (24, 32, 132, 132, 132),
        (3, 3, 12, 16, 126), (40, 10, 38, 217, 127), (20, 50, 201, 108, 126),
    ];

    [Test]
    public async Task Avif_DecodesRealFile()
    {
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "TestAssets", "avif_sample.avif");
        await Assert.That(System.IO.File.Exists(path)).IsTrue();

        using var image = HeifCoder.Decode(System.IO.File.ReadAllBytes(path));
        await Assert.That((int)image.Columns).IsEqualTo(64);
        await Assert.That((int)image.Rows).IsEqualTo(48);

        foreach (var (y, x, r, g, b) in AvifReference)
        {
            var row = image.GetPixelRow(y).ToArray();
            int off = x * image.NumberOfChannels;
            await Assert.That(Math.Abs(Quantum.ScaleToByte(row[off]) - r)).IsLessThanOrEqualTo(12);
            await Assert.That(Math.Abs(Quantum.ScaleToByte(row[off + 1]) - g)).IsLessThanOrEqualTo(12);
            await Assert.That(Math.Abs(Quantum.ScaleToByte(row[off + 2]) - b)).IsLessThanOrEqualTo(12);
        }
    }

    [Test]
    public async Task Avif_Encode_NotSupported()
    {
        using var frame = CreateTestFrame(16, 16);
        await Assert.That(() => HeifCoder.Encode(frame, HeifContainerType.Avif)).Throws<NotSupportedException>();
    }

    // ======================= HEIC =======================
    // HEIC decoding is real (vendored HEVC decoder). Reference pixels captured from the
    // decoder's own output on TestAssets/hevc_sample.heic (a small self-built HEIC whose
    // HEVC stream ffmpeg agrees with to maxdiff 33 — i.e. genuinely decoded, not garbage).
    private static readonly (int Y, int X, int R, int G, int B)[] HeicReference =
    [
        (0, 0, 0, 10, 133), (0, 95, 255, 32, 124), (63, 0, 0, 220, 127), (63, 95, 255, 245, 132),
        (18, 30, 238, 57, 30), (46, 60, 14, 91, 217), (32, 48, 123, 128, 129),
        (5, 5, 15, 27, 136), (50, 10, 12, 180, 128), (25, 80, 226, 113, 130),
    ];

    [Test]
    public async Task Heic_DecodesRealFile()
    {
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "TestAssets", "hevc_sample.heic");
        await Assert.That(System.IO.File.Exists(path)).IsTrue();

        using var image = HeifCoder.Decode(System.IO.File.ReadAllBytes(path));
        await Assert.That((int)image.Columns).IsEqualTo(96);
        await Assert.That((int)image.Rows).IsEqualTo(64);

        foreach (var (y, x, r, g, b) in HeicReference)
        {
            var row = image.GetPixelRow(y).ToArray();
            int off = x * image.NumberOfChannels;
            await Assert.That(Math.Abs(Quantum.ScaleToByte(row[off]) - r)).IsLessThanOrEqualTo(2);
            await Assert.That(Math.Abs(Quantum.ScaleToByte(row[off + 1]) - g)).IsLessThanOrEqualTo(2);
            await Assert.That(Math.Abs(Quantum.ScaleToByte(row[off + 2]) - b)).IsLessThanOrEqualTo(2);
        }
    }

    [Test]
    public async Task Heic_Encode_Produces_Decodable_File()
    {
        // HEIC encoding is implemented (pure-C# HEVC intra encoder); AVIF encoding is not.
        using var frame = CreateTestFrame(32, 32);
        byte[] heic = HeifCoder.Encode(frame, HeifContainerType.Heic);
        await Assert.That(HeifCoder.CanDecode(heic)).IsTrue();
        await Assert.That(() => HeifCoder.Encode(frame, HeifContainerType.Avif)).Throws<NotSupportedException>();
    }

    // ======================= Cross-Format =======================

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
