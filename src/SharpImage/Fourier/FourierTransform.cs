using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Fourier;

/// <summary>
/// Pure C# forward/inverse Fast Fourier Transform for image processing. Uses Cooley-Tukey radix-2 algorithm on 2D data.
/// </summary>
public static class FourierTransform
{
    /// <summary>
    /// Forward FFT on the image's luminance channel. Returns magnitude and phase arrays. Both arrays are width x
    /// height, stored row-major. Magnitude values are log-scaled and normalized to [0..1] for display.
    /// </summary>
    public static (double[] Magnitude, double[] Phase) Forward(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        // Pad to next power of 2
        int pw = NextPowerOf2(width);
        int ph = NextPowerOf2(height);

        // Extract luminance into complex array (real part only)
        double[] realPart = new double[pw * ph];
        double[] imagPart = new double[pw * ph];

        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                double lum;
                if (channels >= 3)
                {
                    lum = 0.2126 * row[off] * Quantum.Scale +
                                          0.7152 * row[off + 1] * Quantum.Scale +
                                          0.0722 * row[off + 2] * Quantum.Scale;
                }
                else
                {
                    lum = row[off] * Quantum.Scale;
                }

                realPart[y * pw + x] = lum;
            }
        }

        // 2D FFT: transform rows, then columns
        FFT2D(realPart, imagPart, pw, ph, forward: true);

        // Shift zero-frequency to center
        ShiftQuadrants(realPart, pw, ph);
        ShiftQuadrants(imagPart, pw, ph);

        // Compute magnitude and phase
        double[] magnitude = new double[pw * ph];
        double[] phase = new double[pw * ph];
        double maxMag = 0;

        for (int i = 0;i < pw * ph;i++)
        {
            magnitude[i] = Math.Sqrt(realPart[i] * realPart[i] + imagPart[i] * imagPart[i]);
            phase[i] = Math.Atan2(imagPart[i], realPart[i]);
            if (magnitude[i] > maxMag)
            {
                maxMag = magnitude[i];
            }
        }

        // Log-scale magnitude for visualization
        if (maxMag > 0)
        {
            double logMax = Math.Log(1 + maxMag);
            for (int i = 0;i < magnitude.Length;i++)
            {
                magnitude[i] = Math.Log(1 + magnitude[i]) / logMax;
            }
        }

        return (magnitude, phase);
    }

    /// <summary>
    /// Inverse FFT: reconstruct image from magnitude and phase arrays. Magnitude should be in log-scale [0..1] as
    /// produced by Forward().
    /// </summary>
    public static ImageFrame Inverse(double[] magnitude, double[] phase,
        int transformWidth, int transformHeight, uint outputWidth, uint outputHeight)
    {
        // Un-log-scale magnitude
        double maxMag = 0;
        for (int i = 0;i < magnitude.Length;i++)
        {
            if (magnitude[i] > maxMag)
            {
                maxMag = magnitude[i];
            }
        }

        double[] realPart = new double[transformWidth * transformHeight];
        double[] imagPart = new double[transformWidth * transformHeight];

        // Estimate original max magnitude (approximate)
        double logMax = maxMag > 0 ? 1.0 : 0; // normalized, invert the log
        for (int i = 0;i < magnitude.Length;i++)
        {
            double mag = Math.Exp(magnitude[i] * Math.Log(1 + 1000)) - 1; // approximate un-log
            realPart[i] = mag * Math.Cos(phase[i]);
            imagPart[i] = mag * Math.Sin(phase[i]);
        }

        // Unshift quadrants
        ShiftQuadrants(realPart, transformWidth, transformHeight);
        ShiftQuadrants(imagPart, transformWidth, transformHeight);

        // Inverse 2D FFT
        FFT2D(realPart, imagPart, transformWidth, transformHeight, forward: false);

        // Build output image from real part
        var result = new ImageFrame();
        result.Initialize(outputWidth, outputHeight, ColorspaceType.SRGB, false);

        for (int y = 0;y < (int)outputHeight;y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0;x < (int)outputWidth;x++)
            {
                double val = Math.Clamp(realPart[y * transformWidth + x], 0, 1);
                ushort q = (ushort)(val * Quantum.MaxValue);
                row[x * 3] = q;
                row[x * 3 + 1] = q;
                row[x * 3 + 2] = q;
            }
        }

        return result;
    }

    /// <summary>
    /// Create a magnitude spectrum image (grayscale) from the FFT of an image.
    /// </summary>
    public static ImageFrame MagnitudeSpectrum(ImageFrame image)
    {
        var (magnitude, _) = Forward(image);
        int pw = NextPowerOf2((int)image.Columns);
        int ph = NextPowerOf2((int)image.Rows);

        var result = new ImageFrame();
        result.Initialize((uint)pw, (uint)ph, ColorspaceType.SRGB, false);

        for (int y = 0;y < ph;y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0;x < pw;x++)
            {
                ushort q = (ushort)(Math.Clamp(magnitude[y * pw + x], 0, 1) * Quantum.MaxValue);
                row[x * 3] = q;
                row[x * 3 + 1] = q;
                row[x * 3 + 2] = q;
            }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Core FFT Implementation (Cooley-Tukey radix-2)
    // ═══════════════════════════════════════════════════════════════════

    private static void FFT2D(double[] real, double[] imag, int width, int height, bool forward)
    {
        // Pool temp arrays instead of allocating per call
        var rowRBuf = ArrayPool<double>.Shared.Rent(Math.Max(width, height));
        var rowIBuf = ArrayPool<double>.Shared.Rent(Math.Max(width, height));

        try
        {
            // Transform each row
            for (int y = 0;y < height;y++)
            {
                int offset = y * width;
                Array.Copy(real, offset, rowRBuf, 0, width);
                Array.Copy(imag, offset, rowIBuf, 0, width);
                FFT1D(rowRBuf.AsSpan(0, width), rowIBuf.AsSpan(0, width), forward);
                Array.Copy(rowRBuf, 0, real, offset, width);
                Array.Copy(rowIBuf, 0, imag, offset, width);
            }

            // Transform each column
            for (int x = 0;x < width;x++)
            {
                for (int y = 0;y < height;y++)
                {
                    rowRBuf[y] = real[y * width + x];
                    rowIBuf[y] = imag[y * width + x];
                }
                FFT1D(rowRBuf.AsSpan(0, height), rowIBuf.AsSpan(0, height), forward);
                for (int y = 0;y < height;y++)
                {
                    real[y * width + x] = rowRBuf[y];
                    imag[y * width + x] = rowIBuf[y];
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rowRBuf);
            ArrayPool<double>.Shared.Return(rowIBuf);
        }
    }

    private static void FFT1D(Span<double> real, Span<double> imag, bool forward)
    {
        int n = real.Length;
        if (n <= 1)
        {
            return;
        }

        // Bit-reversal permutation
        int bits = (int)Math.Log2(n);
        for (int i = 0;i < n;i++)
        {
            int j = BitReverse(i, bits);
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Butterfly operations
        double dir = forward ? -1.0 : 1.0;
        for (int len = 2;len <= n;len *= 2)
        {
            double angle = dir * 2.0 * Math.PI / len;
            double wR = Math.Cos(angle);
            double wI = Math.Sin(angle);

            for (int i = 0;i < n;i += len)
            {
                double curR = 1.0, curI = 0.0;
                int half = len / 2;
                for (int j = 0;j < half;j++)
                {
                    int a = i + j;
                    int b = a + half;
                    double tR = curR * real[b] - curI * imag[b];
                    double tI = curR * imag[b] + curI * real[b];

                    real[b] = real[a] - tR;
                    imag[b] = imag[a] - tI;
                    real[a] += tR;
                    imag[a] += tI;

                    double newR = curR * wR - curI * wI;
                    curI = curR * wI + curI * wR;
                    curR = newR;
                }
            }
        }

        // Normalize for inverse transform
        if (!forward)
        {
            double inv = 1.0 / n;
            for (int i = 0;i < n;i++)
            {
                real[i] *= inv;
                imag[i] *= inv;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitReverse(int value, int bits)
    {
        int result = 0;
        for (int i = 0;i < bits;i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }

    private static void ShiftQuadrants(double[] data, int width, int height)
    {
        int halfW = width / 2;
        int halfH = height / 2;

        for (int y = 0;y < halfH;y++)
        {
            for (int x = 0;x < halfW;x++)
            {
                // Swap Q1 ↔ Q3
                int i1 = y * width + x;
                int i3 = (y + halfH) * width + (x + halfW);
                (data[i1], data[i3]) = (data[i3], data[i1]);

                // Swap Q2 ↔ Q4
                int i2 = y * width + (x + halfW);
                int i4 = (y + halfH) * width + x;
                (data[i2], data[i4]) = (data[i4], data[i2]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextPowerOf2(int v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }
}
