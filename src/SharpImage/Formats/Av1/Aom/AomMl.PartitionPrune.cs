using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// The partition-search ML prunes (av1/encoder/partition_strategy.c) and their feature extraction, taking the values
// they read from AV1_COMP / MACROBLOCK / PartitionSearchState / SIMPLE_MOTION_DATA_TREE as plain arguments. The
// external-partition-model hooks (ext_ml_model_decision_*) are not ported: libaom's controller is never ready unless
// an external model is installed, so they always return false.
internal static partial class AomMl
{
    internal const int HORZ = 0, VERT = 1;                                  // RECT_PART_TYPE
    internal const int HORZ_A = 0, HORZ_B = 1, VERT_A = 2, VERT_B = 3;     // AB_PART_TYPE
    internal const int HORZ4 = 0, VERT4 = 1;                               // PART4_TYPES
    internal const int MAX_SB_SIZE_LOG2 = 7;

    // get_partition_subsize (blockd.h)
    internal static int GetPartitionSubsize(int bsize, int partition)
    {
        if (partition == PARTITION_INVALID) return BLOCK_INVALID;
        int sqr = bsize switch
        {
            BLOCK_4X4 => 0, BLOCK_8X8 => 1, BLOCK_16X16 => 2, BLOCK_32X32 => 3, BLOCK_64X64 => 4, BLOCK_128X128 => 5, _ => 6,
        };
        return sqr >= 6 ? BLOCK_INVALID : SubsizeLookup[partition * 6 + sqr];
    }

    // get_unsigned_bits (av1/common/common.h)
    internal static int GetUnsignedBits(uint numValues) => numValues > 0 ? 32 - System.Numerics.BitOperations.LeadingZeroCount(numValues) : 0;

    // convert_bsize_to_idx (partition_strategy.c)
    private static int ConvertBsizeToIdx(int bsize) => bsize switch
    {
        BLOCK_128X128 => 0, BLOCK_64X64 => 1, BLOCK_32X32 => 2, BLOCK_16X16 => 3, BLOCK_8X8 => 4, _ => -1,
    };

    // ---- av1_get_perpixel_variance (encodeframe.c): the block's variance against the flat mid-grey
    //      AV1_VAR_OFFS / AV1_HIGH_VAR_OFFS_* reference, per pixel (all of aom_[highbd_bd_]variance's kernels agree) ----

    /// <summary>av1_get_perpixel_variance for an 8-bit (lowbd) source buffer.</summary>
    internal static uint GetPerPixelVariance(ReadOnlySpan<byte> src, int stride, int bsize)
    {
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        int sum = 0;
        uint sse = 0;
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
            {
                int d = src[i * stride + j] - 128;
                sum += d;
                sse += (uint)(d * d);
            }
        uint var = unchecked(sse - (uint)((long)sum * sum / (w * h)));
        return RoundPowerOfTwo(var, NumPelsLog2Lookup[bsize]);
    }

    /// <summary>av1_get_perpixel_variance for a high-bitdepth source buffer (aom_highbd_{8,10,12}_variance).</summary>
    internal static uint GetPerPixelVariance(ReadOnlySpan<ushort> src, int stride, int bsize, int bitDepth)
    {
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        int offs = 128 << (bitDepth - 8);
        long sumLong = 0;
        ulong sseLong = 0;
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
            {
                int d = src[i * stride + j] - offs;
                sumLong += d;
                sseLong += (ulong)((long)d * d);
            }
        uint var;
        if (bitDepth == 8)
        {
            uint sse = (uint)sseLong;
            int sum = (int)sumLong;
            var = unchecked(sse - (uint)((long)sum * sum / (w * h)));
        }
        else
        {
            int sh = bitDepth == 10 ? 2 : 4;
            uint sse = (uint)((sseLong + (1UL << (2 * sh - 1))) >> (2 * sh));
            int sum = (int)((sumLong + (1L << (sh - 1))) >> sh);
            long v = (long)sse - (long)sum * sum / (w * h);
            var = v >= 0 ? (uint)v : 0;
        }
        return RoundPowerOfTwo(var, NumPelsLog2Lookup[bsize]);
    }

    private static uint RoundPowerOfTwo(uint value, int n) => unchecked((value + ((1u << n) >> 1)) >> n);

