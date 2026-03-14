using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class SelectionTests
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
                // Left half red, right half blue
                if (x < width / 2)
                {
                    row[off] = Quantum.MaxValue;
                    row[off + 1] = 0;
                    row[off + 2] = 0;
                }
                else
                {
                    row[off] = 0;
                    row[off + 1] = 0;
                    row[off + 2] = Quantum.MaxValue;
                }
            }
        }
        return frame;
    }

    private static ImageFrame CreateGrayscaleMask(int width, int height, Func<int, int, ushort> valueFunc)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
                row[x] = valueFunc(x, y);
        }
        return frame;
    }

    // ─── GrabCut Tests ──────────────────────────────────────────────

    [Test]
    public async Task GrabCut_SeparatesForegroundFromBackground()
    {
        var image = CreateTestImage(40, 30);
        // Trimap: left quarter = definite BG, right quarter = definite FG, middle = unknown
        var trimap = CreateGrayscaleMask(40, 30, (x, y) =>
        {
            if (x < 10) return (ushort)0; // definite background
            if (x >= 30) return Quantum.MaxValue; // definite foreground
            return (ushort)(Quantum.MaxValue / 2); // unknown
        });

        var result = SelectionOps.GrabCut(image, trimap, iterations: 3);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Left edge should be background (black)
        var leftPixel = result.GetPixelRow(15)[2];
        await Assert.That(leftPixel).IsEqualTo((ushort)0);

        // Right edge should be foreground (white)
        var rightPixel = result.GetPixelRow(15)[37];
        await Assert.That(rightPixel).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task GrabCut_ReturnsGrayscaleMask()
    {
        var image = CreateTestImage(20, 20);
        var trimap = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 5 ? (ushort)0 : x >= 15 ? Quantum.MaxValue : (ushort)(Quantum.MaxValue / 2));

        var result = SelectionOps.GrabCut(image, trimap, iterations: 2);

        await Assert.That(result.Colorspace).IsEqualTo(ColorspaceType.Gray);
        await Assert.That(result.HasAlpha).IsFalse();
    }

    // ─── Alpha Matting Tests ────────────────────────────────────────

    [Test]
    public async Task AlphaMatting_ProducesSoftMatte()
    {
        var image = CreateTestImage(40, 30);
        var trimap = CreateGrayscaleMask(40, 30, (x, y) =>
        {
            if (x < 10) return (ushort)0;
            if (x >= 30) return Quantum.MaxValue;
            return (ushort)(Quantum.MaxValue / 2);
        });

        var result = SelectionOps.AlphaMatting(image, trimap, kNeighbors: 5);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
        await Assert.That(result.Colorspace).IsEqualTo(ColorspaceType.Gray);

        // Known BG region should have low alpha
        var bgPixel = result.GetPixelRow(15)[2];
        await Assert.That(bgPixel).IsLessThan((ushort)(Quantum.MaxValue / 4));

        // Known FG region should have high alpha
        var fgPixel = result.GetPixelRow(15)[37];
        await Assert.That(fgPixel).IsGreaterThan((ushort)(Quantum.MaxValue * 3 / 4));
    }

    // ─── Flood Select Tests ─────────────────────────────────────────

    [Test]
    public async Task FloodSelect_SelectsContiguousRegion()
    {
        var image = CreateTestImage(40, 30);
        // Seed in left-half red region
        var result = SelectionOps.FloodSelect(image, 5, 15, tolerancePercent: 5.0);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Selected pixel in red region should be white
        var selectedPixel = result.GetPixelRow(15)[5];
        await Assert.That(selectedPixel).IsEqualTo(Quantum.MaxValue);

        // Blue region should NOT be selected
        var unselectedPixel = result.GetPixelRow(15)[25];
        await Assert.That(unselectedPixel).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task FloodSelect_EightConnectedFindsMorePixels()
    {
        // Create a small image with diagonal color connection
        var frame = new ImageFrame();
        frame.Initialize(5, 5, ColorspaceType.RGB, false);
        // Fill all black
        for (int y = 0; y < 5; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 5; x++)
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = 0;
        }
        // White diagonal: (0,0), (1,1), (2,2)
        for (int i = 0; i < 3; i++)
        {
            var row = frame.GetPixelRowForWrite(i);
            row[i * 3] = row[i * 3 + 1] = row[i * 3 + 2] = Quantum.MaxValue;
        }

        var result4 = SelectionOps.FloodSelect(frame, 0, 0, tolerancePercent: 5.0, eightConnected: false);
        var result8 = SelectionOps.FloodSelect(frame, 0, 0, tolerancePercent: 5.0, eightConnected: true);

        // 4-connected should only get (0,0)
        var pix4_11 = result4.GetPixelRow(1)[1];
        await Assert.That(pix4_11).IsEqualTo((ushort)0);

        // 8-connected should also get (1,1)
        var pix8_11 = result8.GetPixelRow(1)[1];
        await Assert.That(pix8_11).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task FloodSelect_ToleranceControlsSpread()
    {
        // Create gradient image
        var frame = new ImageFrame();
        frame.Initialize(100, 10, ColorspaceType.Gray, false);
        for (int y = 0; y < 10; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 100; x++)
                row[x] = (ushort)(x * Quantum.MaxValue / 99);
        }

        var tightResult = SelectionOps.FloodSelect(frame, 0, 5, tolerancePercent: 1.0);
        var wideResult = SelectionOps.FloodSelect(frame, 0, 5, tolerancePercent: 50.0);

        // Count selected pixels in each
        int tightCount = 0, wideCount = 0;
        for (int x = 0; x < 100; x++)
        {
            if (tightResult.GetPixelRow(5)[x] > 0) tightCount++;
            if (wideResult.GetPixelRow(5)[x] > 0) wideCount++;
        }

        await Assert.That(wideCount).IsGreaterThan(tightCount);
    }

    // ─── Color Range Select Tests ───────────────────────────────────

    [Test]
    public async Task ColorRangeSelect_FindsMatchingPixelsAnywhere()
    {
        var image = CreateTestImage(40, 30);
        // Select red pixels
        var result = SelectionOps.ColorRangeSelect(image, Quantum.MaxValue, 0, 0, tolerancePercent: 5.0);

        // Left half should be selected
        var leftPixel = result.GetPixelRow(15)[5];
        await Assert.That(leftPixel).IsEqualTo(Quantum.MaxValue);

        // Right half should not
        var rightPixel = result.GetPixelRow(15)[35];
        await Assert.That(rightPixel).IsEqualTo((ushort)0);
    }

    // ─── Feather Tests ──────────────────────────────────────────────

    [Test]
    public async Task FeatherMask_SoftensEdges()
    {
        // Create hard-edged mask: left half white, right half black
        var mask = CreateGrayscaleMask(40, 30, (x, y) =>
            x < 20 ? Quantum.MaxValue : (ushort)0);

        var feathered = SelectionOps.FeatherMask(mask, radius: 5.0);

        await Assert.That(feathered.Columns).IsEqualTo(40u);
        await Assert.That(feathered.Rows).IsEqualTo(30u);

        // Pixel well inside white region should still be bright
        var insideWhite = feathered.GetPixelRow(15)[5];
        await Assert.That(insideWhite).IsGreaterThan((ushort)(Quantum.MaxValue * 0.8));

        // Pixel at the edge should be intermediate
        var edgePixel = feathered.GetPixelRow(15)[20];
        await Assert.That(edgePixel).IsGreaterThan((ushort)0);
        await Assert.That(edgePixel).IsLessThan(Quantum.MaxValue);
    }

    [Test]
    public async Task FeatherMask_ZeroRadiusReturnsUnchanged()
    {
        var mask = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);

        var result = SelectionOps.FeatherMask(mask, radius: 0.0);

        var leftPixel = result.GetPixelRow(10)[5];
        var rightPixel = result.GetPixelRow(10)[15];
        await Assert.That(leftPixel).IsEqualTo(Quantum.MaxValue);
        await Assert.That(rightPixel).IsEqualTo((ushort)0);
    }

    // ─── Mask Operation Tests ───────────────────────────────────────

    [Test]
    public async Task InvertMask_SwapsBlackAndWhite()
    {
        var mask = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);

        var inverted = SelectionOps.InvertMask(mask);

        var leftPixel = inverted.GetPixelRow(10)[5];
        var rightPixel = inverted.GetPixelRow(10)[15];
        await Assert.That(leftPixel).IsEqualTo((ushort)0);
        await Assert.That(rightPixel).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task UnionMasks_TakesMaximum()
    {
        var maskA = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);
        var maskB = CreateGrayscaleMask(20, 20, (x, y) =>
            y < 10 ? Quantum.MaxValue : (ushort)0);

        var result = SelectionOps.UnionMasks(maskA, maskB);

        // Top-left: both white → white
        await Assert.That(result.GetPixelRow(5)[5]).IsEqualTo(Quantum.MaxValue);
        // Top-right: B=white, A=black → white
        await Assert.That(result.GetPixelRow(5)[15]).IsEqualTo(Quantum.MaxValue);
        // Bottom-left: A=white, B=black → white
        await Assert.That(result.GetPixelRow(15)[5]).IsEqualTo(Quantum.MaxValue);
        // Bottom-right: both black → black
        await Assert.That(result.GetPixelRow(15)[15]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task IntersectMasks_TakesMinimum()
    {
        var maskA = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);
        var maskB = CreateGrayscaleMask(20, 20, (x, y) =>
            y < 10 ? Quantum.MaxValue : (ushort)0);

        var result = SelectionOps.IntersectMasks(maskA, maskB);

        // Only top-left is white (both masks white)
        await Assert.That(result.GetPixelRow(5)[5]).IsEqualTo(Quantum.MaxValue);
        await Assert.That(result.GetPixelRow(5)[15]).IsEqualTo((ushort)0);
        await Assert.That(result.GetPixelRow(15)[5]).IsEqualTo((ushort)0);
        await Assert.That(result.GetPixelRow(15)[15]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task SubtractMasks_RemovesOverlap()
    {
        var maskA = CreateGrayscaleMask(20, 20, (x, y) => Quantum.MaxValue); // all white
        var maskB = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);

        var result = SelectionOps.SubtractMasks(maskA, maskB);

        // Left: A(white) - B(white) = black
        await Assert.That(result.GetPixelRow(10)[5]).IsEqualTo((ushort)0);
        // Right: A(white) - B(black) = white
        await Assert.That(result.GetPixelRow(10)[15]).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task XorMasks_SelectsNonOverlapping()
    {
        var maskA = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);
        var maskB = CreateGrayscaleMask(20, 20, (x, y) =>
            y < 10 ? Quantum.MaxValue : (ushort)0);

        var result = SelectionOps.XorMasks(maskA, maskB);

        // Top-left: both white → XOR = 0
        var topLeft = result.GetPixelRow(5)[5];
        await Assert.That(topLeft).IsLessThan((ushort)(Quantum.MaxValue / 4));

        // Top-right: A=0, B=1 → XOR = 1
        var topRight = result.GetPixelRow(5)[15];
        await Assert.That(topRight).IsGreaterThan((ushort)(Quantum.MaxValue * 3 / 4));

        // Bottom-left: A=1, B=0 → XOR = 1
        var bottomLeft = result.GetPixelRow(15)[5];
        await Assert.That(bottomLeft).IsGreaterThan((ushort)(Quantum.MaxValue * 3 / 4));

        // Bottom-right: both 0 → XOR = 0
        var bottomRight = result.GetPixelRow(15)[15];
        await Assert.That(bottomRight).IsLessThan((ushort)(Quantum.MaxValue / 4));
    }

    // ─── Expand/Contract Tests ──────────────────────────────────────

    [Test]
    public async Task ExpandMask_GrowsWhiteRegion()
    {
        // Small white dot in center
        var mask = CreateGrayscaleMask(20, 20, (x, y) =>
            x == 10 && y == 10 ? Quantum.MaxValue : (ushort)0);

        var expanded = SelectionOps.ExpandMask(mask, radius: 2);

        // Center should still be white
        await Assert.That(expanded.GetPixelRow(10)[10]).IsEqualTo(Quantum.MaxValue);
        // Adjacent pixel should now be white
        await Assert.That(expanded.GetPixelRow(10)[11]).IsEqualTo(Quantum.MaxValue);
        // Far pixel should still be black
        await Assert.That(expanded.GetPixelRow(10)[15]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task ContractMask_ShrinksWhiteRegion()
    {
        // White block in center 10x10
        var mask = CreateGrayscaleMask(20, 20, (x, y) =>
            x >= 5 && x < 15 && y >= 5 && y < 15 ? Quantum.MaxValue : (ushort)0);

        var contracted = SelectionOps.ContractMask(mask, radius: 2);

        // Edge of original block should now be black
        await Assert.That(contracted.GetPixelRow(5)[5]).IsEqualTo((ushort)0);
        // Interior should still be white
        await Assert.That(contracted.GetPixelRow(10)[10]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── Apply Mask Test ────────────────────────────────────────────

    [Test]
    public async Task ApplyMask_SetsAlphaFromMask()
    {
        var image = CreateTestImage(20, 20);
        var mask = CreateGrayscaleMask(20, 20, (x, y) =>
            x < 10 ? Quantum.MaxValue : (ushort)0);

        var result = SelectionOps.ApplyMask(image, mask);

        await Assert.That(result.HasAlpha).IsTrue();

        // Left pixel should have full alpha
        var leftRow = result.GetPixelRow(10).ToArray();
        int ch = result.NumberOfChannels;
        var leftAlpha = leftRow[5 * ch + ch - 1];
        var rightAlpha = leftRow[15 * ch + ch - 1];
        await Assert.That(leftAlpha).IsEqualTo(Quantum.MaxValue);

        // Right pixel should have zero alpha
        await Assert.That(rightAlpha).IsEqualTo((ushort)0);
    }
}
