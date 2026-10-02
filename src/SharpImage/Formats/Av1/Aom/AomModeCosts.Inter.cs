using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>ModeCosts' inter-frame tables (libaom dimensions, flattened row-major).</summary>
internal sealed partial class AomModeCosts
{
    public readonly int[] SkipModeCost = new int[3 * 2];                        // [SKIP_MODE_CONTEXTS][2]
    public readonly int[] SwitchableInterpCosts = new int[16 * 3];              // [SWITCHABLE_FILTER_CONTEXTS][SWITCHABLE_FILTERS]
    public readonly int[] CompInterCost = new int[5 * 2];                       // [COMP_INTER_CONTEXTS][2]
    public readonly int[] SingleRefCost = new int[3 * 6 * 2];                   // [REF_CONTEXTS][SINGLE_REFS - 1][2]
    public readonly int[] CompRefTypeCost = new int[5 * 2];                     // [COMP_REF_TYPE_CONTEXTS][2]
    public readonly int[] UniCompRefCost = new int[3 * 3 * 2];                  // [UNI_COMP_REF_CONTEXTS][UNIDIR_COMP_REFS - 1][2]
    public readonly int[] CompRefCost = new int[3 * 3 * 2];                     // [REF_CONTEXTS][FWD_REFS - 1][2]
    public readonly int[] CompBwdrefCost = new int[3 * 2 * 2];                  // [REF_CONTEXTS][BWD_REFS - 1][2]
    public readonly int[] IntraInterCost = new int[4 * 2];                      // [INTRA_INTER_CONTEXTS][2]
    public readonly int[] NewmvModeCost = new int[6 * 2];                       // [NEWMV_MODE_CONTEXTS][2]
    public readonly int[] ZeromvModeCost = new int[2 * 2];                      // [GLOBALMV_MODE_CONTEXTS][2]
    public readonly int[] RefmvModeCost = new int[6 * 2];                       // [REFMV_MODE_CONTEXTS][2]
    public readonly int[] DrlModeCost0 = new int[3 * 2];                        // [DRL_MODE_CONTEXTS][2]
    public readonly int[] InterCompoundModeCost = new int[8 * 8];               // [INTER_MODE_CONTEXTS][INTER_COMPOUND_MODES]
    public readonly int[] CompoundTypeCost = new int[22 * 2];                   // [BLOCK_SIZES_ALL][MASKED_COMPOUND_TYPES]
    public readonly int[] WedgeIdxCost = new int[22 * 16];                      // [BLOCK_SIZES_ALL][16]
    public readonly int[] InterintraCost = new int[4 * 2];                      // [BLOCK_SIZE_GROUPS][2]
    public readonly int[] WedgeInterintraCost = new int[22 * 2];                // [BLOCK_SIZES_ALL][2]
    public readonly int[] InterintraModeCost = new int[4 * 4];                  // [BLOCK_SIZE_GROUPS][INTERINTRA_MODES]
    public readonly int[] MotionModeCost = new int[22 * 3];                     // [BLOCK_SIZES_ALL][MOTION_MODES]
    public readonly int[] MotionModeCost1 = new int[22 * 2];                    // [BLOCK_SIZES_ALL][2]
    public readonly int[] CompIdxCost = new int[6 * 2];                         // [COMP_INDEX_CONTEXTS][2]
    public readonly int[] CompGroupIdxCost = new int[6 * 2];                    // [COMP_GROUP_IDX_CONTEXTS][2]
}

internal static partial class AomModeCostFill
{
    /// <summary>libaom BLOCK_SIZE -> the decoder CDF's wedge context (dav1d_wedge_ctx_lut); -1 for sizes without wedges.</summary>
    internal static readonly sbyte[] WedgeCtx = { -1, -1, -1, 0, 1, 2, 3, 4, 5, 6, -1, -1, -1, -1, -1, -1, -1, -1, 7, 8, -1, -1 };
    private static readonly ushort[] NonWedgeCompoundTypeCdf = { 16384, 0 };   // AOM_CDF2(16384)

