// SharpImage — HDR and multi-exposure operations.
// HDR merge, tone mapping (Reinhard, Drago), exposure fusion (Mertens).
// Bundle K of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SharpImage.Effects;

/// <summary>
/// HDR and multi-exposure operations: merge exposure brackets into HDR,
/// tone map HDR to displayable range, and exposure fusion.
/// </summary>
public static class HdrOps
{
    // ─── HDR Merge ─────────────────────────────────────────────────

    /// <summary>
    /// Merges multiple exposure images into a single HDR image.
    /// Uses Debevec-style weighted averaging where well-exposed pixels contribute more.
    /// exposureValues should match the images array (EV for each image).
    /// Returns a high dynamic range image (values may exceed normal quantum range internally,
    /// stored as clamped 16-bit but with maximum fidelity).
    /// </summary>
    public static ImageFrame HdrMerge(ImageFrame[] images, double[] exposureValues)
    {
        if (images.Length == 0 || images.Length != exposureValues.Length)
            throw new ArgumentException("Images and exposure values must have the same non-zero count.");

        int width = (int)images[0].Columns;
        int height = (int)images[0].Rows;
        int channels = images[0].NumberOfChannels;
        int colorChannels = images[0].HasAlpha ? channels - 1 : channels;

        // HDR accumulation in double precision — per-pixel weight (not per-channel)
        int hdrDataLen = height * width * colorChannels;
        int weightSumLen = height * width;
        var hdrData = ArrayPool<double>.Shared.Rent(hdrDataLen);
        var weightSum = ArrayPool<double>.Shared.Rent(weightSumLen);
        Array.Clear(hdrData, 0, hdrDataLen);
        Array.Clear(weightSum, 0, weightSumLen);

        try
        {
            for (int imgIdx = 0; imgIdx < images.Length; imgIdx++)
            {
                double exposure = Math.Pow(2, exposureValues[imgIdx]);

                for (int y = 0; y < height; y++)
                {
                    var row = images[imgIdx].GetPixelRow(y);
                    for (int x = 0; x < width; x++)
                    {
                        int off = x * channels;
                        int pidx = y * width + x;
                        int cidx = pidx * colorChannels;

                        // Compute luminance for per-pixel weight
                        double lum;
                        if (colorChannels >= 3)
                            lum = (row[off] * 0.2126 + row[off + 1] * 0.7152 + row[off + 2] * 0.0722) * Quantum.Scale;
                        else
                            lum = row[off] * Quantum.Scale;

                        double weight = HatWeight(lum);

                        for (int c = 0; c < colorChannels; c++)
                        {
                            double normalized = row[off + c] * Quantum.Scale;
                            double radiance = normalized / exposure;
                            hdrData[cidx + c] += weight * radiance;
                        }
                        weightSum[pidx] += weight;
                    }
                }
            }

            // Normalize — fall back to simple average when all weights are zero
            for (int p = 0; p < width * height; p++)
            {
                int cidx = p * colorChannels;
                if (weightSum[p] > 0)
                {
                    for (int c = 0; c < colorChannels; c++)
                        hdrData[cidx + c] /= weightSum[p];
                }
                else
                {
                    // All images had extreme values here — use simple average of radiance
                    double invCount = 1.0 / images.Length;
                    for (int c = 0; c < colorChannels; c++)
                        hdrData[cidx + c] *= invCount;
                }
            }

            // Find max radiance for normalization to quantum range
            double maxVal = 0;
            for (int i = 0; i < hdrDataLen; i++)
                if (hdrData[i] > maxVal) maxVal = hdrData[i];

            var result = new ImageFrame();
            result.Initialize(width, height, images[0].Colorspace, images[0].HasAlpha);

            double scale = maxVal > 0 ? Quantum.MaxValue / maxVal : 1.0;
            for (int y = 0; y < height; y++)
            {
                var dstRow = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    int pidx = y * width + x;
                    int cidx = pidx * colorChannels;

                    for (int c = 0; c < colorChannels; c++)
                        dstRow[off + c] = Quantum.Clamp((int)(hdrData[cidx + c] * scale));

                    // Preserve alpha from first image
                    if (images[0].HasAlpha)
                        dstRow[off + channels - 1] = images[0].GetPixelRow(y)[off + channels - 1];
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(hdrData);
            ArrayPool<double>.Shared.Return(weightSum);
        }
    }

    // ─── Tone Mapping: Reinhard ────────────────────────────────────

    /// <summary>
    /// Applies Reinhard global tone mapping operator.
    /// Maps HDR luminance to displayable range: L_display = L / (1 + L).
    /// key controls the overall brightness (0.09 = dark, 0.18 = mid, 0.36 = bright).
    /// saturation controls color saturation preservation (1.0 = full).
    /// </summary>
    public static ImageFrame ToneMapReinhard(ImageFrame source, double key = 0.18, double saturation = 1.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Compute log-average luminance
        double logLumSum = 0;
        double delta = 1e-6;
        int pixelCount = width * height;

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double lum = channels >= 3
                    ? (row[off] * 0.2126 + row[off + 1] * 0.7152 + row[off + 2] * 0.0722) * Quantum.Scale
                    : row[off] * Quantum.Scale;
                logLumSum += Math.Log(lum + delta);
            }
        }
        double logAvgLum = Math.Exp(logLumSum / pixelCount);

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        double keyOverLogAvg = key / logAvgLum;
        bool hasAlpha = source.HasAlpha;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                double lum = channels >= 3
                    ? (srcRow[off] * 0.2126 + srcRow[off + 1] * 0.7152 + srcRow[off + 2] * 0.0722) * Quantum.Scale
                    : srcRow[off] * Quantum.Scale;

