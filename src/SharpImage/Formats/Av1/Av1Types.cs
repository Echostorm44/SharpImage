// AV1 decoder types and enumerations
// Ported from dav1d: src/levels.h, include/dav1d/headers.h, src/env.h
// Reference: AV1 Bitstream & Decoding Process Specification v1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Av1;

// ============================================================================
// Constants
// ============================================================================

/// <summary>
/// AV1 decoder constants from the spec and dav1d.
/// </summary>
public static class Av1Constants
{
    /// <summary>Maximum number of CDEF filter strengths.</summary>
    public const int MaxCdefStrengths = 8;

    /// <summary>Maximum number of operating points in a sequence.</summary>
    public const int MaxOperatingPoints = 32;

    /// <summary>Maximum number of tile columns.</summary>
    public const int MaxTileCols = 64;

    /// <summary>Maximum number of tile rows.</summary>
    public const int MaxTileRows = 64;

    /// <summary>Maximum number of segments.</summary>
    public const int MaxSegments = 8;

    /// <summary>Total number of reference frame buffer slots.</summary>
    public const int NumRefFrames = 8;

    /// <summary>Sentinel value indicating no primary reference frame.</summary>
    public const int PrimaryRefNone = 7;

    /// <summary>Number of reference frames used per inter frame (LAST..ALTREF).</summary>
    public const int RefsPerFrame = 7;

    /// <summary>Total references per frame including INTRA_FRAME.</summary>
    public const int TotalRefsPerFrame = RefsPerFrame + 1;

    /// <summary>Range of base quantizer index values (0..255).</summary>
    public const int QIndexRange = 256;

    /// <summary>Number of block sizes in the AV1 block size enum.</summary>
    public const int NumBlockSizes = 22;

    /// <summary>Maximum superblock dimension (128x128 mode).</summary>
    public const int MaxSuperblockSize = 128;

    /// <summary>Minimum block dimension (4x4).</summary>
    public const int MinBlockSize = 4;

    /// <summary>Number of 4x4 blocks per superblock edge in 128x128 mode.</summary>
    public const int MaxMib = MaxSuperblockSize / MinBlockSize; // 32

    /// <summary>Maximum number of planes.</summary>
    public const int MaxPlanes = 3;

    /// <summary>Number of palette colors.</summary>
    public const int MaxPaletteSize = 8;

    /// <summary>Number of warp model parameters.</summary>
    public const int WarpModelParams = 6;

    /// <summary>Number of intra prediction modes (13: DC through Paeth).</summary>
    public const int NumIntraPredModes = 13;

    /// <summary>Number of UV intra prediction modes (14: 13 base + CFL).</summary>
    public const int NumUvIntraPredModes = 14;
}

// ============================================================================
// Enumerations — Transform
// ============================================================================

/// <summary>
/// AV1 square transform sizes.
/// Maps to dav1d TxfmSize enum (levels.h).
/// </summary>
public enum Av1TxSize : byte
{
    Tx4x4 = 0,
    Tx8x8 = 1,
    Tx16x16 = 2,
    Tx32x32 = 3,
    Tx64x64 = 4,
    Count = 5
}

/// <summary>
/// AV1 rectangular transform sizes (extending Av1TxSize).
/// Maps to dav1d RectTxfmSize enum (levels.h).
/// Values continue from Av1TxSize.Count.
/// </summary>
public enum Av1RectTxSize : byte
{
    Rtx4x8 = Av1TxSize.Count,    // 5
    Rtx8x4 = 6,
    Rtx8x16 = 7,
    Rtx16x8 = 8,
    Rtx16x32 = 9,
    Rtx32x16 = 10,
    Rtx32x64 = 11,
    Rtx64x32 = 12,
    Rtx4x16 = 13,
    Rtx16x4 = 14,
    Rtx8x32 = 15,
    Rtx32x8 = 16,
    Rtx16x64 = 17,
    Rtx64x16 = 18,
    Count = 19
}

/// <summary>
/// AV1 transform type (row × column factorization).
/// Maps to dav1d TxfmType enum (levels.h).
/// </summary>
public enum Av1TxType : byte
{
    DctDct = 0,
    AdstDct = 1,
    DctAdst = 2,
    AdstAdst = 3,
    FlipAdstDct = 4,
    DctFlipAdst = 5,
    FlipAdstFlipAdst = 6,
    AdstFlipAdst = 7,
    FlipAdstAdst = 8,
    Identity = 9,
    VDct = 10,
    HDct = 11,
    VAdst = 12,
    HAdst = 13,
    VFlipAdst = 14,
    HFlipAdst = 15,
    Count = 16,
    WhtWht = Count,
    CountPlusLl = 17
}

/// <summary>
/// AV1 transform class (2D, horizontal, vertical).
/// Maps to dav1d TxClass enum (levels.h).
/// </summary>
public enum Av1TxClass : byte
{
    TwoD = 0,
    Horizontal = 1,
    Vertical = 2
}

/// <summary>
/// AV1 transform mode (how transform size is chosen).
/// Maps to dav1d Dav1dTxfmMode enum (headers.h).
/// </summary>
public enum Av1TxfmMode : byte
{
    Only4x4 = 0,
    Largest = 1,
    Switchable = 2,
    Count = 3
}

// ============================================================================
// Enumerations — Block / Partition
// ============================================================================

/// <summary>
/// AV1 block level in the partition tree (superblock → 8x8).
/// Maps to dav1d BlockLevel enum (levels.h).
/// </summary>
public enum Av1BlockLevel : byte
{
    Bl128x128 = 0,
    Bl64x64 = 1,
    Bl32x32 = 2,
    Bl16x16 = 3,
    Bl8x8 = 4,
    Count = 5
}

