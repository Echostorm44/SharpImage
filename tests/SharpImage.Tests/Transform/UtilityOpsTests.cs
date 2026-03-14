using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;
using SharpImage.Threshold;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for Phase 47 utility operations: Thumbnail, Resample, Magnify, Minify, CropToTiles,
/// TextureImage, RemapImage, RangeThreshold.
/// </summary>
public class UtilityOpsTests
{
    // ─── Thumbnail ────────────────────────────────────────────────

    [Test]
    public async Task Thumbnail_PreservesAspectRatio_Landscape()
    {
        using var source = CreateGradient(200, 100);
        using var result = Resize.Thumbnail(source, 50, 50);

        await Assert.That((int)result.Columns).IsEqualTo(50);
        await Assert.That((int)result.Rows).IsEqualTo(25);
    }

    [Test]
    public async Task Thumbnail_PreservesAspectRatio_Portrait()
    {
        using var source = CreateGradient(100, 200);
        using var result = Resize.Thumbnail(source, 50, 50);

        await Assert.That((int)result.Columns).IsEqualTo(25);
        await Assert.That((int)result.Rows).IsEqualTo(50);
    }

    [Test]
    public async Task Thumbnail_StripsMetadata()
    {
        using var source = CreateGradient(100, 100);
        source.Metadata.Xmp = "<xmp>test</xmp>";

        using var result = Resize.Thumbnail(source, 50, 50);

        await Assert.That(result.Metadata.Xmp).IsNull();
        await Assert.That(result.Metadata.ExifProfile).IsNull();
    }

    // ─── Resample ─────────────────────────────────────────────────

    [Test]
    public async Task Resample_HalvesDpi_HalvesDimensions()
    {
        using var source = CreateGradient(100, 100);
        source.ResolutionX = 200.0;
        source.ResolutionY = 200.0;

        using var result = Resize.Resample(source, 100.0, 100.0);

        await Assert.That((int)result.Columns).IsEqualTo(50);
        await Assert.That((int)result.Rows).IsEqualTo(50);
        await Assert.That(result.ResolutionX).IsEqualTo(100.0);
    }

    [Test]
    public async Task Resample_DoubleDpi_DoublesDimensions()
    {
        using var source = CreateGradient(50, 50);
        source.ResolutionX = 72.0;
        source.ResolutionY = 72.0;

        using var result = Resize.Resample(source, 144.0, 144.0);

        await Assert.That((int)result.Columns).IsEqualTo(100);
        await Assert.That((int)result.Rows).IsEqualTo(100);
    }

    // ─── Magnify / Minify ─────────────────────────────────────────

    [Test]
    public async Task Magnify_DoublesDimensions()
    {
        using var source = CreateGradient(32, 24);
        using var result = Resize.Magnify(source);

        await Assert.That((int)result.Columns).IsEqualTo(64);
        await Assert.That((int)result.Rows).IsEqualTo(48);
    }

    [Test]
    public async Task Minify_HalvesDimensions()
    {
        using var source = CreateGradient(64, 48);
        using var result = Resize.Minify(source);

        await Assert.That((int)result.Columns).IsEqualTo(32);
        await Assert.That((int)result.Rows).IsEqualTo(24);
    }

    // ─── CropToTiles ──────────────────────────────────────────────

    [Test]
    public async Task CropToTiles_EvenDivision_ProducesCorrectCount()
    {
        using var source = CreateGradient(64, 64);
        var tiles = Geometry.CropToTiles(source, 32, 32);

        await Assert.That(tiles.Count).IsEqualTo(4);
        await Assert.That((int)tiles[0].Columns).IsEqualTo(32);
        await Assert.That((int)tiles[0].Rows).IsEqualTo(32);
        foreach (var t in tiles) t.Dispose();
    }

    [Test]
    public async Task CropToTiles_UnevenDivision_ProducesEdgeTiles()
    {
        using var source = CreateGradient(50, 50);
        var tiles = Geometry.CropToTiles(source, 32, 32);

        // 2x2 grid: 32+18=50
        await Assert.That(tiles.Count).IsEqualTo(4);
        // Bottom-right tile should be 18x18
        await Assert.That((int)tiles[3].Columns).IsEqualTo(18);
        await Assert.That((int)tiles[3].Rows).IsEqualTo(18);
        foreach (var t in tiles) t.Dispose();
    }

    // ─── TextureImage ─────────────────────────────────────────────