    // ---- av1_ml_prune_rect_partition ----

    /// <summary>av1_ml_prune_rect_partition (libaom calls it on inter frames only): wholeBlockVariance and
    /// splitVariance[4] are av1_get_perpixel_variance of the block and of its four PARTITION_SPLIT quarters.
    /// Sets pruneRectPart[HORZ] / [VERT] to 1 where the model prunes them (never clears).</summary>
    internal static void PruneRectPartition(int bsize, long bestRd, long noneRd, ReadOnlySpan<long> splitRd,
        uint wholeBlockVariance, ReadOnlySpan<uint> splitVariance, Span<int> pruneRectPart)
    {
        if (bsize < BLOCK_8X8 || bestRd >= 1000000000) return;
        bestRd = Math.Max(bestRd, 1);
        ReadOnlySpan<float> probThresholds = [0.01f, 0.01f, 0.004f, 0.002f, 0.002f];
        AomNnConfig? cfg = null;
        float curThresh = 0.0f;
        switch (bsize)
        {
            case BLOCK_8X8: cfg = AomMlModels.RectPartitionNnconfig8; curThresh = probThresholds[0]; break;
            case BLOCK_16X16: cfg = AomMlModels.RectPartitionNnconfig16; curThresh = probThresholds[1]; break;
            case BLOCK_32X32: cfg = AomMlModels.RectPartitionNnconfig32; curThresh = probThresholds[2]; break;
            case BLOCK_64X64: cfg = AomMlModels.RectPartitionNnconfig64; curThresh = probThresholds[3]; break;
            case BLOCK_128X128: cfg = AomMlModels.RectPartitionNnconfig128; curThresh = probThresholds[4]; break;
        }
        if (cfg == null) return;

        Span<float> features = stackalloc float[9];
        for (int i = 0; i < 5; i++) features[i] = 1.0f;
        if (noneRd > 0 && noneRd < 1000000000) features[0] = (float)noneRd / (float)bestRd;
        for (int i = 0; i < 4; i++)
            if (splitRd[i] > 0 && splitRd[i] < 1000000000) features[1 + i] = (float)splitRd[i] / (float)bestRd;
        int whole = Math.Max(unchecked((int)wholeBlockVariance), 1);
        for (int i = 0; i < 4; i++) features[5 + i] = (float)unchecked((int)splitVariance[i]) / (float)whole;

        Span<float> rawScores = stackalloc float[3];
        rawScores.Clear();
        NnPredict(features, cfg, true, rawScores);
        Span<float> probs = stackalloc float[3];
        NnSoftmax(rawScores, probs, 3);
        if (probs[1] <= curThresh) pruneRectPart[HORZ] = 1;
        if (probs[2] <= curThresh) pruneRectPart[VERT] = 1;
    }

    /// <summary>av1_ml_prune_rect_partition with the variances computed from the (lowbd) source block.</summary>
    internal static void PruneRectPartition(ReadOnlySpan<byte> src, int stride, int bsize, long bestRd, long noneRd,
        ReadOnlySpan<long> splitRd, Span<int> pruneRectPart)
    {
        if (bsize < BLOCK_8X8 || bestRd >= 1000000000 || ConvertBsizeToIdx(bsize) < 0) return;
        int subsize = GetPartitionSubsize(bsize, PARTITION_SPLIT), half = BlockSizeWide[bsize] / 2;
        Span<uint> split = stackalloc uint[4];
        for (int i = 0; i < 4; i++) split[i] = GetPerPixelVariance(src[((i >> 1) * half * stride + (i & 1) * half)..], stride, subsize);
        PruneRectPartition(bsize, bestRd, noneRd, splitRd, GetPerPixelVariance(src, stride, bsize), split, pruneRectPart);
    }

    /// <summary>av1_ml_prune_rect_partition with the variances computed from the high-bitdepth source block.</summary>
    internal static void PruneRectPartition(ReadOnlySpan<ushort> src, int stride, int bitDepth, int bsize, long bestRd,
        long noneRd, ReadOnlySpan<long> splitRd, Span<int> pruneRectPart)
    {
        if (bsize < BLOCK_8X8 || bestRd >= 1000000000 || ConvertBsizeToIdx(bsize) < 0) return;
        int subsize = GetPartitionSubsize(bsize, PARTITION_SPLIT), half = BlockSizeWide[bsize] / 2;
        Span<uint> split = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
            split[i] = GetPerPixelVariance(src[((i >> 1) * half * stride + (i & 1) * half)..], stride, subsize, bitDepth);
        PruneRectPartition(bsize, bestRd, noneRd, splitRd, GetPerPixelVariance(src, stride, bsize, bitDepth), split, pruneRectPart);
    }

