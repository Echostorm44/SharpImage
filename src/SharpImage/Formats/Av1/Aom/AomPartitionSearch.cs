using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>PartitionBlkParams.</summary>
internal struct AomPartitionBlkParams
{
    public int MiStep, MiRow, MiCol, MiRowEdge, MiColEdge, Width, MinPartitionSize1d;
    public bool BsizeAtLeast8x8, HasRows, HasCols;
    public int Bsize, Subsize, SplitBsize2;
}

/// <summary>PartitionSearchState.</summary>
internal sealed class AomPartitionSearchState
{
    public const int HORZ = 0, VERT = 1;
    public AomPartitionBlkParams BlkParams;
    public readonly bool[][] SplitPartRectWin = { new bool[2], new bool[2], new bool[2], new bool[2] };
    public AomRdStats ThisRdc, SumRdc;
    public readonly int[] TmpPartitionCost = new int[10];
    public int[] PartitionCost = null!;
    public int PartitionCostOffset;
    public long NoneRd;
    public readonly long[] SplitRd = new long[4];
    public readonly long[][] RectPartRd = { new long[2], new long[2] };
    public readonly int[] IsSplitCtxIsReady = new int[2];
    public readonly int[] IsRectCtxIsReady = new int[2];
    public bool TerminatePartitionSearch;
    public bool PartitionNoneAllowed;
    public readonly bool[] PartitionRectAllowed = new bool[2];
    public bool DoRectangularSplit, DoSquareSplit;
    public readonly bool[] PruneRectPart = new bool[2];
    public int SsX, SsY;
    public int PlCtxIdx;
    public bool FoundBestPartition;

    public int Cost(int partition) => PartitionCost[PartitionCostOffset + partition];

    public void DisableRectPartitions() { DoRectangularSplit = false; PartitionRectAllowed[HORZ] = false; PartitionRectAllowed[VERT] = false; }
    public void DisableSquareSplitPartition() => DoSquareSplit = false;
    public void DisableAllSplits() { DisableSquareSplitPartition(); DisableRectPartitions(); }
    public void SetSquareSplitOnly() { PartitionNoneAllowed = false; DoSquareSplit = true; DisableRectPartitions(); }
    public bool HasRowsAndCols => BlkParams.HasRows && BlkParams.HasCols;
}

internal sealed partial class AomComp
{
    // oxcf.part_cfg
    public bool EnableRectPartitions = true, EnableAbPartitions = true, Enable1To4Partitions = true;
    public int MaxPartitionSizeCfg = 128, MinPartitionSizeCfg = 4;
}

// Port of libaom 3.14.1 av1/encoder/partition_search.c (av1_rd_pick_partition and its none / split / rect / AB / 4-way
// searches) and partition_strategy.c's pruning for intra frames.
internal static partial class AomEncodeFrame
{
    private const int HORZ = 0, VERT = 1, HORZ_A = 0, HORZ_B = 1, VERT_A = 2, VERT_B = 3, HORZ4 = 0, VERT4 = 1;

    /// <summary>av1_rd_stats_subtraction.</summary>
    private static void RdStatsSubtraction(int mult, in AomRdStats left, in AomRdStats right, out AomRdStats result)
    {
        result = default;
        if (left.Rate == int.MaxValue || right.Rate == int.MaxValue || left.Dist == long.MaxValue || right.Dist == long.MaxValue ||
            left.Rdcost == long.MaxValue || right.Rdcost == long.MaxValue)
        {
            result.Invalidate();
            return;
        }
        result.Rate = left.Rate - right.Rate;
        result.Dist = left.Dist - right.Dist;
        result.Rdcost = result.Rate >= 0 ? AomRd.RdCost(mult, result.Rate, result.Dist)
            : result.Dist * (1 << AomRd.RdDivBits) - ((((long)-result.Rate * mult) + 256) >> AomCost.ProbCostShift);   // RDCOST_NEG_R
    }

    /// <summary>av1_active_h_edge / av1_active_v_edge (one-pass: the frame edges).</summary>
    private static bool ActiveHEdge(AomComp cpi, int miRow, int miStep)
    {
        int topEdge = 0, bottomEdge = cpi.Cm.MiRows;
        return (topEdge >= miRow && topEdge < miRow + miStep) || (bottomEdge >= miRow && bottomEdge < miRow + miStep);
    }
    private static bool ActiveVEdge(AomComp cpi, int miCol, int miStep)
    {
        int leftEdge = 0, rightEdge = cpi.Cm.MiCols;
        return (leftEdge >= miCol && leftEdge < miCol + miStep) || (rightEdge >= miCol && rightEdge < miCol + miStep);
    }

    /// <summary>init_partition_search_state_params.</summary>
    private static void InitPartitionSearchStateParams(AomMacroblock x, AomComp cpi, AomPartitionSearchState s, int miRow, int miCol, int bsize)
    {
        var xd = x.E;
        var cm = cpi.Cm;
        ref var bp = ref s.BlkParams;
        bp.MiStep = MiSizeWide[bsize] / 2;
        bp.MiRow = miRow;
        bp.MiCol = miCol;
        bp.MiRowEdge = miRow + bp.MiStep;
        bp.MiColEdge = miCol + bp.MiStep;
        bp.Width = BlockSizeWide[bsize];
        bp.MinPartitionSize1d = BlockSizeWide[x.MinPartitionSize];
        bp.Subsize = PartitionSubsize(bsize, PARTITION_SPLIT);
        bp.SplitBsize2 = bp.Subsize;
        bp.BsizeAtLeast8x8 = bsize >= BLOCK_8X8;
        bp.Bsize = bsize;
        bp.HasRows = bp.MiRowEdge < cm.MiRows;
        bp.HasCols = bp.MiColEdge < cm.MiCols;

        if (cpi.FrameIsIntraOnly && bsize == BLOCK_64X64)
        {
            x.Cnn.QuadTreeIdx = 0;
            x.Cnn.Valid = false;
        }

        s.PlCtxIdx = bp.BsizeAtLeast8x8 ? PartitionPlaneContext(xd, miRow, miCol, bsize) : 0;
        s.PartitionCost = x.ModeCosts.PartitionCost;
        s.PartitionCostOffset = s.PlCtxIdx * 10;

        for (int i = 0; i < 4; i++) { s.SplitPartRectWin[i][HORZ] = true; s.SplitPartRectWin[i][VERT] = true; }
        s.ThisRdc.Init();
        s.NoneRd = 0;
        Array.Clear(s.SplitRd);
        Array.Clear(s.RectPartRd[0]); Array.Clear(s.RectPartRd[1]);
        Array.Clear(s.IsSplitCtxIsReady);
        Array.Clear(s.IsRectCtxIsReady);
        s.SsX = xd.Plane[1].SubsamplingX;
        s.SsY = xd.Plane[1].SubsamplingY;
        s.TerminatePartitionSearch = false;
        s.DoSquareSplit = bp.BsizeAtLeast8x8;
        s.DoRectangularSplit = cpi.EnableRectPartitions && bp.BsizeAtLeast8x8;
        Array.Clear(s.PruneRectPart);
        s.PartitionNoneAllowed = s.HasRowsAndCols;
        s.PartitionRectAllowed[HORZ] = s.DoRectangularSplit && bp.HasCols &&
            AomEncodeMb.PlaneBlockSize(PartitionSubsize(bsize, PARTITION_HORZ), s.SsX, s.SsY) != 255;
        s.PartitionRectAllowed[VERT] = s.DoRectangularSplit && bp.HasRows &&
            AomEncodeMb.PlaneBlockSize(PartitionSubsize(bsize, PARTITION_VERT), s.SsX, s.SsY) != 255;
        s.FoundBestPartition = false;
    }

