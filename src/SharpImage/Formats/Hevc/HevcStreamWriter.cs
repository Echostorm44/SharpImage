// HEVC parameter-set + NAL writers: produce VPS/SPS/PPS RBSP and wrap NAL units with the 2-byte
// header + emulation-prevention, mirroring HevcParameterSetParser / HevcNalParser. Values are chosen
// for a Main-profile all-intra still image (SAO off, deblocking off, no scaling lists, no RExt),
// with MinCb == CTB == 32 so every CTU is one CU. Used by the HEIC muxer.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Hevc;

/// <summary>Exp-Golomb RBSP bit writer (MSB-first) with rbsp_trailing + emulation-prevention output.</summary>
internal sealed class HevcRbspWriter
{
    private readonly HevcBitWriter bits = new();

    public void U(int value, int n) => bits.PutBits((uint)value, n);

    public void Flag(bool b) => bits.PutBit(b ? 1 : 0);

    public void Ue(uint value)
    {
        // Exp-Golomb: write leadingZeros, a 1, then the info bits.
        uint v = value + 1;
        int len = 0;
        while ((v >> len) != 0)
        {
            len++;
        }

        // len = number of bits in v; leadingZeros = len-1.
        for (int i = 0; i < len - 1; i++)
        {
            bits.PutBit(0);
        }

        for (int i = len - 1; i >= 0; i--)
        {
            bits.PutBit((int)((v >> i) & 1));
        }
    }

    public void Se(int value)
    {
        uint u = value <= 0 ? (uint)(-2 * value) : (uint)((2 * value) - 1);
        Ue(u);
    }

    public void RbspTrailing() => bits.ByteAlignWithStopBit();

    public byte[] ToRbsp() => bits.ToArray();
}

internal static class HevcStreamWriter
{
    // NAL unit types.
    public const int NalVps = 32;
    public const int NalSps = 33;
    public const int NalPps = 34;
    public const int NalIdrWRadl = 19;

    /// <summary>Wraps an RBSP payload in a NAL unit: 2-byte header + emulation-prevention.</summary>
    public static byte[] WrapNal(int nalType, byte[] rbsp)
    {
        var outp = new List<byte>(rbsp.Length + 8);
        // nal_unit_header: forbidden_zero(1)=0, nal_unit_type(6), nuh_layer_id(6)=0, nuh_temporal_id_plus1(3)=1.
        outp.Add((byte)((nalType & 0x3F) << 1));
        outp.Add(0x01);

        int zeroRun = 0;
        foreach (byte b in rbsp)
        {
            if (zeroRun >= 2 && b <= 3)
            {
                outp.Add(0x03); // emulation_prevention_three_byte
                zeroRun = 0;
            }

            outp.Add(b);
            zeroRun = b == 0 ? zeroRun + 1 : 0;
        }

        return outp.ToArray();
    }

    public static byte[] BuildVps()
    {
        var w = new HevcRbspWriter();
        w.U(0, 4);        // vps_video_parameter_set_id
        w.U(3, 2);        // vps_base_layer_internal_flag(1)=1, vps_base_layer_available_flag(1)=1
        w.U(0, 6);        // vps_max_layers_minus1
        w.U(0, 3);        // vps_max_sub_layers_minus1
        w.Flag(true);     // vps_temporal_id_nesting_flag
        w.U(0xFFFF, 16);  // vps_reserved_0xffff_16bits
        WriteProfileTierLevel(w, 0);
        w.Flag(false);    // vps_sub_layer_ordering_info_present_flag
        w.Ue(0);          // vps_max_dec_pic_buffering_minus1[0]  (approx; single sub-layer)
        w.Ue(0);          // vps_max_num_reorder_pics[0]
        w.Ue(0);          // vps_max_latency_increase_plus1[0]
        w.U(0, 6);        // vps_max_layer_id
        w.Ue(0);          // vps_num_layer_sets_minus1
        w.Flag(false);    // vps_timing_info_present_flag
        w.Flag(false);    // vps_extension_flag
        w.RbspTrailing();
        return w.ToRbsp();
    }

    public static byte[] BuildSps(int paddedWidth, int paddedHeight, int cropRight, int cropBottom)
    {
        var w = new HevcRbspWriter();
        w.U(0, 4);        // sps_video_parameter_set_id
        w.U(0, 3);        // sps_max_sub_layers_minus1
        w.Flag(true);     // sps_temporal_id_nesting_flag
        WriteProfileTierLevel(w, 0);
        w.Ue(0);          // sps_seq_parameter_set_id
        w.Ue(1);          // chroma_format_idc = 1 (4:2:0)
        w.Ue((uint)paddedWidth);   // pic_width_in_luma_samples
        w.Ue((uint)paddedHeight);  // pic_height_in_luma_samples

        bool crop = cropRight != 0 || cropBottom != 0;
        w.Flag(crop);     // conformance_window_flag
        if (crop)
        {
            // 4:2:0 -> offsets are in chroma units (SubWidthC=SubHeightC=2).
            w.Ue(0);                       // conf_win_left_offset
            w.Ue((uint)(cropRight / 2));   // conf_win_right_offset
            w.Ue(0);                       // conf_win_top_offset
            w.Ue((uint)(cropBottom / 2));  // conf_win_bottom_offset
        }

        w.Ue(0);          // bit_depth_luma_minus8
        w.Ue(0);          // bit_depth_chroma_minus8
        w.Ue(4);          // log2_max_pic_order_cnt_lsb_minus4
        w.Flag(false);    // sps_sub_layer_ordering_info_present_flag
        w.Ue(0);          // sps_max_dec_pic_buffering_minus1[0]
        w.Ue(0);          // sps_max_num_reorder_pics[0]
        w.Ue(0);          // sps_max_latency_increase_plus1[0]

        // MinCb == CTB == 32: log2_min_cb=5 -> minus3 = 2; diff_max_min = 0.
        w.Ue(2);          // log2_min_luma_coding_block_size_minus3   (min CB = 32)
        w.Ue(0);          // log2_diff_max_min_luma_coding_block_size (CTB = 32)
        w.Ue(0);          // log2_min_luma_transform_block_size_minus2 (min TB = 4)
        w.Ue(3);          // log2_diff_max_min_luma_transform_block_size (max TB = 32)
        w.Ue(0);          // max_transform_hierarchy_depth_inter
        w.Ue(0);          // max_transform_hierarchy_depth_intra  (no RQT split)
        w.Flag(false);    // scaling_list_enabled_flag
        w.Flag(false);    // amp_enabled_flag
        w.Flag(false);    // sample_adaptive_offset_enabled_flag
        w.Flag(false);    // pcm_enabled_flag
        w.Ue(0);          // num_short_term_ref_pic_sets
        w.Flag(false);    // long_term_ref_pics_present_flag
        w.Flag(false);    // sps_temporal_mvp_enabled_flag
        w.Flag(true);     // strong_intra_smoothing_enabled_flag
        w.Flag(false);    // vui_parameters_present_flag
        w.Flag(false);    // sps_extension_present_flag
        w.RbspTrailing();
        return w.ToRbsp();
    }

