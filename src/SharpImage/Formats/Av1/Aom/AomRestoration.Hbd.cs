using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

/// <summary>The high bit depth paths of libaom 3.14.1 av1/common/restoration.c on 16-bit planes: the stripe boundary
/// lines, extend_frame_highbd, the unit filter with the stripe swap, and the kernels as an AVX2 machine runs them:
/// av1_highbd_wiener_convolve_add_src_avx2 (av1/common/x86/highbd_wiener_convolve_avx2.c), the highbd branches of
/// av1_selfguided_restoration_avx2 / av1_apply_selfguided_restoration_avx2 (selfguided_avx2.c).</summary>
internal static partial class AomRestoration
{
    private static int BitDepthOf(AomYv12Plane p) => p.BitDepth;

    // ---- frame / boundary lines -----------------------------------------------------------------------------------

    /// <summary>extend_frame_highbd.</summary>
    public static void ExtendFrameHbd(AomYv12Plane p, int width, int height, int borderHorz, int borderVert)
    {
        ushort[] b = p.Buf16;
        for (int i = 0; i < height; ++i)
        {
            int row = p.At(0, i);
            b.AsSpan(row - borderHorz, borderHorz).Fill(b[row]);
            b.AsSpan(row + width, borderHorz).Fill(b[row + width - 1]);
        }
        int w = width + 2 * borderHorz;
        for (int i = -borderVert; i < 0; ++i) Array.Copy(b, p.At(-borderHorz, 0), b, p.At(-borderHorz, i), w);
        for (int i = height; i < height + borderVert; ++i) Array.Copy(b, p.At(-borderHorz, height - 1), b, p.At(-borderHorz, i), w);
    }

    private static void ExtendLines16(ushort[] buf, int start, int width, int height, int stride, int extend)
    {
        for (int i = 0; i < height; ++i, start += stride)
        {
            buf.AsSpan(start - extend, extend).Fill(buf[start]);
            buf.AsSpan(start + width, extend).Fill(buf[start + width - 1]);
        }
    }

    private static void SaveDeblockBoundaryLines16(AomYv12Plane p, AomStripeBoundaries bnd, int row, int stripe, bool isAbove)
    {
        ushort[] dstBuf = isAbove ? bnd.Above16 : bnd.Below16;
        int bdryRows = RestorationExtraHorz + RestorationCtxVert * stripe * bnd.Stride;
        int linesToSave = Math.Min(RestorationCtxVert, p.CropHeight - row);
        int width = p.CropWidth;
        for (int i = 0; i < linesToSave; i++) Array.Copy(p.Buf16, p.At(0, row + i), dstBuf, bdryRows + i * bnd.Stride, width);
        if (linesToSave == 1) Array.Copy(dstBuf, bdryRows, dstBuf, bdryRows + bnd.Stride, width);
        ExtendLines16(dstBuf, bdryRows, width, RestorationCtxVert, bnd.Stride, RestorationExtraHorz);
    }

    private static void SaveCdefBoundaryLines16(AomYv12Plane p, AomStripeBoundaries bnd, int row, int stripe, bool isAbove)
    {
        ushort[] dstBuf = isAbove ? bnd.Above16 : bnd.Below16;
        int bdryRows = RestorationExtraHorz + RestorationCtxVert * stripe * bnd.Stride;
        int width = p.CropWidth;
        for (int i = 0; i < RestorationCtxVert; i++) Array.Copy(p.Buf16, p.At(0, row), dstBuf, bdryRows + i * bnd.Stride, width);
        ExtendLines16(dstBuf, bdryRows, width, RestorationCtxVert, bnd.Stride, RestorationExtraHorz);
    }

