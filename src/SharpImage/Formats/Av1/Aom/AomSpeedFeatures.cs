using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1 speed features (av1/encoder/speed_features.h/.c), ported line by line for the all-intra mode
// (oxcf->mode == ALLINTRA): the SPEED_FEATURES struct with every field under its C name, the init_*_sf defaults, the
// all-intra framesize-independent / framesize-dependent settings, the qindex-dependent overrides, and the parts of
// av1_set_speed_features_framesize_independent / _dependent / _qindex_dependent that run for ALLINTRA (including the
// WinnerModeParams tables they fill and the sequence-header tool flags they clear). Field declarations were generated
// from speed_features.h by tools/codegen/gen_speedfeatures.py; the twin test (AomSpeedFeaturesTwinTests) compares
// every field with libaom's own functions.
//
// libaom calls these per frame as: framesize_independent (set_size_independent_vars, after screen-content
// detection), framesize_dependent (av1_set_size_dependent_vars), then qindex_dependent (after av1_set_quantizer).
// Fields that no init_*_sf resets keep their previous value, exactly as on libaom's calloc'd AV1_COMP.

// ---- SPEED_FEATURES and its sub-structs (field declarations generated from speed_features.h)

/// <summary>MESH_PATTERN (speed_features.h).</summary>
internal struct AomMeshPattern
{
    public int range;
    public int interval;
}

/// <summary>TX_TYPE_SEARCH (speed_features.h).</summary>
internal sealed class AomTxTypeSearch
{
    public int prune_2d_txfm_mode; // TX_TYPE_PRUNE_MODE
    public int fast_intra_tx_type_search;
    public int fast_inter_tx_type_prob_thresh;
    public int use_reduced_intra_txset;
    public int use_skip_flag_prediction;
    public int ml_tx_split_thresh;
    public int skip_tx_search;
    public int prune_tx_type_using_stats;
    public int prune_tx_type_est_rd;
    public int winner_mode_tx_type_pruning;
}

/// <summary>HIGH_LEVEL_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomHighLevelSpeedFeatures
{
    public int frame_parameter_update;
    public int recode_loop; // RECODE_LOOP_TYPE
    public int recode_tolerance;
    public int high_precision_mv_usage; // MV_PREC_LOGIC
    public int static_segmentation;
    public int superres_auto_search_type; // SUPERRES_AUTO_SEARCH_TYPE
    public int disable_extra_sc_testing;
    public int second_alt_ref_filtering;
    public int adjust_num_frames_for_arf_filtering;
    public int accurate_bit_estimate;
    public int weight_calc_level_in_tf;
    public int allow_sub_blk_me_in_tf;
    public int ref_frame_mvs_lvl;
    public int screen_detection_mode2_fast_detection;
}

/// <summary>FIRST_PASS_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomFirstPassSpeedFeatures
{
    public int reduce_mv_step_param;
    public int skip_motion_search_threshold;
    public int disable_recon;
    public int skip_zeromv_motion_search;
}

/// <summary>TPL_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomTplSpeedFeatures
{
    public int gop_length_decision_method;
    public int prune_intra_modes;
    public int reduce_first_step_size;
    public int skip_alike_starting_mv;
    public int subpel_force_stop; // SUBPEL_FORCE_STOP
    public int search_method; // SEARCH_METHODS
    public int prune_starting_mv;
    public int prune_ref_frames_in_tpl;
    public int allow_compound_pred;
    public int use_y_only_rate_distortion;
    public int use_sad_for_mode_decision;
    public int reduce_num_frames;
}

/// <summary>GLOBAL_MOTION_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomGlobalMotionSpeedFeatures
{
    public int gm_search_type; // GM_SEARCH_TYPE
    public int prune_ref_frame_for_gm_search;
    public int prune_zero_mv_with_sse;
    public int disable_gm_search_based_on_stats;
    public int downsample_level;
    public int num_refinement_steps;
    public int gm_erroradv_tr_level;
}

/// <summary>PARTITION_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomPartitionSpeedFeatures
{
    public int partition_search_type; // PARTITION_SEARCH_TYPE
    public int fixed_partition_size; // BLOCK_SIZE
    public int prune_ext_partition_types_search_level;
    public int prune_part4_search;
    public int ml_prune_partition;
    public int ml_early_term_after_part_split_level;
    public int less_rectangular_check_level;
    public int use_square_partition_only_threshold; // BLOCK_SIZE
    public int auto_max_partition_based_on_simple_motion; // MAX_PART_PRED_MODE
    public int default_min_partition_size; // BLOCK_SIZE
    public int default_max_partition_size; // BLOCK_SIZE
    public int adjust_var_based_rd_partitioning;
    public long partition_search_breakout_dist_thr; // int64_t
    public int partition_search_breakout_rate_thr;
    public readonly float[] ml_partition_search_breakout_thresh = new float[5]; // float [PARTITION_BLOCK_SIZES]
    public int ml_partition_search_breakout_model_index;
    public int ml_4_partition_search_level_index;
    public int simple_motion_search_prune_agg;
    public int simple_motion_search_prune_rect;
    public int simple_motion_search_split;
    public int simple_motion_search_early_term_none;
    public int simple_motion_search_reduce_search_steps;
    public int max_intra_bsize; // BLOCK_SIZE
    public int intra_cnn_based_part_prune_level;
    public int ext_partition_eval_thresh; // BLOCK_SIZE
    public int ext_part_eval_based_on_cur_best;
    public int rect_partition_eval_thresh;
    public int prune_ext_part_using_split_info;
    public int prune_rectangular_split_based_on_qidx;
    public bool prune_rect_part_using_4x4_var_deviation;
    public bool prune_rect_part_using_none_pred_mode;
    public int early_term_after_none_split;
    public int ml_predict_breakout_level;
    public int prune_sub_8x8_partition_level;
    public int simple_motion_search_rect_split;
    public int reuse_prev_rd_results_for_part_ab;
    public int reuse_best_prediction_for_part_ab;
    public int use_best_rd_for_pruning;
    public int skip_non_sq_part_based_on_none;
    public int disable_8x8_part_based_on_qidx;
    public bool prune_h_or_v_4part_using_sms_info;
    public int split_partition_penalty_level;
}

/// <summary>MV_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomMvSpeedFeatures
{
    public int search_method; // SEARCH_METHODS
    public int use_bsize_dependent_search_method;
    public int auto_mv_step_size;
    public int subpel_search_method; // SUBPEL_SEARCH_METHOD
    public int subpel_iters_per_step;
    public int subpel_force_stop; // SUBPEL_FORCE_STOP
    public int simple_motion_subpel_force_stop; // SUBPEL_FORCE_STOP
    public int use_accurate_subpel_search; // SUBPEL_SEARCH_TYPE
    public int exhaustive_searches_thresh;
    public readonly AomMeshPattern[] mesh_patterns = new AomMeshPattern[4]; // MESH_PATTERN [MAX_MESH_STEP]
    public readonly AomMeshPattern[] intrabc_mesh_patterns = new AomMeshPattern[4]; // MESH_PATTERN [MAX_MESH_STEP]
    public int reduce_search_range;
    public int prune_mesh_search; // PRUNE_MESH_SEARCH_LEVEL
    public int use_fullpel_costlist;
    public int obmc_full_pixel_search_level;
    public int full_pixel_search_level;
    public int use_intrabc;
    public int prune_intrabc_candidate_block_hash_search;
    public int intrabc_search_level;
    public int hash_max_8x8_intrabc_blocks;
    public int use_downsampled_sad;
    public int disable_extensive_joint_motion_search;
    public int disable_second_mv;
    public int skip_fullpel_search_using_startmv_refmv;
    public int warp_search_method; // WARP_SEARCH_METHOD
    public int warp_search_iters;
}

/// <summary>INTER_MODE_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomInterModeSpeedFeatures
{
    public int inter_mode_rd_model_estimation;
    public readonly int[] txfm_rd_gate_level = new int[3]; // int [TX_SEARCH_CASES]
    public int reduce_inter_modes;
    public int adaptive_rd_thresh;
    public int prune_inter_modes_if_skippable;
    public int selective_ref_frame;
    public int prune_ref_frame_for_rect_partitions;
    public int alt_ref_search_fp;
    public int prune_single_ref;
    public int prune_comp_ref_frames;
    public int skip_newmv_in_drl;
    public int skip_repeated_ref_mv;
    public int perform_best_rd_based_gating_for_chroma;
    public int reuse_inter_intra_mode;
    public int prune_comp_type_by_model_rd;
    public int prune_comp_type_by_comp_avg;
    public int prune_comp_search_by_single_result;
    public int prune_mode_search_simple_translation;
    public int prune_compound_using_single_ref;
    public int prune_ext_comp_using_neighbors;
    public int skip_ext_comp_nearmv_mode;
    public int prune_comp_using_best_single_mode_ref;
    public int prune_nearest_near_mv_using_refmv_weight;
    public int prune_ref_mv_idx_search;
    public int disable_onesided_comp;
    public int prune_obmc_prob_thresh;
    public int prune_warped_prob_thresh;
    public uint disable_interintra_wedge_var_thresh; // unsigned int
    public uint disable_interinter_wedge_var_thresh; // unsigned int
    public int fast_interintra_wedge_search;
    public int fast_wedge_sign_estimate;
    public int disable_interinter_wedge_newmv_search;
    public int use_dist_wtd_comp_flag; // DIST_WTD_COMP_FLAG
    public int mv_cost_upd_level; // INTERNAL_COST_UPDATE_TYPE
    public int coeff_cost_upd_level; // INTERNAL_COST_UPDATE_TYPE
    public int mode_cost_upd_level; // INTERNAL_COST_UPDATE_TYPE
    public int prune_inter_modes_based_on_tpl;
    public int prune_nearmv_using_neighbors; // PRUNE_NEARMV_LEVEL
    public int model_based_post_interp_filter_breakout;
    public int reuse_compound_type_decision;
    public int disable_masked_comp;
    public int enable_fast_compound_mode_search;
    public int reuse_mask_search_results;
    public int enable_fast_wedge_mask_search;
    public int inter_mode_txfm_breakout;
    public int limit_inter_mode_cands;
    public int limit_txfm_eval_per_mode;
    public int extra_prune_warped;
    public int skip_arf_compound;
    public int bias_warp_mode_rd_scale_pct;
    public float bias_obmc_mode_rd_scale_pct;
    public int skip_cmp_using_top_cmp_avg_est_rd_lvl;
    public int skip_interinter_wedge_search_based_on_mse;
}

/// <summary>INTERP_FILTER_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomInterpFilterSpeedFeatures
{
    public int use_fast_interpolation_filter_search;
    public int disable_dual_filter;
    public int use_interp_filter;
    public int skip_sharp_interp_filter_search;
    public int cb_pred_filter_search;
    public int adaptive_interp_filter_search;
    public int skip_interp_filter_search;
    public int use_more_sharp_interp;
    public int skip_model_rd_uv;
}

/// <summary>INTRA_MODE_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomIntraModeSpeedFeatures
{
    public readonly int[] intra_y_mode_mask = new int[5]; // int [TX_SIZES]
    public readonly int[] intra_uv_mode_mask = new int[5]; // int [TX_SIZES]
    public int skip_intra_in_interframe;
    public int intra_pruning_with_hog;
    public int chroma_intra_pruning_with_hog;
    public int disable_smooth_intra;
    public bool prune_smooth_intra_mode_for_chroma;
    public int prune_filter_intra_level;
    public int prune_palette_search_level;
    public int prune_luma_palette_size_search_level;
    public int prune_chroma_modes_using_luma_winner;
    public int dv_cost_upd_level; // INTERNAL_COST_UPDATE_TYPE
    public int cfl_search_range;
    public int top_intra_model_count_allowed;
    public int adapt_top_model_rd_count_using_neighbors;
    public int prune_luma_odd_delta_angles_in_intra;
    public int early_term_chroma_palette_size_search;
    public int skip_filter_intra_in_inter_frames;
}

