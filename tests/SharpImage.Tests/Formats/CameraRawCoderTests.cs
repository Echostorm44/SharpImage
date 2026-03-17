using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// TUnit tests for camera raw format support.
/// Covers: signature detection, synthetic Bayer demosaic, processing pipeline, and real file decoding.
/// </summary>
public class CameraRawCoderTests
{
    private static readonly string TestAssets =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    // ── Signature Detection ─────────────────────────────────────────────────

    [Test]
    public async Task CameraRaw_DetectFormat_CR2()
    {
        // CR2: II\x2A\x00 ... CR\x02 at offset 8-10
        byte[] header = [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52, 0x02, 0x00];
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.CR2);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_CR3()
    {
        // CR3: ftyp box with "crx" brand
        byte[] header = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x63, 0x72, 0x78, 0x20, 0x00, 0x00, 0x00, 0x00];
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.CR3);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_RAF()
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW 0201");
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.RAF);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_ORF()
    {
        // ORF: II + 0x4F52 magic
        byte[] header = [0x49, 0x49, 0x52, 0x4F, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.ORF);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_RW2()
    {
        // RW2: II + 0x0055 magic
        byte[] header = [0x49, 0x49, 0x55, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.RW2);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_MRW()
    {
        byte[] header = [0x00, 0x4D, 0x52, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.MRW);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_X3F()
    {
        byte[] header = [0x46, 0x4F, 0x56, 0x62, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var format = CameraRawCoder.DetectRawFormat(header);
        await Assert.That(format).IsEqualTo(CameraRawFormat.X3F);
    }

    [Test]
    public async Task CameraRaw_DetectFormat_UnknownData()
    {
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
        var format = CameraRawCoder.DetectRawFormat(pngHeader);
        await Assert.That(format).IsEqualTo(CameraRawFormat.Unknown);
    }

    [Test]
    public async Task CameraRaw_CanDecode_ValidSignatures()
    {
        byte[] cr2 = [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52, 0x02, 0x00];
        await Assert.That(CameraRawCoder.CanDecode(cr2)).IsTrue();
    }

    [Test]
    public async Task CameraRaw_CanDecode_InvalidSignature()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
        await Assert.That(CameraRawCoder.CanDecode(png)).IsFalse();
    }

    // ── FormatRegistry Integration ──────────────────────────────────────────

    [Test]
    public async Task FormatRegistry_DetectsFromExtension_CR2()
    {
        var format = FormatRegistry.DetectFromExtension("photo.cr2");
        await Assert.That(format).IsEqualTo(ImageFileFormat.CameraRaw);
    }

    [Test]
    public async Task FormatRegistry_DetectsFromExtension_DNG()
    {
        var format = FormatRegistry.DetectFromExtension("photo.dng");
        await Assert.That(format).IsEqualTo(ImageFileFormat.CameraRaw);
    }

    [Test]
    public async Task FormatRegistry_DetectsFromExtension_NEF()
    {
        var format = FormatRegistry.DetectFromExtension("photo.nef");
        await Assert.That(format).IsEqualTo(ImageFileFormat.CameraRaw);
    }

    [Test]
    [Arguments(".arw")]
    [Arguments(".orf")]
    [Arguments(".rw2")]
    [Arguments(".raf")]
    [Arguments(".pef")]
    [Arguments(".srw")]
    [Arguments(".3fr")]
    [Arguments(".iiq")]
    [Arguments(".x3f")]
    [Arguments(".dcr")]
    [Arguments(".mef")]
    [Arguments(".mrw")]
    [Arguments(".erf")]
    public async Task FormatRegistry_DetectsFromExtension_AllRawFormats(string ext)
    {
        var format = FormatRegistry.DetectFromExtension($"photo{ext}");
        await Assert.That(format).IsEqualTo(ImageFileFormat.CameraRaw);
    }

    // ── Synthetic Bayer Demosaic Tests ──────────────────────────────────────

    [Test]
    public async Task Demosaic_Bilinear_SyntheticRGGB_ProducesValidImage()
    {
        // Create 8×8 synthetic Bayer RGGB sensor data
        int w = 8, h = 8;
        var pixels = CreateSyntheticBayer(w, h, BayerPattern.RGGB);
        var raw = new RawSensorData
        {
            RawPixels = pixels,
            Width = w,
            Height = h,
            Metadata = new CameraRawMetadata { BayerPattern = BayerPattern.RGGB, CfaType = CfaType.Bayer }
        };

        var frame = BayerDemosaic.Demosaic(in raw, DemosaicAlgorithm.Bilinear);

        await Assert.That(frame.Columns).IsEqualTo(8L);
        await Assert.That(frame.Rows).IsEqualTo(8L);

        // Center pixel should have all channels non-zero (interpolated)
        var px = frame.GetPixel(4, 4);
        await Assert.That((int)px.Red).IsGreaterThan(0);
        await Assert.That((int)px.Green).IsGreaterThan(0);
        await Assert.That((int)px.Blue).IsGreaterThan(0);
    }

    [Test]
    public async Task Demosaic_VNG_SyntheticRGGB_ProducesValidImage()
    {
        int w = 16, h = 16;
        var pixels = CreateSyntheticBayer(w, h, BayerPattern.RGGB);
        var raw = new RawSensorData
        {
            RawPixels = pixels,
            Width = w,
            Height = h,
            Metadata = new CameraRawMetadata { BayerPattern = BayerPattern.RGGB, CfaType = CfaType.Bayer }
        };

        var frame = BayerDemosaic.Demosaic(in raw, DemosaicAlgorithm.VNG);

        await Assert.That(frame.Columns).IsEqualTo(16L);
        await Assert.That(frame.Rows).IsEqualTo(16L);
    }

    [Test]
    public async Task Demosaic_AHD_SyntheticRGGB_ProducesValidImage()
    {
        int w = 16, h = 16;
        var pixels = CreateSyntheticBayer(w, h, BayerPattern.RGGB);
        var raw = new RawSensorData
        {
            RawPixels = pixels,
            Width = w,
            Height = h,
            Metadata = new CameraRawMetadata { BayerPattern = BayerPattern.RGGB, CfaType = CfaType.Bayer }
        };

        var frame = BayerDemosaic.Demosaic(in raw, DemosaicAlgorithm.AHD);

        await Assert.That(frame.Columns).IsEqualTo(16L);
        await Assert.That(frame.Rows).IsEqualTo(16L);
    }

    [Test]
    [Arguments(BayerPattern.RGGB)]
    [Arguments(BayerPattern.BGGR)]
    [Arguments(BayerPattern.GRBG)]
    [Arguments(BayerPattern.GBRG)]
    public async Task Demosaic_AllBayerPatterns_ProduceNonZeroOutput(BayerPattern pattern)
    {
        int w = 16, h = 16;
        var pixels = CreateSyntheticBayer(w, h, pattern);
        var raw = new RawSensorData
        {
            RawPixels = pixels,
            Width = w,
            Height = h,
            Metadata = new CameraRawMetadata { BayerPattern = pattern, CfaType = CfaType.Bayer }
        };

        var frame = BayerDemosaic.Demosaic(in raw, DemosaicAlgorithm.Bilinear);

        await Assert.That(frame.Columns).IsEqualTo(16L);
        // Verify non-trivial output
        long sum = 0;
        for (int y = 2; y < 14; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 6; x < 42; x++) sum += row[x]; // channels 2-13
        }
        await Assert.That(sum).IsGreaterThan(0);
    }

    // ── Processing Pipeline Tests ───────────────────────────────────────────

    [Test]
    public async Task ScaleToFullRange_ScalesCorrectly()
    {
        var raw = new RawSensorData
        {
            RawPixels = new ushort[] { 0, 1000, 4000, 8000, 16383 },
            Width = 5, Height = 1,
            Metadata = new CameraRawMetadata
            {
                BitsPerSample = 14,
                WhiteLevel = 16383,
                BlackLevel = 0
            }
        };

        CameraRawProcessor.ScaleToFullRange(ref raw);

        // After scaling, max value should be near 65535
        await Assert.That((int)raw.RawPixels[4]).IsGreaterThanOrEqualTo(65530);
        // Mid-value should be proportional
        await Assert.That((int)raw.RawPixels[2]).IsGreaterThan(15000);
    }

    // ── CFA Pattern Detection (tested via known format signatures) ─────────

    [Test]
    public async Task CameraRaw_BayerPattern_RGGB_Enum_Exists()
    {
        // Verify the BayerPattern enum has expected values
        await Assert.That(BayerPattern.RGGB).IsNotEqualTo(BayerPattern.Unknown);
        await Assert.That(BayerPattern.BGGR).IsNotEqualTo(BayerPattern.Unknown);
        await Assert.That(BayerPattern.GRBG).IsNotEqualTo(BayerPattern.Unknown);
        await Assert.That(BayerPattern.GBRG).IsNotEqualTo(BayerPattern.Unknown);
    }

    [Test]
    public async Task CameraRaw_DecodeOptions_DefaultValues()
    {
        var opts = CameraRawDecodeOptions.Default;
        await Assert.That(opts.Algorithm).IsEqualTo(DemosaicAlgorithm.AHD);
        await Assert.That(opts.ApplyGamma).IsTrue();
        await Assert.That(opts.ApplyColorMatrix).IsTrue();
        await Assert.That(opts.OutputBitDepth).IsEqualTo(16);
    }

    // ── Real File Decode Tests ──────────────────────────────────────────────

    [Test]
    public async Task Decode_RealCR2_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.cr2");
        if (!File.Exists(path)) { Assert.Fail("sample1.cr2 not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var format = CameraRawCoder.DetectRawFormat(data);
        await Assert.That(format).IsEqualTo(CameraRawFormat.CR2);

        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
        await Assert.That(frame.Columns).IsGreaterThan(1000); // CR2 should be high-res
    }

    [Test]
    public async Task Decode_RealDNG_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.dng");
        if (!File.Exists(path)) { Assert.Fail("sample1.dng not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var format = CameraRawCoder.DetectRawFormat(data);
        await Assert.That(format).IsEqualTo(CameraRawFormat.DNG);

        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
    }

    [Test]
    public async Task Decode_RealNEF_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.nef");
        if (!File.Exists(path)) { Assert.Fail("sample1.nef not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
    }

    [Test]
    public async Task Decode_RealORF_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.orf");
        if (!File.Exists(path)) { Assert.Fail("sample1.orf not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
    }

    [Test]
    public async Task Decode_RealPEF_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.pef");
        if (!File.Exists(path)) { Assert.Fail("sample1.pef not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
    }

    [Test]
    public async Task Decode_RealRAF_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.raf");
        if (!File.Exists(path)) { Assert.Fail("sample1.raf not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var format = CameraRawCoder.DetectRawFormat(data);
        await Assert.That(format).IsEqualTo(CameraRawFormat.RAF);

        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
    }

    [Test]
    public async Task Decode_RealRW2_ProducesValidImage()
    {
        string path = Path.Combine(TestAssets, "sample1.rw2");
        if (!File.Exists(path)) { Assert.Fail("sample1.rw2 not found in TestAssets"); return; }

        byte[] data = File.ReadAllBytes(path);
        var format = CameraRawCoder.DetectRawFormat(data);
        await Assert.That(format).IsEqualTo(CameraRawFormat.RW2);

        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        await Assert.That(frame.Columns).IsGreaterThan(0);
        await Assert.That(frame.Rows).IsGreaterThan(0);
    }

    // ── FormatRegistry Read Integration ─────────────────────────────────────

    [Test]
    public async Task FormatRegistry_Read_CR2_Succeeds()
    {
        string path = Path.Combine(TestAssets, "sample1.cr2");
        if (!File.Exists(path)) { Assert.Fail("sample1.cr2 not found"); return; }

        byte[] data = File.ReadAllBytes(path);
        var detectedFormat = FormatRegistry.DetectFormat(data);
        await Assert.That(detectedFormat).IsEqualTo(ImageFileFormat.CameraRaw);
    }

    [Test]
    public async Task FormatRegistry_Read_DNG_Succeeds()
    {
        string path = Path.Combine(TestAssets, "sample1.dng");
        if (!File.Exists(path)) { Assert.Fail("sample1.dng not found"); return; }

        byte[] data = File.ReadAllBytes(path);
        var detectedFormat = FormatRegistry.DetectFormat(data);
        await Assert.That(detectedFormat).IsEqualTo(ImageFileFormat.CameraRaw);
    }

    // ── Cross-Format: Decode Raw → Encode PNG ───────────────────────────────

    [Test]
    public async Task CrossFormat_DecodeDNG_EncodePNG_Roundtrip()
    {
        string path = Path.Combine(TestAssets, "sample1.dng");
        if (!File.Exists(path)) { Assert.Fail("sample1.dng not found"); return; }

        byte[] data = File.ReadAllBytes(path);
        var opts = new CameraRawDecodeOptions
        {
            Algorithm = DemosaicAlgorithm.Bilinear,
            ApplyGamma = true,
            ApplyColorMatrix = false
        };
        var frame = CameraRawCoder.Decode(data, opts);

        // Encode as PNG
        using var ms = new MemoryStream();
        PngCoder.Write(frame, ms);
        byte[] pngBytes = ms.ToArray();

        await Assert.That(pngBytes.Length).IsGreaterThan(0);

        // Verify PNG is valid
        var pngFrame = PngCoder.Read(new MemoryStream(pngBytes));
        await Assert.That(pngFrame.Columns).IsEqualTo(frame.Columns);
        await Assert.That(pngFrame.Rows).IsEqualTo(frame.Rows);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates synthetic Bayer sensor data with known color values for testing demosaic algorithms.
    /// Red=65535, Green=32768, Blue=16384 at their respective CFA positions.
    /// </summary>
    private static ushort[] CreateSyntheticBayer(int width, int height, BayerPattern pattern)
    {
        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int color = GetBayerColor(x, y, pattern);
                pixels[y * width + x] = color switch
                {
                    0 => 65535,  // Red
                    1 => 32768,  // Green
                    2 => 16384,  // Blue
                    _ => 0
                };
            }
        }
        return pixels;
    }

    /// <summary>Returns 0=Red, 1=Green, 2=Blue for a given pixel position in the Bayer pattern.</summary>
    private static int GetBayerColor(int x, int y, BayerPattern pattern)
    {
        int px = x & 1, py = y & 1;
        return pattern switch
        {
            BayerPattern.RGGB => (py, px) switch { (0, 0) => 0, (0, 1) => 1, (1, 0) => 1, _ => 2 },
            BayerPattern.BGGR => (py, px) switch { (0, 0) => 2, (0, 1) => 1, (1, 0) => 1, _ => 0 },
            BayerPattern.GRBG => (py, px) switch { (0, 0) => 1, (0, 1) => 0, (1, 0) => 2, _ => 1 },
            BayerPattern.GBRG => (py, px) switch { (0, 0) => 1, (0, 1) => 2, (1, 0) => 0, _ => 1 },
            _ => 1
        };
    }
}
