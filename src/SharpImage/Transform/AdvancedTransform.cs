// SharpImage — Advanced transform operations.
// Liquify (forward warp), frequency separation, focus stacking.
// Bundle J of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Transform;

/// <summary>
/// Advanced transform operations: liquify (forward warp mesh deformation),
/// frequency separation (high/low frequency decomposition), and focus stacking.
/// </summary>
public static class AdvancedTransform
{
    // ─── Liquify / Forward Warp ────────────────────────────────────

    /// <summary>
    /// Applies a forward warp (liquify) at a given center point.
    /// Pushes pixels outward from the center in the specified direction.
    /// radius is the affected area size, strength controls displacement magnitude.
    /// dx,dy specify the warp direction in pixels.
    /// </summary>
    public static ImageFrame Liquify(ImageFrame source, int centerX, int centerY,
        int radius, double strength, double dx, double dy)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        double radiusSq = radius * radius;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // Copy source to result first
        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }

        // For each pixel in the affected region, compute where it came from (inverse warp)
        int yMin = Math.Max(0, centerY - radius);
        int yMax = Math.Min(height - 1, centerY + radius);
        int xMin = Math.Max(0, centerX - radius);
        int xMax = Math.Min(width - 1, centerX + radius);

        for (int y = yMin; y <= yMax; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = xMin; x <= xMax; x++)
            {
                double distX = x - centerX;
                double distY = y - centerY;
                double distSq = distX * distX + distY * distY;

                if (distSq >= radiusSq) continue;

                // Gaussian falloff weight
                double weight = Math.Exp(-distSq / (2.0 * (radius / 3.0) * (radius / 3.0))) * strength;

                // Source pixel (inverse warp: subtract displacement)
                double srcX = x - dx * weight;
                double srcY = y - dy * weight;

                // Bilinear sample from source
                BilinearSample(source, srcX, srcY, dstRow, x * channels, channels);
            }
        }

        return result;
    }

    /// <summary>
    /// Applies a series of liquify pushes defined by an array of (centerX, centerY, dx, dy) tuples.
    /// Each push is applied sequentially to the result of the previous push.
    /// </summary>
    public static ImageFrame LiquifyMulti(ImageFrame source,
        (int centerX, int centerY, double dx, double dy)[] pushes,
        int radius, double strength)
    {
        var current = source;
        bool ownsFrame = false;

        foreach (var (cx, cy, pushDx, pushDy) in pushes)
        {
            var next = Liquify(current, cx, cy, radius, strength, pushDx, pushDy);
            if (ownsFrame) current.Dispose();
            current = next;
            ownsFrame = true;
        }

        return current;
    }

    // ─── Frequency Separation ──────────────────────────────────────

    /// <summary>
    /// Decomposes an image into low-frequency (color/tone) and high-frequency (texture/detail) layers.
    /// Low = Gaussian blur of source. High = source - low (stored as offset from midgray).
    /// blurRadius controls the separation frequency; larger values put more into the low layer.
    /// Returns (lowFrequency, highFrequency) tuple.
    /// </summary>
    public static (ImageFrame lowFrequency, ImageFrame highFrequency) FrequencySeparation(
        ImageFrame source, double blurRadius = 5.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Compute Gaussian blur for low frequency
        var lowFreq = SharpImage.Effects.ConvolutionFilters.GaussianBlur(source, blurRadius);

        // High frequency = source - low + midgray (offset to avoid negative values)
        var highFreq = new ImageFrame();
        highFreq.Initialize(width, height, source.Colorspace, source.HasAlpha);

        int midgray = Quantum.MaxValue / 2;

        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var lowRow = lowFreq.GetPixelRow(y);
            var highRow = highFreq.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    if (source.HasAlpha && c == channels - 1)
                    {
                        highRow[off + c] = srcRow[off + c]; // preserve alpha
                    }
                    else
                    {
                        int diff = srcRow[off + c] - lowRow[off + c] + midgray;
                        highRow[off + c] = Quantum.Clamp(diff);
                    }
                }
            }
        }

        return (lowFreq, highFreq);
    }

    /// <summary>
    /// Recombines low and high frequency layers back into a single image.
    /// Inverse of FrequencySeparation: result = low + (high - midgray).
    /// </summary>
    public static ImageFrame FrequencyRecombine(ImageFrame lowFrequency, ImageFrame highFrequency)
    {
        int width = (int)lowFrequency.Columns;
        int height = (int)lowFrequency.Rows;
        int channels = lowFrequency.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, lowFrequency.Colorspace, lowFrequency.HasAlpha);

        int midgray = Quantum.MaxValue / 2;

        for (int y = 0; y < height; y++)
        {
            var lowRow = lowFrequency.GetPixelRow(y);
            var highRow = highFrequency.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    if (lowFrequency.HasAlpha && c == channels - 1)
                    {
                        dstRow[off + c] = lowRow[off + c];
                    }
                    else
                    {
                        int val = lowRow[off + c] + highRow[off + c] - midgray;
                        dstRow[off + c] = Quantum.Clamp(val);
                    }
                }
            }
        }

        return result;
    }

    // ─── Focus Stacking ────────────────────────────────────────────

    /// <summary>
    /// Combines multiple images taken at different focal distances into a single all-sharp image.
    /// Uses Laplacian magnitude as the focus measure — for each pixel, the sharpest image wins.
    /// The images must be pre-aligned and the same size.
    /// </summary>
    public static ImageFrame FocusStack(ImageFrame[] images)
    {
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required.");
        if (images.Length == 1)
            return CloneFrame(images[0]);

        int width = (int)images[0].Columns;
        int height = (int)images[0].Rows;
        int channels = images[0].NumberOfChannels;

        // Compute focus measure (Laplacian magnitude) for each image
        var focusMeasures = new double[images.Length][];
        for (int i = 0; i < images.Length; i++)
            focusMeasures[i] = ComputeFocusMeasure(images[i]);

        // For each pixel, pick the image with the highest focus measure
        var bestSource = ArrayPool<int>.Shared.Rent(height * width);
        int[] smoothedSource;
        try
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    double bestFocus = -1;
                    int bestImg = 0;

                    for (int i = 0; i < images.Length; i++)
                    {
                        if (focusMeasures[i][idx] > bestFocus)
                        {
                            bestFocus = focusMeasures[i][idx];
                            bestImg = i;
                        }
                    }
                    bestSource[idx] = bestImg;
                }
            }

            // Optional: smooth the source selection map to avoid pixel-level switching artifacts
            // Use a simple 5x5 majority filter
            smoothedSource = MajorityFilter(bestSource, width, height, images.Length, 2);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bestSource);
        }

        // Compose final image
        var result = new ImageFrame();
        result.Initialize(width, height, images[0].Colorspace, images[0].HasAlpha);

        for (int y = 0; y < height; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int srcImg = smoothedSource[y * width + x];
                var srcRow = images[srcImg].GetPixelRow(y);
                int off = x * channels;
                for (int c = 0; c < channels; c++)
                    dstRow[off + c] = srcRow[off + c];
            }
        }

        return result;
    }

    // ─── Helpers ────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BilinearSample(ImageFrame source, double sx, double sy,
        Span<ushort> dst, int dstOffset, int channels)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;

        int x0 = (int)Math.Floor(sx);
        int y0 = (int)Math.Floor(sy);
        double fx = sx - x0;
        double fy = sy - y0;

        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        x0 = Math.Clamp(x0, 0, width - 1);
        y0 = Math.Clamp(y0, 0, height - 1);

        var row0 = source.GetPixelRow(y0);
        var row1 = source.GetPixelRow(y1);

        for (int c = 0; c < channels; c++)
        {
            double v00 = row0[x0 * channels + c];
            double v10 = row0[x1 * channels + c];
            double v01 = row1[x0 * channels + c];
            double v11 = row1[x1 * channels + c];

            double val = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy)
                       + v01 * (1 - fx) * fy + v11 * fx * fy;
            dst[dstOffset + c] = Quantum.Clamp((int)val);
        }
    }

    private static double[] ComputeFocusMeasure(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        var measure = new double[height * width];

        for (int y = 1; y < height - 1; y++)
        {
            var rowPrev = image.GetPixelRow(y - 1);
            var rowCurr = image.GetPixelRow(y);
            var rowNext = image.GetPixelRow(y + 1);

            for (int x = 1; x < width - 1; x++)
            {
                // Compute Laplacian magnitude across color channels
                double laplacian = 0;
                int colorChannels = channels >= 3 ? 3 : 1;
                for (int c = 0; c < colorChannels; c++)
                {
                    int off = x * channels + c;
                    int offL = (x - 1) * channels + c;
                    int offR = (x + 1) * channels + c;

                    double lap = 4.0 * rowCurr[off]
                               - rowCurr[offL] - rowCurr[offR]
                               - rowPrev[off] - rowNext[off];
                    laplacian += Math.Abs(lap);
                }
                measure[y * width + x] = laplacian;
            }
        }

        return measure;
    }

    private static int[] MajorityFilter(int[] source, int width, int height, int numImages, int radius)
    {
        var result = new int[height * width];
        var counts = new int[numImages];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Array.Clear(counts, 0, numImages);

                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(height - 1, y + radius);
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(width - 1, x + radius);

                for (int ny = y0; ny <= y1; ny++)
                    for (int nx = x0; nx <= x1; nx++)
                        counts[source[ny * width + nx]]++;

                int best = 0;
                for (int i = 1; i < numImages; i++)
                    if (counts[i] > counts[best])
                        best = i;

                result[y * width + x] = best;
            }
        }

        return result;
    }

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var clone = new ImageFrame();
        clone.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }
        return clone;
    }
}
