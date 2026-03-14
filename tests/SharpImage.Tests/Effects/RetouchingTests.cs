using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for Phase 28 — Bundle A: Retouching &amp; Inpainting.
/// InpaintTelea, InpaintNavierStokes, PatchMatch, CloneStamp, HealingBrush, RedEyeRemoval.
/// </summary>
public class RetouchingTests
{
    // ═══════════════════════════════════════════════════════════════
    // TELEA INPAINTING TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task InpaintTelea_FillsSmallGap()
    {
        // Create image with a solid block and mask a few center pixels
        var src = CreateSolid(20, 20, 40000, 40000, 40000);
        var maskFrame = CreateMask(20, 20, 8, 8, 4, 4);

        var result = RetouchingOps.InpaintTelea(src, maskFrame, 5);

        await Assert.That(result.Columns).IsEqualTo(20u);
        await Assert.That(result.Rows).IsEqualTo(20u);

        // Filled pixels should be close to surrounding values (~40000)
        var row = result.GetPixelRow(10).ToArray();
        int ch = result.NumberOfChannels;
        // Pixel at center of masked region should be approximately 40000
        await Assert.That((int)row[10 * ch]).IsGreaterThan(30000);
        await Assert.That((int)row[10 * ch]).IsLessThan(50000);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    [Test]
    public async Task InpaintTelea_UnmaskedPixelsUnchanged()
    {
        var src = CreateGradient(20, 20);
        var maskFrame = CreateMask(20, 20, 5, 5, 2, 2);

        var result = RetouchingOps.InpaintTelea(src, maskFrame, 5);

        // Pixel outside mask should match source exactly
        var srcRow = src.GetPixelRow(0).ToArray();
        var resRow = result.GetPixelRow(0).ToArray();
        await Assert.That(resRow[0]).IsEqualTo(srcRow[0]);
        await Assert.That(resRow[1]).IsEqualTo(srcRow[1]);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    [Test]
    public async Task InpaintTelea_EmptyMask_ReturnsIdentical()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0);
        var maskFrame = CreateSolid(10, 10, 0, 0, 0); // No mask

        var result = RetouchingOps.InpaintTelea(src, maskFrame, 5);

        var srcRow = src.GetPixelRow(5).ToArray();
        var resRow = result.GetPixelRow(5).ToArray();
        await Assert.That(resRow[5 * 3]).IsEqualTo(srcRow[5 * 3]);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // NAVIER-STOKES INPAINTING TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task InpaintNavierStokes_FillsGapSmoothly()
    {
        var src = CreateSolid(20, 20, 50000, 50000, 50000);
        var maskFrame = CreateMask(20, 20, 8, 8, 4, 4);

        var result = RetouchingOps.InpaintNavierStokes(src, maskFrame, 5, 10);

        // Filled region should converge toward surrounding uniform value
        var row = result.GetPixelRow(10).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That((int)row[10 * ch]).IsGreaterThan(40000);
        await Assert.That((int)row[10 * ch]).IsLessThan(60000);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    [Test]
    public async Task InpaintNavierStokes_PreservesUnmasked()
    {
        var src = CreateGradient(20, 20);
        var maskFrame = CreateMask(20, 20, 10, 10, 3, 3);

        var result = RetouchingOps.InpaintNavierStokes(src, maskFrame, 5, 5);

        // Corner pixel should be unmodified
        var srcRow = src.GetPixelRow(0).ToArray();
        var resRow = result.GetPixelRow(0).ToArray();
        await Assert.That(resRow[0]).IsEqualTo(srcRow[0]);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // PATCHMATCH TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task PatchMatch_FillsMaskedRegion()
    {
        var src = CreateSolid(30, 30, 40000, 20000, 50000);
        var maskFrame = CreateMask(30, 30, 12, 12, 6, 6);

        var result = RetouchingOps.PatchMatch(src, maskFrame, 7, 3);

        await Assert.That(result.Columns).IsEqualTo(30u);
        // Filled pixels should be similar to surrounding
        var row = result.GetPixelRow(15).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That((int)row[15 * ch]).IsGreaterThan(20000);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    [Test]
    public async Task PatchMatch_PreservesDimensions()
    {
        var src = CreateSolid(25, 25, 32768, 32768, 32768);
        var maskFrame = CreateMask(25, 25, 10, 10, 5, 5);

        var result = RetouchingOps.PatchMatch(src, maskFrame, 7, 2);

        await Assert.That(result.Columns).IsEqualTo(25u);
        await Assert.That(result.Rows).IsEqualTo(25u);

        src.Dispose(); maskFrame.Dispose(); result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // CLONE STAMP TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task CloneStamp_CopiesRegion()
    {
        // Left half = red, right half = blue
        var src = CreateLeftRightSplit(20, 20, 65535, 0, 0, 0, 0, 65535);

        // Clone from red region to blue region
        var result = RetouchingOps.CloneStamp(src, 0, 5, 12, 5, 5, 5);

        // Target region should now be red
        var row = result.GetPixelRow(7).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That(row[13 * ch]).IsEqualTo((ushort)65535); // R
        await Assert.That(row[13 * ch + 2]).IsEqualTo((ushort)0); // B

        src.Dispose(); result.Dispose();
    }

    [Test]
    public async Task CloneStamp_PreservesOutsideRegion()
    {
        var src = CreateSolid(15, 15, 32768, 32768, 32768);
        var result = RetouchingOps.CloneStamp(src, 0, 0, 10, 10, 3, 3);

        // Pixel outside stamp area should be unchanged
        var srcRow = src.GetPixelRow(0).ToArray();
        var resRow = result.GetPixelRow(0).ToArray();
        await Assert.That(resRow[0]).IsEqualTo(srcRow[0]);

        src.Dispose(); result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // HEALING BRUSH TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task HealingBrush_BlendsWithSurroundings()
    {
        // Target area is green, source area is different color
        var src = CreateLeftRightSplit(30, 30, 50000, 0, 0, 0, 50000, 0);

        var result = RetouchingOps.HealingBrush(src, 2, 10, 20, 10, 8, 8);

        // Center of healed area should be influenced by green surroundings
        var row = result.GetPixelRow(14).ToArray();
        int ch = result.NumberOfChannels;
        // G channel should have some contribution from surrounding green
        await Assert.That((int)row[24 * ch + 1]).IsGreaterThan(0);

        src.Dispose(); result.Dispose();
    }

    [Test]
    public async Task HealingBrush_PreservesDimensions()
    {
        var src = CreateSolid(20, 20, 40000, 40000, 40000);
        var result = RetouchingOps.HealingBrush(src, 0, 0, 10, 10, 5, 5);

        await Assert.That(result.Columns).IsEqualTo(20u);
        await Assert.That(result.Rows).IsEqualTo(20u);

        src.Dispose(); result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // RED-EYE REMOVAL TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task RedEyeRemoval_DesaturatesRedCircle()
    {
        // Create an image with a bright red circle on gray background
        var src = CreateWithRedCircle(30, 30, 15, 15, 5);

        var result = RetouchingOps.RedEyeRemoval(src, 15, 15, 5, 0.5);

        // Center of red circle should have reduced redness
        var srcRow = src.GetPixelRow(15).ToArray();
        var resRow = result.GetPixelRow(15).ToArray();
        int ch = result.NumberOfChannels;

        int srcRed = srcRow[15 * ch];
        int resRed = resRow[15 * ch];
        // Red value should be reduced after correction
        await Assert.That(resRed).IsLessThanOrEqualTo(srcRed);

        src.Dispose(); result.Dispose();
    }

    [Test]
    public async Task RedEyeRemoval_LeavesNonRedAlone()
    {
        // Blue circle — should not be affected
        var src = CreateSolid(20, 20, 0, 0, 65535);
        var result = RetouchingOps.RedEyeRemoval(src, 10, 10, 5, 0.5);

        var srcRow = src.GetPixelRow(10).ToArray();
        var resRow = result.GetPixelRow(10).ToArray();
        // Blue pixel should be unchanged
        await Assert.That(resRow[10 * 3 + 2]).IsEqualTo(srcRow[10 * 3 + 2]);

        src.Dispose(); result.Dispose();
    }

    [Test]
    public async Task RedEyeRemoval_PreservesGreenBlueChannels()
    {
        var src = CreateWithRedCircle(20, 20, 10, 10, 4);
        var result = RetouchingOps.RedEyeRemoval(src, 10, 10, 4, 0.5);

        // Green and blue channels at the red pixel should be the same
        var srcRow = src.GetPixelRow(10).ToArray();
        var resRow = result.GetPixelRow(10).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That(resRow[10 * ch + 1]).IsEqualTo(srcRow[10 * ch + 1]); // G unchanged
        await Assert.That(resRow[10 * ch + 2]).IsEqualTo(srcRow[10 * ch + 2]); // B unchanged

        src.Dispose(); result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════

    private static ImageFrame CreateSolid(int w, int h, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 3] = r;
                row[x * 3 + 1] = g;
                row[x * 3 + 2] = b;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradient(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                ushort val = (ushort)((x + y) * Quantum.MaxValue / (w + h - 2));
                row[x * 3] = val;
                row[x * 3 + 1] = val;
                row[x * 3 + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateLeftRightSplit(int w, int h,
        ushort lr, ushort lg, ushort lb, ushort rr, ushort rg, ushort rb)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        int half = w / 2;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                if (x < half)
                { row[x * 3] = lr; row[x * 3 + 1] = lg; row[x * 3 + 2] = lb; }
                else
                { row[x * 3] = rr; row[x * 3 + 1] = rg; row[x * 3 + 2] = rb; }
            }
        }
        return frame;
    }

    /// <summary>Creates a mask with non-zero pixels in the specified rectangle.</summary>
    private static ImageFrame CreateMask(int w, int h, int mx, int my, int mw, int mh)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = my; y < Math.Min(my + mh, h); y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = mx; x < Math.Min(mx + mw, w); x++)
            {
                row[x * 3] = Quantum.MaxValue;
                row[x * 3 + 1] = Quantum.MaxValue;
                row[x * 3 + 2] = Quantum.MaxValue;
            }
        }
        return frame;
    }

    /// <summary>Creates an image with a red circle on gray background.</summary>
    private static ImageFrame CreateWithRedCircle(int w, int h, int cx, int cy, int radius)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        double radSq = radius * radius;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                double dy = y - cy;
                double dx = x - cx;
                if (dy * dy + dx * dx <= radSq)
                {
                    // Bright red
                    row[x * 3] = 65535;
                    row[x * 3 + 1] = 3000;
                    row[x * 3 + 2] = 3000;
                }
                else
                {
                    // Gray
                    row[x * 3] = 32768;
                    row[x * 3 + 1] = 32768;
                    row[x * 3 + 2] = 32768;
                }
            }
        }
        return frame;
    }
}
