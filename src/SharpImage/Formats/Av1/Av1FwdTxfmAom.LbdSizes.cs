using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using W8 = System.Runtime.Intrinsics.Vector128<short>;
using W16 = System.Runtime.Intrinsics.Vector256<short>;

namespace SharpImage.Formats.Av1;

// libaom's per-size lowbd forward transform drivers (av1_lowbd_fwd_txfm2d_<w>x<h>_sse2 / _avx2 of av1_fwd_txfm_sse2.c and
// av1_fwd_txfm2d_avx2.c, as av1_lowbd_fwd_txfm_avx2 dispatches them): the load / flip / round-shift / in-register
// transpose / store sequence of each size around the shared 1D kernels, instead of ForwardRawLbd's generic staging.
// Lanes libaom leaves uninitialised (and never stores) are zero here.
internal static partial class Av1FwdTxfmAom
{
    [InlineArray(8)] private struct Buf8 { private W8 e; }
    [InlineArray(16)] private struct Buf16 { private W8 e; }
    [InlineArray(16)] private struct Buf16W { private W16 e; }

    /// <summary>The specialised lowbd driver for txSize when there is one (true), else false (ForwardRawLbd's generic path).
    /// output: coefficients in libaom's layout (column-major, rows contiguous).</summary>
    private static bool ForwardLbdSized(ref short input, int stride, int txSize, int hKind, int vKind, bool flipUd, bool flipLr,
        ref int output)
    {
        switch (txSize)
        {
            case 0: Fwd4x4(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 1: Fwd8x8(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 2: Fwd16x16(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 5: Fwd4x8(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 6: Fwd8x4(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 7: Fwd8x16(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 8: Fwd16x8(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 13: Fwd4x16(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            case 14: Fwd16x4(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            default: return false;
        }
    }

    // ---- 1D kernels in place by kind and length (the col_txfm / row_txfm tables) ----

    /// <summary>8 lanes: fdct8x4 / fadst8x4 / fidentity8x4 (n 4), fdct8x8 / fadst8x8 / fidentity8x8 (8), fdct8x16 /
    /// fadst8x16 / fidentity8x16 (16) _new_sse2.</summary>
    private static void Tx8(int kind, int n, ref W8 buf, int cosBit)
    {
        if (kind == Av1InvTransform.Type1dIdentity)
        {
            if (n == 8) for (int i = 0; i < 8; i++) Unsafe.Add(ref buf, i) = AddS(Unsafe.Add(ref buf, i), Unsafe.Add(ref buf, i));
            else
            {
                int scale = n == 4 ? 5793 : 2 * 5793;
                for (int i = 0; i < n; i++) Unsafe.Add(ref buf, i) = ScaleRound(Unsafe.Add(ref buf, i), scale);
            }
            return;
        }
        if (kind == Av1InvTransform.Type1dAdst)
        {
            if (n == 4) FadstMadd4W8(ref buf, cosBit); else if (n == 8) Fadst8W8(ref buf, ref buf, cosBit); else Fadst16W8(ref buf, ref buf, cosBit);
            return;
        }
        if (n == 4) Fdct4W8(ref buf, ref buf, cosBit); else if (n == 8) Fdct8W8(ref buf, ref buf, cosBit); else Fdct16W8(ref buf, ref buf, cosBit);
    }

    /// <summary>16 lanes: fdct8x8 / fadst8x8 / fidentity8x8 (n 8) and fdct16x16 / fadst16x16 / fidentity16x16 (16) _new_avx2.</summary>
    private static void Tx16(int kind, int n, ref W16 buf, int cosBit)
    {
        if (kind == Av1InvTransform.Type1dIdentity)
        {
            if (n == 8) for (int i = 0; i < 8; i++) Unsafe.Add(ref buf, i) = AddS(Unsafe.Add(ref buf, i), Unsafe.Add(ref buf, i));
            else for (int i = 0; i < n; i++) Unsafe.Add(ref buf, i) = ScaleRound(Unsafe.Add(ref buf, i), 2 * 5793);
            return;
        }
        if (kind == Av1InvTransform.Type1dAdst)
        {
            if (n == 8) Fadst8W16(ref buf, ref buf, cosBit); else Fadst16W16(ref buf, ref buf, cosBit);
            return;
        }
        if (n == 8) Fdct8W16(ref buf, ref buf, cosBit); else Fdct16W16(ref buf, ref buf, cosBit);
    }

    /// <summary>The 4 x 4 kernels (col / row_txfm4x4_arr): fdct4x4 / fadst4x4 / fidentity4x4_new_sse2 (low 4 lanes).</summary>
    private static void Tx4x4(int kind, ref W8 buf, int cosBit)
    {
        if (kind == Av1InvTransform.Type1dIdentity) { for (int i = 0; i < 4; i++) Unsafe.Add(ref buf, i) = ScaleRound(Unsafe.Add(ref buf, i), 5793); }
        else if (kind == Av1InvTransform.Type1dAdst) Fadst4x4Madd(ref buf, cosBit);
        else Fdct4x4Madd(ref buf, cosBit);
    }

    /// <summary>fdct4x4_new_sse2 (in place, 4 lanes).</summary>
    private static void Fdct4x4Madd(ref W8 buf, int cosBit)
    {
        ref int cp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(CosPiData), (cosBit - 10) * 64);
        int c32 = Unsafe.Add(ref cp, 32), c16 = Unsafe.Add(ref cp, 16), c48 = Unsafe.Add(ref cp, 48);
        var rnd = Vector128.Create(1 << (cosBit - 1));
        W8 u0 = Sse2.UnpackLow(buf, Unsafe.Add(ref buf, 1));
        W8 u1 = Sse2.UnpackLow(Unsafe.Add(ref buf, 3), Unsafe.Add(ref buf, 2));
        W8 v0 = Sse2.Add(u0, u1), v1 = Sse2.Subtract(u0, u1);
        var a0 = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(v0, Pair128(c32, c32)) + rnd, cosBit);
        var a1 = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(v0, Pair128(c32, -c32)) + rnd, cosBit);
        var a2 = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(v1, Pair128(c16, c48)) + rnd, cosBit);
        var a3 = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(v1, Pair128(c48, -c16)) + rnd, cosBit);
        W8 o0 = Sse2.PackSignedSaturate(a0, a1), o1 = Sse2.PackSignedSaturate(a2, a3);
        buf = o0;
        Unsafe.Add(ref buf, 1) = o1;
        Unsafe.Add(ref buf, 2) = Sse2.ShiftRightLogical128BitLane(o0, 8);
        Unsafe.Add(ref buf, 3) = Sse2.ShiftRightLogical128BitLane(o1, 8);
    }

    /// <summary>fadst4x4_new_sse2 (in place, 4 lanes).</summary>
    private static void Fadst4x4Madd(ref W8 buf, int cosBit)
    {
        ref int sp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SinPiData), (cosBit - 10) * 5);
        int s1 = Unsafe.Add(ref sp, 1), s2 = Unsafe.Add(ref sp, 2), s3 = Unsafe.Add(ref sp, 3), s4 = Unsafe.Add(ref sp, 4);
        var rnd = Vector128.Create(1 << (cosBit - 1));
        W8 i0 = buf, i1 = Unsafe.Add(ref buf, 1), i2 = Unsafe.Add(ref buf, 2), i3 = Unsafe.Add(ref buf, 3), z = W8.Zero;
        W8 in7 = Sse2.Add(i0, i1);
        W8 u0 = Sse2.UnpackLow(i0, i1), u1 = Sse2.UnpackLow(i2, i3), u2 = Sse2.UnpackLow(in7, z);
        W8 u3 = Sse2.UnpackLow(i2, z), u4 = Sse2.UnpackLow(i3, z);
        W8 p33 = Vector128.Create((short)s3);
        var v0 = Sse2.MultiplyAddAdjacent(u0, Pair128(s1, s2));
        var v1 = Sse2.MultiplyAddAdjacent(u1, Pair128(s3, s4));
        var v2 = Sse2.MultiplyAddAdjacent(u2, p33);
        var v3 = Sse2.MultiplyAddAdjacent(u0, Pair128(s4, -s1));
        var v4 = Sse2.MultiplyAddAdjacent(u1, Pair128(-s3, s2));
        var v5 = Sse2.MultiplyAddAdjacent(u3, p33);
        var v6 = Sse2.MultiplyAddAdjacent(u4, p33);
        var w0 = v0 + v1;
        var w1 = v2 - v6;
        var w2 = v3 + v4;
        var w3 = w2 - w0;
        var w4 = Vector128.ShiftLeft(v5, 2);
        var w5 = w4 - v5;
        var w6 = w3 + w5;
        var x0 = Vector128.ShiftRightArithmetic(w0 + rnd, cosBit);
        var x1 = Vector128.ShiftRightArithmetic(w1 + rnd, cosBit);
        var x2 = Vector128.ShiftRightArithmetic(w2 + rnd, cosBit);
        var x3 = Vector128.ShiftRightArithmetic(w6 + rnd, cosBit);
        W8 o0 = Sse2.PackSignedSaturate(x0, x2), o1 = Sse2.PackSignedSaturate(x1, x3);
        buf = o0;
        Unsafe.Add(ref buf, 1) = o1;
        Unsafe.Add(ref buf, 2) = Sse2.ShiftRightLogical128BitLane(o0, 8);
        Unsafe.Add(ref buf, 3) = Sse2.ShiftRightLogical128BitLane(o1, 8);
    }

    /// <summary>fadst8x4_new_sse2 (in place, 8 lanes).</summary>
    private static void FadstMadd4W8(ref W8 buf, int cosBit)
    {
        ref int sp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SinPiData), (cosBit - 10) * 5);
        int s1 = Unsafe.Add(ref sp, 1), s2 = Unsafe.Add(ref sp, 2), s3 = Unsafe.Add(ref sp, 3), s4 = Unsafe.Add(ref sp, 4);
        var rnd = Vector128.Create(1 << (cosBit - 1));
        W8 i0 = buf, i1 = Unsafe.Add(ref buf, 1), i2 = Unsafe.Add(ref buf, 2), i3 = Unsafe.Add(ref buf, 3), z = W8.Zero;
        W8 in7 = Sse2.Add(i0, i1);
        W8 p12 = Pair128(s1, s2), p34 = Pair128(s3, s4), p4m1 = Pair128(s4, -s1), pm32 = Pair128(-s3, s2), p33 = Vector128.Create((short)s3);
        W8 ul0 = Sse2.UnpackLow(i0, i1), uh0 = Sse2.UnpackHigh(i0, i1);
        W8 ul1 = Sse2.UnpackLow(i2, i3), uh1 = Sse2.UnpackHigh(i2, i3);
        W8 ul2 = Sse2.UnpackLow(in7, z), uh2 = Sse2.UnpackHigh(in7, z);
        W8 ul3 = Sse2.UnpackLow(i2, z), uh3 = Sse2.UnpackHigh(i2, z);
        W8 ul4 = Sse2.UnpackLow(i3, z), uh4 = Sse2.UnpackHigh(i3, z);
        var vl0 = Sse2.MultiplyAddAdjacent(ul0, p12); var vh0 = Sse2.MultiplyAddAdjacent(uh0, p12);
        var vl1 = Sse2.MultiplyAddAdjacent(ul1, p34); var vh1 = Sse2.MultiplyAddAdjacent(uh1, p34);
        var vl2 = Sse2.MultiplyAddAdjacent(ul2, p33); var vh2 = Sse2.MultiplyAddAdjacent(uh2, p33);
        var vl3 = Sse2.MultiplyAddAdjacent(ul0, p4m1); var vh3 = Sse2.MultiplyAddAdjacent(uh0, p4m1);
        var vl4 = Sse2.MultiplyAddAdjacent(ul1, pm32); var vh4 = Sse2.MultiplyAddAdjacent(uh1, pm32);
        var vl5 = Sse2.MultiplyAddAdjacent(ul3, p33); var vh5 = Sse2.MultiplyAddAdjacent(uh3, p33);
        var vl6 = Sse2.MultiplyAddAdjacent(ul4, p33); var vh6 = Sse2.MultiplyAddAdjacent(uh4, p33);
        var wl0 = vl0 + vl1; var wh0 = vh0 + vh1;
        var wl1 = vl2 - vl6; var wh1 = vh2 - vh6;
        var wl2 = vl3 + vl4; var wh2 = vh3 + vh4;
        var wl6 = wl2 - wl0 + (Vector128.ShiftLeft(vl5, 2) - vl5);
        var wh6 = wh2 - wh0 + (Vector128.ShiftLeft(vh5, 2) - vh5);
        buf = Sse2.PackSignedSaturate(Vector128.ShiftRightArithmetic(wl0 + rnd, cosBit), Vector128.ShiftRightArithmetic(wh0 + rnd, cosBit));
        Unsafe.Add(ref buf, 1) = Sse2.PackSignedSaturate(Vector128.ShiftRightArithmetic(wl1 + rnd, cosBit), Vector128.ShiftRightArithmetic(wh1 + rnd, cosBit));
        Unsafe.Add(ref buf, 2) = Sse2.PackSignedSaturate(Vector128.ShiftRightArithmetic(wl2 + rnd, cosBit), Vector128.ShiftRightArithmetic(wh2 + rnd, cosBit));
        Unsafe.Add(ref buf, 3) = Sse2.PackSignedSaturate(Vector128.ShiftRightArithmetic(wl6 + rnd, cosBit), Vector128.ShiftRightArithmetic(wh6 + rnd, cosBit));
    }