    [Test]
    public async Task TextureImage_TilesTexture()
    {
        using var target = CreateSolid(32, 32, 0, 0, 0);
        using var texture = CreateColor(8, 8, Quantum.MaxValue, 0, 0);

        using var result = UtilityOps.TextureImage(target, texture);

        // Result should be same size as target
        await Assert.That((int)result.Columns).IsEqualTo(32);
        await Assert.That((int)result.Rows).IsEqualTo(32);

        // All pixels should be red (from texture)
        var row = result.GetPixelRow(0).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That(row[0]).IsEqualTo(Quantum.MaxValue); // Red
        await Assert.That(row[1]).IsEqualTo((ushort)0);         // Green
        await Assert.That(row[2]).IsEqualTo((ushort)0);         // Blue
    }

    [Test]
    public async Task TextureImage_RepeatsPattern()
    {
        using var target = CreateSolid(16, 16, 0, 0, 0);
        // 4x4 checkerboard texture
        using var texture = CreateCheckerboard(4, 4);

        using var result = UtilityOps.TextureImage(target, texture);

        // Check that pattern repeats: pixel (0,0) should equal pixel (4,0), pixel (8,0), etc.
        var row = result.GetPixelRow(0).ToArray();
        int ch = result.NumberOfChannels;
        await Assert.That(row[0]).IsEqualTo(row[4 * ch]);
        await Assert.That(row[0]).IsEqualTo(row[8 * ch]);
        await Assert.That(row[0]).IsEqualTo(row[12 * ch]);
    }

    // ─── RemapImage ───────────────────────────────────────────────

    [Test]
    public async Task RemapImage_MapsToNearestPaletteColor()
    {
        // Create a gradient image
        using var source = CreateGradient(32, 1);

        // Remap to 2-color palette: black and white
        var palette = new PaletteExtraction.PaletteColor[]
        {
            new(0f, 0f, 0f, 50),
            new(1f, 1f, 1f, 50)
        };

        using var result = UtilityOps.RemapImage(source, palette, useDither: false);

        var row = result.GetPixelRow(0).ToArray();
        int ch = result.NumberOfChannels;
        // First pixel should be black (near 0)
        await Assert.That(row[0]).IsEqualTo((ushort)0);
        // Last pixel should be white (near max)
        await Assert.That(row[(31) * ch]).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task RemapImage_WithDither_ProducesResult()
    {
        using var source = CreateGradient(32, 32);

        var palette = new PaletteExtraction.PaletteColor[]
        {
            new(0f, 0f, 0f, 50),
            new(1f, 1f, 1f, 50)
        };

        using var result = UtilityOps.RemapImage(source, palette, useDither: true);

        await Assert.That((int)result.Columns).IsEqualTo(32);
        await Assert.That((int)result.Rows).IsEqualTo(32);
    }

    // ─── RangeThreshold ───────────────────────────────────────────

    [Test]
    public async Task RangeThreshold_BlackOutsideHardBounds()
    {
        // Create image with 3 distinct brightness levels
        var image = CreateGradient(100, 1);

        ThresholdOps.RangeThreshold(image, 0.3, 0.4, 0.6, 0.7);

        var row = image.GetPixelRow(0).ToArray();
        int ch = image.NumberOfChannels;

        // Pixel at x=0 (brightness ~0.0) should be black (below hardLow=0.3)
        await Assert.That(row[0]).IsEqualTo((ushort)0);
        // Pixel at x=99 (brightness ~1.0) should be black (above hardHigh=0.7)
        await Assert.That(row[99 * ch]).IsEqualTo((ushort)0);
        // Pixel at x=50 (brightness ~0.505) should be white (between softLow=0.4 and softHigh=0.6)
        await Assert.That(row[50 * ch]).IsEqualTo(Quantum.MaxValue);

        image.Dispose();
    }

    [Test]
    public async Task RangeThreshold_SoftGradientInTransitionZone()
    {
        var image = CreateGradient(100, 1);

        ThresholdOps.RangeThreshold(image, 0.2, 0.4, 0.6, 0.8);

        var row = image.GetPixelRow(0).ToArray();
        int ch = image.NumberOfChannels;

        // x=30 => brightness ~0.303, in soft zone [0.2..0.4], should be partial
        ushort softValue = row[30 * ch];
        await Assert.That(softValue).IsGreaterThan((ushort)0);
        await Assert.That(softValue).IsLessThan(Quantum.MaxValue);

        image.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateSolid(int width, int height, ushort r, ushort g, ushort b)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * ch;
                row[off] = r; row[off + 1] = g; row[off + 2] = b;
            }
        }
        return image;
    }

    private static ImageFrame CreateColor(int width, int height, ushort r, ushort g, ushort b)
    {
        return CreateSolid(width, height, r, g, b);
    }

    private static ImageFrame CreateGradient(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (ushort)(Quantum.MaxValue * x / Math.Max(width - 1, 1));
                int off = x * ch;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateCheckerboard(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = ((x + y) % 2 == 0) ? Quantum.MaxValue : (ushort)0;
                int off = x * ch;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return image;
    }
}