    /// <summary>set_partition_cost_for_edge_blk (from the frame's initial CDFs, cm->fc).</summary>
    private static void SetPartitionCostForEdgeBlk(AomComp cpi, AomPartitionSearchState s)
    {
        ref var bp = ref s.BlkParams;
        ushort[] partitionCdf = cpi.Cm.Fc.Mode.Partition[(4 - (s.PlCtxIdx >> 2)) * 4 + (s.PlCtxIdx & 3)];
        int maxCost = AomCost.CostSymbol(0);
        for (int i = 0; i < 10; ++i) s.TmpPartitionCost[i] = maxCost;
        Span<ushort> cdf2 = stackalloc ushort[2];
        cdf2.Clear();
        if (bp.HasCols)
        {
            PartitionGatherVertAlike(cdf2, partitionCdf, bp.Bsize);
            ReadOnlySpan<int> botInvMap = stackalloc int[] { PARTITION_HORZ, PARTITION_SPLIT };
            AomCost.CostTokensFromCdf(s.TmpPartitionCost, cdf2, 2, botInvMap);
        }
        else if (bp.HasRows)
        {
            PartitionGatherHorzAlike(cdf2, partitionCdf, bp.Bsize);
            ReadOnlySpan<int> rhsInvMap = stackalloc int[] { PARTITION_VERT, PARTITION_SPLIT };
            AomCost.CostTokensFromCdf(s.TmpPartitionCost, cdf2, 2, rhsInvMap);
        }
        else s.TmpPartitionCost[PARTITION_SPLIT] = 0;
        s.PartitionCost = s.TmpPartitionCost;
        s.PartitionCostOffset = 0;
    }

    // our CDF layout stores 32768 - cumulative probability (libaom's AOM_ICDF values)
    private static int CdfElementProb(ushort[] cdf, int element) => (element > 0 ? cdf[element - 1] : 32768) - cdf[element];

    /// <summary>partition_gather_horz_alike (libaom's AOM_ICDF layout: out[0] = ICDF of the merged probability).</summary>
    private static void PartitionGatherHorzAlike(Span<ushort> outCdf, ushort[] inCdf, int bsize)
    {
        int p = 32768;
        p -= CdfElementProb(inCdf, PARTITION_HORZ);
        p -= CdfElementProb(inCdf, PARTITION_SPLIT);
        p -= CdfElementProb(inCdf, PARTITION_HORZ_A);
        p -= CdfElementProb(inCdf, PARTITION_HORZ_B);
        p -= CdfElementProb(inCdf, PARTITION_VERT_A);
        if (bsize != BLOCK_128X128) p -= CdfElementProb(inCdf, PARTITION_HORZ_4);
        outCdf[0] = (ushort)(32768 - p);   // AOM_ICDF(p)
        outCdf[1] = 0;                     // AOM_ICDF(CDF_PROB_TOP)
    }

    /// <summary>partition_gather_vert_alike.</summary>
    private static void PartitionGatherVertAlike(Span<ushort> outCdf, ushort[] inCdf, int bsize)
    {
        int p = 32768;
        p -= CdfElementProb(inCdf, PARTITION_VERT);
        p -= CdfElementProb(inCdf, PARTITION_SPLIT);
        p -= CdfElementProb(inCdf, PARTITION_HORZ_A);
        p -= CdfElementProb(inCdf, PARTITION_VERT_A);
        p -= CdfElementProb(inCdf, PARTITION_VERT_B);
        if (bsize != BLOCK_128X128) p -= CdfElementProb(inCdf, PARTITION_VERT_4);
        outCdf[0] = (ushort)(32768 - p);
        outCdf[1] = 0;
    }

    /// <summary>reset_part_limitations.</summary>
    private static void ResetPartLimitations(AomComp cpi, AomPartitionSearchState s)
    {
        ref var bp = ref s.BlkParams;
        bool isRectPartAllowed = bp.BsizeAtLeast8x8 && cpi.EnableRectPartitions && bp.Width > bp.MinPartitionSize1d;
        s.DoSquareSplit = bp.BsizeAtLeast8x8 && bp.Width > bp.MinPartitionSize1d;
        s.PartitionNoneAllowed = s.HasRowsAndCols && bp.Width >= bp.MinPartitionSize1d;
        s.PartitionRectAllowed[HORZ] = bp.HasCols && isRectPartAllowed &&
            AomEncodeMb.PlaneBlockSize(PartitionSubsize(bp.Bsize, PARTITION_HORZ), s.SsX, s.SsY) != 255;
        s.PartitionRectAllowed[VERT] = bp.HasRows && isRectPartAllowed &&
            AomEncodeMb.PlaneBlockSize(PartitionSubsize(bp.Bsize, PARTITION_VERT), s.SsX, s.SsY) != 255;
        s.TerminatePartitionSearch = false;
    }

    /// <summary>log_sub_block_var.</summary>
    private static void LogSubBlockVar(AomMacroblock x, int bs, out double varMin, out double varMax)
    {
        var xd = x.E;
        int rightOverflow = xd.MbToRightEdge < 0 ? (-xd.MbToRightEdge) >> 3 : 0;
        int bottomOverflow = xd.MbToBottomEdge < 0 ? (-xd.MbToBottomEdge) >> 3 : 0;
        int bw = 4 * MiSizeWide[bs] - rightOverflow, bh = 4 * MiSizeHigh[bs] - bottomOverflow;
        double minVar4x4 = int.MaxValue, maxVar4x4 = 0.0;
        var src = x.Plane[0].Src;
        for (int i = 0; i < bh; i += 4)
            for (int j = 0; j < bw; j += 4)
            {
                int var = src.Buf16 != null ? (int)AomHbd.Variance(src.Buf16, src.Offset + i * src.Stride + j, src.Stride, null, 0, 0, 0, 4, 4, xd.Bd, out _)
                    : (int)AomIntraModeSearch.VarianceVsZero(src.Buf, src.Offset + i * src.Stride + j, src.Stride, 4, 4, out _);
                minVar4x4 = Math.Min(minVar4x4, var);
                maxVar4x4 = Math.Max(maxVar4x4, var);
            }
        varMin = Math.Log(1 + minVar4x4 / 16.0);
        varMax = Math.Log(1 + maxVar4x4 / 16.0);
    }

    /// <summary>av1_prune_partitions_before_search (intra frames).</summary>
    private static void PrunePartitionsBeforeSearch(AomComp cpi, AomMacroblock x, AomPartitionSearchState s)
    {
        var sf = cpi.Sf;
        ref var bp = ref s.BlkParams;
        int bsize = bp.Bsize;
        if (bsize > sf.part_sf.rect_partition_eval_thresh)
        {
            s.DoRectangularSplit = false;
            s.PartitionRectAllowed[HORZ] = false;
            s.PartitionRectAllowed[VERT] = false;
        }
        if (sf.part_sf.prune_rectangular_split_based_on_qidx == 1)
        {
            if (bsize == BLOCK_8X8 && x.Qindex < 35) s.DisableRectPartitions();
        }
        else if (sf.part_sf.prune_rectangular_split_based_on_qidx == 2)
        {
            const int sqrBsizeStep = BLOCK_32X32 - BLOCK_16X16;
            int maxBsize = BLOCK_32X32 - (x.Qindex * 3 / 256) * sqrBsizeStep;
            maxBsize = Math.Max(maxBsize, BLOCK_4X4);
            int maxPruneBsize = Math.Min(maxBsize, BLOCK_32X32);
            if (bsize < maxPruneBsize) s.DisableRectPartitions();
        }
        if (sf.part_sf.prune_sub_8x8_partition_level != 0 && bsize == BLOCK_8X8)
        {
            var xd = x.E;
            bool pruneSub8x8 = sf.part_sf.prune_sub_8x8_partition_level == 2 ||
                (xd.LeftAvailable && xd.UpAvailable && (xd.LeftMbmi!.Bsize > BLOCK_8X8 || xd.AboveMbmi!.Bsize > BLOCK_8X8));
            if (pruneSub8x8) s.DisableAllSplits();
        }
        // CNN-based pruning of split or of all non-split partitions in intra frames
        bool tryIntraCnn = cpi.FrameIsIntraOnly && sf.part_sf.intra_cnn_based_part_prune_level != 0 && cpi.Cm.SbSize >= BLOCK_64X64 &&
            bsize <= BLOCK_64X64 && bp.BsizeAtLeast8x8 && IsWholeBlkInFrame(bp, cpi.Cm);
        if (tryIntraCnn) IntraModeCnnPartition(cpi, x, x.Cnn.QuadTreeIdx, sf.part_sf.intra_cnn_based_part_prune_level, s);
        // (simple motion search pruning: inter frames only)
    }

