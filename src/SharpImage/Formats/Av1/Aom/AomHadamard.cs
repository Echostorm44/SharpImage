using System;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 aom_dsp/avg.c: the 8-bit Hadamard transforms (aom_hadamard_4x4 / 8x8 / 16x16 / 32x32, whose C
// versions reproduce the SSE2 / AVX2 kernels' output order and int16 arithmetic) and av1_quick_txfm's Hadamard path.
internal static class AomHadamard
{
    private static void Col4(ReadOnlySpan<short> src, int stride, Span<short> coeff)
    {
        short b0 = (short)((src[0 * stride] + src[1 * stride]) >> 1);
        short b1 = (short)((src[0 * stride] - src[1 * stride]) >> 1);
        short b2 = (short)((src[2 * stride] + src[3 * stride]) >> 1);
        short b3 = (short)((src[2 * stride] - src[3 * stride]) >> 1);
        coeff[0] = (short)(b0 + b2);
        coeff[1] = (short)(b1 + b3);
        coeff[2] = (short)(b0 - b2);
        coeff[3] = (short)(b1 - b3);
    }

    /// <summary>aom_hadamard_4x4.</summary>
    internal static void H4x4(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<short> buffer = stackalloc short[16];
        Span<short> buffer2 = stackalloc short[16];
        for (int idx = 0; idx < 4; ++idx) Col4(srcDiff.Slice(idx), srcStride, buffer.Slice(4 * idx));
        for (int idx = 0; idx < 4; ++idx) Col4(buffer.Slice(idx), 4, buffer2.Slice(4 * idx));
        // extra transpose to match SSE2
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) coeff[i * 4 + j] = buffer2[j * 4 + i];
    }

    private static void Col8(ReadOnlySpan<short> src, int stride, Span<short> coeff)
    {
        short b0 = (short)(src[0 * stride] + src[1 * stride]);
        short b1 = (short)(src[0 * stride] - src[1 * stride]);
        short b2 = (short)(src[2 * stride] + src[3 * stride]);
        short b3 = (short)(src[2 * stride] - src[3 * stride]);
        short b4 = (short)(src[4 * stride] + src[5 * stride]);
        short b5 = (short)(src[4 * stride] - src[5 * stride]);
        short b6 = (short)(src[6 * stride] + src[7 * stride]);
        short b7 = (short)(src[6 * stride] - src[7 * stride]);
        short c0 = (short)(b0 + b2), c1 = (short)(b1 + b3), c2 = (short)(b0 - b2), c3 = (short)(b1 - b3);
        short c4 = (short)(b4 + b6), c5 = (short)(b5 + b7), c6 = (short)(b4 - b6), c7 = (short)(b5 - b7);
        coeff[0] = (short)(c0 + c4);
        coeff[7] = (short)(c1 + c5);
        coeff[3] = (short)(c2 + c6);
        coeff[4] = (short)(c3 + c7);
        coeff[2] = (short)(c0 - c4);
        coeff[6] = (short)(c1 - c5);
        coeff[1] = (short)(c2 - c6);
        coeff[5] = (short)(c3 - c7);
    }

    /// <summary>aom_hadamard_8x8.</summary>
    internal static void H8x8(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<short> buffer = stackalloc short[64];
        Span<short> buffer2 = stackalloc short[64];
        for (int idx = 0; idx < 8; ++idx) Col8(srcDiff.Slice(idx), srcStride, buffer.Slice(8 * idx));
        for (int idx = 0; idx < 8; ++idx) Col8(buffer.Slice(idx), 8, buffer2.Slice(8 * idx));
        // extra transpose to match SSE2
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++) coeff[i * 8 + j] = buffer2[j * 8 + i];
    }

    /// <summary>aom_hadamard_16x16 (in the AVX2 kernel's output order).</summary>
    internal static void H16x16(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            H8x8(srcDiff.Slice((idx >> 1) * 8 * srcStride + (idx & 1) * 8), srcStride, coeff.Slice(idx * 64));
        for (int idx = 0; idx < 64; ++idx)
        {
            int a0 = coeff[idx], a1 = coeff[idx + 64], a2 = coeff[idx + 128], a3 = coeff[idx + 192];
            int b0 = (a0 + a1) >> 1, b1 = (a0 - a1) >> 1, b2 = (a2 + a3) >> 1, b3 = (a2 - a3) >> 1;
            coeff[idx] = b0 + b2;
            coeff[idx + 64] = b1 + b3;
            coeff[idx + 128] = b0 - b2;
            coeff[idx + 192] = b1 - b3;
        }
        // extra shift to match AVX2 output
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 4; j++)
            {
                int temp = coeff[i * 16 + 4 + j];
                coeff[i * 16 + 4 + j] = coeff[i * 16 + 8 + j];
                coeff[i * 16 + 8 + j] = temp;
            }
    }

    /// <summary>aom_hadamard_32x32.</summary>
    internal static void H32x32(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            H16x16(srcDiff.Slice((idx >> 1) * 16 * srcStride + (idx & 1) * 16), srcStride, coeff.Slice(idx * 256));
        for (int idx = 0; idx < 256; ++idx)
        {
            int a0 = coeff[idx], a1 = coeff[idx + 256], a2 = coeff[idx + 512], a3 = coeff[idx + 768];
            int b0 = (a0 + a1) >> 2, b1 = (a0 - a1) >> 2, b2 = (a2 + a3) >> 2, b3 = (a2 - a3) >> 2;
            coeff[idx] = b0 + b2;
            coeff[idx + 256] = b1 + b3;
            coeff[idx + 512] = b0 - b2;
            coeff[idx + 768] = b1 - b3;
        }
    }

    /// <summary>wht_fwd_txfm (av1_quick_txfm with use_hadamard, 8-bit).</summary>
    internal static void WhtFwdTxfm(int txSize, ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        switch (txSize)
        {
            case AomTables.TX_4X4: H4x4(srcDiff, srcStride, coeff); break;
            case AomTables.TX_8X8: H8x8(srcDiff, srcStride, coeff); break;
            case AomTables.TX_16X16: H16x16(srcDiff, srcStride, coeff); break;
            case AomTables.TX_32X32: H32x32(srcDiff, srcStride, coeff); break;
            default: throw new ArgumentOutOfRangeException(nameof(txSize));
        }
    }
}
