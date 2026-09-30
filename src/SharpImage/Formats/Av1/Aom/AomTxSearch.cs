using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>struct rdcost_block_args (tx_search.c).</summary>
internal sealed class AomRdcostBlockArgs
{
    public AomComp Cpi = null!;
    public AomMacroblock X = null!;
    public readonly byte[] TAbove = new byte[AomMacroblockD.MaxMibSize];
    public readonly byte[] TLeft = new byte[AomMacroblockD.MaxMibSize];
    public AomRdStats RdStats;
    public long CurrentRd;
    public long BestRd;
    public bool ExitEarly;
    public bool IncompleteExit;
    public int FtxsMode;
    public int SkipTrellis;

    public void Reset()
    {
        Array.Clear(TAbove); Array.Clear(TLeft);
        RdStats = default; CurrentRd = 0; BestRd = 0; ExitEarly = false; IncompleteExit = false; FtxsMode = 0; SkipTrellis = 0;
    }
}

// Port of libaom 3.14.1 av1/encoder/tx_search.c: the transform size / type search of intra blocks (uniform tx sizes,
// luma and chroma): search_tx_type and its pruning (get_tx_mask, prune_txk_type(_separ), predict_dc_only_block,
// skip_trellis_opt_based_on_satd), the distortions, recon_intra, block_rd_txfm, av1_txfm_rd_in_plane,
// uniform_txfm_yrd, choose_tx_size_type_from_rd / choose_largest_tx_size / choose_smallest_tx_size,
// av1_pick_uniform_tx_size_type_yrd and av1_txfm_uvrd. (The inter-only paths - var-tx, the residual hash, skip
// prediction, prune_tx_2D - are not used by the all-intra encoder.)
/// <summary>Optional per-call RD trace (same lines as the scratchpad aomoracle's libaom wrappers).</summary>
internal static class AomTrace
{
    [ThreadStatic] public static System.IO.TextWriter? Out;
}

internal static class AomTxSearch
{
    public const int FTXS_NONE = 0, FTXS_DCT_AND_1D_DCT_ONLY = 1 << 0, FTXS_DISABLE_TRELLIS_OPT = 1 << 1, FTXS_USE_TRANSFORM_DOMAIN = 1 << 2;
    public const int FULL_TXFM_RD = 0, LOW_TXFM_RD = 1;
    private const int TxPruneNone = 0, TxPruneLargest = 1, TxPruneSplit = 2;
    private const int MAX_TX_DEPTH = 2, MAX_VARTX_DEPTH = 2, MAX_TX_SCALE = 1;
    private const int DCT_ADST_TX_MASK = 0x000F;
    private const int DRY_RUN_NORMAL = 1, OUTPUT_ENABLED = 0;
    private const bool UseBQuantNoTrellis = true;   // USE_B_QUANT_NO_TRELLIS

    // pruning thresholds for prune_txk_type and prune_txk_type_separ
    private static readonly int[] PruneFactors = { 200, 200, 120, 80, 40 };   // scale 1000
    private static readonly int[] MulFactors = { 80, 80, 70, 50, 30 };        // scale 100

    private static bool IsInterBlock(AomMbModeInfo mbmi) => mbmi.UseIntrabc != 0 || mbmi.RefFrame0 > 0;

    /// <summary>is_trellis_used (encodemb.h).</summary>
    internal static bool IsTrellisUsed(int optimizeB, int dryRun)
    {
        if (optimizeB == NO_TRELLIS_OPT) return false;
        if (optimizeB == FINAL_PASS_TRELLIS_OPT && dryRun != OUTPUT_ENABLED) return false;
        return true;
    }

    /// <summary>RIGHT_SIGNED_SHIFT (aom_ports/mem.h): a negative count shifts left (64-point transforms: 2 * (1 - 2)).</summary>
    private static long RightSignedShift(long value, int n) => n < 0 ? value << -n : value >> n;

    /// <summary>av1_get_entropy_contexts: copies the plane block's above / left entropy contexts.</summary>
    internal static void GetEntropyContexts(int planeBsize, AomMbdPlane pd, Span<byte> tAbove, Span<byte> tLeft)
    {
        pd.AboveEntropyContext.AsSpan(pd.AboveEntropyOffset, MiSizeWide[planeBsize]).CopyTo(tAbove);
        pd.LeftEntropyContext.AsSpan(pd.LeftEntropyOffset, MiSizeHigh[planeBsize]).CopyTo(tLeft);
    }

    /// <summary>av1_get_skip_txfm_context.</summary>
    internal static int SkipTxfmContext(AomMacroblockD xd)
        => (xd.AboveMbmi?.SkipTxfm ?? 0) + (xd.LeftMbmi?.SkipTxfm ?? 0);

    /// <summary>block_signals_txsize.</summary>
    internal static bool BlockSignalsTxsize(int bsize) => bsize > BLOCK_4X4;

    /// <summary>tx_size_from_tx_mode.</summary>
    internal static int TxSizeFromTxMode(int bsize, int txMode)
    {
        int largest = TxModeToBiggestTxSize[txMode];
        int maxRect = MaxTxsizeRectLookup[bsize];
        if (bsize == BLOCK_4X4) return Math.Min(MaxTxsizeLookup[bsize], largest);
        return TxsizeSqrMap[maxRect] <= largest ? maxRect : largest;
    }

    /// <summary>bsize_to_num_blk.</summary>
    internal static int BsizeToNumBlk(int bsize) => 1 << (NumPelsLog2Lookup[bsize] - 4);

    private static readonly byte[] BsizeToTxSizeDepthTable = { 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 4, 4, 2, 2, 3, 3, 4, 4 };
    /// <summary>bsize_to_tx_size_cat.</summary>
    internal static int BsizeToTxSizeCat(int bsize) => BsizeToTxSizeDepthTable[bsize] - 1;

    /// <summary>tx_size_to_depth (block.h).</summary>
    internal static int TxSizeToDepth(int txSize, int bsize)
    {
        int ctxSize = MaxTxsizeRectLookup[bsize];
        int depth = 0;
        while (txSize != ctxSize) { depth++; ctxSize = SubTxSizeMap[ctxSize]; }
        return depth;
    }

    /// <summary>get_sqr_tx_size.</summary>
    private static int SqrTxSize(int txDim) => txDim switch { 128 or 64 => TX_64X64, 32 => TX_32X32, 16 => TX_16X16, 8 => TX_8X8, _ => TX_4X4 };

    /// <summary>txfm_partition_context.</summary>
    internal static int TxfmPartitionContext(byte aboveCtx, byte leftCtx, int bsize, int txSize)
    {
        int txw = TxSizeWide[txSize], txh = TxSizeHigh[txSize];
        int above = aboveCtx < txw ? 1 : 0;
        int left = leftCtx < txh ? 1 : 0;
        int category = 7;   // TXFM_PARTITION_CONTEXTS
        if (txSize <= TX_4X4) return 0;
        int maxTxSize = SqrTxSize(Math.Max(BlockSizeWide[bsize], BlockSizeHigh[bsize]));
        if (maxTxSize >= TX_8X8)
            category = ((TxsizeSqrUpMap[txSize] != maxTxSize && maxTxSize > TX_8X8) ? 1 : 0) + (5 - 1 - maxTxSize) * 2;
        return category * 3 + above + left;
    }

    /// <summary>get_tx_size_context (pred_common.h).</summary>
    internal static int TxSizeContext(AomMacroblockD xd)
    {
        var mbmi = xd.Mi0;
        var aboveMbmi = xd.AboveMbmi;
        var leftMbmi = xd.LeftMbmi;
        int maxTxSize = MaxTxsizeRectLookup[mbmi.Bsize];
        int maxTxWide = TxSizeWide[maxTxSize], maxTxHigh = TxSizeHigh[maxTxSize];
        bool hasAbove = xd.UpAvailable, hasLeft = xd.LeftAvailable;
        int above = xd.AboveTxfmContext[xd.AboveTxfmContextOffset] >= maxTxWide ? 1 : 0;
        int left = xd.LeftTxfmContextBuffer[xd.LeftTxfmContextOffset] >= maxTxHigh ? 1 : 0;
        if (hasAbove && IsInterBlock(aboveMbmi!)) above = BlockSizeWide[aboveMbmi!.Bsize] >= maxTxWide ? 1 : 0;
        if (hasLeft && IsInterBlock(leftMbmi!)) left = BlockSizeHigh[leftMbmi!.Bsize] >= maxTxHigh ? 1 : 0;
        if (hasAbove && hasLeft) return above + left;
        if (hasAbove) return above;
        if (hasLeft) return left;
        return 0;
    }

