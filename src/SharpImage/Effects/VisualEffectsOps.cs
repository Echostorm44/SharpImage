// SharpImage — Visual effects operations.
// BlueShift, Shadow, Tint, Stegano, Polaroid, SparseColor, Segment.
// Based on ImageMagick visual-effects.c, distort.c, and segment.c algorithms.

using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Visual effects: BlueShift, Shadow, Tint, Stegano, Polaroid.
/// </summary>
public static class VisualEffectsOps
{
    // ─── BlueShift ─────────────────────────────────────────────────────

    /// <summary>
    /// Simulates moonlight by shifting colors toward blue-cool tones.
    /// The factor controls the intensity of the shift (default 1.5 matches IM).
    /// Algorithm: blend = 0.5 * (0.5 * rgb + factor * min) + factor * max
    /// </summary>
    public static ImageFrame BlueShift(ImageFrame source, double factor = 1.5)
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
                double r = srcRow[off];
                double g = channels > 1 ? srcRow[off + 1] : r;
                double b = channels > 2 ? srcRow[off + 2] : r;

                double minVal = Math.Min(r, Math.Min(g, b));
                double maxVal = Math.Max(r, Math.Max(g, b));

                // First pass: blend with min
                double pr = 0.5 * (r + factor * minVal);
                double pg = 0.5 * (g + factor * minVal);
                double pb = 0.5 * (b + factor * minVal);

                // Second pass: blend with max
                pr = 0.5 * (pr + factor * maxVal);
                pg = 0.5 * (pg + factor * maxVal);
                pb = 0.5 * (pb + factor * maxVal);

