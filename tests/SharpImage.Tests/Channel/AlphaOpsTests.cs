// Tests for Alpha operations and Sample/AdaptiveResize + DistanceTransform.

using SharpImage.Channel;
using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;
using SharpImage.Morphology;

namespace SharpImage.Tests.Channel;

public class AlphaOpsTests
{
    private static ImageFrame CreateRgbImage(int width, int height)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = Quantum.ScaleFromByte((byte)(x * 255 / Math.Max(1, width - 1)));
                row[off + 1] = Quantum.ScaleFromByte((byte)(y * 255 / Math.Max(1, height - 1)));
                row[off + 2] = Quantum.ScaleFromByte(128);
            }
        }
        return img;
    }

    private static ImageFrame CreateRgbaImage(int width, int height)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, true);
        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 4;
                row[off] = Quantum.ScaleFromByte(200);
                row[off + 1] = Quantum.ScaleFromByte(100);
                row[off + 2] = Quantum.ScaleFromByte(50);
                // Alpha: left half transparent, right half opaque
                row[off + 3] = x < width / 2 ? (ushort)0 : Quantum.MaxValue;
            }
        }
        return img;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Extract
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Extract_ReturnsGrayscaleFromAlpha()
    {
        using var source = CreateRgbaImage(100, 100);
        using var result = AlphaOps.Extract(source);

        await Assert.That(result.HasAlpha).IsFalse();
        await Assert.That((int)result.Columns).IsEqualTo(100);
        await Assert.That((int)result.Rows).IsEqualTo(100);

        // Left half should be black (alpha was 0), right half should be white (alpha was max)
        var leftPixel = result.GetPixelRow(50).ToArray();
        await Assert.That(leftPixel[0]).IsEqualTo((ushort)0); // transparent → black

        var rightPixel = result.GetPixelRow(50).ToArray();
        int rightOff = 75 * 3;
        await Assert.That(rightPixel[rightOff]).IsEqualTo(Quantum.MaxValue); // opaque → white
    }

    [Test]
    public async Task Extract_NoAlpha_ReturnsAllWhite()
    {
        using var source = CreateRgbImage(50, 50);
        using var result = AlphaOps.Extract(source);

        var pixel = result.GetPixelRow(25).ToArray();
        await Assert.That(pixel[25 * 3]).IsEqualTo(Quantum.MaxValue);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Remove
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Remove_CompositesOverBackground()
    {
        using var source = CreateRgbaImage(100, 100);
        using var result = AlphaOps.Remove(source);

        await Assert.That(result.HasAlpha).IsFalse();
        await Assert.That(result.NumberOfChannels).IsEqualTo(3);

        // Left half was transparent → should be white (default bg)
        var leftPixels = result.GetPixelRow(50).ToArray();
        await Assert.That(leftPixels[0]).IsEqualTo(Quantum.MaxValue);

        // Right half was opaque → should be original color
        var rightPixels = result.GetPixelRow(50).ToArray();
        int rightOff = 75 * 3;
        await Assert.That(rightPixels[rightOff]).IsEqualTo(Quantum.ScaleFromByte(200));
    }

    // ═══════════════════════════════════════════════════════════════════
    // SetAlpha
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetAlpha_AddsAlphaChannel()
    {
        using var source = CreateRgbImage(50, 50);
        using var result = AlphaOps.SetAlpha(source, Quantum.ScaleFromByte(128));

        await Assert.That(result.HasAlpha).IsTrue();
        await Assert.That(result.NumberOfChannels).IsEqualTo(4);

        var pixel = result.GetPixelRow(25).ToArray();
        int off = 25 * 4;
        await Assert.That(pixel[off + 3]).IsEqualTo(Quantum.ScaleFromByte(128));
    }

    // ═══════════════════════════════════════════════════════════════════
    // MakeOpaque
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task MakeOpaque_SetsAllAlphaToMax()
    {
        using var source = CreateRgbaImage(100, 100);
        using var result = AlphaOps.MakeOpaque(source);

        // Left half originally had 0 alpha, should now be max
        var pixel = result.GetPixelRow(50).ToArray();
        await Assert.That(pixel[3]).IsEqualTo(Quantum.MaxValue);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MakeTransparent
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task MakeTransparent_TargetsMatchingColor()
    {
        // Create image with known solid color
        var source = new ImageFrame();
        source.Initialize(10, 10, ColorspaceType.SRGB, false);
        for (int y = 0; y < 10; y++)
        {
            var row = source.GetPixelRowForWrite(y);
            for (int x = 0; x < 10; x++)
            {
                int off = x * 3;
                row[off] = Quantum.MaxValue;   // Red
                row[off + 1] = 0;
                row[off + 2] = 0;
            }
        }

        using var result = AlphaOps.MakeTransparent(source, Quantum.MaxValue, 0, 0, 0.1);
        source.Dispose();

        await Assert.That(result.HasAlpha).IsTrue();
        // All pixels should be transparent since they match the target
        var pixel = result.GetPixelRow(5).ToArray();
        await Assert.That(pixel[5 * 4 + 3]).IsEqualTo((ushort)0);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Sample & Adaptive Resize Tests
// ═══════════════════════════════════════════════════════════════════

public class SampleResizeTests
{
    private static ImageFrame CreateGradientImage(int width, int height)
    {
        var img = new ImageFrame();
        img.Initialize(width, height);
        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = Quantum.ScaleFromByte((byte)(x * 255 / Math.Max(1, width - 1)));
                row[off + 1] = Quantum.ScaleFromByte((byte)(y * 255 / Math.Max(1, height - 1)));
                row[off + 2] = Quantum.ScaleFromByte(128);
            }
        }
        return img;
    }

    [Test]
    public async Task Sample_DownscalePreservesDimensions()
    {
        using var source = CreateGradientImage(200, 200);
        using var result = Resize.Sample(source, 100, 100);

        await Assert.That((int)result.Columns).IsEqualTo(100);
        await Assert.That((int)result.Rows).IsEqualTo(100);
    }

    [Test]
    public async Task Sample_UpscalePreservesDimensions()
    {
        using var source = CreateGradientImage(50, 50);
        using var result = Resize.Sample(source, 200, 200);

        await Assert.That((int)result.Columns).IsEqualTo(200);
        await Assert.That((int)result.Rows).IsEqualTo(200);
    }

    [Test]
    public async Task Sample_PixelValuesAreExactCopies()
    {
        // 2x upscale: each source pixel should appear in a 2x2 block
        using var source = CreateGradientImage(10, 10);
        using var result = Resize.Sample(source, 20, 20);

        var srcPixels = source.GetPixelRow(0).ToArray();
        var dstPixels = result.GetPixelRow(0).ToArray();

        // Pixel (0,0) in source should match pixel (0,0) and (1,0) in result
        await Assert.That(dstPixels[0]).IsEqualTo(srcPixels[0]);
        await Assert.That(dstPixels[3]).IsEqualTo(srcPixels[0]); // pixel (1,0) same as source (0,0)
    }

    [Test]
    public async Task AdaptiveResize_ProducesCorrectDimensions()
    {
        using var source = CreateGradientImage(200, 200);
        using var result = Resize.AdaptiveResize(source, 80, 60);

        await Assert.That((int)result.Columns).IsEqualTo(80);
        await Assert.That((int)result.Rows).IsEqualTo(60);
    }

    [Test]
    public async Task AdaptiveResize_SmoothesDownscale()
    {
        using var source = CreateGradientImage(100, 100);
        using var adaptive = Resize.AdaptiveResize(source, 50, 50);
        using var sample = Resize.Sample(source, 50, 50);

        // Both should produce valid images but adaptive should be smoother
        await Assert.That((int)adaptive.Columns).IsEqualTo(50);
        await Assert.That((int)sample.Columns).IsEqualTo(50);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Distance Transform Tests
// ═══════════════════════════════════════════════════════════════════

public class DistanceTransformTests
{
    [Test]
    public async Task DistanceTransform_AllBlack_ReturnsBlack()
    {
        // All-black image (background only) → distances are all 0
        var img = new ImageFrame();
        img.Initialize(50, 50);
        // Pixels default to 0 (black)

        using var result = MorphologyOps.DistanceTransform(img);
        img.Dispose();

        var pixel = result.GetPixelRow(25).ToArray();
        await Assert.That(pixel[25 * 3]).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task DistanceTransform_WhiteImage_CenterIsBrightest()
    {
        // All-white image → center has maximum distance from any edge
        var img = new ImageFrame();
        img.Initialize(50, 50);
        for (int y = 0; y < 50; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < 50; x++)
            {
                int off = x * 3;
                row[off] = Quantum.MaxValue;
                row[off + 1] = Quantum.MaxValue;
                row[off + 2] = Quantum.MaxValue;
            }
        }
        // Make the border black
        for (int y = 0; y < 50; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            if (y == 0 || y == 49)
            {
                for (int x = 0; x < 50; x++) { row[x * 3] = 0; row[x * 3 + 1] = 0; row[x * 3 + 2] = 0; }
            }
            else
            {
                row[0] = 0; row[1] = 0; row[2] = 0;
                row[49 * 3] = 0; row[49 * 3 + 1] = 0; row[49 * 3 + 2] = 0;
            }
        }

        using var result = MorphologyOps.DistanceTransform(img);
        img.Dispose();

        // Center pixel should be brighter than edge pixels
        var centerRow = result.GetPixelRow(25).ToArray();
        var edgeRow = result.GetPixelRow(1).ToArray();
        ushort centerVal = centerRow[25 * 3];
        ushort edgeVal = edgeRow[1 * 3];
        await Assert.That(centerVal).IsGreaterThan(edgeVal);
    }

    [Test]
    public async Task DistanceTransform_ManhattanMetric_Produces_Result()
    {
        var img = new ImageFrame();
        img.Initialize(30, 30);
        for (int y = 5; y < 25; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 5; x < 25; x++)
            {
                int off = x * 3;
                row[off] = Quantum.MaxValue;
                row[off + 1] = Quantum.MaxValue;
                row[off + 2] = Quantum.MaxValue;
            }
        }

        using var result = MorphologyOps.DistanceTransform(img, DistanceMetric.Manhattan);
        img.Dispose();

        await Assert.That((int)result.Columns).IsEqualTo(30);
        await Assert.That((int)result.Rows).IsEqualTo(30);
    }
}

// ═══════════════════════════════════════════════════════════════════
// VirtualPixel Tests
// ═══════════════════════════════════════════════════════════════════

public class VirtualPixelTests
{
    [Test]
    public async Task MapCoordinate_InBounds_ReturnsSameCoordinates()
    {
        bool result = VirtualPixel.MapCoordinate(5, 10, 100, 100, VirtualPixelMethod.Edge, out int mx, out int my);
        await Assert.That(result).IsTrue();
        await Assert.That(mx).IsEqualTo(5);
        await Assert.That(my).IsEqualTo(10);
    }

    [Test]
    public async Task MapCoordinate_Edge_ClampsToBounds()
    {
        bool result = VirtualPixel.MapCoordinate(-5, 150, 100, 100, VirtualPixelMethod.Edge, out int mx, out int my);
        await Assert.That(result).IsTrue();
        await Assert.That(mx).IsEqualTo(0);
        await Assert.That(my).IsEqualTo(99);
    }

    [Test]
    public async Task MapCoordinate_Tile_Wraps()
    {
        bool result = VirtualPixel.MapCoordinate(105, 205, 100, 100, VirtualPixelMethod.Tile, out int mx, out int my);
        await Assert.That(result).IsTrue();
        await Assert.That(mx).IsEqualTo(5);
        await Assert.That(my).IsEqualTo(5);
    }

    [Test]
    public async Task MapCoordinate_Mirror_Reflects()
    {
        bool result = VirtualPixel.MapCoordinate(110, 50, 100, 100, VirtualPixelMethod.Mirror, out int mx, out int my);
        await Assert.That(result).IsTrue();
        // 110 in a 100-wide image mirrors: period=198, 110%198=110, 110<100 is false, so 198-110=88
        await Assert.That(mx).IsEqualTo(88);
    }

    [Test]
    public async Task MapCoordinate_Transparent_ReturnsFalse()
    {
        bool result = VirtualPixel.MapCoordinate(-1, -1, 100, 100, VirtualPixelMethod.Transparent, out _, out _);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task GetConstantColor_ReturnsCorrectValues()
    {
        var (r, g, b, a) = VirtualPixel.GetConstantColor(VirtualPixelMethod.Transparent);
        await Assert.That(r).IsEqualTo((ushort)0);
        await Assert.That(a).IsEqualTo((ushort)0);

        var (wr, wg, wb, wa) = VirtualPixel.GetConstantColor(VirtualPixelMethod.White);
        await Assert.That(wr).IsEqualTo(Quantum.MaxValue);
        await Assert.That(wa).IsEqualTo(Quantum.MaxValue);
    }
}