    public static byte[] BuildPps(int initQpMinus26, bool signDataHiding)
    {
        var w = new HevcRbspWriter();
        w.Ue(0);          // pps_pic_parameter_set_id
        w.Ue(0);          // pps_seq_parameter_set_id
        w.Flag(false);    // dependent_slice_segments_enabled_flag
        w.Flag(false);    // output_flag_present_flag
        w.U(0, 3);        // num_extra_slice_header_bits
        w.Flag(signDataHiding); // sign_data_hiding_enabled_flag
        w.Flag(false);    // cabac_init_present_flag
        w.Ue(0);          // num_ref_idx_l0_default_active_minus1
        w.Ue(0);          // num_ref_idx_l1_default_active_minus1
        w.Se(initQpMinus26); // init_qp_minus26
        w.Flag(false);    // constrained_intra_pred_flag
        w.Flag(false);    // transform_skip_enabled_flag
        w.Flag(false);    // cu_qp_delta_enabled_flag
        w.Se(0);          // pps_cb_qp_offset
        w.Se(0);          // pps_cr_qp_offset
        w.Flag(false);    // pps_slice_chroma_qp_offsets_present_flag
        w.Flag(false);    // weighted_pred_flag
        w.Flag(false);    // weighted_bipred_flag
        w.Flag(false);    // transquant_bypass_enabled_flag
        w.Flag(false);    // tiles_enabled_flag
        w.Flag(false);    // entropy_coding_sync_enabled_flag
        w.Flag(false);    // pps_loop_filter_across_slices_enabled_flag
        w.Flag(true);     // deblocking_filter_control_present_flag
        w.Flag(false);    //   deblocking_filter_override_enabled_flag
        w.Flag(true);     //   pps_deblocking_filter_disabled_flag
        w.Flag(false);    // pps_scaling_list_data_present_flag
        w.Flag(false);    // lists_modification_present_flag
        w.Ue(0);          // log2_parallel_merge_level_minus2
        w.Flag(false);    // slice_segment_header_extension_present_flag
        w.Flag(false);    // pps_extension_present_flag
        w.RbspTrailing();
        return w.ToRbsp();
    }

    /// <summary>Builds the IDR I-slice segment header RBSP bits (no trailing/alignment; caller appends CABAC data).</summary>
    public static void WriteSliceHeader(HevcBitWriter w)
    {
        w.PutBit(1);      // first_slice_segment_in_pic_flag
        w.PutBit(0);      // no_output_of_prior_pics_flag (IRAP)
        // slice_pic_parameter_set_id = 0 (ue = "1")
        w.PutBit(1);
        // slice_type = 2 (I): ue(2) = "011"
        w.PutBit(0);
        w.PutBit(1);
        w.PutBit(1);
        // (SAO off, IDR so no poc/RPS, I-slice so no ref lists)
        // slice_qp_delta = 0 (se(0) = "1")
        w.PutBit(1);
        // deblocking_filter_control_present=1 but override_enabled=0 -> nothing here.
        // loop_filter_across_slices disabled in PPS -> nothing.
        // byte_alignment(): alignment_bit_equal_to_one, then zeros.
        w.PutBit(1);
        while (!w.IsByteAligned)
        {
            w.PutBit(0);
        }
    }

    private static void WriteProfileTierLevel(HevcRbspWriter w, int maxSubLayersMinus1)
    {
        w.U(0, 2);            // general_profile_space
        w.Flag(false);        // general_tier_flag
        w.U(1, 5);            // general_profile_idc = 1 (Main)
        // general_profile_compatibility_flags: bit for profile 1 set (bit index 1 from MSB).
        w.U(0x60000000 >> 0, 32); // 0110... -> profiles 1 and 2 compatible (Main / Main10-compatible-ish); avoids the RExt constraint branch
        w.Flag(true);         // general_progressive_source_flag
        w.Flag(false);        // general_interlaced_source_flag
        w.Flag(false);        // general_non_packed_constraint_flag
        w.Flag(true);         // general_frame_only_constraint_flag
        // 44 constraint/reserved bits (Main: parser skips 44).
        w.U(0, 32);
        w.U(0, 12);
        w.U(153, 8);          // general_level_idc = 5.1
    }
}