    private static bool IsWholeBlkInFrame(in AomPartitionBlkParams bp, AomCommon cm)
        => bp.MiRow + MiSizeHigh[bp.Bsize] <= cm.MiRows && bp.MiCol + MiSizeWide[bp.Bsize] <= cm.MiCols;

    /// <summary>intra_mode_cnn_partition.</summary>
    private static void IntraModeCnnPartition(AomComp cpi, AomMacroblock x, int quadTreeIdx, int level, AomPartitionSearchState s)
    {
        var src = x.Plane[0].Src;
        int none = s.PartitionNoneAllowed ? 1 : 0, sq = s.DoSquareSplit ? 1 : 0, rect = s.DoRectangularSplit ? 1 : 0;
        Span<int> rectAllowed = stackalloc int[] { s.PartitionRectAllowed[HORZ] ? 1 : 0, s.PartitionRectAllowed[VERT] ? 1 : 0 };
        x.Cnn.QuadTreeIdx = quadTreeIdx;
        // the model reads the 65x65 source from one row above and one column left of the block
        if (src.Buf16 != null)
            AomMl.IntraModeCnnPartition(x.Cnn, src.Buf16.AsSpan(src.Offset - src.Stride - 1), src.Stride, x.E.Bd, x.Qindex, s.BlkParams.Bsize,
                cpi.Cm.Width, cpi.Cm.Height, level, ref none, ref sq, ref rect, rectAllowed);
        else
            AomMl.IntraModeCnnPartition(x.Cnn, src.Buf.AsSpan(src.Offset - src.Stride - 1), src.Stride, x.Qindex, s.BlkParams.Bsize,
                cpi.Cm.Width, cpi.Cm.Height, level, ref none, ref sq, ref rect, rectAllowed);
        s.PartitionNoneAllowed = none != 0;
        s.DoSquareSplit = sq != 0;
        s.DoRectangularSplit = rect != 0;
        s.PartitionRectAllowed[HORZ] = rectAllowed[0] != 0;
        s.PartitionRectAllowed[VERT] = rectAllowed[1] != 0;
    }

    /// <summary>av1_prune_partitions_by_max_min_bsize.</summary>
    private static void PrunePartitionsByMaxMinBsize(AomMacroblock x, AomPartitionSearchState s)
    {
        ref var bp = ref s.BlkParams;
        int max1d = BlockSizeWide[x.MaxPartitionSize], min1d = BlockSizeWide[x.MinPartitionSize];
        int bsize1d = BlockSizeWide[bp.Bsize];
        if (bsize1d > max1d) s.SetSquareSplitOnly();
        else if (bsize1d <= min1d)
        {
            s.DisableRectPartitions();
            if (s.HasRowsAndCols) s.DoSquareSplit = false;
            s.PartitionNoneAllowed = !s.DoSquareSplit;
        }
    }

    /// <summary>rd_try_subblock.</summary>
    private static bool RdTrySubblock(AomComp cpi, AomMacroblock x, bool isLast, int miRow, int miCol, int subsize, AomRdStats bestRdcost,
        ref AomRdStats sumRdc, int partition, AomPickModeContext thisCtx)
    {
        int origMult = x.Rdmult;
        SetupBlockRdmult(cpi, x, miRow, miCol, subsize);
        bestRdcost.CostUpdate(x.Rdmult);
        RdStatsSubtraction(x.Rdmult, bestRdcost, sumRdc, out AomRdStats rdcostRemaining);
        AomRdStats thisRdc = default;
        PickSbModes(cpi, x, miRow, miCol, ref thisRdc, partition, subsize, thisCtx, rdcostRemaining);
        if (thisRdc.Rate == int.MaxValue) sumRdc.Rdcost = long.MaxValue;
        else
        {
            sumRdc.Rate += thisRdc.Rate;
            sumRdc.Dist += thisRdc.Dist;
            sumRdc.CostUpdate(x.Rdmult);
        }
        if (sumRdc.Rdcost >= bestRdcost.Rdcost)
        {
            x.Rdmult = origMult;
            return false;
        }
        if (!isLast)
        {
            UpdateState(cpi, x, thisCtx, miRow, miCol, subsize, DRY_RUN_NORMAL);
            EncodeSuperblock(cpi, x, DRY_RUN_NORMAL, subsize);
        }
        x.Rdmult = origMult;
        return true;
    }

    /// <summary>rd_test_partition3.</summary>
    private static bool RdTestPartition3(AomComp cpi, AomMacroblock x, AomPcTree pcTree, ref AomRdStats bestRdc, out long thisRdcost,
        AomPickModeContext[] ctxs, int miRow, int miCol, int bsize, int partition, ReadOnlySpan<int> abSubsize, ReadOnlySpan<int> abMiPos,
        AomMbModeInfo?[] modeCache)
    {
        var xd = x.E;
        int pl = PartitionPlaneContext(xd, miRow, miCol, bsize);
        AomRdStats sumRdc = default;
        sumRdc.Init();
        sumRdc.Rate = x.ModeCosts.PartitionCost[pl * 10 + partition];
        sumRdc.Rdcost = AomRd.RdCost(x.Rdmult, sumRdc.Rate, 0);
        thisRdcost = 0;
        for (int i = 0; i < 3; i++)
        {
            if (modeCache[i] != null)
            {
                x.UseMbModeCache = true;
                x.MbModeCache = modeCache[i];
            }
            bool ok = RdTrySubblock(cpi, x, i == 2, abMiPos[i * 2], abMiPos[i * 2 + 1], abSubsize[i], bestRdc, ref sumRdc, partition, ctxs[i]);
            x.UseMbModeCache = false;
            x.MbModeCache = null;
            if (!ok) return false;
        }
        sumRdc.CostUpdate(x.Rdmult);
        thisRdcost = sumRdc.Rdcost;
        if (sumRdc.Rdcost >= bestRdc.Rdcost) return false;
        sumRdc.Rdcost = AomRd.RdCost(x.Rdmult, sumRdc.Rate, sumRdc.Dist);
        thisRdcost = sumRdc.Rdcost;
        if (sumRdc.Rdcost >= bestRdc.Rdcost) return false;
        bestRdc = sumRdc;
        pcTree.Partitioning = partition;
        return true;
    }

    private static AomPickModeContext AllocPmc(AomComp cpi, int bsize) => new(bsize, cpi.AllowScreenContentTools);

    /// <summary>rd_pick_rect_partition.</summary>
    private static void RdPickRectPartition(AomComp cpi, AomMacroblock x, AomPickModeContext curCtx, AomPartitionSearchState s,
        ref AomRdStats bestRdc, int idx, int miRow, int miCol, int bsize, int partitionType)
    {
        RdStatsSubtraction(x.Rdmult, bestRdc, s.SumRdc, out AomRdStats bestRemainRdcost);
        PickSbModes(cpi, x, miRow, miCol, ref s.ThisRdc, partitionType, bsize, curCtx, bestRemainRdcost);
        s.ThisRdc.CostUpdate(x.Rdmult);
        if (s.ThisRdc.Rate == int.MaxValue) s.SumRdc.Rdcost = long.MaxValue;
        else
        {
            s.SumRdc.Rate += s.ThisRdc.Rate;
            s.SumRdc.Dist += s.ThisRdc.Dist;
            s.SumRdc.CostUpdate(x.Rdmult);
        }
        int rectPart = partitionType == PARTITION_HORZ ? HORZ : VERT;
        s.RectPartRd[rectPart][idx] = s.ThisRdc.Rdcost;
    }