    /// <summary>av1_fill_mode_rates' skip mode and switchable interpolation filter costs (every frame) and its
    /// !frame_is_intra_only part, from the frame's CDFs.</summary>
    internal static void FillInter(AomModeCosts mc, Av1CdfContext fc, bool skipModeFlag, bool frameIsIntraOnly)
    {
        var m = fc.Mode;
        if (skipModeFlag)
            for (int i = 0; i < 3; i++) AomCost.CostTokensFromCdf(mc.SkipModeCost.AsSpan(i * 2), m.SkipMode[i], 2);
        else Array.Clear(mc.SkipModeCost);
        for (int i = 0; i < 16; i++) AomCost.CostTokensFromCdf(mc.SwitchableInterpCosts.AsSpan(i * 3), m.Filter[i], 3);
        if (frameIsIntraOnly) return;
        for (int i = 0; i < 5; i++) AomCost.CostTokensFromCdf(mc.CompInterCost.AsSpan(i * 2), m.Comp[i], 2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 6; j++) AomCost.CostTokensFromCdf(mc.SingleRefCost.AsSpan((i * 6 + j) * 2), m.Ref[j * 3 + i], 2);
        for (int i = 0; i < 5; i++) AomCost.CostTokensFromCdf(mc.CompRefTypeCost.AsSpan(i * 2), m.CompDir[i], 2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) AomCost.CostTokensFromCdf(mc.UniCompRefCost.AsSpan((i * 3 + j) * 2), m.CompUniRef[j * 3 + i], 2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) AomCost.CostTokensFromCdf(mc.CompRefCost.AsSpan((i * 3 + j) * 2), m.CompFwdRef[j * 3 + i], 2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++) AomCost.CostTokensFromCdf(mc.CompBwdrefCost.AsSpan((i * 2 + j) * 2), m.CompBwdRef[j * 3 + i], 2);
        for (int i = 0; i < 4; i++) AomCost.CostTokensFromCdf(mc.IntraInterCost.AsSpan(i * 2), m.Intra[i], 2);
        for (int i = 0; i < 6; i++) AomCost.CostTokensFromCdf(mc.NewmvModeCost.AsSpan(i * 2), m.NewmvMode[i], 2);
        for (int i = 0; i < 2; i++) AomCost.CostTokensFromCdf(mc.ZeromvModeCost.AsSpan(i * 2), m.GlobalmvMode[i], 2);
        for (int i = 0; i < 6; i++) AomCost.CostTokensFromCdf(mc.RefmvModeCost.AsSpan(i * 2), m.RefmvMode[i], 2);
        for (int i = 0; i < 3; i++) AomCost.CostTokensFromCdf(mc.DrlModeCost0.AsSpan(i * 2), m.DrlBit[i], 2);
        for (int i = 0; i < 8; i++) AomCost.CostTokensFromCdf(mc.InterCompoundModeCost.AsSpan(i * 8), m.CompInterMode[i], 8);
        for (int i = 0; i < BLOCK_SIZES_ALL; i++)
        {
            int w = WedgeCtx[i];
            // compound_type_cdf[bsize] (COMPOUND_WEDGE / COMPOUND_DIFFWTD): libaom keeps a CDF per block size; the sizes
            // without wedges never code the symbol, so theirs stays the default AOM_CDF2(16384) (calc_masked_type_cost
            // still adds its costs)
            AomCost.CostTokensFromCdf(mc.CompoundTypeCost.AsSpan(i * 2), w >= 0 ? m.WedgeComp[w] : NonWedgeCompoundTypeCdf, 2);
            if (w >= 0) AomCost.CostTokensFromCdf(mc.WedgeIdxCost.AsSpan(i * 16), m.WedgeIdx[w], 16);
        }
        for (int i = 0; i < 4; i++)
        {
            AomCost.CostTokensFromCdf(mc.InterintraCost.AsSpan(i * 2), m.Interintra[i], 2);
            AomCost.CostTokensFromCdf(mc.InterintraModeCost.AsSpan(i * 4), m.InterintraMode[i], 4);
        }
        for (int i = 0; i < BLOCK_SIZES_ALL; i++)
        {
            int w = WedgeCtx[i];
            // (the decoder's CDF set has interintra wedge CDFs only for the interintra sizes 8x8 .. 32x32)
            if (w >= 0 && w < m.InterintraWedge.Length) AomCost.CostTokensFromCdf(mc.WedgeInterintraCost.AsSpan(i * 2), m.InterintraWedge[w], 2);
            else Array.Clear(mc.WedgeInterintraCost, i * 2, 2);
        }
        for (int i = BLOCK_8X8; i < BLOCK_SIZES_ALL; i++)
        {
            AomCost.CostTokensFromCdf(mc.MotionModeCost.AsSpan(i * 3), m.MotionMode[LibaomToDav1dBs[i]], 3);
            AomCost.CostTokensFromCdf(mc.MotionModeCost1.AsSpan(i * 2), m.Obmc[LibaomToDav1dBs[i]], 2);
        }
        for (int i = 0; i < 6; i++) AomCost.CostTokensFromCdf(mc.CompIdxCost.AsSpan(i * 2), m.JntComp[i], 2);
        for (int i = 0; i < 6; i++) AomCost.CostTokensFromCdf(mc.CompGroupIdxCost.AsSpan(i * 2), m.MaskComp[i], 2);
    }
}
