// HEVC Inter Prediction — Merge mode, AMVP, motion compensation
// Reference: ITU-T H.265 Section 8.5.3, FFmpeg hevc/mvs.c + hevc/hevcdec.c + hevc/cabac.c

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Hevc;

internal sealed partial class HevcDecoder
{
    // Prediction flag constants (matching ffmpeg PF_*)
    private const byte PredFlagIntra = 0;
    private const byte PredFlagL0 = 1;
    private const byte PredFlagL1 = 2;
    private const byte PredFlagBi = 3;

    /// <summary>
    /// Motion vector field entry for merge candidate list.
    /// Matches ffmpeg's MvField struct.
    /// </summary>
    private struct MvFieldEntry
    {
        public short MvL0X, MvL0Y;
        public short MvL1X, MvL1Y;
        public sbyte RefIdxL0, RefIdxL1;
        public byte PredFlag;
    }

    /// <summary>
    /// Converts a luma-space MV component to chroma-space eighth-pel precision
    /// for use with chroma MC functions (which expect mvX >> 3 = integer, mvX &amp; 7 = fraction).
    /// For 4:2:0 (shift=1): unchanged. For 4:4:4 (shift=0): quarter-pel → eighth-pel.
    /// Matches FFmpeg's chroma MV derivation in hevcdec.c chroma_mc_uni/bi.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LumaToChromaMv(int lumaMv, int chromaShift)
    {
        int intPart = lumaMv >> (2 + chromaShift);
        int fracPart = (lumaMv & ((1 << (2 + chromaShift)) - 1)) << (1 - chromaShift);
        return (intPart << 3) | fracPart;
    }

    // ─────────────────────────────────────────────────────
    // CABAC syntax element decoding for inter prediction
    // ─────────────────────────────────────────────────────

    /// <summary>Decode merge_flag. FFmpeg: ff_hevc_merge_flag_decode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeMergeFlag(ref HevcCabacDecoder cabac)
    {
        return cabac.DecodeBin(HevcCabacContextIndex.MergeFlag);
    }

    /// <summary>
    /// Decode merge_idx (truncated unary, first bin context-coded, rest bypass).
    /// FFmpeg: ff_hevc_merge_idx_decode.
    /// </summary>
    private static int DecodeMergeIdx(ref HevcCabacDecoder cabac, int maxNumMergeCand)
    {
        int i = cabac.DecodeBin(HevcCabacContextIndex.MergeIdx);
        if (i != 0)
        {
            while (i < maxNumMergeCand - 1 && cabac.DecodeBypass() != 0)
                i++;
        }
        return i;
    }

    /// <summary>
    /// Decode inter_pred_idc (L0, L1, or BI).
    /// FFmpeg: ff_hevc_inter_pred_idc_decode.
    /// Returns: 1=PRED_L0, 2=PRED_L1, 3=PRED_BI.
    /// Spec Table 9-35: second bin 0→PRED_L0, 1→PRED_L1.
    /// </summary>
    private int DecodeInterPredIdc(ref HevcCabacDecoder cabac, int nPbW, int nPbH)
    {
        if (nPbW + nPbH == 12)
            return PredFlagL0 + cabac.DecodeBin(HevcCabacContextIndex.InterPredIdc + 4);

        if (cabac.DecodeBin(HevcCabacContextIndex.InterPredIdc + currentCtDepth) != 0)
            return PredFlagBi; // PRED_BI

        return PredFlagL0 + cabac.DecodeBin(HevcCabacContextIndex.InterPredIdc + 4);
    }

    /// <summary>
    /// Decode ref_idx_lx (truncated unary, first 2 bins context-coded, rest bypass).
    /// FFmpeg: ff_hevc_ref_idx_lx_decode. Uses REF_IDX_L0 context offsets for both L0/L1.
    /// </summary>
    private static int DecodeRefIdxLx(ref HevcCabacDecoder cabac, int numRefIdxLx)
    {
        int i = 0;
        int max = numRefIdxLx - 1;
        int maxCtx = Math.Min(max, 2);

        while (i < maxCtx && cabac.DecodeBin(HevcCabacContextIndex.RefIdxL0 + i) != 0)
            i++;

        if (i == 2)
        {
            while (i < max && cabac.DecodeBypass() != 0)
                i++;
        }

        return i;
    }

    /// <summary>
    /// Decode motion vector difference (mvd_coding).
    /// FFmpeg: ff_hevc_hls_mvd_coding + abs_mvd_greater0_flag_decode etc.
    /// </summary>
    private static (short x, short y) DecodeMvd(ref HevcCabacDecoder cabac)
    {
        // Match FFmpeg ff_hevc_hls_mvd_coding decode order exactly:
        // 1. All greater0 flags, 2. All greater1 flags, 3. Remainders+signs

        // Step 1: greater0 flags for both components
        int x = cabac.DecodeBin(HevcCabacContextIndex.AbsMvdGreater0Flag);
        int y = cabac.DecodeBin(HevcCabacContextIndex.AbsMvdGreater0Flag);

        // Step 2: greater1 flags (context offset +1) for both components
        if (x != 0)
            x += cabac.DecodeBin(HevcCabacContextIndex.AbsMvdGreater1Flag + 1);
        if (y != 0)
            y += cabac.DecodeBin(HevcCabacContextIndex.AbsMvdGreater1Flag + 1);

        // Step 3: remainder + sign for X, then Y
        // x/y == 0: mvd=0, x/y == 1: abs=1 decode sign only, x/y == 2: abs>=2 decode remainder+sign
        short mvdX = x switch
        {
            2 => DecodeMvdWithSign(ref cabac),
            1 => cabac.DecodeBypass() != 0 ? (short)-1 : (short)1,
            _ => 0
        };

        short mvdY = y switch
        {
            2 => DecodeMvdWithSign(ref cabac),
            1 => cabac.DecodeBypass() != 0 ? (short)-1 : (short)1,
            _ => 0
        };

        return (mvdX, mvdY);
    }

    /// <summary>
    /// Decode abs_mvd_minus2 + sign in one call (matches FFmpeg's mvd_decode).
    /// Returns signed MVD value with magnitude >= 2.
    /// </summary>
    private static short DecodeMvdWithSign(ref HevcCabacDecoder cabac)
    {
        int abs = DecodeMvdRemainder(ref cabac) + 2;
        return cabac.DecodeBypass() != 0 ? (short)-abs : (short)abs;
    }

    /// <summary>
    /// Decode abs_mvd_minus2 using exp-golomb bypass (k-th order, k=1).
    /// FFmpeg: mvd_decode.
    /// </summary>
    private static int DecodeMvdRemainder(ref HevcCabacDecoder cabac)
    {
        int ret = 2;
        int k = 1;
        const int cabacMaxBin = 31;

        while (k < cabacMaxBin && cabac.DecodeBypass() != 0)
        {
            ret += 1 << k;
            k++;
        }

        while (--k >= 0)
            ret += cabac.DecodeBypass() << k;

        // Subtract the initial 2 because caller adds it back
        return ret - 2;
    }

    /// <summary>Decode mvp_lx_flag. FFmpeg: ff_hevc_mvp_lx_flag_decode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeMvpLxFlag(ref HevcCabacDecoder cabac)
    {
        return cabac.DecodeBin(HevcCabacContextIndex.MvpLxFlag);
    }

    /// <summary>Decode no_residual_data_flag (rqt_root_cbf for inter). FFmpeg: ff_hevc_no_residual_syntax_flag_decode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeNoResidualDataFlag(ref HevcCabacDecoder cabac)
    {
        return cabac.DecodeBin(HevcCabacContextIndex.RqtRootCbf);
    }

    // ─────────────────────────────────────────────────────
    // Merge candidate derivation (Section 8.5.3.1.2)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets MvField at a specific luma sample position from the per-PU field.
    /// Returns an entry with PredFlag=0 (INTRA) if out of bounds or not yet stored.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MvFieldEntry GetMvFieldAt(int x, int y)
    {
        int x4 = x >> 2;
        int y4 = y >> 2;
        if (x4 < 0 || y4 < 0 || x4 >= puWidthIn4 || y4 >= puHeightIn4)
            return default; // PredFlag = 0 = INTRA

        int idx = y4 * puWidthIn4 + x4;
        return new MvFieldEntry
        {
            MvL0X = mvFieldL0X![idx],
            MvL0Y = mvFieldL0Y![idx],
            MvL1X = mvFieldL1X![idx],
            MvL1Y = mvFieldL1Y![idx],
            RefIdxL0 = refIdxFieldL0![idx],
            RefIdxL1 = refIdxFieldL1![idx],
            PredFlag = predModeField![idx]
        };
    }

    /// <summary>
    /// Stores MvField for all 4×4 blocks covered by a prediction unit.
    /// </summary>
    private void StoreMvField(int x0, int y0, int nPbW, int nPbH, in MvFieldEntry mv)
    {
        if (mvFieldL0X == null) return;

        int x4Start = x0 >> 2;
        int y4Start = y0 >> 2;
        int w4 = nPbW >> 2;
        int h4 = nPbH >> 2;

        for (int y4 = 0; y4 < h4; y4++)
        {
            int rowIdx = (y4Start + y4) * puWidthIn4 + x4Start;
            for (int x4 = 0; x4 < w4; x4++)
            {
                int idx = rowIdx + x4;
                mvFieldL0X[idx] = mv.MvL0X;
                mvFieldL0Y[idx] = mv.MvL0Y;
                mvFieldL1X[idx] = mv.MvL1X;
                mvFieldL1Y[idx] = mv.MvL1Y;
                refIdxFieldL0[idx] = mv.RefIdxL0;
                refIdxFieldL1[idx] = mv.RefIdxL1;
                predModeField[idx] = mv.PredFlag;
            }
        }
    }

    /// <summary>
    /// Checks if two MvField entries have identical motion info.
    /// Matches ffmpeg's compare_mv_ref_idx.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareMvRefIdx(in MvFieldEntry a, in MvFieldEntry b)
    {
        if (a.PredFlag != b.PredFlag) return false;
        if (a.PredFlag == PredFlagBi)
            return a.RefIdxL0 == b.RefIdxL0 && a.MvL0X == b.MvL0X && a.MvL0Y == b.MvL0Y &&
                   a.RefIdxL1 == b.RefIdxL1 && a.MvL1X == b.MvL1X && a.MvL1Y == b.MvL1Y;
        if (a.PredFlag == PredFlagL0)
            return a.RefIdxL0 == b.RefIdxL0 && a.MvL0X == b.MvL0X && a.MvL0Y == b.MvL0Y;
        if (a.PredFlag == PredFlagL1)
            return a.RefIdxL1 == b.RefIdxL1 && a.MvL1X == b.MvL1X && a.MvL1Y == b.MvL1Y;
        return false;
    }

    /// <summary>
    /// Z-scan block availability check (Section 6.4.1).
    /// Matches ffmpeg's z_scan_block_avail.
    /// Returns true if block at (xN, yN) is available from position (xCurr, yCurr).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ZScanBlockAvailable(HevcSequenceParameterSet sps, int xCurr, int yCurr, int xN, int yN)
    {
        int log2CtbSize = sps.Log2CtbSizeY;
        int xCurrCtb = xCurr >> log2CtbSize;
        int yCurrCtb = yCurr >> log2CtbSize;
        int xNCtb = xN >> log2CtbSize;
        int yNCtb = yN >> log2CtbSize;

        // If neighbor is in a previous CTB (above or left), it's available
        if (yNCtb < yCurrCtb || xNCtb < xCurrCtb)
            return true;

        // Same CTB: check z-scan order using min TB address
        int log2MinTbSize = sps.Log2MinTbSizeY;
        int log2Diff = log2CtbSize - log2MinTbSize;
        int tbMask = (1 << log2Diff) - 1;

        int currX = (xCurr >> log2MinTbSize) & tbMask;
        int currY = (yCurr >> log2MinTbSize) & tbMask;
        int nX = (xN >> log2MinTbSize) & tbMask;
        int nY = (yN >> log2MinTbSize) & tbMask;

        int currAddr = ComputeZScanAddr(currX, currY, log2Diff);
        int nAddr = ComputeZScanAddr(nX, nY, log2Diff);

        return nAddr <= currAddr;
    }

