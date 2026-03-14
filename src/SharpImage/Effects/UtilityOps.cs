// SharpImage — Utility image operations: texture tiling and palette remapping.

using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Analysis;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Utility image operations that don't fit neatly into other categories.
/// </summary>
public static class UtilityOps
{
    // ─── Texture Image (Tile) ─────────────────────────────────────

    /// <summary>
    /// Tiles a texture/pattern across the entire target image by repeating the texture.
    /// The target image is filled completely with the tiled texture.
    /// </summary>
    /// <param name="target">Target image to fill with the tiled texture.</param>
    /// <param name="texture">Texture image to tile.</param>
    public static ImageFrame TextureImage(ImageFrame target, ImageFrame texture)
    {
        int targetW = (int)target.Columns;
        int targetH = (int)target.Rows;
        int texW = (int)texture.Columns;
        int texH = (int)texture.Rows;
        int ch = target.NumberOfChannels;
        bool hasAlpha = target.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(target.Columns, target.Rows, target.Colorspace, hasAlpha);

        for (int y = 0; y < targetH; y++)
        {
            int texY = y % texH;
            var texRow = texture.GetPixelRow(texY);

            for (int x = 0; x < targetW; x++)
            {
                int texX = x % texW;
                int texCh = texture.NumberOfChannels;
                int texOffset = texX * texCh;
                int colorCh = Math.Min(ch, texCh);

                for (int c = 0; c < colorCh; c++)
                    result.SetPixelChannel(x, y, c, texRow[texOffset + c]);

                // If texture has no alpha but target does, set alpha to opaque
                if (hasAlpha && !texture.HasAlpha)
                    result.SetPixelChannel(x, y, ch - 1, Quantum.MaxValue);
            }
        }

        return result;
    }

    // ─── Remap Image (Palette Remap) ──────────────────────────────

    /// <summary>
    /// Remaps each pixel in the source image to the nearest color in the reference palette.
    /// Uses Euclidean distance in RGB space for matching.
    /// </summary>
    /// <param name="source">Source image to remap.</param>
    /// <param name="palette">Array of palette colors to map to.</param>
    /// <param name="useDither">If true, applies Floyd-Steinberg error diffusion dithering.</param>
    public static ImageFrame RemapImage(ImageFrame source, PaletteExtraction.PaletteColor[] palette,
        bool useDither = false)
    {
        if (palette.Length == 0)
            throw new ArgumentException("Palette must contain at least one color.");

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        // Pre-convert palette to quantum values
        ushort[][] paletteQuantum = new ushort[palette.Length][];
        for (int i = 0; i < palette.Length; i++)
        {
            paletteQuantum[i] =
            [
                Quantum.Clamp(palette[i].R * Quantum.MaxValue),
                Quantum.Clamp(palette[i].G * Quantum.MaxValue),
                Quantum.Clamp(palette[i].B * Quantum.MaxValue)
            ];
        }

        if (useDither)
            RemapWithDither(source, result, paletteQuantum, w, h, ch, colorCh, hasAlpha);
        else
            RemapDirect(source, result, paletteQuantum, w, h, ch, colorCh, hasAlpha);

        return result;
    }

    /// <summary>
    /// Remaps each pixel in the source image to the nearest color in the reference image's palette.
    /// Extracts palette from reference image, then remaps.
    /// </summary>
    /// <param name="source">Source image to remap.</param>
    /// <param name="reference">Reference image whose colors define the palette.</param>
    /// <param name="colorCount">Number of colors to extract from reference (default 256).</param>
    /// <param name="useDither">If true, applies Floyd-Steinberg error diffusion dithering.</param>
    public static ImageFrame RemapImage(ImageFrame source, ImageFrame reference,
        int colorCount = 256, bool useDither = false)
    {
        var palette = PaletteExtraction.Extract(reference, colorCount);
        return RemapImage(source, palette.ToArray(), useDither);
    }

