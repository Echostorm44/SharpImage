using System.Runtime.CompilerServices;

namespace SharpImage.Colorspaces;

using SharpImage.Core;

/// <summary>
/// sRGB gamma encode/decode per IEC 61966-2-1. DecodeGamma: sRGB → linear, EncodeGamma: linear → sRGB.
/// </summary>
public static class SrgbConverter
{
    private const double LinearThreshold = 0.04045;
    private const double LinearCutoff = 0.0031308;

    /// <summary>
    /// Converts an sRGB quantum value to linear light (removes gamma). Input/output in quantum-scaled range [0,
    /// QuantumRange].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Decode(double srgbQuantum)
    {
        double c = srgbQuantum * Quantum.Scale;
        double linear = c <= LinearThreshold
            ? c / 12.92
            : Math.Pow((c + 0.055) / 1.055, 2.4);
        return linear * Quantum.MaxValue;
    }

    /// <summary>
    /// Converts a linear-light quantum value to sRGB (applies gamma). Input/output in quantum-scaled range [0,
    /// QuantumRange].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Encode(double linearQuantum)
    {
        double c = linearQuantum * Quantum.Scale;
        double srgb = c <= LinearCutoff
            ? c * 12.92
            : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        return srgb * Quantum.MaxValue;
    }

    /// <summary>
    /// Normalized decode: sRGB [0,1] → linear [0,1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DecodeNormalized(double srgb)
    {
        return srgb <= LinearThreshold
            ? srgb / 12.92
            : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Normalized encode: linear [0,1] → sRGB [0,1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EncodeNormalized(double linear)
    {
        return linear <= LinearCutoff
            ? linear * 12.92
            : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
    }
}