    /// <summary>
    /// Derives spatial merge candidates and fills the merge candidate list.
    /// Matches ffmpeg's derive_spatial_merge_candidates.
    /// </summary>
    private void DeriveSpatialMergeCandidates(
        HevcSliceSegmentHeader slice, HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int nPbW, int nPbH,
        int log2CbSize, int partIdx, int mergeIdx,
        Span<MvFieldEntry> mergeCandList, out int nbMergeCand)
    {
        nbMergeCand = 0;
        int maxCand = slice.MaxNumMergeCand;
        
        // Spatial candidate positions
        int xA1 = x0 - 1;
        int yA1 = y0 + nPbH - 1;
        int xB1 = x0 + nPbW - 1;
        int yB1 = y0 - 1;
        int xB0 = x0 + nPbW;
        int yB0 = y0 - 1;
        int xA0 = x0 - 1;
        int yA0 = y0 + nPbH;
        int xB2 = x0 - 1;
        int yB2 = y0 - 1;

        int log2ParallelMergeLevel = pps.Log2ParallelMergeLevel;
        int frameW = sps.PictureWidthInLumaSamples;
        int frameH = sps.PictureHeightInLumaSamples;

        // Availability based on CTB boundaries
        // FFmpeg: ff_hevc_set_neighbour_available
        int x0b = x0 & (sps.CtbSizeY - 1);
        int y0b = y0 & (sps.CtbSizeY - 1);
        bool candLeft = ctbLeftFlag || x0b != 0;
        bool candUp = ctbUpFlag || y0b != 0;
        bool candUpLeft = (x0b != 0 || y0b != 0) ? (candLeft && candUp) : ctbUpLeftFlag;
        bool candUpRight = (x0b + nPbW == sps.CtbSizeY) ? (ctbUpRightFlag && y0b == 0) : candUp;
        // Clip by tile/WPP right edge — FFmpeg: cand_up_right &= (x0 + nPbW) < end_of_tiles_x
        candUpRight = candUpRight && (x0 + nPbW) < endOfTilesX;

        // Bottom-left: unavailable if PU extends past the current CTB row's bottom edge.
        // FFmpeg: cand_bottom_left = ((y0 + nPbH) >= end_of_tiles_y) ? 0 : cand_left
        // where end_of_tiles_y = min(y_ctb + ctb_size, height)
        int endOfTilesY = Math.Min(((y0 >> sps.Log2CtbSizeY) + 1) << sps.Log2CtbSizeY, frameH);
        bool candBottomLeft = (y0 + nPbH < endOfTilesY) ? candLeft : false;

        // Helper: check if neighbor is available and not intra
        MvFieldEntry mvA1 = default, mvB1 = default, mvB0 = default, mvA0 = default, mvB2 = default;
        bool availA1 = false, availB1 = false, availB0 = false, availA0 = false, availB2 = false;

        // A1: left (x0-1, y0+nPbH-1)
        bool isDiffMerA1 = IsDiffMer(pps, xA1, yA1, x0, y0);
        bool pruneA1 = !SingleMclFlag(pps, log2CbSize) && partIdx == 1 &&
            (currentPartMode == HevcPartitionMode.PartNx2N ||
             currentPartMode == HevcPartitionMode.PartnLx2N ||
             currentPartMode == HevcPartitionMode.PartnRx2N);
        if (!pruneA1 && !isDiffMerA1 && candLeft && xA1 >= 0 && yA1 >= 0 && yA1 < frameH)
        {
            mvA1 = GetMvFieldAt(xA1, yA1);
            availA1 = mvA1.PredFlag != PredFlagIntra;
        }

        if (availA1)
        {
            mergeCandList[nbMergeCand] = mvA1;
            if (mergeIdx == 0) { nbMergeCand = 1; return; }
            nbMergeCand++;
        }

        // B1: above (x0+nPbW-1, y0-1)
        bool pruneB1 = !SingleMclFlag(pps, log2CbSize) && partIdx == 1 &&
            (currentPartMode == HevcPartitionMode.Part2NxN ||
             currentPartMode == HevcPartitionMode.Part2NxnU ||
             currentPartMode == HevcPartitionMode.Part2NxnD);
        bool isDiffMerB1 = IsDiffMer(pps, xB1, yB1, x0, y0);
        if (!pruneB1 && !isDiffMerB1 && candUp && xB1 >= 0 && yB1 >= 0 && xB1 < frameW)
        {
            mvB1 = GetMvFieldAt(xB1, yB1);
            availB1 = mvB1.PredFlag != PredFlagIntra;
        }

        if (availB1 && !(availA1 && CompareMvRefIdx(mvB1, mvA1)))
        {
            mergeCandList[nbMergeCand] = mvB1;
            if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
            nbMergeCand++;
        }

        // B0: above-right (x0+nPbW, y0-1)
        // FFmpeg: is_available_b0 = AVAILABLE(cand_up_right, B0) && xB0 < width && PRED_BLOCK_AVAILABLE(B0) && !is_diff_mer
        bool isDiffMerB0 = IsDiffMer(pps, xB0, yB0, x0, y0);
        if (!isDiffMerB0 && candUpRight && xB0 >= 0 && xB0 < frameW && yB0 >= 0 &&
            ZScanBlockAvailable(sps, x0, y0, xB0, yB0))
        {
            mvB0 = GetMvFieldAt(xB0, yB0);
            availB0 = mvB0.PredFlag != PredFlagIntra;
        }

        if (availB0 && !(availB1 && CompareMvRefIdx(mvB0, mvB1)))
        {
            mergeCandList[nbMergeCand] = mvB0;
            if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
            nbMergeCand++;
        }

        // A0: below-left (x0-1, y0+nPbH)
        // FFmpeg: is_available_a0 = AVAILABLE(cand_bottom_left, A0) && yA0 < height && PRED_BLOCK_AVAILABLE(A0) && !is_diff_mer
        bool isDiffMerA0 = IsDiffMer(pps, xA0, yA0, x0, y0);
        if (!isDiffMerA0 && candBottomLeft && xA0 >= 0 && yA0 >= 0 && yA0 < frameH &&
            ZScanBlockAvailable(sps, x0, y0, xA0, yA0))
        {
            mvA0 = GetMvFieldAt(xA0, yA0);
            availA0 = mvA0.PredFlag != PredFlagIntra;
        }

        if (availA0 && !(availA1 && CompareMvRefIdx(mvA0, mvA1)))
        {
            mergeCandList[nbMergeCand] = mvA0;
            if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
            nbMergeCand++;
        }

        // B2: above-left (x0-1, y0-1) — only if fewer than 4 candidates so far
        bool isDiffMerB2 = IsDiffMer(pps, xB2, yB2, x0, y0);
        if (!isDiffMerB2 && candUpLeft && xB2 >= 0 && yB2 >= 0 && nbMergeCand < 4)
        {
            mvB2 = GetMvFieldAt(xB2, yB2);
            availB2 = mvB2.PredFlag != PredFlagIntra;
        }

        if (availB2 && !(availA1 && CompareMvRefIdx(mvB2, mvA1)) &&
            !(availB1 && CompareMvRefIdx(mvB2, mvB1)))
        {
            mergeCandList[nbMergeCand] = mvB2;
            if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
            nbMergeCand++;
        }

        // Temporal MVP candidate (Section 8.5.3.1.7)
        if (slice.SliceTemporalMvpEnabled && nbMergeCand < maxCand)
        {
            short mvL0ColX = 0, mvL0ColY = 0, mvL1ColX = 0, mvL1ColY = 0;
            bool availL0 = TemporalLumaMotionVector(sps, x0, y0, nPbW, nPbH, 0, 0,
                out mvL0ColX, out mvL0ColY);
            bool availL1 = slice.SliceType == HevcSliceType.BSlice &&
                TemporalLumaMotionVector(sps, x0, y0, nPbW, nPbH, 0, 1,
                out mvL1ColX, out mvL1ColY);

            if (availL0 || availL1)
            {
                var temporal = new MvFieldEntry
                {
                    PredFlag = (byte)((availL0 ? PredFlagL0 : 0) | (availL1 ? PredFlagL1 : 0)),
                    RefIdxL0 = 0,
                    RefIdxL1 = 0,
                    MvL0X = mvL0ColX,
                    MvL0Y = mvL0ColY,
                    MvL1X = mvL1ColX,
                    MvL1Y = mvL1ColY,
                };
                mergeCandList[nbMergeCand] = temporal;
                if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
                nbMergeCand++;
            }
        }

        int nbOrigMergeCand = nbMergeCand;

        // Combined bi-predictive merge candidates (B slices only, ffmpeg 8.5.3.1.2)
        if (slice.SliceType == HevcSliceType.BSlice && nbOrigMergeCand > 1 && nbMergeCand < maxCand)
        {
            ReadOnlySpan<byte> l0L1CandIdx = [
                0, 1, 1, 0, 0, 2, 2, 0, 1, 2, 2, 1,
                0, 3, 3, 0, 1, 3, 3, 1, 2, 3, 3, 2
            ];
            int maxComb = nbOrigMergeCand * (nbOrigMergeCand - 1);
            if (maxComb > 12) maxComb = 12;

            for (int combIdx = 0; combIdx < maxComb && nbMergeCand < maxCand; combIdx++)
            {
                int l0CandIdx = l0L1CandIdx[combIdx * 2];
                int l1CandIdx = l0L1CandIdx[combIdx * 2 + 1];
                var l0Cand = mergeCandList[l0CandIdx];
                var l1Cand = mergeCandList[l1CandIdx];

                if ((l0Cand.PredFlag & PredFlagL0) != 0 && (l1Cand.PredFlag & PredFlagL1) != 0)
                {
                    // Only add if L0 and L1 refer to different pictures or have different MVs
                    int l0RefDpb = (l0Cand.RefIdxL0 >= 0 && l0Cand.RefIdxL0 < numRefList0) ? refPicList0[l0Cand.RefIdxL0] : -1;
                    int l1RefDpb = (l1Cand.RefIdxL1 >= 0 && l1Cand.RefIdxL1 < numRefList1) ? refPicList1[l1Cand.RefIdxL1] : -1;
                    int l0Poc = (l0RefDpb >= 0 && l0RefDpb < dpbCount) ? dpb[l0RefDpb].Poc : int.MinValue;
                    int l1Poc = (l1RefDpb >= 0 && l1RefDpb < dpbCount) ? dpb[l1RefDpb].Poc : int.MaxValue;

                    if (l0Poc != l1Poc || l0Cand.MvL0X != l1Cand.MvL1X || l0Cand.MvL0Y != l1Cand.MvL1Y)
                    {
                        var combined = new MvFieldEntry
                        {
                            PredFlag = PredFlagBi,
                            RefIdxL0 = l0Cand.RefIdxL0,
                            RefIdxL1 = l1Cand.RefIdxL1,
                            MvL0X = l0Cand.MvL0X,
                            MvL0Y = l0Cand.MvL0Y,
                            MvL1X = l1Cand.MvL1X,
                            MvL1Y = l1Cand.MvL1Y,
                        };
                        mergeCandList[nbMergeCand] = combined;
                        if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
                        nbMergeCand++;
                    }
                }
            }
        }

        // Zero motion vector candidates to fill remaining slots
        int nbRefs = slice.SliceType == HevcSliceType.PSlice
            ? numRefList0
            : Math.Min(numRefList0, numRefList1);
        int zeroIdx = 0;

        while (nbMergeCand < maxCand)
        {
            var zeroMv = new MvFieldEntry
            {
                PredFlag = (byte)(PredFlagL0 | (slice.SliceType == HevcSliceType.BSlice ? PredFlagL1 : 0)),
                RefIdxL0 = (sbyte)(zeroIdx < nbRefs ? zeroIdx : 0),
                RefIdxL1 = (sbyte)(zeroIdx < nbRefs ? zeroIdx : 0),
            };

            mergeCandList[nbMergeCand] = zeroMv;
            if (mergeIdx == nbMergeCand) { nbMergeCand++; return; }
            nbMergeCand++;
            zeroIdx++;
        }
    }

    /// <summary>
    /// Checks if two positions are in the same parallel merge estimation region.
    /// FFmpeg: is_diff_mer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDiffMer(HevcPictureParameterSet pps, int xN, int yN, int xP, int yP)
    {
        int plevel = pps.Log2ParallelMergeLevel;
        return (xN >> plevel) == (xP >> plevel) && (yN >> plevel) == (yP >> plevel);
    }

    /// <summary>
    /// Checks singleMCLFlag condition for merge.
    /// FFmpeg: if (pps->log2_parallel_merge_level > 2 && nCS == 8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SingleMclFlag(HevcPictureParameterSet pps, int log2CbSize)
    {
        return pps.Log2ParallelMergeLevel > 2 && (1 << log2CbSize) == 8;
    }

    // ─────────────────────────────────────────────────────
    // Temporal MVP (Section 8.5.3.1.7, 8.5.3.1.8)
    // Matches ffmpeg temporal_luma_motion_vector + derive_temporal_colocated_mvs
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Scales a motion vector based on POC distance ratio.
    /// Matches ffmpeg's mv_scale.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (short x, short y) ScaleMv(short srcX, short srcY, int td, int tb)
    {
        td = Math.Clamp(td, -128, 127);
        tb = Math.Clamp(tb, -128, 127);
        int tx = (0x4000 + Math.Abs(td / 2)) / td;
        int scaleFactor = Math.Clamp((tb * tx + 32) >> 6, -4096, 4095);
        short outX = (short)Math.Clamp((scaleFactor * srcX + 127 + (scaleFactor * srcX < 0 ? 1 : 0)) >> 8, short.MinValue, short.MaxValue);
        short outY = (short)Math.Clamp((scaleFactor * srcY + 127 + (scaleFactor * srcY < 0 ? 1 : 0)) >> 8, short.MinValue, short.MaxValue);
        return (outX, outY);
    }

    /// <summary>
    /// Checks if collocated MV can be used and optionally scales it.
    /// Matches ffmpeg's check_mvset. Uses per-CTB ref list snapshot for the collocated position.
    /// </summary>
    private bool CheckMvSet(
        out short mvOutX, out short mvOutY,
        short mvColX, short mvColY,
        int colPic, int currentPocVal,
        int listX, int refIdxLx,
        SliceRefListSnapshot? colRefList, int listCol, int refIdxCol)
    {
        mvOutX = 0;
        mvOutY = 0;

        // FFmpeg check_mvset: check long-term mismatch (cur_lt != col_lt → return 0)
        bool curLt = (listX == 0)
            ? (refIdxLx >= 0 && refIdxLx < numRefList0 && refIsLongTerm0[refIdxLx])
            : (refIdxLx >= 0 && refIdxLx < numRefList1 && refIsLongTerm1[refIdxLx]);
        bool colLt;
        if (colRefList == null)
        {
            colLt = false;
        }
        else if (listCol == 0)
            colLt = refIdxCol >= 0 && refIdxCol < colRefList.CountL0 && colRefList.IsLtL0[refIdxCol];
        else
            colLt = refIdxCol >= 0 && refIdxCol < colRefList.CountL1 && colRefList.IsLtL1[refIdxCol];

        if (curLt != colLt)
            return false;

        // Get collocated frame's reference POC using the per-CTB ref list snapshot
        int colRefPoc;
        if (colRefList == null)
            return false;
        if (listCol == 0)
        {
            if (refIdxCol < 0 || refIdxCol >= colRefList.CountL0)
                return false;
            colRefPoc = colRefList.PocL0[refIdxCol];
        }
        else
        {
            if (refIdxCol < 0 || refIdxCol >= colRefList.CountL1)
                return false;
            colRefPoc = colRefList.PocL1[refIdxCol];
        }

        // Get current frame's reference POC
        int curRefPoc;
        if (listX == 0)
        {
            int dpbIdx = (refIdxLx >= 0 && refIdxLx < numRefList0) ? refPicList0[refIdxLx] : -1;
            curRefPoc = (dpbIdx >= 0 && dpbIdx < dpbCount) ? dpb[dpbIdx].Poc : currentPocVal;
        }
        else
        {
            int dpbIdx = (refIdxLx >= 0 && refIdxLx < numRefList1) ? refPicList1[refIdxLx] : -1;
            curRefPoc = (dpbIdx >= 0 && dpbIdx < dpbCount) ? dpb[dpbIdx].Poc : currentPocVal;
        }

        int colPocDiff = colPic - colRefPoc;
        int curPocDiff = currentPocVal - curRefPoc;

        // FFmpeg: if (cur_lt || col_poc_diff == cur_poc_diff || !col_poc_diff) → no scaling
        if (curLt || colPocDiff == curPocDiff || colPocDiff == 0)
        {
            mvOutX = mvColX;
            mvOutY = mvColY;
        }
        else
        {
            (mvOutX, mvOutY) = ScaleMv(mvColX, mvColY, colPocDiff, curPocDiff);
        }
        return true;
    }

