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
    /// <summary>g_bit_depth / g_input_bit_depth: 8, or 10 / 12 with the samples in Planes16 (AOM_CODEC_USE_HIGHBITDEPTH).</summary>
    public int BitDepth = 8;
    public ushort[][]? Planes16;
    /// <summary>oxcf.tool_cfg.enable_restoration (AV1E_SET_ENABLE_RESTORATION; libavif turns it off for 12-bit input).</summary>
    public bool EnableRestoration = true;
    public int BaseQindex;
    public int Speed = 6;
    public bool AllowScreenContentTools, UseScreenContentTools, AllowIntrabc, IsScreenContentType;
    /// <summary>Run libaom's screen-content detection (sets the flags above from the source).</summary>
    public bool DetectScreenContent = true;
    /// <summary>oxcf.kf_cfg.enable_intrabc (AV1E_SET_ENABLE_INTRABC): false keeps intrabc off even for screen content.</summary>
    public bool EnableIntrabc = true;
    /// <summary>Test hook: adjusts the speed features after libaom's setup (e.g. to isolate a stage).</summary>
    public Action<AomSpeedFeatures>? SfOverride;
    /// <summary>AOME_SET_TUNING: AOM_TUNE_IQ is libavif's default for still non-identity colour images, AOM_TUNE_SSIM
    /// for identity-matrix colour (libavif never sets a tuning for lossless, so it is ignored at qindex 0).</summary>
    public AomTune Tune;
    /// <summary>Test hook (tune=iq staging): letters of the sub-features handle_tuning enables to switch back off:
    /// q enable_qm, d deltaq_mode, c cdef, s sharpness, u chroma deltaq, m qm-psnr dist metric, a adaptive sharpness.</summary>
    public string? IqOff;
    /// <summary>cfg.g_threads (libaom's row_mt stays at its default 1): 1 encodes single-threaded; from 2 the superblock
    /// rows run in libaom's row-MT wavefront, whose output is the same for every thread count &gt;= 2 (and differs
    /// from the single-threaded one).</summary>
    public int Threads = 1;
}

// Port of libaom 3.14.1 av1_encode_frame / encode_frame_internal / encode_tiles / av1_encode_tile / encode_sb_row /
// encode_rd_sb for one all-intra key frame (one tile, no segmentation, no delta q): the search and the final encode
// that leave the mode info, the coefficients and the reconstruction for the bitstream writer and the loop filters.
/// <summary>The aom_tune_metric values the port implements.</summary>
internal enum AomTune { Psnr, Ssim, Iq }

