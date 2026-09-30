using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>x->part_search_info's CNN state (block.h PartitionSearchInfo): the per-superblock cache of the intra CNN
/// partition model's outputs. libaom clears Valid at the start of every superblock (encode_rd_sb), and on intra
/// frames also on entering the 64x64 partition search (with QuadTreeIdx = 0) and when a valid partition must be
/// re-searched. QuadTreeIdx follows the recursion of PARTITION_SPLIT on intra frames for bsize &lt;= 64x64:
/// entering child idx (0..3) sets it to 4 * parent + idx + 1; returning restores the parent's value.</summary>
internal sealed class AomCnnPartitionCache
{
    public readonly float[] Buffer = new float[AomMl.CnnOutBufSize];   // cnn_buffer: branch 0, 1, 2, 3 outputs
    public float LogQ;                                                 // log_q (normalised)
    public bool Valid;                                                 // cnn_output_valid
    public int QuadTreeIdx;                                            // quad_tree_idx
}

// libaom's intra-frame CNN partition pruning (partition_strategy.c intra_mode_cnn_partition, cnn.c
// av1_cnn_predict_img_multi_out, x86/cnn_avx2.c av1_cnn_convolve_no_maxpool_padding_valid_avx2) and the
// ML partition-search breakout (av1_ml_predict_breakout).
internal static partial class AomMl
{
    internal const int CnnBranch0OutCh = 20, CnnBranch1OutCh = 4, CnnBranch2OutCh = 20, CnnBranch3OutCh = 20;
    internal const int CnnBranch0OutSize = CnnBranch0OutCh, CnnBranch1OutSize = CnnBranch1OutCh * 2 * 2,
        CnnBranch2OutSize = CnnBranch2OutCh * 4 * 4, CnnBranch3OutSize = CnnBranch3OutCh * 8 * 8;
    internal const int CnnOutBufSize = CnnBranch0OutSize + CnnBranch1OutSize + CnnBranch2OutSize + CnnBranch3OutSize;

