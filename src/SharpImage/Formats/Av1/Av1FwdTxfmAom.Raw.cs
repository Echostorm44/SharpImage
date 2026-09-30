using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using V = System.Runtime.Intrinsics.Vector256<int>;

namespace SharpImage.Formats.Av1;

internal static partial class Av1FwdTxfmAom
{
    /// <summary>libaom's forward 2D transform (av1_fwd_txfm2d / fwd_txfm2d_c) of a w x h int16 residual (stride diffStride)
    /// into tran_low_t coefficients in libaom's layout: coeff[col * min(h, 32) + row] over the retained min(w,32) x
    /// min(h,32) region. hKind / vKind: the row / column 1D kinds (Av1InvTransform.Type1dDct / Adst / Identity; the
    /// flipped ADSTs arrive as ADST with flipUd / flipLr, as fwd_txfm2d_c flips its input).</summary>
    internal static void ForwardRaw(ReadOnlySpan<short> diff, int diffStride, int w, int h, int txSize, int hKind, int vKind,
        bool flipUd, bool flipLr, Span<int> coeff)
    {
        // 8-bit residuals: libaom's av1_lowbd_fwd_txfm (16-bit lanes) below 64-point axes, as the 8-bit encoder runs it
        if (w <= 32 && h <= 32 && Avx2.IsSupported) ForwardRawLbd(diff, diffStride, w, h, txSize, hKind, vKind, flipUd, flipLr, coeff);
        else ForwardRawRef(diff, diffStride, w, h, txSize, hKind, vKind, flipUd, flipLr, coeff);
    }

    /// <summary>ForwardRaw in 32-bit lanes (fwd_txfm2d_c's arithmetic), every size.</summary>
    [SkipLocalsInit]
    internal static void ForwardRawRef(ReadOnlySpan<short> diff, int diffStride, int w, int h, int txSize, int hKind, int vKind,
        bool flipUd, bool flipLr, Span<int> coeff)
    {
        int lw = System.Numerics.BitOperations.Log2((uint)w) - 2, lh = System.Numerics.BitOperations.Log2((uint)h) - 2;
        int sh0 = Shift[txSize * 3], sh1 = -Shift[txSize * 3 + 1], sh2 = -Shift[txSize * 3 + 2];
        int cosCol = CosBitCol[lw * 5 + lh], cosRow = CosBitRow[lw * 5 + lh];
        bool rect2 = w == 2 * h || h == 2 * w;
        int sw = Math.Min(w, 32), sh = Math.Min(h, 32), ng = (w + 7) >> 3;
        int hp = Math.Max(h, 8);
        Span<V> colS = stackalloc V[ng * hp + 64];
        ref V col = ref MemoryMarshal.GetReference(colS);
        ref V tmp = ref Unsafe.Add(ref col, ng * hp);
        Span<int> rowBuf = stackalloc int[8];
        var rnd1 = Vector256.Create(sh1 > 0 ? 1 << (sh1 - 1) : 0);
        ref short d0 = ref MemoryMarshal.GetReference(diff);
        var reverse8 = Vector128.Create((short)7, 6, 5, 4, 3, 2, 1, 0);
        for (int g = 0; g < ng; g++)
        {
            ref V cg = ref Unsafe.Add(ref col, g * hp);
            for (int r = 0; r < h; r++)
            {
                int sr = flipUd ? h - 1 - r : r;
                ref short row = ref Unsafe.Add(ref d0, sr * diffStride);
                V v;
                if (w >= 8)
                {
                    // 8 residuals of the group (flipLr: the mirrored columns, reversed)
                    Vector128<short> raw = flipLr
                        ? Vector128.Shuffle(Vector128.LoadUnsafe(ref row, (nuint)(w - 8 - g * 8)), reverse8)
                        : Vector128.LoadUnsafe(ref row, (nuint)(g * 8));
                    v = Avx2.ConvertToVector256Int32(raw);
                }
                else
                {
                    for (int j = 0; j < 4; j++) rowBuf[j] = Unsafe.Add(ref row, flipLr ? 3 - j : j);
                    rowBuf.Slice(4).Clear();
                    v = Vector256.Create<int>(rowBuf);
                }
                Unsafe.Add(ref tmp, r) = Vector256.ShiftLeft(v, sh0);
            }
            Txfm1d(vKind, h, ref tmp, ref cg, cosCol);
            for (int r = 0; r < h; r++)
            {
                ref V o = ref Unsafe.Add(ref cg, r);
                if (sh1 > 0) o = Vector256.ShiftRightArithmetic(o + rnd1, sh1);
            }
            for (int r = h; r < hp; r++) Unsafe.Add(ref cg, r) = V.Zero;
        }
        Span<V> rinS = stackalloc V[64];
        ref V rin = ref MemoryMarshal.GetReference(rinS);
        var rnd2 = Vector256.Create(sh2 > 0 ? 1 << (sh2 - 1) : 0);
        Span<int> lanes = stackalloc int[8];
        for (int r0 = 0; r0 < sh; r0 += 8)
        {
            for (int g = 0; g < ng; g++)
            {
                ref V src = ref Unsafe.Add(ref col, g * hp + r0);
                ref V dst = ref Unsafe.Add(ref rin, g * 8);
                for (int j = 0; j < 8; j++) Unsafe.Add(ref dst, j) = Unsafe.Add(ref src, j);
                Transpose8(ref dst);
            }
            Txfm1d(hKind, w, ref rin, ref tmp, cosRow);
            int nr = Math.Min(8, h - r0);
            for (int c = 0; c < sw; c++)
            {
                V v = Unsafe.Add(ref tmp, c);
                if (sh2 > 0) v = Vector256.ShiftRightArithmetic(v + rnd2, sh2);
                if (rect2) v = MulRound(v, 5793, 12);
                int rc = c * sh + r0;
                if (nr == 8) v.StoreUnsafe(ref MemoryMarshal.GetReference(coeff), (nuint)rc);
                else
                {
                    v.CopyTo(lanes);
                    for (int k = 0; k < nr; k++) coeff[rc + k] = lanes[k];
                }
            }
        }
    }
}