    // ---- loads, shifts, transposes, stores ----

    // n rows of 8 (or 4: the low 64 bits) residuals, upside down with flipUd, shifted left by sh0
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load8(ref short input, int stride, int n, bool flipUd, int sh0, ref W8 buf)
    {
        for (int i = 0; i < n; i++)
            Unsafe.Add(ref buf, flipUd ? n - 1 - i : i) = Vector128.ShiftLeft(Vector128.LoadUnsafe(ref input, (nuint)(i * stride)), sh0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load4(ref short input, int stride, int n, bool flipUd, int sh0, ref W8 buf)
    {
        for (int i = 0; i < n; i++)
            Unsafe.Add(ref buf, flipUd ? n - 1 - i : i) = Vector128.ShiftLeft(
                Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref input, i * stride)))).AsInt16(), sh0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundShift(ref W8 buf, int n, int bit)
    {
        if (bit <= 0) return;
        var rnd = Vector128.Create((short)(1 << (bit - 1)));
        for (int i = 0; i < n; i++) Unsafe.Add(ref buf, i) = Vector128.ShiftRightArithmetic(Sse2.AddSaturate(Unsafe.Add(ref buf, i), rnd), bit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundShift(ref W16 buf, int n, int bit)
    {
        if (bit <= 0) return;
        var rnd = Vector256.Create((short)(1 << (bit - 1)));
        for (int i = 0; i < n; i++) Unsafe.Add(ref buf, i) = Vector256.ShiftRightArithmetic(Avx2.AddSaturate(Unsafe.Add(ref buf, i), rnd), bit);
    }

    /// <summary>out[j] = column j of the 8 x 8 block in[0..8) (flip: out reversed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(ref W8 src, ref W8 dst, bool flip)
    {
        var a0 = Sse2.UnpackLow(src, Unsafe.Add(ref src, 1)).AsInt32(); var a1 = Sse2.UnpackHigh(src, Unsafe.Add(ref src, 1)).AsInt32();
        var a2 = Sse2.UnpackLow(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32(); var a3 = Sse2.UnpackHigh(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32();
        var a4 = Sse2.UnpackLow(Unsafe.Add(ref src, 4), Unsafe.Add(ref src, 5)).AsInt32(); var a5 = Sse2.UnpackHigh(Unsafe.Add(ref src, 4), Unsafe.Add(ref src, 5)).AsInt32();
        var a6 = Sse2.UnpackLow(Unsafe.Add(ref src, 6), Unsafe.Add(ref src, 7)).AsInt32(); var a7 = Sse2.UnpackHigh(Unsafe.Add(ref src, 6), Unsafe.Add(ref src, 7)).AsInt32();
        var b0 = Sse2.UnpackLow(a0, a2).AsInt64(); var b1 = Sse2.UnpackHigh(a0, a2).AsInt64();
        var b2 = Sse2.UnpackLow(a1, a3).AsInt64(); var b3 = Sse2.UnpackHigh(a1, a3).AsInt64();
        var b4 = Sse2.UnpackLow(a4, a6).AsInt64(); var b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
        var b6 = Sse2.UnpackLow(a5, a7).AsInt64(); var b7 = Sse2.UnpackHigh(a5, a7).AsInt64();
        int s = flip ? 7 : 0, d = flip ? -1 : 1;
        Unsafe.Add(ref dst, s) = Sse2.UnpackLow(b0, b4).AsInt16(); Unsafe.Add(ref dst, s + d) = Sse2.UnpackHigh(b0, b4).AsInt16();
        Unsafe.Add(ref dst, s + 2 * d) = Sse2.UnpackLow(b1, b5).AsInt16(); Unsafe.Add(ref dst, s + 3 * d) = Sse2.UnpackHigh(b1, b5).AsInt16();
        Unsafe.Add(ref dst, s + 4 * d) = Sse2.UnpackLow(b2, b6).AsInt16(); Unsafe.Add(ref dst, s + 5 * d) = Sse2.UnpackHigh(b2, b6).AsInt16();
        Unsafe.Add(ref dst, s + 6 * d) = Sse2.UnpackLow(b3, b7).AsInt16(); Unsafe.Add(ref dst, s + 7 * d) = Sse2.UnpackHigh(b3, b7).AsInt16();
    }

    /// <summary>transpose_16bit_4x8: out[j] (j &lt; 4) = column j of the 8 rows' low 4 lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x8(ref W8 src, ref W8 dst)
    {
        var a0 = Sse2.UnpackLow(src, Unsafe.Add(ref src, 1)).AsInt32();
        var a1 = Sse2.UnpackLow(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32();
        var a2 = Sse2.UnpackLow(Unsafe.Add(ref src, 4), Unsafe.Add(ref src, 5)).AsInt32();
        var a3 = Sse2.UnpackLow(Unsafe.Add(ref src, 6), Unsafe.Add(ref src, 7)).AsInt32();
        var b0 = Sse2.UnpackLow(a0, a1).AsInt64(); var b1 = Sse2.UnpackLow(a2, a3).AsInt64();
        var b2 = Sse2.UnpackHigh(a0, a1).AsInt64(); var b3 = Sse2.UnpackHigh(a2, a3).AsInt64();
        dst = Sse2.UnpackLow(b0, b1).AsInt16(); Unsafe.Add(ref dst, 1) = Sse2.UnpackHigh(b0, b1).AsInt16();
        Unsafe.Add(ref dst, 2) = Sse2.UnpackLow(b2, b3).AsInt16(); Unsafe.Add(ref dst, 3) = Sse2.UnpackHigh(b2, b3).AsInt16();
    }

    /// <summary>transpose_16bit_8x4: out[j] (j &lt; 8) = column j of the 4 rows (low 4 lanes, the rest zero).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x4(ref W8 src, ref W8 dst)
    {
        var a0 = Sse2.UnpackLow(src, Unsafe.Add(ref src, 1)).AsInt32();
        var a1 = Sse2.UnpackLow(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32();
        var a4 = Sse2.UnpackHigh(src, Unsafe.Add(ref src, 1)).AsInt32();
        var a5 = Sse2.UnpackHigh(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32();
        var b0 = Sse2.UnpackLow(a0, a1).AsInt64(); var b2 = Sse2.UnpackLow(a4, a5).AsInt64();
        var b4 = Sse2.UnpackHigh(a0, a1).AsInt64(); var b6 = Sse2.UnpackHigh(a4, a5).AsInt64();
        var z = Vector128<long>.Zero;
        dst = Sse2.UnpackLow(b0, z).AsInt16(); Unsafe.Add(ref dst, 1) = Sse2.UnpackHigh(b0, z).AsInt16();
        Unsafe.Add(ref dst, 2) = Sse2.UnpackLow(b4, z).AsInt16(); Unsafe.Add(ref dst, 3) = Sse2.UnpackHigh(b4, z).AsInt16();
        Unsafe.Add(ref dst, 4) = Sse2.UnpackLow(b2, z).AsInt16(); Unsafe.Add(ref dst, 5) = Sse2.UnpackHigh(b2, z).AsInt16();
        Unsafe.Add(ref dst, 6) = Sse2.UnpackLow(b6, z).AsInt16(); Unsafe.Add(ref dst, 7) = Sse2.UnpackHigh(b6, z).AsInt16();
    }

    /// <summary>Two 8 x 8 transposes at once, one per 128-bit half (transpose2_8x8_avx2 / transpose_16bit_16x8_avx2):
    /// out[j] = column j of in[0..8) in each half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose2x8x8(ref W16 src, ref W16 dst)
    {
        var a0 = Avx2.UnpackLow(src, Unsafe.Add(ref src, 1)).AsInt32(); var a1 = Avx2.UnpackHigh(src, Unsafe.Add(ref src, 1)).AsInt32();
        var a2 = Avx2.UnpackLow(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32(); var a3 = Avx2.UnpackHigh(Unsafe.Add(ref src, 2), Unsafe.Add(ref src, 3)).AsInt32();
        var a4 = Avx2.UnpackLow(Unsafe.Add(ref src, 4), Unsafe.Add(ref src, 5)).AsInt32(); var a5 = Avx2.UnpackHigh(Unsafe.Add(ref src, 4), Unsafe.Add(ref src, 5)).AsInt32();
        var a6 = Avx2.UnpackLow(Unsafe.Add(ref src, 6), Unsafe.Add(ref src, 7)).AsInt32(); var a7 = Avx2.UnpackHigh(Unsafe.Add(ref src, 6), Unsafe.Add(ref src, 7)).AsInt32();
        var b0 = Avx2.UnpackLow(a0, a2).AsInt64(); var b1 = Avx2.UnpackHigh(a0, a2).AsInt64();
        var b2 = Avx2.UnpackLow(a1, a3).AsInt64(); var b3 = Avx2.UnpackHigh(a1, a3).AsInt64();
        var b4 = Avx2.UnpackLow(a4, a6).AsInt64(); var b5 = Avx2.UnpackHigh(a4, a6).AsInt64();
        var b6 = Avx2.UnpackLow(a5, a7).AsInt64(); var b7 = Avx2.UnpackHigh(a5, a7).AsInt64();
        dst = Avx2.UnpackLow(b0, b4).AsInt16(); Unsafe.Add(ref dst, 1) = Avx2.UnpackHigh(b0, b4).AsInt16();
        Unsafe.Add(ref dst, 2) = Avx2.UnpackLow(b1, b5).AsInt16(); Unsafe.Add(ref dst, 3) = Avx2.UnpackHigh(b1, b5).AsInt16();
        Unsafe.Add(ref dst, 4) = Avx2.UnpackLow(b2, b6).AsInt16(); Unsafe.Add(ref dst, 5) = Avx2.UnpackHigh(b2, b6).AsInt16();
        Unsafe.Add(ref dst, 6) = Avx2.UnpackLow(b3, b7).AsInt16(); Unsafe.Add(ref dst, 7) = Avx2.UnpackHigh(b3, b7).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reverse(ref W8 buf, int n)
    {
        for (int i = 0, j = n - 1; i < j; i++, j--) (Unsafe.Add(ref buf, i), Unsafe.Add(ref buf, j)) = (Unsafe.Add(ref buf, j), Unsafe.Add(ref buf, i));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reverse(ref W16 buf, int n)
    {
        for (int i = 0, j = n - 1; i < j; i++, j--) (Unsafe.Add(ref buf, i), Unsafe.Add(ref buf, j)) = (Unsafe.Add(ref buf, j), Unsafe.Add(ref buf, i));
    }

    // (x * NewSqrt2 + 2^11) >> 12: store_rect's scale_round over (x, 1) pairs, exact in 32 bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Rect(Vector256<int> v) => Vector256.ShiftRightArithmetic(v * Vector256.Create(5793) + Vector256.Create(1 << 11), 12);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Rect(Vector128<int> v) => Vector128.ShiftRightArithmetic(v * Vector128.Create(5793) + Vector128.Create(1 << 11), 12);

    /// <summary>store_buffer_16bit_to_32bit_w8 (rect: store_rect_buffer_16bit_to_32bit_w8): n rows of 8 at out + i * stride.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(ref W8 buf, int n, ref int output, int stride, bool rect)
    {
        for (int i = 0; i < n; i++)
        {
            var v = Avx2.ConvertToVector256Int32(Unsafe.Add(ref buf, i));
            (rect ? Rect(v) : v).StoreUnsafe(ref output, (nuint)(i * stride));
        }
    }

    /// <summary>store_buffer_16bit_to_32bit_w4 (rect: _rect_ ..._w4): the low 4 lanes of n rows at out + i * stride.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(ref W8 buf, int n, ref int output, int stride, bool rect)
    {
        for (int i = 0; i < n; i++)
        {
            var v = Sse41.ConvertToVector128Int32(Unsafe.Add(ref buf, i));
            (rect ? Rect(v) : v).StoreUnsafe(ref output, (nuint)(i * stride));
        }
    }

    /// <summary>store_buffer_16bit_to_32bit_w16_avx2 (rect: store_rect_buffer_16bit_to_32bit_w16_avx2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16(ref W16 buf, int n, ref int output, int stride, bool rect)
    {
        for (int i = 0; i < n; i++)
        {
            W16 x = Unsafe.Add(ref buf, i);
            var lo = Avx2.ConvertToVector256Int32(x.GetLower());
            var hi = Avx2.ConvertToVector256Int32(x.GetUpper());
            if (rect) { lo = Rect(lo); hi = Rect(hi); }
            lo.StoreUnsafe(ref output, (nuint)(i * stride));
            hi.StoreUnsafe(ref output, (nuint)(i * stride + 8));
        }
    }

    // ---- the drivers (shift = av1_fwd_txfm_shift_ls, cos bits = av1_fwd_cos_bit_col / _row of the size) ----

    /// <summary>av1_lowbd_fwd_txfm2d_4x4_sse2.</summary>
    [SkipLocalsInit]
    private static void Fwd4x4(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf8 b);
        ref W8 b0 = ref b[0];
        ref W8 b1 = ref b[4];
        Load4(ref input, stride, 4, flipUd, 2, ref b0);
        Tx4x4(vKind, ref b0, 13);
        // transpose_16bit_4x4 (shift[1] = 0)
        var a0 = Sse2.UnpackLow(b0, Unsafe.Add(ref b0, 1)).AsInt32();
        var a1 = Sse2.UnpackLow(Unsafe.Add(ref b0, 2), Unsafe.Add(ref b0, 3)).AsInt32();
        var o0 = Sse2.UnpackLow(a0, a1).AsInt16(); var o2 = Sse2.UnpackHigh(a0, a1).AsInt16();
        b1 = o0; Unsafe.Add(ref b1, 1) = Sse2.ShiftRightLogical128BitLane(o0, 8);
        Unsafe.Add(ref b1, 2) = o2; Unsafe.Add(ref b1, 3) = Sse2.ShiftRightLogical128BitLane(o2, 8);
        if (flipLr) Reverse(ref b1, 4);
        Tx4x4(hKind, ref b1, 13);
        Store4(ref b1, 4, ref output, 4, false);
    }

    /// <summary>av1_lowbd_fwd_txfm2d_8x8_avx2.</summary>
    [SkipLocalsInit]
    private static void Fwd8x8(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf8 b);
        ref W8 b0 = ref b[0];
        Load8(ref input, stride, 8, flipUd, 2, ref b0);
        Tx8(vKind, 8, ref b0, 13);
        TransposeRoundShift8x8(ref b0, flipLr, 1);
        Tx8(hKind, 8, ref b0, 13);
        Store8(ref b0, 8, ref output, 8, false);
    }

    /// <summary>av1_lowbd_fwd_txfm2d_8x4_sse2 (w 8, h 4).</summary>
    [SkipLocalsInit]
    private static void Fwd8x4(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf16 b);
        ref W8 b0 = ref b[0];
        ref W8 b1 = ref b[8];
        Load8(ref input, stride, 4, flipUd, 2, ref b0);
        Tx8(vKind, 4, ref b0, 13);
        RoundShift(ref b0, 4, 1);
        for (int i = 4; i < 8; i++) Unsafe.Add(ref b0, i) = W8.Zero;
        Transpose8x8(ref b0, ref b1, flipLr);
        Tx8(hKind, 8, ref b1, 13);
        Store4(ref b1, 8, ref output, 4, true);
    }

    /// <summary>av1_lowbd_fwd_txfm2d_4x8_sse2 (w 4, h 8).</summary>
    [SkipLocalsInit]
    private static void Fwd4x8(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf16 b);
        ref W8 b0 = ref b[0];
        ref W8 b1 = ref b[8];
        Load4(ref input, stride, 8, flipUd, 2, ref b0);
        Tx8(vKind, 8, ref b0, 13);
        RoundShift(ref b0, 8, 1);
        Transpose4x8(ref b0, ref b1);
        if (flipLr) Reverse(ref b1, 4);
        Tx8(hKind, 4, ref b1, 13);
        Store8(ref b1, 4, ref output, 8, true);
    }

    /// <summary>av1_lowbd_fwd_txfm2d_4x16_sse2 (w 4, h 16).</summary>
    [SkipLocalsInit]
    private static void Fwd4x16(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf16 b);
        Unsafe.SkipInit(out Buf8 t);
        ref W8 b0 = ref b[0];
        ref W8 t0 = ref t[0];
        Load4(ref input, stride, 16, flipUd, 2, ref b0);
        Tx8(vKind, 16, ref b0, 13);
        RoundShift(ref b0, 16, 1);
        Transpose4x8(ref b0, ref t0);
        Transpose4x8(ref Unsafe.Add(ref b0, 8), ref Unsafe.Add(ref t0, 4));
        for (int i = 0; i < 2; i++)
        {
            ref W8 r = ref Unsafe.Add(ref t0, 4 * i);
            if (flipLr) Reverse(ref r, 4);
            Tx8(hKind, 4, ref r, 12);
            Store8(ref r, 4, ref Unsafe.Add(ref output, 8 * i), 16, false);
        }
    }

    /// <summary>av1_lowbd_fwd_txfm2d_16x4_sse2 (w 16, h 4).</summary>
    [SkipLocalsInit]
    private static void Fwd16x4(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf8 b);
        Unsafe.SkipInit(out Buf16 t);
        ref W8 b0 = ref b[0];
        ref W8 t0 = ref t[0];
        for (int i = 0; i < 2; i++)
        {
            Load8(ref Unsafe.Add(ref input, 8 * i), stride, 4, flipUd, 2, ref b0);
            Tx8(vKind, 4, ref b0, 13);
            RoundShift(ref b0, 4, 1);
            Transpose8x4(ref b0, ref Unsafe.Add(ref t0, 8 * i));
        }
        if (flipLr) Reverse(ref t0, 16);
        Tx8(hKind, 16, ref t0, 13);
        Store4(ref t0, 16, ref output, 4, false);
    }

    /// <summary>lowbd_fwd_txfm2d_8x16_avx2 (w 8, h 16).</summary>
    [SkipLocalsInit]
    private static void Fwd8x16(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf16 b);
        Unsafe.SkipInit(out Buf16 t);
        Unsafe.SkipInit(out Buf16W w);
        ref W8 b0 = ref b[0];
        ref W8 t0 = ref t[0];
        ref W16 w0 = ref w[0];
        Load8(ref input, stride, 16, flipUd, 2, ref b0);
        Tx8(vKind, 16, ref b0, 13);
        RoundShift(ref b0, 16, 2);
        Transpose8x8(ref b0, ref t0, flipLr);
        Transpose8x8(ref Unsafe.Add(ref b0, 8), ref Unsafe.Add(ref t0, 8), flipLr);
        for (int i = 0; i < 8; i++) Unsafe.Add(ref w0, i) = Vector256.Create(Unsafe.Add(ref t0, i), Unsafe.Add(ref t0, 8 + i));
        Tx16(hKind, 8, ref w0, 13);
        Store16(ref w0, 8, ref output, 16, true);
    }

    /// <summary>lowbd_fwd_txfm2d_16x8_avx2 (w 16, h 8).</summary>
    [SkipLocalsInit]
    private static void Fwd16x8(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf16W w);
        Unsafe.SkipInit(out Buf16 t);
        ref W16 w0 = ref w[0];
        ref W8 t0 = ref t[0];
        for (int i = 0; i < 8; i++)
            Unsafe.Add(ref w0, flipUd ? 7 - i : i) = Vector256.ShiftLeft(Vector256.LoadUnsafe(ref input, (nuint)(i * stride)), 2);
        Tx16(vKind, 8, ref w0, 13);
        RoundShift(ref w0, 8, 2);
        Transpose2x8x8(ref w0, ref Unsafe.Add(ref w0, 8));
        for (int i = 0; i < 8; i++)
        {
            W16 x = Unsafe.Add(ref w0, 8 + i);
            Unsafe.Add(ref t0, i) = x.GetLower();
            Unsafe.Add(ref t0, 8 + i) = x.GetUpper();
        }
        if (flipLr) Reverse(ref t0, 16);
        Tx8(hKind, 16, ref t0, 13);
        Store8(ref t0, 16, ref output, 8, true);
    }

    /// <summary>lowbd_fwd_txfm2d_16x16_avx2.</summary>
    [SkipLocalsInit]
    private static void Fwd16x16(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Unsafe.SkipInit(out Buf16W w);
        Unsafe.SkipInit(out Buf16W t);
        ref W16 w0 = ref w[0];
        ref W16 t0 = ref t[0];
        for (int i = 0; i < 16; i++)
            Unsafe.Add(ref w0, flipUd ? 15 - i : i) = Vector256.ShiftLeft(Vector256.LoadUnsafe(ref input, (nuint)(i * stride)), 2);
        Tx16(vKind, 16, ref w0, 13);
        RoundShift(ref w0, 16, 2);
        // transpose_16bit_16x16_avx2: the quadrants regrouped by 128-bit half, then two lane-parallel 8 x 8 transposes
        for (int i = 0; i < 8; i++)
        {
            W16 top = Unsafe.Add(ref w0, i), bot = Unsafe.Add(ref w0, 8 + i);
            Unsafe.Add(ref t0, i) = Vector256.Create(top.GetLower(), bot.GetLower());
            Unsafe.Add(ref t0, 8 + i) = Vector256.Create(top.GetUpper(), bot.GetUpper());
        }
        Transpose2x8x8(ref t0, ref w0);
        Transpose2x8x8(ref Unsafe.Add(ref t0, 8), ref Unsafe.Add(ref w0, 8));
        if (flipLr) Reverse(ref w0, 16);
        Tx16(hKind, 16, ref w0, 12);
        Store16(ref w0, 16, ref output, 16, false);
    }

    /// <summary>transpose_round_shift_8x8 / transpose_round_shift_flip_8x8 (in place): the rows' saturating rounding add and
    /// arithmetic shift by bit, then the 8 x 8 transpose (flip: the output rows reversed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeRoundShift8x8(ref W8 buf, bool flip, int bit)
    {
        var rounding = Vector256.Create((short)(1 << (bit - 1)));
        W16 s04 = Vector256.Create(buf, Unsafe.Add(ref buf, 4));
        W16 s15 = Vector256.Create(Unsafe.Add(ref buf, 1), Unsafe.Add(ref buf, 5));
        W16 s26 = Vector256.Create(Unsafe.Add(ref buf, 2), Unsafe.Add(ref buf, 6));
        W16 s37 = Vector256.Create(Unsafe.Add(ref buf, 3), Unsafe.Add(ref buf, 7));
        W16 b0 = Vector256.ShiftRightArithmetic(Avx2.AddSaturate(s04, rounding), bit);
        W16 b1 = Vector256.ShiftRightArithmetic(Avx2.AddSaturate(s15, rounding), bit);
        W16 b2 = Vector256.ShiftRightArithmetic(Avx2.AddSaturate(s26, rounding), bit);
        W16 b3 = Vector256.ShiftRightArithmetic(Avx2.AddSaturate(s37, rounding), bit);
        var aa0 = Avx2.UnpackLow(b0, b1).AsInt32();
        var aa1 = Avx2.UnpackHigh(b0, b1).AsInt32();
        var aa2 = Avx2.UnpackLow(b2, b3).AsInt32();
        var aa3 = Avx2.UnpackHigh(b2, b3).AsInt32();
        var c0 = Avx2.Permute4x64(Avx2.UnpackLow(aa0, aa2).AsInt64(), 0xD8).AsInt16();
        var c1 = Avx2.Permute4x64(Avx2.UnpackHigh(aa0, aa2).AsInt64(), 0xD8).AsInt16();
        var c2 = Avx2.Permute4x64(Avx2.UnpackLow(aa1, aa3).AsInt64(), 0xD8).AsInt16();
        var c3 = Avx2.Permute4x64(Avx2.UnpackHigh(aa1, aa3).AsInt64(), 0xD8).AsInt16();
        if (flip)
        {
            Unsafe.Add(ref buf, 7) = c0.GetLower(); Unsafe.Add(ref buf, 6) = c0.GetUpper();
            Unsafe.Add(ref buf, 5) = c1.GetLower(); Unsafe.Add(ref buf, 4) = c1.GetUpper();
            Unsafe.Add(ref buf, 3) = c2.GetLower(); Unsafe.Add(ref buf, 2) = c2.GetUpper();
            Unsafe.Add(ref buf, 1) = c3.GetLower(); buf = c3.GetUpper();
        }
        else
        {
            buf = c0.GetLower(); Unsafe.Add(ref buf, 1) = c0.GetUpper();
            Unsafe.Add(ref buf, 2) = c1.GetLower(); Unsafe.Add(ref buf, 3) = c1.GetUpper();
            Unsafe.Add(ref buf, 4) = c2.GetLower(); Unsafe.Add(ref buf, 5) = c2.GetUpper();
            Unsafe.Add(ref buf, 6) = c3.GetLower(); Unsafe.Add(ref buf, 7) = c3.GetUpper();
        }
    }
}
