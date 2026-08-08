// HEVC Parameter Sets (VPS, SPS, PPS) - structures and parsing
// Ported from VLC's hevc_nal.c with improvements for C# idioms

using System;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Maximum sublayer count in HEVC.
/// </summary>
public static class HevcConstants
{
    public const int MaxSublayers = 8;
    public const int MaxShortTermRefPicSets = 65;
    public const int MaxLongTermRefPicSets = 33;
    public const int MaxRefs = 16;
    public const int MaxDeltaPocs = 32;
    
    /// <summary>Maximum supported layers in VPS (FFmpeg limits to 2 for stereoscopic MV-HEVC).</summary>
    public const int VpsMaxLayers = 2;
    
    /// <summary>Maximum nuh_layer_id value (6-bit field, 0-62).</summary>
    public const int MaxNuhLayerId = 62;
}

/// <summary>
/// Scalability mask flags for VPS multi-layer extension.
/// Bit positions are 15 minus the scalability type index.
/// </summary>
[Flags]
public enum HevcScalabilityMask : ushort
{
    None       = 0,
    Depth      = 1 << (15 - 0),   // 0x8000
    Multiview  = 1 << (15 - 1),   // 0x4000
    Spatial    = 1 << (15 - 2),   // 0x2000
    Auxiliary  = 1 << (15 - 3),   // 0x1000
}

/// <summary>
/// Representation format for VPS multi-layer extension.
/// Describes the spatial resolution, chroma format, and bit depth shared across layers.
/// </summary>
public struct HevcRepFormat
{
    public ushort PicWidthInLumaSamples;
    public ushort PicHeightInLumaSamples;
    public byte ChromaFormatIdc;
    public bool SeparateColourPlaneFlag;
    public byte BitDepthLuma;     // bit_depth_vps_luma_minus8 + 8
    public byte BitDepthChroma;   // bit_depth_vps_chroma_minus8 + 8
    public ushort ConfWinLeftOffset;
    public ushort ConfWinRightOffset;
    public ushort ConfWinTopOffset;
    public ushort ConfWinBottomOffset;
}

/// <summary>
/// DPB sizing information from VPS multi-layer extension.
/// </summary>
public struct HevcVpsDpbSize
{
    public int MaxDecPicBuffering;  // max_vps_dec_pic_buffering_minus1 + 1
    public int MaxNumReorderPics;   // max_vps_num_reorder_pics
    public int MaxLatencyIncrease;  // max_vps_latency_increase_plus1 - 1
}

/// <summary>
/// HEVC scaling list data for custom quantization matrices.
/// Matches FFmpeg's ScalingList struct: sl[4][6][64] + sl_dc[2][6].
/// Size IDs: 0=4×4 (16 coeffs), 1=8×8, 2=16×16, 3=32×32 (all 64 coeffs for 1-3).
/// Matrix IDs: 0-2 = intra Y/Cb/Cr, 3-5 = inter Y/Cb/Cr.
/// Size 3 only has 2 matrices (intra Y = matrix 0, inter Y = matrix 3).
/// </summary>
public sealed class HevcScalingList
{
    /// <summary>
    /// Scaling list coefficients: [sizeId][matrixId][position].
    /// sizeId: 0=4×4, 1=8×8, 2=16×16, 3=32×32.
    /// matrixId: 0=intraY, 1=intraCb, 2=intraCr, 3=interY, 4=interCb, 5=interCr.
    /// position: up to 64 coefficients (16 for sizeId=0).
    /// </summary>
    public readonly byte[][][] Sl;

    /// <summary>
    /// DC coefficients for 16×16 and 32×32 matrices: [sizeId-2][matrixId].
    /// Index 0 = sizeId 2 (16×16), index 1 = sizeId 3 (32×32).
    /// </summary>
    public readonly byte[][] SlDc;

