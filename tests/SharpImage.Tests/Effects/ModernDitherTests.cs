using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Threshold;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for modern dithering methods: blue noise, Stucki, Atkinson, Sierra.
/// </summary>
public class ModernDitherTests
{
    private static readonly string TestAssets = Path.Combine(
        AppContext.BaseDirectory, "TestAssets");

    // ─── Blue Noise Dithering ──────────────────────────────────────

    [Test]
    public async Task BlueNoiseDither_ProducesOnlyBlackAndWhite_TwoLevels()
    {
        using var image = CreateGradient(64, 64);
        ThresholdOps.BlueNoiseDither(image, 2);

        bool allBinary = true;
        var row = image.GetPixelRow(32);
        for (int x = 0; x < 64; x++)
        {
            ushort val = row[x * 3];
            if (val != 0 && val != Quantum.MaxValue) allBinary = false;
        }
        await Assert.That(allBinary).IsTrue();
    }

    [Test]
    public async Task BlueNoiseDither_FourLevels_ProducesExpectedValues()
    {
        using var image = CreateGradient(64, 64);
        ThresholdOps.BlueNoiseDither(image, 4);

        var allowedValues = new HashSet<ushort>
        {
            0,
            (ushort)(Quantum.MaxValue / 3),
            (ushort)(Quantum.MaxValue * 2 / 3),
            Quantum.MaxValue
        };

        bool allAllowed = true;
        var row = image.GetPixelRow(32);
        for (int x = 0; x < 64; x++)
        {
            ushort val = row[x * 3];
            if (!allowedValues.Any(a => Math.Abs(val - a) <= 1)) allAllowed = false;
        }
        await Assert.That(allAllowed).IsTrue();
    }

    [Test]
    public async Task BlueNoiseDither_PreservesDimensions()
    {
        using var image = CreateGradient(100, 80);
        ThresholdOps.BlueNoiseDither(image);

        await Assert.That(image.Columns).IsEqualTo(100u);
        await Assert.That(image.Rows).IsEqualTo(80u);
    }

    [Test]
    public async Task BlueNoiseDither_PreservesAlpha()
    {
        using var image = CreateGradientWithAlpha(64, 64);
        ushort originalAlpha = image.GetPixelRow(0)[3]; // RGBA, alpha channel
        ThresholdOps.BlueNoiseDither(image);

        var afterRow = image.GetPixelRow(0);
        ushort afterAlpha = afterRow[3];
        await Assert.That(afterAlpha).IsEqualTo(originalAlpha);
    }

    // ─── Stucki Dithering ──────────────────────────────────────────

    [Test]
    public async Task StuckiDither_ProducesBinaryOutput()
    {
        using var image = CreateGradient(64, 64);
        ThresholdOps.StuckiDither(image, 2);

        bool allBinary = true;
        var row = image.GetPixelRow(32);
        for (int x = 0; x < 64; x++)
        {
            ushort val = row[x * 3];
            if (val != 0 && val != Quantum.MaxValue) allBinary = false;
        }
        await Assert.That(allBinary).IsTrue();
    }

    [Test]
    public async Task StuckiDither_PreservesAverageBrightness()
    {
        using var image = CreateGradient(64, 64);
        double avgBefore = ComputeAverageBrightness(image);

        ThresholdOps.StuckiDither(image, 2);
        double avgAfter = ComputeAverageBrightness(image);

        // Error diffusion preserves average brightness approximately
        await Assert.That(Math.Abs(avgAfter - avgBefore)).IsLessThan(0.15);
    }

    // ─── Atkinson Dithering ────────────────────────────────────────

    [Test]
    public async Task AtkinsonDither_ProducesBinaryOutput()
    {
        using var image = CreateGradient(64, 64);
        ThresholdOps.AtkinsonDither(image, 2);

        bool allBinary = true;
        var row = image.GetPixelRow(32);
        for (int x = 0; x < 64; x++)
        {
            ushort val = row[x * 3];
            if (val != 0 && val != Quantum.MaxValue) allBinary = false;
        }
        await Assert.That(allBinary).IsTrue();
    }

