using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Rectangular Marquee: click-drag to create a rectangular selection.
/// Shift constrains to square. Alt draws from center.
/// Renders a marching-ants dashed rectangle overlay.
/// Actual selection mask creation happens in Phase 7 (Selection System).
/// </summary>
public sealed class RectangularMarqueeTool : ITool
{
    public string Name => "Rectangular Marquee";
    public string IconResourceKey => "IconSquareDashed";
    public Cursor ToolCursor => Cursor.Default;

    private bool isDragging;
    private Point dragStart;
    private Rect currentRect;
    private double dashOffset;

    /// <summary>Fires when selection rectangle is completed. Rect is in image coordinates.</summary>
    public event Action<Rect>? SelectionCompleted;

    public void Activate() { }
    public void Deactivate()
    {
        isDragging = false;
        currentRect = default;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        isDragging = true;
        dragStart = canvasPoint;
        currentRect = new Rect(canvasPoint, new Size(0, 0));
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;

        var start = dragStart;
        var end = canvasPoint;

        // Shift constrains to square
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
            currentRect = new Rect(start.X - hw, start.Y - hh, hw * 2, hh * 2);
        }
        else
        {
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double w = Math.Abs(end.X - start.X);
            double h = Math.Abs(end.Y - start.Y);
            currentRect = new Rect(x, y, w, h);
        }
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        isDragging = false;

        if (currentRect.Width > 1 && currentRect.Height > 1)
            SelectionCompleted?.Invoke(currentRect);

        // Clear the tool overlay — the selection mask handles rendering from here
        currentRect = default;
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            isDragging = false;
            currentRect = default;
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (currentRect.Width < 1 || currentRect.Height < 1) return;

        // Marching ants: black dashed on white solid
        double strokeWidth = 1.0 / zoom;
        var whitePen = new Pen(Brushes.White, strokeWidth);
        context.DrawRectangle(null, whitePen, currentRect);

        var dashPen = new Pen(Brushes.Black, strokeWidth)
        {
            DashStyle = new DashStyle([4, 4], dashOffset),
        };
        context.DrawRectangle(null, dashPen, currentRect);

        // Animate dash offset for marching effect (incremented externally)
        dashOffset = (dashOffset + 0.2) % 8;
    }

    public Control? BuildOptionsBar() => null;
}