    /// <summary>rectangular_partition_search.</summary>
    private static void RectangularPartitionSearch(AomComp cpi, AomMacroblock x, AomPcTree pcTree, AomSearchMbContext xCtx, AomPartitionSearchState s,
        ref AomRdStats bestRdc, bool[]? rectPartWinInfo)
    {
        var cm = cpi.Cm;
        var bp = s.BlkParams;
        Span<int> rectPartitionType = stackalloc int[] { PARTITION_HORZ, PARTITION_VERT };
        Span<int> miPosRect = stackalloc int[]
        {
            bp.MiRow, bp.MiCol, bp.MiRowEdge, bp.MiCol,
            bp.MiRow, bp.MiCol, bp.MiRow, bp.MiColEdge,
        };
        Span<bool> isNotEdgeBlock = stackalloc bool[] { bp.HasRows, bp.HasCols };

        for (int i = HORZ; i <= VERT; i++)
        {
            bool activeEdge = i == HORZ ? ActiveHEdge(cpi, miPosRect[i * 4 + 0 + i], bp.MiStep) : ActiveVEdge(cpi, miPosRect[i * 4 + 0 + i], bp.MiStep);
            bool isPartAllowed = !s.TerminatePartitionSearch && s.PartitionRectAllowed[i] && !s.PruneRectPart[i] &&
                (s.DoRectangularSplit || activeEdge);
            if (!isPartAllowed) continue;

            int subPartIdx = 0;
            int partitionType = rectPartitionType[i];
            bp.Subsize = PartitionSubsize(bp.Bsize, partitionType);
            s.SumRdc.Init();
            var ctxArr = i == HORZ ? pcTree.Horizontal : pcTree.Vertical;
            for (int j = 0; j < 2; j++) ctxArr[j] ??= AllocPmc(cpi, bp.Subsize);
            s.SumRdc.Rate = s.Cost(partitionType);
            s.SumRdc.Rdcost = AomRd.RdCost(x.Rdmult, s.SumRdc.Rate, 0);

            RdPickRectPartition(cpi, x, ctxArr[subPartIdx]!, s, ref bestRdc, 0, miPosRect[i * 4 + 0], miPosRect[i * 4 + 1], bp.Subsize, partitionType);

            if (s.SumRdc.Rdcost < bestRdc.Rdcost && isNotEdgeBlock[i])
            {
                var mbmi = ctxArr[subPartIdx]!.Mic;
                if (mbmi.Palette.PaletteSize0 == 0 && mbmi.Palette.PaletteSize1 == 0 && mbmi.UvMode != UV_CFL_PRED) s.IsRectCtxIsReady[i] = 1;
                UpdateState(cpi, x, ctxArr[subPartIdx]!, bp.MiRow, bp.MiCol, bp.Subsize, DRY_RUN_NORMAL);
                EncodeSuperblock(cpi, x, DRY_RUN_NORMAL, bp.Subsize);
                subPartIdx = 1;
                RdPickRectPartition(cpi, x, ctxArr[subPartIdx]!, s, ref bestRdc, 1, miPosRect[i * 4 + 2], miPosRect[i * 4 + 3], bp.Subsize, partitionType);
            }
            if (s.SumRdc.Rdcost < bestRdc.Rdcost)
            {
                s.SumRdc.Rdcost = AomRd.RdCost(x.Rdmult, s.SumRdc.Rate, s.SumRdc.Dist);
                if (s.SumRdc.Rdcost < bestRdc.Rdcost)
                {
                    bestRdc = s.SumRdc;
                    s.FoundBestPartition = true;
                    pcTree.Partitioning = partitionType;
                }
            }
            else if (rectPartWinInfo != null) rectPartWinInfo[i] = false;
            RestoreContext(x, xCtx, bp.MiRow, bp.MiCol, bp.Bsize, cm.NumPlanes);
        }
    }

    /// <summary>evaluate_ab_partition_based_on_split.</summary>
    private static bool EvaluateAbPartitionBasedOnSplit(AomPcTree pcTree, int rectPart, bool[]? rectPartWinInfo, int qindex, int splitIdx1, int splitIdx2)
    {
        int numWin = 0;
        int numWinThresh = Math.Min(3 * (2 * (MAXQ - qindex) / MAXQ), 3);
        bool subPartWin = rectPartWinInfo == null ? pcTree.Partitioning == rectPart
            : rectPart == PARTITION_HORZ ? rectPartWinInfo[HORZ] : rectPartWinInfo[VERT];
        numWin += subPartWin ? 1 : 0;
        numWin += pcTree.Split[splitIdx1] != null ? (pcTree.Split[splitIdx1]!.Partitioning == PARTITION_NONE ? 1 : 0) : 1;
        numWin += pcTree.Split[splitIdx2] != null ? (pcTree.Split[splitIdx2]!.Partitioning == PARTITION_NONE ? 1 : 0) : 1;
        return numWin >= numWinThresh;
    }

    /// <summary>av1_prune_ab_partitions.</summary>
    private static void PruneAbPartitions(AomComp cpi, AomMacroblock x, AomPcTree pcTree, int pbSourceVariance, long bestRdcost,
        bool[]? rectPartWinInfo, bool extPartitionAllowed, AomPartitionSearchState s, Span<bool> abAllowed)
    {
        var sf = cpi.Sf;
        var horzRd = s.RectPartRd[HORZ];
        var vertRd = s.RectPartRd[VERT];
        var splitRd = s.SplitRd;
        bool horzab = extPartitionAllowed && cpi.EnableAbPartitions && s.PartitionRectAllowed[HORZ];
        bool vertab = extPartitionAllowed && cpi.EnableAbPartitions && s.PartitionRectAllowed[VERT];
        int level = sf.part_sf.prune_ext_partition_types_search_level;
        if (level != 0)
        {
            if (level == 1)
            {
                horzab &= pcTree.Partitioning == PARTITION_HORZ || (pcTree.Partitioning == PARTITION_NONE && pbSourceVariance < 32) ||
                          pcTree.Partitioning == PARTITION_SPLIT;
                vertab &= pcTree.Partitioning == PARTITION_VERT || (pcTree.Partitioning == PARTITION_NONE && pbSourceVariance < 32) ||
                          pcTree.Partitioning == PARTITION_SPLIT;
            }
            else
            {
                horzab &= pcTree.Partitioning == PARTITION_HORZ || pcTree.Partitioning == PARTITION_SPLIT;
                vertab &= pcTree.Partitioning == PARTITION_VERT || pcTree.Partitioning == PARTITION_SPLIT;
            }
            for (int i = 0; i < 2; i++) { horzRd[i] = horzRd[i] < long.MaxValue ? horzRd[i] : 0; vertRd[i] = vertRd[i] < long.MaxValue ? vertRd[i] : 0; }
            for (int i = 0; i < 4; i++) splitRd[i] = splitRd[i] < long.MaxValue ? splitRd[i] : 0;
        }
        abAllowed[HORZ_A] = horzab;
        abAllowed[HORZ_B] = horzab;
        if (level != 0)
        {
            long horzARd = horzRd[1] + splitRd[0] + splitRd[1];
            long horzBRd = horzRd[0] + splitRd[2] + splitRd[3];
            int mul = level == 1 ? 14 : 15;
            abAllowed[HORZ_A] &= horzARd / 16 * mul < bestRdcost;
            abAllowed[HORZ_B] &= horzBRd / 16 * mul < bestRdcost;
        }
        abAllowed[VERT_A] = vertab;
        abAllowed[VERT_B] = vertab;
        if (level != 0)
        {
            long vertARd = vertRd[1] + splitRd[0] + splitRd[2];
            long vertBRd = vertRd[0] + splitRd[1] + splitRd[3];
            int mul = level == 1 ? 14 : 15;
            abAllowed[VERT_A] &= vertARd / 16 * mul < bestRdcost;
            abAllowed[VERT_B] &= vertBRd / 16 * mul < bestRdcost;
        }
        if (sf.part_sf.ml_prune_partition != 0 && extPartitionAllowed && cpi.EnableAbPartitions && s.PartitionRectAllowed[HORZ] &&
            s.PartitionRectAllowed[VERT])
        {
            // x->source_variance (libaom notes it may not be this block's; the model was trained with it)
            int varCtx = x.SourceVariance > 0 ? 32 - System.Numerics.BitOperations.LeadingZeroCount(x.SourceVariance) : 0;
            MlPruneAbPartition(cpi, x, pcTree.Partitioning, varCtx, bestRdcost, s, abAllowed);
        }
        if (sf.part_sf.prune_ext_part_using_split_info >= 2)
        {
            if (abAllowed[HORZ_A]) abAllowed[HORZ_A] &= EvaluateAbPartitionBasedOnSplit(pcTree, PARTITION_HORZ, rectPartWinInfo, x.Qindex, 0, 1);
            if (abAllowed[HORZ_B]) abAllowed[HORZ_B] &= EvaluateAbPartitionBasedOnSplit(pcTree, PARTITION_HORZ, rectPartWinInfo, x.Qindex, 2, 3);
            if (abAllowed[VERT_A]) abAllowed[VERT_A] &= EvaluateAbPartitionBasedOnSplit(pcTree, PARTITION_VERT, rectPartWinInfo, x.Qindex, 0, 2);
            if (abAllowed[VERT_B]) abAllowed[VERT_B] &= EvaluateAbPartitionBasedOnSplit(pcTree, PARTITION_VERT, rectPartWinInfo, x.Qindex, 1, 3);
        }
    }

