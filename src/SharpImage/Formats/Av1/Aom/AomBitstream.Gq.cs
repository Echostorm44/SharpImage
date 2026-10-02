using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>SequenceHeader as libaom's encoder fills it (init_seq_coding_tools, init_config_sequence, the speed features'
/// tool flags while !seq_params_locked, av1_set_svc_seq_params, set_bitstream_level_tier).</summary>
internal sealed class AomSeqHeader
{
    public int Profile, BitDepth, SsX = 1, SsY = 1;
    public bool Monochrome;
    public bool StillPicture, ReducedStillPictureHdr;
    public int MaxFrameWidth, MaxFrameHeight, NumBitsWidth, NumBitsHeight;
    public int SbSize = BLOCK_64X64;
    public bool EnableFilterIntra = true, EnableIntraEdgeFilter = true, EnableInterintraCompound = true, EnableMaskedCompound = true;
    public bool EnableWarpedMotion = true, EnableDualFilter = true, EnableOrderHint = true, EnableDistWtdComp = true, EnableRefFrameMvs = true;
    public bool EnableSuperres, EnableCdef, EnableRestoration;
    public int OrderHintBitsMinus1 = DEFAULT_EXPLICIT_ORDER_HINT_BITS - 1;
    public int ForceScreenContentTools = 2, ForceIntegerMv = 2;
    public int OperatingPointsCntMinus1;
    public readonly int[] OperatingPointIdc = new int[32];
    public readonly int[] SeqLevelIdx = new int[32];
    public readonly int[] Tier = new int[32];
    public bool HasNonzeroOperatingPointIdc;
    public bool FilmGrainParamsPresent;
    public AomSequenceConfig Color = new();

    public int MibSize => SbSize == BLOCK_128X128 ? 32 : 16;
}

/// <summary>The frame-level state write_uncompressed_header_obu and av1_pack_bitstream read for a good-quality /
/// real-time frame (cm->current_frame, cm->features, the reference map, the previous frame's deltas / motion).</summary>
internal sealed class AomGqFrameHeader
{
    public required AomSeqHeader Seq;
    public int FrameType = KEY_FRAME;
    public bool ShowFrame = true, ShowableFrame, ErrorResilientMode, DisableCdfUpdate, ShowExistingFrame;
    public int ExistingFbIdxToShow;
    public int OrderHint, PrimaryRefFrame = PRIMARY_REF_NONE, RefreshFrameFlags = 0xff;
    public int TemporalLayerId, SpatialLayerId;
    /// <summary>cm->superres_upscaled_width / height (= cm->width / height without superres) and the render size.</summary>
    public int UpscaledWidth, UpscaledHeight, RenderWidth, RenderHeight;
    /// <summary>cm->remapped_ref_idx [INTER_REFS_PER_FRAME] and the buffers of the reference map (for the sizes).</summary>
    public readonly int[] RemappedRefIdx = new int[REF_FRAMES];
    public readonly AomRefBuffer?[] RefFrameMap = new AomRefBuffer?[REF_FRAMES];
    public bool AllowHighPrecisionMv, CurFrameForceIntegerMv, SwitchableMotionMode, AllowRefFrameMvs, AllowWarpedMotion;
    public int InterpFilter = EIGHTTAP_REGULAR;
    public bool ReferenceSelect;
    public bool SkipModeAllowed, SkipModeFlag;
    public bool RefreshFrameContextDisabled;
    /// <summary>The primary reference frame's loop filter deltas (default deltas when there is none).</summary>
    public readonly sbyte[] PrevRefDeltas = { 1, 0, 0, 0, -1, 0, -1, -1 };
    public readonly sbyte[] PrevModeDeltas = { 0, 0 };
    public bool ModeRefDeltaUpdate;
    /// <summary>cm->global_motion and cm->prev_frame->global_motion (both [REF_FRAMES]).</summary>
    public readonly AomWarpedMotionParams[] GlobalMotion = AomWarpedMotionParams.NewIdentitySet();
    public AomWarpedMotionParams[]? PrevGlobalMotion;

    public bool FrameIsIntraOnly => FrameType == KEY_FRAME || FrameType == INTRA_ONLY_FRAME;
    public AomRefBuffer? GetRefFrameBuf(int refFrame) => RefFrameMap[RemappedRefIdx[refFrame - LAST_FRAME]];
}

