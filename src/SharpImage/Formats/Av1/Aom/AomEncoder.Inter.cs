using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>AomEncodeInput's inter-frame parameters (what av1_encode_strategy / encode_frame_to_data_rate hand the
/// frame encode for an INTER_FRAME of the good-quality / real-time encoder).</summary>
internal sealed partial class AomEncodeInput
{
    /// <summary>The sequence header (good-quality / real-time; null for the all-intra still).</summary>
    public AomSeqHeader? Seq;
    public bool ShowFrame = true;
    /// <summary>current_frame.order_hint / display_order_hint, frame_number.</summary>
    public int OrderHint, DisplayOrderHint, FrameNumber;
    /// <summary>get_ref_frame_buf(cm, ref) per reference frame (index LAST_FRAME..ALTREF_FRAME).</summary>
    public AomRefBuffer?[]? RefBufs;
    /// <summary>cpi->ref_frame_flags as av1_encode_strategy hands them (get_ref_frame_flags).</summary>
    public int RefFrameFlags;
    /// <summary>ext_flags->use_ref_frame_mvs (features->allow_ref_frame_mvs before the frame-level checks).</summary>
    public bool UseRefFrameMvs = true;
    /// <summary>features->primary_ref_frame and its buffer (null: PRIMARY_REF_NONE).</summary>
    public AomRefBuffer? PrimaryRefBuf;
    /// <summary>ppi->frame_probs (persistent).</summary>
    public AomFrameProbs? FrameProbs;
    /// <summary>The source scaler's filter / phase (encode_without_recode): av1_scale_references reuses them.</summary>
    public int FilterScaler = EIGHTTAP_SMOOTH, PhaseScaler;
    /// <summary>oxcf->gf_cfg.lag_in_frames, number of spatial layers.</summary>
    public int LagInFrames, NumSpatialLayers = 1;
    /// <summary>cpi->rc.is_src_frame_alt_ref.</summary>
    public bool IsSrcFrameAltRef;
    /// <summary>features->allow_screen_content_tools etc. of the last intra frame (inter frames keep them).</summary>
    public bool KeepScreenContentFlags;
    /// <summary>cpi->refresh_frame.golden_frame.</summary>
    public bool RefreshGolden;
    /// <summary>ppi->filter_level (the previous frame's searched loop filter levels).</summary>
    public int[]? PpiFilterLevel;
    /// <summary>av1_is_resize_needed (a fixed resize mode from AOME_SET_SCALEMODE).</summary>
    public bool ResizeNeeded;
}

internal static partial class AomEncoder
{
    private const int AOM_LAST_FLAG = 1, AOM_LAST2_FLAG = 2, AOM_LAST3_FLAG = 4, AOM_GOLD_FLAG = 8, AOM_BWD_FLAG = 16, AOM_ALT2_FLAG = 32,
        AOM_ALT_FLAG = 64;

    private static int RefFlag(int refFrame) => 1 << (refFrame - LAST_FRAME);

    /// <summary>av1_encoder_get_relative_dist (display order hints, no wrap).</summary>
    private static int EncoderRelativeDist(int a, int b) => a - b;

    /// <summary>The inter-frame setup of encode_frame_to_data_rate / encode_without_recode / av1_setup_frame /
    /// av1_encode_frame before encode_frame_internal's tile encode: references and scale factors, the scaled
    /// references, the frame features, frame-level reference pruning, the motion field and skip mode.</summary>
    private static void SetupInterFrame(AomComp cpi, AomEncodeInput input)
    {
        var cm = cpi.Cm;
        var seq = input.Seq!;
        cm.FrameType = INTER_FRAME;
        cm.ShowFrame = input.ShowFrame;
        cm.OrderHint = input.OrderHint;
        cm.DisplayOrderHint = input.DisplayOrderHint;
        cm.EnableOrderHint = seq.EnableOrderHint;
        cm.OrderHintBits = seq.OrderHintBitsMinus1 + 1;
        cm.EnableRefFrameMvs = seq.EnableRefFrameMvs;
        cpi.FrameNumber = input.FrameNumber;
        cpi.IsSrcFrameAltRef = input.IsSrcFrameAltRef;
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++)
        {
            var b = input.RefBufs![r];
            cm.RefBufs[r] = b;
            // av1_setup_frame_size -> av1_set_frame_size: the scale factors of each reference
            cm.RefScaleFactors[r] = b != null ? AomScaleFactors.ForFrame(b.Buf.CropWidths[0], b.Buf.CropHeights[0], cm.Width, cm.Height) : null;
        }
        cpi.RefFrameFlags = input.RefFrameFlags;
        if (input.PpiFilterLevel != null) cpi.PpiFilterLevel = input.PpiFilterLevel;
        // encode_frame_internal: the loop filter deltas of the primary reference frame (none: the defaults)
        if (input.PrimaryRefBuf != null && input.BaseQindex != 0)
        {
            Array.Copy(input.PrimaryRefBuf.RefDeltas, cm.LfRefDeltas, 8);
            Array.Copy(input.PrimaryRefBuf.ModeDeltas, cm.LfModeDeltas, 2);
        }

