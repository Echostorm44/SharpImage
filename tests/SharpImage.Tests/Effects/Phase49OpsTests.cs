using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Enhance;
using SharpImage.Image;
using SharpImage.Threshold;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for Phase 49 operations: StripMetadata, SortPixels, UniqueColors, LevelColors,
/// ColorThreshold, RandomThreshold, ModeFilter, Clamp, CycleColormap.
/// </summary>
public class Phase49OpsTests
{
    // ─── StripMetadata ────────────────────────────────────────────

    [Test]
    public async Task StripMetadata_RemovesAllMetadata()
    {
        using var image = CreateGradient(32, 32);
        image.Metadata.Xmp = "<xmp>test</xmp>";

        image.StripMetadata();

        await Assert.That(image.Metadata.Xmp).IsNull();
        await Assert.That(image.Metadata.IptcProfile).IsNull();
        await Assert.That(image.Metadata.ExifProfile).IsNull();
    }

    [Test]
    public async Task StripMetadata_PreservesPixelData()
    {
        using var image = CreateGradient(32, 32);
        var pixelBefore = image.GetPixelRow(0).ToArray();

        image.StripMetadata();

        var pixelAfter = image.GetPixelRow(0).ToArray();
        await Assert.That(pixelAfter[0]).IsEqualTo(pixelBefore[0]);
    }

    // ─── SortPixels ──────────────────────────────────────────────

    [Test]
    public async Task SortPixels_SortsByLuminanceAscending()
    {
        using var source = CreateGradient(64, 1);
        using var sorted = UtilityOps.SortPixels(source);

        await Assert.That((int)sorted.Columns).IsEqualTo(64);
        await Assert.That((int)sorted.Rows).IsEqualTo(1);

        // Already sorted gradient should remain in order
        var row = sorted.GetPixelRow(0).ToArray();
        int ch = sorted.NumberOfChannels;
        for (int x = 1; x < 64; x++)
        {
            ushort prev = row[(x - 1) * ch];
            ushort curr = row[x * ch];
            await Assert.That(curr).IsGreaterThanOrEqualTo(prev);
        }
    }

    [Test]
    public async Task SortPixels_ProducesGradientFromMixedColors()
    {
        // Create an image with descending gradient (bright to dark)
        using var source = new ImageFrame();
        source.Initialize(32, 1, ColorspaceType.SRGB, false);
        int ch = source.NumberOfChannels;
        var row = source.GetPixelRowForWrite(0);
        for (int x = 0; x < 32; x++)
        {
            ushort val = (ushort)(Quantum.MaxValue * (31 - x) / 31);
            int off = x * ch;
            row[off] = val; row[off + 1] = val; row[off + 2] = val;
        }

        using var sorted = UtilityOps.SortPixels(source);
        var sortedRow = sorted.GetPixelRow(0).ToArray();

        // After sorting, should be ascending
        for (int x = 1; x < 32; x++)
        {
            ushort prev = sortedRow[(x - 1) * ch];
            ushort curr = sortedRow[x * ch];
            await Assert.That(curr).IsGreaterThanOrEqualTo(prev);
        }
    }

    // ─── UniqueColors ────────────────────────────────────────────

