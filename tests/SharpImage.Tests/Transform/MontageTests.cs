using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for Append, Montage (Tile), and Coalesce operations.
/// </summary>
public class MontageTests
{
    private static readonly string TestAssetsDir = Path.Combine(
        AppContext.BaseDirectory, "TestAssets");

    // ─── Append ────────────────────────────────────────────────────

    [Test]
    public async Task Append_Horizontal_SumOfWidths()
    {
        using var a = CreateSolidImage(100, 50, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(80, 50, 0, Quantum.MaxValue, 0);
        var images = new[] { a, b };

        using var result = Montage.Append(images, horizontal: true);

        await Assert.That(result.Columns).IsEqualTo(180L);
        await Assert.That(result.Rows).IsEqualTo(50L);
    }

    [Test]
    public async Task Append_Vertical_SumOfHeights()
    {
        using var a = CreateSolidImage(100, 50, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(100, 70, 0, 0, Quantum.MaxValue);
        var images = new[] { a, b };

        using var result = Montage.Append(images, horizontal: false);

        await Assert.That(result.Columns).IsEqualTo(100L);
        await Assert.That(result.Rows).IsEqualTo(120L);
    }

    [Test]
    public async Task Append_Horizontal_DifferentHeights_UsesMaxHeight()
    {
        using var a = CreateSolidImage(50, 30, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(50, 60, 0, Quantum.MaxValue, 0);
        using var c = CreateSolidImage(50, 45, 0, 0, Quantum.MaxValue);
        var images = new[] { a, b, c };

        using var result = Montage.Append(images, horizontal: true);

        await Assert.That(result.Columns).IsEqualTo(150L);
        await Assert.That(result.Rows).IsEqualTo(60L);
    }

    [Test]
    public async Task Append_Horizontal_PixelsCorrect()
    {
        using var red = CreateSolidImage(10, 10, Quantum.MaxValue, 0, 0);
        using var green = CreateSolidImage(10, 10, 0, Quantum.MaxValue, 0);
        var images = new[] { red, green };

        using var result = Montage.Append(images, horizontal: true);

        // Check pixel in left half (red)
        var leftRow = result.GetPixelRow(5);
        int ch = result.NumberOfChannels;
        ushort leftR = leftRow[5 * ch];
        ushort leftG = leftRow[5 * ch + 1];
        ushort rightR = leftRow[15 * ch];
        ushort rightG = leftRow[15 * ch + 1];

        await Assert.That(leftR).IsEqualTo(Quantum.MaxValue);  // R
        await Assert.That(leftG).IsEqualTo((ushort)0);          // G
        await Assert.That(rightR).IsEqualTo((ushort)0);          // R
        await Assert.That(rightG).IsEqualTo(Quantum.MaxValue);  // G
    }

    [Test]
    public async Task Append_SingleImage_ReturnsClone()
    {
        using var source = CreateSolidImage(50, 50, Quantum.MaxValue, 0, 0);
        var images = new[] { source };

        using var result = Montage.Append(images, horizontal: true);

        await Assert.That(result.Columns).IsEqualTo(50L);
        await Assert.That(result.Rows).IsEqualTo(50L);
    }

    [Test]
    public void Append_EmptyArray_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var images = Array.Empty<ImageFrame>();
            Montage.Append(images, horizontal: true);
        });
    }

    // ─── Tile (Montage) ────────────────────────────────────────────

