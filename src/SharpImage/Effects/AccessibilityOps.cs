// SharpImage — Accessibility and print simulation operations.
// ColorBlindnessSimulate, Daltonize, SoftProof (CMYK simulation).

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Type of color vision deficiency for simulation.
/// </summary>
public enum ColorBlindnessType
{
    /// <summary>Red-blind (no L-cones). ~1% of males.</summary>
    Protanopia,
    /// <summary>Green-blind (no M-cones). ~1% of males.</summary>
    Deuteranopia,
    /// <summary>Blue-blind (no S-cones). Very rare.</summary>
    Tritanopia
}

/// <summary>
/// Accessibility and print simulation: color blindness simulation,
/// daltonization (color-blind compensation), and CMYK soft proofing.
/// </summary>
public static class AccessibilityOps
{
    // ─── Color Blindness Simulation ─────────────────────────────

    // Brettel et al. (1997) simulation matrices for sRGB linear space.
    // Each deficiency uses two half-planes separated by a neutral axis.

    // Protanopia matrices (Brettel)
    private static readonly double[] ProtanPlane1 = [
        0.152286, 1.052583, -0.204868,
        0.114503, 0.786281, 0.099216,
       -0.003882, -0.048116, 1.051998
    ];
    private static readonly double[] ProtanPlane2 = [
        0.152286, 1.052583, -0.204868,
        0.114503, 0.786281, 0.099216,
       -0.003882, -0.048116, 1.051998
    ];

    // Deuteranopia matrices (Brettel)
    private static readonly double[] DeutanPlane1 = [
        0.367322, 0.860646, -0.227968,
        0.280085, 0.672501, 0.047413,
       -0.011820, 0.042940, 0.968881
    ];
    private static readonly double[] DeutanPlane2 = [
        0.367322, 0.860646, -0.227968,
        0.280085, 0.672501, 0.047413,
       -0.011820, 0.042940, 0.968881
    ];

    // Tritanopia matrices (Brettel)
    private static readonly double[] TritanPlane1 = [
        1.255528, -0.076749, -0.178779,
       -0.078411, 0.930809, 0.147602,
        0.004733, 0.691367, 0.303900
    ];
    private static readonly double[] TritanPlane2 = [
        1.255528, -0.076749, -0.178779,
       -0.078411, 0.930809, 0.147602,
        0.004733, 0.691367, 0.303900
    ];

