using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Per-pixel mathematical operations matching ImageMagick's statistic.c:
/// EvaluateImage (per-pixel arithmetic), FunctionImage (math functions),
/// StatisticImage (neighborhood statistics), and PolynomialImage.
/// </summary>
public static class MathOps
{
    // ─── Evaluate Operations ─────────────────────────────────────

    /// <summary>
    /// Per-pixel arithmetic operation with a constant value.
    /// Applies the specified operator to every pixel channel: result = op(pixel, value).
    /// </summary>
    public static ImageFrame Evaluate(ImageFrame source, EvaluateOperator op, double value)
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

            for (int i = 0; i < width * channels; i++)
            {
                dstRow[i] = (ushort)Math.Clamp(ApplyEvaluateOp(srcRow[i], op, value), 0, Quantum.MaxValue);
            }
        });

        return result;
    }

    /// <summary>
    /// Combines multiple images using the specified evaluate operator.
    /// For each pixel position, applies the operator across all images (e.g., Mean, Min, Max).
    /// </summary>
    public static ImageFrame EvaluateImages(ReadOnlySpan<ImageFrame> images, EvaluateOperator op)
    {
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required.", nameof(images));

        var first = images[0];
        int width = (int)first.Columns;
        int height = (int)first.Rows;
        int channels = first.NumberOfChannels;
        int count = images.Length;

        var result = new ImageFrame();
        result.Initialize(width, height, first.Colorspace, first.HasAlpha);

        // Copy image references to array for parallel access (Span can't be captured)
        var imageArray = images.ToArray();

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            int rowLength = width * channels;

            for (int i = 0; i < rowLength; i++)
            {
                double accumulated = imageArray[0].GetPixelRow(y)[i];

                for (int img = 1; img < count; img++)
                {
                    double pixel = imageArray[img].GetPixelRow(y)[i];
                    accumulated = CombineEvaluateOp(accumulated, pixel, op, img + 1);
                }

                // For Mean, divide by count at the end
                if (op == EvaluateOperator.Mean)
                    accumulated /= count;

                dstRow[i] = (ushort)Math.Clamp(accumulated, 0, Quantum.MaxValue);
            }
        });

        return result;
    }

    // ─── Function Operations ─────────────────────────────────────

    /// <summary>
    /// Applies a mathematical function to every pixel channel.
    /// Parameters vary by function type (see MathFunction enum).
    /// </summary>
    public static ImageFrame ApplyFunction(ImageFrame source, MathFunction function, double[] parameters)
    {
        if (parameters is null || parameters.Length == 0)
            throw new ArgumentException("Parameters are required.", nameof(parameters));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int i = 0; i < width * channels; i++)
            {
                double normalized = srcRow[i] / (double)Quantum.MaxValue;
                double computed = ApplyMathFunction(normalized, function, parameters);
                dstRow[i] = (ushort)Math.Clamp(computed * Quantum.MaxValue, 0, Quantum.MaxValue);
            }
        });

        return result;
    }

    // ─── Statistic Image ─────────────────────────────────────────

    /// <summary>
    /// Applies a statistical function in a neighborhood window around each pixel.
    /// Window size is (2*radius+1) x (2*radius+1).
    /// </summary>
    public static ImageFrame Statistic(ImageFrame source, StatisticType type, int radius = 1)
    {
        if (radius < 1)
            throw new ArgumentOutOfRangeException(nameof(radius));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int windowSize = 2 * radius + 1;
        int windowArea = windowSize * windowSize;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            Span<ushort> neighborhood = stackalloc ushort[windowArea];

            for (int x = 0; x < width; x++)
            {
                int dstOff = x * channels;

                for (int c = 0; c < channels; c++)
                {
                    int idx = 0;
                    for (int ky = -radius; ky <= radius; ky++)
                    {
                        int sy = Math.Clamp(y + ky, 0, height - 1);
                        var srcRow = source.GetPixelRow(sy);
                        for (int kx = -radius; kx <= radius; kx++)
                        {
                            int sx = Math.Clamp(x + kx, 0, width - 1);
                            neighborhood[idx++] = srcRow[sx * channels + c];
                        }
                    }

                    dstRow[dstOff + c] = ComputeStatistic(neighborhood[..idx], type);
                }
            }
        });

        return result;
    }

    // ─── Polynomial Image ────────────────────────────────────────

    /// <summary>
    /// Weighted polynomial combination of multiple images.
    /// terms is a flat array of [weight0, exponent0, weight1, exponent1, ...].
    /// Result pixel = sum(weight_i * image_i ^ exponent_i) for each pixel position.
    /// </summary>
    public static ImageFrame Polynomial(ReadOnlySpan<ImageFrame> images, double[] terms)
    {
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required.", nameof(images));
        if (terms is null || terms.Length < 2 || terms.Length % 2 != 0)
            throw new ArgumentException("Terms must be pairs of [weight, exponent].", nameof(terms));
        if (terms.Length / 2 > images.Length)
            throw new ArgumentException("More term pairs than images provided.", nameof(terms));

        var first = images[0];
        int width = (int)first.Columns;
        int height = (int)first.Rows;
        int channels = first.NumberOfChannels;
        int termCount = terms.Length / 2;

        var result = new ImageFrame();
        result.Initialize(width, height, first.Colorspace, first.HasAlpha);

        var imageArray = images.ToArray();

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            int rowLength = width * channels;

            for (int i = 0; i < rowLength; i++)
            {
                double sum = 0;
                for (int t = 0; t < termCount; t++)
                {
                    double weight = terms[t * 2];
                    double exponent = terms[t * 2 + 1];
                    double normalized = imageArray[t].GetPixelRow(y)[i] / (double)Quantum.MaxValue;
                    sum += weight * Math.Pow(normalized, exponent);
                }

                dstRow[i] = (ushort)Math.Clamp(sum * Quantum.MaxValue, 0, Quantum.MaxValue);
            }
        });

        return result;
    }

    // ─── Private Helpers ─────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ApplyEvaluateOp(ushort pixel, EvaluateOperator op, double value)
    {
        double p = pixel;
        return op switch
        {
            EvaluateOperator.Add => p + value,
            EvaluateOperator.Subtract => p - value,
            EvaluateOperator.Multiply => p * value,
            EvaluateOperator.Divide => value != 0 ? p / value : p,
            EvaluateOperator.Abs => Math.Abs(p - value),
            EvaluateOperator.Min => Math.Min(p, value),
            EvaluateOperator.Max => Math.Max(p, value),
            EvaluateOperator.Set => value,
            EvaluateOperator.And => (ushort)((ushort)p & (ushort)value),
            EvaluateOperator.Or => (ushort)((ushort)p | (ushort)value),
            EvaluateOperator.Xor => (ushort)((ushort)p ^ (ushort)value),
            EvaluateOperator.LeftShift => (ushort)((ushort)p << (int)value),
            EvaluateOperator.RightShift => (ushort)((ushort)p >> (int)value),
            EvaluateOperator.Log => value > 0 ? Math.Log(p + 1) / Math.Log(value) * Quantum.MaxValue / Math.Log(Quantum.MaxValue + 1) * Math.Log(value) : p,
            EvaluateOperator.Pow => Math.Pow(p / Quantum.MaxValue, value) * Quantum.MaxValue,
            EvaluateOperator.Cosine => (Math.Cos(Math.PI * 2.0 * p / Quantum.MaxValue * value) + 1.0) * Quantum.MaxValue / 2.0,
            EvaluateOperator.Sine => (Math.Sin(Math.PI * 2.0 * p / Quantum.MaxValue * value) + 1.0) * Quantum.MaxValue / 2.0,
            EvaluateOperator.Exponential => Math.Pow(value, p / Quantum.MaxValue) * Quantum.MaxValue,
            EvaluateOperator.ThresholdBlack => p < value ? 0 : p,
            EvaluateOperator.ThresholdWhite => p > value ? Quantum.MaxValue : p,
            EvaluateOperator.Threshold => p > value ? Quantum.MaxValue : 0,
            EvaluateOperator.AddModulus => (p + value) % (Quantum.MaxValue + 1),
            EvaluateOperator.InverseLog => value > 1 ? (Math.Pow(value, p / Quantum.MaxValue) - 1) / (value - 1) * Quantum.MaxValue : p,
            _ => p,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CombineEvaluateOp(double accumulated, double pixel, EvaluateOperator op, int count)
    {
        return op switch
        {
            EvaluateOperator.Add or EvaluateOperator.Mean => accumulated + pixel,
            EvaluateOperator.Subtract => accumulated - pixel,
            EvaluateOperator.Multiply => accumulated * pixel,
            EvaluateOperator.Min => Math.Min(accumulated, pixel),
            EvaluateOperator.Max => Math.Max(accumulated, pixel),
            EvaluateOperator.And => (ushort)((ushort)accumulated & (ushort)pixel),
            EvaluateOperator.Or => (ushort)((ushort)accumulated | (ushort)pixel),
            EvaluateOperator.Xor => (ushort)((ushort)accumulated ^ (ushort)pixel),
            _ => accumulated + pixel,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ApplyMathFunction(double normalizedPixel, MathFunction function, double[] parameters)
    {
        return function switch
        {
            // Polynomial: c0*x^n + c1*x^(n-1) + ... + cn (parameters are coefficients)
            MathFunction.Polynomial => EvaluatePolynomial(normalizedPixel, parameters),

            // Sinusoid: amplitude * sin(2*PI*(frequency*x + phase)) + bias
            MathFunction.Sinusoid => parameters.Length >= 4
                ? parameters[0] * Math.Sin(2.0 * Math.PI * (parameters[1] * normalizedPixel + parameters[2])) + parameters[3]
                : parameters.Length >= 1
                    ? Math.Sin(2.0 * Math.PI * parameters[0] * normalizedPixel)
                    : Math.Sin(2.0 * Math.PI * normalizedPixel),

            // Arcsin: width * arcsin(frequency*x + phase) / PI + bias (range -0.5..0.5)
            MathFunction.Arcsin => parameters.Length >= 4
                ? parameters[0] * Math.Asin(Math.Clamp(parameters[1] * normalizedPixel + parameters[2], -1, 1)) / Math.PI + parameters[3]
                : Math.Asin(Math.Clamp(2.0 * normalizedPixel - 1.0, -1, 1)) / Math.PI + 0.5,

            // Arctan: width * arctan(slope*x + center) / PI + bias (range ~0..1)
            MathFunction.Arctan => parameters.Length >= 4
                ? parameters[0] * Math.Atan(parameters[1] * normalizedPixel + parameters[2]) / Math.PI + parameters[3]
                : Math.Atan(10.0 * (normalizedPixel - 0.5)) / Math.PI + 0.5,

            _ => normalizedPixel,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double EvaluatePolynomial(double x, double[] coefficients)
    {
        // Horner's method: c0*x^n + c1*x^(n-1) + ... + cn
        double result = coefficients[0];
        for (int i = 1; i < coefficients.Length; i++)
        {
            result = result * x + coefficients[i];
        }
        return result;
    }

    private static ushort ComputeStatistic(Span<ushort> values, StatisticType type)
    {
        return type switch
        {
            StatisticType.Minimum => ComputeMin(values),
            StatisticType.Maximum => ComputeMax(values),
            StatisticType.Mean => ComputeMean(values),
            StatisticType.Median => ComputeMedian(values),
            StatisticType.Mode => ComputeMode(values),
            StatisticType.RootMeanSquare => ComputeRMS(values),
            StatisticType.StandardDeviation => ComputeStdDev(values),
            StatisticType.Nonpeak => ComputeNonpeak(values),
            StatisticType.Gradient => ComputeGradient(values),
            StatisticType.Contrast => ComputeContrast(values),
            _ => values[values.Length / 2],
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ComputeMin(Span<ushort> values)
    {
        ushort min = values[0];
        for (int i = 1; i < values.Length; i++)
            if (values[i] < min) min = values[i];
        return min;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ComputeMax(Span<ushort> values)
    {
        ushort max = values[0];
        for (int i = 1; i < values.Length; i++)
            if (values[i] > max) max = values[i];
        return max;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ComputeMean(Span<ushort> values)
    {
        long sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return (ushort)(sum / values.Length);
    }

    private static ushort ComputeMedian(Span<ushort> values)
    {
        values.Sort();
        return values[values.Length / 2];
    }

    private static ushort ComputeMode(Span<ushort> values)
    {
        values.Sort();
        ushort bestValue = values[0];
        int bestCount = 1;
        ushort currentValue = values[0];
        int currentCount = 1;

        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] == currentValue)
            {
                currentCount++;
            }
            else
            {
                if (currentCount > bestCount)
                {
                    bestCount = currentCount;
                    bestValue = currentValue;
                }
                currentValue = values[i];
                currentCount = 1;
            }
        }

        if (currentCount > bestCount)
            bestValue = currentValue;

        return bestValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ComputeRMS(Span<ushort> values)
    {
        double sumSq = 0;
        for (int i = 0; i < values.Length; i++)
            sumSq += (double)values[i] * values[i];
        return (ushort)Math.Clamp(Math.Sqrt(sumSq / values.Length), 0, Quantum.MaxValue);
    }

    private static ushort ComputeStdDev(Span<ushort> values)
    {
        double mean = 0;
        for (int i = 0; i < values.Length; i++)
            mean += values[i];
        mean /= values.Length;

        double variance = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double diff = values[i] - mean;
            variance += diff * diff;
        }
        variance /= values.Length;

        return (ushort)Math.Clamp(Math.Sqrt(variance), 0, Quantum.MaxValue);
    }

    private static ushort ComputeNonpeak(Span<ushort> values)
    {
        // Nonpeak: value that is not the min and not the max (average of middle values)
        values.Sort();
        if (values.Length <= 2)
            return values[values.Length / 2];

        long sum = 0;
        int count = 0;
        ushort min = values[0];
        ushort max = values[values.Length - 1];

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != min && values[i] != max)
            {
                sum += values[i];
                count++;
            }
        }

        return count > 0 ? (ushort)(sum / count) : (ushort)((min + max) / 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ComputeGradient(Span<ushort> values)
    {
        // Gradient = max - min
        ushort min = values[0], max = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }
        return (ushort)(max - min);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ComputeContrast(Span<ushort> values)
    {
        // Contrast: (max - min) / (max + min) scaled to quantum range
        ushort min = values[0], max = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }
        int sum = max + min;
        return sum > 0
            ? (ushort)((long)(max - min) * Quantum.MaxValue / sum)
            : (ushort)0;
    }
}

/// <summary>
/// Per-pixel arithmetic operations matching ImageMagick's MagickEvaluateOperator.
/// </summary>
public enum EvaluateOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Abs,
    Min,
    Max,
    Set,
    And,
    Or,
    Xor,
    LeftShift,
    RightShift,
    Log,
    Pow,
    Cosine,
    Sine,
    Exponential,
    ThresholdBlack,
    ThresholdWhite,
    Threshold,
    AddModulus,
    InverseLog,
    Mean,
}

/// <summary>
/// Mathematical functions for FunctionImage matching ImageMagick's MagickFunction.
/// </summary>
public enum MathFunction
{
    Polynomial,
    Sinusoid,
    Arcsin,
    Arctan,
}

/// <summary>
/// Neighborhood statistical operations matching ImageMagick's StatisticType.
/// </summary>
public enum StatisticType
{
    Minimum,
    Maximum,
    Mean,
    Median,
    Mode,
    RootMeanSquare,
    StandardDeviation,
    Nonpeak,
    Gradient,
    Contrast,
}
