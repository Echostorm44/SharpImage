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
        // Callers mostly pass whole quantum values: those come from a table of this same expression.
        if (srgbQuantum >= 0.0 && srgbQuantum <= Quantum.MaxValue && srgbQuantum == (int)srgbQuantum)
        {
            return DecodeTable[(int)srgbQuantum];
        }
        return DecodeCore(srgbQuantum);
    }

    private static readonly double[] DecodeTable = BuildDecodeTable();

    private static double[] BuildDecodeTable()
    {
        var table = new double[Quantum.MaxValue + 1];
        for (int i = 0; i <= Quantum.MaxValue; i++)
        {
            table[i] = DecodeCore(i);
        }
        return table;
    }

    private static double DecodeCore(double srgbQuantum)
    {
        double c = srgbQuantum * Quantum.Scale;
        double linear = c <= LinearThreshold
            ? c / 12.92
            : PortableMathD.Pow((c + 0.055) / 1.055, 2.4);
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
            : 1.055 * PortableMathD.Pow(c, 1.0 / 2.4) - 0.055;
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
            : PortableMathD.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Normalized encode: linear [0,1] → sRGB [0,1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EncodeNormalized(double linear)
    {
        return linear <= LinearCutoff
            ? linear * 12.92
            : 1.055 * PortableMathD.Pow(linear, 1.0 / 2.4) - 0.055;
    }
}
