using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>WarpedMotionParams (av1/common/mv.h).</summary>
internal sealed class AomWarpedMotionParams
{
    public readonly int[] WmMat = { 0, 0, 1 << WARPEDMODEL_PREC_BITS, 0, 0, 1 << WARPEDMODEL_PREC_BITS };
    public short Alpha, Beta, Gamma, Delta;
    public int WmType = IDENTITY;
    public bool Invalid;

    /// <summary>default_warp_params.</summary>
    public static readonly AomWarpedMotionParams Default = new();

    public void CopyFrom(AomWarpedMotionParams s)
    {
        Array.Copy(s.WmMat, WmMat, 6);
        Alpha = s.Alpha; Beta = s.Beta; Gamma = s.Gamma; Delta = s.Delta;
        WmType = s.WmType; Invalid = s.Invalid;
    }

    public AomWarpedMotionParams Clone() { var w = new AomWarpedMotionParams(); w.CopyFrom(this); return w; }

    public static AomWarpedMotionParams[] NewIdentitySet()
    {
        var a = new AomWarpedMotionParams[REF_FRAMES];
        for (int i = 0; i < a.Length; i++) a[i] = new AomWarpedMotionParams();
        return a;
    }
}

/// <summary>MV_REF: a frame's stored motion for the temporal motion vector projection (8x8 granularity).</summary>
internal struct AomMvRefStore
{
    public AomMv Mv;
    public sbyte RefFrame;
}

/// <summary>RefCntBuffer: a coded frame kept in the reference map (reconstruction with extended borders, its order
/// hints, motion field, CDFs, loop filter deltas and global motion).</summary>
internal sealed class AomRefBuffer
{
    public required AomFrameBuffer Buf;
    public int Width, Height, RenderWidth, RenderHeight;
    public int OrderHint, DisplayOrderHint;
    public readonly int[] RefOrderHints = new int[INTER_REFS_PER_FRAME];
    public readonly int[] RefDisplayOrderHint = new int[INTER_REFS_PER_FRAME];
    public int FrameType;
    public bool Showable;
    public int BaseQindex;
    public int MiRows, MiCols;
    public AomMvRefStore[]? Mvs;
    public readonly sbyte[] RefDeltas = { 1, 0, 0, 0, -1, 0, -1, -1 };
    public readonly sbyte[] ModeDeltas = { 0, 0 };
    public readonly AomWarpedMotionParams[] GlobalMotion = AomWarpedMotionParams.NewIdentitySet();
    public Av1CdfContext? FrameContext;
    public int InterpFilter;
    /// <summary>The frame's pyramid / corner caches for global motion (computed lazily).</summary>
    public object? GmCache;
    public int RefCount;
}
