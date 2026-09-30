using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using V = System.Runtime.Intrinsics.Vector256<int>;
using W8 = System.Runtime.Intrinsics.Vector128<short>;
using W16 = System.Runtime.Intrinsics.Vector256<short>;

namespace SharpImage.Formats.Av1;

// libaom's lowbd forward transform (av1_lowbd_fwd_txfm_avx2: av1_fwd_txfm_sse2.c, av1_fwd_txfm2d_avx2.c), the 8-bit
// encoder's: the residual and every intermediate in 16-bit lanes, 16 columns / rows per vector (8 below 16), half the
// work of the 32-bit lanes for the same butterflies. Its values are the reference transform's wherever no 16-bit
// intermediate saturates, which 8-bit residuals keep (libaom tests its lowbd kernels equal to the reference); the
// coefficients then go through the same quantiser as ForwardQuant.
internal static partial class Av1FwdTxfmAom
{
    /// <summary>This thread encodes 8-bit content (set with the encoder's bit depth): its forward transforms up to 32 x 32
    /// take the lowbd path.</summary>
    [ThreadStatic] internal static bool Lowbd;
    private static readonly bool LowbdOn = Environment.GetEnvironmentVariable("AV1_LOWBDFWD") != "0";

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W8 AddS(W8 a, W8 b) => Sse2.AddSaturate(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W16 AddS(W16 a, W16 b) => Avx2.AddSaturate(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W8 SubS(W8 a, W8 b) => Sse2.SubtractSaturate(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W16 SubS(W16 a, W16 b) => Avx2.SubtractSaturate(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W8 UnpL(W8 a, W8 b) => Sse2.UnpackLow(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W16 UnpL(W16 a, W16 b) => Avx2.UnpackLow(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W8 UnpH(W8 a, W8 b) => Sse2.UnpackHigh(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W16 UnpH(W16 a, W16 b) => Avx2.UnpackHigh(a, b);
    // pair_set_epi16(w0, w1): the (w0, w1) weights of a madd over interleaved (a, b)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W8 Pair128(int w0, int w1) => Vector128.Create((w0 & 0xFFFF) | (w1 << 16)).AsInt16();
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static W16 Pair256(int w0, int w1) => Vector256.Create((w0 & 0xFFFF) | (w1 << 16)).AsInt16();

    // half_btf(w0, a, w1, b, bit) from the unpacked (a, b): madd, round, shift, saturating pack (libaom's btf_16_sse2 half)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W8 Btf(W8 lo, W8 hi, W8 w, Vector128<int> rnd, int bit)
        => Sse2.PackSignedSaturate(Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(lo, w) + rnd, bit),
                                   Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(hi, w) + rnd, bit));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W16 Btf(W16 lo, W16 hi, W16 w, V rnd, int bit)
        => Avx2.PackSignedSaturate(Vector256.ShiftRightArithmetic(Avx2.MultiplyAddAdjacent(lo, w) + rnd, bit),
                                   Vector256.ShiftRightArithmetic(Avx2.MultiplyAddAdjacent(hi, w) + rnd, bit));

    // scale_round_sse2 over (x, 1) pairs: (x * scale + 2^11) >> 12, packed with saturation
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W8 ScaleRound(W8 x, int scale)
    {
        var one = Vector128.Create((short)1); var w = Pair128(scale, 1 << 11);
        return Sse2.PackSignedSaturate(Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(x, one), w), 12),
                                       Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(x, one), w), 12));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W16 ScaleRound(W16 x, int scale)
    {
        var one = Vector256.Create((short)1); var w = Pair256(scale, 1 << 11);
        return Avx2.PackSignedSaturate(Vector256.ShiftRightArithmetic(Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(x, one), w), 12),
                                       Vector256.ShiftRightArithmetic(Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(x, one), w), 12));
    }

