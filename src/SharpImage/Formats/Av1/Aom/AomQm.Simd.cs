using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// The quantization-matrix quantizers (quantize_fp_helper_c / aom_quantize_b_helper_c with qm, which libaom runs in C)
// over the coefficients in raster order, 8 at a time: every coefficient's result is independent of the order, every
// position is written (the C's memset + the passing ones), and the eob is the largest scan index (iscan) of a nonzero
// level. The b helper's pre-scan only skips trailing coefficients that fail the same zbin test the main loop applies.
// The 39-bit products are formed in doubles (exact below 2^53; scaling by 2^-k and floor are exact).
internal static partial class AomQm
{
    internal static bool QmSimdSupported => Avx2.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> FloorShift(Vector256<int> a, Vector256<int> b, double scale)
    {
        // floor(a * b * scale) for a, b >= 0, a * b < 2^53, the result < 2^31
        var s = Vector256.Create(scale);
        var lo = Avx.RoundToNegativeInfinity(Avx.Multiply(Avx.Multiply(Avx.ConvertToVector256Double(a.GetLower()), Avx.ConvertToVector256Double(b.GetLower())), s));
        var hi = Avx.RoundToNegativeInfinity(Avx.Multiply(Avx.Multiply(Avx.ConvertToVector256Double(a.GetUpper()), Avx.ConvertToVector256Double(b.GetUpper())), s));
        return Vector256.Create(Avx.ConvertToVector128Int32WithTruncation(lo), Avx.ConvertToVector128Int32WithTruncation(hi));
    }

