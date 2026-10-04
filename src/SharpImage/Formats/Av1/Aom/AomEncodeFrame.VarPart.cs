using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 partition_search.c's partition application for VAR_BASED_PARTITION on intra-only frames:
// av1_rd_use_partition (all-intra speed 7: the RD mode search over the variance-based partition) and
// av1_nonrd_use_partition with pick_sb_modes_nonrd / hybrid_intra_mode_search / encode_b_nonrd (speeds 8-9).
internal static partial class AomEncodeFrame
{
    private static void SetTxfmContextOffsets(AomCommon cm, AomMacroblockD xd, int miRow, int miCol)
    {
        xd.AboveTxfmContext = cm.AboveTxfm[xd.TileRow];
        xd.AboveTxfmContextOffset = miCol;
        xd.LeftTxfmContextOffset = miRow & MAX_MIB_MASK;
    }

    /// <summary>av1_rd_use_partition (intra-only frames; adjust_var_based_rd_partitioning is 0 in the all-intra mode).</summary>
    internal static void RdUsePartition(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, out int rate, out long dist,
        bool doRecon, AomPcTree pcTree)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var modeCosts = x.ModeCosts;
        int bs = MiSizeWide[bsize], hbs = bs / 2;
        int pl = bsize >= BLOCK_8X8 ? PartitionPlaneContext(xd, miRow, miCol, bsize) : 0;
        int partition = bsize >= BLOCK_8X8 ? AomVarBasedPart.GetPartition(cm, miRow, miCol, bsize) : PARTITION_NONE;
        int subsize = PartitionSubsize(bsize, partition);
        rate = 0;
        dist = 0;

        pcTree.None ??= new AomPickModeContext(bsize, cpi.AllowScreenContentTools);
        var ctxNone = pcTree.None;

        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return;
        var mib0 = cm.MiGridBase[miRow * cm.MiStride + miCol]!;
        int bsType = mib0.Bsize;

        AomRdStats lastPartRdc = default, noneRdc = default, chosenRdc = default, invalidRdc = default;
        lastPartRdc.Invalidate();
        noneRdc.Invalidate();
        chosenRdc.Invalidate();
        invalidRdc.Invalidate();

        pcTree.Partitioning = partition;

        SetTxfmContextOffsets(cm, xd, miRow, miCol);
        var xCtx = new AomSearchMbContext();
        SaveContext(x, xCtx, miRow, miCol, bsize, numPlanes);

        // Save rdmult before it might be changed, so it can be restored later.
        int origRdmult = x.Rdmult;
        SetupBlockRdmult(cpi, x, miRow, miCol, bsize);

        // (is_adjust_var_based_part_enabled: adjust_var_based_rd_partitioning is 0)

