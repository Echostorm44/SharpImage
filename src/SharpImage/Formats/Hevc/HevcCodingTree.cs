// HEVC Coding Tree Structures
// CTU (Coding Tree Unit) -> CU (Coding Unit) -> PU (Prediction Unit) -> TU (Transform Unit)

using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// HEVC prediction unit partition modes.
/// These define how a CU is split for prediction.
/// </summary>
public enum HevcPartitionMode : byte
{
    /// <summary>Full CU, no partitioning (2N x 2N).</summary>
    Part2Nx2N = 0,
    
    /// <summary>Two horizontal partitions (2N x N).</summary>
    Part2NxN = 1,
    
    /// <summary>Two vertical partitions (N x 2N).</summary>
    PartNx2N = 2,
    
    /// <summary>Four equal partitions (N x N) - inter only.</summary>
    PartNxN = 3,
    
    /// <summary>Asymmetric horizontal: narrow top (2N x nU).</summary>
    Part2NxnU = 4,
    
    /// <summary>Asymmetric horizontal: narrow bottom (2N x nD).</summary>
    Part2NxnD = 5,
    
    /// <summary>Asymmetric vertical: narrow left (nL x 2N).</summary>
    PartnLx2N = 6,
    
    /// <summary>Asymmetric vertical: narrow right (nR x 2N).</summary>
    PartnRx2N = 7
}

/// <summary>
/// HEVC prediction mode for a CU.
/// </summary>
public enum HevcPredictionMode : byte
{
    /// <summary>Intra prediction (uses spatial neighbors).</summary>
    Intra = 0,
    
    /// <summary>Inter prediction (uses temporal references).</summary>
    Inter = 1,
    
    /// <summary>Skip mode (no residual, merge mode only).</summary>
    Skip = 2
}

/// <summary>
/// HEVC intra prediction modes (35 angular modes).
/// </summary>
public enum HevcIntraPredictionMode : byte
{
    Planar = 0,
    Dc = 1,
    Angular2 = 2,
    Angular3 = 3,
    Angular4 = 4,
    Angular5 = 5,
    Angular6 = 6,
    Angular7 = 7,
    Angular8 = 8,
    Angular9 = 9,
    Angular10 = 10,  // Horizontal
    Angular11 = 11,
    Angular12 = 12,
    Angular13 = 13,
    Angular14 = 14,
    Angular15 = 15,
    Angular16 = 16,
    Angular17 = 17,
    Angular18 = 18,
    Angular19 = 19,
    Angular20 = 20,
    Angular21 = 21,
    Angular22 = 22,
    Angular23 = 23,
    Angular24 = 24,
    Angular25 = 25,
    Angular26 = 26,  // Vertical
    Angular27 = 27,
    Angular28 = 28,
    Angular29 = 29,
    Angular30 = 30,
    Angular31 = 31,
    Angular32 = 32,
    Angular33 = 33,
    Angular34 = 34
}

/// <summary>
/// A transform unit in the HEVC transform tree.
/// Leaf node containing residual coefficient data.
/// </summary>
public sealed class HevcTransformUnit
{
    /// <summary>X position in luma samples.</summary>
    public int X { get; set; }
    
    /// <summary>Y position in luma samples.</summary>
    public int Y { get; set; }
    
    /// <summary>Log2 of transform block size (2=4x4, 3=8x8, 4=16x16, 5=32x32).</summary>
    public byte Log2Size { get; set; }
    
    /// <summary>Transform block size in samples.</summary>
    public int Size => 1 << Log2Size;
    
    /// <summary>Whether coded block flag is set for luma (cbf_luma).</summary>
    public bool CbfLuma { get; set; }
    
    /// <summary>Whether coded block flag is set for Cb chroma.</summary>
    public bool CbfCb { get; set; }
    
    /// <summary>Whether coded block flag is set for Cr chroma.</summary>
    public bool CbfCr { get; set; }
    
    /// <summary>Whether this TU contains any coded residual data.</summary>
    public bool HasResidual => CbfLuma || CbfCb || CbfCr;
    
    /// <summary>QP delta for this TU (if cu_qp_delta_enabled_flag).</summary>
    public sbyte QpDelta { get; set; }
    
    /// <summary>Transform skip flag for luma.</summary>
    public bool TransformSkipLuma { get; set; }
    
    /// <summary>Transform skip flag for chroma.</summary>
    public bool TransformSkipChroma { get; set; }
    
    // Luma residual coefficients (after entropy decode, before inverse transform)
    public short[]? LumaCoefficients { get; set; }
    
    // Chroma residual coefficients
    public short[]? CbCoefficients { get; set; }
    public short[]? CrCoefficients { get; set; }
}