    public HevcScalingList()
    {
        Sl = new byte[4][][];
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            Sl[sizeId] = new byte[6][];
            for (int matrixId = 0; matrixId < 6; matrixId++)
                Sl[sizeId][matrixId] = new byte[64];
        }
        SlDc = new byte[2][];
        SlDc[0] = new byte[6];
        SlDc[1] = new byte[6];
    }

    /// <summary>Default 8×8 intra scaling list (HEVC spec Table 7-5).</summary>
    public static ReadOnlySpan<byte> DefaultIntra => new byte[]
    {
        16, 16, 16, 16, 17, 18, 21, 24,
        16, 16, 16, 16, 17, 19, 22, 25,
        16, 16, 17, 18, 20, 22, 25, 29,
        16, 16, 18, 21, 24, 27, 31, 36,
        17, 17, 20, 24, 30, 35, 41, 47,
        18, 19, 22, 27, 35, 44, 54, 65,
        21, 22, 25, 31, 41, 54, 70, 88,
        24, 25, 29, 36, 47, 65, 88, 115
    };

    /// <summary>Default 8×8 inter scaling list (HEVC spec Table 7-6).</summary>
    public static ReadOnlySpan<byte> DefaultInter => new byte[]
    {
        16, 16, 16, 16, 17, 18, 20, 24,
        16, 16, 16, 17, 18, 20, 24, 25,
        16, 16, 17, 18, 20, 24, 25, 28,
        16, 17, 18, 20, 24, 25, 28, 33,
        17, 18, 20, 24, 25, 28, 33, 41,
        18, 20, 24, 25, 28, 33, 41, 54,
        20, 24, 25, 28, 33, 41, 54, 71,
        24, 25, 28, 33, 41, 54, 71, 91
    };

    /// <summary>4×4 diagonal scan X coordinates for scaling list parsing.</summary>
    public static ReadOnlySpan<byte> DiagScan4x4X => new byte[]
    {
        0, 0, 1, 0, 1, 2, 0, 1, 2, 3, 1, 2, 3, 2, 3, 3
    };

    /// <summary>4×4 diagonal scan Y coordinates for scaling list parsing.</summary>
    public static ReadOnlySpan<byte> DiagScan4x4Y => new byte[]
    {
        0, 1, 0, 2, 1, 0, 3, 2, 1, 0, 3, 2, 1, 3, 2, 3
    };

    /// <summary>8×8 diagonal scan X coordinates for scaling list parsing.</summary>
    public static ReadOnlySpan<byte> DiagScan8x8X => new byte[]
    {
        0, 0, 1, 0, 1, 2, 0, 1, 2, 3, 0, 1, 2, 3, 4, 0,
        1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3,
        4, 5, 6, 7, 1, 2, 3, 4, 5, 6, 7, 2, 3, 4, 5, 6,
        7, 3, 4, 5, 6, 7, 4, 5, 6, 7, 5, 6, 7, 6, 7, 7
    };

    /// <summary>8×8 diagonal scan Y coordinates for scaling list parsing.</summary>
    public static ReadOnlySpan<byte> DiagScan8x8Y => new byte[]
    {
        0, 1, 0, 2, 1, 0, 3, 2, 1, 0, 4, 3, 2, 1, 0, 5,
        4, 3, 2, 1, 0, 6, 5, 4, 3, 2, 1, 0, 7, 6, 5, 4,
        3, 2, 1, 0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3,
        2, 7, 6, 5, 4, 3, 7, 6, 5, 4, 7, 6, 5, 7, 6, 7
    };

    /// <summary>
    /// Fills this scaling list with the HEVC default values.
    /// Matches FFmpeg's set_default_scaling_list_data().
    /// </summary>
    public void SetDefaults()
    {
        // Size 0 (4×4): all flat 16
        for (int matrixId = 0; matrixId < 6; matrixId++)
            Array.Fill(Sl[0][matrixId], (byte)16);

        // Size 1 (8×8): intra = default_intra, inter = default_inter
        for (int matrixId = 0; matrixId < 3; matrixId++)
            DefaultIntra.CopyTo(Sl[1][matrixId]);
        for (int matrixId = 3; matrixId < 6; matrixId++)
            DefaultInter.CopyTo(Sl[1][matrixId]);

        // Size 2 (16×16): same as size 1
        for (int matrixId = 0; matrixId < 3; matrixId++)
            DefaultIntra.CopyTo(Sl[2][matrixId]);
        for (int matrixId = 3; matrixId < 6; matrixId++)
            DefaultInter.CopyTo(Sl[2][matrixId]);

        // Size 3 (32×32): only matrix 0 (intra Y) and 3 (inter Y) used
        DefaultIntra.CopyTo(Sl[3][0]);
        DefaultInter.CopyTo(Sl[3][3]);
        // Copy to unused slots for completeness
        DefaultIntra.CopyTo(Sl[3][1]);
        DefaultIntra.CopyTo(Sl[3][2]);
        DefaultInter.CopyTo(Sl[3][4]);
        DefaultInter.CopyTo(Sl[3][5]);

        // DC coefficients default to 16
        Array.Fill(SlDc[0], (byte)16);
        Array.Fill(SlDc[1], (byte)16);
    }
}

/// <summary>
/// Short-term reference picture set, matching ffmpeg's ShortTermRPS.
/// delta_poc[] stores POC offsets relative to the current picture.
/// Negative entries come first (num_negative_pics count), then positive.
/// The 'used' bitmask indicates which entries are used by the current picture.
/// </summary>
public sealed class HevcShortTermRps
{
    /// <summary>Delta POC values relative to current picture. Negatives first, then positives.</summary>
    public readonly int[] DeltaPoc = new int[HevcConstants.MaxDeltaPocs];
    
    /// <summary>Bitmask: bit i set means DeltaPoc[i] is used by current picture.</summary>
    public uint Used;
    
    /// <summary>Number of entries with negative delta POC.</summary>
    public int NumNegativePics;
    
    /// <summary>Total number of delta POC entries (negative + positive).</summary>
    public int NumDeltaPocs;
}

/// <summary>
/// Long-term reference picture set, parsed from slice header.
/// </summary>
public sealed class HevcLongTermRps
{
    public readonly int[] Poc = new int[32];
    public readonly bool[] UsedByCurrPic = new bool[32];
    public readonly bool[] PocMsbPresent = new bool[32];
    /// <summary>Raw POC LSB before MSB adjustment (needed for decoder-side POC fixup).</summary>
    public readonly int[] PocLsb = new int[32];
    /// <summary>Accumulated delta_poc_msb_cycle per entry (needed for decoder-side POC fixup).</summary>
    public readonly int[] DeltaPocMsb = new int[32];
    public int NumRefs;
}

/// <summary>
/// Profile/Tier/Level information for HEVC.
/// </summary>
public readonly struct HevcProfileTierLevel
{
    public byte GeneralProfileSpace { get; init; }
    public bool GeneralTierFlag { get; init; }
    public HevcProfile GeneralProfileIdc { get; init; }
    public uint GeneralProfileCompatibilityFlags { get; init; }
    public bool GeneralProgressiveSourceFlag { get; init; }
    public bool GeneralInterlacedSourceFlag { get; init; }
    public bool GeneralNonPackedConstraintFlag { get; init; }
    public bool GeneralFrameOnlyConstraintFlag { get; init; }
    public HevcLevel GeneralLevelIdc { get; init; }
    
    // Constraint flags for range extensions (profiles 4-7)
    public bool Max12BitConstraintFlag { get; init; }
    public bool Max10BitConstraintFlag { get; init; }
    public bool Max8BitConstraintFlag { get; init; }
    public bool Max422ChromaConstraintFlag { get; init; }
    public bool Max420ChromaConstraintFlag { get; init; }
    public bool MaxMonochromeConstraintFlag { get; init; }
    public bool IntraConstraintFlag { get; init; }
    public bool OnePictureOnlyConstraintFlag { get; init; }
    public bool LowerBitRateConstraintFlag { get; init; }
    public bool Max14BitConstraintFlag { get; init; }
    
    /// <summary>Returns the tier as "Main" or "High".</summary>
    public string TierName => GeneralTierFlag ? "High" : "Main";
}

/// <summary>
/// Video Usability Information for HEVC.
/// </summary>
public sealed class HevcVideoUsabilityInfo
{
    public bool AspectRatioInfoPresent { get; set; }
    public byte AspectRatioIdc { get; set; }
    public ushort SarWidth { get; set; }
    public ushort SarHeight { get; set; }
    
