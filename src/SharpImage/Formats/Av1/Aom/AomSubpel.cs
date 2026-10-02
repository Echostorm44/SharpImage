using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>SUBPEL_MOTION_SEARCH_PARAMS (with its SUBPEL_SEARCH_VAR_PARAMS / MV_COST_PARAMS / MSBuffers).</summary>
internal sealed class AomSubpelMsParams
{
    public bool AllowHp;
    public int ForcedStop, ItersPerStep;
    public int[]? CostList;
    public AomFullMvLimits MvLimits;   // SubpelMvLimits (1/8 pel)
    // MV_COST_PARAMS
    public int MvCostType;
    public AomMv RefMv;
    public int[]? MvJCost;
    public int[][]? MvCost;
    public int ErrorPerBit;
    // SUBPEL_SEARCH_VAR_PARAMS
    public int SubpelSearchType, W, H, Bsize;
    public AomBuf2d Src, Ref;
    public int Bd = 8;
    public bool HasSecondPred;
    // the block (aom_upsampled_pred_scaled reads xd->mi[0], the block scale factors and pd->pre[0])
    public AomCommon Cm = null!;
    public AomMacroblockD Xd = null!;
    public readonly byte[] Pred = new byte[128 * 128];
    public readonly ushort[] Pred16 = new ushort[128 * 128];
}

// Port of libaom 3.14.1 av1/encoder/mcomp.c's sub-pixel motion search (av1_make_default_subpel_ms_params,
// av1_find_best_sub_pixel_tree / _pruned / _pruned_more with their check helpers) and reconinter_enc.c's
// aom_upsampled_pred (and the scaled-reference variant), aom_dsp/variance.c's aom_sub_pixel_variance.
internal static class AomSubpel
{
    public const int SUBPEL_TREE = 0, SUBPEL_TREE_PRUNED = 1, SUBPEL_TREE_PRUNED_MORE = 2;
    public const int EIGHTH_PEL = 0, QUARTER_PEL = 1, HALF_PEL = 2, FULL_PEL = 3;
    public const int USE_2_TAPS_ORIG = 0, USE_2_TAPS = 1, USE_4_TAPS = 2, USE_8_TAPS = 3;
    private const int INIT_SUBPEL_STEP_SIZE = 4;

    private static readonly byte[] Bilinear2t = { 128, 0, 112, 16, 96, 32, 80, 48, 64, 64, 48, 80, 32, 96, 16, 112 };

    /// <summary>av1_set_subpel_mv_search_range.</summary>
    public static AomFullMvLimits SetSubpelMvSearchRange(in AomFullMvLimits mvLimits, AomMv refMv)
    {
        const int maxMv = AomMcomp.MAX_FULL_PEL_VAL * 8;
        int minc = Math.Max(mvLimits.ColMin * 8, refMv.Col - maxMv);
        int maxc = Math.Min(mvLimits.ColMax * 8, refMv.Col + maxMv);
        int minr = Math.Max(mvLimits.RowMin * 8, refMv.Row - maxMv);
        int maxr = Math.Min(mvLimits.RowMax * 8, refMv.Row + maxMv);
        maxc = Math.Max(minc, maxc);
        maxr = Math.Max(minr, maxr);
        return new AomFullMvLimits
        {
            ColMin = Math.Max(AomMvCost.MvLow + 1, minc), ColMax = Math.Min(AomMvCost.MvUpp - 1, maxc),
            RowMin = Math.Max(AomMvCost.MvLow + 1, minr), RowMax = Math.Min(AomMvCost.MvUpp - 1, maxr),
        };
    }

    public static bool IsSubpelmvInRange(in AomFullMvLimits l, AomMv mv)
        => mv.Col >= l.ColMin && mv.Col <= l.ColMax && mv.Row >= l.RowMin && mv.Row <= l.RowMax;

    /// <summary>cond_cost_list.</summary>
    public static int[]? CondCostList(AomComp cpi, int[] costList)
        => cpi.Sf.mv_sf.subpel_search_method != SUBPEL_TREE && cpi.Sf.mv_sf.use_fullpel_costlist != 0 ? costList : null;