/// <summary>TX_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomTxSpeedFeatures
{
    public int inter_tx_size_search_init_depth_sqr;
    public int inter_tx_size_search_init_depth_rect;
    public int intra_tx_size_search_init_depth_sqr;
    public int intra_tx_size_search_init_depth_rect;
    public int tx_size_search_lgr_block;
    public readonly AomTxTypeSearch tx_type_search = new();
    public int txb_split_cap;
    public int adaptive_txb_search_level;
    public int model_based_prune_tx_search_level;
    public int refine_fast_tx_search_results;
    public int prune_tx_size_level;
    public bool prune_intra_tx_depths_using_nn;
    public bool use_rd_based_breakout_for_intra_tx_search;
    public int prune_inter_tx_split_rd_eval_lvl;
    public int use_chroma_trellis_rd_mult;
}

/// <summary>RD_CALC_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomRdCalcSpeedFeatures
{
    public int simple_model_rd_from_var;
    public int tx_domain_dist_level;
    public int tx_domain_dist_thres_level;
    public int optimize_coefficients; // TRELLIS_OPT_TYPE
    public int use_mb_rd_hash;
    public int perform_coeff_opt;
}

/// <summary>WINNER_MODE_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomWinnerModeSpeedFeatures
{
    public int enable_winner_mode_for_coeff_opt;
    public int enable_winner_mode_for_tx_size_srch;
    public int tx_size_search_level;
    public int enable_winner_mode_for_use_tx_domain_dist;
    public int multi_winner_mode_type; // MULTI_WINNER_MODE_TYPE
    public int motion_mode_for_winner_cand;
    public int dc_blk_pred_level;
    public int winner_mode_ifs;
    public int prune_winner_mode_eval_level;
}

/// <summary>LOOP_FILTER_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomLoopFilterSpeedFeatures
{
    public int lpf_pick; // LPF_PICK_METHOD
    public int use_coarse_filter_level_search;
    public int adaptive_luma_loop_filter_skip;
    public int skip_loop_filter_using_filt_error;
    public int cdef_pick_method; // CDEF_PICK_METHOD
    public bool zero_low_cdef_strengths;
    public int adaptive_cdef_mode;
    public int dual_sgr_penalty_level;
    public int switchable_lr_with_bias_level;
    public int enable_sgr_ep_pruning;
    public int disable_loop_restoration_chroma;
    public int disable_loop_restoration_luma;
    public int min_lr_unit_size;
    public int max_lr_unit_size;
    public int prune_wiener_based_on_src_var;
    public int prune_sgr_based_on_wiener;
    public int reduce_wiener_window_size;
    public bool disable_wiener_filter;
    public bool disable_sgr_filter;
    public bool disable_wiener_coeff_refine_search;
    public int use_downsampled_wiener_stats;
}

/// <summary>REAL_TIME_SPEED_FEATURES (speed_features.h).</summary>
internal sealed class AomRealTimeSpeedFeatures
{
    public int check_intra_pred_nonrd;
    public int skip_intra_pred;
    public int estimate_motion_for_var_based_partition;
    public int nonrd_check_partition_merge_mode;
    public int nonrd_check_partition_split;
    public uint mode_search_skip_flags; // unsigned int
    public int nonrd_prune_ref_frame_search;
    public int use_nonrd_pick_mode;
    public int discount_color_cost;
    public int use_nonrd_altref_frame;
    public int use_comp_ref_nonrd;
    public readonly int[] ref_frame_comp_nonrd = new int[3]; // int [3]
    public int use_real_time_ref_set;
    public int short_circuit_low_temp_var;
    public int reuse_inter_pred_nonrd;
    public int num_inter_modes_for_tx_search;
    public int use_nonrd_filter_search;
    public int use_simple_rd_model;
    public int hybrid_intra_pickmode;
    public int prune_palette_search_nonrd;
    public int source_metrics_sb_nonrd;
    public int overshoot_detection_cbr; // OVERSHOOT_DETECTION_CBR
    public int check_scene_detection;
    public int rc_adjust_keyframe;
    public int rc_compute_spatial_var_sc_kf;
    public int prefer_large_partition_blocks;
    public int use_temporal_noise_estimate;
    public int fullpel_search_step_param;
    public readonly int[] intra_y_mode_bsize_mask_nrd = new int[16]; // int [BLOCK_SIZES]
    public bool prune_hv_pred_modes_using_src_sad;
    public int nonrd_aggressive_skip;
    public int skip_cdef_sb;
    public int selective_cdf_update;
    public int force_only_last_ref;
    public int force_large_partition_blocks_intra;
    public int use_fast_fixed_part;
    public int increase_source_sad_thresh;
    public int skip_tx_no_split_var_based_partition;
    public int skip_newmv_mode_based_on_sse;
    public int gf_length_lvl;
    public int prune_inter_modes_with_golden_ref;
    public int prune_inter_modes_wrt_gf_arf_based_on_sad;
    public int prune_inter_modes_using_temp_var;
    public int reduce_mv_pel_precision_highmotion;
    public int reduce_mv_pel_precision_lowcomplex;
    public int prune_intra_mode_based_on_mv_range; // BLOCK_SIZE
    public int var_part_split_threshold_shift;
    public int var_part_based_on_qidx;
    public int gf_refresh_based_on_qp;
    public int use_rtc_tf;
    public int use_idtx_nonrd;
    public int prune_idtx_nonrd;
    public int dct_only_palette_nonrd;
    public int skip_lf_screen;
    public int thresh_active_maps_skip_lf_cdef;
    public int part_early_exit_zeromv;
    public int sse_early_term_inter_search; // INTER_SEARCH_EARLY_TERM_IDX
    public int sad_based_adp_altref_lag;
    public int partition_direct_merging;
    public int tx_size_level_based_on_qstep;
    public bool vbp_prune_16x16_split_using_min_max_sub_blk_var;
    public int screen_content_cdef_filter_qindex_thresh;
    public bool prune_compoundmode_with_singlecompound_var;
    public bool frame_level_mode_cost_update;
    public bool prune_h_pred_using_best_mode_so_far;
    public bool enable_intra_mode_pruning_using_neighbors;
    public bool prune_intra_mode_using_best_sad_so_far;
    public bool check_only_zero_zeromv_on_large_blocks;
    public bool disable_cdf_update_non_reference_frame;
    public bool prune_compoundmode_with_singlemode_var;
    public bool skip_compound_based_on_var;
    public int set_zeromv_skip_based_on_source_sad;
    public bool use_adaptive_subpel_search;
    public bool enable_ref_short_signaling;
    public bool check_globalmv_on_single_ref;
    public bool increase_color_thresh_palette;
    public int higher_thresh_scene_detection;
    public int skip_newmv_flat_blocks_screen;
    public int skip_encoding_non_reference_slide_change;
    public int rc_faster_convergence_static;
    public int skip_newmv_mode_sad_screen;
}

/// <summary>SPEED_FEATURES (speed_features.h).</summary>
internal sealed partial class AomSpeedFeatures
{
    public readonly AomHighLevelSpeedFeatures hl_sf = new();
    public readonly AomFirstPassSpeedFeatures fp_sf = new();
    public readonly AomTplSpeedFeatures tpl_sf = new();
    public readonly AomGlobalMotionSpeedFeatures gm_sf = new();
    public readonly AomPartitionSpeedFeatures part_sf = new();
    public readonly AomMvSpeedFeatures mv_sf = new();
    public readonly AomInterModeSpeedFeatures inter_sf = new();
    public readonly AomInterpFilterSpeedFeatures interp_sf = new();
    public readonly AomIntraModeSpeedFeatures intra_sf = new();
    public readonly AomTxSpeedFeatures tx_sf = new();
    public readonly AomRdCalcSpeedFeatures rd_sf = new();
    public readonly AomWinnerModeSpeedFeatures winner_mode_sf = new();
    public readonly AomLoopFilterSpeedFeatures lpf_sf = new();
    public readonly AomRealTimeSpeedFeatures rt_sf = new();
}

// ---- the port

/// <summary>The AV1_COMP / AV1EncoderConfig state the speed-feature functions read (all-intra encoder).</summary>
internal sealed partial class AomSpeedFeatureInputs
{
    public AomSpeedFeatureInputs Clone() => (AomSpeedFeatureInputs)MemberwiseClone();
    public int Width, Height;                       // cm->width, cm->height
    public bool UseHighBitDepth;                    // oxcf.use_highbitdepth
    public bool AllowScreenContentTools;            // cm->features.allow_screen_content_tools
    public bool UseScreenContentTools;              // cpi->use_screen_content_tools
    public bool IsScreenContentType;                // cpi->is_screen_content_type (not read on the all-intra path)
    public int FrContentType = FC_NORMAL;           // cpi->twopass_frame.fr_content_type
    public int BaseQindex;                          // cm->quant_params.base_qindex
    public int FrameType = KEY_FRAME;               // cm->current_frame.frame_type
    public int UpdateType = KF_UPDATE;              // ppi->gf_group.update_type[cpi->gf_frame_index]
    public int DisableTrellisQuant = 3;             // oxcf.algo_cfg.disable_trellis_quant (libaom default 3)
    public int BestAllowedQ, WorstAllowedQ = 255;   // oxcf.rc_cfg.best_allowed_q / worst_allowed_q
    public bool EnableTxSizeSearch = true;          // oxcf.txfm_cfg.enable_tx_size_search
    public int NumWorkers = 1;                      // cpi->mt_info.num_workers
    public int RowMt = 1;                           // oxcf.row_mt
    public int Pass = AOM_RC_ONE_PASS;              // oxcf.pass
    public bool LapEnabled;                         // ppi->lap_enabled
    public int CompressorStage = ENCODE_STAGE;      // cpi->compressor_stage
    public int GfCbrBoostPct;                       // oxcf.rc_cfg.gf_cbr_boost_pct
    public int Tuning = AOM_TUNE_PSNR;              // oxcf.tune_cfg.tuning (not read on the all-intra path)
    public int Mode = ALLINTRA;                     // oxcf.mode (GOOD / ALLINTRA / REALTIME)
    public int Sharpness;                           // oxcf.algo_cfg.sharpness
    public int GfFrameType = KEY_FRAME;             // ppi->gf_group.frame_type[cpi->gf_frame_index]
    public int UnderShootPct = 25, OverShootPct = 25; // oxcf.rc_cfg.under_shoot_pct / over_shoot_pct

    public bool FrameIsIntraOnly => FrameType == KEY_FRAME || FrameType == INTRA_ONLY_FRAME;   // frame_is_intra_only
    public bool IsLosslessRequested => BestAllowedQ == 0 && WorstAllowedQ == 0;             // is_lossless_requested
    public bool IsStatGenerationStage => Pass == AOM_RC_FIRST_PASS || CompressorStage == LAP_STAGE;
    public bool HasNoStatsStage => Pass == AOM_RC_ONE_PASS && !LapEnabled;   // (mode != REALTIME for all-intra)
    /// <summary>frame_is_boosted / frame_is_kf_gf_arf.</summary>
    public bool FrameIsBoosted => FrameIsIntraOnly || UpdateType == ARF_UPDATE || UpdateType == GF_UPDATE;
}

