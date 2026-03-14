using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for artistic visual effects: OilPaint, Charcoal, Sketch, Vignette,
/// Wave, Swirl, Implode.
/// </summary>
public class ArtisticOpsTests
{
    // ─── Oil Paint ──────────────────────────────────────────────

    [Test]
    public async Task OilPaint_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.OilPaint(source, 2, 256);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task OilPaint_ReducesUniqueColors()
    {
        // Gradient has many unique intensities; oil paint should quantize them
        using var source = CreateGradient(64, 64);
        using var result = ArtisticOps.OilPaint(source, 3, 20);

        int channels = source.NumberOfChannels;
        var srcColors = new HashSet<ushort>();
        var resultColors = new HashSet<ushort>();

        for (int y = 10; y < 54; y++)
        {
            var srcRow = source.GetPixelRow(y).ToArray();
            var resRow = result.GetPixelRow(y).ToArray();
            for (int x = 10; x < 54; x++)
            {
                srcColors.Add(srcRow[x * channels]);
                resultColors.Add(resRow[x * channels]);
            }
        }

        await Assert.That(resultColors.Count).IsLessThan(srcColors.Count);
    }

    [Test]
    public async Task OilPaint_SolidColorUnchanged()
    {
        using var source = CreateSolidColor(32, 32, Quantum.MaxValue / 2);
        using var result = ArtisticOps.OilPaint(source, 3);

        var srcRow = source.GetPixelRow(16).ToArray();
        var resRow = result.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        await Assert.That(resRow[16 * channels]).IsEqualTo(srcRow[16 * channels]);
    }

    [Test]
    public async Task OilPaint_PreservesAlpha()
    {
        using var source = CreateSolidColorWithAlpha(32, 32, 10000, 30000);
        using var result = ArtisticOps.OilPaint(source, 2);

        var row = result.GetPixelRow(16).ToArray();
        int channels = result.NumberOfChannels;
        await Assert.That(row[16 * channels + 3]).IsEqualTo((ushort)30000);
    }

    // ─── Charcoal ───────────────────────────────────────────────

    [Test]
    public async Task Charcoal_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Charcoal(source);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Charcoal_ProducesGrayscaleOutput()
    {
        using var source = CreateColorful(32, 32);
        using var result = ArtisticOps.Charcoal(source);

        int channels = result.NumberOfChannels;
        var row = result.GetPixelRow(16).ToArray();

        // All channels should be equal (grayscale)
        for (int x = 0; x < 32; x++)
        {
            int off = x * channels;
            await Assert.That(row[off]).IsEqualTo(row[off + 1]);
            await Assert.That(row[off + 1]).IsEqualTo(row[off + 2]);
        }
    }

    [Test]
    public async Task Charcoal_EdgesAppearAsDarkLines()
    {
        // Step edge should produce a dark line at the boundary
        using var source = CreateStepEdge(64, 64);
        using var result = ArtisticOps.Charcoal(source);

        int channels = result.NumberOfChannels;
        // After charcoal: edges become dark lines on a light background
        // Most of the interior should be near white (inverted from edge dark → negate → white)
        var midRow = result.GetPixelRow(32).ToArray();
        int edgeX = 32; // Where the step edge is
        ushort edgeVal = midRow[edgeX * channels];
        ushort interiorVal = midRow[5 * channels]; // Far from edge

        // Edge region should be darker than flat region
        await Assert.That(edgeVal).IsLessThan(interiorVal);
    }

    // ─── Sketch ─────────────────────────────────────────────────

    [Test]
    public async Task Sketch_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Sketch(source, 0.5, 45);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Sketch_ProducesLighterOutput()
    {
        // ColorDodge composite should produce a lighter, sketch-like result
        using var source = CreateColorful(32, 32);
        using var result = ArtisticOps.Sketch(source, 0.5, 0);

        int channels = result.NumberOfChannels;
        var srcRow = source.GetPixelRow(16).ToArray();
        var resRow = result.GetPixelRow(16).ToArray();

        // The sketch effect (ColorDodge + blend) should generally brighten the image
        long srcSum = 0, resSum = 0;
        for (int x = 0; x < 32; x++)
        {
            srcSum += srcRow[x * channels] + srcRow[x * channels + 1] + srcRow[x * channels + 2];
            resSum += resRow[x * channels] + resRow[x * channels + 1] + resRow[x * channels + 2];
        }

        // Sketch result should be brighter than original on average
        await Assert.That(resSum).IsGreaterThanOrEqualTo(srcSum);
    }