    public bool OverscanInfoPresent { get; set; }
    public bool OverscanAppropriate { get; set; }
    
    public bool VideoSignalTypePresent { get; set; }
    public byte VideoFormat { get; set; }
    public bool VideoFullRangeFlag { get; set; }
    public bool ColourDescriptionPresent { get; set; }
    public byte ColourPrimaries { get; set; }
    public byte TransferCharacteristics { get; set; }
    public byte MatrixCoefficients { get; set; }
    
    public bool ChromaLocInfoPresent { get; set; }
    public int ChromaSampleLocTypeTopField { get; set; }
    public int ChromaSampleLocTypeBottomField { get; set; }
    
    public bool NeutralChromaIndicationFlag { get; set; }
    public bool FieldSeqFlag { get; set; }
    public bool FrameFieldInfoPresentFlag { get; set; }
    
    public bool DefaultDisplayWindowFlag { get; set; }
    public int DefDispWinLeftOffset { get; set; }
    public int DefDispWinRightOffset { get; set; }
    public int DefDispWinTopOffset { get; set; }
    public int DefDispWinBottomOffset { get; set; }
    
    public bool TimingInfoPresent { get; set; }
    public uint NumUnitsInTick { get; set; }
    public uint TimeScale { get; set; }
    public bool PocProportionalToTimingFlag { get; set; }
    public int NumTicksPocDiffOneMinus1 { get; set; }
    public bool HrdParametersPresent { get; set; }
    
    public bool BitstreamRestrictionFlag { get; set; }
    public bool TilesFixedStructureFlag { get; set; }
    public bool MotionVectorsOverPicBoundariesFlag { get; set; }
    public bool RestrictedRefPicListsFlag { get; set; }
    public int MinSpatialSegmentationIdc { get; set; }
    public int MaxBytesPerPicDenom { get; set; }
    public int MaxBitsPerMinCuDenom { get; set; }
    public int Log2MaxMvLengthHorizontal { get; set; }
    public int Log2MaxMvLengthVertical { get; set; }
    
    /// <summary>Calculates frame rate from timing info.</summary>
    public double FrameRate => TimingInfoPresent && NumUnitsInTick > 0 
        ? (double)TimeScale / NumUnitsInTick / 2.0 // Divide by 2 for field rate to frame rate
        : 0;
}

/// <summary>
/// HEVC Video Parameter Set (VPS) - new in HEVC, not present in H.264.
/// Contains sequence-level parameters that can be shared across multiple sequences.
/// </summary>
public sealed class HevcVideoParameterSet
{
    /// <summary>VPS ID (0-15).</summary>
    public byte VideoParameterSetId { get; set; }
    
    public bool BaseLayerInternalFlag { get; set; }
    public bool BaseLayerAvailableFlag { get; set; }
    
    /// <summary>Maximum number of layers minus 1 (0-63).</summary>
    public byte MaxLayersMinus1 { get; set; }
    
    /// <summary>Maximum number of temporal sublayers minus 1 (0-6).</summary>
    public byte MaxSubLayersMinus1 { get; set; }
    
    public bool TemporalIdNestingFlag { get; set; }
    
    public HevcProfileTierLevel ProfileTierLevel { get; set; }
    
    public bool SubLayerOrderingInfoPresent { get; set; }
    
    /// <summary>Maximum decoded picture buffer size minus 1 per sublayer.</summary>
    public int[] MaxDecPicBufferingMinus1 { get; set; } = new int[HevcConstants.MaxSublayers];
    
    /// <summary>Maximum number of reorder pictures per sublayer.</summary>
    public int[] MaxNumReorderPics { get; set; } = new int[HevcConstants.MaxSublayers];
    
    /// <summary>Maximum latency increase plus 1 per sublayer.</summary>
    public int[] MaxLatencyIncreasePlus1 { get; set; } = new int[HevcConstants.MaxSublayers];
    
    public byte MaxLayerId { get; set; }
    public int NumLayerSetsMinus1 { get; set; }
    
    public bool TimingInfoPresent { get; set; }
    public uint NumUnitsInTick { get; set; }
    public uint TimeScale { get; set; }
    
    /// <summary>Calculates frame rate from timing info.</summary>
    public double FrameRate => TimingInfoPresent && NumUnitsInTick > 0
        ? (double)TimeScale / NumUnitsInTick
        : 0;
    
    // --- VPS Multi-Layer Extension ---
    
    /// <summary>
    /// Number of layers parsed from VPS extension (1 or 2).
    /// Set to 1 for single-layer streams or when extension is not present.
    /// Set to 2 for stereoscopic MV-HEVC.
    /// </summary>
    public int NbLayers { get; set; } = 1;
    
    /// <summary>Scalability mask from VPS extension.</summary>
    public HevcScalabilityMask ScalabilityMaskFlag { get; set; }
    
    /// <summary>
    /// Maps nuh_layer_id → VPS layer index (0 or 1).
    /// Entries for unmapped nuh_layer_id values are -1.
    /// </summary>
    public sbyte[] LayerIdx { get; set; } = InitLayerIdx();
    
    /// <summary>Maps VPS layer index → nuh_layer_id.</summary>
    public byte[] LayerIdInNuh { get; set; } = new byte[HevcConstants.VpsMaxLayers];
    
    /// <summary>View IDs per layer index.</summary>
    public ushort[] ViewId { get; set; } = new ushort[HevcConstants.VpsMaxLayers];
    
    /// <summary>Number of direct reference layers per layer index.</summary>
    public byte[] NumDirectRefLayers { get; set; } = new byte[HevcConstants.VpsMaxLayers];
    
    /// <summary>Number of additional layer sets (usually 0).</summary>
    public int NumAddLayerSets { get; set; }
    
    /// <summary>Number of output layer sets.</summary>
    public int NumOutputLayerSets { get; set; } = 1;
    
    /// <summary>
    /// Output layer set bitmasks. Bit i set means layer with VPS index i is in the set.
    /// </summary>
    public ulong[] Ols { get; set; } = new ulong[HevcConstants.VpsMaxLayers];
    
