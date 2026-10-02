using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/mcomp.c's OBMC motion search (av1_obmc_full_pixel_search with
// obmc_full_pixel_diamond / obmc_refining_search_sad, av1_find_best_obmc_sub_pixel_tree_up) and aom_dsp's
// aom_obmc_sad / aom_obmc_variance / aom_obmc_sub_pixel_variance (and the high bit depth variants).
internal static class AomObmcSearch
{
    private static int Rp2Signed(int v, int n) => v < 0 ? -((-v + (1 << (n - 1))) >> n) : (v + (1 << (n - 1))) >> n;

    /// <summary>aom_obmc_sad / aom_highbd_obmc_sad.</summary>
    public static uint ObmcSad(byte[]? pre8, ushort[]? pre16, int preOff, int preStride, int[] wsrc, int[] mask, int w, int h)
    {
        uint sad = 0;
        for (int y = 0; y < h; y++)
        {
            int po = preOff + y * preStride, wo = y * w;
            for (int x = 0; x < w; x++)
            {
                int pv = pre16 != null ? pre16[po + x] : pre8![po + x];
                sad += (uint)((Math.Abs(wsrc[wo + x] - pv * mask[wo + x]) + (1 << 11)) >> 12);
            }
        }
        return sad;
    }

    /// <summary>aom_obmc_variance / aom_highbd_{bd}_obmc_variance.</summary>
    public static uint ObmcVariance(byte[]? pre8, ushort[]? pre16, int preOff, int preStride, int[] wsrc, int[] mask, int w, int h, int bd,
        out uint sse)
    {
        long sum = 0;
        ulong ss = 0;
        for (int y = 0; y < h; y++)
        {
            int po = preOff + y * preStride, wo = y * w;
            for (int x = 0; x < w; x++)
            {
                int pv = pre16 != null ? pre16[po + x] : pre8![po + x];
                int diff = Rp2Signed(wsrc[wo + x] - pv * mask[wo + x], 12);
                sum += diff;
                ss += (ulong)((long)diff * diff);
            }
        }
        if (pre16 == null || bd == 8)
        {
            sse = (uint)ss;
            int isum = (int)sum;
            if (pre16 == null) return sse - (uint)(((long)isum * isum) / (w * h));
            return sse - (uint)(((long)isum * isum) / (w * h));
        }
        int sh = bd == 10 ? 2 : 4;
        int rsum = (int)((sum + (1L << (sh - 1))) >> sh);
        sse = (uint)((ss + (1UL << (2 * sh - 1))) >> (2 * sh));
        long var = (long)sse - ((long)rsum * rsum) / (w * h);
        return var >= 0 ? (uint)var : 0;
    }

    private static uint Osdf(AomFullPelMsParams p, AomMacroblock x, AomMv mv)
    {
        int off = p.Ref.Offset + mv.Row * p.Ref.Stride + mv.Col;
        uint sad = ObmcSad(p.Ref.Buf, p.Ref.Buf16, off, p.Ref.Stride, x.ObmcBuffer.Wsrc, x.ObmcBuffer.Mask, BlockSizeWide[p.Bsize], BlockSizeHigh[p.Bsize]);
        return sad;
    }

    private static int MvsadErrCost(AomFullPelMsParams p, AomMv mv)
        => p.MvCost == null ? 0 : AomMvCost.MvSadErrCost(mv, p.FullRefMv, p.MvJCost!, p.MvCost, p.SadPerBit);

    /// <summary>get_obmc_mvpred_var.</summary>
    private static int GetObmcMvpredVar(AomFullPelMsParams p, AomMacroblock x, AomMv mv)
    {
        int off = p.Ref.Offset + mv.Row * p.Ref.Stride + mv.Col;
        uint v = ObmcVariance(p.Ref.Buf, p.Ref.Buf16, off, p.Ref.Stride, x.ObmcBuffer.Wsrc, x.ObmcBuffer.Mask, BlockSizeWide[p.Bsize],
            BlockSizeHigh[p.Bsize], p.Bd, out _);
        return (int)v + AomMcomp.MvErrCost(p, mv.ToMv());
    }

    private static AomMv ClampFullmv(AomMv mv, in AomFullMvLimits l)
        => new(Math.Clamp((int)mv.Row, l.RowMin, l.RowMax), Math.Clamp((int)mv.Col, l.ColMin, l.ColMax));

