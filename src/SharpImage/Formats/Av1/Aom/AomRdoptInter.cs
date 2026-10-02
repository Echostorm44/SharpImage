using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    // oxcf: motion_mode_cfg.enable_obmc, tool_cfg.enable_global_motion, ref_frm_cfg.enable_onesided_comp /
    // enable_reduced_reference_set, intra_mode_cfg (smooth / paeth / angle delta), tool_cfg.enable_palette,
    // comp_type_cfg (smooth interintra, interintra / interinter wedge)
    public bool EnableObmc = true, EnableGlobalMotion = true, EnableOnesidedComp = true, EnableReducedReferenceSet;
    public bool EnableSmoothInterintra = true, EnableInterintraWedge = true, EnableInterinterWedge = true;
    /// <summary>cpi->ref_frame_dist_info, prune_ref_frame_mask, keep_single_ref_frame_mask, all_one_sided_refs,
    /// rc.is_src_frame_alt_ref.</summary>
    public readonly AomRefFrameDistanceInfo RefFrameDistInfo = new();
    public int PruneRefFrameMask, KeepSingleRefFrameMask, KeepCompRefFrameMask;
    public bool AllOneSidedRefs, IsSrcFrameAltRef;
    /// <summary>cm->cur_frame->ref_display_order_hint[INTER_REFS_PER_FRAME].</summary>
    public readonly int[] RefDisplayOrderHint = new int[INTER_REFS_PER_FRAME];
}

/// <summary>InterModeRdModel (tile data): the estimated rate-distortion model of inter_mode_rd_model_estimation 1.</summary>
internal sealed class AomInterModeRdModel
{
    public int Ready, Num;
    public double A, B, DistMean, LdMean, SseMean, SseSseMean, SseLdMean;
    public double DistSum, LdSum, SseSum, SseSseSum, SseLdSum;
}

/// <summary>motion_mode_best_st_candidate.</summary>
internal sealed class AomMotionModeBestCands
{
    public readonly AomMotionModeCandidate[] Cand = New();
    public int Num;

    private static AomMotionModeCandidate[] New()
    {
        var a = new AomMotionModeCandidate[MAX_WINNER_MOTION_MODES];
        for (int i = 0; i < a.Length; i++) a[i] = new AomMotionModeCandidate();
        return a;
    }
}

/// <summary>mode_skip_mask_t + InterModeSFArgs of the mode loop.</summary>
internal sealed class AomInterModeSfArgs
{
    public AomModeSkipMask ModeSkipMask = null!;
    public AomInterModeSearchState SearchState = null!;
    public int SkipRefFrameMask;
    public bool ReachFirstCompMode;
    public int ModeThreshMulFact;
    public int NumSingleModesProcessed;
    public bool PruneCpdUsingSrStatsReady;
}

// Port of libaom 3.14.1 av1/encoder/rdopt.c's inter-frame mode search: av1_rd_pick_inter_mode with
// set_params_rd_pick_inter_mode, the skip logic, handle_inter_mode, motion_mode_rd, tx_search_best_inter_candidates,
// refine_winner_mode_tx and the helpers they call.
internal static partial class AomRdoptInter
{
    private const int MODE_THRESH_QBITS = 12, LAST_NEW_MV_INDEX = 6, INTER_MODE_RD_DATA_OVERALL_SIZE = 6400;
    private static readonly int[] NumWinnerMotionModes = { 0, 10, 3 };

    /// <summary>av1_ref_frame_flag_list.</summary>
    public static readonly int[] RefFrameFlagList = { 0, 1, 2, 4, 8, 16, 32, 64 };

    // ---- small helpers ----

    internal static void CopyTxTypeMapFrom(AomMacroblockD xd, byte[] src, int n) => Array.Copy(src, 0, xd.TxTypeMap, xd.TxTypeMapOffset, n);
    internal static void CopyTxTypeMapTo(AomMacroblockD xd, byte[] dst, int n) => Array.Copy(xd.TxTypeMap, xd.TxTypeMapOffset, dst, 0, n);