    /// <summary>
    /// Derives temporal colocated motion vectors for a specific list.
    /// Matches ffmpeg's derive_temporal_colocated_mvs.
    /// colRefList is the per-CTB ref list snapshot at the collocated position.
    /// </summary>
    private bool DeriveTemporalColocatedMvs(
        int colPic,
        byte colPredFlag, short colMvL0X, short colMvL0Y, short colMvL1X, short colMvL1Y,
        sbyte colRefIdxL0, sbyte colRefIdxL1,
        int refIdxLx, int listX,
        SliceRefListSnapshot? colRefList,
        out short mvOutX, out short mvOutY)
    {
        mvOutX = 0;
        mvOutY = 0;

        if (colPredFlag == PredFlagIntra)
            return false;

        if ((colPredFlag & PredFlagL0) == 0)
        {
            // Only L1 available
            return CheckMvSet(out mvOutX, out mvOutY, colMvL1X, colMvL1Y,
                colPic, currentPoc, listX, refIdxLx, colRefList, 1, colRefIdxL1);
        }
        else if (colPredFlag == PredFlagL0)
        {
            // Only L0 available
            return CheckMvSet(out mvOutX, out mvOutY, colMvL0X, colMvL0Y,
                colPic, currentPoc, listX, refIdxLx, colRefList, 0, colRefIdxL0);
        }
        else // PF_BI
        {
            // BI: check if any reference has POC > current POC
            bool hasFutureRef = false;
            for (int j = 0; j < 2 && !hasFutureRef; j++)
            {
                int[] list = j == 0 ? refPicList0 : refPicList1;
                int count = j == 0 ? numRefList0 : numRefList1;
                for (int i = 0; i < count; i++)
                {
                    if (list[i] >= 0 && list[i] < dpbCount && dpb[list[i]].Poc > currentPoc)
                    {
                        hasFutureRef = true;
                        break;
                    }
                }
            }

            if (!hasFutureRef)
            {
                // No future refs: use same-list MV
                if (listX == 0)
                    return CheckMvSet(out mvOutX, out mvOutY, colMvL0X, colMvL0Y,
                        colPic, currentPoc, listX, refIdxLx, colRefList, 0, colRefIdxL0);
                else
                    return CheckMvSet(out mvOutX, out mvOutY, colMvL1X, colMvL1Y,
                        colPic, currentPoc, listX, refIdxLx, colRefList, 1, colRefIdxL1);
            }
            else
            {
                // Has future refs: use opposite of collocated_list (spec 8.5.3.2.9)
                bool colFromL0 = currentSliceHeader?.CollocatedFromL0Flag ?? true;
                if (colFromL0) // collocated_list == L0 → use L1
                    return CheckMvSet(out mvOutX, out mvOutY, colMvL1X, colMvL1Y,
                        colPic, currentPoc, listX, refIdxLx, colRefList, 1, colRefIdxL1);
                else // collocated_list == L1 → use L0
                    return CheckMvSet(out mvOutX, out mvOutY, colMvL0X, colMvL0Y,
                        colPic, currentPoc, listX, refIdxLx, colRefList, 0, colRefIdxL0);
            }
        }
    }

