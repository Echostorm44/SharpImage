using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 nonrd_pickmode.c / nonrd_opt.c / nonrd_opt.h for inter frames: av1_nonrd_pick_inter_mode_sb
// with its helpers (one spatial / temporal layer, no SVC, no segmentation / cyclic refresh, no temporal denoiser,
// content default, no one-pass lag).
internal static partial class AomNonrdPickMode
{
    private const int RTC_INTER_MODES = 4;
    private const int NUM_INTER_MODES = 12;
    private const int NUM_COMP_INTER_MODES_RT = 6;
    private const int FILTER_SEARCH_SIZE = 2;
    private const int MOTION_MODE_SEARCH_SIZE = 2;
    private const int EARLY_TERM_IDX_4 = 4;
    private const int RD_THRESH_MAX_FACT = 64, RD_THRESH_INC = 1;

    private static int InterOffset(int mode) => mode - NEARESTMV;

    private static readonly int[] InterModeList = { NEARESTMV, NEARMV, GLOBALMV, NEWMV };

    // mode_idx[REF_FRAMES][RTC_MODES]
    private static readonly int[,] ModeIdx =
    {
        { THR_DC, THR_V_PRED, THR_H_PRED, THR_SMOOTH },
        { THR_NEARESTMV, THR_NEARMV, THR_GLOBALMV, THR_NEWMV },
        { THR_NEARESTL2, THR_NEARL2, THR_GLOBALL2, THR_NEWL2 },
        { THR_NEARESTL3, THR_NEARL3, THR_GLOBALL3, THR_NEWL3 },
        { THR_NEARESTG, THR_NEARG, THR_GLOBALG, THR_NEWG },
        { THR_NEARESTB, THR_NEARB, THR_GLOBALB, THR_NEWB },
        { THR_NEARESTA2, THR_NEARA2, THR_GLOBALA2, THR_NEWA2 },
        { THR_NEARESTA, THR_NEARA, THR_GLOBALA, THR_NEWA },
    };

    /// <summary>mode_offset: the RTC mode slot of an intra / inter mode.</summary>
    private static int ModeOffset(int mode)
    {
        if (mode >= NEARESTMV) return InterOffset(mode);
        return mode switch { DC_PRED => 0, V_PRED => 1, H_PRED => 2, SMOOTH_PRED => 3, _ => 0 };
    }

    private static readonly (int Ref, int Mode)[] RefModeSet =
    {
        (LAST_FRAME, NEARESTMV), (LAST_FRAME, NEARMV), (LAST_FRAME, GLOBALMV), (LAST_FRAME, NEWMV),
        (GOLDEN_FRAME, NEARESTMV), (GOLDEN_FRAME, NEARMV), (GOLDEN_FRAME, GLOBALMV), (GOLDEN_FRAME, NEWMV),
        (ALTREF_FRAME, NEARESTMV), (ALTREF_FRAME, NEARMV), (ALTREF_FRAME, GLOBALMV), (ALTREF_FRAME, NEWMV),
    };

    private static readonly (int Ref0, int Ref1, int Mode)[] CompRefModeSet =
    {
        (LAST_FRAME, GOLDEN_FRAME, GLOBAL_GLOBALMV), (LAST_FRAME, GOLDEN_FRAME, NEAREST_NEARESTMV),
        (LAST_FRAME, LAST2_FRAME, GLOBAL_GLOBALMV), (LAST_FRAME, LAST2_FRAME, NEAREST_NEARESTMV),
        (LAST_FRAME, ALTREF_FRAME, GLOBAL_GLOBALMV), (LAST_FRAME, ALTREF_FRAME, NEAREST_NEARESTMV),
    };

    // filters_ref_set: { y_filter, x_filter } packed as y | x << 16
    private static readonly uint[] FiltersRefSet =
    {
        (uint)EIGHTTAP_REGULAR | ((uint)EIGHTTAP_REGULAR << 16), (uint)EIGHTTAP_SMOOTH | ((uint)EIGHTTAP_SMOOTH << 16),
        (uint)EIGHTTAP_REGULAR | ((uint)EIGHTTAP_SMOOTH << 16), (uint)EIGHTTAP_SMOOTH | ((uint)EIGHTTAP_REGULAR << 16),
        (uint)MULTITAP_SHARP | ((uint)MULTITAP_SHARP << 16), (uint)EIGHTTAP_REGULAR | ((uint)MULTITAP_SHARP << 16),
        (uint)MULTITAP_SHARP | ((uint)EIGHTTAP_REGULAR << 16), (uint)EIGHTTAP_SMOOTH | ((uint)MULTITAP_SHARP << 16),
        (uint)MULTITAP_SHARP | ((uint)EIGHTTAP_SMOOTH << 16),
    };

    /// <summary>PRED_BUFFER: an 8-bit prediction buffer (data / stride / in_use); [3] is the block's destination.</summary>
    private sealed class PredBuffer
    {
        public byte[] Data = Array.Empty<byte>();
        public int Offset, Stride;
        public bool InUse;
    }

    /// <summary>BEST_PICKMODE.</summary>
    private sealed class BestPickmode
    {
        public PredBuffer? BestPred;
        public int BestMode, BestTxSize, TxType, BestRefFrame, BestSecondRefFrame;
        public int BestModeSkipTxfm, BestModeInitialSkipFlag;
        public uint BestPredFilter;
        public int BestMotionMode;
        public readonly AomWarpedMotionParams WmParams = new();
        public int NumProjRef;
        public AomPaletteModeInfo Pmi = new() { PaletteColors = new ushort[3 * AomPaletteModeInfo.PaletteMaxSize] };
        public long BestSse;
    }

    /// <summary>InterModeSearchStateNonrd.</summary>
    private sealed class SearchState
    {
        public readonly BestPickmode BestPickmode = new();
        public AomRdStats ThisRdc, BestRdc;
        public readonly long[,] UvDist = new long[RTC_INTER_MODES, REF_FRAMES];
        public readonly AomBuf2d[,] Yv12Mb = new AomBuf2d[REF_FRAMES, 3];
        public readonly uint[,] Vars = new uint[RTC_INTER_MODES, REF_FRAMES];
        public readonly uint[] RefCostsSingle = new uint[REF_FRAMES];
        public readonly AomMv[,] FrameMv = new AomMv[MB_MODE_COUNT, REF_FRAMES];
        public readonly AomMv[,] FrameMvBest = new AomMv[MB_MODE_COUNT, REF_FRAMES];
        public readonly int[,] SingleInterModeCosts = new int[RTC_INTER_MODES, REF_FRAMES];
        public readonly int[] UseRefFrameMask = new int[REF_FRAMES];
        public readonly byte[,] ModeChecked = new byte[MB_MODE_COUNT, REF_FRAMES];
        public readonly bool[] UseScaledRefFrame = new bool[REF_FRAMES];
    }

    // ---------------------------------------------------------------- small helpers

    /// <summary>early_term_inter_search_with_sse.</summary>
    private static bool EarlyTermInterSearchWithSse(int earlyTermIdx, int bsize, long thisSse, long bestSse, int thisMode)
    {
        double[,] t = { { 0.65, 0.65, 0.65, 0.7 }, { 0.6, 0.65, 0.85, 0.9 }, { 0.5, 0.5, 0.55, 0.6 }, { 0.6, 0.75, 0.85, 0.85 } };
        int sizeGroup = SizeGroupLookup[bsize];
        double threshold = earlyTermIdx == EARLY_TERM_IDX_4 && (thisMode == NEWMV || thisMode == NEARESTMV) ? 0.3 : t[earlyTermIdx - 1, sizeGroup];
        return earlyTermIdx > 0 && threshold * thisSse > bestSse;
    }

    private static readonly AomWarpedMotionParams ZeroWm = MakeZeroWm();

    private static AomWarpedMotionParams MakeZeroWm()
    {
        var w = new AomWarpedMotionParams();
        Array.Clear(w.WmMat);
        w.WmType = 0;
        return w;
    }

    private static void InitBestPickmode(BestPickmode bp)
    {
        bp.BestSse = long.MaxValue;
        bp.BestMode = NEARESTMV;
        bp.BestRefFrame = LAST_FRAME;
        bp.BestSecondRefFrame = NONE_FRAME;
        bp.BestTxSize = TX_8X8;
        bp.TxType = DCT_DCT;
        bp.BestPredFilter = AomInterpSearch.Broadcast(EIGHTTAP_REGULAR);
        bp.BestModeSkipTxfm = 0;
        bp.BestModeInitialSkipFlag = 0;
        bp.BestPred = null;
        bp.BestMotionMode = SIMPLE_TRANSLATION;
        bp.NumProjRef = 0;
        bp.WmParams.CopyFrom(ZeroWm);   // av1_zero(bp->wm_params)
        bp.Pmi.PaletteSize0 = 0;
        bp.Pmi.PaletteSize1 = 0;
        Array.Clear(bp.Pmi.PaletteColors);
    }

    /// <summary>update_search_state_nonrd.</summary>
    private static void UpdateSearchStateNonrd(SearchState st, AomMbModeInfo mi, in AomRdStats nonskipRdc, int thisBestMode, long sseY)
    {
        var bp = st.BestPickmode;
        bp.BestSse = sseY;
        bp.BestMode = thisBestMode;
        bp.BestMotionMode = mi.MotionMode;
        bp.WmParams.CopyFrom(mi.WmParams);
        bp.NumProjRef = mi.NumProjRef;
        bp.BestPredFilter = mi.InterpFilters;
        bp.BestTxSize = mi.TxSize;
        bp.BestRefFrame = mi.RefFrame0;
        bp.BestSecondRefFrame = mi.RefFrame1;
        bp.BestModeSkipTxfm = st.ThisRdc.SkipTxfm;
        bp.BestModeInitialSkipFlag = nonskipRdc.Rate == int.MaxValue && st.ThisRdc.SkipTxfm != 0 ? 1 : 0;
    }