    private static int ModeDefMode(int idx) => ModeDefs[idx * 3];
    private static int ModeDefRef0(int idx) => ModeDefs[idx * 3 + 1];
    private static int ModeDefRef1(int idx) => ModeDefs[idx * 3 + 2];

    /// <summary>get_prediction_mode_idx.</summary>
    public static int GetPredictionModeIdx(int mode, int rf0, int rf1)
    {
        if (mode < INTRA_MODE_END) return IntraToModeIdx[mode - INTRA_MODE_START];
        if (mode >= SINGLE_INTER_MODE_START && mode < SINGLE_INTER_MODE_END)
            return SingleInterToModeIdx[(mode - SINGLE_INTER_MODE_START) * REF_FRAMES + rf0];
        if (mode >= COMP_INTER_MODE_START && mode < COMP_INTER_MODE_END && rf1 != NONE_FRAME)
            return CompInterToModeIdx[((mode - COMP_INTER_MODE_START) * REF_FRAMES + rf0) * REF_FRAMES + rf1];
        return THR_INVALID;
    }

    /// <summary>set_ref_ptrs.</summary>
    public static void SetRefPtrs(AomCommon cm, AomMacroblockD xd, int rf0, int rf1)
    {
        xd.BlockRefScaleFactors[0] = cm.RefScaleFactors[rf0 >= LAST_FRAME ? rf0 : 1];
        xd.BlockRefScaleFactors[1] = cm.RefScaleFactors[rf1 >= LAST_FRAME ? rf1 : 1];
    }

    /// <summary>cost_mv_ref.</summary>
    public static int CostMvRef(AomModeCosts mc, int mode, int modeContext)
    {
        if (AomInter.IsInterCompoundMode(mode))
            return mc.InterCompoundModeCost[modeContext * 8 + (mode - NEAREST_NEARESTMV)];
        int modeCtx = modeContext & NEWMV_CTX_MASK;
        if (mode == NEWMV) return mc.NewmvModeCost[modeCtx * 2 + 0];
        int cost = mc.NewmvModeCost[modeCtx * 2 + 1];
        modeCtx = (modeContext >> GLOBALMV_OFFSET) & GLOBALMV_CTX_MASK;
        if (mode == GLOBALMV) return cost + mc.ZeromvModeCost[modeCtx * 2 + 0];
        cost += mc.ZeromvModeCost[modeCtx * 2 + 1];
        modeCtx = (modeContext >> REFMV_OFFSET) & REFMV_CTX_MASK;
        return cost + mc.RefmvModeCost[modeCtx * 2 + (mode != NEARESTMV ? 1 : 0)];
    }

