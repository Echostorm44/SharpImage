using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

// High bit depth (10 / 12-bit, CONVERT_TO_SHORTPTR buffers) DSP kernels of libaom 3.14.1 as the encoder dispatches them
// on an AVX2 machine (aom_dsp/variance.c, x86/highbd_variance_{sse2,sse4,avx2}.c, x86/sse_avx2.c, ...). The SIMD kernels
// libaom picks for these compute the C reference's exact integer sums (their 16 / 32-bit lanes never overflow for 10 /
// 12-bit samples), so the C semantics are ported; where a kernel differs from its C twin, its own file says so.
internal static class AomHbd
{
    /// <summary>aom_highbd_{8,10,12}_variance{W}x{H}: the exact sum and SSE, rounded to the 8-bit scale ((bd - 8) and
    /// 2 (bd - 8) bits), var = sse - sum^2 / (w h) clamped at 0. b == null: against the constant bConst (var_offs).</summary>
    internal static uint Variance(ushort[] a, int aOff, int aStride, ushort[]? b, int bOff, int bStride, int bConst, int w, int h, int bd, out uint sse)
    {
        long sum = 0;
        ulong ss = 0;
        for (int r = 0; r < h; r++)
        {
            int ar = aOff + r * aStride, br = bOff + r * bStride;
            for (int c = 0; c < w; c++)
            {
                int d = a[ar + c] - (b != null ? b[br + c] : bConst);
                sum += d;
                ss += (ulong)((long)d * d);
            }
        }
        return Finish(ss, sum, w * h, bd, out sse);
    }

    /// <summary>The bit-depth rounding and variance of exact (sse, sum) sums over n samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Finish(ulong ss, long sum, int n, int bd, out uint sse)
    {
        if (bd == 8)
        {
            sse = (uint)ss;
            return sse - (uint)(sum * sum / n);
        }
        int sh = bd == 10 ? 2 : 4;
        long rsum = (sum + (1L << (sh - 1))) >> sh;               // ROUND_POWER_OF_TWO(sum_long, 2 / 4)
        sse = (uint)((ss + (1UL << (2 * sh - 1))) >> (2 * sh));   // ROUND_POWER_OF_TWO(sse_long, 4 / 8)
        long var = (long)sse - rsum * rsum / n;
        return var >= 0 ? (uint)var : 0;
    }

    /// <summary>aom_highbd_sse: the exact SSE of two w x h blocks.</summary>
    internal static long Sse(ushort[] a, int aOff, int aStride, ushort[] b, int bOff, int bStride, int width, int height)
    {
        long sse = 0;
        for (int y = 0; y < height; y++)
        {
            int ar = aOff + y * aStride, br = bOff + y * bStride;
            for (int x = 0; x < width; x++) { int d = a[ar + x] - b[br + x]; sse += d * d; }
        }
        return sse;
    }

    /// <summary>av1_get_perpixel_variance with use_hbd: the plane block's variance against 128 &lt;&lt; (bd - 8) (get_var_offs),
    /// rounded per pixel.</summary>
    internal static uint PerpixelVariance(ushort[] buf, int off, int stride, int w, int h, int bd)
    {
        uint var = Variance(buf, off, stride, null, 0, 0, 128 << (bd - 8), w, h, bd, out _);
        int log2 = System.Numerics.BitOperations.Log2((uint)(w * h));
        return (uint)((var + (1u << (log2 - 1))) >> log2);
    }

    /// <summary>aom_highbd_subtract_block: diff = src - pred over rows x cols.</summary>
    internal static void SubtractBlock(int rows, int cols, short[] diff, int diffOff, int diffStride,
        ushort[] src, int srcOff, int srcStride, ushort[] pred, int predOff, int predStride)
    {
        for (int r = 0; r < rows; r++)
        {
            int d = diffOff + r * diffStride, s = srcOff + r * srcStride, p = predOff + r * predStride;
            for (int c = 0; c < cols; c++) diff[d + c] = (short)(src[s + c] - pred[p + c]);
        }
    }

    /// <summary>A w x h 16-bit block copy.</summary>
    internal static void CopyBlock(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h)
    {
        for (int r = 0; r < h; r++) Array.Copy(src, srcOff + r * srcStride, dst, dstOff + r * dstStride, w);
    }

    /// <summary>av1_highbd_block_error (AVX2: exact 64-bit products): the coefficient error and energy, each rounded down
    /// by 2 (bd - 8) bits.</summary>
    internal static long BlockError(ReadOnlySpan<int> coeff, ReadOnlySpan<int> dqcoeff, int blockSize, out long ssz, int bd)
    {
        long error = 0, sqcoeff = 0;
        int shift = 2 * (bd - 8);
        int rounding = (1 << shift) >> 1;
        for (int i = 0; i < blockSize; i++)
        {
            long diff = coeff[i] - dqcoeff[i];
            error += diff * diff;
            sqcoeff += (long)coeff[i] * coeff[i];
        }
        ssz = (sqcoeff + rounding) >> shift;
        return (error + rounding) >> shift;
    }

    // ---- aom_dsp/avg.c: the high bit depth Hadamard transforms (the coefficient order of libaom's SIMD versions differs,
    //      but only their absolute sum (aom_satd) is used on the all-intra path) ----

