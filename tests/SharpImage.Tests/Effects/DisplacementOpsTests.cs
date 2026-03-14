using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class DisplacementOpsTests
{
    private static ImageFrame CreateCheckerboard(uint w, uint h, int blockSize = 8)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                bool white = ((x / blockSize) + (y / blockSize)) % 2 == 0;
                ushort val = white ? Quantum.MaxValue : (ushort)0;
                int off = x * 3;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGrayValue(uint w, uint h, ushort value)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.Gray, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
                row[x] = value;
        }
        return frame;
    }

    private static ImageFrame CreateHeightRamp(uint w, uint h)
    {
        // Left=low, right=high
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.Gray, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
                row[x] = (ushort)(x * Quantum.MaxValue / (int)w);
        }
        return frame;
    }

    // ─── Displacement Map Tests ─────────────────────────────────

    [Test]
    public async Task DisplacementMap_MidGrayNoChange()
    {
        using var src = CreateCheckerboard(40, 40);
        using var map = CreateGrayValue(40, 40, (ushort)(Quantum.MaxValue / 2));
        using var result = DisplacementOps.DisplacementMap(src, map, scaleX: 10, scaleY: 10);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(40u);

        // Mid-gray = no displacement, pixels should be unchanged
        var srcRow = src.GetPixelRow(20).ToArray();
        var dstRow = result.GetPixelRow(20).ToArray();
        int diff = Math.Abs(srcRow[60] - dstRow[60]);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 20);
    }

    [Test]
    public async Task DisplacementMap_WhiteShiftsRight()
    {
        using var src = CreateCheckerboard(40, 40);
        // White displacement map = max positive shift
        using var map = CreateGrayValue(40, 40, Quantum.MaxValue);
        using var result = DisplacementOps.DisplacementMap(src, map, scaleX: 5, scaleY: 0);

        // Result should be shifted compared to source
        var srcRow = src.GetPixelRow(20).ToArray();
        var dstRow = result.GetPixelRow(20).ToArray();
        bool anyDifferent = false;
        for (int x = 5; x < 35; x++)
        {
            if (srcRow[x * 3] != dstRow[x * 3]) { anyDifferent = true; break; }
        }
        await Assert.That(anyDifferent).IsTrue();
    }

    // ─── Normal Map Tests ───────────────────────────────────────

    [Test]
    public async Task NormalMap_FlatSurfaceIsPurple()
    {
        // Flat (uniform) height = normal pointing straight up = (0.5, 0.5, 1.0) in normalized
        using var flat = CreateGrayValue(40, 40, (ushort)(Quantum.MaxValue / 2));
        using var normals = DisplacementOps.NormalMapFromHeight(flat, strength: 1.0);

        await Assert.That(normals.Columns).IsEqualTo(40u);
        await Assert.That(normals.NumberOfChannels).IsEqualTo(3);

        // Center pixel should be ~(32767, 32767, 65535) = flat normal
        var row = normals.GetPixelRow(20).ToArray();
        int midR = row[60], midG = row[61], midB = row[62];
        int half = Quantum.MaxValue / 2;

        await Assert.That(Math.Abs(midR - half)).IsLessThan(Quantum.MaxValue / 10);
        await Assert.That(Math.Abs(midG - half)).IsLessThan(Quantum.MaxValue / 10);
        await Assert.That(midB).IsGreaterThan(half);
    }

    [Test]
    public async Task NormalMap_RampHasHorizontalGradient()
    {
        using var ramp = CreateHeightRamp(60, 40);
        using var normals = DisplacementOps.NormalMapFromHeight(ramp, strength: 2.0);

        // Ramp increases left-to-right, so normals should have consistent X tilt
        var row = normals.GetPixelRow(20).ToArray();
        int leftR = row[30]; // x=10
        int midR = row[90]; // x=30
        // Both should show similar X-direction normal (consistent gradient)
        await Assert.That(Math.Abs(leftR - midR)).IsLessThan(Quantum.MaxValue / 5);
    }

    [Test]
    public async Task NormalMap_StrengthAffectsIntensity()
    {
        using var ramp = CreateHeightRamp(40, 40);
        using var weak = DisplacementOps.NormalMapFromHeight(ramp, strength: 0.5);
        using var strong = DisplacementOps.NormalMapFromHeight(ramp, strength: 5.0);

        // Stronger normal map should have more deviation from mid-blue
        var weakRow = weak.GetPixelRow(20).ToArray();
        var strongRow = strong.GetPixelRow(20).ToArray();
        int half = Quantum.MaxValue / 2;

        int weakDevR = Math.Abs(weakRow[60] - half);
        int strongDevR = Math.Abs(strongRow[60] - half);
        await Assert.That(strongDevR).IsGreaterThanOrEqualTo(weakDevR);
    }

    // ─── Spherize Tests ─────────────────────────────────────────

    [Test]
    public async Task Spherize_PreservesDimensions()
    {
        using var src = CreateCheckerboard(50, 50);
        using var result = DisplacementOps.Spherize(src, amount: 0.7);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(50u);
    }

    [Test]
    public async Task Spherize_CenterPixelSurvives()
    {
        using var src = CreateCheckerboard(40, 40);
        using var result = DisplacementOps.Spherize(src, amount: 0.5);

        // Center pixel should still be present (minimal distortion at center)
        var srcRow = src.GetPixelRow(20).ToArray();
        var dstRow = result.GetPixelRow(20).ToArray();

        // At least close to original
        int diff = Math.Abs(srcRow[60] - dstRow[60]);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task Spherize_ZeroAmountNoChange()
    {
        using var src = CreateCheckerboard(30, 30);
        using var result = DisplacementOps.Spherize(src, amount: 0.0);

        // Zero amount should be identity
        var srcRow = src.GetPixelRow(15).ToArray();
        var dstRow = result.GetPixelRow(15).ToArray();
        int diff = Math.Abs(srcRow[45] - dstRow[45]);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 20);
    }
}
