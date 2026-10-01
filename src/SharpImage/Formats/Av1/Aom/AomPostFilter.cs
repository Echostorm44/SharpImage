using System;

namespace SharpImage.Formats.Av1;

/// <summary>The chosen post-filter parameters of a frame.</summary>
internal sealed class AomPostFilterResult
{
    public required AomLoopFilterParams LoopFilter { get; init; }
    /// <summary>Per plane; null when loop restoration is off for the sequence.</summary>
    public AomRestorationInfo[]? Restoration { get; init; }
}

/// <summary>libaom encoder.c's loopfilter_frame for the all-intra configuration (CDEF off, no superres): pick the
/// deblocking levels, deblock, save the stripe boundary lines (deblocked, then the frame-edge lines "after CDEF"), pick
/// the loop restoration and optionally apply it (avifenc sets AV1E_SET_SKIP_POSTPROC_FILTERING, which skips only the
/// final application: the parameters are chosen either way).</summary>
internal static class AomPostFilter
{
    /// <summary>Stopwatch ticks of the last Run's deblocking (level search + filtering) and restoration search.</summary>
    internal static long LastLpfTicks, LastRstTicks;

    /// <param name="src">the source frame (cpi->source)</param>
    /// <param name="cur">the reconstruction (cm->cur_frame->buf); filtered in place</param>
    /// <param name="mi">the frame's mode info</param>
    /// <param name="lpfCfg">deblocking search inputs</param>
    /// <param name="rstCfg">restoration search inputs; null when restoration is not used</param>
    /// <param name="applyRestoration">apply the chosen restoration to cur</param>
    /// <param name="lf">the frame's struct loopfilter (its levels are overwritten; deltas as the encoder set them)</param>
    public static AomPostFilterResult Run(AomYv12 src, AomYv12 cur, AomLfMiGrid mi, AomLpfPickConfig lpfCfg,
        AomRstPickConfig? rstCfg, bool applyRestoration, AomLoopFilterParams? lf = null)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lf ??= new AomLoopFilterParams();
        var filter = new AomLoopFilter();
        AomPickLpf.PickFilterLevel(src, cur, mi, lf, lpfCfg, filter);
        if (lf.FilterLevel[0] != 0 || lf.FilterLevel[1] != 0) filter.FilterFrame(cur, mi, lf, 0, cur.NumPlanes, false, lpfCfg.FrameLpfOptLevel);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastLpfTicks = t1 - t0;
        LastRstTicks = 0;
        if (rstCfg == null) return new AomPostFilterResult { LoopFilter = lf };

        var rst = new AomRestorationInfo[cur.NumPlanes];
        for (int p = 0; p < rst.Length; p++) rst[p] = new AomRestorationInfo();
        AomRestoration.SaveBoundaryLines(cur, rst, afterCdef: false);
        AomRestoration.SaveBoundaryLines(cur, rst, afterCdef: true);
        AomPickRst.PickFilterRestoration(src, cur, rst, rstCfg);
        LastRstTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t1;
        if (applyRestoration && Array.Exists(rst, r => r.FrameRestorationType != AomRestoration.RestoreNone))
            AomRestoration.FilterFrame(cur, rst);
        return new AomPostFilterResult { LoopFilter = lf, Restoration = rst };
    }
}
