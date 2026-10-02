using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// The transform-search ML prunes (av1/encoder/tx_search.c) and their feature extraction. They read the luma residual
// (MACROBLOCK::plane[0].src_diff, stride block_size_wide[plane_bsize]); here they take that residual directly, already
// offset to the transform block (src_diff + 4 * blk_row * stride + 4 * blk_col).
internal static partial class AomMl
{
    internal const int TX_TYPE_PRUNE_0 = 0, TX_TYPE_PRUNE_1 = 1, TX_TYPE_PRUNE_2 = 2, TX_TYPE_PRUNE_3 = 3,
        TX_TYPE_PRUNE_4 = 4, TX_TYPE_PRUNE_5 = 5;
    internal const int TX_PRUNE_NONE = 0, TX_PRUNE_LARGEST = 1, TX_PRUNE_SPLIT = 2;
    internal const int NUM_INTRA_TX_SPLIT_FEATURES = 14;

    /// <summary>av1_get_horver_correlation_full (dispatched: av1_get_horver_correlation_full_avx2): the horizontal
    /// and vertical correlation of a residual block (w, h powers of two, at least 4). The AVX2 kernel's 32-bit lane
    /// sums (which wrap exactly as here) and its edge handling are reproduced.</summary>
    internal static void GetHorverCorrelationFull(ReadOnlySpan<short> diff, int stride, int width, int height,
        out float hcorr, out float vcorr)
    {
        long xySum = 0, xzSum = 0, xSum = 0, x2Sum = 0;
        Span<int> xy = stackalloc int[8], xz = stackalloc int[8], xs = stackalloc int[8], x2 = stackalloc int[8];
        for (int i = 0; i <= height - 4; i += 3)
        {
            xy.Clear(); xz.Clear(); xs.Clear(); x2.Clear();
            for (int j = 0; j <= width - 4; j += 3)
            {
                // horver_correlation_4x4: 64-bit group g holds row 3 - g of the 4x4, shifted left one pixel (slli)
                for (int g = 0; g < 4; g++)
                {
                    int r = 3 - g, o = (i + r) * stride + j;
                    int p0 = diff[o], p1 = diff[o + 1], p2 = diff[o + 2], p3 = diff[o + 3];
                    unchecked
                    {
                        xy[2 * g] += p1 * p0;
                        xy[2 * g + 1] += p2 * p1 + p3 * p2;
                        xs[2 * g] += p0;
                        xs[2 * g + 1] += p1 + p2;
                        x2[2 * g] += p0 * p0;
                        x2[2 * g + 1] += p1 * p1 + p2 * p2;
                        // permute4x64(slli, 0x90): group g pairs with the row below it (group 0 with itself)
                        int rb = g == 0 ? r : r + 1, ob = (i + rb) * stride + j;
                        int q0 = diff[ob], q1 = diff[ob + 1], q2 = diff[ob + 2];
                        xz[2 * g] += p0 * q0;
                        xz[2 * g + 1] += p1 * q1 + p2 * q2;
                    }
                }
            }
            unchecked
            {
                // _mm256_hadd_epi32 then lanes 5 + 4 + 1 (xy, x) and 7 + 6 + 3 (xz, x2)
                xySum += (long)(xy[6] + xy[7]) + (xy[4] + xy[5]) + (xy[2] + xy[3]);
                xzSum += (long)(xz[6] + xz[7]) + (xz[4] + xz[5]) + (xz[2] + xz[3]);
                xSum += (long)(xs[6] + xs[7]) + (xs[4] + xs[5]) + (xs[2] + xs[3]);
                x2Sum += (long)(x2[6] + x2[7]) + (x2[4] + x2[5]) + (x2[2] + x2[3]);
            }
        }

        long xFinalRow = 0, xFinalCol = 0, x2FinalRow = 0, x2FinalCol = 0;
        unchecked
        {
            if (height % 3 == 1)
            {
                int x0 = diff[(height - 1) * stride];
                xSum += x0; xFinalRow += x0; x2Sum += x0 * x0; x2FinalRow += x0 * x0;
                for (int j = 0; j < width - 1; ++j)
                {
                    int x = diff[(height - 1) * stride + j], y = diff[(height - 1) * stride + j + 1];
                    xySum += x * y; xSum += y; x2Sum += y * y; xFinalRow += y; x2FinalRow += y * y;
                }
            }
            else
            {
                int x0 = diff[(height - 2) * stride], z0 = diff[(height - 1) * stride];
                xSum += x0 + z0; x2Sum += x0 * x0 + z0 * z0; xFinalRow += z0; x2FinalRow += z0 * z0;
                for (int j = 0; j < width - 1; ++j)
                {
                    int x = diff[(height - 2) * stride + j], y = diff[(height - 2) * stride + j + 1];
                    int z = diff[(height - 1) * stride + j], w = diff[(height - 1) * stride + j + 1];
                    xySum += x * y; xzSum += x * z; xySum += z * w;
                    xSum += y + w; x2Sum += y * y + w * w; xFinalRow += w; x2FinalRow += w * w;
                }
            }

            int lastRows = height - (height % 3 == 1 ? 2 : 3);
            if (width % 3 == 1)
            {
                int x0 = diff[width - 1];
                xSum += x0; xFinalCol += x0; x2Sum += x0 * x0; x2FinalCol += x0 * x0;
                for (int i = 0; i < height - 1; ++i)
                {
                    int x = diff[i * stride + width - 1], z = diff[(i + 1) * stride + width - 1];
                    xzSum += x * z; xFinalCol += z; x2FinalCol += z * z;
                    if (i < lastRows) { xSum += z; x2Sum += z * z; }
                }
            }
            else
            {
                int x0 = diff[width - 2], y0 = diff[width - 1];
                xSum += x0 + y0; x2Sum += x0 * x0 + y0 * y0; xFinalCol += y0; x2FinalCol += y0 * y0;
                for (int i = 0; i < height - 1; ++i)
                {
                    int x = diff[i * stride + width - 2], y = diff[i * stride + width - 1];
                    int z = diff[(i + 1) * stride + width - 2], w = diff[(i + 1) * stride + width - 1];
                    if (i < height - 2 || height % 3 == 1) { xySum += x * y; xzSum += x * z; }
                    xFinalCol += w; x2FinalCol += w * w;
                    if (i < lastRows) { xSum += z + w; x2Sum += z * z + w * w; }
                    xzSum += y * w;
                }
            }
        }

        long xFirstRow = 0, xFirstCol = 0, x2FirstRow = 0, x2FirstCol = 0;
        for (int j = 0; j < width; ++j) { int d = diff[j]; xFirstRow += d; x2FirstRow += d * d; }
        for (int i = 0; i < height; ++i) { int d = diff[i * stride]; xFirstCol += d; x2FirstCol += d * d; }

        long xhorSum = xSum - xFinalCol, xverSum = xSum - xFinalRow;
        long ySum = xSum - xFirstCol, zSum = xSum - xFirstRow;
        long x2horSum = x2Sum - x2FinalCol, x2verSum = x2Sum - x2FinalRow;
        long y2Sum = x2Sum - x2FirstCol, z2Sum = x2Sum - x2FirstRow;

        float numHor = height * (width - 1), numVer = (height - 1) * width;
        float xhorVarN = x2horSum - (float)(xhorSum * xhorSum) / numHor;
        float xverVarN = x2verSum - (float)(xverSum * xverSum) / numVer;
        float yVarN = y2Sum - (float)(ySum * ySum) / numHor;
        float zVarN = z2Sum - (float)(zSum * zSum) / numVer;
        float xyVarN = xySum - (float)(xhorSum * ySum) / numHor;
        float xzVarN = xzSum - (float)(xverSum * zSum) / numVer;

        if (xhorVarN > 0 && yVarN > 0)
        {
            hcorr = xyVarN / MathF.Sqrt(xhorVarN * yVarN);
            hcorr = hcorr < 0 ? 0 : hcorr;
        }
        else hcorr = 1.0f;
        if (xverVarN > 0 && zVarN > 0)
        {
            vcorr = xzVarN / MathF.Sqrt(xverVarN * zVarN);
            vcorr = vcorr < 0 ? 0 : vcorr;
        }
        else vcorr = 1.0f;
    }

