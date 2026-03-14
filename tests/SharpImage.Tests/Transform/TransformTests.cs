using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for image resize, crop, flip, flop, rotate, and composite operations.
/// </summary>
public class TransformTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    // ─── Resize Tests ────────────────────────────────────────────

    [Test]
    public async Task Resize_NearestNeighbor_CorrectDimensions()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.NearestNeighbor);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
    }

    [Test]
    public async Task Resize_Bilinear_Upscale()
    {
        using var source = CreateGradientImage(16, 12);
        using var resized = Resize.Apply(source, 48, 36, InterpolationMethod.Bilinear);

        await Assert.That(resized.Columns).IsEqualTo(48);
        await Assert.That(resized.Rows).IsEqualTo(36);

        // Verify non-black output
        var midRow = resized.GetPixelRow(18).ToArray();
        bool hasContent = false;
        for (int i = 0; i < midRow.Length; i++)
            if (midRow[i] > 0) { hasContent = true; break; }
        await Assert.That(hasContent).IsTrue();
    }

    [Test]
    public async Task Resize_Bicubic_Downscale()
    {
        using var source = CreateGradientImage(128, 96);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Bicubic);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
    }

    [Test]
    public async Task Resize_Lanczos3_Default()
    {
        using var source = CreateGradientImage(80, 60);
        using var resized = Resize.Apply(source, 40, 30);

        await Assert.That(resized.Columns).IsEqualTo(40);
        await Assert.That(resized.Rows).IsEqualTo(30);
    }

    [Test]
    public async Task Resize_RealImage_RoseHalfSize()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");
        using var rose = PngCoder.Read(rosePng);
        int halfW = (int)rose.Columns / 2;
        int halfH = (int)rose.Rows / 2;

        using var resized = Resize.Apply(rose, halfW, halfH, InterpolationMethod.Lanczos3);

        await Assert.That(resized.Columns).IsEqualTo(halfW);
        await Assert.That(resized.Rows).IsEqualTo(halfH);

        // Should still have color content
        var midRow = resized.GetPixelRow(resized.Rows / 2).ToArray();
        bool hasColor = false;
        for (int i = 0; i < midRow.Length; i++)
            if (midRow[i] > 0) { hasColor = true; break; }
        await Assert.That(hasColor).IsTrue();
    }

    // ─── Crop Tests ──────────────────────────────────────────────

    [Test]
    public async Task Crop_BasicRegion()
    {
        using var source = CreateGradientImage(64, 48);
        using var cropped = Geometry.Crop(source, 10, 5, 20, 15);

        await Assert.That(cropped.Columns).IsEqualTo(20);
        await Assert.That(cropped.Rows).IsEqualTo(15);

        // Verify cropped content matches source region
        var srcRow = source.GetPixelRow(5).ToArray();
        var cropRow = cropped.GetPixelRow(0).ToArray();
        int channels = source.NumberOfChannels;

        for (int x = 0; x < 20; x++)
            for (int c = 0; c < channels; c++)
                await Assert.That(cropRow[x * channels + c])
                    .IsEqualTo(srcRow[(10 + x) * channels + c]);
    }

    [Test]
    public async Task Crop_ClampsToBounds()
    {
        using var source = CreateGradientImage(32, 24);
        using var cropped = Geometry.Crop(source, 20, 15, 50, 50);

        // Should clamp to 12x9 (what's available)
        await Assert.That(cropped.Columns).IsEqualTo(12);
        await Assert.That(cropped.Rows).IsEqualTo(9);
    }

    // ─── Flip / Flop Tests ───────────────────────────────────────

    [Test]
    public async Task Flip_VerticalMirror()
    {
        using var source = CreateGradientImage(16, 12);
        using var flipped = Geometry.Flip(source);

        await Assert.That(flipped.Columns).IsEqualTo(16);
        await Assert.That(flipped.Rows).IsEqualTo(12);

        // First row of flipped == last row of source
        var srcLast = source.GetPixelRow(11).ToArray();
        var flipFirst = flipped.GetPixelRow(0).ToArray();
        int channels = source.NumberOfChannels;

        for (int i = 0; i < 16 * channels; i++)
            await Assert.That(flipFirst[i]).IsEqualTo(srcLast[i]);
    }

    [Test]
    public async Task Flop_HorizontalMirror()
    {
        using var source = CreateGradientImage(16, 12);
        using var flopped = Geometry.Flop(source);

        await Assert.That(flopped.Columns).IsEqualTo(16);
        await Assert.That(flopped.Rows).IsEqualTo(12);

        // First pixel of flopped row 0 == last pixel of source row 0
        var srcRow = source.GetPixelRow(0).ToArray();
        var flopRow = flopped.GetPixelRow(0).ToArray();
        int channels = source.NumberOfChannels;

        for (int c = 0; c < channels; c++)
            await Assert.That(flopRow[c]).IsEqualTo(srcRow[(15) * channels + c]);
    }

    [Test]
    public async Task FlipFlip_Identity()
    {
        using var source = CreateGradientImage(16, 12);
        using var flipped1 = Geometry.Flip(source);
        using var flipped2 = Geometry.Flip(flipped1);

        // Double flip should restore original
        int channels = source.NumberOfChannels;
        for (int y = 0; y < 12; y++)
        {
            var srcRow = source.GetPixelRow(y).ToArray();
            var resRow = flipped2.GetPixelRow(y).ToArray();
            for (int i = 0; i < 16 * channels; i++)
                await Assert.That(resRow[i]).IsEqualTo(srcRow[i]);
        }
    }

    // ─── Rotation Tests ──────────────────────────────────────────

    [Test]
    public async Task Rotate90_SwapsDimensions()
    {
        using var source = CreateGradientImage(20, 10);
        using var rotated = Geometry.Rotate(source, RotationAngle.Rotate90);

        // 90° CW: width and height swap
        await Assert.That(rotated.Columns).IsEqualTo(10);
        await Assert.That(rotated.Rows).IsEqualTo(20);
    }

    [Test]
    public async Task Rotate180_SameDimensions()
    {
        using var source = CreateGradientImage(20, 10);
        using var rotated = Geometry.Rotate(source, RotationAngle.Rotate180);

        await Assert.That(rotated.Columns).IsEqualTo(20);
        await Assert.That(rotated.Rows).IsEqualTo(10);
    }

    [Test]
    public async Task Rotate270_SwapsDimensions()
    {
        using var source = CreateGradientImage(20, 10);
        using var rotated = Geometry.Rotate(source, RotationAngle.Rotate270);

        await Assert.That(rotated.Columns).IsEqualTo(10);
        await Assert.That(rotated.Rows).IsEqualTo(20);
    }

    [Test]
    public async Task Rotate360_Identity()
    {
        using var source = CreateGradientImage(16, 12);
        using var r90 = Geometry.Rotate(source, RotationAngle.Rotate90);
        using var r180 = Geometry.Rotate(r90, RotationAngle.Rotate90);
        using var r270 = Geometry.Rotate(r180, RotationAngle.Rotate90);
        using var r360 = Geometry.Rotate(r270, RotationAngle.Rotate90);

        // Four 90° rotations should produce the original
        await Assert.That(r360.Columns).IsEqualTo(source.Columns);
        await Assert.That(r360.Rows).IsEqualTo(source.Rows);

        int channels = source.NumberOfChannels;
        for (int y = 0; y < 12; y++)
        {
            var srcRow = source.GetPixelRow(y).ToArray();
            var resRow = r360.GetPixelRow(y).ToArray();
            for (int i = 0; i < 16 * channels; i++)
                await Assert.That(resRow[i]).IsEqualTo(srcRow[i]);
        }
    }

    [Test]
    public async Task RotateArbitrary_45Degrees_ExpandsDimensions()
    {
        using var source = CreateGradientImage(20, 20);
        using var rotated = Geometry.RotateArbitrary(source, 45);

        // 45° rotation of a square should expand
        await Assert.That((int)rotated.Columns).IsGreaterThan(20);
        await Assert.That((int)rotated.Rows).IsGreaterThan(20);
    }

    // ─── Composite Tests ─────────────────────────────────────────

    [Test]
    public async Task Composite_Over_BasicBlend()
    {
        using var baseImg = CreateSolidImage(32, 24, 0, 0, 65535); // Blue
        using var overlay = CreateSolidImage(16, 12, 65535, 0, 0); // Red, no alpha

        Composite.Apply(baseImg, overlay, 8, 6, CompositeMode.Over);

        // Check that overlay region is now red
        var row = baseImg.GetPixelRow(10).ToArray();
        int channels = baseImg.NumberOfChannels;
        int midX = 16;
        int off = midX * channels;

        await Assert.That(row[off]).IsEqualTo((ushort)65535);     // R
        await Assert.That(row[off + 1]).IsEqualTo((ushort)0);     // G
        await Assert.That(row[off + 2]).IsEqualTo((ushort)0);     // B

        // Check that non-overlay region is still blue
        var cornerRow = baseImg.GetPixelRow(0).ToArray();
        await Assert.That(cornerRow[0]).IsEqualTo((ushort)0);     // R
        await Assert.That(cornerRow[2]).IsEqualTo((ushort)65535); // B
    }

    [Test]
    public async Task Composite_Replace_FullOverwrite()
    {
        using var baseImg = CreateSolidImage(16, 16, 65535, 65535, 65535); // White
        using var overlay = CreateSolidImage(8, 8, 0, 0, 0);             // Black

        Composite.Apply(baseImg, overlay, 4, 4, CompositeMode.Replace);

        // Overlay region should be black
        var row = baseImg.GetPixelRow(8).ToArray();
        int channels = baseImg.NumberOfChannels;
        int off = 6 * channels;

        await Assert.That(row[off]).IsEqualTo((ushort)0);
        await Assert.That(row[off + 1]).IsEqualTo((ushort)0);
        await Assert.That(row[off + 2]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Composite_Multiply_DarkensImage()
    {
        using var baseImg = CreateSolidImage(16, 16, 32768, 32768, 32768); // 50% gray
        using var overlay = CreateSolidImage(16, 16, 32768, 32768, 32768); // 50% gray

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Multiply);

        // Multiply of 0.5 * 0.5 = 0.25 → ~16384
        var row = baseImg.GetPixelRow(8).ToArray();
        int channels = baseImg.NumberOfChannels;
        int val = row[0];

        // Should be approximately 25% (16384) with some rounding
        await Assert.That(val).IsGreaterThan(14000);
        await Assert.That(val).IsLessThan(18000);
    }

    // ─── Integration Tests (Real Images) ─────────────────────────

    [Test]
    public async Task Resize_Rose_ThenSave()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");
        string outFile = Path.Combine(Path.GetTempPath(), "sharpimage_test_rose_resized.png");

        try
        {
            using var rose = PngCoder.Read(rosePng);
            using var resized = Resize.Apply(rose, 35, 23, InterpolationMethod.Lanczos3);

            PngCoder.Write(resized, outFile);
            using var reloaded = PngCoder.Read(outFile);

            await Assert.That(reloaded.Columns).IsEqualTo(35);
            await Assert.That(reloaded.Rows).IsEqualTo(23);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
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
}