    /// <summary>obmc_refining_search_sad.</summary>
    private static int ObmcRefiningSearchSad(AomFullPelMsParams p, AomMacroblock x, ref AomMv bestMv)
    {
        ReadOnlySpan<int> nr = stackalloc int[] { -1, 0, 0, 1 };
        ReadOnlySpan<int> nc = stackalloc int[] { 0, -1, 1, 0 };
        uint bestSad = Osdf(p, x, bestMv) + (uint)MvsadErrCost(p, bestMv);
        for (int i = 0; i < 8; i++)
        {
            int bestSite = -1;
            for (int j = 0; j < 4; j++)
            {
                var mv = new AomMv(bestMv.Row + nr[j], bestMv.Col + nc[j]);
                if (AomMcomp.IsFullmvInRange(p.MvLimits, mv))
                {
                    uint sad = Osdf(p, x, mv);
                    if (sad < bestSad)
                    {
                        sad += (uint)MvsadErrCost(p, mv);
                        if (sad < bestSad) { bestSad = sad; bestSite = j; }
                    }
                }
            }
            if (bestSite == -1) break;
            bestMv = new AomMv(bestMv.Row + nr[bestSite], bestMv.Col + nc[bestSite]);
        }
        return (int)bestSad;
    }

    /// <summary>obmc_diamond_search_sad.</summary>
    private static int ObmcDiamondSearchSad(AomFullPelMsParams p, AomMacroblock x, AomMv startMv, out AomMv bestMv, int searchStep, out int num00)
    {
        var cfg = p.SearchSites;
        int totSteps = cfg.NumSearchSteps - searchStep;
        startMv = ClampFullmv(startMv, p.MvLimits);
        var init = startMv;
        num00 = 0;
        bestMv = startMv;
        int bestSad = (int)(Osdf(p, x, bestMv) + (uint)MvsadErrCost(p, bestMv));
        for (int step = totSteps - 1; step >= 0; --step)
        {
            int bestSite = 0;
            for (int idx = 1; idx <= cfg.SearchesPerStep[step]; ++idx)
            {
                var s = cfg.Site[step, idx];
                var mv = new AomMv(bestMv.Row + s.Row, bestMv.Col + s.Col);
                if (AomMcomp.IsFullmvInRange(p.MvLimits, mv))
                {
                    int sad = (int)Osdf(p, x, mv);
                    if (sad < bestSad)
                    {
                        sad += MvsadErrCost(p, mv);
                        if (sad < bestSad) { bestSad = sad; bestSite = idx; }
                    }
                }
            }
            if (bestSite != 0)
            {
                var s = cfg.Site[step, bestSite];
                bestMv = new AomMv(bestMv.Row + s.Row, bestMv.Col + s.Col);
            }
            else if (bestMv.Equals(init)) num00++;
        }
        return bestSad;
    }

    /// <summary>obmc_full_pixel_diamond.</summary>
    private static int ObmcFullPixelDiamond(AomFullPelMsParams p, AomMacroblock x, AomMv startMv, int stepParam, out AomMv bestMv)
    {
        var cfg = p.SearchSites;
        int num00 = 0;
        int bestsme = ObmcDiamondSearchSad(p, x, startMv, out var tmpMv, stepParam, out int n);
        if (bestsme < int.MaxValue) bestsme = GetObmcMvpredVar(p, x, tmpMv);
        bestMv = tmpMv;
        int furtherSteps = cfg.NumSearchSteps - 1 - stepParam;
        while (n < furtherSteps)
        {
            ++n;
            if (num00 != 0) num00--;
            else
            {
                int thissme = ObmcDiamondSearchSad(p, x, startMv, out tmpMv, stepParam + n, out num00);
                if (thissme < int.MaxValue) thissme = GetObmcMvpredVar(p, x, tmpMv);
                if (thissme < bestsme) { bestsme = thissme; bestMv = tmpMv; }
            }
        }
        return bestsme;
    }

    /// <summary>av1_obmc_full_pixel_search.</summary>
    public static int ObmcFullPixelSearch(AomMv startMv, AomFullPelMsParams p, int stepParam, AomMacroblock x, out AomMv bestMv)
    {
        if (p.FastObmcSearch == 0) return ObmcFullPixelDiamond(p, x, startMv, stepParam, out bestMv);
        bestMv = ClampFullmv(startMv, p.MvLimits);
        int thissme = ObmcRefiningSearchSad(p, x, ref bestMv);
        if (thissme < int.MaxValue) thissme = GetObmcMvpredVar(p, x, bestMv);
        return thissme;
    }

