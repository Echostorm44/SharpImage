// SharpImage — Creative Filters II.
// LensBlur (bokeh), TiltShift, Glow/Bloom, Pixelate, Crystallize, Pointillize, Halftone.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Bokeh kernel shape for lens blur simulation.
/// </summary>
public enum BokehShape
{
    Disk,
    Hexagon
}

/// <summary>
/// Creative filter effects: lens blur (bokeh), tilt-shift, glow/bloom,
/// pixelate, crystallize, pointillize, and halftone.
/// </summary>
public static class CreativeFilters
{
    // ─── Lens Blur (Bokeh) ──────────────────────────────────────

    /// <summary>
    /// Simulates camera lens blur (bokeh) using a shaped kernel.
    /// Optionally uses a depth map to vary blur intensity per pixel.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Maximum blur radius in pixels.</param>
    /// <param name="shape">Bokeh kernel shape (Disk or Hexagon).</param>
    /// <param name="depthMap">Optional grayscale depth map — white=far(blurry), black=near(sharp).</param>
    public static ImageFrame LensBlur(ImageFrame source, int radius = 5, BokehShape shape = BokehShape.Disk, ImageFrame? depthMap = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1, nameof(radius));

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        // Build kernel offsets and weights for the chosen shape
        var (offsets, weights) = BuildBokehKernel(radius, shape);
        double totalWeight = 0;
        for (int i = 0; i < weights.Length; i++) totalWeight += weights[i];
        double invTotal = 1.0 / totalWeight;

        // Pre-cache kernels for all possible effective radii to avoid per-pixel allocation
        var kernelCache = new ((int dx, int dy)[] offsets, double[] weights, double invTotal)[radius + 1];
        kernelCache[radius] = (offsets, weights, invTotal);
        for (int r = 1; r < radius; r++)
        {
            var (sOff, sWt) = BuildBokehKernel(r, shape);
            double sTotal = 0;
            for (int i = 0; i < sWt.Length; i++) sTotal += sWt[i];
            kernelCache[r] = (sOff, sWt, 1.0 / sTotal);
        }

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                // Determine effective radius from depth map
                double depthFactor = 1.0;
                if (depthMap != null)
                {
                    int dx = Math.Clamp(x, 0, (int)depthMap.Columns - 1);
                    int dy = Math.Clamp(y, 0, (int)depthMap.Rows - 1);
                    var dRow = depthMap.GetPixelRow(dy);
                    depthFactor = dRow[dx * depthMap.NumberOfChannels] * Quantum.Scale;
                }

                int effectiveRadius = Math.Max(1, (int)(radius * depthFactor));