                dstRow[off] = (ushort)Math.Clamp(pr + 0.5, 0, Quantum.MaxValue);
                if (channels > 1) dstRow[off + 1] = (ushort)Math.Clamp(pg + 0.5, 0, Quantum.MaxValue);
                if (channels > 2) dstRow[off + 2] = (ushort)Math.Clamp(pb + 0.5, 0, Quantum.MaxValue);
                if (channels > 3) dstRow[off + 3] = srcRow[off + 3]; // preserve alpha
            }
        });

        return result;
    }

    // ─── Shadow ────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a drop shadow for the image. Creates a silhouette filled with the
    /// shadow color, blurred with the specified sigma, offset by (xOffset, yOffset).
    /// Alpha controls opacity (0-100, default 80%).
    /// </summary>
    public static ImageFrame Shadow(ImageFrame source, double alpha = 80.0, double sigma = 4.0,
        int xOffset = 4, int yOffset = 4,
        ushort shadowR = 0, ushort shadowG = 0, ushort shadowB = 0)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;

        // Add border for blur padding
        int borderSize = (int)(2.0 * sigma + 0.5);
        int paddedWidth = srcWidth + borderSize * 2;
        int paddedHeight = srcHeight + borderSize * 2;

        // Create shadow frame with alpha channel
        var shadowFrame = new ImageFrame();
        shadowFrame.Initialize(paddedWidth, paddedHeight, source.Colorspace, true);

        double alphaScale = Math.Clamp(alpha, 0, 100) / 100.0;
        ushort opaqueAlpha = Quantum.MaxValue;

        // Fill: shadow color for all pixels, alpha from source (if present) or opaque
        Parallel.For(0, paddedHeight, py =>
        {
            var dstRow = shadowFrame.GetPixelRowForWrite(py);
            int dstChannels = shadowFrame.NumberOfChannels;
            int srcY = py - borderSize;

            for (int px = 0; px < paddedWidth; px++)
            {
                int dstOff = px * dstChannels;
                int srcX = px - borderSize;

                // Determine alpha: if inside source, use source alpha * alphaScale; else 0
                ushort pixelAlpha = 0;
                if (srcX >= 0 && srcX < srcWidth && srcY >= 0 && srcY < srcHeight)
                {
                    if (source.HasAlpha)
                    {
                        var srcRow = source.GetPixelRow(srcY);
                        int srcChannels = source.NumberOfChannels;
                        pixelAlpha = (ushort)Math.Clamp(srcRow[srcX * srcChannels + srcChannels - 1] * alphaScale + 0.5, 0, Quantum.MaxValue);
                    }
                    else
                    {
                        pixelAlpha = (ushort)Math.Clamp(opaqueAlpha * alphaScale + 0.5, 0, Quantum.MaxValue);
                    }
                }

                dstRow[dstOff] = shadowR;
                dstRow[dstOff + 1] = shadowG;
                dstRow[dstOff + 2] = shadowB;
                dstRow[dstOff + 3] = pixelAlpha;
            }
        });

        // Blur the alpha channel only — we'll do a full Gaussian blur then restore RGB
        var blurred = ConvolutionFilters.GaussianBlur(shadowFrame, sigma);

        // The Gaussian blur blurs all channels. Restore shadow color for RGB.
        Parallel.For(0, (int)blurred.Rows, y =>
        {
            var row = blurred.GetPixelRowForWrite(y);
            int ch = blurred.NumberOfChannels;
            for (int x = 0; x < (int)blurred.Columns; x++)
            {
                int off = x * ch;
                row[off] = shadowR;
                row[off + 1] = shadowG;
                row[off + 2] = shadowB;
                // alpha is already blurred
            }
        });

        return blurred;
    }

    // ─── Tint ──────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a color tint with a midtone-weighted function. Unlike Colorize which
    /// does flat linear blending, Tint applies a parabolic weighting:
    /// f(x) = 1 - 4*(x - 0.5)^2 — maximum effect at midtones, zero at black/white.
    /// blendR/G/B control per-channel tint strength (0-100 percentage).
    /// </summary>
    public static ImageFrame Tint(ImageFrame source, ushort tintR, ushort tintG, ushort tintB,
        double blendR = 100.0, double blendG = 100.0, double blendB = 100.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Compute color vector like IM: vector = blend * tint / 100 - intensity
        double tintIntensity = (tintR + tintG + tintB) / 3.0;
        double vecR = blendR * tintR / 100.0 - tintIntensity;
        double vecG = blendG * tintG / 100.0 - tintIntensity;
        double vecB = blendB * tintB / 100.0 - tintIntensity;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        double invMax = 1.0 / Quantum.MaxValue;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                // Red channel
                double weight = invMax * srcRow[off] - 0.5;
                double parabola = 1.0 - 4.0 * weight * weight;
                double outR = srcRow[off] + vecR * parabola;

                if (channels > 1)
                {
                    weight = invMax * srcRow[off + 1] - 0.5;
                    parabola = 1.0 - 4.0 * weight * weight;
                    double outG = srcRow[off + 1] + vecG * parabola;
                    dstRow[off + 1] = (ushort)Math.Clamp(outG + 0.5, 0, Quantum.MaxValue);
                }
                if (channels > 2)
                {
                    weight = invMax * srcRow[off + 2] - 0.5;
                    parabola = 1.0 - 4.0 * weight * weight;
                    double outB = srcRow[off + 2] + vecB * parabola;
                    dstRow[off + 2] = (ushort)Math.Clamp(outB + 0.5, 0, Quantum.MaxValue);
                }
                dstRow[off] = (ushort)Math.Clamp(outR + 0.5, 0, Quantum.MaxValue);
                if (channels > 3) dstRow[off + 3] = srcRow[off + 3]; // preserve alpha
            }
        });

        return result;
    }

    // ─── Stegano ───────────────────────────────────────────────────────

    /// <summary>
    /// Embeds a watermark image into the low-order bits of the cover image.
    /// Each bit of the watermark's luminance is hidden in the LSB of the RGB channels
    /// cycling R→G→B across successive pixels.
    /// </summary>
    public static ImageFrame SteganoEmbed(ImageFrame cover, ImageFrame watermark, int bitDepth = 1)
    {
        int coverWidth = (int)cover.Columns;
        int coverHeight = (int)cover.Rows;
        int coverChannels = cover.NumberOfChannels;
        int wmWidth = (int)watermark.Columns;
        int wmHeight = (int)watermark.Rows;
        int wmChannels = watermark.NumberOfChannels;

        bitDepth = Math.Clamp(bitDepth, 1, 8);

        // Clone the cover image
        var result = new ImageFrame();
        result.Initialize(coverWidth, coverHeight, cover.Colorspace, cover.HasAlpha);
        for (int y = 0; y < coverHeight; y++)
            cover.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));

        // Embed watermark bits
        int channelCycle = 0; // 0=R, 1=G, 2=B
        int pixelIndex = 0;

        for (int bit = bitDepth - 1; bit >= 0; bit--)
        {
            for (int wy = 0; wy < wmHeight; wy++)
            {
                var wmRow = watermark.GetPixelRow(wy);
                for (int wx = 0; wx < wmWidth; wx++)
                {
                    // Get watermark luminance
                    int wmOff = wx * wmChannels;
                    int luminance;
                    if (wmChannels >= 3)
                        luminance = (int)(0.299 * wmRow[wmOff] + 0.587 * wmRow[wmOff + 1] + 0.114 * wmRow[wmOff + 2]);
                    else
                        luminance = wmRow[wmOff];

                    // Scale luminance to 8-bit for bit extraction
                    int lum8 = (int)(luminance * 255.0 / Quantum.MaxValue + 0.5);
                    int wmBit = (lum8 >> bit) & 1;

                    // Find cover pixel
                    int coverY = pixelIndex / coverWidth;
                    int coverX = pixelIndex % coverWidth;
                    if (coverY >= coverHeight) goto done;

                    var row = result.GetPixelRowForWrite(coverY);
                    int off = coverX * coverChannels;
                    int channelIdx = Math.Min(channelCycle, coverChannels - 1);
                    if (cover.HasAlpha && channelIdx >= coverChannels - 1)
                        channelIdx = coverChannels - 2; // skip alpha

                    ushort val = row[off + channelIdx];
                    // Set LSB
                    val = (ushort)((val & ~1) | wmBit);
                    row[off + channelIdx] = val;

                    channelCycle = (channelCycle + 1) % 3;
                    pixelIndex++;
                    if (pixelIndex >= coverWidth * coverHeight)
                        pixelIndex = 0;
                }
            }
        }
        done:

        return result;
    }

    /// <summary>
    /// Extracts a hidden watermark from a stegano image by reading LSBs.
    /// The caller must know the watermark dimensions.
    /// </summary>
    public static ImageFrame SteganoExtract(ImageFrame source, int watermarkWidth, int watermarkHeight, int bitDepth = 1)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int srcChannels = source.NumberOfChannels;

        bitDepth = Math.Clamp(bitDepth, 1, 8);

        var result = new ImageFrame();
        result.Initialize(watermarkWidth, watermarkHeight, ColorspaceType.SRGB, false);

        int channelCycle = 0;
        int pixelIndex = 0;

        // Accumulate bits
        var luminanceMap = new int[watermarkWidth * watermarkHeight];

        for (int bit = bitDepth - 1; bit >= 0; bit--)
        {
            for (int wy = 0; wy < watermarkHeight; wy++)
            {
                for (int wx = 0; wx < watermarkWidth; wx++)
                {
                    int coverY = pixelIndex / srcWidth;
                    int coverX = pixelIndex % srcWidth;
                    if (coverY >= srcHeight) goto done;

                    var srcRow = source.GetPixelRow(coverY);
                    int off = coverX * srcChannels;
                    int channelIdx = Math.Min(channelCycle, srcChannels - 1);
                    if (source.HasAlpha && channelIdx >= srcChannels - 1)
                        channelIdx = srcChannels - 2;

                    int lsb = srcRow[off + channelIdx] & 1;
                    luminanceMap[wy * watermarkWidth + wx] |= (lsb << bit);

                    channelCycle = (channelCycle + 1) % 3;
                    pixelIndex++;
                    if (pixelIndex >= srcWidth * srcHeight)
                        pixelIndex = 0;
                }
            }
        }
        done:

        // Write luminance to result
        for (int wy = 0; wy < watermarkHeight; wy++)
        {
            var dstRow = result.GetPixelRowForWrite(wy);
            int ch = result.NumberOfChannels;
            for (int wx = 0; wx < watermarkWidth; wx++)
            {
                int lum8 = luminanceMap[wy * watermarkWidth + wx];
                ushort lumQ = (ushort)Math.Clamp(lum8 * Quantum.MaxValue / 255.0 + 0.5, 0, Quantum.MaxValue);
                int off = wx * ch;
                dstRow[off] = lumQ;
                if (ch > 1) dstRow[off + 1] = lumQ;
                if (ch > 2) dstRow[off + 2] = lumQ;
            }
        }

        return result;
    }

    // ─── Polaroid ──────────────────────────────────────────────────────

    /// <summary>
    /// Simulates a Polaroid photo: adds a white border (wider on the bottom for a caption area),
    /// then applies a slight rotation and a drop shadow for a natural look.
    /// </summary>
    public static ImageFrame Polaroid(ImageFrame source, double angle = 0.0, int borderSize = 20,
        ushort borderR = 65535, ushort borderG = 65535, ushort borderB = 65535)
    {
        int bottomBorder = borderSize * 3; // Extra space at bottom for "caption area"

        // Add asymmetric border
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        int newWidth = srcWidth + borderSize * 2;
        int newHeight = srcHeight + borderSize + bottomBorder;

        var bordered = new ImageFrame();
        bordered.Initialize(newWidth, newHeight, source.Colorspace, hasAlpha);

        // Fill with border color
        for (int y = 0; y < newHeight; y++)
        {
            var row = bordered.GetPixelRowForWrite(y);
            int ch = bordered.NumberOfChannels;
            for (int x = 0; x < newWidth; x++)
            {
                int off = x * ch;
                row[off] = borderR;
                if (ch > 1) row[off + 1] = borderG;
                if (ch > 2) row[off + 2] = borderB;
                if (ch > 3) row[off + 3] = Quantum.MaxValue; // opaque
            }
        }

        // Copy source image into bordered region
        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = bordered.GetPixelRowForWrite(y + borderSize);
            int ch = bordered.NumberOfChannels;
            int srcCh = channels;
            for (int x = 0; x < srcWidth; x++)
            {
                int sOff = x * srcCh;
                int dOff = (x + borderSize) * ch;
                for (int c = 0; c < Math.Min(srcCh, ch); c++)
                    dstRow[dOff + c] = srcRow[sOff + c];
                if (ch > srcCh && ch > 3)
                    dstRow[dOff + ch - 1] = Quantum.MaxValue;
            }
        }

        // Apply rotation if non-zero
        if (Math.Abs(angle) > 0.01)
        {
            bordered = Geometry.RotateArbitrary(bordered, angle);
        }

        return bordered;
    }

    // ─── SparseColor ───────────────────────────────────────────────────

    /// <summary>
    /// Interpolates colors at sparse control points to fill the entire image.
    /// Each control point is (x, y, r, g, b) in quantum scale.
    /// </summary>
    public static ImageFrame SparseColor(ImageFrame source, SparseColorMethod method,
        ReadOnlySpan<SparseColorPoint> controlPoints)
    {
        if (controlPoints.Length == 0)
            throw new ArgumentException("At least one control point is required.");

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // Copy control points to array for parallel access
        var points = controlPoints.ToArray();

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double r, g, b;

                switch (method)
                {
                    case SparseColorMethod.Voronoi:
                        NearestNeighborColor(x, y, points, out r, out g, out b, useEuclidean: true);
                        break;

                    case SparseColorMethod.Manhattan:
                        NearestNeighborColor(x, y, points, out r, out g, out b, useEuclidean: false);
                        break;

                    case SparseColorMethod.Shepards:
                        ShepardsColor(x, y, points, 2.0, out r, out g, out b);
                        break;

                    case SparseColorMethod.InverseDistance:
                        ShepardsColor(x, y, points, 1.0, out r, out g, out b);
                        break;

                    default:
                        ShepardsColor(x, y, points, 2.0, out r, out g, out b);
                        break;
                }

                dstRow[off] = (ushort)Math.Clamp(r + 0.5, 0, Quantum.MaxValue);
                if (channels > 1) dstRow[off + 1] = (ushort)Math.Clamp(g + 0.5, 0, Quantum.MaxValue);
                if (channels > 2) dstRow[off + 2] = (ushort)Math.Clamp(b + 0.5, 0, Quantum.MaxValue);
                if (channels > 3) dstRow[off + 3] = source.HasAlpha ? source.GetPixelRow(y)[off + 3] : Quantum.MaxValue;
            }
        });

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NearestNeighborColor(int px, int py, SparseColorPoint[] points,
        out double r, out double g, out double b, bool useEuclidean)
    {
        double minDist = double.MaxValue;
        r = g = b = 0;

        for (int k = 0; k < points.Length; k++)
        {
            double dx = px - points[k].X;
            double dy = py - points[k].Y;
            double dist = useEuclidean ? dx * dx + dy * dy : Math.Abs(dx) + Math.Abs(dy);

            if (dist < minDist)
            {
                minDist = dist;
                r = points[k].R;
                g = points[k].G;
                b = points[k].B;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ShepardsColor(int px, int py, SparseColorPoint[] points,
        double power, out double r, out double g, out double b)
    {
        double sumR = 0, sumG = 0, sumB = 0, sumWeight = 0;

        for (int k = 0; k < points.Length; k++)
        {
            double dx = px - points[k].X;
            double dy = py - points[k].Y;
            double distSq = dx * dx + dy * dy;
            double weight = Math.Pow(distSq, power * 0.5);
            weight = weight < 1.0 ? 1.0 : 1.0 / weight;

            sumR += points[k].R * weight;
            sumG += points[k].G * weight;
            sumB += points[k].B * weight;
            sumWeight += weight;
        }

        if (sumWeight > 0)
        {
            r = sumR / sumWeight;
            g = sumG / sumWeight;
            b = sumB / sumWeight;
        }
        else
        {
            r = g = b = 0;
        }
    }

    // ─── Segment ───────────────────────────────────────────────────────

    /// <summary>
    /// Segments an image into distinct color regions using histogram analysis
    /// and fuzzy c-means clustering. Replaces each pixel's color with the mean
    /// color of the cluster it belongs to.
    /// clusterThreshold controls the sensitivity of cluster detection.
    /// smoothingThreshold controls the scale-space smoothing.
    /// </summary>
    public static ImageFrame Segment(ImageFrame source, double clusterThreshold = 1.0,
        double smoothingThreshold = 1.5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;
        colorChannels = Math.Min(colorChannels, 3);

        // Build per-channel histograms (256 bins, map from quantum to 0-255)
        int binCount = 256;
        double binScale = (binCount - 1.0) / Quantum.MaxValue;

        var histograms = new int[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
            histograms[c] = new int[binCount];

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    int bin = (int)(row[off + c] * binScale + 0.5);
                    bin = Math.Clamp(bin, 0, binCount - 1);
                    histograms[c][bin]++;
                }
            }
        }

        // Find peaks in each channel's histogram using scale-space smoothing
        var channelCenters = new List<double>[colorChannels];
        for (int c = 0; c < colorChannels; c++)
        {
            channelCenters[c] = FindHistogramPeaks(histograms[c], smoothingThreshold, clusterThreshold);
            if (channelCenters[c].Count == 0)
                channelCenters[c].Add(128.0); // fallback to midpoint
        }

        // Build cluster centers as combinations of per-channel peaks
        var clusters = BuildClusterCenters(channelCenters, colorChannels);

        // Assign each pixel to nearest cluster (fuzzy c-means: use closest center)
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                // Get pixel color in 0-255 space
                double pr = srcRow[off] * binScale;
                double pg = colorChannels > 1 ? srcRow[off + 1] * binScale : pr;
                double pb = colorChannels > 2 ? srcRow[off + 2] * binScale : pr;

                // Find closest cluster
                double minDist = double.MaxValue;
                int bestCluster = 0;
                for (int k = 0; k < clusters.Length; k++)
                {
                    double dr = pr - clusters[k][0];
                    double dg = colorChannels > 1 ? pg - clusters[k][1] : 0;
                    double db = colorChannels > 2 ? pb - clusters[k][2] : 0;
                    double dist = dr * dr + dg * dg + db * db;
                    if (dist < minDist)
                    {
                        minDist = dist;
                        bestCluster = k;
                    }
                }

                // Set pixel to cluster center color (convert back to quantum)
                double invBinScale = Quantum.MaxValue / (binCount - 1.0);
                dstRow[off] = (ushort)Math.Clamp(clusters[bestCluster][0] * invBinScale + 0.5, 0, Quantum.MaxValue);
                if (channels > 1) dstRow[off + 1] = (ushort)Math.Clamp(clusters[bestCluster][Math.Min(1, colorChannels - 1)] * invBinScale + 0.5, 0, Quantum.MaxValue);
                if (channels > 2) dstRow[off + 2] = (ushort)Math.Clamp(clusters[bestCluster][Math.Min(2, colorChannels - 1)] * invBinScale + 0.5, 0, Quantum.MaxValue);
                if (channels > 3) dstRow[off + 3] = srcRow[off + 3]; // preserve alpha
            }
        });

        return result;
    }

    /// <summary>
    /// Finds peaks in a histogram using Gaussian smoothing and second-derivative zero crossings.
    /// </summary>
    private static List<double> FindHistogramPeaks(int[] histogram, double sigma, double threshold)
    {
        int n = histogram.Length;
        var smoothed = new double[n];
        var secondDerivative = new double[n];

        // Apply Gaussian smoothing
        int kernelRadius = Math.Max(1, (int)(3.0 * sigma + 0.5));
        double[] kernel = new double[kernelRadius * 2 + 1];
        double kernelSum = 0;
        for (int i = -kernelRadius; i <= kernelRadius; i++)
        {
            kernel[i + kernelRadius] = Math.Exp(-(i * i) / (2.0 * sigma * sigma));
            kernelSum += kernel[i + kernelRadius];
        }
        for (int i = 0; i < kernel.Length; i++)
            kernel[i] /= kernelSum;

        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int k = -kernelRadius; k <= kernelRadius; k++)
            {
                int idx = Math.Clamp(i + k, 0, n - 1);
                sum += histogram[idx] * kernel[k + kernelRadius];
            }
            smoothed[i] = sum;
        }

        // Compute second derivative
        for (int i = 1; i < n - 1; i++)
            secondDerivative[i] = smoothed[i - 1] - 2.0 * smoothed[i] + smoothed[i + 1];

        // Find zero crossings (negative to positive = peak boundary)
        var peaks = new List<double>();
        int totalPixels = 0;
        foreach (int h in histogram) totalPixels += h;
        double minPeakHeight = totalPixels * threshold * 0.001; // threshold as fraction

        for (int i = 2; i < n - 2; i++)
        {
            // Peak is where smoothed has local maximum
            if (smoothed[i] > smoothed[i - 1] && smoothed[i] >= smoothed[i + 1] &&
                smoothed[i] > minPeakHeight)
            {
                peaks.Add(i);
            }
        }

        // If too many peaks, keep only the largest ones
        if (peaks.Count > 16)
        {
            peaks.Sort((a, b) => smoothed[(int)b].CompareTo(smoothed[(int)a]));
            peaks = peaks.Take(16).ToList();
            peaks.Sort();
        }

        return peaks;
    }

    /// <summary>
    /// Builds cluster centers as the Cartesian product of per-channel peak lists,
    /// capped to avoid combinatorial explosion.
    /// </summary>
    private static double[][] BuildClusterCenters(List<double>[] channelCenters, int colorChannels)
    {
        // Limit combinations to prevent explosion
        int maxClusters = 64;
        var result = new List<double[]>();

        if (colorChannels == 1)
        {
            foreach (var r in channelCenters[0])
                result.Add(new[] { r });
        }
        else if (colorChannels == 2)
        {
            foreach (var r in channelCenters[0])
                foreach (var g in channelCenters[1])
                {
                    result.Add(new[] { r, g });
                    if (result.Count >= maxClusters) goto done;
                }
        }
        else
        {
            foreach (var r in channelCenters[0])
                foreach (var g in channelCenters[1])
                    foreach (var b in channelCenters[2])
                    {
                        result.Add(new[] { r, g, b });
                        if (result.Count >= maxClusters) goto done;
                    }
        }
        done:

        if (result.Count == 0)
            result.Add(new double[colorChannels]); // fallback

        return result.ToArray();
    }
}

