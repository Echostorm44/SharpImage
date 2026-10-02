using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMacroblock
{
    public readonly AomMbModeInfo ScratchBaseMbmi = new(), ScratchBestMotionMbmi = new(), ScratchBestInterMbmi = new(), ScratchIntraBestMbmi = new();
    public readonly int[] ScratchWarpPts = new int[16], ScratchWarpPtsInref = new int[16];
    public readonly AomInterModeSearchState InterSearchState = new();
    public readonly AomHandleInterModeArgs InterArgs = new();
    public readonly AomMotionModeCandidate MotionModeCand = new();
    public readonly AomMotionModeBestCands BestMotionModeCands = new();
    public readonly AomModeSkipMask ModeSkipMask = new();
    public readonly AomInterModeSfArgs SfArgs = new();
    public readonly uint[] RefCostsSingle = new uint[REF_FRAMES];
    public readonly uint[,] RefCostsComp = new uint[REF_FRAMES, REF_FRAMES];

    /// <summary>tmp_dst of handle_inter_mode: x->tmp_pred_bufs[0]'s three MAX_SB_SQUARE planes, stride MAX_SB_SIZE.</summary>
    public AomBufferSet TmpDstSet()
    {
        var s = new AomBufferSet();
        bool hbd = E.IsHbd;
        for (int p = 0; p < 3; p++)
            s.Plane[p] = new AomBuf2d { Buf = hbd ? null! : TmpPredBufs[0], Buf16 = hbd ? TmpPredBufs16[0] : null!, Offset = p * 128 * 128,
                Offset0 = p * 128 * 128, Stride = 128, Width = 128, Height = 128 };
        return s;
    }
}

internal static partial class AomRdoptInter
{
    private const int REF_SET_FULL = 0, REF_SET_REDUCED = 1, REF_SET_REALTIME = 2;
    private static readonly int[,] ReducedRefCombos =
    {
        { LAST_FRAME, NONE_FRAME }, { ALTREF_FRAME, NONE_FRAME }, { LAST_FRAME, ALTREF_FRAME }, { GOLDEN_FRAME, NONE_FRAME },
        { INTRA_FRAME, NONE_FRAME }, { GOLDEN_FRAME, ALTREF_FRAME }, { LAST_FRAME, GOLDEN_FRAME }, { LAST_FRAME, INTRA_FRAME },
        { LAST_FRAME, BWDREF_FRAME }, { LAST_FRAME, LAST3_FRAME }, { GOLDEN_FRAME, BWDREF_FRAME }, { GOLDEN_FRAME, INTRA_FRAME },
        { BWDREF_FRAME, NONE_FRAME }, { BWDREF_FRAME, ALTREF_FRAME }, { ALTREF_FRAME, INTRA_FRAME }, { BWDREF_FRAME, INTRA_FRAME },
    };

    private static void DisableReference(int rf, bool[,] combo)
    {
        for (int r2 = NONE_FRAME; r2 < REF_FRAMES; ++r2) combo[rf, r2 + 1] = true;
    }

    private static void DisableInterReferencesExceptAltref(bool[,] combo)
    {
        DisableReference(LAST_FRAME, combo); DisableReference(LAST2_FRAME, combo); DisableReference(LAST3_FRAME, combo);
        DisableReference(GOLDEN_FRAME, combo); DisableReference(BWDREF_FRAME, combo); DisableReference(ALTREF2_FRAME, combo);
    }

    /// <summary>default_skip_mask.</summary>
    private static void DefaultSkipMask(AomModeSkipMask mask, int refSet)
    {
        Array.Clear(mask.PredModes);
        if (refSet == REF_SET_FULL)
        {
            Array.Clear(mask.RefCombo);
            return;
        }
        for (int r1 = INTRA_FRAME; r1 < REF_FRAMES; ++r1)
            for (int r2 = NONE_FRAME; r2 < REF_FRAMES; ++r2) mask.RefCombo[r1, r2 + 1] = true;
        if (refSet == REF_SET_REDUCED)
            for (int i = 0; i < ReducedRefCombos.GetLength(0); ++i) mask.RefCombo[ReducedRefCombos[i, 0], ReducedRefCombos[i, 1] + 1] = false;
        else throw new NotImplementedException("real_time_ref_combos");
    }

