// HEVC/H.265 software video decoder
// Reference: ITU-T H.265, VLC's modules/codec/hevc

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Software HEVC/H.265 video decoder implementing IVideoDecoder.
/// Provides pure-software decoding for HEVC Main, Main10, and higher profiles.
/// </summary>
/// <remarks>
/// This decoder orchestrates the existing HEVC decoding components:
/// - NAL unit parsing (HevcNalParser)
/// - Parameter set management (HevcDecoderContext)
/// - CABAC entropy decoding (HevcCabacDecoder)
/// - Coding tree processing (HevcCodingTree)
/// - Transform and reconstruction (HevcTransform)
/// - Motion compensation (HevcMotionCompensation)
/// - Deblocking filter (HevcDeblockingFilter)
/// - SAO filter (HevcSaoFilter)
/// </remarks>
internal sealed partial class HevcDecoder
{
    private const int MaxDpbFrames = 32; // Matches FFmpeg; allows room for generated missing refs
    private const int CabacContextStorageSize = 512;
    
    // DPB frame flags (matches FFmpeg's HEVC_FRAME_FLAG_* in refs.h)
    private const byte FrameFlagOutput   = 1 << 0; // Frame pending output (display order)
    private const byte FrameFlagShortRef = 1 << 1; // Short-term reference
    private const byte FrameFlagLongRef  = 1 << 2; // Long-term reference
    private const byte FrameFlagUnavailable = 1 << 3; // Generated missing reference (mid-gray)
    private const byte FrameFlagRef = FrameFlagShortRef | FrameFlagLongRef;
    
    private readonly HevcDecoderContext context;
    
    // Multi-layer state (MV-HEVC): each layer has its own DPB and frame state.
    // For single-layer streams, only layers[0] is used (curLayer always 0).
    private readonly HevcLayerState[] layers;
    private int curLayer;
    private int nbLayers = 1; // Set from VPS when MV-HEVC detected
    private uint layersActiveDecode = 1; // Bitmask of active decode layers
    private uint layersActiveOutput = 1; // Bitmask of active output layers
    
    // Active layer's DPB (swapped by SwitchLayer)
    private HevcReferenceFrame[] dpb;
    private int dpbCount;
    
    // Inter-layer reference: DPB index of the borrowed frame from layer 0 in layer 1's DPB.
    // Set to >= 0 when inter_layer_pred is active; -1 otherwise.
    private int interLayerRefDpbIdx = -1;
    
    // Reference picture lists (built per-slice from RPS + DPB)
    // List0/List1 contain indices into dpb[]
    private readonly int[] refPicList0 = new int[HevcConstants.MaxRefs];
    private readonly int[] refPicList1 = new int[HevcConstants.MaxRefs];
    private int numRefList0;
    private int numRefList1;
    
    // Long-term reference flags per ref list entry (true = long-term, false = short-term)
    private readonly bool[] refIsLongTerm0 = new bool[HevcConstants.MaxRefs];
    private readonly bool[] refIsLongTerm1 = new bool[HevcConstants.MaxRefs];
    
    // Resolved reference picture POCs (ref_idx → POC, for boundary strength computation)
    private readonly int[] refPocList0 = new int[HevcConstants.MaxRefs];
    private readonly int[] refPocList1 = new int[HevcConstants.MaxRefs];
    
    // Per-PU motion vector storage (for merge/AMVP spatial candidates)
    // Indexed by (y/4) * puWidthIn4 + (x/4) — quarter-luma-sample grid
    private short[]? mvFieldL0X;
    private short[]? mvFieldL0Y;
    private short[]? mvFieldL1X;
    private short[]? mvFieldL1Y;
    private sbyte[]? refIdxFieldL0;
    private sbyte[]? refIdxFieldL1;
    private byte[]? predModeField; // PredFlag: 0=intra, 1=L0, 2=L1, 3=BI
    private byte[]? cbfLumaField; // 1 if transform block has non-zero luma residual
    private int puWidthIn4;
    private int puHeightIn4;
    
    // Current frame being decoded
    private byte[]? currentFrameBuffer;
    private int currentPoc;
    private bool currentIsReference;
    private long currentPts;
    
    // Collocated reference for temporal MVP (set per-slice)
    private int collocatedRefDpbIdx = -1;
    private HevcSliceSegmentHeader? currentSliceHeader;
    private HevcPictureParameterSet? currentPps;
    private HevcScalingList? activeScalingList;
    
    // Per-CTB ref list accumulation during multi-slice frame decode (FFmpeg's rpl_tab equivalent)
    private List<SliceRefListSnapshot>? currentFrameSliceRefLists;
    private byte[]? currentFramePerCtbSliceIdx;
    
    // POC state — tracks the POC of the last TID0 non-RASL/RADL/sub-layer-non-reference picture (spec 8.3.1)
    private int pocTid0;
    
    // Decoding buffers
    private short[]? residualBuffer;
    private int[]? coefficientBuffer;
    private byte[]? cabacContextStorage;
    
    // Cross-component prediction: save luma residual for chroma CCP
    private short[] ccpLumaResidual = new short[32 * 32]; // Max 32x32 TU
    
    // WPP context state buffer: persists across slices within the same picture.
    // Matches FFmpeg's common_cabac_state which is shared across all local contexts.
    // When entropy_coding_sync_enabled_flag is set, CABAC context states are saved
    // after the 2nd CTU of each row and restored at the start of the next row.
    private byte[]? wppSavedContexts;
    private int[]? wppSavedStatCoeff;

    // Persistent rice adaptation state (FFmpeg: lc->stat_coeff[4])
    // Indices: 0 = luma non-skip, 1 = luma skip/bypass, 2 = chroma non-skip, 3 = chroma skip/bypass
    private readonly int[] statCoeff = new int[4];

    // NAL unit length size for hvcC format (0 = Annex B, 1-4 = length prefix size)
    private int nalLengthSize;
    
    private bool isInitialized;
    private bool isDisposed;
    private bool lastEos; // Set when EOS NAL is received; CRA after EOS forces no_output_of_prior
    private bool noRaslOutputFlag; // When set, RASL pictures are skipped (FFmpeg: no_rasl_output_flag)
    private int framesDecoded;
    private int nalUnitsProcessed;
    
    // Track active SPS for detecting mid-stream SPS changes (dimension + CTB size)
    private int activeSpsWidth;
    private int activeSpsHeight;
    private HevcSequenceParameterSet? lastAllocatedSps; // SPS used for per-picture arrays
    
    // DPB-based output: frames with FrameFlagOutput are pending display-order output.
    // This matches FFmpeg's unified DPB model where OUTPUT is a per-frame flag.
    private int maxNumReorderFrames;
    private int maxDecPicBuffering;
    
    // Output queue for frames that have been reordered and are ready to be returned.
    // Filled when reorder buffer exceeds threshold or at IDR/flush boundaries.
    private readonly Queue<DecodedVideoFrame> outputQueue = new();

    // Diagnostic counters for debugging (public for test harness)
    public int LastCtusDecoded;
    public int LastTotalCtus;
    public int LastCabacBitPosition;
    public int LastCabacRemainingBytes;
    public int DiagCbfLumaCount;
    public int DiagCbfChromaCount;
    public int DiagNonZeroCoeffCount;
    public int DiagTotalTuCount;
    public bool DiagSkipInterResidual;
    public bool DiagSkipDeblocking;
    public bool DiagSkipSao;
    public bool DiagBaseLayerOnly; // Force single-layer mode (skip enhancement layers)
    public bool DiagSkipWppReinit; // Skip WPP CABAC reinit at row boundaries (diagnostic)
    public bool DiagForceTableContextsForWpp; // Use table-init contexts instead of saved WPP contexts
    public bool DiagDumpBsStats; // Dump BS table statistics per frame
    public List<(int X, int Y, int Size, int Mode, int CIdx)>? DiagIntraPredLog;
    public List<(int X, int Y, int Size, bool CbfLuma, int NonZero)>? DiagCbfLog;
    public List<(int X, int Y, int Delta, int QpY, int QpPred)>? DiagQpDeltaLog;
    public List<(int CtbAddr, int CtbX, int CtbY, int BitPos, int BinCount, uint Range, uint Offset)>? DiagCtuStartLog;
    public List<(int CtbAddr, int SaoBins, int TreeBins)>? DiagCtuPhaseLog;
    public List<(int X, int Y, int TrafoDepth, bool SplitTf, bool CbfCb, bool CbfCr, bool CbfLuma, 
        byte CtxStateSplitTf, byte CtxStateCbfLuma, uint PreRange, uint PreOffset)>? DiagTransformTreeLog;
    public HashSet<int>? DiagTraceCtuAddrs;
    public int DiagTracePicNum; // Only trace bins for this pic number (0 = all pics)
    public Dictionary<int, List<(int BinIdx, char Mode, int CtxIdx, int BinVal, uint PreRange, uint PreOffset, byte CtxState)>>? DiagCtuBinLogs;
    public List<(int RowIndex, int CtbY, int NaturalBytePos, int EntryOffset, int Delta)>? DiagWppOffsetLog;
    public List<(int PicNum, char Action, int RowIndex, int CtbX, int CtbY, int Checksum, byte B0, byte B1, byte B2, byte B3)>? DiagWppContextLog;
    public List<(int RowIndex, int CtbY, int ByteOffset, uint InitRange, uint InitOffset, byte[] FirstBytes)>? DiagWppEntryBytesLog;
    public List<(int PicNum, int Poc, int StartCtb, int CtusDecoded, bool Dependent, int EntryPoints, int SliceType, int DataLen)>? DiagSliceLog;
    public List<(int PicNum, int Poc, int[] RefL0Pocs, int[] RefL1Pocs)>? DiagRefListLog;
    public byte[]? DiagWppSavedStatesCapture; // Captures WPP states at dep slice init
    public byte[]? DiagCabacDataCapture; // Captures CABAC data bytes for shadow comparison
    public byte[]? DiagCabacContextCapture; // Captures context states at CABAC init for shadow comparison
    public int DiagCaptureSliceStartCtb = -1; // Which slice's CABAC data to capture (-1 = none)
    public List<(int CtbAddr, int X, int Y, int Log2Size, int CIdx, bool TransformSkip, bool TransquantBypass)>? DiagResidualLog;
    private int diagPicCounter;
    public List<string>? DiagDecodeCallLog; // Tracks what happens in each Decode() call
    public List<(int PicNum, int SliceStartCtb, int CtbAddrRs, bool MoreData, int BytePos)>? DiagCtuMoreDataLog;
    
    public List<string>? DiagContextParseLog
    {
        get => context.DiagParseLog;
        set => context.DiagParseLog = value;
    }
    public HevcSequenceParameterSet? DiagActiveSps => context.ActiveSps;
    public HevcPictureParameterSet? DiagActivePps => context.ActivePps;
    public byte[]? DiagSaoTypeIdxTab => saoTypeIdxTab;
    public byte[]? DiagSaoEoClassTab => saoEoClassTab;
    public byte[]? DiagSaoBandPositionTab => saoBandPositionTab;
    public int[]? DiagSaoOffsetValTab => saoOffsetValTab;

    public string CodecId => "hevc";
    public bool IsHardwareAccelerated => false;
    public int Width => context.Width;
    public int Height => context.Height;
    public PixelFormat OutputFormat => context.BitDepthLuma > 8 ? PixelFormat.Yuv420P10 : PixelFormat.Yuv420P;
    
    // Diagnostic properties
    public bool IsReady => isInitialized && context.ActiveSps != null;
    public int FramesDecoded => framesDecoded;
    public int NalUnitsProcessed => nalUnitsProcessed;

    public HevcDecoder()
    {
        context = new HevcDecoderContext();
        layers = new HevcLayerState[2]; // Max 2 layers for MV-HEVC
        for (int i = 0; i < layers.Length; i++)
            layers[i] = new HevcLayerState(MaxDpbFrames);
        dpb = layers[0].Dpb;
    }

    /// <summary>
    /// Switches the active layer for decode operations.
    /// Saves current layer's mutable state and restores the target layer's state.
    /// This avoids changing hundreds of field references — the decoder always reads/writes
    /// dpb, dpbCount, currentFrameBuffer, etc., and SwitchLayer swaps what they point to.
    /// </summary>
    private void SwitchLayer(int newLayer)
    {
        if (newLayer == curLayer)
            return;
        
        DiagDecodeCallLog?.Add($"  SwitchLayer {curLayer}→{newLayer} (L{curLayer} dpbCount={dpbCount}, L{newLayer} saved dpbCount={layers[newLayer].DpbCount})");
        
        // Save current layer's mutable state
        SaveLayerState(curLayer);
        
        // Switch to new layer and restore its state
        curLayer = newLayer;
        RestoreLayerState(newLayer);
    }
    
    /// <summary>
    /// Saves the decoder's mutable per-layer fields into the specified layer state.
    /// </summary>
    private void SaveLayerState(int layerIdx)
    {
        var ls = layers[layerIdx];
        // DPB: dpb array is already ls.Dpb (reference equality), just save count
        ls.DpbCount = dpbCount;
        ls.CurrentFrameBuffer = currentFrameBuffer;
        ls.CurrentPoc = currentPoc;
        ls.CurrentIsReference = currentIsReference;
        ls.CurrentPts = currentPts;
        ls.MaxNumReorderFrames = maxNumReorderFrames;
        ls.MaxDecPicBuffering = maxDecPicBuffering;
        ls.ActiveSpsWidth = activeSpsWidth;
        ls.ActiveSpsHeight = activeSpsHeight;
        ls.LastAllocatedSps = lastAllocatedSps;
        ls.WppSavedContexts = wppSavedContexts;
        ls.WppSavedStatCoeff = wppSavedStatCoeff;
        ls.FrameSliceRefLists = currentFrameSliceRefLists;
        ls.FramePerCtbSliceIdx = currentFramePerCtbSliceIdx;
        ls.PocTid0 = pocTid0;
        // Save MV working arrays (needed for inter-layer ref access)
        ls.MvFieldL0X = mvFieldL0X;
        ls.MvFieldL0Y = mvFieldL0Y;
        ls.MvFieldL1X = mvFieldL1X;
        ls.MvFieldL1Y = mvFieldL1Y;
        ls.RefIdxFieldL0 = refIdxFieldL0;
        ls.RefIdxFieldL1 = refIdxFieldL1;
        ls.PredModeField = predModeField;
        ls.CbfLumaField = cbfLumaField;
        ls.PuWidthIn4 = puWidthIn4;
        ls.PuHeightIn4 = puHeightIn4;
        // Save CTB-level arrays (per-picture decode state)
        ls.TabCtDepth = tabCtDepth;
        ls.TabSkipFlag = tabSkipFlag;
        ls.TabIntraPredMode = tabIntraPredMode;
        ls.QpYTab = qpYTab;
        ls.VertBsTab = vertBsTab;
        ls.HorizBsTab = horizBsTab;
        ls.BsWidth = bsWidth;
        ls.BsHeight = bsHeight;
        ls.IsIntraTab8x8 = isIntraTab8x8;
        ls.PicWidthIn8 = picWidthIn8;
        ls.PicHeightIn8 = picHeightIn8;
        ls.IsPcmTab = isPcmTab;
        ls.IsPcmWidth = isPcmWidth;
        ls.SaoTypeIdxTab = saoTypeIdxTab;
        ls.SaoEoClassTab = saoEoClassTab;
        ls.SaoBandPositionTab = saoBandPositionTab;
        ls.SaoOffsetValTab = saoOffsetValTab;
        ls.CtuBetaOffset = ctuBetaOffset;
        ls.CtuTcOffset = ctuTcOffset;
        ls.CtuBoundaryFlags = ctuBoundaryFlags;
        ls.CtuLoopFilterAcrossSlices = ctuLoopFilterAcrossSlices;
        ls.CtuDeblockDisabled = ctuDeblockDisabled;
        ls.TabSliceAddress = tabSliceAddress;
        // Save slice/PPS state (needed for finalization)
        ls.CurrentSliceHeader = currentSliceHeader;
        ls.CurrentPps = currentPps;
        ls.ActiveScalingList = activeScalingList;
        ls.CollocatedRefDpbIdx = collocatedRefDpbIdx;
        ls.InterLayerRefDpbIdx = interLayerRefDpbIdx;
        // Save context-level parameter sets (each layer may activate different SPS/PPS)
        ls.ContextActiveSps = context.ActiveSps;
        ls.ContextActivePps = context.ActivePps;
    }
    
    /// <summary>
    /// Restores the decoder's mutable per-layer fields from the specified layer state.
    /// </summary>
    private void RestoreLayerState(int layerIdx)
    {
        var ls = layers[layerIdx];
        dpb = ls.Dpb;
        dpbCount = ls.DpbCount;
        currentFrameBuffer = ls.CurrentFrameBuffer;
        currentPoc = ls.CurrentPoc;
        currentIsReference = ls.CurrentIsReference;
        currentPts = ls.CurrentPts;
        maxNumReorderFrames = ls.MaxNumReorderFrames;
        maxDecPicBuffering = ls.MaxDecPicBuffering;
        activeSpsWidth = ls.ActiveSpsWidth;
        activeSpsHeight = ls.ActiveSpsHeight;
        lastAllocatedSps = ls.LastAllocatedSps;
        wppSavedContexts = ls.WppSavedContexts;
        wppSavedStatCoeff = ls.WppSavedStatCoeff;
        currentFrameSliceRefLists = ls.FrameSliceRefLists;
        currentFramePerCtbSliceIdx = ls.FramePerCtbSliceIdx;
        pocTid0 = ls.PocTid0;
        // Restore MV working arrays
        mvFieldL0X = ls.MvFieldL0X;
        mvFieldL0Y = ls.MvFieldL0Y;
        mvFieldL1X = ls.MvFieldL1X;
        mvFieldL1Y = ls.MvFieldL1Y;
        refIdxFieldL0 = ls.RefIdxFieldL0;
        refIdxFieldL1 = ls.RefIdxFieldL1;
        predModeField = ls.PredModeField;
        cbfLumaField = ls.CbfLumaField;
        puWidthIn4 = ls.PuWidthIn4;
        puHeightIn4 = ls.PuHeightIn4;
        // Restore CTB-level arrays
        tabCtDepth = ls.TabCtDepth;
        tabSkipFlag = ls.TabSkipFlag;
        tabIntraPredMode = ls.TabIntraPredMode;
        qpYTab = ls.QpYTab;
        vertBsTab = ls.VertBsTab;
        horizBsTab = ls.HorizBsTab;
        bsWidth = ls.BsWidth;
        bsHeight = ls.BsHeight;
        isIntraTab8x8 = ls.IsIntraTab8x8;
        picWidthIn8 = ls.PicWidthIn8;
        picHeightIn8 = ls.PicHeightIn8;
        isPcmTab = ls.IsPcmTab;
        isPcmWidth = ls.IsPcmWidth;
        saoTypeIdxTab = ls.SaoTypeIdxTab;
        saoEoClassTab = ls.SaoEoClassTab;
        saoBandPositionTab = ls.SaoBandPositionTab;
        saoOffsetValTab = ls.SaoOffsetValTab;
        ctuBetaOffset = ls.CtuBetaOffset;
        ctuTcOffset = ls.CtuTcOffset;
        ctuBoundaryFlags = ls.CtuBoundaryFlags;
        ctuLoopFilterAcrossSlices = ls.CtuLoopFilterAcrossSlices;
        ctuDeblockDisabled = ls.CtuDeblockDisabled;
        tabSliceAddress = ls.TabSliceAddress;
        // Restore slice/PPS state
        currentSliceHeader = ls.CurrentSliceHeader;
        currentPps = ls.CurrentPps;
        activeScalingList = ls.ActiveScalingList;
        collocatedRefDpbIdx = ls.CollocatedRefDpbIdx;
        interLayerRefDpbIdx = ls.InterLayerRefDpbIdx;
        // Restore context-level parameter sets for this layer
        if (ls.ContextActiveSps != null)
            context.ActiveSps = ls.ContextActiveSps;
        if (ls.ContextActivePps != null)
            context.ActivePps = ls.ContextActivePps;
    }
    
    /// <summary>
    /// Resolves a nuh_layer_id to a VPS layer index. Returns 0 for single-layer streams
    /// or when no VPS is available. Returns -1 if the layer is not in the VPS.
    /// </summary>
    private int ResolveLayerIndex(int nuhLayerId)
    {
        if (nuhLayerId == 0)
            return 0;
        
        var vps = context.ActiveVps;
        if (vps == null)
            return -1;
        
        return vps.LayerIdx[nuhLayerId];
    }

    /// <summary>
    /// Initializes the decoder with codec configuration data (VPS/SPS/PPS from container).
    /// </summary>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (codecPrivate.Length == 0)
        {
            // Will initialize from in-band parameter sets
            isInitialized = true;
            return true;
        }

        // Parse hvcC configuration record (HEVC Decoder Configuration Record)
        if (codecPrivate.Length >= 23 && codecPrivate[0] == 1)
        {
            return ParseHvccConfiguration(codecPrivate);
        }

        // Try parsing as raw NAL units (Annex B format)
        ParseAnnexBParameterSets(codecPrivate);
        
        isInitialized = context.ActiveSps != null;
        
        // If no SPS is active yet, activate the first PPS to chain-activate SPS → VPS.
        if (!isInitialized && context.GetPps(0) != null)
        {
            context.ActivatePps(0);
            isInitialized = context.ActiveSps != null;
        }
        
        if (isInitialized)
        {
            AllocateBuffers();
        }
        
