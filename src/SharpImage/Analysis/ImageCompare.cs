using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Analysis;

/// <summary>
/// Image comparison metrics: RMSE, PSNR, SSIM, MAE, MSE, peak absolute error. Hot-path operations use SIMD-accelerated
/// difference calculations.
/// </summary>
public static class ImageCompare
{
    /// <summary>
    /// Mean Squared Error — average of squared per-pixel differences, normalized to [0..1]. 0 = identical, 1 =
    /// maximally different.
    /// </summary>
    public static double MeanSquaredError(ImageFrame imageA, ImageFrame imageB)
    {
        ValidateSameSize(imageA, imageB);
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        int channelsA = imageA.NumberOfChannels;
        int channelsB = imageB.NumberOfChannels;
        int channels = Math.Min(channelsA, channelsB);
        double scale = Quantum.Scale * Quantum.Scale;
        double sumSq = 0;

        for (int y = 0;y < height;y++)
        {
            var rowA = imageA.GetPixelRow(y);
            var rowB = imageB.GetPixelRow(y);
            sumSq += SimdOps.SumSquaredDifferences(rowA, rowB, channelsA, channelsB, channels, width);
        }

        long count = (long)width * height * channels;
        return count > 0 ? sumSq * scale / count : 0;
    }

    /// <summary>
    /// Root Mean Squared Error — square root of MSE. Range [0..1].
    /// </summary>
    public static double RootMeanSquaredError(ImageFrame imageA, ImageFrame imageB)
    {
        return Math.Sqrt(MeanSquaredError(imageA, imageB));
    }

    /// <summary>
    /// Peak Signal-to-Noise Ratio in decibels. Higher = more similar. Returns double.PositiveInfinity for identical
    /// images.
    /// </summary>
    public static double PeakSignalToNoiseRatio(ImageFrame imageA, ImageFrame imageB)
    {
        double mse = MeanSquaredError(imageA, imageB);
        if (mse == 0)
        {
            return double.PositiveInfinity;
        }

        return 10.0 * Math.Log10(1.0 / mse);
    }

