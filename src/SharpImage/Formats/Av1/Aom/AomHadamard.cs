using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        var bufferSA = new StackArr16<short>(); Span<short> buffer = bufferSA;
        var buffer2SA = new StackArr16<short>(); Span<short> buffer2 = buffer2SA;
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

    /// <summary>hadamard_col8 on 8 lane vectors (every lane an independent column), int16 wrapping like the C's int16
    /// temporaries; the outputs in the C's slot order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Col8V(Vector128<short> s0, Vector128<short> s1, Vector128<short> s2, Vector128<short> s3, Vector128<short> s4,
        Vector128<short> s5, Vector128<short> s6, Vector128<short> s7, out Vector128<short> o0, out Vector128<short> o1,
        out Vector128<short> o2, out Vector128<short> o3, out Vector128<short> o4, out Vector128<short> o5, out Vector128<short> o6,
        out Vector128<short> o7)
    {
        var b0 = s0 + s1; var b1 = s0 - s1; var b2 = s2 + s3; var b3 = s2 - s3;
        var b4 = s4 + s5; var b5 = s4 - s5; var b6 = s6 + s7; var b7 = s6 - s7;
        var c0 = b0 + b2; var c1 = b1 + b3; var c2 = b0 - b2; var c3 = b1 - b3;
        var c4 = b4 + b6; var c5 = b5 + b7; var c6 = b4 - b6; var c7 = b5 - b7;
        o0 = c0 + c4; o7 = c1 + c5; o3 = c2 + c6; o4 = c3 + c7;
        o2 = c0 - c4; o6 = c1 - c5; o1 = c2 - c6; o5 = c3 - c7;
    }

    /// <summary>8x8 int16 transpose.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8(ref Vector128<short> r0, ref Vector128<short> r1, ref Vector128<short> r2, ref Vector128<short> r3,
        ref Vector128<short> r4, ref Vector128<short> r5, ref Vector128<short> r6, ref Vector128<short> r7)
    {
        var a0 = Sse2.UnpackLow(r0, r1); var a1 = Sse2.UnpackHigh(r0, r1);
        var a2 = Sse2.UnpackLow(r2, r3); var a3 = Sse2.UnpackHigh(r2, r3);
        var a4 = Sse2.UnpackLow(r4, r5); var a5 = Sse2.UnpackHigh(r4, r5);
        var a6 = Sse2.UnpackLow(r6, r7); var a7 = Sse2.UnpackHigh(r6, r7);
        var b0 = Sse2.UnpackLow(a0.AsInt32(), a2.AsInt32()); var b1 = Sse2.UnpackHigh(a0.AsInt32(), a2.AsInt32());
        var b2 = Sse2.UnpackLow(a1.AsInt32(), a3.AsInt32()); var b3 = Sse2.UnpackHigh(a1.AsInt32(), a3.AsInt32());
        var b4 = Sse2.UnpackLow(a4.AsInt32(), a6.AsInt32()); var b5 = Sse2.UnpackHigh(a4.AsInt32(), a6.AsInt32());
        var b6 = Sse2.UnpackLow(a5.AsInt32(), a7.AsInt32()); var b7 = Sse2.UnpackHigh(a5.AsInt32(), a7.AsInt32());
        r0 = Sse2.UnpackLow(b0.AsInt64(), b4.AsInt64()).AsInt16(); r1 = Sse2.UnpackHigh(b0.AsInt64(), b4.AsInt64()).AsInt16();
        r2 = Sse2.UnpackLow(b1.AsInt64(), b5.AsInt64()).AsInt16(); r3 = Sse2.UnpackHigh(b1.AsInt64(), b5.AsInt64()).AsInt16();
        r4 = Sse2.UnpackLow(b2.AsInt64(), b6.AsInt64()).AsInt16(); r5 = Sse2.UnpackHigh(b2.AsInt64(), b6.AsInt64()).AsInt16();
        r6 = Sse2.UnpackLow(b3.AsInt64(), b7.AsInt64()).AsInt16(); r7 = Sse2.UnpackHigh(b3.AsInt64(), b7.AsInt64()).AsInt16();
    }

    /// <summary>aom_hadamard_8x8: the first pass runs every column at once on the 8 row vectors; its slot vectors,
    /// transposed, are the second pass's columns; the second pass's slot vectors are then exactly the output rows (the
    /// C's final transpose), sign-extended to tran_low_t.</summary>
    internal static void H8x8(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        if (!Sse2.IsSupported || coeff.Length < 64) { H8x8Scalar(srcDiff, srcStride, coeff); return; }
        ref short s = ref MemoryMarshal.GetReference(srcDiff);
        var r0 = Vector128.LoadUnsafe(ref s, 0); var r1 = Vector128.LoadUnsafe(ref s, (nuint)srcStride);
        var r2 = Vector128.LoadUnsafe(ref s, (nuint)(2 * srcStride)); var r3 = Vector128.LoadUnsafe(ref s, (nuint)(3 * srcStride));
        var r4 = Vector128.LoadUnsafe(ref s, (nuint)(4 * srcStride)); var r5 = Vector128.LoadUnsafe(ref s, (nuint)(5 * srcStride));
        var r6 = Vector128.LoadUnsafe(ref s, (nuint)(6 * srcStride)); var r7 = Vector128.LoadUnsafe(ref s, (nuint)(7 * srcStride));
        Col8V(r0, r1, r2, r3, r4, r5, r6, r7, out var t0, out var t1, out var t2, out var t3, out var t4, out var t5, out var t6, out var t7);
        Transpose8(ref t0, ref t1, ref t2, ref t3, ref t4, ref t5, ref t6, ref t7);
        Col8V(t0, t1, t2, t3, t4, t5, t6, t7, out var u0, out var u1, out var u2, out var u3, out var u4, out var u5, out var u6, out var u7);
        ref int c = ref MemoryMarshal.GetReference(coeff);
        StoreTranLow8(u0, ref c, 0); StoreTranLow8(u1, ref c, 8); StoreTranLow8(u2, ref c, 16); StoreTranLow8(u3, ref c, 24);
        StoreTranLow8(u4, ref c, 32); StoreTranLow8(u5, ref c, 40); StoreTranLow8(u6, ref c, 48); StoreTranLow8(u7, ref c, 56);
    }

    // 8 int16 lanes sign-extended to tran_low_t at c[off]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreTranLow8(Vector128<short> u, ref int c, nuint off)
    {
        if (Avx2.IsSupported) { Avx2.ConvertToVector256Int32(u).StoreUnsafe(ref c, off); return; }
        Vector128.WidenLower(u).StoreUnsafe(ref c, off);
        Vector128.WidenUpper(u).StoreUnsafe(ref c, off + 4);
    }

    /// <summary>aom_hadamard_lp_8x8 (SSE2 kernel / the C reference's transposed output): <see cref="H8x8"/>'s values
    /// kept in int16.</summary>
    internal static void H8x8Lp(ReadOnlySpan<short> srcDiff, int srcStride, Span<short> coeff)
    {
        ref short s = ref MemoryMarshal.GetReference(srcDiff);
        var r0 = Vector128.LoadUnsafe(ref s, 0); var r1 = Vector128.LoadUnsafe(ref s, (nuint)srcStride);
        var r2 = Vector128.LoadUnsafe(ref s, (nuint)(2 * srcStride)); var r3 = Vector128.LoadUnsafe(ref s, (nuint)(3 * srcStride));
        var r4 = Vector128.LoadUnsafe(ref s, (nuint)(4 * srcStride)); var r5 = Vector128.LoadUnsafe(ref s, (nuint)(5 * srcStride));
        var r6 = Vector128.LoadUnsafe(ref s, (nuint)(6 * srcStride)); var r7 = Vector128.LoadUnsafe(ref s, (nuint)(7 * srcStride));
        Col8V(r0, r1, r2, r3, r4, r5, r6, r7, out var t0, out var t1, out var t2, out var t3, out var t4, out var t5, out var t6, out var t7);
        Transpose8(ref t0, ref t1, ref t2, ref t3, ref t4, ref t5, ref t6, ref t7);
        Col8V(t0, t1, t2, t3, t4, t5, t6, t7, out var u0, out var u1, out var u2, out var u3, out var u4, out var u5, out var u6, out var u7);
        ref short c = ref MemoryMarshal.GetReference(coeff);
        u0.StoreUnsafe(ref c, 0); u1.StoreUnsafe(ref c, 8); u2.StoreUnsafe(ref c, 16); u3.StoreUnsafe(ref c, 24);
        u4.StoreUnsafe(ref c, 32); u5.StoreUnsafe(ref c, 40); u6.StoreUnsafe(ref c, 48); u7.StoreUnsafe(ref c, 56);
    }

    private static void H8x8Scalar(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        var bufferSA = new StackArr64<short>(); Span<short> buffer = bufferSA;
        var buffer2SA = new StackArr64<short>(); Span<short> buffer2 = buffer2SA;
        for (int idx = 0; idx < 8; ++idx) Col8(srcDiff.Slice(idx), srcStride, buffer.Slice(8 * idx));
        for (int idx = 0; idx < 8; ++idx) Col8(buffer.Slice(idx), 8, buffer2.Slice(8 * idx));
        // extra transpose to match SSE2
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++) coeff[i * 8 + j] = buffer2[j * 8 + i];
    }

    /// <summary>aom_hadamard_16x16 (in the AVX2 kernel's output order).</summary>
    internal static void H16x16(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        if (Avx2.IsSupported && coeff.Length >= 256 && srcDiff.Length >= 15 * srcStride + 16)
        {
            H16x16Avx2(ref MemoryMarshal.GetReference(srcDiff), srcStride, ref MemoryMarshal.GetReference(coeff));
            return;
        }
        for (int idx = 0; idx < 4; ++idx)
            H8x8(srcDiff.Slice((idx >> 1) * 8 * srcStride + (idx & 1) * 8), srcStride, coeff.Slice(idx * 64));
        if (Avx2.IsSupported && coeff.Length >= 256)
        {
            ref int c0 = ref MemoryMarshal.GetReference(coeff);
            for (int idx = 0; idx < 64; idx += 8)
            {
                var a0 = Vector256.LoadUnsafe(ref c0, (nuint)idx); var a1 = Vector256.LoadUnsafe(ref c0, (nuint)(idx + 64));
                var a2 = Vector256.LoadUnsafe(ref c0, (nuint)(idx + 128)); var a3 = Vector256.LoadUnsafe(ref c0, (nuint)(idx + 192));
                var b0 = Vector256.ShiftRightArithmetic(a0 + a1, 1); var b1 = Vector256.ShiftRightArithmetic(a0 - a1, 1);
                var b2 = Vector256.ShiftRightArithmetic(a2 + a3, 1); var b3 = Vector256.ShiftRightArithmetic(a2 - a3, 1);
                (b0 + b2).StoreUnsafe(ref c0, (nuint)idx); (b1 + b3).StoreUnsafe(ref c0, (nuint)(idx + 64));
                (b0 - b2).StoreUnsafe(ref c0, (nuint)(idx + 128)); (b1 - b3).StoreUnsafe(ref c0, (nuint)(idx + 192));
            }
            // extra shift to match AVX2 output: per 16, the middle two quads swapped
            for (int i = 0; i < 16; i++)
            {
                var m = Vector256.LoadUnsafe(ref c0, (nuint)(i * 16 + 4));
                Avx2.Permute2x128(m, m, 0x01).StoreUnsafe(ref c0, (nuint)(i * 16 + 4));
            }
            return;
        }
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

    /// <summary>hadamard_16x16_avx2 (is_final): two aom_hadamard_lp_8x8_dual_avx2 into an int16 temp, the combining
    /// butterfly in int16 lanes, store_tran_low's lane order.</summary>
    [SkipLocalsInit]
    private static void H16x16Avx2(ref short src, int stride, ref int coeff)
    {
        Unsafe.SkipInit(out StackArr16<Vector256<short>> tBuf);
        ref Vector256<short> t = ref tBuf[0];
        Lp8x8DualAvx2(ref src, stride, ref t);
        Lp8x8DualAvx2(ref Unsafe.Add(ref src, 8 * stride), stride, ref Unsafe.Add(ref t, 8));
        ref short t16 = ref Unsafe.As<Vector256<short>, short>(ref t);
        var one = Vector256.Create((short)1);
        for (int idx = 0; idx < 64; idx += 16)
        {
            var c0 = Vector256.LoadUnsafe(ref t16, (nuint)idx);
            var c1 = Vector256.LoadUnsafe(ref t16, (nuint)(idx + 64));
            var c2 = Vector256.LoadUnsafe(ref t16, (nuint)(idx + 128));
            var c3 = Vector256.LoadUnsafe(ref t16, (nuint)(idx + 192));
            var b0 = Vector256.ShiftRightArithmetic(c0 + c1, 1);
            var b1 = Vector256.ShiftRightArithmetic(c0 - c1, 1);
            var b2 = Vector256.ShiftRightArithmetic(c2 + c3, 1);
            var b3 = Vector256.ShiftRightArithmetic(c2 - c3, 1);
            StoreTranLow(b0 + b2, ref Unsafe.Add(ref coeff, idx));
            StoreTranLow(b1 + b3, ref Unsafe.Add(ref coeff, idx + 64));
            StoreTranLow(b0 - b2, ref Unsafe.Add(ref coeff, idx + 128));
            StoreTranLow(b1 - b3, ref Unsafe.Add(ref coeff, idx + 192));
        }
    }

    // store_tran_low: the 16 int16 lanes sign-extended as [0..3, 8..11], [4..7, 12..15]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreTranLow(Vector256<short> a, ref int dst)
    {
        var sign = Vector256.ShiftRightArithmetic(a, 15);
        Avx2.UnpackLow(a, sign).AsInt32().StoreUnsafe(ref dst);
        Avx2.UnpackHigh(a, sign).AsInt32().StoreUnsafe(ref dst, 8);
    }

    /// <summary>aom_hadamard_lp_8x8_dual_avx2: two 8x8 blocks side by side (16 columns), output as 8 vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Lp8x8DualAvx2(ref short src, int stride, ref Vector256<short> outp)
    {
        var a0 = Vector256.LoadUnsafe(ref src);
        var a1 = Vector256.LoadUnsafe(ref src, (nuint)stride);
        var a2 = Vector256.LoadUnsafe(ref src, (nuint)(2 * stride));
        var a3 = Vector256.LoadUnsafe(ref src, (nuint)(3 * stride));
        var a4 = Vector256.LoadUnsafe(ref src, (nuint)(4 * stride));
        var a5 = Vector256.LoadUnsafe(ref src, (nuint)(5 * stride));
        var a6 = Vector256.LoadUnsafe(ref src, (nuint)(6 * stride));
        var a7 = Vector256.LoadUnsafe(ref src, (nuint)(7 * stride));
        // hadamard_col8x2_avx2, iter 0 (with the transpose)
        var b0 = a0 + a1; var b1 = a0 - a1; var b2 = a2 + a3; var b3 = a2 - a3;
        var b4 = a4 + a5; var b5 = a4 - a5; var b6 = a6 + a7; var b7 = a6 - a7;
        a0 = b0 + b2; a1 = b1 + b3; a2 = b0 - b2; a3 = b1 - b3;
        a4 = b4 + b6; a5 = b5 + b7; a6 = b4 - b6; a7 = b5 - b7;
        b0 = a0 + a4; b7 = a1 + a5; b3 = a2 + a6; b4 = a3 + a7;
        b2 = a0 - a4; b6 = a1 - a5; b1 = a2 - a6; b5 = a3 - a7;
        a0 = Avx2.UnpackLow(b0, b1); a1 = Avx2.UnpackLow(b2, b3); a2 = Avx2.UnpackHigh(b0, b1); a3 = Avx2.UnpackHigh(b2, b3);
        a4 = Avx2.UnpackLow(b4, b5); a5 = Avx2.UnpackLow(b6, b7); a6 = Avx2.UnpackHigh(b4, b5); a7 = Avx2.UnpackHigh(b6, b7);
        var d0 = Avx2.UnpackLow(a0.AsInt32(), a1.AsInt32()); var d1 = Avx2.UnpackLow(a4.AsInt32(), a5.AsInt32());
        var d2 = Avx2.UnpackHigh(a0.AsInt32(), a1.AsInt32()); var d3 = Avx2.UnpackHigh(a4.AsInt32(), a5.AsInt32());
        var d4 = Avx2.UnpackLow(a2.AsInt32(), a3.AsInt32()); var d5 = Avx2.UnpackLow(a6.AsInt32(), a7.AsInt32());
        var d6 = Avx2.UnpackHigh(a2.AsInt32(), a3.AsInt32()); var d7 = Avx2.UnpackHigh(a6.AsInt32(), a7.AsInt32());
        a0 = Avx2.UnpackLow(d0.AsInt64(), d1.AsInt64()).AsInt16(); a1 = Avx2.UnpackHigh(d0.AsInt64(), d1.AsInt64()).AsInt16();
        a2 = Avx2.UnpackLow(d2.AsInt64(), d3.AsInt64()).AsInt16(); a3 = Avx2.UnpackHigh(d2.AsInt64(), d3.AsInt64()).AsInt16();
        a4 = Avx2.UnpackLow(d4.AsInt64(), d5.AsInt64()).AsInt16(); a5 = Avx2.UnpackHigh(d4.AsInt64(), d5.AsInt64()).AsInt16();
        a6 = Avx2.UnpackLow(d6.AsInt64(), d7.AsInt64()).AsInt16(); a7 = Avx2.UnpackHigh(d6.AsInt64(), d7.AsInt64()).AsInt16();
        // iter 1
        b0 = a0 + a1; b1 = a0 - a1; b2 = a2 + a3; b3 = a2 - a3;
        b4 = a4 + a5; b5 = a4 - a5; b6 = a6 + a7; b7 = a6 - a7;
        a0 = b0 + b2; a1 = b1 + b3; a2 = b0 - b2; a3 = b1 - b3;
        a4 = b4 + b6; a5 = b5 + b7; a6 = b4 - b6; a7 = b5 - b7;
        var i0 = a0 + a4; var i7 = a1 + a5; var i3 = a2 + a6; var i4 = a3 + a7;
        var i2 = a0 - a4; var i6 = a1 - a5; var i1 = a2 - a6; var i5 = a3 - a7;
        // the stores of aom_hadamard_lp_8x8_dual_avx2 (permute2x128 0x20 then 0x31 pairs)
        outp = Avx2.Permute2x128(i0, i1, 0x20);
        Unsafe.Add(ref outp, 1) = Avx2.Permute2x128(i2, i3, 0x20);
        Unsafe.Add(ref outp, 2) = Avx2.Permute2x128(i4, i5, 0x20);
        Unsafe.Add(ref outp, 3) = Avx2.Permute2x128(i6, i7, 0x20);
        Unsafe.Add(ref outp, 4) = Avx2.Permute2x128(i0, i1, 0x31);
        Unsafe.Add(ref outp, 5) = Avx2.Permute2x128(i2, i3, 0x31);
        Unsafe.Add(ref outp, 6) = Avx2.Permute2x128(i4, i5, 0x31);
        Unsafe.Add(ref outp, 7) = Avx2.Permute2x128(i6, i7, 0x31);
    }

    /// <summary>aom_hadamard_32x32.</summary>
    internal static void H32x32(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            H16x16(srcDiff.Slice((idx >> 1) * 16 * srcStride + (idx & 1) * 16), srcStride, coeff.Slice(idx * 256));
        if (Avx2.IsSupported && coeff.Length >= 1024)
        {
            ref int c0 = ref MemoryMarshal.GetReference(coeff);
            for (int idx = 0; idx < 256; idx += 8)
            {
                var a0 = Vector256.LoadUnsafe(ref c0, (nuint)idx); var a1 = Vector256.LoadUnsafe(ref c0, (nuint)(idx + 256));
                var a2 = Vector256.LoadUnsafe(ref c0, (nuint)(idx + 512)); var a3 = Vector256.LoadUnsafe(ref c0, (nuint)(idx + 768));
                var b0 = Vector256.ShiftRightArithmetic(a0 + a1, 2); var b1 = Vector256.ShiftRightArithmetic(a0 - a1, 2);
                var b2 = Vector256.ShiftRightArithmetic(a2 + a3, 2); var b3 = Vector256.ShiftRightArithmetic(a2 - a3, 2);
                (b0 + b2).StoreUnsafe(ref c0, (nuint)idx); (b1 + b3).StoreUnsafe(ref c0, (nuint)(idx + 256));
                (b0 - b2).StoreUnsafe(ref c0, (nuint)(idx + 512)); (b1 - b3).StoreUnsafe(ref c0, (nuint)(idx + 768));
            }
            return;
        }
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
