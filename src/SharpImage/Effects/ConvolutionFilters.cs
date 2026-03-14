using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Convolution-based image filters: blur, sharpen, edge detection, emboss. Uses separable kernels where possible for
/// O(n) vs O(n²) per pixel. Parallelized across scanlines for multi-core utilization.
/// </summary>
public static class ConvolutionFilters
{
    /// <summary>
    /// Applies a Gaussian blur with the given radius (sigma).
    /// </summary>
    public static ImageFrame GaussianBlur(ImageFrame source, double sigma = 1.0)
    {
        if (sigma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma));
        }

        float[] kernel = BuildGaussianKernel(sigma);
        return ApplySeparableKernel(source, kernel);
    }

    /// <summary>
    /// Applies a box blur (average filter) with the given radius.
    /// </summary>
    public static ImageFrame BoxBlur(ImageFrame source, int radius = 1)
    {
        if (radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }

        int size = radius * 2 + 1;
        float[] kernel = new float[size];
        float val = 1.0f / size;
        Array.Fill(kernel, val);

        return ApplySeparableKernel(source, kernel);
    }

    /// <summary>
    /// Sharpens the image using unsharp masking. Result = Original + amount * (Original - Blurred) Uses SIMD-
    /// accelerated unsharp mask operation.
    /// </summary>
    public static ImageFrame Sharpen(ImageFrame source, double sigma = 1.0, double amount = 1.0)
    {
        using var blurred = GaussianBlur(source, sigma);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int rowLength = width * channels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var blurRow = blurred.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            SimdOps.UnsharpMask(srcRow, blurRow, dstRow, (float)amount, rowLength);
        });

        return result;
    }

    /// <summary>
    /// Unsharp mask sharpening with threshold control. Applies the classic formula:
    /// result = original + amount * (original - blurred), but only where the difference
    /// exceeds the threshold, preventing noise amplification in smooth areas.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="sigma">Gaussian blur sigma (controls sharpening radius).</param>
    /// <param name="amount">Sharpening strength multiplier (gain). Typical range 0.5–3.0.</param>
    /// <param name="threshold">Minimum difference (0–1 normalized) required to apply sharpening. 0 = sharpen everything.</param>
    public static ImageFrame UnsharpMask(ImageFrame source, double sigma = 1.0, double amount = 1.0, double threshold = 0.0)
    {
        if (sigma <= 0) throw new ArgumentOutOfRangeException(nameof(sigma));

        using var blurred = GaussianBlur(source, sigma);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;
        double quantumThreshold = threshold * Quantum.MaxValue;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var blurRow = blurred.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                for (int c = 0; c < colorChannels; c++)
                {
                    double original = srcRow[off + c];
                    double blur = blurRow[off + c];
                    double diff = original - blur;

                    // Only sharpen where difference exceeds threshold (IM uses |2*diff| < threshold)
                    if (Math.Abs(2.0 * diff) < quantumThreshold)
                        dstRow[off + c] = (ushort)original;
                    else
                        dstRow[off + c] = (ushort)Math.Clamp(original + amount * diff, 0, Quantum.MaxValue);
                }

                // Preserve alpha
                if (source.HasAlpha)
                    dstRow[off + colorChannels] = srcRow[off + colorChannels];
            }
        });

        return result;
    }

    /// <summary>
    /// Applies a 3x3 convolution kernel (edge detect, emboss, etc.).
    /// </summary>
    public static ImageFrame ApplyKernel3x3(ImageFrame source, float[,] kernel)
    {
        if (kernel.GetLength(0) != 3 || kernel.GetLength(1) != 3)
        {
            throw new ArgumentException("Kernel must be 3x3.");
        }

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Flatten kernel for cache-friendly access
        Span<float> flatKernel = stackalloc float[9];
        for (int ky = 0;ky < 3;ky++)
        {
            for (int kx = 0;kx < 3;kx++)
            {
                flatKernel[ky * 3 + kx] = kernel[ky, kx];
            }
        }

        float[] flatKernelArray = flatKernel.ToArray();

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            // Cache the 3 source rows
            var row0 = source.GetPixelRow(Math.Clamp(y - 1, 0, height - 1));
            var row1 = source.GetPixelRow(y);
            var row2 = source.GetPixelRow(Math.Clamp(y + 1, 0, height - 1));

            for (int x = 0;x < width;x++)
            {
                int dstOff = x * channels;
                int xl = Math.Max(0, x - 1) * channels;
                int xc = x * channels;
                int xr = Math.Min(width - 1, x + 1) * channels;

                for (int c = 0;c < channels;c++)
                {
                    float sum =
                        row0[xl + c] * flatKernelArray[0] + row0[xc + c] * flatKernelArray[1] + row0[xr + c] * flatKernelArray[2] +
                        row1[xl + c] * flatKernelArray[3] + row1[xc + c] * flatKernelArray[4] + row1[xr + c] * flatKernelArray[5] +
                        row2[xl + c] * flatKernelArray[6] + row2[xc + c] * flatKernelArray[7] + row2[xr + c] * flatKernelArray[8];
                    dstRow[dstOff + c] = (ushort)Math.Clamp(sum + 0.5f, 0, Quantum.MaxValue);
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Applies an NxN convolution kernel to the image.
    /// Used for edge detection kernels larger than 3x3.
    /// </summary>
    public static ImageFrame ApplyKernelNxN(ImageFrame source, double[] kernel, int kernelSize)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int halfK = kernelSize / 2;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int dstOff = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (int ky = 0; ky < kernelSize; ky++)
                    {
                        int sy = Math.Clamp(y + ky - halfK, 0, height - 1);
                        var srcRow = source.GetPixelRow(sy);
                        for (int kx = 0; kx < kernelSize; kx++)
                        {
                            int sx = Math.Clamp(x + kx - halfK, 0, width - 1);
                            sum += srcRow[sx * channels + c] * kernel[ky * kernelSize + kx];
                        }
                    }
                    dstRow[dstOff + c] = (ushort)Math.Clamp(sum + 0.5, 0, Quantum.MaxValue);
                }
            }
        });

        return result;
    }

    /// <summary>
    /// ImageMagick-style edge detection using a Laplacian kernel.
    /// All kernel values are -1 except the center which is (n²-1).
    /// Much stronger than Sobel — used by IM's charcoal, sketch, etc.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Radius controlling kernel size (kernelSize = 2*ceil(radius)+1).</param>
    public static ImageFrame EdgeDetect(ImageFrame source, double radius)
    {
        int kernelSize = Math.Max(3, 2 * (int)Math.Ceiling(Math.Max(radius, 1.0)) + 1);
        int totalElements = kernelSize * kernelSize;
        double[] kernel = new double[totalElements];

        // Fill all with -1, center with (n²-1)
        for (int i = 0; i < totalElements; i++)
            kernel[i] = -1.0;
        kernel[totalElements / 2] = totalElements - 1.0;

        if (kernelSize == 3)
        {
            // Use the optimized 3x3 path
            float[,] k3 = new float[3, 3];
            for (int ky = 0; ky < 3; ky++)
                for (int kx = 0; kx < 3; kx++)
                    k3[ky, kx] = (float)kernel[ky * 3 + kx];
            return ApplyKernel3x3(source, k3);
        }

        return ApplyKernelNxN(source, kernel, kernelSize);
    }

    /// <summary>
    /// Sobel edge detection (returns magnitude of horizontal + vertical gradients).
    /// </summary>
    public static ImageFrame EdgeDetect(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            var row0 = source.GetPixelRow(Math.Clamp(y - 1, 0, height - 1));
            var row1 = source.GetPixelRow(y);
            var row2 = source.GetPixelRow(Math.Clamp(y + 1, 0, height - 1));

            for (int x = 0;x < width;x++)
            {
                int dstOff = x * channels;
                int xl = Math.Max(0, x - 1) * channels;
                int xc = x * channels;
                int xr = Math.Min(width - 1, x + 1) * channels;

                for (int c = 0;c < channels;c++)
                {
                    // Sobel X: [-1,0,1; -2,0,2; -1,0,1]
                    float gx = -row0[xl + c] + row0[xr + c]
                             - 2f * row1[xl + c] + 2f * row1[xr + c]
                             - row2[xl + c] + row2[xr + c];

                    // Sobel Y: [-1,-2,-1; 0,0,0; 1,2,1]
                    float gy = -row0[xl + c] - 2f * row0[xc + c] - row0[xr + c]
                             + row2[xl + c] + 2f * row2[xc + c] + row2[xr + c];

                    float magnitude = MathF.Sqrt(gx * gx + gy * gy);
                    dstRow[dstOff + c] = (ushort)Math.Clamp(magnitude + 0.5f, 0, Quantum.MaxValue);
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Emboss effect using a directional convolution.
    /// </summary>
    public static ImageFrame Emboss(ImageFrame source)
    {
        float[,] kernel = {
            { -2, -1, 0 },
            { -1, 1, 1 },
            { 0, 1, 2 }
        };
        return ApplyKernel3x3(source, kernel);
    }

    // ─── Separable Convolution Engine ────────────────────────────

    /// <summary>
    /// Applies a 1D kernel in two passes (horizontal then vertical) for O(n) per pixel. Both passes parallelized across
    /// scanlines.
    /// </summary>
    private static ImageFrame ApplySeparableKernel(ImageFrame source, float[] kernel)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int radius = kernel.Length / 2;

        // Pass 1: Horizontal
        var tempBuffer = ArrayPool<float>.Shared.Rent(width * height * channels);

        try
        {
            Parallel.For(0, height, y =>
            {
                var srcRow = source.GetPixelRow(y);
                int rowBase = y * width * channels;

                for (int x = 0;x < width;x++)
                {
                    int dstIdx = rowBase + x * channels;
                    for (int c = 0;c < channels;c++)
                    {
                        float sum = 0;
                        for (int k = 0;k < kernel.Length;k++)
                        {
                            int sx = Math.Clamp(x + k - radius, 0, width - 1);
                            sum += srcRow[sx * channels + c] * kernel[k];
                        }
                        tempBuffer[dstIdx + c] = sum;
                    }
                }
            });

            // Pass 2: Vertical
            var result = new ImageFrame();
            result.Initialize(width, height, source.Colorspace, source.HasAlpha);

            Parallel.For(0, height, y =>
            {
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0;x < width;x++)
                {
                    int dstOff = x * channels;
                    for (int c = 0;c < channels;c++)
                    {
                        float sum = 0;
                        for (int k = 0;k < kernel.Length;k++)
                        {
                            int sy = Math.Clamp(y + k - radius, 0, height - 1);
                            sum += tempBuffer[(sy * width + x) * channels + c] * kernel[k];
                        }
                        dstRow[dstOff + c] = (ushort)Math.Clamp(sum + 0.5f, 0, Quantum.MaxValue);
                    }
                }
            });

            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tempBuffer);
        }
    }

    /// <summary>
    /// Builds a normalized Gaussian kernel for the given sigma.
    /// </summary>
    private static float[] BuildGaussianKernel(double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        int size = radius * 2 + 1;
        float[] kernel = new float[size];

        double sum = 0;
        double twoSigmaSquared = 2.0 * sigma * sigma;

        for (int i = 0;i < size;i++)
        {
            double x = i - radius;
            kernel[i] = (float)Math.Exp(-(x * x) / twoSigmaSquared);
            sum += kernel[i];
        }

        // Normalize
        float invSum = (float)(1.0 / sum);
        for (int i = 0;i < size;i++)
        {
            kernel[i] *= invSum;
        }

        return kernel;
    }

    /// <summary>
    /// Adaptive blur — applies variable-radius Gaussian blur where flat regions get more
    /// blur and edges get less. Uses edge detection to determine local edge strength.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="maxSigma">Maximum blur sigma (used in flat regions). Default 2.0.</param>
    public static ImageFrame AdaptiveBlur(ImageFrame source, double maxSigma = 2.0)
    {
        if (maxSigma <= 0) maxSigma = 2.0;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        // Compute edge map to determine blur radius at each pixel
        using var edgeImage = EdgeDetect(source);
        double[] edgeStrength = ComputeNormalizedEdgeMap(edgeImage);

        try
        {
            // Pre-build a set of blur kernels for different sigma values
            const int kernelCount = 16;
            float[][] kernels = new float[kernelCount][];
            for (int i = 0; i < kernelCount; i++)
            {
                double sigma = maxSigma * (i + 1) / kernelCount;
                kernels[i] = BuildGaussianKernel(sigma);
            }

            var result = new ImageFrame();
            result.Initialize(width, height, source.Colorspace, source.HasAlpha);

            Parallel.For(0, height, y =>
            {
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0; x < width; x++)
                {
                    // Edge strength 0..1 where 1 = strong edge
                    double edge = edgeStrength[y * width + x];
                    // Stronger edges → smaller kernel index (less blur)
                    int kernelIdx = (int)((1.0 - edge) * (kernelCount - 1));
                    kernelIdx = Math.Clamp(kernelIdx, 0, kernelCount - 1);
                    float[] kernel = kernels[kernelIdx];
                    int kRadius = kernel.Length / 2;

                    int dstOff = x * channels;

                    for (int c = 0; c < colorChannels; c++)
                    {
                        // Separable pass approximation: horizontal only for speed,
                        // then vertical via the row sampling
                        double sumH = 0;
                        double sumV = 0;

                        for (int k = -kRadius; k <= kRadius; k++)
                        {
                            float w = kernel[k + kRadius];
                            int sx = Math.Clamp(x + k, 0, width - 1);
                            int sy = Math.Clamp(y + k, 0, height - 1);

                            sumH += w * source.GetPixelRow(y)[sx * channels + c];
                            sumV += w * source.GetPixelRow(sy)[x * channels + c];
                        }

                        // Average of horizontal and vertical passes
                        dstRow[dstOff + c] = (ushort)Math.Clamp((sumH + sumV) * 0.5, 0, Quantum.MaxValue);
                    }

                    if (source.HasAlpha)
                        dstRow[dstOff + colorChannels] = source.GetPixelRow(y)[x * channels + colorChannels];
                }
            });

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(edgeStrength);
        }
    }

    /// <summary>
    /// Adaptive sharpen — applies variable-strength sharpening where edges get more
    /// sharpening and flat regions get less. Uses edge detection to modulate strength.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="sigma">Blur sigma for the unsharp mask base. Default 1.0.</param>
    /// <param name="maxAmount">Maximum sharpening amount (used at edges). Default 2.0.</param>
    public static ImageFrame AdaptiveSharpen(ImageFrame source, double sigma = 1.0, double maxAmount = 2.0)
    {
        if (sigma <= 0) sigma = 1.0;
        if (maxAmount <= 0) maxAmount = 2.0;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        using var blurred = GaussianBlur(source, sigma);
        using var edgeImage = EdgeDetect(source);
        double[] edgeStrength = ComputeNormalizedEdgeMap(edgeImage);

        try
        {
            var result = new ImageFrame();
            result.Initialize(width, height, source.Colorspace, source.HasAlpha);

            Parallel.For(0, height, y =>
            {
                var srcRow = source.GetPixelRow(y);
                var blurRow = blurred.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(y);

                for (int x = 0; x < width; x++)
                {
                    double edge = edgeStrength[y * width + x];
                    // Stronger edges → more sharpening
                    double amount = maxAmount * edge;

                    int off = x * channels;
                    for (int c = 0; c < colorChannels; c++)
                    {
                        double original = srcRow[off + c];
                        double blur = blurRow[off + c];
                        double sharpened = original + amount * (original - blur);
                        dstRow[off + c] = (ushort)Math.Clamp(sharpened, 0, Quantum.MaxValue);
                    }

                    if (source.HasAlpha)
                        dstRow[off + colorChannels] = srcRow[off + colorChannels];
                }
            });

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(edgeStrength);
        }
    }

    /// <summary>
    /// Compute a normalized edge strength map (0..1) from an edge-detected image.
    /// The returned array is rented from ArrayPool — caller must return it via ArrayPool&lt;double&gt;.Shared.Return().
    /// </summary>
    private static double[] ComputeNormalizedEdgeMap(ImageFrame edgeImage)
    {
        int width = (int)edgeImage.Columns;
        int height = (int)edgeImage.Rows;
        int channels = edgeImage.NumberOfChannels;
        int mapLen = width * height;
        double[] map = ArrayPool<double>.Shared.Rent(mapLen);

        // Find max luminance for normalization
        double maxLuma = 1.0;
        for (int y = 0; y < height; y++)
        {
            var row = edgeImage.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double luma;
                if (channels >= 3)
                {
                    int off = x * channels;
                    luma = 0.299 * row[off] + 0.587 * row[off + 1] + 0.114 * row[off + 2];
                }
                else
                {
                    luma = row[x * channels];
                }
                if (luma > maxLuma) maxLuma = luma;
            }
        }

        double invMax = 1.0 / maxLuma;
        for (int y = 0; y < height; y++)
        {
            var row = edgeImage.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double luma;
                if (channels >= 3)
                {
                    int off = x * channels;
                    luma = 0.299 * row[off] + 0.587 * row[off + 1] + 0.114 * row[off + 2];
                }
                else
                {
                    luma = row[x * channels];
                }
                map[y * width + x] = Math.Clamp(luma * invMax, 0, 1);
            }
        }

        return map;
    }
}
