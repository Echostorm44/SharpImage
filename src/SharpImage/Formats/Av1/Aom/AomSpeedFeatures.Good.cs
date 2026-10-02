using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 speed_features.c for oxcf->mode == GOOD: set_good_speed_feature_framesize_dependent and
// set_good_speed_features_framesize_independent (enable_low_complexity_decode stays at its default 0).
internal sealed partial class AomSpeedFeatures
{
    /// <summary>set_good_speed_feature_framesize_dependent.</summary>
    private static void SetGoodFramesizeDependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
    {
        int minDim = Math.Min(cpi.Width, cpi.Height);
        bool is_480p_or_lesser = minDim <= 480;
        bool is_480p_or_larger = minDim >= 480;
        bool is_720p_or_larger = minDim >= 720;
        bool is_1080p_or_larger = minDim >= 1080;
        bool is_4k_or_larger = minDim >= 2160;
        bool use_hbd = cpi.UseHighBitDepth;
        bool boosted = cpi.FrameIsBoosted;
        bool is_boosted_arf2_bwd_type = boosted || cpi.UpdateType == INTNL_ARF_UPDATE;
        bool is_lf_frame = cpi.UpdateType == LF_UPDATE;
        bool allow_screen_content_tools = cpi.AllowScreenContentTools;
        var part = sf.part_sf;
        var inter = sf.inter_sf;

        if (is_480p_or_larger)
        {
            part.use_square_partition_only_threshold = BLOCK_128X128;
            part.auto_max_partition_based_on_simple_motion = is_720p_or_larger ? ADAPT_PRED : RELAXED_PRED;
        }
        else
        {
            part.use_square_partition_only_threshold = BLOCK_64X64;
            part.auto_max_partition_based_on_simple_motion = DIRECT_PRED;
            if (use_hbd) sf.tx_sf.prune_tx_size_level = 1;
        }

        if (is_4k_or_larger) part.default_min_partition_size = BLOCK_8X8;

        // TODO(huisu@google.com): train models for 720P and above.
        if (!is_720p_or_larger)
        {
            part.ml_partition_search_breakout_thresh[0] = -1.0f;      // BLOCK_8X8
            part.ml_partition_search_breakout_thresh[1] = 0.993307f;  // BLOCK_16X16
            part.ml_partition_search_breakout_thresh[2] = 0.952574f;  // BLOCK_32X32
            part.ml_partition_search_breakout_thresh[3] = 0.924142f;  // BLOCK_64X64
            part.ml_partition_search_breakout_thresh[4] = 0.880797f;  // BLOCK_128X128
            part.ml_early_term_after_part_split_level = 1;
        }

        part.ml_partition_search_breakout_model_index = 0;

        if (is_720p_or_larger) sf.mv_sf.use_downsampled_sad = 2;

        if (!is_720p_or_larger)
        {
            int rate_tolerance = Math.Min(cpi.UnderShootPct, cpi.OverShootPct);
            sf.hl_sf.recode_tolerance = 25 + (rate_tolerance >> 2);
        }

        if (speed >= 1)
        {
            part.ml_4_partition_search_level_index = 1;
            inter.skip_newmv_in_drl = 1;

            inter.skip_cmp_using_top_cmp_avg_est_rd_lvl = is_480p_or_lesser ? 1 : 2;

            if (is_720p_or_larger) part.use_square_partition_only_threshold = BLOCK_128X128;
            else if (is_480p_or_larger) part.use_square_partition_only_threshold = BLOCK_64X64;
            else part.use_square_partition_only_threshold = BLOCK_32X32;

            if (is_720p_or_larger)
            {
                part.ml_partition_search_breakout_thresh[0] = 0.5f;
                part.ml_partition_search_breakout_thresh[1] = 0.5042595622791082f;
                part.ml_partition_search_breakout_thresh[2] = 0.5f;
                part.ml_partition_search_breakout_thresh[3] = 0.8378425823517456f;
                part.ml_partition_search_breakout_thresh[4] = 0.8047585616503903f;
                part.ml_partition_search_breakout_model_index = 1;
            }
            else
            {
                part.ml_partition_search_breakout_thresh[0] = -1.0f;
                part.ml_partition_search_breakout_thresh[1] = 0.952574f;
                part.ml_partition_search_breakout_thresh[2] = 0.952574f;
                part.ml_partition_search_breakout_thresh[3] = 0.924142f;
                part.ml_partition_search_breakout_thresh[4] = 0.880797f;
            }
            part.ml_early_term_after_part_split_level = 2;

            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL1;
        }

        if (speed >= 2)
        {
            part.ml_4_partition_search_level_index = 2;
            part.use_square_partition_only_threshold = is_720p_or_larger ? BLOCK_64X64 : BLOCK_32X32;

            if (is_720p_or_larger)
            {
                part.ml_partition_search_breakout_thresh[0] = 0.5f;
                part.ml_partition_search_breakout_thresh[1] = 0.5042595622791082f;
                part.ml_partition_search_breakout_thresh[2] = 0.5f;
                part.ml_partition_search_breakout_thresh[3] = 0.8378425823517456f;
                part.ml_partition_search_breakout_thresh[4] = 0.8047585616503903f;
                part.ml_partition_search_breakout_model_index = 1;
            }

            if (is_720p_or_larger)
            {
                part.partition_search_breakout_dist_thr = 1 << 24;
                part.partition_search_breakout_rate_thr = 120;
            }
            else
            {
                part.partition_search_breakout_dist_thr = 1 << 22;
                part.partition_search_breakout_rate_thr = 100;
            }

            inter.prune_obmc_prob_thresh = is_720p_or_larger ? 16 : 8;
            inter.disable_interintra_wedge_var_thresh = is_480p_or_larger ? 100u : UINT_MAX;

            if (is_480p_or_lesser) inter.skip_ext_comp_nearmv_mode = 1;

            if (is_720p_or_larger) inter.limit_inter_mode_cands = is_lf_frame ? 1 : 0;
            else inter.limit_inter_mode_cands = is_lf_frame ? 2 : 0;

            inter.skip_cmp_using_top_cmp_avg_est_rd_lvl = 3;

            if (is_480p_or_larger)
            {
                sf.tx_sf.tx_type_search.prune_tx_type_using_stats = 1;
                if (use_hbd) sf.tx_sf.prune_tx_size_level = 2;
            }
            else
            {
                if (use_hbd) sf.tx_sf.prune_tx_size_level = 3;
                sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = boosted ? 0 : 1;
                sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch = boosted ? 0 : 1;
            }

            if (!is_720p_or_larger)
            {
                sf.mv_sf.disable_second_mv = 1;
                sf.mv_sf.auto_mv_step_size = 2;
            }
            else
            {
                sf.mv_sf.disable_second_mv = boosted ? 0 : 2;
                sf.mv_sf.auto_mv_step_size = 1;
            }

            if (!is_720p_or_larger)
            {
                sf.hl_sf.recode_tolerance = 50;
                inter.disable_interinter_wedge_newmv_search = is_boosted_arf2_bwd_type ? 0 : 1;
                inter.enable_fast_wedge_mask_search = 1;
            }
        }

        if (speed >= 3)
        {
            inter.enable_fast_wedge_mask_search = 1;
            inter.skip_newmv_in_drl = 2;
            inter.skip_ext_comp_nearmv_mode = 1;
            inter.limit_inter_mode_cands = is_lf_frame ? 3 : 0;
            inter.disable_interinter_wedge_newmv_search = boosted ? 0 : 1;
            sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = 1;
            sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch = cpi.FrameIsIntraOnly ? 0 : 1;

            part.ml_early_term_after_part_split_level = 0;

            if (is_720p_or_larger)
            {
                for (int i = 0; i < PARTITION_BLOCK_SIZES; ++i) part.ml_partition_search_breakout_thresh[i] = -1;
                part.ml_partition_search_breakout_model_index = 0;
            }

            part.ml_4_partition_search_level_index = 3;

            if (is_720p_or_larger)
            {
                part.partition_search_breakout_dist_thr = 1 << 25;
                part.partition_search_breakout_rate_thr = 200;
                part.skip_non_sq_part_based_on_none = is_lf_frame ? 2 : 0;
            }
            else
            {
                part.max_intra_bsize = BLOCK_32X32;
                part.partition_search_breakout_dist_thr = 1 << 23;
                part.partition_search_breakout_rate_thr = 120;
                part.skip_non_sq_part_based_on_none = is_lf_frame ? 1 : 0;
            }
            if (use_hbd) sf.tx_sf.prune_tx_size_level = 3;

            part.early_term_after_none_split = is_480p_or_larger ? 1 : 0;
            if (is_720p_or_larger) sf.intra_sf.skip_intra_in_interframe = boosted ? 1 : 2;
            else sf.intra_sf.skip_intra_in_interframe = boosted ? 1 : 3;

            if (is_720p_or_larger)
            {
                inter.disable_interinter_wedge_var_thresh = 100;
                inter.skip_interinter_wedge_search_based_on_mse = 1;
                inter.limit_txfm_eval_per_mode = boosted ? 0 : 1;
            }
            else
            {
                inter.disable_interinter_wedge_var_thresh = UINT_MAX;
                inter.limit_txfm_eval_per_mode = boosted ? 0 : 2;
                sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL2;
            }

            inter.prune_comp_ref_frames = is_480p_or_lesser ? 0 : 1;

            inter.disable_interintra_wedge_var_thresh = UINT_MAX;
        }

        if (speed >= 4)
        {
            sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = 2;
            sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch = 1;
            part.partition_search_breakout_dist_thr = is_720p_or_larger ? 1 << 26 : 1 << 24;
            part.early_term_after_none_split = 1;

            if (is_480p_or_larger) sf.tx_sf.tx_type_search.prune_tx_type_using_stats = 2;
            else sf.mv_sf.skip_fullpel_search_using_startmv_refmv = boosted ? 0 : 1;

            inter.disable_interinter_wedge_var_thresh = UINT_MAX;
            inter.prune_obmc_prob_thresh = int.MaxValue;
            inter.limit_txfm_eval_per_mode = boosted ? 0 : 2;
            if (is_480p_or_lesser) inter.skip_newmv_in_drl = 3;

            if (is_720p_or_larger) inter.prune_comp_ref_frames = 2;
            else if (is_480p_or_larger) inter.prune_comp_ref_frames = is_boosted_arf2_bwd_type ? 0 : 2;

            sf.hl_sf.recode_tolerance = is_720p_or_larger ? 32 : 55;

            sf.intra_sf.skip_intra_in_interframe = 4;

            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL3;
        }

        if (speed >= 5)
        {
            if (is_720p_or_larger) inter.prune_warped_prob_thresh = 16;
            else if (is_480p_or_larger) inter.prune_warped_prob_thresh = 8;
            if (is_720p_or_larger) sf.hl_sf.recode_tolerance = 40;

            inter.skip_newmv_in_drl = 4;
            inter.prune_comp_ref_frames = 2;
            sf.mv_sf.skip_fullpel_search_using_startmv_refmv = boosted ? 0 : 1;

            if (!is_720p_or_larger)
            {
                inter.mv_cost_upd_level = INTERNAL_COST_UPD_SBROW_SET;
                inter.prune_nearest_near_mv_using_refmv_weight = (boosted || allow_screen_content_tools) ? 0 : 1;
                sf.mv_sf.use_downsampled_sad = 1;
            }

            if (!is_480p_or_larger) part.partition_search_breakout_dist_thr = 1 << 26;

            inter.prune_nearmv_using_neighbors = is_480p_or_lesser ? PRUNE_NEARMV_LEVEL1 : PRUNE_NEARMV_LEVEL2;

            if (is_720p_or_larger)
            {
                part.ext_part_eval_based_on_cur_best = (allow_screen_content_tools || cpi.FrameIsIntraOnly) ? 0 : 1;
                part.auto_max_partition_based_on_simple_motion = NOT_IN_USE;
            }

            if (is_480p_or_larger) sf.tpl_sf.reduce_num_frames = 1;
        }

        if (speed >= 6)
        {
            sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = 4;
            inter.prune_nearmv_using_neighbors = PRUNE_NEARMV_LEVEL3;
            inter.prune_comp_ref_frames = 3;
            inter.prune_nearest_near_mv_using_refmv_weight = (boosted || allow_screen_content_tools) ? 0 : 1;
            sf.mv_sf.skip_fullpel_search_using_startmv_refmv = boosted ? 0 : 2;

            if (is_480p_or_larger && !is_720p_or_larger) part.auto_max_partition_based_on_simple_motion = DIRECT_PRED;

            if (is_480p_or_larger) sf.hl_sf.allow_sub_blk_me_in_tf = 1;

            if (is_1080p_or_larger) part.default_min_partition_size = BLOCK_8X8;

            if (is_720p_or_larger) inter.disable_masked_comp = 1;

            if (!is_720p_or_larger)
            {
                inter.coeff_cost_upd_level = INTERNAL_COST_UPD_SBROW;
                inter.mode_cost_upd_level = INTERNAL_COST_UPD_SBROW;
            }

            if (is_720p_or_larger)
            {
                part.use_square_partition_only_threshold = BLOCK_32X32;
                part.partition_search_breakout_dist_thr = 1 << 28;
            }
            else
            {
                part.use_square_partition_only_threshold = BLOCK_16X16;
                part.partition_search_breakout_dist_thr = 1 << 26;
            }

            inter.prune_ref_mv_idx_search = is_720p_or_larger ? 2 : 1;

            sf.mv_sf.use_bsize_dependent_search_method = is_720p_or_larger ? 1 : 2;

            if (!is_720p_or_larger)
                sf.tx_sf.tx_type_search.fast_inter_tx_type_prob_thresh = is_boosted_arf2_bwd_type ? 450 : 150;

            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL4;

            sf.hl_sf.recode_tolerance = 55;
        }

        if (cpi.Tuning == AOM_TUNE_IQ || cpi.Tuning == AOM_TUNE_SSIMULACRA2) sf.intra_sf.skip_intra_in_interframe = 0;
    }

