// Plasma fractal generator using diamond-square midpoint displacement.
// Produces colorful procedural noise textures.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Generators;

/// <summary>
/// Generates plasma fractal images using diamond-square subdivision.
/// </summary>
public static class PlasmaGenerator
{
    /// <summary>
    /// Creates a plasma fractal image with optional seed for reproducibility.
    /// </summary>
    public static ImageFrame Generate(int width, int height, int? seed = null)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();

        // Work in float RGB per pixel for smooth interpolation
        int pixelCount = width * height;
        float[] rChannel = new float[pixelCount];
        float[] gChannel = new float[pixelCount];
        float[] bChannel = new float[pixelCount];

        // Seed corners with random colors
        SetPixel(rChannel, gChannel, bChannel, width, 0, 0, rng);
        SetPixel(rChannel, gChannel, bChannel, width, width - 1, 0, rng);
        SetPixel(rChannel, gChannel, bChannel, width, 0, height - 1, rng);
        SetPixel(rChannel, gChannel, bChannel, width, width - 1, height - 1, rng);

        // Diamond-square recursive subdivision
        Subdivide(rChannel, gChannel, bChannel, width, height,
                  0, 0, width - 1, height - 1, rng, 1.0f);

        // Convert float buffers to quantum pixel data
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                int offset = x * ch;
                row[offset] = Quantum.Clamp(rChannel[idx] * Quantum.MaxValue);
                row[offset + 1] = Quantum.Clamp(gChannel[idx] * Quantum.MaxValue);
                row[offset + 2] = Quantum.Clamp(bChannel[idx] * Quantum.MaxValue);
            }
        }

        return frame;
    }

    private static void SetPixel(float[] r, float[] g, float[] b,
        int stride, int x, int y, Random rng)
    {
        int idx = y * stride + x;
        r[idx] = (float)rng.NextDouble();
        g[idx] = (float)rng.NextDouble();
        b[idx] = (float)rng.NextDouble();
    }

    private static void Subdivide(float[] rCh, float[] gCh, float[] bCh,
        int stride, int height,
        int x1, int y1, int x2, int y2,
        Random rng, float roughness)
    {
        int dx = x2 - x1;
        int dy = y2 - y1;
        if (dx < 2 && dy < 2) return;

        int mx = (x1 + x2) / 2;
        int my = (y1 + y2) / 2;
        float displacement = roughness * 0.5f;

        // Diamond step: set midpoint as average of corners + random displacement
        AverageFour(rCh, gCh, bCh, stride, mx, my,
            x1, y1, x2, y1, x1, y2, x2, y2, rng, displacement);

        // Square step: set edge midpoints
        if (dx >= 2)
        {
            AverageTwo(rCh, gCh, bCh, stride, mx, y1,
                x1, y1, x2, y1, rng, displacement);
            AverageTwo(rCh, gCh, bCh, stride, mx, y2,
                x1, y2, x2, y2, rng, displacement);
        }

        if (dy >= 2)
        {
            AverageTwo(rCh, gCh, bCh, stride, x1, my,
                x1, y1, x1, y2, rng, displacement);
            AverageTwo(rCh, gCh, bCh, stride, x2, my,
                x2, y1, x2, y2, rng, displacement);
        }

        float nextRoughness = roughness * 0.55f;

        // Recurse into quadrants
        Subdivide(rCh, gCh, bCh, stride, height, x1, y1, mx, my, rng, nextRoughness);
        Subdivide(rCh, gCh, bCh, stride, height, mx, y1, x2, my, rng, nextRoughness);
        Subdivide(rCh, gCh, bCh, stride, height, x1, my, mx, y2, rng, nextRoughness);
        Subdivide(rCh, gCh, bCh, stride, height, mx, my, x2, y2, rng, nextRoughness);
    }

    private static void AverageFour(float[] rCh, float[] gCh, float[] bCh,
        int stride, int tx, int ty,
        int ax, int ay, int bx, int by, int cx, int cy, int dx, int dy,
        Random rng, float displacement)
    {
        int ai = ay * stride + ax;
        int bi = by * stride + bx;
        int ci = cy * stride + cx;
        int di = dy * stride + dx;
        int ti = ty * stride + tx;

        rCh[ti] = Math.Clamp((rCh[ai] + rCh[bi] + rCh[ci] + rCh[di]) * 0.25f
            + ((float)rng.NextDouble() - 0.5f) * displacement, 0f, 1f);
        gCh[ti] = Math.Clamp((gCh[ai] + gCh[bi] + gCh[ci] + gCh[di]) * 0.25f
            + ((float)rng.NextDouble() - 0.5f) * displacement, 0f, 1f);
        bCh[ti] = Math.Clamp((bCh[ai] + bCh[bi] + bCh[ci] + bCh[di]) * 0.25f
            + ((float)rng.NextDouble() - 0.5f) * displacement, 0f, 1f);
    }

    private static void AverageTwo(float[] rCh, float[] gCh, float[] bCh,
        int stride, int tx, int ty,
        int ax, int ay, int bx, int by,
        Random rng, float displacement)
    {
        int ai = ay * stride + ax;
        int bi = by * stride + bx;
        int ti = ty * stride + tx;

        rCh[ti] = Math.Clamp((rCh[ai] + rCh[bi]) * 0.5f
            + ((float)rng.NextDouble() - 0.5f) * displacement, 0f, 1f);
        gCh[ti] = Math.Clamp((gCh[ai] + gCh[bi]) * 0.5f
            + ((float)rng.NextDouble() - 0.5f) * displacement, 0f, 1f);
        bCh[ti] = Math.Clamp((bCh[ai] + bCh[bi]) * 0.5f
            + ((float)rng.NextDouble() - 0.5f) * displacement, 0f, 1f);
    }
}
