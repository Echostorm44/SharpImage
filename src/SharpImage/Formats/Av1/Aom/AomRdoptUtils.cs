using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    // cpi->winner_mode_params (filled with the speed features)
    public readonly AomWinnerModeParams WinnerModeParams = new();
    // oxcf.mode == ALLINTRA, oxcf.speed
    public bool AllIntra = true;
    public int Speed;
    // oxcf.intra_mode_cfg
    public bool EnableDiagonalIntra = true, EnableDirectionalIntra = true, EnableSmoothIntra = true, EnablePaethIntra = true,
        EnableCflIntra = true, EnableAngleDelta = true;
    // oxcf.tool_cfg.enable_palette; cm->features.allow_screen_content_tools / allow_intrabc; seq enable_filter_intra, monochrome
    public bool EnablePalette = true, AllowScreenContentTools, AllowIntrabc, EnableFilterIntra = true, Monochrome;
    public bool FrameIsIntraOnly = true;
    // oxcf.txfm_cfg.use_intra_default_tx_only
    public bool UseIntraDefaultTxOnly;
}

// Port of libaom 3.14.1 av1/encoder/rdopt_utils.h and rd.h helpers used by the intra search: the mode evaluation
// parameters (set_mode_eval_params and its setters), winner mode stats, winner mode processing gates.
internal static class AomRdoptUtils
{
    public const int MULTI_WINNER_MODE_OFF = 0, MULTI_WINNER_MODE_FAST = 1, MULTI_WINNER_MODE_DEFAULT = 2;
    public static readonly int[] WinnerModeCountAllowed = { 1, 2, 3 };
    public const int ONLY_4X4 = 0;

    /// <summary>av1_get_block_dimensions.</summary>
    internal static void GetBlockDimensions(int bsize, int plane, AomMacroblockD xd, out int width, out int height, out int rowsWithinBounds,
        out int colsWithinBounds)
    {
        int blockHeight = BlockSizeHigh[bsize], blockWidth = BlockSizeWide[bsize];
        int blockRows = xd.MbToBottomEdge >= 0 ? blockHeight : (xd.MbToBottomEdge >> 3) + blockHeight;
        int blockCols = xd.MbToRightEdge >= 0 ? blockWidth : (xd.MbToRightEdge >> 3) + blockWidth;
        var pd = xd.Plane[plane];
        int planeBlockWidth = blockWidth >> pd.SubsamplingX, planeBlockHeight = blockHeight >> pd.SubsamplingY;
        int sub8X = plane > 0 && planeBlockWidth < 4 ? 1 : 0, sub8Y = plane > 0 && planeBlockHeight < 4 ? 1 : 0;
        width = planeBlockWidth + 2 * sub8X;
        height = planeBlockHeight + 2 * sub8Y;
        rowsWithinBounds = (blockRows >> pd.SubsamplingY) + 2 * sub8Y;
        colsWithinBounds = (blockCols >> pd.SubsamplingX) + 2 * sub8X;
    }

    /// <summary>select_tx_mode.</summary>
    internal static int SelectTxMode(bool codedLossless, int txSizeSearchMethod)
    {
        if (codedLossless) return ONLY_4X4;
        return txSizeSearchMethod == USE_LARGESTALL ? TX_MODE_LARGEST : TX_MODE_SELECT;
    }

    /// <summary>get_rd_opt_coeff_thresh.</summary>
    private static void GetRdOptCoeffThresh(uint[] coeffOptThreshold, AomTxfmSearchParams p, bool enableWinnerModeForCoeffOpt, bool isWinnerMode)
    {
        int e = !enableWinnerModeForCoeffOpt ? DEFAULT_EVAL : isWinnerMode ? WINNER_MODE_EVAL : MODE_EVAL;
        p.CoeffOptThresholds[0] = coeffOptThreshold[e * 2 + 0];
        p.CoeffOptThresholds[1] = coeffOptThreshold[e * 2 + 1];
    }

    /// <summary>set_tx_size_search_method.</summary>
    private static void SetTxSizeSearchMethod(bool codedLossless, AomWinnerModeParams w, AomTxfmSearchParams p, bool enableWinnerModeForTxSizeSrch,
        bool isWinnerMode)
    {
        p.TxSizeSearchMethod = w.tx_size_search_methods[DEFAULT_EVAL];
        if (enableWinnerModeForTxSizeSrch)
            p.TxSizeSearchMethod = w.tx_size_search_methods[isWinnerMode ? WINNER_MODE_EVAL : MODE_EVAL];
        p.TxModeSearchType = SelectTxMode(codedLossless, p.TxSizeSearchMethod);
    }

