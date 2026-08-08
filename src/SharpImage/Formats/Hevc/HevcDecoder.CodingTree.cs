// HEVC CTU / CU / TU Decoding — CABAC-driven quad-tree
// Reference: ITU-T H.265 Sections 7.3.8 - 7.3.12, FFmpeg hevc/hevcdec.c + hevc/cabac.c

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc;

internal sealed partial class HevcDecoder
{
    // Per-picture arrays for neighbor context (allocated in AllocatePerPictureArrays)
    private byte[]? tabCtDepth;      // min_cb_width * min_cb_height: CT depth per min-CB
    private byte[]? tabSkipFlag;     // min_cb_width * min_cb_height: skip flag per min-CB
    private byte[]? tabIntraPredMode; // min_pu_width * min_pu_height: intra pred mode per min-PU
    private int[]? qpYTab;           // min_cb_width * min_cb_height: QP per min-CB

    // Per-4×4-block deblocking BS arrays (allocated in AllocatePerPictureArrays)
    // Indexed as [y4 * bsWidth + x4] where x4 = x/4, y4 = y/4
    // Matches FFmpeg's vertical_bs/horizontal_bs per-4-pixel granularity
    private byte[]? vertBsTab;       // Boundary strength for vertical edge to LEFT of this 4×4 block
    private byte[]? horizBsTab;      // Boundary strength for horizontal edge ABOVE this 4×4 block
    private int bsWidth;             // Picture width in 4-pixel units
    private int bsHeight;            // Picture height in 4-pixel units

    // Per-8×8-block intra/cbf tables (for BS derivation at TU boundaries)
    private byte[]? isIntraTab8x8;   // Whether this 8×8 block belongs to an intra CU
    private int picWidthIn8;
    private int picHeightIn8;

    // Per-4×4-PU PCM/transquant bypass flag (matches FFmpeg's l->is_pcm)
    // 0 = normal, 2 = deblocking bypass (PCM with pcm_loop_filter_disabled, or cu_transquant_bypass)
    private byte[]? isPcmTab;
    private int isPcmWidth;  // picture width in min-PU (4-pixel) units

    // Per-CTU SAO parameters — 3 components (Y, Cb, Cr), indexed as [ctbAddr * 3 + cIdx]
    // Matches FFmpeg's SAOParams structure in hevc/hevcdec.h
    private byte[]? saoTypeIdxTab;       // ctbCount * 3: SAO type (0=off, 1=band, 2=edge)
    private byte[]? saoEoClassTab;       // ctbCount * 3: edge offset class (0-3)
    private byte[]? saoBandPositionTab;  // ctbCount * 3: band position (0-31)
    private int[]? saoOffsetValTab;      // ctbCount * 3 * 5: offset values (idx 0 unused, 1-4 are offsets)

    // Per-CTU deblocking parameters (matches FFmpeg's l->deblock[ctb])
    private int[]? ctuBetaOffset;        // per-CTU beta_offset from slice header
    private int[]? ctuTcOffset;          // per-CTU tc_offset from slice header
    private byte[]? ctuBoundaryFlags;    // per-CTU: bit 0-1 = slice, bit 2-3 = tile boundaries
    private bool[]? ctuLoopFilterAcrossSlices; // per-CTU: slice_loop_filter_across_slices_enabled_flag
    private bool[]? ctuDeblockDisabled;  // per-CTU: slice_deblocking_filter_disabled_flag
    private int[]? tabSliceAddress;       // per-CTU: slice address (raster) for boundary detection

    private const byte BoundaryLeftSlice = 1;
    private const byte BoundaryUpperSlice = 2;
    private const byte BoundaryLeftTile = 4;
    private const byte BoundaryUpperTile = 8;

    // Per-CTU state
    private bool ctbLeftFlag;
    private bool ctbUpFlag;
    private int endOfTilesX;  // Matches FFmpeg's lc->end_of_tiles_x — right edge of current tile/WPP row
    private int currentCtDepth;
    private int qpY;
    private int qpYPred;

    // CU QP delta state — reset per quantization group (matches FFmpeg's lc->tu.*)
    private bool isCuQpDeltaCoded;
    private int cuQpDelta;
    private bool firstQpGroup;    // Matches FFmpeg's lc->first_qp_group
    private int currentSliceQp;   // Slice-level QP for first_qp_group fallback

    // Per-CU chroma QP offset state (RExt cu_chroma_qp_offset, matches FFmpeg's lc->tu.*)
    private bool isCuChromaQpOffsetCoded;
    private int cuQpOffsetCb;
    private int cuQpOffsetCr;

    // CU-local state
    private HevcPredictionMode currentPredMode;
    private HevcPartitionMode currentPartMode;
    private bool currentIntraSplitFlag;
    private int currentMaxTrafoDepth;
    private int[] intraPredModeForPu = new int[4]; // up to 4 PUs (NxN split)
    private int[] intraPredModeCForPu = new int[4]; // chroma intra pred mode per PU (444 uses per-PU)
    private int[] rawChromaModeForPu = new int[4]; // raw decoded chroma_mode (0-4, where 4=DM_CHROMA_IDX)
    private int intraPredModeC; // single chroma pred mode for 420/422 (also set for 444 from PU[0])
    private int currentTuIntraPredMode; // TU-level intra mode, set in TransformTree (matches FFmpeg lc->tu.intra_pred_mode)
    private int currentTuIntraPredModeC; // TU-level chroma intra mode
    private int currentTuRawChromaMode; // TU-level raw chroma mode (0-4, for cross-comp pred condition)
    private bool lastPuMergeFlag; // Track merge flag of last decoded PU (for rqt_root_cbf check)
    private int currentCuBaseX, currentCuBaseY; // CU base position for QP (matches FFmpeg's cb_xBase/cb_yBase)
    private bool currentCuTransquantBypass; // cu_transquant_bypass_flag: skip dequant, transform, and deblocking
    /// <summary>Returns true if the last decoded PU used merge mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsLastPuMerge() => lastPuMergeFlag;

    private void AllocatePerPictureArrays(HevcSequenceParameterSet sps)
    {
        int minCbWidth = sps.PicWidthInMinCbsY;
        int minCbHeight = sps.PicHeightInMinCbsY;
        int minPuWidth = sps.PictureWidthInLumaSamples >> 2; // log2_min_pu_size = 2 in HEVC
        int minPuHeight = sps.PictureHeightInLumaSamples >> 2;

        tabCtDepth = new byte[minCbWidth * minCbHeight];
        tabSkipFlag = new byte[minCbWidth * minCbHeight];
        tabIntraPredMode = new byte[minPuWidth * minPuHeight];
        qpYTab = new int[minCbWidth * minCbHeight];

        // Deblocking BS arrays: per 4×4 block (matches FFmpeg's per-4-pixel granularity)
        bsWidth = (sps.PictureWidthInLumaSamples + 3) >> 2;
        bsHeight = (sps.PictureHeightInLumaSamples + 3) >> 2;
        int totalBsEntries = bsWidth * bsHeight;
        vertBsTab = new byte[totalBsEntries];
        horizBsTab = new byte[totalBsEntries];

        // Per-8×8 intra flag table (for BS derivation)
        picWidthIn8 = (sps.PictureWidthInLumaSamples + 7) >> 3;
        picHeightIn8 = (sps.PictureHeightInLumaSamples + 7) >> 3;
        int totalBlocks8 = picWidthIn8 * picHeightIn8;
        isIntraTab8x8 = new byte[totalBlocks8];

        // Per-4×4-PU PCM/transquant bypass flag (matches FFmpeg's l->is_pcm)
        isPcmWidth = minPuWidth;
        isPcmTab = new byte[minPuWidth * minPuHeight];

        // SAO arrays: 3 components per CTU
        int ctbCount = sps.PicWidthInCtbsY * sps.PicHeightInCtbsY;
        saoTypeIdxTab = new byte[ctbCount * 3];
        saoEoClassTab = new byte[ctbCount * 3];
        saoBandPositionTab = new byte[ctbCount * 3];
        saoOffsetValTab = new int[ctbCount * 3 * 5];

        // Per-CTU deblocking parameters
        ctuBetaOffset = new int[ctbCount];
        ctuTcOffset = new int[ctbCount];
        ctuBoundaryFlags = new byte[ctbCount];
        ctuLoopFilterAcrossSlices = new bool[ctbCount];
        ctuDeblockDisabled = new bool[ctbCount];
        tabSliceAddress = new int[ctbCount];
        Array.Fill(tabSliceAddress, -1);
    }

    /// <summary>
    /// Decodes SAO parameters for one CTU from the CABAC bitstream.
    /// Must be called BEFORE CodingQuadtree — the bitstream contains SAO data first.
    /// Matches FFmpeg's hls_sao_param() in hevcdec.c exactly.
    /// </summary>
    private void DecodeSaoParam(ref HevcCabacDecoder cabac, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps, int ctbX, int ctbY)
    {
        int ctbAddr = ctbY * sps.PicWidthInCtbsY + ctbX;
        int saoMergeLeftFlag = 0;
        int saoMergeUpFlag = 0;

        bool saoLuma = sliceHeader.SliceSaoLumaFlag;
        bool saoChroma = sliceHeader.SliceSaoChromaFlag;

        if (saoLuma || saoChroma)
        {
            // sao_merge_left_flag
            if (ctbX > 0 && ctbLeftFlag)
                saoMergeLeftFlag = cabac.DecodeBin(HevcCabacContextIndex.SaoMergeFlag);

            // sao_merge_up_flag (only if not merging left)
            if (ctbY > 0 && saoMergeLeftFlag == 0 && ctbUpFlag)
                saoMergeUpFlag = cabac.DecodeBin(HevcCabacContextIndex.SaoMergeFlag);
        }

        int numComponents = sps.ChromaFormatIdc != HevcChromaFormat.Monochrome ? 3 : 1;
        int ctbWidth = sps.PicWidthInCtbsY;

        for (int cIdx = 0; cIdx < numComponents; cIdx++)
        {
            int baseIdx = ctbAddr * 3 + cIdx;
            // log2_sao_offset_scale: 0 for Main/Main10; from PPS range extension for RExt profiles
            int log2SaoOffsetScale = cIdx == 0 ? pps.Log2SaoOffsetScale0 : pps.Log2SaoOffsetScale1;

            // Check per-component slice flag: [0]=luma, [1]=Cb, [2]=Cr (Cb and Cr share slice flag)
            bool sliceSaoFlag = cIdx == 0 ? saoLuma : saoChroma;
            if (!sliceSaoFlag)
            {
                saoTypeIdxTab![baseIdx] = 0; // SAO_NOT_APPLIED
                continue;
            }

            // Cr copies Cb's type_idx and eo_class (spec 7.4.9.3)
            if (cIdx == 2)
            {
                int cbIdx = ctbAddr * 3 + 1;
                if (saoMergeLeftFlag == 0 && saoMergeUpFlag == 0)
                {
                    saoTypeIdxTab![baseIdx] = saoTypeIdxTab[cbIdx];
                    saoEoClassTab![baseIdx] = saoEoClassTab[cbIdx];
                }
                else if (saoMergeLeftFlag != 0)
                {
                    int leftAddr = ctbAddr - 1;
                    saoTypeIdxTab![baseIdx] = saoTypeIdxTab[leftAddr * 3 + cIdx];
                    saoEoClassTab![baseIdx] = saoEoClassTab[leftAddr * 3 + cIdx];
                }
                else // saoMergeUpFlag
                {
                    int upAddr = ctbAddr - ctbWidth;
                    saoTypeIdxTab![baseIdx] = saoTypeIdxTab[upAddr * 3 + cIdx];
                    saoEoClassTab![baseIdx] = saoEoClassTab[upAddr * 3 + cIdx];
                }
            }
            else
            {
                // Decode type_idx from CABAC (or copy from neighbor)
                if (saoMergeLeftFlag == 0 && saoMergeUpFlag == 0)
                {
                    saoTypeIdxTab![baseIdx] = (byte)DecodeSaoTypeIdx(ref cabac);
                }
                else if (saoMergeLeftFlag != 0)
                {
                    int leftAddr = ctbAddr - 1;
                    saoTypeIdxTab![baseIdx] = saoTypeIdxTab[leftAddr * 3 + cIdx];
                }
                else // saoMergeUpFlag
                {
                    int upAddr = ctbAddr - ctbWidth;
                    saoTypeIdxTab![baseIdx] = saoTypeIdxTab[upAddr * 3 + cIdx];
                }
            }

            if (saoTypeIdxTab![baseIdx] == 0) // SAO_NOT_APPLIED
                continue;

            int offsetValBase = ctbAddr * 3 * 5 + cIdx * 5;

            // When merging from neighbor, copy all SAO params directly (already scaled).
            // Matches FFmpeg's SET_SAO macro which copies each element from the merge source.
            if (saoMergeLeftFlag != 0 || saoMergeUpFlag != 0)
            {
                int srcAddr = saoMergeLeftFlag != 0 ? ctbAddr - 1 : ctbAddr - ctbWidth;
                int srcBase = srcAddr * 3 * 5 + cIdx * 5;
                for (int i = 0; i < 5; i++)
                    saoOffsetValTab![offsetValBase + i] = saoOffsetValTab[srcBase + i];

                if (saoTypeIdxTab[baseIdx] == 1) // SAO_BAND: copy band_position
                    saoBandPositionTab![baseIdx] = saoBandPositionTab[srcAddr * 3 + cIdx];

                // Copy eo_class from neighbor (needed for SAO_EDGE, harmless for SAO_BAND)
                saoEoClassTab![baseIdx] = saoEoClassTab[srcAddr * 3 + cIdx];

                continue;
            }

            // Decode 4 offset_abs values from CABAC
            int bitDepth = cIdx == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
            int maxOffsetAbs = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;

            Span<int> offsetAbs = stackalloc int[4];
            for (int i = 0; i < 4; i++)
                offsetAbs[i] = DecodeSaoOffsetAbs(ref cabac, maxOffsetAbs);

            saoOffsetValTab![offsetValBase] = 0; // index 0 is always 0

            if (saoTypeIdxTab[baseIdx] == 1) // SAO_BAND
            {
                // Decode sign for each non-zero offset, then band position
                Span<int> offsetSign = stackalloc int[4];
                for (int i = 0; i < 4; i++)
                    offsetSign[i] = offsetAbs[i] != 0 ? cabac.DecodeBypass() : 0;

                saoBandPositionTab![baseIdx] = (byte)DecodeSaoBandPosition(ref cabac);

                // Compute final offset values with sign and scale
                for (int i = 0; i < 4; i++)
                {
                    int val = offsetAbs[i];
                    if (offsetSign[i] != 0) val = -val;
                    saoOffsetValTab[offsetValBase + 1 + i] = val << log2SaoOffsetScale;
                }
            }
            else // SAO_EDGE (type_idx == 2)
            {
                // Edge offset: signs are fixed (+, +, -, -)
                saoOffsetValTab[offsetValBase + 1] =  offsetAbs[0] << log2SaoOffsetScale;
                saoOffsetValTab[offsetValBase + 2] =  offsetAbs[1] << log2SaoOffsetScale;
                saoOffsetValTab[offsetValBase + 3] = -(offsetAbs[2] << log2SaoOffsetScale);
                saoOffsetValTab[offsetValBase + 4] = -(offsetAbs[3] << log2SaoOffsetScale);

                // Edge offset class: 2 bypass bins (only for Y and Cb, Cr copies Cb)
                if (cIdx != 2)
                    saoEoClassTab![baseIdx] = (byte)DecodeSaoEoClass(ref cabac);
            }
        }
    }

