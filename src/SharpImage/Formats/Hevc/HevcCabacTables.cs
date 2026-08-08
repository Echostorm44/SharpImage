using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// CABAC lookup tables for HEVC/H.265.
    /// Based on ITU-T H.265 Section 9.3.
    /// </summary>
    /// <remarks>
    /// HEVC uses the same 64-state probability model as H.264.
    /// The transition tables and LPS range tables are identical.
    /// Only the context initialization values differ.
    /// </remarks>
    public static class HevcCabacTables
    {
        /// <summary>
        /// State transition table when LPS (Least Probable Symbol) is decoded.
        /// Identical to H.264 (Table 9-45 / HEVC Table 9-48).
        /// </summary>
        public static ReadOnlySpan<byte> TransitionIndexLps => new byte[]
        {
             0,  0,  1,  2,  2,  4,  4,  5,  6,  7,  8,  9,  9, 11, 11, 12,
            13, 13, 15, 15, 16, 16, 18, 18, 19, 19, 21, 21, 22, 22, 23, 24,
            24, 25, 26, 26, 27, 27, 28, 29, 29, 30, 30, 30, 31, 32, 32, 33,
            33, 33, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 63
        };

        /// <summary>
        /// State transition table when MPS (Most Probable Symbol) is decoded.
        /// Identical to H.264 (Table 9-45 / HEVC Table 9-48).
        /// </summary>
        public static ReadOnlySpan<byte> TransitionIndexMps => new byte[]
        {
             1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
            49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 62, 63
        };

        /// <summary>
        /// Range values for LPS based on probability state.
        /// Identical to H.264 (Table 9-40 / HEVC Table 9-46).
        /// </summary>
        public static ReadOnlySpan<byte> RangeLps => new byte[]
        {
            128, 176, 208, 240,  // pStateIdx = 0
            128, 167, 197, 227,  // pStateIdx = 1
            128, 158, 187, 216,  // pStateIdx = 2
            123, 150, 178, 205,  // pStateIdx = 3
            116, 142, 169, 195,  // pStateIdx = 4
            111, 135, 160, 185,  // pStateIdx = 5
            105, 128, 152, 175,  // pStateIdx = 6
            100, 122, 144, 166,  // pStateIdx = 7
             95, 116, 137, 158,  // pStateIdx = 8
             90, 110, 130, 150,  // pStateIdx = 9
             85, 104, 123, 142,  // pStateIdx = 10
             81,  99, 117, 135,  // pStateIdx = 11
             77,  94, 111, 128,  // pStateIdx = 12
             73,  89, 105, 122,  // pStateIdx = 13
             69,  85, 100, 116,  // pStateIdx = 14
             66,  80,  95, 110,  // pStateIdx = 15
             62,  76,  90, 104,  // pStateIdx = 16
             59,  72,  86,  99,  // pStateIdx = 17
             56,  69,  81,  94,  // pStateIdx = 18
             53,  65,  77,  89,  // pStateIdx = 19
             51,  62,  73,  85,  // pStateIdx = 20
             48,  59,  69,  80,  // pStateIdx = 21
             46,  56,  66,  76,  // pStateIdx = 22
             43,  53,  63,  72,  // pStateIdx = 23
             41,  50,  59,  69,  // pStateIdx = 24
             39,  48,  56,  65,  // pStateIdx = 25
             37,  45,  54,  62,  // pStateIdx = 26
             35,  43,  51,  59,  // pStateIdx = 27
             33,  41,  48,  56,  // pStateIdx = 28
             32,  39,  46,  53,  // pStateIdx = 29
             30,  37,  43,  50,  // pStateIdx = 30
             29,  35,  41,  48,  // pStateIdx = 31
             27,  33,  39,  45,  // pStateIdx = 32
             26,  31,  37,  43,  // pStateIdx = 33
             24,  30,  35,  41,  // pStateIdx = 34
             23,  28,  33,  39,  // pStateIdx = 35
             22,  27,  32,  37,  // pStateIdx = 36
             21,  26,  30,  35,  // pStateIdx = 37
             20,  24,  29,  33,  // pStateIdx = 38
             19,  23,  27,  31,  // pStateIdx = 39
             18,  22,  26,  30,  // pStateIdx = 40
             17,  21,  25,  28,  // pStateIdx = 41
             16,  20,  23,  27,  // pStateIdx = 42
             15,  19,  22,  25,  // pStateIdx = 43
             14,  18,  21,  24,  // pStateIdx = 44
             14,  17,  20,  23,  // pStateIdx = 45
             13,  16,  19,  22,  // pStateIdx = 46
             12,  15,  18,  21,  // pStateIdx = 47
             12,  14,  17,  20,  // pStateIdx = 48
             11,  14,  16,  19,  // pStateIdx = 49
             11,  13,  15,  18,  // pStateIdx = 50
             10,  12,  15,  17,  // pStateIdx = 51
             10,  12,  14,  16,  // pStateIdx = 52
              9,  11,  13,  15,  // pStateIdx = 53
              9,  11,  12,  14,  // pStateIdx = 54
              8,  10,  12,  14,  // pStateIdx = 55
              8,   9,  11,  13,  // pStateIdx = 56
              7,   9,  11,  12,  // pStateIdx = 57
              7,   9,  10,  12,  // pStateIdx = 58
              7,   8,  10,  11,  // pStateIdx = 59
              6,   8,   9,  11,  // pStateIdx = 60
              6,   7,   9,  10,  // pStateIdx = 61
              6,   7,   8,   9,  // pStateIdx = 62
              2,   2,   2,   2   // pStateIdx = 63
        };

        /// <summary>
        /// Gets the LPS range for a given state and range index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetLpsRange(int stateIndex, int rangeIndex)
        {
            return RangeLps[(stateIndex << 2) + rangeIndex];
        }

        /// <summary>
        /// Computes the initial CABAC state for a context using the FFmpeg/spec formula.
        /// The init_value byte is decomposed: m = (val >> 4) * 5 - 45, n = ((val &amp; 15) &lt;&lt; 3) - 16.
        /// State is stored as (pStateIdx &lt;&lt; 1) | valMps.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ComputeInitState(int initValue, int sliceQp)
        {
            int m = (initValue >> 4) * 5 - 45;
            int n = ((initValue & 15) << 3) - 16;
            int qp = Math.Clamp(sliceQp, 0, 51);
            int pre = 2 * (((m * qp) >> 4) + n) - 127;
            pre ^= pre >> 31; // absolute value
            if (pre > 124)
                pre = 124 + (pre & 1);
            return (byte)pre;
        }

        /// <summary>
        /// Returns the init_values table for a given init type (0=I, 1=P, 2=B).
        /// Matches FFmpeg's init_values[3][HEVC_CONTEXTS] exactly.
        /// </summary>
        public static ReadOnlySpan<byte> GetInitValues(int initType) => initType switch
        {
            0 => InitValuesI,
            1 => InitValuesP,
            2 => InitValuesB,
            _ => InitValuesI
        };

        // CNU = 154 (Context Not Used — results in equiprobable state)
        private const byte CNU = 154;

        // init_type 0: I-slice (from FFmpeg cabac.c)
        private static ReadOnlySpan<byte> InitValuesI => new byte[]
        {
            // sao_merge_flag (1)
            153,
            // sao_type_idx (1)
            200,
            // split_coding_unit_flag (3)
            139, 141, 157,
            // cu_transquant_bypass_flag (1)
            154,
            // skip_flag (3)
            CNU, CNU, CNU,
            // cu_qp_delta (3)
            154, 154, 154,
            // pred_mode_flag (1)
            CNU,
            // part_mode (4)
            184, CNU, CNU, CNU,
            // prev_intra_luma_pred_flag (1)
            184,
            // intra_chroma_pred_mode (2)
            63, 139,
            // merge_flag (1)
            CNU,
            // merge_idx (1)
            CNU,
            // inter_pred_idc (5)
            CNU, CNU, CNU, CNU, CNU,
            // ref_idx_l0 (2)
            CNU, CNU,
            // ref_idx_l1 (2)
            CNU, CNU,
            // abs_mvd_greater0_flag (2)
            CNU, CNU,
            // abs_mvd_greater1_flag (2)
            CNU, CNU,
            // mvp_lx_flag (1)
            CNU,
            // no_residual_data_flag / rqt_root_cbf (1)
            CNU,
            // split_transform_flag (3)
            153, 138, 138,
            // cbf_luma (2)
            111, 141,
            // cbf_cb_cr (5)
            94, 138, 182, 154, 154,
            // transform_skip_flag (2)
            139, 139,
            // explicit_rdpcm_flag (2)
            139, 139,
            // explicit_rdpcm_dir_flag (2)
            139, 139,
            // last_significant_coeff_x_prefix (18)
            110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111,
             79, 108, 123,  63,
            // last_significant_coeff_y_prefix (18)
            110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111,
             79, 108, 123,  63,
            // significant_coeff_group_flag (4)
            91, 171, 134, 141,
            // significant_coeff_flag (44)
            111, 111, 125, 110, 110,  94, 124, 108, 124, 107, 125, 141, 179, 153,
            125, 107, 125, 141, 179, 153, 125, 107, 125, 141, 179, 153, 125, 140,
            139, 182, 182, 152, 136, 152, 136, 153, 136, 139, 111, 136, 139, 111,
            141, 111,
            // coeff_abs_level_greater1_flag (24)
            140,  92, 137, 138, 140, 152, 138, 139, 153,  74, 149,  92, 139, 107,
            122, 152, 140, 179, 166, 182, 140, 227, 122, 197,
            // coeff_abs_level_greater2_flag (6)
            138, 153, 136, 167, 152, 152,
            // log2_res_scale_abs (8)
            154, 154, 154, 154, 154, 154, 154, 154,
            // res_scale_sign_flag (2)
            154, 154,
            // cu_chroma_qp_offset_flag (1)
            154,
            // cu_chroma_qp_offset_idx (1)
            154,
        };

        // init_type 1: P-slice (from FFmpeg cabac.c)
        private static ReadOnlySpan<byte> InitValuesP => new byte[]
        {
            // sao_merge_flag (1)
            153,
            // sao_type_idx (1)
            185,
            // split_coding_unit_flag (3)
            107, 139, 126,
            // cu_transquant_bypass_flag (1)
            154,
            // skip_flag (3)
            197, 185, 201,
            // cu_qp_delta (3)
            154, 154, 154,
            // pred_mode_flag (1)
            149,
            // part_mode (4)
            154, 139, 154, 154,
            // prev_intra_luma_pred_flag (1)
            154,
            // intra_chroma_pred_mode (2)
            152, 139,
            // merge_flag (1)
            110,
            // merge_idx (1)
            122,
            // inter_pred_idc (5)
            95, 79, 63, 31, 31,
            // ref_idx_l0 (2)
            153, 153,
            // ref_idx_l1 (2)
            153, 153,
            // abs_mvd_greater0_flag (2)
            140, 198,
            // abs_mvd_greater1_flag (2)
            140, 198,
            // mvp_lx_flag (1)
            168,
            // no_residual_data_flag / rqt_root_cbf (1)
            79,
            // split_transform_flag (3)
            124, 138, 94,
            // cbf_luma (2)
            153, 111,
            // cbf_cb_cr (5)
            149, 107, 167, 154, 154,
            // transform_skip_flag (2)
            139, 139,
            // explicit_rdpcm_flag (2)
            139, 139,
            // explicit_rdpcm_dir_flag (2)
            139, 139,
            // last_significant_coeff_x_prefix (18)
            125, 110,  94, 110,  95,  79, 125, 111, 110,  78, 110, 111, 111,  95,
             94, 108, 123, 108,
            // last_significant_coeff_y_prefix (18)
            125, 110,  94, 110,  95,  79, 125, 111, 110,  78, 110, 111, 111,  95,
             94, 108, 123, 108,
            // significant_coeff_group_flag (4)
            121, 140, 61, 154,
            // significant_coeff_flag (44)
            155, 154, 139, 153, 139, 123, 123,  63, 153, 166, 183, 140, 136, 153,
            154, 166, 183, 140, 136, 153, 154, 166, 183, 140, 136, 153, 154, 170,
            153, 123, 123, 107, 121, 107, 121, 167, 151, 183, 140, 151, 183, 140,
            140, 140,
            // coeff_abs_level_greater1_flag (24)
            154, 196, 196, 167, 154, 152, 167, 182, 182, 134, 149, 136, 153, 121,
            136, 137, 169, 194, 166, 167, 154, 167, 137, 182,
            // coeff_abs_level_greater2_flag (6)
            107, 167, 91, 122, 107, 167,
            // log2_res_scale_abs (8)
            154, 154, 154, 154, 154, 154, 154, 154,
            // res_scale_sign_flag (2)
            154, 154,
            // cu_chroma_qp_offset_flag (1)
            154,
            // cu_chroma_qp_offset_idx (1)
            154,
        };

        // init_type 2: B-slice (from FFmpeg cabac.c)
        private static ReadOnlySpan<byte> InitValuesB => new byte[]
        {
            // sao_merge_flag (1)
            153,
            // sao_type_idx (1)
            160,
            // split_coding_unit_flag (3)
            107, 139, 126,
            // cu_transquant_bypass_flag (1)
            154,
            // skip_flag (3)
            197, 185, 201,
            // cu_qp_delta (3)
            154, 154, 154,
            // pred_mode_flag (1)
            134,
            // part_mode (4)
            154, 139, 154, 154,
            // prev_intra_luma_pred_flag (1)
            183,
            // intra_chroma_pred_mode (2)
            152, 139,
            // merge_flag (1)
            154,
            // merge_idx (1)
            137,
            // inter_pred_idc (5)
            95, 79, 63, 31, 31,
            // ref_idx_l0 (2)
            153, 153,
            // ref_idx_l1 (2)
            153, 153,
            // abs_mvd_greater0_flag (2)
            169, 198,
            // abs_mvd_greater1_flag (2)
            169, 198,
            // mvp_lx_flag (1)
            168,
            // no_residual_data_flag / rqt_root_cbf (1)
            79,
            // split_transform_flag (3)
            224, 167, 122,
            // cbf_luma (2)
            153, 111,
            // cbf_cb_cr (5)
            149, 92, 167, 154, 154,
            // transform_skip_flag (2)
            139, 139,
            // explicit_rdpcm_flag (2)
            139, 139,
            // explicit_rdpcm_dir_flag (2)
            139, 139,
            // last_significant_coeff_x_prefix (18)
            125, 110, 124, 110,  95,  94, 125, 111, 111,  79, 125, 126, 111, 111,
             79, 108, 123,  93,
            // last_significant_coeff_y_prefix (18)
            125, 110, 124, 110,  95,  94, 125, 111, 111,  79, 125, 126, 111, 111,
             79, 108, 123,  93,
            // significant_coeff_group_flag (4)
            121, 140, 61, 154,
            // significant_coeff_flag (44)
            170, 154, 139, 153, 139, 123, 123,  63, 124, 166, 183, 140, 136, 153,
            154, 166, 183, 140, 136, 153, 154, 166, 183, 140, 136, 153, 154, 170,
            153, 138, 138, 122, 121, 122, 121, 167, 151, 183, 140, 151, 183, 140,
            140, 140,
            // coeff_abs_level_greater1_flag (24)
            154, 196, 167, 167, 154, 152, 167, 182, 182, 134, 149, 136, 153, 121,
            136, 122, 169, 208, 166, 167, 154, 152, 167, 182,
            // coeff_abs_level_greater2_flag (6)
            107, 167, 91, 107, 107, 167,
            // log2_res_scale_abs (8)
            154, 154, 154, 154, 154, 154, 154, 154,
            // res_scale_sign_flag (2)
            154, 154,
            // cu_chroma_qp_offset_flag (1)
            154,
            // cu_chroma_qp_offset_idx (1)
            154,
        };
    }

    /// <summary>
    /// Context index offsets for HEVC CABAC decoding.
    /// Matches FFmpeg's CABAC_ELEMS ordering exactly.
    /// Based on HEVC Spec Tables 9-4 through 9-31.
    /// </summary>
    public static class HevcCabacContextIndex
    {
        /// <summary>
        /// Total number of CABAC contexts used in HEVC.
        /// </summary>
        public const int TotalContexts = 179;

        // SAO contexts
        public const int SaoMergeFlag = 0;              // 1 context
        public const int SaoTypeIdx = 1;                // 1 context

        // Coding unit contexts
        public const int SplitCuFlag = 2;               // 3 contexts (2,3,4)
        public const int CuTransquantBypassFlag = 5;    // 1 context
        public const int CuSkipFlag = 6;                // 3 contexts (6,7,8)
        public const int CuQpDelta = 9;                 // 3 contexts (9,10,11)
        public const int PredModeFlag = 12;             // 1 context
        public const int PartMode = 13;                 // 4 contexts (13,14,15,16)
        public const int PrevIntraLumaPredFlag = 17;    // 1 context
        public const int IntraChromaPredMode = 18;      // 2 contexts (18,19)

        // Inter prediction contexts
        public const int MergeFlag = 20;                // 1 context
        public const int MergeIdx = 21;                 // 1 context
        public const int InterPredIdc = 22;             // 5 contexts (22-26)
        public const int RefIdxL0 = 27;                 // 2 contexts (27,28)
        public const int RefIdxL1 = 29;                 // 2 contexts (29,30)
        public const int AbsMvdGreater0Flag = 31;       // 2 contexts (31,32)
        public const int AbsMvdGreater1Flag = 33;       // 2 contexts (33,34)
        public const int MvpLxFlag = 35;                // 1 context
        public const int RqtRootCbf = 36;               // 1 context (no_residual_data_flag)

        // Transform unit contexts
        public const int SplitTransformFlag = 37;       // 3 contexts (37,38,39)
        public const int CbfLuma = 40;                  // 2 contexts (40,41)
        public const int CbfChroma = 42;                // 5 contexts (42,43,44,45,46)
        public const int TransformSkipFlag = 47;        // 2 contexts (47,48)
        public const int ExplicitRdpcmFlag = 49;        // 2 contexts (49,50)
        public const int ExplicitRdpcmDirFlag = 51;     // 2 contexts (51,52)

        // Residual coding contexts
        public const int LastSignificantCoeffXPrefix = 53;  // 18 contexts (53-70)
        public const int LastSignificantCoeffYPrefix = 71;  // 18 contexts (71-88)
        public const int SignificantCoeffGroupFlag = 89;    // 4 contexts (89-92)
        public const int SignificantCoeffFlag = 93;         // 44 contexts (93-136)
        public const int CoeffAbsLevelGreater1Flag = 137;   // 24 contexts (137-160)
        public const int CoeffAbsLevelGreater2Flag = 161;   // 6 contexts (161-166)

        // Range extension contexts
        public const int Log2ResScaleAbsPlus1 = 167;    // 8 contexts (167-174)
        public const int ResScaleSignFlag = 175;         // 2 contexts (175,176)
        public const int CuChromaQpOffsetFlag = 177;    // 1 context
        public const int CuChromaQpOffsetIdx = 178;     // 1 context
    }
}
