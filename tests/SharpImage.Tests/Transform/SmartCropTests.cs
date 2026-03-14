using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for entropy-based smart crop and interest map generation.
/// </summary>
public class SmartCropTests
{
    private static readonly string TestAssets = Path.Combine(
        AppContext.BaseDirectory, "TestAssets");

    // ─── Dimension Tests ──────────────────────────────────────────

    [Test]
    public async Task SmartCrop_ReturnsCorrectDimensions()
    {
        using var source = CreateTestImage(100, 80);
        using var result = SmartCrop.Apply(source, 50, 40);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(40u);
    }

    [Test]
    public async Task SmartCrop_TargetLargerThanSource_ClampsToSource()
    {
        using var source = CreateTestImage(60, 40);
        using var result = SmartCrop.Apply(source, 200, 200);

        await Assert.That(result.Columns).IsEqualTo(60u);
        await Assert.That(result.Rows).IsEqualTo(40u);
    }

    [Test]
    public async Task SmartCrop_SameSize_ReturnsCopy()
    {
        using var source = CreateTestImage(50, 50);
        using var result = SmartCrop.Apply(source, 50, 50);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(50u);
    }

    // ─── Content Awareness Tests ──────────────────────────────────

    [Test]
    public async Task SmartCrop_PrefersHighEntropyRegion()
    {
        // Create an image with a detailed region in the top-left and flat elsewhere
        using var source = CreateHighEntropyCornerImage(120, 80);
        using var result = SmartCrop.Apply(source, 60, 40);

        // The crop should prefer the top-left corner where entropy is highest
        // Verify by checking that the result contains varied pixel values
        var row = result.GetPixelRow(0);
        bool hasVariation = false;
        ushort firstR = row[0];
        for (int x = 1; x < 60; x++)
        {
            if (row[x * 3] != firstR)
            {
                hasVariation = true;
                break;
            }
        }
        await Assert.That(hasVariation).IsTrue();
    }

    [Test]
    public async Task SmartCrop_AvoidsFlatRegion()
    {
        // Image: left half is solid gray, right half has checkerboard pattern
        using var source = CreateHalfFlatHalfDetailImage(200, 100);
        var (x, y, w, h) = SmartCrop.FindBestRegion(source, 80, 80);

        // Should prefer the right half (checkerboard) — x should be >= 60 
        // (allowing some overlap since the window is 80 pixels wide)
        await Assert.That(x).IsGreaterThanOrEqualTo(40);
    }

    // ─── FindBestRegion Tests ─────────────────────────────────────

    [Test]
    public async Task FindBestRegion_ReturnsValidCoordinates()
    {
        using var source = CreateTestImage(100, 80);
        var (x, y, w, h) = SmartCrop.FindBestRegion(source, 50, 40);

        await Assert.That(x).IsGreaterThanOrEqualTo(0);
        await Assert.That(y).IsGreaterThanOrEqualTo(0);
        await Assert.That(x + w).IsLessThanOrEqualTo(100);
        await Assert.That(y + h).IsLessThanOrEqualTo(80);
        await Assert.That(w).IsEqualTo(50);
        await Assert.That(h).IsEqualTo(40);
    }

    [Test]
    public async Task FindBestRegion_RequestedSizeEqualsSource_ReturnsOrigin()
    {
        using var source = CreateTestImage(50, 50);
        var (x, y, w, h) = SmartCrop.FindBestRegion(source, 50, 50);

        await Assert.That(x).IsEqualTo(0);
        await Assert.That(y).IsEqualTo(0);
        await Assert.That(w).IsEqualTo(50);
        await Assert.That(h).IsEqualTo(50);
    }

    // ─── Interest Map Tests ───────────────────────────────────────

    [Test]
    public async Task GetInterestMap_ReturnsCorrectDimensions()
    {
        using var source = CreateTestImage(80, 60);
        using var map = SmartCrop.GetInterestMap(source);

        await Assert.That(map.Columns).IsEqualTo(80u);
        await Assert.That(map.Rows).IsEqualTo(60u);
    }

    [Test]
    public async Task GetInterestMap_NoAlphaChannel()
    {
        using var source = CreateTestImage(40, 30);
        using var map = SmartCrop.GetInterestMap(source);

        await Assert.That(map.HasAlpha).IsFalse();
    }

    [Test]
    public async Task GetInterestMap_HighEntropyRegionBrighter()
    {
        // Create image with high-entropy top-left and solid bottom-right
        using var source = CreateHighEntropyCornerImage(80, 60);
        using var map = SmartCrop.GetInterestMap(source);

        // Sample average brightness in top-left quadrant vs bottom-right quadrant
        double topLeftBrightness = AveragePixelBrightness(map, 0, 0, 40, 30);
        double bottomRightBrightness = AveragePixelBrightness(map, 40, 30, 40, 30);

        await Assert.That(topLeftBrightness).IsGreaterThan(bottomRightBrightness);
    }

