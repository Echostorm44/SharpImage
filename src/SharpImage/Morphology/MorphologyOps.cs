using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Morphology;

/// <summary>
/// Distance metric for the distance transform.
/// </summary>
public enum DistanceMetric
{
    Euclidean,
    Manhattan,
    Chebyshev
}

/// <summary>
/// Morphological operations: erode, dilate, open, close, gradient, top-hat, bottom-hat, distance transform.
/// Supports built-in structuring element kernels (diamond, square, disk, cross, plus).
/// </summary>
public static class MorphologyOps
{
    // ═══════════════════════════════════════════════════════════════════
    // Structuring Element Kernel
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A structuring element kernel for morphology operations. Values > 0 are "on" pixels; NaN marks "don't care"
    /// positions.
    /// </summary>
    public sealed class Kernel
    {
        public int Width { get; }
        public int Height { get; }
        public int OriginX { get; }
        public int OriginY { get; }
        public double[] Values { get; }

        public Kernel(int width, int height, int originX, int originY, double[] values)
        {
            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
            Values = values;
        }

        public double this[int x, int y] => Values[y * Width + x];

        /// <summary>
        /// Square kernel of given radius (side = 2*radius+1).
        /// </summary>
        public static Kernel Square(int radius)
        {
            int size = 2 * radius + 1;
            double[] values = new double[size * size];
            Array.Fill(values, 1.0);
            return new Kernel(size, size, radius, radius, values);
        }

        /// <summary>
        /// Diamond kernel of given radius.
        /// </summary>
        public static Kernel Diamond(int radius)
        {
            int size = 2 * radius + 1;
            double[] values = new double[size * size];
            for (int y = 0;y < size;y++)
            {
                for (int x = 0;x < size;x++)
                {
                    values[y * size + x] = (Math.Abs(x - radius) + Math.Abs(y - radius) <= radius)
                                        ? 1.0 : double.NaN;
                }
            }

            return new Kernel(size, size, radius, radius, values);
        }

        /// <summary>
        /// Disk kernel of given radius.
        /// </summary>
        public static Kernel Disk(int radius)
        {
            int size = 2 * radius + 1;
            double r2 = (radius + 0.5) * (radius + 0.5);
            double[] values = new double[size * size];
            for (int y = 0;y < size;y++)
            {
                for (int x = 0;x < size;x++)
                {
                    double dx = x - radius, dy = y - radius;
                    values[y * size + x] = (dx * dx + dy * dy <= r2) ? 1.0 : double.NaN;
                }
            }

            return new Kernel(size, size, radius, radius, values);
        }

        /// <summary>
        /// Cross-shaped kernel (horizontal + vertical lines).
        /// </summary>
        public static Kernel Cross(int radius)
        {
            int size = 2 * radius + 1;
            double[] values = new double[size * size];
            Array.Fill(values, double.NaN);
            for (int i = 0;i < size;i++)
            {
                values[radius * size + i] = 1.0; // horizontal
                values[i * size + radius] = 1.0; // vertical
            }
            return new Kernel(size, size, radius, radius, values);
        }

        /// <summary>
        /// Plus-shaped kernel (same as Cross).
        /// </summary>
        public static Kernel Plus(int radius) => Cross(radius);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Morphology Operations
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Erode: output pixel = minimum of kernel neighborhood. Shrinks bright regions, grows dark regions.
    /// </summary>
    public static ImageFrame Erode(ImageFrame image, Kernel kernel, int iterations = 1)
    {
        var result = CloneImage(image);
        for (int i = 0;i < iterations;i++)
        {
            var src = i == 0 ? image : result;
            var dst = ApplyMorphology(src, kernel, isErosion: true);
            if (i > 0)
            {
                result.Dispose();
            }

            result = dst;
        }
        return result;
    }

    /// <summary>
    /// Dilate: output pixel = maximum of kernel neighborhood. Grows bright regions, shrinks dark regions.
    /// </summary>
    public static ImageFrame Dilate(ImageFrame image, Kernel kernel, int iterations = 1)
    {
        var result = CloneImage(image);
        for (int i = 0;i < iterations;i++)
        {
            var src = i == 0 ? image : result;
            var dst = ApplyMorphology(src, kernel, isErosion: false);
            if (i > 0)
            {
                result.Dispose();
            }

            result = dst;
        }
        return result;
    }

    /// <summary>
    /// Open: erode then dilate. Removes small bright spots, smooths edges.
    /// </summary>
    public static ImageFrame Open(ImageFrame image, Kernel kernel, int iterations = 1)
    {
        using var eroded = Erode(image, kernel, iterations);
        return Dilate(eroded, kernel, iterations);
    }

    /// <summary>
    /// Close: dilate then erode. Fills small dark holes, smooths edges.
    /// </summary>
    public static ImageFrame Close(ImageFrame image, Kernel kernel, int iterations = 1)
    {
        using var dilated = Dilate(image, kernel, iterations);
        return Erode(dilated, kernel, iterations);
    }

    /// <summary>
    /// Morphological gradient: dilate - erode. Highlights edges.
    /// </summary>
    public static ImageFrame Gradient(ImageFrame image, Kernel kernel)
    {
        using var dilated = Dilate(image, kernel);
        using var eroded = Erode(image, kernel);
        return SubtractImages(dilated, eroded);
    }

    /// <summary>
    /// Top-hat (white hat): original - open. Extracts bright features smaller than kernel.
    /// </summary>
    public static ImageFrame TopHat(ImageFrame image, Kernel kernel)
    {
        using var opened = Open(image, kernel);
        return SubtractImages(image, opened);
    }

