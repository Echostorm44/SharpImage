using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>MvCosts (block.h): the joint and component costs of the frame's nmv context.</summary>
internal sealed class AomMvCosts
{
    public readonly int[] NmvJointCost = new int[AomMvCost.MvJoints];
    public readonly int[][] NmvCost = { new int[AomMvCost.MvVals], new int[AomMvCost.MvVals] };
    public readonly int[][] NmvCostHp = { new int[AomMvCost.MvVals], new int[AomMvCost.MvVals] };
    public int[][] MvCostStack;

    public AomMvCosts() { MvCostStack = NmvCost; }

    /// <summary>av1_fill_mv_costs.</summary>
    public void Fill(Av1CdfMvContext nmvc, bool integerMv, bool usehp)
    {
        int precision;
        if (integerMv)
        {
            MvCostStack = NmvCost;
            precision = AomMvCost.MV_SUBPEL_NONE;
        }
        else
        {
            MvCostStack = usehp ? NmvCostHp : NmvCost;
            precision = usehp ? AomMvCost.MV_SUBPEL_HIGH_PRECISION : AomMvCost.MV_SUBPEL_LOW_PRECISION;
        }
        // av1_build_nmv_cost_table
        AomCost.CostTokensFromCdf(NmvJointCost, nmvc.Joint, AomMvCost.MvJoints);
        AomMvCost.BuildComponentCostTable(MvCostStack[0], AomMvCost.MvMax, nmvc.Comp0, precision);
        AomMvCost.BuildComponentCostTable(MvCostStack[1], AomMvCost.MvMax, nmvc.Comp1, precision);
    }

    public void CopyFrom(AomMvCosts s)
    {
        Array.Copy(s.NmvJointCost, NmvJointCost, NmvJointCost.Length);
        for (int i = 0; i < 2; i++)
        {
            Array.Copy(s.NmvCost[i], NmvCost[i], NmvCost[i].Length);
            Array.Copy(s.NmvCostHp[i], NmvCostHp[i], NmvCostHp[i].Length);
        }
        MvCostStack = ReferenceEquals(s.MvCostStack, s.NmvCostHp) ? NmvCostHp : NmvCost;
    }
}

internal sealed partial class AomMacroblock
{
    public AomMvCosts MvCosts = new();
    public readonly int[] PredMv0Sad = new int[REF_FRAMES];
    public readonly int[] PredMv1Sad = new int[REF_FRAMES];
}

// Port of libaom 3.14.1 av1/encoder/motion_search_facade.c (av1_single_motion_search), rd.c (av1_mv_pred,
// enc_clamp_mv), encodemv.c (av1_get_ref_mv / av1_get_ref_mv_from_stack) and mcomp.h (av1_set_mv_limits).
internal static class AomMotionSearch
{
    private const int AOM_INTERP_EXTEND = 4, MV_COST_WEIGHT = 108;

    /// <summary>av1_set_mv_limits.</summary>
    public static void SetMvLimits(AomCommon cm, ref AomFullMvLimits l, int miRow, int miCol, int miHeight, int miWidth, int border)
    {
        int min1 = -(miRow * 4 + border - 2 * AOM_INTERP_EXTEND);
        int min2 = -((miRow + miHeight) * 4 + 2 * AOM_INTERP_EXTEND);
        l.RowMin = Math.Max(min1, min2);
        int max1 = (cm.MiRows - miRow - miHeight) * 4 + border - 2 * AOM_INTERP_EXTEND;
        int max2 = (cm.MiRows - miRow) * 4 + 2 * AOM_INTERP_EXTEND;
        l.RowMax = Math.Min(max1, max2);
        min1 = -(miCol * 4 + border - 2 * AOM_INTERP_EXTEND);
        min2 = -((miCol + miWidth) * 4 + 2 * AOM_INTERP_EXTEND);
        l.ColMin = Math.Max(min1, min2);
        max1 = (cm.MiCols - miCol - miWidth) * 4 + border - 2 * AOM_INTERP_EXTEND;
        max2 = (cm.MiCols - miCol) * 4 + 2 * AOM_INTERP_EXTEND;
        l.ColMax = Math.Min(max1, max2);
    }

