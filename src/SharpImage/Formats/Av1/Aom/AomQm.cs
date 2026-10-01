using System;
using System.IO;
using System.IO.Compression;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1's quantization matrix plumbing: av1_qm_init's per tx size matrices (av1/common/quant_common.c),
// av1_get_qmatrix / av1_get_iqmatrix (flat for 1D and identity transforms), and the C quantizers libaom runs whenever
// matrices are in use (there are no SIMD versions): quantize_fp_helper_c (av1/encoder/av1_quantize.c) and
// aom_quantize_b_helper_c (aom_dsp/quantize.c).
internal static partial class AomQm
{
    private const int QmTotalSize = 3344, NumQmLevels = 16, AomQmBits = 5;
    // [level 0..14][plane type][tx size] -> matrix (null for level 15)
    private static readonly byte[]?[] Wt = Build(WtZ), Iwt = Build(IwtZ);

    private static byte[]?[] Build(string z)
    {
        byte[] raw;
        using (var ms = new MemoryStream(Convert.FromBase64String(z)))
        using (var zs = new ZLibStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            zs.CopyTo(outMs);
            raw = outMs.ToArray();
        }
        if (raw.Length != 15 * 2 * QmTotalSize) throw new InvalidDataException("qm tables");
        var t = new byte[]?[(NumQmLevels - 1) * 2 * TX_SIZES_ALL];
        for (int q = 0; q < NumQmLevels - 1; q++)
            for (int c = 0; c < 2; c++)
            {
                int current = 0;
                for (int tx = 0; tx < TX_SIZES_ALL; tx++)
                {
                    int qmTx = AdjustedTxSize(tx);
                    if (tx != qmTx) { t[(q * 2 + c) * TX_SIZES_ALL + tx] = t[(q * 2 + c) * TX_SIZES_ALL + qmTx]; continue; }
                    int size = TxSize2d[tx];
                    var m = new byte[size];
                    Array.Copy(raw, (q * 2 + c) * QmTotalSize + current, m, 0, size);
                    t[(q * 2 + c) * TX_SIZES_ALL + tx] = m;
                    current += size;
                }
                if (current != QmTotalSize) throw new InvalidDataException("qm layout");
            }
        return t;
    }

    /// <summary>av1_get_adjusted_tx_size.</summary>
    internal static int AdjustedTxSize(int tx) => tx switch
    {
        TX_64X64 or TX_64X32 or TX_32X64 => TX_32X32,
        TX_64X16 => TX_32X16,
        TX_16X64 => TX_16X32,
        _ => tx,
    };

    /// <summary>av1_get_qmatrix: the plane's matrix at its level (xd->plane[].seg_qmatrix), flat (null) for 1D and
    /// identity transforms and for level 15.</summary>
    internal static byte[]? Qmatrix(int level, int plane, int txSize, int txType)
        => txType < IDTX && level < NumQmLevels - 1 ? Wt[(level * 2 + (plane >= 1 ? 1 : 0)) * TX_SIZES_ALL + txSize] : null;

    /// <summary>av1_get_iqmatrix.</summary>
    internal static byte[]? Iqmatrix(int level, int plane, int txSize, int txType)
        => txType < IDTX && level < NumQmLevels - 1 ? Iwt[(level * 2 + (plane >= 1 ? 1 : 0)) * TX_SIZES_ALL + txSize] : null;

    /// <summary>quantize_fp_helper_c with matrices. Returns the eob.</summary>
    internal static int QuantizeFpHelper(ReadOnlySpan<int> coeff, int nCoeffs, ReadOnlySpan<ushort> scan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        int eob = -1;
        int r0 = (round0 + ((1 << logScale) >> 1)) >> logScale, r1 = (round1 + ((1 << logScale) >> 1)) >> logScale;
        qcoeff.Slice(0, nCoeffs).Clear();
        dqcoeff.Slice(0, nCoeffs).Clear();
        for (int i = 0; i < nCoeffs; i++)
        {
            int rc = scan[i];
            int c = coeff[rc];
            int wt = qm[rc], iwt = iqm[rc];
            int dq = rc != 0 ? dequant1 : dequant0;
            int dequant = (dq * iwt + (1 << (AomQmBits - 1))) >> AomQmBits;
            int sign = c >> 31;
            long absCoeff = (c ^ sign) - sign;
            int tmp32 = 0;
            if (absCoeff * wt >= (dq << (AomQmBits - (1 + logScale))))
            {
                absCoeff += rc != 0 ? r1 : r0;
                absCoeff = Math.Clamp(absCoeff, short.MinValue, short.MaxValue);
                tmp32 = (int)((absCoeff * wt * (rc != 0 ? quant1 : quant0)) >> (16 - logScale + AomQmBits));
                qcoeff[rc] = (tmp32 ^ sign) - sign;
                int absDq = (tmp32 * dequant) >> logScale;
                dqcoeff[rc] = (absDq ^ sign) - sign;
            }
            if (tmp32 != 0) eob = i;
        }
        return eob + 1;
    }