                // Scale luminance by key
                double scaledLum = keyOverLogAvg * lum;
                // Reinhard operator
                double mappedLum = scaledLum / (1.0 + scaledLum);

                if (channels >= 3)
                {
                    double ratio = lum > delta ? mappedLum / lum : 0;
                    for (int c = 0; c < 3; c++)
                    {
                        double val = srcRow[off + c] * Quantum.Scale;
                        // Saturation-preserving: lerp between luminance-only and full color
                        double mapped = Math.Pow(val * ratio, saturation) *
                            Math.Pow(mappedLum, 1.0 - saturation);
                        dstRow[off + c] = Quantum.Clamp((int)(mapped * Quantum.MaxValue));
                    }
                }
                else
                {
                    dstRow[off] = Quantum.Clamp((int)(mappedLum * Quantum.MaxValue));
                }

                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ─── Tone Mapping: Drago ───────────────────────────────────────

    /// <summary>
    /// Applies Drago logarithmic tone mapping.
    /// Uses adaptive logarithmic mapping: L_display = log(1 + L) / log(1 + L_max).
    /// bias controls the contrast (0.7–0.9 typical, higher = more contrast).
    /// </summary>
    public static ImageFrame ToneMapDrago(ImageFrame source, double bias = 0.85)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        double delta = 1e-6;

