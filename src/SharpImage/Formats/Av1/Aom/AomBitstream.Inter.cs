using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/bitstream.c's inter-frame mode info (pack_inter_mode_mvs, write_ref_frames,
// write_inter_mode, write_drl_idx, write_motion_mode, write_mb_interp_filter, write_intra_prediction_modes for
// non-key frames) and encodemv.c's av1_encode_mv.
internal static partial class AomBitstream
{
    private static readonly int[,] CompoundModeCtxMapW = { { 0, 1, 1, 1, 1 }, { 1, 2, 3, 4, 4 }, { 4, 4, 5, 6, 7 } };

    /// <summary>mode_context_analyzer (the stored mode context of the block's reference type).</summary>
    private static int ModeContextAnalyzerW(int modeContext, int rf1)
    {
        if (rf1 <= INTRA_FRAME) return modeContext;
        int newmvCtx = modeContext & NEWMV_CTX_MASK;
        int refmvCtx = (modeContext >> REFMV_OFFSET) & REFMV_CTX_MASK;
        return CompoundModeCtxMapW[refmvCtx >> 1, Math.Min(newmvCtx, COMP_NEWMV_CTXS - 1)];
    }

    /// <summary>get_ref_mv_from_stack / get_ref_mv on the block's MB_MODE_INFO_EXT_FRAME.</summary>
    private static AomMv GetRefMvW(AomMbModeInfo mbmi, AomMbmiExtFrameInter ext, int refIdx)
    {
        int refMvIdx = mbmi.RefMvIdx;
        if (mbmi.Mode == NEAR_NEWMV || mbmi.Mode == NEW_NEARMV) refMvIdx += 1;
        if (mbmi.RefFrame1 > INTRA_FRAME) return refIdx != 0 ? ext.RefMvStack[refMvIdx].CompMv : ext.RefMvStack[refMvIdx].ThisMv;
        int rft = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
        return refMvIdx < ext.RefMvCount ? ext.RefMvStack[refMvIdx].ThisMv : ext.GlobalMvs[rft];
    }

