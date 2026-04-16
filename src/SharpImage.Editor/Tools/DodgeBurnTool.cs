using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Core;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Combined Dodge/Burn/Sponge tool.
/// Dodge lightens pixels, Burn darkens, Sponge adjusts saturation.
/// Uses BrushSettings for size/hardness and has an Exposure slider (0–100%).
/// </summary>
public sealed class DodgeBurnTool : ITool
{
    public string Name => "Dodge/Burn";
    public string IconResourceKey => "IconSun";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    /// <summary>Operating mode for this tool.</summary>
    public enum ToolMode { Dodge, Burn, Sponge }

    /// <summary>Current mode: Dodge lightens, Burn darkens, Sponge adjusts saturation.</summary>
    public ToolMode Mode { get; set; } = ToolMode.Dodge;

    /// <summary>Exposure strength from 0.0 to 1.0 (default 0.5 = 50%).</summary>
    public double Exposure { get; set; } = 0.5;

    private EditorDocument? document;
    private BrushSettings? brushSettings;
    private bool isPainting;
    private Point lastDabPoint;
    private double distanceSinceLastDab;

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
        bool hasAlpha = frame.HasAlpha;
        double flow = brushSettings.Flow;
        double hardness = brushSettings.Hardness;
        double exposure = Exposure;

        var selectionMask = document.SelectionMask;
        int docWidth = document.Width;

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

                double strength = falloff * flow * exposure;
                if (strength < 0.001) continue;

                int offset = x * channels;

                if (Mode == ToolMode.Sponge)
                {
                    ApplySponge(row, offset, channels, hasAlpha, strength);
                }
                else
                {
                    // Dodge: multiply by (1 + strength), Burn: multiply by (1 - strength)
                    double factor = Mode == ToolMode.Dodge ? (1.0 + strength) : (1.0 - strength);
                    int colorChannels = hasAlpha && channels > 3 ? channels - 1 : channels;
                    for (int c = 0; c < colorChannels; c++)
                    {
                        double val = row[offset + c] * factor;
                        row[offset + c] = (ushort)Math.Clamp(val, 0, Quantum.MaxValue);
                    }
                }
            }
        }
    }

    /// <summary>Increase saturation by converting to HSL, adjusting S, converting back.</summary>
    private static void ApplySponge(Span<ushort> row, int offset, int channels, bool hasAlpha, double strength)
    {
        if (channels < 3) return;

        double invMax = 1.0 / Quantum.MaxValue;
        double rNorm = row[offset] * invMax;
        double gNorm = row[offset + 1] * invMax;
        double bNorm = row[offset + 2] * invMax;

        // RGB to HSL
        double max = Math.Max(rNorm, Math.Max(gNorm, bNorm));
        double min = Math.Min(rNorm, Math.Min(gNorm, bNorm));
        double l = (max + min) * 0.5;
        double s = 0;
        double h = 0;

        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

            if (max == rNorm)
                h = (gNorm - bNorm) / d + (gNorm < bNorm ? 6 : 0);
            else if (max == gNorm)
                h = (bNorm - rNorm) / d + 2;
            else
                h = (rNorm - gNorm) / d + 4;

            h /= 6.0;
        }

        // Boost saturation
        s = Math.Clamp(s + strength * 0.3, 0, 1);

        // HSL to RGB
        if (s == 0)
        {
            ushort gray = (ushort)(l * Quantum.MaxValue);
            row[offset] = gray;
            row[offset + 1] = gray;
            row[offset + 2] = gray;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            row[offset] = (ushort)(HueToRgb(p, q, h + 1.0 / 3.0) * Quantum.MaxValue);
            row[offset + 1] = (ushort)(HueToRgb(p, q, h) * Quantum.MaxValue);
            row[offset + 2] = (ushort)(HueToRgb(p, q, h - 1.0 / 3.0) * Quantum.MaxValue);
        }
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
