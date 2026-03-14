using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for additional blur and noise operations: MotionBlur, RadialBlur, SelectiveBlur,
/// AddNoise (7 types), Despeckle, WaveletDenoise.
/// </summary>
public class BlurNoiseOpsTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    // ─── Motion Blur ─────────────────────────────────────────────

    [Test]
    public async Task MotionBlur_PreservesDimensions()
    {
        using var source = CreateCheckerboard(32, 32);
        using var result = BlurNoiseOps.MotionBlur(source, 5, 1.0, 0);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task MotionBlur_HorizontalBlursAlongX()
    {
        // Vertical stripes: should be smeared by horizontal motion blur
        using var source = CreateVerticalStripes(32, 32);
        using var blurred = BlurNoiseOps.MotionBlur(source, 5, 2.0, 0);

        // After horizontal blur, vertical stripe contrast should be reduced
        var srcRow = source.GetPixelRow(16).ToArray();
        var blurRow = blurred.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        int srcMaxDiff = 0, blurMaxDiff = 0;
        for (int x = 2; x < 30; x++)
        {
            int sd = Math.Abs(srcRow[x * channels] - srcRow[(x - 1) * channels]);
            int bd = Math.Abs(blurRow[x * channels] - blurRow[(x - 1) * channels]);
            srcMaxDiff = Math.Max(srcMaxDiff, sd);
            blurMaxDiff = Math.Max(blurMaxDiff, bd);
        }

        await Assert.That(blurMaxDiff).IsLessThan(srcMaxDiff);
    }

    [Test]
    public async Task MotionBlur_45DegreeAngle_ProducesDirectionalBlur()
    {
        // Use step edge instead of checkerboard — 45° blur should smear the edge diagonally
        using var source = CreateStepEdge(32, 32);
        using var blurred = BlurNoiseOps.MotionBlur(source, 4, 1.5, 45);

        await Assert.That(blurred.Columns).IsEqualTo(32);
        await Assert.That(blurred.Rows).IsEqualTo(32);

        // Near the edge boundary (x=16), pixels should be intermediate (blurred)
        var blurRow = blurred.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        int edgeVal = blurRow[16 * channels];
        // Should not be pure black or pure white at the edge
        await Assert.That(edgeVal).IsGreaterThan(0);
        await Assert.That(edgeVal).IsLessThan((int)Quantum.MaxValue);
    }

    [Test]
    public async Task MotionBlur_RealImage_ProducesOutput()
    {
        using var rose = PngCoder.Read(Path.Combine(TestImagesDir, "photo_small.png"));
        using var result = BlurNoiseOps.MotionBlur(rose, 10, 3.0, 30);

        await Assert.That(result.Columns).IsEqualTo(rose.Columns);
        await Assert.That(result.Rows).IsEqualTo(rose.Rows);
    }

    // ─── Radial Blur ─────────────────────────────────────────────

    [Test]
    public async Task RadialBlur_PreservesDimensions()
    {
        using var source = CreateCheckerboard(32, 32);
        using var result = BlurNoiseOps.RadialBlur(source, 10);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task RadialBlur_CenterPixelUnchanged()
    {
        using var source = CreateGradient(33, 33);
        using var result = BlurNoiseOps.RadialBlur(source, 5);

        // Center pixel should be identical or very close (rotation around itself)
        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;
        int centerOff = 16 * channels;

        int diff = Math.Abs(srcRow[centerOff] - dstRow[centerOff]);
        await Assert.That(diff).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task RadialBlur_EdgesMoreBlurredThanCenter()
    {
        using var source = CreateCheckerboard(32, 32);
        using var result = BlurNoiseOps.RadialBlur(source, 15);

        int channels = source.NumberOfChannels;

        // Corner pixel vs center — corner should be more blurred (intermediate values)
        var cornerRow = result.GetPixelRow(0).ToArray();
        var centerRow = result.GetPixelRow(16).ToArray();

        // Corner pixel should be closer to midgray (more blurred)
        int cornerVal = cornerRow[0];
        int centerVal = centerRow[16 * channels];
        int halfMax = Quantum.MaxValue / 2;

        int cornerDistFromMid = Math.Abs(cornerVal - halfMax);
        int centerDistFromMid = Math.Abs(centerVal - halfMax);

        // Corner should be closer to mid (more averaging happened there)
        await Assert.That(cornerDistFromMid).IsLessThanOrEqualTo(centerDistFromMid + 1000);
    }

    // ─── Selective Blur ──────────────────────────────────────────

    [Test]
    public async Task SelectiveBlur_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = BlurNoiseOps.SelectiveBlur(source, 2, 1.0, 0.5);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task SelectiveBlur_PreservesHardEdges()
    {
        // Step edge: left half black, right half white
        using var source = CreateStepEdge(32, 32);
        using var blurred = BlurNoiseOps.SelectiveBlur(source, 2, 1.0, 0.1);

        // With low threshold, the hard edge should be preserved
        var row = blurred.GetPixelRow(16).ToArray();
        int channels = blurred.NumberOfChannels;

        // Left side should still be near black
        await Assert.That((int)row[2 * channels]).IsLessThan(Quantum.MaxValue / 4);
        // Right side should still be near white
        await Assert.That((int)row[29 * channels]).IsGreaterThan(Quantum.MaxValue * 3 / 4);
    }

    [Test]
    public async Task SelectiveBlur_HighThreshold_SmoothsGradient()
    {
        // Use gradient — high threshold should smooth transitions since all contrasts are below threshold
        using var source = CreateGradient(32, 32);
        using var blurred = BlurNoiseOps.SelectiveBlur(source, 2, 1.0, 1.0);

        // Measure smoothness: blurred gradient should have lower max adjacent diff than source
        var srcRow = source.GetPixelRow(16).ToArray();
        var blurRow = blurred.GetPixelRow(16).ToArray();
        int channels = blurred.NumberOfChannels;

        int srcMaxDiff = MaxAdjacentDiff(srcRow, channels, 32);
        int blurMaxDiff = MaxAdjacentDiff(blurRow, channels, 32);

        // Blurred gradient should be smoother (smaller steps between adjacent pixels)
        await Assert.That(blurMaxDiff).IsLessThanOrEqualTo(srcMaxDiff);
    }

    // ─── Add Noise ───────────────────────────────────────────────

    [Test]
    [Arguments(NoiseType.Uniform)]
    [Arguments(NoiseType.Gaussian)]
    [Arguments(NoiseType.Impulse)]
    [Arguments(NoiseType.Laplacian)]
    [Arguments(NoiseType.MultiplicativeGaussian)]
    [Arguments(NoiseType.Poisson)]
    [Arguments(NoiseType.Random)]
    public async Task AddNoise_AllTypes_PreservesDimensions(NoiseType noiseType)
    {
        using var source = CreateMidGray(32, 32);
        using var result = BlurNoiseOps.AddNoise(source, noiseType, 1.0);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task AddNoise_Gaussian_ChangesPixelValues()
    {
        using var source = CreateMidGray(32, 32);
        using var noisy = BlurNoiseOps.AddNoise(source, NoiseType.Gaussian, 1.0);

        var srcRow = source.GetPixelRow(16).ToArray();
        var noisyRow = noisy.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        // At least some pixels should differ
        bool anyDifferent = false;
        for (int x = 0; x < 32; x++)
        {
            if (srcRow[x * channels] != noisyRow[x * channels])
            {
                anyDifferent = true;
                break;
            }
        }

        await Assert.That(anyDifferent).IsTrue();
    }

    [Test]
    public async Task AddNoise_Impulse_CreatesSaltAndPepper()
    {
        using var source = CreateMidGray(64, 64);
        using var noisy = BlurNoiseOps.AddNoise(source, NoiseType.Impulse, 1.0);

        int channels = source.NumberOfChannels;
        int blackCount = 0, whiteCount = 0;

        for (int y = 0; y < 64; y++)
        {
            var row = noisy.GetPixelRow(y).ToArray();
            for (int x = 0; x < 64; x++)
            {
                ushort val = row[x * channels];
                if (val == 0) blackCount++;
                else if (val == Quantum.MaxValue) whiteCount++;
            }
        }

        // Should have some salt and pepper pixels
        await Assert.That(blackCount + whiteCount).IsGreaterThan(0);
    }

    [Test]
    public async Task AddNoise_Random_ProducesFullRange()
    {
        using var source = CreateMidGray(64, 64);
        using var noisy = BlurNoiseOps.AddNoise(source, NoiseType.Random, 1.0);

        int channels = source.NumberOfChannels;
        ushort minVal = Quantum.MaxValue, maxVal = 0;

        for (int y = 0; y < 64; y++)
        {
            var row = noisy.GetPixelRow(y).ToArray();
            for (int x = 0; x < 64; x++)
            {
                ushort val = row[x * channels];
                minVal = Math.Min(minVal, val);
                maxVal = Math.Max(maxVal, val);
            }
        }

        // Random noise should span most of the quantum range
        await Assert.That((int)maxVal - (int)minVal).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task AddNoise_PreservesAlpha()
    {
        var source = new ImageFrame();
        source.Initialize(16, 16, ColorspaceType.SRGB, true);
        int channels = source.NumberOfChannels;
        for (int y = 0; y < 16; y++)
        {
            var row = source.GetPixelRowForWrite(y);
            for (int x = 0; x < 16; x++)
            {
                int off = x * channels;
                row[off] = Quantum.MaxValue / 2;
                row[off + 1] = Quantum.MaxValue / 2;
                row[off + 2] = Quantum.MaxValue / 2;
                row[off + 3] = Quantum.MaxValue; // Full alpha
            }
        }

        using var noisy = BlurNoiseOps.AddNoise(source, NoiseType.Gaussian, 1.0);
        source.Dispose();

        var dstRow = noisy.GetPixelRow(8).ToArray();
        int alphaChannel = channels - 1;
        for (int x = 0; x < 16; x++)
            await Assert.That(dstRow[x * channels + alphaChannel]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── Despeckle ───────────────────────────────────────────────

    [Test]
    public async Task Despeckle_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = BlurNoiseOps.Despeckle(source);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task Despeckle_ReducesImpulseNoise()
    {
        // Create mid-gray image with impulse noise added
        using var source = CreateMidGray(32, 32);
        using var noisy = BlurNoiseOps.AddNoise(source, NoiseType.Impulse, 1.0);
        using var despeckled = BlurNoiseOps.Despeckle(noisy);

        int channels = source.NumberOfChannels;
        ushort midGray = Quantum.MaxValue / 2;

        // Count pixels far from mid-gray in noisy vs despeckled
        int noisyOutliers = 0, despeckledOutliers = 0;
        int outlierThreshold = Quantum.MaxValue / 4;

        for (int y = 1; y < 31; y++)
        {
            var noisyRow = noisy.GetPixelRow(y).ToArray();
            var cleanRow = despeckled.GetPixelRow(y).ToArray();
            for (int x = 1; x < 31; x++)
            {
                int nOff = x * channels;
                if (Math.Abs(noisyRow[nOff] - midGray) > outlierThreshold) noisyOutliers++;
                if (Math.Abs(cleanRow[nOff] - midGray) > outlierThreshold) despeckledOutliers++;
            }
        }

        // Despeckled should have fewer outliers
        await Assert.That(despeckledOutliers).IsLessThanOrEqualTo(noisyOutliers);
    }

    [Test]
    public async Task Despeckle_UniformImage_Unchanged()
    {
        using var source = CreateMidGray(16, 16);
        using var result = BlurNoiseOps.Despeckle(source);

        int channels = source.NumberOfChannels;
        var srcRow = source.GetPixelRow(8).ToArray();
        var dstRow = result.GetPixelRow(8).ToArray();

        for (int x = 0; x < 16; x++)
        {
            int diff = Math.Abs(srcRow[x * channels] - dstRow[x * channels]);
            await Assert.That(diff).IsLessThanOrEqualTo(257); // One hull step max
        }
    }

    // ─── Wavelet Denoise ─────────────────────────────────────────

    [Test]
    public async Task WaveletDenoise_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = BlurNoiseOps.WaveletDenoise(source, 0.3);

        await Assert.That(result.Columns).IsEqualTo(32);
        await Assert.That(result.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task WaveletDenoise_ReducesGaussianNoise()
    {
        using var source = CreateMidGray(64, 64);
        using var noisy = BlurNoiseOps.AddNoise(source, NoiseType.Gaussian, 1.0);
        using var denoised = BlurNoiseOps.WaveletDenoise(noisy, 0.5);

        int channels = source.NumberOfChannels;
        ushort midGray = Quantum.MaxValue / 2;

        // Measure variance from expected mid-gray
        double noisyVariance = 0, denoisedVariance = 0;
        int count = 0;

        for (int y = 2; y < 62; y++)
        {
            var noisyRow = noisy.GetPixelRow(y).ToArray();
            var cleanRow = denoised.GetPixelRow(y).ToArray();
            for (int x = 2; x < 62; x++)
            {
                double nDiff = noisyRow[x * channels] - midGray;
                double dDiff = cleanRow[x * channels] - midGray;
                noisyVariance += nDiff * nDiff;
                denoisedVariance += dDiff * dDiff;
                count++;
            }
        }

        noisyVariance /= count;
        denoisedVariance /= count;

        // Denoised should have lower variance
        await Assert.That(denoisedVariance).IsLessThan(noisyVariance);
    }

    [Test]
    public async Task WaveletDenoise_ZeroThreshold_MinimalChange()
    {
        using var source = CreateGradient(32, 32);
        using var result = BlurNoiseOps.WaveletDenoise(source, 0.0);

        // With zero threshold, the wavelet denoise should barely change the image
        // (all detail preserved)
        int channels = source.NumberOfChannels;
        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();

        double totalDiff = 0;
        for (int x = 0; x < 32; x++)
            totalDiff += Math.Abs(srcRow[x * channels] - dstRow[x * channels]);

        double avgDiff = totalDiff / 32;
        // Average difference should be very small (rounding at most)
        await Assert.That(avgDiff).IsLessThan(2.0);
    }

    [Test]
    public async Task WaveletDenoise_PreservesAlpha()
    {
        var source = new ImageFrame();
        source.Initialize(16, 16, ColorspaceType.SRGB, true);
        int channels = source.NumberOfChannels;
        for (int y = 0; y < 16; y++)
        {
            var row = source.GetPixelRowForWrite(y);
            for (int x = 0; x < 16; x++)
            {
                int off = x * channels;
                row[off] = Quantum.MaxValue / 2;
                row[off + 1] = Quantum.MaxValue / 2;
                row[off + 2] = Quantum.MaxValue / 2;
                row[off + 3] = (ushort)(Quantum.MaxValue / 2);
            }
        }

        using var result = BlurNoiseOps.WaveletDenoise(source, 0.5);
        source.Dispose();

        var dstRow = result.GetPixelRow(8).ToArray();
        int alphaChannel = channels - 1;
        for (int x = 0; x < 16; x++)
            await Assert.That(dstRow[x * channels + alphaChannel]).IsEqualTo((ushort)(Quantum.MaxValue / 2));
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static int MaxAdjacentDiff(ushort[] row, int channels, int width)
    {
        int maxDiff = 0;
        for (int x = 1; x < width - 1; x++)
        {
            int diff = Math.Abs(row[x * channels] - row[(x - 1) * channels]);
            maxDiff = Math.Max(maxDiff, diff);
        }
        return maxDiff;
    }

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

    private static ImageFrame CreateVerticalStripes(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (x % 4 < 2) ? Quantum.MaxValue : (ushort)0;
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
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = Quantum.MaxValue / 2;
                row[off + 1] = Quantum.MaxValue / 2;
                row[off + 2] = Quantum.MaxValue / 2;
            }
        }
        return image;
    }
}
