// SharpImage — Color grading and creative color tools.
// Color transfer, split toning, gradient map, channel mixer, photo filter, duotone.
// Bundle I of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Color grading and creative tools: color transfer between images, split toning,
/// gradient map, channel mixer, photo filter, and duotone effects.
/// </summary>
public static class ColorGradingOps
{
    // ─── Color Transfer (Reinhard et al.) ──────────────────────────

    /// <summary>
    /// Transfers the color characteristics of the reference image onto the source image
    /// using the Reinhard et al. method: convert to LAB, match mean/stddev per channel.
    /// strength controls the transfer amount (0.0 = no change, 1.0 = full transfer).
    /// </summary>
    public static ImageFrame ColorTransfer(ImageFrame source, ImageFrame reference, double strength = 1.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int refWidth = (int)reference.Columns;
        int refHeight = (int)reference.Rows;
        int refChannels = reference.NumberOfChannels;

        // Extract LAB-like values (simplified: use linear luminance, a*, b*)
        var srcLab = ExtractLabValues(source);
        var refLab = ExtractLabValues(reference);

        // Compute statistics for each channel
        var srcMean = new double[3];
        var srcStd = new double[3];
        var refMean = new double[3];
        var refStd = new double[3];

        ComputeMeanStd(srcLab, width * height, srcMean, srcStd);
        ComputeMeanStd(refLab, refWidth * refHeight, refMean, refStd);

        // Transfer: for each pixel, normalize by source stats, scale by reference stats
        int pixelCount = width * height;
        Parallel.For(0, pixelCount, i =>
        {
            for (int c = 0; c < 3; c++)
            {
                double srcVal = srcLab[i * 3 + c];
                double normalized = srcStd[c] > 1e-10 ? (srcVal - srcMean[c]) / srcStd[c] : 0;
                double transferred = normalized * refStd[c] + refMean[c];
                double blended = srcVal + (transferred - srcVal) * strength;
                srcLab[i * 3 + c] = blended;
            }
        });

        // Convert back to RGB
        return LabToImageFrame(srcLab, width, height, source.HasAlpha, source);
    }

    // ─── Split Toning ──────────────────────────────────────────────

    /// <summary>
    /// Applies split toning: tints shadows with one color and highlights with another.
    /// shadowColor and highlightColor are RGB ushort tuples.
    /// balance controls the shadow/highlight boundary (0.0 = all shadow tint, 1.0 = all highlight tint).
    /// </summary>
    public static ImageFrame SplitToning(ImageFrame source,
        ushort shadowR, ushort shadowG, ushort shadowB,
        ushort highlightR, ushort highlightG, ushort highlightB,
        double balance = 0.5, double strength = 0.3)
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

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                // Compute luminance
                double lum;
                if (channels >= 3)
                    lum = (srcRow[off] * 0.2126 + srcRow[off + 1] * 0.7152 + srcRow[off + 2] * 0.0722) * Quantum.Scale;
                else
                    lum = srcRow[off] * Quantum.Scale;

                // Compute shadow/highlight weights based on luminance and balance
                double shadowWeight = Math.Max(0, 1.0 - lum / balance) * strength;
                double highlightWeight = Math.Max(0, (lum - balance) / (1.0 - balance)) * strength;

                if (channels >= 3)
                {
                    dstRow[off] = Quantum.Clamp((int)(srcRow[off]
                        + shadowWeight * (shadowR - srcRow[off])
                        + highlightWeight * (highlightR - srcRow[off])));
                    dstRow[off + 1] = Quantum.Clamp((int)(srcRow[off + 1]
                        + shadowWeight * (shadowG - srcRow[off + 1])
                        + highlightWeight * (highlightG - srcRow[off + 1])));
                    dstRow[off + 2] = Quantum.Clamp((int)(srcRow[off + 2]
                        + shadowWeight * (shadowB - srcRow[off + 2])
                        + highlightWeight * (highlightB - srcRow[off + 2])));
                }
                else
                {
                    dstRow[off] = srcRow[off];
                }

