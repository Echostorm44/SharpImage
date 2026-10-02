using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// intra_mode_search.c's inter-frame intra search (av1_handle_intra_y_mode with handle_filter_intra_mode,
// av1_search_intra_uv_modes_in_interframe) and rdopt.c's search_intra_modes_in_interframe /
// skip_intra_modes_in_interframe.
internal static partial class AomRdoptInter
{
    /// <summary>handle_filter_intra_mode.</summary>
    private static void HandleFilterIntraMode(AomComp cpi, AomMacroblock x, int bsize, AomPickModeContext ctx, ref AomRdStats rdStatsY, int modeCost,
        long bestRd, long bestRdSoFar)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        AomRdStats fi = default;
        bool selected = false;
        int bestTxSize = mbmi.TxSize;
        int bestFiMode = FILTER_DC_PRED;
        var bestTxTypeMap = new byte[32 * 32];
        CopyTxTypeMapTo(xd, bestTxTypeMap, ctx.NumFourByFourBlk);
        mbmi.UseFilterIntra = 1;
        for (int fiMode = FILTER_DC_PRED; fiMode < FILTER_INTRA_MODES; ++fiMode)
        {
            mbmi.FilterIntraMode = (byte)fiMode;
            AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref fi, bsize, bestRd);
            if (fi.Rate == int.MaxValue) continue;
            int thisRateTmp = fi.Rate + AomIntraModeSearch.IntraModeInfoCostY(cpi, x, mbmi, bsize, modeCost, false);
            long thisRdTmp = AomRd.RdCost(x.Rdmult, thisRateTmp, fi.Dist);
            if (thisRdTmp != long.MaxValue && thisRdTmp / 2 > bestRd) break;
            if (thisRdTmp < bestRdSoFar)
            {
                bestTxSize = mbmi.TxSize;
                CopyTxTypeMapTo(xd, bestTxTypeMap, ctx.NumFourByFourBlk);
                bestFiMode = fiMode;
                rdStatsY = fi;
                selected = true;
                bestRdSoFar = thisRdTmp;
            }
        }
        mbmi.TxSize = bestTxSize;
        CopyTxTypeMapFrom(xd, bestTxTypeMap, ctx.NumFourByFourBlk);
        if (selected)
        {
            mbmi.UseFilterIntra = 1;
            mbmi.FilterIntraMode = (byte)bestFiMode;
        }
        else mbmi.UseFilterIntra = 0;
    }

    /// <summary>av1_handle_intra_y_mode.</summary>
    private static bool HandleIntraYMode(AomIntraModeSearchState iss, AomComp cpi, AomMacroblock x, int bsize, uint refFrameCost, AomPickModeContext ctx,
        ref AomRdStats rdStatsY, long bestRd, out int modeCostY, out long rdY, ref long bestModelRd, Span<long> topIntraModelRd)
    {
        var isf = cpi.Sf.intra_sf;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int mode = mbmi.Mode;
        var mc = x.ModeCosts;
        int modeCost = mc.MbmodeCost[SizeGroupLookup[bsize] * 13 + mode] + (int)refFrameCost;
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        int knownRate = modeCost + mc.SkipTxfmCost[skipCtx * 2 + 0];
        long knownRd = AomRd.RdCost(x.Rdmult, knownRate, 0);
        modeCostY = 0;
        rdY = long.MaxValue;
        if (knownRd > bestRd)
        {
            iss.SkipIntraModes = true;
            return false;
        }
        bool isDirectional = mode >= V_PRED && mode <= D67_PRED;
        if (isDirectional && bsize >= BLOCK_8X8 && cpi.EnableAngleDelta)
        {
            if (isf.intra_pruning_with_hog != 0 && !iss.DirModeSkipMaskReady)
            {
                float[] thresh = { -1.2f, 0.0f, 0.0f, 1.2f };
                AomIntraModeSearch.PruneIntraModeWithHog(cpi, x, bsize, thresh[isf.intra_pruning_with_hog - 1], iss.DirectionalModeSkipMask, false);
                iss.DirModeSkipMaskReady = true;
            }
            if (iss.DirectionalModeSkipMask[mode] != 0) return false;
        }
        int txSize = Math.Min(TX_32X32, (int)MaxTxsizeLookup[bsize]);
        long thisModelRd = AomIntraModeSearch.IntraModelRd(cpi, x, 0, bsize, txSize, true);
        int modelRdIndexForPruning = AomIntraModeSearch.GetModelRdIndexForPruning(x, cpi.Sf);
        if (AomIntraModeSearch.PruneIntraYMode(thisModelRd, ref bestModelRd, topIntraModelRd, isf.top_intra_model_count_allowed, modelRdIndexForPruning))
            return false;
        rdStatsY.Init();
        AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref rdStatsY, bsize, bestRd);
        if (mode == DC_PRED && AomIntraModeSearch.FilterIntraAllowedBsize(cpi, bsize))
        {
            bool tryFilterIntra = true;
            long bestRdSoFar = long.MaxValue;
            if (rdStatsY.Rate != int.MaxValue)
            {
                mbmi.UseFilterIntra = 0;
                int tmpRate = rdStatsY.Rate + AomIntraModeSearch.IntraModeInfoCostY(cpi, x, mbmi, bsize, modeCost, false);
                bestRdSoFar = AomRd.RdCost(x.Rdmult, tmpRate, rdStatsY.Dist);
                tryFilterIntra = bestRdSoFar / 2 <= bestRd;
            }
            else if (isf.skip_filter_intra_in_inter_frames >= 1) tryFilterIntra = false;
            if (tryFilterIntra) HandleFilterIntraMode(cpi, x, bsize, ctx, ref rdStatsY, modeCost, bestRd, bestRdSoFar);
        }
        if (rdStatsY.Rate == int.MaxValue) return false;
        modeCostY = AomIntraModeSearch.IntraModeInfoCostY(cpi, x, mbmi, bsize, modeCost, false);
        int rateY = rdStatsY.SkipTxfm != 0 ? mc.SkipTxfmCost[skipCtx * 2 + 1] : rdStatsY.Rate;
        rdY = AomRd.RdCost(x.Rdmult, rateY + modeCostY, rdStatsY.Dist);
        if (bestRd < long.MaxValue / 2 && rdY > bestRd + (bestRd >> 2))
        {
            iss.SkipIntraModes = true;
            return false;
        }
        return true;
    }

    /// <summary>av1_search_intra_uv_modes_in_interframe.</summary>
    private static bool SearchIntraUvModesInInterframe(AomIntraModeSearchState iss, AomComp cpi, AomMacroblock x, int bsize, ref AomRdStats rdStats,
        in AomRdStats rdStatsY, ref AomRdStats rdStatsUv, long bestRd)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool tryPalette = cpi.EnablePalette && AomIntraModeSearch.AllowPalette(cpi.AllowScreenContentTools, mbmi.Bsize);
        int uvTx = AomEncodeMb.GetTxSize(1, xd);
        AomIntraModeSearch.RdPickIntraSbuvMode(cpi, x, ref iss.RateUvIntra, ref iss.RateUvTokenonly, ref iss.DistUvs, ref iss.SkipUvs, bsize, uvTx);
        iss.ModeUv = mbmi.UvMode;
        if (tryPalette)
        {
            iss.PmiUv.PaletteSize0 = mbmi.Palette.PaletteSize0;
            iss.PmiUv.PaletteSize1 = mbmi.Palette.PaletteSize1;
            Array.Copy(mbmi.Palette.PaletteColors, iss.PmiUv.PaletteColors, iss.PmiUv.PaletteColors.Length);
        }
        iss.UvAngleDelta = mbmi.AngleDelta[1];
        long uvRd = AomRd.RdCost(x.Rdmult, iss.RateUvTokenonly, iss.DistUvs);
        if (uvRd > bestRd)
        {
            iss.SkipIntraModes = true;
            return false;
        }
        rdStatsUv.Rate = iss.RateUvTokenonly;
        rdStatsUv.Dist = iss.DistUvs;
        rdStatsUv.SkipTxfm = iss.SkipUvs;
        rdStats.SkipTxfm = (byte)(rdStatsY.SkipTxfm != 0 && rdStatsUv.SkipTxfm != 0 ? 1 : 0);
        mbmi.UvMode = iss.ModeUv;
        if (tryPalette)
        {
            mbmi.Palette.PaletteSize1 = iss.PmiUv.PaletteSize1;
            Array.Copy(iss.PmiUv.PaletteColors, AomPaletteModeInfo.PaletteMaxSize, mbmi.Palette.PaletteColors, AomPaletteModeInfo.PaletteMaxSize,
                2 * AomPaletteModeInfo.PaletteMaxSize);
        }
        mbmi.AngleDelta[1] = iss.UvAngleDelta;
        return true;
    }

    /// <summary>search_intra_modes_in_interframe.</summary>
    private static void SearchIntraModesInInterframe(AomInterModeSearchState st, AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize,
        AomPickModeContext ctx, AomInterModeSfArgs sfArgs, uint intraRefFrameCost, long yrdThreshold)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var iss = st.IntraSearchState;
        bool isBestYModeIntra = false;
        AomRdStats bestIntraRdStatsY = default;
        long bestRdY = long.MaxValue;
        int bestModeCostY = -1;
        var bestMbmi = x.ScratchIntraBestMbmi;
        bestMbmi.CopyFrom(mbmi);
        int bestModeEnum = THR_INVALID;
        var bestTxTypeMap = new byte[32 * 32];
        int num4x4 = AomTxSearch.BsizeToNumBlk(bsize);
        long bestModelRd = long.MaxValue;
        Span<long> topIntraModelRd = stackalloc long[TOP_INTRA_MODEL_COUNT];
        topIntraModelRd.Fill(long.MaxValue);
        if (cpi.Sharpness != 0)
        {
            int bh = MiSizeHigh[bsize], bw = MiSizeWide[bsize];
            if (bh > 4 || bw > 4) return;
        }
        mbmi.SkipTxfm = 0;
        for (int modeIdx = 0; modeIdx < LUMA_MODE_COUNT; ++modeIdx)
        {
            if (sf.intra_sf.skip_intra_in_interframe != 0 && iss.SkipIntraModes) break;
            AomIntraModeSearch.SetYModeAndDeltaAngle(modeIdx, mbmi, sf.intra_sf.prune_luma_odd_delta_angles_in_intra != 0);
            if ((sfArgs.ModeSkipMask.PredModes[INTRA_FRAME] & (1u << mbmi.Mode)) != 0) continue;
            int modeEnum = GetPredictionModeIdx(mbmi.Mode, INTRA_FRAME, NONE_FRAME);
            if ((!cpi.EnableSmoothIntra || sf.intra_sf.disable_smooth_intra != 0) &&
                (mbmi.Mode == SMOOTH_PRED || mbmi.Mode == SMOOTH_H_PRED || mbmi.Mode == SMOOTH_V_PRED))
                continue;
            if (!cpi.EnablePaethIntra && mbmi.Mode == PAETH_PRED) continue;
            bool directional = mbmi.Mode >= V_PRED && mbmi.Mode <= D67_PRED;
            if (directional && !(bsize >= BLOCK_8X8 && cpi.EnableAngleDelta) && mbmi.AngleDelta[0] != 0) continue;
            int thisMode = mbmi.Mode;
            InitMbmi(mbmi, thisMode, INTRA_FRAME, NONE_FRAME, cm);
            x.TxfmSkip = 0;
            if (thisMode != DC_PRED)
            {
                if ((sf.rt_sf.mode_search_skip_flags & FLAG_SKIP_INTRA_BESTINTER) != 0 && thisMode >= D45_PRED && thisMode <= PAETH_PRED)
                    if (st.BestModeIndex != THR_INVALID && st.BestMbmode.RefFrame0 > INTRA_FRAME) continue;
                if ((sf.rt_sf.mode_search_skip_flags & FLAG_SKIP_INTRA_DIRMISMATCH) != 0)
                    if (ConditionalSkipintra(thisMode, iss.BestIntraMode)) continue;
            }
            AomRdStats intraRdStatsY = default;
            bool valid = HandleIntraYMode(iss, cpi, x, bsize, intraRefFrameCost, ctx, ref intraRdStatsY, st.BestRd, out int modeCostY, out long intraRdY,
                ref bestModelRd, topIntraModelRd);
            if (intraRdY < long.MaxValue) AdjustCost(cpi, x, ref intraRdY, false);
            if (valid && intraRdY < yrdThreshold)
            {
                isBestYModeIntra = true;
                if (intraRdY < bestRdY)
                {
                    bestIntraRdStatsY = intraRdStatsY;
                    bestModeCostY = modeCostY;
                    bestRdY = intraRdY;
                    bestMbmi.CopyFrom(mbmi);
                    bestModeEnum = modeEnum;
                    CopyTxTypeMapTo(xd, bestTxTypeMap, num4x4);
                }
            }
        }
        if (!isBestYModeIntra) return;
        mbmi.CopyFrom(bestMbmi);
        CopyTxTypeMapFrom(xd, bestTxTypeMap, num4x4);
        AomRdStats intraRdStats = default, intraRdStatsUv = default;
        intraRdStats.Init();
        intraRdStatsUv.Init();
        int numPlanes = cm.NumPlanes;
        if (numPlanes > 1)
            if (!SearchIntraUvModesInInterframe(iss, cpi, x, bsize, ref intraRdStats, bestIntraRdStatsY, ref intraRdStatsUv, st.BestRd)) return;
        intraRdStats.Rate = bestIntraRdStatsY.Rate + bestModeCostY;
        if (xd.Lossless[mbmi.SegmentId] == 0 && AomTxSearch.BlockSignalsTxsize(bsize))
            bestIntraRdStatsY.Rate -= AomTxSearch.TxSizeCost(x, bsize, mbmi.TxSize);
        var mc = x.ModeCosts;
        int mode = mbmi.Mode;
        if (numPlanes > 1 && xd.IsChromaRef)
        {
            int cflAllowed = AomCfl.IsCflAllowed(xd);
            int uvModeCost = mc.IntraUvModeCost[(cflAllowed * 13 + mode) * 14 + mbmi.UvMode];
            intraRdStats.Rate += intraRdStatsUv.Rate + AomIntraModeSearch.IntraModeInfoCostUv(cpi, x, mbmi, bsize, uvModeCost);
        }
        intraRdStats.SkipTxfm = 0;
        intraRdStats.Dist = bestIntraRdStatsY.Dist + intraRdStatsUv.Dist;
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        intraRdStats.Rate += mc.SkipTxfmCost[skipCtx * 2 + 0];
        long thisRd = AomRd.RdCost(x.Rdmult, intraRdStats.Rate, intraRdStats.Dist);
        if (thisRd < st.BestIntraRd)
        {
            st.BestIntraRd = thisRd;
            iss.BestIntraMode = mode;
        }
        for (int i = 0; i < REFERENCE_MODES; ++i) st.BestPredRd[i] = Math.Min(st.BestPredRd[i], thisRd);
        intraRdStats.Rdcost = thisRd;
        AdjustRdcost(cpi, x, ref intraRdStats, false);
        AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, intraRdStats, bestIntraRdStatsY, intraRdStatsUv, bestModeEnum, null, bsize, intraRdStats.Rdcost,
            sf.winner_mode_sf.multi_winner_mode_type, true);
        if (intraRdStats.Rdcost < st.BestRd)
            UpdateSearchState(st, ref rdCost, ctx, intraRdStats, bestIntraRdStatsY, intraRdStatsUv, bestModeEnum, x, true);
    }

    /// <summary>conditional_skipintra.</summary>
    private static bool ConditionalSkipintra(int mode, int bestIntraMode)
    {
        if (mode == D113_PRED && bestIntraMode != V_PRED && bestIntraMode != D135_PRED) return true;
        if (mode == D67_PRED && bestIntraMode != V_PRED && bestIntraMode != D45_PRED) return true;
        if (mode == D203_PRED && bestIntraMode != H_PRED && bestIntraMode != D45_PRED) return true;
        if (mode == D157_PRED && bestIntraMode != H_PRED && bestIntraMode != D135_PRED) return true;
        return false;
    }

    /// <summary>skip_intra_modes_in_interframe.</summary>
    private static void SkipIntraModesInInterframe(AomComp cpi, AomMacroblock x, int bsize, AomInterModeSearchState st, long interCost, long intraCost)
    {
        var sf = cpi.Sf;
        bool comp = st.BestMbmode.RefFrame1 > INTRA_FRAME;
        if (sf.rt_sf.prune_intra_mode_based_on_mv_range != 0 && bsize > sf.part_sf.max_intra_bsize && !comp)
        {
            var bestMv = st.BestMbmode.Mv0;
            int mvThresh = 16 << sf.rt_sf.prune_intra_mode_based_on_mv_range;
            if (Math.Abs((int)bestMv.Row) < mvThresh && Math.Abs((int)bestMv.Col) < mvThresh && x.SourceVariance > 128)
            {
                st.IntraSearchState.SkipIntraModes = true;
                return;
            }
        }
        const uint srcVarThreshIntraSkip = 1;
        int skipIntraInInterframe = sf.intra_sf.skip_intra_in_interframe;
        if (!(skipIntraInInterframe != 0 && x.SourceVariance > srcVarThreshIntraSkip)) return;
        if (skipIntraInInterframe >= 2 && st.BestMbmode.SkipTxfm != 0)
        {
            int[] qindexThresh = { 200, 255 };
            int ind = skipIntraInInterframe >= 3 ? 1 : 0;
            if (!AomInter.HaveNewmvInInterMode(st.BestMbmode.Mode) && x.Qindex <= qindexThresh[ind])
            {
                st.IntraSearchState.SkipIntraModes = true;
                return;
            }
            if (skipIntraInInterframe >= 4 && (interCost < 0 || intraCost < 0))
            {
                st.IntraSearchState.SkipIntraModes = true;
                return;
            }
        }
        if (interCost >= 0 && intraCost >= 0) throw new NotImplementedException("skip_intra_modes_in_interframe NN (TPL costs)");
    }
}
