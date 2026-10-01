using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>update_cdf (aom_dsp/prob.h) on SharpImage's CDF layout (dav1d's: nsymbs-1 inverse-cumulative values then the
/// adaptation counter).</summary>
internal static class AomCdf
{
    internal static void Update(ushort[] cdf, int val, int nsymbs)
    {
        int n = nsymbs - 1;
        uint count = cdf[n];
        int rate = 4 + (int)(count >> 4) + (n > 2 ? 1 : 0);
        int i = 0;
        for (; i < val; i++) cdf[i] += (ushort)((32768 - cdf[i]) >> rate);
        for (; i < n; i++) cdf[i] -= (ushort)(cdf[i] >> rate);
        cdf[n] = (ushort)(count + (count < 32 ? 1u : 0u));
    }
}

/// <summary>RD_SEARCH_MACROBLOCK_CONTEXT.</summary>
internal sealed class AomSearchMbContext
{
    public readonly byte[] A = new byte[32 * 3], L = new byte[32 * 3];
    public readonly byte[] Sa = new byte[32], Sl = new byte[32];
    public readonly byte[] Ta = new byte[32], Tl = new byte[32];
    public int PTa, PTl;   // above / left txfm context offsets
}

/// <summary>CB_COEFF_BUFFER of one superblock: the final coefficients, eobs and txb contexts the bitstream writer codes.</summary>
internal sealed class AomCbCoeffBuffer
{
    public readonly int[][] Tcoeff = new int[3][];
    public readonly ushort[][] Eobs = new ushort[3][];
    public readonly byte[][] EntropyCtx = new byte[3][];

    public AomCbCoeffBuffer(int sbPixels)
    {
        for (int p = 0; p < 3; p++) { Tcoeff[p] = new int[sbPixels]; Eobs[p] = new ushort[sbPixels / 16]; EntropyCtx[p] = new byte[sbPixels / 16]; }
    }
}

internal sealed partial class AomComp
{
    public AomCommon Cm = null!;
    public AomFrameBuffer Source = null!;
    public int RdRdmult;             // cpi->rd.RDMULT
    public bool AllowUpdateCdf = true;   // tile_data->allow_update_cdf
    public bool DisableCdfUpdate;
    // x->txfm_search_info.txb_split_count of the final encode (OUTPUT_ENABLED): intra blocks whose tx size is not the
    // block's largest. Zero turns the frame's TX_MODE_SELECT into TX_MODE_LARGEST before the bitstream is written.
    public int TxbSplitCount;
    // CB_COEFF_BUFFER per superblock (row-major over the frame's SBs)
    public AomCbCoeffBuffer[] CbCoeffBuffers = Array.Empty<AomCbCoeffBuffer>();
    // mbmi_ext_frame cb_offset per mi (y, uv)
    public int[] ExtCbOffset = Array.Empty<int>();
}

internal sealed partial class AomMacroblock
{
    public Av1CdfContext TileCtx = null!;   // xd->tile_ctx
    public readonly int[] CbOffset = new int[2];
    public AomCbCoeffBuffer CbCoefBuff = null!;
    public byte[] TxTypeMapScratch = new byte[32 * 32];   // txfm_info->tx_type_map_
    public int TxfmSkip;                                  // txfm_info->skip_txfm
    public bool UseMbModeCache;
    public AomMbModeInfo? MbModeCache;
    public int MinPartitionSize = BLOCK_4X4, MaxPartitionSize = BLOCK_128X128;   // sb_enc
    // x->part_search_info (the intra CNN partition model's per-SB cache and quad tree index)
    public readonly AomCnnPartitionCache Cnn = new();
    public byte[] EmptyColorMap = new byte[128 * 128];
}

// Port of libaom 3.14.1 partition_search.c / encodeframe_utils.c / encodetxb.c block-level encoding for intra frames:
// av1_set_offsets, setup_block_rdmult, pick_sb_modes, encode_superblock, encode_b, encode_sb, av1_update_state,
// update_stats / av1_sum_intra_stats, av1_update_intra_mb_txb_context, save / restore context, partition contexts.
internal static partial class AomEncodeFrame
{
    public const int OUTPUT_ENABLED = 0, DRY_RUN_NORMAL = 1, DRY_RUN_COSTCOEFFS = 2;
    private const int MAX_MIB_MASK = 31;

    /// <summary>get_sqr_bsize_idx.</summary>
    internal static int SqrBsizeIdx(int bsize) => bsize switch
    {
        BLOCK_4X4 => 0, BLOCK_8X8 => 1, BLOCK_16X16 => 2, BLOCK_32X32 => 3, BLOCK_64X64 => 4, BLOCK_128X128 => 5, _ => 6,
    };

    /// <summary>get_partition_subsize.</summary>
    internal static int PartitionSubsize(int bsize, int partition)
    {
        int sqrBsizeIdx = SqrBsizeIdx(bsize);
        return sqrBsizeIdx >= 6 ? 255 : SubsizeLookup[partition * 6 + sqrBsizeIdx];
    }

    /// <summary>partition_cdf_length.</summary>
    internal static int PartitionCdfLength(int bsize)
        => bsize <= BLOCK_8X8 ? 4 : bsize == BLOCK_128X128 ? 8 : 10;

    /// <summary>partition_plane_context.</summary>
    internal static int PartitionPlaneContext(AomMacroblockD xd, int miRow, int miCol, int bsize)
    {
        int bsl = MiSizeWideLog2[bsize] - MiSizeWideLog2[BLOCK_8X8];
        int above = (xd.AbovePartitionContext[miCol] >> bsl) & 1;
        int left = (xd.LeftPartitionContext[miRow & MAX_MIB_MASK] >> bsl) & 1;
        return (left * 2 + above) + bsl * 4;
    }

    /// <summary>update_partition_context.</summary>
    internal static void UpdatePartitionContext(AomMacroblockD xd, int miRow, int miCol, int subsize, int bsize)
    {
        xd.AbovePartitionContext.AsSpan(miCol, MiSizeWide[bsize]).Fill(PartitionContextLookup[subsize * 2 + 0]);
        xd.LeftPartitionContext.AsSpan(miRow & MAX_MIB_MASK, MiSizeHigh[bsize]).Fill(PartitionContextLookup[subsize * 2 + 1]);
    }