    private static int GetPredBuffer(PredBuffer[] p, int len)
    {
        for (int i = 0; i < len; i++)
            if (!p[i].InUse)
            {
                p[i].InUse = true;
                return i;
            }
        return -1;
    }

    private static void FreePredBuffer(PredBuffer? p)
    {
        if (p != null) p.InUse = false;
    }

    /// <summary>Points the luma destination at a prediction buffer.</summary>
    private static void SetDst(AomMbdPlane pd, PredBuffer b)
    {
        pd.Dst.Buf = b.Data;
        pd.Dst.Offset = b.Offset;
        pd.Dst.Stride = b.Stride;
    }

    private static void ConvolveCopy(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h)
    {
        for (int r = 0; r < h; r++) Array.Copy(src, srcOff + r * srcStride, dst, dstOff + r * dstStride, w);
    }

    /// <summary>av1_get_intra_cost_penalty.</summary>
    private static int GetIntraCostPenalty(int qindex, int qdelta, int bd)
    {
        int bdIdx = bd == 8 ? 0 : bd == 10 ? 1 : 2;
        int q = Av1Tables.DequantTable[bdIdx, Math.Clamp(qindex + qdelta, 0, 255), 0];
        return bd switch { 8 => 20 * q, 10 => 5 * q, _ => (5 * q + 2) >> 2 };
    }

    /// <summary>rd_less_than_thresh.</summary>
    private static bool RdLessThanThresh(long bestRd, long thresh, int threshFact)
        => bestRd < thresh * threshFact >> 5 || thresh == int.MaxValue;

    /// <summary>fn_ptr[bsize].vf: the variance (high bit depth rounded to 8 bits).</summary>
    private static uint Vf(in AomBuf2d a, in AomBuf2d b, int w, int h, int bd, out uint sse)
        => a.Buf16 != null ? AomHbd.Variance(a.Buf16, a.Offset, a.Stride, b.Buf16, b.Offset, b.Stride, 0, w, h, bd, out sse)
            : AomSad.Variance(a.Buf, a.Offset, a.Stride, b.Buf, b.Offset, b.Stride, w, h, out sse);

    // ---------------------------------------------------------------- motion search

    /// <summary>subpel_select.</summary>
    private static int SubpelSelect(AomComp cpi, AomMacroblock x, int bsize, AomMv mvFull, AomMv refMv, AomMv startMv, bool fullpelPerformedWell)
    {
        int frameLowmotion = cpi.Rt!.AvgFrameLowMotion;
        int reduce = cpi.Sf.rt_sf.reduce_mv_pel_precision_highmotion;
        if (reduce >= 3)
        {
            bool isLowResoln = cpi.Cm.Width * cpi.Cm.Height <= 320 * 240;
            int mvThresh = bsize > BLOCK_32X32 ? 2 : bsize > BLOCK_16X16 ? 4 : 6;
            if (frameLowmotion > 0 && frameLowmotion < 40) mvThresh = 12;
            mvThresh = isLowResoln ? mvThresh >> 1 : mvThresh;
            if (Math.Abs((int)mvFull.Row) >= mvThresh || Math.Abs((int)mvFull.Col) >= mvThresh) return HALF_PEL;
        }
        else if (reduce >= 1)
        {
            int[,] thVals = { { 4, 8, 10 }, { 4, 6, 8 } };
            int thIdx = reduce - 1;
            int mvThresh = frameLowmotion > 0 && frameLowmotion < 40 ? 12
                : bsize >= BLOCK_32X32 ? thVals[thIdx, 0] : bsize >= BLOCK_16X16 ? thVals[thIdx, 1] : thVals[thIdx, 2];
            if (Math.Abs((int)mvFull.Row) >= (mvThresh << 1) || Math.Abs((int)mvFull.Col) >= (mvThresh << 1)) return FULL_PEL;
            if (Math.Abs((int)mvFull.Row) >= mvThresh || Math.Abs((int)mvFull.Col) >= mvThresh) return HALF_PEL;
        }
        int lowcomplex = cpi.Sf.rt_sf.reduce_mv_pel_precision_lowcomplex;
        if (lowcomplex >= 2)
        {
            int qband = x.Qindex >> (QINDEX_BITS - 2);
            if (x.SourceSadNonrd <= AomRtSb.kVeryLowSad && bsize > BLOCK_16X16 && qband != 0)
            {
                if (x.SourceVariance < 500) return FULL_PEL;
                if (x.SourceVariance < 5000) return HALF_PEL;
            }
        }
        else if (lowcomplex >= 1)
        {
            if (fullpelPerformedWell && refMv.Row == 0 && refMv.Col == 0 && startMv.Row == 0 && startMv.Col == 0) return HALF_PEL;
        }
        return cpi.Sf.mv_sf.subpel_force_stop;
    }

    /// <summary>use_aggressive_subpel_search_method.</summary>
    private static bool UseAggressiveSubpelSearchMethod(AomMacroblock x, bool useAdaptiveSubpelSearch, bool fullpelPerformedWell)
    {
        if (!useAdaptiveSubpelSearch) return false;
        int qband = x.Qindex >> (QINDEX_BITS - 2);
        return qband > 0 && (fullpelPerformedWell || x.SourceSadNonrd <= AomRtSb.kLowSad || x.SourceVariance < 100);
    }