        return isInitialized;
    }

    private bool ParseHvccConfiguration(ReadOnlySpan<byte> config)
    {
        if (config.Length < 23)
            return false;

        // hvcC structure (ISO 14496-15):
        // [0]   configurationVersion (must be 1)
        // [1]   general_profile_space/tier/profile_idc
        // [2-5] general_profile_compatibility_flags (4 bytes)
        // [6-11] general_constraint_indicator_flags (6 bytes)
        // [12]  general_level_idc
        // [13-14] min_spatial_segmentation_idc (2 bytes, lower 12 bits)
        // [15]  parallelismType (lower 2 bits)
        // [16]  chromaFormat (lower 2 bits)
        // [17]  bitDepthLumaMinus8 (lower 3 bits)
        // [18]  bitDepthChromaMinus8 (lower 3 bits)
        // [19-20] avgFrameRate (2 bytes)
        // [21]  constantFrameRate(2) | numTemporalLayers(3) | temporalIdNested(1) | lengthSizeMinusOne(2)
        // [22]  numOfArrays
        
        nalLengthSize = (config[21] & 0x03) + 1;
        int numArrays = config[22];
        int offset = 23;

        for (int i = 0; i < numArrays && offset + 3 <= config.Length; i++)
        {
            // array_completeness (1 bit) + reserved (1 bit) + NAL_unit_type (6 bits)
            var nalType = (HevcNalUnitType)(config[offset] & 0x3F);
            int numNalus = (config[offset + 1] << 8) | config[offset + 2];
            offset += 3;

            for (int j = 0; j < numNalus && offset + 2 <= config.Length; j++)
            {
                int naluLen = (config[offset] << 8) | config[offset + 1];
                offset += 2;

                if (offset + naluLen > config.Length)
                    break;

                var naluData = config.Slice(offset, naluLen);
                ProcessParameterSetNal(nalType, naluData);
                offset += naluLen;
            }
        }

        isInitialized = context.ActiveSps != null;
        
        // If no SPS is active yet (parameter sets parsed but not activated),
        // activate the first PPS to chain-activate SPS → VPS.
        if (!isInitialized && context.GetPps(0) != null)
        {
            context.ActivatePps(0);
            isInitialized = context.ActiveSps != null;
        }
        
        if (isInitialized)
        {
            AllocateBuffers();
        }
        
        return isInitialized;
    }

    private void ParseAnnexBParameterSets(ReadOnlySpan<byte> data)
    {
        foreach (var nal in HevcNalParser.ParseAnnexB(data))
        {
            context.ProcessNalUnit(nal);
        }
    }

    private void ProcessParameterSetNal(HevcNalUnitType type, ReadOnlySpan<byte> naluData)
    {
        // hvcC stores full NAL units including the 2-byte HEVC NAL header.
        // Payload must be the RBSP (after the header), consistent with ParseNalHeader.
        const int hevcNalHeaderSize = 2;
        var payload = naluData.Length > hevcNalHeaderSize
            ? naluData.Slice(hevcNalHeaderSize)
            : naluData;

        var nal = new HevcNalUnit
        {
            Type = type,
            LayerId = 0,
            TemporalIdPlus1 = 1,
            Payload = payload.ToArray()
        };
        context.ProcessNalUnit(nal);
    }

    private void AllocateBuffers()
    {
        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int bytesPerSample = context.BitDepthLuma > 8 ? 2 : 1;
        int lumaSize = width * height * bytesPerSample;
        var sps = context.ActiveSps;
        int hShift = sps?.HShiftChroma ?? 1;
        int vShift = sps?.VShiftChroma ?? 1;
        int chromaSize = (width >> hShift) * (height >> vShift) * bytesPerSample * 2; // Cb + Cr
        int frameSize = lumaSize + chromaSize;

        currentFrameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);
        Array.Clear(currentFrameBuffer, 0, currentFrameBuffer.Length);
        
        // CTB size can be 16, 32, or 64 - allocate for largest
        int maxCtbSize = 64;
        residualBuffer = new short[maxCtbSize * maxCtbSize]; // Largest CTB residual
        coefficientBuffer = new int[maxCtbSize * maxCtbSize];
        cabacContextStorage = new byte[CabacContextStorageSize];
    }

    /// <summary>
    /// Decodes a compressed HEVC access unit.
    /// </summary>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        int decodeCallNum = framesDecoded + outputQueue.Count + CountDpbOutputPending();
        
        // Parse NAL units using the appropriate format
        HevcNalUnit[] nalUnits;
        if (nalLengthSize > 0)
        {
            // Length-prefixed format (hvcC/MP4/MKV)
            nalUnits = HevcNalParser.ParseLengthPrefixed(data, nalLengthSize);
        }
        else
        {
            // Annex B format (transport streams)
            nalUnits = HevcNalParser.ParseAnnexB(data);
        }

        // Count NAL types for diagnostic
        int vclCount = 0, psCount = 0;
        HevcNalUnitType firstVclType = HevcNalUnitType.Unknown;
        foreach (var n in nalUnits)
        {
            if (n.IsVcl) { vclCount++; if (firstVclType == HevcNalUnitType.Unknown) firstVclType = n.Type; }
            if (n.IsParameterSet) psCount++;
        }

        bool frameStarted = false; // Base layer frame started (for single-layer compat and diagnostics)
        bool isIdr = false;
        
        // Clear per-layer frame tracking for this AU (FFmpeg: decode_nal_units:3707-3710)
        for (int i = 0; i < nbLayers; i++)
            layers[i].FrameStarted = false;

        // Process NAL units
        foreach (var nal in nalUnits)
        {
            nalUnitsProcessed++;
            switch (nal.Type)
            {
                case HevcNalUnitType.VideoParameterSet:
                case HevcNalUnitType.SequenceParameterSet:
                case HevcNalUnitType.PictureParameterSet:
                    context.ProcessNalUnit(nal);
                    if (currentFrameBuffer == null && context.ActiveSps != null)
                    {
                        isInitialized = true;
                        AllocateBuffers();
                    }
                    // Detect MV-HEVC from VPS extension
                    if (nal.Type == HevcNalUnitType.VideoParameterSet && context.ActiveVps is { NbLayers: >= 2 } vps && !DiagBaseLayerOnly)
                    {
                        nbLayers = vps.NbLayers;
                        // Activate all layers for decode and output (matches FFmpeg with -map 0:view:all)
                        layersActiveDecode = (1u << nbLayers) - 1;
                        layersActiveOutput = (1u << nbLayers) - 1;
                    }
                    break;

                case HevcNalUnitType.IdrWithRadl:
                case HevcNalUnitType.IdrNoLeadingPictures:
                    if (!isInitialized)
                        continue;
                    
                    {
                        // Resolve layer index from VPS (FFmpeg: decode_slice:3535)
                        int layerIdx = ResolveLayerIndex(nal.LayerId);
                        if (layerIdx < 0 || (nal.LayerId > 0 && (layersActiveDecode & (1u << layerIdx)) == 0))
                            continue;
                        
                        // Switch to the correct layer (saves/restores per-layer state)
                        if (layerIdx != curLayer)
                        {
                            // Finalize current layer before switching so inter-layer ref
                            // has post-filter data (FFmpeg applies deblock/SAO inline per-CTB,
                            // so layer 0 is already filtered when layer 1 starts)
                            if (nbLayers > 1 && layers[curLayer].FrameStarted)
                            {
                                FinalizeAndBufferFrame();
                                layers[curLayer].FrameStarted = false;
                            }
                            SwitchLayer(layerIdx);
                        }
                        
                        if (!layers[curLayer].FrameStarted)
                        {
                            // Base layer IDR handles DPB output draining and global state
                            if (curLayer == 0)
                            {
                                // Peek at no_output_of_prior_pics_flag before full parse
                                bool noOutputPrior = PeekNoOutputOfPriorPicsFlag(nal);
                                
                                int drainedCount = CountDpbOutputPending();
                                if (noOutputPrior)
                                {
                                    if (nbLayers > 1)
                                        DiscardDpbOutputMultiLayer();
                                    else
                                        DiscardDpbOutput();
                                }
                                else
                                {
                                    if (nbLayers > 1)
                                        DrainDpbOutputMultiLayer();
                                    else
                                        DrainDpbOutput();
                                }
                                
                                DiagDecodeCallLog?.Add($"  IDR: noOutputPrior={noOutputPrior}, drained/discarded={drainedCount}, outputQ={outputQueue.Count}");
                                
                                pocTid0 = 0;
                                isIdr = true;
                                lastEos = false;
                                noRaslOutputFlag = true;
                            }
                            
                            // Clear DPB AFTER draining output (FFmpeg: hevc_frame_start clears refs after output)
                            ClearDpb();
                            
                            if (BeginFrameSlice(nal, presentationTimeTicks, isIdr: curLayer == 0))
                            {
                                layers[curLayer].FrameStarted = true;
                                if (curLayer == 0) frameStarted = true;
                                
                                // After first VCL NAL activates VPS, detect MV-HEVC
                                // FFmpeg setup_multilayer: default is decode/output base layer only.
                                // Layer 1 NALs are skipped unless explicitly requested (e.g. -map 0:view:all).
                                if (curLayer == 0 && nbLayers == 1 && !DiagBaseLayerOnly && context.ActiveVps is { NbLayers: >= 2 } detectedVps)
                                {
                                    nbLayers = detectedVps.NbLayers;
                                    layersActiveDecode = (1u << nbLayers) - 1;
                                    layersActiveOutput = (1u << nbLayers) - 1;
                                    DiagDecodeCallLog?.Add($"  MV-HEVC detected: nbLayers={nbLayers}, decode/output=all layers");
                                }
                            }
                            else
                                DiagDecodeCallLog?.Add($"  IDR BeginFrameSlice FAILED layer={curLayer}");
                        }
                        else
                        {
                            DecodeAdditionalSlice(nal);
                        }
                    }
                    break;

                case HevcNalUnitType.TrailingNonReference:
                case HevcNalUnitType.TrailingReference:
                case HevcNalUnitType.TemporalSublayerAccessNonReference:
                case HevcNalUnitType.TemporalSublayerAccessReference:
                case HevcNalUnitType.StepwiseTemporalAccessNonReference:
                case HevcNalUnitType.StepwiseTemporalAccessReference:
                case HevcNalUnitType.RandomAccessDecodableLeadingNonReference:
                case HevcNalUnitType.RandomAccessDecodableLeadingReference:
                case HevcNalUnitType.RandomAccessSkippedLeadingNonReference:
                case HevcNalUnitType.RandomAccessSkippedLeadingReference:
                case HevcNalUnitType.BrokenLinkAccessWithLeadingPictures:
                case HevcNalUnitType.BrokenLinkAccessWithRadl:
                case HevcNalUnitType.BrokenLinkAccessNoLeadingPictures:
                case HevcNalUnitType.CleanRandomAccess:
                    if (!isInitialized)
                        continue;

                    {
                        // Resolve layer index from VPS (FFmpeg: decode_slice:3535)
                        int layerIdx = ResolveLayerIndex(nal.LayerId);
                        if (layerIdx < 0 || (nal.LayerId > 0 && (layersActiveDecode & (1u << layerIdx)) == 0))
                            continue;
                        
                        // Switch to the correct layer (saves/restores per-layer state)
                        if (layerIdx != curLayer)
                        {
                            // Finalize current layer before switching so inter-layer ref
                            // has post-filter data (FFmpeg applies deblock/SAO inline per-CTB,
                            // so layer 0 is already filtered when layer 1 starts)
                            if (nbLayers > 1 && layers[curLayer].FrameStarted)
                            {
                                FinalizeAndBufferFrame();
                                layers[curLayer].FrameStarted = false;
                            }
                            SwitchLayer(layerIdx);
                        }
                    }

                    // Skip RASL pictures when no_rasl_output_flag is set (FFmpeg hevcdec.c:3558-3559).
                    // RASL pictures reference frames from before the associated IRAP which may not be available.
                    bool isRasl = nal.Type is HevcNalUnitType.RandomAccessSkippedLeadingNonReference or
                                              HevcNalUnitType.RandomAccessSkippedLeadingReference;
                    if (isRasl && noRaslOutputFlag)
                        continue;

                    if (!layers[curLayer].FrameStarted)
                    {
                        bool isBla = nal.Type is HevcNalUnitType.BrokenLinkAccessWithLeadingPictures or
                                     HevcNalUnitType.BrokenLinkAccessWithRadl or
                                     HevcNalUnitType.BrokenLinkAccessNoLeadingPictures;
                        bool isCra = nal.Type == HevcNalUnitType.CleanRandomAccess;
                        
                        // Set no_rasl_output_flag for IRAP pictures (FFmpeg hevcdec.c:3296-3298).
                        // IDR and BLA always set it; CRA only when preceded by EOS.
                        if (isBla || isCra)
                            noRaslOutputFlag = isBla || (isCra && lastEos);
                        
                        // IRAP types (BLA, CRA) need DPB management like IDR.
                        // FFmpeg: new_sequence = IS_IDR || IS_BLA || last_eos
                        // Then calls ff_hevc_output_frames with no_output_of_prior_pics_flag.
                        if (curLayer == 0 && (isBla || (isCra && lastEos)))
                        {
                            // FFmpeg hevcdec.c:780-801: read no_output_of_prior_pics_flag from bitstream,
                            // then FORCE it to 1 for CRA after EOS (regardless of bitstream value).
                            bool noOutputPrior = PeekNoOutputOfPriorPicsFlag(nal);
                            if (isCra && lastEos)
                                noOutputPrior = true;
                            
                            int priorCount = CountDpbOutputPending();
                            if (noOutputPrior)
                            {
                                if (nbLayers > 1)
                                    DiscardDpbOutputMultiLayer();
                                else
                                    DiscardDpbOutput();
                            }
                            else
                            {
                                if (nbLayers > 1)
                                    DrainDpbOutputMultiLayer();
                                else
                                    DrainDpbOutput();
                            }
                            ClearDpb();
                            
                            DiagDecodeCallLog?.Add($"  IRAP: type={nal.Type}, noOutputPrior={noOutputPrior}, prior={priorCount}, {(noOutputPrior ? "discarded" : "drained")}, outQ={outputQueue.Count}, payload0=0x{(nal.Payload.Length > 0 ? nal.Payload.Span[0] : 0):X2}");
                        }
                        
                        lastEos = false;
                        
                        if (BeginFrameSlice(nal, presentationTimeTicks, isIdr: false))
                        {
                            layers[curLayer].FrameStarted = true;
                            if (curLayer == 0) frameStarted = true;
                            
                            // After first VCL NAL activates VPS, detect MV-HEVC
                            if (curLayer == 0 && nbLayers == 1 && !DiagBaseLayerOnly && context.ActiveVps is { NbLayers: >= 2 } detectedVps)
                            {
                                nbLayers = detectedVps.NbLayers;
                                layersActiveDecode = 1; // base layer only (FFmpeg default)
                                layersActiveOutput = 1; // base layer only
                                DiagDecodeCallLog?.Add($"  MV-HEVC detected: nbLayers={nbLayers}, decode/output=base only");
                            }
                        }
                        else
                        {
                            DiagDecodeCallLog?.Add($"  non-IDR BeginFrameSlice FAILED: nalType={nal.Type}, layer={curLayer}, poc={currentPoc}, dpb={dpbCount}, dpbPocs=[{string.Join(",", Enumerable.Range(0, dpbCount).Select(di => $"{dpb[di].Poc}(f={dpb[di].Flags})"))}]");
                        }
                    }
                    else
                    {
                        DecodeAdditionalSlice(nal);
                    }
                    break;

                case HevcNalUnitType.PrefixSei:
                case HevcNalUnitType.SuffixSei:
                    // SEI messages - timing, HDR metadata, etc.
                    break;

                case HevcNalUnitType.AccessUnitDelimiter:
                    // AU delimiter - marks boundary
                    break;
                    
                case HevcNalUnitType.EndOfSequence:
                case HevcNalUnitType.EndOfBitstream:
                    lastEos = true;
                    break;
            }
        }

        // Finalize all layers that started frames in this AU (FFmpeg: decode_nal_units:3786-3797)
        // For multi-layer, we finalize each layer's frame and commit to its DPB, then do multi-layer output.
        if (nbLayers > 1)
        {
            for (int layerI = 0; layerI < nbLayers; layerI++)
            {
                if (!layers[layerI].FrameStarted)
                    continue;
                
                // Switch to this layer to finalize its frame
                if (layerI != curLayer)
                    SwitchLayer(layerI);
                
                // Diagnostic: CRC of raw reconstruction before deblocking/SAO
                if (DiagDecodeCallLog != null && currentFrameBuffer != null)
                {
                    int bps = (context.ActiveSps?.BitDepthLuma ?? 8) > 8 ? 2 : 1;
                    int hShift = context.ActiveSps?.HShiftChroma ?? 1;
                    int vShift = context.ActiveSps?.VShiftChroma ?? 1;
                    int ySize = context.CodedWidth * context.CodedHeight * bps;
                    int uvSize = (context.CodedWidth >> hShift) * (context.CodedHeight >> vShift) * bps;
                    int totalSize = ySize + 2 * uvSize;
                    // Simple hash of first 64 bytes + last 64 bytes for quick comparison
                    var span = currentFrameBuffer.AsSpan(0, Math.Min(totalSize, currentFrameBuffer.Length));
                    uint h = 0;
                    for (int bi = 0; bi < Math.Min(64, span.Length); bi++) h = h * 31 + span[bi];
                    for (int bi = Math.Max(0, span.Length - 64); bi < span.Length; bi++) h = h * 31 + span[bi];
                    DiagDecodeCallLog.Add($"  Layer{layerI} pre-filter hash: 0x{h:x8} totalSize={totalSize} (sps={context.ActiveSps?.SequenceParameterSetId}, pps={context.ActivePps?.PictureParameterSetId})");
                }
                
                FinalizeAndBufferFrame();
                
                // Remove borrowed inter-layer ref from this layer's DPB after finalization
                RemoveInterLayerRef();
            }
            
            // Multi-layer output: scan both layers' DPBs for lowest-POC frames
            TryOutputReorderedFrameMultiLayer();
            
            DiagDecodeCallLog?.Add($"Decode#{decodeCallNum}: nals={nalUnits.Length}(vcl={vclCount},ps={psCount}), type={firstVclType}, layers={nbLayers}, layer0Started={layers[0].FrameStarted}, layer1Started={layers[1].FrameStarted}, poc={currentPoc}, outQ={outputQueue.Count}");
        }
        else if (frameStarted)
        {
            // Diagnostic: pre-filter hash for single-layer path (comparison with multi-layer)
            if (DiagDecodeCallLog != null && currentFrameBuffer != null)
            {
                int bps = (context.ActiveSps?.BitDepthLuma ?? 8) > 8 ? 2 : 1;
                int hShift = context.ActiveSps?.HShiftChroma ?? 1;
                int vShift = context.ActiveSps?.VShiftChroma ?? 1;
                int ySize = context.CodedWidth * context.CodedHeight * bps;
                int uvSize = (context.CodedWidth >> hShift) * (context.CodedHeight >> vShift) * bps;
                int totalSize = ySize + 2 * uvSize;
                var span = currentFrameBuffer.AsSpan(0, Math.Min(totalSize, currentFrameBuffer.Length));
                uint h = 0;
                for (int bi = 0; bi < Math.Min(64, span.Length); bi++) h = h * 31 + span[bi];
                for (int bi = Math.Max(0, span.Length - 64); bi < span.Length; bi++) h = h * 31 + span[bi];
                DiagDecodeCallLog.Add($"  SingleLayer pre-filter hash: 0x{h:x8} totalSize={totalSize} poc={currentPoc}");
            }
            
            bool picOutput = currentSliceHeader?.PicOutputFlag ?? true;
            FinalizeAndBufferFrame();
            
            // Bump output-pending DPB frames as needed (all go to outputQueue for correct ordering)
            TryOutputReorderedFrame();
            
            DiagDecodeCallLog?.Add($"Decode#{decodeCallNum}: nals={nalUnits.Length}(vcl={vclCount},ps={psCount}), type={firstVclType}, frameStarted=True, poc={currentPoc}, picOut={picOutput}, dpb={dpbCount}, outQ={outputQueue.Count}");
        }
        else
        {
            DiagDecodeCallLog?.Add($"Decode#{decodeCallNum}: nals={nalUnits.Length}(vcl={vclCount},ps={psCount}), type={firstVclType}, frameStarted=False, dpb={dpbCount}, outQ={outputQueue.Count}");
        }

        // Return the oldest queued frame (maintains correct display order)
        if (outputQueue.Count > 0)
        {
            DiagDecodeCallLog?.Add($"  → returning from outputQueue ({outputQueue.Count} remaining)");
            return outputQueue.Dequeue();
        }

        DiagDecodeCallLog?.Add($"  → returning null");
        return null;
    }

    /// <summary>
    /// Initializes a new frame and decodes the first slice's CTUs.
    /// Sets up POC, reference lists, and MV fields.
    /// </summary>
    private bool BeginFrameSlice(HevcNalUnit nal, long pts, bool isIdr)
    {
        // Parse slice header (also activates PPS→SPS→VPS chain from in-band parameter sets)
        var sliceHeader = context.ParseSliceHeader(nal);
        
        if (sliceHeader == null || context.ActiveSps == null || context.ActivePps == null)
        {
            DiagDecodeCallLog?.Add($"  BeginFrame: sliceHeader={sliceHeader != null}, activeSps={context.ActiveSps != null}, activePps={context.ActivePps != null}, payloadLen={nal.Payload.Length}");
            return false;
        }

        var sps = context.ActiveSps;
        var pps = context.ActivePps;
        
        // Detect SPS change: if coded dimensions changed, DPB refs are incompatible
        int newWidth = context.CodedWidth;
        int newHeight = context.CodedHeight;
        if (activeSpsWidth != 0 && (newWidth != activeSpsWidth || newHeight != activeSpsHeight))
        {
            ClearDpb();
            // Force buffer reallocation
            if (currentFrameBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(currentFrameBuffer);
                currentFrameBuffer = null;
            }
        }
        activeSpsWidth = newWidth;
        activeSpsHeight = newHeight;
        
        // Detect SPS change that affects per-picture arrays (CTB size, min CB, etc.)
        // Even if dimensions are unchanged, a new SPS can have different CTB parameters
        // which would make saoTypeIdxTab, ctuBetaOffset, tabCtDepth etc. wrong size.
        if (lastAllocatedSps != null && !ReferenceEquals(sps, lastAllocatedSps))
        {
            tabCtDepth = null; // triggers AllocatePerPictureArrays on next CTU decode
            lastAllocatedSps = null;
        }
        
        // Update max reorder depth and DPB capacity from SPS (highest temporal sublayer)
        int maxSublayer = Math.Min(sps.MaxSubLayersMinus1, sps.MaxNumReorderPics.Length - 1);
        maxNumReorderFrames = sps.MaxNumReorderPics[maxSublayer];
        maxDecPicBuffering = sps.MaxDecPicBufferingMinus1[maxSublayer] + 1;

        // Calculate POC
        int poc = CalculatePoc(sps, sliceHeader, isIdr);
        currentPoc = poc;
        
        DiagDecodeCallLog?.Add($"  BeginFrame: poc={poc}, nalType={nal.Type}, lsb={sliceHeader.PicOrderCntLsb}, dpb={dpbCount}");
        
        // Fix up long-term RPS POCs using FFmpeg's formula (decode_lt_rps in hevcdec.c).
        // The parser stores raw POC LSB + accumulated delta MSB. Now that we have the full POC,
        // compute: fullPoc = pocLsb + curPoc - delta * maxPocLsb - curPocLsb
        var ltRps = sliceHeader.LongTermRps;
        if (ltRps.NumRefs > 0)
        {
            int maxPocLsb = sps.MaxPicOrderCntLsb;
            int curPocLsb = sliceHeader.PicOrderCntLsb;
            for (int i = 0; i < ltRps.NumRefs; i++)
            {
                if (ltRps.PocMsbPresent[i])
                    ltRps.Poc[i] = ltRps.PocLsb[i] + poc - ltRps.DeltaPocMsb[i] * maxPocLsb - curPocLsb;
                // When PocMsbPresent is false, Poc[i] stays as the raw LSB (correct for LSB-only matching)
            }
            DiagDecodeCallLog?.Add($"  LT-RPS: poc={poc}, numLtRefs={ltRps.NumRefs}, " +
                string.Join(", ", Enumerable.Range(0, ltRps.NumRefs).Select(i =>
                    $"[{i}]poc={ltRps.Poc[i]},lsb={ltRps.PocLsb[i]},msb={ltRps.PocMsbPresent[i]},delta={ltRps.DeltaPocMsb[i]},used={ltRps.UsedByCurrPic[i]}")));
        }
        // Non-reference types have bit 0 = 0 in their enum value (for types 0-15)
        currentIsReference = nal.Type != HevcNalUnitType.TrailingNonReference && 
                            nal.Type != HevcNalUnitType.TemporalSublayerAccessNonReference &&
                            nal.Type != HevcNalUnitType.StepwiseTemporalAccessNonReference &&
                            nal.Type != HevcNalUnitType.RandomAccessDecodableLeadingNonReference &&
                            nal.Type != HevcNalUnitType.RandomAccessSkippedLeadingNonReference;
        
        // Update pocTid0 for TID0 non-RASL/RADL/sub-layer-non-reference pictures (spec 8.3.1)
        // FFmpeg: hevc_frame_start() only updates poc_tid0 under these conditions
        if (nal.TemporalId == 0 &&
            nal.Type != HevcNalUnitType.TrailingNonReference &&
            nal.Type != HevcNalUnitType.TemporalSublayerAccessNonReference &&
            nal.Type != HevcNalUnitType.StepwiseTemporalAccessNonReference &&
            nal.Type != HevcNalUnitType.RandomAccessDecodableLeadingNonReference &&
            nal.Type != HevcNalUnitType.RandomAccessDecodableLeadingReference &&
            nal.Type != HevcNalUnitType.RandomAccessSkippedLeadingNonReference &&
            nal.Type != HevcNalUnitType.RandomAccessSkippedLeadingReference)
        {
            pocTid0 = poc;
        }
        
        currentPts = pts;

        // Ensure frame buffer is allocated
        if (currentFrameBuffer == null)
        {
            AllocateBuffers();
        }
        
        // Allocate per-PU motion vector field if needed
        int codedW = context.CodedWidth;
        int codedH = context.CodedHeight;
        int newPuW4 = (codedW + 3) / 4;
        int newPuH4 = (codedH + 3) / 4;
        if (puWidthIn4 != newPuW4 || puHeightIn4 != newPuH4)
        {
            puWidthIn4 = newPuW4;
            puHeightIn4 = newPuH4;
            int fieldSize = puWidthIn4 * puHeightIn4;
            mvFieldL0X = new short[fieldSize];
            mvFieldL0Y = new short[fieldSize];
            mvFieldL1X = new short[fieldSize];
            mvFieldL1Y = new short[fieldSize];
            refIdxFieldL0 = new sbyte[fieldSize];
            refIdxFieldL1 = new sbyte[fieldSize];
            predModeField = new byte[fieldSize];
            cbfLumaField = new byte[fieldSize];
        }
        
        // Clear per-frame fields (matches FFmpeg's per-frame reallocation pattern)
        if (mvFieldL0X != null)
        {
            Array.Clear(mvFieldL0X);
            Array.Clear(mvFieldL0Y);
            Array.Clear(mvFieldL1X);
            Array.Clear(mvFieldL1Y);
            Array.Fill(refIdxFieldL0!, (sbyte)-1);
            Array.Fill(refIdxFieldL1!, (sbyte)-1);
            Array.Clear(predModeField!);
            Array.Clear(cbfLumaField!);
        }
        
        // Clear QP table so neighbor lookups don't read stale values from previous frame
        if (qpYTab != null)
            Array.Clear(qpYTab);

        // Build reference picture lists for P/B slices
        currentSliceHeader = sliceHeader;
        currentPps = pps;
        
        // Initialize per-CTB ref list accumulation for multi-slice frames (FFmpeg's rpl_tab)
        int totalCtbs = context.PicWidthInCtbsY * context.PicHeightInCtbsY;
        currentFrameSliceRefLists = new List<SliceRefListSnapshot>(4);
        if (currentFramePerCtbSliceIdx == null || currentFramePerCtbSliceIdx.Length < totalCtbs)
            currentFramePerCtbSliceIdx = new byte[totalCtbs];
        else
            Array.Clear(currentFramePerCtbSliceIdx, 0, totalCtbs);
        
        // Resolve active scaling list: PPS overrides SPS, SPS provides defaults
        if (sps.ScalingListEnabled)
            activeScalingList = pps.ScalingList ?? sps.ScalingList;
        else
            activeScalingList = null;
        
        if (sliceHeader.SliceType != HevcSliceType.ISlice)
        {
            if (!BuildRefPicLists(sliceHeader))
                return false; // Missing refs for non-IRAP frame (e.g., RASL after CRA) — skip
            
            // Snapshot first slice's ref lists and fill per-CTB indices
            SnapshotSliceRefLists(sliceHeader.SliceSegmentAddress, totalCtbs);
            
            // Determine collocated reference frame for temporal MVP
            collocatedRefDpbIdx = -1;
            if (sliceHeader.SliceTemporalMvpEnabled)
            {
                int colRefIdx = sliceHeader.CollocatedRefIdx;
                int[] colList = sliceHeader.CollocatedFromL0Flag ? refPicList0 : refPicList1;
                int colListCount = sliceHeader.CollocatedFromL0Flag ? numRefList0 : numRefList1;
                if (colRefIdx >= 0 && colRefIdx < colListCount)
                    collocatedRefDpbIdx = colList[colRefIdx];
            }
        }
        else
        {
            // I-slice: add an empty snapshot so per-CTB array has a valid entry
            currentFrameSliceRefLists.Add(new SliceRefListSnapshot(
                ReadOnlySpan<int>.Empty, 0, ReadOnlySpan<bool>.Empty,
                ReadOnlySpan<int>.Empty, 0, ReadOnlySpan<bool>.Empty));
            // All CTBs default to index 0 (already cleared)
        }
        
        // Log reference lists for diagnostics
        if (DiagRefListLog != null)
        {
            int[] l0 = new int[numRefList0];
            int[] l1 = new int[numRefList1];
            for (int i = 0; i < numRefList0; i++) l0[i] = refPocList0[i];
            for (int i = 0; i < numRefList1; i++) l1[i] = refPocList1[i];
            DiagRefListLog.Add((diagPicCounter, currentPoc, l0, l1));
        }

        // Decode first slice's CTUs
        DecodeSliceCtus(nal, sliceHeader, sps, pps);
        return true;
    }

    /// <summary>
    /// Decodes CTUs from a subsequent (non-first) slice in a multi-slice frame.
    /// Each slice has its own header but shares the frame buffer.
    /// </summary>
    private void DecodeAdditionalSlice(HevcNalUnit nal)
    {
        var sliceHeader = context.ParseSliceHeader(nal);
        if (sliceHeader == null || context.ActiveSps == null || context.ActivePps == null)
            return;

        var sps = context.ActiveSps;
        var pps = context.ActivePps;
        
        // Update current slice header (may have different QP, ref lists, etc.)
        currentSliceHeader = sliceHeader;
        currentPps = pps;
        
        // Rebuild reference lists if this slice is inter-predicted
        if (sliceHeader.SliceType != HevcSliceType.ISlice)
        {
            if (!BuildRefPicLists(sliceHeader))
                return; // Missing refs — skip this slice
            
            // Snapshot this slice's ref lists and fill per-CTB indices
            int totalCtbs = context.PicWidthInCtbsY * context.PicHeightInCtbsY;
            SnapshotSliceRefLists(sliceHeader.SliceSegmentAddress, totalCtbs);
            
            collocatedRefDpbIdx = -1;
            if (sliceHeader.SliceTemporalMvpEnabled)
            {
                int colRefIdx = sliceHeader.CollocatedRefIdx;
                int[] colList = sliceHeader.CollocatedFromL0Flag ? refPicList0 : refPicList1;
                int colListCount = sliceHeader.CollocatedFromL0Flag ? numRefList0 : numRefList1;
                if (colRefIdx >= 0 && colRefIdx < colListCount)
                    collocatedRefDpbIdx = colList[colRefIdx];
            }
        }
        else
        {
            // I-slice in non-first position: add empty snapshot
            int totalCtbs = context.PicWidthInCtbsY * context.PicHeightInCtbsY;
            if (currentFrameSliceRefLists != null)
            {
                int sliceIdx = currentFrameSliceRefLists.Count;
                currentFrameSliceRefLists.Add(new SliceRefListSnapshot(
                    ReadOnlySpan<int>.Empty, 0, ReadOnlySpan<bool>.Empty,
                    ReadOnlySpan<int>.Empty, 0, ReadOnlySpan<bool>.Empty));
                // Fill from slice start to end of frame (like FFmpeg's init_slice_rpl)
                if (currentFramePerCtbSliceIdx != null)
                    for (int c = sliceHeader.SliceSegmentAddress; c < totalCtbs; c++)
                        currentFramePerCtbSliceIdx[c] = (byte)sliceIdx;
            }
        }
        
        DecodeSliceCtus(nal, sliceHeader, sps, pps);
    }

    /// <summary>
    /// Completes decoding (filtering, reference storage, RPS processing).
    /// Adds the frame to the DPB with REF + OUTPUT flags matching FFmpeg's unified model.
    /// </summary>
    private void FinalizeAndBufferFrame()
    {
        var pps = context.ActivePps!;
        var sps = context.ActiveSps!;
        
        // Detailed finalization diagnostics: full checksums of buffer and filter tables
        if (DiagDecodeCallLog != null && currentFrameBuffer != null)
        {
            int bps = sps.BitDepthLuma > 8 ? 2 : 1;
            int w = sps.PictureWidthInLumaSamples;
            int h = sps.PictureHeightInLumaSamples;
            int cw = w >> sps.HShiftChroma;
            int ch = h >> sps.VShiftChroma;
            int totalBytes = (w * h + 2 * cw * ch) * bps;
            
            // Full buffer checksum (covers every byte)
            long bufSum = 0;
            var bspan = currentFrameBuffer.AsSpan(0, Math.Min(totalBytes, currentFrameBuffer.Length));
            for (int i = 0; i < bspan.Length; i++) bufSum += bspan[i];
            
            // SAO table hash
            long saoHash = 0;
            if (saoTypeIdxTab != null)
                for (int i = 0; i < saoTypeIdxTab.Length; i++) saoHash = saoHash * 31 + saoTypeIdxTab[i];
            
            // Deblocking BS hash
            long bsHash = 0;
            if (vertBsTab != null)
                for (int i = 0; i < vertBsTab.Length; i++) bsHash = bsHash * 31 + vertBsTab[i];
            if (horizBsTab != null)
                for (int i = 0; i < horizBsTab.Length; i++) bsHash = bsHash * 31 + horizBsTab[i];
            
            // QP table hash
            long qpHash = 0;
            if (qpYTab != null)
                for (int i = 0; i < qpYTab.Length; i++) qpHash = qpHash * 31 + qpYTab[i];
            
            DiagDecodeCallLog.Add($"  Finalize poc={currentPoc}: preFilter bufSum={bufSum} saoHash=0x{saoHash:x} bsHash=0x{bsHash:x} qpHash=0x{qpHash:x} sps={sps.SequenceParameterSetId} pps={pps.PictureParameterSetId} saoEnabled={sps.SampleAdaptiveOffsetEnabled} deblockDisabled={currentSliceHeader?.SliceDeblockingFilterDisabled}");
        }
        
        // Apply deblocking filter (uses first slice's header for beta/tc offsets)
        if (!DiagSkipDeblocking)
            ApplyDeblocking(pps, currentSliceHeader!);

        // Post-deblocking diagnostic
        if (DiagDecodeCallLog != null && currentFrameBuffer != null)
        {
            int bps = sps.BitDepthLuma > 8 ? 2 : 1;
            int w = sps.PictureWidthInLumaSamples;
            int h = sps.PictureHeightInLumaSamples;
            int cw = w >> sps.HShiftChroma;
            int ch = h >> sps.VShiftChroma;
            int totalBytes = (w * h + 2 * cw * ch) * bps;
            long bufSum = 0;
            var bspan = currentFrameBuffer.AsSpan(0, Math.Min(totalBytes, currentFrameBuffer.Length));
            for (int i = 0; i < bspan.Length; i++) bufSum += bspan[i];
            DiagDecodeCallLog.Add($"  Finalize poc={currentPoc}: postDeblock bufSum={bufSum}");
        }

        // Apply SAO filter if enabled
        if (sps.SampleAdaptiveOffsetEnabled && !DiagSkipSao)
        {
            ApplySao();
        }
        
        // Post-SAO diagnostic
        if (DiagDecodeCallLog != null && currentFrameBuffer != null)
        {
            int bps = sps.BitDepthLuma > 8 ? 2 : 1;
            int w = sps.PictureWidthInLumaSamples;
            int h = sps.PictureHeightInLumaSamples;
            int cw = w >> sps.HShiftChroma;
            int ch = h >> sps.VShiftChroma;
            int totalBytes = (w * h + 2 * cw * ch) * bps;
            long bufSum = 0;
            var bspan = currentFrameBuffer.AsSpan(0, Math.Min(totalBytes, currentFrameBuffer.Length));
            for (int i = 0; i < bspan.Length; i++) bufSum += bspan[i];
            DiagDecodeCallLog.Add($"  Finalize poc={currentPoc}: postSao bufSum={bufSum}");
        }

        // Clear isPcmTab after both deblocking and SAO have used it
        if (isPcmTab != null) Array.Clear(isPcmTab);

        // Store in DPB with flags and process RPS — matches FFmpeg's set_new_ref + frame_rps flow.
        // The frame gets FrameFlagShortRef always, plus FrameFlagOutput if pic_output_flag.
        StoreReferenceFrame();
    }

    /// <summary>
    /// Computes the Picture Order Count (POC) for the current picture.
    /// Matches FFmpeg's ff_hevc_compute_poc: derives prev_poc_lsb/msb from pocTid0,
    /// not from per-frame state. pocTid0 is updated in BeginFrameSlice for TID0 frames.
    /// </summary>
    private int CalculatePoc(HevcSequenceParameterSet sps, HevcSliceSegmentHeader sliceHeader, bool isIdr)
    {
        if (isIdr)
        {
            pocTid0 = 0;
            return 0;
        }

        int maxPocLsb = sps.MaxPicOrderCntLsb;
        int pocLsb = sliceHeader.PicOrderCntLsb;

        // Derive previous POC LSB and MSB from pocTid0 (spec 8.3.1, FFmpeg ff_hevc_compute_poc)
        int prevPocLsb = pocTid0 % maxPocLsb;
        int prevPocMsb = pocTid0 - prevPocLsb;

        int pocMsb;
        if (pocLsb < prevPocLsb && (prevPocLsb - pocLsb) >= maxPocLsb / 2)
            pocMsb = prevPocMsb + maxPocLsb;
        else if (pocLsb > prevPocLsb && (pocLsb - prevPocLsb) > maxPocLsb / 2)
            pocMsb = prevPocMsb - maxPocLsb;
        else
            pocMsb = prevPocMsb;

        // BLA and IDR pictures reset POC MSB to 0 (spec 8.3.1, FFmpeg ff_hevc_compute_poc).
        // This is reached for layer>0 IDR which gets isIdr=false but still needs poc_msb=0.
        if (sliceHeader.NalType is HevcNalUnitType.BrokenLinkAccessWithLeadingPictures or
            HevcNalUnitType.BrokenLinkAccessWithRadl or
            HevcNalUnitType.BrokenLinkAccessNoLeadingPictures or
            HevcNalUnitType.IdrWithRadl or
            HevcNalUnitType.IdrNoLeadingPictures)
            pocMsb = 0;

        return pocMsb + pocLsb;
    }

    /// <summary>
    /// Builds reference picture lists (List0, List1) from the slice's RPS and current DPB.
    /// <summary>
    /// Adds layer 0's current frame to the current layer's DPB as a borrowed inter-layer reference.
    /// The frame's pixel buffer and MV data are shared (not copied).
    /// Matches FFmpeg's add_candidate_ref for inter-layer refs (refs.c:588-604).
    /// </summary>
    private int AddInterLayerRefToDpb(HevcLayerState layer0)
    {
        // Find a free DPB slot
        int slot = -1;
        for (int i = 0; i < dpb.Length; i++)
        {
            if (dpb[i].Flags == 0 && dpb[i].Buffer == null)
            {
                slot = i;
                break;
            }
        }
        
        if (slot < 0)
            return -1; // DPB full — shouldn't happen in practice
        
        // Create a borrowed entry sharing layer 0's frame data
        ref var entry = ref dpb[slot];
        entry.Buffer = layer0.CurrentFrameBuffer;
        entry.Poc = layer0.CurrentPoc;
        entry.PresentationTimeTicks = layer0.CurrentPts;
        entry.Flags = FrameFlagShortRef; // inter-layer refs are SHORT_REF in DPB (FFmpeg refs.c:599)
        
        // Use layer 0's saved MV working arrays for temporal MVP from inter-layer ref.
        // These were saved during SwitchLayer(1) — they represent layer 0's in-progress frame
        // which hasn't been committed to its DPB yet.
        entry.MvL0X = layer0.MvFieldL0X;
        entry.MvL0Y = layer0.MvFieldL0Y;
        entry.MvL1X = layer0.MvFieldL1X;
        entry.MvL1Y = layer0.MvFieldL1Y;
        entry.RefIdxL0 = layer0.RefIdxFieldL0;
        entry.RefIdxL1 = layer0.RefIdxFieldL1;
        entry.PredFlags = layer0.PredModeField;
        entry.PuWidthIn4 = layer0.PuWidthIn4;
        entry.PuHeightIn4 = layer0.PuHeightIn4;
        entry.PerCtbSliceRefIdx = layer0.FramePerCtbSliceIdx;
        entry.SliceRefListSnapshots = layer0.FrameSliceRefLists?.ToArray();
        entry.NumRefList0 = 0; // Ref list counts not needed for inter-layer ref
        entry.NumRefList1 = 0;
        
        // Copy display metadata for motion compensation
        entry.DisplayWidth = activeSpsWidth;
        entry.DisplayHeight = activeSpsHeight;
        entry.CodedWidth = activeSpsWidth;
        entry.CodedHeight = activeSpsHeight;
        entry.BytesPerSample = context.ActiveSps?.BitDepthLuma > 8 ? 2 : 1;
        entry.ChromaFormat = context.ActiveSps?.ChromaFormatIdc ?? HevcChromaFormat.Chroma420;
        
        if (slot >= dpbCount)
            dpbCount = slot + 1;
        
        return slot;
    }
    
    /// <summary>
    /// Removes the inter-layer borrowed reference from the current layer's DPB.
    /// Called after frame finalization to avoid DPB pollution.
    /// The buffer is NOT returned to ArrayPool since it's borrowed from another layer.
    /// </summary>
    private void RemoveInterLayerRef()
    {
        DiagDecodeCallLog?.Add($"  RemoveILRef: idx={interLayerRefDpbIdx} dpbCount={dpbCount}");
        if (interLayerRefDpbIdx >= 0 && interLayerRefDpbIdx < dpbCount)
        {
            // Null the buffer reference (borrowed, not owned — do NOT return to pool)
            dpb[interLayerRefDpbIdx].Buffer = null;
            // Compact the DPB array (remove the gap)
            for (int i = interLayerRefDpbIdx; i < dpbCount - 1; i++)
                dpb[i] = dpb[i + 1];
            dpb[--dpbCount] = default;
            DiagDecodeCallLog?.Add($"  RemoveILRef: removed at {interLayerRefDpbIdx}, dpbCount now {dpbCount}");
            interLayerRefDpbIdx = -1;
        }
        else
        {
            DiagDecodeCallLog?.Add($"  RemoveILRef: SKIPPED (invalid index or out of bounds)");
        }
    }

    /// <summary>
    /// Builds reference picture lists from the DPB and current slice's RPS.
    /// Follows ffmpeg's ff_hevc_frame_rps + ff_hevc_slice_rpl logic.
    /// Returns false if a non-IRAP picture has unavailable references (frame should be skipped).
    /// </summary>
    private bool BuildRefPicLists(HevcSliceSegmentHeader sliceHeader)
    {
        numRefList0 = 0;
        numRefList1 = 0;
        
        // Clean up any missing refs from previous frame (FFmpeg's unref_missing_refs)
        UnrefMissingRefs();
        
        var rps = sliceHeader.ShortTermRps;
        if (rps == null && !sliceHeader.InterLayerPred)
            return true;
        
        // Determine if current picture is IRAP (types 16-23: BLA, IDR, CRA)
        bool isIrap = sliceHeader.NalType >= HevcNalUnitType.BrokenLinkAccessWithLeadingPictures &&
                      sliceHeader.NalType <= HevcNalUnitType.CleanRandomAccess;
        
        // Classify RPS entries into ST_CURR_BEF, ST_CURR_AFT, ST_FOLL
        // ST_CURR_BEF: used entries with negative delta POC (before current in display order)
        // ST_CURR_AFT: used entries with non-negative delta POC (after current)
        Span<int> stCurrBef = stackalloc int[HevcConstants.MaxRefs];
        Span<int> stCurrAft = stackalloc int[HevcConstants.MaxRefs];
        int numStCurrBef = 0;
        int numStCurrAft = 0;
        
        // Also process ST_FOLL entries — they must also be in DPB (FFmpeg adds them too)
        int rpsNumDeltaPocs = rps?.NumDeltaPocs ?? 0;
        for (int i = 0; i < rpsNumDeltaPocs; i++)
        {
            int poc = currentPoc + rps!.DeltaPoc[i];
            int dpbIdx = FindDpbByPoc(poc);
            
            if (dpbIdx < 0)
            {
                // FFmpeg: add_candidate_ref rejects non-IRAP frames with unavailable refs
                // (refs.c:506-517). This catches RASL pictures whose references
                // from before the associated CRA/BLA are not yet in the DPB.
                if (!isIrap && (rps!.Used & (1u << i)) != 0)
                {
                    DiagDecodeCallLog?.Add($"  BuildRefList: ST ref NOT FOUND poc={poc} (delta={rps!.DeltaPoc[i]}) curPoc={currentPoc} nalType={sliceHeader.NalType} dpb=[{string.Join(",", Enumerable.Range(0, dpbCount).Select(j => $"{dpb[j].Poc}(f={dpb[j].Flags:x})"))}]");
                    return false;
                }
                
                // IRAP or ST_FOLL (unused): missing ref is expected, just skip this entry
                continue;
            }
            
            if ((rps!.Used & (1u << i)) == 0)
                continue; // ST_FOLL — not used by current picture for ref list
            
            if (i < rps!.NumNegativePics)
                stCurrBef[numStCurrBef++] = dpbIdx;
            else
                stCurrAft[numStCurrAft++] = dpbIdx;
        }
        
        // Also handle long-term refs
        var ltRps = sliceHeader.LongTermRps;
        Span<int> ltCurr = stackalloc int[HevcConstants.MaxRefs];
        int numLtCurr = 0;
        for (int i = 0; i < ltRps.NumRefs; i++)
        {
            int dpbIdx = FindDpbByPoc(ltRps.Poc[i], ltRps.PocMsbPresent[i]);
            
            if (dpbIdx < 0)
            {
                DiagDecodeCallLog?.Add($"  BuildRefList: LT ref NOT FOUND poc={ltRps.Poc[i]} lsb={ltRps.PocLsb[i]} msb={ltRps.PocMsbPresent[i]} used={ltRps.UsedByCurrPic[i]} curPoc={currentPoc} dpb=[{string.Join(",", Enumerable.Range(0, dpbCount).Select(j => $"{dpb[j].Poc}(f={dpb[j].Flags:x})"))}]");
                // Same logic as short-term: reject non-IRAP frames with missing used refs
                if (!isIrap && ltRps.UsedByCurrPic[i])
                    return false;
                
                // IRAP or unused LT ref: just skip
                continue;
            }
            
            if (!ltRps.UsedByCurrPic[i])
                continue;
            
            ltCurr[numLtCurr++] = dpbIdx;
        }
        
        // Inter-layer ref (MV-HEVC F.8.1.6): add layer 0's current frame as ref for layer 1.
        // In FFmpeg, this is done in ff_hevc_frame_rps at label "inter_layer:" (refs.c:588-604).
        Span<int> interLayer0 = stackalloc int[1];
        int numInterLayer0 = 0;
        interLayerRefDpbIdx = -1;
        
        if (sliceHeader.InterLayerPred && curLayer > 0)
        {
            var l0 = layers[0];
            if (l0.CurrentFrameBuffer != null)
            {
                // Add layer 0's current frame to this layer's DPB as a borrowed entry.
                // This allows normal DPB-index-based ref list access for motion compensation.
                int ilIdx = AddInterLayerRefToDpb(l0);
                if (ilIdx >= 0)
                {
                    interLayerRefDpbIdx = ilIdx;
                    interLayer0[0] = ilIdx;
                    numInterLayer0 = 1;
                    DiagDecodeCallLog?.Add($"  InterLayerRef: added layer0 frame poc={dpb[ilIdx].Poc} at dpbIdx={ilIdx}");
                }
            }
        }
        
        // Diagnostic: log candidate lists
        if (DiagDecodeCallLog != null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"  RefCand: poc={currentPoc} stBef=[");
            for (int i = 0; i < numStCurrBef; i++) { if (i > 0) sb.Append(','); sb.Append(stCurrBef[i] >= 0 && stCurrBef[i] < dpbCount ? dpb[stCurrBef[i]].Poc : -1); }
            sb.Append($"]({numStCurrBef}) stAft=[");
            for (int i = 0; i < numStCurrAft; i++) { if (i > 0) sb.Append(','); sb.Append(stCurrAft[i] >= 0 && stCurrAft[i] < dpbCount ? dpb[stCurrAft[i]].Poc : -1); }
            sb.Append($"]({numStCurrAft}) lt=[");
            for (int i = 0; i < numLtCurr; i++) { if (i > 0) sb.Append(','); sb.Append(ltCurr[i] >= 0 && ltCurr[i] < dpbCount ? dpb[ltCurr[i]].Poc : -1); }
            sb.Append($"]({numLtCurr}) il0=[");
            for (int i = 0; i < numInterLayer0; i++) { if (i > 0) sb.Append(','); sb.Append(interLayer0[i] >= 0 && interLayer0[i] < dpbCount ? dpb[interLayer0[i]].Poc : -1); }
            sb.Append($"]({numInterLayer0}) numActive=L0:{sliceHeader.NumRefIdxL0Active} L1:{sliceHeader.NumRefIdxL1Active}");
            DiagDecodeCallLog.Add(sb.ToString());
        }

        // Build temporary candidate lists matching FFmpeg's ff_hevc_slice_rpl:
        // The temp list contains ALL candidates (at least one full cycle), which may
        // exceed numRefActive. Modification indices reference this full temp list.
        // After modification, we truncate to numRefActive.
        //
        // MV-HEVC order (F.8.1.5, FFmpeg refs.c:367-374):
        //   L0: ST_CURR_BEF, INTER_LAYER0, ST_CURR_AFT, LT_CURR
        //   L1: ST_CURR_AFT, ST_CURR_BEF, LT_CURR, INTER_LAYER0
        Span<int> tempList0 = stackalloc int[HevcConstants.MaxRefs];
        Span<bool> tempIsLt0 = stackalloc bool[HevcConstants.MaxRefs];
        int tempCount0 = 0;
        BuildRefListFull(sliceHeader.NumRefIdxL0Active,
            stCurrBef, numStCurrBef, stCurrAft, numStCurrAft, ltCurr, numLtCurr,
            tempList0, tempIsLt0, out tempCount0,
            interLayer0, numInterLayer0, interLayer0AfterFirst: true);

        // Apply ref_pic_list_modification for L0 if present (spec 8.3.4)
        if (sliceHeader.RplModificationFlag[0] && sliceHeader.ListEntryLx[0].Length > 0)
        {
            numRefList0 = 0;
            for (int i = 0; i < sliceHeader.NumRefIdxL0Active; i++)
            {
                int idx = sliceHeader.ListEntryLx[0][i];
                if (idx < tempCount0)
                {
                    refPicList0[numRefList0] = tempList0[idx];
                    refIsLongTerm0[numRefList0] = tempIsLt0[idx];
                    numRefList0++;
                }
            }
        }
        else
        {
            // No modification: take first numRefActive entries from temp list
            numRefList0 = Math.Min(sliceHeader.NumRefIdxL0Active, tempCount0);
            for (int i = 0; i < numRefList0; i++)
            {
                refPicList0[i] = tempList0[i];
                refIsLongTerm0[i] = tempIsLt0[i];
            }
        }

        // Build List 1: ST_CURR_AFT, ST_CURR_BEF, LT_CURR (spec 8-10)
        if (sliceHeader.SliceType == HevcSliceType.BSlice)
        {
            Span<int> tempList1 = stackalloc int[HevcConstants.MaxRefs];
            Span<bool> tempIsLt1 = stackalloc bool[HevcConstants.MaxRefs];
            int tempCount1 = 0;
            BuildRefListFull(sliceHeader.NumRefIdxL1Active,
                stCurrAft, numStCurrAft, stCurrBef, numStCurrBef, ltCurr, numLtCurr,
                tempList1, tempIsLt1, out tempCount1,
                interLayer0, numInterLayer0, interLayer0AfterFirst: false);

            // Apply ref_pic_list_modification for L1 if present
            if (sliceHeader.RplModificationFlag[1] && sliceHeader.ListEntryLx[1].Length > 0)
            {
                numRefList1 = 0;
                for (int i = 0; i < sliceHeader.NumRefIdxL1Active; i++)
                {
                    int idx = sliceHeader.ListEntryLx[1][i];
                    if (idx < tempCount1)
                    {
                        refPicList1[numRefList1] = tempList1[idx];
                        refIsLongTerm1[numRefList1] = tempIsLt1[idx];
                        numRefList1++;
                    }
                }
            }
            else
            {
                numRefList1 = Math.Min(sliceHeader.NumRefIdxL1Active, tempCount1);
                for (int i = 0; i < numRefList1; i++)
                {
                    refPicList1[i] = tempList1[i];
                    refIsLongTerm1[i] = tempIsLt1[i];
                }
            }
        }
        
        // Resolve ref_idx → POC for boundary strength computation
        for (int i = 0; i < numRefList0; i++)
        {
            int dpbIdx = refPicList0[i];
            refPocList0[i] = (dpbIdx >= 0 && dpbIdx < dpbCount) ? dpb[dpbIdx].Poc : -1;
        }
        for (int i = 0; i < numRefList1; i++)
        {
            int dpbIdx = refPicList1[i];
            refPocList1[i] = (dpbIdx >= 0 && dpbIdx < dpbCount) ? dpb[dpbIdx].Poc : -1;
        }
        
        // Diagnostic: log ref lists for debugging
        if (DiagDecodeCallLog != null)
        {
            var l0Pocs = string.Join(",", Enumerable.Range(0, numRefList0).Select(i => refPocList0[i]));
            var l1Pocs = string.Join(",", Enumerable.Range(0, numRefList1).Select(i => refPocList1[i]));
            var l0Dpb = string.Join(",", Enumerable.Range(0, numRefList0).Select(i => refPicList0[i]));
            var l1Dpb = string.Join(",", Enumerable.Range(0, numRefList1).Select(i => refPicList1[i]));
            var l0Entries = sliceHeader.RplModificationFlag[0] && sliceHeader.ListEntryLx[0].Length > 0
                ? string.Join(",", sliceHeader.ListEntryLx[0].Take(sliceHeader.NumRefIdxL0Active))
                : "none";
            var l1Entries = sliceHeader.RplModificationFlag[1] && sliceHeader.ListEntryLx[1].Length > 0
                ? string.Join(",", sliceHeader.ListEntryLx[1].Take(sliceHeader.NumRefIdxL1Active))
                : "none";
            var dpbPocs = string.Join(",", Enumerable.Range(0, dpbCount).Select(j => $"{j}:{dpb[j].Poc}(f={dpb[j].Flags:x})"));
            DiagDecodeCallLog.Add($"  RefLists: poc={currentPoc} type={sliceHeader.SliceType} addr={sliceHeader.SliceSegmentAddress} L0pocs=[{l0Pocs}] L1pocs=[{l1Pocs}] L0dpb=[{l0Dpb}] L1dpb=[{l1Dpb}] rplMod=[{sliceHeader.RplModificationFlag[0]},{sliceHeader.RplModificationFlag[1]}] modL0=[{l0Entries}] modL1=[{l1Entries}] dpb=[{dpbPocs}]");
        }
        
        return true;
    }

    /// <summary>
    /// Builds a reference picture candidate list by cycling through candidate lists.
    /// Matches FFmpeg's ff_hevc_slice_rpl: builds at least one full cycle of all candidates,
    /// which may exceed numRefActive. This is needed so that ref_pic_list_modification
    /// indices (0..numPocTotalCurr-1) are all valid.
    /// </summary>
    private static void BuildRefListFull(
        int numRefActive,
        ReadOnlySpan<int> first, int firstCount,
        ReadOnlySpan<int> second, int secondCount,
        ReadOnlySpan<int> third, int thirdCount,
        Span<int> output, Span<bool> outputIsLt, out int outCount,
        ReadOnlySpan<int> interLayer0 = default, int interLayer0Count = 0,
        bool interLayer0AfterFirst = false)
    {
        outCount = 0;
        int totalCandidates = firstCount + secondCount + thirdCount + interLayer0Count;
        if (totalCandidates == 0)
            return;

        // Cycle through candidate lists until we have at least numRefActive entries.
        // Within each cycle, add ALL candidates (not stopping at numRefActive),
        // matching FFmpeg's inner loop limit of HEVC_MAX_REFS.
        //
        // MV-HEVC list order (F.8.1.5):
        //   L0: ST_CURR_BEF, INTER_LAYER0, ST_CURR_AFT, LT_CURR, INTER_LAYER1
        //   L1: ST_CURR_AFT, INTER_LAYER1, ST_CURR_BEF, LT_CURR, INTER_LAYER0
        // When interLayer0AfterFirst=true: first, interLayer0, second, third, (end)
        // When interLayer0AfterFirst=false: first, second, third, interLayer0 (appended)
        while (outCount < numRefActive)
        {
            for (int i = 0; i < firstCount && outCount < HevcConstants.MaxRefs; i++)
            {
                output[outCount] = first[i];
                outputIsLt[outCount] = false;
                outCount++;
            }
            // INTER_LAYER0 after first (L0 order: BEF, IL0, AFT, LT)
            if (interLayer0AfterFirst)
            {
                for (int i = 0; i < interLayer0Count && outCount < HevcConstants.MaxRefs; i++)
                {
                    output[outCount] = interLayer0[i];
                    outputIsLt[outCount] = true; // inter-layer refs treated as long-term (G.8.1.3)
                    outCount++;
                }
            }
            for (int i = 0; i < secondCount && outCount < HevcConstants.MaxRefs; i++)
            {
                output[outCount] = second[i];
                outputIsLt[outCount] = false;
                outCount++;
            }
            for (int i = 0; i < thirdCount && outCount < HevcConstants.MaxRefs; i++)
            {
                output[outCount] = third[i];
                outputIsLt[outCount] = true;
                outCount++;
            }
            // INTER_LAYER0 at end (L1 order: AFT, BEF, LT, IL0)
            if (!interLayer0AfterFirst)
            {
                for (int i = 0; i < interLayer0Count && outCount < HevcConstants.MaxRefs; i++)
                {
                    output[outCount] = interLayer0[i];
                    outputIsLt[outCount] = true; // inter-layer refs treated as long-term (G.8.1.3)
                    outCount++;
                }
            }
        }
    }

    /// <summary>
    /// Captures the current slice's ref lists as a snapshot and fills per-CTB indices.
    /// Matches FFmpeg's init_slice_rpl: all CTBs from sliceStartAddr to end of frame
    /// point to this slice's ref list entry. Later slices overwrite their range.
    /// </summary>
    private void SnapshotSliceRefLists(int sliceStartAddr, int totalCtbs)
    {
        if (currentFrameSliceRefLists == null || currentFramePerCtbSliceIdx == null)
            return;

        int sliceIdx = currentFrameSliceRefLists.Count;
        currentFrameSliceRefLists.Add(new SliceRefListSnapshot(
            refPocList0, numRefList0, refIsLongTerm0,
            refPocList1, numRefList1, refIsLongTerm1));

        // Fill per-CTB indices from slice start to end of frame (matching FFmpeg)
        byte idx = (byte)sliceIdx;
        for (int c = sliceStartAddr; c < totalCtbs; c++)
            currentFramePerCtbSliceIdx[c] = idx;
    }

    /// <summary>
    /// Finds a DPB frame by POC value. Returns dpb index or -1 if not found.
    /// </summary>
    private int FindDpbByPoc(int poc, bool useMsb = true)
    {
        if (useMsb)
        {
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].Poc == poc)
                    return i;
            }
        }
        else
        {
            // LSB-only match for long-term refs
            int mask = (context.ActiveSps != null) 
                ? (1 << (context.ActiveSps.Log2MaxPicOrderCntLsbMinus4 + 4)) - 1 
                : 0xFF;
            for (int i = 0; i < dpbCount; i++)
            {
                if ((dpb[i].Poc & mask) == poc && dpb[i].Poc != currentPoc)
                    return i;
            }
        }
        return -1;
    }


    /// <summary>
    /// Creates a placeholder reference frame for a POC not found in the DPB.
    /// Fills with mid-gray (1 &lt;&lt; (bitDepth-1)) and marks as UNAVAILABLE.
    /// Matches FFmpeg's generate_missing_ref() in refs.c.
    /// </summary>
    private int GenerateMissingRef(int poc)
    {
        if (dpbCount >= MaxDpbFrames || context.ActiveSps == null)
            return -1;
        
        var sps = context.ActiveSps;
        int codedW = context.CodedWidth;
        int codedH = context.CodedHeight;
        int bytesPerSample = sps.BitDepthLuma > 8 ? 2 : 1;
        int lumaSize = codedW * codedH * bytesPerSample;
        int chromaSize = lumaSize / 2; // 4:2:0
        int totalSize = lumaSize + chromaSize;
        
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        
        if (bytesPerSample == 1)
        {
            byte midGray = (byte)(1 << (sps.BitDepthLuma - 1));
            Array.Fill(buffer, midGray, 0, totalSize);
        }
        else
        {
            ushort midGray = (ushort)(1 << (sps.BitDepthLuma - 1));
            var shortSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                buffer.AsSpan(0, totalSize));
            shortSpan.Fill(midGray);
        }
        
        // Create zeroed MV fields so temporal MVP doesn't crash
        int fieldSize = puWidthIn4 * puHeightIn4;
        short[]? mvL0X = null, mvL0Y = null, mvL1X = null, mvL1Y = null;
        sbyte[]? refIdxL0 = null, refIdxL1 = null;
        byte[]? predFlags = null;
        
        if (fieldSize > 0)
        {
            mvL0X = new short[fieldSize];
            mvL0Y = new short[fieldSize];
            mvL1X = new short[fieldSize];
            mvL1Y = new short[fieldSize];
            refIdxL0 = new sbyte[fieldSize];
            refIdxL1 = new sbyte[fieldSize];
            predFlags = new byte[fieldSize];
            Array.Fill(refIdxL0, (sbyte)-1);
            Array.Fill(refIdxL1, (sbyte)-1);
        }
        
        int idx = dpbCount;
        dpb[dpbCount++] = new HevcReferenceFrame
        {
            Buffer = buffer,
            Poc = poc,
            PresentationTimeTicks = 0,
            Flags = FrameFlagUnavailable,
            MvL0X = mvL0X,
            MvL0Y = mvL0Y,
            MvL1X = mvL1X,
            MvL1Y = mvL1Y,
            RefIdxL0 = refIdxL0,
            RefIdxL1 = refIdxL1,
            PredFlags = predFlags,
            PuWidthIn4 = puWidthIn4,
            PuHeightIn4 = puHeightIn4,
            NumRefList0 = 0,
            NumRefList1 = 0,
        };
        
        return idx;
    }
    
    /// <summary>
    /// Removes all UNAVAILABLE (generated missing) frames from the DPB.
    /// Called at the start of each frame's RPS processing to clean up previous frame's placeholders.
    /// Matches FFmpeg's unref_missing_refs() in refs.c.
    /// </summary>
    private void UnrefMissingRefs()
    {
        int writeIdx = 0;
        for (int i = 0; i < dpbCount; i++)
        {
            if ((dpb[i].Flags & FrameFlagUnavailable) != 0)
            {
                if (dpb[i].Buffer != null)
                    ArrayPool<byte>.Shared.Return(dpb[i].Buffer!);
                dpb[i] = default;
                continue;
            }
            if (writeIdx != i)
                dpb[writeIdx] = dpb[i];
            writeIdx++;
        }
        // Clear trailing slots
        for (int i = writeIdx; i < dpbCount; i++)
            dpb[i] = default;
        dpbCount = writeIdx;
    }

    private void DecodeSliceCtus(HevcNalUnit nal, HevcSliceSegmentHeader sliceHeader, HevcSequenceParameterSet sps, HevcPictureParameterSet pps)
    {
        if (sliceHeader.FirstSliceSegmentInPicFlag)
            diagPicCounter++;
        
        int ctbSizeY = context.CtbSizeY;
        int picWidthInCtb = context.PicWidthInCtbsY;
        int picHeightInCtb = context.PicHeightInCtbsY;
        int totalCtbs = picWidthInCtb * picHeightInCtb;

        int startCtbAddr = sliceHeader.SliceSegmentAddress;

        // Remove emulation prevention bytes for RBSP, tracking removed positions
        // for NAL-to-RBSP offset conversion (needed for WPP entry points)
        byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(nal.Payload.Span, out int[] removedBytePositions);

        // HEVC always uses CABAC
        DecodeWithCabac(rbsp, removedBytePositions, sliceHeader, sps, pps, startCtbAddr, totalCtbs, picWidthInCtb);
    }

    private void DecodeWithCabac(byte[] rbsp, int[] removedBytePositions, HevcSliceSegmentHeader sliceHeader, HevcSequenceParameterSet sps, HevcPictureParameterSet pps, int startCtb, int totalCtbs, int ctbWidth)
    {
        // SliceDataByteOffset is in NAL coordinates (computed before emulation prevention removal).
        // Convert to RBSP coordinates by subtracting emulation bytes in the header region.
        int nalDataOffset = sliceHeader.SliceDataByteOffset;
        int removedBeforeSliceData = 0;
        for (int j = 0; j < removedBytePositions.Length; j++)
        {
            if (removedBytePositions[j] < nalDataOffset)
                removedBeforeSliceData++;
            else
                break;
        }
        int dataOffset = nalDataOffset - removedBeforeSliceData;
        if (dataOffset <= 0 || dataOffset >= rbsp.Length)
        {
            dataOffset = 0;
        }
        
        DiagDecodeCallLog?.Add($"    CABAC: nalDataOff={nalDataOffset}, removed={removedBeforeSliceData}, rbspDataOff={dataOffset}, rbspLen={rbsp.Length}, tiles={pps.TilesEnabled}, numTileCols={pps.NumTileColumns}, numTileRows={pps.NumTileRows}, entryPts={sliceHeader.EntryPointOffsets.Length}");
        
        // Slice QP = PPS init QP + slice_qp_delta
        int sliceQp = sliceHeader.SliceQp(pps);

        // Determine CABAC init type for this slice
        int initType = sliceHeader.SliceType switch
        {
            HevcSliceType.ISlice => 0,
            HevcSliceType.PSlice => 1,
            HevcSliceType.BSlice => 2,
            _ => 0
        };
        if (sliceHeader.CabacInitFlag && sliceHeader.SliceType != HevcSliceType.ISlice)
            initType ^= 3;

        // Initialize CABAC decoder starting at slice data (after byte-aligned header)
        // FFmpeg: ff_hevc_cabac_init lines 461-468 — dependent slices inherit CABAC
        // context states, UNLESS they start at a tile boundary, where contexts must
        // be re-initialized from table values (same as independent slices).
        HevcCabacDecoder decoder;
        if (sliceHeader.DependentSliceSegmentFlag)
        {
            int sliceStartTs = pps.CtbAddrRsToTs![startCtb];
            bool atTileBoundary = pps.TilesEnabled && sliceStartTs > 0 &&
                pps.TileIdPerTs![sliceStartTs] != pps.TileIdPerTs[sliceStartTs - 1];

            if (atTileBoundary)
            {
                // Dependent slice at tile boundary: re-init context models
                // (FFmpeg: cabac_init_state called when tile_id differs)
                decoder = new HevcCabacDecoder(
                    rbsp.AsSpan(dataOffset),
                    cabacContextStorage.AsSpan(),
                    sliceQp,
                    initType);
                Array.Clear(statCoeff);
            }
            else
            {
                // Dependent slice within same tile: inherit context models
                decoder = new HevcCabacDecoder(
                    rbsp.AsSpan(dataOffset),
                    cabacContextStorage.AsSpan());
            }
        }
        else
        {
            // Independent slices fully initialize CABAC
            decoder = new HevcCabacDecoder(
                rbsp.AsSpan(dataOffset),
                cabacContextStorage.AsSpan(),
                sliceQp,
                initType);
            Array.Clear(statCoeff);
        }

        int ctbSizeY = context.CtbSizeY;
        Span<short> residual = residualBuffer.AsSpan();

        // Ensure persistent WPP context buffer is allocated (shared across slices).
        // Matches FFmpeg's common_cabac_state which persists across all slices in a picture.
        if (pps.EntropyCodingSyncEnabled)
        {
            wppSavedContexts ??= new byte[HevcCabacContextIndex.TotalContexts];
        }

        // Pre-compute cumulative entry point byte offsets for WPP row starts.
        // Entry point offsets in the bitstream are NAL byte counts (including emulation
        // prevention bytes). Our CABAC decoder operates on RBSP data, so we must convert
        // NAL offsets to RBSP offsets by subtracting removed emulation prevention bytes.
        int[]? wppRowByteOffsets = null;
        if (pps.EntropyCodingSyncEnabled && sliceHeader.EntryPointOffsets.Length > 0)
        {
            int numEntryPoints = sliceHeader.EntryPointOffsets.Length;
            wppRowByteOffsets = new int[numEntryPoints + 1];
            wppRowByteOffsets[0] = 0; // first row starts at beginning of slice data

            // Convert NAL cumulative offsets to RBSP offsets
            int nalCumulative = 0;
            for (int i = 0; i < numEntryPoints; i++)
            {
                nalCumulative += (int)sliceHeader.EntryPointOffsets[i];

                // Count emulation prevention bytes within the slice data NAL range.
                // Entry point offsets are NAL byte counts relative to slice data start,
                // so we count removed bytes in [nalDataOffset, nalDataOffset + nalCumulative).
                int nalAbsoluteOffset = nalDataOffset + nalCumulative;
                int removedInSliceData = 0;
                for (int j = 0; j < removedBytePositions.Length; j++)
                {
                    if (removedBytePositions[j] >= nalDataOffset && removedBytePositions[j] < nalAbsoluteOffset)
                        removedInSliceData++;
                    else if (removedBytePositions[j] >= nalAbsoluteOffset)
                        break;
                }

                wppRowByteOffsets[i + 1] = nalCumulative - removedInSliceData;
            }
        }

        // FFmpeg: first_qp_group = !dependent (line 3066 of hevcdec.c)
        // Dependent slices don't reset QP group — they continue from previous slice.
        // Exception: dependent slices at tile boundaries reset QP (FFmpeg hls_decode_neighbour line 2720).
        {
            int ts = pps.CtbAddrRsToTs![startCtb];
            bool depAtTileBd = sliceHeader.DependentSliceSegmentFlag && pps.TilesEnabled &&
                ts > 0 && pps.TileIdPerTs![ts] != pps.TileIdPerTs[ts - 1];
            firstQpGroup = !sliceHeader.DependentSliceSegmentFlag || depAtTileBd;
        }
        currentSliceQp = sliceQp;

        int ctusDecoded = 0;
        int wppRowIndex = 0; // tracks which row within this slice we're on

        // FFmpeg ff_hevc_cabac_init lines 470-477: When a dependent slice starts at a
        // row boundary in WPP mode (not first slice in pic), load the WPP saved context
        // states. This is because each WPP row must start with the contexts saved from
        // the 2nd CTU of the previous row, regardless of which slice saved them.
        // Also set firstQpGroup = true per FFmpeg's hls_decode_neighbour (line 2713-2714).
        int startCtbX = startCtb % ctbWidth;
        if (pps.EntropyCodingSyncEnabled && !sliceHeader.FirstSliceSegmentInPicFlag &&
            startCtbX == 0 && sliceHeader.DependentSliceSegmentFlag &&
            wppSavedContexts != null)
        {
            // FFmpeg ff_hevc_cabac_init lines 470-477: for ctb_width==1, there's no
            // 2nd CTU to save from, so re-init from tables (not load saved states).
            // For ctb_width>1, load the WPP saved context from the 2nd CTU of the
            // previous row.
            if (ctbWidth == 1)
                decoder.InitializeContexts(sliceQp, initType);
            else
                decoder.LoadContextStates(wppSavedContexts);
            if (sps.PersistentRiceAdaptationEnabled && wppSavedStatCoeff != null)
                Array.Copy(wppSavedStatCoeff, statCoeff, 4);
            firstQpGroup = true;

            // Capture WPP states at dep slice start for shadow validation
            if (DiagWppSavedStatesCapture != null)
            {
                Array.Copy(wppSavedContexts, DiagWppSavedStatesCapture,
                    Math.Min(wppSavedContexts.Length, DiagWppSavedStatesCapture.Length));
            }
            
            // Log WPP load at dependent slice start (includes pic number)
            if (DiagWppContextLog != null)
            {
                int cksum = 0;
                for (int ci = 0; ci < wppSavedContexts.Length; ci++)
                    cksum = (cksum * 31) + wppSavedContexts[ci];
                DiagWppContextLog.Add((diagPicCounter, 'D', 0, startCtbX, startCtb / ctbWidth, cksum,
                    wppSavedContexts[0], wppSavedContexts[1], wppSavedContexts[2], wppSavedContexts[3]));
            }
        }

        // Capture CABAC data and context states for shadow comparison
        if (DiagCaptureSliceStartCtb == startCtb && 
            (DiagTracePicNum < 0 || diagPicCounter == DiagTracePicNum))
        {
            var cabacData = rbsp.AsSpan(dataOffset);
            DiagCabacDataCapture = cabacData.ToArray();
            DiagCabacContextCapture = new byte[HevcCabacContextIndex.TotalContexts];
            decoder.ExportContexts(DiagCabacContextCapture);
        }

        // Tile scan: convert start CTB from raster to tile-scan address
        int[] rsToTs = pps.CtbAddrRsToTs!;
        int[] tsToRs = pps.CtbAddrTsToRs!;
        int[] tileIdPerTs = pps.TileIdPerTs!;
        int startCtbTs = rsToTs[startCtb];

        // Pre-compute entry point byte offsets for tiles (similar to WPP but for tile boundaries)
        int[]? tileEntryByteOffsets = null;
        if (pps.TilesEnabled && sliceHeader.EntryPointOffsets.Length > 0)
        {
            int numEntryPoints = sliceHeader.EntryPointOffsets.Length;
            tileEntryByteOffsets = new int[numEntryPoints + 1];
            tileEntryByteOffsets[0] = 0;

            int nalCumulative = 0;
            for (int i = 0; i < numEntryPoints; i++)
            {
                nalCumulative += (int)sliceHeader.EntryPointOffsets[i];
                int nalAbsoluteOffset = nalDataOffset + nalCumulative;
                int removedInSliceData = 0;
                for (int j = 0; j < removedBytePositions.Length; j++)
                {
                    if (removedBytePositions[j] >= nalDataOffset && removedBytePositions[j] < nalAbsoluteOffset)
                        removedInSliceData++;
                    else if (removedBytePositions[j] >= nalAbsoluteOffset)
                        break;
                }
                tileEntryByteOffsets[i + 1] = nalCumulative - removedInSliceData;
            }
        }
        int tileEntryIndex = 0; // tracks which tile entry point we're on

        for (int ctbAddrTs = startCtbTs; ctbAddrTs < totalCtbs; ctbAddrTs++)
        {
            int ctbAddrRs = tsToRs[ctbAddrTs];
            int ctbX = ctbAddrRs % ctbWidth;
            int ctbY = ctbAddrRs / ctbWidth;

            try
            {
                // Tile boundary: reinit CABAC and reset context states at tile starts
                // Matches FFmpeg's ff_hevc_cabac_init tile_id check
                if (pps.TilesEnabled && ctbAddrTs > startCtbTs &&
                    tileIdPerTs[ctbAddrTs] != tileIdPerTs[ctbAddrTs - 1])
                {
                    tileEntryIndex++;
                    decoder.DecodeTerminate();

                    if (tileEntryByteOffsets != null && tileEntryIndex < tileEntryByteOffsets.Length)
                        decoder.ReinitAtOffset(tileEntryByteOffsets[tileEntryIndex]);
                    else if (!decoder.ByteAlignAndReinit())
                    {
                        DiagDecodeCallLog?.Add($"    CTU loop exit: Tile reinit failed at ctbTs={ctbAddrTs} ctbRs={ctbAddrRs} ({ctbX},{ctbY}) tileEntry={tileEntryIndex}/{tileEntryByteOffsets?.Length ?? 0} decoded={ctusDecoded}/{totalCtbs}");
                        break; // Past end of data — slice exhausted
                    }

                    // Full context re-init at tile boundary (same as slice start)
                    decoder.InitializeContexts(sliceQp, initType);

                    firstQpGroup = true;
                }

                // WPP: at the start of each new CTU row, re-initialize CABAC at the
                // entry point byte offset and load saved context states.
                // Matches FFmpeg's ff_hevc_cabac_init for entropy_coding_sync.
                if (pps.EntropyCodingSyncEnabled && ctbX == 0 && ctbAddrTs > startCtbTs && !DiagSkipWppReinit)
                {
                    wppRowIndex++;
                    decoder.DecodeTerminate(); // end_of_subset_one_bit (always 1)
                    
                    // Record natural byte position after terminate (where ByteAlignAndReinit would start)
                    int naturalBytePos = decoder.DiagBytePos;
                    if (decoder.DiagBitsRemaining < 8)
                        naturalBytePos++; // partial byte consumed
                    
                    // Compare natural position vs entry point offset for diagnostics.
                    // Use the entry point offset if available (correct for both sequential
                    // and parallel decoding), otherwise fall back to natural position.
                    bool reinitOk;
                    if (wppRowByteOffsets != null && wppRowIndex < wppRowByteOffsets.Length)
                    {
                        int entryOffset = wppRowByteOffsets[wppRowIndex];

                        // Log comparison between natural and entry-point positions
                        DiagWppOffsetLog?.Add((wppRowIndex, ctbY, naturalBytePos, entryOffset,
                            entryOffset - naturalBytePos));

                        decoder.ReinitAtOffset(entryOffset);
                        reinitOk = true; // entry point offsets are always valid
                    }
                    else
                        reinitOk = decoder.ByteAlignAndReinit();

                    // If reinit failed (past end of data), this slice's data is exhausted.
                    // Matches FFmpeg's cabac_reinit returning AVERROR_INVALIDDATA → slice stops.
                    if (!reinitOk)
                        break;

                    if (ctbWidth == 1)
                    {
                        // Single-column picture: full context re-initialization
                        // (no 2nd CTU to save from, so re-init from scratch)
                        // Must call InitializeContexts — not LoadContextStates — because
                        // cabacContextStorage IS the decoder's live context state memory,
                        // so loading from it would be a no-op self-copy.
                        decoder.InitializeContexts(sliceQp, initType);
                    }
                    else if (DiagForceTableContextsForWpp)
                    {
                        // Diagnostic: use table-initialized contexts instead of saved WPP contexts
                        decoder.InitializeContexts(sliceQp, initType);
                    }
                    else if (wppSavedContexts != null)
                    {
                        decoder.LoadContextStates(wppSavedContexts);
                        if (sps.PersistentRiceAdaptationEnabled && wppSavedStatCoeff != null)
                            Array.Copy(wppSavedStatCoeff, statCoeff, 4);
                    }

                    // Log context state checksum after load
                    if (DiagWppContextLog != null && wppSavedContexts != null)
                    {
                        int cksum = 0;
                        for (int ci = 0; ci < wppSavedContexts.Length; ci++)
                            cksum = (cksum * 31) + wppSavedContexts[ci];
                        DiagWppContextLog.Add((diagPicCounter, 'L', wppRowIndex, ctbX, ctbY, cksum,
                            wppSavedContexts[0], wppSavedContexts[1], wppSavedContexts[2], wppSavedContexts[3]));
                    }

                    // Reset QP prediction at WPP row boundary (matches FFmpeg's hls_decode_neighbour)
                    firstQpGroup = true;
                }

                // Log CABAC state at CTU start for drift detection
                DiagCtuStartLog?.Add((ctbAddrRs, ctbX, ctbY, decoder.CurrentBitPosition,
                    decoder.DiagBinCount, decoder.DiagIvlRange, decoder.DiagIvlOffset));

                // Enable per-bin tracing for specific CTUs (filtered by pic number if set)
                bool tracePic = DiagTracePicNum == 0 || DiagTracePicNum == diagPicCounter;
                if (tracePic && DiagTraceCtuAddrs != null && DiagTraceCtuAddrs.Contains(ctbAddrRs))
                {
                    decoder.DiagBinLog = new List<(int, char, int, int, uint, uint, byte)>();
                }
                else
                {
                    decoder.DiagBinLog = null;
                }

                // Decode CTU using coding tree structure
                bool moreData = DecodeCtu(ref decoder, sliceHeader, sps, pps, ctbX, ctbY, residual);
                ctusDecoded++;

                DiagCtuMoreDataLog?.Add((diagPicCounter, startCtb, ctbAddrRs, moreData, decoder.DiagBytePos));

                // Save bin log if tracing was enabled
                if (decoder.DiagBinLog != null && DiagCtuBinLogs != null)
                {
                    DiagCtuBinLogs[ctbAddrRs] = decoder.DiagBinLog;
                    decoder.DiagBinLog = null;
                }

                // WPP: save context states after the 2nd CTU of each row.
                // FFmpeg checks (ctb_addr_ts % ctb_width == 2) after incrementing ctb_addr_ts,
                // which is equivalent to checking ctbX == 1 after decoding.
                if (pps.EntropyCodingSyncEnabled && wppSavedContexts != null && !DiagSkipWppReinit &&
                    (ctbX == 1 || (ctbWidth == 2 && ctbX == 0)))
                {
                    decoder.SaveContextStates(wppSavedContexts);
                    if (sps.PersistentRiceAdaptationEnabled)
                    {
                        wppSavedStatCoeff ??= new int[4];
                        Array.Copy(statCoeff, wppSavedStatCoeff, 4);
                    }

                    // Log context state checksum after save
                    if (DiagWppContextLog != null)
                    {
                        int cksum = 0;
                        for (int ci = 0; ci < wppSavedContexts.Length; ci++)
                            cksum = (cksum * 31) + wppSavedContexts[ci];
                        DiagWppContextLog.Add((diagPicCounter, 'S', wppRowIndex, ctbX, ctbY, cksum,
                            wppSavedContexts[0], wppSavedContexts[1], wppSavedContexts[2], wppSavedContexts[3]));
                    }
                }

                if (!moreData)
                {
                    DiagDecodeCallLog?.Add($"    CTU loop exit: moreData=false at ctbTs={ctbAddrTs} ctbRs={ctbAddrRs} ({ctbX},{ctbY}) decoded={ctusDecoded}/{totalCtbs} tileEntry={tileEntryIndex}");
                    break;
                }
            }
            catch (Exception ex)
            {
                DiagDecodeCallLog?.Add($"    CTU loop exit: EXCEPTION at ctbTs={ctbAddrTs} ctbRs={ctbAddrRs} ({ctbX},{ctbY}) decoded={ctusDecoded}/{totalCtbs} tileEntry={tileEntryIndex}: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (decoder.IsEndOfStream)
            {
                DiagDecodeCallLog?.Add($"    CTU loop exit: EndOfStream at ctbTs={ctbAddrTs} ctbRs={ctbAddrRs} decoded={ctusDecoded}/{totalCtbs}");
                break;
            }
        }

        // Expose decode stats for diagnostics
        LastCtusDecoded = ctusDecoded;
        LastTotalCtus = totalCtbs;
        LastCabacBitPosition = decoder.CurrentBitPosition;
        LastCabacRemainingBytes = decoder.RemainingBytes;
        
        DiagSliceLog?.Add((diagPicCounter, currentPoc, startCtb, ctusDecoded, sliceHeader.DependentSliceSegmentFlag,
            sliceHeader.EntryPointOffsets.Length, (int)sliceHeader.SliceType, rbsp.Length - dataOffset));
    }

    // DecodeCtu is now implemented in HevcDecoder.CodingTree.cs (partial class)

    /// <summary>
    /// Returns the PCM deblocking bypass flag for the 4×4 PU at pixel position (x, y).
    /// Matches FFmpeg's get_pcm() in filter.c.
    /// Returns true if the block should bypass deblocking (isPcm == 2).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetPcmBypass(int x, int y)
    {
        if (isPcmTab == null || x < 0 || y < 0) return true;
        int xPu = x >> 2;
        int yPu = y >> 2;
        if (xPu >= isPcmWidth || yPu >= (isPcmTab.Length / isPcmWidth)) return true;
        return isPcmTab[yPu * isPcmWidth + xPu] == 2;
    }

    private void ApplyDeblocking(HevcPictureParameterSet pps, HevcSliceSegmentHeader sliceHeader)
    {
        if (currentFrameBuffer == null || vertBsTab == null || horizBsTab == null || qpYTab == null)
            return;

        var sps = context.ActiveSps;
        if (sps == null) return;

        int bitDepth = sps.BitDepthLuma;
        int chromaBitDepth = sps.BitDepthChroma;
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int width = sps.PictureWidthInLumaSamples;
        int height = sps.PictureHeightInLumaSamples;
        int chromaWidth = width >> sps.HShiftChroma;
        int chromaHeight = height >> sps.VShiftChroma;

        int chromaQpOffset = pps.PpsCbQpOffset;
        int chromaQpOffsetCr = pps.PpsCrQpOffset;

        int lumaPlaneSize = width * height * bytesPerSample;
        int chromaPlaneSize = chromaWidth * chromaHeight * bytesPerSample;
        var buffer = currentFrameBuffer.AsSpan();

        int minCbWidth = sps.PicWidthInMinCbsY;
        int minCbHeight = sps.PicHeightInMinCbsY;
        int log2MinCbSize = sps.Log2MinCbSizeY;
        int ctbSize = sps.CtbSizeY;
        int picWidthInCtbs = sps.PicWidthInCtbsY;
        bool hasPerCtuParams = ctuBetaOffset != null && ctuTcOffset != null;
        int hShift = sps.HShiftChroma;
        int vShift = sps.VShiftChroma;
        int chromaEdgeStepX = 8 << hShift;  // 16 for 420/422, 8 for 444
        int chromaEdgeStepY = 8 << vShift;  // 16 for 420, 8 for 422/444
        int chromaBsOffsetV = 4 << vShift;  // 4 for 444/422, 8 for 420 (FFmpeg filter.c:617 — 4*v)
        int chromaBsOffsetH = 4 << hShift;  // 4 for 444, 8 for 420/422 (FFmpeg filter.c:651 — 4*h)
        bool is420 = sps.ChromaFormatIdc == HevcChromaFormat.Chroma420;

        if (bytesPerSample == 2)
        {
            var lumaPlane = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                buffer.Slice(0, lumaPlaneSize));
            var cbPlane = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                buffer.Slice(lumaPlaneSize, chromaPlaneSize));
            var crPlane = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                buffer.Slice(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize));

            // Process vertical luma edges at 8-pixel intervals
            for (int pixelY = 0; pixelY < height; pixelY += 8)
            {
                for (int pixelX = 8; pixelX < width; pixelX += 8)
                {
                    int x4 = pixelX >> 2;
                    int y4top = pixelY >> 2;
                    int y4bot = (pixelY + 4) >> 2;
                    int bs0 = vertBsTab[y4top * bsWidth + x4];
                    int bs1 = (pixelY + 4 < height) ? vertBsTab[y4bot * bsWidth + x4] : 0;

                    if (bs0 == 0 && bs1 == 0) continue;
                    if (pixelX < 4 || pixelX + 4 > width) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int cbXP = (pixelX - 1) >> log2MinCbSize;
                    int cbXQ = pixelX >> log2MinCbSize;
                    int cbY = pixelY >> log2MinCbSize;
                    int qpP = qpYTab[cbY * minCbWidth + cbXP];
                    int qpQ = qpYTab[cbY * minCbWidth + cbXQ];
                    int qpL = (qpP + qpQ + 1) >> 1;

                    int beta = HevcDeblockingFilter.GetBeta(qpL, betaOffset, bitDepth);
                    int tc0 = bs0 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs0, tcOffset, bitDepth) : 0;
                    int tc1 = bs1 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs1, tcOffset, bitDepth) : 0;

                    int rowsAvailable = Math.Min(8, height - pixelY);
                    if (rowsAvailable < 8) tc1 = 0;

                    bool noP0 = GetPcmBypass(pixelX - 1, pixelY);
                    bool noP1 = GetPcmBypass(pixelX - 1, pixelY + 4);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX, pixelY + 4);

                    HevcDeblockingFilter.FilterLumaEdge8RowVerticalHighBitDepth(
                        lumaPlane, width, pixelX, pixelY, beta, tc0, tc1, bitDepth,
                        noP0, noQ0, noP1, noQ1);
                }
            }

            // Process vertical chroma edges
            for (int pixelY = 0; pixelY < height; pixelY += chromaEdgeStepY)
            {
                for (int pixelX = chromaEdgeStepX; pixelX < width; pixelX += chromaEdgeStepX)
                {
                    int x4 = pixelX >> 2;
                    // Chroma BS: at luma positions (x, y) and (x, y+4*v)
                    // FFmpeg filter.c:616-617: bs1 at y + 4*v where v = 1<<vshift
                    int chromaBs0 = vertBsTab[(pixelY >> 2) * bsWidth + x4];
                    int chromaBs1 = (pixelY + chromaBsOffsetV < height) ? vertBsTab[((pixelY + chromaBsOffsetV) >> 2) * bsWidth + x4] : 0;

                    if (chromaBs0 < 2 && chromaBs1 < 2) continue;

                    int cx = pixelX >> hShift;
                    int cy = pixelY >> vShift;
                    int chromaRowsAvailable = chromaHeight - cy;
                    if (cx < 2 || cx + 2 > chromaWidth || chromaRowsAvailable < 4) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int cbXP = (pixelX - 1) >> log2MinCbSize;
                    int cbXQ = pixelX >> log2MinCbSize;
                    int cbY0 = pixelY >> log2MinCbSize;
                    int qpP0 = qpYTab[cbY0 * minCbWidth + cbXP];
                    int qpQ0 = qpYTab[cbY0 * minCbWidth + cbXQ];
                    int qp0 = (qpP0 + qpQ0 + 1) >> 1;

                    int cbY1 = (pixelY + chromaBsOffsetV) >> log2MinCbSize;
                    int qpP1 = (pixelY + chromaBsOffsetV < height) ? qpYTab[cbY1 * minCbWidth + cbXP] : qpP0;
                    int qpQ1 = (pixelY + chromaBsOffsetV < height) ? qpYTab[cbY1 * minCbWidth + cbXQ] : qpQ0;
                    int qp1 = (qpP1 + qpQ1 + 1) >> 1;

                    int chromaTc0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTc1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;
                    if (chromaRowsAvailable < 8) chromaTc1 = 0;

                    bool noP0 = GetPcmBypass(pixelX - 1, pixelY);
                    bool noP1 = GetPcmBypass(pixelX - 1, pixelY + chromaBsOffsetV);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX, pixelY + chromaBsOffsetV);

                    HevcDeblockingFilter.FilterChromaEdge8RowVerticalHighBitDepth(
                        cbPlane, chromaWidth, cx, cy, chromaTc0, chromaTc1, chromaBitDepth,
                        noP0, noQ0, noP1, noQ1);

                    int chromaTcCr0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTcCr1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;
                    if (chromaRowsAvailable < 8) chromaTcCr1 = 0;

                    HevcDeblockingFilter.FilterChromaEdge8RowVerticalHighBitDepth(
                        crPlane, chromaWidth, cx, cy, chromaTcCr0, chromaTcCr1, chromaBitDepth,
                        noP0, noQ0, noP1, noQ1);
                }
            }

            // Process horizontal luma edges at 8-pixel intervals
            for (int pixelY = 8; pixelY < height; pixelY += 8)
            {
                for (int pixelX = 0; pixelX < width; pixelX += 8)
                {
                    int y4 = pixelY >> 2;
                    int x4left = pixelX >> 2;
                    int x4right = (pixelX + 4) >> 2;
                    int bs0 = horizBsTab[y4 * bsWidth + x4left];
                    int bs1 = (pixelX + 4 < width) ? horizBsTab[y4 * bsWidth + x4right] : 0;

                    if (bs0 == 0 && bs1 == 0) continue;
                    if (pixelY < 4 || pixelY + 4 > height) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int cbX = pixelX >> log2MinCbSize;
                    int cbYP = (pixelY - 1) >> log2MinCbSize;
                    int cbYQ = pixelY >> log2MinCbSize;
                    int qpP = qpYTab[cbYP * minCbWidth + cbX];
                    int qpQ = qpYTab[cbYQ * minCbWidth + cbX];
                    int qpL = (qpP + qpQ + 1) >> 1;

                    int beta = HevcDeblockingFilter.GetBeta(qpL, betaOffset, bitDepth);
                    int tc0 = bs0 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs0, tcOffset, bitDepth) : 0;
                    int tc1 = bs1 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs1, tcOffset, bitDepth) : 0;

                    int colsAvailable = Math.Min(8, width - pixelX);
                    if (colsAvailable < 8) tc1 = 0;

                    bool noP0 = GetPcmBypass(pixelX, pixelY - 1);
                    bool noP1 = GetPcmBypass(pixelX + 4, pixelY - 1);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX + 4, pixelY);

                    HevcDeblockingFilter.FilterLumaEdge8RowHorizontalHighBitDepth(
                        lumaPlane, width, pixelX, pixelY, beta, tc0, tc1, bitDepth,
                        noP0, noQ0, noP1, noQ1);
                }
            }

            // Process horizontal chroma edges
            // FFmpeg filter.c:614,649: y += 8*v, x += 8*h, bs1 at x + 4*h
            for (int pixelY = chromaEdgeStepY; pixelY < height; pixelY += chromaEdgeStepY)
            {
                for (int pixelX = 0; pixelX < width; pixelX += chromaEdgeStepX)
                {
                    int y4 = pixelY >> 2;
                    // Chroma BS: at luma positions (x, y) and (x+4*h, y)
                    int chromaBs0 = horizBsTab[y4 * bsWidth + (pixelX >> 2)];
                    int chromaBs1 = (pixelX + chromaBsOffsetH < width) ? horizBsTab[y4 * bsWidth + ((pixelX + chromaBsOffsetH) >> 2)] : 0;

                    if (chromaBs0 < 2 && chromaBs1 < 2) continue;

                    int cx = pixelX >> hShift;
                    int cy = pixelY >> vShift;
                    if (cy < 2 || cy + 2 > chromaHeight || cx >= chromaWidth) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int chromaColumnsAvailable = chromaWidth - cx;

                    int cbYP = (pixelY - 1) >> log2MinCbSize;
                    int cbYQ = pixelY >> log2MinCbSize;
                    int cbX0 = pixelX >> log2MinCbSize;
                    int qpP0 = qpYTab[cbYP * minCbWidth + cbX0];
                    int qpQ0 = qpYTab[cbYQ * minCbWidth + cbX0];
                    int qp0 = (qpP0 + qpQ0 + 1) >> 1;

                    int cbX1 = (pixelX + chromaBsOffsetH) >> log2MinCbSize;
                    int qpP1 = (pixelX + chromaBsOffsetH < width) ? qpYTab[cbYP * minCbWidth + cbX1] : qpP0;
                    int qpQ1 = (pixelX + chromaBsOffsetH < width) ? qpYTab[cbYQ * minCbWidth + cbX1] : qpQ0;
                    int qp1 = (qpP1 + qpQ1 + 1) >> 1;

                    int chromaTc0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTc1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;

                    bool noP0 = GetPcmBypass(pixelX, pixelY - 1);
                    bool noP1 = GetPcmBypass(pixelX + chromaBsOffsetH, pixelY - 1);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX + chromaBsOffsetH, pixelY);

                    HevcDeblockingFilter.FilterChromaEdge8RowHorizontalHighBitDepth(
                        cbPlane, chromaWidth, cx, cy, chromaTc0, chromaTc1, chromaBitDepth, chromaColumnsAvailable,
                        noP0, noQ0, noP1, noQ1);

                    int chromaTcCr0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTcCr1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;

                    HevcDeblockingFilter.FilterChromaEdge8RowHorizontalHighBitDepth(
                        crPlane, chromaWidth, cx, cy, chromaTcCr0, chromaTcCr1, chromaBitDepth, chromaColumnsAvailable,
                        noP0, noQ0, noP1, noQ1);
                }
            }
        }
        else // 8-bit
        {
            var lumaPlane = buffer.Slice(0, lumaPlaneSize);
            var cbPlane = buffer.Slice(lumaPlaneSize, chromaPlaneSize);
            var crPlane = buffer.Slice(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize);

            // Process vertical luma edges at 8-pixel intervals
            for (int pixelY = 0; pixelY < height; pixelY += 8)
            {
                for (int pixelX = 8; pixelX < width; pixelX += 8)
                {
                    int x4 = pixelX >> 2;
                    int y4top = pixelY >> 2;
                    int y4bot = (pixelY + 4) >> 2;
                    int bs0 = vertBsTab[y4top * bsWidth + x4];
                    int bs1 = (pixelY + 4 < height) ? vertBsTab[y4bot * bsWidth + x4] : 0;

                    if (bs0 == 0 && bs1 == 0) continue;
                    if (pixelX < 4 || pixelX + 4 > width) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int cbXP = (pixelX - 1) >> log2MinCbSize;
                    int cbXQ = pixelX >> log2MinCbSize;
                    int cbY = pixelY >> log2MinCbSize;
                    int qpP = qpYTab[cbY * minCbWidth + cbXP];
                    int qpQ = qpYTab[cbY * minCbWidth + cbXQ];
                    int qpL = (qpP + qpQ + 1) >> 1;

                    int beta = HevcDeblockingFilter.GetBeta(qpL, betaOffset, bitDepth);
                    int tc0 = bs0 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs0, tcOffset, bitDepth) : 0;
                    int tc1 = bs1 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs1, tcOffset, bitDepth) : 0;

                    int rowsAvailable = Math.Min(8, height - pixelY);
                    if (rowsAvailable < 8) tc1 = 0;

                    bool noP0 = GetPcmBypass(pixelX - 1, pixelY);
                    bool noP1 = GetPcmBypass(pixelX - 1, pixelY + 4);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX, pixelY + 4);

                    HevcDeblockingFilter.FilterLumaEdge8RowVertical(
                        lumaPlane, width, pixelX, pixelY, beta, tc0, tc1,
                        noP0, noQ0, noP1, noQ1);
                }
            }

            // Process vertical chroma edges
            // FFmpeg filter.c:616-617: bs1 at y + 4*v where v = 1<<vshift
            for (int pixelY = 0; pixelY < height; pixelY += chromaEdgeStepY)
            {
                for (int pixelX = chromaEdgeStepX; pixelX < width; pixelX += chromaEdgeStepX)
                {
                    int x4 = pixelX >> 2;
                    int chromaBs0 = vertBsTab[(pixelY >> 2) * bsWidth + x4];
                    int chromaBs1 = (pixelY + chromaBsOffsetV < height) ? vertBsTab[((pixelY + chromaBsOffsetV) >> 2) * bsWidth + x4] : 0;

                    if (chromaBs0 < 2 && chromaBs1 < 2) continue;

                    int cx = pixelX >> hShift;
                    int cy = pixelY >> vShift;
                    int chromaRowsAvailable = chromaHeight - cy;
                    if (cx < 2 || cx + 2 > chromaWidth || chromaRowsAvailable < 4) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int cbXP = (pixelX - 1) >> log2MinCbSize;
                    int cbXQ = pixelX >> log2MinCbSize;
                    int cbY0 = pixelY >> log2MinCbSize;
                    int qpP0 = qpYTab[cbY0 * minCbWidth + cbXP];
                    int qpQ0 = qpYTab[cbY0 * minCbWidth + cbXQ];
                    int qp0 = (qpP0 + qpQ0 + 1) >> 1;

                    int cbY1 = (pixelY + chromaBsOffsetV) >> log2MinCbSize;
                    int qpP1 = (pixelY + chromaBsOffsetV < height) ? qpYTab[cbY1 * minCbWidth + cbXP] : qpP0;
                    int qpQ1 = (pixelY + chromaBsOffsetV < height) ? qpYTab[cbY1 * minCbWidth + cbXQ] : qpQ0;
                    int qp1 = (qpP1 + qpQ1 + 1) >> 1;

                    int chromaTc0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTc1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;
                    if (chromaRowsAvailable < 8) chromaTc1 = 0;

                    bool noP0 = GetPcmBypass(pixelX - 1, pixelY);
                    bool noP1 = GetPcmBypass(pixelX - 1, pixelY + chromaBsOffsetV);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX, pixelY + chromaBsOffsetV);

                    HevcDeblockingFilter.FilterChromaEdge8RowVertical(
                        cbPlane, chromaWidth, cx, cy, chromaTc0, chromaTc1,
                        noP0, noQ0, noP1, noQ1);

                    int chromaTcCr0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTcCr1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;
                    if (chromaRowsAvailable < 8) chromaTcCr1 = 0;

                    HevcDeblockingFilter.FilterChromaEdge8RowVertical(
                        crPlane, chromaWidth, cx, cy, chromaTcCr0, chromaTcCr1,
                        noP0, noQ0, noP1, noQ1);
                }
            }

            // Process horizontal luma edges at 8-pixel intervals
            for (int pixelY = 8; pixelY < height; pixelY += 8)
            {
                for (int pixelX = 0; pixelX < width; pixelX += 8)
                {
                    int y4 = pixelY >> 2;
                    int x4left = pixelX >> 2;
                    int x4right = (pixelX + 4) >> 2;
                    int bs0 = horizBsTab[y4 * bsWidth + x4left];
                    int bs1 = (pixelX + 4 < width) ? horizBsTab[y4 * bsWidth + x4right] : 0;

                    if (bs0 == 0 && bs1 == 0) continue;
                    if (pixelY < 4 || pixelY + 4 > height) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int cbX = pixelX >> log2MinCbSize;
                    int cbYP = (pixelY - 1) >> log2MinCbSize;
                    int cbYQ = pixelY >> log2MinCbSize;
                    int qpP = qpYTab[cbYP * minCbWidth + cbX];
                    int qpQ = qpYTab[cbYQ * minCbWidth + cbX];
                    int qpL = (qpP + qpQ + 1) >> 1;

                    int beta = HevcDeblockingFilter.GetBeta(qpL, betaOffset, bitDepth);
                    int tc0 = bs0 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs0, tcOffset, bitDepth) : 0;
                    int tc1 = bs1 > 0 ? HevcDeblockingFilter.GetTc(qpL, bs1, tcOffset, bitDepth) : 0;

                    int colsAvailable = Math.Min(8, width - pixelX);
                    if (colsAvailable < 8) tc1 = 0;

                    bool noP0 = GetPcmBypass(pixelX, pixelY - 1);
                    bool noP1 = GetPcmBypass(pixelX + 4, pixelY - 1);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX + 4, pixelY);

                    HevcDeblockingFilter.FilterLumaEdge8RowHorizontal(
                        lumaPlane, width, pixelX, pixelY, beta, tc0, tc1,
                        noP0, noQ0, noP1, noQ1);
                }
            }

            // Process horizontal chroma edges
            // FFmpeg filter.c:614,649: y += 8*v, x += 8*h, bs1 at x + 4*h
            for (int pixelY = chromaEdgeStepY; pixelY < height; pixelY += chromaEdgeStepY)
            {
                for (int pixelX = 0; pixelX < width; pixelX += chromaEdgeStepX)
                {
                    int y4 = pixelY >> 2;
                    int chromaBs0 = horizBsTab[y4 * bsWidth + (pixelX >> 2)];
                    int chromaBs1 = (pixelX + chromaBsOffsetH < width) ? horizBsTab[y4 * bsWidth + ((pixelX + chromaBsOffsetH) >> 2)] : 0;

                    if (chromaBs0 < 2 && chromaBs1 < 2) continue;

                    int cx = pixelX >> hShift;
                    int cy = pixelY >> vShift;
                    if (cy < 2 || cy + 2 > chromaHeight || cx >= chromaWidth) continue;

                    int ctbAddr = (pixelY / ctbSize) * picWidthInCtbs + (pixelX / ctbSize);
                    if (ctuDeblockDisabled != null && ctuDeblockDisabled[ctbAddr]) continue;
                    int betaOffset = hasPerCtuParams ? ctuBetaOffset![ctbAddr] : 0;
                    int tcOffset = hasPerCtuParams ? ctuTcOffset![ctbAddr] : 0;

                    int chromaColumnsAvailable = chromaWidth - cx;

                    int cbYP = (pixelY - 1) >> log2MinCbSize;
                    int cbYQ = pixelY >> log2MinCbSize;
                    int cbX0 = pixelX >> log2MinCbSize;
                    int qpP0 = qpYTab[cbYP * minCbWidth + cbX0];
                    int qpQ0 = qpYTab[cbYQ * minCbWidth + cbX0];
                    int qp0 = (qpP0 + qpQ0 + 1) >> 1;

                    int cbX1 = (pixelX + chromaBsOffsetH) >> log2MinCbSize;
                    int qpP1 = (pixelX + chromaBsOffsetH < width) ? qpYTab[cbYP * minCbWidth + cbX1] : qpP0;
                    int qpQ1 = (pixelX + chromaBsOffsetH < width) ? qpYTab[cbYQ * minCbWidth + cbX1] : qpQ0;
                    int qp1 = (qpP1 + qpQ1 + 1) >> 1;

                    int chromaTc0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTc1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffset, tcOffset, chromaBitDepth, is420) : 0;

                    bool noP0 = GetPcmBypass(pixelX, pixelY - 1);
                    bool noP1 = GetPcmBypass(pixelX + chromaBsOffsetH, pixelY - 1);
                    bool noQ0 = GetPcmBypass(pixelX, pixelY);
                    bool noQ1 = GetPcmBypass(pixelX + chromaBsOffsetH, pixelY);

                    HevcDeblockingFilter.FilterChromaEdge8RowHorizontal(
                        cbPlane, chromaWidth, cx, cy, chromaTc0, chromaTc1, chromaColumnsAvailable,
                        noP0, noQ0, noP1, noQ1);

                    int chromaTcCr0 = chromaBs0 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp0, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;
                    int chromaTcCr1 = chromaBs1 >= 2 ? HevcDeblockingFilter.GetChromaTc(qp1, chromaQpOffsetCr, tcOffset, chromaBitDepth, is420) : 0;

                    HevcDeblockingFilter.FilterChromaEdge8RowHorizontal(
                        crPlane, chromaWidth, cx, cy, chromaTcCr0, chromaTcCr1, chromaColumnsAvailable,
                        noP0, noQ0, noP1, noQ1);
                }
            }
        }

        // Clear deblocking arrays for next frame (isPcmTab cleared after SAO in FinalizeAndBufferFrame)
        Array.Clear(vertBsTab);
        Array.Clear(horizBsTab);
        Array.Clear(isIntraTab8x8!);
    }

    private void ApplySao()
    {
        if (currentFrameBuffer == null || saoTypeIdxTab == null || saoOffsetValTab == null)
            return;

        var sps = context.ActiveSps;
        var pps = context.ActivePps;
        if (sps == null || pps == null) return;

        int ctbSize = sps.CtbSizeY;
        int picWidthInCtbs = sps.PicWidthInCtbsY;
        int picHeightInCtbs = sps.PicHeightInCtbsY;
        int bitDepth = sps.BitDepthLuma;
        int chromaBitDepth = sps.BitDepthChroma;
        int bytesPerSample = bitDepth > 8 ? 2 : 1;

        int width = sps.PictureWidthInLumaSamples;
        int height = sps.PictureHeightInLumaSamples;
        int chromaWidth = width >> sps.HShiftChroma;
        int chromaHeight = height >> sps.VShiftChroma;
        int chromaCtbSize = ctbSize >> sps.HShiftChroma;
        int chromaCtbSizeV = ctbSize >> sps.VShiftChroma;

        int lumaPlaneSize = width * height * bytesPerSample;
        int chromaPlaneSize = chromaWidth * chromaHeight * bytesPerSample;

        var buffer = currentFrameBuffer.AsSpan();
        int totalFrameBytes = lumaPlaneSize + 2 * chromaPlaneSize;

        // Pre-SAO snapshot: SAO edge offset reads neighbors from this copy
        // to prevent in-place corruption when CTUs are processed in raster order.
        // Without this, a CTU's SAO-modified boundary pixels corrupt neighbor
        // reads for the adjacent CTU (FFmpeg uses per-CTU backup buffers instead).
        byte[] preSaoCopy = ArrayPool<byte>.Shared.Rent(totalFrameBytes);
        buffer.Slice(0, totalFrameBytes).CopyTo(preSaoCopy);
        var readBuffer = preSaoCopy.AsSpan();

        try
        {
            bool noTileFilter = pps.TilesEnabled && !pps.LoopFilterAcrossTilesEnabled;

            for (int ctbY = 0; ctbY < picHeightInCtbs; ctbY++)
            {
                for (int ctbX = 0; ctbX < picWidthInCtbs; ctbX++)
                {
                    int ctbAddrRs = ctbY * picWidthInCtbs + ctbX;

                    // Compute tile/slice edge flags for SAO clamping (FFmpeg filter.c:300-328)
                    bool leftEdge = ctbX == 0;
                    bool rightEdge = ctbX + 1 >= picWidthInCtbs;
                    bool topEdge = ctbY == 0;
                    bool bottomEdge = ctbY + 1 >= picHeightInCtbs;

                    if (noTileFilter && pps.CtbAddrRsToTs != null && pps.TileIdPerTs != null)
                    {
                        int ctbAddrTs = pps.CtbAddrRsToTs[ctbAddrRs];
                        int curTileId = pps.TileIdPerTs[ctbAddrTs];
                        if (!leftEdge && curTileId != pps.TileIdPerTs[pps.CtbAddrRsToTs[ctbAddrRs - 1]])
                            leftEdge = true;
                        if (!rightEdge && curTileId != pps.TileIdPerTs[pps.CtbAddrRsToTs[ctbAddrRs + 1]])
                            rightEdge = true;
                        if (!topEdge && curTileId != pps.TileIdPerTs[pps.CtbAddrRsToTs[ctbAddrRs - picWidthInCtbs]])
                            topEdge = true;
                        if (!bottomEdge && curTileId != pps.TileIdPerTs[pps.CtbAddrRsToTs[ctbAddrRs + picWidthInCtbs]])
                            bottomEdge = true;
                    }

                    // Also check slice boundaries (when loop_filter_across_slices_enabled_flag is off)
                    if (tabSliceAddress != null && ctuLoopFilterAcrossSlices != null &&
                        !ctuLoopFilterAcrossSlices[ctbAddrRs])
                    {
                        int curSlice = tabSliceAddress[ctbAddrRs];
                        if (!leftEdge && tabSliceAddress[ctbAddrRs - 1] != curSlice)
                            leftEdge = true;
                        if (!rightEdge && tabSliceAddress[ctbAddrRs + 1] != curSlice)
                            rightEdge = true;
                        if (!topEdge && tabSliceAddress[ctbAddrRs - picWidthInCtbs] != curSlice)
                            topEdge = true;
                        if (!bottomEdge && tabSliceAddress[ctbAddrRs + picWidthInCtbs] != curSlice)
                            bottomEdge = true;
                    }

                    for (int cIdx = 0; cIdx < 3; cIdx++)
                    {
                        int baseIdx = ctbAddrRs * 3 + cIdx;
                        int typeIdx = saoTypeIdxTab[baseIdx];
                        if (typeIdx == 0) continue;

                        int offsetValBase = ctbAddrRs * 3 * 5 + cIdx * 5;
                        int bd = cIdx == 0 ? bitDepth : chromaBitDepth;

                        Span<int> offsets = stackalloc int[4];
                        for (int i = 0; i < 4; i++)
                            offsets[i] = saoOffsetValTab[offsetValBase + 1 + i];

                        int planeW, planeH, planeStride, planeOffset;
                        int ctuW, ctuH, ctuX0, ctuY0;

                        if (cIdx == 0)
                        {
                            planeW = width; planeH = height;
                            planeStride = width; planeOffset = 0;
                            ctuX0 = ctbX * ctbSize; ctuY0 = ctbY * ctbSize;
                            ctuW = Math.Min(ctbSize, width - ctuX0);
                            ctuH = Math.Min(ctbSize, height - ctuY0);
                        }
                        else
                        {
                            planeW = chromaWidth; planeH = chromaHeight;
                            planeStride = chromaWidth;
                            planeOffset = lumaPlaneSize + (cIdx == 2 ? chromaPlaneSize : 0);
                            ctuX0 = ctbX * chromaCtbSize; ctuY0 = ctbY * chromaCtbSizeV;
                            ctuW = Math.Min(chromaCtbSize, chromaWidth - ctuX0);
                            ctuH = Math.Min(chromaCtbSizeV, chromaHeight - ctuY0);
                        }

                        if (ctuW <= 0 || ctuH <= 0) continue;

                        if (bytesPerSample == 2)
                        {
                            var planeBytes = buffer.Slice(planeOffset, planeW * planeH * 2);
                            var plane = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(planeBytes);
                            var readPlaneBytes = readBuffer.Slice(planeOffset, planeW * planeH * 2);
                            var readPlane = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(readPlaneBytes);
                            int startIdx = ctuY0 * planeStride + ctuX0;

                            ApplySaoHighBitDepthDirect(plane, readPlane, planeStride, startIdx,
                                ctuW, ctuH, planeW, planeH, ctuX0, ctuY0,
                                typeIdx, offsets, bd,
                                saoEoClassTab![baseIdx],
                                saoBandPositionTab![baseIdx],
                                leftEdge, rightEdge, topEdge, bottomEdge);
                        }
                        else
                        {
                            var planeBytes = buffer.Slice(planeOffset, planeW * planeH);
                            var readPlaneBytes = readBuffer.Slice(planeOffset, planeW * planeH);
                            int startIdx = ctuY0 * planeStride + ctuX0;

                            ApplySao8BitDirect(planeBytes, readPlaneBytes, planeStride, startIdx,
                                ctuW, ctuH, planeW, planeH, ctuX0, ctuY0,
                                typeIdx, offsets, bd,
                                saoEoClassTab![baseIdx],
                                saoBandPositionTab![baseIdx],
                                leftEdge, rightEdge, topEdge, bottomEdge);
                        }
                    }
                }
            }

            // Restore PCM / transquant_bypass pixels that SAO may have modified.
            // Matches FFmpeg's restore_tqb_pixels() in filter.c.
            bool needPcmRestore = isPcmTab != null &&
                (pps.TransquantBypassEnabled || (sps.PcmLoopFilterDisabled && sps.PcmEnabled));
            if (needPcmRestore)
                RestorePcmPixelsAfterSao(buffer, readBuffer, sps, lumaPlaneSize, chromaPlaneSize, bytesPerSample);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(preSaoCopy);
        }
    }

    /// <summary>
    /// Restores pre-SAO pixel values for PCM and transquant_bypass blocks.
    /// Matches FFmpeg's restore_tqb_pixels() in filter.c.
    /// isPcmTab stores 2 for bypass blocks at 4×4 PU granularity.
    /// </summary>
    private void RestorePcmPixelsAfterSao(
        Span<byte> buffer, ReadOnlySpan<byte> preSao,
        HevcSequenceParameterSet sps,
        int lumaPlaneSize, int chromaPlaneSize, int bytesPerSample)
    {
        if (isPcmTab == null) return;

        int width = sps.PictureWidthInLumaSamples;
        int height = sps.PictureHeightInLumaSamples;
        int hShift = sps.HShiftChroma;
        int vShift = sps.VShiftChroma;
        int chromaW = width >> hShift;
        int chromaH = height >> vShift;

        // Scan isPcmTab at 4×4 PU granularity
        for (int puY = 0; puY < isPcmTab.Length / isPcmWidth; puY++)
        {
            for (int puX = 0; puX < isPcmWidth; puX++)
            {
                if (isPcmTab[puY * isPcmWidth + puX] == 0)
                    continue;

                // Restore luma: 4×4 block at (puX*4, puY*4)
                int lumaX = puX << 2;
                int lumaY = puY << 2;
                if (lumaX >= width || lumaY >= height) continue;

                int blockW = Math.Min(4, width - lumaX);
                int blockH = Math.Min(4, height - lumaY);

                for (int row = 0; row < blockH; row++)
                {
                    int srcOff = ((lumaY + row) * width + lumaX) * bytesPerSample;
                    int len = blockW * bytesPerSample;
                    preSao.Slice(srcOff, len).CopyTo(buffer.Slice(srcOff, len));
                }

                // Restore chroma block at PU position
                int chromaBlockW = 4 >> hShift;
                int chromaBlockH = 4 >> vShift;
                int chromaX = puX * chromaBlockW;
                int chromaY = puY * chromaBlockH;
                if (chromaX >= chromaW || chromaY >= chromaH) continue;

                int cbW = Math.Min(chromaBlockW, chromaW - chromaX);
                int cbH = Math.Min(chromaBlockH, chromaH - chromaY);

                for (int row = 0; row < cbH; row++)
                {
                    int cbOff = lumaPlaneSize + ((chromaY + row) * chromaW + chromaX) * bytesPerSample;
                    int crOff = lumaPlaneSize + chromaPlaneSize + ((chromaY + row) * chromaW + chromaX) * bytesPerSample;
                    int len = cbW * bytesPerSample;
                    preSao.Slice(cbOff, len).CopyTo(buffer.Slice(cbOff, len));
                    preSao.Slice(crOff, len).CopyTo(buffer.Slice(crOff, len));
                }
            }
        }
    }

    /// <summary>
    /// Applies SAO to a CTU region within a high-bit-depth plane.
    /// Reads pixel values from readPlane (pre-SAO snapshot) and writes results to plane.
    /// Only skips boundary pixels at actual frame edges, not at internal CTU edges.
    /// </summary>
    private static void ApplySaoHighBitDepthDirect(
        Span<ushort> plane, ReadOnlySpan<ushort> readPlane, int stride, int startIdx,
        int width, int height, int planeW, int planeH, int ctuX0, int ctuY0,
        int typeIdx, ReadOnlySpan<int> offsets, int bitDepth,
        int eoClass, int bandPosition,
        bool isLeftEdge, bool isRightEdge, bool isTopEdge, bool isBottomEdge)
    {
        int maxVal = (1 << bitDepth) - 1;

        if (typeIdx == 1) // Band offset
        {
            int bandShift = bitDepth - 5;
            Span<int> bandOffsets = stackalloc int[32];
            for (int i = 0; i < 4; i++)
            {
                int band = (bandPosition + i) & 31;
                bandOffsets[band] = offsets[i];
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = startIdx + y * stride + x;
                    int val = readPlane[idx];
                    int band = val >> bandShift;
                    int off = bandOffsets[band];
                    if (off != 0)
                        plane[idx] = (ushort)Math.Clamp(val + off, 0, maxVal);
                }
            }
        }
        else if (typeIdx == 2) // Edge offset
        {
            ReadOnlySpan<int> dx0 = [-1, 0, -1, 1];
            ReadOnlySpan<int> dy0 = [0, -1, -1, -1];
            ReadOnlySpan<int> dx1 = [1, 0, 1, -1];
            ReadOnlySpan<int> dy1 = [0, 1, 1, 1];

            int ndx0 = dx0[eoClass], ndy0 = dy0[eoClass];
            int ndx1 = dx1[eoClass], ndy1 = dy1[eoClass];

            // Only skip at frame edges where neighbor pixels don't exist.
            // At internal CTU boundaries, neighbors are valid in the full plane.
            bool isLeftFrameEdge = ctuX0 == 0;
            bool isRightFrameEdge = ctuX0 + width >= planeW;
            bool isTopFrameEdge = ctuY0 == 0;
            bool isBottomFrameEdge = ctuY0 + height >= planeH;

            // Combine frame edges with tile/slice boundary edges
            bool clampLeft = isLeftFrameEdge || isLeftEdge;
            bool clampRight = isRightFrameEdge || isRightEdge;
            bool clampTop = isTopFrameEdge || isTopEdge;
            bool clampBottom = isBottomFrameEdge || isBottomEdge;

            int startX = (ndx0 < 0 || ndx1 < 0) && clampLeft ? 1 : 0;
            int endX = (ndx0 > 0 || ndx1 > 0) && clampRight ? width - 1 : width;
            int startY = (ndy0 < 0 || ndy1 < 0) && clampTop ? 1 : 0;
            int endY = (ndy0 > 0 || ndy1 > 0) && clampBottom ? height - 1 : height;

            Span<int> catOffset = stackalloc int[5];
            catOffset[0] = offsets[0];
            catOffset[1] = offsets[1];
            catOffset[2] = 0;
            catOffset[3] = offsets[2]; // Already negated during parsing
            catOffset[4] = offsets[3]; // Already negated during parsing

            for (int y = startY; y < endY; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    int idx = startIdx + y * stride + x;
                    int cur = readPlane[idx];
                    int n0 = readPlane[startIdx + (y + ndy0) * stride + (x + ndx0)];
                    int n1 = readPlane[startIdx + (y + ndy1) * stride + (x + ndx1)];

                    int sign0 = Math.Sign(cur - n0);
                    int sign1 = Math.Sign(cur - n1);
                    int category = sign0 + sign1 + 2; // 0..4

                    int off = catOffset[category];
                    if (off != 0)
                        plane[idx] = (ushort)Math.Clamp(cur + off, 0, maxVal);
                }
            }
        }
    }

    /// <summary>
    /// Applies SAO to a CTU region within an 8-bit plane.
    /// Reads pixel values from readPlane (pre-SAO snapshot) and writes results to plane.
    /// </summary>
    private static void ApplySao8BitDirect(
        Span<byte> plane, ReadOnlySpan<byte> readPlane, int stride, int startIdx,
        int width, int height, int planeW, int planeH, int ctuX0, int ctuY0,
        int typeIdx, ReadOnlySpan<int> offsets, int bitDepth,
        int eoClass, int bandPosition,
        bool isLeftEdge, bool isRightEdge, bool isTopEdge, bool isBottomEdge)
    {
        int maxVal = (1 << bitDepth) - 1;

        if (typeIdx == 1) // Band offset
        {
            int bandShift = bitDepth - 5;
            Span<int> bandOffsets = stackalloc int[32];
            for (int i = 0; i < 4; i++)
            {
                int band = (bandPosition + i) & 31;
                bandOffsets[band] = offsets[i];
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = startIdx + y * stride + x;
                    int val = readPlane[idx];
                    int band = val >> bandShift;
                    int off = bandOffsets[band];
                    if (off != 0)
                        plane[idx] = (byte)Math.Clamp(val + off, 0, maxVal);
                }
            }
        }
        else if (typeIdx == 2) // Edge offset
        {
            ReadOnlySpan<int> dx0 = [-1, 0, -1, 1];
            ReadOnlySpan<int> dy0 = [0, -1, -1, -1];
            ReadOnlySpan<int> dx1 = [1, 0, 1, -1];
            ReadOnlySpan<int> dy1 = [0, 1, 1, 1];

            int ndx0 = dx0[eoClass], ndy0 = dy0[eoClass];
            int ndx1 = dx1[eoClass], ndy1 = dy1[eoClass];

            bool isLeftFrameEdge = ctuX0 == 0;
            bool isRightFrameEdge = ctuX0 + width >= planeW;
            bool isTopFrameEdge = ctuY0 == 0;
            bool isBottomFrameEdge = ctuY0 + height >= planeH;

            bool clampLeft = isLeftFrameEdge || isLeftEdge;
            bool clampRight = isRightFrameEdge || isRightEdge;
            bool clampTop = isTopFrameEdge || isTopEdge;
            bool clampBottom = isBottomFrameEdge || isBottomEdge;

            int startX = (ndx0 < 0 || ndx1 < 0) && clampLeft ? 1 : 0;
            int endX = (ndx0 > 0 || ndx1 > 0) && clampRight ? width - 1 : width;
            int startY = (ndy0 < 0 || ndy1 < 0) && clampTop ? 1 : 0;
            int endY = (ndy0 > 0 || ndy1 > 0) && clampBottom ? height - 1 : height;

            Span<int> catOffset = stackalloc int[5];
            catOffset[0] = offsets[0];
            catOffset[1] = offsets[1];
            catOffset[2] = 0;
            catOffset[3] = offsets[2];
            catOffset[4] = offsets[3];

            for (int y = startY; y < endY; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    int idx = startIdx + y * stride + x;
                    int cur = readPlane[idx];
                    int n0 = readPlane[startIdx + (y + ndy0) * stride + (x + ndx0)];
                    int n1 = readPlane[startIdx + (y + ndy1) * stride + (x + ndx1)];

                    int sign0 = Math.Sign(cur - n0);
                    int sign1 = Math.Sign(cur - n1);
                    int category = sign0 + sign1 + 2;

                    int off = catOffset[category];
                    if (off != 0)
                        plane[idx] = (byte)Math.Clamp(cur + off, 0, maxVal);
                }
            }
        }
    }

    private void StoreReferenceFrame()
    {
        if (currentFrameBuffer == null)
            return;

        // Hard cap: if DPB is completely full, force-evict the frame with no flags (or oldest)
        if (dpbCount >= MaxDpbFrames)
        {
            int evictIdx = -1;
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].Flags == 0)
                { evictIdx = i; break; }
            }
            if (evictIdx < 0)
            {
                evictIdx = 0;
                for (int i = 1; i < dpbCount; i++)
                    if (dpb[i].Poc < dpb[evictIdx].Poc) evictIdx = i;
            }
            FreeDpbEntry(evictIdx);
        }

        // --- Step 1: Add current frame to DPB (FFmpeg: ff_hevc_set_new_ref) ---
        // Set flags: always SHORT_REF, plus OUTPUT if pic_output_flag
        byte flags = FrameFlagShortRef;
        if (currentSliceHeader!.PicOutputFlag)
            flags |= FrameFlagOutput;

        // Allocate new buffer and copy pixel data for reference
        int frameSize = currentFrameBuffer.Length;
        byte[] refBuffer = ArrayPool<byte>.Shared.Rent(frameSize);
        currentFrameBuffer.AsSpan(0, frameSize).CopyTo(refBuffer);

        // Copy MV field for temporal MVP
        int fieldSize = puWidthIn4 * puHeightIn4;
        short[]? refMvL0X = null, refMvL0Y = null, refMvL1X = null, refMvL1Y = null;
        sbyte[]? refRefIdxL0 = null, refRefIdxL1 = null;
        byte[]? refPredFlags = null;

        if (mvFieldL0X != null && fieldSize > 0)
        {
            refMvL0X = new short[fieldSize];
            refMvL0Y = new short[fieldSize];
            refMvL1X = new short[fieldSize];
            refMvL1Y = new short[fieldSize];
            refRefIdxL0 = new sbyte[fieldSize];
            refRefIdxL1 = new sbyte[fieldSize];
            refPredFlags = new byte[fieldSize];

            mvFieldL0X.AsSpan(0, fieldSize).CopyTo(refMvL0X);
            mvFieldL0Y!.AsSpan(0, fieldSize).CopyTo(refMvL0Y);
            mvFieldL1X!.AsSpan(0, fieldSize).CopyTo(refMvL1X);
            mvFieldL1Y!.AsSpan(0, fieldSize).CopyTo(refMvL1Y);
            refIdxFieldL0!.AsSpan(0, fieldSize).CopyTo(refRefIdxL0);
            refIdxFieldL1!.AsSpan(0, fieldSize).CopyTo(refRefIdxL1);
            predModeField!.AsSpan(0, fieldSize).CopyTo(refPredFlags);
        }

        // Attach per-CTB ref list snapshots for temporal MVP and deblocking
        SliceRefListSnapshot[]? sliceRefLists = currentFrameSliceRefLists?.ToArray();
        byte[]? perCtbSliceIdx = null;
        int totalCtbs = context.PicWidthInCtbsY * context.PicHeightInCtbsY;
        if (currentFramePerCtbSliceIdx != null && totalCtbs > 0)
        {
            perCtbSliceIdx = new byte[totalCtbs];
            Array.Copy(currentFramePerCtbSliceIdx, perCtbSliceIdx, totalCtbs);
        }

        // Compute conformance window crop offsets for output
        var sps = context.ActiveSps;
        int cropLeftLuma = 0, cropTopLuma = 0, cropLeftChroma = 0, cropTopChroma = 0;
        if (sps != null && sps.ConformanceWindowFlag)
        {
            int subWidthC = sps.ChromaFormatIdc == HevcChromaFormat.Chroma444 ? 1 : 2;
            int subHeightC = sps.ChromaFormatIdc == HevcChromaFormat.Chroma420 ? 2 : 1;
            cropLeftLuma = sps.ConfWinLeftOffset * subWidthC;
            cropTopLuma = sps.ConfWinTopOffset * subHeightC;
            cropLeftChroma = sps.ConfWinLeftOffset;
            cropTopChroma = sps.ConfWinTopOffset;
        }

        dpb[dpbCount++] = new HevcReferenceFrame
        {
            Buffer = refBuffer,
            Poc = currentPoc,
            PresentationTimeTicks = currentPts,
            Flags = flags,
            MvL0X = refMvL0X,
            MvL0Y = refMvL0Y,
            MvL1X = refMvL1X,
            MvL1Y = refMvL1Y,
            RefIdxL0 = refRefIdxL0,
            RefIdxL1 = refRefIdxL1,
            PredFlags = refPredFlags,
            PuWidthIn4 = puWidthIn4,
            PuHeightIn4 = puHeightIn4,
            PerCtbSliceRefIdx = perCtbSliceIdx,
            SliceRefListSnapshots = sliceRefLists,
            NumRefList0 = numRefList0,
            NumRefList1 = numRefList1,
            DisplayWidth = context.Width,
            DisplayHeight = context.Height,
            CodedWidth = context.CodedWidth,
            CodedHeight = context.CodedHeight,
            BytesPerSample = context.BitDepthLuma > 8 ? 2 : 1,
            ChromaFormat = sps?.ChromaFormatIdc ?? HevcChromaFormat.Chroma420,
            CropLeftLuma = cropLeftLuma,
            CropTopLuma = cropTopLuma,
            CropLeftChroma = cropLeftChroma,
            CropTopChroma = cropTopChroma,
        };

        // --- Step 2: Process RPS (FFmpeg: ff_hevc_frame_rps) ---
        // Clear REF from all non-current frames, then re-add for RPS members.
        // Frames with no remaining flags (no REF, no OUTPUT) are freed.
        ProcessReferencePictureSet();

        framesDecoded++;
    }

    /// <summary>
    /// Processes the Reference Picture Set for the current frame.
    /// Matches FFmpeg's ff_hevc_frame_rps: clears REF from all non-current DPB entries,
    /// re-adds REF for frames that are in the current RPS, then frees dead entries (flags=0).
    /// </summary>
    private void ProcessReferencePictureSet()
    {
        var rps = currentSliceHeader?.ShortTermRps;
        var ltRps = currentSliceHeader?.LongTermRps;

        // Build the set of POCs that should have REF flag
        Span<int> rpsPocs = stackalloc int[32];
        Span<bool> rpsIsLongTerm = stackalloc bool[32];
        Span<bool> rpsPocMsbPresent = stackalloc bool[32];
        int rpsCount = 0;

        if (rps != null)
        {
            for (int i = 0; i < rps.NumDeltaPocs; i++)
            {
                rpsPocs[rpsCount] = currentPoc + rps.DeltaPoc[i];
                rpsIsLongTerm[rpsCount] = false;
                rpsPocMsbPresent[rpsCount] = true; // short-term always uses full POC
                rpsCount++;
            }
        }
        if (ltRps != null)
        {
            for (int i = 0; i < ltRps.NumRefs; i++)
            {
                rpsPocs[rpsCount] = ltRps.Poc[i];
                rpsIsLongTerm[rpsCount] = true;
                rpsPocMsbPresent[rpsCount] = ltRps.PocMsbPresent[i];
                rpsCount++;
            }
        }

        // Phase 1: Clear REF from all non-current frames
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].Poc == currentPoc)
                continue; // current frame keeps its flags
            dpb[i].Flags &= unchecked((byte)~FrameFlagRef); // clear SHORT_REF and LONG_REF
        }

        // Phase 2: Re-add REF for RPS members
        // Long-term refs without poc_msb_present use LSB-only matching (FFmpeg find_ref_idx)
        int pocLsbMask = (context.ActiveSps != null)
            ? (1 << (context.ActiveSps.Log2MaxPicOrderCntLsbMinus4 + 4)) - 1
            : 0xFF;

        for (int r = 0; r < rpsCount; r++)
        {
            int poc = rpsPocs[r];
            byte refFlag = rpsIsLongTerm[r] ? FrameFlagLongRef : FrameFlagShortRef;
            bool useMsb = rpsPocMsbPresent[r];
            bool found = false;

            for (int i = 0; i < dpbCount; i++)
            {
                if (useMsb)
                {
                    if (dpb[i].Poc == poc)
                    {
                        dpb[i].Flags |= refFlag;
                        found = true;
                        break;
                    }
                }
                else
                {
                    // LSB-only match, exclude current frame (matches FFmpeg find_ref_idx)
                    if ((dpb[i].Poc & pocLsbMask) == poc && dpb[i].Poc != currentPoc)
                    {
                        dpb[i].Flags |= refFlag;
                        found = true;
                        DiagDecodeCallLog?.Add($"  RPS-LT: LSB match poc={poc} mask=0x{pocLsbMask:x} → dpb[{i}].Poc={dpb[i].Poc}");
                        break;
                    }
                }
            }
            if (!found && rpsIsLongTerm[r])
                DiagDecodeCallLog?.Add($"  RPS-LT: NOT FOUND poc={poc} msb={useMsb} mask=0x{pocLsbMask:x} dpb=[{string.Join(",", Enumerable.Range(0, dpbCount).Select(i => dpb[i].Poc))}]");
        }

        // Phase 3: Free entries with no flags (no REF, no OUTPUT)
        // Scan backwards to avoid shifting issues
        for (int i = dpbCount - 1; i >= 0; i--)
        {
            if (dpb[i].Flags == 0)
                FreeDpbEntry(i);
        }
    }

    /// <summary>
    /// Frees a DPB entry: returns its buffer to the pool and compacts the array.
    /// Keeps interLayerRefDpbIdx consistent when compaction shifts entries.
    /// </summary>
    private void FreeDpbEntry(int index)
    {
        // Don't return borrowed inter-layer ref buffers to the pool — they belong to another layer
        if (index != interLayerRefDpbIdx && dpb[index].Buffer != null)
            ArrayPool<byte>.Shared.Return(dpb[index].Buffer!);

        for (int i = index; i < dpbCount - 1; i++)
            dpb[i] = dpb[i + 1];
        dpb[--dpbCount] = default;

        // Track compaction: if the inter-layer ref was above the freed slot, it shifted down
        if (interLayerRefDpbIdx > index)
            interLayerRefDpbIdx--;
        else if (interLayerRefDpbIdx == index)
            interLayerRefDpbIdx = -1; // IL ref itself was freed (shouldn't happen — it has SHORT_REF)
    }


    private void ClearDpb()
    {
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].Buffer != null)
                ArrayPool<byte>.Shared.Return(dpb[i].Buffer!);
            dpb[i] = default;
        }
        dpbCount = 0;
    }
    
    /// <summary>
    /// Outputs ALL lowest-POC frames that exceed the reorder or DPB thresholds.
    /// Matches FFmpeg's ff_hevc_output_frames while(1) loop with its dual condition:
    ///   nb_output > max_output  OR  (nb_output > 0 AND nb_dpb > max_dpb)
    /// All bumped frames are enqueued to outputQueue to preserve correct display order
    /// across multiple Decode calls.
    /// </summary>
    private void TryOutputReorderedFrame()
    {
        while (true)
        {
            // Count nb_output and nb_dpb from DPB flags (matches FFmpeg refs.c:282-297)
            int nbOutput = 0;
            int nbDpb = 0;
            int minPoc = int.MaxValue;
            int minIdx = -1;

            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].Flags != 0)
                    nbDpb++;
                if ((dpb[i].Flags & FrameFlagOutput) != 0)
                {
                    nbOutput++;
                    if (dpb[i].Poc < minPoc)
                    {
                        minPoc = dpb[i].Poc;
                        minIdx = i;
                    }
                }
            }

            // FFmpeg's dual condition (refs.c:300-302)
            bool shouldBump = nbOutput > maxNumReorderFrames ||
                              (nbOutput > 0 && nbDpb > maxDecPicBuffering);

            if (!shouldBump || minIdx < 0)
                break;

            // Output the lowest-POC OUTPUT frame
            var frame = CreateOutputFrameFromDpb(minIdx);

            // Clear OUTPUT flag (FFmpeg: ff_hevc_unref_frame(frame, HEVC_FRAME_FLAG_OUTPUT))
            dpb[minIdx].Flags &= unchecked((byte)~FrameFlagOutput);

            // If no flags remain, free the DPB entry entirely
            if (dpb[minIdx].Flags == 0)
                FreeDpbEntry(minIdx);

            if (frame != null)
                outputQueue.Enqueue(frame);
        }
    }
    
    /// <summary>
    /// Multi-layer DPB output: scans all active layers' DPBs for lowest-POC frames.
    /// Matches FFmpeg's ff_hevc_output_frames (refs.c:266-323) which iterates over layers.
    /// At each POC, outputs from each active output layer, producing the interleaved
    /// frame ordering expected by FATE (view0/poc0, view1/poc0, view0/poc1, view1/poc1, ...).
    /// </summary>
    private void TryOutputReorderedFrameMultiLayer()
    {
        // Save current layer (we'll need to switch between layers to access their DPBs)
        int savedLayer = curLayer;
        
        while (true)
        {
            // Scan all active layers' DPBs for output-pending frames (FFmpeg refs.c:276-298)
            int nbOutput = 0;
            int minPoc = int.MaxValue;
            int minLayer = -1;
            int minIdx = -1;
            ushort minViewId = ushort.MaxValue;
            Span<int> nbDpb = stackalloc int[nbLayers];
            var vps = context.ActiveVps;
            
            for (int layer = 0; layer < nbLayers; layer++)
            {
                if ((layersActiveDecode & (1u << layer)) == 0)
                    continue;
                
                var layerDpb = layers[layer].Dpb;
                int layerDpbCount = layer == curLayer ? dpbCount : layers[layer].DpbCount;
                ushort layerViewId = vps?.ViewId[layer] ?? (ushort)layer;
                
                DiagDecodeCallLog?.Add($"    MLOutput scan layer={layer} viewId={layerViewId} dpbCount={layerDpbCount} isCurLayer={layer == curLayer}");
                
                for (int i = 0; i < layerDpbCount; i++)
                {
                    if (layerDpb[i].Flags != 0)
                        nbDpb[layer]++;
                    if ((layerDpb[i].Flags & FrameFlagOutput) != 0)
                    {
                        // FFmpeg refs.c:284-289: nb_output counts AUs, not individual layer frames.
                        // A layer>0 frame whose base layer (layer 0) also has OUTPUT pending at the
                        // same POC is NOT counted — it's part of the same AU as the base layer frame.
                        bool isBaseLayerDuplicate = false;
                        if (layer > 0)
                        {
                            var baseDpb = layers[0].Dpb;
                            int baseDpbCount = 0 == curLayer ? dpbCount : layers[0].DpbCount;
                            for (int j = 0; j < baseDpbCount; j++)
                            {
                                if (baseDpb[j].Poc == layerDpb[i].Poc &&
                                    (baseDpb[j].Flags & FrameFlagOutput) != 0)
                                {
                                    isBaseLayerDuplicate = true;
                                    break;
                                }
                            }
                        }
                        if (!isBaseLayerDuplicate)
                            nbOutput++;
                        
                        DiagDecodeCallLog?.Add($"    MLOutput candidate: layer={layer} i={i} poc={layerDpb[i].Poc} viewId={layerViewId} flags=0x{layerDpb[i].Flags:x2} baseDup={isBaseLayerDuplicate}");
                        if (minLayer < 0 || layerDpb[i].Poc < minPoc ||
                            (layerDpb[i].Poc == minPoc && layerViewId < minViewId))
                        {
                            minPoc = layerDpb[i].Poc;
                            minIdx = i;
                            minLayer = layer;
                            minViewId = layerViewId;
                            DiagDecodeCallLog?.Add($"    MLOutput → new min: poc={minPoc} layer={minLayer} viewId={minViewId}");
                        }
                    }
                }
            }
            
            // Use the live decoder values for the current layer's DPB config.
            // SaveLayerState may not have been called yet, so layers[].Max* can be stale.
            int maxReorder = maxNumReorderFrames;
            int maxDpbBuf = maxDecPicBuffering;
            
            // FFmpeg's dual condition extended for multi-layer (refs.c:300-302)
            bool shouldBump = nbOutput > maxReorder;
            if (!shouldBump)
            {
                for (int layer = 0; layer < nbLayers; layer++)
                {
                    if (nbOutput > 0 && nbDpb[layer] > maxDpbBuf)
                    {
                        shouldBump = true;
                        break;
                    }
                }
            }
            
            DiagDecodeCallLog?.Add($"    MLOutput decision: nbOutput={nbOutput} maxReorder={maxReorder} maxDpb={maxDpbBuf} shouldBump={shouldBump} pick=layer{minLayer}[{minIdx}] poc={minPoc} viewId={minViewId}");
            
            if (!shouldBump || minLayer < 0)
                break;
            
            // Switch to the layer that has the min-POC frame
            if (minLayer != curLayer)
                SwitchLayer(minLayer);
            
            bool doOutput = (layersActiveOutput & (1u << minLayer)) != 0;
            
            if (doOutput)
            {
                var frame = CreateOutputFrameFromDpb(minIdx);
                if (frame != null)
                    outputQueue.Enqueue(frame);
            }
            
            // Clear OUTPUT flag
            dpb[minIdx].Flags &= unchecked((byte)~FrameFlagOutput);
            if (dpb[minIdx].Flags == 0)
                FreeDpbEntry(minIdx);
        }
        
        // Restore original layer
        if (curLayer != savedLayer)
            SwitchLayer(savedLayer);
    }

    /// <summary>
    /// Creates a DecodedVideoFrame from a DPB entry.
    /// If the entry still has REF flags, the buffer is copied (DPB keeps the original).
    /// If the entry has only OUTPUT, the buffer is transferred (DPB entry will be freed).
    /// </summary>
    private DecodedVideoFrame? CreateOutputFrameFromDpb(int dpbIdx)
    {
        ref var entry = ref dpb[dpbIdx];
        if (entry.Buffer == null)
            return null;

        bool hasRef = (entry.Flags & FrameFlagRef) != 0;
        byte[] buffer;

        if (hasRef)
        {
            // Frame is still a reference — copy buffer, DPB keeps the original
            int hShift = entry.ChromaFormat is HevcChromaFormat.Chroma420 or HevcChromaFormat.Chroma422 ? 1 : 0;
            int vShift = entry.ChromaFormat == HevcChromaFormat.Chroma420 ? 1 : 0;
            int yPlaneSize = entry.CodedWidth * entry.CodedHeight * entry.BytesPerSample;
            int uvPlaneSize = (entry.CodedWidth >> hShift) * (entry.CodedHeight >> vShift) * entry.BytesPerSample;
            int totalSize = yPlaneSize + 2 * uvPlaneSize;
            buffer = ArrayPool<byte>.Shared.Rent(totalSize);
            entry.Buffer.AsSpan(0, totalSize).CopyTo(buffer);
        }
        else
        {
            // Frame is OUTPUT-only — transfer buffer ownership, DPB entry will be freed
            buffer = entry.Buffer;
            entry.Buffer = null; // prevent FreeDpbEntry from returning it to pool
        }

        int hShiftOut = entry.ChromaFormat is HevcChromaFormat.Chroma420 or HevcChromaFormat.Chroma422 ? 1 : 0;
        int vShiftOut = entry.ChromaFormat == HevcChromaFormat.Chroma420 ? 1 : 0;
        int ySize = entry.CodedWidth * entry.CodedHeight * entry.BytesPerSample;
        int uvSize = (entry.CodedWidth >> hShiftOut) * (entry.CodedHeight >> vShiftOut) * entry.BytesPerSample;
        var format = DerivePixelFormat(entry.ChromaFormat, entry.BytesPerSample, context.ActiveSps?.BitDepthChroma ?? 8);

        int yStride = entry.CodedWidth * entry.BytesPerSample;
        int uvStride = (entry.CodedWidth >> hShiftOut) * entry.BytesPerSample;
        int yOffset = entry.CropTopLuma * yStride + entry.CropLeftLuma * entry.BytesPerSample;
        int uOffset = ySize + entry.CropTopChroma * uvStride + entry.CropLeftChroma * entry.BytesPerSample;
        int vOffset = ySize + uvSize + entry.CropTopChroma * uvStride + entry.CropLeftChroma * entry.BytesPerSample;

        DiagDecodeCallLog?.Add($"  OutputFrame: poc={entry.Poc} layer={curLayer} disp={entry.DisplayWidth}x{entry.DisplayHeight} coded={entry.CodedWidth}x{entry.CodedHeight} bps={entry.BytesPerSample} chroma={entry.ChromaFormat} cropL={entry.CropLeftLuma} cropT={entry.CropTopLuma} yOff={yOffset} uOff={uOffset} vOff={vOffset} yStride={yStride} uvStride={uvStride} hasRef={hasRef} bufLen={buffer.Length}");

        return new DecodedVideoFrame(
            entry.DisplayWidth, entry.DisplayHeight,
            format,
            entry.PresentationTimeTicks,
            buffer,
            yOffset: yOffset, yStride: yStride,
            uOffset: uOffset, uStride: uvStride,
            vOffset: vOffset, vStride: uvStride);
    }

    private static PixelFormat DerivePixelFormat(HevcChromaFormat chromaFormat, int bytesPerSample, int bitDepth)
    {
        bool is8Bit = bytesPerSample == 1;
        bool is12Bit = bitDepth == 12;
        return chromaFormat switch
        {
            HevcChromaFormat.Chroma420 => is8Bit ? PixelFormat.Yuv420P : is12Bit ? PixelFormat.Yuv420P12 : PixelFormat.Yuv420P10,
            HevcChromaFormat.Chroma422 => is8Bit ? PixelFormat.Yuv422P : is12Bit ? PixelFormat.Yuv422P12 : PixelFormat.Yuv422P10,
            HevcChromaFormat.Chroma444 => is8Bit ? PixelFormat.Yuv444P : is12Bit ? PixelFormat.Yuv444P12 : PixelFormat.Yuv444P10,
            _ => is8Bit ? PixelFormat.Yuv420P : PixelFormat.Yuv420P10,
        };
    }

    /// <summary>
    /// Counts how many DPB entries have the OUTPUT flag (output-pending).
    /// </summary>
    private int CountDpbOutputPending()
    {
        int count = 0;
        for (int i = 0; i < dpbCount; i++)
            if ((dpb[i].Flags & FrameFlagOutput) != 0) count++;
        return count;
    }

    /// <summary>
    /// Drains all OUTPUT-flagged DPB frames in POC order into the output queue.
    /// Called at IDR/IRAP boundaries and during flush so pending frames are not lost.
    /// </summary>
    private void DrainDpbOutput()
    {
        while (true)
        {
            // Find lowest-POC OUTPUT frame
            int minPoc = int.MaxValue;
            int minIdx = -1;
            for (int i = 0; i < dpbCount; i++)
            {
                if ((dpb[i].Flags & FrameFlagOutput) != 0 && dpb[i].Poc < minPoc)
                {
                    minPoc = dpb[i].Poc;
                    minIdx = i;
                }
            }
            if (minIdx < 0) break;

            var frame = CreateOutputFrameFromDpb(minIdx);
            dpb[minIdx].Flags &= unchecked((byte)~FrameFlagOutput);
            if (dpb[minIdx].Flags == 0)
                FreeDpbEntry(minIdx);

            if (frame != null)
                outputQueue.Enqueue(frame);
        }
    }

    /// <summary>
    /// Discards all OUTPUT-flagged DPB frames WITHOUT outputting them.
    /// Called at IRAP when no_output_of_prior_pics_flag is set.
    /// Matches FFmpeg's ff_hevc_output_frames with discard=1.
    /// </summary>
    private void DiscardDpbOutput()
    {
        for (int i = dpbCount - 1; i >= 0; i--)
        {
            if ((dpb[i].Flags & FrameFlagOutput) != 0)
            {
                dpb[i].Flags &= unchecked((byte)~FrameFlagOutput);
                if (dpb[i].Flags == 0)
                    FreeDpbEntry(i);
            }
        }
    }

    /// <summary>
    /// Multi-layer version of DiscardDpbOutput — discards OUTPUT-flagged frames in ALL layers.
    /// </summary>
    private void DiscardDpbOutputMultiLayer()
    {
        int savedLayer = curLayer;
        
        for (int layer = 0; layer < nbLayers; layer++)
        {
            if ((layersActiveDecode & (1u << layer)) == 0)
                continue;
            
            if (layer != curLayer)
                SwitchLayer(layer);
            
            DiscardDpbOutput();
        }
        
        if (curLayer != savedLayer)
            SwitchLayer(savedLayer);
    }

    /// <summary>
    /// Peeks at the no_output_of_prior_pics_flag from an IRAP NAL without fully parsing.
    /// The flag is the 2nd bit in the slice header (after first_slice_segment_in_pic_flag).
    /// </summary>
    private static bool PeekNoOutputOfPriorPicsFlag(HevcNalUnit nal)
    {
        var payload = nal.Payload.Span;
        if (payload.Length < 1)
            return false;
        
        // first_slice_segment_in_pic_flag is bit 7, no_output_of_prior_pics_flag is bit 6
        return (payload[0] & 0x40) != 0;
    }

    public void DrainPendingFrames()
    {
        if (nbLayers > 1)
            DrainDpbOutputMultiLayer();
        else
            DrainDpbOutput();
    }

    /// <summary>
    /// Drains all OUTPUT-flagged frames from ALL layers' DPBs, outputting in POC order
    /// with base layer before enhancement for the same POC.
    /// Equivalent to FFmpeg's ff_hevc_output_frames(s, ..., 0, 0, 0) at end-of-stream.
    /// </summary>
    private void DrainDpbOutputMultiLayer()
    {
        int savedLayer = curLayer;
        var vps = context.ActiveVps;
        
        while (true)
        {
            int minPoc = int.MaxValue;
            int minLayer = -1;
            int minIdx = -1;
            ushort minViewId = ushort.MaxValue;
            
            for (int layer = 0; layer < nbLayers; layer++)
            {
                if ((layersActiveDecode & (1u << layer)) == 0)
                    continue;
                
                var layerDpb = layers[layer].Dpb;
                int layerDpbCount = layer == curLayer ? dpbCount : layers[layer].DpbCount;
                ushort layerViewId = vps?.ViewId[layer] ?? (ushort)layer;
                
                for (int i = 0; i < layerDpbCount; i++)
                {
                    if ((layerDpb[i].Flags & FrameFlagOutput) != 0)
                    {
                        // Lower POC wins; at same POC, lower view_id wins
                        // (matches FFmpeg's -map "0:view:0" -map "0:view:1" output ordering)
                        if (layerDpb[i].Poc < minPoc || 
                            (layerDpb[i].Poc == minPoc && layerViewId < minViewId))
                        {
                            minPoc = layerDpb[i].Poc;
                            minIdx = i;
                            minLayer = layer;
                            minViewId = layerViewId;
                        }
                    }
                }
            }
            
            if (minLayer < 0) break;
            
            if (minLayer != curLayer)
                SwitchLayer(minLayer);
            
            bool doOutput = (layersActiveOutput & (1u << minLayer)) != 0;
            
            if (doOutput)
            {
                var frame = CreateOutputFrameFromDpb(minIdx);
                if (frame != null)
                    outputQueue.Enqueue(frame);
            }
            
            dpb[minIdx].Flags &= unchecked((byte)~FrameFlagOutput);
            if (dpb[minIdx].Flags == 0)
                FreeDpbEntry(minIdx);
        }
        
        if (curLayer != savedLayer)
            SwitchLayer(savedLayer);
    }

    public void Flush()
    {
        // Discard any pending output frames and all DPB references
        ClearDpb();
        
        while (outputQueue.Count > 0)
        {
            var frame = outputQueue.Dequeue();
            frame.Dispose();
        }
        
        currentPoc = 0;
        pocTid0 = 0;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        Flush();

        if (currentFrameBuffer != null)
            ArrayPool<byte>.Shared.Return(currentFrameBuffer);

        isDisposed = true;
    }
}

