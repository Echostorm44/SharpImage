using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>Input to the libaom-port all-intra encoder: 8-bit planes (4:2:0 / 4:4:4 / 4:0:0), the frame qindex and speed.</summary>
internal sealed class AomEncodeInput
{
    public int Width, Height, SsX = 1, SsY = 1;
    public bool Monochrome;
    public byte[][] Planes = null!;
    public int[] Strides = null!;
    public int BaseQindex;
    public int Speed = 6;
    public bool AllowScreenContentTools, UseScreenContentTools, AllowIntrabc, IsScreenContentType;
    /// <summary>Run libaom's screen-content detection (sets the flags above from the source).</summary>
    public bool DetectScreenContent = true;
    /// <summary>Test hook: adjusts the speed features after libaom's setup (e.g. to isolate a stage).</summary>
    public Action<AomSpeedFeatures>? SfOverride;
}

// Port of libaom 3.14.1 av1_encode_frame / encode_frame_internal / encode_tiles / av1_encode_tile / encode_sb_row /
// encode_rd_sb for one all-intra key frame (one tile, no segmentation, no delta q): the search and the final encode
// that leave the mode info, the coefficients and the reconstruction for the bitstream writer and the loop filters.
internal static class AomEncoder
{
    internal static (AomComp cpi, AomMacroblock x) EncodeFrame(AomEncodeInput input)
    {
        var cm = new AomCommon(input.Width, input.Height, input.SsX, input.SsY, input.Monochrome,
            SelectSbSize(input.Width, input.Height, input.Speed));
        cm.BaseQindex = input.BaseQindex;
        var cpi = new AomComp { Cm = cm, Speed = input.Speed, AllowScreenContentTools = input.AllowScreenContentTools,
            UseScreenContentTools = input.UseScreenContentTools, SbSize = cm.SbSize };

        // the source frame with libaom's replicated borders (the lookahead copy runs aom_extend_frame_borders)
        cpi.Source = new AomFrameBuffer(input.Width, input.Height, input.SsX, input.SsY, input.Monochrome);
        for (int p = 0; p < cm.NumPlanes; p++)
        {
            int isUv = p > 0 ? 1 : 0;
            int w = cpi.Source.CropWidths[isUv], h = cpi.Source.CropHeights[isUv];
            var dst = cpi.Source.Buffers[p];
            for (int r = 0; r < h; r++) Array.Copy(input.Planes[p], r * input.Strides[p], dst, cpi.Source.Offsets[p] + r * cpi.Source.Strides[p], w);
            ExtendPlane(dst, cpi.Source.Offsets[p], cpi.Source.Strides[p], w, h, p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> input.SsX,
                p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> input.SsY);
        }

        // av1_set_screen_content_options (anti-aliasing aware detection; the fast variant from speed 3)
        if (input.DetectScreenContent)
        {
            var (sct, ibc, isSc) = EstimateScreenContent(cpi.Source, input.Speed >= 3);
            input.AllowScreenContentTools = sct;
            input.UseScreenContentTools = sct;
            input.AllowIntrabc = ibc;
            input.IsScreenContentType = isSc;
            cpi.AllowScreenContentTools = sct;
            cpi.UseScreenContentTools = sct;
            cpi.AllowIntrabc = ibc;
        }

        // speed features (framesize independent / dependent / qindex dependent) and the winner mode params
        var sfIn = new AomSpeedFeatureInputs
        {
            Width = input.Width, Height = input.Height, AllowScreenContentTools = input.AllowScreenContentTools,
            UseScreenContentTools = input.UseScreenContentTools, IsScreenContentType = input.IsScreenContentType, BaseQindex = input.BaseQindex,
        };
        cpi.Sf.SetForFrame(sfIn, new AomSpeedFeatureSeqFlags(), cpi.WinnerModeParams, input.Speed);
        input.SfOverride?.Invoke(cpi.Sf);

        // lossless / qindex / trellis per segment
        var x = new AomMacroblock();
        var xd = x.E;
        bool lossless = input.BaseQindex == 0;
        for (int i = 0; i < 8; ++i)
        {
            xd.Lossless[i] = lossless ? 1 : 0;
            xd.Qindex[i] = input.BaseQindex;
            cpi.OptimizeSegArr[i] = lossless ? NO_TRELLIS_OPT : cpi.Sf.rd_sf.optimize_coefficients;
        }

        // av1_frame_init_quantizer / set_q_index
        var quants = new AomQuants(8, 0, 0, 0, 0, 0, cpi.Sharpness);
        x.Qindex = input.BaseQindex;
        for (int p = 0; p < 3; p++)
        {
            var mp = x.Plane[p];
            int q = input.BaseQindex;
            mp.QuantFp0 = quants.QuantFp[p, q, 0]; mp.QuantFp1 = quants.QuantFp[p, q, 1];
            mp.RoundFp0 = quants.RoundFp[p, q, 0]; mp.RoundFp1 = quants.RoundFp[p, q, 1];
            mp.Quant0 = quants.Quant[p, q, 0]; mp.Quant1 = quants.Quant[p, q, 1];
            mp.QuantShift0 = quants.QuantShift[p, q, 0]; mp.QuantShift1 = quants.QuantShift[p, q, 1];
            mp.Zbin0 = quants.Zbin[p, q, 0]; mp.Zbin1 = quants.Zbin[p, q, 1];
            mp.Round0 = quants.Round[p, q, 0]; mp.Round1 = quants.Round[p, q, 1];
            mp.Dequant0 = quants.Dequant[p, q, 0]; mp.Dequant1 = quants.Dequant[p, q, 1];
        }

        // init_encode_frame_mb_context / av1_setup_block_planes
        for (int p = 0; p < 3; p++)
        {
            xd.Plane[p].PlaneType = p == 0 ? 0 : 1;
            xd.Plane[p].SubsamplingX = p == 0 ? 0 : input.SsX;
            xd.Plane[p].SubsamplingY = p == 0 ? 0 : input.SsY;
            xd.AboveEntropyContext[p] = cm.AboveEntropy[p];
        }
        xd.AbovePartitionContext = cm.AbovePartition;
        xd.AboveTxfmContext = cm.AboveTxfm;
        xd.Bd = 8;

        // the frame CDFs (key frame defaults for the qindex) and av1_initialize_rd_consts
        cm.Fc = new Av1CdfContext();
        int qCtxQ = input.BaseQindex;
        Av1CdfDefaults.InitializeDefault(cm.Fc, qCtxQ <= 20 ? 0 : qCtxQ <= 60 ? 1 : qCtxQ <= 120 ? 2 : 3);   // get_q_ctx
        cpi.RdRdmult = AomRd.RdMultKeyFrame(input.BaseQindex, 8);
        x.Errorperbit = AomRd.ErrorPerBit(cpi.RdRdmult);
        AomModeCostFill.Fill(x.ModeCosts, cm.Fc, cpi.EnableFilterIntra);
        x.CoeffCosts.Fill(cm.Fc.Coef, cm.NumPlanes);

        // av1_init_tile_data: the tile's adaptive CDFs start from cm->fc
        x.TileCtx = new Av1CdfContext();
        x.TileCtx.CopyFrom(cm.Fc);
        cpi.AllowUpdateCdf = !cpi.DisableCdfUpdate && !DelayWaitForTopRightSb(cpi);

        int sbPixels = 1 << NumPelsLog2Lookup[cm.SbSize];
        int sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2, sbRows = (cm.MiRows + cm.MibSize - 1) >> cm.MibSizeLog2;
        cpi.CbCoeffBuffers = new AomCbCoeffBuffer[sbRows * sbCols];
        for (int i = 0; i < cpi.CbCoeffBuffers.Length; i++) cpi.CbCoeffBuffers[i] = new AomCbCoeffBuffer(sbPixels);
        cpi.ExtCbOffset = new int[cm.MiGridBase.Length * 2];
        x.WinnerModeStats = new AomWinnerModeStats[AomRdoptUtils.WinnerModeCountAllowed[cpi.Sf.winner_mode_sf.multi_winner_mode_type]];
        for (int i = 0; i < x.WinnerModeStats.Length; i++) x.WinnerModeStats[i] = new AomWinnerModeStats();

        // av1_encode_tile
        cm.ZeroAboveContext();
        if (cpi.EnableCflIntra) AomCfl.CflInit(xd.Cfl, input.SsX, input.SsY);
        for (int miRow = cm.TileMiRowStart; miRow < cm.TileMiRowEnd; miRow += cm.MibSize) EncodeSbRow(cpi, x, miRow);
        return (cpi, x);
    }