    /// <summary>estimate_ref_frame_costs (no segmentation).</summary>
    private static void EstimateRefFrameCosts(AomCommon cm, AomMacroblockD xd, AomModeCosts mc, uint[] single, uint[,] comp)
    {
        int ctxIi = AomPredCommon.IntraInter(xd);
        single[INTRA_FRAME] = (uint)mc.IntraInterCost[ctxIi * 2 + 0];
        uint baseCost = (uint)mc.IntraInterCost[ctxIi * 2 + 1];
        for (int i = LAST_FRAME; i <= ALTREF_FRAME; ++i) single[i] = baseCost;
        int p1 = AomPredCommon.SingleRefP1(xd), p2 = AomPredCommon.SingleRefP2(xd), p3 = AomPredCommon.SingleRefP3(xd);
        int p4 = AomPredCommon.SingleRefP4(xd), p5 = AomPredCommon.SingleRefP5(xd), p6 = AomPredCommon.SingleRefP6(xd);
        uint Src(int ctx, int j, int b) => (uint)mc.SingleRefCost[(ctx * 6 + j) * 2 + b];
        single[LAST_FRAME] += Src(p1, 0, 0); single[LAST2_FRAME] += Src(p1, 0, 0); single[LAST3_FRAME] += Src(p1, 0, 0);
        single[GOLDEN_FRAME] += Src(p1, 0, 0); single[BWDREF_FRAME] += Src(p1, 0, 1); single[ALTREF2_FRAME] += Src(p1, 0, 1);
        single[ALTREF_FRAME] += Src(p1, 0, 1);
        single[LAST_FRAME] += Src(p3, 2, 0); single[LAST2_FRAME] += Src(p3, 2, 0); single[LAST3_FRAME] += Src(p3, 2, 1);
        single[GOLDEN_FRAME] += Src(p3, 2, 1);
        single[BWDREF_FRAME] += Src(p2, 1, 0); single[ALTREF2_FRAME] += Src(p2, 1, 0); single[ALTREF_FRAME] += Src(p2, 1, 1);
        single[LAST_FRAME] += Src(p4, 3, 0); single[LAST2_FRAME] += Src(p4, 3, 1);
        single[LAST3_FRAME] += Src(p5, 4, 0); single[GOLDEN_FRAME] += Src(p5, 4, 1);
        single[BWDREF_FRAME] += Src(p6, 5, 0); single[ALTREF2_FRAME] += Src(p6, 5, 1);

        if (cm.ReferenceMode != SINGLE_REFERENCE)
        {
            int bwdP = AomPredCommon.CompBwdrefP(xd), bwdP1 = AomPredCommon.CompBwdrefP1(xd);
            int refP = AomPredCommon.CompRefP(xd), refP1 = AomPredCommon.CompRefP1(xd), refP2 = AomPredCommon.CompRefP2(xd);
            int crtCtx = AomPredCommon.CompReferenceType(xd);
            var bi = new uint[REF_FRAMES];
            uint Crt(int b) => (uint)mc.CompRefTypeCost[crtCtx * 2 + b];
            uint Cr(int ctx, int j, int b) => (uint)mc.CompRefCost[(ctx * 3 + j) * 2 + b];
            uint Cb(int ctx, int j, int b) => (uint)mc.CompBwdrefCost[(ctx * 2 + j) * 2 + b];
            uint Uc(int ctx, int j, int b) => (uint)mc.UniCompRefCost[(ctx * 3 + j) * 2 + b];
            bi[LAST_FRAME] = bi[LAST2_FRAME] = bi[LAST3_FRAME] = bi[GOLDEN_FRAME] = baseCost + Crt(1);
            bi[BWDREF_FRAME] = bi[ALTREF2_FRAME] = 0;
            bi[ALTREF_FRAME] = 0;
            bi[LAST_FRAME] += Cr(refP, 0, 0); bi[LAST2_FRAME] += Cr(refP, 0, 0); bi[LAST3_FRAME] += Cr(refP, 0, 1); bi[GOLDEN_FRAME] += Cr(refP, 0, 1);
            bi[LAST_FRAME] += Cr(refP1, 1, 0); bi[LAST2_FRAME] += Cr(refP1, 1, 1);
            bi[LAST3_FRAME] += Cr(refP2, 2, 0); bi[GOLDEN_FRAME] += Cr(refP2, 2, 1);
            bi[BWDREF_FRAME] += Cb(bwdP, 0, 0); bi[ALTREF2_FRAME] += Cb(bwdP, 0, 0); bi[ALTREF_FRAME] += Cb(bwdP, 0, 1);
            bi[BWDREF_FRAME] += Cb(bwdP1, 1, 0); bi[ALTREF2_FRAME] += Cb(bwdP1, 1, 1);
            for (int r0 = LAST_FRAME; r0 <= GOLDEN_FRAME; ++r0)
                for (int r1 = BWDREF_FRAME; r1 <= ALTREF_FRAME; ++r1) comp[r0, r1] = bi[r0] + bi[r1];
            int uP = AomPredCommon.UniCompRefP(xd), uP1 = AomPredCommon.UniCompRefP1(xd), uP2 = AomPredCommon.UniCompRefP2(xd);
            comp[LAST_FRAME, LAST2_FRAME] = baseCost + Crt(0) + Uc(uP, 0, 0) + Uc(uP1, 1, 0);
            comp[LAST_FRAME, LAST3_FRAME] = baseCost + Crt(0) + Uc(uP, 0, 0) + Uc(uP1, 1, 1) + Uc(uP2, 2, 0);
            comp[LAST_FRAME, GOLDEN_FRAME] = baseCost + Crt(0) + Uc(uP, 0, 0) + Uc(uP1, 1, 1) + Uc(uP2, 2, 1);
            comp[BWDREF_FRAME, ALTREF_FRAME] = baseCost + Crt(0) + Uc(uP, 0, 1);
        }
        else
        {
            for (int r0 = LAST_FRAME; r0 <= GOLDEN_FRAME; ++r0)
                for (int r1 = BWDREF_FRAME; r1 <= ALTREF_FRAME; ++r1) comp[r0, r1] = 512;
            comp[LAST_FRAME, LAST2_FRAME] = 512;
            comp[LAST_FRAME, LAST3_FRAME] = 512;
            comp[LAST_FRAME, GOLDEN_FRAME] = 512;
            comp[BWDREF_FRAME, ALTREF_FRAME] = 512;
        }
    }

