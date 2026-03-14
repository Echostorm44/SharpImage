using System.Runtime.CompilerServices;

namespace SharpImage.Core;

/// <summary>
/// Quantum type representing a single channel value. Default is 16-bit (ushort). All math operations use
/// AggressiveInlining for hot-path performance.
/// </summary>
public static class Quantum
{
    /// <summary>
    /// Maximum value for a quantum channel (65535 for 16-bit depth).
    /// </summary>
    public const ushort MaxValue = ushort.MaxValue;

    /// <summary>
    /// Scale factor to normalize quantum to [0.0, 1.0] range.
    /// </summary>
    public const double Scale = 1.0 / MaxValue;

    /// <summary>
    /// Default bit depth per channel.
    /// </summary>
    public const int Depth = 16;

    /// <summary>
    /// Fully opaque alpha value.
    /// </summary>
    public const ushort Opaque = MaxValue;

    /// <summary>
    /// Fully transparent alpha value.
    /// </summary>
    public const ushort Transparent = 0;

    /// <summary>
    /// Clamps a floating-point value to a valid quantum range [0, MaxValue]. Uses rounding for non-HDRI mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Clamp(double value)
    {
        if (double.IsNaN(value) || value <= 0.0)
        {
            return 0;
        }

        if (value >= MaxValue)
        {
            return MaxValue;
        }

        return (ushort)(value + 0.5);
    }

    /// <summary>
    /// Clamps a float value to valid quantum range [0, MaxValue].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Clamp(float value)
    {
        if (float.IsNaN(value) || value <= 0.0f)
        {
            return 0;
        }

        if (value >= MaxValue)
        {
            return MaxValue;
        }

        return (ushort)(value + 0.5f);
    }

    /// <summary>
    /// Scales a 16-bit quantum to an 8-bit byte value. Uses the fast integer formula: ((q + 128) - ((q + 128) >> 8)) >>
    /// 8
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ScaleToByte(ushort quantum)
    {
        return (byte)(((quantum + 128U) - ((quantum + 128U) >> 8)) >> 8);
    }

    /// <summary>
    /// Scales an 8-bit byte value to a 16-bit quantum. Maps [0, 255] to [0, 65535] using the formula: byte * 257.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ScaleFromByte(byte value)
    {
        return (ushort)(value * 257U);
    }

    /// <summary>
    /// Scales a quantum value to a normalized double in [0.0, 1.0].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ScaleToDouble(ushort quantum)
    {
        return quantum * Scale;
    }

    /// <summary>
    /// Scales a normalized double [0.0, 1.0] to a quantum value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ScaleFromDouble(double value)
    {
        return Clamp(value * MaxValue);
    }

    /// <summary>
    /// Scales a quantum value to a target bit depth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ScaleToDepth(ushort quantum, int targetDepth)
    {
        if (targetDepth == Depth)
        {
            return quantum;
        }

        if (targetDepth == 8)
        {
            return ScaleToByte(quantum);
        }

        double normalized = quantum * Scale;
        double maxTarget = (1 << targetDepth) - 1;
        return (ushort)(normalized * maxTarget + 0.5);
    }

    /// <summary>
    /// Scales a value from a source bit depth to 16-bit quantum.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ScaleFromDepth(uint value, int sourceDepth)
    {
        if (sourceDepth == Depth)
        {
            return (ushort)value;
        }

        if (sourceDepth == 8)
        {
            return ScaleFromByte((byte)value);
        }

        double maxSource = (1 << sourceDepth) - 1;
        return Clamp((value / maxSource) * MaxValue);
    }
}
