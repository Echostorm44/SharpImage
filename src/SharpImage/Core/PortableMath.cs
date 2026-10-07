using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Core;

/// <summary>
/// Float <c>pow</c> / <c>exp</c> / <c>log</c> / <c>log2</c> / <c>log10</c> / <c>cbrt</c> evaluated with IEEE double
/// arithmetic and fused multiply-add only, so the result is bit-identical on every OS and CPU.
/// <see cref="MathF.Pow(float, float)"/> and friends call the C runtime (UCRT on Windows, glibc on Linux), and those differ
/// in the last bit for roughly 1 input in 1000, which is enough to change gain map fractions and tone-mapped pixels. The
/// double counterparts are in <see cref="PortableMathD"/>.
/// <para>Each function goes through table-driven log2 / exp2 kernels in double (64-entry tables, short polynomials;
/// relative error near 2^-52) and rounds to float once, so the result is the correctly rounded float except when the
/// exact value lies within that error of a rounding boundary.</para>
/// </summary>
internal static class PortableMath
{
    private const double Ln2 = 0.6931471805599453094172321;
    private const double Log10Of2 = 0.30102999566398119521373889;
    private const double Log2E = 1.4426950408889634073599247;
    private const double Log2ELo = 2.0355273740931033e-17;   // log2(e) - Log2E

    /// <summary>x^y with C99 powf special cases.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static float Pow(float x, float y)
    {
        if (x > 0.0f && x <= float.MaxValue && MathF.Abs(y) <= float.MaxValue && x != 1.0f && y != 0.0f && y != 2.0f)
        {
            // the common case: positive finite base, finite exponent
            Log2Parts(x, out int e, out double l);
            return (float)Exp2(y * (double)e, y * l);   // y * e is exact
        }
        if (y == 0.0f || x == 1.0f)
        {
            return 1.0f;
        }
        if (float.IsNaN(x) || float.IsNaN(y))
        {
            return float.NaN;
        }

        bool yIsInt = float.IsInteger(y);
        bool yIsOdd = yIsInt && MathF.Abs(y) < 16777216.0f && ((long)y & 1) != 0;
        float ax = MathF.Abs(x);

        if (float.IsInfinity(y))
        {
            if (ax == 1.0f)
            {
                return 1.0f;
            }
            return (ax < 1.0f) == (y > 0.0f) ? 0.0f : float.PositiveInfinity;
        }
        if (x == 0.0f || float.IsInfinity(x))
        {
            // 0^y and inf^y: magnitude 0 or inf, negative only for -0 / -inf with an odd integer exponent.
            bool huge = (x == 0.0f) == (y < 0.0f);
            float mag = huge ? float.PositiveInfinity : 0.0f;
            return float.IsNegative(x) && yIsOdd ? -mag : mag;
        }
        if (x < 0.0f && !yIsInt)
        {
            return float.NaN;
        }
        if (y == 2.0f)
        {
            // x * x is exact in double, so a square that falls on a float rounding midpoint still rounds to even.
            return (float)((double)x * x);
        }
        Log2Parts(ax, out int ea, out double la);
        float r = (float)Exp2(y * (double)ea, y * la);
        return x < 0.0f && yIsOdd ? -r : r;
    }

    /// <summary>Cube root (exact for perfect cubes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Cbrt(float x)
    {
        if (x == 0.0f || float.IsNaN(x) || float.IsInfinity(x))
        {
            return x;
        }
        double ax = MathF.Abs(x);   // a float widened to double is a normal double
        // FreeBSD s_cbrt.c's estimate: exponent / 3 by bit arithmetic, then a polynomial to 23 bits
        double t = BitConverter.Int64BitsToDouble((long)((ulong)(BitConverter.DoubleToInt64Bits(ax) >> 32) / 3 + 715094163) << 32);
        double q = (t * t) * (t / ax);
        t *= (1.87595182427177009643 + q * (-1.88497979543377169875 + q * 1.621429720105354466140))
            + ((q * q) * q) * (-0.758397934778766047437 + q * 0.145996192886612446982);
        // one Halley step: cubic convergence, from 23 bits to the precision of double
        double t3 = t * t * t;
        t *= (t3 + 2.0 * ax) / (2.0 * t3 + ax);
        return (float)(x < 0.0f ? -t : t);
    }
    /// <summary>e^x.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Exp(float x)
    {
        if (float.IsNaN(x))
        {
            return float.NaN;
        }
        // x log2(e) as a double-double
        double th = x * Log2E;
        double tl = Math.FusedMultiplyAdd(x, Log2E, -th) + x * Log2ELo;
        return (float)Exp2(th, tl);
    }