        // encode_frame_to_data_rate: allow_ref_frame_mvs (set_ext_overrides) & frame_might_allow_ref_frame_mvs, allow_warped_motion
        cm.AllowRefFrameMvs = input.UseRefFrameMvs && seq.EnableRefFrameMvs && seq.EnableOrderHint;
        cm.AllowWarpedMotion = cpi.AllowWarpedMotionCfg && seq.EnableWarpedMotion;
        // cur_frame_force_integer_mv (screen content inter frames: av1_is_integer_mv)
        if (cpi.AllowScreenContentTools && cpi.Sf.rt_sf.use_nonrd_pick_mode == 0)
            throw new NotImplementedException("av1_is_integer_mv (screen content inter frames)");
        cm.CurFrameForceIntegerMv = false;

        // encode_without_recode: GOLDEN / ALTREF dropped from the flags when their size differs (one spatial layer)
        if (input.NumSpatialLayers == 1)
        {
            if ((cpi.RefFrameFlags & RefFlag(GOLDEN_FRAME)) != 0)
            {
                var r = cm.RefBufs[GOLDEN_FRAME];
                if (r == null || r.Buf.CropWidths[0] != cm.Width || r.Buf.CropHeights[0] != cm.Height) cpi.RefFrameFlags ^= AOM_GOLD_FLAG;
            }
            if ((cpi.RefFrameFlags & RefFlag(ALTREF_FRAME)) != 0)
            {
                var r = cm.RefBufs[ALTREF_FRAME];
                if (r == null || r.Buf.CropWidths[0] != cm.Width || r.Buf.CropHeights[0] != cm.Height) cpi.RefFrameFlags ^= AOM_ALT_FLAG;
            }
        }
        ScaleReferences(cpi, input.FilterScaler, input.PhaseScaler);

