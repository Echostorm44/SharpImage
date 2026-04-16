using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Freehand Lasso: click-drag to draw a freeform selection path.
/// Release closes the path automatically.
/// Renders a marching-ants dashed polygon overlay.
/// </summary>
public sealed class LassoTool : ITool
{
    public string Name => "Lasso";
    public string IconResourceKey => "IconLasso";
    public Cursor ToolCursor => Cursor.Default;

    private bool isDragging;
    private readonly List<Point> points = [];
    private double dashOffset;

    /// <summary>Fires when the lasso selection path is completed. Points are in image coords.</summary>
    public event Action<IReadOnlyList<Point>>? SelectionCompleted;

    public void Activate() { }
    public void Deactivate()
    {
        isDragging = false;
        points.Clear();
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        isDragging = true;
        points.Clear();
        points.Add(canvasPoint);
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;

        // Only add point if it's far enough from the last (reduces noise)
        if (points.Count > 0)
        {
            var last = points[^1];
            double dist = Math.Sqrt((canvasPoint.X - last.X) * (canvasPoint.X - last.X) +
                                    (canvasPoint.Y - last.Y) * (canvasPoint.Y - last.Y));
            if (dist < 1.5) return;
        }

        points.Add(canvasPoint);
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        isDragging = false;

        // Close the path and fire event if we have enough points
        if (points.Count >= 3)
        {
            points.Add(points[0]); // close the path
            SelectionCompleted?.Invoke(points.AsReadOnly());
        }

        points.Clear();
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            isDragging = false;
            points.Clear();
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (points.Count < 2) return;

        double strokeWidth = 1.0 / zoom;

        // Build the path geometry
        var figure = new PathFigure { StartPoint = points[0], IsClosed = !isDragging };
        for (int i = 1; i < points.Count; i++)
            figure.Segments!.Add(new LineSegment { Point = points[i] });

        // If still drawing, show a closing line from last point to first
        if (isDragging && points.Count >= 2)
            figure.Segments!.Add(new LineSegment { Point = points[0] });

        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);

        // White solid path
        var whitePen = new Pen(Brushes.White, strokeWidth);
        context.DrawGeometry(null, whitePen, geometry);

        // Black dashed path (marching ants)
        var dashPen = new Pen(Brushes.Black, strokeWidth)
        {
            DashStyle = new DashStyle([4, 4], dashOffset),
        };
        context.DrawGeometry(null, dashPen, geometry);

        dashOffset = (dashOffset + 0.2) % 8;
    }

    public Control? BuildOptionsBar() => null;
}
