using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Analysis;

/// <summary>
/// Mean-shift color segmentation. Iteratively shifts each pixel to the mean of its
/// spatial-color neighborhood, grouping similar nearby colors into flat regions.
/// Produces a posterized/segmented look useful for object extraction.
/// </summary>
public static class MeanShift
{
    /// <summary>
    /// Apply mean-shift segmentation to the image.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="spatialRadius">Spatial radius in pixels (default: 10).</param>
    /// <param name="colorRadius">Color distance radius normalized to 0..1 range (default: 0.15).</param>
    /// <param name="maxIterations">Maximum shift iterations per pixel (default: 10).</param>
    /// <returns>Segmented image with smoothed color regions.</returns>
    public static ImageFrame Segment(ImageFrame source, int spatialRadius = 10, double colorRadius = 0.15, int maxIterations = 10)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        float colorRadiusSq = (float)(colorRadius * colorRadius);

        // Read all pixels into float array normalized to 0..1
        float[] pixels = new float[width * height * channels];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            int rowBase = y * width * channels;
            for (int x = 0; x < width; x++)
            {
                int srcOff = x * channels;
                int dstOff = rowBase + x * channels;
                for (int c = 0; c < channels; c++)
                {
                    pixels[dstOff + c] = row[srcOff + c] * (float)Quantum.Scale;
                }
            }
        }

        // Output converged colors
        float[] output = new float[pixels.Length];
        Array.Copy(pixels, output, pixels.Length);

        // Pre-compute squared spatial radius to avoid repeated multiplication
        float spatialRadiusSq = spatialRadius * spatialRadius;

        // Mean-shift iteration per pixel
        Parallel.For(0, height, y =>
        {
            // Reusable buffers per thread — eliminates per-pixel allocations
            var color = new float[channels];
            var sumColor = new float[channels];

            for (int x = 0; x < width; x++)
            {
                float cx = x;
                float cy = y;
                int pixOff = (y * width + x) * channels;
                for (int c = 0; c < channels; c++) color[c] = pixels[pixOff + c];

                for (int iter = 0; iter < maxIterations; iter++)
                {
                    float sumX = 0, sumY = 0;
                    Array.Clear(sumColor);
                    int count = 0;

                    int minY = Math.Max(0, (int)(cy - spatialRadius));
                    int maxY = Math.Min(height - 1, (int)(cy + spatialRadius));
                    int minX = Math.Max(0, (int)(cx - spatialRadius));
                    int maxX = Math.Min(width - 1, (int)(cx + spatialRadius));

                    for (int ny = minY; ny <= maxY; ny++)
                    {
                        for (int nx = minX; nx <= maxX; nx++)
                        {
                            float dx = nx - cx;
                            float dy = ny - cy;
                            if (dx * dx + dy * dy > spatialRadiusSq) continue;

                            int nOff = (ny * width + nx) * channels;
                            float colorDistSq = 0;
                            for (int c = 0; c < channels; c++)
                            {
                                float diff = pixels[nOff + c] - color[c];
                                colorDistSq += diff * diff;
                            }
                            if (colorDistSq > colorRadiusSq) continue;

                            sumX += nx;
                            sumY += ny;
                            for (int c = 0; c < channels; c++)
                            {
                                sumColor[c] += pixels[nOff + c];
                            }
                            count++;
                        }
                    }

                    if (count == 0) break;

                    float invCount = 1f / count;
                    float newCx = sumX * invCount;
                    float newCy = sumY * invCount;

                    // Check convergence (reuse color array after computing shift)
                    float shift = (newCx - cx) * (newCx - cx) + (newCy - cy) * (newCy - cy);
                    for (int c = 0; c < channels; c++)
                    {
                        float newVal = sumColor[c] * invCount;
                        float d = newVal - color[c];
                        shift += d * d;
                        color[c] = newVal;
                    }

                    cx = newCx;
                    cy = newCy;

                    if (shift < 0.001f) break;
                }

                int outOff = (y * width + x) * channels;
                for (int c = 0; c < channels; c++)
                {
                    output[outOff + c] = color[c];
                }
            }
        });

        // Build output image
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < height; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            int rowBase = y * width * channels;
            for (int x = 0; x < width; x++)
            {
                int srcOff = rowBase + x * channels;
                int dstOff = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    row[dstOff + c] = (ushort)Math.Clamp(output[srcOff + c] * Quantum.MaxValue + 0.5f, 0, Quantum.MaxValue);
                }
            }
        }

        return result;
    }
}
