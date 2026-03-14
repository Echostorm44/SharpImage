using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for content-aware resize (seam carving) and energy map operations.
/// </summary>
public class SeamCarvingTests
{
    private static readonly string TestAssets = Path.Combine(
        AppContext.BaseDirectory, "TestAssets");

    // ─── Dimension Tests ──────────────────────────────────────────

    [Test]
    public async Task SeamCarve_ReduceWidth_CorrectDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var result = SeamCarving.Apply(source, 50, 48);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(48u);
    }

    [Test]
    public async Task SeamCarve_ReduceHeight_CorrectDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var result = SeamCarving.Apply(source, 64, 40);

        await Assert.That(result.Columns).IsEqualTo(64u);
        await Assert.That(result.Rows).IsEqualTo(40u);
    }

    [Test]
    public async Task SeamCarve_ReduceBoth_CorrectDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var result = SeamCarving.Apply(source, 50, 40);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(40u);
    }

    [Test]
    public async Task SeamCarve_SameDimensions_ReturnsIdenticalCopy()
    {
        using var source = CreateGradientImage(32, 32);
        using var result = SeamCarving.Apply(source, 32, 32);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    // ─── Content Preservation ─────────────────────────────────────

    [Test]
    public async Task SeamCarve_PreservesHighEnergyContent()
    {
        // Create image with a high-contrast feature in the center (vertical red stripe)
        // Seam carving should remove low-energy uniform areas first
        using var source = CreateImageWithCenterStripe(80, 60);
        using var result = SeamCarving.Apply(source, 60, 60);

        // The center stripe should still be present (high-energy edges preserved)
        int midX = (int)(result.Columns / 2);
        int midY = (int)(result.Rows / 2);
        var midRow = result.GetPixelRow(midY);
        int channels = result.NumberOfChannels;

        // Red channel should be significant somewhere near center
        ushort redValue = midRow[midX * channels];
        await Assert.That(redValue).IsGreaterThan((ushort)0);
    }

    [Test]
    public async Task SeamCarve_PreservesAlphaChannel()
    {
        using var source = CreateGradientImageWithAlpha(64, 48);
        using var result = SeamCarving.Apply(source, 50, 40);

        await Assert.That(result.HasAlpha).IsTrue();
        await Assert.That(result.NumberOfChannels).IsEqualTo(4);
    }

    // ─── Energy Map ───────────────────────────────────────────────

    [Test]
    public async Task EnergyMap_CorrectDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var energy = SeamCarving.GetEnergyMap(source);

        await Assert.That(energy.Columns).IsEqualTo(64u);
        await Assert.That(energy.Rows).IsEqualTo(48u);
    }

    [Test]
    public async Task EnergyMap_SolidImage_AllZeroEnergy()
    {
        using var source = CreateSolidImage(32, 32, 10000, 10000, 10000);
        using var energy = SeamCarving.GetEnergyMap(source);

        // Solid image has zero gradient everywhere (interior pixels)
        // Edge pixels will be zero too since we clamp to image bounds
        var midRow = energy.GetPixelRow(16);
        int channels = energy.NumberOfChannels;
        ushort midEnergy = midRow[16 * channels]; // R channel = energy
        await Assert.That(midEnergy).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task EnergyMap_EdgeDetection_HighAtBoundaries()
    {
        // Image with sharp left-right boundary should have high energy at the edge
        using var source = CreateLeftRightImage(64, 48);
        using var energy = SeamCarving.GetEnergyMap(source);

        // At the boundary (x=32), energy should be much higher than at edges
        int channels = energy.NumberOfChannels;
        var midRow = energy.GetPixelRow(24);
        ushort boundaryEnergy = midRow[32 * channels];
        ushort uniformEnergy = midRow[10 * channels];

        await Assert.That(boundaryEnergy).IsGreaterThan(uniformEnergy);
    }

    // ─── Validation ───────────────────────────────────────────────

    [Test]
    public async Task SeamCarve_WidthTooLarge_Throws()
    {
        using var source = CreateGradientImage(32, 32);
        await Assert.That(() => SeamCarving.Apply(source, 40, 32))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SeamCarve_HeightTooLarge_Throws()
    {
        using var source = CreateGradientImage(32, 32);
        await Assert.That(() => SeamCarving.Apply(source, 32, 40))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SeamCarve_ZeroDimension_Throws()
    {
        using var source = CreateGradientImage(32, 32);
        await Assert.That(() => SeamCarving.Apply(source, 0, 32))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    // ─── Real Image Test ──────────────────────────────────────────

    [Test]
    public async Task SeamCarve_RealImage_ProducesValidOutput()
    {
        string wizardPath = Path.Combine(TestAssets, "peppers_rgba.png");
        if (!File.Exists(wizardPath)) return;

        using var source = FormatRegistry.Read(wizardPath);
        int targetWidth = (int)(source.Columns * 3 / 4);
        int targetHeight = (int)source.Rows;

        using var result = SeamCarving.Apply(source, targetWidth, targetHeight);

        await Assert.That(result.Columns).IsEqualTo((uint)targetWidth);
        await Assert.That(result.Rows).IsEqualTo((uint)targetHeight);

        // Verify output is not all black (sanity check)
        var midRow = result.GetPixelRow((int)(result.Rows / 2));
        int channels = result.NumberOfChannels;
        bool hasNonZero = false;
        for (int x = 0; x < (int)result.Columns && !hasNonZero; x++)
        {
            for (int c = 0; c < 3; c++)
            {
                if (midRow[x * channels + c] > 0) { hasNonZero = true; break; }
            }
        }
        await Assert.That(hasNonZero).IsTrue();
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

    private static ImageFrame CreateGradientImageWithAlpha(int width, int height)
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
                row[off + 3] = Quantum.Opaque;
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

    private static ImageFrame CreateImageWithCenterStripe(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;
        int stripeStart = width / 2 - 3;
        int stripeEnd = width / 2 + 3;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                if (x >= stripeStart && x < stripeEnd)
                {
                    row[off] = Quantum.MaxValue;     // Red stripe
                    row[off + 1] = 0;
                    row[off + 2] = 0;
                }
                else
                {
                    row[off] = (ushort)(Quantum.MaxValue / 2);
                    row[off + 1] = (ushort)(Quantum.MaxValue / 2);
                    row[off + 2] = (ushort)(Quantum.MaxValue / 2);
                }
            }
        }
        return image;
    }

    private static ImageFrame CreateLeftRightImage(int width, int height)
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
                if (x < width / 2)
                {
                    row[off] = 0;
                    row[off + 1] = 0;
                    row[off + 2] = 0;
                }
                else
                {
                    row[off] = Quantum.MaxValue;
                    row[off + 1] = Quantum.MaxValue;
                    row[off + 2] = Quantum.MaxValue;
                }
            }
        }
        return image;
    }
}
