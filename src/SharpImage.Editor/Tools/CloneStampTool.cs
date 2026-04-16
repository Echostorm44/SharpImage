using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Clone Stamp tool. Alt+click sets the source point, then paint to copy
/// source pixels with a constant offset. Uses BrushSettings for size/hardness/opacity.
/// Respects selection mask and renders a crosshair at the source during painting.
/// </summary>
public sealed class CloneStampTool : ITool
{
    public string Name => "Clone Stamp";
    public string IconResourceKey => "IconStamp";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private BrushSettings? brushSettings;

    private bool sourceSet;
    private Point sourceOrigin;
    private double offsetX;
    private double offsetY;
    private bool isPainting;
    private Point lastDabPoint;
    private double distanceSinceLastDab;
    private bool offsetLocked;
    private Point currentCursorPoint;

    /// <summary>Fired at the beginning of a stroke so MainWindow can snapshot for undo.</summary>
    public event Action? StrokeStarted;

    /// <summary>Fired after each pointer-up to let MainWindow commit undo.</summary>
    public event Action? StrokeCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetBrushSettings(BrushSettings settings) => brushSettings = settings;

    public void Activate() { }
    public void Deactivate()
    {
        isPainting = false;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null || brushSettings is null) return;

        // Alt+click sets the source point
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            sourceOrigin = canvasPoint;
            sourceSet = true;
            offsetLocked = false;
            return;
        }

        if (!sourceSet) return;

        // First stroke after setting source: lock the offset
        if (!offsetLocked)
        {
            offsetX = sourceOrigin.X - canvasPoint.X;
            offsetY = sourceOrigin.Y - canvasPoint.Y;
            offsetLocked = true;
        }

        isPainting = true;
        lastDabPoint = canvasPoint;
        distanceSinceLastDab = 0;
        currentCursorPoint = canvasPoint;
        StrokeStarted?.Invoke();
        StampCloneDab(canvasPoint);
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        currentCursorPoint = canvasPoint;

        if (!isPainting || document is null || brushSettings is null) return;

        double dx = canvasPoint.X - lastDabPoint.X;
        double dy = canvasPoint.Y - lastDabPoint.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < 0.5) return;

        double spacing = Math.Max(1, brushSettings.Size * brushSettings.Spacing);
        distanceSinceLastDab += distance;

        while (distanceSinceLastDab >= spacing)
        {
            double t = spacing / distance;
            double dabX = lastDabPoint.X + dx * t;
            double dabY = lastDabPoint.Y + dy * t;
            StampCloneDab(new Point(dabX, dabY));
            lastDabPoint = new Point(dabX, dabY);
            distanceSinceLastDab -= spacing;

            dx = canvasPoint.X - lastDabPoint.X;
            dy = canvasPoint.Y - lastDabPoint.Y;
            distance = Math.Sqrt(dx * dx + dy * dy);
        }

        lastDabPoint = canvasPoint;
    }

    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint)
    {
        if (!isPainting) return;
        isPainting = false;
        StrokeCompleted?.Invoke();
    }

    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (!sourceSet || !offsetLocked) return;

        // Draw crosshair at the current source sample point (coordinates are in image space, already transformed)
        double srcX = currentCursorPoint.X + offsetX;
        double srcY = currentCursorPoint.Y + offsetY;
        double crossSize = 8 / zoom;

        var pen = new Pen(Brushes.White, 1.5 / zoom);
        var penDash = new Pen(Brushes.Black, 1.5 / zoom)
        {
            DashStyle = new DashStyle([3, 3], 0),
        };

        context.DrawLine(pen, new Point(srcX - crossSize, srcY), new Point(srcX + crossSize, srcY));
        context.DrawLine(pen, new Point(srcX, srcY - crossSize), new Point(srcX, srcY + crossSize));
        context.DrawLine(penDash, new Point(srcX - crossSize, srcY), new Point(srcX + crossSize, srcY));
        context.DrawLine(penDash, new Point(srcX, srcY - crossSize), new Point(srcX, srcY + crossSize));

        // Source circle
        double radius = (brushSettings?.Size ?? 20) / 2.0;
        context.DrawEllipse(null, pen, new Point(srcX, srcY), radius, radius);
    }

    public Control? BuildOptionsBar() => null;

    private void StampCloneDab(Point center)
    {
        if (document is null || brushSettings is null) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        double radius = brushSettings.Size / 2.0;
        int cx = (int)Math.Round(center.X) - activeLayer.OffsetX;
        int cy = (int)Math.Round(center.Y) - activeLayer.OffsetY;
        int r = (int)Math.Ceiling(radius);

        // Source center in layer coords
        int srcCx = (int)Math.Round(center.X + offsetX) - activeLayer.OffsetX;
        int srcCy = (int)Math.Round(center.Y + offsetY) - activeLayer.OffsetY;

        int xMin = Math.Max(0, cx - r);
        int xMax = Math.Min((int)frame.Columns - 1, cx + r);
        int yMin = Math.Max(0, cy - r);
        int yMax = Math.Min((int)frame.Rows - 1, cy + r);

        int channels = frame.NumberOfChannels;
        double flow = brushSettings.Flow;
        double opacity = brushSettings.Opacity;
        double hardness = brushSettings.Hardness;

        var selectionMask = document.SelectionMask;
        int docWidth = document.Width;

        for (int y = yMin; y <= yMax; y++)
        {
            int srcY = y - cy + srcCy;
            if (srcY < 0 || srcY >= (int)frame.Rows) continue;

            var dstRow = frame.GetPixelRowForWrite(y);
            var srcRow = frame.GetPixelRow(srcY);

            for (int x = xMin; x <= xMax; x++)
            {
                // Respect selection mask
                if (selectionMask is not null)
                {
                    int imgX = x + activeLayer.OffsetX;
                    int imgY = y + activeLayer.OffsetY;
                    if (imgX < 0 || imgX >= docWidth || imgY < 0 || imgY >= document.Height) continue;
                    if (selectionMask[imgY * docWidth + imgX] == 0) continue;
                }

                int srcX = x - cx + srcCx;
                if (srcX < 0 || srcX >= (int)frame.Columns) continue;

                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;

                // Brush falloff
                double normalizedDist = dist / radius;
                double falloff;
                if (hardness >= 1.0)
                {
                    falloff = 1.0;
                }
                else
                {
                    double hardEdge = hardness;
                    falloff = normalizedDist <= hardEdge ? 1.0
                        : 1.0 - (normalizedDist - hardEdge) / (1.0 - hardEdge);
                    falloff = Math.Clamp(falloff, 0, 1);
                }

                double alpha = falloff * flow * opacity;
                if (alpha < 0.001) continue;

                int dstOffset = x * channels;
                int srcOffset = srcX * channels;

                // Blend source pixel into destination
                for (int c = 0; c < channels; c++)
                {
                    ushort srcVal = srcRow[srcOffset + c];
                    ushort dstVal = dstRow[dstOffset + c];
                    dstRow[dstOffset + c] = (ushort)(dstVal + (srcVal - dstVal) * alpha);
                }
            }
        }
    }
}
