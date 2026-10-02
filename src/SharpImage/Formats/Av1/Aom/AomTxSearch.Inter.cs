using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    // oxcf.txfm_cfg.use_inter_dct_only
    public bool UseInterDctOnly;
}

// Port of libaom 3.14.1 av1/encoder/tx_search.c's inter-block paths as the intrabc search reaches them:
// av1_txfm_search, av1_pick_recursive_tx_size_type_yrd (var-tx: select_tx_size_and_type, select_tx_block,
// try_tx_block_no_split / try_tx_block_split, tx_type_rd, ml_predict_tx_split, inter_block_yrd / tx_block_yrd for
// the refinement of the fast search), predict_skip_txfm / set_skip_txfm, and the tx partition context helpers.
/// <summary>MB_RD_INFO.</summary>
internal sealed class AomMbRdInfo
{
    public int TxSize;
    public readonly byte[] InterTxSize = new byte[16];
    public readonly byte[] TxTypeMap = new byte[32 * 32];
    public AomRdStats RdStats;
    public uint HashValue;
}

/// <summary>MB_RD_RECORD.</summary>
internal sealed class AomMbRdRecord
{
    public const int Len = 8;   // RD_RECORD_BUFFER_LEN
    public readonly AomMbRdInfo[] Info = { new(), new(), new(), new(), new(), new(), new(), new() };
    public int IndexStart, Num;
    public void Reset() { IndexStart = 0; Num = 0; }
}

internal sealed partial class AomMacroblock
{
    public readonly AomMbRdRecord MbRdRecord = new();
}

internal static partial class AomTxSearch
{
    private const int MAX_VARTX_DEPTH_ = 2;

    // skip_pred_threshold[bd_idx] (8 / 10 / 12-bit) and max_predict_sf_tx_size
    private static readonly uint[] SkipPredThreshold8 =
        { 64, 64, 64, 70, 60, 60, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 64, 64, 70, 70, 68, 68 };
    private static readonly uint[] SkipPredThreshold10 =
        { 88, 88, 88, 86, 87, 87, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 88, 88, 86, 86, 68, 68 };
    private static readonly uint[] SkipPredThreshold12 =
        { 90, 93, 93, 90, 93, 93, 74, 74, 74, 74, 74, 74, 74, 74, 74, 74, 90, 90, 90, 90, 74, 74 };
    private static readonly byte[] MaxPredictSfTxSize =
    {
        TX_4X4, TX_4X8, TX_8X4, TX_8X8, TX_8X16, TX_16X8, TX_16X16, TX_16X16, TX_16X16, TX_16X16, TX_16X16, TX_16X16,
        TX_16X16, TX_16X16, TX_16X16, TX_16X16, TX_4X16, TX_16X4, TX_8X8, TX_8X8, TX_16X16, TX_16X16,
    };

    // av1_get_txb_size_index tables
    private static readonly byte[] TwWLog2Table = { 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 3, 0, 1, 1, 2, 2, 3 };
    private static readonly byte[] TwHLog2Table = { 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 3, 1, 0, 2, 1, 3, 2 };
    private static readonly byte[] StrideLog2Table = { 0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1, 1, 2, 2, 0, 1, 0, 1, 0, 1 };

    /// <summary>av1_get_txb_size_index.</summary>
    internal static int GetTxbSizeIndex(int bsize, int blkRow, int blkCol)
        => ((blkRow >> TwHLog2Table[bsize]) << StrideLog2Table[bsize]) + (blkCol >> TwWLog2Table[bsize]);

    /// <summary>get_vartx_max_txsize.</summary>
    internal static int GetVartxMaxTxsize(AomMacroblockD xd, int bsize, int plane)
    {
        if (xd.Lossless[xd.Mi0.SegmentId] != 0) return TX_4X4;
        int maxTxsize = MaxTxsizeRectLookup[bsize];
        return plane == 0 ? maxTxsize : AomTxb.AdjustedTxSize(maxTxsize);
    }

    /// <summary>txfm_partition_update.</summary>
    internal static void TxfmPartitionUpdate(byte[] above, int aOff, byte[] left, int lOff, int txSize, int txbSize)
    {
        int bsize = TxsizeToBsize[txbSize];
        int bh = MiSizeHigh[bsize], bw = MiSizeWide[bsize];
        byte txw = (byte)TxSizeWide[txSize], txh = (byte)TxSizeHigh[txSize];
        for (int i = 0; i < bh; ++i) left[lOff + i] = txh;
        for (int i = 0; i < bw; ++i) above[aOff + i] = txw;
    }