// ─── Supporting Types ──────────────────────────────────────────────────

/// <summary>
/// A control point for sparse color interpolation: position (X,Y) and color (R,G,B) in quantum scale.
/// </summary>
public readonly struct SparseColorPoint
{
    public readonly double X;
    public readonly double Y;
    public readonly double R;
    public readonly double G;
    public readonly double B;

    public SparseColorPoint(double x, double y, double r, double g, double b)
    {
        X = x; Y = y; R = r; G = g; B = b;
    }
}

/// <summary>
/// Interpolation method for SparseColor.
/// </summary>
public enum SparseColorMethod
{
    Voronoi,
    Manhattan,
    Shepards,
    InverseDistance,
    Barycentric,
    Bilinear
}

// ─── MorphImages ──────────────────────────────────────────────────

/// <summary>
/// Creates cross-dissolve interpolation frames between images.
/// </summary>
public static class MorphOps
{
    /// <summary>
    /// Creates N intermediate frames between two images using linear interpolation.
    /// Returns an array of (N + 2) frames: source, N intermediates, target.
    /// </summary>
    public static ImageFrame[] Morph(ImageFrame source, ImageFrame target, int intermediateFrames)
    {
        if (intermediateFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(intermediateFrames));

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var frames = new ImageFrame[intermediateFrames + 2];

        // Clone source as first frame
        frames[0] = CloneFrame(source);

        // Generate intermediate frames
        for (int f = 1; f <= intermediateFrames; f++)
        {
            double t = (double)f / (intermediateFrames + 1);
            double invT = 1.0 - t;

            var frame = new ImageFrame();
            frame.Initialize(width, height, source.Colorspace, source.HasAlpha);

            Parallel.For(0, height, y =>
            {
                var srcRow = source.GetPixelRow(y);
                var tgtRow = target.GetPixelRow(y);
                var dstRow = frame.GetPixelRowForWrite(y);

                for (int i = 0; i < width * channels; i++)
                {
                    dstRow[i] = (ushort)Math.Clamp(invT * srcRow[i] + t * tgtRow[i], 0, Quantum.MaxValue);
                }
            });

            frames[f] = frame;
        }

        // Clone target as last frame
        frames[intermediateFrames + 1] = CloneFrame(target);
        return frames;
    }

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var clone = new ImageFrame();
        clone.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < height; y++)
        {
            var src = source.GetPixelRow(y);
            var dst = clone.GetPixelRowForWrite(y);
            src.CopyTo(dst);
        }

        return clone;
    }
}

