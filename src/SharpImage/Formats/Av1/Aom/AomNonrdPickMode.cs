using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 nonrd_pickmode.c / nonrd_opt.c / nonrd_opt.h for intra-only frames: av1_nonrd_pick_intra_mode
// (DC / V / H / SMOOTH estimated with the Hadamard / 4x4 low-precision transform RD model of av1_block_yrd, the
// neighbour / best-mode / SAD prunes, the luma palette search), av1_estimate_block_intra, and the low-precision kernels
// av1_block_yrd dispatches in libaom's SIMD build (aom_hadamard_lp_8x8_sse2 / _8x8_dual_avx2 / _16x16_avx2,
// aom_fdct4x4_lp_sse2, av1_quantize_lp_avx2, aom_satd_lp_avx2, av1_block_error_lp_avx2: the hadamards and the 4x4
// transform are exactly their C references; quantize / satd / error follow the AVX2 16-bit lane arithmetic).
internal static partial class AomNonrdPickMode
{
    private const int RTC_INTRA_MODES = 4;
    private static readonly int[] IntraModeList = { DC_PRED, V_PRED, H_PRED, SMOOTH_PRED };
    private static readonly byte[] BWidthLog2Lookup = { 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5 };
    private static readonly byte[] BHeightLog2Lookup = { 0, 1, 0, 1, 2, 1, 2, 3, 2, 3, 4, 3, 4, 5, 4, 5 };
    private const int AV1_PROB_COST_SHIFT = 9;

    // av1_default_iscan_8x8_transpose / av1_default_iscan_lp_16x16_transpose (nonrd_opt.h)
    private static readonly short[] Iscan8x8Transpose =
    {
        0, 2, 3, 9, 10, 20, 21, 35, 1, 4, 8, 11, 19, 22, 34, 36,
        5, 7, 12, 18, 23, 33, 37, 48, 6, 13, 17, 24, 32, 38, 47, 49,
        14, 16, 25, 31, 39, 46, 50, 57, 15, 26, 30, 40, 45, 51, 56, 58,
        27, 29, 41, 44, 52, 55, 59, 62, 28, 42, 43, 53, 54, 60, 61, 63,
    };
    private static readonly short[] IscanLp16x16Transpose =
    {
        0, 44, 2, 46, 3, 63, 9, 69, 1, 45, 4, 64, 8, 68, 11, 87,
        5, 65, 7, 67, 12, 88, 18, 94, 6, 66, 13, 89, 17, 93, 24, 116,
        14, 90, 16, 92, 25, 117, 31, 123, 15, 91, 26, 118, 30, 122, 41, 148,
        27, 119, 29, 121, 42, 149, 48, 152, 28, 120, 43, 150, 47, 151, 62, 177,
        10, 86, 20, 96, 21, 113, 35, 127, 19, 95, 22, 114, 34, 126, 37, 144,
        23, 115, 33, 125, 38, 145, 52, 156, 32, 124, 39, 146, 51, 155, 58, 173,
        40, 147, 50, 154, 59, 174, 73, 181, 49, 153, 60, 175, 72, 180, 83, 198,
        61, 176, 71, 179, 84, 199, 98, 202, 70, 178, 85, 200, 97, 201, 112, 219,
        36, 143, 54, 158, 55, 170, 77, 185, 53, 157, 56, 171, 76, 184, 79, 194,
        57, 172, 75, 183, 80, 195, 102, 206, 74, 182, 81, 196, 101, 205, 108, 215,
        82, 197, 100, 204, 109, 216, 131, 223, 99, 203, 110, 217, 130, 222, 140, 232,
        111, 218, 129, 221, 141, 233, 160, 236, 128, 220, 142, 234, 159, 235, 169, 245,
        78, 193, 104, 208, 105, 212, 135, 227, 103, 207, 106, 213, 134, 226, 136, 228,
        107, 214, 133, 225, 137, 229, 164, 240, 132, 224, 138, 230, 163, 239, 165, 241,
        139, 231, 162, 238, 166, 242, 189, 249, 161, 237, 167, 243, 188, 248, 190, 250,
        168, 244, 187, 247, 191, 251, 210, 254, 186, 246, 192, 252, 209, 253, 211, 255,
    };

    // ---- low-precision kernels

    /// <summary>hadamard_col8 (16-bit intermediates).</summary>
    private static void HadamardCol8(ReadOnlySpan<short> src, int stride, Span<short> coeff)
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

