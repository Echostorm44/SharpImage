using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 partition_search.c's non-RD partition application: av1_nonrd_use_partition with
// pick_sb_modes_nonrd, try_split_partition, try_merge / calc_do_split_flag (no segmentation, content default).
internal static partial class AomEncodeFrame
{
    private const int MiSize64Nonrd = 16;

    /// <summary>get_force_zeromv_skip_flag_for_blk.</summary>
    private static int GetForceZeromvSkipFlagForBlk(AomComp cpi, AomMacroblock x, int bsize)
    {
        if (x.ForceZeromvSkipForSb < 2) return x.ForceZeromvSkipForSb;
        var cm = cpi.Cm;
        if (bsize == cm.SbSize) return 0;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        uint threshY = cpi.Rt!.ZeromvSkipThreshExitPart[bsize];
        uint threshUv = (3 * threshY) >> 2;
        var yv12 = cm.RefBufs[LAST_FRAME]!.Buf;
        var sf = cm.RefScaleFactors[LAST_FRAME];
        for (int plane = 0; plane < numPlanes; ++plane)
        {
            var p = x.Plane[plane];
            var pd = xd.Plane[plane];
            int bs = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            AomBuf2d mb = default;
            AomInterPred.SetupPredPlane(ref mb, xd.Mi0.Bsize, yv12, plane, xd.MiRow, xd.MiCol, sf, pd.SubsamplingX, pd.SubsamplingY);
            uint planeSad = AomVarBasedPart.Sdf(p.Src, mb, BlockSizeWide[bs], BlockSizeHigh[bs], cm.BitDepth);
            if (planeSad >= (plane == 0 ? threshY : threshUv)) return 0;
        }
        return 1;
    }

    /// <summary>pick_sb_modes_nonrd (no segmentation).</summary>
    private static void PickSbModesNonrd(AomComp cpi, AomMacroblock x, int miRow, int miCol, ref AomRdStats rdCost, int bsize,
        AomPickModeContext ctx)
    {
        var cm = cpi.Cm;
        AomRowMt.WaitForTopRightSb(cpi, x, bsize, miRow, miCol);
        if (bsize != cm.SbSize || cpi.Sf.rt_sf.nonrd_check_partition_split == 1) SetOffsets(cpi, x, miRow, miCol, bsize);
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        xd.TxTypeMap = x.TxTypeMapScratch;
        xd.TxTypeMapOffset = 0;
        xd.TxTypeMapStride = MiSizeWide[bsize];
        for (int i = 0; i < numPlanes; ++i)
        {
            x.Plane[i].Eobs = ctx.Eobs[i];
            x.Plane[i].TxbEntropyCtx = ctx.TxbEntropyCtx[i];
        }
        for (int i = 0; i < 2; ++i) xd.Plane[i].ColorIndexMap = ctx.ColorIndexMap[i] ?? x.EmptyColorMap;
        x.ForceZeromvSkipForBlk = cm.FrameIsIntraOnly ? 0 : GetForceZeromvSkipFlagForBlk(cpi, x, bsize);
        if (x.ForceZeromvSkipForBlk == 0 && (x.SourceVariance == uint.MaxValue || bsize < cm.SbSize)) x.SourceVariance = PerpixelVariance(x, bsize, 0);
        int origRdmult = x.Rdmult;
        SetupBlockRdmult(cpi, x, miRow, miCol, bsize);
        x.Errorperbit = AomRd.ErrorPerBit(x.Rdmult);
        if (cm.FrameIsIntraOnly) HybridIntraModeSearch(cpi, x, ref rdCost, bsize, ctx);
        else AomNonrdPickMode.NonrdPickInterModeSb(cpi, x, ref rdCost, bsize, ctx);
        if (cpi.Sf.rt_sf.skip_cdef_sb != 0 && cpi.Rt != null)
        {
            var rt = cpi.Rt;
            bool allowCdefSkipping = rt.FramesSinceKey > 10 && !rt.HighSourceSad && !(x.ColorSensitivity[0] != 0 || x.ColorSensitivity[1] != 0);
            int miRowSb = miRow - miRow % MiSize64Nonrd, miColSb = miCol - miCol % MiSize64Nonrd;
            var miSb = cm.MiGridBase[miRowSb * cm.MiStride + miColSb]!;
            const uint threshSpatialVar = uint.MaxValue;   // (speed 11+ only)
            if (cpi.Sf.rt_sf.skip_cdef_sb >= 2)
                miSb.CdefStrength = (sbyte)(miSb.CdefStrength != 0 && (allowCdefSkipping || x.SourceVariance == 0) ? 1 : 0);
            else
                miSb.CdefStrength = (sbyte)(miSb.CdefStrength != 0 && allowCdefSkipping &&
                                            !(x.SourceVariance < threshSpatialVar && (mbmi.Mode < INTRA_MODES || mbmi.Mode == NEWMV)) ? 1 : 0);
            ctx.Mic.CdefStrength = miSb.CdefStrength;
        }
        x.Rdmult = origRdmult;
        ctx.RdStats.Rate = rdCost.Rate;
        ctx.RdStats.Dist = rdCost.Dist;
        ctx.RdStats.Rdcost = rdCost.Rdcost;
    }

