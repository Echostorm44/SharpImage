using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Text tool. Click to place a text insertion point, type to compose text,
/// then press Enter to commit the text onto the active layer as rasterized pixels.
/// Options include font size and color (from foreground).
/// </summary>
public sealed class TextTool : ITool
{
    public string Name => "Text";
    public string IconResourceKey => "IconType";
    public Cursor ToolCursor => new(StandardCursorType.Ibeam);

    /// <summary>Font size in pixels (height of glyphs).</summary>
    public int FontSize { get; set; } = 24;

    /// <summary>Font family name for text rendering.</summary>
    public string FontFamilyName { get; set; } = "Inter";

    private EditorDocument? document;
    private Color textColor;
    private bool isPlaced;
    private Point placementPoint;
    private string currentText = string.Empty;

    /// <summary>Fired after text is committed to the layer.</summary>
    public event Action? TextCommitted;

    public void SetDocument(EditorDocument? doc) => document = doc;
    public void SetColor(Color color) => textColor = color;

    public void Activate() { }
    public void Deactivate()
    {
        if (isPlaced && currentText.Length > 0)
            CommitText();
        isPlaced = false;
        currentText = string.Empty;
    }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;

        // If we already have pending text, commit it first
        if (isPlaced && currentText.Length > 0)
            CommitText();

        placementPoint = canvasPoint;
        currentText = string.Empty;
        isPlaced = true;
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint) { }
    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint) { }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (!isPlaced) return;

        if (e.Key == Key.Enter)
        {
            if (currentText.Length > 0)
                CommitText();
            isPlaced = false;
            currentText = string.Empty;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            isPlaced = false;
            currentText = string.Empty;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back)
        {
            if (currentText.Length > 0)
                currentText = currentText[..^1];
            e.Handled = true;
            return;
        }

        // Append typed character
        string? keyText = e.KeySymbol;
        if (keyText is not null && keyText.Length == 1 && !char.IsControl(keyText[0]))
        {
            currentText += keyText;
            e.Handled = true;
        }
    }

    public void OnKeyUp(KeyEventArgs e) { }

    public void RenderOverlay(DrawingContext context, double zoom)
    {
        if (!isPlaced) return;

        // Draw text preview at placement point (context is already zoom-scaled)
        var formattedText = new FormattedText(
            currentText.Length > 0 ? currentText : "|",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamilyName, FontStyle.Normal, FontWeight.Normal),
            FontSize,
            new SolidColorBrush(currentText.Length > 0 ? textColor : Colors.Gray));

        var drawPoint = new Point(placementPoint.X, placementPoint.Y);
        context.DrawText(formattedText, drawPoint);
    }

    public Control? BuildOptionsBar() => null;

    private void CommitText()
    {
        if (document is null || currentText.Length == 0) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;

        int startX = (int)Math.Round(placementPoint.X) - activeLayer.OffsetX;
        int startY = (int)Math.Round(placementPoint.Y) - activeLayer.OffsetY;

        // Use Avalonia's vector text to get glyph geometry
        var formattedText = new FormattedText(
            currentText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamilyName, FontStyle.Normal, FontWeight.Normal),
            FontSize,
            Brushes.White);

        var geometry = formattedText.BuildGeometry(new Point(0, 0));
        if (geometry is null) return;

        int textWidth = (int)Math.Ceiling(formattedText.Width) + 2;
        int textHeight = (int)Math.Ceiling(formattedText.Height) + 2;

        ushort tR = (ushort)(textColor.R * 257);
        ushort tG = (ushort)(textColor.G * 257);
        ushort tB = (ushort)(textColor.B * 257);
        ushort tA = (ushort)(textColor.A * 257);

        // Rasterize with 4x4 sub-pixel sampling for anti-aliased text
        const int subSamples = 4;
        const double subStep = 1.0 / subSamples;
        double totalSamples = subSamples * subSamples;

        for (int ty = 0; ty < textHeight; ty++)
        {
            int py = startY + ty;
            if (py < 0 || py >= (int)frame.Rows) continue;

            var row = frame.GetPixelRowForWrite(py);

            for (int tx = 0; tx < textWidth; tx++)
            {
                int px = startX + tx;
                if (px < 0 || px >= (int)frame.Columns) continue;

                // Count sub-pixel samples inside the text geometry
                int hits = 0;
                for (int sy = 0; sy < subSamples; sy++)
                    for (int sx = 0; sx < subSamples; sx++)
                        if (geometry.FillContains(new Point(tx + (sx + 0.5) * subStep, ty + (sy + 0.5) * subStep)))
                            hits++;

                if (hits == 0) continue;
                double coverage = hits / totalSamples;

                int offset = px * channels;
                ushort existR = row[offset];
                ushort existG = channels > 1 ? row[offset + 1] : existR;
                ushort existB = channels > 2 ? row[offset + 2] : existR;

                row[offset] = (ushort)(existR + (tR - existR) * coverage);
                if (channels > 1) row[offset + 1] = (ushort)(existG + (tG - existG) * coverage);
                if (channels > 2) row[offset + 2] = (ushort)(existB + (tB - existB) * coverage);
                if (hasAlpha && channels > 3)
                {
                    ushort existA = row[offset + 3];
                    row[offset + 3] = (ushort)Math.Min(65535, existA + (int)(tA * coverage));
                }
            }
        }

        TextCommitted?.Invoke();
    }
}