    /// <summary>ml_prune_ab_partition glue: the model's decision (AomMl) applied to ab_partitions_allowed.</summary>
    private static void MlPruneAbPartition(AomComp cpi, AomMacroblock x, int partCtx, int varCtx, long bestRd, AomPartitionSearchState s, Span<bool> abAllowed)
    {
        Span<int> allowed = stackalloc int[4];
        for (int i = 0; i < 4; i++) allowed[i] = abAllowed[i] ? 1 : 0;
        AomMl.PruneAbPartition(s.BlkParams.Bsize, partCtx, varCtx, bestRd, s.RectPartRd[HORZ], s.RectPartRd[VERT], s.SplitRd, allowed);
        for (int i = 0; i < 4; i++) abAllowed[i] = allowed[i] != 0;
    }

    private static AomMbModeInfo? ModeFromCtx(AomPickModeContext? ctx) => ctx != null && ctx.RdStats.Rate < int.MaxValue ? ctx.Mic : null;
    private static AomMbModeInfo? ModeFromTree(AomPcTree? t) => t != null ? ModeFromCtx(t.None) : null;

    /// <summary>ab_partitions_search.</summary>
    private static void AbPartitionsSearch(AomComp cpi, AomMacroblock x, AomSearchMbContext xCtx, AomPcTree pcTree, AomPartitionSearchState s,
        ref AomRdStats bestRdc, bool[]? rectPartWinInfo, int pbSourceVariance, bool extPartitionAllowed)
    {
        var bp = s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        if (s.TerminatePartitionSearch) return;

        Span<bool> abAllowed = stackalloc bool[4];
        abAllowed.Clear();
        PruneAbPartitions(cpi, x, pcTree, pbSourceVariance, bestRdc.Rdcost, rectPartWinInfo, extPartitionAllowed, s, abAllowed);

        Span<int> isCtxReady = stackalloc int[]
        {
            s.IsSplitCtxIsReady[0], s.IsSplitCtxIsReady[1],
            s.IsRectCtxIsReady[HORZ], 0,
            s.IsSplitCtxIsReady[0], 0,
            s.IsRectCtxIsReady[VERT], 0,
        };
        // set_mode_search_ctx: the contexts whose results can be reused
        AomPickModeContext?[] modeSrchCtx0 = { s.IsSplitCtxIsReady[0] != 0 ? pcTree.Split[0]?.None : null, pcTree.Horizontal[0],
            isCtxReady[4] != 0 ? pcTree.Split[0]?.None : null, pcTree.Vertical[0] };
        AomPickModeContext?[] modeSrchCtx1 = { isCtxReady[1] != 0 ? pcTree.Split[1]?.None : null, null, null, null };

        int split2 = bp.SplitBsize2;
        Span<int> abSubsize = stackalloc int[]
        {
            split2, split2, PartitionSubsize(bsize, PARTITION_HORZ_A),
            PartitionSubsize(bsize, PARTITION_HORZ_B), split2, split2,
            split2, split2, PartitionSubsize(bsize, PARTITION_VERT_A),
            PartitionSubsize(bsize, PARTITION_VERT_B), split2, split2,
        };
        Span<int> abMiPos = stackalloc int[]
        {
            miRow, miCol, miRow, bp.MiColEdge, bp.MiRowEdge, miCol,
            miRow, miCol, bp.MiRowEdge, miCol, bp.MiRowEdge, bp.MiColEdge,
            miRow, miCol, bp.MiRowEdge, miCol, miRow, bp.MiColEdge,
            miRow, miCol, miRow, bp.MiColEdge, bp.MiRowEdge, bp.MiColEdge,
        };
        var modeCache = new AomMbModeInfo?[3];

        for (int abPartType = HORZ_A; abPartType <= VERT_B; abPartType++)
        {
            int partType = abPartType + PARTITION_HORZ_A;
            if (!abAllowed[abPartType]) continue;
            var curCtxs = abPartType switch { HORZ_A => pcTree.HorizontalA, HORZ_B => pcTree.HorizontalB, VERT_A => pcTree.VerticalA, _ => pcTree.VerticalB };
            for (int i = 0; i < 3; i++)
            {
                curCtxs[i] = AllocPmc(cpi, abSubsize[abPartType * 3 + i]);
                curCtxs[i]!.RdModeIsReady = 0;
            }
            if (cpi.Sf.part_sf.reuse_prev_rd_results_for_part_ab != 0 && isCtxReady[abPartType * 2] != 0)
            {
                curCtxs[0]!.CopyFrom(modeSrchCtx0[abPartType]!);
                curCtxs[0]!.Mic.Partition = partType;
                curCtxs[0]!.RdModeIsReady = 1;
                if (isCtxReady[abPartType * 2 + 1] != 0)
                {
                    curCtxs[1]!.CopyFrom(modeSrchCtx1[abPartType]!);
                    curCtxs[1]!.Mic.Partition = partType;
                    curCtxs[1]!.RdModeIsReady = 1;
                }
            }
            Array.Clear(modeCache);
            if (cpi.Sf.part_sf.reuse_best_prediction_for_part_ab != 0)
            {
                switch (abPartType)
                {
                    case HORZ_A: modeCache[0] = ModeFromTree(pcTree.Split[0]); modeCache[1] = ModeFromTree(pcTree.Split[1]); modeCache[2] = ModeFromCtx(pcTree.Horizontal[1]); break;
                    case HORZ_B: modeCache[0] = ModeFromCtx(pcTree.Horizontal[0]); modeCache[1] = ModeFromTree(pcTree.Split[2]); modeCache[2] = ModeFromTree(pcTree.Split[3]); break;
                    case VERT_A: modeCache[0] = ModeFromTree(pcTree.Split[0]); modeCache[1] = ModeFromTree(pcTree.Split[2]); modeCache[2] = ModeFromCtx(pcTree.Vertical[1]); break;
                    default: modeCache[0] = ModeFromCtx(pcTree.Vertical[0]); modeCache[1] = ModeFromTree(pcTree.Split[1]); modeCache[2] = ModeFromTree(pcTree.Split[3]); break;
                }
            }
            // rd_pick_ab_part
            bool found = RdTestPartition3(cpi, x, pcTree, ref bestRdc, out _, curCtxs!, miRow, miCol, bsize, partType,
                abSubsize.Slice(abPartType * 3, 3), abMiPos.Slice(abPartType * 6, 6), modeCache);
            s.FoundBestPartition |= found;
            RestoreContext(x, xCtx, miRow, miCol, bsize, cpi.Cm.NumPlanes);
        }
    }