/// <summary>
/// Reference frame in the HEVC DPB.
/// </summary>
internal struct HevcReferenceFrame
{
    public byte[]? Buffer;
    public int Poc;
    public long PresentationTimeTicks;
    public byte Flags; // FrameFlagOutput | FrameFlagShortRef | FrameFlagLongRef | FrameFlagUnavailable

    // Temporal MVP: per-4x4 motion vector field (copied when frame is stored as reference)
    public short[]? MvL0X;
    public short[]? MvL0Y;
    public short[]? MvL1X;
    public short[]? MvL1Y;
    public sbyte[]? RefIdxL0;
    public sbyte[]? RefIdxL1;
    public byte[]? PredFlags;
    public int PuWidthIn4;
    public int PuHeightIn4;

    // Per-CTB ref list storage for temporal MV prediction and deblocking (FFmpeg's rpl_tab).
    // Each slice in the frame may have different ref lists; per-CTB indexing resolves the correct one.
    public byte[]? PerCtbSliceRefIdx;                  // [ctbRsAddr] → index into SliceRefListSnapshots
    public SliceRefListSnapshot[]? SliceRefListSnapshots; // one per slice in the frame

    // Legacy per-frame ref lists (kept for I-frames and single-slice fallback)
    public int NumRefList0;
    public int NumRefList1;

