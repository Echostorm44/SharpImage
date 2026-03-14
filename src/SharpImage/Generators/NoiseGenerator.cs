// Procedural noise image generator.
// Composes noise primitives (Perlin, Simplex, Worley, FBM, Turbulence)
// into ImageFrame outputs with colorization options.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Generators;

/// <summary>
/// Generates noise-based procedural images.
/// </summary>
public static class NoiseGenerator
{
    /// <summary>
    /// Creates a Perlin noise image. Grayscale by default.
    /// </summary>
    public static ImageFrame Perlin(int width, int height,
        double frequency = 4.0, int seed = 0, bool colorize = false)
    {
        var noise = new PerlinNoise(seed);
        return GenerateFromNoise(width, height, frequency, colorize,
            (x, y) => noise.Evaluate(x, y));
    }

    /// <summary>
    /// Creates a Simplex noise image.
    /// </summary>
    public static ImageFrame Simplex(int width, int height,
        double frequency = 4.0, int seed = 0, bool colorize = false)
    {
        var noise = new SimplexNoise(seed);
        return GenerateFromNoise(width, height, frequency, colorize,
            (x, y) => noise.Evaluate(x, y));
    }

    /// <summary>
    /// Creates a Worley/Voronoi cell noise image.
    /// </summary>
    public static ImageFrame Worley(int width, int height,
        double cellDensity = 8.0, int seed = 0,
        WorleyDistance distanceType = WorleyDistance.Euclidean,
        bool useF2MinusF1 = false)
    {
        var noise = new WorleyNoise(seed);
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        double invW = 1.0 / width;
        double invH = 1.0 / height;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double nx = x * invW;
                double ny = y * invH;

                double value = useF2MinusF1
                    ? noise.EvaluateF2MinusF1(nx, ny, cellDensity, distanceType)
                    : noise.EvaluateF1(nx, ny, cellDensity, distanceType);

                ushort q = Quantum.Clamp(value * Quantum.MaxValue);
                int off = x * ch;
                row[off] = q;
                row[off + 1] = q;
                row[off + 2] = q;
            }
        }

        return frame;
    }

    /// <summary>
    /// Creates an FBM (Fractional Brownian Motion) noise image.
    /// Layers multiple octaves of noise for natural detail.
    /// </summary>
    public static ImageFrame Fbm(int width, int height,
        int octaves = 6, double persistence = 0.5, double lacunarity = 2.0,
        double frequency = 4.0, int seed = 0,
        FbmNoise.BaseNoiseType baseNoise = FbmNoise.BaseNoiseType.Perlin,
        bool colorize = false)
    {
        if (baseNoise == FbmNoise.BaseNoiseType.Simplex)
        {
            var simplex = new SimplexNoise(seed);
            return GenerateFromNoise(width, height, frequency, colorize,
                (x, y) => FbmNoise.Evaluate(x, y, octaves, persistence, lacunarity, simplex));
        }
        else
        {
            var perlin = new PerlinNoise(seed);
            return GenerateFromNoise(width, height, frequency, colorize,
                (x, y) => FbmNoise.Evaluate(x, y, octaves, persistence, lacunarity, perlin));
        }
    }

    /// <summary>
    /// Creates a turbulence noise image (absolute-value FBM).
    /// Produces cloud/fire-like crease patterns.
    /// </summary>
    public static ImageFrame Turbulence(int width, int height,
        int octaves = 6, double persistence = 0.5, double lacunarity = 2.0,
        double frequency = 4.0, int seed = 0, bool colorize = false)
    {
        var perlin = new PerlinNoise(seed);
        return GenerateFromNoise(width, height, frequency, colorize,
            (x, y) => FbmNoise.Turbulence(x, y, octaves, persistence, lacunarity, perlin));
    }

    /// <summary>
    /// Creates a marble texture using turbulence-distorted sine waves.
    /// </summary>
    public static ImageFrame Marble(int width, int height,
        double frequency = 5.0, double turbulencePower = 5.0,
        int octaves = 6, int seed = 0)
    {
        var perlin = new PerlinNoise(seed);
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        double invW = 1.0 / width;
        double invH = 1.0 / height;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double nx = x * invW * frequency;
                double ny = y * invH * frequency;

                double turbulence = FbmNoise.Turbulence(nx, ny, octaves, 0.5, 2.0, perlin);
                double marble = Math.Sin(nx + turbulencePower * turbulence) * 0.5 + 0.5;

                ushort q = Quantum.Clamp(marble * Quantum.MaxValue);
                int off = x * ch;
                row[off] = q;
                row[off + 1] = q;
                row[off + 2] = q;
            }
        }

        return frame;
    }

    /// <summary>
    /// Creates a wood grain texture using noise-distorted concentric rings.
    /// </summary>
    public static ImageFrame Wood(int width, int height,
        double ringFrequency = 12.0, double noisePower = 0.1,
        double frequency = 4.0, int seed = 0)
    {
        var perlin = new PerlinNoise(seed);
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        double invW = 1.0 / width;
        double invH = 1.0 / height;

        // Wood colors: light and dark brown
        ushort lightR = Quantum.ScaleFromByte(200), lightG = Quantum.ScaleFromByte(150), lightB = Quantum.ScaleFromByte(80);
        ushort darkR = Quantum.ScaleFromByte(100), darkG = Quantum.ScaleFromByte(60), darkB = Quantum.ScaleFromByte(20);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double nx = (x * invW - 0.5) * frequency;
                double ny = (y * invH - 0.5) * frequency;

                double noiseVal = perlin.Evaluate(nx * 2, ny * 2);
                double dist = Math.Sqrt(nx * nx + ny * ny) + noisePower * noiseVal;
                double ring = (Math.Sin(dist * ringFrequency * Math.PI) + 1.0) * 0.5;

                int off = x * ch;
                row[off] = (ushort)(darkR + (int)((lightR - darkR) * ring));
                row[off + 1] = (ushort)(darkG + (int)((lightG - darkG) * ring));
                row[off + 2] = (ushort)(darkB + (int)((lightB - darkB) * ring));
            }
        }

        return frame;
    }

    /// <summary>
    /// Internal helper: generates a grayscale or colorized noise image from a noise function.
    /// </summary>
    private static ImageFrame GenerateFromNoise(int width, int height,
        double frequency, bool colorize,
        Func<double, double, double> noiseFunc)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        double invW = 1.0 / width;
        double invH = 1.0 / height;

        if (colorize)
        {
            // Use 3 offset evaluations for R, G, B channels
            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    double nx = x * invW * frequency;
                    double ny = y * invH * frequency;

                    double r = noiseFunc(nx, ny);
                    double g = noiseFunc(nx + 100.0, ny + 100.0);
                    double b = noiseFunc(nx + 200.0, ny + 200.0);

                    int off = x * ch;
                    row[off] = Quantum.Clamp(r * Quantum.MaxValue);
                    row[off + 1] = Quantum.Clamp(g * Quantum.MaxValue);
                    row[off + 2] = Quantum.Clamp(b * Quantum.MaxValue);
                }
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    double nx = x * invW * frequency;
                    double ny = y * invH * frequency;
                    double value = noiseFunc(nx, ny);

                    ushort q = Quantum.Clamp(value * Quantum.MaxValue);
                    int off = x * ch;
                    row[off] = q;
                    row[off + 1] = q;
                    row[off + 2] = q;
                }
            }
        }

        return frame;
    }
}
