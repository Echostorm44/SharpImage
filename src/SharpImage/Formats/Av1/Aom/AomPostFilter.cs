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
    public int CdefDamping = 3, CdefBits, NbCdefStrengths = 1;
    public readonly int[] CdefStrengths = new int[8], CdefUvStrengths = new int[8];
}

/// <summary>libaom encoder.c's loopfilter_frame for the all-intra configuration (CDEF off, no superres): pick the
/// deblocking levels, deblock, save the stripe boundary lines (deblocked, then the frame-edge lines "after CDEF"), pick
/// the loop restoration and optionally apply it (avifenc sets AV1E_SET_SKIP_POSTPROC_FILTERING, which skips only the
/// final application: the parameters are chosen either way).</summary>
internal static class AomPostFilter
{
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
        lf ??= new AomLoopFilterParams();
        var filter = new AomLoopFilter();
        AomPickLpf.PickFilterLevel(src, cur, mi, lf, lpfCfg, filter);
        if (lf.FilterLevel[0] != 0 || lf.FilterLevel[1] != 0) filter.FilterFrame(cur, mi, lf, 0, cur.NumPlanes);
        if (rstCfg == null) return new AomPostFilterResult { LoopFilter = lf };

        var rst = new AomRestorationInfo[cur.NumPlanes];
        for (int p = 0; p < rst.Length; p++) rst[p] = new AomRestorationInfo();
        AomRestoration.SaveBoundaryLines(cur, rst, afterCdef: false);
        AomRestoration.SaveBoundaryLines(cur, rst, afterCdef: true);
        AomPickRst.PickFilterRestoration(src, cur, rst, rstCfg);
        if (applyRestoration && Array.Exists(rst, r => r.FrameRestorationType != AomRestoration.RestoreNone))
            AomRestoration.FilterFrame(cur, rst);
        return new AomPostFilterResult { LoopFilter = lf, Restoration = rst };
    }
}
