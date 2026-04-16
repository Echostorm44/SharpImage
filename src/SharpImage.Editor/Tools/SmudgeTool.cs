using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Smudge tool. Picks up the color under the cursor on press, then smears it
/// along the stroke path by blending the pickup color with each destination pixel
/// and updating the pickup to the blended result, creating a finger-painting effect.
/// </summary>
public sealed class SmudgeTool : ITool
{
    public string Name => "Smudge";
    public string IconResourceKey => "IconFingerprint";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private BrushSettings? brushSettings;
    private bool isPainting;
    private Point lastDabPoint;
    private double distanceSinceLastDab;

    // Pickup color (sampled on press, updated each dab for smearing)
    private ushort pickupR, pickupG, pickupB, pickupA;

    /// <summary>Smudge strength from 0.0 to 1.0 (how much color is carried forward).</summary>
    public double Strength { get; set; } = 0.5;

    /// <summary>Fired at the beginning of a stroke so MainWindow can snapshot for undo.</summary>
    public event Action? StrokeStarted;

    /// <summary>Fired after each pointer-up to let MainWindow commit undo.</summary>
    public event Action? StrokeCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetBrushSettings(BrushSettings settings) => brushSettings = settings;

    public void Activate() { }
    public void Deactivate() { isPainting = false; }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null || brushSettings is null) return;

        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        // Sample pixel under cursor as initial pickup color
        int px = (int)Math.Round(canvasPoint.X) - activeLayer.OffsetX;
        int py = (int)Math.Round(canvasPoint.Y) - activeLayer.OffsetY;
        px = Math.Clamp(px, 0, (int)frame.Columns - 1);
        py = Math.Clamp(py, 0, (int)frame.Rows - 1);

        int channels = frame.NumberOfChannels;
        var row = frame.GetPixelRow(py);
        int offset = px * channels;
        pickupR = row[offset];
        pickupG = channels > 1 ? row[offset + 1] : row[offset];
        pickupB = channels > 2 ? row[offset + 2] : row[offset];
        pickupA = (frame.HasAlpha && channels > 3) ? row[offset + 3] : Quantum.MaxValue;

        isPainting = true;
        lastDabPoint = canvasPoint;
        distanceSinceLastDab = 0;
        StrokeStarted?.Invoke();
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
    public void RenderOverlay(DrawingContext context, double zoom) { }
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
        double hardness = brushSettings.Hardness;
        double strength = Strength;

        var selectionMask = document.SelectionMask;
        int docWidth = document.Width;

        // Accumulate weighted average of destination pixels for pickup update
        double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        double totalWeight = 0;

        for (int y = yMin; y <= yMax; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = xMin; x <= xMax; x++)
            {
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

                double alpha = falloff * strength;
                if (alpha < 0.001) continue;

                int offset = x * channels;
                ushort dstR = row[offset];
                ushort dstG = channels > 1 ? row[offset + 1] : dstR;
                ushort dstB = channels > 2 ? row[offset + 2] : dstR;

                // Blend pickup color into destination
                ushort blendedR = (ushort)(dstR + (pickupR - dstR) * alpha);
                ushort blendedG = (ushort)(dstG + (pickupG - dstG) * alpha);
                ushort blendedB = (ushort)(dstB + (pickupB - dstB) * alpha);

                row[offset] = blendedR;
                if (channels > 1) row[offset + 1] = blendedG;
                if (channels > 2) row[offset + 2] = blendedB;

                if (hasAlpha && channels > 3)
                {
                    ushort dstA = row[offset + 3];
                    row[offset + 3] = (ushort)(dstA + (pickupA - dstA) * alpha);
                }

                // Accumulate for pickup update
                sumR += blendedR * falloff;
                sumG += blendedG * falloff;
                sumB += blendedB * falloff;
                sumA += (hasAlpha && channels > 3 ? row[offset + 3] : Quantum.MaxValue) * falloff;
                totalWeight += falloff;
            }
        }

        // Update pickup color to the weighted average of the blended result
        if (totalWeight > 0)
        {
            pickupR = (ushort)Math.Clamp(sumR / totalWeight, 0, Quantum.MaxValue);
            pickupG = (ushort)Math.Clamp(sumG / totalWeight, 0, Quantum.MaxValue);
            pickupB = (ushort)Math.Clamp(sumB / totalWeight, 0, Quantum.MaxValue);
            pickupA = (ushort)Math.Clamp(sumA / totalWeight, 0, Quantum.MaxValue);
        }
    }
}