    private static void SetupProcessingStripeBoundary16(in AomRestorationTileLimits limits, AomStripeBoundaries rsb,
        int rsbRow, int h, AomYv12Plane data, UnitScratch rlbs, bool copyAbove, bool copyBelow)
    {
        int bufStride = rsb.Stride, bufX0Off = limits.HStart;
        int lineSize = limits.HEnd - limits.HStart + 2 * RestorationExtraHorz;
        int dataX0 = limits.HStart - RestorationExtraHorz;
        if (copyAbove)
            for (int i = -RestorationBorder; i < 0; ++i)
            {
                int bufOff = bufX0Off + (rsbRow + Math.Max(i + RestorationCtxVert, 0)) * bufStride;
                int d = data.At(dataX0, limits.VStart + i);
                Array.Copy(data.Buf16, d, rlbs.SaveAbove16[i + RestorationBorder], 0, lineSize);
                Array.Copy(rsb.Above16, bufOff, data.Buf16, d, lineSize);
            }
        if (copyBelow)
        {
            int stripeEnd = limits.VStart + h;
            for (int i = 0; i < RestorationBorder; ++i)
            {
                int bufOff = bufX0Off + (rsbRow + Math.Min(i, RestorationCtxVert - 1)) * bufStride;
                int d = data.At(dataX0, stripeEnd + i);
                Array.Copy(data.Buf16, d, rlbs.SaveBelow16[i], 0, lineSize);
                Array.Copy(rsb.Below16, bufOff, data.Buf16, d, lineSize);
            }
        }
    }

    private static void RestoreProcessingStripeBoundary16(in AomRestorationTileLimits limits, UnitScratch rlbs, int h,
        AomYv12Plane data, bool copyAbove, bool copyBelow)
    {
        int lineSize = limits.HEnd - limits.HStart + 2 * RestorationExtraHorz;
        int dataX0 = limits.HStart - RestorationExtraHorz;
        if (copyAbove)
            for (int i = -RestorationBorder; i < 0; ++i)
                Array.Copy(rlbs.SaveAbove16[i + RestorationBorder], 0, data.Buf16, data.At(dataX0, limits.VStart + i), lineSize);
        if (copyBelow)
        {
            int stripeBottom = limits.VStart + h;
            for (int i = 0; i < RestorationBorder; ++i)
            {
                if (stripeBottom + i >= limits.VEnd + RestorationBorder) break;
                Array.Copy(rlbs.SaveBelow16[i], 0, data.Buf16, data.At(dataX0, stripeBottom + i), lineSize);
            }
        }
    }

    /// <summary>av1_loop_restoration_filter_unit on 16-bit planes (wiener_filter_stripe_highbd /
    /// sgrproj_filter_stripe_highbd).</summary>
    private static void FilterUnitHbd(in AomRestorationTileLimits limits, in AomRestorationUnitInfo rui, AomStripeBoundaries rsb,
        int planeH, int ssX, int ssY, AomYv12Plane data, AomYv12Plane dst, UnitScratch sc, int bd)
    {
        int unitH = limits.VEnd - limits.VStart, unitW = limits.HEnd - limits.HStart;
        int dataTl = data.At(limits.HStart, limits.VStart), dstTl = dst.At(limits.HStart, limits.VStart);
        if (rui.Type == RestoreNone)
        {
            for (int i = 0; i < unitH; ++i) Array.Copy(data.Buf16, dataTl + i * data.Stride, dst.Buf16, dstTl + i * dst.Stride, unitW);
            return;
        }
        int procunitWidth = RestorationProcUnitSize >> ssX;
        AomRestorationTileLimits remaining = limits;
        int iRow = 0;
        while (iRow < unitH)
        {
            remaining.VStart = limits.VStart + iRow;
            GetStripeBoundaryInfo(remaining, planeH, ssY, out bool copyAbove, out bool copyBelow);
            int fullStripeHeight = RestorationProcUnitSize >> ssY, runitOffset = RestorationUnitOffset >> ssY;
            int frameStripe = (remaining.VStart + runitOffset) / fullStripeHeight;
            int rsbRow = RestorationCtxVert * frameStripe;
            int nominalStripeHeight = fullStripeHeight - (frameStripe == 0 ? runitOffset : 0);
            int h = Math.Min(nominalStripeHeight, remaining.VEnd - remaining.VStart);
            SetupProcessingStripeBoundary16(remaining, rsb, rsbRow, h, data, sc, copyAbove, copyBelow);
            int src = dataTl + iRow * data.Stride, dstRow = dstTl + iRow * dst.Stride;
            if (rui.Type == RestoreWiener)
            {
                for (int j = 0; j < unitW; j += procunitWidth)
                {
                    int w = Math.Min(procunitWidth, (unitW - j + 15) & ~15);
                    HighbdWienerConvolveAddSrc(data.Buf16, src + j, data.Stride, dst.Buf16, dstRow + j, dst.Stride, rui.Wiener.H,
                        rui.Wiener.V, w, h, bd);
                }
            }
            else
            {
                for (int j = 0; j < unitW; j += procunitWidth)
                {
                    int w = Math.Min(procunitWidth, unitW - j);
                    ApplySelfguidedHbd(data.Buf16, src + j, w, h, data.Stride, rui.Sgrproj.Ep, rui.Sgrproj.Xqd0, rui.Sgrproj.Xqd1,
                        dst.Buf16, dstRow + j, dst.Stride, sc.Flt0, sc.Flt1, sc.Sgr, bd);
                }
            }
            RestoreProcessingStripeBoundary16(remaining, sc, h, data, copyAbove, copyBelow);
            iRow += h;
        }
    }

