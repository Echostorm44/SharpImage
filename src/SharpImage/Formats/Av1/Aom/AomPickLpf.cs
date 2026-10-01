using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>aom_dsp/psnr.c + sum_squares.c: the SSE / variance extractors the filter searches use (8-bit). The SIMD
/// get_sse / aom_var_2d_u8 are exact integer sums, so plain loops reproduce them.</summary>
internal static class AomSse
{
    /// <summary>aom_get_{y,u,v}_sse_part: SSE of a width x height window at (hstart, vstart).</summary>
    public static long SsePart(AomYv12Plane a, AomYv12Plane b, int hstart, int width, int vstart, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        // aom_highbd_get_{y,u,v}_sse: the exact SSE (aom_highbd_8_mse16x16 blocks + highbd_encoder_sse edges)
        if (a.Buf16 != null) return AomHbd.Sse(a.Buf16, a.At(hstart, vstart), a.Stride, b.Buf16, b.At(hstart, vstart), b.Stride, width, height);
        return AomEncodeMb.Sse(a.Buf, a.At(hstart, vstart), a.Stride, b.Buf, b.At(hstart, vstart), b.Stride, width, height);
    }

    /// <summary>aom_get_sse_plane (8-bit): SSE over the plane's crop.</summary>
    public static long SsePlane(AomYv12Plane a, AomYv12Plane b) => SsePart(a, b, 0, a.CropWidth, 0, a.CropHeight);

    /// <summary>aom_get_{y,u,v}_var: aom_var_2d_u8 of the window divided by its area.</summary>
    public static ulong VarPart(AomYv12Plane a, int hstart, int width, int vstart, int height)
    {
        ulong ss = 0, s = 0;
        if (a.Buf16 != null)
        {
            // aom_highbd_get_{y,u,v}_var: aom_var_2d_u16 / area
            for (int r = 0; r < height; r++)
            {
                int ia = a.At(hstart, vstart + r);
                for (int c = 0; c < width; c++) { ulong v = a.Buf16[ia + c]; ss += v * v; s += v; }
            }
            ulong n16 = (ulong)(width * height);
            return (ss - s * s / n16) / n16;
        }
        for (int r = 0; r < height; r++)
        {
            int ia = a.At(hstart, vstart + r);
            for (int c = 0; c < width; c++)
            {
                uint v = a.Buf[ia + c];
                ss += v * v;
                s += v;
            }
        }
        ulong n = (ulong)(width * height);
        return (ss - s * s / n) / n;
    }
}

/// <summary>The encoder state av1_pick_filter_level reads (cpi->sf.lpf_sf, cpi->oxcf, cm->features, frame type).</summary>
internal sealed class AomLpfPickConfig
{
    public int Method = LPF_PICK_FROM_FULL_IMAGE;           // sf.lpf_sf.lpf_pick
    public int UseCoarseFilterLevelSearch;                   // sf.lpf_sf.use_coarse_filter_level_search
    public int SkipLoopFilterUsingFiltError;                 // sf.lpf_sf.skip_loop_filter_using_filt_error
    /// <summary>oxcf.mode == ALLINTRA || tune == IQ || tune == SSIMULACRA2: take the sharpness from the config.</summary>
    public bool SharpnessFromConfig = true;
    public int Sharpness;                                    // oxcf.algo_cfg.sharpness
    public bool EnableAdaptiveSharpness;                     // oxcf.algo_cfg.enable_adaptive_sharpness
    public int BaseQindex;
    public int BitDepth = 8;
    public bool KeyFrame = true;                             // current_frame.frame_type == KEY_FRAME
    public bool IntraOnly = true;                            // frame_is_intra_only
    public bool TxModeOnly4x4;                               // cm->features.tx_mode == ONLY_4X4
    /// <summary>ppi->filter_level[0..1], filter_level_u, filter_level_v (read for non-intra frames only).</summary>
    public readonly int[] LastFrameFilterLevel = new int[4];
    /// <summary>try_filter_frame's lpf_opt_level: is_inter_tx_size_search_level_one(&amp;sf.tx_sf).</summary>
    public int SearchLpfOptLevel;
    /// <summary>The final deblocking's get_lpf_opt_level(&amp;sf): 0, 1 (dual/quad), 2 (LPF_PICK_FROM_Q: joint chroma).</summary>
    public int FrameLpfOptLevel;
    /// <summary>Multi-threaded encode (mt_info num_workers &gt; 1): the chroma level searches run beside the luma ones
    /// (the planes filter independently, so the levels are the single-threaded ones, as libaom's MT loop filter's are).</summary>
    public bool Parallel;
}