    /// <summary>write_mbmi_b for an inter frame: set_ref_ptrs then pack_inter_mode_mvs.</summary>
    private static void PackInterModeMvs(TileWriter t)
    {
        var cpi = t.Cpi;
        var cm = t.Cm;
        var xd = t.Xd;
        var w = t.W;
        var m = t.Fc.Mode;
        var mbmi = xd.Mi0;
        AomRdoptInter.SetRefPtrs(cm, xd, mbmi.RefFrame0, mbmi.RefFrame1);
        var ext = cpi.MbmiExtFrameInterBase![xd.MiRow * cm.MiStride + xd.MiCol]!;
        int mode = mbmi.Mode;
        int bsize = mbmi.Bsize;
        bool isInter = AomEncodeMb.IsInterBlock(mbmi);
        bool isCompound = mbmi.HasSecondRef;

        // write_skip_mode
        if (cm.SkipModeFlag && AomInter.IsCompRefAllowed(bsize))
            w.WriteSymbol(mbmi.SkipMode, m.SkipMode[AomPredCommon.SkipMode(xd)], 2);
        int skip;
        if (mbmi.SkipMode != 0) skip = 1;
        else
        {
            skip = mbmi.SkipTxfm;
            w.WriteSymbol(skip, m.Skip[AomTxSearch.SkipTxfmContext(xd)], 2);
        }
        WriteCdef(t, skip);
        WriteDeltaQParams(t, skip);
        if (mbmi.SkipMode == 0) w.WriteSymbol(isInter ? 1 : 0, m.Intra[AomPredCommon.IntraInter(xd)], 2);
        if (mbmi.SkipMode != 0) return;

        if (!isInter)
        {
            WriteIntraPredictionModesNonKf(t);
            return;
        }
        AomRdoptInter.CollectNeighborsRefCountsPublic(xd);
        WriteRefFrames(t);
        int modeCtx = ModeContextAnalyzerW(ext.ModeContext, mbmi.RefFrame1);
        if (mode >= NEAREST_NEARESTMV)   // is_inter_compound_mode
            w.WriteSymbol(mode - NEAREST_NEARESTMV, m.CompInterMode[modeCtx], INTER_COMPOUND_MODES);
        else
        {
            // write_inter_mode
            int newmvCtx = modeCtx & NEWMV_CTX_MASK;
            w.WriteSymbol(mode != NEWMV ? 1 : 0, m.NewmvMode[newmvCtx], 2);
            if (mode != NEWMV)
            {
                int zeromvCtx = (modeCtx >> GLOBALMV_OFFSET) & GLOBALMV_CTX_MASK;
                w.WriteSymbol(mode != GLOBALMV ? 1 : 0, m.GlobalmvMode[zeromvCtx], 2);
                if (mode != GLOBALMV)
                {
                    int refmvCtx = (modeCtx >> REFMV_OFFSET) & REFMV_CTX_MASK;
                    w.WriteSymbol(mode != NEARESTMV ? 1 : 0, m.RefmvMode[refmvCtx], 2);
                }
            }
        }
        if (mode == NEWMV || mode == NEW_NEWMV || AomInter.HaveNearmvInInterMode(mode)) WriteDrlIdx(t, mbmi, ext);

        bool allowHp = cm.AllowHighPrecisionMv;
        if (mode == NEWMV || mode == NEW_NEWMV)
        {
            for (int r = 0; r < 1 + (isCompound ? 1 : 0); ++r)
                EncodeMv(t, r == 0 ? mbmi.Mv0 : mbmi.Mv1, GetRefMvW(mbmi, ext, r), allowHp);
        }
        else if (mode == NEAREST_NEWMV || mode == NEAR_NEWMV) EncodeMv(t, mbmi.Mv1, GetRefMvW(mbmi, ext, 1), allowHp);
        else if (mode == NEW_NEARESTMV || mode == NEW_NEARMV) EncodeMv(t, mbmi.Mv0, GetRefMvW(mbmi, ext, 0), allowHp);

        if (cm.ReferenceMode != COMPOUND_REFERENCE && cpi.Seq!.EnableInterintraCompound && AomInter.IsInterintraAllowed(mbmi))
        {
            int interintra = mbmi.RefFrame1 == INTRA_FRAME ? 1 : 0;
            int g = SizeGroupLookup[bsize];
            w.WriteSymbol(interintra, m.Interintra[g], 2);
            if (interintra != 0)
            {
                w.WriteSymbol(mbmi.InterintraMode, m.InterintraMode[g], INTERINTRA_MODES);
                if (AomInterPred.IsWedgeUsed(bsize))
                {
                    int wc = AomModeCostFill.WedgeCtx[bsize];
                    w.WriteSymbol(mbmi.UseWedgeInterintra, m.InterintraWedge[wc], 2);
                    if (mbmi.UseWedgeInterintra != 0) w.WriteSymbol(mbmi.InterintraWedgeIndex, m.WedgeIdx[wc], 16);
                }
            }
        }

        if (mbmi.RefFrame1 != INTRA_FRAME)
        {
            // write_motion_mode
            int last = cm.SwitchableMotionMode ? AomRdoptInter.MotionModeAllowed(xd.GlobalMotion, xd, mbmi, cm.AllowWarpedMotion) : SIMPLE_TRANSLATION;
            int db = AomModeCostFill.LibaomToDav1dBs[bsize];
            if (last == OBMC_CAUSAL) w.WriteSymbol(mbmi.MotionMode == OBMC_CAUSAL ? 1 : 0, m.Obmc[db], 2);
            else if (last == WARPED_CAUSAL) w.WriteSymbol(mbmi.MotionMode, m.MotionMode[db], MOTION_MODES);
        }

        if (isCompound)
        {
            bool maskedCompoundUsed = AomInter.IsAnyMaskedCompoundUsed(bsize) && cpi.Seq!.EnableMaskedCompound;
            if (maskedCompoundUsed) w.WriteSymbol(mbmi.CompGroupIdx, m.MaskComp[AomPredCommon.CompGroupIdx(xd)], 2);
            if (mbmi.CompGroupIdx == 0)
            {
                if (cpi.Seq!.EnableDistWtdComp) w.WriteSymbol(mbmi.CompoundIdx, m.JntComp[AomPredCommon.CompIndex(cm, xd)], 2);
            }
            else
            {
                if (AomInter.IsInterinterCompoundUsed(COMPOUND_WEDGE, bsize))
                    w.WriteSymbol(mbmi.InterinterComp.Type - COMPOUND_WEDGE, m.WedgeComp[AomModeCostFill.WedgeCtx[bsize]], MASKED_COMPOUND_TYPES);
                if (mbmi.InterinterComp.Type == COMPOUND_WEDGE)
                {
                    w.WriteSymbol(mbmi.InterinterComp.WedgeIndex, m.WedgeIdx[AomModeCostFill.WedgeCtx[bsize]], 16);
                    w.WriteBit(mbmi.InterinterComp.WedgeSign);
                }
                else w.WriteLiteral(mbmi.InterinterComp.MaskType, 1);   // MAX_DIFFWTD_MASK_BITS
            }
        }

        // write_mb_interp_filter
        if (AomInterpSearch.IsInterpNeeded(xd) && cm.InterpFilter == SWITCHABLE)
            for (int dir = 0; dir < 2; ++dir)
            {
                int ctx = AomPredCommon.SwitchableInterp(xd, dir);
                int filter = AomPredCommon.ExtractInterpFilter(mbmi.InterpFilters, dir);
                w.WriteSymbol(filter, m.Filter[ctx], SWITCHABLE_FILTERS);
                if (!cpi.Seq!.EnableDualFilter) break;
            }
    }