/// <summary>
/// AV1 block partition types.
/// Maps to dav1d BlockPartition enum (levels.h).
/// </summary>
public enum Av1BlockPartition : byte
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Split = 3,
    TopSplit = 4,       // split top, horizontal bottom
    BottomSplit = 5,    // horizontal top, split bottom
    LeftSplit = 6,      // split left, vertical right
    RightSplit = 7,     // vertical left, split right
    Horizontal4 = 8,
    Vertical4 = 9,
    Count = 10,
    NumSub8x8 = TopSplit
}

/// <summary>
/// AV1 block sizes.
/// Maps to dav1d BlockSize enum (levels.h).
/// Ordered from largest to smallest.
/// </summary>
public enum Av1BlockSize : byte
{
    Bs128x128 = 0,
    Bs128x64 = 1,
    Bs64x128 = 2,
    Bs64x64 = 3,
    Bs64x32 = 4,
    Bs64x16 = 5,
    Bs32x64 = 6,
    Bs32x32 = 7,
    Bs32x16 = 8,
    Bs32x8 = 9,
    Bs16x64 = 10,
    Bs16x32 = 11,
    Bs16x16 = 12,
    Bs16x8 = 13,
    Bs16x4 = 14,
    Bs8x32 = 15,
    Bs8x16 = 16,
    Bs8x8 = 17,
    Bs8x4 = 18,
    Bs4x16 = 19,
    Bs4x8 = 20,
    Bs4x4 = 21,
    Count = 22
}

// ============================================================================
// Enumerations — Prediction
// ============================================================================

/// <summary>
/// AV1 intra prediction modes.
/// Maps to dav1d IntraPredMode enum (levels.h).
/// </summary>
public enum Av1IntraPredMode : byte
{
    Dc = 0,
    Vertical = 1,
    Horizontal = 2,
    DiagDownLeft = 3,
    DiagDownRight = 4,
    VerticalRight = 5,
    HorizontalDown = 6,
    HorizontalUp = 7,
    VerticalLeft = 8,
    Smooth = 9,
    SmoothV = 10,
    SmoothH = 11,
    Paeth = 12,
    Count = 13,

    // UV-only mode
    ChromaFromLuma = Count,
    UvCount = 14,
    ImplCount = UvCount,

    // Implementation-only values (not signaled directly)
    LeftDc = DiagDownLeft,  // 3
    TopDc = 4,
    Dc128 = 5,
    Z1 = 6,
    Z2 = 7,
    Z3 = 8,
    Filter = Count
}

/// <summary>
/// AV1 inter-intra prediction modes.
/// Maps to dav1d InterIntraPredMode enum (levels.h).
/// </summary>
public enum Av1InterIntraPredMode : byte
{
    Dc = 0,
    Vertical = 1,
    Horizontal = 2,
    Smooth = 3,
    Count = 4
}

/// <summary>
/// AV1 inter prediction modes (single reference).
/// Maps to dav1d InterPredMode enum (levels.h).
/// </summary>
public enum Av1InterPredMode : byte
{
    NearestMv = 0,
    NearMv = 1,
    GlobalMv = 2,
    NewMv = 3,
    Count = 4
}

/// <summary>
/// AV1 compound inter prediction modes (two references).
/// Maps to dav1d CompInterPredMode enum (levels.h).
/// </summary>
public enum Av1CompInterPredMode : byte
{
    NearestNearest = 0,
    NearNear = 1,
    NearestNew = 2,
    NewNearest = 3,
    NearNew = 4,
    NewNear = 5,
    GlobalGlobal = 6,
    NewNew = 7,
    Count = 8
}

/// <summary>
/// AV1 DRL (dynamic reference list) proximity index.
/// Maps to dav1d DRL_PROXIMITY enum (levels.h).
/// </summary>
public enum Av1DrlProximity : byte
{
    Nearest = 0,
    Nearer = 1,
    Near = 2,
    Nearish = 3
}

/// <summary>
/// AV1 compound inter prediction type.
/// Maps to dav1d CompInterType enum (levels.h).
/// </summary>
public enum Av1CompInterType : byte
{
    None = 0,
    WeightedAvg = 1,
    Average = 2,
    Seg = 3,
    Wedge = 4
}

/// <summary>
/// AV1 inter-intra compound type.
/// Maps to dav1d InterIntraType enum (levels.h).
/// </summary>
public enum Av1InterIntraType : byte
{
    None = 0,
    Blend = 1,
    Wedge = 2
}

/// <summary>
/// AV1 motion mode type.
/// Maps to dav1d MotionMode enum (levels.h).
/// </summary>
public enum Av1MotionMode : byte
{
    Translation = 0,
    Obmc = 1,
    Warp = 2
}

// ============================================================================
// Enumerations — Interpolation Filter
// ============================================================================

/// <summary>
/// AV1 1D interpolation filter mode.
/// Maps to dav1d Dav1dFilterMode enum (headers.h).
/// </summary>
public enum Av1FilterMode : byte
{
    EightTapRegular = 0,
    EightTapSmooth = 1,
    EightTapSharp = 2,
    SwitchableCount = 3,
    Bilinear = SwitchableCount,
    Count = 4,
    Switchable = Count
}

/// <summary>
/// AV1 2D interpolation filter (horizontal × vertical combination).
/// Maps to dav1d Filter2d enum (levels.h).
/// </summary>
public enum Av1Filter2d : byte
{
    EightTapRegular = 0,
    EightTapRegularSmooth = 1,
    EightTapRegularSharp = 2,
    EightTapSharpRegular = 3,
    EightTapSharpSmooth = 4,
    EightTapSharp = 5,
    EightTapSmoothRegular = 6,
    EightTapSmooth = 7,
    EightTapSmoothSharp = 8,
    Bilinear = 9,
    Count = 10
}

