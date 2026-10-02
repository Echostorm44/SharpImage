using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>INTERINTER_COMPOUND_DATA.</summary>
internal struct AomInterinterCompound
{
    /// <summary>seg_mask: the DIFFWTD mask buffer the block points at (libaom shares the search's buffer).</summary>
    public byte[]? SegMask;
    public sbyte WedgeIndex, WedgeSign;
    public int MaskType;   // DIFFWTD_MASK_TYPE
    public int Type;       // COMPOUND_TYPE
}

/// <summary>MB_MODE_INFO's inter fields.</summary>
internal sealed partial class AomMbModeInfo
{
    public byte NumProjRef, OverlappableNeighbors;
    public readonly AomWarpedMotionParams WmParams = new();
    public int InterintraMode;
    public sbyte InterintraWedgeIndex;
    public AomInterinterCompound InterinterComp;
    public sbyte DeltaLfFromBase;
    public readonly sbyte[] DeltaLf = new sbyte[4];
    public byte SegIdPredicted, RefMvIdx, CompGroupIdx, CompoundIdx, UseWedgeInterintra;

    /// <summary>The y / x filters of interp_filters (int_interpfilters: as_filters { y_filter, x_filter }).</summary>
    public int YFilter => (int)(InterpFilters & 0xffff);
    public int XFilter => (int)(InterpFilters >> 16);

    private void CopyInterFrom(AomMbModeInfo s)
    {
        NumProjRef = s.NumProjRef; OverlappableNeighbors = s.OverlappableNeighbors;
        WmParams.CopyFrom(s.WmParams);
        InterintraMode = s.InterintraMode; InterintraWedgeIndex = s.InterintraWedgeIndex; InterinterComp = s.InterinterComp;
        DeltaLfFromBase = s.DeltaLfFromBase;
        Array.Copy(s.DeltaLf, DeltaLf, 4);
        SegIdPredicted = s.SegIdPredicted; RefMvIdx = s.RefMvIdx; CompGroupIdx = s.CompGroupIdx; CompoundIdx = s.CompoundIdx;
        UseWedgeInterintra = s.UseWedgeInterintra;
    }

    /// <summary>is_inter_block.</summary>
    public bool IsInterBlock => UseIntrabc != 0 || RefFrame0 > INTRA_FRAME;
    /// <summary>has_second_ref.</summary>
    public bool HasSecondRef => RefFrame1 > INTRA_FRAME;
    /// <summary>is_interintra_pred.</summary>
    public bool IsInterintraPred => AomInter.IsInterintraPred(this);
}
