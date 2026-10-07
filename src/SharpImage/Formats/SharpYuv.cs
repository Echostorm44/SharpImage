// libsharpyuv (libwebp/sharpyuv, BSD): "sharp" RGB -> YUV 4:2:0, as avifenc --sharpyuv uses it. Instead of averaging
// each 2x2 block's chroma, it iteratively refines Y and the half-resolution chroma in linear light so the bilinearly
// upsampled result reproduces the source — much crisper colour edges. Ported operation for operation (fixed-point
// tables for sRGB, the float transfer functions of sharpyuv_gamma.c for other H.273 curves) so the planes equal
// libsharpyuv's; its SSE2 kernels are bit-exact with the C ones ported here.
using SharpImage.Core;

namespace SharpImage.Formats;

internal static class SharpYuv
{
    private const int NumIterations = 4;
    private const int YuvFix = 16;
    private const int YuvHalf = 1 << (YuvFix - 1);
    private const int MaxBitDepth = 14;

    private static int GetPrecisionShift(int rgbBitDepth) => rgbBitDepth + 2 <= MaxBitDepth ? 2 : MaxBitDepth - rgbBitDepth;

    // ---- gamma (sharpyuv_gamma.c) --------------------------------------------------------------------------------

    private const int GammaToLinearTabBits = 10, GammaToLinearTabSize = 1 << GammaToLinearTabBits;
    private const int LinearToGammaTabBits = 9, LinearToGammaTabSize = 1 << LinearToGammaTabBits;
    private const int GammaToLinearBits = 16;
    private static readonly uint[] GammaToLinearTab = new uint[GammaToLinearTabSize + 2];
    private static readonly uint[] LinearToGammaTab = new uint[LinearToGammaTabSize + 2];

    static SharpYuv()
    {
        const double kGammaF = 1.0 / 0.45, a = 0.09929682680944, thresh = 0.018053968510807;
        const double finalScale = 1 << GammaToLinearBits;
        double norm = 1.0 / GammaToLinearTabSize, aRec = 1.0 / (1.0 + a);
        for (int v = 0; v <= GammaToLinearTabSize; v++)
        {
            double g = norm * v;
            double value = g <= thresh * 4.5 ? g / 4.5 : PortableMathD.Pow(aRec * (g + a), kGammaF);
            GammaToLinearTab[v] = (uint)(value * finalScale + 0.5);
        }
        GammaToLinearTab[GammaToLinearTabSize + 1] = GammaToLinearTab[GammaToLinearTabSize];
        double scale = 1.0 / LinearToGammaTabSize;
        for (int v = 0; v <= LinearToGammaTabSize; v++)
        {
            double g = scale * v;
            double value = g <= thresh ? 4.5 * g : (1.0 + a) * PortableMathD.Pow(g, 1.0 / kGammaF) - a;
            LinearToGammaTab[v] = (uint)(finalScale * value + 0.5);
        }
        LinearToGammaTab[LinearToGammaTabSize + 1] = LinearToGammaTab[LinearToGammaTabSize];
    }

    private static int Shift(int v, int shift) => shift >= 0 ? v << shift : v >> -shift;

    private static uint FixedPointInterpolation(int v, uint[] tab, int tabPosShiftRight, int tabValueShift)
    {
        uint tabPos = (uint)Shift(v, -tabPosShiftRight);
        uint x = (uint)v - (tabPos << tabPosShiftRight);
        uint v0 = (uint)Shift((int)tab[tabPos], tabValueShift);
        uint v1 = (uint)Shift((int)tab[tabPos + 1], tabValueShift);
        uint v2 = (v1 - v0) * x;
        int half = tabPosShiftRight > 0 ? 1 << (tabPosShiftRight - 1) : 0;
        return v0 + (uint)((v2 + (uint)half) >> tabPosShiftRight);
    }

    private static float Clamp01(float x) => x < 0f ? 0f : (1f < x ? 1f : x);
    private static float Roundf(float x) => x < 0 ? (float)Math.Ceiling((double)(x - 0.5f)) : (float)Math.Floor((double)(x + 0.5f));
    private static float Powf(float b, float e) => (float)PortableMathD.Pow(b, e);
    private static float Log10f(float x) => (float)PortableMathD.Log10(x);

