using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>COMP_RD_STATS.</summary>
internal sealed class AomCompRdStats
{
    public readonly int[] Rate = new int[COMPOUND_TYPES];
    public readonly long[] Dist = new long[COMPOUND_TYPES];
    public readonly int[] ModelRate = new int[COMPOUND_TYPES];
    public readonly long[] ModelDist = new long[COMPOUND_TYPES];
    public readonly int[] CompRs2 = new int[COMPOUND_TYPES];
    public readonly AomMv[] Mv = new AomMv[2];
    public readonly int[] RefFrames = new int[2];
    public int Mode;
    public uint Filter;
    public int RefMvIdx;
    public readonly bool[] IsGlobal = new bool[2];
    public AomInterinterCompound InterinterComp;
}

/// <summary>CompoundTypeRdBuffers (pred0 / pred1 hold either 8-bit or high bit depth samples).</summary>
internal sealed class AomCompoundTypeRdBuffers
{
    public readonly byte[] Pred0 = new byte[128 * 128], Pred1 = new byte[128 * 128];
    public readonly ushort[] Pred0_16 = new ushort[128 * 128], Pred1_16 = new ushort[128 * 128];
    public readonly short[] Residual1 = new short[128 * 128], Diff10 = new short[128 * 128];
}

internal sealed partial class AomMacroblock
{
    /// <summary>x->comp_rd_stats[MAX_COMP_RD_STATS].</summary>
    public readonly AomCompRdStats[] CompRdStats = NewCompRdStats();
    private AomCompoundTypeRdBuffers? _compTypeRdBuffers;
    /// <summary>Created on first use (inter frames).</summary>
    public AomCompoundTypeRdBuffers CompTypeRdBuffers => _compTypeRdBuffers ??= new();

    private static AomCompRdStats[] NewCompRdStats()
    {
        var a = new AomCompRdStats[MAX_COMP_RD_STATS];
        for (int i = 0; i < a.Length; i++) a[i] = new AomCompRdStats();
        return a;
    }
}

internal sealed partial class AomComp
{
    /// <summary>oxcf.comp_type_cfg.enable_diff_wtd_comp.</summary>
    public bool EnableDiffWtdComp = true;
}

internal static partial class AomInterPred
{
    /// <summary>av1_build_inter_predictors_for_planes_single_buf (dst: contiguous, stride ext stride).</summary>
    public static void BuildInterPredictorsForPlanesSingleBuf(AomMacroblockD xd, int bsize, int planeFrom, int planeTo, int r, byte[]? dst8,
        ushort[]? dst16, int dstStride)
    {
        var mi = xd.Mi0;
        int miX = xd.MiCol * 4, miY = xd.MiRow * 4;
        var wm = xd.GlobalMotion[r == 0 ? mi.RefFrame0 : mi.RefFrame1];
        bool globalWarpAllowed = AomInter.IsGlobalMvBlock(mi, wm.WmType);
        bool localWarpAllowed = mi.MotionMode == WARPED_CAUSAL;
        for (int plane = planeFrom; plane <= planeTo; ++plane)
        {
            var pd = xd.Plane[plane];
            int planeBsize = AomCfl.GetPlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            int bw = BlockSizeWide[planeBsize], bh = BlockSizeHigh[planeBsize];
            var p = new AomInterPredParams();
            InitInterParams(p, bw, bh, miY >> pd.SubsamplingY, miX >> pd.SubsamplingX, pd.SubsamplingX, pd.SubsamplingY, xd.Bd, xd.IsHbd, false,
                xd.BlockRefScaleFactors[r]!, pd.Pre(r), mi.InterpFilters);
            p.ConvParams = AomConvParams.Get(0, plane, xd.Bd);
            InitWarpParams(p, globalWarpAllowed, localWarpAllowed, r, xd, mi);
            BuildOneInterPredictor(dst8, dst16, 0, dstStride, r == 0 ? mi.Mv0 : mi.Mv1, p);
        }
    }

    /// <summary>av1_build_wedge_inter_predictor_from_buf for plane 0 (build_wedge_inter_predictor_from_buf).</summary>
    public static void BuildWedgeInterPredictorFromBufY(AomMacroblockD xd, int bsize, byte[]? e0, ushort[]? e0_16, byte[]? e1, ushort[]? e1_16,
        int stride)
    {
        var mbmi = xd.Mi0;
        var pd = xd.Plane[0];
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        ref var dstBuf = ref pd.Dst;
        mbmi.InterinterComp.SegMask = xd.SegMask;
        var comp = mbmi.InterinterComp;
        bool hbd = xd.IsHbd;
        if (mbmi.HasSecondRef && AomInter.IsMaskedCompoundType(comp.Type))
        {
            if (comp.Type == COMPOUND_DIFFWTD)
            {
                if (hbd) BuildCompoundDiffwtdMaskHbd(comp.SegMask!, comp.MaskType, e0_16!, 0, stride, e1_16!, 0, stride, h, w, xd.Bd);
                else BuildCompoundDiffwtdMask(comp.SegMask!, comp.MaskType, e0!, 0, stride, e1!, 0, stride, h, w);
            }
            int sb = mbmi.Bsize;
            int subh = (2 << MiSizeHighLog2[sb]) == h ? 1 : 0;
            int subw = (2 << MiSizeWideLog2[sb]) == w ? 1 : 0;
            var mask = GetCompoundTypeMask(comp, sb);
            if (hbd) BlendA64MaskHbd(dstBuf.Buf16, dstBuf.Offset, dstBuf.Stride, e0_16!, 0, stride, e1_16!, 0, stride, mask, 0, BlockSizeWide[sb], w, h, subw, subh);
            else BlendA64Mask(dstBuf.Buf, dstBuf.Offset, dstBuf.Stride, e0!, 0, stride, e1!, 0, stride, mask, 0, BlockSizeWide[sb], w, h, subw, subh);
        }
        else
        {
            for (int r = 0; r < h; r++)
                if (hbd) Array.Copy(e0_16!, r * stride, dstBuf.Buf16, dstBuf.Offset + r * dstBuf.Stride, w);
                else Array.Copy(e0!, r * stride, dstBuf.Buf, dstBuf.Offset + r * dstBuf.Stride, w);
        }
    }
}

// Port of libaom 3.14.1 av1/encoder/compound_type.c's av1_compound_type_rd (with masked_compound_type_rd and its helpers),
// rdopt.c's process_compound_inter_mode and motion_search_facade.c's av1_interinter_compound_motion_search /
// av1_joint_motion_search.
internal static partial class AomRdoptInter
{
    private static readonly int[] NumCompModeSkipCand = { 5, 4, 2 };
    private static readonly int[] CompTypeRdThresholdMul = { 1, 11, 12 };
    private static readonly int[] CompTypeRdThresholdDiv = { 3, 16, 16 };
    private const int NUM_JOINT_ME_REFINE_ITER = 2, REDUCED_JOINT_ME_REFINE_ITER = 1;