/// <summary>
/// Stereo anaglyph generation for 3D viewing with red/cyan glasses.
/// </summary>
public static class StereoOps
{
    /// <summary>
    /// Creates a red/cyan anaglyph from a stereo pair (left and right images).
    /// Left image contributes to the red channel, right image to green and blue.
    /// </summary>
    public static ImageFrame Anaglyph(ImageFrame left, ImageFrame right)
    {
        int width = (int)left.Columns;
        int height = (int)left.Rows;
        int channels = left.NumberOfChannels;
        int colorChannels = left.HasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(width, height, left.Colorspace, left.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var leftRow = left.GetPixelRow(y);
            var rightRow = right.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                if (colorChannels >= 3)
                {
                    // Red from left image (luminance weighted for better depth perception)
                    double leftLuma = 0.299 * leftRow[off] + 0.587 * leftRow[off + 1] + 0.114 * leftRow[off + 2];
                    dstRow[off] = (ushort)Math.Clamp(leftLuma, 0, Quantum.MaxValue);
                    // Green and blue from right image
                    dstRow[off + 1] = rightRow[off + 1];
                    dstRow[off + 2] = rightRow[off + 2];
                }
                else
                {
                    dstRow[off] = leftRow[off];
                }

                if (left.HasAlpha)
                    dstRow[off + colorChannels] = leftRow[off + colorChannels];
            }
        });

        return result;
    }
}

