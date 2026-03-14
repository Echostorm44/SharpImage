// SharpImage — Artistic visual effects.
// OilPaint, Charcoal, Sketch, Vignette, Wave, Swirl, Implode.

using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Artistic visual effects: oil painting, charcoal sketch, pencil sketch,
/// vignette, wave distortion, swirl distortion, and implode/explode.
/// All operations return new ImageFrame instances and preserve alpha channels.
/// </summary>
public static class ArtisticOps
{
    // ─── Oil Paint ──────────────────────────────────────────────

    /// <summary>
    /// Simulates an oil painting by replacing each pixel with the most frequent
    /// color (mode) in its neighborhood, quantized to the given number of intensity levels.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Neighborhood radius in pixels.</param>
    /// <param name="levels">Number of intensity quantization levels (default 256).</param>
    public static ImageFrame OilPaint(ImageFrame source, int radius = 3, int levels = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1, nameof(radius));
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2, nameof(levels));
        if (levels > 256) levels = 256;

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.HasAlpha ? 4 : 3;
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        double levelScale = (levels - 1) * Quantum.Scale;

        Parallel.For(0, height, y =>
        {
            // Per-thread histogram on stack (max 256 × 4 = 1KB)
            Span<int> histogram = stackalloc int[levels];
            var outRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                histogram.Clear();
                int maxCount = 0;
                int bestSrcX = x;
                int bestSrcY = y;

                int yStart = Math.Max(0, y - radius);
                int yEnd = Math.Min(height - 1, y + radius);
                int xStart = Math.Max(0, x - radius);
                int xEnd = Math.Min(width - 1, x + radius);

                for (int ky = yStart; ky <= yEnd; ky++)
                {
                    var srcRow = source.GetPixelRow(ky);
                    for (int kx = xStart; kx <= xEnd; kx++)
                    {
                        int idx = kx * channels;
                        // Compute intensity using fast luminance approximation
                        int intensity = (int)((srcRow[idx] * 0.2126 +
                                               srcRow[idx + 1] * 0.7152 +
                                               srcRow[idx + 2] * 0.0722) * levelScale);
                        if (intensity >= levels) intensity = levels - 1;

                        histogram[intensity]++;
                        if (histogram[intensity] > maxCount)
                        {
                            maxCount = histogram[intensity];
                            bestSrcX = kx;
                            bestSrcY = ky;
                        }
                    }
                }

                // Copy the most frequent color to the output
                var bestRow = source.GetPixelRow(bestSrcY);
                int srcIdx = bestSrcX * channels;
                int dstIdx = x * channels;
                outRow[dstIdx] = bestRow[srcIdx];
                outRow[dstIdx + 1] = bestRow[srcIdx + 1];
                outRow[dstIdx + 2] = bestRow[srcIdx + 2];
                if (channels == 4)
                    outRow[dstIdx + 3] = source.GetPixelRow(y)[x * channels + 3];
            }
        });

        return result;
    }

    // ─── Charcoal ───────────────────────────────────────────────

    /// <summary>
    /// Creates a charcoal sketch effect by applying edge detection, blur,
    /// normalization, negation, and grayscale conversion.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="radius">Blur radius/sigma (controls line thickness).</param>
    /// <param name="sigma">Gaussian sigma for smoothing.</param>
    public static ImageFrame Charcoal(ImageFrame source, double radius = 1.0, double sigma = 0.5)
    {
        // IM pipeline: Edge(radius) → strip alpha → Blur(radius, sigma) → Normalize → Negate → Grayscale
        // Flatten alpha to white before edge detection to avoid artifacts at transparency boundaries
        var input = source.HasAlpha ? FlattenAlphaToWhite(source) : source;
        var edged = ConvolutionFilters.EdgeDetect(input, radius);
        var blurred = ConvolutionFilters.GaussianBlur(edged, sigma > 0 ? sigma : 0.5);
        var normalized = SharpImage.Enhance.EnhanceOps.Normalize(blurred);
        var negated = ColorAdjust.Invert(normalized);
        var gray = ColorAdjust.Grayscale(negated);
        return gray;
    }

    /// <summary>
    /// Composites an image with alpha over a white background, producing an opaque result.
    /// </summary>
    private static ImageFrame FlattenAlphaToWhite(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, hasAlpha: false);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int si = x * 4;
                int di = x * 3;
                double alpha = srcRow[si + 3] * Quantum.Scale;
                double invAlpha = 1.0 - alpha;
                // Blend over white (Quantum.MaxValue)
                dstRow[di] = Quantum.Clamp((int)(srcRow[si] * alpha + Quantum.MaxValue * invAlpha + 0.5));
                dstRow[di + 1] = Quantum.Clamp((int)(srcRow[si + 1] * alpha + Quantum.MaxValue * invAlpha + 0.5));
                dstRow[di + 2] = Quantum.Clamp((int)(srcRow[si + 2] * alpha + Quantum.MaxValue * invAlpha + 0.5));
            }
        });

        return result;
    }

    /// <summary>
    /// Composites an image with alpha over a black background, producing an opaque result.
    /// </summary>
    private static ImageFrame FlattenAlphaToBlack(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, hasAlpha: false);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int si = x * 4;
                int di = x * 3;
                double alpha = srcRow[si + 3] * Quantum.Scale;
                // Blend over black (0) — just multiply by alpha
                dstRow[di] = Quantum.Clamp((int)(srcRow[si] * alpha + 0.5));
                dstRow[di + 1] = Quantum.Clamp((int)(srcRow[si + 1] * alpha + 0.5));
                dstRow[di + 2] = Quantum.Clamp((int)(srcRow[si + 2] * alpha + 0.5));
            }
        });

        return result;
    }

    // ─── Sketch ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a pencil sketch effect matching ImageMagick's SketchImage pipeline:
    /// random noise at 2× size → MotionBlur → Edge(7×7 Laplacian) → Normalize → Negate →
    /// resize back → ColorDodge composite with original → blend 20/80 with original.
    /// Alpha images are flattened to black first (transparent areas become dark background).
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="sigma">Sigma for motion blur (controls stroke width).</param>
    /// <param name="angle">Stroke direction angle in degrees.</param>
    public static ImageFrame Sketch(ImageFrame source, double sigma = 0.5, double angle = 45.0)
    {
        // Flatten alpha to black so transparent areas become dark background (matches IM behavior)
        var input = source.HasAlpha ? FlattenAlphaToBlack(source) : source;

        int width = (int)input.Columns;
        int height = (int)input.Rows;
        const int channels = 3; // always opaque after flatten

        // Step 1: Create random noise image at 2× size (grayscale — same value per channel)
        int dblWidth = width * 2;
        int dblHeight = height * 2;
        var noise = new ImageFrame();
        noise.Initialize(dblWidth, dblHeight, input.Colorspace, false);

        var rng = new Random(42); // deterministic for reproducibility
        for (int y = 0; y < dblHeight; y++)
        {
            var row = noise.GetPixelRowForWrite(y);
            for (int x = 0; x < dblWidth; x++)
            {
                ushort val = (ushort)(rng.NextDouble() * Quantum.MaxValue);
                int idx = x * 3;
                row[idx] = val;
                row[idx + 1] = val;
                row[idx + 2] = val;
            }
        }

        // Step 2: MotionBlur along the angle to create directional streaks
        int motionRadius = Math.Max(3, (int)(sigma * 4));
        var blurred = BlurNoiseOps.MotionBlur(noise, motionRadius, sigma > 0 ? sigma : 0.5, angle);

        // Step 3: Edge detect the streaks — use 7×7 Laplacian (radius=3) matching IM's
        // GetOptimalKernelWidth1D(0, 0.5) at Q16 which yields width=7
        var edged = ConvolutionFilters.EdgeDetect(blurred, 3.0);

        // Step 4: Normalize → Negate
        var normalized = SharpImage.Enhance.EnhanceOps.Normalize(edged);
        var negated = ColorAdjust.Invert(normalized);

        // Step 5: Resize from 2× back to original size
        var dodgeImage = Resize.Apply(negated, width, height);

        // Step 6: ColorDodge composite — original / (1 - dodge)
        // Dark dodge values (sketch lines) preserve original; light areas brighten to white
        var result = new ImageFrame();
        result.Initialize(width, height, input.Colorspace, hasAlpha: false);

        Parallel.For(0, height, y =>
        {
            var srcRow = input.GetPixelRow(y);
            var dodgeRow = dodgeImage.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int idx = x * channels;

                for (int c = 0; c < 3; c++)
                {
                    double baseVal = srcRow[idx + c] * Quantum.Scale;
                    double dodgeVal = dodgeRow[idx + c] * Quantum.Scale;

                    // ColorDodge: base / (1 - overlay), clamped to [0,1]
                    double colorDodge;
                    if (dodgeVal >= 1.0)
                        colorDodge = 1.0;
                    else
                        colorDodge = Math.Min(1.0, baseVal / (1.0 - dodgeVal));

                    // Blend: 20% original + 80% sketch
                    double blended = 0.2 * baseVal + 0.8 * colorDodge;
                    dstRow[idx + c] = Quantum.Clamp((int)(blended * Quantum.MaxValue + 0.5));
                }
            }
        });

        return result;
    }

    // ─── Vignette ───────────────────────────────────────────────

    /// <summary>
    /// Applies a vignette effect matching ImageMagick's VignetteImage pipeline:
    /// creates a white ellipse on a black background, applies GaussianBlur for soft edges,
    /// then uses the blurred mask to darken the source image at its edges.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="sigma">Blur sigma for edge softness (larger = wider, softer transition).</param>
    /// <param name="xOffset">Horizontal inset from edges defining ellipse boundary. Null = 10% of width (IM default).</param>
    /// <param name="yOffset">Vertical inset from edges defining ellipse boundary. Null = 10% of height (IM default).</param>
    public static ImageFrame Vignette(ImageFrame source, double sigma = 10.0,
        int? xOffset = null, int? yOffset = null)
    {
        // IM flattens alpha before vignette — transparent areas become the background color (black)
        // so the vignette darkening to black is visible at edges
        var input = source.HasAlpha ? FlattenAlphaToBlack(source) : source;

        int width = (int)input.Columns;
        int height = (int)input.Rows;
        const int channels = 3; // always opaque after flatten

        // Match IM default: 10% of dimensions when not specified
        int xOff = xOffset ?? (int)(width * 0.1);
        int yOff = yOffset ?? (int)(height * 0.1);

        double centerX = width / 2.0;
        double centerY = height / 2.0;
        double semiX = Math.Max(1.0, centerX - xOff);
        double semiY = Math.Max(1.0, centerY - yOff);

        // Step 1: Create grayscale mask — white ellipse on black background
        var mask = new ImageFrame();
        mask.Initialize(width, height, source.Colorspace, false);

        for (int y = 0; y < height; y++)
        {
            var maskRow = mask.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double dx = (x - centerX) / semiX;
                double dy = (y - centerY) / semiY;
                double distSq = dx * dx + dy * dy;

                int idx = x * 3;
                if (distSq <= 1.0)
                {
                    maskRow[idx] = Quantum.MaxValue;
                    maskRow[idx + 1] = Quantum.MaxValue;
                    maskRow[idx + 2] = Quantum.MaxValue;
                }
                // else: already 0 (black) from Initialize
            }
        }

        // Step 2: Blur the mask to create smooth transition from white center to black edges
        var blurredMask = ConvolutionFilters.GaussianBlur(mask, sigma > 0 ? sigma : 10.0);

        // Step 3: Multiply flattened pixels by mask intensity (always opaque output)
        var result = new ImageFrame();
        result.Initialize(width, height, input.Colorspace, false);

        Parallel.For(0, height, y =>
        {
            var srcRow = input.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            var mskRow = blurredMask.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                double factor = mskRow[x * 3] * Quantum.Scale;

                int idx = x * channels;
                dstRow[idx] = Quantum.Clamp((int)(srcRow[idx] * factor + 0.5));
                dstRow[idx + 1] = Quantum.Clamp((int)(srcRow[idx + 1] * factor + 0.5));
                dstRow[idx + 2] = Quantum.Clamp((int)(srcRow[idx + 2] * factor + 0.5));
            }
        });

        return result;
    }

    // ─── Wave ───────────────────────────────────────────────────

    /// <summary>
    /// Applies a sine-wave distortion that shifts pixels vertically based on their x position.
    /// The output image height is expanded by 2 × amplitude to accommodate the shift.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="amplitude">Peak amplitude of the wave in pixels.</param>
    /// <param name="wavelength">Wavelength of the sine wave in pixels.</param>
    public static ImageFrame Wave(ImageFrame source, double amplitude = 10.0, double wavelength = 50.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wavelength, 0, nameof(wavelength));

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.HasAlpha ? 4 : 3;
        int absAmplitude = (int)Math.Ceiling(Math.Abs(amplitude));
        int dstHeight = srcHeight + 2 * absAmplitude;
        int dstWidth = srcWidth;

        var result = new ImageFrame();
        result.Initialize(dstWidth, dstHeight, source.Colorspace, source.HasAlpha);

        // Precompute sine map
        double[] sineMap = new double[dstWidth];
        for (int x = 0; x < dstWidth; x++)
        {
            sineMap[x] = absAmplitude + amplitude * Math.Sin(2.0 * Math.PI * x / wavelength);
        }

        Parallel.For(0, dstHeight, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < dstWidth; x++)
            {
                double srcY = y - sineMap[x];
                double srcX = x;

                int dstIdx = x * channels;
                if (srcY < 0 || srcY >= srcHeight - 1 || srcX < 0 || srcX >= srcWidth)
                {
                    // Outside source bounds → transparent/background
                    dstRow[dstIdx] = 0;
                    dstRow[dstIdx + 1] = 0;
                    dstRow[dstIdx + 2] = 0;
                    if (channels == 4)
                        dstRow[dstIdx + 3] = 0;
                }
                else
                {
                    SampleBilinear(source, srcX, srcY, srcWidth, srcHeight, channels, dstRow, x);
                }
            }
        });

        return result;
    }

    // ─── Swirl ──────────────────────────────────────────────────

    /// <summary>
    /// Applies a rotational swirl distortion centered on the image.
    /// Pixels near the center rotate more than those near the edge.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="degrees">Total rotation angle at the center in degrees. Positive = counter-clockwise.</param>
    public static ImageFrame Swirl(ImageFrame source, double degrees = 60.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.HasAlpha ? 4 : 3;
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        double centerX = width / 2.0;
        double centerY = height / 2.0;
        double radius = Math.Min(centerX, centerY);
        double radiusSq = radius * radius;
        double invRadius = 1.0 / radius;
        double radians = degrees * Math.PI / 180.0;

        // Aspect ratio scaling so swirl is circular
        double scaleX = 1.0;
        double scaleY = (width > 0 && height > 0) ? (double)width / height : 1.0;

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                double deltaX = scaleX * (x - centerX);
                double deltaY = scaleY * (y - centerY);
                double distSq = deltaX * deltaX + deltaY * deltaY;

                int dstIdx = x * channels;

                if (distSq < radiusSq)
                {
                    double distance = Math.Sqrt(distSq);
                    // Factor decreases quadratically from center
                    double factor = 1.0 - distance * invRadius;
                    double angle = radians * factor * factor;
                    double cosA = Math.Cos(angle);
                    double sinA = Math.Sin(angle);

                    double srcX = (cosA * deltaX - sinA * deltaY) / scaleX + centerX;
                    double srcY = (sinA * deltaX + cosA * deltaY) / scaleY + centerY;

                    if (srcX >= 0 && srcX < width - 1 && srcY >= 0 && srcY < height - 1)
                    {
                        SampleBilinear(source, srcX, srcY, width, height, channels, dstRow, x);
                    }
                    else
                    {
                        CopySourcePixel(source, x, y, channels, dstRow, dstIdx);
                    }
                }
                else
                {
                    CopySourcePixel(source, x, y, channels, dstRow, dstIdx);
                }
            }
        });

        return result;
    }

    // ─── Implode ────────────────────────────────────────────────

    /// <summary>
    /// Applies an inward (implode) or outward (explode) spherical distortion.
    /// Positive amount pulls pixels toward center, negative pushes outward.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="amount">Distortion strength. Positive = implode, negative = explode.</param>
    public static ImageFrame Implode(ImageFrame source, double amount = 0.5)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.HasAlpha ? 4 : 3;
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        double centerX = width / 2.0;
        double centerY = height / 2.0;
        double radius = Math.Min(centerX, centerY);
        double radiusSq = radius * radius;
        double invRadius = 1.0 / radius;

        // Aspect ratio scaling so effect is circular
        double scaleX = 1.0;
        double scaleY = (width > 0 && height > 0) ? (double)width / height : 1.0;

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                double deltaX = scaleX * (x - centerX);
                double deltaY = scaleY * (y - centerY);
                double distSq = deltaX * deltaX + deltaY * deltaY;

                int dstIdx = x * channels;

                if (distSq < radiusSq)
                {
                    double distance = Math.Sqrt(distSq);
                    // Sinusoidal factor: sin(π/2 * dist/radius) ^ (-amount)
                    double factor = 1.0;
                    if (distance > 0.0)
                    {
                        double normalizedDist = distance * invRadius;
                        factor = Math.Pow(
                            Math.Sin(Math.PI * 0.5 * normalizedDist),
                            -amount);
                    }

                    double srcX = factor * deltaX / scaleX + centerX;
                    double srcY = factor * deltaY / scaleY + centerY;

                    if (srcX >= 0 && srcX < width - 1 && srcY >= 0 && srcY < height - 1)
                    {
                        SampleBilinear(source, srcX, srcY, width, height, channels, dstRow, x);
                    }
                    else
                    {
                        // Mapped outside bounds — use edge clamping
                        srcX = Math.Clamp(srcX, 0, width - 1);
                        srcY = Math.Clamp(srcY, 0, height - 1);
                        SampleBilinear(source, srcX, srcY, width, height, channels, dstRow, x);
                    }
                }
                else
                {
                    CopySourcePixel(source, x, y, channels, dstRow, dstIdx);
                }
            }
        });

        return result;
    }

    // ─── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Bilinear interpolation at fractional source coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SampleBilinear(ImageFrame source, double sx, double sy,
        int srcWidth, int srcHeight, int channels, Span<ushort> outRow, int outX)
    {
        int x0 = (int)Math.Floor(sx);
        int y0 = (int)Math.Floor(sy);
        double fx = sx - x0;
        double fy = sy - y0;

        for (int c = 0; c < channels; c++)
        {
            double v00 = GetPixelClamped(source, x0, y0, c, srcWidth, srcHeight, channels);
            double v10 = GetPixelClamped(source, x0 + 1, y0, c, srcWidth, srcHeight, channels);
            double v01 = GetPixelClamped(source, x0, y0 + 1, c, srcWidth, srcHeight, channels);
            double v11 = GetPixelClamped(source, x0 + 1, y0 + 1, c, srcWidth, srcHeight, channels);

            double top = v00 + (v10 - v00) * fx;
            double bot = v01 + (v11 - v01) * fx;
            double val = top + (bot - top) * fy;
            outRow[outX * channels + c] = Quantum.Clamp((int)(val + 0.5));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetPixelClamped(ImageFrame source, int x, int y, int c,
        int width, int height, int channels)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return source.GetPixelRow(y)[x * channels + c];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopySourcePixel(ImageFrame source, int x, int y,
        int channels, Span<ushort> dstRow, int dstIdx)
    {
        var srcRow = source.GetPixelRow(y);
        int srcIdx = x * channels;
        dstRow[dstIdx] = srcRow[srcIdx];
        dstRow[dstIdx + 1] = srcRow[srcIdx + 1];
        dstRow[dstIdx + 2] = srcRow[srcIdx + 2];
        if (channels == 4)
            dstRow[dstIdx + 3] = srcRow[srcIdx + 3];
    }
}
