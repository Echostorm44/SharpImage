using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// Port of libaom av1/encoder/av1_quantize.c (av1_build_quantizer) and of the quantizers the 8-bit encoder runs: the
// AVX2 av1_quantize_fp* (av1/encoder/x86/av1_quantize_avx2.c) and aom_quantize_b* (aom_dsp/x86/quantize_avx2.c), whose
// int16 lanes differ from the C reference at the edges, without quantization matrices (libaom's all-intra
// configuration runs using_qm = 0). Coefficients are libaom's tran_low_t (the integer forward transform's output); levels and
// dequantized values in the tx's rc layout, eob = one past the last nonzero scan index.
internal sealed class AomQuants
{
    // [plane 0 y, 1 u, 2 v][qindex][dc / ac]
    public readonly short[,,] Quant = new short[3, 256, 2], QuantShift = new short[3, 256, 2], Zbin = new short[3, 256, 2],
        Round = new short[3, 256, 2], QuantFp = new short[3, 256, 2], RoundFp = new short[3, 256, 2], Dequant = new short[3, 256, 2];

    /// <summary>av1_build_quantizer.</summary>
    public AomQuants(int bitDepth, int yDcDelta, int uDcDelta, int uAcDelta, int vDcDelta, int vAcDelta, int sharpness)
    {
        int bdIdx = bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2;
        int sharpnessAdjustment = 16 * (7 - sharpness) / 7;
        for (int q = 0; q < 256; q++)
        {
            int qzbinFactor = QzbinFactor(q, bitDepth, bdIdx);
            int qroundingFactor = q == 0 ? 64 : 48;
            for (int i = 0; i < 2; i++)
            {
                int qroundingFactorFp = 64;
                if (sharpness != 0 && q != 0)
                {
                    qroundingFactor = 64 - sharpnessAdjustment;
                    qroundingFactorFp = 64 - sharpnessAdjustment;
                }
                for (int p = 0; p < 3; p++)
                {
                    int dcDelta = p == 0 ? yDcDelta : p == 1 ? uDcDelta : vDcDelta, acDelta = p == 0 ? 0 : p == 1 ? uAcDelta : vAcDelta;
                    int quantQtx = Av1Tables.DequantTable[bdIdx, Math.Clamp(q + (i == 0 ? dcDelta : acDelta), 0, 255), i];
                    InvertQuant(out Quant[p, q, i], out QuantShift[p, q, i], quantQtx);
                    QuantFp[p, q, i] = (short)((1 << 16) / quantQtx);
                    RoundFp[p, q, i] = (short)((qroundingFactorFp * quantQtx) >> 7);
                    Zbin[p, q, i] = (short)((qzbinFactor * quantQtx + 64) >> 7);
                    Round[p, q, i] = (short)((qroundingFactor * quantQtx) >> 7);
                    Dequant[p, q, i] = (short)quantQtx;
                }
            }
        }
    }

    private static void InvertQuant(out short quant, out short shift, int d)
    {
        uint t = (uint)d;
        int l = BitOperations.Log2(t);
        int m = 1 + (1 << (16 + l)) / d;
        quant = (short)(m - (1 << 16));
        shift = (short)(1 << (16 - l));
    }

    private static int QzbinFactor(int q, int bitDepth, int bdIdx)
    {
        int quant = Av1Tables.DequantTable[bdIdx, q, 0];
        return bitDepth switch
        {
            8 => q == 0 ? 64 : (quant < 148 ? 84 : 80),
            10 => q == 0 ? 64 : (quant < 592 ? 84 : 80),
            _ => q == 0 ? 64 : (quant < 2368 ? 84 : 80),
        };
    }
}

internal static class AomQuantize
{
    /// <summary>av1_get_tx_scale: 1 for 32x32-class (pels > 256), 2 for 64x64-class (pels > 1024), else 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TxScale(int txSize)
    {
        ref readonly var d = ref Av1Tables.TxfmDimensions[txSize];
        int pels = d.W * 4 * d.H * 4;
        return (pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0);
    }

    // Inverse scans (rc -> scan index) per tx size, as int16 (libaom's iscan)
    private static readonly short[]?[] IScans = new short[]?[19];
    internal static short[] IScan(int tx)
    {
        var a = IScans[tx];
        if (a != null) return a;
        var scan = Av1Tables.Scans[tx];
        a = new short[scan.Length];
        for (int i = 0; i < scan.Length; i++) a[scan[i]] = (short)i;
        return IScans[tx] = a;
    }