/// <summary>The sequence-header tool flags the speed-feature setup clears while !ppi->seq_params_locked.</summary>
internal sealed class AomSpeedFeatureSeqFlags
{
    public bool SeqParamsLocked;               // ppi->seq_params_locked
    public int enable_dist_wtd_comp = 1;       // seq_params->order_hint_info.enable_dist_wtd_comp
    public int enable_dual_filter = 1;         // seq_params->enable_dual_filter
    public int enable_restoration = 1;         // seq_params->enable_restoration
    public int enable_interintra_compound = 1; // seq_params->enable_interintra_compound
    public int enable_masked_compound = 1;     // seq_params->enable_masked_compound
}

/// <summary>WinnerModeParams (av1/encoder/encoder.h), filled from the speed features.</summary>
internal sealed class AomWinnerModeParams
{
    public readonly uint[] coeff_opt_thresholds = new uint[MODE_EVAL_TYPES * 2];     // [MODE_EVAL_TYPES][2]
    public readonly int[] tx_size_search_methods = new int[MODE_EVAL_TYPES];         // TX_SIZE_SEARCH_METHOD
    public readonly uint[] use_transform_domain_distortion = new uint[MODE_EVAL_TYPES];
    public readonly uint[] tx_domain_dist_threshold = new uint[MODE_EVAL_TYPES];
    public readonly uint[] skip_txfm_level = new uint[MODE_EVAL_TYPES];
    public readonly uint[] predict_dc_level = new uint[MODE_EVAL_TYPES];
}

internal sealed partial class AomSpeedFeatures
{
    private const int MAX_MESH_SPEED = 5;
    private const int TX_DOMAIN_DIST_LEVELS = 4;
    private const uint UINT_MAX = uint.MaxValue;

    // good_quality_mesh_patterns / intrabc_mesh_patterns [MAX_MESH_SPEED + 1][MAX_MESH_STEP] as {range, interval}
    private static readonly int[,,] good_quality_mesh_patterns =
    {
        { { 64, 8 }, { 28, 4 }, { 15, 1 }, { 7, 1 } },
        { { 64, 8 }, { 28, 4 }, { 15, 1 }, { 7, 1 } },
        { { 64, 8 }, { 14, 2 }, { 7, 1 }, { 7, 1 } },
        { { 64, 16 }, { 24, 8 }, { 12, 4 }, { 7, 1 } },
        { { 64, 16 }, { 24, 8 }, { 12, 4 }, { 7, 1 } },
        { { 64, 16 }, { 24, 8 }, { 12, 4 }, { 7, 1 } },
    };

    private static readonly int[,,] intrabc_mesh_patterns =
    {
        { { 256, 1 }, { 256, 1 }, { 0, 0 }, { 0, 0 } },
        { { 256, 1 }, { 256, 1 }, { 0, 0 }, { 0, 0 } },
        { { 64, 1 }, { 64, 1 }, { 0, 0 }, { 0, 0 } },
        { { 64, 1 }, { 64, 1 }, { 0, 0 }, { 0, 0 } },
        { { 64, 4 }, { 16, 1 }, { 0, 0 }, { 0, 0 } },
        { { 64, 4 }, { 16, 1 }, { 0, 0 }, { 0, 0 } },
    };

    private static readonly uint[,] tx_domain_dist_thresholds =
    {
        { UINT_MAX, UINT_MAX, UINT_MAX }, { 22026, 22026, 22026 }, { 1377, 1377, 1377 }, { 0, 0, 0 },
    };

    private static readonly uint[,] tx_domain_dist_types = { { 0, 2, 0 }, { 1, 2, 0 }, { 2, 2, 0 }, { 2, 2, 2 } };

    // coeff_opt_thresholds[9][MODE_EVAL_TYPES][2]
    private static readonly uint[,,] coeff_opt_thresholds =
    {
        { { UINT_MAX, UINT_MAX }, { UINT_MAX, UINT_MAX }, { UINT_MAX, UINT_MAX } },
        { { 3200, UINT_MAX }, { 250, UINT_MAX }, { UINT_MAX, UINT_MAX } },
        { { 1728, UINT_MAX }, { 142, UINT_MAX }, { UINT_MAX, UINT_MAX } },
        { { 864, UINT_MAX }, { 142, UINT_MAX }, { UINT_MAX, UINT_MAX } },
        { { 432, UINT_MAX }, { 86, UINT_MAX }, { UINT_MAX, UINT_MAX } },
        { { 864, 97 }, { 142, 16 }, { UINT_MAX, UINT_MAX } },
        { { 432, 97 }, { 86, 16 }, { UINT_MAX, UINT_MAX } },
        { { 216, 25 }, { 86, 10 }, { UINT_MAX, UINT_MAX } },
        { { 216, 25 }, { 0, 10 }, { UINT_MAX, UINT_MAX } },
    };

    private static readonly int[,] tx_size_search_methods =
    {
        { USE_FULL_RD, USE_LARGESTALL, USE_FULL_RD },
        { USE_FAST_RD, USE_LARGESTALL, USE_FULL_RD },
        { USE_LARGESTALL, USE_LARGESTALL, USE_FULL_RD },
        { USE_LARGESTALL, USE_LARGESTALL, USE_LARGESTALL },
    };

    private static readonly uint[,] predict_skip_levels = { { 0, 0, 0 }, { 1, 1, 1 }, { 1, 2, 1 } };

    private static readonly uint[,] predict_dc_levels = { { 0, 0, 0 }, { 1, 1, 0 }, { 2, 2, 0 }, { 2, 2, 2 } };

    // gm_available_reference_frames[GM_DISABLE_SEARCH + 1] (!CONFIG_FPMT_TEST)
    private static readonly int[] gm_available_reference_frames =
        { INTER_REFS_PER_FRAME, INTER_REFS_PER_FRAME - 2, INTER_REFS_PER_FRAME - 3, 0 };

    private static void CopyCoeffOptThresholds(AomWinnerModeParams w, int level)
    {
        for (int i = 0; i < MODE_EVAL_TYPES; i++)
            for (int j = 0; j < 2; j++) w.coeff_opt_thresholds[i * 2 + j] = coeff_opt_thresholds[level, i, j];
    }

    /// <summary>set_txfm_rd_gate_level.</summary>
    private static void SetTxfmRdGateLevel(int[] txfm_rd_gate_level, int level)
    {
        for (int idx = 0; idx < TX_SEARCH_CASES; idx++) txfm_rd_gate_level[idx] = level;
    }

    /// <summary>set_allintra_speed_feature_framesize_dependent.</summary>
    private static void SetAllintraFramesizeDependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
    {
        int minDim = Math.Min(cpi.Width, cpi.Height);
        bool is_480p_or_larger = minDim >= 480;
        bool is_720p_or_larger = minDim >= 720;
        bool is_1080p_or_larger = minDim >= 1080;
        bool is_4k_or_larger = minDim >= 2160;
        bool use_hbd = cpi.UseHighBitDepth;

        if (is_480p_or_larger)
        {
            sf.part_sf.use_square_partition_only_threshold = BLOCK_128X128;
            if (is_720p_or_larger)
                sf.part_sf.auto_max_partition_based_on_simple_motion = ADAPT_PRED;
            else
                sf.part_sf.auto_max_partition_based_on_simple_motion = RELAXED_PRED;
        }
        else
        {
            sf.part_sf.use_square_partition_only_threshold = BLOCK_64X64;
            sf.part_sf.auto_max_partition_based_on_simple_motion = DIRECT_PRED;
            if (use_hbd) sf.tx_sf.prune_tx_size_level = 1;
        }

        if (is_4k_or_larger) sf.part_sf.default_min_partition_size = BLOCK_8X8;

        if (!is_720p_or_larger)
        {
            sf.part_sf.ml_partition_search_breakout_thresh[0] = -1.0f;
            sf.part_sf.ml_partition_search_breakout_thresh[1] = 0.993307f;
            sf.part_sf.ml_partition_search_breakout_thresh[2] = 0.952574f;
            sf.part_sf.ml_partition_search_breakout_thresh[3] = 0.924142f;
            sf.part_sf.ml_partition_search_breakout_thresh[4] = 0.880797f;
            sf.part_sf.ml_early_term_after_part_split_level = 1;
        }

        sf.part_sf.ml_partition_search_breakout_model_index = 0;

        if (is_720p_or_larger) sf.mv_sf.use_downsampled_sad = 2;

        if (speed >= 1)
        {
            sf.part_sf.ml_4_partition_search_level_index = 1;
            if (is_720p_or_larger)
                sf.part_sf.use_square_partition_only_threshold = BLOCK_128X128;
            else if (is_480p_or_larger)
                sf.part_sf.use_square_partition_only_threshold = BLOCK_64X64;
            else
                sf.part_sf.use_square_partition_only_threshold = BLOCK_32X32;

            if (is_720p_or_larger)
            {
                sf.part_sf.ml_partition_search_breakout_thresh[0] = 0.5f;
                sf.part_sf.ml_partition_search_breakout_thresh[1] = 0.5042595622791082f;
                sf.part_sf.ml_partition_search_breakout_thresh[2] = 0.5f;
                sf.part_sf.ml_partition_search_breakout_thresh[3] = 0.8378425823517456f;
                sf.part_sf.ml_partition_search_breakout_thresh[4] = 0.8047585616503903f;
                sf.part_sf.ml_partition_search_breakout_model_index = 1;
            }
            else
            {
                sf.part_sf.ml_partition_search_breakout_thresh[0] = -1.0f;
                sf.part_sf.ml_partition_search_breakout_thresh[1] = 0.952574f;
                sf.part_sf.ml_partition_search_breakout_thresh[2] = 0.952574f;
                sf.part_sf.ml_partition_search_breakout_thresh[3] = 0.924142f;
                sf.part_sf.ml_partition_search_breakout_thresh[4] = 0.880797f;
            }
            sf.part_sf.ml_early_term_after_part_split_level = 2;
        }

        if (speed >= 2)
        {
            sf.part_sf.ml_4_partition_search_level_index = 2;
            if (is_720p_or_larger)
                sf.part_sf.use_square_partition_only_threshold = BLOCK_64X64;
            else
                sf.part_sf.use_square_partition_only_threshold = BLOCK_32X32;

            if (is_720p_or_larger)
            {
                sf.part_sf.ml_partition_search_breakout_thresh[0] = 0.5f;
                sf.part_sf.ml_partition_search_breakout_thresh[1] = 0.5042595622791082f;
                sf.part_sf.ml_partition_search_breakout_thresh[2] = 0.5f;
                sf.part_sf.ml_partition_search_breakout_thresh[3] = 0.8378425823517456f;
                sf.part_sf.ml_partition_search_breakout_thresh[4] = 0.8047585616503903f;
                sf.part_sf.ml_partition_search_breakout_model_index = 1;
            }

            if (is_720p_or_larger)
            {
                sf.part_sf.partition_search_breakout_dist_thr = 1 << 24;
                sf.part_sf.partition_search_breakout_rate_thr = 120;
            }
            else
            {
                sf.part_sf.partition_search_breakout_dist_thr = 1 << 22;
                sf.part_sf.partition_search_breakout_rate_thr = 100;
            }

            if (is_480p_or_larger)
            {
                sf.tx_sf.tx_type_search.prune_tx_type_using_stats = 1;
                if (use_hbd) sf.tx_sf.prune_tx_size_level = 2;
            }
            else
            {
                if (use_hbd) sf.tx_sf.prune_tx_size_level = 3;
            }
        }

        if (speed >= 3)
        {
            sf.part_sf.ml_early_term_after_part_split_level = 0;
            sf.part_sf.ml_4_partition_search_level_index = 3;

            if (is_720p_or_larger)
            {
                for (int i = 0; i < PARTITION_BLOCK_SIZES; ++i)
                    sf.part_sf.ml_partition_search_breakout_thresh[i] = -1;   // -1 means not enabled.
                sf.part_sf.ml_partition_search_breakout_model_index = 0;
            }

            if (is_720p_or_larger)
            {
                sf.part_sf.partition_search_breakout_dist_thr = 1 << 25;
                sf.part_sf.partition_search_breakout_rate_thr = 200;
            }
            else
            {
                sf.part_sf.max_intra_bsize = BLOCK_32X32;
                sf.part_sf.partition_search_breakout_dist_thr = 1 << 23;
                sf.part_sf.partition_search_breakout_rate_thr = 120;
            }
            if (use_hbd) sf.tx_sf.prune_tx_size_level = 3;
        }

        if (speed >= 4)
        {
            if (is_720p_or_larger)
                sf.part_sf.partition_search_breakout_dist_thr = 1 << 26;
            else
                sf.part_sf.partition_search_breakout_dist_thr = 1 << 24;

            if (is_480p_or_larger) sf.tx_sf.tx_type_search.prune_tx_type_using_stats = 2;
        }

        if (speed >= 6)
        {
            if (is_720p_or_larger)
                sf.part_sf.auto_max_partition_based_on_simple_motion = NOT_IN_USE;
            else if (is_480p_or_larger)
                sf.part_sf.auto_max_partition_based_on_simple_motion = DIRECT_PRED;

            if (is_1080p_or_larger) sf.part_sf.default_min_partition_size = BLOCK_8X8;

            sf.part_sf.use_square_partition_only_threshold = BLOCK_16X16;
        }

        if (speed >= 8)
        {
            if (!is_480p_or_larger) sf.rt_sf.nonrd_check_partition_merge_mode = 2;
            if (is_720p_or_larger) sf.rt_sf.force_large_partition_blocks_intra = 1;
        }

        if (speed >= 9)
        {
            if (!is_4k_or_larger)
            {
                sf.inter_sf.coeff_cost_upd_level = INTERNAL_COST_UPD_OFF;
                sf.inter_sf.mode_cost_upd_level = INTERNAL_COST_UPD_OFF;
            }
        }
    }

