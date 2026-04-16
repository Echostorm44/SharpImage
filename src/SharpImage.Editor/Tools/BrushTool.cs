using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;
using SharpImage.Image;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Freehand painting tool. Stamps round brush dabs along the stroke path
/// with configurable size, hardness, opacity, flow, and spacing.
/// Operates directly on the active layer's pixel data.
/// </summary>
public sealed class BrushTool : ITool
{
    public string Name => "Brush";
    public string IconResourceKey => "IconPaintbrush";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private BrushSettings? brushSettings;
    private bool isPainting;
    private Point lastDabPoint;
    private double distanceSinceLastDab;
    private Color paintColor;

    /// <summary>Fired at the beginning of a stroke so MainWindow can snapshot for undo.</summary>
    public event Action? StrokeStarted;

    /// <summary>Fired after each pointer-up to let MainWindow commit undo.</summary>
    public event Action? StrokeCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetBrushSettings(BrushSettings settings) => brushSettings = settings;
    public void SetColor(Color color) => paintColor = color;

    public void Activate() { }
    public void Deactivate() { isPainting = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null || brushSettings is null) return;
        isPainting = true;
        lastDabPoint = canvasPoint;
        distanceSinceLastDab = 0;
        StrokeStarted?.Invoke();
        StampDab(canvasPoint);
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
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
            StampDab(new Point(dabX, dabY));
            lastDabPoint = new Point(dabX, dabY);
            distanceSinceLastDab -= spacing;

            // Recalculate remaining distance from new last dab point
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
        // Draw brush cursor circle at last known position
        if (brushSettings is null) return;
        double radius = brushSettings.Size / 2.0 * zoom;
        if (radius < 1) radius = 1;

        // We draw the cursor relative to the canvas, but we only have overlay context.
        // The cursor rendering will be handled by ImageCanvas tracking the mouse position.
    }

    public Control? BuildOptionsBar() => null;

    private void StampDab(Point center)
    {
        if (document is null || brushSettings is null) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        double radius = brushSettings.Size / 2.0;
        int cx = (int)Math.Round(center.X) - activeLayer.OffsetX;
        int cy = (int)Math.Round(center.Y) - activeLayer.OffsetY;
        int r = (int)Math.Ceiling(radius);

        int xMin = Math.Max(0, cx - r);
        int xMax = Math.Min((int)frame.Columns - 1, cx + r);
        int yMin = Math.Max(0, cy - r);
        int yMax = Math.Min((int)frame.Rows - 1, cy + r);

        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;
        double flow = brushSettings.Flow;
        double opacity = brushSettings.Opacity;
        double hardness = brushSettings.Hardness;

        ushort srcR = (ushort)(paintColor.R * 257);  // Scale byte to 16-bit
        ushort srcG = (ushort)(paintColor.G * 257);
        ushort srcB = (ushort)(paintColor.B * 257);
        ushort srcA = (ushort)(paintColor.A * 257);

        var selectionMask = document.SelectionMask;
        int docWidth = document.Width;

        for (int y = yMin; y <= yMax; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
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
                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;

                // Compute brush falloff based on hardness
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

                int offset = x * channels;
                ushort dstR = row[offset];
                ushort dstG = row[offset + 1];
                ushort dstB = row[offset + 2];

                // Alpha composite (source over)
                ushort blendedR = (ushort)(dstR + (srcR - dstR) * alpha);
                ushort blendedG = (ushort)(dstG + (srcG - dstG) * alpha);
                ushort blendedB = (ushort)(dstB + (srcB - dstB) * alpha);

                row[offset] = blendedR;
                row[offset + 1] = blendedG;
                row[offset + 2] = blendedB;

                if (hasAlpha && channels > 3)
                {
                    ushort dstA = row[offset + 3];
                    ushort targetA = (ushort)(srcA * alpha);
                    // Union of source and dest alpha
                    ushort newA = (ushort)Math.Min(Quantum.MaxValue, dstA + targetA - (dstA * targetA / Quantum.MaxValue));
                    row[offset + 3] = newA;
                }
            }
        }
    }
}