    private static void HadamardHighbdCol8FirstPass(ReadOnlySpan<short> s, int stride, Span<short> coeff)
    {
        short b0 = (short)(s[0] + s[stride]), b1 = (short)(s[0] - s[stride]);
        short b2 = (short)(s[2 * stride] + s[3 * stride]), b3 = (short)(s[2 * stride] - s[3 * stride]);
        short b4 = (short)(s[4 * stride] + s[5 * stride]), b5 = (short)(s[4 * stride] - s[5 * stride]);
        short b6 = (short)(s[6 * stride] + s[7 * stride]), b7 = (short)(s[6 * stride] - s[7 * stride]);
        short c0 = (short)(b0 + b2), c1 = (short)(b1 + b3), c2 = (short)(b0 - b2), c3 = (short)(b1 - b3);
        short c4 = (short)(b4 + b6), c5 = (short)(b5 + b7), c6 = (short)(b4 - b6), c7 = (short)(b5 - b7);
        coeff[0] = (short)(c0 + c4); coeff[7] = (short)(c1 + c5); coeff[3] = (short)(c2 + c6); coeff[4] = (short)(c3 + c7);
        coeff[2] = (short)(c0 - c4); coeff[6] = (short)(c1 - c5); coeff[1] = (short)(c2 - c6); coeff[5] = (short)(c3 - c7);
    }

    private static void HadamardHighbdCol8SecondPass(ReadOnlySpan<short> s, int stride, Span<int> coeff)
    {
        int b0 = s[0] + s[stride], b1 = s[0] - s[stride], b2 = s[2 * stride] + s[3 * stride], b3 = s[2 * stride] - s[3 * stride];
        int b4 = s[4 * stride] + s[5 * stride], b5 = s[4 * stride] - s[5 * stride], b6 = s[6 * stride] + s[7 * stride], b7 = s[6 * stride] - s[7 * stride];
        int c0 = b0 + b2, c1 = b1 + b3, c2 = b0 - b2, c3 = b1 - b3, c4 = b4 + b6, c5 = b5 + b7, c6 = b4 - b6, c7 = b5 - b7;
        coeff[0] = c0 + c4; coeff[7] = c1 + c5; coeff[3] = c2 + c6; coeff[4] = c3 + c7;
        coeff[2] = c0 - c4; coeff[6] = c1 - c5; coeff[1] = c2 - c6; coeff[5] = c3 - c7;
    }

    /// <summary>aom_highbd_hadamard_8x8_c.</summary>
    internal static void Hadamard8x8(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<short> buffer = stackalloc short[64];
        for (int idx = 0; idx < 8; ++idx) HadamardHighbdCol8FirstPass(srcDiff.Slice(idx), srcStride, buffer.Slice(idx * 8, 8));
        for (int idx = 0; idx < 8; ++idx) HadamardHighbdCol8SecondPass(buffer.Slice(idx), 8, coeff.Slice(8 * idx, 8));
    }

    /// <summary>aom_highbd_hadamard_16x16_c.</summary>
    internal static void Hadamard16x16(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            Hadamard8x8(srcDiff.Slice((idx >> 1) * 8 * srcStride + (idx & 1) * 8), srcStride, coeff.Slice(idx * 64, 64));
        for (int idx = 0; idx < 64; ++idx)
        {
            int a0 = coeff[idx], a1 = coeff[idx + 64], a2 = coeff[idx + 128], a3 = coeff[idx + 192];
            int b0 = (a0 + a1) >> 1, b1 = (a0 - a1) >> 1, b2 = (a2 + a3) >> 1, b3 = (a2 - a3) >> 1;
            coeff[idx] = b0 + b2; coeff[idx + 64] = b1 + b3; coeff[idx + 128] = b0 - b2; coeff[idx + 192] = b1 - b3;
        }
    }

    /// <summary>aom_highbd_hadamard_32x32_c.</summary>
    internal static void Hadamard32x32(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            Hadamard16x16(srcDiff.Slice((idx >> 1) * 16 * srcStride + (idx & 1) * 16), srcStride, coeff.Slice(idx * 256, 256));
        for (int idx = 0; idx < 256; ++idx)
        {
            int a0 = coeff[idx], a1 = coeff[idx + 256], a2 = coeff[idx + 512], a3 = coeff[idx + 768];
            int b0 = (a0 + a1) >> 2, b1 = (a0 - a1) >> 2, b2 = (a2 + a3) >> 2, b3 = (a2 - a3) >> 2;
            coeff[idx] = b0 + b2; coeff[idx + 256] = b1 + b3; coeff[idx + 512] = b0 - b2; coeff[idx + 768] = b1 - b3;
        }
    }

    /// <summary>highbd_wht_fwd_txfm (av1_quick_txfm with use_hadamard on a high bit depth buffer; 4x4 is the lowbd kernel).</summary>
    internal static void WhtFwdTxfm(int txSize, ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        switch (txSize)
        {
            case AomTables.TX_4X4: AomHadamard.WhtFwdTxfm(txSize, srcDiff, srcStride, coeff); break;
            case AomTables.TX_8X8: Hadamard8x8(srcDiff, srcStride, coeff); break;
            case AomTables.TX_16X16: Hadamard16x16(srcDiff, srcStride, coeff); break;
            case AomTables.TX_32X32: Hadamard32x32(srcDiff, srcStride, coeff); break;
            default: throw new ArgumentOutOfRangeException(nameof(txSize));
        }
    }

    /// <summary>clip_pixel_highbd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ClipPixel(int val, int bd) => (ushort)Math.Clamp(val, 0, (1 << bd) - 1);
}
