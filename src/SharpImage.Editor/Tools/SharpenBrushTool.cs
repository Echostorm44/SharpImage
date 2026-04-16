using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Localized sharpen brush. Stamps a circular dab that applies an unsharp-mask
/// sharpening effect: sharpened = original + (original - blurred) * strength.
/// The result is blended into the destination using brush falloff.
/// </summary>
public sealed class SharpenBrushTool : ITool
{
    public string Name => "Sharpen";
    public string IconResourceKey => "IconSharpen";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    private EditorDocument? document;
    private BrushSettings? brushSettings;
    private bool isPainting;
    private Point lastDabPoint;
    private double distanceSinceLastDab;

    /// <summary>Sharpen strength from 0.0 to 1.0.</summary>
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
        double flow = brushSettings.Flow;
        double opacity = brushSettings.Opacity;
        double hardness = brushSettings.Hardness;
        double strength = Strength;

        // Fixed 3×3 kernel for the blur used in unsharp mask
        const int kernelRadius = 1;

        var selectionMask = document.SelectionMask;
        int docWidth = document.Width;
        int imgWidth = (int)frame.Columns;
        int imgHeight = (int)frame.Rows;

        Span<long> sums = stackalloc long[channels];

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

                double alpha = falloff * flow * opacity;
                if (alpha < 0.001) continue;

                // Compute average of 3×3 neighborhood
                int count = 0;
                sums.Clear();

                for (int ky = -kernelRadius; ky <= kernelRadius; ky++)
                {
                    int sy = y + ky;
                    if (sy < 0 || sy >= imgHeight) continue;
                    var srcRow = frame.GetPixelRow(sy);
                    for (int kx = -kernelRadius; kx <= kernelRadius; kx++)
                    {
                        int sx = x + kx;
                        if (sx < 0 || sx >= imgWidth) continue;
                        int srcOffset = sx * channels;
                        for (int c = 0; c < channels; c++)
                            sums[c] += srcRow[srcOffset + c];
                        count++;
                    }
                }

                if (count == 0) continue;

                int offset = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    double original = row[offset + c];
                    double averaged = sums[c] / (double)count;
                    // Unsharp mask: sharpened = original + (original - averaged) * strength
                    double sharpened = original + (original - averaged) * strength;
                    sharpened = Math.Clamp(sharpened, 0, Quantum.MaxValue);
                    // Blend sharpened result with original using falloff
                    double blended = original + (sharpened - original) * alpha;
                    row[offset + c] = (ushort)Math.Clamp(blended, 0, Quantum.MaxValue);
                }
            }
        }
    }
}