    /// <summary>tx_size_cost (tx_search.h).</summary>
    internal static int TxSizeCost(AomMacroblock x, int bsize, int txSize)
    {
        if (x.TxfmSearchParams.TxModeSearchType != TX_MODE_SELECT || !BlockSignalsTxsize(bsize)) return 0;
        int txSizeCat = BsizeToTxSizeCat(bsize);
        int depth = TxSizeToDepth(txSize, bsize);
        int txSizeCtx = TxSizeContext(x.E);
        return x.ModeCosts.TxSizeCost[(txSizeCat * AomModeCosts.TxSizeContexts + txSizeCtx) * 5 + depth];
    }

    /// <summary>get_default_tx_type (blockd.h).</summary>
    private static int DefaultTxType(int planeType, AomMacroblockD xd, int txSize, bool useScreenContentTools)
    {
        var mbmi = xd.Mi0;
        if (IsInterBlock(mbmi) || planeType != 0 || xd.Lossless[mbmi.SegmentId] != 0 || txSize >= TX_32X32 || useScreenContentTools)
            return DCT_DCT;
        return AomEncodeMb.IntraModeToTxTypeOf(mbmi, planeType);
    }

    /// <summary>get_search_init_depth.</summary>
    private static int SearchInitDepth(int miWidth, int miHeight, bool isInter, AomSpeedFeatures sf, int txSizeSearchMethod)
    {
        if (txSizeSearchMethod == USE_LARGESTALL) return MAX_VARTX_DEPTH;
        if (sf.tx_sf.tx_size_search_lgr_block != 0)
            if (miWidth > MiSizeWide[BLOCK_64X64] || miHeight > MiSizeHigh[BLOCK_64X64]) return MAX_VARTX_DEPTH;
        if (isInter)
            return miHeight != miWidth ? sf.tx_sf.inter_tx_size_search_init_depth_rect : sf.tx_sf.inter_tx_size_search_init_depth_sqr;
        return miHeight != miWidth ? sf.tx_sf.intra_tx_size_search_init_depth_rect : sf.tx_sf.intra_tx_size_search_init_depth_sqr;
    }

    // ---- distortion ----

    /// <summary>av1_pixel_diff_dist.</summary>
    internal static long PixelDiffDist(AomMacroblock x, int plane, int blkRow, int blkCol, int planeBsize, int txBsize, out uint blockMseQ8)
    {
        var xd = x.E;
        AomEncodeMb.TxbDimensions(xd, plane, planeBsize, blkRow, blkCol, txBsize, out _, out _, out int visibleCols, out int visibleRows);
        int diffStride = BlockSizeWide[planeBsize];
        int off = (blkRow * diffStride + blkCol) << 2;
        ulong sse = AomEncodeMb.SumSquares2dI16(x.Plane[plane].SrcDiff, off, diffStride, visibleCols, visibleRows);
        if (visibleCols > 0 && visibleRows > 0) blockMseQ8 = (uint)((256 * sse) / (ulong)(visibleCols * visibleRows));
        else blockMseQ8 = uint.MaxValue;
        return (long)sse;
    }

    /// <summary>pixel_diff_stats.</summary>
    private static long PixelDiffStats(AomMacroblock x, int plane, int blkRow, int blkCol, int planeBsize, int txBsize,
        out uint blockMseQ8, ref long perPxMean, out ulong blockVar)
    {
        var xd = x.E;
        AomEncodeMb.TxbDimensions(xd, plane, planeBsize, blkRow, blkCol, txBsize, out _, out _, out int visibleCols, out int visibleRows);
        int diffStride = BlockSizeWide[planeBsize];
        int off = (blkRow * diffStride + blkCol) << 2;
        int sum = 0;
        ulong sse = AomEncodeMb.SumSse2dI16(x.Plane[plane].SrcDiff, off, diffStride, visibleCols, visibleRows, ref sum);
        blockVar = ulong.MaxValue;
        if (visibleCols > 0 && visibleRows > 0)
        {
            double normFactor = 1.0 / (visibleCols * visibleRows);
            int signSum = sum > 0 ? 1 : -1;
            perPxMean = (long)(normFactor * Math.Abs(sum)) << 7;
            perPxMean = signSum * perPxMean;
            blockMseQ8 = (uint)(normFactor * (256 * sse));
            blockVar = sse - (ulong)(normFactor * sum * sum);
        }
        else blockMseQ8 = uint.MaxValue;
        return (long)sse;
    }

    [ThreadStatic] private static byte[]? t_recon;

    /// <summary>dist_block_px_domain: the SSE (x16) of the source against dst plus the block's inverse transform.</summary>
    private static long DistBlockPxDomain(AomComp cpi, AomMacroblock x, int plane, int planeBsize, int block, int blkRow, int blkCol, int txSize)
    {
        var xd = x.E;
        var p = x.Plane[plane];
        var pd = xd.Plane[plane];
        int eob = p.Eobs[block];
        int txBsize = TxsizeToBsize[txSize];
        int bsw = BlockSizeWide[txBsize], bsh = BlockSizeHigh[txBsize];
        int srcStride = p.Src.Stride, dstStride = pd.Dst.Stride;
        int srcIdx = p.Src.Offset + ((blkRow * srcStride + blkCol) << 2);
        int dstIdx = pd.Dst.Offset + ((blkRow * dstStride + blkCol) << 2);
        const int MaxTxSize = 64;
        var recon = t_recon ??= new byte[MaxTxSize * MaxTxSize];
        AomEncodeMb.CopyBlock(pd.Dst.Buf, dstIdx, dstStride, recon, 0, MaxTxSize, bsw, bsh);
        int txType = AomEncodeMb.GetTxType(xd, plane == 0 ? 0 : 1, blkRow, blkCol, txSize, cpi.ReducedTxSetUsed != 0);
        AomEncodeMb.InverseTransformBlock(p.Dqcoeff, AomEncodeMb.BlockOffset(block), txType, txSize, recon, 0, MaxTxSize, eob);
        return 16 * (long)PixelDist(x, plane, p.Src.Buf, srcIdx, srcStride, recon, 0, MaxTxSize, blkRow, blkCol, planeBsize, txBsize);
    }

    /// <summary>pixel_dist / pixel_dist_visible_only: the SSE over the visible part of the tx block (the variance
    /// kernel's sse and aom_sse_odd_size are both the exact sum).</summary>
    private static uint PixelDist(AomMacroblock x, int plane, byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride,
        int blkRow, int blkCol, int planeBsize, int txBsize)
    {
        AomEncodeMb.TxbDimensions(x.E, plane, planeBsize, blkRow, blkCol, txBsize, out _, out _, out int visibleCols, out int visibleRows);
        return (uint)AomEncodeMb.Sse(src, srcOff, srcStride, dst, dstOff, dstStride, visibleCols, visibleRows);
    }

    /// <summary>dist_block_tx_domain (no quantization matrices).</summary>
    private static void DistBlockTxDomain(AomMacroblock x, int plane, int block, int txSize, out long outDist, out long outSse)
    {
        var p = x.Plane[plane];
        int bufferLength = AomEncodeMb.MaxEob(txSize);
        int shift = (MAX_TX_SCALE - AomQuantize.TxScale(txSize)) * 2;
        int off = AomEncodeMb.BlockOffset(block);
        long dist = AomEncodeMb.BlockErrorAvx2(p.Coeff.AsSpan(off, bufferLength), p.Dqcoeff.AsSpan(off, bufferLength), bufferLength, out long thisSse);
        outDist = RightSignedShift(dist, shift);
        outSse = RightSignedShift(thisSse, shift);
    }

