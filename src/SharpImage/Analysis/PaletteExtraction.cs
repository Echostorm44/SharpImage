// SharpImage — Color palette extraction via k-means clustering.
// Extracts dominant colors from images, produces palette swatches,
// and maps images to their reduced-palette equivalents.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Analysis;

/// <summary>
/// Extracts dominant colors from images using k-means++ clustering.
/// Produces palette swatches, color-reduced images, and palette metadata.
/// </summary>
public static class PaletteExtraction
{
    /// <summary>A single color in the extracted palette with its dominance percentage.</summary>
    public readonly record struct PaletteColor(float R, float G, float B, double Percentage)
    {
        /// <summary>Returns hex string like #FF8040.</summary>
        public string ToHex()
        {
            int r = (int)(R * 255 + 0.5f);
            int g = (int)(G * 255 + 0.5f);
            int b = (int)(B * 255 + 0.5f);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        public ushort QuantumR => Quantum.Clamp(R * Quantum.MaxValue);
        public ushort QuantumG => Quantum.Clamp(G * Quantum.MaxValue);
        public ushort QuantumB => Quantum.Clamp(B * Quantum.MaxValue);
    }

    /// <summary>
    /// Extract the N most dominant colors from an image using k-means++ clustering.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="colorCount">Number of palette colors to extract (default 8).</param>
    /// <param name="maxIterations">Maximum k-means iterations (default 30).</param>
    /// <param name="sampleFraction">Fraction of pixels to sample for large images (default 0.25).</param>
    public static PaletteColor[] Extract(ImageFrame source, int colorCount = 8,
        int maxIterations = 30, double sampleFraction = 0.25)
    {
        var samples = SamplePixels(source, sampleFraction);
        var centroids = InitializeCentroidsKMeansPlusPlus(samples, colorCount);
        var assignments = new int[samples.Length];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // Assign each sample to nearest centroid
            bool changed = false;
            for (int i = 0; i < samples.Length; i++)
            {
                int nearest = FindNearestCentroid(samples[i], centroids);
                if (nearest != assignments[i])
                {
                    assignments[i] = nearest;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update centroids
            var sums = new (double R, double G, double B)[colorCount];
            var counts = new int[colorCount];

            for (int i = 0; i < samples.Length; i++)
            {
                int cluster = assignments[i];
                sums[cluster].R += samples[i].R;
                sums[cluster].G += samples[i].G;
                sums[cluster].B += samples[i].B;
                counts[cluster]++;
            }

            for (int c = 0; c < colorCount; c++)
            {
                if (counts[c] > 0)
                {
                    centroids[c] = (
                        (float)(sums[c].R / counts[c]),
                        (float)(sums[c].G / counts[c]),
                        (float)(sums[c].B / counts[c])
                    );
                }
            }
        }

        // Compute final counts
        var finalCounts = new int[colorCount];
        for (int i = 0; i < samples.Length; i++)
            finalCounts[assignments[i]]++;

        double totalSamples = samples.Length;
        var palette = new PaletteColor[colorCount];
        for (int c = 0; c < colorCount; c++)
        {
            palette[c] = new PaletteColor(
                centroids[c].R,
                centroids[c].G,
                centroids[c].B,
                finalCounts[c] / totalSamples * 100.0
            );
        }

        // Sort by percentage descending (most dominant first)
        Array.Sort(palette, (a, b) => b.Percentage.CompareTo(a.Percentage));
        return palette;
    }

    /// <summary>
    /// Render a palette swatch image. Each color is a horizontal band.
    /// Width of each band is proportional to the color's dominance.
    /// </summary>
    public static ImageFrame RenderSwatch(PaletteColor[] palette, int width = 600, int height = 100)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        // Proportional width bands
        int x = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            int bandWidth = (i < palette.Length - 1)
                ? (int)(width * palette[i].Percentage / 100.0)
                : width - x;

            if (bandWidth <= 0) continue;

            ushort r = palette[i].QuantumR;
            ushort g = palette[i].QuantumG;
            ushort b = palette[i].QuantumB;

            for (int row = 0; row < height; row++)
            {
                var pixels = frame.GetPixelRowForWrite(row);
                for (int col = x; col < Math.Min(x + bandWidth, width); col++)
                {
                    int offset = col * 3;
                    pixels[offset] = r;
                    pixels[offset + 1] = g;
                    pixels[offset + 2] = b;
                }
            }

            x += bandWidth;
        }

        return frame;
    }

