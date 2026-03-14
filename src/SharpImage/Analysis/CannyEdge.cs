using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;

namespace SharpImage.Analysis;

/// <summary>
/// Canny edge detection with Gaussian smoothing, Sobel gradient computation,
/// non-maximum suppression, and double-threshold hysteresis.
/// Produces clean, thin, well-connected edges.
/// </summary>
public static class CannyEdge
{
    /// <summary>
    /// Apply Canny edge detection to the image.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="sigma">Gaussian blur sigma for noise reduction (default 1.4).</param>
    /// <param name="lowThreshold">Low hysteresis threshold, 0..1 range (default 0.1).</param>
    /// <param name="highThreshold">High hysteresis threshold, 0..1 range (default 0.3).</param>
    /// <returns>Binary edge image (white edges on black background).</returns>
    public static ImageFrame Detect(ImageFrame source, double sigma = 1.4, double lowThreshold = 0.1, double highThreshold = 0.3)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Step 1: Convert to grayscale luminance
        float[] gray = ToGrayscale(source, width, height, channels);

        // Step 2: Gaussian blur
        float[] blurred = GaussianBlur1D(gray, width, height, sigma);

        // Step 3: Sobel gradients → magnitude and direction
        float[] magnitude = new float[width * height];
        float[] direction = new float[width * height];
        ComputeGradients(blurred, magnitude, direction, width, height);

        // Step 4: Non-maximum suppression
        float[] suppressed = NonMaximumSuppression(magnitude, direction, width, height);

        // Step 5: Double-threshold + hysteresis
        byte[] edges = Hysteresis(suppressed, width, height, (float)lowThreshold, (float)highThreshold);