    /// <summary>sort_rd: R-D costs sorted ascending (insertion, stable), the tx types alongside.</summary>
    private static void SortRd(Span<long> rds, Span<int> txk, int len)
    {
        for (int i = 1; i <= len - 1; ++i)
            for (int j = 0; j < i; ++j)
                if (rds[j] > rds[i])
                {
                    long temprd = rds[i];
                    int tempi = txk[i];
                    for (int k = i; k > j; k--) { rds[k] = rds[k - 1]; txk[k] = txk[k - 1]; }
                    rds[j] = temprd;
                    txk[j] = tempi;
                    break;
                }
    }

    /// <summary>av1_xform_quant with a given tx type and quant setup.</summary>
    private static void XformQuant(AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize, int txType,
        in AomQuantParam qp)
    {
        AomEncodeMb.Xform(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType);
        AomEncodeMb.Quant(x, plane, block, txSize, txType, qp);
    }

    /// <summary>av1_cost_coeffs_txb_laplacian (adjust_eob 0).</summary>
    private static int CostCoeffsLaplacian(AomComp cpi, AomMacroblock x, int plane, int block, int txSize, int txType, AomTxbCtx txbCtx)
    {
        var p = x.Plane[plane];
        int off = AomEncodeMb.BlockOffset(block), n = AomEncodeMb.MaxEob(txSize);
        int eob = p.Eobs[block];
        return AomTxb.CostCoeffsTxbLaplacian(x.CoeffCosts, txSize, txType, plane == 0 ? 0 : 1, txbCtx, p.Coeff.AsSpan(off, n),
            p.Qcoeff.AsSpan(off, n), p.Dqcoeff.AsSpan(off, n), ref eob, AomEncodeMb.TxTypeCost(x, plane, txSize, txType, cpi.ReducedTxSetUsed),
            AomEncodeMb.ScanOf(txSize, txType), false, p.Dequant0, p.Dequant1);
    }

    // idx_map / sel patterns of prune_txk_type_separ
    private static readonly int[] IdxMap =
    {
        DCT_DCT, DCT_ADST, DCT_FLIPADST, V_DCT, ADST_DCT, ADST_ADST, ADST_FLIPADST, V_ADST,
        FLIPADST_DCT, FLIPADST_ADST, FLIPADST_FLIPADST, V_FLIPADST, H_DCT, H_ADST, H_FLIPADST, IDTX,
    };
    private static readonly int[] SelPatternV = { 0, 0, 1, 1, 0, 2, 1, 2, 2, 0, 3, 1, 3, 2, 3, 3 };
    private static readonly int[] SelPatternH = { 0, 1, 0, 1, 2, 0, 2, 1, 2, 3, 0, 3, 1, 3, 2, 3 };

    /// <summary>prune_txk_type_separ.</summary>
    private static ushort PruneTxkTypeSepar(AomComp cpi, AomMacroblock x, int plane, int block, int txSize, int blkRow, int blkCol,
        int planeBsize, Span<int> txkMap, int allowedTxMask, int pruneFactor, AomTxbCtx txbCtx, long refBestRd, int numSel)
    {
        Span<long> rdsV = stackalloc long[4];
        Span<long> rdsH = stackalloc long[4];
        Span<int> idxV = stackalloc int[] { 0, 1, 2, 3 };
        Span<int> idxH = stackalloc int[] { 0, 1, 2, 3 };
        Span<int> skipV = stackalloc int[4];
        Span<int> skipH = stackalloc int[4];
        skipV.Clear(); skipH.Clear();
        var qp = AomEncodeMb.SetupQuant(txSize, true, AomXformQuant.B, cpi.QuantBAdapt);
        int rateCost;
        long dist, sse;
        // evaluate horizontal with vertical DCT
        for (int idx = 0; idx < 4; ++idx)
        {
            int txType = IdxMap[idx];
            XformQuant(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType, qp);
            DistBlockTxDomain(x, plane, block, txSize, out dist, out sse);
            rateCost = CostCoeffsLaplacian(cpi, x, plane, block, txSize, txType, txbCtx);
            rdsH[idx] = AomRd.RdCost(x.Rdmult, rateCost, dist);
            if ((rdsH[idx] - (rdsH[idx] >> 2)) > refBestRd) skipH[idx] = 1;
        }
        SortRd(rdsH, idxH, 4);
        for (int idx = 1; idx < 4; idx++)
            if (rdsH[idx] > rdsH[0] * 1.2) skipH[idxH[idx]] = 1;

        if (skipH[idxH[0]] != 0) return 0xFFFF;

        // evaluate vertical with the best horizontal chosen
        rdsV[0] = rdsH[0];
        int mapV = idxH[0];
        for (int idx = 1; idx < 4; ++idx)
        {
            int txType = IdxMap[mapV + idxV[idx] * 4];
            XformQuant(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType, qp);
            DistBlockTxDomain(x, plane, block, txSize, out dist, out sse);
            rateCost = CostCoeffsLaplacian(cpi, x, plane, block, txSize, txType, txbCtx);
            rdsV[idx] = AomRd.RdCost(x.Rdmult, rateCost, dist);
            if ((rdsV[idx] - (rdsV[idx] >> 2)) > refBestRd) skipV[idx] = 1;
        }
        SortRd(rdsV, idxV, 4);
        for (int idx = 1; idx < 4; idx++)
            if (rdsV[idx] > rdsV[0] * 1.2) skipV[idxV[idx]] = 1;

        // combine rd_h and rd_v to prune tx candidates
        Span<long> rds = stackalloc long[16];
        int numCand = 0, last = TX_TYPES - 1;
        for (int i = 0; i < 16; i++)
        {
            int iV = SelPatternV[i], iH = SelPatternH[i];
            int txType = IdxMap[idxV[iV] * 4 + idxH[iH]];
            if ((allowedTxMask & (1 << txType)) == 0 || skipH[idxH[iH]] != 0 || skipV[idxV[iV]] != 0)
            {
                txkMap[last] = txType;
                last--;
            }
            else
            {
                txkMap[numCand] = txType;
                rds[numCand] = rdsV[iV] + rdsH[iH];
                if (rds[numCand] == 0) rds[numCand] = 1;
                numCand++;
            }
        }
        SortRd(rds, txkMap, numCand);

        ushort prune = (ushort)~(1 << txkMap[0]);
        numSel = Math.Min(numSel, numCand);
        for (int i = 1; i < numSel; i++)
        {
            long factor = 1800 * (rds[i] - rds[0]) / rds[0];
            if (factor < pruneFactor) prune &= (ushort)~(1 << txkMap[i]);
            else break;
        }
        return prune;
    }

    /// <summary>prune_txk_type.</summary>
    private static ushort PruneTxkType(AomComp cpi, AomMacroblock x, int plane, int block, int txSize, int blkRow, int blkCol,
        int planeBsize, Span<int> txkMap, int allowedTxMask, int pruneFactor, AomTxbCtx txbCtx)
    {
        Span<long> rds = stackalloc long[TX_TYPES];
        int numCand = 0, last = TX_TYPES - 1;
        var qp = AomEncodeMb.SetupQuant(txSize, true, AomXformQuant.B, cpi.QuantBAdapt);
        for (int idx = 0; idx < TX_TYPES; idx++)
        {
            int txType = idx;
            if ((allowedTxMask & (1 << txType)) == 0)
            {
                txkMap[last] = txType;
                last--;
                continue;
            }
            XformQuant(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType, qp);
            int rateCost = CostCoeffsLaplacian(cpi, x, plane, block, txSize, txType, txbCtx);
            DistBlockTxDomain(x, plane, block, txSize, out long dist, out _);
            txkMap[numCand] = txType;
            rds[numCand] = AomRd.RdCost(x.Rdmult, rateCost, dist);
            if (rds[numCand] == 0) rds[numCand] = 1;
            numCand++;
        }
        if (numCand == 0) return 0xFFFF;

        SortRd(rds, txkMap, numCand);
        ushort prune = (ushort)~(1 << txkMap[0]);
        // 0 < prune_factor <= 1000 controls aggressiveness
        for (int idx = 1; idx < numCand; idx++)
        {
            long factor = 1000 * (rds[idx] - rds[0]) / rds[0];
            if (factor < pruneFactor) prune &= (ushort)~(1 << txkMap[idx]);
            else break;
        }
        return prune;
    }

