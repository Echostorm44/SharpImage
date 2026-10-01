using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal static class AomModeCostFill
{
    /// <summary>libaom BLOCK_SIZE -> the decoder's Av1BlockSize (dav1d order, 128x128 first).</summary>
    internal static readonly byte[] LibaomToDav1dBs =
    {
        (byte)Av1BlockSize.Bs4x4, (byte)Av1BlockSize.Bs4x8, (byte)Av1BlockSize.Bs8x4, (byte)Av1BlockSize.Bs8x8,
        (byte)Av1BlockSize.Bs8x16, (byte)Av1BlockSize.Bs16x8, (byte)Av1BlockSize.Bs16x16, (byte)Av1BlockSize.Bs16x32,
        (byte)Av1BlockSize.Bs32x16, (byte)Av1BlockSize.Bs32x32, (byte)Av1BlockSize.Bs32x64, (byte)Av1BlockSize.Bs64x32,
        (byte)Av1BlockSize.Bs64x64, (byte)Av1BlockSize.Bs64x128, (byte)Av1BlockSize.Bs128x64, (byte)Av1BlockSize.Bs128x128,
        (byte)Av1BlockSize.Bs4x16, (byte)Av1BlockSize.Bs16x4, (byte)Av1BlockSize.Bs8x32, (byte)Av1BlockSize.Bs32x8,
        (byte)Av1BlockSize.Bs16x64, (byte)Av1BlockSize.Bs64x16,
    };

    // use_inter_ext_tx_for_txsize[EXT_TX_SETS_INTER][EXT_TX_SIZES] (rd.c) and av1_ext_tx_set_idx_to_type[1]
    internal static readonly int[] UseInterExtTxForTxsize = { 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1 };
    internal static readonly int[] InterExtTxSetIdxToType = { EXT_TX_SET_DCTONLY, EXT_TX_SET_ALL16, EXT_TX_SET_DTT9_IDTX_1DDCT, EXT_TX_SET_DCT_IDTX };

    /// <summary>av1_filter_intra_allowed_bsize.</summary>
    internal static bool FilterIntraAllowedBsize(bool enableFilterIntra, int bs)
        => enableFilterIntra && BlockSizeWide[bs] <= 32 && BlockSizeHigh[bs] <= 32;

    /// <summary>av1_fill_mode_rates for an intra frame (plus the tx partition / inter tx type costs of intrabc blocks), from
    /// the tile's CDFs.</summary>
    internal static void Fill(AomModeCosts mc, Av1CdfContext fc, bool enableFilterIntra)
    {
        var m = fc.Mode;
        Span<int> tmp = stackalloc int[16];
        // partition_cdf[PARTITION_CONTEXTS]: libaom ctx = bsl * 4 + above/left; bsl 0 = 8x8 .. 4 = 128x128 (dav1d level 4 - bsl)
        for (int i = 0; i < AomModeCosts.PartitionContexts; i++)
        {
            int bsl = i >> 2, n = bsl == 0 ? 4 : bsl == 4 ? 8 : 10;
            AomCost.CostTokensFromCdf(mc.PartitionCost.AsSpan(i * 10), m.Partition[(4 - bsl) * 4 + (i & 3)], n);
        }
        for (int i = 0; i < AomModeCosts.SkipContexts; i++)
            AomCost.CostTokensFromCdf(mc.SkipTxfmCost.AsSpan(i * 2), m.Skip[i], 2);
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                AomCost.CostTokensFromCdf(mc.YModeCosts.AsSpan((i * 13 + j) * 13), fc.Kfym[i * 5 + j], 13);
        for (int i = 0; i < 4; i++) AomCost.CostTokensFromCdf(mc.MbmodeCost.AsSpan(i * 13), m.YMode[i], 13);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 13; j++)
                AomCost.CostTokensFromCdf(mc.IntraUvModeCost.AsSpan((i * 13 + j) * 14), m.UvMode[i * 13 + j], i == 1 ? 14 : 13);
        AomCost.CostTokensFromCdf(mc.FilterIntraModeCost, m.FilterIntra, 5);
        for (int i = 0; i < AomModeCosts.BlockSizesAll; i++)
            if (FilterIntraAllowedBsize(enableFilterIntra, i))
                AomCost.CostTokensFromCdf(mc.FilterIntraCost.AsSpan(i * 2), m.UseFilterIntra[LibaomToDav1dBs[i]], 2);
        for (int i = 0; i < AomModeCosts.PalatteBsizeCtxs; i++)
        {
            AomCost.CostTokensFromCdf(mc.PaletteYSizeCost.AsSpan(i * 7), m.PalSz[i], 7);
            AomCost.CostTokensFromCdf(mc.PaletteUvSizeCost.AsSpan(i * 7), m.PalSz[7 + i], 7);
            for (int j = 0; j < AomModeCosts.PaletteYModeContexts; j++)
                AomCost.CostTokensFromCdf(mc.PaletteYModeCost.AsSpan((i * 3 + j) * 2), m.PalY[i * 3 + j], 2);
        }
        for (int i = 0; i < AomModeCosts.PaletteUvModeContexts; i++)
            AomCost.CostTokensFromCdf(mc.PaletteUvModeCost.AsSpan(i * 2), m.PalUv[i], 2);
        for (int i = 0; i < AomModeCosts.PaletteSizes; i++)
            for (int j = 0; j < AomModeCosts.PaletteColorIndexContexts; j++)
            {
                AomCost.CostTokensFromCdf(mc.PaletteYColorCost.AsSpan((i * 5 + j) * 8), m.ColorMap[(0 * 7 + i) * 5 + j], i + 2);
                AomCost.CostTokensFromCdf(mc.PaletteUvColorCost.AsSpan((i * 5 + j) * 8), m.ColorMap[(1 * 7 + i) * 5 + j], i + 2);
            }
        // cfl_cost[joint_sign][CFL_PRED_U/V][alpha]: the alpha costs plus the joint sign's
        Span<int> signCost = tmp.Slice(0, 8);
        AomCost.CostTokensFromCdf(signCost, m.CflSign, 8);
        for (int js = 0; js < AomModeCosts.CflJointSigns; js++)
        {
            var costU = mc.CflCost.AsSpan((js * 2 + 0) * 16, 16);
            var costV = mc.CflCost.AsSpan((js * 2 + 1) * 16, 16);
            int signU = (js + 1) / 3, signV = (js + 1) % 3;   // CFL_SIGN_U / CFL_SIGN_V
            if (signU == 0) costU.Clear();
            else AomCost.CostTokensFromCdf(costU, m.CflAlpha[(signU - 1) * 3 + signV], 16);   // CFL_CONTEXT_U = js + 1 - 3
            if (signV == 0) costV.Clear();
            else AomCost.CostTokensFromCdf(costV, m.CflAlpha[(signV - 1) * 3 + signU], 16);   // CFL_CONTEXT_V
            for (int u = 0; u < 16; u++) costU[u] += signCost[js];
        }
        // tx_size_cost[cat][ctx][depth]: cat 0 codes depth 0..1, the others 0..2
        for (int i = 0; i < AomModeCosts.MaxTxCats; i++)
            for (int j = 0; j < AomModeCosts.TxSizeContexts; j++)
                AomCost.CostTokensFromCdf(mc.TxSizeCost.AsSpan((i * 3 + j) * 5), m.Txsz[i * 3 + j], i == 0 ? 2 : 3);
        // intra_tx_type_costs[set][square tx][intra mode][tx type] through av1_ext_tx_inv
        for (int i = TX_4X4; i < AomModeCosts.ExtTxSizes; i++)
            for (int s = 1; s < AomModeCosts.ExtTxSetsIntra; s++)
            {
                if (UseIntraExtTxForTxsize[s * 4 + i] == 0) continue;
                int setType = ExtTxSetIdxToType[0 * 4 + s];
                int n = NumExtTxSet[setType];
                ReadOnlySpan<int> inv = ExtTxInv.AsSpan(setType * 16, 16);
                for (int j = 0; j < AomModeCosts.IntraModes; j++)
                {
                    ushort[] cdf = s == 1 ? m.TxtpIntra1[i * 13 + j] : m.TxtpIntra2[i * 13 + j];
                    AomCost.CostTokensFromCdf(mc.IntraTxTypeCosts.AsSpan(((s * 4 + i) * 13 + j) * 16, 16), cdf, n, inv);
                }
            }
        // txfm_partition_cost[TXFM_PARTITION_CONTEXTS][2] and inter_tx_type_costs[set][square tx][tx type]
        for (int i = 0; i < AomModeCosts.TxfmPartitionContexts; i++)
            AomCost.CostTokensFromCdf(mc.TxfmPartitionCost.AsSpan(i * 2, 2), m.Txpart[i], 2);
        for (int i = TX_4X4; i < AomModeCosts.ExtTxSizes; i++)
            for (int s = 1; s < AomModeCosts.ExtTxSetsInter; s++)
            {
                if (UseInterExtTxForTxsize[s * 4 + i] == 0) continue;
                int setType = InterExtTxSetIdxToType[s];
                int n = NumExtTxSet[setType];
                ushort[] cdf = s == 1 ? m.TxtpInter1[i] : s == 2 ? m.TxtpInter2 : m.TxtpInter3[i];
                AomCost.CostTokensFromCdf(mc.InterTxTypeCosts.AsSpan((s * 4 + i) * 16, 16), cdf, n, ExtTxInv.AsSpan(setType * 16, 16));
            }
        for (int i = 0; i < AomModeCosts.DirectionalModes; i++)
            AomCost.CostTokensFromCdf(mc.AngleDeltaCost.AsSpan(i * 7), m.AngleDelta[i], 7);
        AomCost.CostTokensFromCdf(mc.IntrabcCost, m.Intrabc, 2);
    }

    /// <summary>av1_fill_lr_rates.</summary>
    internal static void FillLr(AomModeCosts mc, Av1CdfContext fc)
    {
        AomCost.CostTokensFromCdf(mc.SwitchableRestoreCost, fc.Mode.RestoreSwitchable, 3);
        AomCost.CostTokensFromCdf(mc.WienerRestoreCost, fc.Mode.RestoreWiener, 2);
        AomCost.CostTokensFromCdf(mc.SgrprojRestoreCost, fc.Mode.RestoreSgrproj, 2);
    }
}