                if (effectiveRadius == radius)
                {
                    // Full kernel — use precomputed offsets
                    ApplyKernelAtPixel(source, result, x, y, w, h, ch, hasAlpha, offsets, weights, invTotal);
                }
                else
                {
                    // Reduced radius — use cached kernel
                    var cached = kernelCache[effectiveRadius];
                    ApplyKernelAtPixel(source, result, x, y, w, h, ch, hasAlpha, cached.offsets, cached.weights, cached.invTotal);
                }
            }
        });

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyKernelAtPixel(ImageFrame source, ImageFrame result, int x, int y,
        int w, int h, int ch, bool hasAlpha, (int dx, int dy)[] offsets, double[] weights, double invTotal)
    {
        double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        double wSum = 0;

        for (int i = 0; i < offsets.Length; i++)
        {
            int sx = x + offsets[i].dx;
            int sy = y + offsets[i].dy;
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;

            var row = source.GetPixelRow(sy);
            int off = sx * ch;
            double wt = weights[i];

            sumR += row[off] * wt;
            sumG += row[off + 1] * wt;
            sumB += row[off + 2] * wt;
            if (hasAlpha) sumA += row[off + 3] * wt;
            wSum += wt;
        }

        if (wSum > 0)
        {
            double inv = 1.0 / wSum;
            var outRow = result.GetPixelRowForWrite(y);
            int outOff = x * ch;
            outRow[outOff] = Quantum.Clamp(sumR * inv);
            outRow[outOff + 1] = Quantum.Clamp(sumG * inv);
            outRow[outOff + 2] = Quantum.Clamp(sumB * inv);
            if (hasAlpha) outRow[outOff + 3] = Quantum.Clamp(sumA * inv);
        }
    }

    private static ((int dx, int dy)[] offsets, double[] weights) BuildBokehKernel(int radius, BokehShape shape)
    {
        var offsetList = new List<(int dx, int dy)>();
        var weightList = new List<double>();
        int r2 = radius * radius;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                bool inside = shape switch
                {
                    BokehShape.Hexagon => IsInsideHexagon(dx, dy, radius),
                    _ => dx * dx + dy * dy <= r2 // Disk
                };

                if (inside)
                {
                    offsetList.Add((dx, dy));
                    weightList.Add(1.0); // Uniform weight for bokeh
                }
            }
        }

        return (offsetList.ToArray(), weightList.ToArray());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInsideHexagon(int dx, int dy, int radius)
    {
        // Regular hexagon check: |x| <= r, |y| <= r*sqrt(3)/2, and |x|+|y|/sqrt(3) <= r
        double ax = Math.Abs(dx);
        double ay = Math.Abs(dy);
        double s3 = 0.8660254037844386; // sqrt(3)/2
        return ax <= radius && ay <= radius * s3 && ax + ay / 1.7320508075688772 <= radius;
    }

    // ─── Tilt-Shift ─────────────────────────────────────────────

    /// <summary>
    /// Simulates tilt-shift photography by blurring areas above and below a horizontal
    /// focus band, creating a miniature/toy-like appearance.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="focusY">Center Y position of the focus band (0.0–1.0).</param>
    /// <param name="bandHeight">Height of the sharp focus band (0.0–1.0).</param>
    /// <param name="blurRadius">Maximum blur radius for out-of-focus regions.</param>
    public static ImageFrame TiltShift(ImageFrame source, double focusY = 0.5, double bandHeight = 0.2, int blurRadius = 8)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;

        // Create depth-like mask: black in focus band, white outside
        var depthMap = new ImageFrame();
        depthMap.Initialize(source.Columns, source.Rows, ColorspaceType.Gray, false);

        double centerPx = focusY * h;
        double halfBand = bandHeight * h * 0.5;
        double fadeZone = h * 0.15; // Smooth transition zone

        for (int y = 0; y < h; y++)
        {
            var row = depthMap.GetPixelRowForWrite(y);
            double dist = Math.Abs(y - centerPx) - halfBand;
            double factor = dist <= 0 ? 0.0 : Math.Min(1.0, dist / fadeZone);
            ushort val = Quantum.Clamp(factor * Quantum.MaxValue);
            for (int x = 0; x < w; x++)
                row[x] = val;
        }

        var result = LensBlur(source, blurRadius, BokehShape.Disk, depthMap);
        depthMap.Dispose();
        return result;
    }

    // ─── Glow / Bloom ───────────────────────────────────────────

    /// <summary>
    /// Creates a soft glow/bloom effect by extracting bright areas, blurring them,
    /// and compositing back over the original using Screen blend mode.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="threshold">Brightness threshold (0.0–1.0) for glow extraction.</param>
    /// <param name="blurRadius">Blur radius for the glow layer.</param>
    /// <param name="intensity">Glow intensity/strength (0.0–1.0).</param>
    public static ImageFrame Glow(ImageFrame source, double threshold = 0.5, int blurRadius = 10, double intensity = 0.6)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        ushort threshVal = Quantum.Clamp(threshold * Quantum.MaxValue);

        // Extract bright pixels
        var bright = new ImageFrame();
        bright.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        for (int y = 0; y < h; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = bright.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                int lum = (srcRow[off] * 77 + srcRow[off + 1] * 150 + srcRow[off + 2] * 29) >> 8;
                if (lum >= threshVal)
                {
                    dstRow[off] = srcRow[off];
                    dstRow[off + 1] = srcRow[off + 1];
                    dstRow[off + 2] = srcRow[off + 2];
                    if (hasAlpha) dstRow[off + 3] = srcRow[off + 3];
                }
                else if (hasAlpha)
                {
                    dstRow[off + 3] = 0;
                }
            }
        }

        // Blur the bright layer
        var blurred = SharpImage.Effects.ConvolutionFilters.GaussianBlur(bright, blurRadius);
        bright.Dispose();

        // Screen composite: result = 1 - (1-base)*(1-blend)
        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        for (int y = 0; y < h; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var glowRow = blurred.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                for (int c = 0; c < (hasAlpha ? ch - 1 : ch); c++)
                {
                    double bBase = srcRow[off + c] * Quantum.Scale;
                    double bGlow = glowRow[off + c] * Quantum.Scale * intensity;
                    double screen = 1.0 - (1.0 - bBase) * (1.0 - bGlow);
                    dstRow[off + c] = Quantum.Clamp(screen * Quantum.MaxValue);
                }
                if (hasAlpha) dstRow[off + ch - 1] = srcRow[off + ch - 1];
            }
        }

        blurred.Dispose();
        return result;
    }

    // ─── Pixelate / Mosaic ──────────────────────────────────────

    /// <summary>
    /// Pixelates the image by averaging each block of pixels into a single solid color.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="blockSize">Size of each pixel block.</param>
    public static ImageFrame Pixelate(ImageFrame source, int blockSize = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 2, nameof(blockSize));

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        for (int by = 0; by < h; by += blockSize)
        {
            for (int bx = 0; bx < w; bx += blockSize)
            {
                int bw = Math.Min(blockSize, w - bx);
                int bh = Math.Min(blockSize, h - by);
                int count = bw * bh;

                // Average the block
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (int dy = 0; dy < bh; dy++)
                {
                    var row = source.GetPixelRow(by + dy);
                    for (int dx = 0; dx < bw; dx++)
                    {
                        int off = (bx + dx) * ch;
                        sumR += row[off]; sumG += row[off + 1]; sumB += row[off + 2];
                        if (hasAlpha) sumA += row[off + 3];
                    }
                }

                ushort avgR = (ushort)(sumR / count);
                ushort avgG = (ushort)(sumG / count);
                ushort avgB = (ushort)(sumB / count);
                ushort avgA = hasAlpha ? (ushort)(sumA / count) : Quantum.MaxValue;

                // Fill the block
                for (int dy = 0; dy < bh; dy++)
                {
                    var dstRow = result.GetPixelRowForWrite(by + dy);
                    for (int dx = 0; dx < bw; dx++)
                    {
                        int off = (bx + dx) * ch;
                        dstRow[off] = avgR; dstRow[off + 1] = avgG; dstRow[off + 2] = avgB;
                        if (hasAlpha) dstRow[off + 3] = avgA;
                    }
                }
            }
        }

        return result;
    }

    // ─── Crystallize ────────────────────────────────────────────

    /// <summary>
    /// Creates a Voronoi-cell pixelation effect. Random seed points divide
    /// the image into irregular polygonal cells, each filled with the average color
    /// of the pixels it contains.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="cellSize">Average distance between cell centers.</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    public static ImageFrame Crystallize(ImageFrame source, int cellSize = 16, int seed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 2, nameof(cellSize));

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        // Generate Voronoi seed points on a jittered grid
        var rng = new Random(seed);
        int gridCols = Math.Max(1, w / cellSize);
        int gridRows = Math.Max(1, h / cellSize);
        int numCells = gridCols * gridRows;

        var centers = new (int x, int y)[numCells];
        for (int gy = 0; gy < gridRows; gy++)
        {
            for (int gx = 0; gx < gridCols; gx++)
            {
                int cx = gx * cellSize + rng.Next(cellSize);
                int cy = gy * cellSize + rng.Next(cellSize);
                centers[gy * gridCols + gx] = (Math.Clamp(cx, 0, w - 1), Math.Clamp(cy, 0, h - 1));
            }
        }

        // Assign each pixel to nearest center (using grid acceleration)
        var cellAssignment = new int[h * w];
        var cellSumR = new long[numCells];
        var cellSumG = new long[numCells];
        var cellSumB = new long[numCells];
        var cellSumA = new long[numCells];
        var cellCount = new int[numCells];

        for (int y = 0; y < h; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                // Search nearby grid cells for closest center
                int gx = Math.Clamp(x / cellSize, 0, gridCols - 1);
                int gy = Math.Clamp(y / cellSize, 0, gridRows - 1);

                int bestDist = int.MaxValue;
                int bestCell = 0;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = gy + dy;
                    if (ny < 0 || ny >= gridRows) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = gx + dx;
                        if (nx < 0 || nx >= gridCols) continue;
                        int ci = ny * gridCols + nx;
                        int ddx = x - centers[ci].x;
                        int ddy = y - centers[ci].y;
                        int dist = ddx * ddx + ddy * ddy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestCell = ci;
                        }
                    }
                }

                cellAssignment[y * w + x] = bestCell;
                int off = x * ch;
                cellSumR[bestCell] += row[off];
                cellSumG[bestCell] += row[off + 1];
                cellSumB[bestCell] += row[off + 2];
                if (hasAlpha) cellSumA[bestCell] += row[off + 3];
                cellCount[bestCell]++;
            }
        }

        // Compute cell average colors
        var cellColors = new (ushort r, ushort g, ushort b, ushort a)[numCells];
        for (int i = 0; i < numCells; i++)
        {
            if (cellCount[i] > 0)
            {
                cellColors[i] = (
                    (ushort)(cellSumR[i] / cellCount[i]),
                    (ushort)(cellSumG[i] / cellCount[i]),
                    (ushort)(cellSumB[i] / cellCount[i]),
                    hasAlpha ? (ushort)(cellSumA[i] / cellCount[i]) : Quantum.MaxValue
                );
            }
        }

        // Fill result
        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        Parallel.For(0, h, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int ci = cellAssignment[y * w + x];
                int off = x * ch;
                dstRow[off] = cellColors[ci].r;
                dstRow[off + 1] = cellColors[ci].g;
                dstRow[off + 2] = cellColors[ci].b;
                if (hasAlpha) dstRow[off + 3] = cellColors[ci].a;
            }
        });

        return result;
    }

    // ─── Pointillize ────────────────────────────────────────────

    /// <summary>
    /// Creates a Seurat-style pointillist painting effect by rendering the image
    /// as colored circles on a background.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="dotRadius">Radius of each dot.</param>
    /// <param name="spacing">Distance between dot centers.</param>
    /// <param name="backgroundColor">Background color (default: white).</param>
    public static ImageFrame Pointillize(ImageFrame source, int dotRadius = 4, int spacing = 0, ushort backgroundColor = 65535)
    {
        if (spacing <= 0) spacing = dotRadius * 2;
        ArgumentOutOfRangeException.ThrowIfLessThan(dotRadius, 1, nameof(dotRadius));

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        // Fill with background
        for (int y = 0; y < h; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                row[off] = backgroundColor;
                row[off + 1] = backgroundColor;
                row[off + 2] = backgroundColor;
                if (hasAlpha) row[off + 3] = Quantum.MaxValue;
            }
        }

        int r2 = dotRadius * dotRadius;

        // Place dots on a grid
        for (int cy = dotRadius; cy < h; cy += spacing)
        {
            for (int cx = dotRadius; cx < w; cx += spacing)
            {
                // Sample color at center
                var srcRow = source.GetPixelRow(Math.Clamp(cy, 0, h - 1));
                int sOff = Math.Clamp(cx, 0, w - 1) * ch;
                ushort dotR = srcRow[sOff], dotG = srcRow[sOff + 1], dotB = srcRow[sOff + 2];

                // Draw filled circle
                for (int dy = -dotRadius; dy <= dotRadius; dy++)
                {
                    int py = cy + dy;
                    if (py < 0 || py >= h) continue;
                    var dstRow = result.GetPixelRowForWrite(py);
                    for (int dx = -dotRadius; dx <= dotRadius; dx++)
                    {
                        int px = cx + dx;
                        if (px < 0 || px >= w) continue;
                        if (dx * dx + dy * dy > r2) continue;

                        int dOff = px * ch;
                        dstRow[dOff] = dotR;
                        dstRow[dOff + 1] = dotG;
                        dstRow[dOff + 2] = dotB;
                    }
                }
            }
        }

        return result;
    }

    // ─── Halftone ───────────────────────────────────────────────

    /// <summary>
    /// Simulates CMYK halftone print screening with angled dot grids.
    /// Each channel uses a different screen angle to avoid moiré patterns.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="dotSize">Maximum halftone dot size in pixels.</param>
    /// <param name="cmykAngles">Screen angles for C, M, Y, K channels (degrees). Default: 15, 75, 0, 45.</param>
    public static ImageFrame Halftone(ImageFrame source, int dotSize = 6, double[]? cmykAngles = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dotSize, 2, nameof(dotSize));
        cmykAngles ??= [15.0, 75.0, 0.0, 45.0];

        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        // Convert to CMYK-like representation
        var cChannel = new double[h * w];
        var mChannel = new double[h * w];
        var yChannel = new double[h * w];
        var kChannel = new double[h * w];

        for (int y = 0; y < h; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                double r = row[off] * Quantum.Scale;
                double g = row[off + 1] * Quantum.Scale;
                double b = row[off + 2] * Quantum.Scale;

                double k = 1.0 - Math.Max(r, Math.Max(g, b));
                double invK = k < 1.0 ? 1.0 / (1.0 - k) : 0;
                cChannel[y * w + x] = (1.0 - r - k) * invK;
                mChannel[y * w + x] = (1.0 - g - k) * invK;
                yChannel[y * w + x] = (1.0 - b - k) * invK;
                kChannel[y * w + x] = k;
            }
        }

        // Render halftone for each channel
        var cHalf = RenderHalftoneChannel(cChannel, w, h, dotSize, cmykAngles[0]);
        var mHalf = RenderHalftoneChannel(mChannel, w, h, dotSize, cmykAngles[1]);
        var yHalf = RenderHalftoneChannel(yChannel, w, h, dotSize, cmykAngles[2]);
        var kHalf = RenderHalftoneChannel(kChannel, w, h, dotSize, cmykAngles[3]);

        // Convert back to RGB
        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        Parallel.For(0, h, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                double c = cHalf[idx], m2 = mHalf[idx], y2 = yHalf[idx], k2 = kHalf[idx];

                double r = (1.0 - c) * (1.0 - k2);
                double g = (1.0 - m2) * (1.0 - k2);
                double b = (1.0 - y2) * (1.0 - k2);

                int dOff = x * ch;
                dstRow[dOff] = Quantum.Clamp(r * Quantum.MaxValue);
                dstRow[dOff + 1] = Quantum.Clamp(g * Quantum.MaxValue);
                dstRow[dOff + 2] = Quantum.Clamp(b * Quantum.MaxValue);
                if (hasAlpha) dstRow[dOff + ch - 1] = srcRow[x * ch + ch - 1];
            }
        });

        return result;
    }

    private static double[] RenderHalftoneChannel(double[] channel, int w, int h, int dotSize, double angleDeg)
    {
        var result = new double[h * w];
        double angleRad = angleDeg * Math.PI / 180.0;
        double cosA = Math.Cos(angleRad);
        double sinA = Math.Sin(angleRad);
        double cellSize = dotSize;
        double halfCell = cellSize * 0.5;
        double maxR2 = halfCell * halfCell;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Rotate coordinates to screen angle
                double rx = x * cosA + y * sinA;
                double ry = -x * sinA + y * cosA;

                // Find cell center
                double cellX = Math.Floor(rx / cellSize + 0.5) * cellSize;
                double cellY = Math.Floor(ry / cellSize + 0.5) * cellSize;

                // Distance from cell center
                double dx = rx - cellX;
                double dy = ry - cellY;
                double dist2 = dx * dx + dy * dy;

                // Sample intensity at cell center (inverse-rotate back)
                double icx = cellX * cosA - cellY * sinA;
                double icy = cellX * sinA + cellY * cosA;
                int sx = Math.Clamp((int)icx, 0, w - 1);
                int sy = Math.Clamp((int)icy, 0, h - 1);
                double intensity = channel[sy * w + sx];

                // Dot radius proportional to intensity
                double dotR2 = intensity * maxR2;
                result[y * w + x] = dist2 <= dotR2 ? intensity : 0.0;
            }
        }

        return result;
    }
}