// ============================================================================
// Enumerations — Motion Vector
// ============================================================================

/// <summary>
/// AV1 motion vector joint type.
/// Maps to dav1d MVJoint enum (levels.h).
/// </summary>
public enum Av1MvJoint : byte
{
    Zero = 0,
    Horizontal = 1,
    Vertical = 2,
    HorizontalVertical = 3,
    Count = 4
}

// ============================================================================
// Enumerations — Loop Filter / Restoration
// ============================================================================

/// <summary>
/// AV1 adaptive boolean (off / on / adaptive).
/// Maps to dav1d Dav1dAdaptiveBoolean enum (headers.h).
/// </summary>
public enum Av1AdaptiveBoolean : byte
{
    Off = 0,
    On = 1,
    Adaptive = 2
}

/// <summary>
/// AV1 loop restoration type.
/// Maps to dav1d Dav1dRestorationType enum (headers.h).
/// </summary>
public enum Av1RestorationType : byte
{
    None = 0,
    Switchable = 1,
    Wiener = 2,
    SelfGuided = 3
}

// ============================================================================
// Structs — Loop Filter, CDEF, Restoration
// ============================================================================

/// <summary>
/// Loop filter E/I/H lookup table. Maps to dav1d Av1FilterLUT (lf_mask.h).
/// </summary>
public class Av1FilterLut
{
    public readonly byte[] E = new byte[64];
    public readonly byte[] I = new byte[64];
    public int Sharp0, Sharp1;
}

/// <summary>
/// Per-restoration-unit parameters. Maps to dav1d Av1RestorationUnit (lf_mask.h).
/// </summary>
public struct Av1RestorationUnit
{
    /// <summary>Restoration type (None, Wiener, SelfGuided, or SgrProj + idx).</summary>
    public Av1RestorationType Type;
    public sbyte FilterH0, FilterH1, FilterH2;
    public sbyte FilterV0, FilterV1, FilterV2;
    public sbyte SgrWeight0, SgrWeight1;
}

/// <summary>
/// Per-128x128 area loop filter bitmasks (pre-superres scaling).
/// Maps to dav1d Av1Filter (lf_mask.h).
/// </summary>
public class Av1FilterMask
{
    /// <summary>Luma filter bitmasks: [direction 0=col,1=row][row/col index][strength 0-2][half 0-1].</summary>
    public readonly ushort[,,,] FilterY = new ushort[2, 32, 3, 2];
    /// <summary>Chroma filter bitmasks: [direction 0=col,1=row][row/col index][strength 0-1][half 0-1].</summary>
    public readonly ushort[,,,] FilterUv = new ushort[2, 32, 2, 2];
    /// <summary>CDEF filter index per 64x64 block (-1 = unset).</summary>
    public sbyte CdefIdx0 = -1, CdefIdx1 = -1, CdefIdx2 = -1, CdefIdx3 = -1;
    /// <summary>No-skip mask for 8x8 blocks, stored on 4x8 basis.</summary>
    public readonly ushort[,] NoskipMask = new ushort[16, 2];

    public sbyte GetCdefIdx(int i) => i switch
    {
        0 => CdefIdx0, 1 => CdefIdx1, 2 => CdefIdx2, _ => CdefIdx3
    };
    public void SetCdefIdx(int i, sbyte v)
    {
        switch (i) { case 0: CdefIdx0 = v; break; case 1: CdefIdx1 = v; break;
                     case 2: CdefIdx2 = v; break; default: CdefIdx3 = v; break; }
    }
    public void Reset()
    {
        Array.Clear(FilterY);
        Array.Clear(FilterUv);
        CdefIdx0 = CdefIdx1 = CdefIdx2 = CdefIdx3 = -1;
        Array.Clear(NoskipMask);
    }
    /// <summary>Copy noskip mask and CDEF indices from another mask.</summary>
    public void CopyFrom(Av1FilterMask other)
    {
        CdefIdx0 = other.CdefIdx0;
        CdefIdx1 = other.CdefIdx1;
        CdefIdx2 = other.CdefIdx2;
        CdefIdx3 = other.CdefIdx3;
        Array.Copy(other.NoskipMask, NoskipMask, other.NoskipMask.Length);
    }
}

/// <summary>
/// Per-128x128 area loop restoration parameters (post-superres scaling).
/// Maps to dav1d Av1Restoration (lf_mask.h).
/// </summary>
public class Av1RestorationInfo
{
    /// <summary>Restoration units: [plane][unit index 0-3].</summary>
    public readonly Av1RestorationUnit[,] Lr = new Av1RestorationUnit[3, 4];
}

/// <summary>
/// AV1 warped motion model type.
/// Maps to dav1d Dav1dWarpedMotionType enum (headers.h).
/// </summary>
public enum Av1WarpedMotionType : byte
{
    Identity = 0,
    Translation = 1,
    RotZoom = 2,
    Affine = 3
}

/// <summary>
/// AV1 pixel layout (chroma subsampling).
/// Maps to dav1d Dav1dPixelLayout enum (headers.h).
/// </summary>
public enum Av1PixelLayout : byte
{
    I400 = 0,   // monochrome
    I420 = 1,   // 4:2:0
    I422 = 2,   // 4:2:2
    I444 = 3    // 4:4:4
}

// ============================================================================
// Enumerations — Metadata
// ============================================================================

/// <summary>
/// AV1 OBU metadata types.
/// Maps to dav1d ObuMetaType enum (levels.h).
/// </summary>
public enum Av1ObuMetaType : byte
{
    HdrCll = 1,
    HdrMdcv = 2,
    Scalability = 3,
    ItutT35 = 4,
    Timecode = 5
}

