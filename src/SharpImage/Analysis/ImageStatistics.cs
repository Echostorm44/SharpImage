using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Analysis;

/// <summary>
/// Per-channel and whole-image statistics: mean, median, std dev, entropy, min, max, skewness, kurtosis, and Hu
/// invariant moments.
/// </summary>
public static class ImageStatistics
{
    /// <summary>
    /// Statistics for a single channel.
    /// </summary>
    public struct ChannelStats
    {
        public double Minimum;
        public double Maximum;
        public double Mean;
        public double StandardDeviation;
        public double Variance;
        public double Skewness;
        public double Kurtosis;
        public double Entropy;
        public double Median;
        public int Depth; // effective bit depth
    }

    /// <summary>
    /// Statistics for the whole image, one ChannelStats per channel plus a composite.
    /// </summary>
    public struct ImageStats
    {
        public ChannelStats[] Channels;
        public ChannelStats Composite;
    }

    /// <summary>
    /// Compute per-channel statistics for the image. All values normalized to [0..1].
    /// </summary>
    public static ImageStats GetStatistics(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        long pixelCount = (long)width * height;

        var stats = new ImageStats
        {
            Channels = new ChannelStats[channels]
        };

        for (int c = 0;c < channels;c++)
        {
            stats.Channels[c] = ComputeChannelStats(image, c, width, height, pixelCount);
        }

        // Composite: average of all channels
        stats.Composite = ComputeComposite(stats.Channels);
        return stats;
    }

