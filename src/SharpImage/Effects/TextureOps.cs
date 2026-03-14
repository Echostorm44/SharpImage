// SharpImage — Texture tools: seamless tiling and texture synthesis.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SharpImage.Effects;

/// <summary>
/// Texture manipulation tools: seamless tile generation and exemplar-based
/// texture synthesis.
/// </summary>
public static class TextureOps
{
    // ─── Seamless Tile ──────────────────────────────────────────

    /// <summary>
    /// Makes an image tileable by blending its edges with a cross-fade.
    /// When the result is tiled in a 2×2 grid, seams are invisible.
    /// </summary>
    /// <param name="source">Source image to make tileable.</param>
    /// <param name="blendWidth">Width of the blend zone at edges (0.0–0.5 as fraction of image).</param>
    public static ImageFrame MakeSeamlessTile(ImageFrame source, double blendWidth = 0.25)
    {
        blendWidth = Math.Clamp(blendWidth, 0.01, 0.5);

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int bw = (int)(w * blendWidth);
        int bh = (int)(h * blendWidth);

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        // Copy source
        for (int y = 0; y < h; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }

        // Horizontal blend: left edge blends with right edge
        double invBw = 1.0 / bw;
        Parallel.For(0, h, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            var srcRow = source.GetPixelRow(y);

            for (int x = 0; x < bw; x++)
            {
                double t = x * invBw; // 0 at left edge, 1 at blend boundary
                int leftOff = x * ch;
                int rightX = w - bw + x;
                int rightOff = rightX * ch;

                for (int c = 0; c < ch; c++)
                {
                    // Blend: at x=0, mostly use pixel from right side; at x=bw, keep original
                    double leftVal = srcRow[leftOff + c];
                    double rightVal = srcRow[rightOff + c];
                    dstRow[leftOff + c] = Quantum.Clamp(leftVal * t + rightVal * (1.0 - t));
                    dstRow[rightOff + c] = Quantum.Clamp(rightVal * t + leftVal * (1.0 - t));
                }
            }
        });

