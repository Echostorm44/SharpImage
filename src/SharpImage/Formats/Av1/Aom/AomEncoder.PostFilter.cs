using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    /// <summary>The frame's chosen deblocking levels and loop restoration (after <see cref="AomEncoder.RunPostFilter"/>).</summary>
    public AomPostFilterResult? PostFilter;
    /// <summary>seq_params->enable_restoration (the all-intra speed features may clear it).</summary>
    public bool EnableRestoration = true;
}

// encoder.c's loopfilter_frame for the all-intra key frame (CDEF off): the deblocking level search and deblocking,
// then the loop restoration search, on the reconstruction the final encode left; the restoration is chosen but not
// applied (avifenc's AV1E_SET_SKIP_POSTPROC_FILTERING), exactly like libaom's avifenc configuration.
internal static partial class AomEncoder
{
    internal static AomPostFilterResult RunPostFilter(AomComp cpi, AomMacroblock x, bool applyRestoration = false)
    {
        var cm = cpi.Cm;
        if (cpi.AllowIntrabc)
        {
            // set_postproc_filter_default_params; loopfilter_frame is not run for frames coded with intrabc
            return cpi.PostFilter = new AomPostFilterResult { LoopFilter = new AomLoopFilterParams(), Restoration = null };
        }
        int np = cm.NumPlanes;
        var src = ToYv12(cpi.Source, cm);
        var cur = ToYv12(cm.CurFrame, cm);

        var mi = new AomLfMiGrid(cm.MiRows, cm.MiCols);
        for (int r = 0; r < cm.MiRows; r++)
            for (int c = 0; c < cm.MiCols; c++)
            {
                var m = cm.MiGridBase[r * cm.MiStride + c]!;
                int i = r * cm.MiCols + c;
                mi.Coded[i] = true;
                mi.Bsize[i] = (byte)m.Bsize;
                mi.TxSize[i] = (byte)m.TxSize;
                mi.Skip[i] = m.SkipTxfm != 0;
                mi.RefFrame0[i] = 0;   // INTRA_FRAME
                mi.Mode[i] = (byte)m.Mode;
                mi.SegmentId[i] = m.SegmentId;
                mi.IsInter[i] = m.UseIntrabc != 0;
            }
        for (int s = 0; s < 8; s++) mi.LosslessSeg[s] = x.E.Lossless[s] != 0;

        // set_default_lf_deltas (key frame)
        var lf = new AomLoopFilterParams { ModeRefDeltaEnabled = true };
        int[] refDeltas = { 1, 0, 0, 0, -1, 0, -1, -1 };   // INTRA, LAST, LAST2, LAST3, GOLDEN, BWDREF, ALTREF2, ALTREF
        for (int i = 0; i < 8; i++) lf.RefDeltas[i] = (sbyte)refDeltas[i];
        lf.ModeDeltas[0] = 0;
        lf.ModeDeltas[1] = 0;

        var sf = cpi.Sf;
        var lpfCfg = new AomLpfPickConfig
        {
            Method = sf.lpf_sf.lpf_pick, UseCoarseFilterLevelSearch = sf.lpf_sf.use_coarse_filter_level_search,
            SkipLoopFilterUsingFiltError = sf.lpf_sf.skip_loop_filter_using_filt_error, Sharpness = cpi.Sharpness,
            SharpnessFromConfig = cpi.AllIntra || cpi.TuneIq, EnableAdaptiveSharpness = cpi.EnableAdaptiveSharpness, BaseQindex = cm.BaseQindex, KeyFrame = true, IntraOnly = true,
            TxModeOnly4x4 = x.E.Lossless[0] != 0,
        };
        // is_inter_tx_size_search_level_one / get_lpf_opt_level
        bool txLevelOne = sf.tx_sf.inter_tx_size_search_init_depth_rect >= 1 && sf.tx_sf.inter_tx_size_search_init_depth_sqr >= 1;
        lpfCfg.SearchLpfOptLevel = txLevelOne ? 1 : 0;
        lpfCfg.FrameLpfOptLevel = txLevelOne ? (sf.lpf_sf.lpf_pick == LPF_PICK_FROM_Q ? 2 : 1) : 0;

        AomRstPickConfig? rstCfg = null;
        if (cpi.EnableRestoration && x.E.Lossless[0] == 0)
        {
            rstCfg = new AomRstPickConfig { Rdmult = cpi.RdRdmult, BaseQindex = cm.BaseQindex, SbSize = cm.SbSize };
            rstCfg.SetSpeedFeatures(sf.lpf_sf);
            // av1_fill_lr_rates from the tile's CDFs
            AomModeCostFill.FillLr(x.ModeCosts, x.TileCtx);
            x.ModeCosts.SwitchableRestoreCost.CopyTo(rstCfg.SwitchableRestoreCost, 0);
            x.ModeCosts.WienerRestoreCost.CopyTo(rstCfg.WienerRestoreCost, 0);
            x.ModeCosts.SgrprojRestoreCost.CopyTo(rstCfg.SgrprojRestoreCost, 0);
        }
        // is_cdef_used: seq enable_cdef (cdef_control != CDEF_NONE) and not coded lossless
        bool useCdef = cpi.CdefControl != 0 && x.E.Lossless[0] == 0;
        var result = AomPostFilter.Run(src, cur, mi, lpfCfg, rstCfg, applyRestoration, lf,
            useCdef ? f => AomCdef.Search(cpi, f, src, cpi.RdRdmult) : null, useCdef ? (f, ci) => AomCdef.Frame(cpi, f, ci) : null);
        cpi.PostFilter = result;
        // the filtered reconstruction back into the frame buffer
        for (int p = 0; p < np; p++)
        {
            int isUv = p > 0 ? 1 : 0;
            int w = (((cm.Width + 7) & ~7) >> (isUv != 0 ? cm.SsX : 0)), h = (((cm.Height + 7) & ~7) >> (isUv != 0 ? cm.SsY : 0));
            var pl = cur.Planes[p];
            for (int r = 0; r < h; r++)
                Array.Copy(pl.Buf, pl.At(0, r), cm.CurFrame.Buffers[p], cm.CurFrame.Offsets[p] + r * cm.CurFrame.Strides[p], w);
        }
        return result;
    }

    /// <summary>The frame's 8-aligned area as an AomYv12 (the post filters' frame type).</summary>
    private static AomYv12 ToYv12(AomFrameBuffer fb, AomCommon cm)
    {
        var y = new AomYv12(cm.Width, cm.Height, cm.SsX, cm.SsY, cm.NumPlanes);
        for (int p = 0; p < cm.NumPlanes; p++)
        {
            var pl = y.Planes[p];
            for (int r = 0; r < pl.Height; r++)
                Array.Copy(fb.Buffers[p], fb.Offsets[p] + r * fb.Strides[p], pl.Buf, pl.At(0, r), pl.Width);
        }
        return y;
    }
}