    /// <summary>When true, inter-layer prediction is enabled by default.</summary>
    public bool DefaultRefLayersActive { get; set; }
    
    /// <summary>When true, at most one reference layer can be active.</summary>
    public bool MaxOneActiveRefLayer { get; set; }
    
    /// <summary>When true, POC LSB values are aligned across layers.</summary>
    public bool PocLsbAligned { get; set; }
    
    /// <summary>
    /// Bitmask: bit i set means POC LSB is not present for layer index i.
    /// </summary>
    public byte PocLsbNotPresent { get; set; }
    
    /// <summary>Representation format shared by all layers.</summary>
    public HevcRepFormat RepFormat { get; set; }
    
    /// <summary>DPB sizing info from VPS extension.</summary>
    public HevcVpsDpbSize DpbSize { get; set; }
    
    private static sbyte[] InitLayerIdx()
    {
        var arr = new sbyte[HevcConstants.MaxNuhLayerId + 1];
        arr[0] = 0;
        for (int i = 1; i < arr.Length; i++)
            arr[i] = -1;
        return arr;
    }
}

/// <summary>
/// HEVC Sequence Parameter Set (SPS).
/// Contains picture-level parameters including dimensions, color format, and coding tools.
/// </summary>
public sealed class HevcSequenceParameterSet
{
    /// <summary>Reference to VPS (0-15).</summary>
    public byte VideoParameterSetId { get; set; }
    
    /// <summary>Maximum sublayers minus 1 (0-6).</summary>
    public byte MaxSubLayersMinus1 { get; set; }
    
    /// <summary>True if this SPS was parsed as a multi-layer extension SPS (nuh_layer_id > 0).</summary>
    public bool MultiLayerExt { get; set; }
    
    public bool TemporalIdNestingFlag { get; set; }
    
    public HevcProfileTierLevel ProfileTierLevel { get; set; }
    
    /// <summary>SPS ID (0-15).</summary>
    public byte SequenceParameterSetId { get; set; }
    
    /// <summary>Chroma format (0=mono, 1=4:2:0, 2=4:2:2, 3=4:4:4).</summary>
    public HevcChromaFormat ChromaFormatIdc { get; set; }
    
    /// <summary>True if chroma planes are coded separately (only when ChromaFormatIdc == 3).</summary>
    public bool SeparateColourPlaneFlag { get; set; }
    
    /// <summary>Picture width in luma samples (before cropping).</summary>
    public int PictureWidthInLumaSamples { get; set; }
    
    /// <summary>Picture height in luma samples (before cropping).</summary>
    public int PictureHeightInLumaSamples { get; set; }
    
    /// <summary>Conformance window present.</summary>
    public bool ConformanceWindowFlag { get; set; }
    
    /// <summary>Cropping offset from left edge.</summary>
    public int ConfWinLeftOffset { get; set; }
    
    /// <summary>Cropping offset from right edge.</summary>
    public int ConfWinRightOffset { get; set; }
    
    /// <summary>Cropping offset from top edge.</summary>
    public int ConfWinTopOffset { get; set; }
    
    /// <summary>Cropping offset from bottom edge.</summary>
    public int ConfWinBottomOffset { get; set; }
    
    /// <summary>Luma bit depth = 8 + this value.</summary>
    public byte BitDepthLumaMinus8 { get; set; }
    
    /// <summary>Chroma bit depth = 8 + this value.</summary>
    public byte BitDepthChromaMinus8 { get; set; }
    
    /// <summary>log2_max_pic_order_cnt_lsb = 4 + this value.</summary>
    public byte Log2MaxPicOrderCntLsbMinus4 { get; set; }
    
    public bool SubLayerOrderingInfoPresent { get; set; }
    
    /// <summary>DPB size per sublayer.</summary>
    public int[] MaxDecPicBufferingMinus1 { get; set; } = new int[HevcConstants.MaxSublayers];
    public int[] MaxNumReorderPics { get; set; } = new int[HevcConstants.MaxSublayers];
    public int[] MaxLatencyIncreasePlus1 { get; set; } = new int[HevcConstants.MaxSublayers];
    
    // CTU/CU/TU size parameters (key HEVC feature)
    
    /// <summary>Minimum coding block size = 8 + this value (in log2).</summary>
    public byte Log2MinLumaCodingBlockSizeMinus3 { get; set; }
    
    /// <summary>Difference between min and max coding block size (in log2).</summary>
    public byte Log2DiffMaxMinLumaCodingBlockSize { get; set; }
    
    /// <summary>Minimum transform block size = 4 + this value (in log2).</summary>
    public byte Log2MinLumaTransformBlockSizeMinus2 { get; set; }
    
    /// <summary>Difference between min and max transform block size (in log2).</summary>
    public byte Log2DiffMaxMinLumaTransformBlockSize { get; set; }
    
    /// <summary>Maximum transform hierarchy depth for inter prediction.</summary>
    public byte MaxTransformHierarchyDepthInter { get; set; }
    
    /// <summary>Maximum transform hierarchy depth for intra prediction.</summary>
    public byte MaxTransformHierarchyDepthIntra { get; set; }
    
    public bool ScalingListEnabled { get; set; }
    public bool ScalingListDataPresent { get; set; }
    public HevcScalingList? ScalingList { get; set; }
    
    /// <summary>Asymmetric motion partitions enabled.</summary>
    public bool AmpEnabled { get; set; }
    
    /// <summary>Sample adaptive offset enabled (new in HEVC).</summary>
    public bool SampleAdaptiveOffsetEnabled { get; set; }
    
    /// <summary>PCM sample coding enabled.</summary>
    public bool PcmEnabled { get; set; }
    public byte PcmSampleBitDepthLumaMinus1 { get; set; }
    public byte PcmSampleBitDepthChromaMinus1 { get; set; }
    public byte Log2MinPcmLumaCodingBlockSizeMinus3 { get; set; }
    public byte Log2DiffMaxMinPcmLumaCodingBlockSize { get; set; }
    public bool PcmLoopFilterDisabled { get; set; }
    
    /// <summary>Number of short-term reference picture sets.</summary>
    public int NumShortTermRefPicSets { get; set; }
    
