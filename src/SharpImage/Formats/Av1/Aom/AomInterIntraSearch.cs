using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/compound_type.c's inter-intra search (av1_handle_inter_intra_mode with
// handle_smooth_inter_intra_mode / handle_wedge_inter_intra_mode, compute_best_interintra_mode, pick_interintra_wedge,
// pick_wedge_fixed_sign, estimate_yrd_for_sb) and wedge_utils.c.
internal static class AomInterIntraSearch
{
    private const int MAX_INTERINTRA_SB_SQUARE = 32 * 32, WEDGE_WEIGHT_BITS = 6, MAX_MASK_VALUE = 1 << WEDGE_WEIGHT_BITS;

    /// <summary>av1_wedge_sse_from_residuals.</summary>
    public static ulong WedgeSseFromResiduals(short[] r1, short[] d, byte[] m, int n)
    {
        ulong csse = 0;
        for (int i = 0; i < n; i++)
        {
            int t = MAX_MASK_VALUE * r1[i] + m[i] * d[i];
            t = Math.Clamp(t, short.MinValue, short.MaxValue);
            csse += (ulong)(t * t);
        }
        return (csse + (1UL << (2 * WEDGE_WEIGHT_BITS - 1))) >> (2 * WEDGE_WEIGHT_BITS);
    }

    /// <summary>av1_wedge_sign_from_residuals.</summary>
    public static int WedgeSignFromResiduals(short[] ds, byte[] m, int n, long limit)
    {
        long acc = 0;
        for (int i = 0; i < n; i++) acc += ds[i] * m[i];
        return acc > limit ? 1 : 0;
    }

    /// <summary>av1_wedge_compute_delta_squares.</summary>
    public static void WedgeComputeDeltaSquares(short[] d, short[] a, short[] b, int n)
    {
        for (int i = 0; i < n; i++) d[i] = (short)Math.Clamp(a[i] * a[i] - b[i] * b[i], short.MinValue, short.MaxValue);
    }

