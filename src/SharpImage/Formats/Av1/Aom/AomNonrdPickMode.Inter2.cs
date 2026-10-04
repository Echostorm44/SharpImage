using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// av1_nonrd_pick_inter_mode_sb and its mode loop (nonrd_pickmode.c), the intra check of inter frames
// (av1_estimate_intra_mode, nonrd_opt.c).
internal static partial class AomNonrdPickMode
{
    /// <summary>av1_model_rd_for_sb_uv.</summary>
    private static long ModelRdForSbUv(AomComp cpi, int planeBsize, AomMacroblock x, AomMacroblockD xd, ref AomRdStats thisRdc, int startPlane,
        int stopPlane)
    {
        long totSse = 0;
        thisRdc.Rate = 0;
        thisRdc.Dist = 0;
        thisRdc.SkipTxfm = 0;
        int bw = BlockSizeWide[planeBsize], bh = BlockSizeHigh[planeBsize];
        for (int plane = startPlane; plane <= stopPlane; ++plane)
        {
            var p = x.Plane[plane];
            var pd = xd.Plane[plane];
            uint dcQuant = (uint)p.Dequant0, acQuant = (uint)p.Dequant1;
            if (x.ColorSensitivity[plane - 1] == 0) continue;
            uint var = Vf(p.Src, pd.Dst, bw, bh, xd.Bd, out uint sse);
            totSse += sse;
            AomModelRd.FromVarLapndz(sse - var, NumPelsLog2Lookup[planeBsize], dcQuant >> 3, out int rate, out long dist);
            thisRdc.Rate += rate >> 1;
            thisRdc.Dist += dist << 3;
            AomModelRd.FromVarLapndz(var, NumPelsLog2Lookup[planeBsize], acQuant >> 3, out rate, out dist);
            thisRdc.Rate += rate;
            thisRdc.Dist += dist << 4;
        }
        if (thisRdc.Rate == 0) thisRdc.SkipTxfm = 1;
        if (AomRd.RdCost(x.Rdmult, thisRdc.Rate, thisRdc.Dist) >= AomRd.RdCost(x.Rdmult, 0, totSse << 4))
        {
            thisRdc.Rate = 0;
            thisRdc.Dist = totSse << 4;
            thisRdc.SkipTxfm = 1;
        }
        return totSse;
    }

