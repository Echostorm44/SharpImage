using SharpImage.Core;
using SharpImage.Enhance;
using SharpImage.Image;
using TUnit.Core;

namespace SharpImage.Tests;

/// <summary>
/// Tests for Group 1: Enhancement operations (Equalize, Normalize, AutoLevel, AutoGamma,
/// SigmoidalContrast, CLAHE, Modulate, WhiteBalance, Colorize, Solarize, SepiaTone).
/// </summary>
public class EnhanceOpsTests
{
    // ═══════════════════════════════════════════════════════════════
    // Equalize Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Equalize_LowContrastImage_ExpandsRange()
    {
        // Image with values clustered in 40%-60% range
        using var img = CreateUniformGradient(64, 64, 0.4, 0.6);
        using var equalized = EnhanceOps.Equalize(img);

        // After equalization, should have values near 0 and near max
        var (min, max) = GetMinMaxLuminance(equalized);
        await Assert.That(min).IsLessThan(0.15);
        await Assert.That(max).IsGreaterThan(0.85);
    }

    [Test]
    public async Task Equalize_PreservesAlpha()
    {
        using var img = CreateSolidWithAlpha(16, 16, 0.5, 0.5, 0.5, 0.7);
        using var equalized = EnhanceOps.Equalize(img);

        int channels = equalized.NumberOfChannels;
        var row = equalized.GetPixelRow(8);
        double alpha = row[8 * channels + 3] * Quantum.Scale;
        await Assert.That(alpha).IsGreaterThanOrEqualTo(0.69).And.IsLessThanOrEqualTo(0.71);
    }

    [Test]
    public async Task Equalize_PreservesDimensions()
    {
        using var img = CreateUniformGradient(100, 50, 0.2, 0.8);
        using var equalized = EnhanceOps.Equalize(img);
        await Assert.That((int)equalized.Columns).IsEqualTo(100);
        await Assert.That((int)equalized.Rows).IsEqualTo(50);
    }

    // ═══════════════════════════════════════════════════════════════
    // Normalize Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Normalize_StretchesToFullRange()
    {
        // Image with values only in 30%-70% range
        using var img = CreateUniformGradient(64, 64, 0.3, 0.7);
        using var normalized = EnhanceOps.Normalize(img);

        var (min, max) = GetMinMaxLuminance(normalized);
        await Assert.That(min).IsLessThan(0.02);
        await Assert.That(max).IsGreaterThan(0.98);
    }

    [Test]
    public async Task Normalize_AlreadyFullRange_NoChange()
    {
        using var img = CreateUniformGradient(64, 64, 0.0, 1.0);
        using var normalized = EnhanceOps.Normalize(img);

        // Should be essentially unchanged
        var (min, max) = GetMinMaxLuminance(normalized);
        await Assert.That(min).IsLessThan(0.02);
        await Assert.That(max).IsGreaterThan(0.98);
    }

    // ═══════════════════════════════════════════════════════════════
    // AutoLevel Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task AutoLevel_PerChannelStretch()
    {
        // Create image with different ranges per channel
        using var img = CreateColorBiasedImage(64, 64);
        using var leveled = EnhanceOps.AutoLevel(img);

        // Each channel should now span full range
        var (rMin, rMax, gMin, gMax, bMin, bMax) = GetPerChannelMinMax(leveled);
        await Assert.That(rMax - rMin).IsGreaterThan(0.9);
        await Assert.That(gMax - gMin).IsGreaterThan(0.9);
    }

    // ═══════════════════════════════════════════════════════════════
    // AutoGamma Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task AutoGamma_DarkImage_Brightens()
    {
        // Dark image (mean ~0.2)
        using var img = CreateSolid(64, 64, 0.2, 0.2, 0.2);
        using var corrected = EnhanceOps.AutoGamma(img);

        double meanAfter = GetMeanLuminance(corrected);
        // Should brighten toward 0.5
        await Assert.That(meanAfter).IsGreaterThan(0.35);
    }