    [Test]
    public async Task CountUniqueColors_SolidImage_ReturnsOne()
    {
        using var image = CreateSolid(32, 32, Quantum.MaxValue, 0, 0);
        int count = UtilityOps.CountUniqueColors(image);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task CountUniqueColors_Gradient_ReturnsMany()
    {
        using var image = CreateGradient(64, 1);
        int count = UtilityOps.CountUniqueColors(image);
        await Assert.That(count).IsGreaterThan(10);
    }

    [Test]
    public async Task UniqueColorsImage_ProducesSingleRow()
    {
        using var source = CreateSolid(16, 16, 0, Quantum.MaxValue, 0);
        using var palette = UtilityOps.UniqueColorsImage(source);

        await Assert.That((int)palette.Rows).IsEqualTo(1);
        await Assert.That((int)palette.Columns).IsEqualTo(1); // Solid = 1 unique color
    }

    // ─── Clamp ───────────────────────────────────────────────────

    [Test]
    public async Task ClampImage_PreservesValidValues()
    {
        using var image = CreateGradient(32, 32);
        var before = image.GetPixelRow(0).ToArray();

        UtilityOps.ClampImage(image);

        var after = image.GetPixelRow(0).ToArray();
        await Assert.That(after[0]).IsEqualTo(before[0]);
    }

    [Test]
    public async Task ClampImage_DoesNotThrow()
    {
        using var image = CreateGradient(64, 64);
        UtilityOps.ClampImage(image);
        await Assert.That((int)image.Columns).IsEqualTo(64);
    }

    // ─── CycleColormap ──────────────────────────────────────────

    [Test]
    public async Task CycleColormap_ShiftsColors()
    {
        using var image = CreateGradient(32, 1);
        var before = image.GetPixelRow(0).ToArray();

        UtilityOps.CycleColormap(image, 0.25);

        var after = image.GetPixelRow(0).ToArray();
        // At least some pixels should differ
        bool anyDifferent = false;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i]) { anyDifferent = true; break; }
        }
        await Assert.That(anyDifferent).IsTrue();
    }

    [Test]
    public async Task CycleColormap_ZeroShift_NoChange()
    {
        using var image = CreateGradient(32, 1);
        var before = image.GetPixelRow(0).ToArray();

        UtilityOps.CycleColormap(image, 0.0);

        var after = image.GetPixelRow(0).ToArray();
        for (int i = 0; i < before.Length; i++)
            await Assert.That(after[i]).IsEqualTo(before[i]);
    }

    // ─── ColorThreshold ──────────────────────────────────────────

    [Test]
    public async Task ColorThreshold_InRange_BecomesWhite()
    {
        using var image = CreateSolid(16, 16, (ushort)(Quantum.MaxValue / 2), 0, 0);
        // Range: R 0.3..0.7, G 0..1, B 0..1 — mid-red should be in range
        ThresholdOps.ColorThreshold(image, 0.3, 0.0, 0.0, 0.7, 1.0, 1.0);

        var row = image.GetPixelRow(0).ToArray();
        await Assert.That(row[0]).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task ColorThreshold_OutOfRange_BecomesBlack()
    {
        using var image = CreateSolid(16, 16, Quantum.MaxValue, 0, 0);
        // Range: R 0..0.3 — full red should be outside
        ThresholdOps.ColorThreshold(image, 0.0, 0.0, 0.0, 0.3, 1.0, 1.0);

        var row = image.GetPixelRow(0).ToArray();
        await Assert.That(row[0]).IsEqualTo((ushort)0);
    }

    // ─── RandomThreshold ─────────────────────────────────────────

    [Test]
    public async Task RandomThreshold_ProducesBinaryOutput()
    {
        using var image = CreateGradient(64, 64);
        ThresholdOps.RandomThreshold(image, 0.0, 1.0);

        var row = image.GetPixelRow(32).ToArray();
        int ch = image.NumberOfChannels;
        // All values should be either 0 or MaxValue
        for (int x = 0; x < 64; x++)
        {
            ushort val = row[x * ch];
            bool isBinary = val == 0 || val == Quantum.MaxValue;
            await Assert.That(isBinary).IsTrue();
        }
    }

    [Test]
    public async Task RandomThreshold_NarrowRange_MoreWhite()
    {
        // Low=0.8, high=1.0 means random threshold is very high → most pixels become black
        using var image = CreateGradient(64, 64);
        ThresholdOps.RandomThreshold(image, 0.8, 1.0);

        int blackCount = 0;
        var row = image.GetPixelRow(32).ToArray();
        int ch = image.NumberOfChannels;
        for (int x = 0; x < 64; x++)
        {
            if (row[x * ch] == 0) blackCount++;
        }
        // Most pixels should be black because threshold is high
        await Assert.That(blackCount).IsGreaterThan(20);
    }

    // ─── ModeFilter ──────────────────────────────────────────────

    [Test]
    public async Task ModeFilter_PreservesSolidRegions()
    {
        using var source = CreateSolid(32, 32, Quantum.MaxValue, 0, 0);
        using var result = BlurNoiseOps.ModeFilter(source, 1);

        // Solid image should remain unchanged
        var row = result.GetPixelRow(16).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That(row[16 * ch]).IsEqualTo(Quantum.MaxValue);
        await Assert.That(row[16 * ch + 1]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task ModeFilter_ReducesImpulseNoise()
    {
        using var source = CreateSolid(32, 32, Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue);
        // Add a single black pixel impulse
        var centerRow = source.GetPixelRowForWrite(16);
        int ch = source.NumberOfChannels;
        centerRow[16 * ch] = 0;
        centerRow[16 * ch + 1] = 0;
        centerRow[16 * ch + 2] = 0;

        using var result = BlurNoiseOps.ModeFilter(source, 1);

        // The mode of a 3x3 neighborhood around the impulse is white (8 white, 1 black)
        var resultRow = result.GetPixelRow(16).ToArray();
        await Assert.That(resultRow[16 * ch]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── LevelColors ─────────────────────────────────────────────

    [Test]
    public async Task LevelColors_BlackToRed_WhiteToBlue()
    {
        using var source = CreateGradient(32, 1);
        using var result = EnhanceOps.LevelColors(source, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0);

        var row = result.GetPixelRow(0).ToArray();
        int ch = result.NumberOfChannels;

        // First pixel (was black=0) should map to black color (R=1,G=0,B=0)
        await Assert.That(row[0]).IsEqualTo(Quantum.MaxValue); // Red channel = 1.0 * max
        await Assert.That(row[1]).IsEqualTo((ushort)0);         // Green = 0

        // Last pixel (was white=max) should map to white color (R=0,G=0,B=1)
        int lastOff = 31 * ch;
        await Assert.That(row[lastOff]).IsEqualTo((ushort)0);          // Red = 0
        await Assert.That(row[lastOff + 2]).IsEqualTo(Quantum.MaxValue); // Blue = 1.0 * max
    }

    [Test]
    public async Task LevelColors_Identity_PreservesImage()
    {
        using var source = CreateGradient(32, 1);
        var before = source.GetPixelRow(0).ToArray();

        using var result = EnhanceOps.LevelColors(source, 0, 0, 0, 1, 1, 1);

        var after = result.GetPixelRow(0).ToArray();
        for (int i = 0; i < before.Length; i++)
            await Assert.That(after[i]).IsEqualTo(before[i]);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateSolid(int width, int height, ushort r, ushort g, ushort b)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * ch;
                row[off] = r; row[off + 1] = g; row[off + 2] = b;
            }
        }
        return image;
    }

    private static ImageFrame CreateGradient(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (ushort)(Quantum.MaxValue * x / Math.Max(width - 1, 1));
                int off = x * ch;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return image;
    }
}
