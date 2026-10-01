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
internal static partial class Av1FwdTxfmAom
{
    [InlineArray(8)] private struct Buf8 { private W8 e; }
    [InlineArray(16)] private struct Buf16 { private W8 e; }
    [InlineArray(16)] private struct Buf16W { private W16 e; }

    /// <summary>The specialised lowbd driver for txSize when there is one (true), else false (ForwardRawLbd's generic path).
    /// out: coefficients in libaom's layout (column-major, rows contiguous).</summary>
    private static bool ForwardLbdSized(ref short input, int stride, int txSize, int hKind, int vKind, bool flipUd, bool flipLr,
        ref int output)
    {
        switch (txSize)
        {
            case 1: Fwd8x8(ref input, stride, hKind, vKind, flipUd, flipLr, ref output); return true;
            default: return false;
        }
    }

    // 1D kernels over 8 lanes by kind (col_txfm8x8_arr / row_txfm8x8_arr: fdct8x8 / fadst8x8 / fidentity8x8_new_sse2)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tx8W8(int kind, ref W8 buf, int cosBit)
    {
        if (kind == Av1InvTransform.Type1dDct) Fdct8W8(ref buf, ref buf, cosBit);
        else if (kind == Av1InvTransform.Type1dAdst) Fadst8W8(ref buf, ref buf, cosBit);
        else for (int i = 0; i < 8; i++) Unsafe.Add(ref buf, i) = AddS(Unsafe.Add(ref buf, i), Unsafe.Add(ref buf, i));
    }

    /// <summary>av1_lowbd_fwd_txfm2d_8x8_avx2 (shift {2, -1, 0}, cos bits 13 / 13).</summary>
    [SkipLocalsInit]
    private static void Fwd8x8(ref short input, int stride, int hKind, int vKind, bool flipUd, bool flipLr, ref int output)
    {
        Buf8 b = default;
        ref W8 b0 = ref b[0];
        // load_buffer(_and_flip)_round_shift
        if (flipUd)
            for (int i = 0; i < 8; i++) Unsafe.Add(ref b0, 7 - i) = Vector128.ShiftLeft(Vector128.LoadUnsafe(ref input, (nuint)(i * stride)), 2);
        else
            for (int i = 0; i < 8; i++) Unsafe.Add(ref b0, i) = Vector128.ShiftLeft(Vector128.LoadUnsafe(ref input, (nuint)(i * stride)), 2);
        Tx8W8(vKind, ref b0, 13);
        TransposeRoundShift8x8(ref b0, flipLr, 1);
        Tx8W8(hKind, ref b0, 13);
        // store_buffer_16bit_to_32bit_w8_avx2
        for (int i = 0; i < 8; i++) Avx2.ConvertToVector256Int32(Unsafe.Add(ref b0, i)).StoreUnsafe(ref output, (nuint)(i * 8));
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
