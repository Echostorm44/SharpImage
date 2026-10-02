using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// rdopt.c's single-reference state collection and the compound mode pruning built on it.
internal static partial class AomRdoptInter
{
    private static int InterOffset(int mode) => mode - NEARESTMV;

    /// <summary>collect_single_states.</summary>
    private static void CollectSingleStates(AomMacroblock x, AomInterModeSearchState st, AomMbModeInfo mbmi)
    {
        int refFrame = mbmi.RefFrame0;
        int thisMode = mbmi.Mode;
        int dir = refFrame <= GOLDEN_FRAME ? 0 : 1;
        int off = InterOffset(thisMode);
        int refSet = GetDrlRefmvCount(x, mbmi.RefFrame0, mbmi.RefFrame1, thisMode);
        long simpleRd = st.SimpleRd[thisMode, 0, refFrame];
        for (int i = 1; i < refSet; ++i) simpleRd = Math.Min(simpleRd, st.SimpleRd[thisMode, i, refFrame]);
        var s = new AomSingleInterModeState { Rd = simpleRd, RefFrame = refFrame, Valid = 1 };
        int n = st.SingleStateCnt[dir, off];
        int j;
        for (j = n; j > 0 && st.SingleState[dir, off, j - 1].Rd > s.Rd; --j) st.SingleState[dir, off, j] = st.SingleState[dir, off, j - 1];
        st.SingleState[dir, off, j] = s;
        st.SingleStateCnt[dir, off]++;
        long modelledRd = st.ModelledRd[thisMode, 0, refFrame];
        for (int i = 1; i < refSet; ++i) modelledRd = Math.Min(modelledRd, st.ModelledRd[thisMode, i, refFrame]);
        var m = new AomSingleInterModeState { Rd = modelledRd, RefFrame = refFrame, Valid = 1 };
        n = st.SingleStateModelledCnt[dir, off];
        for (j = n; j > 0 && st.SingleStateModelled[dir, off, j - 1].Rd > m.Rd; --j)
            st.SingleStateModelled[dir, off, j] = st.SingleStateModelled[dir, off, j - 1];
        st.SingleStateModelled[dir, off, j] = m;
        st.SingleStateModelledCnt[dir, off]++;
    }

    /// <summary>analyze_single_states.</summary>
    private static void AnalyzeSingleStates(AomComp cpi, AomInterModeSearchState st)
    {
        int pruneLevel = cpi.Sf.inter_sf.prune_comp_search_by_single_result;
        for (int dir = 0; dir < 2; ++dir)
        {
            int pruneFactor = pruneLevel >= 2 ? 6 : 5;
            long bestRd = Math.Min(st.SingleState[dir, InterOffset(NEWMV), 0].Rd, st.SingleState[dir, InterOffset(GLOBALMV), 0].Rd);
            for (int mode = 0; mode < SINGLE_INTER_MODE_NUM; ++mode)
                for (int i = 1; i < st.SingleStateCnt[dir, mode]; ++i)
                    if (st.SingleState[dir, mode, i].Rd != long.MaxValue && (st.SingleState[dir, mode, i].Rd >> 3) * pruneFactor > bestRd)
                        st.SingleState[dir, mode, i].Valid = 0;
            bestRd = Math.Min(st.SingleStateModelled[dir, InterOffset(NEWMV), 0].Rd, st.SingleStateModelled[dir, InterOffset(GLOBALMV), 0].Rd);
            for (int mode = 0; mode < SINGLE_INTER_MODE_NUM; ++mode)
                for (int i = 1; i < st.SingleStateModelledCnt[dir, mode]; ++i)
                    if (st.SingleStateModelled[dir, mode, i].Rd != long.MaxValue && (st.SingleStateModelled[dir, mode, i].Rd >> 3) * pruneFactor > bestRd)
                        st.SingleStateModelled[dir, mode, i].Valid = 0;
        }
        for (int dir = 0; dir < 2; ++dir)
            for (int mode = 0; mode < SINGLE_INTER_MODE_NUM; ++mode)
            {
                int cntS = st.SingleStateCnt[dir, mode], cntM = st.SingleStateModelledCnt[dir, mode];
                int count = 0;
                int maxCandidates = Math.Max(cntS, cntM);
                for (int i = 0; i < cntS; ++i)
                {
                    if (st.SingleState[dir, mode, i].Rd == long.MaxValue) break;
                    if (st.SingleState[dir, mode, i].Valid != 0) st.SingleRdOrder[dir, mode, count++] = st.SingleState[dir, mode, i].RefFrame;
                }
                if (count >= maxCandidates) continue;
                for (int i = 0; i < cntM && count < maxCandidates; ++i)
                {
                    if (st.SingleStateModelled[dir, mode, i].Rd == long.MaxValue) break;
                    if (st.SingleStateModelled[dir, mode, i].Valid == 0) continue;
                    int rf = st.SingleStateModelled[dir, mode, i].RefFrame;
                    bool match = false;
                    for (int j = 0; j < count; ++j) if (st.SingleRdOrder[dir, mode, j] == rf) { match = true; break; }
                    if (match) continue;
                    bool valid = true;
                    for (int j = 0; j < cntS; ++j)
                        if (rf == st.SingleState[dir, mode, j].RefFrame) { valid = st.SingleState[dir, mode, j].Valid != 0; break; }
                    if (valid) st.SingleRdOrder[dir, mode, count++] = rf;
                }
            }
    }