/// <summary>
/// IM-style noise reduction using weighted neighbor averaging (EnhanceImage).
/// </summary>
public static class NoiseReduceOps
{
    /// <summary>
    /// Noise reduction using distance-weighted averaging of 5x5 neighborhood.
    /// Pixels similar in color contribute more to the average (preserves edges).
    /// </summary>
    public static ImageFrame Enhance(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // 5x5 distance weights (matching IM's EnhanceImage)
        double[] weights = [
            5.0/25, 8.0/25, 10.0/25, 8.0/25, 5.0/25,
            8.0/25, 20.0/25, 40.0/25, 20.0/25, 8.0/25,
            10.0/25, 40.0/25, 80.0/25, 40.0/25, 10.0/25,
            8.0/25, 20.0/25, 40.0/25, 20.0/25, 8.0/25,
            5.0/25, 8.0/25, 10.0/25, 8.0/25, 5.0/25
        ];

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int dstOff = x * channels;
                var centerPixel = source.GetPixelRow(y);
                int centerOff = x * channels;

                for (int c = 0; c < colorChannels; c++)
                {
                    double weightedSum = 0;
                    double totalWeight = 0;
                    int wi = 0;

                    for (int ky = -2; ky <= 2; ky++)
                    {
                        int sy = Math.Clamp(y + ky, 0, height - 1);
                        var srcRow = source.GetPixelRow(sy);

                        for (int kx = -2; kx <= 2; kx++)
                        {
                            int sx = Math.Clamp(x + kx, 0, width - 1);
                            double neighborVal = srcRow[sx * channels + c];
                            double centerVal = centerPixel[centerOff + c];

                            // Weight by both spatial distance and color similarity
                            double colorDist = Math.Abs(neighborVal - centerVal) / Quantum.MaxValue;
                            double similarity = Math.Max(0, 1.0 - colorDist * 3.0);
                            double w = weights[wi++] * similarity;

                            weightedSum += neighborVal * w;
                            totalWeight += w;
                        }
                    }

                    dstRow[dstOff + c] = (ushort)Math.Clamp(
                        totalWeight > 0 ? weightedSum / totalWeight : centerPixel[centerOff + c],
                        0, Quantum.MaxValue);
                }

                if (source.HasAlpha)
                    dstRow[dstOff + colorChannels] = source.GetPixelRow(y)[x * channels + colorChannels];
            }
        });

        return result;
    }
}