    /// <summary>av1_make_default_subpel_ms_params.</summary>
    public static AomSubpelMsParams MakeDefaultSubpelMsParams(AomComp cpi, AomMacroblock x, int bsize, AomMv refMv, int[]? costList,
        AomSubpelMsParams? reuse = null)
    {
        var cm = cpi.Cm;
        var p = reuse ?? new AomSubpelMsParams();
        p.AllowHp = cm.AllowHighPrecisionMv;
        p.ForcedStop = cpi.Sf.mv_sf.subpel_force_stop;
        p.ItersPerStep = cpi.Sf.mv_sf.subpel_iters_per_step;
        p.CostList = costList != null ? CondCostList(cpi, costList) : null;
        p.MvLimits = SetSubpelMvSearchRange(x.MvLimits, refMv);
        if (cpi.Sharpness == 3) throw new NotSupportedException("sharpness 3 motion search margins");
        p.MvCostType = AomMcomp.MV_COST_ENTROPY;
        p.RefMv = refMv;
        p.MvJCost = x.MvCosts.NmvJointCost;
        p.MvCost = x.MvCosts.MvCostStack;
        p.ErrorPerBit = x.Errorperbit;
        p.SubpelSearchType = cpi.Sf.mv_sf.use_accurate_subpel_search;
        p.W = BlockSizeWide[bsize];
        p.H = BlockSizeHigh[bsize];
        p.Bsize = bsize;
        p.Src = x.Plane[0].Src;
        p.Ref = x.E.Plane[0].Pre0;
        p.Bd = x.E.Bd;
        p.HasSecondPred = false;
        p.Cm = cm;
        p.Xd = x.E;
        return p;
    }

    private static int MvErrCost(AomSubpelMsParams p, AomMv mv)
        => AomMcomp.MvErrCost(p.MvCostType, mv, p.RefMv, p.MvJCost, p.MvCost, p.ErrorPerBit);

    // get_buf_from_mv
    private static int RefOff(in AomBuf2d r, AomMv mv) => r.Offset + (mv.Row >> 3) * r.Stride + (mv.Col >> 3);

    /// <summary>var_filter_block2d_bil_first_pass / second_pass (and the high bit depth twins) into p.Pred / p.Pred16.</summary>
    internal static void BilinearFilter(AomSubpelMsParams p, int refOff, int xoff, int yoff)
    {
        int w = p.W, h = p.H;
        ref var r = ref p.Ref;
        Span<ushort> fdata3 = stackalloc ushort[(h + 1) * w];
        int f0 = Bilinear2t[xoff * 2], f1 = Bilinear2t[xoff * 2 + 1];
        int g0 = Bilinear2t[yoff * 2], g1 = Bilinear2t[yoff * 2 + 1];
        if (r.Buf16 != null)
        {
            for (int i = 0; i < h + 1; i++)
                for (int j = 0; j < w; j++)
                {
                    int a = refOff + i * r.Stride + j;
                    fdata3[i * w + j] = (ushort)((r.Buf16[a] * f0 + r.Buf16[a + 1] * f1 + 64) >> 7);
                }
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                    p.Pred16[i * w + j] = (ushort)((fdata3[i * w + j] * g0 + fdata3[(i + 1) * w + j] * g1 + 64) >> 7);
            return;
        }
        for (int i = 0; i < h + 1; i++)
            for (int j = 0; j < w; j++)
            {
                int a = refOff + i * r.Stride + j;
                fdata3[i * w + j] = (ushort)((r.Buf[a] * f0 + r.Buf[a + 1] * f1 + 64) >> 7);
            }
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
                p.Pred[i * w + j] = (byte)((fdata3[i * w + j] * g0 + fdata3[(i + 1) * w + j] * g1 + 64) >> 7);
    }

    /// <summary>aom_sub_pixel_variance{W}x{H} (8-bit) / aom_highbd_{bd}_sub_pixel_variance.</summary>
    public static uint SubPixelVariance(AomSubpelMsParams p, int refOff, int xoff, int yoff, out uint sse)
    {
        BilinearFilter(p, refOff, xoff, yoff);
        if (p.Ref.Buf16 != null) return AomHbd.Variance(p.Pred16, 0, p.W, p.Src.Buf16, p.Src.Offset, p.Src.Stride, 0, p.W, p.H, p.Bd, out sse);
        return AomSad.Variance(p.Pred, 0, p.W, p.Src.Buf, p.Src.Offset, p.Src.Stride, p.W, p.H, out sse);
    }