    /// <summary>
    /// Mean Absolute Error — average of absolute per-pixel differences, normalized to [0..1].
    /// </summary>
    public static double MeanAbsoluteError(ImageFrame imageA, ImageFrame imageB)
    {
        ValidateSameSize(imageA, imageB);
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        int channels = Math.Min(imageA.NumberOfChannels, imageB.NumberOfChannels);
        double sumAbs = 0;
        long count = 0;

        for (int y = 0;y < height;y++)
        {
            var rowA = imageA.GetPixelRow(y);
            var rowB = imageB.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    double a = rowA[x * imageA.NumberOfChannels + c] * Quantum.Scale;
                    double b = rowB[x * imageB.NumberOfChannels + c] * Quantum.Scale;
                    sumAbs += Math.Abs(a - b);
                    count++;
                }
            }
        }

        return count > 0 ? sumAbs / count : 0;
    }

    /// <summary>
    /// Peak Absolute Error — maximum per-pixel difference across all channels. Range [0..1].
    /// </summary>
    public static double PeakAbsoluteError(ImageFrame imageA, ImageFrame imageB)
    {
        ValidateSameSize(imageA, imageB);
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        int channels = Math.Min(imageA.NumberOfChannels, imageB.NumberOfChannels);
        double maxDiff = 0;

        for (int y = 0;y < height;y++)
        {
            var rowA = imageA.GetPixelRow(y);
            var rowB = imageB.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    double a = rowA[x * imageA.NumberOfChannels + c] * Quantum.Scale;
                    double b = rowB[x * imageB.NumberOfChannels + c] * Quantum.Scale;
                    double diff = Math.Abs(a - b);
                    if (diff > maxDiff)
                    {
                        maxDiff = diff;
                    }
                }
            }
        }

        return maxDiff;
    }

    /// <summary>
    /// Absolute Error count — number of pixels that differ beyond a fuzz threshold. Fuzz is normalized [0..1] where 0
    /// means exact match required.
    /// </summary>
    public static long AbsoluteErrorCount(ImageFrame imageA, ImageFrame imageB, double fuzz = 0)
    {
        ValidateSameSize(imageA, imageB);
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        int channels = Math.Min(imageA.NumberOfChannels, imageB.NumberOfChannels);
        double fuzzSq = fuzz * fuzz * channels;
        long count = 0;

        for (int y = 0;y < height;y++)
        {
            var rowA = imageA.GetPixelRow(y);
            var rowB = imageB.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                double pixelDistSq = 0;
                for (int c = 0;c < channels;c++)
                {
                    double a = rowA[x * imageA.NumberOfChannels + c] * Quantum.Scale;
                    double b = rowB[x * imageB.NumberOfChannels + c] * Quantum.Scale;
                    double diff = a - b;
                    pixelDistSq += diff * diff;
                }
                if (pixelDistSq > fuzzSq)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Structural Similarity Index (SSIM). Range [-1..1], where 1 = identical. Uses 8x8 windows with luminance and
    /// contrast comparison. Uses ArrayPool to avoid managed heap allocation for luminance arrays.
    /// </summary>
    public static double StructuralSimilarity(ImageFrame imageA, ImageFrame imageB)
    {
        ValidateSameSize(imageA, imageB);
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;

        if (width < 8 || height < 8)
        {
            return MeanSquaredError(imageA, imageB) == 0 ? 1.0 : 0.0;
        }

        // SSIM constants (from Wang et al. 2004)
        const double k1 = 0.01, k2 = 0.03;
        const double c1 = k1 * k1;
        const double c2 = k2 * k2;
        const int windowSize = 8;

        int channels = Math.Min(imageA.NumberOfChannels, imageB.NumberOfChannels);
        int pixelCount = width * height;

        // Use ArrayPool instead of allocating new arrays
        var lumABuf = ArrayPool<double>.Shared.Rent(pixelCount);
        var lumBBuf = ArrayPool<double>.Shared.Rent(pixelCount);
        try
        {
            var lumA = lumABuf.AsSpan(0, pixelCount);
            var lumB = lumBBuf.AsSpan(0, pixelCount);

            // Extract luminance using SIMD-aware helper
            for (int y = 0;y < height;y++)
            {
                SimdOps.ExtractLuminanceRow(imageA.GetPixelRow(y),
                    lumA.Slice(y * width, width), width, imageA.NumberOfChannels);
                SimdOps.ExtractLuminanceRow(imageB.GetPixelRow(y),
                    lumB.Slice(y * width, width), width, imageB.NumberOfChannels);
            }

            double ssimSum = 0;
            int windowCount = 0;

            for (int wy = 0;wy <= height - windowSize;wy += windowSize)
            {
                for (int wx = 0;wx <= width - windowSize;wx += windowSize)
                {
                    double muA = 0, muB = 0;
                    int n = windowSize * windowSize;

                    for (int dy = 0;dy < windowSize;dy++)
                    {
                        int rowOff = (wy + dy) * width + wx;
                        for (int dx = 0;dx < windowSize;dx++)
                        {
                            muA += lumA[rowOff + dx];
                            muB += lumB[rowOff + dx];
                        }
                    }
                    muA /= n;
                    muB /= n;

                    double sigmaAA = 0, sigmaBB = 0, sigmaAB = 0;
                    for (int dy = 0;dy < windowSize;dy++)
                    {
                        int rowOff = (wy + dy) * width + wx;
                        for (int dx = 0;dx < windowSize;dx++)
                        {
                            double da = lumA[rowOff + dx] - muA;
                            double db = lumB[rowOff + dx] - muB;
                            sigmaAA += da * da;
                            sigmaBB += db * db;
                            sigmaAB += da * db;
                        }
                    }
                    sigmaAA /= n;
                    sigmaBB /= n;
                    sigmaAB /= n;

                    double numerator = (2 * muA * muB + c1) * (2 * sigmaAB + c2);
                    double denominator = (muA * muA + muB * muB + c1) * (sigmaAA + sigmaBB + c2);
                    ssimSum += numerator / denominator;
                    windowCount++;
                }
            }

            return windowCount > 0 ? ssimSum / windowCount : 1.0;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(lumABuf);
            ArrayPool<double>.Shared.Return(lumBBuf);
        }
    }

    /// <summary>
    /// Creates a difference image highlighting pixel differences between two images. Differences are scaled to fill the
    /// full quantum range for visibility.
    /// </summary>
    public static ImageFrame CreateDifferenceImage(ImageFrame imageA, ImageFrame imageB)
    {
        ValidateSameSize(imageA, imageB);
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;
        int channelsA = imageA.NumberOfChannels;
        int channelsB = imageB.NumberOfChannels;
        int channels = Math.Min(channelsA, channelsB);

        // Pass 1: find the maximum per-channel difference for scaling
        int maxDiff = 0;
        for (int y = 0;y < height;y++)
        {
            var rowA = imageA.GetPixelRow(y);
            var rowB = imageB.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int diff = Math.Abs(rowA[x * channelsA + c] - rowB[x * channelsB + c]);
                    if (diff > maxDiff)
                    {
                        maxDiff = diff;
                    }
                }
            }
        }

        // Scale factor to stretch differences to full quantum range
        double scale = maxDiff > 0 ? (double)Quantum.MaxValue / maxDiff : 1.0;

        // Pass 2: write scaled difference image
        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, imageA.Colorspace, false);

        for (int y = 0;y < height;y++)
        {
            var rowA = imageA.GetPixelRow(y);
            var rowB = imageB.GetPixelRow(y);
            var rowOut = result.GetPixelRowForWrite(y);
            int outChannels = result.NumberOfChannels;

            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    int diff = Math.Abs(rowA[x * channelsA + c] - rowB[x * channelsB + c]);
                    rowOut[x * outChannels + c] = Quantum.Clamp((int)(diff * scale));
                }
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateSameSize(ImageFrame a, ImageFrame b)
    {
        if (a.Columns != b.Columns || a.Rows != b.Rows)
        {
            throw new ArgumentException(
                        $"Images must have same dimensions: {a.Columns}x{a.Rows} vs {b.Columns}x{b.Rows}");
        }
    }
}