    /// <summary>Natural logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Log(float x)
    {
        if (!(x > 0.0f && x <= float.MaxValue) && !TryLogSpecial(x, out float special))
        {
            return special;
        }
        Log2Parts(x, out int e, out double l);
        return (float)Math.FusedMultiplyAdd(e, Ln2, l * Ln2);
    }

    /// <summary>Base-2 logarithm (exact for powers of two).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Log2(float x)
    {
        if (!(x > 0.0f && x <= float.MaxValue) && !TryLogSpecial(x, out float special))
        {
            return special;
        }
        Log2Parts(x, out int e, out double l);
        return (float)(e + l);
    }

    /// <summary>Base-10 logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Log10(float x)
    {
        if (!(x > 0.0f && x <= float.MaxValue) && !TryLogSpecial(x, out float special))
        {
            return special;
        }
        Log2Parts(x, out int e, out double l);
        return (float)Math.FusedMultiplyAdd(e, Log10Of2, l * Log10Of2);
    }

    /// <summary>
    /// Natural logarithm to within about an ulp, faster than <see cref="Log(float)"/> (a shorter polynomial on the same
    /// table) and still bit-identical on every OS, for heuristics where the last bit need not be the correctly rounded one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float LogFast(float x)
    {
        if (!(x > 0.0f && x <= float.MaxValue) && !TryLogSpecial(x, out float special))
        {
            return special;
        }
        Log2PartsFast(x, out int e, out double l);
        return (float)Math.FusedMultiplyAdd(e, Ln2, l * Ln2);
    }

    /// <summary>Base-2 logarithm to within about an ulp (see <see cref="LogFast"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Log2Fast(float x)
    {
        if (!(x > 0.0f && x <= float.MaxValue) && !TryLogSpecial(x, out float special))
        {
            return special;
        }
        Log2PartsFast(x, out int e, out double l);
        return (float)(e + l);
    }

    /// <summary>False with the result for NaN, negative, zero and infinite arguments.</summary>
    private static bool TryLogSpecial(float x, out float result)
    {
        result = 0.0f;
        if (float.IsNaN(x) || x < 0.0f)
        {
            result = float.NaN;
            return false;
        }
        if (x == 0.0f)
        {
            result = float.NegativeInfinity;
            return false;
        }
        if (float.IsPositiveInfinity(x))
        {
            result = float.PositiveInfinity;
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- kernels

    /// <summary>log2(x) = e + l for positive finite x (float subnormals included): |l| &lt; 1, relative error ~2^-52.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Log2Parts(double x, out int e, out double l)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);   // a float widened to double is always a normal double
        int ci = (int)((bits >> 46) & 63) * 3;
        e = (int)((bits >> 52) & 0x7FF) - 1023 + (int)Unsafe.Add(ref MemoryMarshal.GetReference(Log2TableBits), ci + 2);
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);   // [1, 2)
        // z = m Inv - 1, |z| < 2^-6; Inv has 10 significant bits, so m Inv is exact in double
        double z = Math.FusedMultiplyAdd(m, Entry(Log2TableBits, ci), -1.0);
        // log2(1 + z) = z (1/ln2 - z/(2 ln2) + ...) to z^9 (truncation below 2^-56 of the result), Estrin
        double z2 = z * z, z4 = z2 * z2;
        double a0 = Math.FusedMultiplyAdd(z, -0.72134752044448170368, 1.4426950408889634074);
        double a1 = Math.FusedMultiplyAdd(z, -0.36067376022224085184, 0.48089834696298780245);
        double a2 = Math.FusedMultiplyAdd(z, -0.24044917348149390123, 0.28853900817779268147);
        double a3 = Math.FusedMultiplyAdd(z, -0.18033688011112042592, 0.20609929155556620105);
        double a4 = 0.16029944898766260082;
        double p = Math.FusedMultiplyAdd(z4, Math.FusedMultiplyAdd(z4, a4, Math.FusedMultiplyAdd(z2, a3, a2)), Math.FusedMultiplyAdd(z2, a1, a0));
        l = Math.FusedMultiplyAdd(z, p, Entry(Log2TableBits, ci + 1));
    }