    /// <summary>set_allintra_speed_features_framesize_independent.</summary>
    private static void SetAllintraFramesizeIndependent(AomSpeedFeatureInputs cpi, AomSpeedFeatures sf, int speed)
    {
        bool allow_screen_content_tools = cpi.AllowScreenContentTools;
        bool use_hbd = cpi.UseHighBitDepth;

        sf.part_sf.less_rectangular_check_level = 1;
        sf.part_sf.ml_prune_partition = 1;
        sf.part_sf.prune_ext_partition_types_search_level = 1;
        sf.part_sf.prune_part4_search = 2;
        sf.part_sf.simple_motion_search_prune_rect = 1;
        sf.part_sf.ml_predict_breakout_level = use_hbd ? 1 : 3;
        sf.part_sf.reuse_prev_rd_results_for_part_ab = 1;
        sf.part_sf.use_best_rd_for_pruning = 1;

        sf.intra_sf.intra_pruning_with_hog = 1;
        sf.intra_sf.prune_luma_palette_size_search_level = 1;
        sf.intra_sf.dv_cost_upd_level = INTERNAL_COST_UPD_OFF;
        sf.intra_sf.early_term_chroma_palette_size_search = 1;

        sf.tx_sf.adaptive_txb_search_level = 1;
        sf.tx_sf.intra_tx_size_search_init_depth_sqr = 1;
        sf.tx_sf.model_based_prune_tx_search_level = 1;
        sf.tx_sf.tx_type_search.use_reduced_intra_txset = 1;
        sf.tx_sf.use_chroma_trellis_rd_mult = 1;

        sf.rt_sf.use_nonrd_pick_mode = 0;
        sf.rt_sf.discount_color_cost = 0;
        sf.rt_sf.use_real_time_ref_set = 0;

        if (cpi.FrContentType == FC_GRAPHICS_ANIMATION || cpi.UseScreenContentTools)
            sf.mv_sf.exhaustive_searches_thresh = 1 << 20;
        else
            sf.mv_sf.exhaustive_searches_thresh = 1 << 25;

        sf.rd_sf.perform_coeff_opt = 1;
        sf.hl_sf.superres_auto_search_type = SUPERRES_AUTO_DUAL;

        if (speed >= 1)
        {
            sf.part_sf.intra_cnn_based_part_prune_level = allow_screen_content_tools ? 0 : 2;
            sf.part_sf.simple_motion_search_prune_agg = allow_screen_content_tools ? NO_PRUNING : SIMPLE_AGG_LVL1;
            sf.part_sf.simple_motion_search_early_term_none = 1;
            sf.part_sf.simple_motion_search_split = allow_screen_content_tools ? 1 : 2;
            sf.part_sf.ml_predict_breakout_level = use_hbd ? 2 : 3;
            sf.part_sf.reuse_best_prediction_for_part_ab = 1;

            sf.mv_sf.exhaustive_searches_thresh <<= 1;
            sf.mv_sf.prune_intrabc_candidate_block_hash_search = 1;

            sf.intra_sf.prune_palette_search_level = 1;
            sf.intra_sf.prune_luma_palette_size_search_level = 2;
            sf.intra_sf.top_intra_model_count_allowed = 3;

            sf.tx_sf.adaptive_txb_search_level = 2;
            sf.tx_sf.inter_tx_size_search_init_depth_rect = 1;
            sf.tx_sf.inter_tx_size_search_init_depth_sqr = 1;
            sf.tx_sf.intra_tx_size_search_init_depth_rect = 1;
            sf.tx_sf.model_based_prune_tx_search_level = 0;
            sf.tx_sf.tx_type_search.ml_tx_split_thresh = 4000;
            sf.tx_sf.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_2;
            sf.tx_sf.tx_type_search.skip_tx_search = 1;

            sf.rd_sf.perform_coeff_opt = 2;
            sf.rd_sf.tx_domain_dist_level = 1;
            sf.rd_sf.tx_domain_dist_thres_level = 1;

            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL1;
            sf.lpf_sf.dual_sgr_penalty_level = 1;
            sf.lpf_sf.enable_sgr_ep_pruning = 1;
        }

        if (speed >= 2)
        {
            sf.mv_sf.auto_mv_step_size = 1;

            sf.part_sf.simple_motion_search_prune_agg = allow_screen_content_tools ? NO_PRUNING : SIMPLE_AGG_LVL2;
            sf.intra_sf.disable_smooth_intra = 1;
            sf.intra_sf.intra_pruning_with_hog = 2;
            sf.intra_sf.prune_filter_intra_level = 1;

            sf.rd_sf.perform_coeff_opt = 3;

            sf.lpf_sf.prune_wiener_based_on_src_var = 1;
            sf.lpf_sf.prune_sgr_based_on_wiener = 1;
        }

        if (speed >= 3)
        {
            sf.hl_sf.high_precision_mv_usage = CURRENT_Q;
            sf.hl_sf.recode_loop = ALLOW_RECODE_KFARFGF;
            sf.hl_sf.screen_detection_mode2_fast_detection = 1;

            sf.part_sf.less_rectangular_check_level = 2;
            sf.part_sf.simple_motion_search_prune_agg = SIMPLE_AGG_LVL3;
            sf.part_sf.prune_ext_part_using_split_info = 1;

            sf.mv_sf.full_pixel_search_level = 1;
            sf.mv_sf.search_method = DIAMOND;

            sf.intra_sf.chroma_intra_pruning_with_hog = 2;
            sf.intra_sf.intra_pruning_with_hog = 3;
            sf.intra_sf.prune_palette_search_level = 2;

            sf.tx_sf.adaptive_txb_search_level = 2;
            sf.tx_sf.tx_type_search.use_skip_flag_prediction = 2;
            sf.tx_sf.use_rd_based_breakout_for_intra_tx_search = true;

            sf.lpf_sf.prune_sgr_based_on_wiener = allow_screen_content_tools ? 1 : 2;
            sf.lpf_sf.disable_loop_restoration_chroma = 0;
            sf.lpf_sf.reduce_wiener_window_size = 1;
            sf.lpf_sf.prune_wiener_based_on_src_var = 2;
        }

        if (speed >= 4)
        {
            sf.mv_sf.subpel_search_method = SUBPEL_TREE_PRUNED_MORE;

            sf.part_sf.simple_motion_search_prune_agg = SIMPLE_AGG_LVL4;
            sf.part_sf.simple_motion_search_reduce_search_steps = 4;
            sf.part_sf.prune_ext_part_using_split_info = 2;
            sf.part_sf.early_term_after_none_split = 1;
            sf.part_sf.ml_predict_breakout_level = 3;

            sf.intra_sf.prune_chroma_modes_using_luma_winner = 1;

            sf.mv_sf.simple_motion_subpel_force_stop = HALF_PEL;

            sf.tpl_sf.prune_starting_mv = 2;
            sf.tpl_sf.subpel_force_stop = HALF_PEL;
            sf.tpl_sf.search_method = FAST_BIGDIA;

            sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = 2;
            sf.tx_sf.tx_type_search.fast_intra_tx_type_search = 2;
            sf.tx_sf.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_3;
            sf.tx_sf.tx_type_search.prune_tx_type_est_rd = 1;

            sf.rd_sf.perform_coeff_opt = 5;
            sf.rd_sf.tx_domain_dist_thres_level = 3;

            sf.lpf_sf.lpf_pick = LPF_PICK_FROM_FULL_IMAGE_NON_DUAL;
            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL3;

            sf.mv_sf.reduce_search_range = 1;
            sf.mv_sf.hash_max_8x8_intrabc_blocks = 1;

            sf.winner_mode_sf.enable_winner_mode_for_coeff_opt = 1;
            sf.winner_mode_sf.enable_winner_mode_for_use_tx_domain_dist = 1;
            sf.winner_mode_sf.multi_winner_mode_type = MULTI_WINNER_MODE_DEFAULT;
            sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch = 1;
        }

        if (speed >= 5)
        {
            sf.part_sf.simple_motion_search_prune_agg = SIMPLE_AGG_LVL5;
            sf.part_sf.ext_partition_eval_thresh = allow_screen_content_tools ? BLOCK_8X8 : BLOCK_16X16;
            sf.part_sf.intra_cnn_based_part_prune_level = allow_screen_content_tools ? 1 : 2;

            sf.intra_sf.chroma_intra_pruning_with_hog = 3;

            sf.lpf_sf.use_coarse_filter_level_search = 0;
            // Disable Wiener and Self-guided Loop restoration filters.
            sf.lpf_sf.disable_wiener_filter = true;
            sf.lpf_sf.disable_sgr_filter = true;

            sf.mv_sf.prune_mesh_search = PRUNE_MESH_SEARCH_LVL_2;

            sf.winner_mode_sf.multi_winner_mode_type = MULTI_WINNER_MODE_FAST;
        }

        if (speed >= 6)
        {
            sf.intra_sf.prune_smooth_intra_mode_for_chroma = true;
            sf.intra_sf.prune_filter_intra_level = 2;
            sf.intra_sf.chroma_intra_pruning_with_hog = 4;
            sf.intra_sf.intra_pruning_with_hog = 4;
            sf.intra_sf.cfl_search_range = 1;
            sf.intra_sf.top_intra_model_count_allowed = 2;
            sf.intra_sf.adapt_top_model_rd_count_using_neighbors = 1;
            sf.intra_sf.prune_luma_odd_delta_angles_in_intra = 1;

            sf.part_sf.prune_rectangular_split_based_on_qidx = allow_screen_content_tools ? 0 : 2;
            sf.part_sf.prune_rect_part_using_4x4_var_deviation = true;
            sf.part_sf.prune_rect_part_using_none_pred_mode = true;
            sf.part_sf.prune_sub_8x8_partition_level = allow_screen_content_tools ? 0 : 1;
            sf.part_sf.prune_part4_search = 3;
            sf.part_sf.default_max_partition_size = BLOCK_32X32;

            sf.mv_sf.use_bsize_dependent_search_method = 3;
            sf.mv_sf.intrabc_search_level = 1;

            sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = 3;
            sf.tx_sf.tx_type_search.prune_tx_type_est_rd = 0;
            sf.tx_sf.prune_intra_tx_depths_using_nn = true;

            sf.rd_sf.perform_coeff_opt = 6;
            sf.rd_sf.tx_domain_dist_level = 3;

            sf.lpf_sf.cdef_pick_method = CDEF_FAST_SEARCH_LVL4;
            sf.lpf_sf.lpf_pick = LPF_PICK_FROM_Q;

            sf.winner_mode_sf.multi_winner_mode_type = MULTI_WINNER_MODE_OFF;
            sf.winner_mode_sf.prune_winner_mode_eval_level = 1;
            sf.winner_mode_sf.dc_blk_pred_level = 1;
        }

        if (speed >= 7)
        {
            sf.part_sf.default_min_partition_size = BLOCK_8X8;
            sf.part_sf.partition_search_type = VAR_BASED_PARTITION;
            sf.lpf_sf.cdef_pick_method = CDEF_PICK_FROM_Q;
            sf.rt_sf.mode_search_skip_flags |= FLAG_SKIP_INTRA_DIRMISMATCH;
            sf.rt_sf.var_part_split_threshold_shift = 7;
        }

        if (speed >= 8)
        {
            sf.rt_sf.hybrid_intra_pickmode = 2;
            sf.rt_sf.use_nonrd_pick_mode = 1;
            sf.rt_sf.nonrd_check_partition_merge_mode = 1;
            sf.rt_sf.var_part_split_threshold_shift = 8;
            sf.rt_sf.prune_palette_search_nonrd = 1;
            // Set mask for intra modes.
            for (int i = 0; i < BLOCK_SIZES; ++i)
                if (i >= BLOCK_32X32)
                    sf.rt_sf.intra_y_mode_bsize_mask_nrd[i] = INTRA_DC;
                else
                    // Use DC, H, V intra mode for block sizes < 32X32.
                    sf.rt_sf.intra_y_mode_bsize_mask_nrd[i] = INTRA_DC_H_V;
        }

        if (speed >= 9)
        {
            sf.inter_sf.coeff_cost_upd_level = INTERNAL_COST_UPD_SBROW;
            sf.inter_sf.mode_cost_upd_level = INTERNAL_COST_UPD_SBROW;

            sf.rt_sf.nonrd_check_partition_merge_mode = 0;
            sf.rt_sf.hybrid_intra_pickmode = 0;
            sf.rt_sf.var_part_split_threshold_shift = 7;
            sf.rt_sf.vbp_prune_16x16_split_using_min_max_sub_blk_var = true;
            sf.rt_sf.prune_h_pred_using_best_mode_so_far = true;
            sf.rt_sf.enable_intra_mode_pruning_using_neighbors = true;
            sf.rt_sf.prune_intra_mode_using_best_sad_so_far = true;
        }

        if (sf.intra_sf.prune_chroma_modes_using_luma_winner != 0)
            sf.intra_sf.chroma_intra_pruning_with_hog = 0;
    }

