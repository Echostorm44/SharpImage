using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Image;
using TUnit.Core;

namespace SharpImage.Tests.Comparison;

/// <summary>
/// Tests for ImageCompare (RMSE, PSNR, SSIM, MAE, etc.) and ImageStatistics.
/// </summary>
public class ComparisonTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Image Compare Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task IdenticalImages_AllMetricsShowPerfect()
    {
        using var img = CreateGradient(32, 32);
        using var copy = CloneImage(img);

        double mse = ImageCompare.MeanSquaredError(img, copy);
        double rmse = ImageCompare.RootMeanSquaredError(img, copy);
        double psnr = ImageCompare.PeakSignalToNoiseRatio(img, copy);
        double mae = ImageCompare.MeanAbsoluteError(img, copy);
        double pae = ImageCompare.PeakAbsoluteError(img, copy);
        long ae = ImageCompare.AbsoluteErrorCount(img, copy);
        double ssim = ImageCompare.StructuralSimilarity(img, copy);

        await Assert.That(mse).IsEqualTo(0.0);
        await Assert.That(rmse).IsEqualTo(0.0);
        await Assert.That(double.IsPositiveInfinity(psnr)).IsTrue();
        await Assert.That(mae).IsEqualTo(0.0);
        await Assert.That(pae).IsEqualTo(0.0);
        await Assert.That(ae).IsEqualTo(0L);
        await Assert.That(ssim).IsEqualTo(1.0);
    }

    [Test]
    public async Task DifferentImages_MetricsShowDifference()
    {
        using var black = CreateSolid(16, 16, 0, 0, 0);
        using var white = CreateSolid(16, 16, 255, 255, 255);

        double mse = ImageCompare.MeanSquaredError(black, white);
        double rmse = ImageCompare.RootMeanSquaredError(black, white);
        double psnr = ImageCompare.PeakSignalToNoiseRatio(black, white);

        // Black vs white should give MSE ≈ 1.0
        await Assert.That(mse).IsGreaterThan(0.99);
        await Assert.That(rmse).IsGreaterThan(0.99);
        await Assert.That(psnr).IsLessThan(1.0); // very low PSNR
    }

    [Test]
    public async Task SlightDifference_PSNRIsHigh()
    {
        using var img = CreateGradient(32, 32);
        using var noisy = CloneImage(img);
        // Add tiny noise to one pixel
        var row = noisy.GetPixelRowForWrite(0);
        row[0] = (ushort)Math.Min(row[0] + 257, Quantum.MaxValue); // ~1/255 change

        double psnr = ImageCompare.PeakSignalToNoiseRatio(img, noisy);
        await Assert.That(psnr).IsGreaterThan(30.0); // should be very high
    }

    [Test]
    public async Task SSIM_SimilarImagesAbove09()
    {
        using var img = CreateGradient(32, 32);
        using var slight = CloneImage(img);
        // Modify a few pixels slightly
        for (int x = 0; x < 4; x++)
        {
            var row = slight.GetPixelRowForWrite(x);
            row[0] = (ushort)Math.Min(row[0] + 1000, Quantum.MaxValue);
        }

        double ssim = ImageCompare.StructuralSimilarity(img, slight);
        await Assert.That(ssim).IsGreaterThan(0.9);
    }

    [Test]
    public async Task AbsoluteErrorCount_WithFuzz()
    {
        using var img = CreateSolid(8, 8, 128, 128, 128);
        using var img2 = CreateSolid(8, 8, 130, 130, 130);

        long exact = ImageCompare.AbsoluteErrorCount(img, img2, fuzz: 0);
        long fuzzy = ImageCompare.AbsoluteErrorCount(img, img2, fuzz: 0.02);

        await Assert.That(exact).IsEqualTo(64L); // all pixels differ
        await Assert.That(fuzzy).IsEqualTo(0L);   // within fuzz tolerance
    }

    [Test]
    public async Task DifferenceImage_BlackWhite_IsWhite()
    {
        using var black = CreateSolid(8, 8, 0, 0, 0);
        using var white = CreateSolid(8, 8, 255, 255, 255);
        using var diff = ImageCompare.CreateDifferenceImage(black, white);

        var row = diff.GetPixelRow(0);
        // Difference should be maxValue
        await Assert.That((int)row[0]).IsEqualTo((int)Quantum.MaxValue);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Image Statistics Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Statistics_SolidBlack_MeanIsZero()
    {
        using var black = CreateSolid(16, 16, 0, 0, 0);
        var stats = ImageStatistics.GetStatistics(black);

        await Assert.That(stats.Composite.Mean).IsLessThan(0.001);
        await Assert.That(stats.Composite.StandardDeviation).IsLessThan(0.001);
        await Assert.That(stats.Composite.Minimum).IsLessThan(0.001);
    }

    [Test]
    public async Task Statistics_SolidWhite_MeanIsOne()
    {
        using var white = CreateSolid(16, 16, 255, 255, 255);
        var stats = ImageStatistics.GetStatistics(white);

        await Assert.That(stats.Composite.Mean).IsGreaterThan(0.99);
        await Assert.That(stats.Composite.Maximum).IsGreaterThan(0.99);
    }

    [Test]
    public async Task Statistics_Gradient_MeanIsMiddle()
    {
        using var grad = CreateGradient(256, 1);
        var stats = ImageStatistics.GetStatistics(grad);

        // Mean of a linear 0..255 gradient should be ~0.5
        await Assert.That(stats.Channels[0].Mean).IsGreaterThan(0.45);
        await Assert.That(stats.Channels[0].Mean).IsLessThan(0.55);
    }

    [Test]
    public async Task Statistics_Entropy_RandomHighSolidLow()
    {
        using var random = CreateRandom(32, 32, seed: 42);
        using var solid = CreateSolid(32, 32, 128, 128, 128);

        var randomStats = ImageStatistics.GetStatistics(random);
        var solidStats = ImageStatistics.GetStatistics(solid);

        // Random should have much higher entropy than solid
        await Assert.That(randomStats.Composite.Entropy).IsGreaterThan(solidStats.Composite.Entropy + 1.0);
    }

    [Test]
    public async Task Histogram_Gradient256_AllBinsHaveOne()
    {
        using var grad = CreateGradient(256, 1);
        long[] hist = ImageStatistics.GetHistogram(grad, channel: 0);

        // Each of 256 bins should have exactly 1 pixel
        int nonZeroBins = 0;
        for (int i = 0; i < 256; i++) if (hist[i] > 0) nonZeroBins++;
        await Assert.That(nonZeroBins).IsEqualTo(256);
    }

    [Test]
    public async Task HuMoments_FlippedImage_SomeMomentsPreserved()
    {
        using var img = CreateGradient(32, 32);
        double[] hu = ImageStatistics.GetHuMoments(img);

        // Hu moments should be non-zero for a gradient image
        await Assert.That(Math.Abs(hu[0])).IsGreaterThan(0);
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

    private static ImageFrame CloneImage(ImageFrame src)
    {
        var clone = new ImageFrame();
        clone.Initialize(src.Columns, src.Rows, src.Colorspace, src.HasAlpha);
        for (int y = 0; y < (int)src.Rows; y++)
        {
            src.GetPixelRow(y).CopyTo(clone.GetPixelRowForWrite(y));
        }
        return clone;
    }
}