    /// <summary>
    /// Bottom-hat (black hat): close - original. Extracts dark features smaller than kernel.
    /// </summary>
    public static ImageFrame BottomHat(ImageFrame image, Kernel kernel)
    {
        using var closed = Close(image, kernel);
        return SubtractImages(closed, image);
    }

    /// <summary>
    /// Distance transform: computes the distance from each foreground pixel (nonzero) to the nearest
    /// background pixel (zero). Uses a two-pass chamfer algorithm. Result is normalized to full range.
    /// </summary>
    public static ImageFrame DistanceTransform(ImageFrame image, DistanceMetric metric = DistanceMetric.Euclidean)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        // Convert to binary: threshold at midpoint, average channels
        var dist = new double[height * width];
        double threshold = Quantum.MaxValue * 0.5;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                int off = x * channels;
                int colorChannels = image.HasAlpha ? channels - 1 : channels;
                for (int c = 0; c < colorChannels; c++)
                    sum += row[off + c];
                double avg = sum / colorChannels;

                // Background (dark) = 0, foreground (bright) = large distance to compute
                dist[y * width + x] = avg > threshold ? double.MaxValue / 2 : 0;
            }
        }

        // Chamfer distances
        double d1, d2;
        switch (metric)
        {
            case DistanceMetric.Manhattan:
                d1 = 1.0; d2 = 2.0; break;
            case DistanceMetric.Chebyshev:
                d1 = 1.0; d2 = 1.0; break;
            default: // Euclidean approximation
                d1 = 1.0; d2 = Math.Sqrt(2.0); break;
        }

        // Forward pass: top-left to bottom-right
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (dist[idx] == 0) continue;

                double minDist = dist[idx];
                if (y > 0)
                {
                    if (x > 0) minDist = Math.Min(minDist, dist[(y - 1) * width + x - 1] + d2);
                    minDist = Math.Min(minDist, dist[(y - 1) * width + x] + d1);
                    if (x < width - 1) minDist = Math.Min(minDist, dist[(y - 1) * width + x + 1] + d2);
                }
                if (x > 0) minDist = Math.Min(minDist, dist[y * width + x - 1] + d1);
                dist[idx] = minDist;
            }
        }

        // Backward pass: bottom-right to top-left
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int idx = y * width + x;
                if (dist[idx] == 0) continue;

                double minDist = dist[idx];
                if (y < height - 1)
                {
                    if (x > 0) minDist = Math.Min(minDist, dist[(y + 1) * width + x - 1] + d2);
                    minDist = Math.Min(minDist, dist[(y + 1) * width + x] + d1);
                    if (x < width - 1) minDist = Math.Min(minDist, dist[(y + 1) * width + x + 1] + d2);
                }
                if (x < width - 1) minDist = Math.Min(minDist, dist[y * width + x + 1] + d1);
                dist[idx] = minDist;
            }
        }

        // Find max distance for normalization
        double maxDist = 0;
        for (int i = 0; i < dist.Length; i++)
            if (dist[i] > maxDist) maxDist = dist[i];

        // Create output grayscale image
        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, image.Colorspace, image.HasAlpha);
        double scale = maxDist > 0 ? Quantum.MaxValue / maxDist : 0;

        Parallel.For(0, height, y =>
        {
            var outRow = result.GetPixelRowForWrite(y);
            int ch = result.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                ushort val = Quantum.Clamp((int)Math.Round(dist[y * width + x] * scale));
                int off = x * ch;
                outRow[off] = val;
                outRow[off + 1] = val;
                outRow[off + 2] = val;
                if (image.HasAlpha) outRow[off + 3] = Quantum.MaxValue;
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Core Apply
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame ApplyMorphology(ImageFrame source, Kernel kernel, bool isErosion)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int kw = kernel.Width, kh = kernel.Height;
        int ox = kernel.OriginX, oy = kernel.OriginY;

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var outRow = result.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    ushort extremeVal = isErosion ? ushort.MaxValue : ushort.MinValue;
                    for (int ky = 0;ky < kh;ky++)
                    {
                        int sy = y + ky - oy;
                        if (sy < 0 || sy >= height)
                        {
                            continue;
                        }

                        var srcRow = source.GetPixelRow(sy);
                        for (int kx = 0;kx < kw;kx++)
                        {
                            double kv = kernel[kx, ky];
                            if (double.IsNaN(kv))
                            {
                                continue;
                            }

                            int sx = x + kx - ox;
                            if (sx < 0 || sx >= width)
                            {
                                continue;
                            }

                            ushort pixVal = srcRow[sx * channels + c];
                            if (isErosion)
                            {
                                if (pixVal < extremeVal)
                                {
                                    extremeVal = pixVal;
                                }
                            }
                            else
                            {
                                if (pixVal > extremeVal)
                                {
                                    extremeVal = pixVal;
                                }
                            }
                        }
                    }
                    outRow[x * channels + c] = extremeVal;
                }
            }
        });
        return result;
    }

    private static ImageFrame SubtractImages(ImageFrame a, ImageFrame b)
    {
        int width = (int)a.Columns;
        int height = (int)a.Rows;
        int channels = a.NumberOfChannels;
        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, a.Colorspace, a.HasAlpha);

        for (int y = 0;y < height;y++)
        {
            var rowA = a.GetPixelRow(y);
            var rowB = b.GetPixelRow(y);
            var rowOut = result.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int idx = x * channels + c;
                    int diff = rowA[idx] - rowB[idx];
                    rowOut[idx] = Quantum.Clamp(Math.Max(diff, 0));
                }
            }
        }
        return result;
    }

    private static ImageFrame CloneImage(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var clone = new ImageFrame();
        clone.Initialize((uint)width, (uint)height, source.Colorspace, source.HasAlpha);
        for (int y = 0;y < height;y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }
        return clone;
    }
}
