// Label and caption generators using the built-in BitmapFont.
// Label: single-line text, auto-sized.
// Caption: multi-line text with word-wrapping.

using SharpImage.Core;
using SharpImage.Drawing;
using SharpImage.Image;

namespace SharpImage.Generators;

/// <summary>
/// Generates text-as-image using the built-in 5×7 bitmap font.
/// </summary>
public static class LabelGenerator
{
    /// <summary>
    /// Creates a single-line label image with auto-sized dimensions.
    /// </summary>
    public static ImageFrame Label(string text,
        byte textR = 0, byte textG = 0, byte textB = 0,
        byte bgR = 255, byte bgG = 255, byte bgB = 255,
        double scale = 1.0, int padding = 2)
    {
        var (textWidth, textHeight) = BitmapFont.MeasureString(text, scale);
        int width = (int)Math.Ceiling(textWidth) + padding * 2;
        int height = (int)Math.Ceiling(textHeight) + padding * 2;
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        var frame = SolidGenerator.Generate(width, height, bgR, bgG, bgB);
        RenderText(frame, text, padding, padding, scale, textR, textG, textB);
        return frame;
    }

    /// <summary>
    /// Creates a caption image with word-wrapping to fit the specified width.
    /// Height is auto-sized to fit all wrapped text.
    /// </summary>
    public static ImageFrame Caption(string text, int maxWidth,
        byte textR = 0, byte textG = 0, byte textB = 0,
        byte bgR = 255, byte bgG = 255, byte bgB = 255,
        double scale = 1.0, int padding = 2)
    {
        int availableWidth = maxWidth - padding * 2;
        if (availableWidth < 1) availableWidth = 1;

        string wrapped = WrapText(text, availableWidth, scale);
        var (_, textHeight) = BitmapFont.MeasureString(wrapped, scale);
        int height = (int)Math.Ceiling(textHeight) + padding * 2;
        if (height < 1) height = 1;

        var frame = SolidGenerator.Generate(maxWidth, height, bgR, bgG, bgB);
        RenderText(frame, wrapped, padding, padding, scale, textR, textG, textB);
        return frame;
    }

    /// <summary>
    /// Renders text onto an existing frame at the specified position.
    /// </summary>
    private static void RenderText(ImageFrame frame, string text,
        int startX, int startY, double scale,
        byte r, byte g, byte b)
    {
        var pixels = BitmapFont.RenderString(text, scale);
        ushort qr = Quantum.ScaleFromByte(r);
        ushort qg = Quantum.ScaleFromByte(g);
        ushort qb = Quantum.ScaleFromByte(b);
        int ch = frame.NumberOfChannels;
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;

        foreach (var (px, py) in pixels)
        {
            int ix = startX + (int)px;
            int iy = startY + (int)py;
            if (ix < 0 || ix >= width || iy < 0 || iy >= height) continue;

            var row = frame.GetPixelRowForWrite(iy);
            int offset = ix * ch;
            row[offset] = qr;
            row[offset + 1] = qg;
            row[offset + 2] = qb;
        }
    }

    /// <summary>
    /// Word-wraps text to fit within maxPixelWidth at the given scale.
    /// </summary>
    private static string WrapText(string text, int maxPixelWidth, double scale)
    {
        double charWidth = (BitmapFont.GlyphWidth + BitmapFont.GlyphSpacing) * scale;
        int maxCharsPerLine = Math.Max(1, (int)(maxPixelWidth / charWidth));

        var lines = new System.Text.StringBuilder();
        string[] inputLines = text.Split('\n');

        for (int lineIdx = 0; lineIdx < inputLines.Length; lineIdx++)
        {
            if (lineIdx > 0) lines.Append('\n');
            string line = inputLines[lineIdx];

            if (line.Length <= maxCharsPerLine)
            {
                lines.Append(line);
                continue;
            }

            string[] words = line.Split(' ');
            int currentLength = 0;
            bool firstWord = true;

            foreach (string word in words)
            {
                int wordLen = word.Length;
                int neededLength = firstWord ? wordLen : wordLen + 1;

                if (currentLength + neededLength > maxCharsPerLine && !firstWord)
                {
                    lines.Append('\n');
                    currentLength = 0;
                    firstWord = true;
                }

                if (!firstWord)
                {
                    lines.Append(' ');
                    currentLength++;
                }

                lines.Append(word);
                currentLength += wordLen;
                firstWord = false;
            }
        }

        return lines.ToString();
    }
}