    /// <summary>
    /// Decodes sao_type_idx: context bin + optional bypass bin.
    /// Returns 0 (off), 1 (band), or 2 (edge).
    /// Matches FFmpeg's ff_hevc_sao_type_idx_decode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSaoTypeIdx(ref HevcCabacDecoder cabac)
    {
        if (cabac.DecodeBin(HevcCabacContextIndex.SaoTypeIdx) == 0)
            return 0; // SAO_NOT_APPLIED
        if (cabac.DecodeBypass() == 0)
            return 1; // SAO_BAND
        return 2; // SAO_EDGE
    }

    /// <summary>
    /// Decodes sao_offset_abs: unary bypass bins, max = (1 &lt;&lt; (min(bitDepth,10)-5)) - 1.
    /// Matches FFmpeg's ff_hevc_sao_offset_abs_decode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSaoOffsetAbs(ref HevcCabacDecoder cabac, int maxVal)
    {
        int i = 0;
        while (i < maxVal && cabac.DecodeBypass() != 0)
            i++;
        return i;
    }

    /// <summary>
    /// Decodes sao_band_position: 5 bypass bins.
    /// Matches FFmpeg's ff_hevc_sao_band_position_decode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSaoBandPosition(ref HevcCabacDecoder cabac)
    {
        int value = cabac.DecodeBypass();
        for (int i = 0; i < 4; i++)
            value = (value << 1) | cabac.DecodeBypass();
        return value;
    }

    /// <summary>
    /// Decodes sao_eo_class: 2 bypass bins.
    /// Matches FFmpeg's ff_hevc_sao_eo_class_decode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSaoEoClass(ref HevcCabacDecoder cabac)
    {
        return (cabac.DecodeBypass() << 1) | cabac.DecodeBypass();
    }

    /// <summary>
    /// Decodes cu_qp_delta_abs from CABAC.
    /// Prefix: truncated unary (max 5) with context bins (first bin ctx+0, rest ctx+1).
    /// Suffix: exp-Golomb order 0 with bypass bins (if prefix >= 5).
    /// Matches FFmpeg's ff_hevc_cu_qp_delta_abs exactly.
    /// </summary>
    private static int DecodeCuQpDeltaAbs(ref HevcCabacDecoder cabac)
    {
        int prefixVal = 0;
        int inc = 0;

        while (prefixVal < 5 && cabac.DecodeBin(HevcCabacContextIndex.CuQpDelta + inc) != 0)
        {
            prefixVal++;
            inc = 1;
        }

        if (prefixVal >= 5)
        {
            int suffixVal = 0;
            int k = 0;
            while (k < 7 && cabac.DecodeBypass() != 0)
            {
                suffixVal += 1 << k;
                k++;
            }
            // Read k remaining bits
            while (k > 0)
            {
                k--;
                suffixVal += cabac.DecodeBypass() << k;
            }
            return prefixVal + suffixVal;
        }

        return prefixVal;
    }

    /// <summary>
    /// Computes predicted QP from left and above neighbors.
    /// Matches FFmpeg's get_qPy_pred in hevc/filter.c exactly.
    /// </summary>
    private int GetQPyPred(HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int xBase, int yBase, int log2CbSize)
    {
        int ctbSizeMask = sps.CtbSizeY - 1;
        int minCuQpDeltaSizeMask = (1 << (sps.Log2CtbSizeY - pps.DiffCuQpDeltaDepth)) - 1;
        int minCbWidth = sps.PicWidthInMinCbsY;

        // QP group base coordinates (snap to QP group boundaries, not CTB boundaries)
        int xQgBase = xBase - (xBase & minCuQpDeltaSizeMask);
        int yQgBase = yBase - (yBase & minCuQpDeltaSizeMask);

        // CU coordinates in min-CB units from QP group base (matches FFmpeg exactly)
        int xCb = xQgBase >> sps.Log2MinCbSizeY;
        int yCb = yQgBase >> sps.Log2MinCbSizeY;

        // Neighbor availability: requires BOTH current CU and QP group base within same CTB
        bool availableA = (xBase & ctbSizeMask) != 0 &&
                          (xQgBase & ctbSizeMask) != 0;
        bool availableB = (yBase & ctbSizeMask) != 0 &&
                          (yQgBase & ctbSizeMask) != 0;

        // First QP group in slice or at CTB (0,0): use slice QP directly
        int qpPred;
        if (firstQpGroup || (xQgBase == 0 && yQgBase == 0))
        {
            firstQpGroup = !isCuQpDeltaCoded;
            qpPred = currentSliceQp;
        }
        else
        {
            qpPred = qpYPred;
        }

        int qpA = availableA && qpYTab != null
            ? qpYTab[yCb * minCbWidth + xCb - 1]
            : qpPred;
        int qpB = availableB && qpYTab != null
            ? qpYTab[(yCb - 1) * minCbWidth + xCb]
            : qpPred;

        return (qpA + qpB + 1) >> 1;
    }

    /// <summary>
    /// Sets QP for the current CU from the predicted QP and decoded delta.
    /// Matches FFmpeg's ff_hevc_set_qPy.
    /// </summary>
    private void SetQPy(HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int xBase, int yBase, int log2CbSize)
    {
        int qpPred = GetQPyPred(sps, pps, xBase, yBase, log2CbSize);
        if (cuQpDelta != 0)
        {
            int qpBdOffset = sps.QpBdOffsetY;
            // FFUMOD(a, b) = ((a) % (b) + (b)) % (b)
            int a = qpPred + cuQpDelta + 52 + 2 * qpBdOffset;
            int b = 52 + qpBdOffset;
            qpY = ((a % b) + b) % b - qpBdOffset;
        }
        else
        {
            qpY = qpPred;
        }
    }

    /// <summary>
    /// Decodes a full CTU by recursively splitting into CUs via the coding quad-tree.
    /// Replaces the gray-fill placeholder. Follows FFmpeg's hls_coding_quadtree exactly.
    /// </summary>
    private bool DecodeCtu(ref HevcCabacDecoder cabac, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps, int ctbX, int ctbY, Span<short> residual)
    {
        if (currentFrameBuffer == null)
            return false;

        int ctbSizeY = sps.CtbSizeY;
        int pixelX = ctbX * ctbSizeY;
        int pixelY = ctbY * ctbSizeY;

        // Ensure per-picture arrays are allocated (reallocates when SPS changes)
        if (tabCtDepth == null)
        {
            AllocatePerPictureArrays(sps);
            lastAllocatedSps = sps;
        }

        // Set neighbor availability flags — tile-aware
        // Matches FFmpeg's hls_decode_neighbour() in hevcdec.c
        int sliceAddr = sliceHeader.SliceAddr;
        int ctbAddr = ctbY * sps.PicWidthInCtbsY + ctbX;
        int ctbAddrInSlice = ctbAddr - sliceAddr;

        // Store slice address for this CTB
        if (tabSliceAddress != null)
            tabSliceAddress[ctbAddr] = sliceAddr;

        int[] rsToTs = pps.CtbAddrRsToTs!;
        int[] tileId = pps.TileIdPerTs!;
        int ctbAddrTs = rsToTs[ctbAddr];
        int ctbW = sps.PicWidthInCtbsY;

        // WPP/tile: set end_of_tiles_x for neighbor availability checks
        // Matches FFmpeg's hls_decode_neighbour lines 2712-2724
        if (pps.EntropyCodingSyncEnabled)
        {
            endOfTilesX = sps.PictureWidthInLumaSamples;
        }
        else if (pps.TilesEnabled)
        {
            // Tile boundary: right edge of current tile column (in pixels)
            int idxX = pps.ColIdxX![ctbX];
            endOfTilesX = Math.Min(pps.ColBd![idxX + 1] * ctbSizeY, sps.PictureWidthInLumaSamples);
        }
        else
        {
            endOfTilesX = sps.PictureWidthInLumaSamples;
        }

        byte flags = 0;
        if (pps.TilesEnabled)
        {
            // Tile boundary detection: compare tile_id of neighbors
            if (ctbX > 0 && tileId[ctbAddrTs] != tileId[rsToTs[ctbAddr - 1]])
                flags |= BoundaryLeftTile;
            if (ctbX > 0 && tabSliceAddress != null && tabSliceAddress[ctbAddr] != tabSliceAddress[ctbAddr - 1])
                flags |= BoundaryLeftSlice;
            if (ctbY > 0 && tileId[ctbAddrTs] != tileId[rsToTs[ctbAddr - ctbW]])
                flags |= BoundaryUpperTile;
            if (ctbY > 0 && tabSliceAddress != null && tabSliceAddress[ctbAddr] != tabSliceAddress[ctbAddr - ctbW])
                flags |= BoundaryUpperSlice;
        }
        else
        {
            if (ctbX > 0 && ctbAddrInSlice <= 0) flags |= BoundaryLeftSlice;
            if (ctbY > 0 && ctbAddrInSlice < ctbW) flags |= BoundaryUpperSlice;
        }

        ctbLeftFlag = ctbX > 0 && ctbAddrInSlice > 0 && (flags & BoundaryLeftTile) == 0;
        ctbUpFlag = ctbY > 0 && ctbAddrInSlice >= ctbW && (flags & BoundaryUpperTile) == 0;

        ctbUpRightFlag = (ctbX + 1 < ctbW) && ctbY > 0 &&
                         (ctbAddrInSlice + 1 >= ctbW) &&
                         tileId[ctbAddrTs] == tileId[rsToTs[ctbAddr + 1 - ctbW]];

        ctbUpLeftFlag = ctbX > 0 && ctbY > 0 &&
                        (ctbAddrInSlice - 1 >= ctbW) &&
                        tileId[ctbAddrTs] == tileId[rsToTs[ctbAddr - 1 - ctbW]];

        // Store per-CTU deblocking parameters and boundary flags
        if (ctuBetaOffset != null)
        {
            ctuBetaOffset[ctbAddr] = sliceHeader.SliceBetaOffsetDiv2;
            ctuTcOffset[ctbAddr] = sliceHeader.SliceTcOffsetDiv2;
            ctuDeblockDisabled![ctbAddr] = sliceHeader.SliceDeblockingFilterDisabled;
            ctuLoopFilterAcrossSlices![ctbAddr] = sliceHeader.SliceLoopFilterAcrossSlicesEnabled;
            ctuBoundaryFlags![ctbAddr] = flags;
        }

        // Initialize QP prediction
        int sliceQp = sliceHeader.SliceQp(pps);
        currentSliceQp = sliceQp;
        if (ctbAddr == sliceAddr)
        {
            qpY = sliceQp;
            qpYPred = sliceQp;
            // FFmpeg: first_qp_group = !dependent (line 3066 of hevcdec.c)
            // Dependent slices continue QP prediction from previous slice
            firstQpGroup = !sliceHeader.DependentSliceSegmentFlag;

            // Reset per-CU chroma QP offset at slice start (FFmpeg hevcdec.c:3071-3072)
            cuQpOffsetCb = 0;
            cuQpOffsetCr = 0;
        }

        // WPP: reset first_qp_group at every row start, overriding the dependent-slice
        // setting above. Matches FFmpeg's hls_decode_neighbour lines 2712-2714.
        if (pps.EntropyCodingSyncEnabled && ctbX == 0)
            firstQpGroup = true;

        // Parse SAO parameters from CABAC BEFORE coding quad-tree
        // (SAO data precedes coding tree data in the bitstream per CTU)

        int binsBeforeSao = cabac.DiagBinCount;
        if (sps.SampleAdaptiveOffsetEnabled)
            DecodeSaoParam(ref cabac, sliceHeader, sps, pps, ctbX, ctbY);
        int binsAfterSao = cabac.DiagBinCount;

        // Decode coding quad-tree
        int binsBeforeTree = cabac.DiagBinCount;
        int moreData = CodingQuadtree(ref cabac, sliceHeader, sps, pps, pixelX, pixelY,
            sps.Log2CtbSizeY, 0, residual);
        int binsAfterTree = cabac.DiagBinCount;

        // Log detailed bin consumption per phase
        DiagCtuPhaseLog?.Add((ctbAddr, binsAfterSao - binsBeforeSao, binsAfterTree - binsBeforeTree));

        // Return true if more data follows (end_of_slice_segment_flag == 0)
        return moreData > 0;
    }

