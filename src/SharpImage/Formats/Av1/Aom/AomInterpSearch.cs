using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    /// <summary>cm->seq_params (the good-quality encoder's sequence header; null for the all-intra still).</summary>
    public AomSeqHeader? Seq;
    /// <summary>cpi->interp_search_flags.</summary>
    public int DefaultInterpSkipFlags = INTERP_SKIP_LUMA_SKIP_CHROMA;
    public ushort InterpFilterSearchMask;
    /// <summary>cm->current_frame.frame_number.</summary>
    public int FrameNumber;
}

internal sealed partial class AomMacroblock
{
    public int RecalcLumaMcData;
}

// Port of libaom 3.14.1 av1/encoder/interp_search.c (av1_interpolation_filter_search and its helpers) and
// rdopt_utils.h's restore_dst_buf / swap_dst_buf.
internal static class AomInterpSearch
{
    private const int INTERP_EVAL_LUMA_EVAL_CHROMA = 0, INTERP_SKIP_LUMA_EVAL_CHROMA = 1, INTERP_EVAL_LUMA_SKIP_CHROMA = 2;
    private const int INTERP_HORZ_EQ_VERT_NEQ = 1, INTERP_HORZ_NEQ_VERT_EQ = 2;
    private const int INTERP_INVALID = 3;

    /// <summary>filter_sets[i]: x_filter = i % 3, y_filter = i / 3 (as_int: y in the low half).</summary>
    public static uint FilterSet(int i) => (uint)(i / 3) | ((uint)(i % 3) << 16);

    private static readonly ushort[,] InterpDualFiltMask =
    {
        { (1 << REG_REG) | (1 << SMOOTH_REG) | (1 << SHARP_REG), (1 << REG_SMOOTH) | (1 << SMOOTH_SMOOTH) | (1 << SHARP_SMOOTH),
          (1 << REG_SHARP) | (1 << SMOOTH_SHARP) | (1 << SHARP_SHARP) },
        { (1 << REG_REG) | (1 << REG_SMOOTH) | (1 << REG_SHARP), (1 << SMOOTH_REG) | (1 << SMOOTH_SMOOTH) | (1 << SMOOTH_SHARP),
          (1 << SHARP_REG) | (1 << SHARP_SMOOTH) | (1 << SHARP_SHARP) },
    };

    private static bool AllowedMask(int mask, int t) => ((mask >> t) & 1) != 0;

    /// <summary>restore_dst_buf.</summary>
    public static void RestoreDstBuf(AomMacroblockD xd, AomBufferSet dst, int numPlanes)
    {
        for (int i = 0; i < numPlanes; i++)
        {
            ref var d = ref xd.Plane[i].Dst;
            d.Buf = dst.Plane[i].Buf;
            d.Buf16 = dst.Plane[i].Buf16;
            d.Offset = dst.Plane[i].Offset;
            d.Stride = dst.Plane[i].Stride;
        }
    }

    /// <summary>swap_dst_buf.</summary>
    public static void SwapDstBuf(AomMacroblockD xd, AomBufferSet[] dstBufs, int numPlanes)
    {
        (dstBufs[0], dstBufs[1]) = (dstBufs[1], dstBufs[0]);
        RestoreDstBuf(xd, dstBufs[0], numPlanes);
    }

    /// <summary>av1_is_interp_needed.</summary>
    public static bool IsInterpNeeded(AomMacroblockD xd)
    {
        var mbmi = xd.Mi0;
        if (mbmi.SkipMode != 0) return false;
        if (mbmi.MotionMode == WARPED_CAUSAL) return false;
        if (AomInter.IsNontransGlobalMotion(xd, mbmi)) return false;
        return true;
    }

    /// <summary>av1_broadcast_interp_filter.</summary>
    public static uint Broadcast(int f) => (uint)f | ((uint)f << 16);

    /// <summary>av1_unswitchable_filter.</summary>
    public static int Unswitchable(int f) => f == SWITCHABLE ? EIGHTTAP_REGULAR : f;

