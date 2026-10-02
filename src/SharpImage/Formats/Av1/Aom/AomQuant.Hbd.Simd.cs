using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// The high bit depth quantizers of AomQuantizeHbd in 8 int32 lanes (av1_highbd_quantize_fp_avx2 /
// aom_highbd_quantize_b_avx2 run 8 lanes too): the same per-coefficient arithmetic, the 64-bit products formed by
// vpmuldq as the AVX2 kernels do, the eob as the largest iscan + 1 of a nonzero lane.
internal static partial class AomQuantizeHbdSimd
{
    internal static bool Supported => Avx2.IsSupported;

    // the low 32 bits of (a * b) >> sh per lane, the products in 64 bits (vpmuldq on the even and the odd lanes; a
    // logical and an arithmetic shift agree on those bits for sh <= 32)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> FloorMulShift(Vector256<int> a, Vector256<int> b, int sh)
    {
        var even = Vector256.ShiftRightLogical(Avx2.Multiply(a, b), sh).AsInt32();
        var odd = Vector256.ShiftLeft(Avx2.Multiply(Vector256.ShiftRightLogical(a.AsInt64(), 32).AsInt32(),
            Vector256.ShiftRightLogical(b.AsInt64(), 32).AsInt32()), 32 - sh).AsInt32();
        return Avx2.Blend(even, odd, 0b10101010);
    }