    // the 8 sub-block rd ratios shared by the AB and 4-way prunes
    private static void SubBlockRdRatios(long bestRd, ReadOnlySpan<long> horzRd, ReadOnlySpan<long> vertRd,
        ReadOnlySpan<long> splitRd, Span<float> features)
    {
        int rdcost = (int)Math.Min(int.MaxValue, bestRd);
        Span<int> sub = stackalloc int[8];
        sub.Clear();
        for (int i = 0; i < 2; i++) if (horzRd[i] > 0 && horzRd[i] < 1000000000) sub[i] = (int)horzRd[i];
        for (int i = 0; i < 2; i++) if (vertRd[i] > 0 && vertRd[i] < 1000000000) sub[2 + i] = (int)vertRd[i];
        for (int i = 0; i < 4; i++) if (splitRd[i] > 0 && splitRd[i] < 1000000000) sub[4 + i] = (int)splitRd[i];
        for (int i = 0; i < 8; i++)
        {
            float rdRatio = 1.0f;
            if (sub[i] > 0 && sub[i] < rdcost) rdRatio = (float)sub[i] / (float)rdcost;
            features[i] = rdRatio;
        }
    }

    // ---- ml_prune_ab_partition ----

    /// <summary>ml_prune_ab_partition (called from av1_prune_ab_partitions with var_ctx =
    /// get_unsigned_bits(x->source_variance)): rewrites abPartitionsAllowed[HORZ_A..VERT_B] from the model's scores.</summary>
    internal static void PruneAbPartition(int bsize, int partCtx, int varCtx, long bestRd, ReadOnlySpan<long> horzRd,
        ReadOnlySpan<long> vertRd, ReadOnlySpan<long> splitRd, Span<int> abPartitionsAllowed)
    {
        if (bsize < BLOCK_8X8 || bestRd >= 1000000000) return;
        AomNnConfig? cfg = bsize switch
        {
            BLOCK_16X16 => AomMlModels.AbPartitionNnconfig16,
            BLOCK_32X32 => AomMlModels.AbPartitionNnconfig32,
            BLOCK_64X64 => AomMlModels.AbPartitionNnconfig64,
            BLOCK_128X128 => AomMlModels.AbPartitionNnconfig128,
            _ => null,
        };
        if (cfg == null) return;

        Span<float> features = stackalloc float[10];
        features[0] = partCtx;
        features[1] = varCtx;
        SubBlockRdRatios(bestRd, horzRd, vertRd, splitRd, features[2..]);

        Span<float> score = stackalloc float[16];
        score.Clear();
        NnPredict(features, cfg, true, score);
        Span<int> intScore = stackalloc int[16];
        int maxScore = -1000;
        for (int i = 0; i < 16; ++i)
        {
            intScore[i] = CvttSs2Si(100 * score[i]);
            maxScore = Math.Max(intScore[i], maxScore);
        }
        int thresh = maxScore;
        if (bsize == BLOCK_16X16) thresh -= 150;
        else if (bsize == BLOCK_32X32) thresh -= 100;
        abPartitionsAllowed[..4].Clear();
        for (int i = 0; i < 16; ++i)
        {
            if (intScore[i] < thresh) continue;
            if (((i >> 0) & 1) != 0) abPartitionsAllowed[HORZ_A] = 1;
            if (((i >> 1) & 1) != 0) abPartitionsAllowed[HORZ_B] = 1;
            if (((i >> 2) & 1) != 0) abPartitionsAllowed[VERT_A] = 1;
            if (((i >> 3) & 1) != 0) abPartitionsAllowed[VERT_B] = 1;
        }
    }

    // ---- av1_ml_prune_4_partition ----