    private static float ToLinear(float g, int tc) => tc switch
    {
        1 or 6 or 14 or 15 => g < 0f ? 0f : g < 4.5f * 0.018053968510807f ? g / 4.5f
            : g < 1f ? Powf((g + 0.09929682680944f) / 1.09929682680944f, 1f / 0.45f) : 1f,
        4 => Powf(Clamp01(g), 2.2f),
        5 => Powf(Clamp01(g), 2.8f),
        7 => g < 0f ? 0f : g < 4f * 0.022821585529445f ? g / 4f
            : g < 1f ? Powf((g + 0.111572195921731f) / 1.111572195921731f, 1f / 0.45f) : 1f,
        9 => g <= 0.0f ? 0.01f / 2f : Powf(10.0f, 2f * (MathF.Min(g, 1f) - 1.0f)),
        10 => g <= 0.0f ? 0.00316227766f / 2f : Powf(10.0f, 2.5f * (MathF.Min(g, 1f) - 1.0f)),
        11 => g <= -4.5f * 0.018053968510807f ? Powf((-g + 0.09929682680944f) / -1.09929682680944f, 1f / 0.45f)
            : g < 4.5f * 0.018053968510807f ? g / 4.5f : Powf((g + 0.09929682680944f) / 1.09929682680944f, 1f / 0.45f),
        12 => g < -0.25f ? -0.25f : g < 0f ? Powf((g - 0.02482420670236f) / -0.27482420670236f, 1f / 0.45f) / -4f
            : g < 4.5f * 0.018053968510807f ? g / 4.5f : g < 1f ? Powf((g + 0.09929682680944f) / 1.09929682680944f, 1f / 0.45f) : 1f,
        16 => ToLinearPq(g),
        17 => Powf(MathF.Max(g, 0f), 2.6f) / 0.91655527974030934f,
        18 => g < 0f ? 0f : g <= 0.5f ? Powf((g * g) * (1f / 3f), 1.2f)
            : Powf((PortableMath.Exp((g - 0.55991073f) / 0.17883277f) + 0.28466892f) / 12.0f, 1.2f),
        _ => 0f,
    };

    private static float FromLinear(float l, int tc) => tc switch
    {
        1 or 6 or 14 or 15 => l < 0f ? 0f : l < 0.018053968510807f ? l * 4.5f
            : l < 1f ? 1.09929682680944f * Powf(l, 0.45f) - 0.09929682680944f : 1f,
        4 => Powf(Clamp01(l), 1f / 2.2f),
        5 => Powf(Clamp01(l), 1f / 2.8f),
        7 => l < 0f ? 0f : l < 0.022821585529445f ? l * 4f
            : l < 1f ? 1.111572195921731f * Powf(l, 0.45f) - 0.111572195921731f : 1f,
        9 => l < 0.01f ? 0.0f : 1.0f + Log10f(MathF.Min(l, 1f)) / 2.0f,
        10 => l < 0.00316227766f ? 0.0f : 1.0f + Log10f(MathF.Min(l, 1f)) / 2.5f,
        11 => l <= -0.018053968510807f ? -1.09929682680944f * Powf(-l, 0.45f) + 0.09929682680944f
            : l < 0.018053968510807f ? l * 4.5f : 1.09929682680944f * Powf(l, 0.45f) - 0.09929682680944f,
        12 => l < -0.25f ? -0.25f : l < 0f ? -0.27482420670236f * Powf(-4f * l, 0.45f) + 0.02482420670236f
            : l < 0.018053968510807f ? l * 4.5f : l < 1f ? 1.09929682680944f * Powf(l, 0.45f) - 0.09929682680944f : 1f,
        16 => FromLinearPq(l),
        17 => Powf(0.91655527974030934f * MathF.Max(l, 0f), 1f / 2.6f),
        18 => FromLinearHlg(l),
        _ => 0f,
    };

    private static float ToLinearPq(float g)
    {
        if (!(g > 0f)) return 0f;
        float powGamma = Powf(g, 32f / 2523f);
        float num = MathF.Max(powGamma - 107f / 128f, 0.0f);
        float den = MathF.Max(2413f / 128f - 2392f / 128f * powGamma, 1.17549435E-38f);
        return Powf(num / den, 4096f / 653f);
    }

