using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Shape drawing tool. Click-drag to draw rectangles, ellipses, or lines onto
/// the active layer. Shift constrains to square/circle. Supports fill vs stroke
/// and configurable stroke width.
/// </summary>
public sealed class ShapeTool : ITool
{
    public string Name => "Shape";
    public string IconResourceKey => "IconSquare";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    /// <summary>Type of shape to draw.</summary>
    public enum ShapeKind { Rectangle, Ellipse, Line }

    /// <summary>Current shape type.</summary>
    public ShapeKind ShapeType { get; set; } = ShapeKind.Rectangle;

    /// <summary>True to fill the shape, false for stroke only.</summary>
    public bool Fill { get; set; } = true;

    /// <summary>Stroke width in pixels (used when Fill is false or for lines).</summary>
    public int StrokeWidth { get; set; } = 2;

    private EditorDocument? document;
    private Color drawColor;
    private bool isDragging;
    private Point dragStart;
    private Point dragEnd;
    private bool shiftHeld;

    /// <summary>Fired when shape drag starts so MainWindow can snapshot for undo.</summary>
    public event Action? ShapeStarted;

    /// <summary>Fired after a shape is drawn so MainWindow can commit undo.</summary>
    public event Action? ShapeCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetColor(Color color) => drawColor = color;

    public void Activate() { }
    public void Deactivate() { isDragging = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;
        isDragging = true;
        dragStart = canvasPoint;
        dragEnd = canvasPoint;
        shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        ShapeStarted?.Invoke();
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        dragEnd = canvasPoint;
        shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging || document is null) return;
        isDragging = false;
        dragEnd = canvasPoint;
        shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        DrawShape();
        ShapeCompleted?.Invoke();
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            shiftHeld = true;
        if (e.Key == Key.Escape && isDragging)
        {
            isDragging = false;
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            shiftHeld = false;
    }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (!isDragging) return;

        var (start, end) = GetConstrainedPoints();
        var pen = new Pen(new SolidColorBrush(drawColor), 1.5 / zoom)
        {
            DashStyle = new DashStyle([4, 4], 0),
        };

        var s = new Point(start.X, start.Y);
        var e = new Point(end.X, end.Y);

