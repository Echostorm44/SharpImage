using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

// Port of libaom av1/encoder/cost.{c,h} and aom_dsp/prob.h get_prob: symbol costs in 1/512 bit (AV1_PROB_COST_SHIFT)
// from a CDF's probabilities through libaom's av1_prob_cost table.
internal static class AomCost
{
    internal const int ProbCostShift = 9;   // AV1_PROB_COST_SHIFT
    internal const int EcMinProb = 4;       // EC_MIN_PROB

    // round(-log2(i/256.) * (1 << AV1_PROB_COST_SHIFT)); i = 128~255.
    private static readonly ushort[] ProbCost =
    {
        512, 506, 501, 495, 489, 484, 478, 473, 467, 462, 456, 451, 446, 441, 435,
        430, 425, 420, 415, 410, 405, 400, 395, 390, 385, 380, 375, 371, 366, 361,
        356, 352, 347, 343, 338, 333, 329, 324, 320, 316, 311, 307, 302, 298, 294,
        289, 285, 281, 277, 273, 268, 264, 260, 256, 252, 248, 244, 240, 236, 232,
        228, 224, 220, 216, 212, 209, 205, 201, 197, 194, 190, 186, 182, 179, 175,
        171, 168, 164, 161, 157, 153, 150, 146, 143, 139, 136, 132, 129, 125, 122,
        119, 115, 112, 109, 105, 102, 99, 95, 92, 89, 86, 82, 79, 76, 73,
        70, 66, 63, 60, 57, 54, 51, 48, 45, 42, 38, 35, 32, 29, 26,
        23, 20, 18, 15, 12, 9, 6, 3,
    };

    /// <summary>av1_cost_literal: n equiprobable bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CostLiteral(int n) => n * (1 << ProbCostShift);

    /// <summary>get_prob (aom_dsp/prob.h): num / den scaled to 8 bits, rounded, clipped to 1..255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetProb(uint num, uint den)
    {
        int p = (int)(((ulong)num * 256 + (den >> 1)) / den);
        int clipped = p | ((255 - p) >> 23) | (p == 0 ? 1 : 0);
        return (byte)clipped;
    }

    /// <summary>av1_cost_symbol: the cost of a symbol of probability p15 / 32768.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CostSymbol(int p15)
    {
        p15 = Math.Clamp(p15, 1, 32767);
        int shift = 15 - 1 - BitOperations.Log2((uint)p15);
        int prob = GetProb((uint)(p15 << shift), 32768);
        return ProbCost[prob - 128] + CostLiteral(shift);
    }

    /// <summary>av1_cost_tokens_from_cdf for an nsyms-symbol CDF in the decoder's layout (icdf[s] = 32768 - cumulative
    /// probability of symbols 0..s, libaom's AOM_ICDF values; the last symbol's entry may be absent). invMap: libaom's
    /// inverse map (costs[invMap[i]] = cost of coded symbol i), null for the identity.</summary>
    internal static void CostTokensFromCdf(Span<int> costs, ReadOnlySpan<ushort> icdf, int nsyms, ReadOnlySpan<int> invMap = default)
    {
        int prev = 0;
        for (int i = 0; i < nsyms; i++)
        {
            int cum = i == nsyms - 1 ? 32768 : 32768 - icdf[i];
            int p15 = cum - prev;
            if (p15 < EcMinProb) p15 = EcMinProb;
            prev = cum;
            int c = CostSymbol(p15);
            if (invMap.IsEmpty) costs[i] = c; else costs[invMap[i]] = c;
        }
    }
}