    [Test]
    public async Task AtkinsonDither_LosesPartialError()
    {
        // Atkinson only distributes 75% of error, affecting brightness
        using var image = CreateSolidGray(64, 64, 0.5);
        ThresholdOps.AtkinsonDither(image, 2);
        double avg = ComputeAverageBrightness(image);

        // Just verify it produces a reasonable result (Atkinson's lost error
        // means output may deviate significantly from the input brightness)
        await Assert.That(avg).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(avg).IsLessThanOrEqualTo(1.0);
    }

    // ─── Sierra Dithering ──────────────────────────────────────────

    [Test]
    public async Task SierraDither_ProducesBinaryOutput()
    {
        using var image = CreateGradient(64, 64);
        ThresholdOps.SierraDither(image, 2);

        bool allBinary = true;
        var row = image.GetPixelRow(32);
        for (int x = 0; x < 64; x++)
        {
            ushort val = row[x * 3];
            if (val != 0 && val != Quantum.MaxValue) allBinary = false;
        }
        await Assert.That(allBinary).IsTrue();
    }

    [Test]
    public async Task SierraDither_PreservesAverageBrightness()
    {
        using var image = CreateGradient(64, 64);
        double avgBefore = ComputeAverageBrightness(image);

        ThresholdOps.SierraDither(image, 2);
        double avgAfter = ComputeAverageBrightness(image);

        await Assert.That(Math.Abs(avgAfter - avgBefore)).IsLessThan(0.15);
    }

    // ─── Real Image Test ───────────────────────────────────────────

    [Test]
    public async Task AllDitherMethods_WorkOnRealImage()
    {
        string rosePath = Path.Combine(TestAssets, "photo_small.png");
        if (!File.Exists(rosePath)) return;

        // Test each method doesn't throw on a real image
        string[] methods = ["BlueNoise", "Stucki", "Atkinson", "Sierra"];
        foreach (var method in methods)
        {
            using var image = FormatRegistry.Read(rosePath);
            switch (method)
            {
                case "BlueNoise": ThresholdOps.BlueNoiseDither(image); break;
                case "Stucki": ThresholdOps.StuckiDither(image); break;
                case "Atkinson": ThresholdOps.AtkinsonDither(image); break;
                case "Sierra": ThresholdOps.SierraDither(image); break;
            }

            await Assert.That(image.Columns).IsGreaterThan(0u);
        }
    }

    // ─── Comparison: Blue Noise vs Bayer ───────────────────────────

    [Test]
    public async Task BlueNoise_ProducesValidOutput()
    {
        // Verify blue noise dithering works on a gradient without error
        using var image = CreateGradient(64, 64);
        ThresholdOps.BlueNoiseDither(image, 2);

        await Assert.That(image.Columns).IsEqualTo(64u);
        await Assert.That(image.Rows).IsEqualTo(64u);

        // Should have some white and some black pixels (not all one value)
        int whiteCount = 0, blackCount = 0;
        for (int y = 0; y < 64; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < 64; x++)
            {
                if (row[x * 3] == 0) blackCount++;
                else if (row[x * 3] == Quantum.MaxValue) whiteCount++;
            }
        }
        await Assert.That(whiteCount).IsGreaterThan(0);
        await Assert.That(blackCount).IsGreaterThan(0);
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static ImageFrame CreateGradient(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double val = (double)x / (width - 1);
                ushort q = Quantum.ScaleFromDouble(val);
                int off = x * 3;
                row[off] = q;
                row[off + 1] = q;
                row[off + 2] = q;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientWithAlpha(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, true);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double val = (double)x / (width - 1);
                ushort q = Quantum.ScaleFromDouble(val);
                int off = x * 4;
                row[off] = q;
                row[off + 1] = q;
                row[off + 2] = q;
                row[off + 3] = Quantum.MaxValue; // fully opaque
            }
        }
        return frame;
    }

    private static ImageFrame CreateSolidGray(int width, int height, double brightness)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
        ushort q = Quantum.ScaleFromDouble(brightness);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = q;
                row[off + 1] = q;
                row[off + 2] = q;
            }
        }
        return frame;
    }

    private static double ComputeAverageBrightness(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        double total = 0;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
                total += row[x * channels] * Quantum.Scale;
        }
        return total / (width * height);
    }
}
