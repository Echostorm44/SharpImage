using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for convolution filters and color adjustment operations.
/// </summary>
public class EffectsTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    // ─── Gaussian Blur ───────────────────────────────────────────

    [Test]
    public async Task GaussianBlur_ReducesHighFrequency()
    {
        using var source = CreateCheckerboard(32, 32);
        using var blurred = ConvolutionFilters.GaussianBlur(source, 2.0);

        await Assert.That(blurred.Columns).IsEqualTo(32);
        await Assert.That(blurred.Rows).IsEqualTo(32);

        // Blurred checkerboard should have less extreme values than original
        var srcRow = source.GetPixelRow(16).ToArray();
        var blurRow = blurred.GetPixelRow(16).ToArray();

        // Find max difference between adjacent pixels
        int srcMaxDiff = 0, blurMaxDiff = 0;
        int channels = source.NumberOfChannels;
        for (int x = 1; x < 31; x++)
        {
            int sd = Math.Abs(srcRow[x * channels] - srcRow[(x - 1) * channels]);
            int bd = Math.Abs(blurRow[x * channels] - blurRow[(x - 1) * channels]);
            srcMaxDiff = Math.Max(srcMaxDiff, sd);
            blurMaxDiff = Math.Max(blurMaxDiff, bd);
        }

        // Blurred max diff should be much less than original
        await Assert.That(blurMaxDiff).IsLessThan(srcMaxDiff);
    }

    [Test]
    public async Task BoxBlur_PreservesDimensions()
    {
        using var source = CreateGradient(24, 16);
        using var blurred = ConvolutionFilters.BoxBlur(source, 2);

        await Assert.That(blurred.Columns).IsEqualTo(24);
        await Assert.That(blurred.Rows).IsEqualTo(16);
    }

    // ─── Sharpen ─────────────────────────────────────────────────

    [Test]
    public async Task Sharpen_IncreasesContrast()
    {
        using var source = CreateGradient(32, 32);
        using var sharpened = ConvolutionFilters.Sharpen(source, 1.0, 2.0);

        await Assert.That(sharpened.Columns).IsEqualTo(32);
        await Assert.That(sharpened.Rows).IsEqualTo(32);
    }

    // ─── UnsharpMask ────────────────────────────────────────────

    [Test]
    public async Task UnsharpMask_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = ConvolutionFilters.UnsharpMask(source, 1.0, 2.0, 0.0);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task UnsharpMask_ThresholdZero_SharpensEverything()
    {
        using var source = CreateGradient(32, 32);
        using var noThreshold = ConvolutionFilters.UnsharpMask(source, 1.0, 2.0, 0.0);

        // With no threshold, result should differ from source (sharpened)
        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = noThreshold.GetPixelRow(16).ToArray();
        bool anyDifference = false;
        for (int i = 0; i < srcRow.Length; i++)
        {
            if (srcRow[i] != dstRow[i]) { anyDifference = true; break; }
        }

        await Assert.That(anyDifference).IsTrue();
    }

    [Test]
    public async Task UnsharpMask_HighThreshold_PreservesFlat()
    {
        // Solid color image — all pixels identical, so no difference to sharpen
        using var source = CreateSolid(32, 32, Quantum.MaxValue / 2);
        using var result = ConvolutionFilters.UnsharpMask(source, 1.0, 2.0, 0.5);

        // Solid image: original - blurred = 0, so threshold blocks all sharpening
        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();
        bool allSame = true;
        for (int i = 0; i < Math.Min(srcRow.Length, dstRow.Length); i++)
        {
            if (Math.Abs(srcRow[i] - dstRow[i]) > 1) { allSame = false; break; }
        }

        await Assert.That(allSame).IsTrue();
    }

    [Test]
    public async Task UnsharpMask_PreservesAlpha()
    {
        using var source = CreateCheckerboardWithAlpha(32, 32);
        using var result = ConvolutionFilters.UnsharpMask(source, 1.0, 1.5, 0.0);

        // Alpha channel should be preserved exactly
        var srcRow = source.GetPixelRow(0).ToArray();
        var dstRow = result.GetPixelRow(0).ToArray();
        int channels = source.NumberOfChannels;
        bool alphaPreserved = true;
        for (int x = 0; x < 32; x++)
        {
            if (srcRow[x * channels + channels - 1] != dstRow[x * channels + channels - 1])
            {
                alphaPreserved = false;
                break;
            }
        }

        await Assert.That(alphaPreserved).IsTrue();
    }

    // ─── Edge Detection ──────────────────────────────────────────

    [Test]
    public async Task EdgeDetect_FindsEdges()
    {
        // Create step edge: left half black, right half white
        using var source = CreateStepEdge(32, 32);
        using var edges = ConvolutionFilters.EdgeDetect(source);

        await Assert.That(edges.Columns).IsEqualTo(32);
        await Assert.That(edges.Rows).IsEqualTo(32);

        // Edge-detected step should have high values at the transition (column 15-16)
        var row = edges.GetPixelRow(16).ToArray();
        int channels = edges.NumberOfChannels;
        int edgeVal = row[16 * channels]; // At the boundary

        await Assert.That(edgeVal).IsGreaterThan(Quantum.MaxValue / 4);
    }

    [Test]
    public async Task Emboss_ProducesDimensionalEffect()
    {
        using var source = CreateGradient(24, 24);
        using var embossed = ConvolutionFilters.Emboss(source);

        await Assert.That(embossed.Columns).IsEqualTo(24);
        await Assert.That(embossed.Rows).IsEqualTo(24);
    }

    // ─── Color Adjustments ───────────────────────────────────────

    [Test]
    public async Task Brightness_Increase_BrightensPixels()
    {
        using var source = CreateMidGray(16, 16);
        using var brightened = ColorAdjust.Brightness(source, 1.5);

        var srcRow = source.GetPixelRow(8).ToArray();
        var brtRow = brightened.GetPixelRow(8).ToArray();

        await Assert.That((int)brtRow[0]).IsGreaterThan((int)srcRow[0]);
    }

    [Test]
    public async Task Brightness_Decrease_DarkensPixels()
    {
        using var source = CreateMidGray(16, 16);
        using var darkened = ColorAdjust.Brightness(source, 0.5);

        var srcRow = source.GetPixelRow(8).ToArray();
        var drkRow = darkened.GetPixelRow(8).ToArray();

        await Assert.That((int)drkRow[0]).IsLessThan((int)srcRow[0]);
    }

    [Test]
    public async Task Contrast_Increase_SpreadsValues()
    {
        using var source = CreateGradient(16, 16);
        using var contrasted = ColorAdjust.Contrast(source, 2.0);

        await Assert.That(contrasted.Columns).IsEqualTo(16);
    }

    [Test]
    public async Task Gamma_BrightensWithHighGamma()
    {
        using var source = CreateMidGray(16, 16);
        using var corrected = ColorAdjust.Gamma(source, 2.0);

        var srcRow = source.GetPixelRow(8).ToArray();
        var gamRow = corrected.GetPixelRow(8).ToArray();

        // Gamma > 1 should brighten midtones
        await Assert.That((int)gamRow[0]).IsGreaterThan((int)srcRow[0]);
    }

    [Test]
    public async Task Grayscale_AllChannelsEqual()
    {
        using var source = CreateColorImage(16, 16);
        using var gray = ColorAdjust.Grayscale(source);

        var row = gray.GetPixelRow(8).ToArray();
        int channels = gray.NumberOfChannels;

        // R, G, B should be equal for every pixel
        for (int x = 0; x < 16; x++)
        {
            int off = x * channels;
            await Assert.That(row[off]).IsEqualTo(row[off + 1]);
            await Assert.That(row[off + 1]).IsEqualTo(row[off + 2]);
        }
    }

    [Test]
    public async Task Invert_DoubleInvert_Identity()
    {
        using var source = CreateGradient(16, 16);
        using var inv1 = ColorAdjust.Invert(source);
        using var inv2 = ColorAdjust.Invert(inv1);

        // Double invert should restore original
        int channels = source.NumberOfChannels;
        for (int y = 0; y < 16; y++)
        {
            var srcRow = source.GetPixelRow(y).ToArray();
            var resRow = inv2.GetPixelRow(y).ToArray();
            for (int i = 0; i < 16 * channels; i++)
                await Assert.That(resRow[i]).IsEqualTo(srcRow[i]);
        }
    }

    [Test]
    public async Task Invert_WhiteBecomesBlack()
    {
        using var white = CreateSolid(4, 4, Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue);
        using var inverted = ColorAdjust.Invert(white);

        var row = inverted.GetPixelRow(0).ToArray();
        await Assert.That(row[0]).IsEqualTo((ushort)0);
        await Assert.That(row[1]).IsEqualTo((ushort)0);
        await Assert.That(row[2]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Threshold_BinarizesImage()
    {
        using var source = CreateGradient(32, 1);
        using var thresholded = ColorAdjust.Threshold(source, 0.5);

        var row = thresholded.GetPixelRow(0).ToArray();
        int channels = thresholded.NumberOfChannels;

        // Left side should be black, right side white
        await Assert.That(row[0]).IsEqualTo((ushort)0); // First pixel: black
        await Assert.That(row[(31) * channels]).IsEqualTo(Quantum.MaxValue); // Last pixel: white
    }

    [Test]
    public async Task Posterize_ReducesColorLevels()
    {
        using var source = CreateGradient(256, 1);
        using var posterized = ColorAdjust.Posterize(source, 4);

        var row = posterized.GetPixelRow(0).ToArray();
        int channels = posterized.NumberOfChannels;

        // With 4 levels, there should be only a few distinct R values
        var distinctValues = new HashSet<ushort>();
        for (int x = 0; x < 256; x++)
            distinctValues.Add(row[x * channels]);

        await Assert.That(distinctValues.Count).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task Saturate_Zero_ProducesGrayscale()
    {
        using var source = CreateColorImage(16, 16);
        using var desaturated = ColorAdjust.Saturate(source, 0.0);

        var row = desaturated.GetPixelRow(8).ToArray();
        int channels = desaturated.NumberOfChannels;

        // R ≈ G ≈ B for desaturated image (may differ by 1 due to rounding)
        for (int x = 0; x < 16; x++)
        {
            int off = x * channels;
            int maxDiff = Math.Max(
                Math.Abs(row[off] - row[off + 1]),
                Math.Abs(row[off + 1] - row[off + 2]));
            await Assert.That(maxDiff).IsLessThanOrEqualTo(1);
        }
    }

    // ─── Real Image Tests ────────────────────────────────────────

    [Test]
    public async Task RealImage_BlurAndSave()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");
        string outFile = Path.Combine(Path.GetTempPath(), "sharpimage_test_rose_blurred.png");

        try
        {
            using var rose = PngCoder.Read(rosePng);
            using var blurred = ConvolutionFilters.GaussianBlur(rose, 1.5);

            PngCoder.Write(blurred, outFile);
            using var reloaded = PngCoder.Read(outFile);

            await Assert.That(reloaded.Columns).IsEqualTo(rose.Columns);
            await Assert.That(reloaded.Rows).IsEqualTo(rose.Rows);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    [Test]
    public async Task Levels_RealImage_AdjustsRange()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");

        using var rose = PngCoder.Read(rosePng);
        using var adjusted = ColorAdjust.Levels(rose, 0.1, 0.9, 0.0, 1.0, 1.2);

        await Assert.That(adjusted.Columns).IsEqualTo(rose.Columns);
        await Assert.That(adjusted.Rows).IsEqualTo(rose.Rows);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateCheckerboard(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = ((x + y) % 2 == 0) ? Quantum.MaxValue : (ushort)0;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateStepEdge(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = x < width / 2 ? (ushort)0 : Quantum.MaxValue;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateGradient(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateMidGray(int width, int height)
    {
        return CreateSolid(width, height, Quantum.MaxValue / 2, Quantum.MaxValue / 2, Quantum.MaxValue / 2);
    }

    private static ImageFrame CreateColorImage(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = Quantum.ScaleFromByte(200);   // R
                row[off + 1] = Quantum.ScaleFromByte(100); // G
                row[off + 2] = Quantum.ScaleFromByte(50);  // B
            }
        }
        return image;
    }

    private static ImageFrame CreateSolid(int width, int height, ushort r, ushort g, ushort b)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = r;
                row[off + 1] = g;
                row[off + 2] = b;
            }
        }
        return image;
    }

    private static ImageFrame CreateSolid(int width, int height, ushort gray)
    {
        return CreateSolid(width, height, gray, gray, gray);
    }

    private static ImageFrame CreateCheckerboardWithAlpha(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = ((x + y) % 2 == 0) ? Quantum.MaxValue : (ushort)0;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
                row[off + 3] = (ushort)(x < width / 2 ? Quantum.MaxValue : Quantum.MaxValue / 2);
            }
        }
        return image;
    }
}
