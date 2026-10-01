using System;
using System.Runtime.CompilerServices;

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

    /// <summary>av1_set_error_per_bit: max(rdmult >> RD_EPB_SHIFT, 1).</summary>
    internal static int ErrorPerBit(int rdmult) => Math.Max(rdmult >> RdEpbShift, 1);
}