    /// <summary>
    /// Recursive coding quad-tree. Matches FFmpeg's hls_coding_quadtree.
    /// Returns >0 if more data follows, 0 if end of slice, &lt;0 on error.
    /// </summary>
    private int CodingQuadtree(ref HevcCabacDecoder cabac, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int log2CbSize, int cbDepth, Span<short> residual)
    {
        int cbSize = 1 << log2CbSize;
        bool splitCu;

        currentCtDepth = cbDepth;

        // Reset CU QP delta state at quantization group boundary
        // Matches FFmpeg: if (log2_cb_size >= sps->log2_ctb_size - pps->diff_cu_qp_delta_depth)
        if (pps.CuQpDeltaEnabled &&
            log2CbSize >= sps.Log2CtbSizeY - pps.DiffCuQpDeltaDepth)
        {
            isCuQpDeltaCoded = false;
            cuQpDelta = 0;
        }

        // Reset per-CU chroma QP offset at quantization group boundary (FFmpeg hevcdec.c:2633-2636)
        if (sliceHeader.CuChromaQpOffsetEnabled &&
            log2CbSize >= sps.Log2CtbSizeY - pps.DiffCuChromaQpOffsetDepth)
        {
            isCuChromaQpOffsetCoded = false;
        }

        // Determine if we must split or can decode split_cu_flag
        if (x0 + cbSize <= sps.PictureWidthInLumaSamples &&
            y0 + cbSize <= sps.PictureHeightInLumaSamples &&
            log2CbSize > sps.Log2MinCbSizeY)
        {
            splitCu = DecodeSplitCuFlag(ref cabac, sps, cbDepth, x0, y0);
        }
        else
        {
            // Must split if CU exceeds picture bounds or is larger than min CB
            splitCu = log2CbSize > sps.Log2MinCbSizeY;
        }


        if (splitCu)
        {
            int cbSizeSplit = cbSize >> 1;
            int x1 = x0 + cbSizeSplit;
            int y1 = y0 + cbSizeSplit;

            int moreData = CodingQuadtree(ref cabac, sliceHeader, sps, pps,
                x0, y0, log2CbSize - 1, cbDepth + 1, residual);
            if (moreData < 0) return moreData;

            if (moreData > 0 && x1 < sps.PictureWidthInLumaSamples)
            {
                moreData = CodingQuadtree(ref cabac, sliceHeader, sps, pps,
                    x1, y0, log2CbSize - 1, cbDepth + 1, residual);
                if (moreData < 0) return moreData;
            }

            if (moreData > 0 && y1 < sps.PictureHeightInLumaSamples)
            {
                moreData = CodingQuadtree(ref cabac, sliceHeader, sps, pps,
                    x0, y1, log2CbSize - 1, cbDepth + 1, residual);
                if (moreData < 0) return moreData;
            }

            if (moreData > 0 && x1 < sps.PictureWidthInLumaSamples &&
                y1 < sps.PictureHeightInLumaSamples)
            {
                moreData = CodingQuadtree(ref cabac, sliceHeader, sps, pps,
                    x1, y1, log2CbSize - 1, cbDepth + 1, residual);
                if (moreData < 0) return moreData;
            }

            // Update qPy_pred at QP group boundary (split path)
            int qpBlockMask = (1 << (sps.Log2CtbSizeY - pps.DiffCuQpDeltaDepth)) - 1;
            if (((x0 + cbSize) & qpBlockMask) == 0 &&
                ((y0 + cbSize) & qpBlockMask) == 0)
            {
                qpYPred = qpY;
            }

            if (moreData > 0)
                return (x1 + cbSizeSplit < sps.PictureWidthInLumaSamples ||
                        y1 + cbSizeSplit < sps.PictureHeightInLumaSamples) ? 1 : 0;
            return 0;
        }
        else
        {
            // Leaf node: decode coding unit
            CodingUnit(ref cabac, sliceHeader, sps, pps, x0, y0, log2CbSize, residual);

            // Store CT depth for neighbor context
            SetCtDepth(sps, x0, y0, log2CbSize, cbDepth);

            // Check end_of_slice at CTU boundary
            bool atCtbRight = ((x0 + cbSize) % sps.CtbSizeY == 0) || (x0 + cbSize >= sps.PictureWidthInLumaSamples);
            bool atCtbBottom = ((y0 + cbSize) % sps.CtbSizeY == 0) || (y0 + cbSize >= sps.PictureHeightInLumaSamples);

            if (atCtbRight && atCtbBottom)
            {
                int endOfSlice = cabac.DecodeTerminate();
                return endOfSlice == 0 ? 1 : 0;
            }

            return 1;
        }
    }

