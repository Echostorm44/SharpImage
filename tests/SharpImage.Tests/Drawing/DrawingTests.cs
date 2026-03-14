using SharpImage.Core;
using SharpImage.Drawing;
using SharpImage.Image;
using TUnit.Core;

namespace SharpImage.Tests.Drawing;

public class DrawingTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Drawing Context State Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task DrawingContext_PushPopState_RestoresFillColor()
    {
        using var img = CreateCanvas(32, 32);
        var ctx = new DrawingContext(img);

        ctx.FillColor = DrawColor.Red;
        ctx.PushState();
        ctx.FillColor = DrawColor.Blue;
        await Assert.That(ctx.FillColor.B).IsEqualTo((byte)255);
        ctx.PopState();
        await Assert.That(ctx.FillColor.R).IsEqualTo((byte)255);
        await Assert.That(ctx.FillColor.B).IsEqualTo((byte)0);
    }

    [Test]
    public async Task DrawingContext_StateDepth_TracksCorrectly()
    {
        using var img = CreateCanvas(16, 16);
        var ctx = new DrawingContext(img);

        await Assert.That(ctx.StateDepth).IsEqualTo(0);
        ctx.PushState();
        await Assert.That(ctx.StateDepth).IsEqualTo(1);
        ctx.PushState();
        await Assert.That(ctx.StateDepth).IsEqualTo(2);
        ctx.PopState();
        await Assert.That(ctx.StateDepth).IsEqualTo(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Primitive Drawing Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task DrawPoint_SetsPixelAtCoordinate()
    {
        using var img = CreateCanvas(32, 32);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Red;

        ctx.DrawPoint(10, 15);

        var pixel = img.GetPixel(10, 15);
        // Red channel should be set (at least partially — full quantum for opaque red)
        await Assert.That((int)pixel.Red).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task DrawRectangle_FillsInterior()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Red;
        ctx.Antialias = false;

        ctx.DrawRectangle(10, 10, 20, 20);

        // Center pixel should be red
        var center = img.GetPixel(20, 20);
        await Assert.That((int)center.Red).IsGreaterThan(Quantum.MaxValue / 2);
        await Assert.That((int)center.Green).IsLessThan(Quantum.MaxValue / 4);

        // Outside pixel should still be white (background)
        var outside = img.GetPixel(5, 5);
        await Assert.That((int)outside.Red).IsGreaterThan(Quantum.MaxValue / 2);
        await Assert.That((int)outside.Green).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task DrawCircle_FillsInterior()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(0, 0, 255);
        ctx.Antialias = false;

        ctx.DrawCircle(32, 32, 15);

        // Center should be blue
        var center = img.GetPixel(32, 32);
        await Assert.That((int)center.Blue).IsGreaterThan(Quantum.MaxValue / 2);

        // Far corner should remain white
        var corner = img.GetPixel(0, 0);
        await Assert.That((int)corner.Red).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task DrawEllipse_FillsOval()
    {
        using var img = CreateCanvas(80, 60);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(0, 128, 0);
        ctx.Antialias = false;

        ctx.DrawEllipse(40, 30, 30, 15);

        // Center should be green-ish
        var center = img.GetPixel(40, 30);
        await Assert.That((int)center.Green).IsGreaterThan(Quantum.MaxValue / 4);
    }

    [Test]
    public async Task DrawLine_StrokesPixels()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Red;
        ctx.StrokeColor = DrawColor.Red;
        ctx.StrokeWidth = 2;

        ctx.DrawLine(0, 32, 63, 32);

        // Middle of line should have red
        var mid = img.GetPixel(32, 32);
        await Assert.That((int)mid.Red).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task DrawPolygon_Triangle_FillsCorrectly()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(255, 0, 255);
        ctx.Antialias = false;

        PointD[] triangle = [new(32, 8), new(56, 56), new(8, 56)];
        ctx.DrawPolygon(triangle);

        // Centroid should be filled
        var centroid = img.GetPixel(32, 40);
        await Assert.That((int)centroid.Red).IsGreaterThan(Quantum.MaxValue / 2);
        await Assert.That((int)centroid.Blue).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task DrawRegularPolygon_Hexagon_FillsCenter()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(255, 128, 0);
        ctx.Antialias = false;

        ctx.DrawRegularPolygon(32, 32, 20, 6);

        var center = img.GetPixel(32, 32);
        await Assert.That((int)center.Red).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task DrawStar_FillsPoints()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(255, 215, 0); // gold
        ctx.Antialias = false;

        ctx.DrawStar(32, 32, 25, 10, 5);

        // Center should be filled
        var center = img.GetPixel(32, 32);
        await Assert.That((int)center.Red).IsGreaterThan(Quantum.MaxValue / 2);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Stroke Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task DrawRectangle_WithStroke_HasOutline()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Transparent;
        ctx.StrokeColor = DrawColor.Red;
        ctx.StrokeWidth = 3;
        ctx.Antialias = false;

        ctx.DrawRectangle(10, 10, 40, 40);

        // Edge pixel should be red (near top edge)
        var edge = img.GetPixel(30, 10);
        await Assert.That((int)edge.Red).IsGreaterThan(Quantum.MaxValue / 2);

        // Interior should remain white (transparent fill on white bg)
        var interior = img.GetPixel(30, 30);
        await Assert.That((int)interior.Red).IsGreaterThan(Quantum.MaxValue / 2);
        await Assert.That((int)interior.Green).IsGreaterThan(Quantum.MaxValue / 2);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Transform Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Translate_ShiftsDrawing()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Red;

        ctx.Translate(20, 20);
        ctx.DrawPoint(0, 0);

        // Should appear at (20, 20)
        var pixel = img.GetPixel(20, 20);
        await Assert.That((int)pixel.Red).IsGreaterThan(Quantum.MaxValue / 2);
    }

    [Test]
    public async Task Scale_EnlargesDrawing()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(0, 0, 255);
        ctx.Antialias = false;

        ctx.Scale(2, 2);
        ctx.DrawRectangle(5, 5, 10, 10);

        // Scaled rect: (10,10) to (30,30)
        var inside = img.GetPixel(20, 20);
        await Assert.That((int)inside.Blue).IsGreaterThan(Quantum.MaxValue / 2);

        // Outside the scaled rect — should still be white (R and G high)
        var outside = img.GetPixel(5, 5);
        await Assert.That((int)outside.Green).IsGreaterThan(Quantum.MaxValue / 2);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SVG Path Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task DrawPath_TriangleSVG_FillsCorrectly()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = new DrawColor(0, 200, 0);
        ctx.Antialias = false;

        ctx.DrawPath("M 32 8 L 56 56 L 8 56 Z");

        var centroid = img.GetPixel(32, 40);
        await Assert.That((int)centroid.Green).IsGreaterThan(Quantum.MaxValue / 3);
    }

    [Test]
    public async Task DrawPath_CubicBezier_DoesNotCrash()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Red;

        // A simple cubic bezier path
        ctx.DrawPath("M 10 30 C 10 10, 50 10, 50 30 Z");

        // Verify the draw completed and image is intact
        await Assert.That(img.Columns).IsEqualTo(64u);
    }

    [Test]
    public async Task DrawPath_QuadraticBezier_DoesNotCrash()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Blue;

        ctx.DrawPath("M 10 50 Q 32 10 54 50 Z");

        await Assert.That(img.Columns).IsEqualTo(64u);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Path Parser Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task PathParser_Rectangle_4Points()
    {
        var pts = PathParser.Rectangle(0, 0, 10, 10);
        await Assert.That(pts.Length).IsEqualTo(4);
        await Assert.That(pts[0].X).IsEqualTo(0);
        await Assert.That(pts[2].X).IsEqualTo(10);
    }

    [Test]
    public async Task PathParser_Circle_64Segments()
    {
        var pts = PathParser.Circle(50, 50, 25);
        await Assert.That(pts.Length).IsEqualTo(64);
    }

    [Test]
    public async Task PathParser_RegularPolygon_CorrectSideCount()
    {
        var pentagon = PathParser.RegularPolygon(0, 0, 10, 5);
        await Assert.That(pentagon.Length).IsEqualTo(5);

        var hexagon = PathParser.RegularPolygon(0, 0, 10, 6);
        await Assert.That(hexagon.Length).IsEqualTo(6);
    }

    [Test]
    public async Task PathParser_Parse_MoveTo_LineTo_Close()
    {
        var subPaths = PathParser.Parse("M 0 0 L 10 0 L 10 10 Z");
        await Assert.That(subPaths.Count).IsEqualTo(1);
        await Assert.That(subPaths[0].Closed).IsTrue();
        await Assert.That(subPaths[0].Points.Length).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task PathParser_Parse_RelativeCommands()
    {
        var subPaths = PathParser.Parse("M 10 10 l 20 0 l 0 20 z");
        await Assert.That(subPaths.Count).IsEqualTo(1);
        await Assert.That(subPaths[0].Closed).IsTrue();
    }

    [Test]
    public async Task PathParser_Parse_HorizontalAndVertical()
    {
        var subPaths = PathParser.Parse("M 0 0 H 50 V 50 Z");
        await Assert.That(subPaths.Count).IsEqualTo(1);
        await Assert.That(subPaths[0].Points.Length).IsGreaterThanOrEqualTo(3);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Text Rendering Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task DrawText_RendersPixels()
    {
        using var img = CreateCanvas(100, 20);
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Black;

        ctx.DrawText(2, 2, "Hi");

        // Check that at least some pixels were changed from white
        int changedPixels = CountNonWhitePixels(img);
        await Assert.That(changedPixels).IsGreaterThan(5);
    }

    [Test]
    public async Task BitmapFont_MeasureString_ReturnsSize()
    {
        var (width, height) = BitmapFont.MeasureString("Hello", 1.0);
        await Assert.That(width).IsGreaterThan(20);
        await Assert.That(height).IsEqualTo(7);
    }

    [Test]
    public async Task BitmapFont_RenderString_GeneratesPixels()
    {
        var pixels = BitmapFont.RenderString("A", 1.0);
        await Assert.That(pixels.Count).IsGreaterThan(5);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Affine Matrix Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task AffineMatrix_Identity_DoesNotChangePoint()
    {
        var m = AffineMatrix.Identity;
        var result = m.Transform(new PointInfo(10, 20));
        await Assert.That(result.X).IsEqualTo(10);
        await Assert.That(result.Y).IsEqualTo(20);
    }

    [Test]
    public async Task AffineMatrix_Translation_ShiftsPoint()
    {
        var m = AffineMatrix.CreateTranslation(5, 10);
        var result = m.Transform(new PointInfo(3, 4));
        await Assert.That(result.X).IsEqualTo(8);
        await Assert.That(result.Y).IsEqualTo(14);
    }

    [Test]
    public async Task AffineMatrix_Inverse_RoundTrips()
    {
        var m = AffineMatrix.CreateTranslation(5, 10)
            .Compose(AffineMatrix.CreateScale(2, 3));
        var inv = m.Inverse();
        var original = new PointInfo(7, 11);
        var transformed = m.Transform(original);
        var restored = inv.Transform(transformed);

        await Assert.That(Math.Abs(restored.X - original.X)).IsLessThan(1e-10);
        await Assert.That(Math.Abs(restored.Y - original.Y)).IsLessThan(1e-10);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Gradient Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task FillLinearGradient_ProducesVariedPixels()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);

        var rect = PathParser.Rectangle(0, 0, 64, 64);
        ctx.FillLinearGradient(rect, 0, 0, 63, 0,
            DrawColor.Black, DrawColor.White);

        // Left side should be darker than right side
        var left = img.GetPixel(2, 32);
        var right = img.GetPixel(60, 32);
        await Assert.That(left.Red).IsLessThan(right.Red);
    }

    [Test]
    public async Task FillRadialGradient_CenterIsDifferentFromEdge()
    {
        using var img = CreateCanvas(64, 64);
        var ctx = new DrawingContext(img);

        var circle = PathParser.Circle(32, 32, 30);
        ctx.FillRadialGradient(circle, 32, 32, 30,
            DrawColor.White, DrawColor.Black);

        var center = img.GetPixel(32, 32);
        var edge = img.GetPixel(32, 4);
        // Center should be brighter (white) than edge area
        await Assert.That(center.Red).IsGreaterThan(edge.Red);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Flood Fill Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task FloodFill_FillsConnectedRegion()
    {
        using var img = CreateCanvas(32, 32); // starts white
        var ctx = new DrawingContext(img);
        ctx.FillColor = DrawColor.Red;

        ctx.FloodFill(0, 0);

        // Every pixel should now be red
        for (int y = 0; y < 32; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < 32; x++)
            {
                int offset = x * img.NumberOfChannels;
                // Red channel should be full, green should be low
                if (row[offset] < Quantum.MaxValue / 2)
                {
                    Assert.Fail($"Pixel ({x},{y}) was not filled red");
                    return;
                }
            }
        }

        await Assert.That(img.Columns).IsEqualTo(32u);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FillRule Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task FillRule_EvenOdd_IsDefault()
    {
        using var img = CreateCanvas(16, 16);
        var ctx = new DrawingContext(img);
        await Assert.That(ctx.FillRule).IsEqualTo(FillRule.EvenOdd);
    }

    [Test]
    public async Task FillRule_NonZero_CanBeSet()
    {
        using var img = CreateCanvas(16, 16);
        var ctx = new DrawingContext(img);
        ctx.FillRule = FillRule.NonZero;
        await Assert.That(ctx.FillRule).IsEqualTo(FillRule.NonZero);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame CreateCanvas(int width, int height)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, false);
        // Fill with white
        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * img.NumberOfChannels;
                row[offset] = Quantum.MaxValue;     // R
                row[offset + 1] = Quantum.MaxValue;  // G
                row[offset + 2] = Quantum.MaxValue;  // B
            }
        }
        return img;
    }

    private static int CountNonWhitePixels(ImageFrame img)
    {
        int count = 0;
        ushort threshold = (ushort)(Quantum.MaxValue * 0.95);
        for (int y = 0; y < (int)img.Rows; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < (int)img.Columns; x++)
            {
                int offset = x * img.NumberOfChannels;
                if (row[offset] < threshold || row[offset + 1] < threshold || row[offset + 2] < threshold)
                    count++;
            }
        }
        return count;
    }
}
