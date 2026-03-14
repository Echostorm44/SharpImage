// SharpImage — Smart Crop (Entropy-Based)
// Automatically finds the most "interesting" region of an image using entropy
// and edge density analysis, then crops to it.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Transform;

/// <summary>
/// Entropy-based smart cropping. Finds and crops to the most visually interesting
/// region of an image by combining local entropy and edge density scores.
/// </summary>
public static class SmartCrop
{
    /// <summary>
    /// Automatically crop to the most interesting region of the given size.
    /// Uses a sliding window approach scored by combined entropy + edge density.
    /// </summary>
    public static ImageFrame Apply(ImageFrame source, int targetWidth, int targetHeight)
    {
        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;

        if (targetWidth >= srcW && targetHeight >= srcH)
            return Geometry.Crop(source, 0, 0, srcW, srcH);

        targetWidth = Math.Min(targetWidth, srcW);
        targetHeight = Math.Min(targetHeight, srcH);

        var grayscale = ArrayPool<byte>.Shared.Rent(srcW * srcH);
        var edgeMag = ArrayPool<float>.Shared.Rent(srcW * srcH);

        try
        {
            BuildGrayscaleAndEdge(source, grayscale, edgeMag, srcW, srcH);

            // Detect background and find foreground bounding box
            var bgColor = DetectBackgroundColor(source, srcW, srcH);
            var (fgLeft, fgTop, fgRight, fgBottom) = FindForegroundBounds(
                source, srcW, srcH, bgColor, threshold: 0.15);

            int fgW = fgRight - fgLeft;
            int fgH = fgBottom - fgTop;
            int fgCenterX = (fgLeft + fgRight) / 2;

            // Build interest map for scoring within the foreground region
            var saliencyMap = BuildSaliencyMap(source, srcW, srcH);
            var entropyMap = BuildLocalEntropyMap(grayscale, srcW, srcH, blockSize: 16);
            var interestMap = ArrayPool<float>.Shared.Rent(srcW * srcH);
            var integralInterest = Array.Empty<double>();
            try
            {
                for (int i = 0; i < srcW * srcH; i++)
                    interestMap[i] = saliencyMap[i] * 2f + entropyMap[i] + MathF.Log(1f + edgeMag[i]) * 0.1f;

                integralInterest = BuildIntegralImage(interestMap, srcW, srcH);

            // Start position: horizontally center on foreground, vertically at upper third
            int startX, startY;
            if (fgW <= targetWidth)
                startX = Math.Clamp(fgCenterX - targetWidth / 2, 0, srcW - targetWidth);
            else
                startX = Math.Clamp(fgLeft, 0, srcW - targetWidth);

            if (fgH <= targetHeight)
                startY = Math.Clamp(fgTop, 0, srcH - targetHeight);
            else
                startY = Math.Clamp(fgTop + (int)(fgH * 0.1), 0, srcH - targetHeight);

            // Search the full foreground area for the best crop position
            int searchX0 = Math.Max(0, fgLeft - targetWidth / 4);
            int searchX1 = Math.Min(srcW - targetWidth, fgRight - targetWidth / 2);
            int searchY0 = Math.Max(0, fgTop - targetHeight / 4);
            int searchY1 = Math.Min(srcH - targetHeight, fgBottom - targetHeight / 4);

            int bestX = startX, bestY = startY;
            double bestScore = double.MinValue;
            int step = Math.Max(2, Math.Min(fgW, fgH) / 50);

            for (int y = searchY0; y <= searchY1; y += step)
            {
                for (int x = searchX0; x <= searchX1; x += step)
                {
                    double interest = QueryIntegral(integralInterest, x, y,
                        x + targetWidth - 1, y + targetHeight - 1, srcW);

                    // Bias toward upper portion of foreground (heads, faces, main content)
                    double verticalProgress = (double)(y - fgTop) / Math.Max(1, fgH);
                    double upperBias = 1.0 - 0.6 * verticalProgress;

                    double score = interest * upperBias;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            // Fine-grained refinement around best position
            int rx0 = Math.Max(0, bestX - step * 2);
            int rx1 = Math.Min(srcW - targetWidth, bestX + step * 2);
            int ry0 = Math.Max(0, bestY - step * 2);
            int ry1 = Math.Min(srcH - targetHeight, bestY + step * 2);

            for (int y = ry0; y <= ry1; y++)
            {
                for (int x = rx0; x <= rx1; x++)
                {
                    double interest = QueryIntegral(integralInterest, x, y,
                        x + targetWidth - 1, y + targetHeight - 1, srcW);
                    double verticalProgress = (double)(y - fgTop) / Math.Max(1, fgH);
                    double upperBias = 1.0 - 0.6 * verticalProgress;
                    double score = interest * upperBias;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            return Geometry.Crop(source, bestX, bestY, targetWidth, targetHeight);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(saliencyMap);
                ArrayPool<float>.Shared.Return(entropyMap);
                ArrayPool<float>.Shared.Return(interestMap);
                if (integralInterest.Length > 0)
                    ArrayPool<double>.Shared.Return(integralInterest);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grayscale);
            ArrayPool<float>.Shared.Return(edgeMag);
        }
    }

    /// <summary>
    /// Returns the crop rectangle (x, y, width, height) without performing the crop.
    /// Useful for previewing or adjusting the crop region.
    /// </summary>
    public static (int X, int Y, int Width, int Height) FindBestRegion(
        ImageFrame source, int targetWidth, int targetHeight)
    {
        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;

        targetWidth = Math.Min(targetWidth, srcW);
        targetHeight = Math.Min(targetHeight, srcH);

        var grayscale = ArrayPool<byte>.Shared.Rent(srcW * srcH);
        var edgeMag = ArrayPool<float>.Shared.Rent(srcW * srcH);

        try
        {
            BuildGrayscaleAndEdge(source, grayscale, edgeMag, srcW, srcH);

            var bgColor = DetectBackgroundColor(source, srcW, srcH);
            var (fgLeft, fgTop, fgRight, fgBottom) = FindForegroundBounds(
                source, srcW, srcH, bgColor, threshold: 0.15);

            int fgW = fgRight - fgLeft;
            int fgH = fgBottom - fgTop;
            int fgCenterX = (fgLeft + fgRight) / 2;

            var saliencyMap = BuildSaliencyMap(source, srcW, srcH);
            var entropyMap = BuildLocalEntropyMap(grayscale, srcW, srcH, blockSize: 16);
            var interestMap = ArrayPool<float>.Shared.Rent(srcW * srcH);
            var integralInterest = Array.Empty<double>();
            try
            {
                for (int i = 0; i < srcW * srcH; i++)
                    interestMap[i] = saliencyMap[i] * 2f + entropyMap[i] + MathF.Log(1f + edgeMag[i]) * 0.1f;

                integralInterest = BuildIntegralImage(interestMap, srcW, srcH);

                int startX = fgW <= targetWidth
                    ? Math.Clamp(fgCenterX - targetWidth / 2, 0, srcW - targetWidth)
                    : Math.Clamp(fgLeft, 0, srcW - targetWidth);

                int startY = fgH <= targetHeight
                    ? Math.Clamp(fgTop, 0, srcH - targetHeight)
                    : Math.Clamp(fgTop + (int)(fgH * 0.1), 0, srcH - targetHeight);

                int searchX0 = Math.Max(0, fgLeft - targetWidth / 4);
                int searchX1 = Math.Min(srcW - targetWidth, fgRight - targetWidth / 2);
                int searchY0 = Math.Max(0, fgTop - targetHeight / 4);
                int searchY1 = Math.Min(srcH - targetHeight, fgBottom - targetHeight / 4);

                int bestX = startX, bestY = startY;
                double bestScore = double.MinValue;
                int step = Math.Max(2, Math.Min(fgW, fgH) / 50);

                for (int y = searchY0; y <= searchY1; y += step)
                {
                    for (int x = searchX0; x <= searchX1; x += step)
                    {
                        double interest = QueryIntegral(integralInterest, x, y,
                            x + targetWidth - 1, y + targetHeight - 1, srcW);
                        double verticalProgress = (double)(y - fgTop) / Math.Max(1, fgH);
                        double upperBias = 1.0 - 0.6 * verticalProgress;
                        double score = interest * upperBias;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                int rx0 = Math.Max(0, bestX - step * 2);
                int rx1 = Math.Min(srcW - targetWidth, bestX + step * 2);
                int ry0 = Math.Max(0, bestY - step * 2);
                int ry1 = Math.Min(srcH - targetHeight, bestY + step * 2);

                for (int y = ry0; y <= ry1; y++)
                {
                    for (int x = rx0; x <= rx1; x++)
                    {
                        double interest = QueryIntegral(integralInterest, x, y,
                            x + targetWidth - 1, y + targetHeight - 1, srcW);
                        double verticalProgress = (double)(y - fgTop) / Math.Max(1, fgH);
                        double upperBias = 1.0 - 0.6 * verticalProgress;
                        double score = interest * upperBias;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                return (bestX, bestY, targetWidth, targetHeight);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(saliencyMap);
                ArrayPool<float>.Shared.Return(entropyMap);
                ArrayPool<float>.Shared.Return(interestMap);
                if (integralInterest.Length > 0)
                    ArrayPool<double>.Shared.Return(integralInterest);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grayscale);
            ArrayPool<float>.Shared.Return(edgeMag);
        }
    }

    /// <summary>
    /// Generates a heatmap showing the interestingness score across the image.
    /// Bright regions indicate areas that Smart Crop considers most important.
    /// </summary>
    public static ImageFrame GetInterestMap(ImageFrame source)
    {
        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;

        var grayscale = ArrayPool<byte>.Shared.Rent(srcW * srcH);
        var edgeMag = ArrayPool<float>.Shared.Rent(srcW * srcH);

        try
        {
            BuildGrayscaleAndEdge(source, grayscale, edgeMag, srcW, srcH);

            var entropyMap = BuildLocalEntropyMap(grayscale, srcW, srcH, blockSize: 16);

            // Combine entropy and edge into a single interestingness score
            float maxEntropy = 0f, maxEdge = 0f;
            for (int i = 0; i < srcW * srcH; i++)
            {
                if (entropyMap[i] > maxEntropy) maxEntropy = entropyMap[i];
                if (edgeMag[i] > maxEdge) maxEdge = edgeMag[i];
            }

            if (maxEntropy < 1e-6f) maxEntropy = 1f;
            if (maxEdge < 1e-6f) maxEdge = 1f;

            var result = new ImageFrame();
            result.Initialize(srcW, srcH, ColorspaceType.SRGB, false);

            Parallel.For(0, srcH, y =>
            {
                var row = result.GetPixelRowForWrite(y);
                for (int x = 0; x < srcW; x++)
                {
                    int idx = y * srcW + x;
                    float normEntropy = entropyMap[idx] / maxEntropy;
                    float normEdge = edgeMag[idx] / maxEdge;
                    float combined = Math.Min(1f, normEntropy * 0.67f + normEdge * 0.33f);

                    // Heat map: black → blue → cyan → green → yellow → red → white
                    HeatmapColor(combined, out ushort r, out ushort g, out ushort b);
                    int px = x * 3;
                    row[px] = r;
                    row[px + 1] = g;
                    row[px + 2] = b;
                }
            });

            ArrayPool<float>.Shared.Return(entropyMap);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grayscale);
            ArrayPool<float>.Shared.Return(edgeMag);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void HeatmapColor(float t, out ushort r, out ushort g, out ushort b)
    {
        // 5-stop heatmap
        if (t < 0.25f)
        {
            float s = t / 0.25f;
            r = 0;
            g = 0;
            b = (ushort)(s * Quantum.MaxValue);
        }
        else if (t < 0.5f)
        {
            float s = (t - 0.25f) / 0.25f;
            r = 0;
            g = (ushort)(s * Quantum.MaxValue);
            b = (ushort)((1f - s) * Quantum.MaxValue);
        }
        else if (t < 0.75f)
        {
            float s = (t - 0.5f) / 0.25f;
            r = (ushort)(s * Quantum.MaxValue);
            g = Quantum.MaxValue;
            b = 0;
        }
        else
        {
            float s = (t - 0.75f) / 0.25f;
            r = Quantum.MaxValue;
            g = (ushort)((1f - s) * Quantum.MaxValue);
            b = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double ScoreRegion(double[] integralEntropy, double[] integralEdge,
        double[] integralColor, double[] integralSaliency,
        int x, int y, int tw, int th, int srcW,
        double centerX, double centerY, double sigmaX, double sigmaY)
    {
        double entropy = QueryIntegral(integralEntropy, x, y, x + tw - 1, y + th - 1, srcW);
        double edge = QueryIntegral(integralEdge, x, y, x + tw - 1, y + th - 1, srcW);
        double color = QueryIntegral(integralColor, x, y, x + tw - 1, y + th - 1, srcW);
        double saliency = QueryIntegral(integralSaliency, x, y, x + tw - 1, y + th - 1, srcW);

        double area = tw * th;
        // Saliency (rare/unique colors) is the primary signal — finds the "subject"
        // Entropy adds texture detail, color variance adds chromatic interest
        // Edge has minimal weight to avoid thin-line dominance (stool legs etc.)
        double content = (saliency / area) * 1.5
                       + (entropy / area) * 0.6
                       + (color / area) * 0.4
                       + (edge / area) * 0.05;

        // Center bias — subjects tend to be near the center
        double dx = (x - centerX) / sigmaX;
        double dy = (y - centerY) / sigmaY;
        double centerBias = Math.Exp(-0.5 * (dx * dx + dy * dy));
        return content * (0.6 + 0.4 * centerBias);
    }

    /// <summary>
    /// Finds the weighted centroid of subject matter in the image.
    /// Uses interest scores as weights so the centroid is pulled toward
    /// the most visually important regions.
    /// </summary>
    static (double X, double Y) FindSubjectCentroid(float[] interestMap, int width, int height)
    {
        double sumW = 0, sumWX = 0, sumWY = 0;

        for (int y = 0; y < height; y++)
        {
            int rowOff = y * width;
            for (int x = 0; x < width; x++)
            {
                double w = interestMap[rowOff + x];
                if (w > 0)
                {
                    sumW += w;
                    sumWX += w * x;
                    sumWY += w * y;
                }
            }
        }

        if (sumW < 1e-10)
            return (width * 0.5, height * 0.5);

        return (sumWX / sumW, sumWY / sumW);
    }

    /// <summary>
    /// Detects the dominant background color by sampling the four corner regions.
    /// Returns normalized (R, G, B) in [0, 1] range.
    /// </summary>
    static (double R, double G, double B) DetectBackgroundColor(ImageFrame source, int srcW, int srcH)
    {
        int sampleSize = Math.Max(1, Math.Min(srcW, srcH) / 10);
        double sumR = 0, sumG = 0, sumB = 0;
        int count = 0;
        int channels = source.HasAlpha ? 4 : 3;

        // Sample four corners
        (int, int, int, int)[] corners = [
            (0, 0, sampleSize, sampleSize),
            (srcW - sampleSize, 0, srcW, sampleSize),
            (0, srcH - sampleSize, sampleSize, srcH),
            (srcW - sampleSize, srcH - sampleSize, srcW, srcH)
        ];

        foreach (var (x0, y0, x1, y1) in corners)
        {
            for (int y = y0; y < y1; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = x0; x < x1; x++)
                {
                    int off = x * channels;
                    sumR += row[off] * Quantum.Scale;
                    sumG += row[off + 1] * Quantum.Scale;
                    sumB += row[off + 2] * Quantum.Scale;
                    count++;
                }
            }
        }

        return (sumR / count, sumG / count, sumB / count);
    }

    /// <summary>
    /// Finds the bounding box of foreground pixels (those differing from background
    /// by more than the given threshold in any RGB channel).
    /// </summary>
    static (int Left, int Top, int Right, int Bottom) FindForegroundBounds(
        ImageFrame source, int srcW, int srcH,
        (double R, double G, double B) bgColor, double threshold)
    {
        int left = srcW, top = srcH, right = 0, bottom = 0;
        int channels = source.HasAlpha ? 4 : 3;

        for (int y = 0; y < srcH; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < srcW; x++)
            {
                int off = x * channels;
                double r = row[off] * Quantum.Scale;
                double g = row[off + 1] * Quantum.Scale;
                double b = row[off + 2] * Quantum.Scale;

                double dr = Math.Abs(r - bgColor.R);
                double dg = Math.Abs(g - bgColor.G);
                double db = Math.Abs(b - bgColor.B);

                if (dr > threshold || dg > threshold || db > threshold)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
        }

        // Fallback if no foreground found
        if (left >= right || top >= bottom)
            return (0, 0, srcW, srcH);

        return (left, top, right + 1, bottom + 1);
    }

    static float[] BuildColorVarianceMap(ImageFrame source, int width, int height, int blockSize)
    {
        var colorVar = ArrayPool<float>.Shared.Rent(width * height);
        int halfBlock = blockSize / 2;
        int channels = source.HasAlpha ? 4 : 3;

        // Pre-extract normalized RGB for each pixel
        var pixR = ArrayPool<float>.Shared.Rent(width * height);
        var pixG = ArrayPool<float>.Shared.Rent(width * height);
        var pixB = ArrayPool<float>.Shared.Rent(width * height);

        try
        {
            Parallel.For(0, height, y =>
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int px = x * channels;
                    int idx = y * width + x;
                    pixR[idx] = row[px] * (float)Quantum.Scale;
                    pixG[idx] = row[px + 1] * (float)Quantum.Scale;
                    pixB[idx] = row[px + 2] * (float)Quantum.Scale;
                }
            });

            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int y0 = Math.Max(0, y - halfBlock);
                    int y1 = Math.Min(height - 1, y + halfBlock);
                    int x0 = Math.Max(0, x - halfBlock);
                    int x1 = Math.Min(width - 1, x + halfBlock);

                    float sumR = 0, sumG = 0, sumB = 0;
                    float sumR2 = 0, sumG2 = 0, sumB2 = 0;
                    int count = 0;

                    for (int by = y0; by <= y1; by++)
                    {
                        for (int bx = x0; bx <= x1; bx++)
                        {
                            int idx = by * width + bx;
                            float r = pixR[idx], g = pixG[idx], b = pixB[idx];
                            sumR += r; sumG += g; sumB += b;
                            sumR2 += r * r; sumG2 += g * g; sumB2 += b * b;
                            count++;
                        }
                    }

                    float invN = 1f / count;
                    float varR = sumR2 * invN - (sumR * invN) * (sumR * invN);
                    float varG = sumG2 * invN - (sumG * invN) * (sumG * invN);
                    float varB = sumB2 * invN - (sumB * invN) * (sumB * invN);
                    colorVar[y * width + x] = MathF.Max(0, varR + varG + varB);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pixR);
            ArrayPool<float>.Shared.Return(pixG);
            ArrayPool<float>.Shared.Return(pixB);
        }

        return colorVar;
    }

    /// <summary>
    /// Builds a saliency map based on color rarity. Pixels with uncommon colors
    /// (e.g., skin tones, unique objects) score higher than pixels with common
    /// colors (e.g., blue robe, white background). Uses a coarse 6×6×6 color
    /// histogram to measure frequency, then rarity = 1/frequency.
    /// </summary>
    static float[] BuildSaliencyMap(ImageFrame source, int width, int height)
    {
        const int bins = 6;
        const int binSize = 256 / bins;
        int channels = source.HasAlpha ? 4 : 3;
        var histogram = new int[bins * bins * bins];
        int totalPixels = width * height;

        // Pass 1: Build coarse color histogram
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int px = x * channels;
                int rb = Math.Min(bins - 1, (int)(row[px] * Quantum.Scale * 255) / binSize);
                int gb = Math.Min(bins - 1, (int)(row[px + 1] * Quantum.Scale * 255) / binSize);
                int bb = Math.Min(bins - 1, (int)(row[px + 2] * Quantum.Scale * 255) / binSize);
                histogram[rb * bins * bins + gb * bins + bb]++;
            }
        }

        // Convert histogram to rarity — background (most common bins) scores zero
        var rarity = new float[bins * bins * bins];
        // Find the max frequency to establish a threshold
        int maxFreq = 0;
        for (int i = 0; i < histogram.Length; i++)
            if (histogram[i] > maxFreq) maxFreq = histogram[i];

        // Bins with > 10% of max frequency are considered "common" (background-like)
        float commonThreshold = maxFreq * 0.1f;
        for (int i = 0; i < rarity.Length; i++)
        {
            if (histogram[i] > 0 && histogram[i] < commonThreshold)
                rarity[i] = MathF.Log(1f + (float)totalPixels / histogram[i]);
            // Common bins and empty bins score 0
        }

        // Pass 2: Assign rarity score to each pixel
        var saliencyMap = ArrayPool<float>.Shared.Rent(width * height);
        Parallel.For(0, height, y =>
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int px = x * channels;
                int rb = Math.Min(bins - 1, (int)(row[px] * Quantum.Scale * 255) / binSize);
                int gb = Math.Min(bins - 1, (int)(row[px + 1] * Quantum.Scale * 255) / binSize);
                int bb = Math.Min(bins - 1, (int)(row[px + 2] * Quantum.Scale * 255) / binSize);
                saliencyMap[y * width + x] = rarity[rb * bins * bins + gb * bins + bb];
            }
        });

        return saliencyMap;
    }

    /// <summary>
    /// Builds an integral image using log(1 + value) to compress dynamic range.
    /// Prevents thin high-contrast edges from dominating the score.
    /// </summary>
    static double[] BuildIntegralImageClamped(float[] values, int width, int height)
    {
        var integral = ArrayPool<double>.Shared.Rent((width + 1) * (height + 1));
        int stride = width + 1;

        for (int i = 0; i < stride; i++) integral[i] = 0;
        for (int y = 0; y <= height; y++) integral[y * stride] = 0;

        for (int y = 1; y <= height; y++)
        {
            double rowSum = 0;
            for (int x = 1; x <= width; x++)
            {
                // Log-compress to reduce impact of extreme edge values
                rowSum += Math.Log(1.0 + values[(y - 1) * width + (x - 1)]);
                integral[y * stride + x] = rowSum + integral[(y - 1) * stride + x];
            }
        }

        return integral;
    }

    static void BuildGrayscaleAndEdge(ImageFrame source, byte[] grayscale, float[] edgeMag,
        int width, int height)
    {
        int channels = source.HasAlpha ? 4 : 3;

        Parallel.For(0, height, y =>
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int px = x * channels;
                double rr = row[px] * Quantum.Scale;
                double gg = row[px + 1] * Quantum.Scale;
                double bb = row[px + 2] * Quantum.Scale;
                grayscale[y * width + x] = (byte)(0.299 * rr * 255 + 0.587 * gg * 255 + 0.114 * bb * 255);
            }
        });

        // Sobel edge detection
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int gx = -grayscale[(y - 1) * width + (x - 1)]
                          + grayscale[(y - 1) * width + (x + 1)]
                          - 2 * grayscale[y * width + (x - 1)]
                          + 2 * grayscale[y * width + (x + 1)]
                          - grayscale[(y + 1) * width + (x - 1)]
                          + grayscale[(y + 1) * width + (x + 1)];

                int gy = -grayscale[(y - 1) * width + (x - 1)]
                          - 2 * grayscale[(y - 1) * width + x]
                          - grayscale[(y - 1) * width + (x + 1)]
                          + grayscale[(y + 1) * width + (x - 1)]
                          + 2 * grayscale[(y + 1) * width + x]
                          + grayscale[(y + 1) * width + (x + 1)];

                edgeMag[y * width + x] = MathF.Sqrt(gx * gx + gy * gy);
            }
        });
    }

    static float[] BuildLocalEntropyMap(byte[] grayscale, int width, int height, int blockSize)
    {
        var entropyMap = ArrayPool<float>.Shared.Rent(width * height);
        int halfBlock = blockSize / 2;

        Parallel.For(0, height, y =>
        {
            Span<int> hist = stackalloc int[256];
            for (int x = 0; x < width; x++)
            {
                hist.Clear();
                int count = 0;

                int y0 = Math.Max(0, y - halfBlock);
                int y1 = Math.Min(height - 1, y + halfBlock);
                int x0 = Math.Max(0, x - halfBlock);
                int x1 = Math.Min(width - 1, x + halfBlock);

                for (int by = y0; by <= y1; by++)
                {
                    for (int bx = x0; bx <= x1; bx++)
                    {
                        hist[grayscale[by * width + bx]]++;
                        count++;
                    }
                }

                float entropy = 0f;
                float invCount = 1f / count;
                for (int i = 0; i < 256; i++)
                {
                    if (hist[i] > 0)
                    {
                        float p = hist[i] * invCount;
                        entropy -= p * MathF.Log2(p);
                    }
                }

                entropyMap[y * width + x] = entropy;
            }
        });

        return entropyMap;
    }

    static double[] BuildIntegralImage(float[] values, int width, int height)
    {
        var integral = ArrayPool<double>.Shared.Rent((width + 1) * (height + 1));
        int stride = width + 1;

        // First row and column are zero (already from Rent default for new arrays,
        // but we clear explicitly for rented buffers)
        for (int i = 0; i < stride; i++) integral[i] = 0;
        for (int y = 0; y <= height; y++) integral[y * stride] = 0;

        for (int y = 1; y <= height; y++)
        {
            double rowSum = 0;
            for (int x = 1; x <= width; x++)
            {
                rowSum += values[(y - 1) * width + (x - 1)];
                integral[y * stride + x] = rowSum + integral[(y - 1) * stride + x];
            }
        }

        return integral;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double QueryIntegral(double[] integral, int x0, int y0, int x1, int y1, int width)
    {
        int stride = width + 1;
        // SAT formula: sum = D - B - C + A
        return integral[(y1 + 1) * stride + (x1 + 1)]
             - integral[y0 * stride + (x1 + 1)]
             - integral[(y1 + 1) * stride + x0]
             + integral[y0 * stride + x0];
    }
}