    /// <summary>compound_skip_get_candidates.</summary>
    private static int CompoundSkipGetCandidates(AomComp cpi, AomInterModeSearchState st, int dir, int mode)
    {
        int off = InterOffset(mode);
        int maxCandidates = 0;
        for (int i = 0; i < FWD_REFS; ++i)
        {
            if (st.SingleRdOrder[dir, off, i] == NONE_FRAME) break;
            maxCandidates++;
        }
        int candidates = maxCandidates;
        int lvl = cpi.Sf.inter_sf.prune_comp_search_by_single_result;
        if (lvl >= 2) candidates = Math.Min(2, maxCandidates);
        if (lvl >= 3)
        {
            if (st.SingleState[dir, off, 0].Rd != long.MaxValue && st.SingleStateModelled[dir, off, 0].Rd != long.MaxValue &&
                st.SingleState[dir, off, 0].RefFrame == st.SingleStateModelled[dir, off, 0].RefFrame)
                candidates = 1;
            if (mode == NEARMV || mode == GLOBALMV) candidates = 1;
        }
        if (lvl >= 4) candidates = Math.Min(1, candidates);
        return candidates;
    }

    /// <summary>compound_skip_by_single_states.</summary>
    private static bool CompoundSkipBySingleStates(AomComp cpi, AomInterModeSearchState st, int thisMode, int rf0, int rf1, AomMacroblock x)
    {
        int[] refs = { rf0, rf1 };
        int[] mode = { AomInter.CompoundRef0Mode(thisMode), AomInter.CompoundRef1Mode(thisMode) };
        int[] off = { InterOffset(mode[0]), InterOffset(mode[1]) };
        int[] dir = { refs[0] <= GOLDEN_FRAME ? 0 : 1, refs[1] <= GOLDEN_FRAME ? 0 : 1 };
        bool[] searched = { false, false };
        bool[] mvMatch = { true, true };
        for (int i = 0; i < 2; ++i)
            for (int j = 0; j < st.SingleStateCnt[dir[i], off[i]]; ++j)
                if (st.SingleState[dir[i], off[i], j].RefFrame == refs[i]) { searched[i] = true; break; }
        int refSet = GetDrlRefmvCount(x, rf0, rf1, thisMode);
        for (int i = 0; i < 2; ++i)
        {
            if (!searched[i] || (mode[i] != NEARESTMV && mode[i] != NEARMV)) continue;
            for (int r = 0; r < refSet; r++)
            {
                GetThisMv(out var singleMv, mode[i], 0, r, false, refs[i], NONE_FRAME, x.MbmiExtInter);
                GetThisMv(out var compMv, thisMode, i, r, false, rf0, rf1, x.MbmiExtInter);
                if (singleMv.AsInt != compMv.AsInt) { mvMatch[i] = false; break; }
            }
        }
        for (int i = 0; i < 2; ++i)
        {
            if (!searched[i] || !mvMatch[i]) continue;
            int candidates = CompoundSkipGetCandidates(cpi, st, dir[i], mode[i]);
            bool match = false;
            for (int j = 0; j < candidates; ++j)
                if (refs[i] == st.SingleRdOrder[dir[i], off[i], j]) { match = true; break; }
            if (!match) return true;
        }
        return false;
    }

