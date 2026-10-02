using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>pred_common.c / pred_common.h: the inter-frame symbol contexts.</summary>
internal static class AomPredCommon
{
    private static bool IsBackward(int rf) => rf >= BWDREF_FRAME && rf <= ALTREF_FRAME;

    public static int ExtractInterpFilter(uint filters, int dir) => dir != 0 ? (int)(filters >> 16) : (int)(filters & 0xffff);

    private static int GetRefFilterType(AomMbModeInfo r, int dir, int refFrame) =>
        r.RefFrame0 == refFrame || r.RefFrame1 == refFrame ? ExtractInterpFilter(r.InterpFilters, dir & 1) : SWITCHABLE_FILTERS;

    public static int SwitchableInterp(AomMacroblockD xd, int dir)
    {
        var mbmi = xd.Mi0;
        int ctxOffset = (mbmi.RefFrame1 > INTRA_FRAME ? 1 : 0) * INTER_FILTER_COMP_OFFSET;
        int refFrame = mbmi.RefFrame0;
        int ctx = ctxOffset + (dir & 1) * INTER_FILTER_DIR_OFFSET;
        int left = SWITCHABLE_FILTERS, above = SWITCHABLE_FILTERS;
        if (xd.LeftAvailable) left = GetRefFilterType(xd.MiAt(0, -1)!, dir, refFrame);
        if (xd.UpAvailable) above = GetRefFilterType(xd.MiAt(-1, 0)!, dir, refFrame);
        if (left == above) ctx += left;
        else if (left == SWITCHABLE_FILTERS) ctx += above;
        else if (above == SWITCHABLE_FILTERS) ctx += left;
        else ctx += SWITCHABLE_FILTERS;
        return ctx;
    }

    public static int IntraInter(AomMacroblockD xd)
    {
        bool ha = xd.UpAvailable, hl = xd.LeftAvailable;
        if (ha && hl)
        {
            bool ai = !xd.AboveMbmi!.IsInterBlock, li = !xd.LeftMbmi!.IsInterBlock;
            return li && ai ? 3 : (li || ai ? 1 : 0);
        }
        if (ha || hl) return 2 * ((ha ? xd.AboveMbmi! : xd.LeftMbmi!).IsInterBlock ? 0 : 1);
        return 0;
    }

    public static int ReferenceMode(AomMacroblockD xd)
    {
        var a = xd.AboveMbmi; var l = xd.LeftMbmi;
        bool ha = xd.UpAvailable, hl = xd.LeftAvailable;
        if (ha && hl)
        {
            if (!a!.HasSecondRef && !l!.HasSecondRef) return (IsBackward(a.RefFrame0) ? 1 : 0) ^ (IsBackward(l.RefFrame0) ? 1 : 0);
            if (!a.HasSecondRef) return 2 + (IsBackward(a.RefFrame0) || !a.IsInterBlock ? 1 : 0);
            if (!l!.HasSecondRef) return 2 + (IsBackward(l.RefFrame0) || !l.IsInterBlock ? 1 : 0);
            return 4;
        }
        if (ha || hl)
        {
            var e = ha ? a! : l!;
            return !e.HasSecondRef ? (IsBackward(e.RefFrame0) ? 1 : 0) : 3;
        }
        return 1;
    }

    public static int CompReferenceType(AomMacroblockD xd)
    {
        var a = xd.AboveMbmi; var l = xd.LeftMbmi;
        bool ai_ = xd.UpAvailable, li_ = xd.LeftAvailable;
        if (ai_ && li_)
        {
            bool aIntra = !a!.IsInterBlock, lIntra = !l!.IsInterBlock;
            if (aIntra && lIntra) return 2;
            if (aIntra || lIntra)
            {
                var im = aIntra ? l : a;
                return !im.HasSecondRef ? 2 : 1 + 2 * (AomInter.HasUniCompRefs(im) ? 1 : 0);
            }
            bool aSg = !a.HasSecondRef, lSg = !l.HasSecondRef;
            int frfa = a.RefFrame0, frfl = l.RefFrame0;
            if (aSg && lSg) return 1 + 2 * (!(IsBackward(frfa) ^ IsBackward(frfl)) ? 1 : 0);
            if (lSg || aSg)
            {
                bool uni = aSg ? AomInter.HasUniCompRefs(l) : AomInter.HasUniCompRefs(a);
                return !uni ? 1 : 3 + (!(IsBackward(frfa) ^ IsBackward(frfl)) ? 1 : 0);
            }
            bool au = AomInter.HasUniCompRefs(a), lu = AomInter.HasUniCompRefs(l);
            if (!au && !lu) return 0;
            if (!au || !lu) return 2;
            return 3 + (!((frfa == BWDREF_FRAME) ^ (frfl == BWDREF_FRAME)) ? 1 : 0);
        }
        if (ai_ || li_)
        {
            var e = ai_ ? a! : l!;
            if (!e.IsInterBlock) return 2;
            return !e.HasSecondRef ? 2 : 4 * (AomInter.HasUniCompRefs(e) ? 1 : 0);
        }
        return 2;
    }