    private static readonly int[,] PruneMode =
    {
        { TX_TYPE_PRUNE_3, TX_TYPE_PRUNE_0 }, { TX_TYPE_PRUNE_4, TX_TYPE_PRUNE_0 }, { TX_TYPE_PRUNE_5, TX_TYPE_PRUNE_2 }, { TX_TYPE_PRUNE_5, TX_TYPE_PRUNE_3 },
    };

    /// <summary>set_tx_type_prune.</summary>
    private static void SetTxTypePrune(AomSpeedFeatures sf, AomTxfmSearchParams p, int winnerModeTxTypePruning, bool isWinnerMode)
    {
        p.Prune2dTxfmMode = sf.tx_sf.tx_type_search.prune_2d_txfm_mode;
        if (winnerModeTxTypePruning == 0) return;
        p.Prune2dTxfmMode = PruneMode[winnerModeTxTypePruning - 1, isWinnerMode ? 1 : 0];
    }

    /// <summary>set_tx_domain_dist_params.</summary>
    private static void SetTxDomainDistParams(AomWinnerModeParams w, AomTxfmSearchParams p, bool enableWinnerModeForTxDomainDist, bool isWinnerMode)
    {
        if (p.UseQmDistMetric != 0)
        {
            p.UseTransformDomainDistortion = 1;
            p.TxDomainDistThreshold = 0;
            return;
        }
        int e = !enableWinnerModeForTxDomainDist ? DEFAULT_EVAL : isWinnerMode ? WINNER_MODE_EVAL : MODE_EVAL;
        p.UseTransformDomainDistortion = (int)w.use_transform_domain_distortion[e];
        p.TxDomainDistThreshold = w.tx_domain_dist_threshold[e];
    }

