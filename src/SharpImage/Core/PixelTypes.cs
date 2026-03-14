using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Core;

/// <summary>
/// High-precision floating-point pixel representation used for color calculations, compositing, and colorspace
/// conversions. Stack-allocated for zero GC pressure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PixelInfo
{
    public StorageClass StorageClass;
    public ColorspaceType Colorspace;
    public PixelTrait AlphaTrait;
    public double Fuzz;
    public int Depth;
    public long Count;
    public double Red;
    public double Green;
    public double Blue;
    public double Black;
    public double Alpha;
    public double Index;

    /// <summary>
    /// Creates a PixelInfo with RGB values in quantum-scaled range [0, 65535].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PixelInfo FromRgb(double red, double green, double blue)
    {
        return new PixelInfo
        {
            StorageClass = StorageClass.Direct,
            Colorspace = ColorspaceType.SRGB,
            AlphaTrait = PixelTrait.Undefined,
            Depth = Quantum.Depth,
            Red = red,
            Green = green,
            Blue = blue,
            Alpha = Quantum.Opaque
        };
    }

    /// <summary>
    /// Creates a PixelInfo with RGBA values in quantum-scaled range [0, 65535].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PixelInfo FromRgba(double red, double green, double blue, double alpha)
    {
        return new PixelInfo
        {
            StorageClass = StorageClass.Direct,
            Colorspace = ColorspaceType.SRGB,
            AlphaTrait = PixelTrait.Blend,
            Depth = Quantum.Depth,
            Red = red,
            Green = green,
            Blue = blue,
            Alpha = alpha
        };
    }

    /// <summary>
    /// Returns true if the pixel has an active alpha channel.
    /// </summary>
    public readonly bool HasAlpha
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AlphaTrait != PixelTrait.Undefined;
    }

    /// <summary>
    /// Determines if two pixels are "close enough" given the fuzz tolerance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsFuzzyEqual(in PixelInfo other, double fuzz)
    {
        double distance = 0.0;
        double scale = 1.0;

        if (HasAlpha || other.HasAlpha)
        {
            double alphaDistance = Alpha - other.Alpha;
            distance += alphaDistance * alphaDistance;
            if (distance > fuzz * fuzz)
            {
                return false;
            }

            scale = Quantum.Scale;
            scale *= Alpha * Quantum.Scale;
            double otherScale = other.Alpha * Quantum.Scale;
            scale = Math.Max(scale, otherScale);
        }

        double rd = Red - other.Red;
        distance += scale * rd * rd;
        if (distance > fuzz * fuzz)
        {
            return false;
        }

        double gd = Green - other.Green;
        distance += scale * gd * gd;
        if (distance > fuzz * fuzz)
        {
            return false;
        }

        double bd = Blue - other.Blue;
        distance += scale * bd * bd;
        if (distance > fuzz * fuzz)
        {
            return false;
        }

        if (Colorspace == ColorspaceType.CMYK)
        {
            double kd = Black - other.Black;
            distance += scale * kd * kd;
        }

        return distance <= fuzz * fuzz;
    }
}

/// <summary>
/// Maps a logical pixel channel to its physical offset in the pixel data array.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PixelChannelMap
{
    public PixelChannel Channel;
    public PixelTrait Traits;
    public int Offset;
}

/// <summary>
/// Compact integer-based pixel for storage efficiency. Used in colormaps and serialization.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PixelPacket
{
    public uint Red;
    public uint Green;
    public uint Blue;
    public uint Alpha;
    public uint Black;
}