    /// <summary>av1_get_ref_mv_from_stack.</summary>
    public static AomMv GetRefMvFromStack(int refIdx, int rf0, int rf1, int refMvIdx, AomMbmiExtInter ext)
    {
        int rft = AomInter.RefFrameType(rf0, rf1);
        var stack = ext.RefMvStack[rft];
        if (rf1 > INTRA_FRAME) return refIdx != 0 ? stack[refMvIdx].CompMv : stack[refMvIdx].ThisMv;
        return refMvIdx < ext.RefMvCount[rft] ? stack[refMvIdx].ThisMv : ext.GlobalMvs[rft];
    }

    /// <summary>av1_get_ref_mv.</summary>
    public static AomMv GetRefMv(AomMacroblock x, int refIdx)
    {
        var mbmi = x.E.Mi0;
        int refMvIdx = mbmi.RefMvIdx;
        if (mbmi.Mode == NEAR_NEWMV || mbmi.Mode == NEW_NEARMV) refMvIdx += 1;
        return GetRefMvFromStack(refIdx, mbmi.RefFrame0, mbmi.RefFrame1, refMvIdx, x.MbmiExtInter);
    }

    /// <summary>av1_mv_bit_cost with x->mv_costs.</summary>
    public static int MvBitCost(AomMacroblock x, AomMv mv, AomMv refMv)
        => AomMvCost.MvBitCost(mv, refMv, x.MvCosts.NmvJointCost, x.MvCosts.MvCostStack, MV_COST_WEIGHT);

    /// <summary>enc_clamp_mv.</summary>
    private static AomMv EncClampMv(AomCommon cm, AomMacroblockD xd, AomMv mv)
    {
        int bw = xd.Width << 2, bh = xd.Height << 2;
        int pl = xd.MiCol << 2, pr = (cm.MiCols - xd.MiCol) << 2, pt = xd.MiRow << 2, pb = (cm.MiRows - xd.MiRow) << 2;
        int colMin = -((pl + bw + AOM_INTERP_EXTEND) * 8), colMax = (pr + AOM_INTERP_EXTEND) * 8;
        int rowMin = -((pt + bh + AOM_INTERP_EXTEND) * 8), rowMax = (pb + AOM_INTERP_EXTEND) * 8;
        return new AomMv(Math.Clamp((int)mv.Row, rowMin, rowMax), Math.Clamp((int)mv.Col, colMin, colMax));
    }

    /// <summary>av1_mv_pred.</summary>
    public static void MvPred(AomComp cpi, AomMacroblock x, in AomBuf2d refY, int refFrame, int bsize)
    {
        var refMv = GetRefMvFromStack(0, refFrame, NONE_FRAME, 0, x.MbmiExtInter);
        var refMv1 = GetRefMvFromStack(0, refFrame, NONE_FRAME, 1, x.MbmiExtInter);
        Span<AomMv> predMv = stackalloc AomMv[MAX_MV_REF_CANDIDATES + 1];
        int num = 0;
        predMv[num++] = refMv;
        if (refMv.AsInt != refMv1.AsInt) predMv[num++] = refMv1;
        var src = x.Plane[0].Src;
        bool zeroSeen = false;
        int bestSad = int.MaxValue, maxMv = 0;
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        for (int i = 0; i < num; ++i)
        {
            var m = EncClampMv(cpi.Cm, x.E, predMv[i]);
            int fpRow = (m.Row + 3 + (m.Row >= 0 ? 1 : 0)) >> 3;
            int fpCol = (m.Col + 3 + (m.Col >= 0 ? 1 : 0)) >> 3;
            maxMv = Math.Max(maxMv, Math.Max(Math.Abs((int)m.Row), Math.Abs((int)m.Col)) >> 3);
            if (fpRow == 0 && fpCol == 0 && zeroSeen) continue;
            zeroSeen |= fpRow == 0 && fpCol == 0;
            int roff = refY.Offset + refY.Stride * fpRow + fpCol;
            int thisSad = src.Buf16 != null
                ? (int)(AomHbd.Sad(src.Buf16, src.Offset, src.Stride, refY.Buf16!, roff, refY.Stride, w, h) >> (x.E.Bd - 8))
                : (int)AomSad.Sad(src.Buf, src.Offset, src.Stride, refY.Buf, roff, refY.Stride, w, h);
            if (thisSad < bestSad) bestSad = thisSad;
            if (i == 0) x.PredMv0Sad[refFrame] = thisSad;
            else if (i == 1) x.PredMv1Sad[refFrame] = thisSad;
        }
        x.MaxMvContext[refFrame] = maxMv;
        x.PredMvSad[refFrame] = bestSad;
    }

