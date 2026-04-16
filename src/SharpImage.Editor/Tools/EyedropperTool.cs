using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Eyedropper: click to sample a pixel's color as the foreground color.
/// Alt+click samples as background color.
/// Shows a color preview ring overlay near the cursor.
/// </summary>
public sealed class EyedropperTool : ITool
{
    public string Name => "Eyedropper";
    public string IconResourceKey => "IconPipette";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private Point lastSamplePoint;
    private Color lastSampledColor = Colors.Transparent;
    private bool hasSample;

    /// <summary>Fires when a color is sampled. Bool is true for foreground, false for background.</summary>
    public event Action<Color, bool>? ColorSampled;

    public void SetDocument(EditorDocument? doc) => document = doc;

    public void Activate() { hasSample = false; }
    public void Deactivate() { hasSample = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        SampleAt(canvasPoint, !e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        // Live sample while dragging
        var props = e.GetCurrentPoint(e.Source as Visual).Properties;
        if (props.IsLeftButtonPressed)
            SampleAt(canvasPoint, !e.KeyModifiers.HasFlag(KeyModifiers.Alt));

        lastSamplePoint = canvasPoint;
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint) { }
    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (!hasSample) return;

        // Draw a small color preview circle at the sample point
        double radius = 12.0 / zoom;
        double strokeWidth = 2.0 / zoom;

        var center = lastSamplePoint;

        // Outer ring (white border)
        context.DrawEllipse(null, new Pen(Brushes.White, strokeWidth), center, radius + strokeWidth, radius + strokeWidth);
        // Color fill
        context.DrawEllipse(new SolidColorBrush(lastSampledColor), new Pen(Brushes.Black, strokeWidth * 0.5), center, radius, radius);
    }

    public Control? BuildOptionsBar() => null;

    private void SampleAt(Point canvasPoint, bool isForeground)
    {
        if (document is null) return;

        int px = (int)Math.Floor(canvasPoint.X);
        int py = (int)Math.Floor(canvasPoint.Y);
        if (px < 0 || py < 0 || px >= document.Width || py >= document.Height) return;

        // Sample from the flattened composite
        var flattened = document.Flatten();
        var row = flattened.GetPixelRow(py);
        int channels = flattened.NumberOfChannels;
        int idx = px * channels;

        if (idx + channels - 1 >= row.Length) return;

        const double scale = 255.0 / 65535.0;
        byte r = (byte)(row[idx] * scale + 0.5);
        byte g = channels > 1 ? (byte)(row[idx + 1] * scale + 0.5) : r;
        byte b = channels > 2 ? (byte)(row[idx + 2] * scale + 0.5) : r;
        byte a = flattened.HasAlpha && channels > 3 ? (byte)(row[idx + 3] * scale + 0.5) : (byte)255;

        lastSampledColor = Color.FromArgb(a, r, g, b);
        lastSamplePoint = canvasPoint;
        hasSample = true;

        ColorSampled?.Invoke(lastSampledColor, isForeground);
    }
}