    // fadst4 in the reference's 32 bits (8 lanes at a time), packed back with saturation
    [SkipLocalsInit]
    private static void Fadst4W8(ref W8 input, ref W8 output, int cosBit)
    {
        Span<V> t = stackalloc V[8];
        for (int i = 0; i < 4; i++) t[i] = Avx2.ConvertToVector256Int32(Unsafe.Add(ref input, i));
        Fadst4(ref t[0], ref t[4], cosBit);
        for (int k = 0; k < 4; k++) Unsafe.Add(ref output, k) = Sse2.PackSignedSaturate(t[4 + k].GetLower(), t[4 + k].GetUpper());
    }

    [SkipLocalsInit]
    private static void Fadst4W16(ref W16 input, ref W16 output, int cosBit)
    {
        Span<W8> lo = stackalloc W8[8];
        Span<W8> hi = stackalloc W8[8];
        for (int i = 0; i < 4; i++) { lo[i] = Unsafe.Add(ref input, i).GetLower(); hi[i] = Unsafe.Add(ref input, i).GetUpper(); }
        Fadst4W8(ref lo[0], ref lo[4], cosBit);
        Fadst4W8(ref hi[0], ref hi[4], cosBit);
        for (int k = 0; k < 4; k++) Unsafe.Add(ref output, k) = Vector256.Create(lo[4 + k], hi[4 + k]);
    }

    // 1D kinds as Av1InvTransform.Type1dDct / Type1dAdst / Type1dIdentity (libaom's fidentity*_new_sse2 for the identity)
    private static void TxW8(int kind, int n, ref W8 input, ref W8 output, int cosBit)
    {
        if (kind == Av1InvTransform.Type1dIdentity)
        {
            for (int i = 0; i < n; i++)
            {
                W8 x = Unsafe.Add(ref input, i);
                Unsafe.Add(ref output, i) = n switch { 4 => ScaleRound(x, 5793), 8 => AddS(x, x), 16 => ScaleRound(x, 2 * 5793), _ => Vector128.ShiftLeft(x, 2) };
            }
            return;
        }
        if (kind == Av1InvTransform.Type1dAdst)
        {
            if (n == 4) Fadst4W8(ref input, ref output, cosBit); else if (n == 8) Fadst8W8(ref input, ref output, cosBit); else Fadst16W8(ref input, ref output, cosBit);
            return;
        }
        switch (n)
        {
            case 4: Fdct4W8(ref input, ref output, cosBit); break;
            case 8: Fdct8W8(ref input, ref output, cosBit); break;
            case 16: Fdct16W8(ref input, ref output, cosBit); break;
            default: Fdct32W8(ref input, ref output, cosBit); break;
        }
    }

    private static void TxW16(int kind, int n, ref W16 input, ref W16 output, int cosBit)
    {
        if (kind == Av1InvTransform.Type1dIdentity)
        {
            for (int i = 0; i < n; i++)
            {
                W16 x = Unsafe.Add(ref input, i);
                Unsafe.Add(ref output, i) = n switch { 4 => ScaleRound(x, 5793), 8 => AddS(x, x), 16 => ScaleRound(x, 2 * 5793), _ => Vector256.ShiftLeft(x, 2) };
            }
            return;
        }
        if (kind == Av1InvTransform.Type1dAdst)
        {
            if (n == 4) Fadst4W16(ref input, ref output, cosBit); else if (n == 8) Fadst8W16(ref input, ref output, cosBit); else Fadst16W16(ref input, ref output, cosBit);
            return;
        }
        switch (n)
        {
            case 4: Fdct4W16(ref input, ref output, cosBit); break;
            case 8: Fdct8W16(ref input, ref output, cosBit); break;
            case 16: Fdct16W16(ref input, ref output, cosBit); break;
            default: Fdct32W16(ref input, ref output, cosBit); break;
        }
    }