    /// <summary>rd_pick_4partition.</summary>
    private static void RdPick4Partition(AomComp cpi, AomMacroblock x, AomSearchMbContext xCtx, AomPcTree pcTree, AomPickModeContext?[] curPartCtx,
        AomPartitionSearchState s, ref AomRdStats bestRdc, int incStepH, int incStepV, int partitionType)
    {
        var cm = cpi.Cm;
        var bp = s.BlkParams;
        int part4Idx = partitionType != PARTITION_HORZ_4 ? 1 : 0;
        bp.Subsize = PartitionSubsize(bp.Bsize, partitionType);
        // set_4_part_ctx_and_rdcost
        s.SumRdc.Init();
        int subsize = PartitionSubsize(bp.Bsize, partitionType);
        s.SumRdc.Rate = s.Cost(partitionType);
        s.SumRdc.Rdcost = AomRd.RdCost(x.Rdmult, s.SumRdc.Rate, 0);
        for (int i = 0; i < 4; ++i) curPartCtx[i] = AllocPmc(cpi, subsize);

        for (int i = 0; i < 4; ++i)
        {
            int miR = bp.MiRow + i * incStepH, miC = bp.MiCol + i * incStepV;
            if (i > 0 && (part4Idx == 0 ? miR >= cm.MiRows : miC >= cm.MiCols)) break;
            curPartCtx[i]!.RdModeIsReady = 0;
            if (!RdTrySubblock(cpi, x, i == 3, miR, miC, bp.Subsize, bestRdc, ref s.SumRdc, partitionType, curPartCtx[i]!))
            {
                s.SumRdc.Invalidate();
                break;
            }
        }
        s.SumRdc.CostUpdate(x.Rdmult);
        if (s.SumRdc.Rdcost < bestRdc.Rdcost)
        {
            bestRdc = s.SumRdc;
            s.FoundBestPartition = true;
            pcTree.Partitioning = partitionType;
        }
        RestoreContext(x, xCtx, bp.MiRow, bp.MiCol, bp.Bsize, cm.NumPlanes);
    }

    /// <summary>prune_4_way_partition_search (intra frames).</summary>
    private static void Prune4WayPartitionSearch(AomComp cpi, AomMacroblock x, AomPcTree pcTree, AomPartitionSearchState s, ref AomRdStats bestRdc,
        int pbSourceVariance, bool pruneExtPartState, Span<bool> part4Allowed)
    {
        var sf = cpi.Sf;
        var bp = s.BlkParams;
        int bsize = bp.Bsize;
        if (bestRdc.Rdcost == long.MaxValue && cpi.Enable1To4Partitions && bsize != BLOCK_128X128) return;

        int part4BsizeThresh = sf.part_sf.ext_partition_eval_thresh;
        if (sf.part_sf.ext_part_eval_based_on_cur_best != 0 && !x.MustFindValidPartition && pcTree.Partitioning == PARTITION_NONE)
            part4BsizeThresh = BLOCK_128X128;
        bool partition4Allowed = s.DoRectangularSplit && bsize > part4BsizeThresh && s.HasRowsAndCols && !pruneExtPartState;
        if (bp.Width < (bp.MinPartitionSize1d << sf.part_sf.prune_part4_search))
        {
            part4Allowed[HORZ4] = false;
            part4Allowed[VERT4] = false;
            return;
        }
        Span<int> curPart = stackalloc int[] { PARTITION_HORZ_4, PARTITION_VERT_4 };
        partition4Allowed &= cpi.Enable1To4Partitions && bsize != BLOCK_128X128;
        for (int i = HORZ4; i <= VERT4; i++)
            part4Allowed[i] = partition4Allowed && s.PartitionRectAllowed[i] &&
                AomEncodeMb.PlaneBlockSize(PartitionSubsize(bsize, curPart[i]), s.SsX, s.SsY) != 255;
        if (sf.part_sf.prune_ext_partition_types_search_level == 2)
        {
            int p = pcTree.Partitioning;
            part4Allowed[HORZ4] &= p == PARTITION_HORZ || p == PARTITION_HORZ_A || p == PARTITION_HORZ_B || p == PARTITION_SPLIT || p == PARTITION_NONE;
            part4Allowed[VERT4] &= p == PARTITION_VERT || p == PARTITION_VERT_A || p == PARTITION_VERT_B || p == PARTITION_SPLIT || p == PARTITION_NONE;
        }
        if (sf.part_sf.ml_prune_partition != 0 && partition4Allowed && s.PartitionRectAllowed[HORZ] && s.PartitionRectAllowed[VERT])
            MlPrune4Partition(cpi, x, pcTree.Partitioning, bestRdc.Rdcost, s, part4Allowed, pbSourceVariance);
        // prune_4_partition_using_split_info
        {
            int numWinThresh = Math.Min(3 * (MAXQ - x.Qindex) / MAXQ + 1, 3);
            for (int i = HORZ; i <= VERT; i++)
            {
                if (!(sf.part_sf.prune_ext_part_using_split_info != 0 && part4Allowed[i])) continue;
                int numChildRectWin = 0;
                for (int idx = 0; idx < 4; idx++) numChildRectWin += s.SplitPartRectWin[idx][i] ? 1 : 0;
                if (numChildRectWin < numWinThresh) part4Allowed[i] = false;
            }
        }
        // (prune_part4_using_sms: inter frames only)
    }

    /// <summary>av1_ml_prune_4_partition glue (the model and its features are AomMl's).</summary>
    private static void MlPrune4Partition(AomComp cpi, AomMacroblock x, int partCtx, long bestRd, AomPartitionSearchState s, Span<bool> part4Allowed, int pbSourceVariance)
    {
        Span<int> allowed = stackalloc int[] { part4Allowed[0] ? 1 : 0, part4Allowed[1] ? 1 : 0 };
        var bp = s.BlkParams;
        // the sub-block variances are measured on the block's own source (the searches moved x->plane[].src)
        SetupSrcPlanes(cpi, x, bp.MiRow, bp.MiCol, cpi.Cm.NumPlanes, bp.Bsize);
        var src = x.Plane[0].Src;
        if (src.Buf16 != null)
            AomMl.Prune4Partition(src.Buf16.AsSpan(src.Offset), src.Stride, x.E.Bd, bp.Bsize, partCtx, bestRd, s.RectPartRd[HORZ], s.RectPartRd[VERT], s.SplitRd,
                (uint)pbSourceVariance, cpi.Cm.Width, cpi.Cm.Height, cpi.Sf.part_sf.ml_4_partition_search_level_index, allowed);
        else
            AomMl.Prune4Partition(src.Buf.AsSpan(src.Offset), src.Stride, bp.Bsize, partCtx, bestRd, s.RectPartRd[HORZ], s.RectPartRd[VERT], s.SplitRd,
                (uint)pbSourceVariance, cpi.Cm.Width, cpi.Cm.Height, cpi.Sf.part_sf.ml_4_partition_search_level_index, allowed);
        part4Allowed[0] = allowed[0] != 0;
        part4Allowed[1] = allowed[1] != 0;
    }

    private static readonly double[,] SplitPenaltyFactors = { { 1.080, 1.040, 1.020, 1.010, 1.000 }, { 1.100, 1.075, 1.050, 1.025, 1.000 } };

    /// <summary>get_split_partition_penalty.</summary>
    private static double SplitPartitionPenalty(int bsize, int level)
    {
        if (level == 0) return 1.00;
        int sqrBsizeIdx = SqrBsizeIdx(bsize);
        return SplitPenaltyFactors[level - 1, sqrBsizeIdx - 1];
    }