    /// <summary>vfp->vf(pred, w, src, src_stride) / vfp->vf(ref, stride, src, ...): the variance against the source.</summary>
    private static uint VarianceAt(AomSubpelMsParams p, byte[]? a8, ushort[]? a16, int aOff, int aStride, out uint sse)
    {
        if (a16 != null) return AomHbd.Variance(a16, aOff, aStride, p.Src.Buf16, p.Src.Offset, p.Src.Stride, 0, p.W, p.H, p.Bd, out sse);
        return AomSad.Variance(a8!, aOff, aStride, p.Src.Buf, p.Src.Offset, p.Src.Stride, p.W, p.H, out sse);
    }

    /// <summary>estimated_pref_error.</summary>
    private static int EstimatedPrefError(AomSubpelMsParams p, AomMv mv, out uint sse)
    {
        if (p.HasSecondPred) throw new NotImplementedException("compound sub-pixel variance");
        return (int)SubPixelVariance(p, RefOff(p.Ref, mv), mv.Col & 7, mv.Row & 7, out sse);
    }

    /// <summary>aom_upsampled_pred_scaled: true (and the prediction in p.Pred) for a scaled reference.</summary>
    private static bool UpsampledPredScaled(AomSubpelMsParams p, AomMv mv)
    {
        var xd = p.Xd;
        var mi = xd.Mi0;
        bool isIntrabc = mi.UseIntrabc != 0;
        var sf = isIntrabc ? AomInterPred.Identity : xd.BlockRefScaleFactors[0]!;
        if (!sf.IsScaled) return false;
        if (isIntrabc) throw new NotSupportedException("scaled intrabc");
        var pd = xd.Plane[0];
        var ip = new AomInterPredParams();
        ip.ConvParams = AomConvParams.Get(0, 0, xd.Bd);
        AomInterPred.InitInterParams(ip, p.W, p.H, (xd.MiRow * 4) >> pd.SubsamplingY, (xd.MiCol * 4) >> pd.SubsamplingX, pd.SubsamplingX,
            pd.SubsamplingY, xd.Bd, xd.IsHbd, false, sf, pd.Pre0, 0);
        AomInterPred.BuildOneInterPredictor(xd.IsHbd ? null : p.Pred, xd.IsHbd ? p.Pred16 : null, 0, p.W, mv, ip);
        return true;
    }