internal static partial class AomBitstream
{
    /// <summary>av1_pack_bitstream for a good-quality / real-time frame: the sequence header OBU (key / intra-only
    /// frames), then one OBU_FRAME (one tile group); the temporal delimiter is the caller's (av1_cx_iface.c adds it
    /// to the first frame of a temporal unit).</summary>
    internal static byte[] PackFrameGq(AomComp cpi, AomGqFrameHeader fh, out int largestTileId, System.IO.TextWriter? trace = null)
    {
        var cm = cpi.Cm;
        var seq = fh.Seq;
        var f = new FrameState(cpi, seq.Color)
        {
            Gq = fh, Profile = seq.Profile, SeqLevelIdx = seq.SeqLevelIdx[0], NumBitsWidth = seq.NumBitsWidth, NumBitsHeight = seq.NumBitsHeight,
            EnableFilterIntra = seq.EnableFilterIntra, EnableIntraEdgeFilter = seq.EnableIntraEdgeFilter, EnableRestoration = seq.EnableRestoration,
            EnableCdef = seq.EnableCdef,
        };
        byte obuExtension = (byte)(fh.TemporalLayerId << 5 | fh.SpatialLayerId << 3);
        var outBuf = new System.IO.MemoryStream();
        if (fh.FrameType == INTRA_ONLY_FRAME || fh.FrameType == KEY_FRAME)
        {
            byte[] sh = WriteSequenceHeaderObuGq(seq);
            outBuf.WriteByte(ObuHeader(OBU_SEQUENCE_HEADER));   // not layer specific: no extension
            WriteUleb(outBuf, (ulong)sh.Length);
            outBuf.Write(sh);
        }
        int numTiles = cm.TileCols * cm.TileRows;
        var tiles = new byte[numTiles][];
        int tokIdx = 0, maxTileSize = 0;
        largestTileId = 0;
        for (int tileRow = 0, i = 0; tileRow < cm.TileRows; tileRow++)
            for (int tileCol = 0; tileCol < cm.TileCols; tileCol++, i++)
            {
                tiles[i] = WriteTile(f, cm.TileInit(tileRow, tileCol), ref tokIdx, trace);
                if (tiles[i].Length > maxTileSize) { largestTileId = i; maxTileSize = tiles[i].Length; }
            }
        int tileSizeBytes = ChooseSizeBytes((uint)maxTileSize);
        var wb = new AomWriteBitBuffer();
        WriteUncompressedHeaderGq(f, fh, wb, largestTileId, tileSizeBytes);
        byte[] hdr = wb.ToArray();
        var payload = new System.IO.MemoryStream();
        payload.Write(hdr);
        if (cm.Log2Rows + cm.Log2Cols > 0) payload.WriteByte(0);   // tile_start_and_end_present_flag 0 (byte aligned)
        for (int i = 0; i < numTiles; i++)
        {
            if (i < numTiles - 1)
            {
                uint sz = (uint)(tiles[i].Length - 1);
                for (int b = 0; b < tileSizeBytes; b++) payload.WriteByte((byte)(sz >> (8 * b)));
            }
            payload.Write(tiles[i]);
        }
        // av1_write_obu_header: the extension for layer-specific OBUs when an operating point idc is non-zero
        bool ext = seq.HasNonzeroOperatingPointIdc;
        outBuf.WriteByte((byte)((OBU_FRAME << 3) | (ext ? 1 << 2 : 0) | (1 << 1)));
        if (ext) outBuf.WriteByte(obuExtension);
        WriteUleb(outBuf, (ulong)payload.Length);
        payload.Position = 0;
        payload.CopyTo(outBuf);
        return outBuf.ToArray();
    }

    /// <summary>The temporal delimiter OBU av1_cx_iface.c writes before the first frame of a temporal unit.</summary>
    internal static readonly byte[] TemporalDelimiter = { (byte)(OBU_TEMPORAL_DELIMITER << 3 | 1 << 1), 0 };