    // ---- av1_highbd_wiener_convolve_add_src_avx2 ----------------------------------------------------------------------

    /// <summary>av1_highbd_wiener_convolve_add_src_avx2 (w a multiple of 16 here): get_conv_params_wiener(bd)'s round_0 /
    /// round_1 (3 / 11, at 12 bits 5 / 9), the horizontal pass clamped to [0, WIENER_CLAMP_LIMIT), the vertical to the
    /// bit depth (both through packs_epi32, which the clamps subsume). Writes all w samples of each row.</summary>
    [ThreadStatic] private static ushort[]? _hbdWienerTemp;

    public static void HighbdWienerConvolveAddSrc(ushort[] src, int s0, int srcStride, ushort[] dst, int d0, int dstStride,
        in AomTaps8 hf, in AomTaps8 vf, int w, int h, int bd)
    {
        const int filterBits = 7, maxSb = 128;
        int round0 = 3, round1 = 2 * filterBits - 3;
        int intbufrange = bd + filterBits - round0 + 2;
        if (intbufrange > 16) { round0 += intbufrange - 16; round1 -= intbufrange - 16; }
        int clampHigh = (1 << (bd + 1 + filterBits - round0)) - 1;
        int max = (1 << bd) - 1;
        Span<ushort> temp = _hbdWienerTemp ??= new ushort[(maxSb + 7) * maxSb];
        Span<int> cx = stackalloc int[8], cy = stackalloc int[8];
        for (int k = 0; k < 8; k++) { cx[k] = (short)(hf[k] + (k == 3 ? 1 << filterBits : 0)); cy[k] = (short)(vf[k] + (k == 3 ? 1 << filterBits : 0)); }
        int roundH = (1 << (round0 - 1)) + (1 << (bd + filterBits - 1));
        int roundV = (1 << (round1 - 1)) - (1 << (bd + round1 - 1));
        int intermediateHeight = h + 7;
        int srow = s0 - 3 * srcStride - 3;
        if (Avx2.IsSupported && (w & 7) == 0 && w <= maxSb && srow >= 0 && d0 >= 0
            && srow + (intermediateHeight - 1) * srcStride + w + 7 <= src.Length && d0 + (h - 1) * dstStride + w <= dst.Length)
        {
            // 8 outputs a vector in 32-bit lanes (the scalar sums exactly), packed with the clamps
            ref ushort sr = ref MemoryMarshal.GetArrayDataReference(src);
            ref ushort tr = ref MemoryMarshal.GetReference(temp);
            var x0 = Vector256.Create(cx[0]); var x1 = Vector256.Create(cx[1]); var x2 = Vector256.Create(cx[2]); var x3 = Vector256.Create(cx[3]);
            var x4 = Vector256.Create(cx[4]); var x5 = Vector256.Create(cx[5]); var x6 = Vector256.Create(cx[6]); var x7 = Vector256.Create(cx[7]);
            var rh = Vector256.Create(roundH); var ch = Vector256.Create(clampHigh);
            for (int i = 0; i < intermediateHeight; ++i, srow += srcStride)
                for (int j = 0; j < w; j += 8)
                {
                    ref ushort q = ref Unsafe.Add(ref sr, srow + j);
                    var sum = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q)) * x0 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 1)) * x1
                        + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 2)) * x2 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 3)) * x3
                        + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 4)) * x4 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 5)) * x5
                        + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 6)) * x6 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 7)) * x7;
                    var v = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sum + rh, round0), Vector256<int>.Zero), ch);
                    Sse41.PackUnsignedSaturate(v.GetLower(), v.GetUpper()).StoreUnsafe(ref tr, (nuint)(i * maxSb + j));
                }
            var y0 = Vector256.Create(cy[0]); var y1 = Vector256.Create(cy[1]); var y2 = Vector256.Create(cy[2]); var y3 = Vector256.Create(cy[3]);
            var y4 = Vector256.Create(cy[4]); var y5 = Vector256.Create(cy[5]); var y6 = Vector256.Create(cy[6]); var y7 = Vector256.Create(cy[7]);
            var rv = Vector256.Create(roundV); var mx = Vector256.Create(max);
            ref ushort dr = ref MemoryMarshal.GetArrayDataReference(dst);
            for (int i = 0; i < h; ++i)
                for (int j = 0; j < w; j += 8)
                {
                    ref ushort q = ref Unsafe.Add(ref tr, i * maxSb + j);
                    var sum = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q)) * y0 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, maxSb)) * y1
                        + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 2 * maxSb)) * y2 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 3 * maxSb)) * y3
                        + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 4 * maxSb)) * y4 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 5 * maxSb)) * y5
                        + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 6 * maxSb)) * y6 + Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref q, 7 * maxSb)) * y7;
                    var v = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sum + rv, round1), Vector256<int>.Zero), mx);
                    Sse41.PackUnsignedSaturate(v.GetLower(), v.GetUpper()).StoreUnsafe(ref dr, (nuint)(d0 + i * dstStride + j));
                }
            return;
        }
        for (int i = 0; i < intermediateHeight; ++i, srow += srcStride)
            for (int j = 0; j < w; ++j)
            {
                int sum = 0;
                for (int k = 0; k < 8; k++) sum += src[srow + j + k] * cx[k];
                int v = (sum + roundH) >> round0;
                temp[i * maxSb + j] = (ushort)(v < 0 ? 0 : v > clampHigh ? clampHigh : v);
            }
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
            {
                int sum = 0;
                for (int k = 0; k < 8; k++) sum += temp[(i + k) * maxSb + j] * cy[k];
                int v = (sum + roundV) >> round1;
                dst[d0 + i * dstStride + j] = (ushort)(v < 0 ? 0 : v > max ? max : v);
            }
    }

    // ---- selfguided_avx2.c (highbd) -----------------------------------------------------------------------------------

    private static unsafe void IntegralImagesHighbd(ushort* src, int srcStride, int width, int height, int* A, int* B, int bufStride)
    {
        MemsetZeroAvx(A, width + 8);
        MemsetZeroAvx(B, width + 8);
        for (int i = 0; i < height; ++i)
        {
            A[(i + 1) * bufStride] = B[(i + 1) * bufStride] = 0;
            Vector256<int> ldiff1 = Vector256<int>.Zero, ldiff2 = Vector256<int>.Zero;
            for (int j = 0; j < width; j += 8)
            {
                int abj = 1 + j;
                var above1 = Avx.LoadVector256(B + abj + i * bufStride);
                var above2 = Avx.LoadVector256(A + abj + i * bufStride);
                var x1 = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(src + j + i * srcStride));
                var x2 = Avx2.MultiplyAddAdjacent(x1.AsInt16(), x1.AsInt16());
                var sc1 = Scan32(x1);
                var sc2 = Scan32(x2);
                var row1 = Avx2.Add(Avx2.Add(sc1, above1), ldiff1);
                var row2 = Avx2.Add(Avx2.Add(sc2, above2), ldiff2);
                Avx.Store(B + abj + (i + 1) * bufStride, row1);
                Avx.Store(A + abj + (i + 1) * bufStride, row2);
                ldiff1 = Vector256.Create(Avx2.Subtract(row1, above1).GetElement(7));
                ldiff2 = Vector256.Create(Avx2.Subtract(row2, above2).GetElement(7));
            }
        }
    }

    /// <summary>calc_ab / calc_ab_fast with compute_p's high bit depth scaling.</summary>
    private static unsafe void CalcAbHbd(int* A, int* B, int* C, int* D, int width, int height, int bufStride,
        int sgrParamsIdx, int radiusIdx, int step, int bd)
    {
        int r = radiusIdx == 0 ? SgrR0[sgrParamsIdx] : SgrR1[sgrParamsIdx];
        int n = (2 * r + 1) * (2 * r + 1);
        var s = Vector256.Create(radiusIdx == 0 ? SgrS0[sgrParamsIdx] : SgrS1[sgrParamsIdx]);
        var nV = Vector256.Create(n);
        var oneOverN = Vector256.Create(OneByX[n - 1]).AsInt16();
        var rndZ = RoundForShift(SgrprojMtableBits);
        var rndRes = RoundForShift(SgrprojRecipBits);
        var c255 = Vector256.Create(255);
        var sgr = Vector256.Create(SgrprojSgr);
        var roundingA = RoundForShift(2 * (bd - 8));
        var roundingB = RoundForShift(bd - 8);
        nint oTl = -(r + 1) - (nint)(r + 1) * bufStride, oTr = r - (nint)(r + 1) * bufStride;
        nint oBl = -(r + 1) + (nint)r * bufStride, oBr = r + (nint)r * bufStride;
        int jLast = -1 + ((width + 1) / 8) * 8;
        int idxLast = width + 1 - jLast;
        var maskLast = SgrMask(Math.Min(idxLast, 8));
        fixed (int* xByXplus1 = SgrTables.XByXplus1)
        {
            for (int i = -1; i < height + 1; i += step)
            {
                nint rowOff = (nint)i * bufStride;
                int* cRow = C + rowOff, dRow = D + rowOff, aRow = A + rowOff, bRow = B + rowOff;
                for (int j = -1; j < width + 1; j += 8)
                {
                    int* cij = cRow + j, dij = dRow + j;
                    var sum1 = Avx2.Subtract(Avx2.Subtract(Avx.LoadVector256(dij + oBr), Avx.LoadVector256(dij + oBl)),
                        Avx2.Subtract(Avx.LoadVector256(dij + oTr), Avx.LoadVector256(dij + oTl)));
                    var sum2 = Avx2.Subtract(Avx2.Subtract(Avx.LoadVector256(cij + oBr), Avx.LoadVector256(cij + oBl)),
                        Avx2.Subtract(Avx.LoadVector256(cij + oTr), Avx.LoadVector256(cij + oTl)));
                    if (j == jLast)
                    {
                        sum1 = Avx2.And(maskLast, sum1);
                        sum2 = Avx2.And(maskLast, sum2);
                    }
                    // compute_p (bit_depth > 8)
                    var a = Avx2.ShiftRightLogical(Avx2.Add(sum2, roundingA), (byte)(2 * (bd - 8)));
                    var b = Avx2.ShiftRightLogical(Avx2.Add(sum1, roundingB), (byte)(bd - 8));
                    var bb = Avx2.MultiplyAddAdjacent(b.AsInt16(), b.AsInt16());
                    var an = Avx2.Max(Avx2.MultiplyLow(a, nV), bb);
                    var p = Avx2.Subtract(an, bb);
                    var z = Avx2.Min(Avx2.ShiftRightLogical(Avx2.Add(Avx2.MultiplyLow(p, s), rndZ), SgrprojMtableBits), c255);
                    var aRes = Avx2.GatherVector256(xByXplus1, z, 4);
                    Avx.Store(aRow + j, aRes);
                    var aComplement = Avx2.Subtract(sgr, aRes);
                    var aCompOverN = Avx2.MultiplyAddAdjacent(aComplement.AsInt16(), oneOverN);
                    var bInt = Avx2.MultiplyLow(aCompOverN, sum1);
                    var bRes = Avx2.ShiftRightLogical(Avx2.Add(bInt, rndRes), SgrprojRecipBits);
                    Avx.Store(bRow + j, bRes);
                }
            }
        }
    }

    private static unsafe void FinalFilterHbd(int* dst, int dstStride, int* A, int* B, int bufStride, ushort* dgd, int dgdStride,
        int width, int height)
    {
        const int nb = 5;
        var rounding = RoundForShift(SgrprojSgrBits + nb - SgrprojRstBits);
        nint up = -bufStride, dn = bufStride;
        for (int i = 0; i < height; ++i)
        {
            int* aRow = A + (nint)i * bufStride, bRow = B + (nint)i * bufStride, dRow = dst + (nint)i * dstStride;
            ushort* sRow = dgd + (nint)i * dgdStride;
            for (int j = 0; j < width; j += 8)
            {
                int* a0 = aRow + j, b0 = bRow + j;
                var aFours = Avx2.Add(Avx.LoadVector256(a0 - 1), Avx2.Add(Avx.LoadVector256(a0 + up),
                    Avx2.Add(Avx.LoadVector256(a0 + 1), Avx2.Add(Avx.LoadVector256(a0 + dn), Avx.LoadVector256(a0)))));
                var aThrees = Avx2.Add(Avx.LoadVector256(a0 - 1 + up), Avx2.Add(Avx.LoadVector256(a0 + 1 + up),
                    Avx2.Add(Avx.LoadVector256(a0 + 1 + dn), Avx.LoadVector256(a0 - 1 + dn))));
                var a = Avx2.Subtract(Avx2.ShiftLeftLogical(Avx2.Add(aFours, aThrees), 2), aThrees);
                var bFours = Avx2.Add(Avx.LoadVector256(b0 - 1), Avx2.Add(Avx.LoadVector256(b0 + up),
                    Avx2.Add(Avx.LoadVector256(b0 + 1), Avx2.Add(Avx.LoadVector256(b0 + dn), Avx.LoadVector256(b0)))));
                var bThrees = Avx2.Add(Avx.LoadVector256(b0 - 1 + up), Avx2.Add(Avx.LoadVector256(b0 + 1 + up),
                    Avx2.Add(Avx.LoadVector256(b0 + 1 + dn), Avx.LoadVector256(b0 - 1 + dn))));
                var b = Avx2.Subtract(Avx2.ShiftLeftLogical(Avx2.Add(bFours, bThrees), 2), bThrees);
                var src = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(sRow + j));
                var v = Avx2.Add(Avx2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                Avx.Store(dRow + j, Avx2.ShiftRightArithmetic(Avx2.Add(v, rounding), SgrprojSgrBits + nb - SgrprojRstBits));
            }
        }
    }

    private static unsafe void FinalFilterFastHbd(int* dst, int dstStride, int* A, int* B, int bufStride, ushort* dgd,
        int dgdStride, int width, int height)
    {
        const int nb0 = 5, nb1 = 4;
        var rounding0 = RoundForShift(SgrprojSgrBits + nb0 - SgrprojRstBits);
        var rounding1 = RoundForShift(SgrprojSgrBits + nb1 - SgrprojRstBits);
        nint up = -bufStride, dn = bufStride;
        for (int i = 0; i < height; ++i)
        {
            int* aRow = A + (nint)i * bufStride, bRow = B + (nint)i * bufStride, dRow = dst + (nint)i * dstStride;
            ushort* sRow = dgd + (nint)i * dgdStride;
            for (int j = 0; j < width; j += 8)
            {
                int* a0 = aRow + j, b0 = bRow + j;
                Vector256<int> a, b;
                if ((i & 1) == 0)
                {
                    var aFives = Avx2.Add(Avx.LoadVector256(a0 - 1 + up), Avx2.Add(Avx.LoadVector256(a0 + 1 + up),
                        Avx2.Add(Avx.LoadVector256(a0 + 1 + dn), Avx.LoadVector256(a0 - 1 + dn))));
                    var aSixes = Avx2.Add(Avx.LoadVector256(a0 + up), Avx.LoadVector256(a0 + dn));
                    var aFs = Avx2.Add(aFives, aSixes);
                    a = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(aFs, 2), aFs), aSixes);
                    var bFives = Avx2.Add(Avx.LoadVector256(b0 - 1 + up), Avx2.Add(Avx.LoadVector256(b0 + 1 + up),
                        Avx2.Add(Avx.LoadVector256(b0 + 1 + dn), Avx.LoadVector256(b0 - 1 + dn))));
                    var bSixes = Avx2.Add(Avx.LoadVector256(b0 + up), Avx.LoadVector256(b0 + dn));
                    var bFs = Avx2.Add(bFives, bSixes);
                    b = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(bFs, 2), bFs), bSixes);
                }
                else
                {
                    var aSixes = Avx.LoadVector256(a0);
                    var aFs = Avx2.Add(Avx2.Add(Avx.LoadVector256(a0 - 1), Avx.LoadVector256(a0 + 1)), aSixes);
                    a = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(aFs, 2), aFs), aSixes);
                    var bSixes = Avx.LoadVector256(b0);
                    var bFs = Avx2.Add(Avx2.Add(Avx.LoadVector256(b0 - 1), Avx.LoadVector256(b0 + 1)), bSixes);
                    b = Avx2.Add(Avx2.Add(Avx2.ShiftLeftLogical(bFs, 2), bFs), bSixes);
                }
                var src = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(sRow + j));
                var v = Avx2.Add(Avx2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                var w = (i & 1) == 0
                    ? Avx2.ShiftRightArithmetic(Avx2.Add(v, rounding0), SgrprojSgrBits + nb0 - SgrprojRstBits)
                    : Avx2.ShiftRightArithmetic(Avx2.Add(v, rounding1), SgrprojSgrBits + nb1 - SgrprojRstBits);
                Avx.Store(dRow + j, w);
            }
        }
    }

    /// <summary>av1_selfguided_restoration_avx2 with highbd = 1.</summary>
    public static unsafe void SelfguidedRestorationHbd(ushort[] dgd16, int d0, int width, int height, int dgdStride,
        int[] flt0, int f0, int[] flt1, int f1, int fltStride, int sgrParamsIdx, SgrScratch sc, int bd)
    {
        int widthExt = width + 2 * SgrprojBorderHorz, heightExt = height + 2 * SgrprojBorderVert;
        int bufStride = (widthExt + 16 + 7) & ~7;
        fixed (int* buf = sc.Ii)
        fixed (ushort* dgdBase = dgd16)
        fixed (int* flt0Base = flt0)
        fixed (int* flt1Base = flt1)
        {
            int* atl = buf + 0 * SgrBufElts + 7, btl = buf + 1 * SgrBufElts + 7;
            int* ctl = buf + 2 * SgrBufElts + 7, dtl = buf + 3 * SgrBufElts + 7;
            int bufDiagBorder = SgrprojBorderHorz + bufStride * SgrprojBorderVert;
            int* A = atl + 1 + bufStride + bufDiagBorder;
            int* B = btl + 1 + bufStride + bufDiagBorder;
            int* C = ctl + 1 + bufStride + bufDiagBorder;
            int* D = dtl + 1 + bufStride + bufDiagBorder;
            ushort* dgd = dgdBase + d0;
            ushort* dgd0 = dgd - (SgrprojBorderHorz + dgdStride * SgrprojBorderVert);
            IntegralImagesHighbd(dgd0, dgdStride, widthExt, heightExt, ctl, dtl, bufStride);
            if (SgrR0[sgrParamsIdx] > 0)
            {
                CalcAbHbd(A, B, C, D, width, height, bufStride, sgrParamsIdx, 0, 2, bd);
                FinalFilterFastHbd(flt0Base + f0, fltStride, A, B, bufStride, dgd, dgdStride, width, height);
            }
            if (SgrR1[sgrParamsIdx] > 0)
            {
                CalcAbHbd(A, B, C, D, width, height, bufStride, sgrParamsIdx, 1, 1, bd);
                FinalFilterHbd(flt1Base + f1, fltStride, A, B, bufStride, dgd, dgdStride, width, height);
            }
        }
    }

    /// <summary>av1_apply_selfguided_restoration_avx2 with highbd = 1: packus_epi32 then a signed min with the bit depth's
    /// maximum (a packed value above 32767 passes through, as in the kernel). Stores the width x height samples.</summary>
    public static void ApplySelfguidedHbd(ushort[] dat, int d0, int width, int height, int stride, int ep, int xqd0, int xqd1,
        ushort[] dst, int dst0, int dstStride, int[] flt0, int[] flt1, SgrScratch sc, int bd)
    {
        SelfguidedRestorationHbd(dat, d0, width, height, stride, flt0, 0, flt1, 0, width, ep, sc, bd);
        DecodeXq(xqd0, xqd1, ep, out int xq0, out int xq1);
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        const int sh = SgrprojPrjBits + SgrprojRstBits;
        short max = (short)((1 << bd) - 1);
        for (int i = 0; i < height; ++i)
            for (int j = 0; j < width; ++j)
            {
                int k = i * width + j;
                int u = dat[d0 + i * stride + j] << SgrprojRstBits;
                int v = u << SgrprojPrjBits;
                if (r0) v += xq0 * (flt0[k] - u);
                if (r1) v += xq1 * (flt1[k] - u);
                int w = (v + (1 << (sh - 1))) >> sh;
                ushort packed = (ushort)(w < 0 ? 0 : w > 65535 ? 65535 : w);
                dst[dst0 + i * dstStride + j] = (ushort)Math.Min((short)packed, max);
            }
    }
}