/// <summary>
/// A transform tree within a coding unit.
/// Can be recursively split into smaller transform units.
/// </summary>
public sealed class HevcTransformTree
{
    /// <summary>X position in luma samples.</summary>
    public int X { get; set; }
    
    /// <summary>Y position in luma samples.</summary>
    public int Y { get; set; }
    
    /// <summary>Log2 of transform tree block size.</summary>
    public byte Log2Size { get; set; }
    
    /// <summary>Current depth in the transform tree.</summary>
    public byte Depth { get; set; }
    
    /// <summary>Whether this node is split into 4 children.</summary>
    public bool SplitTransformFlag { get; set; }
    
    /// <summary>Child transform trees (4 if split, otherwise null).</summary>
    public HevcTransformTree[]? Children { get; set; }
    
    /// <summary>Leaf transform unit (if not split).</summary>
    public HevcTransformUnit? TransformUnit { get; set; }
    
    /// <summary>Returns true if this is a leaf node.</summary>
    public bool IsLeaf => !SplitTransformFlag;
    
    /// <summary>Transform tree block size in samples.</summary>
    public int Size => 1 << Log2Size;
}

/// <summary>
/// A prediction unit within a coding unit.
/// </summary>
public sealed class HevcPredictionUnit
{
    /// <summary>X position in luma samples.</summary>
    public int X { get; set; }
    
    /// <summary>Y position in luma samples.</summary>
    public int Y { get; set; }
    
    /// <summary>Width in luma samples.</summary>
    public int Width { get; set; }
    
    /// <summary>Height in luma samples.</summary>
    public int Height { get; set; }
    
    /// <summary>Prediction mode (Intra, Inter, Skip).</summary>
    public HevcPredictionMode PredictionMode { get; set; }
    
    // Intra prediction fields
    
    /// <summary>Intra prediction mode for luma (35 modes).</summary>
    public HevcIntraPredictionMode IntraModeY { get; set; }
    
    /// <summary>Intra prediction mode for chroma.</summary>
    public byte IntraModeC { get; set; }
    
    // Inter prediction fields
    
    /// <summary>Merge flag (uses neighbor's motion info).</summary>
    public bool MergeFlag { get; set; }
    
    /// <summary>Merge index if MergeFlag is true.</summary>
    public byte MergeIndex { get; set; }
    
    /// <summary>Inter prediction direction (L0, L1, or Bi).</summary>
    public byte InterDirection { get; set; }
    
    /// <summary>Reference index for L0.</summary>
    public sbyte RefIdxL0 { get; set; }
    
    /// <summary>Reference index for L1.</summary>
    public sbyte RefIdxL1 { get; set; }
    
    /// <summary>Motion vector X for L0 (in quarter-pel units).</summary>
    public short MvL0X { get; set; }
    
    /// <summary>Motion vector Y for L0 (in quarter-pel units).</summary>
    public short MvL0Y { get; set; }
    
    /// <summary>Motion vector X for L1 (in quarter-pel units).</summary>
    public short MvL1X { get; set; }
    
    /// <summary>Motion vector Y for L1 (in quarter-pel units).</summary>
    public short MvL1Y { get; set; }
}

/// <summary>
/// A coding unit in the HEVC coding tree.
/// A CU is a leaf node in the CTU's quad-tree structure.
/// </summary>
public sealed class HevcCodingUnit
{
    /// <summary>X position in luma samples.</summary>
    public int X { get; set; }
    
    /// <summary>Y position in luma samples.</summary>
    public int Y { get; set; }
    
    /// <summary>Log2 of CU size (3=8x8, 4=16x16, 5=32x32, 6=64x64).</summary>
    public byte Log2Size { get; set; }
    
    /// <summary>Depth in the CTU quad-tree (0 = CTU level).</summary>
    public byte Depth { get; set; }
    
    /// <summary>CU size in luma samples.</summary>
    public int Size => 1 << Log2Size;
    
    /// <summary>Prediction mode for this CU.</summary>
    public HevcPredictionMode PredictionMode { get; set; }
    
    /// <summary>Partition mode for prediction units.</summary>
    public HevcPartitionMode PartitionMode { get; set; }
    
    /// <summary>Whether cu_transquant_bypass_flag is set.</summary>
    public bool TransquantBypass { get; set; }
    
    /// <summary>PCM flag (uses raw samples instead of prediction+residual).</summary>
    public bool PcmFlag { get; set; }
    
    /// <summary>Prediction units for this CU (1-4 based on PartitionMode).</summary>
    public HevcPredictionUnit[]? PredictionUnits { get; set; }
    
    /// <summary>Transform tree for residual coding.</summary>
    public HevcTransformTree? TransformTree { get; set; }
    
    /// <summary>QP value for this CU.</summary>
    public sbyte Qp { get; set; }
    
