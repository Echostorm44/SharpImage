// libavif's colour math (colr.c / colrconvert.c), ported operation-for-operation in float/double so gain map tone
// mapping matches libavif: CICP colour primaries, the H.273 transfer functions as libavif implements them (PQ and HLG
// scaled so SDR white = 1.0, "extended SDR"), and linear RGB -> RGB conversion through XYZ D50 (Bradford adaptation).
using SharpImage.Core;

namespace SharpImage.Formats;

internal static class AvifColorMath
{
    // rX, rY, gX, gY, bX, bY, wX, wY per CICP colour primaries; unknown values fall back to BT.709.
    private static readonly (int Cp, float[] P)[] Primaries =
    [
        (1, [0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.329f]),
        (4, [0.67f, 0.33f, 0.21f, 0.71f, 0.14f, 0.08f, 0.310f, 0.316f]),
        (5, [0.64f, 0.33f, 0.29f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f]),
        (6, [0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f, 0.3127f, 0.3290f]),
        (7, [0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f, 0.3127f, 0.3290f]),
        (8, [0.681f, 0.319f, 0.243f, 0.692f, 0.145f, 0.049f, 0.310f, 0.316f]),
        (9, [0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.3290f]),
        (10, [1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.3333f, 0.3333f]),
        (11, [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.314f, 0.351f]),
        (12, [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.3127f, 0.3290f]),
        (22, [0.630f, 0.340f, 0.295f, 0.605f, 0.155f, 0.077f, 0.3127f, 0.3290f]),
    ];

    public static float[] PrimariesValues(int cp)
    {
        foreach (var (c, p) in Primaries) if (c == cp) return p;
        return Primaries[0].P;
    }

    /// <summary>avifColorPrimariesComputeYCoeffs: luma coefficients (kr, kg, kb) of the primaries (H.273 eq. 32-37).</summary>
    public static float[] YCoeffs(int cp)
    {
        float[] p = PrimariesValues(cp);
        float rX = p[0], rY = p[1], gX = p[2], gY = p[3], bX = p[4], bY = p[5], wX = p[6], wY = p[7];
        float rZ = 1.0f - (rX + rY), gZ = 1.0f - (gX + gY), bZ = 1.0f - (bX + bY), wZ = 1.0f - (wX + wY);
        float kr = (rY * (wX * (gY * bZ - bY * gZ) + wY * (bX * gZ - gX * bZ) + wZ * (gX * bY - bX * gY))) /
                   (wY * (rX * (gY * bZ - bY * gZ) + gX * (bY * rZ - rY * bZ) + bX * (rY * gZ - gY * rZ)));
        float kb = (bY * (wX * (rY * gZ - gY * rZ) + wY * (gX * rZ - rX * gZ) + wZ * (rX * gY - gX * rY))) /
                   (wY * (rX * (gY * bZ - bY * gZ) + gX * (bY * rZ - rY * bZ) + bX * (rY * gZ - gY * rZ)));
        return [kr, 1.0f - kr - kb, kb];
    }

    // AVIF_CLAMP: NaN passes through.
    private static float Clamp(float x, float lo, float hi) => x < lo ? lo : (hi < x ? hi : x);

    public const float SdrWhiteNits = 203.0f;
    private const float PqMaxNits = 10000.0f, HlgPeakNits = 1000.0f;
    private const float FltMin = 1.17549435E-38f;

    public static Func<float, float> ToLinear(int tc) => tc switch
    {
        1 or 6 or 14 or 15 => ToLinear709,
        4 => g => PortableMath.Pow(Clamp(g, 0.0f, 1.0f), 2.2f),
        5 => g => PortableMath.Pow(Clamp(g, 0.0f, 1.0f), 2.8f),
        7 => ToLinearSmpte240,
        8 => GammaLinear,
        9 => g => g <= 0.0f ? 0.01f / 2.0f : PortableMath.Pow(10.0f, 2.0f * (MathF.Min(g, 1.0f) - 1.0f)),
        10 => g => g <= 0.0f ? 0.00316227766f / 2.0f : PortableMath.Pow(10.0f, 2.5f * (MathF.Min(g, 1.0f) - 1.0f)),
        11 => ToLinearIec61966,
        12 => ToLinearBt1361,
        13 => ToLinearSrgb,
        16 => ToLinearPq,
        17 => g => PortableMath.Pow(MathF.Max(g, 0.0f), 2.6f) / 0.91655527974030934f,
        18 => ToLinearHlg,
        _ => ToLinear709,
    };

