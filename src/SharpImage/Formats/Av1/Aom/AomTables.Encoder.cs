namespace SharpImage.Formats.Av1;

// libaom 3.14.1 encoder enums/constants used by the speed features port (AomSpeedFeatures.cs), hand-copied from the
// named headers (the generated AomTables.cs holds the common ones). Values verified by the speed-feature twin test.
internal static partial class AomTables
{
    // av1/common/enums.h: PREDICTION_MODE (the entries AomTables.cs lacks)
    internal const int D45_PRED = 3, D135_PRED = 4, D113_PRED = 5, D157_PRED = 6, D203_PRED = 7, D67_PRED = 8,
        SMOOTH_V_PRED = 10, SMOOTH_H_PRED = 11;
    internal const int NEARESTMV = 13, NEARMV = 14, GLOBALMV = 15, NEWMV = 16, NEAREST_NEARESTMV = 17, NEAR_NEARMV = 18,
        NEAREST_NEWMV = 19, NEW_NEARESTMV = 20, NEAR_NEWMV = 21, NEW_NEARMV = 22, GLOBAL_GLOBALMV = 23, NEW_NEWMV = 24;
    internal const int PARTITION_BLOCK_SIZES = 5;   // enums.h
    internal const int TOP_INTRA_MODEL_COUNT = 4;   // enums.h
    internal const int LAST_FRAME = 1, ALTREF_FRAME = 7, INTER_REFS_PER_FRAME = ALTREF_FRAME - LAST_FRAME + 1;

    // av1/common/blockd.h: FRAME_TYPE
    internal const int KEY_FRAME = 0, INTER_FRAME = 1, INTRA_ONLY_FRAME = 2, S_FRAME = 3;

    // av1/common/quant_common.h, restoration.h, av1/encoder/global_motion.h
    internal const int MAXQ = 255;
    internal const int RESTORATION_PROC_UNIT_SIZE = 64, RESTORATION_UNITSIZE_MAX = 256;
    internal const int GM_MAX_REFINEMENT_STEPS = 5;

    // av1/common/filter.h: SUBPEL_SEARCH_TYPE
    internal const int USE_2_TAPS_ORIG = 0, USE_2_TAPS = 1, USE_4_TAPS = 2, USE_8_TAPS = 3;

    // av1/encoder/mcomp.h: SUBPEL_FORCE_STOP; mcomp_structs.h: SEARCH_METHODS, WARP_SEARCH_METHOD
    internal const int EIGHTH_PEL = 0, QUARTER_PEL = 1, HALF_PEL = 2, FULL_PEL = 3;
    internal const int DIAMOND = 0, NSTEP = 1, NSTEP_8PT = 2, CLAMPED_DIAMOND = 3, HEX = 4, BIGDIA = 5, FAST_DIAMOND = 6,
        FAST_BIGDIA = 7, VFAST_DIAMOND = 8, NUM_SEARCH_METHODS = 9, NUM_DISTINCT_SEARCH_METHODS = BIGDIA + 1;
    internal const int WARP_SEARCH_DIAMOND = 0, WARP_SEARCH_SQUARE = 1;

    // av1/encoder/encodemb.h: TRELLIS_OPT_TYPE
    internal const int NO_TRELLIS_OPT = 0, FULL_TRELLIS_OPT = 1, FINAL_PASS_TRELLIS_OPT = 2, NO_ESTIMATE_YRD_TRELLIS_OPT = 3;

    // av1/encoder/enc_enums.h: TX_SIZE_SEARCH_METHOD, MODE
    internal const int USE_FULL_RD = 0, USE_FAST_RD = 1, USE_LARGESTALL = 2;
    internal const int GOOD = 0, REALTIME = 1, ALLINTRA = 2;

    // av1/encoder/rd.h: MODE_EVAL_TYPE
    internal const int DEFAULT_EVAL = 0, MODE_EVAL = 1, WINNER_MODE_EVAL = 2, MODE_EVAL_TYPES = 3;

    // av1/encoder/ratectrl.h: FRAME_UPDATE_TYPE; firstpass.h: FRAME_CONTENT_TYPE; lookahead.h: COMPRESSOR_STAGE
    internal const int KF_UPDATE = 0, LF_UPDATE = 1, GF_UPDATE = 2, ARF_UPDATE = 3, OVERLAY_UPDATE = 4,
        INTNL_OVERLAY_UPDATE = 5, INTNL_ARF_UPDATE = 6;
    internal const int FC_NORMAL = 0, FC_GRAPHICS_ANIMATION = 1;
    internal const int ENCODE_STAGE = 0, LAP_STAGE = 1;

