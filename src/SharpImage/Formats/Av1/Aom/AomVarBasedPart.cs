using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>VPartVar (encoder.h): sum of squares, sum, log2 of the sample count, and the derived variance.</summary>
internal struct AomVPartVar
{
    public uint SumSquareError;
    public int SumError;
    public int Log2Count;
    public int Variance;
}

/// <summary>VPVariance (encoder.h): the none / horz / vert variances of one tree node.</summary>
internal sealed class AomVPVariance
{
    public AomVPartVar None, Horz0, Horz1, Vert0, Vert1;
}

/// <summary>The variance tree of one superblock (VP128x128 down to the 4x4-average leaves of a key frame), flat per
/// level: a node's children are at (index * 4 + 0..3) of the next level.</summary>
internal sealed class AomVarTree
{
    public readonly AomVPVariance V128 = new();
    public readonly AomVPVariance[] V64 = New(4), V32 = New(16), V16 = New(64), V8 = New(256);
    public readonly AomVPartVar[] Leaf4 = new AomVPartVar[1024];   // VP4x4.part_variances.none

    private static AomVPVariance[] New(int n)
    {
        var a = new AomVPVariance[n];
        for (int i = 0; i < n; i++) a[i] = new AomVPVariance();
        return a;
    }
}

// Port of libaom 3.14.1 av1/encoder/var_based_part.c for intra-only frames (the all-intra speeds 7-9 run
// VAR_BASED_PARTITION): av1_choose_var_based_partitioning with its key-frame paths (4x4 source averages against 128,
// the key-frame thresholds, the forced splits, set_vt_partitioning's intra rules, the 16x16 sub-block variance prune),
// writing the chosen block sizes into the mi grid for av1_rd_use_partition / av1_nonrd_use_partition to read back.
internal static class AomVarBasedPart
{
    private const int PART_EVAL_ALL = 0, PART_EVAL_ONLY_SPLIT = 1, PART_EVAL_ONLY_NONE = 2;
    private const int RESOLUTION_720P = 1280 * 720;

    private static int BlkIdxX(int idx, int level) => (idx & 1) << level;
    private static int BlkIdxY(int idx, int level) => (idx >> 1) << level;

    /// <summary>fill_variance.</summary>
    private static void FillVariance(uint s2, int s, int c, ref AomVPartVar v)
    {
        v.SumSquareError = s2;
        v.SumError = s;
        v.Log2Count = c;
    }

    /// <summary>get_variance (32-bit unsigned arithmetic like libaom's uint32_t sum_square_error).</summary>
    private static void GetVariance(ref AomVPartVar v)
    {
        uint d = v.SumSquareError - (uint)(((long)v.SumError * v.SumError) >> v.Log2Count);
        v.Variance = (int)((256u * d) >> v.Log2Count);
    }

    /// <summary>sum_2_variances.</summary>
    private static void Sum2Variances(in AomVPartVar a, in AomVPartVar b, ref AomVPartVar r)
        => FillVariance(a.SumSquareError + b.SumSquareError, a.SumError + b.SumError, a.Log2Count + 1, ref r);

    /// <summary>fill_variance_tree: a node's none / horz / vert from its four children's none.</summary>
    private static void FillVarianceTree(AomVPVariance node, in AomVPartVar s0, in AomVPartVar s1, in AomVPartVar s2, in AomVPartVar s3)
    {
        Sum2Variances(s0, s1, ref node.Horz0);
        Sum2Variances(s2, s3, ref node.Horz1);
        Sum2Variances(s0, s2, ref node.Vert0);
        Sum2Variances(s1, s3, ref node.Vert1);
        Sum2Variances(node.Vert0, node.Vert1, ref node.None);
    }

    private static void FillTree8(AomVarTree vt, int i8)
        => FillVarianceTree(vt.V8[i8], vt.Leaf4[i8 * 4], vt.Leaf4[i8 * 4 + 1], vt.Leaf4[i8 * 4 + 2], vt.Leaf4[i8 * 4 + 3]);

    private static void FillTree(AomVPVariance node, AomVPVariance[] children, int first)
        => FillVarianceTree(node, children[first].None, children[first + 1].None, children[first + 2].None, children[first + 3].None);

    /// <summary>set_block_size: the block's top-left mi points at its own allocation and records the size.</summary>
    private static void SetBlockSize(AomCommon cm, int miRow, int miCol, int bsize)
    {
        if (cm.MiCols > miCol && cm.MiRows > miRow)
        {
            int idx = miRow * cm.MiStride + miCol;
            var mi = cm.MiGridBase[idx] = cm.MiAlloc[idx];
            mi.Bsize = bsize;
        }
    }

