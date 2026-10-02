using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>RD_OPT's mode thresholds: thresh_mult (av1_set_rd_speed_thresholds) and threshes[segment][bsize][mode]
/// (set_block_thresholds, the RD search's mode pruning).</summary>
internal sealed class AomRdOpt
{
    public const int RD_THRESH_FAC_FRAC_BITS = 5, RD_THRESH_FAC_FRAC_VAL = 1 << RD_THRESH_FAC_FRAC_BITS;
    public const int RD_THRESH_MAX_FACT = 64, RD_THRESH_LOG_DEC_FACTOR = 4, RD_THRESH_INC = 1;
    private static readonly byte[] RdThreshBlockSizeFactor = { 2, 3, 3, 4, 6, 6, 8, 12, 12, 16, 24, 24, 32, 48, 48, 64, 4, 4, 8, 8, 16, 16 };

    public readonly int[] ThreshMult = new int[MAX_MODES];
    /// <summary>threshes[segment][bsize][mode], flattened.</summary>
    public readonly int[] Threshes = new int[8 * BLOCK_SIZES_ALL * MAX_MODES];

    public int Thresh(int segmentId, int bsize, int mode) => Threshes[(segmentId * BLOCK_SIZES_ALL + bsize) * MAX_MODES + mode];

    /// <summary>av1_set_rd_speed_thresholds.</summary>
    public void SetRdSpeedThresholds() => Array.Copy(RdThreshMult, ThreshMult, MAX_MODES);

    /// <summary>compute_rd_thresh_factor.</summary>
    private static int ComputeRdThreshFactor(int qindex, int bitDepth)
    {
        double q = bitDepth switch
        {
            8 => AomComp.DcQuantQtx(qindex, 0, 8) / 4.0,
            10 => AomComp.DcQuantQtx(qindex, 0, 10) / 16.0,
            _ => AomComp.DcQuantQtx(qindex, 0, 12) / 64.0,
        };
        return Math.Max((int)(Math.Pow(q, 1.25) * 5.12), 8);   // RD_THRESH_POW
    }

    /// <summary>set_block_thresholds (the RD pick mode: every mode; no segmentation, so each segment's qindex is the
    /// base one).</summary>
    public void SetBlockThresholds(int baseQindex, int yDcDeltaQ, int bitDepth)
    {
        for (int segmentId = 0; segmentId < 8; ++segmentId)
        {
            int qindex = Math.Clamp(baseQindex + yDcDeltaQ, 0, 255);
            int q = ComputeRdThreshFactor(qindex, bitDepth);
            for (int bsize = 0; bsize < BLOCK_SIZES_ALL; ++bsize)
            {
                int t = q * RdThreshBlockSizeFactor[bsize];
                int threshMax = int.MaxValue / t;
                for (int i = 0; i < MAX_MODES; ++i)
                    Threshes[(segmentId * BLOCK_SIZES_ALL + bsize) * MAX_MODES + i] = ThreshMult[i] < threshMax ? ThreshMult[i] * t / 4 : int.MaxValue;
            }
        }
    }

    /// <summary>reset_thresh_freq_fact.</summary>
    public static void ResetThreshFreqFact(int[,] f)
    {
        for (int i = 0; i < BLOCK_SIZES_ALL; ++i)
            for (int j = 0; j < MAX_MODES; ++j) f[i, j] = RD_THRESH_FAC_FRAC_VAL;
    }

    private static void UpdateThrFact(int[,] factorBuf, int bestModeIndex, int modeStart, int modeEnd, int minSize, int maxSize, int maxRdThreshFactor)
    {
        for (int mode = modeStart; mode < modeEnd; ++mode)
            for (int bs = minSize; bs <= maxSize; ++bs)
            {
                if (mode == bestModeIndex) factorBuf[bs, mode] -= factorBuf[bs, mode] >> RD_THRESH_LOG_DEC_FACTOR;
                else factorBuf[bs, mode] = Math.Min(factorBuf[bs, mode] + RD_THRESH_INC, maxRdThreshFactor);
            }
    }

    /// <summary>av1_update_rd_thresh_fact.</summary>
    public static void UpdateRdThreshFact(int sbSize, int[,] factorBuf, int useAdaptiveRdThresh, int bsize, int bestModeIndex, int interModeStart,
        int interModeEnd, int intraModeStart, int intraModeEnd)
    {
        int maxRdThreshFactor = useAdaptiveRdThresh * RD_THRESH_MAX_FACT;
        bool bsizeIs1To4 = bsize > sbSize;
        int minSize, maxSize;
        if (bsizeIs1To4) { minSize = bsize; maxSize = bsize; }
        else
        {
            minSize = Math.Max(bsize - 2, BLOCK_4X4);
            maxSize = Math.Min(bsize + 2, sbSize);
        }
        UpdateThrFact(factorBuf, bestModeIndex, interModeStart, interModeEnd, minSize, maxSize, maxRdThreshFactor);
        UpdateThrFact(factorBuf, bestModeIndex, intraModeStart, intraModeEnd, minSize, maxSize, maxRdThreshFactor);
    }
}

internal sealed partial class AomComp
{
    public readonly AomRdOpt Rd = new();
}
