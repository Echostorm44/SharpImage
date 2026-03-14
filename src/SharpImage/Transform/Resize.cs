using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Transform;

/// <summary>
/// Image resize operations with multiple interpolation methods. Uses scanline-based processing with ArrayPool for zero-
/// allocation hot paths. Horizontal pass is parallelized; vertical pass uses SIMD-accelerated accumulation.
/// </summary>
public static class Resize
{
    /// <summary>
    /// Resizes an image to the specified dimensions using the given interpolation method. Returns a new ImageFrame; the
    /// caller is responsible for disposing both old and new.
    /// </summary>
    public static ImageFrame Apply(ImageFrame source, int newWidth, int newHeight,
        InterpolationMethod method = InterpolationMethod.Lanczos3)
    {
        if (newWidth <= 0 || newHeight <= 0)
        {
            throw new ArgumentOutOfRangeException("Resize dimensions must be positive.");
        }

        return method switch
        {
            InterpolationMethod.NearestNeighbor => ResizeNearestNeighbor(source, newWidth, newHeight),
            InterpolationMethod.Bilinear or InterpolationMethod.Triangle or InterpolationMethod.Bartlett
                => ResizeSeparable(source, newWidth, newHeight, TriangleKernel, 1.0),
            InterpolationMethod.Hermite => ResizeSeparable(source, newWidth, newHeight, HermiteKernel, 1.0),
            InterpolationMethod.Bicubic or InterpolationMethod.Catrom
                => ResizeSeparable(source, newWidth, newHeight, CatromKernel, 2.0),
            InterpolationMethod.Mitchell => ResizeSeparable(source, newWidth, newHeight, MitchellKernel, 2.0),
            InterpolationMethod.Gaussian => ResizeSeparable(source, newWidth, newHeight, GaussianKernel, 2.0),
            InterpolationMethod.Spline => ResizeSeparable(source, newWidth, newHeight, SplineKernel, 2.0),
            InterpolationMethod.Sinc => ResizeSeparable(source, newWidth, newHeight, SincKernel, 4.0),
            InterpolationMethod.Lanczos2 => ResizeSeparable(source, newWidth, newHeight, Lanczos2Kernel, 2.0),
            InterpolationMethod.Lanczos3 => ResizeSeparable(source, newWidth, newHeight, Lanczos3Kernel, 3.0),
            InterpolationMethod.Lanczos4 => ResizeSeparable(source, newWidth, newHeight, Lanczos4Kernel, 4.0),
            InterpolationMethod.Lanczos5 => ResizeSeparable(source, newWidth, newHeight, Lanczos5Kernel, 5.0),
            InterpolationMethod.Hann => ResizeSeparable(source, newWidth, newHeight, HannKernel, 3.0),
            InterpolationMethod.Hamming => ResizeSeparable(source, newWidth, newHeight, HammingKernel, 3.0),
            InterpolationMethod.Blackman => ResizeSeparable(source, newWidth, newHeight, BlackmanKernel, 3.0),
            InterpolationMethod.Kaiser => ResizeSeparable(source, newWidth, newHeight, KaiserKernel, 3.0),
            InterpolationMethod.Parzen => ResizeSeparable(source, newWidth, newHeight, ParzenKernel, 2.0),
            InterpolationMethod.Bohman => ResizeSeparable(source, newWidth, newHeight, BohmanKernel, 1.0),
            InterpolationMethod.Welch => ResizeSeparable(source, newWidth, newHeight, WelchKernel, 1.0),
            InterpolationMethod.Lagrange => ResizeSeparable(source, newWidth, newHeight, LagrangeKernel, 2.0),
            _ => ResizeSeparable(source, newWidth, newHeight, Lanczos3Kernel, 3.0)
        };
    }

