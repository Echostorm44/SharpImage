using System;
using System.Runtime.CompilerServices;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom av1/encoder/rd.{c,h}: the rd multiplier, RDCOST, error-per-bit.
internal static class AomRd
{
    internal const int RdDivBits = 7;     // RDDIV_BITS
    internal const int RdEpbShift = 6;    // RD_EPB_SHIFT

    /// <summary>RDCOST(RM, R, D): ROUND_POWER_OF_TWO(R * RM, AV1_PROB_COST_SHIFT) + D * 2^RDDIV_BITS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long RdCost(int rdmult, long rate, long dist) => ((rate * rdmult + 256) >> AomCost.ProbCostShift) + dist * (1 << RdDivBits);

    /// <summary>RDCOST with a 64-bit multiplier (the trellis' scaled rdmult).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>av1_calculate_rd_cost: RDCOST, or RDCOST_NEG_R for a negative rate.</summary>
    internal static long CalculateRdCost(int mult, int rate, long dist)
        => rate >= 0 ? RdCost(mult, rate, dist) : dist * (1 << RdDivBits) - ((((long)-rate * mult) + 256) >> AomCost.ProbCostShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long RdCost64(long rdmult, long rate, long dist) => ((rate * rdmult + 256) >> AomCost.ProbCostShift) + dist * (1 << RdDivBits);

    /// <summary>av1_compute_rd_mult_based_on_qindex for a key frame (KF_UPDATE) with tune=psnr: q = the DC dequantizer,
    /// rdmult = q^2 * (3.3 + 0.0015 q), rounded down to the bit depth.</summary>
    internal static int RdMultKeyFrame(int qindex, int bitDepth, bool tuneIq = false)
    {
        int bdIdx = bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2;
        int q = Av1Tables.DequantTable[bdIdx, qindex, 0];
        long rdmult = (long)q * q;
        double defRdQMult = 3.3 + 0.0015 * q;   // def_kf_rd_multiplier (of the dequantizer, as libaom passes it)
        rdmult = (long)(rdmult * defRdQMult);
        if (tuneIq)
        {
            // AOM_TUNE_IQ (all-intra / good quality): weight up to 200/128, ramping down to 128/128 for high qindexes
            int weight = Math.Clamp(((255 - qindex) * 3) / 4, 0, 72) + 128;
            rdmult = (long)((double)rdmult * weight / 128.0);
        }
        if (bitDepth == 10) rdmult = (rdmult + 8) >> 4;
        else if (bitDepth == 12) rdmult = (rdmult + 128) >> 8;
        return rdmult > 0 ? (int)Math.Min(rdmult, int.MaxValue) : 1;
    }

    private static readonly int[] RdBoostFactor = { 64, 32, 32, 32, 24, 16, 12, 12, 8, 8, 4, 4, 2, 2, 1, 0 };
    private static readonly int[] RdLayerDepthFactor = { 160, 160, 160, 160, 192, 208, 224 };

    /// <summary>av1_compute_rd_mult_based_on_qindex.</summary>
    internal static int ComputeRdMultBasedOnQindex(int bitDepth, int updateType, int qindex, int tuning, int mode)
    {
        int bdIdx = bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2;
        int q = Av1Tables.DequantTable[bdIdx, qindex, 0];
        long rdmult = (long)q * q;
        if (updateType == KF_UPDATE) rdmult = (long)((double)rdmult * (3.3 + 0.0015 * q));
        else if (updateType == GF_UPDATE || updateType == ARF_UPDATE) rdmult = (long)((double)rdmult * (3.25 + 0.0015 * q));
        else rdmult = (long)((double)rdmult * (3.2 + 0.0015 * q));
        if (tuning == AOM_TUNE_IQ || tuning == AOM_TUNE_SSIMULACRA2)
        {
            int weight = mode == REALTIME ? 32 : Math.Clamp(((255 - qindex) * 3) / 4, 0, 72) + 128;
            rdmult = (long)((double)rdmult * weight / 128.0);
        }
        if (bitDepth == 10) rdmult = (rdmult + 8) >> 4;
        else if (bitDepth == 12) rdmult = (rdmult + 128) >> 8;
        return rdmult > 0 ? (int)Math.Min(rdmult, int.MaxValue) : 1;
    }

    /// <summary>av1_compute_rd_mult.</summary>
    internal static int ComputeRdMult(int qindex, int bitDepth, int updateType, int layerDepth, int boostIndex, int frameType,
        int useFixedQpOffsets, bool isStatConsumptionStage, int tuning, int mode)
    {
        long rdmult = ComputeRdMultBasedOnQindex(bitDepth, updateType, qindex, tuning, mode);
        if (isStatConsumptionStage && useFixedQpOffsets == 0 && frameType != KEY_FRAME)
        {
            rdmult = (rdmult * RdLayerDepthFactor[layerDepth]) >> 7;
            rdmult += (rdmult * RdBoostFactor[boostIndex]) >> 7;
        }
        return rdmult > 0 ? (int)Math.Min(rdmult, int.MaxValue) : 1;
    }

    /// <summary>av1_set_error_per_bit: max(rdmult >> RD_EPB_SHIFT, 1).</summary>
    internal static int ErrorPerBit(int rdmult) => Math.Max(rdmult >> RdEpbShift, 1);
}
