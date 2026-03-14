using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Analysis;

/// <summary>
/// Tests for the perceptual image hashing suite: aHash, dHash, pHash, wHash.
/// </summary>
public class HashSuiteTests
{
    private static readonly string TestAssets = Path.Combine(
        AppContext.BaseDirectory, "TestAssets");

    // ─── Individual Hash Determinism ──────────────────────────────

    [Test]
    public async Task AverageHash_SameImage_SameHash()
    {
        using var img = CreateGradientImage(64, 64);
        ulong h1 = PerceptualHash.ComputeAverageHash(img);
        ulong h2 = PerceptualHash.ComputeAverageHash(img);
        await Assert.That(h1).IsEqualTo(h2);
    }

    [Test]
    public async Task DifferenceHash_SameImage_SameHash()
    {
        using var img = CreateGradientImage(64, 64);
        ulong h1 = PerceptualHash.ComputeDifferenceHash(img);
        ulong h2 = PerceptualHash.ComputeDifferenceHash(img);
        await Assert.That(h1).IsEqualTo(h2);
    }

    [Test]
    public async Task WaveletHash_SameImage_SameHash()
    {
        using var img = CreateGradientImage(64, 64);
        ulong h1 = PerceptualHash.ComputeWaveletHash(img);
        ulong h2 = PerceptualHash.ComputeWaveletHash(img);
        await Assert.That(h1).IsEqualTo(h2);
    }

    // ─── Cross-Hash Differentiation ───────────────────────────────

    [Test]
    public async Task AllHashes_DifferentImages_DifferentHashes()
    {
        using var checker = CreateCheckerboardImage(64, 64);
        using var solid = CreateSolidImage(64, 64, Quantum.MaxValue, 0, 0);

        ulong aCheck = PerceptualHash.ComputeAverageHash(checker);
        ulong aSolid = PerceptualHash.ComputeAverageHash(solid);
        await Assert.That(aCheck).IsNotEqualTo(aSolid);

        ulong dCheck = PerceptualHash.ComputeDifferenceHash(checker);
        ulong dSolid = PerceptualHash.ComputeDifferenceHash(solid);
        await Assert.That(dCheck).IsNotEqualTo(dSolid);

        ulong wCheck = PerceptualHash.ComputeWaveletHash(checker);
        ulong wSolid = PerceptualHash.ComputeWaveletHash(solid);
        await Assert.That(wCheck).IsNotEqualTo(wSolid);
    }

    // ─── ComputeAll ───────────────────────────────────────────────

    [Test]
    public async Task ComputeAll_ReturnsAllFourHashes()
    {
        using var img = CreateCheckerboardImage(64, 64);
        var hashes = PerceptualHash.ComputeAll(img);

        // All should be non-zero for a checkerboard image
        await Assert.That(hashes.AverageHash).IsNotEqualTo(0UL);
        await Assert.That(hashes.DifferenceHash).IsNotEqualTo(0UL);
        await Assert.That(hashes.PerceptualHash).IsNotEqualTo(0UL);
        await Assert.That(hashes.WaveletHash).IsNotEqualTo(0UL);
    }

    [Test]
    public async Task ComputeAll_MatchesIndividual()
    {
        using var img = CreateGradientImage(64, 64);
        var suite = PerceptualHash.ComputeAll(img);

        ulong aHash = PerceptualHash.ComputeAverageHash(img);
        ulong dHash = PerceptualHash.ComputeDifferenceHash(img);
        ulong pHash = PerceptualHash.Compute(img);
        ulong wHash = PerceptualHash.ComputeWaveletHash(img);

        await Assert.That(suite.AverageHash).IsEqualTo(aHash);
        await Assert.That(suite.DifferenceHash).IsEqualTo(dHash);
        await Assert.That(suite.PerceptualHash).IsEqualTo(pHash);
        await Assert.That(suite.WaveletHash).IsEqualTo(wHash);
    }