    /// <summary>av1_single_motion_search (SIMPLE_TRANSLATION and OBMC_CAUSAL).</summary>
    public static void SingleMotionSearch(AomComp cpi, AomMacroblock x, int bsize, int refIdx, out int rateMv, int searchRange,
        AomInterModeInfo[] modeInfo, out AomMv bestMv, AomHandleInterModeArgs? args)
    {
        var xd = x.E;
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var mbmi = xd.Mi0;
        AomTrace.Out?.Write($"sms_in {xd.MiRow} {xd.MiCol} bs {bsize} ref {mbmi.RefFrame0} idx {mbmi.RefMvIdx} mode {mbmi.Mode} mm {mbmi.MotionMode} range {searchRange} mv {mbmi.Mv0.Row} {mbmi.Mv0.Col} refmv {GetRefMv(x, refIdx).Row} {GetRefMv(x, refIdx).Col}" + (char)10);
        SingleMotionSearchCore(cpi, x, bsize, refIdx, out rateMv, searchRange, modeInfo, out bestMv, args);
        AomTrace.Out?.Write($"sms_out {bestMv.Row} {bestMv.Col} rate {rateMv}" + (char)10);
    }

    private static void SingleMotionSearchCore(AomComp cpi, AomMacroblock x, int bsize, int refIdx, out int rateMv, int searchRange,
        AomInterModeInfo[] modeInfo, out AomMv bestMv, AomHandleInterModeArgs? args)
    {
        var xd = x.E;
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var mbmi = xd.Mi0;
        var backup = new AomBuf2d[3];
        int bestsme = int.MaxValue;
        int refFrame = refIdx == 0 ? mbmi.RefFrame0 : mbmi.RefFrame1;
        var scaledRef = cpi.GetScaledRefFrame(refFrame);
        int miRow = xd.MiRow, miCol = xd.MiCol;
        var mvSf = cpi.Sf.mv_sf;
        rateMv = 0;
        if (scaledRef != null)
        {
            for (int i = 0; i < numPlanes; i++) backup[i] = xd.Plane[i].Pre(refIdx);
            AomInterPred.SetupPrePlanes(xd, refIdx, scaledRef, miRow, miCol, null, numPlanes);
        }
        int stepParam;
        if (mvSf.auto_mv_step_size != 0 && cm.ShowFrame)
            stepParam = (AomMcomp.InitSearchRange(x.MaxMvContext[refFrame]) + cpi.MvStepParam) / 2;
        else stepParam = cpi.MvStepParam;
        var refMv = GetRefMv(x, refIdx);
        AomMv startMv = mbmi.MotionMode != SIMPLE_TRANSLATION ? mbmi.Mv0.ToFullMv() : refMv.ToFullMv();
        var fullpelRefMv = startMv;
        // cand_mv_t cand[]: the start mv (and the TPL candidates without full_pixel_search_level)
        var cands = new TplCand[65];
        cands[0].Fmv = startMv; cands[0].Weight = 0;
        int cnt = 1, totalWeight = 0;
        if (mvSf.full_pixel_search_level == 0 && mbmi.MotionMode == SIMPLE_TRANSLATION)
            GetMvCandidateFromTpl(cpi, x, bsize, refFrame, cands, ref cnt, ref totalWeight);
        Span<AomMv> candMv = stackalloc AomMv[2];
        Span<int> candWeight = stackalloc int[2];
        for (int i = 0; i < 2; i++) { candMv[i] = cands[i].Fmv; candWeight[i] = cands[i].Weight; }
        int candCnt = Math.Min(2, cnt);
        bool[] candInvalid = new bool[2];
        if (mvSf.skip_fullpel_search_using_startmv_refmv != 0 && mbmi.MotionMode == SIMPLE_TRANSLATION)
        {
            for (int ci = 0; ci < candCnt; ci++)
            {
                var fmvCand = candMv[ci];
                bool skipCandMv = false;
                for (int si = 0; si < args!.StartMvCnt; si++)
                {
                    int thisRefMvIdx = args.RefMvIdxStack[si];
                    bool thisNewmvValid = args.State.SingleNewmvValid[thisRefMvIdx, refFrame] != 0;
                    if (!thisNewmvValid && thisRefMvIdx != mbmi.RefMvIdx) continue;
                    var fmvStack = args.StartMvStack[si];
                    int startRowDiff = Math.Abs(fmvStack.Row - fmvCand.Row), startColDiff = Math.Abs(fmvStack.Col - fmvCand.Col);
                    if (mbmi.Mode == NEAR_NEWMV || mbmi.Mode == NEW_NEARMV) thisRefMvIdx += 1;
                    var thisRefMv = GetRefMvFromStack(refIdx, mbmi.RefFrame0, mbmi.RefFrame1, thisRefMvIdx, x.MbmiExtInter);
                    var thisFullpelRefMv = thisRefMv.ToFullMv();
                    int refRowDiff = Math.Abs(thisFullpelRefMv.Row - fullpelRefMv.Row), refColDiff = Math.Abs(thisFullpelRefMv.Col - fullpelRefMv.Col);
                    if (mvSf.skip_fullpel_search_using_startmv_refmv >= 2)
                    {
                        if (startRowDiff <= 1 && startColDiff <= 1 && refRowDiff <= 1 && refColDiff <= 1) { skipCandMv = true; break; }
                    }
                    else if (mvSf.skip_fullpel_search_using_startmv_refmv >= 1)
                    {
                        if (startRowDiff + startColDiff <= 1 && refRowDiff + refColDiff <= 1) { skipCandMv = true; break; }
                    }
                }
                if (skipCandMv) candInvalid[ci] = true;
                else
                {
                    args.StartMvStack[args.StartMvCnt] = fmvCand;
                    args.RefMvIdxStack[args.StartMvCnt] = mbmi.RefMvIdx;
                    args.StartMvCnt++;
                }
            }
        }
        int searchMethod = AomMcomp.GetDefaultMvSearchMethod(x, mvSf, bsize);
        var srcSearchSiteCfg = cpi.SearchSites;
        if (searchRange < int.MaxValue)
        {
            var cfg = srcSearchSiteCfg[AomMcomp.SearchMethodLookupOf(searchMethod)];
            if (searchRange < 1) stepParam = cfg.NumSearchSteps;
            else
                while (cfg.Radius[cfg.NumSearchSteps - stepParam - 1] > (searchRange << 1) && cfg.NumSearchSteps - stepParam - 1 > 0) stepParam++;
        }
        var costList = new int[5];
        AomFullpelMvStats bestMvStats = default;
        AomMv secondBestMv = AomMv.Invalid;
        bestMv = AomMv.Invalid;
        bool bestValid = false;
        bool fineSearchInterval = cpi.IsScreenContentType && cpi.UpdateType == ARF_UPDATE && cpi.Speed <= 2;
        switch (mbmi.MotionMode)
        {
            case SIMPLE_TRANSLATION:
            {
                int sumWeight = 0;
                for (int m = 0; m < candCnt; m++)
                {
                    if (candInvalid[m]) continue;
                    var smv = candMv[m];
                    var p = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, refMv, srcSearchSiteCfg, searchMethod, fineSearchInterval,
                        x.MvLimits, smv);
                    AomMv thisSecond = default;
                    int thissme = AomMcomp.FullPixelSearch(smv, p, stepParam, AomSubpel.CondCostList(cpi, costList), out var thisBest,
                        out var thisStats, ref thisSecond, true);
                    if (thissme < bestsme)
                    {
                        bestsme = thissme;
                        bestMv = thisBest;
                        bestValid = true;
                        bestMvStats = thisStats;
                        secondBestMv = thisSecond;
                    }
                    sumWeight += candWeight[m];
                    if (4 * sumWeight > 3 * totalWeight) break;
                }
                break;
            }
            case OBMC_CAUSAL:
            {
                var p = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, refMv, srcSearchSiteCfg, searchMethod, fineSearchInterval,
                    x.MvLimits, startMv);
                bestsme = AomObmcSearch.ObmcFullPixelSearch(startMv, p, stepParam, x, out bestMv);
                bestValid = true;
                break;
            }
            default: throw new InvalidOperationException("invalid motion mode");
        }
        if (!bestValid || bestMv.AsInt == AomMv.Invalid.AsInt)
        {
            // (libaom returns with the scaled planes still set up; the caller sees INVALID_MV and gives up the mode)
            bestMv = AomMv.Invalid;
            return;
        }
        if (scaledRef != null)
            for (int i = 0; i < numPlanes; i++) xd.Plane[i].Pre(refIdx) = backup[i];

        // bestMv is a full-pel mv here
        if (cpi.Sf.inter_sf.skip_newmv_in_drl >= 2 && mbmi.MotionMode == SIMPLE_TRANSLATION)
        {
            var thisMv = bestMv.ToMv();
            int refMvIdx = mbmi.RefMvIdx;
            int thisMvRate = MvBitCost(x, thisMv, refMv);
            modeInfo[refMvIdx].FullSearchMv = thisMv;
            modeInfo[refMvIdx].FullMvRate = thisMvRate;
            modeInfo[refMvIdx].FullMvBestsme = bestsme;
            for (int prev = 0; prev < refMvIdx; ++prev)
            {
                if (thisMv.AsInt == modeInfo[prev].FullSearchMv.AsInt)
                {
                    int prevRateCost = modeInfo[prev].FullMvRate + modeInfo[prev].DrlCost;
                    int thisRateCost = thisMvRate + modeInfo[refMvIdx].DrlCost;
                    if (prevRateCost <= thisRateCost) { bestMv = AomMv.Invalid; return; }
                }
                int psme = modeInfo[prev].FullMvBestsme;
                if (psme == int.MaxValue) continue;
                int thr = cpi.Sf.inter_sf.skip_newmv_in_drl == 3 ? psme + (psme >> 2) : psme;
                if (cpi.Sf.inter_sf.skip_newmv_in_drl >= 3 && modeInfo[refMvIdx].FullMvBestsme > thr &&
                    modeInfo[prev].DrlCost < modeInfo[refMvIdx].DrlCost)
                {
                    bestMv = AomMv.Invalid;
                    return;
                }
            }
        }
        bool fullpel = true;
        if (cm.CurFrameForceIntegerMv) { bestMv = bestMv.ToMv(); fullpel = false; }   // convert_fullmv_to_mv
        bool useFractionalMv = bestsme < int.MaxValue && !cm.CurFrameForceIntegerMv;
        int bestMvRate = 0;
        bool mvRateCalculated = false;
        if (useFractionalMv)
        {
            var fractionalMsList = new AomMv[3];
            for (int z = 0; z < 3; z++) fractionalMsList[z] = AomMv.Invalid;   // av1_set_fractional_mv
            var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, costList, cpi.SubpelParamsScratch(x));
            var subpelStartMv = bestMv.ToMv();
            fullpel = false;
            switch (mbmi.MotionMode)
            {
                case SIMPLE_TRANSLATION:
                    if (mvSf.use_accurate_subpel_search != 0)
                    {
                        bool trySecond = secondBestMv.AsInt != AomMv.Invalid.AsInt && secondBestMv.AsInt != bestMv.AsInt &&
                                         mvSf.disable_second_mv <= 1;
                        int bestMvVar = AomSubpel.FindFractionalMvStep(cpi, ms, subpelStartMv, bestMvStats, out var bmv, out _,
                            out uint psse, fractionalMsList);
                        bestMv = bmv;
                        x.PredSse[refFrame] = psse;
                        if (trySecond)
                        {
                            var origDst = AomBufferSet.FromDst(xd);
                            long rd = long.MaxValue;
                            if (mvSf.disable_second_mv == 0)
                            {
                                mbmi.Mv0 = bestMv;
                                AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                                AomEncodeMb.SubtractPlane(x, bsize, 0);
                                var thisRdStats = new AomRdStats();
                                thisRdStats.Init();
                                AomTxSearch.EstimateTxfmYrd(cpi, x, ref thisRdStats, long.MaxValue, bsize, MaxTxsizeRectLookup[bsize]);
                                int thisMvRate = MvBitCost(x, bestMv, refMv);
                                rd = AomRd.RdCost(x.Rdmult, thisMvRate + thisRdStats.Rate, thisRdStats.Dist);
                            }
                            subpelStartMv = secondBestMv.ToMv();
                            if (AomSubpel.IsSubpelmvInRange(ms.MvLimits, subpelStartMv))
                            {
                                int thisVar = AomSubpel.FindFractionalMvStep(cpi, ms, subpelStartMv, null, out var thisBestMv, out _, out uint sse,
                                    fractionalMsList);
                                if (mvSf.disable_second_mv == 0)
                                {
                                    mbmi.Mv0 = thisBestMv;
                                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                                    AomEncodeMb.SubtractPlane(x, bsize, 0);
                                    var tmpRdStats = new AomRdStats();
                                    tmpRdStats.Init();
                                    AomTxSearch.EstimateTxfmYrd(cpi, x, ref tmpRdStats, long.MaxValue, bsize, MaxTxsizeRectLookup[bsize]);
                                    int tmpMvRate = MvBitCost(x, thisBestMv, refMv);
                                    long tmpRd = AomRd.RdCost(x.Rdmult, tmpRdStats.Rate + tmpMvRate, tmpRdStats.Dist);
                                    if (tmpRd < rd)
                                    {
                                        bestMv = thisBestMv;
                                        x.PredSse[refFrame] = sse;
                                    }
                                }
                                else if (thisVar < bestMvVar)
                                {
                                    bestMv = thisBestMv;
                                    x.PredSse[refFrame] = sse;
                                }
                            }
                        }
                    }
                    else
                    {
                        AomSubpel.FindFractionalMvStep(cpi, ms, subpelStartMv, bestMvStats, out var bmv, out _, out uint psse, null);
                        bestMv = bmv;
                        x.PredSse[refFrame] = psse;
                    }
                    break;
                case OBMC_CAUSAL:
                {
                    AomObmcSearch.FindBestObmcSubPixelTreeUp(x, ms, subpelStartMv, out var bmv, out _, out uint psse);
                    bestMv = bmv;
                    x.PredSse[refFrame] = psse;
                    break;
                }
                default: throw new InvalidOperationException("invalid motion mode");
            }
            if (cpi.Sf.inter_sf.skip_newmv_in_drl >= 1 && args != null && mbmi.MotionMode == SIMPLE_TRANSLATION &&
                bestMv.AsInt != AomMv.Invalid.AsInt)
            {
                int refMvIdx = mbmi.RefMvIdx;
                bestMvRate = MvBitCost(x, bestMv, refMv);
                mvRateCalculated = true;
                for (int prev = 0; prev < refMvIdx; ++prev)
                {
                    if (args.State.SingleNewmvValid[prev, refFrame] == 0) continue;
                    if (bestMv.AsInt == args.State.SingleNewmv[prev, refFrame].AsInt)
                    {
                        if (modeInfo[prev].Skip != 0) { modeInfo[refMvIdx].Skip = 1; break; }
                        int prevRateCost = args.State.SingleNewmvRate[prev, refFrame] + modeInfo[prev].DrlCost;
                        int thisRateCost = bestMvRate + modeInfo[refMvIdx].DrlCost;
                        if (prevRateCost <= thisRateCost) { modeInfo[refMvIdx].Skip = 1; break; }
                    }
                }
            }
        }
        // (bestsme == INT_MAX without integer mvs: libaom leaves the full-pel values in the int_mv)
        rateMv = mvRateCalculated ? bestMvRate : MvBitCost(x, bestMv, refMv);
    }

    private struct TplCand { public AomMv Fmv; public int Weight; }

    private static int RightShiftMv(int v) => (v + 3 + (v >= 0 ? 1 : 0)) >> 3;

    /// <summary>get_mv_candidate_from_tpl.</summary>
    private static void GetMvCandidateFromTpl(AomComp cpi, AomMacroblock x, int bsize, int refFrame, TplCand[] cand, ref int candCount, ref int totalCandWeight)
    {
        if (x.TplDataCount == 0) return;
        var cm = cpi.Cm;
        var xd = x.E;
        int nw = MiSizeWide[bsize] / 4, nh = MiSizeHigh[bsize] / 4;
        if (nw < 1 || nh < 1) return;
        int ofH = xd.MiRow % MiSizeHigh[cm.SbSize], ofW = xd.MiCol % MiSizeWide[cm.SbSize];
        int start = ofH / 4 * x.TplStride + ofW / 4;
        bool valid = true;
        cand[0].Weight = nw * nh;
        for (int k = 0; k < nh; k++)
        {
            for (int l = 0; l < nw; l++)
            {
                var mv = x.TplMv[(start + k * x.TplStride + l) * INTER_REFS_PER_FRAME + refFrame - LAST_FRAME];
                if (mv.AsInt == AomMv.Invalid.AsInt)
                {
                    valid = false;
                    break;
                }
                var fmv = new AomMv(AomMv.GetMvRawpel(mv.Row), AomMv.GetMvRawpel(mv.Col));
                bool unique = true;
                for (int m = 0; m < candCount; m++)
                    if (RightShiftMv(fmv.Row) == RightShiftMv(cand[m].Fmv.Row) && RightShiftMv(fmv.Col) == RightShiftMv(cand[m].Fmv.Col))
                    {
                        unique = false;
                        cand[m].Weight++;
                        break;
                    }
                if (unique)
                {
                    cand[candCount].Fmv = fmv;
                    cand[candCount].Weight = 1;
                    candCount++;
                }
            }
            if (!valid) break;
        }
        if (valid)
        {
            totalCandWeight = 2 * nh * nw;
            if (candCount > 2)
                AomPalette.MsvcrtQsort<TplCand>(cand.AsSpan(0, candCount), static (in TplCand a, in TplCand b) =>
                {
                    int diff = a.Weight - b.Weight;
                    return diff < 0 ? 1 : diff > 0 ? -1 : 0;
                });
        }
    }

    /// <summary>av1_compound_single_motion_search: a small-range full-pel search (step 5) and an eighth-pel refinement of
    /// thisMv with the compound (average or masked) prediction against secondPred; returns bestsme.</summary>
    public static int CompoundSingleMotionSearch(AomComp cpi, AomMacroblock x, int bsize, ref AomMv thisMv, AomCompoundRefs comp, out int rateMv, int refIdx)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int refFrame = refIdx == 0 ? mbmi.RefFrame0 : mbmi.RefFrame1;
        var refMv = GetRefMv(x, refIdx);
        var pd = xd.Plane[0];
        var scaledRef = cpi.GetScaledRefFrame(refFrame);
        AomBuf2d origYv12 = default;
        if (refIdx != 0)
        {
            origYv12 = pd.Pre(0);
            pd.Pre(0) = pd.Pre(refIdx);
        }
        var backup = new AomBuf2d[3];
        if (scaledRef != null)
        {
            for (int i = 0; i < numPlanes; i++) backup[i] = xd.Plane[i].Pre(refIdx);
            AomInterPred.SetupPrePlanes(xd, 0, scaledRef, xd.MiRow, xd.MiCol, null!, numPlanes);
        }
        int searchMethod = AomMcomp.GetDefaultMvSearchMethod(x, cpi.Sf.mv_sf, bsize);
        var startFullmv = thisMv.ToFullMv();
        var fp = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, refMv, cpi.SearchSites, searchMethod, false, x.MvLimits, startFullmv);
        comp.InvMask = refIdx != 0;
        fp.Comp = comp;
        fp.HasSecondPred = true;
        AomMv dummy = default;
        int bestsme = AomMcomp.FullPixelSearch(startFullmv, fp, 5, null, out var bestFull, out var bestMvStats, ref dummy, false);
        if (scaledRef != null)
            for (int i = 0; i < numPlanes; i++) xd.Plane[i].Pre(0) = backup[i];
        AomMv bestMv = bestFull;
        if (cm.CurFrameForceIntegerMv) bestMv = bestFull.ToMv();
        bool useFractionalMv = bestsme < int.MaxValue && !cm.CurFrameForceIntegerMv;
        if (useFractionalMv)
        {
            var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, null, cpi.SubpelParamsScratch(x));
            ms.Comp = comp;
            ms.HasSecondPred = true;
            ms.ForcedStop = AomSubpel.EIGHTH_PEL;
            var startMv = bestFull.ToMv();
            bestsme = AomSubpel.FindFractionalMvStep(cpi, ms, startMv, bestMvStats, out bestMv, out _, out _, null);
            ms.HasSecondPred = false;
            ms.Comp = null;
        }
        if (refIdx != 0) pd.Pre(0) = origYv12;
        if (bestsme < int.MaxValue) thisMv = bestMv;
        rateMv = MvBitCost(x, thisMv, refMv);
        return bestsme;
    }
}
