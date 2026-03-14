// Unit tests for PerceptualDiff — SSIM and Delta-E 2000 comparison.

using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Analysis;

public class PerceptualDiffTests
{
    private static readonly string TestAssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string Asset(string name) => Path.Combine(TestAssetsDir, name);

    [Test]
    public async Task IdenticalImages_Have_Perfect_Ssim()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        double ssim = PerceptualDiff.ComputeSsim(image, image);

        await Assert.That(ssim).IsGreaterThan(0.999);
    }

    [Test]
    public async Task IdenticalImages_Have_Zero_DeltaE()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var result = PerceptualDiff.Compare(image, image, generateSsimMap: false, generateDeltaEMap: false);

        await Assert.That(result.MeanDeltaE).IsLessThan(0.001);
        await Assert.That(result.MaxDeltaE).IsLessThan(0.001);
        await Assert.That(result.PercentChanged).IsLessThan(0.001);
    }

    [Test]
    public async Task IdenticalImages_Full_Compare_Has_Maps()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var result = PerceptualDiff.Compare(image, image);

        await Assert.That(result.SsimMap).IsNotNull();
        await Assert.That(result.DeltaEMap).IsNotNull();
        await Assert.That((int)result.SsimMap!.Columns).IsEqualTo((int)image.Columns);
        await Assert.That((int)result.DeltaEMap!.Columns).IsEqualTo((int)image.Columns);

        result.SsimMap?.Dispose();
        result.DeltaEMap?.Dispose();
    }

    [Test]
    public async Task DifferentImages_Have_Lower_Ssim()
    {
        using var imageA = FormatRegistry.Read(Asset("photo_small.png"));

        // Create a modified version (invert colors)
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        var imageB = new ImageFrame();
        imageB.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0; y < height; y++)
        {
            var srcRow = imageA.GetPixelRow(y);
            var dstRow = imageB.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                dstRow[x * 3] = (ushort)(Quantum.MaxValue - srcRow[x * 3]);
                dstRow[x * 3 + 1] = (ushort)(Quantum.MaxValue - srcRow[x * 3 + 1]);
                dstRow[x * 3 + 2] = (ushort)(Quantum.MaxValue - srcRow[x * 3 + 2]);
            }
        }

        double ssim = PerceptualDiff.ComputeSsim(imageA, imageB);
        await Assert.That(ssim).IsLessThan(0.5);

        imageB.Dispose();
    }

    [Test]
    public async Task SlightlyModified_Has_High_But_Not_Perfect_Ssim()
    {
        using var imageA = FormatRegistry.Read(Asset("photo_small.png"));

        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        int channels = imageA.HasAlpha ? 4 : 3;
        var imageB = new ImageFrame();
        imageB.Initialize(width, height, ColorspaceType.SRGB, imageA.HasAlpha);

        // Add small noise
        var rng = new Random(42);
        for (int y = 0; y < height; y++)
        {
            var srcRow = imageA.GetPixelRow(y);
            var dstRow = imageB.GetPixelRowForWrite(y);
            for (int x = 0; x < width * channels; x++)
            {
                int noise = rng.Next(-500, 500);
                dstRow[x] = Quantum.Clamp(srcRow[x] + noise);
            }
        }

        double ssim = PerceptualDiff.ComputeSsim(imageA, imageB);
        await Assert.That(ssim).IsGreaterThan(0.9);
        await Assert.That(ssim).IsLessThan(1.0);

        imageB.Dispose();
    }

    [Test]
    public async Task DeltaE_Between_Similar_Colors_Is_Small()
    {
        // Two solid-color images with slightly different colors
        var imageA = new ImageFrame();
        imageA.Initialize(50, 50, ColorspaceType.SRGB, false);
        var imageB = new ImageFrame();
        imageB.Initialize(50, 50, ColorspaceType.SRGB, false);

        for (int y = 0; y < 50; y++)
        {
            var rowA = imageA.GetPixelRowForWrite(y);
            var rowB = imageB.GetPixelRowForWrite(y);
            for (int x = 0; x < 50; x++)
            {
                int offset = x * 3;
                rowA[offset] = 32768; rowA[offset + 1] = 32768; rowA[offset + 2] = 32768;
                rowB[offset] = 33000; rowB[offset + 1] = 32768; rowB[offset + 2] = 32768;
            }
        }

        var result = PerceptualDiff.Compare(imageA, imageB, generateSsimMap: false, generateDeltaEMap: false);
        await Assert.That(result.MeanDeltaE).IsLessThan(2.0); // just noticeable difference

        imageA.Dispose();
        imageB.Dispose();
    }

    [Test]
    public async Task DeltaE_Map_Dimensions_Match()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var result = PerceptualDiff.Compare(image, image, generateSsimMap: false);

        await Assert.That(result.DeltaEMap).IsNotNull();
        await Assert.That((int)result.DeltaEMap!.Columns).IsEqualTo((int)image.Columns);
        await Assert.That((int)result.DeltaEMap!.Rows).IsEqualTo((int)image.Rows);

        result.DeltaEMap?.Dispose();
    }

    [Test]
    public async Task DimensionMismatch_Throws()
    {
        var imageA = new ImageFrame();
        imageA.Initialize(10, 10, ColorspaceType.SRGB, false);
        var imageB = new ImageFrame();
        imageB.Initialize(20, 20, ColorspaceType.SRGB, false);

        await Assert.That(() => PerceptualDiff.ComputeSsim(imageA, imageB))
            .Throws<ArgumentException>();

        imageA.Dispose();
        imageB.Dispose();
    }

    [Test]
    public async Task MeanSsim_Is_Between_0_And_1()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var result = PerceptualDiff.Compare(image, image, generateSsimMap: false, generateDeltaEMap: false);

        await Assert.That(result.MeanSsim).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(result.MeanSsim).IsLessThanOrEqualTo(1.0);
    }

    [Test]
    public async Task PercentChanged_Is_Zero_For_Identical()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var result = PerceptualDiff.Compare(image, image, generateSsimMap: false, generateDeltaEMap: false);

        await Assert.That(result.PercentChanged).IsEqualTo(0.0);
    }
}