    /// <summary><see cref="Log2Parts"/> to about 2^-34 (log2(1 + z) to z^5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Log2PartsFast(double x, out int e, out double l)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int ci = (int)((bits >> 46) & 63) * 3;
        e = (int)((bits >> 52) & 0x7FF) - 1023 + (int)Unsafe.Add(ref MemoryMarshal.GetReference(Log2TableBits), ci + 2);
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        double z = Math.FusedMultiplyAdd(m, Entry(Log2TableBits, ci), -1.0);
        double z2 = z * z;
        double a0 = Math.FusedMultiplyAdd(z, -0.72134752044448170368, 1.4426950408889634074);
        double a1 = Math.FusedMultiplyAdd(z, -0.36067376022224085184, 0.48089834696298780245);
        double p = Math.FusedMultiplyAdd(z2 * z2, 0.28853900817779268147, Math.FusedMultiplyAdd(z2, a1, a0));
        l = Math.FusedMultiplyAdd(z, p, Entry(Log2TableBits, ci + 1));
    }

    /// <summary>2^(th + tl) in double, th carrying few significant bits (an exact product such as y * e) or tl tiny;
    /// relative error ~2^-52.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Exp2(double th, double tl)
    {
        double t0 = th + tl;
        if (t0 > 160.0)
        {
            return double.PositiveInfinity;
        }
        if (t0 < -180.0)
        {
            return 0.0;
        }
        double kd = Math.Round(t0 * 64.0);
        int k = (int)kd;
        double r = (th - kd * (1.0 / 64.0)) + tl;   // th - k/64 is exact: th and k/64 both fit in far fewer than 53 bits
        int j = k & 63;
        // 2^r = e^(r ln2), |r| <= 1/128: Taylor to r^7 (truncation below 2^-60)
        double s = r * Ln2, s2 = s * s, s4 = s2 * s2;
        double b0 = Math.FusedMultiplyAdd(s, 1.0 / 6.0, 0.5), b1 = Math.FusedMultiplyAdd(s, 1.0 / 120.0, 1.0 / 24.0);
        double b2 = Math.FusedMultiplyAdd(s, 1.0 / 5040.0, 1.0 / 720.0);
        double em1 = Math.FusedMultiplyAdd(s2, Math.FusedMultiplyAdd(s4, b2, Math.FusedMultiplyAdd(s2, b1, b0)), s);
        double t = Entry(Exp2TableBits, j);
        return Math.FusedMultiplyAdd(t, em1, t) * BitConverter.Int64BitsToDouble((long)(((k - j) >> 6) + 1023) << 52);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Entry(ReadOnlySpan<ulong> table, int index) =>
        BitConverter.UInt64BitsToDouble(Unsafe.Add(ref MemoryMarshal.GetReference(table), index));

    // ---------------------------------------------------------------- tables (literal data: no static initialisation)

    /// <summary>Log2TableBits rows (Inv, Log2C, EAdj) from their definition: the 64 buckets of m in [1, 2) by its top 6
    /// bits, Inv = 1 / bucket centre rounded to 10 bits (1 and 1/2 at the ends), Log2C = log2(1 / Inv) or, from sqrt(2)
    /// up, log2(1 / (2 Inv)) with EAdj = 1 (so log2 of values just below 1 does not cancel against the exponent).</summary>
    internal static ulong[] ComputeLog2TableBits()
    {
        var bits = new ulong[64 * 3];
        for (int i = 0; i < 64; i++)
        {
            double centre = 1.0 + (i + 0.5) / 64.0;
            double inv = i == 0 ? 1.0 : i == 63 ? 0.5 : Math.Round(1024.0 / centre) / 1024.0;
            bool upper = centre > 1.4142135623730951;
            bits[i * 3] = BitConverter.DoubleToUInt64Bits(inv);
            bits[i * 3 + 1] = BitConverter.DoubleToUInt64Bits(0.0 - PortableMathD.Log2(upper ? 2.0 * inv : inv));
            bits[i * 3 + 2] = upper ? 1UL : 0UL;
        }
        return bits;
    }

    /// <summary>Exp2TableBits from its definition: 2^(j/64).</summary>
    internal static ulong[] ComputeExp2TableBits()
    {
        var bits = new ulong[64];
        for (int j = 0; j < 64; j++)
        {
            bits[j] = BitConverter.DoubleToUInt64Bits(PortableMathD.Pow(2.0, j / 64.0));
        }
        return bits;
    }

    internal static bool TablesMatchDefinition() =>
        Log2TableBits.SequenceEqual(ComputeLog2TableBits()) && Exp2TableBits.SequenceEqual(ComputeExp2TableBits());

    private static ReadOnlySpan<ulong> Log2TableBits =>
    [
        0x3FF0000000000000, 0x0000000000000000, 0,
        0x3FEF480000000000, 0x3FA0C7B844EF1795, 0,
        0x3FEED00000000000, 0x3FABEEC9151AAC2E, 0,
        0x3FEE580000000000, 0x3FB3A0CF56A06C4B, 0,
        0x3FEDE80000000000, 0x3FB8FDF1CA8EEA6A, 0,
        0x3FED780000000000, 0x3FBE6F50C2D9F754, 0,
        0x3FED100000000000, 0x3FC1C7E77DDE33DC, 0,
        0x3FECA80000000000, 0x3FC46163957AF02E, 0,
        0x3FEC400000000000, 0x3FC7046031C79F85, 0,
        0x3FEBE00000000000, 0x3FC97C1CB13C7EC1, 0,
        0x3FEB800000000000, 0x3FCBFC67A7FFF4CC, 0,
        0x3FEB200000000000, 0x3FCE857D3D361368, 0,
        0x3FEAC80000000000, 0x3FD070352293D724, 0,
        0x3FEA700000000000, 0x3FD1A190A5D674A0, 0,
        0x3FEA180000000000, 0x3FD2D6EB4152324F, 0,
        0x3FE9C00000000000, 0x3FD4106017C3ECA3, 0,
        0x3FE9700000000000, 0x3FD530FD08F29FA7, 0,
        0x3FE9200000000000, 0x3FD6552B49986277, 0,
        0x3FE8D00000000000, 0x3FD77D01B66FBD37, 0,
        0x3FE8880000000000, 0x3FD88A76B7E549C6, 0,
        0x3FE8400000000000, 0x3FD99B072A96C6B2, 0,
        0x3FE7F80000000000, 0x3FDAAEC59DADADBE, 0,
        0x3FE7B00000000000, 0x3FDBC5C5489254CC, 0,
        0x3FE7680000000000, 0x3FDCE01A12F5D8D1, 0,
        0x3FE7280000000000, 0x3FDDDDECF870C4C1, 0,
        0x3FE6E00000000000, 0x3FDEFEC61B011F85, 0,
        0x3FE6A00000000000, 0x3FE0014332BE0033, 0,
        0x3FE6600000000000, 0xBFDEF6D67328E220, 1,
        0x3FE6200000000000, 0xBFDDED3FD442364C, 1,
        0x3FE5E80000000000, 0xBFDD0262D554051C, 1,
        0x3FE5B00000000000, 0xBFDC152A6C24CAE6, 1,
        0x3FE5700000000000, 0xBFDB031BEFE06434, 1,
        0x3FE5380000000000, 0xBFDA10ACD0095AB4, 1,
        0x3FE5000000000000, 0xBFD91BBA891F1709, 1,
        0x3FE4C80000000000, 0xBFD82437A2EE70F7, 1,
        0x3FE4980000000000, 0xBFD74DFB70A66388, 1,
        0x3FE4600000000000, 0xBFD6518FE4677BA7, 1,
        0x3FE4300000000000, 0xBFD577091B3378CB, 1,
        0x3FE3F80000000000, 0xBFD475820E3A4251, 1,
        0x3FE3C80000000000, 0xBFD39683C4A9CE9A, 1,
        0x3FE3980000000000, 0xBFD2B565CB3313B6, 1,
        0x3FE3680000000000, 0xBFD1D21DAD295632, 1,
        0x3FE3380000000000, 0xBFD0ECA0A7E91E0B, 1,
        0x3FE3100000000000, 0xBFD02BABA24D0664, 1,
        0x3FE2E00000000000, 0xBFCE840BE74E6A4D, 1,
        0x3FE2B80000000000, 0xBFCCFB1321B8C400, 1,
        0x3FE2880000000000, 0xBFCB1F27D7BD7A80, 1,
        0x3FE2600000000000, 0xBFC98EDD077E70DF, 1,
        0x3FE2380000000000, 0xBFC7FB27199DF16D, 1,
        0x3FE2080000000000, 0xBFC6121AC74813CF, 1,
        0x3FE1E00000000000, 0xBFC476A9F983F74D, 1,
        0x3FE1B80000000000, 0xBFC2D79C6937EFDD, 1,
        0x3FE1980000000000, 0xBFC188ECBD1D16BE, 1,
        0x3FE1700000000000, 0xBFBFC66A0F0B00A5, 1,
        0x3FE1480000000000, 0xBFBC73632513BD4F, 1,
        0x3FE1200000000000, 0xBFB918A16E46335B, 1,
        0x3FE1000000000000, 0xBFB663F6FAC91316, 1,
        0x3FE0D80000000000, 0xBFB2FAEF55CCB372, 1,
        0x3FE0B80000000000, 0xBFB03AA8F8DC854C, 1,
        0x3FE0980000000000, 0xBFAAEA3316095F72, 1,
        0x3FE0700000000000, 0xBFA3ED3094685A26, 1,
        0x3FE0500000000000, 0xBF9C9363BA850F86, 1,
        0x3FE0300000000000, 0xBF91363117A97B0C, 1,
        0x3FE0000000000000, 0x0000000000000000, 1,
    ];

    private static ReadOnlySpan<ulong> Exp2TableBits =>
    [
        0x3FF0000000000000, 0x3FF02C9A3E778061, 0x3FF059B0D3158574, 0x3FF0874518759BC8,
        0x3FF0B5586CF9890F, 0x3FF0E3EC32D3D1A2, 0x3FF11301D0125B51, 0x3FF1429AAEA92DE0,
        0x3FF172B83C7D517B, 0x3FF1A35BEB6FCB75, 0x3FF1D4873168B9AA, 0x3FF2063B88628CD6,
        0x3FF2387A6E756238, 0x3FF26B4565E27CDD, 0x3FF29E9DF51FDEE1, 0x3FF2D285A6E4030B,
        0x3FF306FE0A31B715, 0x3FF33C08B26416FF, 0x3FF371A7373AA9CB, 0x3FF3A7DB34E59FF7,
        0x3FF3DEA64C123422, 0x3FF4160A21F72E2A, 0x3FF44E086061892D, 0x3FF486A2B5C13CD0,
        0x3FF4BFDAD5362A27, 0x3FF4F9B2769D2CA7, 0x3FF5342B569D4F82, 0x3FF56F4736B527DA,
        0x3FF5AB07DD485429, 0x3FF5E76F15AD2148, 0x3FF6247EB03A5585, 0x3FF6623882552225,
        0x3FF6A09E667F3BCD, 0x3FF6DFB23C651A2F, 0x3FF71F75E8EC5F74, 0x3FF75FEB564267C9,
        0x3FF7A11473EB0187, 0x3FF7E2F336CF4E62, 0x3FF82589994CCE13, 0x3FF868D99B4492ED,
        0x3FF8ACE5422AA0DB, 0x3FF8F1AE99157736, 0x3FF93737B0CDC5E5, 0x3FF97D829FDE4E50,
        0x3FF9C49182A3F090, 0x3FFA0C667B5DE565, 0x3FFA5503B23E255D, 0x3FFA9E6B5579FDBF,
        0x3FFAE89F995AD3AD, 0x3FFB33A2B84F15FB, 0x3FFB7F76F2FB5E47, 0x3FFBCC1E904BC1D2,
        0x3FFC199BDD85529C, 0x3FFC67F12E57D14B, 0x3FFCB720DCEF9069, 0x3FFD072D4A07897C,
        0x3FFD5818DCFBA487, 0x3FFDA9E603DB3285, 0x3FFDFC97337B9B5F, 0x3FFE502EE78B3FF6,
        0x3FFEA4AFA2A490DA, 0x3FFEFA1BEE615A27, 0x3FFF50765B6E4540, 0x3FFFA7C1819E90D8,
    ];
}