    // Output frame metadata (populated when FrameFlagOutput is set)
    public int DisplayWidth;
    public int DisplayHeight;
    public int CodedWidth;
    public int CodedHeight;
    public int BytesPerSample;
    public HevcChromaFormat ChromaFormat;
    public int CropLeftLuma;
    public int CropTopLuma;
    public int CropLeftChroma;
    public int CropTopChroma;
}

/// <summary>
/// Snapshot of a slice's reference picture lists, stored for per-CTB ref list resolution.
/// Matches FFmpeg's per-CTB RefPicListTab entries (rpl_tab).
/// </summary>
internal sealed class SliceRefListSnapshot
{
    public readonly int[] PocL0;
    public readonly int[] PocL1;
    public readonly bool[] IsLtL0;
    public readonly bool[] IsLtL1;
    public readonly int CountL0;
    public readonly int CountL1;

    public SliceRefListSnapshot(
        ReadOnlySpan<int> pocL0, int countL0, ReadOnlySpan<bool> isLtL0,
        ReadOnlySpan<int> pocL1, int countL1, ReadOnlySpan<bool> isLtL1)
    {
        CountL0 = countL0;
        CountL1 = countL1;
        PocL0 = pocL0[..countL0].ToArray();
        PocL1 = pocL1[..countL1].ToArray();
        IsLtL0 = isLtL0[..countL0].ToArray();
        IsLtL1 = isLtL1[..countL1].ToArray();
    }
}