    private static void MatchRefFrame(AomMbModeInfo mbmi, int rf0, int rf1, ref int m0, ref int m1)
    {
        if (!mbmi.IsInterBlock) return;
        m0 |= rf0 == mbmi.RefFrame0 ? 1 : 0;
        m1 |= rf1 == mbmi.RefFrame0 ? 1 : 0;
        if (mbmi.HasSecondRef)
        {
            m0 |= rf0 == mbmi.RefFrame1 ? 1 : 0;
            m1 |= rf1 == mbmi.RefFrame1 ? 1 : 0;
        }
    }

    /// <summary>compound_skip_using_neighbor_refs.</summary>
    private static bool CompoundSkipUsingNeighborRefs(AomMacroblockD xd, int thisMode, int rf0, int rf1, int level)
    {
        if (thisMode == NEAREST_NEARESTMV || thisMode == NEAR_NEARMV || thisMode == NEW_NEWMV || thisMode == GLOBAL_GLOBALMV) return false;
        if (level >= 3) return true;
        int m0 = 0, m1 = 0;
        if (xd.LeftAvailable) MatchRefFrame(xd.LeftMbmi!, rf0, rf1, ref m0, ref m1);
        if (xd.UpAvailable) MatchRefFrame(xd.AboveMbmi!, rf0, rf1, ref m0, ref m1);
        return m0 + m1 < level;
    }

    /// <summary>update_best_single_mode.</summary>
    private static void UpdateBestSingleMode(AomInterModeSearchState st, int thisMode, int refFrame, long thisRd)
    {
        if (thisRd < st.BestSingleRd[refFrame])
        {
            st.BestSingleRd[refFrame] = thisRd;
            st.BestSingleMode[refFrame] = thisMode;
        }
    }

    /// <summary>skip_compound_using_best_single_mode_ref.</summary>
    private static bool SkipCompoundUsingBestSingleModeRef(int thisMode, int rf0, int rf1, int[] bestSingleMode, int level)
    {
        if (thisMode == NEAREST_NEARESTMV || thisMode == NEAR_NEARMV || thisMode == NEW_NEWMV || thisMode == GLOBAL_GLOBALMV) return false;
        int compModeRef0 = AomInter.CompoundRef0Mode(thisMode);
        int newmvDir = compModeRef0 != NEWMV ? 1 : 0;
        int singleMode = bestSingleMode[newmvDir == 0 ? rf0 : rf1];
        if (singleMode == NEWMV) return false;
        if (level == 1 && singleMode == MB_MODE_COUNT) return false;
        return true;
    }