        // set_size_independent_vars: interp_filter SWITCHABLE, switchable_motion_mode
        cm.InterpFilter = SWITCHABLE;
        cm.SwitchableMotionMode = cm.AllowWarpedMotion || cpi.EnableObmc;
        // av1_setup_frame: lf deltas of the primary reference (none: av1_setup_past_independence's defaults)
    }

    /// <summary>av1_scale_references (use_optimized_scaler 1).</summary>
    private static void ScaleReferences(AomComp cpi, int filter, int phase)
    {
        var cm = cpi.Cm;
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++)
        {
            cpi.ScaledRefBufs[r] = null;
            if ((cpi.RefFrameFlags & RefFlag(r)) == 0) continue;
            var refBuf = cm.RefBufs[r];
            if (refBuf == null) continue;
            var reff = refBuf.Buf;
            if (reff.CropWidths[0] == cm.Width && reff.CropHeights[0] == cm.Height) continue;   // the reference itself
            var scaled = new AomFrameBuffer(cm.Width, cm.Height, reff.SsX, reff.SsY, reff.NumPlanes == 1, reff.BitDepth);
            bool opt = AomResize.HasOptimizedScaler(reff.CropWidths[0], reff.CropHeights[0], cm.Width, cm.Height);
            if (cm.NumPlanes > 1)
                opt = opt && AomResize.HasOptimizedScaler(reff.CropWidths[1], reff.CropHeights[1], scaled.CropWidths[1], scaled.CropHeights[1]);
            if (opt && cm.BitDepth == 8) AomResize.ResizeAndExtendFrame(reff, scaled, filter, phase);
            else AomResize.ResizeAndExtendFrameNonnormative(reff, scaled);
            cpi.ScaledRefBufs[r] = scaled;
        }
    }

    /// <summary>av1_encode_frame's pre-encode steps and encode_frame_internal's frame-level setup for an inter frame
    /// (after the quantizer and the frame CDFs are set, before av1_initialize_rd_consts).</summary>
    private static void PrepareInterFrameEncode(AomComp cpi, AomEncodeInput input)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        // av1_pick_and_set_high_precision_mv (no mv stats yet)
        int qindex = cm.BaseQindex;
        bool useHp = qindex < 128;   // HIGH_PRECISION_MV_QTHRESH
        if (sf.hl_sf.high_precision_mv_usage == QTR_ONLY) useHp = false;
        else if (sf.hl_sf.high_precision_mv_usage == LAST_MV_DATA && cpi.MvStatsValid)
            throw new NotImplementedException("get_smart_mv_prec");
        cm.AllowHighPrecisionMv = useHp && !cm.CurFrameForceIntegerMv;

        // av1_setup_frame_buf_refs, enforce_max_ref_frames, set_rel_frame_dist, av1_setup_frame_sign_bias
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++)
        {
            var b = cm.RefBufs[r];
            if (b == null) continue;
            cm.CurRefOrderHints[r - LAST_FRAME] = b.OrderHint;
            cpi.RefDisplayOrderHint[r - LAST_FRAME] = b.DisplayOrderHint;
        }
        EnforceMaxRefFrames(cpi);
        SetRelFrameDist(cpi);
        AomMvPred.SetupFrameSignBias(cm);

        // av1_encode_frame (frame_parameter_update): reference mode select, switchable interp filter / motion mode
        cm.ReferenceMode = REFERENCE_MODE_SELECT;
        cm.InterpFilter = SWITCHABLE;
        cm.SwitchableMotionMode = cm.AllowWarpedMotion || cpi.EnableObmc;

        // encode_frame_internal: warped motion pruned by its frame probability
        if (cm.AllowWarpedMotion && sf.inter_sf.prune_warped_prob_thresh > 0)
        {
            int warpedProbability = cpi.FrameProbs.WarpedProbs[cpi.UpdateType];
            if (warpedProbability < sf.inter_sf.prune_warped_prob_thresh) cm.AllowWarpedMotion = false;
        }
        cpi.DefaultInterpSkipFlags = cm.NumPlanes == 1 ? INTERP_SKIP_LUMA_EVAL_CHROMA : INTERP_SKIP_LUMA_SKIP_CHROMA;
        cpi.AllOneSidedRefs = RefsAreOneSided(cm, cpi);
        cpi.PruneRefFrameMask = 0;
        SetupPruneRefFrameMask(cpi);
        SetupKeepRefFrameMask(cpi);
        // av1_compute_global_motion_facade (identity unless a same-size reference is searched)
        ComputeGlobalMotionFacade(cpi);
        AomMvPred.CalculateRefFrameSide(cm);
        cm.AllowRefFrameMvs &= sf.hl_sf.ref_frame_mvs_lvl != 2;
        if (cm.AllowRefFrameMvs) AomMvPred.SetupMotionField(cm);
        // (check_to_disable_ref_frame_mvs: needs TPL stats)
        if (cm.AllowRefFrameMvs && sf.hl_sf.ref_frame_mvs_lvl == 1 && cpi.TplStatsReady)
            throw new NotImplementedException("check_to_disable_ref_frame_mvs");
        cm.SkipModeFlag = CheckSkipModeEnabled(cpi, input.LagInFrames);
        // the frame's mi-level motion storage and the inter mbmi_ext frame
        cpi.MbmiExtFrameInterBase = new AomMbmiExtFrameInter?[cm.MiGridBase.Length];
        cm.CurFrameMvs = new AomMvRefStore[((cm.MiRows + 1) >> 1) * ((cm.MiCols + 1) >> 1)];
        cpi.Rd.SetRdSpeedThresholds();
        cpi.Rd.SetBlockThresholds(cm.BaseQindex, cm.YDcDeltaQ, cm.BitDepth);
    }

    /// <summary>enforce_max_ref_frames (max_reference_frames 7).</summary>
    private static void EnforceMaxRefFrames(AomComp cpi)
    {
        var cm = cpi.Cm;
        int totalValidRefs = 0;
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++) if ((cpi.RefFrameFlags & RefFlag(r)) != 0) totalValidRefs++;
        // get_num_refs_to_disable
        int numRefsToDisable = 0;
        if (cpi.Sf.inter_sf.selective_ref_frame >= 3)
        {
            numRefsToDisable++;
            if (cpi.Sf.inter_sf.selective_ref_frame >= 6) numRefsToDisable += 2;
            else if (cpi.Sf.inter_sf.selective_ref_frame == 5 && (cpi.RefFrameFlags & RefFlag(LAST2_FRAME)) != 0)
            {
                int last2Dist = EncoderRelativeDist(cpi.RefDisplayOrderHint[LAST2_FRAME - LAST_FRAME], cm.DisplayOrderHint);
                if (Math.Abs(last2Dist) > 2) numRefsToDisable++;
            }
        }
        int maxAllowedRefs = Math.Min(INTER_REFS_PER_FRAME - numRefsToDisable, 7);
        ReadOnlySpan<int> disableOrder = stackalloc int[] { LAST3_FRAME, LAST2_FRAME, ALTREF2_FRAME, BWDREF_FRAME };
        for (int i = 0; i < 4 && totalValidRefs > maxAllowedRefs; ++i)
        {
            int rf = disableOrder[i];
            if ((cpi.RefFrameFlags & RefFlag(rf)) == 0) continue;
            switch (rf)
            {
                case LAST3_FRAME: cpi.RefFrameFlags &= ~AOM_LAST3_FLAG; break;
                case LAST2_FRAME: cpi.RefFrameFlags &= ~AOM_LAST2_FLAG; break;
                case ALTREF2_FRAME: cpi.RefFrameFlags &= ~AOM_ALT2_FLAG; break;
                case BWDREF_FRAME: cpi.RefFrameFlags &= ~AOM_GOLD_FLAG; break;   // (libaom clears the GOLDEN flag here)
            }
            --totalValidRefs;
        }
    }

    /// <summary>set_rel_frame_dist.</summary>
    private static void SetRelFrameDist(AomComp cpi)
    {
        var info = cpi.RefFrameDistInfo;
        int minPast = int.MaxValue, minFuture = int.MaxValue;
        info.NearestPastRef = NONE_FRAME;
        info.NearestFutureRef = NONE_FRAME;
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; ++r)
        {
            info.RefRelativeDist[r - LAST_FRAME] = 0;
            if ((cpi.RefFrameFlags & RefFlag(r)) == 0) continue;
            int dist = EncoderRelativeDist(cpi.RefDisplayOrderHint[r - LAST_FRAME], cpi.Cm.DisplayOrderHint);
            info.RefRelativeDist[r - LAST_FRAME] = dist;
            if (Math.Abs(dist) < minPast && dist < 0) { info.NearestPastRef = r; minPast = Math.Abs(dist); }
            if (dist < minFuture && dist > 0) { info.NearestFutureRef = r; minFuture = dist; }
        }
    }

    /// <summary>refs_are_one_sided.</summary>
    private static bool RefsAreOneSided(AomCommon cm, AomComp cpi)
    {
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; ++r)
        {
            var b = cm.RefBufs[r];
            if (b == null) continue;
            if (EncoderRelativeDist(b.DisplayOrderHint, cm.DisplayOrderHint) > 0) return false;
        }
        return true;
    }

    /// <summary>setup_prune_ref_frame_mask.</summary>
    private static void SetupPruneRefFrameMask(AomComp cpi)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        if ((!cpi.EnableOnesidedComp || sf.inter_sf.disable_onesided_comp != 0) && cpi.AllOneSidedRefs)
        {
            cpi.PruneRefFrameMask = (1 << MODE_CTX_REF_FRAMES) - (1 << REF_FRAMES);
            return;
        }
        if (sf.rt_sf.use_nonrd_pick_mode != 0 || sf.inter_sf.selective_ref_frame < 2) return;
        int cur = cm.DisplayOrderHint;
        int arf2Dist = EncoderRelativeDist(cpi.RefDisplayOrderHint[ALTREF2_FRAME - LAST_FRAME], cur);
        int bwdDist = EncoderRelativeDist(cpi.RefDisplayOrderHint[BWDREF_FRAME - LAST_FRAME], cur);
        for (int refIdx = REF_FRAMES; refIdx < MODE_CTX_REF_FRAMES; ++refIdx)
        {
            var (rf0, rf1) = AomInter.SetRefFrame(refIdx);
            if ((cpi.RefFrameFlags & RefFlag(rf0)) == 0 || (cpi.RefFrameFlags & RefFlag(rf1)) == 0) continue;
            if (!cpi.AllOneSidedRefs)
            {
                int d0 = EncoderRelativeDist(cpi.RefDisplayOrderHint[rf0 - LAST_FRAME], cur);
                int d1 = EncoderRelativeDist(cpi.RefDisplayOrderHint[rf1 - LAST_FRAME], cur);
                if ((d0 > 0) == (d1 > 0)) cpi.PruneRefFrameMask |= 1 << refIdx;
            }
            if (sf.inter_sf.selective_ref_frame >= 4 && (rf0 == ALTREF2_FRAME || rf1 == ALTREF2_FRAME) &&
                (cpi.RefFrameFlags & RefFlag(BWDREF_FRAME)) != 0)
            {
                if (arf2Dist > 0 && bwdDist > 0 && bwdDist <= arf2Dist) cpi.PruneRefFrameMask |= 1 << refIdx;
            }
        }
    }

    /// <summary>setup_keep_ref_frame_mask.</summary>
    private static void SetupKeepRefFrameMask(AomComp cpi)
    {
        var cm = cpi.Cm;
        int pruneSingleRef = cpi.Sf.inter_sf.prune_single_ref, pruneCompRefFrames = cpi.Sf.inter_sf.prune_comp_ref_frames;
        cpi.KeepSingleRefFrameMask = 0;
        cpi.KeepCompRefFrameMask = 0;
        Span<int> score = stackalloc int[INTER_REFS_PER_FRAME];
        Span<int> index = stackalloc int[INTER_REFS_PER_FRAME];
        for (int i = 0; i < INTER_REFS_PER_FRAME; ++i) { score[i] = int.MaxValue; index[i] = i; }
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; ++r)
            if ((cpi.RefFrameFlags & RefFlag(r)) != 0)
                score[r - LAST_FRAME] = Math.Abs(cpi.RefFrameDistInfo.RefRelativeDist[r - LAST_FRAME]) + cm.RefBufs[r]!.BaseQindex;
        // qsort with compare_score_data_asc (score, then index): a stable total order
        for (int i = 1; i < INTER_REFS_PER_FRAME; i++)
            for (int j = i; j > 0 && (score[j - 1] > score[j] || (score[j - 1] == score[j] && index[j - 1] > index[j])); j--)
            {
                (score[j - 1], score[j]) = (score[j], score[j - 1]);
                (index[j - 1], index[j]) = (index[j], index[j - 1]);
            }
        ReadOnlySpan<int> numSingleToKeep = stackalloc int[] { INTER_REFS_PER_FRAME, 5, 3, 0, 0 };
        for (int i = 0; i < numSingleToKeep[pruneSingleRef]; ++i) cpi.KeepSingleRefFrameMask |= 1 << index[i];
        ReadOnlySpan<int> numCompToKeep = stackalloc int[] { INTER_REFS_PER_FRAME, 3, 0, 0 };
        for (int i = 0; i < numCompToKeep[pruneCompRefFrames]; ++i) cpi.KeepCompRefFrameMask |= 1 << index[i];
    }

    /// <summary>av1_compute_global_motion_facade's reference selection: the search itself is not ported yet, so it
    /// fails when a reference would be searched.</summary>
    private static void ComputeGlobalMotionFacade(AomComp cpi)
    {
        var cm = cpi.Cm;
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++) cm.GlobalMotion[r].CopyFrom(AomWarpedMotionParams.Default);
        if (!cpi.EnableGlobalMotion || cpi.Sf.gm_sf.gm_search_type == GM_DISABLE_SEARCH) return;
        for (int frame = ALTREF_FRAME; frame >= LAST_FRAME; --frame)
        {
            var buf = cm.RefBufs[frame];
            bool refDisabled = (cpi.RefFrameFlags & RefFlag(frame)) == 0;
            if (buf == null || (refDisabled && cpi.Sf.hl_sf.recode_loop != DISALLOW_RECODE)) continue;
            if (buf.Buf.CropWidths[0] == cpi.Source.CropWidths[0] && buf.Buf.CropHeights[0] == cpi.Source.CropHeights[0])
                throw new NotImplementedException("global motion estimation");
        }
    }

    /// <summary>check_skip_mode_enabled (av1_setup_skip_mode_allowed with REFERENCE_MODE_SELECT).</summary>
    private static bool CheckSkipModeEnabled(AomComp cpi, int lagInFrames)
    {
        var cm = cpi.Cm;
        cm.SkipModeAllowed = false;
        cm.SkipModeRefFrame0 = cm.SkipModeRefFrame1 = -1;
        if (!cm.EnableOrderHint || cm.FrameIsIntraOnly || cm.ReferenceMode == SINGLE_REFERENCE) return false;
        int curOrderHint = cm.OrderHint;
        int refHint0 = -1, refHint1 = int.MaxValue, idx0 = -1, idx1 = -1;
        for (int i = 0; i < INTER_REFS_PER_FRAME; ++i)
        {
            var b = cm.RefBufs[LAST_FRAME + i];
            if (b == null) continue;
            int h = b.OrderHint;
            if (cm.RelativeDist(h, curOrderHint) < 0)
            {
                if (refHint0 == -1 || cm.RelativeDist(h, refHint0) > 0) { refHint0 = h; idx0 = i; }
            }
            else if (cm.RelativeDist(h, curOrderHint) > 0)
            {
                if (refHint1 == int.MaxValue || cm.RelativeDist(h, refHint1) < 0) { refHint1 = h; idx1 = i; }
            }
        }
        if (idx0 != -1 && idx1 != -1)
        {
            cm.SkipModeAllowed = true;
            cm.SkipModeRefFrame0 = Math.Min(idx0, idx1);
            cm.SkipModeRefFrame1 = Math.Max(idx0, idx1);
        }
        else if (idx0 != -1 && idx1 == -1)
        {
            refHint1 = -1;
            for (int i = 0; i < INTER_REFS_PER_FRAME; ++i)
            {
                var b = cm.RefBufs[LAST_FRAME + i];
                if (b == null) continue;
                int h = b.OrderHint;
                if (refHint0 != -1 && cm.RelativeDist(h, refHint0) < 0 && (refHint1 == -1 || cm.RelativeDist(h, refHint1) > 0))
                {
                    refHint1 = h;
                    idx1 = i;
                }
            }
            if (refHint1 != -1)
            {
                cm.SkipModeAllowed = true;
                cm.SkipModeRefFrame0 = Math.Min(idx0, idx1);
                cm.SkipModeRefFrame1 = Math.Max(idx0, idx1);
            }
        }
        if (!cm.SkipModeAllowed) return false;
        // the reference pair's temporal distances may differ by at most 1
        int o0 = cm.RefBufs[LAST_FRAME + cm.SkipModeRefFrame0]!.OrderHint, o1 = cm.RefBufs[LAST_FRAME + cm.SkipModeRefFrame1]!.OrderHint;
        int curToRef0 = cm.RelativeDist(curOrderHint, o0);
        int curToRef1 = Math.Abs(cm.RelativeDist(curOrderHint, o1));
        if (Math.Abs(curToRef0 - curToRef1) > 1) return false;
        if (cpi.AllOneSidedRefs && lagInFrames > 0) return false;
        if ((cpi.RefFrameFlags & RefFlag(cm.SkipModeRefFrame0 + LAST_FRAME)) == 0 ||
            (cpi.RefFrameFlags & RefFlag(cm.SkipModeRefFrame1 + LAST_FRAME)) == 0) return false;
        return true;
    }

    /// <summary>encode_frame_internal's post-encode frame probability updates and av1_encode_frame's reference mode /
    /// skip mode / tx mode adjustments for an inter frame.</summary>
    private static void FinishInterFrame(AomComp cpi, AomMacroblock x)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        int updateType = cpi.UpdateType;
        // obmc probabilities
        if (sf.inter_sf.prune_obmc_prob_thresh > 0 && sf.inter_sf.prune_obmc_prob_thresh < int.MaxValue)
            for (int i = 0; i < BLOCK_SIZES_ALL; i++)
            {
                int sum = x.ObmcUsed[i * 2] + x.ObmcUsed[i * 2 + 1];
                int newProb = sum != 0 ? 128 * x.ObmcUsed[i * 2 + 1] / sum : 0;
                ref int p = ref cpi.FrameProbs.ObmcProbs[updateType * BLOCK_SIZES_ALL + i];
                p = (p + newProb) >> 1;
            }
        if (cm.AllowWarpedMotion && sf.inter_sf.prune_warped_prob_thresh > 0)
        {
            int sum = x.WarpedUsed[0] + x.WarpedUsed[1];
            int newProb = sum != 0 ? 128 * x.WarpedUsed[1] / sum : 0;
            ref int p = ref cpi.FrameProbs.WarpedProbs[updateType];
            p = (p + newProb) >> 1;
        }
        if (cm.FrameType != KEY_FRAME && sf.interp_sf.adaptive_interp_filter_search == 2 && cm.InterpFilter == SWITCHABLE)
            for (int i = 0; i < SWITCHABLE_FILTER_CONTEXTS; i++)
            {
                int sum = 0, left = 1536;
                for (int j = 0; j < SWITCHABLE_FILTERS; j++) sum += x.Counts.SwitchableInterp[i * SWITCHABLE_FILTERS + j];
                for (int j = SWITCHABLE_FILTERS - 1; j >= 0; j--)
                {
                    int newProb = sum != 0 ? 1536 * x.Counts.SwitchableInterp[i * SWITCHABLE_FILTERS + j] / sum : (j != 0 ? 0 : 1536);
                    ref int p = ref cpi.FrameProbs.SwitchableInterpProbs[(updateType * SWITCHABLE_FILTER_CONTEXTS + i) * SWITCHABLE_FILTERS + j];
                    int prob = (p + newProb) >> 1;
                    left -= prob;
                    if (j == 0) prob += left;
                    p = prob;
                }
            }
        // av1_encode_frame: reference mode / skip mode
        if (cm.ReferenceMode == REFERENCE_MODE_SELECT && !x.CompoundRefUsedFlag) cm.ReferenceMode = SINGLE_REFERENCE;
        if (cm.FrameIsIntraOnly || cm.ReferenceMode == SINGLE_REFERENCE)
        {
            cm.SkipModeAllowed = false;
            cm.SkipModeFlag = false;
        }
        if (cm.SkipModeFlag && !x.SkipModeUsedFlag) cm.SkipModeFlag = false;
        // av1_finalize_encoded_frame: fix_interp_filter (one filter used: signalled at frame level)
        if (cm.InterpFilter == SWITCHABLE)
        {
            Span<int> count = stackalloc int[SWITCHABLE_FILTERS];
            int numFiltersUsed = 0;
            for (int i = 0; i < SWITCHABLE_FILTERS; ++i)
            {
                for (int j = 0; j < SWITCHABLE_FILTER_CONTEXTS; ++j) count[i] += x.Counts.SwitchableInterp[j * SWITCHABLE_FILTERS + i];
                if (count[i] > 0) numFiltersUsed++;
            }
            if (numFiltersUsed == 1)
                for (int i = 0; i < SWITCHABLE_FILTERS; ++i)
                    if (count[i] != 0) { cm.InterpFilter = i; break; }
        }
    }

    /// <summary>encode_frame_internal's tx type probability update (prune_tx_type_using_stats or a fast inter tx type
    /// probability threshold): frame_probs.tx_type_probs[update_type] averaged with this frame's usage.</summary>
    private static void UpdateTxTypeProbs(AomComp cpi, AomMacroblock x)
    {
        var ts = cpi.Sf.tx_sf.tx_type_search;
        if (!(ts.prune_tx_type_using_stats != 0 || (ts.fast_inter_tx_type_prob_thresh != int.MaxValue && ts.fast_inter_tx_type_prob_thresh != 0)))
            return;
        const int MAX_TX_TYPE_PROB = 1024;
        for (int i = 0; i < TX_SIZES_ALL; i++)
        {
            int sum = 0, left = MAX_TX_TYPE_PROB;
            for (int j = 0; j < TX_TYPES; j++) sum += x.TxTypeUsed[i * TX_TYPES + j];
            int off = cpi.TxTypeProbsOffset(i);
            for (int j = TX_TYPES - 1; j >= 0; j--)
            {
                int newProb = sum != 0 ? (int)((long)MAX_TX_TYPE_PROB * x.TxTypeUsed[i * TX_TYPES + j] / sum) : (j != 0 ? 0 : MAX_TX_TYPE_PROB);
                int prob = (cpi.TxTypeProbs[off + j] + newProb) >> 1;
                left -= prob;
                if (j == 0) prob += left;
                cpi.TxTypeProbs[off + j] = prob;
            }
        }
    }
}