    /// <summary>write_drl_idx.</summary>
    private static void WriteDrlIdx(TileWriter t, AomMbModeInfo mbmi, AomMbmiExtFrameInter ext)
    {
        var m = t.Fc.Mode;
        bool newMv = mbmi.Mode == NEWMV || mbmi.Mode == NEW_NEWMV;
        if (newMv)
        {
            for (int idx = 0; idx < 2; ++idx)
                if (ext.RefMvCount > idx + 1)
                {
                    int drlCtx = AomInter.DrlCtx(ext.Weight, idx);
                    t.W.WriteSymbol(mbmi.RefMvIdx != idx ? 1 : 0, m.DrlBit[drlCtx], 2);
                    if (mbmi.RefMvIdx == idx) return;
                }
            return;
        }
        if (AomInter.HaveNearmvInInterMode(mbmi.Mode))
            for (int idx = 1; idx < 3; ++idx)
                if (ext.RefMvCount > idx + 1)
                {
                    int drlCtx = AomInter.DrlCtx(ext.Weight, idx);
                    t.W.WriteSymbol(mbmi.RefMvIdx != idx - 1 ? 1 : 0, m.DrlBit[drlCtx], 2);
                    if (mbmi.RefMvIdx == idx - 1) return;
                }
    }

    /// <summary>write_ref_frames (no segmentation).</summary>
    private static void WriteRefFrames(TileWriter t)
    {
        var cm = t.Cm;
        var xd = t.Xd;
        var w = t.W;
        var m = t.Fc.Mode;
        var mbmi = xd.Mi0;
        int ref0 = mbmi.RefFrame0, ref1 = mbmi.RefFrame1;
        bool isCompound = mbmi.HasSecondRef;
        if (cm.ReferenceMode == REFERENCE_MODE_SELECT && AomInter.IsCompRefAllowed(mbmi.Bsize))
            w.WriteSymbol(isCompound ? 1 : 0, m.Comp[AomPredCommon.ReferenceMode(xd)], 2);
        if (isCompound)
        {
            bool uni = AomInter.HasUniCompRefs(mbmi);
            w.WriteSymbol(uni ? 0 : 1, m.CompDir[AomPredCommon.CompReferenceType(xd)], 2);
            if (uni)
            {
                int bit = ref0 == BWDREF_FRAME ? 1 : 0;
                w.WriteSymbol(bit, m.CompUniRef[0 * 3 + AomPredCommon.UniCompRefP(xd)], 2);
                if (bit == 0)
                {
                    int bit1 = ref1 == LAST3_FRAME || ref1 == GOLDEN_FRAME ? 1 : 0;
                    w.WriteSymbol(bit1, m.CompUniRef[1 * 3 + AomPredCommon.UniCompRefP1(xd)], 2);
                    if (bit1 != 0) w.WriteSymbol(ref1 == GOLDEN_FRAME ? 1 : 0, m.CompUniRef[2 * 3 + AomPredCommon.UniCompRefP2(xd)], 2);
                }
                return;
            }
            int b = ref0 == GOLDEN_FRAME || ref0 == LAST3_FRAME ? 1 : 0;
            w.WriteSymbol(b, m.CompFwdRef[0 * 3 + AomPredCommon.CompRefP(xd)], 2);
            if (b == 0) w.WriteSymbol(ref0 == LAST2_FRAME ? 1 : 0, m.CompFwdRef[1 * 3 + AomPredCommon.CompRefP1(xd)], 2);
            else w.WriteSymbol(ref0 == GOLDEN_FRAME ? 1 : 0, m.CompFwdRef[2 * 3 + AomPredCommon.CompRefP2(xd)], 2);
            int bitBwd = ref1 == ALTREF_FRAME ? 1 : 0;
            w.WriteSymbol(bitBwd, m.CompBwdRef[0 * 3 + AomPredCommon.CompBwdrefP(xd)], 2);
            if (bitBwd == 0) w.WriteSymbol(ref1 == ALTREF2_FRAME ? 1 : 0, m.CompBwdRef[1 * 3 + AomPredCommon.CompBwdrefP1(xd)], 2);
            return;
        }
        int bit0 = ref0 <= ALTREF_FRAME && ref0 >= BWDREF_FRAME ? 1 : 0;
        w.WriteSymbol(bit0, m.Ref[0 * 3 + AomPredCommon.SingleRefP1(xd)], 2);
        if (bit0 != 0)
        {
            int bit1 = ref0 == ALTREF_FRAME ? 1 : 0;
            w.WriteSymbol(bit1, m.Ref[1 * 3 + AomPredCommon.SingleRefP2(xd)], 2);
            if (bit1 == 0) w.WriteSymbol(ref0 == ALTREF2_FRAME ? 1 : 0, m.Ref[5 * 3 + AomPredCommon.SingleRefP6(xd)], 2);
        }
        else
        {
            int bit2 = ref0 == LAST3_FRAME || ref0 == GOLDEN_FRAME ? 1 : 0;
            w.WriteSymbol(bit2, m.Ref[2 * 3 + AomPredCommon.SingleRefP3(xd)], 2);
            if (bit2 == 0) w.WriteSymbol(ref0 != LAST_FRAME ? 1 : 0, m.Ref[3 * 3 + AomPredCommon.SingleRefP4(xd)], 2);
            else w.WriteSymbol(ref0 != LAST3_FRAME ? 1 : 0, m.Ref[4 * 3 + AomPredCommon.SingleRefP5(xd)], 2);
        }
    }