    /// <summary>estimate_screen_content_antialiasing_aware (8-bit; the all-intra default screen detection mode) on the
    /// source's luma over its 8-aligned size. Returns (allow_screen_content_tools, allow_intrabc, is_screen_content_type).</summary>
    internal static (bool Sct, bool Intrabc, bool IsScreenContentType) EstimateScreenContent(AomFrameBuffer src, bool fastDetection)
    {
        const int kBlockWidth = 16, kBlockHeight = 16, kBlockArea = kBlockWidth * kBlockHeight;
        const int kSimpleColorThresh = 4, kComplexInitialColorThresh = 40, kComplexFinalColorThresh = 6, kVarThresh = 5;
        int width = (src.CropWidths[0] + 7) & ~7, height = (src.CropHeights[0] + 7) & ~7;   // y_width / y_height (aligned)
        long area = (long)width * height;
        byte[] buf = src.Buffers[0];
        int stride = src.Strides[0];
        var dilated = new byte[kBlockArea];
        long countPalette = 0, countIntrabc = 0, countPhoto = 0;
        int multiplier = fastDetection ? 2 : 1;
        for (int r = 0; r + kBlockHeight <= height; r += kBlockHeight)
        {
            int initialCol = fastDetection && (r / kBlockHeight) % 2 != 0 ? kBlockWidth : 0;
            for (int c = initialCol; c + kBlockWidth <= width; c += kBlockWidth * multiplier)
            {
                int blkOff = src.Offsets[0] + r * stride + c;
                bool underThreshold = AomPalette.CountColorsWithThreshold(buf, blkOff, stride, kBlockHeight, kBlockWidth, kComplexInitialColorThresh,
                    out int numberOfColors);
                if (numberOfColors > 1 && underThreshold)
                {
                    if (numberOfColors <= kSimpleColorThresh)
                    {
                        ++countPalette;
                        if (PerpixelVariance16x16(buf, blkOff, stride) > kVarThresh) ++countIntrabc;
                    }
                    else
                    {
                        DilateBlock(buf, blkOff, stride, dilated, kBlockWidth, kBlockHeight, kBlockWidth);
                        underThreshold = AomPalette.CountColorsWithThreshold(dilated, 0, kBlockWidth, kBlockHeight, kBlockWidth,
                            kComplexFinalColorThresh, out numberOfColors);
                        if (underThreshold && PerpixelVariance16x16(buf, blkOff, stride) > kVarThresh)
                        {
                            ++countPalette;
                            ++countIntrabc;
                        }
                    }
                }
                else if (numberOfColors > kComplexInitialColorThresh) ++countPhoto;
            }
        }
        if (fastDetection)
        {
            countPhoto *= multiplier;
            countPalette *= multiplier;
            countIntrabc *= multiplier;
        }
        bool sct = (countPalette - countPhoto / 16) * kBlockArea * 10 > area;
        bool intrabc = sct && (countIntrabc - countPhoto / 16) * kBlockArea * 12 > area;
        bool isScreenContentType = intrabc || (countPalette * kBlockArea * 15 > area * 4 && countIntrabc * kBlockArea * 30 > area);
        return (sct, intrabc, isScreenContentType);
    }

