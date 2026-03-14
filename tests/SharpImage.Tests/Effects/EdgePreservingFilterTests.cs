// Tests for edge-preserving filter operations.
// Covers: Median filter, Bilateral blur, Kuwahara filter,
// Adaptive blur/sharpen, Local contrast enhancement.

using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Enhance;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for edge-preserving filters: Median, Bilateral, Kuwahara,
/// Adaptive Blur/Sharpen, and Local Contrast Enhancement.
/// </summary>
public class EdgePreservingFilterTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

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

    private static ImageFrame CreateSaltAndPepperImage(int width, int height, double noiseFraction = 0.1)
    {
        var frame = CreateTestImage(width, height);
        var rng = new Random(42);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                if (rng.NextDouble() < noiseFraction)
                {
                    ushort val = rng.NextDouble() < 0.5 ? (ushort)0 : Quantum.MaxValue;
                    int off = x * 3;
                    row[off] = val;
                    row[off + 1] = val;
                    row[off + 2] = val;
                }
            }
        }
        return frame;
    }

    private static ImageFrame CreateImageWithAlpha(int width, int height)
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

    // ══════════════════════════════════════════════════════════════
    //  Median Filter
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task MedianFilter_PreservesDimensions()
    {
        var source = CreateTestImage(100, 80);
        var result = BlurNoiseOps.MedianFilter(source, 1);

        await Assert.That(result.Columns).IsEqualTo(100);
        await Assert.That(result.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task MedianFilter_RemovesSaltAndPepper()
    {
        var noisy = CreateSaltAndPepperImage(100, 100, 0.15);
        var clean = CreateTestImage(100, 100);
        var filtered = BlurNoiseOps.MedianFilter(noisy, 1);

        // Filtered should be closer to clean than noisy
        double noisyDiff = ComputeMeanAbsDiff(noisy, clean);
        double filteredDiff = ComputeMeanAbsDiff(filtered, clean);

        await Assert.That(filteredDiff).IsLessThan(noisyDiff);
    }

    [Test]
    public async Task MedianFilter_LargerRadius_StrongerSmoothing()
    {
        var source = CreateSaltAndPepperImage(100, 100, 0.1);
        var r1 = BlurNoiseOps.MedianFilter(source, 1);
        var r2 = BlurNoiseOps.MedianFilter(source, 2);

        double diff1 = ComputeMeanAbsDiff(r1, source);
        double diff2 = ComputeMeanAbsDiff(r2, source);

        // Larger radius = more change from original
        await Assert.That(diff2).IsGreaterThan(diff1);
    }

    [Test]
    public async Task MedianFilter_PreservesAlpha()
    {
        var source = CreateImageWithAlpha(50, 50);
        var result = BlurNoiseOps.MedianFilter(source, 1);

        await Assert.That(result.HasAlpha).IsTrue();
        await Assert.That(result.NumberOfChannels).IsEqualTo(4);
    }

    [Test]
    public async Task MedianFilter_Grayscale_Works()
    {
        var source = CreateGrayscaleImage(50, 50);
        var result = BlurNoiseOps.MedianFilter(source, 1);

        await Assert.That(result.Columns).IsEqualTo(50);
        await Assert.That(result.NumberOfChannels).IsEqualTo(1);
    }

    [Test]
    public async Task MedianFilter_1x1Image_NoError()
    {
        var source = CreateTestImage(1, 1);
        var result = BlurNoiseOps.MedianFilter(source, 1);

        await Assert.That(result.Columns).IsEqualTo(1);
        await Assert.That(result.Rows).IsEqualTo(1);
    }

    // ══════════════════════════════════════════════════════════════
    //  Bilateral Blur
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task BilateralBlur_PreservesDimensions()
    {
        var source = CreateTestImage(100, 80);
        var result = BlurNoiseOps.BilateralBlur(source, 3, 1.5, 50.0);

        await Assert.That(result.Columns).IsEqualTo(100);
        await Assert.That(result.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task BilateralBlur_SmoothsFlatRegions()
    {
        // Create uniform gray image with gentle noise (not salt-and-pepper)
        var source = new ImageFrame();
        source.Initialize(80, 80, ColorspaceType.SRGB, false);
        var rng = new Random(42);
        for (int y = 0; y < 80; y++)
        {
            var row = source.GetPixelRowForWrite(y);
            for (int x = 0; x < 80; x++)
            {
                // Uniform gray ~32768 with ±5000 noise
                int off = x * 3;
                for (int c = 0; c < 3; c++)
                    row[off + c] = (ushort)Math.Clamp(32768 + rng.Next(-5000, 5000), 0, 65535);
            }
        }

        var result = BlurNoiseOps.BilateralBlur(source, 3, 2.0, 50.0);

        // Smoothed image should have lower variance (more uniform)
        double srcVariance = ComputeVariance(source);
        double dstVariance = ComputeVariance(result);

        await Assert.That(dstVariance).IsLessThan(srcVariance);
    }

    [Test]
    public async Task BilateralBlur_PreservesEdges_BetterThanGaussian()
    {
        // Create image with sharp edge (left half black, right half white)
        var source = CreateEdgeImage(100, 100);
        var bilateral = BlurNoiseOps.BilateralBlur(source, 3, 2.0, 20.0);
        var gaussian = ConvolutionFilters.GaussianBlur(source, 2.0);

        // Bilateral should preserve the edge better — measure edge sharpness at center column
        double bilateralEdgeSharpness = MeasureEdgeSharpness(bilateral, 50);
        double gaussianEdgeSharpness = MeasureEdgeSharpness(gaussian, 50);

        await Assert.That(bilateralEdgeSharpness).IsGreaterThan(gaussianEdgeSharpness);
    }

    [Test]
    public async Task BilateralBlur_HighRangeSigma_ActsLikeGaussian()
    {
        var source = CreateTestImage(50, 50);
        // Very high range sigma means all colors treated equally → behaves like spatial blur
        var result = BlurNoiseOps.BilateralBlur(source, 2, 1.5, 50000.0);

        // Should still produce valid output
        await Assert.That(result.Columns).IsEqualTo(50);
    }

    [Test]
    public async Task BilateralBlur_PreservesAlpha()
    {
        var source = CreateImageWithAlpha(50, 50);
        var result = BlurNoiseOps.BilateralBlur(source, 2);

        await Assert.That(result.HasAlpha).IsTrue();
        // Check alpha is preserved at a known pixel
        ushort srcAlpha = source.GetPixelRow(25)[25 * 4 + 3];
        ushort dstAlpha = result.GetPixelRow(25)[25 * 4 + 3];
        await Assert.That(dstAlpha).IsEqualTo(srcAlpha);
    }

    // ══════════════════════════════════════════════════════════════
    //  Kuwahara Filter
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task KuwaharaFilter_PreservesDimensions()
    {
        var source = CreateTestImage(100, 80);
        var result = BlurNoiseOps.KuwaharaFilter(source, 2);

        await Assert.That(result.Columns).IsEqualTo(100);
        await Assert.That(result.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task KuwaharaFilter_CreatesPainteryEffect()
    {
        var source = CreateTestImage(100, 100);
        var result = BlurNoiseOps.KuwaharaFilter(source, 3);

        // The result should differ from source (smoothed)
        double diff = ComputeMeanAbsDiff(result, source);
        await Assert.That(diff).IsGreaterThan(0);
    }

    [Test]
    public async Task KuwaharaFilter_LargerRadius_MoreSmoothing()
    {
        var source = CreateTestImage(100, 100);
        var r2 = BlurNoiseOps.KuwaharaFilter(source, 2);
        var r4 = BlurNoiseOps.KuwaharaFilter(source, 4);

        double diff2 = ComputeMeanAbsDiff(r2, source);
        double diff4 = ComputeMeanAbsDiff(r4, source);

        await Assert.That(diff4).IsGreaterThan(diff2);
    }

    [Test]
    public async Task KuwaharaFilter_PreservesAlpha()
    {
        var source = CreateImageWithAlpha(50, 50);
        var result = BlurNoiseOps.KuwaharaFilter(source, 2);

        await Assert.That(result.HasAlpha).IsTrue();
        ushort srcAlpha = source.GetPixelRow(25)[25 * 4 + 3];
        ushort dstAlpha = result.GetPixelRow(25)[25 * 4 + 3];
        await Assert.That(dstAlpha).IsEqualTo(srcAlpha);
    }

    [Test]
    public async Task KuwaharaFilter_Grayscale_Works()
    {
        var source = CreateGrayscaleImage(50, 50);
        var result = BlurNoiseOps.KuwaharaFilter(source, 2);

        await Assert.That(result.Columns).IsEqualTo(50);
        await Assert.That(result.NumberOfChannels).IsEqualTo(1);
    }

    [Test]
    public async Task KuwaharaFilter_1x1Image_NoError()
    {
        var source = CreateTestImage(1, 1);
        var result = BlurNoiseOps.KuwaharaFilter(source, 1);

        await Assert.That(result.Columns).IsEqualTo(1);
    }

    // ══════════════════════════════════════════════════════════════
    //  Adaptive Blur
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AdaptiveBlur_PreservesDimensions()
    {
        var source = CreateTestImage(100, 80);
        var result = ConvolutionFilters.AdaptiveBlur(source, 2.0);

        await Assert.That(result.Columns).IsEqualTo(100);
        await Assert.That(result.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task AdaptiveBlur_ModifiesImage()
    {
        var source = CreateTestImage(80, 80);
        var result = ConvolutionFilters.AdaptiveBlur(source, 2.0);

        double diff = ComputeMeanAbsDiff(result, source);
        await Assert.That(diff).IsGreaterThan(0);
    }

    [Test]
    public async Task AdaptiveBlur_LargerSigma_MoreBlur()
    {
        var source = CreateTestImage(80, 80);
        var r1 = ConvolutionFilters.AdaptiveBlur(source, 1.0);
        var r3 = ConvolutionFilters.AdaptiveBlur(source, 3.0);

        double diff1 = ComputeMeanAbsDiff(r1, source);
        double diff3 = ComputeMeanAbsDiff(r3, source);

        await Assert.That(diff3).IsGreaterThan(diff1);
    }

    [Test]
    public async Task AdaptiveBlur_PreservesAlpha()
    {
        var source = CreateImageWithAlpha(50, 50);
        var result = ConvolutionFilters.AdaptiveBlur(source, 2.0);

        await Assert.That(result.HasAlpha).IsTrue();
        ushort srcAlpha = source.GetPixelRow(25)[25 * 4 + 3];
        ushort dstAlpha = result.GetPixelRow(25)[25 * 4 + 3];
        await Assert.That(dstAlpha).IsEqualTo(srcAlpha);
    }

    // ══════════════════════════════════════════════════════════════
    //  Adaptive Sharpen
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AdaptiveSharpen_PreservesDimensions()
    {
        var source = CreateTestImage(100, 80);
        var result = ConvolutionFilters.AdaptiveSharpen(source, 1.0, 2.0);

        await Assert.That(result.Columns).IsEqualTo(100);
        await Assert.That(result.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task AdaptiveSharpen_EnhancesEdges()
    {
        var source = CreateEdgeImage(100, 100);
        var result = ConvolutionFilters.AdaptiveSharpen(source, 1.0, 2.0);

        // Sharpened image should have stronger edge contrast
        double srcEdge = MeasureEdgeSharpness(source, 50);
        double dstEdge = MeasureEdgeSharpness(result, 50);

        await Assert.That(dstEdge).IsGreaterThanOrEqualTo(srcEdge);
    }

    [Test]
    public async Task AdaptiveSharpen_PreservesAlpha()
    {
        var source = CreateImageWithAlpha(50, 50);
        var result = ConvolutionFilters.AdaptiveSharpen(source, 1.0, 2.0);

        await Assert.That(result.HasAlpha).IsTrue();
        ushort srcAlpha = source.GetPixelRow(25)[25 * 4 + 3];
        ushort dstAlpha = result.GetPixelRow(25)[25 * 4 + 3];
        await Assert.That(dstAlpha).IsEqualTo(srcAlpha);
    }

    [Test]
    public async Task AdaptiveSharpen_HigherAmount_StrongerEffect()
    {
        var source = CreateTestImage(80, 80);
        var r1 = ConvolutionFilters.AdaptiveSharpen(source, 1.0, 1.0);
        var r3 = ConvolutionFilters.AdaptiveSharpen(source, 1.0, 3.0);

        double diff1 = ComputeMeanAbsDiff(r1, source);
        double diff3 = ComputeMeanAbsDiff(r3, source);

        await Assert.That(diff3).IsGreaterThan(diff1);
    }

    // ══════════════════════════════════════════════════════════════
    //  Local Contrast
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task LocalContrast_PreservesDimensions()
    {
        var source = CreateTestImage(100, 80);
        var result = EnhanceOps.LocalContrast(source, 10, 50.0);

        await Assert.That(result.Columns).IsEqualTo(100);
        await Assert.That(result.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task LocalContrast_EnhancesDetail()
    {
        var source = CreateTestImage(100, 100);
        var result = EnhanceOps.LocalContrast(source, 10, 50.0);

        double diff = ComputeMeanAbsDiff(result, source);
        await Assert.That(diff).IsGreaterThan(0);
    }

    [Test]
    public async Task LocalContrast_HigherStrength_MoreChange()
    {
        var source = CreateTestImage(100, 100);
        var r25 = EnhanceOps.LocalContrast(source, 10, 25.0);
        var r75 = EnhanceOps.LocalContrast(source, 10, 75.0);

        double diff25 = ComputeMeanAbsDiff(r25, source);
        double diff75 = ComputeMeanAbsDiff(r75, source);

        await Assert.That(diff75).IsGreaterThan(diff25);
    }

    [Test]
    public async Task LocalContrast_ZeroStrength_NoChange()
    {
        var source = CreateTestImage(50, 50);
        var result = EnhanceOps.LocalContrast(source, 10, 0.0);

        double diff = ComputeMeanAbsDiff(result, source);
        await Assert.That(diff).IsLessThan(1.0); // essentially unchanged
    }

    [Test]
    public async Task LocalContrast_PreservesAlpha()
    {
        var source = CreateImageWithAlpha(50, 50);
        var result = EnhanceOps.LocalContrast(source, 5, 50.0);

        await Assert.That(result.HasAlpha).IsTrue();
        ushort srcAlpha = source.GetPixelRow(25)[25 * 4 + 3];
        ushort dstAlpha = result.GetPixelRow(25)[25 * 4 + 3];
        await Assert.That(dstAlpha).IsEqualTo(srcAlpha);
    }

    [Test]
    public async Task LocalContrast_Grayscale_Works()
    {
        var source = CreateGrayscaleImage(50, 50);
        var result = EnhanceOps.LocalContrast(source, 5, 50.0);

        await Assert.That(result.Columns).IsEqualTo(50);
        await Assert.That(result.NumberOfChannels).IsEqualTo(1);
    }

    // ══════════════════════════════════════════════════════════════
    //  Real image tests
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task MedianFilter_RealImage_ProducesValidOutput()
    {
        string path = Path.Combine(TestImagesDir, "photo_small.png");
        if (!File.Exists(path)) return;

        var image = FormatRegistry.Read(path);
        var result = BlurNoiseOps.MedianFilter(image, 1);

        await Assert.That(result.Columns).IsEqualTo(image.Columns);
        await Assert.That(result.Rows).IsEqualTo(image.Rows);
    }

    [Test]
    public async Task BilateralBlur_RealImage_ProducesValidOutput()
    {
        string path = Path.Combine(TestImagesDir, "photo_small.png");
        if (!File.Exists(path)) return;

        var image = FormatRegistry.Read(path);
        var result = BlurNoiseOps.BilateralBlur(image, 3, 2.0, 30.0);

        await Assert.That(result.Columns).IsEqualTo(image.Columns);
        await Assert.That(result.Rows).IsEqualTo(image.Rows);
    }

    [Test]
    public async Task KuwaharaFilter_RealImage_ProducesValidOutput()
    {
        string path = Path.Combine(TestImagesDir, "photo_small.png");
        if (!File.Exists(path)) return;

        var image = FormatRegistry.Read(path);
        var result = BlurNoiseOps.KuwaharaFilter(image, 3);

        await Assert.That(result.Columns).IsEqualTo(image.Columns);
        await Assert.That(result.Rows).IsEqualTo(image.Rows);
    }

    [Test]
    public async Task LocalContrast_RealImage_ProducesValidOutput()
    {
        string path = Path.Combine(TestImagesDir, "photo_small.png");
        if (!File.Exists(path)) return;

        var image = FormatRegistry.Read(path);
        var result = EnhanceOps.LocalContrast(image, 10, 50.0);

        await Assert.That(result.Columns).IsEqualTo(image.Columns);
    }

    // ══════════════════════════════════════════════════════════════
    //  Test Utilities
    // ══════════════════════════════════════════════════════════════

    private static ImageFrame CreateEdgeImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = x < width / 2 ? (ushort)0 : Quantum.MaxValue;
                int off = x * 3;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return frame;
    }

    private static double ComputeMeanAbsDiff(ImageFrame a, ImageFrame b)
    {
        int width = (int)Math.Min(a.Columns, b.Columns);
        int height = (int)Math.Min(a.Rows, b.Rows);
        int channels = Math.Min(a.NumberOfChannels, b.NumberOfChannels);
        long totalDiff = 0;
        long count = 0;

        for (int y = 0; y < height; y++)
        {
            var rowA = a.GetPixelRow(y);
            var rowB = b.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    totalDiff += Math.Abs(rowA[x * a.NumberOfChannels + c] - rowB[x * b.NumberOfChannels + c]);
                    count++;
                }
            }
        }

        return count > 0 ? (double)totalDiff / count : 0;
    }

    private static double MeasureEdgeSharpness(ImageFrame image, int edgeX)
    {
        // Measure max gradient across the edge column
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        double maxGradient = 0;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            if (edgeX > 0 && edgeX < image.Columns - 1)
            {
                double left = row[(edgeX - 1) * channels];
                double right = row[(edgeX + 1) * channels];
                double gradient = Math.Abs(right - left);
                if (gradient > maxGradient) maxGradient = gradient;
            }
        }

        return maxGradient;
    }

    private static double ComputeVariance(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        double sum = 0, sumSq = 0;
        long count = 0;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double val = row[x * channels]; // just red/first channel
                sum += val;
                sumSq += val * val;
                count++;
            }
        }

        double mean = sum / count;
        return (sumSq / count) - (mean * mean);
    }
}