    /// <summary>Parsed short-term reference picture sets (indexed 0..NumShortTermRefPicSets-1).</summary>
    public HevcShortTermRps[] ShortTermRpsList { get; set; } = Array.Empty<HevcShortTermRps>();
    
    public bool LongTermRefPicsPresent { get; set; }
    public int NumLongTermRefPicsSps { get; set; }
    
    /// <summary>Long-term ref pic POC LSBs from SPS.</summary>
    public int[] LtRefPicPocLsbSps { get; set; } = Array.Empty<int>();
    
    /// <summary>used_by_curr_pic_lt_sps_flag for each long-term ref.</summary>
    public bool[] UsedByCurrPicLtSpsFlag { get; set; } = Array.Empty<bool>();
    
    public bool SpsTemporalMvpEnabled { get; set; }
    public bool StrongIntraSmoothingEnabled { get; set; }
    
    public bool VuiParametersPresent { get; set; }
    public HevcVideoUsabilityInfo? Vui { get; set; }

    // SPS range extension (profile >= 4)
    public bool ExtensionPresent { get; set; }
    public bool RangeExtension { get; set; }
    public bool TransformSkipRotationEnabled { get; set; }
    public bool TransformSkipContextEnabled { get; set; }
    public bool ImplicitRdpcmEnabled { get; set; }
    public bool ExplicitRdpcmEnabled { get; set; }
    public bool ExtendedPrecisionProcessing { get; set; }
    public bool IntraSmoothingDisabled { get; set; }
    public bool HighPrecisionOffsetsEnabled { get; set; }
    public bool PersistentRiceAdaptationEnabled { get; set; }
    public bool CabacBypassAlignmentEnabled { get; set; }

    // Derived values
    
    /// <summary>Luma bit depth (8, 10, 12, etc.).</summary>
    public int BitDepthLuma => 8 + BitDepthLumaMinus8;

    /// <summary>QP bit depth offset for luma: 6 * (BitDepthLuma - 8).</summary>
    public int QpBdOffsetY => 6 * BitDepthLumaMinus8;
    
    /// <summary>Chroma bit depth (8, 10, 12, etc.).</summary>
    public int BitDepthChroma => 8 + BitDepthChromaMinus8;
    
    /// <summary>Log2 of minimum coding block size in luma samples.</summary>
    public int Log2MinCbSizeY => Log2MinLumaCodingBlockSizeMinus3 + 3;
    
    /// <summary>Log2 of CTB (Coding Tree Block) size in luma samples.</summary>
    public int Log2CtbSizeY => Log2MinCbSizeY + Log2DiffMaxMinLumaCodingBlockSize;
    
    /// <summary>CTB size in luma samples (16, 32, or 64).</summary>
    public int CtbSizeY => 1 << Log2CtbSizeY;
    
    /// <summary>Minimum coding block size in luma samples.</summary>
    public int MinCbSizeY => 1 << Log2MinCbSizeY;
    
    /// <summary>Log2 of minimum transform block size.</summary>
    public int Log2MinTbSizeY => Log2MinLumaTransformBlockSizeMinus2 + 2;
    
    /// <summary>Log2 of maximum transform block size.</summary>
    public int Log2MaxTbSizeY => Log2MinTbSizeY + Log2DiffMaxMinLumaTransformBlockSize;
    
    /// <summary>Maximum transform block size (up to 32x32).</summary>
    public int MaxTbSizeY => 1 << Log2MaxTbSizeY;
    
    /// <summary>Picture width in CTBs.</summary>
    public int PicWidthInCtbsY => (PictureWidthInLumaSamples + CtbSizeY - 1) / CtbSizeY;
    
    /// <summary>Picture height in CTBs.</summary>
    public int PicHeightInCtbsY => (PictureHeightInLumaSamples + CtbSizeY - 1) / CtbSizeY;
    
    /// <summary>Picture width in minimum coding blocks.</summary>
    public int PicWidthInMinCbsY => PictureWidthInLumaSamples / MinCbSizeY;
    
    /// <summary>Picture height in minimum coding blocks.</summary>
    public int PicHeightInMinCbsY => PictureHeightInLumaSamples / MinCbSizeY;
    
    /// <summary>Maximum picture order count LSB value.</summary>
    public int MaxPicOrderCntLsb => 1 << (Log2MaxPicOrderCntLsbMinus4 + 4);
    
    /// <summary>Display width after conformance window cropping.</summary>
    public int DisplayWidth
    {
        get
        {
            if (!ConformanceWindowFlag)
                return PictureWidthInLumaSamples;
            
            int subWidthC = ChromaFormatIdc == HevcChromaFormat.Chroma444 ? 1 : 2;
            return PictureWidthInLumaSamples - subWidthC * (ConfWinLeftOffset + ConfWinRightOffset);
        }
    }
    
    /// <summary>Display height after conformance window cropping.</summary>
    public int DisplayHeight
    {
        get
        {
            if (!ConformanceWindowFlag)
                return PictureHeightInLumaSamples;
            
            int subHeightC = ChromaFormatIdc == HevcChromaFormat.Chroma420 ? 2 : 1;
            return PictureHeightInLumaSamples - subHeightC * (ConfWinTopOffset + ConfWinBottomOffset);
        }
    }
    
    /// <summary>Frame rate from VUI if available.</summary>
    public double FrameRate => Vui?.FrameRate ?? 0;

    /// <summary>Horizontal chroma subsampling shift. 420/422=1, 444/mono=0.</summary>
    public int HShiftChroma => ChromaFormatIdc is HevcChromaFormat.Chroma420 or HevcChromaFormat.Chroma422 ? 1 : 0;

    /// <summary>Vertical chroma subsampling shift. 420=1, 422/444/mono=0.</summary>
    public int VShiftChroma => ChromaFormatIdc == HevcChromaFormat.Chroma420 ? 1 : 0;
}

/// <summary>
/// HEVC Picture Parameter Set (PPS).
/// Contains picture-level parameters that can change per-picture.
/// </summary>
public sealed class HevcPictureParameterSet
{
    /// <summary>PPS ID (0-63).</summary>
    public byte PictureParameterSetId { get; set; }
    