    private static void RemapDirect(ImageFrame source, ImageFrame result,
        ushort[][] palette, int w, int h, int ch, int colorCh, bool hasAlpha)
    {
        for (int y = 0; y < h; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int offset = x * ch;
                ushort r = row[offset];
                ushort g = colorCh >= 2 ? row[offset + 1] : r;
                ushort b = colorCh >= 3 ? row[offset + 2] : r;

                int bestIdx = FindNearestPaletteColor(r, g, b, palette);

                result.SetPixelChannel(x, y, 0, palette[bestIdx][0]);
                if (colorCh >= 2) result.SetPixelChannel(x, y, 1, palette[bestIdx][1]);
                if (colorCh >= 3) result.SetPixelChannel(x, y, 2, palette[bestIdx][2]);

                if (hasAlpha)
                    result.SetPixelChannel(x, y, ch - 1, row[offset + ch - 1]);
            }
        }
    }

    private static void RemapWithDither(ImageFrame source, ImageFrame result,
        ushort[][] palette, int w, int h, int ch, int colorCh, bool hasAlpha)
    {
        // Error buffers for Floyd-Steinberg dithering
        double[] errorR = new double[w * h];
        double[] errorG = new double[w * h];
        double[] errorB = new double[w * h];

        for (int y = 0; y < h; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int offset = x * ch;
                int idx = y * w + x;

                double r = row[offset] + errorR[idx];
                double g = colorCh >= 2 ? row[offset + 1] + errorG[idx] : r;
                double b = colorCh >= 3 ? row[offset + 2] + errorB[idx] : r;

                ushort qr = Quantum.Clamp(r);
                ushort qg = Quantum.Clamp(g);
                ushort qb = Quantum.Clamp(b);

                int bestIdx = FindNearestPaletteColor(qr, qg, qb, palette);

                result.SetPixelChannel(x, y, 0, palette[bestIdx][0]);
                if (colorCh >= 2) result.SetPixelChannel(x, y, 1, palette[bestIdx][1]);
                if (colorCh >= 3) result.SetPixelChannel(x, y, 2, palette[bestIdx][2]);

                if (hasAlpha)
                    result.SetPixelChannel(x, y, ch - 1, row[offset + ch - 1]);

                // Compute and diffuse error
                double errR = r - palette[bestIdx][0];
                double errG = g - palette[bestIdx][1];
                double errB = b - palette[bestIdx][2];

                DiffuseError(errorR, errR, x, y, w, h);
                DiffuseError(errorG, errG, x, y, w, h);
                DiffuseError(errorB, errB, x, y, w, h);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DiffuseError(double[] errorBuf, double err, int x, int y, int w, int h)
    {
        // Floyd-Steinberg coefficients: 7/16, 3/16, 5/16, 1/16
        if (x + 1 < w)
            errorBuf[y * w + x + 1] += err * (7.0 / 16.0);
        if (y + 1 < h)
        {
            if (x > 0) errorBuf[(y + 1) * w + x - 1] += err * (3.0 / 16.0);
            errorBuf[(y + 1) * w + x] += err * (5.0 / 16.0);
            if (x + 1 < w) errorBuf[(y + 1) * w + x + 1] += err * (1.0 / 16.0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNearestPaletteColor(ushort r, ushort g, ushort b, ushort[][] palette)
    {
        long bestDist = long.MaxValue;
        int bestIdx = 0;

        for (int i = 0; i < palette.Length; i++)
        {
            long dr = r - palette[i][0];
            long dg = g - palette[i][1];
            long db = b - palette[i][2];
            long dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    // ─── Sort Pixels ──────────────────────────────────────────────

    /// <summary>
    /// Sorts each row's pixels by luminance (ascending). Creates a striking artistic effect
    /// where each scan line becomes a smooth gradient of its original colors.
    /// </summary>
    public static ImageFrame SortPixels(ImageFrame source)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        var result = new ImageFrame();
        result.Initialize(w, h, source.Colorspace, hasAlpha);

        Parallel.For(0, h, () => new (double luma, int index)[w],
            (y, _, sortBuffer) =>
            {
                var srcRow = source.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(y);

                // Compute luminance for each pixel and sort indices by it
                for (int x = 0; x < w; x++)
                {
                    int off = x * ch;
                    double luma;
                    if (colorCh >= 3)
                        luma = 0.299 * srcRow[off] + 0.587 * srcRow[off + 1] + 0.114 * srcRow[off + 2];
                    else
                        luma = srcRow[off];
                    sortBuffer[x] = (luma, x);
                }

                Array.Sort(sortBuffer, 0, w, Comparer<(double luma, int index)>.Create(
                    (a, b) => a.luma.CompareTo(b.luma)));

                // Write sorted pixels
                for (int x = 0; x < w; x++)
                {
                    int srcOff = sortBuffer[x].index * ch;
                    int dstOff = x * ch;
                    for (int c = 0; c < ch; c++)
                        dstRow[dstOff + c] = srcRow[srcOff + c];
                }

                return sortBuffer;
            },
            _ => { });

        return result;
    }

    // ─── Unique Colors ────────────────────────────────────────────

    /// <summary>
    /// Returns the count of distinct colors in the image.
    /// Only considers color channels (not alpha).
    /// </summary>
    public static int CountUniqueColors(ImageFrame source)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        var uniqueColors = new HashSet<long>();

        for (int y = 0; y < h; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                // Pack RGB into a single long for fast set lookup
                long key = row[off];
                if (colorCh >= 2) key |= (long)row[off + 1] << 16;
                if (colorCh >= 3) key |= (long)row[off + 2] << 32;
                uniqueColors.Add(key);
            }
        }

        return uniqueColors.Count;
    }

    /// <summary>
    /// Returns an image containing one pixel per unique color, laid out as a single row.
    /// Useful for generating a visual palette of all distinct colors in an image.
    /// </summary>
    public static ImageFrame UniqueColorsImage(ImageFrame source)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        var uniqueColors = new HashSet<long>();
        var colorList = new List<ushort[]>();

        for (int y = 0; y < h; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                long key = row[off];
                if (colorCh >= 2) key |= (long)row[off + 1] << 16;
                if (colorCh >= 3) key |= (long)row[off + 2] << 32;

                if (uniqueColors.Add(key))
                {
                    var pixel = new ushort[ch];
                    for (int c = 0; c < ch; c++)
                        pixel[c] = row[off + c];
                    colorList.Add(pixel);
                }
            }
        }

        int count = colorList.Count;
        var result = new ImageFrame();
        result.Initialize(count, 1, source.Colorspace, hasAlpha);

        var dstRow = result.GetPixelRowForWrite(0);
        for (int i = 0; i < count; i++)
        {
            int dstOff = i * ch;
            for (int c = 0; c < ch; c++)
                dstRow[dstOff + c] = colorList[i][c];
        }

        return result;
    }

    // ─── Clamp ────────────────────────────────────────────────────

    /// <summary>
    /// Forces all pixel channel values into the valid quantum range [0, MaxValue].
    /// Useful after floating-point arithmetic that may produce out-of-range values.
    /// Modifies the image in-place.
    /// </summary>
    public static void ClampImage(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int ch = image.NumberOfChannels;
        ushort qMax = Quantum.MaxValue;

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            int rowLen = w * ch;
            for (int i = 0; i < rowLen; i++)
            {
                if (row[i] > qMax) row[i] = qMax;
            }
        }
    }

    // ─── Cycle Colormap ───────────────────────────────────────────

    /// <summary>
    /// Cycles pixel color values by rotating all channel values by the specified offset.
    /// Each channel value is shifted and wrapped around the quantum range.
    /// </summary>
    /// <param name="image">Image to modify in-place.</param>
    /// <param name="displacement">Amount to shift values (can be negative). Normalized 0..1 maps to quantum range.</param>
    public static void CycleColormap(ImageFrame image, double displacement)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int ch = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        int shift = (int)(displacement * Quantum.MaxValue);
        int qRange = Quantum.MaxValue + 1;

        Parallel.For(0, h, y =>
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                for (int c = 0; c < colorCh; c++)
                {
                    int val = row[off + c] + shift;
                    // Wrap around
                    val = ((val % qRange) + qRange) % qRange;
                    row[off + c] = (ushort)val;
                }
            }
        });
    }
}