    /// <summary>process_compound_inter_mode: returns true when the mode is not the best (skip it).</summary>
    private static bool ProcessCompoundInterMode(AomComp cpi, AomMacroblock x, AomHandleInterModeArgs args, long refBestRd, AomMv[] curMv, int bsize,
        out int compmodeInterinterCost, AomBufferSet origDst, AomBufferSet tmpDst, ref int rateMv, ref AomRdStats rdStats, long[] skipRd,
        ref int skipBuildPred)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var cm = cpi.Cm;
        bool maskedCompoundUsed = AomInter.IsAnyMaskedCompoundUsed(bsize) && cpi.Seq!.EnableMaskedCompound;
        int modeSearchMask = (1 << COMPOUND_AVERAGE) | (1 << COMPOUND_DISTWTD) | (1 << COMPOUND_WEDGE) | (1 << COMPOUND_DIFFWTD);
        int numPlanes = cm.NumPlanes;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        bool isLumaInterpDone = false;
        AomInterpSearch.SetDefaultInterpFilters(mbmi, cm.InterpFilter);
        long rdThresh = AomInterIntraSearch.RdThreshFromBestRd(refBestRd, 1 << COMP_TYPE_RD_THRESH_SHIFT, COMP_TYPE_RD_THRESH_SCALE);
        compmodeInterinterCost = CompoundTypeRd(cpi, x, args, bsize, curMv, modeSearchMask, maskedCompoundUsed, origDst, tmpDst, x.CompTypeRdBuffers,
            ref rateMv, out long bestRdCompound, ref rdStats, refBestRd, skipRd[1], ref isLumaInterpDone, rdThresh);
        if (refBestRd < long.MaxValue && (bestRdCompound >> COMP_TYPE_RD_THRESH_SHIFT) * COMP_TYPE_RD_THRESH_SCALE > refBestRd)
        {
            AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
            return true;
        }
        if (mbmi.InterinterComp.Type == COMPOUND_AVERAGE && isLumaInterpDone)
        {
            if (numPlanes > 1) AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 1, numPlanes - 1, cpi.EnableIntraEdgeFilter);
            skipBuildPred = INTERP_SKIP_LUMA_SKIP_CHROMA;
        }
        return false;
    }

    private static bool IsCompRdMatch(AomComp cpi, AomMacroblock x, AomCompRdStats st, AomMbModeInfo mi, int[] compRate, long[] compDist,
        int[] compModelRate, long[] compModelDist, int[] compRs2)
    {
        if (st.Filter != mi.InterpFilters) return false;
        var xd = x.E;
        for (int i = 0; i < 2; ++i)
        {
            int rf = i == 0 ? mi.RefFrame0 : mi.RefFrame1;
            var mv = i == 0 ? mi.Mv0 : mi.Mv1;
            if (st.RefFrames[i] != rf || st.Mv[i].AsInt != mv.AsInt) return false;
            if (AomInter.IsGlobalMvBlock(mi, xd.GlobalMotion[rf].WmType) != st.IsGlobal[i]) return false;
        }
        Span<bool> reuse = stackalloc bool[COMPOUND_TYPES] { true, true, false, false };
        if ((!AomInter.HaveNewmvInInterMode(mi.Mode) && !AomInter.HaveNewmvInInterMode(st.Mode)) ||
            cpi.Sf.inter_sf.disable_interinter_wedge_newmv_search != 0)
            reuse[COMPOUND_WEDGE] = true;
        if (cpi.Sf.inter_sf.enable_fast_compound_mode_search != 0 ||
            (!AomInter.HaveNewmvInInterMode(mi.Mode) && !AomInter.HaveNewmvInInterMode(st.Mode)))
            reuse[COMPOUND_DIFFWTD] = true;
        for (int t = COMPOUND_AVERAGE; t < COMPOUND_TYPES; t++)
            if (reuse[t])
            {
                compRate[t] = st.Rate[t];
                compDist[t] = st.Dist[t];
                compModelRate[t] = st.ModelRate[t];
                compModelDist[t] = st.ModelDist[t];
                compRs2[t] = st.CompRs2[t];
            }
        return true;
    }

    private static bool FindCompRdInStats(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int[] compRate, long[] compDist, int[] compModelRate,
        long[] compModelDist, int[] compRs2, out int matchIndex)
    {
        for (int j = 0; j < x.CompRdStatsIdx; ++j)
            if (IsCompRdMatch(cpi, x, x.CompRdStats[j], mbmi, compRate, compDist, compModelRate, compModelDist, compRs2))
            {
                matchIndex = j;
                return true;
            }
        matchIndex = 0;
        return false;
    }

    private static bool EnableWedgeInterinterSearch(AomMacroblock x, AomComp cpi) =>
        x.SourceVariance > cpi.Sf.inter_sf.disable_interinter_wedge_var_thresh && cpi.EnableInterinterWedge;

    private static uint VarianceAny(AomMacroblockD xd, byte[]? a8, ushort[]? a16, int aOff, int aStride, byte[]? b8, ushort[]? b16, int bOff,
        int bStride, int w, int h, out uint sse) =>
        xd.IsHbd ? AomHbd.Variance(a16!, aOff, aStride, b16, bOff, bStride, 0, w, h, xd.Bd, out sse)
                 : AomSad.Variance(a8!, aOff, aStride, b8!, bOff, bStride, w, h, out sse);

    private static readonly int[] SplitQtr =
    {
        BLOCK_INVALID, BLOCK_INVALID, BLOCK_INVALID, BLOCK_4X4, BLOCK_4X8, BLOCK_8X4, BLOCK_8X8, BLOCK_8X16, BLOCK_16X8, BLOCK_16X16,
        BLOCK_16X32, BLOCK_32X16, BLOCK_32X32, BLOCK_32X64, BLOCK_64X32, BLOCK_64X64, BLOCK_INVALID, BLOCK_INVALID, BLOCK_4X16, BLOCK_16X4,
        BLOCK_8X32, BLOCK_32X8,
    };

    /// <summary>estimate_wedge_sign (the last variance uses stride0, as libaom does).</summary>
    private static int EstimateWedgeSign(AomMacroblock x, int bsize, byte[]? p0, ushort[]? p0_16, int stride0, byte[]? p1, ushort[]? p1_16,
        int stride1)
    {
        var xd = x.E;
        var src = x.Plane[0].Src;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int bwBy2 = bw >> 1, bhBy2 = bh >> 1;
        int f = SplitQtr[bsize];
        int fw = BlockSizeWide[f], fh = BlockSizeHigh[f];
        int sOff2 = src.Offset + bhBy2 * src.Stride + bwBy2;
        VarianceAny(xd, src.Buf, src.Buf16, src.Offset, src.Stride, p0, p0_16, 0, stride0, fw, fh, out uint e00);
        VarianceAny(xd, src.Buf, src.Buf16, sOff2, src.Stride, p0, p0_16, bhBy2 * stride0 + bwBy2, stride0, fw, fh, out uint e01);
        VarianceAny(xd, src.Buf, src.Buf16, src.Offset, src.Stride, p1, p1_16, 0, stride1, fw, fh, out uint e10);
        VarianceAny(xd, src.Buf, src.Buf16, sOff2, src.Stride, p1, p1_16, bhBy2 * stride1 + bwBy2, stride0, fw, fh, out uint e11);
        long tl = (long)e00 - e10;
        long br = (long)e11 - e01;
        return tl + br > 0 ? 1 : 0;
    }

    private static ulong SumSquaresI16(short[] a, int n)
    {
        ulong s = 0;
        for (int i = 0; i < n; i++) s += (ulong)(a[i] * a[i]);
        return s;
    }

    /// <summary>pick_wedge.</summary>
    private static long PickWedge(AomComp cpi, AomMacroblock x, int bsize, byte[]? p0, ushort[]? p0_16, short[] residual1, short[] diff10,
        out int bestWedgeSign, out int bestWedgeIndex, out ulong bestSse)
    {
        var xd = x.E;
        var src = x.Plane[0].Src;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int n = bw * bh;
        long bestRd = long.MaxValue;
        int wedgeTypes = AomInterPred.WedgeTypes(bsize);
        int bdRound = xd.IsHbd ? (xd.Bd - 8) * 2 : 0;
        var residual0 = new short[n];
        AomInterIntraSearch.Subtract(residual0, bw, bh, src.Buf, src.Buf16, src.Offset, src.Stride, p0, p0_16, 0, bw);
        long signLimit = ((long)SumSquaresI16(residual0, n) - (long)SumSquaresI16(residual1, n)) * (1 << 6) / 2;
        var ds = residual0;
        AomInterIntraSearch.WedgeComputeDeltaSquares(ds, residual0, residual1, n);
        bestWedgeSign = 0; bestWedgeIndex = 0; bestSse = 0;
        for (int wi = 0; wi < wedgeTypes; ++wi)
        {
            var mask = AomInterPred.GetContiguousSoftMask(wi, 0, bsize);
            int sign = AomInterIntraSearch.WedgeSignFromResiduals(ds, mask, n, signLimit);
            mask = AomInterPred.GetContiguousSoftMask(wi, sign, bsize);
            ulong sse = AomInterIntraSearch.WedgeSseFromResiduals(residual1, diff10, mask, n);
            if (bdRound > 0) sse = (sse + (1UL << (bdRound - 1))) >> bdRound;
            AomModelRd.SseFn(AomModelRd.MODELRD_TYPE_MASKED_COMPOUND, cpi, x, bsize, 0, (long)sse, n, out int rate, out long dist);
            rate += x.ModeCosts.WedgeIdxCost[bsize * 16 + wi];
            long rd = AomRd.RdCost(x.Rdmult, rate, dist);
            if (rd < bestRd)
            {
                bestWedgeIndex = wi;
                bestWedgeSign = sign;
                bestRd = rd;
                bestSse = sse;
            }
        }
        return bestRd - AomRd.RdCost(x.Rdmult, x.ModeCosts.WedgeIdxCost[bsize * 16 + bestWedgeIndex], 0);
    }

    /// <summary>pick_interinter_wedge.</summary>
    private static long PickInterinterWedge(AomComp cpi, AomMacroblock x, int bsize, AomCompoundTypeRdBuffers b, out ulong bestSse)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int bw = BlockSizeWide[bsize];
        bool hbd = xd.IsHbd;
        long rd;
        int wedgeIndex, wedgeSign;
        if (cpi.Sf.inter_sf.fast_wedge_sign_estimate != 0)
        {
            wedgeSign = EstimateWedgeSign(x, bsize, hbd ? null : b.Pred0, hbd ? b.Pred0_16 : null, bw, hbd ? null : b.Pred1, hbd ? b.Pred1_16 : null, bw);
            rd = AomInterIntraSearch.PickWedgeFixedSign(cpi, x, bsize, b.Residual1, b.Diff10, wedgeSign, out wedgeIndex, out bestSse);
        }
        else rd = PickWedge(cpi, x, bsize, hbd ? null : b.Pred0, hbd ? b.Pred0_16 : null, b.Residual1, b.Diff10, out wedgeSign, out wedgeIndex, out bestSse);
        mbmi.InterinterComp.WedgeSign = (sbyte)wedgeSign;
        mbmi.InterinterComp.WedgeIndex = (sbyte)wedgeIndex;
        return rd;
    }

    /// <summary>pick_interinter_seg.</summary>
    private static long PickInterinterSeg(AomComp cpi, AomMacroblock x, int bsize, AomCompoundTypeRdBuffers b, out ulong bestSse)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int n = 1 << NumPelsLog2Lookup[bsize];
        long bestRd = long.MaxValue;
        int bestMaskType = 0;
        bool hbd = xd.IsHbd;
        int bdRound = hbd ? (xd.Bd - 8) * 2 : 0;
        var segMask = new byte[2 * 128 * 128];
        var tmpMask = new[] { xd.SegMask, segMask };
        bestSse = 0;
        for (int t = 0; t < DIFFWTD_MASK_TYPES; t++)
        {
            if (hbd) AomInterPred.BuildCompoundDiffwtdMaskHbd(tmpMask[t], t, b.Pred0_16, 0, bw, b.Pred1_16, 0, bw, bh, bw, xd.Bd);
            else AomInterPred.BuildCompoundDiffwtdMask(tmpMask[t], t, b.Pred0, 0, bw, b.Pred1, 0, bw, bh, bw);
            ulong sse = AomInterIntraSearch.WedgeSseFromResiduals(b.Residual1, b.Diff10, tmpMask[t], n);
            if (bdRound > 0) sse = (sse + (1UL << (bdRound - 1))) >> bdRound;
            AomModelRd.SseFn(AomModelRd.MODELRD_TYPE_MASKED_COMPOUND, cpi, x, bsize, 0, (long)sse, n, out int rate, out long dist);
            long rd0 = AomRd.RdCost(x.Rdmult, rate, dist);
            if (rd0 < bestRd)
            {
                bestMaskType = t;
                bestRd = rd0;
                bestSse = sse;
            }
        }
        mbmi.InterinterComp.MaskType = bestMaskType;
        if (bestMaskType == DIFFWTD_38_INV) Array.Copy(segMask, xd.SegMask, n * 2);
        return bestRd;
    }

    /// <summary>get_inter_predictors_masked_compound.</summary>
    private static void GetInterPredictorsMaskedCompound(AomMacroblock x, int bsize, AomCompoundTypeRdBuffers b)
    {
        var xd = x.E;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        bool hbd = xd.IsHbd;
        AomInterPred.BuildInterPredictorsForPlanesSingleBuf(xd, bsize, 0, 0, 0, hbd ? null : b.Pred0, hbd ? b.Pred0_16 : null, bw);
        AomInterPred.BuildInterPredictorsForPlanesSingleBuf(xd, bsize, 0, 0, 1, hbd ? null : b.Pred1, hbd ? b.Pred1_16 : null, bw);
        var src = x.Plane[0].Src;
        AomInterIntraSearch.Subtract(b.Residual1, bw, bh, src.Buf, src.Buf16, src.Offset, src.Stride, hbd ? null : b.Pred1, hbd ? b.Pred1_16 : null, 0, bw);
        AomInterIntraSearch.Subtract(b.Diff10, bw, bh, hbd ? null : b.Pred1, hbd ? b.Pred1_16 : null, 0, bw, hbd ? null : b.Pred0, hbd ? b.Pred0_16 : null, 0, bw);
    }

    private static void PushCompAvgEstRd(long[] top, long tmpRd, int lvl)
    {
        if (lvl == 0) return;
        int numTopCand = NumCompModeSkipCand[lvl - 1];
        for (int i = 0; i < numTopCand; i++)
            if (tmpRd < top[i])
            {
                for (int j = numTopCand - 1; j > i; j--) top[j] = top[j - 1];
                top[i] = tmpRd;
                break;
            }
    }

    private static bool PruneCompEvalUsingCompAvgEstRd(long[] top, long tmpRd, long refBestRd, int lvl)
    {
        if (lvl == 0) return false;
        int numTopCand = NumCompModeSkipCand[lvl - 1];
        if (top[numTopCand - 1] == long.MaxValue || refBestRd == long.MaxValue) return false;
        return tmpRd > top[numTopCand - 1];
    }

    private static int ComputeValidCompTypes(AomMacroblock x, AomComp cpi, int bsize, bool maskedCompoundUsed, int modeSearchMask, int[] valid)
    {
        int n = 0;
        bool tryAverage = (modeSearchMask & (1 << COMPOUND_AVERAGE)) != 0;
        bool tryDistwtd = (modeSearchMask & (1 << COMPOUND_DISTWTD)) != 0 && cpi.Seq!.EnableDistWtdComp &&
                          cpi.Sf.inter_sf.use_dist_wtd_comp_flag != DIST_WTD_COMP_DISABLED;
        for (int t = COMPOUND_AVERAGE; t <= COMPOUND_DISTWTD; t++)
        {
            bool check = t == COMPOUND_AVERAGE ? tryAverage : tryDistwtd;
            if (check && AomInter.IsInterinterCompoundUsed(t, bsize)) valid[n++] = t;
        }
        if (maskedCompoundUsed)
        {
            Span<bool> en = stackalloc bool[2] { EnableWedgeInterinterSearch(x, cpi), cpi.EnableDiffWtdComp };
            for (int t = COMPOUND_WEDGE; t <= COMPOUND_DIFFWTD; t++)
                if ((modeSearchMask & (1 << t)) != 0 && AomInter.IsInterinterCompoundUsed(t, bsize) && en[t - COMPOUND_WEDGE]) valid[n++] = t;
        }
        return n;
    }

    private static void CalcMaskedTypeCost(AomModeCosts mc, int bsize, int groupCtx, int indexCtx, bool maskedCompoundUsed, int[] cost)
    {
        Array.Clear(cost);
        if (maskedCompoundUsed)
        {
            cost[COMPOUND_AVERAGE] += mc.CompGroupIdxCost[groupCtx * 2 + 0];
            cost[COMPOUND_DISTWTD] += cost[COMPOUND_AVERAGE];
            cost[COMPOUND_WEDGE] += mc.CompGroupIdxCost[groupCtx * 2 + 1];
            cost[COMPOUND_DIFFWTD] += cost[COMPOUND_WEDGE];
        }
        cost[COMPOUND_AVERAGE] += mc.CompIdxCost[indexCtx * 2 + 1];
        cost[COMPOUND_DISTWTD] += mc.CompIdxCost[indexCtx * 2 + 0];
        cost[COMPOUND_WEDGE] += mc.CompoundTypeCost[bsize * 2 + 0];
        cost[COMPOUND_DIFFWTD] += mc.CompoundTypeCost[bsize * 2 + 1];
    }

    private static void UpdateMbmiForCompoundType(AomMbModeInfo mbmi, int t)
    {
        mbmi.InterinterComp.Type = t;
        mbmi.CompGroupIdx = (byte)(t >= COMPOUND_WEDGE ? 1 : 0);
        mbmi.CompoundIdx = (byte)(t != COMPOUND_DISTWTD ? 1 : 0);
    }

    private static void SaveCompRdSearchStat(AomMacroblock x, AomMbModeInfo mbmi, int[] compRate, long[] compDist, int[] compModelRate,
        long[] compModelDist, AomMv[] curMv, int[] compRs2)
    {
        int offset = x.CompRdStatsIdx;
        if (offset >= MAX_COMP_RD_STATS) return;
        var s = x.CompRdStats[offset];
        Array.Copy(compRate, s.Rate, COMPOUND_TYPES);
        Array.Copy(compDist, s.Dist, COMPOUND_TYPES);
        Array.Copy(compModelRate, s.ModelRate, COMPOUND_TYPES);
        Array.Copy(compModelDist, s.ModelDist, COMPOUND_TYPES);
        Array.Copy(compRs2, s.CompRs2, COMPOUND_TYPES);
        s.Mv[0] = curMv[0]; s.Mv[1] = curMv[1];
        s.RefFrames[0] = mbmi.RefFrame0; s.RefFrames[1] = mbmi.RefFrame1;
        s.Mode = mbmi.Mode;
        s.Filter = mbmi.InterpFilters;
        s.RefMvIdx = mbmi.RefMvIdx;
        var xd = x.E;
        s.IsGlobal[0] = AomInter.IsGlobalMvBlock(mbmi, xd.GlobalMotion[mbmi.RefFrame0].WmType);
        s.IsGlobal[1] = AomInter.IsGlobalMvBlock(mbmi, xd.GlobalMotion[mbmi.RefFrame1].WmType);
        s.InterinterComp = mbmi.InterinterComp;
        ++x.CompRdStatsIdx;
    }

    private static int GetInterinterCompoundMaskRate(AomModeCosts mc, AomMbModeInfo mbmi)
    {
        if (mbmi.InterinterComp.Type == COMPOUND_WEDGE)
            return AomInterPred.IsWedgeUsed(mbmi.Bsize) ? AomCost.CostLiteral(1) + mc.WedgeIdxCost[mbmi.Bsize * 16 + mbmi.InterinterComp.WedgeIndex] : 0;
        return AomCost.CostLiteral(1);
    }

    private static void BackupStats(int t, int[] compRate, long[] compDist, int[] compModelRate, long[] compModelDist, int rateSum, long distSum,
        in AomRdStats rd, int[] compRs2, int rs2)
    {
        compRate[t] = rd.Rate;
        compDist[t] = rd.Dist;
        compModelRate[t] = rateSum;
        compModelDist[t] = distSum;
        compRs2[t] = rs2;
    }

    /// <summary>prune_mode_by_skip_rd: true = evaluate the transform.</summary>
    private static bool PruneModeBySkipRd(AomComp cpi, AomMacroblock x, int bsize, long refSkipRd, int modeRate)
    {
        bool evalTxfm = true;
        int level = GetTxfmRdGateLevel(cpi.Seq!.EnableMaskedCompound, cpi.Sf.inter_sf.txfm_rd_gate_level, bsize, TX_SEARCH_COMP_TYPE_MODE, false);
        if (level != 0)
        {
            long sseY = AomModelRd.ComputeSsePlane(x, x.E, 0, bsize);
            long skipRd = AomRd.RdCost(x.Rdmult, modeRate, sseY << 4);
            evalTxfm = CheckTxfmEval(x, bsize, refSkipRd, skipRd, level, true);
        }
        return evalTxfm;
    }

    private static void ModelMaskedY(AomComp cpi, int bsize, AomMacroblock x, out int rateSum, out long distSum) =>
        AomModelRd.SbFn(AomModelRd.MODELRD_TYPE_MASKED_COMPOUND, cpi, bsize, x, x.E, 0, 0, out rateSum, out distSum, out _, out _, null, null, null);

    private static void BuildWedgeFromBuf(AomMacroblock x, int bsize, AomCompoundTypeRdBuffers b)
    {
        var xd = x.E;
        bool hbd = xd.IsHbd;
        AomInterPred.BuildWedgeInterPredictorFromBufY(xd, bsize, hbd ? null : b.Pred0, hbd ? b.Pred0_16 : null, hbd ? null : b.Pred1,
            hbd ? b.Pred1_16 : null, BlockSizeWide[bsize]);
    }

    /// <summary>masked_compound_type_rd.</summary>
    private static long MaskedCompoundTypeRd(AomComp cpi, AomMacroblock x, AomMv[] curMv, int bsize, int thisMode, ref int rs2, int rateMv,
        AomBufferSet ctx, out int outRateMv, AomCompoundTypeRdBuffers b, int modeRate, long rdThresh, ref bool calcPredMaskedCompound,
        int[] compRate, long[] compDist, int[] compModelRate, long[] compModelDist, long compBestModelRd, out long compModelRdCur, int[] compRs2,
        long refSkipRd)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        long rd;
        int compoundType = mbmi.InterinterComp.Type;
        outRateMv = rateMv;
        if (calcPredMaskedCompound)
        {
            GetInterPredictorsMaskedCompound(x, bsize, b);
            calcPredMaskedCompound = false;
        }
        long bestRdCur = compoundType == COMPOUND_WEDGE ? PickInterinterWedge(cpi, x, bsize, b, out ulong curSse) : PickInterinterSeg(cpi, x, bsize, b, out curSse);
        rs2 += GetInterinterCompoundMaskRate(x.ModeCosts, mbmi);
        bestRdCur += AomRd.RdCost(x.Rdmult, rs2 + rateMv, 0);
        long skipRdCur = AomRd.RdCost(x.Rdmult, rs2 + rateMv, (long)(curSse << 4));
        long modeRd = AomRd.RdCost(x.Rdmult, rs2 + modeRate, 0);
        if (modeRd > rdThresh)
        {
            compModelRdCur = long.MaxValue;
            return long.MaxValue;
        }
        int level = GetTxfmRdGateLevel(cpi.Seq!.EnableMaskedCompound, cpi.Sf.inter_sf.txfm_rd_gate_level, bsize, TX_SEARCH_COMP_TYPE_MODE, false);
        if (level != 0 && !CheckTxfmEval(x, bsize, refSkipRd, skipRdCur, level, true))
        {
            compModelRdCur = long.MaxValue;
            return long.MaxValue;
        }
        if (compRate[compoundType] == int.MaxValue)
        {
            bool wedgeNewmvSearch = AomInter.HaveNewmvInInterMode(thisMode) && compoundType == COMPOUND_WEDGE &&
                                    cpi.Sf.inter_sf.disable_interinter_wedge_newmv_search == 0;
            if (wedgeNewmvSearch)
            {
                outRateMv = InterinterCompoundMotionSearch(cpi, x, curMv, bsize, thisMode);
                AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, ctx, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
            }
            else
            {
                outRateMv = rateMv;
                BuildWedgeFromBuf(x, bsize, b);
            }
            ModelMaskedY(cpi, bsize, x, out int rateSum, out long distSum);
            rd = AomRd.RdCost(x.Rdmult, rs2 + outRateMv + rateSum, distSum);
            compModelRdCur = rd;
            if (wedgeNewmvSearch && rd >= bestRdCur)
            {
                mbmi.Mv0 = curMv[0];
                mbmi.Mv1 = curMv[1];
                outRateMv = rateMv;
                BuildWedgeFromBuf(x, bsize, b);
                compModelRdCur = bestRdCur;
            }
            if (cpi.Sf.inter_sf.prune_comp_type_by_model_rd != 0 && compModelRdCur > compBestModelRd && compBestModelRd != long.MaxValue)
            {
                compModelRdCur = long.MaxValue;
                return long.MaxValue;
            }
            long tmpModeRd = AomRd.RdCost(x.Rdmult, rs2 + outRateMv, 0);
            long tmpRdThresh = rdThresh - tmpModeRd;
            rd = AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, tmpRdThresh, out var rdStats);
            if (rd != long.MaxValue)
            {
                rd = AomRd.RdCost(x.Rdmult, rs2 + outRateMv + rdStats.Rate, rdStats.Dist);
                BackupStats(compoundType, compRate, compDist, compModelRate, compModelDist, rateSum, distSum, rdStats, compRs2, rs2);
            }
        }
        else
        {
            outRateMv = rateMv;
            rd = AomRd.RdCost(x.Rdmult, rs2 + outRateMv + compRate[compoundType], compDist[compoundType]);
            compModelRdCur = AomRd.RdCost(x.Rdmult, rs2 + outRateMv + compModelRate[compoundType], compModelDist[compoundType]);
        }
        return rd;
    }

    /// <summary>av1_compound_type_rd.</summary>
    private static int CompoundTypeRd(AomComp cpi, AomMacroblock x, AomHandleInterModeArgs args, int bsize, AomMv[] curMv, int modeSearchMask,
        bool maskedCompoundUsed, AomBufferSet origDst, AomBufferSet tmpDst, AomCompoundTypeRdBuffers buffers, ref int rateMv, out long rd,
        ref AomRdStats rdStats, long refBestRd, long refSkipRd, ref bool isLumaInterpDone, long rdThresh)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var sf = cpi.Sf.inter_sf;
        int thisMode = mbmi.Mode;
        int refFrame = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
        int rs2;
        var bestMv = new AomMv[2];
        int bestTmpRateMv = rateMv;
        var bestCompoundData = new AomInterinterCompound { Type = COMPOUND_AVERAGE };
        int bestCompmodeInterinterCost = 0;
        long compBestModelRd = long.MaxValue;
        int tmpRateMv;
        var maskedTypeCost = new int[COMPOUND_TYPES];
        bool calcPredMaskedCompound = true;
        var compDist = new[] { long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue };
        var compRate = new[] { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
        var compRs2 = new[] { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
        var compModelRate = new[] { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
        var compModelDist = new[] { long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue };
        bool matchFound = FindCompRdInStats(cpi, x, mbmi, compRate, compDist, compModelRate, compModelDist, compRs2, out int matchIndex);
        bestMv[0] = curMv[0]; bestMv[1] = curMv[1];
        rd = long.MaxValue;
        var validCompTypes = new[] { COMPOUND_AVERAGE, COMPOUND_DISTWTD, COMPOUND_WEDGE, COMPOUND_DIFFWTD };
        int validTypeCount = ComputeValidCompTypes(x, cpi, bsize, maskedCompoundUsed, modeSearchMask, validCompTypes);
        int groupCtx = AomPredCommon.CompGroupIdx(xd);
        int indexCtx = AomPredCommon.CompIndex(cm, xd);
        CalcMaskedTypeCost(x.ModeCosts, bsize, groupCtx, indexCtx, maskedCompoundUsed, maskedTypeCost);
        AomTrace.Out?.Write($"mtc {xd.MiRow} {xd.MiCol} bs {bsize} g {groupCtx} i {indexCtx} mcu {(maskedCompoundUsed ? 1 : 0)} {maskedTypeCost[0]} {maskedTypeCost[1]} {maskedTypeCost[2]} {maskedTypeCost[3]} vt {validCompTypes[0]} {validCompTypes[1]} {validCompTypes[2]} {validCompTypes[3]} n {validTypeCount}" + (char)10);
        long compModelRdCur;
        long bestRdCur;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        if (matchFound && sf.reuse_compound_type_decision != 0)
        {
            // populate_reuse_comp_type_data
            int winner = x.CompRdStats[matchIndex].InterinterComp.Type;
            if (compRate[winner] == int.MaxValue) return bestCompmodeInterinterCost;
            UpdateMbmiForCompoundType(mbmi, winner);
            mbmi.InterinterComp = x.CompRdStats[matchIndex].InterinterComp;
            rd = AomRd.RdCost(x.Rdmult, compRs2[winner] + rateMv + compRate[winner], compDist[winner]);
            mbmi.Mv0 = curMv[0];
            mbmi.Mv1 = curMv[1];
            return compRs2[winner];
        }
        if (validCompTypes[0] != COMPOUND_AVERAGE) AomInterpSearch.RestoreDstBuf(xd, tmpDst, 1);
        for (int i = 0; i < validTypeCount; i++)
        {
            int curType = validCompTypes[i];
            if (args.CmpMode[refFrame] == COMPOUND_AVERAGE && curType == COMPOUND_WEDGE) continue;
            compModelRdCur = long.MaxValue;
            tmpRateMv = rateMv;
            bestRdCur = long.MaxValue;
            refBestRd = Math.Min(refBestRd, rd);
            UpdateMbmiForCompoundType(mbmi, curType);
            rs2 = maskedTypeCost[curType];
            long modeRd = AomRd.RdCost(x.Rdmult, rs2 + rdStats.Rate, 0);
            if (modeRd >= refBestRd) continue;
            int fastSearch = sf.enable_fast_compound_mode_search;
            bool skipMvRefinement = fastSearch == 3 || (fastSearch == 2 && thisMode != NEW_NEWMV);
            if (curType < COMPOUND_WEDGE)
            {
                if (skipMvRefinement)
                {
                    if (compRate[curType] == int.MaxValue)
                    {
                        AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                        if (curType == COMPOUND_AVERAGE) isLumaInterpDone = true;
                        long tmpRdThresh = Math.Min(rd, rdThresh) - modeRd;
                        long estRd = long.MaxValue;
                        AomRdStats estRdStats = default;
                        if (PruneModeBySkipRd(cpi, x, bsize, refSkipRd, rs2 + rateMv))
                            estRd = AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, tmpRdThresh, out estRdStats);
                        if (estRd != long.MaxValue)
                        {
                            bestRdCur = AomRd.RdCost(x.Rdmult, rs2 + rateMv + estRdStats.Rate, estRdStats.Dist);
                            ModelMaskedY(cpi, bsize, x, out int rateSum, out long distSum);
                            compModelRdCur = AomRd.RdCost(x.Rdmult, rs2 + rateMv + rateSum, distSum);
                            BackupStats(curType, compRate, compDist, compModelRate, compModelDist, rateSum, distSum, estRdStats, compRs2, rs2);
                        }
                    }
                    else
                    {
                        bestRdCur = AomRd.RdCost(x.Rdmult, rs2 + rateMv + compRate[curType], compDist[curType]);
                        compModelRdCur = AomRd.RdCost(x.Rdmult, rs2 + rateMv + compModelRate[curType], compModelDist[curType]);
                    }
                }
                else
                {
                    tmpRateMv = rateMv;
                    if (AomInter.HaveNewmvInInterMode(thisMode))
                    {
                        var cp = new AomConvParams();
                        AomInterPred.DistWtdCompWeightAssign(cm, mbmi, ref cp, true);
                        Array.Fill(xd.SegMask, (byte)(cp.FwdOffset * 4));
                        tmpRateMv = InterinterCompoundMotionSearch(cpi, x, curMv, bsize, thisMode);
                    }
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                    if (curType == COMPOUND_AVERAGE) isLumaInterpDone = true;
                    long estRd = long.MaxValue;
                    AomRdStats estRdStats = default;
                    if (PruneModeBySkipRd(cpi, x, bsize, refSkipRd, rs2 + rateMv))
                        estRd = AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, long.MaxValue, out estRdStats);
                    if (estRd != long.MaxValue)
                    {
                        bestRdCur = AomRd.RdCost(x.Rdmult, rs2 + tmpRateMv + estRdStats.Rate, estRdStats.Dist);
                        ModelMaskedY(cpi, bsize, x, out int rateSum, out long distSum);
                        compModelRdCur = AomRd.RdCost(x.Rdmult, rs2 + tmpRateMv + rateSum, distSum);
                        BackupStats(curType, compRate, compDist, compModelRate, compModelDist, rateSum, distSum, estRdStats, compRs2, rs2);
                    }
                }
                if (curType == COMPOUND_AVERAGE) AomInterpSearch.RestoreDstBuf(xd, tmpDst, 1);
            }
            else if (curType == COMPOUND_WEDGE)
            {
                int bestMaskIndex = 0, bestWedgeSign = 0;
                var tmpMv0 = mbmi.Mv0; var tmpMv1 = mbmi.Mv1;
                int bestRs2 = 0;
                int bestRateMv = rateMv;
                int wedgeMaskSize = AomInterPred.WedgeTypes(bsize);
                bool needMaskSearch = args.WedgeIndex == -1;
                bool wedgeNewmvSearch = AomInter.HaveNewmvInInterMode(thisMode) && sf.disable_interinter_wedge_newmv_search == 0;
                if ((needMaskSearch && !wedgeNewmvSearch) || sf.skip_interinter_wedge_search_based_on_mse != 0)
                {
                    bool hbd = xd.IsHbd;
                    int bw = BlockSizeWide[bsize];
                    AomInterPred.BuildInterPredictorsForPlanesSingleBuf(xd, bsize, 0, 0, 0, hbd ? null : buffers.Pred0, hbd ? buffers.Pred0_16 : null, bw);
                    AomInterPred.BuildInterPredictorsForPlanesSingleBuf(xd, bsize, 0, 0, 1, hbd ? null : buffers.Pred1, hbd ? buffers.Pred1_16 : null, bw);
                    if (sf.skip_interinter_wedge_search_based_on_mse != 0)
                    {
                        VarianceAny(xd, hbd ? null : buffers.Pred0, hbd ? buffers.Pred0_16 : null, 0, bw, hbd ? null : buffers.Pred1,
                            hbd ? buffers.Pred1_16 : null, 0, bw, bw, BlockSizeHigh[bsize], out uint sse);
                        int sh = NumPelsLog2Lookup[bsize];
                        uint mse = (sse + (1u << (sh - 1))) >> sh;
                        if (mse < 512) continue;
                    }
                }
                for (int wedgeMask = 0; wedgeMask < wedgeMaskSize && needMaskSearch; ++wedgeMask)
                {
                    for (int wedgeSign = 0; wedgeSign < 2; ++wedgeSign)
                    {
                        tmpRateMv = rateMv;
                        mbmi.InterinterComp.WedgeIndex = (sbyte)wedgeMask;
                        mbmi.InterinterComp.WedgeSign = (sbyte)wedgeSign;
                        rs2 = maskedTypeCost[curType];
                        rs2 += GetInterinterCompoundMaskRate(x.ModeCosts, mbmi);
                        modeRd = AomRd.RdCost(x.Rdmult, rs2 + rdStats.Rate, 0);
                        if (modeRd >= refBestRd / 2) continue;
                        if (wedgeNewmvSearch)
                        {
                            tmpRateMv = InterinterCompoundMotionSearch(cpi, x, curMv, bsize, thisMode);
                            AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                        }
                        else BuildWedgeFromBuf(x, bsize, buffers);
                        long thisRdCur = long.MaxValue;
                        AomRdStats estRdStats = default;
                        if (PruneModeBySkipRd(cpi, x, bsize, refSkipRd, rs2 + rateMv))
                            thisRdCur = AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, Math.Min(bestRdCur, refBestRd), out estRdStats);
                        if (thisRdCur < long.MaxValue) thisRdCur = AomRd.RdCost(x.Rdmult, rs2 + tmpRateMv + estRdStats.Rate, estRdStats.Dist);
                        if (thisRdCur < bestRdCur)
                        {
                            bestMaskIndex = wedgeMask;
                            bestWedgeSign = wedgeSign;
                            bestRdCur = thisRdCur;
                            tmpMv0 = mbmi.Mv0; tmpMv1 = mbmi.Mv1;
                            bestRateMv = tmpRateMv;
                            bestRs2 = rs2;
                        }
                    }
                    if (sf.enable_fast_wedge_mask_search != 0)
                    {
                        const int idxBeforeAsymOblique = 7, lastObliqueSymIdx = 3;
                        if (wedgeMask == idxBeforeAsymOblique)
                        {
                            if (bestMaskIndex > lastObliqueSymIdx) break;
                            ReadOnlySpan<int> asymMaskIdx = stackalloc int[4] { 7, 11, 13, 9 };
                            wedgeMask = asymMaskIdx[bestMaskIndex];
                            wedgeMaskSize = wedgeMask + 3;
                        }
                    }
                }
                if (needMaskSearch)
                {
                    if (sf.reuse_mask_search_results != 0 || thisMode == NEW_NEWMV)
                    {
                        args.WedgeIndex = bestMaskIndex;
                        args.WedgeSign = bestWedgeSign;
                    }
                }
                else
                {
                    mbmi.InterinterComp.WedgeIndex = (sbyte)args.WedgeIndex;
                    mbmi.InterinterComp.WedgeSign = (sbyte)args.WedgeSign;
                    rs2 = maskedTypeCost[curType];
                    rs2 += GetInterinterCompoundMaskRate(x.ModeCosts, mbmi);
                    if (wedgeNewmvSearch) tmpRateMv = InterinterCompoundMotionSearch(cpi, x, curMv, bsize, thisMode);
                    bestMaskIndex = args.WedgeIndex;
                    bestWedgeSign = args.WedgeSign;
                    tmpMv0 = mbmi.Mv0; tmpMv1 = mbmi.Mv1;
                    bestRateMv = tmpRateMv;
                    bestRs2 = maskedTypeCost[curType];
                    bestRs2 += GetInterinterCompoundMaskRate(x.ModeCosts, mbmi);
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                    if (PruneModeBySkipRd(cpi, x, bsize, refSkipRd, bestRs2 + rateMv))
                    {
                        AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, long.MaxValue, out var estRdStats);
                        bestRdCur = AomRd.RdCost(x.Rdmult, bestRs2 + tmpRateMv + estRdStats.Rate, estRdStats.Dist);
                    }
                }
                mbmi.InterinterComp.WedgeIndex = (sbyte)bestMaskIndex;
                mbmi.InterinterComp.WedgeSign = (sbyte)bestWedgeSign;
                mbmi.Mv0 = tmpMv0; mbmi.Mv1 = tmpMv1;
                tmpRateMv = bestRateMv;
                rs2 = bestRs2;
            }
            else if (sf.enable_fast_compound_mode_search == 0 && curType == COMPOUND_DIFFWTD)
            {
                AomMv tmpMv0 = default, tmpMv1 = default;
                int bestMaskIndex = 0;
                rs2 += GetInterinterCompoundMaskRate(x.ModeCosts, mbmi);
                bool needMaskSearch = args.DiffwtdIndex == -1;
                for (int maskIndex = 0; maskIndex < 2 && needMaskSearch; ++maskIndex)
                {
                    tmpRateMv = rateMv;
                    mbmi.InterinterComp.MaskType = maskIndex;
                    if (AomInter.HaveNewmvInInterMode(thisMode))
                    {
                        Array.Fill(xd.SegMask, (byte)(maskIndex == 0 ? 38 : 26));
                        tmpRateMv = InterinterCompoundMotionSearch(cpi, x, curMv, bsize, thisMode);
                    }
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                    long thisRdCur = long.MaxValue;
                    AomRdStats estRdStats = default;
                    if (PruneModeBySkipRd(cpi, x, bsize, refSkipRd, rs2 + rateMv))
                        thisRdCur = AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, refBestRd, out estRdStats);
                    if (thisRdCur < long.MaxValue) thisRdCur = AomRd.RdCost(x.Rdmult, rs2 + tmpRateMv + estRdStats.Rate, estRdStats.Dist);
                    if (thisRdCur < bestRdCur)
                    {
                        bestRdCur = thisRdCur;
                        bestMaskIndex = mbmi.InterinterComp.MaskType;
                        tmpMv0 = mbmi.Mv0; tmpMv1 = mbmi.Mv1;
                    }
                }
                if (needMaskSearch)
                {
                    if (thisMode == NEW_NEWMV) args.DiffwtdIndex = bestMaskIndex;
                }
                else
                {
                    mbmi.InterinterComp.MaskType = args.DiffwtdIndex;
                    rs2 = maskedTypeCost[curType];
                    rs2 += GetInterinterCompoundMaskRate(x.ModeCosts, mbmi);
                    Array.Fill(xd.SegMask, (byte)(mbmi.InterinterComp.MaskType == 0 ? 38 : 26));
                    if (AomInter.HaveNewmvInInterMode(thisMode)) tmpRateMv = InterinterCompoundMotionSearch(cpi, x, curMv, bsize, thisMode);
                    bestMaskIndex = mbmi.InterinterComp.MaskType;
                    tmpMv0 = mbmi.Mv0; tmpMv1 = mbmi.Mv1;
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                    long thisRdCur = long.MaxValue;
                    AomRdStats estRdStats = default;
                    if (PruneModeBySkipRd(cpi, x, bsize, refSkipRd, rs2 + rateMv))
                        thisRdCur = AomInterIntraSearch.EstimateYrdForSb(cpi, bsize, x, refBestRd, out estRdStats);
                    if (thisRdCur < long.MaxValue) bestRdCur = AomRd.RdCost(x.Rdmult, rs2 + tmpRateMv + estRdStats.Rate, estRdStats.Dist);
                }
                mbmi.InterinterComp.MaskType = bestMaskIndex;
                mbmi.Mv0 = tmpMv0; mbmi.Mv1 = tmpMv1;
            }
            else
            {
                bool evalMaskedCompType = true;
                if (rd != long.MaxValue)
                {
                    int mul = CompTypeRdThresholdMul[sf.prune_comp_type_by_comp_avg];
                    int div = CompTypeRdThresholdDiv[sf.prune_comp_type_by_comp_avg];
                    long approxRd = (rd / div) * mul;
                    if (approxRd >= refBestRd) evalMaskedCompType = false;
                }
                if (evalMaskedCompType)
                {
                    long tmpRdThresh = Math.Min(rd, rdThresh);
                    bestRdCur = MaskedCompoundTypeRd(cpi, x, curMv, bsize, thisMode, ref rs2, rateMv, origDst, out tmpRateMv, buffers, rdStats.Rate,
                        tmpRdThresh, ref calcPredMaskedCompound, compRate, compDist, compModelRate, compModelDist, compBestModelRd,
                        out compModelRdCur, compRs2, refSkipRd);
                }
            }
            if (bestRdCur < rd)
            {
                rd = bestRdCur;
                compBestModelRd = compModelRdCur;
                bestCompoundData = mbmi.InterinterComp;
                bestCompmodeInterinterCost = rs2;
                if (AomInter.HaveNewmvInInterMode(thisMode))
                {
                    bestTmpRateMv = tmpRateMv;
                    bestMv[0] = mbmi.Mv0;
                    bestMv[1] = mbmi.Mv1;
                }
            }
            if (curType == COMPOUND_AVERAGE)
            {
                int lvl = sf.skip_cmp_using_top_cmp_avg_est_rd_lvl;
                PushCompAvgEstRd(x.TopCompAvgEstRd, bestRdCur, lvl);
                if (PruneCompEvalUsingCompAvgEstRd(x.TopCompAvgEstRd, bestRdCur, refBestRd, lvl))
                {
                    rd = long.MaxValue;
                    AomInterpSearch.RestoreDstBuf(xd, origDst, 1);
                    return 0;
                }
            }
            mbmi.Mv0 = curMv[0];
            mbmi.Mv1 = curMv[1];
        }
        mbmi.CompGroupIdx = (byte)(bestCompoundData.Type < COMPOUND_WEDGE ? 0 : 1);
        mbmi.CompoundIdx = (byte)(bestCompoundData.Type == COMPOUND_DISTWTD ? 0 : 1);
        mbmi.InterinterComp = bestCompoundData;
        if (AomInter.HaveNewmvInInterMode(thisMode))
        {
            mbmi.Mv0 = bestMv[0];
            mbmi.Mv1 = bestMv[1];
            rdStats.Rate += bestTmpRateMv - rateMv;
            rateMv = bestTmpRateMv;
        }
        if (thisMode == NEW_NEWMV) args.CmpMode[refFrame] = mbmi.InterinterComp.Type;
        AomInterpSearch.RestoreDstBuf(xd, origDst, 1);
        if (!matchFound) SaveCompRdSearchStat(x, mbmi, compRate, compDist, compModelRate, compModelDist, curMv, compRs2);
        AomTrace.Out?.Write($"cte {xd.MiRow} {xd.MiCol} type {bestCompoundData.Type} rd {rd} cost {bestCompmodeInterinterCost} rate_mv {rateMv}" + (char)10);
        return bestCompmodeInterinterCost;
    }

    /// <summary>av1_interinter_compound_motion_search.</summary>
    private static int InterinterCompoundMotionSearch(AomComp cpi, AomMacroblock x, AomMv[] curMv, int bsize, int thisMode)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var tmpMv = new AomMv[2];
        int tmpRateMv = 0;
        mbmi.InterinterComp.SegMask = mbmi.InterinterComp.Type == COMPOUND_AVERAGE ? null : xd.SegMask;
        if (thisMode == NEW_NEWMV)
        {
            DoMaskedMotionSearchIndexed(cpi, x, curMv, bsize, tmpMv, ref tmpRateMv, 2);
            mbmi.Mv0 = tmpMv[0];
            mbmi.Mv1 = tmpMv[1];
        }
        else if (thisMode >= NEAREST_NEWMV && thisMode <= NEW_NEARMV)
        {
            int which = AomInter.CompoundRef1Mode(thisMode) == NEWMV ? 1 : 0;
            DoMaskedMotionSearchIndexed(cpi, x, curMv, bsize, tmpMv, ref tmpRateMv, which);
            if (which == 0) mbmi.Mv0 = tmpMv[0]; else mbmi.Mv1 = tmpMv[1];
        }
        return tmpRateMv;
    }

    /// <summary>do_masked_motion_search_indexed.</summary>
    private static void DoMaskedMotionSearchIndexed(AomComp cpi, AomMacroblock x, AomMv[] curMv, int bsize, AomMv[] tmpMv, ref int rateMv, int which)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int maskStride = BlockSizeWide[bsize];
        var mask = AomInterPred.GetCompoundTypeMask(mbmi.InterinterComp, mbmi.Bsize);
        tmpMv[0] = curMv[0];
        tmpMv[1] = curMv[1];
        if (which == 0 || which == 1)
        {
            // compound_single_motion_search_interinter
            var comp = BuildSecondInterPred(cpi, x, bsize, tmpMv[which == 0 ? 1 : 0], which);
            comp.Mask = mask; comp.MaskOffset = 0; comp.MaskStride = maskStride;
            AomMotionSearch.CompoundSingleMotionSearch(cpi, x, bsize, ref tmpMv[which], comp, out rateMv, which);
        }
        else if (which == 2)
        {
            int iters = cpi.Sf.inter_sf.enable_fast_compound_mode_search == 2 ? REDUCED_JOINT_ME_REFINE_ITER : NUM_JOINT_ME_REFINE_ITER;
            JointMotionSearch(cpi, x, bsize, tmpMv, mask, maskStride, out rateMv, cpi.Sf.mv_sf.disable_second_mv == 0, iters);
        }
    }

    /// <summary>build_second_inter_pred: the prediction of the 'other' reference (ref !refIdx) into a fresh compound-refs holder.</summary>
    private static AomCompoundRefs BuildSecondInterPred(AomComp cpi, AomMacroblock x, int bsize, AomMv otherMv, int refIdx)
    {
        var cm = cpi.Cm;
        int pw = BlockSizeWide[bsize], ph = BlockSizeHigh[bsize];
        var xd = x.E;
        var mbmi = xd.Mi0;
        var pd = xd.Plane[0];
        int pCol = (xd.MiCol * 4) >> pd.SubsamplingX, pRow = (xd.MiRow * 4) >> pd.SubsamplingY;
        var refYv12 = pd.Pre(refIdx == 0 ? 1 : 0);
        var sf = AomScaleFactors.ForFrame(refYv12.Width, refYv12.Height, cm.Width, cm.Height);
        var p = new AomInterPredParams();
        AomInterPred.InitInterParams(p, pw, ph, pRow, pCol, pd.SubsamplingX, pd.SubsamplingY, xd.Bd, xd.IsHbd, false, sf, refYv12, mbmi.InterpFilters);
        p.ConvParams = AomConvParams.Get(0, 0, xd.Bd);
        var comp = new AomCompoundRefs();
        if (xd.IsHbd) comp.SecondPred16 = new ushort[pw * ph]; else comp.SecondPred8 = new byte[pw * ph];
        AomInterPred.BuildOneInterPredictor(comp.SecondPred8, comp.SecondPred16, 0, pw, otherMv, p);
        return comp;
    }

    /// <summary>av1_joint_motion_search.</summary>
    internal static int JointMotionSearchPublic(AomComp cpi, AomMacroblock x, int bsize, AomMv[] curMv, byte[]? mask, int maskStride, out int rateMv,
        bool allowSecondMv, int jointMeNumRefineIter) => JointMotionSearch(cpi, x, bsize, curMv, mask, maskStride, out rateMv, allowSecondMv, jointMeNumRefineIter);

    private static int JointMotionSearch(AomComp cpi, AomMacroblock x, int bsize, AomMv[] curMv, byte[]? mask, int maskStride, out int rateMv,
        bool allowSecondMv, int jointMeNumRefineIter)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        int pw = BlockSizeWide[bsize], ph = BlockSizeHigh[bsize];
        var xd = x.E;
        var mbmi = xd.Mi0;
        var initMv0 = curMv[0]; var initMv1 = curMv[1];
        Span<int> refs = stackalloc int[2] { mbmi.RefFrame0, mbmi.RefFrame1 };
        var refMv = new AomMv[2];
        var backupYv12 = new AomBuf2d[2, 3];
        Span<int> lastBesterr = stackalloc int[2] { int.MaxValue, int.MaxValue };
        var scaledRef = new[] { cpi.GetScaledRefFrame(refs[0]), cpi.GetScaledRefFrame(refs[1]) };
        var comp = new AomCompoundRefs { Mask = mask, MaskOffset = 0, MaskStride = maskStride };
        if (xd.IsHbd) comp.SecondPred16 = new ushort[pw * ph]; else comp.SecondPred8 = new byte[pw * ph];
        AomMv bestMv = default, secondBestMv = AomMv.Invalid;
        var pd0 = xd.Plane[0];
        for (int ite = 0; ite < 2 * jointMeNumRefineIter; ite++)
        {
            var refYv12 = new AomBuf2d[2];
            int bestsme = int.MaxValue;
            int id = ite % 2;
            var init = id == 0 ? initMv0 : initMv1;
            var initOther = id == 0 ? initMv1 : initMv0;
            if (ite >= 2 && curMv[1 - id].AsInt == initOther.AsInt)
            {
                if (curMv[id].AsInt == init.AsInt) break;
                var ci = new AomMv(curMv[id].Row >> 3, curMv[id].Col >> 3);
                var ii = new AomMv(init.Row >> 3, init.Col >> 3);
                if (ci.AsInt == ii.AsInt) break;
            }
            for (int r = 0; r < 2; ++r)
            {
                refMv[r] = AomMotionSearch.GetRefMv(x, r);
                if (scaledRef[r] != null)
                {
                    for (int i = 0; i < numPlanes; i++) backupYv12[r, i] = xd.Plane[i].Pre(r);
                    AomInterPred.SetupPrePlanes(xd, r, scaledRef[r]!, xd.MiRow, xd.MiCol, null!, numPlanes);
                }
            }
            refYv12[0] = pd0.Pre(0);
            refYv12[1] = pd0.Pre(1);
            var p = new AomInterPredParams();
            AomInterPred.InitInterParams(p, pw, ph, xd.MiRow * 4, xd.MiCol * 4, 0, 0, xd.Bd, xd.IsHbd, false, AomInterPred.Identity, refYv12[1 - id], 0);
            p.ConvParams = AomConvParams.Get(0, 0, xd.Bd);
            AomInterPred.BuildOneInterPredictor(comp.SecondPred8, comp.SecondPred16, 0, pw, curMv[1 - id], p);
            if (id != 0) pd0.Pre(0) = refYv12[id];
            int searchMethod = AomMcomp.GetDefaultMvSearchMethod(x, cpi.Sf.mv_sf, bsize);
            var startFullmv = curMv[id].ToFullMv();
            var fp = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, refMv[id], cpi.SearchSites, searchMethod, false, x.MvLimits, startFullmv);
            comp.InvMask = id != 0;
            fp.Comp = comp;
            fp.HasSecondPred = true;
            AomMv bestFull;
            AomFullpelMvStats? bestMvStats = null;
            if (cpi.Sf.mv_sf.disable_extensive_joint_motion_search == 0 && mbmi.InterinterComp.Type != COMPOUND_WEDGE)
            {
                bestsme = AomMcomp.FullPixelSearch(startFullmv, fp, 5, null, out bestFull, out var stats, ref secondBestMv, true);
                bestMvStats = stats;
            }
            else
            {
                bestsme = AomMcomp.RefiningSearch8p(fp, startFullmv, out bestFull);
                secondBestMv = bestFull;
            }
            bool trySecond = secondBestMv.AsInt != AomMv.Invalid.AsInt && secondBestMv.AsInt != bestFull.AsInt && allowSecondMv;
            if (id != 0) pd0.Pre(0) = refYv12[0];
            for (int r = 0; r < 2; ++r)
                if (scaledRef[r] != null)
                {
                    for (int i = 0; i < numPlanes; i++) xd.Plane[i].Pre(r) = backupYv12[r, i];
                    refYv12[r] = pd0.Pre(r);
                }
            if (id != 0) pd0.Pre(0) = refYv12[id];
            bestMv = bestFull;
            if (cm.CurFrameForceIntegerMv) bestMv = bestFull.ToMv();
            if (bestsme < int.MaxValue && !cm.CurFrameForceIntegerMv)
            {
                var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv[id], null, cpi.SubpelParamsScratch(x));
                ms.Comp = comp;
                ms.HasSecondPred = true;
                ms.ForcedStop = AomSubpel.EIGHTH_PEL;
                var startMv = bestFull.ToMv();
                bestsme = AomSubpel.FindFractionalMvStep(cpi, ms, startMv, null, out bestMv, out _, out _, null);
                if (trySecond)
                {
                    var subpelStart = secondBestMv.ToMv();
                    if (AomSubpel.IsSubpelmvInRange(ms.MvLimits, subpelStart))
                    {
                        int thissme = AomSubpel.FindFractionalMvStep(cpi, ms, subpelStart, null, out var thisBest, out _, out _, null);
                        if (thissme < bestsme)
                        {
                            bestMv = thisBest;
                            bestsme = thissme;
                        }
                    }
                }
                ms.HasSecondPred = false;
                ms.Comp = null;
            }
            fp.Comp = null;
            if (id != 0) pd0.Pre(0) = refYv12[0];
            if (bestsme < lastBesterr[id])
            {
                curMv[id] = bestMv;
                lastBesterr[id] = bestsme;
            }
            else break;
        }
        rateMv = 0;
        for (int r = 0; r < 2; ++r) rateMv += AomMotionSearch.MvBitCost(x, curMv[r], AomMotionSearch.GetRefMv(x, r));
        return Math.Min(lastBesterr[0], lastBesterr[1]);
    }
}