    /// <summary>init_mode_skip_mask (no segmentation).</summary>
    private static void InitModeSkipMask(AomModeSkipMask mask, AomComp cpi, AomMacroblock x, int bsize)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        var isf = sf.inter_sf;
        int refSet = REF_SET_FULL;
        if (sf.rt_sf.use_real_time_ref_set != 0) refSet = REF_SET_REALTIME;
        else if (cpi.EnableReducedReferenceSet) refSet = REF_SET_REDUCED;
        DefaultSkipMask(mask, refSet);
        int minPredMvSad = int.MaxValue;
        if (refSet == REF_SET_REALTIME) throw new NotImplementedException("real_time_ref_combos");
        for (int rf = LAST_FRAME; rf <= ALTREF_FRAME; ++rf) minPredMvSad = Math.Min(minPredMvSad, x.PredMvSad[rf]);
        for (int rf = LAST_FRAME; rf <= ALTREF_FRAME; ++rf)
        {
            if ((cpi.RefFrameFlags & RefFrameFlagList[rf]) == 0) DisableReference(rf, mask.RefCombo);
            else if ((x.PredMvSad[rf] >> 2) > minPredMvSad) mask.PredModes[rf] |= INTER_NEAREST_NEAR_ZERO;
        }
        if (cpi.IsSrcFrameAltRef && cpi.ArnrMaxFrames == 0)
        {
            DisableInterReferencesExceptAltref(mask.RefCombo);
            mask.PredModes[ALTREF_FRAME] = ~(uint)INTER_NEAREST_NEAR_ZERO;
            GetThisMv(out var nearestMv, NEARESTMV, 0, 0, false, ALTREF_FRAME, NONE_FRAME, x.MbmiExtInter);
            GetThisMv(out var nearMv, NEARMV, 0, 0, false, ALTREF_FRAME, NONE_FRAME, x.MbmiExtInter);
            GetThisMv(out var globalMv, GLOBALMV, 0, 0, false, ALTREF_FRAME, NONE_FRAME, x.MbmiExtInter);
            if (nearMv.AsInt != globalMv.AsInt) mask.PredModes[ALTREF_FRAME] |= 1u << NEARMV;
            if (nearestMv.AsInt != globalMv.AsInt) mask.PredModes[ALTREF_FRAME] |= 1u << NEARESTMV;
        }
        if (cpi.IsSrcFrameAltRef)
            if (isf.alt_ref_search_fp != 0 && (cpi.RefFrameFlags & RefFrameFlagList[ALTREF_FRAME]) != 0)
            {
                mask.PredModes[ALTREF_FRAME] = 0;
                DisableInterReferencesExceptAltref(mask.RefCombo);
                DisableReference(INTRA_FRAME, mask.RefCombo);
            }
        if (isf.alt_ref_search_fp != 0)
            if (!cm.ShowFrame && x.BestPredMvSad[0] < int.MaxValue)
            {
                int sadThresh = x.BestPredMvSad[0] + (x.BestPredMvSad[0] >> 3);
                int startFrame = isf.alt_ref_search_fp == 1 ? ALTREF2_FRAME : BWDREF_FRAME;
                var d = cpi.RefFrameDistInfo.RefRelativeDist;
                for (int rf = startFrame; rf <= ALTREF_FRAME; rf++)
                    if (d[rf - LAST_FRAME] < 0)
                    {
                        if (Math.Abs(d[rf - LAST_FRAME] - d[0]) > 4) continue;
                        if (x.PredMvSad[rf] > sadThresh) mask.PredModes[rf] |= INTER_ALL;
                    }
            }
        if (sf.rt_sf.prune_inter_modes_wrt_gf_arf_based_on_sad != 0) throw new NotImplementedException("prune_inter_modes_wrt_gf_arf_based_on_sad");
        if (bsize > sf.part_sf.max_intra_bsize) DisableReference(INTRA_FRAME, mask.RefCombo);
        if (!cpi.EnableGlobalMotion)
            for (int rf = LAST_FRAME; rf <= ALTREF_FRAME; ++rf)
            {
                mask.PredModes[rf] |= 1u << GLOBALMV;
                mask.PredModes[rf] |= 1u << GLOBAL_GLOBALMV;
            }
        mask.PredModes[INTRA_FRAME] |= ~(uint)sf.intra_sf.intra_y_mode_mask[MaxTxsizeLookup[bsize]];
        if (isf.prune_single_ref != 0)
        {
            double pruneThresh = isf.prune_single_ref <= 3 ? 1.20 : 1.05;
            var dist = cpi.RefFrameDistInfo;
            for (int rf = LAST_FRAME; rf <= ALTREF_FRAME; ++rf)
            {
                bool isClosestRef = rf == dist.NearestPastRef || rf == dist.NearestFutureRef;
                int refIdx = rf - LAST_FRAME;
                if (!((cpi.KeepSingleRefFrameMask & (1 << refIdx)) != 0 || isClosestRef))
                {
                    int dir = dist.RefRelativeDist[rf - LAST_FRAME] < 0 ? 0 : 1;
                    if (x.BestPredMvSad[dir] < int.MaxValue && x.PredMvSad[rf] > pruneThresh * x.BestPredMvSad[dir])
                        mask.PredModes[rf] |= INTER_SINGLE_ALL;
                }
            }
        }
    }

    /// <summary>prune_ref + prune_ref_by_selective_ref_frame (rdopt.h).</summary>
    internal static bool PruneRefBySelectiveRefFrame(AomComp cpi, AomMacroblock? x, int rf0, int rf1, int[] refDisplayOrderHint)
    {
        var sf = cpi.Sf;
        if (sf.inter_sf.selective_ref_frame == 0) return false;
        bool comp = rf1 > INTRA_FRAME;
        bool PruneRef(int[] list, int frameDisplayOrderHint)
        {
            for (int i = 0; i < 2; i++)
            {
                if (list[i] == NONE_FRAME) continue;
                if (rf0 == list[i] || rf1 == list[i])
                    if (refDisplayOrderHint[list[i] - LAST_FRAME] - frameDisplayOrderHint < 0) return true;   // av1_encoder_get_relative_dist
            }
            return false;
        }
        if (sf.inter_sf.selective_ref_frame >= 2 || (sf.inter_sf.selective_ref_frame == 1 && comp))
        {
            int[] list = { LAST3_FRAME, LAST2_FRAME };
            if (x != null)
            {
                if (x.TplKeepRefFrame[LAST3_FRAME] || x.PredMvSad[LAST3_FRAME] == x.BestPredMvSad[0]) list[0] = NONE_FRAME;
                if (x.TplKeepRefFrame[LAST2_FRAME] || x.PredMvSad[LAST2_FRAME] == x.BestPredMvSad[0]) list[1] = NONE_FRAME;
            }
            if (PruneRef(list, refDisplayOrderHint[GOLDEN_FRAME - LAST_FRAME])) return true;
        }
        if (sf.inter_sf.selective_ref_frame >= 3)
        {
            int[] list = { ALTREF2_FRAME, BWDREF_FRAME };
            if (x != null)
            {
                if (x.TplKeepRefFrame[ALTREF2_FRAME] || x.PredMvSad[ALTREF2_FRAME] == x.BestPredMvSad[0]) list[0] = NONE_FRAME;
                if (x.TplKeepRefFrame[BWDREF_FRAME] || x.PredMvSad[BWDREF_FRAME] == x.BestPredMvSad[0]) list[1] = NONE_FRAME;
            }
            if (PruneRef(list, refDisplayOrderHint[LAST_FRAME - LAST_FRAME])) return true;
        }
        if (x != null && sf.inter_sf.prune_comp_ref_frames != 0 && comp)
        {
            var dist = cpi.RefFrameDistInfo;
            bool hasPast = rf0 == dist.NearestPastRef || rf1 == dist.NearestPastRef;
            bool hasFuture = rf0 == dist.NearestFutureRef || rf1 == dist.NearestFutureRef;
            bool closest = hasPast && hasFuture;
            int i0 = rf0 - LAST_FRAME, i1 = rf1 - LAST_FRAME;
            bool keep = (cpi.KeepCompRefFrameMask & (1 << i0)) != 0 && (cpi.KeepCompRefFrameMask & (1 << i1)) != 0;
            if (!(keep || closest))
            {
                if (sf.inter_sf.prune_comp_ref_frames >= 3) return true;
                if (sf.inter_sf.prune_comp_ref_frames >= 1)
                {
                    bool hasBest = false;
                    if (x.BestPredMvSad[0] < int.MaxValue && x.BestPredMvSad[1] < int.MaxValue)
                    {
                        bool bp = x.PredMvSad[rf0] == x.BestPredMvSad[0] || x.PredMvSad[rf1] == x.BestPredMvSad[0];
                        bool bf = x.PredMvSad[rf0] == x.BestPredMvSad[1] || x.PredMvSad[rf1] == x.BestPredMvSad[1];
                        hasBest = bp && bf;
                    }
                    if (!hasBest) return true;
                }
            }
        }
        return false;
    }

    /// <summary>prune_ref_frame.</summary>
    private static bool PruneRefFrame(AomComp cpi, AomMacroblock x, int refFrame)
    {
        var (rf0, rf1) = AomInter.SetRefFrame(refFrame);
        if (((cpi.PruneRefFrameMask >> refFrame) & 1) != 0) return true;
        return PruneRefBySelectiveRefFrame(cpi, x, rf0, rf1, cpi.RefDisplayOrderHint);
    }

    private static bool IsRefFrameUsedByCompoundRef(int refFrame, int skipRefFrameMask)
    {
        for (int r = ALTREF_FRAME + 1; r < MODE_CTX_REF_FRAMES; ++r)
            if ((skipRefFrameMask & (1 << r)) == 0)
            {
                var (a, b) = AomInter.SetRefFrame(r);
                if (a == refFrame || b == refFrame) return true;
            }
        return false;
    }

    private static bool IsRefFrameUsedInCache(int refFrame, AomMbModeInfo? cache)
    {
        if (cache == null) return false;
        if (refFrame < REF_FRAMES) return refFrame == cache.RefFrame0 || refFrame == cache.RefFrame1;
        return refFrame == AomInter.RefFrameType(cache.RefFrame0, cache.RefFrame1);
    }

    /// <summary>calc_target_weighted_pred.</summary>
    private static void CalcTargetWeightedPred(AomCommon cm, AomMacroblock x, AomMacroblockD xd, AomBuf2d above, AomBuf2d left)
    {
        int bsize = xd.Mi0.Bsize;
        int bw = xd.Width * 4, bh = xd.Height * 4;
        var wsrcBuf = x.ObmcBuffer.Wsrc;
        var maskBuf = x.ObmcBuffer.Mask;
        bool hbd = xd.IsHbd;
        const int MaxAlpha = 64, RoundBits = 6;
        const int srcScale = MaxAlpha * MaxAlpha;
        Array.Clear(wsrcBuf, 0, bw * bh);
        for (int i = 0; i < bw * bh; ++i) maskBuf[i] = MaxAlpha;
        if (xd.UpAvailable)
        {
            int overlap = Math.Min(BlockSizeHigh[bsize], BlockSizeHigh[BLOCK_64X64]) >> 1;
            AomInterPred.ForeachOverlappableNbAbove(cm, xd, AomInter.MaxNeighborObmc[MiSizeWideLog2[bsize]], (relMiRow, relMiCol, opMiSize, dir, nb) =>
            {
                var mask1d = AomInterPred.GetObmcMask(overlap);
                int wo = relMiCol * 4;
                int to = above.Offset + relMiCol * 4;
                for (int row = 0; row < overlap; ++row)
                {
                    int m0 = mask1d[row], m1 = MaxAlpha - m0;
                    for (int col = 0; col < opMiSize * 4; ++col)
                    {
                        wsrcBuf[wo + col] = m1 * (hbd ? above.Buf16[to + col] : above.Buf[to + col]);
                        maskBuf[wo + col] = m0;
                    }
                    wo += bw;
                    to += above.Stride;
                }
            });
        }
        for (int i = 0; i < bw * bh; ++i)
        {
            wsrcBuf[i] *= MaxAlpha;
            maskBuf[i] *= MaxAlpha;
        }
        if (xd.LeftAvailable)
        {
            int overlap = Math.Min(BlockSizeWide[bsize], BlockSizeWide[BLOCK_64X64]) >> 1;
            AomInterPred.ForeachOverlappableNbLeft(cm, xd, AomInter.MaxNeighborObmc[MiSizeHighLog2[bsize]], (relMiRow, relMiCol, opMiSize, dir, nb) =>
            {
                var mask1d = AomInterPred.GetObmcMask(overlap);
                int wo = relMiRow * 4 * bw;
                int to = left.Offset + relMiRow * 4 * left.Stride;
                for (int row = 0; row < opMiSize * 4; ++row)
                {
                    for (int col = 0; col < overlap; ++col)
                    {
                        int m0 = mask1d[col], m1 = MaxAlpha - m0;
                        int t = hbd ? left.Buf16[to + col] : left.Buf[to + col];
                        wsrcBuf[wo + col] = (wsrcBuf[wo + col] >> RoundBits) * m0 + (t << RoundBits) * m1;
                        maskBuf[wo + col] = (maskBuf[wo + col] >> RoundBits) * m0;
                    }
                    wo += bw;
                    to += left.Stride;
                }
            });
        }
        var src = x.Plane[0].Src;
        for (int row = 0; row < bh; ++row)
            for (int col = 0; col < bw; ++col)
            {
                int sv = hbd ? src.Buf16[src.Offset + row * src.Stride + col] : src.Buf[src.Offset + row * src.Stride + col];
                wsrcBuf[row * bw + col] = sv * srcScale - wsrcBuf[row * bw + col];
            }
    }

    /// <summary>init_neighbor_pred_buf: the OBMC neighbour prediction planes (above stride MAX_SB_SIZE, left MAX_SB_SIZE / 2).</summary>
    private static void InitNeighborPredBuf(AomMacroblock x, AomHandleInterModeArgs args, bool hbd)
    {
        var ob = x.ObmcBuffer;
        int[] offs = { 0, 128 * 128 >> 1, 128 * 128 };
        for (int p = 0; p < 3; p++)
        {
            args.AbovePredBuf[p] = new AomBuf2d { Buf = hbd ? null! : ob.AbovePred, Buf16 = hbd ? ob.AbovePred16 : null!, Offset = offs[p], Offset0 = offs[p],
                Stride = 128, Width = 128, Height = 64 };
            args.LeftPredBuf[p] = new AomBuf2d { Buf = hbd ? null! : ob.LeftPred, Buf16 = hbd ? ob.LeftPred16 : null!, Offset = offs[p], Offset0 = offs[p],
                Stride = 64, Width = 64, Height = 128 };
        }
    }

    /// <summary>set_params_rd_pick_inter_mode.</summary>
    private static void SetParamsRdPickInterMode(AomComp cpi, AomMacroblock x, AomHandleInterModeArgs args, int bsize, AomModeSkipMask modeSkipMask,
        int skipRefFrameMask, uint[] refCostsSingle, uint[,] refCostsComp, AomBuf2d[,] yv12Mb)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var ext = x.MbmiExtInter;
        InitNeighborPredBuf(x, args, xd.IsHbd);
        CollectNeighborsRefCounts(xd);
        EstimateRefFrameCosts(cm, xd, x.ModeCosts, refCostsSingle, refCostsComp);
        AomTrace.Out?.Write($"rcs {xd.MiRow} {xd.MiCol} {refCostsSingle[0]} {refCostsSingle[1]} {refCostsSingle[2]} {refCostsSingle[3]} {refCostsSingle[4]} {refCostsSingle[5]} {refCostsSingle[6]} {refCostsSingle[7]}" + (char)10);
        int miRow = xd.MiRow, miCol = xd.MiCol;
        x.BestPredMvSad[0] = int.MaxValue;
        x.BestPredMvSad[1] = int.MaxValue;
        for (int rf = LAST_FRAME; rf <= ALTREF_FRAME; ++rf)
        {
            x.PredMvSad[rf] = int.MaxValue;
            ext.ModeContext[rf] = 0;
            ext.RefMvCount[rf] = byte.MaxValue;
            if ((cpi.RefFrameFlags & RefFrameFlagList[rf]) != 0)
            {
                if ((skipRefFrameMask & (1 << rf)) != 0 && !IsRefFrameUsedByCompoundRef(rf, skipRefFrameMask) && !IsRefFrameUsedInCache(rf, x.MbModeCache))
                    continue;
                SetupBufferRefMvsInter(cpi, x, rf, bsize, yv12Mb);
            }
            if (cpi.Sf.inter_sf.alt_ref_search_fp != 0 || cpi.Sf.inter_sf.prune_single_ref != 0 || cpi.Sf.rt_sf.prune_inter_modes_wrt_gf_arf_based_on_sad != 0)
            {
                if (cpi.RefFrameDistInfo.RefRelativeDist[rf - LAST_FRAME] < 0) x.BestPredMvSad[0] = Math.Min(x.BestPredMvSad[0], x.PredMvSad[rf]);
                else x.BestPredMvSad[1] = Math.Min(x.BestPredMvSad[1], x.PredMvSad[rf]);
            }
        }
        if (cpi.Sf.rt_sf.use_real_time_ref_set == 0 && AomInter.IsCompRefAllowed(bsize))
        {
            for (int rf = EXTREF_FRAME; rf < MODE_CTX_REF_FRAMES; ++rf)
            {
                ext.ModeContext[rf] = 0;
                ext.RefMvCount[rf] = byte.MaxValue;
                var (a, b) = AomInter.SetRefFrame(rf);
                if (!((cpi.RefFrameFlags & RefFrameFlagList[a]) != 0 && (cpi.RefFrameFlags & RefFrameFlagList[b]) != 0)) continue;
                if ((skipRefFrameMask & (1 << rf)) != 0 && !IsRefFrameUsedInCache(rf, x.MbModeCache)) continue;
                if (PruneRefFrame(cpi, x, rf)) continue;
                AomMvPred.FindMvRefs(cm, xd, mbmi, rf, ext);
                AomMvPred.CopyUsableRefMvStackAndWeight(xd, ext, rf);
            }
        }
        AomInterPred.CountOverlappableNeighbors(cm, xd);
        bool pruneObmc = cpi.FrameProbs.ObmcProbs[cpi.UpdateType * BLOCK_SIZES_ALL + bsize] < cpi.Sf.inter_sf.prune_obmc_prob_thresh;
        if (cpi.EnableObmc && !pruneObmc)
            if (mbmi.OverlappableNeighbors != 0 && AomInter.IsMotionVariationAllowedBsize(bsize))
            {
                AomInterPred.BuildPredictionByAbovePreds(cm, xd, args.AbovePredBuf);
                AomInterPred.BuildPredictionByLeftPreds(cm, xd, args.LeftPredBuf);
                AomEncodeFrame.SetupDstPlanes(cpi, xd, bsize, miRow, miCol);
                CalcTargetWeightedPred(cm, x, xd, args.AbovePredBuf[0], args.LeftPredBuf[0]);
            }
        InitModeSkipMask(modeSkipMask, cpi, x, bsize);
        AomRdoptUtils.SetModeEvalParams(cpi, x, MODE_EVAL);
        x.CompRdStatsIdx = 0;
        for (int i = 0; i < REF_FRAMES; i++) args.BestSingleSseInRefs[i] = int.MaxValue;
    }

    /// <summary>init_inter_mode_search_state.</summary>
    private static void InitInterModeSearchState(AomInterModeSearchState st, AomComp cpi, AomMacroblock x, int bsize, long bestRdSoFar)
    {
        st.IntraSearchState.Init();
        st.BestYRdcost.Invalidate();
        st.BestRd = bestRdSoFar;
        st.BestSkipRd[0] = long.MaxValue;
        st.BestSkipRd[1] = long.MaxValue;
        st.BestMbmode.CopyFrom(AomMbModeInfo.Zero);
        st.BestRateY = int.MaxValue;
        st.BestRateUv = int.MaxValue;
        st.BestModeSkippable = 0;
        st.BestSkip2 = 0;
        st.BestModeIndex = THR_INVALID;
        var mbmi = x.E.Mi0;
        int seg = mbmi.SegmentId;
        st.NumAvailableRefs = 0;
        Array.Fill(st.DistRefs, -1);
        Array.Fill(st.DistOrderRefs, -1);
        for (int i = 0; i <= LAST_NEW_MV_INDEX; ++i) st.ModeThreshold[i] = 0;
        for (int i = LAST_NEW_MV_INDEX + 1; i < SINGLE_REF_MODE_END; ++i)
            st.ModeThreshold[i] = ((long)cpi.Rd.Thresh(seg, bsize, i) * x.ThreshFreqFact[bsize, i]) >> AomRdOpt.RD_THRESH_FAC_FRAC_BITS;
        st.BestIntraRd = long.MaxValue;
        st.BestPredSse = uint.MaxValue;
        Array.Clear(st.SingleNewmv);
        Array.Clear(st.SingleNewmvRate);
        Array.Clear(st.SingleNewmvValid);
        for (int i = SINGLE_INTER_MODE_START; i < SINGLE_INTER_MODE_END; ++i)
            for (int j = 0; j < MAX_REF_MV_SEARCH; ++j)
                for (int rf = 0; rf < REF_FRAMES; ++rf)
                {
                    st.ModelledRd[i, j, rf] = long.MaxValue;
                    st.SimpleRd[i, j, rf] = long.MaxValue;
                }
        for (int i = 0; i < REFERENCE_MODES; ++i) st.BestPredRd[i] = long.MaxValue;
        if (cpi.Cm.ReferenceMode != SINGLE_REFERENCE)
        {
            for (int i = SINGLE_REF_MODE_END; i < THR_INTER_MODE_END; ++i)
                st.ModeThreshold[i] = ((long)cpi.Rd.Thresh(seg, bsize, i) * x.ThreshFreqFact[bsize, i]) >> AomRdOpt.RD_THRESH_FAC_FRAC_BITS;
            for (int i = COMP_INTER_MODE_START; i < COMP_INTER_MODE_END; ++i)
                for (int j = 0; j < MAX_REF_MV_SEARCH; ++j)
                    for (int rf = 0; rf < REF_FRAMES; ++rf)
                    {
                        st.ModelledRd[i, j, rf] = long.MaxValue;
                        st.SimpleRd[i, j, rf] = long.MaxValue;
                    }
            InitSingleInterModeSearchState(st);
        }
    }

    /// <summary>init_single_inter_mode_search_state.</summary>
    private static void InitSingleInterModeSearchState(AomInterModeSearchState st)
    {
        for (int dir = 0; dir < 2; ++dir)
            for (int mode = 0; mode < SINGLE_INTER_MODE_NUM; ++mode)
                for (int rf = 0; rf < FWD_REFS; ++rf)
                {
                    st.SingleState[dir, mode, rf].RefFrame = NONE_FRAME;
                    st.SingleState[dir, mode, rf].Rd = long.MaxValue;
                    st.SingleStateModelled[dir, mode, rf].RefFrame = NONE_FRAME;
                    st.SingleStateModelled[dir, mode, rf].Rd = long.MaxValue;
                    st.SingleRdOrder[dir, mode, rf] = NONE_FRAME;
                }
        for (int rf = 0; rf < REF_FRAMES; ++rf)
        {
            st.BestSingleRd[rf] = long.MaxValue;
            st.BestSingleMode[rf] = PRED_MODE_INVALID;
        }
        Array.Clear(st.SingleStateCnt);
        Array.Clear(st.SingleStateModelledCnt);
    }

    private static bool MaskSaysSkip(AomModeSkipMask m, int rf0, int rf1, int mode)
        => (m.PredModes[rf0] & (1u << mode)) != 0 || m.RefCombo[rf0, rf1 + 1];

    /// <summary>inter_mode_compatible_skip (no segmentation).</summary>
    private static bool InterModeCompatibleSkip(AomComp cpi, AomMacroblock x, int bsize, int mode, int rf0, int rf1)
    {
        bool comp = rf1 > INTRA_FRAME;
        if (comp)
        {
            if (!AomInter.IsCompRefAllowed(bsize)) return true;
            if ((cpi.RefFrameFlags & RefFrameFlagList[rf1]) == 0) return true;
            if (cpi.Cm.FrameIsIntraOnly) return true;
            if (cpi.Cm.ReferenceMode == SINGLE_REFERENCE) return true;
        }
        if (rf0 > INTRA_FRAME && rf1 == INTRA_FRAME)
        {
            if (!AomInter.IsInterintraAllowedBsize(bsize)) return true;
            if (!AomInter.IsInterintraAllowedMode(mode)) return true;
        }
        return false;
    }

    private static int FetchPickedRefFramesMask(AomMacroblock x, int bsize, int mibSize)
    {
        int m = mibSize - 1;
        var xd = x.E;
        int r0 = xd.MiRow & m, c0 = xd.MiCol & m;
        int mask = 0;
        for (int i = r0; i < r0 + MiSizeHigh[bsize]; ++i)
            for (int j = c0; j < c0 + MiSizeWide[bsize]; ++j) mask |= x.PickedRefFramesMask[i * 32 + j];
        return mask;
    }

    private static bool MatchRefFramePair(AomMbModeInfo mbmi, int rf0, int rf1) => rf0 == mbmi.RefFrame0 && rf1 == mbmi.RefFrame1;

    /// <summary>inter_mode_search_order_independent_skip.</summary>
    private static int InterModeSearchOrderIndependentSkip(AomComp cpi, AomMacroblock x, AomModeSkipMask mask, AomInterModeSearchState st,
        int skipRefFrameMask, int mode, int rf0, int rf1)
    {
        if (MaskSaysSkip(mask, rf0, rf1, mode)) return 1;
        int refType = AomInter.RefFrameType(rf0, rf1);
        if (cpi.Sf.rt_sf.use_real_time_ref_set == 0 && PruneRefFrame(cpi, x, refType)) return 1;
        var cm = cpi.Cm;
        if (SkipRepeatedMv(cm, x, mode, rf0, rf1, st)) return 1;
        if (x.UseMbModeCache)
        {
            // reuse the prediction mode in cache
            var cachedMi = x.MbModeCache!;
            int cachedMode = cachedMi.Mode;
            int cf0 = cachedMi.RefFrame0, cf1 = cachedMi.RefFrame1;
            bool cachedModeIsSingle = cf1 <= INTRA_FRAME;
            if (cachedMode < INTRA_MODE_END && mode != cachedMode) return 1;
            if (cachedModeIsSingle)
            {
                if (mode != cachedMode || rf0 != cf0) return 1;
            }
            else
            {
                bool modeIsSingle = rf1 <= INTRA_FRAME;
                if (modeIsSingle)
                {
                    bool skipMotionModeOnly = false;
                    if (cachedMode == NEW_NEARMV || cachedMode == NEW_NEARESTMV) skipMotionModeOnly = rf0 == cf0;
                    else if (cachedMode == NEAR_NEWMV || cachedMode == NEAREST_NEWMV) skipMotionModeOnly = rf0 == cf1;
                    else if (cachedMode == NEW_NEWMV) skipMotionModeOnly = rf0 == cf0 || rf0 == cf1;
                    return 1 + (skipMotionModeOnly ? 1 : 0);
                }
                if (mode != cachedMode || rf0 != cf0 || rf1 != cf1) return 1;
            }
        }
        var mbmi = x.E.Mi0;
        if (st.BestRd == long.MaxValue && mbmi.Partition == PARTITION_NONE && x.MustFindValidPartition) return 0;
        var sf = cpi.Sf;
        if (sf.inter_sf.prune_nearmv_using_neighbors != 0 && (mode == NEAR_NEARMV || mode == NEARMV))
        {
            var xd = x.E;
            if (st.BestRd != long.MaxValue && xd.LeftAvailable && xd.UpAvailable)
            {
                int[,] thresholds = { { 1, 0, 0 }, { 1, 1, 0 }, { 2, 1, 0 } };
                int qsub = x.Qindex * 3 / QINDEX_RANGE;
                int thresh = thresholds[sf.inter_sf.prune_nearmv_using_neighbors - 1, qsub];
                int n = (MatchRefFramePair(xd.LeftMbmi!, rf0, rf1) ? 1 : 0) + (MatchRefFramePair(xd.AboveMbmi!, rf0, rf1) ? 1 : 0);
                if (n < thresh) return 1;
            }
        }
        bool skipMotionMode = false;
        if (mbmi.Partition != PARTITION_NONE)
        {
            bool skipRef = (skipRefFrameMask & (1 << refType)) != 0;
            if (refType <= ALTREF_FRAME && skipRef)
                if (IsRefFrameUsedByCompoundRef(refType, skipRefFrameMask)) { skipMotionMode = true; skipRef = false; }
            if (IsRefFrameUsedInCache(refType, x.MbModeCache))
            {
                skipRef = false;
                skipMotionMode = refType <= ALTREF_FRAME && x.MbModeCache!.RefFrame1 > INTRA_FRAME;
            }
            if (skipRef) return 1;
        }
        if (rf0 == INTRA_FRAME && mode != DC_PRED)
            if ((sf.rt_sf.mode_search_skip_flags & FLAG_SKIP_INTRA_LOWVAR) != 0 && x.SourceVariance < 64) return 1;
        if (skipMotionMode) return 2;
        return 0;
    }

    /// <summary>init_mbmi.</summary>
    private static void InitMbmi(AomMbModeInfo mbmi, int mode, int rf0, int rf1, AomCommon cm)
    {
        mbmi.RefMvIdx = 0;
        mbmi.Mode = mode;
        mbmi.UvMode = UV_DC_PRED;
        mbmi.RefFrame0 = rf0;
        mbmi.RefFrame1 = rf1;
        mbmi.Palette.PaletteSize0 = 0;
        mbmi.Palette.PaletteSize1 = 0;
        mbmi.UseFilterIntra = 0;
        mbmi.Mv0 = default;
        mbmi.Mv1 = default;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.InterintraMode = II_DC_PRED - 1;
        AomInterpSearch.SetDefaultInterpFilters(mbmi, cm.InterpFilter);
    }

    /// <summary>skip_inter_mode.</summary>
    [ThreadStatic] private static int _skipReason;

    private static bool SkipInterMode(AomComp cpi, AomMacroblock x, int bsize, long[] refFrameRd, int midx, AomInterModeSfArgs args, bool isLowTempVar)
    {
        var sf = cpi.Sf;
        int modeEnum = DefaultModeOrder[midx];
        int thisMode = ModeDefMode(modeEnum), rf0 = ModeDefRef0(modeEnum), rf1 = ModeDefRef1(modeEnum);
        bool comp = rf1 > INTRA_FRAME;
        if (rf0 == INTRA_FRAME) { _skipReason = 1; return true; }
        if (sf.inter_sf.skip_arf_compound != 0 && cpi.UpdateType == ARF_UPDATE && comp) { _skipReason = 2; return true; }
        if (isLowTempVar && !comp && rf0 != LAST_FRAME && thisMode != NEARESTMV) { _skipReason = 3; return true; }
        if (InterModeCompatibleSkip(cpi, x, bsize, thisMode, rf0, rf1)) { _skipReason = 4; return true; }
        int ret = InterModeSearchOrderIndependentSkip(cpi, x, args.ModeSkipMask, args.SearchState, args.SkipRefFrameMask, thisMode, rf0, rf1);
        if (ret == 1) { _skipReason = 5; return true; }
        x.InterArgs.SkipMotionMode = ret == 2 ? 1 : 0;
        if (sf.interp_sf.skip_interp_filter_search == 0 && sf.inter_sf.prune_comp_search_by_single_result > 0 && comp && !args.ReachFirstCompMode)
        {
            AnalyzeSingleStates(cpi, args.SearchState);
            args.ReachFirstCompMode = true;
        }
        int mulFact = args.SearchState.BestModeSkippable != 0 ? args.ModeThreshMulFact : 1 << MODE_THRESH_QBITS;
        long modeThreshold = (args.SearchState.ModeThreshold[modeEnum] * mulFact) >> MODE_THRESH_QBITS;
        if (args.SearchState.BestRd < modeThreshold) { _skipReason = 6; return true; }
        if (sf.interp_sf.skip_interp_filter_search == 0 && sf.inter_sf.prune_comp_search_by_single_result > 0 && comp)
            if (CompoundSkipBySingleStates(cpi, args.SearchState, thisMode, rf0, rf1, x)) { _skipReason = 7; return true; }
        if (sf.inter_sf.prune_compound_using_single_ref != 0 && comp)
        {
            if (!args.PruneCpdUsingSrStatsReady && args.NumSingleModesProcessed == NUM_SINGLE_REF_MODES)
            {
                FindTopRef(refFrameRd);
                args.PruneCpdUsingSrStatsReady = true;
            }
            if (args.PruneCpdUsingSrStatsReady && !InSingleRefCutoff(refFrameRd, rf0, rf1)) { _skipReason = 8; return true; }
        }
        if (sf.inter_sf.skip_ext_comp_nearmv_mode != 0 && (thisMode == NEW_NEARMV || thisMode == NEAR_NEWMV)) { _skipReason = 9; return true; }
        if (sf.inter_sf.prune_ext_comp_using_neighbors != 0 && comp)
            if (CompoundSkipUsingNeighborRefs(x.E, thisMode, rf0, rf1, sf.inter_sf.prune_ext_comp_using_neighbors)) { _skipReason = 10; return true; }
        if (sf.inter_sf.prune_comp_using_best_single_mode_ref != 0 && comp)
            if (SkipCompoundUsingBestSingleModeRef(thisMode, rf0, rf1, args.SearchState.BestSingleMode, sf.inter_sf.prune_comp_using_best_single_mode_ref))
                { _skipReason = 11; return true; }
        if (sf.inter_sf.prune_nearest_near_mv_using_refmv_weight != 0 && !comp)
        {
            int rft = AomInter.RefFrameType(rf0, rf1);
            if (SkipNearestNearMvUsingRefmvWeight(x, thisMode, rft, args.SearchState.BestMbmode.Mode)) { _skipReason = 12; return true; }
        }
        if (sf.rt_sf.prune_inter_modes_with_golden_ref != 0 && rf0 == GOLDEN_FRAME && !comp)
            throw new NotImplementedException("prune_inter_modes_with_golden_ref");
        return false;
    }

    /// <summary>find_top_ref.</summary>
    private static void FindTopRef(long[] refFrameRd)
    {
        var copy = new long[REF_FRAMES - 1];
        Array.Copy(refFrameRd, 1, copy, 0, REF_FRAMES - 1);
        Array.Sort(copy);
        long cutoff = copy[0];
        if (cutoff != long.MaxValue) cutoff = (110 * cutoff) / 100;
        refFrameRd[0] = cutoff;
    }

    private static bool InSingleRefCutoff(long[] refFrameRd, int f1, int f2) => refFrameRd[f1] <= refFrameRd[0] || refFrameRd[f2] <= refFrameRd[0];

    /// <summary>update_search_state.</summary>
    private static void UpdateSearchState(AomInterModeSearchState st, ref AomRdStats bestRdStatsDst, AomPickModeContext ctx, in AomRdStats newBest,
        in AomRdStats newBestY, in AomRdStats newBestUv, int newBestMode, AomMacroblock x, bool txfmSearchDone)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        bool skipTxfm = mbmi.SkipTxfm != 0 && ModeDefMode(newBestMode) >= INTRA_MODE_END;
        st.BestRd = newBest.Rdcost;
        st.BestModeIndex = newBestMode;
        bestRdStatsDst = newBest;
        st.BestMbmode.CopyFrom(mbmi);
        st.BestSkip2 = skipTxfm ? 1 : 0;
        st.BestModeSkippable = newBest.SkipTxfm;
        if (txfmSearchDone)
        {
            st.BestRateY = newBestY.Rate + x.ModeCosts.SkipTxfmCost[skipCtx * 2 + (newBest.SkipTxfm != 0 || skipTxfm ? 1 : 0)];
            st.BestRateUv = newBestUv.Rate;
        }
        st.BestYRdcost = newBestY;
        CopyTxTypeMapTo(xd, ctx.TxTypeMap, ctx.NumFourByFourBlk);
    }

    /// <summary>record_best_compound.</summary>
    private static void RecordBestCompound(int referenceMode, in AomRdStats rdStats, bool comp, int rdmult, AomInterModeSearchState st, int compmodeCost)
    {
        long singleRate, hybridRate;
        if (referenceMode == REFERENCE_MODE_SELECT)
        {
            singleRate = rdStats.Rate - compmodeCost;
            hybridRate = rdStats.Rate;
        }
        else
        {
            singleRate = rdStats.Rate;
            hybridRate = rdStats.Rate + compmodeCost;
        }
        long singleRd = AomRd.RdCost(rdmult, singleRate, rdStats.Dist);
        long hybridRd = AomRd.RdCost(rdmult, hybridRate, rdStats.Dist);
        if (!comp) { if (singleRd < st.BestPredRd[SINGLE_REFERENCE]) st.BestPredRd[SINGLE_REFERENCE] = singleRd; }
        else if (singleRd < st.BestPredRd[COMPOUND_REFERENCE]) st.BestPredRd[COMPOUND_REFERENCE] = singleRd;
        if (hybridRd < st.BestPredRd[REFERENCE_MODE_SELECT]) st.BestPredRd[REFERENCE_MODE_SELECT] = hybridRd;
    }

    /// <summary>handle_winner_cand.</summary>
    private static void HandleWinnerCand(AomMbModeInfo mbmi, AomMotionModeBestCands best, int maxCand, long thisRd, AomMotionModeCandidate cand,
        int skipMotionMode)
    {
        int num = best.Num;
        int loc = num;
        for (int j = 0; j < num; j++)
            if (thisRd < best.Cand[j].RdCost) { loc = j; break; }
        if (loc < maxCand)
        {
            if (num > 0 && loc < maxCand - 1)
            {
                // memmove of min(num, max - 1) - loc entries one slot up
                int count = Math.Min(num, maxCand - 1) - loc;
                for (int k = loc + count; k > loc; k--) best.Cand[k].CopyFrom(best.Cand[k - 1]);
            }
            cand.Mbmi.CopyFrom(mbmi);
            cand.RdCost = thisRd;
            cand.SkipMotionMode = skipMotionMode;
            best.Cand[loc].CopyFrom(cand);
            best.Num = Math.Min(maxCand, best.Num + 1);
        }
    }

    /// <summary>skip_interp_filter_search.</summary>
    private static bool SkipInterpFilterSearch(AomComp cpi, bool isSinglePred)
    {
        if (cpi.Mode == REALTIME)
            return cpi.Cm.ReferenceMode == SINGLE_REFERENCE &&
                   (cpi.Sf.interp_sf.skip_interp_filter_search != 0 || cpi.Sf.winner_mode_sf.winner_mode_ifs != 0);
        if (cpi.Mode == GOOD) return cpi.Sf.interp_sf.skip_interp_filter_search != 0 && isSinglePred;
        return false;
    }

    /// <summary>av1_rd_pick_inter_mode.</summary>
    public static void RdPickInterMode(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize, AomPickModeContext ctx, long bestRdSoFar)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var sf = cpi.Sf;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var mc = x.ModeCosts;
        int compInterCtx = AomPredCommon.ReferenceMode(xd);
        var st = x.InterSearchState;
        InitInterModeSearchState(st, cpi, x, bsize, bestRdSoFar);
        var args = x.InterArgs;
        args.State = st;
        for (int i = 0; i < REF_FRAMES; i++) args.InterIntraMode[i] = INTERINTRA_MODES;
        args.RefFrameCost = int.MaxValue;
        args.SingleCompCost = int.MaxValue;
        args.SkipMotionMode = 0;
        args.SkipIfs = false;
        args.StartMvCnt = 0;
        args.InterpFilterStatsIdx = 0;
        args.WedgeIndex = -1; args.WedgeSign = -1; args.DiffwtdIndex = -1;
        args.BestPredSse = uint.MaxValue;
        Array.Clear(args.BestSingleSseInRefs);
        if (sf.tx_sf.prune_inter_tx_split_rd_eval_lvl != 0)
            for (int i = 0; i < MAX_TX_BLOCKS_IN_MAX_SB; i++)
                for (int j = 0; j < TOP_INTER_TX_NO_SPLIT_COUNT; j++) x.TopInterTxNoSplitRd[i, j] = long.MaxValue;
        bool isLowTempVar = false;   // get_block_temp_var: real-time variance partitioning only
        if (sf.part_sf.partition_search_type == VAR_BASED_PARTITION && sf.rt_sf.short_circuit_low_temp_var != 0 &&
            sf.rt_sf.prune_inter_modes_using_temp_var != 0)
            throw new NotImplementedException("get_block_temp_var");
        for (int i = 0; i < MODE_CTX_REF_FRAMES; ++i) args.CmpMode[i] = -1;
        int maxWinnerMotionModeCand = NumWinnerMotionModes[sf.winner_mode_sf.motion_mode_for_winner_cand];
        var motionModeCand = x.MotionModeCand;
        var bestMotionModeCands = x.BestMotionModeCands;
        bestMotionModeCands.Num = 0;
        for (int i = 0; i < MAX_WINNER_MOTION_MODES; ++i) bestMotionModeCands.Cand[i].RdCost = long.MaxValue;
        for (int i = 0; i < REF_FRAMES; ++i) x.PredSse[i] = int.MaxValue;
        rdCost.Invalidate();
        for (int i = 0; i < REF_FRAMES; ++i) x.WarpSampleInfo[i].Num = -1;

        int pickedRefFramesMask = 0;
        if (sf.inter_sf.prune_ref_frame_for_rect_partitions != 0 && mbmi.Partition != PARTITION_NONE)
            if ((mbmi.Partition != PARTITION_VERT && mbmi.Partition != PARTITION_HORZ) || sf.inter_sf.prune_ref_frame_for_rect_partitions >= 2)
                pickedRefFramesMask = FetchPickedRefFramesMask(x, bsize, cm.MibSize);
        int skipRefFrameMask = pickedRefFramesMask != 0 ? ~pickedRefFramesMask : 0;
        var modeSkipMask = x.ModeSkipMask;
        var refCostsSingle = x.RefCostsSingle;
        var refCostsComp = x.RefCostsComp;
        var yv12Mb = x.Yv12Mb;
        SetParamsRdPickInterMode(cpi, x, args, bsize, modeSkipMask, skipRefFrameMask, refCostsSingle, refCostsComp, yv12Mb);

        long bestEstRd = long.MaxValue;
        var md = x.TileData!.InterModeRdModels[bsize];
        bool doTxSearch = !((sf.inter_sf.inter_mode_rd_model_estimation == 1 && md.Ready != 0) ||
                            (sf.inter_sf.inter_mode_rd_model_estimation == 2 && NumPelsLog2Lookup[bsize] > 8));
        var interModesInfo = x.InterModesInfo;
        interModesInfo.Num = 0;
        var refFrameRd = new long[REF_FRAMES];
        Array.Fill(refFrameRd, long.MaxValue);
        long interCost = -1, intraCost = -1;
        if (sf.inter_sf.prune_inter_modes_based_on_tpl != 0)
        {
            var validRefs = new bool[INTER_REFS_PER_FRAME];
            for (int frame = LAST_FRAME; frame < REF_FRAMES; frame++)
                validRefs[frame - 1] = x.TplKeepRefFrame[frame] || !PruneRefBySelectiveRefFrame(cpi, x, frame, NONE_FRAME, cpi.RefDisplayOrderHint);
            Array.Clear(args.TplRefInterCost);
            args.TplBestInterCost = 0;
            GetBlockLevelTplStats(cpi, bsize, xd.MiRow, xd.MiCol, validRefs, args);
        }
        bool doPruning = !(Math.Min(cm.Width, cm.Height) > 480 && cpi.Speed <= 1);
        if (doPruning && sf.intra_sf.skip_intra_in_interframe != 0 && cpi.EnableTplModel && cpi.Tpl != null)
            CalculateCostFromTplData(cpi, x, bsize, xd.MiRow, xd.MiCol, ref interCost, ref intraCost);

        int maxWinnerModeCount = AomRdoptUtils.WinnerModeCountAllowed[sf.winner_mode_sf.multi_winner_mode_type];
        AomRdoptUtils.ZeroWinnerModeStats(bsize, maxWinnerModeCount, x.WinnerModeStats);
        x.WinnerModeCount = 0;
        AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, null, null, null, THR_INVALID, null, bsize, bestRdSoFar, sf.winner_mode_sf.multi_winner_mode_type,
            false);
        int modeThreshMulFact = 1 << MODE_THRESH_QBITS;
        if (sf.inter_sf.prune_inter_modes_if_skippable != 0) modeThreshMulFact = ModeThresholdMulFactor[x.Qindex];
        var sfArgs = x.SfArgs;
        sfArgs.ModeSkipMask = modeSkipMask;
        sfArgs.SearchState = st;
        sfArgs.SkipRefFrameMask = skipRefFrameMask;
        sfArgs.ReachFirstCompMode = false;
        sfArgs.ModeThreshMulFact = modeThreshMulFact;
        sfArgs.NumSingleModesProcessed = 0;
        sfArgs.PruneCpdUsingSrStatsReady = false;
        long bestInterYrd = long.MaxValue;
        int modeStart = THR_INTER_MODE_START, modeEnd = THR_INTER_MODE_END;
        if (cm.ReferenceMode == SINGLE_REFERENCE) { modeStart = SINGLE_REF_MODE_START; modeEnd = SINGLE_REF_MODE_END; }
        if (sf.inter_sf.skip_cmp_using_top_cmp_avg_est_rd_lvl != 0)
            for (int j = 0; j < TOP_COMP_AVG_EST_RD_COUNT; j++) x.TopCompAvgEstRd[j] = long.MaxValue;
        var skipRd = new long[2];
        for (int midx = modeStart; midx < modeEnd; ++midx)
        {
            int modeEnum = DefaultModeOrder[midx];
            int thisMode = ModeDefMode(modeEnum), rf0 = ModeDefRef0(modeEnum), rf1 = ModeDefRef1(modeEnum);
            bool isSinglePred = rf0 > INTRA_FRAME && rf1 == NONE_FRAME;
            bool compPred = rf1 > INTRA_FRAME;
            x.TxfmSkip = 0;
            if (isSinglePred) sfArgs.NumSingleModesProcessed++;
            bool isSkipInterMode = SkipInterMode(cpi, x, bsize, refFrameRd, midx, sfArgs, isLowTempVar);
            if (AomTrace.Out != null && ModeDefMode(DefaultModeOrder[midx]) >= NEARESTMV)
                AomTrace.Out.Write($"skm {xd.MiRow} {xd.MiCol} m {ModeDefMode(DefaultModeOrder[midx])} r {ModeDefRef0(DefaultModeOrder[midx])} {ModeDefRef1(DefaultModeOrder[midx])} skip {(isSkipInterMode ? 1 : 0)} why {(isSkipInterMode ? _skipReason : 0)} best {st.BestRd} th {st.ModeThreshold[DefaultModeOrder[midx]]} fact {x.ThreshFreqFact[bsize, DefaultModeOrder[midx]]}" + (char)10);
            if (isSkipInterMode) continue;
            InitMbmi(mbmi, thisMode, rf0, rf1, cm);
            SetRefPtrs(cm, xd, rf0, rf1);
            for (int i = 0; i < numPlanes; i++)
            {
                xd.Plane[i].Pre0 = yv12Mb[rf0, i];
                if (compPred) xd.Plane[i].Pre1 = yv12Mb[rf1, i];
            }
            mbmi.AngleDelta[0] = 0;
            mbmi.AngleDelta[1] = 0;
            mbmi.UseFilterIntra = 0;
            mbmi.RefMvIdx = 0;
            long refBestRd = st.BestRd;
            AomRdStats rdStats = default, rdStatsY = default, rdStatsUv = default;
            rdStats.Init();
            int refFrameCost = compPred ? (int)refCostsComp[rf0, rf1] : (int)refCostsSingle[rf0];
            int compmodeCost = AomInter.IsCompRefAllowed(mbmi.Bsize) ? mc.CompInterCost[compInterCtx * 2 + (compPred ? 1 : 0)] : 0;
            int realCompmodeCost = cm.ReferenceMode == REFERENCE_MODE_SELECT ? compmodeCost : 0;
            args.SingleCompCost = realCompmodeCost;
            args.RefFrameCost = refFrameCost;
            args.BestPredSse = st.BestPredSse;
            args.SkipIfs = SkipInterpFilterSearch(cpi, isSinglePred);
            skipRd[0] = st.BestSkipRd[0];
            skipRd[1] = st.BestSkipRd[1];
            AomTrace.Out?.Write($"hm {xd.MiRow} {xd.MiCol} bs {bsize} m {thisMode} r {rf0} {rf1} best {refBestRd}" + (char)10);
            long thisRd = HandleInterMode(cpi, x, bsize, ref rdStats, ref rdStatsY, ref rdStatsUv, args, refBestRd, ref bestEstRd, doTxSearch,
                interModesInfo, motionModeCand, skipRd, out long thisYrd);
            AomTrace.Out?.Write($"hmr {thisRd} rate {rdStats.Rate} dist {rdStats.Dist}" + (char)10);
            if (cm.ReferenceMode != SINGLE_REFERENCE)
            {
                if (!args.SkipIfs && sf.inter_sf.prune_comp_search_by_single_result > 0 && AomInter.IsInterSinglerefMode(thisMode))
                    CollectSingleStates(x, st, mbmi);
                if (sf.inter_sf.prune_comp_using_best_single_mode_ref > 0 && AomInter.IsInterSinglerefMode(thisMode))
                    UpdateBestSingleMode(st, thisMode, rf0, thisRd);
            }
            if (thisRd == long.MaxValue) continue;
            if (mbmi.SkipTxfm != 0)
            {
                rdStatsY.Rate = 0;
                rdStatsUv.Rate = 0;
            }
            if (sf.inter_sf.prune_compound_using_single_ref != 0 && isSinglePred && thisRd < refFrameRd[rf0]) refFrameRd[rf0] = thisRd;
            AdjustCost(cpi, x, ref thisRd, true);
            AdjustRdcost(cpi, x, ref rdStats, true);
            if (thisRd < st.BestRd)
            {
                st.BestPredSse = x.PredSse[rf0];
                bestInterYrd = thisYrd;
                UpdateSearchState(st, ref rdCost, ctx, rdStats, rdStatsY, rdStatsUv, modeEnum, x, doTxSearch);
                if (doTxSearch) st.BestSkipRd[0] = skipRd[0];
                st.BestSkipRd[1] = skipRd[1];
            }
            if (sf.winner_mode_sf.motion_mode_for_winner_cand != 0)
                HandleWinnerCand(mbmi, bestMotionModeCands, maxWinnerMotionModeCand, thisRd, motionModeCand, args.SkipMotionMode);
            RecordBestCompound(cm.ReferenceMode, rdStats, compPred, x.Rdmult, st, compmodeCost);
        }

        if (sf.winner_mode_sf.motion_mode_for_winner_cand != 0)
            EvaluateMotionModeForWinnerCandidates(cpi, x, ref rdCost, args, ctx, yv12Mb, bestMotionModeCands, doTxSearch, bsize, ref bestEstRd, st,
                ref bestInterYrd);
        if (!doTxSearch) TxSearchBestInterCandidates(cpi, x, bestRdSoFar, bsize, yv12Mb, st, ref rdCost, ctx, ref bestInterYrd);

        SkipIntraModesInInterframe(cpi, x, bsize, st, interCost, intraCost);
        uint intraRefFrameCost = refCostsSingle[INTRA_FRAME];
        SearchIntraModesInInterframe(st, cpi, x, ref rdCost, bsize, ctx, sfArgs, intraRefFrameCost, bestInterYrd);

        int winnerModeCount = sf.winner_mode_sf.multi_winner_mode_type != 0 ? x.WinnerModeCount : 1;
        RefineWinnerModeTx(cpi, x, ref rdCost, bsize, ctx, ref st.BestModeIndex, st.BestMbmode, yv12Mb, st.BestRateY, st.BestRateUv, ref st.BestSkip2,
            winnerModeCount);
        AomRdoptUtils.SetModeEvalParams(cpi, x, DEFAULT_EVAL);

        bool tryPalette = cpi.EnablePalette && AomIntraModeSearch.AllowPalette(cpi.AllowScreenContentTools, mbmi.Bsize) &&
                          !AomInter.IsInterMode(st.BestMbmode.Mode) && rdCost.Rate != int.MaxValue;
        if (tryPalette) throw new NotImplementedException("av1_search_palette_mode in inter frames");

        st.BestMbmode.SkipMode = 0;
        if (cm.SkipModeFlag && cpi.Sharpness != 3 && AomInter.IsCompRefAllowed(bsize))
            RdPickSkipMode(ref rdCost, st, cpi, x, bsize, yv12Mb);

        if (st.BestMbmode.RefMvIdx != 0 &&
            !(st.BestMbmode.Mode == NEWMV || st.BestMbmode.Mode == NEW_NEWMV || AomInter.HaveNearmvInInterMode(st.BestMbmode.Mode)))
            st.BestMbmode.RefMvIdx = 0;
        if (st.BestModeIndex == THR_INVALID || st.BestRd >= bestRdSoFar)
        {
            rdCost.Rate = int.MaxValue;
            rdCost.Rdcost = long.MaxValue;
            return;
        }
        if (!cpi.IsSrcFrameAltRef && sf.inter_sf.adaptive_rd_thresh != 0)
            AomRdOpt.UpdateRdThreshFact(cm.SbSize, x.ThreshFreqFact, sf.inter_sf.adaptive_rd_thresh, bsize, st.BestModeIndex, modeStart, modeEnd, THR_DC,
                MAX_MODES);
        mbmi.CopyFrom(st.BestMbmode);
        x.TxfmSkip |= st.BestSkip2;
        x.TxfmSkip |= st.BestModeSkippable;
        StoreCodingContext(x, ctx, st.BestModeSkippable);
        if (mbmi.Palette.PaletteSize1 > 0) throw new NotImplementedException("av1_restore_uv_color_map");
    }

    /// <summary>evaluate_motion_mode_for_winner_candidates.</summary>
    private static void EvaluateMotionModeForWinnerCandidates(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, AomHandleInterModeArgs args,
        AomPickModeContext ctx, AomBuf2d[,] yv12Mb, AomMotionModeBestCands best, bool doTxSearch, int bsize, ref long bestEstRd,
        AomInterModeSearchState st, ref long yrd)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var interModesInfo = x.InterModesInfo;
        var skipRd = new long[2];
        for (int c = 0; c < best.Num; c++)
        {
            AomRdStats rdStats = default, rdStatsY = default, rdStatsUv = default;
            rdStats.Init(); rdStatsY.Init(); rdStatsUv.Init();
            int rateMv = best.Cand[c].RateMv;
            args.SkipMotionMode = best.Cand[c].SkipMotionMode;
            mbmi.CopyFrom(best.Cand[c].Mbmi);
            rdStats.Rate = best.Cand[c].Rate2Nocoeff;
            if (!AomInter.IsInterSinglerefMode(mbmi.Mode)) continue;
            x.TxfmSkip = 0;
            var origDst = AomBufferSet.FromDst(xd);
            SetRefPtrs(cm, xd, mbmi.RefFrame0, mbmi.RefFrame1);
            mbmi.MotionMode = 0;
            bool isComp = mbmi.RefFrame1 > INTRA_FRAME;
            for (int i = 0; i < numPlanes; i++)
            {
                xd.Plane[i].Pre0 = yv12Mb[mbmi.RefFrame0, i];
                if (isComp) xd.Plane[i].Pre1 = yv12Mb[mbmi.RefFrame1, i];
            }
            skipRd[0] = st.BestSkipRd[0];
            skipRd[1] = st.BestSkipRd[1];
            long ret = MotionModeRd(cpi, x, bsize, ref rdStats, ref rdStatsY, ref rdStatsUv, args, st.BestRd, skipRd, ref rateMv, origDst, ref bestEstRd,
                doTxSearch, interModesInfo, true, out long thisYrd);
            if (ret != long.MaxValue)
            {
                rdStats.Rdcost = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
                int modeEnum = GetPredictionModeIdx(mbmi.Mode, mbmi.RefFrame0, mbmi.RefFrame1);
                AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, rdStats, rdStatsY, rdStatsUv, modeEnum, null, bsize, rdStats.Rdcost,
                    cpi.Sf.winner_mode_sf.multi_winner_mode_type, doTxSearch);
                long bestScaledRd = st.BestRd, thisScaledRd = rdStats.Rdcost;
                if (st.BestModeIndex != THR_INVALID)
                    IncreaseMotionModeRd(st.BestMbmode, mbmi, ref bestScaledRd, ref thisScaledRd, cpi.Sf.inter_sf.bias_warp_mode_rd_scale_pct,
                        cpi.Sf.inter_sf.bias_obmc_mode_rd_scale_pct);
                if (thisScaledRd < bestScaledRd)
                {
                    yrd = thisYrd;
                    UpdateSearchState(st, ref rdCost, ctx, rdStats, rdStatsY, rdStatsUv, modeEnum, x, doTxSearch);
                    if (doTxSearch) st.BestSkipRd[0] = skipRd[0];
                }
            }
        }
    }

    /// <summary>tx_search_best_inter_candidates.</summary>
    private static void TxSearchBestInterCandidates(AomComp cpi, AomMacroblock x, long bestRdSoFar, int bsize, AomBuf2d[,] yv12Mb,
        AomInterModeSearchState st, ref AomRdStats rdCost, AomPickModeContext ctx, ref long yrd)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mc = x.ModeCosts;
        int numPlanes = cm.NumPlanes;
        int skipCtx = AomTxSearch.SkipTxfmContext(xd);
        var mbmi = xd.Mi0;
        var info = x.InterModesInfo;
        InterModesInfoSort(info);
        st.BestRd = bestRdSoFar;
        st.BestModeIndex = THR_INVALID;
        x.WinnerModeCount = 0;
        AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, null, null, null, THR_INVALID, null, bsize, bestRdSoFar,
            cpi.Sf.winner_mode_sf.multi_winner_mode_type, false);
        info.Num = info.Num < cpi.Sf.rt_sf.num_inter_modes_for_tx_search ? info.Num : cpi.Sf.rt_sf.num_inter_modes_for_tx_search;
        long topEstRd = info.Num > 0 ? info.EstRdArr[info.RdIdxPairArr[0].Idx] : long.MaxValue;
        yrd = long.MaxValue;
        long bestRdInThisPartition = long.MaxValue;
        int numInterModeCands = info.Num;
        bool newmvModeEvaled = false;
        int maxAllowedCands = int.MaxValue;
        if (cpi.Sf.inter_sf.limit_inter_mode_cands != 0)
        {
            int[] n = { int.MaxValue, 10, 9, 6, 2 };
            maxAllowedCands = n[cpi.Sf.inter_sf.limit_inter_mode_cands];
        }
        int numModeThresh = int.MaxValue;
        if (cpi.Sf.inter_sf.limit_txfm_eval_per_mode != 0)
        {
            int[] n = { int.MaxValue, 4, 3, 0 };
            numModeThresh = n[cpi.Sf.inter_sf.limit_txfm_eval_per_mode];
        }
        int numTxCands = 0;
        var numTxSearchModes = new int[INTER_MODE_END - INTER_MODE_START];
        for (int j = 0; j < numInterModeCands; ++j)
        {
            int dataIdx = info.RdIdxPairArr[j].Idx;
            mbmi.CopyFrom(info.MbmiArr[dataIdx]);
            int predictionMode = mbmi.Mode;
            long currEstRd = info.EstRdArr[dataIdx];
            if (currEstRd * 0.80 > topEstRd) break;
            if (numTxCands > numModeThresh)
            {
                if ((predictionMode != NEARESTMV && numTxSearchModes[predictionMode - INTER_MODE_START] >= 1) ||
                    (predictionMode == NEARESTMV && numTxSearchModes[predictionMode - INTER_MODE_START] >= 2))
                    continue;
            }
            x.TxfmSkip = 0;
            SetRefPtrs(cm, xd, mbmi.RefFrame0, mbmi.RefFrame1);
            bool isComp = mbmi.RefFrame1 > INTRA_FRAME;
            for (int i = 0; i < numPlanes; i++)
            {
                xd.Plane[i].Pre0 = yv12Mb[mbmi.RefFrame0, i];
                if (isComp) xd.Plane[i].Pre1 = yv12Mb[mbmi.RefFrame1, i];
            }
            AomRdStats rdStats = default, rdStatsY = default, rdStatsUv = default;
            int modeRate = info.ModeRateArr[dataIdx];
            long skipRd = long.MaxValue;
            int gate = GetTxfmRdGateLevel(cpi.Seq!.EnableMaskedCompound, cpi.Sf.inter_sf.txfm_rd_gate_level, bsize, TX_SEARCH_DEFAULT, false);
            if (gate != 0)
            {
                long currSse = info.SseArr[dataIdx];
                skipRd = AomRd.RdCost(x.Rdmult, modeRate, currSse);
                if (!CheckTxfmEval(x, bsize, st.BestSkipRd[0], skipRd, gate, false)) continue;
            }
            AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, null, bsize, 0, numPlanes - 1, cpi.EnableIntraEdgeFilter);
            if (mbmi.MotionMode == OBMC_CAUSAL)
                AomInterPred.BuildObmcInterPredictorsSb(cm, xd, () => AomEncodeFrame.SetupDstPlanes(cpi, xd, mbmi.Bsize, xd.MiRow, xd.MiCol));
            numTxCands++;
            if (AomInter.HaveNewmvInInterMode(predictionMode)) newmvModeEvaled = true;
            numTxSearchModes[predictionMode - INTER_MODE_START]++;
            long thisYrd;
            if (!AomTxSearch.TxfmSearch(cpi, x, bsize, ref rdStats, ref rdStatsY, ref rdStatsUv, modeRate, st.BestRd)) continue;
            int yRate = rdStats.SkipTxfm != 0 ? mc.SkipTxfmCost[skipCtx * 2 + 1] : rdStatsY.Rate + mc.SkipTxfmCost[skipCtx * 2 + 0];
            thisYrd = AomRd.RdCost(x.Rdmult, yRate + modeRate, rdStatsY.Dist);
            if (cpi.Sf.inter_sf.inter_mode_rd_model_estimation == 1)
                InterModeDataPush(x.TileData!, mbmi.Bsize, rdStats.Sse, rdStats.Dist,
                    rdStatsY.Rate + rdStatsUv.Rate + mc.SkipTxfmCost[skipCtx * 2 + mbmi.SkipTxfm]);
            rdStats.Rdcost = AomRd.RdCost(x.Rdmult, rdStats.Rate, rdStats.Dist);
            int modeEnum = GetPredictionModeIdx(predictionMode, mbmi.RefFrame0, mbmi.RefFrame1);
            AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, rdStats, rdStatsY, rdStatsUv, modeEnum, null, bsize, rdStats.Rdcost,
                cpi.Sf.winner_mode_sf.multi_winner_mode_type, true);
            long bestScaledRd = st.BestRd, thisScaledRd = rdStats.Rdcost;
            IncreaseMotionModeRd(st.BestMbmode, mbmi, ref bestScaledRd, ref thisScaledRd, cpi.Sf.inter_sf.bias_warp_mode_rd_scale_pct,
                cpi.Sf.inter_sf.bias_obmc_mode_rd_scale_pct);
            if (thisScaledRd < bestRdInThisPartition)
            {
                bestRdInThisPartition = rdStats.Rdcost;
                yrd = thisYrd;
            }
            if (thisScaledRd < bestScaledRd)
            {
                UpdateSearchState(st, ref rdCost, ctx, rdStats, rdStatsY, rdStatsUv, modeEnum, x, true);
                st.BestSkipRd[0] = skipRd;
                if (cpi.Sf.inter_sf.inter_mode_txfm_breakout != 0)
                {
                    if (j == 0 && (st.BestMbmode.SkipTxfm != 0 || rdStats.SkipTxfm != 0))
                    {
                        int[] cap = { 2, 3, 5, 7, 9 };
                        int qband = (5 * x.Qindex) >> QINDEX_BITS;
                        numInterModeCands = Math.Min(cap[qband], info.Num);
                    }
                    else if (j == 0 && st.BestMbmode.HasSecondRef)
                    {
                        int aggr = cpi.Sf.inter_sf.inter_mode_txfm_breakout - 1;
                        int[,] capCmp = { { 10, 7, 5, 4 }, { 10, 7, 5, 3 } };
                        int qbandCmp = (4 * x.Qindex) >> QINDEX_BITS;
                        numInterModeCands = Math.Min(capCmp[aggr, qbandCmp], info.Num);
                    }
                }
            }
            if (numTxCands > maxAllowedCands && newmvModeEvaled) break;
        }
    }

    /// <summary>refine_winner_mode_tx.</summary>
    private static void RefineWinnerModeTx(AomComp cpi, AomMacroblock x, ref AomRdStats rdCost, int bsize, AomPickModeContext ctx, ref int bestModeIndex,
        AomMbModeInfo bestMbmode, AomBuf2d[,] yv12Mb, int bestRateY, int bestRateUv, ref int bestSkip2, int winnerModeCount)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var mbmi = xd.Mi0;
        var txfmParams = x.TxfmSearchParams;
        int numPlanes = cm.NumPlanes;
        if (!AomRdoptUtils.IsWinnerModeProcessingEnabled(cpi, x, bestMbmode, rdCost.SkipTxfm != 0)) return;
        AomRdoptUtils.SetModeEvalParams(cpi, x, WINNER_MODE_EVAL);
        if (bestModeIndex == THR_INVALID) return;
        long bestRd = AomRd.RdCost(x.Rdmult, rdCost.Rate, rdCost.Dist);
        var winnerCopy = x.ScratchIntraBestMbmi;
        for (int modeIdx = 0; modeIdx < winnerModeCount; modeIdx++)
        {
            AomMbModeInfo winnerMbmi;
            AomRdStats winnerRdStats;
            int winnerRateY, winnerRateUv, winnerModeIndex;
            if (cpi.Sf.winner_mode_sf.multi_winner_mode_type != 0)
            {
                var ws = x.WinnerModeStats[modeIdx];
                winnerMbmi = ws.Mbmi;
                winnerRdStats = ws.RdCost;
                winnerRateY = ws.RateY;
                winnerRateUv = ws.RateUv;
                winnerModeIndex = ws.ModeIndex;
            }
            else
            {
                winnerMbmi = bestMbmode;
                winnerRdStats = rdCost;
                winnerRateY = bestRateY;
                winnerRateUv = bestRateUv;
                winnerModeIndex = bestModeIndex;
            }
            if (xd.Lossless[winnerMbmi.SegmentId] == 0 && winnerModeIndex != THR_INVALID &&
                AomRdoptUtils.IsWinnerModeProcessingEnabled(cpi, x, winnerMbmi, rdCost.SkipTxfm != 0))
            {
                AomRdStats rdStats = winnerRdStats;
                int skipBlk;
                AomRdStats rdStatsY = default, rdStatsUv = default;
                int skipCtx = AomTxSearch.SkipTxfmContext(xd);
                mbmi.CopyFrom(winnerMbmi);
                SetRefPtrs(cm, xd, mbmi.RefFrame0, mbmi.RefFrame1);
                for (int i = 0; i < numPlanes; i++)
                {
                    xd.Plane[i].Pre0 = yv12Mb[mbmi.RefFrame0, i];
                    if (mbmi.HasSecondRef) xd.Plane[i].Pre1 = yv12Mb[mbmi.RefFrame1, i];
                }
                if (AomInter.IsInterMode(mbmi.Mode))
                {
                    bool built = false;
                    if (cpi.Sf.winner_mode_sf.winner_mode_ifs != 0 && cpi.Mode == REALTIME && cm.ReferenceMode == SINGLE_REFERENCE &&
                        mbmi.MotionMode == SIMPLE_TRANSLATION && !AomInter.IsInterCompoundMode(mbmi.Mode))
                        throw new NotImplementedException("fast_interp_search (real time winner mode)");
                    if (!built) AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, null, bsize, 0, numPlanes - 1, cpi.EnableIntraEdgeFilter);
                    if (mbmi.MotionMode == OBMC_CAUSAL)
                        AomInterPred.BuildObmcInterPredictorsSb(cm, xd, () => AomEncodeFrame.SetupDstPlanes(cpi, xd, mbmi.Bsize, xd.MiRow, xd.MiCol));
                    AomEncodeMb.SubtractPlane(x, bsize, 0);
                    if (txfmParams.TxModeSearchType == TX_MODE_SELECT && xd.Lossless[mbmi.SegmentId] == 0)
                        AomTxSearch.PickRecursiveTxSizeTypeYrd(cpi, x, ref rdStatsY, bsize, long.MaxValue);
                    else
                    {
                        AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref rdStatsY, bsize, long.MaxValue);
                        Array.Fill(mbmi.InterTxSize, (byte)mbmi.TxSize);
                    }
                }
                else AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref rdStatsY, bsize, long.MaxValue);
                if (numPlanes > 1) AomTxSearch.TxfmUvrd(cpi, x, ref rdStatsUv, bsize, long.MaxValue);
                else rdStatsUv.Init();
                bool compPred = mbmi.RefFrame1 > INTRA_FRAME;
                var mc = x.ModeCosts;
                if (AomInter.IsInterMode(mbmi.Mode) && (cpi.Sharpness == 0 || !compPred) &&
                    AomRd.RdCost(x.Rdmult, mc.SkipTxfmCost[skipCtx * 2 + 0] + rdStatsY.Rate + rdStatsUv.Rate, rdStatsY.Dist + rdStatsUv.Dist) >
                    AomRd.RdCost(x.Rdmult, mc.SkipTxfmCost[skipCtx * 2 + 1], rdStatsY.Sse + rdStatsUv.Sse))
                {
                    skipBlk = 1;
                    rdStatsY.Rate = mc.SkipTxfmCost[skipCtx * 2 + 1];
                    rdStatsUv.Rate = 0;
                    rdStatsY.Dist = rdStatsY.Sse;
                    rdStatsUv.Dist = rdStatsUv.Sse;
                }
                else
                {
                    skipBlk = 0;
                    rdStatsY.Rate += mc.SkipTxfmCost[skipCtx * 2 + 0];
                }
                int thisRate = rdStats.Rate + rdStatsY.Rate + rdStatsUv.Rate - winnerRateY - winnerRateUv;
                AomTrace.Out?.Write($"rwm {xd.MiRow} {xd.MiCol} r {rdStats.Rate} y {rdStatsY.Rate} uv {rdStatsUv.Rate} wy {winnerRateY} wuv {winnerRateUv} sc {AomTxSearch.SkipTxfmContext(xd)} this {thisRate}" + (char)10);
                long thisRd = AomRd.RdCost(x.Rdmult, thisRate, rdStatsY.Dist + rdStatsUv.Dist);
                if (bestRd > thisRd)
                {
                    bestMbmode.CopyFrom(mbmi);
                    bestModeIndex = winnerModeIndex;
                    CopyTxTypeMapTo(xd, ctx.TxTypeMap, ctx.NumFourByFourBlk);
                    rdCost.Rate = thisRate;
                    rdCost.Dist = rdStatsY.Dist + rdStatsUv.Dist;
                    rdCost.Sse = rdStatsY.Sse + rdStatsUv.Sse;
                    rdCost.Rdcost = thisRd;
                    bestRd = thisRd;
                    bestSkip2 = skipBlk;
                }
            }
        }
    }
}
