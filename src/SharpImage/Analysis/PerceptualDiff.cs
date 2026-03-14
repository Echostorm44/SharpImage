// SharpImage — Perceptual image comparison.
// SSIM (Structural Similarity Index) and CIE Delta-E 2000 difference maps.
// Produces heatmap visualizations showing where two images differ.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Analysis;

/// <summary>
/// Perceptual image comparison tools: SSIM and Delta-E difference maps.
/// </summary>
public static class PerceptualDiff
{
    /// <summary>Result of a perceptual comparison between two images.</summary>
    public readonly record struct ComparisonResult(
        double MeanSsim,
        double MeanDeltaE,
        double MaxDeltaE,
        double PercentChanged,
        ImageFrame? SsimMap,
        ImageFrame? DeltaEMap
    );

    /// <summary>
    /// Compare two images and produce SSIM and/or Delta-E difference maps.
    /// Images must have the same dimensions.
    /// </summary>
    /// <param name="imageA">First image (reference).</param>
    /// <param name="imageB">Second image (comparison).</param>
    /// <param name="generateSsimMap">If true, produce an SSIM heatmap.</param>
    /// <param name="generateDeltaEMap">If true, produce a Delta-E heatmap.</param>
    /// <param name="deltaEThreshold">Pixels with Delta-E below this are considered unchanged (default 2.0).</param>
    public static ComparisonResult Compare(ImageFrame imageA, ImageFrame imageB,
        bool generateSsimMap = true, bool generateDeltaEMap = true,
        double deltaEThreshold = 2.0)
    {
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;

        if (width != (int)imageB.Columns || height != (int)imageB.Rows)
            throw new ArgumentException("Images must have the same dimensions for perceptual comparison.");

        // Convert both images to floating-point L*a*b* for Delta-E
        var labA = ConvertToLab(imageA);
        var labB = ConvertToLab(imageB);

        // Convert to grayscale for SSIM
        var grayA = ConvertToGrayscale(imageA);
        var grayB = ConvertToGrayscale(imageB);

        // Compute per-pixel Delta-E 2000
        var deltaEValues = new double[height, width];
        double sumDeltaE = 0;
        double maxDeltaE = 0;
        int changedPixels = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double de = DeltaE2000(
                    labA[y, x * 3], labA[y, x * 3 + 1], labA[y, x * 3 + 2],
                    labB[y, x * 3], labB[y, x * 3 + 1], labB[y, x * 3 + 2]
                );
                deltaEValues[y, x] = de;
                sumDeltaE += de;
                if (de > maxDeltaE) maxDeltaE = de;
                if (de > deltaEThreshold) changedPixels++;
            }
        }

        long totalPixels = (long)width * height;
        double meanDeltaE = sumDeltaE / totalPixels;
        double percentChanged = changedPixels * 100.0 / totalPixels;

        // Compute windowed SSIM
        int windowSize = 8;
        var ssimValues = ComputeSsimMap(grayA, grayB, width, height, windowSize);
        double meanSsim = 0;
        int ssimCount = 0;
        int ssimH = height - windowSize + 1;
        int ssimW = width - windowSize + 1;
        for (int y = 0; y < ssimH; y++)
            for (int x = 0; x < ssimW; x++)
            {
                meanSsim += ssimValues[y, x];
                ssimCount++;
            }
        meanSsim = ssimCount > 0 ? meanSsim / ssimCount : 1.0;

        // Generate heatmaps
        ImageFrame? ssimMap = generateSsimMap ? RenderSsimMap(ssimValues, width, height, windowSize) : null;
        ImageFrame? deltaEMap = generateDeltaEMap ? RenderDeltaEMap(deltaEValues, width, height, maxDeltaE) : null;

        return new ComparisonResult(meanSsim, meanDeltaE, maxDeltaE, percentChanged, ssimMap, deltaEMap);
    }

    /// <summary>
    /// Compute SSIM between two images (scalar result only, no maps).
    /// </summary>
    public static double ComputeSsim(ImageFrame imageA, ImageFrame imageB)
    {
        int width = (int)imageA.Columns;
        int height = (int)imageA.Rows;

        if (width != (int)imageB.Columns || height != (int)imageB.Rows)
            throw new ArgumentException("Images must have the same dimensions.");

        var grayA = ConvertToGrayscale(imageA);
        var grayB = ConvertToGrayscale(imageB);

        int windowSize = 8;
        var ssimValues = ComputeSsimMap(grayA, grayB, width, height, windowSize);

        double sum = 0;
        int count = 0;
        int ssimH = height - windowSize + 1;
        int ssimW = width - windowSize + 1;
        for (int y = 0; y < ssimH; y++)
            for (int x = 0; x < ssimW; x++)
            {
                sum += ssimValues[y, x];
                count++;
            }

        return count > 0 ? sum / count : 1.0;
    }

    private static double[,] ComputeSsimMap(double[,] grayA, double[,] grayB,
        int width, int height, int windowSize)
    {
        // SSIM constants for 8-bit equivalent
        double c1 = 0.01 * 0.01; // (k1*L)^2 where L=1.0 (normalized)
        double c2 = 0.03 * 0.03; // (k2*L)^2

        int ssimH = height - windowSize + 1;
        int ssimW = width - windowSize + 1;
        var ssimMap = new double[Math.Max(1, ssimH), Math.Max(1, ssimW)];
        int windowPixels = windowSize * windowSize;

        for (int y = 0; y < ssimH; y++)
        {
            for (int x = 0; x < ssimW; x++)
            {
                double sumA = 0, sumB = 0;
                double sumA2 = 0, sumB2 = 0, sumAB = 0;

                for (int wy = 0; wy < windowSize; wy++)
                {
                    for (int wx = 0; wx < windowSize; wx++)
                    {
                        double a = grayA[y + wy, x + wx];
                        double b = grayB[y + wy, x + wx];
                        sumA += a;
                        sumB += b;
                        sumA2 += a * a;
                        sumB2 += b * b;
                        sumAB += a * b;
                    }
                }

                double meanA = sumA / windowPixels;
                double meanB = sumB / windowPixels;
                double varA = sumA2 / windowPixels - meanA * meanA;
                double varB = sumB2 / windowPixels - meanB * meanB;
                double covAB = sumAB / windowPixels - meanA * meanB;

                double ssim = (2 * meanA * meanB + c1) * (2 * covAB + c2)
                    / ((meanA * meanA + meanB * meanB + c1) * (varA + varB + c2));

                ssimMap[y, x] = Math.Clamp(ssim, 0, 1);
            }
        }

        return ssimMap;
    }

    private static ImageFrame RenderSsimMap(double[,] ssimValues, int width, int height, int windowSize)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        int ssimH = ssimValues.GetLength(0);
        int ssimW = ssimValues.GetLength(1);
        int halfWin = windowSize / 2;

        // Expand SSIM map to full image size
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int sy = Math.Clamp(y - halfWin, 0, ssimH - 1);
                int sx = Math.Clamp(x - halfWin, 0, ssimW - 1);
                double ssim = ssimValues[sy, sx];

                // Green = identical (SSIM=1), Red = different (SSIM=0)
                HeatmapColor(1.0 - ssim, out ushort r, out ushort g, out ushort b);
                int offset = x * 3;
                row[offset] = r;
                row[offset + 1] = g;
                row[offset + 2] = b;
            }
        }

        return frame;
    }

    private static ImageFrame RenderDeltaEMap(double[,] deltaEValues, int width, int height, double maxDeltaE)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        // Normalize to [0, 1] using a perceptual scale
        // Delta-E of 0 = identical, 1 = just noticeable, 5 = clearly different, 10+ = very different
        double scaleMax = Math.Max(maxDeltaE, 10.0);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double normalized = Math.Clamp(deltaEValues[y, x] / scaleMax, 0, 1);
                HeatmapColor(normalized, out ushort r, out ushort g, out ushort b);
                int offset = x * 3;
                row[offset] = r;
                row[offset + 1] = g;
                row[offset + 2] = b;
            }
        }

        return frame;
    }

    /// <summary>Maps a 0-1 value to a heatmap color: black → blue → green → yellow → red → white.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HeatmapColor(double t, out ushort r, out ushort g, out ushort b)
    {
        double rf, gf, bf;
        if (t < 0.01)
        {
            // Near-zero: black (identical)
            rf = gf = bf = 0;
        }
        else if (t < 0.25)
        {
            double s = (t - 0.01) / 0.24;
            rf = 0; gf = 0; bf = s; // black → blue
        }
        else if (t < 0.5)
        {
            double s = (t - 0.25) / 0.25;
            rf = 0; gf = s; bf = 1 - s; // blue → green
        }
        else if (t < 0.75)
        {
            double s = (t - 0.5) / 0.25;
            rf = s; gf = 1; bf = 0; // green → yellow
        }
        else
        {
            double s = (t - 0.75) / 0.25;
            rf = 1; gf = 1 - s; bf = 0; // yellow → red
        }

        r = Quantum.Clamp(rf * Quantum.MaxValue);
        g = Quantum.Clamp(gf * Quantum.MaxValue);
        b = Quantum.Clamp(bf * Quantum.MaxValue);
    }

    private static double[,] ConvertToGrayscale(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.HasAlpha ? 4 : 3;
        double scale = 1.0 / Quantum.MaxValue;

        var gray = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                double r = row[offset] * scale;
                double g = row[offset + 1] * scale;
                double b = row[offset + 2] * scale;
                gray[y, x] = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            }
        }
        return gray;
    }

    private static double[,] ConvertToLab(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.HasAlpha ? 4 : 3;
        double scale = 1.0 / Quantum.MaxValue;

        var lab = new double[height, width * 3];
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                double r = row[offset] * scale;
                double g = row[offset + 1] * scale;
                double b = row[offset + 2] * scale;

                SrgbToLab(r, g, b, out double l, out double a, out double bLab);
                lab[y, x * 3] = l;
                lab[y, x * 3 + 1] = a;
                lab[y, x * 3 + 2] = bLab;
            }
        }
        return lab;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SrgbToLab(double r, double g, double b, out double l, out double a, out double bLab)
    {
        // sRGB → linear
        r = r > 0.04045 ? Math.Pow((r + 0.055) / 1.055, 2.4) : r / 12.92;
        g = g > 0.04045 ? Math.Pow((g + 0.055) / 1.055, 2.4) : g / 12.92;
        b = b > 0.04045 ? Math.Pow((b + 0.055) / 1.055, 2.4) : b / 12.92;

        // Linear RGB → XYZ (D65)
        double x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
        double y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        double z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;

        // Normalize to D65 white point
        x /= 0.95047;
        z /= 1.08883;

        // XYZ → L*a*b*
        x = x > 0.008856 ? Math.Cbrt(x) : 7.787 * x + 16.0 / 116.0;
        y = y > 0.008856 ? Math.Cbrt(y) : 7.787 * y + 16.0 / 116.0;
        z = z > 0.008856 ? Math.Cbrt(z) : 7.787 * z + 16.0 / 116.0;

        l = 116 * y - 16;
        a = 500 * (x - y);
        bLab = 200 * (y - z);
    }

    /// <summary>CIE Delta-E 2000 — perceptually uniform color difference.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double DeltaE2000(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        double c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double cMean = (c1 + c2) / 2.0;

        double cMean7 = Math.Pow(cMean, 7);
        double g = 0.5 * (1 - Math.Sqrt(cMean7 / (cMean7 + 6103515625.0))); // 25^7

        double a1P = a1 * (1 + g);
        double a2P = a2 * (1 + g);

        double c1P = Math.Sqrt(a1P * a1P + b1 * b1);
        double c2P = Math.Sqrt(a2P * a2P + b2 * b2);

        double h1P = Math.Atan2(b1, a1P);
        if (h1P < 0) h1P += 2 * Math.PI;
        double h2P = Math.Atan2(b2, a2P);
        if (h2P < 0) h2P += 2 * Math.PI;

        h1P *= 180 / Math.PI;
        h2P *= 180 / Math.PI;

        double dLP = l2 - l1;
        double dCP = c2P - c1P;

        double dhP;
        if (c1P * c2P == 0)
            dhP = 0;
        else if (Math.Abs(h2P - h1P) <= 180)
            dhP = h2P - h1P;
        else if (h2P - h1P > 180)
            dhP = h2P - h1P - 360;
        else
            dhP = h2P - h1P + 360;

        double dHP = 2 * Math.Sqrt(c1P * c2P) * Math.Sin(dhP * Math.PI / 360);

        double lMean = (l1 + l2) / 2.0;
        double cPMean = (c1P + c2P) / 2.0;

        double hPMean;
        if (c1P * c2P == 0)
            hPMean = h1P + h2P;
        else if (Math.Abs(h1P - h2P) <= 180)
            hPMean = (h1P + h2P) / 2.0;
        else if (h1P + h2P < 360)
            hPMean = (h1P + h2P + 360) / 2.0;
        else
            hPMean = (h1P + h2P - 360) / 2.0;

        double t = 1
            - 0.17 * Math.Cos((hPMean - 30) * Math.PI / 180)
            + 0.24 * Math.Cos(2 * hPMean * Math.PI / 180)
            + 0.32 * Math.Cos((3 * hPMean + 6) * Math.PI / 180)
            - 0.20 * Math.Cos((4 * hPMean - 63) * Math.PI / 180);

        double lMeanM50 = (lMean - 50) * (lMean - 50);
        double sl = 1 + 0.015 * lMeanM50 / Math.Sqrt(20 + lMeanM50);
        double sc = 1 + 0.045 * cPMean;
        double sh = 1 + 0.015 * cPMean * t;

        double cPMean7 = Math.Pow(cPMean, 7);
        double rt = -2 * Math.Sqrt(cPMean7 / (cPMean7 + 6103515625.0))
            * Math.Sin(60 * Math.Exp(-((hPMean - 275) / 25) * ((hPMean - 275) / 25)) * Math.PI / 180);

        double dL = dLP / sl;
        double dC = dCP / sc;
        double dH = dHP / sh;

        return Math.Sqrt(dL * dL + dC * dC + dH * dH + rt * dC * dH);
    }
}
