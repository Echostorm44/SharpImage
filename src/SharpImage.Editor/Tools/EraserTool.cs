using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;
using SharpImage.Image;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Eraser tool — paints transparency (or background color on non-alpha layers).
/// Uses the same dab-stamping approach as BrushTool but writes transparent pixels.
/// </summary>
public sealed class EraserTool : ITool
{
    public string Name => "Eraser";
    public string IconResourceKey => "IconEraser";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private BrushSettings? brushSettings;
    private bool isErasing;
    private Point lastDabPoint;
    private double distanceSinceLastDab;

    /// <summary>Fired at the beginning of a stroke so MainWindow can snapshot for undo.</summary>
    public event Action? StrokeStarted;

    /// <summary>Fired after each pointer-up so MainWindow can commit undo.</summary>
    public event Action? StrokeCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetBrushSettings(BrushSettings settings) => brushSettings = settings;

    public void Activate() { }
    public void Deactivate() { isErasing = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null || brushSettings is null) return;
        isErasing = true;
        lastDabPoint = canvasPoint;
        distanceSinceLastDab = 0;
        StrokeStarted?.Invoke();
        StampErase(canvasPoint);
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint)
    {
        if (!isErasing || document is null || brushSettings is null) return;

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
            StampErase(new Point(dabX, dabY));
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
        if (!isErasing) return;
        isErasing = false;
        StrokeCompleted?.Invoke();
    }

    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }
    public void RenderOverlay(DrawingContext context, double zoom) { }
    public Control? BuildOptionsBar() => null;

    private void StampErase(Point center)
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

                double eraseStrength = falloff * flow * opacity;
                if (eraseStrength < 0.001) continue;

                int offset = x * channels;

                if (hasAlpha && channels > 3)
                {
                    // Reduce alpha toward 0
                    ushort dstA = row[offset + 3];
                    ushort newA = (ushort)(dstA * (1.0 - eraseStrength));
                    row[offset + 3] = newA;
                }
                else
                {
                    // No alpha channel — erase to white
                    row[offset] = (ushort)(row[offset] + (Quantum.MaxValue - row[offset]) * eraseStrength);
                    row[offset + 1] = (ushort)(row[offset + 1] + (Quantum.MaxValue - row[offset + 1]) * eraseStrength);
                    row[offset + 2] = (ushort)(row[offset + 2] + (Quantum.MaxValue - row[offset + 2]) * eraseStrength);
                }
            }
        }
    }
}