    /// <summary>none_partition_search.</summary>
    private static void NonePartitionSearch(AomComp cpi, AomMacroblock x, AomPcTree pcTree, AomSearchMbContext xCtx, AomPartitionSearchState s,
        ref AomRdStats bestRdc, ref uint pbSourceVariance, ref long noneRd, bool hasNoneRd, ref long partNoneRd)
    {
        var cm = cpi.Cm;
        var bp = s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        if (s.TerminatePartitionSearch || !s.PartitionNoneAllowed) return;

        int ptCost = 0;
        AomRdStats bestRemainRdcost = default;
        bestRemainRdcost.Invalidate();
        // set_none_partition_params
        pcTree.None ??= AllocPmc(cpi, bp.Bsize);
        if (s.PartitionNoneAllowed)
        {
            if (bp.BsizeAtLeast8x8) ptCost = s.Cost(PARTITION_NONE) < int.MaxValue ? s.Cost(PARTITION_NONE) : 0;
            AomRdStats partitionRdcost = default;
            partitionRdcost.Init();
            partitionRdcost.Rate = ptCost;
            partitionRdcost.CostUpdate(x.Rdmult);
            RdStatsSubtraction(x.Rdmult, bestRdc, partitionRdcost, out bestRemainRdcost);
        }

        PickSbModes(cpi, x, miRow, miCol, ref s.ThisRdc, PARTITION_NONE, bsize, pcTree.None, bestRemainRdcost);
        s.ThisRdc.CostUpdate(x.Rdmult);

        pbSourceVariance = x.SourceVariance;
        if (hasNoneRd) noneRd = s.ThisRdc.Rdcost;
        s.NoneRd = s.ThisRdc.Rdcost;
        if (s.ThisRdc.Rate != int.MaxValue)
        {
            if (bp.BsizeAtLeast8x8)
            {
                s.ThisRdc.Rate += ptCost;
                s.ThisRdc.Rdcost = AomRd.RdCost(x.Rdmult, s.ThisRdc.Rate, s.ThisRdc.Dist);
            }
            partNoneRd = s.ThisRdc.Rdcost;
            if (s.ThisRdc.Rdcost < bestRdc.Rdcost)
            {
                bestRdc = s.ThisRdc;
                s.FoundBestPartition = true;
                if (bp.BsizeAtLeast8x8) pcTree.Partitioning = PARTITION_NONE;
                // prune_partitions_after_none: inter-frame only for intra frames (skippable breakout / sms)
            }
            if (cpi.Sf.part_sf.prune_rect_part_using_none_pred_mode)
                PruneRectPartUsingNonePredMode(x.E, s, pcTree.None.Mic.Mode, bsize);
        }
        RestoreContext(x, xCtx, miRow, miCol, bsize, cm.NumPlanes);
    }

    /// <summary>prune_rect_part_using_none_pred_mode.</summary>
    private static void PruneRectPartUsingNonePredMode(AomMacroblockD xd, AomPartitionSearchState s, int mode, int bsize)
    {
        if (mode == DC_PRED || mode == SMOOTH_PRED)
        {
            int curBlkArea = BlockSizeHigh[bsize] * BlockSizeWide[bsize];
            bool larger = (xd.LeftAvailable && BlockSizeHigh[xd.LeftMbmi!.Bsize] * BlockSizeWide[xd.LeftMbmi.Bsize] > curBlkArea) ||
                          (xd.UpAvailable && BlockSizeHigh[xd.AboveMbmi!.Bsize] * BlockSizeWide[xd.AboveMbmi.Bsize] > curBlkArea);
            if (larger) { s.PruneRectPart[HORZ] = true; s.PruneRectPart[VERT] = true; }
        }
        else if (mode == D67_PRED || mode == V_PRED || mode == D113_PRED) s.PruneRectPart[HORZ] = true;
        else if (mode == D157_PRED || mode == H_PRED || mode == D203_PRED) s.PruneRectPart[VERT] = true;
    }