/// <summary>
/// Per-layer decoder state for MV-HEVC (multi-view).
/// Each layer has its own DPB, current frame, and output configuration.
/// Matches FFmpeg's HEVCLayerContext (hevcdec.h:452-488) for the subset of state
/// that must be distinct per-layer. Working buffers (per-picture tables, CABAC state,
/// residual buffers) are shared since layers decode sequentially.
/// </summary>
internal sealed class HevcLayerState
{
    public readonly HevcReferenceFrame[] Dpb;
    public int DpbCount;
    
    // Current frame being decoded in this layer
    public byte[]? CurrentFrameBuffer;
    public int CurrentPoc;
    public bool CurrentIsReference;
    public long CurrentPts;
    
    // DPB configuration (can differ per layer via VPS DpbSize)
    public int MaxNumReorderFrames;
    public int MaxDecPicBuffering;
    
    // SPS change tracking (per-layer since layers can use different SPS)
    public int ActiveSpsWidth;
    public int ActiveSpsHeight;
    public HevcSequenceParameterSet? LastAllocatedSps;
    
    // WPP state (per-layer: each layer's WPP contexts are independent)
    public byte[]? WppSavedContexts;
    public int[]? WppSavedStatCoeff;
    
    // Per-frame ref list accumulation (per-layer since each frame has its own ref lists)
    public List<SliceRefListSnapshot>? FrameSliceRefLists;
    public byte[]? FramePerCtbSliceIdx;
    