    /// <summary>av1_write_sequence_header_obu.</summary>
    private static byte[] WriteSequenceHeaderObuGq(AomSeqHeader seq)
    {
        var wb = new AomWriteBitBuffer();
        wb.WriteLiteral(seq.Profile, 3);
        wb.WriteBit(seq.StillPicture ? 1 : 0);
        wb.WriteBit(seq.ReducedStillPictureHdr ? 1 : 0);
        if (seq.ReducedStillPictureHdr) wb.WriteLiteral(seq.SeqLevelIdx[0], 5);
        else
        {
            wb.WriteBit(0);   // timing_info_present_flag
            wb.WriteBit(0);   // display_model_info_present_flag
            wb.WriteLiteral(seq.OperatingPointsCntMinus1, 5);   // OP_POINTS_CNT_MINUS_1_BITS
            for (int i = 0; i < seq.OperatingPointsCntMinus1 + 1; i++)
            {
                wb.WriteLiteral(seq.OperatingPointIdc[i], 12);   // OP_POINTS_IDC_BITS
                wb.WriteLiteral(seq.SeqLevelIdx[i], 5);
                if (seq.SeqLevelIdx[i] >= 8) wb.WriteBit(seq.Tier[i]);   // SEQ_LEVEL_4_0
            }
        }
        // write_sequence_header
        wb.WriteLiteral(seq.NumBitsWidth - 1, 4);
        wb.WriteLiteral(seq.NumBitsHeight - 1, 4);
        wb.WriteLiteral(seq.MaxFrameWidth - 1, seq.NumBitsWidth);
        wb.WriteLiteral(seq.MaxFrameHeight - 1, seq.NumBitsHeight);
        if (!seq.ReducedStillPictureHdr) wb.WriteBit(0);   // frame_id_numbers_present_flag
        wb.WriteBit(seq.SbSize == BLOCK_128X128 ? 1 : 0);
        wb.WriteBit(seq.EnableFilterIntra ? 1 : 0);
        wb.WriteBit(seq.EnableIntraEdgeFilter ? 1 : 0);
        if (!seq.ReducedStillPictureHdr)
        {
            wb.WriteBit(seq.EnableInterintraCompound ? 1 : 0);
            wb.WriteBit(seq.EnableMaskedCompound ? 1 : 0);
            wb.WriteBit(seq.EnableWarpedMotion ? 1 : 0);
            wb.WriteBit(seq.EnableDualFilter ? 1 : 0);
            wb.WriteBit(seq.EnableOrderHint ? 1 : 0);
            if (seq.EnableOrderHint)
            {
                wb.WriteBit(seq.EnableDistWtdComp ? 1 : 0);
                wb.WriteBit(seq.EnableRefFrameMvs ? 1 : 0);
            }
            if (seq.ForceScreenContentTools == 2) wb.WriteBit(1);
            else { wb.WriteBit(0); wb.WriteBit(seq.ForceScreenContentTools); }
            if (seq.ForceScreenContentTools > 0)
            {
                if (seq.ForceIntegerMv == 2) wb.WriteBit(1);
                else { wb.WriteBit(0); wb.WriteBit(seq.ForceIntegerMv); }
            }
            if (seq.EnableOrderHint) wb.WriteLiteral(seq.OrderHintBitsMinus1, 3);
        }
        wb.WriteBit(seq.EnableSuperres ? 1 : 0);
        wb.WriteBit(seq.EnableCdef ? 1 : 0);
        wb.WriteBit(seq.EnableRestoration ? 1 : 0);
        WriteColorConfigGq(seq, wb);
        wb.WriteBit(seq.FilmGrainParamsPresent ? 1 : 0);
        AddTrailingBits(wb);
        return wb.ToArray();
    }

    /// <summary>write_color_config.</summary>
    private static void WriteColorConfigGq(AomSeqHeader seq, AomWriteBitBuffer wb)
    {
        var c = seq.Color;
        wb.WriteBit(seq.BitDepth == 8 ? 0 : 1);
        if (seq.Profile == 2 && seq.BitDepth != 8) wb.WriteBit(seq.BitDepth == 10 ? 0 : 1);
        if (seq.Profile != 1) wb.WriteBit(seq.Monochrome ? 1 : 0);
        if (c.ColorPrimaries == 2 && c.TransferCharacteristics == 2 && c.MatrixCoefficients == 2) wb.WriteBit(0);
        else
        {
            wb.WriteBit(1);
            wb.WriteLiteral(c.ColorPrimaries, 8);
            wb.WriteLiteral(c.TransferCharacteristics, 8);
            wb.WriteLiteral(c.MatrixCoefficients, 8);
        }
        if (seq.Monochrome)
        {
            wb.WriteBit(c.ColorRange);
            return;
        }
        if (c.ColorPrimaries == 1 && c.TransferCharacteristics == 13 && c.MatrixCoefficients == 0) { }
        else
        {
            wb.WriteBit(c.ColorRange);
            if (seq.Profile == 2 && seq.BitDepth == 12)
            {
                wb.WriteBit(seq.SsX);
                if (seq.SsX != 0) wb.WriteBit(seq.SsY);
            }
            if (seq.SsX == 1 && seq.SsY == 1) wb.WriteLiteral(c.ChromaSamplePosition, 2);
        }
        wb.WriteBit(0);   // separate_uv_delta_q
    }

