using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>The sequence-level settings of the packet that are not encoder decisions (libaom's oxcf / seq_params
/// for avifenc's all-intra still configuration).</summary>
internal sealed class AomSequenceConfig
{
    /// <summary>oxcf.input_cfg.init_framerate (g_timebase den / num; libaom's default timebase is 1/30).</summary>
    public double FrameRate = 30.0;
    /// <summary>CICP; 2/2/2 (unspecified) writes no color description.</summary>
    public int ColorPrimaries = 2, TransferCharacteristics = 2, MatrixCoefficients = 2;
    /// <summary>0 studio, 1 full (AV1E_SET_COLOR_RANGE AOM_CR_FULL_RANGE).</summary>
    public int ColorRange = 1;
    public int ChromaSamplePosition;
    /// <summary>Film grain parameters to signal (avifenc --film-grain / a grain table: libaom codes the frame unchanged
    /// and adds film_grain_params); null = none.</summary>
    public Av1FilmGrainData? FilmGrain;
}

// Port of libaom 3.14.1 av1/encoder/bitstream.c (av1_pack_bitstream and what it reaches) and encodetxb.c's coefficient
// writer for one shown all-intra key frame of a still picture (reduced still picture header, one tile, one tile
// group, no segmentation / delta q / CDEF / superres / film grain), plus the temporal delimiter av1_cx_iface.c
// prepends: the bytes libaom's aom_codec_get_cx_data returns for the frame.
internal static class AomBitstream
{
    private const int OBU_SEQUENCE_HEADER = 1, OBU_TEMPORAL_DELIMITER = 2, OBU_FRAME = 6;
    private const int MAX_MIB_MASK = 31;
    private const int ONLY_4X4 = 0;
    private const int NUM_BASE_LEVELS = 2, COEFF_BASE_RANGE = 12, BR_CDF_SIZE = 4;
    private const int SEQ_LEVEL_MAX = 31;

    /// <summary>The frame's packet (TD, sequence header OBU, frame OBU) for the encoder state left by
    /// <see cref="AomEncoder.EncodeFrame"/> and <see cref="AomEncoder.RunPostFilter"/>.</summary>
    internal static byte[] PackFrame(AomComp cpi, AomSequenceConfig? seqCfg = null, System.IO.TextWriter? trace = null)
    {
        seqCfg ??= new AomSequenceConfig();
        var cm = cpi.Cm;
        if (cpi.PostFilter == null) throw new InvalidOperationException("RunPostFilter must run before the bitstream is packed");
        var f = new FrameState(cpi, seqCfg);

        var outBuf = new System.IO.MemoryStream();
        // temporal delimiter (av1_cx_iface.c encoder_encode)
        outBuf.WriteByte(ObuHeader(OBU_TEMPORAL_DELIMITER));
        outBuf.WriteByte(0);
        // sequence header OBU (every key frame)
        byte[] seq = WriteSequenceHeaderObu(f);
        outBuf.WriteByte(ObuHeader(OBU_SEQUENCE_HEADER));
        WriteUleb(outBuf, (ulong)seq.Length);
        outBuf.Write(seq);
        // OBU_FRAME: the uncompressed header (no trailing bits, byte aligned) then the one tile group of one tile
        var wb = new AomWriteBitBuffer();
        WriteUncompressedHeader(f, wb);
        byte[] hdr = wb.ToArray();
        byte[] tile = WriteTile(f, trace);
        outBuf.WriteByte(ObuHeader(OBU_FRAME));
        WriteUleb(outBuf, (ulong)(hdr.Length + tile.Length));
        outBuf.Write(hdr);
        outBuf.Write(tile);
        return outBuf.ToArray();
    }

    /// <summary>av1_write_obu_header (no extension, has_size_field).</summary>
    private static byte ObuHeader(int type) => (byte)((type << 3) | (1 << 1));

    /// <summary>aom_uleb_encode with the minimal length.</summary>
    private static void WriteUleb(System.IO.Stream s, ulong v)
    {
        do
        {
            byte b = (byte)(v & 0x7f);
            v >>= 7;
            if (v != 0) b |= 0x80;
            s.WriteByte(b);
        } while (v != 0);
    }

    /// <summary>The frame-level state the writer derives from the encoder (cm->features, cm->seq_params, cm->tiles).</summary>
    private sealed class FrameState
    {
        public readonly AomComp Cpi;
        public readonly AomCommon Cm;
        public readonly AomSequenceConfig SeqCfg;
        public readonly int Profile, SeqLevelIdx, NumBitsWidth, NumBitsHeight;
        public readonly bool EnableFilterIntra, EnableIntraEdgeFilter, EnableRestoration, EnableCdef;
        public readonly bool AllowScreenContentTools, AllowIntrabc, CodedLossless, AllLossless;
        public readonly int TxMode, ReducedTxSetUsed;
        public readonly AomLoopFilterParams Lf;
        public readonly AomRestorationInfo[]? Rst;
        public readonly int[] FrameRestorationType = new int[3];
        public int Log2Cols, MinLog2Cols, MaxLog2Cols, Log2Rows, MinLog2Rows, MaxLog2Rows;

        public FrameState(AomComp cpi, AomSequenceConfig seqCfg)
        {
            Cpi = cpi; Cm = cpi.Cm; SeqCfg = seqCfg;
            var cm = Cm;
            // profile: 0 for 4:2:0 / monochrome, 1 for 4:4:4, 2 otherwise (8 / 10-bit); 12-bit is profile 2 only
            Profile = cm.BitDepth == 12 ? 2 : cm.Monochrome || (cm.SsX == 1 && cm.SsY == 1) ? 0 : (cm.SsX == 0 && cm.SsY == 0) ? 1 : 2;
            SeqLevelIdx = BitstreamLevel(cm.Width, cm.Height, seqCfg.FrameRate);
            NumBitsWidth = cm.Width > 1 ? MostSignificantBit(cm.Width - 1) + 1 : 1;
            NumBitsHeight = cm.Height > 1 ? MostSignificantBit(cm.Height - 1) + 1 : 1;
            EnableFilterIntra = cpi.EnableFilterIntra;
            EnableIntraEdgeFilter = cpi.EnableIntraEdgeFilter;
            EnableRestoration = cpi.EnableRestoration;
            EnableCdef = cpi.CdefControl != 0;   // seq_params->enable_cdef = cdef_control != CDEF_NONE
            AllowScreenContentTools = cpi.AllowScreenContentTools;
            // encode_frame_internal: "If intrabc is allowed but never selected, reset the allow_intrabc flag."
            AllowIntrabc = cpi.AllowScreenContentTools && cpi.AllowIntrabc && AnyIntrabcBlock(cm);
            CodedLossless = cm.BaseQindex == 0;   // (delta q is never present at base_qindex 0): lossless iff base_qindex is 0
            AllLossless = CodedLossless;           // no superres
            ReducedTxSetUsed = cpi.ReducedTxSetUsed;
            // encode_frame_internal's select_tx_mode, then av1_encode_frame's TX_MODE_SELECT -> TX_MODE_LARGEST
            var sf = cpi.Sf;
            int evalType = sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch != 0 ? WINNER_MODE_EVAL : DEFAULT_EVAL;
            TxMode = AomRdoptUtils.SelectTxMode(CodedLossless, cpi.WinnerModeParams.tx_size_search_methods[evalType]);
            if (TxMode == TX_MODE_SELECT && cpi.TxbSplitCount == 0) TxMode = TX_MODE_LARGEST;
            Lf = cpi.PostFilter!.LoopFilter;
            Rst = cpi.PostFilter.Restoration;
            for (int p = 0; p < 3; p++)
                FrameRestorationType[p] = Rst != null && p < Rst.Length && EnableRestoration && !AllLossless && !AllowIntrabc
                    ? Rst[p].FrameRestorationType : AomRestoration.RestoreNone;
            SetTileInfo();
        }

        /// <summary>av1_get_tile_limits + set_tile_info (tile_columns = tile_rows = 0, uniform spacing).</summary>
        private void SetTileInfo()
        {
            var cm = Cm;
            int sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2, sbRows = (cm.MiRows + cm.MibSize - 1) >> cm.MibSizeLog2;
            int sbSizeLog2 = cm.MibSizeLog2 + 2;
            int maxWidthSb = 4096 >> sbSizeLog2;                       // MAX_TILE_WIDTH
            int maxTileAreaSb = (4096 * 2304) >> (2 * sbSizeLog2);      // MAX_TILE_AREA
            MinLog2Cols = TileLog2(maxWidthSb, sbCols);
            MaxLog2Cols = TileLog2(1, Math.Min(sbCols, 64));
            MaxLog2Rows = TileLog2(1, Math.Min(sbRows, 64));
            int minLog2 = Math.Max(TileLog2(maxTileAreaSb, sbCols * sbRows), MinLog2Cols);
            Log2Cols = MinLog2Cols;
            int k = 0;
            for (; (maxWidthSb << k) <= sbCols; ++k) { }
            Log2Cols = Math.Min(Math.Max(Log2Cols, k), MaxLog2Cols);
            MinLog2Rows = Math.Max(minLog2 - Log2Cols, 0);
            Log2Rows = Math.Min(Math.Max(0, MinLog2Rows), MaxLog2Rows);
            int colSb = (sbCols + (1 << Log2Cols) - 1) >> Log2Cols, rowSb = (sbRows + (1 << Log2Rows) - 1) >> Log2Rows;
            if (colSb < sbCols || rowSb < sbRows)
                throw new NotSupportedException("frames needing more than one tile are not supported by the port");
        }

        /// <summary>cpi->intrabc_used: any block of the frame coded with intrabc.</summary>
        private static bool AnyIntrabcBlock(AomCommon cm)
        {
            for (int r = 0; r < cm.MiRows; r++)
                for (int c = 0; c < cm.MiCols; c++)
                    if ((cm.MiGridBase[r * cm.MiStride + c]?.UseIntrabc ?? 0) != 0) return true;
            return false;
        }

        private static int TileLog2(int blkSize, int target)
        {
            int k = 0;
            for (; (blkSize << k) < target; k++) { }
            return k;
        }
    }

    private static int MostSignificantBit(int v) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    // ---- sequence header -----------------------------------------------------------------------------------------

    /// <summary>set_bitstream_level_tier (no target level).</summary>
    private static int BitstreamLevel(int width, int height, double fps)
    {
        static bool Match(int w, int h, double fps, int lw, int lh, double lfps, int mult)
        {
            long lvlLumaPels = (long)lw * lh;
            double lvlDisplaySampleRate = lvlLumaPels * lfps;
            long lumaPels = (long)w * h;
            double displaySampleRate = lumaPels * fps;
            return lumaPels <= lvlLumaPels && displaySampleRate <= lvlDisplaySampleRate && w <= lw * mult && h <= lh * mult;
        }
        if (Match(width, height, fps, 512, 288, 30.0, 4)) return 0;      // 2.0
        if (Match(width, height, fps, 704, 396, 30.0, 4)) return 1;      // 2.1
        if (Match(width, height, fps, 1088, 612, 30.0, 4)) return 4;     // 3.0
        if (Match(width, height, fps, 1376, 774, 30.0, 4)) return 5;     // 3.1
        if (Match(width, height, fps, 2048, 1152, 30.0, 3)) return 8;    // 4.0
        if (Match(width, height, fps, 2048, 1152, 60.0, 3)) return 9;    // 4.1
        if (Match(width, height, fps, 4096, 2176, 30.0, 2)) return 12;   // 5.0
        if (Match(width, height, fps, 4096, 2176, 60.0, 2)) return 13;   // 5.1
        if (Match(width, height, fps, 4096, 2176, 120.0, 2)) return 14;  // 5.2
        if (Match(width, height, fps, 8192, 4352, 30.0, 2)) return 16;   // 6.0
        if (Match(width, height, fps, 8192, 4352, 60.0, 2)) return 17;   // 6.1
        if (Match(width, height, fps, 8192, 4352, 120.0, 2)) return 18;  // 6.2
        return SEQ_LEVEL_MAX;
    }

    /// <summary>av1_write_sequence_header_obu (still_picture, reduced_still_picture_hdr).</summary>
    private static byte[] WriteSequenceHeaderObu(FrameState f)
    {
        var cm = f.Cm;
        var wb = new AomWriteBitBuffer();
        wb.WriteLiteral(f.Profile, 3);          // write_profile
        wb.WriteBit(1);                          // still_picture
        wb.WriteBit(1);                          // reduced_still_picture_hdr
        wb.WriteLiteral(f.SeqLevelIdx, 5);       // write_bitstream_level
        // write_sequence_header
        wb.WriteLiteral(f.NumBitsWidth - 1, 4);
        wb.WriteLiteral(f.NumBitsHeight - 1, 4);
        wb.WriteLiteral(cm.Width - 1, f.NumBitsWidth);
        wb.WriteLiteral(cm.Height - 1, f.NumBitsHeight);
        wb.WriteBit(cm.SbSize == BLOCK_128X128 ? 1 : 0);   // write_sb_size
        wb.WriteBit(f.EnableFilterIntra ? 1 : 0);
        wb.WriteBit(f.EnableIntraEdgeFilter ? 1 : 0);
        wb.WriteBit(0);                                    // enable_superres
        wb.WriteBit(f.EnableCdef ? 1 : 0);
        wb.WriteBit(f.EnableRestoration ? 1 : 0);
        WriteColorConfig(f, wb);
        wb.WriteBit(f.SeqCfg.FilmGrain != null ? 1 : 0);   // film_grain_params_present
        AddTrailingBits(wb);
        return wb.ToArray();
    }

    /// <summary>write_color_config.</summary>
    private static void WriteColorConfig(FrameState f, AomWriteBitBuffer wb)
    {
        var c = f.SeqCfg;
        var cm = f.Cm;
        // write_bitdepth: profile 0 / 1: [0] 8-bit, [1] 10-bit; profile 2: [0] 8-bit, [10] 10-bit, [11] 12-bit
        wb.WriteBit(cm.BitDepth == 8 ? 0 : 1);
        if (f.Profile == 2 && cm.BitDepth != 8) wb.WriteBit(cm.BitDepth == 10 ? 0 : 1);
        if (f.Profile != 1) wb.WriteBit(cm.Monochrome ? 1 : 0);
        if (c.ColorPrimaries == 2 && c.TransferCharacteristics == 2 && c.MatrixCoefficients == 2) wb.WriteBit(0);
        else
        {
            wb.WriteBit(1);
            wb.WriteLiteral(c.ColorPrimaries, 8);
            wb.WriteLiteral(c.TransferCharacteristics, 8);
            wb.WriteLiteral(c.MatrixCoefficients, 8);
        }
        if (cm.Monochrome)
        {
            wb.WriteBit(c.ColorRange);
            return;
        }
        if (c.ColorPrimaries == 1 && c.TransferCharacteristics == 13 && c.MatrixCoefficients == 0) { }   // sRGB identity: 4:4:4 implied
        else
        {
            wb.WriteBit(c.ColorRange);
            // profile 2: 12-bit codes its subsampling (4:2:0 / 4:2:2 / 4:4:4); 8 / 10-bit is 4:2:2 only
            if (f.Profile == 2 && cm.BitDepth == 12)
            {
                wb.WriteBit(cm.SsX);
                if (cm.SsX != 0) wb.WriteBit(cm.SsY);
            }
            if (cm.SsX == 1 && cm.SsY == 1) wb.WriteLiteral(c.ChromaSamplePosition, 2);
        }
        wb.WriteBit(0);                                    // separate_uv_delta_q
    }

    /// <summary>add_trailing_bits.</summary>
    private static void AddTrailingBits(AomWriteBitBuffer wb)
    {
        if (wb.IsByteAligned) wb.WriteLiteral(0x80, 8);
        else wb.WriteBit(1);
    }

    // ---- uncompressed frame header ---------------------------------------------------------------------------------

    /// <summary>write_uncompressed_header_obu for the shown key frame of a reduced still picture header.</summary>
    private static void WriteUncompressedHeader(FrameState f, AomWriteBitBuffer wb)
    {
        var cpi = f.Cpi;
        var cm = f.Cm;
        wb.WriteBit(cpi.DisableCdfUpdate ? 1 : 0);
        wb.WriteBit(f.AllowScreenContentTools ? 1 : 0);    // seq force_screen_content_tools == 2
        if (f.AllowScreenContentTools) wb.WriteBit(0);     // cur_frame_force_integer_mv (seq force_integer_mv == 2; 0 for intra)
        // KEY_FRAME: write_frame_size (frame_size_override_flag 0: no superres scale, render size = frame size)
        wb.WriteBit(0);                                     // write_render_size: scaling_active
        if (f.AllowScreenContentTools) wb.WriteBit(f.AllowIntrabc ? 1 : 0);
        // write_tile_info (uniform spacing, one tile)
        wb.WriteBit(1);
        for (int ones = f.Log2Cols - f.MinLog2Cols; ones-- > 0;) wb.WriteBit(1);
        if (f.Log2Cols < f.MaxLog2Cols) wb.WriteBit(0);
        for (int ones = f.Log2Rows - f.MinLog2Rows; ones-- > 0;) wb.WriteBit(1);
        if (f.Log2Rows < f.MaxLog2Rows) wb.WriteBit(0);
        // encode_quantization (separate_uv_delta_q 0)
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
        wb.WriteBit(0);                                     // encode_segmentation: enabled
        if (cm.BaseQindex > 0)
        {
            wb.WriteBit(cpi.DeltaQPresentFlag ? 1 : 0);
            if (cpi.DeltaQPresentFlag)
            {
                wb.WriteLiteral(MostSignificantBit(cpi.DeltaQRes), 2);
                if (!f.AllowIntrabc) wb.WriteBit(0);       // delta_lf_present_flag
            }
        }
        if (!f.AllLossless)
        {
            if (!f.CodedLossless)
            {
                EncodeLoopfilter(f, wb);
                EncodeCdef(f, wb);
            }
            EncodeRestorationMode(f, wb);
        }
        if (!f.CodedLossless) wb.WriteBit(f.TxMode == TX_MODE_SELECT ? 1 : 0);
        wb.WriteBit(f.ReducedTxSetUsed);
        if (f.SeqCfg.FilmGrain is { } fg) WriteFilmGrainParams(fg, cm.Monochrome, !cm.Monochrome && cm.SsX == 1 && cm.SsY == 1, wb);
    }

    /// <summary>write_film_grain_params for a shown key frame: apply_grain, grain_seed (update_grain implied), the
    /// parameter set; chroma point counts absent for mono, chroma-from-luma and 4:2:0 without luma points.</summary>
    private static unsafe void WriteFilmGrainParams(Av1FilmGrainData fg, bool mono, bool is420, AomWriteBitBuffer wb)
    {
        wb.WriteBit(1);                                    // apply_grain
        wb.WriteLiteral((int)(fg.Seed & 0xFFFF), 16);
        wb.WriteLiteral(fg.NumYPoints, 4);
        for (int i = 0; i < fg.NumYPoints; i++) { wb.WriteLiteral(fg.YPoints[i * 2], 8); wb.WriteLiteral(fg.YPoints[i * 2 + 1], 8); }
        if (!mono) wb.WriteBit(fg.ChromaScalingFromLuma != 0 ? 1 : 0);
        if (!mono && fg.ChromaScalingFromLuma == 0 && !(fg.NumYPoints == 0 && is420))
        {
            wb.WriteLiteral(fg.NumUvPoints0, 4);
            for (int i = 0; i < fg.NumUvPoints0; i++) { wb.WriteLiteral(fg.UvPoints[i * 2], 8); wb.WriteLiteral(fg.UvPoints[i * 2 + 1], 8); }
            wb.WriteLiteral(fg.NumUvPoints1, 4);
            for (int i = 0; i < fg.NumUvPoints1; i++) { wb.WriteLiteral(fg.UvPoints[20 + i * 2], 8); wb.WriteLiteral(fg.UvPoints[21 + i * 2], 8); }
        }
        wb.WriteLiteral(fg.ScalingShift - 8, 2);
        wb.WriteLiteral(fg.ArCoeffLag, 2);
        int numPosLuma = 2 * fg.ArCoeffLag * (fg.ArCoeffLag + 1);
        int numPosChroma = numPosLuma + (fg.NumYPoints > 0 ? 1 : 0);
        if (fg.NumYPoints > 0)
            for (int i = 0; i < numPosLuma; i++) wb.WriteLiteral(fg.ArCoeffsY[i] + 128, 8);
        if (fg.NumUvPoints0 > 0 || fg.ChromaScalingFromLuma != 0)
            for (int i = 0; i < numPosChroma; i++) wb.WriteLiteral(fg.ArCoeffsUv[i] + 128, 8);
        if (fg.NumUvPoints1 > 0 || fg.ChromaScalingFromLuma != 0)
            for (int i = 0; i < numPosChroma; i++) wb.WriteLiteral(fg.ArCoeffsUv[28 + i] + 128, 8);
        wb.WriteLiteral((int)(fg.ArCoeffShift - 6), 2);
        wb.WriteLiteral(fg.GrainScaleShift, 2);
        if (fg.NumUvPoints0 > 0) { wb.WriteLiteral(fg.UvMult0 + 128, 8); wb.WriteLiteral(fg.UvLumaMult0 + 128, 8); wb.WriteLiteral(fg.UvOffset0 + 256, 9); }
        if (fg.NumUvPoints1 > 0) { wb.WriteLiteral(fg.UvMult1 + 128, 8); wb.WriteLiteral(fg.UvLumaMult1 + 128, 8); wb.WriteLiteral(fg.UvOffset1 + 256, 9); }
        wb.WriteBit(fg.OverlapFlag != 0 ? 1 : 0);
        wb.WriteBit(fg.ClipToRestrictedRange != 0 ? 1 : 0);
    }

    /// <summary>write_delta_q (frame header).</summary>
    private static void WriteDeltaQ(AomWriteBitBuffer wb, int deltaQ)
    {
        if (deltaQ != 0)
        {
            wb.WriteBit(1);
            wb.WriteInvSignedLiteral(deltaQ, 6);
        }
        else wb.WriteBit(0);
    }

    /// <summary>encode_cdef.</summary>
    private static void EncodeCdef(FrameState f, AomWriteBitBuffer wb)
    {
        if (!f.EnableCdef || f.AllowIntrabc) return;
        var ci = f.Cpi.PostFilter!.Cdef!;
        wb.WriteLiteral(ci.CdefDamping - 3, 2);
        wb.WriteLiteral(ci.CdefBits, 2);
        for (int i = 0; i < ci.NbCdefStrengths; i++)
        {
            wb.WriteLiteral(ci.CdefStrengths[i], 6);
            if (f.Cm.NumPlanes > 1) wb.WriteLiteral(ci.CdefUvStrengths[i], 6);
        }
    }

    /// <summary>encode_loopfilter.</summary>
    private static void EncodeLoopfilter(FrameState f, AomWriteBitBuffer wb)
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
        // is_mode_ref_delta_meaningful: against the defaults (no primary reference frame)
        sbyte[] defRef = { 1, 0, 0, 0, -1, 0, -1, -1 };
        bool meaningful = false;
        for (int i = 0; i < 8; i++) meaningful |= lf.RefDeltas[i] != defRef[i];
        for (int i = 0; i < 2; i++) meaningful |= lf.ModeDeltas[i] != 0;
        wb.WriteBit(meaningful ? 1 : 0);
        if (!meaningful) return;
        for (int i = 0; i < 8; i++)
        {
            bool changed = lf.RefDeltas[i] != defRef[i];
            wb.WriteBit(changed ? 1 : 0);
            if (changed) wb.WriteInvSignedLiteral(lf.RefDeltas[i], 6);
        }
        for (int i = 0; i < 2; i++)
        {
            bool changed = lf.ModeDeltas[i] != 0;
            wb.WriteBit(changed ? 1 : 0);
            if (changed) wb.WriteInvSignedLiteral(lf.ModeDeltas[i], 6);
        }
    }

    /// <summary>encode_restoration_mode.</summary>
    private static void EncodeRestorationMode(FrameState f, AomWriteBitBuffer wb)
    {
        if (!f.EnableRestoration || f.AllowIntrabc) return;
        var cm = f.Cm;
        int numPlanes = cm.NumPlanes;
        bool allNone = true, chromaNone = true;
        for (int p = 0; p < numPlanes; ++p)
        {
            int t = f.FrameRestorationType[p];
            if (t != AomRestoration.RestoreNone)
            {
                allNone = false;
                chromaNone &= p == 0;
            }
            switch (t)
            {
                case AomRestoration.RestoreNone: wb.WriteBit(0); wb.WriteBit(0); break;
                case AomRestoration.RestoreWiener: wb.WriteBit(1); wb.WriteBit(0); break;
                case AomRestoration.RestoreSgrproj: wb.WriteBit(1); wb.WriteBit(1); break;
                default: wb.WriteBit(0); wb.WriteBit(1); break;   // RESTORE_SWITCHABLE
            }
        }
        var rst = f.Rst!;
        if (!allNone)
        {
            int sbSize = cm.SbSize == BLOCK_128X128 ? 128 : 64;
            int size0 = rst[0].RestorationUnitSize;
            if (sbSize == 64) wb.WriteBit(size0 > 64 ? 1 : 0);
            if (size0 > 64) wb.WriteBit(size0 > 128 ? 1 : 0);
        }
        if (numPlanes > 1)
        {
            int s = Math.Min(cm.SsX, cm.SsY);
            if (s != 0 && !chromaNone) wb.WriteBit(rst[1].RestorationUnitSize != rst[0].RestorationUnitSize ? 1 : 0);
        }
    }

    // ---- tile data ---------------------------------------------------------------------------------------------------

    /// <summary>The writer's thread data: MACROBLOCKD (its own above / left contexts), the tile's adaptive CDFs, the
    /// palette token cursor and the loop restoration reference filters (xd->wiener_info / sgrproj_info).</summary>
    private sealed class TileWriter
    {
        public required FrameState F;
        public required AomComp Cpi;
        public required AomCommon Cm;
        public required AomWriter W;
        public required Av1CdfContext Fc;
        public required AomMacroblockD Xd;
        public int TokIdx;
        public AomCbCoeffBuffer Cb = null!;
        public readonly AomWienerInfo[] RefWiener = new AomWienerInfo[3];
        public readonly AomSgrprojInfo[] RefSgrproj = new AomSgrprojInfo[3];
        public readonly byte[] Levels = new byte[AomTxb.TxPad2d];
        public readonly sbyte[] CoeffContexts = new sbyte[64 * 64];
        public readonly bool[] CdefTransmitted = new bool[4];
    }

    /// <summary>av1_pack_tile_info for the frame's single tile: write_modes then aom_stop_encode.</summary>
    private static byte[] WriteTile(FrameState f, System.IO.TextWriter? trace)
    {
        var cpi = f.Cpi;
        var cm = f.Cm;
        // av1_finalize_encoded_frame: the tile contexts restart from cm->fc
        var fc = new Av1CdfContext();
        fc.CopyFrom(cm.Fc);
        var w = new AomWriter { AllowUpdateCdf = !cpi.DisableCdfUpdate, Trace = trace };
        var xd = new AomMacroblockD();
        for (int p = 0; p < 3; p++)
        {
            xd.Plane[p].PlaneType = p == 0 ? 0 : 1;
            xd.Plane[p].SubsamplingX = p == 0 ? 0 : cm.SsX;
            xd.Plane[p].SubsamplingY = p == 0 ? 0 : cm.SsY;
        }
        for (int i = 0; i < 8; i++) xd.Lossless[i] = f.CodedLossless ? 1 : 0;
        int alignedMiCols = (cm.MiCols + 31) & ~31;
        xd.AbovePartitionContext = new byte[alignedMiCols];
        xd.AboveTxfmContext = new byte[alignedMiCols];
        xd.MiGrid = cm.MiGridBase;
        xd.MiStride = cm.MiStride;
        xd.TxTypeMap = cm.TxTypeMap;
        xd.TxTypeMapStride = cm.MiStride;
        var t = new TileWriter { F = f, Cpi = cpi, Cm = cm, W = w, Fc = fc, Xd = xd };
        xd.CurrentBaseQindex = cm.BaseQindex;

        // av1_reset_loop_restoration
        for (int p = 0; p < cm.NumPlanes; ++p)
        {
            AomRestoration.SetDefaultWiener(ref t.RefWiener[p]);
            AomRestoration.SetDefaultSgrproj(ref t.RefSgrproj[p]);
        }

        // write_modes: av1_zero_above_context
        Array.Clear(xd.AbovePartitionContext);
        Array.Fill(xd.AboveTxfmContext, (byte)TxSizeWide[TX_64X64]);
        int sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2;
        for (int miRow = 0; miRow < cm.MiRows; miRow += cm.MibSize)
        {
            // av1_zero_left_context
            Array.Clear(xd.LeftPartitionContext);
            Array.Fill(xd.LeftTxfmContextBuffer, (byte)TxSizeHigh[TX_64X64]);
            for (int miCol = 0, sbCol = 0; miCol < cm.MiCols; miCol += cm.MibSize, sbCol++)
            {
                t.Cb = cpi.CbCoeffBuffers[(miRow >> cm.MibSizeLog2) * sbCols + sbCol];
                WriteModesSb(t, miRow, miCol, cm.SbSize);
            }
        }
        if (t.TokIdx != cpi.PaletteTokens.Count)
            throw new InvalidOperationException($"palette tokens: wrote {t.TokIdx} of {cpi.PaletteTokens.Count}");
        return w.Finish();
    }

    /// <summary>get_partition: the partition of the block at (miRow, miCol) of size bsize, read from the mi grid.</summary>
    private static int GetPartition(AomCommon cm, int miRow, int miCol, int bsize)
    {
        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return -1;   // PARTITION_INVALID
        int offset = miRow * cm.MiStride + miCol;
        var mi = cm.MiGridBase;
        int subsize = mi[offset]!.Bsize;
        if (subsize == bsize) return PARTITION_NONE;
        int bhigh = MiSizeHigh[bsize], bwide = MiSizeWide[bsize];
        int sshigh = MiSizeHigh[subsize], sswide = MiSizeWide[subsize];
        if (bsize > BLOCK_8X8 && miRow + bwide / 2 < cm.MiRows && miCol + bhigh / 2 < cm.MiCols)
        {
            var mbmiRight = mi[offset + bwide / 2]!;
            var mbmiBelow = mi[offset + bhigh / 2 * cm.MiStride]!;
            if (sswide == bwide)
            {
                if (sshigh * 4 == bhigh) return PARTITION_HORZ_4;
                return mbmiBelow.Bsize == subsize ? PARTITION_HORZ : PARTITION_HORZ_B;
            }
            if (sshigh == bhigh)
            {
                if (sswide * 4 == bwide) return PARTITION_VERT_4;
                return mbmiRight.Bsize == subsize ? PARTITION_VERT : PARTITION_VERT_B;
            }
            if (sswide * 2 != bwide || sshigh * 2 != bhigh) return PARTITION_SPLIT;
            if (MiSizeWide[mbmiBelow.Bsize] == bwide) return PARTITION_HORZ_A;
            if (MiSizeHigh[mbmiRight.Bsize] == bhigh) return PARTITION_VERT_A;
            return PARTITION_SPLIT;
        }
        int vertSplit = sswide < bwide ? 1 : 0, horzSplit = sshigh < bhigh ? 1 : 0;
        return (vertSplit << 1 | horzSplit) switch { 1 => PARTITION_HORZ, 2 => PARTITION_VERT, 3 => PARTITION_SPLIT, _ => -1 };
    }

    /// <summary>write_modes_sb.</summary>
    private static void WriteModesSb(TileWriter t, int miRow, int miCol, int bsize)
    {
        var cm = t.Cm;
        var xd = t.Xd;
        int hbs = MiSizeWide[bsize] / 2;
        int quarterStep = MiSizeWide[bsize] / 4;
        int partition = GetPartition(cm, miRow, miCol, bsize);
        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return;
        int subsize = AomEncodeFrame.PartitionSubsize(bsize, partition);

        for (int plane = 0; plane < cm.NumPlanes; ++plane)
        {
            if (t.F.FrameRestorationType[plane] == AomRestoration.RestoreNone) continue;
            if (LoopRestorationCornersInSb(t, plane, miRow, miCol, bsize, out int rcol0, out int rcol1, out int rrow0, out int rrow1))
            {
                int rstride = t.F.Rst![plane].HorzUnits;
                for (int rrow = rrow0; rrow < rrow1; ++rrow)
                    for (int rcol = rcol0; rcol < rcol1; ++rcol)
                        LoopRestorationWriteSbCoeffs(t, rcol + rrow * rstride, plane);
            }
        }

        WritePartition(t, hbs, miRow, miCol, partition, bsize);
        switch (partition)
        {
            case PARTITION_NONE:
                WriteModesB(t, miRow, miCol);
                break;
            case PARTITION_HORZ:
                WriteModesB(t, miRow, miCol);
                if (miRow + hbs < cm.MiRows) WriteModesB(t, miRow + hbs, miCol);
                break;
            case PARTITION_VERT:
                WriteModesB(t, miRow, miCol);
                if (miCol + hbs < cm.MiCols) WriteModesB(t, miRow, miCol + hbs);
                break;
            case PARTITION_SPLIT:
                WriteModesSb(t, miRow, miCol, subsize);
                WriteModesSb(t, miRow, miCol + hbs, subsize);
                WriteModesSb(t, miRow + hbs, miCol, subsize);
                WriteModesSb(t, miRow + hbs, miCol + hbs, subsize);
                break;
            case PARTITION_HORZ_A:
                WriteModesB(t, miRow, miCol);
                WriteModesB(t, miRow, miCol + hbs);
                WriteModesB(t, miRow + hbs, miCol);
                break;
            case PARTITION_HORZ_B:
                WriteModesB(t, miRow, miCol);
                WriteModesB(t, miRow + hbs, miCol);
                WriteModesB(t, miRow + hbs, miCol + hbs);
                break;
            case PARTITION_VERT_A:
                WriteModesB(t, miRow, miCol);
                WriteModesB(t, miRow + hbs, miCol);
                WriteModesB(t, miRow, miCol + hbs);
                break;
            case PARTITION_VERT_B:
                WriteModesB(t, miRow, miCol);
                WriteModesB(t, miRow, miCol + hbs);
                WriteModesB(t, miRow + hbs, miCol + hbs);
                break;
            case PARTITION_HORZ_4:
                for (int i = 0; i < 4; ++i)
                {
                    int thisMiRow = miRow + i * quarterStep;
                    if (i > 0 && thisMiRow >= cm.MiRows) break;
                    WriteModesB(t, thisMiRow, miCol);
                }
                break;
            case PARTITION_VERT_4:
                for (int i = 0; i < 4; ++i)
                {
                    int thisMiCol = miCol + i * quarterStep;
                    if (i > 0 && thisMiCol >= cm.MiCols) break;
                    WriteModesB(t, miRow, thisMiCol);
                }
                break;
            default: throw new InvalidOperationException($"invalid partition {partition}");
        }
        AomEncodeFrame.UpdateExtPartitionContext(xd, miRow, miCol, subsize, bsize, partition);
    }

    /// <summary>write_partition, with libaom's partition_gather_{vert,horz}_alike on its CDF memory layout.</summary>
    private static void WritePartition(TileWriter t, int hbs, int miRow, int miCol, int p, int bsize)
    {
        if (bsize < BLOCK_8X8) return;   // is_partition_point
        var cm = t.Cm;
        bool hasRows = (miRow + hbs) < cm.MiRows;
        bool hasCols = (miCol + hbs) < cm.MiCols;
        int ctx = AomEncodeFrame.PartitionPlaneContext(t.Xd, miRow, miCol, bsize);
        ushort[] cdf = t.Fc.Mode.Partition[(4 - (ctx >> 2)) * 4 + (ctx & 3)];
        int nsymbs = AomEncodeFrame.PartitionCdfLength(bsize);
        if (!hasRows && !hasCols) return;   // PARTITION_SPLIT implied
        if (hasRows && hasCols)
        {
            t.W.WriteSymbol(p, cdf, nsymbs);
            return;
        }
        // libaom's partition_cdf[ctx] row: the nsymbs - 1 inverse CDF values, AOM_ICDF(32768) = 0, the adaptation
        // counter, then zeros up to CDF_SIZE(EXT_PARTITION_TYPES). The gathers read that memory as it is, so for an
        // 8x8 block (4 symbols) the elements past the alphabet (the terminal 0 and the counter) take part.
        Span<ushort> inCdf = stackalloc ushort[11];
        inCdf.Clear();
        for (int i = 0; i < nsymbs - 1; i++) inCdf[i] = cdf[i];
        inCdf[nsymbs] = cdf[nsymbs - 1];
        Span<ushort> gathered = stackalloc ushort[2];
        ushort o = 32768;
        if (!hasRows)
        {
            // partition_gather_vert_alike
            o -= ElementProb(inCdf, PARTITION_VERT);
            o -= ElementProb(inCdf, PARTITION_SPLIT);
            o -= ElementProb(inCdf, PARTITION_HORZ_A);
            o -= ElementProb(inCdf, PARTITION_VERT_A);
            o -= ElementProb(inCdf, PARTITION_VERT_B);
            if (bsize != BLOCK_128X128) o -= ElementProb(inCdf, PARTITION_VERT_4);
        }
        else
        {
            // partition_gather_horz_alike
            o -= ElementProb(inCdf, PARTITION_HORZ);
            o -= ElementProb(inCdf, PARTITION_SPLIT);
            o -= ElementProb(inCdf, PARTITION_HORZ_A);
            o -= ElementProb(inCdf, PARTITION_HORZ_B);
            o -= ElementProb(inCdf, PARTITION_VERT_A);
            if (bsize != BLOCK_128X128) o -= ElementProb(inCdf, PARTITION_HORZ_4);
        }
        gathered[0] = (ushort)(32768 - o);   // AOM_ICDF
        gathered[1] = 0;
        t.W.WriteCdf(p == PARTITION_SPLIT ? 1 : 0, gathered, 2);
    }

    /// <summary>cdf_element_prob (uint16 arithmetic).</summary>
    private static ushort ElementProb(ReadOnlySpan<ushort> cdf, int element)
        => (ushort)((element > 0 ? cdf[element - 1] : 32768) - cdf[element]);

    /// <summary>write_modes_b (intra frame).</summary>
    private static void WriteModesB(TileWriter t, int miRow, int miCol)
    {
        var cm = t.Cm;
        var cpi = t.Cpi;
        var xd = t.Xd;
        var w = t.W;
        int gridIdx = miRow * cm.MiStride + miCol;
        xd.MiOffset = gridIdx;
        xd.Mi0 = cm.MiGridBase[gridIdx]!;
        xd.TxTypeMapOffset = gridIdx;
        var mbmi = xd.Mi0;
        int bsize = mbmi.Bsize;
        int bh = MiSizeHigh[bsize], bw = MiSizeWide[bsize];
        AomEncodeFrame.SetMiRowCol(xd, cm, miRow, bh, miCol, bw);
        xd.AboveTxfmContextOffset = miCol;
        xd.LeftTxfmContextOffset = miRow & MAX_MIB_MASK;

        WriteMbModesKf(t);

        for (int plane = 0; plane < Math.Min(2, cm.NumPlanes); ++plane)
        {
            int n = plane == 0 ? mbmi.Palette.PaletteSize0 : mbmi.Palette.PaletteSize1;
            if (n > 0)
            {
                AomRdoptUtils.GetBlockDimensions(bsize, plane, xd, out _, out _, out int rows, out int cols);
                PackMapTokens(t, plane, n, rows * cols);
            }
        }

        bool isInterTx = AomEncodeMb.IsInterBlock(mbmi);
        if (t.F.TxMode == TX_MODE_SELECT && AomTxSearch.BlockSignalsTxsize(bsize) && !(isInterTx && mbmi.SkipTxfm != 0) &&
            xd.Lossless[mbmi.SegmentId] == 0)
        {
            if (isInterTx)
            {
                int maxTxSize = AomTxSearch.GetVartxMaxTxsize(xd, bsize, 0);
                int txbh = TxSizeHighUnit[maxTxSize], txbw = TxSizeWideUnit[maxTxSize];
                for (int idy = 0; idy < MiSizeHigh[bsize]; idy += txbh)
                    for (int idx = 0; idx < MiSizeWide[bsize]; idx += txbw) WriteTxSizeVartx(t, maxTxSize, 0, idy, idx);
            }
            else
            {
                WriteSelectedTxSize(t);
                SetTxfmCtxs(xd, mbmi.TxSize, false);
            }
        }
        else SetTxfmCtxs(xd, mbmi.TxSize, mbmi.SkipTxfm != 0 && isInterTx);

        if (mbmi.SkipTxfm == 0)
        {
            if (!isInterTx) WriteIntraCoeffsMb(t, bsize);
            else WriteInterCoeffsMb(t, bsize);
        }
    }

    /// <summary>write_mb_modes_kf (no segmentation, CDEF strength bits 0, no delta q).</summary>
    private static void WriteMbModesKf(TileWriter t)
    {
        var xd = t.Xd;
        var mbmi = xd.Mi0;
        var m = t.Fc.Mode;
        // write_skip
        t.W.WriteSymbol(mbmi.SkipTxfm, m.Skip[AomTxSearch.SkipTxfmContext(xd)], 2);
        WriteCdef(t, mbmi.SkipTxfm);
        WriteDeltaQParams(t, mbmi.SkipTxfm);
        if (t.F.AllowIntrabc)
        {
            // write_intrabc_info
            t.W.WriteSymbol(mbmi.UseIntrabc, m.Intrabc, 2);
            if (mbmi.UseIntrabc != 0)
            {
                var ext = t.Cpi.MbmiExtFrameAt(xd.MiRow, xd.MiCol)!;   // x->mbmi_ext_frame
                EncodeDv(t.W, mbmi.Mv0, ext.RefMvStack[0].ThisMv, t.Fc.Mv);
                return;
            }
        }
        WriteIntraPredictionModes(t);
    }

    /// <summary>write_cdef: the CDEF unit's strength with its first non-skip block.</summary>
    private static void WriteCdef(TileWriter t, int skip)
    {
        var f = t.F;
        if (f.CodedLossless || f.AllowIntrabc || !f.EnableCdef) return;
        var cm = t.Cm;
        var xd = t.Xd;
        int sbMask = cm.MibSize - 1;
        if ((xd.MiRow & sbMask) == 0 && (xd.MiCol & sbMask) == 0) Array.Clear(t.CdefTransmitted);
        const int cdefSize = 16;   // 64x64 CDEF units in mi
        int index = cm.SbSize == BLOCK_128X128 ? ((xd.MiCol & cdefSize) != 0 ? 1 : 0) + 2 * ((xd.MiRow & cdefSize) != 0 ? 1 : 0) : 0;
        if (!t.CdefTransmitted[index] && skip == 0)
        {
            int firstBlockMask = ~(cdefSize - 1);
            var mbmi = cm.MiGridBase[(xd.MiRow & firstBlockMask) * cm.MiStride + (xd.MiCol & firstBlockMask)]!;
            t.W.WriteLiteral(mbmi.CdefStrength, t.Cpi.PostFilter!.Cdef!.CdefBits);
            t.CdefTransmitted[index] = true;
        }
    }

    /// <summary>write_delta_q_params (no delta lf).</summary>
    private static void WriteDeltaQParams(TileWriter t, int skip)
    {
        var cpi = t.Cpi;
        if (!cpi.DeltaQPresentFlag) return;
        var cm = t.Cm;
        var xd = t.Xd;
        var mbmi = xd.Mi0;
        bool superBlockUpperLeft = (xd.MiRow & (cm.MibSize - 1)) == 0 && (xd.MiCol & (cm.MibSize - 1)) == 0;
        if ((mbmi.Bsize != cm.SbSize || skip == 0) && superBlockUpperLeft)
        {
            int reduced = (mbmi.CurrentQindex - xd.CurrentBaseQindex) / cpi.DeltaQRes;
            // write_delta_qindex
            int sign = reduced < 0 ? 1 : 0;
            int abs = sign != 0 ? -reduced : reduced;
            const int deltaQSmall = 3;
            t.W.WriteSymbol(Math.Min(abs, deltaQSmall), t.Fc.Mode.DeltaQ, deltaQSmall + 1);
            if (abs >= deltaQSmall)
            {
                int remBits = MostSignificantBit(abs - 1);
                int thr = (1 << remBits) + 1;
                t.W.WriteLiteral(remBits - 1, 3);
                t.W.WriteLiteral(abs - thr, remBits);
            }
            if (abs > 0) t.W.WriteBit(sign);
            xd.CurrentBaseQindex = mbmi.CurrentQindex;
        }
    }

    /// <summary>av1_encode_dv (encodemv.c): the DV difference with MV_SUBPEL_NONE on the ndvc CDFs.</summary>
    private static void EncodeDv(AomWriter w, AomMv mv, AomMv refMv, Av1CdfMvContext mvctx)
    {
        var diff = new AomMv(mv.Row - refMv.Row, mv.Col - refMv.Col);
        int j = AomMvCost.GetMvJoint(diff);
        w.WriteSymbol(j, mvctx.Joint, AomMvCost.MvJoints);
        if (AomMvCost.MvJointVertical(j)) EncodeMvComponent(w, diff.Row, mvctx.Comp0);
        if (AomMvCost.MvJointHorizontal(j)) EncodeMvComponent(w, diff.Col, mvctx.Comp1);
    }

    /// <summary>encode_mv_component (MV_SUBPEL_NONE: no fractional / high precision bits).</summary>
    private static void EncodeMvComponent(AomWriter w, int comp, Av1CdfMvComponent mvcomp)
    {
        int sign = comp < 0 ? 1 : 0;
        int mag = sign != 0 ? -comp : comp;
        int mvClass = AomMvCost.GetMvClass(mag - 1, out int offset);
        int d = offset >> 3;
        w.WriteSymbol(sign, mvcomp.Sign, 2);
        w.WriteSymbol(mvClass, mvcomp.Classes, AomMvCost.MvClasses);
        if (mvClass == 0) w.WriteSymbol(d, mvcomp.Class0, AomMvCost.Class0Size);
        else
        {
            int n = mvClass + AomMvCost.Class0Bits - 1;
            for (int i = 0; i < n; ++i) w.WriteSymbol((d >> i) & 1, mvcomp.ClassN[i], 2);
        }
    }

    /// <summary>write_intra_prediction_modes (key frame).</summary>
    private static void WriteIntraPredictionModes(TileWriter t)
    {
        var cpi = t.Cpi;
        var cm = t.Cm;
        var xd = t.Xd;
        var w = t.W;
        var fc = t.Fc;
        var m = fc.Mode;
        var mbmi = xd.Mi0;
        int mode = mbmi.Mode;
        int bsize = mbmi.Bsize;

        // write_intra_y_mode_kf (get_y_mode_cdf: av1_above_block_mode / av1_left_block_mode)
        int above = xd.AboveMbmi?.Mode ?? DC_PRED, left = xd.LeftMbmi?.Mode ?? DC_PRED;
        w.WriteSymbol(mode, fc.Kfym[IntraModeContext[above] * 5 + IntraModeContext[left]], 13);

        bool useAngleDelta = bsize >= BLOCK_8X8;
        if (useAngleDelta && mode >= V_PRED && mode <= AomTables.D67_PRED)
            w.WriteSymbol(mbmi.AngleDelta[0] + 3, m.AngleDelta[mode - V_PRED], 7);

        if (!cm.Monochrome && xd.IsChromaRef)
        {
            int uvMode = mbmi.UvMode;
            int cflAllowed = AomCfl.IsCflAllowed(xd);
            w.WriteSymbol(uvMode, m.UvMode[cflAllowed * 13 + mode], 14 - (cflAllowed != 0 ? 0 : 1));
            if (uvMode == UV_CFL_PRED)
            {
                // write_cfl_alphas
                int jointSign = mbmi.CflAlphaSigns;
                int idx = mbmi.CflAlphaIdx;
                w.WriteSymbol(jointSign, m.CflSign, 8);
                int signU = (jointSign + 1) / 3, signV = (jointSign + 1) % 3;
                if (signU != 0) w.WriteSymbol(idx >> 4, m.CflAlpha[(signU - 1) * 3 + signV], 16);
                if (signV != 0) w.WriteSymbol(idx & 15, m.CflAlpha[(signV - 1) * 3 + signU], 16);
            }
            int intraMode = uvMode == UV_CFL_PRED ? DC_PRED : uvMode;
            if (useAngleDelta && intraMode >= V_PRED && intraMode <= AomTables.D67_PRED)
                w.WriteSymbol(mbmi.AngleDelta[1] + 3, m.AngleDelta[intraMode - V_PRED], 7);
        }

        if (AomIntraModeSearch.AllowPalette(t.F.AllowScreenContentTools, bsize)) WritePaletteModeInfo(t);

        // write_filter_intra_mode_info
        if (AomIntraModeSearch.FilterIntraAllowed(cpi, mbmi))
        {
            w.WriteSymbol(mbmi.UseFilterIntra, m.UseFilterIntra[AomModeCostFill.LibaomToDav1dBs[bsize]], 2);
            if (mbmi.UseFilterIntra != 0) w.WriteSymbol(mbmi.FilterIntraMode, m.FilterIntra, 5);
        }
    }

    /// <summary>write_palette_mode_info.</summary>
    private static void WritePaletteModeInfo(TileWriter t)
    {
        var xd = t.Xd;
        var w = t.W;
        var m = t.Fc.Mode;
        var mbmi = xd.Mi0;
        int bsizeCtx = NumPelsLog2Lookup[mbmi.Bsize] - NumPelsLog2Lookup[BLOCK_8X8];
        if (mbmi.Mode == DC_PRED)
        {
            int n = mbmi.Palette.PaletteSize0;
            int ctx = AomIntraModeSearch.PaletteModeCtx(xd);
            w.WriteSymbol(n > 0 ? 1 : 0, m.PalY[bsizeCtx * 3 + ctx], 2);
            if (n > 0)
            {
                w.WriteSymbol(n - AomPalette.PALETTE_MIN_SIZE, m.PalSz[bsizeCtx], 7);
                WritePaletteColorsY(t);
            }
        }
        if (t.Cm.NumPlanes > 1 && mbmi.UvMode == UV_DC_PRED && xd.IsChromaRef)
        {
            int n = mbmi.Palette.PaletteSize1;
            w.WriteSymbol(n > 0 ? 1 : 0, m.PalUv[mbmi.Palette.PaletteSize0 > 0 ? 1 : 0], 2);
            if (n > 0)
            {
                w.WriteSymbol(n - AomPalette.PALETTE_MIN_SIZE, m.PalSz[7 + bsizeCtx], 7);
                WritePaletteColorsUv(t);
            }
        }
    }

    /// <summary>delta_encode_palette_colors.</summary>
    private static void DeltaEncodePaletteColors(AomWriter w, ReadOnlySpan<int> colors, int num, int bitDepth, int minVal)
    {
        if (num <= 0) return;
        w.WriteLiteral(colors[0], bitDepth);
        if (num == 1) return;
        int maxDelta = 0;
        Span<int> deltas = stackalloc int[AomPalette.PALETTE_MAX_SIZE];
        deltas.Clear();
        for (int i = 1; i < num; ++i)
        {
            int delta = colors[i] - colors[i - 1];
            deltas[i - 1] = delta;
            if (delta > maxDelta) maxDelta = delta;
        }
        int minBits = bitDepth - 3;
        int bits = Math.Max(AomPalette.CeilLog2(maxDelta + 1 - minVal), minBits);
        int range = (1 << bitDepth) - colors[0] - minVal;
        w.WriteLiteral(bits - minBits, 2);
        for (int i = 0; i < num - 1; ++i)
        {
            w.WriteLiteral(deltas[i] - minVal, bits);
            range -= deltas[i];
            bits = Math.Min(bits, AomPalette.CeilLog2(range));
        }
    }

    /// <summary>write_palette_colors_y.</summary>
    private static void WritePaletteColorsY(TileWriter t)
    {
        var mbmi = t.Xd.Mi0;
        int n = mbmi.Palette.PaletteSize0;
        Span<ushort> colorCache = stackalloc ushort[2 * AomPalette.PALETTE_MAX_SIZE];
        int nCache = AomPalette.GetPaletteCache(t.Xd, 0, colorCache);
        Span<int> outCacheColors = stackalloc int[AomPalette.PALETTE_MAX_SIZE];
        Span<byte> cacheColorFound = stackalloc byte[2 * AomPalette.PALETTE_MAX_SIZE];
        int nOutCache = AomPalette.IndexColorCache(colorCache, nCache, mbmi.Palette.PaletteColors, n, cacheColorFound, outCacheColors);
        int nInCache = 0;
        for (int i = 0; i < nCache && nInCache < n; ++i)
        {
            int found = cacheColorFound[i];
            t.W.WriteBit(found);
            nInCache += found;
        }
        DeltaEncodePaletteColors(t.W, outCacheColors, nOutCache, t.Cm.BitDepth, 1);
    }

    /// <summary>write_palette_colors_uv.</summary>
    private static void WritePaletteColorsUv(TileWriter t)
    {
        int bitDepth = t.Cm.BitDepth;
        var w = t.W;
        var pmi = t.Xd.Mi0.Palette;
        int n = pmi.PaletteSize1;
        var colorsU = pmi.PaletteColors.AsSpan(AomPalette.PALETTE_MAX_SIZE);
        var colorsV = pmi.PaletteColors.AsSpan(2 * AomPalette.PALETTE_MAX_SIZE);
        Span<ushort> colorCache = stackalloc ushort[2 * AomPalette.PALETTE_MAX_SIZE];
        int nCache = AomPalette.GetPaletteCache(t.Xd, 1, colorCache);
        Span<int> outCacheColors = stackalloc int[AomPalette.PALETTE_MAX_SIZE];
        Span<byte> cacheColorFound = stackalloc byte[2 * AomPalette.PALETTE_MAX_SIZE];
        int nOutCache = AomPalette.IndexColorCache(colorCache, nCache, colorsU, n, cacheColorFound, outCacheColors);
        int nInCache = 0;
        for (int i = 0; i < nCache && nInCache < n; ++i)
        {
            int found = cacheColorFound[i];
            w.WriteBit(found);
            nInCache += found;
        }
        DeltaEncodePaletteColors(w, outCacheColors, nOutCache, bitDepth, 0);

        // V channel: delta or raw, whichever is cheaper
        int maxVal = 1 << bitDepth;
        int bitsV = AomPalette.GetPaletteDeltaBitsV(pmi, bitDepth, out int zeroCount, out int minBitsV);
        int rateUsingDelta = 2 + bitDepth + (bitsV + 1) * (n - 1) - zeroCount;
        int rateUsingRaw = bitDepth * n;
        if (rateUsingDelta < rateUsingRaw)
        {
            w.WriteBit(1);
            w.WriteLiteral(bitsV - minBitsV, 2);
            w.WriteLiteral(colorsV[0], bitDepth);
            for (int i = 1; i < n; ++i)
            {
                if (colorsV[i] == colorsV[i - 1])
                {
                    w.WriteLiteral(0, bitsV);
                    continue;
                }
                int delta = Math.Abs(colorsV[i] - colorsV[i - 1]);
                int signBit = colorsV[i] < colorsV[i - 1] ? 1 : 0;
                if (delta <= maxVal - delta)
                {
                    w.WriteLiteral(delta, bitsV);
                    w.WriteBit(signBit);
                }
                else
                {
                    w.WriteLiteral(maxVal - delta, bitsV);
                    w.WriteBit(signBit ^ 1);
                }
            }
        }
        else
        {
            w.WriteBit(0);
            for (int i = 0; i < n; ++i) w.WriteLiteral(colorsV[i], bitDepth);
        }
    }

    /// <summary>write_uniform.</summary>
    private static void WriteUniform(AomWriter w, int n, int v)
    {
        int l = n > 0 ? MostSignificantBit(n) + 1 : 0;   // get_unsigned_bits
        int m = (1 << l) - n;
        if (l == 0) return;
        if (v < m) w.WriteLiteral(v, l - 1);
        else
        {
            w.WriteLiteral(m + ((v - m) >> 1), l - 1);
            w.WriteLiteral((v - m) & 1, 1);
        }
    }

    /// <summary>pack_map_tokens: the palette color map from the final encode's token list.</summary>
    private static void PackMapTokens(TileWriter t, int plane, int n, int num)
    {
        var tokens = t.Cpi.PaletteTokens;
        var m = t.Fc.Mode;
        int paletteSizeIdx = n - AomPalette.PALETTE_MIN_SIZE;
        WriteUniform(t.W, n, tokens[t.TokIdx++].Token);
        --num;
        for (int i = 0; i < num; ++i)
        {
            var p = tokens[t.TokIdx++];
            t.W.WriteSymbol(p.Token, m.ColorMap[(plane * 7 + paletteSizeIdx) * 5 + p.ColorCtx], n);
        }
    }

    /// <summary>set_txfm_ctxs.</summary>
    private static void SetTxfmCtxs(AomMacroblockD xd, int txSize, bool skip)
    {
        byte bw = (byte)TxSizeWide[txSize], bh = (byte)TxSizeHigh[txSize];
        if (skip)
        {
            bw = (byte)(xd.Width * 4);
            bh = (byte)(xd.Height * 4);
        }
        xd.AboveTxfmContext.AsSpan(xd.AboveTxfmContextOffset, xd.Width).Fill(bw);
        xd.LeftTxfmContextBuffer.AsSpan(xd.LeftTxfmContextOffset, xd.Height).Fill(bh);
    }

    /// <summary>write_tx_size_vartx.</summary>
    private static void WriteTxSizeVartx(TileWriter t, int txSize, int depth, int blkRow, int blkCol)
    {
        var xd = t.Xd;
        var mbmi = xd.Mi0;
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, mbmi.Bsize, 0), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, mbmi.Bsize, 0);
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        var above = xd.AboveTxfmContext;
        var left = xd.LeftTxfmContextBuffer;
        int aOff = xd.AboveTxfmContextOffset + blkCol, lOff = xd.LeftTxfmContextOffset + blkRow;
        if (depth == 2)   // MAX_VARTX_DEPTH
        {
            AomTxSearch.TxfmPartitionUpdate(above, aOff, left, lOff, txSize, txSize);
            return;
        }
        int ctx = AomTxSearch.TxfmPartitionContext(above[aOff], left[lOff], mbmi.Bsize, txSize);
        int txbSizeIndex = AomTxSearch.GetTxbSizeIndex(mbmi.Bsize, blkRow, blkCol);
        if (txSize == mbmi.InterTxSize[txbSizeIndex])
        {
            t.W.WriteSymbol(0, t.Fc.Mode.Txpart[ctx], 2);
            AomTxSearch.TxfmPartitionUpdate(above, aOff, left, lOff, txSize, txSize);
        }
        else
        {
            int subTxs = SubTxSizeMap[txSize];
            int bsw = TxSizeWideUnit[subTxs], bsh = TxSizeHighUnit[subTxs];
            t.W.WriteSymbol(1, t.Fc.Mode.Txpart[ctx], 2);
            if (subTxs == TX_4X4)
            {
                AomTxSearch.TxfmPartitionUpdate(above, aOff, left, lOff, subTxs, txSize);
                return;
            }
            for (int row = 0; row < TxSizeHighUnit[txSize]; row += bsh)
                for (int col = 0; col < TxSizeWideUnit[txSize]; col += bsw)
                    WriteTxSizeVartx(t, subTxs, depth + 1, blkRow + row, blkCol + col);
        }
    }

    /// <summary>write_tokens_b's inter branch (write_inter_txb_coeff / pack_txb_tokens).</summary>
    private static void WriteInterCoeffsMb(TileWriter t, int bsize)
    {
        var cm = t.Cm;
        var xd = t.Xd;
        Span<int> block = stackalloc int[3];
        block.Clear();
        int num4x4W = MiSizeWide[bsize], num4x4H = MiSizeHigh[bsize];
        int muBlocksWide = Math.Min(num4x4W, MiSizeWide[BLOCK_64X64]), muBlocksHigh = Math.Min(num4x4H, MiSizeHigh[BLOCK_64X64]);
        for (int row = 0; row < num4x4H; row += muBlocksHigh)
            for (int col = 0; col < num4x4W; col += muBlocksWide)
                for (int plane = 0; plane < cm.NumPlanes; ++plane)
                {
                    if (plane != 0 && !xd.IsChromaRef) break;
                    var pd = xd.Plane[plane];
                    int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
                    int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, ssX, ssY);
                    int maxTxSize = AomTxSearch.GetVartxMaxTxsize(xd, planeBsize, plane);
                    int step = TxSizeWideUnit[maxTxSize] * TxSizeHighUnit[maxTxSize];
                    int bkw = TxSizeWideUnit[maxTxSize], bkh = TxSizeHighUnit[maxTxSize];
                    int maxUnitBsize = AomEncodeMb.PlaneBlockSize(BLOCK_64X64, ssX, ssY);
                    int unitHeight = Math.Min(MiSizeHigh[maxUnitBsize] + (row >> ssY), MiSizeHigh[planeBsize]);
                    int unitWidth = Math.Min(MiSizeWide[maxUnitBsize] + (col >> ssX), MiSizeWide[planeBsize]);
                    for (int blkRow = row >> ssY; blkRow < unitHeight; blkRow += bkh)
                        for (int blkCol = col >> ssX; blkCol < unitWidth; blkCol += bkw)
                        {
                            PackTxbTokens(t, plane, planeBsize, block[plane], blkRow, blkCol, maxTxSize);
                            block[plane] += step;
                        }
                }
    }

    /// <summary>pack_txb_tokens.</summary>
    private static void PackTxbTokens(TileWriter t, int plane, int planeBsize, int block, int blkRow, int blkCol, int txSize)
    {
        var xd = t.Xd;
        var mbmi = xd.Mi0;
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane);
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        var pd = xd.Plane[plane];
        int planeTxSize = plane != 0 ? AomEncodeMb.MaxUvTxsize(mbmi.Bsize, pd.SubsamplingX, pd.SubsamplingY)
            : mbmi.InterTxSize[AomTxSearch.GetTxbSizeIndex(planeBsize, blkRow, blkCol)];
        if (txSize == planeTxSize || plane != 0)
        {
            WriteCoeffsTxb(t, blkRow, blkCol, plane, block, txSize);
            return;
        }
        int subTxs = SubTxSizeMap[txSize];
        int bsw = TxSizeWideUnit[subTxs], bsh = TxSizeHighUnit[subTxs];
        int step = bsh * bsw;
        int rowEnd = Math.Min(TxSizeHighUnit[txSize], maxBlocksHigh - blkRow);
        int colEnd = Math.Min(TxSizeWideUnit[txSize], maxBlocksWide - blkCol);
        for (int r = 0; r < rowEnd; r += bsh)
            for (int c = 0; c < colEnd; c += bsw)
            {
                PackTxbTokens(t, plane, planeBsize, block, blkRow + r, blkCol + c, subTxs);
                block += step;
            }
    }

    /// <summary>write_selected_tx_size.</summary>
    private static void WriteSelectedTxSize(TileWriter t)
    {
        var xd = t.Xd;
        var mbmi = xd.Mi0;
        int bsize = mbmi.Bsize;
        if (!AomTxSearch.BlockSignalsTxsize(bsize)) return;
        int txSizeCtx = AomTxSearch.TxSizeContext(xd);
        int depth = AomTxSearch.TxSizeToDepth(mbmi.TxSize, bsize);
        int maxDepths = BsizeToMaxDepth(bsize);
        int cat = AomTxSearch.BsizeToTxSizeCat(bsize);
        t.W.WriteSymbol(depth, t.Fc.Mode.Txsz[cat * 3 + txSizeCtx], maxDepths + 1);
    }

    /// <summary>bsize_to_max_depth.</summary>
    private static int BsizeToMaxDepth(int bsize)
    {
        int txSize = MaxTxsizeRectLookup[bsize];
        int depth = 0;
        while (depth < 2 && txSize != TX_4X4) { depth++; txSize = SubTxSizeMap[txSize]; }
        return depth;
    }

    // ---- coefficients (encodetxb.c) -----------------------------------------------------------------------------

    /// <summary>av1_write_intra_coeffs_mb.</summary>
    private static void WriteIntraCoeffsMb(TileWriter t, int bsize)
    {
        var cm = t.Cm;
        var xd = t.Xd;
        int numPlanes = cm.NumPlanes;
        Span<int> block = stackalloc int[3];
        block.Clear();
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, bsize, 0);
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, bsize, 0);
        int muBlocksWide = Math.Min(maxBlocksWide, MiSizeWide[BLOCK_64X64]);
        int muBlocksHigh = Math.Min(maxBlocksHigh, MiSizeHigh[BLOCK_64X64]);
        for (int row = 0; row < maxBlocksHigh; row += muBlocksHigh)
            for (int col = 0; col < maxBlocksWide; col += muBlocksWide)
                for (int plane = 0; plane < numPlanes; ++plane)
                {
                    if (plane != 0 && !xd.IsChromaRef) break;
                    int txSize = AomEncodeMb.GetTxSize(plane, xd);
                    int stepr = TxSizeHighUnit[txSize], stepc = TxSizeWideUnit[txSize];
                    int step = stepr * stepc;
                    var pd = xd.Plane[plane];
                    int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
                    int unitHeight = (Math.Min(muBlocksHigh + row, maxBlocksHigh) + ((1 << ssY) >> 1)) >> ssY;
                    int unitWidth = (Math.Min(muBlocksWide + col, maxBlocksWide) + ((1 << ssX) >> 1)) >> ssX;
                    for (int blkRow = row >> ssY; blkRow < unitHeight; blkRow += stepr)
                        for (int blkCol = col >> ssX; blkCol < unitWidth; blkCol += stepc)
                        {
                            WriteCoeffsTxb(t, blkRow, blkCol, plane, block[plane], txSize);
                            block[plane] += step;
                        }
                }
    }

    /// <summary>av1_write_coeffs_txb from the superblock's CB_COEFF_BUFFER.</summary>
    private static void WriteCoeffsTxb(TileWriter t, int blkRow, int blkCol, int plane, int block, int txSize)
    {
        var cpi = t.Cpi;
        var cm = t.Cm;
        var xd = t.Xd;
        var w = t.W;
        var ec = t.Fc.Coef;
        var cb = t.Cb;
        int planeType = plane == 0 ? 0 : 1;
        int cbOffset = cpi.ExtCbOffset[xd.MiOffset * 2 + planeType];
        int txbOffset = cbOffset / 16;
        int eob = cb.Eobs[plane][txbOffset + block];
        int entropyCtx = cb.EntropyCtx[plane][txbOffset + block];
        int txbSkipCtx = entropyCtx & 15;   // TXB_SKIP_CTX_MASK
        int txsCtx = AomTxb.TxsizeEntropyCtx(txSize);
        w.WriteSymbol(eob == 0 ? 1 : 0, ec.CoefSkip[txsCtx * 13 + txbSkipCtx], 2);
        if (eob == 0) return;

        int txType = AomEncodeMb.GetTxType(xd, planeType, blkRow, blkCol, txSize, t.F.ReducedTxSetUsed != 0);
        if (plane == 0) WriteTxType(t, txType, txSize);

        int eobPt = AomTxb.EobPosToken(eob, out int eobExtra);
        int eobMultiSize = TxsizeLog2Minus4[txSize];
        int txClass = AomTxb.TxTypeToClass[txType];
        int eobMultiCtx = txClass == TX_CLASS_2D ? 0 : 1;
        ushort[] eobCdf = eobMultiSize switch
        {
            0 => ec.EobBin16[planeType * 2 + eobMultiCtx], 1 => ec.EobBin32[planeType * 2 + eobMultiCtx],
            2 => ec.EobBin64[planeType * 2 + eobMultiCtx], 3 => ec.EobBin128[planeType * 2 + eobMultiCtx],
            4 => ec.EobBin256[planeType * 2 + eobMultiCtx], 5 => ec.EobBin512[planeType], _ => ec.EobBin1024[planeType],
        };
        w.WriteSymbol(eobPt - 1, eobCdf, eobMultiSize + 5);

        int eobOffsetBits = EobOffsetBits[eobPt];
        if (eobOffsetBits > 0)
        {
            int eobCtx = eobPt - 3;
            int eobShift = eobOffsetBits - 1;
            int bit = (eobExtra & (1 << eobShift)) != 0 ? 1 : 0;
            w.WriteSymbol(bit, ec.EobHiBit[(txsCtx * 2 + planeType) * 9 + eobCtx], 2);
            for (int i = 1; i < eobOffsetBits; i++)
            {
                eobShift = eobOffsetBits - 1 - i;
                bit = (eobExtra & (1 << eobShift)) != 0 ? 1 : 0;
                w.WriteBit(bit);
            }
        }

        int width = AomTxb.TxbWide(txSize), height = AomTxb.TxbHigh(txSize);
        int[] tcoeffArr = cb.Tcoeff[plane];
        int tcoeffOff = cbOffset + AomEncodeMb.BlockOffset(block);
        var levels = t.Levels;
        AomTxb.InitLevels(tcoeffArr.AsSpan(tcoeffOff, width * height), width, height, levels);
        var scan = AomEncodeMb.ScanOf(txSize, txType);
        int bhl = AomTxb.TxbBhl(txSize);
        var coeffContexts = t.CoeffContexts;
        // av1_get_nz_map_contexts
        for (int i = 0; i < eob; ++i)
        {
            int pos = scan[i];
            coeffContexts[pos] = (sbyte)(i == eob - 1 ? AomTxb.LowerLevelsCtxEob(bhl, width, i)
                : AomTxb.LowerLevelsCtx(levels, pos, bhl, txSize, txClass));
        }

        for (int c = eob - 1; c >= 0; --c)
        {
            int pos = scan[c];
            int coeffCtx = coeffContexts[pos];
            int level = Math.Abs(tcoeffArr[tcoeffOff + pos]);
            if (c == eob - 1) w.WriteSymbol(Math.Min(level, 3) - 1, ec.EobBaseTok[(txsCtx * 2 + planeType) * 4 + coeffCtx], 3);
            else w.WriteSymbol(Math.Min(level, 3), ec.BaseTok[(txsCtx * 2 + planeType) * 41 + coeffCtx], 4);
            if (level > NUM_BASE_LEVELS)
            {
                int baseRange = level - 1 - NUM_BASE_LEVELS;
                int brCtx = AomTxb.BrCtx(levels, pos, bhl, txClass);
                ushort[] cdf = ec.BrTok[(Math.Min(txsCtx, TX_32X32) * 2 + planeType) * 21 + brCtx];
                for (int idx = 0; idx < COEFF_BASE_RANGE; idx += BR_CDF_SIZE - 1)
                {
                    int k = Math.Min(baseRange - idx, BR_CDF_SIZE - 1);
                    w.WriteSymbol(k, cdf, BR_CDF_SIZE);
                    if (k < BR_CDF_SIZE - 1) break;
                }
            }
        }

        // the signs, DC first, then the Golomb remainders
        for (int c = 0; c < eob; ++c)
        {
            int v = tcoeffArr[tcoeffOff + scan[c]];
            int level = Math.Abs(v);
            int sign = v < 0 ? 1 : 0;
            if (level == 0) continue;
            if (c == 0)
            {
                int dcSignCtx = (entropyCtx >> 4) & 3;   // DC_SIGN_CTX_SHIFT / MASK
                w.WriteSymbol(sign, ec.DcSign[planeType * 3 + dcSignCtx], 2);
            }
            else w.WriteBit(sign);
            if (level > COEFF_BASE_RANGE + NUM_BASE_LEVELS) WriteGolomb(w, level - COEFF_BASE_RANGE - 1 - NUM_BASE_LEVELS);
        }
    }

    /// <summary>write_golomb.</summary>
    private static void WriteGolomb(AomWriter w, int level)
    {
        int x = level + 1;
        int length = 0;
        for (int i = x; i != 0; i >>= 1) ++length;
        for (int i = 0; i < length - 1; ++i) w.WriteBit(0);
        for (int i = length - 1; i >= 0; --i) w.WriteBit((x >> i) & 1);
    }

    /// <summary>av1_write_tx_type (intra).</summary>
    private static void WriteTxType(TileWriter t, int txType, int txSize)
    {
        var mbmi = t.Xd.Mi0;
        bool reduced = t.F.ReducedTxSetUsed != 0;
        bool isInter = AomEncodeMb.IsInterBlock(mbmi);
        int setType = AomEncodeMb.ExtTxSetType(txSize, isInter, reduced);
        if (NumExtTxSet[setType] > 1 && t.Cm.BaseQindex > 0 && mbmi.SkipTxfm == 0 && isInter)
        {
            int eset = ExtTxSetIndex[6 + setType];
            int sqr = TxsizeSqrMap[txSize];
            var mi = t.Fc.Mode;
            ushort[] cdf = eset == 1 ? mi.TxtpInter1[sqr] : eset == 2 ? mi.TxtpInter2 : mi.TxtpInter3[sqr];
            t.W.WriteSymbol(ExtTxInd[setType * 16 + txType], cdf, NumExtTxSet[setType]);
        }
        else if (NumExtTxSet[setType] > 1 && t.Cm.BaseQindex > 0 && mbmi.SkipTxfm == 0)
        {
            int eset = ExtTxSetIndex[0 * 16 + setType];
            int intraDir = mbmi.UseFilterIntra != 0 ? FimodeToIntradir[mbmi.FilterIntraMode] : mbmi.Mode;
            int sqr = TxsizeSqrMap[txSize];
            var m = t.Fc.Mode;
            ushort[] cdf = eset == 1 ? m.TxtpIntra1[sqr * 13 + intraDir] : m.TxtpIntra2[sqr * 13 + intraDir];
            t.W.WriteSymbol(ExtTxInd[setType * 16 + txType], cdf, NumExtTxSet[setType]);
        }
    }

    // ---- loop restoration -----------------------------------------------------------------------------------------

    /// <summary>av1_loop_restoration_corners_in_sb (no superres).</summary>
    private static bool LoopRestorationCornersInSb(TileWriter t, int plane, int miRow, int miCol, int bsize,
        out int rcol0, out int rcol1, out int rrow0, out int rrow1)
    {
        rcol0 = rcol1 = rrow0 = rrow1 = 0;
        var cm = t.Cm;
        if (bsize != cm.SbSize) return false;
        bool isUv = plane > 0;
        int miRow1 = miRow + MiSizeHigh[bsize], miCol1 = miCol + MiSizeWide[bsize];
        var rsi = t.F.Rst![plane];
        int size = rsi.RestorationUnitSize;
        int ssX = isUv ? cm.SsX : 0, ssY = isUv ? cm.SsY : 0;
        int miToNumX = 4 >> ssX, miToNumY = 4 >> ssY;
        int denomX = size, denomY = size;
        int rndX = denomX - 1, rndY = denomY - 1;
        rcol0 = (miCol * miToNumX + rndX) / denomX;
        rrow0 = (miRow * miToNumY + rndY) / denomY;
        rcol1 = Math.Min((miCol1 * miToNumX + rndX) / denomX, rsi.HorzUnits);
        rrow1 = Math.Min((miRow1 * miToNumY + rndY) / denomY, rsi.VertUnits);
        return rcol0 < rcol1 && rrow0 < rrow1;
    }

    /// <summary>loop_restoration_write_sb_coeffs.</summary>
    private static void LoopRestorationWriteSbCoeffs(TileWriter t, int runitIdx, int plane)
    {
        var rsi = t.F.Rst![plane];
        var rui = rsi.UnitInfo[runitIdx];
        int frameRtype = t.F.FrameRestorationType[plane];
        var m = t.Fc.Mode;
        var w = t.W;
        int wienerWin = plane > 0 ? AomRestoration.WienerWinChroma : AomRestoration.WienerWin;
        int unitRtype = rui.Type;
        if (frameRtype == AomRestoration.RestoreSwitchable)
        {
            w.WriteSymbol(unitRtype, m.RestoreSwitchable, AomRestoration.RestoreSwitchableTypes);
            if (unitRtype == AomRestoration.RestoreWiener) WriteWienerFilter(w, wienerWin, rui.Wiener, ref t.RefWiener[plane]);
            else if (unitRtype == AomRestoration.RestoreSgrproj) WriteSgrprojFilter(w, rui.Sgrproj, ref t.RefSgrproj[plane]);
        }
        else if (frameRtype == AomRestoration.RestoreWiener)
        {
            w.WriteSymbol(unitRtype != AomRestoration.RestoreNone ? 1 : 0, m.RestoreWiener, 2);
            if (unitRtype != AomRestoration.RestoreNone) WriteWienerFilter(w, wienerWin, rui.Wiener, ref t.RefWiener[plane]);
        }
        else if (frameRtype == AomRestoration.RestoreSgrproj)
        {
            w.WriteSymbol(unitRtype != AomRestoration.RestoreNone ? 1 : 0, m.RestoreSgrproj, 2);
            if (unitRtype != AomRestoration.RestoreNone) WriteSgrprojFilter(w, rui.Sgrproj, ref t.RefSgrproj[plane]);
        }
    }

    /// <summary>write_wiener_filter.</summary>
    private static void WriteWienerFilter(AomWriter w, int wienerWin, in AomWienerInfo info, ref AomWienerInfo reff)
    {
        const int n0 = AomRestoration.WienerFiltTap0Maxv - AomRestoration.WienerFiltTap0Minv + 1;
        const int n1 = AomRestoration.WienerFiltTap1Maxv - AomRestoration.WienerFiltTap1Minv + 1;
        const int n2 = AomRestoration.WienerFiltTap2Maxv - AomRestoration.WienerFiltTap2Minv + 1;
        const int min0 = AomRestoration.WienerFiltTap0Minv, min1 = AomRestoration.WienerFiltTap1Minv, min2 = AomRestoration.WienerFiltTap2Minv;
        const int k0 = AomRestoration.WienerFiltTap0SubexpK, k1 = AomRestoration.WienerFiltTap1SubexpK, k2 = AomRestoration.WienerFiltTap2SubexpK;
        if (wienerWin == AomRestoration.WienerWin) WritePrimitiveRefsubexpfin(w, n0, k0, reff.V[0] - min0, info.V[0] - min0);
        WritePrimitiveRefsubexpfin(w, n1, k1, reff.V[1] - min1, info.V[1] - min1);
        WritePrimitiveRefsubexpfin(w, n2, k2, reff.V[2] - min2, info.V[2] - min2);
        if (wienerWin == AomRestoration.WienerWin) WritePrimitiveRefsubexpfin(w, n0, k0, reff.H[0] - min0, info.H[0] - min0);
        WritePrimitiveRefsubexpfin(w, n1, k1, reff.H[1] - min1, info.H[1] - min1);
        WritePrimitiveRefsubexpfin(w, n2, k2, reff.H[2] - min2, info.H[2] - min2);
        reff = info;
    }

    /// <summary>write_sgrproj_filter.</summary>
    private static void WriteSgrprojFilter(AomWriter w, in AomSgrprojInfo info, ref AomSgrprojInfo reff)
    {
        const int n0 = AomRestoration.SgrprojPrjMax0 - AomRestoration.SgrprojPrjMin0 + 1;
        const int n1 = AomRestoration.SgrprojPrjMax1 - AomRestoration.SgrprojPrjMin1 + 1;
        const int min0 = AomRestoration.SgrprojPrjMin0, min1 = AomRestoration.SgrprojPrjMin1, k = AomRestoration.SgrprojPrjSubexpK;
        w.WriteLiteral(info.Ep, AomRestoration.SgrprojParamsBits);
        if (AomRestoration.SgrR0[info.Ep] == 0)
            WritePrimitiveRefsubexpfin(w, n1, k, reff.Xqd1 - min1, info.Xqd1 - min1);
        else if (AomRestoration.SgrR1[info.Ep] == 0)
            WritePrimitiveRefsubexpfin(w, n0, k, reff.Xqd0 - min0, info.Xqd0 - min0);
        else
        {
            WritePrimitiveRefsubexpfin(w, n0, k, reff.Xqd0 - min0, info.Xqd0 - min0);
            WritePrimitiveRefsubexpfin(w, n1, k, reff.Xqd1 - min1, info.Xqd1 - min1);
        }
        reff = info;
    }

    // ---- binary_codes_writer.c ----

    private static void WritePrimitiveQuniform(AomWriter w, int n, int v)
    {
        if (n <= 1) return;
        int l = MostSignificantBit(n) + 1;
        int m = (1 << l) - n;
        if (v < m) w.WriteLiteral(v, l - 1);
        else
        {
            w.WriteLiteral(m + ((v - m) >> 1), l - 1);
            w.WriteBit((v - m) & 1);
        }
    }

    private static void WritePrimitiveSubexpfin(AomWriter w, int n, int k, int v)
    {
        int i = 0, mk = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (n <= mk + 3 * a)
            {
                WritePrimitiveQuniform(w, n - mk, v - mk);
                break;
            }
            int tt = v >= mk + a ? 1 : 0;
            w.WriteBit(tt);
            if (tt != 0)
            {
                i++;
                mk += a;
            }
            else
            {
                w.WriteLiteral(v - mk, b);
                break;
            }
        }
    }

    /// <summary>aom_write_primitive_refsubexpfin.</summary>
    private static void WritePrimitiveRefsubexpfin(AomWriter w, int n, int k, int reff, int v)
        => WritePrimitiveSubexpfin(w, n, k, RecenterFiniteNonneg(n, reff, v));

    private static int RecenterNonneg(int r, int v)
    {
        if (v > (r << 1)) return v;
        if (v >= r) return (v - r) << 1;
        return ((r - v) << 1) - 1;
    }

    private static int RecenterFiniteNonneg(int n, int r, int v)
        => (r << 1) <= n ? RecenterNonneg(r, v) : RecenterNonneg(n - 1 - r, n - 1 - v);
}