    /// <summary>set_vt_partitioning (intra-only frames).</summary>
    private static bool SetVtPartitioning(AomComp cpi, AomMacroblockD xd, AomVPVariance vt, int bsize, int miRow, int miCol,
        long threshold, int bsizeMin, int forceSplit)
    {
        var cm = cpi.Cm;
        int blockWidth = MiSizeWide[bsize], blockHeight = MiSizeHigh[bsize];
        int bsWidthCheck = blockWidth, bsHeightCheck = blockHeight;
        int bsWidthVertCheck = blockWidth >> 1, bsHeightHorizCheck = blockHeight >> 1;
        // On the right and bottom boundary only half the bsize needs to fit (the boundary is extended up to 64): 64x64 SBs
        if (cm.SbSize == BLOCK_64X64)
        {
            if (cm.TileMiColEnd == cm.MiCols)
            {
                bsWidthCheck = (blockWidth >> 1) + 1;
                bsWidthVertCheck = (blockWidth >> 2) + 1;
            }
            if (cm.TileMiRowEnd == cm.MiRows)
            {
                bsHeightCheck = (blockHeight >> 1) + 1;
                bsHeightHorizCheck = (blockHeight >> 2) + 1;
            }
        }

        if (miCol + bsWidthCheck <= cm.TileMiColEnd && miRow + bsHeightCheck <= cm.TileMiRowEnd && forceSplit == PART_EVAL_ONLY_NONE)
        {
            SetBlockSize(cm, miRow, miCol, bsize);
            return true;
        }
        if (forceSplit == PART_EVAL_ONLY_SPLIT) return false;

        if (bsize == bsizeMin)
        {
            GetVariance(ref vt.None);   // frame_is_intra_only
            if (miCol + bsWidthCheck <= cm.TileMiColEnd && miRow + bsHeightCheck <= cm.TileMiRowEnd && vt.None.Variance < threshold)
            {
                SetBlockSize(cm, miRow, miCol, bsize);
                return true;
            }
            return false;
        }
        if (bsize > bsizeMin)
        {
            GetVariance(ref vt.None);
            // For key frame: take split for bsize above 32X32 or very high variance.
            if (bsize > BLOCK_32X32 || vt.None.Variance > (threshold << 4)) return false;
            // If variance is low, take the bsize (no split).
            if (miCol + bsWidthCheck <= cm.TileMiColEnd && miRow + bsHeightCheck <= cm.TileMiRowEnd && vt.None.Variance < threshold)
            {
                SetBlockSize(cm, miRow, miCol, bsize);
                return true;
            }
            // Check vertical split.
            if (miRow + bsHeightCheck <= cm.TileMiRowEnd && miCol + bsWidthVertCheck <= cm.TileMiColEnd)
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
            // Check horizontal split.
            if (miCol + bsWidthCheck <= cm.TileMiColEnd && miRow + bsHeightHorizCheck <= cm.TileMiRowEnd)
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

    /// <summary>aom_avg_4x4 (the SSE2 kernel computes the same rounded mean).</summary>
    private static int Avg4x4(byte[] s, int off, int p)
    {
        int sum = 0;
        for (int i = 0; i < 4; i++, off += p) sum += s[off] + s[off + 1] + s[off + 2] + s[off + 3];
        return (sum + 8) >> 4;
    }

    /// <summary>fill_variance_4x4avg (key frames: the 4x4 source means against 128).</summary>
    private static void FillVariance4x4Avg(byte[] src, int srcOff, int srcStride, int x8Idx, int y8Idx, AomVarTree vt, int i8,
        int pixelsWide, int pixelsHigh)
    {
        for (int idx = 0; idx < 4; idx++)
        {
            int x4Idx = x8Idx + BlkIdxX(idx, 2), y4Idx = y8Idx + BlkIdxY(idx, 2);
            uint sse = 0;
            int sum = 0;
            if (x4Idx < pixelsWide && y4Idx < pixelsHigh)
            {
                int srcAvg = Avg4x4(src, srcOff + y4Idx * srcStride + x4Idx, srcStride);
                sum = srcAvg - 128;
                sse = (uint)(sum * sum);
            }
            FillVariance(sse, sum, 0, ref vt.Leaf4[i8 * 4 + idx]);
        }
    }

    /// <summary>set_vbp_thresholds for a key frame (set_vbp_thresholds_key_frame).</summary>
    private static void SetVbpThresholdsKeyFrame(AomComp cpi, Span<long> thresholds, int qindex)
    {
        var cm = cpi.Cm;
        int acQ = Av1Tables.DequantTable[0, Math.Clamp(qindex, 0, 255), 1];   // av1_ac_quant_QTX (8-bit)
        long thresholdBase = 120L * acQ;
        int thresholdLeftShift = cpi.Sf.rt_sf.var_part_split_threshold_shift;
        int numPixels = cm.Width * cm.Height;
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

    /// <summary>get_part_eval_based_on_sub_blk_var.</summary>
    private static int PartEvalBasedOnSubBlkVar(AomVarTree vt, int i16, long threshold16)
    {
        int max8x8Var = 0, min8x8Var = int.MaxValue;
        for (int splitIdx = 0; splitIdx < 4; splitIdx++)
        {
            ref var v = ref vt.V8[i16 * 4 + splitIdx].None;
            GetVariance(ref v);
            max8x8Var = Math.Max(v.Variance, max8x8Var);
            min8x8Var = Math.Min(v.Variance, min8x8Var);
        }
        return (max8x8Var - min8x8Var) > (threshold16 << 2) ? PART_EVAL_ONLY_SPLIT : PART_EVAL_ONLY_NONE;
    }

    /// <summary>av1_choose_var_based_partitioning for an intra-only frame (no segmentation, no delta q, no temporal
    /// filtering). The caller has run av1_set_offsets for the superblock.</summary>
    internal static void ChooseVarBasedPartitioning(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var vt = x.VarTree;
        Span<int> forceSplit = stackalloc int[85];
        Span<int> maxVar32x32 = stackalloc int[4], minVar32x32 = stackalloc int[4];
        Span<long> thresholds = stackalloc long[5];
        bool isSmallSb = cm.SbSize == BLOCK_64X64;
        int num64x64Blocks = isSmallSb ? 1 : 4;

        // the superblock qindex (delta q: base + x->delta_qindex)
        int sbQindex = Math.Clamp(cpi.DeltaQPresentFlag ? cm.BaseQindex + x.DeltaQindex : cm.BaseQindex, 0, 255);
        SetVbpThresholdsKeyFrame(cpi, thresholds, sbQindex);

        var src = x.Plane[0].Src;
        byte[] srcBuf = src.Buf;
        int srcOff = src.Offset, srcStride = src.Stride;

        forceSplit[0] = PART_EVAL_ALL;
        x.SourceVariance = uint.MaxValue;
        // (use_nonrd_pick_mode source variance: source_sad_nonrd is kMedSad, not above kLowSad, on key frames)
        // (chroma_check returns for key frames)

        // fill_variance_tree_leaves (key frame: 4x4 source averages)
        int pixelsWide = isSmallSb ? 64 : 128, pixelsHigh = pixelsWide;
        if (xd.MbToRightEdge < 0) pixelsWide += xd.MbToRightEdge >> 3;
        if (xd.MbToBottomEdge < 0) pixelsHigh += xd.MbToBottomEdge >> 3;
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
                for (int lvl2Idx = 0; lvl2Idx < 4; lvl2Idx++)
                {
                    int x16Idx = x32Idx + BlkIdxX(lvl2Idx, 4), y16Idx = y32Idx + BlkIdxY(lvl2Idx, 4);
                    forceSplit[21 + lvl1ScaleIdx + lvl2Idx] = PART_EVAL_ALL;
                    int i16 = lvl1ScaleIdx + lvl2Idx;
                    for (int lvl3Idx = 0; lvl3Idx < 4; lvl3Idx++)
                    {
                        int x8Idx = x16Idx + BlkIdxX(lvl3Idx, 3), y8Idx = y16Idx + BlkIdxY(lvl3Idx, 3);
                        FillVariance4x4Avg(srcBuf, srcOff, srcStride, x8Idx, y8Idx, vt, i16 * 4 + lvl3Idx, pixelsWide, pixelsHigh);
                    }
                }
            }
        }

        for (int blk64Idx = 0; blk64Idx < num64x64Blocks; ++blk64Idx)
        {
            maxVar32x32[blk64Idx] = 0;
            minVar32x32[blk64Idx] = int.MaxValue;
            int blk64ScaleIdx = blk64Idx << 2;
            for (int lvl1Idx = 0; lvl1Idx < 4; lvl1Idx++)
            {
                int lvl1ScaleIdx = (blk64ScaleIdx + lvl1Idx) << 2;
                for (int lvl2Idx = 0; lvl2Idx < 4; lvl2Idx++)
                {
                    int i16 = lvl1ScaleIdx + lvl2Idx;
                    var vtemp = vt.V16[i16];
                    for (int lvl3Idx = 0; lvl3Idx < 4; lvl3Idx++) FillTree8(vt, i16 * 4 + lvl3Idx);
                    FillTree(vtemp, vt.V8, i16 * 4);
                    // If variance of this 16x16 block is above the threshold, force block to split. This also forces a
                    // split on the upper levels.
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
                int i32 = blk64ScaleIdx + lvl1Idx;
                FillTree(vt.V32[i32], vt.V16, i32 * 4);
                // If variance of this 32x32 block is above the threshold, force this block to split (also forces a
                // split on the 64x64 level).
                if (forceSplit[5 + blk64ScaleIdx + lvl1Idx] == PART_EVAL_ALL)
                {
                    GetVariance(ref vt.V32[i32].None);
                    int var32x32 = vt.V32[i32].None.Variance;
                    maxVar32x32[blk64Idx] = Math.Max(var32x32, maxVar32x32[blk64Idx]);
                    minVar32x32[blk64Idx] = Math.Min(var32x32, minVar32x32[blk64Idx]);
                    if (var32x32 > thresholds[2])
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
                // (the max - min 32x32 variance split is for inter frames only)
            }
            if (isSmallSb) forceSplit[0] = PART_EVAL_ONLY_SPLIT;
        }

        if (forceSplit[0] == PART_EVAL_ALL)
        {
            FillTree(vt.V128, vt.V64, 0);
            GetVariance(ref vt.V128.None);
        }

        if (miCol + 32 > cm.TileMiColEnd || miRow + 32 > cm.TileMiRowEnd ||
            !SetVtPartitioning(cpi, xd, vt.V128, BLOCK_128X128, miRow, miCol, thresholds[0], BLOCK_16X16, forceSplit[0]))
        {
            for (int blk64Idx = 0; blk64Idx < num64x64Blocks; ++blk64Idx)
            {
                int x64Idx = BlkIdxX(blk64Idx, 4), y64Idx = BlkIdxY(blk64Idx, 4);
                int blk64ScaleIdx = blk64Idx << 2;
                // Go through the entire structure, splitting every block size until one has a variance lower than
                // the threshold.
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
        // (short_circuit_low_temp_var is off in the all-intra mode)
    }

    /// <summary>get_partition (av1_common_int.h): the partition of bsize at (mi_row, mi_col) read back from the mi grid.</summary>
    internal static int GetPartition(AomCommon cm, int miRow, int miCol, int bsize)
    {
        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return PARTITION_INVALID;
        int offset = miRow * cm.MiStride + miCol;
        var mi = cm.MiGridBase;
        int subsize = mi[offset]!.Bsize;
        if (subsize == bsize) return PARTITION_NONE;
        int bhigh = MiSizeHigh[bsize], bwide = MiSizeWide[bsize];
        int sshigh = MiSizeHigh[subsize], sswide = MiSizeWide[subsize];
        if (bsize > BLOCK_8X8 && miRow + bwide / 2 < cm.MiRows && miCol + bhigh / 2 < cm.MiCols)
        {
            // In this case, the block might be using an extended partition type.
            var mbmiRight = mi[offset + bwide / 2]!;
            var mbmiBelow = mi[offset + bhigh / 2 * cm.MiStride]!;
            if (sswide == bwide)
            {
                if (sshigh * 4 == bhigh) return PARTITION_HORZ_4;
                return mbmiBelow.Bsize == subsize ? PARTITION_HORZ : PARTITION_HORZ_B;
            }
            if (sshigh == bhigh)
            {
                if (sswide * 4 == bwide) return PARTITION_VERT_4;
                return mbmiRight.Bsize == subsize ? PARTITION_VERT : PARTITION_VERT_B;
            }
            if (sswide * 2 != bwide || sshigh * 2 != bhigh) return PARTITION_SPLIT;
            if (MiSizeWide[mbmiBelow.Bsize] == bwide) return PARTITION_HORZ_A;
            if (MiSizeHigh[mbmiRight.Bsize] == bhigh) return PARTITION_VERT_A;
            return PARTITION_SPLIT;
        }
        int vertSplit = sswide < bwide ? 1 : 0, horzSplit = sshigh < bhigh ? 1 : 0;
        int splitIdx = (vertSplit << 1) | horzSplit;
        return splitIdx switch { 1 => PARTITION_HORZ, 2 => PARTITION_VERT, 3 => PARTITION_SPLIT, _ => PARTITION_INVALID };
    }
}
