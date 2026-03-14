using SharpImage.Core;
using SharpImage.Fourier;
using SharpImage.Image;
using SharpImage.Morphology;
using SharpImage.Quantize;
using SharpImage.Threshold;
using SharpImage.Transform;
using TUnit.Core;

namespace SharpImage.Tests;

/// <summary>
/// Tests for Phase 8: Morphology, Thresholding, Quantization, Distortion.
/// </summary>
public class AdvancedOpsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Morphology Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Erode_WhiteOnBlack_Shrinks()
    {
        // White rectangle (8x8) centered in 16x16 black image
        using var img = CreateBlackWithWhiteCenter(16, 16, 4, 4, 12, 12);
        var kernel = MorphologyOps.Kernel.Square(1);
        using var eroded = MorphologyOps.Erode(img, kernel);

        // Center should still be white, but edges should have moved inward
        byte centerVal = Quantum.ScaleToByte(eroded.GetPixelRow(8)[8 * 3]);
        byte edgeVal = Quantum.ScaleToByte(eroded.GetPixelRow(4)[4 * 3]);

        await Assert.That((int)centerVal).IsEqualTo(255);
        // Edge should now be eroded (black or reduced)
        await Assert.That((int)edgeVal).IsLessThan(255);
    }

    [Test]
    public async Task Dilate_WhiteOnBlack_Grows()
    {
        using var img = CreateBlackWithWhiteCenter(16, 16, 6, 6, 10, 10);
        var kernel = MorphologyOps.Kernel.Square(1);
        using var dilated = MorphologyOps.Dilate(img, kernel);

        // Pixel just outside the white rectangle should now be white (dilated)
        byte justOutside = Quantum.ScaleToByte(dilated.GetPixelRow(5)[8 * 3]);
        await Assert.That((int)justOutside).IsEqualTo(255);
    }

    [Test]
    public async Task Open_RemovesSmallBrightSpots()
    {
        // Black image with a single white pixel (noise)
        using var img = CreateSolid(16, 16, 0, 0, 0);
        img.GetPixelRowForWrite(8)[8 * 3] = Quantum.MaxValue;
        img.GetPixelRowForWrite(8)[8 * 3 + 1] = Quantum.MaxValue;
        img.GetPixelRowForWrite(8)[8 * 3 + 2] = Quantum.MaxValue;

        var kernel = MorphologyOps.Kernel.Square(1);
        using var opened = MorphologyOps.Open(img, kernel);

        // The single white pixel should be removed by opening
        byte val = Quantum.ScaleToByte(opened.GetPixelRow(8)[8 * 3]);
        await Assert.That((int)val).IsEqualTo(0);
    }

    [Test]
    public async Task Close_FillsSmallDarkHoles()
    {
        // White image with a single black pixel (hole)
        using var img = CreateSolid(16, 16, 255, 255, 255);
        img.GetPixelRowForWrite(8)[8 * 3] = 0;
        img.GetPixelRowForWrite(8)[8 * 3 + 1] = 0;
        img.GetPixelRowForWrite(8)[8 * 3 + 2] = 0;

        var kernel = MorphologyOps.Kernel.Square(1);
        using var closed = MorphologyOps.Close(img, kernel);

        // The single black pixel should be filled by closing
        byte val = Quantum.ScaleToByte(closed.GetPixelRow(8)[8 * 3]);
        await Assert.That((int)val).IsEqualTo(255);
    }

    [Test]
    public async Task MorphologicalGradient_HighlightsEdges()
    {
        using var img = CreateBlackWithWhiteCenter(32, 32, 8, 8, 24, 24);
        var kernel = MorphologyOps.Kernel.Square(1);
        using var gradient = MorphologyOps.Gradient(img, kernel);

        // Interior (far from edge) should be black (dilate == erode for interior)
        byte interior = Quantum.ScaleToByte(gradient.GetPixelRow(16)[16 * 3]);
        // Edge should be bright (dilate - erode != 0)
        byte edge = Quantum.ScaleToByte(gradient.GetPixelRow(8)[8 * 3]);

        await Assert.That((int)interior).IsEqualTo(0);
        await Assert.That((int)edge).IsGreaterThan(0);
    }

    [Test]
    public async Task Kernel_Diamond_HasCorrectShape()
    {
        var k = MorphologyOps.Kernel.Diamond(2);
        await Assert.That(k.Width).IsEqualTo(5);
        await Assert.That(k.Height).IsEqualTo(5);
        // Center should be 1
        await Assert.That(k[2, 2]).IsEqualTo(1.0);
        // Corner should be NaN (outside diamond)
        await Assert.That(double.IsNaN(k[0, 0])).IsTrue();
    }

    [Test]
    public async Task Kernel_Disk_CenterIsSet()
    {
        var k = MorphologyOps.Kernel.Disk(3);
        await Assert.That(k.Width).IsEqualTo(7);
        await Assert.That(k[3, 3]).IsEqualTo(1.0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Threshold Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task BinaryThreshold_MidGray_CorrectSplit()
    {
        using var img = CreateGradient(256, 1);
        ThresholdOps.BinaryThreshold(img, 0.5);

        byte dark = Quantum.ScaleToByte(img.GetPixelRow(0)[0]); // x=0 → black
        byte bright = Quantum.ScaleToByte(img.GetPixelRow(0)[255 * 3]); // x=255 → white

        await Assert.That((int)dark).IsEqualTo(0);
        await Assert.That((int)bright).IsEqualTo(255);
    }

    [Test]
    public async Task OtsuThreshold_BimodalImage_FindsGap()
    {
        // Bimodal: left half dark (50), right half bright (200)
        using var img = CreateBimodal(64, 64, 50, 200);
        double threshold = ThresholdOps.OtsuThreshold(img);

        // Otsu should find threshold between the dark and bright values
        await Assert.That(threshold).IsGreaterThanOrEqualTo(50.0 / 255);
        await Assert.That(threshold).IsLessThanOrEqualTo(200.0 / 255);
    }

    [Test]
    public async Task KapurThreshold_BimodalImage_FindsGap()
    {
        using var img = CreateBimodal(64, 64, 40, 210);
        double threshold = ThresholdOps.KapurThreshold(img);

        await Assert.That(threshold).IsGreaterThanOrEqualTo(40.0 / 255);
        await Assert.That(threshold).IsLessThanOrEqualTo(210.0 / 255);
    }

    [Test]
    public async Task TriangleThreshold_Returns_ValidValue()
    {
        using var img = CreateGradient(256, 64);
        double threshold = ThresholdOps.TriangleThreshold(img);

        await Assert.That(threshold).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(threshold).IsLessThanOrEqualTo(1.0);
    }

    [Test]
    public async Task AdaptiveThreshold_UnevenIllumination()
    {
        // Create image with varying background brightness
        using var img = CreateSolid(32, 32, 100, 100, 100);
        // Add bright spot
        for (int y = 12; y < 20; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 12; x < 20; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte(200);
                row[x * 3 + 1] = Quantum.ScaleFromByte(200);
                row[x * 3 + 2] = Quantum.ScaleFromByte(200);
            }
        }

        using var result = ThresholdOps.AdaptiveThreshold(img, 7, 0.05);

        // Interior of bright spot should be white, background should be black
        byte spotCenter = Quantum.ScaleToByte(result.GetPixelRow(16)[16 * 3]);
        await Assert.That((int)spotCenter).IsEqualTo(255);
    }

    [Test]
    public async Task OrderedDither_ReducesToTwoLevels()
    {
        using var img = CreateGradient(32, 32);
        ThresholdOps.OrderedDither(img, bayerOrder: 4, levels: 2);

        // All pixels should be either 0 or 65535
        bool allBinary = true;
        for (int y = 0; y < 32; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < 32; x++)
            {
                ushort v = row[x * 3];
                if (v != 0 && v != Quantum.MaxValue) { allBinary = false; break; }
            }
            if (!allBinary) break;
        }
        await Assert.That(allBinary).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Quantization Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Quantize_ReducesColorCount()
    {
        using var img = CreateRandom(32, 32, seed: 42);
        using var quantized = ColorQuantize.Quantize(img, maxColors: 8, ColorQuantize.DitherMethod.None);

        // Count unique colors
        var colors = new HashSet<uint>();
        for (int y = 0; y < 32; y++)
        {
            var row = quantized.GetPixelRow(y);
            for (int x = 0; x < 32; x++)
            {
                byte r = Quantum.ScaleToByte(row[x * 3]);
                byte g = Quantum.ScaleToByte(row[x * 3 + 1]);
                byte b = Quantum.ScaleToByte(row[x * 3 + 2]);
                colors.Add((uint)(r << 16 | g << 8 | b));
            }
        }

        await Assert.That(colors.Count).IsLessThanOrEqualTo(8);
    }

    [Test]
    public async Task Quantize_FloydSteinberg_ProducesSimilarImage()
    {
        using var img = CreateGradient(64, 64);
        using var quantized = ColorQuantize.Quantize(img, maxColors: 16, ColorQuantize.DitherMethod.FloydSteinberg);

        // The dithered result should still look similar to original (PSNR > 15dB)
        double psnr = SharpImage.Analysis.ImageCompare.PeakSignalToNoiseRatio(img, quantized);
        await Assert.That(psnr).IsGreaterThan(15.0);
    }

    [Test]
    public async Task BuildPalette_ReturnsRequestedCount()
    {
        using var img = CreateRandom(32, 32, seed: 99);
        byte[][] palette = ColorQuantize.BuildPalette(img, 16);

        await Assert.That(palette.Length).IsEqualTo(16);
        // Each palette entry should be RGB
        await Assert.That(palette[0].Length).IsEqualTo(3);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Distortion Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Affine_Identity_PreservesImage()
    {
        using var img = CreateGradient(32, 32);
        var identity = new AffineMatrix { Sx = 1, Rx = 0, Ry = 0, Sy = 1, Tx = 0, Ty = 0 };
        using var result = Distort.Affine(img, identity, 32, 32);

        double mse = SharpImage.Analysis.ImageCompare.MeanSquaredError(img, result);
        // Identity should produce near-identical image (minor bilinear rounding)
        await Assert.That(mse).IsLessThan(0.001);
    }

    [Test]
    public async Task Affine_Scale2x_DoublesSize()
    {
        using var img = CreateSolid(16, 16, 128, 64, 200);
        var scale2x = new AffineMatrix { Sx = 0.5, Rx = 0, Ry = 0, Sy = 0.5, Tx = 0, Ty = 0 };
        using var result = Distort.Affine(img, scale2x, 32, 32);

        await Assert.That((int)result.Columns).IsEqualTo(32);
        await Assert.That((int)result.Rows).IsEqualTo(32);
        // Center pixel should still be approximately the same color
        byte r = Quantum.ScaleToByte(result.GetPixelRow(16)[16 * 3]);
        await Assert.That((int)r).IsGreaterThan(120);
        await Assert.That((int)r).IsLessThan(136);
    }

    [Test]
    public async Task Barrel_Identity_PreservesImage()
    {
        using var img = CreateGradient(32, 32);
        // a=0, b=0, c=0, d=1 → r'=r → identity
        using var result = Distort.Barrel(img, 0, 0, 0, 1);

        double mse = SharpImage.Analysis.ImageCompare.MeanSquaredError(img, result);
        // Identity barrel has minor numerical imprecision from center-offset sampling
        await Assert.That(mse).IsLessThan(0.05);
    }

    [Test]
    public async Task Barrel_Pincushion_ChangesImage()
    {
        using var img = CreateGradient(32, 32);
        using var result = Distort.Barrel(img, 0.1, -0.1, 0, 1.0);

        // Result should differ from original
        double mse = SharpImage.Analysis.ImageCompare.MeanSquaredError(img, result);
        await Assert.That(mse).IsGreaterThan(0);
    }

    [Test]
    public async Task Perspective_Identity_PreservesImage()
    {
        using var img = CreateGradient(32, 32);
        // Map corners to themselves
        double[] src = [0, 0, 31, 0, 31, 31, 0, 31];
        double[] dst = [0, 0, 31, 0, 31, 31, 0, 31];
        using var result = Distort.Perspective(img, src, dst, 32, 32);

        double mse = SharpImage.Analysis.ImageCompare.MeanSquaredError(img, result);
        await Assert.That(mse).IsLessThan(0.01);
    }

    [Test]
    public async Task PolarRoundTrip_PreservesCenter()
    {
        using var img = CreateSolid(32, 32, 128, 64, 200);
        using var polar = Distort.CartesianToPolar(img);
        using var back = Distort.PolarToCartesian(polar);

        // Center region should be approximately preserved
        byte origCenter = Quantum.ScaleToByte(img.GetPixelRow(16)[16 * 3]);
        byte backCenter = Quantum.ScaleToByte(back.GetPixelRow(16)[16 * 3]);
        await Assert.That(Math.Abs((int)origCenter - (int)backCenter)).IsLessThan(30);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FFT Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task FFT_MagnitudeSpectrum_CenterIsBright()
    {
        using var img = CreateGradient(32, 32);
        var (magnitude, _) = FourierTransform.Forward(img);
        int pw = FourierTransform.NextPowerOf2(32);

        // DC component (center after shift) should be the brightest
        int centerIdx = (pw / 2) * pw + pw / 2;
        double dcMag = magnitude[centerIdx];
        await Assert.That(dcMag).IsGreaterThan(0.5);
    }

    [Test]
    public async Task FFT_SolidImage_OnlyDCComponent()
    {
        using var img = CreateSolid(16, 16, 128, 128, 128);
        var (magnitude, _) = FourierTransform.Forward(img);
        int pw = FourierTransform.NextPowerOf2(16);

        // For solid image, only DC component should have significant magnitude
        int centerIdx = (pw / 2) * pw + pw / 2;
        double dcMag = magnitude[centerIdx];

        // Non-DC components should be much smaller
        double maxNonDC = 0;
        for (int i = 0; i < magnitude.Length; i++)
            if (i != centerIdx && magnitude[i] > maxNonDC) maxNonDC = magnitude[i];

        await Assert.That(dcMag).IsGreaterThan(maxNonDC * 2);
    }

    [Test]
    public async Task FFT_MagnitudeSpectrumImage_HasCorrectDimensions()
    {
        using var img = CreateGradient(24, 24);
        using var spectrum = FourierTransform.MagnitudeSpectrum(img);

        // Should be padded to next power of 2
        await Assert.That((int)spectrum.Columns).IsEqualTo(32);
        await Assert.That((int)spectrum.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task FFT_NextPowerOf2_CorrectValues()
    {
        await Assert.That(FourierTransform.NextPowerOf2(1)).IsEqualTo(1);
        await Assert.That(FourierTransform.NextPowerOf2(5)).IsEqualTo(8);
        await Assert.That(FourierTransform.NextPowerOf2(16)).IsEqualTo(16);
        await Assert.That(FourierTransform.NextPowerOf2(100)).IsEqualTo(128);
        await Assert.That(FourierTransform.NextPowerOf2(256)).IsEqualTo(256);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame CreateSolid(int w, int h, byte r, byte g, byte b)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte(r);
                row[x * 3 + 1] = Quantum.ScaleFromByte(g);
                row[x * 3 + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return img;
    }

    private static ImageFrame CreateGradient(int w, int h)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(x * 255 / Math.Max(w - 1, 1));
                row[x * 3] = Quantum.ScaleFromByte(v);
                row[x * 3 + 1] = Quantum.ScaleFromByte(v);
                row[x * 3 + 2] = Quantum.ScaleFromByte(v);
            }
        }
        return img;
    }

    private static ImageFrame CreateRandom(int w, int h, int seed)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte((byte)rng.Next(256));
                row[x * 3 + 1] = Quantum.ScaleFromByte((byte)rng.Next(256));
                row[x * 3 + 2] = Quantum.ScaleFromByte((byte)rng.Next(256));
            }
        }
        return img;
    }

    private static ImageFrame CreateBlackWithWhiteCenter(int w, int h, int x0, int y0, int x1, int y1)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = y0; y < y1 && y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = x0; x < x1 && x < w; x++)
            {
                row[x * 3] = Quantum.MaxValue;
                row[x * 3 + 1] = Quantum.MaxValue;
                row[x * 3 + 2] = Quantum.MaxValue;
            }
        }
        return img;
    }

    private static ImageFrame CreateBimodal(int w, int h, byte darkVal, byte brightVal)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                byte v = x < w / 2 ? darkVal : brightVal;
                row[x * 3] = Quantum.ScaleFromByte(v);
                row[x * 3 + 1] = Quantum.ScaleFromByte(v);
                row[x * 3 + 2] = Quantum.ScaleFromByte(v);
            }
        }
        return img;
    }
}
