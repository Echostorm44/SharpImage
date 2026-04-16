using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Measures the distance (in pixels) and angle (in degrees) between two points.
/// Click to place start, drag to end, release to display measurement.
/// Does not modify any pixels — purely an overlay/readout tool.
/// </summary>
public sealed class MeasureTool : ITool
{
    public string Name => "Measure";
    public string IconResourceKey => "IconRulerMeasure";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    /// <summary>Euclidean distance in pixels between start and end.</summary>
    public double Distance { get; private set; }

    /// <summary>Angle in degrees from start to end (0° = right, counter-clockwise positive).</summary>
    public double Angle { get; private set; }

    /// <summary>Fired whenever the measurement values change (drag or release).</summary>
    public event Action<double, double>? MeasurementChanged;

    private bool isDragging;
    private Point startPoint;
    private Point endPoint;
    private bool hasMeasurement;

    public void Activate() { }

    public void Deactivate()
    {
        isDragging = false;
        hasMeasurement = false;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        isDragging = true;
        startPoint = canvasPoint;
        endPoint = canvasPoint;
        hasMeasurement = true;
        UpdateMeasurement();
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        endPoint = canvasPoint;
        UpdateMeasurement();
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        isDragging = false;
        endPoint = canvasPoint;
        UpdateMeasurement();
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            isDragging = false;
            hasMeasurement = false;
            Distance = 0;
            Angle = 0;
            MeasurementChanged?.Invoke(0, 0);
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (!hasMeasurement) return;

        double lineThickness = 1.5 / zoom;
        var linePen = new Pen(Brushes.Yellow, lineThickness);
        var endpointBrush = Brushes.Red;
        double markerRadius = 3.0 / zoom;

        // Draw measurement line
        context.DrawLine(linePen, startPoint, endPoint);

        // Draw endpoint markers
        context.DrawEllipse(endpointBrush, null, startPoint, markerRadius, markerRadius);
        context.DrawEllipse(endpointBrush, null, endPoint, markerRadius, markerRadius);

        // Draw labels at midpoint
        double midX = (startPoint.X + endPoint.X) / 2.0;
        double midY = (startPoint.Y + endPoint.Y) / 2.0;
        double labelOffset = 12.0 / zoom;
        double fontSize = 11.0 / zoom;

        var distanceText = new FormattedText(
            $"{Distance:F1} px",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
            fontSize,
            Brushes.Yellow);

        var angleText = new FormattedText(
            $"{Angle:F1}°",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold),
            fontSize,
            Brushes.Yellow);

        context.DrawText(distanceText, new Point(midX + labelOffset, midY - labelOffset));
        context.DrawText(angleText, new Point(midX + labelOffset, midY + labelOffset * 0.5));
    }

    public Control? BuildOptionsBar() => null;

    private void UpdateMeasurement()
    {
        double dx = endPoint.X - startPoint.X;
        double dy = endPoint.Y - startPoint.Y;
        Distance = Math.Sqrt(dx * dx + dy * dy);
        Angle = Math.Atan2(-dy, dx) * (180.0 / Math.PI);
        MeasurementChanged?.Invoke(Distance, Angle);
    }
}
