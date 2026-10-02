using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal static partial class AomRdoptInter
{
    // ---- mv candidates / drl ----

    /// <summary>check_repeat_ref_mv.</summary>
    private static bool CheckRepeatRefMv(AomMbmiExtInter ext, int refIdx, int rf0, int rf1, int singleMode)
    {
        int rft = AomInter.RefFrameType(rf0, rf1);
        int refMvCount = ext.RefMvCount[rft];
        if (singleMode == NEARESTMV) return false;
        if (singleMode == NEARMV) { if (refMvCount < 2) return true; }
        else if (singleMode == GLOBALMV)
        {
            if (refMvCount == 0) return true;
            if (refMvCount == 1) return false;
            int stackSize = Math.Min(USABLE_REF_MV_STACK_SIZE, refMvCount);
            int rf = refIdx == 0 ? rf0 : rf1;
            for (int i = 0; i < stackSize; i++)
            {
                var m = refIdx == 0 ? ext.RefMvStack[rft][i].ThisMv : ext.RefMvStack[rft][i].CompMv;
                if (m.AsInt == ext.GlobalMvs[rf].AsInt) return true;
            }
        }
        return false;
    }

    /// <summary>get_this_mv.</summary>
    private static bool GetThisMv(out AomMv thisMv, int thisMode, int refIdx, int refMvIdx, bool skipRepeatedRefMv, int rf0, int rf1,
        AomMbmiExtInter ext)
    {
        int singleMode = AomInter.GetSingleMode(thisMode, refIdx);
        int rf = refIdx == 0 ? rf0 : rf1;
        thisMv = AomMv.Invalid;
        if (singleMode == NEWMV) thisMv = AomMv.Invalid;
        else if (singleMode == GLOBALMV)
        {
            if (skipRepeatedRefMv && CheckRepeatRefMv(ext, refIdx, rf0, rf1, singleMode)) return false;
            thisMv = ext.GlobalMvs[rf];
        }
        else
        {
            int rft = AomInter.RefFrameType(rf0, rf1);
            int off = singleMode == NEARESTMV ? 0 : refMvIdx + 1;
            if (off < ext.RefMvCount[rft])
                thisMv = refIdx == 0 ? ext.RefMvStack[rft][off].ThisMv : ext.RefMvStack[rft][off].CompMv;
            else
            {
                if (skipRepeatedRefMv && CheckRepeatRefMv(ext, refIdx, rf0, rf1, singleMode)) return false;
                thisMv = ext.GlobalMvs[rf];
            }
        }
        return true;
    }

    /// <summary>skip_nearest_near_mv_using_refmv_weight.</summary>
    private static bool SkipNearestNearMvUsingRefmvWeight(AomMacroblock x, int thisMode, int rft, int bestMode)
    {
        if (thisMode != NEARESTMV && thisMode != NEARMV) return false;
        if (!AomInter.IsInterMode(bestMode)) return false;
        var xd = x.E;
        if (!xd.LeftAvailable || !xd.UpAvailable) return false;
        var ext = x.MbmiExtInter;
        var w = ext.Weight[rft];
        int refMvCount = Math.Min(MAX_REF_MV_SEARCH, (int)ext.RefMvCount[rft]);
        if (refMvCount == 0) return false;
        if (thisMode == NEARESTMV && w[0] >= REF_CAT_LEVEL) return false;
        int nearestRefmvCount = 0;
        for (int i = 0; i < refMvCount; i++) if (w[i] >= REF_CAT_LEVEL) nearestRefmvCount++;
        int pruneThresh = 1 + (refMvCount >= 2 ? 1 : 0);
        return nearestRefmvCount < pruneThresh;
    }

    /// <summary>build_cur_mv.</summary>
    private static bool BuildCurMv(AomMv[] curMv, int thisMode, AomCommon cm, AomMacroblock x, bool skipRepeatedRefMv)
    {
        var mbmi = x.E.Mi0;
        bool isComp = mbmi.HasSecondRef;
        bool ret = true;
        if (AomTrace.Out != null && isComp)
        {
            int rft0 = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
            var e = x.MbmiExtInter;
            AomTrace.Out.Write($"bcm {x.E.MiRow} {x.E.MiCol} rft {rft0} cnt {e.RefMvCount[rft0]} m {thisMode} idx {mbmi.RefMvIdx} s {e.RefMvStack[rft0][0].ThisMv.AsInt:x} {e.RefMvStack[rft0][0].CompMv.AsInt:x} g {e.GlobalMvs[mbmi.RefFrame0].AsInt:x} {e.GlobalMvs[mbmi.RefFrame1].AsInt:x}" + (char)10);
        }
        for (int i = 0; i < (isComp ? 2 : 1); ++i)
        {
            if (!GetThisMv(out var thisMv, thisMode, i, mbmi.RefMvIdx, skipRepeatedRefMv, mbmi.RefFrame0, mbmi.RefFrame1, x.MbmiExtInter)) return false;
            int singleMode = AomInter.GetSingleMode(thisMode, i);
            if (singleMode == NEWMV)
            {
                int rft = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
                curMv[i] = i == 0 ? x.MbmiExtInter.RefMvStack[rft][mbmi.RefMvIdx].ThisMv : x.MbmiExtInter.RefMvStack[rft][mbmi.RefMvIdx].CompMv;
            }
            else
            {
                ret &= ClampAndCheckMv(out var m, thisMv, cm, x);
                curMv[i] = m;
            }
        }
        return ret;
    }

    /// <summary>get_drl_cost.</summary>
    private static int GetDrlCost(AomMbModeInfo mbmi, AomMbmiExtInter ext, int[] drlModeCost0, int rft)
    {
        int cost = 0;
        if (mbmi.Mode == NEWMV || mbmi.Mode == NEW_NEWMV)
        {
            for (int idx = 0; idx < 2; ++idx)
                if (ext.RefMvCount[rft] > idx + 1)
                {
                    int drlCtx = AomInter.DrlCtx(ext.Weight[rft], idx);
                    cost += drlModeCost0[drlCtx * 2 + (mbmi.RefMvIdx != idx ? 1 : 0)];
                    if (mbmi.RefMvIdx == idx) return cost;
                }
            return cost;
        }
        if (AomInter.HaveNearmvInInterMode(mbmi.Mode))
        {
            for (int idx = 1; idx < 3; ++idx)
                if (ext.RefMvCount[rft] > idx + 1)
                {
                    int drlCtx = AomInter.DrlCtx(ext.Weight[rft], idx);
                    cost += drlModeCost0[drlCtx * 2 + (mbmi.RefMvIdx != idx - 1 ? 1 : 0)];
                    if (mbmi.RefMvIdx == idx - 1) return cost;
                }
            return cost;
        }
        return cost;
    }

    /// <summary>is_single_newmv_valid.</summary>
    private static bool IsSingleNewmvValid(AomHandleInterModeArgs args, AomMbModeInfo mbmi, int thisMode)
    {
        for (int refIdx = 0; refIdx < 2; ++refIdx)
        {
            int singleMode = AomInter.GetSingleMode(thisMode, refIdx);
            int rf = refIdx == 0 ? mbmi.RefFrame0 : mbmi.RefFrame1;
            if (singleMode == NEWMV && args.State.SingleNewmvValid[mbmi.RefMvIdx, rf] == 0) return false;
        }
        return true;
    }

    /// <summary>get_drl_refmv_count.</summary>
    private static int GetDrlRefmvCount(AomMacroblock x, int rf0, int rf1, int mode)
    {
        var ext = x.MbmiExtInter;
        int rft = AomInter.RefFrameType(rf0, rf1);
        int hasNearmv = AomInter.HaveNearmvInInterMode(mode) ? 1 : 0;
        int refMvCount = ext.RefMvCount[rft];
        bool onlyNewmv = mode == NEWMV || mode == NEW_NEWMV;
        bool hasDrl = (hasNearmv != 0 && refMvCount > 2) || (onlyNewmv && refMvCount > 1);
        return hasDrl ? Math.Min(MAX_REF_MV_SEARCH, refMvCount - hasNearmv) : 1;
    }

    /// <summary>prune_ref_mv_idx_using_qindex.</summary>
    private static bool PruneRefMvIdxUsingQindex(int reduceInterModes, int qindex, int refMvIdx)
    {
        if (reduceInterModes >= 3) return true;
        int minPruneRefMvIdx = (qindex * 3 / QINDEX_RANGE) + 1;
        return refMvIdx >= minPruneRefMvIdx;
    }

    /// <summary>ref_mv_idx_early_breakout.</summary>
    private static bool RefMvIdxEarlyBreakout(AomComp cpi, AomMacroblock x, AomHandleInterModeArgs args, long refBestRd, int refMvIdx)
    {
        var sf = cpi.Sf;
        var dist = cpi.RefFrameDistInfo;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        int rft = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
        bool isComp = mbmi.HasSecondRef;
        if (sf.inter_sf.reduce_inter_modes != 0 && refMvIdx > 0)
        {
            if (mbmi.RefFrame0 == LAST2_FRAME || mbmi.RefFrame0 == LAST3_FRAME || mbmi.RefFrame1 == LAST2_FRAME || mbmi.RefFrame1 == LAST3_FRAME)
            {
                int hasNearmv = AomInter.HaveNearmvInInterMode(mbmi.Mode) ? 1 : 0;
                if (ext.Weight[rft][refMvIdx + hasNearmv] < REF_CAT_LEVEL) return true;
            }
            if (sf.inter_sf.reduce_inter_modes >= 2 && !isComp && AomInter.HaveNewmvInInterMode(mbmi.Mode))
                if (mbmi.RefFrame0 != dist.NearestPastRef && mbmi.RefFrame0 != dist.NearestFutureRef)
                {
                    int hasNearmv = AomInter.HaveNearmvInInterMode(mbmi.Mode) ? 1 : 0;
                    bool doPrune = PruneRefMvIdxUsingQindex(sf.inter_sf.reduce_inter_modes, x.Qindex, refMvIdx);
                    if (doPrune && ext.Weight[rft][refMvIdx + hasNearmv] < REF_CAT_LEVEL) return true;
                }
        }
        mbmi.RefMvIdx = (byte)refMvIdx;
        if (isComp && !IsSingleNewmvValid(args, mbmi, mbmi.Mode)) return true;
        long estRdRate = args.RefFrameCost + args.SingleCompCost;
        int drlCost = GetDrlCost(mbmi, ext, x.ModeCosts.DrlModeCost0, rft);
        estRdRate += drlCost;
        if (AomRd.RdCost(x.Rdmult, estRdRate, 0) > refBestRd && mbmi.Mode != NEARESTMV && mbmi.Mode != NEAREST_NEARESTMV) return true;
        return false;
    }

    /// <summary>simple_translation_pred_rd.</summary>
    private static long SimpleTranslationPredRd(AomComp cpi, AomMacroblock x, AomHandleInterModeArgs args, int refMvIdx, long refBestRd, int bsize)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        int rft = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
        var cm = cpi.Cm;
        bool isComp = mbmi.HasSecondRef;
        var mc = x.ModeCosts;
        var origDst = AomBufferSet.FromDst(xd);
        AomRdStats rd = default;
        rd.Init();
        mbmi.InterinterComp.Type = COMPOUND_AVERAGE;
        mbmi.CompGroupIdx = 0;
        mbmi.CompoundIdx = 1;
        if (mbmi.RefFrame1 == INTRA_FRAME) mbmi.RefFrame1 = NONE_FRAME;
        int modeCtx = AomInter.ModeContextAnalyzer(ext.ModeContext, mbmi.RefFrame0, mbmi.RefFrame1);
        mbmi.NumProjRef = 0;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.RefMvIdx = (byte)refMvIdx;
        rd.Rate += args.RefFrameCost + args.SingleCompCost;
        rd.Rate += GetDrlCost(mbmi, ext, mc.DrlModeCost0, rft);
        var curMv = new AomMv[2];
        if (!BuildCurMv(curMv, mbmi.Mode, cm, x, false)) return long.MaxValue;
        mbmi.Mv0 = curMv[0];
        if (isComp) mbmi.Mv1 = curMv[1];
        rd.Rate += CostMvRef(mc, mbmi.Mode, modeCtx);
        if (AomRd.RdCost(x.Rdmult, rd.Rate, 0) > refBestRd) return long.MaxValue;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.NumProjRef = 0;
        if (isComp)
        {
            mbmi.InterinterComp.Type = COMPOUND_AVERAGE;
            mbmi.CompGroupIdx = 0;
            mbmi.CompoundIdx = 1;
        }
        AomInterpSearch.SetDefaultInterpFilters(mbmi, cm.InterpFilter);
        AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
        AomModelRd.SbFn(AomModelRd.MODELRD_CURVFIT, cpi, bsize, x, xd, 0, 0, out int estRate, out long estDist, out _, out _, null, null, null);
        return AomRd.RdCost(x.Rdmult, rd.Rate + estRate, estDist);
    }

    /// <summary>ref_mv_idx_to_search.</summary>
    private static int RefMvIdxToSearch(AomComp cpi, AomMacroblock x, AomHandleInterModeArgs args, long refBestRd, int bsize, int refSet)
    {
        if (refSet == 1) return 1;
        var cm = cpi.Cm;
        var mbmi = x.E.Mi0;
        int thisMode = mbmi.Mode;
        int goodIndices = 0;
        for (int i = 0; i < refSet; ++i)
        {
            if (RefMvIdxEarlyBreakout(cpi, x, args, refBestRd, i)) continue;
            goodIndices |= 1 << i;
        }
        if (cpi.Sf.inter_sf.prune_mode_search_simple_translation == 0) return goodIndices;
        if (!AomInter.HaveNearmvInInterMode(thisMode)) return goodIndices;
        if (NumPelsLog2Lookup[bsize] <= 6) return goodIndices;
        if (cm.RefScaleFactors[mbmi.RefFrame0]!.IsScaled || (mbmi.RefFrame1 > 0 && cm.RefScaleFactors[mbmi.RefFrame1]!.IsScaled)) return goodIndices;
        Span<long> idxRdcost = stackalloc long[] { long.MaxValue, long.MaxValue, long.MaxValue };
        for (int i = 0; i < refSet; ++i)
        {
            if (((goodIndices >> i) & 1) == 0) continue;
            idxRdcost[i] = SimpleTranslationPredRd(cpi, x, args, i, refBestRd, bsize);
        }
        int bestIdx = 0;
        for (int i = 1; i < MAX_REF_MV_SEARCH; ++i) if (idxRdcost[i] < idxRdcost[bestIdx]) bestIdx = i;
        double dth = mbmi.HasSecondRef ? 1.05 : 1.001;
        const double refDth = 5;
        int result = 0;
        for (int i = 0; i < refSet; ++i)
            if (((goodIndices >> i) & 1) != 0 && (1.0 * idxRdcost[i]) / idxRdcost[bestIdx] < dth && (1.0 * idxRdcost[i]) / refBestRd < refDth)
                result |= 1 << i;
        return result;
    }

    /// <summary>prune_ref_mv_idx_search.</summary>
    private static bool PruneRefMvIdxSearch(int refMvIdx, int bestRefMvIdx, AomMv[,] saveMv, AomMbModeInfo mbmi, int pruningFactor)
    {
        bool isComp = mbmi.HasSecondRef;
        int thr = (1 + (isComp ? 1 : 0)) << (pruningFactor + 1);
        if (refMvIdx > 0)
            for (int idx = 0; idx < refMvIdx; ++idx)
            {
                if (saveMv[idx, 0].AsInt == AomMv.Invalid.AsInt) continue;
                int mvDiff = Math.Abs(saveMv[idx, 0].Row - mbmi.Mv0.Row) + Math.Abs(saveMv[idx, 0].Col - mbmi.Mv0.Col);
                if (isComp) mvDiff += Math.Abs(saveMv[idx, 1].Row - mbmi.Mv1.Row) + Math.Abs(saveMv[idx, 1].Col - mbmi.Mv1.Col);
                if (bestRefMvIdx == -1 && mvDiff <= thr) return true;
            }
        if (refMvIdx < MAX_REF_MV_SEARCH - 1)
        {
            saveMv[refMvIdx, 0] = mbmi.Mv0;
            if (isComp) saveMv[refMvIdx, 1] = mbmi.Mv1;
        }
        return false;
    }

    /// <summary>prune_zero_mv_with_sse.</summary>
    private static bool PruneZeroMvWithSse(AomMacroblock x, int bsize, AomHandleInterModeArgs args, int level)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool isComp = mbmi.HasSecondRef;
        for (int idx = 0; idx < (isComp ? 2 : 1); idx++)
        {
            int rf = idx == 0 ? mbmi.RefFrame0 : mbmi.RefFrame1;
            if (xd.GlobalMotion[rf].WmType != IDENTITY) return false;
            if (args.BestSingleSseInRefs[rf] == int.MaxValue) return false;
        }
        uint thisSum = 0, bestSum = 0;
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        for (int idx = 0; idx < (isComp ? 2 : 1); idx++)
        {
            var p = x.Plane[0];
            ref var pre = ref xd.Plane[0].Pre(idx);
            uint thisSse;
            if (xd.IsHbd) AomHbd.Variance(pre.Buf16, pre.Offset, pre.Stride, p.Src.Buf16, p.Src.Offset, p.Src.Stride, 0, w, h, xd.Bd, out thisSse);
            else AomSad.Variance(pre.Buf, pre.Offset, pre.Stride, p.Src.Buf, p.Src.Offset, p.Src.Stride, w, h, out thisSse);
            thisSum += thisSse;
            int rf = idx == 0 ? mbmi.RefFrame0 : mbmi.RefFrame1;
            bestSum += args.BestSingleSseInRefs[rf];
        }
        double mul = level > 1 ? 1.00 : 1.25;
        return (double)thisSum > mul * bestSum;
    }

    // ---- inter mode rd model / candidates ----

    /// <summary>inter_mode_data_block_idx.</summary>
    private static int InterModeDataBlockIdx(int bsize)
        => bsize == BLOCK_4X4 || bsize == BLOCK_4X8 || bsize == BLOCK_8X4 || bsize == BLOCK_4X16 || bsize == BLOCK_16X4 ? -1 : 1;

    /// <summary>av1_inter_mode_data_init.</summary>
    public static void InterModeDataInit(AomTileDataEnc tile)
    {
        foreach (var md in tile.InterModeRdModels)
        {
            md.Ready = 0; md.Num = 0; md.DistSum = 0; md.LdSum = 0; md.SseSum = 0; md.SseSseSum = 0; md.SseLdSum = 0;
        }
    }

    /// <summary>get_est_rate_dist.</summary>
    private static bool GetEstRateDist(AomTileDataEnc tile, int bsize, long sse, out int estResidueCost, out long estDist)
    {
        var md = tile.InterModeRdModels[bsize];
        estResidueCost = 0; estDist = 0;
        if (md.Ready == 0) return false;
        if (sse < md.DistMean)
        {
            estResidueCost = 0;
            estDist = sse;
        }
        else
        {
            estDist = (long)Math.Round(md.DistMean, MidpointRounding.AwayFromZero);
            double estLd = md.A * sse + md.B;
            if (Math.Abs(estLd) < 1e-2) estResidueCost = int.MaxValue / 2;
            else
            {
                double c = (sse - md.DistMean) / estLd;
                estResidueCost = c < 0 ? 0 : (int)Math.Min((long)Math.Round(c, MidpointRounding.AwayFromZero), int.MaxValue / 2);
            }
            if (estResidueCost <= 0)
            {
                estResidueCost = 0;
                estDist = sse;
            }
        }
        return true;
    }

    /// <summary>av1_inter_mode_data_fit.</summary>
    public static void InterModeDataFit(AomTileDataEnc tile)
    {
        for (int bsize = 0; bsize < BLOCK_SIZES_ALL; ++bsize)
        {
            if (InterModeDataBlockIdx(bsize) == -1) continue;
            var md = tile.InterModeRdModels[bsize];
            if ((md.Ready == 0 && md.Num < 200) || (md.Ready == 1 && md.Num < 64)) continue;
            if (md.Ready == 0)
            {
                md.DistMean = md.DistSum / md.Num;
                md.LdMean = md.LdSum / md.Num;
                md.SseMean = md.SseSum / md.Num;
                md.SseSseMean = md.SseSseSum / md.Num;
                md.SseLdMean = md.SseLdSum / md.Num;
            }
            else
            {
                const double factor = 3;
                md.DistMean = (md.DistMean * factor + (md.DistSum / md.Num)) / (factor + 1);
                md.LdMean = (md.LdMean * factor + (md.LdSum / md.Num)) / (factor + 1);
                md.SseMean = (md.SseMean * factor + (md.SseSum / md.Num)) / (factor + 1);
                md.SseSseMean = (md.SseSseMean * factor + (md.SseSseSum / md.Num)) / (factor + 1);
                md.SseLdMean = (md.SseLdMean * factor + (md.SseLdSum / md.Num)) / (factor + 1);
            }
            double my = md.LdMean, mx = md.SseMean, dx = Math.Sqrt(md.SseSseMean), dxy = md.SseLdMean;
            md.A = (dxy - mx * my) / (dx * dx - mx * mx);
            md.B = my - md.A * mx;
            md.Ready = 1;
            md.Num = 0; md.DistSum = 0; md.LdSum = 0; md.SseSum = 0; md.SseSseSum = 0; md.SseLdSum = 0;
        }
    }

    /// <summary>inter_mode_data_push.</summary>
    private static void InterModeDataPush(AomTileDataEnc tile, int bsize, long sse, long dist, int residueCost)
    {
        if (residueCost == 0 || sse == dist) return;
        if (InterModeDataBlockIdx(bsize) == -1) return;
        var md = tile.InterModeRdModels[bsize];
        if (md.Num < INTER_MODE_RD_DATA_OVERALL_SIZE)
        {
            double ld = (sse - dist) * 1.0 / residueCost;
            ++md.Num;
            md.DistSum += dist;
            md.LdSum += ld;
            md.SseSum += sse;
            md.SseSseSum += (double)sse * sse;
            md.SseLdSum += sse * ld;
        }
    }

    /// <summary>inter_modes_info_push.</summary>
    private static void InterModesInfoPush(AomInterModesInfo info, int modeRate, long sse, long rd, in AomRdStats rdCost, in AomRdStats rdCostY,
        in AomRdStats rdCostUv, AomMbModeInfo mbmi)
    {
        int n = info.Num;
        info.MbmiArr[n].CopyFrom(mbmi);
        info.ModeRateArr[n] = modeRate;
        info.SseArr[n] = sse;
        info.EstRdArr[n] = rd;
        info.RdCostArr[n] = rdCost;
        info.RdCostYArr[n] = rdCostY;
        info.RdCostUvArr[n] = rdCostUv;
        ++info.Num;
    }

    /// <summary>inter_modes_info_sort (qsort with compare_rd_idx_pair: a total order).</summary>
    private static void InterModesInfoSort(AomInterModesInfo info)
    {
        if (info.Num == 0) return;
        for (int i = 0; i < info.Num; ++i) info.RdIdxPairArr[i] = (i, info.EstRdArr[i]);
        Array.Sort(info.RdIdxPairArr, 0, info.Num, Comparer.Instance);
    }

    private sealed class Comparer : System.Collections.Generic.IComparer<(int Idx, long Rd)>
    {
        public static readonly Comparer Instance = new();
        public int Compare((int Idx, long Rd) a, (int Idx, long Rd) b)
        {
            if (a.Rd == b.Rd) return a.Idx == b.Idx ? 0 : a.Idx > b.Idx ? 1 : -1;
            return a.Rd > b.Rd ? 1 : -1;
        }
    }
}