    /// <summary>av1_ml_prune_4_partition: horz4SourceVar / vert4SourceVar[4] are av1_get_perpixel_variance of the
    /// PARTITION_HORZ_4 / VERT_4 sub-blocks, pbSourceVariance the block's; frameWidth/Height are cm->width/height,
    /// ml4PartitionSearchLevelIndex sf.part_sf.ml_4_partition_search_level_index. Updates part4Allowed[HORZ4, VERT4].</summary>
    internal static void Prune4Partition(int bsize, int partCtx, long bestRd, ReadOnlySpan<long> horzRd,
        ReadOnlySpan<long> vertRd, ReadOnlySpan<long> splitRd, uint pbSourceVariance, ReadOnlySpan<uint> horz4SourceVar,
        ReadOnlySpan<uint> vert4SourceVar, int frameWidth, int frameHeight, int ml4PartitionSearchLevelIndex,
        Span<int> part4Allowed)
    {
        if (bestRd >= 1000000000) return;
        int minWh = Math.Min(frameWidth, frameHeight);
        int resIdx = (minWh >= 480 ? 1 : 0) + (minWh >= 720 ? 1 : 0);
        int bsizeIdx = ConvertBsizeToIdx(bsize);
        if (bsizeIdx < 0) return;
        float[]? mlMean = AomMlModels.Partition4NnMean[bsizeIdx], mlStd = AomMlModels.Partition4NnStd[bsizeIdx];
        int mlModelIndex = ml4PartitionSearchLevelIndex < 3 ? 1 : 0;
        AomNnConfig? cfg = bsize switch
        {
            BLOCK_16X16 => AomMlModels.FourPartitionNnconfig16[mlModelIndex],
            BLOCK_32X32 => AomMlModels.FourPartitionNnconfig32[mlModelIndex],
            BLOCK_64X64 => AomMlModels.FourPartitionNnconfig64[mlModelIndex],
            _ => null,
        };
        if (cfg == null || mlMean == null || mlStd == null) return;

        const int numFeatures = 18;
        Span<float> features = stackalloc float[numFeatures];
        int fi = 0;
        features[fi++] = partCtx;
        features[fi++] = GetUnsignedBits(pbSourceVariance);
        SubBlockRdRatios(bestRd, horzRd, vertRd, splitRd, features[fi..]);
        fi += 8;
        float denom = (float)unchecked(pbSourceVariance + 1);
        const float lowB = 0.1f, highB = 10.0f;
        for (int i = 0; i < 4; ++i)
        {
            float varRatio = (float)unchecked(horz4SourceVar[i] + 1) / denom;
            if (varRatio < lowB) varRatio = lowB;
            if (varRatio > highB) varRatio = highB;
            features[fi++] = varRatio;
        }
        for (int i = 0; i < 4; ++i)
        {
            float varRatio = (float)unchecked(vert4SourceVar[i] + 1) / denom;
            if (varRatio < lowB) varRatio = lowB;
            if (varRatio > highB) varRatio = highB;
            features[fi++] = varRatio;
        }
        if (mlModelIndex != 0)
            for (int idx = 0; idx < numFeatures; idx++) features[idx] = (features[idx] - mlMean[idx]) / mlStd[idx];

        if (mlModelIndex == 0)
        {
            Span<float> score = stackalloc float[4];
            score.Clear();
            NnPredict(features, cfg, true, score);
            Span<int> intScore = stackalloc int[4];
            int maxScore = -1000;
            for (int i = 0; i < 4; ++i)
            {
                intScore[i] = CvttSs2Si(100 * score[i]);
                maxScore = Math.Max(intScore[i], maxScore);
            }
            int thresh = maxScore;
            switch (bsize)
            {
                case BLOCK_16X16: thresh -= 500; break;
                case BLOCK_32X32: thresh -= 500; break;
                case BLOCK_64X64: thresh -= 200; break;
            }
            part4Allowed[..2].Clear();
            for (int i = 0; i < 4; ++i)
            {
                if (intScore[i] < thresh) continue;
                if (((i >> 0) & 1) != 0) part4Allowed[HORZ4] = 1;
                if (((i >> 1) & 1) != 0) part4Allowed[VERT4] = 1;
            }
        }
        else
        {
            Span<float> score = stackalloc float[3], probs = stackalloc float[3];
            score.Clear();
            NnPredict(features, cfg, true, score);
            NnSoftmax(score, probs, 3);
            int t = (ml4PartitionSearchLevelIndex * 3 + resIdx) * 5 + bsizeIdx;
            float searchThresh = AomMlModels.Partition4SearchThresh[t];
            float notSearchThresh = AomMlModels.Partition4NotSearchThresh[t];
            for (int i = 1; i < 3; ++i)
            {
                if (probs[i] >= searchThresh) part4Allowed[i == 1 ? HORZ4 : VERT4] = 1;
                if (probs[i] < notSearchThresh) part4Allowed[i == 1 ? HORZ4 : VERT4] = 0;
            }
        }
    }

