// SharpImage — Additional blur and noise operations.
// MotionBlur, RadialBlur, SelectiveBlur, AddNoise, Despeckle, WaveletDenoise,
// MedianFilter, BilateralBlur, KuwaharaFilter.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Noise type for AddNoise operation. Matches ImageMagick's noise types.
/// </summary>
public enum NoiseType
{
    Uniform,
    Gaussian,
    Impulse,
    Laplacian,
    MultiplicativeGaussian,
    Poisson,
    Random
}

/// <summary>
/// Advanced blur and noise operations: motion blur, radial blur, selective blur,
/// noise generation, despeckle, and wavelet denoise. All operations return new ImageFrame
/// instances and preserve alpha channels.
/// </summary>
public static class BlurNoiseOps
{
    // ─── Motion Blur ─────────────────────────────────────────────

    /// <summary>
    /// Applies directional motion blur along a given angle.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Blur length in pixels.</param>
    /// <param name="sigma">Gaussian sigma for the 1D kernel along the motion direction.</param>
    /// <param name="angle">Direction angle in degrees (0 = horizontal right, 90 = vertical down).</param>
    public static ImageFrame MotionBlur(ImageFrame source, int radius, double sigma, double angle)
    {
        if (radius < 1) throw new ArgumentOutOfRangeException(nameof(radius));
        if (sigma <= 0) throw new ArgumentOutOfRangeException(nameof(sigma));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int kernelSize = radius * 2 + 1;

        // Build 1D Gaussian kernel
        float[] kernel = BuildGaussian1D(sigma, kernelSize);

        // Compute directional offsets
        double radians = angle * Math.PI / 180.0;
        double cosA = Math.Cos(radians);
        double sinA = Math.Sin(radians);

        var offsetsX = new double[kernelSize];
        var offsetsY = new double[kernelSize];
        for (int i = 0; i < kernelSize; i++)
        {
            double t = i - radius;
            offsetsX[i] = t * cosA;
            offsetsY[i] = t * sinA;
        }

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            Span<float> accum = stackalloc float[channels];

            for (int x = 0; x < width; x++)
            {
                int dstOff = x * channels;
                accum.Clear();
                float weightSum = 0f;

                for (int k = 0; k < kernelSize; k++)
                {
                    int sx = (int)Math.Round(x + offsetsX[k]);
                    int sy = (int)Math.Round(y + offsetsY[k]);

                    if (sx < 0 || sx >= width || sy < 0 || sy >= height)
                        continue;

                    var srcRow = source.GetPixelRow(sy);
                    int srcOff = sx * channels;
                    float w = kernel[k];
                    weightSum += w;

                    for (int c = 0; c < channels; c++)
                        accum[c] += srcRow[srcOff + c] * w;
                }

                if (weightSum > 0)
                {
                    float invWeight = 1f / weightSum;
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = (ushort)Math.Clamp(accum[c] * invWeight + 0.5f, 0, Quantum.MaxValue);
                }
            }
        });

        return result;
    }

    // ─── Radial Blur ─────────────────────────────────────────────

    /// <summary>
    /// Applies rotational (radial/spin) blur around the image center.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="angle">Blur angle in degrees. Higher values produce more spinning.</param>
    public static ImageFrame RadialBlur(ImageFrame source, double angle)
    {
        if (angle <= 0) throw new ArgumentOutOfRangeException(nameof(angle));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        double centerX = width / 2.0;
        double centerY = height / 2.0;
        double maxRadius = Math.Sqrt(centerX * centerX + centerY * centerY);

        // Number of samples scales with angle and distance
        int numSamples = Math.Max(3, (int)(2.0 * angle * Math.Sqrt(maxRadius)) + 1);
        if (numSamples % 2 == 0) numSamples++;

        double thetaStep = (angle * Math.PI / 180.0) / (numSamples - 1);

        // Precompute rotation tables
        var cosTheta = new double[numSamples];
        var sinTheta = new double[numSamples];
        for (int i = 0; i < numSamples; i++)
        {
            double t = (i - numSamples / 2) * thetaStep;
            cosTheta[i] = Math.Cos(t);
            sinTheta[i] = Math.Sin(t);
        }

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            Span<float> accum = stackalloc float[channels];

            for (int x = 0; x < width; x++)
            {
                int dstOff = x * channels;
                double dx = x - centerX;
                double dy = y - centerY;

                accum.Clear();
                int count = 0;

                for (int s = 0; s < numSamples; s++)
                {
                    double rx = dx * cosTheta[s] - dy * sinTheta[s] + centerX;
                    double ry = dx * sinTheta[s] + dy * cosTheta[s] + centerY;

                    int sx = (int)Math.Round(rx);
                    int sy = (int)Math.Round(ry);

                    if (sx < 0 || sx >= width || sy < 0 || sy >= height)
                        continue;

                    var srcRow = source.GetPixelRow(sy);
                    int srcOff = sx * channels;
                    count++;

                    for (int c = 0; c < channels; c++)
                        accum[c] += srcRow[srcOff + c];
                }

                if (count > 0)
                {
                    float inv = 1f / count;
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = (ushort)Math.Clamp(accum[c] * inv + 0.5f, 0, Quantum.MaxValue);
                }
                else
                {
                    var srcRow = source.GetPixelRow(y);
                    int srcOff = x * channels;
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = srcRow[srcOff + c];
                }
            }
        });

        return result;
    }

    // ─── Selective Blur ──────────────────────────────────────────

    /// <summary>
    /// Blurs only pixels whose neighbors are within a contrast threshold.
    /// Preserves hard edges while smoothing gradual transitions.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Blur radius in pixels.</param>
    /// <param name="sigma">Gaussian sigma.</param>
    /// <param name="threshold">Contrast threshold (0.0-1.0). Only neighbors within this range are included.</param>
    public static ImageFrame SelectiveBlur(ImageFrame source, int radius, double sigma, double threshold)
    {
        if (radius < 1) throw new ArgumentOutOfRangeException(nameof(radius));
        if (sigma <= 0) throw new ArgumentOutOfRangeException(nameof(sigma));
        if (threshold < 0 || threshold > 1) throw new ArgumentOutOfRangeException(nameof(threshold));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        // Build 2D Gaussian kernel
        int kernelSize = radius * 2 + 1;
        float[,] kernel = Build2DGaussian(sigma, kernelSize);
        ushort thresholdQ = (ushort)(threshold * Quantum.MaxValue);

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            var centerRow = source.GetPixelRow(y);
            Span<float> accum = stackalloc float[channels];

            for (int x = 0; x < width; x++)
            {
                int dstOff = x * channels;
                int centerOff = x * channels;

                // Compute intensity of center pixel
                int centerIntensity = 0;
                for (int c = 0; c < colorChannels; c++)
                    centerIntensity += centerRow[centerOff + c];
                centerIntensity /= colorChannels;

                accum.Clear();
                float gamma = 0f;

                for (int ky = -radius; ky <= radius; ky++)
                {
                    int sy = Math.Clamp(y + ky, 0, height - 1);
                    var srcRow = source.GetPixelRow(sy);

                    for (int kx = -radius; kx <= radius; kx++)
                    {
                        int sx = Math.Clamp(x + kx, 0, width - 1);
                        int srcOff = sx * channels;

                        // Compute intensity of neighbor
                        int neighborIntensity = 0;
                        for (int c = 0; c < colorChannels; c++)
                            neighborIntensity += srcRow[srcOff + c];
                        neighborIntensity /= colorChannels;

                        int contrast = Math.Abs(centerIntensity - neighborIntensity);
                        if (contrast < thresholdQ)
                        {
                            float w = kernel[ky + radius, kx + radius];
                            gamma += w;
                            for (int c = 0; c < channels; c++)
                                accum[c] += srcRow[srcOff + c] * w;
                        }
                    }
                }

                if (gamma > 0)
                {
                    float invGamma = 1f / gamma;
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = (ushort)Math.Clamp(accum[c] * invGamma + 0.5f, 0, Quantum.MaxValue);
                }
                else
                {
                    // No neighbors within threshold — copy original
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = centerRow[centerOff + c];
                }
            }
        });

        return result;
    }

    // ─── Add Noise ───────────────────────────────────────────────

    /// <summary>
    /// Adds noise of the specified type to the image.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="noiseType">Type of noise to add.</param>
    /// <param name="attenuate">Noise strength multiplier (default 1.0). Higher = more noise.</param>
    public static ImageFrame AddNoise(ImageFrame source, NoiseType noiseType, double attenuate = 1.0)
    {
        if (attenuate < 0) throw new ArgumentOutOfRangeException(nameof(attenuate));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, () => new Random(Thread.CurrentThread.ManagedThreadId + Environment.TickCount),
            (y, _, rng) =>
            {
                var srcRow = source.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;

                    for (int c = 0; c < colorChannels; c++)
                    {
                        double pixel = srcRow[off + c];
                        double noise = GenerateNoise(rng, noiseType, pixel, attenuate);
                        dstRow[off + c] = (ushort)Math.Clamp(noise + 0.5, 0, Quantum.MaxValue);
                    }

                    // Preserve alpha
                    if (source.HasAlpha)
                        dstRow[off + colorChannels] = srcRow[off + colorChannels];
                }

                return rng;
            },
            _ => { });

        return result;
    }

    /// <summary>
    /// Generates a noisy pixel value based on noise type. Follows ImageMagick's noise formulas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GenerateNoise(Random rng, NoiseType type, double pixel, double attenuate)
    {
        double alpha, beta;

        switch (type)
        {
            case NoiseType.Uniform:
                alpha = rng.NextDouble();
                return pixel + Quantum.MaxValue * attenuate * (alpha - 0.5);

            case NoiseType.Gaussian:
                // Box-Muller transform
                alpha = Math.Max(rng.NextDouble(), 1e-10);
                beta = rng.NextDouble();
                double gaussian = Math.Sqrt(-2.0 * Math.Log(alpha)) * Math.Cos(2.0 * Math.PI * beta);
                return pixel + Quantum.MaxValue * attenuate * 0.025 * gaussian;

            case NoiseType.Impulse:
                // Salt-and-pepper
                alpha = rng.NextDouble();
                if (alpha < attenuate * 0.04)
                    return 0;
                if (alpha > 1.0 - attenuate * 0.04)
                    return Quantum.MaxValue;
                return pixel;

            case NoiseType.Laplacian:
                alpha = Math.Max(rng.NextDouble(), 1e-10);
                if (alpha <= 0.5)
                    return pixel - Quantum.MaxValue * attenuate * 0.04 * Math.Log(2.0 * alpha);
                else
                    return pixel + Quantum.MaxValue * attenuate * 0.04 * Math.Log(2.0 * (1.0 - alpha));

            case NoiseType.MultiplicativeGaussian:
                alpha = Math.Max(rng.NextDouble(), 1e-10);
                beta = rng.NextDouble();
                double mulNoise = Math.Sqrt(-2.0 * Math.Log(alpha)) * Math.Cos(2.0 * Math.PI * beta);
                return pixel * (1.0 + attenuate * 0.04 * mulNoise);

            case NoiseType.Poisson:
                double lambda = pixel * Quantum.Scale * attenuate;
                if (lambda < 30)
                {
                    // Direct method for small lambda
                    double expLambda = Math.Exp(-lambda);
                    double p = 1.0;
                    int k = 0;
                    do
                    {
                        k++;
                        p *= rng.NextDouble();
                    } while (p > expLambda && k < 1000);
                    return (k - 1) / (Quantum.Scale * attenuate);
                }
                else
                {
                    // Normal approximation for large lambda
                    alpha = Math.Max(rng.NextDouble(), 1e-10);
                    beta = rng.NextDouble();
                    double approx = lambda + Math.Sqrt(lambda) * Math.Sqrt(-2.0 * Math.Log(alpha)) * Math.Cos(2.0 * Math.PI * beta);
                    return Math.Max(0, approx) / (Quantum.Scale * attenuate);
                }

            case NoiseType.Random:
                alpha = rng.NextDouble();
                return Quantum.MaxValue * alpha;

            default:
                return pixel;
        }
    }

    // ─── Despeckle ───────────────────────────────────────────────

    /// <summary>
    /// Removes speckle noise using Crimmins complementary hulling.
    /// Reduces impulsive noise while preserving edges better than simple median filtering.
    /// </summary>
    public static ImageFrame Despeckle(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // Process each channel independently
        int paddedW = width + 2;
        int paddedH = height + 2;
        int paddedLen = paddedW * paddedH;

        for (int c = 0; c < channels; c++)
        {
            // Extract channel into padded buffer (1-pixel border of replicated edge)
            var pixels = ArrayPool<ushort>.Shared.Rent(paddedLen);
            var buffer = ArrayPool<ushort>.Shared.Rent(paddedLen);

            try
            {
                Array.Clear(pixels, 0, paddedLen);

                // Fill interior
                for (int y = 0; y < height; y++)
                {
                    var srcRow = source.GetPixelRow(y);
                    int rowBase = (y + 1) * paddedW + 1;
                    for (int x = 0; x < width; x++)
                        pixels[rowBase + x] = srcRow[x * channels + c];
                }

                // Replicate edges
                ReplicateEdges(pixels, paddedW, paddedH, width, height);

                // Copy to buffer
                Array.Copy(pixels, buffer, paddedLen);

                // Four direction passes with complementary hulling
                // Directions: horizontal(0,1), vertical(1,0), diagonal(1,1), anti-diagonal(-1,1)
                int[][] directions = [
                    [0, 1],
                    [1, 0],
                    [1, 1],
                    [-1, 1]
                ];

                foreach (var dir in directions)
                {
                    int dy = dir[0];
                    int dx = dir[1];
                    int offset = dy * paddedW + dx;

                    // Raise (fill dark speckles)
                    HullPass(pixels, buffer, paddedW, paddedH, offset, 1);
                    HullPass(buffer, pixels, paddedW, paddedH, offset, 1);
                    HullPass(pixels, buffer, paddedW, paddedH, -offset, 1);
                    HullPass(buffer, pixels, paddedW, paddedH, -offset, 1);

                    // Lower (fill bright speckles)
                    HullPass(pixels, buffer, paddedW, paddedH, -offset, -1);
                    HullPass(buffer, pixels, paddedW, paddedH, -offset, -1);
                    HullPass(pixels, buffer, paddedW, paddedH, offset, -1);
                    HullPass(buffer, pixels, paddedW, paddedH, offset, -1);
                }

                // Extract result from padded buffer
                for (int y = 0; y < height; y++)
                {
                    var dstRow = result.GetPixelRowForWrite(y);
                    int rowBase = (y + 1) * paddedW + 1;
                    for (int x = 0; x < width; x++)
                        dstRow[x * channels + c] = pixels[rowBase + x];
                }
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(pixels);
                ArrayPool<ushort>.Shared.Return(buffer);
            }
        }

        return result;
    }

    /// <summary>
    /// Replicates edge pixels into the 1-pixel border of a padded buffer.
    /// </summary>
    private static void ReplicateEdges(ushort[] buf, int pw, int ph, int w, int h)
    {
        // Top and bottom rows
        for (int x = 0; x < w; x++)
        {
            buf[x + 1] = buf[pw + x + 1]; // top edge
            buf[(ph - 1) * pw + x + 1] = buf[(ph - 2) * pw + x + 1]; // bottom
        }

        // Left and right columns
        for (int y = 0; y < ph; y++)
        {
            buf[y * pw] = buf[y * pw + 1]; // left edge
            buf[y * pw + pw - 1] = buf[y * pw + pw - 2]; // right
        }
    }

    /// <summary>
    /// Crimmins hull pass — raises or lowers speckles in one direction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HullPass(ushort[] src, ushort[] dst, int pw, int ph, int offset, int polarity)
    {
        // Unit step: ScaleCharToQuantum(1) ≈ 257 for 16-bit
        const int step = 257;

        for (int i = pw + 1; i < (ph - 1) * pw - 1; i++)
        {
            int neighbor = src[i + offset];
            int pixel = src[i];

            if (polarity > 0)
            {
                // Raise: if neighbor is brighter by at least 2 steps, nudge pixel up
                if (neighbor >= pixel + step * 2)
                    dst[i] = (ushort)Math.Min(pixel + step, Quantum.MaxValue);
                else
                    dst[i] = src[i];
            }
            else
            {
                // Lower: if neighbor is darker by at least 2 steps, nudge pixel down
                if (neighbor <= pixel - step * 2)
                    dst[i] = (ushort)Math.Max(pixel - step, 0);
                else
                    dst[i] = src[i];
            }
        }
    }

    // ─── Wavelet Denoise ─────────────────────────────────────────

    /// <summary>
    /// Multi-scale wavelet-based noise reduction. Decomposes the image into frequency bands
    /// using a hat transform and soft-thresholds detail coefficients.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="threshold">Noise threshold (0.0-1.0). Higher = more denoising.</param>
    /// <param name="softness">Attenuation softness (0.0-1.0, default 0.0). 0=hard threshold, 1=gradual.</param>
    public static ImageFrame WaveletDenoise(ImageFrame source, double threshold, double softness = 0.0)
    {
        if (threshold < 0 || threshold > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        softness = Math.Clamp(softness, 0.0, 1.0);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        // Noise level scaling factors per decomposition level (empirical, from IM)
        ReadOnlySpan<double> noiseLevels = [0.8002, 0.2735, 0.1202, 0.0585, 0.0291];
        const int numLevels = 5;

        double thresholdQ = threshold * Quantum.MaxValue;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // Copy alpha channel directly if present
        if (source.HasAlpha)
        {
            for (int y = 0; y < height; y++)
            {
                var srcRow = source.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                    dstRow[x * channels + colorChannels] = srcRow[x * channels + colorChannels];
            }
        }

        // Process each color channel independently
        int pixelCount = width * height;
        var channel = ArrayPool<float>.Shared.Rent(pixelCount);
        var lowpass = ArrayPool<float>.Shared.Rent(pixelCount);
        var temp = ArrayPool<float>.Shared.Rent(pixelCount);

        try
        {
            for (int c = 0; c < colorChannels; c++)
            {
                // Extract channel
                for (int y = 0; y < height; y++)
                {
                    var srcRow = source.GetPixelRow(y);
                    for (int x = 0; x < width; x++)
                        channel[y * width + x] = srcRow[x * channels + c];
                }

                // Multi-level wavelet decomposition and thresholding
                Array.Copy(channel, 0, lowpass, 0, pixelCount);

                for (int level = 0; level < numLevels; level++)
                {
                    int scale = 1 << level;
                    double noiseLevel = level < noiseLevels.Length ? noiseLevels[level] : noiseLevels[^1];
                    double levelThreshold = thresholdQ * noiseLevel;

                    // Hat transform: lowpass filter (horizontal then vertical)
                    HatTransformRows(lowpass, temp, width, height, scale);
                    HatTransformCols(temp, lowpass, width, height, scale);

                    // Now lowpass has the smoothed version. Detail = channel - lowpass.
                    // Soft-threshold the detail coefficients and reconstruct.
                    Parallel.For(0, pixelCount, i =>
                    {
                        float detail = channel[i] - lowpass[i];
                        float absDetail = Math.Abs(detail);

                        if (absDetail <= levelThreshold)
                        {
                            // Below threshold: attenuate based on softness
                            if (softness > 0)
                                detail *= (float)(softness * absDetail / levelThreshold);
                            else
                                detail = 0;
                        }
                        else if (softness > 0)
                        {
                            // Above threshold with softness: gradual transition
                            float excess = absDetail - (float)levelThreshold;
                            float scale2 = excess / absDetail;
                            detail *= (float)(scale2 + (1.0 - scale2) * softness);
                        }

                        channel[i] = lowpass[i] + detail;
                    });
                }

                // Write denoised channel back
                for (int y = 0; y < height; y++)
                {
                    var dstRow = result.GetPixelRowForWrite(y);
                    for (int x = 0; x < width; x++)
                        dstRow[x * channels + c] = (ushort)Math.Clamp(channel[y * width + x] + 0.5f, 0, Quantum.MaxValue);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(channel);
            ArrayPool<float>.Shared.Return(lowpass);
            ArrayPool<float>.Shared.Return(temp);
        }

        return result;
    }

    /// <summary>
    /// Hat transform (lowpass) applied to rows: out[i] = 0.25*(in[i-scale] + 2*in[i] + in[i+scale])
    /// </summary>
    private static void HatTransformRows(float[] input, float[] output, int width, int height, int scale)
    {
        Parallel.For(0, height, y =>
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int left = Math.Clamp(x - scale, 0, width - 1);
                int right = Math.Clamp(x + scale, 0, width - 1);
                output[rowBase + x] = 0.25f * (input[rowBase + left] + 2f * input[rowBase + x] + input[rowBase + right]);
            }
        });
    }

    /// <summary>
    /// Hat transform (lowpass) applied to columns.
    /// </summary>
    private static void HatTransformCols(float[] input, float[] output, int width, int height, int scale)
    {
        Parallel.For(0, width, x =>
        {
            for (int y = 0; y < height; y++)
            {
                int top = Math.Clamp(y - scale, 0, height - 1);
                int bottom = Math.Clamp(y + scale, 0, height - 1);
                output[y * width + x] = 0.25f * (input[top * width + x] + 2f * input[y * width + x] + input[bottom * width + x]);
            }
        });
    }

    // ─── Kernel Helpers ──────────────────────────────────────────

    /// <summary>
    /// Builds a normalized 1D Gaussian kernel.
    /// </summary>
    private static float[] BuildGaussian1D(double sigma, int size)
    {
        float[] kernel = new float[size];
        int radius = size / 2;
        double twoSigmaSq = 2.0 * sigma * sigma;
        double sum = 0;

        for (int i = 0; i < size; i++)
        {
            double x = i - radius;
            kernel[i] = (float)Math.Exp(-(x * x) / twoSigmaSq);
            sum += kernel[i];
        }

        float invSum = (float)(1.0 / sum);
        for (int i = 0; i < size; i++)
            kernel[i] *= invSum;

        return kernel;
    }

    /// <summary>
    /// Builds a normalized 2D Gaussian kernel.
    /// </summary>
    private static float[,] Build2DGaussian(double sigma, int size)
    {
        float[,] kernel = new float[size, size];
        int radius = size / 2;
        double twoSigmaSq = 2.0 * sigma * sigma;
        double sum = 0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - radius;
                double dy = y - radius;
                kernel[y, x] = (float)Math.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
                sum += kernel[y, x];
            }
        }

        float invSum = (float)(1.0 / sum);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                kernel[y, x] *= invSum;

        return kernel;
    }

    // ─── Spread (Pixel Scatter) ──────────────────────────────────

    /// <summary>
    /// Scatters each pixel to a random position within the given radius, producing a grainy dissolve effect.
    /// Follows ImageMagick's SpreadImage: for each output pixel, sample a random source pixel within radius.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Maximum displacement in pixels. Larger values produce more scatter.</param>
    public static ImageFrame Spread(ImageFrame source, double radius)
    {
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (radius < 0.5) return source.Clone();

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, () => new Random(Thread.CurrentThread.ManagedThreadId + Environment.TickCount),
            (y, _, rng) =>
            {
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0; x < width; x++)
                {
                    // Random displacement within [-radius, +radius]
                    double dx = radius * (rng.NextDouble() * 2.0 - 1.0);
                    double dy = radius * (rng.NextDouble() * 2.0 - 1.0);

                    // Clamp source coordinates to image bounds
                    int srcX = Math.Clamp((int)Math.Round(x + dx), 0, width - 1);
                    int srcY = Math.Clamp((int)Math.Round(y + dy), 0, height - 1);

                    var srcRow = source.GetPixelRow(srcY);
                    int srcOff = srcX * channels;
                    int dstOff = x * channels;

                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = srcRow[srcOff + c];
                }

                return rng;
            },
            _ => { });

        return result;
    }

    /// <summary>
    /// Median filter — replaces each pixel with the median of its NxN neighborhood.
    /// Excellent at removing salt-and-pepper noise while preserving edges.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Half-width of the neighborhood (1 = 3×3, 2 = 5×5, etc.).</param>
    public static ImageFrame MedianFilter(ImageFrame source, int radius = 1)
    {
        if (radius < 1) radius = 1;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int kernelSize = 2 * radius + 1;
        int windowArea = kernelSize * kernelSize;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, () => new ushort[windowArea],
            (y, _, buffer) =>
            {
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        int count = 0;
                        for (int ky = -radius; ky <= radius; ky++)
                        {
                            int sy = Math.Clamp(y + ky, 0, height - 1);
                            var srcRow = source.GetPixelRow(sy);
                            for (int kx = -radius; kx <= radius; kx++)
                            {
                                int sx = Math.Clamp(x + kx, 0, width - 1);
                                buffer[count++] = srcRow[sx * channels + c];
                            }
                        }

                        Array.Sort(buffer, 0, count);
                        dstRow[x * channels + c] = buffer[count / 2];
                    }
                }

                return buffer;
            },
            _ => { });

        return result;
    }

    /// <summary>
    /// Bilateral blur — edge-preserving smoothing that blurs similar colors while keeping edges sharp.
    /// Combines spatial proximity weighting with color similarity weighting.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Spatial radius of the filter kernel.</param>
    /// <param name="spatialSigma">Spatial gaussian sigma (controls blur distance).</param>
    /// <param name="rangeSigma">Range gaussian sigma (controls color sensitivity — lower = stronger edge preservation).</param>
    public static ImageFrame BilateralBlur(ImageFrame source, int radius = 3,
        double spatialSigma = 1.5, double rangeSigma = 50.0)
    {
        if (radius < 1) radius = 1;
        if (spatialSigma <= 0) spatialSigma = radius / 2.0;
        if (rangeSigma <= 0) rangeSigma = 50.0;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int kernelSize = 2 * radius + 1;

        // Pre-compute spatial gaussian weights
        double[] spatialWeights = new double[kernelSize * kernelSize];
        double spatialDenom = -2.0 * spatialSigma * spatialSigma;
        for (int ky = 0; ky < kernelSize; ky++)
        {
            int dy = ky - radius;
            for (int kx = 0; kx < kernelSize; kx++)
            {
                int dx = kx - radius;
                spatialWeights[ky * kernelSize + kx] = Math.Exp((dx * dx + dy * dy) / spatialDenom);
            }
        }

        // Pre-compute range gaussian LUT (intensity differences 0..65535)
        // Quantize to 256 buckets for cache efficiency
        const int rangeLutSize = 256;
        double[] rangeLut = new double[rangeLutSize];
        double rangeDenom = -2.0 * rangeSigma * rangeSigma;
        double rangeScale = (rangeLutSize - 1) * Quantum.Scale;
        for (int i = 0; i < rangeLutSize; i++)
        {
            double diff = i / rangeScale;
            rangeLut[i] = Math.Exp((diff * diff) / rangeDenom);
        }

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            var centerRow = source.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                int centerOff = x * channels;

                for (int c = 0; c < colorChannels; c++)
                {
                    double centerVal = centerRow[centerOff + c];
                    double weightedSum = 0;
                    double weightSum = 0;

                    for (int ky = -radius; ky <= radius; ky++)
                    {
                        int sy = Math.Clamp(y + ky, 0, height - 1);
                        var srcRow = source.GetPixelRow(sy);

                        for (int kx = -radius; kx <= radius; kx++)
                        {
                            int sx = Math.Clamp(x + kx, 0, width - 1);
                            double neighborVal = srcRow[sx * channels + c];

                            double spatialW = spatialWeights[(ky + radius) * kernelSize + (kx + radius)];
                            double diff = Math.Abs(centerVal - neighborVal);
                            int lutIdx = Math.Min((int)(diff * rangeScale), rangeLutSize - 1);
                            double rangeW = rangeLut[lutIdx];

                            double w = spatialW * rangeW;
                            weightedSum += w * neighborVal;
                            weightSum += w;
                        }
                    }

                    dstRow[centerOff + c] = (ushort)Math.Clamp(
                        weightSum > 0 ? weightedSum / weightSum : centerVal, 0, Quantum.MaxValue);
                }

                // Preserve alpha unchanged
                if (source.HasAlpha)
                    dstRow[centerOff + colorChannels] = centerRow[centerOff + colorChannels];
            }
        });

        return result;
    }

    /// <summary>
    /// Kuwahara filter — edge-preserving smoothing that creates a painterly effect.
    /// Splits each pixel's neighborhood into 4 quadrants, selects the one with minimum
    /// variance, and outputs its mean color.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Half-width of the neighborhood (1 = 3×3, 2 = 5×5, etc.).</param>
    public static ImageFrame KuwaharaFilter(ImageFrame source, int radius = 2)
    {
        if (radius < 1) radius = 1;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;
        int quadrantSize = (radius + 1) * (radius + 1);

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // Rec709 luma coefficients for variance computation
        const double lumaR = 0.212656;
        const double lumaG = 0.715158;
        const double lumaB = 0.072186;

        Parallel.For(0, height, () => (meanBuf: new double[Math.Max(colorChannels, 3)], quadMeans: new double[4 * colorChannels]),
            (y, _, buffers) =>
            {
                var dstRow = result.GetPixelRowForWrite(y);
                var meanBuf = buffers.meanBuf;
                var quadMeans = buffers.quadMeans;

                for (int x = 0; x < width; x++)
                {
                    double bestVariance = double.MaxValue;
                    int bestQuadrant = 0;

                    // Reset quadrant means
                    Array.Clear(quadMeans);

                    // For each of 4 quadrants: (0=top-left, 1=top-right, 2=bottom-left, 3=bottom-right)
                    for (int q = 0; q < 4; q++)
                    {
                        int startY = (q < 2) ? -radius : 0;
                        int endY = (q < 2) ? 0 : radius;
                        int startX = (q == 0 || q == 2) ? -radius : 0;
                        int endX = (q == 0 || q == 2) ? 0 : radius;

                        // Compute mean
                        for (int c = 0; c < colorChannels; c++)
                            meanBuf[c] = 0;
                        int count = 0;

                        for (int ky = startY; ky <= endY; ky++)
                        {
                            int sy = Math.Clamp(y + ky, 0, height - 1);
                            var srcRow = source.GetPixelRow(sy);
                            for (int kx = startX; kx <= endX; kx++)
                            {
                                int sx = Math.Clamp(x + kx, 0, width - 1);
                                int off = sx * channels;
                                for (int c = 0; c < colorChannels; c++)
                                    meanBuf[c] += srcRow[off + c];
                                count++;
                            }
                        }

                        double invCount = 1.0 / count;
                        for (int c = 0; c < colorChannels; c++)
                        {
                            meanBuf[c] *= invCount;
                            quadMeans[q * colorChannels + c] = meanBuf[c];
                        }

                        // Compute variance (based on luminance)
                        double meanLuma = colorChannels >= 3
                            ? lumaR * meanBuf[0] + lumaG * meanBuf[1] + lumaB * meanBuf[2]
                            : meanBuf[0];
                        double variance = 0;

                        for (int ky = startY; ky <= endY; ky++)
                        {
                            int sy = Math.Clamp(y + ky, 0, height - 1);
                            var srcRow = source.GetPixelRow(sy);
                            for (int kx = startX; kx <= endX; kx++)
                            {
                                int sx = Math.Clamp(x + kx, 0, width - 1);
                                int off = sx * channels;
                                double luma = colorChannels >= 3
                                    ? lumaR * srcRow[off] + lumaG * srcRow[off + 1] + lumaB * srcRow[off + 2]
                                    : srcRow[off];
                                double diff = luma - meanLuma;
                                variance += diff * diff;
                            }
                        }

                        if (variance < bestVariance)
                        {
                            bestVariance = variance;
                            bestQuadrant = q;
                        }
                    }

                    // Output the mean of the best quadrant
                    int dstOff = x * channels;
                    for (int c = 0; c < colorChannels; c++)
                        dstRow[dstOff + c] = (ushort)Math.Clamp(quadMeans[bestQuadrant * colorChannels + c], 0, Quantum.MaxValue);

                    // Preserve alpha
                    if (source.HasAlpha)
                        dstRow[dstOff + colorChannels] = source.GetPixelRow(y)[x * channels + colorChannels];
                }

                return buffers;
            },
            _ => { });

        return result;
    }

    // ─── Mode Filter ─────────────────────────────────────────────

    /// <summary>
    /// Mode filter — replaces each pixel with the most frequently occurring value
    /// in its NxN neighborhood. Good at removing impulse noise while preserving
    /// uniform regions better than median filter.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Half-width of the neighborhood (1 = 3×3, 2 = 5×5, etc.).</param>
    public static ImageFrame ModeFilter(ImageFrame source, int radius = 1)
    {
        if (radius < 1) radius = 1;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int kernelSize = 2 * radius + 1;
        int windowArea = kernelSize * kernelSize;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, () => new ushort[windowArea],
            (y, _, buffer) =>
            {
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        int count = 0;
                        for (int ky = -radius; ky <= radius; ky++)
                        {
                            int sy = Math.Clamp(y + ky, 0, height - 1);
                            var srcRow = source.GetPixelRow(sy);
                            for (int kx = -radius; kx <= radius; kx++)
                            {
                                int sx = Math.Clamp(x + kx, 0, width - 1);
                                buffer[count++] = srcRow[sx * channels + c];
                            }
                        }

                        // Sort to find runs of identical values
                        Array.Sort(buffer, 0, count);

                        // Find the value with the longest run
                        ushort modeVal = buffer[0];
                        int maxRun = 1;
                        int currentRun = 1;

                        for (int i = 1; i < count; i++)
                        {
                            if (buffer[i] == buffer[i - 1])
                            {
                                currentRun++;
                            }
                            else
                            {
                                if (currentRun > maxRun)
                                {
                                    maxRun = currentRun;
                                    modeVal = buffer[i - 1];
                                }
                                currentRun = 1;
                            }
                        }
                        if (currentRun > maxRun)
                            modeVal = buffer[count - 1];

                        dstRow[x * channels + c] = modeVal;
                    }
                }

                return buffer;
            },
            _ => { });

        return result;
    }
}