    /// <summary>
    /// Decodes a coding unit. Matches FFmpeg's hls_coding_unit (I-slice path).
    /// </summary>
    private void CodingUnit(ref HevcCabacDecoder cabac, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int log2CbSize, Span<short> residual)
    {
        int cbSize = 1 << log2CbSize;
        int log2MinCbSize = sps.Log2MinCbSizeY;
        int minCbWidth = sps.PicWidthInMinCbsY;
        int length = cbSize >> log2MinCbSize;
        int xCb = x0 >> log2MinCbSize;
        int yCb = y0 >> log2MinCbSize;

        // Default: intra, 2Nx2N
        currentPredMode = HevcPredictionMode.Intra;
        currentPartMode = HevcPartitionMode.Part2Nx2N;
        currentIntraSplitFlag = false;
        currentCuBaseX = x0;
        currentCuBaseY = y0;

        // Clear skip flag
        if (tabSkipFlag != null)
        {
            for (int y = 0; y < length; y++)
                for (int x = 0; x < length; x++)
                    tabSkipFlag[(yCb + y) * minCbWidth + xCb + x] = 0;
        }

        // Initialize intra pred mode array
        for (int i = 0; i < 4; i++)
            intraPredModeForPu[i] = 1; // INTRA_DC

        // cu_transquant_bypass_flag
        bool cuTransquantBypass = false;
        if (pps.TransquantBypassEnabled)
            cuTransquantBypass = cabac.DecodeBin(HevcCabacContextIndex.CuTransquantBypassFlag) != 0;
        currentCuTransquantBypass = cuTransquantBypass;

        // Mark deblocking bypass for transquant_bypass CUs (matches FFmpeg hevcdec.c line 2447)
        if (cuTransquantBypass)
            SetDeblockingBypass(x0, y0, log2CbSize);

        // For non-I slices: decode skip_flag and pred_mode
        if (sliceHeader.SliceType != HevcSliceType.ISlice)
        {
            // skip_flag
            int skipInc = 0;
            int x0b = x0 & (sps.CtbSizeY - 1);
            int y0b = y0 & (sps.CtbSizeY - 1);

            if (ctbLeftFlag || x0b != 0)
            {
                if (tabSkipFlag != null && xCb > 0)
                    skipInc = tabSkipFlag[yCb * minCbWidth + xCb - 1] != 0 ? 1 : 0;
            }
            if (ctbUpFlag || y0b != 0)
            {
                if (tabSkipFlag != null && yCb > 0)
                    skipInc += tabSkipFlag[(yCb - 1) * minCbWidth + xCb] != 0 ? 1 : 0;
            }

            int skipFlag = cabac.DecodeBin(HevcCabacContextIndex.CuSkipFlag + skipInc);

            if (tabSkipFlag != null)
            {
                for (int y = 0; y < length; y++)
                    for (int x = 0; x < length; x++)
                        tabSkipFlag[(yCb + y) * minCbWidth + xCb + x] = (byte)skipFlag;
            }

            currentPredMode = skipFlag != 0 ? HevcPredictionMode.Skip : HevcPredictionMode.Inter;
        }

        if (currentPredMode == HevcPredictionMode.Skip)
        {
            // Skip mode: merge prediction, no residual
            PredictionUnit(ref cabac, sliceHeader, sps, pps,
                x0, y0, cbSize, cbSize, log2CbSize, 0, isSkip: true);
            SetIntraPredModeDefault(sps, x0, y0, log2CbSize);

            FillDeblockingInfo(x0, y0, cbSize, false);

            // Skip = whole CU is one TU with no residual
            FillDeblockingInfoTU(x0, y0, log2CbSize, false, false);

            // QP handling for skip CUs
            if (pps.CuQpDeltaEnabled && !isCuQpDeltaCoded)
                SetQPy(sps, pps, x0, y0, log2CbSize);
            if (qpYTab != null)
            {
                for (int y = 0; y < length; y++)
                    for (int x = 0; x < length; x++)
                        qpYTab[(yCb + y) * minCbWidth + xCb + x] = qpY;
            }
            int qpBlockMaskSkip = (1 << (sps.Log2CtbSizeY - pps.DiffCuQpDeltaDepth)) - 1;
            if (((x0 + cbSize) & qpBlockMaskSkip) == 0 &&
                ((y0 + cbSize) & qpBlockMaskSkip) == 0)
                qpYPred = qpY;

            return;
        }

        // Not skip — decode pred_mode and part_mode
        if (sliceHeader.SliceType != HevcSliceType.ISlice)
        {
            currentPredMode = cabac.DecodeBin(HevcCabacContextIndex.PredModeFlag) != 0
                ? HevcPredictionMode.Intra
                : HevcPredictionMode.Inter;
        }

        if (currentPredMode != HevcPredictionMode.Intra || log2CbSize == sps.Log2MinCbSizeY)
        {
            currentPartMode = DecodePartMode(ref cabac, sps, log2CbSize);
            currentIntraSplitFlag = currentPartMode == HevcPartitionMode.PartNxN &&
                                    currentPredMode == HevcPredictionMode.Intra;
        }

        bool pcmFlag = false;
        if (currentPredMode == HevcPredictionMode.Intra)
        {
            // Check PCM flag: only for 2Nx2N intra with PCM enabled and block size in range
            if (currentPartMode == HevcPartitionMode.Part2Nx2N && sps.PcmEnabled)
            {
                int log2MinPcmCbSize = sps.Log2MinPcmLumaCodingBlockSizeMinus3 + 3;
                int log2MaxPcmCbSize = log2MinPcmCbSize + sps.Log2DiffMaxMinPcmLumaCodingBlockSize;
                if (log2CbSize >= log2MinPcmCbSize && log2CbSize <= log2MaxPcmCbSize)
                    pcmFlag = cabac.DecodeTerminate() != 0;
            }

            if (pcmFlag)
            {
                // PCM mode: set default intra pred mode (DC) and read raw samples
                SetIntraPredModeDefault(sps, x0, y0, log2CbSize);
                DecodePcmSamples(ref cabac, sps, x0, y0, log2CbSize);

                // Mark deblocking bypass for PCM blocks when loop filter is disabled
                // (matches FFmpeg hevcdec.c line 2500-2501)
                if (sps.PcmLoopFilterDisabled)
                    SetDeblockingBypass(x0, y0, log2CbSize);
            }
            else
            {
                IntraPredictionUnit(ref cabac, sps, x0, y0, log2CbSize);
            }
        }
        else
        {
            // Inter prediction — decode PUs based on partition mode
            SetIntraPredModeDefault(sps, x0, y0, log2CbSize);
            int idx = log2CbSize - 2;

            switch (currentPartMode)
            {
                case HevcPartitionMode.Part2Nx2N:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize, cbSize, log2CbSize, 0, isSkip: false);
                    break;
                case HevcPartitionMode.Part2NxN:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize, cbSize / 2, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0 + cbSize / 2, cbSize, cbSize / 2, log2CbSize, 1, isSkip: false);
                    break;
                case HevcPartitionMode.PartNx2N:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize / 2, cbSize, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0 + cbSize / 2, y0, cbSize / 2, cbSize, log2CbSize, 1, isSkip: false);
                    break;
                case HevcPartitionMode.Part2NxnU:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize, cbSize / 4, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0 + cbSize / 4, cbSize, cbSize * 3 / 4, log2CbSize, 1, isSkip: false);
                    break;
                case HevcPartitionMode.Part2NxnD:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize, cbSize * 3 / 4, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0 + cbSize * 3 / 4, cbSize, cbSize / 4, log2CbSize, 1, isSkip: false);
                    break;
                case HevcPartitionMode.PartnLx2N:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize / 4, cbSize, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0 + cbSize / 4, y0, cbSize * 3 / 4, cbSize, log2CbSize, 1, isSkip: false);
                    break;
                case HevcPartitionMode.PartnRx2N:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize * 3 / 4, cbSize, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0 + cbSize * 3 / 4, y0, cbSize / 4, cbSize, log2CbSize, 1, isSkip: false);
                    break;
                case HevcPartitionMode.PartNxN:
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0, cbSize / 2, cbSize / 2, log2CbSize, 0, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0 + cbSize / 2, y0, cbSize / 2, cbSize / 2, log2CbSize, 1, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0, y0 + cbSize / 2, cbSize / 2, cbSize / 2, log2CbSize, 2, isSkip: false);
                    PredictionUnit(ref cabac, sliceHeader, sps, pps,
                        x0 + cbSize / 2, y0 + cbSize / 2, cbSize / 2, cbSize / 2, log2CbSize, 3, isSkip: false);
                    break;
            }
        }

        // Decode transform tree (handles residual coding)
        // PCM blocks skip all residual coding — samples are written directly.
        if (!pcmFlag)
        {
            // For inter: check rqt_root_cbf (no_residual_syntax_flag).
            // Skip rqt_root_cbf if 2Nx2N merge (matches ffmpeg: part_mode == PART_2Nx2N && merge_flag)
            bool rqtRootCbf = true;
            bool interMerge2Nx2N = currentPredMode != HevcPredictionMode.Intra &&
                currentPartMode == HevcPartitionMode.Part2Nx2N &&
                IsLastPuMerge();
            if (currentPredMode != HevcPredictionMode.Intra && !interMerge2Nx2N)
            {
                rqtRootCbf = DecodeNoResidualDataFlag(ref cabac) != 0;
            }

            if (rqtRootCbf)
            {
                currentMaxTrafoDepth = currentPredMode == HevcPredictionMode.Intra
                    ? sps.MaxTransformHierarchyDepthIntra + (currentIntraSplitFlag ? 1 : 0)
                    : sps.MaxTransformHierarchyDepthInter;

                TransformTree(ref cabac, sliceHeader, sps, pps,
                    x0, y0, x0, y0, log2CbSize, log2CbSize, 0, 0,
                    false, false, false, false, residual);
            }
            else
            {
                // No transform tree — whole CU is one TU with no residual
                FillDeblockingInfoTU(x0, y0, log2CbSize,
                    currentPredMode == HevcPredictionMode.Intra, false);
            }
        }

        // If CU QP delta is enabled but no delta was coded in this CU, update QP from prediction
        // Matches FFmpeg: if (pps->cu_qp_delta_enabled_flag && lc->tu.is_cu_qp_delta_coded == 0) set_qPy()
        if (pps.CuQpDeltaEnabled && !isCuQpDeltaCoded)
            SetQPy(sps, pps, x0, y0, log2CbSize);

        // Store QP
        if (qpYTab != null)
        {
            for (int y = 0; y < length; y++)
                for (int x = 0; x < length; x++)
                    qpYTab[(yCb + y) * minCbWidth + xCb + x] = qpY;
        }

        // Update qPy_pred at QP group boundary
        // Matches FFmpeg: if(((x0 + (1<<log2_cb_size)) & qp_block_mask) == 0 && ...)
        int qpBlockMask = (1 << (sps.Log2CtbSizeY - pps.DiffCuQpDeltaDepth)) - 1;
        if (((x0 + cbSize) & qpBlockMask) == 0 &&
            ((y0 + cbSize) & qpBlockMask) == 0)
        {
            qpYPred = qpY;
        }

        // Fill isIntra per 8×8 block (BS is marked at TU level by FillDeblockingInfoTU)
        FillDeblockingInfo(x0, y0, cbSize, currentPredMode == HevcPredictionMode.Intra);
    }

    /// <summary>
    /// Fills the per-8×8-block isIntra flag for a decoded CU.
    /// BS is now marked at TU level by FillDeblockingInfoTU.
    /// </summary>
    private void FillDeblockingInfo(int x0, int y0, int cbSize, bool isIntra)
    {
        if (isIntraTab8x8 == null)
            return;

        int x8 = x0 >> 3;
        int y8 = y0 >> 3;
        int blocksW = cbSize >> 3;
        int blocksH = cbSize >> 3;

        for (int by = 0; by < blocksH; by++)
        {
            for (int bx = 0; bx < blocksW; bx++)
            {
                int idx = (y8 + by) * picWidthIn8 + (x8 + bx);
                isIntraTab8x8[idx] = isIntra ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>
    /// Marks per-4×4-PU deblocking bypass flag for PCM (when pcm_loop_filter_disabled)
    /// and cu_transquant_bypass blocks. Matches FFmpeg's set_deblocking_bypass().
    /// Value 2 means "skip deblocking on this side of the edge".
    /// </summary>
    private void SetDeblockingBypass(int x0, int y0, int log2CbSize)
    {
        if (isPcmTab == null) return;
        var sps = context.ActiveSps;
        if (sps == null) return;

        int cbSize = 1 << log2CbSize;
        int xEnd = Math.Min(x0 + cbSize, sps.PictureWidthInLumaSamples);
        int yEnd = Math.Min(y0 + cbSize, sps.PictureHeightInLumaSamples);

        for (int j = y0 >> 2; j < (yEnd >> 2); j++)
            for (int i = x0 >> 2; i < (xEnd >> 2); i++)
                isPcmTab[j * isPcmWidth + i] = 2;
    }

    /// <summary>
    /// Marks deblocking boundary strengths at TU boundaries in the per-4×4 BS tables.
    /// Called at each leaf TU in the transform tree, matching FFmpeg's
    /// ff_hevc_deblocking_boundary_strengths() in filter.c.
    /// </summary>
    private void FillDeblockingInfoTU(int x0, int y0, int log2TrafoSize, bool isIntra, bool cbfLuma)
    {
        if (vertBsTab == null || horizBsTab == null) return;

        var sps = context.ActiveSps;
        var pps = context.ActivePps;
        if (sps == null || pps == null) return;

        // Skip BS computation when deblocking is disabled for this CTU's slice
        // (matches FFmpeg: ff_hevc_deblocking_boundary_strengths is only called when !disable_deblocking_filter_flag)
        if (ctuDeblockDisabled != null)
        {
            int ctbSize0 = sps.CtbSizeY;
            int ctbAddr0 = (y0 / ctbSize0) * sps.PicWidthInCtbsY + (x0 / ctbSize0);
            if (ctbAddr0 >= 0 && ctbAddr0 < ctuDeblockDisabled.Length && ctuDeblockDisabled[ctbAddr0])
                return;
        }

        int width = sps.PictureWidthInLumaSamples;
        int height = sps.PictureHeightInLumaSamples;
        int tuSize = 1 << log2TrafoSize;
        int ctbSize = sps.CtbSizeY;

        // Check if vertical (left) edge at CTB boundary should be suppressed
        // due to cross-slice or cross-tile filtering being disabled
        bool suppressVerticalEdge = false;
        if (x0 > 0 && (x0 % ctbSize) == 0 && ctuBoundaryFlags != null)
        {
            int ctbAddr = (y0 / ctbSize) * sps.PicWidthInCtbsY + (x0 / ctbSize);
            if (ctbAddr >= 0 && ctbAddr < ctuBoundaryFlags.Length)
            {
                byte bf = ctuBoundaryFlags[ctbAddr];
                if ((bf & BoundaryLeftSlice) != 0 && !ctuLoopFilterAcrossSlices![ctbAddr])
                    suppressVerticalEdge = true;
                if ((bf & BoundaryLeftTile) != 0 && !pps.LoopFilterAcrossTilesEnabled)
                    suppressVerticalEdge = true;
            }
        }

        // Check if horizontal (top) edge at CTB boundary should be suppressed
        bool suppressHorizontalEdge = false;
        if (y0 > 0 && (y0 % ctbSize) == 0 && ctuBoundaryFlags != null)
        {
            int ctbAddr = (y0 / ctbSize) * sps.PicWidthInCtbsY + (x0 / ctbSize);
            if (ctbAddr >= 0 && ctbAddr < ctuBoundaryFlags.Length)
            {
                byte bf = ctuBoundaryFlags[ctbAddr];
                if ((bf & BoundaryUpperSlice) != 0 && !ctuLoopFilterAcrossSlices![ctbAddr])
                    suppressHorizontalEdge = true;
                if ((bf & BoundaryUpperTile) != 0 && !pps.LoopFilterAcrossTilesEnabled)
                    suppressHorizontalEdge = true;
            }
        }

        // Mark vertical (left) edge of TU — only at 8-pixel aligned boundaries
        if (x0 > 0 && (x0 & 7) == 0 && !suppressVerticalEdge)
        {
            for (int dy = 0; dy < tuSize && (y0 + dy) < height; dy += 4)
            {
                int y = y0 + dy;
                int x4 = x0 >> 2;
                int y4 = y >> 2;

                byte bs;
                if (isIntra)
                {
                    bs = 2;
                }
                else
                {
                    // Check if neighbor to the left is intra
                    int leftX8 = (x0 - 1) >> 3;
                    int y8 = y >> 3;
                    if (isIntraTab8x8![y8 * picWidthIn8 + leftX8] != 0)
                    {
                        bs = 2;
                    }
                    else if (cbfLuma)
                    {
                        bs = 1;
                    }
                    else
                    {
                        // Check cbf on neighbor side
                        int leftPuX = (x0 - 1) >> 2;
                        int puY = y >> 2;
                        if (cbfLumaField != null && leftPuX >= 0 && leftPuX < puWidthIn4 &&
                            puY >= 0 && puY < puHeightIn4 &&
                            cbfLumaField[puY * puWidthIn4 + leftPuX] != 0)
                        {
                            bs = 1;
                        }
                        else
                        {
                            bs = ComputeInterBs(x0, y, x0 - 1, y);
                        }
                    }
                }

                vertBsTab[y4 * bsWidth + x4] = bs;
            }
        }

        // Mark horizontal (top) edge of TU — only at 8-pixel aligned boundaries
        if (y0 > 0 && (y0 & 7) == 0 && !suppressHorizontalEdge)
        {
            for (int dx = 0; dx < tuSize && (x0 + dx) < width; dx += 4)
            {
                int x = x0 + dx;
                int x4 = x >> 2;
                int y4 = y0 >> 2;

                byte bs;
                if (isIntra)
                {
                    bs = 2;
                }
                else
                {
                    int aboveY8 = (y0 - 1) >> 3;
                    int x8 = x >> 3;
                    if (isIntraTab8x8![aboveY8 * picWidthIn8 + x8] != 0)
                    {
                        bs = 2;
                    }
                    else if (cbfLuma)
                    {
                        bs = 1;
                    }
                    else
                    {
                        int abovePuY = (y0 - 1) >> 2;
                        int puX = x >> 2;
                        if (cbfLumaField != null && puX >= 0 && puX < puWidthIn4 &&
                            abovePuY >= 0 && abovePuY < puHeightIn4 &&
                            cbfLumaField[abovePuY * puWidthIn4 + puX] != 0)
                        {
                            bs = 1;
                        }
                        else
                        {
                            bs = ComputeInterBs(x, y0, x, y0 - 1);
                        }
                    }
                }

                horizBsTab[y4 * bsWidth + x4] = bs;
            }
        }

        // Mark internal PU boundaries within inter TUs (FFmpeg filter.c lines 833-864)
        // For inter blocks where TU is larger than min PU, check for PU boundaries at 8-pixel spacing
        int log2MinPuSize = sps.Log2MinCbSizeY - 1;
        if (log2TrafoSize > log2MinPuSize && !isIntra)
        {
            // Internal horizontal PU boundaries at 8-pixel spacing
            for (int j = 8; j < tuSize; j += 8)
            {
                int yAbove = y0 + j - 1;
                int yCurr = y0 + j;
                if (yCurr >= height) break;

                for (int i = 0; i < tuSize && (x0 + i) < width; i += 4)
                {
                    int x = x0 + i;
                    byte bs = ComputeInternalBs(x, yCurr, x, yAbove);
                    horizBsTab[(yCurr >> 2) * bsWidth + (x >> 2)] = bs;
                }
            }

            // Internal vertical PU boundaries at 8-pixel spacing
            for (int j = 0; j < tuSize && (y0 + j) < height; j += 4)
            {
                int y = y0 + j;
                for (int i = 8; i < tuSize; i += 8)
                {
                    int xLeft = x0 + i - 1;
                    int xCurr = x0 + i;
                    if (xCurr >= width) break;

                    byte bs = ComputeInternalBs(xCurr, y, xLeft, y);
                    vertBsTab[(y >> 2) * bsWidth + (xCurr >> 2)] = bs;
                }
            }
        }
    }

    /// <summary>
    /// Computes boundary strength for an edge between two inter-predicted blocks.
    /// HEVC spec 8.7.2.4: returns 1 if coded residual or MV/ref mismatch, 0 otherwise.
    /// Used at TU boundary edges where cbf check is needed.
    /// At slice boundaries, resolves the P-side ref_idx through its per-CTB ref list.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ComputeInterBs(int xQ, int yQ, int xP, int yP)
    {
        if (cbfLumaField == null || mvFieldL0X == null) return 1;

        int q4x = xQ >> 2, q4y = yQ >> 2;
        int p4x = xP >> 2, p4y = yP >> 2;
        int qIdx = q4y * puWidthIn4 + q4x;
        int pIdx = p4y * puWidthIn4 + p4x;

        if (qIdx < 0 || qIdx >= cbfLumaField.Length || pIdx < 0 || pIdx >= cbfLumaField.Length)
            return 1;

        // Check coded residual (cbf_luma) on either side
        if (cbfLumaField[qIdx] != 0 || cbfLumaField[pIdx] != 0)
            return 1;

        // Check if P is in a different slice — if so, use per-CTB ref list for P
        SliceRefListSnapshot? pRefList = GetNeighborRefList(xQ, yQ, xP, yP);
        return ComputeMvBs(qIdx, pIdx, pRefList);
    }

    /// <summary>
    /// Computes boundary strength based on MV/ref comparison only (no cbf check).
    /// Used for internal PU boundaries within a TU, matching FFmpeg's boundary_strength().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ComputeInternalBs(int xQ, int yQ, int xP, int yP)
    {
        if (mvFieldL0X == null) return 0;

        int q4x = xQ >> 2, q4y = yQ >> 2;
        int p4x = xP >> 2, p4y = yP >> 2;
        int qIdx = q4y * puWidthIn4 + q4x;
        int pIdx = p4y * puWidthIn4 + p4x;

        int fieldLen = puWidthIn4 * puHeightIn4;
        if (qIdx < 0 || qIdx >= fieldLen || pIdx < 0 || pIdx >= fieldLen)
            return 0;

        return ComputeMvBs(qIdx, pIdx);
    }

    /// <summary>
    /// Core MV/ref boundary strength computation. Returns 1 if MVs or references differ
    /// beyond the HEVC threshold (4 quarter-pel = 1 integer sample), 0 otherwise.
    /// Matches FFmpeg's boundary_strength() in filter.c.
    /// When pRefList is non-null, P-side ref_idx is resolved through that per-CTB snapshot
    /// (for cross-slice boundaries). Q-side always uses the current slice's ref lists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ComputeMvBs(int qIdx, int pIdx, SliceRefListSnapshot? pRefList = null)
    {
        byte predQ = predModeField![qIdx];
        byte predP = predModeField[pIdx];

        // Both bi-pred (PF_BI = 3)
        if (predQ == 3 && predP == 3)
        {
            // Resolve reference POCs for both blocks
            int refQPocL0 = ResolveRefPoc(0, refIdxFieldL0![qIdx]);
            int refQPocL1 = ResolveRefPoc(1, refIdxFieldL1![qIdx]);
            int refPPocL0 = ResolveRefPocForP(pRefList, 0, refIdxFieldL0[pIdx]);
            int refPPocL1 = ResolveRefPocForP(pRefList, 1, refIdxFieldL1[pIdx]);

            // Special case: both L0 and L1 reference the same picture for both blocks
            if (refQPocL0 == refQPocL1 && refPPocL0 == refPPocL1 && refQPocL0 == refPPocL0)
            {
                // Both orderings are valid — need BOTH to fail for BS=1
                if ((Math.Abs(mvFieldL0X![pIdx] - mvFieldL0X[qIdx]) >= 4 || Math.Abs(mvFieldL0Y![pIdx] - mvFieldL0Y![qIdx]) >= 4 ||
                     Math.Abs(mvFieldL1X![pIdx] - mvFieldL1X![qIdx]) >= 4 || Math.Abs(mvFieldL1Y![pIdx] - mvFieldL1Y![qIdx]) >= 4) &&
                    (Math.Abs(mvFieldL1X![pIdx] - mvFieldL0X![qIdx]) >= 4 || Math.Abs(mvFieldL1Y![pIdx] - mvFieldL0Y![qIdx]) >= 4 ||
                     Math.Abs(mvFieldL0X![pIdx] - mvFieldL1X![qIdx]) >= 4 || Math.Abs(mvFieldL0Y![pIdx] - mvFieldL1Y![qIdx]) >= 4))
                    return 1;
                return 0;
            }

            // Same-order: L0 refs match AND L1 refs match
            if (refPPocL0 == refQPocL0 && refPPocL1 == refQPocL1)
            {
                if (Math.Abs(mvFieldL0X![pIdx] - mvFieldL0X[qIdx]) >= 4 || Math.Abs(mvFieldL0Y![pIdx] - mvFieldL0Y![qIdx]) >= 4 ||
                    Math.Abs(mvFieldL1X![pIdx] - mvFieldL1X![qIdx]) >= 4 || Math.Abs(mvFieldL1Y![pIdx] - mvFieldL1Y![qIdx]) >= 4)
                    return 1;
                return 0;
            }

            // Crossed: L0↔L1
            if (refPPocL1 == refQPocL0 && refPPocL0 == refQPocL1)
            {
                if (Math.Abs(mvFieldL1X![pIdx] - mvFieldL0X![qIdx]) >= 4 || Math.Abs(mvFieldL1Y![pIdx] - mvFieldL0Y![qIdx]) >= 4 ||
                    Math.Abs(mvFieldL0X![pIdx] - mvFieldL1X![qIdx]) >= 4 || Math.Abs(mvFieldL0Y![pIdx] - mvFieldL1Y![qIdx]) >= 4)
                    return 1;
                return 0;
            }

            return 1;
        }

        // Both uni-pred (one MV each)
        if (predQ != 3 && predP != 3 && predQ != 0 && predP != 0)
        {
            // Pick the active list's MV and resolved ref POC for each block
            int refPocQ = (predQ & 1) != 0
                ? ResolveRefPoc(0, refIdxFieldL0![qIdx])
                : ResolveRefPoc(1, refIdxFieldL1![qIdx]);
            int refPocP = (predP & 1) != 0
                ? ResolveRefPocForP(pRefList, 0, refIdxFieldL0![pIdx])
                : ResolveRefPocForP(pRefList, 1, refIdxFieldL1![pIdx]);

            if (refPocQ != refPocP) return 1;

            short mvQx = (predQ & 1) != 0 ? mvFieldL0X![qIdx] : mvFieldL1X![qIdx];
            short mvQy = (predQ & 1) != 0 ? mvFieldL0Y![qIdx] : mvFieldL1Y![qIdx];
            short mvPx = (predP & 1) != 0 ? mvFieldL0X![pIdx] : mvFieldL1X![pIdx];
            short mvPy = (predP & 1) != 0 ? mvFieldL0Y![pIdx] : mvFieldL1Y![pIdx];

            if (Math.Abs(mvQx - mvPx) >= 4 || Math.Abs(mvQy - mvPy) >= 4) return 1;
            return 0;
        }

        // Mixed (one bi-pred, one uni-pred) or unexpected → BS=1
        return 1;
    }

    /// <summary>
    /// Resolves a reference index to its picture's POC using the current slice's ref pic lists.
    /// Matches FFmpeg's refPicList[list].list[ref_idx] lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveRefPoc(int list, sbyte refIdx)
    {
        if (refIdx < 0) return -1;
        if (list == 0 && refIdx < numRefList0)
            return refPocList0[refIdx];
        if (list == 1 && refIdx < numRefList1)
            return refPocList1[refIdx];
        return -1;
    }

    /// <summary>
    /// Resolves a P-side (neighbor) reference index to POC. Uses per-CTB ref list snapshot
    /// when available (cross-slice boundary), otherwise falls back to current slice's lists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveRefPocForP(SliceRefListSnapshot? pRefList, int list, sbyte refIdx)
    {
        if (refIdx < 0) return -1;
        if (pRefList != null)
        {
            if (list == 0 && refIdx < pRefList.CountL0)
                return pRefList.PocL0[refIdx];
            if (list == 1 && refIdx < pRefList.CountL1)
                return pRefList.PocL1[refIdx];
            return -1;
        }
        return ResolveRefPoc(list, refIdx);
    }

    /// <summary>
    /// Returns the per-CTB ref list snapshot for a neighbor position if it's in a different slice.
    /// Used by deblocking boundary strength at cross-slice edges.
    /// Matches FFmpeg's use of ff_hevc_get_ref_list for the neighbor at slice boundaries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SliceRefListSnapshot? GetNeighborRefList(int xQ, int yQ, int xP, int yP)
    {
        if (tabSliceAddress == null || currentFramePerCtbSliceIdx == null || currentFrameSliceRefLists == null)
            return null;
        var sps = context.ActiveSps;
        if (sps == null) return null;

        int ctbSize = sps.CtbSizeY;
        int picWidthInCtbs = sps.PicWidthInCtbsY;
        int qCtb = (yQ / ctbSize) * picWidthInCtbs + (xQ / ctbSize);
        int pCtb = (yP / ctbSize) * picWidthInCtbs + (xP / ctbSize);

        if (qCtb == pCtb) return null; // Same CTB, same slice
        if (qCtb >= tabSliceAddress.Length || pCtb >= tabSliceAddress.Length) return null;
        if (tabSliceAddress[qCtb] == tabSliceAddress[pCtb]) return null; // Same slice

        // P is in a different slice — look up its per-CTB ref list
        if (pCtb >= currentFramePerCtbSliceIdx.Length) return null;
        int pSliceIdx = currentFramePerCtbSliceIdx[pCtb];
        if (pSliceIdx >= currentFrameSliceRefLists.Count) return null;
        return currentFrameSliceRefLists[pSliceIdx];
    }

    /// <summary>
    /// Decodes split_cu_flag using CABAC with neighbor depth context.
    /// Matches FFmpeg's ff_hevc_split_coding_unit_flag_decode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DecodeSplitCuFlag(ref HevcCabacDecoder cabac, HevcSequenceParameterSet sps,
        int ctDepth, int x0, int y0)
    {
        int inc = 0;
        int x0b = x0 & (sps.CtbSizeY - 1); // x0 % ctbSizeY
        int y0b = y0 & (sps.CtbSizeY - 1);
        int xCb = x0 >> sps.Log2MinCbSizeY;
        int yCb = y0 >> sps.Log2MinCbSizeY;
        int minCbWidth = sps.PicWidthInMinCbsY;

        if (tabCtDepth != null)
        {
            int depthLeft = 0, depthTop = 0;
            if (ctbLeftFlag || x0b != 0)
            {
                if (xCb > 0)
                    depthLeft = tabCtDepth[yCb * minCbWidth + xCb - 1];
            }
            if (ctbUpFlag || y0b != 0)
            {
                if (yCb > 0)
                    depthTop = tabCtDepth[(yCb - 1) * minCbWidth + xCb];
            }

            inc += depthLeft > ctDepth ? 1 : 0;
            inc += depthTop > ctDepth ? 1 : 0;
        }

        return cabac.DecodeBin(HevcCabacContextIndex.SplitCuFlag + inc) != 0;
    }

    /// <summary>
    /// Decodes part_mode. Matches FFmpeg's ff_hevc_part_mode_decode.
    /// </summary>
    private HevcPartitionMode DecodePartMode(ref HevcCabacDecoder cabac, HevcSequenceParameterSet sps, int log2CbSize)
    {
        // First bin: 1 → 2Nx2N
        if (cabac.DecodeBin(HevcCabacContextIndex.PartMode) != 0)
            return HevcPartitionMode.Part2Nx2N;

        if (log2CbSize == sps.Log2MinCbSizeY)
        {
            if (currentPredMode == HevcPredictionMode.Intra)
                return HevcPartitionMode.PartNxN; // 0 → NxN for intra at min size

            if (cabac.DecodeBin(HevcCabacContextIndex.PartMode + 1) != 0)
                return HevcPartitionMode.Part2NxN; // 01

            if (log2CbSize == 3) // 8x8
                return HevcPartitionMode.PartNx2N; // 00

            if (cabac.DecodeBin(HevcCabacContextIndex.PartMode + 2) != 0)
                return HevcPartitionMode.PartNx2N; // 001

            return HevcPartitionMode.PartNxN; // 000
        }

        if (!sps.AmpEnabled)
        {
            if (cabac.DecodeBin(HevcCabacContextIndex.PartMode + 1) != 0)
                return HevcPartitionMode.Part2NxN;
            return HevcPartitionMode.PartNx2N;
        }

        // AMP enabled, not at minimum size
        if (cabac.DecodeBin(HevcCabacContextIndex.PartMode + 1) != 0)
        {
            // 01X, 01XX
            if (cabac.DecodeBin(HevcCabacContextIndex.PartMode + 3) != 0)
                return HevcPartitionMode.Part2NxN; // 011

            if (cabac.DecodeBypass() != 0)
                return HevcPartitionMode.Part2NxnD; // 0101

            return HevcPartitionMode.Part2NxnU; // 0100
        }

        if (cabac.DecodeBin(HevcCabacContextIndex.PartMode + 3) != 0)
            return HevcPartitionMode.PartNx2N; // 001

        if (cabac.DecodeBypass() != 0)
            return HevcPartitionMode.PartnRx2N; // 0001

        return HevcPartitionMode.PartnLx2N; // 0000
    }

    /// <summary>
    /// Decodes intra prediction modes for all PUs in the CU.
    /// Matches FFmpeg's intra_prediction_unit.
    /// </summary>
    private void IntraPredictionUnit(ref HevcCabacDecoder cabac, HevcSequenceParameterSet sps,
        int x0, int y0, int log2CbSize)
    {
        bool split = currentPartMode == HevcPartitionMode.PartNxN;
        int pbSize = (1 << log2CbSize) >> (split ? 1 : 0);
        int side = split ? 2 : 1;

        // Decode prev_intra_luma_pred_flag for all PUs
        Span<int> prevIntraLumaPredFlag = stackalloc int[4];
        for (int i = 0; i < side; i++)
            for (int j = 0; j < side; j++)
                prevIntraLumaPredFlag[2 * i + j] = cabac.DecodeBin(HevcCabacContextIndex.PrevIntraLumaPredFlag);

        // Decode mpm_idx or rem_intra_luma_pred_mode for each PU
        for (int i = 0; i < side; i++)
        {
            for (int j = 0; j < side; j++)
            {
                int idx = 2 * i + j;
                int mpmIdx = 0;
                int remIntraLumaPredMode = 0;

                if (prevIntraLumaPredFlag[idx] != 0)
                {
                    // mpm_idx: truncated unary, max 2, bypass bins
                    mpmIdx = 0;
                    while (mpmIdx < 2 && cabac.DecodeBypass() != 0)
                        mpmIdx++;
                }
                else
                {
                    // rem_intra_luma_pred_mode: 5 bypass bins
                    remIntraLumaPredMode = (int)cabac.DecodeBypassBits(5);
                }

                int predMode = LumaIntraPredMode(sps,
                    x0 + pbSize * j, y0 + pbSize * i, pbSize,
                    prevIntraLumaPredFlag[idx] != 0, mpmIdx, remIntraLumaPredMode);

                intraPredModeForPu[idx] = predMode;
            }
        }

        // Chroma intra prediction mode
        // For 420/422: one intra_chroma_pred_mode per CU
        // For 444 (chroma_format_idc == 3): one intra_chroma_pred_mode per PU (FFmpeg hevcdec.c:2356-2369)
        if (sps.ChromaFormatIdc == HevcChromaFormat.Chroma444)
        {
            for (int i = 0; i < side; i++)
            {
                for (int j = 0; j < side; j++)
                {
                    int idx = 2 * i + j;
                    int chromaMode = DecodeIntraChromaPredMode(ref cabac);
                    rawChromaModeForPu[idx] = chromaMode;
                    if (chromaMode != 4)
                    {
                        int derived = DeriveIntraChromaPredMode(chromaMode, intraPredModeForPu[idx]);
                        intraPredModeCForPu[idx] = derived;
                    }
                    else
                    {
                        intraPredModeCForPu[idx] = intraPredModeForPu[idx];
                    }
                }
            }
            intraPredModeC = intraPredModeCForPu[0];
        }
        else if (sps.ChromaFormatIdc != HevcChromaFormat.Monochrome)
        {
            // 420/422: one chroma mode per CU, replicate to all PU slots for NxN consistency
            int chromaMode = DecodeIntraChromaPredMode(ref cabac);
            rawChromaModeForPu[0] = rawChromaModeForPu[1] = rawChromaModeForPu[2] = rawChromaModeForPu[3] = chromaMode;
            int modeIdx = DeriveIntraChromaPredMode(chromaMode, intraPredModeForPu[0]);
            // 422: apply additional angle remapping (HEVC spec Table 8-2, FFmpeg tab_mode_idx)
            // 422 chroma has double height vs width, so angular modes are remapped to account
            // for the non-square aspect ratio of the chroma coding blocks.
            if (sps.ChromaFormatIdc == HevcChromaFormat.Chroma422)
                modeIdx = ChromaMode422Table[modeIdx];
            intraPredModeC = modeIdx;
            intraPredModeCForPu[0] = intraPredModeCForPu[1] = intraPredModeCForPu[2] = intraPredModeCForPu[3] = intraPredModeC;
        }
        else
        {
            rawChromaModeForPu[0] = rawChromaModeForPu[1] = rawChromaModeForPu[2] = rawChromaModeForPu[3] = 4;
            intraPredModeC = intraPredModeForPu[0];
            intraPredModeCForPu[0] = intraPredModeCForPu[1] = intraPredModeCForPu[2] = intraPredModeCForPu[3] = intraPredModeC;
        }
    }

    /// <summary>
    /// Derives luma intra prediction mode from MPM (Most Probable Mode) candidates.
    /// Matches FFmpeg's luma_intra_pred_mode.
    /// </summary>
    private int LumaIntraPredMode(HevcSequenceParameterSet sps,
        int x0, int y0, int puSize, bool prevIntraLumaPredFlag, int mpmIdx, int remIntraPredMode)
    {
        int log2MinPuSize = 2; // HEVC min PU = 4x4
        int xPu = x0 >> log2MinPuSize;
        int yPu = y0 >> log2MinPuSize;
        int minPuWidth = sps.PictureWidthInLumaSamples >> log2MinPuSize;
        int sizeInPus = puSize >> log2MinPuSize;

        int x0b = x0 & (sps.CtbSizeY - 1);
        int y0b = y0 & (sps.CtbSizeY - 1);

        // Get neighbor intra pred modes
        const int INTRA_DC = 1;
        const int INTRA_PLANAR = 0;
        const int INTRA_ANGULAR_26 = 26;

        int candUp = (ctbUpFlag || y0b != 0) && tabIntraPredMode != null && yPu > 0
            ? tabIntraPredMode[(yPu - 1) * minPuWidth + xPu]
            : INTRA_DC;

        int candLeft = (ctbLeftFlag || x0b != 0) && tabIntraPredMode != null && xPu > 0
            ? tabIntraPredMode[yPu * minPuWidth + xPu - 1]
            : INTRA_DC;

        // Intra pred mode prediction doesn't cross vertical CTB boundaries
        int yCtb = (y0 >> sps.Log2CtbSizeY) << sps.Log2CtbSizeY;
        if ((y0 - 1) < yCtb)
            candUp = INTRA_DC;

        // Derive MPM candidates
        Span<int> candidate = stackalloc int[3];
        if (candLeft == candUp)
        {
            if (candLeft < 2)
            {
                candidate[0] = INTRA_PLANAR;
                candidate[1] = INTRA_DC;
                candidate[2] = INTRA_ANGULAR_26;
            }
            else
            {
                candidate[0] = candLeft;
                candidate[1] = 2 + ((candLeft - 2 - 1 + 32) & 31);
                candidate[2] = 2 + ((candLeft - 2 + 1) & 31);
            }
        }
        else
        {
            candidate[0] = candLeft;
            candidate[1] = candUp;
            if (candidate[0] != INTRA_PLANAR && candidate[1] != INTRA_PLANAR)
                candidate[2] = INTRA_PLANAR;
            else if (candidate[0] != INTRA_DC && candidate[1] != INTRA_DC)
                candidate[2] = INTRA_DC;
            else
                candidate[2] = INTRA_ANGULAR_26;
        }

        int intraPredMode;
        if (prevIntraLumaPredFlag)
        {
            intraPredMode = candidate[mpmIdx];
        }
        else
        {
            // Sort candidates
            if (candidate[0] > candidate[1]) (candidate[0], candidate[1]) = (candidate[1], candidate[0]);
            if (candidate[0] > candidate[2]) (candidate[0], candidate[2]) = (candidate[2], candidate[0]);
            if (candidate[1] > candidate[2]) (candidate[1], candidate[2]) = (candidate[2], candidate[1]);

            intraPredMode = remIntraPredMode;
            for (int i = 0; i < 3; i++)
                if (intraPredMode >= candidate[i])
                    intraPredMode++;
        }

        // Store in tab_ipm for neighbor context
        if (tabIntraPredMode != null)
        {
            if (sizeInPus == 0) sizeInPus = 1;
            for (int i = 0; i < sizeInPus; i++)
                for (int j = 0; j < sizeInPus; j++)
                {
                    int py = yPu + i;
                    int px = xPu + j;
                    if (py < sps.PictureHeightInLumaSamples >> log2MinPuSize &&
                        px < minPuWidth)
                        tabIntraPredMode[py * minPuWidth + px] = (byte)intraPredMode;
                }
        }

        return intraPredMode;
    }

    /// <summary>
    /// Decodes intra_chroma_pred_mode. Matches FFmpeg's ff_hevc_intra_chroma_pred_mode_decode.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeIntraChromaPredMode(ref HevcCabacDecoder cabac)
    {
        if (cabac.DecodeBin(HevcCabacContextIndex.IntraChromaPredMode) == 0)
            return 4; // Derived from luma

        int ret = cabac.DecodeBypass() << 1;
        ret |= cabac.DecodeBypass();
        return ret; // 0, 1, 2, or 3
    }

    /// <summary>
    /// Derives the chroma intra prediction mode from the signaled mode and luma mode.
    /// </summary>
    private static int DeriveIntraChromaPredMode(int chromaMode, int lumaMode)
    {
        ReadOnlySpan<int> intraChromaTable = [0, 26, 10, 1]; // Planar, Angular26, Angular10, DC

        if (chromaMode == 4)
            return lumaMode; // Same as luma

        int derivedMode = intraChromaTable[chromaMode];
        if (lumaMode == derivedMode)
            return 34; // Angular34 as fallback
        return derivedMode;
    }

    /// <summary>
    /// 422 chroma intra prediction mode remapping table (HEVC spec Table 8-2).
    /// Maps standard intra modes [0..34] to 422-adjusted modes that account for
    /// the non-square aspect ratio of 422 chroma blocks (half-width, full-height).
    /// Matches FFmpeg's tab_mode_idx in hevcdec.c.
    /// </summary>
    private static ReadOnlySpan<byte> ChromaMode422Table =>
    [
         0,  1,  2,  2,  2,  2,  3,  5,  7,  8, 10, 12, 13, 15, 17, 18, 19, 20,
        21, 22, 23, 23, 24, 24, 25, 25, 26, 27, 27, 28, 28, 29, 29, 30, 31
    ];

    /// <summary>
    /// Decodes cross-component prediction parameters (log2_res_scale_abs_plus1, res_scale_sign_flag).
    /// Matches FFmpeg's hls_cross_component_pred. idx=0 for Cb, idx=1 for Cr.
    /// Returns res_scale_val: 0 if disabled, or ±(1 << (log2_res_scale_abs_plus1 - 1)).
    /// </summary>
    private int DecodeCrossComponentPred(ref HevcCabacDecoder cabac, int idx)
    {
        // log2_res_scale_abs_plus1: truncated unary coded, max 4 bins
        // FFmpeg cabac.c:851-858: context = LOG2_RES_SCALE_ABS_OFFSET + 4*idx + i
        int log2ResScaleAbsPlus1 = 0;
        while (log2ResScaleAbsPlus1 < 4 &&
               cabac.DecodeBin(HevcCabacContextIndex.Log2ResScaleAbsPlus1 + 4 * idx + log2ResScaleAbsPlus1) != 0)
        {
            log2ResScaleAbsPlus1++;
        }

        if (log2ResScaleAbsPlus1 != 0)
        {
            // res_scale_sign_flag: 1 context-coded bin
            // FFmpeg cabac.c:861-864: context = RES_SCALE_SIGN_FLAG_OFFSET + idx
            int resScaleSignFlag = cabac.DecodeBin(HevcCabacContextIndex.ResScaleSignFlag + idx);
            return (1 << (log2ResScaleAbsPlus1 - 1)) * (1 - 2 * resScaleSignFlag);
        }

        return 0;
    }

    /// <summary>
    /// Stores CT depth into the per-picture array.
    /// Matches FFmpeg's set_ct_depth.
    /// </summary>
    private void SetCtDepth(HevcSequenceParameterSet sps, int x0, int y0, int log2CbSize, int ctDepth)
    {
        if (tabCtDepth == null) return;

        int length = (1 << log2CbSize) >> sps.Log2MinCbSizeY;
        int xCb = x0 >> sps.Log2MinCbSizeY;
        int yCb = y0 >> sps.Log2MinCbSizeY;
        int minCbWidth = sps.PicWidthInMinCbsY;

        for (int y = 0; y < length; y++)
        {
            int offset = (yCb + y) * minCbWidth + xCb;
            for (int x = 0; x < length; x++)
                tabCtDepth[offset + x] = (byte)ctDepth;
        }
    }

    /// <summary>
    /// Sets default intra prediction mode (DC) for all PUs in the CU.
    /// Used when skip mode or when intra pred is not decoded.
    /// </summary>
    private void SetIntraPredModeDefault(HevcSequenceParameterSet sps, int x0, int y0, int log2CbSize)
    {
        if (tabIntraPredMode == null) return;

        int log2MinPuSize = 2;
        int pbSize = 1 << log2CbSize;
        int sizeInPus = pbSize >> log2MinPuSize;
        int minPuWidth = sps.PictureWidthInLumaSamples >> log2MinPuSize;
        int xPu = x0 >> log2MinPuSize;
        int yPu = y0 >> log2MinPuSize;

        if (sizeInPus == 0) sizeInPus = 1;
        for (int i = 0; i < sizeInPus; i++)
            for (int j = 0; j < sizeInPus; j++)
            {
                int py = yPu + i;
                int px = xPu + j;
                if (py < sps.PictureHeightInLumaSamples >> log2MinPuSize && px < minPuWidth)
                    tabIntraPredMode[py * minPuWidth + px] = 1; // DC
            }
    }

    /// <summary>
    /// Decodes the transform tree recursively.
    /// Matches FFmpeg's hls_transform_tree.
    /// </summary>
    private void TransformTree(ref HevcCabacDecoder cabac, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int xBase, int yBase,
        int log2CbSize, int log2TrafoSize, int trafoDepth, int blkIdx,
        bool baseCbfCb0, bool baseCbfCb1, bool baseCbfCr0, bool baseCbfCr1, Span<short> residual)
    {
        bool cbfCb0 = baseCbfCb0;
        bool cbfCb1 = baseCbfCb1;
        bool cbfCr0 = baseCbfCr0;
        bool cbfCr1 = baseCbfCr1;
        bool is422 = sps.ChromaFormatIdc == HevcChromaFormat.Chroma422;

        // Set TU intra pred mode (matches FFmpeg's hls_transform_tree)
        // For NxN CUs: set at trafoDepth==1 from the PU's blkIdx, persists for deeper splits
        // For non-NxN: set at every depth from PU[0]
        if (currentIntraSplitFlag)
        {
            if (trafoDepth == 1)
            {
                currentTuIntraPredMode = intraPredModeForPu[blkIdx];
                currentTuIntraPredModeC = intraPredModeCForPu[blkIdx];
                currentTuRawChromaMode = rawChromaModeForPu[blkIdx];
            }
        }
        else
        {
            currentTuIntraPredMode = intraPredModeForPu[0];
            currentTuIntraPredModeC = intraPredModeCForPu[0];
            currentTuRawChromaMode = rawChromaModeForPu[0];
        }

        // Determine split_transform_flag
        bool splitTransformFlag;
        byte ctxStateSplitTf = 0;
        if (log2TrafoSize <= sps.Log2MaxTbSizeY &&
            log2TrafoSize > sps.Log2MinTbSizeY &&
            trafoDepth < currentMaxTrafoDepth &&
            !(currentIntraSplitFlag && trafoDepth == 0))
        {
            int stfCtx = HevcCabacContextIndex.SplitTransformFlag + 5 - log2TrafoSize;
            ctxStateSplitTf = cabac.GetContextState(stfCtx);
            // CABAC context: 5 - log2_trafo_size
            splitTransformFlag = cabac.DecodeBin(stfCtx) != 0;
        }
        else
        {
            bool interSplit = sps.MaxTransformHierarchyDepthInter == 0 &&
                              currentPredMode != HevcPredictionMode.Intra &&
                              currentPartMode != HevcPartitionMode.Part2Nx2N &&
                              trafoDepth == 0;

            splitTransformFlag = log2TrafoSize > sps.Log2MaxTbSizeY ||
                                 (currentIntraSplitFlag && trafoDepth == 0) ||
                                 interSplit;
        }

        // Decode cbf_cb and cbf_cr (chroma coded block flags)
        // For 422: decode an additional cbf[1] for each component when appropriate
        if (sps.ChromaFormatIdc != HevcChromaFormat.Monochrome &&
            (log2TrafoSize > 2 || sps.ChromaFormatIdc == HevcChromaFormat.Chroma444))
        {
            if (trafoDepth == 0 || cbfCb0)
            {
                cbfCb0 = cabac.DecodeBin(HevcCabacContextIndex.CbfChroma + trafoDepth) != 0;
                if (is422 && (!splitTransformFlag || log2TrafoSize == 3))
                    cbfCb1 = cabac.DecodeBin(HevcCabacContextIndex.CbfChroma + trafoDepth) != 0;
            }

            if (trafoDepth == 0 || cbfCr0)
            {
                cbfCr0 = cabac.DecodeBin(HevcCabacContextIndex.CbfChroma + trafoDepth) != 0;
                if (is422 && (!splitTransformFlag || log2TrafoSize == 3))
                    cbfCr1 = cabac.DecodeBin(HevcCabacContextIndex.CbfChroma + trafoDepth) != 0;
            }
        }

        if (splitTransformFlag)
        {
            int trafoSizeSplit = 1 << (log2TrafoSize - 1);
            int x1 = x0 + trafoSizeSplit;
            int y1 = y0 + trafoSizeSplit;

            TransformTree(ref cabac, sliceHeader, sps, pps, x0, y0, x0, y0,
                log2CbSize, log2TrafoSize - 1, trafoDepth + 1, 0, cbfCb0, cbfCb1, cbfCr0, cbfCr1, residual);
            TransformTree(ref cabac, sliceHeader, sps, pps, x1, y0, x0, y0,
                log2CbSize, log2TrafoSize - 1, trafoDepth + 1, 1, cbfCb0, cbfCb1, cbfCr0, cbfCr1, residual);
            TransformTree(ref cabac, sliceHeader, sps, pps, x0, y1, x0, y0,
                log2CbSize, log2TrafoSize - 1, trafoDepth + 1, 2, cbfCb0, cbfCb1, cbfCr0, cbfCr1, residual);
            TransformTree(ref cabac, sliceHeader, sps, pps, x1, y1, x0, y0,
                log2CbSize, log2TrafoSize - 1, trafoDepth + 1, 3, cbfCb0, cbfCb1, cbfCr0, cbfCr1, residual);
        }
        else
        {
            // Leaf: decode cbf_luma
            // For 422, check both cbf[0] and cbf[1] for chroma presence
            bool anyCbfCb = cbfCb0 || (is422 && cbfCb1);
            bool anyCbfCr = cbfCr0 || (is422 && cbfCr1);
            bool cbfLuma = true;
            byte ctxStateCbfLuma = 0;
            uint preRange = 0, preOffset = 0;
            if (currentPredMode == HevcPredictionMode.Intra || trafoDepth != 0 || anyCbfCb || anyCbfCr)
            {
                int cbfLumaCtx = HevcCabacContextIndex.CbfLuma + (trafoDepth == 0 ? 1 : 0);
                ctxStateCbfLuma = cabac.GetContextState(cbfLumaCtx);
                preRange = cabac.DiagIvlRange;
                preOffset = cabac.DiagIvlOffset;
                cbfLuma = cabac.DecodeBin(cbfLumaCtx) != 0;
            }

            // Log transform tree decisions
            if (DiagTransformTreeLog != null)
            {
                DiagTransformTreeLog.Add((x0, y0, trafoDepth, splitTransformFlag, cbfCb0, cbfCr0, cbfLuma,
                    ctxStateSplitTf, ctxStateCbfLuma, preRange, preOffset));
            }

            // Transform unit: decode residual and apply reconstruction
            TransformUnit(ref cabac, sliceHeader, sps, pps,
                x0, y0, xBase, yBase, log2CbSize, log2TrafoSize,
                blkIdx, cbfLuma, cbfCb0, cbfCb1, cbfCr0, cbfCr1, residual);

            // Mark deblocking BS at this leaf TU's boundaries (matches FFmpeg's
            // ff_hevc_deblocking_boundary_strengths called per leaf TU)
            FillDeblockingInfoTU(x0, y0, log2TrafoSize,
                currentPredMode == HevcPredictionMode.Intra, cbfLuma);

            // Mark deblocking bypass at TU level for transquant_bypass CUs
            // (matches FFmpeg hevcdec.c line 1651: set_deblocking_bypass after BS calc)
            if (currentCuTransquantBypass && context.ActivePps is { TransquantBypassEnabled: true })
                SetDeblockingBypass(x0, y0, log2TrafoSize);
        }
    }

    /// <summary>
    /// Decodes a transform unit (leaf of transform tree).
    /// Decodes residual coefficients, applies inverse transform and reconstruction.
    /// </summary>
    private void TransformUnit(ref HevcCabacDecoder cabac, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int xBase, int yBase,
        int log2CbSize, int log2TrafoSize,
        int blkIdx, bool cbfLuma, bool cbfCb0, bool cbfCb1, bool cbfCr0, bool cbfCr1, Span<short> residual)
    {
        if (currentFrameBuffer == null) return;

        int trafoSize = 1 << log2TrafoSize;
        bool is422 = sps.ChromaFormatIdc == HevcChromaFormat.Chroma422;

        // Use TU-level intra pred mode set by TransformTree (handles NxN depth correctly)
        int intraPredMode = currentTuIntraPredMode;

        // Pre-compute cross-component prediction flag for saving luma residual
        // FFmpeg hevcdec.c:1393-1395: must know this before luma IDCT to save residual
        bool crossPf = pps.CrossComponentPredictionEnabled && cbfLuma &&
            (currentPredMode == HevcPredictionMode.Inter || currentTuRawChromaMode == 4);

        // Perform intra prediction for luma
        if (currentPredMode == HevcPredictionMode.Intra)
        {
            PerformIntraPrediction(sps, pps.ConstrainedIntraPred, x0, y0, log2TrafoSize, intraPredMode, 0);
        }

        DiagTotalTuCount++;

        // Log cbf decisions for diagnostic
        if (DiagCbfLog != null)
        {
            int nzCount = 0; // will be updated after decode if cbfLuma
            DiagCbfLog.Add((x0, y0, trafoSize, cbfLuma, nzCount));
        }

        // Decode CU QP delta if any CBF is non-zero
        // Must happen BEFORE residual coding — the CABAC bins are in this order
        // Matches FFmpeg's hls_transform_unit: cu_qp_delta comes before residual_coding
        bool anyCbf = cbfLuma || cbfCb0 || cbfCr0 || (is422 && (cbfCb1 || cbfCr1));
        if (anyCbf)
        {
            if (pps.CuQpDeltaEnabled && !isCuQpDeltaCoded)
            {
                cuQpDelta = DecodeCuQpDeltaAbs(ref cabac);
                if (cuQpDelta != 0)
                {
                    if (cabac.DecodeBypass() != 0)
                        cuQpDelta = -cuQpDelta;
                }
                isCuQpDeltaCoded = true;

                // Apply QP using CU base position (matches FFmpeg's cb_xBase/cb_yBase)
                SetQPy(sps, pps, currentCuBaseX, currentCuBaseY, log2CbSize);
                DiagQpDeltaLog?.Add((currentCuBaseX, currentCuBaseY, cuQpDelta, qpY, qpYPred));
            }
        }

        // Decode per-CU chroma QP offset (RExt, FFmpeg hevcdec.c:1349-1366)
        bool cbfChroma = cbfCb0 || cbfCr0 || (is422 && (cbfCb1 || cbfCr1));
        if (sliceHeader.CuChromaQpOffsetEnabled && cbfChroma &&
            !currentCuTransquantBypass && !isCuChromaQpOffsetCoded)
        {
            int cuChromaQpOffsetFlag = cabac.DecodeBin(HevcCabacContextIndex.CuChromaQpOffsetFlag);
            if (cuChromaQpOffsetFlag != 0)
            {
                int cuChromaQpOffsetIdx = 0;
                int listLenMinus1 = pps.ChromaQpOffsetListLen - 1;
                if (listLenMinus1 > 0)
                {
                    // Truncated unary decode (FFmpeg cabac.c:623-631)
                    int cMax = Math.Max(5, listLenMinus1);
                    int i = 0;
                    while (i < cMax && cabac.DecodeBin(HevcCabacContextIndex.CuChromaQpOffsetIdx) != 0)
                        i++;
                    cuChromaQpOffsetIdx = i;
                }
                cuQpOffsetCb = pps.CbQpOffset[cuChromaQpOffsetIdx];
                cuQpOffsetCr = pps.CrQpOffset[cuChromaQpOffsetIdx];
            }
            else
            {
                cuQpOffsetCb = 0;
                cuQpOffsetCr = 0;
            }
            isCuChromaQpOffsetCoded = true;
        }

        // Decode and apply luma residual
        if (cbfLuma)
        {
            DiagCbfLumaCount++;

            // Mark cbf_luma in per-4x4 field for deblocking BS derivation
            if (cbfLumaField != null)
            {
                int x4Start = x0 >> 2;
                int y4Start = y0 >> 2;
                int w4 = trafoSize >> 2;
                int h4 = trafoSize >> 2;
                for (int y4 = 0; y4 < h4; y4++)
                {
                    int rowIdx = (y4Start + y4) * puWidthIn4 + x4Start;
                    for (int x4 = 0; x4 < w4; x4++)
                        cbfLumaField[rowIdx + x4] = 1;
                }
            }

            // Scan index: only non-diagonal for intra (matches FFmpeg: scan_idx = SCAN_DIAG for inter)
            int scanIdx = currentPredMode == HevcPredictionMode.Intra
                ? DeriveIntraScanIdx(intraPredMode, log2TrafoSize) : 0;
            bool lumaTransformSkip = DecodeResidualCoding(ref cabac, sps, pps, x0, y0, log2TrafoSize, scanIdx, 0, residual,
                out bool lumaExplicitRdpcm, out int lumaExplicitRdpcmDir);

            // Count non-zero coefficients for diagnostics
            int tuArea2 = trafoSize * trafoSize;
            int nzLocal = 0;
            for (int di = 0; di < tuArea2; di++)
                if (residual[di] != 0) { DiagNonZeroCoeffCount++; nzLocal++; }

            // Update cbf log with non-zero count
            if (DiagCbfLog != null && DiagCbfLog.Count > 0)
            {
                var last = DiagCbfLog[^1];
                if (last.X == x0 && last.Y == y0)
                    DiagCbfLog[^1] = (last.X, last.Y, last.Size, last.CbfLuma, nzLocal);
            }

            // Apply inverse transform and add to prediction
            // DIAG: skip inter residual if DiagSkipInterResidual is set
            if (!(currentPredMode != HevcPredictionMode.Intra && DiagSkipInterResidual))
            {
                ApplyInverseTransformAndReconstruct(sps, x0, y0, log2TrafoSize, 0,
                    intraPredMode, lumaTransformSkip, lumaExplicitRdpcm, lumaExplicitRdpcmDir, residual,
                    saveLumaResidual: crossPf);
            }
        }

        // Chroma processing
        if (sps.ChromaFormatIdc != HevcChromaFormat.Monochrome)
        {
            // For 444, chroma TU size = luma TU size. For 420/422, chroma TU is horizontally halved.
            int log2TrafoSizeC = sps.ChromaFormatIdc == HevcChromaFormat.Chroma444
                ? log2TrafoSize
                : log2TrafoSize - 1;
            if (log2TrafoSizeC < 2) log2TrafoSizeC = 2; // Minimum 4x4

            // For 420/422, chroma is processed at the level where log2_trafo_size > 2,
            // or at blkIdx==3 when log2_trafo_size == 2 (aggregates 4 2x2 chroma into one 4x4).
            // For 444, always process chroma at the same level as luma.
            bool processChroma = sps.ChromaFormatIdc == HevcChromaFormat.Chroma444
                || log2TrafoSize > 2 || blkIdx == 3;

            if (processChroma)
            {
                // For 444, always use current position. For 420/422, use base position for 4x4 aggregation.
                int xC = (sps.ChromaFormatIdc == HevcChromaFormat.Chroma444 || log2TrafoSize > 2) ? x0 : xBase;
                int yC = (sps.ChromaFormatIdc == HevcChromaFormat.Chroma444 || log2TrafoSize > 2) ? y0 : yBase;
                int chromaPredMode = currentTuIntraPredModeC;

                // Number of chroma sub-blocks: 2 for 422 (top and bottom halves), 1 for 420/444
                int chromaLoopCount = is422 ? 2 : 1;
                int chromaBlockStep = 1 << log2TrafoSizeC;

                // Decode cross-component prediction for Cb (idx=0)
                // FFmpeg hevcdec.c:1397-1398: decoded ONCE before the Cb loop
                int resScaleValCb = 0;
                if (crossPf)
                    resScaleValCb = DecodeCrossComponentPred(ref cabac, 0);

                // Cb loop (1 iteration for 420/444, 2 for 422)
                for (int ci = 0; ci < chromaLoopCount; ci++)
                {
                    int yCi = yC + (ci * chromaBlockStep);
                    bool cbfCbI = ci == 0 ? cbfCb0 : cbfCb1;

                    if (currentPredMode == HevcPredictionMode.Intra)
                        PerformIntraPrediction(sps, pps.ConstrainedIntraPred, xC, yCi, log2TrafoSizeC, chromaPredMode, 1);
                    if (cbfCbI)
                    {
                        int scanIdxC = currentPredMode == HevcPredictionMode.Intra
                            ? DeriveIntraScanIdx(chromaPredMode, log2TrafoSize) : 0;
                        bool cbTransformSkip = DecodeResidualCoding(ref cabac, sps, pps, xC, yCi, log2TrafoSizeC, scanIdxC, 1, residual,
                            out bool cbExplicitRdpcm, out int cbExplicitRdpcmDir);
                        if (!(currentPredMode != HevcPredictionMode.Intra && DiagSkipInterResidual))
                        {
                            ApplyInverseTransformAndReconstruct(sps, xC, yCi, log2TrafoSizeC, 1,
                                chromaPredMode, cbTransformSkip, cbExplicitRdpcm, cbExplicitRdpcmDir, residual,
                                ccpResScaleVal: resScaleValCb);
                        }
                    }
                    else if (crossPf)
                    {
                        ApplyCrossComponentPredOnly(sps, xC, yCi, log2TrafoSizeC, 1, resScaleValCb);
                    }
                }

                // Decode cross-component prediction for Cr (idx=1)
                // FFmpeg hevcdec.c:1427-1428: decoded ONCE before the Cr loop
                int resScaleValCr = 0;
                if (crossPf)
                    resScaleValCr = DecodeCrossComponentPred(ref cabac, 1);

                // Cr loop (1 iteration for 420/444, 2 for 422)
                for (int ci = 0; ci < chromaLoopCount; ci++)
                {
                    int yCi = yC + (ci * chromaBlockStep);
                    bool cbfCrI = ci == 0 ? cbfCr0 : cbfCr1;

                    if (currentPredMode == HevcPredictionMode.Intra)
                        PerformIntraPrediction(sps, pps.ConstrainedIntraPred, xC, yCi, log2TrafoSizeC, chromaPredMode, 2);
                    if (cbfCrI)
                    {
                        int scanIdxC = currentPredMode == HevcPredictionMode.Intra
                            ? DeriveIntraScanIdx(chromaPredMode, log2TrafoSize) : 0;
                        bool crTransformSkip = DecodeResidualCoding(ref cabac, sps, pps, xC, yCi, log2TrafoSizeC, scanIdxC, 2, residual,
                            out bool crExplicitRdpcm, out int crExplicitRdpcmDir);
                        if (!(currentPredMode != HevcPredictionMode.Intra && DiagSkipInterResidual))
                        {
                            ApplyInverseTransformAndReconstruct(sps, xC, yCi, log2TrafoSizeC, 2,
                                chromaPredMode, crTransformSkip, crExplicitRdpcm, crExplicitRdpcmDir, residual,
                                ccpResScaleVal: resScaleValCr);
                        }
                    }
                    else if (crossPf)
                    {
                        ApplyCrossComponentPredOnly(sps, xC, yCi, log2TrafoSizeC, 2, resScaleValCr);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Derives the scan index from the intra prediction mode and transform size.
    /// </summary>
    private static int DeriveIntraScanIdx(int intraPredMode, int log2TrafoSize)
    {
        // SCAN_DIAG = 0, SCAN_HORIZ = 1, SCAN_VERT = 2
        if (log2TrafoSize == 2 || (log2TrafoSize == 3 && intraPredMode >= 0))
        {
            if (intraPredMode >= 6 && intraPredMode <= 14)
                return 2; // SCAN_VERT
            if (intraPredMode >= 22 && intraPredMode <= 30)
                return 1; // SCAN_HORIZ
        }
        return 0; // SCAN_DIAG
    }
}
