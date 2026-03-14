// Drawing enums, structs, and small types used by the drawing engine.

using SharpImage.Core;
using System.Runtime.CompilerServices;

namespace SharpImage.Drawing;

/// <summary>
/// How the interior of a shape is determined for filling.
/// </summary>
public enum FillRule
{
    /// <summary>
    /// Even-odd parity rule.
    /// </summary>
    EvenOdd,
    /// <summary>
    /// Non-zero winding number rule.
    /// </summary>
    NonZero
}

/// <summary>
/// How the end of an open stroke is rendered.
/// </summary>
public enum LineCap
{
    Butt,
    Round,
    Square
}

/// <summary>
/// How corners between stroke segments are rendered.
/// </summary>
public enum LineJoin
{
    Miter,
    Round,
    Bevel
}

/// <summary>
/// A 2D point with double precision.
/// </summary>
public struct PointD
{
    public double X;
    public double Y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PointD(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD p, double s) => new(p.X * s, p.Y * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double DistanceTo(PointD other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// An RGBA color used in the drawing context.
/// </summary>
public struct DrawColor
{
    public byte R, G, B, A;

    public DrawColor(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static DrawColor Black => new(0, 0, 0);
    public static DrawColor White => new(255, 255, 255);
    public static DrawColor Transparent => new(0, 0, 0, 0);
    public static DrawColor Red => new(255, 0, 0);
    public static DrawColor Green => new(0, 255, 0);
    public static DrawColor Blue => new(0, 0, 255);
}

/// <summary>
/// Immutable snapshot of drawing state for the context stack.
/// </summary>
public sealed class DrawingState
{
    public DrawColor FillColor = DrawColor.Black;
    public DrawColor StrokeColor = DrawColor.Transparent;
    public double StrokeWidth = 1.0;
    public double[] StrokeDashPattern = [];
    public double StrokeDashOffset;
    public FillRule FillRule = FillRule.EvenOdd;
    public LineCap LineCap = LineCap.Butt;
    public LineJoin LineJoin = LineJoin.Miter;
    public double MiterLimit = 10.0;
    public bool Antialias = true;
    public double Opacity = 1.0;
    public AffineMatrix Transform = AffineMatrix.Identity;

    public DrawingState Clone() => new()
    {
        FillColor = FillColor,
        StrokeColor = StrokeColor,
        StrokeWidth = StrokeWidth,
        StrokeDashPattern = (double[])StrokeDashPattern.Clone(),
        StrokeDashOffset = StrokeDashOffset,
        FillRule = FillRule,
        LineCap = LineCap,
        LineJoin = LineJoin,
        MiterLimit = MiterLimit,
        Antialias = Antialias,
        Opacity = Opacity,
        Transform = Transform
    };
}