    /// <summary>AomQuantizeHbd.QuantizeFp vectorised (n a multiple of 8). Returns the eob.</summary>
    internal static int QuantizeFp(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if ((nCoeffs & 7) != 0 || coeff.Length < nCoeffs || iscan.Length < nCoeffs || qcoeff.Length < nCoeffs || dqcoeff.Length < nCoeffs)
            throw new ArgumentException("quantizer buffers");
        int r0 = round0, r1 = round1;
        if (logScale > 0) { r0 = (r0 + (1 << (logScale - 1))) >> logScale; r1 = (r1 + (1 << (logScale - 1))) >> logScale; }
        var lane0 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0);
        var rnd = Vector256.ConditionalSelect(lane0, Vector256.Create(r0), Vector256.Create(r1));
        var q = Vector256.ConditionalSelect(lane0, Vector256.Create((int)(ushort)quant0), Vector256.Create((int)(ushort)quant1));
        var dqv = Vector256.ConditionalSelect(lane0, Vector256.Create((int)(ushort)dequant0), Vector256.Create((int)(ushort)dequant1));
        int sh = 16 - logScale;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int d0 = ref MemoryMarshal.GetReference(dqcoeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        var eob = Vector256<int>.Zero;
        for (int i = 0; i < nCoeffs; i += 8)
        {
            if (i == 8) { rnd = Vector256.Create(r1); q = Vector256.Create((int)(ushort)quant1); dqv = Vector256.Create((int)(ushort)dequant1); }
            var c = Vector256.LoadUnsafe(ref c0, (nuint)i);
            var abs = Vector256.Abs(c);
            var lvl = FloorMulShift(abs + rnd, q, sh);
            // zeroed where dequant > |c| << (1 + log_scale), and for c == 0
            var keep = ~Vector256.GreaterThan(dqv, Vector256.ShiftLeft(abs, 1 + logScale)) & ~Vector256.Equals(c, Vector256<int>.Zero);
            lvl &= keep;
            var dq = Vector256.ShiftRightArithmetic(lvl * dqv, logScale);
            var neg = Vector256.LessThan(c, Vector256<int>.Zero);
            Vector256.ConditionalSelect(neg, -lvl, lvl).StoreUnsafe(ref q0, (nuint)i);
            Vector256.ConditionalSelect(neg, -dq, dq).StoreUnsafe(ref d0, (nuint)i);
            var isc = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref is0, (nuint)i)) + Vector256<int>.One;
            eob = Vector256.Max(eob, isc & ~Vector256.Equals(dq, Vector256<int>.Zero));
        }
        int e = 0;
        for (int k = 0; k < 8; k++) e = Math.Max(e, eob.GetElement(k));
        return e;
    }

    /// <summary>AomQuantizeHbd.QuantizeB vectorised (n a multiple of 8). Returns the eob.</summary>
    internal static int QuantizeB(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1, short round0, short round1,
        short quant0, short quant1, short quantShift0, short quantShift1, short dequant0, short dequant1, int logScale,
        Span<int> qcoeff, Span<int> dqcoeff)
    {
        if ((nCoeffs & 7) != 0 || coeff.Length < nCoeffs || iscan.Length < nCoeffs || qcoeff.Length < nCoeffs || dqcoeff.Length < nCoeffs)
            throw new ArgumentException("quantizer buffers");
        int z0 = zbin0, z1 = zbin1, r0 = round0, r1 = round1;
        if (logScale > 0)
        {
            int rn = 1 << (logScale - 1);
            z0 = (z0 + rn) >> logScale; z1 = (z1 + rn) >> logScale;
            r0 = (r0 + rn) >> logScale; r1 = (r1 + rn) >> logScale;
        }
        var lane0 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0);
        var zb = Vector256.ConditionalSelect(lane0, Vector256.Create(z0), Vector256.Create(z1));
        var rnd = Vector256.ConditionalSelect(lane0, Vector256.Create(r0), Vector256.Create(r1));
        var q = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quant0), Vector256.Create((int)quant1));
        var qs = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quantShift0), Vector256.Create((int)quantShift1));
        var dqv = Vector256.ConditionalSelect(lane0, Vector256.Create((int)dequant0), Vector256.Create((int)dequant1));
        int sh = 16 - logScale;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int d0 = ref MemoryMarshal.GetReference(dqcoeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        var eob = Vector256<int>.Zero;
        for (int i = 0; i < nCoeffs; i += 8)
        {
            if (i == 8)
            {
                zb = Vector256.Create(z1); rnd = Vector256.Create(r1); q = Vector256.Create((int)quant1); qs = Vector256.Create((int)quantShift1);
                dqv = Vector256.Create((int)dequant1);
            }
            var c = Vector256.LoadUnsafe(ref c0, (nuint)i);
            var abs = Vector256.Abs(c);
            var pass = ~Vector256.LessThan(abs, zb);
            var tmpRnd = abs + rnd;
            var tmp2 = FloorMulShift(tmpRnd, q, 16) + tmpRnd;
            var absQ = FloorMulShift(tmp2, qs, sh) & pass;
            var absDq = Vector256.ShiftRightLogical(absQ * dqv, logScale);
            var neg = Vector256.LessThan(c, Vector256<int>.Zero);
            Vector256.ConditionalSelect(neg, -absQ, absQ).StoreUnsafe(ref q0, (nuint)i);
            Vector256.ConditionalSelect(neg, -absDq, absDq).StoreUnsafe(ref d0, (nuint)i);
            var isc = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref is0, (nuint)i)) + Vector256<int>.One;
            eob = Vector256.Max(eob, isc & Vector256.GreaterThan(absQ, Vector256<int>.Zero));
        }
        int e = 0;
        for (int k = 0; k < 8; k++) e = Math.Max(e, eob.GetElement(k));
        return e;
    }
    /// <summary>AomQuantizeHbd.QuantizeFpHelperQm over the coefficients in raster order, 8 at a time (every position is
    /// written, the eob the largest iscan of a nonzero level, as AomQm's 8-bit vector quantizers). Returns the eob.</summary>
    internal static int QuantizeFpHelperQm(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short round0, short round1,
        short quant0, short quant1, short dequant0, short dequant1, byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if ((nCoeffs & 7) != 0 || coeff.Length < nCoeffs || iscan.Length < nCoeffs || qm.Length < nCoeffs || iqm.Length < nCoeffs
            || qcoeff.Length < nCoeffs || dqcoeff.Length < nCoeffs)
            throw new ArgumentException("quantizer buffers");
        const int QmBits = 5;
        int half = (1 << logScale) >> 1;
        int r0 = (round0 + half) >> logScale, r1 = (round1 + half) >> logScale;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        ref byte wt0 = ref MemoryMarshal.GetArrayDataReference(qm);
        ref byte iw0 = ref MemoryMarshal.GetArrayDataReference(iqm);
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int d0 = ref MemoryMarshal.GetReference(dqcoeff);
        var lane0 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0);
        var dqV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)dequant0), Vector256.Create((int)dequant1));
        var rndV = Vector256.ConditionalSelect(lane0, Vector256.Create(r0), Vector256.Create(r1));
        var qV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quant0), Vector256.Create((int)quant1));
        var rq = Vector256.Create(1 << (QmBits - 1));
        int sh = 16 - logScale + QmBits;
        var eobV = Vector256<int>.Zero;
        for (int i = 0; i < nCoeffs; i += 8)
        {
            if (i == 8) { dqV = Vector256.Create((int)dequant1); rndV = Vector256.Create(r1); qV = Vector256.Create((int)quant1); }
            var c = Vector256.LoadUnsafe(ref c0, (nuint)i);
            var sign = Vector256.ShiftRightArithmetic(c, 31);
            var abs = (c ^ sign) - sign;
            var wt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref wt0, i))).AsByte());
            var iwt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref iw0, i))).AsByte());
            var dequant = Vector256.ShiftRightArithmetic(dqV * iwt + rq, QmBits);
            var pass = Vector256.GreaterThanOrEqual(abs * wt, Vector256.ShiftLeft(dqV, QmBits - (1 + logScale)));
            var absQ = FloorMulShift((abs + rndV) * wt, qV, sh) & pass;
            var absDq = Vector256.ShiftRightArithmetic(absQ * dequant, logScale);
            ((absQ ^ sign) - sign).StoreUnsafe(ref q0, (nuint)i);
            ((absDq ^ sign) - sign).StoreUnsafe(ref d0, (nuint)i);
            var isc = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref is0, (nuint)i)) + Vector256<int>.One;
            eobV = Vector256.Max(eobV, isc & ~Vector256.Equals(absQ, Vector256<int>.Zero));
        }
        int e = 0;
        for (int k = 0; k < 8; k++) e = Math.Max(e, eobV.GetElement(k));
        return e;
    }

    /// <summary>AomQuantizeHbd.QuantizeBHelperQm in raster order, 8 at a time (see QuantizeFpHelperQm). Returns the eob.</summary>
    internal static int QuantizeBHelperQm(ReadOnlySpan<int> coeff, int nCoeffs, short[] iscan, short zbin0, short zbin1,
        short round0, short round1, short quant0, short quant1, short quantShift0, short quantShift1, short dequant0, short dequant1,
        byte[] qm, byte[] iqm, int logScale, Span<int> qcoeff, Span<int> dqcoeff)
    {
        if ((nCoeffs & 7) != 0 || coeff.Length < nCoeffs || iscan.Length < nCoeffs || qm.Length < nCoeffs || iqm.Length < nCoeffs
            || qcoeff.Length < nCoeffs || dqcoeff.Length < nCoeffs)
            throw new ArgumentException("quantizer buffers");
        const int QmBits = 5;
        int half = (1 << logScale) >> 1;
        int z0 = (zbin0 + half) >> logScale, z1 = (zbin1 + half) >> logScale;
        int r0 = (round0 + half) >> logScale, r1 = (round1 + half) >> logScale;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref short is0 = ref MemoryMarshal.GetArrayDataReference(iscan);
        ref byte wt0 = ref MemoryMarshal.GetArrayDataReference(qm);
        ref byte iw0 = ref MemoryMarshal.GetArrayDataReference(iqm);
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref int d0 = ref MemoryMarshal.GetReference(dqcoeff);
        var lane0 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0);
        var zbV = Vector256.ConditionalSelect(lane0, Vector256.Create(z0 << QmBits), Vector256.Create(z1 << QmBits));
        var rndV = Vector256.ConditionalSelect(lane0, Vector256.Create(r0), Vector256.Create(r1));
        var qV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quant0), Vector256.Create((int)quant1));
        var shV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)quantShift0), Vector256.Create((int)quantShift1));
        var dqV = Vector256.ConditionalSelect(lane0, Vector256.Create((int)dequant0), Vector256.Create((int)dequant1));
        var rq = Vector256.Create(1 << (QmBits - 1));
        int shOut = 16 - logScale + QmBits;
        var eobV = Vector256<int>.Zero;
        for (int i = 0; i < nCoeffs; i += 8)
        {
            if (i == 8)
            {
                zbV = Vector256.Create(z1 << QmBits); rndV = Vector256.Create(r1); qV = Vector256.Create((int)quant1);
                shV = Vector256.Create((int)quantShift1); dqV = Vector256.Create((int)dequant1);
            }
            var c = Vector256.LoadUnsafe(ref c0, (nuint)i);
            var sign = Vector256.ShiftRightArithmetic(c, 31);
            var abs = (c ^ sign) - sign;
            var wt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref wt0, i))).AsByte());
            var iwt = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref iw0, i))).AsByte());
            // c * wt >= zbin << 5 or <= -(zbin << 5): |c| * wt >= zbin << 5
            var pass = Vector256.GreaterThanOrEqual(abs * wt, zbV);
            var tmpw = (abs + rndV) * wt;
            var tmp2 = FloorMulShift(tmpw, qV, 16) + tmpw;
            var absQ = FloorMulShift(tmp2, shV, shOut) & pass;
            var dequant = Vector256.ShiftRightArithmetic(dqV * iwt + rq, QmBits);
            var absDq = Vector256.ShiftRightArithmetic(absQ * dequant, logScale);
            ((absQ ^ sign) - sign).StoreUnsafe(ref q0, (nuint)i);
            ((absDq ^ sign) - sign).StoreUnsafe(ref d0, (nuint)i);
            var isc = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref is0, (nuint)i)) + Vector256<int>.One;
            eobV = Vector256.Max(eobV, isc & ~Vector256.Equals(absQ, Vector256<int>.Zero));
        }
        int e = 0;
        for (int k = 0; k < 8; k++) e = Math.Max(e, eobV.GetElement(k));
        return e;
    }
}
