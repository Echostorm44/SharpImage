using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Elliptical Marquee: click-drag to create an elliptical selection.
/// Shift constrains to circle. Alt draws from center.
/// Renders a marching-ants dashed ellipse overlay.
/// </summary>
public sealed class EllipticalMarqueeTool : ITool
{
    public string Name => "Elliptical Marquee";
    public string IconResourceKey => "IconCircleDashed";
    public Cursor ToolCursor => Cursor.Default;

    private bool isDragging;
    private Point dragStart;
    private Rect boundingRect;
    private double dashOffset;

    /// <summary>Fires when selection ellipse is completed. Rect is bounding box in image coords.</summary>
    public event Action<Rect>? SelectionCompleted;

    public void Activate() { }
    public void Deactivate()
    {
        isDragging = false;
        boundingRect = default;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        isDragging = true;
        dragStart = canvasPoint;
        boundingRect = new Rect(canvasPoint, new Size(0, 0));
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;

        var start = dragStart;
        var end = canvasPoint;

        // Shift constrains to circle
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            double size = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            end = new Point(
                start.X + Math.Sign(end.X - start.X) * size,
                start.Y + Math.Sign(end.Y - start.Y) * size);
        }

        // Alt draws from center
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            double hw = Math.Abs(end.X - start.X);
            double hh = Math.Abs(end.Y - start.Y);
            boundingRect = new Rect(start.X - hw, start.Y - hh, hw * 2, hh * 2);
        }
        else
        {
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double w = Math.Abs(end.X - start.X);
            double h = Math.Abs(end.Y - start.Y);
            boundingRect = new Rect(x, y, w, h);
        }
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        isDragging = false;

        if (boundingRect.Width > 1 && boundingRect.Height > 1)
            SelectionCompleted?.Invoke(boundingRect);

        boundingRect = default;
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            isDragging = false;
            boundingRect = default;
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (boundingRect.Width < 1 || boundingRect.Height < 1) return;

        double strokeWidth = 1.0 / zoom;

        // White solid ellipse
        var whitePen = new Pen(Brushes.White, strokeWidth);
        var geometry = new EllipseGeometry(boundingRect);
        context.DrawGeometry(null, whitePen, geometry);

        // Black dashed ellipse (marching ants)
        var dashPen = new Pen(Brushes.Black, strokeWidth)
        {
            DashStyle = new DashStyle([4, 4], dashOffset),
        };
        context.DrawGeometry(null, dashPen, geometry);

        dashOffset = (dashOffset + 0.2) % 8;
    }

    public Control? BuildOptionsBar() => null;
}