    /// <summary>
    /// Temporal luma motion vector prediction.
    /// Matches ffmpeg's temporal_luma_motion_vector (Section 8.5.3.1.7).
    /// Checks bottom-right first, then center of collocated PU.
    /// </summary>
    private bool TemporalLumaMotionVector(
        HevcSequenceParameterSet sps,
        int x0, int y0, int nPbW, int nPbH,
        int refIdxLx, int listX,
        out short mvColX, out short mvColY)
    {
        mvColX = 0;
        mvColY = 0;

        if (collocatedRefDpbIdx < 0 || collocatedRefDpbIdx >= dpbCount)
            return false;

        ref readonly var colFrame = ref dpb[collocatedRefDpbIdx];
        if (colFrame.MvL0X == null || colFrame.PuWidthIn4 == 0)
            return false;

        int colPic = colFrame.Poc;
        int colPuW4 = colFrame.PuWidthIn4;
        int colPuH4 = colFrame.PuHeightIn4;
        int frameW = sps.PictureWidthInLumaSamples;
        int frameH = sps.PictureHeightInLumaSamples;
        int picWidthInCtbs = (frameW + sps.CtbSizeY - 1) / sps.CtbSizeY;

        // Bottom-right collocated position
        int x = x0 + nPbW;
        int y = y0 + nPbH;

        // Must be in same CTB row as current PU top-left, and within frame bounds
        if ((y0 >> sps.Log2CtbSizeY) == (y >> sps.Log2CtbSizeY) && y < frameH && x < frameW)
        {
            // Align to 16-pixel grid (matching ffmpeg: x &= ~15, y &= ~15)
            x &= ~15;
            y &= ~15;
            int x4 = x >> 2;
            int y4 = y >> 2;

            if (x4 >= 0 && x4 < colPuW4 && y4 >= 0 && y4 < colPuH4)
            {
                int idx = y4 * colPuW4 + x4;
                byte colPred = colFrame.PredFlags![idx];

                // Look up per-CTB ref list for this collocated position
                var colRefList = GetCollocatedRefList(colFrame, x, y, sps.Log2CtbSizeY, picWidthInCtbs);

                if (DeriveTemporalColocatedMvs(
                        colPic,
                        colPred,
                        colFrame.MvL0X[idx], colFrame.MvL0Y![idx],
                        colFrame.MvL1X![idx], colFrame.MvL1Y![idx],
                        colFrame.RefIdxL0![idx], colFrame.RefIdxL1![idx],
                        refIdxLx, listX,
                        colRefList,
                        out mvColX, out mvColY))
                    return true;
            }
        }

        // Center collocated position (fallback)
        x = x0 + (nPbW >> 1);
        y = y0 + (nPbH >> 1);
        x &= ~15;
        y &= ~15;
        int cx4 = x >> 2;
        int cy4 = y >> 2;

        if (cx4 >= 0 && cx4 < colPuW4 && cy4 >= 0 && cy4 < colPuH4)
        {
            int idx = cy4 * colPuW4 + cx4;
            byte colPred = colFrame.PredFlags![idx];

            // Look up per-CTB ref list for center position
            var colRefList = GetCollocatedRefList(colFrame, x, y, sps.Log2CtbSizeY, picWidthInCtbs);

            if (DeriveTemporalColocatedMvs(
                    colPic,
                    colPred,
                    colFrame.MvL0X[idx], colFrame.MvL0Y![idx],
                    colFrame.MvL1X![idx], colFrame.MvL1Y![idx],
                    colFrame.RefIdxL0![idx], colFrame.RefIdxL1![idx],
                    refIdxLx, listX,
                    colRefList,
                    out mvColX, out mvColY))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the per-CTB ref list snapshot for a collocated position.
    /// Matches FFmpeg's ff_hevc_get_ref_list (refs.c:57-65).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SliceRefListSnapshot? GetCollocatedRefList(
        in HevcReferenceFrame colFrame, int x, int y, int log2CtbSize, int picWidthInCtbs)
    {
        if (colFrame.SliceRefListSnapshots == null || colFrame.PerCtbSliceRefIdx == null)
            return null;
        int ctbX = x >> log2CtbSize;
        int ctbY = y >> log2CtbSize;
        int ctbAddr = ctbY * picWidthInCtbs + ctbX;
        if (ctbAddr < 0 || ctbAddr >= colFrame.PerCtbSliceRefIdx.Length)
            return null;
        int sliceIdx = colFrame.PerCtbSliceRefIdx[ctbAddr];
        if (sliceIdx < 0 || sliceIdx >= colFrame.SliceRefListSnapshots.Length)
            return null;
        return colFrame.SliceRefListSnapshots[sliceIdx];
    }

    // ─────────────────────────────────────────────────────
    // Merge mode (Section 8.5.3.1.1)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Derives motion vectors for merge mode (skip or explicit merge).
    /// Matches ffmpeg's ff_hevc_luma_mv_merge_mode.
    /// </summary>
    private MvFieldEntry MergeModeDerive(
        HevcSliceSegmentHeader slice, HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int nPbW, int nPbH, int log2CbSize, int partIdx, int mergeIdx)
    {
        int nCS = 1 << log2CbSize;

        // Save original PU dimensions for the bi-pred demotion check
        int origPbW = nPbW;
        int origPbH = nPbH;

        // singleMCLFlag: when parallel merge level > 2 and CU is 8×8, treat entire CU
        // as one merge region — override PU coords/size to CU origin/size.
        // FFmpeg: ff_hevc_luma_mv_merge_mode (mvs.c:495-502)
        if (pps.Log2ParallelMergeLevel > 2 && nCS == 8)
        {
            x0 = x0 & ~(nCS - 1); // CU origin X
            y0 = y0 & ~(nCS - 1); // CU origin Y
            nPbW = nCS;
            nPbH = nCS;
            partIdx = 0;
        }

        Span<MvFieldEntry> mergeCandList = stackalloc MvFieldEntry[6]; // MRG_MAX_NUM_CANDS + 1
        DeriveSpatialMergeCandidates(slice, sps, pps, x0, y0, nPbW, nPbH,
            log2CbSize, partIdx, mergeIdx, mergeCandList, out _);

        var result = mergeCandList[mergeIdx];

        // If bi-pred and PU is 8×4 or 4×8, force L0 only (use original PU dimensions)
        if (result.PredFlag == PredFlagBi && (origPbW + origPbH) == 12)
            result.PredFlag = PredFlagL0;

        return result;
    }

    // ─────────────────────────────────────────────────────
    // AMVP mode (Section 8.5.3.1.6)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Derives motion vectors for AMVP (non-merge) inter mode.
    /// Matches ffmpeg's hls_prediction_unit AMVP path.
    /// </summary>
    private MvFieldEntry AmvpModeDerive(
        ref HevcCabacDecoder cabac,
        HevcSliceSegmentHeader slice, HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int nPbW, int nPbH, int log2CbSize, int partIdx, int mergeIdx)
    {
        var mv = new MvFieldEntry();

        byte interPredIdc = PredFlagL0;
        if (slice.SliceType == HevcSliceType.BSlice)
            interPredIdc = (byte)DecodeInterPredIdc(ref cabac, nPbW, nPbH);

        // L0
        if ((interPredIdc & PredFlagL0) != 0)
        {
            if (numRefList0 > 0)
                mv.RefIdxL0 = (sbyte)DecodeRefIdxLx(ref cabac, numRefList0);

            mv.PredFlag = PredFlagL0;

            var mvdL0 = DecodeMvd(ref cabac);
            int mvpFlagL0 = DecodeMvpLxFlag(ref cabac);

            var mvpL0 = LumaMvMvpMode(sps, pps, x0, y0, nPbW, nPbH, mv.RefIdxL0, 0, mvpFlagL0);

            mv.MvL0X = (short)(mvpL0.x + mvdL0.x);
            mv.MvL0Y = (short)(mvpL0.y + mvdL0.y);
        }

        // L1
        if ((interPredIdc & PredFlagL1) != 0)
        {
            if (numRefList1 > 0)
                mv.RefIdxL1 = (sbyte)DecodeRefIdxLx(ref cabac, numRefList1);

            short mvdL1X = 0, mvdL1Y = 0;
            if (slice.MvdL1ZeroFlag && interPredIdc == PredFlagBi)
            {
                // mvd_l1_zero_flag: zero MVD for L1 in bi-pred (no bitstream read)
            }
            else
            {
                var mvdL1 = DecodeMvd(ref cabac);
                mvdL1X = mvdL1.x;
                mvdL1Y = mvdL1.y;
            }

            // mvp_lx_flag is ALWAYS parsed, even with mvd_l1_zero_flag
            int mvpFlagL1 = DecodeMvpLxFlag(ref cabac);

            // MVP is ALWAYS computed
            var mvpL1 = LumaMvMvpMode(sps, pps, x0, y0, nPbW, nPbH, mv.RefIdxL1, 1, mvpFlagL1);

            mv.MvL1X = (short)(mvpL1.x + mvdL1X);
            mv.MvL1Y = (short)(mvpL1.y + mvdL1Y);
            mv.PredFlag |= PredFlagL1;
        }

        return mv;
    }

    /// <summary>
    /// Gets the reference picture POC for a given list and ref index.
    /// Returns int.MinValue if invalid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetRefPicPoc(int listIdx, int refIdx)
    {
        int[] list = listIdx == 0 ? refPicList0 : refPicList1;
        int count = listIdx == 0 ? numRefList0 : numRefList1;
        if (refIdx < 0 || refIdx >= count) return int.MinValue;
        int dpbIdx = list[refIdx];
        if (dpbIdx < 0 || dpbIdx >= dpbCount) return int.MinValue;
        return dpb[dpbIdx].Poc;
    }

    /// <summary>
    /// Exact ref-picture match for AMVP: checks if neighbor's MV references the same picture.
    /// Matches ffmpeg's mv_mp_mode_mx.
    /// Returns true and sets mv if match found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MvMpModeMx(in MvFieldEntry neighbor, int predFlagIndex, int refIdxCurr, int listCurr,
        out short mvX, out short mvY)
    {
        mvX = 0;
        mvY = 0;
        if ((neighbor.PredFlag & (1 << predFlagIndex)) == 0)
            return false;

        // Get neighbor's reference POC for the specified list
        int neighborRefIdx = predFlagIndex == 0 ? neighbor.RefIdxL0 : neighbor.RefIdxL1;
        int neighborRefPoc = GetRefPicPoc(predFlagIndex, neighborRefIdx);

        // Get current PU's reference POC
        int currentRefPoc = GetRefPicPoc(listCurr, refIdxCurr);

        if (neighborRefPoc == currentRefPoc && neighborRefPoc != int.MinValue)
        {
            if (predFlagIndex == 0)
            {
                mvX = neighbor.MvL0X;
                mvY = neighbor.MvL0Y;
            }
            else
            {
                mvX = neighbor.MvL1X;
                mvY = neighbor.MvL1Y;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Scaled ref-picture match for AMVP: accepts different ref pictures and scales MV.
    /// Matches ffmpeg's mv_mp_mode_mx_lt + dist_scale.
    /// </summary>
    private bool MvMpModeMxLt(in MvFieldEntry neighbor, int predFlagIndex, int refIdxCurr, int listCurr,
        out short mvX, out short mvY)
    {
        mvX = 0;
        mvY = 0;
        if ((neighbor.PredFlag & (1 << predFlagIndex)) == 0)
            return false;

        // FFmpeg mv_mp_mode_mx_lt: check long-term match
        bool currIsLongTerm = (listCurr == 0)
            ? (refIdxCurr >= 0 && refIdxCurr < numRefList0 && refIsLongTerm0[refIdxCurr])
            : (refIdxCurr >= 0 && refIdxCurr < numRefList1 && refIsLongTerm1[refIdxCurr]);

        int neighborRefIdx = predFlagIndex == 0 ? neighbor.RefIdxL0 : neighbor.RefIdxL1;
        bool colIsLongTerm = (predFlagIndex == 0)
            ? (neighborRefIdx >= 0 && neighborRefIdx < numRefList0 && refIsLongTerm0[neighborRefIdx])
            : (neighborRefIdx >= 0 && neighborRefIdx < numRefList1 && refIsLongTerm1[neighborRefIdx]);

        if (colIsLongTerm != currIsLongTerm)
            return false;

        short nbMvX = predFlagIndex == 0 ? neighbor.MvL0X : neighbor.MvL1X;
        short nbMvY = predFlagIndex == 0 ? neighbor.MvL0Y : neighbor.MvL1Y;

        mvX = nbMvX;
        mvY = nbMvY;

        // Only scale for short-term references
        if (!currIsLongTerm)
        {
            // Get neighbor's reference POC
            int neighborRefPoc = GetRefPicPoc(predFlagIndex, neighborRefIdx);
            // Get current PU's reference POC
            int currentRefPoc = GetRefPicPoc(listCurr, refIdxCurr);

            if (neighborRefPoc == int.MinValue || currentRefPoc == int.MinValue)
                return false;

            // Scale MV if ref pictures differ (dist_scale)
            if (neighborRefPoc != currentRefPoc)
            {
                int pocDiffNeighbor = currentPoc - neighborRefPoc;
                if (pocDiffNeighbor == 0) pocDiffNeighbor = 1;
                int pocDiffCurrent = currentPoc - currentRefPoc;
                (mvX, mvY) = ScaleMv(nbMvX, nbMvY, pocDiffNeighbor, pocDiffCurrent);
            }
        }
        return true;
    }

    /// <summary>
    /// Full AMVP candidate list derivation matching ffmpeg's ff_hevc_luma_mv_mvp_mode.
    /// Returns the selected MVP based on mvpFlag.
    /// </summary>
    private (short x, short y) LumaMvMvpMode(
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int nPbW, int nPbH, int refIdx, int listIdx, int mvpFlag)
    {
        int frameW = sps.PictureWidthInLumaSamples;
        int frameH = sps.PictureHeightInLumaSamples;

        // Neighbor availability (same as merge)
        int x0b = x0 & (sps.CtbSizeY - 1);
        int y0b = y0 & (sps.CtbSizeY - 1);
        bool candLeft = ctbLeftFlag || x0b != 0;
        bool candUp = ctbUpFlag || y0b != 0;
        bool candUpLeft = (x0b != 0 || y0b != 0) ? (candLeft && candUp) : ctbUpLeftFlag;
        bool candUpRightSap = (x0b + nPbW == sps.CtbSizeY) ? (ctbUpRightFlag && y0b == 0) : candUp;
        // Clip by tile/WPP right edge — FFmpeg: cand_up_right = cand_up_right_sap && (x0+nPbW) < end_of_tiles_x
        bool candUpRight = candUpRightSap && (x0 + nPbW) < endOfTilesX;

        // Bottom-left: unavailable if PU extends past the current CTB row's bottom edge.
        int endOfTilesY = Math.Min(((y0 >> sps.Log2CtbSizeY) + 1) << sps.Log2CtbSizeY, frameH);
        bool candBottomLeft = (y0 + nPbH < endOfTilesY) ? candLeft : false;

        int predFlagSameList = listIdx;      // 0 for L0, 1 for L1
        int predFlagCrossList = 1 - listIdx; // 1 for L0, 0 for L1

        (short x, short y) mxA = (0, 0);
        (short x, short y) mxB = (0, 0);
        bool availableFlagA = false;
        bool availableFlagB = false;
        bool isScaledFlagL0 = false;

        // ── A candidates (left group) ──
        int xA0 = x0 - 1, yA0 = y0 + nPbH;
        int xA1 = x0 - 1, yA1 = y0 + nPbH - 1;

        bool isAvailA0 = candBottomLeft && yA0 < frameH &&
            ZScanBlockAvailable(sps, x0, y0, xA0, yA0);
        bool isAvailA1 = candLeft;

        MvFieldEntry nbA0 = default, nbA1 = default;
        if (isAvailA0)
        {
            nbA0 = GetMvFieldAt(xA0, yA0);
            isAvailA0 = nbA0.PredFlag != PredFlagIntra;
        }
        if (isAvailA1)
        {
            nbA1 = GetMvFieldAt(xA1, yA1);
            isAvailA1 = nbA1.PredFlag != PredFlagIntra;
        }

        if (isAvailA0 || isAvailA1)
            isScaledFlagL0 = true;

        // First pass: exact ref match on A0, A1 (same list, then cross list)
        if (isAvailA0)
        {
            if (MvMpModeMx(nbA0, predFlagSameList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
            if (MvMpModeMx(nbA0, predFlagCrossList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
        }
        if (isAvailA1)
        {
            if (MvMpModeMx(nbA1, predFlagSameList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
            if (MvMpModeMx(nbA1, predFlagCrossList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
        }

        // Second pass: scaled match on A0, A1 (same list, then cross list)
        if (isAvailA0)
        {
            if (MvMpModeMxLt(nbA0, predFlagSameList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
            if (MvMpModeMxLt(nbA0, predFlagCrossList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
        }
        if (isAvailA1)
        {
            if (MvMpModeMxLt(nbA1, predFlagSameList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
            if (MvMpModeMxLt(nbA1, predFlagCrossList, refIdx, listIdx, out mxA.x, out mxA.y))
            { availableFlagA = true; goto bCandidates; }
        }

    bCandidates:
        // ── B candidates (above group) ──
        int xB0 = x0 + nPbW, yB0 = y0 - 1;
        int xB1 = x0 + nPbW - 1, yB1 = y0 - 1;
        int xB2 = x0 - 1, yB2 = y0 - 1;

        bool isAvailB0 = candUpRight && xB0 < frameW &&
            ZScanBlockAvailable(sps, x0, y0, xB0, yB0);
        bool isAvailB1 = candUp;
        bool isAvailB2 = candUpLeft;

        MvFieldEntry nbB0 = default, nbB1 = default, nbB2 = default;
        if (isAvailB0) { nbB0 = GetMvFieldAt(xB0, yB0); isAvailB0 = nbB0.PredFlag != PredFlagIntra; }
        if (isAvailB1) { nbB1 = GetMvFieldAt(xB1, yB1); isAvailB1 = nbB1.PredFlag != PredFlagIntra; }
        if (isAvailB2) { nbB2 = GetMvFieldAt(xB2, yB2); isAvailB2 = nbB2.PredFlag != PredFlagIntra; }

        // First pass: exact ref match on B0, B1, B2
        if (isAvailB0)
        {
            if (MvMpModeMx(nbB0, predFlagSameList, refIdx, listIdx, out mxB.x, out mxB.y))
            { availableFlagB = true; goto scaleF; }
            if (MvMpModeMx(nbB0, predFlagCrossList, refIdx, listIdx, out mxB.x, out mxB.y))
            { availableFlagB = true; goto scaleF; }
        }
        if (isAvailB1)
        {
            if (MvMpModeMx(nbB1, predFlagSameList, refIdx, listIdx, out mxB.x, out mxB.y))
            { availableFlagB = true; goto scaleF; }
            if (MvMpModeMx(nbB1, predFlagCrossList, refIdx, listIdx, out mxB.x, out mxB.y))
            { availableFlagB = true; goto scaleF; }
        }
        if (isAvailB2)
        {
            if (MvMpModeMx(nbB2, predFlagSameList, refIdx, listIdx, out mxB.x, out mxB.y))
            { availableFlagB = true; goto scaleF; }
            if (MvMpModeMx(nbB2, predFlagCrossList, refIdx, listIdx, out mxB.x, out mxB.y))
            { availableFlagB = true; goto scaleF; }
        }

    scaleF:
        // When no left candidate was available, promote B to A and try scaled B
        if (!isScaledFlagL0)
        {
            if (availableFlagB)
            {
                availableFlagA = true;
                mxA = mxB;
            }
            availableFlagB = false;

            // Scaled match on B0, B1, B2
            if (isAvailB0)
            {
                if (MvMpModeMxLt(nbB0, predFlagSameList, refIdx, listIdx, out mxB.x, out mxB.y))
                    availableFlagB = true;
                if (!availableFlagB && MvMpModeMxLt(nbB0, predFlagCrossList, refIdx, listIdx, out mxB.x, out mxB.y))
                    availableFlagB = true;
            }
            if (!availableFlagB && isAvailB1)
            {
                if (MvMpModeMxLt(nbB1, predFlagSameList, refIdx, listIdx, out mxB.x, out mxB.y))
                    availableFlagB = true;
                if (!availableFlagB && MvMpModeMxLt(nbB1, predFlagCrossList, refIdx, listIdx, out mxB.x, out mxB.y))
                    availableFlagB = true;
            }
            if (!availableFlagB && isAvailB2)
            {
                if (MvMpModeMxLt(nbB2, predFlagSameList, refIdx, listIdx, out mxB.x, out mxB.y))
                    availableFlagB = true;
                if (!availableFlagB && MvMpModeMxLt(nbB2, predFlagCrossList, refIdx, listIdx, out mxB.x, out mxB.y))
                    availableFlagB = true;
            }
        }

        // Build 2-candidate list
        int numMvpCand = 0;
        (short x, short y) mvpCand0 = (0, 0), mvpCand1 = (0, 0);

        if (availableFlagA)
        {
            mvpCand0 = mxA;
            numMvpCand++;
        }

        if (availableFlagB && (!availableFlagA || mxA.x != mxB.x || mxA.y != mxB.y))
        {
            if (numMvpCand == 0) mvpCand0 = mxB;
            else mvpCand1 = mxB;
            numMvpCand++;
        }

        // Temporal MVP candidate (if < 2 candidates and mvpFlag matches)
        if (numMvpCand < 2 && currentSliceHeader?.SliceTemporalMvpEnabled == true &&
            mvpFlag == numMvpCand)
        {
            if (TemporalLumaMotionVector(sps, x0, y0, nPbW, nPbH, refIdx, listIdx,
                out short mvColX, out short mvColY))
            {
                if (numMvpCand == 0) mvpCand0 = (mvColX, mvColY);
                else mvpCand1 = (mvColX, mvColY);
                numMvpCand++;
            }
        }

        return mvpFlag == 0 ? mvpCand0 : mvpCand1;
    }

    // ─────────────────────────────────────────────────────
    // Prediction unit decode (Section 7.3.8.6)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a prediction unit: merge/AMVP syntax + motion compensation.
    /// Matches ffmpeg's hls_prediction_unit.
    /// </summary>
    private void PredictionUnit(
        ref HevcCabacDecoder cabac,
        HevcSliceSegmentHeader slice, HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int x0, int y0, int nPbW, int nPbH, int log2CbSize, int partIdx,
        bool isSkip)
    {
        MvFieldEntry currentMv;

        if (isSkip)
        {
            // Skip mode: always merge, decode merge_idx
            lastPuMergeFlag = true;
            int mergeIdx = 0;
            if (slice.MaxNumMergeCand > 1)
                mergeIdx = DecodeMergeIdx(ref cabac, slice.MaxNumMergeCand);
            currentMv = MergeModeDerive(slice, sps, pps, x0, y0, nPbW, nPbH, log2CbSize, partIdx, mergeIdx);
        }
        else
        {
            // Non-skip inter: decode merge_flag
            int mergeFlag = DecodeMergeFlag(ref cabac);
            lastPuMergeFlag = mergeFlag != 0;
            if (mergeFlag != 0)
            {
                int mergeIdx = 0;
                if (slice.MaxNumMergeCand > 1)
                    mergeIdx = DecodeMergeIdx(ref cabac, slice.MaxNumMergeCand);
                currentMv = MergeModeDerive(slice, sps, pps, x0, y0, nPbW, nPbH, log2CbSize, partIdx, mergeIdx);
            }
            else
            {
                // AMVP mode
                currentMv = AmvpModeDerive(ref cabac, slice, sps, pps,
                    x0, y0, nPbW, nPbH, log2CbSize, partIdx, 0);
            }
        }

        // Store MV field for all 4×4 blocks in this PU
        StoreMvField(x0, y0, nPbW, nPbH, currentMv);

        // Perform motion compensation
        PerformMotionCompensation(sps, x0, y0, nPbW, nPbH, currentMv);
    }

    // Additional neighbor availability tracking (needed for merge candidate derivation)
    private bool ctbUpLeftFlag;
    private bool ctbUpRightFlag;

    // ─────────────────────────────────────────────────────
    // Motion compensation
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Performs motion compensation for a prediction unit.
    /// Writes predicted samples directly into currentFrameBuffer.
    /// </summary>
    private void PerformMotionCompensation(
        HevcSequenceParameterSet sps,
        int x0, int y0, int nPbW, int nPbH,
        in MvFieldEntry mv)
    {
        if (currentFrameBuffer == null) return;

        int bitDepth = sps.BitDepthLuma;
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int codedW = sps.PictureWidthInLumaSamples;
        int codedH = sps.PictureHeightInLumaSamples;
        int lumaPlaneSize = codedW * codedH * bytesPerSample;
        int chromaW = codedW >> sps.HShiftChroma;
        int chromaH = codedH >> sps.VShiftChroma;
        int chromaPlaneSize = chromaW * chromaH * bytesPerSample;

        // Check if weighted prediction is active
        var slice = currentSliceHeader;
        var pps = currentPps;
        bool weightedPred = slice != null && pps != null &&
            ((pps.WeightedPred && slice.SliceType == HevcSliceType.PSlice) ||
             (pps.WeightedBipred && slice.SliceType == HevcSliceType.BSlice));

        if (mv.PredFlag == PredFlagL0)
        {
            int refDpbIdx = mv.RefIdxL0 >= 0 && mv.RefIdxL0 < numRefList0
                ? refPicList0[mv.RefIdxL0] : -1;

            if (refDpbIdx >= 0 && refDpbIdx < dpbCount && dpb[refDpbIdx].Buffer != null)
            {
                if (weightedPred && mv.RefIdxL0 < slice!.LumaWeightL0.Length)
                {
                    MotionCompensateWeightedUniPred(sps, dpb[refDpbIdx].Buffer!,
                        x0, y0, nPbW, nPbH, mv.MvL0X, mv.MvL0Y,
                        slice.LumaWeightL0[mv.RefIdxL0], slice.LumaOffsetL0[mv.RefIdxL0],
                        slice.LumaLog2WeightDenom,
                        mv.RefIdxL0 < slice.ChromaWeightL0.GetLength(0) ? slice.ChromaWeightL0[mv.RefIdxL0, 0] : (short)(1 << slice.ChromaLog2WeightDenom),
                        mv.RefIdxL0 < slice.ChromaOffsetL0.GetLength(0) ? slice.ChromaOffsetL0[mv.RefIdxL0, 0] : (short)0,
                        mv.RefIdxL0 < slice.ChromaWeightL0.GetLength(0) ? slice.ChromaWeightL0[mv.RefIdxL0, 1] : (short)(1 << slice.ChromaLog2WeightDenom),
                        mv.RefIdxL0 < slice.ChromaOffsetL0.GetLength(0) ? slice.ChromaOffsetL0[mv.RefIdxL0, 1] : (short)0,
                        slice.ChromaLog2WeightDenom);
                }
                else
                {
                    MotionCompensateOnePlane(sps, dpb[refDpbIdx].Buffer!, x0, y0, nPbW, nPbH,
                        mv.MvL0X, mv.MvL0Y, isL1: false, isBiPred: false);
                }
            }
        }
        else if (mv.PredFlag == PredFlagL1)
        {
            int refDpbIdx = mv.RefIdxL1 >= 0 && mv.RefIdxL1 < numRefList1
                ? refPicList1[mv.RefIdxL1] : -1;

            if (refDpbIdx >= 0 && refDpbIdx < dpbCount && dpb[refDpbIdx].Buffer != null)
            {
                if (weightedPred && mv.RefIdxL1 < slice!.LumaWeightL1.Length)
                {
                    MotionCompensateWeightedUniPred(sps, dpb[refDpbIdx].Buffer!,
                        x0, y0, nPbW, nPbH, mv.MvL1X, mv.MvL1Y,
                        slice.LumaWeightL1[mv.RefIdxL1], slice.LumaOffsetL1[mv.RefIdxL1],
                        slice.LumaLog2WeightDenom,
                        mv.RefIdxL1 < slice.ChromaWeightL1.GetLength(0) ? slice.ChromaWeightL1[mv.RefIdxL1, 0] : (short)(1 << slice.ChromaLog2WeightDenom),
                        mv.RefIdxL1 < slice.ChromaOffsetL1.GetLength(0) ? slice.ChromaOffsetL1[mv.RefIdxL1, 0] : (short)0,
                        mv.RefIdxL1 < slice.ChromaWeightL1.GetLength(0) ? slice.ChromaWeightL1[mv.RefIdxL1, 1] : (short)(1 << slice.ChromaLog2WeightDenom),
                        mv.RefIdxL1 < slice.ChromaOffsetL1.GetLength(0) ? slice.ChromaOffsetL1[mv.RefIdxL1, 1] : (short)0,
                        slice.ChromaLog2WeightDenom);
                }
                else
                {
                    MotionCompensateOnePlane(sps, dpb[refDpbIdx].Buffer!, x0, y0, nPbW, nPbH,
                        mv.MvL1X, mv.MvL1Y, isL1: true, isBiPred: false);
                }
            }
        }
        else if (mv.PredFlag == PredFlagBi)
        {
            int refDpbIdx0 = mv.RefIdxL0 >= 0 && mv.RefIdxL0 < numRefList0
                ? refPicList0[mv.RefIdxL0] : -1;
            int refDpbIdx1 = mv.RefIdxL1 >= 0 && mv.RefIdxL1 < numRefList1
                ? refPicList1[mv.RefIdxL1] : -1;

            if (refDpbIdx0 >= 0 && refDpbIdx0 < dpbCount && dpb[refDpbIdx0].Buffer != null &&
                refDpbIdx1 >= 0 && refDpbIdx1 < dpbCount && dpb[refDpbIdx1].Buffer != null)
            {
                if (weightedPred &&
                    mv.RefIdxL0 < slice!.LumaWeightL0.Length &&
                    mv.RefIdxL1 < slice.LumaWeightL1.Length)
                {
                    MotionCompensateWeightedBiPred(sps,
                        dpb[refDpbIdx0].Buffer!, mv.MvL0X, mv.MvL0Y, mv.RefIdxL0,
                        dpb[refDpbIdx1].Buffer!, mv.MvL1X, mv.MvL1Y, mv.RefIdxL1,
                        x0, y0, nPbW, nPbH);
                }
                else
                {
                    MotionCompensateBiPredIntermediate(sps,
                        dpb[refDpbIdx0].Buffer!, mv.MvL0X, mv.MvL0Y,
                        dpb[refDpbIdx1].Buffer!, mv.MvL1X, mv.MvL1Y,
                        x0, y0, nPbW, nPbH);
                }
            }
        }
    }

    /// <summary>
    /// Performs motion compensation for one reference (L0 or L1) on luma and chroma.
    /// For uni-prediction, writes directly to frame. For bi-prediction L0, writes to frame.
    /// </summary>
    private void MotionCompensateOnePlane(
        HevcSequenceParameterSet sps, byte[] refBuffer,
        int x0, int y0, int nPbW, int nPbH,
        short mvX, short mvY, bool isL1, bool isBiPred)
    {
        int bitDepth = sps.BitDepthLuma;
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int codedW = sps.PictureWidthInLumaSamples;
        int codedH = sps.PictureHeightInLumaSamples;
        int lumaStride = codedW;
        int lumaPlaneSize = codedW * codedH * bytesPerSample;
        int chromaW = codedW >> sps.HShiftChroma;
        int chromaH = codedH >> sps.VShiftChroma;
        int chromaPlaneSize = chromaW * chromaH * bytesPerSample;

        if (bytesPerSample == 2)
        {
            // 10-bit path
            var dstLuma = MemoryMarshal.Cast<byte, ushort>(currentFrameBuffer.AsSpan(0, lumaPlaneSize));
            var refLuma = MemoryMarshal.Cast<byte, ushort>(refBuffer.AsSpan(0, lumaPlaneSize));

            // Temporary buffer for MC output
            Span<ushort> tempLuma = stackalloc ushort[nPbW * nPbH];

            HevcMotionCompensation.CompensateLumaHighBitDepth(
                refLuma, lumaStride, tempLuma, nPbW,
                nPbW, nPbH, mvX, mvY, x0, y0, codedW, codedH, bitDepth);

            // Write to frame buffer
            for (int row = 0; row < nPbH; row++)
            {
                int dstY = y0 + row;
                if (dstY >= codedH) break;
                for (int col = 0; col < nPbW; col++)
                {
                    int dstX = x0 + col;
                    if (dstX >= codedW) break;
                    dstLuma[dstY * lumaStride + dstX] = tempLuma[row * nPbW + col];
                }
            }

            // Chroma MC — convert luma MV to chroma eighth-pel (FFmpeg hevcdec.c:1889-1898)
            int chromaMvX = LumaToChromaMv(mvX, sps.HShiftChroma);
            int chromaMvY = LumaToChromaMv(mvY, sps.VShiftChroma);
            int nPbWC = nPbW >> sps.HShiftChroma;
            int nPbHC = nPbH >> sps.VShiftChroma;
            int x0C = x0 >> sps.HShiftChroma;
            int y0C = y0 >> sps.VShiftChroma;

            if (nPbWC > 0 && nPbHC > 0)
            {
                var refCb = MemoryMarshal.Cast<byte, ushort>(
                    refBuffer.AsSpan(lumaPlaneSize, chromaPlaneSize));
                var refCr = MemoryMarshal.Cast<byte, ushort>(
                    refBuffer.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize));

                Span<ushort> tempCb = stackalloc ushort[nPbWC * nPbHC];
                CompensateChromaHighBitDepth(refCb, chromaW, tempCb, nPbWC,
                    nPbWC, nPbHC, chromaMvX, chromaMvY, x0C, y0C, chromaW, chromaH, bitDepth);

                Span<ushort> tempCr = stackalloc ushort[nPbWC * nPbHC];
                CompensateChromaHighBitDepth(refCr, chromaW, tempCr, nPbWC,
                    nPbWC, nPbHC, chromaMvX, chromaMvY, x0C, y0C, chromaW, chromaH, bitDepth);

                // Write to correct position in frame buffer
                var dstCb = MemoryMarshal.Cast<byte, ushort>(
                    currentFrameBuffer.AsSpan(lumaPlaneSize, chromaPlaneSize));
                var dstCr = MemoryMarshal.Cast<byte, ushort>(
                    currentFrameBuffer.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize));

                for (int row = 0; row < nPbHC; row++)
                {
                    int dstY = y0C + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < nPbWC; col++)
                    {
                        int dstX = x0C + col;
                        if (dstX >= chromaW) break;
                        dstCb[dstY * chromaW + dstX] = tempCb[row * nPbWC + col];
                        dstCr[dstY * chromaW + dstX] = tempCr[row * nPbWC + col];
                    }
                }
            }
        }
        else
        {
            // 8-bit path
            var refLuma = refBuffer.AsSpan(0, lumaPlaneSize);

            Span<byte> tempLuma = stackalloc byte[nPbW * nPbH];
            HevcMotionCompensation.CompensateLuma(
                refLuma, codedW, tempLuma, nPbW,
                nPbW, nPbH, mvX, mvY, x0, y0, codedW, codedH);

            // Write to correct position in frame buffer
            for (int row = 0; row < nPbH; row++)
            {
                int dstY = y0 + row;
                if (dstY >= codedH) break;
                for (int col = 0; col < nPbW; col++)
                {
                    int dstX = x0 + col;
                    if (dstX >= codedW) break;
                    currentFrameBuffer![dstY * codedW + dstX] = tempLuma[row * nPbW + col];
                }
            }

            // Chroma — convert luma MV to chroma eighth-pel
            int chromaMvX = LumaToChromaMv(mvX, sps.HShiftChroma);
            int chromaMvY = LumaToChromaMv(mvY, sps.VShiftChroma);
            int nPbWC = nPbW >> sps.HShiftChroma;
            int nPbHC = nPbH >> sps.VShiftChroma;
            int x0C = x0 >> sps.HShiftChroma;
            int y0C = y0 >> sps.VShiftChroma;

            if (nPbWC > 0 && nPbHC > 0)
            {
                var refCb = refBuffer.AsSpan(lumaPlaneSize, chromaPlaneSize);
                var refCr = refBuffer.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize);

                Span<byte> tempCb = stackalloc byte[nPbWC * nPbHC];
                HevcMotionCompensation.CompensateChroma(
                    refCb, chromaW, tempCb, nPbWC,
                    nPbWC, nPbHC, chromaMvX, chromaMvY, x0C, y0C, chromaW, chromaH);

                Span<byte> tempCr = stackalloc byte[nPbWC * nPbHC];
                HevcMotionCompensation.CompensateChroma(
                    refCr, chromaW, tempCr, nPbWC,
                    nPbWC, nPbHC, chromaMvX, chromaMvY, x0C, y0C, chromaW, chromaH);

                // Write to correct position in frame buffer
                for (int row = 0; row < nPbHC; row++)
                {
                    int dstY = y0C + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < nPbWC; col++)
                    {
                        int dstX = x0C + col;
                        if (dstX >= chromaW) break;
                        currentFrameBuffer![lumaPlaneSize + dstY * chromaW + dstX] = tempCb[row * nPbWC + col];
                        currentFrameBuffer![lumaPlaneSize + chromaPlaneSize + dstY * chromaW + dstX] = tempCr[row * nPbWC + col];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Bi-prediction using intermediate ~14-bit precision for both L0 and L1.
    /// Averages at full precision before clipping, matching the HEVC spec and FFmpeg.
    /// Formula: clip((L0_intermediate + L1_intermediate + offset) >> shift)
    /// where shift = 14 - bitDepth + 1, offset = 1 << (14 - bitDepth).
    /// </summary>
    private void MotionCompensateBiPredIntermediate(
        HevcSequenceParameterSet sps,
        byte[] refBuffer0, short mvX0, short mvY0,
        byte[] refBuffer1, short mvX1, short mvY1,
        int x0, int y0, int nPbW, int nPbH)
    {
        int bitDepth = sps.BitDepthLuma;
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int codedW = sps.PictureWidthInLumaSamples;
        int codedH = sps.PictureHeightInLumaSamples;
        int lumaStride = codedW;
        int lumaPlaneSize = codedW * codedH * bytesPerSample;
        int chromaW = codedW >> sps.HShiftChroma;
        int chromaH = codedH >> sps.VShiftChroma;
        int chromaPlaneSize = chromaW * chromaH * bytesPerSample;

        if (bytesPerSample == 2)
        {
            int shift14 = 14 - bitDepth;
            int biShift = shift14 + 1;
            int biOffset = 1 << shift14;
            int maxVal = (1 << bitDepth) - 1;

            // Luma bi-prediction at intermediate precision
            var refLuma0 = MemoryMarshal.Cast<byte, ushort>(refBuffer0.AsSpan(0, lumaPlaneSize));
            var refLuma1 = MemoryMarshal.Cast<byte, ushort>(refBuffer1.AsSpan(0, lumaPlaneSize));
            var dstLuma = MemoryMarshal.Cast<byte, ushort>(currentFrameBuffer.AsSpan(0, lumaPlaneSize));

            Span<int> tempL0 = stackalloc int[nPbW * nPbH];
            Span<int> tempL1 = stackalloc int[nPbW * nPbH];

            HevcMotionCompensation.CompensateLumaHighBitDepthIntermediate(
                refLuma0, lumaStride, tempL0, nPbW,
                nPbW, nPbH, mvX0, mvY0, x0, y0, codedW, codedH, bitDepth);

            HevcMotionCompensation.CompensateLumaHighBitDepthIntermediate(
                refLuma1, lumaStride, tempL1, nPbW,
                nPbW, nPbH, mvX1, mvY1, x0, y0, codedW, codedH, bitDepth);

            for (int row = 0; row < nPbH; row++)
            {
                int dstY = y0 + row;
                if (dstY >= codedH) break;
                for (int col = 0; col < nPbW; col++)
                {
                    int dstX = x0 + col;
                    if (dstX >= codedW) break;
                    int l0 = tempL0[row * nPbW + col];
                    int l1 = tempL1[row * nPbW + col];
                    dstLuma[dstY * lumaStride + dstX] = (ushort)Math.Clamp(
                        (l0 + l1 + biOffset) >> biShift, 0, maxVal);
                }
            }

            // Chroma bi-prediction at intermediate precision
            int nPbWC = nPbW >> sps.HShiftChroma;
            int nPbHC = nPbH >> sps.VShiftChroma;
            int x0C = x0 >> sps.HShiftChroma;
            int y0C = y0 >> sps.VShiftChroma;
            int chromaMvX0 = LumaToChromaMv(mvX0, sps.HShiftChroma);
            int chromaMvY0 = LumaToChromaMv(mvY0, sps.VShiftChroma);
            int chromaMvX1 = LumaToChromaMv(mvX1, sps.HShiftChroma);
            int chromaMvY1 = LumaToChromaMv(mvY1, sps.VShiftChroma);

            int chromaBitDepth = sps.BitDepthChroma;
            int chromaShift14 = 14 - chromaBitDepth;
            int chromaBiShift = chromaShift14 + 1;
            int chromaBiOffset = 1 << chromaShift14;
            int chromaMaxVal = (1 << chromaBitDepth) - 1;

            if (nPbWC > 0 && nPbHC > 0)
            {
                Span<int> tempCL0 = stackalloc int[nPbWC * nPbHC];
                Span<int> tempCL1 = stackalloc int[nPbWC * nPbHC];

                // Cb plane
                var refCb0 = MemoryMarshal.Cast<byte, ushort>(refBuffer0.AsSpan(lumaPlaneSize, chromaPlaneSize));
                var refCb1 = MemoryMarshal.Cast<byte, ushort>(refBuffer1.AsSpan(lumaPlaneSize, chromaPlaneSize));
                var dstCb = MemoryMarshal.Cast<byte, ushort>(currentFrameBuffer.AsSpan(lumaPlaneSize, chromaPlaneSize));

                CompensateChromaHighBitDepthIntermediate(refCb0, chromaW, tempCL0, nPbWC,
                    nPbWC, nPbHC, chromaMvX0, chromaMvY0, x0C, y0C, chromaW, chromaH, chromaBitDepth);
                CompensateChromaHighBitDepthIntermediate(refCb1, chromaW, tempCL1, nPbWC,
                    nPbWC, nPbHC, chromaMvX1, chromaMvY1, x0C, y0C, chromaW, chromaH, chromaBitDepth);

                for (int row = 0; row < nPbHC; row++)
                {
                    int dstY = y0C + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < nPbWC; col++)
                    {
                        int dstX = x0C + col;
                        if (dstX >= chromaW) break;
                        int l0 = tempCL0[row * nPbWC + col];
                        int l1 = tempCL1[row * nPbWC + col];
                        dstCb[dstY * chromaW + dstX] = (ushort)Math.Clamp(
                            (l0 + l1 + chromaBiOffset) >> chromaBiShift, 0, chromaMaxVal);
                    }
                }

                // Cr plane
                var refCr0 = MemoryMarshal.Cast<byte, ushort>(refBuffer0.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize));
                var refCr1 = MemoryMarshal.Cast<byte, ushort>(refBuffer1.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize));
                var dstCr = MemoryMarshal.Cast<byte, ushort>(currentFrameBuffer.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize));

                CompensateChromaHighBitDepthIntermediate(refCr0, chromaW, tempCL0, nPbWC,
                    nPbWC, nPbHC, chromaMvX0, chromaMvY0, x0C, y0C, chromaW, chromaH, chromaBitDepth);
                CompensateChromaHighBitDepthIntermediate(refCr1, chromaW, tempCL1, nPbWC,
                    nPbWC, nPbHC, chromaMvX1, chromaMvY1, x0C, y0C, chromaW, chromaH, chromaBitDepth);

                for (int row = 0; row < nPbHC; row++)
                {
                    int dstY = y0C + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < nPbWC; col++)
                    {
                        int dstX = x0C + col;
                        if (dstX >= chromaW) break;
                        int l0 = tempCL0[row * nPbWC + col];
                        int l1 = tempCL1[row * nPbWC + col];
                        dstCr[dstY * chromaW + dstX] = (ushort)Math.Clamp(
                            (l0 + l1 + chromaBiOffset) >> chromaBiShift, 0, chromaMaxVal);
                    }
                }
            }
        }
        else
        {
            // 8-bit bi-prediction at intermediate precision
            var refLuma0 = refBuffer0.AsSpan(0, lumaPlaneSize);
            var refLuma1 = refBuffer1.AsSpan(0, lumaPlaneSize);

            Span<int> tempL0 = stackalloc int[nPbW * nPbH];
            Span<int> tempL1 = stackalloc int[nPbW * nPbH];

            // For 8-bit: shift14 = 14-8 = 6, biShift = 7, biOffset = 64
            int biShift = 7;
            int biOffset = 64;

            HevcMotionCompensation.CompensateLumaIntermediate(
                refLuma0, codedW, tempL0, nPbW,
                nPbW, nPbH, mvX0, mvY0, x0, y0, codedW, codedH);

            HevcMotionCompensation.CompensateLumaIntermediate(
                refLuma1, codedW, tempL1, nPbW,
                nPbW, nPbH, mvX1, mvY1, x0, y0, codedW, codedH);

            for (int row = 0; row < nPbH; row++)
            {
                int dstY = y0 + row;
                if (dstY >= codedH) break;
                for (int col = 0; col < nPbW; col++)
                {
                    int dstX = x0 + col;
                    if (dstX >= codedW) break;
                    int l0 = tempL0[row * nPbW + col];
                    int l1 = tempL1[row * nPbW + col];
                    currentFrameBuffer![dstY * codedW + dstX] = (byte)Math.Clamp(
                        (l0 + l1 + biOffset) >> biShift, 0, 255);
                }
            }

            // Chroma bi-prediction — convert luma MVs to chroma eighth-pel
            int chromaMvX0 = LumaToChromaMv(mvX0, sps.HShiftChroma);
            int chromaMvY0 = LumaToChromaMv(mvY0, sps.VShiftChroma);
            int chromaMvX1 = LumaToChromaMv(mvX1, sps.HShiftChroma);
            int chromaMvY1 = LumaToChromaMv(mvY1, sps.VShiftChroma);
            int nPbWC = nPbW >> sps.HShiftChroma;
            int nPbHC = nPbH >> sps.VShiftChroma;
            int x0C = x0 >> sps.HShiftChroma;
            int y0C = y0 >> sps.VShiftChroma;
            int chromaBiShift = 7;
            int chromaBiOffset = 64;

            if (nPbWC > 0 && nPbHC > 0)
            {
                Span<int> tempCL0 = stackalloc int[nPbWC * nPbHC];
                Span<int> tempCL1 = stackalloc int[nPbWC * nPbHC];

                // Cb plane
                var refCb0 = refBuffer0.AsSpan(lumaPlaneSize, chromaPlaneSize);
                var refCb1 = refBuffer1.AsSpan(lumaPlaneSize, chromaPlaneSize);

                HevcMotionCompensation.CompensateChromaIntermediate(
                    refCb0, chromaW, tempCL0, nPbWC,
                    nPbWC, nPbHC, chromaMvX0, chromaMvY0, x0C, y0C, chromaW, chromaH);
                HevcMotionCompensation.CompensateChromaIntermediate(
                    refCb1, chromaW, tempCL1, nPbWC,
                    nPbWC, nPbHC, chromaMvX1, chromaMvY1, x0C, y0C, chromaW, chromaH);

                for (int row = 0; row < nPbHC; row++)
                {
                    int dstY = y0C + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < nPbWC; col++)
                    {
                        int dstX = x0C + col;
                        if (dstX >= chromaW) break;
                        int l0 = tempCL0[row * nPbWC + col];
                        int l1 = tempCL1[row * nPbWC + col];
                        currentFrameBuffer![lumaPlaneSize + dstY * chromaW + dstX] = (byte)Math.Clamp(
                            (l0 + l1 + chromaBiOffset) >> chromaBiShift, 0, 255);
                    }
                }

                // Cr plane
                var refCr0 = refBuffer0.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize);
                var refCr1 = refBuffer1.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize);

                HevcMotionCompensation.CompensateChromaIntermediate(
                    refCr0, chromaW, tempCL0, nPbWC,
                    nPbWC, nPbHC, chromaMvX0, chromaMvY0, x0C, y0C, chromaW, chromaH);
                HevcMotionCompensation.CompensateChromaIntermediate(
                    refCr1, chromaW, tempCL1, nPbWC,
                    nPbWC, nPbHC, chromaMvX1, chromaMvY1, x0C, y0C, chromaW, chromaH);

                for (int row = 0; row < nPbHC; row++)
                {
                    int dstY = y0C + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < nPbWC; col++)
                    {
                        int dstX = x0C + col;
                        if (dstX >= chromaW) break;
                        int l0 = tempCL0[row * nPbWC + col];
                        int l1 = tempCL1[row * nPbWC + col];
                        currentFrameBuffer![lumaPlaneSize + chromaPlaneSize + dstY * chromaW + dstX] = (byte)Math.Clamp(
                            (l0 + l1 + chromaBiOffset) >> chromaBiShift, 0, 255);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Weighted uni-prediction: MC at intermediate precision, then apply weight + offset.
    /// Matches HEVC spec 8.5.3.3.3.1 and FFmpeg's put_hevc_qpel_uni_w.
    /// </summary>
    private void MotionCompensateWeightedUniPred(
        HevcSequenceParameterSet sps, byte[] refBuffer,
        int x0, int y0, int nPbW, int nPbH,
        short mvX, short mvY,
        short lumaWeight, short lumaOffset, int lumaLog2WeightDenom,
        short chromaWeightCb, short chromaOffsetCb,
        short chromaWeightCr, short chromaOffsetCr,
        int chromaLog2WeightDenom)
    {
        int bitDepth = sps.BitDepthLuma;
        int codedW = sps.PictureWidthInLumaSamples;
        int codedH = sps.PictureHeightInLumaSamples;
        int lumaPlaneSize = codedW * codedH;
        int chromaW = codedW >> sps.HShiftChroma;
        int chromaH = codedH >> sps.VShiftChroma;
        int chromaPlaneSize = chromaW * chromaH;

        if (bitDepth > 8)
        {
            // 10-bit weighted uni-pred: same formula as 8-bit but with high-bit-depth MC
            var refLuma16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                refBuffer.AsSpan(0, lumaPlaneSize * 2));
            int lumaPS16 = codedW * codedH;
            Span<int> tmpLuma = stackalloc int[nPbW * nPbH];
            HevcMotionCompensation.CompensateLumaHighBitDepthIntermediate(
                refLuma16, codedW, tmpLuma, nPbW,
                nPbW, nPbH, mvX, mvY, x0, y0, codedW, codedH, bitDepth);

            int uniShift = lumaLog2WeightDenom + 14 - bitDepth;
            int uniRound = uniShift > 0 ? 1 << (uniShift - 1) : 0;
            int ox = lumaOffset * (1 << (bitDepth - 8));
            int maxVal = (1 << bitDepth) - 1;

            var dst16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                currentFrameBuffer!.AsSpan(0, lumaPS16 * 2));
            for (int row = 0; row < nPbH; row++)
            {
                int dstY = y0 + row;
                if (dstY >= codedH) break;
                for (int col = 0; col < nPbW; col++)
                {
                    int dstX = x0 + col;
                    if (dstX >= codedW) break;
                    int val = tmpLuma[row * nPbW + col];
                    dst16[dstY * codedW + dstX] = (ushort)Math.Clamp(
                        ((val * lumaWeight + uniRound) >> uniShift) + ox, 0, maxVal);
                }
            }

            // Chroma 10-bit weighted unipred — convert luma MV to chroma eighth-pel
            int cMvX = LumaToChromaMv(mvX, sps.HShiftChroma);
            int cMvY = LumaToChromaMv(mvY, sps.VShiftChroma);
            int pbWC = nPbW >> sps.HShiftChroma;
            int pbHC = nPbH >> sps.VShiftChroma;
            int cX0 = x0 >> sps.HShiftChroma;
            int cY0 = y0 >> sps.VShiftChroma;
            if (pbWC > 0 && pbHC > 0)
            {
                int cShift = chromaLog2WeightDenom + 14 - bitDepth;
                int cRound = cShift > 0 ? 1 << (cShift - 1) : 0;
                int oxCb = chromaOffsetCb * (1 << (bitDepth - 8));
                int oxCr = chromaOffsetCr * (1 << (bitDepth - 8));

                Span<int> tmpC = stackalloc int[pbWC * pbHC];

                var refCb16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    refBuffer.AsSpan(lumaPS16 * 2, chromaW * chromaH * 2));
                CompensateChromaHighBitDepthIntermediate(
                    refCb16, chromaW, tmpC, pbWC,
                    pbWC, pbHC, cMvX, cMvY, cX0, cY0, chromaW, chromaH, bitDepth);
                var dstCb16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    currentFrameBuffer!.AsSpan(lumaPS16 * 2, chromaW * chromaH * 2));
                for (int row = 0; row < pbHC; row++)
                {
                    int dstY = cY0 + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < pbWC; col++)
                    {
                        int dstX = cX0 + col;
                        if (dstX >= chromaW) break;
                        int val = tmpC[row * pbWC + col];
                        dstCb16[dstY * chromaW + dstX] = (ushort)Math.Clamp(
                            ((val * chromaWeightCb + cRound) >> cShift) + oxCb, 0, maxVal);
                    }
                }

                var refCr16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    refBuffer.AsSpan((lumaPS16 + chromaW * chromaH) * 2, chromaW * chromaH * 2));
                CompensateChromaHighBitDepthIntermediate(
                    refCr16, chromaW, tmpC, pbWC,
                    pbWC, pbHC, cMvX, cMvY, cX0, cY0, chromaW, chromaH, bitDepth);
                var dstCr16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    currentFrameBuffer!.AsSpan((lumaPS16 + chromaW * chromaH) * 2, chromaW * chromaH * 2));
                for (int row = 0; row < pbHC; row++)
                {
                    int dstY = cY0 + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < pbWC; col++)
                    {
                        int dstX = cX0 + col;
                        if (dstX >= chromaW) break;
                        int val = tmpC[row * pbWC + col];
                        dstCr16[dstY * chromaW + dstX] = (ushort)Math.Clamp(
                            ((val * chromaWeightCr + cRound) >> cShift) + oxCr, 0, maxVal);
                    }
                }
            }
            return;
        }

        // Luma: MC at intermediate precision, apply weight
        var refLuma = refBuffer.AsSpan(0, lumaPlaneSize);
        Span<int> tempLuma = stackalloc int[nPbW * nPbH];
        HevcMotionCompensation.CompensateLumaIntermediate(
            refLuma, codedW, tempLuma, nPbW,
            nPbW, nPbH, mvX, mvY, x0, y0, codedW, codedH);

        int wShift = lumaLog2WeightDenom + 14 - bitDepth;
        int wRound = wShift > 0 ? 1 << (wShift - 1) : 0;

        for (int row = 0; row < nPbH; row++)
        {
            int dstY = y0 + row;
            if (dstY >= codedH) break;
            for (int col = 0; col < nPbW; col++)
            {
                int dstX = x0 + col;
                if (dstX >= codedW) break;
                int val = tempLuma[row * nPbW + col];
                currentFrameBuffer![dstY * codedW + dstX] = (byte)Math.Clamp(
                    ((val * lumaWeight + wRound) >> wShift) + lumaOffset, 0, 255);
            }
        }

        // Chroma — convert luma MV to chroma eighth-pel
        int chromaMvX = LumaToChromaMv(mvX, sps.HShiftChroma);
        int chromaMvY = LumaToChromaMv(mvY, sps.VShiftChroma);
        int nPbWC = nPbW >> sps.HShiftChroma;
        int nPbHC = nPbH >> sps.VShiftChroma;
        int x0C = x0 >> sps.HShiftChroma;
        int y0C = y0 >> sps.VShiftChroma;

        if (nPbWC > 0 && nPbHC > 0)
        {
            int cWShift = chromaLog2WeightDenom + 14 - bitDepth;
            int cWRound = cWShift > 0 ? 1 << (cWShift - 1) : 0;

            Span<int> tempC = stackalloc int[nPbWC * nPbHC];

            // Cb
            var refCb = refBuffer.AsSpan(lumaPlaneSize, chromaPlaneSize);
            HevcMotionCompensation.CompensateChromaIntermediate(
                refCb, chromaW, tempC, nPbWC,
                nPbWC, nPbHC, chromaMvX, chromaMvY, x0C, y0C, chromaW, chromaH);

            for (int row = 0; row < nPbHC; row++)
            {
                int dstY = y0C + row;
                if (dstY >= chromaH) break;
                for (int col = 0; col < nPbWC; col++)
                {
                    int dstX = x0C + col;
                    if (dstX >= chromaW) break;
                    int val = tempC[row * nPbWC + col];
                    currentFrameBuffer![lumaPlaneSize + dstY * chromaW + dstX] = (byte)Math.Clamp(
                        ((val * chromaWeightCb + cWRound) >> cWShift) + chromaOffsetCb, 0, 255);
                }
            }

            // Cr
            var refCr = refBuffer.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize);
            HevcMotionCompensation.CompensateChromaIntermediate(
                refCr, chromaW, tempC, nPbWC,
                nPbWC, nPbHC, chromaMvX, chromaMvY, x0C, y0C, chromaW, chromaH);

            for (int row = 0; row < nPbHC; row++)
            {
                int dstY = y0C + row;
                if (dstY >= chromaH) break;
                for (int col = 0; col < nPbWC; col++)
                {
                    int dstX = x0C + col;
                    if (dstX >= chromaW) break;
                    int val = tempC[row * nPbWC + col];
                    currentFrameBuffer![lumaPlaneSize + chromaPlaneSize + dstY * chromaW + dstX] = (byte)Math.Clamp(
                        ((val * chromaWeightCr + cWRound) >> cWShift) + chromaOffsetCr, 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// Weighted bi-prediction: MC both refs at intermediate precision, weighted combine.
    /// Matches HEVC spec 8.5.3.3.3.2 and FFmpeg's put_hevc_qpel_bi_w.
    /// </summary>
    private void MotionCompensateWeightedBiPred(
        HevcSequenceParameterSet sps,
        byte[] refBuffer0, short mvX0, short mvY0, int refIdxL0,
        byte[] refBuffer1, short mvX1, short mvY1, int refIdxL1,
        int x0, int y0, int nPbW, int nPbH)
    {
        var slice = currentSliceHeader!;
        int bitDepth = sps.BitDepthLuma;
        int codedW = sps.PictureWidthInLumaSamples;
        int codedH = sps.PictureHeightInLumaSamples;
        int lumaPlaneSize = codedW * codedH;
        int chromaW = codedW >> sps.HShiftChroma;
        int chromaH = codedH >> sps.VShiftChroma;
        int chromaPlaneSize = chromaW * chromaH;

        if (bitDepth > 8)
        {
            // 10-bit weighted bi-pred
            int biDenom = slice.LumaLog2WeightDenom;
            short biWL0 = slice.LumaWeightL0[refIdxL0];
            short biOL0 = slice.LumaOffsetL0[refIdxL0];
            short biWL1 = slice.LumaWeightL1[refIdxL1];
            short biOL1 = slice.LumaOffsetL1[refIdxL1];
            int oxScale = 1 << (bitDepth - 8);
            int biMaxVal = (1 << bitDepth) - 1;

            int lumaPS16 = codedW * codedH;
            var refLuma0_16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                refBuffer0.AsSpan(0, lumaPS16 * 2));
            var refLuma1_16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                refBuffer1.AsSpan(0, lumaPS16 * 2));

            Span<int> tmpL0 = stackalloc int[nPbW * nPbH];
            Span<int> tmpL1 = stackalloc int[nPbW * nPbH];
            HevcMotionCompensation.CompensateLumaHighBitDepthIntermediate(
                refLuma0_16, codedW, tmpL0, nPbW,
                nPbW, nPbH, mvX0, mvY0, x0, y0, codedW, codedH, bitDepth);
            HevcMotionCompensation.CompensateLumaHighBitDepthIntermediate(
                refLuma1_16, codedW, tmpL1, nPbW,
                nPbW, nPbH, mvX1, mvY1, x0, y0, codedW, codedH, bitDepth);

            int biLog2Wd = biDenom + 14 - bitDepth;
            int biShift = biLog2Wd + 1;
            int biRound = (biOL0 * oxScale + biOL1 * oxScale + 1) << biLog2Wd;

            var dst16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                currentFrameBuffer!.AsSpan(0, lumaPS16 * 2));
            for (int row = 0; row < nPbH; row++)
            {
                int dstY = y0 + row;
                if (dstY >= codedH) break;
                for (int col = 0; col < nPbW; col++)
                {
                    int dstX = x0 + col;
                    if (dstX >= codedW) break;
                    int l0v = tmpL0[row * nPbW + col];
                    int l1v = tmpL1[row * nPbW + col];
                    dst16[dstY * codedW + dstX] = (ushort)Math.Clamp(
                        ((l0v * biWL0 + l1v * biWL1 + biRound) >> biShift), 0, biMaxVal);
                }
            }

            // Chroma 10-bit weighted bi-pred — convert luma MVs to chroma eighth-pel
            int cMvX0 = LumaToChromaMv(mvX0, sps.HShiftChroma);
            int cMvY0 = LumaToChromaMv(mvY0, sps.VShiftChroma);
            int cMvX1 = LumaToChromaMv(mvX1, sps.HShiftChroma);
            int cMvY1 = LumaToChromaMv(mvY1, sps.VShiftChroma);
            int pbWC = nPbW >> sps.HShiftChroma;
            int pbHC = nPbH >> sps.VShiftChroma;
            int cX0 = x0 >> sps.HShiftChroma;
            int cY0 = y0 >> sps.VShiftChroma;
            if (pbWC > 0 && pbHC > 0)
            {
                int biCDenom = slice.ChromaLog2WeightDenom;
                int biCLog2Wd = biCDenom + 14 - bitDepth;
                int biCShift = biCLog2Wd + 1;

                Span<int> tmpCL0 = stackalloc int[pbWC * pbHC];
                Span<int> tmpCL1 = stackalloc int[pbWC * pbHC];

                // Cb
                short wCbL0 = refIdxL0 < slice.ChromaWeightL0.GetLength(0) ? slice.ChromaWeightL0[refIdxL0, 0] : (short)(1 << biCDenom);
                short oCbL0 = refIdxL0 < slice.ChromaOffsetL0.GetLength(0) ? slice.ChromaOffsetL0[refIdxL0, 0] : (short)0;
                short wCbL1 = refIdxL1 < slice.ChromaWeightL1.GetLength(0) ? slice.ChromaWeightL1[refIdxL1, 0] : (short)(1 << biCDenom);
                short oCbL1 = refIdxL1 < slice.ChromaOffsetL1.GetLength(0) ? slice.ChromaOffsetL1[refIdxL1, 0] : (short)0;
                int biCbRound = (oCbL0 * oxScale + oCbL1 * oxScale + 1) << biCLog2Wd;

                var refCb0_16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    refBuffer0.AsSpan(lumaPS16 * 2, chromaW * chromaH * 2));
                var refCb1_16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    refBuffer1.AsSpan(lumaPS16 * 2, chromaW * chromaH * 2));
                CompensateChromaHighBitDepthIntermediate(refCb0_16, chromaW, tmpCL0, pbWC,
                    pbWC, pbHC, cMvX0, cMvY0, cX0, cY0, chromaW, chromaH, bitDepth);
                CompensateChromaHighBitDepthIntermediate(refCb1_16, chromaW, tmpCL1, pbWC,
                    pbWC, pbHC, cMvX1, cMvY1, cX0, cY0, chromaW, chromaH, bitDepth);

                var dstCb16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    currentFrameBuffer!.AsSpan(lumaPS16 * 2, chromaW * chromaH * 2));
                for (int row = 0; row < pbHC; row++)
                {
                    int dstY = cY0 + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < pbWC; col++)
                    {
                        int dstX = cX0 + col;
                        if (dstX >= chromaW) break;
                        int l0v = tmpCL0[row * pbWC + col];
                        int l1v = tmpCL1[row * pbWC + col];
                        dstCb16[dstY * chromaW + dstX] = (ushort)Math.Clamp(
                            ((l0v * wCbL0 + l1v * wCbL1 + biCbRound) >> biCShift), 0, biMaxVal);
                    }
                }

                // Cr
                short wCrL0 = refIdxL0 < slice.ChromaWeightL0.GetLength(0) ? slice.ChromaWeightL0[refIdxL0, 1] : (short)(1 << biCDenom);
                short oCrL0 = refIdxL0 < slice.ChromaOffsetL0.GetLength(0) ? slice.ChromaOffsetL0[refIdxL0, 1] : (short)0;
                short wCrL1 = refIdxL1 < slice.ChromaWeightL1.GetLength(0) ? slice.ChromaWeightL1[refIdxL1, 1] : (short)(1 << biCDenom);
                short oCrL1 = refIdxL1 < slice.ChromaOffsetL1.GetLength(0) ? slice.ChromaOffsetL1[refIdxL1, 1] : (short)0;
                int biCrRound = (oCrL0 * oxScale + oCrL1 * oxScale + 1) << biCLog2Wd;

                var refCr0_16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    refBuffer0.AsSpan((lumaPS16 + chromaW * chromaH) * 2, chromaW * chromaH * 2));
                var refCr1_16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    refBuffer1.AsSpan((lumaPS16 + chromaW * chromaH) * 2, chromaW * chromaH * 2));
                CompensateChromaHighBitDepthIntermediate(refCr0_16, chromaW, tmpCL0, pbWC,
                    pbWC, pbHC, cMvX0, cMvY0, cX0, cY0, chromaW, chromaH, bitDepth);
                CompensateChromaHighBitDepthIntermediate(refCr1_16, chromaW, tmpCL1, pbWC,
                    pbWC, pbHC, cMvX1, cMvY1, cX0, cY0, chromaW, chromaH, bitDepth);

                var dstCr16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                    currentFrameBuffer!.AsSpan((lumaPS16 + chromaW * chromaH) * 2, chromaW * chromaH * 2));
                for (int row = 0; row < pbHC; row++)
                {
                    int dstY = cY0 + row;
                    if (dstY >= chromaH) break;
                    for (int col = 0; col < pbWC; col++)
                    {
                        int dstX = cX0 + col;
                        if (dstX >= chromaW) break;
                        int l0v = tmpCL0[row * pbWC + col];
                        int l1v = tmpCL1[row * pbWC + col];
                        dstCr16[dstY * chromaW + dstX] = (ushort)Math.Clamp(
                            ((l0v * wCrL0 + l1v * wCrL1 + biCrRound) >> biCShift), 0, biMaxVal);
                    }
                }
            }
            return;
        }

