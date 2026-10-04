using System;

namespace SharpImage.Formats.Av1;

/// <summary>ForceIntegerMVInfo (cpi->force_intpel_info).</summary>
internal sealed class AomForceIntMvInfo
{
    public readonly double[] CsRateArray = new double[32];
    public int RateIndex, RateSize;
}

internal sealed partial class AomEncodeInput
{
    /// <summary>cpi->source before scaling and cpi->unscaled_last_source, for av1_is_integer_mv.</summary>
    public AomFrameBuffer? ForceIntMvSource, UnscaledLastSource;
    public AomForceIntMvInfo? ForceIntpelInfo;
}

// Port of libaom 3.14.1 encoder_utils.c av1_is_integer_mv and hash_motion.c av1_hash_is_horizontal_perfect /
// av1_hash_is_vertical_perfect.
internal static class AomForceIntMv
{
    private const int BlockSize = 8;   // FORCE_INT_MV_DECISION_BLOCK_SIZE

    private static int Px(AomFrameBuffer b, int idx) => b.Hbd ? b.Buffers16[0][idx] : b.Buffers[0][idx];

    private static bool HorizontalPerfect(AomFrameBuffer p, int x, int y)
    {
        int stride = p.Strides[0], o = p.Offsets[0] + y * stride + x;
        for (int i = 0; i < BlockSize; i++, o += stride)
            for (int j = 1; j < BlockSize; j++) if (Px(p, o + j) != Px(p, o)) return false;
        return true;
    }

    private static bool VerticalPerfect(AomFrameBuffer p, int x, int y)
    {
        int stride = p.Strides[0], o = p.Offsets[0] + y * stride + x;
        for (int i = 0; i < BlockSize; i++)
            for (int j = 1; j < BlockSize; j++) if (Px(p, o + j * stride + i) != Px(p, o + i)) return false;
        return true;
    }

    /// <summary>av1_is_integer_mv (on the 8-aligned y_width / y_height).</summary>
    internal static bool IsIntegerMv(AomFrameBuffer cur, AomFrameBuffer last, AomForceIntMvInfo info)
    {
        const double thresholdCurrent = 0.8, thresholdAverage = 0.95;
        const int maxHistorySize = 32;
        int T = 0, C = 0, S = 0;
        int picWidth = (cur.CropWidths[0] + 7) & ~7, picHeight = (cur.CropHeights[0] + 7) & ~7;
        for (int i = 0; i + BlockSize <= picHeight; i += BlockSize)
            for (int j = 0; j + BlockSize <= picWidth; j += BlockSize)
            {
                bool match = true;
                T++;
                int oc = cur.Offsets[0] + i * cur.Strides[0] + j, orf = last.Offsets[0] + i * last.Strides[0] + j;
                for (int ty = 0; ty < BlockSize && match; ty++, oc += cur.Strides[0], orf += last.Strides[0])
                    for (int tx = 0; tx < BlockSize && match; tx++)
                        if (Px(cur, oc + tx) != Px(last, orf + tx)) match = false;
                if (match) { C++; continue; }
                if (HorizontalPerfect(cur, j, i) || VerticalPerfect(cur, j, i)) S++;
            }
        double csRate = (double)(C + S) / T;
        info.CsRateArray[info.RateIndex] = csRate;
        info.RateIndex = (info.RateIndex + 1) % maxHistorySize;
        info.RateSize = Math.Min(info.RateSize + 1, maxHistorySize);
        if (csRate < thresholdCurrent) return false;
        if (C == T) return true;
        double csAverage = 0.0;
        for (int k = 0; k < info.RateSize; k++) csAverage += info.CsRateArray[k];
        csAverage /= info.RateSize;
        if (csAverage < thresholdAverage) return false;
        if (T - C - S < 0) return true;
        return csAverage > 1.01;
    }
}
