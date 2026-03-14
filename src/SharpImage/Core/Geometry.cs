using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Core;

/// <summary>
/// Integer rectangle with position and dimensions. Used for crop regions, tile bounds, etc.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RectangleInfo
{
    public long Width;
    public long Height;
    public long X;
    public long Y;

    public RectangleInfo(long width, long height, long x = 0, long y = 0)
    {
        Width = width;
        Height = height;
        X = x;
        Y = y;
    }

    /// <summary>Total pixel count in this rectangle.</summary>
    public readonly long Area
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Width * Height;
    }

    /// <summary>True if the rectangle has zero or negative area.</summary>
    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// Returns the intersection of this rectangle with another.
    /// </summary>
    public readonly RectangleInfo Intersect(in RectangleInfo other)
    {
        long left = Math.Max(X, other.X);
        long top = Math.Max(Y, other.Y);
        long right = Math.Min(X + Width, other.X + other.Width);
        long bottom = Math.Min(Y + Height, other.Y + other.Height);

        if (right <= left || bottom <= top)
            return default;

        return new RectangleInfo(right - left, bottom - top, left, top);
    }

    public override readonly string ToString() => $"{Width}x{Height}+{X}+{Y}";
}

/// <summary>
/// Double-precision 2D point. Used for sub-pixel coordinates, control points, etc.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PointInfo
{
    public double X;
    public double Y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PointInfo(double x, double y)
    {
        X = x;
        Y = y;
    }

    public override readonly string ToString() => $"({X:F2}, {Y:F2})";
}

/// <summary>
/// Integer offset for animation frames within a canvas.
/// </summary>
public struct FrameOffset
{
    public int X;
    public int Y;

    public FrameOffset(int x, int y) { X = x; Y = y; }
}

/// <summary>
/// 2D affine transformation matrix.
/// Represents: [sx rx 0]
///             [ry sy 0]
///             [tx ty 1]
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AffineMatrix
{
    public double Sx;   // Scale X
    public double Rx;   // Rotate X (shear)
    public double Ry;   // Rotate Y (shear)
    public double Sy;   // Scale Y
    public double Tx;   // Translate X
    public double Ty;   // Translate Y

    /// <summary>Returns the identity matrix (no transformation).</summary>
    public static AffineMatrix Identity => new()
    {
        Sx = 1.0, Rx = 0.0,
        Ry = 0.0, Sy = 1.0,
        Tx = 0.0, Ty = 0.0
    };

    /// <summary>
    /// Multiplies this matrix with another: result = this * other.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly AffineMatrix Multiply(in AffineMatrix other)
    {
        return new AffineMatrix
        {
            Sx = Sx * other.Sx + Rx * other.Ry,
            Rx = Sx * other.Rx + Rx * other.Sy,
            Ry = Ry * other.Sx + Sy * other.Ry,
            Sy = Ry * other.Rx + Sy * other.Sy,
            Tx = Tx * other.Sx + Ty * other.Ry + other.Tx,
            Ty = Tx * other.Rx + Ty * other.Sy + other.Ty
        };
    }

    /// <summary>
    /// Transforms a point through this affine matrix.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly PointInfo Transform(in PointInfo point)
    {
        return new PointInfo(
            Sx * point.X + Ry * point.Y + Tx,
            Rx * point.X + Sy * point.Y + Ty
        );
    }

    /// <summary>Creates a translation matrix.</summary>
    public static AffineMatrix CreateTranslation(double tx, double ty)
        => new() { Sx = 1, Sy = 1, Tx = tx, Ty = ty };

    /// <summary>Creates a scale matrix.</summary>
    public static AffineMatrix CreateScale(double sx, double sy)
        => new() { Sx = sx, Sy = sy };

    /// <summary>Creates a rotation matrix (angle in radians).</summary>
    public static AffineMatrix CreateRotation(double angleRadians)
    {
        double c = Math.Cos(angleRadians), s = Math.Sin(angleRadians);
        return new AffineMatrix { Sx = c, Rx = s, Ry = -s, Sy = c };
    }

    /// <summary>Composes this * other (this applied after other).</summary>
    public readonly AffineMatrix Compose(in AffineMatrix other) => new()
    {
        Sx = Sx * other.Sx + Ry * other.Rx,
        Rx = Rx * other.Sx + Sy * other.Rx,
        Ry = Sx * other.Ry + Ry * other.Sy,
        Sy = Rx * other.Ry + Sy * other.Sy,
        Tx = Sx * other.Tx + Ry * other.Ty + Tx,
        Ty = Rx * other.Tx + Sy * other.Ty + Ty
    };

    /// <summary>Compute the inverse affine matrix.</summary>
    public readonly AffineMatrix Inverse()
    {
        double det = Sx * Sy - Rx * Ry;
        if (Math.Abs(det) < 1e-15) return Identity;
        double invDet = 1.0 / det;
        return new AffineMatrix
        {
            Sx = Sy * invDet,
            Rx = -Rx * invDet,
            Ry = -Ry * invDet,
            Sy = Sx * invDet,
            Tx = (Ry * Ty - Sy * Tx) * invDet,
            Ty = (Rx * Tx - Sx * Ty) * invDet
        };
    }
}