    // the 8 x 8 block of 16-bit values at src (row stride `stride`) transposed: dst[j] = its column j
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8S(ref short src, int stride, ref W8 dst)
    {
        W8 r0 = Vector128.LoadUnsafe(ref src), r1 = Vector128.LoadUnsafe(ref src, (nuint)stride), r2 = Vector128.LoadUnsafe(ref src, (nuint)(2 * stride)), r3 = Vector128.LoadUnsafe(ref src, (nuint)(3 * stride));
        W8 r4 = Vector128.LoadUnsafe(ref src, (nuint)(4 * stride)), r5 = Vector128.LoadUnsafe(ref src, (nuint)(5 * stride)), r6 = Vector128.LoadUnsafe(ref src, (nuint)(6 * stride)), r7 = Vector128.LoadUnsafe(ref src, (nuint)(7 * stride));
        var a0 = Sse2.UnpackLow(r0, r1).AsInt32(); var a1 = Sse2.UnpackHigh(r0, r1).AsInt32();
        var a2 = Sse2.UnpackLow(r2, r3).AsInt32(); var a3 = Sse2.UnpackHigh(r2, r3).AsInt32();
        var a4 = Sse2.UnpackLow(r4, r5).AsInt32(); var a5 = Sse2.UnpackHigh(r4, r5).AsInt32();
        var a6 = Sse2.UnpackLow(r6, r7).AsInt32(); var a7 = Sse2.UnpackHigh(r6, r7).AsInt32();
        var b0 = Sse2.UnpackLow(a0, a2).AsInt64(); var b1 = Sse2.UnpackHigh(a0, a2).AsInt64();
        var b2 = Sse2.UnpackLow(a1, a3).AsInt64(); var b3 = Sse2.UnpackHigh(a1, a3).AsInt64();
        var b4 = Sse2.UnpackLow(a4, a6).AsInt64(); var b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
        var b6 = Sse2.UnpackLow(a5, a7).AsInt64(); var b7 = Sse2.UnpackHigh(a5, a7).AsInt64();
        dst = Sse2.UnpackLow(b0, b4).AsInt16(); Unsafe.Add(ref dst, 1) = Sse2.UnpackHigh(b0, b4).AsInt16();
        Unsafe.Add(ref dst, 2) = Sse2.UnpackLow(b1, b5).AsInt16(); Unsafe.Add(ref dst, 3) = Sse2.UnpackHigh(b1, b5).AsInt16();
        Unsafe.Add(ref dst, 4) = Sse2.UnpackLow(b2, b6).AsInt16(); Unsafe.Add(ref dst, 5) = Sse2.UnpackHigh(b2, b6).AsInt16();
        Unsafe.Add(ref dst, 6) = Sse2.UnpackLow(b3, b7).AsInt16(); Unsafe.Add(ref dst, 7) = Sse2.UnpackHigh(b3, b7).AsInt16();
    }

    // round_shift_16bit's right shift: a saturating add of the rounding, then an arithmetic shift
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W8 RShift(W8 v, W8 rnd, int bit) => bit > 0 ? Vector128.ShiftRightArithmetic(Sse2.AddSaturate(v, rnd), bit) : v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W16 RShift(W16 v, W16 rnd, int bit) => bit > 0 ? Vector256.ShiftRightArithmetic(Avx2.AddSaturate(v, rnd), bit) : v;

