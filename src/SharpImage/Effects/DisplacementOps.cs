// SharpImage — Displacement mapping and image-space map generation.
// DisplacementMap, NormalMapFromHeight, Spherize.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Displacement and map generation operations: displacement map distortion,
/// normal map generation from height maps, and spherize distortion.
/// </summary>
public static class DisplacementOps
{
    // ─── Displacement Map ───────────────────────────────────────

    /// <summary>
    /// Distorts the source image by offsetting pixel positions according to a
    /// grayscale displacement map. Red channel controls horizontal offset,
    /// green channel controls vertical offset. Mid-gray (50%) = no displacement.
    /// </summary>
    /// <param name="source">Source image to distort.</param>
    /// <param name="displacementMap">RGB map: R=horizontal, G=vertical displacement.</param>
    /// <param name="scaleX">Horizontal displacement scale in pixels.</param>
    /// <param name="scaleY">Vertical displacement scale in pixels.</param>
    public static ImageFrame DisplacementMap(ImageFrame source, ImageFrame displacementMap, double scaleX = 20.0, double scaleY = 20.0)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        int dCh = displacementMap.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        double halfMax = Quantum.MaxValue * 0.5;

        Parallel.For(0, h, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            int my = Math.Clamp(y, 0, (int)displacementMap.Rows - 1);
            var mapRow = displacementMap.GetPixelRow(my);

            for (int x = 0; x < w; x++)
            {
                int mx = Math.Clamp(x, 0, (int)displacementMap.Columns - 1);
                int mOff = mx * dCh;

                // R channel = horizontal, G channel = vertical (or just intensity for grayscale)
                double dxNorm = (mapRow[mOff] - halfMax) / halfMax;
                double dyNorm = dCh >= 2 ? (mapRow[mOff + 1] - halfMax) / halfMax : dxNorm;

                double srcX = x + dxNorm * scaleX;
                double srcY = y + dyNorm * scaleY;

                SampleBilinear(source, srcX, srcY, w, h, ch, hasAlpha, dstRow, x * ch);
            }
        });

        return result;
    }

    // ─── Normal Map from Height Map ─────────────────────────────

    /// <summary>
    /// Generates a tangent-space normal map from a grayscale height map using
    /// Sobel gradient estimation. Output encodes normals as RGB where
    /// (128,128,255) = flat surface pointing up.
    /// </summary>
    /// <param name="heightMap">Grayscale height map (white = high, black = low).</param>
    /// <param name="strength">Normal map strength/intensity multiplier.</param>
    public static ImageFrame NormalMapFromHeight(ImageFrame heightMap, double strength = 1.0)
    {
        int w = (int)heightMap.Columns;
        int h = (int)heightMap.Rows;
        int hCh = heightMap.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(heightMap.Columns, heightMap.Rows, ColorspaceType.RGB, false);

        Parallel.For(0, h, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < w; x++)
            {
                // Sobel 3×3 for X and Y gradients
                double gx = 0, gy = 0;

                for (int ky = -1; ky <= 1; ky++)
                {
                    int sy = Math.Clamp(y + ky, 0, h - 1);
                    var sRow = heightMap.GetPixelRow(sy);

                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int sx = Math.Clamp(x + kx, 0, w - 1);
                        double val = sRow[sx * hCh] * Quantum.Scale;

                        // Sobel X kernel: [-1,0,1; -2,0,2; -1,0,1]
                        double wx = kx switch { -1 => -(1 + Math.Abs(ky)), 1 => 1 + Math.Abs(ky), _ => 0 };
                        // Sobel Y kernel: [-1,-2,-1; 0,0,0; 1,2,1]
                        double wy = ky switch { -1 => -(1 + Math.Abs(kx)), 1 => 1 + Math.Abs(kx), _ => 0 };

                        gx += val * wx;
                        gy += val * wy;
                    }
                }

                gx *= strength;
                gy *= strength;

                // Normal vector: (-gx, -gy, 1), normalized
                double len = Math.Sqrt(gx * gx + gy * gy + 1.0);
                double nx = (-gx / len + 1.0) * 0.5;
                double ny = (-gy / len + 1.0) * 0.5;
                double nz = (1.0 / len + 1.0) * 0.5;

                int off = x * 3;
                dstRow[off] = Quantum.Clamp(nx * Quantum.MaxValue);
                dstRow[off + 1] = Quantum.Clamp(ny * Quantum.MaxValue);
                dstRow[off + 2] = Quantum.Clamp(nz * Quantum.MaxValue);
            }
        });

        return result;
    }

    // ─── Spherize ───────────────────────────────────────────────

    /// <summary>
    /// Wraps the image onto a sphere surface, creating a fisheye/globe effect.
    /// Pixels outside the sphere are transparent (if alpha) or black.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="amount">Spherize amount (0.0 = none, 1.0 = full sphere, negative = pinch).</param>
    /// <param name="centerX">Sphere center X (0.0–1.0). Default 0.5.</param>
    /// <param name="centerY">Sphere center Y (0.0–1.0). Default 0.5.</param>
    public static ImageFrame Spherize(ImageFrame source, double amount = 1.0, double centerX = 0.5, double centerY = 0.5)
    {
        int w = (int)source.Columns;
        int h = (int)source.Rows;
        int ch = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, hasAlpha);

        double cx = centerX * w;
        double cy = centerY * h;
        double radius = Math.Min(w, h) * 0.5;
        double r2 = radius * radius;

        Parallel.For(0, h, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double dist2 = dx * dx + dy * dy;

                if (dist2 >= r2)
                {
                    // Outside sphere: transparent or black
                    int off = x * ch;
                    if (hasAlpha) dstRow[off + ch - 1] = 0;
                    continue;
                }

                double dist = Math.Sqrt(dist2);
                double normDist = dist / radius;

                // Spherical refraction: map flat coords to sphere surface
                double theta = Math.Asin(normDist * amount);
                double newDist = radius * Math.Tan(theta) / Math.Tan(Math.Asin(amount));

                if (amount == 0 || dist < 0.001)
                {
                    newDist = dist;
                }
                else if (Math.Abs(amount) >= 0.999)
                {
                    // Full sphere
                    newDist = radius * Math.Sin(Math.Asin(normDist) * amount) / normDist * normDist;
                    // Simplified: just use the refracted coordinate
                    double angle = Math.Atan2(dy, dx);
                    double refracted = normDist * normDist * amount;
                    double srcX = cx + Math.Cos(angle) * refracted * radius;
                    double srcY = cy + Math.Sin(angle) * refracted * radius;
                    SampleBilinear(source, srcX, srcY, w, h, ch, hasAlpha, dstRow, x * ch);
                    continue;
                }

                double ang = Math.Atan2(dy, dx);
                double sX = cx + Math.Cos(ang) * newDist;
                double sY = cy + Math.Sin(ang) * newDist;

                SampleBilinear(source, sX, sY, w, h, ch, hasAlpha, dstRow, x * ch);
            }
        });

        return result;
    }

    // ─── Bilinear Sampling Helper ───────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SampleBilinear(ImageFrame source, double srcX, double srcY,
        int w, int h, int ch, bool hasAlpha, Span<ushort> dstRow, int dstOff)
    {
        int x0 = (int)Math.Floor(srcX);
        int y0 = (int)Math.Floor(srcY);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        double fx = srcX - x0;
        double fy = srcY - y0;

        x0 = Math.Clamp(x0, 0, w - 1);
        y0 = Math.Clamp(y0, 0, h - 1);
        x1 = Math.Clamp(x1, 0, w - 1);
        y1 = Math.Clamp(y1, 0, h - 1);

        var r00 = source.GetPixelRow(y0);
        var r10 = source.GetPixelRow(y1);

        double w00 = (1 - fx) * (1 - fy);
        double w10 = fx * (1 - fy);
        double w01 = (1 - fx) * fy;
        double w11 = fx * fy;

        int o00 = x0 * ch, o10 = x1 * ch, o01 = x0 * ch, o11 = x1 * ch;

        for (int c = 0; c < ch; c++)
        {
            double val = r00[o00 + c] * w00 + r00[o10 + c] * w10 +
                         r10[o01 + c] * w01 + r10[o11 + c] * w11;
            dstRow[dstOff + c] = Quantum.Clamp(val);
        }
    }
}