// ============================================================================
// Structs — Motion Vector
// ============================================================================

/// <summary>
/// AV1 motion vector (Y, X pair in 1/8-pel units).
/// Maps to dav1d mv union (levels.h).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct Av1MotionVector
{
    [FieldOffset(0)] public short Y;
    [FieldOffset(2)] public short X;
    [FieldOffset(0)] public uint Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsZero() => Raw == 0;

    public override readonly string ToString() => $"({Y}, {X})";
}

// ============================================================================
// Structs — Transform Info
// ============================================================================

/// <summary>
/// AV1 transform dimensions and metadata.
/// Maps to dav1d TxfmInfo struct (tables.h).
/// Width/height are in 4px blocks.
/// </summary>
public struct Av1TxfmInfo
{
    /// <summary>Width in 4px blocks.</summary>
    public byte W;
    /// <summary>Height in 4px blocks.</summary>
    public byte H;
    /// <summary>Log2 of width in 4px blocks.</summary>
    public byte Lw;
    /// <summary>Log2 of height in 4px blocks.</summary>
    public byte Lh;
    /// <summary>Min of Lw and Lh.</summary>
    public byte Min;
    /// <summary>Max of Lw and Lh.</summary>
    public byte Max;
    /// <summary>Sub-transform size index (for split transforms).</summary>
    public byte Sub;
    /// <summary>Context index for CDF selection.</summary>
    public byte Ctx;
}

// ============================================================================
// Structs — Warped Motion
// ============================================================================

/// <summary>
/// AV1 warped motion parameters.
/// Maps to dav1d Dav1dWarpedMotionParams (headers.h).
/// </summary>
public struct Av1WarpedMotionParams
{
    public Av1WarpedMotionType Type;
    public int Matrix0, Matrix1, Matrix2, Matrix3, Matrix4, Matrix5;
    public short Alpha, Beta, Gamma, Delta;

    /// <summary>Gets or sets a matrix element by index (0..5).</summary>
    public int this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => index switch
        {
            0 => Matrix0, 1 => Matrix1, 2 => Matrix2,
            3 => Matrix3, 4 => Matrix4, 5 => Matrix5,
            _ => throw new IndexOutOfRangeException()
        };
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            switch (index)
            {
                case 0: Matrix0 = value; break;
                case 1: Matrix1 = value; break;
                case 2: Matrix2 = value; break;
                case 3: Matrix3 = value; break;
                case 4: Matrix4 = value; break;
                case 5: Matrix5 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }
}

/// <summary>
/// AV1 scaling parameters for reference frame resizing (dav1d: svc[ref][xy]).
/// </summary>
public struct Av1ScalingParams
{
    public int Scale;
    public int Step;
}

// ============================================================================
// Structs — Segmentation
// ============================================================================

/// <summary>
/// AV1 per-segment feature data.
/// Maps to dav1d Dav1dSegmentationData (headers.h).
/// </summary>
public struct Av1SegmentationData
{
    public short DeltaQ;
    public sbyte DeltaLfYV, DeltaLfYH, DeltaLfU, DeltaLfV;
    public sbyte Ref;
    public byte Skip;
    public byte GlobalMv;
}

/// <summary>
/// AV1 segmentation data set for all segments.
/// Maps to dav1d Dav1dSegmentationDataSet (headers.h).
/// </summary>
public unsafe struct Av1SegmentationDataSet
{
    public fixed byte SegmentDataStorage[Av1Constants.MaxSegments * 8]; // Av1SegmentationData[8]
    public byte Preskip;
    public sbyte LastActiveSegId;

    /// <summary>Gets a reference to segment data by index.</summary>
    public Span<Av1SegmentationData> Segments
    {
        get
        {
            fixed (byte* p = SegmentDataStorage)
                return new Span<Av1SegmentationData>(p, Av1Constants.MaxSegments);
        }
    }
}

// ============================================================================
// Structs — Loop Filter
// ============================================================================

/// <summary>
/// AV1 loop filter mode/reference deltas.
/// Maps to dav1d Dav1dLoopfilterModeRefDeltas (headers.h).
/// </summary>
public struct Av1LoopfilterModeRefDeltas
{
    /// <summary>Mode deltas: [0] = zero-MV, [1] = non-zero-MV.</summary>
    public sbyte ModeDelta0, ModeDelta1;

    /// <summary>Reference deltas for each reference type (INTRA_FRAME..ALTREF).</summary>
    public sbyte RefDelta0, RefDelta1, RefDelta2, RefDelta3;
    public sbyte RefDelta4, RefDelta5, RefDelta6, RefDelta7;

    /// <summary>Gets a mode delta by index (0 or 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly sbyte GetModeDelta(int index) => index == 0 ? ModeDelta0 : ModeDelta1;

    /// <summary>Sets a mode delta by index (0 or 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetModeDelta(int index, sbyte value)
    {
        if (index == 0) ModeDelta0 = value; else ModeDelta1 = value;
    }

    /// <summary>Gets a reference delta by index (0..7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly sbyte GetRefDelta(int index) => index switch
    {
        0 => RefDelta0, 1 => RefDelta1, 2 => RefDelta2, 3 => RefDelta3,
        4 => RefDelta4, 5 => RefDelta5, 6 => RefDelta6, 7 => RefDelta7,
        _ => 0
    };