    private static readonly int[,] ThreshArr = { { 10, 15, 15, 10, 15, 15, 15 }, { 10, 17, 17, 10, 17, 17, 17 } };

    /// <summary>get_tx_mask (intra blocks).</summary>
    private static ushort GetTxMask(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize,
        AomTxbCtx txbCtx, int ftxsMode, long refBestRd, out int allowedTxkTypes, Span<int> txkMap)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        var sf = cpi.Sf;
        bool isInter = IsInterBlock(mbmi);
        if (isInter) throw new NotSupportedException("get_tx_mask: inter blocks are not part of the all-intra port");
        bool fastTxSearch = (ftxsMode & FTXS_DCT_AND_1D_DCT_ONLY) != 0;
        int txkAllowed = TX_TYPES;
        int probsOff = cpi.TxTypeProbsOffset(txSize);

        if (txfmParams.UseDefaultIntraTxType != 0)
            txkAllowed = DefaultTxType(0, xd, txSize, cpi.UseScreenContentTools);
        else if (x.RdModel == LOW_TXFM_RD)
        {
            if (plane == 0) txkAllowed = DCT_DCT;
        }

        int txSetType = AomEncodeMb.ExtTxSetType(txSize, isInter, cpi.ReducedTxSetUsed != 0);

        int uvTxType = DCT_DCT;
        if (plane != 0)
            uvTxType = txkAllowed = AomEncodeMb.GetTxType(xd, 1, blkRow, blkCol, txSize, cpi.ReducedTxSetUsed != 0);
        int intraDir = mbmi.UseFilterIntra != 0 ? FimodeToIntradir[mbmi.FilterIntraMode] : mbmi.Mode;
        int extTxUsedFlag = sf.tx_sf.tx_type_search.use_reduced_intra_txset != 0 && txSetType == EXT_TX_SET_DTT4_IDTX_1DDCT
            ? ReducedIntraTxUsedFlag[intraDir] : ExtTxUsedFlag[txSetType];
        if (sf.tx_sf.tx_type_search.use_reduced_intra_txset == 2) extTxUsedFlag &= DerivedIntraTxUsedFlag[intraDir];

        if (xd.Lossless[mbmi.SegmentId] != 0 || TxsizeSqrUpMap[txSize] > TX_32X32 || extTxUsedFlag == 0x0001 || cpi.UseIntraDctOnly)
            txkAllowed = DCT_DCT;

        if (!cpi.EnableFlipIdtx) extTxUsedFlag &= DCT_ADST_TX_MASK;

        int allowedTxMask;   // 1: allow; 0: skip
        if (txkAllowed < TX_TYPES)
        {
            allowedTxMask = 1 << txkAllowed;
            allowedTxMask &= extTxUsedFlag;
        }
        else if (fastTxSearch)
        {
            allowedTxMask = 0x0c01;   // V_DCT, H_DCT, DCT_DCT
            allowedTxMask &= extTxUsedFlag;
        }
        else if (txfmParams.UseDerivedIntraTxTypeSet != 0)
        {
            allowedTxMask = DerivedIntraTxUsedFlag[intraDir];
            allowedTxMask &= extTxUsedFlag;
        }
        else
        {
            allowedTxMask = extTxUsedFlag;
            int numAllowed = 0;
            if (sf.tx_sf.tx_type_search.prune_tx_type_using_stats != 0)
            {
                int thresh = ThreshArr[sf.tx_sf.tx_type_search.prune_tx_type_using_stats - 1, cpi.UpdateType];
                int prune = 0, maxProb = -1, maxIdx = 0;
                for (int i = 0; i < TX_TYPES; i++)
                {
                    int prob = cpi.TxTypeProbs[probsOff + i];
                    if (prob > maxProb && (allowedTxMask & (1 << i)) != 0) { maxProb = prob; maxIdx = i; }
                    if (prob < thresh) prune |= 1 << i;
                }
                if (((prune >> maxIdx) & 1) != 0) prune &= ~(1 << maxIdx);
                allowedTxMask &= ~prune;
            }
            for (int i = 0; i < TX_TYPES; i++)
                if ((allowedTxMask & (1 << i)) != 0) numAllowed++;

            if (numAllowed > 2 && sf.tx_sf.tx_type_search.prune_tx_type_est_rd != 0)
            {
                int pf = PruneFactors[txfmParams.Prune2dTxfmMode];
                int mf = MulFactors[txfmParams.Prune2dTxfmMode];
                if (numAllowed <= 7)
                {
                    ushort prune = PruneTxkType(cpi, x, plane, block, txSize, blkRow, blkCol, planeBsize, txkMap, allowedTxMask, pf, txbCtx);
                    allowedTxMask &= ~prune;
                }
                else
                {
                    int numSel = (numAllowed * mf + 50) / 100;
                    ushort prune = PruneTxkTypeSepar(cpi, x, plane, block, txSize, blkRow, blkCol, planeBsize, txkMap, allowedTxMask, pf,
                        txbCtx, refBestRd, numSel);
                    allowedTxMask &= ~prune;
                }
            }
            // (prune_tx_2D runs for inter blocks only)
        }