    /// <summary>set_params_nonrd_pick_inter_mode.</summary>
    private static void SetParamsNonrdPickInterMode(AomComp cpi, AomMacroblock x, SearchState st, ref AomRdStats rdCost, ref int forceSkipLowTempVar,
        int miRow, int miCol, bool gfTemporalRef, int bsize)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        for (int i = 0; i < RTC_INTER_MODES; i++)
            for (int r = 0; r < REF_FRAMES; r++)
            {
                st.Vars[i, r] = uint.MaxValue;
                st.UvDist[i, r] = long.MaxValue;
            }
        x.ColorSensitivity[0] = x.ColorSensitivitySb[0];
        x.ColorSensitivity[1] = x.ColorSensitivitySb[1];
        InitBestPickmode(st.BestPickmode);
        EstimateSingleRefFrameCosts(cm, xd, x.ModeCosts, bsize, st.RefCostsSingle);
        Array.Clear(st.ModeChecked);
        x.TxfmSkip = 0;
        st.BestRdc.Invalidate();
        st.ThisRdc.Invalidate();
        rdCost.Invalidate();
        for (int r = 0; r < REF_FRAMES; ++r) x.WarpSampleInfo[r].Num = -1;
        mi.Bsize = bsize;
        mi.RefFrame0 = NONE_FRAME;
        mi.RefFrame1 = NONE_FRAME;
        if ((cpi.RefFrameFlags & AOM_LAST_FLAG) != 0)
            FindPredictors(cpi, x, LAST_FRAME, st, bsize, forceSkipLowTempVar, x.ForceZeromvSkipForBlk != 0);
        GetRefFrameUseMask(cpi, x, mi, miRow, miCol, bsize, gfTemporalRef, st.UseRefFrameMask, ref forceSkipLowTempVar);
        bool skipPredMv = x.ForceZeromvSkipForBlk != 0 ||
                          (x.NonrdPruneRefFrameSearch > 2 && x.ColorSensitivity[0] != 2 && x.ColorSensitivity[1] != 2);
        for (int rf = LAST_FRAME + 1; rf <= ALTREF_FRAME; ++rf)
            if (st.UseRefFrameMask[rf] != 0) FindPredictors(cpi, x, rf, st, bsize, forceSkipLowTempVar, skipPredMv);
    }

    /// <summary>skip_inter_mode_nonrd (one layer, content default, no segmentation).</summary>
    private static bool SkipInterModeNonrd(AomComp cpi, AomMacroblock x, SearchState st, ref long threshSadPred, ref bool isSinglePred, out int thisMode,
        ref int lastCompRefFrame, out int refFrame, out int refFrame2, int idx, int forceSkipLowTempVar, uint sseZeromvNorm, int numInterModes,
        int bsize, bool compUseZeroZeromvOnly, bool checkGlobalmv)
    {
        var xd = x.E;
        var mi = xd.Mi0;
        var rtSf = cpi.Sf.rt_sf;
        if (idx >= numInterModes)
        {
            int compIndex = idx - numInterModes;
            var cm0 = CompRefModeSet[compIndex];
            thisMode = cm0.Mode;
            refFrame = cm0.Ref0;
            refFrame2 = cm0.Ref1;
            if (!SetupCompoundParamsFromCompIdx(cpi, x, st, ref thisMode, refFrame, refFrame2, compIndex, compUseZeroZeromvOnly, ref lastCompRefFrame,
                    bsize))
                return true;
            isSinglePred = false;
        }
        else
        {
            refFrame2 = NONE_FRAME;
            thisMode = RefModeSet[idx].Mode;
            refFrame = RefModeSet[idx].Ref;
        }
        // (skip_newmv_mode_sad_screen: screen content)
        if (st.UseRefFrameMask[refFrame] == 0) return true;
        if ((cpi.RefFrameFlags & AOM_LAST_FLAG) == 0 && (refFrame == GOLDEN_FRAME || refFrame == ALTREF_FRAME)) return false;
        if (x.ForceZeromvSkipForBlk != 0 &&
            ((!(thisMode == NEARESTMV && st.FrameMv[thisMode, refFrame].AsInt == 0) && thisMode != GLOBALMV) || refFrame != LAST_FRAME))
            return true;
        if (x.SbMeBlock && refFrame == LAST_FRAME)
        {
            if (thisMode == NEARESTMV && st.FrameMv[NEARESTMV, LAST_FRAME].AsInt == x.SbMeMv.AsInt) return false;
            if (thisMode == NEARMV && st.FrameMv[NEARMV, LAST_FRAME].AsInt == x.SbMeMv.AsInt) return false;
            if (thisMode == NEWMV) return false;
        }
        if (isSinglePred && st.ModeChecked[thisMode, refFrame] != 0) return true;
        if (!checkGlobalmv && thisMode == GLOBALMV) return true;
        mi.Mode = thisMode;
        mi.RefFrame0 = refFrame;
        mi.RefFrame1 = refFrame2;
        if (rtSf.prune_compoundmode_with_singlemode_var && !isSinglePred &&
            PruneCompoundmodeWithSinglemodeVar(thisMode, refFrame, refFrame2, st))
            return true;
        if (SkipModeByBsizeAndRefFrame(thisMode, refFrame, bsize, x.NonrdPruneRefFrameSearch, sseZeromvNorm, rtSf.nonrd_aggressive_skip,
                rtSf.increase_source_sad_thresh))
            return true;
        if (SkipModeByLowTemp(thisMode, refFrame, bsize, x.SourceSadNonrd, st.FrameMv[thisMode, refFrame], forceSkipLowTempVar)) return true;
        if (rtSf.nonrd_prune_ref_frame_search > 0 && x.PredMvSad[refFrame] != int.MaxValue && refFrame != LAST_FRAME)
            if (x.PredMvSad[refFrame] > threshSadPred) return true;
        if (thisMode == NEARMV && x.PredMv1Sad[refFrame] != int.MaxValue && x.PredMv1Sad[refFrame] > (x.PredMv0Sad[refFrame] << 1)) return true;
        if (isSinglePred)
            if (SkipModeByThreshold(thisMode, refFrame, st.FrameMv[thisMode, refFrame], cpi.Rt!.FramesSinceGolden, cpi, bsize, x, st.BestRdc.Rdcost,
                    st.BestPickmode.BestModeSkipTxfm, rtSf.nonrd_aggressive_skip != 0 ? 1 : 0))
                return true;
        return false;
    }

    /// <summary>handle_inter_mode_nonrd: false stops the mode loop.</summary>
    private static bool HandleInterModeNonrd(AomComp cpi, AomMacroblock x, SearchState st, AomPickModeContext ctx, ref PredBuffer? thisModePred,
        PredBuffer[] tmpBuffer, ref int bestEarlyTerm, ref uint sseZeromvNorm, ref bool checkGlobalmv, int idx, bool isSinglePred,
        bool gfTemporalRef, bool useModelYrdLarge, int filterSearchEnabledBlk, int bsize, int thisMode, int filtSelect, int cbPredFilterSearch,
        bool reuseInterPred, ref bool sbMeHasBeenTested)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        var ext = x.MbmiExtInter;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        var pd = xd.Plane[0];
        int bw = BlockSizeWide[bsize];
        int filterRef = cm.InterpFilter;
        var mc = x.ModeCosts;
        var rtSf = cpi.Sf.rt_sf;
        var bp = st.BestPickmode;
        int refFrame = mi.RefFrame0, refFrame2 = mi.RefFrame1;
        uint var = uint.MaxValue;
        int thisEarlyTerm = 0;
        int rateMv = 0;
        uint varThreshold = uint.MaxValue;
        AomRdStats nonskipRdc = default;
        nonskipRdc.Invalidate();

        if (x.SbMeBlock && thisMode == NEWMV && refFrame == LAST_FRAME) st.FrameMv[NEWMV, LAST_FRAME] = x.SbMeMv;
        else if (thisMode == NEWMV)
        {
            if (SearchNewMv(cpi, x, st, refFrame, gfTemporalRef, bsize, miRow, miCol, out rateMv, st.BestRdc) != 0) return true;
        }
        var thisMv = st.FrameMv[thisMode, refFrame];
        for (int m = NEARESTMV; m <= NEWMV; m++)
        {
            if (m == thisMode) continue;
            if (isSinglePred && st.ModeChecked[m, refFrame] != 0 && thisMv.AsInt == st.FrameMv[m, refFrame].AsInt) return true;
        }
        mi.Mode = thisMode;
        mi.Mv0 = thisMv;
        mi.Mv1 = default;
        if (!isSinglePred) mi.Mv1 = st.FrameMv[thisMode, refFrame2];
        if (reuseInterPred)
        {
            if (thisModePred == null) thisModePred = tmpBuffer[3];
            else
            {
                thisModePred = tmpBuffer[GetPredBuffer(tmpBuffer, 3)];
                pd.Dst.Buf = thisModePred.Data;
                pd.Dst.Offset = thisModePred.Offset;
                pd.Dst.Stride = bw;
            }
        }
        mi.MotionMode = SIMPLE_TRANSLATION;
        if (cpi.AllowWarpedMotionCfg) CalcNumProjRef(cpi, x, mi);
        if (rtSf.prune_compoundmode_with_singlecompound_var && !isSinglePred && useModelYrdLarge)
        {
            int m0 = AomInter.CompoundRef0Mode(thisMode), m1 = AomInter.CompoundRef1Mode(thisMode);
            varThreshold = Math.Min(varThreshold, st.Vars[InterOffset(m0), refFrame]);
            varThreshold = Math.Min(varThreshold, st.Vars[InterOffset(m1), refFrame2]);
        }
        bool isMvSubpel = (mi.Mv0.Row & 0x07) != 0 || (mi.Mv0.Col & 0x07) != 0;
        bool enableFiltSearchThisMode = filterSearchEnabledBlk == 2 ||
                                        (filterSearchEnabledBlk != 0 && isSinglePred && (refFrame == LAST_FRAME || x.NonrdPruneRefFrameSearch == 0));
        if (isMvSubpel && enableFiltSearchThisMode)
            SearchFilterRef(cpi, x, ref st.ThisRdc, miRow, miCol, tmpBuffer, bsize, reuseInterPred, ref thisModePred, ref thisEarlyTerm, ref var,
                useModelYrdLarge, bp.BestSse, isSinglePred);
        else if (cpi.AllowWarpedMotionCfg && thisMode == NEWMV)
        {
            SearchMotionMode(cpi, x, ref st.ThisRdc, miRow, miCol, bsize, ref thisEarlyTerm, useModelYrdLarge, ref rateMv, bp.BestSse);
            st.FrameMv[thisMode, refFrame] = mi.Mv0;
            thisMv = mi.Mv0;
        }
        else
        {
            mi.InterpFilters = filterRef == SWITCHABLE ? AomInterpSearch.Broadcast(EIGHTTAP_REGULAR) : AomInterpSearch.Broadcast(filterRef);
            if (isMvSubpel && cbPredFilterSearch != 0) mi.InterpFilters = AomInterpSearch.Broadcast(filtSelect);
            BuildLumaPrediction(cpi, xd, miRow, miCol, bsize, isSinglePred);
            if (useModelYrdLarge)
                ModelSkipForSbYLarge(cpi, bsize, miRow, miCol, x, xd, ref st.ThisRdc, ref thisEarlyTerm, false, bp.BestSse, ref var, true, varThreshold);
            else ModelRdForSbY(cpi, bsize, x, xd, ref st.ThisRdc, ref var, false, ref thisEarlyTerm, true);
        }
        if (isSinglePred)
        {
            st.Vars[InterOffset(thisMode), refFrame] = var;
            if (thisMv.AsInt == 0) st.Vars[InterOffset(GLOBALMV), refFrame] = var;
        }
        if (!isSinglePred && var > varThreshold)
        {
            if (reuseInterPred) FreePredBuffer(thisModePred);
            return true;
        }
        if (refFrame == LAST_FRAME && thisMv.AsInt == 0)
            sseZeromvNorm = (uint)(st.ThisRdc.Sse >> (BWidthLog2Lookup[bsize] + BHeightLog2Lookup[bsize]));
        if (rtSf.sse_early_term_inter_search != 0 &&
            EarlyTermInterSearchWithSse(rtSf.sse_early_term_inter_search, bsize, st.ThisRdc.Sse, bp.BestSse, thisMode))
        {
            if (reuseInterPred) FreePredBuffer(thisModePred);
            return true;
        }
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        int skipTxfmCost = mc.SkipTxfmCost[skipCtx * 2 + 1], noSkipTxfmCost = mc.SkipTxfmCost[skipCtx * 2 + 0];
        long sseY = st.ThisRdc.Sse;
        if (thisEarlyTerm != 0)
        {
            st.ThisRdc.SkipTxfm = 1;
            st.ThisRdc.Rate = skipTxfmCost;
            st.ThisRdc.Dist = st.ThisRdc.Sse << 4;
        }
        else
        {
            int isSkippable = 0;
            BlockYrd(x, ref st.ThisRdc, ref isSkippable, bsize, mi.TxSize);
            if (st.ThisRdc.SkipTxfm != 0 ||
                AomRd.RdCost(x.Rdmult, st.ThisRdc.Rate, st.ThisRdc.Dist) >= AomRd.RdCost(x.Rdmult, 0, st.ThisRdc.Sse))
            {
                if (st.ThisRdc.SkipTxfm == 0)
                {
                    nonskipRdc = st.ThisRdc;
                    nonskipRdc.Rate += noSkipTxfmCost;
                }
                st.ThisRdc.Rate = skipTxfmCost;
                st.ThisRdc.SkipTxfm = 1;
                st.ThisRdc.Dist = st.ThisRdc.Sse;
            }
            else st.ThisRdc.Rate += noSkipTxfmCost;
            if (x.ColorSensitivity[0] != 0 || x.ColorSensitivity[1] != 0)
            {
                AomRdStats rdcUv = default;
                int uvBsize = AomEncodeMb.PlaneBlockSize(bsize, xd.Plane[1].SubsamplingX, xd.Plane[1].SubsamplingY);
                if (x.ColorSensitivity[0] != 0) AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 1, 1, cpi.EnableIntraEdgeFilter);
                if (x.ColorSensitivity[1] != 0) AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 2, 2, cpi.EnableIntraEdgeFilter);
                long sseUv = ModelRdForSbUv(cpi, uvBsize, x, xd, ref rdcUv, 1, 2);
                if (rdcUv.Dist < x.MinDistInterUv) x.MinDistInterUv = rdcUv.Dist;
                st.ThisRdc.Sse += sseUv;
                if (st.ThisRdc.SkipTxfm != 0 && rdcUv.SkipTxfm == 0 && nonskipRdc.Rate != int.MaxValue) st.ThisRdc = nonskipRdc;
                if (isSinglePred) st.UvDist[InterOffset(thisMode), refFrame] = rdcUv.Dist;
                st.ThisRdc.Rate += rdcUv.Rate;
                st.ThisRdc.Dist += rdcUv.Dist;
                st.ThisRdc.SkipTxfm = (byte)(st.ThisRdc.SkipTxfm != 0 && rdcUv.SkipTxfm != 0 ? 1 : 0);
            }
        }
        int thisBestMode = thisMode;
        st.ThisRdc.Rate += rateMv;
        if (!isSinglePred)
        {
            int modeCtx = AomInter.ModeContextAnalyzer(ext.ModeContext, mi.RefFrame0, mi.RefFrame1);
            st.ThisRdc.Rate += CostMvRefNonrd(mc, thisMode, modeCtx);
        }
        else
        {
            if (thisMode != GLOBALMV && thisMv.AsInt == st.FrameMv[GLOBALMV, refFrame].AsInt)
                if (IsGlobalmvBetter(thisMode, refFrame, rateMv, mc, st.SingleInterModeCosts, ext)) thisBestMode = GLOBALMV;
            st.ThisRdc.Rate += st.SingleInterModeCosts[InterOffset(thisBestMode), refFrame];
        }
        if (isSinglePred && thisMv.AsInt == 0 && var < uint.MaxValue) st.Vars[InterOffset(GLOBALMV), refFrame] = var;
        st.ThisRdc.Rate += (int)st.RefCostsSingle[refFrame];
        st.ThisRdc.Rdcost = AomRd.RdCost(x.Rdmult, st.ThisRdc.Rate, st.ThisRdc.Dist);
        if (isSinglePred)
            NewmvDiffBias(xd, thisBestMode, ref st.ThisRdc, bsize, st.FrameMv[thisBestMode, refFrame].Row, st.FrameMv[thisBestMode, refFrame].Col,
                cpi.Speed, x.SourceVariance, x.SourceSadNonrd);
        st.ModeChecked[thisMode, refFrame] = 1;
        st.ModeChecked[thisBestMode, refFrame] = 1;
        if (checkGlobalmv)
        {
            int absMv = Math.Abs((int)st.FrameMv[thisBestMode, refFrame].Row) + Math.Abs((int)st.FrameMv[thisBestMode, refFrame].Col);
            if (absMv < 2) checkGlobalmv = false;
        }
        if (x.SbMeBlock && refFrame == LAST_FRAME && st.FrameMv[thisBestMode, refFrame].AsInt == x.SbMeMv.AsInt) sbMeHasBeenTested = true;
        if (st.ThisRdc.Rdcost < st.BestRdc.Rdcost)
        {
            st.BestRdc = st.ThisRdc;
            bestEarlyTerm = thisEarlyTerm;
            UpdateSearchStateNonrd(st, mi, nonskipRdc, thisBestMode, sseY);
            st.FrameMvBest[thisBestMode, refFrame] = st.FrameMv[thisBestMode, refFrame];
            if (refFrame2 > NONE_FRAME) st.FrameMvBest[thisBestMode, refFrame2] = st.FrameMv[thisBestMode, refFrame2];
            if (reuseInterPred)
            {
                FreePredBuffer(bp.BestPred);
                bp.BestPred = thisModePred;
            }
        }
        else if (reuseInterPred) FreePredBuffer(thisModePred);
        if (bestEarlyTerm != 0 && (idx > 0 || rtSf.nonrd_aggressive_skip != 0))
        {
            x.TxfmSkip = 1;
            if (!x.SbMeBlock || sbMeHasBeenTested) return false;
        }
        return true;
    }

    /// <summary>is_prune_intra_mode.</summary>
    private static bool IsPruneIntraMode(AomComp cpi, int modeIndex, bool forceIntraCheck, int bsize, int sourceSadNonrd, byte[] colorSensitivity)
    {
        int thisMode = IntraModeList[modeIndex];
        if (modeIndex > 2 || !forceIntraCheck)
        {
            if (((1 << thisMode) & cpi.Sf.rt_sf.intra_y_mode_bsize_mask_nrd[bsize]) == 0) return true;
            if (thisMode == DC_PRED) return false;
            if (!cpi.Sf.rt_sf.prune_hv_pred_modes_using_src_sad) return false;
            var rt = cpi.Rt!;
            bool hasColorSensitivity = colorSensitivity[0] != 0 && colorSensitivity[1] != 0;
            if (hasColorSensitivity && (rt.FrameSourceSad > 1.1 * rt.AvgSourceSad || sourceSadNonrd > AomRtSb.kMedSad)) return false;
            return true;
        }
        return false;
    }

    /// <summary>compute_intra_yprediction (each max-size transform block predicted with the block's (0, 0) offsets).</summary>
    private static void ComputeIntraYprediction(AomComp cpi, int mode, int bsize, AomMacroblock x, AomMacroblockD xd)
    {
        var pd = xd.Plane[0];
        var dstBase = pd.Dst;
        int txSize = MaxTxsizeLookup[bsize];
        int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, 0), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, 0);
        for (int row = 0; row < maxBlocksHigh; row += 1 << txSize)
            for (int col = 0; col < maxBlocksWide; col += 1 << txSize)
            {
                int off = dstBase.Offset + 4 * (row * dstBase.Stride + col);
                if (dstBase.Buf16 != null)
                    AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, BlockSizeWide[bsize], BlockSizeHigh[bsize], txSize, mode, 0,
                        false, FILTER_INTRA_MODES, dstBase.Buf16, off, dstBase.Stride, dstBase.Buf16, off, dstBase.Stride, 0, 0, 0);
                else
                    AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, BlockSizeWide[bsize], BlockSizeHigh[bsize], txSize, mode, 0,
                        false, FILTER_INTRA_MODES, dstBase.Buf, off, dstBase.Stride, dstBase.Buf, off, dstBase.Stride, 0, 0, 0);
            }
        pd.Dst = dstBase;
    }

    /// <summary>av1_foreach_transformed_block_in_plane(xd, bsize, plane, av1_estimate_block_intra) for a chroma plane.</summary>
    private static void ForeachTxBlockUv(AomComp cpi, AomMacroblock x, EstimateBlockIntraArgs args, ref AomRdStats rdc, int plane, int planeBsize)
    {
        var xd = x.E;
        int txSize = AomEncodeMb.GetTxSize(plane, xd);
        int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize];
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane);
        var pd = xd.Plane[plane];
        int muBlocksWide = Math.Min(MiSizeWide[BLOCK_64X64] >> pd.SubsamplingX, maxBlocksWide);
        int muBlocksHigh = Math.Min(MiSizeHigh[BLOCK_64X64] >> pd.SubsamplingY, maxBlocksHigh);
        for (int r = 0; r < maxBlocksHigh; r += muBlocksHigh)
        {
            int unitHeight = Math.Min(muBlocksHigh + r, maxBlocksHigh);
            for (int c = 0; c < maxBlocksWide; c += muBlocksWide)
            {
                int unitWidth = Math.Min(muBlocksWide + c, maxBlocksWide);
                for (int blkRow = r; blkRow < unitHeight; blkRow += txhUnit)
                    for (int blkCol = c; blkCol < unitWidth; blkCol += txwUnit)
                        EstimateBlockIntra(cpi, x, args, ref rdc, plane, blkRow, blkCol, planeBsize, txSize);
            }
        }
    }

    /// <summary>av1_estimate_intra_mode (content default).</summary>
    private static void EstimateIntraMode(AomComp cpi, AomMacroblock x, int bsize, int bestEarlyTerm, uint refCostIntra, bool reusePrediction,
        in AomBuf2d origDst, PredBuffer[] tmpBuffers, ref PredBuffer? thisModePred, ref AomRdStats bestRdc, BestPickmode bp, out uint bestSadNorm)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        var tp = x.TxfmSearchParams;
        var pd = xd.Plane[0];
        var rtSf = cpi.Sf.rt_sf;
        AomRdStats thisRdc = default;
        int intraCostPenalty = GetIntraCostPenalty(cm.BaseQindex, cm.YDcDeltaQ, cm.BitDepth);
        long interModeThresh = AomRd.RdCost(x.Rdmult, (int)refCostIntra + intraCostPenalty, 0);
        bool performIntraPred = rtSf.check_intra_pred_nonrd != 0;
        bool forceIntraCheck = false;
        bool doEarlyExitRdthresh = true;
        uint spatialVarThresh = 50;
        int motionThresh = 32;
        bestSadNorm = uint.MaxValue;
        if (rtSf.use_nonrd_altref_frame == 0 && rtSf.nonrd_prune_ref_frame_search > 0)
        {
            spatialVarThresh = 150;
            motionThresh = 0;
        }
        if (x.SourceVariance < spatialVarThresh)
        {
            if (bestRdc.Rdcost != long.MaxValue &&
                (bp.BestRefFrame != LAST_FRAME || Math.Abs((int)mi.Mv0.Row) >= motionThresh || Math.Abs((int)mi.Mv0.Col) >= motionThresh))
            {
                intraCostPenalty >>= 2;
                interModeThresh = AomRd.RdCost(x.Rdmult, (int)refCostIntra + intraCostPenalty, 0);
                doEarlyExitRdthresh = false;
            }
            if (x.SourceVariance < Math.Max(50u, spatialVarThresh >> 1) && x.SourceSadNonrd >= AomRtSb.kHighSad) forceIntraCheck = true;
            if (bsize >= BLOCK_32X32) bestEarlyTerm = 0;
        }
        else if (rtSf.source_metrics_sb_nonrd != 0 && x.SourceSadNonrd <= AomRtSb.kLowSad) performIntraPred = false;
        if (bestRdc.SkipTxfm != 0 && bp.BestModeInitialSkipFlag != 0)
        {
            if (rtSf.skip_intra_pred == 1 && bp.BestMode != NEWMV) performIntraPred = false;
            else if (rtSf.skip_intra_pred == 2) performIntraPred = false;
        }
        if (!(bestRdc.Rdcost == long.MaxValue || forceIntraCheck || (performIntraPred && bestEarlyTerm == 0 && bsize <= cpi.Sf.part_sf.max_intra_bsize)))
            return;
        long knownRd = interModeThresh;
        if (knownRd > bestRdc.Rdcost) return;
        var args = new EstimateBlockIntraArgs { Mode = DC_PRED, Skippable = 1, BestSad = uint.MaxValue };
        int intraTxSize = Math.Min(Math.Min((int)MaxTxsizeLookup[bsize], (int)TxModeToBiggestTxSize[tp.TxModeSearchType]), TX_16X16);
        var bestPred = bp.BestPred;
        if (reusePrediction && bestPred != null)
        {
            int bh = BlockSizeHigh[bsize], bw = BlockSizeWide[bsize];
            if (bestPred.Data == origDst.Buf && bestPred.Offset == origDst.Offset)
            {
                thisModePred = tmpBuffers[GetPredBuffer(tmpBuffers, 3)];
                ConvolveCopy(bestPred.Data, bestPred.Offset, bestPred.Stride, thisModePred.Data, thisModePred.Offset, thisModePred.Stride, bw, bh);
                bp.BestPred = thisModePred;
            }
        }
        pd.Dst = origDst;
        for (int midx = 0; midx < RTC_INTRA_MODES; ++midx)
        {
            int thisMode = IntraModeList[midx];
            int modeIndex = ModeIdx[INTRA_FRAME, ModeOffset(thisMode)];
            long modeRdThresh = cpi.Rd.Thresh(0, bsize, modeIndex);
            if (IsPruneIntraMode(cpi, midx, forceIntraCheck, bsize, x.SourceSadNonrd, x.ColorSensitivity)) continue;
            if (RdLessThanThresh(bestRdc.Rdcost, modeRdThresh, x.ThreshFreqFact[bsize, modeIndex]) && (doEarlyExitRdthresh || thisMode == SMOOTH_PRED))
                continue;
            int uvBsize = AomEncodeMb.PlaneBlockSize(bsize, xd.Plane[1].SubsamplingX, xd.Plane[1].SubsamplingY);
            mi.Mode = thisMode;
            mi.RefFrame0 = INTRA_FRAME;
            mi.RefFrame1 = NONE_FRAME;
            thisRdc.Invalidate();
            args.Mode = thisMode;
            args.Skippable = 1;
            mi.TxSize = intraTxSize;
            ComputeIntraYprediction(cpi, thisMode, bsize, x, xd);
            BlockYrd(x, ref thisRdc, ref args.Skippable, bsize, mi.TxSize);
            if (x.ColorSensitivity[0] != 0) ForeachTxBlockUv(cpi, x, args, ref thisRdc, 1, uvBsize);
            if (x.ColorSensitivity[1] != 0) ForeachTxBlockUv(cpi, x, args, ref thisRdc, 2, uvBsize);
            int modeCost = 0;
            if (AomReconIntra.IsDirectionalMode(thisMode) && AomReconIntra.UseAngleDelta(bsize))
                modeCost += x.ModeCosts.AngleDeltaCost[(thisMode - V_PRED) * 7 + 3 + mi.AngleDelta[0]];
            if (thisMode == DC_PRED && AomIntraModeSearch.FilterIntraAllowedBsize(cpi, bsize)) modeCost += x.ModeCosts.FilterIntraCost[bsize * 2 + 0];
            thisRdc.Rate += (int)refCostIntra;
            thisRdc.Rate += intraCostPenalty;
            thisRdc.Rate += modeCost;
            thisRdc.Rdcost = AomRd.RdCost(x.Rdmult, thisRdc.Rate, thisRdc.Dist);
            if (thisRdc.Rdcost < bestRdc.Rdcost)
            {
                bestRdc = thisRdc;
                bp.BestMode = thisMode;
                bp.BestTxSize = mi.TxSize;
                bp.BestRefFrame = INTRA_FRAME;
                bp.BestSecondRefFrame = NONE_FRAME;
                bp.BestModeSkipTxfm = thisRdc.SkipTxfm;
                mi.UvMode = thisMode;
                mi.Mv0 = AomMv.Invalid;
                mi.Mv1 = AomMv.Invalid;
            }
        }
        mi.TxSize = bp.BestTxSize;
        bestSadNorm = args.BestSad >> (BWidthLog2Lookup[bsize] + BHeightLog2Lookup[bsize]);
    }

    /// <summary>enable_palette (content default: prune_palette_testing_inter never applies).</summary>
    private static bool EnablePalette(AomComp cpi, bool isModeIntra, int bsize, uint sourceVariance, int forceZeromvSkip, bool skipIdtxPalette,
        bool forcePaletteTest)
    {
        if (!cpi.EnablePalette) return false;
        if (!AomIntraModeSearch.AllowPalette(cpi.AllowScreenContentTools, bsize)) return false;
        if (skipIdtxPalette) return false;
        if (cpi.Sf.rt_sf.prune_palette_search_nonrd > 1 && bsize > BLOCK_16X16) return false;
        return (isModeIntra || forcePaletteTest) && sourceVariance > 0 && forceZeromvSkip == 0 &&
               (cpi.Rt!.HighSourceSad || sourceVariance > 300);
    }

    /// <summary>av1_nonrd_pick_inter_mode_sb.</summary>
    internal static void NonrdPickInterModeSb(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize, AomPickModeContext ctx)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mi = xd.Mi0;
        var pd = xd.Plane[0];
        var ext = x.MbmiExtInter;
        int bestEarlyTerm = 0;
        int forceSkipLowTempVar = 0;
        uint sseZeromvNorm = uint.MaxValue;
        const int numInterModes = NUM_INTER_MODES;
        var rtSf = cpi.Sf.rt_sf;
        bool checkGlobalmv = rtSf.check_globalmv_on_single_ref;
        bool reuseInterPred = rtSf.reuse_inter_pred_nonrd != 0 && cm.BitDepth == 8;
        var st = new SearchState();
        var bp = st.BestPickmode;
        int bh = BlockSizeHigh[bsize], bw = BlockSizeWide[bsize];
        int pixelsInBlock = bh * bw;
        var origDst = pd.Dst;
        var tp = x.TxfmSearchParams;
        long threshSadPred = long.MaxValue;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        bool compUseZeroZeromvOnly = false;
        int totNumCompModes = NUM_COMP_INTER_MODES_RT;
        var mc = x.ModeCosts;
        var sfNoScale = AomScaleFactors.ForFrame(cm.Width, cm.Height, cm.Width, cm.Height);
        var tmpBuffer = new PredBuffer[4];
        for (int i = 0; i < 4; i++) tmpBuffer[i] = new PredBuffer();
        if (reuseInterPred)
        {
            var predBuf = x.NonrdPredBuf ??= new byte[3 * 128 * 128];
            for (int i = 0; i < 3; i++)
            {
                tmpBuffer[i].Data = predBuf;
                tmpBuffer[i].Offset = pixelsInBlock * i;
                tmpBuffer[i].Stride = bw;
                tmpBuffer[i].InUse = false;
            }
            tmpBuffer[3].Data = pd.Dst.Buf;
            tmpBuffer[3].Offset = pd.Dst.Offset;
            tmpBuffer[3].Stride = pd.Dst.Stride;
            tmpBuffer[3].InUse = false;
        }
        bool gfTemporalRef = IsSameGfAndLastScale(cm);
        SetParamsNonrdPickInterMode(cpi, x, st, ref rdCost, ref forceSkipLowTempVar, miRow, miCol, gfTemporalRef, bsize);
        if (rtSf.use_comp_ref_nonrd != 0 && AomInter.IsCompRefAllowed(bsize))
        {
            if (bsize > BLOCK_16X16) compUseZeroZeromvOnly = rtSf.check_only_zero_zeromv_on_large_blocks;
            else totNumCompModes = 0;
        }
        else totNumCompModes = 0;
        if (x.PredMvSad[LAST_FRAME] != int.MaxValue)
        {
            threshSadPred = (long)x.PredMvSad[LAST_FRAME] << 1;
            if (rtSf.nonrd_prune_ref_frame_search == 1) threshSadPred += x.PredMvSad[LAST_FRAME] >> 2;
        }
        bool useModelYrdLarge = cm.BaseQindex != 0 && bsize >= BLOCK_32X32 && cm.BitDepth == 8;   // get_model_rd_flag (CBR, no cyclic refresh)
        int filtSelect = EIGHTTAP_REGULAR;
        int cbPredFilterSearch = x.SourceSadNonrd > AomRtSb.kVeryLowSad ? cpi.Sf.interp_sf.cb_pred_filter_search : 0;
        int filterSearchEnabledBlk = IsFilterSearchEnabledBlk(cpi, x, miRow, miCol, bsize, cbPredFilterSearch, ref filtSelect);
        InitMbmiNonrd(mi, DC_PRED, NONE_FRAME, NONE_FRAME, cm);
        mi.TxSize = Math.Min(Math.Min((int)MaxTxsizeLookup[bsize], (int)TxModeToBiggestTxSize[tp.TxModeSearchType]), TX_16X16);
        FillSingleInterModeCosts(st.SingleInterModeCosts, mc, ext.ModeContext);
        int lastCompRefFrame = NONE_FRAME;
        x.BlockIsZeroSad = x.SourceSadNonrd == AomRtSb.kZeroSad;
        bool sbMeHasBeenTested = false;
        x.SbMeBlock = x.SbMePartition;
        if (x.SbMeBlock)
        {
            if (cm.SbSize == BLOCK_128X128 && bsize < BLOCK_64X64) x.SbMeBlock = false;
            else if (cm.SbSize == BLOCK_64X64 && bsize < BLOCK_32X32) x.SbMeBlock = false;
        }
        x.MinDistInterUv = long.MaxValue;
        PredBuffer? thisModePred = null;
        for (int idx = 0; idx < numInterModes + totNumCompModes; ++idx)
        {
            if (rtSf.skip_compound_based_on_var && idx == numInterModes && SkipCompBasedOnVar(st.Vars, bsize)) break;
            bool isSinglePred = true;
            if (idx == 0 && x.ForceZeromvSkipForBlk == 0)
            {
                if (st.UseRefFrameMask[LAST_FRAME] != 0 && x.PredMv0Sad[LAST_FRAME] != int.MaxValue)
                {
                    int ySad = x.PredMv0Sad[LAST_FRAME];
                    if (x.PredMv1Sad[LAST_FRAME] != int.MaxValue &&
                        Math.Abs((int)st.FrameMv[NEARMV, LAST_FRAME].Col) + Math.Abs((int)st.FrameMv[NEARMV, LAST_FRAME].Row) <
                        Math.Abs((int)st.FrameMv[NEARESTMV, LAST_FRAME].Col) + Math.Abs((int)st.FrameMv[NEARESTMV, LAST_FRAME].Row))
                        ySad = x.PredMv1Sad[LAST_FRAME];
                    SetColorSensitivity(cpi, x, bsize, ySad, x.SourceVariance, st);
                }
            }
            if (SkipInterModeNonrd(cpi, x, st, ref threshSadPred, ref isSinglePred, out int thisMode, ref lastCompRefFrame, out int refFrame,
                    out int refFrame2, idx, forceSkipLowTempVar, sseZeromvNorm, numInterModes, bsize, compUseZeroZeromvOnly, checkGlobalmv))
                continue;
            for (int plane = 0; plane < 3; plane++)
            {
                xd.Plane[plane].Pre0 = st.Yv12Mb[refFrame, plane];
                if (!isSinglePred) xd.Plane[plane].Pre1 = st.Yv12Mb[refFrame2, plane];
            }
            mi.RefFrame0 = refFrame;
            mi.RefFrame1 = refFrame2;
            AomRdoptInter.SetRefPtrs(cm, xd, refFrame, refFrame2);
            if (st.UseScaledRefFrame[refFrame]) xd.BlockRefScaleFactors[0] = sfNoScale;
            if (!isSinglePred && st.UseScaledRefFrame[refFrame2]) xd.BlockRefScaleFactors[1] = sfNoScale;
            if (!HandleInterModeNonrd(cpi, x, st, ctx, ref thisModePred, tmpBuffer, ref bestEarlyTerm, ref sseZeromvNorm, ref checkGlobalmv, idx,
                    isSinglePred, gfTemporalRef, useModelYrdLarge, filterSearchEnabledBlk, bsize, thisMode, filtSelect, cbPredFilterSearch,
                    reuseInterPred, ref sbMeHasBeenTested))
                break;
        }

        mi.Mode = bp.BestMode;
        mi.MotionMode = (byte)bp.BestMotionMode;
        mi.WmParams.CopyFrom(bp.WmParams);
        mi.NumProjRef = (byte)bp.NumProjRef;
        mi.InterpFilters = bp.BestPredFilter;
        mi.TxSize = bp.BestTxSize;
        Array.Fill(mi.InterTxSize, (byte)mi.TxSize);
        mi.RefFrame0 = bp.BestRefFrame;
        mi.Mv0 = st.FrameMvBest[bp.BestMode, bp.BestRefFrame];
        mi.Mv1 = default;
        if (bp.BestSecondRefFrame > INTRA_FRAME)
        {
            mi.RefFrame1 = bp.BestSecondRefFrame;
            mi.Mv1 = st.FrameMvBest[bp.BestMode, bp.BestSecondRefFrame];
        }
        mi.AngleDelta[0] = 0;
        mi.AngleDelta[1] = 0;
        mi.UseFilterIntra = 0;
        bool forcePaletteTest = false;
        uint bestIntraSadNorm = uint.MaxValue;
        if (x.ForceZeromvSkipForBlk == 0)
            EstimateIntraMode(cpi, x, bsize, bestEarlyTerm, st.RefCostsSingle[INTRA_FRAME], reuseInterPred, origDst, tmpBuffer, ref thisModePred,
                ref st.BestRdc, bp, out bestIntraSadNorm);
        var rt = cpi.Rt!;
        bool skipIdtxPalette = (x.ColorSensitivity[0] != 0 || x.ColorSensitivity[1] != 0) && x.SourceSadNonrd != AomRtSb.kZeroSad &&
                               !rt.HighSourceSad && rt.FrameSourceSad < 1000;
        bool tryPalette = EnablePalette(cpi, bp.BestMode < INTRA_MODE_END, bsize, x.SourceVariance, x.ForceZeromvSkipForBlk, skipIdtxPalette,
            forcePaletteTest);
        HandleScreenContentModeNonrd(cpi, x, st, ctx, tmpBuffer, origDst, skipIdtxPalette, tryPalette, bsize, reuseInterPred);
        pd.Dst = origDst;
        if (tryPalette)
        {
            mi.Palette.PaletteSize0 = bp.Pmi.PaletteSize0;
            mi.Palette.PaletteSize1 = bp.Pmi.PaletteSize1;
            Array.Copy(bp.Pmi.PaletteColors, mi.Palette.PaletteColors, mi.Palette.PaletteColors.Length);
        }
        mi.Mode = bp.BestMode;
        mi.RefFrame0 = bp.BestRefFrame;
        mi.RefFrame1 = bp.BestSecondRefFrame;
        x.TxfmSkip = bp.BestModeSkipTxfm;
        if (mi.HasSecondRef)
        {
            mi.CompGroupIdx = 0;
            mi.CompoundIdx = 1;
            mi.InterinterComp.Type = COMPOUND_AVERAGE;
        }
        if (!mi.IsInterBlock) mi.InterpFilters = AomInterpSearch.Broadcast(SWITCHABLE_FILTERS);
        else if (st.UseScaledRefFrame[bp.BestRefFrame] || (mi.HasSecondRef && st.UseScaledRefFrame[bp.BestSecondRefFrame])) x.ReuseInterPred = false;
        if (reuseInterPred && bp.BestPred != null)
        {
            var bestPred = bp.BestPred;
            if (!(bestPred.Data == origDst.Buf && bestPred.Offset == origDst.Offset) && AomInter.IsInterMode(mi.Mode))
                ConvolveCopy(bestPred.Data, bestPred.Offset, bestPred.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride, bw, bh);
        }
        if (cpi.Sf.inter_sf.adaptive_rd_thresh != 0 && !mi.HasSecondRef)
        {
            int bestModeIdx = ModeIdx[bp.BestRefFrame, ModeOffset(mi.Mode)];
            if (bp.BestRefFrame == INTRA_FRAME)
                foreach (int m in IntraModeList) UpdateThreshFreqFact(cpi, x, bsize, INTRA_FRAME, bestModeIdx, m);
            else
                for (int m = NEARESTMV; m <= NEWMV; ++m) UpdateThreshFreqFact(cpi, x, bsize, bp.BestRefFrame, bestModeIdx, m);
        }
        // store_coding_context_nonrd
        ctx.RdStats.SkipTxfm = (byte)x.TxfmSkip;
        ctx.Skippable = x.TxfmSkip;
        ctx.Mic.CopyFrom(mi);
        ctx.MbmiExtBestInter.CopyFrom(x.MbmiExtInter, AomInter.RefFrameType(mi.RefFrame0, mi.RefFrame1));
        rdCost = st.BestRdc;
        AomRdoptInter.SetRefPtrs(cm, xd, mi.RefFrame0, mi.RefFrame1);
    }

    /// <summary>is_same_gf_and_last_scale.</summary>
    private static bool IsSameGfAndLastScale(AomCommon cm)
    {
        var l = cm.RefScaleFactors[LAST_FRAME];
        var g = cm.RefScaleFactors[GOLDEN_FRAME];
        if (l == null || g == null) return l == g;
        return l.XScaleFp == g.XScaleFp && l.YScaleFp == g.YScaleFp;
    }

    /// <summary>handle_screen_content_mode_nonrd: the IDTX and palette trials (palette needs screen content tools).</summary>
    private static void HandleScreenContentModeNonrd(AomComp cpi, AomMacroblock x, SearchState st, AomPickModeContext ctx, PredBuffer[] tmpBuffer,
        in AomBuf2d origDst, bool skipIdtxPalette, bool tryPalette, int bsize, bool reuseInterPred)
    {
        var xd = x.E;
        var mi = xd.Mi0;
        var pd = xd.Plane[0];
        var bp = st.BestPickmode;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        if (cpi.Cm.BitDepth == 8 && cpi.Sf.rt_sf.use_idtx_nonrd != 0 && !skipIdtxPalette && x.ForceZeromvSkipForBlk == 0 &&
            AomInter.IsInterMode(bp.BestMode) && bp.BestPred != null &&
            (cpi.Sf.rt_sf.prune_idtx_nonrd == 0 || (bsize <= BLOCK_32X32 && bp.BestModeSkipTxfm != 1 && x.SourceVariance > 200)))
            throw new NotImplementedException("av1_block_yrd_idtx (use_idtx_nonrd)");
        if (!tryPalette) return;
        uint intraRefFrameCost = st.RefCostsSingle[INTRA_FRAME];
        if (!(bp.BestMode < INTRA_MODE_END))
        {
            var bestPred = bp.BestPred;
            if (reuseInterPred && bestPred != null && bestPred.Data == origDst.Buf && bestPred.Offset == origDst.Offset)
            {
                var thisModePred = tmpBuffer[GetPredBuffer(tmpBuffer, 3)];
                ConvolveCopy(bestPred.Data, bestPred.Offset, bestPred.Stride, thisModePred.Data, thisModePred.Offset, thisModePred.Stride, bw, bh);
                bp.BestPred = thisModePred;
            }
            pd.Dst = origDst;
        }
        SearchPaletteModeLuma(cpi, x, bsize, (int)intraRefFrameCost, ctx, ref st.ThisRdc, st.BestRdc.Rdcost);
        if (st.ThisRdc.Rdcost < st.BestRdc.Rdcost)
        {
            bp.Pmi.PaletteSize0 = mi.Palette.PaletteSize0;
            bp.Pmi.PaletteSize1 = mi.Palette.PaletteSize1;
            Array.Copy(mi.Palette.PaletteColors, bp.Pmi.PaletteColors, bp.Pmi.PaletteColors.Length);
            bp.BestMode = DC_PRED;
            mi.Mv0 = AomMv.Invalid;
            mi.Mv1 = AomMv.Invalid;
            bp.BestRefFrame = INTRA_FRAME;
            bp.BestSecondRefFrame = NONE_FRAME;
            st.BestRdc.Rate = st.ThisRdc.Rate;
            st.BestRdc.Dist = st.ThisRdc.Dist;
            st.BestRdc.Rdcost = st.ThisRdc.Rdcost;
            bp.BestModeSkipTxfm = st.ThisRdc.SkipTxfm;
            if (x.ColorSensitivity[0] != 0 || x.ColorSensitivity[1] != 0) st.ThisRdc.SkipTxfm = 0;
            if (xd.TxTypeMap[xd.TxTypeMapOffset] != DCT_DCT) xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(ctx.TxTypeMap);
        }
    }
}

internal sealed partial class AomMacroblock
{
    /// <summary>The non-RD search's prediction buffers (pred_buf[MAX_MB_PLANE * MAX_SB_SQUARE]).</summary>
    public byte[]? NonrdPredBuf;
}
