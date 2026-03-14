// Procedural image generators for creating images from code (no file I/O).
// Gradient: linear/radial/multi-stop color transitions.
// Solid: uniform color fill.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Generators;

/// <summary>
/// Generates solid-color images.
/// </summary>
public static class SolidGenerator
{
    /// <summary>
    /// Creates a solid-color image from RGBA byte values.
    /// </summary>
    public static ImageFrame Generate(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var frame = new ImageFrame();
        bool hasAlpha = a < 255;
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);

        ushort qr = Quantum.ScaleFromByte(r);
        ushort qg = Quantum.ScaleFromByte(g);
        ushort qb = Quantum.ScaleFromByte(b);
        ushort qa = Quantum.ScaleFromByte(a);
        int ch = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * ch;
                row[offset] = qr;
                row[offset + 1] = qg;
                row[offset + 2] = qb;
                if (hasAlpha) row[offset + 3] = qa;
            }
        }

        return frame;
    }
}

/// <summary>
/// A color stop for multi-stop gradient generation.
/// Position is 0.0 (start) to 1.0 (end).
/// </summary>
public readonly record struct GradientStop(double Position, byte R, byte G, byte B, byte A = 255);

/// <summary>
/// Generates gradient images: linear, radial, and multi-stop.
/// </summary>
public static class GradientGenerator
{
    /// <summary>
    /// Creates a linear gradient at an arbitrary angle (degrees, 0=left-to-right, 90=top-to-bottom).
    /// </summary>
    public static ImageFrame Linear(int width, int height,
        byte r1, byte g1, byte b1,
        byte r2, byte g2, byte b2,
        double angleDegrees = 0.0)
    {
        var stops = new GradientStop[]
        {
            new(0.0, r1, g1, b1),
            new(1.0, r2, g2, b2)
        };
        return LinearMultiStop(width, height, stops, angleDegrees);
    }

