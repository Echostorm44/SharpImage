using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomSpeedFeatureInputs
{
    // REALTIME state the speed features read
    public int RcMode = AOM_Q;                    // oxcf.rc_cfg.mode
    public int KeyFreqMax = 9999;                 // oxcf.kf_cfg.key_freq_max
    public int LagInFrames;                       // oxcf.gf_cfg.lag_in_frames
    public int NumSpatialLayers = 1;              // cpi->svc.number_spatial_layers
    public int AqMode;                            // oxcf.q_cfg.aq_mode (NO_AQ)
    public bool EnableWarpedMotion = true;        // oxcf.motion_mode_cfg.enable_warped_motion
    public bool HighSourceSad;                    // cpi->rc.high_source_sad
    public bool RefreshAltRef;                    // cpi->refresh_frame.alt_ref_frame
    public bool IsSrcFrameAltRef;                 // cpi->rc.is_src_frame_alt_ref
    public bool IsOnePassRtLagParams => Pass == AOM_RC_ONE_PASS && LagInFrames > 0 && Mode == REALTIME;
}

// Port of libaom 3.14.1 speed_features.c for oxcf->mode == REALTIME: set_rt_speed_feature_framesize_dependent and
// set_rt_speed_features_framesize_independent (no SVC / rtc reference config, content default, no superres, no
// active map, PSNR not computed).
internal sealed partial class AomSpeedFeatures
{
    /// <summary>set_rt_speed_feature_framesize_dependent.</summary>
    private static void SetRtFramesizeDependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
    {
        bool boosted = cpi.FrameIsBoosted;
        int minDim = Math.Min(cpi.Width, cpi.Height);
        bool is_1080p_or_larger = minDim >= 1080, is_720p_or_larger = minDim >= 720, is_480p_or_larger = minDim >= 480,
            is_360p_or_larger = minDim >= 360;
        var rt = sf.rt_sf;

        if (!is_360p_or_larger)
        {
            rt.prune_intra_mode_based_on_mv_range = 1;
            rt.prune_inter_modes_wrt_gf_arf_based_on_sad = 1;
            if (speed >= 6) sf.winner_mode_sf.prune_winner_mode_eval_level = boosted ? 0 : 2;
            if (speed == 7) rt.prefer_large_partition_blocks = 2;
            if (speed >= 7)
            {
                sf.lpf_sf.cdef_pick_method = CDEF_PICK_FROM_Q;
                rt.check_only_zero_zeromv_on_large_blocks = true;
                rt.use_rtc_tf = 2;
            }
            if (speed == 8) rt.prefer_large_partition_blocks = 1;
            if (speed >= 8)
            {
                rt.use_nonrd_filter_search = 1;
                rt.tx_size_level_based_on_qstep = 1;
            }
            if (speed >= 9)
            {
                rt.use_comp_ref_nonrd = 0;
                rt.nonrd_aggressive_skip = 1;
                rt.skip_intra_pred = 1;
                // Only turn on enable_ref_short_signaling for low resolution when only LAST and GOLDEN ref frames are used.
                rt.enable_ref_short_signaling = rt.use_nonrd_altref_frame == 0 &&
                    (rt.use_comp_ref_nonrd == 0 || (rt.ref_frame_comp_nonrd[1] == 0 && rt.ref_frame_comp_nonrd[2] == 0));
                rt.use_adaptive_subpel_search = false;
            }
            if (speed >= 10)
            {
                rt.estimate_motion_for_var_based_partition = 0;
                rt.skip_intra_pred = 2;
                rt.hybrid_intra_pickmode = 3;
                rt.reduce_mv_pel_precision_lowcomplex = 1;
                rt.reduce_mv_pel_precision_highmotion = 2;
                rt.use_nonrd_filter_search = 0;
            }
        }
        else
        {
            rt.prune_intra_mode_based_on_mv_range = 2;
            sf.intra_sf.skip_filter_intra_in_inter_frames = 1;
            if (speed <= 5)
            {
                sf.tx_sf.tx_type_search.fast_inter_tx_type_prob_thresh = boosted ? int.MaxValue : 350;
                sf.winner_mode_sf.prune_winner_mode_eval_level = boosted ? 0 : 2;
            }
            if (speed == 6) sf.part_sf.disable_8x8_part_based_on_qidx = 1;
            if (speed >= 6) rt.skip_newmv_mode_based_on_sse = 2;
            if (speed == 7)
            {
                rt.prefer_large_partition_blocks = 1;
                // Enable this feature for [360p, 720p] resolution range initially (no external rate control, not high
                // bit depth).
                if (minDim <= 720 && !cpi.UseHighBitDepth) sf.hl_sf.accurate_bit_estimate = cpi.AqMode == NO_AQ ? 1 : 0;
            }
            if (speed >= 7) rt.use_rtc_tf = 1;
            if (speed == 8)   // !use_svc
            {
                rt.short_circuit_low_temp_var = 0;
                rt.use_nonrd_altref_frame = 1;
            }
            if (speed >= 8) rt.tx_size_level_based_on_qstep = 2;
            if (speed >= 9)
            {
                rt.gf_length_lvl = 1;
                rt.skip_cdef_sb = 1;
                rt.sad_based_adp_altref_lag = 2;
                rt.reduce_mv_pel_precision_highmotion = 2;
                rt.use_adaptive_subpel_search = true;
                sf.interp_sf.cb_pred_filter_search = 1;
            }
            if (speed >= 10)
            {
                rt.hybrid_intra_pickmode = 2;
                rt.sad_based_adp_altref_lag = 4;
                rt.tx_size_level_based_on_qstep = 0;
                rt.reduce_mv_pel_precision_highmotion = 3;
                rt.use_adaptive_subpel_search = false;
                sf.interp_sf.cb_pred_filter_search = 2;
            }
        }
        if (!is_480p_or_larger)
        {
            if (speed == 7) rt.nonrd_check_partition_merge_mode = 2;
        }
        if (!is_720p_or_larger)
        {
            if (speed >= 9) rt.force_large_partition_blocks_intra = 1;
        }
        else
        {
            if (speed >= 6) rt.skip_newmv_mode_based_on_sse = 3;
            if (speed == 7) rt.prefer_large_partition_blocks = 0;
            if (speed >= 7)
            {
                rt.reduce_mv_pel_precision_lowcomplex = 2;
                rt.reduce_mv_pel_precision_highmotion = 1;
            }
            if (speed >= 9)
            {
                rt.sad_based_adp_altref_lag = 1;
                rt.reduce_mv_pel_precision_lowcomplex = 0;
                rt.reduce_mv_pel_precision_highmotion = 2;
            }
            if (speed >= 10)
            {
                rt.sad_based_adp_altref_lag = 3;
                rt.reduce_mv_pel_precision_highmotion = 3;
            }
        }
        // TODO(Any): Check/Tune settings of other sfs for 1080p.
        if (is_1080p_or_larger)
        {
            if (speed >= 7)
            {
                rt.reduce_mv_pel_precision_highmotion = 0;
                rt.use_adaptive_subpel_search = false;
            }
            if (speed >= 9) sf.interp_sf.cb_pred_filter_search = 0;
        }
        else
        {
            if (speed >= 9) sf.lpf_sf.cdef_pick_method = CDEF_PICK_FROM_Q;
            if (speed >= 10) rt.nonrd_aggressive_skip = 1;
        }
        // TODO(marpan): Tune settings for speed 11 video mode, for content not screen.
        if (speed >= 11)
        {
            rt.skip_cdef_sb = 1;
            rt.force_only_last_ref = 1;
            rt.selective_cdf_update = 1;
            rt.use_nonrd_filter_search = 0;
            if (is_360p_or_larger)
            {
                sf.part_sf.fixed_partition_size = BLOCK_32X32;
                rt.use_fast_fixed_part = 1;
                rt.reduce_mv_pel_precision_lowcomplex = 2;
            }
            rt.increase_source_sad_thresh = 1;
            rt.part_early_exit_zeromv = 2;
            rt.set_zeromv_skip_based_on_source_sad = 2;
            for (int i = 0; i < BLOCK_SIZES; ++i) rt.intra_y_mode_bsize_mask_nrd[i] = INTRA_DC;
            rt.hybrid_intra_pickmode = 0;
        }
        // (use_svc / rtc_ref.set_ref_frame_config, screen content, superres, PSNR, active maps: not used)
        if (cpi.IsLosslessRequested)
        {
            rt.use_rtc_tf = 0;
            sf.hl_sf.accurate_bit_estimate = 0;
        }
        if (cpi.UseHighBitDepth) rt.estimate_motion_for_var_based_partition = 0;
        if (cpi.NumSpatialLayers > 1) rt.use_rtc_tf = 0;
        if (cpi.IsOnePassRtLagParams)
        {
            if (cpi.RefreshAltRef)
            {
                rt.source_metrics_sb_nonrd = 0;
                rt.var_part_based_on_qidx = 0;
            }
            rt.use_nonrd_altref_frame = 1;
            rt.use_rtc_tf = 0;
            rt.nonrd_check_partition_merge_mode = 0;
            rt.nonrd_check_partition_split = 0;
            if (cpi.IsSrcFrameAltRef)
            {
                rt.increase_source_sad_thresh = 0;
                rt.part_early_exit_zeromv = 0;
            }
            rt.gf_refresh_based_on_qp = 0;
        }
    }

