using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class AccessibilityOpsTests
{
    private static ImageFrame CreateRedGreen(uint w, uint h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        int hw = (int)w / 2;
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                int off = x * 3;
                if (x < hw)
                { row[off] = Quantum.MaxValue; row[off + 1] = 0; row[off + 2] = 0; }
                else
                { row[off] = 0; row[off + 1] = Quantum.MaxValue; row[off + 2] = 0; }
            }
        }
        return frame;
    }

    private static ImageFrame CreateVibrant(uint w, uint h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                int off = x * 3;
                row[off] = (ushort)(x * Quantum.MaxValue / (int)w);
                row[off + 1] = (ushort)(y * Quantum.MaxValue / (int)h);
                row[off + 2] = Quantum.MaxValue;
            }
        }
        return frame;
    }

    // ─── Color Blindness Simulation ─────────────────────────────

    [Test]
    public async Task SimulateProtanopia_ReducesRedGreenDifference()
    {
        using var src = CreateRedGreen(40, 40);
        using var result = AccessibilityOps.SimulateColorBlindness(src, ColorBlindnessType.Protanopia);

        await Assert.That(result.Columns).IsEqualTo(40u);

        // For protanopia, red and green should look more similar
        var row = result.GetPixelRow(20).ToArray();
        int leftR = row[5 * 3], leftG = row[5 * 3 + 1];
        int rightR = row[25 * 3], rightG = row[25 * 3 + 1];

        // The R/G difference should be reduced compared to original
        int origDiff = Quantum.MaxValue; // Pure red vs pure green
        int simDiff = Math.Abs((leftR - leftG) - (rightR - rightG));
        await Assert.That(simDiff).IsLessThan(origDiff);
    }

    [Test]
    public async Task SimulateDeuteranopia_ProducesResult()
    {
        using var src = CreateRedGreen(40, 40);
        using var result = AccessibilityOps.SimulateColorBlindness(src, ColorBlindnessType.Deuteranopia);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(40u);
    }

    [Test]
    public async Task SimulateTritanopia_AffectsBlueChannel()
    {
        using var src = CreateVibrant(40, 40);
        using var result = AccessibilityOps.SimulateColorBlindness(src, ColorBlindnessType.Tritanopia);

        // Blue channel should be changed by tritanopia
        var srcRow = src.GetPixelRow(20).ToArray();
        var dstRow = result.GetPixelRow(20).ToArray();
        bool blueChanged = false;
        for (int x = 5; x < 35; x++)
        {
            if (Math.Abs(srcRow[x * 3 + 2] - dstRow[x * 3 + 2]) > Quantum.MaxValue / 20)
            { blueChanged = true; break; }
        }
        await Assert.That(blueChanged).IsTrue();
    }

    // ─── Daltonize ──────────────────────────────────────────────

    [Test]
    public async Task Daltonize_IncreasesDistinguishability()
    {
        using var src = CreateRedGreen(40, 40);
        using var daltonized = AccessibilityOps.Daltonize(src, ColorBlindnessType.Protanopia, strength: 1.0);

        await Assert.That(daltonized.Columns).IsEqualTo(40u);

        // Daltonized version should differ from original (compensation applied)
        var srcRow2 = src.GetPixelRow(20).ToArray();
        var daltRow2 = daltonized.GetPixelRow(20).ToArray();
        bool changed = false;
        for (int x = 0; x < 40; x++)
        {
            if (Math.Abs(srcRow2[x * 3] - daltRow2[x * 3]) > Quantum.MaxValue / 50 ||
                Math.Abs(srcRow2[x * 3 + 1] - daltRow2[x * 3 + 1]) > Quantum.MaxValue / 50 ||
                Math.Abs(srcRow2[x * 3 + 2] - daltRow2[x * 3 + 2]) > Quantum.MaxValue / 50)
            { changed = true; break; }
        }
        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task Daltonize_ZeroStrengthIsOriginal()
    {
        using var src = CreateRedGreen(30, 30);
        using var result = AccessibilityOps.Daltonize(src, ColorBlindnessType.Protanopia, strength: 0.0);

        var srcRow = src.GetPixelRow(15).ToArray();
        var dstRow = result.GetPixelRow(15).ToArray();
        int diff = Math.Abs(srcRow[45] - dstRow[45]);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 50);
    }

    // ─── Soft Proof ─────────────────────────────────────────────

    [Test]
    public async Task SoftProof_LowInkLimitChangesOutput()
    {
        // Create a dark saturated image (high CMYK ink coverage)
        var src = new ImageFrame();
        src.Initialize(40, 40, ColorspaceType.RGB, false);
        for (int y = 0; y < 40; y++)
        {
            var row = src.GetPixelRowForWrite(y);
            for (int x = 0; x < 40; x++)
            {
                int off = x * 3;
                // Dark blue-green: high C+M+K
                row[off] = (ushort)(Quantum.MaxValue / 10);     // R = 10%
                row[off + 1] = (ushort)(Quantum.MaxValue / 5);  // G = 20%
                row[off + 2] = (ushort)(Quantum.MaxValue / 4);  // B = 25%
            }
        }

        using var result = AccessibilityOps.SoftProof(src, inkLimit: 1.5, blackPoint: 1.0);
        using var highInk = AccessibilityOps.SoftProof(src, inkLimit: 4.0, blackPoint: 1.0);
        src.Dispose();

        await Assert.That(result.Columns).IsEqualTo(40u);

        // With low ink limit, dark saturated pixels should be lightened
        var hiRow = highInk.GetPixelRow(20).ToArray();
        var loRow = result.GetPixelRow(20).ToArray();

        bool anyDiff = false;
        for (int x = 0; x < 40; x++)
        {
            if (Math.Abs(hiRow[x * 3] - loRow[x * 3]) > 1 ||
                Math.Abs(hiRow[x * 3 + 1] - loRow[x * 3 + 1]) > 1 ||
                Math.Abs(hiRow[x * 3 + 2] - loRow[x * 3 + 2]) > 1)
            { anyDiff = true; break; }
        }
        await Assert.That(anyDiff).IsTrue();
    }

    [Test]
    public async Task SoftProof_LowInkLimit_DesaturatesMore()
    {
        using var src = CreateVibrant(30, 30);
        using var highInk = AccessibilityOps.SoftProof(src, inkLimit: 4.0, blackPoint: 1.0);
        using var lowInk = AccessibilityOps.SoftProof(src, inkLimit: 2.0, blackPoint: 1.0);

        // Lower ink limit should produce more desaturated (closer to gray) result
        var hiRow = highInk.GetPixelRow(15).ToArray();
        var loRow = lowInk.GetPixelRow(15).ToArray();

        // Measure saturation: variance of RGB channels
        long hiVar = 0, loVar = 0;
        for (int x = 0; x < 30; x++)
        {
            int hR = hiRow[x * 3], hG = hiRow[x * 3 + 1], hB = hiRow[x * 3 + 2];
            int lR = loRow[x * 3], lG = loRow[x * 3 + 1], lB = loRow[x * 3 + 2];
            int hMean = (hR + hG + hB) / 3;
            int lMean = (lR + lG + lB) / 3;
            hiVar += (hR - hMean) * (hR - hMean) + (hG - hMean) * (hG - hMean) + (hB - hMean) * (hB - hMean);
            loVar += (lR - lMean) * (lR - lMean) + (lG - lMean) * (lG - lMean) + (lB - lMean) * (lB - lMean);
        }
        await Assert.That(loVar).IsLessThanOrEqualTo(hiVar);
    }
}