    /// <summary>Sets a reference delta by index (0..7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetRefDelta(int index, sbyte value)
    {
        switch (index)
        {
            case 0: RefDelta0 = value; break;
            case 1: RefDelta1 = value; break;
            case 2: RefDelta2 = value; break;
            case 3: RefDelta3 = value; break;
            case 4: RefDelta4 = value; break;
            case 5: RefDelta5 = value; break;
            case 6: RefDelta6 = value; break;
            case 7: RefDelta7 = value; break;
        }
    }
}

// ============================================================================
// Structs — Film Grain
// ============================================================================

/// <summary>
/// AV1 film grain synthesis parameters.
/// Maps to dav1d Dav1dFilmGrainData (headers.h).
/// </summary>
public unsafe struct Av1FilmGrainData
{
    public uint Seed;
    public int NumYPoints;
    /// <summary>Y points: [14][2] where [i][0] = value, [i][1] = scaling.</summary>
    public fixed byte YPoints[14 * 2];
    public int ChromaScalingFromLuma;
    public int NumUvPoints0, NumUvPoints1;
    /// <summary>UV points: [2][10][2] where [plane][i][0] = value, [plane][i][1] = scaling.</summary>
    public fixed byte UvPoints[2 * 10 * 2];
    public int ScalingShift;
    public int ArCoeffLag;
    public fixed sbyte ArCoeffsY[24];
    /// <summary>UV AR coefficients: [2][28] (25 + 3 padding for alignment).</summary>
    public fixed sbyte ArCoeffsUv[2 * 28];
    public long ArCoeffShift;
    public int GrainScaleShift;
    public int UvMult0, UvMult1;
    public int UvLumaMult0, UvLumaMult1;
    public int UvOffset0, UvOffset1;
    public int OverlapFlag;
    public int ClipToRestrictedRange;
}

// ============================================================================
// Structs — Block
// ============================================================================

/// <summary>
/// AV1 decoded block information.
/// Maps to dav1d Av1Block (levels.h).
/// Stores per-block decode results for reconstruction.
/// </summary>
/// <remarks>
/// This is a dense struct used in the frame thread's block array.
/// The intra/inter union is modeled with separate fields since C# doesn't have unions.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct Av1Block
{
    // Common fields
    public byte BlockLevel;   // Av1BlockLevel
    public byte BlockSize;    // Av1BlockSize
    public byte Partition;    // Av1BlockPartition
    public byte Intra;        // 1 = intra, 0 = inter
    public byte SegId;
    public byte SkipMode;
    public byte Skip;
    public byte UvTx;         // UV transform size

    // --- Intra fields ---
    public byte YMode;        // Av1IntraPredMode (luma)
    public byte UvMode;       // Av1IntraPredMode (chroma) or CFL
    public byte Tx;           // transform size for intra
    public byte PalSzY;       // palette size for Y
    public byte PalSzUv;      // palette size for UV
    public sbyte YAngle;      // directional intra angle delta
    public sbyte UvAngle;     // directional intra angle delta (UV)
    public sbyte CflAlpha0;   // CfL alpha parameter [0]
    public sbyte CflAlpha1;   // CfL alpha parameter [1]

    /// <summary>Get CFL alpha by plane index (0=U, 1=V).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetCflAlpha(int pl) => pl == 0 ? CflAlpha0 : CflAlpha1;

    // --- Inter fields ---
    public Av1MotionVector Mv0;
    public Av1MotionVector Mv1;
    public byte WedgeIdx;
    public byte MaskSign;
    public byte InterIntraMode; // Av1InterIntraPredMode
    public byte CompType;       // Av1CompInterType
    public byte InterMode;      // Av1InterPredMode or Av1CompInterPredMode
    public byte Motion;         // Av1MotionMode
    public byte DrlIdx;
    public sbyte Ref0;          // reference index 0 (-1 = intra)
    public sbyte Ref1;          // reference index 1 (-1 = unused)
    public byte MaxYTx;         // max Y transform size
    public byte Filter;         // Av1Filter2d
    public byte InterIntraTypeField; // Av1InterIntraType
    public byte TxSplit0;
    public ushort TxSplit1;

    // Warp fields (overlaps mv in dav1d's union, but we keep separate)
    public Av1MotionVector WarpMv;
    public short WarpMatrix0, WarpMatrix1, WarpMatrix2, WarpMatrix3;
}

// ============================================================================
// Structs — Block Context (neighbor state for entropy coding)
// ============================================================================

/// <summary>
/// AV1 block context — stores left/above neighbor information for entropy coding.
/// Maps to dav1d BlockContext (env.h).
/// Arrays are sized for 128-pixel superblock edge = 32 4x4-blocks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Av1BlockContext
{
    public fixed byte Mode[32];
    public fixed byte LCoef[32];
    public fixed byte CCoef0[32];
    public fixed byte CCoef1[32];
    public fixed byte SegPred[32];
    public fixed byte Skip[32];
    public fixed byte SkipMode[32];
    public fixed byte Intra[32];
    public fixed byte CompType[32];
    public fixed sbyte Ref0[32];
    public fixed sbyte Ref1[32];
    public fixed byte Filter0[32];
    public fixed byte Filter1[32];
    public fixed sbyte TxIntra[32];
    public fixed sbyte Tx[32];
    public fixed byte TxLpfY[32];
    public fixed byte TxLpfUv[32];
    public fixed byte Partition[16];
    public fixed byte UvMode[32];
    public fixed byte PalSz[32];
}

// ============================================================================
// Structs — Sequence / Frame Header (extended for decoder, beyond demuxer)
// ============================================================================