    /// <summary>set_mode_eval_params (the dist metric is PSNR: no QM distortion; the mb rd record is inter-only).</summary>
    internal static void SetModeEvalParams(AomComp cpi, AomMacroblock x, int modeEvalType)
    {
        bool codedLossless = cpi.Cm.BaseQindex == 0;   // cm->features.coded_lossless (no delta q / segmentation)
        var sf = cpi.Sf;
        var w = cpi.WinnerModeParams;
        var p = x.TxfmSearchParams;
        p.UseQmDistMetric = cpi.QmPsnrDistMetric ? 1 : 0;   // dist_metric == AOM_DIST_METRIC_QM_PSNR
        switch (modeEvalType)
        {
            case DEFAULT_EVAL:
                p.DefaultInterTxTypeProbThresh = int.MaxValue;
                p.UseDefaultIntraTxType = 0;
                p.UseDerivedIntraTxTypeSet = 0;
                p.SkipTxfmLevel = (int)w.skip_txfm_level[DEFAULT_EVAL];
                p.PredictDcLevel = (int)w.predict_dc_level[DEFAULT_EVAL];
                SetTxDomainDistParams(w, p, false, false);
                GetRdOptCoeffThresh(w.coeff_opt_thresholds, p, false, false);
                SetTxSizeSearchMethod(codedLossless, w, p, false, false);
                SetTxTypePrune(sf, p, 0, false);
                break;
            case MODE_EVAL:
                p.UseDefaultIntraTxType = sf.tx_sf.tx_type_search.fast_intra_tx_type_search == 2 || cpi.UseIntraDefaultTxOnly ? 1 : 0;
                p.UseDerivedIntraTxTypeSet = sf.tx_sf.tx_type_search.fast_intra_tx_type_search == 1 ? 1 : 0;
                p.DefaultInterTxTypeProbThresh = sf.tx_sf.tx_type_search.fast_inter_tx_type_prob_thresh;
                p.SkipTxfmLevel = (int)w.skip_txfm_level[MODE_EVAL];
                p.PredictDcLevel = (int)w.predict_dc_level[MODE_EVAL];
                SetTxDomainDistParams(w, p, sf.winner_mode_sf.enable_winner_mode_for_use_tx_domain_dist != 0, false);
                GetRdOptCoeffThresh(w.coeff_opt_thresholds, p, sf.winner_mode_sf.enable_winner_mode_for_coeff_opt != 0, false);
                SetTxSizeSearchMethod(codedLossless, w, p, sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch != 0, false);
                SetTxTypePrune(sf, p, sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning, false);
                break;
            case WINNER_MODE_EVAL:
                p.DefaultInterTxTypeProbThresh = int.MaxValue;
                p.UseDefaultIntraTxType = 0;
                p.UseDerivedIntraTxTypeSet = 0;
                p.SkipTxfmLevel = (int)w.skip_txfm_level[WINNER_MODE_EVAL];
                p.PredictDcLevel = (int)w.predict_dc_level[WINNER_MODE_EVAL];
                SetTxDomainDistParams(w, p, sf.winner_mode_sf.enable_winner_mode_for_use_tx_domain_dist != 0, true);
                GetRdOptCoeffThresh(w.coeff_opt_thresholds, p, sf.winner_mode_sf.enable_winner_mode_for_coeff_opt != 0, true);
                SetTxSizeSearchMethod(codedLossless, w, p, sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch != 0, true);
                SetTxTypePrune(sf, p, sf.tx_sf.tx_type_search.winner_mode_tx_type_pruning, true);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(modeEvalType));
        }
        p.ModeEvalType = modeEvalType;
    }

    /// <summary>bypass_winner_mode_processing (intra blocks: the best mode is never NEWMV).</summary>
    private static bool BypassWinnerModeProcessing(AomMacroblock x, AomSpeedFeatures sf, bool useTxfmSkip, bool actualTxfmSkip, int bestMode)
    {
        int level = sf.winner_mode_sf.prune_winner_mode_eval_level;
        if (level == 1)
        {
            uint srcVarThresh = (uint)(64 - 48 * x.Qindex / (MAXQ + 1));
            if (x.SourceVariance < srcVarThresh) return true;
        }
        else if (level == 2)
        {
            if (!AomInter.HaveNewmvInInterMode(bestMode) && actualTxfmSkip) return true;
        }
        else if (level == 3)
        {
            bool isTxfmSkip = x.Qindex > 127 ? actualTxfmSkip : actualTxfmSkip || useTxfmSkip;
            if (!AomInter.HaveNewmvInInterMode(bestMode) && isTxfmSkip) return true;
        }
        else if (level >= 4)
        {
            if (sf.rd_sf.perform_coeff_opt >= 5 && x.Qindex <= 70) return false;
            if (useTxfmSkip || actualTxfmSkip) return true;
        }
        return false;
    }

    /// <summary>is_winner_mode_processing_enabled.</summary>
    internal static bool IsWinnerModeProcessingEnabled(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, bool actualTxfmSkip)
    {
        var sf = cpi.Sf;
        int bestMode = mbmi.Mode;
        if (BypassWinnerModeProcessing(x, sf, mbmi.SkipTxfm != 0, actualTxfmSkip, bestMode)) return false;
        if (mbmi.IsInterBlock)
        {
            if (AomInter.IsInterMode(bestMode) && sf.tx_sf.tx_type_search.fast_inter_tx_type_prob_thresh != int.MaxValue && !cpi.UseInterDctOnly)
                return true;
        }
        else if (sf.tx_sf.tx_type_search.fast_intra_tx_type_search != 0 && !cpi.UseIntraDefaultTxOnly && !cpi.UseIntraDctOnly) return true;
        int opt = cpi.OptimizeSegArr[mbmi.SegmentId];
        if (sf.winner_mode_sf.enable_winner_mode_for_coeff_opt != 0 && opt != NO_TRELLIS_OPT && opt != FINAL_PASS_TRELLIS_OPT) return true;
        if (sf.winner_mode_sf.enable_winner_mode_for_tx_size_srch != 0) return true;
        return false;
    }

    /// <summary>zero_winner_mode_stats.</summary>
    internal static void ZeroWinnerModeStats(int bsize, int nStats, AomWinnerModeStats[] stats)
    {
        int n = BlockSizeHigh[bsize] * BlockSizeWide[bsize];
        for (int i = 0; i < nStats; ++i)
        {
            var s = stats[i];
            s.Mbmi.CopyFrom(AomMbModeInfo.Zero);
            s.RdCost = default;
            s.Rd = 0;
            s.RateY = 0;
            s.RateUv = 0;
            Array.Clear(s.ColorIndexMap, 0, n);
            s.ModeIndex = 0;
        }
    }

    /// <summary>store_winner_mode_stats (intra frames).</summary>
    internal static void StoreWinnerModeStats(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, byte[]? colorMap, int bsize, long thisRd,
        int multiWinnerModeType)
    {
        var stats = x.WinnerModeStats;
        int modeIdx = 0;
        bool isPaletteMode = mbmi.Palette.PaletteSize0 > 0;
        if (multiWinnerModeType == MULTI_WINNER_MODE_OFF) return;
        if (thisRd == long.MaxValue) return;
        if (!cpi.FrameIsIntraOnly && isPaletteMode) return;

        int maxWinnerModeCount = WinnerModeCountAllowed[multiWinnerModeType];
        if (x.WinnerModeCount != 0)
        {
            for (modeIdx = 0; modeIdx < x.WinnerModeCount; modeIdx++)
                if (stats[modeIdx].Rd > thisRd) break;
            if (modeIdx == maxWinnerModeCount) return;
            if (modeIdx < maxWinnerModeCount - 1)
            {
                // memmove: shift the entries up one slot (the last falls off; its object is reused for the new entry)
                var spare = stats[maxWinnerModeCount - 1];
                for (int k = maxWinnerModeCount - 1; k > modeIdx; k--) stats[k] = stats[k - 1];
                stats[modeIdx] = spare;
                // the moved-from slot's content is overwritten below; the entries above keep their stats
                CopyStats(stats[modeIdx], stats[modeIdx + 1]);
            }
        }
        var st = stats[modeIdx];
        st.Mbmi.CopyFrom(mbmi);
        st.Rd = thisRd;
        st.ModeIndex = 0;
        if (colorMap != null)
        {
            GetBlockDimensions(bsize, 0, x.E, out int bw, out int bh, out _, out _);
            Array.Copy(colorMap, st.ColorIndexMap, bw * bh);
        }
        x.WinnerModeCount = Math.Min(x.WinnerModeCount + 1, maxWinnerModeCount);
    }

    /// <summary>store_winner_mode_stats with the inter-frame rd stats (rd_cost / rate_y / rate_uv) and the mode index.</summary>
    internal static void StoreWinnerModeStats(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, AomRdStats? rdCost, AomRdStats? rdCostY,
        AomRdStats? rdCostUv, int modeIndex, byte[]? colorMap, int bsize, long thisRd, int multiWinnerModeType, bool txfmSearchDone)
    {
        var stats = x.WinnerModeStats;
        int modeIdx = 0;
        bool isPaletteMode = mbmi.Palette.PaletteSize0 > 0;
        if (multiWinnerModeType == MULTI_WINNER_MODE_OFF) return;
        if (thisRd == long.MaxValue) return;
        if (!cpi.FrameIsIntraOnly && isPaletteMode) return;
        int maxWinnerModeCount = WinnerModeCountAllowed[multiWinnerModeType];
        if (x.WinnerModeCount != 0)
        {
            for (modeIdx = 0; modeIdx < x.WinnerModeCount; modeIdx++)
                if (stats[modeIdx].Rd > thisRd) break;
            if (modeIdx == maxWinnerModeCount) return;
            if (modeIdx < maxWinnerModeCount - 1)
            {
                var spare = stats[maxWinnerModeCount - 1];
                for (int k = maxWinnerModeCount - 1; k > modeIdx; k--) stats[k] = stats[k - 1];
                stats[modeIdx] = spare;
                CopyStats(stats[modeIdx], stats[modeIdx + 1]);
            }
        }
        var st = stats[modeIdx];
        st.Mbmi.CopyFrom(mbmi);
        st.Rd = thisRd;
        st.ModeIndex = modeIndex;
        if (!cpi.FrameIsIntraOnly && rdCost is AomRdStats rc && rdCostY is AomRdStats ry && rdCostUv is AomRdStats ru)
        {
            int skipCtx = AomTxSearch.SkipTxfmContext(x.E);
            bool isIntraMode = ModeDefs[modeIndex * 3] < INTRA_MODE_END;
            bool skipTxfm = mbmi.SkipTxfm != 0 && !isIntraMode;
            st.RdCost = rc;
            if (txfmSearchDone)
            {
                st.RateY = ry.Rate + x.ModeCosts.SkipTxfmCost[skipCtx * 2 + (rc.SkipTxfm != 0 || skipTxfm ? 1 : 0)];
                st.RateUv = ru.Rate;
            }
        }
        if (colorMap != null)
        {
            GetBlockDimensions(bsize, 0, x.E, out int bw, out int bh, out _, out _);
            Array.Copy(colorMap, st.ColorIndexMap, bw * bh);
        }
        x.WinnerModeCount = Math.Min(x.WinnerModeCount + 1, maxWinnerModeCount);
    }

    // memmove leaves the old bytes in the vacated slot: fields the new entry does not overwrite (rd_cost, rate_y /
    // rate_uv, the colour map beyond what is copied) keep the shifted-away entry's values
    private static void CopyStats(AomWinnerModeStats dst, AomWinnerModeStats src)
    {
        dst.Mbmi.CopyFrom(src.Mbmi);
        dst.RdCost = src.RdCost;
        dst.Rd = src.Rd;
        dst.RateY = src.RateY;
        dst.RateUv = src.RateUv;
        Array.Copy(src.ColorIndexMap, dst.ColorIndexMap, dst.ColorIndexMap.Length);
        dst.ModeIndex = src.ModeIndex;
    }
}