    public static Func<float, float> ToGamma(int tc) => tc switch
    {
        1 or 6 or 14 or 15 => ToGamma709,
        4 => l => PortableMath.Pow(Clamp(l, 0.0f, 1.0f), 1.0f / 2.2f),
        5 => l => PortableMath.Pow(Clamp(l, 0.0f, 1.0f), 1.0f / 2.8f),
        7 => ToGammaSmpte240,
        8 => GammaLinear,
        9 => l => l <= 0.01f ? 0.0f : 1.0f + PortableMath.Log10(MathF.Min(l, 1.0f)) / 2.0f,
        10 => l => l <= 0.00316227766f ? 0.0f : 1.0f + PortableMath.Log10(MathF.Min(l, 1.0f)) / 2.5f,
        11 => ToGammaIec61966,
        12 => ToGammaBt1361,
        13 => ToGammaSrgb,
        16 => ToGammaPq,
        17 => l => PortableMath.Pow(0.91655527974030934f * MathF.Max(l, 0.0f), 1.0f / 2.6f),
        18 => ToGammaHlg,
        _ => ToGamma709,
    };

    private static float GammaLinear(float g) => Clamp(g, 0.0f, 1.0f);

    private static float ToLinear709(float g)
    {
        if (g < 0.0f) return 0.0f;
        if (g < 4.5f * 0.018053968510807f) return g / 4.5f;
        if (g < 1.0f) return PortableMath.Pow((g + 0.09929682680944f) / 1.09929682680944f, 1.0f / 0.45f);
        return 1.0f;
    }

    private static float ToGamma709(float l)
    {
        if (l < 0.0f) return 0.0f;
        if (l < 0.018053968510807f) return l * 4.5f;
        if (l < 1.0f) return 1.09929682680944f * PortableMath.Pow(l, 0.45f) - 0.09929682680944f;
        return 1.0f;
    }

    private static float ToLinearSmpte240(float g)
    {
        if (g < 0.0f) return 0.0f;
        if (g < 4.0f * 0.022821585529445f) return g / 4.0f;
        if (g < 1.0f) return PortableMath.Pow((g + 0.111572195921731f) / 1.111572195921731f, 1.0f / 0.45f);
        return 1.0f;
    }

    private static float ToGammaSmpte240(float l)
    {
        if (l < 0.0f) return 0.0f;
        if (l < 0.022821585529445f) return l * 4.0f;
        if (l < 1.0f) return 1.111572195921731f * PortableMath.Pow(l, 0.45f) - 0.111572195921731f;
        return 1.0f;
    }

    private static float ToLinearIec61966(float g)
    {
        if (g < -4.5f * 0.018053968510807f) return -PortableMath.Pow((g - 0.09929682680944f) / -1.09929682680944f, 1.0f / 0.45f);
        if (g < 4.5f * 0.018053968510807f) return g / 4.5f;
        return PortableMath.Pow((g + 0.09929682680944f) / 1.09929682680944f, 1.0f / 0.45f);
    }

    private static float ToGammaIec61966(float l)
    {
        if (l < -0.018053968510807f) return -1.09929682680944f * PortableMath.Pow(-l, 0.45f) + 0.09929682680944f;
        if (l < 0.018053968510807f) return l * 4.5f;
        return 1.09929682680944f * PortableMath.Pow(l, 0.45f) - 0.09929682680944f;
    }