    // ---- sub-pixel ----

    private sealed class State
    {
        public uint BestErr;
        public uint Sse1;
        public int Distortion;
    }

    private static int MvErrCost(AomSubpelMsParams p, AomMv mv)
        => AomMcomp.MvErrCost(p.MvCostType, mv, p.RefMv, p.MvJCost, p.MvCost, p.ErrorPerBit);

    /// <summary>estimate_obmc_mvcost (MV_COST_ENTROPY).</summary>
    private static int EstimateObmcMvcost(AomSubpelMsParams p, AomMv mv)
    {
        if (p.MvCostType != AomMcomp.MV_COST_ENTROPY) return 0;
        var diff = new AomMv((mv.Row - p.RefMv.Row) * 8, (mv.Col - p.RefMv.Col) * 8);
        return (int)(((uint)AomMvCost.MvCostOf(diff, p.MvJCost!, p.MvCost!) * (uint)p.ErrorPerBit + 4096) >> 13);
    }

    private static int EstimateObmcPrefError(AomSubpelMsParams p, AomMacroblock x, AomMv mv, out uint sse)
    {
        int w = p.W, h = p.H;
        ref var r = ref p.Ref;
        int refOff = r.Offset + (mv.Row >> 3) * r.Stride + (mv.Col >> 3);
        // aom_obmc_sub_pixel_variance: the bilinear 2-pass filter then the obmc variance
        AomSubpel.BilinearFilter(p, refOff, mv.Col & 7, mv.Row & 7);
        bool hbd = r.Buf16 != null;
        return (int)ObmcVariance(hbd ? null : p.Pred, hbd ? p.Pred16 : null, 0, w, x.ObmcBuffer.Wsrc, x.ObmcBuffer.Mask, w, h, p.Bd, out sse);
    }

    private static int UpsampledObmcPrefError(AomSubpelMsParams p, AomMacroblock x, AomMv mv, out uint sse)
    {
        int w = p.W, h = p.H;
        AomSubpel.UpsampledPred(p, mv, mv.Col & 7, mv.Row & 7, p.Ref.Offset + (mv.Row >> 3) * p.Ref.Stride + (mv.Col >> 3));
        bool hbd = p.Ref.Buf16 != null;
        return (int)ObmcVariance(hbd ? null : p.Pred, hbd ? p.Pred16 : null, 0, w, x.ObmcBuffer.Wsrc, x.ObmcBuffer.Mask, w, h, p.Bd, out sse);
    }

    private static uint ObmcCheckBetterFast(AomSubpelMsParams p, AomMacroblock x, AomMv thisMv, ref AomMv bestMv, State st, ref bool hasBetter)
    {
        uint cost;
        if (AomSubpel.IsSubpelmvInRange(p.MvLimits, thisMv))
        {
            int thismse = EstimateObmcPrefError(p, x, thisMv, out uint sse);
            cost = (uint)EstimateObmcMvcost(p, thisMv);
            cost += (uint)thismse;
            if (cost < st.BestErr)
            {
                st.BestErr = cost; bestMv = thisMv; st.Distortion = thismse; st.Sse1 = sse; hasBetter = true;
            }
        }
        else cost = int.MaxValue;
        return cost;
    }

    private static uint ObmcCheckBetter(AomSubpelMsParams p, AomMacroblock x, AomMv thisMv, ref AomMv bestMv, State st, ref bool hasBetter)
    {
        uint cost;
        if (AomSubpel.IsSubpelmvInRange(p.MvLimits, thisMv))
        {
            int thismse = UpsampledObmcPrefError(p, x, thisMv, out uint sse);
            cost = (uint)MvErrCost(p, thisMv);
            cost += (uint)thismse;
            if (cost < st.BestErr)
            {
                st.BestErr = cost; bestMv = thisMv; st.Distortion = thismse; st.Sse1 = sse; hasBetter = true;
            }
        }
        else cost = int.MaxValue;
        return cost;
    }

