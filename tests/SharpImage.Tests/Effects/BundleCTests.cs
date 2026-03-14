using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class BundleCTests
{
    private static ImageFrame CreateTestImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                ushort val = (ushort)(x * Quantum.MaxValue / (width - 1));
                row[off] = val;
                row[off + 1] = (ushort)(val / 2);
                row[off + 2] = (ushort)(Quantum.MaxValue - val);
            }
        }
        return frame;
    }

    // ─── Curves Tests ───────────────────────────────────────────────

    [Test]
    public async Task Curves_IdentityCurvePreservesImage()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Curves(image, [(0.0, 0.0), (1.0, 1.0)]);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Center pixel should be nearly identical
        var srcVal = image.GetPixelRow(15)[25 * 3];
        var dstVal = result.GetPixelRow(15)[25 * 3];
        await Assert.That(Math.Abs(srcVal - dstVal)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Curves_BrightensCurveIncreasesValues()
    {
        var image = CreateTestImage(50, 30);
        // S-curve that lifts midtones
        var result = ColorAdjust.Curves(image, [(0.0, 0.0), (0.25, 0.4), (0.75, 0.9), (1.0, 1.0)]);

        // Midtone pixel should be brighter
        var srcMid = image.GetPixelRow(15)[25 * 3];
        var dstMid = result.GetPixelRow(15)[25 * 3];
        await Assert.That(dstMid).IsGreaterThan(srcMid);
    }

    [Test]
    public async Task Curves_ThreePointsCurveProducesSmooth()
    {
        var image = CreateTestImage(100, 10);
        var result = ColorAdjust.Curves(image, [(0.0, 0.0), (0.5, 0.7), (1.0, 1.0)]);

        // Verify no sudden jumps: neighboring pixels should be close
        var row = result.GetPixelRow(5).ToArray();
        bool smooth = true;
        for (int x = 1; x < 99; x++)
        {
            int diff = Math.Abs(row[x * 3] - row[(x - 1) * 3]);
            if (diff > Quantum.MaxValue / 10) { smooth = false; break; }
        }
        await Assert.That(smooth).IsTrue();
    }

    // ─── Dodge Tests ────────────────────────────────────────────────

    [Test]
    public async Task Dodge_LightensImage()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Dodge(image, exposure: 0.5, range: 0.5);

        // Midtone pixel should be lighter
        var srcMid = image.GetPixelRow(15)[25 * 3];
        var dstMid = result.GetPixelRow(15)[25 * 3];
        await Assert.That(dstMid).IsGreaterThanOrEqualTo(srcMid);
    }

    [Test]
    public async Task Dodge_ZeroExposurePreservesImage()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Dodge(image, exposure: 0.0);

        var srcVal = image.GetPixelRow(15)[25 * 3];
        var dstVal = result.GetPixelRow(15)[25 * 3];
        await Assert.That(Math.Abs(srcVal - dstVal)).IsLessThanOrEqualTo(1);
    }

    // ─── Burn Tests ─────────────────────────────────────────────────

    [Test]
    public async Task Burn_DarkensImage()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Burn(image, exposure: 0.5, range: 0.5);

        var srcMid = image.GetPixelRow(15)[25 * 3];
        var dstMid = result.GetPixelRow(15)[25 * 3];
        await Assert.That(dstMid).IsLessThanOrEqualTo(srcMid);
    }

    // ─── Exposure Tests ─────────────────────────────────────────────

    [Test]
    public async Task Exposure_PositiveEVBrightens()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Exposure(image, evStops: 1.0);

        var srcVal = image.GetPixelRow(15)[10 * 3];
        var dstVal = result.GetPixelRow(15)[10 * 3];
        await Assert.That(dstVal).IsGreaterThanOrEqualTo(srcVal);
    }

    [Test]
    public async Task Exposure_NegativeEVDarkens()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Exposure(image, evStops: -1.0);

        var srcVal = image.GetPixelRow(15)[25 * 3];
        var dstVal = result.GetPixelRow(15)[25 * 3];
        await Assert.That(dstVal).IsLessThanOrEqualTo(srcVal);
    }

    [Test]
    public async Task Exposure_ZeroEVPreservesImage()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Exposure(image, evStops: 0.0);

        var srcVal = image.GetPixelRow(15)[25 * 3];
        var dstVal = result.GetPixelRow(15)[25 * 3];
        await Assert.That(srcVal).IsEqualTo(dstVal);
    }

    // ─── Vibrance Tests ─────────────────────────────────────────────

    [Test]
    public async Task Vibrance_PositiveBoostsSaturation()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Vibrance(image, amount: 0.8);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Check a colored pixel has changed
        var srcR = image.GetPixelRow(15)[10 * 3];
        var srcG = image.GetPixelRow(15)[10 * 3 + 1];
        var dstR = result.GetPixelRow(15)[10 * 3];
        var dstG = result.GetPixelRow(15)[10 * 3 + 1];
        // At least one channel should differ
        bool changed = srcR != dstR || srcG != dstG;
        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task Vibrance_ZeroPreservesImage()
    {
        var image = CreateTestImage(50, 30);
        var result = ColorAdjust.Vibrance(image, amount: 0.0);

        // Should be nearly identical (floating point rounding)
        var srcVal = image.GetPixelRow(15)[25 * 3];
        var dstVal = result.GetPixelRow(15)[25 * 3];
        await Assert.That(Math.Abs(srcVal - dstVal)).IsLessThanOrEqualTo(1);
    }

    // ─── Dehaze Tests ───────────────────────────────────────────────

    [Test]
    public async Task Dehaze_ProducesValidOutput()
    {
        var image = CreateTestImage(30, 30);
        var result = ColorAdjust.Dehaze(image, strength: 0.5, patchRadius: 3);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Verify all pixels are within valid range
        bool allValid = true;
        for (int y = 0; y < 30 && allValid; y++)
        {
            var row = result.GetPixelRow(y);
            for (int x = 0; x < 30 && allValid; x++)
            {
                if (row[x * 3] > Quantum.MaxValue) allValid = false;
            }
        }
        await Assert.That(allValid).IsTrue();
    }

    [Test]
    public async Task Dehaze_HighStrengthHasStrongerEffect()
    {
        var image = CreateTestImage(20, 20);
        var mild = ColorAdjust.Dehaze(image, strength: 0.2, patchRadius: 3);
        var strong = ColorAdjust.Dehaze(image, strength: 0.8, patchRadius: 3);

        // Compare center pixels — stronger dehaze should differ more from source
        var srcVal = image.GetPixelRow(10)[10 * 3];
        var mildVal = mild.GetPixelRow(10)[10 * 3];
        var strongVal = strong.GetPixelRow(10)[10 * 3];

        int mildDiff = Math.Abs(srcVal - mildVal);
        int strongDiff = Math.Abs(srcVal - strongVal);
        await Assert.That(strongDiff).IsGreaterThanOrEqualTo(mildDiff);
    }
}