/// <summary>
/// Extended sequence header with all fields needed by the decoder.
/// The existing Av1SequenceHeader in Av1HeaderParser.cs is a simplified version
/// for demuxing. This struct contains the full set of tool-enable flags.
/// </summary>
/// <summary>
/// Per-operating-point parameters within the sequence header.
/// Maps to dav1d Dav1dSequenceHeaderOperatingPoint.
/// </summary>
public struct Av1OperatingPoint
{
    public byte MajorLevel, MinorLevel;
    public byte InitialDisplayDelay;
    public ushort Idc;
    public byte Tier;
    public bool DecoderModelParamPresent;
    public bool DisplayModelParamPresent;
}

/// <summary>
/// Per-operating-point decoder model info within the sequence header.
/// Maps to dav1d Dav1dSequenceHeaderOperatingParameterInfo.
/// </summary>
public struct Av1OperatingParameterInfo
{
    public uint DecoderBufferDelay;
    public uint EncoderBufferDelay;
    public bool LowDelayMode;
}

/// <summary>
/// Extended sequence header with all fields needed by the decoder.
/// Maps to dav1d Dav1dSequenceHeader (headers.h).
/// </summary>
public class Av1DecoderSequenceHeader
{
    public Av1Profile Profile;
    public int MaxWidth, MaxHeight;
    public Av1PixelLayout Layout;
    public Av1ColorPrimaries ColorPrimaries;
    public Av1TransferCharacteristics TransferCharacteristics;
    public Av1MatrixCoefficients MatrixCoefficients;
    public Av1ChromaSamplePosition ChromaSamplePosition;

    /// <summary>High bit depth: 0=8bit, 1=10bit, 2=12bit.</summary>
    public byte Hbd;
    public byte ColorRange;

    public byte NumOperatingPoints;
    public Av1OperatingPoint[] OperatingPoints = new Av1OperatingPoint[Av1Constants.MaxOperatingPoints];
    public Av1OperatingParameterInfo[] OperatingParameterInfo = new Av1OperatingParameterInfo[Av1Constants.MaxOperatingPoints];

    public bool StillPicture;
    public bool ReducedStillPictureHeader;
    public bool TimingInfoPresent;
    public uint NumUnitsInTick;
    public uint TimeScale;
    public bool EqualPictureInterval;
    public uint NumTicksPerPicture;
    public bool DecoderModelInfoPresent;
    public byte EncoderDecoderBufferDelayLength;
    public uint NumUnitsInDecodingTick;
    public byte BufferRemovalDelayLength;
    public byte FramePresentationDelayLength;
    public bool DisplayModelInfoPresent;
    public byte WidthNBits, HeightNBits;
    public bool FrameIdNumbersPresent;
    public byte DeltaFrameIdNBits;
    public byte FrameIdNBits;

    // Tool-enable flags
    public bool Sb128;
    public bool FilterIntra;
    public bool IntraEdgeFilter;
    public bool InterIntra;
    public bool MaskedCompound;
    public bool WarpedMotion;
    public bool DualFilter;
    public bool OrderHint;
    public bool JntComp;
    public bool RefFrameMvs;
    public Av1AdaptiveBoolean ScreenContentTools;
    public Av1AdaptiveBoolean ForceIntegerMv;
    public byte OrderHintNBits;
    public bool SuperRes;
    public bool Cdef;
    public bool Restoration;
    public byte SubsamplingX, SubsamplingY;
    public bool Monochrome;
    public bool ColorDescriptionPresent;
    public bool SeparateUvDeltaQ;
    public bool FilmGrainPresent;

    /// <summary>Computed bit depth from Hbd field.</summary>
    public int BitDepth => Hbd == 0 ? 8 : Hbd == 1 ? 10 : 12;

    /// <summary>Number of planes (1 for monochrome, 3 otherwise).</summary>
    public int NumPlanes => Monochrome ? 1 : 3;

    /// <summary>Reset all fields to defaults for reuse.</summary>
    public void Clear()
    {
        Profile = default;
        MaxWidth = MaxHeight = 0;
        Layout = default;
        ColorPrimaries = default;
        TransferCharacteristics = default;
        MatrixCoefficients = default;
        ChromaSamplePosition = default;
        Hbd = 0;
        ColorRange = 0;
        NumOperatingPoints = 0;
        Array.Clear(OperatingPoints);
        Array.Clear(OperatingParameterInfo);
        StillPicture = false;
        ReducedStillPictureHeader = false;
        TimingInfoPresent = false;
        NumUnitsInTick = 0;
        TimeScale = 0;
        EqualPictureInterval = false;
        NumTicksPerPicture = 0;
        DecoderModelInfoPresent = false;
        EncoderDecoderBufferDelayLength = 0;
        NumUnitsInDecodingTick = 0;
        BufferRemovalDelayLength = 0;
        FramePresentationDelayLength = 0;
        DisplayModelInfoPresent = false;
        WidthNBits = HeightNBits = 0;
        FrameIdNumbersPresent = false;
        DeltaFrameIdNBits = 0;
        FrameIdNBits = 0;
        Sb128 = false;
        FilterIntra = false;
        IntraEdgeFilter = false;
        InterIntra = false;
        MaskedCompound = false;
        WarpedMotion = false;
        DualFilter = false;
        OrderHint = false;
        JntComp = false;
        RefFrameMvs = false;
        ScreenContentTools = default;
        ForceIntegerMv = default;
        OrderHintNBits = 0;
        SuperRes = false;
        Cdef = false;
        Restoration = false;
        SubsamplingX = SubsamplingY = 0;
        Monochrome = false;
        ColorDescriptionPresent = false;
        SeparateUvDeltaQ = false;
        FilmGrainPresent = false;
    }
}

