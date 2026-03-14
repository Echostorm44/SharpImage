using SharpImage.Colorspaces;
using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests;

/// <summary>
/// Edge-case stress tests: unusual image dimensions, all-transparent, 16-bit extremes,
/// grayscale, and boundary conditions that might crash or produce garbage output.
/// </summary>
public class StressTests
{
    // ─── 1×1 images ──────────────────────────────────────────────

    [Test]
    public async Task OneByOne_Resize_DoesNotCrash()
    {
        using var tiny = CreateFrame(1, 1, 32768);
        using var resized = Resize.Apply(tiny, 4, 4, InterpolationMethod.Lanczos3);
        await Assert.That((int)resized.Columns).IsEqualTo(4);
        await Assert.That((int)resized.Rows).IsEqualTo(4);

        // All pixels should be the same color (solid image)
        var row = resized.GetPixelRow(0);
        ushort center = row[0];
        await Assert.That(Math.Abs(center - 32768)).IsLessThan(1000);
    }

    [Test]
    public async Task OneByOne_Flip_DoesNotCrash()
    {
        using var tiny = CreateFrame(1, 1, 32768);
        using var flipped = Geometry.Flip(tiny);
        await Assert.That((int)flipped.Columns).IsEqualTo(1);
        await Assert.That((int)flipped.Rows).IsEqualTo(1);
    }

    [Test]
    public async Task OneByOne_Rotate90_DoesNotCrash()
    {
        using var tiny = CreateFrame(1, 1, 32768);
        using var rotated = Geometry.Rotate(tiny, RotationAngle.Rotate90);
        await Assert.That((int)rotated.Columns).IsEqualTo(1);
        await Assert.That((int)rotated.Rows).IsEqualTo(1);
    }

    [Test]
    public async Task OneByOne_Blur_DoesNotCrash()
    {
        using var tiny = CreateFrame(1, 1, 32768);
        using var blurred = ConvolutionFilters.GaussianBlur(tiny, 3.0);
        await Assert.That((int)blurred.Columns).IsEqualTo(1);
    }

    [Test]
    public async Task OneByOne_ColorspaceConvert_DoesNotCrash()
    {
        using var tiny = CreateFrame(1, 1, 32768);
        var converted = ColorspaceOps.ConvertToColorspace(tiny, "lab");
        var back = ColorspaceOps.ConvertFromColorspace(converted, "lab");
        await Assert.That((int)back.Columns).IsEqualTo(1);
        converted.Dispose();
        back.Dispose();
    }

    // ─── Wide and tall images ────────────────────────────────────

    [Test]
    public async Task WideStrip_16384x1_Resize()
    {
        using var wide = CreateFrame(16384, 1, Quantum.MaxValue);
        using var resized = Resize.Apply(wide, 100, 1, InterpolationMethod.Lanczos3);
        await Assert.That((int)resized.Columns).IsEqualTo(100);
        await Assert.That((int)resized.Rows).IsEqualTo(1);
    }

    [Test]
    public async Task TallStrip_1x16384_Resize()
    {
        using var tall = CreateFrame(1, 16384, Quantum.MaxValue);
        using var resized = Resize.Apply(tall, 1, 100, InterpolationMethod.Lanczos3);
        await Assert.That((int)resized.Columns).IsEqualTo(1);
        await Assert.That((int)resized.Rows).IsEqualTo(100);
    }

    // ─── All-black and all-white images ──────────────────────────

    [Test]
    public async Task AllBlack_Operations_NoArithmeticErrors()
    {
        using var black = CreateFrame(32, 32, 0);

        using var blurred = ConvolutionFilters.GaussianBlur(black, 2.0);
        using var resized = Resize.Apply(black, 16, 16, InterpolationMethod.Mitchell);
        using var flipped = Geometry.Flip(black);
        var labConverted = ColorspaceOps.ConvertToColorspace(black, "lab");

        // All should succeed without NaN or division-by-zero
        var row = blurred.GetPixelRow(0);
        await Assert.That(row[0]).IsEqualTo((ushort)0);
        labConverted.Dispose();
    }

