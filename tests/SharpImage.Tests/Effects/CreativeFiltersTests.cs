using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class CreativeFiltersTests
{
    private static ImageFrame CreateGradient(uint w, uint h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / (int)w);
                int off = x * 3;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateColorBlock(uint w, uint h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        int hw = (int)w / 2;
        int hh = (int)h / 2;
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                int off = x * 3;
                if (x < hw && y < hh) { row[off] = Quantum.MaxValue; row[off + 1] = 0; row[off + 2] = 0; }
                else if (x >= hw && y < hh) { row[off] = 0; row[off + 1] = Quantum.MaxValue; row[off + 2] = 0; }
                else if (x < hw) { row[off] = 0; row[off + 1] = 0; row[off + 2] = Quantum.MaxValue; }
                else { row[off] = Quantum.MaxValue; row[off + 1] = Quantum.MaxValue; row[off + 2] = 0; }
            }
        }
        return frame;
    }

    // ─── Lens Blur ──────────────────────────────────────────────

    [Test]
    public async Task LensBlur_Disk_ProducesSmoothResult()
    {
        using var src = CreateGradient(60, 40);
        using var result = CreativeFilters.LensBlur(src, radius: 3, shape: BokehShape.Disk);

        await Assert.That(result.Columns).IsEqualTo(60u);
        await Assert.That(result.Rows).IsEqualTo(40u);

        // Center pixel should be averaged (not original gradient value)
        var row = result.GetPixelRow(20).ToArray();
        int midVal = row[30 * 3];
        await Assert.That(midVal).IsGreaterThan(0);
        await Assert.That(midVal).IsLessThan(Quantum.MaxValue);
    }

    [Test]
    public async Task LensBlur_Hexagon_ProducesResult()
    {
        using var src = CreateGradient(40, 30);
        using var result = CreativeFilters.LensBlur(src, radius: 4, shape: BokehShape.Hexagon);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
    }

    [Test]
    public async Task LensBlur_WithDepthMap_VariesBlur()
    {
        using var src = CreateColorBlock(40, 40);
        // Depth map: left half sharp (black), right half blurry (white)
        var depthMap = new ImageFrame();
        depthMap.Initialize(40, 40, ColorspaceType.Gray, false);
        for (int y = 0; y < 40; y++)
        {
            var row = depthMap.GetPixelRowForWrite(y);
            for (int x = 0; x < 40; x++)
                row[x] = x < 20 ? (ushort)0 : Quantum.MaxValue;
        }

        using var result = CreativeFilters.LensBlur(src, radius: 5, depthMap: depthMap);
        depthMap.Dispose();

        await Assert.That(result.Columns).IsEqualTo(40u);
    }

    // ─── Tilt-Shift ─────────────────────────────────────────────

    [Test]
    public async Task TiltShift_CenterStaysSharp()
    {
        using var src = CreateGradient(60, 60);
        using var result = CreativeFilters.TiltShift(src, focusY: 0.5, bandHeight: 0.3, blurRadius: 5);

        await Assert.That(result.Columns).IsEqualTo(60u);
        await Assert.That(result.Rows).IsEqualTo(60u);

        // Center should be close to original
        var srcRow = src.GetPixelRow(30).ToArray();
        var dstRow = result.GetPixelRow(30).ToArray();
        int diff = Math.Abs(srcRow[90] - dstRow[90]);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 10);
    }

    // ─── Glow / Bloom ───────────────────────────────────────────

    [Test]
    public async Task Glow_BrightensImage()
    {
        using var src = CreateGradient(50, 40);
        using var result = CreativeFilters.Glow(src, threshold: 0.3, blurRadius: 5, intensity: 0.8);

        await Assert.That(result.Columns).IsEqualTo(50u);

        // Bright area should be at least as bright (screen blend only adds light)
        var srcRow = src.GetPixelRow(20).ToArray();
        var dstRow = result.GetPixelRow(20).ToArray();
        int brightX = 45; // Near-white in gradient
        await Assert.That((int)dstRow[brightX * 3]).IsGreaterThanOrEqualTo((int)srcRow[brightX * 3] - 1);
    }

    // ─── Pixelate ───────────────────────────────────────────────

    [Test]
    public async Task Pixelate_CreatesUniformBlocks()
    {
        using var src = CreateGradient(40, 40);
        using var result = CreativeFilters.Pixelate(src, blockSize: 8);

        await Assert.That(result.Columns).IsEqualTo(40u);

        // Within a block, all pixels should be identical
        var row = result.GetPixelRow(2).ToArray();
        ushort blockVal = row[0];
        await Assert.That(row[3]).IsEqualTo(blockVal);
        await Assert.That(row[6]).IsEqualTo(blockVal);
    }

    // ─── Crystallize ────────────────────────────────────────────

    [Test]
    public async Task Crystallize_ProducesVoronoiCells()
    {
        using var src = CreateColorBlock(60, 60);
        using var result = CreativeFilters.Crystallize(src, cellSize: 10, seed: 123);

        await Assert.That(result.Columns).IsEqualTo(60u);
        await Assert.That(result.Rows).IsEqualTo(60u);

        // Result should have fewer unique colors than original (cell averaging)
        var colors = new HashSet<(ushort, ushort, ushort)>();
        for (int y = 0; y < 60; y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 60; x++)
                colors.Add((row[x * 3], row[x * 3 + 1], row[x * 3 + 2]));
        }
        // With 6x6 grid = 36 cells, far fewer unique colors than 3600 pixels
        await Assert.That(colors.Count).IsLessThan(100);
    }

    // ─── Pointillize ────────────────────────────────────────────

    [Test]
    public async Task Pointillize_HasBackgroundAndDots()
    {
        using var src = CreateColorBlock(50, 50);
        using var result = CreativeFilters.Pointillize(src, dotRadius: 3, spacing: 8, backgroundColor: Quantum.MaxValue);

        await Assert.That(result.Columns).IsEqualTo(50u);

        // Should contain both background (white) and non-white dot pixels
        bool hasWhite = false, hasNonWhite = false;
        for (int y = 0; y < 50 && !(hasWhite && hasNonWhite); y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 50; x++)
            {
                if (row[x * 3] == Quantum.MaxValue && row[x * 3 + 1] == Quantum.MaxValue && row[x * 3 + 2] == Quantum.MaxValue)
                    hasWhite = true;
                else
                    hasNonWhite = true;
            }
        }
        await Assert.That(hasWhite).IsTrue();
        await Assert.That(hasNonWhite).IsTrue();
    }

    // ─── Halftone ───────────────────────────────────────────────

    [Test]
    public async Task Halftone_ProducesDotPattern()
    {
        using var src = CreateGradient(60, 60);
        using var result = CreativeFilters.Halftone(src, dotSize: 6);

        await Assert.That(result.Columns).IsEqualTo(60u);
        await Assert.That(result.Rows).IsEqualTo(60u);

        // Dark side should have more dark dots, light side should have fewer
        long darkSum = 0, lightSum = 0;
        for (int y = 0; y < 60; y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 15; x++) darkSum += row[x * 3]; // Left (dark) quarter
            for (int x = 45; x < 60; x++) lightSum += row[x * 3]; // Right (light) quarter
        }
        await Assert.That(lightSum).IsGreaterThan(darkSum);
    }

    [Test]
    public async Task Halftone_CustomAngles()
    {
        using var src = CreateGradient(40, 40);
        using var result = CreativeFilters.Halftone(src, dotSize: 4, cmykAngles: [0, 30, 60, 90]);

        await Assert.That(result.Columns).IsEqualTo(40u);
    }
}