/// <summary>Port of libaom av1/encoder/picklpf.c (single-threaded semantics; the row-MT loop filter gives the same
/// frame). The search filters cur in place and restores it after every trial, like libaom's last_frame_uf copy.</summary>
internal static class AomPickLpf
{
    private const int MaxLoopFilter = AomLoopFilterParams.MaxLoopFilter;

    private static long TryFilterFrame(AomYv12 sd, AomYv12 cur, AomYv12Plane backup, AomLfMiGrid mi,
        AomLoopFilterParams lf, AomLoopFilter filter, int filtLevel, bool partialFrame, int plane, int dir, int optLevel)
    {
        int f0 = filtLevel, f1 = filtLevel;
        if (plane == 0 && dir == 0) f1 = lf.FilterLevel[1];
        if (plane == 0 && dir == 1) f0 = lf.FilterLevel[0];
        switch (plane)
        {
            case 0: lf.FilterLevel[0] = f0; lf.FilterLevel[1] = f1; break;
            case 1: lf.FilterLevelU = f0; break;
            case 2: lf.FilterLevelV = f0; break;
        }
        filter.FilterFrame(cur, mi, lf, plane, plane + 1, partialFrame, optLevel);
        long filtErr = AomSse.SsePlane(sd.Planes[plane], cur.Planes[plane]);
        // Re-instate the unfiltered frame
        cur.Planes[plane].CopyAreaFrom(backup);
        return filtErr;
    }

    private static int SearchFilterLevel(AomYv12 sd, AomYv12 cur, AomYv12Plane backup, AomLfMiGrid mi,
        AomLoopFilterParams lf, AomLoopFilter filter, AomLpfPickConfig cfg, bool partialFrame,
        ReadOnlySpan<int> lastFrameFilterLevel, int plane, int dir, out long bestFilterSse)
    {
        const int minFilterLevel = 0;
        const int maxFilterLevel = MaxLoopFilter;   // get_max_filter_level: one-pass (no twopass stats)
        int filtDirection = 0;
        int lvl = plane switch
        {
            0 => dir == 2 ? (lastFrameFilterLevel[0] + lastFrameFilterLevel[1] + 1) >> 1 : lastFrameFilterLevel[dir],
            1 => lastFrameFilterLevel[2],
            _ => lastFrameFilterLevel[3],
        };
        int filtMid = Math.Clamp(lvl, minFilterLevel, maxFilterLevel);
        int filterStep = filtMid < 16 ? 4 : filtMid / 4;
        Span<long> ssErr = stackalloc long[MaxLoopFilter + 1];
        ssErr.Fill(-1);
        int minFilterStepThresh = cfg.UseCoarseFilterLevelSearch != 0 ? 2 : 0;

        backup.CopyAreaFrom(cur.Planes[plane]);
        long bestErr = TryFilterFrame(sd, cur, backup, mi, lf, filter, filtMid, partialFrame, plane, dir, cfg.SearchLpfOptLevel);
        int filtBest = filtMid;
        ssErr[filtMid] = bestErr;

        while (filterStep > minFilterStepThresh)
        {
            int filtHigh = Math.Min(filtMid + filterStep, maxFilterLevel);
            int filtLow = Math.Max(filtMid - filterStep, minFilterLevel);

            // Bias against raising loop filter in favor of lowering it.
            long bias = (bestErr >> (15 - (filtMid / 8))) * filterStep;
            // yx, bias less for large block size
            if (!cfg.TxModeOnly4x4) bias >>= 1;

            if (filtDirection <= 0 && filtLow != filtMid)
            {
                if (ssErr[filtLow] < 0)
                    ssErr[filtLow] = TryFilterFrame(sd, cur, backup, mi, lf, filter, filtLow, partialFrame, plane, dir, cfg.SearchLpfOptLevel);
                // If value is close to the best so far then bias towards a lower loop filter value.
                if (ssErr[filtLow] < bestErr + bias)
                {
                    if (ssErr[filtLow] < bestErr) bestErr = ssErr[filtLow];
                    filtBest = filtLow;
                }
            }
            if (filtDirection >= 0 && filtHigh != filtMid)
            {
                if (ssErr[filtHigh] < 0)
                    ssErr[filtHigh] = TryFilterFrame(sd, cur, backup, mi, lf, filter, filtHigh, partialFrame, plane, dir, cfg.SearchLpfOptLevel);
                // If value is significantly better than previous best, bias added against raising filter value
                if (ssErr[filtHigh] < bestErr - bias)
                {
                    bestErr = ssErr[filtHigh];
                    filtBest = filtHigh;
                }
            }
            // Half the step distance if the best filter value was the same as last time
            if (filtBest == filtMid)
            {
                filterStep /= 2;
                filtDirection = 0;
            }
            else
            {
                filtDirection = filtBest < filtMid ? -1 : 1;
                filtMid = filtBest;
            }
        }
        bestFilterSse = ssErr[filtBest];
        return filtBest;
    }