    /// <summary>get_energy_distribution_finer (tx_search.c): normalised 1D projections of the (downscaled) residual
    /// energy; writes esq_w - 1 values to hordist and esq_h - 1 to verdist.</summary>
    internal static void GetEnergyDistributionFiner(ReadOnlySpan<short> diff, int stride, int bw, int bh,
        Span<float> hordist, Span<float> verdist)
    {
        Span<uint> esq = stackalloc uint[256];
        int wShift = bw <= 8 ? 0 : 1, hShift = bh <= 8 ? 0 : 1;
        int esqW = bw >> wShift, esqH = bh >> hShift, esqSz = esqW * esqH;
        esq[..esqSz].Clear();
        unchecked
        {
            if (wShift != 0)
            {
                for (int i = 0; i < bh; i++)
                {
                    int row = (i >> hShift) * esqW, d = i * stride;
                    for (int j = 0; j < bw; j += 2)
                        esq[row + (j >> 1)] += (uint)(diff[d + j] * diff[d + j] + diff[d + j + 1] * diff[d + j + 1]);
                }
            }
            else
            {
                for (int i = 0; i < bh; i++)
                {
                    int row = (i >> hShift) * esqW, d = i * stride;
                    for (int j = 0; j < bw; j++) esq[row + j] += (uint)(diff[d + j] * diff[d + j]);
                }
            }
        }

        ulong total = 0;
        for (int i = 0; i < esqSz; i++) total += esq[i];

        if (total == 0)
        {
            float horVal = 1.0f / esqW;
            for (int j = 0; j < esqW - 1; j++) hordist[j] = horVal;
            float verVal = 1.0f / esqH;
            for (int i = 0; i < esqH - 1; i++) verdist[i] = verVal;
            return;
        }

        float eRecip = 1.0f / (float)(long)total;
        hordist[..(esqW - 1)].Clear();
        verdist[..(esqH - 1)].Clear();
        int ii, jj;
        for (ii = 0; ii < esqH - 1; ii++)
        {
            int row = ii * esqW;
            for (jj = 0; jj < esqW - 1; jj++)
            {
                hordist[jj] += (float)(long)esq[row + jj];
                verdist[ii] += (float)(long)esq[row + jj];
            }
            verdist[ii] += (float)(long)esq[row + jj];
        }
        {
            int row = ii * esqW;
            for (jj = 0; jj < esqW - 1; jj++) hordist[jj] += (float)(long)esq[row + jj];
        }
        for (jj = 0; jj < esqW - 1; jj++) hordist[jj] *= eRecip;
        for (ii = 0; ii < esqH - 1; ii++) verdist[ii] *= eRecip;
    }