        switch (ShapeType)
        {
            case ShapeKind.Rectangle:
                var rect = new Rect(s, e);
                context.DrawRectangle(Fill ? new SolidColorBrush(drawColor) : null, pen, rect);
                break;
            case ShapeKind.Ellipse:
                var center = new Point((s.X + e.X) / 2, (s.Y + e.Y) / 2);
                double rx = Math.Abs(e.X - s.X) / 2;
                double ry = Math.Abs(e.Y - s.Y) / 2;
                context.DrawEllipse(Fill ? new SolidColorBrush(drawColor) : null, pen, center, rx, ry);
                break;
            case ShapeKind.Line:
                context.DrawLine(new Pen(new SolidColorBrush(drawColor), StrokeWidth), s, e);
                break;
        }
    }

    public Control? BuildOptionsBar() => null;

    private (Point Start, Point End) GetConstrainedPoints()
    {
        var start = dragStart;
        var end = dragEnd;

        if (shiftHeld && ShapeType != ShapeKind.Line)
        {
            // Constrain to square/circle
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            end = new Point(
                start.X + Math.Sign(dx) * side,
                start.Y + Math.Sign(dy) * side);
        }

        return (start, end);
    }

    private void DrawShape()
    {
        if (document is null) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        var (start, end) = GetConstrainedPoints();
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;

        ushort cR = (ushort)(drawColor.R * 257);
        ushort cG = (ushort)(drawColor.G * 257);
        ushort cB = (ushort)(drawColor.B * 257);
        ushort cA = (ushort)(drawColor.A * 257);

        int ox = activeLayer.OffsetX;
        int oy = activeLayer.OffsetY;

        switch (ShapeType)
        {
            case ShapeKind.Rectangle:
                DrawRectangle(frame, channels, hasAlpha, start, end, ox, oy, cR, cG, cB, cA);
                break;
            case ShapeKind.Ellipse:
                DrawEllipse(frame, channels, hasAlpha, start, end, ox, oy, cR, cG, cB, cA);
                break;
            case ShapeKind.Line:
                DrawLine(frame, channels, hasAlpha, start, end, ox, oy, cR, cG, cB, cA);
                break;
        }
    }

    private void DrawRectangle(Image.ImageFrame frame, int channels, bool hasAlpha,
        Point start, Point end, int ox, int oy,
        ushort cR, ushort cG, ushort cB, ushort cA)
    {
        int x1 = (int)Math.Round(Math.Min(start.X, end.X)) - ox;
        int y1 = (int)Math.Round(Math.Min(start.Y, end.Y)) - oy;
        int x2 = (int)Math.Round(Math.Max(start.X, end.X)) - ox;
        int y2 = (int)Math.Round(Math.Max(start.Y, end.Y)) - oy;

        int frameW = (int)frame.Columns;
        int frameH = (int)frame.Rows;

        if (Fill)
        {
            int yMin = Math.Clamp(y1, 0, frameH - 1);
            int yMax = Math.Clamp(y2, 0, frameH - 1);
            int xMin = Math.Clamp(x1, 0, frameW - 1);
            int xMax = Math.Clamp(x2, 0, frameW - 1);

            for (int y = yMin; y <= yMax; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = xMin; x <= xMax; x++)
                    SetPixel(row, x, channels, hasAlpha, cR, cG, cB, cA);
            }
        }
        else
        {
            // Stroke only — draw four edges
            for (int sw = 0; sw < StrokeWidth; sw++)
            {
                DrawHorizontalLine(frame, channels, hasAlpha, x1, x2, y1 + sw, cR, cG, cB, cA);
                DrawHorizontalLine(frame, channels, hasAlpha, x1, x2, y2 - sw, cR, cG, cB, cA);
                DrawVerticalLine(frame, channels, hasAlpha, x1 + sw, y1, y2, cR, cG, cB, cA);
                DrawVerticalLine(frame, channels, hasAlpha, x2 - sw, y1, y2, cR, cG, cB, cA);
            }
        }
    }

    private void DrawEllipse(Image.ImageFrame frame, int channels, bool hasAlpha,
        Point start, Point end, int ox, int oy,
        ushort cR, ushort cG, ushort cB, ushort cA)
    {
        double centerX = (start.X + end.X) / 2.0 - ox;
        double centerY = (start.Y + end.Y) / 2.0 - oy;
        double radiusX = Math.Abs(end.X - start.X) / 2.0;
        double radiusY = Math.Abs(end.Y - start.Y) / 2.0;
        if (radiusX < 1 || radiusY < 1) return;

        int frameW = (int)frame.Columns;
        int frameH = (int)frame.Rows;
        int yMin = Math.Clamp((int)(centerY - radiusY - 1), 0, frameH - 1);
        int yMax = Math.Clamp((int)(centerY + radiusY + 1), 0, frameH - 1);
        int xMin = Math.Clamp((int)(centerX - radiusX - 1), 0, frameW - 1);
        int xMax = Math.Clamp((int)(centerX + radiusX + 1), 0, frameW - 1);

        double rxSq = radiusX * radiusX;
        double rySq = radiusY * radiusY;

        if (Fill)
        {
            for (int y = yMin; y <= yMax; y++)
            {
                double dy = y - centerY;
                var row = frame.GetPixelRowForWrite(y);
                for (int x = xMin; x <= xMax; x++)
                {
                    double dx = x - centerX;
                    if (dx * dx / rxSq + dy * dy / rySq <= 1.0)
                        SetPixel(row, x, channels, hasAlpha, cR, cG, cB, cA);
                }
            }
        }
        else
        {
            double innerRx = radiusX - StrokeWidth;
            double innerRy = radiusY - StrokeWidth;
            double innerRxSq = innerRx > 0 ? innerRx * innerRx : 0;
            double innerRySq = innerRy > 0 ? innerRy * innerRy : 0;

            for (int y = yMin; y <= yMax; y++)
            {
                double dy = y - centerY;
                var row = frame.GetPixelRowForWrite(y);
                for (int x = xMin; x <= xMax; x++)
                {
                    double dx = x - centerX;
                    double outerDist = dx * dx / rxSq + dy * dy / rySq;
                    if (outerDist > 1.0) continue;
                    double innerDist = innerRx > 0 ? dx * dx / innerRxSq + dy * dy / innerRySq : 0;
                    if (innerDist < 1.0 && innerRx > 0) continue;
                    SetPixel(row, x, channels, hasAlpha, cR, cG, cB, cA);
                }
            }
        }
    }

    private void DrawLine(Image.ImageFrame frame, int channels, bool hasAlpha,
        Point start, Point end, int ox, int oy,
        ushort cR, ushort cG, ushort cB, ushort cA)
    {
        // Bresenham's line with thickness
        int x0 = (int)Math.Round(start.X) - ox;
        int y0 = (int)Math.Round(start.Y) - oy;
        int x1 = (int)Math.Round(end.X) - ox;
        int y1 = (int)Math.Round(end.Y) - oy;

        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        int halfStroke = StrokeWidth / 2;

        while (true)
        {
            // Draw a filled circle of StrokeWidth at each point for thickness
            for (int sy2 = -halfStroke; sy2 <= halfStroke; sy2++)
            {
                for (int sx2 = -halfStroke; sx2 <= halfStroke; sx2++)
                {
                    int px = x0 + sx2;
                    int py = y0 + sy2;
                    if (px >= 0 && px < (int)frame.Columns && py >= 0 && py < (int)frame.Rows)
                    {
                        var row = frame.GetPixelRowForWrite(py);
                        SetPixel(row, px, channels, hasAlpha, cR, cG, cB, cA);
                    }
                }
            }

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void SetPixel(Span<ushort> row, int x, int channels, bool hasAlpha,
        ushort r, ushort g, ushort b, ushort a)
    {
        int offset = x * channels;
        row[offset] = r;
        row[offset + 1] = g;
        row[offset + 2] = b;
        if (hasAlpha && channels > 3)
            row[offset + 3] = a;
    }

    private static void DrawHorizontalLine(Image.ImageFrame frame, int channels, bool hasAlpha,
        int x1, int x2, int y, ushort cR, ushort cG, ushort cB, ushort cA)
    {
        if (y < 0 || y >= (int)frame.Rows) return;
        int xMin = Math.Clamp(Math.Min(x1, x2), 0, (int)frame.Columns - 1);
        int xMax = Math.Clamp(Math.Max(x1, x2), 0, (int)frame.Columns - 1);
        var row = frame.GetPixelRowForWrite(y);
        for (int x = xMin; x <= xMax; x++)
            SetPixel(row, x, channels, hasAlpha, cR, cG, cB, cA);
    }

    private static void DrawVerticalLine(Image.ImageFrame frame, int channels, bool hasAlpha,
        int x, int y1, int y2, ushort cR, ushort cG, ushort cB, ushort cA)
    {
        if (x < 0 || x >= (int)frame.Columns) return;
        int yMin = Math.Clamp(Math.Min(y1, y2), 0, (int)frame.Rows - 1);
        int yMax = Math.Clamp(Math.Max(y1, y2), 0, (int)frame.Rows - 1);
        for (int y = yMin; y <= yMax; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            SetPixel(row, x, channels, hasAlpha, cR, cG, cB, cA);
        }
    }
}
