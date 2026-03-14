// SharpImage — Retouching and inpainting operations.
// Telea inpainting, Navier-Stokes inpainting, PatchMatch, CloneStamp, HealingBrush, RedEyeRemoval.
// Bundle A of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Image retouching and repair operations: inpainting, clone stamp, healing brush, red-eye removal.
/// </summary>
public static class RetouchingOps
{
    // ─── Telea Inpainting (Fast Marching Method) ───────────────────

    /// <summary>
    /// Fills masked regions by propagating known pixel values inward from the boundary
    /// using inverse-distance weighted averages. The mask should have non-zero values
    /// where inpainting is needed.
    /// radius controls how far to search for known neighbors.
    /// </summary>
    public static ImageFrame InpaintTelea(ImageFrame source, ImageFrame mask, int radius = 5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int maskChannels = mask.NumberOfChannels;

        // Clone source to result
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));

        // Build mask: true = needs inpainting
        var isMasked = new bool[height, width];
        for (int y = 0; y < height; y++)
        {
            var mRow = mask.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * maskChannels;
                isMasked[y, x] = mRow[off] > 0;
            }
        }

        // Compute distance from boundary and process inward using layered approach
        var distances = new float[height, width];
        var processed = new bool[height, width];

        // Initialize: mark known pixels as processed
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (!isMasked[y, x])
                {
                    processed[y, x] = true;
                    distances[y, x] = 0;
                }
                else
                {
                    distances[y, x] = float.MaxValue;
                }
            }

        // BFS to find boundary pixels and process layer by layer
        var current = new Queue<(int y, int x)>();
        var next = new Queue<(int y, int x)>();

        // Find initial boundary: masked pixels adjacent to known pixels
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!isMasked[y, x]) continue;
                if (HasKnownNeighbor(processed, y, x, height, width))
                {
                    current.Enqueue((y, x));
                    distances[y, x] = 1;
                }
            }
        }

        // Process layers outward
        float currentDist = 1;
        while (current.Count > 0)
        {
            while (current.Count > 0)
            {
                var (py, px) = current.Dequeue();
                if (processed[py, px]) continue;

                // Fill this pixel using weighted average of known neighbors within radius
                FillPixelTelea(result, processed, py, px, radius, channels, width, height);
                processed[py, px] = true;

                // Add unprocessed masked neighbors
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dy == 0 && dx == 0) continue;
                        int ny = py + dy, nx = px + dx;
                        if (ny < 0 || ny >= height || nx < 0 || nx >= width) continue;
                        if (processed[ny, nx] || !isMasked[ny, nx]) continue;
                        if (distances[ny, nx] > currentDist + 1)
                        {
                            distances[ny, nx] = currentDist + 1;
                            next.Enqueue((ny, nx));
                        }
                    }
                }
            }
            currentDist++;
            (current, next) = (next, current);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasKnownNeighbor(bool[,] processed, int y, int x, int height, int width)
    {
        if (y > 0 && processed[y - 1, x]) return true;
        if (y < height - 1 && processed[y + 1, x]) return true;
        if (x > 0 && processed[y, x - 1]) return true;
        if (x < width - 1 && processed[y, x + 1]) return true;
        return false;
    }

    private static void FillPixelTelea(ImageFrame result, bool[,] processed,
        int py, int px, int radius, int channels, int width, int height)
    {
        double[] sum = new double[channels];
        double totalWeight = 0;

        int y0 = Math.Max(0, py - radius);
        int y1 = Math.Min(height - 1, py + radius);
        int x0 = Math.Max(0, px - radius);
        int x1 = Math.Min(width - 1, px + radius);
        double radiusSq = radius * radius;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (!processed[y, x]) continue;

                double dy = y - py;
                double dx = x - px;
                double distSq = dy * dy + dx * dx;
                if (distSq > radiusSq || distSq == 0) continue;

                double weight = 1.0 / distSq;
                totalWeight += weight;

                var row = result.GetPixelRow(y);
                int off = x * channels;
                for (int c = 0; c < channels; c++)
                    sum[c] += row[off + c] * weight;
            }
        }

        if (totalWeight > 0)
        {
            var dstRow = result.GetPixelRowForWrite(py);
            int dstOff = px * channels;
            for (int c = 0; c < channels; c++)
                dstRow[dstOff + c] = (ushort)Math.Clamp(sum[c] / totalWeight + 0.5, 0, Quantum.MaxValue);
        }
    }

    // ─── Navier-Stokes Inpainting ──────────────────────────────────

    /// <summary>
    /// Structure-preserving inpainting using isophote continuation with Laplacian diffusion.
    /// Better at preserving edges and linear structures than Telea.
    /// </summary>
    public static ImageFrame InpaintNavierStokes(ImageFrame source, ImageFrame mask,
        int radius = 5, int iterations = 20)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int maskChannels = mask.NumberOfChannels;

        // Start with Telea result as initial guess
        var result = InpaintTelea(source, mask, radius);

        // Build mask
        var isMasked = new bool[height, width];
        for (int y = 0; y < height; y++)
        {
            var mRow = mask.GetPixelRow(y);
            for (int x = 0; x < width; x++)
                isMasked[y, x] = mRow[x * maskChannels] > 0;
        }

        // Iterative refinement using Laplacian diffusion with gradient-direction weighting
        for (int iter = 0; iter < iterations; iter++)
        {
            var temp = new ImageFrame();
            temp.Initialize(width, height, source.Colorspace, source.HasAlpha);
            for (int y = 0; y < height; y++)
                result.GetPixelRow(y).CopyTo(temp.GetPixelRowForWrite(y));

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (!isMasked[y, x]) continue;

                    var rowN = result.GetPixelRow(y - 1);
                    var rowC = result.GetPixelRow(y);
                    var rowS = result.GetPixelRow(y + 1);
                    var dstRow = temp.GetPixelRowForWrite(y);

                    for (int c = 0; c < channels; c++)
                    {
                        int off = x * channels + c;
                        int offW = (x - 1) * channels + c;
                        int offE = (x + 1) * channels + c;

                        double north = rowN[off];
                        double south = rowS[off];
                        double west = rowC[offW];
                        double east = rowC[offE];
                        double center = rowC[off];

                        // Compute gradients
                        double gx = (east - west) * 0.5;
                        double gy = (south - north) * 0.5;
                        double gradMag = Math.Sqrt(gx * gx + gy * gy);

                        // Laplacian
                        double laplacian = north + south + east + west - 4.0 * center;

                        // Isophote direction (perpendicular to gradient)
                        double isophoteWeight = gradMag > 0.001 ? 1.0 / (1.0 + gradMag) : 1.0;

                        // Update: blend between Laplacian smoothing and current value
                        double updated = center + 0.25 * laplacian * isophoteWeight;
                        dstRow[off] = (ushort)Math.Clamp(updated + 0.5, 0, Quantum.MaxValue);
                    }
                }
            }

            result.Dispose();
            result = temp;
        }

        return result;
    }

    // ─── PatchMatch ────────────────────────────────────────────────

    /// <summary>
    /// Content-aware fill using randomized nearest-neighbor patch matching.
    /// Fills masked regions by finding similar patches in the known area.
    /// patchSize should be odd (7, 9, 11...).
    /// </summary>
    public static ImageFrame PatchMatch(ImageFrame source, ImageFrame mask,
        int patchSize = 9, int iterations = 5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int maskChannels = mask.NumberOfChannels;
        int halfPatch = patchSize / 2;

        // Clone source
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));

        // Build mask
        var isMasked = new bool[height, width];
        var maskedPixels = new List<(int y, int x)>();
        for (int y = 0; y < height; y++)
        {
            var mRow = mask.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                isMasked[y, x] = mRow[x * maskChannels] > 0;
                if (isMasked[y, x])
                    maskedPixels.Add((y, x));
            }
        }

        if (maskedPixels.Count == 0) return result;

        // Build list of known pixels for random selection
        var knownPixels = new List<(int y, int x)>();
        for (int y = halfPatch; y < height - halfPatch; y++)
            for (int x = halfPatch; x < width - halfPatch; x++)
                if (!isMasked[y, x])
                    knownPixels.Add((y, x));

        if (knownPixels.Count == 0)
        {
            // Fallback: no known pixels available
            return result;
        }

        // Nearest-neighbor field: for each masked pixel, store best match coords
        var nnf = new (int y, int x)[height, width];
        var nnfDist = new double[height, width];
        var rng = new Random(42);

        // Initialize NNF with random known pixels
        foreach (var (my, mx) in maskedPixels)
        {
            var known = knownPixels[rng.Next(knownPixels.Count)];
            nnf[my, mx] = known;
            nnfDist[my, mx] = PatchSSD(source, result, isMasked, my, mx, known.y, known.x,
                halfPatch, channels, width, height);
        }

        // Iterative refinement
        for (int iter = 0; iter < iterations; iter++)
        {
            // Process pixels in alternating scan order
            bool forward = (iter % 2 == 0);
            int start = forward ? 0 : maskedPixels.Count - 1;
            int end = forward ? maskedPixels.Count : -1;
            int step = forward ? 1 : -1;

            for (int i = start; i != end; i += step)
            {
                var (py, px) = maskedPixels[i];

                // Propagation: check if neighbor's match + offset is better
                int[] dys = forward ? new[] { 0, -1 } : new[] { 0, 1 };
                int[] dxs = forward ? new[] { -1, 0 } : new[] { 1, 0 };

                for (int n = 0; n < 2; n++)
                {
                    int ny = py + dys[n];
                    int nx = px + dxs[n];
                    if (ny < 0 || ny >= height || nx < 0 || nx >= width) continue;
                    if (!isMasked[ny, nx]) continue;

                    var nMatch = nnf[ny, nx];
                    int candY = nMatch.y - dys[n];
                    int candX = nMatch.x - dxs[n];

                    if (candY < halfPatch || candY >= height - halfPatch ||
                        candX < halfPatch || candX >= width - halfPatch) continue;
                    if (isMasked[candY, candX]) continue;

                    double dist = PatchSSD(source, result, isMasked, py, px, candY, candX,
                        halfPatch, channels, width, height);
                    if (dist < nnfDist[py, px])
                    {
                        nnf[py, px] = (candY, candX);
                        nnfDist[py, px] = dist;
                    }
                }

                // Random search: exponentially decreasing radius
                int searchRadius = Math.Max(width, height);
                while (searchRadius > 1)
                {
                    int randY = py + rng.Next(-searchRadius, searchRadius + 1);
                    int randX = px + rng.Next(-searchRadius, searchRadius + 1);
                    randY = Math.Clamp(randY, halfPatch, height - halfPatch - 1);
                    randX = Math.Clamp(randX, halfPatch, width - halfPatch - 1);

                    if (!isMasked[randY, randX])
                    {
                        double dist = PatchSSD(source, result, isMasked, py, px, randY, randX,
                            halfPatch, channels, width, height);
                        if (dist < nnfDist[py, px])
                        {
                            nnf[py, px] = (randY, randX);
                            nnfDist[py, px] = dist;
                        }
                    }
                    searchRadius /= 2;
                }
            }

            // Apply NNF to fill masked pixels
            foreach (var (py, px) in maskedPixels)
            {
                var best = nnf[py, px];
                var srcRow = source.GetPixelRow(best.y);
                var dstRow = result.GetPixelRowForWrite(py);
                int sOff = best.x * channels;
                int dOff = px * channels;
                for (int c = 0; c < channels; c++)
                    dstRow[dOff + c] = srcRow[sOff + c];
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double PatchSSD(ImageFrame source, ImageFrame current, bool[,] isMasked,
        int py, int px, int qy, int qx, int halfPatch, int channels, int width, int height)
    {
        double ssd = 0;
        int count = 0;

        for (int dy = -halfPatch; dy <= halfPatch; dy++)
        {
            int sy = py + dy, ty = qy + dy;
            if (sy < 0 || sy >= height || ty < 0 || ty >= height) continue;

            var pRow = isMasked[sy, px] ? current.GetPixelRow(sy) : source.GetPixelRow(sy);
            var qRow = source.GetPixelRow(ty);

            for (int dx = -halfPatch; dx <= halfPatch; dx++)
            {
                int sx = px + dx, tx = qx + dx;
                if (sx < 0 || sx >= width || tx < 0 || tx >= width) continue;

                int pOff = sx * channels;
                int qOff = tx * channels;
                for (int c = 0; c < Math.Min(channels, 3); c++)
                {
                    double diff = pRow[pOff + c] - qRow[qOff + c];
                    ssd += diff * diff;
                }
                count++;
            }
        }

        return count > 0 ? ssd / count : double.MaxValue;
    }

    // ─── Clone Stamp ───────────────────────────────────────────────

    /// <summary>
    /// Copies a rectangular region from a source position to a target position.
    /// This is the programmatic equivalent of the clone stamp tool.
    /// </summary>
    public static ImageFrame CloneStamp(ImageFrame source,
        int srcX, int srcY, int dstX, int dstY, int stampWidth, int stampHeight)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));

        int sw = Math.Min(stampWidth, Math.Min(width - srcX, width - dstX));
        int sh = Math.Min(stampHeight, Math.Min(height - srcY, height - dstY));

        for (int y = 0; y < sh; y++)
        {
            int sy = srcY + y;
            int dy = dstY + y;
            if (sy < 0 || sy >= height || dy < 0 || dy >= height) continue;

            var srcRow = source.GetPixelRow(sy);
            var dstRow = result.GetPixelRowForWrite(dy);

            for (int x = 0; x < sw; x++)
            {
                int sx = srcX + x;
                int dx = dstX + x;
                if (sx < 0 || sx >= width || dx < 0 || dx >= width) continue;

                int sOff = sx * channels;
                int dOff = dx * channels;
                for (int c = 0; c < channels; c++)
                    dstRow[dOff + c] = srcRow[sOff + c];
            }
        }

        return result;
    }

    // ─── Healing Brush ─────────────────────────────────────────────

    /// <summary>
    /// Copies structure from source region to target, then blends luminance/color
    /// to seamlessly match the target surroundings. Combines clone stamp with
    /// Poisson-like gradient blending.
    /// </summary>
    public static ImageFrame HealingBrush(ImageFrame source,
        int srcX, int srcY, int dstX, int dstY, int stampWidth, int stampHeight)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // First, clone stamp
        var result = CloneStamp(source, srcX, srcY, dstX, dstY, stampWidth, stampHeight);

        int sw = Math.Min(stampWidth, Math.Min(width - srcX, width - dstX));
        int sh = Math.Min(stampHeight, Math.Min(height - srcY, height - dstY));

        if (sw <= 2 || sh <= 2) return result;

        // Compute mean color of the target region border in the original
        double[] borderMean = new double[channels];
        int borderCount = 0;

        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                // Only border pixels
                if (y > 0 && y < sh - 1 && x > 0 && x < sw - 1) continue;

                int iy = dstY + y, ix = dstX + x;
                if (iy < 0 || iy >= height || ix < 0 || ix >= width) continue;

                var origRow = source.GetPixelRow(iy);
                int off = ix * channels;
                for (int c = 0; c < channels; c++)
                    borderMean[c] += origRow[off + c];
                borderCount++;
            }
        }

        if (borderCount > 0)
            for (int c = 0; c < channels; c++)
                borderMean[c] /= borderCount;

        // Compute mean color of the stamped region (from source area)
        double[] stampMean = new double[channels];
        int stampCount = 0;
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                if (y > 0 && y < sh - 1 && x > 0 && x < sw - 1) continue;

                int sy = srcY + y, sx = srcX + x;
                if (sy < 0 || sy >= height || sx < 0 || sx >= width) continue;

                var srcRow = source.GetPixelRow(sy);
                int off = sx * channels;
                for (int c = 0; c < channels; c++)
                    stampMean[c] += srcRow[off + c];
                stampCount++;
            }
        }

        if (stampCount > 0)
            for (int c = 0; c < channels; c++)
                stampMean[c] /= stampCount;

        // Apply color offset to blend the stamped region with target surroundings
        double[] colorOffset = new double[channels];
        for (int c = 0; c < channels; c++)
            colorOffset[c] = borderMean[c] - stampMean[c];

        for (int y = 0; y < sh; y++)
        {
            int dy = dstY + y;
            if (dy < 0 || dy >= height) continue;
            var dstRow = result.GetPixelRowForWrite(dy);

            // Compute blend weight based on distance from border (feathered)
            double wy = Math.Min(y, sh - 1 - y) / (double)Math.Max(sh / 2, 1);
            wy = Math.Min(wy, 1.0);

            for (int x = 0; x < sw; x++)
            {
                int dx = dstX + x;
                if (dx < 0 || dx >= width) continue;
                int off = dx * channels;

                double wx = Math.Min(x, sw - 1 - x) / (double)Math.Max(sw / 2, 1);
                wx = Math.Min(wx, 1.0);
                double blendWeight = wy * wx;

                for (int c = 0; c < Math.Min(channels, 3); c++)
                {
                    double adjusted = dstRow[off + c] + colorOffset[c] * blendWeight;
                    dstRow[off + c] = (ushort)Math.Clamp(adjusted + 0.5, 0, Quantum.MaxValue);
                }
            }
        }

        return result;
    }

    // ─── Red-Eye Removal ───────────────────────────────────────────

    /// <summary>
    /// Detects and removes red-eye in a circular region. Pixels that are predominantly
    /// red (high R, low G+B) within the specified circle are desaturated and darkened.
    /// </summary>
    public static ImageFrame RedEyeRemoval(ImageFrame source,
        int centerX, int centerY, int eyeRadius, double threshold = 0.6)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));

        if (channels < 3) return result; // Need RGB

        double radiusSq = eyeRadius * eyeRadius;
        double invMax = 1.0 / Quantum.MaxValue;

        int y0 = Math.Max(0, centerY - eyeRadius);
        int y1 = Math.Min(height - 1, centerY + eyeRadius);
        int x0 = Math.Max(0, centerX - eyeRadius);
        int x1 = Math.Min(width - 1, centerX + eyeRadius);

        for (int y = y0; y <= y1; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = x0; x <= x1; x++)
            {
                double dy = y - centerY;
                double dx = x - centerX;
                if (dy * dy + dx * dx > radiusSq) continue;

                int off = x * channels;
                double r = row[off] * invMax;
                double g = row[off + 1] * invMax;
                double b = row[off + 2] * invMax;

                // Check if pixel is "red": high R relative to G and B
                double totalGB = g + b;
                double redness = totalGB > 0.001 ? r / (totalGB * 0.5 + r) : 0;

                if (redness > threshold)
                {
                    // Desaturate and darken the red component
                    double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                    double feather = (redness - threshold) / (1.0 - threshold);
                    feather = Math.Clamp(feather, 0, 1);

                    double newR = r * (1.0 - feather) + luminance * feather;
                    row[off] = (ushort)Math.Clamp(newR * Quantum.MaxValue + 0.5, 0, Quantum.MaxValue);
                    // Keep G and B unchanged
                }
            }
        }

        return result;
    }
}