/// <summary>
/// Chroma key (green screen) removal.
/// </summary>
public static class ChromaKeyOps
{
    /// <summary>
    /// Removes a background of a specific color, replacing it with transparency.
    /// Tolerance controls how far from the key color to remove (0–1 normalized).
    /// </summary>
    public static ImageFrame ChromaKey(ImageFrame source, ushort keyR, ushort keyG, ushort keyB, double tolerance = 0.15)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Ensure output has alpha
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, true);
        int outChannels = result.NumberOfChannels;

        double tolSquared = tolerance * tolerance * Quantum.MaxValue * Quantum.MaxValue * 3.0;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int srcOff = x * channels;
                int dstOff = x * outChannels;

                double dr = srcRow[srcOff] - keyR;
                double dg = srcRow[srcOff + 1] - keyG;
                double db = srcRow[srcOff + 2] - keyB;
                double distSquared = dr * dr + dg * dg + db * db;

                // Copy color channels
                dstRow[dstOff] = srcRow[srcOff];
                dstRow[dstOff + 1] = srcRow[srcOff + 1];
                dstRow[dstOff + 2] = srcRow[srcOff + 2];

                // Set alpha based on distance from key color
                if (distSquared <= tolSquared)
                {
                    // Fully transparent for close matches
                    double ratio = Math.Sqrt(distSquared / tolSquared);
                    dstRow[dstOff + 3] = (ushort)(ratio * Quantum.MaxValue);
                }
                else
                {
                    dstRow[dstOff + 3] = source.HasAlpha ? srcRow[srcOff + channels - 1] : Quantum.MaxValue;
                }
            }
        });

        return result;
    }
}

