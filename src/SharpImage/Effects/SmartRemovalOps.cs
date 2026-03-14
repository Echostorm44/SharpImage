// SharpImage — Smart removal and automatic background detection.
// Saliency maps, automatic background removal, content-aware fill, object removal.
// Bundle H of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Smart removal operations: saliency detection, automatic background removal,
/// content-aware fill, and object removal with seamless inpainting.
/// </summary>
public static class SmartRemovalOps
{
    private const double InvQuantumMax = 1.0 / Quantum.MaxValue;

    // ─── Saliency Map (Spectral Residual) ──────────────────────────

    /// <summary>
    /// Computes a saliency map using the spectral residual method.
    /// Returns a grayscale ImageFrame where brighter pixels indicate more visually salient regions.
    /// Based on Hou &amp; Zhang 2007 — log spectrum → spectral residual → inverse FFT → saliency.
    /// Since we avoid full FFT, this uses a simplified spatial-domain approach:
    /// saliency = |I - mean_blur(I)| with multi-scale averaging.
    /// </summary>
    public static ImageFrame SaliencyMap(ImageFrame source, int[] scales = null!)
    {
        scales ??= [3, 7, 15, 31];

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Convert to grayscale luminance
        int pixelCount = height * width;
        var luminance = ArrayPool<double>.Shared.Rent(pixelCount);
        var saliency = ArrayPool<double>.Shared.Rent(pixelCount);
        Array.Clear(saliency, 0, pixelCount);
        double[]? rentedSaliency = saliency;
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    luminance[y * width + x] = channels >= 3
                        ? (row[off] * 0.2126 + row[off + 1] * 0.7152 + row[off + 2] * 0.0722) * InvQuantumMax
                        : row[off] * InvQuantumMax;
                }
            }

            // Multi-scale center-surround: accumulate |I - blur(I)| at each scale
            foreach (int scale in scales)
            {
                var blurred = BoxBlurDouble(luminance, width, height, scale / 2);
                for (int i = 0; i < pixelCount; i++)
                    saliency[i] += Math.Abs(luminance[i] - blurred[i]);
            }

            // Normalize to [0, 1]
            double maxSal = 0;
            for (int i = 0; i < pixelCount; i++)
                if (saliency[i] > maxSal) maxSal = saliency[i];

            if (maxSal > 0)
                for (int i = 0; i < pixelCount; i++)
                    saliency[i] /= maxSal;

            // Apply Gaussian smoothing for cleaner map — returns new heap array
            var smoothedSaliency = BoxBlurDouble(saliency, width, height, 5);
            ArrayPool<double>.Shared.Return(saliency);
            rentedSaliency = null;
            saliency = smoothedSaliency;

            // Re-normalize after blur
            maxSal = 0;
            for (int i = 0; i < pixelCount; i++)
                if (saliency[i] > maxSal) maxSal = saliency[i];
            if (maxSal > 0)
                for (int i = 0; i < pixelCount; i++)
                    saliency[i] /= maxSal;

            // Output as grayscale
            var result = new ImageFrame();
            result.Initialize(width, height, ColorspaceType.Gray, false);
            for (int y = 0; y < height; y++)
            {
                var row = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                    row[x] = Quantum.Clamp((int)(saliency[y * width + x] * Quantum.MaxValue));
            }

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(luminance);
            if (rentedSaliency != null)
                ArrayPool<double>.Shared.Return(rentedSaliency);
        }
    }

    // ─── Auto Background Remove ────────────────────────────────────

    /// <summary>
    /// Automatically removes the background from an image using a saliency-driven pipeline:
    /// 1. Compute saliency map
    /// 2. Generate trimap from saliency (foreground/background/unknown)
    /// 3. Run GrabCut refinement
    /// 4. Apply soft alpha matte
    /// Returns an RGBA image with the background made transparent.
    /// saliencyThreshold controls the initial foreground/background cutoff (0.0–1.0).
    /// borderMargin is the fraction of the saliency range used for the "unknown" band.
    /// </summary>
    public static ImageFrame AutoBackgroundRemove(ImageFrame source,
        double saliencyThreshold = 0.4, double borderMargin = 0.15, int grabCutIterations = 5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Step 1: Compute saliency
        var saliencyFrame = SaliencyMap(source);

        // Step 2: Generate trimap from saliency
        double fgThreshold = saliencyThreshold + borderMargin;
        double bgThreshold = saliencyThreshold - borderMargin;

        var trimap = new ImageFrame();
        trimap.Initialize(width, height, ColorspaceType.Gray, false);

        for (int y = 0; y < height; y++)
        {
            var salRow = saliencyFrame.GetPixelRow(y);
            var triRow = trimap.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double sal = salRow[x] * InvQuantumMax;
                if (sal >= fgThreshold)
                    triRow[x] = Quantum.MaxValue; // Definite foreground
                else if (sal <= bgThreshold)
                    triRow[x] = 0; // Definite background
                else
                    triRow[x] = (ushort)(Quantum.MaxValue / 2); // Unknown
            }
        }

        // Step 3: Build trimap ImageFrame for GrabCut (white=FG, black=BG, gray=unknown)
        var grabCutTrimap = new ImageFrame();
        grabCutTrimap.Initialize(width, height, ColorspaceType.Gray, false);
        for (int y = 0; y < height; y++)
        {
            var triRow = trimap.GetPixelRow(y);
            var gcRow = grabCutTrimap.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double val = triRow[x] * InvQuantumMax;
                // GrabCut trimap: white (MaxValue) = foreground, black (0) = background, gray = unknown
                gcRow[x] = val > 0.75 ? Quantum.MaxValue
                    : val < 0.25 ? (ushort)0
                    : (ushort)(Quantum.MaxValue / 2);
            }
        }

        var refinedMask = SelectionOps.GrabCut(source, grabCutTrimap, grabCutIterations);

        // Step 4: Feather the mask edges slightly for smoother results
        var featheredMask = SelectionOps.FeatherMask(refinedMask, 2.0);

        // Step 5: Apply mask to create RGBA output
        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.RGB, true);

        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var maskRow = featheredMask.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int maskCh = featheredMask.NumberOfChannels;

            for (int x = 0; x < width; x++)
            {
                int srcOff = x * channels;
                int dstOff = x * 4; // RGBA

                // Copy RGB
                dstRow[dstOff] = channels >= 3 ? srcRow[srcOff] : srcRow[srcOff];
                dstRow[dstOff + 1] = channels >= 3 ? srcRow[srcOff + 1] : srcRow[srcOff];
                dstRow[dstOff + 2] = channels >= 3 ? srcRow[srcOff + 2] : srcRow[srcOff];

                // Alpha from mask
                dstRow[dstOff + 3] = maskRow[x * maskCh];
            }
        }

        saliencyFrame.Dispose();
        trimap.Dispose();
        grabCutTrimap.Dispose();
        refinedMask.Dispose();
        featheredMask.Dispose();

        return result;
    }

    // ─── Content-Aware Fill ────────────────────────────────────────

    /// <summary>
    /// Fills masked regions with content that seamlessly blends with the surroundings.
    /// Uses PatchMatch-based exemplar synthesis followed by Poisson-like gradient blending
    /// for a seamless result. The mask should have non-zero values where fill is needed.
    /// patchSize controls the synthesis patch radius (larger = more context).
    /// </summary>
    public static ImageFrame ContentAwareFill(ImageFrame source, ImageFrame mask,
        int patchSize = 9, int patchMatchIterations = 5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int maskChannels = mask.NumberOfChannels;

        // Step 1: Use PatchMatch to find best patches for masked regions
        var filled = RetouchingOps.PatchMatch(source, mask, patchSize, patchMatchIterations);

        // Step 2: Gradient-domain blending at mask boundary for seamless transition
        // Compute boundary pixels (mask edge)
        var isMasked = new bool[height * width];
        for (int y = 0; y < height; y++)
        {
            var mRow = mask.GetPixelRow(y);
            for (int x = 0; x < width; x++)
                isMasked[y * width + x] = mRow[x * maskChannels] > Quantum.MaxValue / 4;
        }

        // Iterative Poisson relaxation on the filled image within mask boundary
        int blendIterations = 50;
        int bufLen = height * width * channels;
        var current = ArrayPool<double>.Shared.Rent(bufLen);
        var next = ArrayPool<double>.Shared.Rent(bufLen);
        var sourceData = ArrayPool<double>.Shared.Rent(bufLen);

        try
        {
            // Initialize from filled result
            for (int y = 0; y < height; y++)
            {
                var row = filled.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * channels;
                    int off = x * channels;
                    for (int c = 0; c < channels; c++)
                        current[idx + c] = row[off + c];
                }
            }

            // Get source gradient field
            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * channels;
                    int off = x * channels;
                    for (int c = 0; c < channels; c++)
                        sourceData[idx + c] = row[off + c];
                }
            }

            Array.Copy(current, next, bufLen);

            for (int iter = 0; iter < blendIterations; iter++)
            {
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (!isMasked[y * width + x]) continue;

                        int idx = (y * width + x) * channels;
                        int idxN = ((y - 1) * width + x) * channels;
                        int idxS = ((y + 1) * width + x) * channels;
                        int idxW = (y * width + (x - 1)) * channels;
                        int idxE = (y * width + (x + 1)) * channels;

                        for (int c = 0; c < channels; c++)
                        {
                            // Laplacian from filled image's gradients
                            double laplacian = current[idxN + c] + current[idxS + c]
                                             + current[idxW + c] + current[idxE + c];
                            next[idx + c] = laplacian / 4.0;
                        }
                    }
                }

                // Swap
                (current, next) = (next, current);
            }

            // Write result
            var result = new ImageFrame();
            result.Initialize(width, height, source.Colorspace, source.HasAlpha);
            for (int y = 0; y < height; y++)
            {
                var dstRow = result.GetPixelRowForWrite(y);
                var srcRow = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    if (isMasked[y * width + x])
                    {
                        int idx = (y * width + x) * channels;
                        for (int c = 0; c < channels; c++)
                            dstRow[off + c] = Quantum.Clamp((int)current[idx + c]);
                    }
                    else
                    {
                        for (int c = 0; c < channels; c++)
                            dstRow[off + c] = srcRow[off + c];
                    }
                }
            }

            filled.Dispose();
            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(current);
            ArrayPool<double>.Shared.Return(next);
            ArrayPool<double>.Shared.Return(sourceData);
        }
    }

    // ─── Object Remove ─────────────────────────────────────────────

    /// <summary>
    /// Removes an object defined by a mask and fills the region with surrounding content.
    /// Combines content-aware fill with boundary feathering for a natural result.
    /// The mask should have non-zero values where the object to remove is located.
    /// </summary>
    public static ImageFrame ObjectRemove(ImageFrame source, ImageFrame mask,
        int patchSize = 9, double featherRadius = 3.0)
    {
        // Slightly expand the mask to cover edge artifacts
        var expandedMask = SelectionOps.ExpandMask(mask, (int)Math.Ceiling(featherRadius));

        // Content-aware fill the expanded region
        var filled = ContentAwareFill(source, expandedMask, patchSize);

        // Feather the mask for smooth blending
        var feathered = SelectionOps.FeatherMask(expandedMask, featherRadius);

        // Blend: where feathered mask is white use filled, where black use original
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int maskCh = feathered.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var fillRow = filled.GetPixelRow(y);
            var mskRow = feathered.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                double alpha = mskRow[x * maskCh] * InvQuantumMax;
                int off = x * channels;

                for (int c = 0; c < channels; c++)
                {
                    double blended = fillRow[off + c] * alpha + srcRow[off + c] * (1.0 - alpha);
                    dstRow[off + c] = Quantum.Clamp((int)blended);
                }
            }
        }

        expandedMask.Dispose();
        filled.Dispose();
        feathered.Dispose();

        return result;
    }

    // ─── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Simple box blur on a double array. Used for saliency map smoothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double[] BoxBlurDouble(double[] data, int width, int height, int radius)
    {
        int len = width * height;
        var result = new double[len];
        var temp = ArrayPool<double>.Shared.Rent(len);

        try
        {

            // Horizontal pass
            for (int y = 0; y < height; y++)
            {
                double sum = 0;
                int count = 0;

                // Initialize window
                for (int x = 0; x <= radius && x < width; x++)
                {
                    sum += data[y * width + x];
                    count++;
                }

                double invCount = 1.0 / count;
                for (int x = 0; x < width; x++)
                {
                    temp[y * width + x] = sum * invCount;

                    // Expand right
                    int right = x + radius + 1;
                    int left = x - radius;
                    int prevCount = count;
                    if (right < width) { sum += data[y * width + right]; count++; }

                    // Shrink left
                    if (left >= 0) { sum -= data[y * width + left]; count--; }
                    if (count != prevCount) invCount = 1.0 / count;
                }
            }

            // Vertical pass
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                int count = 0;

                for (int y = 0; y <= radius && y < height; y++)
                {
                    sum += temp[y * width + x];
                    count++;
                }

                double invCount = 1.0 / count;
                for (int y = 0; y < height; y++)
                {
                    result[y * width + x] = sum * invCount;

                    int bottom = y + radius + 1;
                    int top = y - radius;
                    int prevCount = count;
                    if (bottom < height) { sum += temp[bottom * width + x]; count++; }

                    if (top >= 0) { sum -= temp[top * width + x]; count--; }
                    if (count != prevCount) invCount = 1.0 / count;
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(temp);
        }
    }
}