        // Need to have at least one transform type allowed.
        if (allowedTxMask == 0)
        {
            txkAllowed = plane != 0 ? uvTxType : DCT_DCT;
            allowedTxMask = 1 << txkAllowed;
        }
        allowedTxkTypes = txkAllowed;
        return (ushort)allowedTxMask;
    }

    /// <summary>skip_trellis_opt_based_on_satd.</summary>
    private static bool SkipTrellisOptBasedOnSatd(AomMacroblock x, ref AomQuantParam qp, int plane, int block, int txSize, int quantBAdapt,
        int qstep, uint coeffOptSatdThreshold, bool skipTrellis, bool dcOnlyBlk)
    {
        if (skipTrellis || coeffOptSatdThreshold == uint.MaxValue) return skipTrellis;
        var p = x.Plane[plane];
        int off = AomEncodeMb.BlockOffset(block);
        int nCoeffs = AomEncodeMb.MaxEob(txSize);
        int shift = MAX_TX_SCALE - AomQuantize.TxScale(txSize);
        int satd = dcOnlyBlk ? Math.Abs(p.Coeff[off]) : AomEncodeMb.Satd(p.Coeff.AsSpan(off, nCoeffs), nCoeffs);
        satd = (int)RightSignedShift(satd, shift);
        satd >>= x.E.Bd - 8;
        bool skipBlockTrellis = (ulong)satd > (ulong)coeffOptSatdThreshold * (ulong)qstep * (ulong)SqrtTxPixels2d[txSize];
        qp = AomEncodeMb.SetupQuant(txSize, !skipBlockTrellis,
            skipBlockTrellis ? (UseBQuantNoTrellis ? AomXformQuant.B : AomXformQuant.Fp) : AomXformQuant.Fp, quantBAdapt);
        return skipBlockTrellis;
    }

    /// <summary>predict_dc_only_block.</summary>
    private static void PredictDcOnlyBlock(AomMacroblock x, int plane, int planeBsize, int txSize, int block, int blkRow, int blkCol,
        ref AomRdStats bestRdStats, out long blockSse, out uint blockMseQ8, ref long perPxMean, ref bool dcOnlyBlk)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int dequantShift = xd.Bd > 8 ? xd.Bd - 5 : 3;
        int qstep = x.Plane[plane].Dequant1 >> dequantShift;
        int dcQstep = x.Plane[plane].Dequant0 >> 3;
        blockSse = PixelDiffStats(x, plane, blkRow, blkCol, planeBsize, TxsizeToBsize[txSize], out blockMseQ8, ref perPxMean, out ulong blockVar);
        ulong varThreshold = (ulong)(1.8 * qstep * qstep);
        if (xd.Bd > 8) blockVar = (blockVar + (1UL << ((xd.Bd - 8) * 2 - 1))) >> ((xd.Bd - 8) * 2);
        if (blockVar >= varThreshold) return;
        int predictDcLevel = x.TxfmSearchParams.PredictDcLevel;

        // Prediction of skip block if residual mean and variance are less than qstep based threshold
        if (Math.Abs(perPxMean) * DcCoeffScale[txSize] < ((long)dcQstep << 12))
        {
            bestRdStats.SkipTxfm = 1;
            x.Plane[plane].Eobs[block] = 0;
            if (xd.Bd > 8) blockSse = (blockSse + (1L << ((xd.Bd - 8) * 2 - 1))) >> ((xd.Bd - 8) * 2);
            bestRdStats.Dist = blockSse << 4;
            bestRdStats.Sse = bestRdStats.Dist;

            // (libaom takes the contexts of the whole plane block's top-left here, not of this tx block)
            Span<byte> ctxa = stackalloc byte[AomMacroblockD.MaxMibSize];
            Span<byte> ctxl = stackalloc byte[AomMacroblockD.MaxMibSize];
            GetEntropyContexts(planeBsize, xd.Plane[plane], ctxa, ctxl);
            int txsCtx = AomTxb.TxsizeEntropyCtx(txSize);
            var txbCtxTmp = AomTxb.TxbCtx(planeBsize, txSize, plane, ctxa, ctxl);
            int zeroBlkRate = x.CoeffCosts.Get(txsCtx, plane == 0 ? 0 : 1).TxbSkip[txbCtxTmp.TxbSkipCtx * 2 + 1];
            bestRdStats.Rate = zeroBlkRate;
            bestRdStats.Rdcost = AomRd.RdCost(x.Rdmult, bestRdStats.Rate, bestRdStats.Sse);
            x.Plane[plane].TxbEntropyCtx[block] = 0;
        }
        else if (predictDcLevel > 1)
        {
            // Predict DC only blocks based on residual variance. For chroma, disabled for intra blocks.
            if (plane == 0 || (plane > 0 && IsInterBlock(mbmi))) dcOnlyBlk = true;
        }
    }

    /// <summary>inverse_transform_block_facade.</summary>
    private static void InverseTransformBlockFacade(AomMacroblock x, int plane, int block, int blkRow, int blkCol, int eob, int reducedTxSet)
    {
        if (eob == 0) return;
        var p = x.Plane[plane];
        var xd = x.E;
        int txSize = AomEncodeMb.GetTxSize(plane, xd);
        int txType = AomEncodeMb.GetTxType(xd, plane == 0 ? 0 : 1, blkRow, blkCol, txSize, reducedTxSet != 0);
        var pd = xd.Plane[plane];
        int dstStride = pd.Dst.Stride;
        AomEncodeMb.InverseTransformBlock(p.Dqcoeff, AomEncodeMb.BlockOffset(block), txType, txSize, pd.Dst.Buf,
            pd.Dst.Offset + ((blkRow * dstStride + blkCol) << 2), dstStride, eob);
    }

    /// <summary>recon_intra.</summary>
    private static void ReconIntra(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize,
        AomTxbCtx txbCtx, bool skipTrellis, int bestTxType, bool doQuant, ref int rateCost, int bestEob)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool isInter = IsInterBlock(mbmi);
        if (!isInter && bestEob != 0 &&
            (blkRow + TxSizeHighUnit[txSize] < MiSizeHigh[planeBsize] || blkCol + TxSizeWideUnit[txSize] < MiSizeWide[planeBsize]))
        {
            // if the quantized coefficients are stored in the dqcoeff buffer, no transform / quantization again
            if (doQuant)
            {
                var qp = AomEncodeMb.SetupQuant(txSize, !skipTrellis,
                    skipTrellis ? (UseBQuantNoTrellis ? AomXformQuant.B : AomXformQuant.Fp) : AomXformQuant.Fp, cpi.QuantBAdapt);
                XformQuant(x, plane, block, blkRow, blkCol, planeBsize, txSize, bestTxType, qp);
                if (qp.UseOptimizeB) AomEncodeMb.OptimizeB(cpi, x, plane, block, txSize, bestTxType, txbCtx, out rateCost);
            }
            InverseTransformBlockFacade(x, plane, block, blkRow, blkCol, x.Plane[plane].Eobs[block], cpi.ReducedTxSetUsed);

            // (a hash collision can leave eob 0 with a non-DCT type: keep the map at DCT_DCT then)
            if (plane == 0 && x.Plane[plane].Eobs[block] == 0 && bestTxType != DCT_DCT)
                AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, DCT_DCT);
        }
    }

    /// <summary>search_tx_type: the best transform type for one tx block (intra blocks, luma or chroma).</summary>
    internal static void SearchTxType(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize,
        AomTxbCtx txbCtx, int ftxsMode, long refBestRd, ref AomRdStats bestRdStats)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        var sf = cpi.Sf;
        long bestRd = long.MaxValue;
        int bestEob = 0;
        int bestTxType = DCT_DCT;
        int rateCost = 0;
        var p = x.Plane[plane];
        int[] origDqcoeff = p.Dqcoeff;
        int[] bestDqcoeff = x.DqcoeffBuf;
        int txTypeMapIdx = plane != 0 ? 0 : blkRow * xd.TxTypeMapStride + blkCol;
        bestRdStats.Invalidate();

        bool skipTrellis = !IsTrellisUsed(cpi.OptimizeSegArr[mbmi.SegmentId], DRY_RUN_NORMAL);

        byte bestTxbCtx = 0;
        int txkAllowed = TX_TYPES;
        Span<int> txkMap = stackalloc int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        int dequantShift = xd.Bd > 8 ? xd.Bd - 5 : 3;
        int qstep = p.Dequant1 >> dequantShift;

        int txw = TxSizeWide[txSize], txh = TxSizeHigh[txSize];
        long blockSse;
        uint blockMseQ8;
        bool dcOnlyBlk = false;
        bool predictDcBlock = txfmParams.PredictDcLevel >= 1 && txw != 64 && txh != 64;
        long perPxMean = long.MaxValue;
        if (predictDcBlock)
        {
            PredictDcOnlyBlock(x, plane, planeBsize, txSize, block, blkRow, blkCol, ref bestRdStats, out blockSse, out blockMseQ8,
                ref perPxMean, ref dcOnlyBlk);
            if (bestRdStats.SkipTxfm == 1)
            {
                if (plane == 0) xd.TxTypeMap[xd.TxTypeMapOffset + txTypeMapIdx] = DCT_DCT;
                return;
            }
        }
        else blockSse = PixelDiffDist(x, plane, blkRow, blkCol, planeBsize, TxsizeToBsize[txSize], out blockMseQ8);

        // Bit mask of the transform types allowed in the RD search (DCT_DCT only for DC-only blocks)
        ushort txMask = dcOnlyBlk
            ? (ushort)(1 << DCT_DCT)
            : GetTxMask(cpi, x, plane, block, blkRow, blkCol, planeBsize, txSize, txbCtx, ftxsMode, refBestRd, out txkAllowed, txkMap);
        ushort allowedTxMask = txMask;
        if (AomTrace.Out != null) AomTrace.Out.Write($" st p{plane} blk {blkRow} {blkCol} tx {txSize} mask {allowedTxMask:x4} allowed {txkAllowed} dconly {(dcOnlyBlk ? 1 : 0)} bsse {blockSse} mse {blockMseQ8} skip_trellis {(skipTrellis ? 1 : 0)}\n");

        if (xd.Bd > 8)
        {
            int s = (xd.Bd - 8) * 2;
            blockSse = (blockSse + (1L << (s - 1))) >> s;
            blockMseQ8 = (uint)((blockMseQ8 + (1u << (s - 1))) >> s);
        }
        blockSse *= 16;
        // mse / qstep^2 based decision of R-D optimization of coeffs
        bool performBlockCoeffOpt = (ulong)blockMseQ8 <= (ulong)txfmParams.CoeffOptThresholds[0] * (ulong)qstep * (ulong)qstep;
        skipTrellis |= !performBlockCoeffOpt;

        // transform domain distortion while iterating the candidates (accurate for higher residuals)
        bool useTransformDomainDistortion = txfmParams.UseTransformDomainDistortion > 0 &&
            blockMseQ8 >= txfmParams.TxDomainDistThreshold &&
            TxsizeSqrUpMap[txSize] != TX_64X64 &&   // 64-pt transforms keep only half the coefficients
            !dcOnlyBlk;
        // an extra pixel-domain distortion at the end, after the best type is chosen
        bool calcPixelDomainDistortionFinal = txfmParams.UseTransformDomainDistortion == 1 && useTransformDomainDistortion &&
            x.RdModel != LOW_TXFM_RD;
        if (calcPixelDomainDistortionFinal && (txkAllowed < TX_TYPES || allowedTxMask == 0x0001))
            calcPixelDomainDistortionFinal = useTransformDomainDistortion = false;

        Span<bool> skipTrellisBasedOnSatd = stackalloc bool[TX_TYPES];
        skipTrellisBasedOnSatd.Clear();
        var qp = AomEncodeMb.SetupQuant(txSize, !skipTrellis,
            skipTrellis ? (UseBQuantNoTrellis ? AomXformQuant.B : AomXformQuant.Fp) : AomXformQuant.Fp, cpi.QuantBAdapt);

        // Iterate through all transform type candidates.
        for (int idx = 0; idx < TX_TYPES; ++idx)
        {
            int txType = txkMap[idx];
            if ((allowedTxMask & (1 << txType)) == 0) continue;
            if (plane == 0) xd.TxTypeMap[xd.TxTypeMapOffset + txTypeMapIdx] = (byte)txType;
            AomRdStats thisRdStats = default;
            thisRdStats.Invalidate();

            if (!dcOnlyBlk) AomEncodeMb.Xform(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType);
            else AomEncodeMb.XformDcOnly(x, plane, block, txSize, perPxMean);

            skipTrellisBasedOnSatd[txType] = SkipTrellisOptBasedOnSatd(x, ref qp, plane, block, txSize, cpi.QuantBAdapt, qstep,
                txfmParams.CoeffOptThresholds[1], skipTrellis, dcOnlyBlk);

            AomEncodeMb.Quant(x, plane, block, txSize, txType, qp);

            // rate cost of the quantized coefficients
            if (qp.UseOptimizeB) AomEncodeMb.OptimizeB(cpi, x, plane, block, txSize, txType, txbCtx, out rateCost);
            else rateCost = AomEncodeMb.CostCoeffs(x, plane, block, txSize, txType, txbCtx, cpi.ReducedTxSetUsed);

            // terminate early when the coefficient rate alone exceeds best_rd
            if (AomRd.RdCost(x.Rdmult, rateCost, 0) > bestRd) continue;

            // distortion
            if (p.Eobs[block] == 0)
            {
                thisRdStats.Dist = thisRdStats.Sse = blockSse;
            }
            else if (dcOnlyBlk)
            {
                thisRdStats.Sse = blockSse;
                thisRdStats.Dist = DistBlockPxDomain(cpi, x, plane, planeBsize, block, blkRow, blkCol, txSize);
            }
            else if (useTransformDomainDistortion)
            {
                DistBlockTxDomain(x, plane, block, txSize, out thisRdStats.Dist, out thisRdStats.Sse);
            }
            else
            {
                long sseDiff = long.MaxValue;
                // every pixel at 25% of the maximum residue energy (128 * 128 at 8 bits)
                long highEnergyThresh = 128L * 128 * TxSize2d[txSize];
                bool isHighEnergy = blockSse >= highEnergyThresh;
                if (txSize == TX_64X64 || isHighEnergy)
                {
                    // 3 of 4 quadrants of a 64-pt transform are zero and the inverse tends to overflow; sse_diff is
                    // their energy, deciding whether pixel-domain distortion is safe
                    DistBlockTxDomain(x, plane, block, txSize, out thisRdStats.Dist, out thisRdStats.Sse);
                    sseDiff = blockSse - thisRdStats.Sse;
                }
                if (txSize != TX_64X64 || !isHighEnergy || (sseDiff * 2) < thisRdStats.Sse)
                {
                    long txDomainDist = thisRdStats.Dist;
                    thisRdStats.Dist = DistBlockPxDomain(cpi, x, plane, planeBsize, block, blkRow, blkCol, txSize);
                    // high-energy blocks: reconstruction clamping can make the pixel distortion artificially low
                    if (isHighEnergy && thisRdStats.Dist < txDomainDist) thisRdStats.Dist = txDomainDist;
                }
                else
                {
                    thisRdStats.Dist += sseDiff;
                }
                thisRdStats.Sse = blockSse;
            }

            thisRdStats.Rate = rateCost;
            long rd = AomRd.RdCost(x.Rdmult, thisRdStats.Rate, thisRdStats.Dist);
            if (AomTrace.Out != null) AomTrace.Out.Write($"  stt p{plane} blk {blkRow} {blkCol} tx {txSize} type {txType} eob {p.Eobs[block]} rate {thisRdStats.Rate} dist {thisRdStats.Dist} sse {thisRdStats.Sse} rd {rd} trellis {(qp.UseOptimizeB ? 1 : 0)}\n");

            if (rd < bestRd)
            {
                bestRd = rd;
                bestRdStats = thisRdStats;
                bestTxType = txType;
                bestTxbCtx = p.TxbEntropyCtx[block];
                bestEob = p.Eobs[block];
                // Swap dqcoeff buffers
                int[] tmpDqcoeff = bestDqcoeff;
                bestDqcoeff = p.Dqcoeff;
                p.Dqcoeff = tmpDqcoeff;
            }

            // terminate when the best RD is already much worse than the reference
            if (sf.tx_sf.adaptive_txb_search_level != 0)
                if ((bestRd - (bestRd >> sf.tx_sf.adaptive_txb_search_level)) > refBestRd) break;

            // terminate when the block quantized to all zero
            if (sf.tx_sf.tx_type_search.skip_tx_search != 0 && bestEob == 0) break;
        }

        bestRdStats.SkipTxfm = (byte)(bestEob == 0 ? 1 : 0);
        if (plane == 0) AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, bestTxType);
        p.TxbEntropyCtx[block] = bestTxbCtx;
        p.Eobs[block] = (ushort)bestEob;
        skipTrellis = skipTrellisBasedOnSatd[bestTxType];

        // dqcoeff -> the best type's coefficients (no transform / quantization again below)
        p.Dqcoeff = bestDqcoeff;

        if (calcPixelDomainDistortionFinal && bestEob != 0)
        {
            bestRdStats.Dist = DistBlockPxDomain(cpi, x, plane, planeBsize, block, blkRow, blkCol, txSize);
            bestRdStats.Sse = blockSse;
        }

        // Intra mode needs decoded pixels so the next transform block can predict from them.
        ReconIntra(cpi, x, plane, block, blkRow, blkCol, planeBsize, txSize, txbCtx, skipTrellis, bestTxType, false, ref rateCost, bestEob);
        p.Dqcoeff = origDqcoeff;
    }

    /// <summary>block_rd_txfm (intra blocks).</summary>
    private static void BlockRdTxfm(AomRdcostBlockArgs args, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize)
    {
        if (args.ExitEarly)
        {
            args.IncompleteExit = true;
            return;
        }
        var x = args.X;
        var xd = x.E;
        var cpi = args.Cpi;
        bool isInter = IsInterBlock(xd.Mi0);
        AomRdStats thisRdStats = default;
        thisRdStats.Init();

        if (!isInter)
        {
            AomReconIntra.PredictIntraBlockFacade(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, plane, blkCol, blkRow, txSize);
            AomEncodeMb.SubtractTxb(x, plane, planeBsize, blkCol, blkRow, txSize);
            var txfmParams = x.TxfmSearchParams;
            if (txfmParams.EnableNnPruneIntraTxDepths)
            {
                int diffStride = BlockSizeWide[planeBsize];
                int r = AomMl.PredictIntraTxDepthPrune(x.Plane[0].SrcDiff.AsSpan(4 * blkRow * diffStride + 4 * blkCol), diffStride,
                    planeBsize, txSize, xd.Lossless[xd.Mi0.SegmentId] != 0, xd.Bd, x.SourceVariance, AomComp.DcQuantQtx(x.Qindex, 0, xd.Bd));
                if (r >= 0) txfmParams.NnPruneDepthsForIntraTx = r;
                if (txfmParams.NnPruneDepthsForIntraTx == TxPruneLargest)
                {
                    args.RdStats.Invalidate();
                    args.ExitEarly = true;
                    return;
                }
            }
        }

        var a = args.TAbove.AsSpan(blkCol);
        var l = args.TLeft.AsSpan(blkRow);
        var txbCtx = AomTxb.TxbCtx(planeBsize, txSize, plane, a, l);
        SearchTxType(cpi, x, plane, block, blkRow, blkCol, planeBsize, txSize, txbCtx, args.FtxsMode, args.BestRd - args.CurrentRd,
            ref thisRdStats);

        if (plane == 0 && xd.Cfl.StoreY != 0) AomCfl.CflStoreTx(xd, blkRow, blkCol, txSize, planeBsize);

        AomEncodeMb.SetTxbContext(x, plane, block, txSize, a, l);

        // Signal non-skip_txfm for Intra blocks
        long rd = AomRd.RdCost(x.Rdmult, thisRdStats.Rate, thisRdStats.Dist);
        thisRdStats.SkipTxfm = 0;

        args.RdStats.Merge(thisRdStats);
        args.CurrentRd += rd;
        if (args.CurrentRd > args.BestRd) args.ExitEarly = true;
    }

    [ThreadStatic] private static AomRdcostBlockArgs? t_args;

    /// <summary>av1_txfm_rd_in_plane.</summary>
    internal static void TxfmRdInPlane(AomMacroblock x, AomComp cpi, ref AomRdStats rdStats, long refBestRd, long currentRd, int plane,
        int planeBsize, int txSize, int ftxsMode)
    {
        if (!cpi.EnableTx64 && TxsizeSqrUpMap[txSize] == TX_64X64) { rdStats.Invalidate(); return; }
        if (currentRd > refBestRd) { rdStats.Invalidate(); return; }

        var xd = x.E;
        var pd = xd.Plane[plane];
        var args = t_args ??= new AomRdcostBlockArgs();
        // (re-entrancy: the chroma CfL search can nest a luma search; take a fresh args object when one is live)
        if (args.X != null) args = new AomRdcostBlockArgs();
        args.Reset();
        args.X = x;
        args.Cpi = cpi;
        args.BestRd = refBestRd;
        args.CurrentRd = currentRd;
        args.FtxsMode = ftxsMode;
        args.SkipTrellis = 0;
        args.RdStats.Init();

        GetEntropyContexts(planeBsize, pd, args.TAbove, args.TLeft);
        ForeachTransformedBlockInPlane(xd, planeBsize, plane, args);

        bool isInter = IsInterBlock(xd.Mi0);
        bool invalidRd = isInter ? args.IncompleteExit : args.ExitEarly;
        if (invalidRd) rdStats.Invalidate();
        else rdStats = args.RdStats;
        args.X = null!;
    }

    /// <summary>av1_foreach_transformed_block_in_plane with block_rd_txfm as the visitor.</summary>
    private static void ForeachTransformedBlockInPlane(AomMacroblockD xd, int planeBsize, int plane, AomRdcostBlockArgs args)
    {
        var pd = xd.Plane[plane];
        int txSize = AomEncodeMb.GetTxSize(plane, xd);
        int txBsize = TxsizeToBsize[txSize];
        if (planeBsize == txBsize) { BlockRdTxfm(args, plane, 0, 0, 0, planeBsize, txSize); return; }
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
                        BlockRdTxfm(args, plane, i, blkRow, blkCol, planeBsize, txSize);
                        i += step;
                    }
            }
        }
    }

    /// <summary>uniform_txfm_yrd.</summary>
    private static long UniformTxfmYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, long refBestRd, int bs, int txSize, int ftxsMode)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        var modeCosts = x.ModeCosts;
        bool isInter = IsInterBlock(mbmi);
        bool txSelect = txfmParams.TxModeSearchType == TX_MODE_SELECT && BlockSignalsTxsize(mbmi.Bsize);
        int txSizeRate = 0;
        if (txSelect)
        {
            int ctx = TxfmPartitionContext(xd.AboveTxfmContext[xd.AboveTxfmContextOffset], xd.LeftTxfmContextBuffer[xd.LeftTxfmContextOffset],
                mbmi.Bsize, txSize);
            txSizeRate = isInter ? modeCosts.TxfmPartitionCost[ctx * 2 + 0] : TxSizeCost(x, bs, txSize);
        }
        int skipCtx = SkipTxfmContext(xd);
        int noSkipTxfmRate = modeCosts.SkipTxfmCost[skipCtx * 2 + 0];
        int skipTxfmRate = modeCosts.SkipTxfmCost[skipCtx * 2 + 1];
        long skipTxfmRd = isInter ? AomRd.RdCost(x.Rdmult, skipTxfmRate, 0) : long.MaxValue;
        long noThisRd = AomRd.RdCost(x.Rdmult, noSkipTxfmRate + txSizeRate, 0);

        mbmi.TxSize = txSize;
        TxfmRdInPlane(x, cpi, ref rdStats, refBestRd, Math.Min(noThisRd, skipTxfmRd), 0, bs, txSize, ftxsMode);
        if (AomTrace.Out != null) AomTrace.Out.Write($" uyrd tx {txSize} ref {refBestRd} -> rate {rdStats.Rate} dist {rdStats.Dist} txrate {txSizeRate}\n");
        if (rdStats.Rate == int.MaxValue) return long.MaxValue;

        long rd;
        // rate carries everything but the skip flag (the callers add it after all planes); decisions include it
        if (rdStats.SkipTxfm != 0 && isInter) rd = AomRd.RdCost(x.Rdmult, skipTxfmRate, rdStats.Sse);
        else
        {
            // Intra blocks are always signalled as non-skip
            rd = AomRd.RdCost(x.Rdmult, rdStats.Rate + noSkipTxfmRate + txSizeRate, rdStats.Dist);
            rdStats.Rate += txSizeRate;
        }
        if (isInter && rdStats.SkipTxfm == 0 && xd.Lossless[mbmi.SegmentId] == 0)
        {
            long tempSkipTxfmRd = AomRd.RdCost(x.Rdmult, skipTxfmRate, rdStats.Sse);
            if (tempSkipTxfmRd <= rd)
            {
                rd = tempSkipTxfmRd;
                rdStats.Rate = 0;
                rdStats.Dist = rdStats.Sse;
                rdStats.SkipTxfm = 1;
            }
        }
        return rd;
    }

    /// <summary>choose_tx_size_type_from_rd.</summary>
    private static void ChooseTxSizeTypeFromRd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, long refBestRd, int bs)
    {
        rdStats.Invalidate();
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        var sf = cpi.Sf;
        int maxRectTxSize = MaxTxsizeRectLookup[bs];
        bool txSelect = txfmParams.TxModeSearchType == TX_MODE_SELECT;
        int startTx, initDepth;
        if (txSelect)
        {
            startTx = maxRectTxSize;
            initDepth = SearchInitDepth(MiSizeWide[bs], MiSizeHigh[bs], IsInterBlock(mbmi), sf, txfmParams.TxSizeSearchMethod);
            if (initDepth == MAX_TX_DEPTH && !cpi.EnableTx64 && TxsizeSqrUpMap[startTx] == TX_64X64) startTx = SubTxSizeMap[startTx];
        }
        else
        {
            startTx = TxSizeFromTxMode(bs, txfmParams.TxModeSearchType);
            initDepth = MAX_TX_DEPTH;
        }

        Span<byte> bestTxkTypeMap = stackalloc byte[AomMacroblockD.MaxMibSize * AomMacroblockD.MaxMibSize];
        int bestTxSize = maxRectTxSize;
        long bestRd = long.MaxValue;
        int numBlks = BsizeToNumBlk(bs);
        x.RdModel = FULL_TXFM_RD;
        Span<long> rd = stackalloc long[] { long.MaxValue, long.MaxValue, long.MaxValue };
        var map = xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, numBlks);
        for (int txSize = startTx, depth = initDepth; depth <= MAX_TX_DEPTH; depth++, txSize = SubTxSizeMap[txSize])
        {
            if ((!cpi.EnableTx64 && TxsizeSqrUpMap[txSize] == TX_64X64) || (!cpi.EnableRectTx && TxSizeWide[txSize] != TxSizeHigh[txSize]))
                continue;

            if (txfmParams.NnPruneDepthsForIntraTx == TxPruneSplit) break;

            // the NN classifier of the depths runs on the largest transform's residual only
            txfmParams.EnableNnPruneIntraTxDepths = sf.tx_sf.prune_intra_tx_depths_using_nn && txSize == startTx;

            AomRdStats thisRdStats = default;
            long rdThresh = sf.tx_sf.use_rd_based_breakout_for_intra_tx_search ? Math.Min(refBestRd, bestRd) : refBestRd;
            rd[depth] = UniformTxfmYrd(cpi, x, ref thisRdStats, rdThresh, bs, txSize, FTXS_NONE);
            if (rd[depth] < bestRd)
            {
                map.CopyTo(bestTxkTypeMap);
                bestTxSize = txSize;
                bestRd = rd[depth];
                rdStats = thisRdStats;
            }
            if (txSize == TX_4X4) break;
            // with three depths, prune the smallest on the first two's results for low contrast blocks
            if (depth > initDepth && depth != MAX_TX_DEPTH && x.SourceVariance < 256)
                if (rd[depth - 1] != long.MaxValue && rd[depth] > rd[depth - 1]) break;
        }

        if (rdStats.Rate != int.MaxValue)
        {
            mbmi.TxSize = bestTxSize;
            bestTxkTypeMap.Slice(0, numBlks).CopyTo(map);
        }

        // reset the NN flags against unintended use
        txfmParams.EnableNnPruneIntraTxDepths = false;
        txfmParams.NnPruneDepthsForIntraTx = TxPruneNone;
    }

    private static readonly byte[] TxSizeMax32 = { TX_4X4, TX_8X8, TX_16X16, TX_32X32, TX_32X32, TX_4X8, TX_8X4, TX_8X16, TX_16X8, TX_16X32, TX_32X16, TX_32X32, TX_32X32, TX_4X16, TX_16X4, TX_8X32, TX_32X8, TX_16X32, TX_32X16 };
    private static readonly byte[] TxSizeMaxSquare = { TX_4X4, TX_8X8, TX_16X16, TX_32X32, TX_64X64, TX_4X4, TX_4X4, TX_8X8, TX_8X8, TX_16X16, TX_16X16, TX_32X32, TX_32X32, TX_4X4, TX_4X4, TX_8X8, TX_8X8, TX_16X16, TX_16X16 };
    private static readonly byte[] TxSizeMax32Square = { TX_4X4, TX_8X8, TX_16X16, TX_32X32, TX_32X32, TX_4X4, TX_4X4, TX_8X8, TX_8X8, TX_16X16, TX_16X16, TX_32X32, TX_32X32, TX_4X4, TX_4X4, TX_8X8, TX_8X8, TX_16X16, TX_16X16 };

    /// <summary>choose_largest_tx_size.</summary>
    private static void ChooseLargestTxSize(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, long refBestRd, int bs)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        mbmi.TxSize = TxSizeFromTxMode(bs, txfmParams.TxModeSearchType);
        // without tx64, the next available size
        if (!cpi.EnableTx64 && cpi.EnableRectTx) mbmi.TxSize = TxSizeMax32[mbmi.TxSize];
        else if (cpi.EnableTx64 && !cpi.EnableRectTx) mbmi.TxSize = TxSizeMaxSquare[mbmi.TxSize];
        else if (!cpi.EnableTx64 && !cpi.EnableRectTx) mbmi.TxSize = TxSizeMax32Square[mbmi.TxSize];

        int skipCtx = SkipTxfmContext(xd);
        int noSkipTxfmRate = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0];
        int skipTxfmRate = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
        // Skip RDcost is used only for Inter blocks
        long skipTxfmRd = IsInterBlock(mbmi) ? AomRd.RdCost(x.Rdmult, skipTxfmRate, 0) : long.MaxValue;
        long noSkipTxfmRd = AomRd.RdCost(x.Rdmult, noSkipTxfmRate, 0);
        TxfmRdInPlane(x, cpi, ref rdStats, refBestRd, Math.Min(noSkipTxfmRd, skipTxfmRd), 0, bs, mbmi.TxSize, FTXS_NONE);
    }

    /// <summary>choose_smallest_tx_size.</summary>
    private static void ChooseSmallestTxSize(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, long refBestRd, int bs)
    {
        x.E.Mi0.TxSize = TX_4X4;
        TxfmRdInPlane(x, cpi, ref rdStats, refBestRd, 0, 0, bs, TX_4X4, FTXS_NONE);
    }

    /// <summary>av1_pick_uniform_tx_size_type_yrd (intra blocks).</summary>
    internal static void PickUniformTxSizeTypeYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bs, long refBestRd)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        rdStats.Init();
        if (xd.Lossless[mbmi.SegmentId] != 0) ChooseSmallestTxSize(cpi, x, ref rdStats, refBestRd, bs);
        else if (x.TxfmSearchParams.TxSizeSearchMethod == USE_LARGESTALL) ChooseLargestTxSize(cpi, x, ref rdStats, refBestRd, bs);
        else ChooseTxSizeTypeFromRd(cpi, x, ref rdStats, refBestRd, bs);
        if (AomTrace.Out != null)
            AomTrace.Out.Write($"yrd {xd.MiRow} {xd.MiCol} bs {bs} y {mbmi.Mode} ad {mbmi.AngleDelta[0]} fi {mbmi.UseFilterIntra} {mbmi.FilterIntraMode} ref {refBestRd} -> rate {rdStats.Rate} dist {rdStats.Dist} sse {rdStats.Sse} skip {rdStats.SkipTxfm} tx {mbmi.TxSize}\n");
    }

    /// <summary>av1_txfm_uvrd (intra blocks). Returns whether the cost is valid.</summary>
    internal static bool TxfmUvrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd)
    {
        rdStats.Init();
        if (refBestRd < 0) return false;
        if (!x.E.IsChromaRef) return true;

        var xd = x.E;
        var pd = xd.Plane[1];
        long thisRd, skipTxfmRd;
        int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        int uvTxSize = AomEncodeMb.GetTxSize(1, xd);
        bool isCostValid = true;
        for (int plane = 1; plane < 3; ++plane)
        {
            AomRdStats thisRdStats = default;
            TxfmRdInPlane(x, cpi, ref thisRdStats, refBestRd, 0, plane, planeBsize, uvTxSize, FTXS_NONE);
            if (thisRdStats.Rate == int.MaxValue) { isCostValid = false; break; }
            rdStats.Merge(thisRdStats);
            thisRd = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
            skipTxfmRd = AomRd.RdCost(x.Rdmult, 0, rdStats.Sse);
            if (Math.Min(thisRd, skipTxfmRd) > refBestRd) { isCostValid = false; break; }
        }
        if (!isCostValid) rdStats.Invalidate();
        return isCostValid;
    }
}