    [Test]
    public async Task AutoGamma_BrightImage_Darkens()
    {
        // Bright image (mean ~0.8)
        using var img = CreateSolid(64, 64, 0.8, 0.8, 0.8);
        using var corrected = EnhanceOps.AutoGamma(img);

        double meanAfter = GetMeanLuminance(corrected);
        // Should darken toward 0.5
        await Assert.That(meanAfter).IsLessThan(0.7);
    }

    // ═══════════════════════════════════════════════════════════════
    // SigmoidalContrast Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task SigmoidalContrast_Sharpen_IncreasesContrast()
    {
        using var img = CreateUniformGradient(64, 64, 0.0, 1.0);
        using var result = EnhanceOps.SigmoidalContrast(img, contrast: 10.0, sharpen: true);

        // Midtone region should have steeper transitions
        // Dark pixels should get darker, light pixels should get lighter
        var row = result.GetPixelRow(0); // darkest row in gradient
        double darkVal = row[0] * Quantum.Scale;
        var bottomRow = result.GetPixelRow((int)result.Rows - 1);
        double lightVal = bottomRow[0] * Quantum.Scale;

        await Assert.That(darkVal).IsLessThan(0.1);
        await Assert.That(lightVal).IsGreaterThan(0.9);
    }

    [Test]
    public async Task SigmoidalContrast_Unsharpen_DecreasesContrast()
    {
        // Use a gradient that doesn't include exact 0 and 1 endpoints
        using var img = CreateUniformGradient(64, 64, 0.1, 0.9);
        using var result = EnhanceOps.SigmoidalContrast(img, contrast: 10.0, sharpen: false);

        // Midtone values should move toward the midpoint
        // The darkest row (0.1) should become brighter, the lightest (0.9) should become darker
        var darkVals = result.GetPixelRow(0).ToArray();
        var lightVals = result.GetPixelRow((int)result.Rows - 1).ToArray();
        double darkVal = darkVals[0] * Quantum.Scale;
        double lightVal = lightVals[0] * Quantum.Scale;

        await Assert.That(darkVal).IsGreaterThan(0.15); // should have increased from ~0.1
        await Assert.That(lightVal).IsLessThan(0.85); // should have decreased from ~0.9
    }

    // ═══════════════════════════════════════════════════════════════
    // CLAHE Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task CLAHE_LowContrastImage_ImprovesLocalContrast()
    {
        using var img = CreateUniformGradient(128, 128, 0.3, 0.7);
        using var result = EnhanceOps.CLAHE(img, tilesX: 4, tilesY: 4, clipLimit: 2.0);

        // Should expand the range beyond the original
        var (min, max) = GetMinMaxLuminance(result);
        double range = max - min;
        await Assert.That(range).IsGreaterThan(0.45);
    }

    [Test]
    public async Task CLAHE_PreservesDimensions()
    {
        using var img = CreateSolid(100, 80, 0.5, 0.5, 0.5);
        using var result = EnhanceOps.CLAHE(img);
        await Assert.That((int)result.Columns).IsEqualTo(100);
        await Assert.That((int)result.Rows).IsEqualTo(80);
    }

    // ═══════════════════════════════════════════════════════════════
    // Modulate Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Modulate_DoubleBrightness_BrightensImage()
    {
        using var img = CreateSolid(32, 32, 0.4, 0.3, 0.2);
        using var result = EnhanceOps.Modulate(img, brightness: 200);

        double meanAfter = GetMeanLuminance(result);
        double meanBefore = GetMeanLuminance(img);
        await Assert.That(meanAfter).IsGreaterThan(meanBefore * 1.5);
    }