    // ─── Comparison ───────────────────────────────────────────────

    [Test]
    public async Task Compare_IdenticalImages_ZeroDistance()
    {
        using var img1 = CreateGradientImage(64, 64);
        using var img2 = CreateGradientImage(64, 64);
        var h1 = PerceptualHash.ComputeAll(img1);
        var h2 = PerceptualHash.ComputeAll(img2);
        var result = PerceptualHash.Compare(h1, h2);

        await Assert.That(result.AverageDistance).IsEqualTo(0);
        await Assert.That(result.DifferenceDistance).IsEqualTo(0);
        await Assert.That(result.PerceptualDistance).IsEqualTo(0);
        await Assert.That(result.WaveletDistance).IsEqualTo(0);
        await Assert.That(result.AreSimilar()).IsTrue();
    }

    [Test]
    public async Task Compare_DifferentImages_HighDistance()
    {
        using var gradient = CreateGradientImage(64, 64);
        using var solid = CreateSolidImage(64, 64, Quantum.MaxValue, 0, 0);
        var h1 = PerceptualHash.ComputeAll(gradient);
        var h2 = PerceptualHash.ComputeAll(solid);
        var result = PerceptualHash.Compare(h1, h2);

        // At least some distances should be significant
        await Assert.That(result.MaxDistance).IsGreaterThan(5);
    }

    // ─── Similar Image Detection ──────────────────────────────────

    [Test]
    public async Task AverageHash_SlightlyModifiedImage_LowDistance()
    {
        // A slightly shifted gradient should produce a similar aHash
        using var original = CreateGradientImage(64, 64);
        using var shifted = CreateGradientImage(64, 64, offset: 100);

        ulong h1 = PerceptualHash.ComputeAverageHash(original);
        ulong h2 = PerceptualHash.ComputeAverageHash(shifted);

        int distance = PerceptualHash.HammingDistance(h1, h2);
        await Assert.That(distance).IsLessThan(32); // Not completely different
    }

    // ─── Real Image Test ──────────────────────────────────────────

    [Test]
    public async Task HashSuite_RealImage_ProducesConsistentHashes()
    {
        string wizardPath = Path.Combine(TestAssets, "peppers_rgba.png");
        if (!File.Exists(wizardPath)) return;

        using var img = FormatRegistry.Read(wizardPath);
        var h1 = PerceptualHash.ComputeAll(img);
        var h2 = PerceptualHash.ComputeAll(img);

        var result = PerceptualHash.Compare(h1, h2);
        await Assert.That(result.MaxDistance).IsEqualTo(0);
    }

    [Test]
    public async Task HashSuite_ToString_FormatsCorrectly()
    {
        var hashes = new ImageHashSet
        {
            AverageHash = 0x0123456789ABCDEF,
            DifferenceHash = 0xFEDCBA9876543210,
            PerceptualHash = 0xAAAABBBBCCCCDDDD,
            WaveletHash = 0x1111222233334444,
        };

        string str = hashes.ToString();
        await Assert.That(str).Contains("0123456789abcdef");
        await Assert.That(str).Contains("fedcba9876543210");
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static ImageFrame CreateGradientImage(int width, int height, int offset = 0)
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
                row[off] = Quantum.Clamp(((double)(x + offset) / Math.Max(width - 1, 1)) * Quantum.MaxValue);
                row[off + 1] = Quantum.Clamp(((double)(y + offset) / Math.Max(height - 1, 1)) * Quantum.MaxValue);
                row[off + 2] = Quantum.Clamp(((double)(x + y + offset) / Math.Max(width + height - 2, 1)) * Quantum.MaxValue);
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

    private static ImageFrame CreateCheckerboardImage(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;
        int blockSize = 8;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                bool isWhite = ((x / blockSize) + (y / blockSize)) % 2 == 0;
                ushort val = isWhite ? Quantum.MaxValue : (ushort)0;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }
}
