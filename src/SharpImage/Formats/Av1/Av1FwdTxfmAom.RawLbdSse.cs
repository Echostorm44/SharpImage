using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using W8 = System.Runtime.Intrinsics.Vector128<short>;

namespace SharpImage.Formats.Av1;

internal static partial class Av1FwdTxfmAom
{
    /// <summary><see cref="ForwardRawLbd"/> in 8-lane (SSE2..4.1) vectors only, for CPUs / builds without AVX2 (the default
    /// Native AOT instruction set): the column pass 8 columns at a time, the row pass 8 rows at a time, through the same
    /// 16-bit kernels (TxW8: what the 16-lane kernels compute per lane), so the coefficients are the same.</summary>
    [SkipLocalsInit]
    internal static void ForwardRawLbdSse(ReadOnlySpan<short> diff, int diffStride, int w, int h, int txSize, int hKind, int vKind,
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
        Span<W8> bufS = stackalloc W8[64];
        ref W8 ci = ref bufS[0];
        ref W8 co = ref bufS[32];
        var r1 = Vector128.Create((short)(sh1 > 0 ? 1 << (sh1 - 1) : 0));
        var rev8 = Vector128.Create((short)7, 6, 5, 4, 3, 2, 1, 0);
        var rev4 = Vector128.Create((short)3, 2, 1, 0, 4, 5, 6, 7);
        for (int c0 = 0; c0 < w; c0 += 8)
        {
            for (int r = 0; r < h; r++)
            {
                ref short row = ref Unsafe.Add(ref d0, (flipUd ? h - 1 - r : r) * diffStride);
                W8 v;
                if (w >= 8) v = flipLr ? Vector128.Shuffle(Vector128.LoadUnsafe(ref row, (nuint)(w - 8 - c0)), rev8) : Vector128.LoadUnsafe(ref row, (nuint)c0);
                else
                {
                    v = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref row))).AsInt16();
                    if (flipLr) v = Vector128.Shuffle(v, rev4);
                }
                Unsafe.Add(ref ci, r) = Vector128.ShiftLeft(v, sh0);
            }
            TxW8(vKind, h, ref ci, ref co, cosCol);
            for (int r = 0; r < h; r++) RShift(Unsafe.Add(ref co, r), r1, sh1).StoreUnsafe(ref cb, (nuint)(r * cs + c0));
        }

        // row pass: 8 rows as lanes at a time; each column's output rc-contiguous (every row retained: h <= 32)
        int sh = h;
        ref int o0 = ref MemoryMarshal.GetReference(coeff);
        var rs = Vector128.Create(5793);
        var rr = Vector128.Create(1 << 11);
        var r2 = Vector128.Create((short)(sh2 > 0 ? 1 << (sh2 - 1) : 0));
        Span<W8> blk = stackalloc W8[8];
        ref W8 bk = ref MemoryMarshal.GetReference(blk);
        for (int r0 = 0; r0 < hp; r0 += 8)
        {
            for (int c0 = 0; c0 < w; c0 += 8)
            {
                Transpose8x8S(ref Unsafe.Add(ref cb, r0 * cs + c0), cs, ref bk);
                int nc = Math.Min(8, w - c0);
                for (int j = 0; j < nc; j++) Unsafe.Add(ref ci, c0 + j) = Unsafe.Add(ref bk, j);
            }
            TxW8(hKind, w, ref ci, ref co, cosRow);
            for (int c = 0; c < w; c++)
            {
                W8 v = RShift(Unsafe.Add(ref co, c), r2, sh2);
                var lo = Sse41.ConvertToVector128Int32(v);
                var hi = Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(v, 8));
                if (rect2)
                {
                    lo = Vector128.ShiftRightArithmetic(lo * rs + rr, 12);
                    hi = Vector128.ShiftRightArithmetic(hi * rs + rr, 12);
                }
                int rc = c * sh + r0;
                lo.StoreUnsafe(ref o0, (nuint)rc);
                if (h > 4) hi.StoreUnsafe(ref o0, (nuint)(rc + 4));
            }
        }
    }
}
