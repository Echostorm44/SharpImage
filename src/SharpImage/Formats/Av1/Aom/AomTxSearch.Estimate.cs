using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal static partial class AomTxSearch
{
    /// <summary>av1_estimate_txfm_yrd: a DCT_DCT-only luma rd estimate at one tx size (no trellis).</summary>
    internal static long EstimateTxfmYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, long refBestRd, int bs, int txSize)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        var mc = x.ModeCosts;
        bool isInter = IsInterBlock(mbmi);
        bool txSelect = txfmParams.TxModeSearchType == TX_MODE_SELECT && BlockSignalsTxsize(mbmi.Bsize);
        int txSizeRate = 0;
        if (txSelect)
        {
            int ctx = TxfmPartitionContext(xd.AboveTxfmContext[xd.AboveTxfmContextOffset], xd.LeftTxfmContextBuffer[xd.LeftTxfmContextOffset],
                mbmi.Bsize, txSize);
            txSizeRate = mc.TxfmPartitionCost[ctx * 2 + 0];
        }
        int skipCtx = SkipTxfmContext(xd);
        int noSkipTxfmRate = mc.SkipTxfmCost[skipCtx * 2 + 0];
        int skipTxfmRate = mc.SkipTxfmCost[skipCtx * 2 + 1];
        long skipTxfmRd = AomRd.RdCost(x.Rdmult, skipTxfmRate, 0);
        long noThisRd = AomRd.RdCost(x.Rdmult, noSkipTxfmRate + txSizeRate, 0);
        mbmi.TxSize = txSize;

        int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize];
        int step = txwUnit * txhUnit;
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, bs, 0), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, bs, 0);
        bool exitEarly = false, incompleteExit = false;
        long currentRd = Math.Min(noThisRd, skipTxfmRd);
        AomRdStats acc = default;
        acc.Init();
        Span<byte> tAbove = stackalloc byte[32], tLeft = stackalloc byte[32];
        GetEntropyContexts(bs, xd.Plane[0], tAbove, tLeft);
        int i = 0;
        for (int blkRow = 0; blkRow < maxBlocksHigh && !incompleteExit; blkRow += txhUnit)
        {
            for (int blkCol = 0; blkCol < maxBlocksWide; blkCol += txwUnit)
            {
                AomRdStats thisRd = default;
                thisRd.Init();
                if (exitEarly) { incompleteExit = true; break; }
                var a = tAbove.Slice(blkCol);
                var l = tLeft.Slice(blkRow);
                var txbCtx = AomTxb.TxbCtx(bs, txSize, 0, a, l);
                // av1_setup_xform (DCT_DCT) / av1_setup_quant(tx_size, 0, AomXformQuant.B, 0)
                var qp = AomEncodeMb.SetupQuant(txSize, false, AomXformQuant.B, 0);
                AomEncodeMb.Xform(x, 0, i, blkRow, blkCol, bs, txSize, DCT_DCT);
                AomEncodeMb.Quant(x, 0, i, txSize, DCT_DCT, qp);
                thisRd.Rate = AomEncodeMb.CostCoeffs(x, 0, i, txSize, DCT_DCT, txbCtx, 0);
                DistBlockTxDomain(x, 0, i, txSize, qp.Qmatrix, DCT_DCT, out thisRd.Dist, out thisRd.Sse);
                long noSkipRd = AomRd.RdCost(x.Rdmult, thisRd.Rate, thisRd.Dist);
                long skipRd = AomRd.RdCost(x.Rdmult, 0, thisRd.Sse);
                thisRd.SkipTxfm &= (byte)(x.Plane[0].Eobs[i] == 0 ? 1 : 0);
                acc.Merge(thisRd);
                currentRd += Math.Min(noSkipRd, skipRd);
                if (currentRd > refBestRd) { exitEarly = true; break; }
                AomEncodeMb.SetTxbContext(x, 0, i, txSize, a, l);
                i += step;
            }
        }
        if (incompleteExit) acc.Invalidate();
        rdStats = acc;
        if (rdStats.Rate == int.MaxValue) return long.MaxValue;
        long rd;
        if (rdStats.SkipTxfm != 0 && isInter) rd = AomRd.RdCost(x.Rdmult, skipTxfmRate, rdStats.Sse);
        else
        {
            rd = AomRd.RdCost(x.Rdmult, rdStats.Rate + noSkipTxfmRate + txSizeRate, rdStats.Dist);
            rdStats.Rate += txSizeRate;
        }
        if (isInter && rdStats.SkipTxfm == 0 && xd.Lossless[mbmi.SegmentId] == 0)
        {
            long tmp = AomRd.RdCost(x.Rdmult, skipTxfmRate, rdStats.Sse);
            if (tmp <= rd)
            {
                rd = tmp;
                rdStats.Rate = 0;
                rdStats.Dist = rdStats.Sse;
                rdStats.SkipTxfm = 1;
            }
        }
        return rd;
    }
}
