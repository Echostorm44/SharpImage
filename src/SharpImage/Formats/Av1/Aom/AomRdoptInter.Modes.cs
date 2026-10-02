using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal static partial class AomRdoptInter
{
    /// <summary>motion_mode_allowed (blockd.h).</summary>
    internal static int MotionModeAllowed(AomWarpedMotionParams[] gmParams, AomMacroblockD xd, AomMbModeInfo mbmi, bool allowWarpedMotion)
    {
        if (mbmi.OverlappableNeighbors == 0) return SIMPLE_TRANSLATION;
        if (!xd.CurFrameForceIntegerMv)
            if (AomInter.IsGlobalMvBlock(mbmi, gmParams[mbmi.RefFrame0].WmType)) return SIMPLE_TRANSLATION;
        if (AomInter.IsMotionVariationAllowedBsize(mbmi.Bsize) && AomInter.IsInterMode(mbmi.Mode) && mbmi.RefFrame1 != INTRA_FRAME &&
            !mbmi.HasSecondRef)
        {
            if (mbmi.NumProjRef >= 1 && allowWarpedMotion && !xd.CurFrameForceIntegerMv && !xd.BlockRefScaleFactors[0]!.IsScaled)
                return WARPED_CAUSAL;
            return OBMC_CAUSAL;
        }
        return SIMPLE_TRANSLATION;
    }

    /// <summary>motion_mode_rd.</summary>
    private static long MotionModeRd(AomComp cpi, AomMacroblock x, int bsize, ref AomRdStats rdStats, ref AomRdStats rdStatsY, ref AomRdStats rdStatsUv,
        AomHandleInterModeArgs args, long refBestRd, long[] refSkipRd, ref int rateMv, AomBufferSet origDst, ref long bestEstRd, bool doTxSearch,
        AomInterModesInfo interModesInfo, bool evalMotionMode, out long yrd)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool isComp = mbmi.HasSecondRef;
        int thisMode = mbmi.Mode;
        int rate2Nocoeff = rdStats.Rate;
        int bestXskipTxfm = 0;
        AomRdStats bestRdStats = default, bestRdStatsY = default, bestRdStatsUv = default;
        var bestTxTypeMap = new byte[32 * 32];
        int rateMv0 = rateMv;
        bool interintraAllowed = cpi.Seq!.EnableInterintraCompound && AomInter.IsInterintraAllowed(mbmi) && mbmi.CompoundIdx != 0;
        var warpInfo = x.WarpSampleInfo[mbmi.RefFrame0];
        int refFrame1 = mbmi.RefFrame1;
        bestRdStats.Invalidate();
        mbmi.NumProjRef = 1;
        int lastMotionModeAllowed = SIMPLE_TRANSLATION;
        yrd = long.MaxValue;
        if (cm.SwitchableMotionMode) lastMotionModeAllowed = MotionModeAllowed(xd.GlobalMotion, xd, mbmi, cm.AllowWarpedMotion);
        if (lastMotionModeAllowed == WARPED_CAUSAL)
        {
            if (warpInfo.Num < 0) warpInfo.Num = AomWarp.FindSamples(cm, xd, warpInfo.Pts, warpInfo.PtsInref);
            mbmi.NumProjRef = (byte)warpInfo.Num;
        }
        int totalSamples = mbmi.NumProjRef;
        if (totalSamples == 0) lastMotionModeAllowed = OBMC_CAUSAL;
        var baseMbmi = x.ScratchBaseMbmi;
        baseMbmi.CopyFrom(mbmi);
        var bestMbmi = x.ScratchBestMotionMbmi;
        int interpFilter = cm.InterpFilter;
        int switchableRate = AomInterpSearch.IsInterpNeeded(xd) ? AomInterpSearch.GetSwitchableRateFrame(x, xd, interpFilter, cpi.Seq.EnableDualFilter) : 0;
        long bestRd = long.MaxValue;
        int bestRateMv = rateMv0;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        int txfmRdGateLevel = GetTxfmRdGateLevel(cpi.Seq.EnableMaskedCompound, cpi.Sf.inter_sf.txfm_rd_gate_level, bsize, TX_SEARCH_MOTION_MODE, evalMotionMode);
        UpdateModeStartEndIndex(cpi, mbmi, out int modeIndexStart, out int modeIndexEnd, lastMotionModeAllowed, interintraAllowed, evalMotionMode);
        for (int modeIndex = modeIndexStart; modeIndex <= modeIndexEnd; modeIndex++)
        {
            if (args.SkipMotionMode != 0 && modeIndex != 0) continue;
            int tmpRate2 = rate2Nocoeff;
            bool isInterintraMode = modeIndex > lastMotionModeAllowed;
            int tmpRateMv = rateMv0;
            mbmi.CopyFrom(baseMbmi);
            mbmi.MotionMode = (byte)(isInterintraMode ? SIMPLE_TRANSLATION : modeIndex);
            if (cpi.Sharpness == 3 && (mbmi.MotionMode == OBMC_CAUSAL || mbmi.MotionMode == WARPED_CAUSAL)) continue;
            bool pruneObmc = cpi.FrameProbs.ObmcProbs[cpi.UpdateType * BLOCK_SIZES_ALL + bsize] < cpi.Sf.inter_sf.prune_obmc_prob_thresh;
            if ((!cpi.EnableObmc || pruneObmc) && mbmi.MotionMode == OBMC_CAUSAL) continue;

            if (mbmi.MotionMode == SIMPLE_TRANSLATION && !isInterintraMode) { }
            else if (mbmi.MotionMode == OBMC_CAUSAL)
            {
                uint curMv = mbmi.Mv0.AsInt;
                if (AomInter.HaveNewmvInInterMode(thisMode))
                {
                    AomMotionSearch.SingleMotionSearch(cpi, x, bsize, 0, out tmpRateMv, int.MaxValue, Array.Empty<AomInterModeInfo>(), out var mv, null);
                    mbmi.Mv0 = mv;
                    tmpRate2 = rate2Nocoeff - rateMv0 + tmpRateMv;
                }
                if (mbmi.Mv0.AsInt != curMv || evalMotionMode)
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, 0, numPlanes - 1, cpi.EnableIntraEdgeFilter);
                AomInterPred.BuildObmcInterPrediction(cm, xd, args.AbovePredBuf, args.LeftPredBuf);
            }
            else if (mbmi.MotionMode == WARPED_CAUSAL)
            {
                var pts = x.ScratchWarpPts;
                var ptsInref = x.ScratchWarpPtsInref;
                mbmi.MotionMode = WARPED_CAUSAL;
                mbmi.WmParams.WmType = DEFAULT_WMTYPE;
                AomInterpSearch.SetDefaultInterpFilters(mbmi, interpFilter);
                Array.Copy(warpInfo.Pts, pts, totalSamples * 2);
                Array.Copy(warpInfo.PtsInref, ptsInref, totalSamples * 2);
                if (mbmi.NumProjRef > 1) mbmi.NumProjRef = (byte)AomWarp.SelectSamples(mbmi.Mv0, pts, ptsInref, mbmi.NumProjRef, bsize);
                if (!AomWarp.FindProjection(mbmi.NumProjRef, pts, ptsInref, bsize, mbmi.Mv0.Row, mbmi.Mv0.Col, mbmi.WmParams, miRow, miCol))
                {
                    if (AomInter.HaveNewmvInInterMode(thisMode))
                    {
                        var mv0 = mbmi.Mv0;
                        var wm0 = mbmi.WmParams.Clone();
                        byte numProjRef0 = mbmi.NumProjRef;
                        var refMv = AomMotionSearch.GetRefMv(x, 0);
                        var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, null);
                        AomWarp.RefineWarpedMv(xd, cm, ms, bsize, warpInfo.Pts, warpInfo.PtsInref, totalSamples, cpi.Sf.mv_sf.warp_search_method,
                            cpi.Sf.mv_sf.warp_search_iters, cpi.EnableIntraEdgeFilter);
                        if (mv0.AsInt != mbmi.Mv0.AsInt)
                        {
                            tmpRateMv = AomMotionSearch.MvBitCost(x, mbmi.Mv0, refMv);
                            tmpRate2 = rate2Nocoeff - rateMv0 + tmpRateMv;
                        }
                        else
                        {
                            mbmi.Mv0 = mv0;
                            mbmi.WmParams.CopyFrom(wm0);
                            mbmi.NumProjRef = numProjRef0;
                        }
                    }
                    AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 0, numPlanes - 1, cpi.EnableIntraEdgeFilter);
                }
                else continue;
            }
            else if (isInterintraMode)
            {
                int ret = AomInterIntraSearch.HandleInterIntraMode(cpi, x, bsize, mbmi, args, refBestRd, ref tmpRateMv, ref tmpRate2, origDst);
                if (ret < 0) continue;
            }

            if (!CheckNewmvJointNonzero(cm, x)) continue;

            x.TxfmSkip = 0;
            rdStats.Dist = 0;
            rdStats.Sse = 0;
            rdStats.SkipTxfm = 1;
            rdStats.Rate = tmpRate2;
            var mc = x.ModeCosts;
            if (mbmi.MotionMode != WARPED_CAUSAL) rdStats.Rate += switchableRate;
            if (interintraAllowed)
                rdStats.Rate += mc.InterintraCost[SizeGroupLookup[bsize] * 2 + (mbmi.RefFrame1 == INTRA_FRAME ? 1 : 0)];
            if (lastMotionModeAllowed > SIMPLE_TRANSLATION && mbmi.RefFrame1 != INTRA_FRAME)
            {
                if (lastMotionModeAllowed == WARPED_CAUSAL) rdStats.Rate += mc.MotionModeCost[bsize * 3 + mbmi.MotionMode];
                else rdStats.Rate += mc.MotionModeCost1[bsize * 2 + mbmi.MotionMode];
            }

            long thisYrd = long.MaxValue;
            if (!doTxSearch)
            {
                long currSse = -1, sseY = -1;
                int estResidueCost = 0;
                long estDist = 0;
                if (cpi.Sf.inter_sf.inter_mode_rd_model_estimation == 1)
                {
                    currSse = GetSse(cpi, x, out sseY);
                    GetEstRateDist(x.TileData!, bsize, currSse, out estResidueCost, out estDist);
                }
                else if (cpi.Sf.inter_sf.inter_mode_rd_model_estimation == 2 || cpi.Sf.rt_sf.use_nonrd_pick_mode != 0)
                {
                    AomModelRd.SbFn(AomModelRd.MODELRD_TYPE_MOTION_MODE_RD, cpi, bsize, x, xd, 0, numPlanes - 1, out estResidueCost, out estDist, out _,
                        out currSse, null, null, null);
                    sseY = x.PredSse[mbmi.RefFrame0];
                }
                long estRd = AomRd.RdCost(x.Rdmult, rdStats.Rate + estResidueCost, estDist);
                if (estRd * 0.80 > bestEstRd)
                {
                    mbmi.RefFrame1 = refFrame1;
                    continue;
                }
                int modeRate0 = rdStats.Rate;
                rdStats.Rate += estResidueCost;
                rdStats.Dist = estDist;
                rdStats.Rdcost = estRd;
                if (rdStats.Rdcost < bestEstRd)
                {
                    bestEstRd = rdStats.Rdcost;
                    refSkipRd[1] = txfmRdGateLevel != 0 ? AomRd.RdCost(x.Rdmult, modeRate0, sseY << 4) : long.MaxValue;
                }
                if (cm.ReferenceMode == SINGLE_REFERENCE)
                {
                    if (!isComp) InterModesInfoPush(interModesInfo, modeRate0, currSse, rdStats.Rdcost, rdStats, rdStatsY, rdStatsUv, mbmi);
                }
                else InterModesInfoPush(interModesInfo, modeRate0, currSse, rdStats.Rdcost, rdStats, rdStatsY, rdStatsUv, mbmi);
                mbmi.SkipTxfm = 0;
            }
            else
            {
                long skipRd = long.MaxValue, skipRdy = long.MaxValue;
                if (txfmRdGateLevel != 0)
                {
                    long currSse = GetSse(cpi, x, out long sseY);
                    skipRd = AomRd.RdCost(x.Rdmult, rdStats.Rate, currSse);
                    skipRdy = AomRd.RdCost(x.Rdmult, rdStats.Rate, sseY << 4);
                    if (!CheckTxfmEval(x, bsize, refSkipRd[0], skipRd, txfmRdGateLevel, false)) continue;
                }
                int modeRate = rdStats.Rate;
                if (!AomTxSearch.TxfmSearch(cpi, x, bsize, ref rdStats, ref rdStatsY, ref rdStatsUv, rdStats.Rate, refBestRd))
                {
                    if (rdStatsY.Rate == int.MaxValue && modeIndex == 0) return long.MaxValue;
                    continue;
                }
                if (AomTrace.Out != null)
                {
                    ulong hh = 1469598103934665603UL;
                    for (int pl = 0; pl < cm.NumPlanes; pl++)
                    {
                        var pd = xd.Plane[pl];
                        int bw = BlockSizeWide[bsize] >> pd.SubsamplingX, bh = BlockSizeHigh[bsize] >> pd.SubsamplingY;
                        for (int r = 0; r < bh; r++)
                            for (int c = 0; c < bw; c++)
                            {
                                int v = pd.Dst.Buf16 != null ? pd.Dst.Buf16[pd.Dst.Offset + r * pd.Dst.Stride + c] : pd.Dst.Buf[pd.Dst.Offset + r * pd.Dst.Stride + c];
                                hh = (hh ^ (uint)v) * 1099511628211UL;
                            }
                    }
                    AomTrace.Out.Write($"pred mv {mbmi.Mv0.Row} {mbmi.Mv0.Col} {mbmi.Mv1.Row} {mbmi.Mv1.Col} hash {hh:x16}" + (char)10);
                }
                AomTrace.Out?.Write($"mmt {modeIndex} mm {mbmi.MotionMode} ii {(mbmi.RefFrame1 == INTRA_FRAME ? 1 : 0)} if {mbmi.InterpFilters:x} mrate {modeRate} y {rdStatsY.Rate} {rdStatsY.Dist} uv {rdStatsUv.Rate} {rdStatsUv.Dist} tot {rdStats.Rate} {rdStats.Dist} skip {rdStats.SkipTxfm}" + (char)10);
                int skipCtx = AomTxSearch.SkipTxfmContext(xd);
                int yRate = rdStats.SkipTxfm != 0 ? x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 1] : rdStatsY.Rate + x.ModeCosts.SkipTxfmCost[skipCtx * 2 + 0];
                thisYrd = AomRd.RdCost(x.Rdmult, yRate + modeRate, rdStatsY.Dist);
                long currRd = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
                if (currRd < refBestRd)
                {
                    refBestRd = currRd;
                    refSkipRd[0] = skipRd;
                    refSkipRd[1] = skipRdy;
                }
                if (cpi.Sf.inter_sf.inter_mode_rd_model_estimation == 1)
                    InterModeDataPush(x.TileData!, mbmi.Bsize, rdStats.Sse, rdStats.Dist,
                        rdStatsY.Rate + rdStatsUv.Rate + mc.SkipTxfmCost[skipCtx * 2 + mbmi.SkipTxfm]);
            }

            if (thisMode == GLOBALMV || thisMode == GLOBAL_GLOBALMV)
                if (AomInter.IsNontransGlobalMotion(xd, mbmi))
                    mbmi.InterpFilters = AomInterpSearch.Broadcast(AomInterpSearch.Unswitchable(interpFilter));

            if (thisYrd < long.MaxValue) AdjustCost(cpi, x, ref thisYrd, true);
            AdjustRdcost(cpi, x, ref rdStats, true);
            if (!doTxSearch || rdStatsY.Rdcost < long.MaxValue) AdjustRdcost(cpi, x, ref rdStatsY, true);

            long tmpRd = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
            if (modeIndex == 0) args.State.SimpleRd[thisMode, mbmi.RefMvIdx, mbmi.RefFrame0] = tmpRd;
            long bestScaledRd = bestRd, thisScaledRd = tmpRd;
            if (modeIndex != 0)
                IncreaseMotionModeRd(bestMbmi, mbmi, ref bestScaledRd, ref thisScaledRd, cpi.Sf.inter_sf.bias_warp_mode_rd_scale_pct,
                    cpi.Sf.inter_sf.bias_obmc_mode_rd_scale_pct);
            if (modeIndex == 0 || thisScaledRd < bestScaledRd)
            {
                bestMbmi.CopyFrom(mbmi);
                bestRd = tmpRd;
                bestRdStats = rdStats;
                bestRdStatsY = rdStatsY;
                bestRateMv = tmpRateMv;
                yrd = thisYrd;
                if (numPlanes > 1) bestRdStatsUv = rdStatsUv;
                CopyTxTypeMapTo(xd, bestTxTypeMap, xd.Height * xd.Width);
                bestXskipTxfm = mbmi.SkipTxfm;
            }
        }
        mbmi.RefFrame1 = refFrame1;
        rateMv = bestRateMv;
        if (bestRd == long.MaxValue || !CheckNewmvJointNonzero(cm, x))
        {
            rdStats.Invalidate();
            AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
            return long.MaxValue;
        }
        mbmi.CopyFrom(bestMbmi);
        rdStats = bestRdStats;
        rdStatsY = bestRdStatsY;
        if (numPlanes > 1) rdStatsUv = bestRdStatsUv;
        CopyTxTypeMapFrom(xd, bestTxTypeMap, xd.Height * xd.Width);
        x.TxfmSkip = bestXskipTxfm;
        AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
        return 0;
    }

    /// <summary>handle_inter_mode.</summary>
    private static long HandleInterMode(AomComp cpi, AomMacroblock x, int bsize, ref AomRdStats rdStats, ref AomRdStats rdStatsY,
        ref AomRdStats rdStatsUv, AomHandleInterModeArgs args, long refBestRd, ref long bestEstRd, bool doTxSearch, AomInterModesInfo interModesInfo,
        AomMotionModeCandidate motionModeCand, long[] skipRd, out long yrd)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        bool isComp = mbmi.HasSecondRef;
        int thisMode = mbmi.Mode;
        bool pruneModesBasedOnTpl = cpi.Sf.inter_sf.prune_inter_modes_based_on_tpl != 0 && cpi.TplStatsReady;
        int r0 = mbmi.RefFrame0, r1 = mbmi.RefFrame1 < 0 ? 0 : mbmi.RefFrame1;
        int rateMv = 0;
        long rd = long.MaxValue;
        var origDst = AomBufferSet.FromDst(xd);
        var tmpDst = x.TmpDstSet();
        long retVal = long.MaxValue;
        int rft = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
        AomRdStats bestRdStats = default, bestRdStatsY = default, bestRdStatsUv = default;
        long bestRd = long.MaxValue;
        var bestTxTypeMap = new byte[32 * 32];
        long bestYrd = long.MaxValue;
        var bestMbmi = x.ScratchBestInterMbmi;
        bestMbmi.CopyFrom(mbmi);
        int bestXskipTxfm = 0;
        var modeInfo = new AomInterModeInfo[MAX_REF_MV_SEARCH];
        yrd = long.MaxValue;
        bool refMatchAbove = false, refMatchLeft = false;
        if (pruneModesBasedOnTpl)
        {
            refMatchAbove = FindRefMatchInAboveNbs(cm.MiCols, xd);
            refMatchLeft = FindRefMatchInLeftNbs(cm.MiRows, xd);
        }
        int refSet = GetDrlRefmvCount(x, mbmi.RefFrame0, mbmi.RefFrame1, thisMode);
        var saveMv = new AomMv[MAX_REF_MV_SEARCH - 1, 2];
        int bestRefMvIdx = -1;
        int idxMask = RefMvIdxToSearch(cpi, x, args, refBestRd, bsize, refSet);
        int modeCtx = AomInter.ModeContextAnalyzer(ext.ModeContext, mbmi.RefFrame0, mbmi.RefFrame1);
        var mc = x.ModeCosts;
        int refMvCost = CostMvRef(mc, thisMode, modeCtx);
        int baseRate = args.RefFrameCost + args.SingleCompCost + refMvCost;
        AomTrace.Out?.Write($"brt {xd.MiRow} {xd.MiCol} {thisMode} {mbmi.RefFrame0} {args.RefFrameCost} {args.SingleCompCost} {refMvCost}" + (char)10);
        for (int i = 0; i < MAX_REF_MV_SEARCH - 1; ++i) { saveMv[i, 0] = AomMv.Invalid; saveMv[i, 1] = AomMv.Invalid; }
        args.StartMvCnt = 0;
        var curMv = new AomMv[2];
        for (int refMvIdx = 0; refMvIdx < refSet; ++refMvIdx)
        {
            mbmi.RefMvIdx = (byte)refMvIdx;
            modeInfo[refMvIdx].FullSearchMv = AomMv.Invalid;
            modeInfo[refMvIdx].FullMvBestsme = int.MaxValue;
            int drlCost = GetDrlCost(mbmi, ext, mc.DrlModeCost0, rft);
            modeInfo[refMvIdx].DrlCost = drlCost;
            modeInfo[refMvIdx].Skip = 0;
            if (((idxMask >> refMvIdx) & 1) == 0) continue;
            if (pruneModesBasedOnTpl && !refMatchAbove && !refMatchLeft && refBestRd != long.MaxValue)
                if (PruneModesBasedOnTplStats(args, mbmi.RefFrame0, mbmi.RefFrame1, refMvIdx, thisMode, cpi.Sf.inter_sf.prune_inter_modes_based_on_tpl))
                    continue;
            rdStats.Init();
            mbmi.InterinterComp.Type = COMPOUND_AVERAGE;
            mbmi.CompGroupIdx = 0;
            mbmi.CompoundIdx = 1;
            if (mbmi.RefFrame1 == INTRA_FRAME) mbmi.RefFrame1 = NONE_FRAME;
            mbmi.NumProjRef = 0;
            mbmi.MotionMode = SIMPLE_TRANSLATION;
            rdStats.Rate = baseRate;
            rdStats.Rate += drlCost;
            int rs = 0;
            int compmodeInterinterCost = 0;
            bool skipRepeatedRefMv = !isComp && cpi.Sf.inter_sf.skip_repeated_ref_mv != 0;
            curMv[0] = AomMv.Invalid; curMv[1] = AomMv.Invalid;
            if (!BuildCurMv(curMv, thisMode, cm, x, skipRepeatedRefMv)) continue;
            if (AomInter.HaveNewmvInInterMode(thisMode))
            {
                long newmvRet = HandleNewmv(cpi, x, bsize, curMv, out rateMv, args, modeInfo);
                if (newmvRet != 0) continue;
                if (AomInter.IsInterSinglerefMode(thisMode) && curMv[0].AsInt != AomMv.Invalid.AsInt)
                {
                    uint thisSse = x.PredSse[r0];
                    if (thisSse < args.BestSingleSseInRefs[r0]) args.BestSingleSseInRefs[r0] = thisSse;
                    if (cpi.Sf.rt_sf.skip_newmv_mode_based_on_sse != 0) throw new NotImplementedException("skip_newmv_mode_based_on_sse");
                }
                rdStats.Rate += rateMv;
            }
            mbmi.Mv0 = curMv[0];
            if (isComp) mbmi.Mv1 = curMv[1];
            if (AomRd.RdCost(x.Rdmult, rdStats.Rate, 0) > refBestRd && mbmi.Mode != NEARESTMV && mbmi.Mode != NEAREST_NEARESTMV) continue;
            if (cpi.Sf.inter_sf.prune_ref_mv_idx_search != 0 && isComp &&
                PruneRefMvIdxSearch(refMvIdx, bestRefMvIdx, saveMv, mbmi, cpi.Sf.inter_sf.prune_ref_mv_idx_search))
                continue;
            if (cpi.Sf.gm_sf.prune_zero_mv_with_sse != 0 && (thisMode == GLOBALMV || thisMode == GLOBAL_GLOBALMV))
                if (PruneZeroMvWithSse(x, bsize, args, cpi.Sf.gm_sf.prune_zero_mv_with_sse)) continue;
            int skipBuildPred = 0;   // INTERP_EVAL_LUMA_EVAL_CHROMA
            int miRow = xd.MiRow, miCol = xd.MiCol;
            if (isComp)
            {
                bool notBest = ProcessCompoundInterMode(cpi, x, args, refBestRd, curMv, bsize, out compmodeInterinterCost, origDst, tmpDst, ref rateMv,
                    ref rdStats, skipRd, ref skipBuildPred);
                if (notBest) continue;
            }
            if (!args.SkipIfs)
            {
                retVal = AomInterpSearch.InterpolationFilterSearch(x, cpi, bsize, tmpDst, origDst, ref rd, ref rs, ref skipBuildPred, args, refBestRd);
                if (!isComp) args.State.ModelledRd[thisMode, refMvIdx, r0] = rd;
                if (retVal != 0)
                {
                    AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
                    continue;
                }
                if (cpi.Sf.inter_sf.model_based_post_interp_filter_breakout != 0 && refBestRd != long.MaxValue && (rd >> 3) * 3 > refBestRd)
                {
                    AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
                    continue;
                }
                if (isComp)
                {
                    int mode0 = AomInter.CompoundRef0Mode(thisMode), mode1 = AomInter.CompoundRef1Mode(thisMode);
                    long mrd = Math.Min(args.State.ModelledRd[mode0, refMvIdx, r0], args.State.ModelledRd[mode1, refMvIdx, r1]);
                    if ((rd >> 3) * 6 > mrd && refBestRd < long.MaxValue)
                    {
                        AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
                        continue;
                    }
                }
            }
            rdStats.Rate += compmodeInterinterCost;
            if (skipBuildPred != INTERP_SKIP_LUMA_SKIP_CHROMA)
            {
                bool skipLumaPlane = skipBuildPred == 1 /* INTERP_SKIP_LUMA_EVAL_CHROMA */ && mbmi.InterinterComp.Type != COMPOUND_DIFFWTD;
                AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, origDst, bsize, skipLumaPlane ? 1 : 0, numPlanes - 1, cpi.EnableIntraEdgeFilter);
            }
            int rate2Nocoeff = rdStats.Rate;
            retVal = MotionModeRd(cpi, x, bsize, ref rdStats, ref rdStatsY, ref rdStatsUv, args, refBestRd, skipRd, ref rateMv, origDst, ref bestEstRd,
                doTxSearch, interModesInfo, false, out long thisYrd);
            if (retVal != long.MaxValue)
            {
                long tmpRd = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
                int modeEnum = GetPredictionModeIdx(mbmi.Mode, mbmi.RefFrame0, mbmi.RefFrame1);
                AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, rdStats, rdStatsY, rdStatsUv, modeEnum, null, bsize, tmpRd,
                    cpi.Sf.winner_mode_sf.multi_winner_mode_type, doTxSearch);
                if (tmpRd < bestRd)
                {
                    bestYrd = thisYrd;
                    bestRdStats = rdStats;
                    bestRdStatsY = rdStatsY;
                    bestRdStatsUv = rdStatsUv;
                    bestRd = tmpRd;
                    bestMbmi.CopyFrom(mbmi);
                    bestXskipTxfm = x.TxfmSkip;
                    CopyTxTypeMapTo(xd, bestTxTypeMap, xd.Height * xd.Width);
                    motionModeCand.RateMv = rateMv;
                    motionModeCand.Rate2Nocoeff = rate2Nocoeff;
                }
                if (tmpRd < refBestRd)
                {
                    refBestRd = tmpRd;
                    bestRefMvIdx = refMvIdx;
                }
            }
            AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
        }
        if (bestRd == long.MaxValue) return long.MaxValue;
        rdStats = bestRdStats;
        rdStatsY = bestRdStatsY;
        rdStatsUv = bestRdStatsUv;
        yrd = bestYrd;
        mbmi.CopyFrom(bestMbmi);
        x.TxfmSkip = bestXskipTxfm;
        CopyTxTypeMapFrom(xd, bestTxTypeMap, xd.Height * xd.Width);
        rdStats.Rdcost = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
        return rdStats.Rdcost;
    }
}