    /// <summary>aom_get_blk_sse_sum (dispatched: aom_get_blk_sse_sum_avx2). For heights that are a multiple of 4 the AVX2
    /// kernel sums the squares in eight 32-bit lanes (pairs of horizontally adjacent samples, accumulated down the
    /// rows, then zero-extended), so for extreme residuals its x2_sum is the lane sums mod 2^32; that is reproduced.</summary>
    internal static void GetBlkSseSum(ReadOnlySpan<short> data, int stride, int bw, int bh, out int xSum, out long x2Sum)
    {
        if ((bh & 3) == 0 && (bw == 4 || bw == 8 || bw == 16 || bw == 32 || bw == 64))
        {
            int s = 0;
            long s2 = 0;
            Span<int> lanes = stackalloc int[8];
            unchecked
            {
                if (bw == 64 && bh > 32)
                {
                    s2 += SseLanesWd16(data, stride, 32, 4, lanes, ref s);
                    s2 += SseLanesWd16(data[(32 * stride)..], stride, 32, 4, lanes, ref s);
                }
                else if (bw >= 16) s2 += SseLanesWd16(data, stride, bh, bw >> 4, lanes, ref s);
                else
                {
                    // sse_sum_wd4 (4 rows of 4 per register) / sse_sum_wd8 (2 rows of 8): lane k holds samples 2k, 2k+1
                    // of that 16-sample group
                    lanes.Clear();
                    int rowsPer = bw == 4 ? 4 : 2;
                    for (int r = 0; r < bh; r += rowsPer)
                        for (int k = 0; k < 16; k += 2)
                        {
                            int o = (r + k / bw) * stride + k % bw;
                            int a = data[o], b = data[o + 1];
                            s += a + b;
                            lanes[k >> 1] += a * a + b * b;
                        }
                    for (int k = 0; k < 8; k++) s2 += (uint)lanes[k];
                }
            }
            xSum = s; x2Sum = s2;
            return;
        }
        int cs = 0; long cs2 = 0;
        for (int i = 0; i < bh; ++i)
            for (int j = 0; j < bw; ++j)
            {
                int val = data[i * stride + j];
                cs = unchecked(cs + val);
                cs2 += val * val;
            }
        xSum = cs; x2Sum = cs2;
    }