        for (int i = 0; i < 4; ++i)
        {
            pcTree.Split[i] = new AomPcTree(subsize);
            pcTree.Split[i]!.Index = i;
        }
        switch (partition)
        {
            case PARTITION_NONE:
                PickSbModes(cpi, x, miRow, miCol, ref lastPartRdc, PARTITION_NONE, bsize, ctxNone, invalidRdc);
                break;
            case PARTITION_HORZ:
            {
                for (int i = 0; i < 2; ++i) pcTree.Horizontal[i] = new AomPickModeContext(subsize, cpi.AllowScreenContentTools);
                PickSbModes(cpi, x, miRow, miCol, ref lastPartRdc, PARTITION_HORZ, subsize, pcTree.Horizontal[0]!, invalidRdc);
                if (lastPartRdc.Rate != int.MaxValue && bsize >= BLOCK_8X8 && miRow + hbs < cm.MiRows)
                {
                    AomRdStats tmpRdc = default;
                    tmpRdc.Init();
                    UpdateState(cpi, x, pcTree.Horizontal[0]!, miRow, miCol, subsize, DRY_RUN_NORMAL);
                    EncodeSuperblock(cpi, x, DRY_RUN_NORMAL, subsize);
                    PickSbModes(cpi, x, miRow + hbs, miCol, ref tmpRdc, PARTITION_HORZ, subsize, pcTree.Horizontal[1]!, invalidRdc);
                    if (tmpRdc.Rate == int.MaxValue || tmpRdc.Dist == long.MaxValue)
                    {
                        lastPartRdc.Invalidate();
                        break;
                    }
                    lastPartRdc.Rate += tmpRdc.Rate;
                    lastPartRdc.Dist += tmpRdc.Dist;
                    lastPartRdc.Rdcost += tmpRdc.Rdcost;
                }
                break;
            }
            case PARTITION_VERT:
            {
                for (int i = 0; i < 2; ++i) pcTree.Vertical[i] = new AomPickModeContext(subsize, cpi.AllowScreenContentTools);
                PickSbModes(cpi, x, miRow, miCol, ref lastPartRdc, PARTITION_VERT, subsize, pcTree.Vertical[0]!, invalidRdc);
                if (lastPartRdc.Rate != int.MaxValue && bsize >= BLOCK_8X8 && miCol + hbs < cm.MiCols)
                {
                    AomRdStats tmpRdc = default;
                    tmpRdc.Init();
                    UpdateState(cpi, x, pcTree.Vertical[0]!, miRow, miCol, subsize, DRY_RUN_NORMAL);
                    EncodeSuperblock(cpi, x, DRY_RUN_NORMAL, subsize);
                    PickSbModes(cpi, x, miRow, miCol + hbs, ref tmpRdc, PARTITION_VERT, subsize, pcTree.Vertical[bsize > BLOCK_8X8 ? 1 : 0]!,
                        invalidRdc);
                    if (tmpRdc.Rate == int.MaxValue || tmpRdc.Dist == long.MaxValue)
                    {
                        lastPartRdc.Invalidate();
                        break;
                    }
                    lastPartRdc.Rate += tmpRdc.Rate;
                    lastPartRdc.Dist += tmpRdc.Dist;
                    lastPartRdc.Rdcost += tmpRdc.Rdcost;
                }
                break;
            }
            case PARTITION_SPLIT:
                lastPartRdc.Rate = 0;
                lastPartRdc.Dist = 0;
                lastPartRdc.Rdcost = 0;
                for (int i = 0; i < 4; i++)
                {
                    int xIdx = (i & 1) * hbs, yIdx = (i >> 1) * hbs;
                    if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) continue;
                    RdUsePartition(cpi, x, miRow + yIdx, miCol + xIdx, subsize, out int tmpRate, out long tmpDist, i != 3, pcTree.Split[i]!);
                    if (tmpRate == int.MaxValue || tmpDist == long.MaxValue)
                    {
                        lastPartRdc.Invalidate();
                        break;
                    }
                    lastPartRdc.Rate += tmpRate;
                    lastPartRdc.Dist += tmpDist;
                }
                break;
            default: throw new InvalidOperationException("av1_rd_use_partition cannot handle extended partition types");
        }

        if (lastPartRdc.Rate < int.MaxValue)
        {
            lastPartRdc.Rate += modeCosts.PartitionCost[pl * 10 + partition];
            lastPartRdc.Rdcost = AomRd.RdCost(x.Rdmult, lastPartRdc.Rate, lastPartRdc.Dist);
        }

        // If last_part is better set the partitioning to that.
        if (lastPartRdc.Rdcost < chosenRdc.Rdcost)
        {
            mib0.Bsize = bsType;
            if (bsize >= BLOCK_8X8) pcTree.Partitioning = partition;
            chosenRdc = lastPartRdc;
        }
        // (none_rdc is invalid: no PARTITION_NONE trial)

        RestoreContext(x, xCtx, miRow, miCol, bsize, numPlanes);

        if (doRecon)
        {
            if (bsize == cm.SbSize)
            {
                x.CbOffset[0] = 0;
                x.CbOffset[1] = 0;
                EncodeSb(cpi, x, miRow, miCol, OUTPUT_ENABLED, bsize, pcTree);
            }
            else EncodeSb(cpi, x, miRow, miCol, DRY_RUN_NORMAL, bsize, pcTree);
        }

        rate = chosenRdc.Rate;
        dist = chosenRdc.Dist;
        x.Rdmult = origRdmult;
    }

    /// <summary>av1_reset_pmc.</summary>
    private static void ResetPmc(AomPickModeContext ctx)
    {
        Array.Clear(ctx.TxTypeMap, 0, ctx.NumFourByFourBlk);
        ctx.RdStats.Invalidate();
    }

    private static AomPickModeContext AllocOrResetPmc(AomComp cpi, ref AomPickModeContext? ctx, int bsize)
    {
        if (ctx == null) ctx = new AomPickModeContext(bsize, cpi.AllowScreenContentTools);
        else ResetPmc(ctx);
        return ctx;
    }

    /// <summary>hybrid_intra_mode_search: the RD intra search for small high-variance blocks, the non-RD one otherwise.</summary>
    private static void HybridIntraModeSearch(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize, AomPickModeContext ctx)
    {
        bool useRdopt = false;
        int hybridIntraPickmode = cpi.Sf.rt_sf.hybrid_intra_pickmode;
        // Use rd pick for intra mode search based on block size and variance.
        if (hybridIntraPickmode != 0 && bsize < BLOCK_16X16)
        {
            ReadOnlySpan<uint> varThresh = stackalloc uint[] { 0, 101, 201 };
            if (x.SourceVariance >= varThresh[hybridIntraPickmode - 1]) useRdopt = true;
        }
        string tag = useRdopt ? "rd" : "nrd";
        AomTrace.Out?.Write($"hyb {tag} {x.E.MiRow} {x.E.MiCol} bs {bsize} var {x.SourceVariance} rdmult {x.Rdmult} q {x.Qindex}\n");
        if (useRdopt) RdPickIntraModeSb(cpi, x, ref rdCost, bsize, ctx, long.MaxValue);
        else AomNonrdPickMode.NonrdPickIntraMode(cpi, x, ref rdCost, bsize, ctx);
        AomTrace.Out?.Write($"hyb {tag} -> rate {rdCost.Rate} dist {rdCost.Dist} y {x.E.Mi0.Mode} uv {x.E.Mi0.UvMode}\n");
    }

}