    private static float ToLinearBt1361(float g)
    {
        if (g < -0.25f) return -0.25f;
        if (g < 0.0f) return PortableMath.Pow((g - 0.02482420670236f) / -0.27482420670236f, 1.0f / 0.45f) / -4.0f;
        if (g < 4.5f * 0.018053968510807f) return g / 4.5f;
        if (g < 1.0f) return PortableMath.Pow((g + 0.09929682680944f) / 1.09929682680944f, 1.0f / 0.45f);
        return 1.0f;
    }

    private static float ToGammaBt1361(float l)
    {
        if (l < -0.25f) return -0.25f;
        if (l < 0.0f) return -0.27482420670236f * PortableMath.Pow(-4.0f * l, 0.45f) + 0.02482420670236f;
        if (l < 0.018053968510807f) return l * 4.5f;
        if (l < 1.0f) return 1.09929682680944f * PortableMath.Pow(l, 0.45f) - 0.09929682680944f;
        return 1.0f;
    }

    private static float ToLinearSrgb(float g)
    {
        if (g < 0.0f) return 0.0f;
        if (g < 12.92f * 0.0030412825601275209f) return g / 12.92f;
        if (g < 1.0f) return PortableMath.Pow((g + 0.0550107189475866f) / 1.0550107189475866f, 2.4f);
        return 1.0f;
    }

    private static float ToGammaSrgb(float l)
    {
        if (l < 0.0f) return 0.0f;
        if (l < 0.0030412825601275209f) return l * 12.92f;
        if (l < 1.0f) return 1.0550107189475866f * PortableMath.Pow(l, 1.0f / 2.4f) - 0.0550107189475866f;
        return 1.0f;
    }

    private static float ToLinearPq(float g)
    {
        if (!(g > 0.0f)) return 0.0f;
        float powGamma = PortableMath.Pow(g, 1.0f / 78.84375f);
        float num = MathF.Max(powGamma - 0.8359375f, 0.0f);
        float den = MathF.Max(18.8515625f - 18.6875f * powGamma, FltMin);
        float linear = PortableMath.Pow(num / den, 1.0f / 0.1593017578125f);
        return linear * PqMaxNits / SdrWhiteNits;
    }

    private static float ToGammaPq(float l)
    {
        if (!(l > 0.0f)) return 0.0f;
        l = Clamp(l * SdrWhiteNits / PqMaxNits, 0.0f, 1.0f);
        float powLinear = PortableMath.Pow(l, 0.1593017578125f);
        float num = 0.1640625f * powLinear - 0.1640625f;
        float den = 1.0f + 18.6875f * powLinear;
        return PortableMath.Pow(1.0f + num / den, 78.84375f);
    }

    private static float ToLinearHlg(float g)
    {
        if (g < 0.0f) return 0.0f;
        float linear = g <= 0.5f
            ? PortableMath.Pow((g * g) * (1.0f / 3.0f), 1.2f)
            : PortableMath.Pow((PortableMath.Exp((g - 0.55991073f) / 0.17883277f) + 0.28466892f) / 12.0f, 1.2f);
        return linear * HlgPeakNits / SdrWhiteNits;
    }

    private static float ToGammaHlg(float l)
    {
        l = Clamp(l * SdrWhiteNits / HlgPeakNits, 0.0f, 1.0f);
        l = PortableMath.Pow(l, 1.0f / 1.2f);
        if (l < 0.0f) return 0.0f;
        if (l <= 1.0f / 12.0f) return MathF.Sqrt(3.0f * l);
        return 0.17883277f * PortableMath.Log(12.0f * l - 0.28466892f) + 0.55991073f;
    }

    // ---- RGB -> RGB through XYZ D50 (avifColorPrimariesComputeRGBToRGBMatrix) --------------------------------------

