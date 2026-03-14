using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Color adjustment operations: brightness, contrast, gamma, grayscale, invert, threshold, levels, posterize. All
/// operations return new ImageFrame instances (non-destructive).
/// </summary>
public static class ColorAdjust
{
    /// <summary>
    /// Adjusts brightness by a factor. 1.0 = no change, &gt;1.0 = brighter, &lt;1.0 = darker.
    /// </summary>
    public static ImageFrame Brightness(ImageFrame source, double factor)
    {
        return ApplyPerPixel(source, val => val * factor);
    }

    /// <summary>
    /// Adjusts contrast. Factor &gt;1.0 increases contrast, &lt;1.0 decreases. Uses the formula: 0.5 + factor * (value
    /// - 0.5)
    /// </summary>
    public static ImageFrame Contrast(ImageFrame source, double factor)
    {
        return ApplyPerPixel(source, val => 0.5 + factor * (val - 0.5));
    }

    /// <summary>
    /// Applies gamma correction. Gamma &gt;1.0 brightens midtones, &lt;1.0 darkens. Formula: value^(1/gamma)
    /// </summary>
    public static ImageFrame Gamma(ImageFrame source, double gamma)
    {
        if (gamma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gamma));
        }

        double invGamma = 1.0 / gamma;
        return ApplyPerPixel(source, val => Math.Pow(val, invGamma));
    }

    /// <summary>
    /// Converts to grayscale using ITU-R BT.709 luminance weights. Returns a 3-channel image where R=G=B=luminance.
    /// </summary>
    public static ImageFrame Grayscale(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int srcChannels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);
        int dstChannels = result.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int srcOff = x * srcChannels;
                int dstOff = x * dstChannels;

                double r = srcRow[srcOff] * Quantum.Scale;
                double g = srcRow[srcOff + 1] * Quantum.Scale;
                double b = srcRow[srcOff + 2] * Quantum.Scale;
                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                ushort gray = (ushort)Math.Clamp(lum * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);

                dstRow[dstOff] = gray;
                dstRow[dstOff + 1] = gray;
                dstRow[dstOff + 2] = gray;

                if (dstChannels > 3 && srcChannels > 3)
                {
                    dstRow[dstOff + 3] = srcRow[srcOff + 3];
                }
                else if (dstChannels > 3)
                {
                    dstRow[dstOff + 3] = Quantum.MaxValue;
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Inverts all pixel values (negative). Parallelized across scanlines.
    /// </summary>
    public static ImageFrame Invert(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int colorChannels = Math.Min(channels, 3);
                for (int c = 0;c < colorChannels;c++)
                {
                    dstRow[off + c] = (ushort)(Quantum.MaxValue - srcRow[off + c]);
                }

                if (channels > 3)
                {
                    dstRow[off + 3] = srcRow[off + 3];
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Binary threshold: pixels above threshold become white, below become black. Threshold is 0.0-1.0 (normalized).
    /// </summary>
    public static ImageFrame Threshold(ImageFrame source, double threshold = 0.5)
    {
        ushort thresholdQ = (ushort)(threshold * Quantum.MaxValue);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int off = x * channels;

                int avg = (srcRow[off] + srcRow[off + 1] + srcRow[off + 2]) / 3;
                ushort val = avg >= thresholdQ ? Quantum.MaxValue : (ushort)0;

                dstRow[off] = val;
                dstRow[off + 1] = val;
                dstRow[off + 2] = val;

                if (channels > 3)
                {
                    dstRow[off + 3] = srcRow[off + 3];
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Adjusts levels: maps input range [inBlack, inWhite] to output [outBlack, outWhite]. Values are 0.0-1.0
    /// normalized.
    /// </summary>
    public static ImageFrame Levels(ImageFrame source,
        double inBlack = 0.0, double inWhite = 1.0,
        double outBlack = 0.0, double outWhite = 1.0,
        double midGamma = 1.0)
    {
        double range = inWhite - inBlack;
        if (range <= 0)
        {
            range = 1.0;
        }

        double invGamma = 1.0 / midGamma;

        return ApplyPerPixel(source, val =>
        {
            double normalized = Math.Clamp((val - inBlack) / range, 0.0, 1.0);
            double gammaCorrected = Math.Pow(normalized, invGamma);
            return outBlack + gammaCorrected * (outWhite - outBlack);
        });
    }

    /// <summary>
    /// Reduces the number of distinct color levels per channel.
    /// </summary>
    public static ImageFrame Posterize(ImageFrame source, int levels = 4)
    {
        if (levels < 2)
        {
            levels = 2;
        }

        double step = 1.0 / (levels - 1);

        return ApplyPerPixel(source, val =>
        {
            int quantized = (int)Math.Round(val / step);
            return quantized * step;
        });
    }

    /// <summary>
    /// Adjusts color saturation. 0.0 = grayscale, 1.0 = no change, &gt;1.0 = oversaturated.
    /// </summary>
    public static ImageFrame Saturate(ImageFrame source, double factor)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                double r = srcRow[off] * Quantum.Scale;
                double g = srcRow[off + 1] * Quantum.Scale;
                double b = srcRow[off + 2] * Quantum.Scale;

                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;

                dstRow[off] = (ushort)Math.Clamp((lum + factor * (r - lum)) * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp((lum + factor * (g - lum)) * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp((lum + factor * (b - lum)) * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);

                if (channels > 3)
                {
                    dstRow[off + 3] = srcRow[off + 3];
                }
            }
        });

        return result;
    }

    // ─── Internal Helper ─────────────────────────────────────────

    /// <summary>
    /// Applies a per-channel color function (value in 0.0-1.0 range). Only processes RGB channels, preserves alpha.
    /// Parallelized across scanlines.
    /// </summary>
    private static ImageFrame ApplyPerPixel(ImageFrame source, Func<double, double> transform)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int colorChannels = Math.Min(channels, 3);

                for (int c = 0;c < colorChannels;c++)
                {
                    double val = srcRow[off + c] * Quantum.Scale;
                    double adjusted = transform(val);
                    dstRow[off + c] = (ushort)Math.Clamp(adjusted * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                }

                if (channels > 3)
                {
                    dstRow[off + 3] = srcRow[off + 3];
                }
            }
        });

        return result;
    }

    // ─── Color Matrix ────────────────────────────────────────────

    /// <summary>
    /// Applies an NxN color matrix transformation to each pixel. The matrix multiplies the pixel's
    /// channel values to produce new channel values. Supports 3×3 (RGB), 4×4 (RGBA), and 5×5
    /// (RGBA + offset) matrices. For a 5×5 matrix, the 5th column is an offset scaled by QuantumRange.
    /// Follows ImageMagick's ColorMatrixImage.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="matrix">NxN transformation matrix (row-major). Rows are output channels, columns are input channels.</param>
    public static ImageFrame ColorMatrix(ImageFrame source, double[,] matrix)
    {
        int matRows = matrix.GetLength(0);
        int matCols = matrix.GetLength(1);
        if (matRows != matCols || matRows < 3 || matRows > 6)
            throw new ArgumentException("Color matrix must be square, 3×3 to 6×6.", nameof(matrix));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;
        bool hasOffset = matCols > colorChannels + (source.HasAlpha ? 1 : 0);

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            // Flatten channel values for matrix multiply
            Span<double> inputChannels = stackalloc double[7]; // max 6 channels + 1 offset
            Span<double> outputChannels = stackalloc double[6];

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                // Clear input channels
                inputChannels.Clear();

                // Fill input channels (normalized to 0..1)
                for (int c = 0; c < channels && c < matCols; c++)
                    inputChannels[c] = srcRow[off + c] * Quantum.Scale;

                // If matrix is larger than channel count, last column is offset (multiply by 1.0)
                if (matCols > channels)
                    inputChannels[matCols - 1] = 1.0;

                // Matrix multiply: output[i] = sum(matrix[i,j] * input[j])
                int outCount = Math.Min(matRows, channels);
                for (int i = 0; i < outCount; i++)
                {
                    double sum = 0;
                    for (int j = 0; j < matCols; j++)
                        sum += matrix[i, j] * inputChannels[j];
                    outputChannels[i] = sum;
                }

                // Write output channels, clamped
                for (int c = 0; c < channels; c++)
                {
                    if (c < outCount)
                        dstRow[off + c] = (ushort)Math.Clamp(outputChannels[c] * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                    else
                        dstRow[off + c] = srcRow[off + c]; // pass through extra channels unchanged
                }
            }
        });

        return result;
    }

    // ─── Bundle C: Curves, Dodge/Burn, Exposure, Vibrance, Dehaze ──

    /// <summary>
    /// Applies a custom tone curve defined by control points. Points are (input, output) pairs
    /// normalized to [0,1]. Uses monotone cubic interpolation for smooth transitions.
    /// </summary>
    public static ImageFrame Curves(ImageFrame source, (double input, double output)[] controlPoints)
    {
        if (controlPoints.Length < 2)
            throw new ArgumentException("At least 2 control points required.", nameof(controlPoints));

        // Sort control points by input value
        var sorted = controlPoints.OrderBy(p => p.input).ToArray();

        // Build a 65536-entry lookup table via monotone cubic interpolation
        var lut = new ushort[Quantum.MaxValue + 1];
        BuildCurveLut(sorted, lut);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = lut[srcRow[off + c]];
                if (source.HasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    /// <summary>
    /// Dodge (lighten) operation: lightens pixels based on a grayscale intensity map.
    /// exposure controls the strength (0-1). Midtones, shadows, and highlights can be targeted.
    /// Range selects tonal range: 0=shadows, 0.5=midtones, 1=highlights.
    /// </summary>
    public static ImageFrame Dodge(ImageFrame source, double exposure = 0.5, double range = 0.5, double radius = 0.0)
    {
        return DodgeBurnCore(source, exposure, range, radius, isDodge: true);
    }

    /// <summary>
    /// Burn (darken) operation: darkens pixels based on tonal range.
    /// exposure controls the strength (0-1). Range selects tonal target.
    /// </summary>
    public static ImageFrame Burn(ImageFrame source, double exposure = 0.5, double range = 0.5, double radius = 0.0)
    {
        return DodgeBurnCore(source, exposure, range, radius, isDodge: false);
    }

    /// <summary>
    /// Adjusts exposure in EV stops. Positive = brighter, negative = darker.
    /// Simulates camera exposure compensation with gamma-aware math.
    /// </summary>
    public static ImageFrame Exposure(ImageFrame source, double evStops)
    {
        double multiplier = Math.Pow(2.0, evStops);
        return ApplyPerPixel(source, val => val * multiplier);
    }

    /// <summary>
    /// Adjusts vibrance — a smart saturation that protects already-saturated colors and skin tones.
    /// amount ranges from -1 (desaturate) to +1 (saturate). 0 = no change.
    /// </summary>
    public static ImageFrame Vibrance(ImageFrame source, double amount)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        if (channels < 3) return Saturate(source, 1.0 + amount);

        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double r = srcRow[off] * Quantum.Scale;
                double g = srcRow[off + 1] * Quantum.Scale;
                double b = srcRow[off + 2] * Quantum.Scale;

                double maxC = Math.Max(r, Math.Max(g, b));
                double minC = Math.Min(r, Math.Min(g, b));
                double currentSat = maxC > 1e-10 ? (maxC - minC) / maxC : 0;

                // Scale amount inversely with current saturation
                double adjustedAmount = amount * (1.0 - currentSat);
                double lum = r * 0.2126 + g * 0.7152 + b * 0.0722;

                double nr = lum + (r - lum) * (1.0 + adjustedAmount);
                double ng = lum + (g - lum) * (1.0 + adjustedAmount);
                double nb = lum + (b - lum) * (1.0 + adjustedAmount);

                dstRow[off] = (ushort)Math.Clamp(nr * Quantum.MaxValue, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(ng * Quantum.MaxValue, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(nb * Quantum.MaxValue, 0, Quantum.MaxValue);

                for (int c = 3; c < channels; c++)
                    dstRow[off + c] = srcRow[off + c];
            }
        });

        return result;
    }

    /// <summary>
    /// Removes haze from an image using the dark channel prior method.
    /// strength controls how aggressively haze is removed (0-1).
    /// </summary>
    public static ImageFrame Dehaze(ImageFrame source, double strength = 0.5, int patchRadius = 7)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        if (channels < 3) return ApplyPerPixel(source, val => val * (1.0 + strength * 0.5));

        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        // Step 1: Compute dark channel (minimum of RGB in local patch)
        var darkChannel = new double[height * width];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double minVal = double.MaxValue;
                for (int dy = -patchRadius; dy <= patchRadius; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, height - 1);
                    var nRow = source.GetPixelRow(ny);
                    for (int dx = -patchRadius; dx <= patchRadius; dx++)
                    {
                        int nx = Math.Clamp(x + dx, 0, width - 1);
                        int nOff = nx * channels;
                        for (int c = 0; c < 3; c++)
                        {
                            double v = nRow[nOff + c] * Quantum.Scale;
                            if (v < minVal) minVal = v;
                        }
                    }
                }
                darkChannel[y * width + x] = minVal;
            }
        });

        // Step 2: Estimate atmospheric light from brightest dark channel pixels
        int pixelCount = width * height;
        int topCount = Math.Max(1, pixelCount / 1000);

        // Use partial sort instead of full LINQ OrderByDescending — find top-N threshold
        var darkCopy = ArrayPool<double>.Shared.Rent(pixelCount);
        Array.Copy(darkChannel, darkCopy, pixelCount);
        Array.Sort(darkCopy, 0, pixelCount);
        double threshold = darkCopy[pixelCount - topCount];
        ArrayPool<double>.Shared.Return(darkCopy);

        double atmR = 0, atmG = 0, atmB = 0;
        int atmCount = 0;
        for (int i = 0; i < pixelCount && atmCount < topCount; i++)
        {
            if (darkChannel[i] >= threshold)
            {
                int ax = i % width, ay = i / width;
                var aRow = source.GetPixelRow(ay);
                int aOff = ax * channels;
                atmR += aRow[aOff] * Quantum.Scale;
                atmG += aRow[aOff + 1] * Quantum.Scale;
                atmB += aRow[aOff + 2] * Quantum.Scale;
                atmCount++;
            }
        }
        if (atmCount > 0) { atmR /= atmCount; atmG /= atmCount; atmB /= atmCount; }
        double atmMax = Math.Max(atmR, Math.Max(atmG, atmB));
        if (atmMax < 0.1) atmMax = 1.0;

        // Step 3: Compute transmission and recover scene
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        double omega = strength; // controls haze removal aggressiveness

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double transmission = 1.0 - omega * darkChannel[y * width + x];
                transmission = Math.Max(transmission, 0.1); // prevent division by near-zero

                double r = srcRow[off] * Quantum.Scale;
                double g = srcRow[off + 1] * Quantum.Scale;
                double b = srcRow[off + 2] * Quantum.Scale;

                double nr = (r - atmR) / transmission + atmR;
                double ng = (g - atmG) / transmission + atmG;
                double nb = (b - atmB) / transmission + atmB;

                dstRow[off] = (ushort)Math.Clamp(nr * Quantum.MaxValue, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(ng * Quantum.MaxValue, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(nb * Quantum.MaxValue, 0, Quantum.MaxValue);

                for (int c = 3; c < channels; c++)
                    dstRow[off + c] = srcRow[off + c];
            }
        });

        return result;
    }

    // ─── Curves/DodgeBurn helpers ───────────────────────────────────

    private static void BuildCurveLut((double input, double output)[] points, ushort[] lut)
    {
        // Monotone cubic (Fritsch-Carlson) interpolation
        int n = points.Length;
        var dx = new double[n - 1];
        var dy = new double[n - 1];
        var m = new double[n - 1]; // slopes of secants

        for (int i = 0; i < n - 1; i++)
        {
            dx[i] = points[i + 1].input - points[i].input;
            dy[i] = points[i + 1].output - points[i].output;
            m[i] = dx[i] > 1e-12 ? dy[i] / dx[i] : 0;
        }

        // Tangents at each control point
        var tangents = new double[n];
        tangents[0] = m[0];
        tangents[n - 1] = m[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            if (m[i - 1] * m[i] <= 0)
                tangents[i] = 0;
            else
                tangents[i] = (m[i - 1] + m[i]) * 0.5;
        }

        // Fritsch-Carlson monotonicity correction
        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(m[i]) < 1e-12)
            {
                tangents[i] = 0;
                tangents[i + 1] = 0;
            }
            else
            {
                double alpha = tangents[i] / m[i];
                double beta = tangents[i + 1] / m[i];
                double s = alpha * alpha + beta * beta;
                if (s > 9)
                {
                    double tau = 3.0 / Math.Sqrt(s);
                    tangents[i] = tau * alpha * m[i];
                    tangents[i + 1] = tau * beta * m[i];
                }
            }
        }

        // Evaluate the spline for every quantum value
        for (int v = 0; v <= Quantum.MaxValue; v++)
        {
            double t = v * Quantum.Scale;

            // Find segment
            int seg = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (t >= points[i].input && (i == n - 2 || t < points[i + 1].input))
                { seg = i; break; }
            }

            // Hermite interpolation
            double h = dx[seg];
            if (h < 1e-12)
            {
                lut[v] = (ushort)Math.Clamp(points[seg].output * Quantum.MaxValue, 0, Quantum.MaxValue);
                continue;
            }

            double s2 = (t - points[seg].input) / h;
            double s3 = s2 * s2;
            double s4 = s3 * s2;

            double h00 = 2 * s4 - 3 * s3 + 1;
            double h10 = s4 - 2 * s3 + s2;
            double h01 = -2 * s4 + 3 * s3;
            double h11 = s4 - s3;

            double result = h00 * points[seg].output + h10 * h * tangents[seg]
                          + h01 * points[seg + 1].output + h11 * h * tangents[seg + 1];

            lut[v] = (ushort)Math.Clamp(result * Quantum.MaxValue, 0, Quantum.MaxValue);
        }
    }

    private static ImageFrame DodgeBurnCore(ImageFrame source, double exposure, double range, double radius, bool isDodge)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                // Compute luminance to determine how much dodge/burn to apply
                double lum;
                if (colorChannels >= 3)
                    lum = (srcRow[off] * 0.2126 + srcRow[off + 1] * 0.7152 + srcRow[off + 2] * 0.0722) * Quantum.Scale;
                else
                    lum = srcRow[off] * Quantum.Scale;

                // Tonal targeting: Gaussian around the target range
                double distance = lum - range;
                double weight = Math.Exp(-(distance * distance) / (2.0 * 0.15 * 0.15));
                double adjustedExposure = exposure * weight;

                for (int c = 0; c < colorChannels; c++)
                {
                    double v = srcRow[off + c] * Quantum.Scale;
                    double adjusted;
                    if (isDodge)
                    {
                        // Color dodge formula: result = base / (1 - dodge)
                        double dodge = adjustedExposure;
                        adjusted = dodge < 1.0 ? v / (1.0 - dodge * 0.5) : 1.0;
                    }
                    else
                    {
                        // Color burn formula: result = 1 - (1 - base) / burn
                        double burn = 1.0 - adjustedExposure * 0.5;
                        adjusted = burn > 0 ? 1.0 - (1.0 - v) / burn : 0.0;
                    }
                    dstRow[off + c] = (ushort)Math.Clamp(adjusted * Quantum.MaxValue, 0, Quantum.MaxValue);
                }

                if (source.HasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }
}
