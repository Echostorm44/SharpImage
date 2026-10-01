using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// High bit depth pieces of av1/encoder/encodemb.c and av1/common/idct.c: av1_inverse_transform_block's highbd branch
// (av1_highbd_inv_txfm_add) on 16-bit samples, with the lossless Walsh-Hadamard inverse (av1_highbd_iwht4x4_add).
internal static partial class AomEncodeMb
{
    /// <summary>av1_inverse_transform_block (high bit depth): the dequantised coefficients' inverse added into the 16-bit
    /// dst (coefficients untouched). eob: libaom's count (0 = none).</summary>
    internal static void InverseTransformBlock(int[] dqcoeff, int dqOff, int txType, int txSize, ushort[] dst, int dstOff, int dstStride,
        int eob, int bd, bool lossless)
    {
        if (eob == 0) return;
        if (lossless && txSize == TX_4X4) { IwhtAdd4x4(dqcoeff, dqOff, dst, dstOff, dstStride, eob, bd); return; }
        Av1InvTransform.InvTxfmAdd16(dst.AsSpan(dstOff), dstStride, dqcoeff.AsSpan(dqOff, MaxEob(txSize)), eob - 1, txSize,
            Av1InvTransform.TxShift[txSize], (Av1TxType)txType, bd, preserveCoeffs: true);
    }

    /// <summary>av1_inverse_transform_block into a buf_2d: the 8-bit or the high bit depth path by the buffer.</summary>
    internal static void InverseTransformBlockDst(int[] dqcoeff, int dqOff, int txType, int txSize, in AomBuf2d dst, int dstOff, int dstStride,
        int eob, int bd, bool lossless)
    {
        if (dst.Buf16 != null) InverseTransformBlock(dqcoeff, dqOff, txType, txSize, dst.Buf16, dstOff, dstStride, eob, bd, lossless);
        else InverseTransformBlock(dqcoeff, dqOff, txType, txSize, dst.Buf, dstOff, dstStride, eob, lossless);
    }

    /// <summary>av1_highbd_iwht4x4_add: eob &gt; 1 runs av1_highbd_iwht4x4_16_add, else av1_highbd_iwht4x4_1_add.</summary>
    internal static void IwhtAdd4x4(int[] input, int inOff, ushort[] dest, int destOff, int stride, int eob, int bd)
    {
        if (eob > 1)
        {
            Span<int> output = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a1 = input[inOff + i] >> UnitQuantShift;
                int c1 = input[inOff + 4 + i] >> UnitQuantShift;
                int d1 = input[inOff + 8 + i] >> UnitQuantShift;
                int b1 = input[inOff + 12 + i] >> UnitQuantShift;
                a1 += c1;
                d1 -= b1;
                int e1 = (a1 - d1) >> 1;
                b1 = e1 - b1;
                c1 = e1 - c1;
                a1 -= b1;
                d1 += c1;
                output[i] = a1; output[4 + i] = b1; output[8 + i] = c1; output[12 + i] = d1;
            }
            for (int i = 0; i < 4; i++)
            {
                int a1 = output[i * 4], c1 = output[i * 4 + 1], d1 = output[i * 4 + 2], b1 = output[i * 4 + 3];
                a1 += c1;
                d1 -= b1;
                int e1 = (a1 - d1) >> 1;
                b1 = e1 - b1;
                c1 = e1 - c1;
                a1 -= b1;
                d1 += c1;
                int o = destOff + i;
                dest[o] = AomHbd.ClipPixel(dest[o] + a1, bd);
                dest[o + stride] = AomHbd.ClipPixel(dest[o + stride] + b1, bd);
                dest[o + 2 * stride] = AomHbd.ClipPixel(dest[o + 2 * stride] + c1, bd);
                dest[o + 3 * stride] = AomHbd.ClipPixel(dest[o + 3 * stride] + d1, bd);
            }
        }
        else
        {
            Span<int> tmp = stackalloc int[4];
            int a1 = input[inOff] >> UnitQuantShift;
            int e1 = a1 >> 1;
            a1 -= e1;
            tmp[0] = a1;
            tmp[1] = tmp[2] = tmp[3] = e1;
            for (int i = 0; i < 4; i++)
            {
                e1 = tmp[i] >> 1;
                a1 = tmp[i] - e1;
                int o = destOff + i;
                dest[o] = AomHbd.ClipPixel(dest[o] + a1, bd);
                dest[o + stride] = AomHbd.ClipPixel(dest[o + stride] + e1, bd);
                dest[o + 2 * stride] = AomHbd.ClipPixel(dest[o + 2 * stride] + e1, bd);
                dest[o + 3 * stride] = AomHbd.ClipPixel(dest[o + 3 * stride] + e1, bd);
            }
        }
    }
}