internal static partial class AomEncoder
{
    internal static (AomComp cpi, AomMacroblock x) EncodeFrame(AomEncodeInput input)
    {
        // handle_tuning (av1_cx_iface.c) for AOM_TUNE_IQ (AOM_TUNE_SSIM only adds the SSIM rdmult scaling)
        var tune = input.BaseQindex != 0 ? input.Tune : AomTune.Psnr;
        bool tuneIq = tune == AomTune.Iq;
        string off = tuneIq ? input.IqOff ?? "" : "";
        bool deltaqVarianceBoost = tuneIq && !off.Contains('d');
        int bd = input.BitDepth;
        var cm = new AomCommon(input.Width, input.Height, input.SsX, input.SsY, input.Monochrome,
            deltaqVarianceBoost ? BLOCK_64X64 : SelectSbSize(input.Width, input.Height, input.Speed), bd);   // Variance Boost: 64x64 SBs
        cm.BaseQindex = input.BaseQindex;
        var cpi = new AomComp { Cm = cm, Speed = input.Speed, AllowScreenContentTools = input.AllowScreenContentTools,
            UseScreenContentTools = input.UseScreenContentTools, AllowIntrabc = input.AllowIntrabc, SbSize = cm.SbSize, Tune = tune,
            BitDepth = bd, UseHighbitdepth = bd > 8 };
        if (tuneIq)
        {
            cpi.UsingQm = !off.Contains('q');
            cpi.QmMinLevel = 2; cpi.QmMaxLevel = 10;   // QM_FIRST_IQ_SSIMULACRA2 / QM_LAST_IQ_SSIMULACRA2
            cpi.Sharpness = off.Contains('s') ? 0 : 7;
            cpi.QmPsnrDistMetric = !off.Contains('m');
            cpi.CdefControl = off.Contains('c') ? 0 : 3;   // CDEF_ADAPTIVE
            cpi.EnableChromaDeltaq = !off.Contains('u');
            cpi.DeltaqVarianceBoost = deltaqVarianceBoost;
            cpi.EnableAdaptiveSharpness = !off.Contains('a');
        }

        // the source frame with libaom's replicated borders (the lookahead copy runs aom_extend_frame_borders)
        cpi.Source = new AomFrameBuffer(input.Width, input.Height, input.SsX, input.SsY, input.Monochrome, bd);
        for (int p = 0; p < cm.NumPlanes; p++)
        {
            int isUv = p > 0 ? 1 : 0;
            int w = cpi.Source.CropWidths[isUv], h = cpi.Source.CropHeights[isUv];
            if (bd > 8)
            {
                var dst16 = cpi.Source.Buffers16[p];
                for (int r = 0; r < h; r++) Array.Copy(input.Planes16![p], r * input.Strides[p], dst16, cpi.Source.Offsets[p] + r * cpi.Source.Strides[p], w);
                ExtendPlane(dst16, cpi.Source.Offsets[p], cpi.Source.Strides[p], w, h, p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> input.SsX,
                    p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> input.SsY);
                continue;
            }
            var dst = cpi.Source.Buffers[p];
            for (int r = 0; r < h; r++) Array.Copy(input.Planes[p], r * input.Strides[p], dst, cpi.Source.Offsets[p] + r * cpi.Source.Strides[p], w);
            ExtendPlane(dst, cpi.Source.Offsets[p], cpi.Source.Strides[p], w, h, p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> input.SsX,
                p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> input.SsY);
        }

        // av1_set_screen_content_options (anti-aliasing aware detection; the fast variant from speed 3)
        // (non-RD pick mode without the hybrid intra search, speed 9: screen content detection is disabled and the
        // tools stay off)
        bool nonrdNoHybrid = input.Speed >= 9;
        if (input.DetectScreenContent && nonrdNoHybrid)
        {
            input.AllowScreenContentTools = input.UseScreenContentTools = input.AllowIntrabc = input.IsScreenContentType = false;
            cpi.AllowScreenContentTools = cpi.UseScreenContentTools = cpi.AllowIntrabc = false;
        }
        else if (input.DetectScreenContent)
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
        // encode_frame_internal: features->allow_intrabc &= oxcf->kf_cfg.enable_intrabc
        cpi.AllowIntrabc &= input.EnableIntrabc;

        // cpi->mt_info.num_workers (av1_compute_num_workers_for_mt / av1_get_max_num_workers): the speed features and
        // the encode stage read it
        cpi.NumWorkers = AomRowMt.ComputeNumWorkers(cm, input.Threads);

        // speed features (framesize independent / dependent / qindex dependent) and the winner mode params
        var sfIn = new AomSpeedFeatureInputs
        {
            Width = input.Width, Height = input.Height, AllowScreenContentTools = input.AllowScreenContentTools,
            UseScreenContentTools = input.UseScreenContentTools, IsScreenContentType = input.IsScreenContentType, BaseQindex = input.BaseQindex,
            NumWorkers = cpi.NumWorkers,
        };
        // avifenc --lossless / quality 100 (quantizer 0): libavif sets rc_min_quantizer = rc_max_quantizer = 0 and
        // AV1E_SET_LOSSLESS, so oxcf.rc_cfg.best_allowed_q = worst_allowed_q = 0 (is_lossless_requested)
        if (input.BaseQindex == 0) { sfIn.BestAllowedQ = 0; sfIn.WorstAllowedQ = 0; }
        sfIn.UseHighBitDepth = bd > 8;
        var seqFlags = new AomSpeedFeatureSeqFlags { enable_restoration = input.EnableRestoration ? 1 : 0 };
        cpi.Sf.SetForFrame(sfIn, seqFlags, cpi.WinnerModeParams, input.Speed);
        cpi.EnableRestoration = seqFlags.enable_restoration != 0;
        input.SfOverride?.Invoke(cpi.Sf);

        // trellis per segment
        bool lossless = input.BaseQindex == 0;
        for (int i = 0; i < 8; ++i) cpi.OptimizeSegArr[i] = lossless ? NO_TRELLIS_OPT : cpi.Sf.rd_sf.optimize_coefficients;

        // av1_set_quantizer: chroma delta q and the quantization matrix levels; av1_init_quantizer
        AomQuantSetup.SetQuantizer(cpi, input.BaseQindex);
        cpi.Quants = new AomQuants(bd, cm.YDcDeltaQ, cm.UDcDeltaQ, cm.UAcDeltaQ, cm.VDcDeltaQ, cm.VAcDeltaQ, cpi.Sharpness);

        // the frame CDFs (key frame defaults for the qindex) and av1_initialize_rd_consts
        cm.Fc = new Av1CdfContext();
        int qCtxQ = input.BaseQindex;
        Av1CdfDefaults.InitializeDefault(cm.Fc, qCtxQ <= 20 ? 0 : qCtxQ <= 60 ? 1 : qCtxQ <= 120 ? 2 : 3);   // get_q_ctx
        cpi.RdRdmult = AomRd.RdMultKeyFrame(input.BaseQindex + cm.YDcDeltaQ, bd, cpi.TuneIq);

        // av1_init_tile_data: allow_update_cdf
        cpi.AllowUpdateCdf = !cpi.DisableCdfUpdate && !DelayWaitForTopRightSb(cpi);

        int sbPixels = 1 << NumPelsLog2Lookup[cm.SbSize];
        int sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2, sbRows = (cm.MiRows + cm.MibSize - 1) >> cm.MibSizeLog2;
        cpi.CbCoeffBuffers = new AomCbCoeffBuffer[sbRows * sbCols];
        for (int i = 0; i < cpi.CbCoeffBuffers.Length; i++) cpi.CbCoeffBuffers[i] = new AomCbCoeffBuffer(sbPixels);
        cpi.ExtCbOffset = new int[cm.MiGridBase.Length * 2];

        // encode_frame_internal's intrabc setup: allow_intrabc &= enable_intrabc, the source hash table (av1_use_hash_me),
        // the full-pel search sites / step (init_motion_estimation, av1_set_mv_search_params)
        cpi.IntrabcUsed = false;
        cpi.AllowIntrabc &= cpi.EnableIntrabcCfg;
        if (cpi.UseHashMe && cpi.Sf.rt_sf.use_nonrd_pick_mode == 0)
            cpi.IntrabcHash = AomHashMotion.BuildFrameTable(cpi.Source, cm.MibSizeLog2, cpi.Sf.mv_sf.hash_max_8x8_intrabc_blocks != 0,
                BlockSizeWide[BLOCK_4X4]);   // mi_alloc_bsize
        cpi.SearchSites = AomMcomp.InitSearchSites();
        cpi.MvStepParam = AomMcomp.InitSearchRange(Math.Max(input.Width, input.Height));
        if (cpi.Sf.rt_sf.use_nonrd_pick_mode == 0 && cpi.AllowIntrabcNow)   // av1_need_dv_costs
            cpi.MbmiExtFrameBase = new AomMbmiExtFrame?[cm.MiGridBase.Length];

        // encoder.c: av1_set_mb_ssim_rdmult_scaling (tune SSIM / IQ / SSIMULACRA2)
        if (cpi.SsimRdmult) cpi.SetMbSsimRdmultScaling();

        // encode_frame_internal: delta q resolution and presence (Variance Boost: delta_q_res by the base qindex)
        cpi.DeltaQRes = 0;
        if (cpi.DeltaqVarianceBoost) cpi.DeltaQRes = input.BaseQindex >= 160 ? 8 : input.BaseQindex >= 120 ? 4 : input.BaseQindex >= 80 ? 2 : 1;
        cpi.DeltaQPresentFlag = cpi.DeltaqVarianceBoost && input.BaseQindex > 0;
        cpi.DeltaqUsed = false;

        // cpi->td.mb
        var x = NewThreadData(cpi, input);
        x.PaletteTokens = cpi.PaletteTokens;

        if (cpi.NumWorkers > 1)
        {
            // oxcf->row_mt && mt_info->num_workers > 1: av1_encode_tiles_row_mt (each other worker's thread data is set
            // up like cpi->td's, as prepare_enc_workers copies cpi->td.mb)
            AomRowMt.EncodeTilesRowMt(cpi, x, () => NewThreadData(cpi, input), input.Threads);
        }
        else
        {
            // av1_encode_tile
            cm.ZeroAboveContext();
            if (cpi.EnableCflIntra) AomCfl.CflInit(x.E.Cfl, input.SsX, input.SsY);
            for (int miRow = cm.TileMiRowStart; miRow < cm.TileMiRowEnd; miRow += cm.MibSize) EncodeSbRow(cpi, x, miRow);
        }

        // intrabc allowed but never selected: reset the flag
        if (cpi.AllowIntrabc && !cpi.IntrabcUsed) cpi.AllowIntrabc = false;
        // no non-zero delta q used: drop delta_q_present_flag
        if (cpi.DeltaQPresentFlag && !cpi.DeltaqUsed) cpi.DeltaQPresentFlag = false;
        return (cpi, x);
    }

