// AV1 warped-motion parameter derivation, ported from dav1d src/warpmv.c (VideoLAN dav1d, BSD-2-Clause):
// shear parameters for global / local warps, the least-squares affine fit from neighbouring motion samples, and the
// translation terms from a block's motion vector.

using System;

namespace SharpImage.Formats.Av1;

internal static class Av1WarpMv
{
    private static readonly ushort[] DivLut =
    [
        16384, 16320, 16257, 16194, 16132, 16070, 16009, 15948, 15888, 15828, 15768,
        15709, 15650, 15592, 15534, 15477, 15420, 15364, 15308, 15252, 15197, 15142,
        15087, 15033, 14980, 14926, 14873, 14821, 14769, 14717, 14665, 14614, 14564,
        14513, 14463, 14413, 14364, 14315, 14266, 14218, 14170, 14122, 14075, 14028,
        13981, 13935, 13888, 13843, 13797, 13752, 13707, 13662, 13618, 13574, 13530,
        13487, 13443, 13400, 13358, 13315, 13273, 13231, 13190, 13148, 13107, 13066,
        13026, 12985, 12945, 12906, 12866, 12827, 12788, 12749, 12710, 12672, 12633,
        12596, 12558, 12520, 12483, 12446, 12409, 12373, 12336, 12300, 12264, 12228,
        12193, 12157, 12122, 12087, 12053, 12018, 11984, 11950, 11916, 11882, 11848,
        11815, 11782, 11749, 11716, 11683, 11651, 11619, 11586, 11555, 11523, 11491,
        11460, 11429, 11398, 11367, 11336, 11305, 11275, 11245, 11215, 11185, 11155,
        11125, 11096, 11067, 11038, 11009, 10980, 10951, 10923, 10894, 10866, 10838,
        10810, 10782, 10755, 10727, 10700, 10673, 10645, 10618, 10592, 10565, 10538,
        10512, 10486, 10460, 10434, 10408, 10382, 10356, 10331, 10305, 10280, 10255,
        10230, 10205, 10180, 10156, 10131, 10107, 10082, 10058, 10034, 10010,  9986,
         9963,  9939,  9916,  9892,  9869,  9846,  9823,  9800,  9777,  9754,  9732,
         9709,  9687,  9664,  9642,  9620,  9598,  9576,  9554,  9533,  9511,  9489,
         9468,  9447,  9425,  9404,  9383,  9362,  9341,  9321,  9300,  9279,  9259,
         9239,  9218,  9198,  9178,  9158,  9138,  9118,  9098,  9079,  9059,  9039,
         9020,  9001,  8981,  8962,  8943,  8924,  8905,  8886,  8867,  8849,  8830,
         8812,  8793,  8775,  8756,  8738,  8720,  8702,  8684,  8666,  8648,  8630,
         8613,  8595,  8577,  8560,  8542,  8525,  8508,  8490,  8473,  8456,  8439,
         8422,  8405,  8389,  8372,  8355,  8339,  8322,  8306,  8289,  8273,  8257,
         8240,  8224,  8208,  8192,
    ];

    private static int ApplySign(int v, long s) => s < 0 ? -v : v;

    private static int IclipWmp(int v)
    {
        int cv = Math.Clamp(v, short.MinValue, short.MaxValue);
        return ApplySign((Math.Abs(cv) + 32) >> 6, cv) * (1 << 6);
    }

    private static int Ulog2(ulong v) => 63 - System.Numerics.BitOperations.LeadingZeroCount(v);

    private static int ResolveDivisor32(uint d, out int shift)
    {
        shift = Ulog2(d);
        int e = (int)(d - (1u << shift));
        int f = shift > 8 ? (e + (1 << (shift - 9))) >> (shift - 8) : e << (8 - shift);
        shift += 14;
        return DivLut[f];
    }

    private static int ResolveDivisor64(ulong d, out int shift)
    {
        shift = Ulog2(d);
        long e = (long)(d - (1UL << shift));
        long f = shift > 8 ? (e + (1L << (shift - 9))) >> (shift - 8) : e << (8 - shift);
        shift += 14;
        return DivLut[f];
    }

    /// <summary>dav1d_get_shear_params: fills alpha..delta; returns true when the warp is invalid (too sheared).</summary>
    public static bool GetShearParams(ref Av1WarpedMotionParams wm)
    {
        if (wm.Matrix2 <= 0) return true;
        wm.Alpha = (short)IclipWmp(wm.Matrix2 - 0x10000);
        wm.Beta = (short)IclipWmp(wm.Matrix3);
        int y = ApplySign(ResolveDivisor32((uint)Math.Abs(wm.Matrix2), out int shift), wm.Matrix2);
        long v1 = (long)wm.Matrix4 * 0x10000 * y;
        int rnd = (1 << shift) >> 1;
        wm.Gamma = (short)IclipWmp(ApplySign((int)((Math.Abs(v1) + rnd) >> shift), v1));
        long v2 = (long)wm.Matrix3 * wm.Matrix4 * y;
        wm.Delta = (short)IclipWmp(wm.Matrix5 - ApplySign((int)((Math.Abs(v2) + rnd) >> shift), v2) - 0x10000);
        return 4 * Math.Abs(wm.Alpha) + 7 * Math.Abs(wm.Beta) >= 0x10000
            || 4 * Math.Abs(wm.Gamma) + 4 * Math.Abs(wm.Delta) >= 0x10000;
    }

