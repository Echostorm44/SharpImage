using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1 aom_dsp/x86/intrapred_avx2.c (8-bit, no edge upsampling): dr_prediction_z2_HxW_avx2 and the z3 kernels built
// from dr_prediction_z1_HxW_internal_avx2 plus a 16x16 byte transpose. Same arithmetic as the C ((a * 32 + 16 + (b - a) *
// shift) >> 5), so the predictions are the C's.
internal static unsafe partial class AomReconIntra
{
    private static readonly Vector128<byte>[] BaseMask16 = MakeBaseMask();
    private static Vector128<byte>[] MakeBaseMask()
    {
        var m = new Vector128<byte>[17];
        for (int n = 0; n <= 16; n++)
        {
            var b = new byte[16];
            for (int i = 0; i < n; i++) b[i] = 0xFF;
            m[n] = Vector128.Create(b);
        }
        return m;
    }

    /// <summary>dr_prediction_z2_HxW_avx2 (W a multiple of 16).</summary>
    internal static void DrPredictionZ2HxWAvx2(byte* dst, nint stride, int W, int H, byte* above, byte* left, int dx, int dy)
    {
        const int minBaseX = -1, minBaseY = -1;
        var a16 = Vector256.Create((short)16);
        var c1 = Vector256.Create((short)1);
        var minBaseY256 = Vector256.Create((short)minBaseY);
        var c3f = Vector256.Create((short)0x3f);
        var dy256 = Vector256.Create((short)dy);
        var c0123 = Vector256.Create((short)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
        var c1234 = c0123 + c1;
        short* baseYc = stackalloc short[16];
        for (int r = 0; r < H; r++, dst += stride)
        {
            int y = r + 1;
            var ydx = Vector256.Create((short)(y * dx));
            int baseX = (-y * dx) >> 6;
            for (int j = 0; j < W; j += 16)
            {
                var j256 = Vector256.Create((short)j);
                int baseShift = 0;
                if (baseX + j < minBaseX - 1) baseShift = minBaseX - (baseX + j) - 1;
                int baseMinDiff = minBaseX - baseX - j;
                baseMinDiff = baseMinDiff > 16 ? 16 : baseMinDiff < 0 ? 0 : baseMinDiff;
                Vector128<byte> resx, resy;
                if (baseShift < 16)
                {
                    // the loads start at base_shift (LoadMaskx shifts the bytes back into place)
                    var a0x = Sse2.LoadVector128(above + baseX + baseShift + j);
                    var a1x = Sse2.LoadVector128(above + baseX + baseShift + 1 + j);
                    var lm = LoadMaskX[baseShift];
                    a0x = Ssse3.Shuffle(a0x, lm);
                    a1x = Ssse3.Shuffle(a1x, lm);
                    var a0 = Avx2.ConvertToVector256Int16(a0x);
                    var a1 = Avx2.ConvertToVector256Int16(a1x);
                    var r6 = Avx2.ShiftLeftLogical(c0123 + j256, 6);
                    var shift = Avx2.ShiftRightLogical((r6 - ydx) & c3f, 1);
                    var res = Avx2.ShiftRightLogical(Avx2.ShiftLeftLogical(a0, 5) + a16 + Avx2.MultiplyLow(a1 - a0, shift), 5);
                    resx = Sse2.PackUnsignedSaturate(res.GetLower(), res.GetUpper());
                }
                else resx = Vector128<byte>.Zero;

                if (baseX < minBaseX)
                {
                    var r6 = Vector256.Create((short)(r << 6));
                    var c256 = j256 + c1234;
                    var mul16 = Avx2.Min(Avx2.MultiplyLow(c256, dy256).AsUInt16(), Avx2.ShiftRightLogical(minBaseY256, 1).AsUInt16()).AsInt16();
                    var yc256 = r6 - mul16;
                    var baseYc256 = Avx2.ShiftRightArithmetic(yc256, 6);
                    var mask256 = Avx2.CompareGreaterThan(minBaseY256, baseYc256);
                    baseYc256 = Avx2.BlendVariable(baseYc256.AsByte(), minBaseY256.AsByte(), mask256.AsByte()).AsInt16();
                    short minY = baseYc256.GetElement(15), maxY = baseYc256.GetElement(0);
                    int offsetDiff = maxY - minY;
                    Vector256<short> a0y, a1y;
                    if (offsetDiff < 16)
                    {
                        var off = baseYc256 - Vector256.Create(minY);
                        var off128 = Sse2.PackSignedSaturate(off.GetLower(), off.GetUpper()).AsByte();
                        // (libaom masks the loads to offset_diff + 1 bytes; the shuffle only reads those)
                        var l0 = Ssse3.Shuffle(Sse2.LoadVector128(left + minY), off128);
                        var l1 = Ssse3.Shuffle(Sse2.LoadVector128(left + minY + 1), off128);
                        a0y = Avx2.ConvertToVector256Int16(l0);
                        a1y = Avx2.ConvertToVector256Int16(l1);
                    }
                    else
                    {
                        baseYc256 = Avx2.AndNot(mask256, baseYc256);
                        baseYc256.Store(baseYc);
                        a0y = Vector256.Create(left[baseYc[0]], left[baseYc[1]], left[baseYc[2]], left[baseYc[3]], left[baseYc[4]], left[baseYc[5]],
                            left[baseYc[6]], left[baseYc[7]], left[baseYc[8]], left[baseYc[9]], left[baseYc[10]], left[baseYc[11]],
                            left[baseYc[12]], left[baseYc[13]], left[baseYc[14]], left[baseYc[15]]).AsInt16();
                        a1y = Vector256.Create(left[baseYc[0] + 1], left[baseYc[1] + 1], left[baseYc[2] + 1], left[baseYc[3] + 1], left[baseYc[4] + 1],
                            left[baseYc[5] + 1], left[baseYc[6] + 1], left[baseYc[7] + 1], left[baseYc[8] + 1], left[baseYc[9] + 1], left[baseYc[10] + 1],
                            left[baseYc[11] + 1], left[baseYc[12] + 1], left[baseYc[13] + 1], left[baseYc[14] + 1], left[baseYc[15] + 1]).AsInt16();
                    }
                    var shifty = Avx2.ShiftRightLogical(yc256 & c3f, 1);
                    var res = Avx2.ShiftRightLogical(Avx2.ShiftLeftLogical(a0y, 5) + a16 + Avx2.MultiplyLow(a1y - a0y, shifty), 5);
                    resy = Sse2.PackUnsignedSaturate(res.GetLower(), res.GetUpper());
                }
                else resy = Vector128<byte>.Zero;
                Sse2.Store(dst + j, Sse41.BlendVariable(resx, resy, BaseMask16[baseMinDiff]));
            }
        }
    }

    // LoadMaskx[shift]: lane i takes byte max(i - shift, 0) (the bytes before the load start replicate its first byte)
    private static readonly Vector128<byte>[] LoadMaskX = MakeLoadMaskX();
    private static Vector128<byte>[] MakeLoadMaskX()
    {
        var m = new Vector128<byte>[16];
        for (int s = 0; s < 16; s++)
        {
            var b = new byte[16];
            for (int i = 0; i < 16; i++) b[i] = (byte)(i < s ? 0 : i - s);
            m[s] = Vector128.Create(b);
        }
        return m;
    }

    /// <summary>dr_prediction_z1_HxW_internal_avx2 without upsampling: W rows (dst[r]) of H (&lt;= 16) interpolated samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DrZ1HxWInternal(int H, int W, Vector128<byte>* dst, byte* above, int dx)
    {
        int maxBaseX = W + H - 1;
        var a16 = Vector256.Create((short)16);
        var c3f = Vector256.Create((short)0x3f);
        var mbase = Vector128.Create(above[maxBaseX]);
        int x = dx;
        for (int r = 0; r < W; r++, x += dx)
        {
            int b = x >> 6;
            int baseMaxDiff = maxBaseX - b;
            if (baseMaxDiff <= 0)
            {
                for (int i = r; i < W; i++) dst[i] = mbase;
                return;
            }
            if (baseMaxDiff > H) baseMaxDiff = H;
            var a0 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(above + b));
            var a1 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(above + b + 1));
            var shift = Avx2.ShiftRightLogical(Vector256.Create((short)x) & c3f, 1);
            var res = Avx2.ShiftRightLogical(Avx2.ShiftLeftLogical(a0, 5) + a16 + Avx2.MultiplyLow(a1 - a0, shift), 5);
            dst[r] = Sse41.BlendVariable(mbase, Sse2.PackUnsignedSaturate(res.GetLower(), res.GetUpper()), BaseMask16[baseMaxDiff]);
        }
    }

    /// <summary>av1_dr_prediction_z3_avx2 for bh &lt;= 16 (no upsampling): zone 1 down the left edge (bw columns of bh), then
    /// byte transposes in registers (8 x 8 for bh &lt;= 8, else 16 x 16), as libaom's z3 kernels.</summary>
    internal static void DrPredictionZ3SmallAvx2(byte* dst, nint stride, int bw, int bh, byte* left, int dy)
    {
        Vector128<byte>* col = stackalloc Vector128<byte>[64];
        DrZ1HxWInternal(bh, bw, col, left, dy);
        var z = Vector128<byte>.Zero;
        if (bh <= 8)
        {
            for (int c0 = 0; c0 < bw; c0 += 8)
            {
                Vector128<byte>* s8 = col + c0;
                int n = Math.Min(8, bw - c0);
                Transpose8x8(s8[0], n > 1 ? s8[1] : z, n > 2 ? s8[2] : z, n > 3 ? s8[3] : z,
                    n > 4 ? s8[4] : z, n > 5 ? s8[5] : z, n > 6 ? s8[6] : z, n > 7 ? s8[7] : z,
                    out var r0, out var r1, out var r2, out var r3);
                Store2(dst, stride, c0, 0, r0, n, bh); Store2(dst, stride, c0, 2, r1, n, bh);
                Store2(dst, stride, c0, 4, r2, n, bh); Store2(dst, stride, c0, 6, r3, n, bh);
            }
            return;
        }
        for (int c0 = 0; c0 < bw; c0 += 16)
        {
            Vector128<byte>* q = col + c0;
            int n = Math.Min(16, bw - c0);

                Vector128<byte> t0 = q[0], t1 = n > 1 ? q[1] : z, t2 = n > 2 ? q[2] : z, t3 = n > 3 ? q[3] : z;
            Vector128<byte> t4 = n > 4 ? q[4] : z, t5 = n > 5 ? q[5] : z, t6 = n > 6 ? q[6] : z, t7 = n > 7 ? q[7] : z;
            Vector128<byte> t8 = n > 8 ? q[8] : z, t9 = n > 9 ? q[9] : z, t10 = n > 10 ? q[10] : z, t11 = n > 11 ? q[11] : z;
            Vector128<byte> t12 = n > 12 ? q[12] : z, t13 = n > 13 ? q[13] : z, t14 = n > 14 ? q[14] : z, t15 = n > 15 ? q[15] : z;
            var a0 = Sse2.UnpackLow(t0, t1).AsUInt16(); var a1 = Sse2.UnpackHigh(t0, t1).AsUInt16();
            var a2 = Sse2.UnpackLow(t2, t3).AsUInt16(); var a3 = Sse2.UnpackHigh(t2, t3).AsUInt16();
            var a4 = Sse2.UnpackLow(t4, t5).AsUInt16(); var a5 = Sse2.UnpackHigh(t4, t5).AsUInt16();
            var a6 = Sse2.UnpackLow(t6, t7).AsUInt16(); var a7 = Sse2.UnpackHigh(t6, t7).AsUInt16();
            var a8 = Sse2.UnpackLow(t8, t9).AsUInt16(); var a9 = Sse2.UnpackHigh(t8, t9).AsUInt16();
            var a10 = Sse2.UnpackLow(t10, t11).AsUInt16(); var a11 = Sse2.UnpackHigh(t10, t11).AsUInt16();
            var a12 = Sse2.UnpackLow(t12, t13).AsUInt16(); var a13 = Sse2.UnpackHigh(t12, t13).AsUInt16();
            var a14 = Sse2.UnpackLow(t14, t15).AsUInt16(); var a15 = Sse2.UnpackHigh(t14, t15).AsUInt16();
            var b0 = Sse2.UnpackLow(a0, a2).AsUInt32(); var b1 = Sse2.UnpackHigh(a0, a2).AsUInt32();
            var b2 = Sse2.UnpackLow(a1, a3).AsUInt32(); var b3 = Sse2.UnpackHigh(a1, a3).AsUInt32();
            var b4 = Sse2.UnpackLow(a4, a6).AsUInt32(); var b5 = Sse2.UnpackHigh(a4, a6).AsUInt32();
            var b6 = Sse2.UnpackLow(a5, a7).AsUInt32(); var b7 = Sse2.UnpackHigh(a5, a7).AsUInt32();
            var b8 = Sse2.UnpackLow(a8, a10).AsUInt32(); var b9 = Sse2.UnpackHigh(a8, a10).AsUInt32();
            var b10 = Sse2.UnpackLow(a9, a11).AsUInt32(); var b11 = Sse2.UnpackHigh(a9, a11).AsUInt32();
            var b12 = Sse2.UnpackLow(a12, a14).AsUInt32(); var b13 = Sse2.UnpackHigh(a12, a14).AsUInt32();
            var b14 = Sse2.UnpackLow(a13, a15).AsUInt32(); var b15 = Sse2.UnpackHigh(a13, a15).AsUInt32();
            var c0v = Sse2.UnpackLow(b0, b4).AsUInt64(); var c1v = Sse2.UnpackHigh(b0, b4).AsUInt64();
            var c2v = Sse2.UnpackLow(b1, b5).AsUInt64(); var c3v = Sse2.UnpackHigh(b1, b5).AsUInt64();
            var c4v = Sse2.UnpackLow(b2, b6).AsUInt64(); var c5v = Sse2.UnpackHigh(b2, b6).AsUInt64();
            var c6v = Sse2.UnpackLow(b3, b7).AsUInt64(); var c7v = Sse2.UnpackHigh(b3, b7).AsUInt64();
            var c8v = Sse2.UnpackLow(b8, b12).AsUInt64(); var c9v = Sse2.UnpackHigh(b8, b12).AsUInt64();
            var c10v = Sse2.UnpackLow(b9, b13).AsUInt64(); var c11v = Sse2.UnpackHigh(b9, b13).AsUInt64();
            var c12v = Sse2.UnpackLow(b10, b14).AsUInt64(); var c13v = Sse2.UnpackHigh(b10, b14).AsUInt64();
            var c14v = Sse2.UnpackLow(b11, b15).AsUInt64(); var c15v = Sse2.UnpackHigh(b11, b15).AsUInt64();
            Store16(dst, stride, c0, 0, Sse2.UnpackLow(c0v, c8v), n, bh); Store16(dst, stride, c0, 1, Sse2.UnpackHigh(c0v, c8v), n, bh);
            Store16(dst, stride, c0, 2, Sse2.UnpackLow(c1v, c9v), n, bh); Store16(dst, stride, c0, 3, Sse2.UnpackHigh(c1v, c9v), n, bh);
            Store16(dst, stride, c0, 4, Sse2.UnpackLow(c2v, c10v), n, bh); Store16(dst, stride, c0, 5, Sse2.UnpackHigh(c2v, c10v), n, bh);
            Store16(dst, stride, c0, 6, Sse2.UnpackLow(c3v, c11v), n, bh); Store16(dst, stride, c0, 7, Sse2.UnpackHigh(c3v, c11v), n, bh);
            Store16(dst, stride, c0, 8, Sse2.UnpackLow(c4v, c12v), n, bh); Store16(dst, stride, c0, 9, Sse2.UnpackHigh(c4v, c12v), n, bh);
            Store16(dst, stride, c0, 10, Sse2.UnpackLow(c5v, c13v), n, bh); Store16(dst, stride, c0, 11, Sse2.UnpackHigh(c5v, c13v), n, bh);
            Store16(dst, stride, c0, 12, Sse2.UnpackLow(c6v, c14v), n, bh); Store16(dst, stride, c0, 13, Sse2.UnpackHigh(c6v, c14v), n, bh);
            Store16(dst, stride, c0, 14, Sse2.UnpackLow(c7v, c15v), n, bh); Store16(dst, stride, c0, 15, Sse2.UnpackHigh(c7v, c15v), n, bh);
        }
    }

    /// <summary>transpose16x16_sse2 of n (&lt;= 16) column vectors q, rows 0 .. rows - 1 stored at dst.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose16Store(byte* dst, nint stride, Vector128<byte>* q, int n, int bh)
    {
        const int c0 = 0;
        var z = Vector128<byte>.Zero;
            Vector128<byte> t0 = q[0], t1 = n > 1 ? q[1] : z, t2 = n > 2 ? q[2] : z, t3 = n > 3 ? q[3] : z;
        Vector128<byte> t4 = n > 4 ? q[4] : z, t5 = n > 5 ? q[5] : z, t6 = n > 6 ? q[6] : z, t7 = n > 7 ? q[7] : z;
        Vector128<byte> t8 = n > 8 ? q[8] : z, t9 = n > 9 ? q[9] : z, t10 = n > 10 ? q[10] : z, t11 = n > 11 ? q[11] : z;
        Vector128<byte> t12 = n > 12 ? q[12] : z, t13 = n > 13 ? q[13] : z, t14 = n > 14 ? q[14] : z, t15 = n > 15 ? q[15] : z;
        var a0 = Sse2.UnpackLow(t0, t1).AsUInt16(); var a1 = Sse2.UnpackHigh(t0, t1).AsUInt16();
        var a2 = Sse2.UnpackLow(t2, t3).AsUInt16(); var a3 = Sse2.UnpackHigh(t2, t3).AsUInt16();
        var a4 = Sse2.UnpackLow(t4, t5).AsUInt16(); var a5 = Sse2.UnpackHigh(t4, t5).AsUInt16();
        var a6 = Sse2.UnpackLow(t6, t7).AsUInt16(); var a7 = Sse2.UnpackHigh(t6, t7).AsUInt16();
        var a8 = Sse2.UnpackLow(t8, t9).AsUInt16(); var a9 = Sse2.UnpackHigh(t8, t9).AsUInt16();
        var a10 = Sse2.UnpackLow(t10, t11).AsUInt16(); var a11 = Sse2.UnpackHigh(t10, t11).AsUInt16();
        var a12 = Sse2.UnpackLow(t12, t13).AsUInt16(); var a13 = Sse2.UnpackHigh(t12, t13).AsUInt16();
        var a14 = Sse2.UnpackLow(t14, t15).AsUInt16(); var a15 = Sse2.UnpackHigh(t14, t15).AsUInt16();
        var b0 = Sse2.UnpackLow(a0, a2).AsUInt32(); var b1 = Sse2.UnpackHigh(a0, a2).AsUInt32();
        var b2 = Sse2.UnpackLow(a1, a3).AsUInt32(); var b3 = Sse2.UnpackHigh(a1, a3).AsUInt32();
        var b4 = Sse2.UnpackLow(a4, a6).AsUInt32(); var b5 = Sse2.UnpackHigh(a4, a6).AsUInt32();
        var b6 = Sse2.UnpackLow(a5, a7).AsUInt32(); var b7 = Sse2.UnpackHigh(a5, a7).AsUInt32();
        var b8 = Sse2.UnpackLow(a8, a10).AsUInt32(); var b9 = Sse2.UnpackHigh(a8, a10).AsUInt32();
        var b10 = Sse2.UnpackLow(a9, a11).AsUInt32(); var b11 = Sse2.UnpackHigh(a9, a11).AsUInt32();
        var b12 = Sse2.UnpackLow(a12, a14).AsUInt32(); var b13 = Sse2.UnpackHigh(a12, a14).AsUInt32();
        var b14 = Sse2.UnpackLow(a13, a15).AsUInt32(); var b15 = Sse2.UnpackHigh(a13, a15).AsUInt32();
        var c0v = Sse2.UnpackLow(b0, b4).AsUInt64(); var c1v = Sse2.UnpackHigh(b0, b4).AsUInt64();
        var c2v = Sse2.UnpackLow(b1, b5).AsUInt64(); var c3v = Sse2.UnpackHigh(b1, b5).AsUInt64();
        var c4v = Sse2.UnpackLow(b2, b6).AsUInt64(); var c5v = Sse2.UnpackHigh(b2, b6).AsUInt64();
        var c6v = Sse2.UnpackLow(b3, b7).AsUInt64(); var c7v = Sse2.UnpackHigh(b3, b7).AsUInt64();
        var c8v = Sse2.UnpackLow(b8, b12).AsUInt64(); var c9v = Sse2.UnpackHigh(b8, b12).AsUInt64();
        var c10v = Sse2.UnpackLow(b9, b13).AsUInt64(); var c11v = Sse2.UnpackHigh(b9, b13).AsUInt64();
        var c12v = Sse2.UnpackLow(b10, b14).AsUInt64(); var c13v = Sse2.UnpackHigh(b10, b14).AsUInt64();
        var c14v = Sse2.UnpackLow(b11, b15).AsUInt64(); var c15v = Sse2.UnpackHigh(b11, b15).AsUInt64();
        Store16(dst, stride, c0, 0, Sse2.UnpackLow(c0v, c8v), n, bh); Store16(dst, stride, c0, 1, Sse2.UnpackHigh(c0v, c8v), n, bh);
        Store16(dst, stride, c0, 2, Sse2.UnpackLow(c1v, c9v), n, bh); Store16(dst, stride, c0, 3, Sse2.UnpackHigh(c1v, c9v), n, bh);
        Store16(dst, stride, c0, 4, Sse2.UnpackLow(c2v, c10v), n, bh); Store16(dst, stride, c0, 5, Sse2.UnpackHigh(c2v, c10v), n, bh);
        Store16(dst, stride, c0, 6, Sse2.UnpackLow(c3v, c11v), n, bh); Store16(dst, stride, c0, 7, Sse2.UnpackHigh(c3v, c11v), n, bh);
        Store16(dst, stride, c0, 8, Sse2.UnpackLow(c4v, c12v), n, bh); Store16(dst, stride, c0, 9, Sse2.UnpackHigh(c4v, c12v), n, bh);
        Store16(dst, stride, c0, 10, Sse2.UnpackLow(c5v, c13v), n, bh); Store16(dst, stride, c0, 11, Sse2.UnpackHigh(c5v, c13v), n, bh);
        Store16(dst, stride, c0, 12, Sse2.UnpackLow(c6v, c14v), n, bh); Store16(dst, stride, c0, 13, Sse2.UnpackHigh(c6v, c14v), n, bh);
        Store16(dst, stride, c0, 14, Sse2.UnpackLow(c7v, c15v), n, bh); Store16(dst, stride, c0, 15, Sse2.UnpackHigh(c7v, c15v), n, bh);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16(byte* dst, nint stride, int c0, int r, Vector128<ulong> v, int n, int bh)
    {
        if (r >= bh) return;
        byte* d = dst + r * stride + c0;
        if (n == 16) Sse2.Store(d, v.AsByte());
        else if (n == 8) *(ulong*)d = v.ToScalar();
        else *(uint*)d = v.AsUInt32().ToScalar();
    }

    // output rows r and r + 1 from the low / high 8 bytes of v (n columns, 4 or 8)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store2(byte* dst, nint stride, int c0, int r, Vector128<byte> v, int n, int bh)
    {
        var q = v.AsUInt64();
        if (r < bh) { byte* d = dst + r * stride + c0; if (n == 8) *(ulong*)d = q.GetElement(0); else *(uint*)d = (uint)q.GetElement(0); }
        if (r + 1 < bh) { byte* d = dst + (r + 1) * stride + c0; if (n == 8) *(ulong*)d = q.GetElement(1); else *(uint*)d = (uint)q.GetElement(1); }
    }

    // the transpose of the low 8 bytes of x0..x7: o_k = output rows 2k (low half) and 2k + 1 (high half)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(Vector128<byte> x0, Vector128<byte> x1, Vector128<byte> x2, Vector128<byte> x3,
        Vector128<byte> x4, Vector128<byte> x5, Vector128<byte> x6, Vector128<byte> x7,
        out Vector128<byte> o0, out Vector128<byte> o1, out Vector128<byte> o2, out Vector128<byte> o3)
    {
        var w0 = Sse2.UnpackLow(x0, x1).AsUInt16(); var w1 = Sse2.UnpackLow(x2, x3).AsUInt16();
        var w2 = Sse2.UnpackLow(x4, x5).AsUInt16(); var w3 = Sse2.UnpackLow(x6, x7).AsUInt16();
        var d0 = Sse2.UnpackLow(w0, w1).AsUInt32(); var d1 = Sse2.UnpackHigh(w0, w1).AsUInt32();
        var d2 = Sse2.UnpackLow(w2, w3).AsUInt32(); var d3 = Sse2.UnpackHigh(w2, w3).AsUInt32();
        o0 = Sse2.UnpackLow(d0, d2).AsByte(); o1 = Sse2.UnpackHigh(d0, d2).AsByte();
        o2 = Sse2.UnpackLow(d1, d3).AsByte(); o3 = Sse2.UnpackHigh(d1, d3).AsByte();
    }

    private static readonly Vector256<byte>[] BaseMask32 = MakeBaseMask32();
    private static Vector256<byte>[] MakeBaseMask32()
    {
        var m = new Vector256<byte>[33];
        for (int n = 0; n <= 32; n++)
        {
            var b = new byte[32];
            for (int i = 0; i < n; i++) b[i] = 0xFF;
            m[n] = Vector256.Create(b);
        }
        return m;
    }

    /// <summary>dr_prediction_z1_32xN_internal_avx2: N rows of 32 samples.</summary>
    private static void DrZ1_32xNInternal(int N, Vector256<byte>* dst, byte* above, int dx)
    {
        int maxBaseX = 32 + N - 1;
        var a16 = Vector256.Create((short)16);
        var c3f = Vector256.Create((short)0x3f);
        var mbase = Vector256.Create(above[maxBaseX]);
        int x = dx;
        for (int r = 0; r < N; r++, x += dx)
        {
            int b = x >> 6;
            int baseMaxDiff = maxBaseX - b;
            if (baseMaxDiff <= 0)
            {
                for (int i = r; i < N; i++) dst[i] = mbase;
                return;
            }
            if (baseMaxDiff > 32) baseMaxDiff = 32;
            var shift = Avx2.ShiftRightLogical(Vector256.Create((short)x) & c3f, 1);
            Vector128<byte> lo, hi;
            {
                var a0 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(above + b));
                var a1 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(above + b + 1));
                var res = Avx2.ShiftRightLogical(Avx2.ShiftLeftLogical(a0, 5) + a16 + Avx2.MultiplyLow(a1 - a0, shift), 5);
                lo = Sse2.PackUnsignedSaturate(res.GetLower(), res.GetUpper());
            }
            if (baseMaxDiff - 16 <= 0) hi = mbase.GetLower();
            else
            {
                var a0 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(above + b + 16));
                var a1 = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(above + b + 17));
                var res = Avx2.ShiftRightLogical(Avx2.ShiftLeftLogical(a0, 5) + a16 + Avx2.MultiplyLow(a1 - a0, shift), 5);
                hi = Sse2.PackUnsignedSaturate(res.GetLower(), res.GetUpper());
            }
            dst[r] = Avx2.BlendVariable(mbase, Vector256.Create(lo, hi), BaseMask32[baseMaxDiff]);
        }
    }

    /// <summary>dr_prediction_z1_32xN_avx2.</summary>
    internal static void DrPredictionZ1_32Avx2(byte* dst, nint stride, int bh, byte* above, int dx)
    {
        Vector256<byte>* rows = stackalloc Vector256<byte>[64];
        DrZ1_32xNInternal(bh, rows, above, dx);
        for (int i = 0; i < bh; i++) rows[i].Store(dst + i * stride);
    }

    /// <summary>The z3 kernels for bh == 32: zone 1 down the left edge (bw columns of 32), the two 16-row halves transposed.</summary>
    internal static void DrPredictionZ3Tall32Avx2(byte* dst, nint stride, int bw, byte* left, int dy)
    {
        Vector256<byte>* col = stackalloc Vector256<byte>[64];
        DrZ1_32xNInternal(bw, col, left, dy);
        Vector128<byte>* lo = stackalloc Vector128<byte>[16];
        Vector128<byte>* hi = stackalloc Vector128<byte>[16];
        for (int c0 = 0; c0 < bw; c0 += 16)
        {
            int n = Math.Min(16, bw - c0);
            for (int k = 0; k < n; k++) { lo[k] = col[c0 + k].GetLower(); hi[k] = col[c0 + k].GetUpper(); }
            Transpose16Store(dst + c0, stride, lo, n, 16);
            Transpose16Store(dst + 16 * stride + c0, stride, hi, n, 16);
        }
    }
}
