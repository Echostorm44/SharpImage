// SharpImage — Selection and masking operations.
// GrabCut segmentation, alpha matting, flood select, feathering, mask operations.
// Bundle B of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Selection and masking: GrabCut foreground extraction, alpha matting, flood select (magic wand),
/// feathering, and boolean mask operations.
/// </summary>
public static class SelectionOps
{
    // ─── GrabCut Foreground Segmentation ─────────────────────────────

    /// <summary>
    /// Pixel class labels for GrabCut segmentation.
    /// </summary>
    public enum GrabCutLabel : byte
    {
        Background = 0,
        Foreground = 1,
        ProbableBackground = 2,
        ProbableForeground = 3
    }

    /// <summary>
    /// Extracts foreground from background using iterative GMM-based graph cut.
    /// The trimap frame encodes pixel labels: 0=background, 65535=foreground, mid-values=probable foreground.
    /// Returns a grayscale mask where white = foreground.
    /// </summary>
    public static ImageFrame GrabCut(ImageFrame source, ImageFrame trimap, int iterations = 5, int gmmComponents = 5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int trimapChannels = trimap.NumberOfChannels;

        // Parse trimap into labels
        var labels = new GrabCutLabel[height * width];
        for (int y = 0; y < height; y++)
        {
            var tRow = trimap.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                ushort v = tRow[x * trimapChannels];
                if (v > Quantum.MaxValue * 3 / 4)
                    labels[y * width + x] = GrabCutLabel.Foreground;
                else if (v < Quantum.MaxValue / 4)
                    labels[y * width + x] = GrabCutLabel.Background;
                else
                    labels[y * width + x] = GrabCutLabel.ProbableForeground;
            }
        }