    /// <summary>init_hl_sf.</summary>
    private static void InitHlSf(AomHighLevelSpeedFeatures hl_sf)
    {
        hl_sf.frame_parameter_update = 1;
        hl_sf.recode_loop = ALLOW_RECODE;
        hl_sf.recode_tolerance = 25;
        hl_sf.high_precision_mv_usage = CURRENT_Q;
        hl_sf.superres_auto_search_type = SUPERRES_AUTO_ALL;
        hl_sf.disable_extra_sc_testing = 0;
        hl_sf.second_alt_ref_filtering = 1;
        hl_sf.adjust_num_frames_for_arf_filtering = 0;
        hl_sf.accurate_bit_estimate = 0;
        hl_sf.weight_calc_level_in_tf = 0;
        hl_sf.allow_sub_blk_me_in_tf = 0;
        hl_sf.ref_frame_mvs_lvl = 0;
        hl_sf.screen_detection_mode2_fast_detection = 0;
    }

    /// <summary>init_fp_sf.</summary>
    private static void InitFpSf(AomFirstPassSpeedFeatures fp_sf)
    {
        fp_sf.reduce_mv_step_param = 3;
        fp_sf.skip_motion_search_threshold = 0;
        fp_sf.disable_recon = 0;
        fp_sf.skip_zeromv_motion_search = 0;
    }

    /// <summary>init_tpl_sf.</summary>
    private static void InitTplSf(AomTplSpeedFeatures tpl_sf)
    {
        tpl_sf.gop_length_decision_method = 1;
        tpl_sf.prune_intra_modes = 0;
        tpl_sf.prune_starting_mv = 0;
        tpl_sf.reduce_first_step_size = 0;
        tpl_sf.skip_alike_starting_mv = 0;
        tpl_sf.subpel_force_stop = EIGHTH_PEL;
        tpl_sf.search_method = NSTEP;
        tpl_sf.prune_ref_frames_in_tpl = 0;
        tpl_sf.allow_compound_pred = 1;
        tpl_sf.use_y_only_rate_distortion = 0;
        tpl_sf.use_sad_for_mode_decision = 0;
        tpl_sf.reduce_num_frames = 0;
    }

    /// <summary>init_gm_sf.</summary>
    private static void InitGmSf(AomGlobalMotionSpeedFeatures gm_sf)
    {
        gm_sf.gm_search_type = GM_FULL_SEARCH;
        gm_sf.prune_ref_frame_for_gm_search = 0;
        gm_sf.prune_zero_mv_with_sse = 0;
        gm_sf.disable_gm_search_based_on_stats = 0;
        gm_sf.downsample_level = 0;
        gm_sf.num_refinement_steps = GM_MAX_REFINEMENT_STEPS;
        gm_sf.gm_erroradv_tr_level = 0;
    }

    /// <summary>init_part_sf.</summary>
    private static void InitPartSf(AomPartitionSpeedFeatures part_sf)
    {
        part_sf.partition_search_type = SEARCH_PARTITION;
        part_sf.less_rectangular_check_level = 0;
        part_sf.use_square_partition_only_threshold = BLOCK_128X128;
        part_sf.auto_max_partition_based_on_simple_motion = NOT_IN_USE;
        part_sf.default_max_partition_size = BLOCK_LARGEST;
        part_sf.default_min_partition_size = BLOCK_4X4;
        part_sf.adjust_var_based_rd_partitioning = 0;
        part_sf.max_intra_bsize = BLOCK_LARGEST;
        // This setting only takes effect when partition_search_type is set to FIXED_PARTITION.
        part_sf.fixed_partition_size = BLOCK_16X16;
        part_sf.partition_search_breakout_dist_thr = 0;
        part_sf.partition_search_breakout_rate_thr = 0;
        part_sf.prune_ext_partition_types_search_level = 0;
        part_sf.prune_part4_search = 0;
        part_sf.ml_prune_partition = 0;
        part_sf.ml_early_term_after_part_split_level = 0;
        for (int i = 0; i < PARTITION_BLOCK_SIZES; ++i)
            part_sf.ml_partition_search_breakout_thresh[i] = -1;   // -1 means not enabled.
        part_sf.ml_partition_search_breakout_model_index = 0;
        part_sf.ml_4_partition_search_level_index = 0;
        part_sf.simple_motion_search_prune_agg = SIMPLE_AGG_LVL0;
        part_sf.simple_motion_search_split = 0;
        part_sf.simple_motion_search_prune_rect = 0;
        part_sf.simple_motion_search_early_term_none = 0;
        part_sf.simple_motion_search_reduce_search_steps = 0;
        part_sf.intra_cnn_based_part_prune_level = 0;
        part_sf.ext_partition_eval_thresh = BLOCK_8X8;
        part_sf.rect_partition_eval_thresh = BLOCK_128X128;
        part_sf.ext_part_eval_based_on_cur_best = 0;
        part_sf.prune_ext_part_using_split_info = 0;
        part_sf.prune_rectangular_split_based_on_qidx = 0;
        part_sf.prune_rect_part_using_4x4_var_deviation = false;
        part_sf.prune_rect_part_using_none_pred_mode = false;
        part_sf.early_term_after_none_split = 0;
        part_sf.ml_predict_breakout_level = 0;
        part_sf.prune_sub_8x8_partition_level = 0;
        part_sf.simple_motion_search_rect_split = 0;
        part_sf.reuse_prev_rd_results_for_part_ab = 0;
        part_sf.reuse_best_prediction_for_part_ab = 0;
        part_sf.use_best_rd_for_pruning = 0;
        part_sf.skip_non_sq_part_based_on_none = 0;
        part_sf.disable_8x8_part_based_on_qidx = 0;
        part_sf.split_partition_penalty_level = 0;
        part_sf.prune_h_or_v_4part_using_sms_info = false;
    }

    /// <summary>init_mv_sf.</summary>
    private static void InitMvSf(AomMvSpeedFeatures mv_sf)
    {
        mv_sf.full_pixel_search_level = 0;
        mv_sf.auto_mv_step_size = 0;
        mv_sf.exhaustive_searches_thresh = 0;
        mv_sf.obmc_full_pixel_search_level = 0;
        mv_sf.prune_mesh_search = PRUNE_MESH_SEARCH_DISABLED;
        mv_sf.reduce_search_range = 0;
        mv_sf.search_method = NSTEP;
        mv_sf.simple_motion_subpel_force_stop = EIGHTH_PEL;
        mv_sf.subpel_force_stop = EIGHTH_PEL;
        mv_sf.subpel_iters_per_step = 2;
        mv_sf.subpel_search_method = SUBPEL_TREE;
        mv_sf.use_accurate_subpel_search = USE_8_TAPS;
        mv_sf.use_bsize_dependent_search_method = 0;
        mv_sf.use_fullpel_costlist = 0;
        mv_sf.use_downsampled_sad = 0;
        mv_sf.disable_extensive_joint_motion_search = 0;
        mv_sf.disable_second_mv = 0;
        mv_sf.skip_fullpel_search_using_startmv_refmv = 0;
        mv_sf.warp_search_method = WARP_SEARCH_SQUARE;
        mv_sf.warp_search_iters = 8;
        mv_sf.use_intrabc = 1;
        mv_sf.prune_intrabc_candidate_block_hash_search = 0;
        mv_sf.intrabc_search_level = 0;
        mv_sf.hash_max_8x8_intrabc_blocks = 0;
    }