    /// <summary>predict_skip_txfm: whether the residual (luma, the whole block) is predicted to quantize to all zero;
    /// dist receives its SSE.</summary>
    internal static bool PredictSkipTxfm(AomMacroblock x, int bsize, out long dist, bool reducedTxSet)
    {
        var txfmParams = x.TxfmSearchParams;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        var xd = x.E;
        int dcQ = AomComp.DcQuantQtx(x.Qindex, 0, xd.Bd);
        dist = PixelDiffDist(x, 0, 0, 0, bsize, bsize, out _);
        long mse = dist / bw / bh;
        int normalizedDcQ = (short)dcQ >> 3;
        long mseThresh = (long)normalizedDcQ * normalizedDcQ / 8;
        long predErr = txfmParams.SkipTxfmLevel >= 2 ? dist : mse;
        if (predErr > mseThresh) return false;
        if (txfmParams.SkipTxfmLevel >= 2) return true;

        int maxTxSize = MaxPredictSfTxSize[bsize];
        int txH = TxSizeHigh[maxTxSize], txW = TxSizeWide[maxTxSize];
        Span<int> coefs = stackalloc int[32 * 32];
        coefs.Clear();
        uint maxQcoefThresh = (xd.Bd == 8 ? SkipPredThreshold8 : xd.Bd == 10 ? SkipPredThreshold10 : SkipPredThreshold12)[bsize];
        var srcDiff = x.Plane[0].SrcDiff;
        int nCoeff = txW * txH;
        int acQ = Av1Tables.DequantTable[xd.Bd == 8 ? 0 : xd.Bd == 10 ? 1 : 2, Math.Clamp(x.Qindex, 0, 255), 1];
        uint dcThresh = maxQcoefThresh * (uint)dcQ, acThresh = maxQcoefThresh * (uint)acQ;
        int rowOff = 0;
        for (int row = 0; row < bh; row += txH)
        {
            for (int col = 0; col < bw; col += txW)
            {
                // av1_fwd_txfm (DCT_DCT, lossless 0; high bit depth: av1_highbd_fwd_txfm)
                if (xd.Bd > 8)
                    Av1FwdTxfmAom.ForwardRawRef(srcDiff.AsSpan(rowOff + col), bw, txW, txH, maxTxSize, Av1InvTransform.Type1dDct,
                        Av1InvTransform.Type1dDct, false, false, coefs.Slice(0, nCoeff));
                else
                    Av1FwdTxfmAom.ForwardRaw(srcDiff.AsSpan(rowOff + col), bw, txW, txH, maxTxSize, Av1InvTransform.Type1dDct,
                        Av1InvTransform.Type1dDct, false, false, coefs.Slice(0, nCoeff));
                uint dcCoef = (uint)Math.Abs(coefs[0]) << 7;
                if (dcCoef >= dcThresh) return false;
                for (int i = 1; i < nCoeff; ++i)
                {
                    uint acCoef = (uint)Math.Abs(coefs[i]) << 7;
                    if (acCoef >= acThresh) return false;
                }
            }
            rowOff += txH * bw;
        }
        return true;
    }