    private static float FromLinearPq(float l)
    {
        if (!(l > 0f)) return 0f;
        float powLinear = Powf(l, 653f / 4096f);
        float num = 107f / 128f + 2413f / 128f * powLinear;
        float den = 1.0f + 2392f / 128f * powLinear;
        return Powf(num / den, 2523f / 32f);
    }

    private static float FromLinearHlg(float l)
    {
        l = Powf(l, 1f / 1.2f);
        if (l < 0f) return 0f;
        if (l <= 1f / 12f) return MathF.Sqrt(3f * l);
        return 0.17883277f * PortableMath.Log(12f * l - 0.28466892f) + 0.55991073f;
    }

    // SharpYuvGammaToLinear / SharpYuvLinearToGamma (13 = sRGB tables; 8 = linear returns the sample as is).
    private static uint GammaToLinear(ushort v, int bitDepth, int tc)
    {
        if (tc == 13)
        {
            int shift = GammaToLinearTabBits - bitDepth;
            return shift > 0 ? GammaToLinearTab[v << shift] : FixedPointInterpolation(v, GammaToLinearTab, -shift, 0);
        }
        if (tc == 8) return v;
        float vf = (float)v / ((1 << bitDepth) - 1);
        return (uint)Roundf(ToLinear(vf, tc) * ((1 << 16) - 1));
    }

    private static ushort LinearToGamma(uint value, int bitDepth, int tc)
    {
        if (tc == 13)
            return (ushort)FixedPointInterpolation((int)value, LinearToGammaTab, GammaToLinearBits - LinearToGammaTabBits, bitDepth - GammaToLinearBits);
        if (tc == 8) return (ushort)value;
        float vf = (float)value / ((1 << 16) - 1);
        return (ushort)Roundf(FromLinear(vf, tc) * ((1 << bitDepth) - 1));
    }

    // ---- conversion (sharpyuv.c) ---------------------------------------------------------------------------------

    private static int RgbToGray(long r, long g, long b) => (int)((13933 * r + 46871 * g + 4732 * b + YuvHalf) >> YuvFix);

    private static uint ScaleDown(ushort a, ushort b, ushort c, ushort d, int rgbBitDepth, int tc)
    {
        int bitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        uint A = GammaToLinear(a, bitDepth, tc), B = GammaToLinear(b, bitDepth, tc);
        uint C = GammaToLinear(c, bitDepth, tc), D = GammaToLinear(d, bitDepth, tc);
        return LinearToGamma((A + B + C + D + 2) >> 2, bitDepth, tc);
    }

    private static void UpdateW(ReadOnlySpan<ushort> src, Span<ushort> dst, int w, int rgbBitDepth, int tc)
    {
        int bitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        for (int i = 0; i < w; i++)
        {
            uint R = GammaToLinear(src[0 * w + i], bitDepth, tc);
            uint G = GammaToLinear(src[1 * w + i], bitDepth, tc);
            uint B = GammaToLinear(src[2 * w + i], bitDepth, tc);
            uint Y = (uint)RgbToGray(R, G, B);
            dst[i] = LinearToGamma(Y, bitDepth, tc);
        }
    }

    private static void UpdateChroma(ReadOnlySpan<ushort> src1, ReadOnlySpan<ushort> src2, Span<short> dst, int uvW, int rgbBitDepth, int tc)
    {
        for (int i = 0; i < uvW; i++)
        {
            int o = 2 * i;
            int r = (int)ScaleDown(src1[0 * uvW + o], src1[0 * uvW + o + 1], src2[0 * uvW + o], src2[0 * uvW + o + 1], rgbBitDepth, tc);
            int g = (int)ScaleDown(src1[2 * uvW + o], src1[2 * uvW + o + 1], src2[2 * uvW + o], src2[2 * uvW + o + 1], rgbBitDepth, tc);
            int b = (int)ScaleDown(src1[4 * uvW + o], src1[4 * uvW + o + 1], src2[4 * uvW + o], src2[4 * uvW + o + 1], rgbBitDepth, tc);
            int W = RgbToGray(r, g, b);
            dst[0 * uvW + i] = (short)(r - W);
            dst[1 * uvW + i] = (short)(g - W);
            dst[2 * uvW + i] = (short)(b - W);
        }
    }