    /// <summary>write_uncompressed_header_obu (no frame ids, decoder model, S-frames, superres or large-scale tiles).</summary>
    private static void WriteUncompressedHeaderGq(FrameState f, AomGqFrameHeader fh, AomWriteBitBuffer wb, int contextUpdateTileId, int tileSizeBytes)
    {
        var cpi = f.Cpi;
        var cm = f.Cm;
        var seq = fh.Seq;
        if (!seq.ReducedStillPictureHdr)
        {
            if (fh.ShowExistingFrame)
            {
                wb.WriteBit(1);
                wb.WriteLiteral(fh.ExistingFbIdxToShow, 3);
                return;
            }
            wb.WriteBit(0);
            wb.WriteLiteral(fh.FrameType, 2);
            wb.WriteBit(fh.ShowFrame ? 1 : 0);
            if (!fh.ShowFrame) wb.WriteBit(fh.ShowableFrame ? 1 : 0);
            if (!(fh.FrameType == KEY_FRAME && fh.ShowFrame)) wb.WriteBit(fh.ErrorResilientMode ? 1 : 0);
        }
        wb.WriteBit(fh.DisableCdfUpdate ? 1 : 0);
        if (seq.ForceScreenContentTools == 2) wb.WriteBit(f.AllowScreenContentTools ? 1 : 0);
        if (f.AllowScreenContentTools && seq.ForceIntegerMv == 2) wb.WriteBit(fh.CurFrameForceIntegerMv ? 1 : 0);
        bool frameSizeOverride = false;
        if (!seq.ReducedStillPictureHdr)
        {
            frameSizeOverride = fh.UpscaledWidth != seq.MaxFrameWidth || fh.UpscaledHeight != seq.MaxFrameHeight;
            wb.WriteBit(frameSizeOverride ? 1 : 0);
            if (seq.EnableOrderHint) wb.WriteLiteral(fh.OrderHint, seq.OrderHintBitsMinus1 + 1);
            if (!fh.ErrorResilientMode && !fh.FrameIsIntraOnly) wb.WriteLiteral(fh.PrimaryRefFrame, 3);   // PRIMARY_REF_BITS
        }
        if ((fh.FrameType == KEY_FRAME && !fh.ShowFrame) || fh.FrameType == INTER_FRAME || fh.FrameType == INTRA_ONLY_FRAME)
            wb.WriteLiteral(fh.RefreshFrameFlags, REF_FRAMES);
        if (!fh.FrameIsIntraOnly || fh.RefreshFrameFlags != 0xff)
        {
            if (fh.ErrorResilientMode && seq.EnableOrderHint)
                for (int refIdx = 0; refIdx < REF_FRAMES; refIdx++)
                    wb.WriteLiteral(fh.RefFrameMap[refIdx]!.OrderHint, seq.OrderHintBitsMinus1 + 1);
        }
        if (fh.FrameType == KEY_FRAME || fh.FrameType == INTRA_ONLY_FRAME)
        {
            WriteFrameSizeGq(fh, cm, frameSizeOverride, wb);
            if (f.AllowScreenContentTools) wb.WriteBit(f.AllowIntrabc ? 1 : 0);
        }
        else
        {
            // INTER_FRAME (frame_refs_short_signaling 0: enable_ref_frame_mvs or no short signaling speed feature)
            if (seq.EnableOrderHint) wb.WriteBit(0);
            for (int refFrame = LAST_FRAME; refFrame <= ALTREF_FRAME; ++refFrame)
                wb.WriteLiteral(fh.RemappedRefIdx[refFrame - LAST_FRAME], REF_FRAMES_LOG2);
            if (!fh.ErrorResilientMode && frameSizeOverride)
            {
                // write_frame_size_with_refs
                bool found = false;
                for (int refFrame = LAST_FRAME; refFrame <= ALTREF_FRAME; ++refFrame)
                {
                    var b = fh.GetRefFrameBuf(refFrame);
                    if (b != null)
                    {
                        found = fh.UpscaledWidth == b.Width && fh.UpscaledHeight == b.Height;
                        found &= fh.RenderWidth == b.RenderWidth && fh.RenderHeight == b.RenderHeight;
                    }
                    wb.WriteBit(found ? 1 : 0);
                    if (found) break;   // (write_superres_scale: no superres)
                }
                if (!found) WriteFrameSizeGq(fh, cm, true, wb);
            }
            else WriteFrameSizeGq(fh, cm, frameSizeOverride, wb);
            if (!fh.CurFrameForceIntegerMv) wb.WriteBit(fh.AllowHighPrecisionMv ? 1 : 0);
            // write_frame_interp_filter
            wb.WriteBit(fh.InterpFilter == SWITCHABLE ? 1 : 0);
            if (fh.InterpFilter != SWITCHABLE) wb.WriteLiteral(fh.InterpFilter, LOG_SWITCHABLE_FILTERS);
            wb.WriteBit(fh.SwitchableMotionMode ? 1 : 0);
            // frame_might_allow_ref_frame_mvs
            if (!fh.ErrorResilientMode && seq.EnableRefFrameMvs && seq.EnableOrderHint && !fh.FrameIsIntraOnly)
                wb.WriteBit(fh.AllowRefFrameMvs ? 1 : 0);
        }
        bool mightBwdAdapt = !seq.ReducedStillPictureHdr && !fh.DisableCdfUpdate;
        if (mightBwdAdapt) wb.WriteBit(fh.RefreshFrameContextDisabled ? 1 : 0);
        // write_tile_info (uniform)
        wb.WriteBit(1);
        for (int ones = cm.Log2Cols - cm.MinLog2Cols; ones-- > 0;) wb.WriteBit(1);
        if (cm.Log2Cols < cm.MaxLog2Cols) wb.WriteBit(0);
        for (int ones = cm.Log2Rows - cm.MinLog2Rows; ones-- > 0;) wb.WriteBit(1);
        if (cm.Log2Rows < cm.MaxLog2Rows) wb.WriteBit(0);
        if (cm.TileRows * cm.TileCols > 1)
        {
            wb.WriteLiteral(contextUpdateTileId, cm.Log2Cols + cm.Log2Rows);
            wb.WriteLiteral(tileSizeBytes - 1, 2);
        }
        // encode_quantization
        wb.WriteLiteral(cm.BaseQindex, 8);
        WriteDeltaQ(wb, cm.YDcDeltaQ);
        if (cm.NumPlanes > 1)
        {
            bool diffUvDelta = cm.UDcDeltaQ != cm.VDcDeltaQ || cm.UAcDeltaQ != cm.VAcDeltaQ;
            WriteDeltaQ(wb, cm.UDcDeltaQ);
            WriteDeltaQ(wb, cm.UAcDeltaQ);
            if (diffUvDelta)
            {
                WriteDeltaQ(wb, cm.VDcDeltaQ);
                WriteDeltaQ(wb, cm.VAcDeltaQ);
            }
        }
        wb.WriteBit(cm.UsingQmatrix ? 1 : 0);
        if (cm.UsingQmatrix)
        {
            wb.WriteLiteral(cm.QmLevelY, 4);
            wb.WriteLiteral(cm.QmLevelU, 4);
        }
        wb.WriteBit(0);   // encode_segmentation: enabled
        if (cm.BaseQindex > 0)
        {
            wb.WriteBit(cpi.DeltaQPresentFlag ? 1 : 0);
            if (cpi.DeltaQPresentFlag)
            {
                wb.WriteLiteral(MostSignificantBit(cpi.DeltaQRes), 2);
                if (!f.AllowIntrabc) wb.WriteBit(0);   // delta_lf_present_flag
            }
        }
        if (!f.AllLossless)
        {
            if (!f.CodedLossless)
            {
                EncodeLoopfilterGq(f, fh, wb);
                EncodeCdef(f, wb);
            }
            EncodeRestorationMode(f, wb);
        }
        if (!f.CodedLossless) wb.WriteBit(f.TxMode == TX_MODE_SELECT ? 1 : 0);
        if (!fh.FrameIsIntraOnly) wb.WriteBit(fh.ReferenceSelect ? 1 : 0);
        if (fh.SkipModeAllowed) wb.WriteBit(fh.SkipModeFlag ? 1 : 0);
        // frame_might_allow_warped_motion
        if (!fh.ErrorResilientMode && !fh.FrameIsIntraOnly && seq.EnableWarpedMotion) wb.WriteBit(fh.AllowWarpedMotion ? 1 : 0);
        wb.WriteBit(f.ReducedTxSetUsed);
        if (!fh.FrameIsIntraOnly) WriteGlobalMotion(fh, wb);
        if (seq.FilmGrainParamsPresent && (fh.ShowFrame || fh.ShowableFrame) && seq.Color.FilmGrain is { } fg)
            WriteFilmGrainParams(fg, cm.Monochrome, !cm.Monochrome && cm.SsX == 1 && cm.SsY == 1, wb);
    }