    private static int MultShiftNdiag(long px, int idet, int shift)
    {
        long v1 = px * idet;
        int v2 = ApplySign((int)((Math.Abs(v1) + ((1L << shift) >> 1)) >> shift), v1);
        return Math.Clamp(v2, -0x1fff, 0x1fff);
    }

    private static int MultShiftDiag(long px, int idet, int shift)
    {
        long v1 = px * idet;
        int v2 = ApplySign((int)((Math.Abs(v1) + ((1L << shift) >> 1)) >> shift), v1);
        return Math.Clamp(v2, 0xe001, 0x11fff);
    }

    /// <summary>dav1d_set_affine_mv2d: translation terms from the block's motion vector.</summary>
    public static void SetAffineMv2d(int bw4, int bh4, Av1MotionVector mv, ref Av1WarpedMotionParams wm, int bx4, int by4)
    {
        int rsuy = 2 * bh4 - 1, rsux = 2 * bw4 - 1;
        int isuy = by4 * 4 + rsuy, isux = bx4 * 4 + rsux;
        wm.Matrix0 = Math.Clamp(mv.X * 0x2000 - (isux * (wm.Matrix2 - 0x10000) + isuy * wm.Matrix3), -0x800000, 0x7fffff);
        wm.Matrix1 = Math.Clamp(mv.Y * 0x2000 - (isux * wm.Matrix4 + isuy * (wm.Matrix5 - 0x10000)), -0x800000, 0x7fffff);
    }

    /// <summary>dav1d_find_affine_int: least-squares affine fit to <paramref name="np"/> sample pairs
    /// (pts[i*4 + 0..1] = source x/y, pts[i*4 + 2..3] = destination x/y). Returns true on failure.</summary>
    public static bool FindAffineInt(int[] pts, int np, int bw4, int bh4, Av1MotionVector mv, ref Av1WarpedMotionParams wm,
        int bx4, int by4)
    {
        int a00 = 0, a01 = 0, a11 = 0, bx0 = 0, bx1 = 0, by0 = 0, by1 = 0;
        int rsuy = 2 * bh4 - 1, rsux = 2 * bw4 - 1;
        int suy = rsuy * 8, sux = rsux * 8;
        int duy = suy + mv.Y, dux = sux + mv.X;
        int isuy = by4 * 4 + rsuy, isux = bx4 * 4 + rsux;
        for (int i = 0; i < np; i++)
        {
            int dx = pts[i * 4 + 2] - dux, dy = pts[i * 4 + 3] - duy;
            int sx = pts[i * 4 + 0] - sux, sy = pts[i * 4 + 1] - suy;
            if (Math.Abs(sx - dx) < 256 && Math.Abs(sy - dy) < 256)
            {
                a00 += ((sx * sx) >> 2) + sx * 2 + 8;
                a01 += ((sx * sy) >> 2) + sx + sy + 4;
                a11 += ((sy * sy) >> 2) + sy * 2 + 8;
                bx0 += ((sx * dx) >> 2) + sx + dx + 8;
                bx1 += ((sy * dx) >> 2) + sy + dx + 4;
                by0 += ((sx * dy) >> 2) + sx + dy + 4;
                by1 += ((sy * dy) >> 2) + sy + dy + 8;
            }
        }
        long det = (long)a00 * a11 - (long)a01 * a01;
        if (det == 0) return true;
        int idet = ApplySign(ResolveDivisor64((ulong)Math.Abs(det), out int shift), det);
        shift -= 16;
        if (shift < 0)
        {
            idet <<= -shift;
            shift = 0;
        }
        wm.Matrix2 = MultShiftDiag((long)a11 * bx0 - (long)a01 * bx1, idet, shift);
        wm.Matrix3 = MultShiftNdiag((long)a00 * bx1 - (long)a01 * bx0, idet, shift);
        wm.Matrix4 = MultShiftNdiag((long)a11 * by0 - (long)a01 * by1, idet, shift);
        wm.Matrix5 = MultShiftDiag((long)a00 * by1 - (long)a01 * by0, idet, shift);
        wm.Matrix0 = Math.Clamp(mv.X * 0x2000 - (isux * (wm.Matrix2 - 0x10000) + isuy * wm.Matrix3), -0x800000, 0x7fffff);
        wm.Matrix1 = Math.Clamp(mv.Y * 0x2000 - (isux * wm.Matrix4 + isuy * (wm.Matrix5 - 0x10000)), -0x800000, 0x7fffff);
        return false;
    }
}