    /// <summary>av1_quantize_fp / _32x32 / _64x64 as libaom runs them on AVX2 (av1_quantize_avx2.c): coefficients
    /// saturated to int16, 16 at a time in memory order, a group quantised only when one of its magnitudes exceeds
    /// (dequant >> (1 + log_scale)) - 1, the level mulhi((|c| + round) sat, quant) (unsigned for 32x32, split for 64x64),
    /// <summary>(dc, ac, ac, ... ac): a broadcast with lane 0 replaced (a 16-argument Vector256.Create is a long chain of
    /// inserts per call).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> DcAc(short dc, short ac)
    {
        var a = Vector128.Create(ac);
        return Vector256.Create(a.WithElement(0, dc), a);
    }

    /// the dequantised value in 16-bit lanes. round / quant / dequant: [dc, ac]. Returns the eob.</summary>
    internal static int QuantizeFpAvx2(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if (!Avx2.IsSupported)
            return QuantizeFpAvx2Emu(coeff, nCoeffs, iscan, round0, round1, quant0, quant1, dequant0, dequant1, logScale, qcoeff, dqcoeff);
        // init_qp: lane 0 of the first group the DC values, every other lane AC
        short rnd0 = round0, rnd1 = round1;
        if (logScale > 0)
        {
            short r = (short)(1 << (logScale - 1));
            rnd0 = (short)((short)(rnd0 + r) >> logScale); rnd1 = (short)((short)(rnd1 + r) >> logScale);
        }
        short q0 = quant0, q1 = quant1;
        if (logScale == 1) { q0 = (short)(q0 << 1); q1 = (short)(q1 << 1); }
        var vRnd = DcAc(rnd0, rnd1);
        var vQ = DcAc(q0, q1);
        var vDq = DcAc(dequant0, dequant1);
        var vThr = Vector256.ShiftRightArithmetic(vDq, 1 + logScale) - Vector256<short>.One;
        var eob = Vector256<short>.Zero;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int qc0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int dq0 = ref MemoryMarshal.GetReference(dqcoeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        for (int k = 0; k < nCoeffs; k += 16)
        {
            // load_coefficients_avx2: packs_epi32 of two 8-lane loads (lane order 0-3, 8-11, 4-7, 12-15)
            var coef = Avx2.PackSignedSaturate(Vector256.LoadUnsafe(ref c0, (nuint)k), Vector256.LoadUnsafe(ref c0, (nuint)(k + 8)));
            var abs = Avx2.Abs(coef).AsInt16();
            var mask = Avx2.CompareGreaterThan(abs, vThr);
            if (Avx2.MoveMask(mask.AsByte()) != 0)
            {
                Vector256<short> absQ, q, dq, nz;
                if (logScale == 0)
                {
                    absQ = Avx2.MultiplyHigh(Avx2.AddSaturate(abs, vRnd), vQ);
                    q = Avx2.Sign(absQ, coef);
                    dq = Avx2.MultiplyLow(q, vDq);
                    nz = Avx2.CompareGreaterThan(absQ, Vector256<short>.Zero);
                }
                else if (logScale == 1)
                {
                    absQ = Avx2.MultiplyHigh(Avx2.AddSaturate(abs, vRnd).AsUInt16(), vQ.AsUInt16()).AsInt16();
                    q = Avx2.Sign(absQ, coef);
                    var absDq = Avx2.ShiftRightLogical(Avx2.MultiplyLow(absQ, vDq).AsUInt16(), 1).AsInt16();
                    nz = Avx2.CompareGreaterThan(absQ, Vector256<short>.Zero);
                    dq = Avx2.Sign(absDq, coef);
                }
                else
                {
                    var tmpRnd = Avx2.AddSaturate(abs, vRnd) & mask;
                    var qh = Avx2.ShiftLeftLogical(Avx2.MultiplyHigh(tmpRnd, vQ), 2);
                    var ql = Avx2.ShiftRightLogical(Avx2.MultiplyLow(tmpRnd, vQ).AsUInt16(), 14).AsInt16();
                    absQ = qh | ql;
                    var dqh = Avx2.ShiftLeftLogical(Avx2.MultiplyHigh(absQ, vDq), 14);
                    var dql = Avx2.ShiftRightLogical(Avx2.MultiplyLow(absQ, vDq).AsUInt16(), 2).AsInt16();
                    var absDq = dqh | dql;
                    q = Avx2.Sign(absQ, coef);
                    dq = Avx2.Sign(absDq, coef);
                    var z = Avx2.CompareEqual(dq, Vector256<short>.Zero);
                    nz = Avx2.CompareEqual(z, Vector256<short>.Zero);
                }
                StoreCoefficients(q, ref Unsafe.Add(ref qc0, k));
                StoreCoefficients(dq, ref Unsafe.Add(ref dq0, k));
                // get_max_lane_eob: the iscan in the packed lane order
                var isc = Avx2.Permute4x64(Vector256.LoadUnsafe(ref is0, (nuint)k).AsInt64(), 0xD8).AsInt16();
                eob = Avx2.Max(eob, (isc - nz) & nz);
            }
            else
            {
                Vector256<int>.Zero.StoreUnsafe(ref qc0, (nuint)k); Vector256<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 8));
                Vector256<int>.Zero.StoreUnsafe(ref dq0, (nuint)k); Vector256<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 8));
            }
            if (k == 0)
            {
                // update_qp: the AC values in every lane from the second group on
                vRnd = Vector256.Create(rnd1); vQ = Vector256.Create(q1); vDq = Vector256.Create(dequant1);
                vThr = Vector256.ShiftRightArithmetic(vDq, 1 + logScale) - Vector256<short>.One;
            }
        }
        // quant_gather_eob
        return MaxU16(Sse2.Max(eob.GetLower(), eob.GetUpper()).AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaxU16(Vector128<ushort> v)
    {
        // INT16_MAX - minpos(INT16_MAX -sat v): the largest lane (all lanes are 0..INT16_MAX)
        var s = Sse2.SubtractSaturate(Vector128.Create((ushort)short.MaxValue), v);
        return short.MaxValue - Sse41.MinHorizontal(s).ToScalar();
    }

    // store_coefficients_avx2: sign-extended back to 32 bits, unpacked in the packed lane order (restores memory order)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCoefficients(Vector256<short> v, ref int dst)
    {
        var sign = Avx2.ShiftRightArithmetic(v, 15);
        Avx2.UnpackLow(v, sign).AsInt32().StoreUnsafe(ref dst);
        Avx2.UnpackHigh(v, sign).AsInt32().StoreUnsafe(ref Unsafe.Add(ref dst, 8));
    }

    /// <summary>aom_quantize_b / _32x32 / _64x64 as libaom runs them on AVX2 (aom_dsp/x86/quantize_avx2.c): int16
    /// lanes, a group quantised only when a magnitude exceeds zbin - 1, the level through two 16-bit mulhi steps (and
    /// the log-scale split for 32x32 / 64x64), dequantised in 16-bit lanes. Params: [dc, ac]. Returns the eob.</summary>
    internal static int QuantizeBAvx2(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1,
        short round0, short round1, short quant0, short quant1, short shift0, short shift1, short dequant0, short dequant1,
        int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if (!Avx2.IsSupported)
            return QuantizeBAvx2Emu(coeff, nCoeffs, iscan, zbin0, zbin1, round0, round1, quant0, quant1, shift0, shift1, dequant0, dequant1,
                logScale, qcoeff, dqcoeff);
        // load_b_values_avx2: lane 0 the DC values
        static Vector256<short> Dc(short dc, short ac) => DcAc(dc, ac);
        short zb0 = zbin0, zb1 = zbin1, rn0 = round0, rn1 = round1;
        if (logScale > 0)
        {
            short r = (short)(1 << (logScale - 1));
            zb0 = (short)((short)(zb0 + r) >> logScale); zb1 = (short)((short)(zb1 + r) >> logScale);
            rn0 = (short)((short)(rn0 + r) >> logScale); rn1 = (short)((short)(rn1 + r) >> logScale);
        }
        var vZbin = Dc(zb0, zb1) - Vector256<short>.One;
        var vRound = Dc(rn0, rn1);
        var vQuant = Dc(quant0, quant1);
        var vDequant = Dc(dequant0, dequant1);
        var vShift = Dc(shift0, shift1);
        var eob = Vector256<short>.Zero;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int qc0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int dq0 = ref MemoryMarshal.GetReference(dqcoeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        for (int k = 0; k < nCoeffs; k += 16)
        {
            var coef = Avx2.PackSignedSaturate(Vector256.LoadUnsafe(ref c0, (nuint)k), Vector256.LoadUnsafe(ref c0, (nuint)(k + 8)));
            var abs = Avx2.Abs(coef).AsInt16();
            var zmask = Avx2.CompareGreaterThan(abs, vZbin);
            Vector256<short> nz;
            if (Avx2.MoveMask(zmask.AsByte()) == 0)
            {
                Vector256<int>.Zero.StoreUnsafe(ref qc0, (nuint)k); Vector256<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 8));
                Vector256<int>.Zero.StoreUnsafe(ref dq0, (nuint)k); Vector256<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 8));
                nz = Vector256<short>.Zero;
            }
            else
            {
                var tmpRnd = Avx2.AddSaturate(abs, vRound) & zmask;
                var tmp32b = Avx2.MultiplyHigh(tmpRnd, vQuant) + tmpRnd;
                Vector256<short> tmp32, dqc;
                if (logScale == 0)
                {
                    tmp32 = Avx2.MultiplyHigh(tmp32b, vShift);
                    dqc = Avx2.MultiplyLow(Avx2.Sign(tmp32, coef), vDequant);
                }
                else
                {
                    var hi = Avx2.ShiftLeftLogical(Avx2.MultiplyHigh(tmp32b, vShift), (byte)logScale);
                    var lo = Avx2.ShiftRightLogical(Avx2.MultiplyLow(tmp32b, vShift).AsUInt16(), (byte)(16 - logScale)).AsInt16();
                    tmp32 = hi | lo;
                    var dh = Avx2.ShiftLeftLogical(Avx2.MultiplyHigh(tmp32, vDequant), (byte)(16 - logScale));
                    var dl = Avx2.ShiftRightLogical(Avx2.MultiplyLow(tmp32, vDequant).AsUInt16(), (byte)logScale).AsInt16();
                    dqc = Avx2.Sign(dh | dl, coef);
                }
                nz = Avx2.CompareGreaterThan(tmp32, Vector256<short>.Zero);
                StoreCoefficients(Avx2.Sign(tmp32, coef), ref Unsafe.Add(ref qc0, k));
                StoreCoefficients(dqc, ref Unsafe.Add(ref dq0, k));
            }
            var isc = Avx2.Permute4x64(Vector256.LoadUnsafe(ref is0, (nuint)k).AsInt64(), 0xD8).AsInt16();
            eob = Avx2.Max(eob, (isc - nz) & nz);
            if (k == 0)
            {
                vRound = Vector256.Create(rn1); vQuant = Vector256.Create(quant1); vDequant = Vector256.Create(dequant1);
                vShift = Vector256.Create(shift1); vZbin = Vector256.Create((short)(zb1 - 1));
            }
        }
        return MaxU16(Sse2.Max(eob.GetLower(), eob.GetUpper()).AsUInt16());
    }

    // ---- the AVX2 kernels' int16 lane arithmetic, one lane at a time (no AVX2: the same levels and eob) ----

    private static short Sat16(int v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    private static short AddSat(short a, short b) => Sat16(a + b);
    private static short MulHi(short a, short b) => (short)((a * b) >> 16);
    private static ushort MulHiU(ushort a, ushort b) => (ushort)(((uint)a * b) >> 16);
    private static short MulLo(short a, short b) => (short)(a * b);
    // _mm256_sign_epi16
    private static short Sign(short a, short b) => b < 0 ? (short)-a : b == 0 ? (short)0 : a;

    /// <summary><see cref="QuantizeFpAvx2"/> lane by lane: a group of 16 quantised only when a magnitude exceeds the
    /// threshold, the lanes' int16 saturation / wrap-around as the kernel's.</summary>
    internal static int QuantizeFpAvx2Emu(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        short rnd0 = round0, rnd1 = round1;
        if (logScale > 0)
        {
            short r = (short)(1 << (logScale - 1));
            rnd0 = (short)((short)(rnd0 + r) >> logScale); rnd1 = (short)((short)(rnd1 + r) >> logScale);
        }
        short q0 = quant0, q1 = quant1;
        if (logScale == 1) { q0 = (short)(q0 << 1); q1 = (short)(q1 << 1); }
        int eob = 0;
        for (int k = 0; k < nCoeffs; k += 16)
        {
            bool any = false;
            for (int i = k; i < k + 16; i++)
            {
                short dqv = i == 0 ? dequant0 : dequant1;
                short abs = (short)Math.Abs((int)Sat16(coeff[i]));
                if (abs > (short)((dqv >> (1 + logScale)) - 1)) any = true;
            }
            if (!any)
            {
                for (int i = k; i < k + 16; i++) { qcoeff[i] = 0; dqcoeff[i] = 0; }
                continue;
            }
            for (int i = k; i < k + 16; i++)
            {
                bool dcLane = i == 0;
                short rnd = dcLane ? rnd0 : rnd1, qv = dcLane ? q0 : q1, dqv = dcLane ? dequant0 : dequant1;
                short coef = Sat16(coeff[i]);
                short abs = (short)Math.Abs((int)coef);
                short thr = (short)((dqv >> (1 + logScale)) - 1);
                short absQ, q, dq; bool nz;
                if (logScale == 0)
                {
                    absQ = MulHi(AddSat(abs, rnd), qv);
                    q = Sign(absQ, coef);
                    dq = MulLo(q, dqv);
                    nz = absQ > 0;
                }
                else if (logScale == 1)
                {
                    absQ = (short)MulHiU((ushort)AddSat(abs, rnd), (ushort)qv);
                    q = Sign(absQ, coef);
                    short absDq = (short)((ushort)MulLo(absQ, dqv) >> 1);
                    nz = absQ > 0;
                    dq = Sign(absDq, coef);
                }
                else
                {
                    short tmpRnd = abs > thr ? AddSat(abs, rnd) : (short)0;
                    short qh = (short)(MulHi(tmpRnd, qv) << 2);
                    short ql = (short)((ushort)MulLo(tmpRnd, qv) >> 14);
                    absQ = (short)(qh | ql);
                    short dqh = (short)(MulHi(absQ, dqv) << 14);
                    short dql = (short)((ushort)MulLo(absQ, dqv) >> 2);
                    q = Sign(absQ, coef);
                    dq = Sign((short)(dqh | dql), coef);
                    nz = dq != 0;
                }
                qcoeff[i] = q;
                dqcoeff[i] = dq;
                if (nz) eob = Math.Max(eob, (ushort)(iscan[i] + 1));
            }
        }
        return eob;
    }

    /// <summary><see cref="QuantizeBAvx2"/> lane by lane (see <see cref="QuantizeFpAvx2Emu"/>).</summary>
    internal static int QuantizeBAvx2Emu(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1,
        short round0, short round1, short quant0, short quant1, short shift0, short shift1, short dequant0, short dequant1,
        int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        short zb0 = zbin0, zb1 = zbin1, rn0 = round0, rn1 = round1;
        if (logScale > 0)
        {
            short r = (short)(1 << (logScale - 1));
            zb0 = (short)((short)(zb0 + r) >> logScale); zb1 = (short)((short)(zb1 + r) >> logScale);
            rn0 = (short)((short)(rn0 + r) >> logScale); rn1 = (short)((short)(rn1 + r) >> logScale);
        }
        int eob = 0;
        for (int k = 0; k < nCoeffs; k += 16)
        {
            bool any = false;
            for (int i = k; i < k + 16; i++)
                if ((short)Math.Abs((int)Sat16(coeff[i])) > (short)((i == 0 ? zb0 : zb1) - 1)) any = true;
            if (!any)
            {
                for (int i = k; i < k + 16; i++) { qcoeff[i] = 0; dqcoeff[i] = 0; }
                continue;
            }
            for (int i = k; i < k + 16; i++)
            {
                bool dcLane = i == 0;
                short zb = (short)((dcLane ? zb0 : zb1) - 1), rnd = dcLane ? rn0 : rn1, qv = dcLane ? quant0 : quant1;
                short dqv = dcLane ? dequant0 : dequant1, shv = dcLane ? shift0 : shift1;
                short coef = Sat16(coeff[i]);
                short abs = (short)Math.Abs((int)coef);
                short tmpRnd = abs > zb ? AddSat(abs, rnd) : (short)0;
                short tmp32b = (short)(MulHi(tmpRnd, qv) + tmpRnd);
                short tmp32, dqc;
                if (logScale == 0)
                {
                    tmp32 = MulHi(tmp32b, shv);
                    dqc = MulLo(Sign(tmp32, coef), dqv);
                }
                else
                {
                    short hi = (short)(MulHi(tmp32b, shv) << logScale);
                    short lo = (short)((ushort)MulLo(tmp32b, shv) >> (16 - logScale));
                    tmp32 = (short)(hi | lo);
                    short dh = (short)(MulHi(tmp32, dqv) << (16 - logScale));
                    short dl = (short)((ushort)MulLo(tmp32, dqv) >> logScale);
                    dqc = Sign((short)(dh | dl), coef);
                }
                qcoeff[i] = Sign(tmp32, coef);
                dqcoeff[i] = dqc;
                if (tmp32 > 0) eob = Math.Max(eob, (ushort)(iscan[i] + 1));
            }
        }
        return eob;
    }
}