/// <summary>
/// Extended frame header with all fields needed by the decoder.
/// Maps to dav1d Dav1dFrameHeader (headers.h).
/// Changed from struct to class to support array fields for tile/segment/global motion data.
/// </summary>
public class Av1DecoderFrameHeader
{
    public Av1FilmGrainData FilmGrain;
    public bool FilmGrainPresent;
    public bool FilmGrainUpdate;

    public Av1FrameType FrameType;
    public int CodedWidth;
    public int SuperResUpscaledWidth;
    public int Height;
    public byte FrameOffset;
    public byte TemporalId;
    public byte SpatialId;

    public bool ShowExistingFrame;
    public byte ExistingFrameIdx;
    public uint FrameId;
    public uint FramePresentationDelay;
    public bool ShowFrame;
    public bool ShowableFrame;
    public bool ErrorResilientMode;
    public bool DisableCdfUpdate;
    public bool AllowScreenContentTools;
    public bool ForceIntegerMv;
    public bool FrameSizeOverride;
    public byte PrimaryRefFrame;
    public bool BufferRemovalTimePresent;
    public uint[] OperatingPointBufferRemovalTime = new uint[Av1Constants.MaxOperatingPoints];
    public byte RefreshFrameFlags;
    public int RenderWidth, RenderHeight;

    // Super-resolution
    public byte SuperResScaleDenominator;
    public bool SuperResEnabled;
    public bool HaveRenderSize;

    public bool AllowIntraBc;
    public bool FrameRefShortSignaling;

    /// <summary>Reference indices for each of the 7 reference types.</summary>
    public sbyte RefIdx0, RefIdx1, RefIdx2, RefIdx3, RefIdx4, RefIdx5, RefIdx6;

    public bool Hp; // allow high-precision MVs
    public Av1FilterMode SubpelFilterMode;
    public bool SwitchableMotionMode;
    public bool UseRefFrameMvs;
    public bool RefreshContext;

    // Tiling
    public byte TileUniform;
    public byte TileNBytes;
    public byte TileMinLog2Cols, TileMaxLog2Cols, TileLog2Cols, TileCols;
    public byte TileMinLog2Rows, TileMaxLog2Rows, TileLog2Rows, TileRows;
    public ushort TileUpdate;
    public ushort[] TileColStartSb = new ushort[Av1Constants.MaxTileCols + 1];
    public ushort[] TileRowStartSb = new ushort[Av1Constants.MaxTileRows + 1];

    // Quantization
    public byte QuantBaseQIdx;
    public sbyte QuantYDcDelta;
    public sbyte QuantUDcDelta, QuantUAcDelta;
    public sbyte QuantVDcDelta, QuantVAcDelta;
    public bool QuantUseQMatrix;
    public byte QmY, QmU, QmV;

    // Segmentation
    public bool SegmentationEnabled;
    public bool SegmentationUpdateMap;
    public bool SegmentationTemporal;
    public bool SegmentationUpdateData;
    public Av1SegmentationDataSet SegmentationData;
    public byte[] SegmentationQIdx = new byte[Av1Constants.MaxSegments];
    public bool[] SegmentationLossless = new bool[Av1Constants.MaxSegments];

    // Delta Q / Delta LF
    public bool DeltaQPresent;
    public byte DeltaQResLog2;
    public bool DeltaLfPresent;
    public byte DeltaLfResLog2;
    public bool DeltaLfMulti;

    public bool AllLossless;

    // Loop filter
    public byte LfLevelY0, LfLevelY1;
    public byte LfLevelU, LfLevelV;
    public bool LfModeRefDeltaEnabled;
    public bool LfModeRefDeltaUpdate;
    public Av1LoopfilterModeRefDeltas LfModeRefDeltas;
    public byte LfSharpness;

    // CDEF
    public byte CdefDamping;
    public byte CdefNBits;
    public byte CdefYStrength0, CdefYStrength1, CdefYStrength2, CdefYStrength3;
    public byte CdefYStrength4, CdefYStrength5, CdefYStrength6, CdefYStrength7;
    public byte CdefUvStrength0, CdefUvStrength1, CdefUvStrength2, CdefUvStrength3;
    public byte CdefUvStrength4, CdefUvStrength5, CdefUvStrength6, CdefUvStrength7;

    // Loop restoration
    public Av1RestorationType LrType0, LrType1, LrType2;
    public byte LrUnitSizeY, LrUnitSizeUv;

    public Av1TxfmMode TxfmMode;
    public bool SwitchableCompRefs;
    public bool SkipModeAllowed;
    public bool SkipModeEnabled;
    public sbyte SkipModeRef0, SkipModeRef1;
    public bool WarpMotion;
    public bool ReducedTxSet;

    // Global motion
    public Av1WarpedMotionParams[] Gmv = new Av1WarpedMotionParams[Av1Constants.RefsPerFrame];

    /// <summary>Gets a reference index by ordinal (0..6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte GetRefIdx(int index) => index switch
    {
        0 => RefIdx0, 1 => RefIdx1, 2 => RefIdx2, 3 => RefIdx3,
        4 => RefIdx4, 5 => RefIdx5, 6 => RefIdx6,
        _ => -1
    };

    /// <summary>Sets a reference index by ordinal (0..6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetRefIdx(int index, sbyte value)
    {
        switch (index)
        {
            case 0: RefIdx0 = value; break;
            case 1: RefIdx1 = value; break;
            case 2: RefIdx2 = value; break;
            case 3: RefIdx3 = value; break;
            case 4: RefIdx4 = value; break;
            case 5: RefIdx5 = value; break;
            case 6: RefIdx6 = value; break;
        }
    }

    /// <summary>Gets a CDEF Y strength by index (0..7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetCdefYStrength(int index) => index switch
    {
        0 => CdefYStrength0, 1 => CdefYStrength1, 2 => CdefYStrength2, 3 => CdefYStrength3,
        4 => CdefYStrength4, 5 => CdefYStrength5, 6 => CdefYStrength6, 7 => CdefYStrength7,
        _ => 0
    };