    // sse_sum_wd16_avx2: 16 samples a row, loopCount 16-wide columns, one set of lane accumulators throughout
    private static long SseLanesWd16(ReadOnlySpan<short> data, int stride, int bh, int loopCount, Span<int> lanes, ref int s)
    {
        lanes.Clear();
        unchecked
        {
            for (int i = 0; i < loopCount; ++i)
                for (int j = 0; j < bh; ++j)
                    for (int k = 0; k < 16; k += 2)
                    {
                        int o = j * stride + 16 * i + k;
                        int a = data[o], b = data[o + 1];
                        s += a + b;
                        lanes[k >> 1] += a * a + b * b;
                    }
        }
        long s2 = 0;
        for (int k = 0; k < 8; k++) s2 += (uint)lanes[k];
        return s2;
    }

    // get_var (tx_search.c)
    private static float GetVar(float mean, double x2Sum, int num)
    {
        float eX2 = (float)(x2Sum / num);
        return eX2 - mean * mean;
    }

    /// <summary>get_blk_var_dev (tx_search.c): the deviation of the means and the variance of the variances of the
    /// block and its halves / quarters.</summary>
    internal static void GetBlkVarDev(ReadOnlySpan<short> data, int stride, int bw, int bh, ref float devOfMean, ref float varOfVars)
    {
        int subh = bh >= bw ? bh >> 1 : bh;
        int subw = bw >= bh ? bw >> 1 : bw;
        int num = bw * bh, subNum = subw * subh;
        int totalXSum = 0;
        long totalX2Sum = 0;
        int blkIdx = 0;
        float varSum = 0.0f, meanSum = 0.0f;
        double var2Sum = 0.0f, mean2Sum = 0.0f;
        for (int row = 0; row < bh; row += subh)
            for (int col = 0; col < bw; col += subw)
            {
                GetBlkSseSum(data[(row * stride + col)..], stride, subw, subh, out int xSum, out long x2Sum);
                totalXSum += xSum;
                totalX2Sum += x2Sum;
                float mean = (float)xSum / subNum;
                float var = GetVar(mean, x2Sum, subNum);
                meanSum += mean;
                mean2Sum += (double)(mean * mean);
                varSum += var;
                var2Sum += var * var;
                blkIdx++;
            }
        float lvl0Mean = (float)totalXSum / num;
        float blockVar = GetVar(lvl0Mean, totalX2Sum, num);
        meanSum += lvl0Mean;
        mean2Sum += (double)(lvl0Mean * lvl0Mean);
        varSum += blockVar;
        var2Sum += blockVar * blockVar;
        float avMean = meanSum / 5;
        if (blkIdx > 1)
        {
            devOfMean = GetDev(avMean, mean2Sum, blkIdx + 1);
            float meanVar = varSum / (blkIdx + 1);
            varOfVars = GetVar(meanVar, var2Sum, blkIdx + 1);
        }
    }

    private static readonly int[] NoSplitThreshScales = { 0, 24, 8, 8 }, SplitThreshScales = { 0, 24, 10, 8 };

    /// <summary>prune_tx_split_no_split (tx_search.c).</summary>
    internal static void PruneTxSplitNoSplit(ReadOnlySpan<short> diff, int diffStride, int bw, int bh, int dequantDc, int dequantAc,
        ref bool tryNoSplit, ref bool trySplit, int pruningLevel)
    {
        float devOfMeans = 0.0f, varOfVars = 0.0f;
        GetBlkVarDev(diff, diffStride, bw, bh, ref devOfMeans, ref varOfVars);
        int dcQ = dequantDc >> 3, acQ = dequantAc >> 3;
        int noSplitThreshScale = NoSplitThreshScales[pruningLevel], splitThreshScale = SplitThreshScales[pruningLevel];
        if (devOfMeans <= dcQ && splitThreshScale * varOfVars <= acQ * acQ) trySplit = false;
        if (devOfMeans > noSplitThreshScale * dcQ && varOfVars > noSplitThreshScale * acQ * acQ) tryNoSplit = false;
    }

