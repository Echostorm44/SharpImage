using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Vector path tool that places anchor points to build a Bézier path.
/// Click to add a straight anchor; click-and-drag to create a curve handle.
/// Double-click or click on the first point to close the path.
/// Does not modify pixels directly — the completed path can be converted to a selection.
/// </summary>
public sealed class PenTool : ITool
{
    public string Name => "Pen";
    public string IconResourceKey => "IconPenTool";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    /// <summary>A single anchor in the path with optional cubic Bézier control handles.</summary>
    public struct AnchorPoint
    {
        /// <summary>Position of the anchor in image-space coordinates.</summary>
        public Point Position;

        /// <summary>Incoming control handle (for the curve arriving at this anchor).</summary>
        public Point? ControlIn;

        /// <summary>Outgoing control handle (for the curve leaving this anchor).</summary>
        public Point? ControlOut;
    }

    /// <summary>Current list of anchor points forming the path.</summary>
    public List<AnchorPoint> Anchors { get; } = [];

    /// <summary>Whether the path has been closed (forms a loop).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Fired when the path is closed (double-click or click on first point).</summary>
    public event Action? PathCompleted;

    private bool isDraggingHandle;
    private Point dragOrigin;
    private DateTime lastClickTime;
    private const double CloseThreshold = 15.0;
    private const double DoubleClickMs = 400;

    public void Activate() { }

    public void Deactivate()
    {
        ClearPath();
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (IsClosed)
            ClearPath();

        var now = DateTime.UtcNow;
        bool isDoubleClick = (now - lastClickTime).TotalMilliseconds < DoubleClickMs && Anchors.Count >= 2;
        lastClickTime = now;

        // Double-click closes the path
        if (isDoubleClick)
        {
            ClosePath();
            return;
        }

        // Click near the first anchor closes the path
        if (Anchors.Count >= 3)
        {
            double dx = canvasPoint.X - Anchors[0].Position.X;
            double dy = canvasPoint.Y - Anchors[0].Position.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < CloseThreshold)
            {
                ClosePath();
                return;
            }
        }

        // Add new anchor — start tracking for potential handle drag
        isDraggingHandle = true;
        dragOrigin = canvasPoint;
        Anchors.Add(new AnchorPoint { Position = canvasPoint });
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDraggingHandle || Anchors.Count == 0) return;

        // Dragging away from anchor creates symmetric control handles
        double dx = canvasPoint.X - dragOrigin.X;
        double dy = canvasPoint.Y - dragOrigin.Y;
        if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;

        var anchor = Anchors[^1];
        anchor.ControlOut = canvasPoint;
        anchor.ControlIn = new Point(dragOrigin.X - dx, dragOrigin.Y - dy);
        Anchors[^1] = anchor;
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        isDraggingHandle = false;
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearPath();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Anchors.Count >= 2)
        {
            ClosePath();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && Anchors.Count > 0)
        {
            // Remove last anchor
            Anchors.RemoveAt(Anchors.Count - 1);
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (Anchors.Count == 0) return;

        double lineThickness = 1.5 / zoom;
        double anchorSize = 4.0 / zoom;
        double handleSize = 3.0 / zoom;
        var pathPen = new Pen(Brushes.Cyan, lineThickness);
        var handleLinePen = new Pen(Brushes.Gray, 0.8 / zoom);

        // Draw path segments
        for (int i = 0; i < Anchors.Count - 1; i++)
            DrawSegment(context, Anchors[i], Anchors[i + 1], pathPen);

        // Draw closing segment if path is closed
        if (IsClosed && Anchors.Count >= 2)
            DrawSegment(context, Anchors[^1], Anchors[0], pathPen);

        // Draw anchors and control handles
        for (int i = 0; i < Anchors.Count; i++)
        {
            var anchor = Anchors[i];

            // Control handle lines and dots
            if (anchor.ControlIn.HasValue)
            {
                context.DrawLine(handleLinePen, anchor.Position, anchor.ControlIn.Value);
                context.DrawEllipse(Brushes.White, null, anchor.ControlIn.Value, handleSize, handleSize);
            }
            if (anchor.ControlOut.HasValue)
            {
                context.DrawLine(handleLinePen, anchor.Position, anchor.ControlOut.Value);
                context.DrawEllipse(Brushes.White, null, anchor.ControlOut.Value, handleSize, handleSize);
            }

            // Anchor square — first anchor is highlighted when path can be closed
            var anchorBrush = (i == 0 && Anchors.Count >= 3 && !IsClosed) ? Brushes.Lime : Brushes.White;
            var halfSize = anchorSize / 2.0;
            var anchorRect = new Rect(
                anchor.Position.X - halfSize, anchor.Position.Y - halfSize,
                anchorSize, anchorSize);
            context.DrawRectangle(anchorBrush, new Pen(Brushes.Cyan, 0.8 / zoom), anchorRect);
        }
    }

    public Control? BuildOptionsBar() => null;

    /// <summary>
    /// Returns the path as a flat list of points (line-approximated).
    /// Useful for converting to a selection mask.
    /// </summary>
    public List<Point> ToPointList(int segmentsPerCurve = 20)
    {
        var points = new List<Point>();
        if (Anchors.Count == 0) return points;

        points.Add(Anchors[0].Position);

        int count = IsClosed ? Anchors.Count : Anchors.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = Anchors[i];
            var b = Anchors[(i + 1) % Anchors.Count];
            bool hasCurve = a.ControlOut.HasValue || b.ControlIn.HasValue;

            if (hasCurve)
            {
                var cp1 = a.ControlOut ?? a.Position;
                var cp2 = b.ControlIn ?? b.Position;
                for (int t = 1; t <= segmentsPerCurve; t++)
                {
                    double f = t / (double)segmentsPerCurve;
                    points.Add(CubicBezier(a.Position, cp1, cp2, b.Position, f));
                }
            }
            else
            {
                points.Add(b.Position);
            }
        }

        return points;
    }

    private void ClosePath()
    {
        IsClosed = true;
        isDraggingHandle = false;
        PathCompleted?.Invoke();
    }

    private void ClearPath()
    {
        Anchors.Clear();
        IsClosed = false;
        isDraggingHandle = false;
    }

    private static void DrawSegment(DrawingContext context, AnchorPoint from, AnchorPoint to, Pen pen)
    {
        bool hasCurve = from.ControlOut.HasValue || to.ControlIn.HasValue;
        if (!hasCurve)
        {
            context.DrawLine(pen, from.Position, to.Position);
            return;
        }

        // Draw cubic Bézier as polyline segments
        var cp1 = from.ControlOut ?? from.Position;
        var cp2 = to.ControlIn ?? to.Position;
        const int segments = 24;
        var prev = from.Position;
        for (int i = 1; i <= segments; i++)
        {
            double t = i / (double)segments;
            var pt = CubicBezier(from.Position, cp1, cp2, to.Position, t);
            context.DrawLine(pen, prev, pt);
            prev = pt;
        }
    }

    private static Point CubicBezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1.0 - t;
        double uu = u * u;
        double tt = t * t;
        double x = uu * u * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + tt * t * p3.X;
        double y = uu * u * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + tt * t * p3.Y;
        return new Point(x, y);
    }
}