    private static uint Check(AomSubpelMsParams p, AomMacroblock x, AomMv m, ref AomMv bestMv, State st, ref bool hb)
        => p.SubpelSearchType != AomSubpel.USE_2_TAPS_ORIG ? ObmcCheckBetter(p, x, m, ref bestMv, st, ref hb)
                                                           : ObmcCheckBetterFast(p, x, m, ref bestMv, st, ref hb);

    /// <summary>obmc_first_level_check.</summary>
    private static AomMv ObmcFirstLevelCheck(AomSubpelMsParams p, AomMacroblock x, AomMv thisMv, ref AomMv bestMv, int hstep, State st)
    {
        bool d = false;
        uint left = Check(p, x, new AomMv(thisMv.Row, thisMv.Col - hstep), ref bestMv, st, ref d);
        uint right = Check(p, x, new AomMv(thisMv.Row, thisMv.Col + hstep), ref bestMv, st, ref d);
        uint up = Check(p, x, new AomMv(thisMv.Row - hstep, thisMv.Col), ref bestMv, st, ref d);
        uint down = Check(p, x, new AomMv(thisMv.Row + hstep, thisMv.Col), ref bestMv, st, ref d);
        var diag = new AomMv(up <= down ? -hstep : hstep, left <= right ? -hstep : hstep);
        Check(p, x, new AomMv(thisMv.Row + diag.Row, thisMv.Col + diag.Col), ref bestMv, st, ref d);
        return diag;
    }

    /// <summary>obmc_second_level_check_v2.</summary>
    private static void ObmcSecondLevelCheckV2(AomSubpelMsParams p, AomMacroblock x, AomMv thisMv, AomMv diag, ref AomMv bestMv, State st)
    {
        int dr = diag.Row, dc = diag.Col;
        if (thisMv.Equals(bestMv)) return;
        if (thisMv.Row == bestMv.Row) dr = -dr;
        else if (thisMv.Col == bestMv.Col) dc = -dc;
        var rowBias = new AomMv(bestMv.Row + dr, bestMv.Col);
        var colBias = new AomMv(bestMv.Row, bestMv.Col + dc);
        var diagBias = new AomMv(bestMv.Row + dr, bestMv.Col + dc);
        bool hb = false;
        Check(p, x, rowBias, ref bestMv, st, ref hb);
        Check(p, x, colBias, ref bestMv, st, ref hb);
        if (hb) Check(p, x, diagBias, ref bestMv, st, ref hb);
    }

    /// <summary>av1_find_best_obmc_sub_pixel_tree_up.</summary>
    public static int FindBestObmcSubPixelTreeUp(AomMacroblock x, AomSubpelMsParams p, AomMv startMv, out AomMv bestMv, out int distortion, out uint sse1)
    {
        var st = new State();
        int hstep = 4;
        int round = Math.Min(AomSubpel.FULL_PEL - p.ForcedStop, 3 - (p.AllowHp ? 0 : 1));
        bestMv = startMv;
        if (p.SubpelSearchType != AomSubpel.USE_2_TAPS_ORIG)
        {
            st.BestErr = (uint)UpsampledObmcPrefError(p, x, bestMv, out st.Sse1);
            st.Distortion = (int)st.BestErr;
            st.BestErr += (uint)MvErrCost(p, bestMv);
        }
        else
        {
            // setup_obmc_center_error: ms_buffers->ref->buf (the block position, whatever the start mv)
            bool hbd = p.Ref.Buf16 != null;
            st.BestErr = ObmcVariance(p.Ref.Buf, p.Ref.Buf16, p.Ref.Offset, p.Ref.Stride, x.ObmcBuffer.Wsrc, x.ObmcBuffer.Mask, p.W, p.H, p.Bd,
                out st.Sse1);
            st.Distortion = (int)st.BestErr;
            st.BestErr += (uint)MvErrCost(p, bestMv);
        }
        for (int iter = 0; iter < round; ++iter)
        {
            var center = bestMv;
            var diag = ObmcFirstLevelCheck(p, x, center, ref bestMv, hstep, st);
            if (!center.Equals(bestMv) && p.ItersPerStep > 1) ObmcSecondLevelCheckV2(p, x, center, diag, ref bestMv, st);
            hstep >>= 1;
        }
        distortion = st.Distortion;
        sse1 = st.Sse1;
        return (int)st.BestErr;
    }
}
