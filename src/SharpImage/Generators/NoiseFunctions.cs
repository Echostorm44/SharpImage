// Procedural noise functions: Perlin, Simplex, Worley/Voronoi.
// All functions return values in [0, 1] for easy image generation.
// These are pure mathematical functions — no ImageFrame dependency.

using System.Runtime.CompilerServices;

namespace SharpImage.Generators;

/// <summary>
/// Classic Perlin gradient noise (improved, 2002 revision).
/// Returns values in [0, 1] for 2D input coordinates.
/// </summary>
public sealed class PerlinNoise
{
    private readonly byte[] permutation = new byte[512];

    public PerlinNoise(int seed = 0)
    {
        var rng = new Random(seed);
        byte[] p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;

        // Fisher-Yates shuffle
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        // Double the permutation table to avoid wrapping
        for (int i = 0; i < 512; i++)
            permutation[i] = p[i & 255];
    }

    /// <summary>
    /// Evaluates 2D Perlin noise at (x, y). Returns value in [0, 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Evaluate(double x, double y)
    {
        // Find unit grid cell
        int xi = (int)Math.Floor(x) & 255;
        int yi = (int)Math.Floor(y) & 255;

        // Relative position within cell
        double xf = x - Math.Floor(x);
        double yf = y - Math.Floor(y);

        // Quintic fade curves (6t^5 - 15t^4 + 10t^3)
        double u = Fade(xf);
        double v = Fade(yf);

        // Hash coordinates of the 4 corners
        int aa = permutation[permutation[xi] + yi];
        int ab = permutation[permutation[xi] + yi + 1];
        int ba = permutation[permutation[xi + 1] + yi];
        int bb = permutation[permutation[xi + 1] + yi + 1];

        // Gradient dot products and bilinear interpolation
        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);

        // Map from [-1,1] to [0,1]
        return (Lerp(x1, x2, v) + 1.0) * 0.5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Lerp(double a, double b, double t) => a + t * (b - a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Grad(int hash, double x, double y)
    {
        // Use 4 gradient directions
        return (hash & 3) switch
        {
            0 => x + y,
            1 => -x + y,
            2 => x - y,
            _ => -x - y,
        };
    }
}

/// <summary>
/// Simplex noise (2D). Faster than Perlin, fewer directional artifacts.
/// Returns values in [0, 1].
/// </summary>
public sealed class SimplexNoise
{
    private readonly byte[] perm = new byte[512];

    // Skewing factors for 2D simplex
    private static readonly double F2 = 0.5 * (Math.Sqrt(3.0) - 1.0);
    private static readonly double G2 = (3.0 - Math.Sqrt(3.0)) / 6.0;

    // 12 gradient directions
    private static readonly (double X, double Y)[] Gradients =
    [
        (1, 1), (-1, 1), (1, -1), (-1, -1),
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (-1, 1), (1, -1), (-1, -1),
    ];

    public SimplexNoise(int seed = 0)
    {
        var rng = new Random(seed);
        byte[] p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++)
            perm[i] = p[i & 255];
    }

    /// <summary>
    /// Evaluates 2D simplex noise at (x, y). Returns value in [0, 1].
    /// </summary>
    public double Evaluate(double x, double y)
    {
        // Skew input space to determine which simplex cell we're in
        double s = (x + y) * F2;
        int i = (int)Math.Floor(x + s);
        int j = (int)Math.Floor(y + s);

        double t = (i + j) * G2;
        double x0 = x - (i - t);
        double y0 = y - (j - t);

        // Determine which simplex we're in (upper or lower triangle)
        int i1, j1;
        if (x0 > y0) { i1 = 1; j1 = 0; }
        else { i1 = 0; j1 = 1; }

        double x1 = x0 - i1 + G2;
        double y1 = y0 - j1 + G2;
        double x2 = x0 - 1.0 + 2.0 * G2;
        double y2 = y0 - 1.0 + 2.0 * G2;

        int ii = i & 255;
        int jj = j & 255;

        // Contribution from the three corners
        double n0 = CornerContribution(x0, y0, perm[perm[ii] + jj]);
        double n1 = CornerContribution(x1, y1, perm[perm[ii + i1] + jj + j1]);
        double n2 = CornerContribution(x2, y2, perm[perm[ii + 1] + jj + 1]);

        // Scale to [0, 1]
        return (70.0 * (n0 + n1 + n2) + 1.0) * 0.5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CornerContribution(double x, double y, int hash)
    {
        double t = 0.5 - x * x - y * y;
        if (t < 0) return 0;
        t *= t;
        var g = Gradients[hash % 12];
        return t * t * (g.X * x + g.Y * y);
    }
}

/// <summary>
/// Distance metric for Worley/Voronoi noise.
/// </summary>
public enum WorleyDistance
{
    Euclidean,
    Manhattan,
    Chebyshev,
}

/// <summary>
/// Worley (cellular/Voronoi) noise. Returns distance to nearest feature points.
/// Values in [0, 1].
/// </summary>
public sealed class WorleyNoise
{
    private readonly int seed;