    // get_dev (tx_search.c)
    private static float GetDev(float mean, double x2Sum, int num)
    {
        float eX2 = (float)(x2Sum / num);
        float diff = eX2 - mean * mean;
        return diff > 0 ? MathF.Sqrt(diff) : 0;
    }

    /// <summary>get_mean_dev_features (tx_search.c): mean / deviation of the block and its halves or quarters;
    /// returns the number of features written (at most 12).</summary>
    internal static int GetMeanDevFeatures(ReadOnlySpan<short> data, int stride, int bw, int bh, Span<float> features)
    {
        int subh = bh >= bw ? bh >> 1 : bh;
        int subw = bw >= bh ? bw >> 1 : bw;
        int num = bw * bh, subNum = subw * subh;
        int featureIdx = 2;
        int totalXSum = 0;
        long totalX2Sum = 0;
        int numSubBlks = 0;
        double mean2Sum = 0.0;
        float devSum = 0.0f;
        for (int row = 0; row < bh; row += subh)
            for (int col = 0; col < bw; col += subw)
            {
                GetBlkSseSum(data[(row * stride + col)..], stride, subw, subh, out int xSum, out long x2Sum);
                totalXSum += xSum;
                totalX2Sum += x2Sum;
                float mean = (float)xSum / subNum;
                float dev = GetDev(mean, x2Sum, subNum);
                features[featureIdx++] = mean;
                features[featureIdx++] = dev;
                mean2Sum += mean * mean;
                devSum += dev;
                numSubBlks++;
            }
        float lvl0Mean = (float)totalXSum / num;
        features[0] = lvl0Mean;
        features[1] = GetDev(lvl0Mean, totalX2Sum, num);
        features[featureIdx++] = GetDev(lvl0Mean, mean2Sum, numSubBlks);
        features[featureIdx++] = devSum / numSubBlks;
        return featureIdx;
    }

    /// <summary>ml_predict_tx_split (tx_search.c): the tx-split model's score (x10000, clamped to +-80000), or -1
    /// when the tx size has no model.</summary>
    internal static int PredictTxSplit(ReadOnlySpan<short> diff, int stride, int txSize)
    {
        AomNnConfig? cfg = AomMlModels.TxSplitNnconfigMap[txSize];
        if (cfg == null) return -1;
        Span<float> features = stackalloc float[64];
        features.Clear();
        GetMeanDevFeatures(diff, stride, TxSizeWide[txSize], TxSizeHigh[txSize], features);
        Span<float> score = stackalloc float[1];
        score[0] = 0f;
        NnPredict(features, cfg, true, score);
        int intScore = CvttSs2Si(score[0] * 10000);
        return Math.Clamp(intScore, -80000, 80000);
    }

    /// <summary>ml_predict_intra_tx_depth_prune (tx_search.c): returns the new nn_prune_depths_for_intra_tx
    /// (TX_PRUNE_SPLIT / TX_PRUNE_LARGEST), or -1 where libaom leaves it unchanged. bsize is the plane block size,
    /// sourceVariance x->source_variance, dcQ av1_dc_quant_QTX(x->qindex, 0, bd) (the model only runs at 8 bits).</summary>
    internal static int PredictIntraTxDepthPrune(ReadOnlySpan<short> diff, int stride, int bsize, int txSize, bool lossless,
        int bitDepth, uint sourceVariance, int dcQ)
    {
        if (lossless || TxsizeToBsize[txSize] != bsize || bitDepth != 8) return -1;
        if (txSize != TX_8X8) return -1;
        Span<float> features = stackalloc float[NUM_INTRA_TX_SPLIT_FEATURES];
        features.Clear();
        int featureIdx = GetMeanDevFeatures(diff, stride, TxSizeWide[txSize], TxSizeHigh[txSize], features);
        features[featureIdx++] = Log1pf((float)sourceVariance);
        int dcq = dcQ >> (bitDepth - 8);
        features[featureIdx++] = Log1pf((float)(dcq * dcq) / 256.0f);
        var mean = AomMlModels.IntraTxSplit8x8Mean;
        var std = AomMlModels.IntraTxSplit8x8Std;
        for (int i = 0; i < NUM_INTRA_TX_SPLIT_FEATURES; i++) features[i] = (features[i] - mean[i]) / std[i];
        Span<float> score = stackalloc float[1];
        NnPredict(features, AomMlModels.IntraTxSplitNnconfig8x8, true, score);
        var thresh = AomMlModels.IntraTxPruneNnThresh8x8;
        if (score[0] <= thresh[0]) return TX_PRUNE_SPLIT;
        if (score[0] > thresh[1]) return TX_PRUNE_LARGEST;
        return -1;
    }

