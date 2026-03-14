using SharpImage.Core;
using SharpImage.Enhance;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Enhance;

/// <summary>
/// Tests for histogram matching (color transfer) and histogram visualization.
/// </summary>
public class HistogramMatchingTests
{
    private static readonly string TestAssets = Path.Combine(
        AppContext.BaseDirectory, "TestAssets");

    // ─── Dimension Preservation ───────────────────────────────────

    [Test]
    public async Task HistogramMatch_PreservesDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var reference = CreateGradientImage(32, 32);
        using var result = HistogramMatching.Apply(source, reference);

        await Assert.That(result.Columns).IsEqualTo(64u);
        await Assert.That(result.Rows).IsEqualTo(48u);
    }

    // ─── Identity Matching ────────────────────────────────────────

    [Test]
    public async Task HistogramMatch_SameImage_SimilarOutput()
    {
        using var source = CreateGradientImage(64, 64);
        using var reference = CreateGradientImage(64, 64);
        using var result = HistogramMatching.Apply(source, reference);

        // When source and reference are the same, output should be nearly identical
        double maxError = ComputeMaxError(source, result);
        // Allow small quantization error from bin mapping
        await Assert.That(maxError).IsLessThan(0.02);
    }

    // ─── Dark-to-Bright Transfer ──────────────────────────────────

    [Test]
    public async Task HistogramMatch_DarkToLight_IncreasedBrightness()
    {
        using var dark = CreateSolidImage(32, 32, 5000, 5000, 5000);
        using var bright = CreateSolidImage(32, 32, 60000, 60000, 60000);
        using var result = HistogramMatching.Apply(dark, bright);

        // Result should be much brighter than source
        var midRow = result.GetPixelRow(16);
        ushort resultRed = midRow[16 * result.NumberOfChannels];
        await Assert.That(resultRed).IsGreaterThan((ushort)40000);
    }

    // ─── Preserves Alpha ──────────────────────────────────────────

    [Test]
    public async Task HistogramMatch_PreservesAlpha()
    {
        using var source = CreateImageWithAlpha(32, 32, Quantum.MaxValue / 2);
        using var reference = CreateGradientImage(32, 32);
        using var result = HistogramMatching.Apply(source, reference);

        await Assert.That(result.HasAlpha).IsTrue();

        // Alpha should be unchanged
        var srcRow = source.GetPixelRow(16);
        var dstRow = result.GetPixelRow(16);
        int channels = result.NumberOfChannels;
        ushort srcAlpha = srcRow[16 * channels + channels - 1];
        ushort dstAlpha = dstRow[16 * channels + channels - 1];
        await Assert.That(dstAlpha).IsEqualTo(srcAlpha);
    }

    // ─── Channel Independence ─────────────────────────────────────

    [Test]
    public async Task HistogramMatch_ChannelIndependence()
    {
        // Match a red source to a blue reference
        using var redSource = CreateSolidImage(32, 32, Quantum.MaxValue, 0, 0);
        using var blueRef = CreateSolidImage(32, 32, 0, 0, Quantum.MaxValue);
        using var result = HistogramMatching.Apply(redSource, blueRef);

        var midRow = result.GetPixelRow(16);
        int channels = result.NumberOfChannels;
        ushort r = midRow[16 * channels];
        ushort g = midRow[16 * channels + 1];
        ushort b = midRow[16 * channels + 2];

        // Each channel is mapped independently
        // Blue channel should be dominant after matching to blue reference
        await Assert.That(b).IsGreaterThan(r);
    }

    // ─── Histogram Computation ────────────────────────────────────

    [Test]
    public async Task ComputeHistogram_SolidImage_SingleBin()
    {
        using var solid = CreateSolidImage(32, 32, Quantum.MaxValue, 0, 0);
        int[] redHist = HistogramMatching.ComputeHistogram(solid, 0);
        int[] greenHist = HistogramMatching.ComputeHistogram(solid, 1);

        // Red channel: all pixels at max → all in last bin
        int nonZeroBins = redHist.Count(v => v > 0);
        await Assert.That(nonZeroBins).IsEqualTo(1);

        // Green channel: all pixels at 0 → all in first bin
        await Assert.That(greenHist[0]).IsEqualTo(32 * 32);
    }

    // ─── Histogram Visualization ──────────────────────────────────

    [Test]
    public async Task RenderHistogram_CorrectDimensions()
    {
        using var img = CreateGradientImage(64, 64);
        using var histogram = HistogramMatching.RenderHistogram(img, 512, 256);

        await Assert.That(histogram.Columns).IsEqualTo(512u);
        await Assert.That(histogram.Rows).IsEqualTo(256u);
    }

    [Test]
    public async Task RenderHistogram_HasContent()
    {
        using var img = CreateGradientImage(64, 64);
        using var histogram = HistogramMatching.RenderHistogram(img);

        // Histogram should not be all black
        bool hasNonZero = false;
        for (int y = 0; y < (int)histogram.Rows && !hasNonZero; y++)
        {
            var row = histogram.GetPixelRow(y);
            int channels = histogram.NumberOfChannels;
            for (int x = 0; x < (int)histogram.Columns && !hasNonZero; x++)
                for (int c = 0; c < channels; c++)
                    if (row[x * channels + c] > 0) { hasNonZero = true; break; }
        }
        await Assert.That(hasNonZero).IsTrue();
    }

    // ─── Real Image Test ──────────────────────────────────────────

    [Test]
    public async Task HistogramMatch_RealImages_ProducesValidOutput()
    {
        string wizardPath = Path.Combine(TestAssets, "peppers_rgba.png");
        string rosePath = Path.Combine(TestAssets, "photo_small.png");
        if (!File.Exists(wizardPath) || !File.Exists(rosePath)) return;

        using var wizard = FormatRegistry.Read(wizardPath);
        using var rose = FormatRegistry.Read(rosePath);
        using var result = HistogramMatching.Apply(wizard, rose);

        await Assert.That(result.Columns).IsEqualTo(wizard.Columns);
        await Assert.That(result.Rows).IsEqualTo(wizard.Rows);

        // Result should not be all black
        bool hasContent = false;
        var midRow = result.GetPixelRow((int)(result.Rows / 2));
        int channels = result.NumberOfChannels;
        for (int x = 0; x < (int)result.Columns && !hasContent; x++)
            for (int c = 0; c < 3; c++)
                if (midRow[x * channels + c] > 0) { hasContent = true; break; }
        await Assert.That(hasContent).IsTrue();
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static ImageFrame CreateGradientImage(int width, int height)
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
                row[off] = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));
                row[off + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(height - 1, 1));
                row[off + 2] = (ushort)((x + y) * Quantum.MaxValue / Math.Max(width + height - 2, 1));
            }
        }
        return image;
    }

    private static ImageFrame CreateSolidImage(int width, int height, ushort r, ushort g, ushort b)
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

    private static ImageFrame CreateImageWithAlpha(int width, int height, ushort alpha)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));
                row[off + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(height - 1, 1));
                row[off + 2] = (ushort)((x + y) * Quantum.MaxValue / Math.Max(width + height - 2, 1));
                row[off + 3] = alpha;
            }
        }
        return image;
    }

    private static double ComputeMaxError(ImageFrame a, ImageFrame b)
    {
        int width = (int)a.Columns;
        int height = (int)a.Rows;
        int channels = a.NumberOfChannels;
        double maxError = 0;

        for (int y = 0; y < height; y++)
        {
            var rowA = a.GetPixelRow(y);
            var rowB = b.GetPixelRow(y);
            for (int x = 0; x < width; x++)
                for (int c = 0; c < Math.Min(channels, 3); c++)
                {
                    double diff = Math.Abs(rowA[x * channels + c] - rowB[x * channels + c]) / (double)Quantum.MaxValue;
                    maxError = Math.Max(maxError, diff);
                }
        }
        return maxError;
    }
}