    /// <summary>split_partition_search.</summary>
    private static void SplitPartitionSearch(AomComp cpi, AomMacroblock x, AomPcTree pcTree, AomSearchMbContext xCtx, AomPartitionSearchState s,
        ref AomRdStats bestRdc, ref long partSplitRd)
    {
        var cm = cpi.Cm;
        var bp = s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        AomRdStats sumRdc = s.SumRdc;
        int subsize = PartitionSubsize(bsize, PARTITION_SPLIT);
        if (s.TerminatePartitionSearch || !s.DoSquareSplit) return;

        for (int i = 0; i < 4; ++i)
        {
            pcTree.Split[i] ??= new AomPcTree(subsize);
            pcTree.Split[i]!.Index = i;
        }
        sumRdc.Init();
        sumRdc.Rate = s.Cost(PARTITION_SPLIT);
        sumRdc.Rdcost = AomRd.RdCost(x.Rdmult, sumRdc.Rate, 0);

        int idx;
        for (idx = 0; idx < 4 && sumRdc.Rdcost < bestRdc.Rdcost; ++idx)
        {
            int xIdx = (idx & 1) * bp.MiStep, yIdx = (idx >> 1) * bp.MiStep;
            if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) continue;
            pcTree.Split[idx]!.Index = idx;
            RdStatsSubtraction(x.Rdmult, bestRdc, sumRdc, out AomRdStats bestRemainRdcost);

            int currQuadTreeIdx = 0;
            if (cpi.FrameIsIntraOnly && bsize <= BLOCK_64X64)
            {
                currQuadTreeIdx = x.Cnn.QuadTreeIdx;
                x.Cnn.QuadTreeIdx = 4 * currQuadTreeIdx + idx + 1;
            }
            long splitRd = s.SplitRd[idx];
            bool ok = RdPickPartition(cpi, x, miRow + yIdx, miCol + xIdx, subsize, ref s.ThisRdc, bestRemainRdcost, pcTree.Split[idx]!,
                ref splitRd, true, s.SplitPartRectWin[idx]);
            s.SplitRd[idx] = splitRd;
            if (!ok)
            {
                sumRdc.Invalidate();
                break;
            }
            if (cpi.FrameIsIntraOnly && bsize <= BLOCK_64X64) x.Cnn.QuadTreeIdx = currQuadTreeIdx;

            sumRdc.Rate += s.ThisRdc.Rate;
            sumRdc.Dist += s.ThisRdc.Dist;
            sumRdc.CostUpdate(x.Rdmult);

            if (idx <= 1 && (bsize <= BLOCK_8X8 || pcTree.Split[idx]!.Partitioning == PARTITION_NONE))
            {
                var mbmi = pcTree.Split[idx]!.None!.Mic;
                if (mbmi.Palette.PaletteSize0 == 0 && mbmi.Palette.PaletteSize1 == 0 && mbmi.UvMode != UV_CFL_PRED) s.IsSplitCtxIsReady[idx] = 1;
            }
        }
        bool reachedLastIndex = idx == 4;
        partSplitRd = sumRdc.Rdcost;
        if (reachedLastIndex && sumRdc.Rdcost < bestRdc.Rdcost)
        {
            sumRdc.Rdcost = AomRd.RdCost(x.Rdmult, sumRdc.Rate, sumRdc.Dist);
            double penaltyFactor = SplitPartitionPenalty(bsize, cpi.Sf.part_sf.split_partition_penalty_level);
            long thisRdcost = (long)(sumRdc.Rdcost * penaltyFactor);
            if (thisRdcost < bestRdc.Rdcost)
            {
                bestRdc = sumRdc;
                s.FoundBestPartition = true;
                pcTree.Partitioning = PARTITION_SPLIT;
            }
        }
        else if (cpi.Sf.part_sf.less_rectangular_check_level > 0)
        {
            if (cpi.Sf.part_sf.less_rectangular_check_level == 2 || idx <= 2)
            {
                bool partitionNoneValid = s.NoneRd > 0;
                bool partitionNoneBetter = s.NoneRd < sumRdc.Rdcost;
                s.DoRectangularSplit &= !(partitionNoneValid && partitionNoneBetter);
            }
        }
        if (bsize <= x.MaxPartitionSize || bsize == cm.SbSize) RestoreContext(x, xCtx, miRow, miCol, bsize, cm.NumPlanes);
    }

    /// <summary>should_do_dry_run_encode_for_current_block.</summary>
    private static bool ShouldDoDryRunEncode(int sbSize, int maxPartitionSize, int currBlockIndex, int bsize)
    {
        if (bsize > maxPartitionSize) return false;
        if (currBlockIndex != 3) return true;
        int subSbSize = PartitionSubsize(sbSize, PARTITION_SPLIT);
        return bsize == maxPartitionSize && subSbSize != maxPartitionSize;
    }

    /// <summary>av1_rd_pick_partition (intra frames, single pass).</summary>
    internal static bool RdPickPartition(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, ref AomRdStats rdCost, AomRdStats bestRdc,
        AomPcTree pcTree, ref long noneRd, bool hasNoneRd, bool[]? rectPartWinInfo)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var sf = cpi.Sf;
        var xCtx = new AomSearchMbContext();
        var s = new AomPartitionSearchState();

        InitPartitionSearchStateParams(x, cpi, s, miRow, miCol, bsize);

        if (bestRdc.Rdcost < 0)
        {
            rdCost.Invalidate();
            return s.FoundBestPartition;
        }
        if (bsize == cm.SbSize) x.MustFindValidPartition = false;
        if (hasNoneRd) noneRd = 0;

        if (!s.HasRowsAndCols) SetPartitionCostForEdgeBlk(cpi, s);

        if (bsize > sf.part_sf.use_square_partition_only_threshold)
        {
            s.PartitionRectAllowed[HORZ] &= !s.BlkParams.HasRows;
            s.PartitionRectAllowed[VERT] &= !s.BlkParams.HasCols;
        }

        SetOffsets(cpi, x, miRow, miCol, bsize);

        if (cpi.AllIntra && bsize == cm.SbSize)
        {
            LogSubBlockVar(x, bsize, out double varMin, out double varMax);
            x.IntraSbRdmultModifier = 128;
            if (varMin < 2.0 && varMax > 4.0)
            {
                if (varMax - varMin > 8.0) x.IntraSbRdmultModifier -= 48;
                else x.IntraSbRdmultModifier -= (int)((varMax - varMin) * 6);
            }
        }

        int origRdmult = x.Rdmult;
        SetupBlockRdmult(cpi, x, miRow, miCol, bsize);
        bestRdc.CostUpdate(x.Rdmult);

        xd.AboveTxfmContext = cm.AboveTxfm;
        xd.AboveTxfmContextOffset = miCol;
        xd.LeftTxfmContextOffset = miRow & MAX_MIB_MASK;
        SaveContext(x, xCtx, miRow, miCol, bsize, numPlanes);

        PrunePartitionsBeforeSearch(cpi, x, s);
        PrunePartitionsByMaxMinBsize(x, s);

    BEGIN_PARTITION_SEARCH:
        if (x.MustFindValidPartition)
        {
            ResetPartLimitations(cpi, s);
            PrunePartitionsByMaxMinBsize(x, s);
            if (cpi.FrameIsIntraOnly && bsize == BLOCK_64X64)
            {
                x.Cnn.QuadTreeIdx = 0;
                x.Cnn.Valid = false;
            }
        }
        uint pbSourceVariance = uint.MaxValue;

        if (cpi.AllIntra)
        {
            bool bsizeAtLeast16x16 = bsize >= BLOCK_16X16;
            bool pruneRectPartUsing4x4VarDeviation = sf.part_sf.prune_rect_part_using_4x4_var_deviation && !x.MustFindValidPartition;
            if (bsizeAtLeast16x16 || pruneRectPartUsing4x4VarDeviation)
            {
                LogSubBlockVar(x, bsize, out double varMin, out double varMax);
                if (bsizeAtLeast16x16 && varMin < 0.272 && varMax - varMin > 3.0)
                {
                    s.PartitionNoneAllowed = false;
                    s.TerminatePartitionSearch = false;
                    s.DoSquareSplit = true;
                }
                else if (pruneRectPartUsing4x4VarDeviation && varMax - varMin < 3.0) s.DoRectangularSplit = false;
            }
        }

        long partNoneRd = long.MaxValue;
        NonePartitionSearch(cpi, x, pcTree, xCtx, s, ref bestRdc, ref pbSourceVariance, ref noneRd, hasNoneRd, ref partNoneRd);

        long partSplitRd = long.MaxValue;
        SplitPartitionSearch(cpi, x, pcTree, xCtx, s, ref bestRdc, ref partSplitRd);

        if (sf.part_sf.early_term_after_none_split != 0 && partNoneRd == long.MaxValue && partSplitRd == long.MaxValue &&
            !x.MustFindValidPartition && bsize != cm.SbSize)
            s.TerminatePartitionSearch = true;
        // (skip_non_sq_part_based_on_none >= 2: inter modes only; prune_partitions_after_split: inter frames only)

        RectangularPartitionSearch(cpi, x, pcTree, xCtx, s, ref bestRdc, rectPartWinInfo);

        if (pbSourceVariance == uint.MaxValue)
        {
            SetupSrcPlanes(cpi, x, miRow, miCol, numPlanes, bsize);
            pbSourceVariance = PerpixelVariance(x, bsize, 0);
        }

        bool pruneExtPartState = sf.part_sf.skip_non_sq_part_based_on_none >= 1 && pcTree.None != null && pcTree.None.Skippable != 0 &&
            !x.MustFindValidPartition && bsize >= BLOCK_16X16;

        // allow_ab_partition_search
        bool abPartitionAllowed;
        if (bestRdc.Rdcost == long.MaxValue) abPartitionAllowed = true;
        else
        {
            int abBsizeThresh = sf.part_sf.ext_partition_eval_thresh;
            if (sf.part_sf.ext_part_eval_based_on_cur_best != 0 && !x.MustFindValidPartition &&
                !(pcTree.Partitioning == PARTITION_HORZ || pcTree.Partitioning == PARTITION_VERT))
                abBsizeThresh = BLOCK_128X128;
            abPartitionAllowed = s.DoRectangularSplit && bsize > abBsizeThresh && s.HasRowsAndCols && !pruneExtPartState;
        }

        AbPartitionsSearch(cpi, x, xCtx, pcTree, s, ref bestRdc, rectPartWinInfo, (int)pbSourceVariance, abPartitionAllowed);

        Span<bool> part4Allowed = stackalloc bool[] { true, true };
        Prune4WayPartitionSearch(cpi, x, pcTree, s, ref bestRdc, (int)pbSourceVariance, pruneExtPartState, part4Allowed);

        if (!s.TerminatePartitionSearch && part4Allowed[HORZ4])
            RdPick4Partition(cpi, x, xCtx, pcTree, pcTree.Horizontal4, s, ref bestRdc, MiSizeHigh[s.BlkParams.Bsize] / 4, 0, PARTITION_HORZ_4);
        if (!s.TerminatePartitionSearch && part4Allowed[VERT4] && s.BlkParams.HasCols)
            RdPick4Partition(cpi, x, xCtx, pcTree, pcTree.Vertical4, s, ref bestRdc, 0, MiSizeWide[s.BlkParams.Bsize] / 4, PARTITION_VERT_4);

        if (bsize == cm.SbSize && !s.FoundBestPartition)
        {
            x.MustFindValidPartition = true;
            goto BEGIN_PARTITION_SEARCH;
        }

        rdCost = bestRdc;

        bool pcTreeDealloc = false;
        if (s.FoundBestPartition)
        {
            if (bsize == cm.SbSize)
            {
                x.CbOffset[0] = 0; x.CbOffset[1] = 0;   // set_cb_offsets
                EncodeSb(cpi, x, miRow, miCol, OUTPUT_ENABLED, bsize, pcTree);
                AomPcTree.FreeRecursive(pcTree, false, false);
                pcTreeDealloc = true;
            }
            else if (ShouldDoDryRunEncode(cm.SbSize, x.MaxPartitionSize, pcTree.Index, bsize))
                EncodeSb(cpi, x, miRow, miCol, DRY_RUN_NORMAL, bsize, pcTree);
        }
        if (!pcTreeDealloc) AomPcTree.FreeRecursive(pcTree, true, true);

        x.Rdmult = origRdmult;
        return s.FoundBestPartition;
    }
}