    /// <summary>av1_encode_mv (with the auto_mv_step_size max magnitude tracking).</summary>
    private static void EncodeMv(TileWriter t, AomMv mv, AomMv refMv, bool allowHp)
    {
        var diff = new AomMv(mv.Row - refMv.Row, mv.Col - refMv.Col);
        int j = AomMvCost.GetMvJoint(diff);
        int precision = t.Cm.CurFrameForceIntegerMv ? AomMvCost.MV_SUBPEL_NONE : (allowHp ? AomMvCost.MV_SUBPEL_HIGH_PRECISION : AomMvCost.MV_SUBPEL_LOW_PRECISION);
        var mvctx = t.Fc.Mv;
        t.W.WriteSymbol(j, mvctx.Joint, AomMvCost.MvJoints);
        if (AomMvCost.MvJointVertical(j)) EncodeMvComponent(t.W, diff.Row, mvctx.Comp0, precision);
        if (AomMvCost.MvJointHorizontal(j)) EncodeMvComponent(t.W, diff.Col, mvctx.Comp1, precision);
        if (t.Cpi.Sf.mv_sf.auto_mv_step_size != 0)
        {
            int maxv = Math.Max(Math.Abs((int)mv.Row), Math.Abs((int)mv.Col)) >> 3;
            t.MaxMvMagnitude = Math.Max(maxv, t.MaxMvMagnitude);
        }
    }

    /// <summary>encode_mv_component with the fractional / high precision bits.</summary>
    private static void EncodeMvComponent(AomWriter w, int comp, Av1CdfMvComponent mvcomp, int precision)
    {
        int sign = comp < 0 ? 1 : 0;
        int mag = sign != 0 ? -comp : comp;
        int mvClass = AomMvCost.GetMvClass(mag - 1, out int offset);
        int d = offset >> 3, fr = (offset >> 1) & 3, hp = offset & 1;
        w.WriteSymbol(sign, mvcomp.Sign, 2);
        w.WriteSymbol(mvClass, mvcomp.Classes, AomMvCost.MvClasses);
        if (mvClass == 0) w.WriteSymbol(d, mvcomp.Class0, AomMvCost.Class0Size);
        else
        {
            int n = mvClass + AomMvCost.Class0Bits - 1;
            for (int i = 0; i < n; ++i) w.WriteSymbol((d >> i) & 1, mvcomp.ClassN[i], 2);
        }
        if (precision > AomMvCost.MV_SUBPEL_NONE) w.WriteSymbol(fr, mvClass == 0 ? mvcomp.Class0Fp[d] : mvcomp.ClassNFp, AomMvCost.MvFpSize);
        if (precision > AomMvCost.MV_SUBPEL_LOW_PRECISION) w.WriteSymbol(hp, mvClass == 0 ? mvcomp.Class0Hp : mvcomp.ClassNHp, 2);
    }

    /// <summary>write_intra_prediction_modes for a non-key frame (y mode by size group).</summary>
    private static void WriteIntraPredictionModesNonKf(TileWriter t) => WriteIntraPredictionModes(t, false);
}
