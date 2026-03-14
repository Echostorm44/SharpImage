using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Enhance;

/// <summary>
/// Image enhancement operations: histogram equalization, normalization, auto-level, auto-gamma,
/// sigmoidal contrast, CLAHE, modulate, white balance, tint/colorize, solarize, sepia tone,
/// local contrast, color LUT (CLUT/Hald), contrast stretch, linear stretch, levelize.
/// All operations return new ImageFrame instances (non-destructive).
/// </summary>
public static class EnhanceOps
{
    // ═══════════════════════════════════════════════════════════════
    // Equalize — Histogram equalization
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Histogram equalization: redistributes pixel values to span the full dynamic range evenly.
    /// Builds a cumulative distribution function (CDF) per channel and maps pixel values accordingly.
    /// </summary>
    public static ImageFrame Equalize(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);
        long totalPixels = (long)width * height;

        // Build per-channel histograms (256-bin for 16-bit quantum scaled to 8-bit bins)
        int binCount = 256;
        var histograms = new long[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
            histograms[c] = new long[binCount];

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    int bin = Quantum.ScaleToByte(row[off + c]);
                    histograms[c][bin]++;
                }
            }
        }

        // Build CDF lookup tables
        var luts = new ushort[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
        {
            luts[c] = BuildEqualizeLut(histograms[c], totalPixels, binCount);
        }

        return ApplyLuts(source, luts, colorChannels);
    }

    // ═══════════════════════════════════════════════════════════════
    // Normalize — Linear stretch to fill full quantum range
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Linear stretch: maps the darkest pixel to black and the brightest to white,
    /// stretching all values in between proportionally. Applied per-channel.
    /// </summary>
    public static ImageFrame Normalize(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

        // Find global min/max across all color channels
        ushort globalMin = Quantum.MaxValue;
        ushort globalMax = 0;

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    ushort val = row[off + c];
                    if (val < globalMin) globalMin = val;
                    if (val > globalMax) globalMax = val;
                }
            }
        }

        if (globalMin >= globalMax)
            return CloneFrame(source);

        double range = globalMax - globalMin;
        float stretchScale = (float)(Quantum.MaxValue / range);

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            SimdOps.LinearStretchRow(srcRow, dstRow, colorChannels, channels, width, globalMin, stretchScale);
            // Copy alpha if present and not handled by SIMD fast path
            if (channels > colorChannels)
            {
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    dstRow[off + colorChannels] = srcRow[off + colorChannels];
                }
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // AutoLevel — Per-channel min/max stretch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-channel linear stretch: each channel is independently stretched from its own min to max.
    /// More aggressive than Normalize which uses a global min/max.
    /// </summary>
    public static ImageFrame AutoLevel(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

        var channelMin = new ushort[colorChannels];
        var channelMax = new ushort[colorChannels];
        for (int c = 0; c < colorChannels; c++)
        {
            channelMin[c] = Quantum.MaxValue;
            channelMax[c] = 0;
        }

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    ushort val = row[off + c];
                    if (val < channelMin[c]) channelMin[c] = val;
                    if (val > channelMax[c]) channelMax[c] = val;
                }
            }
        }

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
                {
                    double range = channelMax[c] - channelMin[c];
                    if (range <= 0)
                    {
                        dstRow[off + c] = srcRow[off + c];
                    }
                    else
                    {
                        double stretched = (srcRow[off + c] - channelMin[c]) / range;
                        dstRow[off + c] = (ushort)Math.Clamp(stretched * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                    }
                }
                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // AutoGamma — Automatic gamma correction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Automatically adjusts gamma per channel so the log-mean of each channel maps to mid-gray (0.5).
    /// Formula: gamma = log(0.5) / log(mean), applied per channel.
    /// </summary>
    public static ImageFrame AutoGamma(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);
        long totalPixels = (long)width * height;

        // Compute per-channel mean (normalized to 0..1)
        var channelSum = new double[colorChannels];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    channelSum[c] += row[off + c] * Quantum.Scale;
            }
        }

        var gammaValues = new double[colorChannels];
        for (int c = 0; c < colorChannels; c++)
        {
            double mean = channelSum[c] / totalPixels;
            if (mean > 1e-10 && mean < 1.0 - 1e-10)
                gammaValues[c] = Math.Log(0.5) / Math.Log(mean);
            else
                gammaValues[c] = 1.0; // no correction needed
        }

        // Precompute gamma LUTs to avoid Math.Pow per pixel
        int lutSize = Quantum.MaxValue + 1;
        var gammaLuts = new ushort[colorChannels][];
        double invMax = 1.0 / Quantum.MaxValue;
        for (int c = 0; c < colorChannels; c++)
        {
            gammaLuts[c] = new ushort[lutSize];
            double gamma = gammaValues[c];
            for (int i = 0; i < lutSize; i++)
            {
                double val = i * invMax;
                double corrected = Math.Pow(val, gamma);
                gammaLuts[c][i] = (ushort)Math.Clamp(corrected * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
            }
        }

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
                {
                    dstRow[off + c] = gammaLuts[c][srcRow[off + c]];
                }
                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // SigmoidalContrast — Non-linear S-curve contrast
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies sigmoidal (S-curve) contrast adjustment. Produces more natural-looking contrast
    /// than linear contrast by preserving detail in highlights and shadows.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="contrast">Contrast strength (typically 3-20, higher = more contrast).</param>
    /// <param name="midpoint">Midpoint of the curve in 0.0-1.0 range (default 0.5).</param>
    /// <param name="sharpen">True to increase contrast, false to decrease.</param>
    public static ImageFrame SigmoidalContrast(ImageFrame source, double contrast, double midpoint = 0.5, bool sharpen = true)
    {
        // Build a LUT for the sigmoid function for speed
        int binCount = 256;
        var lut = new ushort[binCount];

        for (int i = 0; i < binCount; i++)
        {
            double val = i / 255.0;
            double result;

            if (sharpen)
            {
                // Increase contrast: sigmoid stretch
                double sigIn = 1.0 / (1.0 + Math.Exp(contrast * (midpoint - val)));
                double sigMid = 1.0 / (1.0 + Math.Exp(contrast * (midpoint - 0.0)));
                double sigMax = 1.0 / (1.0 + Math.Exp(contrast * (midpoint - 1.0)));
                result = (sigIn - sigMid) / (sigMax - sigMid);
            }
            else
            {
                // Decrease contrast: inverse sigmoid
                double sigMid = 1.0 / (1.0 + Math.Exp(contrast * (midpoint - 0.0)));
                double sigMax = 1.0 / (1.0 + Math.Exp(contrast * (midpoint - 1.0)));
                double scaledVal = val * (sigMax - sigMid) + sigMid;
                if (scaledVal <= 0 || scaledVal >= 1.0)
                    result = val;
                else
                    result = midpoint - Math.Log(1.0 / scaledVal - 1.0) / contrast;
            }

            lut[i] = (ushort)Math.Clamp(result * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
        }

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

        var resultFrame = new ImageFrame();
        resultFrame.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = resultFrame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                {
                    int bin = Quantum.ScaleToByte(srcRow[off + c]);
                    dstRow[off + c] = lut[bin];
                }
                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return resultFrame;
    }

    // ═══════════════════════════════════════════════════════════════
    // CLAHE — Contrast Limited Adaptive Histogram Equalization
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Contrast Limited Adaptive Histogram Equalization. Operates on luminance only,
    /// divides image into tiles, equalizes each tile's histogram with a clip limit,
    /// and bilinearly interpolates between tiles for smooth results.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="tilesX">Number of horizontal tiles (default 8).</param>
    /// <param name="tilesY">Number of vertical tiles (default 8).</param>
    /// <param name="clipLimit">Histogram clip limit as a factor (default 2.0). Higher = more contrast.</param>
    public static ImageFrame CLAHE(ImageFrame source, int tilesX = 8, int tilesY = 8, double clipLimit = 2.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);
        int binCount = 256;

        tilesX = Math.Max(2, Math.Min(tilesX, width));
        tilesY = Math.Max(2, Math.Min(tilesY, height));

        // Extract luminance
        var luminance = new byte[height * width];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double r = row[off] * Quantum.Scale;
                double g = row[off + 1] * Quantum.Scale;
                double b = row[off + 2] * Quantum.Scale;
                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                luminance[y * width + x] = (byte)Math.Clamp(lum * 255.0 + 0.5, 0, 255);
            }
        }

        double tileW = (double)width / tilesX;
        double tileH = (double)height / tilesY;

        // Build per-tile CDFs
        var tileCdfs = new double[tilesY, tilesX, binCount];
        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int x0 = (int)(tx * tileW);
                int y0 = (int)(ty * tileH);
                int x1 = (int)((tx + 1) * tileW);
                int y1 = (int)((ty + 1) * tileH);
                x1 = Math.Min(x1, width);
                y1 = Math.Min(y1, height);

                int tilePixels = (x1 - x0) * (y1 - y0);
                if (tilePixels == 0) continue;

                var hist = new int[binCount];
                for (int py = y0; py < y1; py++)
                    for (int px = x0; px < x1; px++)
                        hist[luminance[py * width + px]]++;

                // Apply clip limit
                int clipThreshold = (int)Math.Max(1, clipLimit * tilePixels / binCount);
                int excess = 0;
                for (int i = 0; i < binCount; i++)
                {
                    if (hist[i] > clipThreshold)
                    {
                        excess += hist[i] - clipThreshold;
                        hist[i] = clipThreshold;
                    }
                }
                int redistPerBin = excess / binCount;
                for (int i = 0; i < binCount; i++)
                    hist[i] += redistPerBin;

                // Build CDF
                long cumulative = 0;
                for (int i = 0; i < binCount; i++)
                {
                    cumulative += hist[i];
                    tileCdfs[ty, tx, i] = (double)cumulative / tilePixels;
                }
            }
        }

        // Apply CLAHE with bilinear interpolation between tiles
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            double fy = (y / tileH) - 0.5;
            int ty0 = Math.Max(0, (int)Math.Floor(fy));
            int ty1 = Math.Min(tilesY - 1, ty0 + 1);
            double wy = fy - ty0;
            wy = Math.Clamp(wy, 0, 1);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                byte lum = luminance[y * width + x];

                double fx = (x / tileW) - 0.5;
                int tx0 = Math.Max(0, (int)Math.Floor(fx));
                int tx1 = Math.Min(tilesX - 1, tx0 + 1);
                double wx = fx - tx0;
                wx = Math.Clamp(wx, 0, 1);

                // Bilinear interpolation of CDF values
                double cdf00 = tileCdfs[ty0, tx0, lum];
                double cdf10 = tileCdfs[ty0, tx1, lum];
                double cdf01 = tileCdfs[ty1, tx0, lum];
                double cdf11 = tileCdfs[ty1, tx1, lum];

                double mapped = (1 - wy) * ((1 - wx) * cdf00 + wx * cdf10) +
                                wy * ((1 - wx) * cdf01 + wx * cdf11);

                // Scale RGB channels proportionally
                double lumNorm = lum / 255.0;
                double scale = lumNorm > 1e-6 ? mapped / lumNorm : 1.0;

                for (int c = 0; c < colorChannels; c++)
                {
                    double val = srcRow[off + c] * Quantum.Scale * scale;
                    dstRow[off + c] = (ushort)Math.Clamp(val * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                }
                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Modulate — HSB brightness/saturation/hue modulation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Adjusts brightness, saturation, and hue as percentages.
    /// 100 = no change. 200 = double. 0 = zero. Hue rotates by (hue-100)*1.8 degrees.
    /// Matches ImageMagick's -modulate behavior.
    /// </summary>
    public static ImageFrame Modulate(ImageFrame source, double brightness = 100, double saturation = 100, double hue = 100)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

        double brightFactor = brightness / 100.0;
        double satFactor = saturation / 100.0;
        double hueShift = ((hue - 100.0) * 1.8) / 360.0; // convert to 0..1 hue fraction

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

                RgbToHsl(r, g, b, out double h, out double s, out double l);

                l *= brightFactor;
                s *= satFactor;
                h += hueShift;
                if (h < 0) h += 1.0;
                if (h > 1) h -= 1.0;

                l = Math.Clamp(l, 0, 1);
                s = Math.Clamp(s, 0, 1);

                HslToRgb(h, s, l, out double rr, out double gg, out double bb);

                dstRow[off] = (ushort)Math.Clamp(rr * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(gg * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(bb * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);

                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // WhiteBalance — Auto white point correction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Automatic white balance using the Gray World assumption:
    /// adjusts each channel so its mean equals the overall luminance mean.
    /// </summary>
    public static ImageFrame WhiteBalance(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);
        long totalPixels = (long)width * height;

        var channelSum = new double[colorChannels];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    channelSum[c] += row[off + c];
            }
        }

        var channelMean = new double[colorChannels];
        double overallMean = 0;
        for (int c = 0; c < colorChannels; c++)
        {
            channelMean[c] = channelSum[c] / totalPixels;
            overallMean += channelMean[c];
        }
        overallMean /= colorChannels;

        var scaleFactor = new double[colorChannels];
        for (int c = 0; c < colorChannels; c++)
        {
            scaleFactor[c] = channelMean[c] > 1e-6 ? overallMean / channelMean[c] : 1.0;
        }

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
                {
                    double val = srcRow[off + c] * scaleFactor[c];
                    dstRow[off + c] = (ushort)Math.Clamp(val + 0.5, 0, Quantum.MaxValue);
                }
                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Tint / Colorize — Apply color tint with opacity
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies a color tint by blending each pixel with the fill color.
    /// Tint values are 0-255 byte range. Amount controls blend: 0.0 = original, 1.0 = solid fill color.
    /// Matches ImageMagick's -colorize behavior: output = (1-amount)*original + amount*fill.
    /// </summary>
    public static ImageFrame Colorize(ImageFrame source, double tintR, double tintG, double tintB, double amount = 0.5)
    {
        amount = Math.Clamp(amount, 0, 1);
        double keepOriginal = 1.0 - amount;

        // Normalize tint from 0-255 byte range to quantum range
        double fillR = (tintR / 255.0) * Quantum.MaxValue;
        double fillG = (tintG / 255.0) * Quantum.MaxValue;
        double fillB = (tintB / 255.0) * Quantum.MaxValue;

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

                double outR = keepOriginal * srcRow[off] + amount * fillR;
                double outG = keepOriginal * srcRow[off + 1] + amount * fillG;
                double outB = keepOriginal * srcRow[off + 2] + amount * fillB;

                dstRow[off] = (ushort)Math.Clamp(outR + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(outG + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(outB + 0.5, 0, Quantum.MaxValue);

                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Solarize — Invert tones above a threshold
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Solarize effect: inverts pixel values that exceed the given threshold.
    /// Threshold is normalized 0.0-1.0 (default 0.5 = 50%).
    /// </summary>
    public static ImageFrame Solarize(ImageFrame source, double threshold = 0.5)
    {
        ushort thresholdQ = (ushort)(threshold * Quantum.MaxValue);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

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
                {
                    ushort val = srcRow[off + c];
                    dstRow[off + c] = val > thresholdQ ? (ushort)(Quantum.MaxValue - val) : val;
                }
                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // SepiaTone — Warm brown tone effect
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies a sepia tone effect. Threshold controls the intensity:
    /// 0.8 = subtle warm tone (default), 1.0 = stronger sepia.
    /// Uses the standard sepia matrix coefficients.
    /// </summary>
    public static ImageFrame SepiaTone(ImageFrame source, double threshold = 0.8)
    {
        threshold = Math.Clamp(threshold, 0, 1);

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
                double r = srcRow[off] * Quantum.Scale;
                double g = srcRow[off + 1] * Quantum.Scale;
                double b = srcRow[off + 2] * Quantum.Scale;

                // Standard sepia coefficients
                double sepR = 0.393 * r + 0.769 * g + 0.189 * b;
                double sepG = 0.349 * r + 0.686 * g + 0.168 * b;
                double sepB = 0.272 * r + 0.534 * g + 0.131 * b;

                // Blend between original and sepia by threshold
                double outR = r + threshold * (sepR - r);
                double outG = g + threshold * (sepG - g);
                double outB = b + threshold * (sepB - b);

                dstRow[off] = (ushort)Math.Clamp(outR * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(outG * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(outB * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);

                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal Helpers
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        l = (max + min) * 0.5;

        if (delta < 1e-10)
        {
            h = 0;
            s = 0;
            return;
        }

        s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);

        if (max == r)
            h = (g - b) / delta + (g < b ? 6.0 : 0.0);
        else if (max == g)
            h = (b - r) / delta + 2.0;
        else
            h = (r - g) / delta + 4.0;

        h /= 6.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s < 1e-10)
        {
            r = g = b = l;
            return;
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;

        r = HueToRgb(p, q, h + 1.0 / 3.0);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1.0;
        if (t > 1) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    private static ushort[] BuildEqualizeLut(long[] histogram, long totalPixels, int binCount)
    {
        // Find first non-zero bin for CDF normalization
        long cdfMin = 0;
        for (int i = 0; i < binCount; i++)
        {
            if (histogram[i] > 0)
            {
                cdfMin = histogram[i];
                break;
            }
        }

        var lut = new ushort[binCount];
        long cumulative = 0;
        double denominator = totalPixels - cdfMin;
        if (denominator <= 0) denominator = 1;

        for (int i = 0; i < binCount; i++)
        {
            cumulative += histogram[i];
            double mapped = (double)(cumulative - cdfMin) / denominator;
            lut[i] = (ushort)Math.Clamp(mapped * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
        }

        return lut;
    }

    private static ImageFrame ApplyLuts(ImageFrame source, ushort[][] luts, int colorChannels)
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
            SimdOps.ApplyLutRow(srcRow, dstRow, luts, colorChannels, channels, width);
        });

        return result;
    }

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < height; y++)
        {
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));
        }

        return result;
    }

    /// <summary>
    /// Local contrast enhancement — enhances fine detail by boosting differences between each pixel
    /// and its local neighborhood mean. Uses a separable triangular blur to compute the local mean,
    /// then applies a multiplicative contrast boost.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Neighborhood radius for local mean computation. Default 10.</param>
    /// <param name="strength">Enhancement strength as a percentage (0–100). Default 50.</param>
    public static ImageFrame LocalContrast(ImageFrame source, int radius = 10, double strength = 50.0)
    {
        if (radius < 1) radius = 1;
        strength = Math.Clamp(strength, 0, 100) / 100.0;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int colorChannels = source.HasAlpha ? channels - 1 : channels;

        // Compute local mean luminance using separable triangular blur
        // Step 1: Horizontal pass → intermediate buffer
        double[] hBlur = new double[width * height];
        Parallel.For(0, height, y =>
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                double weightSum = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int sx = Math.Clamp(x + k, 0, width - 1);
                    double weight = radius + 1 - Math.Abs(k);

                    // Use luminance for local mean
                    double luma;
                    if (colorChannels >= 3)
                    {
                        int off = sx * channels;
                        luma = 0.299 * row[off] + 0.587 * row[off + 1] + 0.114 * row[off + 2];
                    }
                    else
                    {
                        luma = row[sx * channels];
                    }

                    sum += weight * luma;
                    weightSum += weight;
                }

                hBlur[y * width + x] = sum / weightSum;
            }
        });

        // Step 2: Vertical pass → local mean buffer
        double[] localMean = new double[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                double weightSum = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int sy = Math.Clamp(y + k, 0, height - 1);
                    double weight = radius + 1 - Math.Abs(k);
                    sum += weight * hBlur[sy * width + x];
                    weightSum += weight;
                }

                localMean[y * width + x] = sum / weightSum;
            }
        });

        // Step 3: Apply local contrast boost
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double blurredLuma = localMean[y * width + x];

                // Compute current pixel luminance
                double pixelLuma;
                if (colorChannels >= 3)
                    pixelLuma = 0.299 * srcRow[off] + 0.587 * srcRow[off + 1] + 0.114 * srcRow[off + 2];
                else
                    pixelLuma = srcRow[off];

                // Compute multiplier: boost contrast based on difference from local mean
                double multiplier = 1.0;
                if (pixelLuma > 1.0) // avoid div-by-zero for pure black
                {
                    double diff = (pixelLuma - blurredLuma) * strength;
                    multiplier = (pixelLuma + diff) / pixelLuma;
                }

                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = (ushort)Math.Clamp(srcRow[off + c] * multiplier, 0, Quantum.MaxValue);

                if (source.HasAlpha)
                    dstRow[off + colorChannels] = srcRow[off + colorChannels];
            }
        });

        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  CLUT — 1D Color Lookup Table
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply a 1D color lookup table image to remap colors.
    /// The CLUT image is sampled diagonally — each pixel's normalized intensity
    /// indexes into the CLUT to produce the output color.
    /// </summary>
    public static ImageFrame ApplyClut(ImageFrame source, ImageFrame clutImage)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;
        int clutWidth = (int)clutImage.Columns;
        int clutHeight = (int)clutImage.Rows;
        int clutChannels = clutImage.NumberOfChannels;

        // Build LUT by sampling CLUT image diagonally (65536 entries for 16-bit)
        int lutSize = Quantum.MaxValue + 1;
        ushort[][] lut = new ushort[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
            lut[c] = new ushort[lutSize];

        for (int i = 0; i < lutSize; i++)
        {
            double t = (double)i / Quantum.MaxValue;
            double cx = t * (clutWidth - 1);
            double cy = t * (clutHeight - 1);
            int ix = Math.Clamp((int)cx, 0, clutWidth - 1);
            int iy = Math.Clamp((int)cy, 0, clutHeight - 1);

            var clutRow = clutImage.GetPixelRow(iy);
            int off = ix * clutChannels;
            for (int c = 0; c < colorChannels; c++)
            {
                int clutC = c < clutChannels ? c : 0;
                lut[c][i] = clutRow[off + clutC];
            }
        }

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = lut[c][srcRow[off + c]];
                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  Hald CLUT — 3D Color Lookup Table
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply a Hald CLUT (3D LUT stored as 2D image) for color grading.
    /// The Hald image encodes a level² × level² identity cube
    /// where level = sqrt(dimension) and dimension³ fits in image width.
    /// </summary>
    public static ImageFrame ApplyHaldClut(ImageFrame source, ImageFrame haldImage)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;
        int haldWidth = (int)haldImage.Columns;
        int haldChannels = haldImage.NumberOfChannels;

        // Determine cube level: find level where level³ < min(w,h)
        int haldLength = Math.Min((int)haldImage.Columns, (int)haldImage.Rows);
        int level = 2;
        while (level * level * level < haldLength) level++;
        int cubeSize = level * level; // one dimension of the 3D cube

        // Pre-read Hald image into flat array for fast random access
        int haldHeight = (int)haldImage.Rows;
        ushort[] haldPixels = new ushort[haldWidth * haldHeight * haldChannels];
        for (int y = 0; y < haldHeight; y++)
        {
            var row = haldImage.GetPixelRow(y);
            int rowBase = y * haldWidth * haldChannels;
            for (int x = 0; x < haldWidth * haldChannels; x++)
                haldPixels[rowBase + x] = row[x];
        }

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);
        double scale = (cubeSize - 1.0) / Quantum.MaxValue;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                // Get RGB as float cube coordinates [0, cubeSize-1]
                double rr = srcRow[off] * scale;
                double gg = colorChannels >= 3 ? srcRow[off + 1] * scale : rr;
                double bb = colorChannels >= 3 ? srcRow[off + 2] * scale : rr;

                // Trilinear interpolation over 8 cube corners
                int r0 = (int)rr, g0 = (int)gg, b0 = (int)bb;
                int r1 = Math.Min(r0 + 1, cubeSize - 1);
                int g1 = Math.Min(g0 + 1, cubeSize - 1);
                int b1 = Math.Min(b0 + 1, cubeSize - 1);
                double rf = rr - r0, gf = gg - g0, bf = bb - b0;

                for (int c = 0; c < colorChannels; c++)
                {
                    int haldC = c < haldChannels ? c : 0;
                    double c000 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r0, g0, b0, haldC);
                    double c001 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r0, g0, b1, haldC);
                    double c010 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r0, g1, b0, haldC);
                    double c011 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r0, g1, b1, haldC);
                    double c100 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r1, g0, b0, haldC);
                    double c101 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r1, g0, b1, haldC);
                    double c110 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r1, g1, b0, haldC);
                    double c111 = SampleHald(haldPixels, haldWidth, haldChannels, cubeSize, r1, g1, b1, haldC);

                    // Trilinear blend
                    double c00 = c000 * (1 - rf) + c100 * rf;
                    double c01 = c001 * (1 - rf) + c101 * rf;
                    double c10 = c010 * (1 - rf) + c110 * rf;
                    double c11 = c011 * (1 - rf) + c111 * rf;
                    double c0 = c00 * (1 - gf) + c10 * gf;
                    double c1 = c01 * (1 - gf) + c11 * gf;
                    double val = c0 * (1 - bf) + c1 * bf;

                    dstRow[off + c] = (ushort)Math.Clamp(val, 0, Quantum.MaxValue);
                }
                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SampleHald(ushort[] pixels, int haldWidth, int haldChannels,
        int cubeSize, int r, int g, int b, int channel)
    {
        // Hald layout: linear index = r + cubeSize * g + cubeSize * cubeSize * b
        int idx = r + cubeSize * g + cubeSize * cubeSize * b;
        int hx = idx % haldWidth;
        int hy = idx / haldWidth;
        return pixels[(hy * haldWidth + hx) * haldChannels + channel];
    }

    // ══════════════════════════════════════════════════════════════
    //  Levelize — Inverse Level
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Inverse of LevelImage — maps the full quantum range [0, MaxValue]
    /// into the specified [blackPoint, whitePoint] with gamma correction.
    /// blackPoint and whitePoint are in quantum units (0-65535).
    /// </summary>
    public static ImageFrame Levelize(ImageFrame source, double blackPoint, double whitePoint,
        double gamma = 1.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;
        double invGamma = gamma != 0 ? 1.0 / gamma : 1.0;

        // Build LUT
        ushort[] lut = new ushort[Quantum.MaxValue + 1];
        double range = whitePoint - blackPoint;
        for (int i = 0; i <= Quantum.MaxValue; i++)
        {
            double normalized = (double)i / Quantum.MaxValue;
            double gammaCorrected = Math.Pow(normalized, invGamma);
            double output = gammaCorrected * range + blackPoint;
            lut[i] = (ushort)Math.Clamp(output, 0, Quantum.MaxValue);
        }

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = lut[srcRow[off + c]];
                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  Contrast Stretch — Histogram percentile-based
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Stretches the histogram by clipping the specified percentage of pixels
    /// from each end. blackPercent and whitePercent are 0-100.
    /// </summary>
    public static ImageFrame ContrastStretch(ImageFrame source,
        double blackPercent = 0.15, double whitePercent = 0.05)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;
        long totalPixels = (long)width * height;
        double blackCount = totalPixels * blackPercent / 100.0;
        double whiteCount = totalPixels * whitePercent / 100.0;

        // Build per-channel histograms
        int histSize = Quantum.MaxValue + 1;
        long[][] histograms = new long[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
            histograms[c] = new long[histSize];

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    histograms[c][row[off + c]]++;
            }
        }

        // Find per-channel black and white levels
        ushort[] blackLevels = new ushort[colorChannels];
        ushort[] whiteLevels = new ushort[colorChannels];

        for (int c = 0; c < colorChannels; c++)
        {
            // Black level: cumulate from bottom
            long cumulative = 0;
            int bl = 0;
            for (; bl < histSize; bl++)
            {
                cumulative += histograms[c][bl];
                if (cumulative >= blackCount) break;
            }
            blackLevels[c] = (ushort)bl;

            // White level: cumulate from top
            cumulative = 0;
            int wl = histSize - 1;
            for (; wl >= 0; wl--)
            {
                cumulative += histograms[c][wl];
                if (cumulative >= whiteCount) break;
            }
            whiteLevels[c] = (ushort)wl;
        }

        // Build per-channel stretch LUTs
        ushort[][] luts = new ushort[colorChannels][];
        for (int c = 0; c < colorChannels; c++)
        {
            luts[c] = new ushort[histSize];
            int bl = blackLevels[c];
            int wl = whiteLevels[c];
            double scale = wl > bl ? (double)Quantum.MaxValue / (wl - bl) : 1.0;
            for (int i = 0; i < histSize; i++)
            {
                if (i <= bl) luts[c][i] = 0;
                else if (i >= wl) luts[c][i] = Quantum.MaxValue;
                else luts[c][i] = (ushort)Math.Clamp((i - bl) * scale, 0, Quantum.MaxValue);
            }
        }

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = luts[c][srcRow[off + c]];
                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  Linear Stretch — Histogram percentile then Level
    // ══════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════
    //  Level Colors — Map black/white to specific colors
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps the black point of the image to the specified dark color and the white point
    /// to the specified light color, with all intermediate values interpolated linearly.
    /// Colors are normalized [0..1] per channel.
    /// </summary>
    public static ImageFrame LevelColors(ImageFrame source,
        double blackR, double blackG, double blackB,
        double whiteR, double whiteG, double whiteB)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;

        // Build per-channel LUTs
        ushort[][] luts = new ushort[3][];
        double[] blacks = [blackR, blackG, blackB];
        double[] whites = [whiteR, whiteG, whiteB];

        for (int c = 0; c < Math.Min(colorChannels, 3); c++)
        {
            luts[c] = new ushort[Quantum.MaxValue + 1];
            double bk = blacks[c] * Quantum.MaxValue;
            double wk = whites[c] * Quantum.MaxValue;
            for (int i = 0; i <= Quantum.MaxValue; i++)
            {
                double t = (double)i / Quantum.MaxValue;
                luts[c][i] = (ushort)Math.Clamp(bk + t * (wk - bk), 0, Quantum.MaxValue);
            }
        }

        // For grayscale, use first LUT for all
        if (colorChannels < 3)
        {
            for (int c = 1; c < colorChannels; c++)
                luts[c] = luts[0];
        }

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = luts[c][srcRow[off + c]];
                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  Linear Stretch — Histogram percentile then Level
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds black/white levels at the given percentile thresholds,
    /// then applies a linear level remap. Uses intensity histogram
    /// (grayscale equivalent) rather than per-channel histograms.
    /// blackPercent and whitePercent are 0-100.
    /// </summary>
    public static ImageFrame LinearStretch(ImageFrame source,
        double blackPercent = 2.0, double whitePercent = 1.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;
        long totalPixels = (long)width * height;
        double blackCount = totalPixels * blackPercent / 100.0;
        double whiteCount = totalPixels * whitePercent / 100.0;

        // Build intensity histogram
        int histSize = Quantum.MaxValue + 1;
        long[] histogram = new long[histSize];

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double intensity;
                if (colorChannels >= 3)
                    intensity = 0.212656 * row[off] + 0.715158 * row[off + 1] + 0.072186 * row[off + 2];
                else
                    intensity = row[off];
                histogram[(int)Math.Clamp(intensity, 0, Quantum.MaxValue)]++;
            }
        }

        // Find black level
        long cumulative = 0;
        int blackLevel = 0;
        for (; blackLevel < histSize; blackLevel++)
        {
            cumulative += histogram[blackLevel];
            if (cumulative >= blackCount) break;
        }

        // Find white level
        cumulative = 0;
        int whiteLevel = histSize - 1;
        for (; whiteLevel >= 0; whiteLevel--)
        {
            cumulative += histogram[whiteLevel];
            if (cumulative >= whiteCount) break;
        }

        // Apply level remap using single LUT across all channels
        ushort[] lut = new ushort[histSize];
        double scale = whiteLevel > blackLevel ? (double)Quantum.MaxValue / (whiteLevel - blackLevel) : 1.0;
        for (int i = 0; i < histSize; i++)
        {
            if (i <= blackLevel) lut[i] = 0;
            else if (i >= whiteLevel) lut[i] = Quantum.MaxValue;
            else lut[i] = (ushort)Math.Clamp((i - blackLevel) * scale, 0, Quantum.MaxValue);
        }

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = lut[srcRow[off + c]];
                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }
}