    /// <summary>av1_pick_filter_level: sets lf's levels and sharpness. cur (the reconstruction before deblocking) is
    /// left unfiltered. filter carries the loop-filter tables (cm->lf_info).</summary>
    public static void PickFilterLevel(AomYv12 sd, AomYv12 cur, AomLfMiGrid mi, AomLoopFilterParams lf,
        AomLpfPickConfig cfg, AomLoopFilter filter)
    {
        int numPlanes = cur.NumPlanes;
        lf.SharpnessLevel = cfg.SharpnessFromConfig ? cfg.Sharpness : 0;
        if (cfg.EnableAdaptiveSharpness)
        {
            int maxLfSharpness = cfg.BaseQindex <= 112 ? 7 : cfg.BaseQindex <= 160 ? 1 : 0;
            lf.SharpnessLevel = Math.Min(lf.SharpnessLevel, maxLfSharpness);
        }

        int method = cfg.Method;
        if (method == LPF_PICK_MINIMAL_LPF)
        {
            lf.FilterLevel[0] = 0;
            lf.FilterLevel[1] = 0;
        }
        else if (method >= LPF_PICK_FROM_Q)
        {
            const int minFilterLevel = 0, maxFilterLevel = MaxLoopFilter;
            int bdIdx = cfg.BitDepth == 8 ? 0 : cfg.BitDepth == 10 ? 1 : 2;
            int q = Av1Tables.DequantTable[bdIdx, cfg.BaseQindex, 1];   // av1_ac_quant_QTX
            // (the non-key 8-bit branch's multiplier: 12034 for q > 0, the rt-only boosts do not apply here)
            int interFrameMultiplier = q > 0 ? 12034 : 6017;
            int filtGuess = cfg.BitDepth switch
            {
                8 => cfg.KeyFrame ? (q * 17563 - 421574 + (1 << 17)) >> 18
                                  : (q * interFrameMultiplier + 650707 + (1 << 17)) >> 18,
                10 => (q * 20723 + 4060632 + (1 << 19)) >> 20,
                _ => (q * 20723 + 16242526 + (1 << 21)) >> 22,
            };
            if (cfg.BitDepth != 8 && cfg.KeyFrame) filtGuess -= 4;
            int g = Math.Clamp(filtGuess, minFilterLevel, maxFilterLevel);
            lf.FilterLevel[0] = g;
            lf.FilterLevel[1] = g;
            lf.FilterLevelU = g;
            lf.FilterLevelV = g;
        }
        else
        {
            Span<int> lastFrameFilterLevel = stackalloc int[4];
            if (!cfg.IntraOnly) cfg.LastFrameFilterLevel.CopyTo(lastFrameFilterLevel);
            bool partial = method == LPF_PICK_FROM_SUBIMAGE;
            Span<long> zeroFilterSse = stackalloc long[3];
            Span<long> bestFilterSse = stackalloc long[3];
            if (cfg.SkipLoopFilterUsingFiltError >= 1)
                for (int plane = 0; plane < numPlanes; plane++)
                    zeroFilterSse[plane] = AomSse.SsePlane(sd.Planes[plane], cur.Planes[plane]);

            // chroma: U then V, independent of the luma levels (each search filters its own plane only)
            System.Threading.Tasks.Task? chromaTask = null;
            AomLoopFilterParams? lfChroma = null;
            long[]? chromaSse = null;
            if (numPlanes > 1 && cfg.Parallel)
            {
                lfChroma = lf.Clone();
                chromaSse = new long[3];
                int[] lastLevels = lastFrameFilterLevel.ToArray();
                chromaTask = System.Threading.Tasks.Task.Run(() =>
                {
                    Span<int> last = lastLevels;
                    var lfc = lfChroma;
                    var p1 = cur.Planes[1];
                    var backupUv = new AomYv12Plane(p1.Width, p1.Height, p1.CropWidth, p1.CropHeight, p1.Border);
                    var filterC = new AomLoopFilter();
                    lfc.FilterLevelU = SearchFilterLevel(sd, cur, backupUv, mi, lfc, filterC, cfg, partial, last, 1, 0, out chromaSse[1]);
                    lfc.FilterLevelV = SearchFilterLevel(sd, cur, backupUv, mi, lfc, filterC, cfg, partial, last, 2, 0, out chromaSse[2]);
                });
            }

            var p0 = cur.Planes[0];
            var backupY = new AomYv12Plane(p0.Width, p0.Height, p0.CropWidth, p0.CropHeight, p0.Border, p0.Buf16 != null) { BitDepth = p0.BitDepth };
            lf.FilterLevel[0] = lf.FilterLevel[1] = SearchFilterLevel(sd, cur, backupY, mi, lf, filter, cfg, partial,
                lastFrameFilterLevel, 0, 2, out bestFilterSse[0]);
            if (method != LPF_PICK_FROM_FULL_IMAGE_NON_DUAL)
            {
                lf.FilterLevel[0] = SearchFilterLevel(sd, cur, backupY, mi, lf, filter, cfg, partial,
                    lastFrameFilterLevel, 0, 0, out bestFilterSse[0]);
                lf.FilterLevel[1] = SearchFilterLevel(sd, cur, backupY, mi, lf, filter, cfg, partial,
                    lastFrameFilterLevel, 0, 1, out bestFilterSse[0]);
            }
            if (chromaTask != null)
            {
                chromaTask.Wait();
                lf.FilterLevelU = lfChroma!.FilterLevelU;
                lf.FilterLevelV = lfChroma.FilterLevelV;
                bestFilterSse[1] = chromaSse![1];
                bestFilterSse[2] = chromaSse[2];
            }
            else if (numPlanes > 1)
            {
                var p1 = cur.Planes[1];
                var backupUv = new AomYv12Plane(p1.Width, p1.Height, p1.CropWidth, p1.CropHeight, p1.Border, p1.Buf16 != null) { BitDepth = p1.BitDepth };
                lf.FilterLevelU = SearchFilterLevel(sd, cur, backupUv, mi, lf, filter, cfg, partial,
                    lastFrameFilterLevel, 1, 0, out bestFilterSse[1]);
                lf.FilterLevelV = SearchFilterLevel(sd, cur, backupUv, mi, lf, filter, cfg, partial,
                    lastFrameFilterLevel, 2, 0, out bestFilterSse[2]);
            }
            // (adaptive_luma_loop_filter_skip reads reference frames: inter-only, not ported)
            if (lf.FilterLevel[0] != 0 && lf.FilterLevel[1] != 0 && cfg.SkipLoopFilterUsingFiltError >= 1)
            {
                const double pctImprovementThresh = 2.0;
                bool resetFilterLevelY = true;
                for (int plane = 0; plane < numPlanes; plane++)
                {
                    double pctImprovementSse = (zeroFilterSse[plane] - bestFilterSse[plane]) * 100.0 / zeroFilterSse[plane];
                    resetFilterLevelY &= pctImprovementSse < pctImprovementThresh;
                }
                if (resetFilterLevelY)
                {
                    lf.FilterLevel[0] = 0;
                    lf.FilterLevel[1] = 0;
                }
            }
        }
    }
}