    private static void StoreGray(ReadOnlySpan<ushort> rgb, Span<ushort> y, int w)
    {
        for (int i = 0; i < w; i++) y[i] = (ushort)RgbToGray(rgb[0 * w + i], rgb[1 * w + i], rgb[2 * w + i]);
    }

    private static ushort ClipBitDepth(int y, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        return (y & ~max) == 0 ? (ushort)y : y < 0 ? (ushort)0 : (ushort)max;
    }

    private static ushort Filter2(int A, int B, int W0, int bitDepth) => ClipBitDepth(((A * 3 + B + 2) >> 2) + W0, bitDepth);

    private static ushort Clip(int v, int max) => v < 0 ? (ushort)0 : v > max ? (ushort)max : (ushort)v;

    private static void FilterRow(ReadOnlySpan<short> A, ReadOnlySpan<short> B, int len, ReadOnlySpan<ushort> bestY, Span<ushort> output, int bitDepth)
    {
        int maxY = (1 << bitDepth) - 1;
        for (int i = 0; i < len; i++)
        {
            int v0 = (A[i] * 9 + A[i + 1] * 3 + B[i] * 3 + B[i + 1] + 8) >> 4;
            int v1 = (A[i + 1] * 9 + A[i] * 3 + B[i + 1] * 3 + B[i] + 8) >> 4;
            output[2 * i] = Clip(bestY[2 * i] + v0, maxY);
            output[2 * i + 1] = Clip(bestY[2 * i + 1] + v1, maxY);
        }
    }

    private static void InterpolateTwoRows(ReadOnlySpan<ushort> bestY, ReadOnlySpan<short> prevUv, ReadOnlySpan<short> curUv,
        ReadOnlySpan<short> nextUv, int w, Span<ushort> out1, Span<ushort> out2, int rgbBitDepth)
    {
        int uvW = w >> 1, len = (w - 1) >> 1;
        int bitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        for (int k = 0; k < 3; k++)
        {
            var cur = curUv.Slice(k * uvW);
            var prev = prevUv.Slice(k * uvW);
            var next = nextUv.Slice(k * uvW);
            var o1 = out1.Slice(k * w);
            var o2 = out2.Slice(k * w);
            o1[0] = Filter2(cur[0], prev[0], bestY[0], bitDepth);
            o2[0] = Filter2(cur[0], next[0], bestY[w], bitDepth);
            FilterRow(cur, prev, len, bestY.Slice(1), o1.Slice(1), bitDepth);
            FilterRow(cur, next, len, bestY.Slice(w + 1), o2.Slice(1), bitDepth);
            if ((w & 1) == 0)
            {
                o1[w - 1] = Filter2(cur[uvW - 1], prev[uvW - 1], bestY[w - 1], bitDepth);
                o2[w - 1] = Filter2(cur[uvW - 1], next[uvW - 1], bestY[w - 1 + w], bitDepth);
            }
        }
    }

    private static int RgbToYuvComponent(int r, int g, int b, int[] coeffs, int sfix)
    {
        int srounder = 1 << (YuvFix + sfix - 1);
        int luma = coeffs[0] * r + coeffs[1] * g + coeffs[2] * b + coeffs[3] + srounder;
        return luma >> (YuvFix + sfix);
    }

    private static int ToFixed16(float f) => (int)Math.Floor((double)(f * (1 << 16) + 0.5f));