    /// <summary>init_inter_sf.</summary>
    private static void InitInterSf(AomInterModeSpeedFeatures inter_sf)
    {
        inter_sf.adaptive_rd_thresh = 0;
        inter_sf.model_based_post_interp_filter_breakout = 0;
        inter_sf.reduce_inter_modes = 0;
        inter_sf.alt_ref_search_fp = 0;
        inter_sf.prune_single_ref = 0;
        inter_sf.prune_comp_ref_frames = 0;
        inter_sf.selective_ref_frame = 0;
        inter_sf.prune_ref_frame_for_rect_partitions = 0;
        inter_sf.fast_wedge_sign_estimate = 0;
        inter_sf.use_dist_wtd_comp_flag = DIST_WTD_COMP_ENABLED;
        inter_sf.reuse_inter_intra_mode = 0;
        inter_sf.mv_cost_upd_level = INTERNAL_COST_UPD_SB;
        inter_sf.coeff_cost_upd_level = INTERNAL_COST_UPD_SB;
        inter_sf.mode_cost_upd_level = INTERNAL_COST_UPD_SB;
        inter_sf.prune_inter_modes_based_on_tpl = 0;
        inter_sf.prune_nearmv_using_neighbors = PRUNE_NEARMV_OFF;
        inter_sf.prune_comp_search_by_single_result = 0;
        inter_sf.skip_repeated_ref_mv = 0;
        inter_sf.skip_newmv_in_drl = 0;
        inter_sf.inter_mode_rd_model_estimation = 0;
        inter_sf.prune_compound_using_single_ref = 0;
        inter_sf.prune_ext_comp_using_neighbors = 0;
        inter_sf.skip_ext_comp_nearmv_mode = 0;
        inter_sf.prune_comp_using_best_single_mode_ref = 0;
        inter_sf.prune_nearest_near_mv_using_refmv_weight = 0;
        inter_sf.disable_onesided_comp = 0;
        inter_sf.prune_mode_search_simple_translation = 0;
        inter_sf.prune_comp_type_by_comp_avg = 0;
        inter_sf.disable_interinter_wedge_newmv_search = 0;
        inter_sf.fast_interintra_wedge_search = 0;
        inter_sf.prune_comp_type_by_model_rd = 0;
        inter_sf.perform_best_rd_based_gating_for_chroma = 0;
        inter_sf.prune_obmc_prob_thresh = 0;
        inter_sf.disable_interinter_wedge_var_thresh = 0;
        inter_sf.disable_interintra_wedge_var_thresh = 0;
        inter_sf.prune_ref_mv_idx_search = 0;
        inter_sf.prune_warped_prob_thresh = 0;
        inter_sf.reuse_compound_type_decision = 0;
        inter_sf.prune_inter_modes_if_skippable = 0;
        inter_sf.disable_masked_comp = 0;
        inter_sf.enable_fast_compound_mode_search = 0;
        inter_sf.reuse_mask_search_results = 0;
        inter_sf.enable_fast_wedge_mask_search = 0;
        inter_sf.inter_mode_txfm_breakout = 0;
        inter_sf.limit_inter_mode_cands = 0;
        inter_sf.limit_txfm_eval_per_mode = 0;
        inter_sf.skip_arf_compound = 0;
        inter_sf.bias_warp_mode_rd_scale_pct = 0;
        inter_sf.bias_obmc_mode_rd_scale_pct = 0.0f;
        inter_sf.skip_cmp_using_top_cmp_avg_est_rd_lvl = 0;
        inter_sf.skip_interinter_wedge_search_based_on_mse = 0;
        SetTxfmRdGateLevel(inter_sf.txfm_rd_gate_level, 0);
    }

    /// <summary>init_interp_sf.</summary>
    private static void InitInterpSf(AomInterpFilterSpeedFeatures interp_sf)
    {
        interp_sf.adaptive_interp_filter_search = 0;
        interp_sf.cb_pred_filter_search = 0;
        interp_sf.disable_dual_filter = 0;
        interp_sf.skip_sharp_interp_filter_search = 0;
        interp_sf.use_fast_interpolation_filter_search = 0;
        interp_sf.use_interp_filter = 0;
        interp_sf.skip_interp_filter_search = 0;
        interp_sf.use_more_sharp_interp = 0;
        interp_sf.skip_model_rd_uv = 0;
    }

    /// <summary>init_intra_sf.</summary>
    private static void InitIntraSf(AomIntraModeSpeedFeatures intra_sf)
    {
        intra_sf.dv_cost_upd_level = INTERNAL_COST_UPD_SB;
        intra_sf.skip_intra_in_interframe = 1;
        intra_sf.intra_pruning_with_hog = 0;
        intra_sf.chroma_intra_pruning_with_hog = 0;
        intra_sf.prune_palette_search_level = 0;
        intra_sf.prune_luma_palette_size_search_level = 0;

        for (int i = 0; i < TX_SIZES; i++)
        {
            intra_sf.intra_y_mode_mask[i] = INTRA_ALL;
            intra_sf.intra_uv_mode_mask[i] = UV_INTRA_ALL;
        }
        intra_sf.disable_smooth_intra = 0;
        intra_sf.prune_smooth_intra_mode_for_chroma = false;
        intra_sf.prune_filter_intra_level = 0;
        intra_sf.prune_chroma_modes_using_luma_winner = 0;
        intra_sf.cfl_search_range = 3;
        intra_sf.top_intra_model_count_allowed = TOP_INTRA_MODEL_COUNT;
        intra_sf.adapt_top_model_rd_count_using_neighbors = 0;
        intra_sf.early_term_chroma_palette_size_search = 0;
        intra_sf.skip_filter_intra_in_inter_frames = 0;
        intra_sf.prune_luma_odd_delta_angles_in_intra = 0;
    }

    /// <summary>init_tx_sf.</summary>
    private static void InitTxSf(AomTxSpeedFeatures tx_sf)
    {
        tx_sf.inter_tx_size_search_init_depth_sqr = 0;
        tx_sf.inter_tx_size_search_init_depth_rect = 0;
        tx_sf.intra_tx_size_search_init_depth_rect = 0;
        tx_sf.intra_tx_size_search_init_depth_sqr = 0;
        tx_sf.tx_size_search_lgr_block = 0;
        tx_sf.model_based_prune_tx_search_level = 0;
        tx_sf.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_1;
        tx_sf.tx_type_search.ml_tx_split_thresh = 8500;
        tx_sf.tx_type_search.use_skip_flag_prediction = 1;
        tx_sf.tx_type_search.use_reduced_intra_txset = 0;
        tx_sf.tx_type_search.fast_intra_tx_type_search = 0;
        tx_sf.tx_type_search.fast_inter_tx_type_prob_thresh = int.MaxValue;
        tx_sf.tx_type_search.skip_tx_search = 0;
        tx_sf.tx_type_search.prune_tx_type_using_stats = 0;
        tx_sf.tx_type_search.prune_tx_type_est_rd = 0;
        tx_sf.tx_type_search.winner_mode_tx_type_pruning = 0;
        tx_sf.txb_split_cap = 1;
        tx_sf.adaptive_txb_search_level = 0;
        tx_sf.refine_fast_tx_search_results = 1;
        tx_sf.prune_tx_size_level = 0;
        tx_sf.prune_intra_tx_depths_using_nn = false;
        tx_sf.use_rd_based_breakout_for_intra_tx_search = false;
        tx_sf.prune_inter_tx_split_rd_eval_lvl = 0;
        tx_sf.use_chroma_trellis_rd_mult = 0;
    }

    /// <summary>init_rd_sf.</summary>
    private static void InitRdSf(AomRdCalcSpeedFeatures rd_sf, AomSpeedFeatureInputs oxcf)
    {
        int disable_trellis_quant = oxcf.DisableTrellisQuant;
        if (disable_trellis_quant == 3)
            rd_sf.optimize_coefficients = !oxcf.IsLosslessRequested ? NO_ESTIMATE_YRD_TRELLIS_OPT : NO_TRELLIS_OPT;
        else if (disable_trellis_quant == 2)
            rd_sf.optimize_coefficients = !oxcf.IsLosslessRequested ? FINAL_PASS_TRELLIS_OPT : NO_TRELLIS_OPT;
        else if (disable_trellis_quant == 0)
            rd_sf.optimize_coefficients = oxcf.IsLosslessRequested ? NO_TRELLIS_OPT : FULL_TRELLIS_OPT;
        else if (disable_trellis_quant == 1)
            rd_sf.optimize_coefficients = NO_TRELLIS_OPT;
        // else: assert(0) in libaom (NDEBUG: optimize_coefficients left unchanged); the value is range-checked to 0..3
        rd_sf.use_mb_rd_hash = 0;
        rd_sf.simple_model_rd_from_var = 0;
        rd_sf.tx_domain_dist_level = 0;
        rd_sf.tx_domain_dist_thres_level = 0;
        rd_sf.perform_coeff_opt = 0;
    }

    /// <summary>init_winner_mode_sf.</summary>
    private static void InitWinnerModeSf(AomWinnerModeSpeedFeatures winner_mode_sf)
    {
        winner_mode_sf.motion_mode_for_winner_cand = 0;
        winner_mode_sf.tx_size_search_level = 0;
        winner_mode_sf.enable_winner_mode_for_coeff_opt = 0;
        winner_mode_sf.enable_winner_mode_for_tx_size_srch = 0;
        winner_mode_sf.enable_winner_mode_for_use_tx_domain_dist = 0;
        winner_mode_sf.multi_winner_mode_type = 0;
        winner_mode_sf.dc_blk_pred_level = 0;
        winner_mode_sf.winner_mode_ifs = 0;
        winner_mode_sf.prune_winner_mode_eval_level = 0;
    }

    /// <summary>init_lpf_sf.</summary>
    private static void InitLpfSf(AomLoopFilterSpeedFeatures lpf_sf)
    {
        lpf_sf.disable_loop_restoration_chroma = 0;
        lpf_sf.disable_loop_restoration_luma = 0;
        lpf_sf.min_lr_unit_size = RESTORATION_PROC_UNIT_SIZE;
        lpf_sf.max_lr_unit_size = RESTORATION_UNITSIZE_MAX;
        lpf_sf.prune_wiener_based_on_src_var = 0;
        lpf_sf.prune_sgr_based_on_wiener = 0;
        lpf_sf.enable_sgr_ep_pruning = 0;
        lpf_sf.reduce_wiener_window_size = 0;
        lpf_sf.adaptive_luma_loop_filter_skip = 0;
        lpf_sf.skip_loop_filter_using_filt_error = 0;
        lpf_sf.lpf_pick = LPF_PICK_FROM_FULL_IMAGE;
        lpf_sf.use_coarse_filter_level_search = 0;
        lpf_sf.cdef_pick_method = CDEF_FULL_SEARCH;
        lpf_sf.zero_low_cdef_strengths = false;
        lpf_sf.dual_sgr_penalty_level = 0;
        lpf_sf.disable_wiener_filter = false;
        lpf_sf.disable_sgr_filter = false;
        lpf_sf.disable_wiener_coeff_refine_search = false;
        lpf_sf.use_downsampled_wiener_stats = 0;
        lpf_sf.switchable_lr_with_bias_level = 0;
        lpf_sf.adaptive_cdef_mode = 0;
    }