    /// <summary>av1_ml_prune_4_partition with the sub-block variances computed from the (lowbd) source block.</summary>
    internal static void Prune4Partition(ReadOnlySpan<byte> src, int stride, int bsize, int partCtx, long bestRd,
        ReadOnlySpan<long> horzRd, ReadOnlySpan<long> vertRd, ReadOnlySpan<long> splitRd, uint pbSourceVariance,
        int frameWidth, int frameHeight, int ml4PartitionSearchLevelIndex, Span<int> part4Allowed)
    {
        if (bestRd >= 1000000000 || ConvertBsizeToIdx(bsize) < 0) return;
        int hbs = GetPartitionSubsize(bsize, PARTITION_HORZ_4), vbs = GetPartitionSubsize(bsize, PARTITION_VERT_4);
        if (hbs == BLOCK_INVALID || vbs == BLOCK_INVALID) return;
        Span<uint> hv = stackalloc uint[4], vv = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            hv[i] = GetPerPixelVariance(src[(i * BlockSizeHigh[hbs] * stride)..], stride, hbs);
            vv[i] = GetPerPixelVariance(src[(i * BlockSizeWide[vbs])..], stride, vbs);
        }
        Prune4Partition(bsize, partCtx, bestRd, horzRd, vertRd, splitRd, pbSourceVariance, hv, vv, frameWidth, frameHeight,
            ml4PartitionSearchLevelIndex, part4Allowed);
    }

    /// <summary>av1_ml_prune_4_partition with the sub-block variances computed from the high-bitdepth source block.</summary>
    internal static void Prune4Partition(ReadOnlySpan<ushort> src, int stride, int bitDepth, int bsize, int partCtx,
        long bestRd, ReadOnlySpan<long> horzRd, ReadOnlySpan<long> vertRd, ReadOnlySpan<long> splitRd,
        uint pbSourceVariance, int frameWidth, int frameHeight, int ml4PartitionSearchLevelIndex, Span<int> part4Allowed)
    {
        if (bestRd >= 1000000000 || ConvertBsizeToIdx(bsize) < 0) return;
        int hbs = GetPartitionSubsize(bsize, PARTITION_HORZ_4), vbs = GetPartitionSubsize(bsize, PARTITION_VERT_4);
        if (hbs == BLOCK_INVALID || vbs == BLOCK_INVALID) return;
        Span<uint> hv = stackalloc uint[4], vv = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            hv[i] = GetPerPixelVariance(src[(i * BlockSizeHigh[hbs] * stride)..], stride, hbs, bitDepth);
            vv[i] = GetPerPixelVariance(src[(i * BlockSizeWide[vbs])..], stride, vbs, bitDepth);
        }
        Prune4Partition(bsize, partCtx, bestRd, horzRd, vertRd, splitRd, pbSourceVariance, hv, vv, frameWidth, frameHeight,
            ml4PartitionSearchLevelIndex, part4Allowed);
    }

    // ---- av1_ml_early_term_after_split ----

    /// <summary>One node of get_min_bsize (partition_strategy.c): folds a SIMPLE_MOTION_DATA_TREE node's partition into
    /// minBw / minBh (log2 mi units). Returns true for PARTITION_SPLIT, whose four children the caller then visits.</summary>
    internal static bool GetMinBsizeNode(int bsize, int partitioning, ref int minBw, ref int minBh)
    {
        if (bsize == BLOCK_4X4) { minBw = 0; minBh = 0; return false; }
        if (partitioning == PARTITION_INVALID) return false;
        if (partitioning == PARTITION_SPLIT) return true;
        if (partitioning == PARTITION_HORZ_A || partitioning == PARTITION_HORZ_B ||
            partitioning == PARTITION_VERT_A || partitioning == PARTITION_VERT_B)
            partitioning = PARTITION_SPLIT;
        int subsize = GetPartitionSubsize(bsize, partitioning);
        if (subsize != BLOCK_INVALID)
        {
            minBw = Math.Min(minBw, MiSizeWideLog2[subsize]);
            minBh = Math.Min(minBh, MiSizeHighLog2[subsize]);
        }
        return false;
    }

    private static void AddRdFeature(long rd, long bestRd, Span<float> features, ref int idx)
    {
        bool rdValid = rd > 0 && rd < long.MaxValue;
        float rdRatio = rdValid ? (float)rd / (float)bestRd : 1.0f;
        features[idx++] = rdValid ? 1f : 0f;
        features[idx++] = rdRatio;
    }

    /// <summary>av1_ml_early_term_after_split (libaom calls it on inter frames only). dcQ is av1_dc_quant_QTX(qindex, 0,
    /// bd) >> (bd - 8); minBwBh[2 * i, 2 * i + 1] the get_min_bsize of split child i (starting from MAX_SB_SIZE_LOG2);
    /// smsNoneFeat1 / splitSmsNoneFeat1[4] / smsRectFeat[8] the simple-motion-search features (sms_none_feat[1] of the
    /// node and its split children, sms_rect_feat). Returns the new terminate_partition_search.</summary>
    internal static bool EarlyTermAfterSplit(int bsize, int frameWidth, int frameHeight, int mlEarlyTermAfterPartSplitLevel,
        int dcQ, long bestRd, long partNoneRd, long partSplitRd, ReadOnlySpan<long> splitBlockRd, ReadOnlySpan<int> minBwBh,
        uint smsNoneFeat1, ReadOnlySpan<uint> splitSmsNoneFeat1, ReadOnlySpan<uint> smsRectFeat, bool terminatePartitionSearch)
    {
        if (bestRd <= 0 || bestRd == long.MaxValue || terminatePartitionSearch) return terminatePartitionSearch;
        bool is480pOrLarger = Math.Min(frameWidth, frameHeight) >= 480;
        AomNnConfig? cfg = null;
        float thresh = -1e6f;
        switch (bsize)
        {
            case BLOCK_128X128:
            case BLOCK_64X64: cfg = AomMlModels.EarlyTermAfterSplitNnconfig64; thresh = is480pOrLarger ? -2.0f : -1.2f; break;
            case BLOCK_32X32: cfg = AomMlModels.EarlyTermAfterSplitNnconfig32; thresh = is480pOrLarger ? -2.6f : -2.3f; break;
            case BLOCK_16X16: cfg = AomMlModels.EarlyTermAfterSplitNnconfig16; thresh = is480pOrLarger ? -2.0f : -2.4f; break;
            case BLOCK_8X8: cfg = AomMlModels.EarlyTermAfterSplitNnconfig8; thresh = is480pOrLarger ? -1.0f : -1.4f; break;
        }
        if (cfg == null) return terminatePartitionSearch;
        if (mlEarlyTermAfterPartSplitLevel < 2) thresh -= 0.3f;

        int bs = BlockSizeWide[bsize];
        int f = 0;
        Span<float> features = stackalloc float[31];
        features.Clear();
        features[f++] = Log1pf((float)dcQ / 4.0f);
        features[f++] = Log1pf((float)bestRd / bs / bs / 1024.0f);
        AddRdFeature(partNoneRd, bestRd, features, ref f);
        AddRdFeature(partSplitRd, bestRd, features, ref f);
        for (int i = 0; i < 4; ++i)
        {
            AddRdFeature(splitBlockRd[i], bestRd, features, ref f);
            features[f++] = minBwBh[2 * i];
            features[f++] = minBwBh[2 * i + 1];
        }
        features[f++] = Log1pf((float)smsNoneFeat1);
        for (int i = 0; i < 4; i++) features[f++] = Log1pf((float)splitSmsNoneFeat1[i]);
        features[f++] = Log1pf((float)smsRectFeat[1]);
        features[f++] = Log1pf((float)smsRectFeat[3]);
        features[f++] = Log1pf((float)smsRectFeat[5]);
        features[f++] = Log1pf((float)smsRectFeat[7]);

        Span<float> score = stackalloc float[1];
        score[0] = 0f;
        NnPredict(features, cfg, true, score);
        return score[0] < thresh || terminatePartitionSearch;
    }
}