    /// <summary>Sets a CDEF Y strength by index (0..7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCdefYStrength(int index, byte value)
    {
        switch (index)
        {
            case 0: CdefYStrength0 = value; break; case 1: CdefYStrength1 = value; break;
            case 2: CdefYStrength2 = value; break; case 3: CdefYStrength3 = value; break;
            case 4: CdefYStrength4 = value; break; case 5: CdefYStrength5 = value; break;
            case 6: CdefYStrength6 = value; break; case 7: CdefYStrength7 = value; break;
        }
    }

    /// <summary>Gets a CDEF UV strength by index (0..7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetCdefUvStrength(int index) => index switch
    {
        0 => CdefUvStrength0, 1 => CdefUvStrength1, 2 => CdefUvStrength2, 3 => CdefUvStrength3,
        4 => CdefUvStrength4, 5 => CdefUvStrength5, 6 => CdefUvStrength6, 7 => CdefUvStrength7,
        _ => 0
    };

    /// <summary>Sets a CDEF UV strength by index (0..7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCdefUvStrength(int index, byte value)
    {
        switch (index)
        {
            case 0: CdefUvStrength0 = value; break; case 1: CdefUvStrength1 = value; break;
            case 2: CdefUvStrength2 = value; break; case 3: CdefUvStrength3 = value; break;
            case 4: CdefUvStrength4 = value; break; case 5: CdefUvStrength5 = value; break;
            case 6: CdefUvStrength6 = value; break; case 7: CdefUvStrength7 = value; break;
        }
    }

    /// <summary>Gets a loop restoration type by plane index (0..2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Av1RestorationType GetLrType(int plane) => plane switch
    {
        0 => LrType0, 1 => LrType1, 2 => LrType2,
        _ => Av1RestorationType.None
    };

    /// <summary>Sets a loop restoration type by plane index (0..2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLrType(int plane, Av1RestorationType value)
    {
        switch (plane)
        {
            case 0: LrType0 = value; break;
            case 1: LrType1 = value; break;
            case 2: LrType2 = value; break;
        }
    }

    /// <summary>True if this is a keyframe or intra-only frame.</summary>
    public bool IsIntra => FrameType == Av1FrameType.Key || FrameType == Av1FrameType.IntraOnly;

    /// <summary>True if this is an inter or switch frame.</summary>
    public bool IsInterOrSwitch => FrameType == Av1FrameType.Inter || FrameType == Av1FrameType.Switch;

    /// <summary>Whether the given segment is lossless.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLossless(int segId) => SegmentationLossless[segId];

    /// <summary>Alias for CdefNBits (number of CDEF bits).</summary>
    public byte CdefBits => CdefNBits;

    /// <summary>Alias for TxfmMode (for decode.c compatibility naming).</summary>
    public Av1TxfmMode TxMode => TxfmMode;

    /// <summary>Pixel layout from the associated sequence header (set during frame header parse).</summary>
    public Av1PixelLayout PixelLayout;
}

// ============================================================================
// Lookup helpers
// ============================================================================

/// <summary>
/// AV1 block size lookup helpers.
/// </summary>
public static class Av1BlockSizeHelper
{
    /// <summary>Block width in pixels, indexed by Av1BlockSize.</summary>
    public static ReadOnlySpan<byte> WidthPixels =>
    [
        128, 128, 64, 64, 64, 64, 32, 32, 32, 32,
         16,  16, 16, 16, 16,  8,  8,  8,  8,  4,  4, 4
    ];

    /// <summary>Block height in pixels, indexed by Av1BlockSize.</summary>
    public static ReadOnlySpan<byte> HeightPixels =>
    [
        128, 64, 128, 64, 32, 16, 64, 32, 16,  8,
         64, 32,  16,  8,  4, 32, 16,  8,  4, 16,  8, 4
    ];

    /// <summary>Block width in 4x4 units, indexed by Av1BlockSize.</summary>
    public static ReadOnlySpan<byte> Width4 =>
    [
        32, 32, 16, 16, 16, 16,  8,  8,  8,  8,
         4,  4,  4,  4,  4,  2,  2,  2,  2,  1,  1, 1
    ];

    /// <summary>Block height in 4x4 units, indexed by Av1BlockSize.</summary>
    public static ReadOnlySpan<byte> Height4 =>
    [
        32, 16, 32, 16,  8,  4, 16,  8,  4,  2,
        16,  8,  4,  2,  1,  8,  4,  2,  1,  4,  2, 1
    ];

    /// <summary>Gets the width in pixels for a block size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWidth(Av1BlockSize bs) => WidthPixels[(int)bs];

    /// <summary>Gets the height in pixels for a block size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHeight(Av1BlockSize bs) => HeightPixels[(int)bs];

    /// <summary>Gets the width in 4x4 units for a block size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWidth4(Av1BlockSize bs) => Width4[(int)bs];

    /// <summary>Gets the height in 4x4 units for a block size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHeight4(Av1BlockSize bs) => Height4[(int)bs];
}

/// <summary>
/// AV1 transform size lookup helpers.
/// </summary>
public static class Av1TxSizeHelper
{
    /// <summary>Transform width in pixels, indexed by Av1TxSize (square only, 0..4).</summary>
    public static ReadOnlySpan<byte> WidthPixels => [4, 8, 16, 32, 64];

    /// <summary>Transform log2 width, indexed by Av1TxSize (square only).</summary>
    public static ReadOnlySpan<byte> Log2Width => [2, 3, 4, 5, 6];
}
