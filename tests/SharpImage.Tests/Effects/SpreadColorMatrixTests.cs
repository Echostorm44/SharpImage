using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for Spread (pixel scatter) and ColorMatrix effects.
/// </summary>
public class SpreadColorMatrixTests
{
    /// <summary>Creates a gradient test image for effects testing.</summary>
    private static ImageFrame CreateGradientImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 3;
                row[offset] = Quantum.ScaleFromByte((byte)(x * 255 / Math.Max(width - 1, 1)));
                row[offset + 1] = Quantum.ScaleFromByte((byte)(y * 255 / Math.Max(height - 1, 1)));
                row[offset + 2] = Quantum.ScaleFromByte((byte)((x + y) * 127 / Math.Max(width + height - 2, 1)));
            }
        }
        return frame;
    }

    /// <summary>Creates a solid color test image.</summary>
    private static ImageFrame CreateSolidImage(int width, int height, byte r, byte g, byte b)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 3;
                row[offset] = Quantum.ScaleFromByte(r);
                row[offset + 1] = Quantum.ScaleFromByte(g);
                row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return frame;
    }

    /// <summary>Creates a test image with alpha channel.</summary>
    private static ImageFrame CreateImageWithAlpha(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, true);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                row[offset] = Quantum.ScaleFromByte((byte)(x * 255 / Math.Max(width - 1, 1)));
                row[offset + 1] = Quantum.ScaleFromByte((byte)(y * 255 / Math.Max(height - 1, 1)));
                row[offset + 2] = Quantum.ScaleFromByte(128);
                row[offset + 3] = Quantum.ScaleFromByte((byte)(200)); // partially transparent
            }
        }
        return frame;
    }

    // ─── Spread Tests ────────────────────────────────────────────

    [Test]
    public async Task Spread_PreservesDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var result = BlurNoiseOps.Spread(source, 5.0);

        await Assert.That(result.Columns).IsEqualTo(64u);
        await Assert.That(result.Rows).IsEqualTo(48u);
        await Assert.That(result.NumberOfChannels).IsEqualTo(source.NumberOfChannels);
    }

    [Test]
    public async Task Spread_ZeroRadius_ReturnsClone()
    {
        using var source = CreateGradientImage(32, 32);
        using var result = BlurNoiseOps.Spread(source, 0.0);

        // Zero radius should return identical image
        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();
        for (int i = 0; i < 32 * 3; i++)
            await Assert.That(dstRow[i]).IsEqualTo(srcRow[i]);
    }

    [Test]
    public async Task Spread_ScattersPixels()
    {
        using var source = CreateGradientImage(64, 64);
        using var result = BlurNoiseOps.Spread(source, 10.0);

        // After spread, pixels should differ from source at most positions
        int diffCount = 0;
        int totalPixels = 0;
        for (int y = 10; y < 54; y++) // avoid edges
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRow(y);
            for (int x = 10; x < 54; x++)
            {
                int off = x * 3;
                if (srcRow[off] != dstRow[off] || srcRow[off + 1] != dstRow[off + 1])
                    diffCount++;
                totalPixels++;
            }
        }
        double diffRatio = (double)diffCount / totalPixels;
        // With radius=10 on a gradient, most pixels should be displaced
        await Assert.That(diffRatio).IsGreaterThan(0.5);
    }

    [Test]
    public async Task Spread_LargeRadius_StillValid()
    {
        using var source = CreateGradientImage(32, 32);
        using var result = BlurNoiseOps.Spread(source, 50.0);

        // Even with large radius, all pixel values should be valid (from the source)
        for (int y = 0; y < 32; y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 32; x++)
            {
                int off = x * 3;
                await Assert.That(row[off]).IsGreaterThanOrEqualTo((ushort)0);
                await Assert.That(row[off]).IsLessThanOrEqualTo(Quantum.MaxValue);
            }
        }
    }

    [Test]
    public async Task Spread_PreservesAlphaChannel()
    {
        using var source = CreateImageWithAlpha(32, 32);
        using var result = BlurNoiseOps.Spread(source, 5.0);

        await Assert.That(result.HasAlpha).IsTrue();
        await Assert.That(result.NumberOfChannels).IsEqualTo(4);
    }

    [Test]
    public async Task Spread_NegativeRadius_Throws()
    {
        using var source = CreateGradientImage(32, 32);
        await Assert.That(() => BlurNoiseOps.Spread(source, -1.0))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Spread_TinyImage()
    {
        using var source = CreateGradientImage(4, 4);
        using var result = BlurNoiseOps.Spread(source, 2.0);

        await Assert.That(result.Columns).IsEqualTo(4u);
        await Assert.That(result.Rows).IsEqualTo(4u);
    }

    [Test]
    public async Task Spread_SolidImage_AllPixelsSameColor()
    {
        // Spreading a solid color should still result in all the same color
        using var source = CreateSolidImage(32, 32, 128, 64, 200);
        using var result = BlurNoiseOps.Spread(source, 10.0);

        ushort expectedR = Quantum.ScaleFromByte(128);
        ushort expectedG = Quantum.ScaleFromByte(64);
        ushort expectedB = Quantum.ScaleFromByte(200);

        for (int y = 0; y < 32; y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 32; x++)
            {
                int off = x * 3;
                await Assert.That(row[off]).IsEqualTo(expectedR);
                await Assert.That(row[off + 1]).IsEqualTo(expectedG);
                await Assert.That(row[off + 2]).IsEqualTo(expectedB);
            }
        }
    }

    // ─── ColorMatrix Tests ───────────────────────────────────────

    [Test]
    public async Task ColorMatrix_Identity_PreservesImage()
    {
        using var source = CreateGradientImage(32, 32);
        double[,] identity = {
            { 1, 0, 0 },
            { 0, 1, 0 },
            { 0, 0, 1 }
        };
        using var result = ColorAdjust.ColorMatrix(source, identity);

        // Identity matrix should produce identical output
        for (int y = 0; y < 32; y++)
        {
            var srcRow = source.GetPixelRow(y).ToArray();
            var dstRow = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 32 * 3; x++)
                await Assert.That(dstRow[x]).IsEqualTo(srcRow[x]);
        }
    }

    [Test]
    public async Task ColorMatrix_SwapRedBlue()
    {
        using var source = CreateSolidImage(16, 16, 255, 0, 0); // pure red
        double[,] swapRB = {
            { 0, 0, 1 },
            { 0, 1, 0 },
            { 1, 0, 0 }
        };
        using var result = ColorAdjust.ColorMatrix(source, swapRB);

        // Red should become blue
        var row = result.GetPixelRow(0).ToArray();
        byte r = Quantum.ScaleToByte(row[0]);
        byte g = Quantum.ScaleToByte(row[1]);
        byte b = Quantum.ScaleToByte(row[2]);
        await Assert.That(r).IsEqualTo((byte)0);
        await Assert.That(g).IsEqualTo((byte)0);
        await Assert.That(b).IsEqualTo((byte)255);
    }

    [Test]
    public async Task ColorMatrix_GrayscaleConversion()
    {
        using var source = CreateGradientImage(32, 32);
        // BT.709 luminance weights
        double[,] grayscale = {
            { 0.2126, 0.7152, 0.0722 },
            { 0.2126, 0.7152, 0.0722 },
            { 0.2126, 0.7152, 0.0722 }
        };
        using var result = ColorAdjust.ColorMatrix(source, grayscale);

        // All three channels should be equal (grayscale)
        for (int y = 0; y < 32; y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 32; x++)
            {
                int off = x * 3;
                await Assert.That(row[off]).IsEqualTo(row[off + 1]);
                await Assert.That(row[off + 1]).IsEqualTo(row[off + 2]);
            }
        }
    }

    [Test]
    public async Task ColorMatrix_Brightness_4x4()
    {
        using var source = CreateSolidImage(8, 8, 100, 100, 100);
        // 4×4 matrix with alpha pass-through, 1.5× brightness
        double[,] bright = {
            { 1.5, 0, 0, 0 },
            { 0, 1.5, 0, 0 },
            { 0, 0, 1.5, 0 },
            { 0, 0, 0, 1 }
        };
        using var result = ColorAdjust.ColorMatrix(source, bright);

        var row = result.GetPixelRow(0).ToArray();
        byte r = Quantum.ScaleToByte(row[0]);
        // 100 * 1.5 = 150
        await Assert.That(r).IsEqualTo((byte)150);
    }

    [Test]
    public async Task ColorMatrix_WithOffset_5x5()
    {
        using var source = CreateSolidImage(8, 8, 0, 0, 0); // black
        // 5×5 matrix: identity + offset in column 5 (adds brightness)
        double[,] offset = {
            { 1, 0, 0, 0, 0.5 },
            { 0, 1, 0, 0, 0.5 },
            { 0, 0, 1, 0, 0.5 },
            { 0, 0, 0, 1, 0 },
            { 0, 0, 0, 0, 1 }
        };
        using var result = ColorAdjust.ColorMatrix(source, offset);

        var row = result.GetPixelRow(0).ToArray();
        byte r = Quantum.ScaleToByte(row[0]);
        // Black + 0.5 * max → ~128
        await Assert.That(r).IsGreaterThanOrEqualTo((byte)126);
        await Assert.That(r).IsLessThanOrEqualTo((byte)130);
    }

    [Test]
    public async Task ColorMatrix_Clamping_NoOverflow()
    {
        using var source = CreateSolidImage(8, 8, 200, 200, 200);
        // Amplify way beyond range
        double[,] amplify = {
            { 5, 0, 0 },
            { 0, 5, 0 },
            { 0, 0, 5 }
        };
        using var result = ColorAdjust.ColorMatrix(source, amplify);

        var row = result.GetPixelRow(0).ToArray();
        // Should be clamped to max
        await Assert.That(row[0]).IsEqualTo(Quantum.MaxValue);
        await Assert.That(row[1]).IsEqualTo(Quantum.MaxValue);
        await Assert.That(row[2]).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task ColorMatrix_NegativeValues_ClampToZero()
    {
        using var source = CreateSolidImage(8, 8, 100, 100, 100);
        double[,] negate = {
            { -1, 0, 0 },
            { 0, -1, 0 },
            { 0, 0, -1 }
        };
        using var result = ColorAdjust.ColorMatrix(source, negate);

        var row = result.GetPixelRow(0).ToArray();
        // Negative values should clamp to 0
        await Assert.That(row[0]).IsEqualTo((ushort)0);
        await Assert.That(row[1]).IsEqualTo((ushort)0);
        await Assert.That(row[2]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task ColorMatrix_SepiaTransform()
    {
        using var source = CreateGradientImage(32, 32);
        // Standard sepia matrix
        double[,] sepia = {
            { 0.393, 0.769, 0.189 },
            { 0.349, 0.686, 0.168 },
            { 0.272, 0.534, 0.131 }
        };
        using var result = ColorAdjust.ColorMatrix(source, sepia);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);

        // Sepia should produce warm tones: R > G > B
        var row = result.GetPixelRow(16).ToArray();
        int off = 16 * 3;
        await Assert.That(row[off]).IsGreaterThanOrEqualTo(row[off + 1]); // R >= G
        await Assert.That(row[off + 1]).IsGreaterThanOrEqualTo(row[off + 2]); // G >= B
    }

    [Test]
    public async Task ColorMatrix_InvalidSize_Throws()
    {
        using var source = CreateGradientImage(8, 8);
        double[,] tooSmall = { { 1, 0 }, { 0, 1 } }; // 2×2 — too small
        await Assert.That(() => ColorAdjust.ColorMatrix(source, tooSmall))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task ColorMatrix_NonSquare_Throws()
    {
        using var source = CreateGradientImage(8, 8);
        double[,] nonSquare = { { 1, 0, 0 }, { 0, 1, 0 } }; // 2×3 — not square
        await Assert.That(() => ColorAdjust.ColorMatrix(source, nonSquare))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task ColorMatrix_PreservesAlpha_WithRGBMatrix()
    {
        using var source = CreateImageWithAlpha(16, 16);
        double[,] invert = {
            { -1, 0, 0 },
            { 0, -1, 0 },
            { 0, 0, -1 }
        };
        // 3×3 matrix on RGBA image — alpha should be preserved
        using var result = ColorAdjust.ColorMatrix(source, invert);

        await Assert.That(result.HasAlpha).IsTrue();
        // Alpha should be unchanged
        var srcRow = source.GetPixelRow(8).ToArray();
        var dstRow = result.GetPixelRow(8).ToArray();
        for (int x = 0; x < 16; x++)
            await Assert.That(dstRow[x * 4 + 3]).IsEqualTo(srcRow[x * 4 + 3]);
    }

    [Test]
    public async Task ColorMatrix_ChannelMixing()
    {
        using var source = CreateSolidImage(8, 8, 100, 0, 0); // pure red
        // Mix red into green and blue channels
        double[,] mix = {
            { 1, 0, 0 },
            { 0.5, 1, 0 },
            { 0.3, 0, 1 }
        };
        using var result = ColorAdjust.ColorMatrix(source, mix);

        var row = result.GetPixelRow(0).ToArray();
        byte r = Quantum.ScaleToByte(row[0]);
        byte g = Quantum.ScaleToByte(row[1]);
        byte b = Quantum.ScaleToByte(row[2]);
        await Assert.That(r).IsEqualTo((byte)100);
        await Assert.That(g).IsEqualTo((byte)50);   // 0.5 * 100
        await Assert.That(b).IsEqualTo((byte)30);   // 0.3 * 100
    }
}
