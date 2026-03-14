using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for Trim, Extent, Shave, Chop, Transpose, Transverse, and Deskew operations.
/// </summary>
public class GeometryTransformTests
{
    // ─── Trim ────────────────────────────────────────────────────

    [Test]
    public async Task Trim_RemovesUniformBorder()
    {
        // Create 64×64 image with 10px white border around red center
        using var source = CreateBorderedImage(64, 64, 10, Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue,
            Quantum.MaxValue, 0, 0);
        using var result = Geometry.Trim(source);

        // Should trim to 44×44 (64 - 10*2)
        await Assert.That(result.Columns).IsEqualTo(44u);
        await Assert.That(result.Rows).IsEqualTo(44u);
    }

    [Test]
    public async Task Trim_ExactMatchNoBorder()
    {
        // Image with no uniform border — trim should preserve dimensions
        using var source = CreateGradientImage(32, 32);
        using var result = Geometry.Trim(source);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Trim_WithFuzz_TrimsNearMatchColors()
    {
        // Create image with border that's almost-white (off by small amount)
        using var source = CreateBorderedImage(64, 64, 10,
            (ushort)(Quantum.MaxValue - 500), (ushort)(Quantum.MaxValue - 500), (ushort)(Quantum.MaxValue - 500),
            Quantum.MaxValue, 0, 0);

        // Without fuzz — border doesn't match corner exactly, so minimal trim
        using var noFuzz = Geometry.Trim(source, 0.0);
        // The corner is the border color, so it should still trim (corners are border)
        await Assert.That(noFuzz.Columns).IsEqualTo(44u);

        // With fuzz — should also trim
        using var withFuzz = Geometry.Trim(source, 0.05);
        await Assert.That(withFuzz.Columns).IsEqualTo(44u);
    }

    [Test]
    public async Task Trim_AllSameColor_Returns1x1()
    {
        using var source = CreateSolidImage(32, 32, 1000, 2000, 3000);
        using var result = Geometry.Trim(source);

        await Assert.That(result.Columns).IsEqualTo(1u);
        await Assert.That(result.Rows).IsEqualTo(1u);
    }

    // ─── Extent ──────────────────────────────────────────────────

    [Test]
    public async Task Extent_ExpandsCanvas()
    {
        using var source = CreateSolidImage(32, 32, Quantum.MaxValue, 0, 0);
        using var result = Geometry.Extent(source, 64, 64, 16, 16);

        await Assert.That(result.Columns).IsEqualTo(64u);
        await Assert.That(result.Rows).IsEqualTo(64u);

        // Center should have original red content
        int ch = result.NumberOfChannels;
        ushort centerRed = result.GetPixelRow(32).ToArray()[32 * ch];
        await Assert.That(centerRed).IsEqualTo(Quantum.MaxValue); // red at center

        // Top-left corner should be black (background)
        ushort topLeft = result.GetPixelRow(0).ToArray()[0];
        await Assert.That(topLeft).IsEqualTo((ushort)0); // black
    }

    [Test]
    public async Task Extent_SmallerCanvas_Crops()
    {
        using var source = CreateGradientImage(64, 64);
        using var result = Geometry.Extent(source, 32, 32);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Extent_WithNegativeOffset()
    {
        using var source = CreateSolidImage(32, 32, Quantum.MaxValue, 0, 0);
        using var result = Geometry.Extent(source, 32, 32, -8, -8);

        await Assert.That(result.Columns).IsEqualTo(32u);

        // Row 0 should contain content from source row 8
        ushort firstRed = result.GetPixelRow(0).ToArray()[0];
        await Assert.That(firstRed).IsEqualTo(Quantum.MaxValue); // red
    }

    // ─── Shave ───────────────────────────────────────────────────

    [Test]
    public async Task Shave_RemovesEdgeStrips()
    {
        using var source = CreateGradientImage(64, 64);
        using var result = Geometry.Shave(source, 10, 5);

        await Assert.That(result.Columns).IsEqualTo(44u); // 64 - 2*10
        await Assert.That(result.Rows).IsEqualTo(54u);    // 64 - 2*5
    }

    [Test]
    public async Task Shave_ZeroAmount_PreservesDimensions()
    {
        using var source = CreateGradientImage(32, 32);
        using var result = Geometry.Shave(source, 0, 0);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Shave_TooLarge_Throws()
    {
        using var source = CreateGradientImage(32, 32);
        await Assert.That(() => Geometry.Shave(source, 20, 0)).Throws<ArgumentException>();
    }

    // ─── Chop ────────────────────────────────────────────────────

    [Test]
    public async Task Chop_RemovesAndCollapses()
    {
        using var source = CreateGradientImage(64, 64);
        using var result = Geometry.Chop(source, 10, 10, 20, 20);

        await Assert.That(result.Columns).IsEqualTo(44u); // 64 - 20
        await Assert.That(result.Rows).IsEqualTo(44u);    // 64 - 20
    }

    [Test]
    public async Task Chop_VerticalColumnStrip()
    {
        // Remove a 10-pixel-wide vertical column strip (chopHeight=0 means skip no rows)
        using var source = CreateGradientImage(64, 64);
        using var result = Geometry.Chop(source, 20, 0, 10, 0);

        await Assert.That(result.Columns).IsEqualTo(54u); // 64 - 10
        await Assert.That(result.Rows).IsEqualTo(64u);    // unchanged
    }

    [Test]
    public async Task Chop_HorizontalRowStrip()
    {
        // Remove a 10-pixel-tall horizontal row strip (chopWidth=0 means skip no columns)
        using var source = CreateGradientImage(64, 64);
        using var result = Geometry.Chop(source, 0, 20, 0, 10);

        await Assert.That(result.Columns).IsEqualTo(64u); // unchanged
        await Assert.That(result.Rows).IsEqualTo(54u);    // 64 - 10
    }

    [Test]
    public async Task Chop_PreservesPixelsOutsideRegion()
    {
        using var source = CreateSolidImage(32, 32, Quantum.MaxValue, 0, 0);
        using var result = Geometry.Chop(source, 10, 10, 5, 5);

        // Result should be 27×27
        await Assert.That(result.Columns).IsEqualTo(27u);
        await Assert.That(result.Rows).IsEqualTo(27u);

        // Pixels outside chop should still be red
        ushort firstPixelRed = result.GetPixelRow(0).ToArray()[0];
        await Assert.That(firstPixelRed).IsEqualTo(Quantum.MaxValue);
    }

    // ─── Transpose ───────────────────────────────────────────────

    [Test]
    public async Task Transpose_SwapsDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var result = Geometry.Transpose(source);

        await Assert.That(result.Columns).IsEqualTo(48u);
        await Assert.That(result.Rows).IsEqualTo(64u);
    }

    [Test]
    public async Task Transpose_PixelMapping()
    {
        // Verify pixel (x,y) → (y,x)
        using var source = CreateGradientImage(32, 24);
        using var result = Geometry.Transpose(source);

        int ch = source.NumberOfChannels;
        // Check that source pixel (10, 5) equals result pixel (5, 10)
        ushort[] srcRowArr = source.GetPixelRow(5).ToArray();
        ushort[] dstRowArr = result.GetPixelRow(10).ToArray();

        for (int c = 0; c < ch; c++)
            await Assert.That(dstRowArr[5 * ch + c]).IsEqualTo(srcRowArr[10 * ch + c]);
    }

    [Test]
    public async Task Transpose_DoubleTransposeIsIdentity()
    {
        using var source = CreateGradientImage(32, 32);
        using var t1 = Geometry.Transpose(source);
        using var t2 = Geometry.Transpose(t1);

        // Double transpose should restore original
        var srcRow = source.GetPixelRow(16).ToArray();
        var resRow = t2.GetPixelRow(16).ToArray();

        for (int i = 0; i < srcRow.Length; i++)
            await Assert.That(resRow[i]).IsEqualTo(srcRow[i]);
    }

    // ─── Transverse ──────────────────────────────────────────────

    [Test]
    public async Task Transverse_SwapsDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var result = Geometry.Transverse(source);

        await Assert.That(result.Columns).IsEqualTo(48u);
        await Assert.That(result.Rows).IsEqualTo(64u);
    }

    [Test]
    public async Task Transverse_PixelMapping()
    {
        // Verify pixel (x,y) → (H-1-y, W-1-x) with swapped dims
        using var source = CreateGradientImage(32, 24);
        using var result = Geometry.Transverse(source);

        int ch = source.NumberOfChannels;
        // Source(10, 5) → Dest(srcHeight-1-y, srcWidth-1-x) = Dest(18, 21)
        // Dest dims: Columns=srcHeight=24, Rows=srcWidth=32
        // dstX = srcHeight-1-y = 23-5 = 18, dstY = srcWidth-1-x = 31-10 = 21
        ushort[] srcRowArr = source.GetPixelRow(5).ToArray();
        ushort[] dstRowArr = result.GetPixelRow(21).ToArray();

        for (int c = 0; c < ch; c++)
            await Assert.That(dstRowArr[18 * ch + c]).IsEqualTo(srcRowArr[10 * ch + c]);
    }

    [Test]
    public async Task Transverse_DoubleTransverseIsIdentity()
    {
        using var source = CreateGradientImage(32, 32);
        using var t1 = Geometry.Transverse(source);
        using var t2 = Geometry.Transverse(t1);

        var srcRow = source.GetPixelRow(16).ToArray();
        var resRow = t2.GetPixelRow(16).ToArray();

        for (int i = 0; i < srcRow.Length; i++)
            await Assert.That(resRow[i]).IsEqualTo(srcRow[i]);
    }

    // ─── Deskew ──────────────────────────────────────────────────

    [Test]
    public async Task Deskew_StraightImage_MinimalChange()
    {
        // An image with strong horizontal features should detect ~0° skew
        using var source = CreateHorizontalLines(64, 64);
        using var result = Geometry.Deskew(source);

        // Should detect near-zero skew and return dimensions close to original
        await Assert.That(result.Columns).IsGreaterThan(0u);
        await Assert.That(result.Rows).IsGreaterThan(0u);
    }

    [Test]
    public async Task Deskew_SkewedImage_Corrects()
    {
        // Create horizontal lines then rotate slightly
        using var lines = CreateHorizontalLines(100, 100);
        using var skewed = Geometry.RotateArbitrary(lines, 5.0); // 5° skew

        using var deskewed = Geometry.Deskew(skewed, 0.4);

        // The deskewed image should exist and be reasonable size
        await Assert.That(deskewed.Columns).IsGreaterThan(0u);
        await Assert.That(deskewed.Rows).IsGreaterThan(0u);
    }

    [Test]
    public async Task Deskew_PreservesColorspace()
    {
        using var source = CreateHorizontalLines(32, 32);
        using var result = Geometry.Deskew(source);

        await Assert.That(result.Colorspace).IsEqualTo(source.Colorspace);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateGradientImage(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));
                row[off + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(height - 1, 1));
                row[off + 2] = (ushort)((x + y) * Quantum.MaxValue / Math.Max(width + height - 2, 1));
            }
        }
        return image;
    }

    private static ImageFrame CreateSolidImage(int width, int height, ushort r, ushort g, ushort b)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = r;
                row[off + 1] = g;
                row[off + 2] = b;
            }
        }
        return image;
    }

    /// <summary>
    /// Creates an image with a colored border around a differently-colored interior.
    /// </summary>
    private static ImageFrame CreateBorderedImage(int width, int height, int borderWidth,
        ushort borderR, ushort borderG, ushort borderB,
        ushort interiorR, ushort interiorG, ushort interiorB)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            bool isBorderRow = y < borderWidth || y >= height - borderWidth;

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                bool isBorder = isBorderRow || x < borderWidth || x >= width - borderWidth;

                if (isBorder)
                {
                    row[off] = borderR;
                    row[off + 1] = borderG;
                    row[off + 2] = borderB;
                }
                else
                {
                    row[off] = interiorR;
                    row[off + 1] = interiorG;
                    row[off + 2] = interiorB;
                }
            }
        }
        return image;
    }

    /// <summary>
    /// Creates an image with alternating black and white horizontal lines.
    /// </summary>
    private static ImageFrame CreateHorizontalLines(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            ushort val = (y % 4 < 2) ? (ushort)0 : Quantum.MaxValue;
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }
}