    /// <summary>quantize_fp_helper_c with matrices, vectorised (see above). Returns the eob.</summary>
    internal static int QuantizeFpHelperAvx2(ReadOnlySpan<int> coeff, int nCoeffs, ReadOnlySpan<short> iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if ((nCoeffs & 7) != 0 || coeff.Length < nCoeffs || iscan.Length < nCoeffs || qm.Length < nCoeffs || iqm.Length < nCoeffs
            || qcoeff.Length < nCoeffs || dqcoeff.Length < nCoeffs)
            throw new ArgumentException("quantizer buffers");
        int r0 = (round0 + ((1 << logScale) >> 1)) >> logScale, r1 = (round1 + ((1 << logScale) >> 1)) >> logScale;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref short is0 = ref MemoryMarshal.GetReference(iscan);
        ref byte wt0 = ref MemoryMarshal.GetArrayDataReference(qm);
        ref byte iw0 = ref MemoryMarshal.GetArrayDataReference(iqm);
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int dq0 = ref MemoryMarshal.GetReference(dqcoeff);
        var lane0 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0);
        // the DC's parameters in lane 0 of the first group, the AC's elsewhere
        var dqV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)dequant0), Vector256.Create((int)dequant1));
        var rndV = Vector256.ConditionalSelect(lane0, Vector256.Create(r0), Vector256.Create(r1));
        var qV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quant0), Vector256.Create((int)quant1));
        var max16 = Vector256.Create((int)short.MaxValue);
        var rq = Vector256.Create(1 << (AomQmBits - 1));
        double scale = 1.0 / (1L << (16 - logScale + AomQmBits));
        var eobV = Vector256.Create(-1);
        for (int i = 0; i < nCoeffs; i += 8)
        {
            if (i == 8)
            {
                dqV = Vector256.Create((int)dequant1); rndV = Vector256.Create(r1); qV = Vector256.Create((int)quant1);
            }
            var c = Vector256.LoadUnsafe(ref c0, (nuint)i);
            var sign = Vector256.ShiftRightArithmetic(c, 31);
            var abs = (c ^ sign) - sign;
            var wt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref wt0, i))).AsByte());
            var iwt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref iw0, i))).AsByte());
            var dequant = Vector256.ShiftRightArithmetic(dqV * iwt + rq, AomQmBits);
            // abs_coeff * wt >= dq << (AOM_QM_BITS - (1 + log_scale)) (abs * wt fits: |coeff| < 2^23 for 8-bit)
            var pass = Vector256.GreaterThanOrEqual(abs * wt, Vector256.ShiftLeft(dqV, AomQmBits - (1 + logScale)));
            var a2 = Vector256.Min(abs + rndV, max16);
            var tmp32 = FloorShift(a2 * wt, qV, scale) & pass;
            var q = (tmp32 ^ sign) - sign;
            var absDq = Vector256.ShiftRightArithmetic(tmp32 * dequant, logScale);
            (q).StoreUnsafe(ref q0, (nuint)i);
            ((absDq ^ sign) - sign).StoreUnsafe(ref dq0, (nuint)i);
            var isc = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref is0, (nuint)i));
            eobV = Vector256.Max(eobV, Vector256.ConditionalSelect(Vector256.Equals(tmp32, Vector256<int>.Zero), Vector256.Create(-1), isc));
        }
        int eob = -1;
        for (int k = 0; k < 8; k++) eob = Math.Max(eob, eobV.GetElement(k));
        return eob + 1;
    }

    /// <summary>aom_quantize_b_helper_c with matrices, vectorised (see above). Returns the eob.</summary>
    internal static int QuantizeBHelperAvx2(ReadOnlySpan<int> coeff, int nCoeffs, ReadOnlySpan<short> iscan, short zbin0, short zbin1,
        short round0, short round1, short quant0, short quant1, short shift0, short shift1, short dequant0, short dequant1,
        byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if ((nCoeffs & 7) != 0 || coeff.Length < nCoeffs || iscan.Length < nCoeffs || qm.Length < nCoeffs || iqm.Length < nCoeffs
            || qcoeff.Length < nCoeffs || dqcoeff.Length < nCoeffs)
            throw new ArgumentException("quantizer buffers");
        int half = (1 << logScale) >> 1;
        int zb0 = (zbin0 + half) >> logScale, zb1 = (zbin1 + half) >> logScale;
        int rn0 = (round0 + half) >> logScale, rn1 = (round1 + half) >> logScale;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref short is0 = ref MemoryMarshal.GetReference(iscan);
        ref byte wt0 = ref MemoryMarshal.GetArrayDataReference(qm);
        ref byte iw0 = ref MemoryMarshal.GetArrayDataReference(iqm);
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int dq0 = ref MemoryMarshal.GetReference(dqcoeff);
        var lane0 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0);
        var zbV = Vector256.ConditionalSelect(lane0, Vector256.Create(zb0 << AomQmBits), Vector256.Create(zb1 << AomQmBits));
        var rndV = Vector256.ConditionalSelect(lane0, Vector256.Create(rn0), Vector256.Create(rn1));
        var qV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quant0), Vector256.Create((int)quant1));
        var shV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)shift0), Vector256.Create((int)shift1));
        var dqV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)dequant0), Vector256.Create((int)dequant1));
        var max16 = Vector256.Create((int)short.MaxValue);
        var rq = Vector256.Create(1 << (AomQmBits - 1));
        double scale16 = 1.0 / 65536, scaleOut = 1.0 / (1L << (16 - logScale + AomQmBits));
        var eobV = Vector256.Create(-1);
        for (int i = 0; i < nCoeffs; i += 8)
        {
            if (i == 8)
            {
                zbV = Vector256.Create(zb1 << AomQmBits); rndV = Vector256.Create(rn1); qV = Vector256.Create((int)quant1);
                shV = Vector256.Create((int)shift1); dqV = Vector256.Create((int)dequant1);
            }
            var c = Vector256.LoadUnsafe(ref c0, (nuint)i);
            var sign = Vector256.ShiftRightArithmetic(c, 31);
            var abs = (c ^ sign) - sign;
            var wt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref wt0, i))).AsByte());
            var iwt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref iw0, i))).AsByte());
            var pass = Vector256.GreaterThanOrEqual(abs * wt, zbV);
            var tmp = Vector256.Min(abs + rndV, max16) * wt;                 // < 2^23
            var t1 = FloorShift(tmp, qV, scale16) + tmp;                      // ((tmp * quant) >> 16) + tmp < 2^24
            var tmp32 = FloorShift(t1, shV, scaleOut) & pass;
            var dequant = Vector256.ShiftRightArithmetic(dqV * iwt + rq, AomQmBits);
            var absDq = Vector256.ShiftRightArithmetic(tmp32 * dequant, logScale);
            ((tmp32 ^ sign) - sign).StoreUnsafe(ref q0, (nuint)i);
            ((absDq ^ sign) - sign).StoreUnsafe(ref dq0, (nuint)i);
            var isc = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref is0, (nuint)i));
            eobV = Vector256.Max(eobV, Vector256.ConditionalSelect(Vector256.Equals(tmp32, Vector256<int>.Zero), Vector256.Create(-1), isc));
        }
        int eob = -1;
        for (int k = 0; k < 8; k++) eob = Math.Max(eob, eobV.GetElement(k));
        return eob + 1;
    }
}