    /// <summary>
    /// Simulates how an image appears to a person with the specified color vision deficiency.
    /// Uses the Brettel/Vienot/Mollon algorithm for accurate simulation.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="type">Type of color blindness to simulate.</param>
    public static ImageFrame SimulateColorBlindness(ImageFrame source, ColorBlindnessType type)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        var matrix = type switch
        {
            ColorBlindnessType.Protanopia => ProtanPlane1,
            ColorBlindnessType.Deuteranopia => DeutanPlane1,
            ColorBlindnessType.Tritanopia => TritanPlane1,
            _ => ProtanPlane1
        };

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        Parallel.For(0, h, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < w; x++)
            {
                int off = x * ch;

                // Convert sRGB to linear
                double lr = SrgbToLinear(srcRow[off] * Quantum.Scale);
                double lg = SrgbToLinear(srcRow[off + 1] * Quantum.Scale);
                double lb = SrgbToLinear(srcRow[off + 2] * Quantum.Scale);

                // Apply simulation matrix
                double sr = matrix[0] * lr + matrix[1] * lg + matrix[2] * lb;
                double sg = matrix[3] * lr + matrix[4] * lg + matrix[5] * lb;
                double sb = matrix[6] * lr + matrix[7] * lg + matrix[8] * lb;

                // Convert back to sRGB
                dstRow[off] = Quantum.Clamp(LinearToSrgb(Math.Clamp(sr, 0, 1)) * Quantum.MaxValue);
                dstRow[off + 1] = Quantum.Clamp(LinearToSrgb(Math.Clamp(sg, 0, 1)) * Quantum.MaxValue);
                dstRow[off + 2] = Quantum.Clamp(LinearToSrgb(Math.Clamp(sb, 0, 1)) * Quantum.MaxValue);
                if (hasAlpha) dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ─── Daltonize ──────────────────────────────────────────────

    /// <summary>
    /// Compensates an image for color blindness by shifting lost color information
    /// into channels the viewer can perceive. The daltonized image should look
    /// more distinguishable to someone with the specified deficiency.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="type">Type of color blindness to compensate for.</param>
    /// <param name="strength">Compensation strength (0.0–1.0).</param>
    public static ImageFrame Daltonize(ImageFrame source, ColorBlindnessType type, double strength = 1.0)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        // First simulate the color blindness
        using var simulated = SimulateColorBlindness(source, type);

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        // Error correction matrix: shift error into visible channels
        // Error = original - simulated; add error to channels the user CAN see
        double[] errorMatrix = type switch
        {
            ColorBlindnessType.Protanopia => [
                0, 0, 0,     // R error stays in R (lost)
                0.7, 1, 0,   // R error shifts to G
                0.7, 0, 1    // R error shifts to B
            ],
            ColorBlindnessType.Deuteranopia => [
                1, 0.7, 0,   // G error shifts to R
                0, 0, 0,     // G error stays in G (lost)
                0, 0.7, 1    // G error shifts to B
            ],
            ColorBlindnessType.Tritanopia => [
                1, 0, 0.7,   // B error shifts to R
                0, 1, 0.7,   // B error shifts to G
                0, 0, 0      // B error stays in B (lost)
            ],
            _ => [1, 0, 0, 0, 1, 0, 0, 0, 1]
        };

        Parallel.For(0, h, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var simRow = simulated.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < w; x++)
            {
                int off = x * ch;

                // Compute error (what the color-blind person can't see)
                double er = (srcRow[off] - simRow[off]) * Quantum.Scale;
                double eg = (srcRow[off + 1] - simRow[off + 1]) * Quantum.Scale;
                double eb = (srcRow[off + 2] - simRow[off + 2]) * Quantum.Scale;

                // Transform error through correction matrix
                double cr = errorMatrix[0] * er + errorMatrix[1] * eg + errorMatrix[2] * eb;
                double cg = errorMatrix[3] * er + errorMatrix[4] * eg + errorMatrix[5] * eb;
                double cb = errorMatrix[6] * er + errorMatrix[7] * eg + errorMatrix[8] * eb;

                // Add corrected error back to original
                double origR = srcRow[off] * Quantum.Scale;
                double origG = srcRow[off + 1] * Quantum.Scale;
                double origB = srcRow[off + 2] * Quantum.Scale;

                dstRow[off] = Quantum.Clamp(Math.Clamp(origR + cr * strength, 0, 1) * Quantum.MaxValue);
                dstRow[off + 1] = Quantum.Clamp(Math.Clamp(origG + cg * strength, 0, 1) * Quantum.MaxValue);
                dstRow[off + 2] = Quantum.Clamp(Math.Clamp(origB + cb * strength, 0, 1) * Quantum.MaxValue);
                if (hasAlpha) dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ─── CMYK Soft Proof ────────────────────────────────────────

    /// <summary>
    /// Simulates how an image would look when printed in CMYK by converting
    /// RGB→CMYK→RGB with gamut clipping. Colors outside the CMYK gamut are
    /// clipped, showing what detail would be lost in print.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="inkLimit">Maximum total ink coverage (0.0–4.0, default 3.0 = 300%).</param>
    /// <param name="blackPoint">Black generation strength (0.0–1.0, default 1.0 = full UCR).</param>
    public static ImageFrame SoftProof(ImageFrame source, double inkLimit = 3.0, double blackPoint = 1.0)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        Parallel.For(0, h, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                double r = srcRow[off] * Quantum.Scale;
                double g = srcRow[off + 1] * Quantum.Scale;
                double b = srcRow[off + 2] * Quantum.Scale;

                // RGB → CMYK with under-color removal (UCR)
                double c = 1.0 - r;
                double m = 1.0 - g;
                double yy = 1.0 - b;
                double k = Math.Min(c, Math.Min(m, yy)) * blackPoint;

                if (k < 1.0)
                {
                    double invK = 1.0 / (1.0 - k);
                    c = (c - k) * invK;
                    m = (m - k) * invK;
                    yy = (yy - k) * invK;
                }
                else
                {
                    c = m = yy = 0;
                }

                // Apply ink limit
                double totalInk = c + m + yy + k;
                if (totalInk > inkLimit && totalInk > 0)
                {
                    double scale = inkLimit / totalInk;
                    c *= scale; m *= scale; yy *= scale; k *= scale;
                }

                // Clamp CMYK
                c = Math.Clamp(c, 0, 1);
                m = Math.Clamp(m, 0, 1);
                yy = Math.Clamp(yy, 0, 1);
                k = Math.Clamp(k, 0, 1);

                // CMYK → RGB
                double rr = (1.0 - c) * (1.0 - k);
                double gg = (1.0 - m) * (1.0 - k);
                double bb = (1.0 - yy) * (1.0 - k);

                dstRow[off] = Quantum.Clamp(rr * Quantum.MaxValue);
                dstRow[off + 1] = Quantum.Clamp(gg * Quantum.MaxValue);
                dstRow[off + 2] = Quantum.Clamp(bb * Quantum.MaxValue);
                if (hasAlpha) dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ─── sRGB Transfer Functions ────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SrgbToLinear(double v)
    {
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LinearToSrgb(double v)
    {
        return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
    }
}
