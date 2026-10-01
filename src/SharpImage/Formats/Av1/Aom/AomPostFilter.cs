using System;

namespace SharpImage.Formats.Av1;

/// <summary>The chosen post-filter parameters of a frame.</summary>
internal sealed class AomPostFilterResult
{
    public required AomLoopFilterParams LoopFilter { get; init; }
    /// <summary>Per plane; null when loop restoration is off for the sequence.</summary>
    public AomRestorationInfo[]? Restoration { get; init; }
    /// <summary>cm->cdef_info; null when CDEF is off for the sequence.</summary>
    public AomCdefInfo? Cdef { get; set; }
}

/// <summary>CdefInfo: the frame's CDEF damping, strength bits and strength presets (luma / chroma, each primary * 4 + secondary).</summary>
internal sealed class AomCdefInfo
{
    public int CdefDamping, CdefBits, NbCdefStrengths = 1;
    public readonly int[] CdefStrengths = new int[8], CdefUvStrengths = new int[8];
}

/// <summary>libaom encoder.c's loopfilter_frame for the all-intra configuration (CDEF off, no superres): pick the
/// deblocking levels, deblock, save the stripe boundary lines (deblocked, then the frame-edge lines "after CDEF"), pick
/// the loop restoration and optionally apply it (avifenc sets AV1E_SET_SKIP_POSTPROC_FILTERING, which skips only the
/// final application: the parameters are chosen either way).</summary>
internal static class AomPostFilter
{
    /// <summary>Stopwatch ticks of the last Run's deblocking (level search + filtering), CDEF (deblocked boundary lines,
    /// search + filter) and restoration (frame-edge boundary lines + search).</summary>
    internal static long LastLpfTicks, LastCdefTicks, LastRstTicks;

    /// <param name="src">the source frame (cpi->source)</param>
    /// <param name="cur">the reconstruction (cm->cur_frame->buf); filtered in place</param>
    /// <param name="mi">the frame's mode info</param>
    /// <param name="lpfCfg">deblocking search inputs</param>
    /// <param name="rstCfg">restoration search inputs; null when restoration is not used</param>
    /// <param name="applyRestoration">apply the chosen restoration to cur</param>
    /// <param name="lf">the frame's struct loopfilter (its levels are overwritten; deltas as the encoder set them)</param>
    public static AomPostFilterResult Run(AomYv12 src, AomYv12 cur, AomLfMiGrid mi, AomLpfPickConfig lpfCfg,
        AomRstPickConfig? rstCfg, bool applyRestoration, AomLoopFilterParams? lf = null,
        Func<AomYv12, AomCdefInfo>? cdefSearch = null, Action<AomYv12, AomCdefInfo>? cdefApply = null)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lf ??= new AomLoopFilterParams();
        var filter = new AomLoopFilter();
        AomPickLpf.PickFilterLevel(src, cur, mi, lf, lpfCfg, filter);
        if (lf.FilterLevel[0] != 0 || lf.FilterLevel[1] != 0)
        {
            if (lpfCfg.Parallel && cur.NumPlanes > 1)
            {
                // the planes filter independently (luma beside chroma, as the MT loop filter's planes; U and V stay
                // together for the joint-chroma opt level)
                var chroma = System.Threading.Tasks.Task.Run(() =>
                    new AomLoopFilter().FilterFrame(cur, mi, lf, 1, cur.NumPlanes, false, lpfCfg.FrameLpfOptLevel));
                filter.FilterFrame(cur, mi, lf, 0, 1, false, lpfCfg.FrameLpfOptLevel);
                chroma.Wait();
            }
            else filter.FilterFrame(cur, mi, lf, 0, cur.NumPlanes, false, lpfCfg.FrameLpfOptLevel);
        }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastLpfTicks = t1 - t0;
        LastRstTicks = LastCdefTicks = 0;

        // cdef_restoration_frame: the deblocked stripe boundaries, the CDEF search and the CDEF filter (avifenc's
        // skip_postproc_filtering skips it when nothing after CDEF reads the frame)
        AomRestorationInfo[]? rst = null;
        if (rstCfg != null)
        {
            rst = new AomRestorationInfo[cur.NumPlanes];
            for (int p = 0; p < rst.Length; p++) rst[p] = new AomRestorationInfo();
            AomRestoration.SaveBoundaryLines(cur, rst, afterCdef: false);
        }
        AomCdefInfo? cdef = null;
        if (cdefSearch != null)
        {
            cdef = cdefSearch(cur);
            if (rstCfg != null || applyRestoration) cdefApply!(cur, cdef);
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastCdefTicks = t2 - t1;
        if (rstCfg == null) return new AomPostFilterResult { LoopFilter = lf, Cdef = cdef };
        AomRestoration.SaveBoundaryLines(cur, rst!, afterCdef: true);
        AomPickRst.PickFilterRestoration(src, cur, rst, rstCfg);
        LastRstTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t2;
        if (applyRestoration && Array.Exists(rst, r => r.FrameRestorationType != AomRestoration.RestoreNone))
            AomRestoration.FilterFrame(cur, rst);
        return new AomPostFilterResult { LoopFilter = lf, Restoration = rst, Cdef = cdef };
    }
}
