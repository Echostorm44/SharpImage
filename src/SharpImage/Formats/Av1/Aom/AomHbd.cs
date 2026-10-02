using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        if (Avx2.IsSupported && b != null && w >= 16 && (w & 15) == 0 && aOff >= 0 && bOff >= 0
            && aOff + (h - 1) * aStride + w <= a.Length && bOff + (h - 1) * bStride + w <= b.Length)
        {
            // exact sums: the 16-bit differences, their pair-summed squares per row in 32 bits (8 * 2 * 4095^2 < 2^31),
            // widened to 64 bits per row
            ref ushort ra = ref MemoryMarshal.GetArrayDataReference(a), rb = ref MemoryMarshal.GetArrayDataReference(b);
            var vsum = Vector256<int>.Zero;
            var vss = Vector256<long>.Zero;
            for (int r = 0; r < h; r++)
            {
                int ar = aOff + r * aStride, br = bOff + r * bStride;
                var rowSs = Vector256<int>.Zero;
                for (int c = 0; c < w; c += 16)
                {
                    var d = (Vector256.LoadUnsafe(ref ra, (nuint)(ar + c)) - Vector256.LoadUnsafe(ref rb, (nuint)(br + c))).AsInt16();
                    vsum += Avx2.MultiplyAddAdjacent(d, Vector256<short>.One);
                    rowSs += Avx2.MultiplyAddAdjacent(d, d);
                }
                var (lo, hi) = Vector256.Widen(rowSs);
                vss += lo + hi;
            }
            return Finish((ulong)Vector256.Sum(vss), Vector256.Sum(vsum), w * h, bd, out sse);
        }
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
        if (Avx2.IsSupported && (width & 3) == 0 && width > 0 && width <= 128 && aOff >= 0 && bOff >= 0 && height > 0
            && aOff + (height - 1) * aStride + width <= a.Length && bOff + (height - 1) * bStride + width <= b.Length)
            return SseAvx2(a, aOff, aStride, b, bOff, bStride, width, height);
        return SseScalar(a, aOff, aStride, b, bOff, bStride, width, height);
    }

    // exact: the 16-bit differences (|d| < 4096), pair-summed squares in 32 bits per row (w / 16 * 2 * 4095^2 < 2^31
    // for w <= 128), widened to 64 bits per row
    private static long SseAvx2(ushort[] a, int aOff, int aStride, ushort[] b, int bOff, int bStride, int width, int height)
    {
        ref ushort ra = ref MemoryMarshal.GetArrayDataReference(a), rb = ref MemoryMarshal.GetArrayDataReference(b);
        var acc = Vector256<long>.Zero;
        for (int y = 0; y < height; y++)
        {
            nuint ar = (nuint)(aOff + y * aStride), br = (nuint)(bOff + y * bStride);
            var row = Vector256<int>.Zero;
            int x = 0;
            for (; x + 16 <= width; x += 16)
            {
                var d = (Vector256.LoadUnsafe(ref ra, ar + (nuint)x) - Vector256.LoadUnsafe(ref rb, br + (nuint)x)).AsInt16();
                row += Avx2.MultiplyAddAdjacent(d, d);
            }
            if (x + 8 <= width)
            {
                var d = (Vector128.LoadUnsafe(ref ra, ar + (nuint)x) - Vector128.LoadUnsafe(ref rb, br + (nuint)x)).AsInt16();
                row += Vector256.Create(Sse2.MultiplyAddAdjacent(d, d), Vector128<int>.Zero);
                x += 8;
            }
            if (x < width)
            {
                var va = Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref ra, ar + (nuint)x)))).AsInt16();
                var vb = Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref rb, br + (nuint)x)))).AsInt16();
                var d = va - vb;
                row += Vector256.Create(Sse2.MultiplyAddAdjacent(d, d), Vector128<int>.Zero);
            }
            var (lo, hi) = Vector256.Widen(row);
            acc += lo + hi;
        }
        return Vector256.Sum(acc);
    }

    internal static long SseScalar(ushort[] a, int aOff, int aStride, ushort[] b, int bOff, int bStride, int width, int height)
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
        if (Vector128.IsHardwareAccelerated && (cols & 3) == 0 && cols > 0 && rows > 0 && diffOff >= 0 && srcOff >= 0 && predOff >= 0
            && diffOff + (rows - 1) * diffStride + cols <= diff.Length && srcOff + (rows - 1) * srcStride + cols <= src.Length
            && predOff + (rows - 1) * predStride + cols <= pred.Length)
        {
            ref ushort rs = ref MemoryMarshal.GetArrayDataReference(src), rp = ref MemoryMarshal.GetArrayDataReference(pred);
            ref short rd = ref MemoryMarshal.GetArrayDataReference(diff);
            for (int r = 0; r < rows; r++)
            {
                nuint d = (nuint)(diffOff + r * diffStride), s = (nuint)(srcOff + r * srcStride), p = (nuint)(predOff + r * predStride);
                if (cols == 4)
                {
                    long v = Unsafe.ReadUnaligned<long>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref rs, s)));
                    long w = Unsafe.ReadUnaligned<long>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref rp, p)));
                    Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref rd, d)),
                        (Vector128.CreateScalar(v).AsInt16() - Vector128.CreateScalar(w).AsInt16()).AsInt64().ToScalar());
                    continue;
                }
                for (int c = 0; c < cols; c += 8)
                    (Vector128.LoadUnsafe(ref rs, s + (nuint)c) - Vector128.LoadUnsafe(ref rp, p + (nuint)c)).AsInt16().StoreUnsafe(ref rd, d + (nuint)c);
            }
            return;
        }
        for (int r = 0; r < rows; r++)
        {
            int d = diffOff + r * diffStride, s = srcOff + r * srcStride, p = predOff + r * predStride;
            for (int c = 0; c < cols; c++) diff[d + c] = (short)(src[s + c] - pred[p + c]);
        }
    }

    /// <summary>A w x h 16-bit block copy.</summary>
    internal static void CopyBlock(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h)
    {
        if ((w == 4 || (w & 7) == 0) && h > 0 && srcOff >= 0 && dstOff >= 0
            && srcOff + (h - 1) * srcStride + w <= src.Length && dstOff + (h - 1) * dstStride + w <= dst.Length)
        {
            // whole rows in registers (Array.Copy per row is a call and a length dispatch each)
            ref ushort s = ref MemoryMarshal.GetArrayDataReference(src), d = ref MemoryMarshal.GetArrayDataReference(dst);
            for (int r = 0; r < h; r++)
            {
                ref byte sr = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref s, srcOff + r * srcStride));
                ref byte dr = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref d, dstOff + r * dstStride));
                if (w == 4) Unsafe.WriteUnaligned(ref dr, Unsafe.ReadUnaligned<long>(ref sr));
                else for (int c = 0; c < 2 * w; c += 16) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dr, c), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref sr, c)));
            }
            return;
        }
        for (int r = 0; r < h; r++) Array.Copy(src, srcOff + r * srcStride, dst, dstOff + r * dstStride, w);
    }

    /// <summary>av1_highbd_block_error (AVX2: exact 64-bit products): the coefficient error and energy, each rounded down
    /// by 2 (bd - 8) bits.</summary>
    internal static long BlockError(ReadOnlySpan<int> coeff, ReadOnlySpan<int> dqcoeff, int blockSize, out long ssz, int bd)
    {
        long error = 0, sqcoeff = 0;
        int shift = 2 * (bd - 8);
        int rounding = (1 << shift) >> 1;
        int i = 0;
        if (Avx2.IsSupported && coeff.Length >= blockSize && dqcoeff.Length >= blockSize)
        {
            // exact 64-bit products of the even and the odd 32-bit lanes (vpmuldq)
            ref int rc = ref MemoryMarshal.GetReference(coeff), rq = ref MemoryMarshal.GetReference(dqcoeff);
            var err = Vector256<long>.Zero; var sq = Vector256<long>.Zero;
            for (; i + 8 <= blockSize; i += 8)
            {
                var c = Vector256.LoadUnsafe(ref rc, (nuint)i);
                var d = c - Vector256.LoadUnsafe(ref rq, (nuint)i);
                var co = Vector256.ShiftRightLogical(c.AsInt64(), 32).AsInt32();
                var dd = Vector256.ShiftRightLogical(d.AsInt64(), 32).AsInt32();
                err += Avx2.Multiply(d, d) + Avx2.Multiply(dd, dd);
                sq += Avx2.Multiply(c, c) + Avx2.Multiply(co, co);
            }
            error = Vector256.Sum(err); sqcoeff = Vector256.Sum(sq);
        }
        for (; i < blockSize; i++)
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
        if (Avx2.IsSupported && srcDiff.Length >= 7 * srcStride + 8 && coeff.Length >= 64)
        {
            Hadamard8x8Avx2(ref MemoryMarshal.GetReference(srcDiff), srcStride, ref MemoryMarshal.GetReference(coeff));
            return;
        }
        Hadamard8x8Scalar(srcDiff, srcStride, coeff);
    }

    /// <summary>aom_highbd_hadamard_8x8 in vector lanes: the first pass in wrapping 16-bit lanes down the columns, a
    /// transpose, the second pass in 32-bit lanes; the coefficients come out transposed (coeff[k * 8 + idx] instead of
    /// coeff[idx * 8 + k]), a fixed permutation that the absolute sum (the only use) and the 16x16 / 32x32 combines
    /// (position-wise across the 8x8s) do not see.</summary>
    private static void Hadamard8x8Avx2(ref short s, int stride, ref int coeff)
    {
        var r0 = Vector128.LoadUnsafe(ref s); var r1 = Vector128.LoadUnsafe(ref s, (nuint)stride);
        var r2 = Vector128.LoadUnsafe(ref s, (nuint)(2 * stride)); var r3 = Vector128.LoadUnsafe(ref s, (nuint)(3 * stride));
        var r4 = Vector128.LoadUnsafe(ref s, (nuint)(4 * stride)); var r5 = Vector128.LoadUnsafe(ref s, (nuint)(5 * stride));
        var r6 = Vector128.LoadUnsafe(ref s, (nuint)(6 * stride)); var r7 = Vector128.LoadUnsafe(ref s, (nuint)(7 * stride));
        var b0 = r0 + r1; var b1 = r0 - r1; var b2 = r2 + r3; var b3 = r2 - r3;
        var b4 = r4 + r5; var b5 = r4 - r5; var b6 = r6 + r7; var b7 = r6 - r7;
        var c0 = b0 + b2; var c1 = b1 + b3; var c2 = b0 - b2; var c3 = b1 - b3;
        var c4 = b4 + b6; var c5 = b5 + b7; var c6 = b4 - b6; var c7 = b5 - b7;
        // o_k (lane = column idx) = buffer[idx * 8 + k]
        var o0 = c0 + c4; var o7 = c1 + c5; var o3 = c2 + c6; var o4 = c3 + c7;
        var o2 = c0 - c4; var o6 = c1 - c5; var o1 = c2 - c6; var o5 = c3 - c7;
        // t_m (lane idx) = o_idx[m]: the second pass's 8 inputs of column idx, down the lanes
        var a0 = Sse2.UnpackLow(o0, o1); var a1 = Sse2.UnpackHigh(o0, o1); var a2 = Sse2.UnpackLow(o2, o3); var a3 = Sse2.UnpackHigh(o2, o3);
        var a4 = Sse2.UnpackLow(o4, o5); var a5 = Sse2.UnpackHigh(o4, o5); var a6 = Sse2.UnpackLow(o6, o7); var a7 = Sse2.UnpackHigh(o6, o7);
        var e0 = Sse2.UnpackLow(a0.AsInt32(), a2.AsInt32()); var e1 = Sse2.UnpackHigh(a0.AsInt32(), a2.AsInt32());
        var e2 = Sse2.UnpackLow(a1.AsInt32(), a3.AsInt32()); var e3 = Sse2.UnpackHigh(a1.AsInt32(), a3.AsInt32());
        var e4 = Sse2.UnpackLow(a4.AsInt32(), a6.AsInt32()); var e5 = Sse2.UnpackHigh(a4.AsInt32(), a6.AsInt32());
        var e6 = Sse2.UnpackLow(a5.AsInt32(), a7.AsInt32()); var e7 = Sse2.UnpackHigh(a5.AsInt32(), a7.AsInt32());
        var t0 = Avx2.ConvertToVector256Int32(Sse2.UnpackLow(e0.AsInt64(), e4.AsInt64()).AsInt16());
        var t1 = Avx2.ConvertToVector256Int32(Sse2.UnpackHigh(e0.AsInt64(), e4.AsInt64()).AsInt16());
        var t2 = Avx2.ConvertToVector256Int32(Sse2.UnpackLow(e1.AsInt64(), e5.AsInt64()).AsInt16());
        var t3 = Avx2.ConvertToVector256Int32(Sse2.UnpackHigh(e1.AsInt64(), e5.AsInt64()).AsInt16());
        var t4 = Avx2.ConvertToVector256Int32(Sse2.UnpackLow(e2.AsInt64(), e6.AsInt64()).AsInt16());
        var t5 = Avx2.ConvertToVector256Int32(Sse2.UnpackHigh(e2.AsInt64(), e6.AsInt64()).AsInt16());
        var t6 = Avx2.ConvertToVector256Int32(Sse2.UnpackLow(e3.AsInt64(), e7.AsInt64()).AsInt16());
        var t7 = Avx2.ConvertToVector256Int32(Sse2.UnpackHigh(e3.AsInt64(), e7.AsInt64()).AsInt16());
        var d0 = t0 + t1; var d1 = t0 - t1; var d2 = t2 + t3; var d3 = t2 - t3;
        var d4 = t4 + t5; var d5 = t4 - t5; var d6 = t6 + t7; var d7 = t6 - t7;
        var f0 = d0 + d2; var f1 = d1 + d3; var f2 = d0 - d2; var f3 = d1 - d3;
        var f4 = d4 + d6; var f5 = d5 + d7; var f6 = d4 - d6; var f7 = d5 - d7;
        (f0 + f4).StoreUnsafe(ref coeff, 0); (f1 + f5).StoreUnsafe(ref coeff, 56); (f2 + f6).StoreUnsafe(ref coeff, 24); (f3 + f7).StoreUnsafe(ref coeff, 32);
        (f0 - f4).StoreUnsafe(ref coeff, 16); (f1 - f5).StoreUnsafe(ref coeff, 48); (f2 - f6).StoreUnsafe(ref coeff, 8); (f3 - f7).StoreUnsafe(ref coeff, 40);
    }

    internal static void Hadamard8x8Scalar(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<short> buffer = stackalloc short[64];
        for (int idx = 0; idx < 8; ++idx) HadamardHighbdCol8FirstPass(srcDiff.Slice(idx), srcStride, buffer.Slice(idx * 8, 8));
        for (int idx = 0; idx < 8; ++idx) HadamardHighbdCol8SecondPass(buffer.Slice(idx), 8, coeff.Slice(8 * idx, 8));
    }

    /// <summary>aom_highbd_hadamard_16x16_c.</summary>
    internal static void Hadamard16x16(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int q = 0; q < 4; ++q)
            Hadamard8x8(srcDiff.Slice((q >> 1) * 8 * srcStride + (q & 1) * 8), srcStride, coeff.Slice(q * 64, 64));
        int idx = 0;
        if (Vector256.IsHardwareAccelerated && coeff.Length >= 256)
        {
            ref int c = ref MemoryMarshal.GetReference(coeff);
            for (; idx < 64; idx += 8)
            {
                var a0 = Vector256.LoadUnsafe(ref c, (nuint)idx); var a1 = Vector256.LoadUnsafe(ref c, (nuint)idx + 64);
                var a2 = Vector256.LoadUnsafe(ref c, (nuint)idx + 128); var a3 = Vector256.LoadUnsafe(ref c, (nuint)idx + 192);
                var b0 = Vector256.ShiftRightArithmetic(a0 + a1, 1); var b1 = Vector256.ShiftRightArithmetic(a0 - a1, 1);
                var b2 = Vector256.ShiftRightArithmetic(a2 + a3, 1); var b3 = Vector256.ShiftRightArithmetic(a2 - a3, 1);
                (b0 + b2).StoreUnsafe(ref c, (nuint)idx); (b1 + b3).StoreUnsafe(ref c, (nuint)idx + 64);
                (b0 - b2).StoreUnsafe(ref c, (nuint)idx + 128); (b1 - b3).StoreUnsafe(ref c, (nuint)idx + 192);
            }
        }
        for (; idx < 64; ++idx)
        {
            int a0 = coeff[idx], a1 = coeff[idx + 64], a2 = coeff[idx + 128], a3 = coeff[idx + 192];
            int b0 = (a0 + a1) >> 1, b1 = (a0 - a1) >> 1, b2 = (a2 + a3) >> 1, b3 = (a2 - a3) >> 1;
            coeff[idx] = b0 + b2; coeff[idx + 64] = b1 + b3; coeff[idx + 128] = b0 - b2; coeff[idx + 192] = b1 - b3;
        }
    }

    /// <summary>aom_highbd_hadamard_32x32_c.</summary>
    internal static void Hadamard32x32(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int q = 0; q < 4; ++q)
            Hadamard16x16(srcDiff.Slice((q >> 1) * 16 * srcStride + (q & 1) * 16), srcStride, coeff.Slice(q * 256, 256));
        int idx = 0;
        if (Vector256.IsHardwareAccelerated && coeff.Length >= 1024)
        {
            ref int c = ref MemoryMarshal.GetReference(coeff);
            for (; idx < 256; idx += 8)
            {
                var a0 = Vector256.LoadUnsafe(ref c, (nuint)idx); var a1 = Vector256.LoadUnsafe(ref c, (nuint)idx + 256);
                var a2 = Vector256.LoadUnsafe(ref c, (nuint)idx + 512); var a3 = Vector256.LoadUnsafe(ref c, (nuint)idx + 768);
                var b0 = Vector256.ShiftRightArithmetic(a0 + a1, 2); var b1 = Vector256.ShiftRightArithmetic(a0 - a1, 2);
                var b2 = Vector256.ShiftRightArithmetic(a2 + a3, 2); var b3 = Vector256.ShiftRightArithmetic(a2 - a3, 2);
                (b0 + b2).StoreUnsafe(ref c, (nuint)idx); (b1 + b3).StoreUnsafe(ref c, (nuint)idx + 256);
                (b0 - b2).StoreUnsafe(ref c, (nuint)idx + 512); (b1 - b3).StoreUnsafe(ref c, (nuint)idx + 768);
            }
        }
        for (; idx < 256; ++idx)
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

    /// <summary>aom_fdct4x4_sse2 (the lowbd kernel libaom's nonrd block_yrd runs on high bit depth residuals): 16-bit lanes
    /// that wrap (the &lt;&lt; 4 pre-scale, the butterfly adds) and saturate (packs), as the SSE2 code does.</summary>
    internal static void Fdct4x4Sse2(ReadOnlySpan<short> input, int stride, Span<int> output)
    {
        const short c16 = 11585, c8 = 15137, c24 = 6270;
        var kA = Vector128.Create(c16, c16, c16, c16, c16, (short)-c16, c16, (short)-c16);
        var kB = Vector128.Create(c16, (short)-c16, c16, (short)-c16, c16, c16, c16, c16);
        var kC = Vector128.Create(c8, c24, c8, c24, c24, (short)-c8, c24, (short)-c8);
        var kD = Vector128.Create(c24, (short)-c8, c24, (short)-c8, c8, c24, c8, c24);
        var kE = Vector128.Create(c16);
        var kF = Vector128.Create(c16, (short)-c16, c16, (short)-c16, c16, (short)-c16, c16, (short)-c16);
        var kG = Vector128.Create(c8, c24, c8, c24, (short)-c8, (short)-c24, (short)-c8, (short)-c24);
        var kH = Vector128.Create(c24, (short)-c8, c24, (short)-c8, (short)-c24, c8, (short)-c24, c8);
        var rnd = Vector128.Create(1 << 13);
        var rnd2 = Vector128.Create((1 << 13) + ((1 << 13) << 1));
        var biasA = Vector128.Create((short)0, 1, 1, 1, 1, 1, 1, 1);
        var biasB = Vector128.Create((short)1, 0, 0, 0, 0, 0, 0, 0);
        var in0 = Vector128.Create(input[0], input[1], input[2], input[3], input[3 * stride], input[3 * stride + 1], input[3 * stride + 2], input[3 * stride + 3]);
        var in1 = Vector128.Create(input[stride], input[stride + 1], input[stride + 2], input[stride + 3], input[2 * stride], input[2 * stride + 1], input[2 * stride + 2], input[2 * stride + 3]);
        in0 = Sse2.ShiftLeftLogical(in0, 4);
        in1 = Sse2.ShiftLeftLogical(in1, 4);
        var mask = Sse2.CompareEqual(in0, biasA);
        in0 = Sse2.Add(in0, mask);
        in0 = Sse2.Add(in0, biasB);
        {
            var r0 = Sse2.UnpackLow(in0, in1);
            var r1 = Sse2.UnpackHigh(in0, in1);
            var r2 = Sse2.Shuffle(r0.AsInt32(), 0xB4).AsInt16();
            var r3 = Sse2.Shuffle(r1.AsInt32(), 0xB4).AsInt16();
            var t0 = Sse2.Add(r2, r3);
            var t1 = Sse2.Subtract(r2, r3);
            var w0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, kA), rnd), 14);
            var w1 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t1, kC), rnd), 14);
            var w2 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, kB), rnd), 14);
            var w3 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t1, kD), rnd), 14);
            var x0 = Sse2.PackSignedSaturate(w0, w1);
            var x1 = Sse2.PackSignedSaturate(w2, w3);
            in0 = Sse2.Shuffle(x0.AsInt32(), 0xD8).AsInt16();
            in1 = Sse2.Shuffle(x1.AsInt32(), 0x8D).AsInt16();
        }
        {
            var t0 = Sse2.Add(in0, in1);
            var t1 = Sse2.Subtract(in0, in1);
            var w0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, kE), rnd2), 16);
            var w1 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, kF), rnd2), 16);
            var w2 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t1, kG), rnd2), 16);
            var w3 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t1, kH), rnd2), 16);
            in0 = Sse2.PackSignedSaturate(w0, w2);
            in1 = Sse2.PackSignedSaturate(w1, w3);
        }
        for (int i = 0; i < 8; i++) { output[i] = in0.GetElement(i); output[8 + i] = in1.GetElement(i); }
    }

    /// <summary>aom_hadamard_16x16_avx2 with its int16 lanes wrapping in the last stage (the 8-bit port's H16x16 sums in
    /// 32 bits, identical for 8-bit residuals): what libaom's nonrd block_yrd gets from high bit depth residuals.</summary>
    internal static void Hadamard16x16Lbd(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            AomHadamard.H8x8(srcDiff.Slice((idx >> 1) * 8 * srcStride + (idx & 1) * 8), srcStride, coeff.Slice(idx * 64));
        for (int idx = 0; idx < 64; ++idx)
        {
            short a0 = (short)coeff[idx], a1 = (short)coeff[idx + 64], a2 = (short)coeff[idx + 128], a3 = (short)coeff[idx + 192];
            short b0 = (short)((short)(a0 + a1) >> 1), b1 = (short)((short)(a0 - a1) >> 1);
            short b2 = (short)((short)(a2 + a3) >> 1), b3 = (short)((short)(a2 - a3) >> 1);
            coeff[idx] = (short)(b0 + b2);
            coeff[idx + 64] = (short)(b1 + b3);
            coeff[idx + 128] = (short)(b0 - b2);
            coeff[idx + 192] = (short)(b1 - b3);
        }
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 4; j++) (coeff[i * 16 + 4 + j], coeff[i * 16 + 8 + j]) = (coeff[i * 16 + 8 + j], coeff[i * 16 + 4 + j]);
    }

    /// <summary>aom_highbd_sad (any size): the exact sum of absolute differences.</summary>
    internal static uint Sad(ushort[] a, int aOff, int aStride, ushort[] b, int bOff, int bStride, int w, int h)
    {
        if (Vector256.IsHardwareAccelerated && w >= 16 && (w & 15) == 0 && aOff >= 0 && bOff >= 0
            && aOff + (h - 1) * aStride + w <= a.Length && bOff + (h - 1) * bStride + w <= b.Length)
        {
            // |a - b| per 16-bit lane (max - min), widened to 32 bits per row (exact: no lane overflows)
            ref ushort ra = ref MemoryMarshal.GetArrayDataReference(a), rb = ref MemoryMarshal.GetArrayDataReference(b);
            var acc = Vector256<uint>.Zero;
            for (int r = 0; r < h; r++)
            {
                int ar = aOff + r * aStride, br = bOff + r * bStride;
                var rowAcc = Vector256<ushort>.Zero;
                for (int c = 0; c < w; c += 16)
                {
                    var va = Vector256.LoadUnsafe(ref ra, (nuint)(ar + c));
                    var vb = Vector256.LoadUnsafe(ref rb, (nuint)(br + c));
                    // a row is at most 8 vectors (w <= 128) and 8 * 4095 < 65536: the 16-bit row sums stay exact
                    rowAcc += Vector256.Max(va, vb) - Vector256.Min(va, vb);
                }
                var (lo, hi) = Vector256.Widen(rowAcc);
                acc += lo + hi;
            }
            return Vector256.Sum(acc);
        }
        if (Vector128.IsHardwareAccelerated && w == 8 && aOff >= 0 && bOff >= 0
            && aOff + (h - 1) * aStride + w <= a.Length && bOff + (h - 1) * bStride + w <= b.Length)
        {
            ref ushort ra = ref MemoryMarshal.GetArrayDataReference(a), rb = ref MemoryMarshal.GetArrayDataReference(b);
            var acc = Vector128<uint>.Zero;
            for (int r = 0; r < h; r++)
            {
                var va = Vector128.LoadUnsafe(ref ra, (nuint)(aOff + r * aStride));
                var vb = Vector128.LoadUnsafe(ref rb, (nuint)(bOff + r * bStride));
                var (lo, hi) = Vector128.Widen(Vector128.Max(va, vb) - Vector128.Min(va, vb));
                acc += lo + hi;
            }
            return Vector128.Sum(acc);
        }
        uint sad = 0;
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++) sad += (uint)Math.Abs(a[aOff + r * aStride + c] - b[bOff + r * bStride + c]);
        return sad;
    }

    /// <summary>clip_pixel_highbd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ClipPixel(int val, int bd) => (ushort)Math.Clamp(val, 0, (1 << bd) - 1);
}
