using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 var_based_part.c: av1_choose_var_based_partitioning for key and inter frames (one spatial
// layer, no segmentation / cyclic refresh / ROI, no temporal denoiser, content default).
internal static partial class AomVarBasedPart
{
    private const int QINDEX_LARGE_BLOCK_THR = 100;
    private const int RES_1080P = 1920 * 1080, RES_1440P = 2560 * 1440;

    /// <summary>fn_ptr[bsize].sdf: the SAD (the high bit depth wrappers scale it to 8 bits).</summary>
    internal static uint Sdf(in AomBuf2d a, in AomBuf2d b, int w, int h, int bd)
        => a.Buf16 != null ? AomHbd.Sad(a.Buf16, a.Offset, a.Stride, b.Buf16!, b.Offset, b.Stride, w, h) >> (bd - 8)
            : AomSad.Sad(a.Buf, a.Offset, a.Stride, b.Buf, b.Offset, b.Stride, w, h);

    /// <summary>fn_ptr[bsize].sdf at an offset into b.</summary>
    internal static uint SdfAt(in AomBuf2d a, in AomBuf2d b, int bOff, int w, int h, int bd)
        => a.Buf16 != null ? AomHbd.Sad(a.Buf16, a.Offset, a.Stride, b.Buf16!, bOff, b.Stride, w, h) >> (bd - 8)
            : AomSad.Sad(a.Buf, a.Offset, a.Stride, b.Buf, bOff, b.Stride, w, h);

    /// <summary>av1_choose_var_based_partitioning (the caller has run av1_set_offsets for the superblock).</summary>
    internal static void ChooseVarBasedPartitioning(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var vt = x.VarTree;
        var rt = cpi.Rt;
        Span<int> forceSplit = stackalloc int[85];
        Span<int> maxVar32x32 = stackalloc int[4], minVar32x32 = stackalloc int[4];
        Span<int> avg16x16 = stackalloc int[16], maxvar16x16 = stackalloc int[16], minvar16x16 = stackalloc int[16];
        int minVar64x64 = int.MaxValue, maxVar64x64 = 0;
        Span<uint> uvSad = stackalloc uint[2];
        int noiseLevel = 0;   // kLow
        bool isZeroMotion = true;
        bool scaledRefLast = false;
        bool isKeyFrame = cm.FrameIsIntraOnly;
        bool isSmallSb = cm.SbSize == BLOCK_64X64;
        int num64x64Blocks = isSmallSb ? 1 : 4;
        uint ySad = uint.MaxValue, ySadG = uint.MaxValue, ySadAlt = uint.MaxValue, ySadLast = uint.MaxValue;
        int bsize = isSmallSb ? BLOCK_64X64 : BLOCK_128X128;
        // (skip_encoding_non_reference_slide_change needs a non-reference frame)
        int refFramePartition = LAST_FRAME;
        Span<long> thresholds = stackalloc long[5];
        long[] vbp = rt?.VbpThresholds ?? new long[5];
        for (int i = 0; i < 5; i++) thresholds[i] = vbp[i];
        ulong blkSad = 0;
        if (rt?.SrcSadBlk64 != null)
        {
            int sbSizeByMb = cm.SbSize == BLOCK_128X128 ? cm.MibSize >> 1 : cm.MibSize;
            int sbCols = (cm.MiCols + sbSizeByMb - 1) / sbSizeByMb;
            blkSad = rt.SrcSadBlk64[miCol / sbSizeByMb + miRow / sbSizeByMb * sbCols];
        }
        int sbQindex = Math.Clamp(cpi.DeltaQPresentFlag ? cm.BaseQindex + x.DeltaQindex : cm.BaseQindex, 0, 255);
        SetVbpThresholds(cpi, thresholds, blkSad, sbQindex, x.LowSumdiff, x.SourceSadNonrd, x.SourceSadRd, false, x.LightingChange);

        var src = x.Plane[0].Src;
        forceSplit[0] = PART_EVAL_ALL;
        Array.Clear(x.VarianceLow);
        if (!cm.FrameIsIntraOnly)
        {
            var refBuf = cm.RefBufs[LAST_FRAME];
            if (refBuf == null) isKeyFrame = true;
            else if (refBuf.Buf.CropHeights[0] != cm.Height || refBuf.Buf.CropWidths[0] != cm.Width)
            {
                scaledRefLast = true;
                if (cpi.GetScaledRefFrame(LAST_FRAME) == null) isKeyFrame = true;
            }
        }
        x.SourceVariance = uint.MaxValue;
        if (cpi.Sf.rt_sf.use_nonrd_pick_mode != 0 && x.SourceSadNonrd > AomRtSb.kLowSad)
            x.SourceVariance = AomEncodeFrame.PerpixelVariance(x, cm.SbSize, 0);

        AomBuf2d dst = default;
        bool haveDst = false;
        if (!isKeyFrame)
        {
            SetupPlanes(cpi, x, ref ySad, ref ySadG, ref ySadAlt, ref ySadLast, ref refFramePartition, miRow, miCol, isSmallSb, scaledRefLast);
            var mi = xd.Mi0;
            if (mi.Mv0.AsInt != 0)
            {
                dst = xd.Plane[0].Dst;
                isZeroMotion = false;
            }
            else dst = xd.Plane[0].Pre0;
            haveDst = true;
        }
        uvSad.Clear();
        ChromaCheck(cpi, x, bsize, ySadLast, ySadG, ySadAlt, isKeyFrame, isZeroMotion, uvSad);
        x.ForceZeromvSkipForSb = 0;

        if (!isKeyFrame && cpi.Sf.rt_sf.part_early_exit_zeromv != 0 && rt!.FramesSinceKey > 30 && refFramePartition == LAST_FRAME &&
            xd.Mi0.Mv0.AsInt == 0)
        {
            if (SetForceZeromvSkipForSb(cpi, x, uvSad, miRow, miCol, ySad, bsize)) return;
        }
        if (rt != null && rt.NoiseEstimateEnabled) noiseLevel = rt.NoiseLevel;

        FillVarianceTreeLeaves(cpi, x, vt, forceSplit, avg16x16, maxvar16x16, minvar16x16, thresholds, src, dst, haveDst, isKeyFrame, isSmallSb);

        int avg64x64 = 0;
        for (int blk64Idx = 0; blk64Idx < num64x64Blocks; ++blk64Idx)
        {
            maxVar32x32[blk64Idx] = 0;
            minVar32x32[blk64Idx] = int.MaxValue;
            int blk64ScaleIdx = blk64Idx << 2;
            for (int lvl1Idx = 0; lvl1Idx < 4; lvl1Idx++)
            {
                int lvl1ScaleIdx = (blk64ScaleIdx + lvl1Idx) << 2;
                if (isKeyFrame)
                {
                    for (int lvl2Idx = 0; lvl2Idx < 4; lvl2Idx++)
                    {
                        int i16 = lvl1ScaleIdx + lvl2Idx;
                        var vtemp = vt.V16[i16];
                        for (int lvl3Idx = 0; lvl3Idx < 4; lvl3Idx++) FillTree8(vt, i16 * 4 + lvl3Idx);
                        FillTree(vtemp, vt.V8, i16 * 4);
                        GetVariance(ref vtemp.None);
                        if (vtemp.None.Variance > thresholds[3])
                        {
                            int splitIndex = 21 + lvl1ScaleIdx + lvl2Idx;
                            forceSplit[splitIndex] = cpi.Sf.rt_sf.vbp_prune_16x16_split_using_min_max_sub_blk_var
                                ? PartEvalBasedOnSubBlkVar(vt, i16, thresholds[3])
                                : PART_EVAL_ONLY_SPLIT;
                            forceSplit[5 + blk64ScaleIdx + lvl1Idx] = PART_EVAL_ONLY_SPLIT;
                            forceSplit[blk64Idx + 1] = PART_EVAL_ONLY_SPLIT;
                            forceSplit[0] = PART_EVAL_ONLY_SPLIT;
                        }
                    }
                }
                int i32 = blk64ScaleIdx + lvl1Idx;
                FillTree(vt.V32[i32], vt.V16, i32 * 4);
                ulong frameSadThresh = 20000;
                bool is360pOrSmaller = cm.Width * cm.Height <= RESOLUTION_360P;
                if (forceSplit[5 + blk64ScaleIdx + lvl1Idx] == PART_EVAL_ALL)
                {
                    GetVariance(ref vt.V32[i32].None);
                    int var32x32 = vt.V32[i32].None.Variance;
                    maxVar32x32[blk64Idx] = Math.Max(var32x32, maxVar32x32[blk64Idx]);
                    minVar32x32[blk64Idx] = Math.Min(var32x32, minVar32x32[blk64Idx]);
                    int k = blk64Idx * 4 + lvl1Idx;
                    int maxMinVar16Diff = maxvar16x16[k] - minvar16x16[k];
                    if (var32x32 > thresholds[2] || (!isKeyFrame && var32x32 > (thresholds[2] >> 1) && var32x32 > (avg16x16[k] >> 1)))
                    {
                        forceSplit[5 + blk64ScaleIdx + lvl1Idx] = PART_EVAL_ONLY_SPLIT;
                        forceSplit[blk64Idx + 1] = PART_EVAL_ONLY_SPLIT;
                        forceSplit[0] = PART_EVAL_ONLY_SPLIT;
                    }
                    else if (!isKeyFrame && is360pOrSmaller &&
                             ((maxMinVar16Diff > (thresholds[2] >> 1) && maxvar16x16[k] > thresholds[2]) ||
                              (cpi.Sf.rt_sf.prefer_large_partition_blocks != 0 && x.SourceSadNonrd > AomRtSb.kLowSad &&
                               rt!.FrameSourceSad < frameSadThresh && maxvar16x16[k] > (thresholds[2] >> 4) &&
                               maxvar16x16[k] > (minvar16x16[k] << 2))))
                    {
                        forceSplit[5 + blk64ScaleIdx + lvl1Idx] = PART_EVAL_ONLY_SPLIT;
                        forceSplit[blk64Idx + 1] = PART_EVAL_ONLY_SPLIT;
                        forceSplit[0] = PART_EVAL_ONLY_SPLIT;
                    }
                }
            }
            if (forceSplit[1 + blk64Idx] == PART_EVAL_ALL)
            {
                FillTree(vt.V64[blk64Idx], vt.V32, blk64Idx * 4);
                GetVariance(ref vt.V64[blk64Idx].None);
                int var64x64 = vt.V64[blk64Idx].None.Variance;
                maxVar64x64 = Math.Max(var64x64, maxVar64x64);
                minVar64x64 = Math.Min(var64x64, minVar64x64);
                int maxMinVar32Diff = maxVar32x32[blk64Idx] - minVar32x32[blk64Idx];
                bool checkMaxVar = maxVar32x32[blk64Idx] > thresholds[1] >> 1;
                bool checkNoiseLvl = noiseLevel >= 2 || cpi.Sf.rt_sf.prefer_large_partition_blocks != 0;   // kMedium
                long setThreshold = 3 * (thresholds[1] >> 3);
                if (!isKeyFrame && maxMinVar32Diff > setThreshold && checkMaxVar && checkNoiseLvl)
                {
                    forceSplit[1 + blk64Idx] = PART_EVAL_ONLY_SPLIT;
                    forceSplit[0] = PART_EVAL_ONLY_SPLIT;
                }
                avg64x64 += var64x64;
            }
            if (isSmallSb) forceSplit[0] = PART_EVAL_ONLY_SPLIT;
        }

        if (forceSplit[0] == PART_EVAL_ALL)
        {
            FillTree(vt.V128, vt.V64, 0);
            GetVariance(ref vt.V128.None);
            int setAvg64x64 = (9 * avg64x64) >> 5;
            if (!isKeyFrame && vt.V128.None.Variance > setAvg64x64) forceSplit[0] = PART_EVAL_ONLY_SPLIT;
            if (!isKeyFrame && maxVar64x64 - minVar64x64 > 3 * (thresholds[0] >> 3) && maxVar64x64 > thresholds[0] >> 1)
                forceSplit[0] = PART_EVAL_ONLY_SPLIT;
        }

        if (miCol + 32 > xd.TileMiColEnd || miRow + 32 > xd.TileMiRowEnd ||
            !SetVtPartitioning(cpi, xd, vt.V128, BLOCK_128X128, miRow, miCol, thresholds[0], BLOCK_16X16, forceSplit[0]))
        {
            for (int blk64Idx = 0; blk64Idx < num64x64Blocks; ++blk64Idx)
            {
                int x64Idx = BlkIdxX(blk64Idx, 4), y64Idx = BlkIdxY(blk64Idx, 4);
                int blk64ScaleIdx = blk64Idx << 2;
                if (SetVtPartitioning(cpi, xd, vt.V64[blk64Idx], BLOCK_64X64, miRow + y64Idx, miCol + x64Idx, thresholds[1], BLOCK_16X16,
                        forceSplit[1 + blk64Idx]))
                    continue;
                for (int lvl1Idx = 0; lvl1Idx < 4; ++lvl1Idx)
                {
                    int x32Idx = BlkIdxX(lvl1Idx, 3), y32Idx = BlkIdxY(lvl1Idx, 3);
                    int lvl1ScaleIdx = (blk64ScaleIdx + lvl1Idx) << 2;
                    if (SetVtPartitioning(cpi, xd, vt.V32[blk64ScaleIdx + lvl1Idx], BLOCK_32X32, miRow + y64Idx + y32Idx,
                            miCol + x64Idx + x32Idx, thresholds[2], BLOCK_16X16, forceSplit[5 + blk64ScaleIdx + lvl1Idx]))
                        continue;
                    for (int lvl2Idx = 0; lvl2Idx < 4; ++lvl2Idx)
                    {
                        int x16Idx = BlkIdxX(lvl2Idx, 2), y16Idx = BlkIdxY(lvl2Idx, 2);
                        int splitIndex = 21 + lvl1ScaleIdx + lvl2Idx;
                        if (SetVtPartitioning(cpi, xd, vt.V16[lvl1ScaleIdx + lvl2Idx], BLOCK_16X16, miRow + y64Idx + y32Idx + y16Idx,
                                miCol + x64Idx + x32Idx + x16Idx, thresholds[3], BLOCK_8X8, forceSplit[splitIndex]))
                            continue;
                        for (int lvl3Idx = 0; lvl3Idx < 4; ++lvl3Idx)
                        {
                            int x8Idx = BlkIdxX(lvl3Idx, 1), y8Idx = BlkIdxY(lvl3Idx, 1);
                            SetBlockSize(cm, miRow + y64Idx + y32Idx + y16Idx + y8Idx, miCol + x64Idx + x32Idx + x16Idx + x8Idx, BLOCK_8X8);
                        }
                    }
                }
            }
        }

        if (cpi.Sf.rt_sf.short_circuit_low_temp_var != 0 && refFramePartition == LAST_FRAME)
        {
            if (isSmallSb) SetLowTempVarFlag64x64(cm, x, xd, vt, 0, thresholds, miCol, miRow);
            else SetLowTempVarFlag128x128(cm, x, xd, vt, thresholds, miCol, miRow);
        }
    }

    /// <summary>set_vt_partitioning.</summary>
    private static bool SetVtPartitioning(AomComp cpi, AomMacroblockD xd, AomVPVariance vt, int bsize, int miRow, int miCol,
        long threshold, int bsizeMin, int forceSplit)
    {
        var cm = cpi.Cm;
        bool intraOnly = cm.FrameIsIntraOnly;
        int blockWidth = MiSizeWide[bsize], blockHeight = MiSizeHigh[bsize];
        int bsWidthCheck = blockWidth, bsHeightCheck = blockHeight;
        int bsWidthVertCheck = blockWidth >> 1, bsHeightHorizCheck = blockHeight >> 1;
        if (cm.SbSize == BLOCK_64X64)
        {
            if (xd.TileMiColEnd == cm.MiCols)
            {
                bsWidthCheck = (blockWidth >> 1) + 1;
                bsWidthVertCheck = (blockWidth >> 2) + 1;
            }
            if (xd.TileMiRowEnd == cm.MiRows)
            {
                bsHeightCheck = (blockHeight >> 1) + 1;
                bsHeightHorizCheck = (blockHeight >> 2) + 1;
            }
        }
        if (miCol + bsWidthCheck <= xd.TileMiColEnd && miRow + bsHeightCheck <= xd.TileMiRowEnd && forceSplit == PART_EVAL_ONLY_NONE)
        {
            SetBlockSize(cm, miRow, miCol, bsize);
            return true;
        }
        if (forceSplit == PART_EVAL_ONLY_SPLIT) return false;
        if (bsize == bsizeMin)
        {
            if (intraOnly) GetVariance(ref vt.None);
            if (miCol + bsWidthCheck <= xd.TileMiColEnd && miRow + bsHeightCheck <= xd.TileMiRowEnd && vt.None.Variance < threshold)
            {
                SetBlockSize(cm, miRow, miCol, bsize);
                return true;
            }
            return false;
        }
        if (bsize > bsizeMin)
        {
            if (intraOnly) GetVariance(ref vt.None);
            if (intraOnly && (bsize > BLOCK_32X32 || vt.None.Variance > (threshold << 4))) return false;
            if (miCol + bsWidthCheck <= xd.TileMiColEnd && miRow + bsHeightCheck <= xd.TileMiRowEnd && vt.None.Variance < threshold)
            {
                SetBlockSize(cm, miRow, miCol, bsize);
                return true;
            }
            if (miRow + bsHeightCheck <= xd.TileMiRowEnd && miCol + bsWidthVertCheck <= xd.TileMiColEnd)
            {
                int subsize = AomEncodeFrame.PartitionSubsize(bsize, PARTITION_VERT);
                int planeBsize = AomEncodeMb.PlaneBlockSize(subsize, xd.Plane[1].SubsamplingX, xd.Plane[1].SubsamplingY);
                GetVariance(ref vt.Vert0);
                GetVariance(ref vt.Vert1);
                if (vt.Vert0.Variance < threshold && vt.Vert1.Variance < threshold && planeBsize < BLOCK_INVALID)
                {
                    SetBlockSize(cm, miRow, miCol, subsize);
                    SetBlockSize(cm, miRow, miCol + blockWidth / 2, subsize);
                    return true;
                }
            }
            if (miCol + bsWidthCheck <= xd.TileMiColEnd && miRow + bsHeightHorizCheck <= xd.TileMiRowEnd)
            {
                int subsize = AomEncodeFrame.PartitionSubsize(bsize, PARTITION_HORZ);
                int planeBsize = AomEncodeMb.PlaneBlockSize(subsize, xd.Plane[1].SubsamplingX, xd.Plane[1].SubsamplingY);
                GetVariance(ref vt.Horz0);
                GetVariance(ref vt.Horz1);
                if (vt.Horz0.Variance < threshold && vt.Horz1.Variance < threshold && planeBsize < BLOCK_INVALID)
                {
                    SetBlockSize(cm, miRow, miCol, subsize);
                    SetBlockSize(cm, miRow + blockHeight / 2, miCol, subsize);
                    return true;
                }
            }
            return false;
        }
        return false;
    }

    private static int Avg8x8(byte[] s, int off, int p)
    {
        int sum = 0;
        for (int i = 0; i < 8; i++, off += p)
            for (int j = 0; j < 8; j++) sum += s[off + j];
        return (sum + 32) >> 6;
    }

    private static int Avg8x8Hbd(ushort[] s, int off, int p)
    {
        int sum = 0;
        for (int i = 0; i < 8; i++, off += p)
            for (int j = 0; j < 8; j++) sum += s[off + j];
        return (sum + 32) >> 6;
    }

    /// <summary>fill_variance_8x8avg (the 8x8 source / prediction means; the quad kernel gives the same).</summary>
    private static void FillVariance8x8Avg(in AomBuf2d src, in AomBuf2d dst, int x16Idx, int y16Idx, AomVarTree vt, int i16,
        int pixelsWide, int pixelsHigh)
    {
        for (int idx = 0; idx < 4; idx++)
        {
            int x8Idx = x16Idx + BlkIdxX(idx, 3), y8Idx = y16Idx + BlkIdxY(idx, 3);
            uint sse = 0;
            int sum = 0;
            if (x8Idx < pixelsWide && y8Idx < pixelsHigh)
            {
                int srcAvg, dstAvg;
                if (src.Buf16 != null)
                {
                    srcAvg = Avg8x8Hbd(src.Buf16, src.Offset + y8Idx * src.Stride + x8Idx, src.Stride);
                    dstAvg = Avg8x8Hbd(dst.Buf16!, dst.Offset + y8Idx * dst.Stride + x8Idx, dst.Stride);
                }
                else
                {
                    srcAvg = Avg8x8(src.Buf, src.Offset + y8Idx * src.Stride + x8Idx, src.Stride);
                    dstAvg = Avg8x8(dst.Buf, dst.Offset + y8Idx * dst.Stride + x8Idx, dst.Stride);
                }
                sum = srcAvg - dstAvg;
                sse = (uint)(sum * sum);
            }
            FillVariance(sse, sum, 0, ref vt.V8[i16 * 4 + idx].None);
        }
    }

    /// <summary>fill_variance_tree_leaves.</summary>
    private static void FillVarianceTreeLeaves(AomComp cpi, AomMacroblock x, AomVarTree vt, Span<int> forceSplit, Span<int> avg16x16,
        Span<int> maxvar16x16, Span<int> minvar16x16, Span<long> thresholds, in AomBuf2d src, in AomBuf2d dst, bool haveDst, bool isKeyFrame,
        bool isSmallSb)
    {
        var xd = x.E;
        int num64x64Blocks = isSmallSb ? 1 : 4;
        int pixelsWide = isSmallSb ? 64 : 128, pixelsHigh = pixelsWide;
        if (xd.MbToRightEdge < 0) pixelsWide += xd.MbToRightEdge >> 3;
        if (xd.MbToBottomEdge < 0) pixelsHigh += xd.MbToBottomEdge >> 3;
        // border_offset_4x4: only for inter frames with the temporal denoiser (the 4x4 leaves are key frame only)
        for (int blk64Idx = 0; blk64Idx < num64x64Blocks; blk64Idx++)
        {
            int x64Idx = BlkIdxX(blk64Idx, 6), y64Idx = BlkIdxY(blk64Idx, 6);
            int blk64ScaleIdx = blk64Idx << 2;
            forceSplit[blk64Idx + 1] = PART_EVAL_ALL;
            for (int lvl1Idx = 0; lvl1Idx < 4; lvl1Idx++)
            {
                int x32Idx = x64Idx + BlkIdxX(lvl1Idx, 5), y32Idx = y64Idx + BlkIdxY(lvl1Idx, 5);
                int lvl1ScaleIdx = (blk64ScaleIdx + lvl1Idx) << 2;
                forceSplit[5 + blk64ScaleIdx + lvl1Idx] = PART_EVAL_ALL;
                int k = blk64Idx * 4 + lvl1Idx;
                avg16x16[k] = 0;
                maxvar16x16[k] = 0;
                minvar16x16[k] = int.MaxValue;
                for (int lvl2Idx = 0; lvl2Idx < 4; lvl2Idx++)
                {
                    int x16Idx = x32Idx + BlkIdxX(lvl2Idx, 4), y16Idx = y32Idx + BlkIdxY(lvl2Idx, 4);
                    int splitIndex = 21 + lvl1ScaleIdx + lvl2Idx;
                    int i16 = lvl1ScaleIdx + lvl2Idx;
                    forceSplit[splitIndex] = PART_EVAL_ALL;
                    if (isKeyFrame)
                    {
                        for (int lvl3Idx = 0; lvl3Idx < 4; lvl3Idx++)
                        {
                            int x8Idx = x16Idx + BlkIdxX(lvl3Idx, 3), y8Idx = y16Idx + BlkIdxY(lvl3Idx, 3);
                            if (src.Buf16 != null)
                                FillVariance4x4AvgHbd(src.Buf16, src.Offset, src.Stride, x8Idx, y8Idx, vt, i16 * 4 + lvl3Idx, pixelsWide, pixelsHigh);
                            else
                                FillVariance4x4Avg(src.Buf, src.Offset, src.Stride, x8Idx, y8Idx, vt, i16 * 4 + lvl3Idx, pixelsWide, pixelsHigh);
                        }
                    }
                    else
                    {
                        FillVariance8x8Avg(src, dst, x16Idx, y16Idx, vt, i16, pixelsWide, pixelsHigh);
                        FillTree(vt.V16[i16], vt.V8, i16 * 4);
                        ref var noneVar = ref vt.V16[i16].None;
                        GetVariance(ref noneVar);
                        int valNoneVar = noneVar.Variance;
                        avg16x16[k] += valNoneVar;
                        minvar16x16[k] = Math.Min(minvar16x16[k], valNoneVar);
                        maxvar16x16[k] = Math.Max(maxvar16x16[k], valNoneVar);
                        if (valNoneVar > thresholds[3])
                        {
                            forceSplit[splitIndex] = PART_EVAL_ONLY_SPLIT;
                            forceSplit[5 + blk64ScaleIdx + lvl1Idx] = PART_EVAL_ONLY_SPLIT;
                            forceSplit[blk64Idx + 1] = PART_EVAL_ONLY_SPLIT;
                            forceSplit[0] = PART_EVAL_ONLY_SPLIT;
                        }
                        // (compute_minmax_variance is 0)
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- thresholds

    private static long ScalePartThreshContent(long thresholdBase, int speed, bool nonReferenceFrame, bool isStatic)
    {
        long threshold = thresholdBase;
        if (nonReferenceFrame && !isStatic) threshold = (3 * threshold) >> 1;
        if (speed >= 8) return (5 * threshold) >> 2;
        return threshold;
    }

    /// <summary>tune_thresh_based_on_qindex.</summary>
    private static void TuneThreshBasedOnQindex(AomComp cpi, Span<long> thresholds, ulong blockSad, int currentQindex, int numPixels,
        bool isSegmentIdBoosted, int sourceSadNonrd, bool lightingChange)
    {
        double weight;
        var rtSf = cpi.Sf.rt_sf;
        var rt = cpi.Rt!;
        if (rtSf.prefer_large_partition_blocks >= 3)
        {
            const int win = 20;
            if (currentQindex < QINDEX_LARGE_BLOCK_THR - win) weight = 1.0;
            else if (currentQindex > QINDEX_LARGE_BLOCK_THR + win) weight = 0.0;
            else weight = 1.0 - (currentQindex - QINDEX_LARGE_BLOCK_THR + win) / (2 * win);   // integer division
            if (numPixels > RESOLUTION_480P)
                for (int i = 0; i < 4; i++) thresholds[i] <<= 1;
            if (numPixels <= RESOLUTION_288P)
            {
                thresholds[3] = long.MaxValue;
                if (!isSegmentIdBoosted)
                {
                    thresholds[1] <<= 2;
                    thresholds[2] <<= sourceSadNonrd <= AomRtSb.kLowSad ? 5 : 4;
                }
                else
                {
                    thresholds[1] <<= 1;
                    thresholds[2] <<= 3;
                }
                ulong avgSourceSadThresh = 25000, blockSadLow = 25000, blockSadHigh = 50000;
                if (!isSegmentIdBoosted && rt.AvgSourceSad < avgSourceSadThresh && blockSad > blockSadLow && blockSad < blockSadHigh && !lightingChange)
                {
                    thresholds[2] = (3 * thresholds[2]) >> 2;
                    thresholds[3] = thresholds[2] << 3;
                }
            }
            else if (numPixels > RESOLUTION_480P && !isSegmentIdBoosted && (sourceSadNonrd != AomRtSb.kHighSad || rt.AvgSourceSad > 50000))
            {
                thresholds[0] = (3 * thresholds[0]) >> 1;
                thresholds[3] = long.MaxValue;
                if (currentQindex > QINDEX_LARGE_BLOCK_THR)
                {
                    thresholds[1] = (int)((1 - weight) * (thresholds[1] << 1) + weight * thresholds[1]);
                    thresholds[2] = (int)((1 - weight) * (thresholds[2] << 1) + weight * thresholds[2]);
                }
            }
            else if (currentQindex > QINDEX_LARGE_BLOCK_THR && !isSegmentIdBoosted &&
                     (sourceSadNonrd != AomRtSb.kHighSad || rt.AvgSourceSad > 50000))
            {
                thresholds[1] = (int)((1 - weight) * (thresholds[1] << 2) + weight * thresholds[1]);
                thresholds[2] = (int)((1 - weight) * (thresholds[2] << 4) + weight * thresholds[2]);
                thresholds[3] = long.MaxValue;
            }
        }
        else if (rtSf.prefer_large_partition_blocks >= 2)
        {
            thresholds[1] <<= sourceSadNonrd <= AomRtSb.kLowSad ? 2 : 0;
            thresholds[2] = sourceSadNonrd <= AomRtSb.kLowSad ? 3 * thresholds[2] : thresholds[2];
        }
        else if (rtSf.prefer_large_partition_blocks >= 1)
        {
            int fac = sourceSadNonrd <= AomRtSb.kLowSad ? 2 : 1;
            if (currentQindex < QINDEX_LARGE_BLOCK_THR - 45) weight = 1.0;
            else if (currentQindex > QINDEX_LARGE_BLOCK_THR + 45) weight = 0.0;
            else weight = 1.0 - (currentQindex - QINDEX_LARGE_BLOCK_THR + 45) / (2 * 45);   // integer division
            thresholds[1] = (int)((1 - weight) * (thresholds[1] << 1) + weight * thresholds[1]);
            thresholds[2] = (int)((1 - weight) * (thresholds[2] << 1) + weight * thresholds[2]);
            thresholds[3] = (int)((1 - weight) * (thresholds[3] << fac) + weight * thresholds[3]);
        }
        if (cpi.Sf.part_sf.disable_8x8_part_based_on_qidx != 0 && currentQindex < 128) thresholds[3] = long.MaxValue;
    }

    /// <summary>set_vbp_thresholds_key_frame.</summary>
    private static void SetVbpThresholdsKeyFrame(AomComp cpi, Span<long> thresholds, long thresholdBase, int thresholdLeftShift, int numPixels)
    {
        if (cpi.Sf.rt_sf.force_large_partition_blocks_intra != 0)
        {
            int shiftSteps = thresholdLeftShift - (cpi.AllIntra ? 7 : 8);
            thresholdBase <<= shiftSteps;
        }
        thresholds[0] = thresholdBase;
        thresholds[1] = thresholdBase;
        if (numPixels < RESOLUTION_720P)
        {
            thresholds[2] = thresholdBase / 3;
            thresholds[3] = thresholdBase >> 1;
        }
        else
        {
            int shiftVal = 2;
            if (cpi.Sf.rt_sf.force_large_partition_blocks_intra != 0) shiftVal = cpi.AllIntra ? 1 : 0;
            thresholds[2] = thresholdBase >> shiftVal;
            thresholds[3] = thresholdBase >> shiftVal;
        }
        thresholds[4] = thresholdBase << 2;
    }

    /// <summary>tune_thresh_based_on_resolution.</summary>
    private static void TuneThreshBasedOnResolution(AomComp cpi, Span<long> thresholds, long thresholdBase, int currentQindex, int sourceSadRd,
        int numPixels)
    {
        if (numPixels >= RESOLUTION_720P) thresholds[3] <<= 1;
        if (numPixels <= RESOLUTION_288P)
        {
            int[,] qindexThr = { { 200, 220 }, { 140, 170 }, { 120, 150 }, { 200, 210 }, { 170, 220 } };
            int thIdx = 0;
            int vpq = cpi.Sf.rt_sf.var_part_based_on_qidx;
            if (vpq >= 1) thIdx = sourceSadRd <= AomRtSb.kLowSad ? vpq : 0;
            if (vpq >= 3) thIdx = vpq;
            int qLow = qindexThr[thIdx, 0], qHigh = qindexThr[thIdx, 1];
            if (currentQindex >= qHigh)
            {
                thresholdBase = (5 * thresholdBase) >> 1;
                thresholds[1] = thresholdBase >> 3;
                thresholds[2] = thresholdBase << 2;
                thresholds[3] = thresholdBase << 5;
            }
            else if (currentQindex < qLow)
            {
                thresholds[1] = thresholdBase >> 3;
                thresholds[2] = thresholdBase >> 1;
                thresholds[3] = thresholdBase << 3;
            }
            else
            {
                long qiDiffLow = currentQindex - qLow, qiDiffHigh = qHigh - currentQindex;
                long thresholdDiff = qHigh - qLow;
                long thresholdBaseHigh = (5 * thresholdBase) >> 1;
                thresholdDiff = thresholdDiff > 0 ? thresholdDiff : 1;
                thresholdBase = (qiDiffLow * thresholdBaseHigh + qiDiffHigh * thresholdBase) / thresholdDiff;
                thresholds[1] = thresholdBase >> 3;
                thresholds[2] = (qiDiffLow * thresholdBase + qiDiffHigh * (thresholdBase >> 1)) / thresholdDiff;
                thresholds[3] = (qiDiffLow * (thresholdBase << 5) + qiDiffHigh * (thresholdBase << 3)) / thresholdDiff;
            }
        }
        else if (numPixels < RESOLUTION_720P) thresholds[2] = (5 * thresholdBase) >> 2;
        else if (numPixels < RES_1080P) thresholds[2] = thresholdBase << 1;
        else thresholds[2] = cpi.Speed > 7 ? 6 * thresholdBase : 3 * thresholdBase;
    }

    /// <summary>tune_base_thresh_content.</summary>
    private static long TuneBaseThreshContent(AomComp cpi, long thresholdBase, bool contentLowsumdiff, int sourceSadNonrd, int numPixels)
    {
        var rt = cpi.Rt!;
        long updated = thresholdBase;
        if (rt.NoiseEstimateEnabled && contentLowsumdiff && numPixels > RESOLUTION_480P && rt.FrameNumber > 60)
        {
            if (rt.NoiseLevel == 3) updated = (5 * updated) >> 1;   // kHigh
            else if (rt.NoiseLevel == 2 && cpi.Sf.rt_sf.prefer_large_partition_blocks == 0) updated = (5 * updated) >> 2;   // kMedium
        }
        updated = ScalePartThreshContent(updated, cpi.Speed, false, rt.FrameSourceSad == 0);
        return updated;
    }

    /// <summary>set_vbp_thresholds.</summary>
    internal static void SetVbpThresholds(AomComp cpi, Span<long> thresholds, ulong blkSad, int qindex, bool contentLowsumdiff,
        int sourceSadNonrd, int sourceSadRd, bool isSegmentIdBoosted, bool lightingChange)
    {
        var cm = cpi.Cm;
        bool isKeyFrame = cm.FrameIsIntraOnly;
        int thresholdMultiplier = isKeyFrame ? 120 : 1;
        int bdIdx = cm.BitDepth == 8 ? 0 : cm.BitDepth == 10 ? 1 : 2;
        int acQ = Av1Tables.DequantTable[bdIdx, Math.Clamp(qindex, 0, 255), 1];
        long thresholdBase = (long)thresholdMultiplier * acQ;
        int currentQindex = cm.BaseQindex;
        int thresholdLeftShift = cpi.Sf.rt_sf.var_part_split_threshold_shift;
        int numPixels = cm.Width * cm.Height;
        if (isKeyFrame)
        {
            SetVbpThresholdsKeyFrame(cpi, thresholds, thresholdBase, thresholdLeftShift, numPixels);
            return;
        }
        thresholdBase = TuneBaseThreshContent(cpi, thresholdBase, contentLowsumdiff, sourceSadNonrd, numPixels);
        thresholds[0] = thresholdBase >> 1;
        thresholds[1] = thresholdBase;
        thresholds[3] = thresholdBase << thresholdLeftShift;
        TuneThreshBasedOnResolution(cpi, thresholds, thresholdBase, currentQindex, sourceSadRd, numPixels);
        TuneThreshBasedOnQindex(cpi, thresholds, blkSad, currentQindex, numPixels, isSegmentIdBoosted, sourceSadNonrd, lightingChange);
    }

    /// <summary>av1_set_variance_partition_thresholds (the frame-level thresholds of cpi->vbp_info).</summary>
    internal static void SetVariancePartitionThresholds(AomComp cpi, int qindex)
    {
        if (cpi.Sf.part_sf.partition_search_type != VAR_BASED_PARTITION || cpi.Rt == null) return;
        SetVbpThresholds(cpi, cpi.Rt.VbpThresholds, 0, qindex, false, 0, 0, false, false);
    }

    // ---------------------------------------------------------------- chroma, references, motion

    /// <summary>chroma_check (content default).</summary>
    private static void ChromaCheck(AomComp cpi, AomMacroblock x, int bsize, uint ySad, uint ySadG, uint ySadAlt, bool isKeyFrame,
        bool zeroMotion, Span<uint> uvSad)
    {
        var xd = x.E;
        var cm = cpi.Cm;
        int sourceSadNonrd = x.SourceSadNonrd;
        int shiftUpperLimit = 1, shiftLowerLimit = 3;
        if (isKeyFrame || cm.Monochrome) return;
        int facUv = cm.Width * cm.Height >= RES_1080P ? 3 : 5;
        if (sourceSadNonrd >= AomRtSb.kMedSad && x.SourceVariance > 500 && cm.Width * cm.Height >= 640 * 360)
        {
            shiftUpperLimit = 2;
            shiftLowerLimit = sourceSadNonrd > AomRtSb.kMedSad ? 5 : 4;
        }
        var mi = xd.Mi0;
        var yv12 = cm.RefBufs[LAST_FRAME]?.Buf;
        var yv12G = cm.RefBufs[GOLDEN_FRAME]?.Buf;
        var yv12Alt = cm.RefBufs[ALTREF_FRAME]?.Buf;
        var sf = cm.RefScaleFactors[LAST_FRAME];
        var sfG = cm.RefScaleFactors[GOLDEN_FRAME];
        var sfAlt = cm.RefScaleFactors[ALTREF_FRAME];
        uint uvSadG = 0, uvSadAlt = 0;
        int bd = cm.BitDepth;
        for (int plane = 1; plane < 3; ++plane)
        {
            var p = x.Plane[plane];
            var pd = xd.Plane[plane];
            int bs = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            if (bs != BLOCK_INVALID)
            {
                int bw = BlockSizeWide[bs], bh = BlockSizeHigh[bs];
                AomBuf2d d = default;
                if (zeroMotion)
                {
                    if (mi.RefFrame0 == LAST_FRAME) uvSad[plane - 1] = Sdf(p.Src, pd.Pre0, bw, bh, bd);
                    else
                    {
                        AomInterPred.SetupPredPlane(ref d, mi.Bsize, yv12!, plane, xd.MiRow, xd.MiCol, sf, pd.SubsamplingX, pd.SubsamplingY);
                        uvSad[plane - 1] = Sdf(p.Src, d, bw, bh, bd);
                    }
                }
                else uvSad[plane - 1] = Sdf(p.Src, pd.Dst, bw, bh, bd);
                if (ySadG != uint.MaxValue)
                {
                    AomInterPred.SetupPredPlane(ref d, mi.Bsize, yv12G!, plane, xd.MiRow, xd.MiCol, sfG, pd.SubsamplingX, pd.SubsamplingY);
                    uvSadG = Sdf(p.Src, d, bw, bh, bd);
                }
                if (ySadAlt != uint.MaxValue)
                {
                    AomInterPred.SetupPredPlane(ref d, mi.Bsize, yv12Alt!, plane, xd.MiRow, xd.MiCol, sfAlt, pd.SubsamplingX, pd.SubsamplingY);
                    uvSadAlt = Sdf(p.Src, d, bw, bh, bd);
                }
            }
            if (uvSad[plane - 1] > (ySad >> shiftUpperLimit)) x.ColorSensitivitySb[plane - 1] = 1;
            else if (uvSad[plane - 1] < (ySad >> shiftLowerLimit)) x.ColorSensitivitySb[plane - 1] = 0;
            else x.ColorSensitivitySb[plane - 1] = 2;
            x.ColorSensitivitySbG[plane - 1] = (byte)(uvSadG > ySadG / (uint)facUv ? 1 : 0);
            x.ColorSensitivitySbAlt[plane - 1] = (byte)(uvSadAlt > ySadAlt / (uint)facUv ? 1 : 0);
        }
    }

    /// <summary>set_ref_frame_for_partition.</summary>
    private static void SetRefFrameForPartition(AomComp cpi, AomMacroblock x, AomMacroblockD xd, ref int refFramePartition, AomMbModeInfo mi,
        ref uint ySad, uint ySadG, uint ySadAlt, AomFrameBuffer? yv12G, AomFrameBuffer? yv12Alt, int miRow, int miCol, int numPlanes)
    {
        var cm = cpi.Cm;
        const double fac = 0.9;
        bool setGolden = ySadG < fac * ySad && ySadG < ySadAlt;
        bool setAlt = ySadAlt < fac * ySad && ySadAlt < ySadG;
        if (setGolden)
        {
            AomInterPred.SetupPrePlanes(xd, 0, yv12G!, miRow, miCol, cm.RefScaleFactors[GOLDEN_FRAME]!, numPlanes);
            mi.RefFrame0 = GOLDEN_FRAME;
            mi.Mv0 = default;
            ySad = ySadG;
            refFramePartition = GOLDEN_FRAME;
            x.NonrdPruneRefFrameSearch = 0;
            x.SbMePartition = false;
        }
        else if (setAlt)
        {
            AomInterPred.SetupPrePlanes(xd, 0, yv12Alt!, miRow, miCol, cm.RefScaleFactors[ALTREF_FRAME]!, numPlanes);
            mi.RefFrame0 = ALTREF_FRAME;
            mi.Mv0 = default;
            ySad = ySadAlt;
            refFramePartition = ALTREF_FRAME;
            x.NonrdPruneRefFrameSearch = 0;
            x.SbMePartition = false;
        }
        else
        {
            refFramePartition = LAST_FRAME;
            x.NonrdPruneRefFrameSearch = cpi.Sf.rt_sf.nonrd_prune_ref_frame_search;
        }
    }

    /// <summary>evaluate_neighbour_mvs.</summary>
    private static void EvaluateNeighbourMvs(AomComp cpi, AomMacroblock x, ref uint ySad, bool isSmallSb, int estMotion)
    {
        int sourceSadNonrd = x.SourceSadNonrd;
        if (estMotion > 2 && sourceSadNonrd > AomRtSb.kMedSad) return;
        var xd = x.E;
        int bsize = isSmallSb ? BLOCK_64X64 : BLOCK_128X128;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        var mi = xd.Mi0;
        uint aboveYSad = uint.MaxValue, leftYSad = uint.MaxValue;
        AomMv aboveMv = default, leftMv = default;   // full-pel
        var subpelLimits = AomSubpel.SetSubpelMvSearchRange(x.MvLimits, default);
        var bestMv = mi.Mv0.ToFullMv();
        int multi = estMotion > 2 && sourceSadNonrd > AomRtSb.kLowSad ? 7 : 8;
        var pre = xd.Plane[0].Pre0;
        int bd = cpi.Cm.BitDepth;
        if (xd.UpAvailable)
        {
            var above = xd.AboveMbmi!;
            if (above.Mode >= INTRA_MODE_END && above.RefFrame0 == LAST_FRAME)
            {
                var temp = ClampSubpelMv(above.Mv0, subpelLimits);
                aboveMv = temp.ToFullMv();
                if (MvDistance(bestMv, aboveMv) > 0)
                    aboveYSad = SdfAt(x.Plane[0].Src, pre, pre.Offset + aboveMv.Row * pre.Stride + aboveMv.Col, bw, bh, bd);
            }
        }
        if (xd.LeftAvailable)
        {
            var left = xd.LeftMbmi!;
            if (left.Mode >= INTRA_MODE_END && left.RefFrame0 == LAST_FRAME)
            {
                var temp = ClampSubpelMv(left.Mv0, subpelLimits);
                leftMv = temp.ToFullMv();
                if (MvDistance(bestMv, leftMv) > 0 && MvDistance(aboveMv, leftMv) > 0)
                    leftYSad = SdfAt(x.Plane[0].Src, pre, pre.Offset + leftMv.Row * pre.Stride + leftMv.Col, bw, bh, bd);
            }
        }
        if (aboveYSad < ((multi * ySad) >> 3) && aboveYSad < leftYSad)
        {
            ySad = aboveYSad;
            mi.Mv0 = ClampSubpelMv(aboveMv.ToMv(), subpelLimits);
        }
        if (leftYSad < ((multi * ySad) >> 3) && leftYSad < aboveYSad)
        {
            ySad = leftYSad;
            mi.Mv0 = ClampSubpelMv(leftMv.ToMv(), subpelLimits);
        }
    }

    /// <summary>clamp_mv (to subpel limits).</summary>
    internal static AomMv ClampSubpelMv(AomMv mv, in AomFullMvLimits l)
        => new(Math.Clamp((int)mv.Row, l.RowMin, l.RowMax), Math.Clamp((int)mv.Col, l.ColMin, l.ColMax));

    private static int MvDistance(AomMv a, AomMv b) => Math.Abs(a.Row - b.Row) + Math.Abs(a.Col - b.Col);

    /// <summary>do_int_pro_motion_estimation (content default).</summary>
    private static void DoIntProMotionEstimation(AomComp cpi, AomMacroblock x, ref uint ySad, int miRow, int miCol, int sourceSadNonrd)
    {
        var cm = cpi.Cm;
        var mi = x.E.Mi0;
        bool largeSearch = sourceSadNonrd > AomRtSb.kMedSad && cm.Width * cm.Height > 1280 * 720;
        const int maxSw = 256;
        bool increaseColSw = sourceSadNonrd > AomRtSb.kMedSad;
        int meSearchSizeCol = largeSearch ? (increaseColSw ? maxSw : 96) : BlockSizeWide[cm.SbSize] >> 1;
        int meSearchSizeRow = largeSearch ? (sourceSadNonrd > AomRtSb.kMedSad ? maxSw : 192) : BlockSizeHigh[cm.SbSize] >> 1;
        if (cm.Width * cm.Height >= 3840 * 2160)
        {
            meSearchSizeRow <<= 1;
            meSearchSizeCol <<= 1;
        }
        ySad = IntProMotionEstimation(cpi, x, cm.SbSize, miRow, miCol, default, out uint ySadZero, meSearchSizeCol, meSearchSizeRow, true, largeSearch);
        if (largeSearch)
        {
            uint threshSad = cm.SbSize == BLOCK_128X128 ? 50000u : 20000u;
            if (ySad < (ySadZero >> 1) && ySad < threshSad)
            {
                x.SbMePartition = true;
                x.SbMeMv = mi.Mv0;
                if (Math.Abs((int)mi.Mv0.Col) > 16 && mi.Mv0.Row == 0) x.SbColScroll++;
                else if (Math.Abs((int)mi.Mv0.Row) > 16 && mi.Mv0.Col == 0) x.SbRowScroll++;
            }
            else
            {
                x.SbMePartition = false;
                ySad = ySadZero;
                mi.Mv0 = default;
            }
        }
    }

    /// <summary>setup_planes (one spatial layer).</summary>
    private static void SetupPlanes(AomComp cpi, AomMacroblock x, ref uint ySad, ref uint ySadG, ref uint ySadAlt, ref uint ySadLast,
        ref int refFramePartition, int miRow, int miCol, bool isSmallSb, bool scaledRefLast)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int numPlanes = cm.NumPlanes;
        int bsize = isSmallSb ? BLOCK_64X64 : BLOCK_128X128;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int bd = cm.BitDepth;
        var mi = xd.Mi0;
        var yv12 = scaledRefLast ? cpi.GetScaledRefFrame(LAST_FRAME)! : cm.RefBufs[LAST_FRAME]!.Buf;
        AomFrameBuffer? yv12G = null, yv12Alt = null;
        bool useLastRef = (cpi.RefFrameFlags & AOM_LAST_FLAG) != 0;
        bool useGoldenRef = (cpi.RefFrameFlags & AOM_GOLD_FLAG) != 0;
        var rtSf = cpi.Sf.rt_sf;
        bool useAltRef = rtSf.use_nonrd_altref_frame != 0 || (rtSf.use_comp_ref_nonrd != 0 && rtSf.ref_frame_comp_nonrd[2] == 1);
        if (useGoldenRef && (x.SourceSadNonrd != AomRtSb.kZeroSad || !useLastRef))
        {
            yv12G = cm.RefBufs[GOLDEN_FRAME]?.Buf;
            bool scaledG = false;
            if (yv12G != null && (yv12G.CropHeights[0] != cm.Height || yv12G.CropWidths[0] != cm.Width))
            {
                yv12G = cpi.GetScaledRefFrame(GOLDEN_FRAME);
                scaledG = true;
            }
            if (yv12G != null && (yv12G != yv12 || !useLastRef))
            {
                AomInterPred.SetupPrePlanes(xd, 0, yv12G, miRow, miCol, scaledG ? null! : cm.RefScaleFactors[GOLDEN_FRAME]!, numPlanes);
                ySadG = Sdf(x.Plane[0].Src, xd.Plane[0].Pre0, bw, bh, bd);
            }
        }
        if (useAltRef && (cpi.RefFrameFlags & AOM_ALT_FLAG) != 0 && (x.SourceSadNonrd != AomRtSb.kZeroSad || !useLastRef))
        {
            yv12Alt = cm.RefBufs[ALTREF_FRAME]?.Buf;
            bool scaledAlt = false;
            if (yv12Alt != null && (yv12Alt.CropHeights[0] != cm.Height || yv12Alt.CropWidths[0] != cm.Width))
            {
                yv12Alt = cpi.GetScaledRefFrame(ALTREF_FRAME);
                scaledAlt = true;
            }
            if (yv12Alt != null && (yv12Alt != yv12 || !useLastRef))
            {
                AomInterPred.SetupPrePlanes(xd, 0, yv12Alt, miRow, miCol, scaledAlt ? null! : cm.RefScaleFactors[ALTREF_FRAME]!, numPlanes);
                ySadAlt = Sdf(x.Plane[0].Src, xd.Plane[0].Pre0, bw, bh, bd);
            }
        }
        if (useLastRef)
        {
            int sourceSadNonrd = x.SourceSadNonrd;
            AomInterPred.SetupPrePlanes(xd, 0, yv12, miRow, miCol, scaledRefLast ? null! : cm.RefScaleFactors[LAST_FRAME]!, numPlanes);
            mi.RefFrame0 = LAST_FRAME;
            mi.RefFrame1 = NONE_FRAME;
            mi.Bsize = cm.SbSize;
            mi.Mv0 = default;
            mi.InterpFilters = AomInterpSearch.Broadcast(BILINEAR);
            int estMotion = rtSf.estimate_motion_for_var_based_partition;
            if (estMotion > 2 && sourceSadNonrd > AomRtSb.kMedSad) estMotion = 2;
            if ((estMotion == 1 || estMotion == 2) && xd.MbToRightEdge >= 0 && xd.MbToBottomEdge >= 0 && x.SourceVariance > 100 &&
                sourceSadNonrd > AomRtSb.kLowSad)
                DoIntProMotionEstimation(cpi, x, ref ySad, miRow, miCol, sourceSadNonrd);
            if (ySad == uint.MaxValue) ySad = Sdf(x.Plane[0].Src, xd.Plane[0].Pre0, bw, bh, bd);
            if (estMotion >= 2 && (xd.UpAvailable || xd.LeftAvailable)) EvaluateNeighbourMvs(cpi, x, ref ySad, isSmallSb, estMotion);
            ySadLast = ySad;
        }
        SetRefFrameForPartition(cpi, x, xd, ref refFramePartition, mi, ref ySad, ySadG, ySadAlt, yv12G, yv12Alt, miRow, miCol, numPlanes);
        if (mi.Mv0.AsInt != 0)
        {
            if (!scaledRefLast) AomRdoptInter.SetRefPtrs(cm, xd, mi.RefFrame0, mi.RefFrame1);
            else
            {
                var noScale = AomScaleFactors.ForFrame(cm.Width, cm.Height, cm.Width, cm.Height);
                xd.BlockRefScaleFactors[0] = noScale;
                xd.BlockRefScaleFactors[1] = noScale;
            }
            AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, cm.SbSize, 0, numPlanes - 1, cpi.EnableIntraEdgeFilter);
        }
    }

    /// <summary>av1_int_pro_motion_estimation (content default: no full search).</summary>
    internal static uint IntProMotionEstimation(AomComp cpi, AomMacroblock x, int bsize, int miRow, int miCol, AomMv refMv, out uint ySadZero,
        int meSearchSizeCol, int meSearchSizeRow, bool isVarPart, bool useLargerSearch)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        bool scrollSuperblock = useLargerSearch && bsize == cm.SbSize;
        int border = (cpi.BorderInPixels >> 4) << 4;
        int left = meSearchSizeCol, right = meSearchSizeCol, top = meSearchSizeRow, bottom = meSearchSizeRow;
        if (scrollSuperblock && isVarPart)
        {
            if ((miCol << 2) - left < -border) left = (miCol << 2) + border;
            if ((miCol << 2) + right + bw > cm.Width + border) right = cm.Width + border - (miCol << 2) - bw;
        }
        else if ((miCol << 2) - left < -border || (miCol << 2) + right + bw > cm.Width + border)
        {
            left = Math.Min(border, (miCol << 2) + border);
            right = Math.Min(border, cm.Width + border - (miCol << 2) - bw);
        }
        if (scrollSuperblock && isVarPart)
        {
            if ((miRow << 2) - top < -border) top = (miRow << 2) + border;
            if ((miRow << 2) + bottom + bh > cm.Height + border) bottom = cm.Height + border - (miRow << 2) - bh;
        }
        else if ((miRow << 2) - top < -border || (miRow << 2) + bottom + bh > cm.Height + border)
        {
            top = Math.Min(border, (miRow << 2) + border);
            bottom = Math.Min(border, cm.Height + border - (miRow << 2) - bh);
        }
        left &= ~15;
        right &= ~15;
        top &= ~15;
        bottom &= ~15;
        var src = x.Plane[0].Src;
        AomBuf2d backup0 = default, backup1 = default, backup2 = default;
        var scaledRef = cpi.GetScaledRefFrame(mi.RefFrame0);
        if (scaledRef != null)
        {
            backup0 = xd.Plane[0].Pre0; backup1 = xd.Plane[1].Pre0; backup2 = xd.Plane[2].Pre0;
            AomInterPred.SetupPrePlanes(xd, 0, scaledRef, miRow, miCol, null!, 3);
        }
        var pre = xd.Plane[0].Pre0;
        int refStride = pre.Stride;
        uint bestSad;
        if (xd.Bd != 8)
        {
            mi.Mv0 = default;
            bestSad = Sdf(src, pre, bw, bh, xd.Bd);
            if (scaledRef != null) { xd.Plane[0].Pre0 = backup0; xd.Plane[1].Pre0 = backup1; xd.Plane[2].Pre0 = backup2; }
            ySadZero = 0;
            return bestSad;
        }
        int rowNormFactor = MiSizeHighLog2[bsize] + 1;
        int colNormFactor = 3 + (bw >> 5);
        int widthRefBuf = left + right + bw, heightRefBuf = top + bottom + bh;
        var hbuf = new short[widthRefBuf];
        var vbuf = new short[heightRefBuf];
        var srcHbuf = new short[bw];
        var srcVbuf = new short[bh];
        IntProRow(hbuf, pre.Buf, pre.Offset - left, refStride, widthRefBuf, bh, rowNormFactor);
        IntProCol(vbuf, pre.Buf, pre.Offset - top * refStride, refStride, bw, heightRefBuf, colNormFactor);
        IntProRow(srcHbuf, src.Buf, src.Offset, src.Stride, bw, bh, rowNormFactor);
        IntProCol(srcVbuf, src.Buf, src.Offset, src.Stride, bw, bh, colNormFactor);
        int bestCol = VectorMatch(hbuf, srcHbuf, MiSizeWideLog2[bsize], left, right, false, out int bestSadCol);
        int bestRow = VectorMatch(vbuf, srcVbuf, MiSizeHighLog2[bsize], top, bottom, false, out int bestSadRow);
        var thisMv = new AomMv(bestRow, bestCol);   // full-pel
        var best = thisMv;
        int refOff = pre.Offset + thisMv.Row * refStride + thisMv.Col;
        bestSad = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, refOff, refStride, bw, bh);
        if (scrollSuperblock)
        {
            if (bestSadCol < bestSadRow && bestSadCol < (int)bestSad)
            {
                best = new AomMv(0, best.Col);
                bestSad = (uint)bestSadCol;
            }
            else if (bestSadRow < bestSadCol && bestSadRow < (int)bestSad)
            {
                best = new AomMv(best.Row, 0);
                bestSad = (uint)bestSadRow;
            }
        }
        if (best.AsInt != 0)
        {
            uint tmpSad = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, pre.Offset, refStride, bw, bh);
            ySadZero = tmpSad;
            if (tmpSad < bestSad)
            {
                best = default;
                thisMv = best;
                refOff = pre.Offset;
                bestSad = tmpSad;
            }
        }
        else ySadZero = bestSad;
        if (!scrollSuperblock)
        {
            ReadOnlySpan<int> posRow = stackalloc int[] { -1, 0, 0, 1 };
            ReadOnlySpan<int> posCol = stackalloc int[] { 0, -1, 1, 0 };
            Span<uint> thisSad = stackalloc uint[4];
            thisSad[0] = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, refOff - refStride, refStride, bw, bh);
            thisSad[1] = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, refOff - 1, refStride, bw, bh);
            thisSad[2] = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, refOff + 1, refStride, bw, bh);
            thisSad[3] = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, refOff + refStride, refStride, bw, bh);
            for (int idx = 0; idx < 4; ++idx)
                if (thisSad[idx] < bestSad)
                {
                    bestSad = thisSad[idx];
                    best = new AomMv(posRow[idx] + thisMv.Row, posCol[idx] + thisMv.Col);
                }
            int tr = thisMv.Row + (thisSad[0] < thisSad[3] ? -1 : 1);
            int tc = thisMv.Col + (thisSad[1] < thisSad[2] ? -1 : 1);
            thisMv = new AomMv(tr, tc);
            uint tmpSad = AomSad.Sad(src.Buf, src.Offset, src.Stride, pre.Buf, pre.Offset + tr * refStride + tc, refStride, bw, bh);
            if (bestSad > tmpSad)
            {
                best = thisMv;
                bestSad = tmpSad;
            }
        }
        var limits = x.MvLimits;
        AomMcomp.SetMvSearchRange(ref limits, refMv);
        best = new AomMv(Math.Clamp((int)best.Row, limits.RowMin, limits.RowMax), Math.Clamp((int)best.Col, limits.ColMin, limits.ColMax));
        mi.Mv0 = best.ToMv();
        if (scaledRef != null) { xd.Plane[0].Pre0 = backup0; xd.Plane[1].Pre0 = backup1; xd.Plane[2].Pre0 = backup2; }
        return bestSad;
    }

    /// <summary>aom_int_pro_row: column sums (16-bit) of height rows, normalised.</summary>
    private static void IntProRow(short[] hbuf, byte[] r, int off, int stride, int width, int height, int normFactor)
    {
        for (int idx = 0; idx < width; ++idx)
        {
            short s = 0;
            for (int i = 0; i < height; ++i) s += r[off + idx + i * stride];
            hbuf[idx] = (short)(s >> normFactor);
        }
    }

    /// <summary>aom_int_pro_col: row sums (16-bit), normalised.</summary>
    private static void IntProCol(short[] vbuf, byte[] r, int off, int stride, int width, int height, int normFactor)
    {
        for (int ht = 0; ht < height; ++ht)
        {
            short s = 0;
            for (int idx = 0; idx < width; ++idx) s += r[off + idx];
            vbuf[ht] = (short)(s >> normFactor);
            off += stride;
        }
    }

    /// <summary>aom_vector_var.</summary>
    private static int VectorVar(short[] r, int rOff, short[] s, int bwl)
    {
        int width = 4 << bwl;
        int sse = 0, mean = 0;
        for (int i = 0; i < width; ++i)
        {
            int diff = r[rOff + i] - s[i];
            mean += diff;
            sse += diff * diff;
        }
        uint meanAbs = (uint)Math.Abs(mean);
        return sse - (int)((meanAbs * meanAbs) >> (bwl + 2));
    }

    /// <summary>av1_vector_match.</summary>
    private static int VectorMatch(short[] r, short[] s, int bwl, int searchSizeTop, int searchSizeBottom, bool fullSearch, out int sad)
    {
        int bestSad = int.MaxValue, thisSad, offset = 0, center;
        int bw = searchSizeTop + searchSizeBottom;
        if (fullSearch)
        {
            for (int d = 0; d <= bw; d++)
            {
                thisSad = VectorVar(r, d, s, bwl);
                if (thisSad < bestSad) { bestSad = thisSad; offset = d; }
            }
            sad = bestSad;
            return offset - searchSizeTop;
        }
        for (int d = 0; d <= bw; d += 16)
        {
            thisSad = VectorVar(r, d, s, bwl);
            if (thisSad < bestSad) { bestSad = thisSad; offset = d; }
        }
        center = offset;
        foreach (int step in new[] { 8, 4, 2, 1 })
        {
            for (int d = -step; d <= step; d += 2 * step)
            {
                int thisPos = offset + d;
                if (thisPos < 0 || thisPos > bw) continue;
                thisSad = VectorVar(r, thisPos, s, bwl);
                if (thisSad < bestSad) { bestSad = thisSad; center = thisPos; }
            }
            offset = center;
        }
        sad = bestSad;
        return center - searchSizeTop;
    }

    // ---------------------------------------------------------------- zero-mv skip and low temporal variance

    /// <summary>set_force_zeromv_skip_for_sb.</summary>
    private static bool SetForceZeromvSkipForSb(AomComp cpi, AomMacroblock x, Span<uint> uvSad, int miRow, int miCol, uint ySad, int bsize)
    {
        var cm = cpi.Cm;
        int s = cpi.Sf.rt_sf.set_zeromv_skip_based_on_source_sad;
        int ssn = x.SourceSadNonrd;
        bool bySrcSad = s != 0 && (s >= 3 ? ssn <= AomRtSb.kLowSad : s >= 2 ? ssn <= AomRtSb.kVeryLowSad : ssn == AomRtSb.kZeroSad);
        if (!bySrcSad) return false;
        int shift = cpi.Sf.rt_sf.increase_source_sad_thresh != 0 ? 1 : 0;
        int blockWidth = MiSizeWide[cm.SbSize], blockHeight = MiSizeHigh[cm.SbSize];
        uint threshY = cpi.Rt!.ZeromvSkipThreshExitPart[bsize] << shift;
        uint threshUv = ((3 * threshY) >> 2) << shift;
        if (ssn >= AomRtSb.kVeryLowSad && cpi.Sf.rt_sf.part_early_exit_zeromv == 1) threshUv >>= 3;
        var xd = x.E;
        if (miCol + blockWidth <= xd.TileMiColEnd && miRow + blockHeight <= xd.TileMiRowEnd && ySad < threshY && uvSad[0] < threshUv &&
            uvSad[1] < threshUv)
        {
            SetBlockSize(cm, miRow, miCol, bsize);
            x.ForceZeromvSkipForSb = 1;
            return true;
        }
        if (ssn == AomRtSb.kZeroSad && cpi.Sf.rt_sf.part_early_exit_zeromv >= 2) x.ForceZeromvSkipForSb = 2;
        return false;
    }

    /// <summary>set_low_temp_var_flag_64x64.</summary>
    private static void SetLowTempVarFlag64x64(AomCommon cm, AomMacroblock x, AomMacroblockD xd, AomVarTree vt, int i64, Span<long> thresholds,
        int miCol, int miRow)
    {
        var v64 = vt.V64[i64];
        int bs = xd.Mi0.Bsize;
        if (bs == BLOCK_64X64)
        {
            if (v64.None.Variance < (thresholds[0] >> 1)) x.VarianceLow[0] = 1;
        }
        else if (bs == BLOCK_64X32)
        {
            if (v64.Horz0.Variance < (thresholds[0] >> 2)) x.VarianceLow[1] = 1;
            if (v64.Horz1.Variance < (thresholds[0] >> 2)) x.VarianceLow[2] = 1;
        }
        else if (bs == BLOCK_32X64)
        {
            if (v64.Vert0.Variance < (thresholds[0] >> 2)) x.VarianceLow[3] = 1;
            if (v64.Vert1.Variance < (thresholds[0] >> 2)) x.VarianceLow[4] = 1;
        }
        else
        {
            ReadOnlySpan<int> idxR = stackalloc int[] { 0, 0, 8, 8 };
            ReadOnlySpan<int> idxC = stackalloc int[] { 0, 8, 0, 8 };
            for (int lvl1 = 0; lvl1 < 4; lvl1++)
            {
                if (cm.MiCols <= miCol + idxC[lvl1] || cm.MiRows <= miRow + idxR[lvl1]) continue;
                var thisMi = cm.MiGridBase[cm.MiStride * (miRow + idxR[lvl1]) + miCol + idxC[lvl1]];
                if (thisMi == null) continue;
                var v32 = vt.V32[i64 * 4 + lvl1];
                if (thisMi.Bsize == BLOCK_32X32)
                {
                    long t32 = (5 * thresholds[1]) >> 3;
                    if (v32.None.Variance < t32) x.VarianceLow[lvl1 + 5] = 1;
                }
                else if (thisMi.Bsize == BLOCK_16X16 || thisMi.Bsize == BLOCK_32X16 || thisMi.Bsize == BLOCK_16X32)
                {
                    for (int lvl2 = 0; lvl2 < 4; lvl2++)
                        if (vt.V16[(i64 * 4 + lvl1) * 4 + lvl2].None.Variance < (thresholds[2] >> 8))
                            x.VarianceLow[(lvl1 << 2) + lvl2 + 9] = 1;
                }
            }
        }
    }

    /// <summary>set_low_temp_var_flag_128x128.</summary>
    private static void SetLowTempVarFlag128x128(AomCommon cm, AomMacroblock x, AomMacroblockD xd, AomVarTree vt, Span<long> thresholds,
        int miCol, int miRow)
    {
        int bs = xd.Mi0.Bsize;
        var v = vt.V128;
        if (bs == BLOCK_128X128)
        {
            if (v.None.Variance < (thresholds[0] >> 1)) x.VarianceLow[0] = 1;
            return;
        }
        if (bs == BLOCK_128X64)
        {
            if (v.Horz0.Variance < (thresholds[0] >> 2)) x.VarianceLow[1] = 1;
            if (v.Horz1.Variance < (thresholds[0] >> 2)) x.VarianceLow[2] = 1;
            return;
        }
        if (bs == BLOCK_64X128)
        {
            if (v.Vert0.Variance < (thresholds[0] >> 2)) x.VarianceLow[3] = 1;
            if (v.Vert1.Variance < (thresholds[0] >> 2)) x.VarianceLow[4] = 1;
            return;
        }
        ReadOnlySpan<int> i64R = stackalloc int[] { 0, 0, 16, 16 };
        ReadOnlySpan<int> i64C = stackalloc int[] { 0, 16, 0, 16 };
        ReadOnlySpan<int> i32R = stackalloc int[] { 0, 0, 8, 8 };
        ReadOnlySpan<int> i32C = stackalloc int[] { 0, 8, 0, 8 };
        for (int lvl1 = 0; lvl1 < 4; lvl1++)
        {
            int idxStr = cm.MiStride * (miRow + i64R[lvl1]) + miCol + i64C[lvl1];
            var mi64 = idxStr < cm.MiGridBase.Length ? cm.MiGridBase[idxStr] : null;
            if (mi64 == null) continue;
            if (cm.MiCols <= miCol + i64C[lvl1] || cm.MiRows <= miRow + i64R[lvl1]) continue;
            long t64 = (5 * thresholds[1]) >> 3;
            var v64 = vt.V64[lvl1];
            if (mi64.Bsize == BLOCK_64X64)
            {
                if (v64.None.Variance < t64) x.VarianceLow[5 + lvl1] = 1;
            }
            else if (mi64.Bsize == BLOCK_64X32)
            {
                if (v64.Horz0.Variance < (t64 >> 1)) x.VarianceLow[9 + (lvl1 << 1)] = 1;
                if (v64.Horz1.Variance < (t64 >> 1)) x.VarianceLow[9 + (lvl1 << 1) + 1] = 1;
            }
            else if (mi64.Bsize == BLOCK_32X64)
            {
                if (v64.Vert0.Variance < (t64 >> 1)) x.VarianceLow[17 + (lvl1 << 1)] = 1;
                if (v64.Vert1.Variance < (t64 >> 1)) x.VarianceLow[17 + (lvl1 << 1) + 1] = 1;
            }
            else
            {
                for (int lvl2 = 0; lvl2 < 4; lvl2++)
                {
                    var mi32 = cm.MiGridBase[idxStr + cm.MiStride * i32R[lvl2] + i32C[lvl2]];
                    if (mi32 == null) continue;
                    if (cm.MiCols <= miCol + i64C[lvl1] + i32C[lvl2] || cm.MiRows <= miRow + i64R[lvl1] + i32R[lvl2]) continue;
                    long t32 = (5 * thresholds[2]) >> 3;
                    if (mi32.Bsize == BLOCK_32X32)
                    {
                        if (vt.V32[lvl1 * 4 + lvl2].None.Variance < t32) x.VarianceLow[25 + (lvl1 << 2) + lvl2] = 1;
                    }
                    else if (mi32.Bsize == BLOCK_16X16 || mi32.Bsize == BLOCK_32X16 || mi32.Bsize == BLOCK_16X32)
                    {
                        for (int lvl3 = 0; lvl3 < 4; lvl3++)
                            if (vt.V16[(lvl1 * 4 + lvl2) * 4 + lvl3].None.Variance < (thresholds[3] >> 8))
                                x.VarianceLow[41 + (lvl1 << 4) + (lvl2 << 2) + lvl3] = 1;
                    }
                }
            }
        }
    }

    private static readonly int[,] PosShift16x16 = { { 9, 10, 13, 14 }, { 11, 12, 15, 16 }, { 17, 18, 21, 22 }, { 19, 20, 23, 24 } };

    /// <summary>av1_get_force_skip_low_temp_var_small_sb.</summary>
    internal static int GetForceSkipLowTempVarSmallSb(byte[] varianceLow, int miRow, int miCol, int bsize)
    {
        int miX = miRow & 0xF, miY = miCol & 0xF;
        int i = miX >> 2, j = miY >> 2;
        switch (bsize)
        {
            case BLOCK_64X64: return varianceLow[0];
            case BLOCK_64X32:
                if (miY == 0 && miX == 0) return varianceLow[1];
                if (miY == 0 && miX != 0) return varianceLow[2];
                return 0;
            case BLOCK_32X64:
                if (miY == 0 && miX == 0) return varianceLow[3];
                if (miY != 0 && miX == 0) return varianceLow[4];
                return 0;
            case BLOCK_32X32:
                if (miY == 0 && miX == 0) return varianceLow[5];
                if (miY != 0 && miX == 0) return varianceLow[6];
                if (miY == 0 && miX != 0) return varianceLow[7];
                return varianceLow[8];
            case BLOCK_32X16:
            case BLOCK_16X32:
            case BLOCK_16X16:
                return varianceLow[PosShift16x16[i, j]];
            default: return 0;
        }
    }

    /// <summary>av1_get_force_skip_low_temp_var (128x128 superblocks).</summary>
    internal static int GetForceSkipLowTempVar(byte[] varianceLow, int miRow, int miCol, int bsize)
    {
        int x = (miCol & 0x1F) >> 4, y = (miRow & 0x17) >> 3;
        int idx64 = y + x;
        x = (miCol & 0xF) >> 3;
        y = (miRow & 0xB) >> 2;
        int idx32 = y + x;
        x = (miCol & 0x7) >> 2;
        y = (miRow & 0x5) >> 1;
        int idx16 = y + x;
        switch (bsize)
        {
            case BLOCK_128X128: return varianceLow[0];
            case BLOCK_128X64: return varianceLow[1 + ((miRow & 0x1F) != 0 ? 1 : 0)];
            case BLOCK_64X128: return varianceLow[3 + ((miCol & 0x1F) != 0 ? 1 : 0)];
            case BLOCK_64X64: return varianceLow[5 + idx64];
            case BLOCK_64X32:
            {
                int xx = (miCol & 0x1F) >> 4, yy = (miRow & 0x1F) >> 3;
                return varianceLow[9 + (xx << 1) + (yy % 2) + ((yy >> 1) << 2)];
            }
            case BLOCK_32X64:
            {
                int xx = (miCol & 0x1F) >> 3, yy = (miRow & 0x1F) >> 4;
                return varianceLow[17 + (yy << 2) + xx];
            }
            case BLOCK_32X32: return varianceLow[25 + (idx64 << 2) + idx32];
            case BLOCK_32X16:
            case BLOCK_16X32:
            case BLOCK_16X16: return varianceLow[41 + (idx64 << 4) + (idx32 << 2) + idx16];
            default: return 0;
        }
    }
}
