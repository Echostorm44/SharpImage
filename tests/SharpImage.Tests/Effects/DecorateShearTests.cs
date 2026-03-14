using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for Phase 26: Decorative &amp; Paint Ops + Shear Transform.
/// </summary>
public class DecorateShearTests
{
    // ═══════════════════════════════════════════════════════════════
    // BORDER TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Border_ExtendsDimensions()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0);
        var result = DecorateOps.Border(src, 5, 3, 0, 65535, 0);

        await Assert.That(result.Columns).IsEqualTo(20u);  // 10 + 5*2
        await Assert.That(result.Rows).IsEqualTo(16u);      // 10 + 3*2

        // Center pixel should be original red
        var centerArr = result.GetPixelRow(8).ToArray();
        await Assert.That(centerArr[5 * 3]).IsEqualTo((ushort)65535);
        await Assert.That(centerArr[5 * 3 + 1]).IsEqualTo((ushort)0);

        // Border pixel should be green
        var borderArr = result.GetPixelRow(0).ToArray();
        await Assert.That(borderArr[0]).IsEqualTo((ushort)0);
        await Assert.That(borderArr[1]).IsEqualTo((ushort)65535);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Border_PreservesAlpha()
    {
        var src = CreateSolidWithAlpha(8, 8, 65535, 0, 0, 32768);
        var result = DecorateOps.Border(src, 2, 2, 0, 0, 65535);

        await Assert.That(result.HasAlpha).IsTrue();
        await Assert.That(result.Columns).IsEqualTo(12u);

        // Interior alpha preserved
        ushort interiorAlpha = result.GetPixelRow(4).ToArray()[4 * 4 + 3];
        await Assert.That(interiorAlpha).IsEqualTo((ushort)32768);

        // Border alpha is opaque
        ushort borderAlpha = result.GetPixelRow(0).ToArray()[3];
        await Assert.That(borderAlpha).IsEqualTo(Quantum.MaxValue);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // FRAME TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Frame_ExtendsDimensionsWithBevel()
    {
        var src = CreateSolid(20, 20, 32768, 32768, 32768);
        var result = DecorateOps.Frame(src, 3, 3, 4, 2);

        // New dimensions: 20 + (3 + 4 + 2) * 2 = 20 + 18 = 38
        await Assert.That(result.Columns).IsEqualTo(38u);
        await Assert.That(result.Rows).IsEqualTo(38u);

        // Center should contain source
        int offset = 3 + 4 + 2; // 9
        var centerArr = result.GetPixelRow(offset + 10).ToArray();
        int centerIdx = (offset + 10) * 3;
        await Assert.That(centerArr[centerIdx]).IsEqualTo((ushort)32768);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Frame_OuterBevelHasHighlight()
    {
        var src = CreateSolid(20, 20, 32768, 32768, 32768);
        // matteR=G=B=32768, outerBevel=5, innerBevel=0
        var result = DecorateOps.Frame(src, 2, 2, 5, 0);

        // Top-left corner should be highlight (brighter than matte)
        ushort topLeftR = result.GetPixelRow(0).ToArray()[0];
        await Assert.That(topLeftR).IsGreaterThan((ushort)32768);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // RAISE TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Raise_RaisedBrightensTopLeft()
    {
        var src = CreateSolid(30, 30, 32768, 32768, 32768);
        var result = DecorateOps.Raise(src, 5, 5, raise: true);

        // Same dimensions
        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(30u);

        // Top-left should be brighter (highlighted)
        ushort topLeftR = result.GetPixelRow(2).ToArray()[2 * 3];
        await Assert.That(topLeftR).IsGreaterThan((ushort)32768);

        // Bottom-right should be darker (shadowed)
        ushort bottomRightR = result.GetPixelRow(27).ToArray()[27 * 3];
        await Assert.That(bottomRightR).IsLessThan((ushort)32768);

        // Center should be unmodified
        ushort centerR = result.GetPixelRow(15).ToArray()[15 * 3];
        await Assert.That(centerR).IsEqualTo((ushort)32768);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Raise_SunkenInverts()
    {
        var src = CreateSolid(30, 30, 32768, 32768, 32768);
        var result = DecorateOps.Raise(src, 5, 5, raise: false);

        // Top-left should be darker (sunken)
        ushort topLeftR = result.GetPixelRow(2).ToArray()[2 * 3];
        await Assert.That(topLeftR).IsLessThan((ushort)32768);

        // Bottom-right should be brighter
        ushort bottomRightR = result.GetPixelRow(27).ToArray()[27 * 3];
        await Assert.That(bottomRightR).IsGreaterThan((ushort)32768);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // SHADE TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Shade_GrayMode_ModulatesIntensity()
    {
        // Create an image with a gradient to generate normals
        var src = CreateHorizontalGradient(50, 50);
        var result = DecorateOps.Shade(src, gray: true, azimuthDegrees: 315, elevationDegrees: 45);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(50u);

        // Result should not be uniform (shading varies)
        var row1Arr = result.GetPixelRow(25).ToArray();
        ushort val1 = row1Arr[10 * 3];
        ushort val2 = row1Arr[40 * 3];
        await Assert.That(val1 != val2).IsTrue();

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shade_NonGray_OutputsGrayscaleShade()
    {
        var src = CreateHorizontalGradient(50, 50);
        var result = DecorateOps.Shade(src, gray: false, azimuthDegrees: 0, elevationDegrees: 45);

        // R, G, B should all be equal (grayscale shade)
        var rowArr = result.GetPixelRow(25).ToArray();
        int idx = 25 * 3;
        await Assert.That(rowArr[idx]).IsEqualTo(rowArr[idx + 1]);
        await Assert.That(rowArr[idx + 1]).IsEqualTo(rowArr[idx + 2]);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shade_FlatImage_UniformShade()
    {
        // Flat solid color → no gradient → shade = lightZ
        var src = CreateSolid(20, 20, 32768, 32768, 32768);
        var result = DecorateOps.Shade(src, gray: false, azimuthDegrees: 45, elevationDegrees: 45);

        // Interior pixels should all be the same (flat surface)
        var flatArr = result.GetPixelRow(10).ToArray();
        ushort v1 = flatArr[5 * 3];
        ushort v2 = flatArr[15 * 3];
        await Assert.That(v1).IsEqualTo(v2);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // OPAQUE PAINT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task OpaquePaint_ExactMatch_ReplacesColor()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0); // Red
        var result = DecorateOps.OpaquePaint(src,
            targetR: 65535, targetG: 0, targetB: 0,
            fillR: 0, fillG: 0, fillB: 65535);

        // All pixels should now be blue
        var paintArr = result.GetPixelRow(5).ToArray();
        await Assert.That(paintArr[5 * 3]).IsEqualTo((ushort)0);
        await Assert.That(paintArr[5 * 3 + 1]).IsEqualTo((ushort)0);
        await Assert.That(paintArr[5 * 3 + 2]).IsEqualTo((ushort)65535);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task OpaquePaint_WithFuzz_MatchesSimilar()
    {
        var src = new ImageFrame();
        src.Initialize(10, 10, ColorspaceType.SRGB, false);
        for (int y = 0; y < 10; y++)
        {
            var row = src.GetPixelRowForWrite(y);
            for (int x = 0; x < 10; x++)
            {
                int idx = x * 3;
                row[idx] = (ushort)(65535 - x * 100); // Slight gradient
                row[idx + 1] = 0;
                row[idx + 2] = 0;
            }
        }

        var result = DecorateOps.OpaquePaint(src,
            targetR: 65535, targetG: 0, targetB: 0,
            fillR: 0, fillG: 65535, fillB: 0,
            fuzz: 1000); // 1000 fuzz should match nearby reds

        // x=0 (65535): should match (distance=0)
        ushort fuzzFill = result.GetPixelRow(0).ToArray()[1];
        await Assert.That(fuzzFill).IsEqualTo((ushort)65535); // Green fill

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task OpaquePaint_Invert_ReplacesNonMatching()
    {
        var src = CreateTwoColor(10, 10);
        var result = DecorateOps.OpaquePaint(src,
            targetR: 65535, targetG: 0, targetB: 0,
            fillR: 0, fillG: 65535, fillB: 0,
            invert: true);

        // Red pixels should NOT be replaced (they match → invert skips)
        var invertArr = result.GetPixelRow(0).ToArray();
        await Assert.That(invertArr[0]).IsEqualTo((ushort)65535); // Still red

        // Blue pixels SHOULD be replaced (they don't match → invert hits)
        await Assert.That(invertArr[3 * 1 + 1]).IsEqualTo((ushort)65535); // Now green

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // TRANSPARENT PAINT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task TransparentPaint_MakesMatchingTransparent()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0);
        var result = DecorateOps.TransparentPaint(src,
            targetR: 65535, targetG: 0, targetB: 0);

        // Output must have alpha
        await Assert.That(result.HasAlpha).IsTrue();

        // All matching pixels should be transparent (alpha=0)
        ushort transAlpha = result.GetPixelRow(5).ToArray()[5 * 4 + 3];
        await Assert.That(transAlpha).IsEqualTo((ushort)0);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task TransparentPaint_NonMatching_KeepsOpaque()
    {
        var src = CreateTwoColor(10, 10);
        var result = DecorateOps.TransparentPaint(src,
            targetR: 65535, targetG: 0, targetB: 0);

        // Red pixels → transparent
        var twoColorArr = result.GetPixelRow(0).ToArray();
        await Assert.That(twoColorArr[0 * 4 + 3]).IsEqualTo((ushort)0);

        // Blue pixels → opaque
        await Assert.That(twoColorArr[1 * 4 + 3]).IsEqualTo(Quantum.MaxValue);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // SHEAR TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Shear_XOnly_ExpandsWidth()
    {
        var src = CreateSolid(20, 20, 32768, 32768, 32768);
        var result = Geometry.Shear(src, xShear: 20, yShear: 0);

        // Width should increase
        await Assert.That(result.Columns).IsGreaterThan(20u);
        // Height stays approximately the same (Y-shear=0 still expands slightly)
        await Assert.That(result.Rows).IsGreaterThanOrEqualTo(20u);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shear_YOnly_ExpandsHeight()
    {
        var src = CreateSolid(20, 20, 32768, 32768, 32768);
        var result = Geometry.Shear(src, xShear: 0, yShear: 20);

        // Height should increase
        await Assert.That(result.Rows).IsGreaterThan(20u);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shear_BothAxes_ExpandsBoth()
    {
        var src = CreateSolid(30, 30, 65535, 0, 0);
        var result = Geometry.Shear(src, xShear: 15, yShear: 10);

        await Assert.That(result.Columns).IsGreaterThan(30u);
        await Assert.That(result.Rows).IsGreaterThan(30u);

        // Should contain some non-background pixels
        bool hasContent = false;
        for (int y = 0; y < (int)result.Rows && !hasContent; y++)
        {
            var row = result.GetPixelRow(y);
            for (int x = 0; x < (int)result.Columns && !hasContent; x++)
            {
                if (row[x * 3] > 100) hasContent = true;
            }
        }
        await Assert.That(hasContent).IsTrue();

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shear_PreservesAlpha()
    {
        var src = CreateSolidWithAlpha(20, 20, 65535, 0, 0, 32768);
        var result = Geometry.Shear(src, xShear: 10, yShear: 0);

        await Assert.That(result.HasAlpha).IsTrue();

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shear_ZeroAngles_PreservesDimensions()
    {
        var src = CreateSolid(20, 20, 32768, 32768, 32768);
        var result = Geometry.Shear(src, xShear: 0, yShear: 0);

        // With zero shear, dimensions should be the same (or very close)
        await Assert.That(result.Columns).IsEqualTo(20u);
        await Assert.That(result.Rows).IsEqualTo(20u);

        // Content should be preserved
        ushort srcVal = src.GetPixelRow(10).ToArray()[10 * 3];
        ushort dstVal = result.GetPixelRow(10).ToArray()[10 * 3];
        await Assert.That(dstVal).IsEqualTo(srcVal);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
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
                int idx = x * 3;
                row[idx] = r;
                row[idx + 1] = g;
                row[idx + 2] = b;
            }
        }
        return frame;
    }

    private static ImageFrame CreateSolidWithAlpha(int w, int h, ushort r, ushort g, ushort b, ushort a)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, true);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int idx = x * 4;
                row[idx] = r;
                row[idx + 1] = g;
                row[idx + 2] = b;
                row[idx + 3] = a;
            }
        }
        return frame;
    }

    private static ImageFrame CreateHorizontalGradient(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / (w - 1));
                int idx = x * 3;
                row[idx] = val;
                row[idx + 1] = val;
                row[idx + 2] = val;
            }
        }
        return frame;
    }

    /// <summary>
    /// Creates 10×10 image: even columns red, odd columns blue.
    /// </summary>
    private static ImageFrame CreateTwoColor(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int idx = x * 3;
                if (x % 2 == 0)
                {
                    row[idx] = 65535; row[idx + 1] = 0; row[idx + 2] = 0;
                }
                else
                {
                    row[idx] = 0; row[idx + 1] = 0; row[idx + 2] = 65535;
                }
            }
        }
        return frame;
    }
}