    /// <summary>aom_subtract_block / aom_highbd_subtract_block into a contiguous diff.</summary>
    internal static void Subtract(short[] diff, int w, int h, byte[]? a8, ushort[]? a16, int aOff, int aStride, byte[]? b8, ushort[]? b16, int bOff,
        int bStride)
    {
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int av = a16 != null ? a16[aOff + r * aStride + c] : a8![aOff + r * aStride + c];
                int bv = b16 != null ? b16[bOff + r * bStride + c] : b8![bOff + r * bStride + c];
                diff[r * w + c] = (short)(av - bv);
            }
    }

    /// <summary>pick_wedge_fixed_sign.</summary>
    internal static long PickWedgeFixedSign(AomComp cpi, AomMacroblock x, int bsize, short[] residual1, short[] diff10, int wedgeSign, out int bestWedgeIndex,
        out ulong bestSse)
    {
        var xd = x.E;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int n = bw * bh;
        long bestRd = long.MaxValue;
        int wedgeTypes = AomInterPred.WedgeTypes(bsize);
        int bdRound = xd.IsHbd ? (xd.Bd - 8) * 2 : 0;
        bestWedgeIndex = -1;
        bestSse = 0;
        for (int wi = 0; wi < wedgeTypes; ++wi)
        {
            var mask = AomInterPred.GetContiguousSoftMask(wi, wedgeSign, bsize);
            ulong sse = WedgeSseFromResiduals(residual1, diff10, mask, n);
            if (bdRound > 0) sse = (sse + (1UL << (bdRound - 1))) >> bdRound;
            AomModelRd.SseFn(AomModelRd.MODELRD_TYPE_MASKED_COMPOUND, cpi, x, bsize, 0, (long)sse, n, out int rate, out long dist);
            rate += x.ModeCosts.WedgeIdxCost[bsize * 16 + wi];
            long rd = AomRd.RdCost(x.Rdmult, rate, dist);
            if (rd < bestRd)
            {
                bestWedgeIndex = wi;
                bestRd = rd;
                bestSse = sse;
            }
        }
        return bestRd - AomRd.RdCost(x.Rdmult, x.ModeCosts.WedgeIdxCost[bsize * 16 + bestWedgeIndex], 0);
    }

    /// <summary>pick_interintra_wedge (p0: the intra prediction, p1: the inter prediction, both contiguous of stride bw).</summary>
    private static long PickInterintraWedge(AomComp cpi, AomMacroblock x, int bsize, byte[]? p08, ushort[]? p016, byte[]? p18, ushort[]? p116)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var src = x.Plane[0].Src;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        var residual1 = new short[128 * 128];
        var diff10 = new short[128 * 128];
        Subtract(residual1, bw, bh, src.Buf, src.Buf16, src.Offset, src.Stride, p18, p116, 0, bw);
        Subtract(diff10, bw, bh, p18, p116, 0, bw, p08, p016, 0, bw);
        long rd = PickWedgeFixedSign(cpi, x, bsize, residual1, diff10, 0, out int wedgeIndex, out _);
        mbmi.InterintraWedgeIndex = (sbyte)wedgeIndex;
        return rd;
    }

    /// <summary>compute_best_interintra_mode.</summary>
    private static void ComputeBestInterintraMode(AomComp cpi, AomMbModeInfo mbmi, AomMacroblockD xd, AomMacroblock x, int iiCostOff, AomBufferSet origDst,
        Buf b, ref int bestMode, ref long bestRd, int mode, int bsize)
    {
        var cm = cpi.Cm;
        int bw = BlockSizeWide[bsize];
        mbmi.InterintraMode = mode;
        int rmode = x.ModeCosts.InterintraModeCost[iiCostOff + mode];
        AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
        AomInterPred.CombineInterintra(xd, bsize, 0, b.TmpBuf2d(bw), b.Intra8, b.Intra16, 0, bw);
        AomModelRd.SbFn(AomModelRd.MODELRD_TYPE_INTERINTRA, cpi, bsize, x, xd, 0, 0, out int rate, out long dist, out _, out _, null, null, null);
        long rd = AomRd.RdCost(x.Rdmult, rate + rmode, dist);
        if (rd < bestRd)
        {
            bestRd = rd;
            bestMode = mbmi.InterintraMode;
        }
    }

    /// <summary>estimate_yrd_for_sb.</summary>
    internal static long EstimateYrdForSb(AomComp cpi, int bs, AomMacroblock x, long refBestRd, out AomRdStats rdStats)
    {
        rdStats = default;
        if (refBestRd < 0) return long.MaxValue;
        AomEncodeMb.SubtractPlane(x, bs, 0);
        long rd = AomTxSearch.EstimateTxfmYrd(cpi, x, ref rdStats, refBestRd, bs, MaxTxsizeRectLookup[bs]);
        if (rd != long.MaxValue)
        {
            int skipCtx = AomTxSearch.SkipTxfmContext(x.E);
            if (rdStats.SkipTxfm != 0) rdStats.Rate = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
            else rdStats.Rate += x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0];
        }
        return rd;
    }

    /// <summary>The tmp_buf_ / intrapred_ scratch of av1_handle_inter_intra_mode.</summary>
    private sealed class Buf
    {
        public byte[]? Tmp8, Intra8;
        public ushort[]? Tmp16, Intra16;
        public AomBuf2d TmpBuf2d(int stride) => new() { Buf = Tmp8!, Buf16 = Tmp16!, Offset = 0, Offset0 = 0, Stride = stride, Width = stride, Height = stride };
    }

    /// <summary>get_rd_thresh_from_best_rd.</summary>
    internal static long RdThreshFromBestRd(long refBestRd, int mulFactor, int divFactor)
    {
        long t = refBestRd;
        if (divFactor != 0) t = refBestRd < divFactor * (long.MaxValue / mulFactor) ? (refBestRd / divFactor) * mulFactor : long.MaxValue;
        return t;
    }

    /// <summary>av1_handle_inter_intra_mode.</summary>
    public static int HandleInterIntraMode(AomComp cpi, AomMacroblock x, int bsize, AomMbModeInfo mbmi, AomHandleInterModeArgs args, long refBestRd,
        ref int rateMv, ref int tmpRate2, AomBufferSet origDst)
    {
        bool trySmooth = cpi.EnableSmoothInterintra;
        bool isWedgeUsed = AomInterPred.IsWedgeUsed(bsize);
        bool tryWedge = isWedgeUsed && x.SourceVariance > (uint)cpi.Sf.inter_sf.disable_interintra_wedge_var_thresh && cpi.EnableInterintraWedge;
        var cm = cpi.Cm;
        var xd = x.E;
        int bw = BlockSizeWide[bsize];
        bool hbd = xd.IsHbd;
        var b = new Buf();
        if (hbd) { b.Tmp16 = new ushort[MAX_INTERINTRA_SB_SQUARE]; b.Intra16 = new ushort[MAX_INTERINTRA_SB_SQUARE]; }
        else { b.Tmp8 = new byte[MAX_INTERINTRA_SB_SQUARE]; b.Intra8 = new byte[MAX_INTERINTRA_SB_SQUARE]; }
        int miRow = xd.MiRow, miCol = xd.MiCol;
        int numPlanes = cm.NumPlanes;
        mbmi.RefFrame1 = NONE_FRAME;
        xd.Plane[0].Dst = b.TmpBuf2d(bw);
        AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
        AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
        mbmi.RefFrame1 = INTRA_FRAME;
        int bestInterintraMode = args.InterIntraMode[mbmi.RefFrame0];
        long bestRdNowedge = long.MaxValue;
        int bestModeRate = int.MaxValue;
        var mc = x.ModeCosts;
        int iiCostOff = SizeGroupLookup[bsize] * 4;
        if (trySmooth)
        {
            // handle_smooth_inter_intra_mode
            mbmi.UseWedgeInterintra = 0;
            if (cpi.Sf.inter_sf.reuse_inter_intra_mode == 0 || bestInterintraMode == INTERINTRA_MODES)
            {
                long bestInterintraRd = long.MaxValue;
                for (int curMode = 0; curMode < INTERINTRA_MODES; ++curMode)
                {
                    if ((!cpi.EnableSmoothIntra || cpi.Sf.intra_sf.disable_smooth_intra != 0) && curMode == II_SMOOTH_PRED) continue;
                    ComputeBestInterintraMode(cpi, mbmi, xd, x, iiCostOff, origDst, b, ref bestInterintraMode, ref bestInterintraRd, curMode, bsize);
                }
                args.InterIntraMode[mbmi.RefFrame0] = bestInterintraMode;
            }
            bool reuse = cpi.Sf.inter_sf.reuse_inter_intra_mode != 0 || bestInterintraMode != INTERINTRA_MODES;
            if (reuse || bestInterintraMode != INTERINTRA_MODES - 1)
            {
                mbmi.InterintraMode = bestInterintraMode;
                AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
                AomInterPred.CombineInterintra(xd, bsize, 0, b.TmpBuf2d(bw), b.Intra8, b.Intra16, 0, bw);
            }
            int rmode = mc.InterintraModeCost[iiCostOff + bestInterintraMode] + (isWedgeUsed ? mc.WedgeInterintraCost[bsize * 2 + 0] : 0);
            int totalModeRate = rmode + rateMv;
            long rdThresh = RdThreshFromBestRd(refBestRd, 1 << INTER_INTRA_RD_THRESH_SHIFT, INTER_INTRA_RD_THRESH_SCALE) -
                            AomRd.RdCost(x.Rdmult, totalModeRate, 0);
            long rd = EstimateYrdForSb(cpi, bsize, x, rdThresh, out var rs);
            if (rd != long.MaxValue) rd = AomRd.RdCost(x.Rdmult, totalModeRate + rs.Rate, rs.Dist);
            else return IGNORE_MODE;
            bestRdNowedge = rd;
            bestModeRate = rmode;
            if (refBestRd < long.MaxValue && (bestRdNowedge >> INTER_INTRA_RD_THRESH_SHIFT) * INTER_INTRA_RD_THRESH_SCALE > refBestRd) return IGNORE_MODE;
        }
        long bestRdWedge = long.MaxValue;
        var mv0 = mbmi.Mv0;
        var tmpMv = mv0;
        int tmpRateMv = 0, rateOverhead = 0;
        if (tryWedge)
        {
            // handle_wedge_inter_intra_mode
            mbmi.UseWedgeInterintra = 1;
            if (cpi.Sf.inter_sf.fast_interintra_wedge_search == 0)
            {
                long bestTotalRd = long.MaxValue;
                int bestMode = 0, bestWedgeIndex = 0;
                for (int mode = 0; mode < INTERINTRA_MODES; ++mode)
                {
                    mbmi.InterintraMode = mode;
                    AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
                    long rd = PickInterintraWedge(cpi, x, bsize, b.Intra8, b.Intra16, b.Tmp8, b.Tmp16);
                    int over = mc.InterintraModeCost[iiCostOff + mode] + mc.WedgeIdxCost[bsize * 16 + mbmi.InterintraWedgeIndex];
                    long totalRd = rd + AomRd.RdCost(x.Rdmult, over, 0);
                    if (totalRd < bestTotalRd)
                    {
                        bestTotalRd = totalRd;
                        bestRdWedge = rd;
                        bestMode = mbmi.InterintraMode;
                        bestWedgeIndex = mbmi.InterintraWedgeIndex;
                    }
                }
                mbmi.InterintraMode = bestMode;
                mbmi.InterintraWedgeIndex = (sbyte)bestWedgeIndex;
                if (bestMode != INTERINTRA_MODES - 1)
                    AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
            }
            else if (!trySmooth)
            {
                if (bestInterintraMode == INTERINTRA_MODES)
                {
                    mbmi.InterintraMode = INTERINTRA_MODES - 1;
                    bestInterintraMode = INTERINTRA_MODES - 1;
                    AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
                    bestRdWedge = PickInterintraWedge(cpi, x, bsize, b.Intra8, b.Intra16, b.Tmp8, b.Tmp16);
                    for (int curMode = 0; curMode < INTERINTRA_MODES; ++curMode)
                        ComputeBestInterintraMode(cpi, mbmi, xd, x, iiCostOff, origDst, b, ref bestInterintraMode, ref bestRdWedge, curMode, bsize);
                    args.InterIntraMode[mbmi.RefFrame0] = bestInterintraMode;
                    mbmi.InterintraMode = bestInterintraMode;
                    if (bestInterintraMode != INTERINTRA_MODES - 1)
                        AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
                }
                else
                {
                    mbmi.InterintraMode = bestInterintraMode;
                    AomInterPred.BuildIntraPredictorsForInterintra(xd, cm.SbSize, cpi.EnableIntraEdgeFilter, bsize, 0, origDst, b.Intra8, b.Intra16, 0, bw);
                    bestRdWedge = PickInterintraWedge(cpi, x, bsize, b.Intra8, b.Intra16, b.Tmp8, b.Tmp16);
                }
            }
            else bestRdWedge = PickInterintraWedge(cpi, x, bsize, b.Intra8, b.Intra16, b.Tmp8, b.Tmp16);

            rateOverhead = mc.InterintraModeCost[iiCostOff + mbmi.InterintraMode] + mc.WedgeIdxCost[bsize * 16 + mbmi.InterintraWedgeIndex] +
                           mc.WedgeInterintraCost[bsize * 2 + 1];
            bestRdWedge += AomRd.RdCost(x.Rdmult, rateOverhead + rateMv, 0);
            long rdw = long.MaxValue;
            tmpRateMv = rateMv;   // (set below by the compound search when it runs)
            if (AomInter.HaveNewmvInInterMode(mbmi.Mode))
            {
                // refine the motion vector against the (negated) wedge-masked intra prediction
                var comp = new AomCompoundRefs
                {
                    SecondPred8 = xd.IsHbd ? null : b.Intra8, SecondPred16 = xd.IsHbd ? b.Intra16 : null,
                    Mask = AomInterPred.GetContiguousSoftMask(mbmi.InterintraWedgeIndex, 1, bsize), MaskOffset = 0, MaskStride = bw,
                };
                AomMotionSearch.CompoundSingleMotionSearch(cpi, x, bsize, ref tmpMv, comp, out tmpRateMv, 0);
                if (mbmi.Mv0.AsInt != tmpMv.AsInt)
                {
                    mbmi.Mv0 = tmpMv;
                    mbmi.RefFrame1 = NONE_FRAME;   // no intra prediction inside the inter build
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                    mbmi.RefFrame1 = INTRA_FRAME;
                    AomInterPred.CombineInterintra(xd, bsize, 0, xd.Plane[0].Dst, b.Intra8, b.Intra16, 0, bw);
                    AomModelRd.SbFn(AomModelRd.MODELRD_TYPE_MASKED_COMPOUND, cpi, bsize, x, xd, 0, 0, out int rateSum, out long distSum,
                        out _, out _, null, null, null);
                    rdw = AomRd.RdCost(x.Rdmult, tmpRateMv + rateOverhead + rateSum, distSum);
                }
            }
            if (rdw >= bestRdWedge)
            {
                tmpMv = mv0;
                tmpRateMv = rateMv;
                AomInterPred.CombineInterintra(xd, bsize, 0, b.TmpBuf2d(bw), b.Intra8, b.Intra16, 0, bw);
            }
            long modeRd = AomRd.RdCost(x.Rdmult, rateOverhead + tmpRateMv, 0);
            long tmpRdThresh = bestRdNowedge - modeRd;
            rdw = EstimateYrdForSb(cpi, bsize, x, tmpRdThresh, out var rs);
            if (rdw != long.MaxValue) rdw = AomRd.RdCost(x.Rdmult, rateOverhead + tmpRateMv + rs.Rate, rs.Dist);
            else if (bestRdWedge == long.MaxValue) return IGNORE_MODE;
            bestRdWedge = rdw;
        }
        if (bestRdNowedge == long.MaxValue && bestRdWedge == long.MaxValue) return IGNORE_MODE;
        if (bestRdWedge < bestRdNowedge)
        {
            mbmi.Mv0 = tmpMv;
            tmpRate2 += tmpRateMv - rateMv;
            rateMv = tmpRateMv;
            bestModeRate = rateOverhead;
        }
        else if (trySmooth && tryWedge)
        {
            mbmi.UseWedgeInterintra = 0;
            mbmi.InterintraMode = bestInterintraMode;
            mbmi.Mv0 = mv0;
            AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
        }
        tmpRate2 += bestModeRate;
        if (numPlanes > 1) AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 1, numPlanes - 1, cpi.EnableIntraEdgeFilter);
        return 0;
    }
}