    [Test]
    public async Task Modulate_ZeroSaturation_ProducesGrayscale()
    {
        using var img = CreateSolid(32, 32, 0.8, 0.2, 0.4);
        using var result = EnhanceOps.Modulate(img, saturation: 0);

        // R, G, B should be approximately equal (grayscale)
        var row = result.GetPixelRow(16);
        int ch = result.NumberOfChannels;
        double r = row[16 * ch] * Quantum.Scale;
        double g = row[16 * ch + 1] * Quantum.Scale;
        double b = row[16 * ch + 2] * Quantum.Scale;
        await Assert.That(Math.Abs(r - g)).IsLessThan(0.02);
        await Assert.That(Math.Abs(g - b)).IsLessThan(0.02);
    }

    [Test]
    public async Task Modulate_HueShift180_SwapsColors()
    {
        using var img = CreateSolid(32, 32, 1.0, 0.0, 0.0); // pure red
        // 180 degree hue shift = hue param of 200 (100 + 100*1.8=180 degrees)
        using var result = EnhanceOps.Modulate(img, hue: 200);

        var row = result.GetPixelRow(16);
        int ch = result.NumberOfChannels;
        double r = row[16 * ch] * Quantum.Scale;
        double b = row[16 * ch + 2] * Quantum.Scale;
        // Red should become cyan-ish (low red, high blue/green)
        await Assert.That(r).IsLessThan(0.3);
    }