        // Extract RGB pixels as doubles
        int pixelsLen = height * width * 3;
        var pixels = ArrayPool<double>.Shared.Rent(pixelsLen);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var sRow = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    int pi = (y * width + x) * 3;
                    pixels[pi] = sRow[off];
                    pixels[pi + 1] = channels > 1 ? sRow[off + 1] : sRow[off];
                    pixels[pi + 2] = channels > 2 ? sRow[off + 2] : sRow[off];
                }
            }

            // Build GMMs for foreground and background
            var fgGmm = new GmmModel(gmmComponents);
            var bgGmm = new GmmModel(gmmComponents);

            // Initial GMM assignment from hard labels
            InitializeGmms(pixels, labels, fgGmm, bgGmm, width, height);

            // Compute beta for N-link weight
            double beta = ComputeBeta(pixels, width, height);

            // Iterative refinement
            for (int iter = 0; iter < iterations; iter++)
            {
                // Assign each pixel to closest GMM component
                AssignGmmComponents(pixels, labels, fgGmm, bgGmm, width, height);

                // Learn GMM parameters from assignments
                fgGmm.LearnParameters(pixels, labels, width, height, isForeground: true);
                bgGmm.LearnParameters(pixels, labels, width, height, isForeground: false);

                // Graph cut: simplified energy minimization via iterated conditional modes
                RefineLabelsICM(pixels, labels, fgGmm, bgGmm, beta, width, height);
            }

            // Build result mask
            var result = new ImageFrame();
            result.Initialize(width, height, ColorspaceType.Gray, false);
            for (int y = 0; y < height; y++)
            {
                var dstRow = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    var label = labels[y * width + x];
                    dstRow[x] = (label == GrabCutLabel.Foreground || label == GrabCutLabel.ProbableForeground)
                        ? Quantum.MaxValue : (ushort)0;
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(pixels);
        }
    }

    // ─── Alpha Matting ───────────────────────────────────────────────

    /// <summary>
    /// Extracts a soft alpha matte from a trimap using KNN-based matting.
    /// Trimap: 0=background, 65535=foreground, mid-values=unknown region.
    /// Returns a grayscale frame with continuous alpha values.
    /// </summary>
    public static ImageFrame AlphaMatting(ImageFrame source, ImageFrame trimap, int kNeighbors = 20, double sigma = 10.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int trimapChannels = trimap.NumberOfChannels;

        // Parse trimap: 0=definite BG, 1=definite FG, -1=unknown
        int alphaLen = height * width;
        var trimapState = ArrayPool<int>.Shared.Rent(alphaLen);
        var pixelColors = ArrayPool<double>.Shared.Rent(alphaLen * 3);
        var alpha = ArrayPool<double>.Shared.Rent(alphaLen);
        try
        {
            var unknownPixels = new List<int>(); // flat indices of unknown pixels
            var knownFg = new List<int>();
            var knownBg = new List<int>();

            for (int y = 0; y < height; y++)
            {
                var tRow = trimap.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    ushort v = tRow[x * trimapChannels];
                    int idx = y * width + x;
                    if (v > Quantum.MaxValue * 3 / 4)
                    {
                        trimapState[idx] = 1;
                        knownFg.Add(idx);
                    }
                    else if (v < Quantum.MaxValue / 4)
                    {
                        trimapState[idx] = 0;
                        knownBg.Add(idx);
                    }
                    else
                    {
                        trimapState[idx] = -1;
                        unknownPixels.Add(idx);
                    }
                }
            }

            // Extract pixel colors
            for (int y = 0; y < height; y++)
            {
                var sRow = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    int pi = (y * width + x) * 3;
                    pixelColors[pi] = sRow[off] * Quantum.Scale;
                    pixelColors[pi + 1] = channels > 1 ? sRow[off + 1] * Quantum.Scale : pixelColors[pi];
                    pixelColors[pi + 2] = channels > 2 ? sRow[off + 2] * Quantum.Scale : pixelColors[pi];
                }
            }

            // Build alpha values
            for (int i = 0; i < alphaLen; i++)
            {
                alpha[i] = trimapState[i] switch
                {
                    1 => 1.0,
                    0 => 0.0,
                    _ => 0.5
                };
            }

            // For each unknown pixel, use KNN from known FG and BG to estimate alpha
            double sigmaSq = sigma * sigma;
            int k = Math.Min(kNeighbors, Math.Min(knownFg.Count, knownBg.Count));
            if (k < 1) k = 1;

            Parallel.For(0, unknownPixels.Count, ui =>
            {
                int idx = unknownPixels[ui];
                int px = idx % width;
                int py = idx / width;
                double pr = pixelColors[idx * 3];
                double pg = pixelColors[idx * 3 + 1];
                double pb = pixelColors[idx * 3 + 2];

                // Find k nearest FG and BG by color distance
                double bestFgDist = FindKnnAlpha(pixelColors, knownFg, pr, pg, pb, k, width, px, py, sigmaSq);
                double bestBgDist = FindKnnAlpha(pixelColors, knownBg, pr, pg, pb, k, width, px, py, sigmaSq);

                // Alpha from relative distance: closer to FG → higher alpha
                double totalDist = bestFgDist + bestBgDist;
                alpha[idx] = totalDist > 1e-12 ? bestBgDist / totalDist : 0.5;
            });

            // Build result
            var result = new ImageFrame();
            result.Initialize(width, height, ColorspaceType.Gray, false);
            for (int y = 0; y < height; y++)
            {
                var dstRow = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                    dstRow[x] = (ushort)Math.Clamp(alpha[y * width + x] * Quantum.MaxValue, 0, Quantum.MaxValue);
            }

            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(trimapState);
            ArrayPool<double>.Shared.Return(pixelColors);
            ArrayPool<double>.Shared.Return(alpha);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double FindKnnAlpha(double[] pixelColors, List<int> knownIndices,
        double pr, double pg, double pb, int k, int width, int px, int py, double sigmaSq)
    {
        // Partial sort for k nearest by combined color + spatial distance
        var distances = ArrayPool<double>.Shared.Rent(knownIndices.Count);
        try
        {
            for (int i = 0; i < knownIndices.Count; i++)
            {
                int ki = knownIndices[i];
                double dr = pixelColors[ki * 3] - pr;
                double dg = pixelColors[ki * 3 + 1] - pg;
                double db = pixelColors[ki * 3 + 2] - pb;
                double colorDist = dr * dr + dg * dg + db * db;

                int kx = ki % width, ky = ki / width;
                double spatialDist = ((kx - px) * (kx - px) + (ky - py) * (ky - py)) / sigmaSq;
                distances[i] = colorDist + spatialDist * 0.1;
            }

            // Find k-th smallest distance and compute weighted average
            var span = distances.AsSpan(0, knownIndices.Count);
            double weightedDist = 0;
            double weightSum = 0;

            // Simple partial sort: pick k smallest
            for (int j = 0; j < k; j++)
            {
                int minIdx = j;
                for (int m = j + 1; m < span.Length; m++)
                    if (span[m] < span[minIdx]) minIdx = m;
                (span[j], span[minIdx]) = (span[minIdx], span[j]);

                double w = Math.Exp(-span[j] / (2.0 * sigmaSq + 1e-12));
                weightedDist += w * span[j];
                weightSum += w;
            }

            return weightSum > 1e-12 ? weightedDist / weightSum : 1.0;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(distances);
        }
    }

    // ─── Flood Select (Magic Wand) ──────────────────────────────────

    /// <summary>
    /// Selects a contiguous region of similar color starting from (seedX, seedY).
    /// tolerancePercent is 0-100 controlling how much color deviation is allowed.
    /// Returns a grayscale mask where selected pixels are white.
    /// </summary>
    public static ImageFrame FloodSelect(ImageFrame source, int seedX, int seedY,
        double tolerancePercent = 10.0, bool eightConnected = false)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        seedX = Math.Clamp(seedX, 0, width - 1);
        seedY = Math.Clamp(seedY, 0, height - 1);

        // Get seed color
        var seedRow = source.GetPixelRow(seedY);
        int seedOff = seedX * channels;
        double seedR = seedRow[seedOff];
        double seedG = channels > 1 ? seedRow[seedOff + 1] : seedR;
        double seedB = channels > 2 ? seedRow[seedOff + 2] : seedR;

        double fuzz = tolerancePercent / 100.0 * Quantum.MaxValue;
        double fuzzSq = fuzz * fuzz;

        var visited = new bool[height * width];
        var selected = new bool[height * width];
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((seedX, seedY));
        visited[seedY * width + seedX] = true;

        ReadOnlySpan<(int dx, int dy)> neighbors4 = [(0, -1), (0, 1), (-1, 0), (1, 0)];
        ReadOnlySpan<(int dx, int dy)> neighbors8 = [(0, -1), (0, 1), (-1, 0), (1, 0), (-1, -1), (1, -1), (-1, 1), (1, 1)];

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            selected[y * width + x] = true;

            var neighborSet = eightConnected ? neighbors8 : neighbors4;
            foreach (var (dx, dy) in neighborSet)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                int ni = ny * width + nx;
                if (visited[ni]) continue;
                visited[ni] = true;

                var nRow = source.GetPixelRow(ny);
                int no = nx * channels;
                double nR = nRow[no];
                double nG = channels > 1 ? nRow[no + 1] : nR;
                double nB = channels > 2 ? nRow[no + 2] : nR;

                double dr = nR - seedR, dg = nG - seedG, db = nB - seedB;
                if (dr * dr + dg * dg + db * db <= fuzzSq)
                    queue.Enqueue((nx, ny));
            }
        }

        // Build result mask
        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);
        for (int y = 0; y < height; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
                dstRow[x] = selected[y * width + x] ? Quantum.MaxValue : (ushort)0;
        }

        return result;
    }

    /// <summary>
    /// Selects all pixels in the image matching the given color within tolerance, regardless of connectivity.
    /// Returns a grayscale mask.
    /// </summary>
    public static ImageFrame ColorRangeSelect(ImageFrame source, ushort targetR, ushort targetG, ushort targetB,
        double tolerancePercent = 10.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        double fuzz = tolerancePercent / 100.0 * Quantum.MaxValue;
        double fuzzSq = fuzz * fuzz;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var sRow = source.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double dr = sRow[off] - targetR;
                double dg = channels > 1 ? sRow[off + 1] - targetG : 0;
                double db = channels > 2 ? sRow[off + 2] - targetB : 0;
                dRow[x] = (dr * dr + dg * dg + db * db <= fuzzSq) ? Quantum.MaxValue : (ushort)0;
            }
        });

        return result;
    }

    // ─── Feathering ─────────────────────────────────────────────────

    /// <summary>
    /// Feathers (softens) the edges of a selection mask by applying a Gaussian blur.
    /// The mask should be grayscale. Radius controls the feather width in pixels.
    /// </summary>
    public static ImageFrame FeatherMask(ImageFrame mask, double radius)
    {
        if (radius < 0.5) return CloneMask(mask);

        int width = (int)mask.Columns;
        int height = (int)mask.Rows;

        double sigma = radius / 3.0;
        if (sigma < 0.5) sigma = 0.5;

        float[] kernel = BuildGaussianKernel1D(sigma);
        int kernelRadius = kernel.Length / 2;

        // Horizontal pass
        var tempBuffer = ArrayPool<float>.Shared.Rent(width * height);
        try
        {
            Parallel.For(0, height, y =>
            {
                var srcRow = mask.GetPixelRow(y);
                int maskCh = mask.NumberOfChannels;
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    for (int k = -kernelRadius; k <= kernelRadius; k++)
                    {
                        int sx = Math.Clamp(x + k, 0, width - 1);
                        sum += srcRow[sx * maskCh] * kernel[k + kernelRadius];
                    }
                    tempBuffer[y * width + x] = sum;
                }
            });

            // Vertical pass
            var result = new ImageFrame();
            result.Initialize(width, height, ColorspaceType.Gray, false);

            Parallel.For(0, height, y =>
            {
                var dstRow = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    for (int k = -kernelRadius; k <= kernelRadius; k++)
                    {
                        int sy = Math.Clamp(y + k, 0, height - 1);
                        sum += tempBuffer[sy * width + x] * kernel[k + kernelRadius];
                    }
                    dstRow[x] = (ushort)Math.Clamp(sum, 0, Quantum.MaxValue);
                }
            });

            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tempBuffer);
        }
    }

    // ─── Mask Operations ────────────────────────────────────────────

    /// <summary>
    /// Inverts a mask: white becomes black and vice versa.
    /// </summary>
    public static ImageFrame InvertMask(ImageFrame mask)
    {
        int width = (int)mask.Columns;
        int height = (int)mask.Rows;
        int channels = mask.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, mask.Colorspace, mask.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var sRow = mask.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                    dRow[x * channels + c] = (ushort)(Quantum.MaxValue - sRow[x * channels + c]);
            }
        });

        return result;
    }

    /// <summary>
    /// Combines two masks using the union (OR) operation: max of each pixel.
    /// </summary>
    public static ImageFrame UnionMasks(ImageFrame maskA, ImageFrame maskB)
    {
        int width = Math.Min((int)maskA.Columns, (int)maskB.Columns);
        int height = Math.Min((int)maskA.Rows, (int)maskB.Rows);
        int chA = maskA.NumberOfChannels;
        int chB = maskB.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var aRow = maskA.GetPixelRow(y);
            var bRow = maskB.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
                dRow[x] = Math.Max(aRow[x * chA], bRow[x * chB]);
        });

        return result;
    }

    /// <summary>
    /// Combines two masks using the intersection (AND) operation: min of each pixel.
    /// </summary>
    public static ImageFrame IntersectMasks(ImageFrame maskA, ImageFrame maskB)
    {
        int width = Math.Min((int)maskA.Columns, (int)maskB.Columns);
        int height = Math.Min((int)maskA.Rows, (int)maskB.Rows);
        int chA = maskA.NumberOfChannels;
        int chB = maskB.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var aRow = maskA.GetPixelRow(y);
            var bRow = maskB.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
                dRow[x] = Math.Min(aRow[x * chA], bRow[x * chB]);
        });

        return result;
    }

    /// <summary>
    /// Subtracts maskB from maskA: result = max(0, A - B).
    /// </summary>
    public static ImageFrame SubtractMasks(ImageFrame maskA, ImageFrame maskB)
    {
        int width = Math.Min((int)maskA.Columns, (int)maskB.Columns);
        int height = Math.Min((int)maskA.Rows, (int)maskB.Rows);
        int chA = maskA.NumberOfChannels;
        int chB = maskB.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var aRow = maskA.GetPixelRow(y);
            var bRow = maskB.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int diff = aRow[x * chA] - bRow[x * chB];
                dRow[x] = (ushort)Math.Max(0, diff);
            }
        });

        return result;
    }

    /// <summary>
    /// Exclusive-or of two masks: selects pixels that are in one mask but not both.
    /// Uses soft XOR formula: A + B - 2*A*B (normalized).
    /// </summary>
    public static ImageFrame XorMasks(ImageFrame maskA, ImageFrame maskB)
    {
        int width = Math.Min((int)maskA.Columns, (int)maskB.Columns);
        int height = Math.Min((int)maskA.Rows, (int)maskB.Rows);
        int chA = maskA.NumberOfChannels;
        int chB = maskB.NumberOfChannels;
        double maxVal = Quantum.MaxValue;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var aRow = maskA.GetPixelRow(y);
            var bRow = maskB.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double a = aRow[x * chA] / maxVal;
                double b = bRow[x * chB] / maxVal;
                double xorVal = a + b - 2.0 * a * b;
                dRow[x] = (ushort)Math.Clamp(xorVal * maxVal, 0, maxVal);
            }
        });

        return result;
    }

    /// <summary>
    /// Expands (dilates) a mask by the given pixel radius using a circular structuring element.
    /// </summary>
    public static ImageFrame ExpandMask(ImageFrame mask, int radius)
    {
        int width = (int)mask.Columns;
        int height = (int)mask.Rows;
        int maskCh = mask.NumberOfChannels;
        int radiusSq = radius * radius;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort maxVal = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    var nRow = mask.GetPixelRow(ny);
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy > radiusSq) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        ushort v = nRow[nx * maskCh];
                        if (v > maxVal) maxVal = v;
                    }
                }
                dRow[x] = maxVal;
            }
        });

        return result;
    }

    /// <summary>
    /// Contracts (erodes) a mask by the given pixel radius using a circular structuring element.
    /// </summary>
    public static ImageFrame ContractMask(ImageFrame mask, int radius)
    {
        int width = (int)mask.Columns;
        int height = (int)mask.Rows;
        int maskCh = mask.NumberOfChannels;
        int radiusSq = radius * radius;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.Gray, false);

        Parallel.For(0, height, y =>
        {
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort minVal = Quantum.MaxValue;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    var nRow = mask.GetPixelRow(ny);
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy > radiusSq) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        ushort v = nRow[nx * maskCh];
                        if (v < minVal) minVal = v;
                    }
                }
                dRow[x] = minVal;
            }
        });

        return result;
    }

    /// <summary>
    /// Applies a mask to an image, setting the alpha channel to the mask's intensity.
    /// Returns a copy of the source with alpha from the mask.
    /// </summary>
    public static ImageFrame ApplyMask(ImageFrame source, ImageFrame mask)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int srcCh = source.NumberOfChannels;
        int maskCh = mask.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, true);
        int dstCh = result.NumberOfChannels;

        Parallel.For(0, height, y =>
        {
            var sRow = source.GetPixelRow(y);
            var mRow = mask.GetPixelRow(y);
            var dRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int srcOff = x * srcCh;
                int dstOff = x * dstCh;

                // Copy color channels
                int colorChannels = Math.Min(srcCh, dstCh - 1);
                for (int c = 0; c < colorChannels; c++)
                    dRow[dstOff + c] = sRow[srcOff + c];

                // Set alpha from mask
                int mx = Math.Min(x, (int)mask.Columns - 1);
                int my = Math.Min(y, (int)mask.Rows - 1);
                var maskRow = mask.GetPixelRow(my);
                dRow[dstOff + dstCh - 1] = maskRow[mx * maskCh];
            }
        });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame CloneMask(ImageFrame mask)
    {
        int width = (int)mask.Columns;
        int height = (int)mask.Rows;
        var result = new ImageFrame();
        result.Initialize(width, height, mask.Colorspace, mask.HasAlpha);
        for (int y = 0; y < height; y++)
            mask.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));
        return result;
    }

    private static float[] BuildGaussianKernel1D(double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        if (radius < 1) radius = 1;
        int size = 2 * radius + 1;
        var kernel = new float[size];
        double sum = 0;
        double twoSigmaSq = 2.0 * sigma * sigma;

        for (int i = 0; i < size; i++)
        {
            int d = i - radius;
            double val = Math.Exp(-(d * d) / twoSigmaSq);
            kernel[i] = (float)val;
            sum += val;
        }

        for (int i = 0; i < size; i++)
            kernel[i] /= (float)sum;

        return kernel;
    }

    // ─── GrabCut GMM internals ──────────────────────────────────────

    private sealed class GmmComponent
    {
        public double[] Mean = new double[3];
        public double[] Variance = new double[3]; // diagonal covariance
        public double Weight;
        public int SampleCount;
    }

    private sealed class GmmModel
    {
        public GmmComponent[] Components;

        public GmmModel(int k)
        {
            Components = new GmmComponent[k];
            for (int i = 0; i < k; i++)
                Components[i] = new GmmComponent();
        }

        public double LogLikelihood(double r, double g, double b)
        {
            double maxLog = double.NegativeInfinity;
            foreach (var comp in Components)
            {
                if (comp.Weight < 1e-10) continue;
                double dr = r - comp.Mean[0];
                double dg = g - comp.Mean[1];
                double db = b - comp.Mean[2];
                double varR = Math.Max(comp.Variance[0], 1e-6);
                double varG = Math.Max(comp.Variance[1], 1e-6);
                double varB = Math.Max(comp.Variance[2], 1e-6);

                double logDet = Math.Log(varR) + Math.Log(varG) + Math.Log(varB);
                double mahal = dr * dr / varR + dg * dg / varG + db * db / varB;
                double logProb = Math.Log(comp.Weight) - 0.5 * (3.0 * Math.Log(2 * Math.PI) + logDet + mahal);
                if (logProb > maxLog) maxLog = logProb;
            }
            return maxLog;
        }

        public void LearnParameters(double[] pixels, GrabCutLabel[] labels, int width, int height, bool isForeground)
        {
            // Accumulate per-component statistics
            foreach (var comp in Components)
            {
                comp.Mean[0] = comp.Mean[1] = comp.Mean[2] = 0;
                comp.Variance[0] = comp.Variance[1] = comp.Variance[2] = 0;
                comp.SampleCount = 0;
                comp.Weight = 0;
            }

            int totalPixels = width * height;
            int totalSamples = 0;

            for (int i = 0; i < totalPixels; i++)
            {
                var label = labels[i];
                bool isFg = label == GrabCutLabel.Foreground || label == GrabCutLabel.ProbableForeground;
                if (isFg != isForeground) continue;

                // Assign to nearest component
                int bestComp = 0;
                double bestDist = double.MaxValue;
                for (int c = 0; c < Components.Length; c++)
                {
                    double dr = pixels[i * 3] - Components[c].Mean[0];
                    double dg = pixels[i * 3 + 1] - Components[c].Mean[1];
                    double db = pixels[i * 3 + 2] - Components[c].Mean[2];
                    double dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist) { bestDist = dist; bestComp = c; }
                }

                Components[bestComp].Mean[0] += pixels[i * 3];
                Components[bestComp].Mean[1] += pixels[i * 3 + 1];
                Components[bestComp].Mean[2] += pixels[i * 3 + 2];
                Components[bestComp].SampleCount++;
                totalSamples++;
            }

            // Finalize means
            foreach (var comp in Components)
            {
                if (comp.SampleCount > 0)
                {
                    comp.Mean[0] /= comp.SampleCount;
                    comp.Mean[1] /= comp.SampleCount;
                    comp.Mean[2] /= comp.SampleCount;
                }
            }

            // Compute variance
            for (int i = 0; i < totalPixels; i++)
            {
                var label = labels[i];
                bool isFg = label == GrabCutLabel.Foreground || label == GrabCutLabel.ProbableForeground;
                if (isFg != isForeground) continue;

                int bestComp = 0;
                double bestDist = double.MaxValue;
                for (int c = 0; c < Components.Length; c++)
                {
                    double dr = pixels[i * 3] - Components[c].Mean[0];
                    double dg = pixels[i * 3 + 1] - Components[c].Mean[1];
                    double db = pixels[i * 3 + 2] - Components[c].Mean[2];
                    double dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist) { bestDist = dist; bestComp = c; }
                }

                double dr2 = pixels[i * 3] - Components[bestComp].Mean[0];
                double dg2 = pixels[i * 3 + 1] - Components[bestComp].Mean[1];
                double db2 = pixels[i * 3 + 2] - Components[bestComp].Mean[2];
                Components[bestComp].Variance[0] += dr2 * dr2;
                Components[bestComp].Variance[1] += dg2 * dg2;
                Components[bestComp].Variance[2] += db2 * db2;
            }

            foreach (var comp in Components)
            {
                if (comp.SampleCount > 1)
                {
                    comp.Variance[0] /= comp.SampleCount;
                    comp.Variance[1] /= comp.SampleCount;
                    comp.Variance[2] /= comp.SampleCount;
                }
                else
                {
                    comp.Variance[0] = comp.Variance[1] = comp.Variance[2] = 1e4;
                }
                comp.Weight = totalSamples > 0 ? (double)comp.SampleCount / totalSamples : 1.0 / Components.Length;
            }
        }
    }

    private static void InitializeGmms(double[] pixels, GrabCutLabel[] labels,
        GmmModel fgGmm, GmmModel bgGmm, int width, int height)
    {
        // Collect FG and BG pixels, initialize GMM means via simple k-means-style spread
        var fgPixels = new List<int>();
        var bgPixels = new List<int>();

        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] == GrabCutLabel.Foreground || labels[i] == GrabCutLabel.ProbableForeground)
                fgPixels.Add(i);
            else
                bgPixels.Add(i);
        }

        InitializeGmmFromPixels(pixels, fgPixels, fgGmm);
        InitializeGmmFromPixels(pixels, bgPixels, bgGmm);
    }

    private static void InitializeGmmFromPixels(double[] pixels, List<int> indices, GmmModel gmm)
    {
        if (indices.Count == 0)
        {
            for (int c = 0; c < gmm.Components.Length; c++)
            {
                gmm.Components[c].Mean = [c * Quantum.MaxValue / (double)gmm.Components.Length,
                                           c * Quantum.MaxValue / (double)gmm.Components.Length,
                                           c * Quantum.MaxValue / (double)gmm.Components.Length];
                gmm.Components[c].Variance = [1e4, 1e4, 1e4];
                gmm.Components[c].Weight = 1.0 / gmm.Components.Length;
            }
            return;
        }

        // Spread components evenly across sorted luminance
        var sorted = new List<int>(indices);
        sorted.Sort((a, b) =>
        {
            double lumA = pixels[a * 3] * 0.3 + pixels[a * 3 + 1] * 0.59 + pixels[a * 3 + 2] * 0.11;
            double lumB = pixels[b * 3] * 0.3 + pixels[b * 3 + 1] * 0.59 + pixels[b * 3 + 2] * 0.11;
            return lumA.CompareTo(lumB);
        });

        int k = gmm.Components.Length;
        for (int c = 0; c < k; c++)
        {
            int idx = sorted[(int)((long)c * sorted.Count / k)];
            gmm.Components[c].Mean = [pixels[idx * 3], pixels[idx * 3 + 1], pixels[idx * 3 + 2]];
            gmm.Components[c].Variance = [1e4, 1e4, 1e4];
            gmm.Components[c].Weight = 1.0 / k;
        }
    }

    private static void AssignGmmComponents(double[] pixels, GrabCutLabel[] labels,
        GmmModel fgGmm, GmmModel bgGmm, int width, int height)
    {
        // No-op for simplified version; component assignment is done in LearnParameters
    }

    private static double ComputeBeta(double[] pixels, int width, int height)
    {
        // beta = 1 / (2 * mean(||Ip - Iq||^2)) over all neighboring pixel pairs
        double totalDiffSq = 0;
        long pairCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                // Right neighbor
                if (x + 1 < width)
                {
                    int j = (y * width + x + 1) * 3;
                    double dr = pixels[i] - pixels[j];
                    double dg = pixels[i + 1] - pixels[j + 1];
                    double db = pixels[i + 2] - pixels[j + 2];
                    totalDiffSq += dr * dr + dg * dg + db * db;
                    pairCount++;
                }
                // Bottom neighbor
                if (y + 1 < height)
                {
                    int j = ((y + 1) * width + x) * 3;
                    double dr = pixels[i] - pixels[j];
                    double dg = pixels[i + 1] - pixels[j + 1];
                    double db = pixels[i + 2] - pixels[j + 2];
                    totalDiffSq += dr * dr + dg * dg + db * db;
                    pairCount++;
                }
            }
        }

        double meanDiffSq = pairCount > 0 ? totalDiffSq / pairCount : 1.0;
        return meanDiffSq > 1e-10 ? 1.0 / (2.0 * meanDiffSq) : 0.0;
    }

    private static void RefineLabelsICM(double[] pixels, GrabCutLabel[] labels,
        GmmModel fgGmm, GmmModel bgGmm, double beta, int width, int height)
    {
        // Iterated Conditional Modes: for each probable pixel, choose label that minimizes energy
        double smoothnessLambda = 50.0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (labels[idx] == GrabCutLabel.Foreground || labels[idx] == GrabCutLabel.Background)
                    continue; // hard constraints

                double r = pixels[idx * 3], g = pixels[idx * 3 + 1], b = pixels[idx * 3 + 2];
                double fgEnergy = -fgGmm.LogLikelihood(r, g, b);
                double bgEnergy = -bgGmm.LogLikelihood(r, g, b);

                // Add smoothness term from neighbors
                ReadOnlySpan<(int dx, int dy)> neighbors = [(0, -1), (0, 1), (-1, 0), (1, 0)];
                foreach (var (dx, dy) in neighbors)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;

                    int ni = (ny * width + nx) * 3;
                    double dr = r - pixels[ni], dg = g - pixels[ni + 1], db = b - pixels[ni + 2];
                    double nWeight = smoothnessLambda * Math.Exp(-beta * (dr * dr + dg * dg + db * db));

                    var nLabel = labels[ny * width + nx];
                    bool nIsFg = nLabel == GrabCutLabel.Foreground || nLabel == GrabCutLabel.ProbableForeground;

                    // Penalty for disagreeing with neighbor
                    if (nIsFg) bgEnergy += nWeight;
                    else fgEnergy += nWeight;
                }

                labels[idx] = fgEnergy <= bgEnergy ? GrabCutLabel.ProbableForeground : GrabCutLabel.ProbableBackground;
            }
        }
    }
}
