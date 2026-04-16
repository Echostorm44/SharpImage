using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;
using SharpImage.Image;
using AvDrawingContext = Avalonia.Media.DrawingContext;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Gradient tool. Click and drag to define a gradient line between foreground and
/// background colors. Supports linear gradient rendering onto the active layer.
/// </summary>
public sealed class GradientTool : ITool
{
    public string Name => "Gradient";
    public string IconResourceKey => "IconGradient";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private BrushSettings? brushSettings;
    private Color startColor;
    private Color endColor;
    private bool isDragging;
    private Point dragStart;
    private Point dragEnd;

    /// <summary>Fired when gradient drag starts so MainWindow can snapshot for undo.</summary>
    public event Action? GradientStarted;

    /// <summary>Fired after a gradient is applied so MainWindow can commit undo.</summary>
    public event Action? GradientApplied;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetBrushSettings(BrushSettings settings) => brushSettings = settings;
    public void SetColors(Color foreground, Color background)
    {
        startColor = foreground;
        endColor = background;
    }

    public void Activate() { }
    public void Deactivate() { isDragging = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;
        isDragging = true;
        dragStart = canvasPoint;
        dragEnd = canvasPoint;
        GradientStarted?.Invoke();
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging) return;
        dragEnd = canvasPoint;
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging || document is null) return;
        isDragging = false;
        dragEnd = canvasPoint;

        ApplyGradient();
        GradientApplied?.Invoke();
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && isDragging)
        {
            isDragging = false;
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(AvDrawingContext context, double zoom)
    {
        if (!isDragging) return;

        // Draw gradient preview line
        var pen = new Pen(Brushes.White, 1.5 / zoom);
        var penDash = new Pen(Brushes.Black, 1.5 / zoom)
        {
            DashStyle = new DashStyle([4, 4], 0),
        };

        var start = new Point(dragStart.X, dragStart.Y);
        var end = new Point(dragEnd.X, dragEnd.Y);

        context.DrawLine(pen, start, end);
        context.DrawLine(penDash, start, end);

        // Start/end markers
        double markerR = 4 / zoom;
        context.DrawEllipse(new SolidColorBrush(startColor), pen, start, markerR, markerR);
        context.DrawEllipse(new SolidColorBrush(endColor), pen, end, markerR, markerR);
    }

    public Control? BuildOptionsBar() => null;

    private void ApplyGradient()
    {
        if (document is null) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        double gx = dragEnd.X - dragStart.X;
        double gy = dragEnd.Y - dragStart.Y;
        double gradientLength = Math.Sqrt(gx * gx + gy * gy);
        if (gradientLength < 1) return;

        // Normalize gradient direction
        double nx = gx / gradientLength;
        double ny = gy / gradientLength;

        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;
        double opacity = brushSettings?.Opacity ?? 1.0;

        ushort sR = (ushort)(startColor.R * 257);
        ushort sG = (ushort)(startColor.G * 257);
        ushort sB = (ushort)(startColor.B * 257);
        ushort sA = (ushort)(startColor.A * 257);
        ushort eR = (ushort)(endColor.R * 257);
        ushort eG = (ushort)(endColor.G * 257);
        ushort eB = (ushort)(endColor.B * 257);
        ushort eA = (ushort)(endColor.A * 257);

        int offsetX = activeLayer.OffsetX;
        int offsetY = activeLayer.OffsetY;

        for (int y = 0; y < (int)frame.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)frame.Columns; x++)
            {
                // Project pixel onto gradient vector
                double px = (x + offsetX) - dragStart.X;
                double py = (y + offsetY) - dragStart.Y;
                double t = (px * nx + py * ny) / gradientLength;
                t = Math.Clamp(t, 0, 1);

                ushort r = (ushort)(sR + (eR - sR) * t);
                ushort g = (ushort)(sG + (eG - sG) * t);
                ushort b = (ushort)(sB + (eB - sB) * t);

                int offset = x * channels;

                if (opacity >= 1.0)
                {
                    row[offset] = r;
                    row[offset + 1] = g;
                    row[offset + 2] = b;
                    if (hasAlpha && channels > 3)
                    {
                        ushort a = (ushort)(sA + (eA - sA) * t);
                        row[offset + 3] = a;
                    }
                }
                else
                {
                    // Blend with existing pixels
                    row[offset] = (ushort)(row[offset] + (r - row[offset]) * opacity);
                    row[offset + 1] = (ushort)(row[offset + 1] + (g - row[offset + 1]) * opacity);
                    row[offset + 2] = (ushort)(row[offset + 2] + (b - row[offset + 2]) * opacity);
                    if (hasAlpha && channels > 3)
                    {
                        ushort a = (ushort)(sA + (eA - sA) * t);
                        row[offset + 3] = (ushort)(row[offset + 3] + (a - row[offset + 3]) * opacity);
                    }
                }
            }
        }
    }
}
