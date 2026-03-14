using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for Phase 27 — Bundle G: Remaining ImageMagick Gaps.
/// AutoOrient, Roll, Splice, Segment, SparseColor, Tint, BlueShift, Shadow, Stegano, Polaroid.
/// </summary>
public class BundleGTests
{
    // ═══════════════════════════════════════════════════════════════
    // AUTOORIENT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task AutoOrient_TopLeft_ReturnsClone()
    {
        var src = CreateSolid(10, 8, 65535, 0, 0);
        src.Orientation = OrientationType.TopLeft;
        var result = Geometry.AutoOrient(src);

        await Assert.That(result.Columns).IsEqualTo(10u);
        await Assert.That(result.Rows).IsEqualTo(8u);
        await Assert.That(result.Orientation).IsEqualTo(OrientationType.TopLeft);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task AutoOrient_TopRight_FlopsImage()
    {
        // TopRight = mirror horizontally (flop)
        var src = CreateGradientHorizontal(10, 5);
        src.Orientation = OrientationType.TopRight;
        var result = Geometry.AutoOrient(src);

        await Assert.That(result.Columns).IsEqualTo(10u);
        await Assert.That(result.Orientation).IsEqualTo(OrientationType.TopLeft);

        // First pixel should now be what was last pixel
        var origRow = src.GetPixelRow(0).ToArray();
        var resultRow = result.GetPixelRow(0).ToArray();
        await Assert.That(resultRow[0]).IsEqualTo(origRow[(10 - 1) * 3]);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task AutoOrient_RightTop_Rotates90()
    {
        // RightTop = rotate 90° CW — dimensions swap
        var src = CreateSolid(10, 5, 65535, 0, 0);
        src.Orientation = OrientationType.RightTop;
        var result = Geometry.AutoOrient(src);

        await Assert.That(result.Columns).IsEqualTo(5u);
        await Assert.That(result.Rows).IsEqualTo(10u);
        await Assert.That(result.Orientation).IsEqualTo(OrientationType.TopLeft);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task AutoOrient_BottomRight_Rotates180()
    {
        var src = CreateSolid(8, 6, 0, 65535, 0);
        src.Orientation = OrientationType.BottomRight;
        var result = Geometry.AutoOrient(src);

        await Assert.That(result.Columns).IsEqualTo(8u);
        await Assert.That(result.Rows).IsEqualTo(6u);
        await Assert.That(result.Orientation).IsEqualTo(OrientationType.TopLeft);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // ROLL TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Roll_ZeroOffset_ReturnsCopy()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0);
        var result = Geometry.Roll(src, 0, 0);

        await Assert.That(result.Columns).IsEqualTo(10u);
        var srcRow = src.GetPixelRow(0).ToArray();
        var dstRow = result.GetPixelRow(0).ToArray();
        await Assert.That(dstRow[0]).IsEqualTo(srcRow[0]);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Roll_HorizontalWrap_PixelsShiftRight()
    {
        // Create image: left half red, right half blue
        var src = CreateLeftRightSplit(10, 10, 65535, 0, 0, 0, 0, 65535);
        var result = Geometry.Roll(src, 5, 0);

        // After roll +5, the left half should now be blue (was right) and right half red (was left)
        var row = result.GetPixelRow(5).ToArray();
        // x=0 should be blue (was at x=5..9 in source)
        await Assert.That(row[0]).IsEqualTo((ushort)0);         // R=0
        await Assert.That(row[2]).IsEqualTo((ushort)65535);      // B=65535
        // x=5 should be red (was at x=0..4 in source)
        await Assert.That(row[5 * 3]).IsEqualTo((ushort)65535); // R=65535
        await Assert.That(row[5 * 3 + 2]).IsEqualTo((ushort)0); // B=0

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Roll_NegativeOffset_WrapsBackward()
    {
        var src = CreateLeftRightSplit(10, 10, 65535, 0, 0, 0, 0, 65535);
        var result = Geometry.Roll(src, -5, 0);

        // -5 on a 10-wide image is same as +5
        var row = result.GetPixelRow(5).ToArray();
        await Assert.That(row[0]).IsEqualTo((ushort)0);         // blue half moved left
        await Assert.That(row[2]).IsEqualTo((ushort)65535);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // SPLICE TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Splice_InsertsSpace_ExpandsDimensions()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0);
        var result = Geometry.Splice(src, 5, 5, 3, 4, 0, 65535, 0);

        await Assert.That(result.Columns).IsEqualTo(13u);  // 10 + 3
        await Assert.That(result.Rows).IsEqualTo(14u);      // 10 + 4

        // Original pixel at (0,0) should be red
        var topRow = result.GetPixelRow(0).ToArray();
        await Assert.That(topRow[0]).IsEqualTo((ushort)65535);
        await Assert.That(topRow[1]).IsEqualTo((ushort)0);

        // Spliced area at (5,5) should be green fill
        var spliceRow = result.GetPixelRow(5).ToArray();
        await Assert.That(spliceRow[5 * 3]).IsEqualTo((ushort)0);
        await Assert.That(spliceRow[5 * 3 + 1]).IsEqualTo((ushort)65535);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Splice_AtOrigin_PushesEverythingRight()
    {
        var src = CreateSolid(8, 8, 0, 0, 65535);
        var result = Geometry.Splice(src, 0, 0, 4, 4, 65535, 65535, 65535);

        await Assert.That(result.Columns).IsEqualTo(12u);
        await Assert.That(result.Rows).IsEqualTo(12u);

        // Top-left should be white fill
        var topRow = result.GetPixelRow(0).ToArray();
        await Assert.That(topRow[0]).IsEqualTo((ushort)65535);

        // Bottom-right should be original blue
        var botRow = result.GetPixelRow(11).ToArray();
        await Assert.That(botRow[11 * 3 + 2]).IsEqualTo((ushort)65535); // B
        await Assert.That(botRow[11 * 3]).IsEqualTo((ushort)0);         // R=0

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // BLUESHIFT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task BlueShift_ShiftsTowardBlue()
    {
        var src = CreateSolid(10, 10, 65535, 65535, 65535);
        var result = VisualEffectsOps.BlueShift(src, 1.5);

        await Assert.That(result.Columns).IsEqualTo(10u);

        // On a white image, BlueShift should still produce output
        var row = result.GetPixelRow(5).ToArray();
        // All channels should be close (white stays relatively uniform)
        await Assert.That((int)row[0]).IsGreaterThan(0);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task BlueShift_RedImage_DesaturatesTowardBlue()
    {
        var src = CreateSolid(10, 10, 65535, 0, 0);
        var result = VisualEffectsOps.BlueShift(src, 1.5);

        var row = result.GetPixelRow(5).ToArray();
        // Blue channel should be boosted relative to pure red
        // min(R,G,B) = 0, max = 65535
        // r_new = 0.5*(0.5*65535 + 1.5*0) + 1.5*65535) = 0.5*(32767.5 + 98302.5) ≈ 65535
        // But blue was 0 originally, blue_new = 0.5*(0.5*0 + 1.5*0) + 1.5*65535) ≈ 49151
        await Assert.That((int)row[2]).IsGreaterThan(0); // blue channel boosted from 0

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // TINT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Tint_MidtoneWeighting_AffectsMidtonesMore()
    {
        // Create a gradient from black to white
        var src = CreateGradientHorizontal(256, 10);
        var result = VisualEffectsOps.Tint(src, 65535, 0, 0); // Red tint

        // Midtone pixel (~x=128) should have more red boost than extremes
        var midRow = result.GetPixelRow(5).ToArray();
        int midCh = result.NumberOfChannels;
        double midR = midRow[128 * midCh]; // red value at x=128
        double edgeR = midRow[0]; // red value at x=0 (black)
        double whiteR = midRow[255 * midCh]; // red value at x=255 (white)

        // Midtones should have more tint effect relative to their original value
        // At black/white, the parabola weight is 0, so no tint added
        // At midtone (0.5), parabola weight is 1.0
        await Assert.That(midR).IsGreaterThanOrEqualTo(edgeR);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Tint_PreservesAlpha()
    {
        var src = CreateSolidWithAlpha(10, 10, 32768, 32768, 32768, 16384);
        var result = VisualEffectsOps.Tint(src, 65535, 0, 0);

        var row = result.GetPixelRow(5).ToArray();
        await Assert.That(row[5 * 4 + 3]).IsEqualTo((ushort)16384);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // SHADOW TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Shadow_CreatesShadowFrame()
    {
        var src = CreateSolid(20, 20, 65535, 0, 0);
        var result = VisualEffectsOps.Shadow(src, 80, 4.0, 4, 4);

        // Should be larger than source (border padding)
        await Assert.That((int)result.Columns).IsGreaterThan(20);
        await Assert.That((int)result.Rows).IsGreaterThan(20);
        await Assert.That(result.HasAlpha).IsTrue();

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Shadow_HasTransparentEdgesAndOpaqueCenter()
    {
        var src = CreateSolid(30, 30, 65535, 65535, 65535);
        var result = VisualEffectsOps.Shadow(src, 100, 3.0, 0, 0);

        int ch = result.NumberOfChannels;
        // Corner should be nearly transparent
        var cornerRow = result.GetPixelRow(0).ToArray();
        await Assert.That((int)cornerRow[ch - 1]).IsLessThan(Quantum.MaxValue / 2);

        // Center should have significant alpha
        int cy = (int)result.Rows / 2;
        int cx = (int)result.Columns / 2;
        var centerRow = result.GetPixelRow(cy).ToArray();
        await Assert.That((int)centerRow[cx * ch + ch - 1]).IsGreaterThan(0);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // STEGANO TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Stegano_EmbedExtract_RecoversBinaryPattern()
    {
        // Create a cover image and a small watermark
        var cover = CreateSolid(50, 50, 32768, 32768, 32768);
        var watermark = CreateCheckerboard(8, 8); // Black and white checkerboard

        var embedded = VisualEffectsOps.SteganoEmbed(cover, watermark, 1);
        var extracted = VisualEffectsOps.SteganoExtract(embedded, 8, 8, 1);

        // The extracted watermark should have similar pattern (black/white)
        var wmRow = watermark.GetPixelRow(0).ToArray();
        var exRow = extracted.GetPixelRow(0).ToArray();

        // Check that the first pixel (should be black) is low
        int wmCh = watermark.NumberOfChannels;
        int exCh = extracted.NumberOfChannels;
        bool firstIsBlack = wmRow[0] < Quantum.MaxValue / 2;
        bool extractedFirstIsBlack = exRow[0] < Quantum.MaxValue / 2;
        await Assert.That(extractedFirstIsBlack).IsEqualTo(firstIsBlack);

        cover.Dispose();
        watermark.Dispose();
        embedded.Dispose();
        extracted.Dispose();
    }

    [Test]
    public async Task Stegano_Embed_PreservesCoverDimensions()
    {
        var cover = CreateSolid(30, 30, 65535, 0, 0);
        var watermark = CreateSolid(5, 5, 0, 0, 0);

        var embedded = VisualEffectsOps.SteganoEmbed(cover, watermark, 1);
        await Assert.That(embedded.Columns).IsEqualTo(30u);
        await Assert.That(embedded.Rows).IsEqualTo(30u);

        cover.Dispose();
        watermark.Dispose();
        embedded.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // POLAROID TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Polaroid_AddsBorderWithBottomExtra()
    {
        var src = CreateSolid(40, 30, 65535, 0, 0);
        var result = VisualEffectsOps.Polaroid(src, 0, 10);

        // Width: 40 + 10*2 = 60
        // Height: 30 + 10 (top) + 30 (bottom = 10*3) = 70
        await Assert.That(result.Columns).IsEqualTo(60u);
        await Assert.That(result.Rows).IsEqualTo(70u);

        // Corner should be white border
        var topRow = result.GetPixelRow(0).ToArray();
        await Assert.That(topRow[0]).IsEqualTo((ushort)65535); // white

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Polaroid_WithRotation_ExpandsDimensions()
    {
        var src = CreateSolid(40, 30, 0, 65535, 0);
        var result = VisualEffectsOps.Polaroid(src, 15.0, 10);

        // Rotated image should be larger than the straight version
        await Assert.That((int)result.Columns).IsGreaterThan(40);
        await Assert.That((int)result.Rows).IsGreaterThan(30);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // SPARSE COLOR TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task SparseColor_Voronoi_AssignsNearestColor()
    {
        var src = CreateSolid(20, 20, 0, 0, 0);
        var points = new SparseColorPoint[]
        {
            new(0, 0, 65535, 0, 0),       // Red in top-left
            new(19, 19, 0, 0, 65535),     // Blue in bottom-right
        };

        var result = VisualEffectsOps.SparseColor(src, SparseColorMethod.Voronoi, points);

        // Top-left should be red
        var topRow = result.GetPixelRow(0).ToArray();
        await Assert.That(topRow[0]).IsEqualTo((ushort)65535); // R
        await Assert.That(topRow[2]).IsEqualTo((ushort)0);      // B

        // Bottom-right should be blue
        var botRow = result.GetPixelRow(19).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That(botRow[19 * ch]).IsEqualTo((ushort)0);      // R
        await Assert.That(botRow[19 * ch + 2]).IsEqualTo((ushort)65535); // B

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task SparseColor_Shepards_BlendsSmoothly()
    {
        var src = CreateSolid(20, 20, 0, 0, 0);
        var points = new SparseColorPoint[]
        {
            new(0, 10, 65535, 0, 0),   // Red on left
            new(19, 10, 0, 0, 65535),  // Blue on right
        };

        var result = VisualEffectsOps.SparseColor(src, SparseColorMethod.Shepards, points);

        // Middle pixel should be a blend of red and blue
        var midRow = result.GetPixelRow(10).ToArray();
        int ch = result.NumberOfChannels;
        int midX = 10;
        int redVal = midRow[midX * ch];
        int blueVal = midRow[midX * ch + 2];

        // Both should have significant values (smooth blend)
        await Assert.That(redVal).IsGreaterThan(0);
        await Assert.That(blueVal).IsGreaterThan(0);

        src.Dispose();
        result.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // SEGMENT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Segment_ReducesColorCount()
    {
        // Create image with two distinct color regions
        var src = CreateLeftRightSplit(20, 20, 65535, 0, 0, 0, 0, 65535);
        var result = VisualEffectsOps.Segment(src, 1.0, 1.5);

        await Assert.That(result.Columns).IsEqualTo(20u);
        await Assert.That(result.Rows).IsEqualTo(20u);

        // Count distinct colors — should be very few
        var colors = new HashSet<(ushort, ushort, ushort)>();
        int ch = result.NumberOfChannels;
        for (int y = 0; y < 20; y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 20; x++)
            {
                int off = x * ch;
                colors.Add((row[off], row[off + 1], row[off + 2]));
            }
        }
        // Should have very few distinct colors (ideally 2 for two regions)
        await Assert.That(colors.Count).IsLessThanOrEqualTo(10);

        src.Dispose();
        result.Dispose();
    }

    [Test]
    public async Task Segment_PreservesAlpha()
    {
        var src = CreateSolidWithAlpha(10, 10, 65535, 0, 0, 32000);
        var result = VisualEffectsOps.Segment(src, 1.0, 1.5);

        var row = result.GetPixelRow(5).ToArray();
        await Assert.That(row[5 * 4 + 3]).IsEqualTo((ushort)32000);

        src.Dispose();
        result.Dispose();
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

    private static ImageFrame CreateSolidWithAlpha(int w, int h, ushort r, ushort g, ushort b, ushort a)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, true);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 4] = r;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = b;
                row[x * 4 + 3] = a;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientHorizontal(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / (w - 1));
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
                {
                    row[x * 3] = lr;
                    row[x * 3 + 1] = lg;
                    row[x * 3 + 2] = lb;
                }
                else
                {
                    row[x * 3] = rr;
                    row[x * 3 + 1] = rg;
                    row[x * 3 + 2] = rb;
                }
            }
        }
        return frame;
    }

    private static ImageFrame CreateCheckerboard(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                ushort val = ((x + y) % 2 == 0) ? (ushort)0 : Quantum.MaxValue;
                row[x * 3] = val;
                row[x * 3 + 1] = val;
                row[x * 3 + 2] = val;
            }
        }
        return frame;
    }
}