    /// <summary>aom_hadamard_lp_8x8 (the SSE2 kernel; the C reference transposes its output to match it).</summary>
    internal static void HadamardLp8x8Scalar(ReadOnlySpan<short> srcDiff, int srcStride, Span<short> coeff)
    {
        Span<short> buffer = stackalloc short[64];
        Span<short> buffer2 = stackalloc short[64];
        for (int idx = 0; idx < 8; ++idx) HadamardCol8(srcDiff.Slice(idx), srcStride, buffer.Slice(idx * 8, 8));
        for (int idx = 0; idx < 8; ++idx) HadamardCol8(buffer.Slice(idx), 8, buffer2.Slice(8 * idx, 8));
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++) coeff[i * 8 + j] = buffer2[j * 8 + i];
    }

    /// <summary>aom_hadamard_lp_16x16 (the AVX2 kernel equals the C reference).</summary>
    internal static void HadamardLp16x16Scalar(ReadOnlySpan<short> srcDiff, int srcStride, Span<short> coeff)
    {
        for (int idx = 0; idx < 4; ++idx)
            HadamardLp8x8Scalar(srcDiff.Slice((idx >> 1) * 8 * srcStride + (idx & 1) * 8), srcStride, coeff.Slice(idx * 64, 64));
        for (int idx = 0; idx < 64; ++idx)
        {
            short a0 = coeff[idx], a1 = coeff[idx + 64], a2 = coeff[idx + 128], a3 = coeff[idx + 192];
            short b0 = (short)((a0 + a1) >> 1), b1 = (short)((a0 - a1) >> 1), b2 = (short)((a2 + a3) >> 1), b3 = (short)((a2 - a3) >> 1);
            coeff[idx] = (short)(b0 + b2);
            coeff[idx + 64] = (short)(b1 + b3);
            coeff[idx + 128] = (short)(b0 - b2);
            coeff[idx + 192] = (short)(b1 - b3);
        }
    }

    private static int FdctRoundShift(int input) => (input + (1 << 13)) >> 14;

    /// <summary>aom_fdct4x4_lp (the SSE2 kernel equals the C reference).</summary>
    internal static void Fdct4x4Lp(ReadOnlySpan<short> input, int stride, Span<short> output)
    {
        const int cospi_16_64 = 11585, cospi_24_64 = 6270, cospi_8_64 = 15137;
        Span<short> intermediate = stackalloc short[16];
        Span<int> inHigh = stackalloc int[4];
        for (int pass = 0; pass < 2; ++pass)
        {
            for (int i = 0; i < 4; ++i)
            {
                if (pass == 0)
                {
                    inHigh[0] = input[0 * stride + i] * 16;
                    inHigh[1] = input[1 * stride + i] * 16;
                    inHigh[2] = input[2 * stride + i] * 16;
                    inHigh[3] = input[3 * stride + i] * 16;
                    if (i == 0 && inHigh[0] != 0) ++inHigh[0];
                }
                else
                {
                    inHigh[0] = intermediate[0 * 4 + i];
                    inHigh[1] = intermediate[1 * 4 + i];
                    inHigh[2] = intermediate[2 * 4 + i];
                    inHigh[3] = intermediate[3 * 4 + i];
                }
                int step0 = inHigh[0] + inHigh[3], step1 = inHigh[1] + inHigh[2];
                int step2 = inHigh[1] - inHigh[2], step3 = inHigh[0] - inHigh[3];
                short t0 = (short)FdctRoundShift((step0 + step1) * cospi_16_64);
                short t2 = (short)FdctRoundShift((step0 - step1) * cospi_16_64);
                short t1 = (short)FdctRoundShift(step2 * cospi_24_64 + step3 * cospi_8_64);
                short t3 = (short)FdctRoundShift(-step2 * cospi_8_64 + step3 * cospi_24_64);
                if (pass == 0)
                {
                    intermediate[i * 4 + 0] = t0;
                    intermediate[i * 4 + 1] = t1;
                    intermediate[i * 4 + 2] = t2;
                    intermediate[i * 4 + 3] = t3;
                }
                else
                {
                    output[0 * 4 + i] = t0;
                    output[1 * 4 + i] = t1;
                    output[2 * 4 + i] = t2;
                    output[3 * 4 + i] = t3;
                }
            }
        }
        for (int i = 0; i < 16; ++i) output[i] = (short)((output[i] + 1) >> 2);
    }

    private static short Abs16(short v) => v == short.MinValue ? short.MinValue : Math.Abs(v);
    private static short AddSat16(int a, int b) => (short)Math.Clamp(a + b, short.MinValue, short.MaxValue);

    /// <summary>av1_quantize_lp_avx2: 16-bit lanes (saturating add of the rounding, mulhi by the quantizer, sign of the
    /// input, 16-bit dequantize); eob = 1 + the largest iscan of a nonzero.</summary>
    internal static int QuantizeLpScalar(ReadOnlySpan<short> coeff, int nCoeffs, short round0, short round1, short quant0, short quant1,
        Span<short> qcoeff, Span<short> dqcoeff, short dequant0, short dequant1, ReadOnlySpan<short> iscan)
    {
        int eob = 0;
        for (int i = 0; i < nCoeffs; i++)
        {
            short c = coeff[i];
            short absC = Abs16(c);
            short tmpRnd = AddSat16(absC, i == 0 ? round0 : round1);
            short absQ = (short)((tmpRnd * (i == 0 ? quant0 : quant1)) >> 16);
            short q = c < 0 ? (short)-absQ : c == 0 ? (short)0 : absQ;
            qcoeff[i] = q;
            dqcoeff[i] = (short)(q * (i == 0 ? dequant0 : dequant1));
            if (absQ > 0) eob = Math.Max(eob, iscan[i] + 1);
        }
        return eob;
    }

    /// <summary>aom_satd_lp_avx2.</summary>
    internal static int SatdLpScalar(ReadOnlySpan<short> coeff, int length)
    {
        int satd = 0;
        for (int i = 0; i < length; i++) satd += Abs16(coeff[i]);
        return satd;
    }

    /// <summary>av1_block_error_lp_avx2: 16-bit differences, pairwise 32-bit products, the per-lane 32-bit sums read as
    /// unsigned and accumulated in 64 bits.</summary>
    internal static long BlockErrorLpScalar(ReadOnlySpan<short> coeff, ReadOnlySpan<short> dqcoeff, int blockSize)
    {
        Span<int> err = stackalloc int[8];
        if (blockSize == 16)
        {
            for (int k = 0; k < 8; k++)
            {
                int d0 = (short)(dqcoeff[2 * k] - coeff[2 * k]), d1 = (short)(dqcoeff[2 * k + 1] - coeff[2 * k + 1]);
                err[k] = unchecked(d0 * d0 + d1 * d1);
            }
            // hadd within each 128-bit lane, low half unpacked to 64 bits
            return (long)(uint)unchecked(err[0] + err[1]) + (uint)unchecked(err[2] + err[3]) + (uint)unchecked(err[4] + err[5]) +
                   (uint)unchecked(err[6] + err[7]);
        }
        long sse = 0;
        for (int i = 0; i < blockSize; i += 32)
        {
            for (int k = 0; k < 8; k++)
            {
                int d0 = (short)(dqcoeff[i + 2 * k] - coeff[i + 2 * k]), d1 = (short)(dqcoeff[i + 2 * k + 1] - coeff[i + 2 * k + 1]);
                int e0 = unchecked(d0 * d0 + d1 * d1);
                int d2 = (short)(dqcoeff[i + 16 + 2 * k] - coeff[i + 16 + 2 * k]), d3 = (short)(dqcoeff[i + 16 + 2 * k + 1] - coeff[i + 16 + 2 * k + 1]);
                int e1 = unchecked(d2 * d2 + d3 * d3);
                sse += (uint)unchecked(e0 + e1);
            }
        }
        return sse;
    }


    // ---- vector kernels (the dispatched SIMD arithmetic; the *Scalar versions above are their references)

    /// <summary>aom_hadamard_lp_8x8.</summary>
    internal static void HadamardLp8x8(ReadOnlySpan<short> srcDiff, int srcStride, Span<short> coeff)
    {
        if (Sse2.IsSupported) AomHadamard.H8x8Lp(srcDiff, srcStride, coeff);
        else HadamardLp8x8Scalar(srcDiff, srcStride, coeff);
    }

    /// <summary>aom_hadamard_lp_16x16_avx2.</summary>
    internal static void HadamardLp16x16(ReadOnlySpan<short> srcDiff, int srcStride, Span<short> coeff)
    {
        if (!Avx2.IsSupported) { HadamardLp16x16Scalar(srcDiff, srcStride, coeff); return; }
        for (int idx = 0; idx < 4; ++idx)
            AomHadamard.H8x8Lp(srcDiff.Slice((idx >> 1) * 8 * srcStride + (idx & 1) * 8), srcStride, coeff.Slice(idx * 64, 64));
        ref short c = ref MemoryMarshal.GetReference(coeff);
        for (int idx = 0; idx < 64; idx += 16)
        {
            var c0 = Vector256.LoadUnsafe(ref c, (nuint)idx);
            var c1 = Vector256.LoadUnsafe(ref c, (nuint)(idx + 64));
            var c2 = Vector256.LoadUnsafe(ref c, (nuint)(idx + 128));
            var c3 = Vector256.LoadUnsafe(ref c, (nuint)(idx + 192));
            var b0 = Avx2.ShiftRightArithmetic(c0 + c1, 1);
            var b1 = Avx2.ShiftRightArithmetic(c0 - c1, 1);
            var b2 = Avx2.ShiftRightArithmetic(c2 + c3, 1);
            var b3 = Avx2.ShiftRightArithmetic(c2 - c3, 1);
            (b0 + b2).StoreUnsafe(ref c, (nuint)idx);
            (b1 + b3).StoreUnsafe(ref c, (nuint)(idx + 64));
            (b0 - b2).StoreUnsafe(ref c, (nuint)(idx + 128));
            (b1 - b3).StoreUnsafe(ref c, (nuint)(idx + 192));
        }
    }

    /// <summary>av1_quantize_lp_avx2.</summary>
    internal static int QuantizeLp(ReadOnlySpan<short> coeff, int nCoeffs, short round0, short round1, short quant0, short quant1,
        Span<short> qcoeff, Span<short> dqcoeff, short dequant0, short dequant1, ReadOnlySpan<short> iscan)
    {
        if (!Avx2.IsSupported)
            return QuantizeLpScalar(coeff, nCoeffs, round0, round1, quant0, quant1, qcoeff, dqcoeff, dequant0, dequant1, iscan);
        ref short cp = ref MemoryMarshal.GetReference(coeff);
        ref short qp = ref MemoryMarshal.GetReference(qcoeff);
        ref short dp = ref MemoryMarshal.GetReference(dqcoeff);
        ref short ip = ref MemoryMarshal.GetReference(iscan);
        var round = Vector256.Create(round1).WithElement(0, round0);
        var quant = Vector256.Create(quant1).WithElement(0, quant0);
        var dequant = Vector256.Create(dequant1).WithElement(0, dequant0);
        var eob = Vector256<short>.Zero;
        for (int i = 0; i < nCoeffs; i += 16)
        {
            if (i == 16)
            {
                round = Vector256.Create(round1);
                quant = Vector256.Create(quant1);
                dequant = Vector256.Create(dequant1);
            }
            var c = Vector256.LoadUnsafe(ref cp, (nuint)i);
            var absC = Avx2.Abs(c).AsInt16();
            var tmpRnd = Avx2.AddSaturate(absC, round);
            var absQ = Avx2.MultiplyHigh(tmpRnd, quant);
            var q = Avx2.Sign(absQ, c);
            var dq = Avx2.MultiplyLow(q, dequant);
            var nz = Avx2.CompareGreaterThan(absQ, Vector256<short>.Zero);
            q.StoreUnsafe(ref qp, (nuint)i);
            dq.StoreUnsafe(ref dp, (nuint)i);
            var isc = Vector256.LoadUnsafe(ref ip, (nuint)i);
            eob = Avx2.Max(eob, Avx2.And(isc - nz, nz));
        }
        var e = Sse2.Max(eob.GetLower(), eob.GetUpper());
        return Math.Max(Math.Max(Math.Max(e.GetElement(0), e.GetElement(1)), Math.Max(e.GetElement(2), e.GetElement(3))),
            Math.Max(Math.Max(e.GetElement(4), e.GetElement(5)), Math.Max(e.GetElement(6), e.GetElement(7))));
    }

    /// <summary>aom_satd_lp_avx2.</summary>
    internal static int SatdLp(ReadOnlySpan<short> coeff, int length)
    {
        if (!Avx2.IsSupported) return SatdLpScalar(coeff, length);
        ref short cp = ref MemoryMarshal.GetReference(coeff);
        var one = Vector256.Create((short)1);
        var acc = Vector256<int>.Zero;
        for (int i = 0; i < length; i += 16)
            acc += Avx2.MultiplyAddAdjacent(Avx2.Abs(Vector256.LoadUnsafe(ref cp, (nuint)i)).AsInt16(), one);
        return Vector256.Sum(acc);
    }

    /// <summary>av1_block_error_lp_avx2.</summary>
    internal static long BlockErrorLp(ReadOnlySpan<short> coeff, ReadOnlySpan<short> dqcoeff, int blockSize)
    {
        if (!Avx2.IsSupported || blockSize == 16) return BlockErrorLpScalar(coeff, dqcoeff, blockSize);
        ref short cp = ref MemoryMarshal.GetReference(coeff);
        ref short dp = ref MemoryMarshal.GetReference(dqcoeff);
        var sse = Vector256<long>.Zero;
        for (int i = 0; i < blockSize; i += 32)
        {
            var d0 = Vector256.LoadUnsafe(ref dp, (nuint)i) - Vector256.LoadUnsafe(ref cp, (nuint)i);
            var d1 = Vector256.LoadUnsafe(ref dp, (nuint)(i + 16)) - Vector256.LoadUnsafe(ref cp, (nuint)(i + 16));
            var e = Avx2.MultiplyAddAdjacent(d0, d0) + Avx2.MultiplyAddAdjacent(d1, d1);
            sse += Avx2.UnpackLow(e, Vector256<int>.Zero).AsInt64() + Avx2.UnpackHigh(e, Vector256<int>.Zero).AsInt64();
        }
        return Vector256.Sum(sse);
    }

    // ---- av1_block_yrd

    [ThreadStatic] private static short[]? t_lowCoeff, t_lowQcoeff, t_lowDqcoeff;

    /// <summary>av1_block_yrd (8-bit): the Hadamard (8x8 / 16x16) or 4x4 DCT low-precision RD estimate of the luma
    /// residual of a bsize block (prediction already in dst).</summary>
    internal static void BlockYrd(AomMacroblock x, ref AomRdStats thisRdc, ref int skippable, int bsize, int txSize)
    {
        var xd = x.E;
        var pd = xd.Plane[0];
        var p = x.Plane[0];
        int num4x4W = MiSizeWide[bsize], num4x4H = MiSizeHigh[bsize];
        int step = 1 << (txSize << 1);
        int blockStep = 1 << txSize;
        int maxBlocksWide = num4x4W + (xd.MbToRightEdge >= 0 ? 0 : xd.MbToRightEdge >> 5);
        int maxBlocksHigh = num4x4H + (xd.MbToBottomEdge >= 0 ? 0 : xd.MbToBottomEdge >> 5);
        int eobCost = 0;
        int bw = 4 * num4x4W, bh = 4 * num4x4H;

        if (p.Src.Buf16 != null)
        {
            BlockYrdHbd(x, ref thisRdc, ref skippable, bsize, txSize);
            return;
        }
        AomEncodeMb.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, p.Src.Buf, p.Src.Offset, p.Src.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride);

        int tempSkippable = 1;
        thisRdc.Dist = 0;
        thisRdc.Rate = 0;
        // (is_tx_8x8_dual_applicable: aom_hadamard_lp_8x8_dual computes the same two 8x8 transforms)

        var lowCoeff = t_lowCoeff ??= new short[256];
        var lowQcoeff = t_lowQcoeff ??= new short[256];
        var lowDqcoeff = t_lowDqcoeff ??= new short[256];
        int diffStride = bw;
        for (int r = 0; r < maxBlocksHigh; r += blockStep)
        {
            for (int c = 0; c < maxBlocksWide; c += blockStep)
            {
                var srcDiff = p.SrcDiff.AsSpan((r * diffStride + c) << 2);
                int eob;
                switch (txSize)
                {
                    case TX_16X16:
                        HadamardLp16x16(srcDiff, diffStride, lowCoeff);
                        eob = QuantizeLp(lowCoeff, 256, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1, lowQcoeff, lowDqcoeff, p.Dequant0,
                            p.Dequant1, IscanLp16x16Transpose);
                        break;
                    case TX_8X8:
                        HadamardLp8x8(srcDiff, diffStride, lowCoeff);
                        eob = QuantizeLp(lowCoeff, 64, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1, lowQcoeff, lowDqcoeff, p.Dequant0,
                            p.Dequant1, Iscan8x8Transpose);
                        break;
                    default:
                        Fdct4x4Lp(srcDiff, diffStride, lowCoeff);
                        eob = QuantizeLp(lowCoeff, 16, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1, lowQcoeff, lowDqcoeff, p.Dequant0,
                            p.Dequant1, AomEncodeMb.IScanOf(TX_4X4, DCT_DCT));
                        break;
                }
                // update_yrd_loop_vars (blk_skip is not read by the intra paths)
                int ncoeffs = eob;
                tempSkippable &= ncoeffs == 0 ? 1 : 0;
                eobCost += System.Numerics.BitOperations.Log2((uint)(ncoeffs + 1));
                if (ncoeffs == 1) thisRdc.Rate += Math.Abs((int)lowQcoeff[0]);
                else if (ncoeffs > 1) thisRdc.Rate += SatdLp(lowQcoeff, step << 4);
                thisRdc.Dist += BlockErrorLp(lowCoeff, lowDqcoeff, step << 4) >> 2;
            }
        }

        thisRdc.SkipTxfm = (byte)tempSkippable;
        skippable = tempSkippable;
        if (thisRdc.Sse < long.MaxValue)
        {
            thisRdc.Sse = (thisRdc.Sse << 6) >> 2;
            if (tempSkippable != 0)
            {
                thisRdc.Dist = thisRdc.Sse;
                return;
            }
        }
        // If skippable is set, rate gets clobbered later.
        thisRdc.Rate <<= 2 + AV1_PROB_COST_SHIFT;
        thisRdc.Rate += eobCost << AV1_PROB_COST_SHIFT;
    }

    // av1_default_iscan_fp_16x16_transpose (nonrd_opt.h)
    private static readonly short[] IscanFp16x16Transpose =
    {
        0, 44, 2, 46, 1, 45, 4, 64, 3, 63, 9, 69, 8, 68, 11, 87, 5, 65, 7, 67, 6, 66, 13, 89, 12, 88, 18, 94, 17, 93, 24, 116,
        14, 90, 16, 92, 15, 91, 26, 118, 25, 117, 31, 123, 30, 122, 41, 148, 27, 119, 29, 121, 28, 120, 43, 150, 42, 149, 48, 152,
        47, 151, 62, 177, 10, 86, 20, 96, 19, 95, 22, 114, 21, 113, 35, 127, 34, 126, 37, 144, 23, 115, 33, 125, 32, 124, 39, 146,
        38, 145, 52, 156, 51, 155, 58, 173, 40, 147, 50, 154, 49, 153, 60, 175, 59, 174, 73, 181, 72, 180, 83, 198, 61, 176, 71, 179,
        70, 178, 85, 200, 84, 199, 98, 202, 97, 201, 112, 219, 36, 143, 54, 158, 53, 157, 56, 171, 55, 170, 77, 185, 76, 184, 79, 194,
        57, 172, 75, 183, 74, 182, 81, 196, 80, 195, 102, 206, 101, 205, 108, 215, 82, 197, 100, 204, 99, 203, 110, 217, 109, 216,
        131, 223, 130, 222, 140, 232, 111, 218, 129, 221, 128, 220, 142, 234, 141, 233, 160, 236, 159, 235, 169, 245, 78, 193, 104,
        208, 103, 207, 106, 213, 105, 212, 135, 227, 134, 226, 136, 228, 107, 214, 133, 225, 132, 224, 138, 230, 137, 229, 164, 240,
        163, 239, 165, 241, 139, 231, 162, 238, 161, 237, 167, 243, 166, 242, 189, 249, 188, 248, 190, 250, 168, 244, 187, 247, 186,
        246, 192, 252, 191, 251, 210, 254, 209, 253, 211, 255,
    };

    [ThreadStatic] private static int[]? t_hCoeff, t_hQcoeff, t_hDqcoeff;

    /// <summary>av1_block_yrd's high bit depth path: aom_highbd_subtract_block, the lowbd aom_hadamard_16x16 / 8x8 and
    /// aom_fdct4x4 kernels into tran_low_t, the lowbd av1_quantize_fp (the transposed fp iscans for the Hadamards),
    /// update_yrd_loop_vars_hbd (aom_satd, av1_highbd_block_error).</summary>
    private static void BlockYrdHbd(AomMacroblock x, ref AomRdStats thisRdc, ref int skippable, int bsize, int txSize)
    {
        var xd = x.E;
        var pd = xd.Plane[0];
        var p = x.Plane[0];
        int num4x4W = MiSizeWide[bsize], num4x4H = MiSizeHigh[bsize];
        int step = 1 << (txSize << 1);
        int blockStep = 1 << txSize;
        int maxBlocksWide = num4x4W + (xd.MbToRightEdge >= 0 ? 0 : xd.MbToRightEdge >> 5);
        int maxBlocksHigh = num4x4H + (xd.MbToBottomEdge >= 0 ? 0 : xd.MbToBottomEdge >> 5);
        int eobCost = 0;
        int bw = 4 * num4x4W, bh = 4 * num4x4H;
        AomHbd.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, p.Src.Buf16, p.Src.Offset, p.Src.Stride, pd.Dst.Buf16, pd.Dst.Offset, pd.Dst.Stride);
        int tempSkippable = 1;
        thisRdc.Dist = 0;
        thisRdc.Rate = 0;
        var coeff = t_hCoeff ??= new int[256];
        var qcoeff = t_hQcoeff ??= new int[256];
        var dqcoeff = t_hDqcoeff ??= new int[256];
        int diffStride = bw;
        int n = step << 4;
        for (int r = 0; r < maxBlocksHigh; r += blockStep)
            for (int c = 0; c < maxBlocksWide; c += blockStep)
            {
                var srcDiff = p.SrcDiff.AsSpan((r * diffStride + c) << 2);
                short[] iscan;
                switch (txSize)
                {
                    case TX_16X16: AomHbd.Hadamard16x16Lbd(srcDiff, diffStride, coeff); iscan = IscanFp16x16Transpose; break;
                    case TX_8X8: AomHadamard.H8x8(srcDiff, diffStride, coeff); iscan = Iscan8x8Transpose; break;
                    default: AomHbd.Fdct4x4Sse2(srcDiff, diffStride, coeff); iscan = AomEncodeMb.IScanOf(TX_4X4, DCT_DCT); break;
                }
                int eob = AomQuantize.QuantizeFpAvx2(coeff, n, iscan, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1, p.Dequant0, p.Dequant1,
                    0, qcoeff, dqcoeff);
                tempSkippable &= eob == 0 ? 1 : 0;
                eobCost += System.Numerics.BitOperations.Log2((uint)(eob + 1));
                if (eob == 1) thisRdc.Rate += Math.Abs(qcoeff[0]);
                else if (eob > 1) thisRdc.Rate += AomEncodeMb.Satd(qcoeff, n);
                thisRdc.Dist += AomHbd.BlockError(coeff, dqcoeff, n, out _, xd.Bd) >> 2;
            }
        thisRdc.SkipTxfm = (byte)tempSkippable;
        skippable = tempSkippable;
        if (thisRdc.Sse < long.MaxValue)
        {
            thisRdc.Sse = (thisRdc.Sse << 6) >> 2;
            if (tempSkippable != 0)
            {
                thisRdc.Dist = thisRdc.Sse;
                return;
            }
        }
        thisRdc.Rate <<= 2 + AV1_PROB_COST_SHIFT;
        thisRdc.Rate += eobCost << AV1_PROB_COST_SHIFT;
    }

    // ---- av1_nonrd_pick_intra_mode

    private sealed class EstimateBlockIntraArgs
    {
        public int Mode;
        public int Skippable;
        public uint BestSad;
        public bool PruneModeBasedOnSad;
    }

    /// <summary>av1_estimate_block_intra (luma, the only plane the intra-frame path visits).</summary>
    private static void EstimateBlockIntra(AomComp cpi, AomMacroblock x, EstimateBlockIntraArgs args, ref AomRdStats rdc, int plane,
        int blkRow, int blkCol, int planeBsize, int txSize)
    {
        var xd = x.E;
        var p = x.Plane[plane];
        var pd = xd.Plane[plane];
        int bsizeTx = TxsizeToBsize[txSize];
        var srcBase = p.Src;
        var dstBase = pd.Dst;

        AomReconIntra.PredictIntraBlockFacade(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, plane, blkCol, blkRow, txSize);

        if (args.PruneModeBasedOnSad)
        {
            uint thisSad = p.Src.Buf16 != null
                ? AomHbd.Sad(p.Src.Buf16, p.Src.Offset, p.Src.Stride, pd.Dst.Buf16, pd.Dst.Offset, pd.Dst.Stride,
                    BlockSizeWide[planeBsize], BlockSizeHigh[planeBsize]) >> (xd.Bd - 8)   // fn_ptr sdf: the _bits10 / _bits12 wrappers
                : AomSad.Sad(p.Src.Buf, p.Src.Offset, p.Src.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride,
                BlockSizeWide[planeBsize], BlockSizeHigh[planeBsize]);
            uint sadThreshold = args.BestSad != uint.MaxValue ? args.BestSad + (args.BestSad >> 4) : uint.MaxValue;
            // Skip the evaluation of the current mode if its SAD is more than a threshold.
            if (thisSad > sadThreshold)
            {
                rdc.Rate = int.MaxValue;
                rdc.Dist = long.MaxValue;
                AomTrace.Out?.Write($"ebi {xd.MiRow} {xd.MiCol} p{plane} {blkRow} {blkCol} mode {args.Mode} tx {txSize} sad {args.BestSad} rate {rdc.Rate} dist {rdc.Dist} skip {args.Skippable}\n");
                return;
            }
            if (thisSad < args.BestSad) args.BestSad = thisSad;
        }

        AomRdStats thisRdc = default;
        thisRdc.Invalidate();
        p.Src.Offset = srcBase.Offset + 4 * (blkRow * srcBase.Stride + blkCol);
        pd.Dst.Offset = dstBase.Offset + 4 * (blkRow * dstBase.Stride + blkCol);
        if (plane == 0) BlockYrd(x, ref thisRdc, ref args.Skippable, bsizeTx, Math.Min(txSize, TX_16X16));
        else ModelRdForSbUv(cpi, bsizeTx, x, xd, ref thisRdc, plane, plane);
        p.Src = srcBase;
        pd.Dst = dstBase;
        rdc.Rate += thisRdc.Rate;
        rdc.Dist += thisRdc.Dist;
        AomTrace.Out?.Write($"ebi {xd.MiRow} {xd.MiCol} p{plane} {blkRow} {blkCol} mode {args.Mode} tx {txSize} sad {args.BestSad} rate {rdc.Rate} dist {rdc.Dist} skip {args.Skippable}\n");
    }

    /// <summary>should_prune_intra_modes_using_neighbors.</summary>
    private static bool ShouldPruneIntraModesUsingNeighbors(AomMacroblockD xd, bool enable, int thisMode, int aboveMode, int leftMode)
    {
        if (!enable) return false;
        // Avoid pruning of DC_PRED as it is the most probable mode to win.
        if (thisMode == DC_PRED) return false;
        // Prune the current mode only if it is not the winner mode of both the neighbouring blocks (left / top).
        return xd.UpAvailable && thisMode != aboveMode && xd.LeftAvailable && thisMode != leftMode;
    }

    /// <summary>av1_nonrd_pick_intra_mode.</summary>
    internal static void NonrdPickIntraMode(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize, AomPickModeContext ctx)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        var args = new EstimateBlockIntraArgs { Mode = DC_PRED, Skippable = 1, BestSad = uint.MaxValue };
        var txfmParams = x.TxfmSearchParams;
        mi.TxSize = Math.Min(MaxTxsizeLookup[bsize], TxModeToBiggestTxSize[txfmParams.TxModeSearchType]);
        int txBsize = TxsizeToBsize[mi.TxSize];

        // If the current block size is the same as the transform block size, enable mode pruning based on the best SAD.
        if (cpi.Sf.rt_sf.prune_intra_mode_using_best_sad_so_far && bsize == txBsize) args.PruneModeBasedOnSad = true;

        int bestMode = DC_PRED;
        int a = xd.AboveMbmi?.Mode ?? DC_PRED;   // av1_above_block_mode
        int l = xd.LeftMbmi?.Mode ?? DC_PRED;    // av1_left_block_mode
        int aboveCtx = IntraModeContext[a], leftCtx = IntraModeContext[l];
        int bmodeCostsOff = (aboveCtx * 13 + leftCtx) * 13;
        uint sourceVariance = x.SourceVariance;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        // (flat_blocks_screen: REALTIME screen content only)
        AomRdStats bestRdc = default, thisRdc = default;
        bestRdc.Invalidate();
        thisRdc.Invalidate();

        // init_mbmi_nonrd(mi, DC_PRED, INTRA_FRAME, NONE_FRAME)
        mi.Mode = DC_PRED;
        mi.UvMode = UV_DC_PRED;
        mi.RefFrame0 = 0;
        mi.RefFrame1 = -1;
        mi.Palette.PaletteSize0 = 0;
        mi.Palette.PaletteSize1 = 0;
        mi.UseFilterIntra = 0;
        mi.MotionMode = 0;
        mi.InterpFilters = 0;
        mi.Mv0 = AomMv.Invalid;
        mi.Mv1 = AomMv.Invalid;

        bool allowSkipNondc = true;
        for (int modeIndex = 0; modeIndex < RTC_INTRA_MODES; ++modeIndex)
        {
            int thisMode = IntraModeList[modeIndex];

            // Force DC for spatially flat block for large bsize, on top-left corner.
            if (x.SourceVariance == 0 && miCol == 0 && miRow == 0 && bsize >= BLOCK_32X32 && thisMode > 0) continue;

            // H_PRED rarely wins when V_PRED is the best so far (out of DC_PRED and V_PRED).
            if (cpi.Sf.rt_sf.prune_h_pred_using_best_mode_so_far && thisMode == H_PRED && bestMode == V_PRED && allowSkipNondc) continue;

            if (ShouldPruneIntraModesUsingNeighbors(xd, cpi.Sf.rt_sf.enable_intra_mode_pruning_using_neighbors, thisMode, a, l))
            {
                // Prune V_PRED and H_PRED if source variance of the block is less than or equal to 50.
                if ((thisMode == V_PRED || thisMode == H_PRED) && sourceVariance <= 50 && allowSkipNondc) continue;
                // SMOOTH_PRED rarely wins when the best mode so far is DC_PRED.
                if (bestMode == DC_PRED && thisMode == SMOOTH_PRED && allowSkipNondc) continue;
            }

            thisRdc.Dist = 0;
            thisRdc.Rate = 0;
            args.Mode = thisMode;
            args.Skippable = 1;
            mi.Mode = thisMode;
            ForeachTxBlockY(cpi, x, args, ref thisRdc, bsize);

            if (thisRdc.Rate == int.MaxValue) continue;

            int skipCtx = AomTxSearch.SkipTxfmContext(xd);
            if (args.Skippable != 0) thisRdc.Rate = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
            else thisRdc.Rate += x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0];
            thisRdc.Rate += x.ModeCosts.YModeCosts[bmodeCostsOff + thisMode];
            thisRdc.Rdcost = AomRd.RdCost(x.Rdmult, thisRdc.Rate, thisRdc.Dist);
            if (thisRdc.Rdcost < bestRdc.Rdcost)
            {
                bestRdc = thisRdc;
                bestMode = thisMode;
            }
        }

        uint threshSad = cpi.Sf.rt_sf.prune_palette_search_nonrd > 1 ? 100u : 20u;
        uint bestSadNorm = args.BestSad >> (BWidthLog2Lookup[bsize] + BHeightLog2Lookup[bsize]);

        // Try palette if it's enabled.
        bool tryPalette = cpi.EnablePalette && AomIntraModeSearch.AllowPalette(cpi.AllowScreenContentTools, mi.Bsize);
        if (cpi.Sf.rt_sf.prune_palette_search_nonrd > 0)
        {
            bool prune = (!args.PruneModeBasedOnSad || bestSadNorm > threshSad) && bsize <= BLOCK_16X16 && x.SourceVariance > 200;
            tryPalette &= prune;
        }
        if (tryPalette)
        {
            const int intraRefFrameCost = 0;
            x.ColorPaletteThresh = bestSadNorm < 500 ? 32 : 64;
            // Search palette mode for Luma plane in intra frame.
            SearchPaletteModeLuma(cpi, x, bsize, intraRefFrameCost, ctx, ref thisRdc, bestRdc.Rdcost);
            // Update best mode data.
            if (thisRdc.Rdcost < bestRdc.Rdcost)
            {
                bestMode = DC_PRED;
                mi.Mv0 = AomMv.Invalid;
                mi.Mv1 = AomMv.Invalid;
                bestRdc.Rate = thisRdc.Rate;
                bestRdc.Dist = thisRdc.Dist;
                bestRdc.Rdcost = thisRdc.Rdcost;
                if (xd.TxTypeMap[xd.TxTypeMapOffset] != DCT_DCT)
                    xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(ctx.TxTypeMap);
            }
            else
            {
                mi.Palette.PaletteSize0 = 0;
                mi.Palette.PaletteSize1 = 0;
                Array.Clear(mi.Palette.PaletteColors);
            }
        }

        mi.Mode = bestMode;
        // Keep DC for UV since mode test is based on Y channel only.
        mi.UvMode = UV_DC_PRED;
        rdCost = bestRdc;

        // For lossless: always force the skip flags off.
        if (xd.Lossless[0] != 0) x.TxfmSkip = 0;   // is_lossless_requested

        // store_coding_context_nonrd
        ctx.RdStats.SkipTxfm = (byte)x.TxfmSkip;
        ctx.Skippable = x.TxfmSkip;
        ctx.Mic.CopyFrom(xd.Mi0);
        ctx.MbmiExtBest.CopyFrom(x.MbmiExt);
    }

    /// <summary>av1_foreach_transformed_block_in_plane(xd, bsize, AOM_PLANE_Y, av1_estimate_block_intra).</summary>
    private static void ForeachTxBlockY(AomComp cpi, AomMacroblock x, EstimateBlockIntraArgs args, ref AomRdStats rdc, int planeBsize)
    {
        var xd = x.E;
        int txSize = xd.Mi0.TxSize;
        int txBsize = TxsizeToBsize[txSize];
        if (planeBsize == txBsize)
        {
            EstimateBlockIntra(cpi, x, args, ref rdc, 0, 0, 0, planeBsize, txSize);
            return;
        }
        int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize];
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, 0), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, 0);
        int muBlocksWide = Math.Min(MiSizeWide[BLOCK_64X64], maxBlocksWide);
        int muBlocksHigh = Math.Min(MiSizeHigh[BLOCK_64X64], maxBlocksHigh);
        for (int r = 0; r < maxBlocksHigh; r += muBlocksHigh)
        {
            int unitHeight = Math.Min(muBlocksHigh + r, maxBlocksHigh);
            for (int c = 0; c < maxBlocksWide; c += muBlocksWide)
            {
                int unitWidth = Math.Min(muBlocksWide + c, maxBlocksWide);
                for (int blkRow = r; blkRow < unitHeight; blkRow += txhUnit)
                    for (int blkCol = c; blkCol < unitWidth; blkCol += txwUnit)
                        EstimateBlockIntra(cpi, x, args, ref rdc, 0, blkRow, blkCol, planeBsize, txSize);
            }
        }
    }

    /// <summary>av1_search_palette_mode_luma (intra_mode_search.c).</summary>
    private static void SearchPaletteModeLuma(AomComp cpi, AomMacroblock x, int bsize, int refFrameCost, AomPickModeContext ctx,
        ref AomRdStats thisRdCost, long bestRd)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        long bestRdPalette = bestRd;
        byte[] bestPaletteColorMap = x.BestPaletteColorMap;
        byte[] colorMap = xd.Plane[0].ColorIndexMap;
        var bestMbmiPalette = mbmi.Clone();
        var bestTxTypeMap = new byte[32 * 32];
        int rows = BlockSizeHigh[bsize], cols = BlockSizeWide[bsize];

        mbmi.Mode = DC_PRED;
        mbmi.UvMode = UV_DC_PRED;
        mbmi.RefFrame0 = 0;
        mbmi.RefFrame1 = -1;
        mbmi.Palette.PaletteSize0 = 0;
        mbmi.Palette.PaletteSize1 = 0;

        AomRdStats rdStatsY = default;
        rdStatsY.Invalidate();
        int unusedRateTokenonly = 0;
        bool unusedBeat = false;
        AomPalette.RdPickPaletteIntraSby(cpi, x, bsize, x.ModeCosts.MbmodeCost[SizeGroupLookup[bsize] * 13 + DC_PRED], bestMbmiPalette,
            bestPaletteColorMap, ref bestRdPalette, ref rdStatsY.Rate, ref unusedRateTokenonly, ref rdStatsY.Dist, ref rdStatsY.SkipTxfm,
            ref unusedBeat, ctx, bestTxTypeMap);
        if (rdStatsY.Rate == int.MaxValue || mbmi.Palette.PaletteSize0 == 0)
        {
            thisRdCost.Rdcost = long.MaxValue;
            return;
        }

        bestTxTypeMap.AsSpan(0, ctx.NumFourByFourBlk).CopyTo(xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset));
        Array.Copy(bestPaletteColorMap, colorMap, rows * cols);

        rdStatsY.Rate += refFrameCost;
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        if (rdStatsY.SkipTxfm != 0) rdStatsY.Rate = refFrameCost + x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
        else rdStatsY.Rate += x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0];
        long thisRd = AomRd.RdCost(x.Rdmult, rdStatsY.Rate, rdStatsY.Dist);
        thisRdCost.Rate = rdStatsY.Rate;
        thisRdCost.Dist = rdStatsY.Dist;
        thisRdCost.Rdcost = thisRd;
        thisRdCost.SkipTxfm = rdStatsY.SkipTxfm;
    }
}