    public WorleyNoise(int seed = 0)
    {
        this.seed = seed;
    }

    /// <summary>
    /// Evaluates Worley F1 noise (distance to nearest point). Returns [0, 1].
    /// cellDensity controls how many feature points per unit area.
    /// </summary>
    public double EvaluateF1(double x, double y, double cellDensity = 1.0,
        WorleyDistance distanceType = WorleyDistance.Euclidean)
    {
        double scaledX = x * cellDensity;
        double scaledY = y * cellDensity;
        int cellX = (int)Math.Floor(scaledX);
        int cellY = (int)Math.Floor(scaledY);

        double minDist = double.MaxValue;

        // Check 3x3 neighborhood
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cellX + dx;
                int ny = cellY + dy;

                // Hash cell to get feature point position
                var (px, py) = CellPoint(nx, ny);
                double fx = nx + px;
                double fy = ny + py;

                double dist = CalculateDistance(scaledX - fx, scaledY - fy, distanceType);
                if (dist < minDist) minDist = dist;
            }
        }

        return Math.Clamp(minDist, 0, 1);
    }

    /// <summary>
    /// Evaluates Worley F2-F1 noise (difference between 2nd and 1st nearest distances).
    /// Produces cell-edge patterns.
    /// </summary>
    public double EvaluateF2MinusF1(double x, double y, double cellDensity = 1.0,
        WorleyDistance distanceType = WorleyDistance.Euclidean)
    {
        double scaledX = x * cellDensity;
        double scaledY = y * cellDensity;
        int cellX = (int)Math.Floor(scaledX);
        int cellY = (int)Math.Floor(scaledY);

        double f1 = double.MaxValue;
        double f2 = double.MaxValue;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cellX + dx;
                int ny = cellY + dy;
                var (px, py) = CellPoint(nx, ny);
                double dist = CalculateDistance(scaledX - (nx + px), scaledY - (ny + py), distanceType);

                if (dist < f1) { f2 = f1; f1 = dist; }
                else if (dist < f2) f2 = dist;
            }
        }

        return Math.Clamp(f2 - f1, 0, 1);
    }

    private (double X, double Y) CellPoint(int cx, int cy)
    {
        // Simple hash function to generate pseudo-random point within cell
        int h = HashCell(cx, cy);
        double px = (h & 0xFFFF) / 65535.0;
        double py = ((h >> 16) & 0xFFFF) / 65535.0;
        return (px, py);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int HashCell(int x, int y)
    {
        int h = seed;
        h ^= x * 374761393;
        h ^= y * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CalculateDistance(double dx, double dy, WorleyDistance type) => type switch
    {
        WorleyDistance.Manhattan => Math.Abs(dx) + Math.Abs(dy),
        WorleyDistance.Chebyshev => Math.Max(Math.Abs(dx), Math.Abs(dy)),
        _ => Math.Sqrt(dx * dx + dy * dy),
    };
}

/// <summary>
/// Fractional Brownian Motion — layers multiple octaves of noise for natural-looking detail.
/// </summary>
public static class FbmNoise
{
    /// <summary>
    /// The type of base noise to use for FBM/Turbulence.
    /// </summary>
    public enum BaseNoiseType { Perlin, Simplex }

    /// <summary>
    /// Evaluates FBM noise at (x, y) by summing multiple octaves.
    /// persistence: amplitude decay per octave (0.5 typical).
    /// lacunarity: frequency multiplier per octave (2.0 typical).
    /// Returns [0, 1].
    /// </summary>
    public static double Evaluate(double x, double y,
        int octaves, double persistence, double lacunarity,
        PerlinNoise perlin)
    {
        double total = 0;
        double amplitude = 1.0;
        double frequency = 1.0;
        double maxValue = 0;

        for (int i = 0; i < octaves; i++)
        {
            total += perlin.Evaluate(x * frequency, y * frequency) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return total / maxValue;
    }

    /// <summary>
    /// FBM using Simplex noise as the base.
    /// </summary>
    public static double Evaluate(double x, double y,
        int octaves, double persistence, double lacunarity,
        SimplexNoise simplex)
    {
        double total = 0;
        double amplitude = 1.0;
        double frequency = 1.0;
        double maxValue = 0;

        for (int i = 0; i < octaves; i++)
        {
            total += simplex.Evaluate(x * frequency, y * frequency) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return total / maxValue;
    }

    /// <summary>
    /// Turbulence: FBM with absolute-value noise for sharp creases.
    /// Produces cloud/fire-like patterns.
    /// </summary>
    public static double Turbulence(double x, double y,
        int octaves, double persistence, double lacunarity,
        PerlinNoise perlin)
    {
        double total = 0;
        double amplitude = 1.0;
        double frequency = 1.0;
        double maxValue = 0;

        for (int i = 0; i < octaves; i++)
        {
            // Map from [0,1] to [-1,1], take abs, back to [0,1]
            double n = Math.Abs(perlin.Evaluate(x * frequency, y * frequency) * 2.0 - 1.0);
            total += n * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return total / maxValue;
    }
}
