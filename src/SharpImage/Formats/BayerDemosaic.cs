// Demosaicing algorithms for converting raw Bayer/X-Trans CFA sensor data to full RGB.
// Three quality tiers: Bilinear (fast), VNG (balanced), AHD (highest quality).
// Reference: dcraw.c, libraw demosaic implementations.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Demosaicing algorithms for converting raw Bayer/X-Trans CFA sensor data to full RGB.
/// </summary>
public static class BayerDemosaic
{
    /// <summary>
    /// Demosaics raw sensor data into a full RGB ImageFrame using the specified algorithm.
    /// Input raw data should be scaled to 16-bit range before calling.
    /// </summary>
    public static ImageFrame Demosaic(in RawSensorData raw, DemosaicAlgorithm algorithm = DemosaicAlgorithm.AHD)
    {
        return algorithm switch
        {
            DemosaicAlgorithm.Bilinear => BilinearDemosaic(in raw),
            DemosaicAlgorithm.VNG => VngDemosaic(in raw),
            DemosaicAlgorithm.AHD => AhdDemosaic(in raw),
            _ => BilinearDemosaic(in raw)
        };
    }

    /// <summary>
    /// Demosaics Fuji X-Trans 6×6 CFA data into full RGB using adaptive gradient interpolation.
    /// </summary>
    public static ImageFrame DemosaicXTrans(in RawSensorData raw)
    {
        int w = raw.Width, h = raw.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);

        // Use file-specific CFA if available, otherwise fall back to default
        byte[,] cfa = raw.XTransCfaEffective ?? XTransCfa.Pattern;

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int offset = x * 3;
                int thisColor = cfa[y % 6, x % 6];
                int thisValue = raw.RawPixels[y * w + x];

                int rSum = 0, rCount = 0;
                int gSum = 0, gCount = 0;
                int bSum = 0, bCount = 0;

                // Search 5×5 neighborhood for color samples
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = Math.Clamp(x + dx, 0, w - 1);
                        int ny = Math.Clamp(y + dy, 0, h - 1);
                        int nc = cfa[ny % 6, nx % 6];
                        int nv = raw.RawPixels[ny * w + nx];

                        int dist = Math.Abs(dx) + Math.Abs(dy);
                        if (dist == 0) dist = 1;
                        int weight = 5 - dist;
                        if (weight <= 0) weight = 1;