    // prune_2D_adaptive_thresholds (tx_search.c), by tx size; null where there is no model
    private static readonly float[]?[] Prune2DAdaptiveThresholds =
    [
        [0.00549f, 0.01306f, 0.02039f, 0.02747f, 0.03406f, 0.04065f, 0.04724f, 0.05383f, 0.06067f, 0.06799f, 0.07605f, 0.08533f, 0.09778f, 0.11780f],
        [0.00037f, 0.00183f, 0.00525f, 0.01038f, 0.01697f, 0.02502f, 0.03381f, 0.04333f, 0.05286f, 0.06287f, 0.07434f, 0.08850f, 0.10803f, 0.14124f],
        [0.01404f, 0.02000f, 0.04211f, 0.05164f, 0.05798f, 0.06335f, 0.06897f, 0.07629f, 0.08875f, 0.11169f],
        null,
        null,
        [0.00183f, 0.00745f, 0.01428f, 0.02185f, 0.02966f, 0.03723f, 0.04456f, 0.05188f, 0.05920f, 0.06702f, 0.07605f, 0.08704f, 0.10168f, 0.12585f],
        [0.00085f, 0.00476f, 0.01135f, 0.01892f, 0.02698f, 0.03528f, 0.04358f, 0.05164f, 0.05994f, 0.06848f, 0.07849f, 0.09021f, 0.10583f, 0.13123f],
        [0.00037f, 0.00232f, 0.00671f, 0.01257f, 0.01965f, 0.02722f, 0.03552f, 0.04382f, 0.05237f, 0.06189f, 0.07336f, 0.08728f, 0.10730f, 0.14221f],
        [0.00061f, 0.00330f, 0.00818f, 0.01453f, 0.02185f, 0.02966f, 0.03772f, 0.04578f, 0.05383f, 0.06262f, 0.07288f, 0.08582f, 0.10339f, 0.13464f],
        null,
        null,
        null,
        null,
        [0.00232f, 0.00671f, 0.01257f, 0.01941f, 0.02673f, 0.03430f, 0.04211f, 0.04968f, 0.05750f, 0.06580f, 0.07507f, 0.08655f, 0.10242f, 0.12878f],
        [0.00110f, 0.00525f, 0.01208f, 0.01990f, 0.02795f, 0.03601f, 0.04358f, 0.05115f, 0.05896f, 0.06702f, 0.07629f, 0.08752f, 0.10217f, 0.12610f],
        null,
        null,
        null,
        null,
    ];

    private static readonly int[] TxTypeTable2D =
    [
        DCT_DCT, DCT_ADST, DCT_FLIPADST, V_DCT, ADST_DCT, ADST_ADST, ADST_FLIPADST, V_ADST,
        FLIPADST_DCT, FLIPADST_ADST, FLIPADST_FLIPADST, V_FLIPADST, H_DCT, H_ADST, H_FLIPADST, IDTX,
    ];

    // get_adaptive_thresholds (tx_search.c)
    private static float GetAdaptiveThresholds(int txSize, int txSetType, int prune2dTxfmMode)
    {
        ReadOnlySpan<int> pruneAggrTable = [4, 1, 6, 3, 9, 6, 9, 6, 12, 9];
        int pruningAggressiveness = 0;
        if (txSetType == EXT_TX_SET_ALL16) pruningAggressiveness = pruneAggrTable[(prune2dTxfmMode - TX_TYPE_PRUNE_1) * 2];
        else if (txSetType == EXT_TX_SET_DTT9_IDTX_1DDCT)
            pruningAggressiveness = pruneAggrTable[(prune2dTxfmMode - TX_TYPE_PRUNE_1) * 2 + 1];
        return Prune2DAdaptiveThresholds[txSize]![pruningAggressiveness];
    }

