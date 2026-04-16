using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Hand tool: click-drag to pan the canvas viewport.
/// Space bar activates this tool temporarily from any other tool.
/// </summary>
public sealed class HandTool : ITool
{
    public string Name => "Hand";
    public string IconResourceKey => "IconHand";
    public Cursor ToolCursor => new(StandardCursorType.Hand);

    private bool isPanning;
    private Point lastPoint;

    /// <summary>Fired when the user drags — the canvas should scroll by this delta.</summary>
    public event Action<Vector>? PanRequested;

    public void Activate() { }
    public void Deactivate() { isPanning = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        isPanning = true;
        lastPoint = e.GetPosition(e.Source as Visual);
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isPanning) return;
        var current = e.GetPosition(e.Source as Visual);
        var delta = current - lastPoint;
        lastPoint = current;
        PanRequested?.Invoke(delta);
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        isPanning = false;
    }

    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }
    public void RenderOverlay(DrawingContext context, double zoom) { }
    public Control? BuildOptionsBar() => null;
}