    /// <summary>ForwardQuant through the lowbd transform (w, h at most 32).</summary>
    [SkipLocalsInit]
    private static int ForwardQuantLbd(ReadOnlySpan<int> residual, int w, int h, int txSize, int hKind, int vKind,
        double dcDq, double acDq, double bias, int[] levels, double[]? qfOut)
    {
        ref int inv0 = ref MemoryMarshal.GetArrayDataReference(InvScan(txSize));
        int lw = System.Numerics.BitOperations.Log2((uint)w) - 2, lh = System.Numerics.BitOperations.Log2((uint)h) - 2;
        int sh0 = Shift[txSize * 3], sh1 = -Shift[txSize * 3 + 1], sh2 = -Shift[txSize * 3 + 2];
        int cosCol = CosBitCol[lw * 5 + lh], cosRow = CosBitRow[lw * 5 + lh];
        bool rect2 = w == 2 * h || h == 2 * w;
        int pels = w * h;
        double scale = 1 << ((pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0));
        // the column pass's output, row-major (stride cs), rows past h zero
        int cs = Math.Max(w, 8), hp = Math.Max(h, 8);
        Span<short> cbS = stackalloc short[cs * hp];
        if (h < hp) cbS.Slice(h * cs).Clear();
        ref short cb = ref MemoryMarshal.GetReference(cbS);
        ref int res0 = ref MemoryMarshal.GetReference(residual);
        Span<W16> bufS = stackalloc W16[64];
        ref W16 bi = ref bufS[0];
        ref W16 bo = ref bufS[32];
        if (w >= 16)
        {
            var r1 = Vector256.Create((short)(sh1 > 0 ? 1 << (sh1 - 1) : 0));
            for (int c0 = 0; c0 < w; c0 += 16)
            {
                for (int r = 0; r < h; r++)
                {
                    ref int p = ref Unsafe.Add(ref res0, r * w + c0);
                    var v = Avx2.PackSignedSaturate(Vector256.LoadUnsafe(ref p), Vector256.LoadUnsafe(ref p, 8));
                    Unsafe.Add(ref bi, r) = Vector256.ShiftLeft(Avx2.Permute4x64(v.AsInt64(), 0xD8).AsInt16(), sh0);
                }
                TxW16(vKind, h, ref bi, ref bo, cosCol);
                for (int r = 0; r < h; r++) RShift(Unsafe.Add(ref bo, r), r1, sh1).StoreUnsafe(ref cb, (nuint)(r * cs + c0));
            }
        }
        else
        {
            ref W8 ci = ref Unsafe.As<W16, W8>(ref bi);
            ref W8 co = ref Unsafe.As<W16, W8>(ref bo);
            var r1 = Vector128.Create((short)(sh1 > 0 ? 1 << (sh1 - 1) : 0));
            for (int r = 0; r < h; r++)
            {
                ref int p = ref Unsafe.Add(ref res0, r * w);
                W8 v = Sse2.PackSignedSaturate(Vector128.LoadUnsafe(ref p), w == 8 ? Vector128.LoadUnsafe(ref p, 4) : Vector128<int>.Zero);
                Unsafe.Add(ref ci, r) = Vector128.ShiftLeft(v, sh0);
            }
            TxW8(vKind, h, ref ci, ref co, cosCol);
            for (int r = 0; r < h; r++) RShift(Unsafe.Add(ref co, r), r1, sh1).StoreUnsafe(ref cb, (nuint)(r * cs));
        }

        // row pass: rows as lanes (16 at a time from 16 tall, else 8), each column's output the rc-contiguous levels
        var eobV = Vector128.Create(-1);   // per lane: the largest scan index of a nonzero level seen
        var vdc = Vector256.Create(scale / dcDq, scale / acDq, scale / acDq, scale / acDq); var vac = Vector256.Create(scale / acDq);
        var half = Vector256.Create(0.5); var vbias = Vector256.Create(bias);
        ref int lv0 = ref MemoryMarshal.GetArrayDataReference(levels);
        Span<W8> blk = stackalloc W8[16];
        ref W8 bk = ref MemoryMarshal.GetReference(blk);
        if (h >= 16)
        {
            var r2 = Vector256.Create((short)(sh2 > 0 ? 1 << (sh2 - 1) : 0));
            for (int r0 = 0; r0 < h; r0 += 16)
            {
                for (int c0 = 0; c0 < w; c0 += 8)
                {
                    Transpose8x8S(ref Unsafe.Add(ref cb, r0 * cs + c0), cs, ref bk);
                    Transpose8x8S(ref Unsafe.Add(ref cb, (r0 + 8) * cs + c0), cs, ref Unsafe.Add(ref bk, 8));
                    int nc = Math.Min(8, w - c0);
                    for (int j = 0; j < nc; j++) Unsafe.Add(ref bi, c0 + j) = Vector256.Create(Unsafe.Add(ref bk, j), Unsafe.Add(ref bk, 8 + j));
                }
                TxW16(hKind, w, ref bi, ref bo, cosRow);
                for (int c = 0; c < w; c++)
                {
                    W16 v = RShift(Unsafe.Add(ref bo, c), r2, sh2);
                    int rc = c * h + r0;
                    QuantCol(Avx2.ConvertToVector256Int32(v.GetLower()), rect2, rc, 8, vdc, vac, half, vbias, ref lv0, qfOut, ref inv0, ref eobV);
                    QuantCol(Avx2.ConvertToVector256Int32(v.GetUpper()), rect2, rc + 8, 8, vdc, vac, half, vbias, ref lv0, qfOut, ref inv0, ref eobV);
                }
            }
        }
        else
        {
            ref W8 ri = ref Unsafe.As<W16, W8>(ref bi);
            ref W8 ro = ref Unsafe.As<W16, W8>(ref bo);
            var r2 = Vector128.Create((short)(sh2 > 0 ? 1 << (sh2 - 1) : 0));
            for (int c0 = 0; c0 < w; c0 += 8)
            {
                Transpose8x8S(ref Unsafe.Add(ref cb, c0), cs, ref bk);
                int nc = Math.Min(8, w - c0);
                for (int j = 0; j < nc; j++) Unsafe.Add(ref ri, c0 + j) = Unsafe.Add(ref bk, j);
            }
            TxW8(hKind, w, ref ri, ref ro, cosRow);
            for (int c = 0; c < w; c++)
                QuantCol(Avx2.ConvertToVector256Int32(RShift(Unsafe.Add(ref ro, c), r2, sh2)), rect2, c * h, h, vdc, vac, half, vbias, ref lv0, qfOut, ref inv0, ref eobV);
        }
        return Math.Max(Math.Max(eobV.GetElement(0), eobV.GetElement(1)), Math.Max(eobV.GetElement(2), eobV.GetElement(3)));
    }