    /// <summary>
    /// Non-interpolated point-sample resize. Selects the nearest source pixel without any filtering.
    /// Ideal for pixel art, index-based images, and cases where sharp edges must be preserved.
    /// Unlike nearest-neighbor in the Apply method, this is a direct decimation/replication
    /// with no contribution weighting.
    /// </summary>
    public static ImageFrame Sample(ImageFrame source, int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentOutOfRangeException("Sample dimensions must be positive.");

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        // Precompute source X indices for each destination column
        int[] srcXMap = new int[newWidth];
        for (int x = 0; x < newWidth; x++)
            srcXMap[x] = Math.Min((int)((long)x * srcWidth / newWidth), srcWidth - 1);

        Parallel.For(0, newHeight, y =>
        {
            int srcY = Math.Min((int)((long)y * srcHeight / newHeight), srcHeight - 1);
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < newWidth; x++)
            {
                int srcOffset = srcXMap[x] * channels;
                int dstOffset = x * channels;
                for (int c = 0; c < channels; c++)
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
            }
        });

        return result;
    }

    /// <summary>
    /// Adaptive resize using mesh interpolation. For downscaling, averages a neighborhood of source
    /// pixels weighted by area overlap. For upscaling, uses bilinear interpolation.
    /// Produces smoother results than nearest-neighbor while being simpler than full filter-based resize.
    /// </summary>
    public static ImageFrame AdaptiveResize(ImageFrame source, int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentOutOfRangeException("AdaptiveResize dimensions must be positive.");

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        double xScale = (double)srcWidth / newWidth;
        double yScale = (double)srcHeight / newHeight;

        Parallel.For(0, newHeight, y =>
        {
            double srcYStart = y * yScale;
            double srcYEnd = (y + 1) * yScale;
            int yStart = (int)srcYStart;
            int yEnd = Math.Min((int)Math.Ceiling(srcYEnd), srcHeight);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < newWidth; x++)
            {
                double srcXStart = x * xScale;
                double srcXEnd = (x + 1) * xScale;
                int xStart = (int)srcXStart;
                int xEnd = Math.Min((int)Math.Ceiling(srcXEnd), srcWidth);

                for (int c = 0; c < channels; c++)
                {
                    double sum = 0;
                    double totalWeight = 0;

                    for (int sy = yStart; sy < yEnd; sy++)
                    {
                        double yWeight = Math.Min(sy + 1, srcYEnd) - Math.Max(sy, srcYStart);
                        var srcRow = source.GetPixelRow(sy);

                        for (int sx = xStart; sx < xEnd; sx++)
                        {
                            double xWeight = Math.Min(sx + 1, srcXEnd) - Math.Max(sx, srcXStart);
                            double weight = xWeight * yWeight;
                            sum += srcRow[sx * channels + c] * weight;
                            totalWeight += weight;
                        }
                    }

                    dstRow[x * channels + c] = totalWeight > 0
                        ? Quantum.Clamp((int)Math.Round(sum / totalWeight))
                        : (ushort)0;
                }
            }
        });

        return result;
    }
    /// <summary>
    /// Nearest-neighbor: fastest, no anti-aliasing. Good for pixel art or index-based images.
    /// </summary>
    private static ImageFrame ResizeNearestNeighbor(ImageFrame source, int newWidth, int newHeight)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        double xRatio = (double)srcWidth / newWidth;
        double yRatio = (double)srcHeight / newHeight;

        for (int y = 0;y < newHeight;y++)
        {
            int srcY = Math.Min((int)(y * yRatio), srcHeight - 1);
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < newWidth;x++)
            {
                int srcX = Math.Min((int)(x * xRatio), srcWidth - 1);
                int srcOffset = srcX * channels;
                int dstOffset = x * channels;

                for (int c = 0;c < channels;c++)
                {
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Two-pass separable resize: horizontal then vertical. Both passes are parallelized. Vertical pass uses row-order
    /// accumulation for cache locality.
    /// </summary>
    private static ImageFrame ResizeSeparable(ImageFrame source, int newWidth, int newHeight,
        Func<double, double> kernel, double kernelRadius)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Pre-compute horizontal contribution lists
        double xScale = (double)newWidth / srcWidth;
        double xFilterRadius = xScale < 1.0 ? kernelRadius / xScale : kernelRadius;
        double xScaleFactor = xScale < 1.0 ? xScale : 1.0;

        var contributions = new ContributionList[newWidth];
        for (int x = 0;x < newWidth;x++)
        {
            double center = (x + 0.5) / xScale - 0.5;
            int left = Math.Max(0, (int)Math.Floor(center - xFilterRadius));
            int right = Math.Min(srcWidth - 1, (int)Math.Ceiling(center + xFilterRadius));
            int count = right - left + 1;

            var weights = new float[count];
            float totalWeight = 0;
            for (int k = 0;k < count;k++)
            {
                double dist = (left + k - center) * xScaleFactor;
                float w = (float)kernel(dist);
                weights[k] = w;
                totalWeight += w;
            }

            if (totalWeight > 0)
            {
                float inv = 1.0f / totalWeight;
                for (int k = 0;k < count;k++)
                {
                    weights[k] *= inv;
                }
            }

            contributions[x] = new ContributionList(left, weights);
        }

        // Pass 1: Horizontal resize (srcWidth → newWidth, keeping srcHeight)
        int tempStride = newWidth * channels;
        var tempBuffer = ArrayPool<float>.Shared.Rent(tempStride * srcHeight);

        try
        {
            Parallel.For(0, srcHeight, y =>
            {
                var srcRow = source.GetPixelRow(y);
                int rowBase = y * tempStride;
                ref ushort srcBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(srcRow));
                ref float tmpBase = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(tempBuffer), rowBase);

                if (channels == 3)
                {
                    for (int x = 0; x < newWidth; x++)
                    {
                        ref readonly var contrib = ref contributions[x];
                        var weights = contrib.Weights;
                        int left = contrib.Left;
                        int wLen = weights.Length;
                        ref float wRef = ref MemoryMarshal.GetArrayDataReference(weights);
                        float s0 = 0, s1 = 0, s2 = 0;
                        for (int k = 0; k < wLen; k++)
                        {
                            float w = Unsafe.Add(ref wRef, k);
                            int si = (left + k) * 3;
                            s0 += w * Unsafe.Add(ref srcBase, si);
                            s1 += w * Unsafe.Add(ref srcBase, si + 1);
                            s2 += w * Unsafe.Add(ref srcBase, si + 2);
                        }
                        int dstOff = x * 3;
                        Unsafe.Add(ref tmpBase, dstOff) = s0;
                        Unsafe.Add(ref tmpBase, dstOff + 1) = s1;
                        Unsafe.Add(ref tmpBase, dstOff + 2) = s2;
                    }
                }
                else if (channels == 4)
                {
                    for (int x = 0; x < newWidth; x++)
                    {
                        ref readonly var contrib = ref contributions[x];
                        var weights = contrib.Weights;
                        int left = contrib.Left;
                        int wLen = weights.Length;
                        ref float wRef = ref MemoryMarshal.GetArrayDataReference(weights);
                        float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                        for (int k = 0; k < wLen; k++)
                        {
                            float w = Unsafe.Add(ref wRef, k);
                            int si = (left + k) * 4;
                            s0 += w * Unsafe.Add(ref srcBase, si);
                            s1 += w * Unsafe.Add(ref srcBase, si + 1);
                            s2 += w * Unsafe.Add(ref srcBase, si + 2);
                            s3 += w * Unsafe.Add(ref srcBase, si + 3);
                        }
                        int dstOff = x * 4;
                        Unsafe.Add(ref tmpBase, dstOff) = s0;
                        Unsafe.Add(ref tmpBase, dstOff + 1) = s1;
                        Unsafe.Add(ref tmpBase, dstOff + 2) = s2;
                        Unsafe.Add(ref tmpBase, dstOff + 3) = s3;
                    }
                }
                else
                {
                    for (int x = 0; x < newWidth; x++)
                    {
                        ref readonly var contrib = ref contributions[x];
                        var weights = contrib.Weights;
                        int left = contrib.Left;
                        int wLen = weights.Length;
                        ref float wRef = ref MemoryMarshal.GetArrayDataReference(weights);
                        int dstOff = x * channels;
                        for (int c = 0; c < channels; c++)
                        {
                            float sum = 0;
                            for (int k = 0; k < wLen; k++)
                            {
                                sum += Unsafe.Add(ref wRef, k) * Unsafe.Add(ref srcBase, (left + k) * channels + c);
                            }
                            Unsafe.Add(ref tmpBase, dstOff + c) = sum;
                        }
                    }
                }
            });

            // Pre-compute vertical contributions
            double yScale = (double)newHeight / srcHeight;
            double yFilterRadius = yScale < 1.0 ? kernelRadius / yScale : kernelRadius;
            double yScaleFactor = yScale < 1.0 ? yScale : 1.0;

            var yContributions = new ContributionList[newHeight];
            for (int y = 0;y < newHeight;y++)
            {
                double center = (y + 0.5) / yScale - 0.5;
                int top = Math.Max(0, (int)Math.Floor(center - yFilterRadius));
                int bottom = Math.Min(srcHeight - 1, (int)Math.Ceiling(center + yFilterRadius));
                int count = bottom - top + 1;

                var weights = new float[count];
                float totalWeight = 0;
                for (int k = 0;k < count;k++)
                {
                    double dist = (top + k - center) * yScaleFactor;
                    float w = (float)kernel(dist);
                    weights[k] = w;
                    totalWeight += w;
                }

                if (totalWeight > 0)
                {
                    float inv = 1.0f / totalWeight;
                    for (int k = 0;k < count;k++)
                    {
                        weights[k] *= inv;
                    }
                }

                yContributions[y] = new ContributionList(top, weights);
            }

            // Pass 2: Vertical resize — row-order accumulation for cache locality
            var result = new ImageFrame();
            result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

            Parallel.For(0, newHeight, y =>
            {
                ref readonly var contrib = ref yContributions[y];
                var dstRow = result.GetPixelRowForWrite(y);
                var weights = contrib.Weights;
                int top = contrib.Left;
                int rowLen = newWidth * channels;

                // Rent a float accumulator for this output row
                var accBuf = ArrayPool<float>.Shared.Rent(rowLen);
                try
                {
                    var acc = accBuf.AsSpan(0, rowLen);
                    acc.Clear();

                    // Accumulate each contributing source row (sequential memory access)
                    for (int k = 0;k < weights.Length;k++)
                    {
                        float w = weights[k];
                        int srcRowBase = (top + k) * tempStride;
                        ref float srcRef = ref tempBuffer[srcRowBase];
                        ref float accRef = ref MemoryMarshal.GetReference(acc);

                        // SIMD-friendly: sequential acc[i] += w * src[i]
                        int i = 0;
                        if (Vector256.IsHardwareAccelerated && rowLen >= 8)
                        {
                            var vw = Vector256.Create(w);
                            for (;i + 8 <= rowLen;i += 8)
                            {
                                var vs = Vector256.LoadUnsafe(ref Unsafe.Add(ref srcRef, i));
                                var va = Vector256.LoadUnsafe(ref Unsafe.Add(ref accRef, i));
                                va = Fma.IsSupported
                                    ? Fma.MultiplyAdd(vs, vw, va)
                                    : va + vs * vw;
                                va.StoreUnsafe(ref Unsafe.Add(ref accRef, i));
                            }
                        }
                        for (;i < rowLen;i++)
                        {
                            Unsafe.Add(ref accRef, i) += w * Unsafe.Add(ref srcRef, i);
                        }
                    }

                    // Clamp and write to destination — SIMD vectorized
                    ref float accClamp = ref MemoryMarshal.GetReference(acc);
                    ref ushort dstRef = ref MemoryMarshal.GetReference(dstRow);
                    int ci = 0;
                    if (Vector256.IsHardwareAccelerated && rowLen >= 8)
                    {
                        var vHalf = Vector256.Create(0.5f);
                        var vMin = Vector256<float>.Zero;
                        var vMax = Vector256.Create((float)Quantum.MaxValue);
                        for (; ci + 8 <= rowLen; ci += 8)
                        {
                            var v = Vector256.LoadUnsafe(ref Unsafe.Add(ref accClamp, ci)) + vHalf;
                            v = Vector256.Max(Vector256.Min(v, vMax), vMin);
                            var vi = Vector256.ConvertToInt32(v);
                            // Narrow 8 int32s to 8 ushorts: pack to 128-bit shorts
                            var lo = vi.GetLower();
                            var hi = vi.GetUpper();
                            var packed = Sse41.IsSupported
                                ? Sse41.PackUnsignedSaturate(lo, hi)
                                : Sse2.IsSupported
                                    ? PackUnsignedSse2(lo, hi)
                                    : NarrowToUshortFallback(vi);
                            packed.StoreUnsafe(ref Unsafe.Add(ref dstRef, ci));
                        }
                    }
                    for (; ci < rowLen; ci++)
                    {
                        Unsafe.Add(ref dstRef, ci) = (ushort)Math.Clamp(
                            Unsafe.Add(ref accClamp, ci) + 0.5f, 0, Quantum.MaxValue);
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(accBuf);
                }
            });

            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tempBuffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> NarrowToUshortFallback(Vector256<int> v)
    {
        Span<ushort> result = stackalloc ushort[8];
        for (int j = 0; j < 8; j++)
            result[j] = (ushort)Math.Clamp(v.GetElement(j), 0, ushort.MaxValue);
        return Vector128.Create(result);
    }

    // SSE2-only unsigned 32→16 packing: subtract 32768, signed pack, add 32768 back
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> PackUnsignedSse2(Vector128<int> lo, Vector128<int> hi)
    {
        var offset = Vector128.Create(32768);
        var loShifted = Sse2.Subtract(lo, offset);
        var hiShifted = Sse2.Subtract(hi, offset);
        var packed = Sse2.PackSignedSaturate(loShifted, hiShifted);
        return Sse2.Add(packed, Vector128.Create((short)(-32768))).AsUInt16();
    }

    // ─── Pre-computed Contribution Weights ───────────────────────
    private readonly struct ContributionList(int left, float[] weights)
    {
        public readonly int Left = left;
        public readonly float[] Weights = weights;
    }

    // ─── Interpolation Kernels ───────────────────────────────────

    // Triangle / Bilinear / Bartlett (support = 1.0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TriangleKernel(double x)
    {
        x = Math.Abs(x);
        return x < 1.0 ? 1.0 - x : 0.0;
    }

    // Hermite cubic (support = 1.0) — smooth interpolation, no overshoot
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HermiteKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 1.0) return 0.0;
        return (2.0 * x - 3.0) * x * x + 1.0;
    }

    // Catmull-Rom spline / Catrom (B=0, C=0.5, support = 2.0) — sharp, slight ringing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CatromKernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1.0)
            return (1.5 * x - 2.5) * x * x + 1.0;
        if (x < 2.0)
            return ((-0.5 * x + 2.5) * x - 4.0) * x + 2.0;
        return 0.0;
    }

    // Mitchell-Netravali (B=1/3, C=1/3, support = 2.0) — balanced sharpness and ringing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double MitchellKernel(double x)
    {
        x = Math.Abs(x);
        double x2 = x * x;
        double x3 = x2 * x;
        if (x < 1.0)
            return (7.0 * x3 - 12.0 * x2 + 5.333333333333333) / 6.0;
        if (x < 2.0)
            return (-2.333333333333333 * x3 + 12.0 * x2 - 20.0 * x + 10.666666666666666) / 6.0;
        return 0.0;
    }

    // Gaussian (support = 2.0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GaussianKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 2.0) return 0.0;
        return Math.Exp(-2.0 * x * x);
    }

    // B-Spline (B=1, C=0, support = 2.0) — very smooth, no ringing, blurry
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SplineKernel(double x)
    {
        x = Math.Abs(x);
        double x2 = x * x;
        double x3 = x2 * x;
        if (x < 1.0)
            return (0.5 * x3 - x2 + 2.0 / 3.0);
        if (x < 2.0)
        {
            double t = 2.0 - x;
            return t * t * t / 6.0;
        }
        return 0.0;
    }

    // Sinc helper — normalized sinc: sin(πx)/(πx)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SincValue(double x)
    {
        if (Math.Abs(x) < 1e-10) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    // Truncated Sinc (support = 4.0) — pure sinc, some ringing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SincKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 4.0) return 0.0;
        return SincValue(x);
    }

    // Lanczos family: Sinc windowed by Sinc(x/n)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LanczosN(double x, double n)
    {
        x = Math.Abs(x);
        if (x < 1e-10) return 1.0;
        if (x >= n) return 0.0;
        double px = Math.PI * x;
        return n * Math.Sin(px) * Math.Sin(px / n) / (px * px);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Lanczos2Kernel(double x) => LanczosN(x, 2.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Lanczos3Kernel(double x) => LanczosN(x, 3.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Lanczos4Kernel(double x) => LanczosN(x, 4.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Lanczos5Kernel(double x) => LanczosN(x, 5.0);

    // Hann-windowed Sinc (support = 3.0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HannKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 3.0) return 0.0;
        if (x < 1e-10) return 1.0;
        double window = 0.5 + 0.5 * Math.Cos(Math.PI * x / 3.0);
        return SincValue(x) * window;
    }

    // Hamming-windowed Sinc (support = 3.0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HammingKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 3.0) return 0.0;
        if (x < 1e-10) return 1.0;
        double window = 0.54 + 0.46 * Math.Cos(Math.PI * x / 3.0);
        return SincValue(x) * window;
    }

    // Blackman-windowed Sinc (support = 3.0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double BlackmanKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 3.0) return 0.0;
        if (x < 1e-10) return 1.0;
        double ratio = Math.PI * x / 3.0;
        double window = 0.42 + 0.5 * Math.Cos(ratio) + 0.08 * Math.Cos(2.0 * ratio);
        return SincValue(x) * window;
    }

    // Kaiser-windowed Sinc (support = 3.0, α = 6.5 matching ImageMagick)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double KaiserKernel(double x)
    {
        const double alpha = 6.5;
        x = Math.Abs(x);
        if (x >= 3.0) return 0.0;
        if (x < 1e-10) return 1.0;
        double ratio = x / 3.0;
        double arg = 1.0 - ratio * ratio;
        if (arg < 0.0) arg = 0.0;
        double window = BesselI0(alpha * Math.Sqrt(arg)) / BesselI0(alpha);
        return SincValue(x) * window;
    }

    // Modified Bessel function of the first kind, order 0 — used by Kaiser window
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double xHalf = x * 0.5;
        for (int k = 1; k < 30; k++)
        {
            term *= (xHalf / k) * (xHalf / k);
            sum += term;
            if (term < sum * 1e-15) break;
        }
        return sum;
    }

    // Parzen / de la Vallée-Poussin (support = 2.0) — smooth 4th-order B-spline approximation
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ParzenKernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1.0)
            return 1.0 - 1.5 * x * x * (1.0 - x * 0.5);
        if (x < 2.0)
        {
            double t = 2.0 - x;
            return 0.5 * t * t * t * 0.25;
        }
        return 0.0;
    }

    // Bohman (support = 1.0) — smooth taper, no ringing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double BohmanKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 1.0) return 0.0;
        double piX = Math.PI * x;
        return (1.0 - x) * Math.Cos(piX) + Math.Sin(piX) / Math.PI;
    }

    // Welch / Welsh (support = 1.0) — parabolic window
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double WelchKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 1.0) return 0.0;
        return 1.0 - x * x;
    }

    // Lagrange 3rd-order (support = 2.0) — polynomial interpolation
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LagrangeKernel(double x)
    {
        x = Math.Abs(x);
        if (x >= 2.0) return 0.0;
        if (x < 1.0)
            return (x * (x * (1.5 * x - 2.5))) + 1.0;
        return x * (x * (-0.5 * x + 2.5) - 4.0) + 2.0;
    }

    // ─── Convenience Methods ──────────────────────────────────────

    /// <summary>
    /// Creates a thumbnail that fits within the specified box while preserving aspect ratio.
    /// Uses Lanczos3 for high quality. Strips metadata (EXIF, ICC, XMP) from the result.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="maxWidth">Maximum width of the thumbnail.</param>
    /// <param name="maxHeight">Maximum height of the thumbnail.</param>
    /// <param name="method">Interpolation method (default Lanczos3).</param>
    public static ImageFrame Thumbnail(ImageFrame source, int maxWidth, int maxHeight,
        InterpolationMethod method = InterpolationMethod.Lanczos3)
    {
        if (maxWidth <= 0 || maxHeight <= 0)
            throw new ArgumentOutOfRangeException("Thumbnail dimensions must be positive.");

        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;

        double scaleX = (double)maxWidth / srcW;
        double scaleY = (double)maxHeight / srcH;
        double scale = Math.Min(scaleX, scaleY);

        int newWidth = Math.Max(1, (int)(srcW * scale + 0.5));
        int newHeight = Math.Max(1, (int)(srcH * scale + 0.5));

        var result = Apply(source, newWidth, newHeight, method);

        // Strip metadata for thumbnail output
        result.Metadata = new SharpImage.Metadata.ImageMetadata();

        return result;
    }

    /// <summary>
    /// Resample an image to a new resolution (DPI), resizing proportionally.
    /// For example, resampling a 300 DPI image to 72 DPI shrinks it to 24% of its size.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="targetDpiX">Target horizontal DPI.</param>
    /// <param name="targetDpiY">Target vertical DPI.</param>
    /// <param name="method">Interpolation method (default Lanczos3).</param>
    public static ImageFrame Resample(ImageFrame source, double targetDpiX, double targetDpiY,
        InterpolationMethod method = InterpolationMethod.Lanczos3)
    {
        if (targetDpiX <= 0 || targetDpiY <= 0)
            throw new ArgumentOutOfRangeException("Target DPI must be positive.");

        double sourceDpiX = source.ResolutionX > 0 ? source.ResolutionX : 72.0;
        double sourceDpiY = source.ResolutionY > 0 ? source.ResolutionY : 72.0;

        double scaleX = targetDpiX / sourceDpiX;
        double scaleY = targetDpiY / sourceDpiY;

        int newWidth = Math.Max(1, (int)((int)source.Columns * scaleX + 0.5));
        int newHeight = Math.Max(1, (int)((int)source.Rows * scaleY + 0.5));

        var result = Apply(source, newWidth, newHeight, method);
        result.ResolutionX = targetDpiX;
        result.ResolutionY = targetDpiY;
        return result;
    }

    /// <summary>
    /// Magnify: double the image dimensions (2× scale up) using the specified interpolation.
    /// </summary>
    public static ImageFrame Magnify(ImageFrame source,
        InterpolationMethod method = InterpolationMethod.Lanczos3)
    {
        return Apply(source, (int)source.Columns * 2, (int)source.Rows * 2, method);
    }

    /// <summary>
    /// Minify: halve the image dimensions (2× scale down) using the specified interpolation.
    /// </summary>
    public static ImageFrame Minify(ImageFrame source,
        InterpolationMethod method = InterpolationMethod.Lanczos3)
    {
        return Apply(source, Math.Max(1, (int)source.Columns / 2), Math.Max(1, (int)source.Rows / 2), method);
    }
}

/// <summary>
/// Interpolation methods for image resizing. Includes all common filter families:
/// simple (nearest/triangle/hermite), cubic (catrom/mitchell/spline), windowed-sinc
/// (lanczos/hann/hamming/blackman/kaiser), and specialized (gaussian/parzen/bohman/welch/lagrange).
/// </summary>
public enum InterpolationMethod
{
    NearestNeighbor,
    Bilinear,
    Bicubic,
    Lanczos3,
    Triangle,
    Hermite,
    Mitchell,
    Catrom,
    Gaussian,
    Sinc,
    Hann,
    Hamming,
    Blackman,
    Kaiser,
    Lanczos2,
    Lanczos4,
    Lanczos5,
    Spline,
    Parzen,
    Bohman,
    Bartlett,
    Welch,
    Lagrange
}