                        switch (nc)
                        {
                            case 0: rSum += nv * weight; rCount += weight; break;
                            case 1: gSum += nv * weight; gCount += weight; break;
                            case 2: bSum += nv * weight; bCount += weight; break;
                        }
                    }
                }

                int r = thisColor == 0 ? thisValue : (rCount > 0 ? rSum / rCount : 0);
                int g = thisColor == 1 ? thisValue : (gCount > 0 ? gSum / gCount : 0);
                int b = thisColor == 2 ? thisValue : (bCount > 0 ? bSum / bCount : 0);

                row[offset] = ClampU16(r);
                row[offset + 1] = ClampU16(g);
                row[offset + 2] = ClampU16(b);
            }
        }

        return frame;
    }

    // ─────────────────────────────────────────────────────────────
    // Bilinear Demosaicing — simple averaging of neighbors
    // ─────────────────────────────────────────────────────────────

    private static ImageFrame BilinearDemosaic(in RawSensorData raw)
    {
        int w = raw.Width, h = raw.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        var pixels = raw.RawPixels;
        var pattern = raw.Metadata.BayerPattern;

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int offset = x * 3;
                int color = GetBayerColor(x, y, pattern);
                int r, g, b;

                switch (color)
                {
                    case 0: // Red site
                        r = Get(pixels, x, y, w, h);
                        g = AvgCardinal(pixels, x, y, w, h);
                        b = AvgDiagonal(pixels, x, y, w, h);
                        break;
                    case 1: // Green on red row
                        g = Get(pixels, x, y, w, h);
                        r = AvgHorizontal(pixels, x, y, w, h);
                        b = AvgVertical(pixels, x, y, w, h);
                        break;
                    case 2: // Green on blue row
                        g = Get(pixels, x, y, w, h);
                        r = AvgVertical(pixels, x, y, w, h);
                        b = AvgHorizontal(pixels, x, y, w, h);
                        break;
                    default: // Blue site
                        b = Get(pixels, x, y, w, h);
                        g = AvgCardinal(pixels, x, y, w, h);
                        r = AvgDiagonal(pixels, x, y, w, h);
                        break;
                }

                row[offset] = ClampU16(r);
                row[offset + 1] = ClampU16(g);
                row[offset + 2] = ClampU16(b);
            }
        }

        return frame;
    }

    // ─────────────────────────────────────────────────────────────
    // VNG (Variable Number of Gradients) Demosaicing
    // ─────────────────────────────────────────────────────────────

    private static ImageFrame VngDemosaic(in RawSensorData raw)
    {
        int w = raw.Width, h = raw.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        var pixels = raw.RawPixels;
        var pattern = raw.Metadata.BayerPattern;

        // VNG needs a 2-pixel border — use bilinear for borders
        // First fill entire image with bilinear, then overwrite interior with VNG
        var bilinear = BilinearDemosaic(in raw);

        // Copy bilinear border pixels
        for (int y = 0; y < h; y++)
        {
            var srcRow = bilinear.GetPixelRow(y);
            var dstRow = frame.GetPixelRowForWrite(y);
            if (y < 2 || y >= h - 2)
            {
                srcRow.CopyTo(dstRow);
                continue;
            }

            // Copy left and right 2-pixel borders
            for (int x = 0; x < 2; x++)
            {
                int off = x * 3;
                dstRow[off] = srcRow[off];
                dstRow[off + 1] = srcRow[off + 1];
                dstRow[off + 2] = srcRow[off + 2];
            }
            for (int x = w - 2; x < w; x++)
            {
                int off = x * 3;
                dstRow[off] = srcRow[off];
                dstRow[off + 1] = srcRow[off + 1];
                dstRow[off + 2] = srcRow[off + 2];
            }
        }

        // VNG interior: 8 directional gradients in 5×5 neighborhood
        for (int y = 2; y < h - 2; y++)
        {
            var dstRow = frame.GetPixelRowForWrite(y);
            for (int x = 2; x < w - 2; x++)
            {
                int color = GetBayerColor(x, y, pattern);
                ComputeVngPixel(pixels, x, y, w, h, color, pattern, out int r, out int g, out int b);

                int off = x * 3;
                dstRow[off] = ClampU16(r);
                dstRow[off + 1] = ClampU16(g);
                dstRow[off + 2] = ClampU16(b);
            }
        }

        bilinear.Dispose();
        return frame;
    }

    private static void ComputeVngPixel(ushort[] pixels, int x, int y, int w, int h,
        int color, BayerPattern pattern, out int r, out int g, out int b)
    {
        // Compute 8 directional gradients
        Span<int> gradients = stackalloc int[8];
        Span<int> rValues = stackalloc int[8];
        Span<int> gValues = stackalloc int[8];
        Span<int> bValues = stackalloc int[8];

        // Direction offsets: N, NE, E, SE, S, SW, W, NW
        ReadOnlySpan<int> dx = [-0, 1, 2, 1, 0, -1, -2, -1];
        ReadOnlySpan<int> dy = [-2, -1, 0, 1, 2, 1, 0, -1];

        int center = Get(pixels, x, y, w, h);

        for (int d = 0; d < 8; d++)
        {
            int nx1 = x + dx[d];
            int ny1 = y + dy[d];
            int v1 = Get(pixels, nx1, ny1, w, h);

            // Second pixel in same direction
            int nx2 = x + dx[d] * 2;
            int ny2 = y + dy[d] * 2;
            nx2 = Math.Clamp(nx2, 0, w - 1);
            ny2 = Math.Clamp(ny2, 0, h - 1);
            int v2 = Get(pixels, nx2, ny2, w, h);

            // Gradient = absolute luminance differences along this direction
            gradients[d] = Math.Abs(center - v1) + Math.Abs(v1 - v2);

            // Get RGB at neighbor position using simple bilinear for estimation
            int nc = GetBayerColor(nx1, ny1, pattern);
            BilinearAtPoint(pixels, nx1, ny1, w, h, nc, pattern, out rValues[d], out gValues[d], out bValues[d]);
        }

        // Find threshold: min gradient + 1.5 * (max - min)
        int minGrad = int.MaxValue, maxGrad = int.MinValue;
        for (int d = 0; d < 8; d++)
        {
            if (gradients[d] < minGrad) minGrad = gradients[d];
            if (gradients[d] > maxGrad) maxGrad = gradients[d];
        }

        int threshold = minGrad + ((maxGrad - minGrad) * 3 / 2);

        // Average colors from low-gradient directions
        long rAcc = 0, gAcc = 0, bAcc = 0;
        int count = 0;
        for (int d = 0; d < 8; d++)
        {
            if (gradients[d] <= threshold)
            {
                rAcc += rValues[d];
                gAcc += gValues[d];
                bAcc += bValues[d];
                count++;
            }
        }

        if (count == 0) count = 1;

        // Blend with the center pixel's known color
        r = (int)(rAcc / count);
        g = (int)(gAcc / count);
        b = (int)(bAcc / count);

        // Override the known channel with the actual raw value
        switch (color)
        {
            case 0: r = center; break;
            case 1:
            case 2: g = center; break;
            case 3: b = center; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BilinearAtPoint(ushort[] pixels, int x, int y, int w, int h,
        int color, BayerPattern pattern, out int r, out int g, out int b)
    {
        switch (color)
        {
            case 0: // Red site
                r = Get(pixels, x, y, w, h);
                g = AvgCardinal(pixels, x, y, w, h);
                b = AvgDiagonal(pixels, x, y, w, h);
                break;
            case 1: // Green on red row
                g = Get(pixels, x, y, w, h);
                r = AvgHorizontal(pixels, x, y, w, h);
                b = AvgVertical(pixels, x, y, w, h);
                break;
            case 2: // Green on blue row
                g = Get(pixels, x, y, w, h);
                r = AvgVertical(pixels, x, y, w, h);
                b = AvgHorizontal(pixels, x, y, w, h);
                break;
            default: // Blue site
                b = Get(pixels, x, y, w, h);
                g = AvgCardinal(pixels, x, y, w, h);
                r = AvgDiagonal(pixels, x, y, w, h);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // AHD (Adaptive Homogeneity-Directed) Demosaicing
    // ─────────────────────────────────────────────────────────────

    private static ImageFrame AhdDemosaic(in RawSensorData raw)
    {
        int w = raw.Width, h = raw.Height;
        var pixels = raw.RawPixels;
        var pattern = raw.Metadata.BayerPattern;

        // Step 1 & 2: Create horizontal and vertical interpolations
        var hImage = new int[h * w * 3]; // horizontal candidate
        var vImage = new int[h * w * 3]; // vertical candidate

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 3;
                int color = GetBayerColor(x, y, pattern);
                int center = Get(pixels, x, y, w, h);

                // Horizontal interpolation (left-right neighbors only)
                InterpolateHorizontal(pixels, x, y, w, h, color, center,
                    out hImage[idx], out hImage[idx + 1], out hImage[idx + 2]);

                // Vertical interpolation (top-bottom neighbors only)
                InterpolateVertical(pixels, x, y, w, h, color, center,
                    out vImage[idx], out vImage[idx + 1], out vImage[idx + 2]);
            }
        }

        // Step 3: Convert both to CIELab and compute homogeneity
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                // Compute homogeneity for both candidates in a 3×3 window
                int hHomog = ComputeHomogeneity(hImage, x, y, w, h);
                int vHomog = ComputeHomogeneity(vImage, x, y, w, h);

                // Select the candidate with higher homogeneity
                int idx = (y * w + x) * 3;
                int off = x * 3;
                if (hHomog >= vHomog)
                {
                    row[off] = ClampU16(hImage[idx]);
                    row[off + 1] = ClampU16(hImage[idx + 1]);
                    row[off + 2] = ClampU16(hImage[idx + 2]);
                }
                else
                {
                    row[off] = ClampU16(vImage[idx]);
                    row[off + 1] = ClampU16(vImage[idx + 1]);
                    row[off + 2] = ClampU16(vImage[idx + 2]);
                }
            }
        }

        return frame;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterpolateHorizontal(ushort[] pixels, int x, int y, int w, int h,
        int color, int center, out int r, out int g, out int b)
    {
        // Horizontal-emphasis interpolation for AHD.
        // Green uses horizontal neighbors; the opposite color (Blue at Red, Red at Blue)
        // is only available at diagonal positions, so AvgDiagonal is used.
        switch (color)
        {
            case 0: // Red site — blue only exists at diagonal (odd,odd) positions
                r = center;
                g = AvgHorizontal(pixels, x, y, w, h);
                b = AvgDiagonal(pixels, x, y, w, h);
                break;
            case 1: // Green on red row — R left/right, B above/below
                g = center;
                r = AvgHorizontal(pixels, x, y, w, h);
                b = AvgVertical(pixels, x, y, w, h);
                break;
            case 2: // Green on blue row — B left/right, R above/below
                g = center;
                b = AvgHorizontal(pixels, x, y, w, h);
                r = AvgVertical(pixels, x, y, w, h);
                break;
            default: // Blue site — red only exists at diagonal (even,even) positions
                b = center;
                g = AvgHorizontal(pixels, x, y, w, h);
                r = AvgDiagonal(pixels, x, y, w, h);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterpolateVertical(ushort[] pixels, int x, int y, int w, int h,
        int color, int center, out int r, out int g, out int b)
    {
        // Vertical-emphasis interpolation for AHD.
        // Green uses vertical neighbors; the opposite color uses AvgDiagonal
        // since it only exists at diagonal positions.
        switch (color)
        {
            case 0: // Red site — blue at diagonals
                r = center;
                g = AvgVertical(pixels, x, y, w, h);
                b = AvgDiagonal(pixels, x, y, w, h);
                break;
            case 1: // Green on red row — R horizontal, B vertical
                g = center;
                r = AvgHorizontal(pixels, x, y, w, h);
                b = AvgVertical(pixels, x, y, w, h);
                break;
            case 2: // Green on blue row — B horizontal, R vertical
                g = center;
                b = AvgHorizontal(pixels, x, y, w, h);
                r = AvgVertical(pixels, x, y, w, h);
                break;
            default: // Blue site — red at diagonals
                b = center;
                g = AvgVertical(pixels, x, y, w, h);
                r = AvgDiagonal(pixels, x, y, w, h);
                break;
        }
    }

    private static int ComputeHomogeneity(int[] image, int cx, int cy, int w, int h)
    {
        int idx = (cy * w + cx) * 3;
        double cR = image[idx] / 65535.0;
        double cG = image[idx + 1] / 65535.0;
        double cB = image[idx + 2] / 65535.0;
        RgbToLab(cR, cG, cB, out double cL, out double cA, out double cLB);

        int homogeneity = 0;
        const double labThreshold = 2.0; // perceptual difference threshold

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = Math.Clamp(cx + dx, 0, w - 1);
                int ny = Math.Clamp(cy + dy, 0, h - 1);
                int nIdx = (ny * w + nx) * 3;

                double nR = image[nIdx] / 65535.0;
                double nG = image[nIdx + 1] / 65535.0;
                double nB = image[nIdx + 2] / 65535.0;
                RgbToLab(nR, nG, nB, out double nL, out double nA, out double nLB);

                if (Math.Abs(cL - nL) < labThreshold &&
                    Math.Abs(cA - nA) < labThreshold &&
                    Math.Abs(cLB - nLB) < labThreshold)
                {
                    homogeneity++;
                }
            }
        }

        return homogeneity;
    }

    /// <summary>
    /// Simplified sRGB → CIELab conversion for AHD homogeneity comparison.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbToLab(double r, double g, double b,
        out double labL, out double labA, out double labB)
    {
        // Linearize sRGB
        r = r > 0.04045 ? Math.Pow((r + 0.055) / 1.055, 2.4) : r / 12.92;
        g = g > 0.04045 ? Math.Pow((g + 0.055) / 1.055, 2.4) : g / 12.92;
        b = b > 0.04045 ? Math.Pow((b + 0.055) / 1.055, 2.4) : b / 12.92;

        // sRGB to XYZ (D65)
        double x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
        double y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        double z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;

        // Normalize to D65 white point
        x /= 0.95047;
        z /= 1.08883;

        x = x > 0.008856 ? Math.Cbrt(x) : 7.787 * x + 16.0 / 116.0;
        y = y > 0.008856 ? Math.Cbrt(y) : 7.787 * y + 16.0 / 116.0;
        z = z > 0.008856 ? Math.Cbrt(z) : 7.787 * z + 16.0 / 116.0;

        labL = 116.0 * y - 16.0;
        labA = 500.0 * (x - y);
        labB = 200.0 * (y - z);
    }

    // ─────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the Bayer CFA color at position (x, y).
    /// Returns 0=Red, 1=Green (red row), 2=Green (blue row), 3=Blue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBayerColor(int x, int y, BayerPattern pattern)
    {
        return pattern switch
        {
            BayerPattern.RGGB => (y & 1) * 2 + (x & 1),
            BayerPattern.BGGR => (1 - (y & 1)) * 2 + (1 - (x & 1)),
            BayerPattern.GRBG => (y & 1) * 2 + (1 - (x & 1)),
            BayerPattern.GBRG => (1 - (y & 1)) * 2 + (x & 1),
            _ => 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Get(ushort[] pixels, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        return pixels[y * w + x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClampU16(int value) => (ushort)Math.Clamp(value, 0, 65535);

    /// <summary>Average of 4 cardinal neighbors (up, down, left, right).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AvgCardinal(ushort[] p, int x, int y, int w, int h) =>
        (Get(p, x - 1, y, w, h) + Get(p, x + 1, y, w, h) +
         Get(p, x, y - 1, w, h) + Get(p, x, y + 1, w, h)) / 4;

    /// <summary>Average of 4 diagonal neighbors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AvgDiagonal(ushort[] p, int x, int y, int w, int h) =>
        (Get(p, x - 1, y - 1, w, h) + Get(p, x + 1, y - 1, w, h) +
         Get(p, x - 1, y + 1, w, h) + Get(p, x + 1, y + 1, w, h)) / 4;

    /// <summary>Average of left and right neighbors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AvgHorizontal(ushort[] p, int x, int y, int w, int h) =>
        (Get(p, x - 1, y, w, h) + Get(p, x + 1, y, w, h)) / 2;

    /// <summary>Average of top and bottom neighbors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AvgVertical(ushort[] p, int x, int y, int w, int h) =>
        (Get(p, x, y - 1, w, h) + Get(p, x, y + 1, w, h)) / 2;
}
