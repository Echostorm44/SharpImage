using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using W8 = System.Runtime.Intrinsics.Vector128<short>;
using W16 = System.Runtime.Intrinsics.Vector256<short>;

namespace SharpImage.Formats.Av1;

internal static partial class Av1FwdTxfmAom
{
    private static readonly Vector256<byte> Reverse16Bytes = Vector256.Create(
        (byte)14, 15, 12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1, 14, 15, 12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1);

    /// <summary>ForwardRaw through libaom's lowbd transform (av1_lowbd_fwd_txfm_avx2 / _sse2, the 8-bit encoder's), for
    /// w and h at most 32: the residual and every intermediate in 16-bit lanes. Same arguments and output layout as
    /// ForwardRaw, whose values it reproduces (8-bit residuals never saturate a 16-bit intermediate).</summary>
    [SkipLocalsInit]
    internal static void ForwardRawLbd(ReadOnlySpan<short> diff, int diffStride, int w, int h, int txSize, int hKind, int vKind,
        bool flipUd, bool flipLr, Span<int> coeff)
    {
        int lw = System.Numerics.BitOperations.Log2((uint)w) - 2, lh = System.Numerics.BitOperations.Log2((uint)h) - 2;
        int sh0 = Shift[txSize * 3], sh1 = -Shift[txSize * 3 + 1], sh2 = -Shift[txSize * 3 + 2];
        int cosCol = CosBitCol[lw * 5 + lh], cosRow = CosBitRow[lw * 5 + lh];
        bool rect2 = w == 2 * h || h == 2 * w;
        // the column pass's output, row-major (stride cs), rows past h zero
        int cs = Math.Max(w, 8), hp = Math.Max(h, 8);
        Span<short> cbS = stackalloc short[cs * hp];
        if (h < hp) cbS.Slice(h * cs).Clear();
        ref short cb = ref MemoryMarshal.GetReference(cbS);
        ref short d0 = ref MemoryMarshal.GetReference(diff);
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
                    ref short row = ref Unsafe.Add(ref d0, (flipUd ? h - 1 - r : r) * diffStride);
                    W16 v;
                    if (flipLr)
                    {
                        v = Avx2.Shuffle(Vector256.LoadUnsafe(ref row, (nuint)(w - 16 - c0)).AsByte(), Reverse16Bytes).AsInt16();
                        v = Avx2.Permute4x64(v.AsInt64(), 0x4E).AsInt16();
                    }
                    else v = Vector256.LoadUnsafe(ref row, (nuint)c0);
                    Unsafe.Add(ref bi, r) = Vector256.ShiftLeft(v, sh0);
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
            var rev8 = Vector128.Create((short)7, 6, 5, 4, 3, 2, 1, 0);
            var rev4 = Vector128.Create((short)3, 2, 1, 0, 4, 5, 6, 7);
            for (int r = 0; r < h; r++)
            {
                ref short row = ref Unsafe.Add(ref d0, (flipUd ? h - 1 - r : r) * diffStride);
                W8 v;
                if (w == 8) v = flipLr ? Vector128.Shuffle(Vector128.LoadUnsafe(ref row), rev8) : Vector128.LoadUnsafe(ref row);
                else
                {
                    v = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref row))).AsInt16();
                    if (flipLr) v = Vector128.Shuffle(v, rev4);
                }
                Unsafe.Add(ref ci, r) = Vector128.ShiftLeft(v, sh0);
            }
            TxW8(vKind, h, ref ci, ref co, cosCol);
            for (int r = 0; r < h; r++) RShift(Unsafe.Add(ref co, r), r1, sh1).StoreUnsafe(ref cb, (nuint)(r * cs));
        }

        // row pass: rows as lanes (16 at a time from 16 tall, else 8); each column's output rc-contiguous
        int sh = h;   // at most 32: every row retained
        ref int o0 = ref MemoryMarshal.GetReference(coeff);
        var rs = Vector256.Create(5793);
        var rr = Vector256.Create(1 << 11);
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
                    var lo = Avx2.ConvertToVector256Int32(v.GetLower());
                    var hi = Avx2.ConvertToVector256Int32(v.GetUpper());
                    if (rect2)
                    {
                        lo = Vector256.ShiftRightArithmetic(lo * rs + rr, 12);
                        hi = Vector256.ShiftRightArithmetic(hi * rs + rr, 12);
                    }
                    int rc = c * sh + r0;
                    lo.StoreUnsafe(ref o0, (nuint)rc);
                    hi.StoreUnsafe(ref o0, (nuint)(rc + 8));
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
            {
                var v = Avx2.ConvertToVector256Int32(RShift(Unsafe.Add(ref ro, c), r2, sh2));
                if (rect2) v = Vector256.ShiftRightArithmetic(v * rs + rr, 12);
                if (h == 8) v.StoreUnsafe(ref o0, (nuint)(c * 8));
                else v.GetLower().StoreUnsafe(ref o0, (nuint)(c * 4));
            }
        }
    }
}
