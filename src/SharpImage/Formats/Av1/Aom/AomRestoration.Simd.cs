using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

/// <summary>Line-by-line ports of the restoration kernels libaom 3.14.1 dispatches on an AVX2 machine:
/// av1_wiener_convolve_add_src_avx2 (av1/common/x86/wiener_convolve_avx2.c), av1_selfguided_restoration_avx2 and
/// av1_apply_selfguided_restoration_avx2 (av1/common/x86/selfguided_avx2.c). Twin-verified against the dispatched
/// functions.</summary>
internal static partial class AomRestoration
{
    /// <summary>av1_wiener_convolve_add_src (the RTCD choice): the AVX2 kernel when available (w a multiple of 8).</summary>
    public static void WienerConvolveAddSrc(byte[] src, int s0, int srcStride, byte[] dst, int d0, int dstStride,
        in AomTaps8 hf, in AomTaps8 vf, int w, int h, ushort[] temp)
    {
        if (Avx2.IsSupported && (w & 7) == 0) WienerConvolveAddSrcAvx2(src, s0, srcStride, dst, d0, dstStride, hf, vf, w, h);
        else WienerConvolveAddSrcC(src, s0, srcStride, dst, d0, dstStride, hf, vf, w, h, temp);
    }

    /// <summary>av1_selfguided_restoration (the RTCD choice).</summary>
    public static void SelfguidedRestoration(byte[] dgd8, int d0, int width, int height, int dgdStride, int[] flt0,
        int f0, int[] flt1, int f1, int fltStride, int sgrParamsIdx, SgrScratch sc)
    {
        if (Avx2.IsSupported) SelfguidedRestorationAvx2(dgd8, d0, width, height, dgdStride, flt0, f0, flt1, f1, fltStride, sgrParamsIdx, sc);
        else SelfguidedRestorationC(dgd8, d0, width, height, dgdStride, flt0, f0, flt1, f1, fltStride, sgrParamsIdx, sc);
    }

    /// <summary>av1_apply_selfguided_restoration (the RTCD choice).</summary>
    public static void ApplySelfguided(byte[] dat, int d0, int width, int height, int stride, int ep, int xqd0, int xqd1,
        byte[] dst, int dst0, int dstStride, int[] flt0, int[] flt1, SgrScratch sc)
    {
        if (Avx2.IsSupported) ApplySelfguidedAvx2(dat, d0, width, height, stride, ep, xqd0, xqd1, dst, dst0, dstStride, flt0, flt1, sc);
        else ApplySelfguidedC(dat, d0, width, height, stride, ep, xqd0, xqd1, dst, dst0, dstStride, flt0, flt1, sc);
    }

    // ---- wiener_convolve_avx2.c ---------------------------------------------------------------------------------------

    private static readonly byte[] Filt1Global = { 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8 };
    private static readonly byte[] Filt2Global = { 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10 };
    private static readonly byte[] Filt3Global = { 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12 };
    private static readonly byte[] Filt4Global = { 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14 };
    private static readonly byte[] FiltCenterGlobal = { 3, 255, 4, 255, 5, 255, 6, 255, 7, 255, 8, 255, 9, 255, 10, 255, 3, 255, 4, 255, 5, 255, 6, 255, 7, 255, 8, 255, 9, 255, 10, 255 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Ld256(byte[] a) => Unsafe.ReadUnaligned<Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(a));