    /// <summary>
    /// Creates a linear gradient with multiple color stops at an arbitrary angle.
    /// </summary>
    public static ImageFrame LinearMultiStop(int width, int height,
        ReadOnlySpan<GradientStop> stops, double angleDegrees = 0.0)
    {
        if (stops.Length < 2)
            throw new ArgumentException("At least 2 gradient stops required.", nameof(stops));

        bool hasAlpha = false;
        for (int i = 0; i < stops.Length; i++)
            if (stops[i].A < 255) { hasAlpha = true; break; }

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int ch = frame.NumberOfChannels;

        double angleRad = angleDegrees * Math.PI / 180.0;
        double cosA = Math.Cos(angleRad);
        double sinA = Math.Sin(angleRad);

        // Project corners onto gradient axis to find the full extent
        double cx = (width - 1) * 0.5;
        double cy = (height - 1) * 0.5;
        double halfExtent = Math.Abs(cx * cosA) + Math.Abs(cy * sinA);
        double invExtent = halfExtent > 0 ? 0.5 / halfExtent : 0;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            double dy = y - cy;

            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                // Project pixel onto gradient direction, normalize to 0..1
                double projection = dx * cosA + dy * sinA;
                double t = Math.Clamp(projection * invExtent + 0.5, 0.0, 1.0);

                InterpolateStops(stops, t, out ushort qr, out ushort qg, out ushort qb, out ushort qa);

                int offset = x * ch;
                row[offset] = qr;
                row[offset + 1] = qg;
                row[offset + 2] = qb;
                if (hasAlpha) row[offset + 3] = qa;
            }
        }

        return frame;
    }

    /// <summary>
    /// Creates a radial gradient from center outward.
    /// </summary>
    public static ImageFrame Radial(int width, int height,
        byte r1, byte g1, byte b1,
        byte r2, byte g2, byte b2,
        double centerX = 0.5, double centerY = 0.5)
    {
        var stops = new GradientStop[]
        {
            new(0.0, r1, g1, b1),
            new(1.0, r2, g2, b2)
        };
        return RadialMultiStop(width, height, stops, centerX, centerY);
    }

    /// <summary>
    /// Creates a radial gradient with multiple color stops.
    /// centerX/centerY are normalized 0..1 (0.5 = image center).
    /// </summary>
    public static ImageFrame RadialMultiStop(int width, int height,
        ReadOnlySpan<GradientStop> stops,
        double centerX = 0.5, double centerY = 0.5)
    {
        if (stops.Length < 2)
            throw new ArgumentException("At least 2 gradient stops required.", nameof(stops));

        bool hasAlpha = false;
        for (int i = 0; i < stops.Length; i++)
            if (stops[i].A < 255) { hasAlpha = true; break; }

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int ch = frame.NumberOfChannels;

        double cx = centerX * (width - 1);
        double cy = centerY * (height - 1);

        // Maximum distance from center to any corner
        double d1 = Math.Sqrt(cx * cx + cy * cy);
        double d2 = Math.Sqrt((width - 1 - cx) * (width - 1 - cx) + cy * cy);
        double d3 = Math.Sqrt(cx * cx + (height - 1 - cy) * (height - 1 - cy));
        double d4 = Math.Sqrt((width - 1 - cx) * (width - 1 - cx) + (height - 1 - cy) * (height - 1 - cy));
        double maxDist = Math.Max(Math.Max(d1, d2), Math.Max(d3, d4));
        double invMaxDist = maxDist > 0 ? 1.0 / maxDist : 0;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            double dy = y - cy;
            double dySq = dy * dy;

            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double dist = Math.Sqrt(dx * dx + dySq);
                double t = Math.Clamp(dist * invMaxDist, 0.0, 1.0);

                InterpolateStops(stops, t, out ushort qr, out ushort qg, out ushort qb, out ushort qa);

                int offset = x * ch;
                row[offset] = qr;
                row[offset + 1] = qg;
                row[offset + 2] = qb;
                if (hasAlpha) row[offset + 3] = qa;
            }
        }

        return frame;
    }

    /// <summary>
    /// Interpolates between gradient stops at position t (0..1).
    /// </summary>
    private static void InterpolateStops(ReadOnlySpan<GradientStop> stops, double t,
        out ushort r, out ushort g, out ushort b, out ushort a)
    {
        // Find the two surrounding stops
        if (t <= stops[0].Position)
        {
            r = Quantum.ScaleFromByte(stops[0].R);
            g = Quantum.ScaleFromByte(stops[0].G);
            b = Quantum.ScaleFromByte(stops[0].B);
            a = Quantum.ScaleFromByte(stops[0].A);
            return;
        }

        if (t >= stops[^1].Position)
        {
            r = Quantum.ScaleFromByte(stops[^1].R);
            g = Quantum.ScaleFromByte(stops[^1].G);
            b = Quantum.ScaleFromByte(stops[^1].B);
            a = Quantum.ScaleFromByte(stops[^1].A);
            return;
        }

        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (t >= stops[i].Position && t <= stops[i + 1].Position)
            {
                double range = stops[i + 1].Position - stops[i].Position;
                double frac = range > 0 ? (t - stops[i].Position) / range : 0;

                r = Quantum.Clamp(Quantum.ScaleFromByte(stops[i].R) * (1 - frac)
                    + Quantum.ScaleFromByte(stops[i + 1].R) * frac);
                g = Quantum.Clamp(Quantum.ScaleFromByte(stops[i].G) * (1 - frac)
                    + Quantum.ScaleFromByte(stops[i + 1].G) * frac);
                b = Quantum.Clamp(Quantum.ScaleFromByte(stops[i].B) * (1 - frac)
                    + Quantum.ScaleFromByte(stops[i + 1].B) * frac);
                a = Quantum.Clamp(Quantum.ScaleFromByte(stops[i].A) * (1 - frac)
                    + Quantum.ScaleFromByte(stops[i + 1].A) * frac);
                return;
            }
        }

        // Fallback (should not reach)
        r = g = b = 0;
        a = Quantum.ScaleFromByte(255);
    }
}