    [Test]
    public async Task Modulate_NoChange_Identity()
    {
        using var img = CreateSolid(32, 32, 0.5, 0.3, 0.7);
        using var result = EnhanceOps.Modulate(img, brightness: 100, saturation: 100, hue: 100);

        // Should be very close to original
        int ch = img.NumberOfChannels;
        var srcVals = img.GetPixelRow(16).ToArray();
        var dstVals = result.GetPixelRow(16).ToArray();
        for (int c = 0; c < 3; c++)
        {
            double diff = Math.Abs(srcVals[16 * ch + c] - dstVals[16 * ch + c]) * Quantum.Scale;
            await Assert.That(diff).IsLessThan(0.01);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WhiteBalance Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task WhiteBalance_WarmTint_ReducesRedBias()
    {
        // Image with warm (red) cast
        using var img = CreateSolid(64, 64, 0.7, 0.4, 0.3);
        using var balanced = EnhanceOps.WhiteBalance(img);

        var row = balanced.GetPixelRow(32);
        int ch = balanced.NumberOfChannels;
        double r = row[32 * ch] * Quantum.Scale;
        double g = row[32 * ch + 1] * Quantum.Scale;
        double b = row[32 * ch + 2] * Quantum.Scale;

        // Channels should be closer together after white balance
        double maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
        await Assert.That(maxDiff).IsLessThan(0.15);
    }

    // ═══════════════════════════════════════════════════════════════
    // Colorize Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Colorize_FullAmount_TintsToColor()
    {
        using var img = CreateSolid(32, 32, 0.5, 0.5, 0.5);
        using var tinted = EnhanceOps.Colorize(img, 255, 0, 0, amount: 1.0); // full red tint

        var row = tinted.GetPixelRow(16);
        int ch = tinted.NumberOfChannels;
        double r = row[16 * ch] * Quantum.Scale;
        double g = row[16 * ch + 1] * Quantum.Scale;
        double b = row[16 * ch + 2] * Quantum.Scale;

        await Assert.That(r).IsGreaterThan(0.3); // red should be significant
        await Assert.That(g).IsLessThan(0.05); // green should be near zero
        await Assert.That(b).IsLessThan(0.05); // blue should be near zero
    }

    [Test]
    public async Task Colorize_ZeroAmount_NoChange()
    {
        using var img = CreateSolid(32, 32, 0.5, 0.5, 0.5);
        using var tinted = EnhanceOps.Colorize(img, 255, 0, 0, amount: 0.0);

        int ch = img.NumberOfChannels;
        var srcVals = img.GetPixelRow(16).ToArray();
        var dstVals = tinted.GetPixelRow(16).ToArray();
        for (int c = 0; c < 3; c++)
        {
            double diff = Math.Abs(srcVals[16 * ch + c] - dstVals[16 * ch + c]) * Quantum.Scale;
            await Assert.That(diff).IsLessThan(0.01);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Solarize Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Solarize_BrightPixels_GetInverted()
    {
        // White pixel should be inverted to black at threshold 0.5
        using var img = CreateSolid(16, 16, 1.0, 1.0, 1.0);
        using var solarized = EnhanceOps.Solarize(img, threshold: 0.5);

        var row = solarized.GetPixelRow(8);
        double val = row[8 * 3] * Quantum.Scale;
        await Assert.That(val).IsLessThan(0.01); // white inverted to near-black
    }

    [Test]
    public async Task Solarize_DarkPixels_Unchanged()
    {
        // Dark pixel (0.2) should NOT be inverted at threshold 0.5
        using var img = CreateSolid(16, 16, 0.2, 0.2, 0.2);
        using var solarized = EnhanceOps.Solarize(img, threshold: 0.5);

        var row = solarized.GetPixelRow(8);
        double val = row[8 * 3] * Quantum.Scale;
        await Assert.That(val).IsGreaterThan(0.15).And.IsLessThan(0.25);
    }

    [Test]
    public async Task Solarize_RoundTrip_AtThreshold1_IsOriginal()
    {
        // With threshold=1.0, all values are below threshold, so nothing changes
        using var img = CreateSolid(16, 16, 0.5, 0.3, 0.7);
        using var solarized = EnhanceOps.Solarize(img, threshold: 1.0);

        var srcVals = img.GetPixelRow(8).ToArray();
        var dstVals = solarized.GetPixelRow(8).ToArray();
        for (int c = 0; c < 3; c++)
        {
            await Assert.That((int)dstVals[8 * 3 + c]).IsEqualTo((int)srcVals[8 * 3 + c]);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SepiaTone Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task SepiaTone_ProducesWarmTone()
    {
        using var img = CreateSolid(32, 32, 0.5, 0.5, 0.5);
        using var sepia = EnhanceOps.SepiaTone(img, threshold: 1.0);

        var row = sepia.GetPixelRow(16);
        int ch = sepia.NumberOfChannels;
        double r = row[16 * ch] * Quantum.Scale;
        double g = row[16 * ch + 1] * Quantum.Scale;
        double b = row[16 * ch + 2] * Quantum.Scale;

        // Sepia should have R > G > B (warm brown)
        await Assert.That(r).IsGreaterThan(g);
        await Assert.That(g).IsGreaterThan(b);
    }

    [Test]
    public async Task SepiaTone_ZeroThreshold_NoChange()
    {
        using var img = CreateSolid(32, 32, 0.5, 0.5, 0.5);
        using var sepia = EnhanceOps.SepiaTone(img, threshold: 0.0);

        int ch = img.NumberOfChannels;
        var srcVals = img.GetPixelRow(16).ToArray();
        var dstVals = sepia.GetPixelRow(16).ToArray();
        for (int c = 0; c < 3; c++)
        {
            double diff = Math.Abs(srcVals[16 * ch + c] - dstVals[16 * ch + c]) * Quantum.Scale;
            await Assert.That(diff).IsLessThan(0.01);
        }
    }

    [Test]
    public async Task SepiaTone_PreservesAlpha()
    {
        using var img = CreateSolidWithAlpha(32, 32, 0.5, 0.5, 0.5, 0.6);
        using var sepia = EnhanceOps.SepiaTone(img, threshold: 0.8);

        int ch = sepia.NumberOfChannels;
        var row = sepia.GetPixelRow(16);
        double alpha = row[16 * ch + 3] * Quantum.Scale;
        await Assert.That(alpha).IsGreaterThanOrEqualTo(0.59).And.IsLessThanOrEqualTo(0.61);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════

    private static ImageFrame CreateSolid(int width, int height, double r, double g, double b)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, false);
        ushort rQ = (ushort)(r * Quantum.MaxValue);
        ushort gQ = (ushort)(g * Quantum.MaxValue);
        ushort bQ = (ushort)(b * Quantum.MaxValue);

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = rQ;
                row[x * 3 + 1] = gQ;
                row[x * 3 + 2] = bQ;
            }
        }
        return img;
    }

    private static ImageFrame CreateSolidWithAlpha(int width, int height, double r, double g, double b, double a)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, true);
        ushort rQ = (ushort)(r * Quantum.MaxValue);
        ushort gQ = (ushort)(g * Quantum.MaxValue);
        ushort bQ = (ushort)(b * Quantum.MaxValue);
        ushort aQ = (ushort)(a * Quantum.MaxValue);

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 4] = rQ;
                row[x * 4 + 1] = gQ;
                row[x * 4 + 2] = bQ;
                row[x * 4 + 3] = aQ;
            }
        }
        return img;
    }

    private static ImageFrame CreateUniformGradient(int width, int height, double minVal, double maxVal)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0; y < height; y++)
        {
            double t = (double)y / Math.Max(1, height - 1);
            double val = minVal + t * (maxVal - minVal);
            ushort q = (ushort)(val * Quantum.MaxValue);
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = q;
                row[x * 3 + 1] = q;
                row[x * 3 + 2] = q;
            }
        }
        return img;
    }

    private static ImageFrame CreateColorBiasedImage(int width, int height)
    {
        // Red: 30-60%, Green: 20-90%, Blue: 40-50%
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0; y < height; y++)
        {
            double t = (double)y / Math.Max(1, height - 1);
            ushort rQ = (ushort)((0.3 + t * 0.3) * Quantum.MaxValue);
            ushort gQ = (ushort)((0.2 + t * 0.7) * Quantum.MaxValue);
            ushort bQ = (ushort)((0.4 + t * 0.1) * Quantum.MaxValue);
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = rQ;
                row[x * 3 + 1] = gQ;
                row[x * 3 + 2] = bQ;
            }
        }
        return img;
    }

    private static (double min, double max) GetMinMaxLuminance(ImageFrame img)
    {
        int width = (int)img.Columns;
        int height = (int)img.Rows;
        int ch = img.NumberOfChannels;
        double min = 1.0, max = 0.0;

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double r = row[x * ch] * Quantum.Scale;
                double g = row[x * ch + 1] * Quantum.Scale;
                double b = row[x * ch + 2] * Quantum.Scale;
                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (lum < min) min = lum;
                if (lum > max) max = lum;
            }
        }
        return (min, max);
    }

    private static double GetMeanLuminance(ImageFrame img)
    {
        int width = (int)img.Columns;
        int height = (int)img.Rows;
        int ch = img.NumberOfChannels;
        double sum = 0;

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double r = row[x * ch] * Quantum.Scale;
                double g = row[x * ch + 1] * Quantum.Scale;
                double b = row[x * ch + 2] * Quantum.Scale;
                sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
            }
        }
        return sum / (width * height);
    }

    private static (double rMin, double rMax, double gMin, double gMax, double bMin, double bMax) GetPerChannelMinMax(ImageFrame img)
    {
        int width = (int)img.Columns;
        int height = (int)img.Rows;
        int ch = img.NumberOfChannels;
        double rMin = 1, rMax = 0, gMin = 1, gMax = 0, bMin = 1, bMax = 0;

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double r = row[x * ch] * Quantum.Scale;
                double g = row[x * ch + 1] * Quantum.Scale;
                double b = row[x * ch + 2] * Quantum.Scale;
                if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                if (b < bMin) bMin = b; if (b > bMax) bMax = b;
            }
        }
        return (rMin, rMax, gMin, gMax, bMin, bMax);
    }
}
