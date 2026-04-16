using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Crop tool: click-drag to define a crop region with handles on corners and edges.
/// Darkens the area outside the crop. Enter applies, Escape cancels.
/// </summary>
public sealed class CropTool : ITool
{
    public string Name => "Crop";
    public string IconResourceKey => "IconCrop";
    public Cursor ToolCursor => Cursor.Default;

    private EditorDocument? document;
    private bool isDragging;
    private bool hasCropRect;
    private Point dragStart;
    private Rect cropRect;
    private CropHandle activeHandle = CropHandle.None;

    private const double HandleSize = 6;

    /// <summary>Fires when the user presses Enter to apply the crop.</summary>
    public event Action<Rect>? CropApplied;

    /// <summary>Fires when the crop overlay changes — canvas should redraw.</summary>
    public event Action? CropChanged;

    public void SetDocument(EditorDocument? doc) => document = doc;

    public void Activate() { }
    public void Deactivate()
    {
        isDragging = false;
        hasCropRect = false;
        cropRect = default;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (hasCropRect)
        {
            // Check if clicking on a handle to resize
            activeHandle = HitTestHandle(canvasPoint);
            if (activeHandle != CropHandle.None)
            {
                isDragging = true;
                dragStart = canvasPoint;
                return;
            }

            // Check if clicking inside crop rect to move it
            if (cropRect.Contains(canvasPoint))
            {
                activeHandle = CropHandle.Move;
                isDragging = true;
                dragStart = canvasPoint;
                return;
            }
        }

        // Start a new crop rectangle
        isDragging = true;
        hasCropRect = true;
        dragStart = canvasPoint;
        cropRect = new Rect(canvasPoint, new Size(0, 0));
        activeHandle = CropHandle.BottomRight;
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;

        var delta = canvasPoint - dragStart;

        if (activeHandle == CropHandle.Move)
        {
            cropRect = new Rect(cropRect.X + delta.X, cropRect.Y + delta.Y, cropRect.Width, cropRect.Height);
            dragStart = canvasPoint;
        }
        else if (activeHandle == CropHandle.BottomRight && !hasCropRect)
        {
            // Drawing new rect
            double x = Math.Min(dragStart.X, canvasPoint.X);
            double y = Math.Min(dragStart.Y, canvasPoint.Y);
            double w = Math.Abs(canvasPoint.X - dragStart.X);
            double h = Math.Abs(canvasPoint.Y - dragStart.Y);
            cropRect = new Rect(x, y, w, h);
        }
        else
        {
            cropRect = ResizeRect(cropRect, activeHandle, delta);
            dragStart = canvasPoint;
        }

        CropChanged?.Invoke();
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        isDragging = false;
        activeHandle = CropHandle.None;
        hasCropRect = cropRect.Width > 1 && cropRect.Height > 1;
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            if (hasCropRect && cropRect.Width > 0 && cropRect.Height > 0)
            {
                CropApplied?.Invoke(cropRect);
                hasCropRect = false;
                cropRect = default;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            hasCropRect = false;
            cropRect = default;
            CropChanged?.Invoke();
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    /// <summary>Apply the current crop selection programmatically (from options bar button).</summary>
    public void ApplyCrop()
    {
        if (hasCropRect && cropRect.Width > 0 && cropRect.Height > 0)
        {
            CropApplied?.Invoke(cropRect);
            hasCropRect = false;
            cropRect = default;
        }
    }

    /// <summary>Cancel the current crop selection programmatically.</summary>
    public void CancelCrop()
    {
        hasCropRect = false;
        cropRect = default;
        CropChanged?.Invoke();
    }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (!hasCropRect || cropRect.Width < 1 || cropRect.Height < 1) return;
        if (document is null) return;

        double docWidth = document.Width;
        double docHeight = document.Height;

        // Darken outside the crop area
        var dimBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
        // Top
        if (cropRect.Y > 0)
            context.DrawRectangle(dimBrush, null, new Rect(0, 0, docWidth, cropRect.Y));
        // Bottom
        double bottomY = cropRect.Y + cropRect.Height;
        if (bottomY < docHeight)
            context.DrawRectangle(dimBrush, null, new Rect(0, bottomY, docWidth, docHeight - bottomY));
        // Left
        if (cropRect.X > 0)
            context.DrawRectangle(dimBrush, null, new Rect(0, cropRect.Y, cropRect.X, cropRect.Height));
        // Right
        double rightX = cropRect.X + cropRect.Width;
        if (rightX < docWidth)
            context.DrawRectangle(dimBrush, null, new Rect(rightX, cropRect.Y, docWidth - rightX, cropRect.Height));

        // Crop border
        double strokeWidth = 1.0 / zoom;
        var borderPen = new Pen(Brushes.White, strokeWidth);
        context.DrawRectangle(null, borderPen, cropRect);
        var dashPen = new Pen(Brushes.Black, strokeWidth)
        {
            DashStyle = new DashStyle([4, 4], 0),
        };
        context.DrawRectangle(null, dashPen, cropRect);

        // Rule of thirds grid
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), strokeWidth * 0.5);
        double thirdW = cropRect.Width / 3;
        double thirdH = cropRect.Height / 3;
        for (int i = 1; i <= 2; i++)
        {
            context.DrawLine(gridPen,
                new Point(cropRect.X + thirdW * i, cropRect.Y),
                new Point(cropRect.X + thirdW * i, cropRect.Y + cropRect.Height));
            context.DrawLine(gridPen,
                new Point(cropRect.X, cropRect.Y + thirdH * i),
                new Point(cropRect.X + cropRect.Width, cropRect.Y + thirdH * i));
        }

