// SharpImage — Histogram Matching (Color Transfer).
// Adjusts the color distribution of a source image to match a reference image.
// Works per-channel using CDF (cumulative distribution function) mapping.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Enhance;

/// <summary>
/// Histogram matching transfers the color distribution from a reference image to a source image.
/// Each channel's histogram is matched independently using CDF-based mapping.
/// </summary>
public static class HistogramMatching
{
    private const int BinCount = 256;

    /// <summary>
    /// Match the histogram of the source image to the reference image.
    /// Returns a new image with the same dimensions as source, but with the color
    /// distribution of the reference.
    /// </summary>
    public static ImageFrame Apply(ImageFrame source, ImageFrame reference)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;

        // Build histograms for both images
        int[][] srcHistograms = BuildHistograms(source, colorChannels);
        int[][] refHistograms = BuildHistograms(reference, colorChannels);

        // Build CDFs
        double[][] srcCdfs = new double[colorChannels][];
        double[][] refCdfs = new double[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
        {
            srcCdfs[c] = BuildCdf(srcHistograms[c]);
            refCdfs[c] = BuildCdf(refHistograms[c]);
        }

        // Build lookup tables (source bin → matched bin)
        ushort[][] lookupTables = new ushort[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
        {
            lookupTables[c] = BuildLookupTable(srcCdfs[c], refCdfs[c]);
        }

        // Apply mapping
        var result = new ImageFrame();
        result.Initialize(srcWidth, srcHeight, source.Colorspace, hasAlpha);

        Parallel.For(0, srcHeight, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < srcWidth; x++)
            {
                int offset = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    int bin = QuantumToBin(srcRow[offset + c]);
                    dstRow[offset + c] = lookupTables[c][bin];
                }

                // Preserve alpha
                if (hasAlpha)
                    dstRow[offset + channels - 1] = srcRow[offset + channels - 1];
            }
        });

        return result;
    }

    /// <summary>
    /// Compute the histogram for a single channel (for visualization/analysis).
    /// Returns an array of BinCount values representing the frequency of each intensity level.
    /// </summary>
    public static int[] ComputeHistogram(ImageFrame image, int channelIndex)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        var histogram = new int[BinCount];

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int bin = QuantumToBin(row[x * channels + channelIndex]);
                histogram[bin]++;
            }
        }

        return histogram;
    }

    /// <summary>
    /// Render a histogram visualization as a grayscale image.
    /// </summary>
    public static ImageFrame RenderHistogram(ImageFrame image, int renderWidth = 256, int renderHeight = 200)
    {
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(renderWidth, renderHeight, ColorspaceType.SRGB, false);

        // Compute histograms for all color channels
        int[][] histograms = new int[colorChannels][];
        int maxCount = 0;
        for (int c = 0; c < colorChannels; c++)
        {
            histograms[c] = ComputeHistogram(image, c);
            for (int i = 0; i < BinCount; i++)
                maxCount = Math.Max(maxCount, histograms[c][i]);
        }

        if (maxCount == 0) return result;

        // Channel colors: R, G, B (or just white for single-channel)
        ushort[][] channelColors = colorChannels >= 3
            ? [[Quantum.MaxValue, 0, 0], [0, Quantum.MaxValue, 0], [0, 0, Quantum.MaxValue]]
            : [[Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue]];

        double binWidth = (double)renderWidth / BinCount;

        for (int c = 0; c < colorChannels; c++)
        {
            for (int bin = 0; bin < BinCount; bin++)
            {
                double barHeight = (double)histograms[c][bin] / maxCount * renderHeight;
                int xStart = (int)(bin * binWidth);
                int xEnd = Math.Min((int)((bin + 1) * binWidth), renderWidth);

                for (int y = renderHeight - 1; y >= renderHeight - (int)barHeight; y--)
                {
                    var row = result.GetPixelRowForWrite(y);
                    for (int x = xStart; x < xEnd; x++)
                    {
                        int off = x * 3;
                        // Additive blend for overlapping channels
                        row[off] = Quantum.Clamp(row[off] + (double)channelColors[c][0] / colorChannels);
                        row[off + 1] = Quantum.Clamp(row[off + 1] + (double)channelColors[c][1] / colorChannels);
                        row[off + 2] = Quantum.Clamp(row[off + 2] + (double)channelColors[c][2] / colorChannels);
                    }
                }
            }
        }

        return result;
    }

    // ─── Internal Helpers ──────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QuantumToBin(ushort quantum)
    {
        return Math.Min((int)((double)quantum / Quantum.MaxValue * (BinCount - 1)), BinCount - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort BinToQuantum(int bin)
    {
        return Quantum.Clamp((double)bin / (BinCount - 1) * Quantum.MaxValue);
    }

    private static int[][] BuildHistograms(ImageFrame image, int colorChannels)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        var histograms = new int[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
            histograms[c] = new int[BinCount];

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    int bin = QuantumToBin(row[offset + c]);
                    histograms[c][bin]++;
                }
            }
        }

        return histograms;
    }

    private static double[] BuildCdf(int[] histogram)
    {
        var cdf = new double[BinCount];
        long total = 0;
        for (int i = 0; i < BinCount; i++)
            total += histogram[i];

        if (total == 0) return cdf;

        long cumulative = 0;
        for (int i = 0; i < BinCount; i++)
        {
            cumulative += histogram[i];
            cdf[i] = (double)cumulative / total;
        }

        return cdf;
    }

    private static ushort[] BuildLookupTable(double[] srcCdf, double[] refCdf)
    {
        var lut = new ushort[BinCount];

        for (int srcBin = 0; srcBin < BinCount; srcBin++)
        {
            double srcVal = srcCdf[srcBin];

            // Find closest matching bin in reference CDF
            int bestBin = 0;
            double bestDiff = double.MaxValue;
            for (int refBin = 0; refBin < BinCount; refBin++)
            {
                double diff = Math.Abs(srcVal - refCdf[refBin]);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestBin = refBin;
                }
            }

            lut[srcBin] = BinToQuantum(bestBin);
        }

        return lut;
    }
}