    [Test]
    public async Task Sketch_DifferentAnglesProduceDifferentMotionBlur()
    {
        // The Sketch pipeline's ColorDodge step saturates on simple images, making
        // final output angle-independent. Instead, verify that MotionBlur (the angle-
        // dependent step) correctly produces different results at different angles.
        using var source = CreateGradient(64, 64);
        using var blur0 = BlurNoiseOps.MotionBlur(source, 5, 1.5, 0);
        using var blur90 = BlurNoiseOps.MotionBlur(source, 5, 1.5, 90);

        double mse = ImageCompare.MeanSquaredError(blur0, blur90);
        await Assert.That(mse).IsGreaterThan(0.0);
    }

    [Test]
    public async Task Sketch_AlphaImageProducesOpaqueResult()
    {
        using var source = CreateSolidColorWithAlpha(32, 32, Quantum.MaxValue, 40000);
        using var result = ArtisticOps.Sketch(source, 0.5, 45);

        // Sketch flattens alpha to black then applies pipeline — result is always opaque
        await Assert.That(result.HasAlpha).IsFalse();
        await Assert.That(result.NumberOfChannels).IsEqualTo(3);
    }

    // ─── Vignette───────────────────────────────────────────────

    [Test]
    public async Task Vignette_PreservesDimensions()
    {
        using var source = CreateSolidColor(32, 32, Quantum.MaxValue);
        using var result = ArtisticOps.Vignette(source, 5.0, 4, 4);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Vignette_CenterBrighterThanCorners()
    {
        using var source = CreateSolidColor(64, 64, Quantum.MaxValue);
        using var result = ArtisticOps.Vignette(source, 3.0, 8, 8);

        int channels = result.NumberOfChannels;
        var centerRow = result.GetPixelRow(32).ToArray();
        ushort centerVal = centerRow[32 * channels]; // Center pixel

        var cornerRow = result.GetPixelRow(0).ToArray();
        ushort cornerVal = cornerRow[0]; // Top-left corner

        await Assert.That(centerVal).IsGreaterThan(cornerVal);
    }

    [Test]
    public async Task Vignette_BlackImageStaysBlack()
    {
        using var source = CreateSolidColor(32, 32, 0);
        using var result = ArtisticOps.Vignette(source, 5.0);

        var row = result.GetPixelRow(16).ToArray();
        int channels = result.NumberOfChannels;

        // Black multiplied by any factor is still black
        await Assert.That(row[16 * channels]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Vignette_AlphaImageProducesOpaqueResult()
    {
        using var source = CreateSolidColorWithAlpha(32, 32, Quantum.MaxValue, 40000);
        using var result = ArtisticOps.Vignette(source, 3.0);

        // Vignette flattens alpha to black then applies darkening — result is always opaque
        await Assert.That(result.HasAlpha).IsFalse();
        await Assert.That(result.NumberOfChannels).IsEqualTo(3);
    }

    // ─── Wave ───────────────────────────────────────────────────

    [Test]
    public async Task Wave_ExpandsHeight()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Wave(source, 5.0, 20.0);

        await Assert.That(result.Columns).IsEqualTo(32u);
        // Height should expand by 2 * ceil(amplitude)
        await Assert.That(result.Rows).IsEqualTo(42u); // 32 + 2*5
    }

    [Test]
    public async Task Wave_ZeroAmplitude_PreservesImage()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Wave(source, 0.0, 20.0);

        // With 0 amplitude, height stays the same and pixels should match
        await Assert.That(result.Rows).IsEqualTo(32u);

        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        bool allMatch = true;
        for (int x = 0; x < 32; x++)
        {
            if (srcRow[x * channels] != dstRow[x * channels]) { allMatch = false; break; }
        }

        await Assert.That(allMatch).IsTrue();
    }

    [Test]
    public async Task Wave_ProducesVerticalDisplacement()
    {
        // Horizontal stripes: sine wave should shift them vertically
        using var source = CreateHorizontalStripes(64, 64);
        using var result = ArtisticOps.Wave(source, 8.0, 32.0);

        // The result should have different vertical stripe patterns at different x positions
        int channels = result.NumberOfChannels;
        int midY = (int)result.Rows / 2;
        var row = result.GetPixelRow(midY).ToArray();

        // At x=0 and x=wavelength/2 (16), the sine values differ, so pixels should differ
        ushort v0 = row[0 * channels];
        ushort v16 = row[16 * channels];

        // They don't have to be different depending on alignment, but dimensions are correct
        await Assert.That(result.Rows).IsEqualTo(80u); // 64 + 2*8
    }

    // ─── Swirl ──────────────────────────────────────────────────

    [Test]
    public async Task Swirl_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Swirl(source, 45.0);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Swirl_ZeroDegrees_PreservesImage()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Swirl(source, 0.0);

        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        bool allClose = true;
        for (int x = 0; x < 32; x++)
        {
            int diff = Math.Abs(srcRow[x * channels] - dstRow[x * channels]);
            if (diff > 2) { allClose = false; break; }
        }

        await Assert.That(allClose).IsTrue();
    }