    private static readonly int[] QuadToLinear1 = [0, 1, 2, 3];
    private static readonly int[] QuadToLinear2 = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];
    private static readonly int[] QuadToLinear3 =
    [
        0, 1, 8, 9, 2, 3, 10, 11, 16, 17, 24, 25, 18, 19, 26, 27, 4, 5, 12, 13, 6, 7, 14, 15, 20, 21, 28, 29, 22, 23, 30, 31,
        32, 33, 40, 41, 34, 35, 42, 43, 48, 49, 56, 57, 50, 51, 58, 59, 36, 37, 44, 45, 38, 39, 46, 47, 52, 53, 60, 61, 54, 55, 62, 63,
    ];

    /// <summary>av1_dc_quant_QTX(qindex, 0, bitDepth).</summary>
    internal static int DcQuantQtx(int qindex, int bitDepth)
    {
        int q = Math.Clamp(qindex, 0, 255);
        return AomMlModels.DcQLookupQtx[(bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2) * 256 + q];
    }

    // relu (cnn.c): (x < 0) ? 0 : x, which keeps -0
    private static float CnnRelu(float x) => x < 0 ? 0 : x;

    // cnn_convolve_no_maxpool_padding_valid_5x5_avx2 (layer 0: 5x5, stride 4): three blocks per pair of 8-lane
    // registers, then single blocks, each with its own reduction order
    private static void CnnConvolve5x5Avx2(float[] input, int inWidth, int inHeight, int inStride, int inChannels,
        int outChannels, float[] weights, float[] bias, float[] output, int outStride)
    {
        const int fw = 5, skip = 4;
        int cstep = inChannels * outChannels;
        Span<float> w = stackalloc float[25];
        Span<float> a0 = stackalloc float[8], a1 = stackalloc float[8];
        for (int i = 0; i < outChannels; i++)
        {
            float outChBias = bias[i];
            for (int k = 0; k < inChannels; k++)
            {
                int off = k * outChannels + i;
                for (int t = 0; t < 25; t++) { w[t] = weights[off]; off += cstep; }
                int plane = k * inStride * inHeight;
                for (int h = 0, u = 0; h < inHeight - fw + 1; h += skip, ++u)
                {
                    int outH = i * outStride * outStride + u * outStride;   // channel planes are outStride x outStride
                    int v = 0, x = 0, rem = inWidth;
                    while (rem >= skip * 2 + fw)
                    {
                        a0.Clear(); a1.Clear();
                        for (int row = 0; row < 5; row++)
                        {
                            int p = plane + (h + row) * inStride + x;
                            int wr = row * 5;
                            // load_src_0 = p[0..7] permuted {0,1,2,3,4,4,5,6} x [w0 w1 w2 w3 w4 w0 w1 w2]
                            a0[0] = input[p + 0] * w[wr + 0] + a0[0];
                            a0[1] = input[p + 1] * w[wr + 1] + a0[1];
                            a0[2] = input[p + 2] * w[wr + 2] + a0[2];
                            a0[3] = input[p + 3] * w[wr + 3] + a0[3];
                            a0[4] = input[p + 4] * w[wr + 4] + a0[4];
                            a0[5] = input[p + 4] * w[wr + 0] + a0[5];
                            a0[6] = input[p + 5] * w[wr + 1] + a0[6];
                            a0[7] = input[p + 6] * w[wr + 2] + a0[7];
                            // load_src_1 = p[7..14] permuted {0,1,1,2,3,4,5,0} x [w3 w4 w0 w1 w2 w3 w4 w0]
                            a1[0] = input[p + 7] * w[wr + 3] + a1[0];
                            a1[1] = input[p + 8] * w[wr + 4] + a1[1];
                            a1[2] = input[p + 8] * w[wr + 0] + a1[2];
                            a1[3] = input[p + 9] * w[wr + 1] + a1[3];
                            a1[4] = input[p + 10] * w[wr + 2] + a1[4];
                            a1[5] = input[p + 11] * w[wr + 3] + a1[5];
                            a1[6] = input[p + 12] * w[wr + 4] + a1[6];
                            a1[7] = input[p + 7] * w[wr + 0] + a1[7];
                        }
                        float l0 = a0[0] + a0[1], l1 = a0[2] + a0[3], l2 = a1[0] + a1[1], l3 = a1[2] + a1[3];   // accum_l
                        float h1 = a0[6] + a0[7], h2 = a1[4] + a1[5];                                           // accum_h
                        output[outH + v] = outChBias + (l0 + a0[4]) + l1;
                        output[outH + v + 1] = outChBias + (a0[5] + h1) + l2;
                        output[outH + v + 2] = outChBias + (a1[6] + h2) + l3;
                        v += 3; x += skip * 3; rem -= skip * 3;
                    }
                    while (rem >= fw)
                    {
                        float last = 0;
                        Span<float> acc = stackalloc float[4];
                        for (int row = 0; row < 5; row++)
                            last += input[plane + (h + row) * inStride + x + 4] * w[row * 5 + 4];
                        for (int j = 0; j < 4; j++)
                        {
                            float m0 = input[plane + h * inStride + x + j] * w[j];
                            float m1 = input[plane + (h + 1) * inStride + x + j] * w[5 + j];
                            float m2 = input[plane + (h + 2) * inStride + x + j] * w[10 + j];
                            float m3 = input[plane + (h + 3) * inStride + x + j] * w[15 + j];
                            float m4 = input[plane + (h + 4) * inStride + x + j] * w[20 + j];
                            acc[j] = (m0 + 0f) + ((m1 + m2) + (m3 + m4));
                        }
                        output[outH + v] = outChBias + last + (acc[0] + acc[1]) + (acc[2] + acc[3]);
                        v += 1; x += skip; rem -= skip;
                    }
                }
            }
        }
    }

    // one 2x2 (stride 2) block as the layer 1 / 2 AVX2 kernels reduce it: (p00 w0 + p10 w2) + (p01 w1 + p11 w3)
    private static float Cnn2x2Avx2(float p00, float p01, float p10, float p11, ReadOnlySpan<float> w) =>
        (p00 * w[0] + p10 * w[2]) + (p01 * w[1] + p11 * w[3]);

    // cnn_convolve_no_maxpool_padding_valid_layer1_avx2 / layer2_avx2: per output, bias + the in-channels in order
    private static void CnnConvolve2x2Avx2(float[] input, int inSize, int inChannels, int outChannels, float[] weights,
        float[] bias, float[] output, int outBase, int outStride)
    {
        int cstep = inChannels * outChannels, outSize = inSize / 2;
        Span<float> w = stackalloc float[4];
        Span<float> acc = stackalloc float[64];
        for (int i = 0; i < outChannels; i++)
        {
            acc[..(outSize * outSize)].Fill(bias[i]);
            for (int k = 0; k < inChannels; k++)
            {
                int off = k * outChannels + i;
                for (int t = 0; t < 4; t++) { w[t] = weights[off]; off += cstep; }
                int plane = k * inSize * inSize;
                for (int y = 0; y < outSize; y++)
                    for (int x = 0; x < outSize; x++)
                    {
                        int p = plane + 2 * y * inSize + 2 * x;
                        acc[y * outSize + x] += Cnn2x2Avx2(input[p], input[p + 1], input[p + inSize], input[p + inSize + 1], w);
                    }
            }
            for (int j = 0; j < outSize * outSize; j++) output[outBase + i * outStride * outStride + j] = acc[j];
        }
    }

    // av1_cnn_convolve_no_maxpool_padding_valid_c for a 2x2 stride-2 layer (layers 3 and 4)
    private static void CnnConvolve2x2C(float[] input, int inBase, int inSize, int inChannels, int outChannels,
        float[] weights, float[] bias, float[] output, int outBase)
    {
        int cstep = inChannels * outChannels, outSize = inSize / 2;
        for (int i = 0; i < outChannels; i++)
            for (int y = 0; y < outSize; y++)
                for (int x = 0; x < outSize; x++)
                {
                    float sum = bias[i];
                    for (int k = 0; k < inChannels; k++)
                    {
                        int off = k * outChannels + i;
                        int p = inBase + k * inSize * inSize;
                        for (int ii = 2 * y; ii < 2 * y + 2; ++ii)
                            for (int jj = 2 * x; jj < 2 * x + 2; ++jj)
                            {
                                sum += weights[off] * input[p + ii * inSize + jj];
                                off += cstep;
                            }
                    }
                    output[outBase + i * outSize * outSize + y * outSize + x] = sum;
                }
    }

    private static void CnnActivate(float[] buf, int start, int count)
    {
        for (int j = start; j < start + count; j++) buf[j] = CnnRelu(buf[j]);
    }

    // av1_cnn_predict_c over av1_intra_mode_cnn_partition_cnn_config from the 65x65 float input
    private static void CnnPartitionPredict(float[] input, float[] cnnBuffer)
    {
        const int b1 = CnnBranch0OutSize, b2 = b1 + CnnBranch1OutSize, b3 = b2 + CnnBranch2OutSize;
        var l0 = new float[20 * 16 * 16];
        // layer 0: 65x65x1 -> 16x16x20
        CnnConvolve5x5Avx2(input, 65, 65, 65, 1, 20, AomMlModels.IntraModeCnnPartitionCnnLayer0Kernel,
            AomMlModels.IntraModeCnnPartitionCnnLayer0Bias, l0, 16);
        CnnActivate(l0, 0, l0.Length);
        // layer 1: 16x16x20 -> 8x8x20 (output 3)
        CnnConvolve2x2Avx2(l0, 16, 20, 20, AomMlModels.IntraModeCnnPartitionCnnLayer1Kernel,
            AomMlModels.IntraModeCnnPartitionCnnLayer1Bias, cnnBuffer, b3, 8);
        CnnActivate(cnnBuffer, b3, CnnBranch3OutSize);
        // layer 2: 8x8x20 -> 4x4x20 (output 2)
        var l1 = cnnBuffer.AsSpan(b3, CnnBranch3OutSize).ToArray();
        CnnConvolve2x2Avx2(l1, 8, 20, 20, AomMlModels.IntraModeCnnPartitionCnnLayer2Kernel,
            AomMlModels.IntraModeCnnPartitionCnnLayer2Bias, cnnBuffer, b2, 4);
        CnnActivate(cnnBuffer, b2, CnnBranch2OutSize);
        // layer 3: 4x4x20 -> 2x2x4 (output 1), C kernel
        CnnConvolve2x2C(cnnBuffer, b2, 4, 20, 4, AomMlModels.IntraModeCnnPartitionCnnLayer3Kernel,
            AomMlModels.IntraModeCnnPartitionCnnLayer3Bias, cnnBuffer, b1);
        CnnActivate(cnnBuffer, b1, CnnBranch1OutSize);
        // layer 4: 2x2x4 -> 1x1x20 (output 0), C kernel
        CnnConvolve2x2C(cnnBuffer, b1, 2, 4, 20, AomMlModels.IntraModeCnnPartitionCnnLayer4Kernel,
            AomMlModels.IntraModeCnnPartitionCnnLayer4Bias, cnnBuffer, 0);
        CnnActivate(cnnBuffer, 0, CnnBranch0OutSize);
    }

    /// <summary>The CNN half of intra_mode_cnn_partition for one 64x64 superblock: av1_cnn_predict_img_multi_out
    /// (lowbd). src starts one row above and one column left of the 64x64 luma block (65x65 samples are read).
    /// Fills cnnBuffer[CnnOutBufSize] (branch 0: 20 x 1x1, branch 1: 4 x 2x2, branch 2: 20 x 4x4, branch 3: 20 x 8x8).</summary>
    internal static void CnnPartitionPredict(ReadOnlySpan<byte> src, int stride, float[] cnnBuffer)
    {
        const float maxVal = 255.0f;
        var input = new float[65 * 65];
        for (int i = 0; i < 65; i++)
            for (int j = 0; j < 65; j++) input[i * 65 + j] = (float)src[i * stride + j] / maxVal;
        CnnPartitionPredict(input, cnnBuffer);
    }

    /// <summary>av1_cnn_predict_img_multi_out_highbd for the partition CNN (see the lowbd overload).</summary>
    internal static void CnnPartitionPredict(ReadOnlySpan<ushort> src, int stride, int bitDepth, float[] cnnBuffer)
    {
        float maxVal = (float)((1 << bitDepth) - 1);
        var input = new float[65 * 65];
        for (int i = 0; i < 65; i++)
            for (int j = 0; j < 65; j++) input[i * 65 + j] = (float)src[i * stride + j] / maxVal;
        CnnPartitionPredict(input, cnnBuffer);
    }

    /// <summary>part_info->log_q: the normalised log1p(dc_q^2 / 256), dc_q = av1_dc_quant_QTX(qindex, 0, bd) &gt;&gt; (bd - 8).</summary>
    internal static float CnnPartitionLogQ(int qindex, int bitDepth)
    {
        int dcQ = DcQuantQtx(qindex, bitDepth) >> (bitDepth - 8);
        float logQ = Log1pf((float)(dcQ * dcQ) / 256.0f);
        return (logQ - AomMlModels.IntraModeCnnPartitionMean[0]) / AomMlModels.IntraModeCnnPartitionStd[0];
    }

    /// <summary>The NN-head half of intra_mode_cnn_partition: the branch logit of a block (bsize 8x8..64x64 at
    /// quadTreeIdx) from the cached CNN outputs.</summary>
    internal static float CnnPartitionLogit(ReadOnlySpan<float> cnnBuffer, float logQ, int bsize, int quadTreeIdx)
    {
        const int b1 = CnnBranch0OutSize, b2 = b1 + CnnBranch1OutSize, b3 = b2 + CnnBranch2OutSize;
        Span<float> f = stackalloc float[100];
        int fi = 0;
        AomNnConfig cfg;
        if (bsize == BLOCK_64X64)
        {
            cfg = AomMlModels.IntraModeCnnPartitionBranch0DnnConfig;
            for (int c = 0; c < CnnBranch0OutCh; c++) f[fi++] = cnnBuffer[c];
            for (int lin = 0; lin < 4; lin++)
                for (int c = 0; c < CnnBranch1OutCh; c++) f[fi++] = cnnBuffer[b1 + lin + c * 4];
        }
        else if (bsize == BLOCK_32X32)
        {
            cfg = AomMlModels.IntraModeCnnPartitionBranch1DnnConfig;
            for (int c = 0; c < CnnBranch0OutCh; c++) f[fi++] = cnnBuffer[c];
            int lin = QuadToLinear1[quadTreeIdx - 1];
            for (int c = 0; c < CnnBranch1OutCh; c++) f[fi++] = cnnBuffer[b1 + lin + c * 4];
        }
        else if (bsize == BLOCK_16X16)
        {
            cfg = AomMlModels.IntraModeCnnPartitionBranch2DnnConfig;
            int prev = QuadToLinear1[(quadTreeIdx - 1) / 4 - 1];
            for (int c = 0; c < CnnBranch1OutCh; c++) f[fi++] = cnnBuffer[b1 + prev + c * 4];
            int lin = QuadToLinear2[quadTreeIdx - 5];
            for (int c = 0; c < CnnBranch2OutCh; c++) f[fi++] = cnnBuffer[b2 + lin + c * 16];
        }
        else if (bsize == BLOCK_8X8)
        {
            cfg = AomMlModels.IntraModeCnnPartitionBranch3DnnConfig;
            int prev = QuadToLinear2[(quadTreeIdx - 1) / 4 - 5];
            for (int c = 0; c < CnnBranch2OutCh; c++) f[fi++] = cnnBuffer[b2 + prev + c * 16];
            int lin = QuadToLinear3[quadTreeIdx - 21];
            for (int c = 0; c < CnnBranch3OutCh; c++) f[fi++] = cnnBuffer[b3 + lin + c * 64];
        }
        else throw new ArgumentOutOfRangeException(nameof(bsize));
        f[fi++] = logQ;
        Span<float> logits = stackalloc float[4];
        logits.Clear();
        NnPredict(f, cfg, true, logits);
        return logits[0];
    }

    /// <summary>intra_mode_cnn_partition (partition_strategy.c), called by av1_prune_partitions_before_search on intra
    /// frames when sf.part_sf.intra_cnn_based_part_prune_level != 0, the superblock is at least 64x64, bsize &lt;= 64x64
    /// and at least 8x8, and the block is wholly inside the frame. On a 64x64 block with an invalid cache it runs the
    /// CNN (src: one row above and one column left of the 64x64 luma block, 65x65 samples). Updates the partition
    /// state as libaom does: a logit above the split threshold clears partitionNoneAllowed (unless pruneLevel == 1),
    /// sets doSquareSplit and disables the rect partitions; one below the no-split threshold clears doSquareSplit.</summary>
    internal static void IntraModeCnnPartition(AomCnnPartitionCache cache, ReadOnlySpan<byte> src, int stride, int qindex,
        int bsize, int frameWidth, int frameHeight, int intraCnnBasedPartPruneLevel, ref int partitionNoneAllowed,
        ref int doSquareSplit, ref int doRectangularSplit, Span<int> partitionRectAllowed)
    {
        if (bsize == BLOCK_128X128) return;
        if (bsize == BLOCK_64X64 && !cache.Valid)
        {
            cache.LogQ = CnnPartitionLogQ(qindex, 8);
            CnnPartitionPredict(src, stride, cache.Buffer);
            cache.Valid = true;
        }
        CnnPartitionDecide(cache, bsize, frameWidth, frameHeight, intraCnnBasedPartPruneLevel, ref partitionNoneAllowed,
            ref doSquareSplit, ref doRectangularSplit, partitionRectAllowed);
    }

    /// <summary>intra_mode_cnn_partition for a high-bitdepth source (see the lowbd overload).</summary>
    internal static void IntraModeCnnPartition(AomCnnPartitionCache cache, ReadOnlySpan<ushort> src, int stride,
        int bitDepth, int qindex, int bsize, int frameWidth, int frameHeight, int intraCnnBasedPartPruneLevel,
        ref int partitionNoneAllowed, ref int doSquareSplit, ref int doRectangularSplit, Span<int> partitionRectAllowed)
    {
        if (bsize == BLOCK_128X128) return;
        if (bsize == BLOCK_64X64 && !cache.Valid)
        {
            cache.LogQ = CnnPartitionLogQ(qindex, bitDepth);
            CnnPartitionPredict(src, stride, bitDepth, cache.Buffer);
            cache.Valid = true;
        }
        CnnPartitionDecide(cache, bsize, frameWidth, frameHeight, intraCnnBasedPartPruneLevel, ref partitionNoneAllowed,
            ref doSquareSplit, ref doRectangularSplit, partitionRectAllowed);
    }

    private static void CnnPartitionDecide(AomCnnPartitionCache cache, int bsize, int frameWidth, int frameHeight,
        int pruneLevel, ref int partitionNoneAllowed, ref int doSquareSplit, ref int doRectangularSplit,
        Span<int> partitionRectAllowed)
    {
        if (!cache.Valid) return;
        int bsizeIdx = ConvertBsizeToIdx(bsize);
        float logit = CnnPartitionLogit(cache.Buffer, cache.LogQ, bsize, cache.QuadTreeIdx);
        int minWh = Math.Min(frameWidth, frameHeight);
        float splitOnly, noSplit;
        if (minWh >= 720)
        {
            splitOnly = AomMlModels.IntraModeCnnPartitionSplitThreshHdres[bsizeIdx];
            noSplit = AomMlModels.IntraModeCnnPartitionNoSplitThreshHdres[bsizeIdx];
        }
        else if (minWh >= 480)
        {
            splitOnly = AomMlModels.IntraModeCnnPartitionSplitThreshMidres[bsizeIdx];
            noSplit = AomMlModels.IntraModeCnnPartitionNoSplitThreshMidres[bsizeIdx];
        }
        else
        {
            splitOnly = AomMlModels.IntraModeCnnPartitionSplitThreshLowres[bsizeIdx];
            noSplit = AomMlModels.IntraModeCnnPartitionNoSplitThreshLowres[bsizeIdx];
        }
        if (logit > splitOnly)
        {
            if (pruneLevel != 1) partitionNoneAllowed = 0;
            doSquareSplit = 1;
            doRectangularSplit = 0;                        // av1_disable_rect_partitions
            partitionRectAllowed[HORZ] = 0;
            partitionRectAllowed[VERT] = 0;
        }
        if (logit < noSplit) doSquareSplit = 0;            // av1_disable_square_split_partition
    }

    // ---- av1_ml_predict_breakout ----

    /// <summary>av1_ml_predict_breakout (partition_strategy.c; libaom calls it from prune_partitions_after_none on
    /// non-intra frames, for 4x4 &lt; bsize &lt;= use_square_partition_only_threshold with ml_predict_breakout_level
    /// &gt;= 1). rate / dist are PARTITION_NONE's RD_STATS, rdmult x->rdmult, qindex the block's (dc dequant
    /// x->plane[0].dequant_QTX[0] = av1_dc_quant_QTX(qindex, 0, bd)), breakoutThresh[5] and modelIndex
    /// sf.part_sf.ml_partition_search_breakout_thresh / _model_index. Clears doSquareSplit and doRectangularSplit
    /// when the model predicts a breakout.</summary>
    internal static void MlPredictBreakout(int bsize, int rate, long dist, uint pbSourceVariance, int rdmult, int qindex,
        int bitDepth, ReadOnlySpan<float> breakoutThresh, int modelIndex, int mlPredictBreakoutLevel,
        ref int doSquareSplit, ref int doRectangularSplit)
    {
        int bsizeIdx = ConvertBsizeToIdx(bsize);
        if (bsizeIdx < 0) return;
        float[]? mlMean = AomMlModels.HdPartitionBreakoutNnMean[bsizeIdx], mlStd = AomMlModels.HdPartitionBreakoutNnStd[bsizeIdx];
        AomNnConfig cfg = bsize switch
        {
            BLOCK_8X8 => AomMlModels.PartitionBreakoutNnconfig8[modelIndex],
            BLOCK_16X16 => AomMlModels.PartitionBreakoutNnconfig16[modelIndex],
            BLOCK_32X32 => AomMlModels.PartitionBreakoutNnconfig32[modelIndex],
            BLOCK_64X64 => AomMlModels.PartitionBreakoutNnconfig64[modelIndex],
            _ => AomMlModels.PartitionBreakoutNnconfig128[modelIndex],
        };
        float thresh = breakoutThresh[bsizeIdx];
        if (thresh < 0) return;
        ReadOnlySpan<float> scale = [1.15f, 1.05f, 1.0f];
        thresh = thresh * scale[mlPredictBreakoutLevel - 1];

        Span<float> features = stackalloc float[4];
        int npl = NumPelsLog2Lookup[bsize];
        float rateF = (float)Math.Min(rate, int.MaxValue);
        rateF = ((float)rdmult / 128.0f / 512.0f / (float)(1 << npl)) * rateF;
        features[0] = modelIndex != 0 ? Log1pf(rateF) : rateF;
        float distF = (float)(Math.Min(dist, int.MaxValue) >> npl);
        features[1] = modelIndex != 0 ? Log1pf(distF) : distF;
        features[2] = modelIndex != 0 ? Log1pf((float)pbSourceVariance) : (float)pbSourceVariance;
        int dcQ = DcQuantQtx(qindex, bitDepth) >> (bitDepth - 8);
        features[3] = modelIndex != 0 ? Log1pf((float)(dcQ * dcQ) / 256.0f) : (float)(dcQ * dcQ) / 256.0f;
        if (modelIndex != 0)
            for (int i = 0; i < 4; i++) features[i] = (features[i] - mlMean![i]) / mlStd![i];

        Span<float> score = stackalloc float[1];
        score[0] = 0f;
        NnPredict(features, cfg, true, score);
        float threshScore = (float)Math.Log(thresh / (1 - thresh));
        if (score[0] >= threshScore)
        {
            doSquareSplit = 0;
            doRectangularSplit = 0;
        }
    }
}