    private static readonly double[,] Bradford = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };
    private static readonly double[] LmsD50 = [0.996284, 1.02043, 0.818644];
    private const double Epsilon = 1e-12;

    private static bool XyToXyz(float x, float y, double[] xyz)
    {
        if (MathF.Abs(y) < Epsilon) return false;
        double factor = 1.0 / y;
        xyz[0] = x * factor;
        xyz[1] = 1;
        xyz[2] = (1 - x - y) * factor;
        return true;
    }

    private static bool MatInv(double[,] m, double[,] r)
    {
        double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[2, 1] * m[1, 2]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                     m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        if (Math.Abs(det) < Epsilon) return false;
        det = 1.0 / det;
        r[0, 0] = (m[1, 1] * m[2, 2] - m[2, 1] * m[1, 2]) * det;
        r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) * det;
        r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) * det;
        r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) * det;
        r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) * det;
        r[1, 2] = (m[1, 0] * m[0, 2] - m[0, 0] * m[1, 2]) * det;
        r[2, 0] = (m[1, 0] * m[2, 1] - m[2, 0] * m[1, 1]) * det;
        r[2, 1] = (m[2, 0] * m[0, 1] - m[0, 0] * m[2, 1]) * det;
        r[2, 2] = (m[0, 0] * m[1, 1] - m[1, 0] * m[0, 1]) * det;
        return true;
    }

    private static double[,] MatMul(double[,] a, double[,] b)
    {
        var c = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return c;
    }

    private static double[] VecMul(double[,] m, double[] x) =>
        [m[0, 0] * x[0] + m[0, 1] * x[1] + m[0, 2] * x[2],
         m[1, 0] * x[0] + m[1, 1] * x[1] + m[1, 2] * x[2],
         m[2, 0] * x[0] + m[2, 1] * x[1] + m[2, 2] * x[2]];

    private static double[,] Diag(double[] d) => new double[,] { { d[0], 0, 0 }, { 0, d[1], 0 }, { 0, 0, d[2] } };

    private static double[,]? RgbToXyzD50(int cp)
    {
        float[] p = PrimariesValues(cp);
        var white = new double[3];
        if (!XyToXyz(p[6], p[7], white)) return null;
        var rgbPrimaries = new double[,]
        {
            { p[0], p[2], p[4] },
            { p[1], p[3], p[5] },
            { 1.0 - p[0] - p[1], 1.0 - p[2] - p[3], 1.0 - p[4] - p[5] },
        };
        var inv = new double[3, 3];
        if (!MatInv(rgbPrimaries, inv)) return null;
        double[] rgbCoefficients = VecMul(inv, white);
        double[,] rgbXyz = MatMul(rgbPrimaries, Diag(rgbCoefficients));
        double[] lms = VecMul(Bradford, white);
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(lms[i]) < Epsilon) return null;
            lms[i] = LmsD50[i] / lms[i];
        }
        double[,] tmp = MatMul(Diag(lms), Bradford);
        var bradfordInv = new double[3, 3];
        if (!MatInv(Bradford, bradfordInv)) return null;
        double[,] adaptation = MatMul(bradfordInv, tmp);
        return MatMul(adaptation, rgbXyz);
    }

    /// <summary>Linear RGB (src primaries) -> linear RGB (dst primaries), or null when not computable.</summary>
    public static double[,]? RgbToRgbMatrix(int srcCp, int dstCp)
    {
        var srcToXyz = RgbToXyzD50(srcCp);
        var dstToXyz = RgbToXyzD50(dstCp);
        if (srcToXyz == null || dstToXyz == null) return null;
        var xyzToDst = new double[3, 3];
        if (!MatInv(dstToXyz, xyzToDst)) return null;
        return MatMul(xyzToDst, srcToXyz);
    }

    /// <summary>avifLinearRGBConvertColorSpace: converts in double, stores back as float.</summary>
    public static void Convert(Span<float> rgb, double[,] m)
    {
        double r = rgb[0], g = rgb[1], b = rgb[2];
        rgb[0] = (float)(m[0, 0] * r + m[0, 1] * g + m[0, 2] * b);
        rgb[1] = (float)(m[1, 0] * r + m[1, 1] * g + m[1, 2] * b);
        rgb[2] = (float)(m[2, 0] * r + m[2, 1] * g + m[2, 2] * b);
    }
}