    /// <summary>
    /// Compute the 7 Hu invariant moments for the image (grayscale luminance). These are rotation, scale, and
    /// translation invariant.
    /// </summary>
    public static double[] GetHuMoments(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        // Compute grayscale luminance
        double[] lum = new double[width * height];
        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                if (channels >= 3)
                {
                    lum[y * width + x] =
                                        0.2126 * row[off] * Quantum.Scale +
                                        0.7152 * row[off + 1] * Quantum.Scale +
                                        0.0722 * row[off + 2] * Quantum.Scale;
                }
                else
                {
                    lum[y * width + x] = row[off] * Quantum.Scale;
                }
            }
        }

        // Raw moments m_pq
        double m00 = 0, m10 = 0, m01 = 0;
        double m20 = 0, m11 = 0, m02 = 0;
        double m30 = 0, m21 = 0, m12 = 0, m03 = 0;
        for (int y = 0;y < height;y++)
        {
            for (int x = 0;x < width;x++)
            {
                double v = lum[y * width + x];
                m00 += v;
                m10 += x * v;
                m01 += y * v;
                m20 += x * x * v;
                m11 += x * y * v;
                m02 += y * y * v;
                m30 += x * x * x * v;
                m21 += x * x * y * v;
                m12 += x * y * y * v;
                m03 += y * y * y * v;
            }
        }

        if (m00 == 0)
        {
            return new double[7];
        }

        // Centroid
        double xc = m10 / m00;
        double yc = m01 / m00;

        // Central moments mu_pq
        double mu20 = m20 - xc * m10;
        double mu11 = m11 - xc * m01;
        double mu02 = m02 - yc * m01;
        double mu30 = m30 - 3 * xc * m20 + 2 * xc * xc * m10;
        double mu21 = m21 - 2 * xc * m11 - yc * m20 + 2 * xc * xc * m01;
        double mu12 = m12 - 2 * yc * m11 - xc * m02 + 2 * yc * yc * m10;
        double mu03 = m03 - 3 * yc * m02 + 2 * yc * yc * m01;

        // Normalized central moments eta_pq = mu_pq / m00^((p+q)/2 + 1)
        double inv2 = 1.0 / Math.Pow(m00, 2.0);   // (p+q=2) -> m00^2
        double inv2_5 = 1.0 / Math.Pow(m00, 2.5); // (p+q=3) -> m00^2.5

        double n20 = mu20 * inv2;
        double n11 = mu11 * inv2;
        double n02 = mu02 * inv2;
        double n30 = mu30 * inv2_5;
        double n21 = mu21 * inv2_5;
        double n12 = mu12 * inv2_5;
        double n03 = mu03 * inv2_5;

        // 7 Hu moments
        double[] hu = new double[7];
        hu[0] = n20 + n02;
        hu[1] = (n20 - n02) * (n20 - n02) + 4 * n11 * n11;
        hu[2] = (n30 - 3 * n12) * (n30 - 3 * n12) + (3 * n21 - n03) * (3 * n21 - n03);
        hu[3] = (n30 + n12) * (n30 + n12) + (n21 + n03) * (n21 + n03);

        double a = n30 + n12, b = n21 + n03;
        hu[4] = (n30 - 3 * n12) * a * (a * a - 3 * b * b)
              + (3 * n21 - n03) * b * (3 * a * a - b * b);
        hu[5] = (n20 - n02) * (a * a - b * b) + 4 * n11 * a * b;
        hu[6] = (3 * n21 - n03) * a * (a * a - 3 * b * b)
              - (n30 - 3 * n12) * b * (3 * a * a - b * b);

        return hu;
    }

    /// <summary>
    /// Builds a histogram (256 bins) for a single channel.
    /// </summary>
    public static long[] GetHistogram(ImageFrame image, int channel)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        if (channel < 0 || channel >= channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        long[] histogram = new long[256];
        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                histogram[Quantum.ScaleToByte(row[x * channels + channel])]++;
            }
        }
        return histogram;
    }

    private static ChannelStats ComputeChannelStats(ImageFrame image, int channel,
        int width, int height, long pixelCount)
    {
        int channels = image.NumberOfChannels;
        var stats = new ChannelStats
        {
            Minimum = double.MaxValue,
            Maximum = double.MinValue
        };

        // Pass 1: min, max, mean (Welford's online algorithm for variance)
        double m1 = 0, m2 = 0, m3 = 0, m4 = 0;
        long n = 0;

        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                double v = row[x * channels + channel] * Quantum.Scale;
                n++;
                double delta = v - m1;
                double deltaN = delta / n;
                double deltaN2 = deltaN * deltaN;
                double term1 = delta * deltaN * (n - 1);
                m1 += deltaN;
                m4 += term1 * deltaN2 * (n * n - 3 * n + 3) + 6 * deltaN2 * m2 - 4 * deltaN * m3;
                m3 += term1 * deltaN * (n - 2) - 3 * deltaN * m2;
                m2 += term1;

                if (v < stats.Minimum)
                {
                    stats.Minimum = v;
                }

                if (v > stats.Maximum)
                {
                    stats.Maximum = v;
                }
            }
        }

        stats.Mean = m1;
        stats.Variance = n > 1 ? m2 / (n - 1) : 0;
        stats.StandardDeviation = Math.Sqrt(stats.Variance);
        stats.Skewness = m2 > 0 ? Math.Sqrt((double)n) * m3 / Math.Pow(m2, 1.5) : 0;
        stats.Kurtosis = m2 > 0 ? (double)n * m4 / (m2 * m2) - 3.0 : 0;

        // Histogram for entropy and median
        long[] histogram = new long[256];
        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                histogram[Quantum.ScaleToByte(row[x * channels + channel])]++;
            }
        }

        // Entropy: -sum(p * log2(p))
        double entropy = 0;
        for (int i = 0;i < 256;i++)
        {
            if (histogram[i] > 0)
            {
                double p = (double)histogram[i] / pixelCount;
                entropy -= p * Math.Log2(p);
            }
        }
        stats.Entropy = entropy;

        // Median: 50th percentile from histogram
        long halfCount = pixelCount / 2;
        long cumulative = 0;
        for (int i = 0;i < 256;i++)
        {
            cumulative += histogram[i];
            if (cumulative >= halfCount)
            {
                stats.Median = i / 255.0;
                break;
            }
        }

        // Effective depth
        stats.Depth = ComputeEffectiveDepth(image, channel, width, height);

        return stats;
    }

    private static int ComputeEffectiveDepth(ImageFrame image, int channel, int width, int height)
    {
        int channels = image.NumberOfChannels;
        // Check if values use fewer bits than the full quantum range
        for (int depth = 1;depth <= 16;depth++)
        {
            int levels = 1 << depth;
            double step = (double)Quantum.MaxValue / (levels - 1);
            bool allMatch = true;

            for (int y = 0;y < height && allMatch;y++)
            {
                var row = image.GetPixelRow(y);
                for (int x = 0;x < width && allMatch;x++)
                {
                    ushort v = row[x * channels + channel];
                    int nearest = (int)Math.Round(v / step) * (int)Math.Round(step);
                    if (Math.Abs(v - nearest) > 1)
                    {
                        allMatch = false;
                    }
                }
            }
            if (allMatch)
            {
                return depth;
            }
        }
        return 16;
    }

    private static ChannelStats ComputeComposite(ChannelStats[] channels)
    {
        if (channels.Length == 0)
        {
            return default;
        }

        var composite = new ChannelStats
        {
            Minimum = double.MaxValue,
            Maximum = double.MinValue,
            Depth = 0
        };

        foreach (var ch in channels)
        {
            if (ch.Minimum < composite.Minimum)
            {
                composite.Minimum = ch.Minimum;
            }

            if (ch.Maximum > composite.Maximum)
            {
                composite.Maximum = ch.Maximum;
            }

            composite.Mean += ch.Mean;
            composite.Variance += ch.Variance;
            composite.Entropy += ch.Entropy;
            composite.Median += ch.Median;
            composite.Skewness += ch.Skewness;
            composite.Kurtosis += ch.Kurtosis;
            if (ch.Depth > composite.Depth)
            {
                composite.Depth = ch.Depth;
            }
        }

        int n = channels.Length;
        composite.Mean /= n;
        composite.Variance /= n;
        composite.StandardDeviation = Math.Sqrt(composite.Variance);
        composite.Entropy /= n;
        composite.Median /= n;
        composite.Skewness /= n;
        composite.Kurtosis /= n;
        return composite;
    }
}