    private static int Cmp3(int x, int y) => x == y ? 1 : (x < y ? 0 : 2);

    public static int UniCompRefP(AomMacroblockD xd)
    {
        var c = xd.NeighborsRefCounts;
        return Cmp3(c[LAST_FRAME] + c[LAST2_FRAME] + c[LAST3_FRAME] + c[GOLDEN_FRAME], c[BWDREF_FRAME] + c[ALTREF2_FRAME] + c[ALTREF_FRAME]);
    }
    public static int UniCompRefP1(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[LAST2_FRAME], c[LAST3_FRAME] + c[GOLDEN_FRAME]); }
    public static int UniCompRefP2(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[LAST3_FRAME], c[GOLDEN_FRAME]); }
    private static int Ll2OrL3gld(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[LAST_FRAME] + c[LAST2_FRAME], c[LAST3_FRAME] + c[GOLDEN_FRAME]); }
    private static int LastOrLast2(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[LAST_FRAME], c[LAST2_FRAME]); }
    private static int Last3OrGld(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[LAST3_FRAME], c[GOLDEN_FRAME]); }
    private static int Brfarf2OrArf(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[BWDREF_FRAME] + c[ALTREF2_FRAME], c[ALTREF_FRAME]); }
    private static int BrfOrArf2(AomMacroblockD xd) { var c = xd.NeighborsRefCounts; return Cmp3(c[BWDREF_FRAME], c[ALTREF2_FRAME]); }

    public static int CompRefP(AomMacroblockD xd) => Ll2OrL3gld(xd);
    public static int CompRefP1(AomMacroblockD xd) => LastOrLast2(xd);
    public static int CompRefP2(AomMacroblockD xd) => Last3OrGld(xd);
    public static int CompBwdrefP(AomMacroblockD xd) => Brfarf2OrArf(xd);
    public static int CompBwdrefP1(AomMacroblockD xd) => BrfOrArf2(xd);
    public static int SingleRefP1(AomMacroblockD xd) => UniCompRefP(xd);
    public static int SingleRefP2(AomMacroblockD xd) => Brfarf2OrArf(xd);
    public static int SingleRefP3(AomMacroblockD xd) => Ll2OrL3gld(xd);
    public static int SingleRefP4(AomMacroblockD xd) => LastOrLast2(xd);
    public static int SingleRefP5(AomMacroblockD xd) => Last3OrGld(xd);
    public static int SingleRefP6(AomMacroblockD xd) => BrfOrArf2(xd);

    /// <summary>get_comp_index_context.</summary>
    public static int CompIndex(AomCommon cm, AomMacroblockD xd)
    {
        var mbmi = xd.Mi0;
        var bck = (mbmi.RefFrame0 > INTRA_FRAME ? cm.RefBufs[mbmi.RefFrame0] : null);
        var fwd = (mbmi.RefFrame1 > INTRA_FRAME ? cm.RefBufs[mbmi.RefFrame1] : null);
        int bckIdx = bck?.OrderHint ?? 0, fwdIdx = fwd?.OrderHint ?? 0;
        int cur = cm.OrderHint;
        int f = Math.Abs(cm.RelativeDist(fwdIdx, cur));
        int b = Math.Abs(cm.RelativeDist(cur, bckIdx));
        int aCtx = 0, lCtx = 0;
        int offset = f == b ? 1 : 0;
        var a = xd.AboveMbmi; var l = xd.LeftMbmi;
        if (a != null) { if (a.HasSecondRef) aCtx = a.CompoundIdx; else if (a.RefFrame0 == ALTREF_FRAME) aCtx = 1; }
        if (l != null) { if (l.HasSecondRef) lCtx = l.CompoundIdx; else if (l.RefFrame0 == ALTREF_FRAME) lCtx = 1; }
        return aCtx + lCtx + 3 * offset;
    }

    /// <summary>get_comp_group_idx_context.</summary>
    public static int CompGroupIdx(AomMacroblockD xd)
    {
        int aCtx = 0, lCtx = 0;
        var a = xd.AboveMbmi; var l = xd.LeftMbmi;
        if (a != null) { if (a.HasSecondRef) aCtx = a.CompGroupIdx; else if (a.RefFrame0 == ALTREF_FRAME) aCtx = 3; }
        if (l != null) { if (l.HasSecondRef) lCtx = l.CompGroupIdx; else if (l.RefFrame0 == ALTREF_FRAME) lCtx = 3; }
        return Math.Min(5, aCtx + lCtx);
    }

    public static int SkipMode(AomMacroblockD xd) => (xd.AboveMbmi?.SkipMode ?? 0) + (xd.LeftMbmi?.SkipMode ?? 0);
}