        int lumaDenom = slice.LumaLog2WeightDenom;
        short wL0 = slice.LumaWeightL0[refIdxL0];
        short oL0 = slice.LumaOffsetL0[refIdxL0];
        short wL1 = slice.LumaWeightL1[refIdxL1];
        short oL1 = slice.LumaOffsetL1[refIdxL1];

        // Luma weighted bipred at intermediate precision
        var refLuma0 = refBuffer0.AsSpan(0, lumaPlaneSize);
        var refLuma1 = refBuffer1.AsSpan(0, lumaPlaneSize);

        Span<int> tempL0 = stackalloc int[nPbW * nPbH];
        Span<int> tempL1 = stackalloc int[nPbW * nPbH];

        HevcMotionCompensation.CompensateLumaIntermediate(
            refLuma0, codedW, tempL0, nPbW,
            nPbW, nPbH, mvX0, mvY0, x0, y0, codedW, codedH);
        HevcMotionCompensation.CompensateLumaIntermediate(
            refLuma1, codedW, tempL1, nPbW,
            nPbW, nPbH, mvX1, mvY1, x0, y0, codedW, codedH);

        // FFmpeg formula: ((l0*w0 + l1*w1 + (o0+o1+1) << log2Wd) >> (log2Wd + 1))
        // Offset goes INSIDE sum before shift, NOT added separately after
        int log2Wd = lumaDenom + 14 - bitDepth;
        int wShift = log2Wd + 1;
        int wRound = (oL0 + oL1 + 1) << log2Wd;

