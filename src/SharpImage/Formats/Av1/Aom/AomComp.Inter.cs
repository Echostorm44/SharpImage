using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>AV1_COMP's inter-frame state: the scaled references (av1_scale_references), cpi->is_screen_content_type,
/// the reference frame flags.</summary>
internal sealed partial class AomComp
{
    /// <summary>cpi->scaled_ref_buf[ref - 1] when it differs from the reference itself (index by ref frame).</summary>
    public readonly AomFrameBuffer?[] ScaledRefBufs = new AomFrameBuffer?[REF_FRAMES];
    public bool IsScreenContentType;
    /// <summary>cpi->ref_frame_flags (AOM_LAST_FLAG ...).</summary>
    public int RefFrameFlags;

    /// <summary>av1_get_scaled_ref_frame.</summary>
    public AomFrameBuffer? GetScaledRefFrame(int refFrame) => ScaledRefBufs[refFrame];

    /// <summary>x->sb_enc.tpl_data_count (no TPL stats yet: 0).</summary>
    public int TplDataCount(AomMacroblock x) => x.TplDataCount;

    public AomSubpelMsParams SubpelParamsScratch(AomMacroblock x) => x.SubpelParams;
}

internal sealed partial class AomMacroblock
{
    public int TplDataCount;
    public readonly AomSubpelMsParams SubpelParams = new();
}

/// <summary>cpi->ppi->frame_probs (FrameProbInfo) without tx_type_probs (AomComp.TxTypeProbs): the obmc / warped /
/// switchable interpolation filter probabilities, carried across frames.</summary>
internal sealed class AomFrameProbs
{
    public readonly int[] ObmcProbs = new int[FRAME_UPDATE_TYPES * BLOCK_SIZES_ALL];
    public readonly int[] WarpedProbs = new int[FRAME_UPDATE_TYPES];
    public readonly int[] SwitchableInterpProbs = new int[FRAME_UPDATE_TYPES * SWITCHABLE_FILTER_CONTEXTS * SWITCHABLE_FILTERS];
}

internal sealed partial class AomComp
{
    public AomFrameProbs FrameProbs = new();

    /// <summary>frame_probs.switchable_interp_probs[update_type][ctx][filter].</summary>
    public int SwitchableInterpProb(int updateType, int ctx, int filter)
        => FrameProbs.SwitchableInterpProbs[(updateType * SWITCHABLE_FILTER_CONTEXTS + ctx) * SWITCHABLE_FILTERS + filter];

    /// <summary>copy_frame_prob_info (key frames; with extra_prune_warped also golden refreshes).</summary>
    public void CopyFrameProbInfo()
    {
        if (Sf.tx_sf.tx_type_search.prune_tx_type_using_stats != 0) Array.Copy(DefaultTxTypeProbs, TxTypeProbs, TxTypeProbs.Length);
        if (Sf.inter_sf.prune_obmc_prob_thresh > 0 && Sf.inter_sf.prune_obmc_prob_thresh < int.MaxValue)
            Array.Copy(DefaultObmcProbs, FrameProbs.ObmcProbs, DefaultObmcProbs.Length);
        if (Sf.inter_sf.prune_warped_prob_thresh > 0) Array.Copy(DefaultWarpedProbs, FrameProbs.WarpedProbs, DefaultWarpedProbs.Length);
        if (Sf.interp_sf.adaptive_interp_filter_search == 2)
            Array.Copy(DefaultSwitchableInterpProbs, FrameProbs.SwitchableInterpProbs, DefaultSwitchableInterpProbs.Length);
    }
}