    /// <summary>init_rt_sf.</summary>
    private static void InitRtSf(AomRealTimeSpeedFeatures rt_sf)
    {
        rt_sf.check_intra_pred_nonrd = 0;
        rt_sf.skip_intra_pred = 0;
        rt_sf.estimate_motion_for_var_based_partition = 0;
        rt_sf.nonrd_check_partition_merge_mode = 0;
        rt_sf.nonrd_check_partition_split = 0;
        rt_sf.mode_search_skip_flags = 0;
        rt_sf.nonrd_prune_ref_frame_search = 0;
        rt_sf.use_nonrd_pick_mode = 0;
        rt_sf.discount_color_cost = 0;
        rt_sf.use_nonrd_altref_frame = 0;
        rt_sf.use_comp_ref_nonrd = 0;
        rt_sf.use_real_time_ref_set = 0;
        rt_sf.short_circuit_low_temp_var = 0;
        rt_sf.reuse_inter_pred_nonrd = 0;
        rt_sf.num_inter_modes_for_tx_search = int.MaxValue;
        rt_sf.use_nonrd_filter_search = 0;
        rt_sf.use_simple_rd_model = 0;
        rt_sf.hybrid_intra_pickmode = 0;
        rt_sf.prune_palette_search_nonrd = 0;
        rt_sf.source_metrics_sb_nonrd = 0;
        rt_sf.overshoot_detection_cbr = NO_DETECTION;
        rt_sf.check_scene_detection = 0;
        rt_sf.rc_adjust_keyframe = 0;
        rt_sf.rc_compute_spatial_var_sc_kf = 0;
        rt_sf.prefer_large_partition_blocks = 0;
        rt_sf.use_temporal_noise_estimate = 0;
        rt_sf.fullpel_search_step_param = 0;
        for (int i = 0; i < BLOCK_SIZES; ++i) rt_sf.intra_y_mode_bsize_mask_nrd[i] = INTRA_ALL;
        rt_sf.prune_hv_pred_modes_using_src_sad = false;
        rt_sf.nonrd_aggressive_skip = 0;
        rt_sf.skip_cdef_sb = 0;
        rt_sf.force_large_partition_blocks_intra = 0;
        rt_sf.skip_tx_no_split_var_based_partition = 0;
        rt_sf.skip_newmv_mode_based_on_sse = 0;
        rt_sf.gf_length_lvl = 0;
        rt_sf.prune_inter_modes_with_golden_ref = 0;
        rt_sf.prune_inter_modes_wrt_gf_arf_based_on_sad = 0;
        rt_sf.prune_inter_modes_using_temp_var = 0;
        rt_sf.reduce_mv_pel_precision_highmotion = 0;
        rt_sf.reduce_mv_pel_precision_lowcomplex = 0;
        rt_sf.prune_intra_mode_based_on_mv_range = 0;
        rt_sf.var_part_split_threshold_shift = 7;
        rt_sf.gf_refresh_based_on_qp = 0;
        rt_sf.use_rtc_tf = 0;
        rt_sf.use_idtx_nonrd = 0;
        rt_sf.prune_idtx_nonrd = 0;
        rt_sf.dct_only_palette_nonrd = 0;
        rt_sf.part_early_exit_zeromv = 0;
        rt_sf.sse_early_term_inter_search = EARLY_TERM_DISABLED;
        rt_sf.skip_lf_screen = 0;
        rt_sf.thresh_active_maps_skip_lf_cdef = 100;
        rt_sf.sad_based_adp_altref_lag = 0;
        rt_sf.partition_direct_merging = 0;
        rt_sf.var_part_based_on_qidx = 0;
        rt_sf.tx_size_level_based_on_qstep = 0;
        rt_sf.vbp_prune_16x16_split_using_min_max_sub_blk_var = false;
        rt_sf.prune_compoundmode_with_singlecompound_var = false;
        rt_sf.frame_level_mode_cost_update = false;
        rt_sf.prune_h_pred_using_best_mode_so_far = false;
        rt_sf.enable_intra_mode_pruning_using_neighbors = false;
        rt_sf.prune_intra_mode_using_best_sad_so_far = false;
        rt_sf.check_only_zero_zeromv_on_large_blocks = false;
        rt_sf.disable_cdf_update_non_reference_frame = false;
        rt_sf.prune_compoundmode_with_singlemode_var = false;
        rt_sf.skip_compound_based_on_var = false;
        rt_sf.set_zeromv_skip_based_on_source_sad = 1;
        rt_sf.use_adaptive_subpel_search = false;
        rt_sf.screen_content_cdef_filter_qindex_thresh = 0;
        rt_sf.enable_ref_short_signaling = false;
        rt_sf.check_globalmv_on_single_ref = true;
        rt_sf.increase_color_thresh_palette = false;
        rt_sf.selective_cdf_update = 0;
        rt_sf.force_only_last_ref = 0;
        rt_sf.higher_thresh_scene_detection = 1;
        rt_sf.skip_newmv_flat_blocks_screen = 0;
        rt_sf.skip_encoding_non_reference_slide_change = 0;
        rt_sf.rc_faster_convergence_static = 0;
        rt_sf.skip_newmv_mode_sad_screen = 0;
    }

    /// <summary>
    /// av1_set_speed_features_framesize_dependent for oxcf->mode == ALLINTRA. (set_subpel_search_method only picks
    /// the fractional-mv search function from mv_sf.subpel_search_method; nothing to port.)
    /// </summary>
    public void SetFramesizeDependent(AomSpeedFeatureInputs cpi, AomSpeedFeatureSeqFlags seq, int speed)
    {
        var sf = this;
        if (cpi.Mode == GOOD) SetGoodFramesizeDependent(cpi, sf, speed);
        else if (cpi.Mode == REALTIME) SetRtFramesizeDependent(cpi, sf, speed);
        else SetAllintraFramesizeDependent(cpi, sf, speed);

        if (!seq.SeqParamsLocked)
        {
            seq.enable_masked_compound &= sf.inter_sf.disable_masked_comp == 0 ? 1 : 0;
            seq.enable_interintra_compound &= sf.inter_sf.disable_interintra_wedge_var_thresh != UINT_MAX ? 1 : 0;
        }

        // For multi-thread use case with row_mt enabled, cost update for a set of SB rows is not desirable.
        if (cpi.RowMt == 1 && cpi.NumWorkers > 1)
        {
            if (sf.inter_sf.mv_cost_upd_level == INTERNAL_COST_UPD_SBROW_SET)
                sf.inter_sf.mv_cost_upd_level = INTERNAL_COST_UPD_SBROW;
        }
    }

    /// <summary>av1_set_speed_features_framesize_independent for oxcf->mode == ALLINTRA.</summary>
    public void SetFramesizeIndependent(AomSpeedFeatureInputs cpi, AomSpeedFeatureSeqFlags seq,
        AomWinnerModeParams winner_mode_params, int speed)
    {
        var sf = this;
        var oxcf = cpi;

        InitHlSf(sf.hl_sf);
        InitFpSf(sf.fp_sf);
        InitTplSf(sf.tpl_sf);
        InitGmSf(sf.gm_sf);
        InitPartSf(sf.part_sf);
        InitMvSf(sf.mv_sf);
        InitInterSf(sf.inter_sf);
        InitInterpSf(sf.interp_sf);
        InitIntraSf(sf.intra_sf);
        InitTxSf(sf.tx_sf);
        InitRdSf(sf.rd_sf, oxcf);
        InitWinnerModeSf(sf.winner_mode_sf);
        InitLpfSf(sf.lpf_sf);
        InitRtSf(sf.rt_sf);

        if (cpi.Mode == GOOD) SetGoodFramesizeIndependent(cpi, sf, speed);
        else if (cpi.Mode == REALTIME) SetRtFramesizeIndependent(cpi, sf, speed);
        else SetAllintraFramesizeIndependent(cpi, sf, speed);

        if (!oxcf.EnableTxSizeSearch && sf.rt_sf.use_nonrd_pick_mode == 0)
            sf.winner_mode_sf.tx_size_search_level = 3;

        if (cpi.NumWorkers > 1)
        {
            if (speed >= 5)
            {
                sf.lpf_sf.disable_sgr_filter = true;
                sf.lpf_sf.disable_wiener_filter = true;
            }
        }

        if (!seq.SeqParamsLocked)
        {
            seq.enable_dist_wtd_comp &= sf.inter_sf.use_dist_wtd_comp_flag != DIST_WTD_COMP_DISABLED ? 1 : 0;
            seq.enable_dual_filter &= sf.interp_sf.disable_dual_filter == 0 ? 1 : 0;
            seq.enable_restoration &= !sf.lpf_sf.disable_wiener_filter || !sf.lpf_sf.disable_sgr_filter ? 1 : 0;
            seq.enable_interintra_compound &= sf.inter_sf.disable_interintra_wedge_var_thresh != UINT_MAX ? 1 : 0;
        }

        int mesh_speed = Math.Min(speed, MAX_MESH_SPEED);
        for (int i = 0; i < MAX_MESH_STEP; ++i)
        {
            sf.mv_sf.mesh_patterns[i].range = good_quality_mesh_patterns[mesh_speed, i, 0];
            sf.mv_sf.mesh_patterns[i].interval = good_quality_mesh_patterns[mesh_speed, i, 1];
        }
        for (int i = 0; i < MAX_MESH_STEP; ++i)
        {
            sf.mv_sf.intrabc_mesh_patterns[i].range = intrabc_mesh_patterns[mesh_speed, i, 0];
            sf.mv_sf.intrabc_mesh_patterns[i].interval = intrabc_mesh_patterns[mesh_speed, i, 1];
        }

        // Slow quant, dct and trellis not worthwhile for first pass so make sure they are always turned off.
        if (cpi.IsStatGenerationStage) sf.rd_sf.optimize_coefficients = NO_TRELLIS_OPT;

        // No recode for 1 pass.
        if (oxcf.Pass == AOM_RC_ONE_PASS && cpi.HasNoStatsStage) sf.hl_sf.recode_loop = DISALLOW_RECODE;

        for (int i = 0; i < MODE_EVAL_TYPES; i++)
        {
            winner_mode_params.tx_domain_dist_threshold[i] = tx_domain_dist_thresholds[sf.rd_sf.tx_domain_dist_thres_level, i];
            winner_mode_params.use_transform_domain_distortion[i] = tx_domain_dist_types[sf.rd_sf.tx_domain_dist_level, i];
        }
        CopyCoeffOptThresholds(winner_mode_params, sf.rd_sf.perform_coeff_opt);
        for (int i = 0; i < MODE_EVAL_TYPES; i++)
        {
            winner_mode_params.skip_txfm_level[i] = predict_skip_levels[sf.tx_sf.tx_type_search.use_skip_flag_prediction, i];
            winner_mode_params.tx_size_search_methods[i] = tx_size_search_methods[sf.winner_mode_sf.tx_size_search_level, i];
            winner_mode_params.predict_dc_level[i] = predict_dc_levels[sf.winner_mode_sf.dc_blk_pred_level, i];
        }

        if (oxcf.RowMt == 1 && cpi.NumWorkers > 1)
        {
            if (sf.inter_sf.inter_mode_rd_model_estimation == 1) sf.inter_sf.inter_mode_rd_model_estimation = 2;

            if (sf.gm_sf.gm_search_type != GM_DISABLE_SEARCH &&
                cpi.NumWorkers >= gm_available_reference_frames[sf.gm_sf.gm_search_type])
                sf.gm_sf.prune_ref_frame_for_gm_search = 0;
        }

        if (oxcf.GfCbrBoostPct > 0) sf.rt_sf.gf_refresh_based_on_qp = 0;
    }

