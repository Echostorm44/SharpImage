// HEVC Parameter Set Parser
// Parses VPS, SPS, PPS from RBSP (after emulation prevention removal)

using System;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Parser for HEVC parameter sets (VPS, SPS, PPS).
/// </summary>
public static class HevcParameterSetParser
{
    /// <summary>
    /// Parses a Video Parameter Set from RBSP data.
    /// Ported from FFmpeg's ff_hevc_decode_nal_vps (ps.c).
    /// </summary>
    public static HevcVideoParameterSet? ParseVps(ReadOnlySpan<byte> rbspData)
    {
        if (rbspData.Length < 2)
            return null;

        try
        {
            var reader = new BitstreamReader(rbspData);
            var vps = new HevcVideoParameterSet();

            // vps_video_parameter_set_id: 4 bits
            vps.VideoParameterSetId = (byte)reader.ReadBits(4);
            
            // vps_base_layer_internal_flag: 1 bit
            vps.BaseLayerInternalFlag = reader.ReadBit() == 1;
            
            // vps_base_layer_available_flag: 1 bit
            vps.BaseLayerAvailableFlag = reader.ReadBit() == 1;
            
            if (!vps.BaseLayerInternalFlag || !vps.BaseLayerAvailableFlag)
                return null; // Not supported (matches FFmpeg)
            
            // vps_max_layers_minus1: 6 bits
            vps.MaxLayersMinus1 = (byte)reader.ReadBits(6);
            
            // vps_max_sub_layers_minus1: 3 bits
            vps.MaxSubLayersMinus1 = (byte)reader.ReadBits(3);
            
            // vps_temporal_id_nesting_flag: 1 bit
            vps.TemporalIdNestingFlag = reader.ReadBit() == 1;
            
            // vps_reserved_0xffff_16bits: 16 bits (skip)
            reader.SkipBits(16);

            if (vps.MaxSubLayersMinus1 + 1 > HevcConstants.MaxSublayers)
                return null;

            // Parse profile_tier_level
            vps.ProfileTierLevel = ParseProfileTierLevel(ref reader, true, vps.MaxSubLayersMinus1);

            // vps_sub_layer_ordering_info_present_flag
            vps.SubLayerOrderingInfoPresent = reader.ReadBit() == 1;
            
            int startIndex = vps.SubLayerOrderingInfoPresent ? 0 : vps.MaxSubLayersMinus1;
            for (int i = startIndex; i <= vps.MaxSubLayersMinus1; i++)
            {
                vps.MaxDecPicBufferingMinus1[i] = (int)reader.ReadExpGolombUnsigned();
                vps.MaxNumReorderPics[i] = (int)reader.ReadExpGolombUnsigned();
                vps.MaxLatencyIncreasePlus1[i] = (int)reader.ReadExpGolombUnsigned();
            }

            // vps_max_layer_id: 6 bits
            vps.MaxLayerId = (byte)reader.ReadBits(6);
            
            // vps_num_layer_sets_minus1
            vps.NumLayerSetsMinus1 = (int)reader.ReadExpGolombUnsigned();
            
            // Initialize output layer sets
            vps.Ols[0] = 1; // Layer set 0 always includes base layer
            
            // Capture layer1_id_included for extension parsing.
            // We support at most 2 layers, so read first set, skip rest.
            ulong layer1IdIncluded = 0;
            if (vps.NumLayerSetsMinus1 >= 1)
            {
                layer1IdIncluded = reader.ReadBits64(vps.MaxLayerId + 1);
            }
            for (int i = 2; i <= vps.NumLayerSetsMinus1; i++)
            {
                reader.SkipBits(vps.MaxLayerId + 1);
            }

            // vps_timing_info_present_flag
            vps.TimingInfoPresent = reader.ReadBit() == 1;
            if (vps.TimingInfoPresent)
            {
                vps.NumUnitsInTick = reader.ReadBits(32);
                vps.TimeScale = reader.ReadBits(32);
                
                // vps_poc_proportional_to_timing_flag
                if (reader.ReadBit() == 1)
                    reader.ReadExpGolombUnsigned(); // vps_num_ticks_poc_diff_one_minus1
                
                // vps_num_hrd_parameters — skip HRD data (not needed for decode)
                uint numHrdParams = reader.ReadExpGolombUnsigned();
                for (uint i = 0; i < numHrdParams; i++)
                {
                    reader.ReadExpGolombUnsigned(); // hrd_layer_set_idx
                    bool commonInfPresent = i == 0 || reader.ReadBit() == 1;
                    SkipHrdParameters(ref reader, commonInfPresent, vps.MaxSubLayersMinus1);
                }
            }

            // VPS extension
            if (vps.MaxLayersMinus1 >= 1 && reader.RemainingBits > 0 && reader.ReadBit() == 1)
            {
                if (!ParseVpsExtension(ref reader, vps, layer1IdIncluded))
                {
                    // Extension parsing failed — fall back to single-layer
                    vps.NbLayers = 1;
                }
            }

            return vps;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses VPS multi-layer extension. Ported from FFmpeg's decode_vps_ext (ps.c:460-760).
    /// Supports stereoscopic MV-HEVC with the same simplifying assumptions as FFmpeg:
    /// max 2 layers, 2 layer sets, NumScalabilityTypes=1, direct_dependency_flag[1][0]=1.
    /// </summary>
    /// <returns>True on success, false if the extension is unsupported or malformed.</returns>
    private static bool ParseVpsExtension(ref BitstreamReader reader, HevcVideoParameterSet vps, ulong layer1IdIncluded)
    {
        int vpsMaxLayers = vps.MaxLayersMinus1 + 1;
        int vpsMaxSubLayers = vps.MaxSubLayersMinus1 + 1;
        
        if (vpsMaxLayers == 1)
            return true; // Nothing to do for single-layer
        
        if (vpsMaxLayers > 2)
            return false; // Only 2 layers supported
        
        if (vps.NumLayerSetsMinus1 + 1 > 2)
            return false; // Only 2 layer sets supported
        
        reader.AlignToByte();
        
        vps.NbLayers = 2;
        
        // Parse extension PTL (discarded — we use the base VPS PTL)
        ParseProfileTierLevel(ref reader, false, vps.MaxSubLayersMinus1);
        
        // splitting_flag
        bool splittingFlag = reader.ReadBit() == 1;
        ushort scalabilityMaskFlag = (ushort)reader.ReadBits(16);
        vps.ScalabilityMaskFlag = (HevcScalabilityMask)scalabilityMaskFlag;
        int numScalabilityTypes = BitCount(scalabilityMaskFlag);
        
        if (numScalabilityTypes == 0)
            return false;
        
        if ((vps.ScalabilityMaskFlag & (HevcScalabilityMask.Multiview | HevcScalabilityMask.Auxiliary)) == 0)
            return false; // Unsupported scalability type

        // dimension_id_len
        Span<byte> dimensionIdLen = stackalloc byte[16];
        int n = 0;
        for (int i = 0; i < numScalabilityTypes - (splittingFlag ? 1 : 0); i++)
        {
            dimensionIdLen[i] = (byte)(reader.ReadBits(3) + 1);
            n += dimensionIdLen[i];
        }
        if (splittingFlag)
            dimensionIdLen[numScalabilityTypes - 1] = (byte)(5 - n);
        
        // vps_nuh_layer_id_present_flag
        if (reader.ReadBit() == 1)
        {
            int layerIdInNuh = (int)reader.ReadBits(6);
            if (layerIdInNuh > HevcConstants.MaxNuhLayerId)
                return false;
            vps.LayerIdx[layerIdInNuh] = 1;
            vps.LayerIdInNuh[1] = (byte)layerIdInNuh;
        }
        else
        {
            vps.LayerIdx[1] = 1;
            vps.LayerIdInNuh[1] = 1;
        }
        
        // dimension_id (when not splitting)
        Span<byte> dimensionId = stackalloc byte[16];
        if (!splittingFlag)
        {
            int index = 0;
            for (int i = 0; i < numScalabilityTypes; i++)
                dimensionId[i] = (byte)reader.ReadBits(dimensionIdLen[i]);
            
            if ((vps.ScalabilityMaskFlag & HevcScalabilityMask.Multiview) != 0)
                index++;
            
            // AuxId: 1=alpha, 2=depth. Only alpha supported.
            if ((vps.ScalabilityMaskFlag & HevcScalabilityMask.Auxiliary) != 0 && dimensionId[index] != 1)
                return false; // Unsupported auxiliary type
        }
        
        // view_id_len and view_id values
        int viewIdLen = (int)reader.ReadBits(4);
        if (viewIdLen > 0)
        {
            int numViews = (vps.ScalabilityMaskFlag & HevcScalabilityMask.Multiview) != 0 ? 2 : 1;
            for (int i = 0; i < numViews; i++)
                vps.ViewId[i] = (ushort)reader.ReadBits(viewIdLen);
        }
        
        // direct_dependency_flag[1][0] (single bit for 2-layer case)
        vps.NumDirectRefLayers[1] = (byte)reader.ReadBit();
        if (vps.NumDirectRefLayers[1] == 0)
        {
            // Independent layers — parse additional layer sets
            vps.NumAddLayerSets = (int)reader.ReadExpGolombUnsigned();
            if (vps.NumAddLayerSets > 1)
                return false;
            
            if (vps.NumAddLayerSets > 0)
            {
                // highest_layer_idx_plus1
                if (reader.ReadBit() == 0)
                    return false;
            }
        }
        int numOutputLayerSets = vps.NumLayerSetsMinus1 + 1 + vps.NumAddLayerSets;
        vps.NumOutputLayerSets = numOutputLayerSets;
        if (numOutputLayerSets != 2)
            return false;
        
        // vps_sub_layers_max_minus1_present_flag
        Span<byte> maxSubLayers = stackalloc byte[2];
        maxSubLayers[0] = (byte)vpsMaxSubLayers;
        maxSubLayers[1] = (byte)vpsMaxSubLayers;
        bool subLayersMaxPresent = reader.ReadBit() == 1;
        if (subLayersMaxPresent)
        {
            for (int i = 0; i < vpsMaxLayers; i++)
                maxSubLayers[i] = (byte)(reader.ReadBits(3) + 1);
        }
        
        // max_tid_ref_present_flag
        if (reader.ReadBit() == 1)
            reader.SkipBits(3); // max_tid_il_ref_pics_plus1
        
        vps.DefaultRefLayersActive = reader.ReadBit() == 1;
        
        // Profile-tier-level signalling
        int nbPtl = (int)reader.ReadExpGolombUnsigned() + 1;
        // idx[0] is in base VPS, idx[1] is at start of extension; skip idx 2+
        for (int i = 2; i < nbPtl; i++)
        {
            bool profilePresent = reader.ReadBit() == 1;
            ParseProfileTierLevel(ref reader, profilePresent, vps.MaxSubLayersMinus1);
        }
        
        // num_add_olss
        int numAddOlss = (int)reader.ReadExpGolombUnsigned();
        if (numAddOlss != 0)
            return false;
        
        // default_output_layer_idc
        int defaultOutputLayerIdc = (int)reader.ReadBits(2);
        if (defaultOutputLayerIdc != 0)
            return false;
        
        // Verify layer dependencies
        if (layer1IdIncluded != 0 &&
            layer1IdIncluded != ((1UL << vps.LayerIdInNuh[0]) | (1UL << vps.LayerIdInNuh[1])))
        {
            return false;
        }
        vps.Ols[1] = layer1IdIncluded == 0 ? 2UL : 3UL;
        
        // output_layer_flag conditional skip
        if (vps.NumLayerSetsMinus1 + 1 == 1 || defaultOutputLayerIdc == 2)
            reader.SkipBits(1);
        
        // PTL index for output layer set 1
        if (nbPtl > 1)
        {
            int olsLayerCount = BitCount64(vps.Ols[1]);
            int ptlIdxBits = CeilLog2(nbPtl);
            for (int j = 0; j < olsLayerCount; j++)
            {
                int ptlIdx = (int)reader.ReadBits(ptlIdxBits);
                if (ptlIdx >= nbPtl)
                    return false;
            }
        }
        
        // vps_num_rep_formats_minus1 — must be 0
        if (reader.ReadExpGolombUnsigned() != 0)
            return false;
        
        // rep_format
        var repFormat = new HevcRepFormat();
        repFormat.PicWidthInLumaSamples = (ushort)reader.ReadBits(16);
        repFormat.PicHeightInLumaSamples = (ushort)reader.ReadBits(16);
        
        // chroma_and_bit_depth_vps_present_flag — must be 1 for first rep_format
        if (reader.ReadBit() == 0)
            return false;
        
        repFormat.ChromaFormatIdc = (byte)reader.ReadBits(2);
        if (repFormat.ChromaFormatIdc == 3) // 4:4:4
            repFormat.SeparateColourPlaneFlag = reader.ReadBit() == 1;
        repFormat.BitDepthLuma = (byte)(reader.ReadBits(4) + 8);
        repFormat.BitDepthChroma = (byte)(reader.ReadBits(4) + 8);
        
        if (repFormat.BitDepthLuma > 16 || repFormat.BitDepthChroma > 16 ||
            repFormat.BitDepthLuma != repFormat.BitDepthChroma)
            return false;
        
        // conformance_window_vps_flag
        if (reader.ReadBit() == 1)
        {
            // Sub-width/height-C tables: {1, 2, 2, 1} and {1, 2, 1, 1}
            ReadOnlySpan<int> subWidthC = [1, 2, 2, 1];
            ReadOnlySpan<int> subHeightC = [1, 2, 1, 1];
            int horizMult = subWidthC[repFormat.ChromaFormatIdc];
            int vertMult = subHeightC[repFormat.ChromaFormatIdc];
            repFormat.ConfWinLeftOffset = (ushort)(reader.ReadExpGolombUnsigned() * horizMult);
            repFormat.ConfWinRightOffset = (ushort)(reader.ReadExpGolombUnsigned() * horizMult);
            repFormat.ConfWinTopOffset = (ushort)(reader.ReadExpGolombUnsigned() * vertMult);
            repFormat.ConfWinBottomOffset = (ushort)(reader.ReadExpGolombUnsigned() * vertMult);
        }
        vps.RepFormat = repFormat;
        
        vps.MaxOneActiveRefLayer = reader.ReadBit() == 1;
        vps.PocLsbAligned = reader.ReadBit() == 1;
        
        if (vps.NumDirectRefLayers[1] == 0)
            vps.PocLsbNotPresent = (byte)(reader.ReadBit() << 1);
        
        // DPB size
        bool subLayerFlagInfoPresentFlag = reader.ReadBit() == 1;
        int maxSubLayersBoth = Math.Max(maxSubLayers[0], maxSubLayers[1]);
        var dpbSize = new HevcVpsDpbSize();
        for (int j = 0; j < maxSubLayersBoth; j++)
        {
            bool subLayerDpbInfoPresent = true;
            if (j > 0 && subLayerFlagInfoPresentFlag)
                subLayerDpbInfoPresent = reader.ReadBit() == 1;
            
            if (subLayerDpbInfoPresent)
            {
                int olsLayerCount = BitCount64(vps.Ols[1]);
                for (int k = 0; k < olsLayerCount; k++)
                    dpbSize.MaxDecPicBuffering = (int)reader.ReadExpGolombUnsigned() + 1;
                dpbSize.MaxNumReorderPics = (int)reader.ReadExpGolombUnsigned();
                dpbSize.MaxLatencyIncrease = (int)reader.ReadExpGolombUnsigned() - 1;
            }
        }
        vps.DpbSize = dpbSize;
        
        // direct_dep_type_len
        int directDepTypeLen = (int)reader.ReadExpGolombUnsigned() + 2;
        if (directDepTypeLen > 32)
            return false;
        
        // direct_dependency_all_layers_flag
        if (reader.ReadBit() == 1)
        {
            int directDepType = (int)reader.ReadBits(directDepTypeLen);
            if (directDepType > 2) // > HEVC_DEP_TYPE_BOTH
                return false;
        }
        
        // non_vui_extension
        uint nonVuiExtensionLength = reader.ReadExpGolombUnsigned();
        if (nonVuiExtensionLength > 4096)
            return false;
        reader.SkipBits((int)(nonVuiExtensionLength * 8));
        
        // VPS VUI (ignored)
        // if (reader.RemainingBits > 0 && reader.ReadBit() == 1) { /* VPS VUI */ }
        
        return true;
    }
    
    /// <summary>Population count (number of set bits) for a 16-bit value.</summary>
    private static int BitCount(ushort value)
    {
        int count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }
        return count;
    }
    
    /// <summary>Population count (number of set bits) for a 64-bit value.</summary>
    private static int BitCount64(ulong value)
    {
        int count = 0;
        while (value != 0)
        {
            count++;
            value &= value - 1; // Clear lowest set bit
        }
        return count;
    }
    
    /// <summary>Ceiling of log2 (at least 1). Used for bit-width calculations.</summary>
    private static int CeilLog2(int value)
    {
        int bits = 0;
        int v = value - 1;
        while (v > 0)
        {
            bits++;
            v >>= 1;
        }
        return Math.Max(bits, 1);
    }
    /// <summary>
    /// Parses a Sequence Parameter Set from RBSP data.
    /// For multi-layer streams, pass the nuhLayerId and referenced VPS.
    /// </summary>
    public static HevcSequenceParameterSet? ParseSps(ReadOnlySpan<byte> rbspData, byte nuhLayerId = 0, HevcVideoParameterSet? vps = null)
    {
        if (rbspData.Length < 3)
            return null;

        try
        {
            var reader = new BitstreamReader(rbspData);
            var sps = new HevcSequenceParameterSet();

            // sps_video_parameter_set_id: 4 bits
            sps.VideoParameterSetId = (byte)reader.ReadBits(4);
            
            // sps_max_sub_layers_minus1: 3 bits
            sps.MaxSubLayersMinus1 = (byte)reader.ReadBits(3);
            
            // Detect multi-layer extension SPS (F.7.3.2.2.1):
            // nuh_layer_id > 0 and coded max_sub_layers = 8 (3-bit field = 7, +1 = 8)
            bool multiLayerExt = nuhLayerId > 0 && sps.MaxSubLayersMinus1 + 1 == HevcConstants.MaxSublayers;
            sps.MultiLayerExt = multiLayerExt;
            
            if (multiLayerExt)
            {
                if (vps == null || vps.NbLayers < 2)
                    return null;
                
                // Inherit max_sub_layers from VPS
                sps.MaxSubLayersMinus1 = vps.MaxSubLayersMinus1;
            }
            
            if (!multiLayerExt)
            {
                // sps_temporal_id_nesting_flag: 1 bit
                sps.TemporalIdNestingFlag = reader.ReadBit() == 1;

                // Parse profile_tier_level
                sps.ProfileTierLevel = ParseProfileTierLevel(ref reader, true, sps.MaxSubLayersMinus1);
            }
            else
            {
                // Multi-layer SPS: temporal_id_nesting and PTL are not present
                sps.TemporalIdNestingFlag = sps.MaxSubLayersMinus1 > 0;
                // Inherit PTL from VPS
                sps.ProfileTierLevel = vps!.ProfileTierLevel;
            }

            // sps_seq_parameter_set_id
            sps.SequenceParameterSetId = (byte)reader.ReadExpGolombUnsigned();
            if (sps.SequenceParameterSetId > HevcParameterSetLimits.MaxSpsId)
                return null;

            if (multiLayerExt)
            {
                // Multi-layer extension: get dimensions, chroma, bit depth from VPS RepFormat
                var rf = vps!.RepFormat;
                
                // update_rep_format_flag + sps_rep_format_idx
                bool updateRepFormat = reader.ReadBit() == 1;
                if (updateRepFormat)
                {
                    int repFormatIdx = (int)reader.ReadBits(8);
                    if (repFormatIdx != 0)
                        return null; // Only single rep_format supported
                }
                
                sps.SeparateColourPlaneFlag = rf.SeparateColourPlaneFlag;
                sps.ChromaFormatIdc = rf.SeparateColourPlaneFlag 
                    ? HevcChromaFormat.Monochrome 
                    : (HevcChromaFormat)rf.ChromaFormatIdc;
                sps.BitDepthLumaMinus8 = (byte)(rf.BitDepthLuma - 8);
                sps.BitDepthChromaMinus8 = (byte)(rf.BitDepthChroma - 8);
                sps.PictureWidthInLumaSamples = rf.PicWidthInLumaSamples;
                sps.PictureHeightInLumaSamples = rf.PicHeightInLumaSamples;
                
                sps.ConformanceWindowFlag = rf.ConfWinLeftOffset != 0 || rf.ConfWinRightOffset != 0 ||
                                            rf.ConfWinTopOffset != 0 || rf.ConfWinBottomOffset != 0;
                sps.ConfWinLeftOffset = rf.ConfWinLeftOffset;
                sps.ConfWinRightOffset = rf.ConfWinRightOffset;
                sps.ConfWinTopOffset = rf.ConfWinTopOffset;
                sps.ConfWinBottomOffset = rf.ConfWinBottomOffset;
            }
            else
            {
                // Standard SPS: parse chroma, dimensions, bit depth from bitstream
                
                // chroma_format_idc
                sps.ChromaFormatIdc = (HevcChromaFormat)reader.ReadExpGolombUnsigned();
                
                // separate_colour_plane_flag (only if chroma_format_idc == 3)
                if (sps.ChromaFormatIdc == HevcChromaFormat.Chroma444)
                {
                    sps.SeparateColourPlaneFlag = reader.ReadBit() == 1;
                }

                // pic_width_in_luma_samples
                sps.PictureWidthInLumaSamples = (int)reader.ReadExpGolombUnsigned();
                
                // pic_height_in_luma_samples
                sps.PictureHeightInLumaSamples = (int)reader.ReadExpGolombUnsigned();
                
                if (sps.PictureWidthInLumaSamples == 0 || sps.PictureHeightInLumaSamples == 0)
                    return null;

                // conformance_window_flag
                sps.ConformanceWindowFlag = reader.ReadBit() == 1;
                if (sps.ConformanceWindowFlag)
                {
                    sps.ConfWinLeftOffset = (int)reader.ReadExpGolombUnsigned();
                    sps.ConfWinRightOffset = (int)reader.ReadExpGolombUnsigned();
                    sps.ConfWinTopOffset = (int)reader.ReadExpGolombUnsigned();
                    sps.ConfWinBottomOffset = (int)reader.ReadExpGolombUnsigned();
                }

                // bit_depth_luma_minus8
                sps.BitDepthLumaMinus8 = (byte)reader.ReadExpGolombUnsigned();
                
                // bit_depth_chroma_minus8
                sps.BitDepthChromaMinus8 = (byte)reader.ReadExpGolombUnsigned();
            }
            
            // log2_max_pic_order_cnt_lsb_minus4
            sps.Log2MaxPicOrderCntLsbMinus4 = (byte)reader.ReadExpGolombUnsigned();

            if (!multiLayerExt)
            {
                // sps_sub_layer_ordering_info_present_flag
                sps.SubLayerOrderingInfoPresent = reader.ReadBit() == 1;
                
                int startIndex = sps.SubLayerOrderingInfoPresent ? 0 : sps.MaxSubLayersMinus1;
                for (int i = startIndex; i <= sps.MaxSubLayersMinus1; i++)
                {
                    sps.MaxDecPicBufferingMinus1[i] = (int)reader.ReadExpGolombUnsigned();
                    sps.MaxNumReorderPics[i] = (int)reader.ReadExpGolombUnsigned();
                    sps.MaxLatencyIncreasePlus1[i] = (int)reader.ReadExpGolombUnsigned();
                }
            }
            else
            {
                // Multi-layer extension: DPB sizing from VPS extension
                var dpb = vps!.DpbSize;
                for (int i = 0; i <= sps.MaxSubLayersMinus1; i++)
                {
                    sps.MaxDecPicBufferingMinus1[i] = dpb.MaxDecPicBuffering - 1;
                    sps.MaxNumReorderPics[i] = dpb.MaxNumReorderPics;
                    sps.MaxLatencyIncreasePlus1[i] = dpb.MaxLatencyIncrease + 1;
                }
            }

            // CTU/CU/TU size parameters - key HEVC feature
            sps.Log2MinLumaCodingBlockSizeMinus3 = (byte)reader.ReadExpGolombUnsigned();
            sps.Log2DiffMaxMinLumaCodingBlockSize = (byte)reader.ReadExpGolombUnsigned();
            sps.Log2MinLumaTransformBlockSizeMinus2 = (byte)reader.ReadExpGolombUnsigned();
            sps.Log2DiffMaxMinLumaTransformBlockSize = (byte)reader.ReadExpGolombUnsigned();
            
            sps.MaxTransformHierarchyDepthInter = (byte)reader.ReadExpGolombUnsigned();
            sps.MaxTransformHierarchyDepthIntra = (byte)reader.ReadExpGolombUnsigned();

            // scaling_list_enabled_flag
            sps.ScalingListEnabled = reader.ReadBit() == 1;
            if (sps.ScalingListEnabled)
            {
                // Always set up defaults first (like FFmpeg's set_default_scaling_list_data)
                sps.ScalingList = new HevcScalingList();
                sps.ScalingList.SetDefaults();
                
                sps.ScalingListDataPresent = reader.ReadBit() == 1;
                if (sps.ScalingListDataPresent)
                {
                    ParseScalingListData(ref reader, sps.ScalingList, (int)sps.ChromaFormatIdc);
                }
            }

            // amp_enabled_flag
            sps.AmpEnabled = reader.ReadBit() == 1;
            
            // sample_adaptive_offset_enabled_flag (new in HEVC)
            sps.SampleAdaptiveOffsetEnabled = reader.ReadBit() == 1;

            // pcm_enabled_flag
            sps.PcmEnabled = reader.ReadBit() == 1;
            if (sps.PcmEnabled)
            {
                sps.PcmSampleBitDepthLumaMinus1 = (byte)reader.ReadBits(4);
                sps.PcmSampleBitDepthChromaMinus1 = (byte)reader.ReadBits(4);
                sps.Log2MinPcmLumaCodingBlockSizeMinus3 = (byte)reader.ReadExpGolombUnsigned();
                sps.Log2DiffMaxMinPcmLumaCodingBlockSize = (byte)reader.ReadExpGolombUnsigned();
                sps.PcmLoopFilterDisabled = reader.ReadBit() == 1;
            }

            // num_short_term_ref_pic_sets
            sps.NumShortTermRefPicSets = (int)reader.ReadExpGolombUnsigned();
            if (sps.NumShortTermRefPicSets > HevcConstants.MaxShortTermRefPicSets)
                return null;

            // Parse short-term reference picture sets
            sps.ShortTermRpsList = new HevcShortTermRps[sps.NumShortTermRefPicSets + 1]; // +1 for slice-header inline RPS
            for (int i = 0; i < sps.NumShortTermRefPicSets; i++)
            {
                sps.ShortTermRpsList[i] = new HevcShortTermRps();
                if (!ParseShortTermRefPicSet(ref reader, sps.ShortTermRpsList, i, sps.NumShortTermRefPicSets, isSliceHeader: false))
                    return null;
            }
            // Allocate one extra slot for slice-header inline RPS
            sps.ShortTermRpsList[sps.NumShortTermRefPicSets] = new HevcShortTermRps();

            // long_term_ref_pics_present_flag
            sps.LongTermRefPicsPresent = reader.ReadBit() == 1;
            if (sps.LongTermRefPicsPresent)
            {
                sps.NumLongTermRefPicsSps = (int)reader.ReadExpGolombUnsigned();
                if (sps.NumLongTermRefPicsSps > HevcConstants.MaxLongTermRefPicSets)
                    return null;

                int pocLsbBits = sps.Log2MaxPicOrderCntLsbMinus4 + 4;
                sps.LtRefPicPocLsbSps = new int[sps.NumLongTermRefPicsSps];
                sps.UsedByCurrPicLtSpsFlag = new bool[sps.NumLongTermRefPicsSps];
                for (int i = 0; i < sps.NumLongTermRefPicsSps; i++)
                {
                    sps.LtRefPicPocLsbSps[i] = (int)reader.ReadBits(pocLsbBits);
                    sps.UsedByCurrPicLtSpsFlag[i] = reader.ReadBit() == 1;
                }
            }

            // sps_temporal_mvp_enabled_flag
            sps.SpsTemporalMvpEnabled = reader.ReadBit() == 1;
            
            // strong_intra_smoothing_enabled_flag
            sps.StrongIntraSmoothingEnabled = reader.ReadBit() == 1;

            // vui_parameters_present_flag
            sps.VuiParametersPresent = reader.ReadBit() == 1;
            if (sps.VuiParametersPresent)
            {
                sps.Vui = ParseVuiParameters(ref reader, sps.MaxSubLayersMinus1);
            }

            // sps_extension_present_flag
            sps.ExtensionPresent = reader.ReadBit() == 1;
            if (sps.ExtensionPresent)
            {
                sps.RangeExtension = reader.ReadBit() == 1;
                reader.SkipBits(1); // multilayer_extension
                reader.SkipBits(1); // sps_3d_extension
                reader.SkipBits(1); // scc_extension
                reader.SkipBits(4); // sps_extension_4bits

                if (sps.RangeExtension)
                {
                    sps.TransformSkipRotationEnabled = reader.ReadBit() == 1;
                    sps.TransformSkipContextEnabled = reader.ReadBit() == 1;
                    sps.ImplicitRdpcmEnabled = reader.ReadBit() == 1;
                    sps.ExplicitRdpcmEnabled = reader.ReadBit() == 1;
                    sps.ExtendedPrecisionProcessing = reader.ReadBit() == 1;
                    sps.IntraSmoothingDisabled = reader.ReadBit() == 1;
                    sps.HighPrecisionOffsetsEnabled = reader.ReadBit() == 1;
                    sps.PersistentRiceAdaptationEnabled = reader.ReadBit() == 1;
                    sps.CabacBypassAlignmentEnabled = reader.ReadBit() == 1;
                }
            }

            return sps;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a Picture Parameter Set from RBSP data.
    /// </summary>
    public static HevcPictureParameterSet? ParsePps(ReadOnlySpan<byte> rbspData, int chromaFormatIdc = 1, HevcProfile profileIdc = HevcProfile.None)
    {
        if (rbspData.Length < 2)
            return null;

        try
        {
            var reader = new BitstreamReader(rbspData);
            var pps = new HevcPictureParameterSet();

            // pps_pic_parameter_set_id
            pps.PictureParameterSetId = (byte)reader.ReadExpGolombUnsigned();
            if (pps.PictureParameterSetId > HevcParameterSetLimits.MaxPpsId)
                return null;

            // pps_seq_parameter_set_id
            pps.SequenceParameterSetId = (byte)reader.ReadExpGolombUnsigned();
            if (pps.SequenceParameterSetId > HevcParameterSetLimits.MaxSpsId)
                return null;

            // dependent_slice_segments_enabled_flag
            pps.DependentSliceSegmentsEnabled = reader.ReadBit() == 1;
            
            // output_flag_present_flag
            pps.OutputFlagPresent = reader.ReadBit() == 1;
            
            // num_extra_slice_header_bits: 3 bits
            pps.NumExtraSliceHeaderBits = (byte)reader.ReadBits(3);
            
            // sign_data_hiding_enabled_flag
            pps.SignDataHidingEnabled = reader.ReadBit() == 1;
            
            // cabac_init_present_flag
            pps.CabacInitPresent = reader.ReadBit() == 1;

            // num_ref_idx_l0_default_active_minus1
            pps.NumRefIdxL0DefaultActiveMinus1 = (byte)reader.ReadExpGolombUnsigned();
            
            // num_ref_idx_l1_default_active_minus1
            pps.NumRefIdxL1DefaultActiveMinus1 = (byte)reader.ReadExpGolombUnsigned();

            // init_qp_minus26
            pps.InitQpMinus26 = (sbyte)reader.ReadExpGolombSigned();
            
            // constrained_intra_pred_flag
            pps.ConstrainedIntraPred = reader.ReadBit() == 1;
            
            // transform_skip_enabled_flag
            pps.TransformSkipEnabled = reader.ReadBit() == 1;
            
            // cu_qp_delta_enabled_flag
            pps.CuQpDeltaEnabled = reader.ReadBit() == 1;
            if (pps.CuQpDeltaEnabled)
            {
                pps.DiffCuQpDeltaDepth = (int)reader.ReadExpGolombUnsigned();
            }

            // pps_cb_qp_offset
            pps.PpsCbQpOffset = (sbyte)reader.ReadExpGolombSigned();
            
            // pps_cr_qp_offset
            pps.PpsCrQpOffset = (sbyte)reader.ReadExpGolombSigned();
            
            // pps_slice_chroma_qp_offsets_present_flag
            pps.PicSliceLevelChromaQpOffsetsPresent = reader.ReadBit() == 1;
            
            // weighted_pred_flag
            pps.WeightedPred = reader.ReadBit() == 1;
            
            // weighted_bipred_flag
            pps.WeightedBipred = reader.ReadBit() == 1;
            
            // transquant_bypass_enabled_flag
            pps.TransquantBypassEnabled = reader.ReadBit() == 1;
            
            // tiles_enabled_flag
            pps.TilesEnabled = reader.ReadBit() == 1;
            
            // entropy_coding_sync_enabled_flag
            pps.EntropyCodingSyncEnabled = reader.ReadBit() == 1;

            if (pps.TilesEnabled)
            {
                pps.NumTileColumnsMinus1 = (int)reader.ReadExpGolombUnsigned();
                pps.NumTileRowsMinus1 = (int)reader.ReadExpGolombUnsigned();
                pps.UniformSpacingFlag = reader.ReadBit() == 1;
                
                if (!pps.UniformSpacingFlag)
                {
                    pps.ColumnWidthMinus1 = new int[pps.NumTileColumnsMinus1];
                    for (int i = 0; i < pps.NumTileColumnsMinus1; i++)
                        pps.ColumnWidthMinus1[i] = (int)reader.ReadExpGolombUnsigned();
                    
                    pps.RowHeightMinus1 = new int[pps.NumTileRowsMinus1];
                    for (int i = 0; i < pps.NumTileRowsMinus1; i++)
                        pps.RowHeightMinus1[i] = (int)reader.ReadExpGolombUnsigned();
                }
                
                pps.LoopFilterAcrossTilesEnabled = reader.ReadBit() == 1;
            }

            // pps_loop_filter_across_slices_enabled_flag
            pps.LoopFilterAcrossSlicesEnabled = reader.ReadBit() == 1;
            
            // deblocking_filter_control_present_flag
            pps.DeblockingFilterControlPresent = reader.ReadBit() == 1;
            if (pps.DeblockingFilterControlPresent)
            {
                pps.DeblockingFilterOverrideEnabled = reader.ReadBit() == 1;
                pps.DeblockingFilterDisabled = reader.ReadBit() == 1;
                
                if (!pps.DeblockingFilterDisabled)
                {
                    pps.BetaOffsetDiv2 = (sbyte)reader.ReadExpGolombSigned();
                    pps.TcOffsetDiv2 = (sbyte)reader.ReadExpGolombSigned();
                }
            }

            // pps_scaling_list_data_present_flag
            pps.ScalingListDataPresent = reader.ReadBit() == 1;
            if (pps.ScalingListDataPresent)
            {
                // Start with defaults, then override with parsed data (matches FFmpeg)
                pps.ScalingList = new HevcScalingList();
                pps.ScalingList.SetDefaults();
                ParseScalingListData(ref reader, pps.ScalingList, chromaFormatIdc);
            }

            // lists_modification_present_flag
            pps.ListsModificationPresent = reader.ReadBit() == 1;
            
            // log2_parallel_merge_level_minus2
            pps.Log2ParallelMergeLevelMinus2 = (byte)reader.ReadExpGolombUnsigned();
            
            // slice_segment_header_extension_present_flag
            pps.SliceHeaderExtensionPresent = reader.ReadBit() == 1;

            // pps_extension_present_flag
            pps.PpsExtensionPresent = reader.ReadBit() == 1;
            if (pps.PpsExtensionPresent)
            {
                pps.PpsRangeExtensionFlag = reader.ReadBit() == 1;
                pps.PpsMultilayerExtensionFlag = reader.ReadBit() == 1;
                pps.Pps3DExtensionFlag = reader.ReadBit() == 1;
                reader.SkipBits(5); // pps_extension_5bits

                // PPS range extension (FFmpeg ps.c:2399: profile_idc >= REXT guard)
                if (profileIdc >= HevcProfile.RangeExtensions && pps.PpsRangeExtensionFlag)
                {
                    if (pps.TransformSkipEnabled)
                        pps.Log2MaxTransformSkipBlockSize = (int)reader.ReadExpGolombUnsigned() + 2;
                    pps.CrossComponentPredictionEnabled = reader.ReadBit() == 1;
                    pps.ChromaQpOffsetListEnabled = reader.ReadBit() == 1;
                    if (pps.ChromaQpOffsetListEnabled)
                    {
                        pps.DiffCuChromaQpOffsetDepth = (int)reader.ReadExpGolombUnsigned();
                        pps.ChromaQpOffsetListLen = (int)reader.ReadExpGolombUnsigned() + 1;
                        pps.CbQpOffset = new int[pps.ChromaQpOffsetListLen];
                        pps.CrQpOffset = new int[pps.ChromaQpOffsetListLen];
                        for (int i = 0; i < pps.ChromaQpOffsetListLen; i++)
                        {
                            pps.CbQpOffset[i] = reader.ReadExpGolombSigned();
                            pps.CrQpOffset[i] = reader.ReadExpGolombSigned();
                        }
                    }
                    pps.Log2SaoOffsetScale0 = (int)reader.ReadExpGolombUnsigned();
                    pps.Log2SaoOffsetScale1 = (int)reader.ReadExpGolombUnsigned();
                }
            }

            return pps;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses profile_tier_level structure.
    /// </summary>
    private static HevcProfileTierLevel ParseProfileTierLevel(
        ref BitstreamReader reader, 
        bool profilePresentFlag,
        int maxNumSubLayersMinus1)
    {
        var ptl = new HevcProfileTierLevel();

        if (profilePresentFlag)
        {
            ptl = ptl with
            {
                GeneralProfileSpace = (byte)reader.ReadBits(2),
                GeneralTierFlag = reader.ReadBit() == 1,
                GeneralProfileIdc = (HevcProfile)reader.ReadBits(5),
                GeneralProfileCompatibilityFlags = reader.ReadBits(32),
                GeneralProgressiveSourceFlag = reader.ReadBit() == 1,
                GeneralInterlacedSourceFlag = reader.ReadBit() == 1,
                GeneralNonPackedConstraintFlag = reader.ReadBit() == 1,
                GeneralFrameOnlyConstraintFlag = reader.ReadBit() == 1
            };

            // Check for range extension profiles (4-10)
            int profileIdc = (int)ptl.GeneralProfileIdc;
            if ((profileIdc >= 4 && profileIdc <= 10) ||
                ((ptl.GeneralProfileCompatibilityFlags & 0x0F700000) != 0))
            {
                ptl = ptl with
                {
                    Max12BitConstraintFlag = reader.ReadBit() == 1,
                    Max10BitConstraintFlag = reader.ReadBit() == 1,
                    Max8BitConstraintFlag = reader.ReadBit() == 1,
                    Max422ChromaConstraintFlag = reader.ReadBit() == 1,
                    Max420ChromaConstraintFlag = reader.ReadBit() == 1,
                    MaxMonochromeConstraintFlag = reader.ReadBit() == 1,
                    IntraConstraintFlag = reader.ReadBit() == 1,
                    OnePictureOnlyConstraintFlag = reader.ReadBit() == 1,
                    LowerBitRateConstraintFlag = reader.ReadBit() == 1,
                    Max14BitConstraintFlag = reader.ReadBit() == 1
                };
                reader.SkipBits(34); // reserved bits
            }
            else
            {
                reader.SkipBits(44); // skip constraint flags
            }
        }

        // general_level_idc: 8 bits
        ptl = ptl with { GeneralLevelIdc = (HevcLevel)reader.ReadBits(8) };

        // Sub-layer profile/level flags and data (FFmpeg: parse_ptl lines 331-354)
        if (maxNumSubLayersMinus1 > 0)
        {
            Span<bool> subLayerProfilePresent = stackalloc bool[maxNumSubLayersMinus1];
            Span<bool> subLayerLevelPresent = stackalloc bool[maxNumSubLayersMinus1];

            for (int i = 0; i < maxNumSubLayersMinus1; i++)
            {
                subLayerProfilePresent[i] = reader.ReadBit() == 1;
                subLayerLevelPresent[i] = reader.ReadBit() == 1;
            }

            // Reserved 2 bits for unused sub-layer slots up to 8
            for (int i = maxNumSubLayersMinus1; i < 8; i++)
                reader.SkipBits(2);

            // Parse/skip sub-layer profile_tier_level data
            for (int i = 0; i < maxNumSubLayersMinus1; i++)
            {
                if (subLayerProfilePresent[i])
                    reader.SkipBits(88); // profile_tier_level is always 88 bits

                if (subLayerLevelPresent[i])
                    reader.SkipBits(8); // sub_layer_level_idc
            }
        }

        return ptl;
    }

    /// <summary>
    /// Parses VUI parameters.
    /// </summary>
    private static HevcVideoUsabilityInfo ParseVuiParameters(ref BitstreamReader reader, int maxSubLayersMinus1)
    {
        var vui = new HevcVideoUsabilityInfo();

        // aspect_ratio_info_present_flag
        vui.AspectRatioInfoPresent = reader.ReadBit() == 1;
        if (vui.AspectRatioInfoPresent)
        {
            vui.AspectRatioIdc = (byte)reader.ReadBits(8);
            if (vui.AspectRatioIdc == 255) // Extended SAR
            {
                vui.SarWidth = (ushort)reader.ReadBits(16);
                vui.SarHeight = (ushort)reader.ReadBits(16);
            }
        }

        // overscan_info_present_flag
        vui.OverscanInfoPresent = reader.ReadBit() == 1;
        if (vui.OverscanInfoPresent)
        {
            vui.OverscanAppropriate = reader.ReadBit() == 1;
        }

        // video_signal_type_present_flag
        vui.VideoSignalTypePresent = reader.ReadBit() == 1;
        if (vui.VideoSignalTypePresent)
        {
            vui.VideoFormat = (byte)reader.ReadBits(3);
            vui.VideoFullRangeFlag = reader.ReadBit() == 1;
            vui.ColourDescriptionPresent = reader.ReadBit() == 1;
            if (vui.ColourDescriptionPresent)
            {
                vui.ColourPrimaries = (byte)reader.ReadBits(8);
                vui.TransferCharacteristics = (byte)reader.ReadBits(8);
                vui.MatrixCoefficients = (byte)reader.ReadBits(8);
            }
        }

        // chroma_loc_info_present_flag
        vui.ChromaLocInfoPresent = reader.ReadBit() == 1;
        if (vui.ChromaLocInfoPresent)
        {
            vui.ChromaSampleLocTypeTopField = (int)reader.ReadExpGolombUnsigned();
            vui.ChromaSampleLocTypeBottomField = (int)reader.ReadExpGolombUnsigned();
        }

        // neutral_chroma_indication_flag
        vui.NeutralChromaIndicationFlag = reader.ReadBit() == 1;
        
        // field_seq_flag
        vui.FieldSeqFlag = reader.ReadBit() == 1;
        
        // frame_field_info_present_flag
        vui.FrameFieldInfoPresentFlag = reader.ReadBit() == 1;

        // default_display_window_flag
        vui.DefaultDisplayWindowFlag = reader.ReadBit() == 1;
        if (vui.DefaultDisplayWindowFlag)
        {
            vui.DefDispWinLeftOffset = (int)reader.ReadExpGolombUnsigned();
            vui.DefDispWinRightOffset = (int)reader.ReadExpGolombUnsigned();
            vui.DefDispWinTopOffset = (int)reader.ReadExpGolombUnsigned();
            vui.DefDispWinBottomOffset = (int)reader.ReadExpGolombUnsigned();
        }

        // vui_timing_info_present_flag
        vui.TimingInfoPresent = reader.ReadBit() == 1;
        if (vui.TimingInfoPresent)
        {
            vui.NumUnitsInTick = reader.ReadBits(32);
            vui.TimeScale = reader.ReadBits(32);
            vui.PocProportionalToTimingFlag = reader.ReadBit() == 1;
            if (vui.PocProportionalToTimingFlag)
                vui.NumTicksPocDiffOneMinus1 = (int)reader.ReadExpGolombUnsigned();
            vui.HrdParametersPresent = reader.ReadBit() == 1;
            if (vui.HrdParametersPresent)
                SkipHrdParameters(ref reader, true, maxSubLayersMinus1);
        }

        // bitstream_restriction_flag
        vui.BitstreamRestrictionFlag = reader.ReadBit() == 1;
        if (vui.BitstreamRestrictionFlag)
        {
            vui.TilesFixedStructureFlag = reader.ReadBit() == 1;
            vui.MotionVectorsOverPicBoundariesFlag = reader.ReadBit() == 1;
            vui.RestrictedRefPicListsFlag = reader.ReadBit() == 1;
            vui.MinSpatialSegmentationIdc = (int)reader.ReadExpGolombUnsigned();
            vui.MaxBytesPerPicDenom = (int)reader.ReadExpGolombUnsigned();
            vui.MaxBitsPerMinCuDenom = (int)reader.ReadExpGolombUnsigned();
            vui.Log2MaxMvLengthHorizontal = (int)reader.ReadExpGolombUnsigned();
            vui.Log2MaxMvLengthVertical = (int)reader.ReadExpGolombUnsigned();
        }

        return vui;
    }

    /// <summary>
    /// Skips HRD parameters in the bitstream, consuming all bits correctly without storing values.
    /// Matches FFmpeg's decode_hrd() in ps.c.
    /// </summary>
    private static void SkipHrdParameters(ref BitstreamReader reader, bool commonInfPresent, int maxSubLayersMinus1)
    {
        bool nalHrdPresent = false;
        bool vclHrdPresent = false;
        bool subPicPresent = false;

        if (commonInfPresent)
        {
            nalHrdPresent = reader.ReadBit() == 1;
            vclHrdPresent = reader.ReadBit() == 1;

            if (nalHrdPresent || vclHrdPresent)
            {
                subPicPresent = reader.ReadBit() == 1; // sub_pic_hrd_params_present_flag
                if (subPicPresent)
                {
                    reader.SkipBits(8);  // tick_divisor_minus2
                    reader.SkipBits(5);  // du_cpb_removal_delay_increment_length_minus1
                    reader.SkipBits(1);  // sub_pic_cpb_params_in_pic_timing_sei_flag
                    reader.SkipBits(5);  // dpb_output_delay_du_length_minus1
                }
                reader.SkipBits(4); // bit_rate_scale
                reader.SkipBits(4); // cpb_size_scale
                if (subPicPresent)
                    reader.SkipBits(4); // cpb_size_du_scale
                reader.SkipBits(5); // initial_cpb_removal_delay_length_minus1
                reader.SkipBits(5); // au_cpb_removal_delay_length_minus1
                reader.SkipBits(5); // dpb_output_delay_length_minus1
            }
        }

        for (int i = 0; i <= maxSubLayersMinus1; i++)
        {
            bool fixedPicRateGeneral = reader.ReadBit() == 1;
            bool fixedPicRateWithinCvs = false;
            bool lowDelayHrd = false;

            if (!fixedPicRateGeneral)
                fixedPicRateWithinCvs = reader.ReadBit() == 1;

            if (fixedPicRateWithinCvs || fixedPicRateGeneral)
                reader.ReadExpGolombUnsigned(); // elemental_duration_in_tc_minus1
            else
                lowDelayHrd = reader.ReadBit() == 1;

            int cpbCntMinus1 = 0;
            if (!lowDelayHrd)
                cpbCntMinus1 = (int)reader.ReadExpGolombUnsigned();

            if (nalHrdPresent)
                SkipSublayerHrd(ref reader, cpbCntMinus1 + 1, subPicPresent);
            if (vclHrdPresent)
                SkipSublayerHrd(ref reader, cpbCntMinus1 + 1, subPicPresent);
        }
    }

    /// <summary>
    /// Skips sublayer HRD parameters. Matches FFmpeg's decode_sublayer_hrd() in ps.c.
    /// </summary>
    private static void SkipSublayerHrd(ref BitstreamReader reader, int nbCpb, bool subPicPresent)
    {
        for (int i = 0; i < nbCpb; i++)
        {
            reader.ReadExpGolombUnsigned(); // bit_rate_value_minus1
            reader.ReadExpGolombUnsigned(); // cpb_size_value_minus1
            if (subPicPresent)
            {
                reader.ReadExpGolombUnsigned(); // cpb_size_du_value_minus1
                reader.ReadExpGolombUnsigned(); // bit_rate_du_value_minus1
            }
            reader.SkipBits(1); // cbr_flag
        }
    }

    /// <summary>
    /// Skips over scaling list data (complex structure not needed for basic decoding).
    /// </summary>
    /// <summary>
    /// Parses scaling_list_data() and populates the ScalingList structure.
    /// Matches FFmpeg's scaling_list_data() in ps.c.
    /// </summary>
    private static void ParseScalingListData(ref BitstreamReader reader, HevcScalingList sl, int chromaFormatIdc)
    {
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            int matrixIdStep = (sizeId == 3) ? 3 : 1;
            for (int matrixId = 0; matrixId < 6; matrixId += matrixIdStep)
            {
                bool scalingListPredModeFlag = reader.ReadBit() == 1;
                if (!scalingListPredModeFlag)
                {
                    // Prediction mode: copy from a previous matrix
                    int delta = (int)reader.ReadExpGolombUnsigned();
                    if (delta > 0)
                    {
                        int refMatrixId = matrixId - delta * ((sizeId == 3) ? 3 : 1);
                        if (refMatrixId >= 0)
                        {
                            Array.Copy(sl.Sl[sizeId][refMatrixId], sl.Sl[sizeId][matrixId],
                                sizeId > 0 ? 64 : 16);
                            if (sizeId > 1)
                                sl.SlDc[sizeId - 2][matrixId] = sl.SlDc[sizeId - 2][refMatrixId];
                        }
                    }
                    // delta == 0: use default (already set by SetDefaults)
                }
                else
                {
                    // Explicit mode: parse coefficients
                    int nextCoef = 8;
                    int coefNum = Math.Min(64, 1 << (4 + (sizeId << 1)));
                    
                    if (sizeId > 1)
                    {
                        int dcCoefMinus8 = reader.ReadExpGolombSigned();
                        nextCoef = dcCoefMinus8 + 8;
                        sl.SlDc[sizeId - 2][matrixId] = (byte)nextCoef;
                    }
                    
                    for (int i = 0; i < coefNum; i++)
                    {
                        int pos;
                        if (sizeId == 0)
                            pos = 4 * HevcScalingList.DiagScan4x4Y[i] + HevcScalingList.DiagScan4x4X[i];
                        else
                            pos = 8 * HevcScalingList.DiagScan8x8Y[i] + HevcScalingList.DiagScan8x8X[i];
                        
                        int deltaCoef = reader.ReadExpGolombSigned();
                        nextCoef = (nextCoef + 256 + deltaCoef) % 256;
                        sl.Sl[sizeId][matrixId][pos] = (byte)nextCoef;
                    }
                }
            }
        }
        
        // Chroma format 4:4:4: copy size 2 matrices to size 3
        if (chromaFormatIdc == 3)
        {
            for (int i = 0; i < 64; i++)
            {
                sl.Sl[3][1][i] = sl.Sl[2][1][i];
                sl.Sl[3][2][i] = sl.Sl[2][2][i];
                sl.Sl[3][4][i] = sl.Sl[2][4][i];
                sl.Sl[3][5][i] = sl.Sl[2][5][i];
            }
            sl.SlDc[1][1] = sl.SlDc[0][1];
            sl.SlDc[1][2] = sl.SlDc[0][2];
            sl.SlDc[1][4] = sl.SlDc[0][4];
            sl.SlDc[1][5] = sl.SlDc[0][5];
        }
    }

    /// <summary>
    /// Skips over short-term reference picture set data.
    /// </summary>
    /// <summary>
    /// Parses a short_term_ref_pic_set structure, matching ffmpeg's ff_hevc_decode_short_term_rps.
    /// Populates rpsArray[stRpsIdx] with delta_poc[] (negatives first, largest-magnitude first),
    /// num_negative_pics, num_delta_pocs, and a used bitmask.
    /// </summary>
    private static bool ParseShortTermRefPicSet(
        ref BitstreamReader reader,
        HevcShortTermRps[] rpsArray,
        int stRpsIdx,
        int numShortTermRefPicSets,
        bool isSliceHeader)
    {
        var rps = rpsArray[stRpsIdx];
        rps.Used = 0;
        
        bool rpsPredictFlag = false;
        if (stRpsIdx > 0)
            rpsPredictFlag = reader.ReadBit() == 1;

        if (rpsPredictFlag)
        {
            // Inter-RPS prediction mode
            int deltaIdxMinus1 = 0;
            if (isSliceHeader)
                deltaIdxMinus1 = (int)reader.ReadExpGolombUnsigned();
            
            int refRpsIdx = stRpsIdx - deltaIdxMinus1 - 1;
            if (refRpsIdx < 0 || refRpsIdx >= stRpsIdx)
                return false;
            
            var rpsRef = rpsArray[refRpsIdx];
            
            int deltaRpsSign = (int)reader.ReadBit();
            int absDeltaRpsMinus1 = (int)reader.ReadExpGolombUnsigned();
            int deltaRps = (1 - (deltaRpsSign << 1)) * (absDeltaRpsMinus1 + 1);
            
            Span<byte> usedFlags = stackalloc byte[HevcConstants.MaxDeltaPocs];
            int k0 = 0; // count of negative delta POCs
            int k = 0;  // total count
            
            for (int j = 0; j <= rpsRef.NumDeltaPocs; j++)
            {
                int usedByCurrPicFlag = (int)reader.ReadBit();
                int useDeltaFlag = 0;
                if (usedByCurrPicFlag == 0)
                    useDeltaFlag = (int)reader.ReadBit();
                
                if (usedByCurrPicFlag != 0 || useDeltaFlag != 0)
                {
                    int deltaPoc = deltaRps + (j < rpsRef.NumDeltaPocs ? rpsRef.DeltaPoc[j] : 0);
                    rps.DeltaPoc[k] = deltaPoc;
                    usedFlags[k] = (byte)usedByCurrPicFlag;
                    if (deltaPoc < 0)
                        k0++;
                    k++;
                }
            }
            
            if (k >= HevcConstants.MaxDeltaPocs)
                return false;
            
            rps.NumDeltaPocs = k;
            rps.NumNegativePics = k0;
            
            // Sort in increasing order (smallest first)
            for (int i = 1; i < rps.NumDeltaPocs; i++)
            {
                int dpoc = rps.DeltaPoc[i];
                byte u = usedFlags[i];
                int m = i - 1;
                while (m >= 0 && rps.DeltaPoc[m] > dpoc)
                {
                    rps.DeltaPoc[m + 1] = rps.DeltaPoc[m];
                    usedFlags[m + 1] = usedFlags[m];
                    m--;
                }
                rps.DeltaPoc[m + 1] = dpoc;
                usedFlags[m + 1] = u;
            }
            
            // Flip the negative values to largest-magnitude first (most negative first)
            if (rps.NumNegativePics > 1)
            {
                int lo = 0;
                int hi = rps.NumNegativePics - 1;
                while (lo < hi)
                {
                    (rps.DeltaPoc[lo], rps.DeltaPoc[hi]) = (rps.DeltaPoc[hi], rps.DeltaPoc[lo]);
                    (usedFlags[lo], usedFlags[hi]) = (usedFlags[hi], usedFlags[lo]);
                    lo++;
                    hi--;
                }
            }
            
            // Build used bitmask
            for (int i = 0; i < rps.NumDeltaPocs; i++)
                rps.Used |= (uint)usedFlags[i] << i;
        }
        else
        {
            // Direct specification mode
            int numNegativePics = (int)reader.ReadExpGolombUnsigned();
            int numPositivePics = (int)reader.ReadExpGolombUnsigned();
            
            if (numNegativePics >= HevcConstants.MaxRefs || numPositivePics >= HevcConstants.MaxRefs)
                return false;
            
            rps.NumNegativePics = numNegativePics;
            rps.NumDeltaPocs = numNegativePics + numPositivePics;
            
            if (rps.NumDeltaPocs > 0)
            {
                int prev = 0;
                for (int i = 0; i < numNegativePics; i++)
                {
                    int deltaPocS0Minus1 = (int)reader.ReadExpGolombUnsigned();
                    prev -= (deltaPocS0Minus1 + 1);
                    rps.DeltaPoc[i] = prev;
                    if (reader.ReadBit() == 1) // used_by_curr_pic_s0_flag
                        rps.Used |= 1u << i;
                }
                
                prev = 0;
                for (int i = 0; i < numPositivePics; i++)
                {
                    int deltaPocS1Minus1 = (int)reader.ReadExpGolombUnsigned();
                    prev += (deltaPocS1Minus1 + 1);
                    rps.DeltaPoc[numNegativePics + i] = prev;
                    if (reader.ReadBit() == 1) // used_by_curr_pic_s1_flag
                        rps.Used |= 1u << (numNegativePics + i);
                }
            }
        }

        return true;
    }
}
