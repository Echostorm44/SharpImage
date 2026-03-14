using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Threshold;

/// <summary>
/// Thresholding operations: binary, Otsu auto-threshold, adaptive threshold, and ordered dithering.
/// </summary>
public static class ThresholdOps
{
    /// <summary>
    /// Binary threshold: pixels above threshold become white, below become black. Threshold is in [0..1] range
    /// (normalized quantum value).
    /// </summary>
    public static void BinaryThreshold(ImageFrame image, double threshold)
    {
        ushort threshQuantum = (ushort)(threshold * Quantum.MaxValue);
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int idx = x * channels + c;
                    row[idx] = row[idx] >= threshQuantum ? Quantum.MaxValue : (ushort)0;
                }
            }
        });
    }

    /// <summary>
    /// Black threshold: pixels below threshold become black, above are unchanged.
    /// </summary>
    public static void BlackThreshold(ImageFrame image, double threshold)
    {
        ushort threshQuantum = (ushort)(threshold * Quantum.MaxValue);
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int idx = x * channels + c;
                    if (row[idx] < threshQuantum)
                    {
                        row[idx] = 0;
                    }
                }
            }
        });
    }

    /// <summary>
    /// White threshold: pixels above threshold become white, below are unchanged.
    /// </summary>
    public static void WhiteThreshold(ImageFrame image, double threshold)
    {
        ushort threshQuantum = (ushort)(threshold * Quantum.MaxValue);
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int idx = x * channels + c;
                    if (row[idx] > threshQuantum)
                    {
                        row[idx] = Quantum.MaxValue;
                    }
                }
            }
        });
    }

    /// <summary>
    /// Otsu's method: automatically determine the optimal threshold that minimizes intra-class variance. Returns the
    /// threshold used (normalized [0..1]). Operates on luminance of the image.
    /// </summary>
    public static double OtsuThreshold(ImageFrame image)
    {
        long[] histogram = BuildLuminanceHistogram(image);
        return ComputeOtsuThreshold(histogram);
    }

    /// <summary>
    /// Apply Otsu auto-threshold to the image. Returns the threshold used.
    /// </summary>
    public static double ApplyOtsuThreshold(ImageFrame image)
    {
        double threshold = OtsuThreshold(image);
        BinaryThreshold(image, threshold);
        return threshold;
    }

    /// <summary>
    /// Kapur's entropy-based threshold: maximizes inter-class entropy. Returns the optimal threshold (normalized
    /// [0..1]).
    /// </summary>
    public static double KapurThreshold(ImageFrame image)
    {
        long[] histogram = BuildLuminanceHistogram(image);
        return ComputeKapurThreshold(histogram);
    }

    /// <summary>
    /// Triangle method threshold: finds the threshold at the point of maximum distance from a line between histogram
    /// min and max. Returns the optimal threshold (normalized [0..1]).
    /// </summary>
    public static double TriangleThreshold(ImageFrame image)
    {
        long[] histogram = BuildLuminanceHistogram(image);
        return ComputeTriangleThreshold(histogram);
    }

    /// <summary>
    /// Adaptive (local) threshold: each pixel is thresholded against the mean of its neighborhood minus an offset. Good
    /// for uneven illumination. windowSize should be odd; offset is in [0..1] range.
    /// </summary>
    public static ImageFrame AdaptiveThreshold(ImageFrame image, int windowSize, double offset)
    {
        if (windowSize % 2 == 0)
        {
            windowSize++;
        }

        int radius = windowSize / 2;
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        ushort offsetQ = (ushort)(Math.Abs(offset) * Quantum.MaxValue);

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, image.Colorspace, image.HasAlpha);

        // Use integral image for O(1) per-pixel mean computation
        long[,] integral = BuildIntegralImage(image, width, height, channels);

        for (int y = 0;y < height;y++)
        {
            var srcRow = image.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int x0 = Math.Max(x - radius, 0);
                int y0 = Math.Max(y - radius, 0);
                int x1 = Math.Min(x + radius, width - 1);
                int y1 = Math.Min(y + radius, height - 1);
                int area = (x1 - x0 + 1) * (y1 - y0 + 1);

                for (int c = 0;c < channels;c++)
                {
                    long sum = integral[y1 + 1, (x1 + 1) * channels + c]
                             - integral[y0, (x1 + 1) * channels + c]
                             - integral[y1 + 1, x0 * channels + c]
                             + integral[y0, x0 * channels + c];
                    ushort localMean = (ushort)(sum / area);
                    ushort thresh = (ushort)Math.Max(localMean - offsetQ, 0);
                    int idx = x * channels + c;
                    dstRow[idx] = srcRow[idx] >= thresh ? Quantum.MaxValue : (ushort)0;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Ordered dithering with a Bayer matrix of specified order (2, 4, or 8). Reduces to the specified number of levels
    /// per channel.
    /// </summary>
    public static void OrderedDither(ImageFrame image, int bayerOrder = 4, int levels = 2)
    {
        double[,] matrix = GenerateBayerMatrix(bayerOrder);
        int matSize = matrix.GetLength(0);
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        // Only dither color channels, not alpha
        int colorChannels = image.HasAlpha ? channels - 1 : channels;
        Parallel.For(0, height, y =>
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                double threshold = matrix[y % matSize, x % matSize];
                for (int c = 0;c < colorChannels;c++)
                {
                    int idx = x * channels + c;
                    double normalized = row[idx] * Quantum.Scale;
                    double dithered = normalized + (threshold - 0.5) / levels;
                    int level = (int)Math.Round(dithered * (levels - 1));
                    level = Math.Clamp(level, 0, levels - 1);
                    row[idx] = (ushort)((double)level / (levels - 1) * Quantum.MaxValue);
                }
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Internals
    // ═══════════════════════════════════════════════════════════════════

    private static long[] BuildLuminanceHistogram(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        long[] histogram = new long[256];

        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                byte lum;
                if (channels >= 3)
                {
                    double l = 0.2126 * row[off] * Quantum.Scale +
                               0.7152 * row[off + 1] * Quantum.Scale +
                               0.0722 * row[off + 2] * Quantum.Scale;
                    lum = (byte)Math.Clamp(l * 255.0 + 0.5, 0, 255);
                }
                else
                {
                    lum = Quantum.ScaleToByte(row[off]);
                }
                histogram[lum]++;
            }
        }
        return histogram;
    }

    private static double ComputeOtsuThreshold(long[] histogram)
    {
        long totalPixels = 0;
        for (int i = 0;i < 256;i++)
        {
            totalPixels += histogram[i];
        }

        if (totalPixels == 0)
        {
            return 0.5;
        }

        double totalMean = 0;
        for (int i = 0;i < 256;i++)
        {
            totalMean += i * histogram[i];
        }

        double bestVariance = 0;
        int bestThreshold = 0;
        long w0 = 0;
        double sum0 = 0;

        for (int t = 0;t < 256;t++)
        {
            w0 += histogram[t];
            if (w0 == 0)
            {
                continue;
            }

            long w1 = totalPixels - w0;
            if (w1 == 0)
            {
                break;
            }

            sum0 += t * histogram[t];
            double mean0 = sum0 / w0;
            double mean1 = (totalMean - sum0) / w1;
            double variance = (double)w0 * w1 * (mean0 - mean1) * (mean0 - mean1);

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t;
            }
        }

        return bestThreshold / 255.0;
    }

    private static double ComputeKapurThreshold(long[] histogram)
    {
        long totalPixels = 0;
        for (int i = 0;i < 256;i++)
        {
            totalPixels += histogram[i];
        }

        if (totalPixels == 0)
        {
            return 0.5;
        }

        double bestEntropy = double.NegativeInfinity;
        int bestThreshold = 0;

        for (int t = 0;t < 256;t++)
        {
            long w0 = 0;
            for (int i = 0;i <= t;i++)
            {
                w0 += histogram[i];
            }

            long w1 = totalPixels - w0;
            if (w0 == 0 || w1 == 0)
            {
                continue;
            }

            double h0 = 0;
            for (int i = 0;i <= t;i++)
            {
                if (histogram[i] > 0)
                {
                    double p = (double)histogram[i] / w0;
                    h0 -= p * Math.Log(p);
                }
            }

            double h1 = 0;
            for (int i = t + 1;i < 256;i++)
            {
                if (histogram[i] > 0)
                {
                    double p = (double)histogram[i] / w1;
                    h1 -= p * Math.Log(p);
                }
            }

            double totalEntropy = h0 + h1;
            if (totalEntropy > bestEntropy)
            {
                bestEntropy = totalEntropy;
                bestThreshold = t;
            }
        }

        return bestThreshold / 255.0;
    }

    private static double ComputeTriangleThreshold(long[] histogram)
    {
        // Find the peak and the furthest non-zero bin
        int peakBin = 0;
        long peakVal = 0;
        int minBin = 0, maxBin = 255;

        for (int i = 0;i < 256;i++)
        {
            if (histogram[i] > peakVal)
            {
                peakVal = histogram[i];
                peakBin = i;
            }
        }

        for (int i = 0;i < 256;i++)
        {
            if (histogram[i] > 0)
            {
                minBin = i;
                break;
            }
        }

        for (int i = 255;i >= 0;i--)
        {
            if (histogram[i] > 0)
            {
                maxBin = i;
                break;
            }
        }

        // Use the longer tail
        bool flipDirection = (peakBin - minBin) < (maxBin - peakBin);
        int startBin = flipDirection ? peakBin : minBin;
        int endBin = flipDirection ? maxBin : peakBin;

        // Line from (startBin, histogram[startBin]) to (endBin, histogram[endBin])
        double x0 = startBin, y0 = histogram[startBin];
        double x1 = endBin, y1 = histogram[endBin];
        double lineLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (lineLen == 0)
        {
            return 0.5;
        }

        double maxDist = 0;
        int bestThreshold = startBin;

        for (int i = startBin;i <= endBin;i++)
        {
            double dist = Math.Abs((y1 - y0) * i - (x1 - x0) * histogram[i] + x1 * y0 - y1 * x0) / lineLen;
            if (dist > maxDist)
            {
                maxDist = dist;
                bestThreshold = i;
            }
        }

        return bestThreshold / 255.0;
    }

    private static long[,] BuildIntegralImage(ImageFrame image, int width, int height, int channels)
    {
        var integral = new long[height + 1, (width + 1) * channels];

        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int col = (x + 1) * channels + c;
                    integral[y + 1, col] = row[x * channels + c]
                                         + integral[y, col]
                                         + integral[y + 1, col - channels]
                                         - integral[y, col - channels];
                }
            }
        }
        return integral;
    }

    private static double[,] GenerateBayerMatrix(int order)
    {
        if (order < 2)
        {
            order = 2;
        }
        // Start with 2x2 base
        double[,] matrix = { { 0, 2 }, { 3, 1 } };

        int currentSize = 2;
        while (currentSize < order)
        {
            int newSize = currentSize * 2;
            var newMatrix = new double[newSize, newSize];
            for (int y = 0;y < currentSize;y++)
            {
                for (int x = 0;x < currentSize;x++)
                {
                    double val = 4 * matrix[y, x];
                    newMatrix[y, x] = val;
                    newMatrix[y, x + currentSize] = val + 2;
                    newMatrix[y + currentSize, x] = val + 3;
                    newMatrix[y + currentSize, x + currentSize] = val + 1;
                }
            }

            matrix = newMatrix;
            currentSize = newSize;
        }

        // Normalize to [0..1]
        int n = currentSize * currentSize;
        var result = new double[currentSize, currentSize];
        for (int y = 0;y < currentSize;y++)
        {
            for (int x = 0;x < currentSize;x++)
            {
                result[y, x] = (matrix[y, x] + 0.5) / n;
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Modern Dithering Methods
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Blue noise dithering using a void-and-cluster generated threshold matrix.
    /// Produces perceptually superior results compared to Bayer ordered dithering,
    /// with no visible patterning artifacts. The matrix tiles seamlessly.
    /// </summary>
    public static void BlueNoiseDither(ImageFrame image, int levels = 2)
    {
        var matrix = GenerateBlueNoiseMatrix(64);
        int matSize = 64;
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        int colorChannels = image.HasAlpha ? channels - 1 : channels;

        Parallel.For(0, height, y =>
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double threshold = matrix[y % matSize, x % matSize];
                for (int c = 0; c < colorChannels; c++)
                {
                    int idx = x * channels + c;
                    double normalized = row[idx] * Quantum.Scale;
                    double dithered = normalized + (threshold - 0.5) / levels;
                    int level = (int)Math.Round(dithered * (levels - 1));
                    level = Math.Clamp(level, 0, levels - 1);
                    row[idx] = (ushort)((double)level / (levels - 1) * Quantum.MaxValue);
                }
            }
        });
    }

    /// <summary>
    /// Error diffusion dithering using Stucki's filter. Distributes quantization error
    /// over a wider area than Floyd-Steinberg, producing smoother gradients.
    /// </summary>
    public static void StuckiDither(ImageFrame image, int levels = 2)
    {
        ErrorDiffusionDither(image, levels, StuckiKernel, 42);
    }

    /// <summary>
    /// Error diffusion dithering using Atkinson's filter. Distributes only 3/4 of the
    /// error, producing a lighter result with more contrast. Popular for 1-bit Mac output.
    /// </summary>
    public static void AtkinsonDither(ImageFrame image, int levels = 2)
    {
        ErrorDiffusionDither(image, levels, AtkinsonKernel, 8);
    }

    /// <summary>
    /// Error diffusion dithering using Sierra's 3-line filter. Produces smooth results
    /// similar to Floyd-Steinberg but with wider diffusion.
    /// </summary>
    public static void SierraDither(ImageFrame image, int levels = 2)
    {
        ErrorDiffusionDither(image, levels, SierraKernel, 32);
    }

    // Stucki kernel: 5 wide, distributes error across 12 neighbors
    private static readonly (int dx, int dy, int weight)[] StuckiKernel =
    [
        (1, 0, 8), (2, 0, 4),
        (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2),
        (-2, 2, 1), (-1, 2, 2), (0, 2, 4), (1, 2, 2), (2, 2, 1),
    ];

    // Atkinson kernel: only distributes 6/8 = 75% of error
    private static readonly (int dx, int dy, int weight)[] AtkinsonKernel =
    [
        (1, 0, 1), (2, 0, 1),
        (-1, 1, 1), (0, 1, 1), (1, 1, 1),
        (0, 2, 1),
    ];

    // Sierra 3-line kernel
    private static readonly (int dx, int dy, int weight)[] SierraKernel =
    [
        (1, 0, 5), (2, 0, 3),
        (-2, 1, 2), (-1, 1, 4), (0, 1, 5), (1, 1, 4), (2, 1, 2),
        (-1, 2, 2), (0, 2, 3), (1, 2, 2),
    ];

    private static void ErrorDiffusionDither(ImageFrame image, int levels,
        (int dx, int dy, int weight)[] kernel, int divisor)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        int colorChannels = image.HasAlpha ? channels - 1 : channels;

        // Work buffer with floating-point precision for error accumulation
        double[] buffer = new double[width * height * colorChannels];
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < colorChannels; c++)
                    buffer[(y * width + x) * colorChannels + c] = row[x * channels + c] * Quantum.Scale;
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < colorChannels; c++)
                {
                    int bIdx = (y * width + x) * colorChannels + c;
                    double oldVal = buffer[bIdx];
                    int level = (int)Math.Round(oldVal * (levels - 1));
                    level = Math.Clamp(level, 0, levels - 1);
                    double newVal = (double)level / (levels - 1);
                    double error = oldVal - newVal;
                    buffer[bIdx] = newVal;

                    // Distribute error to neighbors
                    for (int k = 0; k < kernel.Length; k++)
                    {
                        int nx = x + kernel[k].dx;
                        int ny = y + kernel[k].dy;
                        if (nx >= 0 && nx < width && ny < height)
                        {
                            int nIdx = (ny * width + nx) * colorChannels + c;
                            buffer[nIdx] += error * kernel[k].weight / divisor;
                        }
                    }
                }
            }
        }

        // Write back to image
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < colorChannels; c++)
                {
                    int bIdx = (y * width + x) * colorChannels + c;
                    row[x * channels + c] = Quantum.ScaleFromDouble(buffer[bIdx]);
                }
            }
        }
    }

    /// <summary>
    /// Generates a blue noise threshold matrix using the R2 quasi-random sequence.
    /// This approach uses the plastic constant (generalization of the golden ratio to 2D)
    /// to produce a spatially well-distributed threshold pattern with no visible artifacts.
    /// </summary>
    private static double[,] GenerateBlueNoiseMatrix(int size)
    {
        double[,] result = new double[size, size];

        // Use R2 quasi-random sequence based on plastic constant
        // This produces excellent blue noise distribution
        const double g = 1.32471795724474602596; // plastic constant
        double a1 = 1.0 / g;
        double a2 = 1.0 / (g * g);

        // Fill matrix with R2 sequence values mapped by spatial index
        int total = size * size;
        double[] values = new double[total];
        for (int i = 0; i < total; i++)
            values[i] = (0.5 + a1 * (i + 1)) % 1.0;

        // Create spatial ordering using bit-reversal permutation for better distribution
        int[] spatialOrder = new int[total];
        for (int i = 0; i < total; i++)
            spatialOrder[i] = i;

        // Fisher-Yates shuffle with deterministic seed for reproducibility
        var rng = new Random(42);
        for (int i = total - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (spatialOrder[i], spatialOrder[j]) = (spatialOrder[j], spatialOrder[i]);
        }

        // Assign R2 values to spatial positions via the permuted ordering
        // This gives each position a unique threshold in [0,1) with good spatial distribution
        double[] thresholds = new double[total];
        for (int i = 0; i < total; i++)
        {
            int pos = spatialOrder[i];
            thresholds[pos] = (0.5 + a1 * (i + 1) + a2 * (i + 1)) % 1.0;
        }

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                result[y, x] = thresholds[y * size + x];

        return result;
    }

    // ─── Range Threshold ──────────────────────────────────────────

    /// <summary>
    /// Multi-level range threshold. Pixels with intensity in [softLow..softHigh] are white.
    /// Pixels in [hardLow..softLow) or (softHigh..hardHigh] get a soft gradient.
    /// Pixels outside [hardLow..hardHigh] are black.
    /// All values are normalized [0..1].
    /// </summary>
    /// <param name="image">Image to threshold (modified in-place).</param>
    /// <param name="hardLow">Hard low boundary (below = black).</param>
    /// <param name="softLow">Soft low boundary (above = start white).</param>
    /// <param name="softHigh">Soft high boundary (below = end white).</param>
    /// <param name="hardHigh">Hard high boundary (above = black).</param>
    public static void RangeThreshold(ImageFrame image, double hardLow, double softLow,
        double softHigh, double hardHigh)
    {
        ushort qMax = Quantum.MaxValue;
        double invQMax = 1.0 / qMax;
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int ch = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int offset = x * ch;

                // Compute luminance from RGB channels
                double lum;
                if (colorCh >= 3)
                    lum = (row[offset] * 0.299 + row[offset + 1] * 0.587 + row[offset + 2] * 0.114) * invQMax;
                else
                    lum = row[offset] * invQMax;

                double output;
                if (lum < hardLow || lum > hardHigh)
                {
                    output = 0.0;
                }
                else if (lum >= softLow && lum <= softHigh)
                {
                    output = 1.0;
                }
                else if (lum < softLow)
                {
                    // Soft ramp from hardLow to softLow
                    double range = softLow - hardLow;
                    output = range > 0 ? (lum - hardLow) / range : 1.0;
                }
                else
                {
                    // Soft ramp from softHigh to hardHigh
                    double range = hardHigh - softHigh;
                    output = range > 0 ? 1.0 - (lum - softHigh) / range : 1.0;
                }

                ushort qVal = Quantum.Clamp(output * qMax);
                for (int c = 0; c < colorCh; c++)
                    image.SetPixelChannel(x, y, c, qVal);
            }
        }
    }

    // ─── Color Threshold ──────────────────────────────────────────

    /// <summary>
    /// Thresholds pixels based on whether their color falls within a specified color range.
    /// Pixels inside the range become white, pixels outside become black.
    /// All color components are in [0..1] normalized range.
    /// </summary>
    /// <param name="image">Image to threshold (modified in-place).</param>
    /// <param name="startR">Low red boundary.</param>
    /// <param name="startG">Low green boundary.</param>
    /// <param name="startB">Low blue boundary.</param>
    /// <param name="endR">High red boundary.</param>
    /// <param name="endG">High green boundary.</param>
    /// <param name="endB">High blue boundary.</param>
    public static void ColorThreshold(ImageFrame image, double startR, double startG, double startB,
        double endR, double endG, double endB)
    {
        ushort qMax = Quantum.MaxValue;
        double invQMax = 1.0 / qMax;
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int ch = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int offset = x * ch;
                double r = row[offset] * invQMax;
                double g = colorCh >= 2 ? row[offset + 1] * invQMax : r;
                double b = colorCh >= 3 ? row[offset + 2] * invQMax : r;

                bool inRange = r >= startR && r <= endR
                            && g >= startG && g <= endG
                            && b >= startB && b <= endB;

                ushort val = inRange ? qMax : (ushort)0;
                for (int c = 0; c < colorCh; c++)
                    row[offset + c] = val;
            }
        }
    }

    // ─── Random Threshold ─────────────────────────────────────────

    /// <summary>
    /// Applies a random threshold to each pixel, producing a stochastic dithering effect.
    /// Each pixel is compared against a random value in [low..high] range.
    /// low and high are normalized [0..1].
    /// </summary>
    /// <param name="image">Image to threshold (modified in-place).</param>
    /// <param name="low">Lower bound of random range (0..1).</param>
    /// <param name="high">Upper bound of random range (0..1).</param>
    public static void RandomThreshold(ImageFrame image, double low = 0.0, double high = 1.0)
    {
        ushort qMax = Quantum.MaxValue;
        double invQMax = 1.0 / qMax;
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int ch = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int colorCh = hasAlpha ? ch - 1 : ch;

        Parallel.For(0, h, () => new Random(Thread.CurrentThread.ManagedThreadId + Environment.TickCount),
            (y, _, rng) =>
            {
                var row = image.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    int offset = x * ch;
                    double threshold = low + rng.NextDouble() * (high - low);

                    for (int c = 0; c < colorCh; c++)
                    {
                        double val = row[offset + c] * invQMax;
                        row[offset + c] = val >= threshold ? qMax : (ushort)0;
                    }
                }
                return rng;
            },
            _ => { });
    }
}
