using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Zoom tool: click to zoom in, Alt+click to zoom out, drag rectangle to zoom to region.
/// </summary>
public sealed class ZoomTool : ITool
{
    public string Name => "Zoom";
    public string IconResourceKey => "IconZoomIn";
    public Cursor ToolCursor => new(StandardCursorType.Hand);

    /// <summary>Fired when the user clicks to zoom. positive = zoom in, negative = zoom out.</summary>
    public event Action<Point, double>? ZoomRequested;

    public void Activate() { }
    public void Deactivate() { }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        bool zoomOut = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        double factor = zoomOut ? 1.0 / 1.5 : 1.5;
        ZoomRequested?.Invoke(canvasPoint, factor);
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint) { }
    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint) { }
    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }
    public void RenderOverlay(DrawingContext context, double zoom) { }
    public Control? BuildOptionsBar() => null;
}