    /// <summary>set_default_interp_filters.</summary>
    public static void SetDefaultInterpFilters(AomMbModeInfo mbmi, int frameInterpFilter)
        => mbmi.InterpFilters = Broadcast(Unswitchable(frameInterpFilter));

    private static int IsInterpFilterGoodMatch(AomInterpFilterStats st, AomMbModeInfo mi, int skipLevel)
    {
        bool isComp = mi.HasSecondRef;
        if (st.RefFrames[0] != mi.RefFrame0) return int.MaxValue;
        if (isComp && st.RefFrames[1] != mi.RefFrame1) return int.MaxValue;
        if (skipLevel == 1 && isComp)
        {
            if (st.CompType != mi.InterinterComp.Type) return int.MaxValue;
            if (st.CompGroupIdx != mi.CompoundIdx) return int.MaxValue;
        }
        int mvDiff = Math.Abs(st.Mv[0].Row - mi.Mv0.Row) + Math.Abs(st.Mv[0].Col - mi.Mv0.Col);
        if (isComp) mvDiff += Math.Abs(st.Mv[1].Row - mi.Mv1.Row) + Math.Abs(st.Mv[1].Col - mi.Mv1.Col);
        return mvDiff;
    }

    private static int SaveInterpFilterSearchStat(AomMbModeInfo mbmi, long rd, uint predSse, AomInterpFilterStats[] stats, int idx)
    {
        if (idx < MAX_INTERP_FILTER_STATS)
        {
            var st = stats[idx];
            st.Filters = mbmi.InterpFilters;
            st.Mv[0] = mbmi.Mv0; st.Mv[1] = mbmi.Mv1;
            st.RefFrames[0] = mbmi.RefFrame0; st.RefFrames[1] = mbmi.RefFrame1;
            st.CompType = mbmi.InterinterComp.Type;
            st.CompGroupIdx = mbmi.CompoundIdx;   // (the stat's compound_idx)
            st.Rd = rd;
            st.PredSse = predSse;
            idx++;
        }
        return idx;
    }

    private static int FindInterpFilterInStats(AomMbModeInfo mbmi, AomInterpFilterStats[] stats, int n, int skipLevel)
    {
        int[,] thr = { { 0, 0 }, { 3, 7 } };
        int isComp = mbmi.HasSecondRef ? 1 : 0;
        int best = int.MaxValue, match = -1;
        for (int j = 0; j < n; ++j)
        {
            int mvDiff = IsInterpFilterGoodMatch(stats[j], mbmi, skipLevel);
            if (mvDiff == 0) { match = j; break; }
            if (mvDiff < best && mvDiff <= thr[skipLevel - 1, isComp]) { best = mvDiff; match = j; }
        }
        if (match != -1)
        {
            mbmi.InterpFilters = stats[match].Filters;
            return match;
        }
        return -1;
    }

    private static int FindInterpFilterMatch(AomMbModeInfo mbmi, AomComp cpi, int assignFilter, bool needSearch, AomInterpFilterStats[] stats, int n)
    {
        int m = -1;
        if (cpi.Sf.interp_sf.use_interp_filter != 0 && needSearch) m = FindInterpFilterInStats(mbmi, stats, n, cpi.Sf.interp_sf.use_interp_filter);
        if (!needSearch || m == -1) SetDefaultInterpFilters(mbmi, assignFilter);
        return m;
    }

    private static int GetSwitchableRate(AomMacroblock x, uint filters, int[] ctx, bool dualFilter)
    {
        int f0 = (int)(filters & 0xffff);
        int cost = x.ModeCosts.SwitchableInterpCosts[ctx[0] * 3 + f0];
        if (dualFilter)
        {
            int f1 = (int)(filters >> 16);
            cost += x.ModeCosts.SwitchableInterpCosts[ctx[1] * 3 + f1];
        }
        return SWITCHABLE_INTERP_RATE_FACTOR * cost;
    }

