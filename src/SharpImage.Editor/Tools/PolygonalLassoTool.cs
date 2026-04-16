using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Polygonal Lasso: click to add vertices, double-click or click near the start
/// to close and complete the selection. Renders line segments as an overlay.
/// Calls document.SelectPolygon on completion.
/// </summary>
public sealed class PolygonalLassoTool : ITool
{
    public string Name => "Polygonal Lasso";
    public string IconResourceKey => "IconPentagon";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private readonly List<Point> vertices = [];
    private Point currentMousePoint;
    private bool isActive;
    private double dashOffset;

    private const double CloseThreshold = 8.0;

    /// <summary>Fires when the polygon selection is completed.</summary>
    public event Action? SelectionCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;

    public void Activate() { }
    public void Deactivate()
    {
        vertices.Clear();
        isActive = false;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;

        // Double-click: close and finish
        if (e.ClickCount >= 2 && vertices.Count >= 3)
        {
            CompleteSelection();
            return;
        }

        // If we have vertices, check if clicking near the start to close
        if (vertices.Count >= 3)
        {
            double dx = canvasPoint.X - vertices[0].X;
            double dy = canvasPoint.Y - vertices[0].Y;
            if (Math.Sqrt(dx * dx + dy * dy) < CloseThreshold)
            {
                CompleteSelection();
                return;
            }
        }

        vertices.Add(canvasPoint);
        isActive = true;
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        currentMousePoint = canvasPoint;
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint) { }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            vertices.Clear();
            isActive = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && vertices.Count >= 3)
        {
            CompleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && vertices.Count > 0)
        {
            // Undo last vertex
            vertices.RemoveAt(vertices.Count - 1);
            if (vertices.Count == 0) isActive = false;
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (vertices.Count == 0) return;

        double strokeWidth = 1.0 / zoom;
        var whitePen = new Pen(Brushes.White, strokeWidth);
        var dashPen = new Pen(Brushes.Black, strokeWidth)
        {
            DashStyle = new DashStyle([4, 4], dashOffset),
        };

        // Draw completed segments
        for (int i = 0; i < vertices.Count - 1; i++)
        {
            var a = new Point(vertices[i].X, vertices[i].Y);
            var b = new Point(vertices[i + 1].X, vertices[i + 1].Y);
            context.DrawLine(whitePen, a, b);
            context.DrawLine(dashPen, a, b);
        }

        // Draw line from last vertex to current mouse position
        if (isActive)
        {
            var last = new Point(vertices[^1].X, vertices[^1].Y);
            var mouse = new Point(currentMousePoint.X, currentMousePoint.Y);
            context.DrawLine(whitePen, last, mouse);
            context.DrawLine(dashPen, last, mouse);

            // Draw a closing line preview (faint) from mouse to first vertex
            if (vertices.Count >= 2)
            {
                var first = new Point(vertices[0].X, vertices[0].Y);
                var faintPen = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), strokeWidth);
                context.DrawLine(faintPen, mouse, first);
            }
        }

        // Draw vertex markers
        double markerR = 3.0 / zoom;
        foreach (var v in vertices)
        {
            var p = new Point(v.X, v.Y);
            context.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1 / zoom), p, markerR, markerR);
        }

        // Highlight first vertex when close enough to close
        if (vertices.Count >= 3)
        {
            double dx = currentMousePoint.X - vertices[0].X;
            double dy = currentMousePoint.Y - vertices[0].Y;
            if (Math.Sqrt(dx * dx + dy * dy) < CloseThreshold)
            {
                var first = new Point(vertices[0].X, vertices[0].Y);
                context.DrawEllipse(null, new Pen(Brushes.Lime, 2 / zoom), first, markerR + 2 / zoom, markerR + 2 / zoom);
            }
        }

        dashOffset = (dashOffset + 0.2) % 8;
    }

    public Control? BuildOptionsBar() => null;

    private void CompleteSelection()
    {
        if (document is null || vertices.Count < 3)
        {
            vertices.Clear();
            isActive = false;
            return;
        }

        // Convert to integer tuple array for EditorDocument.SelectPolygon
        var intPoints = new (int X, int Y)[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
            intPoints[i] = ((int)Math.Round(vertices[i].X), (int)Math.Round(vertices[i].Y));

        document.SelectPolygon(intPoints);

        vertices.Clear();
        isActive = false;
        SelectionCompleted?.Invoke();
    }
}
