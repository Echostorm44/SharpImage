using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Quantize;

/// <summary>
/// Color quantization using median-cut algorithm and optional Floyd-Steinberg dithering. Reduces image to N colors by
/// building an optimal palette.
/// </summary>
public static class ColorQuantize
{
    /// <summary>
    /// Dithering method to apply after quantization.
    /// </summary>
    public enum DitherMethod
    {
        None,
        FloydSteinberg
    }

    /// <summary>
    /// Quantize the image to the specified number of colors. Returns a new ImageFrame with the reduced palette applied.
    /// </summary>
    public static ImageFrame Quantize(ImageFrame image, int maxColors, DitherMethod dither = DitherMethod.FloydSteinberg)
    {
        if (maxColors < 2)
        {
            maxColors = 2;
        }

        if (maxColors > 256)
        {
            maxColors = 256;
        }

        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        // Extract all pixels as byte RGB
        var pixels = new byte[width * height * 3];
        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int pIdx = (y * width + x) * 3;
                pixels[pIdx] = Quantum.ScaleToByte(row[off]);
                pixels[pIdx + 1] = Quantum.ScaleToByte(row[off + 1]);
                pixels[pIdx + 2] = Quantum.ScaleToByte(row[off + 2]);
            }
        }

        // Build palette via median-cut
        byte[][] palette = MedianCut(pixels, width * height, maxColors);

        // Apply palette with optional dithering
        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, image.Colorspace, image.HasAlpha);

        if (dither == DitherMethod.FloydSteinberg)
        {
            ApplyFloydSteinberg(image, result, palette, width, height, channels);
        }
        else
        {
            ApplyDirect(image, result, palette, width, height, channels);
        }

        return result;
    }

    /// <summary>
    /// Build a palette of N colors from the image using median-cut. Returns array of [R,G,B] byte triplets.
    /// </summary>
    public static byte[][] BuildPalette(ImageFrame image, int maxColors)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        var pixels = new byte[width * height * 3];
        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int pIdx = (y * width + x) * 3;
                pixels[pIdx] = Quantum.ScaleToByte(row[off]);
                pixels[pIdx + 1] = channels >= 2 ? Quantum.ScaleToByte(row[off + 1]) : pixels[pIdx];
                pixels[pIdx + 2] = channels >= 3 ? Quantum.ScaleToByte(row[off + 2]) : pixels[pIdx];
            }
        }
        return MedianCut(pixels, width * height, maxColors);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Median-Cut Algorithm
    // ═══════════════════════════════════════════════════════════════════

    private struct ColorBox
    {
        public int Start, Count;
        public byte MinR, MaxR, MinG, MaxG, MinB, MaxB;

        public int LongestAxis
        {
            get
            {
                int rangeR = MaxR - MinR;
                int rangeG = MaxG - MinG;
                int rangeB = MaxB - MinB;
                if (rangeR >= rangeG && rangeR >= rangeB)
                {
                    return 0;
                }

                if (rangeG >= rangeR && rangeG >= rangeB)
                {
                    return 1;
                }

                return 2;
            }
        }

        public int Volume => (MaxR - MinR + 1) * (MaxG - MinG + 1) * (MaxB - MinB + 1);
    }

    private static byte[][] MedianCut(byte[] pixels, int pixelCount, int maxColors)
    {
        // Initial box encompassing all pixels
        var boxes = new List<ColorBox>(maxColors);
        var box = CreateBox(pixels, 0, pixelCount);
        boxes.Add(box);

        // Repeatedly split the box with largest volume along its longest axis
        while (boxes.Count < maxColors)
        {
            int bestIdx = -1;
            int bestVolume = 0;
            for (int i = 0;i < boxes.Count;i++)
            {
                if (boxes[i].Count < 2)
                {
                    continue;
                }

                int vol = boxes[i].Volume;
                if (vol > bestVolume)
                {
                    bestVolume = vol;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
            {
                break;
            }

            var b = boxes[bestIdx];
            int axis = b.LongestAxis;

            // Sort pixels in this box by the longest axis
            SortPixelsByAxis(pixels, b.Start, b.Count, axis);

            // Split at median
            int mid = b.Count / 2;
            var box1 = CreateBox(pixels, b.Start, mid);
            var box2 = CreateBox(pixels, b.Start + mid, b.Count - mid);

            boxes[bestIdx] = box1;
            boxes.Add(box2);
        }

        // Compute average color per box
        byte[][] palette = new byte[boxes.Count][];
        for (int i = 0;i < boxes.Count;i++)
        {
            var bx = boxes[i];
            long sumR = 0, sumG = 0, sumB = 0;
            for (int j = 0;j < bx.Count;j++)
            {
                int idx = (bx.Start + j) * 3;
                sumR += pixels[idx];
                sumG += pixels[idx + 1];
                sumB += pixels[idx + 2];
            }
            palette[i] = [ (byte)(sumR / bx.Count), (byte)(sumG / bx.Count), (byte)(sumB / bx.Count) ];
        }

        return palette;
    }

    private static ColorBox CreateBox(byte[] pixels, int start, int count)
    {
        var box = new ColorBox
        {
            Start = start, Count = count,
            MinR = 255, MaxR = 0, MinG = 255, MaxG = 0, MinB = 255, MaxB = 0
        };

        for (int i = 0;i < count;i++)
        {
            int idx = (start + i) * 3;
            byte r = pixels[idx], g = pixels[idx + 1], b = pixels[idx + 2];
            if (r < box.MinR)
            {
                box.MinR = r;
            }

            if (r > box.MaxR)
            {
                box.MaxR = r;
            }

            if (g < box.MinG)
            {
                box.MinG = g;
            }

            if (g > box.MaxG)
            {
                box.MaxG = g;
            }

            if (b < box.MinB)
            {
                box.MinB = b;
            }

            if (b > box.MaxB)
            {
                box.MaxB = b;
            }
        }
        return box;
    }

    private static void SortPixelsByAxis(byte[] pixels, int start, int count, int axis)
    {
        // Simple insertion sort for small boxes, quicksort for large
        var keys = new byte[count];
        for (int i = 0;i < count;i++)
        {
            keys[i] = pixels[(start + i) * 3 + axis];
        }

        var indices = new int[count];
        for (int i = 0;i < count;i++)
        {
            indices[i] = i;
        }

        Array.Sort(keys, indices);

        // Rearrange pixels according to sorted indices
        var temp = new byte[count * 3];
        for (int i = 0;i < count;i++)
        {
            int srcIdx = (start + indices[i]) * 3;
            temp[i * 3] = pixels[srcIdx];
            temp[i * 3 + 1] = pixels[srcIdx + 1];
            temp[i * 3 + 2] = pixels[srcIdx + 2];
        }
        Buffer.BlockCopy(temp, 0, pixels, start * 3, count * 3);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Palette Application
    // ═══════════════════════════════════════════════════════════════════

    private static void ApplyDirect(ImageFrame source, ImageFrame dest, byte[][] palette,
        int width, int height, int channels)
    {
        int destChannels = dest.NumberOfChannels;
        bool hasAlpha = channels >= 4 && destChannels >= 4;
        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = dest.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int dOff = x * destChannels;
                byte r = Quantum.ScaleToByte(srcRow[off]);
                byte g = Quantum.ScaleToByte(srcRow[off + 1]);
                byte b = Quantum.ScaleToByte(srcRow[off + 2]);
                byte[] nearest = FindNearestColor(palette, r, g, b);
                dstRow[dOff] = Quantum.ScaleFromByte(nearest[0]);
                dstRow[dOff + 1] = Quantum.ScaleFromByte(nearest[1]);
                dstRow[dOff + 2] = Quantum.ScaleFromByte(nearest[2]);
                if (hasAlpha)
                {
                    dstRow[dOff + 3] = srcRow[off + 3]; // preserve alpha
                }
            }
        });
    }

    private static void ApplyFloydSteinberg(ImageFrame source, ImageFrame dest, byte[][] palette,
        int width, int height, int channels)
    {
        int destChannels = dest.NumberOfChannels;
        bool hasAlpha = channels >= 4 && destChannels >= 4;

        // Work in float space for error diffusion
        float[] errorR = new float[width + 2];
        float[] errorG = new float[width + 2];
        float[] errorB = new float[width + 2];
        float[] nextErrorR = new float[width + 2];
        float[] nextErrorG = new float[width + 2];
        float[] nextErrorB = new float[width + 2];

        for (int y = 0;y < height;y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = dest.GetPixelRowForWrite(y);
            Array.Clear(nextErrorR);
            Array.Clear(nextErrorG);
            Array.Clear(nextErrorB);

            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int dOff = x * destChannels;
                float r = Quantum.ScaleToByte(srcRow[off]) + errorR[x + 1];
                float g = Quantum.ScaleToByte(srcRow[off + 1]) + errorG[x + 1];
                float b = Quantum.ScaleToByte(srcRow[off + 2]) + errorB[x + 1];

                byte qr = (byte)Math.Clamp(r + 0.5f, 0, 255);
                byte qg = (byte)Math.Clamp(g + 0.5f, 0, 255);
                byte qb = (byte)Math.Clamp(b + 0.5f, 0, 255);

                byte[] nearest = FindNearestColor(palette, qr, qg, qb);
                dstRow[dOff] = Quantum.ScaleFromByte(nearest[0]);
                dstRow[dOff + 1] = Quantum.ScaleFromByte(nearest[1]);
                dstRow[dOff + 2] = Quantum.ScaleFromByte(nearest[2]);
                if (hasAlpha)
                {
                    dstRow[dOff + 3] = srcRow[off + 3]; // preserve alpha
                }

                // Error diffusion (Floyd-Steinberg weights: 7/16, 3/16, 5/16, 1/16)
                float er = r - nearest[0];
                float eg = g - nearest[1];
                float eb = b - nearest[2];

                errorR[x + 2] += er * (7f / 16f);
                errorG[x + 2] += eg * (7f / 16f);
                errorB[x + 2] += eb * (7f / 16f);
                nextErrorR[x] += er * (3f / 16f);
                nextErrorG[x] += eg * (3f / 16f);
                nextErrorB[x] += eb * (3f / 16f);
                nextErrorR[x + 1] += er * (5f / 16f);
                nextErrorG[x + 1] += eg * (5f / 16f);
                nextErrorB[x + 1] += eb * (5f / 16f);
                nextErrorR[x + 2] += er * (1f / 16f);
                nextErrorG[x + 2] += eg * (1f / 16f);
                nextErrorB[x + 2] += eb * (1f / 16f);
            }

            // Swap error rows
            (errorR, nextErrorR) = (nextErrorR, errorR);
            (errorG, nextErrorG) = (nextErrorG, errorG);
            (errorB, nextErrorB) = (nextErrorB, errorB);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] FindNearestColor(byte[][] palette, byte r, byte g, byte b)
    {
        int bestDist = int.MaxValue;
        byte[] best = palette[0];
        for (int i = 0;i < palette.Length;i++)
        {
            int dr = r - palette[i][0];
            int dg = g - palette[i][1];
            int db = b - palette[i][2];
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = palette[i];
            }
        }
        return best;
    }
}