    /// <summary>try_split_partition.</summary>
    private static bool TrySplitPartition(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, int pl, AomPcTree pcTree)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mc = x.ModeCosts;
        int hbs = MiSizeWide[bsize] / 2;
        if (miRow + MiSizeHigh[bsize] >= cm.MiRows || miCol + MiSizeWide[bsize] >= cm.MiCols) return false;
        if (bsize <= BLOCK_8X8 || cm.FrameIsIntraOnly) return false;
        if (x.SourceSadNonrd <= AomRtSb.kLowSad) return false;
        throw new NotImplementedException("try_split_partition (nonrd_check_partition_split)");
    }

    /// <summary>calc_do_split_flag (content default, no cyclic refresh).</summary>
    private static bool CalcDoSplitFlag(AomComp cpi, AomMacroblock x, AomPcTree pcTree, in AomRdStats noneRdc, int miRow, int miCol, int hbs, int bsize,
        int partition)
    {
        var cm = cpi.Cm;
        bool isLargerQindex = cm.BaseQindex > 100;
        var xd = x.E;
        int mergeMode = cpi.Sf.rt_sf.nonrd_check_partition_merge_mode;
        bool doSplit = mergeMode == 3 ? bsize <= BLOCK_32X32 || (isLargerQindex && bsize <= BLOCK_64X64) : true;
        if (mergeMode < 2 || noneRdc.SkipTxfm == 0) return doSplit;
        bool useModelYrdLarge = bsize >= BLOCK_32X32 && cm.BaseQindex != 0 && cm.BitDepth == 8;   // get_model_rd_flag
        if (!useModelYrdLarge || !isLargerQindex) return false;
        if (pcTree.None!.Mic.Mode == NEWMV && bsize == BLOCK_32X32 && doSplit)
        {
            int subsize = PartitionSubsize(bsize, partition);
            double minErr = double.MaxValue, maxErr = 0;
            int i;
            for (i = 0; i < 4; i++)
            {
                int xIdx = (i & 1) * hbs, yIdx = (i >> 1) * hbs;
                if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) break;
                var src = x.Plane[0].Src;
                var dst = xd.Plane[0].Dst;
                int so = src.Offset + yIdx * 4 * src.Stride + xIdx * 4, d0 = dst.Offset + yIdx * 4 * dst.Stride + xIdx * 4;
                int w = BlockSizeWide[subsize], h = BlockSizeHigh[subsize];
                uint var = AomSad.Variance(src.Buf, so, src.Stride, dst.Buf, d0, dst.Stride, w, h, out _);
                double e = Math.Sqrt((double)var / w / h);
                if (e < minErr) minErr = e;
                if (e > maxErr) maxErr = e;
            }
            if (i == 4 && maxErr - minErr <= 1.5) doSplit = false;
        }
        return doSplit;
    }

    /// <summary>try_merge.</summary>
    private static void TryMerge(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, AomPcTree pcTree, int partition, int subsize, int pl)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mc = x.ModeCosts;
        int numPlanes = cm.NumPlanes;
        int hbs = MiSizeWide[bsize] / 2;
        bool doSplit = false;
        var xCtx = new AomSearchMbContext();
        AomRdStats splitRdc = default, noneRdc = default;
        splitRdc.Invalidate();
        noneRdc.Invalidate();
        SaveContext(x, xCtx, miRow, miCol, bsize, numPlanes);
        SetTxfmContextOffsets(cm, xd, miRow, miCol);
        pcTree.Partitioning = PARTITION_NONE;
        var none = AllocOrResetPmc(cpi, ref pcTree.None, bsize);
        PickSbModesNonrd(cpi, x, miRow, miCol, ref noneRdc, bsize, none);
        noneRdc.Rate += mc.PartitionCost[pl * 10 + PARTITION_NONE];
        noneRdc.Rdcost = AomRd.RdCost(x.Rdmult, noneRdc.Rate, noneRdc.Dist);
        RestoreContext(x, xCtx, miRow, miCol, bsize, numPlanes);
        if (cpi.Sf.rt_sf.nonrd_check_partition_merge_mode < 2 || noneRdc.SkipTxfm != 1 || pcTree.None!.Mic.Mode == NEWMV)
        {
            doSplit = CalcDoSplitFlag(cpi, x, pcTree, noneRdc, miRow, miCol, hbs, bsize, partition);
            if (doSplit)
            {
                splitRdc.Init();
                splitRdc.Rate += mc.PartitionCost[pl * 10 + PARTITION_SPLIT];
                for (int i = 0; i < 4; i++)
                {
                    AomRdStats blockRdc = default;
                    blockRdc.Invalidate();
                    int xIdx = (i & 1) * hbs, yIdx = (i >> 1) * hbs;
                    if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) continue;
                    SetTxfmContextOffsets(cm, xd, miRow + yIdx, miCol + xIdx);
                    var sub = AllocOrResetPmc(cpi, ref pcTree.Split[i]!.None, subsize);
                    pcTree.Split[i]!.Partitioning = PARTITION_NONE;
                    PickSbModesNonrd(cpi, x, miRow + yIdx, miCol + xIdx, ref blockRdc, subsize, sub);
                    AomTrace.Out?.Write($"tmsub {miRow + yIdx} {miCol + xIdx} {blockRdc.Rate} {blockRdc.Dist} mode {sub.Mic.Mode} ref {sub.Mic.RefFrame0} skip {blockRdc.SkipTxfm}" + (char)10);
                    splitRdc.Rate += blockRdc.Rate;
                    splitRdc.Dist += blockRdc.Dist;
                    splitRdc.CostUpdate(x.Rdmult);
                    if (noneRdc.Rdcost < splitRdc.Rdcost) break;
                    if (i != 3) EncodeB(cpi, x, miRow + yIdx, miCol + xIdx, 1, subsize, PARTITION_NONE, sub, nonrd: true);
                }
                RestoreContext(x, xCtx, miRow, miCol, bsize, numPlanes);
                splitRdc.Rdcost = AomRd.RdCost(x.Rdmult, splitRdc.Rate, splitRdc.Dist);
            }
        }
        AomTrace.Out?.Write($"tmerge {miRow} {miCol} bs {bsize} none {noneRdc.Rate} {noneRdc.Dist} {noneRdc.Rdcost} skip {noneRdc.SkipTxfm} mode {pcTree.None!.Mic.Mode} split {splitRdc.Rate} {splitRdc.Dist} {splitRdc.Rdcost} dosplit {(doSplit ? 1 : 0)}" + (char)10);
        var mib = cm.MiGridBase[miRow * cm.MiStride + miCol]!;
        if (noneRdc.Rdcost < splitRdc.Rdcost)
        {
            if (doSplit) x.ReuseInterPred = false;
            mib.Bsize = bsize;
            pcTree.Partitioning = PARTITION_NONE;
            EncodeB(cpi, x, miRow, miCol, 0, bsize, partition, pcTree.None!, nonrd: true);
        }
        else
        {
            mib.Bsize = subsize;
            pcTree.Partitioning = PARTITION_SPLIT;
            x.ReuseInterPred = false;
            for (int i = 0; i < 4; i++)
            {
                int xIdx = (i & 1) * hbs, yIdx = (i >> 1) * hbs;
                if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) continue;
                var sub = pcTree.Split[i]!.None ?? AllocOrResetPmc(cpi, ref pcTree.Split[i]!.None, subsize);
                EncodeB(cpi, x, miRow + yIdx, miCol + xIdx, 0, subsize, PARTITION_NONE, sub, nonrd: true);
            }
        }
    }

    /// <summary>av1_is_leaf_split_partition.</summary>
    private static bool IsLeafSplitPartition(AomCommon cm, int miRow, int miCol, int bsize)
    {
        int hbs = MiSizeWide[bsize] / 2;
        int subsize = PartitionSubsize(bsize, PARTITION_SPLIT);
        for (int i = 0; i < 4; i++)
        {
            int xIdx = (i & 1) * hbs, yIdx = (i >> 1) * hbs;
            if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) return false;
            if (AomVarBasedPart.GetPartition(cm, miRow + yIdx, miCol + xIdx, subsize) != PARTITION_NONE && subsize != BLOCK_8X8) return false;
        }
        return true;
    }

    /// <summary>av1_nonrd_use_partition.</summary>
    internal static void NonrdUsePartition(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, AomPcTree pcTree)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mc = x.ModeCosts;
        int bs = MiSizeWide[bsize], hbs = bs / 2;
        int partition = bsize >= BLOCK_8X8 ? AomVarBasedPart.GetPartition(cm, miRow, miCol, bsize) : PARTITION_NONE;
        int subsize = PartitionSubsize(bsize, partition);
        int pl = bsize >= BLOCK_8X8 ? PartitionPlaneContext(xd, miRow, miCol, bsize) : 0;
        AomRdStats dummyCost = default;
        dummyCost.Invalidate();
        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return;
        SetTxfmContextOffsets(cm, xd, miRow, miCol);
        AomRdoptUtils.SetModeEvalParams(cpi, x, DEFAULT_EVAL);
        x.ReuseInterPred = cpi.Sf.rt_sf.reuse_inter_pred_nonrd != 0;
        bool changeNoneToSplit = false;
        if (partition == PARTITION_NONE && cpi.Sf.rt_sf.nonrd_check_partition_split == 1)
        {
            changeNoneToSplit = TrySplitPartition(cpi, x, miRow, miCol, bsize, pl, pcTree);
            if (changeNoneToSplit)
            {
                partition = PARTITION_SPLIT;
                subsize = PartitionSubsize(bsize, partition);
            }
        }
        pcTree.Partitioning = partition;
        switch (partition)
        {
            case PARTITION_NONE:
            {
                var ctx = AllocOrResetPmc(cpi, ref pcTree.None, bsize);
                PickSbModesNonrd(cpi, x, miRow, miCol, ref dummyCost, bsize, ctx);
                EncodeB(cpi, x, miRow, miCol, OUTPUT_ENABLED, bsize, partition, ctx, nonrd: true);
                break;
            }
            case PARTITION_VERT:
            {
                for (int i = 0; i < 2; ++i) AllocOrResetPmc(cpi, ref pcTree.Vertical[i], subsize);
                PickSbModesNonrd(cpi, x, miRow, miCol, ref dummyCost, subsize, pcTree.Vertical[0]!);
                EncodeB(cpi, x, miRow, miCol, OUTPUT_ENABLED, subsize, PARTITION_VERT, pcTree.Vertical[0]!, nonrd: true);
                if (miCol + hbs < cm.MiCols && bsize > BLOCK_8X8)
                {
                    PickSbModesNonrd(cpi, x, miRow, miCol + hbs, ref dummyCost, subsize, pcTree.Vertical[1]!);
                    EncodeB(cpi, x, miRow, miCol + hbs, OUTPUT_ENABLED, subsize, PARTITION_VERT, pcTree.Vertical[1]!, nonrd: true);
                }
                break;
            }
            case PARTITION_HORZ:
            {
                for (int i = 0; i < 2; ++i) AllocOrResetPmc(cpi, ref pcTree.Horizontal[i], subsize);
                PickSbModesNonrd(cpi, x, miRow, miCol, ref dummyCost, subsize, pcTree.Horizontal[0]!);
                EncodeB(cpi, x, miRow, miCol, OUTPUT_ENABLED, subsize, PARTITION_HORZ, pcTree.Horizontal[0]!, nonrd: true);
                if (miRow + hbs < cm.MiRows && bsize > BLOCK_8X8)
                {
                    PickSbModesNonrd(cpi, x, miRow + hbs, miCol, ref dummyCost, subsize, pcTree.Horizontal[1]!);
                    EncodeB(cpi, x, miRow + hbs, miCol, OUTPUT_ENABLED, subsize, PARTITION_HORZ, pcTree.Horizontal[1]!, nonrd: true);
                }
                break;
            }
            case PARTITION_SPLIT:
                for (int i = 0; i < 4; ++i)
                {
                    pcTree.Split[i] ??= new AomPcTree(subsize);
                    pcTree.Split[i]!.Index = i;
                }
                if (cpi.Sf.rt_sf.nonrd_check_partition_merge_mode != 0 && IsLeafSplitPartition(cm, miRow, miCol, bsize) && !cm.FrameIsIntraOnly &&
                    bsize <= BLOCK_64X64)
                    TryMerge(cpi, x, miRow, miCol, bsize, pcTree, partition, subsize, pl);
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int xIdx = (i & 1) * hbs, yIdx = (i >> 1) * hbs;
                        if (miRow + yIdx >= cm.MiRows || miCol + xIdx >= cm.MiCols) continue;
                        NonrdUsePartition(cpi, x, miRow + yIdx, miCol + xIdx, subsize, pcTree.Split[i]!);
                    }
                    if (!changeNoneToSplit && !cm.FrameIsIntraOnly && !cpi.AllowUpdateCdf && cpi.Sf.rt_sf.partition_direct_merging != 0 &&
                        mc.PartitionCost[pl * 10 + PARTITION_NONE] < mc.PartitionCost[pl * 10 + PARTITION_SPLIT] &&
                        miRow + bs <= cm.MiRows && miCol + bs <= cm.MiCols)
                        throw new NotImplementedException("direct_partition_merging (cdf updates off)");
                }
                break;
            default: throw new InvalidOperationException("av1_nonrd_use_partition cannot handle extended partition types");
        }
    }
}