    /// <summary>prune_tx_2D (tx_search.c; libaom calls it for inter blocks only): prunes 2D tx types with the hor/ver
    /// tx-type models. txkMap receives the (sorted) allowed tx types; allowedTxMask is updated.</summary>
    internal static void PruneTx2D(ReadOnlySpan<short> diff, int diffStride, int txSize, int txSetType, int prune2dTxfmMode,
        Span<int> txkMap, ref ushort allowedTxMask)
    {
        if (txSetType != EXT_TX_SET_ALL16 && txSetType != EXT_TX_SET_DTT9_IDTX_1DDCT) return;
        AomNnConfig? cfgHor = AomMlModels.TxTypeNnconfigMapHor[txSize], cfgVer = AomMlModels.TxTypeNnconfigMapVer[txSize];
        if (cfgHor == null || cfgVer == null) return;

        Span<float> hfeatures = stackalloc float[16], vfeatures = stackalloc float[16];
        Span<float> hscores = stackalloc float[4], vscores = stackalloc float[4];
        Span<float> scores2DRaw = stackalloc float[16];
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        int hfeaturesNum = bw <= 8 ? bw : bw / 2, vfeaturesNum = bh <= 8 ? bh : bh / 2;
        GetEnergyDistributionFiner(diff, diffStride, bw, bh, hfeatures, vfeatures);
        GetHorverCorrelationFull(diff, diffStride, bw, bh, out hfeatures[hfeaturesNum - 1], out vfeatures[vfeaturesNum - 1]);
        NnPredict(hfeatures, cfgHor, true, hscores);
        NnPredict(vfeatures, cfgVer, true, vscores);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) scores2DRaw[i * 4 + j] = vscores[i] * hscores[j];
        NnFastSoftmax16(scores2DRaw, scores2DRaw);

        float scoreThresh = GetAdaptiveThresholds(txSize, txSetType, prune2dTxfmMode);
        int maxScoreI = 0;
        float maxScore = 0.0f;
        int allowBitmask = 0;
        float sumScore = 0.0f;
        int allowCount = 0;
        Span<int> txTypeAllowed = stackalloc int[16];
        txTypeAllowed.Fill(TX_TYPE_INVALID);
        Span<float> scores2D = stackalloc float[16];
        scores2D.Fill(-1f);
        for (int txIdx = 0; txIdx < TX_TYPES; txIdx++)
        {
            if ((allowedTxMask & (1 << TxTypeTable2D[txIdx])) == 0) continue;
            if (scores2DRaw[txIdx] > maxScore) { maxScore = scores2DRaw[txIdx]; maxScoreI = txIdx; }
            if (scores2DRaw[txIdx] >= scoreThresh)
            {
                allowBitmask |= 1 << TxTypeTable2D[txIdx];
                sumScore += scores2DRaw[txIdx];
                scores2D[allowCount] = scores2DRaw[txIdx];
                txTypeAllowed[allowCount] = TxTypeTable2D[txIdx];
                allowCount += 1;
            }
        }
        if ((allowBitmask & (1 << TxTypeTable2D[maxScoreI])) == 0)
        {
            allowBitmask |= 1 << TxTypeTable2D[maxScoreI];
            TxTypeTable2D.CopyTo(txkMap);
            allowedTxMask = (ushort)allowBitmask;
            return;
        }

        if (allowCount <= 8) SortFi32_8(scores2D, txTypeAllowed);
        else SortFi32_16(scores2D, txTypeAllowed);

        if (prune2dTxfmMode >= TX_TYPE_PRUNE_4)
        {
            float tempScore = 0.0f, scoreRatio = 0.0f;
            int txIdx, txCount = 0;
            float invSumScore = 100 / sumScore;
            for (txIdx = 0; txIdx < allowCount; txIdx++)
            {
                if (scoreRatio > 30.0f && txCount >= 2) break;
                tempScore += scores2D[txIdx];
                scoreRatio = tempScore * invSumScore;
                txCount++;
            }
            for (; txIdx < allowCount; txIdx++) allowBitmask &= ~(1 << txTypeAllowed[txIdx]);
        }

        txTypeAllowed.CopyTo(txkMap);
        allowedTxMask = (ushort)allowBitmask;
    }
}