    /// <summary>av1_set_speed_features_qindex_dependent for oxcf->mode == ALLINTRA.</summary>
    public void SetQindexDependent(AomSpeedFeatureInputs cpi, AomWinnerModeParams winner_mode_params, int speed)
    {
        var sf = this;
        int base_qindex = cpi.BaseQindex;
        bool allow_screen_content_tools = cpi.AllowScreenContentTools;
        bool frame_is_intra_only = cpi.FrameIsIntraOnly;
        bool boosted = cpi.FrameIsBoosted;
        int minDim = Math.Min(cpi.Width, cpi.Height);
        bool is_480p_or_lesser = minDim <= 480;
        bool is_480p_or_larger = minDim >= 480;
        bool is_720p_or_larger = minDim >= 720;
        bool is_1080p_or_larger = minDim >= 1080;
        bool is_1440p_or_larger = minDim >= 1440;
        bool is_arf2_bwd_type = cpi.UpdateType == INTNL_ARF_UPDATE;

        if (cpi.Mode == ALLINTRA || cpi.Tuning == AOM_TUNE_IQ || cpi.Tuning == AOM_TUNE_SSIMULACRA2)
        {
            if (base_qindex <= 140) sf.lpf_sf.zero_low_cdef_strengths = true;
        }

        if (cpi.Mode == REALTIME)
        {
            if (speed >= 6)
            {
                int qindex_thresh_rt = boosted ? 190 : (is_720p_or_larger ? 120 : 150);
                sf.part_sf.adjust_var_based_rd_partitioning = frame_is_intra_only ? 0 : base_qindex > qindex_thresh_rt ? 1 : 0;
            }
            return;
        }

        if (speed == 0)
        {
            // qindex_thresh for resolution < 720p
            int qindex_thresh = boosted ? 70 : (is_arf2_bwd_type ? 110 : 140);
            if (!is_720p_or_larger && base_qindex <= qindex_thresh)
            {
                sf.part_sf.simple_motion_search_split = allow_screen_content_tools ? 1 : 2;
                sf.part_sf.simple_motion_search_early_term_none = 1;
                sf.tx_sf.model_based_prune_tx_search_level = 0;
            }

            if (is_720p_or_larger && base_qindex <= 128)
            {
                sf.rd_sf.perform_coeff_opt = 2 + (is_1080p_or_larger ? 1 : 0);
                CopyCoeffOptThresholds(winner_mode_params, sf.rd_sf.perform_coeff_opt);
                sf.part_sf.simple_motion_search_split = allow_screen_content_tools ? 1 : 2;
                sf.tx_sf.inter_tx_size_search_init_depth_rect = 1;
                sf.tx_sf.inter_tx_size_search_init_depth_sqr = 1;
                sf.tx_sf.intra_tx_size_search_init_depth_rect = 1;
                sf.tx_sf.model_based_prune_tx_search_level = 0;

                if (is_1080p_or_larger && base_qindex <= 108)
                {
                    sf.inter_sf.selective_ref_frame = 2;
                    sf.rd_sf.tx_domain_dist_level = boosted ? 1 : 2;
                    sf.rd_sf.tx_domain_dist_thres_level = 1;
                    sf.part_sf.simple_motion_search_early_term_none = 1;
                    sf.tx_sf.tx_type_search.ml_tx_split_thresh = 4000;
                    sf.interp_sf.cb_pred_filter_search = 0;
                    sf.tx_sf.tx_type_search.prune_2d_txfm_mode = TX_TYPE_PRUNE_2;
                    sf.tx_sf.tx_type_search.skip_tx_search = 1;
                }
            }
        }

        if (speed >= 2)
        {
            // Disable extended partitions for lower quantizers
            int aggr = Math.Min(4, speed - 2);
            ReadOnlySpan<int> qindex_thresh1 = [50, 50, 80, 100];
            ReadOnlySpan<int> qindex_thresh2 = [80, 100, 120, 160];
            int qindex_thresh;
            if (aggr <= 1)
            {
                int qthresh2 = (aggr == 0 && !is_480p_or_larger) ? 70 : qindex_thresh2[aggr];
                qindex_thresh = allow_screen_content_tools ? qindex_thresh1[aggr] : qthresh2;
                if (base_qindex <= qindex_thresh && !boosted)
                    sf.part_sf.ext_partition_eval_thresh = BLOCK_128X128;
            }
            else if (aggr <= 2)
            {
                qindex_thresh = boosted ? qindex_thresh1[aggr] : qindex_thresh2[aggr];
                if (base_qindex <= qindex_thresh && !frame_is_intra_only)
                    sf.part_sf.ext_partition_eval_thresh = BLOCK_128X128;
            }
            else if (aggr <= 3)
            {
                if (!is_480p_or_larger)
                {
                    sf.part_sf.ext_partition_eval_thresh = BLOCK_128X128;
                }
                else if (!is_720p_or_larger && !frame_is_intra_only && !allow_screen_content_tools)
                {
                    sf.part_sf.ext_partition_eval_thresh = BLOCK_128X128;
                }
                else
                {
                    qindex_thresh = boosted ? qindex_thresh1[aggr] : qindex_thresh2[aggr];
                    if (base_qindex <= qindex_thresh && !frame_is_intra_only)
                        sf.part_sf.ext_partition_eval_thresh = BLOCK_128X128;
                }
            }
            else
            {
                sf.part_sf.ext_partition_eval_thresh = BLOCK_128X128;
            }
        }

        if (speed >= 3)
        {
            // Disable rectangular partitions for lower quantizers
            int aggr = speed <= 4 ? 0 : 1;
            ReadOnlySpan<int> qindex_thresh = [65, 80];
            bool disable_rect_part = !boosted;
            if (base_qindex <= qindex_thresh[aggr] && disable_rect_part && is_480p_or_larger)
                sf.part_sf.rect_partition_eval_thresh = BLOCK_8X8;
        }

        if (speed <= 2)
        {
            if (!cpi.IsStatGenerationStage)
            {
                // Use faster full-pel motion search for high quantizers. Also use reduced total search range for
                // low resolutions at high quantizers.
                int aggr = speed;

                if (!is_720p_or_larger)
                {
                    // For < 720p resolutions: ms_qindex_thresh[3][2] = { { 200, 70 }, { 170, 50 }, { 170, 40 } }
                    ReadOnlySpan<int> ms_qindex_thresh = [200, 70, 170, 50, 170, 40];
                    int qindex_thresh1 = ms_qindex_thresh[aggr * 2];
                    int qindex_thresh2 = ms_qindex_thresh[aggr * 2 + 1];
                    if (base_qindex > qindex_thresh1)
                    {
                        sf.mv_sf.search_method = CLAMPED_DIAMOND;
                        sf.tpl_sf.search_method = CLAMPED_DIAMOND;
                    }
                    else if (base_qindex > qindex_thresh2)
                    {
                        sf.mv_sf.search_method = NSTEP_8PT;
                    }
                }
                else
                {
                    // For >= 720p resolutions: ms_qindex_thresh[3][2] = { { MAXQ, 200 }, { MAXQ, -1 }, { 200, -1 } },
                    // motion_search_method[3][2] = { { NSTEP_8PT, NSTEP_8PT }, { NSTEP_8PT, DIAMOND }, { NSTEP_8PT, DIAMOND } }
                    ReadOnlySpan<int> ms_qindex_thresh = [MAXQ, 200, MAXQ, -1, 200, -1];
                    ReadOnlySpan<int> motion_search_method = [NSTEP_8PT, NSTEP_8PT, NSTEP_8PT, DIAMOND, NSTEP_8PT, DIAMOND];
                    int qindex_thresh1 = ms_qindex_thresh[aggr * 2];
                    int qindex_thresh2 = ms_qindex_thresh[aggr * 2 + 1];
                    if (base_qindex > qindex_thresh1)
                    {
                        sf.mv_sf.search_method = DIAMOND;
                        sf.tpl_sf.search_method = DIAMOND;
                    }
                    else if (base_qindex > qindex_thresh2)
                    {
                        sf.mv_sf.search_method = motion_search_method[aggr * 2];
                        sf.tpl_sf.search_method = motion_search_method[aggr * 2 + 1];
                    }
                }
            }
            sf.part_sf.less_rectangular_check_level = 1;
        }

        if (speed == 3) sf.part_sf.less_rectangular_check_level = base_qindex >= 170 ? 1 : 2;

        if (speed >= 4)
        {
            // Disable LR search at low and high quantizers and enable only for mid-quantizer range.
            if (!boosted && !is_arf2_bwd_type)
            {
                ReadOnlySpan<int> qindex_low = [100, 60];
                ReadOnlySpan<int> qindex_high = [180, 160];
                int r = is_720p_or_larger ? 1 : 0;
                if (base_qindex <= qindex_low[r] || base_qindex > qindex_high[r])
                {
                    sf.lpf_sf.disable_sgr_filter = true;
                    sf.lpf_sf.disable_wiener_coeff_refine_search = true;
                }
            }
            sf.part_sf.less_rectangular_check_level = 2;
        }

        if (speed == 1)
        {
            // Reuse interinter wedge mask search from first search for non-boosted non-internal-arf frames, except
            // at very high quantizers.
            if (base_qindex <= 200)
            {
                if (!boosted && !is_arf2_bwd_type) sf.inter_sf.reuse_mask_search_results = 1;
            }
        }

        if (speed == 5)
        {
            if (!(frame_is_intra_only || allow_screen_content_tools))
            {
                ReadOnlySpan<int> qindex = [256, 128];
                // Set the sf value as 3 for low resolution and for higher resolutions with low quantizers.
                if (base_qindex < qindex[is_480p_or_larger ? 1 : 0])
                    sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning = 3;
            }
        }

        if (speed >= 5)
        {
            // Disable the sf for low quantizers in case of low resolution screen contents.
            if (allow_screen_content_tools && base_qindex < 128 && is_480p_or_lesser)
                sf.part_sf.prune_sub_8x8_partition_level = 0;
        }

        // Loop restoration size search
        // At speed 0, always search all available sizes for the maximum possible gain
        sf.lpf_sf.min_lr_unit_size = RESTORATION_PROC_UNIT_SIZE;
        sf.lpf_sf.max_lr_unit_size = RESTORATION_UNITSIZE_MAX;

        if (speed >= 1)
        {
            // For large frames, small restoration units are almost never useful, so prune them away
            if (is_1440p_or_larger)
                sf.lpf_sf.min_lr_unit_size = RESTORATION_UNITSIZE_MAX;
            else if (is_720p_or_larger)
                sf.lpf_sf.min_lr_unit_size = RESTORATION_UNITSIZE_MAX >> 1;
        }

        if (speed >= 3 || (cpi.Mode == ALLINTRA && speed >= 1))
        {
            // At this speed, a full search is too expensive. Instead, pick a single size based on size and qindex.
            int qindex_thresh = 96;
            if (base_qindex <= qindex_thresh && !is_1440p_or_larger)
            {
                sf.lpf_sf.min_lr_unit_size = RESTORATION_UNITSIZE_MAX >> 1;
                sf.lpf_sf.max_lr_unit_size = RESTORATION_UNITSIZE_MAX >> 1;
            }
            else
            {
                sf.lpf_sf.min_lr_unit_size = RESTORATION_UNITSIZE_MAX;
                sf.lpf_sf.max_lr_unit_size = RESTORATION_UNITSIZE_MAX;
            }
        }
        // (set_good_speed_features_lc_dec_qindex_dependent runs for mode == GOOD only)
    }

    /// <summary>
    /// The per-frame sequence of libaom's all-intra encode: framesize_independent (set_size_independent_vars),
    /// framesize_dependent (av1_set_size_dependent_vars), qindex_dependent (after av1_set_quantizer).
    /// </summary>
    public void SetForFrame(AomSpeedFeatureInputs cpi, AomSpeedFeatureSeqFlags seq, AomWinnerModeParams winner_mode_params,
        int speed)
    {
        SetFramesizeIndependent(cpi, seq, winner_mode_params, speed);
        SetFramesizeDependent(cpi, seq, speed);
        SetQindexDependent(cpi, winner_mode_params, speed);
    }
}