    /// <summary>
    /// Map an image to a reduced palette. Each pixel is replaced by the nearest palette color.
    /// Returns a new frame.
    /// </summary>
    public static ImageFrame MapToPalette(ImageFrame source, PaletteColor[] palette)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int srcChannels = source.HasAlpha ? 4 : 3;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.SRGB, source.HasAlpha);

        double scale = 1.0 / Quantum.MaxValue;

        // Pre-compute palette as tuples
        var centroids = new (float R, float G, float B)[palette.Length];
        for (int i = 0; i < palette.Length; i++)
            centroids[i] = (palette[i].R, palette[i].G, palette[i].B);

        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int dstChannels = result.HasAlpha ? 4 : 3;

            for (int x = 0; x < width; x++)
            {
                int srcOffset = x * srcChannels;
                float r = (float)(srcRow[srcOffset] * scale);
                float g = (float)(srcRow[srcOffset + 1] * scale);
                float b = (float)(srcRow[srcOffset + 2] * scale);

                int nearest = FindNearestCentroid((r, g, b), centroids);

                int dstOffset = x * dstChannels;
                dstRow[dstOffset] = palette[nearest].QuantumR;
                dstRow[dstOffset + 1] = palette[nearest].QuantumG;
                dstRow[dstOffset + 2] = palette[nearest].QuantumB;

                if (source.HasAlpha)
                    dstRow[dstOffset + 3] = srcRow[srcOffset + 3]; // preserve alpha
            }
        }

        return result;
    }

    private static (float R, float G, float B)[] SamplePixels(ImageFrame source, double fraction)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.HasAlpha ? 4 : 3;
        long totalPixels = (long)width * height;
        int sampleCount = Math.Max(1000, (int)(totalPixels * Math.Clamp(fraction, 0.01, 1.0)));
        sampleCount = (int)Math.Min(sampleCount, totalPixels);

        double scale = 1.0 / Quantum.MaxValue;
        var rng = new Random(42); // deterministic for reproducibility

        if (sampleCount >= totalPixels)
        {
            // Sample all pixels
            var all = new (float R, float G, float B)[totalPixels];
            int idx = 0;
            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * channels;
                    all[idx++] = (
                        (float)(row[offset] * scale),
                        (float)(row[offset + 1] * scale),
                        (float)(row[offset + 2] * scale)
                    );
                }
            }
            return all;
        }

        // Reservoir sampling for large images
        var samples = new (float R, float G, float B)[sampleCount];
        int step = Math.Max(1, (int)(totalPixels / sampleCount));
        int sIdx = 0;

        for (int y = 0; y < height && sIdx < sampleCount; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width && sIdx < sampleCount; x++)
            {
                long pixelIndex = (long)y * width + x;
                if (pixelIndex % step == 0)
                {
                    int offset = x * channels;
                    samples[sIdx++] = (
                        (float)(row[offset] * scale),
                        (float)(row[offset + 1] * scale),
                        (float)(row[offset + 2] * scale)
                    );
                }
            }
        }

        return samples[..sIdx];
    }

    /// <summary>k-means++ initialization — select initial centroids with distance-weighted probability.</summary>
    private static (float R, float G, float B)[] InitializeCentroidsKMeansPlusPlus(
        (float R, float G, float B)[] samples, int k)
    {
        var rng = new Random(42);
        var centroids = new (float R, float G, float B)[k];

        // First centroid: random
        centroids[0] = samples[rng.Next(samples.Length)];

        var distances = new double[samples.Length];

        for (int c = 1; c < k; c++)
        {
            // Compute distance to nearest existing centroid
            double totalDist = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double minDist = double.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    double d = ColorDistanceSquared(samples[i], centroids[j]);
                    if (d < minDist) minDist = d;
                }
                distances[i] = minDist;
                totalDist += minDist;
            }

            // Weighted random selection
            double target = rng.NextDouble() * totalDist;
            double cumulative = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                cumulative += distances[i];
                if (cumulative >= target)
                {
                    centroids[c] = samples[i];
                    break;
                }
            }
        }

        return centroids;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNearestCentroid((float R, float G, float B) pixel,
        (float R, float G, float B)[] centroids)
    {
        int nearest = 0;
        double minDist = ColorDistanceSquared(pixel, centroids[0]);

        for (int i = 1; i < centroids.Length; i++)
        {
            double d = ColorDistanceSquared(pixel, centroids[i]);
            if (d < minDist)
            {
                minDist = d;
                nearest = i;
            }
        }

        return nearest;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ColorDistanceSquared((float R, float G, float B) a, (float R, float G, float B) b)
    {
        float dr = a.R - b.R;
        float dg = a.G - b.G;
        float db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }
}