    /// <summary>store_coding_context.</summary>
    private static void StoreCodingContext(AomMacroblock x, AomPickModeContext ctx, int skippable)
    {
        var xd = x.E;
        ctx.RdStats.SkipTxfm = (byte)x.TxfmSkip;
        ctx.Skippable = skippable;
        ctx.Mic.CopyFrom(xd.Mi0);
        ctx.MbmiExtBestInter.CopyFrom(x.MbmiExtInter, AomInter.RefFrameType(xd.Mi0.RefFrame0, xd.Mi0.RefFrame1));
        AomTrace.Out?.Write($"scc {xd.MiRow} {xd.MiCol} bs {xd.Mi0.Bsize} rft {AomInter.RefFrameType(xd.Mi0.RefFrame0, xd.Mi0.RefFrame1)} mc {ctx.MbmiExtBestInter.ModeContext} cnt {ctx.MbmiExtBestInter.RefMvCount}" + (char)10);
    }

    /// <summary>av1_setup_pred_block: the reference planes at the block position.</summary>
    private static void SetupPredBlock(AomMacroblockD xd, AomBuf2d[,] yv12Mb, int refFrame, AomFrameBuffer src, AomScaleFactors? sf, int numPlanes)
    {
        for (int i = 0; i < numPlanes; ++i)
        {
            var pd = xd.Plane[i];
            AomInterPred.SetupPredPlane(ref yv12Mb[refFrame, i], xd.Mi0.Bsize, src, i, xd.MiRow, xd.MiCol, sf, pd.SubsamplingX, pd.SubsamplingY);
        }
    }