    /// <summary>av1_get_switchable_rate (rd.c).</summary>
    public static int GetSwitchableRateFrame(AomMacroblock x, AomMacroblockD xd, int interpFilter, bool dualFilter)
    {
        if (interpFilter != SWITCHABLE) return 0;
        var mbmi = xd.Mi0;
        int cost = 0;
        for (int dir = 0; dir < 2; ++dir)
        {
            if (dir != 0 && !dualFilter) break;
            int ctx = AomPredCommon.SwitchableInterp(xd, dir);
            int filter = AomPredCommon.ExtractInterpFilter(mbmi.InterpFilters, dir);
            cost += x.ModeCosts.SwitchableInterpCosts[ctx * 3 + filter];
        }
        return SWITCHABLE_INTERP_RATE_FACTOR * cost;
    }

    /// <summary>interp_model_rd_eval.</summary>
    private static void InterpModelRdEval(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet origDst, int planeFrom, int planeTo,
        ref AomRdStats rdStats, bool isSkipBuildPred)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        AomRdStats tmp = default;
        tmp.Init();
        if (!isSkipBuildPred) AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, origDst, bsize, planeFrom, planeTo, cpi.EnableIntraEdgeFilter);
        AomModelRd.SbFn(cpi.Sf.rt_sf.use_simple_rd_model != 0 ? AomModelRd.MODELRD_LEGACY : AomModelRd.MODELRD_TYPE_INTERP_FILTER, cpi, bsize, x, xd,
            planeFrom, planeTo, out tmp.Rate, out tmp.Dist, out tmp.SkipTxfm, out tmp.Sse, null, null, null);
        rdStats.Merge(tmp);
    }

    /// <summary>interpolation_filter_rd.</summary>
    private static bool InterpolationFilterRd(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet origDst, ref long rd, ref AomRdStats rdStatsLuma,
        ref AomRdStats rdStats, ref int switchableRate, AomBufferSet[] dstBufs, int filterIdx, int[] switchableCtx, int skipPred)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        AomRdStats thisLuma = default;
        thisLuma.Init();
        AomRdStats thisRd = rdStatsLuma;
        uint lastBest = mbmi.InterpFilters;
        mbmi.InterpFilters = FilterSet(filterIdx);
        bool isSharp = mbmi.XFilter == MULTITAP_SHARP || mbmi.YFilter == MULTITAP_SHARP;
        int mul = isSharp && cpi.Sf.interp_sf.use_more_sharp_interp != 0 ? 90 : 100;
        int tmpRs = GetSwitchableRate(x, mbmi.InterpFilters, switchableCtx, cpi.Seq!.EnableDualFilter);
        long minRd = AomRd.RdCost(x.Rdmult, tmpRs, 0);
        if (minRd * mul / 100 > rd)
        {
            mbmi.InterpFilters = lastBest;
            return false;
        }
        int tmpSkipPred = skipPred == cpi.DefaultInterpSkipFlags ? INTERP_SKIP_LUMA_SKIP_CHROMA : skipPred;
        if (tmpSkipPred == INTERP_SKIP_LUMA_SKIP_CHROMA) thisRd = rdStats;
        else
        {
            if (tmpSkipPred == INTERP_EVAL_LUMA_EVAL_CHROMA || tmpSkipPred == INTERP_EVAL_LUMA_SKIP_CHROMA)
            {
                InterpModelRdEval(x, cpi, bsize, origDst, 0, 0, ref thisLuma, false);
                thisRd = thisLuma;
            }
            if (tmpSkipPred == INTERP_EVAL_LUMA_EVAL_CHROMA || tmpSkipPred == INTERP_SKIP_LUMA_EVAL_CHROMA)
            {
                for (int plane = 1; plane < numPlanes; ++plane)
                {
                    long tmpRd0 = AomRd.RdCost(x.Rdmult, tmpRs + thisRd.Rate, thisRd.Dist);
                    if (tmpRd0 * mul / 100 >= rd)
                    {
                        mbmi.InterpFilters = lastBest;
                        return false;
                    }
                    InterpModelRdEval(x, cpi, bsize, origDst, plane, plane, ref thisRd, false);
                }
            }
        }
        long tmpRd = AomRd.RdCost(x.Rdmult, tmpRs + thisRd.Rate, thisRd.Dist);
        if (tmpRd * mul / 100 < rd)
        {
            rd = tmpRd;
            switchableRate = tmpRs;
            if (skipPred != cpi.DefaultInterpSkipFlags)
            {
                if (skipPred == INTERP_EVAL_LUMA_EVAL_CHROMA)
                {
                    rdStatsLuma = thisLuma;
                    rdStats = thisRd;
                    x.RecalcLumaMcData = 0;
                }
                else if (skipPred == INTERP_SKIP_LUMA_EVAL_CHROMA)
                {
                    rdStats = thisRd;
                    x.RecalcLumaMcData ^= 1;
                }
                SwapDstBuf(xd, dstBufs, numPlanes);
            }
            return true;
        }
        mbmi.InterpFilters = lastBest;
        return false;
    }

    /// <summary>is_pred_filter_search_allowed.</summary>
    private static int IsPredFilterSearchAllowed(AomComp cpi, AomMacroblockD xd, int bsize, ref uint af, ref uint lf)
    {
        var a = xd.AboveMbmi; var l = xd.LeftMbmi;
        int bsl = MiSizeWideLog2[bsize];
        int isHorizEq = 0, isVertEq = 0;
        if (a != null && a.IsInterBlock) af = a.InterpFilters;
        if (l != null && l.IsInterBlock) lf = l.InterpFilters;
        if ((af >> 16) != INTERP_INVALID) isHorizEq = (af >> 16) == (lf >> 16) ? 1 : 0;
        if ((af & 0xffff) != INTERP_INVALID) isVertEq = (af & 0xffff) == (lf & 0xffff) ? 1 : 0;
        int predFilterType = (isVertEq << 1) + isHorizEq;
        int enable = cpi.Sf.interp_sf.cb_pred_filter_search != 0 ? (((xd.MiRow + xd.MiCol) >> bsl) + (cpi.FrameNumber & 1)) & 1 : 0;
        enable &= isHorizEq | isVertEq;
        return enable * predFilterType;
    }

    /// <summary>find_best_interp_rd_facade.</summary>
    private static int FindBestInterpRdFacade(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet origDst, ref long rd, ref AomRdStats rdStatsY,
        ref AomRdStats rdStats, ref int switchableRate, AomBufferSet[] dstBufs, int[] switchableCtx, int skipPred, int allowMask, bool isW4OrH4)
    {
        int best = REG_REG;
        if (allowMask == 0) return best;
        int tmpSkipPred = isW4OrH4 ? cpi.DefaultInterpSkipFlags : skipPred;
        for (int ft = SHARP_SHARP; ft >= REG_REG; --ft)
        {
            if (AllowedMask(allowMask, ft))
                if (InterpolationFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, ft, switchableCtx,
                        tmpSkipPred))
                    best = ft;
            tmpSkipPred = skipPred;
        }
        return best;
    }

    /// <summary>pred_dual_interp_filter_rd.</summary>
    private static void PredDualInterpFilterRd(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet origDst, ref long rd, ref AomRdStats rdStatsY,
        ref AomRdStats rdStats, ref int switchableRate, AomBufferSet[] dstBufs, int[] switchableCtx, int skipPred, int predFiltType, uint af)
    {
        int mask = 0;
        if (predFiltType == INTERP_HORZ_EQ_VERT_NEQ) mask = InterpDualFiltMask[predFiltType - 1, af >> 16];
        else if (predFiltType == INTERP_HORZ_NEQ_VERT_EQ) mask = InterpDualFiltMask[predFiltType - 1, af & 0xffff];
        else mask |= 1 << (int)((af >> 16) + (af & 0xffff) * SWITCHABLE_FILTERS);
        mask &= ~(1 << REG_REG) & ALLOW_ALL_INTERP_FILT_MASK;
        FindBestInterpRdFacade(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, switchableCtx, skipPred, mask, false);
    }

    /// <summary>fast_dual_interp_filter_rd.</summary>
    private static void FastDualInterpFilterRd(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet origDst, ref long rd, ref AomRdStats rdStatsY,
        ref AomRdStats rdStats, ref int switchableRate, AomBufferSet[] dstBufs, int[] switchableCtx, int skipHor, int skipVer)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int predFilterType = INTERP_HORZ_NEQ_VERT_NEQ;
        uint af = Broadcast(INTERP_INVALID), lf = af;
        if (!AomInter.HaveNewmvInInterMode(mbmi.Mode)) predFilterType = IsPredFilterSearchAllowed(cpi, xd, bsize, ref af, ref lf);
        if (predFilterType != 0)
            PredDualInterpFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, switchableCtx,
                skipHor & skipVer, predFilterType, af);
        else
        {
            int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
            int bestDualMode = 0;
            int skipPred = bw <= 4 ? cpi.DefaultInterpSkipFlags : skipHor;
            for (int i = SWITCHABLE_FILTERS - 1; i >= 1; --i)
            {
                if (InterpolationFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, i, switchableCtx, skipPred))
                    bestDualMode = i;
                skipPred = skipHor;
            }
            skipPred = bh <= 4 ? cpi.DefaultInterpSkipFlags : skipVer;
            for (int i = bestDualMode + SWITCHABLE_FILTERS * 2; i >= bestDualMode + SWITCHABLE_FILTERS; i -= SWITCHABLE_FILTERS)
            {
                InterpolationFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, i, switchableCtx, skipPred);
                skipPred = skipVer;
            }
        }
    }

    /// <summary>find_best_non_dual_interp_filter.</summary>
    private static void FindBestNonDualInterpFilter(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet origDst, ref long rd,
        ref AomRdStats rdStatsY, ref AomRdStats rdStats, ref int switchableRate, AomBufferSet[] dstBufs, int[] switchableCtx, int skipVer, int skipHor)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int mask = cpi.InterpFilterSearchMask;
        if (cpi.Sf.interp_sf.adaptive_interp_filter_search == 2)
        {
            int ctx0 = AomPredCommon.SwitchableInterp(xd, 0), ctx1 = AomPredCommon.SwitchableInterp(xd, 1);
            int[] thr = { 0, 8, 8, 8, 8, 0, 8 };
            int thresh = thr[cpi.UpdateType];
            for (int i = 0; i < SWITCHABLE_FILTERS; i++)
            {
                if (cpi.SwitchableInterpProb(cpi.UpdateType, ctx0, i) < thresh && cpi.SwitchableInterpProb(cpi.UpdateType, ctx1, i) < thresh)
                    mask &= ~(1 << (i + SWITCHABLE_FILTERS * i)) & ALLOW_ALL_INTERP_FILT_MASK;
                if (cpi.Sharpness == 3 && i == EIGHTTAP_SMOOTH) mask &= ~(1 << (i + SWITCHABLE_FILTERS * i)) & ALLOW_ALL_INTERP_FILT_MASK;
            }
        }
        if ((skipHor & skipVer) != cpi.DefaultInterpSkipFlags)
        {
            uint af = Broadcast(INTERP_INVALID), lf = af;
            int predFilterType = IsPredFilterSearchAllowed(cpi, xd, bsize, ref af, ref lf);
            if (predFilterType != 0)
            {
                int filterIdx = SWITCHABLE * (int)(af >> 16);
                if (cpi.Sf.interp_sf.adaptive_interp_filter_search != 0 && !AllowedMask(mask, filterIdx)) return;
                if (filterIdx != 0)
                    InterpolationFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, filterIdx, switchableCtx,
                        skipHor & skipVer);
                return;
            }
        }
        if (bsize == BLOCK_4X4 || (BlockSizeWide[bsize] == 4 && skipVer == cpi.DefaultInterpSkipFlags) ||
            (BlockSizeHigh[bsize] == 4 && skipHor == cpi.DefaultInterpSkipFlags))
        {
            int skipPred = skipHor & skipVer;
            int allowed = (1 << SHARP_SHARP) | (1 << SMOOTH_SMOOTH);
            if (cpi.Sf.interp_sf.adaptive_interp_filter_search != 0) allowed &= mask;
            FindBestInterpRdFacade(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, switchableCtx, skipPred,
                allowed, true);
        }
        else
        {
            int skipPred = skipHor & skipVer;
            for (int i = SWITCHABLE_FILTERS + 1; i < DUAL_FILTER_SET_SIZE; i += SWITCHABLE_FILTERS + 1)
            {
                if (cpi.Sf.interp_sf.adaptive_interp_filter_search != 0 && !AllowedMask(mask, i)) continue;
                InterpolationFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsY, ref rdStats, ref switchableRate, dstBufs, i, switchableCtx, skipPred);
                if (cpi.Sf.interp_sf.skip_sharp_interp_filter_search != 0 && skipPred != cpi.DefaultInterpSkipFlags)
                    if (mbmi.InterpFilters == FilterSet(SMOOTH_SMOOTH)) break;
            }
        }
    }

    /// <summary>calc_interp_skip_pred_flag.</summary>
    private static void CalcInterpSkipPredFlag(AomMacroblock x, AomComp cpi, ref int skipHor, ref int skipVer)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int numPlanes = cm.NumPlanes;
        bool isCompound = mbmi.HasSecondRef;
        for (int r = 0; r < 1 + (isCompound ? 1 : 0); ++r)
        {
            var sf = cm.RefScaleFactors[r == 0 ? mbmi.RefFrame0 : mbmi.RefFrame1]!;
            if (sf.IsScaled) { skipHor = 0; skipVer = 0; break; }
            var mv = r == 0 ? mbmi.Mv0 : mbmi.Mv1;
            int skipHorPlane = 0, skipVerPlane = 0;
            for (int planeIdx = 0; planeIdx < Math.Max(1, numPlanes - 1); ++planeIdx)
            {
                var pd = xd.Plane[planeIdx];
                var mvQ4 = AomInterPred.ClampMvToUmvBorderSb(xd, mv, pd.Width, pd.Height, pd.SubsamplingX, pd.SubsamplingY);
                int subX = (mvQ4.Col & 15) << 6, subY = (mvQ4.Row & 15) << 6;
                skipHorPlane |= (subX == 0 ? 1 : 0) << planeIdx;
                skipVerPlane |= (subY == 0 ? 1 : 0) << planeIdx;
            }
            skipHor &= skipHorPlane;
            skipVer &= skipVerPlane;
        }
        if (isCompound && mbmi.CompoundIdx == 1 && mbmi.InterinterComp.Type == COMPOUND_DIFFWTD)
            if (skipHor == 0 && skipVer == 1) skipVer = 0;
        if (cpi.Sf.interp_sf.skip_model_rd_uv != 0)
        {
            skipHor |= INTERP_EVAL_LUMA_SKIP_CHROMA;
            skipVer |= INTERP_EVAL_LUMA_SKIP_CHROMA;
        }
    }

    /// <summary>av1_interpolation_filter_search.</summary>
    public static long InterpolationFilterSearch(AomMacroblock x, AomComp cpi, int bsize, AomBufferSet tmpDst, AomBufferSet origDst, ref long rd,
        ref int switchableRate, ref int skipBuildPred, AomHandleInterModeArgs args, long refBestRd)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool needSearch = IsInterpNeeded(xd);
        int refFrame = mbmi.RefFrame0;
        bool skipModelRdUv = cpi.Sf.interp_sf.skip_model_rd_uv != 0;
        AomRdStats rdStatsLuma = default, rdStats = default;
        rdStatsLuma.Init();
        rdStats.Init();
        int assignFilter = cm.InterpFilter;
        int matchFoundIdx = FindInterpFilterMatch(mbmi, cpi, assignFilter, needSearch, args.InterpFilterStats, args.InterpFilterStatsIdx);
        AomTrace.Out?.Write($"ifm {xd.MiRow} {xd.MiCol} bs {bsize} m {mbmi.Mode} r {mbmi.RefFrame0} {mbmi.RefFrame1} idx {matchFoundIdx} n {args.InterpFilterStatsIdx}" + (char)10);
        if (matchFoundIdx != -1)
        {
            rd = args.InterpFilterStats[matchFoundIdx].Rd;
            x.PredSse[refFrame] = args.InterpFilterStats[matchFoundIdx].PredSse;
            skipBuildPred = INTERP_EVAL_LUMA_EVAL_CHROMA;
            return 0;
        }
        int[] switchableCtx = { AomPredCommon.SwitchableInterp(xd, 0), AomPredCommon.SwitchableInterp(xd, 1) };
        switchableRate = GetSwitchableRate(x, mbmi.InterpFilters, switchableCtx, cpi.Seq!.EnableDualFilter);
        InterpModelRdEval(x, cpi, bsize, origDst, 0, 0, ref rdStatsLuma, skipBuildPred != 0);
        if (numPlanes > 1 && !skipModelRdUv) InterpModelRdEval(x, cpi, bsize, origDst, 1, 2, ref rdStats, skipBuildPred != 0);
        skipBuildPred = numPlanes > 1 && skipModelRdUv ? INTERP_SKIP_LUMA_EVAL_CHROMA : INTERP_SKIP_LUMA_SKIP_CHROMA;
        rdStats.Merge(rdStatsLuma);
        rd = AomRd.RdCost(x.Rdmult, switchableRate + rdStats.Rate, rdStats.Dist);
        x.PredSse[refFrame] = (uint)(rdStatsLuma.Sse >> 4);
        AomTrace.Out?.Write($"ifd rd {rd} sw {switchableRate} y {rdStatsLuma.Rate} {rdStatsLuma.Dist} {rdStatsLuma.Sse} uv {rdStats.Rate} {rdStats.Dist}" + (char)10);
        if (assignFilter != SWITCHABLE || matchFoundIdx != -1) return 0;
        if (!needSearch) return 0;
        if (mbmi.HasSecondRef)
        {
            int refMvIdx = mbmi.RefMvIdx;
            int mode0 = AomInter.CompoundRef0Mode(mbmi.Mode), mode1 = AomInter.CompoundRef1Mode(mbmi.Mode);
            long mrd = Math.Min(args.State.ModelledRd[mode0, refMvIdx, mbmi.RefFrame0], args.State.ModelledRd[mode1, refMvIdx, mbmi.RefFrame1]);
            if ((rd >> 1) > mrd && refBestRd < long.MaxValue) return long.MaxValue;
        }
        x.RecalcLumaMcData = 0;
        int skipHor = cpi.DefaultInterpSkipFlags, skipVer = cpi.DefaultInterpSkipFlags;
        CalcInterpSkipPredFlag(x, cpi, ref skipHor, ref skipVer);
        RestoreDstBuf(xd, tmpDst, numPlanes);
        var dstBufs = new[] { tmpDst, origDst };
        if (cpi.Seq.EnableDualFilter)
        {
            if (cpi.Sf.interp_sf.use_fast_interpolation_filter_search != 0)
                FastDualInterpFilterRd(x, cpi, bsize, origDst, ref rd, ref rdStatsLuma, ref rdStats, ref switchableRate, dstBufs, switchableCtx, skipHor, skipVer);
            else
            {
                int allowed = ALLOW_ALL_INTERP_FILT_MASK & ~(1 << REG_REG);
                FindBestInterpRdFacade(x, cpi, bsize, origDst, ref rd, ref rdStatsLuma, ref rdStats, ref switchableRate, dstBufs, switchableCtx,
                    skipHor & skipVer, allowed, false);
            }
        }
        else FindBestNonDualInterpFilter(x, cpi, bsize, origDst, ref rd, ref rdStatsLuma, ref rdStats, ref switchableRate, dstBufs, switchableCtx, skipVer, skipHor);
        SwapDstBuf(xd, dstBufs, numPlanes);
        if (x.RecalcLumaMcData == 1) AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
        x.PredSse[refFrame] = (uint)(rdStatsLuma.Sse >> 4);
        AomTrace.Out?.Write($"iff rd {rd} f {mbmi.InterpFilters:x}" + (char)10);
        if (cpi.Sf.interp_sf.use_interp_filter != 0)
            args.InterpFilterStatsIdx = SaveInterpFilterSearchStat(mbmi, rd, x.PredSse[refFrame], args.InterpFilterStats, args.InterpFilterStatsIdx);
        return 0;
    }
}
