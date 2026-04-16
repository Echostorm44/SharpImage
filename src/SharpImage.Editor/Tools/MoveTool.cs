using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Move tool: click-drag to move the active layer's content.
/// Arrow keys nudge 1px, Shift+arrow nudges 10px.
/// </summary>
public sealed class MoveTool : ITool
{
    public string Name => "Move";
    public string IconResourceKey => "IconMousePointer";
    public Cursor ToolCursor => new(StandardCursorType.SizeAll);

    private EditorDocument? document;
    private bool isDragging;
    private Point dragStart;
    private int originalOffsetX;
    private int originalOffsetY;

    /// <summary>Fired when the layer position changes — the canvas should refresh.</summary>
    public event Action? LayerMoved;

    /// <summary>Fired when a move drag completes — push undo state for the offset change.</summary>
    public event Action<int, int, int, int>? MoveCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;

    public void Activate() { }
    public void Deactivate() { isDragging = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;
        isDragging = true;
        dragStart = canvasPoint;
        var layer = document.Layers[document.ActiveLayerIndex];
        originalOffsetX = layer.OffsetX;
        originalOffsetY = layer.OffsetY;
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isDragging || document is null) return;

        var delta = canvasPoint - dragStart;
        var layer = document.Layers[document.ActiveLayerIndex];
        layer.OffsetX = originalOffsetX + (int)delta.X;
        layer.OffsetY = originalOffsetY + (int)delta.Y;
        LayerMoved?.Invoke();
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isDragging || document is null) return;
        isDragging = false;

        var layer = document.Layers[document.ActiveLayerIndex];
        int newOffsetX = layer.OffsetX;
        int newOffsetY = layer.OffsetY;

        // Only fire if actually moved
        if (newOffsetX != originalOffsetX || newOffsetY != originalOffsetY)
            MoveCompleted?.Invoke(originalOffsetX, originalOffsetY, newOffsetX, newOffsetY);
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (document is null) return;

        int nudge = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var layer = document.Layers[document.ActiveLayerIndex];
        int oldX = layer.OffsetX, oldY = layer.OffsetY;

        switch (e.Key)
        {
            case Key.Left:  layer.OffsetX -= nudge; break;
            case Key.Right: layer.OffsetX += nudge; break;
            case Key.Up:    layer.OffsetY -= nudge; break;
            case Key.Down:  layer.OffsetY += nudge; break;
            default: return;
        }

        e.Handled = true;
        MoveCompleted?.Invoke(oldX, oldY, layer.OffsetX, layer.OffsetY);
        LayerMoved?.Invoke();
    }

    public void OnKeyUp(KeyEventArgs e) { }
    public void RenderOverlay(DrawingContext context, double zoom) { }
    public Control? BuildOptionsBar() => null;
}