    /// <summary>
    /// SharpYuvComputeConversionMatrix + SharpYuvConvertWithOptions: RGB samples (row-major, <paramref name="rgbBitDepth"/>
    /// 8/10/12/16) to 4:2:0 planes at <paramref name="yuvBitDepth"/> with the kr/kb matrix, range and H.273 transfer
    /// (2 = unspecified is treated as sRGB, as libavif does).
    /// </summary>
    public static void Convert(ushort[] r, ushort[] g, ushort[] b, int width, int height, int rgbBitDepth,
        int yuvBitDepth, float kr, float kb, bool fullRange, int transfer, out ushort[] yOut, out ushort[] uOut, out ushort[] vOut)
    {
        if (transfer == 2) transfer = 13;
        // SharpYuvComputeConversionMatrix.
        float kg = 1.0f - kr - kb, cb = 0.5f / (1.0f - kb), cr = 0.5f / (1.0f - kr);
        int cshift = yuvBitDepth - 8;
        float denom = (float)((1 << yuvBitDepth) - 1);
        float scaleY = 1.0f, addY = 0.0f, scaleU = cb, scaleV = cr, addUv = (float)(128 << cshift);
        if (!fullRange)
        {
            scaleY *= (219 << cshift) / denom;
            scaleU *= (224 << cshift) / denom;
            scaleV *= (224 << cshift) / denom;
            addY = (float)(16 << cshift);
        }
        int[] toY = [ToFixed16(kr * scaleY), ToFixed16(kg * scaleY), ToFixed16(kb * scaleY), ToFixed16(addY)];
        int[] toU = [ToFixed16(-kr * scaleU), ToFixed16(-kg * scaleU), ToFixed16((1 - kb) * scaleU), ToFixed16(addUv)];
        int[] toV = [ToFixed16((1 - kr) * scaleV), ToFixed16(-kg * scaleV), ToFixed16(-kb * scaleV), ToFixed16(addUv)];

        // SharpYuvConvertWithOptions: rescale the matrix to the RGB depth.
        int rgbMax = (1 << rgbBitDepth) - 1, rgbRound = 1 << (rgbBitDepth - 1), yuvMax = (1 << yuvBitDepth) - 1;
        int sfix = GetPrecisionShift(rgbBitDepth);
        if (rgbBitDepth != yuvBitDepth)
            for (int i = 0; i < 3; i++)
            {
                toY[i] = (toY[i] * yuvMax + rgbRound) / rgbMax;
                toU[i] = (toU[i] * yuvMax + rgbRound) / rgbMax;
                toV[i] = (toV[i] * yuvMax + rgbRound) / rgbMax;
            }
        toY[3] = Shift(toY[3], sfix);
        toU[3] = Shift(toU[3], sfix);
        toV[3] = Shift(toV[3], sfix);

        // DoSharpArgbToYuv.
        int w = (width + 1) & ~1, h = (height + 1) & ~1, uvW = w >> 1, uvH = h >> 1;
        int yBitDepth = rgbBitDepth + sfix;
        ulong prevDiffYSum = ulong.MaxValue;
        var tmp = new ushort[w * 3 * 2];
        var bestYBase = new ushort[w * h];
        var targetYBase = new ushort[w * h];
        var bestRgbY = new ushort[w * 2];
        var bestUvBase = new short[uvW * 3 * uvH];
        var targetUvBase = new short[uvW * 3 * uvH];
        var bestRgbUv = new short[uvW * 3];
        ulong diffYThreshold = (ulong)(3.0 * w * h);

        void ImportOneRow(int row, Span<ushort> dst)
        {
            int shift = GetPrecisionShift(rgbBitDepth);
            for (int i = 0; i < width; i++)
            {
                dst[i + 0 * w] = (ushort)Shift(r[row * width + i], shift);
                dst[i + 1 * w] = (ushort)Shift(g[row * width + i], shift);
                dst[i + 2 * w] = (ushort)Shift(b[row * width + i], shift);
            }
            if ((width & 1) != 0)
            {
                dst[width + 0 * w] = dst[width + 0 * w - 1];
                dst[width + 1 * w] = dst[width + 1 * w - 1];
                dst[width + 2 * w] = dst[width + 2 * w - 1];
            }
        }

        int by = 0, buv = 0;
        for (int j = 0; j < height; j += 2)
        {
            var src1 = tmp.AsSpan(0, 3 * w);
            var src2 = tmp.AsSpan(3 * w, 3 * w);
            ImportOneRow(j, src1);
            if (j != height - 1) ImportOneRow(j + 1, src2);
            else src1.CopyTo(src2);
            StoreGray(src1, bestYBase.AsSpan(by), w);
            StoreGray(src2, bestYBase.AsSpan(by + w), w);
            UpdateW(src1, targetYBase.AsSpan(by), w, rgbBitDepth, transfer);
            UpdateW(src2, targetYBase.AsSpan(by + w), w, rgbBitDepth, transfer);
            UpdateChroma(src1, src2, targetUvBase.AsSpan(buv), uvW, rgbBitDepth, transfer);
            targetUvBase.AsSpan(buv, 3 * uvW).CopyTo(bestUvBase.AsSpan(buv));
            by += 2 * w;
            buv += 3 * uvW;
        }

        for (int iter = 0; iter < NumIterations; iter++)
        {
            int cur = 0, prev = 0;
            ulong diffYSum = 0;
            by = 0; buv = 0;
            int j = 0;
            do
            {
                var src1 = tmp.AsSpan(0, 3 * w);
                var src2 = tmp.AsSpan(3 * w, 3 * w);
                int next = cur + (j < h - 2 ? 3 * uvW : 0);
                InterpolateTwoRows(bestYBase.AsSpan(by), bestUvBase.AsSpan(prev), bestUvBase.AsSpan(cur), bestUvBase.AsSpan(next), w, src1, src2, rgbBitDepth);
                prev = cur;
                cur = next;
                UpdateW(src1, bestRgbY.AsSpan(0, w), w, rgbBitDepth, transfer);
                UpdateW(src2, bestRgbY.AsSpan(w, w), w, rgbBitDepth, transfer);
                UpdateChroma(src1, src2, bestRgbUv, uvW, rgbBitDepth, transfer);
                // SharpYuvUpdateY / SharpYuvUpdateRGB.
                int maxY = (1 << yBitDepth) - 1;
                for (int i = 0; i < 2 * w; i++)
                {
                    int diff = targetYBase[by + i] - bestRgbY[i];
                    bestYBase[by + i] = Clip(bestYBase[by + i] + diff, maxY);
                    diffYSum += (ulong)Math.Abs(diff);
                }
                for (int i = 0; i < 3 * uvW; i++)
                    bestUvBase[buv + i] = (short)(bestUvBase[buv + i] + (targetUvBase[buv + i] - bestRgbUv[i]));
                by += 2 * w;
                buv += 3 * uvW;
                j += 2;
            } while (j < h);
            if (iter > 0)
            {
                if (diffYSum < diffYThreshold) break;
                if (diffYSum > prevDiffYSum) break;
            }
            prevDiffYSum = diffYSum;
        }

        // ConvertWRGBToYUV.
        yOut = new ushort[width * height];
        uOut = new ushort[((width + 1) >> 1) * ((height + 1) >> 1)];
        vOut = new ushort[uOut.Length];
        int cw = (width + 1) >> 1, chh = (height + 1) >> 1;
        int yRow = 0, uvRow = 0;
        for (int jj = 0; jj < height; jj++)
        {
            for (int i = 0; i < width; i++)
            {
                int off = i >> 1;
                int W = bestYBase[yRow + i];
                int rr = bestUvBase[uvRow + off] + W, gg = bestUvBase[uvRow + off + uvW] + W, bb = bestUvBase[uvRow + off + 2 * uvW] + W;
                yOut[jj * width + i] = Clip(RgbToYuvComponent(rr, gg, bb, toY, sfix), yuvMax);
            }
            yRow += w;
            uvRow += (jj & 1) * 3 * uvW;
        }
        uvRow = 0;
        for (int jj = 0; jj < uvH; jj++)
        {
            for (int i = 0; i < uvW; i++)
            {
                int rr = bestUvBase[uvRow + i], gg = bestUvBase[uvRow + i + uvW], bb = bestUvBase[uvRow + i + 2 * uvW];
                if (i < cw && jj < chh)
                {
                    uOut[jj * cw + i] = Clip(RgbToYuvComponent(rr, gg, bb, toU, sfix), yuvMax);
                    vOut[jj * cw + i] = Clip(RgbToYuvComponent(rr, gg, bb, toV, sfix), yuvMax);
                }
            }
            uvRow += 3 * uvW;
        }
    }
}