        for (int row = 0; row < nPbH; row++)
        {
            int dstY = y0 + row;
            if (dstY >= codedH) break;
            for (int col = 0; col < nPbW; col++)
            {
                int dstX = x0 + col;
                if (dstX >= codedW) break;
                int l0 = tempL0[row * nPbW + col];
                int l1 = tempL1[row * nPbW + col];
                currentFrameBuffer![dstY * codedW + dstX] = (byte)Math.Clamp(
                    ((l0 * wL0 + l1 * wL1 + wRound) >> wShift), 0, 255);
            }
        }

        // Chroma weighted bipred — convert luma MVs to chroma eighth-pel
        int chromaMvX0 = LumaToChromaMv(mvX0, sps.HShiftChroma);
        int chromaMvY0 = LumaToChromaMv(mvY0, sps.VShiftChroma);
        int chromaMvX1 = LumaToChromaMv(mvX1, sps.HShiftChroma);
        int chromaMvY1 = LumaToChromaMv(mvY1, sps.VShiftChroma);
        int nPbWC = nPbW >> sps.HShiftChroma;
        int nPbHC = nPbH >> sps.VShiftChroma;
        int x0C = x0 >> sps.HShiftChroma;
        int y0C = y0 >> sps.VShiftChroma;

        if (nPbWC > 0 && nPbHC > 0)
        {
            int chromaDenom = slice.ChromaLog2WeightDenom;
            int cLog2Wd = chromaDenom + 14 - bitDepth;
            int cWShift = cLog2Wd + 1;

            Span<int> tempCL0 = stackalloc int[nPbWC * nPbHC];
            Span<int> tempCL1 = stackalloc int[nPbWC * nPbHC];

            // Cb
            short wCbL0 = refIdxL0 < slice.ChromaWeightL0.GetLength(0) ? slice.ChromaWeightL0[refIdxL0, 0] : (short)(1 << chromaDenom);
            short oCbL0 = refIdxL0 < slice.ChromaOffsetL0.GetLength(0) ? slice.ChromaOffsetL0[refIdxL0, 0] : (short)0;
            short wCbL1 = refIdxL1 < slice.ChromaWeightL1.GetLength(0) ? slice.ChromaWeightL1[refIdxL1, 0] : (short)(1 << chromaDenom);
            short oCbL1 = refIdxL1 < slice.ChromaOffsetL1.GetLength(0) ? slice.ChromaOffsetL1[refIdxL1, 0] : (short)0;
            int cbRound = (oCbL0 + oCbL1 + 1) << cLog2Wd;

            HevcMotionCompensation.CompensateChromaIntermediate(
                refBuffer0.AsSpan(lumaPlaneSize, chromaPlaneSize), chromaW, tempCL0, nPbWC,
                nPbWC, nPbHC, chromaMvX0, chromaMvY0, x0C, y0C, chromaW, chromaH);
            HevcMotionCompensation.CompensateChromaIntermediate(
                refBuffer1.AsSpan(lumaPlaneSize, chromaPlaneSize), chromaW, tempCL1, nPbWC,
                nPbWC, nPbHC, chromaMvX1, chromaMvY1, x0C, y0C, chromaW, chromaH);

            for (int row = 0; row < nPbHC; row++)
            {
                int dstY = y0C + row;
                if (dstY >= chromaH) break;
                for (int col = 0; col < nPbWC; col++)
                {
                    int dstX = x0C + col;
                    if (dstX >= chromaW) break;
                    int l0 = tempCL0[row * nPbWC + col];
                    int l1 = tempCL1[row * nPbWC + col];
                    currentFrameBuffer![lumaPlaneSize + dstY * chromaW + dstX] = (byte)Math.Clamp(
                        ((l0 * wCbL0 + l1 * wCbL1 + cbRound) >> cWShift), 0, 255);
                }
            }

            // Cr
            short wCrL0 = refIdxL0 < slice.ChromaWeightL0.GetLength(0) ? slice.ChromaWeightL0[refIdxL0, 1] : (short)(1 << chromaDenom);
            short oCrL0 = refIdxL0 < slice.ChromaOffsetL0.GetLength(0) ? slice.ChromaOffsetL0[refIdxL0, 1] : (short)0;
            short wCrL1 = refIdxL1 < slice.ChromaWeightL1.GetLength(0) ? slice.ChromaWeightL1[refIdxL1, 1] : (short)(1 << chromaDenom);
            short oCrL1 = refIdxL1 < slice.ChromaOffsetL1.GetLength(0) ? slice.ChromaOffsetL1[refIdxL1, 1] : (short)0;
            int crRound = (oCrL0 + oCrL1 + 1) << cLog2Wd;

            HevcMotionCompensation.CompensateChromaIntermediate(
                refBuffer0.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize), chromaW, tempCL0, nPbWC,
                nPbWC, nPbHC, chromaMvX0, chromaMvY0, x0C, y0C, chromaW, chromaH);
            HevcMotionCompensation.CompensateChromaIntermediate(
                refBuffer1.AsSpan(lumaPlaneSize + chromaPlaneSize, chromaPlaneSize), chromaW, tempCL1, nPbWC,
                nPbWC, nPbHC, chromaMvX1, chromaMvY1, x0C, y0C, chromaW, chromaH);

