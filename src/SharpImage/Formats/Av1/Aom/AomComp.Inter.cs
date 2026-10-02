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