        // Resize handles (small squares at corners and edge midpoints)
        double hs = HandleSize / zoom;
        var handleBrush = Brushes.White;
        var handlePen = new Pen(Brushes.Black, strokeWidth);
        DrawHandle(context, handleBrush, handlePen, cropRect.TopLeft, hs);
        DrawHandle(context, handleBrush, handlePen, cropRect.TopRight, hs);
        DrawHandle(context, handleBrush, handlePen, cropRect.BottomLeft, hs);
        DrawHandle(context, handleBrush, handlePen, cropRect.BottomRight, hs);
        DrawHandle(context, handleBrush, handlePen, new Point(cropRect.X + cropRect.Width / 2, cropRect.Y), hs);
        DrawHandle(context, handleBrush, handlePen, new Point(cropRect.X + cropRect.Width / 2, cropRect.Y + cropRect.Height), hs);
        DrawHandle(context, handleBrush, handlePen, new Point(cropRect.X, cropRect.Y + cropRect.Height / 2), hs);
        DrawHandle(context, handleBrush, handlePen, new Point(cropRect.X + cropRect.Width, cropRect.Y + cropRect.Height / 2), hs);
    }

    public Control? BuildOptionsBar() => null;

    // ═══════ Handle helpers ═══════

    private static void DrawHandle(DrawingContext context, IBrush brush, Pen pen, Point center, double size)
    {
        var rect = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
        context.DrawRectangle(brush, pen, rect);
    }

    /// <summary>The current zoom level, set by the canvas before hit testing.</summary>
    public double CurrentZoom { get; set; } = 1.0;

    private CropHandle HitTestHandle(Point point)
    {
        // Scale threshold inversely with zoom so handles stay grabbable at all zoom levels
        double threshold = HandleSize * 2 / Math.Max(0.1, CurrentZoom);
        if (Distance(point, cropRect.TopLeft) < threshold) return CropHandle.TopLeft;
        if (Distance(point, cropRect.TopRight) < threshold) return CropHandle.TopRight;
        if (Distance(point, cropRect.BottomLeft) < threshold) return CropHandle.BottomLeft;
        if (Distance(point, cropRect.BottomRight) < threshold) return CropHandle.BottomRight;
        if (Distance(point, new Point(cropRect.X + cropRect.Width / 2, cropRect.Y)) < threshold) return CropHandle.Top;
        if (Distance(point, new Point(cropRect.X + cropRect.Width / 2, cropRect.Y + cropRect.Height)) < threshold) return CropHandle.Bottom;
        if (Distance(point, new Point(cropRect.X, cropRect.Y + cropRect.Height / 2)) < threshold) return CropHandle.Left;
        if (Distance(point, new Point(cropRect.X + cropRect.Width, cropRect.Y + cropRect.Height / 2)) < threshold) return CropHandle.Right;
        return CropHandle.None;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Rect ResizeRect(Rect rect, CropHandle handle, Vector delta)
    {
        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;

        switch (handle)
        {
            case CropHandle.TopLeft:     x += delta.X; y += delta.Y; w -= delta.X; h -= delta.Y; break;
            case CropHandle.Top:         y += delta.Y; h -= delta.Y; break;
            case CropHandle.TopRight:    y += delta.Y; w += delta.X; h -= delta.Y; break;
            case CropHandle.Left:        x += delta.X; w -= delta.X; break;
            case CropHandle.Right:       w += delta.X; break;
            case CropHandle.BottomLeft:  x += delta.X; w -= delta.X; h += delta.Y; break;
            case CropHandle.Bottom:      h += delta.Y; break;
            case CropHandle.BottomRight: w += delta.X; h += delta.Y; break;
        }

        // Prevent negative dimensions
        if (w < 1) { w = 1; }
        if (h < 1) { h = 1; }

        return new Rect(x, y, w, h);
    }

    private enum CropHandle
    {
        None, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight, Move
    }
}