    /// <summary>Number of prediction units based on PartitionMode.</summary>
    public int PredictionUnitCount => PartitionMode switch
    {
        HevcPartitionMode.Part2Nx2N => 1,
        HevcPartitionMode.PartNxN => 4,
        _ => 2
    };
    
    /// <summary>Returns true if this CU uses intra prediction.</summary>
    public bool IsIntra => PredictionMode == HevcPredictionMode.Intra;
    
    /// <summary>Returns true if this CU uses inter prediction.</summary>
    public bool IsInter => PredictionMode == HevcPredictionMode.Inter || 
                           PredictionMode == HevcPredictionMode.Skip;
}

/// <summary>
/// A coding tree unit (CTU) - the largest coding block in HEVC.
/// CTU is a quad-tree that recursively splits into smaller coding units.
/// </summary>
public sealed class HevcCodingTreeUnit
{
    /// <summary>X position in luma samples.</summary>
    public int X { get; set; }
    
    /// <summary>Y position in luma samples.</summary>
    public int Y { get; set; }
    
    /// <summary>CTU address in raster scan order.</summary>
    public int Address { get; set; }
    
    /// <summary>Log2 of CTU size (typically 6 for 64x64).</summary>
    public byte Log2Size { get; set; }
    
    /// <summary>CTU size in luma samples (16, 32, or 64).</summary>
    public int Size => 1 << Log2Size;
    
    /// <summary>Whether this CTU node is split into 4 children.</summary>
    public bool SplitCuFlag { get; set; }
    
    /// <summary>Child CTU nodes (4 if split, otherwise null).</summary>
    public HevcCodingTreeUnit[]? Children { get; set; }
    
    /// <summary>Leaf coding unit (if not split).</summary>
    public HevcCodingUnit? CodingUnit { get; set; }
    
    /// <summary>Returns true if this is a leaf node (no further splitting).</summary>
    public bool IsLeaf => !SplitCuFlag;
    
    /// <summary>SAO parameters for this CTU (if SAO enabled).</summary>
    public HevcSaoParameters? SaoLuma { get; set; }
    
    /// <summary>SAO parameters for chroma.</summary>
    public HevcSaoParameters? SaoChroma { get; set; }
    
    /// <summary>
    /// Iterates all leaf coding units in this CTU tree.
    /// </summary>
    public IEnumerable<HevcCodingUnit> GetAllCodingUnits()
    {
        if (IsLeaf)
        {
            if (CodingUnit != null)
                yield return CodingUnit;
        }
        else if (Children != null)
        {
            foreach (var child in Children)
            {
                foreach (var cu in child.GetAllCodingUnits())
                    yield return cu;
            }
        }
    }
}

/// <summary>
/// SAO (Sample Adaptive Offset) parameters for a CTU.
/// New in HEVC - per-CTU adaptive filtering.
/// </summary>
public sealed class HevcSaoParameters
{
    /// <summary>SAO merge flag for left neighbor.</summary>
    public bool MergeLeftFlag { get; set; }
    
    /// <summary>SAO merge flag for up neighbor.</summary>
    public bool MergeUpFlag { get; set; }
    
    /// <summary>SAO type index (0=off, 1=band, 2=edge).</summary>
    public byte SaoTypeIdx { get; set; }
    
    /// <summary>SAO band position (for band offset type).</summary>
    public byte SaoBandPosition { get; set; }
    
    /// <summary>SAO offset values (4 per component).</summary>
    public sbyte[] SaoOffset { get; set; } = new sbyte[4];
    
    /// <summary>Edge offset class (0-3 for edge type).</summary>
    public byte SaoEoClass { get; set; }
    
    /// <summary>Returns true if SAO is disabled for this CTU.</summary>
    public bool IsOff => SaoTypeIdx == 0;
    
    /// <summary>Returns true if this is band offset mode.</summary>
    public bool IsBandOffset => SaoTypeIdx == 1;
    
    /// <summary>Returns true if this is edge offset mode.</summary>
    public bool IsEdgeOffset => SaoTypeIdx == 2;
}

/// <summary>
/// Represents a complete HEVC slice decoded into CTU trees.
/// </summary>
public sealed class HevcSliceData
{
    /// <summary>Slice header.</summary>
    public HevcSliceSegmentHeader Header { get; set; } = null!;
    
    /// <summary>CTU array for this slice (in raster scan order).</summary>
    public HevcCodingTreeUnit[] CodingTreeUnits { get; set; } = [];
    
    /// <summary>Number of CTUs in this slice.</summary>
    public int CtuCount => CodingTreeUnits.Length;
    
    /// <summary>Picture order count (full, not just LSB).</summary>
    public int PictureOrderCount { get; set; }
}