    // ─── Real Image Test ──────────────────────────────────────────

    [Test]
    public async Task SmartCrop_WizardImage_ProducesValidOutput()
    {
        string wizardPath = Path.Combine(TestAssets, "peppers_rgba.png");
        if (!File.Exists(wizardPath)) return;

        using var wizard = FormatRegistry.Read(wizardPath);
        using var cropped = SmartCrop.Apply(wizard, 400, 400);

        await Assert.That(cropped.Columns).IsEqualTo(400u);
        await Assert.That(cropped.Rows).IsEqualTo(400u);

        // Verify non-blank output
        var row = cropped.GetPixelRow(200);
        bool hasContent = false;
        for (int x = 0; x < 400; x++)
        {
            if (row[x * 4] > 0 || row[x * 4 + 1] > 0 || row[x * 4 + 2] > 0)
            {
                hasContent = true;
                break;
            }
        }
        await Assert.That(hasContent).IsTrue();
    }

    [Test]
    public async Task SmartCrop_WithAlpha_PreservesAlpha()
    {
        using var source = new ImageFrame();
        source.Initialize(80, 60, ColorspaceType.SRGB, true);
        // Fill with varied content and varying alpha
        for (int y = 0; y < 60; y++)
        {
            var row = source.GetPixelRowForWrite(y);
            for (int x = 0; x < 80; x++)
            {
                row[x * 4] = (ushort)((x * 819) % Quantum.MaxValue);
                row[x * 4 + 1] = (ushort)((y * 1092) % Quantum.MaxValue);
                row[x * 4 + 2] = (ushort)(((x + y) * 546) % Quantum.MaxValue);
                row[x * 4 + 3] = (ushort)(x < 40 ? Quantum.MaxValue : Quantum.MaxValue / 2);
            }
        }

        using var result = SmartCrop.Apply(source, 40, 30);
        await Assert.That(result.HasAlpha).IsTrue();

        // Verify alpha values are present
        var resultRow = result.GetPixelRow(0);
        await Assert.That(resultRow[3]).IsGreaterThan((ushort)0);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    static ImageFrame CreateTestImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                // Checkerboard with varying intensity
                bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                ushort val = (ushort)(checker ? Quantum.MaxValue * 3 / 4 : Quantum.MaxValue / 4);
                row[x * 3] = val;
                row[x * 3 + 1] = val;
                row[x * 3 + 2] = val;
            }
        }
        return frame;
    }

    static ImageFrame CreateHighEntropyCornerImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        var rng = new Random(42);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                if (x < width / 2 && y < height / 2)
                {
                    // High entropy: random noise
                    row[x * 3] = (ushort)(rng.Next(Quantum.MaxValue));
                    row[x * 3 + 1] = (ushort)(rng.Next(Quantum.MaxValue));
                    row[x * 3 + 2] = (ushort)(rng.Next(Quantum.MaxValue));
                }
                else
                {
                    // Flat: solid gray
                    ushort gray = (ushort)(Quantum.MaxValue / 2);
                    row[x * 3] = gray;
                    row[x * 3 + 1] = gray;
                    row[x * 3 + 2] = gray;
                }
            }
        }
        return frame;
    }

    static ImageFrame CreateHalfFlatHalfDetailImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                if (x < width / 2)
                {
                    // Left half: solid gray
                    ushort gray = (ushort)(Quantum.MaxValue / 2);
                    row[x * 3] = gray;
                    row[x * 3 + 1] = gray;
                    row[x * 3 + 2] = gray;
                }
                else
                {
                    // Right half: fine checkerboard
                    bool checker = ((x % 2) + (y % 2)) % 2 == 0;
                    ushort val = checker ? Quantum.MaxValue : (ushort)0;
                    row[x * 3] = val;
                    row[x * 3 + 1] = val;
                    row[x * 3 + 2] = val;
                }
            }
        }
        return frame;
    }

    static double AveragePixelBrightness(ImageFrame img, int rx, int ry, int rw, int rh)
    {
        double sum = 0;
        int channels = img.HasAlpha ? 4 : 3;
        for (int y = ry; y < ry + rh; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = rx; x < rx + rw; x++)
            {
                sum += row[x * channels] * Quantum.Scale;
                sum += row[x * channels + 1] * Quantum.Scale;
                sum += row[x * channels + 2] * Quantum.Scale;
            }
        }
        return sum / (rw * rh * 3);
    }
}
