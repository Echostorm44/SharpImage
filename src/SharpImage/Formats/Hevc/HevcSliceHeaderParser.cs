// HEVC Slice Segment Header Parser
// Parses slice headers which reference VPS/SPS/PPS

using System;
using System.Numerics;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Parser for HEVC slice segment headers.
/// </summary>
public static class HevcSliceHeaderParser
{
    /// <summary>
    /// Parses an HEVC slice segment header from a NAL unit payload.
    /// </summary>
    /// <param name="nalPayload">The NAL payload (after 2-byte NAL header, before emulation prevention removal).</param>
    /// <param name="nalType">The NAL unit type from the NAL header.</param>
    /// <param name="pps">The active PPS referenced by this slice.</param>
    /// <param name="sps">The active SPS referenced by the PPS.</param>
    /// <returns>The parsed slice segment header, or null if parsing fails.</returns>
    public static HevcSliceSegmentHeader? ParseSliceSegmentHeader(
        ReadOnlySpan<byte> nalPayload,
        HevcNalUnitType nalType,
        HevcPictureParameterSet pps,
        HevcSequenceParameterSet sps,
        HevcSliceSegmentHeader? previousIndependentHeader = null,
        HevcVideoParameterSet? vps = null,
        byte nuhLayerId = 0,
        List<string>? diagLog = null)
    {
        if (nalPayload.Length < 1 || pps == null || sps == null)
        {
            diagLog?.Add($"SLICE GUARD: payload={nalPayload.Length}, pps={pps != null}, sps={sps != null}");
            return null;
        }

        try
        {
            // Remove emulation prevention bytes
            byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(nalPayload);
            var reader = new BitstreamReader(rbsp);
            
            var slice = new HevcSliceSegmentHeader
            {
                NalType = nalType
            };

            // first_slice_segment_in_pic_flag
            slice.FirstSliceSegmentInPicFlag = reader.ReadBit() == 1;

            // no_output_of_prior_pics_flag (only for IRAP pictures, types 16-23)
            if (IsIrapNalType(nalType))
            {
                slice.NoOutputOfPriorPicsFlag = reader.ReadBit() == 1;
            }

            // slice_pic_parameter_set_id
            slice.SlicePicParameterSetId = (byte)reader.ReadExpGolombUnsigned();
            if (slice.SlicePicParameterSetId > HevcParameterSetLimits.MaxPpsId)
            {
                diagLog?.Add($"SLICE GUARD: ppsId={slice.SlicePicParameterSetId} > max");
                return null;
            }

            // If not the first slice segment, read address
            if (!slice.FirstSliceSegmentInPicFlag)
            {
                // dependent_slice_segment_flag
                if (pps.DependentSliceSegmentsEnabled)
                {
                    slice.DependentSliceSegmentFlag = reader.ReadBit() == 1;
                }

                // slice_segment_address
                int picSizeInCtbs = sps.PicWidthInCtbsY * sps.PicHeightInCtbsY;
                int addressBits = BitOperations.Log2((uint)picSizeInCtbs - 1) + 1;
                if (addressBits > 0)
                {
                    slice.SliceSegmentAddress = (int)reader.ReadBits(addressBits);
                }

                // For dependent slices: inherit ALL fields from the previous independent
                // slice header, then just read byte_alignment and set data offset.
                // The spec says dependent slices only contain the address + CABAC data.
                if (slice.DependentSliceSegmentFlag)
                {
                    if (previousIndependentHeader == null)
                        return null; // No independent slice to inherit from

                    // Copy all relevant fields from the previous independent header
                    slice.SliceType = previousIndependentHeader.SliceType;
                    slice.PicOutputFlag = previousIndependentHeader.PicOutputFlag;
                    slice.ColourPlaneId = previousIndependentHeader.ColourPlaneId;
                    slice.PicOrderCntLsb = previousIndependentHeader.PicOrderCntLsb;
                    slice.ShortTermRps = previousIndependentHeader.ShortTermRps;
                    slice.LongTermRps = previousIndependentHeader.LongTermRps;
                    slice.SliceTemporalMvpEnabled = previousIndependentHeader.SliceTemporalMvpEnabled;
                    slice.SliceSaoLumaFlag = previousIndependentHeader.SliceSaoLumaFlag;
                    slice.SliceSaoChromaFlag = previousIndependentHeader.SliceSaoChromaFlag;
                    slice.NumRefIdxL0Active = previousIndependentHeader.NumRefIdxL0Active;
                    slice.NumRefIdxL1Active = previousIndependentHeader.NumRefIdxL1Active;
                    slice.MvdL1ZeroFlag = previousIndependentHeader.MvdL1ZeroFlag;
                    slice.CabacInitFlag = previousIndependentHeader.CabacInitFlag;
                    slice.CollocatedFromL0Flag = previousIndependentHeader.CollocatedFromL0Flag;
                    slice.CollocatedRefIdx = previousIndependentHeader.CollocatedRefIdx;
                    slice.MaxNumMergeCand = previousIndependentHeader.MaxNumMergeCand;
                    slice.SliceQpDelta = previousIndependentHeader.SliceQpDelta;
                    slice.SliceCbQpOffset = previousIndependentHeader.SliceCbQpOffset;
                    slice.SliceCrQpOffset = previousIndependentHeader.SliceCrQpOffset;
                    slice.CuChromaQpOffsetEnabled = previousIndependentHeader.CuChromaQpOffsetEnabled;
                    slice.SliceDeblockingFilterDisabled = previousIndependentHeader.SliceDeblockingFilterDisabled;
                    slice.SliceBetaOffsetDiv2 = previousIndependentHeader.SliceBetaOffsetDiv2;
                    slice.SliceTcOffsetDiv2 = previousIndependentHeader.SliceTcOffsetDiv2;
                    slice.SliceLoopFilterAcrossSlicesEnabled = previousIndependentHeader.SliceLoopFilterAcrossSlicesEnabled;
                    slice.RplModificationFlag = previousIndependentHeader.RplModificationFlag;
                    slice.ListEntryLx = previousIndependentHeader.ListEntryLx;
                    slice.NoOutputOfPriorPicsFlag = previousIndependentHeader.NoOutputOfPriorPicsFlag;
                    // Inherit the independent slice's start address for QP prediction
                    slice.SliceAddr = previousIndependentHeader.SliceAddr;
                    // Weight table is inherited from independent slice
                    slice.LumaLog2WeightDenom = previousIndependentHeader.LumaLog2WeightDenom;
                    slice.ChromaLog2WeightDenom = previousIndependentHeader.ChromaLog2WeightDenom;
                    slice.LumaWeightL0 = previousIndependentHeader.LumaWeightL0;
                    slice.LumaOffsetL0 = previousIndependentHeader.LumaOffsetL0;
                    slice.ChromaWeightL0 = previousIndependentHeader.ChromaWeightL0;
                    slice.ChromaOffsetL0 = previousIndependentHeader.ChromaOffsetL0;
                    slice.LumaWeightL1 = previousIndependentHeader.LumaWeightL1;
                    slice.LumaOffsetL1 = previousIndependentHeader.LumaOffsetL1;
                    slice.ChromaWeightL1 = previousIndependentHeader.ChromaWeightL1;
                    slice.ChromaOffsetL1 = previousIndependentHeader.ChromaOffsetL1;

                    // Entry point offsets are present in dependent slices too (HEVC spec 7.3.6.1)
                    if (pps.TilesEnabled || pps.EntropyCodingSyncEnabled)
                    {
                        uint numEpOffsets = reader.ReadExpGolombUnsigned();
                        if (numEpOffsets > 0)
                        {
                            uint epOffsetLenMinus1 = reader.ReadExpGolombUnsigned();
                            slice.EntryPointOffsets = new uint[numEpOffsets];
                            for (uint i = 0; i < numEpOffsets; i++)
                                slice.EntryPointOffsets[i] = reader.ReadBits((int)epOffsetLenMinus1 + 1) + 1;
                        }
                    }

                    // slice_segment_header_extension (HEVC spec 7.3.6.1 — outside dependent check)
                    if (pps.SliceHeaderExtensionPresent)
                    {
                        uint extLength = reader.ReadExpGolombUnsigned();
                        reader.SkipBits((int)extLength * 8);
                    }

                    // byte_alignment()
                    reader.ReadBit(); // alignment_bit_equal_to_one
                    while (reader.BitPosition % 8 != 0)
                        reader.ReadBit();

                    slice.SliceDataByteOffset = reader.BitPosition / 8;
                    return slice;
                }
            }

            // If not dependent, read slice type and related fields
            if (!slice.DependentSliceSegmentFlag)
            {
                // For independent slices, SliceAddr = SliceSegmentAddress (FFmpeg: sh->slice_addr)
                slice.SliceAddr = slice.SliceSegmentAddress;

                // Skip extra slice header bits
                if (pps.NumExtraSliceHeaderBits > 0)
                {
                    reader.SkipBits(pps.NumExtraSliceHeaderBits);
                }

                // slice_type
                uint sliceTypeValue = reader.ReadExpGolombUnsigned();
                if (sliceTypeValue > 2) // B=0, P=1, I=2
                {
                    diagLog?.Add($"SLICE GUARD: sliceType={sliceTypeValue} > 2, nalType={nalType}, extraBits={pps.NumExtraSliceHeaderBits}, nuhLayerId={nuhLayerId}, firstSlice={slice.FirstSliceSegmentInPicFlag}");
                    return null;
                }
                slice.SliceType = (HevcSliceType)sliceTypeValue;
                
                // pic_output_flag
                if (pps.OutputFlagPresent)
                {
                    slice.PicOutputFlag = reader.ReadBit() == 1;
                }
                else
                {
                    slice.PicOutputFlag = true;
                }
            }

            // colour_plane_id
            if (sps.SeparateColourPlaneFlag)
            {
                slice.ColourPlaneId = (byte)reader.ReadBits(2);
            }

            // pic_order_cnt_lsb: present for non-IDR, or for layer>0 IDR when POC LSB is not suppressed.
            // FFmpeg hevcdec.c:858-860: !IS_IDR(s) || (nuh_layer_id > 0 && !(vps->poc_lsb_not_present & (1 << layer_idx)))
            bool isIdr = nalType is HevcNalUnitType.IdrWithRadl or HevcNalUnitType.IdrNoLeadingPictures;
            int layerIdx = vps != null && nuhLayerId > 0 ? vps.LayerIdx[nuhLayerId] : 0;
            bool readPocLsb = !isIdr ||
                (nuhLayerId > 0 && vps != null && layerIdx >= 0 &&
                 (vps.PocLsbNotPresent & (1 << layerIdx)) == 0);
            
            if (readPocLsb)
            {
                int pocBits = sps.Log2MaxPicOrderCntLsbMinus4 + 4;
                slice.PicOrderCntLsb = (int)reader.ReadBits(pocBits);
            }

            // RPS: only for non-IDR (FFmpeg hevcdec.c:875-922)
            // Even layer>0 IDR does NOT have RPS — the poc_lsb is consumed above but RPS is absent.
            if (!isIdr)
            {
                // short_term_ref_pic_set_sps_flag
                uint shortTermRefPicSetSpsFlag = reader.ReadBit();
                if (shortTermRefPicSetSpsFlag == 0)
                {
                    // Parse inline short_term_ref_pic_set (stored in the extra slot at index NumShortTermRefPicSets)
                    int rpsIdx = sps.NumShortTermRefPicSets;
                    sps.ShortTermRpsList[rpsIdx] = new HevcShortTermRps();
                    ParseShortTermRefPicSetSlice(ref reader, sps, rpsIdx);
                    slice.ShortTermRps = sps.ShortTermRpsList[rpsIdx];
                }
                else
                {
                    int rpsIdx = 0;
                    if (sps.NumShortTermRefPicSets > 1)
                    {
                        int bits = CeilLog2(sps.NumShortTermRefPicSets);
                        if (bits > 0)
                            rpsIdx = (int)reader.ReadBits(bits);
                    }
                    slice.ShortTermRps = sps.ShortTermRpsList[rpsIdx];
                }
                
                // long_term_ref_pics
                if (sps.LongTermRefPicsPresent)
                {
                    var ltRps = slice.LongTermRps;
                    ltRps.NumRefs = 0;
                    
                    int numLongTermSps = 0;
                    if (sps.NumLongTermRefPicsSps > 0)
                        numLongTermSps = (int)reader.ReadExpGolombUnsigned();
                    int numLongTermPics = (int)reader.ReadExpGolombUnsigned();
                    int totalLt = numLongTermSps + numLongTermPics;
                    
                    int prevDeltaMsb = 0;
                    for (int i = 0; i < totalLt; i++)
                    {
                        int pocLsbLt;
                        bool usedByCurrPicLt;
                        
                        if (i < numLongTermSps)
                        {
                            int ltIdx = 0;
                            if (sps.NumLongTermRefPicsSps > 1)
                                ltIdx = (int)reader.ReadBits(CeilLog2(sps.NumLongTermRefPicsSps));
                            pocLsbLt = sps.LtRefPicPocLsbSps[ltIdx];
                            usedByCurrPicLt = sps.UsedByCurrPicLtSpsFlag[ltIdx];
                        }
                        else
                        {
                            pocLsbLt = (int)reader.ReadBits(sps.Log2MaxPicOrderCntLsbMinus4 + 4);
                            usedByCurrPicLt = reader.ReadBit() == 1;
                        }
                        
                        ltRps.Poc[i] = pocLsbLt;
                        ltRps.PocLsb[i] = pocLsbLt;
                        ltRps.UsedByCurrPic[i] = usedByCurrPicLt;
                        
                        bool deltaPocMsbPresentFlag = reader.ReadBit() == 1;
                        ltRps.PocMsbPresent[i] = deltaPocMsbPresentFlag;
                        if (deltaPocMsbPresentFlag)
                        {
                            int deltaPocMsbCycleLt = (int)reader.ReadExpGolombUnsigned();
                            if (i == 0 || i == numLongTermSps)
                                prevDeltaMsb = deltaPocMsbCycleLt;
                            else
                                prevDeltaMsb = deltaPocMsbCycleLt + prevDeltaMsb;
                            
                            ltRps.DeltaPocMsb[i] = prevDeltaMsb;
                        }
                        else
                        {
                            ltRps.DeltaPocMsb[i] = 0;
                        }
                        
                        ltRps.NumRefs++;
                    }
                }
                
                // slice_temporal_mvp_enabled_flag
                if (sps.SpsTemporalMvpEnabled)
                    slice.SliceTemporalMvpEnabled = reader.ReadBit() == 1;
            }
            else
            {
                // IDR: POC = 0, no RPS (FFmpeg hevcdec.c:913-922)
                slice.PicOrderCntLsb = 0;
                slice.ShortTermRps = null;
                slice.SliceTemporalMvpEnabled = false;
            }
            
            // inter_layer_pred (MV-HEVC: F.7.3.6.1, FFmpeg hevcdec.c:924-938)
            slice.InterLayerPred = false;
            if (nuhLayerId > 0 && vps != null && layerIdx >= 0)
            {
                int numDirectRefLayers = vps.NumDirectRefLayers[layerIdx];
                if (vps.DefaultRefLayersActive)
                {
                    slice.InterLayerPred = numDirectRefLayers > 0;
                }
                else if (numDirectRefLayers > 0)
                {
                    slice.InterLayerPred = reader.ReadBit() == 1;
                    // If inter_layer_pred && num_direct_ref_layers > 1: unsupported
                    // (our 2-layer assumption means max 1 direct ref layer)
                }
            }

            // slice_sao_luma_flag / slice_sao_chroma_flag
            if (sps.SampleAdaptiveOffsetEnabled)
            {
                slice.SliceSaoLumaFlag = reader.ReadBit() == 1;
                if (sps.ChromaFormatIdc != HevcChromaFormat.Monochrome)
                    slice.SliceSaoChromaFlag = reader.ReadBit() == 1;
            }

            // For P/B slices: reference picture list modification, prediction weights, etc.
            if (slice.SliceType == HevcSliceType.PSlice || slice.SliceType == HevcSliceType.BSlice)
            {
                // Default ref counts from PPS
                slice.NumRefIdxL0Active = pps.NumRefIdxL0DefaultActiveMinus1 + 1;
                slice.NumRefIdxL1Active = slice.SliceType == HevcSliceType.BSlice 
                    ? pps.NumRefIdxL1DefaultActiveMinus1 + 1 : 0;
                
                // num_ref_idx_active_override_flag
                bool numRefIdxActiveOverrideFlag = reader.ReadBit() == 1;
                if (numRefIdxActiveOverrideFlag)
                {
                    slice.NumRefIdxL0Active = (int)reader.ReadExpGolombUnsigned() + 1;
                    if (slice.SliceType == HevcSliceType.BSlice)
                        slice.NumRefIdxL1Active = (int)reader.ReadExpGolombUnsigned() + 1;
                }
                
                // ref_pic_lists_modification
                int numPocTotalCurr = CountPocTotalCurr(slice);
                if (pps.ListsModificationPresent && numPocTotalCurr > 1)
                {
                    ParseRefPicListsModification(ref reader, slice, pps, sps, numPocTotalCurr);
                }
                
                // mvd_l1_zero_flag (B slices only)
                if (slice.SliceType == HevcSliceType.BSlice)
                    slice.MvdL1ZeroFlag = reader.ReadBit() == 1;
                
                // cabac_init_flag
                if (pps.CabacInitPresent)
                    slice.CabacInitFlag = reader.ReadBit() == 1;
                
                // collocated info
                if (slice.SliceTemporalMvpEnabled)
                {
                    slice.CollocatedFromL0Flag = true;
                    if (slice.SliceType == HevcSliceType.BSlice)
                        slice.CollocatedFromL0Flag = reader.ReadBit() == 1;
                    
                    // collocated_ref_idx is only present when there are multiple refs in the collocated list
                    int collocatedListRefs = slice.CollocatedFromL0Flag 
                        ? slice.NumRefIdxL0Active : slice.NumRefIdxL1Active;
                    if (collocatedListRefs > 1)
                        slice.CollocatedRefIdx = (int)reader.ReadExpGolombUnsigned();
                    else
                        slice.CollocatedRefIdx = 0;
                }
                
                // pred_weight_table
                if ((pps.WeightedPred && slice.SliceType == HevcSliceType.PSlice) ||
                    (pps.WeightedBipred && slice.SliceType == HevcSliceType.BSlice))
                {
                    ParsePredWeightTable(ref reader, slice, sps);
                }
                
                // five_minus_max_num_merge_cand
                slice.MaxNumMergeCand = 5 - (int)reader.ReadExpGolombUnsigned();
            }

            // slice_qp_delta
            slice.SliceQpDelta = reader.ReadExpGolombSigned();
            
            // slice_cb_qp_offset / slice_cr_qp_offset
            if (pps.PicSliceLevelChromaQpOffsetsPresent)
            {
                slice.SliceCbQpOffset = reader.ReadExpGolombSigned();
                slice.SliceCrQpOffset = reader.ReadExpGolombSigned();
            }
            
            // cu_chroma_qp_offset_enabled_flag (RExt: per-CU chroma QP offset selection)
            if (pps.ChromaQpOffsetListEnabled)
            {
                slice.CuChromaQpOffsetEnabled = reader.ReadBit() == 1;
            }
            
            // deblocking filter — default to PPS values, override if slice says so
            slice.SliceDeblockingFilterDisabled = pps.DeblockingFilterDisabled;
            slice.SliceLoopFilterAcrossSlicesEnabled = pps.LoopFilterAcrossSlicesEnabled;
            slice.SliceBetaOffsetDiv2 = pps.BetaOffsetDiv2;
            slice.SliceTcOffsetDiv2 = pps.TcOffsetDiv2;
            
            if (pps.DeblockingFilterControlPresent)
            {
                if (pps.DeblockingFilterOverrideEnabled)
                {
                    bool overrideFlag = reader.ReadBit() == 1;
                    if (overrideFlag)
                    {
                        slice.SliceDeblockingFilterDisabled = reader.ReadBit() == 1;
                        if (!slice.SliceDeblockingFilterDisabled)
                        {
                            slice.SliceBetaOffsetDiv2 = reader.ReadExpGolombSigned();
                            slice.SliceTcOffsetDiv2 = reader.ReadExpGolombSigned();
                        }
                    }
                }
            }
            
            // HEVC spec 7.3.6.1: slice_loop_filter_across_slices_enabled_flag is
            // OUTSIDE the deblocking_filter_control_present block
            if (pps.LoopFilterAcrossSlicesEnabled &&
                (slice.SliceSaoLumaFlag || slice.SliceSaoChromaFlag ||
                 !slice.SliceDeblockingFilterDisabled))
            {
                slice.SliceLoopFilterAcrossSlicesEnabled = reader.ReadBit() == 1;
            }
            
            // entry_point_offsets
            if (pps.TilesEnabled || pps.EntropyCodingSyncEnabled)
            {
                uint numEntryPointOffsets = reader.ReadExpGolombUnsigned();
                if (numEntryPointOffsets > 0)
                {
                    uint offsetLenMinus1 = reader.ReadExpGolombUnsigned();
                    slice.EntryPointOffsets = new uint[numEntryPointOffsets];
                    for (uint i = 0; i < numEntryPointOffsets; i++)
                        slice.EntryPointOffsets[i] = reader.ReadBits((int)offsetLenMinus1 + 1) + 1;
                }
            }
            
            // slice_segment_header_extension
            if (pps.SliceHeaderExtensionPresent)
            {
                uint extLength = reader.ReadExpGolombUnsigned();
                reader.SkipBits((int)extLength * 8);
            }
            
            // byte_alignment()
            reader.ReadBit(); // alignment_bit_equal_to_one (always 1)
            while (reader.BitPosition % 8 != 0)
                reader.ReadBit(); // alignment_bit_equal_to_zero
            
            // Record where CABAC slice data begins
            slice.SliceDataByteOffset = reader.BitPosition / 8;

            return slice;
        }
        catch (Exception ex)
        {
            diagLog?.Add($"SLICE PARSE EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Quickly peeks the PPS ID from a slice NAL unit without full parsing.
    /// </summary>
    public static byte? PeekSlicePpsId(ReadOnlySpan<byte> nalPayload, HevcNalUnitType nalType = HevcNalUnitType.TrailingReference)
    {
        if (nalPayload.Length < 1)
            return null;

        try
        {
            byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(nalPayload);
            var reader = new BitstreamReader(rbsp);

            // Skip first_slice_segment_in_pic_flag
            reader.ReadBit();

            // IRAP NAL types (BLA, IDR, CRA) have no_output_of_prior_pics_flag before PPS ID
            if (IsIrapNalType(nalType))
                reader.ReadBit();

            uint ppsId = reader.ReadExpGolombUnsigned();
            if (ppsId > HevcParameterSetLimits.MaxPpsId)
                return null;

            return (byte)ppsId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a NAL type is an IRAP (Intra Random Access Point).
    /// </summary>
    private static bool IsIrapNalType(HevcNalUnitType type)
    {
        byte t = (byte)type;
        return t >= 16 && t <= 23;
    }

    /// <summary>
    /// Computes ceil(log2(x)) for x > 0. Returns 0 for x <= 1.
    /// </summary>
    private static int CeilLog2(int x)
    {
        if (x <= 1) return 0;
        return BitOperations.Log2((uint)(x - 1)) + 1;
    }

    /// <summary>
    /// Parses a short_term_ref_pic_set that appears inline in the slice header.
    /// The SPS rpsArray must already have the SPS-level RPS sets parsed.
    /// </summary>
    private static void ParseShortTermRefPicSetSlice(ref BitstreamReader reader, HevcSequenceParameterSet sps, int stRpsIdx)
    {
        var rps = sps.ShortTermRpsList[stRpsIdx];
        rps.Used = 0;
        
        bool rpsPredictFlag = false;
        if (stRpsIdx > 0)
            rpsPredictFlag = reader.ReadBit() == 1;

        if (rpsPredictFlag)
        {
            int deltaIdxMinus1 = (int)reader.ReadExpGolombUnsigned();
            int refRpsIdx = stRpsIdx - deltaIdxMinus1 - 1;
            var rpsRef = sps.ShortTermRpsList[refRpsIdx];
            
            int deltaRpsSign = (int)reader.ReadBit();
            int absDeltaRpsMinus1 = (int)reader.ReadExpGolombUnsigned();
            int deltaRps = (1 - (deltaRpsSign << 1)) * (absDeltaRpsMinus1 + 1);
            
            Span<byte> usedFlags = stackalloc byte[HevcConstants.MaxDeltaPocs];
            int k0 = 0;
            int k = 0;
            
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
            
            rps.NumDeltaPocs = k;
            rps.NumNegativePics = k0;
            
            // Sort increasing, then flip negatives to largest-magnitude first
            for (int i = 1; i < k; i++)
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
            
            if (k0 > 1)
            {
                int lo = 0, hi = k0 - 1;
                while (lo < hi)
                {
                    (rps.DeltaPoc[lo], rps.DeltaPoc[hi]) = (rps.DeltaPoc[hi], rps.DeltaPoc[lo]);
                    (usedFlags[lo], usedFlags[hi]) = (usedFlags[hi], usedFlags[lo]);
                    lo++; hi--;
                }
            }
            
            for (int i = 0; i < k; i++)
                rps.Used |= (uint)usedFlags[i] << i;
        }
        else
        {
            int numNeg = (int)reader.ReadExpGolombUnsigned();
            int numPos = (int)reader.ReadExpGolombUnsigned();
            rps.NumNegativePics = numNeg;
            rps.NumDeltaPocs = numNeg + numPos;
            
            int prev = 0;
            for (int i = 0; i < numNeg; i++)
            {
                prev -= ((int)reader.ReadExpGolombUnsigned() + 1);
                rps.DeltaPoc[i] = prev;
                if (reader.ReadBit() == 1) rps.Used |= 1u << i;
            }
            prev = 0;
            for (int i = 0; i < numPos; i++)
            {
                prev += ((int)reader.ReadExpGolombUnsigned() + 1);
                rps.DeltaPoc[numNeg + i] = prev;
                if (reader.ReadBit() == 1) rps.Used |= 1u << (numNeg + i);
            }
        }
    }

    /// <summary>
    /// Parses ref_pic_lists_modification. Stores reorder indices for L0/L1.
    /// </summary>
    private static void ParseRefPicListsModification(ref BitstreamReader reader, HevcSliceSegmentHeader slice, HevcPictureParameterSet pps, HevcSequenceParameterSet sps, int numPocTotalCurr)
    {
        int bits = CeilLog2(numPocTotalCurr);
        
        // ref_pic_list_modification_flag_l0
        slice.RplModificationFlag[0] = reader.ReadBit() == 1;
        if (slice.RplModificationFlag[0])
        {
            slice.ListEntryLx[0] = new int[slice.NumRefIdxL0Active];
            for (int i = 0; i < slice.NumRefIdxL0Active; i++)
                slice.ListEntryLx[0][i] = bits > 0 ? (int)reader.ReadBits(bits) : 0;
        }
        
        if (slice.SliceType == HevcSliceType.BSlice)
        {
            slice.RplModificationFlag[1] = reader.ReadBit() == 1;
            if (slice.RplModificationFlag[1])
            {
                slice.ListEntryLx[1] = new int[slice.NumRefIdxL1Active];
                for (int i = 0; i < slice.NumRefIdxL1Active; i++)
                    slice.ListEntryLx[1][i] = bits > 0 ? (int)reader.ReadBits(bits) : 0;
            }
        }
    }

    /// <summary>
    /// Counts NumPocTotalCurr — total reference pictures used by the current picture.
    /// For MV-HEVC, includes inter-layer references (FFmpeg refs.c:632-634).
    /// </summary>
    private static int CountPocTotalCurr(HevcSliceSegmentHeader slice)
    {
        int count = 0;
        var rps = slice.ShortTermRps;
        if (rps != null)
        {
            for (int i = 0; i < rps.NumDeltaPocs; i++)
            {
                if ((rps.Used & (1u << i)) != 0)
                    count++;
            }
        }
        
        var ltRps = slice.LongTermRps;
        for (int i = 0; i < ltRps.NumRefs; i++)
        {
            if (ltRps.UsedByCurrPic[i])
                count++;
        }
        
        // MV-HEVC: inter-layer ref adds one candidate (F.8.1.6, FFmpeg refs.c:632-634)
        if (slice.InterLayerPred)
            count++;
        
        return count;
    }

    /// <summary>
    /// Parses pred_weight_table from the bitstream and stores weights/offsets in the slice header.
    /// Matches FFmpeg's pred_weight_table() in hevcdec.c.
    /// </summary>
    private static void ParsePredWeightTable(ref BitstreamReader reader, HevcSliceSegmentHeader slice, HevcSequenceParameterSet sps)
    {
        int numRefL0 = slice.NumRefIdxL0Active;
        bool hasChroma = sps.ChromaFormatIdc != HevcChromaFormat.Monochrome;

        // luma_log2_weight_denom (0..7)
        int lumaLog2WeightDenom = (int)reader.ReadExpGolombUnsigned();
        lumaLog2WeightDenom = Math.Clamp(lumaLog2WeightDenom, 0, 7);
        slice.LumaLog2WeightDenom = lumaLog2WeightDenom;

        // delta_chroma_log2_weight_denom
        int chromaLog2WeightDenom = lumaLog2WeightDenom;
        if (hasChroma)
        {
            chromaLog2WeightDenom = lumaLog2WeightDenom + reader.ReadExpGolombSigned();
            chromaLog2WeightDenom = Math.Clamp(chromaLog2WeightDenom, 0, 7);
        }
        slice.ChromaLog2WeightDenom = chromaLog2WeightDenom;

        // Allocate L0 arrays
        slice.LumaWeightL0 = new short[numRefL0];
        slice.LumaOffsetL0 = new short[numRefL0];
        slice.ChromaWeightL0 = new short[numRefL0, 2];
        slice.ChromaOffsetL0 = new short[numRefL0, 2];

        // Read L0 flags
        Span<bool> lumaWeightL0Flag = stackalloc bool[numRefL0];
        Span<bool> chromaWeightL0Flag = stackalloc bool[numRefL0];
        for (int i = 0; i < numRefL0; i++)
            lumaWeightL0Flag[i] = reader.ReadBit() == 1;
        if (hasChroma)
            for (int i = 0; i < numRefL0; i++)
                chromaWeightL0Flag[i] = reader.ReadBit() == 1;

        // Parse L0 weights and offsets
        for (int i = 0; i < numRefL0; i++)
        {
            if (lumaWeightL0Flag[i])
            {
                int deltaWeight = reader.ReadExpGolombSigned();
                slice.LumaWeightL0[i] = (short)((1 << lumaLog2WeightDenom) + deltaWeight);
                slice.LumaOffsetL0[i] = (short)reader.ReadExpGolombSigned();
            }
            else
            {
                slice.LumaWeightL0[i] = (short)(1 << lumaLog2WeightDenom);
                slice.LumaOffsetL0[i] = 0;
            }

            if (chromaWeightL0Flag[i])
            {
                for (int j = 0; j < 2; j++)
                {
                    int deltaChromaWeight = reader.ReadExpGolombSigned();
                    int deltaChromaOffset = reader.ReadExpGolombSigned();
                    int weight = (1 << chromaLog2WeightDenom) + deltaChromaWeight;
                    slice.ChromaWeightL0[i, j] = (short)weight;
                    slice.ChromaOffsetL0[i, j] = (short)Math.Clamp(
                        deltaChromaOffset - ((128 * weight) >> chromaLog2WeightDenom) + 128,
                        -128, 127);
                }
            }
            else
            {
                for (int j = 0; j < 2; j++)
                {
                    slice.ChromaWeightL0[i, j] = (short)(1 << chromaLog2WeightDenom);
                    slice.ChromaOffsetL0[i, j] = 0;
                }
            }
        }

        // L1 for B-slices
        if (slice.SliceType == HevcSliceType.BSlice)
        {
            int numRefL1 = slice.NumRefIdxL1Active;
            slice.LumaWeightL1 = new short[numRefL1];
            slice.LumaOffsetL1 = new short[numRefL1];
            slice.ChromaWeightL1 = new short[numRefL1, 2];
            slice.ChromaOffsetL1 = new short[numRefL1, 2];

            Span<bool> lumaWeightL1Flag = stackalloc bool[numRefL1];
            Span<bool> chromaWeightL1Flag = stackalloc bool[numRefL1];
            for (int i = 0; i < numRefL1; i++)
                lumaWeightL1Flag[i] = reader.ReadBit() == 1;
            if (hasChroma)
                for (int i = 0; i < numRefL1; i++)
                    chromaWeightL1Flag[i] = reader.ReadBit() == 1;

            for (int i = 0; i < numRefL1; i++)
            {
                if (lumaWeightL1Flag[i])
                {
                    int deltaWeight = reader.ReadExpGolombSigned();
                    slice.LumaWeightL1[i] = (short)((1 << lumaLog2WeightDenom) + deltaWeight);
                    slice.LumaOffsetL1[i] = (short)reader.ReadExpGolombSigned();
                }
                else
                {
                    slice.LumaWeightL1[i] = (short)(1 << lumaLog2WeightDenom);
                    slice.LumaOffsetL1[i] = 0;
                }

                if (chromaWeightL1Flag[i])
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int deltaChromaWeight = reader.ReadExpGolombSigned();
                        int deltaChromaOffset = reader.ReadExpGolombSigned();
                        int weight = (1 << chromaLog2WeightDenom) + deltaChromaWeight;
                        slice.ChromaWeightL1[i, j] = (short)weight;
                        slice.ChromaOffsetL1[i, j] = (short)Math.Clamp(
                            deltaChromaOffset - ((128 * weight) >> chromaLog2WeightDenom) + 128,
                            -128, 127);
                    }
                }
                else
                {
                    for (int j = 0; j < 2; j++)
                    {
                        slice.ChromaWeightL1[i, j] = (short)(1 << chromaLog2WeightDenom);
                        slice.ChromaOffsetL1[i, j] = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates a slice segment header directly from NAL unit data including NAL header.
    /// </summary>
    public static HevcSliceSegmentHeader? ParseFromNalUnit(
        HevcNalUnit nalUnit,
        HevcPictureParameterSet pps,
        HevcSequenceParameterSet sps,
        HevcSliceSegmentHeader? previousIndependentHeader = null)
    {
        if (!nalUnit.IsVcl)
            return null;

        var slice = ParseSliceSegmentHeader(nalUnit.Payload.Span, nalUnit.Type, pps, sps, previousIndependentHeader);
        if (slice != null)
        {
            slice.NuhLayerId = nalUnit.LayerId;
            slice.TemporalIdPlus1 = nalUnit.TemporalIdPlus1;
        }
        return slice;
    }
}