        // Find max luminance
        double maxLum = 0;
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double lum = channels >= 3
                    ? (row[off] * 0.2126 + row[off + 1] * 0.7152 + row[off + 2] * 0.0722) * Quantum.Scale
                    : row[off] * Quantum.Scale;
                if (lum > maxLum) maxLum = lum;
            }
        }

        double logMaxLum = Math.Log10(1 + maxLum);
        double biasP = Math.Log(bias) / Math.Log(0.5);
        double invMaxLumDelta = 1.0 / (maxLum + delta);
        bool hasAlpha = source.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                double lum = channels >= 3
                    ? (srcRow[off] * 0.2126 + srcRow[off + 1] * 0.7152 + srcRow[off + 2] * 0.0722) * Quantum.Scale
                    : srcRow[off] * Quantum.Scale;

                // Drago operator with adaptive bias
                double mapped = Math.Log10(1 + lum) /
                    (logMaxLum * (Math.Log10(2 + 8 * Math.Pow(lum * invMaxLumDelta, biasP))));

                mapped = Math.Clamp(mapped, 0, 1);

                if (channels >= 3)
                {
                    double ratio = lum > delta ? mapped / lum : 0;
                    for (int c = 0; c < 3; c++)
                    {
                        double val = srcRow[off + c] * Quantum.Scale;
                        dstRow[off + c] = Quantum.Clamp((int)(val * ratio * Quantum.MaxValue));
                    }
                }
                else
                {
                    dstRow[off] = Quantum.Clamp((int)(mapped * Quantum.MaxValue));
                }

                if (hasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    // ─── Exposure Fusion (Mertens) ─────────────────────────────────

    /// <summary>
    /// Fuses multiple exposures directly into an LDR image without creating an HDR intermediate.
    /// Uses the Mertens algorithm: compute quality measures (contrast, saturation, well-exposedness)
    /// for each pixel in each image, then blend using normalized weights.
    /// No exposure values needed — works purely on pixel quality.
    /// </summary>
    public static ImageFrame ExposureFusion(ImageFrame[] images,
        double contrastWeight = 1.0, double saturationWeight = 1.0, double exposednessWeight = 1.0)
    {
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required.");
        if (images.Length == 1)
            return CloneFrame(images[0]);

        int width = (int)images[0].Columns;
        int height = (int)images[0].Rows;
        int channels = images[0].NumberOfChannels;
        int pixelCount = width * height;

        // Compute weight maps for each image
        var weights = new double[images.Length][];
        for (int i = 0; i < images.Length; i++)
            weights[i] = ComputeMertensWeights(images[i], contrastWeight, saturationWeight, exposednessWeight);

        // Normalize weights across images for each pixel
        for (int p = 0; p < pixelCount; p++)
        {
            double sum = 0;
            for (int i = 0; i < images.Length; i++)
                sum += weights[i][p];

            if (sum > 0)
            {
                for (int i = 0; i < images.Length; i++)
                    weights[i][p] /= sum;
            }
            else
            {
                // Equal weight if all zero
                double equal = 1.0 / images.Length;
                for (int i = 0; i < images.Length; i++)
                    weights[i][p] = equal;
            }
        }

        // Weighted blend
        var result = new ImageFrame();
        result.Initialize(width, height, images[0].Colorspace, images[0].HasAlpha);

        for (int y = 0; y < height; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                int pidx = y * width + x;

                for (int c = 0; c < channels; c++)
                {
                    double val = 0;
                    for (int i = 0; i < images.Length; i++)
                    {
                        var srcRow = images[i].GetPixelRow(y);
                        val += srcRow[off + c] * weights[i][pidx];
                    }
                    dstRow[off + c] = Quantum.Clamp((int)val);
                }
            }
        }

        return result;
    }

    // ─── Helpers ────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HatWeight(double value)
    {
        // Triangle weight: peak at 0.5, zero at 0 and 1
        return value <= 0.5 ? 2.0 * value : 2.0 * (1.0 - value);
    }

    private static double[] ComputeMertensWeights(ImageFrame image,
        double contrastW, double saturationW, double exposednessW)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        int pixelCount = width * height;
        var weights = new double[pixelCount];

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;

                double r = channels >= 3 ? row[off] * Quantum.Scale : row[off] * Quantum.Scale;
                double g = channels >= 3 ? row[off + 1] * Quantum.Scale : r;
                double b = channels >= 3 ? row[off + 2] * Quantum.Scale : r;

                // Well-exposedness: Gaussian centered at 0.5
                double expR = Math.Exp(-12.5 * (r - 0.5) * (r - 0.5));
                double expG = Math.Exp(-12.5 * (g - 0.5) * (g - 0.5));
                double expB = Math.Exp(-12.5 * (b - 0.5) * (b - 0.5));
                double exposedness = Math.Pow(expR * expG * expB, exposednessW);

                // Saturation: std dev of channels
                double mean = (r + g + b) / 3.0;
                double sat = channels >= 3
                    ? Math.Sqrt(((r - mean) * (r - mean) + (g - mean) * (g - mean) + (b - mean) * (b - mean)) / 3.0)
                    : 0;
                double satPow = Math.Pow(sat + 1e-10, saturationW);

                // Contrast: Laplacian magnitude (computed below for interior pixels)
                double contrast = 0;
                if (y > 0 && y < height - 1 && x > 0 && x < width - 1)
                {
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    var rowUp = image.GetPixelRow(y - 1);
                    var rowDn = image.GetPixelRow(y + 1);

                    double lumUp = channels >= 3
                        ? (rowUp[off] * 0.2126 + rowUp[off + 1] * 0.7152 + rowUp[off + 2] * 0.0722) * Quantum.Scale
                        : rowUp[off] * Quantum.Scale;
                    double lumDn = channels >= 3
                        ? (rowDn[off] * 0.2126 + rowDn[off + 1] * 0.7152 + rowDn[off + 2] * 0.0722) * Quantum.Scale
                        : rowDn[off] * Quantum.Scale;
                    double lumL = channels >= 3
                        ? (row[off - channels] * 0.2126 + row[off - channels + 1] * 0.7152 + row[off - channels + 2] * 0.0722) * Quantum.Scale
                        : row[off - 1] * Quantum.Scale;
                    double lumR = channels >= 3
                        ? (row[off + channels] * 0.2126 + row[off + channels + 1] * 0.7152 + row[off + channels + 2] * 0.0722) * Quantum.Scale
                        : row[off + 1] * Quantum.Scale;

                    contrast = Math.Abs(4 * lum - lumUp - lumDn - lumL - lumR);
                }
                double contPow = Math.Pow(contrast + 1e-10, contrastW);

                weights[y * width + x] = contPow * satPow * exposedness + 1e-12;
            }
        }

        return weights;
    }

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var clone = new ImageFrame();
        clone.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }
        return clone;
    }
}