    /// <summary>aom_upsampled_pred_c / aom_highbd_upsampled_pred_c into p.Pred / p.Pred16.</summary>
    internal static void UpsampledPred(AomSubpelMsParams p, AomMv mv, int subpelX, int subpelY, int refOff)
    {
        if (UpsampledPredScaled(p, mv)) return;
        var filter = p.SubpelSearchType switch
        {
            USE_2_TAPS => AomFilter.Interp4Tap[BILINEAR],
            USE_4_TAPS => AomFilter.Interp4Tap[EIGHTTAP_REGULAR],
            USE_8_TAPS => AomFilter.ParamsList[EIGHTTAP_REGULAR],
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };
        int w = p.W, h = p.H;
        ref var r = ref p.Ref;
        int stride = r.Stride;
        bool hbd = r.Buf16 != null;
        int maxv = (1 << p.Bd) - 1;
        if (subpelX == 0 && subpelY == 0)
        {
            for (int i = 0; i < h; i++)
                if (hbd) Array.Copy(r.Buf16!, refOff + i * stride, p.Pred16, i * w, w);
                else Array.Copy(r.Buf, refOff + i * stride, p.Pred, i * w, w);
            return;
        }
        var k = filter.Filter;
        if (subpelY == 0)
        {
            int kx = (subpelX << 1) * 8;
            ConvHoriz(r, refOff, stride, p, w, h, k, kx, hbd, maxv);
            return;
        }
        if (subpelX == 0)
        {
            int ky = (subpelY << 1) * 8;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int s = refOff + (y - 3) * stride + x, sum = 0;
                    for (int t = 0; t < 8; t++) sum += (hbd ? r.Buf16![s + t * stride] : r.Buf[s + t * stride]) * k[ky + t];
                    int v = Math.Clamp((sum + 64) >> 7, 0, maxv);
                    if (hbd) p.Pred16[y * w + x] = (ushort)v; else p.Pred[y * w + x] = (byte)v;
                }
            return;
        }
        {
            int taps = filter.Taps;
            int kx = (subpelX << 1) * 8, ky = (subpelY << 1) * 8;
            int ih = (((h - 1) * 8 + subpelY) >> 3) + taps;
            // temp rows: libaom's vertical pass reads 3 rows above temp + MAX_SB_SIZE * ((taps >> 1) - 1) (the 4-tap
            // kernels' outer taps are zero, so rows outside the filtered ones only ever meet a zero tap)
            const int pad = 4;
            var temp = new int[(ih + 2 * pad) * w];
            int src0 = refOff - stride * ((taps >> 1) - 1);
            for (int y = 0; y < ih; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = src0 + y * stride + x - 3, sum = 0;
                    for (int t = 0; t < 8; t++) sum += (hbd ? r.Buf16![s + t] : r.Buf[s + t]) * k[kx + t];
                    temp[(y + pad) * w + x] = Math.Clamp((sum + 64) >> 7, 0, maxv);
                }
            int base0 = pad + ((taps >> 1) - 1) - 3;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int sum = 0;
                    for (int t = 0; t < 8; t++)
                    {
                        int kt = k[ky + t];
                        if (kt != 0) sum += temp[(base0 + y + t) * w + x] * kt;
                    }
                    int v = Math.Clamp((sum + 64) >> 7, 0, maxv);
                    if (hbd) p.Pred16[y * w + x] = (ushort)v; else p.Pred[y * w + x] = (byte)v;
                }
        }
    }

    private static void ConvHoriz(in AomBuf2d r, int refOff, int stride, AomSubpelMsParams p, int w, int h, short[] k, int kx, bool hbd, int maxv)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = refOff + y * stride + x - 3, sum = 0;
                for (int t = 0; t < 8; t++) sum += (hbd ? r.Buf16![s + t] : r.Buf[s + t]) * k[kx + t];
                int v = Math.Clamp((sum + 64) >> 7, 0, maxv);
                if (hbd) p.Pred16[y * w + x] = (ushort)v; else p.Pred[y * w + x] = (byte)v;
            }
    }

    /// <summary>upsampled_pref_error.</summary>
    private static int UpsampledPrefError(AomSubpelMsParams p, AomMv mv, out uint sse)
    {
        if (p.HasSecondPred) throw new NotImplementedException("compound upsampled prediction");
        UpsampledPred(p, mv, mv.Col & 7, mv.Row & 7, RefOff(p.Ref, mv));
        return (int)VarianceAt(p, p.Pred, p.Ref.Buf16 != null ? p.Pred16 : null, 0, p.W, out sse);
    }

    private sealed class State
    {
        public uint BestErr;
        public uint Sse1;
        public int Distortion;
    }

    /// <summary>check_better_fast.</summary>
    private static uint CheckBetterFast(AomSubpelMsParams p, AomMv thisMv, ref AomMv bestMv, State st, ref bool hasBetter, bool isScaled)
    {
        uint cost;
        if (IsSubpelmvInRange(p.MvLimits, thisMv))
        {
            uint sse;
            int thismse = isScaled ? UpsampledPrefError(p, thisMv, out sse) : EstimatedPrefError(p, thisMv, out sse);
            cost = (uint)MvErrCost(p, thisMv);
            cost += (uint)thismse;
            if (cost < st.BestErr)
            {
                st.BestErr = cost;
                bestMv = thisMv;
                st.Distortion = thismse;
                st.Sse1 = sse;
                hasBetter = true;
            }
        }
        else cost = int.MaxValue;
        return cost;
    }

    /// <summary>check_better.</summary>
    private static uint CheckBetter(AomSubpelMsParams p, AomMv thisMv, ref AomMv bestMv, State st, ref bool isBetter)
    {
        uint cost;
        if (IsSubpelmvInRange(p.MvLimits, thisMv))
        {
            int thismse = UpsampledPrefError(p, thisMv, out uint sse);
            cost = (uint)MvErrCost(p, thisMv);
            cost += (uint)thismse;
            if (cost < st.BestErr)
            {
                st.BestErr = cost;
                bestMv = thisMv;
                st.Distortion = thismse;
                st.Sse1 = sse;
                isBetter = true;
            }
        }
        else cost = int.MaxValue;
        return cost;
    }

    private static AomMv GetBestDiagStep(int step, uint left, uint right, uint up, uint down)
        => new(up <= down ? -step : step, left <= right ? -step : step);

    /// <summary>first_level_check_fast.</summary>
    private static AomMv FirstLevelCheckFast(AomSubpelMsParams p, AomMv thisMv, ref AomMv bestMv, int hstep, State st, bool isScaled)
    {
        bool d = false;
        uint left = CheckBetterFast(p, new AomMv(thisMv.Row, thisMv.Col - hstep), ref bestMv, st, ref d, isScaled);
        uint right = CheckBetterFast(p, new AomMv(thisMv.Row, thisMv.Col + hstep), ref bestMv, st, ref d, isScaled);
        uint up = CheckBetterFast(p, new AomMv(thisMv.Row - hstep, thisMv.Col), ref bestMv, st, ref d, isScaled);
        uint down = CheckBetterFast(p, new AomMv(thisMv.Row + hstep, thisMv.Col), ref bestMv, st, ref d, isScaled);
        var diag = GetBestDiagStep(hstep, left, right, up, down);
        CheckBetterFast(p, new AomMv(thisMv.Row + diag.Row, thisMv.Col + diag.Col), ref bestMv, st, ref d, isScaled);
        return diag;
    }

    /// <summary>second_level_check_fast.</summary>
    private static void SecondLevelCheckFast(AomSubpelMsParams p, AomMv thisMv, AomMv diag, ref AomMv bestMv, int hstep, State st, bool isScaled)
    {
        int tr = thisMv.Row, tc = thisMv.Col, br = bestMv.Row, bc = bestMv.Col;
        bool d = false;
        if (tr != br && tc != bc)
        {
            CheckBetterFast(p, new AomMv(br, bc + diag.Col), ref bestMv, st, ref d, isScaled);
            CheckBetterFast(p, new AomMv(br + diag.Row, bc), ref bestMv, st, ref d, isScaled);
        }
        else if (tr == br && tc != bc)
        {
            CheckBetterFast(p, new AomMv(br + hstep, bc + diag.Col), ref bestMv, st, ref d, isScaled);
            CheckBetterFast(p, new AomMv(br - hstep, bc + diag.Col), ref bestMv, st, ref d, isScaled);
            CheckBetterFast(p, new AomMv(br - diag.Row, bc), ref bestMv, st, ref d, isScaled);
        }
        else if (tr != br && tc == bc)
        {
            CheckBetterFast(p, new AomMv(br + diag.Row, bc + hstep), ref bestMv, st, ref d, isScaled);
            CheckBetterFast(p, new AomMv(br + diag.Row, bc - hstep), ref bestMv, st, ref d, isScaled);
            CheckBetterFast(p, new AomMv(br, bc - diag.Col), ref bestMv, st, ref d, isScaled);
        }
    }

    /// <summary>two_level_checks_fast.</summary>
    private static void TwoLevelChecksFast(AomSubpelMsParams p, AomMv thisMv, ref AomMv bestMv, int hstep, State st, int iters, bool isScaled)
    {
        var diag = FirstLevelCheckFast(p, thisMv, ref bestMv, hstep, st, isScaled);
        if (iters > 1) SecondLevelCheckFast(p, thisMv, diag, ref bestMv, hstep, st, isScaled);
    }

    /// <summary>first_level_check.</summary>
    private static AomMv FirstLevelCheck(AomSubpelMsParams p, AomMv thisMv, ref AomMv bestMv, int hstep, State st)
    {
        bool d = false;
        uint left = CheckBetter(p, new AomMv(thisMv.Row, thisMv.Col - hstep), ref bestMv, st, ref d);
        uint right = CheckBetter(p, new AomMv(thisMv.Row, thisMv.Col + hstep), ref bestMv, st, ref d);
        uint up = CheckBetter(p, new AomMv(thisMv.Row - hstep, thisMv.Col), ref bestMv, st, ref d);
        uint down = CheckBetter(p, new AomMv(thisMv.Row + hstep, thisMv.Col), ref bestMv, st, ref d);
        var diag = GetBestDiagStep(hstep, left, right, up, down);
        CheckBetter(p, new AomMv(thisMv.Row + diag.Row, thisMv.Col + diag.Col), ref bestMv, st, ref d);
        return diag;
    }

    /// <summary>second_level_check_v2.</summary>
    private static void SecondLevelCheckV2(AomSubpelMsParams p, AomMv thisMv, AomMv diag, ref AomMv bestMv, State st, bool isScaled)
    {
        int dr = diag.Row, dc = diag.Col;
        if (thisMv.Equals(bestMv)) return;
        if (thisMv.Row == bestMv.Row) dr = -dr;
        else if (thisMv.Col == bestMv.Col) dc = -dc;
        var rowBias = new AomMv(bestMv.Row + dr, bestMv.Col);
        var colBias = new AomMv(bestMv.Row, bestMv.Col + dc);
        var diagBias = new AomMv(bestMv.Row + dr, bestMv.Col + dc);
        bool hasBetter = false;
        if (p.SubpelSearchType > USE_2_TAPS)
        {
            CheckBetter(p, rowBias, ref bestMv, st, ref hasBetter);
            CheckBetter(p, colBias, ref bestMv, st, ref hasBetter);
            if (hasBetter) CheckBetter(p, diagBias, ref bestMv, st, ref hasBetter);
        }
        else
        {
            CheckBetterFast(p, rowBias, ref bestMv, st, ref hasBetter, isScaled);
            CheckBetterFast(p, colBias, ref bestMv, st, ref hasBetter, isScaled);
            if (hasBetter) CheckBetterFast(p, diagBias, ref bestMv, st, ref hasBetter, isScaled);
        }
    }

    /// <summary>setup_center_error.</summary>
    private static uint SetupCenterError(AomSubpelMsParams p, AomMv bestMv, State st)
    {
        if (p.HasSecondPred) throw new NotImplementedException("compound center error");
        int off = RefOff(p.Ref, bestMv);
        uint besterr = VarianceAt(p, p.Ref.Buf, p.Ref.Buf16, off, p.Ref.Stride, out st.Sse1);
        st.Distortion = (int)besterr;
        besterr += (uint)MvErrCost(p, bestMv);
        return besterr;
    }

    /// <summary>upsampled_setup_center_error.</summary>
    private static uint UpsampledSetupCenterError(AomSubpelMsParams p, AomMv bestMv, State st)
    {
        uint besterr = (uint)UpsampledPrefError(p, bestMv, out st.Sse1);
        st.Distortion = (int)besterr;
        besterr += (uint)MvErrCost(p, bestMv);
        return besterr;
    }

    private static int DivideAndRound(int n, int d) => ((n < 0) ^ (d < 0)) ? ((n - d / 2) / d) : ((n + d / 2) / d);

    private static bool IsCostListWellbehaved(int[] c) => c[0] < c[1] && c[0] < c[2] && c[0] < c[3] && c[0] < c[4];

    private static void GetCostSurfMin(int[] c, out int ir, out int ic, int bits)
    {
        ic = DivideAndRound((c[1] - c[3]) * (1 << (bits - 1)), c[1] - 2 * c[0] + c[3]);
        ir = DivideAndRound((c[4] - c[2]) * (1 << (bits - 1)), c[4] - 2 * c[0] + c[2]);
    }

    private static bool CheckRepeatedMvAndUpdate(AomMv[]? list, AomMv cur, int iter)
    {
        if (list != null)
        {
            if (list[iter].Equals(cur)) return true;
            list[iter] = cur;
        }
        return false;
    }

    private static bool IsScaledBlock(AomSubpelMsParams p)
    {
        var mi = p.Xd.Mi0;
        var sf = mi.UseIntrabc != 0 ? AomInterPred.Identity : p.Xd.BlockRefScaleFactors[0]!;
        return sf.IsScaled;
    }

    private static uint SetupCenterErrorFacade(AomSubpelMsParams p, AomMv bestMv, State st, bool isScaled)
        => isScaled ? UpsampledSetupCenterError(p, bestMv, st) : SetupCenterError(p, bestMv, st);

    /// <summary>mv_search_params->find_fractional_mv_step (the speed feature's subpel_search_method).</summary>
    public static int FindFractionalMvStep(AomComp cpi, AomSubpelMsParams p, AomMv startMv, AomFullpelMvStats? startStats, out AomMv bestMv,
        out int distortion, out uint sse1, AomMv[]? lastMvSearchList)
    {
        return cpi.Sf.mv_sf.subpel_search_method switch
        {
            SUBPEL_TREE => FindBestSubPixelTree(p, startMv, startStats, out bestMv, out distortion, out sse1, lastMvSearchList),
            SUBPEL_TREE_PRUNED => FindBestSubPixelTreePruned(p, startMv, startStats, out bestMv, out distortion, out sse1, lastMvSearchList),
            _ => FindBestSubPixelTreePrunedMore(p, startMv, startStats, out bestMv, out distortion, out sse1, lastMvSearchList),
        };
    }

    /// <summary>av1_find_best_sub_pixel_tree_pruned_more.</summary>
    public static int FindBestSubPixelTreePrunedMore(AomSubpelMsParams p, AomMv startMv, AomFullpelMvStats? startStats, out AomMv bestMv,
        out int distortion, out uint sse1, AomMv[]? lastMvSearchList)
    {
        var st = new State { BestErr = int.MaxValue };
        int iter = 0, hstep = INIT_SUBPEL_STEP_SIZE;
        bestMv = startMv;
        bool isScaled = IsScaledBlock(p);
        if (startStats is AomFullpelMvStats ss && !isScaled)
        {
            st.BestErr = (uint)(ss.Distortion + ss.ErrCost);
            st.Distortion = ss.Distortion;
            st.Sse1 = ss.Sse;
        }
        else st.BestErr = SetupCenterErrorFacade(p, bestMv, st, isScaled);
        distortion = st.Distortion; sse1 = st.Sse1;
        if (p.ForcedStop == FULL_PEL) return (int)st.BestErr;
        if (CheckRepeatedMvAndUpdate(lastMvSearchList, bestMv, iter)) return int.MaxValue;
        iter++;
        var cl = p.CostList;
        if (cl != null && cl[0] != int.MaxValue && cl[1] != int.MaxValue && cl[2] != int.MaxValue && cl[3] != int.MaxValue &&
            cl[4] != int.MaxValue && IsCostListWellbehaved(cl))
        {
            GetCostSurfMin(cl, out int ir, out int ic, 1);
            if (ir != 0 || ic != 0)
            {
                bool d = false;
                CheckBetterFast(p, new AomMv(startMv.Row + ir * hstep, startMv.Col + ic * hstep), ref bestMv, st, ref d, isScaled);
            }
        }
        else TwoLevelChecksFast(p, startMv, ref bestMv, hstep, st, p.ItersPerStep, isScaled);
        if (p.ForcedStop < HALF_PEL)
        {
            if (CheckRepeatedMvAndUpdate(lastMvSearchList, bestMv, iter)) { distortion = st.Distortion; sse1 = st.Sse1; return int.MaxValue; }
            iter++;
            hstep >>= 1;
            startMv = bestMv;
            TwoLevelChecksFast(p, startMv, ref bestMv, hstep, st, p.ItersPerStep, isScaled);
        }
        if (p.AllowHp && p.ForcedStop == EIGHTH_PEL)
        {
            if (CheckRepeatedMvAndUpdate(lastMvSearchList, bestMv, iter)) { distortion = st.Distortion; sse1 = st.Sse1; return int.MaxValue; }
            iter++;
            hstep >>= 1;
            startMv = bestMv;
            TwoLevelChecksFast(p, startMv, ref bestMv, hstep, st, p.ItersPerStep, isScaled);
        }
        distortion = st.Distortion; sse1 = st.Sse1;
        return (int)st.BestErr;
    }

    /// <summary>av1_find_best_sub_pixel_tree_pruned.</summary>
    public static int FindBestSubPixelTreePruned(AomSubpelMsParams p, AomMv startMv, AomFullpelMvStats? startStats, out AomMv bestMv,
        out int distortion, out uint sse1, AomMv[]? lastMvSearchList)
    {
        var st = new State { BestErr = int.MaxValue };
        int iter = 0, hstep = INIT_SUBPEL_STEP_SIZE;
        bestMv = startMv;
        bool isScaled = IsScaledBlock(p);
        if (startStats is AomFullpelMvStats ss && !isScaled)
        {
            st.BestErr = (uint)(ss.Distortion + ss.ErrCost);
            st.Distortion = ss.Distortion;
            st.Sse1 = ss.Sse;
        }
        else st.BestErr = SetupCenterErrorFacade(p, bestMv, st, isScaled);
        distortion = st.Distortion; sse1 = st.Sse1;
        if (p.ForcedStop == FULL_PEL) return (int)st.BestErr;
        if (CheckRepeatedMvAndUpdate(lastMvSearchList, bestMv, iter)) return int.MaxValue;
        iter++;
        var cl = p.CostList;
        if (cl != null && cl[0] != int.MaxValue && cl[1] != int.MaxValue && cl[2] != int.MaxValue && cl[3] != int.MaxValue &&
            cl[4] != int.MaxValue)
        {
            int whichdir = (cl[1] < cl[3] ? 0 : 1) + (cl[2] < cl[4] ? 0 : 2);
            var left = new AomMv(startMv.Row, startMv.Col - hstep);
            var right = new AomMv(startMv.Row, startMv.Col + hstep);
            var bottom = new AomMv(startMv.Row + hstep, startMv.Col);
            var top = new AomMv(startMv.Row - hstep, startMv.Col);
            var bl = new AomMv(startMv.Row + hstep, startMv.Col - hstep);
            var brr = new AomMv(startMv.Row + hstep, startMv.Col + hstep);
            var tl = new AomMv(startMv.Row - hstep, startMv.Col - hstep);
            var tr = new AomMv(startMv.Row - hstep, startMv.Col + hstep);
            bool d = false;
            switch (whichdir)
            {
                case 0:
                    CheckBetterFast(p, left, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, bottom, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, bl, ref bestMv, st, ref d, isScaled);
                    break;
                case 1:
                    CheckBetterFast(p, right, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, bottom, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, brr, ref bestMv, st, ref d, isScaled);
                    break;
                case 2:
                    CheckBetterFast(p, left, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, top, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, tl, ref bestMv, st, ref d, isScaled);
                    break;
                case 3:
                    CheckBetterFast(p, right, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, top, ref bestMv, st, ref d, isScaled);
                    CheckBetterFast(p, tr, ref bestMv, st, ref d, isScaled);
                    break;
            }
        }
        else TwoLevelChecksFast(p, startMv, ref bestMv, hstep, st, p.ItersPerStep, isScaled);
        if (p.ForcedStop < HALF_PEL)
        {
            if (CheckRepeatedMvAndUpdate(lastMvSearchList, bestMv, iter)) { distortion = st.Distortion; sse1 = st.Sse1; return int.MaxValue; }
            iter++;
            hstep >>= 1;
            startMv = bestMv;
            TwoLevelChecksFast(p, startMv, ref bestMv, hstep, st, p.ItersPerStep, isScaled);
        }
        if (p.AllowHp && p.ForcedStop == EIGHTH_PEL)
        {
            if (CheckRepeatedMvAndUpdate(lastMvSearchList, bestMv, iter)) { distortion = st.Distortion; sse1 = st.Sse1; return int.MaxValue; }
            iter++;
            hstep >>= 1;
            startMv = bestMv;
            TwoLevelChecksFast(p, startMv, ref bestMv, hstep, st, p.ItersPerStep, isScaled);
        }
        distortion = st.Distortion; sse1 = st.Sse1;
        return (int)st.BestErr;
    }

    /// <summary>av1_find_best_sub_pixel_tree.</summary>
    public static int FindBestSubPixelTree(AomSubpelMsParams p, AomMv startMv, AomFullpelMvStats? startStats, out AomMv bestMv,
        out int distortion, out uint sse1, AomMv[]? lastMvSearchList)
    {
        var st = new State { BestErr = int.MaxValue };
        int round = Math.Min(FULL_PEL - p.ForcedStop, 3 - (p.AllowHp ? 0 : 1));
        int hstep = INIT_SUBPEL_STEP_SIZE;
        bestMv = startMv;
        bool isScaled = IsScaledBlock(p);
        if (startStats is AomFullpelMvStats ss && !isScaled)
        {
            st.BestErr = (uint)(ss.Distortion + ss.ErrCost);
            st.Distortion = ss.Distortion;
            st.Sse1 = ss.Sse;
        }
        else st.BestErr = p.SubpelSearchType > USE_2_TAPS ? UpsampledSetupCenterError(p, bestMv, st) : SetupCenterError(p, bestMv, st);
        distortion = st.Distortion; sse1 = st.Sse1;
        if (round == 0) return (int)st.BestErr;
        for (int iter = 0; iter < round; ++iter)
        {
            var center = bestMv;
            if (CheckRepeatedMvAndUpdate(lastMvSearchList, center, iter)) { distortion = st.Distortion; sse1 = st.Sse1; return int.MaxValue; }
            AomMv diag = p.SubpelSearchType > USE_2_TAPS
                ? FirstLevelCheck(p, center, ref bestMv, hstep, st)
                : FirstLevelCheckFast(p, center, ref bestMv, hstep, st, isScaled);
            if (!center.Equals(bestMv) && p.ItersPerStep > 1) SecondLevelCheckV2(p, center, diag, ref bestMv, st, isScaled);
            hstep >>= 1;
        }
        distortion = st.Distortion; sse1 = st.Sse1;
        return (int)st.BestErr;
    }
}