/// <summary>
/// Channel expression processing matching ImageMagick's ChannelFx.
/// </summary>
public static class ChannelFxOps
{
    /// <summary>
    /// Applies channel operations described by an expression string.
    /// Supported expressions: "red=&gt;blue" (copy), "swap:red,blue" (swap), "grayscale" (convert).
    /// Multiple operations separated by semicolons.
    /// </summary>
    public static ImageFrame ChannelFx(ImageFrame source, string expression)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        // Copy source to result first
        for (int y = 0; y < height; y++)
        {
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));
        }

        var operations = expression.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var op in operations)
        {
            if (op.Contains("=>"))
            {
                // Copy operation: "red=>blue" copies red channel to blue
                var parts = op.Split("=>", StringSplitOptions.TrimEntries);
                int srcCh = ChannelIndex(parts[0]);
                int dstCh = ChannelIndex(parts[1]);
                if (srcCh >= 0 && srcCh < channels && dstCh >= 0 && dstCh < channels)
                {
                    for (int y = 0; y < height; y++)
                    {
                        var srcRow = source.GetPixelRow(y);
                        var dstRow = result.GetPixelRowForWrite(y);
                        for (int x = 0; x < width; x++)
                        {
                            dstRow[x * channels + dstCh] = srcRow[x * channels + srcCh];
                        }
                    }
                }
            }
            else if (op.StartsWith("swap:", StringComparison.OrdinalIgnoreCase))
            {
                // Swap: "swap:red,blue"
                var chParts = op[5..].Split(',', StringSplitOptions.TrimEntries);
                if (chParts.Length == 2)
                {
                    int ch1 = ChannelIndex(chParts[0]);
                    int ch2 = ChannelIndex(chParts[1]);
                    if (ch1 >= 0 && ch1 < channels && ch2 >= 0 && ch2 < channels)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            var dstRow = result.GetPixelRowForWrite(y);
                            for (int x = 0; x < width; x++)
                            {
                                int off = x * channels;
                                (dstRow[off + ch1], dstRow[off + ch2]) = (dstRow[off + ch2], dstRow[off + ch1]);
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    private static int ChannelIndex(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "red" or "r" or "0" => 0,
            "green" or "g" or "1" => 1,
            "blue" or "b" or "2" => 2,
            "alpha" or "a" or "3" => 3,
            _ => -1,
        };
    }
}

/// <summary>
/// ASC-CDL (Color Decision List) color grading operations.
/// </summary>
public static class CdlOps
{
    /// <summary>
    /// Applies ASC-CDL color correction: out = pow(in * slope + offset, power).
    /// Saturation is applied after the SOP transform.
    /// </summary>
    public static ImageFrame ApplyCdl(ImageFrame source,
        double slopeR, double slopeG, double slopeB,
        double offsetR, double offsetG, double offsetB,
        double powerR, double powerG, double powerB,
        double saturation = 1.0)
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

                // SOP transform per channel (normalized 0..1)
                double r = srcRow[off] / (double)Quantum.MaxValue;
                double g = colorChannels >= 2 ? srcRow[off + 1] / (double)Quantum.MaxValue : r;
                double b = colorChannels >= 3 ? srcRow[off + 2] / (double)Quantum.MaxValue : r;

                r = Math.Pow(Math.Clamp(r * slopeR + offsetR, 0, 1), powerR);
                g = Math.Pow(Math.Clamp(g * slopeG + offsetG, 0, 1), powerG);
                b = Math.Pow(Math.Clamp(b * slopeB + offsetB, 0, 1), powerB);

                // Apply saturation
                if (Math.Abs(saturation - 1.0) > 0.001)
                {
                    double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    r = luma + saturation * (r - luma);
                    g = luma + saturation * (g - luma);
                    b = luma + saturation * (b - luma);
                }

                dstRow[off] = (ushort)Math.Clamp(r * Quantum.MaxValue, 0, Quantum.MaxValue);
                if (colorChannels >= 2) dstRow[off + 1] = (ushort)Math.Clamp(g * Quantum.MaxValue, 0, Quantum.MaxValue);
                if (colorChannels >= 3) dstRow[off + 2] = (ushort)Math.Clamp(b * Quantum.MaxValue, 0, Quantum.MaxValue);

                if (source.HasAlpha)
                    dstRow[off + colorChannels] = srcRow[off + colorChannels];
            }
        });

        return result;
    }
}