    // aom/aom_encoder.h: aom_enc_pass; aom/aomcx.h: aom_tune_metric
    internal const int AOM_RC_ONE_PASS = 0, AOM_RC_FIRST_PASS = 1, AOM_RC_SECOND_PASS = 2, AOM_RC_THIRD_PASS = 3;
    internal const int AOM_TUNE_PSNR = 0, AOM_TUNE_SSIM = 1, AOM_TUNE_IQ = 10, AOM_TUNE_SSIMULACRA2 = 11;

    // av1/encoder/speed_features.h
    internal const int GM_FULL_SEARCH = 0, GM_REDUCED_REF_SEARCH_SKIP_L2_L3 = 1, GM_REDUCED_REF_SEARCH_SKIP_L2_L3_ARF2 = 2,
        GM_SEARCH_CLOSEST_REFS_ONLY = 3, GM_DISABLE_SEARCH = 4;
    internal const int DIST_WTD_COMP_ENABLED = 0, DIST_WTD_COMP_SKIP_MV_SEARCH = 1, DIST_WTD_COMP_DISABLED = 2;
    internal const int INTRA_ALL = (1 << DC_PRED) | (1 << V_PRED) | (1 << H_PRED) | (1 << D45_PRED) | (1 << D135_PRED) |
        (1 << D113_PRED) | (1 << D157_PRED) | (1 << D203_PRED) | (1 << D67_PRED) | (1 << SMOOTH_PRED) |
        (1 << SMOOTH_V_PRED) | (1 << SMOOTH_H_PRED) | (1 << PAETH_PRED);
    internal const int UV_INTRA_ALL = (1 << UV_DC_PRED) | (1 << UV_V_PRED) | (1 << UV_H_PRED) | (1 << UV_D45_PRED) |
        (1 << UV_D135_PRED) | (1 << UV_D113_PRED) | (1 << UV_D157_PRED) | (1 << UV_D203_PRED) | (1 << UV_D67_PRED) |
        (1 << UV_SMOOTH_PRED) | (1 << UV_SMOOTH_V_PRED) | (1 << UV_SMOOTH_H_PRED) | (1 << UV_PAETH_PRED) |
        (1 << UV_CFL_PRED);
    internal const int UV_INTRA_DC = 1 << UV_DC_PRED;
    internal const int UV_INTRA_DC_CFL = (1 << UV_DC_PRED) | (1 << UV_CFL_PRED);
    internal const int UV_INTRA_DC_TM = (1 << UV_DC_PRED) | (1 << UV_PAETH_PRED);
    internal const int UV_INTRA_DC_PAETH_CFL = (1 << UV_DC_PRED) | (1 << UV_PAETH_PRED) | (1 << UV_CFL_PRED);
    internal const int UV_INTRA_DC_H_V = (1 << UV_DC_PRED) | (1 << UV_V_PRED) | (1 << UV_H_PRED);
    internal const int UV_INTRA_DC_H_V_CFL = (1 << UV_DC_PRED) | (1 << UV_V_PRED) | (1 << UV_H_PRED) | (1 << UV_CFL_PRED);
    internal const int UV_INTRA_DC_PAETH_H_V = (1 << UV_DC_PRED) | (1 << UV_PAETH_PRED) | (1 << UV_V_PRED) | (1 << UV_H_PRED);
    internal const int UV_INTRA_DC_PAETH_H_V_CFL = (1 << UV_DC_PRED) | (1 << UV_PAETH_PRED) | (1 << UV_V_PRED) |
        (1 << UV_H_PRED) | (1 << UV_CFL_PRED);
    internal const int INTRA_DC = 1 << DC_PRED;
    internal const int INTRA_DC_TM = (1 << DC_PRED) | (1 << PAETH_PRED);
    internal const int INTRA_DC_H_V = (1 << DC_PRED) | (1 << V_PRED) | (1 << H_PRED);
    internal const int INTRA_DC_H_V_SMOOTH = (1 << DC_PRED) | (1 << V_PRED) | (1 << H_PRED) | (1 << SMOOTH_PRED);
    internal const int INTRA_DC_PAETH_H_V = (1 << DC_PRED) | (1 << PAETH_PRED) | (1 << V_PRED) | (1 << H_PRED);
    internal const int DISALLOW_RECODE = 0, ALLOW_RECODE_KFARFGF = 1, ALLOW_RECODE = 2;
    internal const int SUBPEL_TREE = 0, SUBPEL_TREE_PRUNED = 1, SUBPEL_TREE_PRUNED_MORE = 2, SUBPEL_SEARCH_METHODS = 3;
    internal const int LPF_PICK_FROM_FULL_IMAGE = 0, LPF_PICK_FROM_FULL_IMAGE_NON_DUAL = 1, LPF_PICK_FROM_SUBIMAGE = 2,
        LPF_PICK_FROM_Q = 3, LPF_PICK_MINIMAL_LPF = 4;
    internal const int CDEF_FULL_SEARCH = 0, CDEF_FAST_SEARCH_LVL1 = 1, CDEF_FAST_SEARCH_LVL2 = 2, CDEF_FAST_SEARCH_LVL3 = 3,
        CDEF_FAST_SEARCH_LVL4 = 4, CDEF_FAST_SEARCH_LVL5 = 5, CDEF_PICK_FROM_Q = 6, CDEF_PICK_METHODS = 7;
    internal const int FLAG_EARLY_TERMINATE = 1 << 0, FLAG_SKIP_COMP_BESTINTRA = 1 << 1, FLAG_SKIP_INTRA_BESTINTER = 1 << 3,
        FLAG_SKIP_INTRA_DIRMISMATCH = 1 << 4, FLAG_SKIP_INTRA_LOWVAR = 1 << 5;
    internal const int TX_TYPE_PRUNE_0 = 0, TX_TYPE_PRUNE_1 = 1, TX_TYPE_PRUNE_2 = 2, TX_TYPE_PRUNE_3 = 3,
        TX_TYPE_PRUNE_4 = 4, TX_TYPE_PRUNE_5 = 5;
    internal const int NO_DETECTION = 0, FAST_DETECTION_MAXQ = 1;
    internal const int MULTI_WINNER_MODE_OFF = 0, MULTI_WINNER_MODE_FAST = 1, MULTI_WINNER_MODE_DEFAULT = 2;
    internal const int PRUNE_NEARMV_OFF = 0, PRUNE_NEARMV_LEVEL1 = 1, PRUNE_NEARMV_LEVEL2 = 2, PRUNE_NEARMV_LEVEL3 = 3;
    internal const int TX_SEARCH_DEFAULT = 0, TX_SEARCH_MOTION_MODE = 1, TX_SEARCH_COMP_TYPE_MODE = 2, TX_SEARCH_CASES = 3;
    internal const int SEARCH_PARTITION = 0, FIXED_PARTITION = 1, VAR_BASED_PARTITION = 2;
    internal const int NOT_IN_USE = 0, DIRECT_PRED = 1, RELAXED_PRED = 2, ADAPT_PRED = 3;
    internal const int LAST_MV_DATA = 0, CURRENT_Q = 1, QTR_ONLY = 2;
    internal const int SUPERRES_AUTO_ALL = 0, SUPERRES_AUTO_DUAL = 1, SUPERRES_AUTO_SOLO = 2;
    internal const int INTERNAL_COST_UPD_OFF = 0, INTERNAL_COST_UPD_TILE = 1, INTERNAL_COST_UPD_SBROW_SET = 2,
        INTERNAL_COST_UPD_SBROW = 3, INTERNAL_COST_UPD_SB = 4;
    internal const int NO_PRUNING = -1, SIMPLE_AGG_LVL0 = 0, SIMPLE_AGG_LVL1 = 1, SIMPLE_AGG_LVL2 = 2, SIMPLE_AGG_LVL3 = 3,
        SIMPLE_AGG_LVL4 = 4, SIMPLE_AGG_LVL5 = 5, QIDX_BASED_AGG_LVL1 = 6;
    internal const int PRUNE_MESH_SEARCH_DISABLED = 0, PRUNE_MESH_SEARCH_LVL_1 = 1, PRUNE_MESH_SEARCH_LVL_2 = 2;
    internal const int EARLY_TERM_DISABLED = 0, EARLY_TERM_IDX_1 = 1, EARLY_TERM_IDX_2 = 2, EARLY_TERM_IDX_3 = 3,
        EARLY_TERM_IDX_4 = 4;
    internal const int MAX_MESH_STEP = 4;
}