    /// <summary>write_frame_size (+ write_superres_scale: no superres, + write_render_size).</summary>
    private static void WriteFrameSizeGq(AomGqFrameHeader fh, AomCommon cm, bool frameSizeOverride, AomWriteBitBuffer wb)
    {
        if (frameSizeOverride)
        {
            wb.WriteLiteral(fh.UpscaledWidth - 1, fh.Seq.NumBitsWidth);
            wb.WriteLiteral(fh.UpscaledHeight - 1, fh.Seq.NumBitsHeight);
        }
        // write_render_size: av1_resize_scaled (superres_upscaled size != render size)
        bool scalingActive = fh.UpscaledWidth != fh.RenderWidth || fh.UpscaledHeight != fh.RenderHeight;
        wb.WriteBit(scalingActive ? 1 : 0);
        if (scalingActive)
        {
            wb.WriteLiteral(fh.RenderWidth - 1, 16);
            wb.WriteLiteral(fh.RenderHeight - 1, 16);
        }
    }

    /// <summary>encode_loopfilter (is_mode_ref_delta_meaningful against the primary reference frame's deltas).</summary>
    private static void EncodeLoopfilterGq(FrameState f, AomGqFrameHeader fh, AomWriteBitBuffer wb)
    {
        if (f.AllowIntrabc) return;
        var lf = f.Lf;
        wb.WriteLiteral(lf.FilterLevel[0], 6);
        wb.WriteLiteral(lf.FilterLevel[1], 6);
        if (f.Cm.NumPlanes > 1 && (lf.FilterLevel[0] != 0 || lf.FilterLevel[1] != 0))
        {
            wb.WriteLiteral(lf.FilterLevelU, 6);
            wb.WriteLiteral(lf.FilterLevelV, 6);
        }
        wb.WriteLiteral(lf.SharpnessLevel, 3);
        wb.WriteBit(lf.ModeRefDeltaEnabled ? 1 : 0);
        bool meaningful = false;
        if (fh.ModeRefDeltaUpdate)
        {
            for (int i = 0; i < 8; i++) meaningful |= lf.RefDeltas[i] != fh.PrevRefDeltas[i];
            for (int i = 0; i < 2; i++) meaningful |= lf.ModeDeltas[i] != fh.PrevModeDeltas[i];
        }
        wb.WriteBit(meaningful ? 1 : 0);
        if (!meaningful) return;
        for (int i = 0; i < 8; i++)
        {
            bool changed = lf.RefDeltas[i] != fh.PrevRefDeltas[i];
            wb.WriteBit(changed ? 1 : 0);
            if (changed) wb.WriteInvSignedLiteral(lf.RefDeltas[i], 6);
        }
        for (int i = 0; i < 2; i++)
        {
            bool changed = lf.ModeDeltas[i] != fh.PrevModeDeltas[i];
            wb.WriteBit(changed ? 1 : 0);
            if (changed) wb.WriteInvSignedLiteral(lf.ModeDeltas[i], 6);
        }
    }