    // Quantise nr (4 or 8) consecutive coefficients (rc ..): the rect scale round_shift(x * NewInvSqrt2, 12) (exact in 32
    // bits for 16-bit x), then ForwardQuant's quantiser and eob tracking.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuantCol(V v, bool rect2, int rc, int nr, Vector256<double> vdc, Vector256<double> vac, Vector256<double> half,
        Vector256<double> vbias, ref int lv0, double[]? qfOut, ref int inv0, ref Vector128<int> eobV)
    {
        if (rect2) v = Vector256.ShiftRightArithmetic(v * Vector256.Create(5793) + Vector256.Create(1 << 11), 12);
        var m1 = Vector128.Create(-1);
        var q = Quant4(v.GetLower(), rc == 0 ? vdc : vac, half, vbias, ref Unsafe.Add(ref lv0, rc), qfOut, rc);
        eobV = Vector128.Max(eobV, Vector128.ConditionalSelect(Vector128.Equals(q, Vector128<int>.Zero), m1, Vector128.LoadUnsafe(ref inv0, (nuint)rc)));
        if (nr == 8)
        {
            q = Quant4(v.GetUpper(), vac, half, vbias, ref Unsafe.Add(ref lv0, rc + 4), qfOut, rc + 4);
            eobV = Vector128.Max(eobV, Vector128.ConditionalSelect(Vector128.Equals(q, Vector128<int>.Zero), m1, Vector128.LoadUnsafe(ref inv0, (nuint)(rc + 4))));
        }
    }
}
