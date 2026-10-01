using System;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1's high bit depth quantizers as the encoder dispatches them on AVX2: av1_highbd_quantize_fp_avx2
// (av1/encoder/x86/av1_highbd_quantize_avx2.c), aom_highbd_quantize_b{,_32x32,_64x64}_avx2
// (aom_dsp/x86/highbd_quantize_intrin_avx2.c), and with quantization matrices the C helpers
// (highbd_quantize_fp_helper_c in av1_quantize.c, aom_highbd_quantize_b_helper_c in aom_dsp/quantize.c). The AVX2
// kernels work in 32-bit lanes (64-bit products): one coefficient at a time here, in the kernels' arithmetic.
internal static class AomQuantizeHbd
{
    /// <summary>av1_highbd_quantize_fp_avx2: round = ROUND_POWER_OF_TWO(round, log_scale) (mulhrs), level =
    /// ((|c| + round) * quant) &gt;&gt; (16 - log_scale), zeroed where dequant &gt; |c| &lt;&lt; (1 + log_scale), dequantised
    /// (level * dequant) &gt;&gt; log_scale; the DC parameters for coefficient 0 only. Returns the eob.</summary>
    internal static int QuantizeFp(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, int logScale, Span<int> qcoeff, Span<int> dqcoeff) =>
        AomQuantizeHbdSimd.Supported && (nCoeffs & 7) == 0
            ? AomQuantizeHbdSimd.QuantizeFp(coeff, nCoeffs, iscan, round0, round1, quant0, quant1, dequant0, dequant1, logScale, qcoeff, dqcoeff)
            : QuantizeFpScalar(coeff, nCoeffs, iscan, round0, round1, quant0, quant1, dequant0, dequant1, logScale, qcoeff, dqcoeff);

    internal static int QuantizeFpScalar(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        int r0 = round0, r1 = round1;
        if (logScale > 0) { r0 = (r0 + (1 << (logScale - 1))) >> logScale; r1 = (r1 + (1 << (logScale - 1))) >> logScale; }
        // init_one_qp zero-extends the int16 parameters
        long q0 = (ushort)quant0, q1 = (ushort)quant1;
        int dq0 = (ushort)dequant0, dq1 = (ushort)dequant1;
        int eob = 0;
        for (int i = 0; i < nCoeffs; i++)
        {
            int c = coeff[i];
            bool dc = i == 0;
            int abs = Math.Abs(c);
            long prod = (long)(abs + (dc ? r0 : r1)) * (dc ? q0 : q1);
            int q = (int)(uint)((ulong)prod >> (16 - logScale));
            int dqv = dc ? dq0 : dq1;
            if (dqv > (abs << (1 + logScale))) q = 0;
            int dq = (q * dqv) >> logScale;
            if (c < 0) { q = -q; dq = -dq; }
            else if (c == 0) { q = 0; dq = 0; }
            qcoeff[i] = q;
            dqcoeff[i] = dq;
            if (dq != 0) eob = Math.Max(eob, iscan[i] + 1);
        }
        return eob;
    }

    /// <summary>aom_highbd_quantize_b{,_32x32,_64x64}_avx2: zbin / round = ROUND_POWER_OF_TWO(., log_scale), a coefficient
    /// quantised when |c| &gt;= zbin: tmp = |c| + round, tmp2 = ((tmp * quant) &gt;&gt; 16) + tmp, level = (tmp2 * quant_shift)
    /// &gt;&gt; (16 - log_scale), dequantised (level * dequant) &gt;&gt; log_scale. Returns the eob.</summary>
    internal static int QuantizeB(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1, short round0, short round1,
        short quant0, short quant1, short quantShift0, short quantShift1, short dequant0, short dequant1, int logScale,
        Span<int> qcoeff, Span<int> dqcoeff) =>
        AomQuantizeHbdSimd.Supported && (nCoeffs & 7) == 0
            ? AomQuantizeHbdSimd.QuantizeB(coeff, nCoeffs, iscan, zbin0, zbin1, round0, round1, quant0, quant1, quantShift0, quantShift1, dequant0, dequant1, logScale, qcoeff, dqcoeff)
            : QuantizeBScalar(coeff, nCoeffs, iscan, zbin0, zbin1, round0, round1, quant0, quant1, quantShift0, quantShift1, dequant0, dequant1, logScale, qcoeff, dqcoeff);