    /// <summary>av1_get_perpixel_variance for a BLOCK_16X16 luma block.</summary>
    private static int PerpixelVariance16x16(byte[] buf, int off, int stride)
    {
        uint var = AomIntraModeSearch.VarianceVsZero(buf, off, stride, 16, 16, out _);
        return (int)((var + 128) >> 8);
    }

    /// <summary>av1_find_dominant_value.</summary>
    private static byte FindDominantValue(byte[] src, int off, int stride, int rows, int cols)
    {
        Span<uint> valueCount = stackalloc uint[256];
        valueCount.Clear();
        uint dominantValueCount = 0;
        byte dominantValue = 0;
        for (int r = 0; r < rows; ++r)
            for (int c = 0; c < cols; ++c)
            {
                byte value = src[off + r * stride + c];
                valueCount[value]++;
                if (valueCount[value] > dominantValueCount) { dominantValue = value; dominantValueCount = valueCount[value]; }
            }
        return dominantValue;
    }

    /// <summary>av1_dilate_block.</summary>
    private static void DilateBlock(byte[] src, int off, int srcStride, byte[] dilated, int dilatedStride, int rows, int cols)
    {
        byte dominantValue = FindDominantValue(src, off, srcStride, rows, cols);
        for (int r = 0; r < rows; ++r)
            for (int c = 0; c < cols; ++c) dilated[r * dilatedStride + c] = src[off + r * srcStride + c];
        for (int r = 0; r < rows; ++r)
            for (int c = 0; c < cols; ++c)
            {
                byte value = src[off + r * srcStride + c];
                if (value != dominantValue) continue;
                if (r != 0) dilated[(r - 1) * dilatedStride + c] = value;
                if (r != rows - 1) dilated[(r + 1) * dilatedStride + c] = value;
                if (c != 0) dilated[r * dilatedStride + (c - 1)] = value;
                if (c != cols - 1) dilated[r * dilatedStride + (c + 1)] = value;
                if (r != 0 && c != 0) dilated[(r - 1) * dilatedStride + (c - 1)] = value;
                if (r != 0 && c != cols - 1) dilated[(r - 1) * dilatedStride + (c + 1)] = value;
                if (r != rows - 1 && c != 0) dilated[(r + 1) * dilatedStride + (c - 1)] = value;
                if (r != rows - 1 && c != cols - 1) dilated[(r + 1) * dilatedStride + (c + 1)] = value;
            }
    }