    /// <summary>write_global_motion.</summary>
    private static void WriteGlobalMotion(AomGqFrameHeader fh, AomWriteBitBuffer wb)
    {
        for (int frame = LAST_FRAME; frame <= ALTREF_FRAME; ++frame)
        {
            var refParams = fh.PrevGlobalMotion != null ? fh.PrevGlobalMotion[frame] : AomWarpedMotionParams.Default;
            WriteGlobalMotionParams(fh.GlobalMotion[frame], refParams, wb, fh.AllowHighPrecisionMv);
        }
    }

    /// <summary>write_global_motion_params.</summary>
    private static void WriteGlobalMotionParams(AomWarpedMotionParams p, AomWarpedMotionParams r, AomWriteBitBuffer wb, bool allowHp)
    {
        int type = p.WmType;
        wb.WriteBit(type != IDENTITY ? 1 : 0);
        if (type != IDENTITY)
        {
            wb.WriteBit(type == ROTZOOM ? 1 : 0);
            if (type != ROTZOOM) wb.WriteBit(type == TRANSLATION ? 1 : 0);
        }
        if (type >= ROTZOOM)
        {
            WriteSignedPrimitiveRefsubexpfin(wb, GM_ALPHA_MAX + 1, SUBEXPFIN_K, (r.WmMat[2] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS),
                (p.WmMat[2] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS));
            WriteSignedPrimitiveRefsubexpfin(wb, GM_ALPHA_MAX + 1, SUBEXPFIN_K, r.WmMat[3] >> GM_ALPHA_PREC_DIFF, p.WmMat[3] >> GM_ALPHA_PREC_DIFF);
        }
        if (type >= AFFINE)
        {
            WriteSignedPrimitiveRefsubexpfin(wb, GM_ALPHA_MAX + 1, SUBEXPFIN_K, r.WmMat[4] >> GM_ALPHA_PREC_DIFF, p.WmMat[4] >> GM_ALPHA_PREC_DIFF);
            WriteSignedPrimitiveRefsubexpfin(wb, GM_ALPHA_MAX + 1, SUBEXPFIN_K, (r.WmMat[5] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS),
                (p.WmMat[5] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS));
        }
        if (type >= TRANSLATION)
        {
            int transBits = type == TRANSLATION ? GM_ABS_TRANS_ONLY_BITS - (allowHp ? 0 : 1) : GM_ABS_TRANS_BITS;
            int transPrecDiff = type == TRANSLATION ? GM_TRANS_ONLY_PREC_DIFF + (allowHp ? 0 : 1) : GM_TRANS_PREC_DIFF;
            WriteSignedPrimitiveRefsubexpfin(wb, (1 << transBits) + 1, SUBEXPFIN_K, r.WmMat[0] >> transPrecDiff, p.WmMat[0] >> transPrecDiff);
            WriteSignedPrimitiveRefsubexpfin(wb, (1 << transBits) + 1, SUBEXPFIN_K, r.WmMat[1] >> transPrecDiff, p.WmMat[1] >> transPrecDiff);
        }
    }