    /// <summary>setup_buffer_ref_mvs_inter.</summary>
    private static void SetupBufferRefMvsInter(AomComp cpi, AomMacroblock x, int refFrame, int bsize, AomBuf2d[,] yv12Mb)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var scaledRef = cpi.GetScaledRefFrame(refFrame);
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        var sf = cm.RefScaleFactors[refFrame];
        var yv12 = cm.RefBufs[refFrame]!.Buf;
        if (scaledRef != null) SetupPredBlock(xd, yv12Mb, refFrame, scaledRef, null, numPlanes);
        else SetupPredBlock(xd, yv12Mb, refFrame, yv12, sf, numPlanes);
        AomMvPred.FindMvRefs(cm, xd, mbmi, refFrame, ext);
        AomMvPred.CopyUsableRefMvStackAndWeight(xd, ext, refFrame);
        AomMotionSearch.MvPred(cpi, x, yv12Mb[refFrame, 0], refFrame, bsize);
        if (scaledRef != null) SetupPredBlock(xd, yv12Mb, refFrame, yv12, sf, numPlanes);
    }

    /// <summary>clamp_mv2.</summary>
    private static AomMv ClampMv2(AomMv mv, AomMacroblockD xd)
    {
        const int margin = (288 - 4) << 3;   // (AOM_BORDER_IN_PIXELS - AOM_INTERP_EXTEND) << 3
        return new AomMv(Math.Clamp((int)mv.Row, xd.MbToTopEdge - margin, xd.MbToBottomEdge + margin),
            Math.Clamp((int)mv.Col, xd.MbToLeftEdge - margin, xd.MbToRightEdge + margin));
    }

    /// <summary>skip_repeated_mv.</summary>
    private static bool SkipRepeatedMv(AomCommon cm, AomMacroblock x, int thisMode, int rf0, int rf1, AomInterModeSearchState st)
    {
        bool isComp = rf1 > INTRA_FRAME;
        int rft = AomInter.RefFrameType(rf0, rf1);
        var ext = x.MbmiExtInter;
        int refMvCount = ext.RefMvCount[rft];
        int compareMode = MB_MODE_COUNT;
        if (!isComp)
        {
            if (thisMode == NEARMV)
            {
                if (refMvCount == 0) compareMode = NEARESTMV;
                if (refMvCount == 1 && cm.GlobalMotion[rf0].WmType <= TRANSLATION) compareMode = GLOBALMV;
            }
            if (thisMode == GLOBALMV)
            {
                if (refMvCount == 0 && cm.GlobalMotion[rf0].WmType <= TRANSLATION) compareMode = NEARESTMV;
                if (refMvCount == 1) compareMode = NEARMV;
            }
            if (compareMode != MB_MODE_COUNT)
            {
                if (st.ModelledRd[compareMode, 0, rf0] != long.MaxValue)
                {
                    int modeCtx = AomInter.ModeContextAnalyzer(ext.ModeContext, rf0, rf1);
                    int compareCost = CostMvRef(x.ModeCosts, compareMode, modeCtx);
                    int thisCost = CostMvRef(x.ModeCosts, thisMode, modeCtx);
                    if (thisCost > compareCost)
                    {
                        st.ModelledRd[thisMode, 0, rf0] = st.ModelledRd[compareMode, 0, rf0];
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>clamp_and_check_mv.</summary>
    private static bool ClampAndCheckMv(out AomMv outMv, AomMv inMv, AomCommon cm, AomMacroblock x)
    {
        outMv = AomInter.LowerMvPrecision(inMv, cm.AllowHighPrecisionMv, cm.CurFrameForceIntegerMv);
        outMv = ClampMv2(outMv, x.E);
        return AomMcomp.IsFullmvInRange(x.MvLimits, outMv.ToFullMv());
    }

    /// <summary>clamp_mv_in_range.</summary>
    private static AomMv ClampMvInRange(AomMacroblock x, AomMv mv, int refIdx)
    {
        var refMv = AomMotionSearch.GetRefMv(x, refIdx);
        var l = AomSubpel.SetSubpelMvSearchRange(x.MvLimits, refMv);
        return new AomMv(Math.Clamp((int)mv.Row, l.RowMin, l.RowMax), Math.Clamp((int)mv.Col, l.ColMin, l.ColMax));
    }

    /// <summary>handle_newmv.</summary>
    private static long HandleNewmv(AomComp cpi, AomMacroblock x, int bsize, AomMv[] curMv, out int rateMv, AomHandleInterModeArgs args,
        AomInterModeInfo[] modeInfo)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool isComp = mbmi.HasSecondRef;
        int thisMode = mbmi.Mode;
        int r0 = mbmi.RefFrame0, r1 = mbmi.RefFrame1 < 0 ? 0 : mbmi.RefFrame1;
        int refMvIdx = mbmi.RefMvIdx;
        var st = args.State;
        rateMv = 0;
        if (isComp)
        {
            bool v0 = st.SingleNewmvValid[refMvIdx, r0] != 0, v1 = st.SingleNewmvValid[refMvIdx, r1] != 0;
            if (thisMode == NEW_NEWMV)
            {
                if (v0) curMv[0] = ClampMvInRange(x, st.SingleNewmv[refMvIdx, r0], 0);
                if (v1) curMv[1] = ClampMvInRange(x, st.SingleNewmv[refMvIdx, r1], 1);
                rateMv = 0;
                for (int i = 0; i < 2; ++i) rateMv += AomMotionSearch.MvBitCost(x, curMv[i], AomMotionSearch.GetRefMv(x, i));
            }
            else if (thisMode == NEAREST_NEWMV || thisMode == NEAR_NEWMV)
            {
                if (v1) curMv[1] = ClampMvInRange(x, st.SingleNewmv[refMvIdx, r1], 1);
                rateMv = AomMotionSearch.MvBitCost(x, curMv[1], AomMotionSearch.GetRefMv(x, 1));
            }
            else
            {
                if (v0) curMv[0] = ClampMvInRange(x, st.SingleNewmv[refMvIdx, r0], 0);
                rateMv = AomMotionSearch.MvBitCost(x, curMv[0], AomMotionSearch.GetRefMv(x, 0));
            }
        }
        else
        {
            const int refIdx = 0;
            int searchRange = int.MaxValue;
            if (cpi.Sf.mv_sf.reduce_search_range != 0 && mbmi.RefMvIdx > 0)
            {
                var refMv = AomMotionSearch.GetRefMv(x, refIdx);
                int minMvDiff = int.MaxValue, bestMatch = -1;
                Span<AomMv> prevRefMv = stackalloc AomMv[2];
                for (int idx = 0; idx < mbmi.RefMvIdx; ++idx)
                {
                    prevRefMv[idx] = AomMotionSearch.GetRefMvFromStack(refIdx, mbmi.RefFrame0, mbmi.RefFrame1, idx, x.MbmiExtInter);
                    int d = Math.Max(Math.Abs(refMv.Row - prevRefMv[idx].Row), Math.Abs(refMv.Col - prevRefMv[idx].Col));
                    if (minMvDiff > d) { minMvDiff = d; bestMatch = idx; }
                }
                if (minMvDiff < (16 << 3))
                    if (st.SingleNewmvValid[bestMatch, r0] != 0)
                    {
                        searchRange = minMvDiff;
                        searchRange += Math.Max(Math.Abs(st.SingleNewmv[bestMatch, r0].Row - prevRefMv[bestMatch].Row),
                            Math.Abs(st.SingleNewmv[bestMatch, r0].Col - prevRefMv[bestMatch].Col));
                        searchRange = (searchRange + 4) >> 3;
                    }
            }
            AomMotionSearch.SingleMotionSearch(cpi, x, bsize, refIdx, out rateMv, searchRange, modeInfo, out var bestMv, args);
            if (bestMv.AsInt == AomMv.Invalid.AsInt) return long.MaxValue;
            st.SingleNewmv[refMvIdx, r0] = bestMv;
            st.SingleNewmvRate[refMvIdx, r0] = rateMv;
            st.SingleNewmvValid[refMvIdx, r0] = 1;
            curMv[0] = bestMv;
            if (modeInfo[mbmi.RefMvIdx].Skip != 0) return long.MaxValue;
        }
        return 0;
    }

    /// <summary>update_mode_start_end_index.</summary>
    private static void UpdateModeStartEndIndex(AomComp cpi, AomMbModeInfo mbmi, out int start, out int end, int lastMotionModeAllowed,
        bool interintraAllowed, bool evalMotionMode)
    {
        start = SIMPLE_TRANSLATION;
        end = lastMotionModeAllowed + (interintraAllowed ? 1 : 0);
        if (cpi.Sf.winner_mode_sf.motion_mode_for_winner_cand != 0)
        {
            if (!evalMotionMode) end = SIMPLE_TRANSLATION;
            else start = 1;
        }
        if (cpi.Sf.inter_sf.extra_prune_warped != 0 && mbmi.Bsize > BLOCK_16X16) end = SIMPLE_TRANSLATION;
    }

    /// <summary>increase_motion_mode_rd.</summary>
    private static void IncreaseMotionModeRd(AomMbModeInfo best, AomMbModeInfo cur, ref long bestScaledRd, ref long thisScaledRd, int warpPct, float obmcPct)
    {
        if (bestScaledRd == long.MaxValue || thisScaledRd == long.MaxValue) return;
        double warpScale = warpPct / 100.0, obmcScale = obmcPct / 100.0;
        if (best.MotionMode == WARPED_CAUSAL) bestScaledRd += (long)(warpScale * bestScaledRd);
        else if (best.MotionMode == OBMC_CAUSAL) bestScaledRd += (long)(obmcScale * bestScaledRd);
        if (cur.MotionMode == WARPED_CAUSAL) thisScaledRd += (long)(warpScale * thisScaledRd);
        else if (cur.MotionMode == OBMC_CAUSAL) thisScaledRd += (long)(obmcScale * thisScaledRd);
    }

    /// <summary>adjust_rdcost.</summary>
    internal static void AdjustRdcost(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, bool isInterPred)
    {
        if ((cpi.Tuning == AOM_TUNE_IQ || cpi.Tuning == AOM_TUNE_SSIMULACRA2) && isInterPred)
        {
            rdCost.Dist += rdCost.Dist >> 3;
            rdCost.Rdcost += rdCost.Rdcost >> 3;
            return;
        }
        if (cpi.Sharpness != 3) return;
        throw new NotSupportedException("sharpness 3 variance-based rd adjustment");
    }

    /// <summary>adjust_cost.</summary>
    internal static void AdjustCost(AomComp cpi, AomMacroblock x, ref long rdCost, bool isInterPred)
    {
        if ((cpi.Tuning == AOM_TUNE_IQ || cpi.Tuning == AOM_TUNE_SSIMULACRA2) && isInterPred)
        {
            rdCost += rdCost >> 3;
            return;
        }
        if (cpi.Sharpness != 3) return;
        throw new NotSupportedException("sharpness 3 variance-based rd adjustment");
    }

    /// <summary>get_sse.</summary>
    private static long GetSse(AomComp cpi, AomMacroblock x, out long sseY)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        long total = 0;
        sseY = 0;
        for (int plane = 0; plane < cpi.Cm.NumPlanes; ++plane)
        {
            if (plane != 0 && !xd.IsChromaRef) break;
            var p = x.Plane[plane];
            var pd = xd.Plane[plane];
            int bs = AomEncodeMb.PlaneBlockSize(mbmi.Bsize, pd.SubsamplingX, pd.SubsamplingY);
            uint sse;
            int w = BlockSizeWide[bs], h = BlockSizeHigh[bs];
            if (xd.IsHbd) AomHbd.Variance(p.Src.Buf16, p.Src.Offset, p.Src.Stride, pd.Dst.Buf16, pd.Dst.Offset, pd.Dst.Stride, 0, w, h, xd.Bd, out sse);
            else AomSad.Variance(p.Src.Buf, p.Src.Offset, p.Src.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride, w, h, out sse);
            total += sse;
            if (plane == 0) sseY = sse;
        }
        return total << 4;
    }

    /// <summary>av1_check_newmv_joint_nonzero.</summary>
    internal static bool CheckNewmvJointNonzero(AomCommon cm, AomMacroblock x)
    {
        var mbmi = x.E.Mi0;
        int thisMode = mbmi.Mode;
        if (thisMode == NEW_NEWMV)
        {
            var r0 = AomMotionSearch.GetRefMv(x, 0);
            var r1 = AomMotionSearch.GetRefMv(x, 1);
            if (mbmi.Mv0.AsInt == r0.AsInt || mbmi.Mv1.AsInt == r1.AsInt) return false;
        }
        else if (thisMode == NEAREST_NEWMV || thisMode == NEAR_NEWMV)
        {
            if (mbmi.Mv1.AsInt == AomMotionSearch.GetRefMv(x, 1).AsInt) return false;
        }
        else if (thisMode == NEW_NEARESTMV || thisMode == NEW_NEARMV)
        {
            if (mbmi.Mv0.AsInt == AomMotionSearch.GetRefMv(x, 0).AsInt) return false;
        }
        else if (thisMode == NEWMV)
        {
            if (mbmi.Mv0.AsInt == AomMotionSearch.GetRefMv(x, 0).AsInt) return false;
        }
        return true;
    }

    /// <summary>get_txfm_rd_gate_level.</summary>
    private static int GetTxfmRdGateLevel(bool isMaskedCompoundEnabled, int[] level, int bsize, int txSearchCase, bool evalMotionMode)
    {
        if (txSearchCase == TX_SEARCH_MOTION_MODE && !evalMotionMode && NumPelsLog2Lookup[bsize] > 8) return level[TX_SEARCH_MOTION_MODE];
        if (txSearchCase == TX_SEARCH_COMP_TYPE_MODE && isMaskedCompoundEnabled) return level[TX_SEARCH_COMP_TYPE_MODE];
        return level[TX_SEARCH_DEFAULT];
    }

    /// <summary>check_txfm_eval.</summary>
    internal static bool CheckTxfmEval(AomMacroblock x, int bsize, long bestSkipRd, long skipRd, int level, bool isLumaOnly)
    {
        int[] scale = { int.MaxValue, 4, 3, 2, 2, 1 };
        int qslope = 2 * (isLumaOnly ? 0 : 1);
        int[] levelToQindexMap = { 0, 0, 0, 80, 100, 140 };
        int aggrFactor = 4;
        int predQindexThresh = levelToQindexMap[level];
        if (!isLumaOnly && level <= 2)
            aggrFactor = 4 * Math.Max(1, (((255 - x.Qindex) * qslope) + (1 << 7)) >> 8);
        if (bestSkipRd > (long)(uint)(x.SourceVariance << (NumPelsLog2Lookup[bsize] + 7)) && x.Qindex >= predQindexThresh)
            aggrFactor *= scale[level];
        else if (level <= 1 && !isLumaOnly) aggrFactor = (aggrFactor >> 2) * 6;
        int[] lumaMul = { int.MaxValue, 32, 29, 17, 17, 17 };
        int mulFactor = isLumaOnly ? lumaMul[level] : 16;
        long rdThresh = bestSkipRd == long.MaxValue ? bestSkipRd : (bestSkipRd * aggrFactor * mulFactor) >> 6;
        return skipRd <= rdThresh;
    }

    internal static void CollectNeighborsRefCountsPublic(AomMacroblockD xd) => CollectNeighborsRefCounts(xd);

    /// <summary>av1_collect_neighbors_ref_counts.</summary>
    private static void CollectNeighborsRefCounts(AomMacroblockD xd)
    {
        var c = xd.NeighborsRefCounts;
        Array.Clear(c);
        var a = xd.AboveMbmi; var l = xd.LeftMbmi;
        if (xd.UpAvailable && a!.IsInterBlock)
        {
            c[a.RefFrame0]++;
            if (a.HasSecondRef) c[a.RefFrame1]++;
        }
        if (xd.LeftAvailable && l!.IsInterBlock)
        {
            c[l.RefFrame0]++;
            if (l.HasSecondRef) c[l.RefFrame1]++;
        }
    }
}