    // Whether this layer has started a frame in the current access unit
    public bool FrameStarted;
    
    // POC state for this layer (pocTid0 tracks last TID0 picture per-layer)
    public int PocTid0;
    
    // Per-frame MV field working arrays (saved during SwitchLayer for inter-layer ref access)
    public short[]? MvFieldL0X;
    public short[]? MvFieldL0Y;
    public short[]? MvFieldL1X;
    public short[]? MvFieldL1Y;
    public sbyte[]? RefIdxFieldL0;
    public sbyte[]? RefIdxFieldL1;
    public byte[]? PredModeField;
    public byte[]? CbfLumaField;
    public int PuWidthIn4;
    public int PuHeightIn4;
    
    // Per-picture CTB-level arrays (from CodingTree partial class — must be per-layer
    // because each layer has its own SAO, deblock, skip/intra state during decode)
    public byte[]? TabCtDepth;
    public byte[]? TabSkipFlag;
    public byte[]? TabIntraPredMode;
    public int[]? QpYTab;
    public byte[]? VertBsTab;
    public byte[]? HorizBsTab;
    public int BsWidth;
    public int BsHeight;
    public byte[]? IsIntraTab8x8;
    public int PicWidthIn8;
    public int PicHeightIn8;
    public byte[]? IsPcmTab;
    public int IsPcmWidth;
    public byte[]? SaoTypeIdxTab;
    public byte[]? SaoEoClassTab;
    public byte[]? SaoBandPositionTab;
    public int[]? SaoOffsetValTab;
    public int[]? CtuBetaOffset;
    public int[]? CtuTcOffset;
    public byte[]? CtuBoundaryFlags;
    public bool[]? CtuLoopFilterAcrossSlices;
    public bool[]? CtuDeblockDisabled;
    public int[]? TabSliceAddress;
    
    // Per-frame slice/PPS state (each layer's finalization needs the correct slice header)
    public HevcSliceSegmentHeader? CurrentSliceHeader;
    public HevcPictureParameterSet? CurrentPps;
    public HevcScalingList? ActiveScalingList;
    public int CollocatedRefDpbIdx = -1;
    
    // Inter-layer reference DPB index (only layer 1+ has a borrowed inter-layer ref)
    public int InterLayerRefDpbIdx = -1;
    
    // Context-level parameter sets (each layer activates different SPS/PPS during decode)
    public HevcSequenceParameterSet? ContextActiveSps;
    public HevcPictureParameterSet? ContextActivePps;

    public HevcLayerState(int maxDpbFrames)
    {
        Dpb = new HevcReferenceFrame[maxDpbFrames];
    }
}