    /// <summary>set_rt_speed_features_framesize_independent.</summary>
    private static void SetRtFramesizeIndependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
    {
        bool boosted = cpi.FrameIsBoosted;
        var rt = sf.rt_sf;
        var inter = sf.inter_sf;
        var part = sf.part_sf;
        var tx = sf.tx_sf;

        // Currently, rt speed 0, 1, 2, 3, 4, 5 are the same.
        // Following set of speed features are not impacting encoder's decisions as the relevant tools are disabled by
        // default.
        sf.gm_sf.gm_search_type = GM_DISABLE_SEARCH;
        sf.hl_sf.recode_loop = ALLOW_RECODE_KFARFGF;
        inter.reuse_inter_intra_mode = 1;
        inter.prune_compound_using_single_ref = 0;
        inter.prune_comp_search_by_single_result = 2;
        inter.prune_comp_type_by_comp_avg = 2;
        inter.fast_wedge_sign_estimate = 1;
        inter.use_dist_wtd_comp_flag = DIST_WTD_COMP_DISABLED;
        inter.mv_cost_upd_level = INTERNAL_COST_UPD_SBROW;
        inter.disable_interinter_wedge_var_thresh = 100;
        sf.interp_sf.cb_pred_filter_search = 0;
        sf.interp_sf.skip_interp_filter_search = 1;
        part.ml_prune_partition = 1;
        part.reuse_prev_rd_results_for_part_ab = 1;
        part.prune_ext_partition_types_search_level = 2;
        part.less_rectangular_check_level = 2;
        sf.mv_sf.obmc_full_pixel_search_level = 1;
        sf.intra_sf.dv_cost_upd_level = INTERNAL_COST_UPD_OFF;
        tx.model_based_prune_tx_search_level = 0;
        sf.lpf_sf.dual_sgr_penalty_level = 1;
        // Disable Wiener and Self-guided Loop restoration filters.
        sf.lpf_sf.disable_wiener_filter = true;
        sf.lpf_sf.disable_sgr_filter = true;
        sf.intra_sf.prune_palette_search_level = 2;
        sf.intra_sf.prune_luma_palette_size_search_level = 2;
        sf.intra_sf.early_term_chroma_palette_size_search = 1;

        // End of set

        // TODO(any, yunqing): tune these features for real-time use cases.
        sf.hl_sf.superres_auto_search_type = SUPERRES_AUTO_SOLO;
        sf.hl_sf.frame_parameter_update = 0;

        inter.model_based_post_interp_filter_breakout = 1;
        // TODO(any): As per the experiments, this speed feature is doing redundant computation since the model rd based
        // pruning logic is similar to model rd based gating when inter_mode_rd_model_estimation = 2. Enable this SF if
        // either of the condition becomes true.
        //    (1) inter_mode_rd_model_estimation != 2
        //    (2) skip_interp_filter_search == 0
        //    (3) Motion mode or compound mode is enabled */
        inter.prune_mode_search_simple_translation = 0;
        inter.prune_ref_frame_for_rect_partitions = boosted ? 0 : 1;
        inter.disable_interintra_wedge_var_thresh = UINT_MAX;
        inter.selective_ref_frame = 4;
        inter.alt_ref_search_fp = 2;
        SetTxfmRdGateLevel(inter.txfm_rd_gate_level, boosted ? 0 : 4);
        inter.limit_txfm_eval_per_mode = 3;

        inter.adaptive_rd_thresh = 4;
        inter.inter_mode_rd_model_estimation = 2;
        inter.prune_inter_modes_if_skippable = 1;
        inter.prune_nearmv_using_neighbors = PRUNE_NEARMV_LEVEL3;
        inter.reduce_inter_modes = boosted ? 1 : 3;
        inter.skip_newmv_in_drl = 4;

        sf.interp_sf.use_fast_interpolation_filter_search = 1;
        sf.interp_sf.use_interp_filter = 1;
        sf.interp_sf.adaptive_interp_filter_search = 1;
        sf.interp_sf.disable_dual_filter = 1;

        part.default_max_partition_size = BLOCK_128X128;
        part.default_min_partition_size = BLOCK_8X8;
        part.use_best_rd_for_pruning = 1;
        part.early_term_after_none_split = 1;
        part.partition_search_breakout_dist_thr = 1 << 25;
        part.max_intra_bsize = BLOCK_16X16;
        part.partition_search_breakout_rate_thr = 500;
        part.partition_search_type = VAR_BASED_PARTITION;
        part.adjust_var_based_rd_partitioning = 2;

        sf.mv_sf.full_pixel_search_level = 1;
        sf.mv_sf.exhaustive_searches_thresh = int.MaxValue;
        sf.mv_sf.auto_mv_step_size = 1;
        sf.mv_sf.subpel_iters_per_step = 1;
        sf.mv_sf.use_accurate_subpel_search = USE_2_TAPS;
        sf.mv_sf.search_method = FAST_DIAMOND;
        sf.mv_sf.subpel_force_stop = EIGHTH_PEL;
        sf.mv_sf.subpel_search_method = SUBPEL_TREE_PRUNED;

        for (int i = 0; i < TX_SIZES; ++i)
        {
            sf.intra_sf.intra_y_mode_mask[i] = INTRA_DC;
            sf.intra_sf.intra_uv_mode_mask[i] = UV_INTRA_DC_CFL;
        }
        sf.intra_sf.skip_intra_in_interframe = 5;
        sf.intra_sf.disable_smooth_intra = 1;
        sf.intra_sf.skip_filter_intra_in_inter_frames = 1;

        tx.intra_tx_size_search_init_depth_sqr = 1;
        tx.tx_type_search.use_reduced_intra_txset = 1;
        tx.adaptive_txb_search_level = 2;
        tx.intra_tx_size_search_init_depth_rect = 1;
        tx.tx_size_search_lgr_block = 1;
        tx.tx_type_search.ml_tx_split_thresh = 4000;
        tx.tx_type_search.skip_tx_search = 1;
        tx.inter_tx_size_search_init_depth_rect = 1;
        tx.inter_tx_size_search_init_depth_sqr = 1;
        tx.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_3;
        tx.refine_fast_tx_search_results = 0;
        tx.tx_type_search.fast_intra_tx_type_search = 2;
        tx.tx_type_search.use_skip_flag_prediction = 2;
        tx.tx_type_search.winner_mode_tx_type_pruning = 4;
        tx.use_chroma_trellis_rd_mult = 1;

        sf.rd_sf.optimize_coefficients = NO_TRELLIS_OPT;
        sf.rd_sf.simple_model_rd_from_var = 1;
        sf.rd_sf.tx_domain_dist_level = 2;
        sf.rd_sf.tx_domain_dist_thres_level = 2;

        sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL4;
        sf.lpf_sf.lpf_pick = LPF_PICK_FROM_Q;

        sf.winner_mode_sf.dc_blk_pred_level = cpi.FrameIsIntraOnly ? 0 : 3;
        sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch = 1;
        sf.winner_mode_sf.tx_size_search_level = 1;
        sf.winner_mode_sf.winner_mode_ifs = 1;

        rt.check_intra_pred_nonrd = 1;
        rt.estimate_motion_for_var_based_partition = 2;
        rt.hybrid_intra_pickmode = 1;
        rt.use_comp_ref_nonrd = 0;
        rt.ref_frame_comp_nonrd[0] = 0;
        rt.ref_frame_comp_nonrd[1] = 0;
        rt.ref_frame_comp_nonrd[2] = 0;
        rt.use_nonrd_filter_search = 1;
        rt.mode_search_skip_flags |= FLAG_SKIP_INTRA_DIRMISMATCH;
        rt.num_inter_modes_for_tx_search = 5;
        rt.prune_inter_modes_using_temp_var = 1;
        rt.use_real_time_ref_set = cpi.IsOnePassRtLagParams ? 0 : 1;
        rt.use_simple_rd_model = 1;
        rt.prune_inter_modes_with_golden_ref = boosted ? 0 : 1;
        // TODO(any): This sf could be removed.
        rt.short_circuit_low_temp_var = 1;
        rt.check_scene_detection = 1;   // (no external rate control)
        if (cpi.FrameType != KEY_FRAME && cpi.RcMode == AOM_CBR) rt.overshoot_detection_cbr = FAST_DETECTION_MAXQ;
        // Enable noise estimation only for high resolutions for now.
        //
        // Since use_temporal_noise_estimate has no effect for all-intra frame encoding, it is disabled for this case.
        if (cpi.KeyFreqMax != 0 && cpi.Width * cpi.Height > 640 * 480) rt.use_temporal_noise_estimate = 1;
        rt.skip_tx_no_split_var_based_partition = 1;
        rt.skip_newmv_mode_based_on_sse = 1;
        rt.mode_search_skip_flags = cpi.FrameType == KEY_FRAME ? 0
            : FLAG_SKIP_INTRA_DIRMISMATCH | FLAG_SKIP_INTRA_BESTINTER | FLAG_SKIP_COMP_BESTINTRA | FLAG_SKIP_INTRA_LOWVAR | FLAG_EARLY_TERMINATE;
        rt.var_part_split_threshold_shift = 5;
        if (!cpi.FrameIsIntraOnly) rt.var_part_based_on_qidx = 1;
        rt.use_fast_fixed_part = 0;
        rt.increase_source_sad_thresh = 0;

        if (cpi.IsOnePassRtLagParams && speed <= 6)
        {
            sf.hl_sf.frame_parameter_update = 1;
            inter.use_dist_wtd_comp_flag = 0;
            inter.disable_masked_comp = 1;
            inter.disable_onesided_comp = 1;
        }

        if (speed >= 6)
        {
            sf.mv_sf.use_fullpel_costlist = 1;

            sf.rd_sf.tx_domain_dist_thres_level = 3;

            tx.tx_type_search.fast_inter_tx_type_prob_thresh = 0;
            inter.limit_inter_mode_cands = 4;
            inter.prune_warped_prob_thresh = 8;
            inter.extra_prune_warped = 1;

            rt.gf_refresh_based_on_qp = 1;
            rt.prune_inter_modes_wrt_gf_arf_based_on_sad = 1;
            rt.var_part_split_threshold_shift = 7;
            if (!cpi.FrameIsIntraOnly) rt.var_part_based_on_qidx = 2;

            sf.winner_mode_sf.prune_winner_mode_eval_level = boosted ? 0 : 3;
        }

        if (speed >= 7)
        {
            rt.sse_early_term_inter_search = EARLY_TERM_IDX_1;
            rt.use_comp_ref_nonrd = 1;
            rt.ref_frame_comp_nonrd[2] = 1;   // LAST_ALTREF
            tx.intra_tx_size_search_init_depth_sqr = 2;
            part.partition_search_type = VAR_BASED_PARTITION;
            part.max_intra_bsize = BLOCK_32X32;

            sf.mv_sf.search_method = FAST_DIAMOND;
            sf.mv_sf.subpel_force_stop = QUARTER_PEL;

            inter.inter_mode_rd_model_estimation = 2;
            // This sf is not applicable in non-rd path.
            inter.skip_newmv_in_drl = 0;

            sf.interp_sf.skip_interp_filter_search = 0;

            // Disable intra_y_mode_mask pruning since the performance at speed 7 isn't significantly better than
            // speed 6.
            for (int i = 0; i < TX_SIZES; ++i) sf.intra_sf.intra_y_mode_mask[i] = INTRA_ALL;

            sf.lpf_sf.lpf_pick = LPF_PICK_FROM_Q;
            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL5;

            rt.mode_search_skip_flags |= FLAG_SKIP_INTRA_DIRMISMATCH;
            rt.nonrd_prune_ref_frame_search = 1;
            // This is for rd path only.
            rt.prune_inter_modes_using_temp_var = 0;
            rt.prune_inter_modes_wrt_gf_arf_based_on_sad = 0;
            rt.prune_intra_mode_based_on_mv_range = 0;
            rt.reuse_inter_pred_nonrd = cpi.EnableWarpedMotion ? 0 : 1;   // (no temporal denoising)
            rt.short_circuit_low_temp_var = 0;
            // For spatial layers, only LAST and GOLDEN are currently used in the SVC for nonrd. The flag
            // use_nonrd_altref_frame can disable GOLDEN in the get_ref_frame_flags() for some patterns, so disable it
            // here for spatial layers.
            rt.use_nonrd_altref_frame = cpi.NumSpatialLayers > 1 ? 0 : 1;
            rt.use_nonrd_pick_mode = 1;
            rt.discount_color_cost = 1;
            rt.nonrd_check_partition_merge_mode = 3;
            rt.skip_intra_pred = 1;
            rt.source_metrics_sb_nonrd = 1;
            // Set mask for intra modes.
            for (int i = 0; i < BLOCK_SIZES; ++i)
                rt.intra_y_mode_bsize_mask_nrd[i] = i >= BLOCK_32X32 ? INTRA_DC : INTRA_DC_H_V;   // Use DC, H, V intra mode for block sizes < 32X32.

            sf.winner_mode_sf.dc_blk_pred_level = 0;
            rt.var_part_based_on_qidx = 3;
            rt.prune_compoundmode_with_singlecompound_var = true;
            rt.prune_compoundmode_with_singlemode_var = true;
            rt.skip_compound_based_on_var = true;
            rt.use_adaptive_subpel_search = true;
        }

        if (speed >= 8)
        {
            rt.sse_early_term_inter_search = EARLY_TERM_IDX_2;
            sf.intra_sf.intra_pruning_with_hog = 1;
            rt.short_circuit_low_temp_var = 1;
            rt.use_nonrd_altref_frame = 0;
            rt.nonrd_prune_ref_frame_search = 2;
            rt.nonrd_check_partition_merge_mode = 0;
            rt.var_part_split_threshold_shift = 8;
            rt.var_part_based_on_qidx = 4;
            rt.partition_direct_merging = 1;
            rt.prune_compoundmode_with_singlemode_var = false;
            sf.mv_sf.use_bsize_dependent_search_method = 4;
            rt.prune_hv_pred_modes_using_src_sad = true;
        }
        if (speed >= 9)
        {
            rt.sse_early_term_inter_search = EARLY_TERM_IDX_3;
            rt.estimate_motion_for_var_based_partition = 3;
            rt.prefer_large_partition_blocks = 3;
            rt.skip_intra_pred = 2;
            rt.var_part_split_threshold_shift = 9;
            for (int i = 0; i < BLOCK_SIZES; ++i) rt.intra_y_mode_bsize_mask_nrd[i] = INTRA_DC;
            rt.var_part_based_on_qidx = 0;
            rt.frame_level_mode_cost_update = true;
            rt.check_only_zero_zeromv_on_large_blocks = true;
            rt.reduce_mv_pel_precision_highmotion = 0;
            rt.use_adaptive_subpel_search = true;
            sf.mv_sf.use_bsize_dependent_search_method = 0;
        }
        if (speed >= 10)
        {
            rt.sse_early_term_inter_search = EARLY_TERM_IDX_4;
            rt.nonrd_prune_ref_frame_search = 3;
            rt.var_part_split_threshold_shift = 10;
            sf.mv_sf.subpel_search_method = SUBPEL_TREE_PRUNED_MORE;
        }
        // (speed >= 11 screen content: not used)
        if (cpi.Tuning == AOM_TUNE_IQ || cpi.Tuning == AOM_TUNE_SSIMULACRA2) sf.intra_sf.skip_intra_in_interframe = 0;
    }
}