        // Vertical blend: top edge blends with bottom edge
        double invBh = 1.0 / bh;
        Parallel.For(0, bh, y =>
        {
            double t = y * invBh;
            int bottomY = h - bh + y;

            var topRow = result.GetPixelRowForWrite(y);
            var botRow = result.GetPixelRowForWrite(bottomY);

            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                for (int c = 0; c < ch; c++)
                {
                    double topVal = topRow[off + c];
                    double botVal = botRow[off + c];
                    topRow[off + c] = Quantum.Clamp(topVal * t + botVal * (1.0 - t));
                    botRow[off + c] = Quantum.Clamp(botVal * t + topVal * (1.0 - t));
                }
            }
        });

        return result;
    }

    // ─── Texture Synthesis ──────────────────────────────────────

    /// <summary>
    /// Synthesizes a larger texture from a small exemplar patch using a
    /// non-parametric patch-based approach (simplified Efros-Leung/Wei-Levoy).
    /// Grows the texture pixel-by-pixel using best-matching neighborhood search.
    /// </summary>
    /// <param name="exemplar">Small exemplar texture patch.</param>
    /// <param name="outputWidth">Width of the synthesized texture.</param>
    /// <param name="outputHeight">Height of the synthesized texture.</param>
    /// <param name="neighborhoodRadius">Radius of the neighborhood used for matching.</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    public static ImageFrame SynthesizeTexture(ImageFrame exemplar, uint outputWidth, uint outputHeight,
        int neighborhoodRadius = 3, int seed = 42)
    {
        int ew = (int)exemplar.Columns;
        int eh = (int)exemplar.Rows;
        int ow = (int)outputWidth;
        int oh = (int)outputHeight;
        int ch = exemplar.NumberOfChannels;
        bool hasAlpha = exemplar.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(outputWidth, outputHeight, exemplar.Colorspace, hasAlpha);
        var filled = new bool[oh * ow];
        var rng = new Random(seed);

        // Seed the center with a random patch from the exemplar
        int seedX = rng.Next(ew);
        int seedY = rng.Next(eh);
        int patchSize = neighborhoodRadius * 2 + 1;
        int startX = ow / 2 - patchSize / 2;
        int startY = oh / 2 - patchSize / 2;

        for (int dy = 0; dy < patchSize; dy++)
        {
            int oy = startY + dy;
            if (oy < 0 || oy >= oh) continue;
            var dstRow = result.GetPixelRowForWrite(oy);
            int sy = (seedY + dy) % eh;
            var srcRow = exemplar.GetPixelRow(sy);

            for (int dx = 0; dx < patchSize; dx++)
            {
                int ox = startX + dx;
                if (ox < 0 || ox >= ow) continue;
                int sx = (seedX + dx) % ew;

                int sOff = sx * ch;
                int dOff = ox * ch;
                for (int c = 0; c < ch; c++) dstRow[dOff + c] = srcRow[sOff + c];
                filled[oy * ow + ox] = true;
            }
        }

        // Pre-cache exemplar pixel data for fast lookup
        var exemplarData = new ushort[eh * ew * ch];
        for (int y = 0; y < eh; y++)
        {
            var row = exemplar.GetPixelRow(y);
            for (int x = 0; x < ew * ch; x++)
                exemplarData[y * ew * ch + x] = row[x];
        }

        // Grow outward in a spiral-like order (BFS from filled pixels)
        var frontier = new Queue<(int x, int y)>();
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
                if (filled[y * ow + x])
                    EnqueueNeighbors(frontier, filled, x, y, ow, oh);

        int maxCandidates = Math.Min(200, ew * eh); // Limit search for performance

        while (frontier.Count > 0)
        {
            var (px, py) = frontier.Dequeue();
            if (filled[py * ow + px]) continue;

            // Find best matching exemplar pixel
            FindBestMatch(result, filled, exemplarData, ew, eh, ch,
                px, py, ow, oh, neighborhoodRadius, maxCandidates, rng,
                out ushort bestR, out ushort bestG, out ushort bestB, out ushort bestA);

            var dstRow = result.GetPixelRowForWrite(py);
            int dOff = px * ch;
            dstRow[dOff] = bestR;
            if (ch > 1) dstRow[dOff + 1] = bestG;
            if (ch > 2) dstRow[dOff + 2] = bestB;
            if (hasAlpha && ch > 3) dstRow[dOff + 3] = bestA;
            filled[py * ow + px] = true;

            EnqueueNeighbors(frontier, filled, px, py, ow, oh);
        }

        // Fill any remaining unfilled pixels with random exemplar pixels
        for (int y = 0; y < oh; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < ow; x++)
            {
                if (filled[y * ow + x]) continue;
                int sx = rng.Next(ew), sy = rng.Next(eh);
                int sOff = (sy * ew + sx) * ch;
                int dOff = x * ch;
                for (int c = 0; c < ch; c++)
                    dstRow[dOff + c] = exemplarData[sOff + c];
            }
        }

        return result;
    }

    private static void EnqueueNeighbors(Queue<(int x, int y)> frontier, bool[] filled, int x, int y, int w, int h)
    {
        if (x > 0 && !filled[y * w + x - 1]) frontier.Enqueue((x - 1, y));
        if (x < w - 1 && !filled[y * w + x + 1]) frontier.Enqueue((x + 1, y));
        if (y > 0 && !filled[(y - 1) * w + x]) frontier.Enqueue((x, y - 1));
        if (y < h - 1 && !filled[(y + 1) * w + x]) frontier.Enqueue((x, y + 1));
    }

    private static void FindBestMatch(ImageFrame output, bool[] filled, ushort[] exemplarData,
        int ew, int eh, int ch, int px, int py, int ow, int oh,
        int radius, int maxCandidates, Random rng,
        out ushort bestR, out ushort bestG, out ushort bestB, out ushort bestA)
    {
        long bestError = long.MaxValue;
        bestR = bestG = bestB = bestA = 0;
        int colorCh = Math.Min(ch, 3);

        for (int i = 0; i < maxCandidates; i++)
        {
            int cx = rng.Next(ew);
            int cy = rng.Next(eh);

            long error = 0;
            int matches = 0;

            for (int dy = -radius; dy <= radius; dy++)
            {
                int oy = py + dy;
                if (oy < 0 || oy >= oh) continue;
                int ey = (cy + dy + eh) % eh;

                var outRow = output.GetPixelRow(oy);
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int ox = px + dx;
                    if (ox < 0 || ox >= ow || !filled[oy * ow + ox]) continue;

                    int ex = (cx + dx + ew) % ew;
                    int eOff = (ey * ew + ex) * ch;
                    int oOff = ox * ch;

                    for (int c = 0; c < colorCh; c++)
                    {
                        long diff = outRow[oOff + c] - exemplarData[eOff + c];
                        error += diff * diff;
                    }
                    matches++;
                }
            }

            if (matches > 0) error /= matches;

            if (error < bestError)
            {
                bestError = error;
                int bOff = (cy * ew + cx) * ch;
                bestR = exemplarData[bOff];
                bestG = ch > 1 ? exemplarData[bOff + 1] : (ushort)0;
                bestB = ch > 2 ? exemplarData[bOff + 2] : (ushort)0;
                bestA = ch > 3 ? exemplarData[bOff + 3] : Quantum.MaxValue;
            }
        }
    }
}