    /// <summary>update_ext_partition_context.</summary>
    internal static void UpdateExtPartitionContext(AomMacroblockD xd, int miRow, int miCol, int subsize, int bsize, int partition)
    {
        if (bsize < BLOCK_8X8) return;
        int hbs = MiSizeWide[bsize] / 2;
        int bsize2 = PartitionSubsize(bsize, PARTITION_SPLIT);
        switch (partition)
        {
            case PARTITION_SPLIT:
                if (bsize != BLOCK_8X8) break;
                UpdatePartitionContext(xd, miRow, miCol, subsize, bsize);
                break;
            case PARTITION_NONE: case PARTITION_HORZ: case PARTITION_VERT: case PARTITION_HORZ_4: case PARTITION_VERT_4:
                UpdatePartitionContext(xd, miRow, miCol, subsize, bsize);
                break;
            case PARTITION_HORZ_A:
                UpdatePartitionContext(xd, miRow, miCol, bsize2, subsize);
                UpdatePartitionContext(xd, miRow + hbs, miCol, subsize, subsize);
                break;
            case PARTITION_HORZ_B:
                UpdatePartitionContext(xd, miRow, miCol, subsize, subsize);
                UpdatePartitionContext(xd, miRow + hbs, miCol, bsize2, subsize);
                break;
            case PARTITION_VERT_A:
                UpdatePartitionContext(xd, miRow, miCol, bsize2, subsize);
                UpdatePartitionContext(xd, miRow, miCol + hbs, subsize, subsize);
                break;
            case PARTITION_VERT_B:
                UpdatePartitionContext(xd, miRow, miCol, subsize, subsize);
                UpdatePartitionContext(xd, miRow, miCol + hbs, bsize2, subsize);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(partition));
        }
    }

    /// <summary>av1_save_context.</summary>
    internal static void SaveContext(AomMacroblock x, AomSearchMbContext ctx, int miRow, int miCol, int bsize, int numPlanes)
    {
        var xd = x.E;
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];
        for (int p = 0; p < numPlanes; ++p)
        {
            int txCol = miCol, txRow = miRow & MAX_MIB_MASK;
            var pd = xd.Plane[p];
            Array.Copy(xd.AboveEntropyContext[p], txCol >> pd.SubsamplingX, ctx.A, miWidth * p, miWidth >> pd.SubsamplingX);
            Array.Copy(xd.LeftEntropyContext[p], txRow >> pd.SubsamplingY, ctx.L, miHeight * p, miHeight >> pd.SubsamplingY);
        }
        Array.Copy(xd.AbovePartitionContext, miCol, ctx.Sa, 0, miWidth);
        Array.Copy(xd.LeftPartitionContext, miRow & MAX_MIB_MASK, ctx.Sl, 0, miHeight);
        Array.Copy(xd.AboveTxfmContext, xd.AboveTxfmContextOffset, ctx.Ta, 0, miWidth);
        Array.Copy(xd.LeftTxfmContextBuffer, xd.LeftTxfmContextOffset, ctx.Tl, 0, miHeight);
        ctx.PTa = xd.AboveTxfmContextOffset;
        ctx.PTl = xd.LeftTxfmContextOffset;
    }

    /// <summary>av1_restore_context.</summary>
    internal static void RestoreContext(AomMacroblock x, AomSearchMbContext ctx, int miRow, int miCol, int bsize, int numPlanes)
    {
        var xd = x.E;
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];
        for (int p = 0; p < numPlanes; p++)
        {
            int txCol = miCol, txRow = miRow & MAX_MIB_MASK;
            var pd = xd.Plane[p];
            Array.Copy(ctx.A, miWidth * p, xd.AboveEntropyContext[p], txCol >> pd.SubsamplingX, miWidth >> pd.SubsamplingX);
            Array.Copy(ctx.L, miHeight * p, xd.LeftEntropyContext[p], txRow >> pd.SubsamplingY, miHeight >> pd.SubsamplingY);
        }
        Array.Copy(ctx.Sa, 0, xd.AbovePartitionContext, miCol, miWidth);
        Array.Copy(ctx.Sl, 0, xd.LeftPartitionContext, miRow & MAX_MIB_MASK, miHeight);
        xd.AboveTxfmContextOffset = ctx.PTa;
        xd.LeftTxfmContextOffset = ctx.PTl;
        Array.Copy(ctx.Ta, 0, xd.AboveTxfmContext, xd.AboveTxfmContextOffset, miWidth);
        Array.Copy(ctx.Tl, 0, xd.LeftTxfmContextBuffer, xd.LeftTxfmContextOffset, miHeight);
    }

    /// <summary>setup_pred_plane: the block's sample offset in a plane (4-wide / high chroma blocks at odd mi use the pair's origin).</summary>
    private static void SetupPredPlane(ref AomBuf2d dst, int bsize, byte[] buf, int planeOffset, int width, int height, int stride,
        int miRow, int miCol, int ssX, int ssY, ushort[]? buf16 = null)
    {
        dst.Buf16 = buf16!;
        if (ssY != 0 && (miRow & 1) != 0 && MiSizeHigh[bsize] == 1) miRow -= 1;
        if (ssX != 0 && (miCol & 1) != 0 && MiSizeWide[bsize] == 1) miCol -= 1;
        int px = (4 * miCol) >> ssX, py = (4 * miRow) >> ssY;
        dst.Buf = buf;
        dst.Offset = planeOffset + py * stride + px;
        dst.Width = width;
        dst.Height = height;
        dst.Stride = stride;
    }

    /// <summary>av1_setup_src_planes.</summary>
    internal static void SetupSrcPlanes(AomComp cpi, AomMacroblock x, int miRow, int miCol, int numPlanes, int bsize)
    {
        var src = cpi.Source;
        for (int i = 0; i < numPlanes; i++)
        {
            int isUv = i > 0 ? 1 : 0;
            var pd = x.E.Plane[i];
            SetupPredPlane(ref x.Plane[i].Src, bsize, src.Buffers[i], src.Offsets[i], src.CropWidths[isUv], src.CropHeights[isUv], src.Strides[i],
                miRow, miCol, pd.SubsamplingX, pd.SubsamplingY, src.Buffers16[i]);
        }
    }

    /// <summary>av1_set_offsets_without_segment_id.</summary>
    internal static void SetOffsetsWithoutSegmentId(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];

        // set_mode_info_offsets / set_mi_offsets
        int gridIdx = miRow * cm.MiStride + miCol;
        cm.MiGridBase[gridIdx] = cm.MiAlloc[gridIdx];
        xd.MiGrid = cm.MiGridBase;
        xd.MiStride = cm.MiStride;
        xd.MiOffset = gridIdx;
        xd.Mi0 = cm.MiAlloc[gridIdx];
        xd.TxTypeMap = cm.TxTypeMap;
        xd.TxTypeMapOffset = gridIdx;
        xd.TxTypeMapStride = cm.MiStride;

        // set_entropy_context
        for (int i = 0; i < numPlanes; ++i)
        {
            var pd = xd.Plane[i];
            int rowOffset = miRow, colOffset = miCol;
            int mbsize = xd.Mi0.Bsize;
            if (pd.SubsamplingY != 0 && (miRow & 1) != 0 && MiSizeHigh[mbsize] == 1) rowOffset = miRow - 1;
            if (pd.SubsamplingX != 0 && (miCol & 1) != 0 && MiSizeWide[mbsize] == 1) colOffset = miCol - 1;
            pd.AboveEntropyContext = xd.AboveEntropyContext[i];
            pd.AboveEntropyOffset = colOffset >> pd.SubsamplingX;
            pd.LeftEntropyContext = xd.LeftEntropyContext[i];
            pd.LeftEntropyOffset = (rowOffset & MAX_MIB_MASK) >> pd.SubsamplingY;
        }
        xd.AboveTxfmContext = cm.AboveTxfm;
        xd.AboveTxfmContextOffset = miCol;
        xd.LeftTxfmContextOffset = miRow & MAX_MIB_MASK;

        // av1_setup_dst_planes
        var cur = cm.CurFrame;
        for (int i = 0; i < numPlanes; i++)
        {
            int isUv = i > 0 ? 1 : 0;
            var pd = xd.Plane[i];
            SetupPredPlane(ref pd.Dst, bsize, cur.Buffers[i], cur.Offsets[i], cur.CropWidths[isUv], cur.CropHeights[isUv], cur.Strides[i],
                miRow, miCol, pd.SubsamplingX, pd.SubsamplingY, cur.Buffers16[i]);
        }

        // set_plane_n4
        for (int i = 0; i < numPlanes; i++)
        {
            var pd = xd.Plane[i];
            pd.Width = Math.Max((miWidth * 4) >> pd.SubsamplingX, 4);
            pd.Height = Math.Max((miHeight * 4) >> pd.SubsamplingY, 4);
        }

        SetMiRowCol(xd, cm, miRow, miHeight, miCol, miWidth);
        SetupSrcPlanes(cpi, x, miRow, miCol, numPlanes, bsize);
    }

    /// <summary>set_mi_row_col.</summary>
    internal static void SetMiRowCol(AomMacroblockD xd, AomCommon cm, int miRow, int bh, int miCol, int bw)
    {
        xd.MbToTopEdge = -((miRow * 4) * 8);
        xd.MbToBottomEdge = ((cm.MiRows - bh - miRow) * 4) * 8;
        xd.MbToLeftEdge = -((miCol * 4) * 8);
        xd.MbToRightEdge = ((cm.MiCols - bw - miCol) * 4) * 8;
        xd.MiRow = miRow;
        xd.MiCol = miCol;
        xd.TileMiRowStart = cm.TileMiRowStart; xd.TileMiRowEnd = cm.TileMiRowEnd;
        xd.TileMiColStart = cm.TileMiColStart; xd.TileMiColEnd = cm.TileMiColEnd;

        xd.UpAvailable = miRow > cm.TileMiRowStart;
        int ssX = xd.Plane[1].SubsamplingX, ssY = xd.Plane[1].SubsamplingY;
        xd.LeftAvailable = miCol > cm.TileMiColStart;
        xd.ChromaUpAvailable = xd.UpAvailable;
        xd.ChromaLeftAvailable = xd.LeftAvailable;
        if (ssX != 0 && bw < MiSizeWide[BLOCK_8X8]) xd.ChromaLeftAvailable = (miCol - 1) > cm.TileMiColStart;
        if (ssY != 0 && bh < MiSizeHigh[BLOCK_8X8]) xd.ChromaUpAvailable = (miRow - 1) > cm.TileMiRowStart;
        xd.AboveMbmi = xd.UpAvailable ? xd.MiGrid[xd.MiOffset - xd.MiStride] : null;
        xd.LeftMbmi = xd.LeftAvailable ? xd.MiGrid[xd.MiOffset - 1] : null;

        bool chromaRef = ((miRow & 1) != 0 || (bh & 1) == 0 || ssY == 0) && ((miCol & 1) != 0 || (bw & 1) == 0 || ssX == 0);
        xd.IsChromaRef = chromaRef;
        if (chromaRef)
        {
            int baseMi = xd.MiOffset - (miRow & ssY) * xd.MiStride - (miCol & ssX);
            xd.ChromaAboveMbmi = xd.ChromaUpAvailable ? xd.MiGrid[baseMi - xd.MiStride + ssX] : null;
            xd.ChromaLeftMbmi = xd.ChromaLeftAvailable ? xd.MiGrid[baseMi + ssY * xd.MiStride - 1] : null;
        }
        xd.Height = bh;
        xd.Width = bw;
        xd.IsLastVerticalRect = bw < bh && ((miCol + bw) & (bh - 1)) == 0;
        xd.IsFirstHorizontalRect = bw > bh && (miRow & (bw - 1)) == 0;
    }

    /// <summary>av1_set_offsets (segmentation off: segment 0).</summary>
    internal static void SetOffsets(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize)
    {
        SetOffsetsWithoutSegmentId(cpi, x, miRow, miCol, bsize);
        x.E.Mi0.SegmentId = 0;
    }

    /// <summary>setup_block_rdmult (no AQ): the frame rdmult, the delta-q superblock's rdmult (av1_get_cb_rdmult without
    /// TPL stats), tune=ssim / iq's SSIM scaling (av1_set_ssim_rdmult) and the all-intra superblock modifier.</summary>
    internal static void SetupBlockRdmult(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize)
    {
        x.Rdmult = cpi.RdRdmult;
        if (cpi.DeltaQPresentFlag && cpi.Sf.rt_sf.use_nonrd_pick_mode == 0) x.Rdmult = cpi.SetRdmultDeltaQ(x);
        if (cpi.SsimRdmult) SetSsimRdmult(cpi, x, bsize, miRow, miCol);
        if (cpi.AllIntra) x.Rdmult = (int)(((long)x.Rdmult * x.IntraSbRdmultModifier) >> 7);
        x.Rdmult = x.Rdmult > 0 ? x.Rdmult : 1;
    }

    /// <summary>av1_set_ssim_rdmult: scales rdmult by the geometric mean of the 16x16 SSIM scaling factors the block covers.</summary>
    private static void SetSsimRdmult(AomComp cpi, AomMacroblock x, int bsize, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        const int numMiW = 4, numMiH = 4;   // BLOCK_16X16
        int numCols = (cm.MiCols + numMiW - 1) / numMiW, numRows = (cm.MiRows + numMiH - 1) / numMiH;
        int numBcols = (MiSizeWide[bsize] + numMiW - 1) / numMiW, numBrows = (MiSizeHigh[bsize] + numMiH - 1) / numMiH;
        double numOfMi = 0.0, geomMeanOfScale = 1.0;
        for (int row = miRow / numMiW; row < numRows && row < miRow / numMiW + numBrows; ++row)
            for (int col = miCol / numMiH; col < numCols && col < miCol / numMiH + numBcols; ++col)
            {
                geomMeanOfScale *= cpi.SsimRdmultScalingFactors![row * numCols + col];
                numOfMi += 1.0;
            }
        geomMeanOfScale = Math.Pow(geomMeanOfScale, 1.0 / numOfMi);
        x.Rdmult = (int)((double)x.Rdmult * geomMeanOfScale + 0.5);
        x.Rdmult = Math.Max(x.Rdmult, 0);
        x.Errorperbit = AomRd.ErrorPerBit(x.Rdmult);
    }

    /// <summary>av1_get_perpixel_variance (luma of the source block).</summary>
    internal static uint PerpixelVariance(AomMacroblock x, int bsize, int plane)
    {
        var pd = x.E.Plane[plane];
        int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        var src = x.Plane[plane].Src;
        if (src.Buf16 != null) return AomHbd.PerpixelVariance(src.Buf16, src.Offset, src.Stride, BlockSizeWide[planeBsize], BlockSizeHigh[planeBsize], x.E.Bd);
        uint var = AomIntraModeSearch.VarianceVsZero(src.Buf, src.Offset, src.Stride, BlockSizeWide[planeBsize], BlockSizeHigh[planeBsize], out _);
        int sh = NumPelsLog2Lookup[planeBsize];
        return (var + ((1u << sh) >> 1)) >> sh;
    }

    /// <summary>pick_sb_modes (intra frames).</summary>
    internal static void PickSbModes(AomComp cpi, AomMacroblock x, int miRow, int miCol, ref AomRdStats rdCost, int partition, int bsize,
        AomPickModeContext ctx, AomRdStats bestRd)
    {
        var sf = cpi.Sf;
        if (sf.part_sf.use_best_rd_for_pruning != 0 && bestRd.Rdcost < 0)
        {
            ctx.RdStats.Rdcost = long.MaxValue;
            ctx.RdStats.SkipTxfm = 0;
            rdCost.Invalidate();
            return;
        }

        SetOffsets(cpi, x, miRow, miCol, bsize);

        if (sf.part_sf.reuse_prev_rd_results_for_part_ab != 0 && ctx.RdModeIsReady != 0)
        {
            rdCost.Rate = ctx.RdStats.Rate;
            rdCost.Dist = ctx.RdStats.Dist;
            rdCost.Rdcost = ctx.RdStats.Rdcost;
            return;
        }

        // only needed for row-MT encoding with the cost update frequency off / tile
        AomRowMt.WaitForTopRightSb(cpi, bsize, miRow, miCol);

        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        mbmi.Bsize = bsize;
        mbmi.Partition = partition;

        xd.TxTypeMap = x.TxTypeMapScratch;
        xd.TxTypeMapOffset = 0;
        xd.TxTypeMapStride = MiSizeWide[bsize];

        for (int i = 0; i < numPlanes; ++i)
        {
            x.Plane[i].Eobs = ctx.Eobs[i];
            x.Plane[i].TxbEntropyCtx = ctx.TxbEntropyCtx[i];
        }
        for (int i = 0; i < 2; ++i) xd.Plane[i].ColorIndexMap = ctx.ColorIndexMap[i] ?? x.EmptyColorMap;

        ctx.Skippable = 0;
        mbmi.SkipTxfm = 0;
        mbmi.SkipMode = 0;

        x.SourceVariance = PerpixelVariance(x, bsize, 0);

        AomRdoptUtils.SetModeEvalParams(cpi, x, DEFAULT_EVAL);

        int origRdmult = x.Rdmult;
        SetupBlockRdmult(cpi, x, miRow, miCol, bsize);
        x.Errorperbit = AomRd.ErrorPerBit(x.Rdmult);
        bestRd.CostUpdate(x.Rdmult);

        if (sf.part_sf.use_best_rd_for_pruning == 0) bestRd.Invalidate();

        RdPickIntraModeSb(cpi, x, ref rdCost, bsize, ctx, bestRd.Rdcost);

        x.Rdmult = origRdmult;
        if (rdCost.Rate == int.MaxValue) rdCost.Rdcost = long.MaxValue;
        ctx.RdStats.Rate = rdCost.Rate;
        ctx.RdStats.Dist = rdCost.Dist;
        ctx.RdStats.Rdcost = rdCost.Rdcost;
    }

    /// <summary>av1_rd_pick_intra_mode_sb.</summary>
    internal static void RdPickIntraModeSb(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize, AomPickModeContext ctx, long bestRd)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int numPlanes = cm.NumPlanes;
        int rateY = 0, rateUv = 0, rateYTokenonly = 0, rateUvTokenonly = 0;
        byte ySkipTxfm = 0, uvSkipTxfm = 0;
        long distY = 0, distUv = 0;

        ctx.RdStats.SkipTxfm = 0;
        mbmi.RefFrame0 = 0;       // INTRA_FRAME
        mbmi.RefFrame1 = -1;      // NONE_FRAME
        mbmi.UseIntrabc = 0;
        mbmi.Mv0 = default;
        mbmi.SkipMode = 0;

        long intraYrd = AomIntraModeSearch.RdPickIntraSbyMode(cpi, x, ref rateY, ref rateYTokenonly, ref distY, ref ySkipTxfm, bsize, bestRd, ctx);

        AomRdoptUtils.SetModeEvalParams(cpi, x, DEFAULT_EVAL);

        if (intraYrd < bestRd)
        {
            if (numPlanes > 1)
            {
                // the tx variables to reproduce the luma predictions for chroma-from-luma
                if (xd.IsChromaRef && AomIntraModeSearch.StoreCflRequiredRdo(cpi, xd))
                    ctx.TxTypeMap.AsSpan(0, ctx.NumFourByFourBlk).CopyTo(xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset));
                int maxUvTxSize = AomEncodeMb.GetTxSize(1, xd);
                AomIntraModeSearch.RdPickIntraSbuvMode(cpi, x, ref rateUv, ref rateUvTokenonly, ref distUv, ref uvSkipTxfm, bsize, maxUvTxSize);
            }
            // Intra block is always coded as non-skip
            rdCost.Rate = rateY + rateUv + x.ModeCosts.SkipTxfmCost[AomTxSearch.SkipTxfmContext(xd) * 2 + 0];
            rdCost.Dist = distY + distUv;
            rdCost.Rdcost = AomRd.RdCost(x.Rdmult, rdCost.Rate, rdCost.Dist);
            rdCost.SkipTxfm = 0;
        }
        else rdCost.Rate = int.MaxValue;

        if (rdCost.Rate != int.MaxValue && rdCost.Rdcost < bestRd) bestRd = rdCost.Rdcost;
        if (RdPickIntrabcModeSb(cpi, x, ctx, ref rdCost, bsize, bestRd) < bestRd) ctx.RdStats.SkipTxfm = mbmi.SkipTxfm;
        if (rdCost.Rate == int.MaxValue) return;

        ctx.Mic.CopyFrom(mbmi);
        ctx.MbmiExtBest.CopyFrom(x.MbmiExt);
        xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(ctx.TxTypeMap);
    }

    /// <summary>av1_update_state (no segmentation / AQ / ref mvs).</summary>
    internal static void UpdateState(AomComp cpi, AomMacroblock x, AomPickModeContext ctx, int miRow, int miCol, int bsize, int dryRun)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mi = ctx.Mic;
        var miAddr = xd.Mi0;
        int bw = MiSizeWide[bsize], bh = MiSizeHigh[bsize];
        int mis = cm.MiStride;

        miAddr.CopyFrom(mi);
        ctx.MbmiExtBest.CopyTo(x.MbmiExt);   // copy_mbmi_ext_frame_to_mbmi_ext
        x.TxfmSkip = ctx.RdStats.SkipTxfm;

        xd.TxTypeMap = ctx.TxTypeMap;
        xd.TxTypeMapOffset = 0;
        xd.TxTypeMapStride = bw;
        if (dryRun == OUTPUT_ENABLED)
        {
            int gridIdx = miRow * mis + miCol;
            for (int blkRow = 0; blkRow < bh; ++blkRow)
                Array.Copy(ctx.TxTypeMap, blkRow * bw, cm.TxTypeMap, gridIdx + blkRow * mis, bw);
            xd.TxTypeMap = cm.TxTypeMap;
            xd.TxTypeMapOffset = gridIdx;
            xd.TxTypeMapStride = mis;
        }

        for (int i = 0; i < numPlanes; ++i)
        {
            x.Plane[i].Eobs = ctx.Eobs[i];
            x.Plane[i].TxbEntropyCtx = ctx.TxbEntropyCtx[i];
        }
        for (int i = 0; i < 2; ++i) xd.Plane[i].ColorIndexMap = ctx.ColorIndexMap[i] ?? x.EmptyColorMap;

        int cols = Math.Min((xd.MbToRightEdge >> (3 + 2)) + bw, bw);
        int rows = Math.Min((xd.MbToBottomEdge >> (3 + 2)) + bh, bh);
        for (int y = 0; y < rows; y++)
            for (int xi = 0; xi < cols; xi++) xd.MiGrid[xd.MiOffset + xi + y * mis] = miAddr;
    }

    /// <summary>encode_superblock (intra blocks).</summary>
    internal static void EncodeSuperblock(AomComp cpi, AomMacroblock x, int dryRun, int bsize)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int mis = cm.MiStride;
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];
        var txfmParams = x.TxfmSearchParams;

        // set_tx_size_search_method(..., winner mode)
        txfmParams.TxSizeSearchMethod = cpi.WinnerModeParams.tx_size_search_methods[DEFAULT_EVAL];
        if (cpi.Sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch != 0)
            txfmParams.TxSizeSearchMethod = cpi.WinnerModeParams.tx_size_search_methods[WINNER_MODE_EVAL];
        txfmParams.TxModeSearchType = AomRdoptUtils.SelectTxMode(cm.BaseQindex == 0, txfmParams.TxSizeSearchMethod);

        int miRow = xd.MiRow, miCol = xd.MiCol;
        bool isInter = AomEncodeMb.IsInterBlock(mbmi);
        if (!isInter)
        {
            // store_cfl_required
            xd.Cfl.StoreY = StoreCflRequired(cm, xd) ? 1 : 0;
            for (int plane = 0; plane < numPlanes; ++plane)
                AomIntraModeSearch.EncodeIntraBlockPlane(cpi, x, bsize, plane, dryRun, cpi.OptimizeSegArr[mbmi.SegmentId]);
            xd.Cfl.StoreY = 0;
            if (AomIntraModeSearch.AllowPalette(cpi.AllowScreenContentTools, bsize))
                for (int plane = 0; plane < Math.Min(2, numPlanes); ++plane)
                    if ((plane == 0 ? mbmi.Palette.PaletteSize0 : mbmi.Palette.PaletteSize1) > 0 && dryRun == OUTPUT_ENABLED)
                        AomPalette.TokenizeColorMap(cpi, x, plane, bsize, mbmi.TxSize, cpi.AllowUpdateCdf);

            UpdateIntraMbTxbContext(cpi, x, dryRun, bsize, cpi.AllowUpdateCdf);
        }
        else
        {
            // intrabc: the prediction from the current frame, then the residual and tokens (x->reuse_inter_pred is
            // real-time only: all planes are predicted)
            for (int i = 0; i < numPlanes; ++i) xd.Plane[i].Pre0 = xd.Plane[i].Dst;
            AomReconInter.BuildIntrabcPredictor(cm, xd, miRow, miCol, 0, numPlanes - 1);
            EncodeSbInter(cpi, x, bsize, dryRun);
            TokenizeSbVartx(cpi, x, dryRun, bsize, cpi.AllowUpdateCdf);
        }

        if (dryRun == OUTPUT_ENABLED)
        {
            if (cpi.AllowIntrabcNow && mbmi.UseIntrabc != 0) cpi.IntrabcUsed = true;
            if (txfmParams.TxModeSearchType == TX_MODE_SELECT && xd.Lossless[mbmi.SegmentId] == 0 && mbmi.Bsize > BLOCK_4X4 &&
                !(isInter && mbmi.SkipTxfm != 0))
            {
                if (isInter) TxPartitionCountUpdate(cpi, x, bsize, cpi.AllowUpdateCdf);
                else if (mbmi.TxSize != MaxTxsizeRectLookup[bsize]) System.Threading.Interlocked.Increment(ref cpi.TxbSplitCount);
                if (!isInter && AomTxSearch.BlockSignalsTxsize(bsize))
                {
                    int txSizeCtx = AomTxSearch.TxSizeContext(xd);
                    int txSizeCat = AomTxSearch.BsizeToTxSizeCat(bsize);
                    int depth = AomTxSearch.TxSizeToDepth(mbmi.TxSize, bsize);
                    int maxDepths = BsizeToMaxDepth[bsize];
                    if (cpi.AllowUpdateCdf) AomCdf.Update(x.TileCtx.Mode.Txsz[txSizeCat * 3 + txSizeCtx], depth, maxDepths + 1);
                }
            }
            else
            {
                int intraTxSize = isInter
                    ? (xd.Lossless[mbmi.SegmentId] != 0 ? TX_4X4 : AomTxSearch.TxSizeFromTxMode(bsize, txfmParams.TxModeSearchType))
                    : mbmi.TxSize;
                int cols = Math.Min(cm.MiCols - miCol, miWidth);
                int rows = Math.Min(cm.MiRows - miRow, miHeight);
                for (int j = 0; j < rows; j++)
                    for (int i = 0; i < cols; i++) xd.MiGrid[xd.MiOffset + mis * j + i]!.TxSize = intraTxSize;
                if (intraTxSize != MaxTxsizeRectLookup[bsize]) System.Threading.Interlocked.Increment(ref cpi.TxbSplitCount);
            }
        }

        if (txfmParams.TxModeSearchType == TX_MODE_SELECT && AomTxSearch.BlockSignalsTxsize(mbmi.Bsize) && isInter && mbmi.SkipTxfm == 0 &&
            xd.Lossless[mbmi.SegmentId] == 0)
        {
            if (dryRun != OUTPUT_ENABLED) TxPartitionSetContexts(cm, xd, bsize);
        }
        else
        {
            int txSize;
            if (isInter) txSize = xd.Lossless[mbmi.SegmentId] != 0 ? TX_4X4 : AomTxSearch.TxSizeFromTxMode(bsize, txfmParams.TxModeSearchType);
            else txSize = bsize > BLOCK_4X4 ? mbmi.TxSize : TX_4X4;
            mbmi.TxSize = txSize;
            // set_txfm_ctxs (skip-sized for skipped inter blocks)
            byte bwTx = (byte)TxSizeWide[txSize], bhTx = (byte)TxSizeHigh[txSize];
            if (mbmi.SkipTxfm != 0 && isInter)
            {
                bwTx = (byte)(xd.Width * 4);
                bhTx = (byte)(xd.Height * 4);
            }
            xd.AboveTxfmContext.AsSpan(xd.AboveTxfmContextOffset, xd.Width).Fill(bwTx);
            xd.LeftTxfmContextBuffer.AsSpan(xd.LeftTxfmContextOffset, xd.Height).Fill(bhTx);
        }

        if (isInter && !xd.IsChromaRef && AomCfl.IsCflAllowed(xd) != 0) AomCfl.CflStoreBlock(xd, mbmi.Bsize, mbmi.TxSize);
    }

    private static readonly byte[] BsizeToMaxDepth = { 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };

    /// <summary>store_cfl_required (intra blocks of a colour frame: whether the chroma block may use CfL).</summary>
    private static bool StoreCflRequired(AomCommon cm, AomMacroblockD xd)
    {
        var mbmi = xd.Mi0;
        if (cm.Monochrome) return false;
        if (!xd.IsChromaRef) return true;   // store for the chroma reference block that follows (sub-8x8)
        return mbmi.UvMode == UV_CFL_PRED;
    }

    /// <summary>encode_b (and encode_b_nonrd with nonrd: the skip flag of intra blocks is cleared).</summary>
    internal static void EncodeB(AomComp cpi, AomMacroblock x, int miRow, int miCol, int dryRun, int bsize, int partition, AomPickModeContext ctx,
        bool nonrd = false)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        SetOffsetsWithoutSegmentId(cpi, x, miRow, miCol, bsize);
        int originMult = x.Rdmult;
        SetupBlockRdmult(cpi, x, miRow, miCol, bsize);
        var mbmi = xd.Mi0;
        mbmi.Partition = partition;
        UpdateState(cpi, x, ctx, miRow, miCol, bsize, dryRun);

        AomMbmiExtFrame? extFrame = null;
        if (cpi.MbmiExtFrameBase != null)
            extFrame = cpi.MbmiExtFrameBase[miRow * cm.MiStride + miCol] ??= new AomMbmiExtFrame();   // x->mbmi_ext_frame
        if (dryRun == OUTPUT_ENABLED)
        {
            cpi.ExtCbOffset[(miRow * cm.MiStride + miCol) * 2 + 0] = x.CbOffset[0];
            cpi.ExtCbOffset[(miRow * cm.MiStride + miCol) * 2 + 1] = x.CbOffset[1];
            if (extFrame != null) { extFrame.CbOffset[0] = (ushort)x.CbOffset[0]; extFrame.CbOffset[1] = (ushort)x.CbOffset[1]; }
        }

        // encode_b_nonrd: intra blocks are coded as non-skip
        if (nonrd && !AomEncodeMb.IsInterBlock(xd.Mi0)) xd.Mi0.SkipTxfm = 0;
        EncodeSuperblock(cpi, x, dryRun, bsize);

        if (dryRun == OUTPUT_ENABLED)
        {
            // update_cb_offsets
            x.CbOffset[0] += BlockSizeWide[bsize] * BlockSizeHigh[bsize];
            if (xd.IsChromaRef)
            {
                int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, cm.SsX, cm.SsY);
                x.CbOffset[1] += BlockSizeWide[planeBsize] * BlockSizeHigh[planeBsize];
            }
            // delta quant: the superblock's first coded block moves the running base qindex
            bool superBlockUpperLeft = (miRow & (cm.MibSize - 1)) == 0 && (miCol & (cm.MibSize - 1)) == 0;
            if (!nonrd && cpi.DeltaQPresentFlag && (bsize != cm.SbSize || mbmi.SkipTxfm == 0) && superBlockUpperLeft)   // (encode_b only; encode_b_nonrd does not)
                xd.CurrentBaseQindex = mbmi.CurrentQindex;
            if (cpi.AllowUpdateCdf) UpdateStats(cpi, x);
        }
        extFrame?.CopyFrom(x.MbmiExt);   // av1_copy_mbmi_ext_to_mbmi_ext_frame
        x.Rdmult = originMult;
    }

    /// <summary>encode_sb.</summary>
    internal static void EncodeSb(AomComp cpi, AomMacroblock x, int miRow, int miCol, int dryRun, int bsize, AomPcTree pcTree)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int hbs = MiSizeWide[bsize] / 2;
        bool isPartitionRoot = bsize >= BLOCK_8X8;
        int ctx = isPartitionRoot ? PartitionPlaneContext(xd, miRow, miCol, bsize) : -1;
        int partition = pcTree.Partitioning;
        int subsize = PartitionSubsize(bsize, partition);
        int quarterStep = MiSizeWide[bsize] / 4;
        int bsize2 = PartitionSubsize(bsize, PARTITION_SPLIT);

        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return;
        if (subsize == 255) return;

        if (dryRun == OUTPUT_ENABLED && ctx >= 0)
        {
            bool hasRows = (miRow + hbs) < cm.MiRows, hasCols = (miCol + hbs) < cm.MiCols;
            if (hasRows && hasCols && cpi.AllowUpdateCdf)
                AomCdf.Update(x.TileCtx.Mode.Partition[(4 - (ctx >> 2)) * 4 + (ctx & 3)], partition, PartitionCdfLength(bsize));
        }

        switch (partition)
        {
            case PARTITION_NONE:
                EncodeB(cpi, x, miRow, miCol, dryRun, subsize, partition, pcTree.None!);
                break;
            case PARTITION_VERT:
                EncodeB(cpi, x, miRow, miCol, dryRun, subsize, partition, pcTree.Vertical[0]!);
                if (miCol + hbs < cm.MiCols) EncodeB(cpi, x, miRow, miCol + hbs, dryRun, subsize, partition, pcTree.Vertical[1]!);
                break;
            case PARTITION_HORZ:
                EncodeB(cpi, x, miRow, miCol, dryRun, subsize, partition, pcTree.Horizontal[0]!);
                if (miRow + hbs < cm.MiRows) EncodeB(cpi, x, miRow + hbs, miCol, dryRun, subsize, partition, pcTree.Horizontal[1]!);
                break;
            case PARTITION_SPLIT:
                EncodeSb(cpi, x, miRow, miCol, dryRun, subsize, pcTree.Split[0]!);
                EncodeSb(cpi, x, miRow, miCol + hbs, dryRun, subsize, pcTree.Split[1]!);
                EncodeSb(cpi, x, miRow + hbs, miCol, dryRun, subsize, pcTree.Split[2]!);
                EncodeSb(cpi, x, miRow + hbs, miCol + hbs, dryRun, subsize, pcTree.Split[3]!);
                break;
            case PARTITION_HORZ_A:
                EncodeB(cpi, x, miRow, miCol, dryRun, bsize2, partition, pcTree.HorizontalA[0]!);
                EncodeB(cpi, x, miRow, miCol + hbs, dryRun, bsize2, partition, pcTree.HorizontalA[1]!);
                EncodeB(cpi, x, miRow + hbs, miCol, dryRun, subsize, partition, pcTree.HorizontalA[2]!);
                break;
            case PARTITION_HORZ_B:
                EncodeB(cpi, x, miRow, miCol, dryRun, subsize, partition, pcTree.HorizontalB[0]!);
                EncodeB(cpi, x, miRow + hbs, miCol, dryRun, bsize2, partition, pcTree.HorizontalB[1]!);
                EncodeB(cpi, x, miRow + hbs, miCol + hbs, dryRun, bsize2, partition, pcTree.HorizontalB[2]!);
                break;
            case PARTITION_VERT_A:
                EncodeB(cpi, x, miRow, miCol, dryRun, bsize2, partition, pcTree.VerticalA[0]!);
                EncodeB(cpi, x, miRow + hbs, miCol, dryRun, bsize2, partition, pcTree.VerticalA[1]!);
                EncodeB(cpi, x, miRow, miCol + hbs, dryRun, subsize, partition, pcTree.VerticalA[2]!);
                break;
            case PARTITION_VERT_B:
                EncodeB(cpi, x, miRow, miCol, dryRun, subsize, partition, pcTree.VerticalB[0]!);
                EncodeB(cpi, x, miRow, miCol + hbs, dryRun, bsize2, partition, pcTree.VerticalB[1]!);
                EncodeB(cpi, x, miRow + hbs, miCol + hbs, dryRun, bsize2, partition, pcTree.VerticalB[2]!);
                break;
            case PARTITION_HORZ_4:
                for (int i = 0; i < 4; ++i)
                {
                    int thisMiRow = miRow + i * quarterStep;
                    if (i > 0 && thisMiRow >= cm.MiRows) break;
                    EncodeB(cpi, x, thisMiRow, miCol, dryRun, subsize, partition, pcTree.Horizontal4[i]!);
                }
                break;
            case PARTITION_VERT_4:
                for (int i = 0; i < 4; ++i)
                {
                    int thisMiCol = miCol + i * quarterStep;
                    if (i > 0 && thisMiCol >= cm.MiCols) break;
                    EncodeB(cpi, x, miRow, thisMiCol, dryRun, subsize, partition, pcTree.Vertical4[i]!);
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(partition));
        }
        UpdateExtPartitionContext(xd, miRow, miCol, subsize, bsize, partition);
    }

    /// <summary>update_stats (intra frames): skip flag, av1_sum_intra_stats, intrabc flag.</summary>
    private static void UpdateStats(AomComp cpi, AomMacroblock x)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var fc = x.TileCtx;
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        AomCdf.Update(fc.Mode.Skip[skipCtx], mbmi.SkipTxfm, 2);
        if (!AomEncodeMb.IsInterBlock(mbmi)) SumIntraStats(cpi, x, mbmi);
        if (cpi.AllowIntrabcNow)
        {
            AomCdf.Update(fc.Mode.Intrabc, mbmi.UseIntrabc, 2);
            if (mbmi.UseIntrabc != 0)
            {
                var dvRef = x.MbmiExt.RefMvStack[0].ThisMv;
                AomMvCost.UpdateMvStats(mbmi.Mv0, dvRef, fc.Mv, AomMvCost.MV_SUBPEL_NONE);   // fc->ndvc
            }
        }
    }

    /// <summary>av1_sum_intra_stats (key frames).</summary>
    private static void SumIntraStats(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi)
    {
        var xd = x.E;
        var fc = x.TileCtx;
        var m = fc.Mode;
        int yMode = mbmi.Mode;
        int bsize = mbmi.Bsize;
        int above = xd.AboveMbmi?.Mode ?? DC_PRED, left = xd.LeftMbmi?.Mode ?? DC_PRED;
        AomCdf.Update(fc.Kfym[IntraModeContext[above] * 5 + IntraModeContext[left]], yMode, 13);

        if (AomIntraModeSearch.FilterIntraAllowed(cpi, mbmi))
        {
            int useFilterIntraMode = mbmi.UseFilterIntra;
            AomCdf.Update(m.UseFilterIntra[AomModeCostFill.LibaomToDav1dBs[mbmi.Bsize]], useFilterIntraMode, 2);
            if (useFilterIntraMode != 0) AomCdf.Update(m.FilterIntra, mbmi.FilterIntraMode, 5);
        }
        if (yMode >= V_PRED && yMode <= D67_PRED && bsize >= BLOCK_8X8)
            AomCdf.Update(m.AngleDelta[yMode - V_PRED], mbmi.AngleDelta[0] + 3, 7);

        if (!xd.IsChromaRef) return;

        int uvMode = mbmi.UvMode;
        int cflAllowed = AomCfl.IsCflAllowed(xd);
        AomCdf.Update(m.UvMode[cflAllowed * 13 + yMode], uvMode, 14 - (cflAllowed != 0 ? 0 : 1));
        if (uvMode == UV_CFL_PRED)
        {
            int jointSign = mbmi.CflAlphaSigns;
            int idx = mbmi.CflAlphaIdx;
            AomCdf.Update(m.CflSign, jointSign, 8);
            int signU = (jointSign + 1) / 3, signV = (jointSign + 1) % 3;
            if (signU != 0) AomCdf.Update(m.CflAlpha[(signU - 1) * 3 + signV], idx >> 4, 16);
            if (signV != 0) AomCdf.Update(m.CflAlpha[(signV - 1) * 3 + signU], idx & 15, 16);
        }
        int intraMode = uvMode == UV_CFL_PRED ? DC_PRED : uvMode;
        if (intraMode >= V_PRED && intraMode <= D67_PRED && bsize >= BLOCK_8X8)
            AomCdf.Update(m.AngleDelta[intraMode - V_PRED], mbmi.AngleDelta[1] + 3, 7);
        if (AomIntraModeSearch.AllowPalette(cpi.AllowScreenContentTools, bsize)) UpdatePaletteCdf(x, mbmi);
    }

    /// <summary>update_palette_cdf.</summary>
    private static void UpdatePaletteCdf(AomMacroblock x, AomMbModeInfo mbmi)
    {
        var xd = x.E;
        var m = x.TileCtx.Mode;
        int bsizeCtx = NumPelsLog2Lookup[mbmi.Bsize] - NumPelsLog2Lookup[BLOCK_8X8];
        if (mbmi.Mode == DC_PRED)
        {
            int n = mbmi.Palette.PaletteSize0;
            int modeCtx = AomIntraModeSearch.PaletteModeCtx(xd);
            AomCdf.Update(m.PalY[bsizeCtx * 3 + modeCtx], n > 0 ? 1 : 0, 2);
            if (n > 0) AomCdf.Update(m.PalSz[bsizeCtx], n - 2, 7);
        }
        if (mbmi.UvMode == UV_DC_PRED)
        {
            int n = mbmi.Palette.PaletteSize1;
            int uvModeCtx = mbmi.Palette.PaletteSize0 > 0 ? 1 : 0;
            AomCdf.Update(m.PalUv[uvModeCtx], n > 0 ? 1 : 0, 2);
            if (n > 0) AomCdf.Update(m.PalSz[7 + bsizeCtx], n - 2, 7);
        }
    }

    // ---- encodetxb.c ----

    /// <summary>av1_set_entropy_contexts.</summary>
    internal static void SetEntropyContexts(AomMacroblockD xd, AomMbdPlane pd, int plane, int planeBsize, int txSize, int hasEob, int aoff, int loff)
    {
        var a = pd.AboveEntropyContext.AsSpan(pd.AboveEntropyOffset + aoff);
        var l = pd.LeftEntropyContext.AsSpan(pd.LeftEntropyOffset + loff);
        int txsWide = TxSizeWideUnit[txSize], txsHigh = TxSizeHighUnit[txSize];
        if (hasEob != 0 && xd.MbToRightEdge < 0)
        {
            int blocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane);
            int aboveContexts = Math.Min(txsWide, blocksWide - aoff);
            a.Slice(0, aboveContexts).Fill((byte)hasEob);
            a.Slice(aboveContexts, txsWide - aboveContexts).Clear();
        }
        else a.Slice(0, txsWide).Fill((byte)hasEob);
        if (hasEob != 0 && xd.MbToBottomEdge < 0)
        {
            int blocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane);
            int leftContexts = Math.Min(txsHigh, blocksHigh - loff);
            l.Slice(0, leftContexts).Fill((byte)hasEob);
            l.Slice(leftContexts, txsHigh - leftContexts).Clear();
        }
        else l.Slice(0, txsHigh).Fill((byte)hasEob);
    }

    /// <summary>av1_reset_entropy_context.</summary>
    internal static void ResetEntropyContext(AomMacroblockD xd, int bsize, int numPlanes)
    {
        int nplanes = 1 + (numPlanes - 1) * (xd.IsChromaRef ? 1 : 0);
        for (int i = 0; i < nplanes; i++)
        {
            var pd = xd.Plane[i];
            int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            pd.AboveEntropyContext.AsSpan(pd.AboveEntropyOffset, MiSizeWide[planeBsize]).Clear();
            pd.LeftEntropyContext.AsSpan(pd.LeftEntropyOffset, MiSizeHigh[planeBsize]).Clear();
        }
    }

    /// <summary>av1_update_intra_mb_txb_context.</summary>
    internal static void UpdateIntraMbTxbContext(AomComp cpi, AomMacroblock x, int dryRun, int bsize, bool allowUpdateCdf)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        if (mbmi.SkipTxfm != 0)
        {
            ResetEntropyContext(xd, bsize, numPlanes);
            return;
        }
        for (int plane = 0; plane < numPlanes; ++plane)
        {
            if (plane != 0 && !xd.IsChromaRef) break;
            var pd = xd.Plane[plane];
            int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            int txSize = AomEncodeMb.GetTxSize(plane, xd);
            int txBsize = TxsizeToBsize[txSize];
            if (planeBsize == txBsize) { UpdateAndRecordTxbContext(cpi, x, plane, 0, 0, 0, planeBsize, txSize, dryRun, allowUpdateCdf); continue; }
            int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize], step = txwUnit * txhUnit;
            int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane);
            int maxUnitBsize = AomEncodeMb.PlaneBlockSize(BLOCK_64X64, pd.SubsamplingX, pd.SubsamplingY);
            int muBlocksWide = Math.Min(MiSizeWide[maxUnitBsize], maxBlocksWide);
            int muBlocksHigh = Math.Min(MiSizeHigh[maxUnitBsize], maxBlocksHigh);
            int i = 0;
            for (int r = 0; r < maxBlocksHigh; r += muBlocksHigh)
            {
                int unitHeight = Math.Min(muBlocksHigh + r, maxBlocksHigh);
                for (int c = 0; c < maxBlocksWide; c += muBlocksWide)
                {
                    int unitWidth = Math.Min(muBlocksWide + c, maxBlocksWide);
                    for (int blkRow = r; blkRow < unitHeight; blkRow += txhUnit)
                        for (int blkCol = c; blkCol < unitWidth; blkCol += txwUnit)
                        {
                            UpdateAndRecordTxbContext(cpi, x, plane, i, blkRow, blkCol, planeBsize, txSize, dryRun, allowUpdateCdf);
                            i += step;
                        }
                }
            }
        }
    }

    [ThreadStatic] private static byte[]? t_levels;
    [ThreadStatic] private static sbyte[]? t_coeffContexts;

    /// <summary>av1_update_and_record_txb_context (and av1_record_txb_context without CDF updates).</summary>
    private static void UpdateAndRecordTxbContext(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize,
        int txSize, int dryRun, bool allowUpdateCdf)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var p = x.Plane[plane];
        var pd = xd.Plane[plane];
        int eob = p.Eobs[block];
        int blockOffset = AomEncodeMb.BlockOffset(block);
        int planeType = plane == 0 ? 0 : 1;
        int txType = AomEncodeMb.GetTxType(xd, planeType, blkRow, blkCol, txSize, cpi.ReducedTxSetUsed != 0);
        var scan = AomEncodeMb.ScanOf(txSize, txType);
        int[] tcoeffArr;
        int tcoeffOff;
        if (dryRun == OUTPUT_ENABLED)
        {
            var mbmi = xd.Mi0;
            var txbCtx = AomTxb.TxbCtx(planeBsize, txSize, plane, pd.AboveEntropyContext.AsSpan(pd.AboveEntropyOffset + blkCol),
                pd.LeftEntropyContext.AsSpan(pd.LeftEntropyOffset + blkRow));
            int bhl = AomTxb.TxbBhl(txSize);
            int width = AomTxb.TxbWide(txSize), height = AomTxb.TxbHigh(txSize);
            int txsizeCtx = AomTxb.TxsizeEntropyCtx(txSize);
            var ec = x.TileCtx.Coef;
            if (allowUpdateCdf) AomCdf.Update(ec.CoefSkip[txsizeCtx * 13 + txbCtx.TxbSkipCtx], eob == 0 ? 1 : 0, 2);

            var cb = x.CbCoefBuff;
            int cbOffset = cpi.ExtCbOffset[(xd.MiRow * cm.MiStride + xd.MiCol) * 2 + planeType];
            int txbOffset = cbOffset / 16;
            cb.EntropyCtx[plane][txbOffset + block] = (byte)txbCtx.TxbSkipCtx;
            cb.Eobs[plane][txbOffset + block] = (ushort)eob;

            if (eob == 0)
            {
                SetEntropyContexts(xd, pd, plane, planeBsize, txSize, 0, blkCol, blkRow);
                return;
            }
            int segEob = AomEncodeMb.MaxEob(txSize);   // av1_get_tx_eob (no SEG_LVL_SKIP)
            tcoeffArr = cb.Tcoeff[plane];
            tcoeffOff = cbOffset + blockOffset;
            Array.Copy(p.Qcoeff, blockOffset, tcoeffArr, tcoeffOff, segEob);

            var levels = t_levels ??= new byte[AomTxb.TxPad2d];
            AomTxb.InitLevels(tcoeffArr.AsSpan(tcoeffOff, segEob), width, height, levels);
            UpdateTxTypeCount(cpi, x, blkRow, blkCol, plane, txSize, allowUpdateCdf);

            int txClass = AomTxb.TxTypeToClass[txType];
            UpdateEobContext(eob, txSize, txClass, planeType, ec, allowUpdateCdf);

            var coeffContexts = t_coeffContexts ??= new sbyte[64 * 64];
            // av1_get_nz_map_contexts
            for (int i = 0; i < eob; ++i)
            {
                int pos = scan[i];
                coeffContexts[pos] = (sbyte)(i == eob - 1 ? AomTxb.LowerLevelsCtxEob(bhl, width, i) : AomTxb.LowerLevelsCtx(levels, pos, bhl, txSize, txClass));
            }

            for (int c = eob - 1; c >= 0; --c)
            {
                int pos = scan[c];
                int coeffCtx = coeffContexts[pos];
                int v = p.Qcoeff[blockOffset + pos];
                int level = Math.Abs(v);
                if (allowUpdateCdf)
                {
                    if (c == eob - 1) AomCdf.Update(ec.EobBaseTok[(txsizeCtx * 2 + planeType) * 4 + coeffCtx], Math.Min(level, 3) - 1, 3);
                    else AomCdf.Update(ec.BaseTok[(txsizeCtx * 2 + planeType) * 41 + coeffCtx], Math.Min(level, 3), 4);
                }
                if (level > 2)   // NUM_BASE_LEVELS
                {
                    int baseRange = level - 1 - 2;
                    int brCtx = AomTxb.BrCtx(levels, pos, bhl, txClass);
                    for (int idx = 0; idx < 12; idx += 3)   // COEFF_BASE_RANGE, BR_CDF_SIZE - 1
                    {
                        int k = Math.Min(baseRange - idx, 3);
                        if (allowUpdateCdf) AomCdf.Update(ec.BrTok[(Math.Min(txsizeCtx, TX_32X32) * 2 + planeType) * 21 + brCtx], k, 4);
                        if (k < 3) break;
                    }
                }
            }
            // the context needed to code the DC sign
            if (tcoeffArr[tcoeffOff] != 0)
            {
                int dcSign = tcoeffArr[tcoeffOff] < 0 ? 1 : 0;
                int dcSignCtx = txbCtx.DcSignCtx;
                if (allowUpdateCdf) AomCdf.Update(ec.DcSign[planeType * 3 + dcSignCtx], dcSign, 2);
                cb.EntropyCtx[plane][txbOffset + block] |= (byte)(dcSignCtx << 4);   // DC_SIGN_CTX_SHIFT
            }
        }
        else
        {
            tcoeffArr = p.Qcoeff;
            tcoeffOff = blockOffset;
        }
        byte culLevel = AomTxb.TxbEntropyContext(tcoeffArr.AsSpan(tcoeffOff, AomEncodeMb.MaxEob(txSize)), scan, eob);
        SetEntropyContexts(xd, pd, plane, planeBsize, txSize, culLevel, blkCol, blkRow);
    }

    /// <summary>update_eob_context.</summary>
    private static void UpdateEobContext(int eob, int txSize, int txClass, int plane, Av1CdfCoefContext ec, bool allowUpdateCdf)
    {
        int eobPt = AomTxb.EobPosToken(eob, out int eobExtra);
        int txsCtx = AomTxb.TxsizeEntropyCtx(txSize);
        int eobMultiSize = TxsizeLog2Minus4[txSize];
        int eobMultiCtx = txClass == TX_CLASS_2D ? 0 : 1;
        if (allowUpdateCdf)
        {
            ushort[] cdf = eobMultiSize switch
            {
                0 => ec.EobBin16[plane * 2 + eobMultiCtx], 1 => ec.EobBin32[plane * 2 + eobMultiCtx], 2 => ec.EobBin64[plane * 2 + eobMultiCtx],
                3 => ec.EobBin128[plane * 2 + eobMultiCtx], 4 => ec.EobBin256[plane * 2 + eobMultiCtx], 5 => ec.EobBin512[plane], _ => ec.EobBin1024[plane],
            };
            AomCdf.Update(cdf, eobPt - 1, eobMultiSize + 5);
        }
        if (EobOffsetBits[eobPt] > 0)
        {
            int eobCtx = eobPt - 3;
            int eobShift = EobOffsetBits[eobPt] - 1;
            int bit = (eobExtra & (1 << eobShift)) != 0 ? 1 : 0;
            if (allowUpdateCdf) AomCdf.Update(ec.EobHiBit[(txsCtx * 2 + plane) * 9 + eobCtx], bit, 2);
        }
    }

    /// <summary>update_tx_type_count (luma, intra).</summary>
    private static void UpdateTxTypeCount(AomComp cpi, AomMacroblock x, int blkRow, int blkCol, int plane, int txSize, bool allowUpdateCdf)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        if (plane > 0) return;
        int txType = AomEncodeMb.GetTxType(xd, 0, blkRow, blkCol, txSize, cpi.ReducedTxSetUsed != 0);
        bool isInter = AomEncodeMb.IsInterBlock(mbmi);
        int setType = AomEncodeMb.ExtTxSetType(txSize, isInter, cpi.ReducedTxSetUsed != 0);
        if (NumExtTxSet[setType] > 1 && cpi.Cm.BaseQindex > 0 && mbmi.SkipTxfm == 0)
        {
            int eset = ExtTxSetIndex[(isInter ? 6 : 0) + setType];
            if (eset > 0 && isInter)
            {
                int sqr = TxsizeSqrMap[txSize];
                var m = x.TileCtx.Mode;
                ushort[] cdf = eset == 1 ? m.TxtpInter1[sqr] : eset == 2 ? m.TxtpInter2 : m.TxtpInter3[sqr];
                if (allowUpdateCdf) AomCdf.Update(cdf, ExtTxInd[setType * 16 + txType], NumExtTxSet[setType]);
            }
            else if (eset > 0)
            {
                int intraDir = mbmi.UseFilterIntra != 0 ? FimodeToIntradir[mbmi.FilterIntraMode] : mbmi.Mode;
                int sqr = TxsizeSqrMap[txSize];
                var m = x.TileCtx.Mode;
                ushort[] cdf = eset == 1 ? m.TxtpIntra1[sqr * 13 + intraDir] : m.TxtpIntra2[sqr * 13 + intraDir];
                if (allowUpdateCdf) AomCdf.Update(cdf, ExtTxInd[setType * 16 + txType], NumExtTxSet[setType]);
            }
        }
    }
}