    /// <summary>aom_quantize_b_helper_c with matrices. Returns the eob.</summary>
    internal static int QuantizeBHelper(ReadOnlySpan<int> coeff, int nCoeffs, ReadOnlySpan<ushort> scan, short zbin0, short zbin1,
        short round0, short round1, short quant0, short quant1, short shift0, short shift1, short dequant0, short dequant1,
        byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        int half = (1 << logScale) >> 1;
        int zb0 = (zbin0 + half) >> logScale, zb1 = (zbin1 + half) >> logScale;
        int nonZeroCount = nCoeffs, eob = -1;
        qcoeff.Slice(0, nCoeffs).Clear();
        dqcoeff.Slice(0, nCoeffs).Clear();
        // pre-scan pass
        for (int i = nCoeffs - 1; i >= 0; i--)
        {
            int rc = scan[i];
            int c = coeff[rc] * qm[rc];
            int zb = rc != 0 ? zb1 : zb0;
            if (c < zb * (1 << AomQmBits) && c > -zb * (1 << AomQmBits)) nonZeroCount--;
            else break;
        }
        for (int i = 0; i < nonZeroCount; i++)
        {
            int rc = scan[i];
            int c = coeff[rc];
            int sign = c >> 31;
            int absCoeff = (c ^ sign) - sign;
            int wt = qm[rc];
            int ac = rc != 0 ? 1 : 0;
            if (absCoeff * wt >= ((ac != 0 ? zb1 : zb0) << AomQmBits))
            {
                int rnd = ((ac != 0 ? round1 : round0) + half) >> logScale;
                long tmp = Math.Clamp(absCoeff + rnd, short.MinValue, short.MaxValue);
                tmp *= wt;
                int tmp32 = (int)(((((tmp * (ac != 0 ? quant1 : quant0)) >> 16) + tmp) * (ac != 0 ? shift1 : shift0)) >> (16 - logScale + AomQmBits));
                qcoeff[rc] = (tmp32 ^ sign) - sign;
                int iwt = iqm[rc];
                int dequant = ((ac != 0 ? dequant1 : dequant0) * iwt + (1 << (AomQmBits - 1))) >> AomQmBits;
                int absDq = (tmp32 * dequant) >> logScale;
                dqcoeff[rc] = (absDq ^ sign) - sign;
                if (tmp32 != 0) eob = i;
            }
        }
        return eob + 1;
    }

    /// <summary>av1_block_error_qm (8-bit): the matrix-weighted coefficient error and energy (weights by scan[i], as
    /// libaom indexes them).</summary>
    internal static long BlockErrorQm(ReadOnlySpan<int> coeff, ReadOnlySpan<int> dqcoeff, int blockSize, byte[] qm, ReadOnlySpan<ushort> scan, out long ssz,
        int bd = 8)
    {
        if (bd > 8)
        {
            // high bit depth: the sums rounded down by 2 (bd - 8) bits
            long e = BlockErrorQm(coeff, dqcoeff, blockSize, qm, scan, out long s8);
            int shift = 2 * (bd - 8), rounding = (1 << shift) >> 1;
            ssz = (s8 + rounding) >> shift;
            return (e + rounding) >> shift;
        }
        long error = 0, sqcoeff = 0;
        for (int i = 0; i < blockSize; i++)
        {
            long weight = qm[scan[i]];
            long dd = (long)(coeff[i] - dqcoeff[i]) * weight;
            long cc = coeff[i] * weight;
            error += (dd * dd + (1 << (2 * AomQmBits - 1))) >> (2 * AomQmBits);
            sqcoeff += (cc * cc + (1 << (2 * AomQmBits - 1))) >> (2 * AomQmBits);
        }
        ssz = sqcoeff;
        return error;
    }

    /// <summary>get_coeff_dist with a matrix (txb_rdopt_utils.h).</summary>
    internal static long CoeffDistQm(int tcoeff, int dqcoeff, int shift, byte[] qm, int ci)
    {
        long diff = (long)(tcoeff - dqcoeff) * (1 << shift);
        diff *= qm[ci];
        return (diff * diff + (1 << (2 * AomQmBits - 1))) >> (2 * AomQmBits);
    }
}