    /// <summary>Reference to SPS (0-15).</summary>
    public byte SequenceParameterSetId { get; set; }
    
    /// <summary>Dependent slice segments enabled.</summary>
    public bool DependentSliceSegmentsEnabled { get; set; }
    
    /// <summary>Output flag present in slice header.</summary>
    public bool OutputFlagPresent { get; set; }
    
    /// <summary>Number of extra slice header bits.</summary>
    public byte NumExtraSliceHeaderBits { get; set; }
    
    public bool SignDataHidingEnabled { get; set; }
    
    /// <summary>CABAC init type can be specified in slice.</summary>
    public bool CabacInitPresent { get; set; }
    
    /// <summary>Default number of active L0 references minus 1.</summary>
    public byte NumRefIdxL0DefaultActiveMinus1 { get; set; }
    
    /// <summary>Default number of active L1 references minus 1.</summary>
    public byte NumRefIdxL1DefaultActiveMinus1 { get; set; }
    
    /// <summary>Initial QP = 26 + this value.</summary>
    public sbyte InitQpMinus26 { get; set; }
    
    public bool ConstrainedIntraPred { get; set; }
    public bool TransformSkipEnabled { get; set; }
    
    public bool CuQpDeltaEnabled { get; set; }
    public int DiffCuQpDeltaDepth { get; set; }
    
    public sbyte PpsCbQpOffset { get; set; }
    public sbyte PpsCrQpOffset { get; set; }
    public bool PicSliceLevelChromaQpOffsetsPresent { get; set; }
    
    public bool WeightedPred { get; set; }
    public bool WeightedBipred { get; set; }
    public bool TransquantBypassEnabled { get; set; }
    
    // Tile and wavefront parallelism (key HEVC feature)
    
    /// <summary>Tiles enabled for parallel processing.</summary>
    public bool TilesEnabled { get; set; }
    
    /// <summary>Entropy coding sync enabled for wavefront parallel processing.</summary>
    public bool EntropyCodingSyncEnabled { get; set; }
    
    /// <summary>Number of tile columns minus 1.</summary>
    public int NumTileColumnsMinus1 { get; set; }
    
    /// <summary>Number of tile rows minus 1.</summary>
    public int NumTileRowsMinus1 { get; set; }
    
    public bool UniformSpacingFlag { get; set; }
    public int[]? ColumnWidthMinus1 { get; set; }
    public int[]? RowHeightMinus1 { get; set; }
    
    public bool LoopFilterAcrossTilesEnabled { get; set; }
    public bool LoopFilterAcrossSlicesEnabled { get; set; }
    
    // Deblocking filter control
    
    public bool DeblockingFilterControlPresent { get; set; }
    public bool DeblockingFilterOverrideEnabled { get; set; }
    public bool DeblockingFilterDisabled { get; set; }
    public sbyte BetaOffsetDiv2 { get; set; }
    public sbyte TcOffsetDiv2 { get; set; }
    
    public bool ScalingListDataPresent { get; set; }
    public HevcScalingList? ScalingList { get; set; }
    public bool ListsModificationPresent { get; set; }
    public byte Log2ParallelMergeLevelMinus2 { get; set; }
    public bool SliceHeaderExtensionPresent { get; set; }
    
    public bool PpsExtensionPresent { get; set; }
    public bool PpsRangeExtensionFlag { get; set; }
    public bool PpsMultilayerExtensionFlag { get; set; }
    public bool Pps3DExtensionFlag { get; set; }

    // PPS range extension fields
    public int Log2MaxTransformSkipBlockSize { get; set; } = 2; // default for Main profile
    public bool CrossComponentPredictionEnabled { get; set; }
    public bool ChromaQpOffsetListEnabled { get; set; }
    public int DiffCuChromaQpOffsetDepth { get; set; }
    public int ChromaQpOffsetListLen { get; set; }
    public int[] CbQpOffset { get; set; } = Array.Empty<int>();
    public int[] CrQpOffset { get; set; } = Array.Empty<int>();
    public int Log2SaoOffsetScale0 { get; set; }
    public int Log2SaoOffsetScale1 { get; set; }
    
    // Derived values
    
    /// <summary>Initial QP value.</summary>
    public int InitQp => 26 + InitQpMinus26;
    
    /// <summary>Number of tile columns.</summary>
    public int NumTileColumns => TilesEnabled ? NumTileColumnsMinus1 + 1 : 1;
    
    /// <summary>Number of tile rows.</summary>
    public int NumTileRows => TilesEnabled ? NumTileRowsMinus1 + 1 : 1;
    
    /// <summary>Log2 of parallel merge level (derived from Log2ParallelMergeLevelMinus2 + 2).</summary>
    public int Log2ParallelMergeLevel => Log2ParallelMergeLevelMinus2 + 2;

    // ── Tile derived arrays (computed by ComputeTileDerivedArrays) ──

    /// <summary>Tile column widths in CTB units.</summary>
    public int[]? ColumnWidth { get; set; }

    /// <summary>Tile row heights in CTB units.</summary>
    public int[]? RowHeight { get; set; }

    /// <summary>Tile column boundaries in CTB units (length = NumTileColumns + 1).</summary>
    public int[]? ColBd { get; set; }

    /// <summary>Tile row boundaries in CTB units (length = NumTileRows + 1).</summary>
    public int[]? RowBd { get; set; }

    /// <summary>CTB x-coordinate → tile column index lookup.</summary>
    public int[]? ColIdxX { get; set; }

    /// <summary>Raster scan CTB address → tile scan CTB address.</summary>
    public int[]? CtbAddrRsToTs { get; set; }

    /// <summary>Tile scan CTB address → raster scan CTB address.</summary>
    public int[]? CtbAddrTsToRs { get; set; }

    /// <summary>Tile ID per tile-scan CTB address.</summary>
    public int[]? TileIdPerTs { get; set; }

    /// <summary>First raster CTB address of each tile (indexed by tile ID).</summary>
    public int[]? TilePosRs { get; set; }

