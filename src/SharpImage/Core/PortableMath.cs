namespace SharpImage.Core;

/// <summary>
/// Float <c>pow</c> / <c>exp</c> / <c>log</c> / <c>log2</c> / <c>log10</c> evaluated with IEEE double basic arithmetic
/// only, so the result is bit-identical on every OS and CPU. <see cref="MathF.Pow(float, float)"/> and friends call the C
/// runtime (UCRT on Windows, glibc on Linux), and those differ in the last bit for roughly 1 input in 1000, which is
/// enough to change gain map fractions and tone-mapped pixels. Each function runs in double with a relative error near
/// 2^-50 and rounds to float once, so the result is the correctly rounded float except when the exact value lies within
/// that error of a rounding boundary (about 1 input in 2^26).
/// </summary>
internal static class PortableMath
{
    // ln 2 split so that k * Ln2Hi is exact for |k| < 2^20 (Ln2Hi has 32 trailing zero bits), as in fdlibm.
    private const double Ln2Hi = 6.93147180369123816490e-01;
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double InvLn2 = 1.44269504088896338700e+00;
    private const double InvLn10 = 4.34294481903251827651e-01;
    private const double Sqrt2 = 1.41421356237309504880;

    /// <summary>x^y with C99 powf special cases.</summary>
    public static float Pow(float x, float y)
    {
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
        float r = (float)ExpCore(y * LnCore(ax));
        return x < 0.0f && yIsOdd ? -r : r;
    }

    /// <summary>e^x.</summary>
    public static float Exp(float x)
    {
        if (float.IsNaN(x))
        {
            return float.NaN;
        }
        return (float)ExpCore(x);
    }

    /// <summary>Natural logarithm.</summary>
    public static float Log(float x)
    {
        if (!TryLogSpecial(x, out float special))
        {
            return special;
        }
        return (float)LnCore(x);
    }

    /// <summary>Base-2 logarithm (exact for powers of two).</summary>
    public static float Log2(float x)
    {
        if (!TryLogSpecial(x, out float special))
        {
            return special;
        }
        Split(x, out int e, out double m);
        return (float)(e + LnMantissa(m) * InvLn2);
    }

    /// <summary>Base-10 logarithm.</summary>
    public static float Log10(float x)
    {
        if (!TryLogSpecial(x, out float special))
        {
            return special;
        }
        return (float)(LnCore(x) * InvLn10);
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

    /// <summary>x = 2^e * m with m in [sqrt(1/2), sqrt(2)); x positive and finite (float subnormals included).</summary>
    private static void Split(double x, out int e, out double m)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);   // a float widened to double is always a normal double
        e = (int)((bits >> 52) & 0x7FF) - 1023;
        m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > Sqrt2)
        {
            m *= 0.5;
            e++;
        }
    }

    /// <summary>ln(m) for m in [sqrt(1/2), sqrt(2)]: 2 atanh(s), s = (m - 1) / (m + 1), |s| &lt; 0.1716.</summary>
    private static double LnMantissa(double m)
    {
        double s = (m - 1.0) / (m + 1.0);   // m - 1 is exact (Sterbenz)
        double z = s * s;
        // atanh(s) / s = sum z^k / (2k + 1); z < 0.0295, so 13 terms leave a truncation error below 2^-60.
        double p = 1.0 / 27.0;
        p = p * z + 1.0 / 25.0;
        p = p * z + 1.0 / 23.0;
        p = p * z + 1.0 / 21.0;
        p = p * z + 1.0 / 19.0;
        p = p * z + 1.0 / 17.0;
        p = p * z + 1.0 / 15.0;
        p = p * z + 1.0 / 13.0;
        p = p * z + 1.0 / 11.0;
        p = p * z + 1.0 / 9.0;
        p = p * z + 1.0 / 7.0;
        p = p * z + 1.0 / 5.0;
        p = p * z + 1.0 / 3.0;
        return 2.0 * s + 2.0 * s * (z * p);
    }

    /// <summary>ln(x) for positive finite x.</summary>
    private static double LnCore(double x)
    {
        Split(x, out int e, out double m);
        return e * Ln2Hi + (LnMantissa(m) + e * Ln2Lo);
    }

    /// <summary>e^t in double; saturates well outside the float range.</summary>
    private static double ExpCore(double t)
    {
        if (t > 128.0)
        {
            return double.PositiveInfinity;
        }
        if (t < -128.0)
        {
            return 0.0;
        }
        double k = Math.Round(t * InvLn2);
        double r = (t - k * Ln2Hi) - k * Ln2Lo;   // |r| <= ln2 / 2; k * Ln2Hi is exact
        // e^r by its Taylor series to r^13 / 13! (truncation below 2^-60 for |r| <= 0.347).
        double p = 1.0 / 6227020800.0;
        p = p * r + 1.0 / 479001600.0;
        p = p * r + 1.0 / 39916800.0;
        p = p * r + 1.0 / 3628800.0;
        p = p * r + 1.0 / 362880.0;
        p = p * r + 1.0 / 40320.0;
        p = p * r + 1.0 / 5040.0;
        p = p * r + 1.0 / 720.0;
        p = p * r + 1.0 / 120.0;
        p = p * r + 1.0 / 24.0;
        p = p * r + 1.0 / 6.0;
        p = p * r + 0.5;
        double er = 1.0 + (r + r * r * p);
        return er * BitConverter.Int64BitsToDouble((long)((int)k + 1023) << 52);
    }
}