            for (int row = 0; row < nPbHC; row++)
            {
                int dstY = y0C + row;
                if (dstY >= chromaH) break;
                for (int col = 0; col < nPbWC; col++)
                {
                    int dstX = x0C + col;
                    if (dstX >= chromaW) break;
                    int l0 = tempCL0[row * nPbWC + col];
                    int l1 = tempCL1[row * nPbWC + col];
                    currentFrameBuffer![lumaPlaneSize + chromaPlaneSize + dstY * chromaW + dstX] = (byte)Math.Clamp(
                        ((l0 * wCrL0 + l1 * wCrL1 + crRound) >> cWShift), 0, 255);
                }
            }
        }
    }
    private static void CompensateChromaHighBitDepth(
        ReadOnlySpan<ushort> reference, int refStride,
        Span<ushort> output, int outStride,
        int width, int height,
        int mvX, int mvY,
        int blockX, int blockY,
        int frameWidth, int frameHeight,
        int bitDepth)
    {
        int maxValue = (1 << bitDepth) - 1;

        // Chroma MV: quarter-pel luma → eighth-pel chroma for 4:2:0
        // fracX = (mvX & 7), fracY = (mvY & 7) with proper chroma scaling
        int fullX = blockX + (mvX >> 3);
        int fullY = blockY + (mvY >> 3);
        int fracX = mvX & 7;
        int fracY = mvY & 7;

        ReadOnlySpan<short> chromaFilter = [
            0, 64, 0, 0,
            -2, 58, 10, -2,
            -4, 54, 16, -2,
            -6, 46, 28, -4,
            -4, 36, 36, -4,
            -4, 28, 46, -6,
            -2, 16, 54, -4,
            -2, 10, 58, -2
        ];

        if (fracX == 0 && fracY == 0)
        {
            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                    output[row * outStride + col] = reference[srcY * refStride + srcX];
                }
            }
        }
        else
        {
            int filterOffsetH = fracX * 4;
            int filterOffsetV = fracY * 4;
            int shift = 14 - bitDepth;
            int offset = 1 << (shift - 1);

            // Temporary buffer for horizontal pass
            int tempHeight = height + 3;
            Span<int> temp = stackalloc int[width * tempHeight];

            // Horizontal pass — partial normalization matching ffmpeg:
            // tmp[x] = EPEL_FILTER(src, 1) >> (BIT_DEPTH - 8)
            int hShift = bitDepth - 8;
            for (int row = 0; row < tempHeight; row++)
            {
                int srcY = Math.Clamp(fullY + row - 1, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int srcX = Math.Clamp(fullX + col - 1 + k, 0, frameWidth - 1);
                        sum += chromaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                    }
                    temp[row * width + col] = sum >> hShift;
                }
            }

            // Vertical pass — combined normalization:
            // dst = av_clip_pixel(((EPEL_FILTER(tmp, stride) >> 6) + offset) >> shift)
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        sum += chromaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                    }
                    output[row * outStride + col] = (ushort)Math.Clamp(((sum >> 6) + offset) >> shift, 0, maxValue);
                }
            }
        }
    }

    /// <summary>
    /// Chroma MC producing intermediate ~14-bit precision output for bi-prediction.
    /// </summary>
    private static void CompensateChromaHighBitDepthIntermediate(
        ReadOnlySpan<ushort> reference, int refStride,
        Span<int> output, int outStride,
        int width, int height,
        int mvX, int mvY,
        int blockX, int blockY,
        int frameWidth, int frameHeight,
        int bitDepth)
    {
        int fullX = blockX + (mvX >> 3);
        int fullY = blockY + (mvY >> 3);
        int fracX = mvX & 7;
        int fracY = mvY & 7;
        int shift14 = 14 - bitDepth;

        ReadOnlySpan<short> chromaFilter = [
            0, 64, 0, 0,
            -2, 58, 10, -2,
            -4, 54, 16, -2,
            -6, 46, 28, -4,
            -4, 36, 36, -4,
            -4, 28, 46, -6,
            -2, 16, 54, -4,
            -2, 10, 58, -2
        ];

        if (fracX == 0 && fracY == 0)
        {
            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                    output[row * outStride + col] = reference[srcY * refStride + srcX] << shift14;
                }
            }
        }
        else
        {
            int filterOffsetH = fracX * 4;
            int filterOffsetV = fracY * 4;
            int hShift = bitDepth - 8;

            int tempHeight = height + 3;
            Span<int> temp = stackalloc int[width * tempHeight];

            for (int row = 0; row < tempHeight; row++)
            {
                int srcY = Math.Clamp(fullY + row - 1, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int srcX = Math.Clamp(fullX + col - 1 + k, 0, frameWidth - 1);
                        sum += chromaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                    }
                    temp[row * width + col] = sum >> hShift;
                }
            }

            // Vertical pass: intermediate output (sum >> 6) without clip
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        sum += chromaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                    }
                    output[row * outStride + col] = sum >> 6;
                }
            }
        }
    }
}