    /// <summary>av1_select_sb_size (AOM_SUPERBLOCK_SIZE_DYNAMIC, ALLINTRA, deltaq objective, no resize / superres /
    /// spatial layers).</summary>
    internal static int SelectSbSize(int width, int height, int speed)
    {
        bool is480pOrLesser = Math.Min(width, height) <= 480;
        if (speed >= 1 && is480pOrLesser) return BLOCK_64X64;
        bool is4kOrLarger = Math.Min(width, height) >= 2160;
        if (speed >= 9 && !is4kOrLarger) return BLOCK_64X64;
        return BLOCK_128X128;
    }

    /// <summary>delay_wait_for_top_right_sb (ALLINTRA).</summary>
    private static bool DelayWaitForTopRightSb(AomComp cpi)
    {
        var sf = cpi.Sf;
        return sf.inter_sf.coeff_cost_upd_level <= INTERNAL_COST_UPD_TILE && sf.inter_sf.mode_cost_upd_level <= INTERNAL_COST_UPD_TILE &&
               sf.intra_sf.dv_cost_upd_level <= INTERNAL_COST_UPD_TILE;
    }

    /// <summary>aom_extend_frame_borders for one plane: replicate the edge samples into the border.</summary>
    internal static void ExtendPlane(byte[] buf, int off, int stride, int w, int h, int borderX, int borderY)
    {
        for (int r = 0; r < h; r++)
        {
            int row = off + r * stride;
            buf.AsSpan(row - borderX, borderX).Fill(buf[row]);
            buf.AsSpan(row + w, borderX).Fill(buf[row + w - 1]);
        }
        int rowLen = w + 2 * borderX;
        for (int r = 1; r <= borderY; r++)
        {
            Array.Copy(buf, off - borderX, buf, off - borderX - r * stride, rowLen);
            Array.Copy(buf, off - borderX + (h - 1) * stride, buf, off - borderX + (h - 1 + r) * stride, rowLen);
        }
    }

    /// <summary>encode_sb_row (single-threaded).</summary>
    private static void EncodeSbRow(AomComp cpi, AomMacroblock x, int miRow)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        // av1_zero_left_context
        for (int p = 0; p < 3; p++) Array.Clear(xd.LeftEntropyContext[p]);
        Array.Clear(xd.LeftPartitionContext);
        Array.Fill(xd.LeftTxfmContextBuffer, (byte)TxSizeHigh[TX_64X64]);