    internal static int QuantizeBScalar(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1, short round0, short round1,
        short quant0, short quant1, short quantShift0, short quantShift1, short dequant0, short dequant1, int logScale,
        Span<int> qcoeff, Span<int> dqcoeff)
    {
        int z0 = zbin0, z1 = zbin1, r0 = round0, r1 = round1;
        if (logScale > 0)
        {
            int rnd = 1 << (logScale - 1);
            z0 = (z0 + rnd) >> logScale; z1 = (z1 + rnd) >> logScale;
            r0 = (r0 + rnd) >> logScale; r1 = (r1 + rnd) >> logScale;
        }
        int eob = 0;
        for (int i = 0; i < nCoeffs; i++)
        {
            int c = coeff[i];
            bool dc = i == 0;
            int abs = Math.Abs(c);
            if (abs < (dc ? z0 : z1)) { qcoeff[i] = 0; dqcoeff[i] = 0; continue; }
            int tmpRnd = abs + (dc ? r0 : r1);
            int tmp = (int)(((long)tmpRnd * (dc ? quant0 : quant1)) >> 16);
            int tmp2 = tmp + tmpRnd;
            int absQ = (int)(uint)((ulong)((long)tmp2 * (dc ? quantShift0 : quantShift1)) >> (16 - logScale));
            int absDq = (int)((uint)(absQ * (dc ? dequant0 : dequant1)) >> logScale);
            if (c < 0) { qcoeff[i] = -absQ; dqcoeff[i] = -absDq; }
            else { qcoeff[i] = absQ; dqcoeff[i] = absDq; }
            if (absQ > 0) eob = Math.Max(eob, iscan[i] + 1);
        }
        return eob;
    }

    private const int AomQmBits = 5;

    /// <summary>highbd_quantize_fp_helper_c with quantization matrices (av1_highbd_quantize_fp_facade, qm != NULL).</summary>
    internal static int QuantizeFpHelperQm(ReadOnlySpan<int> coeff, int count, ReadOnlySpan<ushort> scan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        int eob = -1;
        int shift = 16 - logScale;
        for (int i = 0; i < count; i++)
        {
            int rc = scan[i];
            int coeffV = coeff[rc];
            int wt = qm[rc], iwt = iqm[rc];
            int dqp = rc != 0 ? dequant1 : dequant0;
            int dequant = (dqp * iwt + (1 << (AomQmBits - 1))) >> AomQmBits;
            int coeffSign = coeffV < 0 ? -1 : 0;
            long absCoeff = (coeffV ^ coeffSign) - coeffSign;
            if (absCoeff * wt >= (dqp << (AomQmBits - (1 + logScale))))
            {
                int rnd = rc != 0 ? round1 : round0;
                long tmp = absCoeff + ((rnd + ((1 << logScale) >> 1)) >> logScale);
                int absQ = (int)((tmp * (rc != 0 ? quant1 : quant0) * wt) >> (shift + AomQmBits));
                qcoeff[rc] = (absQ ^ coeffSign) - coeffSign;
                int absDq = (absQ * dequant) >> logScale;
                dqcoeff[rc] = (absDq ^ coeffSign) - coeffSign;
                if (absQ != 0) eob = i;
            }
            else
            {
                qcoeff[rc] = 0;
                dqcoeff[rc] = 0;
            }
        }
        return eob + 1;
    }

    /// <summary>aom_highbd_quantize_b_helper_c with quantization matrices (av1_highbd_quantize_b_facade, qm != NULL).</summary>
    internal static int QuantizeBHelperQm(ReadOnlySpan<int> coeff, int nCoeffs, ReadOnlySpan<ushort> scan, short zbin0, short zbin1,
        short round0, short round1, short quant0, short quant1, short quantShift0, short quantShift1, short dequant0, short dequant1,
        byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        int z0 = (zbin0 + ((1 << logScale) >> 1)) >> logScale, z1 = (zbin1 + ((1 << logScale) >> 1)) >> logScale;
        int nz0 = -z0, nz1 = -z1;
        qcoeff.Slice(0, nCoeffs).Clear();
        dqcoeff.Slice(0, nCoeffs).Clear();
        Span<int> idxArr = nCoeffs <= 1024 ? stackalloc int[nCoeffs] : new int[nCoeffs];
        int idx = 0;
        for (int i = 0; i < nCoeffs; i++)
        {
            int rc = scan[i];
            int wt = qm[rc];
            int c = coeff[rc] * wt;
            if (c >= (rc != 0 ? z1 : z0) * (1 << AomQmBits) || c <= (rc != 0 ? nz1 : nz0) * (1 << AomQmBits)) idxArr[idx++] = i;
        }
        int eob = -1;
        for (int i = 0; i < idx; i++)
        {
            int rc = scan[idxArr[i]];
            int c = coeff[rc];
            int coeffSign = c < 0 ? -1 : 0;
            int wt = qm[rc], iwt = iqm[rc];
            int absCoeff = (c ^ coeffSign) - coeffSign;
            int rnd = rc != 0 ? round1 : round0;
            long tmp1 = absCoeff + ((rnd + ((1 << logScale) >> 1)) >> logScale);
            long tmpw = tmp1 * wt;
            long tmp2 = ((tmpw * (rc != 0 ? quant1 : quant0)) >> 16) + tmpw;
            int absQ = (int)((tmp2 * (rc != 0 ? quantShift1 : quantShift0)) >> (16 - logScale + AomQmBits));
            qcoeff[rc] = (absQ ^ coeffSign) - coeffSign;
            int dequant = ((rc != 0 ? dequant1 : dequant0) * iwt + (1 << (AomQmBits - 1))) >> AomQmBits;
            int absDq = (absQ * dequant) >> logScale;
            dqcoeff[rc] = (absDq ^ coeffSign) - coeffSign;
            if (absQ != 0) eob = idxArr[i];
        }
        return eob + 1;
    }
}