    /// <summary>A thread's ThreadData MACROBLOCK for the frame (cpi->td.mb as encode_frame_internal sets it up, and the
    /// row-MT workers' copies of it from prepare_enc_workers): the segment lossless / qindex, the quantizers
    /// (set_q_index), the block planes, the frame-level rate costs from cm->fc (av1_initialize_rd_consts), the tile
    /// CDFs, the winner mode stats, sadperbit, the DV costs and the delta q base.</summary>
    private static AomMacroblock NewThreadData(AomComp cpi, AomEncodeInput input)
    {
        var cm = cpi.Cm;
        var x = new AomMacroblock();
        var xd = x.E;
        bool lossless = input.BaseQindex == 0;
        for (int i = 0; i < 8; ++i)
        {
            xd.Lossless[i] = lossless ? 1 : 0;
            xd.Qindex[i] = input.BaseQindex;
        }

        // av1_frame_init_quantizer / set_q_index
        AomQuantSetup.SetQIndex(cpi, x, input.BaseQindex);

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
        xd.Bd = input.BitDepth;

        // av1_initialize_rd_consts
        x.Errorperbit = AomRd.ErrorPerBit(cpi.RdRdmult);
        AomModeCostFill.Fill(x.ModeCosts, cm.Fc, cpi.EnableFilterIntra);
        x.CoeffCosts.Fill(cm.Fc.Coef, cm.NumPlanes);

        // av1_init_tile_data: the tile's adaptive CDFs start from cm->fc
        x.TileCtx = new Av1CdfContext();
        x.TileCtx.CopyFrom(cm.Fc);

        x.WinnerModeStats = new AomWinnerModeStats[AomRdoptUtils.WinnerModeCountAllowed[cpi.Sf.winner_mode_sf.multi_winner_mode_type]];
        for (int i = 0; i < x.WinnerModeStats.Length; i++) x.WinnerModeStats[i] = new AomWinnerModeStats();

        // sadperbit and the DV costs (av1_initialize_rd_consts: dv_costs from cm->fc->ndvc)
        x.SadPerBit = AomEncodeFrame.SadPerBit(input.BaseQindex, input.BitDepth);
        if (cpi.Sf.rt_sf.use_nonrd_pick_mode == 0 && cpi.AllowIntrabcNow)   // av1_need_dv_costs
        {
            x.DvCosts = new AomDvCosts();
            AomMvCost.FillDvCosts(cm.Fc.Mv, x.DvCosts);
        }

        xd.CurrentBaseQindex = input.BaseQindex;

        // encode_tiles / enc_row_mt_worker_hook: the real-time path preallocates one PC_TREE per thread
        if (cpi.Sf.rt_sf.use_nonrd_pick_mode != 0) x.NonrdPcRoot = new AomPcTree(cm.SbSize);
        return x;
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
        // high bit depth: each block down-converted to 8 bits (downconv_blk) for the colour counts; the variance is the source's
        bool useHbd = src.Hbd;
        int bd = src.BitDepth;
        ushort[] buf16 = src.Buffers16[0];
        var downconv = useHbd ? new byte[kBlockArea] : null;
        long countPalette = 0, countIntrabc = 0, countPhoto = 0;
        int multiplier = fastDetection ? 2 : 1;
        for (int r = 0; r + kBlockHeight <= height; r += kBlockHeight)
        {
            int initialCol = fastDetection && (r / kBlockHeight) % 2 != 0 ? kBlockWidth : 0;
            for (int c = initialCol; c + kBlockWidth <= width; c += kBlockWidth * multiplier)
            {
                int blkOff = src.Offsets[0] + r * stride + c;
                byte[] blk = buf;
                int blkStart = blkOff, blkStride = stride;
                if (useHbd)
                {
                    for (int br = 0; br < kBlockHeight; ++br)
                        for (int bc = 0; bc < kBlockWidth; ++bc) downconv![br * kBlockWidth + bc] = (byte)(buf16[blkOff + br * stride + bc] >> (bd - 8));
                    blk = downconv!;
                    blkStart = 0;
                    blkStride = kBlockWidth;
                }
                bool underThreshold = AomPalette.CountColorsWithThreshold(blk, blkStart, blkStride, kBlockHeight, kBlockWidth, kComplexInitialColorThresh,
                    out int numberOfColors);
                if (numberOfColors > 1 && underThreshold)
                {
                    if (numberOfColors <= kSimpleColorThresh)
                    {
                        ++countPalette;
                        int var = useHbd ? (int)AomHbd.PerpixelVariance(buf16, blkOff, stride, 16, 16, bd) : PerpixelVariance16x16(buf, blkOff, stride);
                        if (var > kVarThresh) ++countIntrabc;
                    }
                    else
                    {
                        DilateBlock(blk, blkStart, blkStride, dilated, kBlockWidth, kBlockHeight, kBlockWidth);
                        underThreshold = AomPalette.CountColorsWithThreshold(dilated, 0, kBlockWidth, kBlockHeight, kBlockWidth,
                            kComplexFinalColorThresh, out numberOfColors);
                        if (underThreshold && (useHbd ? (int)AomHbd.PerpixelVariance(buf16, blkOff, stride, 16, 16, bd) : PerpixelVariance16x16(buf, blkOff, stride)) > kVarThresh)
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
    internal static bool DelayWaitForTopRightSb(AomComp cpi)
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

    /// <summary>aom_extend_frame_borders (high bit depth) for one plane.</summary>
    internal static void ExtendPlane(ushort[] buf, int off, int stride, int w, int h, int borderX, int borderY)
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

    /// <summary>encode_sb_row (with the row-MT steps when cpi.RowMt is set).</summary>
    internal static void EncodeSbRow(AomComp cpi, AomMacroblock x, int miRow)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var rowMt = cpi.RowMt;
        // av1_zero_left_context
        for (int p = 0; p < 3; p++) Array.Clear(xd.LeftEntropyContext[p]);
        Array.Clear(xd.LeftPartitionContext);
        Array.Fill(xd.LeftTxfmContextBuffer, (byte)TxSizeHigh[TX_64X64]);

        // reset the delta q at the beginning of every tile, and of every row with row-MT (no delta lf here)
        if ((miRow == cm.TileMiRowStart || rowMt != null) && cpi.DeltaQPresentFlag) xd.CurrentBaseQindex = cm.BaseQindex;

        int sbRow = (miRow - cm.TileMiRowStart) >> cm.MibSizeLog2;
        int sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2;
        for (int miCol = cm.TileMiColStart, sbCol = 0; miCol < cm.TileMiColEnd; miCol += cm.MibSize, sbCol++)
        {
            // row-MT: wait for the top / top-right superblock, then the row's CDFs (restore / left + top-right average)
            if (rowMt != null && !rowMt.BeforeSb(cpi, x, miRow, miCol, sbRow, sbCol)) return;
            SetCostUpdFreq(cpi, x, miRow, miCol);
            if (cpi.AllIntra) x.IntraSbRdmultModifier = 128;
            x.SourceVariance = uint.MaxValue;
            x.CbCoefBuff = cpi.CbCoeffBuffers[sbRow * sbCols + sbCol];
            x.ColorPaletteThresh = 64;
            x.InitSrcVarInfo(cm.SbSize);
            if (cpi.Sf.rt_sf.use_nonrd_pick_mode != 0) EncodeNonrdSb(cpi, x, miRow, miCol);
            else EncodeRdSb(cpi, x, miRow, miCol);
            // row-MT: the top-right context for the next row, and this superblock done
            rowMt?.AfterSb(cpi, x, miRow, sbRow, sbCol, sbCols);
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
        // (mv costs: inter frames only)
        int dvLevel = cpi.Sf.intra_sf.dv_cost_upd_level;
        if (dvLevel >= INTERNAL_COST_UPD_SBROW_SET && cpi.AllowIntrabcNow && !SkipCostUpdate(cm, miRow, miCol, dvLevel))   // skip_dv_cost_update
            AomMvCost.FillDvCosts(x.TileCtx.Mv, x.DvCosts!);
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
        if (cpi.DeltaQPresentFlag) AomQuantSetup.SetupDeltaQ(cpi, x, miRow, miCol);
        x.TxfmSearchParams.ModeEvalType = DEFAULT_EVAL;
        AomRdStats dummyRdc = default;
        dummyRdc.Invalidate();

        if (sf.part_sf.partition_search_type == VAR_BASED_PARTITION)
        {
            // partition search starting from a variance-based partition
            AomEncodeFrame.SetOffsets(cpi, x, miRow, miCol, cm.SbSize);
            AomVarBasedPart.ChooseVarBasedPartitioning(cpi, x, miRow, miCol);
            var root = new AomPcTree(cm.SbSize);
            AomEncodeFrame.RdUsePartition(cpi, x, miRow, miCol, cm.SbSize, out _, out _, true, root);
            return;
        }
        if (sf.part_sf.partition_search_type != SEARCH_PARTITION)
            throw new NotSupportedException("FIXED_PARTITION is not used by the all-intra speeds");

        // set_max_min_partition_size (no auto max partition for intra frames)
        x.MaxPartitionSize = Math.Min(sf.part_sf.default_max_partition_size, DimToSize(cpi.MaxPartitionSizeCfg));
        x.MinPartitionSize = Math.Max(sf.part_sf.default_min_partition_size, DimToSize(cpi.MinPartitionSizeCfg));
        x.MaxPartitionSize = Math.Min(x.MaxPartitionSize, cm.SbSize);
        x.MinPartitionSize = Math.Min(x.MinPartitionSize, cm.SbSize);

        AomIntraModeSearch.ProduceGradientsForSb(cpi, x, cm.SbSize, miRow, miCol);
        var pcRoot = new AomPcTree(cm.SbSize);
        long noneRd = 0;
        AomEncodeFrame.RdPickPartition(cpi, x, miRow, miCol, cm.SbSize, ref dummyRdc, dummyRdc, pcRoot, ref noneRd, false, null);
    }

    /// <summary>encode_nonrd_sb (VAR_BASED_PARTITION, no segment skip).</summary>
    private static void EncodeNonrdSb(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        if (cpi.DeltaQPresentFlag) AomQuantSetup.SetupDeltaQNonrd(cpi, x, miRow, miCol);
        // set a variance-based partition
        AomEncodeFrame.SetOffsets(cpi, x, miRow, miCol, cm.SbSize);
        AomVarBasedPart.ChooseVarBasedPartitioning(cpi, x, miRow, miCol);
        x.CbOffset[0] = 0;
        x.CbOffset[1] = 0;
        // (skip_cdef_sb is off in the all-intra mode)
        AomEncodeFrame.NonrdUsePartition(cpi, x, miRow, miCol, cm.SbSize, x.NonrdPcRoot!);
    }

    /// <summary>dim_to_size.</summary>
    private static int DimToSize(int dim) => dim switch
    {
        4 => BLOCK_4X4, 8 => BLOCK_8X8, 16 => BLOCK_16X16, 32 => BLOCK_32X32, 64 => BLOCK_64X64, 128 => BLOCK_128X128,
        _ => throw new ArgumentOutOfRangeException(nameof(dim)),
    };
}
