using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>TPL_MV_REF: a projected motion vector of the motion field (8x8 units).</summary>
internal struct AomTplMvRef
{
    public AomMv Mfmv0;
    public sbyte RefFrameOffset;
}

/// <summary>AV1_COMMON's inter-frame state (current_frame, features, the reference buffers and scale factors, global
/// motion, sign bias / side, the motion field).</summary>
internal sealed partial class AomCommon
{
    public int FrameType = KEY_FRAME;
    public bool ShowFrame = true;
    public int OrderHint, DisplayOrderHint;
    public bool EnableOrderHint;
    public int OrderHintBits = DEFAULT_EXPLICIT_ORDER_HINT_BITS;
    /// <summary>get_ref_frame_buf(cm, ref) for LAST_FRAME..ALTREF_FRAME (index by ref frame; [0] unused).</summary>
    public readonly AomRefBuffer?[] RefBufs = new AomRefBuffer?[REF_FRAMES];
    /// <summary>cm->ref_scale_factors per reference (index by ref frame).</summary>
    public readonly AomScaleFactors?[] RefScaleFactors = new AomScaleFactors?[REF_FRAMES];
    public readonly AomWarpedMotionParams[] GlobalMotion = AomWarpedMotionParams.NewIdentitySet();
    public readonly int[] RefFrameSignBias = new int[REF_FRAMES];
    public readonly sbyte[] RefFrameSide = new sbyte[REF_FRAMES];
    /// <summary>cm->tpl_mvs ((mi_rows + MAX_MIB_SIZE) / 2 x mi_stride / 2).</summary>
    public AomTplMvRef[]? TplMvs;
    /// <summary>cm->cur_frame->mvs (the frame's motion stored for later frames' projection).</summary>
    public AomMvRefStore[]? CurFrameMvs;
    public readonly int[] CurRefOrderHints = new int[INTER_REFS_PER_FRAME];
    // cm->features
    public bool AllowRefFrameMvs, AllowHighPrecisionMv, CurFrameForceIntegerMv, AllowWarpedMotion, SwitchableMotionMode;
    public int InterpFilter = SWITCHABLE;
    public int ReferenceMode = SINGLE_REFERENCE;
    public bool SkipModeAllowed, SkipModeFlag;
    public int SkipModeRefFrame0, SkipModeRefFrame1;

    public bool FrameIsIntraOnly => FrameType == KEY_FRAME || FrameType == INTRA_ONLY_FRAME;
    public int RelativeDist(int a, int b) => AomInter.GetRelativeDist(EnableOrderHint, OrderHintBits, a, b);
}