    [Test]
    public async Task Tile_2x2Grid_CorrectDimensions()
    {
        using var a = CreateSolidImage(50, 50, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(50, 50, 0, Quantum.MaxValue, 0);
        using var c = CreateSolidImage(50, 50, 0, 0, Quantum.MaxValue);
        using var d = CreateSolidImage(50, 50, Quantum.MaxValue, Quantum.MaxValue, 0);
        var images = new[] { a, b, c, d };

        using var result = Montage.Tile(images, columns: 2, spacing: 0);

        await Assert.That(result.Columns).IsEqualTo(100L);
        await Assert.That(result.Rows).IsEqualTo(100L);
    }

    [Test]
    public async Task Tile_WithSpacing_IncludesGaps()
    {
        using var a = CreateSolidImage(50, 50, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(50, 50, 0, Quantum.MaxValue, 0);
        using var c = CreateSolidImage(50, 50, 0, 0, Quantum.MaxValue);
        using var d = CreateSolidImage(50, 50, Quantum.MaxValue, Quantum.MaxValue, 0);
        var images = new[] { a, b, c, d };

        using var result = Montage.Tile(images, columns: 2, spacing: 10);

        // 2*50 + 1*10 = 110 width, 2*50 + 1*10 = 110 height
        await Assert.That(result.Columns).IsEqualTo(110L);
        await Assert.That(result.Rows).IsEqualTo(110L);
    }

    [Test]
    public async Task Tile_AutoColumns_RoughlySquare()
    {
        var images = new ImageFrame[9];
        for (int i = 0; i < 9; i++)
            images[i] = CreateSolidImage(20, 20, (ushort)(i * 7000), 0, 0);

        using var result = Montage.Tile(images, columns: 0, spacing: 0);

        // sqrt(9) = 3, so 3x3 grid = 60x60
        await Assert.That(result.Columns).IsEqualTo(60L);
        await Assert.That(result.Rows).IsEqualTo(60L);

        foreach (var img in images) img.Dispose();
    }

    [Test]
    public async Task Tile_3Images2Cols_ProducesCorrectGrid()
    {
        using var a = CreateSolidImage(40, 40, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(40, 40, 0, Quantum.MaxValue, 0);
        using var c = CreateSolidImage(40, 40, 0, 0, Quantum.MaxValue);
        var images = new[] { a, b, c };

        using var result = Montage.Tile(images, columns: 2, spacing: 0);

        // 2 cols, 2 rows (ceil(3/2)), each 40x40
        await Assert.That(result.Columns).IsEqualTo(80L);
        await Assert.That(result.Rows).IsEqualTo(80L);
    }

    [Test]
    public async Task Tile_DifferentSizes_CentersInCells()
    {
        using var big = CreateSolidImage(60, 60, Quantum.MaxValue, 0, 0);
        using var small = CreateSolidImage(30, 30, 0, Quantum.MaxValue, 0);
        var images = new[] { big, small };

        using var result = Montage.Tile(images, columns: 2, spacing: 0);

        // Max tile is 60x60, grid is 120x60
        await Assert.That(result.Columns).IsEqualTo(120L);
        await Assert.That(result.Rows).IsEqualTo(60L);
    }

    [Test]
    public async Task Tile_WithBackground_FillsGaps()
    {
        using var a = CreateSolidImage(40, 40, Quantum.MaxValue, 0, 0);
        using var b = CreateSolidImage(40, 40, 0, Quantum.MaxValue, 0);
        using var c = CreateSolidImage(40, 40, 0, 0, Quantum.MaxValue);
        var images = new[] { a, b, c };

        ushort gray = Quantum.ScaleFromByte(128);
        using var result = Montage.Tile(images, columns: 2, spacing: 4, backgroundColor: gray);

        // Grid: 2 cols, 2 rows => 2*40+4 = 84 wide, 2*40+4 = 84 tall
        await Assert.That(result.Columns).IsEqualTo(84L);
        await Assert.That(result.Rows).IsEqualTo(84L);

        // Check that the empty cell area (bottom-right) has background color
        int ch = result.NumberOfChannels;
        var lastRow = result.GetPixelRow((int)result.Rows - 1);
        int lastPixelOff = ((int)result.Columns - 1) * ch;
        await Assert.That(lastRow[lastPixelOff]).IsEqualTo(gray);
    }

    // ─── Coalesce ────────────────────────────────────────────────

    [Test]
    public async Task Coalesce_ProducesFullCanvasFrames()
    {
        // Create a simple 2-frame sequence
        var seq = new ImageSequence { CanvasWidth = 100, CanvasHeight = 100 };

        var frame1 = CreateSolidImage(50, 50, Quantum.MaxValue, 0, 0);
        frame1.Delay = 10;
        frame1.DisposeMethod = DisposeType.None;
        seq.AddFrame(frame1);

        var frame2 = CreateSolidImage(50, 50, 0, Quantum.MaxValue, 0);
        frame2.Delay = 10;
        frame2.Page = new FrameOffset(50, 50);
        frame2.DisposeMethod = DisposeType.None;
        seq.AddFrame(frame2);

        using var coalesced = Montage.Coalesce(seq);

        // Both output frames should be full canvas size
        await Assert.That(coalesced.Count).IsEqualTo(2);
        await Assert.That(coalesced[0].Columns).IsEqualTo(100L);
        await Assert.That(coalesced[0].Rows).IsEqualTo(100L);
        await Assert.That(coalesced[1].Columns).IsEqualTo(100L);
        await Assert.That(coalesced[1].Rows).IsEqualTo(100L);

        seq.Dispose();
    }

    [Test]
    public async Task Coalesce_PreservesDelays()
    {
        var seq = new ImageSequence { CanvasWidth = 50, CanvasHeight = 50 };

        var frame1 = CreateSolidImage(50, 50, Quantum.MaxValue, 0, 0);
        frame1.Delay = 5;
        seq.AddFrame(frame1);

        var frame2 = CreateSolidImage(50, 50, 0, Quantum.MaxValue, 0);
        frame2.Delay = 15;
        seq.AddFrame(frame2);

        using var coalesced = Montage.Coalesce(seq);

        await Assert.That(coalesced[0].Delay).IsEqualTo(5);
        await Assert.That(coalesced[1].Delay).IsEqualTo(15);

        seq.Dispose();
    }

    [Test]
    public async Task Coalesce_BackgroundDispose_ClearsFrame()
    {
        var seq = new ImageSequence { CanvasWidth = 50, CanvasHeight = 50 };

        var frame1 = CreateSolidImage(50, 50, Quantum.MaxValue, 0, 0);
        frame1.Delay = 10;
        frame1.DisposeMethod = DisposeType.Background;
        seq.AddFrame(frame1);

        var frame2 = CreateSolidImage(25, 25, 0, Quantum.MaxValue, 0);
        frame2.Delay = 10;
        frame2.Page = new FrameOffset(0, 0);
        frame2.DisposeMethod = DisposeType.None;
        seq.AddFrame(frame2);

        using var coalesced = Montage.Coalesce(seq);

        // Frame 1 should be all red (full canvas)
        int ch = coalesced[0].NumberOfChannels;
        var row0 = coalesced[0].GetPixelRow(25);
        await Assert.That(row0[25 * ch]).IsEqualTo(Quantum.MaxValue); // R

        // Frame 2: after background dispose, canvas was cleared,
        // then green 25x25 composited at (0,0).
        // Pixel at (30,30) should be transparent/black (cleared area)
        var row1 = coalesced[1].GetPixelRow(30);
        await Assert.That(row1[30 * ch]).IsEqualTo((ushort)0); // R = 0

        seq.Dispose();
    }

    [Test]
    public async Task Coalesce_RealGif_ProducesFullFrames()
    {
        string gifPath = Path.Combine(TestAssetsDir, "bounce_anim.gif");
        if (!File.Exists(gifPath))
        {
            return;
        }

        using var seq = GifCoder.ReadSequence(gifPath);
        if (seq.Count < 2)
        {
            return;
        }

        using var coalesced = Montage.Coalesce(seq);

        await Assert.That(coalesced.Count).IsEqualTo(seq.Count);
        // All frames should be full canvas
        for (int i = 0; i < coalesced.Count; i++)
        {
            await Assert.That(coalesced[i].Columns).IsEqualTo((long)seq.CanvasWidth);
            await Assert.That(coalesced[i].Rows).IsEqualTo((long)seq.CanvasHeight);
        }
    }

    // ─── Real Image Tests ──────────────────────────────────────────

    [Test]
    public async Task Append_RealImages_Horizontal()
    {
        string wizardPath = Path.Combine(TestAssetsDir, "peppers_rgba.png");
        string rosePath = Path.Combine(TestAssetsDir, "photo_small.png");

        using var wizard = FormatRegistry.Read(wizardPath);
        using var rose = FormatRegistry.Read(rosePath);
        var images = new[] { wizard, rose };

        using var result = Montage.Append(images, horizontal: true);

        await Assert.That(result.Columns).IsEqualTo(wizard.Columns + rose.Columns);
        await Assert.That(result.Rows).IsEqualTo(Math.Max(wizard.Rows, rose.Rows));
    }

    [Test]
    public async Task Tile_RealImages_Grid()
    {
        string wizardPath = Path.Combine(TestAssetsDir, "peppers_rgba.png");
        string rosePath = Path.Combine(TestAssetsDir, "photo_small.png");
        string logoPath = Path.Combine(TestAssetsDir, "scene.png");

        using var wizard = FormatRegistry.Read(wizardPath);
        using var rose = FormatRegistry.Read(rosePath);
        using var logo = FormatRegistry.Read(logoPath);
        var images = new[] { wizard, rose, logo };

        using var result = Montage.Tile(images, columns: 2, spacing: 4);

        // Should be 2 cols, 2 rows
        long maxW = Math.Max(wizard.Columns, Math.Max(rose.Columns, logo.Columns));
        long maxH = Math.Max(wizard.Rows, Math.Max(rose.Rows, logo.Rows));
        await Assert.That(result.Columns).IsEqualTo(maxW * 2 + 4);
        await Assert.That(result.Rows).IsEqualTo(maxH * 2 + 4);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static ImageFrame CreateSolidImage(int width, int height, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = r;
                row[off + 1] = g;
                row[off + 2] = b;
            }
        }

        return frame;
    }
}
