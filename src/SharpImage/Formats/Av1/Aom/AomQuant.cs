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
    /// the dequantised value in 16-bit lanes. round / quant / dequant: [dc, ac]. Returns the eob.</summary>
    internal static int QuantizeFpAvx2(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        // init_qp: lane 0 of the first group the DC values, every other lane AC
        short rnd0 = round0, rnd1 = round1;
        if (logScale > 0)
        {
            short r = (short)(1 << (logScale - 1));
            rnd0 = (short)((short)(rnd0 + r) >> logScale); rnd1 = (short)((short)(rnd1 + r) >> logScale);
        }
        short q0 = quant0, q1 = quant1;
        if (logScale == 1) { q0 = (short)(q0 << 1); q1 = (short)(q1 << 1); }
        var vRnd = Vector256.Create(rnd0, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1, rnd1);
        var vQ = Vector256.Create(q0, q1, q1, q1, q1, q1, q1, q1, q1, q1, q1, q1, q1, q1, q1, q1);
        var vDq = Vector256.Create(dequant0, dequant1, dequant1, dequant1, dequant1, dequant1, dequant1, dequant1,
            dequant1, dequant1, dequant1, dequant1, dequant1, dequant1, dequant1, dequant1);
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
        // load_b_values_avx2: lane 0 the DC values
        static Vector256<short> Dc(short dc, short ac) => Vector256.Create(dc, ac, ac, ac, ac, ac, ac, ac, ac, ac, ac, ac, ac, ac, ac, ac);
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
}