    /// <summary>set_good_speed_features_framesize_independent.</summary>
    private static void SetGoodFramesizeIndependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
    {
        bool boosted = cpi.FrameIsBoosted;
        bool is_boosted_arf2_bwd_type = boosted || cpi.UpdateType == INTNL_ARF_UPDATE;
        bool is_inter_frame = cpi.GfFrameType == INTER_FRAME;
        bool allow_screen_content_tools = cpi.AllowScreenContentTools;
        bool use_hbd = cpi.UseHighBitDepth;
        bool intraOnly = cpi.FrameIsIntraOnly;
        var part = sf.part_sf;
        var inter = sf.inter_sf;
        var tx = sf.tx_sf;

        // (!enable_large_scale_tile)
        sf.hl_sf.high_precision_mv_usage = LAST_MV_DATA;

        sf.gm_sf.gm_search_type = boosted ? GM_REDUCED_REF_SEARCH_SKIP_L2_L3_ARF2 : GM_SEARCH_CLOSEST_REFS_ONLY;
        sf.gm_sf.prune_ref_frame_for_gm_search = boosted ? 0 : 1;
        sf.gm_sf.disable_gm_search_based_on_stats = 1;

        part.ml_prune_partition = 1;
        part.prune_ext_partition_types_search_level = 1;
        part.prune_part4_search = 2;
        part.simple_motion_search_prune_rect = 1;
        part.ml_predict_breakout_level = use_hbd ? 1 : 3;
        part.reuse_prev_rd_results_for_part_ab = 1;
        part.use_best_rd_for_pruning = 1;
        part.simple_motion_search_prune_agg = allow_screen_content_tools ? NO_PRUNING : SIMPLE_AGG_LVL0;

        // TODO(debargha): Test, tweak and turn on either 1 or 2
        inter.inter_mode_rd_model_estimation = cpi.Sharpness != 0 ? 0 : 1;
        inter.model_based_post_interp_filter_breakout = 1;
        inter.prune_compound_using_single_ref = 1;
        inter.prune_mode_search_simple_translation = 1;
        inter.prune_ref_frame_for_rect_partitions = (boosted || allow_screen_content_tools) ? 0 : (is_boosted_arf2_bwd_type ? 1 : 2);
        inter.reduce_inter_modes = boosted ? 1 : 2;
        inter.selective_ref_frame = 1;
        inter.use_dist_wtd_comp_flag = DIST_WTD_COMP_SKIP_MV_SEARCH;
        inter.enable_fast_compound_mode_search = 1;

        sf.interp_sf.use_fast_interpolation_filter_search = 1;
        sf.interp_sf.disable_dual_filter = 1;
        sf.interp_sf.use_more_sharp_interp = boosted ? 0 : 1;

        sf.intra_sf.intra_pruning_with_hog = 1;

        tx.adaptive_txb_search_level = 1;
        tx.intra_tx_size_search_init_depth_sqr = 1;
        tx.model_based_prune_tx_search_level = 1;
        tx.tx_type_search.use_reduced_intra_txset = 1;

        sf.tpl_sf.search_method = NSTEP_8PT;

        sf.rt_sf.use_nonrd_pick_mode = 0;
        sf.rt_sf.discount_color_cost = 0;
        sf.rt_sf.use_real_time_ref_set = 0;

        if (cpi.FrContentType == FC_GRAPHICS_ANIMATION || cpi.UseScreenContentTools)
            sf.mv_sf.exhaustive_searches_thresh = 1 << 20;
        else
            sf.mv_sf.exhaustive_searches_thresh = 1 << 25;

        sf.rd_sf.perform_coeff_opt = 1;
        sf.hl_sf.superres_auto_search_type = SUPERRES_AUTO_DUAL;

        sf.lpf_sf.reduce_wiener_window_size = 1;

        if (speed >= 1)
        {
            sf.hl_sf.adjust_num_frames_for_arf_filtering = allow_screen_content_tools ? 0 : 1;

            part.intra_cnn_based_part_prune_level = allow_screen_content_tools ? 0 : 2;

            part.simple_motion_search_prune_agg = allow_screen_content_tools ? NO_PRUNING : SIMPLE_AGG_LVL1;
            part.simple_motion_search_early_term_none = 1;
            // TODO(Venkat): Clean-up frame type dependency for simple_motion_search_split in partition search function
            part.simple_motion_search_split = allow_screen_content_tools ? 1 : 2;
            part.ml_predict_breakout_level = use_hbd ? 2 : 3;

            sf.mv_sf.exhaustive_searches_thresh <<= 1;
            sf.mv_sf.obmc_full_pixel_search_level = 1;
            sf.mv_sf.use_accurate_subpel_search = USE_4_TAPS;
            sf.mv_sf.disable_extensive_joint_motion_search = 1;

            inter.prune_comp_search_by_single_result = boosted ? 2 : 1;
            inter.prune_comp_type_by_comp_avg = 1;
            inter.prune_comp_type_by_model_rd = boosted ? 0 : 1;
            inter.prune_ref_frame_for_rect_partitions = (intraOnly || allow_screen_content_tools) ? 0 : (boosted ? 1 : 2);
            inter.reduce_inter_modes = boosted ? 1 : 3;
            inter.reuse_inter_intra_mode = 1;
            inter.selective_ref_frame = 2;
            inter.skip_arf_compound = 1;
            inter.prune_comp_using_best_single_mode_ref = 2;
            inter.use_dist_wtd_comp_flag = DIST_WTD_COMP_DISABLED;
            inter.prune_inter_modes_based_on_tpl = 1;

            sf.interp_sf.use_interp_filter = 1;

            sf.intra_sf.prune_palette_search_level = 1;

            tx.adaptive_txb_search_level = 2;
            tx.inter_tx_size_search_init_depth_rect = 1;
            tx.inter_tx_size_search_init_depth_sqr = 1;
            tx.intra_tx_size_search_init_depth_rect = 1;
            tx.model_based_prune_tx_search_level = 0;
            tx.tx_type_search.ml_tx_split_thresh = 4000;
            tx.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_2;
            tx.tx_type_search.skip_tx_search = 1;
            tx.prune_inter_tx_split_rd_eval_lvl = 1;

            sf.rd_sf.perform_coeff_opt = boosted ? 2 : 3;
            sf.rd_sf.tx_domain_dist_level = boosted ? 1 : 2;
            sf.rd_sf.tx_domain_dist_thres_level = 1;

            sf.lpf_sf.dual_sgr_penalty_level = 1;
            sf.lpf_sf.enable_sgr_ep_pruning = 1;

            // TODO(any, yunqing): move this feature to speed 0.
            sf.tpl_sf.skip_alike_starting_mv = 1;
        }

        if (speed >= 2)
        {
            sf.hl_sf.recode_loop = ALLOW_RECODE_KFARFGF;

            part.simple_motion_search_prune_agg = allow_screen_content_tools ? NO_PRUNING : SIMPLE_AGG_LVL2;
            sf.fp_sf.skip_motion_search_threshold = 25;

            sf.gm_sf.num_refinement_steps = 2;

            part.reuse_best_prediction_for_part_ab = intraOnly ? 0 : 1;

            sf.mv_sf.simple_motion_subpel_force_stop = QUARTER_PEL;
            sf.mv_sf.subpel_iters_per_step = 1;
            sf.mv_sf.reduce_search_range = 1;

            // TODO(chiyotsai@google.com): We can get 10% speed up if we move
            // adaptive_rd_thresh to speed 1. But currently it performs poorly on some
            // clips (e.g. 5% loss on dinner_1080p). We need to examine the sequence a
            // bit more closely to figure out why.
            inter.adaptive_rd_thresh = 1;
            inter.disable_interinter_wedge_var_thresh = 100;
            inter.fast_interintra_wedge_search = 1;
            inter.prune_comp_search_by_single_result = boosted ? 4 : 1;
            inter.prune_ext_comp_using_neighbors = 1;
            inter.prune_comp_type_by_comp_avg = 2;
            inter.selective_ref_frame = 3;
            inter.reuse_mask_search_results = 1;
            SetTxfmRdGateLevel(inter.txfm_rd_gate_level, boosted ? 0 : 1);
            inter.inter_mode_txfm_breakout = boosted ? 0 : 1;
            inter.alt_ref_search_fp = 1;
            inter.prune_single_ref = boosted ? 1 : 2;

            sf.interp_sf.adaptive_interp_filter_search = 1;

            sf.intra_sf.intra_pruning_with_hog = 2;
            sf.intra_sf.skip_intra_in_interframe = is_inter_frame ? 2 : 1;
            sf.intra_sf.skip_filter_intra_in_inter_frames = 1;

            sf.tpl_sf.prune_starting_mv = 1;
            sf.tpl_sf.search_method = DIAMOND;

            sf.rd_sf.perform_coeff_opt = is_boosted_arf2_bwd_type ? 3 : 4;
            sf.rd_sf.use_mb_rd_hash = 1;

            sf.lpf_sf.prune_wiener_based_on_src_var = 1;
            sf.lpf_sf.prune_sgr_based_on_wiener = 1;
            sf.lpf_sf.disable_loop_restoration_chroma = boosted ? 0 : 1;

            // TODO(any): Re-evaluate this feature set to 1 in speed 2.
            sf.tpl_sf.allow_compound_pred = 0;
            sf.tpl_sf.prune_ref_frames_in_tpl = 1;

            tx.prune_inter_tx_split_rd_eval_lvl = 2;
        }

        if (speed >= 3)
        {
            sf.hl_sf.high_precision_mv_usage = CURRENT_Q;
            sf.hl_sf.weight_calc_level_in_tf = 1;

            sf.gm_sf.prune_ref_frame_for_gm_search = 1;
            sf.gm_sf.prune_zero_mv_with_sse = 1;
            sf.gm_sf.num_refinement_steps = 0;

            part.simple_motion_search_prune_agg =
                allow_screen_content_tools ? SIMPLE_AGG_LVL0 : (boosted ? SIMPLE_AGG_LVL3 : QIDX_BASED_AGG_LVL1);
            part.prune_ext_part_using_split_info = 1;
            part.simple_motion_search_rect_split = 1;
            part.prune_h_or_v_4part_using_sms_info = true;

            sf.mv_sf.subpel_search_method = SUBPEL_TREE_PRUNED;
            sf.mv_sf.search_method = DIAMOND;
            sf.mv_sf.disable_second_mv = 2;
            sf.mv_sf.prune_mesh_search = PRUNE_MESH_SEARCH_LVL_1;
            sf.mv_sf.use_intrabc = 0;

            inter.disable_interinter_wedge_newmv_search = boosted ? 0 : 1;
            inter.mv_cost_upd_level = INTERNAL_COST_UPD_SBROW;
            inter.disable_onesided_comp = 1;
            inter.disable_interintra_wedge_var_thresh = UINT_MAX;
            // TODO(any): Experiment with the early exit mechanism for speeds 0, 1 and 2
            // and clean-up the speed feature
            inter.perform_best_rd_based_gating_for_chroma = 1;
            inter.prune_inter_modes_based_on_tpl = boosted ? 1 : 2;
            inter.prune_comp_search_by_single_result = boosted ? 4 : 2;
            inter.selective_ref_frame = 5;
            inter.reuse_compound_type_decision = 1;
            SetTxfmRdGateLevel(inter.txfm_rd_gate_level, boosted ? 0 : (is_boosted_arf2_bwd_type ? 1 : 2));
            inter.inter_mode_txfm_breakout = boosted ? 0 : 2;
            inter.prune_single_ref = 2;

            sf.interp_sf.adaptive_interp_filter_search = 2;

            // TODO(chiyotsai@google.com): the thresholds chosen for intra hog are
            // inherited directly from luma hog with some minor tweaking. Eventually we
            // should run this with a bayesian optimizer to find the Pareto frontier.
            sf.intra_sf.chroma_intra_pruning_with_hog = 2;
            sf.intra_sf.intra_pruning_with_hog = 3;
            sf.intra_sf.prune_palette_search_level = 2;
            sf.intra_sf.top_intra_model_count_allowed = 2;

            sf.tpl_sf.prune_starting_mv = 2;
            sf.tpl_sf.skip_alike_starting_mv = 2;
            sf.tpl_sf.prune_intra_modes = 1;
            sf.tpl_sf.reduce_first_step_size = 6;
            sf.tpl_sf.subpel_force_stop = QUARTER_PEL;

            tx.adaptive_txb_search_level = boosted ? 2 : 3;
            tx.tx_type_search.use_skip_flag_prediction = 2;
            tx.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_3;

            // TODO(any): Refactor the code related to following winner mode speed features
            sf.winner_mode_sf.enable_winner_mode_for_coeff_opt = 1;
            sf.winner_mode_sf.enable_winner_mode_for_use_tx_domain_dist = 1;
            sf.winner_mode_sf.motion_mode_for_winner_cand = boosted ? 0 : cpi.UpdateType == INTNL_ARF_UPDATE ? 1 : 2;
            sf.winner_mode_sf.prune_winner_mode_eval_level = boosted ? 0 : 4;

            // For screen content, "prune_sgr_based_on_wiener = 2" cause large quality
            // loss.
            sf.lpf_sf.prune_sgr_based_on_wiener = allow_screen_content_tools ? 1 : 2;
            sf.lpf_sf.prune_wiener_based_on_src_var = 2;
            sf.lpf_sf.use_coarse_filter_level_search = intraOnly ? 0 : 1;
            sf.lpf_sf.use_downsampled_wiener_stats = 1;
        }

        if (speed >= 4)
        {
            sf.mv_sf.subpel_search_method = SUBPEL_TREE_PRUNED_MORE;

            sf.gm_sf.prune_zero_mv_with_sse = 2;
            sf.gm_sf.downsample_level = 1;

            part.simple_motion_search_prune_agg = allow_screen_content_tools ? SIMPLE_AGG_LVL0 : SIMPLE_AGG_LVL4;
            part.simple_motion_search_reduce_search_steps = 4;
            part.prune_ext_part_using_split_info = 2;
            part.ml_predict_breakout_level = 3;
            part.prune_rectangular_split_based_on_qidx = (allow_screen_content_tools || intraOnly) ? 0 : 1;

            inter.alt_ref_search_fp = 2;
            inter.txfm_rd_gate_level[TX_SEARCH_DEFAULT] = boosted ? 0 : 3;
            inter.txfm_rd_gate_level[TX_SEARCH_MOTION_MODE] = boosted ? 0 : 5;
            inter.txfm_rd_gate_level[TX_SEARCH_COMP_TYPE_MODE] = boosted ? 0 : 3;

            inter.prune_inter_modes_based_on_tpl = boosted ? 1 : 3;
            inter.prune_ext_comp_using_neighbors = 2;
            inter.prune_obmc_prob_thresh = int.MaxValue;
            inter.disable_interinter_wedge_var_thresh = UINT_MAX;

            sf.interp_sf.cb_pred_filter_search = 1;
            sf.interp_sf.skip_sharp_interp_filter_search = 1;
            sf.interp_sf.use_interp_filter = 2;
            sf.interp_sf.use_more_sharp_interp = 0;

            sf.intra_sf.intra_uv_mode_mask[TX_16X16] = UV_INTRA_DC_H_V_CFL;
            sf.intra_sf.intra_uv_mode_mask[TX_32X32] = UV_INTRA_DC_H_V_CFL;
            sf.intra_sf.intra_uv_mode_mask[TX_64X64] = UV_INTRA_DC_H_V_CFL;
            // TODO(any): "intra_y_mode_mask" doesn't help much at speed 4.
            // sf->intra_sf.intra_y_mode_mask[TX_16X16] = INTRA_DC_H_V;
            // sf->intra_sf.intra_y_mode_mask[TX_32X32] = INTRA_DC_H_V;
            // sf->intra_sf.intra_y_mode_mask[TX_64X64] = INTRA_DC_H_V;
            sf.intra_sf.skip_intra_in_interframe = 4;

            sf.mv_sf.simple_motion_subpel_force_stop = HALF_PEL;
            sf.mv_sf.prune_mesh_search = PRUNE_MESH_SEARCH_LVL_2;

            sf.tpl_sf.subpel_force_stop = HALF_PEL;
            sf.tpl_sf.search_method = FAST_BIGDIA;
            sf.tpl_sf.use_sad_for_mode_decision = 1;

            tx.tx_type_search.fast_intra_tx_type_search = 1;

            sf.rd_sf.perform_coeff_opt = is_boosted_arf2_bwd_type ? 5 : 7;

            // TODO(any): Extend multi-winner mode processing support for inter frames
            sf.winner_mode_sf.multi_winner_mode_type = intraOnly ? MULTI_WINNER_MODE_DEFAULT : MULTI_WINNER_MODE_OFF;
            sf.winner_mode_sf.dc_blk_pred_level = boosted ? 0 : 2;

            sf.lpf_sf.lpf_pick = LPF_PICK_FROM_FULL_IMAGE_NON_DUAL;
        }

        if (speed >= 5)
        {
            sf.hl_sf.adjust_num_frames_for_arf_filtering = allow_screen_content_tools ? 0 : 2;

            sf.fp_sf.reduce_mv_step_param = 4;

            part.simple_motion_search_prune_agg = allow_screen_content_tools ? SIMPLE_AGG_LVL0 : SIMPLE_AGG_LVL5;
            part.ext_partition_eval_thresh = allow_screen_content_tools ? BLOCK_8X8 : BLOCK_16X16;
            part.prune_sub_8x8_partition_level = allow_screen_content_tools ? 1 : 2;

            sf.mv_sf.warp_search_method = WARP_SEARCH_DIAMOND;

            inter.prune_inter_modes_if_skippable = 1;
            inter.prune_single_ref = is_boosted_arf2_bwd_type ? 0 : 3;
            inter.txfm_rd_gate_level[TX_SEARCH_DEFAULT] = boosted ? 0 : 4;
            inter.txfm_rd_gate_level[TX_SEARCH_COMP_TYPE_MODE] = boosted ? 0 : 5;
            inter.enable_fast_compound_mode_search = 2;

            sf.interp_sf.skip_interp_filter_search = boosted ? 0 : 1;

            sf.intra_sf.chroma_intra_pruning_with_hog = 3;
            sf.intra_sf.disable_smooth_intra = 1;

            // TODO(any): Extend multi-winner mode processing support for inter frames
            sf.winner_mode_sf.multi_winner_mode_type = intraOnly ? MULTI_WINNER_MODE_FAST : MULTI_WINNER_MODE_OFF;

            // Disable Self-guided Loop restoration filter.
            sf.lpf_sf.enable_sgr_ep_pruning = 2;
            sf.lpf_sf.disable_wiener_coeff_refine_search = true;

            sf.tpl_sf.prune_starting_mv = 3;
            sf.tpl_sf.use_y_only_rate_distortion = 1;
            sf.tpl_sf.subpel_force_stop = FULL_PEL;
            sf.tpl_sf.gop_length_decision_method = 2;
            sf.tpl_sf.use_sad_for_mode_decision = 2;

            sf.winner_mode_sf.dc_blk_pred_level = 2;

            sf.fp_sf.disable_recon = 1;
        }

        if (speed >= 6)
        {
            sf.hl_sf.disable_extra_sc_testing = 1;
            sf.hl_sf.second_alt_ref_filtering = 0;

            sf.gm_sf.downsample_level = 2;

            inter.prune_inter_modes_based_on_tpl = boosted ? 1 : 4;
            inter.selective_ref_frame = 6;
            inter.prune_single_ref = is_boosted_arf2_bwd_type ? 0 : 4;
            inter.prune_ext_comp_using_neighbors = 3;

            sf.intra_sf.chroma_intra_pruning_with_hog = 4;
            sf.intra_sf.intra_pruning_with_hog = 4;
            sf.intra_sf.intra_uv_mode_mask[TX_32X32] = UV_INTRA_DC;
            sf.intra_sf.intra_uv_mode_mask[TX_64X64] = UV_INTRA_DC;
            sf.intra_sf.intra_y_mode_mask[TX_32X32] = INTRA_DC;
            sf.intra_sf.intra_y_mode_mask[TX_64X64] = INTRA_DC;
            sf.intra_sf.early_term_chroma_palette_size_search = 1;

            part.prune_rectangular_split_based_on_qidx = boosted || allow_screen_content_tools ? 0 : 2;

            part.prune_part4_search = 3;

            sf.mv_sf.simple_motion_subpel_force_stop = FULL_PEL;

            sf.tpl_sf.gop_length_decision_method = 3;

            sf.rd_sf.perform_coeff_opt = is_boosted_arf2_bwd_type ? 6 : 8;

            sf.winner_mode_sf.dc_blk_pred_level = 3;
            sf.winner_mode_sf.multi_winner_mode_type = MULTI_WINNER_MODE_OFF;

            sf.fp_sf.skip_zeromv_motion_search = 1;
        }

        if (cpi.Sharpness == 3)
        {
            tx.adaptive_txb_search_level = 0;
            tx.tx_type_search.use_skip_flag_prediction = 0;
        }

        if (cpi.Tuning == AOM_TUNE_IQ || cpi.Tuning == AOM_TUNE_SSIMULACRA2)
        {
            sf.intra_sf.skip_intra_in_interframe = 0;
            inter.inter_mode_rd_model_estimation = 0;
            sf.mv_sf.use_intrabc = 1;

            if (sf.intra_sf.intra_pruning_with_hog > 3) sf.intra_sf.intra_pruning_with_hog = 3;
            if (sf.intra_sf.chroma_intra_pruning_with_hog > 3) sf.intra_sf.chroma_intra_pruning_with_hog = 3;
        }
    }

    /// <summary>set_rt_speed_feature_framesize_dependent (not ported yet).</summary>
    private static void SetRtFramesizeDependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
        => throw new NotImplementedException("libaom REALTIME speed features");

    /// <summary>set_rt_speed_features_framesize_independent (not ported yet).</summary>
    private static void SetRtFramesizeIndependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
        => throw new NotImplementedException("libaom REALTIME speed features");
}