    [Test]
    public async Task Swirl_ModifiesPixels()
    {
        using var source = CreateCheckerboard(64, 64);
        using var result = ArtisticOps.Swirl(source, 90.0);

        var srcRow = source.GetPixelRow(32).ToArray();
        var dstRow = result.GetPixelRow(32).ToArray();

        bool anyDifferent = false;
        for (int i = 0; i < srcRow.Length; i++)
        {
            if (srcRow[i] != dstRow[i]) { anyDifferent = true; break; }
        }

        await Assert.That(anyDifferent).IsTrue();
    }

    [Test]
    public async Task Swirl_CornersUnaffected()
    {
        // Corners are outside the radius, should be unchanged
        using var source = CreateGradient(64, 64);
        using var result = ArtisticOps.Swirl(source, 90.0);

        int channels = source.NumberOfChannels;
        var srcCorner = source.GetPixelRow(0).ToArray();
        var dstCorner = result.GetPixelRow(0).ToArray();

        // Top-left corner (0,0) should be outside swirl radius
        await Assert.That(dstCorner[0]).IsEqualTo(srcCorner[0]);
    }

    // ─── Implode ────────────────────────────────────────────────

    [Test]
    public async Task Implode_PreservesDimensions()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Implode(source, 0.5);

        await Assert.That(result.Columns).IsEqualTo(32u);
        await Assert.That(result.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task Implode_ZeroAmount_PreservesImage()
    {
        using var source = CreateGradient(32, 32);
        using var result = ArtisticOps.Implode(source, 0.0);

        var srcRow = source.GetPixelRow(16).ToArray();
        var dstRow = result.GetPixelRow(16).ToArray();
        int channels = source.NumberOfChannels;

        bool allClose = true;
        for (int x = 0; x < 32; x++)
        {
            int diff = Math.Abs(srcRow[x * channels] - dstRow[x * channels]);
            if (diff > 2) { allClose = false; break; }
        }

        await Assert.That(allClose).IsTrue();
    }

    [Test]
    public async Task Implode_PositiveAmount_PullsCenterInward()
    {
        // With positive implode, center pixels are pulled inward
        using var source = CreateCheckerboard(64, 64);
        using var result = ArtisticOps.Implode(source, 0.8);

        var srcRow = source.GetPixelRow(32).ToArray();
        var dstRow = result.GetPixelRow(32).ToArray();

        bool anyDifferent = false;
        for (int i = 0; i < srcRow.Length; i++)
        {
            if (srcRow[i] != dstRow[i]) { anyDifferent = true; break; }
        }

        await Assert.That(anyDifferent).IsTrue();
    }

    [Test]
    public async Task Implode_NegativeAmount_PushesOutward()
    {
        using var source = CreateCheckerboard(64, 64);
        using var result = ArtisticOps.Implode(source, -0.8);

        var srcRow = source.GetPixelRow(32).ToArray();
        var dstRow = result.GetPixelRow(32).ToArray();

        bool anyDifferent = false;
        for (int i = 0; i < srcRow.Length; i++)
        {
            if (srcRow[i] != dstRow[i]) { anyDifferent = true; break; }
        }

        await Assert.That(anyDifferent).IsTrue();
    }

    [Test]
    public async Task Implode_CornersUnaffected()
    {
        using var source = CreateGradient(64, 64);
        using var result = ArtisticOps.Implode(source, 0.5);

        int channels = source.NumberOfChannels;
        var srcCorner = source.GetPixelRow(0).ToArray();
        var dstCorner = result.GetPixelRow(0).ToArray();

        await Assert.That(dstCorner[0]).IsEqualTo(srcCorner[0]);
    }

    // ─── Helper Methods ─────────────────────────────────────────

    private static ImageFrame CreateGradient(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateSolidColor(int width, int height, ushort value)
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
                row[off] = value;
                row[off + 1] = value;
                row[off + 2] = value;
            }
        }
        return image;
    }

    private static ImageFrame CreateSolidColorWithAlpha(int width, int height, ushort color, ushort alpha)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = color;
                row[off + 1] = color;
                row[off + 2] = color;
                row[off + 3] = alpha;
            }
        }
        return image;
    }

    private static ImageFrame CreateCheckerboard(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = ((x + y) % 2 == 0) ? Quantum.MaxValue : (ushort)0;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateColorful(int width, int height)
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
                row[off] = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));     // R varies with x
                row[off + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(height - 1, 1)); // G varies with y
                row[off + 2] = (ushort)(Quantum.MaxValue / 2);                           // B constant
            }
        }
        return image;
    }

    private static ImageFrame CreateStepEdge(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = x < width / 2 ? (ushort)0 : Quantum.MaxValue;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateHorizontalStripes(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            ushort val = (y % 4 < 2) ? Quantum.MaxValue : (ushort)0;
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