    /// <summary>
    /// Computes tile-derived arrays from parsed PPS parameters and SPS dimensions.
    /// Must be called after PPS parsing with a valid SPS reference.
    /// Matches FFmpeg ps.c lines 2040-2162.
    /// </summary>
    public void ComputeTileDerivedArrays(int ctbWidth, int ctbHeight)
    {
        int numTileCols = NumTileColumns;
        int numTileRows = NumTileRows;

        // Compute column widths (in CTB units)
        ColumnWidth = new int[numTileCols];
        if (!TilesEnabled || UniformSpacingFlag)
        {
            for (int i = 0; i < numTileCols; i++)
                ColumnWidth[i] = ((i + 1) * ctbWidth) / numTileCols - (i * ctbWidth) / numTileCols;
        }
        else
        {
            int remaining = ctbWidth;
            for (int i = 0; i < numTileCols - 1; i++)
            {
                ColumnWidth[i] = ColumnWidthMinus1![i] + 1;
                remaining -= ColumnWidth[i];
            }
            ColumnWidth[numTileCols - 1] = remaining;
        }

        // Compute row heights (in CTB units)
        RowHeight = new int[numTileRows];
        if (!TilesEnabled || UniformSpacingFlag)
        {
            for (int i = 0; i < numTileRows; i++)
                RowHeight[i] = ((i + 1) * ctbHeight) / numTileRows - (i * ctbHeight) / numTileRows;
        }
        else
        {
            int remaining = ctbHeight;
            for (int i = 0; i < numTileRows - 1; i++)
            {
                RowHeight[i] = RowHeightMinus1![i] + 1;
                remaining -= RowHeight[i];
            }
            RowHeight[numTileRows - 1] = remaining;
        }

        // Column and row boundaries (cumulative)
        ColBd = new int[numTileCols + 1];
        ColBd[0] = 0;
        for (int i = 0; i < numTileCols; i++)
            ColBd[i + 1] = ColBd[i] + ColumnWidth[i];

        RowBd = new int[numTileRows + 1];
        RowBd[0] = 0;
        for (int i = 0; i < numTileRows; i++)
            RowBd[i + 1] = RowBd[i] + RowHeight[i];

        // ColIdxX: CTB x → tile column index
        ColIdxX = new int[ctbWidth];
        for (int i = 0, j = 0; i < ctbWidth; i++)
        {
            if (i >= ColBd[j + 1])
                j++;
            ColIdxX[i] = j;
        }

        // Address mappings: raster ↔ tile scan
        int totalCtbs = ctbWidth * ctbHeight;
        CtbAddrRsToTs = new int[totalCtbs];
        CtbAddrTsToRs = new int[totalCtbs];

        for (int rs = 0; rs < totalCtbs; rs++)
        {
            int tbX = rs % ctbWidth;
            int tbY = rs / ctbWidth;

            // Find tile column/row for this CTB
            int tileX = ColIdxX[tbX];
            int tileY = 0;
            for (int i = 0; i < numTileRows; i++)
            {
                if (tbY < RowBd[i + 1]) { tileY = i; break; }
            }

            // Compute tile-scan address
            int val = 0;
            for (int i = 0; i < tileX; i++)
                val += RowHeight[tileY] * ColumnWidth[i];
            for (int i = 0; i < tileY; i++)
                val += ctbWidth * RowHeight[i];
            val += (tbY - RowBd[tileY]) * ColumnWidth[tileX] + tbX - ColBd[tileX];

            CtbAddrRsToTs[rs] = val;
            CtbAddrTsToRs[val] = rs;
        }

        // Tile IDs per tile-scan address
        TileIdPerTs = new int[totalCtbs];
        int tileId = 0;
        for (int j = 0; j < numTileRows; j++)
        {
            for (int i = 0; i < numTileCols; i++, tileId++)
            {
                for (int y = RowBd[j]; y < RowBd[j + 1]; y++)
                    for (int x = ColBd[i]; x < ColBd[i + 1]; x++)
                        TileIdPerTs[CtbAddrRsToTs[y * ctbWidth + x]] = tileId;
            }
        }

        // First raster address of each tile
        int numTiles = numTileCols * numTileRows;
        TilePosRs = new int[numTiles];
        for (int j = 0; j < numTileRows; j++)
            for (int i = 0; i < numTileCols; i++)
                TilePosRs[j * numTileCols + i] = RowBd[j] * ctbWidth + ColBd[i];
    }
}

/// <summary>
/// HEVC Slice Segment Header.
/// Contains per-slice parameters.
/// </summary>
public sealed class HevcSliceSegmentHeader
{
    /// <summary>NAL unit type from the NAL header.</summary>
    public HevcNalUnitType NalType { get; set; }
    
    /// <summary>Layer ID from the NAL header.</summary>
    public byte NuhLayerId { get; set; }
    
    /// <summary>Temporal ID plus 1 from the NAL header.</summary>
    public byte TemporalIdPlus1 { get; set; }
    
    /// <summary>True if this is the first slice segment in the picture.</summary>
    public bool FirstSliceSegmentInPicFlag { get; set; }
    
    /// <summary>True if prior pictures should not be output (for IRAP pictures).</summary>
    public bool NoOutputOfPriorPicsFlag { get; set; }
    
    /// <summary>Reference to PPS.</summary>
    public byte SlicePicParameterSetId { get; set; }
    
    /// <summary>True if this slice depends on the previous slice for header info.</summary>
    public bool DependentSliceSegmentFlag { get; set; }
    
    /// <summary>Address of the first CTB in this slice segment.</summary>
    public int SliceSegmentAddress { get; set; }
    
    /// <summary>Slice type (B=0, P=1, I=2).</summary>
    public HevcSliceType SliceType { get; set; }
    
    /// <summary>Picture output flag.</summary>
    public bool PicOutputFlag { get; set; }
    
    /// <summary>Colour plane ID (when separate_colour_plane_flag is set).</summary>
    public byte ColourPlaneId { get; set; }
    
    /// <summary>Picture order count LSB.</summary>
    public int PicOrderCntLsb { get; set; }
    
    /// <summary>CABAC init flag (selects init table swap for P/B slices).</summary>
    public bool CabacInitFlag { get; set; }
    
