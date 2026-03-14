using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

public class BundleDTests
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
                row[off] = (ushort)(x * Quantum.MaxValue / (width - 1));
                row[off + 1] = (ushort)(y * Quantum.MaxValue / (height - 1));
                row[off + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }
        return frame;
    }

    // ─── Chromatic Aberration Fix ────────────────────────────────────

    [Test]
    public async Task ChromaticAberrationFix_ProducesSameDimensions()
    {
        var image = CreateTestImage(40, 30);
        var result = Distort.ChromaticAberrationFix(image, redShift: 0.002, blueShift: -0.002);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
    }

    [Test]
    public async Task ChromaticAberrationFix_GreenChannelPreservedAtCenter()
    {
        // Use a uniform-color image so center pixel is easy to verify
        var frame = new ImageFrame();
        frame.Initialize(41, 31, ColorspaceType.RGB, false);
        ushort testVal = (ushort)(Quantum.MaxValue / 2);
        for (int y = 0; y < 31; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 41; x++)
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = testVal;
        }

        var result = Distort.ChromaticAberrationFix(frame, redShift: 0.01, blueShift: -0.01);

        // Green channel (no shift) at center should be preserved exactly
        var dstVal = result.GetPixelRow(15)[20 * 3 + 1];
        await Assert.That(Math.Abs(dstVal - testVal)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task ChromaticAberrationFix_EdgePixelsDiffer()
    {
        var image = CreateTestImage(40, 30);
        var result = Distort.ChromaticAberrationFix(image, redShift: 0.05, blueShift: -0.05);

        // Edge pixel channels should differ due to per-channel shift
        var srcRow = image.GetPixelRow(0).ToArray();
        var dstRow = result.GetPixelRow(0).ToArray();

        // At least one edge pixel should differ from source
        bool anyDiff = false;
        for (int x = 0; x < 40; x++)
        {
            int off = x * 3;
            if (srcRow[off] != dstRow[off] || srcRow[off + 2] != dstRow[off + 2])
            { anyDiff = true; break; }
        }
        await Assert.That(anyDiff).IsTrue();
    }

    // ─── Vignette Correction ────────────────────────────────────────

    [Test]
    public async Task VignetteCorrection_BrightensEdges()
    {
        // Create uniform mid-gray image
        var frame = new ImageFrame();
        frame.Initialize(40, 40, ColorspaceType.RGB, false);
        ushort midGray = (ushort)(Quantum.MaxValue / 2);
        for (int y = 0; y < 40; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 40; x++)
            {
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = midGray;
            }
        }

        var result = Distort.VignetteCorrection(frame, strength: 1.0, midpoint: 0.3);

        // Center should be roughly the same
        var centerVal = result.GetPixelRow(20)[20 * 3];
        await Assert.That(Math.Abs(centerVal - midGray)).IsLessThan(midGray / 10);

        // Corner should be brighter (corrected upward)
        var cornerVal = result.GetPixelRow(0)[0];
        await Assert.That(cornerVal).IsGreaterThanOrEqualTo(midGray);
    }

    [Test]
    public async Task VignetteCorrection_ZeroStrengthPreservesImage()
    {
        var image = CreateTestImage(20, 20);
        var result = Distort.VignetteCorrection(image, strength: 0.0);

        var srcVal = image.GetPixelRow(10)[10 * 3];
        var dstVal = result.GetPixelRow(10)[10 * 3];
        await Assert.That(srcVal).IsEqualTo(dstVal);
    }

    // ─── Perspective Correction ─────────────────────────────────────

    [Test]
    public async Task PerspectiveCorrection_ZeroTiltPreservesImage()
    {
        var image = CreateTestImage(40, 30);
        var result = Distort.PerspectiveCorrection(image, tiltX: 0, tiltY: 0);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Center should be very close to original
        var srcVal = image.GetPixelRow(15)[20 * 3];
        var dstVal = result.GetPixelRow(15)[20 * 3];
        await Assert.That(Math.Abs(srcVal - dstVal)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task PerspectiveCorrection_TiltXShiftsHorizontally()
    {
        var image = CreateTestImage(40, 30);
        var result = Distort.PerspectiveCorrection(image, tiltX: 5, tiltY: 0);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // The correction should have changed some pixels
        bool anyDiff = false;
        for (int x = 0; x < 40; x++)
        {
            var srcVal = image.GetPixelRow(0)[x * 3];
            var dstVal = result.GetPixelRow(0)[x * 3];
            if (srcVal != dstVal) { anyDiff = true; break; }
        }
        await Assert.That(anyDiff).IsTrue();
    }

    [Test]
    public async Task PerspectiveCorrection_TiltYShiftsVertically()
    {
        var image = CreateTestImage(40, 30);
        var result = Distort.PerspectiveCorrection(image, tiltX: 0, tiltY: 10);

        await Assert.That(result.Columns).IsEqualTo(40u);

        // Check the middle column for differences
        bool anyDiff = false;
        for (int y = 0; y < 30; y++)
        {
            var srcVal = image.GetPixelRow(y)[20 * 3];
            var dstVal = result.GetPixelRow(y)[20 * 3];
            if (srcVal != dstVal) { anyDiff = true; break; }
        }
        await Assert.That(anyDiff).IsTrue();
    }
}