    /// <summary>convolve (convolve_avx2.h): four madd_epi16 summed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Convolve(Vector256<short> s0, Vector256<short> s1, Vector256<short> s2, Vector256<short> s3,
        Vector256<short> c0, Vector256<short> c1, Vector256<short> c2, Vector256<short> c3)
    {
        var res0 = Avx2.MultiplyAddAdjacent(s0, c0);
        var res1 = Avx2.MultiplyAddAdjacent(s1, c1);
        var res2 = Avx2.MultiplyAddAdjacent(s2, c2);
        var res3 = Avx2.MultiplyAddAdjacent(s3, c3);
        return Avx2.Add(Avx2.Add(res0, res1), Avx2.Add(res2, res3));
    }

    /// <summary>av1_wiener_convolve_add_src_avx2 (8-bit, round_0 = 3, round_1 = 11).</summary>
    [SkipLocalsInit]
    public static unsafe void WienerConvolveAddSrcAvx2(byte[] src, int s0, int srcStride, byte[] dst, int d0, int dstStride,
        in AomTaps8 filterX, in AomTaps8 filterY, int w, int h)
    {
        const int bd = 8, filterBits = 7, round0 = 3, round1 = 11, subpelTaps = 8, maxSbSize = 128;
        short* imBlock = stackalloc short[(maxSbSize + subpelTaps) * 8 + 16];
        int imH = h + subpelTaps - 2;
        const int imStride = 8;
        new Span<short>(imBlock + imH * imStride, maxSbSize / 2).Clear();   // memset(..., 0, MAX_SB_SIZE) bytes
        const int centerTap = (subpelTaps - 1) / 2;

        var filt0 = Ld256(Filt1Global);
        var filt1 = Ld256(Filt2Global);
        var filt2 = Ld256(Filt3Global);
        var filt3 = Ld256(Filt4Global);
        var filtCenter = Ld256(FiltCenterGlobal);

        var coeffsX = Vector128.Create(filterX[0], filterX[1], filterX[2], filterX[3], filterX[4], filterX[5], filterX[6], filterX[7]);
        var filterCoeffsX = Vector256.Create(coeffsX, coeffsX).AsByte();
        // coeffs 0 1 0 1 0 1 0 1 / 2 3 / 4 5 / 6 7 (the low bytes)
        var coeffsH0 = Avx2.Shuffle(filterCoeffsX, Vector256.Create((ushort)0x0200).AsByte()).AsSByte();
        var coeffsH1 = Avx2.Shuffle(filterCoeffsX, Vector256.Create((ushort)0x0604).AsByte()).AsSByte();
        var coeffsH2 = Avx2.Shuffle(filterCoeffsX, Vector256.Create((ushort)0x0a08).AsByte()).AsSByte();
        var coeffsH3 = Avx2.Shuffle(filterCoeffsX, Vector256.Create((ushort)0x0e0c).AsByte()).AsSByte();

        var roundConstH = Vector256.Create((short)(1 << (round0 - 1)));
        var roundConstHorz = Vector256.Create((short)(1 << (bd + filterBits - round0 - 1)));
        var clampLow = Vector256<short>.Zero;
        var clampHigh = Vector256.Create((short)((1 << (bd + 1 + filterBits - round0)) - 1));   // WIENER_CLAMP_LIMIT - 1

        // Add an offset to account for the "add_src" part of the convolve function.
        var coeffsY = Vector128.Create(filterY[0], filterY[1], filterY[2], (short)(filterY[3] + (1 << filterBits)), filterY[4],
            filterY[5], filterY[6], filterY[7]);
        var filterCoeffsY = Vector256.Create(coeffsY, coeffsY).AsInt32();
        var coeffsV0 = Avx2.Shuffle(filterCoeffsY, 0x00).AsInt16();
        var coeffsV1 = Avx2.Shuffle(filterCoeffsY, 0x55).AsInt16();
        var coeffsV2 = Avx2.Shuffle(filterCoeffsY, 0xaa).AsInt16();
        var coeffsV3 = Avx2.Shuffle(filterCoeffsY, 0xff).AsInt16();

        var roundConstV = Vector256.Create((1 << (round1 - 1)) - (1 << (bd + round1 - 1)));

        fixed (byte* srcBase = src)
        fixed (byte* dstBase = dst)
        {
            byte* srcPtr = srcBase + s0 - centerTap * srcStride - centerTap;
            byte* dstPtr = dstBase + d0;
            for (int j = 0; j < w; j += 8)
            {
                for (int i = 0; i < imH; i += 2)
                {
                    var data = Vector256.Create(Sse2.LoadVector128(&srcPtr[i * srcStride + j]), Vector128<byte>.Zero);
                    // Load the next line
                    if (i + 1 < imH)
                        data = Avx2.InsertVector128(data, Sse2.LoadVector128(&srcPtr[i * srcStride + j + srcStride]), 1);

                    // convolve_lowbd_x
                    var sx0 = Avx2.Shuffle(data, filt0);
                    var sx1 = Avx2.Shuffle(data, filt1);
                    var sx2 = Avx2.Shuffle(data, filt2);
                    var sx3 = Avx2.Shuffle(data, filt3);
                    var res01 = Avx2.MultiplyAddAdjacent(sx0, coeffsH0);
                    var res23 = Avx2.MultiplyAddAdjacent(sx1, coeffsH1);
                    var res45 = Avx2.MultiplyAddAdjacent(sx2, coeffsH2);
                    var res67 = Avx2.MultiplyAddAdjacent(sx3, coeffsH3);
                    var res = Avx2.Add(Avx2.Add(res01, res45), Avx2.Add(res23, res67));

                    res = Avx2.ShiftRightArithmetic(Avx2.Add(res, roundConstH), round0);

                    var data0 = Avx2.Shuffle(data, filtCenter).AsInt16();
                    // multiply the center pixel by 2^(FILTER_BITS - round_0) and add it to the result
                    data0 = Avx2.ShiftLeftLogical(data0, filterBits - round0);
                    res = Avx2.Add(res, data0);
                    res = Avx2.Add(res, roundConstHorz);
                    var resClamped = Avx2.Min(Avx2.Max(res, clampLow), clampHigh);
                    Avx.Store(imBlock + i * imStride, resClamped);
                }

                /* Vertical filter */
                {
                    var src0 = Avx.LoadVector256(imBlock + 0 * imStride);
                    var src1 = Avx.LoadVector256(imBlock + 1 * imStride);
                    var src2 = Avx.LoadVector256(imBlock + 2 * imStride);
                    var src3 = Avx.LoadVector256(imBlock + 3 * imStride);
                    var src4 = Avx.LoadVector256(imBlock + 4 * imStride);
                    var src5 = Avx.LoadVector256(imBlock + 5 * imStride);

                    var s0v = Avx2.UnpackLow(src0, src1);
                    var s1v = Avx2.UnpackLow(src2, src3);
                    var s2v = Avx2.UnpackLow(src4, src5);
                    var s4v = Avx2.UnpackHigh(src0, src1);
                    var s5v = Avx2.UnpackHigh(src2, src3);
                    var s6v = Avx2.UnpackHigh(src4, src5);

                    int i;
                    for (i = 0; i < h - 1; i += 2)
                    {
                        short* data = &imBlock[i * imStride];
                        var s6 = Avx.LoadVector256(data + 6 * imStride);
                        var s7 = Avx.LoadVector256(data + 7 * imStride);
                        var s3v = Avx2.UnpackLow(s6, s7);
                        var s7v = Avx2.UnpackHigh(s6, s7);

                        var resA = Convolve(s0v, s1v, s2v, s3v, coeffsV0, coeffsV1, coeffsV2, coeffsV3);
                        var resB = Convolve(s4v, s5v, s6v, s7v, coeffsV0, coeffsV1, coeffsV2, coeffsV3);

                        var resARound = Avx2.ShiftRightArithmetic(Avx2.Add(resA, roundConstV), round1);
                        var resBRound = Avx2.ShiftRightArithmetic(Avx2.Add(resB, roundConstV), round1);

                        /* rounding code */
                        // 16 bit conversion
                        var res16 = Avx2.PackSignedSaturate(resARound, resBRound);
                        // 8 bit conversion and saturation to uint8
                        var res8 = Avx2.PackUnsignedSaturate(res16, res16);

                        var r0 = res8.GetLower();
                        var r1 = res8.GetUpper();
                        // Store values into the destination buffer
                        *(ulong*)&dstPtr[i * dstStride + j] = r0.AsUInt64().ToScalar();
                        *(ulong*)&dstPtr[i * dstStride + j + dstStride] = r1.AsUInt64().ToScalar();

                        s0v = s1v;
                        s1v = s2v;
                        s2v = s3v;
                        s4v = s5v;
                        s5v = s6v;
                        s6v = s7v;
                    }
                    if (h - i != 0)
                    {
                        s0v = Avx2.Permute2x128(s0v, s4v, 0x20);
                        s1v = Avx2.Permute2x128(s1v, s5v, 0x20);
                        s2v = Avx2.Permute2x128(s2v, s6v, 0x20);

                        short* data = &imBlock[i * imStride];
                        var s6_ = Sse2.LoadVector128(data + 6 * imStride);
                        var s7_ = Sse2.LoadVector128(data + 7 * imStride);
                        var s3 = Sse2.UnpackLow(s6_, s7_);
                        var s7 = Sse2.UnpackHigh(s6_, s7_);
                        var s3v = Vector256.Create(s3, s7);
                        var convolveRes = Convolve(s0v, s1v, s2v, s3v, coeffsV0, coeffsV1, coeffsV2, coeffsV3);
                        var resRound = Avx2.ShiftRightArithmetic(Avx2.Add(convolveRes, roundConstV), round1);

                        /* rounding code */
                        // 16 bit conversion
                        var res16 = Sse41.PackUnsignedSaturate(resRound.GetLower(), resRound.GetUpper());
                        // 8 bit conversion and saturation to uint8
                        var res8 = Sse2.PackUnsignedSaturate(res16.AsInt16(), res16.AsInt16());
                        *(ulong*)&dstPtr[i * dstStride + j] = res8.AsUInt64().ToScalar();
                    }
                }
            }
        }
    }

    // ---- selfguided_avx2.c --------------------------------------------------------------------------------------------

    private static class SgrTables
    {
        /// <summary>av1_x_by_xplus1 for the gather.</summary>
        public static readonly int[] XByXplus1 = (int[])AomRestoration.XByXplus1.Clone();
    }

    /// <summary>ALIGN_POWER_OF_TWO(RESTORATION_PROC_UNIT_PELS, 3): one of the four integral-image / A / B buffers.</summary>
    internal const int SgrBufElts = (RestorationProcUnitPels + 7) & ~7;

    // Compute the scan of an AVX2 register holding 8 32-bit integers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Scan32(Vector256<int> x)
    {
        var x01 = Avx2.ShiftLeftLogical128BitLane(x, 4);
        var x02 = Avx2.Add(x, x01);
        var x03 = Avx2.ShiftLeftLogical128BitLane(x02, 8);
        var x04 = Avx2.Add(x02, x03);
        int s = x04.GetElement(3);
        var s02 = Vector256.Create(Vector128<int>.Zero, Vector128.Create(s));
        return Avx2.Add(x04, s02);
    }

    private static unsafe void MemsetZeroAvx(int* dest, int count)
    {
        var zero = Vector256<int>.Zero;
        int i;
        for (i = 0; i < (count & ~31); i += 32)
        {
            Avx.Store(dest + i, zero);
            Avx.Store(dest + i + 8, zero);
            Avx.Store(dest + i + 16, zero);
            Avx.Store(dest + i + 24, zero);
        }
        for (; i < (count & ~7); i += 8) Avx.Store(dest + i, zero);
        for (; i < count; i++) dest[i] = 0;
    }

    // Compute two integral images from src. B sums elements; A sums their squares. The images are offset by one pixel,
    // so will have width and height equal to width + 1, height + 1 and the first row and column will be zero.
    private static unsafe void IntegralImages(byte* src, int srcStride, int width, int height, int* A, int* B, int bufStride)
    {
        MemsetZeroAvx(A, width + 8);
        MemsetZeroAvx(B, width + 8);
        for (int i = 0; i < height; ++i)
        {
            // Zero the left column.
            A[(i + 1) * bufStride] = B[(i + 1) * bufStride] = 0;
            // ldiff is the difference H - D where H is the output sample immediately to the left and D is the output
            // sample above it. These are scalars, replicated across the eight lanes.
            Vector256<int> ldiff1 = Vector256<int>.Zero, ldiff2 = Vector256<int>.Zero;
            for (int j = 0; j < width; j += 8)
            {
                int abj = 1 + j;
                var above1 = Avx.LoadVector256(B + abj + i * bufStride);
                var above2 = Avx.LoadVector256(A + abj + i * bufStride);
                var x1 = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(*(ulong*)(src + j + i * srcStride)).AsByte());
                var x2 = Avx2.MultiplyAddAdjacent(x1.AsInt16(), x1.AsInt16());
                var sc1 = Scan32(x1);
                var sc2 = Scan32(x2);
                var row1 = Avx2.Add(Avx2.Add(sc1, above1), ldiff1);
                var row2 = Avx2.Add(Avx2.Add(sc2, above2), ldiff2);
                Avx.Store(B + abj + (i + 1) * bufStride, row1);
                Avx.Store(A + abj + (i + 1) * bufStride, row2);
                // Calculate the new H - D.
                ldiff1 = Vector256.Create(Avx2.Subtract(row1, above1).GetElement(7));
                ldiff2 = Vector256.Create(Avx2.Subtract(row2, above2).GetElement(7));
            }
        }
    }

    // Compute 8 values of boxsum from the given integral image. ii should point at the middle of the box (for the
    // first value). r is the box radius.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<int> BoxsumFromIi(int* ii, int stride, int r)
    {
        var tl = Avx.LoadVector256(ii - (r + 1) - (r + 1) * stride);
        var tr = Avx.LoadVector256(ii + (r + 0) - (r + 1) * stride);
        var bl = Avx.LoadVector256(ii - (r + 1) + r * stride);
        var br = Avx.LoadVector256(ii + (r + 0) + r * stride);
        var u = Avx2.Subtract(tr, tl);
        var v = Avx2.Subtract(br, bl);
        return Avx2.Subtract(v, u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> RoundForShift(int shift) => Vector256.Create((1 << shift) >> 1);

    // compute_p (8-bit)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ComputeP(Vector256<int> sum1, Vector256<int> sum2, int n)
    {
        var bb = Avx2.MultiplyAddAdjacent(sum1.AsInt16(), sum1.AsInt16());
        var an = Avx2.MultiplyLow(sum2, Vector256.Create(n));
        return Avx2.Subtract(an, bb);
    }

    // calc_ab / calc_ab_fast: step 1 or 2 rows. Assumes that C, D are integral images for the original buffer which has
    // been extended to have a padding of SGRPROJ_BORDER_VERT/SGRPROJ_BORDER_HORZ pixels on the sides. A, B, C, D point
    // at logical position (0, 0).
    private static unsafe void CalcAb(int* A, int* B, int* C, int* D, int width, int height, int bufStride,
        int sgrParamsIdx, int radiusIdx, int step)
    {
        int r = radiusIdx == 0 ? SgrR0[sgrParamsIdx] : SgrR1[sgrParamsIdx];
        int n = (2 * r + 1) * (2 * r + 1);
        var s = Vector256.Create(radiusIdx == 0 ? SgrS0[sgrParamsIdx] : SgrS1[sgrParamsIdx]);
        // one_over_n[n-1] is 2^12/n, so easily fits in an int16
        var oneOverN = Vector256.Create(OneByX[n - 1]);
        var rndZ = RoundForShift(SgrprojMtableBits);
        var rndRes = RoundForShift(SgrprojRecipBits);
        var c255 = Vector256.Create(255);
        var sgr = Vector256.Create(SgrprojSgr);
        fixed (int* xByXplus1 = SgrTables.XByXplus1)
        {
            for (int i = -1; i < height + 1; i += step)
            {
                for (int j = -1; j < width + 1; j += 8)
                {
                    int* cij = C + i * bufStride + j;
                    int* dij = D + i * bufStride + j;
                    var sum1 = BoxsumFromIi(dij, bufStride, r);
                    var sum2 = BoxsumFromIi(cij, bufStride, r);
                    // When width + 2 isn't a multiple of 8, sum1 and sum2 will contain some uninitialised data in their
                    // upper words. We use a mask to ensure that these bits are set to 0.
                    int idx = Math.Min(8, width + 1 - j);
                    if (idx < 8)
                    {
                        var mask = SgrMask(idx);
                        sum1 = Avx2.And(mask, sum1);
                        sum2 = Avx2.And(mask, sum2);
                    }
                    var p = ComputeP(sum1, sum2, n);
                    var z = Avx2.Min(Avx2.ShiftRightLogical(Avx2.Add(Avx2.MultiplyLow(p, s), rndZ), SgrprojMtableBits), c255);
                    var aRes = Avx2.GatherVector256(xByXplus1, z, 4);
                    Avx.Store(A + i * bufStride + j, aRes);
                    var aComplement = Avx2.Subtract(sgr, aRes);
                    // sum1 might have lanes greater than 2^15, so we can't use madd to do multiplication involving
                    // sum1. However, a_complement and one_over_n are both less than 256, so we can multiply them first.
                    var aCompOverN = Avx2.MultiplyAddAdjacent(aComplement.AsInt16(), oneOverN.AsInt16());
                    var bInt = Avx2.MultiplyLow(aCompOverN, sum1);
                    var bRes = Avx2.ShiftRightLogical(Avx2.Add(bInt, rndRes), SgrprojRecipBits);
                    Avx.Store(B + i * bufStride + j, bRes);
                }
            }
        }
    }

    /// <summary>mask[idx]: the first idx 32-bit lanes set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> SgrMask(int idx)
        => Avx2.CompareGreaterThan(Vector256.Create(idx), Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7));

    // Calculate 8 values of the "cross sum" starting at buf: a 3x3 filter where the outer four corners have weight 3
    // and all other pixels have weight 4.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<int> CrossSum(int* buf, int stride)
    {
        var xtl = Avx.LoadVector256(buf - 1 - stride);
        var xt = Avx.LoadVector256(buf - stride);
        var xtr = Avx.LoadVector256(buf + 1 - stride);
        var xl = Avx.LoadVector256(buf - 1);
        var x = Avx.LoadVector256(buf);
        var xr = Avx.LoadVector256(buf + 1);
        var xbl = Avx.LoadVector256(buf - 1 + stride);
        var xb = Avx.LoadVector256(buf + stride);
        var xbr = Avx.LoadVector256(buf + 1 + stride);
        var fours = Avx2.Add(xl, Avx2.Add(xt, Avx2.Add(xr, Avx2.Add(xb, x))));
        var threes = Avx2.Add(xtl, Avx2.Add(xtr, Avx2.Add(xbr, xbl)));
        return Avx2.Subtract(Avx2.ShiftLeftLogical(Avx2.Add(fours, threes), 2), threes);
    }

    // The final filter for self-guided restoration.
    private static unsafe void FinalFilter(int* dst, int dstStride, int* A, int* B, int bufStride, byte* dgd8, int dgdStride,
        int width, int height)
    {
        const int nb = 5;
        var rounding = RoundForShift(SgrprojSgrBits + nb - SgrprojRstBits);
        for (int i = 0; i < height; ++i)
        {
            for (int j = 0; j < width; j += 8)
            {
                var a = CrossSum(A + i * bufStride + j, bufStride);
                var b = CrossSum(B + i * bufStride + j, bufStride);
                var raw = Sse2.LoadVector128(dgd8 + i * dgdStride + j);
                var src = Avx2.ConvertToVector256Int32(raw);
                var v = Avx2.Add(Avx2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                var w = Avx2.ShiftRightArithmetic(Avx2.Add(v, rounding), SgrprojSgrBits + nb - SgrprojRstBits);
                Avx.Store(dst + i * dstStride + j, w);
            }
        }
    }

    // Pixels weighted 5 6 5 / 0 0 0 / 5 6 5 around buf.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<int> CrossSumFastEvenRow(int* buf, int stride)
    {
        var xtl = Avx.LoadVector256(buf - 1 - stride);
        var xt = Avx.LoadVector256(buf - stride);
        var xtr = Avx.LoadVector256(buf + 1 - stride);
        var xbl = Avx.LoadVector256(buf - 1 + stride);
        var xb = Avx.LoadVector256(buf + stride);
        var xbr = Avx.LoadVector256(buf + 1 + stride);
        var fives = Avx2.Add(xtl, Avx2.Add(xtr, Avx2.Add(xbr, xbl)));
        var sixes = Avx2.Add(xt, xb);
        var fivesPlusSixes = Avx2.Add(fives, sixes);
        return Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(fivesPlusSixes, 2), fivesPlusSixes), sixes);
    }

    // Pixels weighted 5 6 5 on buf's row.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<int> CrossSumFastOddRow(int* buf)
    {
        var xl = Avx.LoadVector256(buf - 1);
        var x = Avx.LoadVector256(buf);
        var xr = Avx.LoadVector256(buf + 1);
        var fives = Avx2.Add(xl, xr);
        var sixes = x;
        var fivesPlusSixes = Avx2.Add(fives, sixes);
        return Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(fivesPlusSixes, 2), fivesPlusSixes), sixes);
    }

    private static unsafe void FinalFilterFast(int* dst, int dstStride, int* A, int* B, int bufStride, byte* dgd8,
        int dgdStride, int width, int height)
    {
        const int nb0 = 5, nb1 = 4;
        var rounding0 = RoundForShift(SgrprojSgrBits + nb0 - SgrprojRstBits);
        var rounding1 = RoundForShift(SgrprojSgrBits + nb1 - SgrprojRstBits);
        for (int i = 0; i < height; ++i)
        {
            if ((i & 1) == 0)
            {
                // even row
                for (int j = 0; j < width; j += 8)
                {
                    var a = CrossSumFastEvenRow(A + i * bufStride + j, bufStride);
                    var b = CrossSumFastEvenRow(B + i * bufStride + j, bufStride);
                    var src = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(dgd8 + i * dgdStride + j));
                    var v = Avx2.Add(Avx2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                    var w = Avx2.ShiftRightArithmetic(Avx2.Add(v, rounding0), SgrprojSgrBits + nb0 - SgrprojRstBits);
                    Avx.Store(dst + i * dstStride + j, w);
                }
            }
            else
            {
                // odd row
                for (int j = 0; j < width; j += 8)
                {
                    var a = CrossSumFastOddRow(A + i * bufStride + j);
                    var b = CrossSumFastOddRow(B + i * bufStride + j);
                    var src = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(dgd8 + i * dgdStride + j));
                    var v = Avx2.Add(Avx2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                    var w = Avx2.ShiftRightArithmetic(Avx2.Add(v, rounding1), SgrprojSgrBits + nb1 - SgrprojRstBits);
                    Avx.Store(dst + i * dstStride + j, w);
                }
            }
        }
    }

    /// <summary>av1_selfguided_restoration_avx2 (8-bit). Like libaom it stores whole 8-sample groups, up to 7 samples
    /// past width in each flt row.</summary>
    public static unsafe void SelfguidedRestorationAvx2(byte[] dgd8, int d0, int width, int height, int dgdStride,
        int[] flt0, int f0, int[] flt1, int f1, int fltStride, int sgrParamsIdx, SgrScratch sc)
    {
        int widthExt = width + 2 * SgrprojBorderHorz;
        int heightExt = height + 2 * SgrprojBorderVert;
        // Adjusting the stride of A and B here appears to avoid bad cache effects, leading to a significant speed
        // improvement. We also align the stride to a multiple of 32 bytes for efficiency.
        int bufStride = (widthExt + 16 + 7) & ~7;
        fixed (int* buf = sc.Ii)
        fixed (byte* dgdBase = dgd8)
        fixed (int* flt0Base = flt0)
        fixed (int* flt1Base = flt1)
        {
            // The "tl" pointers point at the top-left of the initialised data for the array.
            int* atl = buf + 0 * SgrBufElts + 7;
            int* btl = buf + 1 * SgrBufElts + 7;
            int* ctl = buf + 2 * SgrBufElts + 7;
            int* dtl = buf + 3 * SgrBufElts + 7;
            // The "0" pointers are (- SGRPROJ_BORDER_VERT, -SGRPROJ_BORDER_HORZ). Note there's a zero row and column
            // in A, B (integral images), so we move down and right one for them.
            int bufDiagBorder = SgrprojBorderHorz + bufStride * SgrprojBorderVert;
            int* a0 = atl + 1 + bufStride;
            int* b0 = btl + 1 + bufStride;
            int* c0 = ctl + 1 + bufStride;
            int* dd0 = dtl + 1 + bufStride;
            // Finally, A, B, C, D point at position (0, 0).
            int* A = a0 + bufDiagBorder;
            int* B = b0 + bufDiagBorder;
            int* C = c0 + bufDiagBorder;
            int* D = dd0 + bufDiagBorder;

            byte* dgd = dgdBase + d0;
            int dgdDiagBorder = SgrprojBorderHorz + dgdStride * SgrprojBorderVert;
            byte* dgd0 = dgd - dgdDiagBorder;

            // Generate integral images from the input. C will contain sums of squares; D will contain just sums
            IntegralImages(dgd0, dgdStride, widthExt, heightExt, ctl, dtl, bufStride);

            // Write to flt0 and flt1
            if (SgrR0[sgrParamsIdx] > 0)
            {
                CalcAb(A, B, C, D, width, height, bufStride, sgrParamsIdx, 0, 2);
                FinalFilterFast(flt0Base + f0, fltStride, A, B, bufStride, dgd, dgdStride, width, height);
            }
            if (SgrR1[sgrParamsIdx] > 0)
            {
                CalcAb(A, B, C, D, width, height, bufStride, sgrParamsIdx, 1, 1);
                FinalFilter(flt1Base + f1, fltStride, A, B, bufStride, dgd, dgdStride, width, height);
            }
        }
    }

    /// <summary>av1_apply_selfguided_restoration_avx2 (8-bit). Stores only the width x height samples (libaom writes the
    /// last 16-sample group whole; the samples past width are never read).</summary>
    public static unsafe void ApplySelfguidedAvx2(byte[] dat, int d0, int width, int height, int stride, int ep, int xqd0,
        int xqd1, byte[] dst, int dst0, int dstStride, int[] flt0, int[] flt1, SgrScratch sc)
    {
        SelfguidedRestorationAvx2(dat, d0, width, height, stride, flt0, 0, flt1, 0, width, ep, sc);
        DecodeXq(xqd0, xqd1, ep, out int xq0i, out int xq1i);
        var xq0 = Vector256.Create(xq0i);
        var xq1 = Vector256.Create(xq1i);
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        var rounding = RoundForShift(SgrprojPrjBits + SgrprojRstBits);
        Span<byte> tail = stackalloc byte[16];
        fixed (byte* datBase = dat)
        fixed (byte* dstBase = dst)
        fixed (int* f0p = flt0)
        fixed (int* f1p = flt1)
        {
            for (int i = 0; i < height; ++i)
            {
                // Calculate output in batches of 16 pixels
                for (int j = 0; j < width; j += 16)
                {
                    int k = i * width + j;
                    int m = i * dstStride + j;
                    byte* dat8ij = datBase + d0 + i * stride + j;
                    var src0 = Sse2.LoadVector128(dat8ij);
                    var ep0 = Avx2.ConvertToVector256Int32(src0);
                    var ep1 = Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(src0, 8));
                    var u0 = Avx2.ShiftLeftLogical(ep0, SgrprojRstBits);
                    var u1 = Avx2.ShiftLeftLogical(ep1, SgrprojRstBits);
                    var v0 = Avx2.ShiftLeftLogical(u0, SgrprojPrjBits);
                    var v1 = Avx2.ShiftLeftLogical(u1, SgrprojPrjBits);
                    if (r0)
                    {
                        var f10 = Avx2.Subtract(Avx.LoadVector256(f0p + k), u0);
                        v0 = Avx2.Add(v0, Avx2.MultiplyLow(xq0, f10));
                        var f11 = Avx2.Subtract(Avx.LoadVector256(f0p + k + 8), u1);
                        v1 = Avx2.Add(v1, Avx2.MultiplyLow(xq0, f11));
                    }
                    if (r1)
                    {
                        var f20 = Avx2.Subtract(Avx.LoadVector256(f1p + k), u0);
                        v0 = Avx2.Add(v0, Avx2.MultiplyLow(xq1, f20));
                        var f21 = Avx2.Subtract(Avx.LoadVector256(f1p + k + 8), u1);
                        v1 = Avx2.Add(v1, Avx2.MultiplyLow(xq1, f21));
                    }
                    var w0 = Avx2.ShiftRightArithmetic(Avx2.Add(v0, rounding), SgrprojPrjBits + SgrprojRstBits);
                    var w1 = Avx2.ShiftRightArithmetic(Avx2.Add(v1, rounding), SgrprojPrjBits + SgrprojRstBits);
                    // Pack into 8 bits and clamp to [0, 256)
                    var tmp = Avx2.PackSignedSaturate(w0, w1);
                    var tmp2 = Avx2.Permute4x64(tmp.AsInt64(), 0xd8).AsInt16();
                    var res = Avx2.PackUnsignedSaturate(tmp2, tmp2);
                    var res2 = Avx2.Permute4x64(res.AsInt64(), 0xd8).GetLower().AsByte();
                    if (j + 16 <= width) Sse2.Store(dstBase + dst0 + m, res2);
                    else
                    {
                        res2.CopyTo(tail);
                        tail.Slice(0, width - j).CopyTo(new Span<byte>(dstBase + dst0 + m, width - j));
                    }
                }
            }
        }
    }
}