        int sbRow = miRow >> cm.MibSizeLog2;
        int sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2;
        for (int miCol = cm.TileMiColStart, sbCol = 0; miCol < cm.TileMiColEnd; miCol += cm.MibSize, sbCol++)
        {
            SetCostUpdFreq(cpi, x, miRow, miCol);
            if (cpi.AllIntra) x.IntraSbRdmultModifier = 128;
            x.SourceVariance = uint.MaxValue;
            x.CbCoefBuff = cpi.CbCoeffBuffers[sbRow * sbCols + sbCol];
            // (produce_gradients_for_sb: the HOG is computed without the gradient cache; identical results)
            x.InitSrcVarInfo(cm.SbSize);
            EncodeRdSb(cpi, x, miRow, miCol);
        }
    }

    /// <summary>av1_set_cost_upd_freq (intra frames: coefficient and mode costs).</summary>
    private static void SetCostUpdFreq(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        if (cpi.DisableCdfUpdate) return;
        int coeffLevel = cpi.Sf.inter_sf.coeff_cost_upd_level;
        if (coeffLevel >= INTERNAL_COST_UPD_SBROW_SET && !SkipCostUpdate(cm, miRow, miCol, coeffLevel))
            x.CoeffCosts.Fill(x.TileCtx.Coef, cm.NumPlanes);
        int modeLevel = cpi.Sf.inter_sf.mode_cost_upd_level;
        if (modeLevel >= INTERNAL_COST_UPD_SBROW_SET && !SkipCostUpdate(cm, miRow, miCol, modeLevel))
            AomModeCostFill.Fill(x.ModeCosts, x.TileCtx, cpi.EnableFilterIntra);
    }

    /// <summary>skip_cost_update.</summary>
    private static bool SkipCostUpdate(AomCommon cm, int miRow, int miCol, int updLevel)
    {
        if (updLevel == INTERNAL_COST_UPD_SB) return false;
        if (updLevel == INTERNAL_COST_UPD_OFF) return true;
        if (miCol != cm.TileMiColStart) return true;
        if (updLevel == INTERNAL_COST_UPD_SBROW_SET)
        {
            int sbRow = (miRow - cm.TileMiRowStart) >> cm.MibSizeLog2;
            int sbSize = cm.MibSize * 4;
            int tileHeight = (cm.TileMiRowEnd - cm.TileMiRowStart) * 4;
            int updateFreqSbRows = sbSize != 128 ? 4 : 2;
            int updateFreqNumRows = sbSize * updateFreqSbRows;
            int numUpdatesPerTile = (tileHeight + updateFreqNumRows - 1) / updateFreqNumRows;
            int numRowsUpdatePerTile = numUpdatesPerTile * sbSize;
            int numSbRowsPerUpdate = (tileHeight + numRowsUpdatePerTile - 1) / numRowsUpdatePerTile;
            if (sbRow % numSbRowsPerUpdate != 0) return true;
        }
        return false;
    }

    /// <summary>encode_rd_sb (SEARCH_PARTITION, single pass).</summary>
    private static void EncodeRdSb(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        // init_encode_rd_sb
        x.Cnn.Valid = false;
        x.TxfmSearchParams.ModeEvalType = DEFAULT_EVAL;
        AomRdStats dummyRdc = default;
        dummyRdc.Invalidate();

        if (sf.part_sf.partition_search_type != SEARCH_PARTITION)
            throw new NotSupportedException("only SEARCH_PARTITION (the all-intra RD speeds) is ported");

        // set_max_min_partition_size (no auto max partition for intra frames)
        x.MaxPartitionSize = Math.Min(sf.part_sf.default_max_partition_size, DimToSize(cpi.MaxPartitionSizeCfg));
        x.MinPartitionSize = Math.Max(sf.part_sf.default_min_partition_size, DimToSize(cpi.MinPartitionSizeCfg));
        x.MaxPartitionSize = Math.Min(x.MaxPartitionSize, cm.SbSize);
        x.MinPartitionSize = Math.Min(x.MinPartitionSize, cm.SbSize);

        var pcRoot = new AomPcTree(cm.SbSize);
        long noneRd = 0;
        AomEncodeFrame.RdPickPartition(cpi, x, miRow, miCol, cm.SbSize, ref dummyRdc, dummyRdc, pcRoot, ref noneRd, false, null);
    }

    /// <summary>dim_to_size.</summary>
    private static int DimToSize(int dim) => dim switch
    {
        4 => BLOCK_4X4, 8 => BLOCK_8X8, 16 => BLOCK_16X16, 32 => BLOCK_32X32, 64 => BLOCK_64X64, 128 => BLOCK_128X128,
        _ => throw new ArgumentOutOfRangeException(nameof(dim)),
    };
}