    // --- Reference picture management ---
    
    /// <summary>Active short-term RPS for this slice (from SPS table or parsed inline).</summary>
    public HevcShortTermRps? ShortTermRps { get; set; }
    
    /// <summary>Long-term RPS parsed from slice header.</summary>
    public HevcLongTermRps LongTermRps { get; set; } = new();
    
    /// <summary>Inter-layer prediction flag (MV-HEVC: layer 1 can reference layer 0's frame).</summary>
    public bool InterLayerPred { get; set; }
    
    /// <summary>Number of active L0 references.</summary>
    public int NumRefIdxL0Active { get; set; }
    
    /// <summary>Number of active L1 references.</summary>
    public int NumRefIdxL1Active { get; set; }
    
    /// <summary>ref_pic_list_modification_flag for L0/L1.</summary>
    public bool[] RplModificationFlag { get; set; } = new bool[2];
    
    /// <summary>list_entry_lx[listIdx][i] reorder indices.</summary>
    public int[][] ListEntryLx { get; set; } = { Array.Empty<int>(), Array.Empty<int>() };
    
    /// <summary>mvd_l1_zero_flag for B slices.</summary>
    public bool MvdL1ZeroFlag { get; set; }
    
    /// <summary>collocated_from_l0_flag (B slices, temporal MVP).</summary>
    public bool CollocatedFromL0Flag { get; set; } = true;
    
    /// <summary>collocated_ref_idx for temporal MVP.</summary>
    public int CollocatedRefIdx { get; set; }
    
    /// <summary>5 - five_minus_max_num_merge_cand.</summary>
    public int MaxNumMergeCand { get; set; } = 5;
    
    // --- Weighted prediction (pred_weight_table) ---
    
    /// <summary>Log2 of luma weight denominator.</summary>
    public int LumaLog2WeightDenom { get; set; }
    
    /// <summary>Log2 of chroma weight denominator.</summary>
    public int ChromaLog2WeightDenom { get; set; }
    
    /// <summary>Luma weights for L0 references (indexed by ref_idx).</summary>
    public short[] LumaWeightL0 { get; set; } = Array.Empty<short>();
    
    /// <summary>Luma offsets for L0 references.</summary>
    public short[] LumaOffsetL0 { get; set; } = Array.Empty<short>();
    
    /// <summary>Chroma weights for L0 references [refIdx][component: 0=Cb, 1=Cr].</summary>
    public short[,] ChromaWeightL0 { get; set; } = new short[0, 0];
    
    /// <summary>Chroma offsets for L0 references [refIdx][component: 0=Cb, 1=Cr].</summary>
    public short[,] ChromaOffsetL0 { get; set; } = new short[0, 0];
    
    /// <summary>Luma weights for L1 references.</summary>
    public short[] LumaWeightL1 { get; set; } = Array.Empty<short>();
    
    /// <summary>Luma offsets for L1 references.</summary>
    public short[] LumaOffsetL1 { get; set; } = Array.Empty<short>();
    
    /// <summary>Chroma weights for L1 references [refIdx][component: 0=Cb, 1=Cr].</summary>
    public short[,] ChromaWeightL1 { get; set; } = new short[0, 0];
    
    /// <summary>Chroma offsets for L1 references [refIdx][component: 0=Cb, 1=Cr].</summary>
    public short[,] ChromaOffsetL1 { get; set; } = new short[0, 0];
    
    /// <summary>Slice QP delta (added to PPS init QP).</summary>
    public int SliceQpDelta { get; set; }
    
    /// <summary>Slice CB QP offset.</summary>
    public int SliceCbQpOffset { get; set; }
    
    /// <summary>Slice CR QP offset.</summary>
    public int SliceCrQpOffset { get; set; }
    
    /// <summary>Per-CU chroma QP offset enabled for this slice (RExt).</summary>
    public bool CuChromaQpOffsetEnabled { get; set; }
    
    /// <summary>Slice SAO luma flag.</summary>
    public bool SliceSaoLumaFlag { get; set; }
    
    /// <summary>Slice SAO chroma flag.</summary>
    public bool SliceSaoChromaFlag { get; set; }
    
    /// <summary>Slice temporal MVP enabled.</summary>
    public bool SliceTemporalMvpEnabled { get; set; }
    
    /// <summary>Disable deblocking filter for this slice.</summary>
    public bool SliceDeblockingFilterDisabled { get; set; }
    
    /// <summary>Slice loop filter across slices enabled.</summary>
    public bool SliceLoopFilterAcrossSlicesEnabled { get; set; }
    
    /// <summary>Beta offset div 2 for deblocking.</summary>
    public int SliceBetaOffsetDiv2 { get; set; }
    
    /// <summary>TC offset div 2 for deblocking.</summary>
    public int SliceTcOffsetDiv2 { get; set; }
    
    /// <summary>Entry point offsets for tiles/WPP. Each value is the byte size of the corresponding segment.</summary>
    public uint[] EntryPointOffsets { get; set; } = Array.Empty<uint>();
    
    /// <summary>Byte offset in RBSP where CABAC-encoded slice data begins (after byte_alignment).</summary>
    public int SliceDataByteOffset { get; set; }
    
    /// <summary>Start CTB address of the independent slice this segment belongs to. 
    /// For independent slices, equals SliceSegmentAddress. For dependent slices, inherited from the independent slice.</summary>
    public int SliceAddr { get; set; }
    
    /// <summary>Slice QP = PPS init QP + SliceQpDelta.</summary>
    public int SliceQp(HevcPictureParameterSet pps) => pps.InitQp + SliceQpDelta;
    
    /// <summary>True if the slice is an IRAP.</summary>
    public bool IsIrap => HevcNalUtilities.IsIrapNalType(NalType);
    
    /// <summary>True if the slice is an IDR.</summary>
    public bool IsIdr => NalType is HevcNalUnitType.IdrWithRadl or HevcNalUnitType.IdrNoLeadingPictures;
    
    /// <summary>Temporal ID (0-6).</summary>
    public int TemporalId => TemporalIdPlus1 > 0 ? TemporalIdPlus1 - 1 : 0;
}
