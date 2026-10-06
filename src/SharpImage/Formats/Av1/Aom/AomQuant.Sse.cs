using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using W8 = System.Runtime.Intrinsics.Vector128<short>;

namespace SharpImage.Formats.Av1;

// The AVX2 quantizers' arithmetic in 128-bit lanes (SSE4.1: the default Native AOT instruction set has no AVX2): each
// group of 16 coefficients as two 8-lane halves in memory order, quantised when any of its 16 magnitudes passes the
// threshold, with the same int16 saturation, mulhi / mullo, sign and eob gather, so the levels, dequantised values and
// eob equal the AVX2 kernels' (and their lane-by-lane emulation's).
internal static partial class AomQuantize
{
    // 8 int32 coefficients saturated to int16
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W8 Load8Sat(ref int c, int k) => Sse2.PackSignedSaturate(Vector128.LoadUnsafe(ref c, (nuint)k), Vector128.LoadUnsafe(ref c, (nuint)(k + 4)));

    // 8 int16 lanes sign-extended to int32 at dst[k..k + 8)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(W8 v, ref int dst, int k)
    {
        Sse41.ConvertToVector128Int32(v).StoreUnsafe(ref dst, (nuint)k);
        Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(v, 8)).StoreUnsafe(ref dst, (nuint)(k + 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W8 DcAc8(short dc, short ac) => Vector128.Create(ac).WithElement(0, dc);

    /// <summary><see cref="QuantizeFpAvx2"/> in SSE4.1.</summary>
    internal static int QuantizeFpSse41(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
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
        // lane 0 of the first group the DC values (the low half), every other lane AC
        W8 rndL = DcAc8(rnd0, rnd1), qL = DcAc8(q0, q1), dqL = DcAc8(dequant0, dequant1);
        W8 rndH = Vector128.Create(rnd1), qH = Vector128.Create(q1), dqH = Vector128.Create(dequant1);
        W8 thrL = Sse2.Subtract(Sse2.ShiftRightArithmetic(dqL, (byte)(1 + logScale)), Vector128<short>.One);
        W8 thrH = Sse2.Subtract(Sse2.ShiftRightArithmetic(dqH, (byte)(1 + logScale)), Vector128<short>.One);
        W8 eob = W8.Zero;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int qc0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int dq0 = ref MemoryMarshal.GetReference(dqcoeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        for (int k = 0; k < nCoeffs; k += 16)
        {
            W8 coefL = Load8Sat(ref c0, k), coefH = Load8Sat(ref c0, k + 8);
            W8 absL = Ssse3.Abs(coefL).AsInt16(), absH = Ssse3.Abs(coefH).AsInt16();
            W8 maskL = Sse2.CompareGreaterThan(absL, thrL), maskH = Sse2.CompareGreaterThan(absH, thrH);
            if (Sse2.MoveMask(Sse2.Or(maskL, maskH).AsByte()) != 0)
            {
                QuantFpHalf(coefL, absL, maskL, rndL, qL, dqL, logScale, ref qc0, ref dq0, k, ref is0, ref eob);
                QuantFpHalf(coefH, absH, maskH, rndH, qH, dqH, logScale, ref qc0, ref dq0, k + 8, ref is0, ref eob);
            }
            else
            {
                Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)k); Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 4));
                Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 8)); Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 12));
                Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)k); Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 4));
                Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 8)); Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 12));
            }
            if (k == 0)
            {
                rndL = rndH; qL = qH; dqL = dqH; thrL = thrH;
            }
        }
        return MaxU16(eob.AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuantFpHalf(W8 coef, W8 abs, W8 mask, W8 vRnd, W8 vQ, W8 vDq, int logScale, ref int qc0, ref int dq0, int k,
        ref short is0, ref W8 eob)
    {
        W8 absQ, q, dq, nz;
        if (logScale == 0)
        {
            absQ = Sse2.MultiplyHigh(Sse2.AddSaturate(abs, vRnd), vQ);
            q = Ssse3.Sign(absQ, coef);
            dq = Sse2.MultiplyLow(q, vDq);
            nz = Sse2.CompareGreaterThan(absQ, W8.Zero);
        }
        else if (logScale == 1)
        {
            absQ = Sse2.MultiplyHigh(Sse2.AddSaturate(abs, vRnd).AsUInt16(), vQ.AsUInt16()).AsInt16();
            q = Ssse3.Sign(absQ, coef);
            var absDq = Sse2.ShiftRightLogical(Sse2.MultiplyLow(absQ, vDq).AsUInt16(), 1).AsInt16();
            nz = Sse2.CompareGreaterThan(absQ, W8.Zero);
            dq = Ssse3.Sign(absDq, coef);
        }
        else
        {
            var tmpRnd = Sse2.And(Sse2.AddSaturate(abs, vRnd), mask);
            var qh = Sse2.ShiftLeftLogical(Sse2.MultiplyHigh(tmpRnd, vQ), 2);
            var ql = Sse2.ShiftRightLogical(Sse2.MultiplyLow(tmpRnd, vQ).AsUInt16(), 14).AsInt16();
            absQ = Sse2.Or(qh, ql);
            var dqh = Sse2.ShiftLeftLogical(Sse2.MultiplyHigh(absQ, vDq), 14);
            var dql = Sse2.ShiftRightLogical(Sse2.MultiplyLow(absQ, vDq).AsUInt16(), 2).AsInt16();
            q = Ssse3.Sign(absQ, coef);
            dq = Ssse3.Sign(Sse2.Or(dqh, dql), coef);
            nz = Sse2.CompareEqual(Sse2.CompareEqual(dq, W8.Zero), W8.Zero);
        }
        Store8(q, ref qc0, k);
        Store8(dq, ref dq0, k);
        var isc = Vector128.LoadUnsafe(ref is0, (nuint)k);
        eob = Sse2.Max(eob, Sse2.And(Sse2.Subtract(isc, nz), nz));
    }

    /// <summary><see cref="QuantizeBAvx2"/> in SSE4.1.</summary>
    internal static int QuantizeBSse41(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1,
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
        W8 zbL = Sse2.Subtract(DcAc8(zb0, zb1), Vector128<short>.One), rndL = DcAc8(rn0, rn1), qL = DcAc8(quant0, quant1);
        W8 dqL = DcAc8(dequant0, dequant1), shL = DcAc8(shift0, shift1);
        W8 zbH = Vector128.Create((short)(zb1 - 1)), rndH = Vector128.Create(rn1), qH = Vector128.Create(quant1);
        W8 dqH = Vector128.Create(dequant1), shH = Vector128.Create(shift1);
        W8 eob = W8.Zero;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int qc0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int dq0 = ref MemoryMarshal.GetReference(dqcoeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        for (int k = 0; k < nCoeffs; k += 16)
        {
            W8 coefL = Load8Sat(ref c0, k), coefH = Load8Sat(ref c0, k + 8);
            W8 absL = Ssse3.Abs(coefL).AsInt16(), absH = Ssse3.Abs(coefH).AsInt16();
            W8 zmL = Sse2.CompareGreaterThan(absL, zbL), zmH = Sse2.CompareGreaterThan(absH, zbH);
            if (Sse2.MoveMask(Sse2.Or(zmL, zmH).AsByte()) == 0)
            {
                Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)k); Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 4));
                Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 8)); Vector128<int>.Zero.StoreUnsafe(ref qc0, (nuint)(k + 12));
                Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)k); Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 4));
                Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 8)); Vector128<int>.Zero.StoreUnsafe(ref dq0, (nuint)(k + 12));
                // nz = 0: the eob is unchanged
            }
            else
            {
                QuantBHalf(coefL, absL, zmL, rndL, qL, shL, dqL, logScale, ref qc0, ref dq0, k, ref is0, ref eob);
                QuantBHalf(coefH, absH, zmH, rndH, qH, shH, dqH, logScale, ref qc0, ref dq0, k + 8, ref is0, ref eob);
            }
            if (k == 0)
            {
                rndL = rndH; qL = qH; dqL = dqH; shL = shH; zbL = zbH;
            }
        }
        return MaxU16(eob.AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuantBHalf(W8 coef, W8 abs, W8 zmask, W8 vRound, W8 vQuant, W8 vShift, W8 vDequant, int logScale,
        ref int qc0, ref int dq0, int k, ref short is0, ref W8 eob)
    {
        var tmpRnd = Sse2.And(Sse2.AddSaturate(abs, vRound), zmask);
        var tmp32b = Sse2.Add(Sse2.MultiplyHigh(tmpRnd, vQuant), tmpRnd);
        W8 tmp32, dqc;
        if (logScale == 0)
        {
            tmp32 = Sse2.MultiplyHigh(tmp32b, vShift);
            dqc = Sse2.MultiplyLow(Ssse3.Sign(tmp32, coef), vDequant);
        }
        else
        {
            var hi = Sse2.ShiftLeftLogical(Sse2.MultiplyHigh(tmp32b, vShift), (byte)logScale);
            var lo = Sse2.ShiftRightLogical(Sse2.MultiplyLow(tmp32b, vShift).AsUInt16(), (byte)(16 - logScale)).AsInt16();
            tmp32 = Sse2.Or(hi, lo);
            var dh = Sse2.ShiftLeftLogical(Sse2.MultiplyHigh(tmp32, vDequant), (byte)(16 - logScale));
            var dl = Sse2.ShiftRightLogical(Sse2.MultiplyLow(tmp32, vDequant).AsUInt16(), (byte)logScale).AsInt16();
            dqc = Ssse3.Sign(Sse2.Or(dh, dl), coef);
        }
        var nz = Sse2.CompareGreaterThan(tmp32, W8.Zero);
        Store8(Ssse3.Sign(tmp32, coef), ref qc0, k);
        Store8(dqc, ref dq0, k);
        var isc = Vector128.LoadUnsafe(ref is0, (nuint)k);
        eob = Sse2.Max(eob, Sse2.And(Sse2.Subtract(isc, nz), nz));
    }
}
