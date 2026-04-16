using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Drawing;
using SharpImage.Editor.Models;
using SharpDrawingContext = SharpImage.Drawing.DrawingContext;
using AvDrawingContext = Avalonia.Media.DrawingContext;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Paint bucket (flood fill) tool. Click to flood-fill contiguous pixels of
/// similar color with the foreground color. Uses SharpImage's DrawingContext.FloodFill.
/// </summary>
public sealed class PaintBucketTool : ITool
{
    public string Name => "Paint Bucket";
    public string IconResourceKey => "IconPaintBucket";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private Color fillColor;
    private int tolerance = 15;

    /// <summary>Fired before fill so MainWindow can snapshot for undo.</summary>
    public event Action? FillStarted;

    /// <summary>Fired after a fill operation so MainWindow can commit undo.</summary>
    public event Action? FillCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetColor(Color color) => fillColor = color;

    public void Activate() { }
    public void Deactivate() { }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        int x = (int)Math.Round(canvasPoint.X) - activeLayer.OffsetX;
        int y = (int)Math.Round(canvasPoint.Y) - activeLayer.OffsetY;

        if (x < 0 || x >= frame.Columns || y < 0 || y >= frame.Rows) return;

        FillStarted?.Invoke();

        var ctx = new SharpDrawingContext(frame);
        ctx.FillColor = new DrawColor(fillColor.R, fillColor.G, fillColor.B, fillColor.A);
        ctx.FloodFill(x, y, tolerance);

        FillCompleted?.Invoke();
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint) { }
    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint) { }
    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }
    public void RenderOverlay(AvDrawingContext context, double zoom) { }
    public Control? BuildOptionsBar() => null;
}