        // Build output image
        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0; y < height; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = edges[y * width + x] == 1 ? Quantum.MaxValue : (ushort)0;
                int off = x * 3;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }

        return result;
    }

    private static float[] ToGrayscale(ImageFrame source, int width, int height, int channels)
    {
        float[] gray = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                if (channels >= 3)
                {
                    gray[y * width + x] = 0.2126f * row[off] + 0.7152f * row[off + 1] + 0.0722f * row[off + 2];
                }
                else
                {
                    gray[y * width + x] = row[off];
                }
            }
        }

        // Normalize to 0..1
        float maxVal = Quantum.MaxValue;
        for (int i = 0; i < gray.Length; i++)
        {
            gray[i] /= maxVal;
        }

        return gray;
    }

    private static float[] GaussianBlur1D(float[] input, int width, int height, double sigma)
    {
        int radius = (int)Math.Ceiling(3 * sigma);
        int ksize = radius * 2 + 1;
        float[] kernel = new float[ksize];
        float sum = 0;
        float s2 = (float)(2.0 * sigma * sigma);
        for (int i = 0; i < ksize; i++)
        {
            int d = i - radius;
            kernel[i] = MathF.Exp(-(d * d) / s2);
            sum += kernel[i];
        }
        for (int i = 0; i < ksize; i++) kernel[i] /= sum;

        // Horizontal pass
        float[] temp = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int k = 0; k < ksize; k++)
                {
                    int sx = Math.Clamp(x + k - radius, 0, width - 1);
                    acc += input[y * width + sx] * kernel[k];
                }
                temp[y * width + x] = acc;
            }
        });

        // Vertical pass
        float[] output = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int k = 0; k < ksize; k++)
                {
                    int sy = Math.Clamp(y + k - radius, 0, height - 1);
                    acc += temp[sy * width + x] * kernel[k];
                }
                output[y * width + x] = acc;
            }
        });

        return output;
    }

    private static void ComputeGradients(float[] blurred, float[] magnitude, float[] direction, int width, int height)
    {
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                // Sobel X
                float gx = -blurred[(y - 1) * width + (x - 1)] + blurred[(y - 1) * width + (x + 1)]
                         - 2f * blurred[y * width + (x - 1)] + 2f * blurred[y * width + (x + 1)]
                         - blurred[(y + 1) * width + (x - 1)] + blurred[(y + 1) * width + (x + 1)];

                // Sobel Y
                float gy = -blurred[(y - 1) * width + (x - 1)] - 2f * blurred[(y - 1) * width + x] - blurred[(y - 1) * width + (x + 1)]
                         + blurred[(y + 1) * width + (x - 1)] + 2f * blurred[(y + 1) * width + x] + blurred[(y + 1) * width + (x + 1)];

                magnitude[y * width + x] = MathF.Sqrt(gx * gx + gy * gy);
                direction[y * width + x] = MathF.Atan2(gy, gx);
            }
        });
    }

    private static float[] NonMaximumSuppression(float[] magnitude, float[] direction, int width, int height)
    {
        float[] result = new float[width * height];

        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                float angle = direction[y * width + x];
                // Normalize angle to 0..PI
                if (angle < 0) angle += MathF.PI;

                float mag = magnitude[y * width + x];
                float neighbor1, neighbor2;

                // 4 directions: 0°, 45°, 90°, 135°
                if (angle < MathF.PI / 8 || angle >= 7 * MathF.PI / 8)
                {
                    // Horizontal: compare left/right
                    neighbor1 = magnitude[y * width + (x - 1)];
                    neighbor2 = magnitude[y * width + (x + 1)];
                }
                else if (angle < 3 * MathF.PI / 8)
                {
                    // 45°: compare top-right/bottom-left
                    neighbor1 = magnitude[(y - 1) * width + (x + 1)];
                    neighbor2 = magnitude[(y + 1) * width + (x - 1)];
                }
                else if (angle < 5 * MathF.PI / 8)
                {
                    // Vertical: compare top/bottom
                    neighbor1 = magnitude[(y - 1) * width + x];
                    neighbor2 = magnitude[(y + 1) * width + x];
                }
                else
                {
                    // 135°: compare top-left/bottom-right
                    neighbor1 = magnitude[(y - 1) * width + (x - 1)];
                    neighbor2 = magnitude[(y + 1) * width + (x + 1)];
                }

                result[y * width + x] = (mag >= neighbor1 && mag >= neighbor2) ? mag : 0;
            }
        });

        return result;
    }

    private static byte[] Hysteresis(float[] suppressed, int width, int height, float lowThreshold, float highThreshold)
    {
        // Find max magnitude for threshold scaling
        float maxMag = 0;
        for (int i = 0; i < suppressed.Length; i++)
        {
            if (suppressed[i] > maxMag) maxMag = suppressed[i];
        }

        if (maxMag == 0) return new byte[width * height];

        float high = highThreshold * maxMag;
        float low = lowThreshold * maxMag;

        // 0 = non-edge, 1 = strong, 2 = weak
        byte[] edges = new byte[width * height];
        var strongPixels = new System.Collections.Generic.Queue<int>();

        for (int i = 0; i < suppressed.Length; i++)
        {
            if (suppressed[i] >= high)
            {
                edges[i] = 1;
                strongPixels.Enqueue(i);
            }
            else if (suppressed[i] >= low)
            {
                edges[i] = 2; // weak — may be promoted
            }
        }

        // BFS: promote weak pixels connected to strong pixels
        int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        while (strongPixels.Count > 0)
        {
            int idx = strongPixels.Dequeue();
            int cy = idx / width;
            int cx = idx % width;

            for (int d = 0; d < 8; d++)
            {
                int nx = cx + dx[d];
                int ny = cy + dy[d];
                if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                {
                    int nIdx = ny * width + nx;
                    if (edges[nIdx] == 2)
                    {
                        edges[nIdx] = 1;
                        strongPixels.Enqueue(nIdx);
                    }
                }
            }
        }

        // Remove remaining weak edges
        for (int i = 0; i < edges.Length; i++)
        {
            if (edges[i] == 2) edges[i] = 0;
        }

        return edges;
    }
}