    [Test]
    public async Task AllWhite_Operations_NoArithmeticErrors()
    {
        using var white = CreateFrame(32, 32, Quantum.MaxValue);

        using var blurred = ConvolutionFilters.GaussianBlur(white, 2.0);
        using var resized = Resize.Apply(white, 16, 16, InterpolationMethod.Mitchell);

        var row = blurred.GetPixelRow(16);
        await Assert.That(row[0]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── Transparent images (RGBA) ───────────────────────────────

    [Test]
    public async Task FullyTransparent_Resize_PreservesTransparency()
    {
        var frame = new ImageFrame();
        frame.Initialize(32, 32, ColorspaceType.SRGB, true);
        // All channels = 0 including alpha
        for (int y = 0; y < 32; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            row.Fill(0);
        }

        using var resized = Resize.Apply(frame, 16, 16, InterpolationMethod.Lanczos3);
        var resRow = resized.GetPixelRow(8);
        int ch = resized.NumberOfChannels;
        // Alpha should still be 0
        await Assert.That(resRow[8 * ch + ch - 1]).IsEqualTo((ushort)0);
        frame.Dispose();
    }

    [Test]
    public async Task FullyTransparent_Composite_NoVisibleResult()
    {
        using var baseImg = CreateRgbaFrame(16, 16, Quantum.MaxValue, 0, 0, Quantum.MaxValue);
        using var overlay = CreateRgbaFrame(8, 8, 0, Quantum.MaxValue, 0, 0); // fully transparent overlay

        Composite.Apply(baseImg, overlay, 4, 4);

        // Base should be unchanged since overlay is fully transparent
        var row = baseImg.GetPixelRow(6);
        int ch = baseImg.NumberOfChannels;
        await Assert.That(row[6 * ch]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── Extreme resize ratios ───────────────────────────────────

    [Test]
    public async Task Resize_256x256_To_1x1_AllMethods()
    {
        using var source = CreateFrame(256, 256, 32768);
        InterpolationMethod[] methods =
        [
            InterpolationMethod.NearestNeighbor,
            InterpolationMethod.Bilinear,
            InterpolationMethod.Lanczos3,
            InterpolationMethod.Mitchell,
        ];

        foreach (var method in methods)
        {
            using var resized = Resize.Apply(source, 1, 1, method);
            await Assert.That((int)resized.Columns).IsEqualTo(1);
            await Assert.That((int)resized.Rows).IsEqualTo(1);
            // Solid color resized to 1×1 should be close to original
            var row = resized.GetPixelRow(0);
            int diff = Math.Abs(row[0] - 32768);
            if (diff > 2000)
                throw new Exception($"{method} 256→1 solid color error: {diff}");
        }
    }

    [Test]
    public async Task Resize_1x1_To_256x256_AllMethods()
    {
        using var source = CreateFrame(1, 1, 32768);
        InterpolationMethod[] methods =
        [
            InterpolationMethod.NearestNeighbor,
            InterpolationMethod.Bilinear,
            InterpolationMethod.Lanczos3,
            InterpolationMethod.Mitchell,
        ];

        foreach (var method in methods)
        {
            using var resized = Resize.Apply(source, 256, 256, method);
            await Assert.That((int)resized.Columns).IsEqualTo(256);
            // All pixels should be the same as the source
            var row = resized.GetPixelRow(128);
            int diff = Math.Abs(row[128 * 3] - 32768);
            if (diff > 2000)
                throw new Exception($"{method} 1→256 error: {diff}");
        }
    }

    // ─── Odd dimensions ──────────────────────────────────────────

    [Test]
    public async Task OddDimensions_Resize_CorrectOutput()
    {
        using var source = CreateFrame(31, 17, 32768);
        using var resized = Resize.Apply(source, 13, 7, InterpolationMethod.Lanczos3);
        await Assert.That((int)resized.Columns).IsEqualTo(13);
        await Assert.That((int)resized.Rows).IsEqualTo(7);
    }

    [Test]
    public async Task PrimeDimensions_Resize_CorrectOutput()
    {
        using var source = CreateFrame(37, 41, 32768);
        using var resized = Resize.Apply(source, 19, 23, InterpolationMethod.Lanczos3);
        await Assert.That((int)resized.Columns).IsEqualTo(19);
        await Assert.That((int)resized.Rows).IsEqualTo(23);
    }

    // ─── 16-bit extreme values ───────────────────────────────────

    [Test]
    public async Task QuantumExtremes_Resize_Preserves()
    {
        // Create image with alternating 0 and MaxValue columns
        var frame = new ImageFrame();
        frame.Initialize(16, 8, ColorspaceType.SRGB, false);
        for (int y = 0; y < 8; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 16; x++)
            {
                ushort val = (x % 2 == 0) ? (ushort)0 : Quantum.MaxValue;
                row[x * 3] = val;
                row[x * 3 + 1] = val;
                row[x * 3 + 2] = val;
            }
        }

        using var resized = Resize.Apply(frame, 8, 4, InterpolationMethod.NearestNeighbor);
        var r = resized.GetPixelRow(0);

        // Nearest neighbor should pick one or the other exactly
        ushort v0 = r[0];
        bool isExact = v0 == 0 || v0 == Quantum.MaxValue;
        await Assert.That(isExact).IsTrue();
        frame.Dispose();
    }

    [Test]
    public async Task QuantumExtremes_ColorspaceRoundTrip()
    {
        // Test with max-value pixels in all colorspaces
        using var frame = CreateFrame(4, 4, Quantum.MaxValue);

        string[] safeColorspaces = ["lab", "xyz", "hsv", "hsl", "oklab", "yuv"];
        foreach (string cs in safeColorspaces)
        {
            var converted = ColorspaceOps.ConvertToColorspace(frame, cs);
            var back = ColorspaceOps.ConvertFromColorspace(converted, cs);

            var row = back.GetPixelRow(0);
            int diff = Math.Abs(row[0] - Quantum.MaxValue);
            if (diff > Quantum.MaxValue * 0.02)
                throw new Exception($"Extreme value round-trip for {cs}: diff={diff}");

            converted.Dispose();
            back.Dispose();
        }

        await Assert.That(true).IsTrue();
    }

    // ─── Grayscale image operations ──────────────────────────────

    [Test]
    public async Task Grayscale_Resize_DoesNotCrash()
    {
        var frame = new ImageFrame();
        frame.Initialize(32, 32, ColorspaceType.Gray, false);
        for (int y = 0; y < 32; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 32; x++)
                row[x] = (ushort)(x * 2048);
        }

        using var resized = Resize.Apply(frame, 16, 16, InterpolationMethod.Lanczos3);
        await Assert.That((int)resized.Columns).IsEqualTo(16);
        frame.Dispose();
    }

    [Test]
    public async Task Grayscale_Flip_DoesNotCrash()
    {
        var frame = new ImageFrame();
        frame.Initialize(8, 8, ColorspaceType.Gray, false);
        for (int y = 0; y < 8; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 8; x++)
                row[x] = (ushort)(y * 8192);
        }

        using var flipped = Geometry.Flip(frame);
        var topRow = flipped.GetPixelRow(0);
        var botRow = frame.GetPixelRow(7);
        await Assert.That(topRow[0]).IsEqualTo(botRow[0]);
        frame.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateFrame(int width, int height, ushort value)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                row[x * channels] = value;
                if (channels > 1) row[x * channels + 1] = value;
                if (channels > 2) row[x * channels + 2] = value;
            }
        }
        return frame;
    }

    private static ImageFrame CreateRgbaFrame(int width, int height, ushort r, ushort g, ushort b, ushort a)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = frame.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                row[x * channels] = r;
                row[x * channels + 1] = g;
                row[x * channels + 2] = b;
                row[x * channels + 3] = a;
            }
        }
        return frame;
    }
}