                // Copy alpha if present
                if (source.HasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ─── Gradient Map ──────────────────────────────────────────────

    /// <summary>
    /// Maps pixel luminance values to colors from a gradient defined by stops.
    /// Each stop is (position 0-1, R, G, B). Creates a 65536-entry LUT and applies it.
    /// </summary>
    public static ImageFrame GradientMap(ImageFrame source, (double position, ushort r, ushort g, ushort b)[] stops)
    {
        if (stops.Length < 2)
            throw new ArgumentException("At least two gradient stops are required.");

        // Sort by position
        var sorted = stops.OrderBy(s => s.position).ToArray();

        // Build 65536-entry RGB LUT
        var lutR = new ushort[65536];
        var lutG = new ushort[65536];
        var lutB = new ushort[65536];

        for (int i = 0; i < 65536; i++)
        {
            double t = i / 65535.0;

            // Find surrounding stops
            int idx = 0;
            while (idx < sorted.Length - 2 && sorted[idx + 1].position < t)
                idx++;

            double t0 = sorted[idx].position;
            double t1 = sorted[idx + 1].position;
            double frac = (t1 > t0) ? (t - t0) / (t1 - t0) : 0;
            frac = Math.Clamp(frac, 0, 1);

            lutR[i] = (ushort)(sorted[idx].r + (sorted[idx + 1].r - sorted[idx].r) * frac);
            lutG[i] = (ushort)(sorted[idx].g + (sorted[idx + 1].g - sorted[idx].g) * frac);
            lutB[i] = (ushort)(sorted[idx].b + (sorted[idx + 1].b - sorted[idx].b) * frac);
        }

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.RGB, source.HasAlpha);
        int dstChannels = result.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int srcOff = x * channels;
                int dstOff = x * dstChannels;

                // Compute luminance index
                int lum;
                if (channels >= 3)
                    lum = (int)(srcRow[srcOff] * 0.2126 + srcRow[srcOff + 1] * 0.7152 + srcRow[srcOff + 2] * 0.0722);
                else
                    lum = srcRow[srcOff];
                lum = Math.Clamp(lum, 0, 65535);

                dstRow[dstOff] = lutR[lum];
                dstRow[dstOff + 1] = lutG[lum];
                dstRow[dstOff + 2] = lutB[lum];

                if (source.HasAlpha)
                    dstRow[dstOff + 3] = srcRow[srcOff + channels - 1];
            }
        });

        return result;
    }

    // ─── Channel Mixer ─────────────────────────────────────────────

    /// <summary>
    /// Remixes RGB channels using a 3×3 matrix.
    /// outR = rr*R + rg*G + rb*B
    /// outG = gr*R + gg*G + gb*B
    /// outB = br*R + bg*G + bb*B
    /// For creative B&amp;W: set all rows to same luminance weights.
    /// </summary>
    public static ImageFrame ChannelMixer(ImageFrame source,
        double rr, double rg, double rb,
        double gr, double gg, double gb,
        double br, double bg, double bb)
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

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                if (channels >= 3)
                {
                    double r = srcRow[off], g = srcRow[off + 1], b = srcRow[off + 2];
                    dstRow[off] = Quantum.Clamp((int)(rr * r + rg * g + rb * b));
                    dstRow[off + 1] = Quantum.Clamp((int)(gr * r + gg * g + gb * b));
                    dstRow[off + 2] = Quantum.Clamp((int)(br * r + bg * g + bb * b));
                }
                else
                {
                    dstRow[off] = srcRow[off];
                }

                if (source.HasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ─── Photo Filter ──────────────────────────────────────────────

    /// <summary>
    /// Applies a color overlay filter (warming, cooling, or custom color).
    /// Uses luminance-preserving blending so the image isn't just tinted.
    /// density controls the filter strength (0.0–1.0).
    /// </summary>
    public static ImageFrame PhotoFilter(ImageFrame source,
        ushort filterR, ushort filterG, ushort filterB, double density = 0.25)
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

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                if (channels >= 3)
                {
                    // Multiply blend for color filter effect
                    double r = srcRow[off] * (1.0 - density) + (srcRow[off] * filterR * Quantum.Scale) * density;
                    double g = srcRow[off + 1] * (1.0 - density) + (srcRow[off + 1] * filterG * Quantum.Scale) * density;
                    double b = srcRow[off + 2] * (1.0 - density) + (srcRow[off + 2] * filterB * Quantum.Scale) * density;

                    // Preserve luminance
                    double origLum = srcRow[off] * 0.2126 + srcRow[off + 1] * 0.7152 + srcRow[off + 2] * 0.0722;
                    double newLum = r * 0.2126 + g * 0.7152 + b * 0.0722;
                    double lumRatio = newLum > 0 ? origLum / newLum : 1.0;

                    dstRow[off] = Quantum.Clamp((int)(r * lumRatio));
                    dstRow[off + 1] = Quantum.Clamp((int)(g * lumRatio));
                    dstRow[off + 2] = Quantum.Clamp((int)(b * lumRatio));
                }
                else
                {
                    dstRow[off] = srcRow[off];
                }

                if (source.HasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ─── Duotone ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a duotone effect: maps shadows to one color and highlights to another.
    /// Like a gradient map with exactly two stops (black → shadow color, white → highlight color).
    /// </summary>
    public static ImageFrame Duotone(ImageFrame source,
        ushort darkR, ushort darkG, ushort darkB,
        ushort lightR, ushort lightG, ushort lightB)
    {
        return GradientMap(source, [
            (0.0, darkR, darkG, darkB),
            (1.0, lightR, lightG, lightB)
        ]);
    }

    // ─── LAB Helpers ───────────────────────────────────────────────

    /// <summary>
    /// Extracts simplified LAB values from an image.
    /// Uses a perceptually-motivated transform: L=luminance, a=R-G opponent, b=B-Y opponent.
    /// </summary>
    private static double[] ExtractLabValues(ImageFrame frame)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        int count = width * height;
        var lab = new double[count * 3];

        Parallel.For(0, height, y =>
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                int idx = (y * width + x) * 3;

                double r, g, b;
                if (channels >= 3)
                {
                    r = row[off] * Quantum.Scale;
                    g = row[off + 1] * Quantum.Scale;
                    b = row[off + 2] * Quantum.Scale;
                }
                else
                {
                    r = g = b = row[off] * Quantum.Scale;
                }

                // Rudakov-style LAB approximation
                lab[idx] = 0.5774 * r + 0.5774 * g + 0.5774 * b;       // L
                lab[idx + 1] = 0.4082 * r + 0.4082 * g - 0.8165 * b;   // a
                lab[idx + 2] = 0.7071 * r - 0.7071 * g;                 // b
            }
        });

        return lab;
    }

    private static void ComputeMeanStd(double[] data, int pixelCount, double[] mean, double[] std)
    {
        mean[0] = mean[1] = mean[2] = 0;
        std[0] = std[1] = std[2] = 0;

        if (pixelCount <= 0)
            return;

        for (int i = 0; i < pixelCount; i++)
            for (int c = 0; c < 3; c++)
                mean[c] += data[i * 3 + c];
        for (int c = 0; c < 3; c++)
            mean[c] /= pixelCount;

        std[0] = std[1] = std[2] = 0;
        for (int i = 0; i < pixelCount; i++)
            for (int c = 0; c < 3; c++)
            {
                double d = data[i * 3 + c] - mean[c];
                std[c] += d * d;
            }
        for (int c = 0; c < 3; c++)
            std[c] = Math.Sqrt(std[c] / pixelCount);
    }

    /// <summary>
    /// Converts LAB values back to an ImageFrame.
    /// </summary>
    private static ImageFrame LabToImageFrame(double[] lab, int width, int height, bool hasAlpha, ImageFrame alphaSource)
    {
        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.RGB, hasAlpha);
        int dstChannels = result.NumberOfChannels;
        int srcChannels = alphaSource.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            var srcRow = alphaSource.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 3;
                int dstOff = x * dstChannels;
                int srcOff = x * srcChannels;

                double l = lab[idx];
                double a = lab[idx + 1];
                double b = lab[idx + 2];

                // Inverse transform
                double r = 0.5774 * l + 0.4082 * a + 0.7071 * b;
                double g = 0.5774 * l + 0.4082 * a - 0.7071 * b;
                double bl = 0.5774 * l - 0.8165 * a;

                dstRow[dstOff] = Quantum.Clamp((int)(r * Quantum.MaxValue));
                dstRow[dstOff + 1] = Quantum.Clamp((int)(g * Quantum.MaxValue));
                dstRow[dstOff + 2] = Quantum.Clamp((int)(bl * Quantum.MaxValue));

                if (hasAlpha)
                    dstRow[dstOff + 3] = srcRow[srcOff + srcChannels - 1];
            }
        });

        return result;
    }
}