    /// <summary>set_skip_txfm.</summary>
    internal static void SetSkipTxfm(AomMacroblock x, ref AomRdStats rdStats, int bsize, long dist)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int n4 = BsizeToNumBlk(bsize);
        int txSize = MaxTxsizeRectLookup[bsize];
        xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, n4).Fill(DCT_DCT);
        Array.Fill(mbmi.InterTxSize, (byte)txSize);
        mbmi.TxSize = txSize;
        rdStats.SkipTxfm = 1;
        if (xd.Bd > 8) dist = (dist + (1L << (2 * (xd.Bd - 8) - 1))) >> (2 * (xd.Bd - 8));   // ROUND_POWER_OF_TWO
        rdStats.Dist = rdStats.Sse = dist << 4;
        Span<byte> ctxa = stackalloc byte[AomMacroblockD.MaxMibSize];
        Span<byte> ctxl = stackalloc byte[AomMacroblockD.MaxMibSize];
        GetEntropyContexts(bsize, xd.Plane[0], ctxa, ctxl);
        int txsCtx = AomTxb.TxsizeEntropyCtx(txSize);
        var txbCtx = AomTxb.TxbCtx(bsize, txSize, 0, ctxa, ctxl);
        int zeroBlkRate = x.CoeffCosts.Get(txsCtx, 0).TxbSkip[txbCtx.TxbSkipCtx * 2 + 1];
        rdStats.Rate = zeroBlkRate * (BlockSizeWide[bsize] >> TxSizeWideLog2[txSize]) * (BlockSizeHigh[bsize] >> TxSizeHighLog2[txSize]);
    }

    /// <summary>tx_type_rd: the best tx type of an inter luma tx block, merged into rdStats.</summary>
    private static void TxTypeRd(AomComp cpi, AomMacroblock x, int txSize, int blkRow, int blkCol, int block, int planeBsize, AomTxbCtx txbCtx,
        ref AomRdStats rdStats, int ftxsMode, long refRdcost)
    {
        AomRdStats thisRdStats = default;
        SearchTxType(cpi, x, 0, block, blkRow, blkCol, planeBsize, txSize, txbCtx, ftxsMode, refRdcost, ref thisRdStats);
        rdStats.Merge(thisRdStats);
    }

    private struct TxCandidateInfo
    {
        public long Rd;
        public int TxbEntropyCtx;
        public int TxType;
    }

    /// <summary>try_tx_block_no_split.</summary>
    private static void TryTxBlockNoSplit(AomComp cpi, AomMacroblock x, int blkRow, int blkCol, int block, int txSize, int depth,
        int planeBsize, byte[] ta, byte[] tl, int txfmPartitionCtx, ref AomRdStats rdStats, long refBestRd, int ftxsMode,
        ref TxCandidateInfo noSplit)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var p = x.Plane[0];
        int txsCtx = AomTxb.TxsizeEntropyCtx(txSize);
        var txbCtx = AomTxb.TxbCtx(planeBsize, txSize, 0, ta.AsSpan(blkCol), tl.AsSpan(blkRow));
        int zeroBlkRate = x.CoeffCosts.Get(txsCtx, 0).TxbSkip[txbCtx.TxbSkipCtx * 2 + 1];
        rdStats.ZeroRate = zeroBlkRate;
        int index = GetTxbSizeIndex(planeBsize, blkRow, blkCol);
        mbmi.InterTxSize[index] = (byte)txSize;
        TxTypeRd(cpi, x, txSize, blkRow, blkCol, block, planeBsize, txbCtx, ref rdStats, ftxsMode, refBestRd);

        bool pickSkipTxfm = xd.Lossless[mbmi.SegmentId] == 0 &&
            (rdStats.SkipTxfm == 1 || AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist) >= AomRd.RdCost(x.Rdmult, zeroBlkRate, rdStats.Sse));
        if (pickSkipTxfm)
        {
            rdStats.Rate = zeroBlkRate;
            rdStats.Dist = rdStats.Sse;
            p.Eobs[block] = 0;
            AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, DCT_DCT);
        }
        rdStats.SkipTxfm = (byte)(pickSkipTxfm ? 1 : 0);
        if (txSize > TX_4X4 && depth < MAX_VARTX_DEPTH_) rdStats.Rate += x.ModeCosts.TxfmPartitionCost[txfmPartitionCtx * 2 + 0];
        noSplit.Rd = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
        noSplit.TxbEntropyCtx = p.TxbEntropyCtx[block];
        noSplit.TxType = xd.TxTypeMap[xd.TxTypeMapOffset + blkRow * xd.TxTypeMapStride + blkCol];
    }

    /// <summary>try_tx_block_split.</summary>
    private static void TryTxBlockSplit(AomComp cpi, AomMacroblock x, int blkRow, int blkCol, int block, int txSize, int depth, int planeBsize,
        byte[] ta, byte[] tl, byte[] txAbove, byte[] txLeft, int txfmPartitionCtx, long noSplitRd, long refBestRd, int ftxsMode,
        ref AomRdStats splitRdStats)
    {
        var xd = x.E;
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, 0), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, 0);
        int txbWidth = TxSizeWideUnit[txSize], txbHeight = TxSizeHighUnit[txSize];
        int subTxs = SubTxSizeMap[txSize];
        int subTxbWidth = TxSizeWideUnit[subTxs], subTxbHeight = TxSizeHighUnit[subTxs];
        int subStep = subTxbWidth * subTxbHeight;
        int nblks = (txbHeight / subTxbHeight) * (txbWidth / subTxbWidth);
        splitRdStats.Init();
        splitRdStats.Rate = x.ModeCosts.TxfmPartitionCost[txfmPartitionCtx * 2 + 1];
        for (int r = 0; r < txbHeight; r += subTxbHeight)
        {
            int offsetr = blkRow + r;
            if (offsetr >= maxBlocksHigh) break;
            for (int c = 0; c < txbWidth; c += subTxbWidth)
            {
                int offsetc = blkCol + c;
                if (offsetc >= maxBlocksWide) continue;
                AomRdStats thisRdStats = default;
                bool thisCostValid = true;
                SelectTxBlock(cpi, x, offsetr, offsetc, block, subTxs, depth + 1, planeBsize, ta, tl, txAbove, txLeft, ref thisRdStats,
                    noSplitRd / nblks, refBestRd - splitRdStats.Rdcost, ref thisCostValid, ftxsMode, -1);
                if (!thisCostValid)
                {
                    splitRdStats.Rdcost = long.MaxValue;
                    return;
                }
                splitRdStats.Merge(thisRdStats);
                splitRdStats.Rdcost = AomRd.RdCost(x.Rdmult, splitRdStats.Rate, splitRdStats.Dist);
                if (splitRdStats.Rdcost > refBestRd)
                {
                    splitRdStats.Rdcost = long.MaxValue;
                    return;
                }
                block += subStep;
            }
        }
    }

    /// <summary>select_tx_block: the best tx partition / types of an inter luma tx block (recursive).</summary>
    private static void SelectTxBlock(AomComp cpi, AomMacroblock x, int blkRow, int blkCol, int block, int txSize, int depth, int planeBsize,
        byte[] ta, byte[] tl, byte[] txAbove, byte[] txLeft, ref AomRdStats rdStats, long prevLevelRd, long refBestRd,
        ref bool isCostValid, int ftxsMode, int blkIdx)
    {
        rdStats.Init();
        if (refBestRd < 0)
        {
            isCostValid = false;
            return;
        }
        var xd = x.E;
        var mbmi = xd.Mi0;
        var sf = cpi.Sf;
        int ctx = TxfmPartitionContext(txAbove[blkCol], txLeft[blkRow], mbmi.Bsize, txSize);
        var p = x.Plane[0];
        bool tryNoSplit = (cpi.EnableTx64 || TxsizeSqrUpMap[txSize] != TX_64X64) && (cpi.EnableRectTx || TxSizeWide[txSize] == TxSizeHigh[txSize]);
        bool trySplit = txSize > TX_4X4 && depth < MAX_VARTX_DEPTH_;
        var noSplit = new TxCandidateInfo { Rd = long.MaxValue, TxbEntropyCtx = 0, TxType = TX_TYPES };

        if (txSize != TX_4X4 && trySplit && tryNoSplit && sf.tx_sf.prune_tx_size_level > 0)
        {
            int diffStride = BlockSizeWide[planeBsize];
            AomMl.PruneTxSplitNoSplit(p.SrcDiff.AsSpan(4 * blkRow * diffStride + 4 * blkCol), diffStride, TxSizeWide[txSize], TxSizeHigh[txSize],
                p.Dequant0, p.Dequant1, ref tryNoSplit, ref trySplit, sf.tx_sf.prune_tx_size_level);
        }
        // (rt_sf.skip_tx_no_split_var_based_partition is a real-time feature)

        if (tryNoSplit)
        {
            TryTxBlockNoSplit(cpi, x, blkRow, blkCol, block, txSize, depth, planeBsize, ta, tl, ctx, ref rdStats, refBestRd, ftxsMode, ref noSplit);
            // push_inter_block_tx_no_split_rd / prune_tx_split_eval_using_no_split_rd: never for intrabc blocks
            if (sf.tx_sf.prune_inter_tx_split_rd_eval_lvl != 0 && mbmi.UseIntrabc == 0 && mbmi.SkipMode == 0)
                throw new NotSupportedException("prune_inter_tx_split_rd_eval_lvl (inter frames)");
            int searchLevel = sf.tx_sf.adaptive_txb_search_level;
            if (searchLevel != 0)
            {
                if ((noSplit.Rd - (noSplit.Rd >> (1 + searchLevel))) > refBestRd)
                {
                    isCostValid = false;
                    return;
                }
                if (noSplit.Rd - (noSplit.Rd >> (2 + searchLevel)) > prevLevelRd) trySplit = false;
            }
            if (sf.tx_sf.txb_split_cap != 0 && p.Eobs[block] == 0) trySplit = false;
        }

        // ML based speed feature to skip searching for split transform blocks
        if (xd.Bd == 8 && trySplit && !(refBestRd == long.MaxValue && noSplit.Rd == long.MaxValue))
        {
            int threshold = sf.tx_sf.tx_type_search.ml_tx_split_thresh;
            if (threshold >= 0)
            {
                int diffStride = BlockSizeWide[planeBsize];
                int splitScore = AomMl.PredictTxSplit(p.SrcDiff.AsSpan(4 * blkRow * diffStride + 4 * blkCol), diffStride, txSize);
                if (splitScore < -threshold) trySplit = false;
            }
        }

        AomRdStats splitRdStats = default;
        splitRdStats.Rdcost = long.MaxValue;
        if (trySplit)
            TryTxBlockSplit(cpi, x, blkRow, blkCol, block, txSize, depth, planeBsize, ta, tl, txAbove, txLeft, ctx, noSplit.Rd,
                Math.Min(noSplit.Rd, refBestRd), ftxsMode, ref splitRdStats);

        if (noSplit.Rd < splitRdStats.Rdcost)
        {
            p.TxbEntropyCtx[block] = (byte)noSplit.TxbEntropyCtx;
            AomEncodeMb.SetTxbContext(x, 0, block, txSize, ta.AsSpan(blkCol), tl.AsSpan(blkRow));
            TxfmPartitionUpdate(txAbove, blkCol, txLeft, blkRow, txSize, txSize);
            for (int idy = 0; idy < TxSizeHighUnit[txSize]; ++idy)
                for (int idx = 0; idx < TxSizeWideUnit[txSize]; ++idx)
                {
                    int index = GetTxbSizeIndex(planeBsize, blkRow + idy, blkCol + idx);
                    mbmi.InterTxSize[index] = (byte)txSize;
                }
            mbmi.TxSize = txSize;
            AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, noSplit.TxType);
        }
        else
        {
            rdStats = splitRdStats;
            if (splitRdStats.Rdcost == long.MaxValue) isCostValid = false;
        }
    }

    /// <summary>tx_block_yrd (the fast search's refinement with the tx sizes decided).</summary>
    private static void TxBlockYrd(AomComp cpi, AomMacroblock x, int blkRow, int blkCol, int block, int txSize, int planeBsize, int depth,
        byte[] aboveCtx, byte[] leftCtx, byte[] txAbove, byte[] txLeft, long refBestRd, ref AomRdStats rdStats, int ftxsMode)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, 0), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, 0);
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        int planeTxSize = mbmi.InterTxSize[GetTxbSizeIndex(planeBsize, blkRow, blkCol)];
        int ctx = TxfmPartitionContext(txAbove[blkCol], txLeft[blkRow], mbmi.Bsize, txSize);
        rdStats.Init();
        if (txSize == planeTxSize)
        {
            int txsCtx = AomTxb.TxsizeEntropyCtx(txSize);
            var txbCtx = AomTxb.TxbCtx(planeBsize, txSize, 0, aboveCtx.AsSpan(blkCol), leftCtx.AsSpan(blkRow));
            int zeroBlkRate = x.CoeffCosts.Get(txsCtx, 0).TxbSkip[txbCtx.TxbSkipCtx * 2 + 1];
            rdStats.ZeroRate = zeroBlkRate;
            TxTypeRd(cpi, x, txSize, blkRow, blkCol, block, planeBsize, txbCtx, ref rdStats, ftxsMode, refBestRd);
            if (AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist) >= AomRd.RdCost(x.Rdmult, zeroBlkRate, rdStats.Sse) || rdStats.SkipTxfm == 1)
            {
                rdStats.Rate = zeroBlkRate;
                rdStats.Dist = rdStats.Sse;
                rdStats.SkipTxfm = 1;
                x.Plane[0].Eobs[block] = 0;
                x.Plane[0].TxbEntropyCtx[block] = 0;
                AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, DCT_DCT);
            }
            else rdStats.SkipTxfm = 0;
            if (txSize > TX_4X4 && depth < MAX_VARTX_DEPTH_) rdStats.Rate += x.ModeCosts.TxfmPartitionCost[ctx * 2 + 0];
            AomEncodeMb.SetTxbContext(x, 0, block, txSize, aboveCtx.AsSpan(blkCol), leftCtx.AsSpan(blkRow));
            TxfmPartitionUpdate(txAbove, blkCol, txLeft, blkRow, txSize, txSize);
        }
        else
        {
            int subTxs = SubTxSizeMap[txSize];
            int txbWidth = TxSizeWideUnit[subTxs], txbHeight = TxSizeHighUnit[subTxs];
            int step = txbHeight * txbWidth;
            int rowEnd = Math.Min(TxSizeHighUnit[txSize], maxBlocksHigh - blkRow);
            int colEnd = Math.Min(TxSizeWideUnit[txSize], maxBlocksWide - blkCol);
            long thisRd = 0;
            for (int row = 0; row < rowEnd; row += txbHeight)
                for (int col = 0; col < colEnd; col += txbWidth)
                {
                    AomRdStats pnRdStats = default;
                    pnRdStats.Init();
                    TxBlockYrd(cpi, x, blkRow + row, blkCol + col, block, subTxs, planeBsize, depth + 1, aboveCtx, leftCtx, txAbove, txLeft,
                        refBestRd - thisRd, ref pnRdStats, ftxsMode);
                    if (pnRdStats.Rate == int.MaxValue)
                    {
                        rdStats.Invalidate();
                        return;
                    }
                    rdStats.Merge(pnRdStats);
                    thisRd += AomRd.RdCost(x.Rdmult, pnRdStats.Rate, pnRdStats.Dist);
                    block += step;
                }
            if (txSize > TX_4X4 && depth < MAX_VARTX_DEPTH_) rdStats.Rate += x.ModeCosts.TxfmPartitionCost[ctx * 2 + 1];
        }
    }

    /// <summary>inter_block_yrd. Returns whether the cost is valid.</summary>
    private static bool InterBlockYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd, int ftxsMode)
    {
        if (refBestRd < 0)
        {
            rdStats.Invalidate();
            return false;
        }
        rdStats.Init();
        var xd = x.E;
        var txfmParams = x.TxfmSearchParams;
        var pd = xd.Plane[0];
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];
        int maxTxSize = GetVartxMaxTxsize(xd, bsize, 0);
        int bh = TxSizeHighUnit[maxTxSize], bw = TxSizeWideUnit[maxTxSize];
        int step = bw * bh;
        int initDepth = SearchInitDepth(miWidth, miHeight, true, cpi.Sf, txfmParams.TxSizeSearchMethod);
        var ctxa = new byte[AomMacroblockD.MaxMibSize];
        var ctxl = new byte[AomMacroblockD.MaxMibSize];
        var txAbove = new byte[AomMacroblockD.MaxMibSize];
        var txLeft = new byte[AomMacroblockD.MaxMibSize];
        GetEntropyContexts(bsize, pd, ctxa, ctxl);
        Array.Copy(xd.AboveTxfmContext, xd.AboveTxfmContextOffset, txAbove, 0, miWidth);
        Array.Copy(xd.LeftTxfmContextBuffer, xd.LeftTxfmContextOffset, txLeft, 0, miHeight);
        long thisRd = 0;
        for (int idy = 0, block = 0; idy < miHeight; idy += bh)
            for (int idx = 0; idx < miWidth; idx += bw)
            {
                AomRdStats pnRdStats = default;
                pnRdStats.Init();
                TxBlockYrd(cpi, x, idy, idx, block, maxTxSize, bsize, initDepth, ctxa, ctxl, txAbove, txLeft, refBestRd - thisRd, ref pnRdStats,
                    ftxsMode);
                if (pnRdStats.Rate == int.MaxValue)
                {
                    rdStats.Invalidate();
                    return false;
                }
                rdStats.Merge(pnRdStats);
                thisRd += Math.Min(AomRd.RdCost(x.Rdmult, pnRdStats.Rate, pnRdStats.Dist), AomRd.RdCost(x.Rdmult, pnRdStats.ZeroRate, pnRdStats.Sse));
                block += step;
            }
        int skipCtx = SkipTxfmContext(xd);
        int noSkipTxfmRate = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0], skipTxfmRate = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
        long skipTxfmRd = AomRd.RdCost(x.Rdmult, skipTxfmRate, rdStats.Sse);
        thisRd = AomRd.RdCost(x.Rdmult, rdStats.Rate + noSkipTxfmRate, rdStats.Dist);
        if (skipTxfmRd < thisRd)
        {
            thisRd = skipTxfmRd;
            rdStats.Rate = 0;
            rdStats.Dist = rdStats.Sse;
            rdStats.SkipTxfm = 1;
        }
        bool isCostValid = thisRd > refBestRd;   // (sic: libaom's condition)
        if (!isCostValid) rdStats.Invalidate();
        return isCostValid;
    }

    /// <summary>select_tx_size_and_type. Returns the RD cost.</summary>
    private static long SelectTxSizeAndType(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd)
    {
        var xd = x.E;
        var txfmParams = x.TxfmSearchParams;
        bool fastTxSearch = txfmParams.TxSizeSearchMethod > USE_FULL_RD;
        long rdThresh = refBestRd;
        if (rdThresh == 0)
        {
            rdStats.Invalidate();
            return long.MaxValue;
        }
        if (fastTxSearch && rdThresh < long.MaxValue)
            if (long.MaxValue - rdThresh > (rdThresh >> 3)) rdThresh += rdThresh >> 3;
        int ftxsMode = fastTxSearch ? FTXS_DCT_AND_1D_DCT_ONLY : FTXS_NONE;
        var pd = xd.Plane[0];
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];
        var ctxa = new byte[AomMacroblockD.MaxMibSize];
        var ctxl = new byte[AomMacroblockD.MaxMibSize];
        var txAbove = new byte[AomMacroblockD.MaxMibSize];
        var txLeft = new byte[AomMacroblockD.MaxMibSize];
        GetEntropyContexts(bsize, pd, ctxa, ctxl);
        Array.Copy(xd.AboveTxfmContext, xd.AboveTxfmContextOffset, txAbove, 0, miWidth);
        Array.Copy(xd.LeftTxfmContextBuffer, xd.LeftTxfmContextOffset, txLeft, 0, miHeight);
        int initDepth = SearchInitDepth(miWidth, miHeight, true, cpi.Sf, txfmParams.TxSizeSearchMethod);
        int maxTxSize = MaxTxsizeRectLookup[bsize];
        int bh = TxSizeHighUnit[maxTxSize], bw = TxSizeWideUnit[maxTxSize];
        int step = bw * bh;
        int skipCtx = SkipTxfmContext(xd);
        int noSkipTxfmCost = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0], skipTxfmCost = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
        long skipTxfmRd = AomRd.RdCost(x.Rdmult, skipTxfmCost, 0);
        long noSkipTxfmRd = AomRd.RdCost(x.Rdmult, noSkipTxfmCost, 0);
        int block = 0, blkIdx = 0;
        rdStats.Init();
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, bsize, 0), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, bsize, 0);
        for (int idy = 0; idy < maxBlocksHigh; idy += bh)
            for (int idx = 0; idx < maxBlocksWide; idx += bw)
            {
                long bestRdSofar = rdThresh == long.MaxValue ? long.MaxValue : rdThresh - Math.Min(skipTxfmRd, noSkipTxfmRd);
                bool isCostValid = true;
                AomRdStats pnRdStats = default;
                SelectTxBlock(cpi, x, idy, idx, block, maxTxSize, initDepth, bsize, ctxa, ctxl, txAbove, txLeft, ref pnRdStats, long.MaxValue,
                    bestRdSofar, ref isCostValid, ftxsMode, blkIdx);
                blkIdx++;
                if (!isCostValid || pnRdStats.Rate == int.MaxValue)
                {
                    rdStats.Invalidate();
                    return long.MaxValue;
                }
                rdStats.Merge(pnRdStats);
                skipTxfmRd = AomRd.RdCost(x.Rdmult, skipTxfmCost, rdStats.Sse);
                noSkipTxfmRd = AomRd.RdCost(x.Rdmult, rdStats.Rate + noSkipTxfmCost, rdStats.Dist);
                block += step;
            }
        if (rdStats.Rate == int.MaxValue) return long.MaxValue;
        rdStats.SkipTxfm = (byte)(skipTxfmRd <= noSkipTxfmRd ? 1 : 0);

        // the fast search tested only DCT and 1D DCT: redo the tx types with the tx sizes decided
        if (fastTxSearch && cpi.Sf.tx_sf.refine_fast_tx_search_results != 0)
            if (!InterBlockYrd(cpi, x, ref rdStats, bsize, refBestRd, FTXS_NONE)) return long.MaxValue;

        long finalRd;
        if (rdStats.SkipTxfm != 0) finalRd = AomRd.RdCost(x.Rdmult, skipTxfmCost, rdStats.Sse);
        else
        {
            finalRd = AomRd.RdCost(x.Rdmult, rdStats.Rate + noSkipTxfmCost, rdStats.Dist);
            if (xd.Lossless[xd.Mi0.SegmentId] == 0) finalRd = Math.Min(finalRd, AomRd.RdCost(x.Rdmult, skipTxfmCost, rdStats.Sse));
        }
        return finalRd;
    }

    /// <summary>av1_pick_recursive_tx_size_type_yrd (no mb rd hash: use_mb_rd_hash is off for all-intra).</summary>
    internal static void PickRecursiveTxSizeTypeYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd)
    {
        var txfmParams = x.TxfmSearchParams;
        var xd = x.E;
        rdStats.Invalidate();
        if (cpi.Sf.tx_sf.model_based_prune_tx_search_level != 0 && refBestRd != long.MaxValue)
            if (ModelBasedTxSearchPrune(cpi, x, bsize, refBestRd)) return;
        uint hash = 0;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        bool withinBorder = miRow >= xd.TileMiRowStart && miRow + MiSizeHigh[bsize] < xd.TileMiRowEnd && miCol >= xd.TileMiColStart &&
                            miCol + MiSizeWide[bsize] < xd.TileMiColEnd;
        bool hashEnabled = withinBorder && cpi.Sf.rd_sf.use_mb_rd_hash != 0;
        int n4 = BsizeToNumBlk(bsize);
        var rec = x.MbRdRecord;
        if (hashEnabled)
        {
            hash = GetBlockResidueHash(x, bsize);
            int match = FindMbRdInfo(rec, refBestRd, hash);
            if (match != -1)
            {
                FetchMbRdInfo(n4, rec.Info[match], ref rdStats, x);
                return;
            }
        }
        if (txfmParams.SkipTxfmLevel != 0 && PredictSkipTxfm(x, bsize, out long dist, cpi.ReducedTxSetUsed != 0))
        {
            SetSkipTxfm(x, ref rdStats, bsize, dist);
            if (hashEnabled) SaveMbRdInfo(n4, hash, x, rdStats, rec);
            return;
        }
        long rd = SelectTxSizeAndType(cpi, x, ref rdStats, bsize, refBestRd);
        if (rd == long.MaxValue)
        {
            rdStats.Invalidate();
            return;
        }
        if (hashEnabled) SaveMbRdInfo(n4, hash, x, rdStats, rec);
    }

    /// <summary>model_based_tx_search_prune.</summary>
    private static bool ModelBasedTxSearchPrune(AomComp cpi, AomMacroblock x, int bsize, long refBestRd)
    {
        int level = cpi.Sf.tx_sf.model_based_prune_tx_search_level;
        AomModelRd.SbFn(AomModelRd.MODELRD_TYPE_TX_SEARCH_PRUNE, cpi, bsize, x, x.E, 0, 0, out int modelRate, out long modelDist, out byte modelSkip,
            out _, null, null, null);
        if (modelSkip != 0) return false;
        long modelRd = AomRd.RdCost(x.Rdmult, modelRate, modelDist);
        int factor = level == 1 ? 3 : 5;
        return ((modelRd * factor) >> 3) > refBestRd;
    }

    /// <summary>get_block_residue_hash: CRC32C of the luma residual, then (hash &lt;&lt; 5) + bsize.</summary>
    internal static uint GetBlockResidueHash(AomMacroblock x, int bsize)
    {
        int n = BlockSizeHigh[bsize] * BlockSizeWide[bsize];
        var diff = System.Runtime.InteropServices.MemoryMarshal.AsBytes(x.Plane[0].SrcDiff.AsSpan(0, n));
        uint crc = 0xffffffff;
        int i = 0;
        for (; i + 8 <= diff.Length; i += 8) crc = System.Numerics.BitOperations.Crc32C(crc, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(diff.Slice(i)));
        for (; i < diff.Length; i++) crc = System.Numerics.BitOperations.Crc32C(crc, diff[i]);
        uint hash = crc ^ 0xffffffff;
        return (hash << 5) + (uint)bsize;
    }

    private static int FindMbRdInfo(AomMbRdRecord rec, long refBestRd, uint hash)
    {
        if (refBestRd != long.MaxValue)
            for (int i = 0; i < rec.Num; ++i)
            {
                int index = (rec.IndexStart + i) % AomMbRdRecord.Len;
                if (rec.Info[index].HashValue == hash) return index;
            }
        return -1;
    }

    private static void FetchMbRdInfo(int n4, AomMbRdInfo info, ref AomRdStats rdStats, AomMacroblock x)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        mbmi.TxSize = info.TxSize;
        Array.Copy(info.InterTxSize, mbmi.InterTxSize, mbmi.InterTxSize.Length);
        AomRdoptInter.CopyTxTypeMapFrom(xd, info.TxTypeMap, n4);
        rdStats = info.RdStats;
    }

    private static void SaveMbRdInfo(int n4, uint hash, AomMacroblock x, in AomRdStats rdStats, AomMbRdRecord rec)
    {
        int index;
        if (rec.Num < AomMbRdRecord.Len)
        {
            index = (rec.IndexStart + rec.Num) % AomMbRdRecord.Len;
            ++rec.Num;
        }
        else
        {
            index = rec.IndexStart;
            rec.IndexStart = (rec.IndexStart + 1) % AomMbRdRecord.Len;
        }
        var info = rec.Info[index];
        var xd = x.E;
        info.HashValue = hash;
        info.TxSize = xd.Mi0.TxSize;
        Array.Copy(xd.Mi0.InterTxSize, info.InterTxSize, info.InterTxSize.Length);
        AomRdoptInter.CopyTxTypeMapTo(xd, info.TxTypeMap, n4);
        info.RdStats = rdStats;
    }

    /// <summary>av1_txfm_search: the luma and chroma transform search of an inter (intrabc) block whose prediction is
    /// built; sets mbmi->skip_txfm. Returns whether the RD stats are valid.</summary>
    internal static bool TxfmSearch(AomComp cpi, AomMacroblock x, int bsize, ref AomRdStats rdStats, ref AomRdStats rdStatsY,
        ref AomRdStats rdStatsUv, int modeRate, long refBestRd)
    {
        var xd = x.E;
        var txfmParams = x.TxfmSearchParams;
        int skipCtx = SkipTxfmContext(xd);
        int skipTxfmCost0 = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0], skipTxfmCost1 = x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1];
        long minHeaderRate = modeRate + Math.Min(skipTxfmCost0, skipTxfmCost1);
        long minHeaderRdPossible = AomRd.RdCost(x.Rdmult, minHeaderRate, 0);
        if (minHeaderRdPossible > refBestRd)
        {
            rdStatsY.Invalidate();
            return false;
        }
        var mbmi = xd.Mi0;
        long modeRd = AomRd.RdCost(x.Rdmult, modeRate, 0);
        long rdThresh = refBestRd == long.MaxValue ? long.MaxValue : refBestRd - modeRd;
        rdStats.Init();
        rdStatsY.Init();
        rdStats.Rate = modeRate;

        AomEncodeMb.SubtractPlane(x, bsize, 0);
        if (txfmParams.TxModeSearchType == TX_MODE_SELECT && xd.Lossless[mbmi.SegmentId] == 0)
            PickRecursiveTxSizeTypeYrd(cpi, x, ref rdStatsY, bsize, rdThresh);
        else
        {
            PickUniformTxSizeTypeYrd(cpi, x, ref rdStatsY, bsize, rdThresh);
            Array.Fill(mbmi.InterTxSize, (byte)mbmi.TxSize);
        }
        if (rdStatsY.Rate == int.MaxValue) return false;
        rdStats.Merge(rdStatsY);

        long nonSkipTxfmRdcosty = AomRd.RdCost(x.Rdmult, rdStats.Rate + skipTxfmCost0, rdStats.Dist);
        long skipTxfmRdcosty = AomRd.RdCost(x.Rdmult, modeRate + skipTxfmCost1, rdStats.Sse);
        long minRdcosty = Math.Min(nonSkipTxfmRdcosty, skipTxfmRdcosty);
        if (minRdcosty > refBestRd) return false;

        rdStatsUv.Init();
        if (cpi.Cm.NumPlanes > 1)
        {
            long refBestChromaRd = refBestRd;
            if (cpi.Sf.inter_sf.perform_best_rd_based_gating_for_chroma != 0 && refBestChromaRd != long.MaxValue)
                refBestChromaRd = refBestChromaRd - Math.Min(nonSkipTxfmRdcosty, skipTxfmRdcosty);
            bool isCostValidUv = TxfmUvrd(cpi, x, ref rdStatsUv, bsize, refBestChromaRd);
            if (!isCostValidUv) return false;
            rdStats.Merge(rdStatsUv);
        }

        bool chooseSkipTxfm = rdStats.SkipTxfm != 0;
        if (!chooseSkipTxfm && xd.Lossless[mbmi.SegmentId] == 0)
        {
            long rdcostNoSkipTxfm = AomRd.RdCost(x.Rdmult, (long)rdStatsY.Rate + rdStatsUv.Rate + skipTxfmCost0, rdStats.Dist);
            long rdcostSkipTxfm = AomRd.RdCost(x.Rdmult, skipTxfmCost1, rdStats.Sse);
            if (rdcostNoSkipTxfm >= rdcostSkipTxfm) chooseSkipTxfm = true;
        }
        if (chooseSkipTxfm)
        {
            rdStatsY.Rate = 0;
            rdStatsUv.Rate = 0;
            rdStats.Rate = modeRate + skipTxfmCost1;
            rdStats.Dist = rdStats.Sse;
            rdStatsY.Dist = rdStatsY.Sse;
            rdStatsUv.Dist = rdStatsUv.Sse;
            mbmi.SkipTxfm = 1;
            if (rdStats.SkipTxfm != 0)
            {
                long tmprd = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
                if (tmprd > refBestRd) return false;
            }
        }
        else
        {
            rdStats.Rate += skipTxfmCost0;
            mbmi.SkipTxfm = 0;
        }
        return true;
    }
}