    /// <summary>combined_motion_search.</summary>
    private static bool CombinedMotionSearch(AomComp cpi, AomMacroblock x, int bsize, ref AomMv tmpMv, out int rateMv, long bestRdSofar,
        bool useBaseMv)
    {
        var xd = x.E;
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        var mi = xd.Mi0;
        int stepParam = sf.rt_sf.fullpel_search_step_param != 0 ? sf.rt_sf.fullpel_search_step_param : cpi.MvStepParam;
        int refFrame = mi.RefFrame0;
        var refMv = AomMotionSearch.GetRefMv(x, mi.RefMvIdx);
        var costList = new int[5];
        var startMv = refMv.ToFullMv();
        var centerMv = useBaseMv ? tmpMv : refMv;
        int searchMethod = AomMcomp.GetDefaultMvSearchMethod(x, sf.mv_sf, bsize);
        var p = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, centerMv, cpi.SearchSites, searchMethod, false, x.MvLimits, startMv);
        AomMv second = default;
        int fullVarRd = AomMcomp.FullPixelSearch(startMv, p, stepParam, AomSubpel.CondCostList(cpi, costList), out var bestFull,
            out var bestMvStats, ref second, false);
        tmpMv = bestFull;
        var mvpFull = bestFull.ToMv();
        rateMv = AomMotionSearch.MvBitCost(x, mvpFull, refMv);
        bool rv = !(AomRd.RdCost(x.Rdmult, rateMv, 0) > bestRdSofar);
        if (rv)
        {
            var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, costList, cpi.SubpelParamsScratch(x));
            uint fv = (uint)fullVarRd;
            bool fullpelPerformedWell = (bsize == BLOCK_64X64 && fv * 40 < 62267 * 7) || (bsize == BLOCK_32X32 && fv * 8 < 42380) ||
                                        (bsize == BLOCK_16X16 && fv * 8 < 10127);
            if (sf.rt_sf.reduce_mv_pel_precision_highmotion != 0 || sf.rt_sf.reduce_mv_pel_precision_lowcomplex != 0)
                ms.ForcedStop = SubpelSelect(cpi, x, bsize, bestFull, refMv, startMv, fullpelPerformedWell);
            var subpelStartMv = bestFull.ToMv();
            AomMv best;
            uint psse;
            if (UseAggressiveSubpelSearchMethod(x, sf.rt_sf.use_adaptive_subpel_search, fullpelPerformedWell))
                AomSubpel.FindBestSubPixelTreePrunedMore(ms, subpelStartMv, bestMvStats, out best, out _, out psse, null);
            else AomSubpel.FindFractionalMvStep(cpi, ms, subpelStartMv, bestMvStats, out best, out _, out psse, null);
            x.PredSse[refFrame] = psse;
            tmpMv = best;
            rateMv = AomMotionSearch.MvBitCost(x, tmpMv, refMv);
        }
        // (rate too high: tmp_mv keeps the full-pel numbers, compared below as a 1/8 mv like libaom's union)
        return tmpMv.Col != refMv.Col || tmpMv.Row != refMv.Row;
    }

    /// <summary>search_new_mv (the GOLDEN int-pro path needs CBR without one-pass lag).</summary>
    private static int SearchNewMv(AomComp cpi, AomMacroblock x, SearchState st, int refFrame, bool gfTemporalRef, int bsize, int miRow, int miCol,
        out int rateMv, in AomRdStats bestRdc)
    {
        var xd = x.E;
        var mi = xd.Mi0;
        rateMv = 0;
        if (refFrame > LAST_FRAME && (cpi.RefFrameFlags & AOM_LAST_FLAG) != 0 && gfTemporalRef)
        {
            if (bsize < BLOCK_16X16) return -1;
            int meCol = BlockSizeWide[bsize] >> 1, meRow = BlockSizeHigh[bsize] >> 1;
            var refMv = AomMotionSearch.GetRefMv(x, 0);
            uint tmpSad = AomVarBasedPart.IntProMotionEstimation(cpi, x, bsize, miRow, miCol, refMv, out _, meCol, meRow, false, false);
            if (tmpSad > (uint)x.PredMvSad[LAST_FRAME]) return -1;
            var bestMv = new AomMv(mi.Mv0.Row >> 3, mi.Mv0.Col >> 3);   // full-pel
            var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, null, cpi.SubpelParamsScratch(x));
            if (cpi.Sf.rt_sf.reduce_mv_pel_precision_highmotion != 0 || cpi.Sf.rt_sf.reduce_mv_pel_precision_lowcomplex != 0)
                ms.ForcedStop = SubpelSelect(cpi, x, bsize, bestMv, refMv, default, false);
            var startMv = bestMv.ToMv();
            AomSubpel.FindFractionalMvStep(cpi, ms, startMv, null, out var best, out _, out uint psse, null);
            x.PredSse[refFrame] = psse;
            st.FrameMv[NEWMV, refFrame] = best;
            if (best.Col == refMv.Col && best.Row == refMv.Row) return -1;
            rateMv = AomMotionSearch.MvBitCost(x, best, refMv);
        }
        else
        {
            var tmp = st.FrameMv[NEWMV, refFrame];
            bool ok = CombinedMotionSearch(cpi, x, bsize, ref tmp, out rateMv, bestRdc.Rdcost, false);
            st.FrameMv[NEWMV, refFrame] = tmp;
            if (!ok) return -1;
        }
        return 0;
    }

    /// <summary>estimate_single_ref_frame_costs (context 0 for the single reference costs).</summary>
    private static void EstimateSingleRefFrameCosts(AomCommon cm, AomMacroblockD xd, AomModeCosts mc, int bsize, uint[] refCostsSingle)
    {
        int intraInterCtx = AomPredCommon.IntraInter(xd);
        refCostsSingle[INTRA_FRAME] = (uint)mc.IntraInterCost[intraInterCtx * 2 + 0];
        uint baseCost = (uint)mc.IntraInterCost[intraInterCtx * 2 + 1];
        if (cm.ReferenceMode == REFERENCE_MODE_SELECT && AomInter.IsCompRefAllowed(bsize))
        {
            int compRefTypeCtx = AomPredCommon.CompReferenceType(xd);
            baseCost += (uint)mc.CompRefTypeCost[compRefTypeCtx * 2 + 1];
        }
        uint Src(int j, int b) => (uint)mc.SingleRefCost[(0 * 6 + j) * 2 + b];
        refCostsSingle[LAST_FRAME] = baseCost + Src(0, 0);
        refCostsSingle[GOLDEN_FRAME] = baseCost + Src(0, 1) + Src(1, 0);
        refCostsSingle[ALTREF_FRAME] = baseCost + Src(0, 1) + Src(2, 0);
    }

    // ---------------------------------------------------------------- transform size and luma models

    private const int TX_SIZE_FOR_BSIZE_GT32 = TX_16X16;

    private static bool CapTxSizeForBsizeGt32(int txModeSearchType, int bsize) => txModeSearchType != ONLY_4X4 && bsize > BLOCK_32X32;

    /// <summary>calculate_tx_size (no cyclic refresh).</summary>
    private static int CalculateTxSize(AomComp cpi, int bsize, AomMacroblock x, uint var, uint sse, ref int forceSkip)
    {
        var xd = x.E;
        int txSize;
        var tp = x.TxfmSearchParams;
        if (tp.TxModeSearchType == TX_MODE_SELECT)
        {
            int multiplier = 8;
            uint varThresh = 0;
            if (cpi.Sf.rt_sf.tx_size_level_based_on_qstep != 0)
            {
                int qband = x.Qindex >> (QINDEX_BITS - 2);
                ReadOnlySpan<int> mult = stackalloc int[] { 8, 7, 6, 5 };
                multiplier = mult[qband];
                int qstep = x.Plane[0].Dequant1 >> (xd.Bd - 5);
                uint qstepSq = (uint)(qstep * qstep);
                varThresh = qstepSq * 2;
                if (cpi.Sf.rt_sf.tx_size_level_based_on_qstep >= 2)
                {
                    if (sse < qstepSq && x.SourceVariance < qstepSq && x.ColorSensitivity[0] == 0 && x.ColorSensitivity[1] == 0) forceSkip = 1;
                }
            }
            if (sse > ((var * (uint)multiplier) >> 2) || var < varThresh)
                txSize = Math.Min(MaxTxsizeLookup[bsize], TxModeToBiggestTxSize[tp.TxModeSearchType]);
            else txSize = TX_8X8;
            if (txSize > TX_16X16) txSize = TX_16X16;
        }
        else txSize = Math.Min(MaxTxsizeLookup[bsize], TxModeToBiggestTxSize[tp.TxModeSearchType]);
        if (CapTxSizeForBsizeGt32(tp.TxModeSearchType, bsize)) txSize = TX_SIZE_FOR_BSIZE_GT32;
        return Math.Min(txSize, TX_16X16);
    }

    /// <summary>block_variance (aom_get_var_sse_sum_8x8_quad: per-8x8 sse / sum / var).</summary>
    private static void BlockVariance(in AomBuf2d src, in AomBuf2d r, int w, int h, out uint sse, out int sum, uint[] sse8x8, int[] sum8x8,
        uint[] var8x8)
    {
        sse = 0;
        sum = 0;
        int k = 0;
        for (int row = 0; row < h; row += 8)
            for (int col = 0; col < w; col += 8)
            {
                int s = 0;
                uint ss = 0;
                for (int i = 0; i < 8; i++)
                {
                    int so = src.Offset + (row + i) * src.Stride + col, ro = r.Offset + (row + i) * r.Stride + col;
                    for (int j = 0; j < 8; j++)
                    {
                        int d = src.Buf[so + j] - r.Buf[ro + j];
                        s += d;
                        ss += (uint)(d * d);
                    }
                }
                sse8x8[k] = ss;
                sum8x8[k] = s;
                var8x8[k] = ss - (uint)(((long)s * s) >> 6);
                sse += ss;
                sum += s;
                k++;
            }
        // (the quad kernel walks 32-wide column groups; the index order matches for w >= 32: rows of 8x8 blocks)
    }

    /// <summary>block_variance_16x16_dual.</summary>
    private static void BlockVariance16x16Dual(in AomBuf2d src, in AomBuf2d r, int w, int h, out uint sse, out int sum, uint[] sse16, uint[] var16)
    {
        sse = 0;
        sum = 0;
        int k = 0;
        for (int row = 0; row < h; row += 16)
            for (int col = 0; col < w; col += 16)
            {
                int s = 0;
                uint ss = 0;
                for (int i = 0; i < 16; i++)
                {
                    int so = src.Offset + (row + i) * src.Stride + col, ro = r.Offset + (row + i) * r.Stride + col;
                    for (int j = 0; j < 16; j++)
                    {
                        int d = src.Buf[so + j] - r.Buf[ro + j];
                        s += d;
                        ss += (uint)(d * d);
                    }
                }
                sse16[k] = ss;
                var16[k] = ss - (uint)(((long)s * s) >> 8);
                sse += ss;
                sum += s;
                k++;
            }
    }

    /// <summary>calculate_variance (8x8 statistics merged to 16x16).</summary>
    private static void CalculateVariance(int bw, int bh, int txSize, uint[] sseI, int[] sumI, uint[] varO, uint[] sseO, int[] sumO)
    {
        int unitSize = TxsizeToBsize[txSize];
        int nw = 1 << (bw - BWidthLog2Lookup[unitSize]), nh = 1 << (bh - BHeightLog2Lookup[unitSize]);
        int k = 0;
        for (int row = 0; row < nh; row += 2)
            for (int col = 0; col < nw; col += 2)
            {
                sseO[k] = sseI[row * nw + col] + sseI[row * nw + col + 1] + sseI[(row + 1) * nw + col] + sseI[(row + 1) * nw + col + 1];
                sumO[k] = sumI[row * nw + col] + sumI[row * nw + col + 1] + sumI[(row + 1) * nw + col] + sumI[(row + 1) * nw + col + 1];
                varO[k] = sseO[k] - (uint)(((long)sumO[k] * sumO[k]) >> (BWidthLog2Lookup[unitSize] + BHeightLog2Lookup[unitSize] + 6));
                k++;
            }
    }

    private static int AcThrFactor(int speed, int width, int height, int normSum)
    {
        if (speed >= 8 && normSum < 5) return width <= 640 && height <= 480 ? 4 : 2;
        return 1;
    }

    /// <summary>set_early_term_based_on_uv_plane.</summary>
    private static void SetEarlyTermBasedOnUvPlane(AomComp cpi, AomMacroblock x, int bsize, AomMacroblockD xd, int miRow, int miCol, ref int earlyTerm,
        int numBlk, uint[] sseTx, uint[] varTx, int sum, uint var, uint sse)
    {
        var cm = cpi.Cm;
        var p = x.Plane[0];
        uint dcQuant = (uint)p.Dequant0, acQuant = (uint)p.Dequant1;
        long dcThr = dcQuant * dcQuant >> 6;
        long acThr = acQuant * acQuant >> 6;
        int bw = BWidthLog2Lookup[bsize], bh = BHeightLog2Lookup[bsize];
        bool acTest = true, dcTest = true;
        int normSum = Math.Abs(sum) >> (bw + bh);
        acThr *= AcThrFactor(cpi.Speed, cm.Width, cm.Height, normSum);
        if (cpi.Sf.rt_sf.increase_source_sad_thresh != 0)
        {
            dcThr <<= 1;
            acThr <<= 2;
        }
        if (cm.Width * cm.Height >= 1280 * 720 && x.SourceSadNonrd > AomRtSb.kLowSad && (sse >> (bw + bh)) > 1000)
        {
            dcThr >>= 4;
            acThr >>= 4;
        }
        for (int k = 0; k < numBlk; k++)
        {
            if (!(varTx[k] < acThr || var == 0)) { acTest = false; break; }
            if (!(sseTx[k] - varTx[k] < dcThr || sse == var)) { dcTest = false; break; }
        }
        if (acTest && dcTest)
        {
            Span<bool> skipUv = stackalloc bool[2];
            for (int plane = 1; plane <= 2; plane++)
            {
                int j = plane - 1;
                skipUv[j] = true;
                if (x.ColorSensitivity[plane - 1] != 0)
                {
                    skipUv[j] = false;
                    var puv = x.Plane[plane];
                    var puvd = xd.Plane[plane];
                    int uvBsize = AomEncodeMb.PlaneBlockSize(bsize, puvd.SubsamplingX, puvd.SubsamplingY);
                    int shiftAc = cpi.Sf.rt_sf.increase_source_sad_thresh != 0 ? 5 : 3;
                    int shiftDc = cpi.Sf.rt_sf.increase_source_sad_thresh != 0 ? 4 : 3;
                    long uvDcThr = (long)(puv.Dequant0 * puv.Dequant0) >> shiftDc;
                    long uvAcThr = (long)(puv.Dequant1 * puv.Dequant1) >> shiftAc;
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, plane, plane, cpi.EnableIntraEdgeFilter);
                    uint varUv = Vf(puv.Src, puvd.Dst, BlockSizeWide[uvBsize], BlockSizeHigh[uvBsize], xd.Bd, out uint sseUv);
                    if ((varUv < uvAcThr || varUv == 0) && (sseUv - varUv < uvDcThr || sseUv == varUv)) skipUv[j] = true;
                    else break;
                }
            }
            if (skipUv[0] && skipUv[1]) earlyTerm = 1;
        }
    }

    /// <summary>calc_rate_dist_block_param.</summary>
    private static void CalcRateDistBlockParam(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, bool calculateRd, ref int earlyTerm, int bsize,
        uint sse)
    {
        if (!calculateRd) return;
        if (earlyTerm == 0)
        {
            int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
            AomModelRd.WithCurvfit(cpi, x, bsize, 0, rdStats.Sse, bw * bh, out rdStats.Rate, out rdStats.Dist);
        }
        if (earlyTerm != 0)
        {
            rdStats.Rate = 0;
            rdStats.Dist = (long)sse << 4;
        }
    }

    /// <summary>model_skip_for_sb_y_large_64.</summary>
    private static void ModelSkipForSbYLarge64(AomComp cpi, int bsize, int miRow, int miCol, AomMacroblock x, AomMacroblockD xd, ref AomRdStats rdStats,
        ref int earlyTerm, bool calculateRd, long bestSse, ref uint varOutput, bool haveVarOutput, uint varPruneThreshold)
    {
        var p = x.Plane[0];
        var pd = xd.Plane[0];
        int bw = BWidthLog2Lookup[bsize], bh = BHeightLog2Lookup[bsize];
        var sse16 = new uint[64];
        var var16 = new uint[64];
        BlockVariance16x16Dual(p.Src, pd.Dst, 4 << bw, 4 << bh, out uint sse, out int sum, sse16, var16);
        uint var = sse - (uint)(((long)sum * sum) >> (bw + bh + 4));
        if (haveVarOutput)
        {
            varOutput = var;
            if (varOutput > varPruneThreshold) return;
        }
        rdStats.Sse = sse;
        earlyTerm = 0;
        SetForceSkipFlag(cpi, x, sse, ref earlyTerm);
        bool testSkip = true;
        var mi = xd.Mi0;
        if (!calculateRd && cpi.Sf.rt_sf.sse_early_term_inter_search != 0 &&
            EarlyTermInterSearchWithSse(cpi.Sf.rt_sf.sse_early_term_inter_search, bsize, sse, bestSse, mi.Mode))
            testSkip = false;
        if (earlyTerm != 0) testSkip = false;
        if (testSkip)
        {
            int numBlock = (1 << (bw + bh - 2)) >> 2;
            SetEarlyTermBasedOnUvPlane(cpi, x, bsize, xd, miRow, miCol, ref earlyTerm, numBlock, sse16, var16, sum, var, sse);
        }
        CalcRateDistBlockParam(cpi, x, ref rdStats, calculateRd, ref earlyTerm, bsize, sse);
    }

    /// <summary>set_force_skip_flag.</summary>
    private static void SetForceSkipFlag(AomComp cpi, AomMacroblock x, uint sse, ref int forceSkip)
    {
        if (x.TxfmSearchParams.TxModeSearchType == TX_MODE_SELECT && cpi.Sf.rt_sf.tx_size_level_based_on_qstep >= 2)
        {
            int qstep = x.Plane[0].Dequant1 >> (x.E.Bd - 5);
            uint qstepSq = (uint)(qstep * qstep);
            if (sse < qstepSq && x.SourceVariance < qstepSq && x.ColorSensitivity[0] == 0 && x.ColorSensitivity[1] == 0) forceSkip = 1;
        }
    }

    /// <summary>model_skip_for_sb_y_large.</summary>
    private static void ModelSkipForSbYLarge(AomComp cpi, int bsize, int miRow, int miCol, AomMacroblock x, AomMacroblockD xd, ref AomRdStats rdStats,
        ref int earlyTerm, bool calculateRd, long bestSse, ref uint varOutput, bool haveVarOutput, uint varPruneThreshold)
    {
        if (x.ForceZeromvSkipForBlk != 0)
        {
            earlyTerm = 1;
            rdStats.Rate = 0;
            rdStats.Dist = 0;
            rdStats.Sse = 0;
            return;
        }
        var tp = x.TxfmSearchParams;
        if (CapTxSizeForBsizeGt32(tp.TxModeSearchType, bsize))
        {
            xd.Mi0.TxSize = TX_SIZE_FOR_BSIZE_GT32;
            ModelSkipForSbYLarge64(cpi, bsize, miRow, miCol, x, xd, ref rdStats, ref earlyTerm, calculateRd, bestSse, ref varOutput, haveVarOutput,
                varPruneThreshold);
            return;
        }
        var p = x.Plane[0];
        var pd = xd.Plane[0];
        int bw = BWidthLog2Lookup[bsize], bh = BHeightLog2Lookup[bsize];
        var sse8 = new uint[256];
        var sum8 = new int[256];
        var var8 = new uint[256];
        BlockVariance(p.Src, pd.Dst, 4 << bw, 4 << bh, out uint sse, out int sum, sse8, sum8, var8);
        uint var = sse - (uint)(((long)sum * sum) >> (bw + bh + 4));
        if (haveVarOutput)
        {
            varOutput = var;
            if (varOutput > varPruneThreshold) return;
        }
        rdStats.Sse = sse;
        earlyTerm = 0;
        int txSize = CalculateTxSize(cpi, bsize, x, var, sse, ref earlyTerm);
        if (txSize < TX_8X8) txSize = TX_8X8;
        xd.Mi0.TxSize = txSize;
        var mi = xd.Mi0;
        bool testSkip = true;
        if (!calculateRd && cpi.Sf.rt_sf.sse_early_term_inter_search != 0 &&
            EarlyTermInterSearchWithSse(cpi.Sf.rt_sf.sse_early_term_inter_search, bsize, sse, bestSse, mi.Mode))
            testSkip = false;
        if (earlyTerm != 0) testSkip = false;
        if (testSkip)
        {
            uint[] sseTx = sse8, varTx = var8;
            int numBlks = 1 << (bw + bh - 2);
            if (txSize >= TX_16X16)
            {
                var sse16 = new uint[64];
                var sum16 = new int[64];
                var var16 = new uint[64];
                CalculateVariance(bw, bh, TX_8X8, sse8, sum8, var16, sse16, sum16);
                sseTx = sse16;
                varTx = var16;
                numBlks >>= 2;
            }
            SetEarlyTermBasedOnUvPlane(cpi, x, bsize, xd, miRow, miCol, ref earlyTerm, numBlks, sseTx, varTx, sum, var, sse);
        }
        CalcRateDistBlockParam(cpi, x, ref rdStats, calculateRd, ref earlyTerm, bsize, sse);
    }

    /// <summary>model_rd_for_sb_y.</summary>
    private static void ModelRdForSbY(AomComp cpi, int bsize, AomMacroblock x, AomMacroblockD xd, ref AomRdStats rdStats, ref uint varOut,
        bool calculateRd, ref int earlyTerm, bool haveEarlyTerm)
    {
        if (x.ForceZeromvSkipForBlk != 0 && haveEarlyTerm)
        {
            earlyTerm = 1;
            rdStats.Rate = 0;
            rdStats.Dist = 0;
            rdStats.Sse = 0;
        }
        int refFrame = xd.Mi0.RefFrame0;
        var p = x.Plane[0];
        var pd = xd.Plane[0];
        uint var = Vf(p.Src, pd.Dst, BlockSizeWide[bsize], BlockSizeHigh[bsize], xd.Bd, out uint sse);
        int forceSkip = 0;
        xd.Mi0.TxSize = CalculateTxSize(cpi, bsize, x, var, sse, ref forceSkip);
        varOut = var;
        int rate;
        long dist;
        if (calculateRd && (forceSkip == 0 || refFrame == INTRA_FRAME))
            AomModelRd.WithCurvfit(cpi, x, bsize, 0, sse, BlockSizeWide[bsize] * BlockSizeHigh[bsize], out rate, out dist);
        else
        {
            rate = int.MaxValue;
            dist = int.MaxValue;
        }
        rdStats.Sse = sse;
        x.PredSse[refFrame] = sse;
        if (forceSkip != 0 && refFrame > INTRA_FRAME)
        {
            rate = 0;
            dist = (long)sse << 4;
        }
        rdStats.SkipTxfm = (byte)(rate == 0 ? 1 : 0);
        rdStats.Rate = rate;
        rdStats.Dist = dist;
    }

    /// <summary>get_drl_cost.</summary>
    private static int GetDrlCost(int thisMode, int refMvIdx, AomMbmiExtInter ext, int[] drlModeCost0, int refFrameType)
    {
        int cost = 0;
        if (thisMode == NEWMV || thisMode == NEW_NEWMV)
        {
            for (int idx = 0; idx < 2; ++idx)
                if (ext.RefMvCount[refFrameType] > idx + 1)
                {
                    int drlCtx = AomInter.DrlCtx(ext.Weight[refFrameType], idx);
                    cost += drlModeCost0[drlCtx * 2 + (refMvIdx != idx ? 1 : 0)];
                    if (refMvIdx == idx) return cost;
                }
            return cost;
        }
        if (AomInter.HaveNearmvInInterMode(thisMode))
        {
            for (int idx = 1; idx < 3; ++idx)
                if (ext.RefMvCount[refFrameType] > idx + 1)
                {
                    int drlCtx = AomInter.DrlCtx(ext.Weight[refFrameType], idx);
                    cost += drlModeCost0[drlCtx * 2 + (refMvIdx != idx - 1 ? 1 : 0)];
                    if (refMvIdx == idx - 1) return cost;
                }
            return cost;
        }
        return cost;
    }

    /// <summary>cost_mv_ref (the compound modes included).</summary>
    private static int CostMvRefNonrd(AomModeCosts mc, int mode, int modeContext)
    {
        if (AomInter.IsInterCompoundMode(mode)) return mc.InterCompoundModeCost[modeContext * 8 + (mode - NEAREST_NEARESTMV)];
        return AomRdoptInter.CostMvRef(mc, mode, modeContext);
    }

    /// <summary>newmv_diff_bias.</summary>
    private static void NewmvDiffBias(AomMacroblockD xd, int thisMode, ref AomRdStats thisRdc, int bsize, int mvRow, int mvCol, int speed,
        uint spatialVariance, int sourceSadNonrd)
    {
        if (thisMode == NEWMV)
        {
            if (bsize >= BLOCK_64X64 && sourceSadNonrd != AomRtSb.kHighSad && spatialVariance < 300 &&
                (mvRow > 16 || mvRow < -16 || mvCol > 16 || mvCol < -16))
            {
                thisRdc.Rdcost <<= 2;
                return;
            }
            const int INVALID_MV_ROW_COL = -32768;
            bool aboveValid = false, leftValid = false;
            int aboveRow = INVALID_MV_ROW_COL, aboveCol = INVALID_MV_ROW_COL, leftRow = INVALID_MV_ROW_COL, leftCol = INVALID_MV_ROW_COL;
            if (xd.AboveMbmi != null)
            {
                aboveValid = xd.AboveMbmi.Mv0.AsInt != AomMv.Invalid.AsInt;
                aboveRow = xd.AboveMbmi.Mv0.Row;
                aboveCol = xd.AboveMbmi.Mv0.Col;
            }
            if (xd.LeftMbmi != null)
            {
                leftValid = xd.LeftMbmi.Mv0.AsInt != AomMv.Invalid.AsInt;
                leftRow = xd.LeftMbmi.Mv0.Row;
                leftCol = xd.LeftMbmi.Mv0.Col;
            }
            int avgRow, avgCol;
            if (aboveValid && leftValid)
            {
                avgRow = (aboveRow + leftRow + 1) >> 1;
                avgCol = (aboveCol + leftCol + 1) >> 1;
            }
            else if (aboveValid) { avgRow = aboveRow; avgCol = aboveCol; }
            else if (leftValid) { avgRow = leftRow; avgCol = leftCol; }
            else avgRow = avgCol = 0;
            int rowDiff = avgRow - mvRow, colDiff = avgCol - mvCol;
            if (rowDiff > 80 || rowDiff < -80 || colDiff > 80 || colDiff < -80)
            {
                if (bsize >= BLOCK_32X32) thisRdc.Rdcost <<= 1;
                else thisRdc.Rdcost = 5 * thisRdc.Rdcost >> 2;
            }
        }
        else if (speed >= 8 && spatialVariance < 150 && (mvRow > 64 || mvRow < -64 || mvCol > 64 || mvCol < -64))
            thisRdc.Rdcost = 5 * thisRdc.Rdcost >> 2;
    }

    /// <summary>update_thresh_freq_fact.</summary>
    private static void UpdateThreshFreqFact(AomComp cpi, AomMacroblock x, int bsize, int refFrame, int bestModeIdx, int mode)
    {
        int thrModeIdx = ModeIdx[refFrame, ModeOffset(mode)];
        int minSize = Math.Max(bsize - 3, BLOCK_4X4), maxSize = Math.Min(bsize + 6, BLOCK_128X128);
        for (int bs = minSize; bs <= maxSize; bs += 3)
        {
            ref int f = ref x.ThreshFreqFact[bs, thrModeIdx];
            if (thrModeIdx == bestModeIdx) f -= f >> 4;
            else f = Math.Min(f + RD_THRESH_INC, cpi.Sf.inter_sf.adaptive_rd_thresh * RD_THRESH_MAX_FACT);
        }
    }

    /// <summary>The single-reference luma prediction (av1_enc_build_inter_predictor_y_nonrd) or the full one.</summary>
    private static void BuildLumaPrediction(AomComp cpi, AomMacroblockD xd, int miRow, int miCol, int bsize, bool isSinglePred)
    {
        if (isSinglePred) AomInterPred.EncBuildInterPredictorY(xd, miRow, miCol);
        else AomInterPred.EncBuildInterPredictor(cpi.Cm, xd, miRow, miCol, null, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
    }

    /// <summary>search_filter_ref.</summary>
    private static void SearchFilterRef(AomComp cpi, AomMacroblock x, ref AomRdStats thisRdc, int miRow, int miCol, PredBuffer[] tmpBuffer, int bsize,
        bool reuseInterPred, ref PredBuffer? thisModePred, ref int thisEarlyTerm, ref uint var, bool useModelYrdLarge, long bestSse, bool isSinglePred)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var pd = xd.Plane[0];
        var mi = xd.Mi0;
        int bw = BlockSizeWide[bsize];
        int dimFactor = cpi.Sf.interp_sf.disable_dual_filter == 0 ? FILTER_SEARCH_SIZE : 1;
        var pfRdStats = new AomRdStats[FILTER_SEARCH_SIZE * FILTER_SEARCH_SIZE];
        var pfTxSize = new int[FILTER_SEARCH_SIZE * FILTER_SEARCH_SIZE];
        var currentPred = thisModePred;
        int bestSkip = 0, bestEarlyTerm = 0;
        long bestCost = long.MaxValue;
        int bestFilterIndex = -1;
        for (int filterIdx = 0; filterIdx < FILTER_SEARCH_SIZE * FILTER_SEARCH_SIZE; ++filterIdx)
        {
            uint fs = FiltersRefSet[filterIdx];
            if (cpi.Sf.interp_sf.disable_dual_filter != 0 && (fs & 0xffff) != (fs >> 16)) continue;
            mi.InterpFilters = fs;
            BuildLumaPrediction(cpi, xd, miRow, miCol, bsize, isSinglePred);
            uint currVar = uint.MaxValue;
            if (useModelYrdLarge)
                ModelSkipForSbYLarge(cpi, bsize, miRow, miCol, x, xd, ref pfRdStats[filterIdx], ref thisEarlyTerm, true, bestSse, ref currVar, true,
                    uint.MaxValue);
            else
            {
                int dummy = 0;
                ModelRdForSbY(cpi, bsize, x, xd, ref pfRdStats[filterIdx], ref currVar, true, ref dummy, false);
            }
            pfRdStats[filterIdx].Rate += AomInterpSearch.GetSwitchableRateFrame(x, xd, cm.InterpFilter, cpi.Seq!.EnableDualFilter);
            long cost = AomRd.RdCost(x.Rdmult, pfRdStats[filterIdx].Rate, pfRdStats[filterIdx].Dist);
            pfTxSize[filterIdx] = mi.TxSize;
            if (cost < bestCost)
            {
                var = currVar;
                bestFilterIndex = filterIdx;
                bestCost = cost;
                bestSkip = pfRdStats[filterIdx].SkipTxfm;
                bestEarlyTerm = thisEarlyTerm;
                if (reuseInterPred)
                {
                    if (thisModePred != currentPred)
                    {
                        FreePredBuffer(thisModePred);
                        thisModePred = currentPred;
                    }
                    currentPred = tmpBuffer[GetPredBuffer(tmpBuffer, 3)];
                    pd.Dst.Buf = currentPred.Data;
                    pd.Dst.Offset = currentPred.Offset;
                    pd.Dst.Stride = bw;
                }
            }
        }
        if (reuseInterPred && thisModePred != currentPred) FreePredBuffer(currentPred);
        mi.InterpFilters = FiltersRefSet[bestFilterIndex];
        mi.TxSize = pfTxSize[bestFilterIndex];
        thisRdc.Rate = pfRdStats[bestFilterIndex].Rate;
        thisRdc.Dist = pfRdStats[bestFilterIndex].Dist;
        thisRdc.Sse = pfRdStats[bestFilterIndex].Sse;
        thisRdc.SkipTxfm = (byte)(bestSkip != 0 || bestEarlyTerm != 0 ? 1 : 0);
        thisEarlyTerm = bestEarlyTerm;
        if (reuseInterPred)
        {
            pd.Dst.Buf = thisModePred!.Data;
            pd.Dst.Offset = thisModePred.Offset;
            pd.Dst.Stride = thisModePred.Stride;
        }
        else if (bestFilterIndex < dimFactor * FILTER_SEARCH_SIZE - 1) BuildLumaPrediction(cpi, xd, miRow, miCol, bsize, isSinglePred);
    }

    /// <summary>is_warped_mode_allowed.</summary>
    private static bool IsWarpedModeAllowed(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        if (cpi.Sf.inter_sf.extra_prune_warped != 0) return false;
        if (mbmi.HasSecondRef) return false;
        int last = SIMPLE_TRANSLATION;
        if (cm.SwitchableMotionMode) last = AomRdoptInter.MotionModeAllowed(xd.GlobalMotion, xd, mbmi, cm.AllowWarpedMotion);
        return last == WARPED_CAUSAL;
    }

    /// <summary>calc_num_proj_ref.</summary>
    private static void CalcNumProjRef(AomComp cpi, AomMacroblock x, AomMbModeInfo mi)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        mi.NumProjRef = 1;
        var wsi = x.WarpSampleInfo[mi.RefFrame0];
        int last = SIMPLE_TRANSLATION;
        if (cm.SwitchableMotionMode) last = AomRdoptInter.MotionModeAllowed(xd.GlobalMotion, xd, mi, cm.AllowWarpedMotion);
        if (last == WARPED_CAUSAL)
        {
            if (wsi.Num < 0) wsi.Num = AomWarp.FindSamples(cm, xd, wsi.Pts, wsi.PtsInref);
            mi.NumProjRef = (byte)wsi.Num;
        }
    }

    /// <summary>search_motion_mode.</summary>
    private static void SearchMotionMode(AomComp cpi, AomMacroblock x, ref AomRdStats thisRdc, int miRow, int miCol, int bsize, ref int thisEarlyTerm,
        bool useModelYrdLarge, ref int rateMv, long bestSse)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        var pfRdStats = new AomRdStats[MOTION_MODE_SEARCH_SIZE];
        int bestSkip = 0, bestEarlyTerm = 0;
        long bestCost = long.MaxValue;
        int bestModeIndex = -1;
        int interpFilter = cm.InterpFilter;
        ReadOnlySpan<int> motionModes = stackalloc int[] { SIMPLE_TRANSLATION, WARPED_CAUSAL };
        int modeSearchSize = IsWarpedModeAllowed(cpi, x, mi) ? 2 : 1;
        var wsi = x.WarpSampleInfo[mi.RefFrame0];
        int totalSamples = mi.NumProjRef;
        if (totalSamples == 0) modeSearchSize = 1;
        var baseMbmi = mi.Clone();
        var bestMbmi = new AomMbModeInfo();
        for (int modeIndex = 0; modeIndex < modeSearchSize; ++modeIndex)
        {
            long cost = long.MaxValue;
            int motionMode = motionModes[modeIndex];
            mi.CopyFrom(baseMbmi);
            mi.MotionMode = (byte)motionMode;
            uint dummyVar = 0;
            if (motionMode == SIMPLE_TRANSLATION)
            {
                mi.InterpFilters = AomInterpSearch.Broadcast(EIGHTTAP_REGULAR);
                AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                if (useModelYrdLarge)
                    ModelSkipForSbYLarge(cpi, bsize, miRow, miCol, x, xd, ref pfRdStats[modeIndex], ref thisEarlyTerm, true, bestSse, ref dummyVar,
                        false, uint.MaxValue);
                else
                {
                    int d = 0;
                    ModelRdForSbY(cpi, bsize, x, xd, ref pfRdStats[modeIndex], ref dummyVar, true, ref d, false);
                }
                pfRdStats[modeIndex].Rate += AomInterpSearch.GetSwitchableRateFrame(x, xd, cm.InterpFilter, cpi.Seq!.EnableDualFilter);
                cost = AomRd.RdCost(x.Rdmult, pfRdStats[modeIndex].Rate, pfRdStats[modeIndex].Dist);
            }
            else
            {
                var pts = new int[SAMPLES_ARRAY_SIZE];
                var ptsInref = new int[SAMPLES_ARRAY_SIZE];
                mi.WmParams.WmType = DEFAULT_WMTYPE;
                mi.InterpFilters = AomInterpSearch.Broadcast(AomInterpSearch.Unswitchable(interpFilter));
                Array.Copy(wsi.Pts, pts, totalSamples * 2);
                Array.Copy(wsi.PtsInref, ptsInref, totalSamples * 2);
                if (mi.NumProjRef > 1) mi.NumProjRef = (byte)AomWarp.SelectSamples(mi.Mv0, pts, ptsInref, mi.NumProjRef, bsize);
                if (!AomWarp.FindProjection(mi.NumProjRef, pts, ptsInref, bsize, mi.Mv0.Row, mi.Mv0.Col, mi.WmParams, miRow, miCol))
                {
                    if (mi.Mode == NEWMV)
                    {
                        var mv0 = mi.Mv0;
                        var wm0 = new AomWarpedMotionParams();
                        wm0.CopyFrom(mi.WmParams);
                        byte numProjRef0 = mi.NumProjRef;
                        var refMv = AomMotionSearch.GetRefMv(x, 0);
                        var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, null, cpi.SubpelParamsScratch(x));
                        AomWarp.RefineWarpedMv(xd, cm, ms, bsize, wsi.Pts, wsi.PtsInref, totalSamples, cpi.Sf.mv_sf.warp_search_method,
                            cpi.Sf.mv_sf.warp_search_iters, cpi.EnableIntraEdgeFilter);
                        if (mi.Mv0.AsInt == refMv.AsInt) continue;
                        if (mv0.AsInt != mi.Mv0.AsInt) rateMv = AomMotionSearch.MvBitCost(x, mi.Mv0, refMv);
                        else
                        {
                            mi.Mv0 = mv0;
                            mi.WmParams.CopyFrom(wm0);
                            mi.NumProjRef = numProjRef0;
                        }
                    }
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 0, cm.NumPlanes - 1, cpi.EnableIntraEdgeFilter);
                    if (useModelYrdLarge)
                        ModelSkipForSbYLarge(cpi, bsize, miRow, miCol, x, xd, ref pfRdStats[modeIndex], ref thisEarlyTerm, true, bestSse, ref dummyVar,
                            false, uint.MaxValue);
                    else
                    {
                        int d = 0;
                        ModelRdForSbY(cpi, bsize, x, xd, ref pfRdStats[modeIndex], ref dummyVar, true, ref d, false);
                    }
                    pfRdStats[modeIndex].Rate += x.ModeCosts.MotionModeCost[bsize * 3 + mi.MotionMode];
                    cost = AomRd.RdCost(x.Rdmult, pfRdStats[modeIndex].Rate, pfRdStats[modeIndex].Dist);
                }
                else cost = long.MaxValue;
            }
            if (cost < bestCost)
            {
                bestModeIndex = modeIndex;
                bestCost = cost;
                bestSkip = pfRdStats[modeIndex].SkipTxfm;
                bestEarlyTerm = thisEarlyTerm;
                bestMbmi.CopyFrom(mi);
            }
        }
        mi.CopyFrom(bestMbmi);
        thisRdc.Rate = pfRdStats[bestModeIndex].Rate;
        thisRdc.Dist = pfRdStats[bestModeIndex].Dist;
        thisRdc.Sse = pfRdStats[bestModeIndex].Sse;
        thisRdc.SkipTxfm = (byte)(bestSkip != 0 || bestEarlyTerm != 0 ? 1 : 0);
        thisEarlyTerm = bestEarlyTerm;
        if (bestModeIndex < FILTER_SEARCH_SIZE - 1)
            AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
    }

    // ---------------------------------------------------------------- reference / mode pruning

    /// <summary>get_ref_frame_use_mask (no rtc reference config, one spatial layer, no segmentation).</summary>
    private static void GetRefFrameUseMask(AomComp cpi, AomMacroblock x, AomMbModeInfo mi, int miRow, int miCol, int bsize, bool gfTemporalRef,
        int[] useRefFrame, ref int forceSkipLowTempVar)
    {
        var cm = cpi.Cm;
        bool isSmallSb = cm.SbSize == BLOCK_64X64;
        bool useAltRefFrame = cpi.Sf.rt_sf.use_nonrd_altref_frame != 0;
        bool useGoldenRefFrame = true;
        const bool useLastRefFrame = true;
        if (cpi.Rt!.FramesSinceGolden == 0 && gfTemporalRef) useGoldenRefFrame = false;
        if (cpi.Sf.rt_sf.short_circuit_low_temp_var != 0 && x.NonrdPruneRefFrameSearch != 0)
        {
            forceSkipLowTempVar = isSmallSb ? AomVarBasedPart.GetForceSkipLowTempVarSmallSb(x.VarianceLow, miRow, miCol, bsize)
                : AomVarBasedPart.GetForceSkipLowTempVar(x.VarianceLow, miRow, miCol, bsize);
            if (forceSkipLowTempVar != 0)
            {
                useGoldenRefFrame = false;
                useAltRefFrame = false;
            }
        }
        if (x.NonrdPruneRefFrameSearch > 2 || x.ForceZeromvSkipForBlk != 0 || (x.NonrdPruneRefFrameSearch > 1 && bsize > BLOCK_64X64))
        {
            useGoldenRefFrame = false;
            useAltRefFrame = false;
        }
        if (x.SourceVariance < 200 && x.SourceSadNonrd >= AomRtSb.kLowSad)
        {
            if (x.ColorSensitivitySbG[0] == 1 || x.ColorSensitivitySbG[1] == 1) useGoldenRefFrame = false;
            if (x.ColorSensitivitySbAlt[0] == 1 || x.ColorSensitivitySbAlt[1] == 1) useAltRefFrame = false;
        }
        if ((cpi.RefFrameFlags & AOM_LAST_FLAG) != 0 && !useGoldenRefFrame && !useAltRefFrame && x.PredMvSad[LAST_FRAME] != int.MaxValue &&
            x.NonrdPruneRefFrameSearch > 2 && x.ColorSensitivitySbG[0] == 0 && x.ColorSensitivitySbG[1] == 0)
        {
            int thr = cm.Width * cm.Height > RESOLUTION_288P ? 100 : 150;
            int pred = x.PredMvSad[LAST_FRAME] >> (BWidthLog2Lookup[bsize] + BHeightLog2Lookup[bsize]);
            if (pred > thr) useGoldenRefFrame = true;
        }
        useAltRefFrame = (cpi.RefFrameFlags & AOM_ALT_FLAG) != 0 && useAltRefFrame;
        useGoldenRefFrame = (cpi.RefFrameFlags & AOM_GOLD_FLAG) != 0 && useGoldenRefFrame;
        useRefFrame[ALTREF_FRAME] = useAltRefFrame ? 1 : 0;
        useRefFrame[GOLDEN_FRAME] = useGoldenRefFrame ? 1 : 0;
        useRefFrame[LAST_FRAME] = useLastRefFrame ? 1 : 0;
    }

    /// <summary>get_chessboard_index.</summary>
    private static int GetChessboardIndex(int frameIndex) => frameIndex & 0x1;

    /// <summary>is_filter_search_enabled_blk.</summary>
    private static int IsFilterSearchEnabledBlk(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, int cbPredFilterSearch, ref int filtSelect)
    {
        if (cpi.Sf.rt_sf.use_nonrd_filter_search == 0) return 0;
        if (cbPredFilterSearch == 0) return 1;
        var xd = x.E;
        int enableInterpSearch;
        if (xd.LeftMbmi == null || xd.AboveMbmi == null) enableInterpSearch = 2;
        else if (!(xd.LeftMbmi.IsInterBlock && xd.AboveMbmi.IsInterBlock)) enableInterpSearch = 2;
        else if (xd.LeftMbmi.InterpFilters != xd.AboveMbmi.InterpFilters) enableInterpSearch = 2;
        else if (cbPredFilterSearch == 1 && (int)(xd.LeftMbmi.InterpFilters >> 16) != EIGHTTAP_REGULAR) enableInterpSearch = 2;
        else
        {
            if ((int)(xd.LeftMbmi.InterpFilters >> 16) == EIGHTTAP_SMOOTH) filtSelect = EIGHTTAP_SMOOTH;
            int bsl = MiSizeWideLog2[bsize];
            enableInterpSearch = (((miRow + miCol) >> bsl) + GetChessboardIndex(cpi.FrameNumber)) & 0x1;
        }
        return enableInterpSearch;
    }

    /// <summary>skip_mode_by_threshold.</summary>
    private static bool SkipModeByThreshold(int mode, int refFrame, AomMv mv, int framesSinceGolden, AomComp cpi, int bsize, AomMacroblock x,
        long bestCost, int bestSkip, int extraShift)
    {
        int modeIndex = ModeIdx[refFrame, InterOffset(mode)];
        long modeRdThresh = bestSkip != 0 ? (long)cpi.Rd.Thresh(0, bsize, modeIndex) << (extraShift + 1)
            : (long)cpi.Rd.Thresh(0, bsize, modeIndex) << extraShift;
        if (refFrame != LAST_FRAME)
        {
            modeRdThresh <<= 1;
            if (refFrame == GOLDEN_FRAME && framesSinceGolden > 4) modeRdThresh <<= extraShift + 1;
        }
        if (RdLessThanThresh(bestCost, modeRdThresh, x.ThreshFreqFact[bsize, modeIndex]))
            if (mv.AsInt != 0) return true;
        return false;
    }

    private static bool SkipModeByLowTemp(int mode, int refFrame, int bsize, int sourceSadNonrd, AomMv mv, int forceSkipLowTempVar)
    {
        if (forceSkipLowTempVar != 0 && refFrame != LAST_FRAME && mv.AsInt != 0) return true;
        if (sourceSadNonrd != AomRtSb.kHighSad && bsize >= BLOCK_64X64 && forceSkipLowTempVar != 0 && mode == NEWMV) return true;
        return false;
    }

    private static bool SkipModeByBsizeAndRefFrame(int mode, int refFrame, int bsize, int extraPrune, uint sseZeromvNorm, int morePrune, int skipNearmv)
    {
        const uint threshSkipGolden = 500;
        if (refFrame != LAST_FRAME && sseZeromvNorm < threshSkipGolden && mode == NEWMV) return true;
        if ((bsize == BLOCK_128X128 && mode == NEWMV) || (skipNearmv != 0 && mode == NEARMV)) return true;
        if (extraPrune != 0)
        {
            if (extraPrune > 1 && refFrame != LAST_FRAME && bsize > BLOCK_16X16 && mode == NEWMV) return true;
            if (refFrame != LAST_FRAME && mode == NEARMV) return true;
            if (morePrune != 0 && bsize >= BLOCK_32X32 && mode == NEARMV) return true;
        }
        return false;
    }

    /// <summary>set_color_sensitivity (content default, noise estimate off unless enabled).</summary>
    private static void SetColorSensitivity(AomComp cpi, AomMacroblock x, int bsize, int ySad, uint sourceVariance, SearchState st)
    {
        var cm = cpi.Cm;
        int sourceSadNonrd = x.SourceSadNonrd;
        bool highRes = cm.Width * cm.Height >= 640 * 360;
        if (bsize == cm.SbSize && !x.ForceColorCheckBlockLevel)
        {
            if (x.ColorSensitivity[0] == 2) x.ColorSensitivity[0] = (byte)(sourceSadNonrd >= AomRtSb.kMedSad ? 1 : 0);
            if (x.ColorSensitivity[1] == 2) x.ColorSensitivity[1] = (byte)(sourceSadNonrd >= AomRtSb.kMedSad ? 1 : 0);
            return;
        }
        int shift = 3;
        const uint sourceVarThr = 50;
        const int normUvSadThresh = 100, normUvSadThresh2 = 40;
        if (sourceSadNonrd >= AomRtSb.kMedSad && x.SourceVariance > 0 && highRes) shift = 4;
        int noiseLevel = cpi.Rt!.NoiseEstimateEnabled ? cpi.Rt.NoiseLevel : 0;
        int normSad = ySad >> (BWidthLog2Lookup[bsize] + BHeightLog2Lookup[bsize]);
        uint threshSpatial = cm.Width > 1920 ? 5000u : 1000u;
        if (noiseLevel == 0 && sourceVariance > threshSpatial && normSad < 50)
        {
            x.ColorSensitivity[0] = 0;
            x.ColorSensitivity[1] = 0;
            return;
        }
        for (int plane = 1; plane < cm.NumPlanes; ++plane)
        {
            if (x.ColorSensitivity[plane - 1] == 2 || x.ForceColorCheckBlockLevel ||
                (x.ColorSensitivity[plane - 1] == 0 && sourceSadNonrd >= AomRtSb.kMedSad && highRes))
            {
                var p = x.Plane[plane];
                int bs = AomEncodeMb.PlaneBlockSize(bsize, cm.SsX, cm.SsY);
                int uvSad = (int)AomVarBasedPart.Sdf(p.Src, st.Yv12Mb[LAST_FRAME, plane], BlockSizeWide[bs], BlockSizeHigh[bs], cm.BitDepth);
                int normUvSad = uvSad >> (BWidthLog2Lookup[bs] + BHeightLog2Lookup[bs]);
                x.ColorSensitivity[plane - 1] = (byte)(uvSad > (ySad >> shift) && normUvSad > normUvSadThresh2 ? 1 : 0);
                if (sourceVariance < sourceVarThr && normUvSad > normUvSadThresh) x.ColorSensitivity[plane - 1] = 1;
            }
        }
    }

    // ---------------------------------------------------------------- compound

    /// <summary>setup_compound_prediction.</summary>
    private static void SetupCompoundPrediction(AomComp cpi, AomMacroblock x, SearchState st, int rf0, int rf1, out int refMvIdx)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        if (st.UseRefFrameMask[rf1] == 0)
        {
            var yv12 = cm.RefBufs[rf1]?.Buf;
            if (yv12 != null) SetupPredBlock(xd, st.Yv12Mb, rf1, yv12, cm.RefScaleFactors[rf1], cm.NumPlanes);
        }
        int refFrameComp = AomInter.RefFrameType(rf0, rf1);
        ext.ModeContext[refFrameComp] = 0;
        ext.RefMvCount[refFrameComp] = byte.MaxValue;
        AomMvPred.FindMvRefs(cm, xd, mbmi, refFrameComp, ext);
        AomMvPred.CopyUsableRefMvStackAndWeight(xd, ext, refFrameComp);
        refMvIdx = mbmi.RefMvIdx + 1;
    }

    /// <summary>set_compound_mode.</summary>
    private static void SetCompoundMode(AomMacroblock x, int refFrame, int refFrame2, int refMvIdx, SearchState st, int thisMode)
    {
        var xd = x.E;
        var mi = xd.Mi0;
        mi.RefFrame0 = refFrame;
        mi.RefFrame1 = refFrame2;
        mi.CompoundIdx = 1;
        mi.CompGroupIdx = 0;
        mi.InterinterComp.Type = COMPOUND_AVERAGE;
        int refFrameComp = AomInter.RefFrameType(refFrame, refFrame2);
        var stack = x.MbmiExtInter.RefMvStack[refFrameComp];
        if (thisMode == GLOBAL_GLOBALMV)
        {
            st.FrameMv[thisMode, refFrame] = default;
            st.FrameMv[thisMode, refFrame2] = default;
        }
        else if (thisMode == NEAREST_NEARESTMV)
        {
            st.FrameMv[thisMode, refFrame] = stack[0].ThisMv;
            st.FrameMv[thisMode, refFrame2] = stack[0].CompMv;
        }
        else if (thisMode == NEAR_NEARMV)
        {
            st.FrameMv[thisMode, refFrame] = stack[refMvIdx].ThisMv;
            st.FrameMv[thisMode, refFrame2] = stack[refMvIdx].CompMv;
        }
    }

    /// <summary>skip_comp_based_on_var.</summary>
    private static bool SkipCompBasedOnVar(uint[,] singleVars, int bsize)
    {
        uint bestVar = uint.MaxValue;
        for (int m = 0; m < RTC_INTER_MODES; m++)
            for (int r = 0; r < REF_FRAMES; r++) bestVar = Math.Min(bestVar, singleVars[m, r]);
        uint thresh64 = (uint)(0.57356805f * 8659);
        uint thresh32 = (uint)(0.23964763f * 4281);
        return bsize switch
        {
            BLOCK_128X128 => bestVar < 4 * thresh64,
            BLOCK_64X64 => bestVar < thresh64,
            BLOCK_32X32 => bestVar < thresh32,
            BLOCK_16X16 => bestVar < thresh32 / 4,
            _ => false,
        };
    }

    /// <summary>fill_single_inter_mode_costs.</summary>
    private static void FillSingleInterModeCosts(int[,] costs, AomModeCosts mc, short[] modeContext)
    {
        var used = new bool[REF_FRAMES];
        foreach (var (r, _) in RefModeSet) used[r] = true;
        for (int rf = LAST_FRAME; rf < REF_FRAMES; rf++)
        {
            if (!used[rf]) continue;
            int modeCtx = AomInter.ModeContextAnalyzer(modeContext, rf, NONE_FRAME);
            for (int m = NEARESTMV; m <= NEWMV; m++) costs[InterOffset(m), rf] = AomRdoptInter.CostMvRef(mc, m, modeCtx);
        }
    }

    /// <summary>is_globalmv_better.</summary>
    private static bool IsGlobalmvBetter(int thisMode, int refFrame, int rateMv, AomModeCosts mc, int[,] costs, AomMbmiExtInter ext)
    {
        int globalmvCost = costs[InterOffset(GLOBALMV), refFrame];
        int thisModeCost = rateMv + costs[InterOffset(thisMode), refFrame];
        if (thisMode == NEWMV || thisMode == NEARMV)
            thisModeCost += GetDrlCost(NEWMV, 0, ext, mc.DrlModeCost0, AomInter.RefFrameType(refFrame, NONE_FRAME));
        return thisModeCost > globalmvCost;
    }

    /// <summary>setup_compound_params_from_comp_idx.</summary>
    private static bool SetupCompoundParamsFromCompIdx(AomComp cpi, AomMacroblock x, SearchState st, ref int thisMode, int refFrame, int refFrame2,
        int compIndex, bool compUseZeroZeromvOnly, ref int lastCompRefFrame, int bsize)
    {
        var rfs = CompRefModeSet[compIndex];
        bool skipGf = false, skipAlt = false;
        if (x.SourceVariance < 50 && bsize > BLOCK_16X16)
        {
            if (x.ColorSensitivitySbG[0] == 1 || x.ColorSensitivitySbG[1] == 1) skipGf = true;
            if (x.ColorSensitivitySbAlt[0] == 1 || x.ColorSensitivitySbAlt[1] == 1) skipAlt = true;
        }
        if (compUseZeroZeromvOnly && thisMode != GLOBAL_GLOBALMV) return false;
        var rtSf = cpi.Sf.rt_sf;
        if (refFrame2 == GOLDEN_FRAME && (rtSf.ref_frame_comp_nonrd[0] == 0 || skipGf || (cpi.RefFrameFlags & AOM_GOLD_FLAG) == 0)) return false;
        if (refFrame2 == LAST2_FRAME && (rtSf.ref_frame_comp_nonrd[1] == 0 || (cpi.RefFrameFlags & AOM_LAST2_FLAG) == 0)) return false;
        if (refFrame2 == ALTREF_FRAME && (rtSf.ref_frame_comp_nonrd[2] == 0 || skipAlt || (cpi.RefFrameFlags & AOM_ALT_FLAG) == 0)) return false;
        int refMvIdx = 0;
        if (lastCompRefFrame != rfs.Ref1)
        {
            SetupCompoundPrediction(cpi, x, st, rfs.Ref0, rfs.Ref1, out refMvIdx);
            lastCompRefFrame = rfs.Ref1;
        }
        SetCompoundMode(x, refFrame, refFrame2, refMvIdx, st, thisMode);
        if (thisMode != GLOBAL_GLOBALMV && st.FrameMv[thisMode, refFrame].AsInt == 0 && st.FrameMv[thisMode, refFrame2].AsInt == 0) return false;
        return true;
    }

    private static bool PreviousModePerformedPoorly(int mode, int refFrame, uint[,] vars, long[,] uvDist)
    {
        uint bestVar = uint.MaxValue;
        long bestUvDist = long.MaxValue;
        for (int m = 0; m < RTC_INTER_MODES; m++)
        {
            bestVar = Math.Min(bestVar, vars[m, refFrame]);
            bestUvDist = Math.Min(bestUvDist, uvDist[m, refFrame]);
        }
        const float mult = 1.125f;
        bool varBad = mult * bestVar < vars[InterOffset(mode), refFrame];
        if (uvDist[InterOffset(mode), refFrame] < long.MaxValue && bestUvDist != uvDist[InterOffset(mode), refFrame])
            varBad &= mult * bestUvDist < uvDist[InterOffset(mode), refFrame];
        return varBad;
    }

    private static bool PruneCompoundmodeWithSinglemodeVar(int compoundMode, int refFrame, int refFrame2, SearchState st)
    {
        int m0 = AomInter.CompoundRef0Mode(compoundMode), m1 = AomInter.CompoundRef1Mode(compoundMode);
        bool firstValid = false, secondValid = false, firstBad = false, secondBad = false;
        if (st.ModeChecked[m0, refFrame] != 0 && st.FrameMv[m0, refFrame].AsInt == st.FrameMv[compoundMode, refFrame].AsInt &&
            st.Vars[InterOffset(m0), refFrame] < uint.MaxValue)
        {
            firstValid = true;
            firstBad = PreviousModePerformedPoorly(m0, refFrame, st.Vars, st.UvDist);
        }
        if (st.ModeChecked[m1, refFrame2] != 0 && st.FrameMv[m1, refFrame2].AsInt == st.FrameMv[compoundMode, refFrame2].AsInt &&
            st.Vars[InterOffset(m1), refFrame2] < uint.MaxValue)
        {
            secondValid = true;
            secondBad = PreviousModePerformedPoorly(m1, refFrame2, st.Vars, st.UvDist);
        }
        if (firstValid && secondValid) return firstBad && secondBad;
        if (firstValid || secondValid) return firstBad || secondBad;
        return false;
    }

    // ---------------------------------------------------------------- predictors

    /// <summary>av1_setup_pred_block.</summary>
    private static void SetupPredBlock(AomMacroblockD xd, AomBuf2d[,] yv12Mb, int refFrame, AomFrameBuffer src, AomScaleFactors? sf, int numPlanes)
    {
        for (int i = 0; i < numPlanes; ++i)
        {
            var pd = xd.Plane[i];
            AomInterPred.SetupPredPlane(ref yv12Mb[refFrame, i], xd.Mi0.Bsize, src, i, xd.MiRow, xd.MiCol, sf, pd.SubsamplingX, pd.SubsamplingY);
        }
    }

    /// <summary>find_predictors.</summary>
    private static void FindPredictors(AomComp cpi, AomMacroblock x, int refFrame, SearchState st, int bsize, int forceSkipLowTempVar,
        bool skipPredMv)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        var refBuf = cm.RefBufs[refFrame]!.Buf;
        bool refIsScaled = refBuf.CropHeights[0] != cm.Height || refBuf.CropWidths[0] != cm.Width;
        var scaledRef = cpi.GetScaledRefFrame(refFrame);
        var yv12 = refIsScaled && scaledRef != null ? scaledRef : refBuf;
        int numPlanes = cm.NumPlanes;
        x.PredMvSad[refFrame] = int.MaxValue;
        x.PredMv0Sad[refFrame] = int.MaxValue;
        x.PredMv1Sad[refFrame] = int.MaxValue;
        st.FrameMv[NEWMV, refFrame] = AomMv.Invalid;
        var sf = scaledRef != null ? null : cm.RefScaleFactors[refFrame];
        SetupPredBlock(xd, st.Yv12Mb, refFrame, yv12, sf, numPlanes);
        AomMvPred.FindMvRefs(cm, xd, mbmi, refFrame, ext);
        AomMvPred.CopyUsableRefMvStackAndWeight(xd, ext, refFrame);
        FindBestRefMvsFromStack(cm.AllowHighPrecisionMv, ext, refFrame, out st.FrameMv[NEARESTMV, refFrame], out st.FrameMv[NEARMV, refFrame]);
        st.FrameMv[GLOBALMV, refFrame] = ext.GlobalMvs[refFrame];
        if (!refIsScaled && bsize >= BLOCK_8X8 && !skipPredMv && !(forceSkipLowTempVar != 0 && refFrame != LAST_FRAME))
            AomMotionSearch.MvPred(cpi, x, st.Yv12Mb[refFrame, 0], refFrame, bsize);
        if (cm.SwitchableMotionMode) AomInterPred.CountOverlappableNeighbors(cm, xd);
        mbmi.NumProjRef = 1;
        st.UseScaledRefFrame[refFrame] = refIsScaled && scaledRef != null;
    }

    /// <summary>av1_find_best_ref_mvs_from_stack (is_integer 0).</summary>
    private static void FindBestRefMvsFromStack(bool allowHp, AomMbmiExtInter ext, int refFrame, out AomMv nearest, out AomMv near)
    {
        nearest = AomInter.LowerMvPrecision(RefMvAt(ext, refFrame, 0), allowHp, false);
        near = AomInter.LowerMvPrecision(RefMvAt(ext, refFrame, 1), allowHp, false);
    }

    private static AomMv RefMvAt(AomMbmiExtInter ext, int refFrame, int idx)
        => idx < ext.RefMvCount[refFrame] ? ext.RefMvStack[refFrame][idx].ThisMv : ext.GlobalMvs[refFrame];

    /// <summary>init_mbmi_nonrd.</summary>
    private static void InitMbmiNonrd(AomMbModeInfo mbmi, int predMode, int ref0, int ref1, AomCommon cm)
    {
        mbmi.RefMvIdx = 0;
        mbmi.Mode = predMode;
        mbmi.UvMode = UV_DC_PRED;
        mbmi.RefFrame0 = ref0;
        mbmi.RefFrame1 = ref1;
        mbmi.Palette.PaletteSize0 = 0;
        mbmi.Palette.PaletteSize1 = 0;
        mbmi.UseFilterIntra = 0;
        mbmi.Mv0 = default;
        mbmi.Mv1 = default;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.NumProjRef = 1;
        mbmi.InterintraMode = 0;
        mbmi.InterpFilters = AomInterpSearch.Broadcast(AomInterpSearch.Unswitchable(cm.InterpFilter));   // set_default_interp_filters
    }
}