    // ---- aom_dsp/binary_codes_writer.c (bit buffer variants) --------------------------------------------------------

    /// <summary>aom_wb_write_primitive_quniform.</summary>
    private static void WbWritePrimitiveQuniform(AomWriteBitBuffer wb, int n, int v)
    {
        if (n <= 1) return;
        int l = MostSignificantBit(n - 1) + 1;
        int m = (1 << l) - n;
        if (v < m) wb.WriteLiteral(v, l - 1);
        else
        {
            wb.WriteLiteral(m + ((v - m) >> 1), l - 1);
            wb.WriteBit((v - m) & 1);
        }
    }

    /// <summary>aom_wb_write_primitive_subexpfin.</summary>
    private static void WbWritePrimitiveSubexpfin(AomWriteBitBuffer wb, int n, int k, int v)
    {
        int i = 0, mk = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (n <= mk + 3 * a)
            {
                WbWritePrimitiveQuniform(wb, n - mk, v - mk);
                break;
            }
            int t = v >= mk + a ? 1 : 0;
            wb.WriteBit(t);
            if (t != 0)
            {
                i = i + 1;
                mk += a;
            }
            else
            {
                wb.WriteLiteral(v - mk, b);
                break;
            }
        }
    }

    /// <summary>aom_wb_write_signed_primitive_refsubexpfin.</summary>
    private static void WriteSignedPrimitiveRefsubexpfin(AomWriteBitBuffer wb, int n, int k, int r, int v)
    {
        int scaledN = (n << 1) - 1;
        // aom_wb_write_primitive_refsubexpfin(wb, scaled_n, k, ref + n - 1, v + n - 1)
        WbWritePrimitiveSubexpfin(wb, scaledN, k, RecenterFiniteNonneg(scaledN, r + n - 1, v + n - 1));
    }
}
