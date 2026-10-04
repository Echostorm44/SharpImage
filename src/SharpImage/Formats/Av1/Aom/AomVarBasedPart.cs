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
internal static partial class AomVarBasedPart
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
            var mi = cm.MiGridBase[idx] = cm.MiAlloc(idx);
            mi.Bsize = bsize;
        }
    }

    /// <summary>aom_avg_4x4 (the SSE2 kernel computes the same rounded mean).</summary>
    private static int Avg4x4(byte[] s, int off, int p)
    {
        int sum = 0;
        for (int i = 0; i < 4; i++, off += p) sum += s[off] + s[off + 1] + s[off + 2] + s[off + 3];
        return (sum + 8) >> 4;
    }

    /// <summary>fill_variance_4x4avg on a high bit depth source (aom_highbd_avg_4x4; dst_avg stays 128).</summary>
    private static void FillVariance4x4AvgHbd(ushort[] src, int srcOff, int srcStride, int x8Idx, int y8Idx, AomVarTree vt, int i8,
        int pixelsWide, int pixelsHigh)
    {
        for (int idx = 0; idx < 4; idx++)
        {
            int x4Idx = x8Idx + BlkIdxX(idx, 2), y4Idx = y8Idx + BlkIdxY(idx, 2);
            uint sse = 0;
            int sum = 0;
            if (x4Idx < pixelsWide && y4Idx < pixelsHigh)
            {
                int off = srcOff + y4Idx * srcStride + x4Idx, s = 0;
                for (int i = 0; i < 4; i++, off += srcStride) s += src[off] + src[off + 1] + src[off + 2] + src[off + 3];
                sum = ((s + 8) >> 4) - 128;
                sse = (uint)(sum * sum);
            }
            FillVariance(sse, sum, 0, ref vt.Leaf4[i8 * 4 + idx]);
        }
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