    /// <summary>rd_pick_skip_mode.</summary>
    private static void RdPickSkipMode(ref AomRdStats rdCost, AomInterModeSearchState st, AomComp cpi, AomMacroblock x, int bsize, AomBuf2d[,] yv12Mb)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        x.CompoundIdx = 1;
        var skipRd = new AomRdStats();
        skipRd.Invalidate();
        if (cm.SkipModeRefFrame0 == -1 || cm.SkipModeRefFrame1 == -1) return;
        int refFrame = LAST_FRAME + cm.SkipModeRefFrame0, secondRefFrame = LAST_FRAME + cm.SkipModeRefFrame1;
        const int thisMode = NEAREST_NEARESTMV;
        int modeIndex = GetPredictionModeIdx(thisMode, refFrame, secondRefFrame);
        if (modeIndex == THR_INVALID) return;
        if ((!cpi.EnableOnesidedComp || cpi.Sf.inter_sf.disable_onesided_comp != 0) && cpi.AllOneSidedRefs) return;
        mbmi.Mode = thisMode;
        mbmi.UvMode = UV_DC_PRED;
        mbmi.RefFrame0 = refFrame;
        mbmi.RefFrame1 = secondRefFrame;
        int refFrameType = AomInter.RefFrameType(refFrame, secondRefFrame);
        var ext = x.MbmiExtInter;
        if (ext.RefMvCount[refFrameType] == byte.MaxValue)
        {
            if (ext.RefMvCount[refFrame] == byte.MaxValue || ext.RefMvCount[secondRefFrame] == byte.MaxValue) return;
            AomMvPred.FindMvRefs(cm, xd, mbmi, refFrameType, ext);
            AomMvPred.CopyUsableRefMvStackAndWeight(xd, ext, refFrameType);
        }
        var curMv = new AomMv[2];
        if (!BuildCurMv(curMv, thisMode, cm, x, false)) return;
        mbmi.Mv0 = curMv[0];
        mbmi.Mv1 = curMv[1];
        mbmi.UseFilterIntra = 0;
        mbmi.InterintraMode = II_DC_PRED - 1;
        mbmi.CompGroupIdx = 0;
        mbmi.CompoundIdx = (byte)x.CompoundIdx;
        mbmi.InterinterComp.Type = COMPOUND_AVERAGE;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.RefMvIdx = 0;
        mbmi.SkipMode = 1;
        mbmi.SkipTxfm = 1;
        mbmi.Palette.PaletteSize0 = 0;
        mbmi.Palette.PaletteSize1 = 0;
        AomInterpSearch.SetDefaultInterpFilters(mbmi, cm.InterpFilter);
        SetRefPtrs(cm, xd, mbmi.RefFrame0, mbmi.RefFrame1);
        for (int i = 0; i < numPlanes; i++)
        {
            xd.Plane[i].Pre0 = yv12Mb[mbmi.RefFrame0, i];
            xd.Plane[i].Pre1 = yv12Mb[mbmi.RefFrame1, i];
        }
        var origDst = AomBufferSet.FromDst(xd);
        int skipModeCtx = AomPredCommon.SkipMode(xd);
        long bestIntraInterModeCost = long.MaxValue;
        if (rdCost.Dist < long.MaxValue && rdCost.Rate < int.MaxValue)
        {
            bestIntraInterModeCost = AomRd.RdCost(x.Rdmult, rdCost.Rate + x.ModeCosts.SkipModeCost[skipModeCtx * 2 + 0], rdCost.Dist);
            rdCost.Rate += x.ModeCosts.SkipModeCost[skipModeCtx * 2 + 0];
            if (rdCost.Rate < int.MaxValue && rdCost.Dist < long.MaxValue) rdCost.Rdcost = AomRd.RdCost(x.Rdmult, rdCost.Rate, rdCost.Dist);
        }
        // skip_mode_rd
        {
            long totalSse = 0, thisRd = long.MaxValue;
            skipRd.Rate = x.ModeCosts.SkipModeCost[skipModeCtx * 2 + 1];
            for (int plane = 0; plane < numPlanes; ++plane)
            {
                AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, origDst, bsize, plane, plane, cpi.EnableIntraEdgeFilter);
                var pd = xd.Plane[plane];
                int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
                AomEncodeMb.SubtractPlane(x, planeBsize, plane);
                long sse = AomTxSearch.PixelDiffDist(x, plane, 0, 0, planeBsize, planeBsize, out _);
                if (xd.IsHbd) sse = (sse + ((1L << ((xd.Bd - 8) * 2)) >> 1)) >> ((xd.Bd - 8) * 2);
                sse <<= 4;
                totalSse += sse;
                thisRd = AomRd.RdCost(x.Rdmult, skipRd.Rate, totalSse);
                if (thisRd > bestIntraInterModeCost) break;
            }
            skipRd.Dist = skipRd.Sse = totalSse;
            skipRd.Rdcost = thisRd;
            AomInterpSearch.RestoreDstBuf(xd, origDst, numPlanes);
        }
        if (skipRd.Rdcost <= bestIntraInterModeCost && (xd.Lossless[mbmi.SegmentId] == 0 || skipRd.Dist == 0))
        {
            st.BestMbmode.CopyFrom(mbmi);
            st.BestMbmode.SkipMode = 1;
            Array.Fill(st.BestMbmode.InterTxSize, (byte)st.BestMbmode.TxSize);
            AomBitstream.SetTxfmCtxs(xd, st.BestMbmode.TxSize, st.BestMbmode.SkipTxfm != 0 && mbmi.IsInterBlock);
            st.BestModeIndex = modeIndex;
            rdCost.Rate = skipRd.Rate;
            rdCost.Dist = rdCost.Sse = skipRd.Dist;
            rdCost.Rdcost = skipRd.Rdcost;
            st.BestRd = rdCost.Rdcost;
            st.BestSkip2 = 1;
            st.BestModeSkippable = 1;
            x.TxfmSkip = 1;
        }
    }
}
