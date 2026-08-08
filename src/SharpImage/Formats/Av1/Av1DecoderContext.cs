// Copyright (c) MediaKernel. All rights reserved.
// Port of dav1d decoder context from src/internal.h (VideoLAN dav1d, BSD-2-Clause)

using System;
using System.Buffers;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Per-tile decoding state for AV1.
/// Each tile within a frame has its own MSAC entropy coder and CDF context.
/// </summary>
public sealed class Av1TileState
{
    public Av1CdfContext Cdf = new();

    /// <summary>Tile boundaries in 4-pixel units.</summary>
    public int ColStart, ColEnd, RowStart, RowEnd;

    /// <summary>Tile position in tile grid units.</summary>
    public int TileCol, TileRow;

    /// <summary>Dequantization values per segment/plane/dc-ac: [segment][plane][0=dc,1=ac].</summary>
    public ushort[,,] Dq = new ushort[8, 3, 2];

    /// <summary>Last quantizer index seen in this tile (for delta_q).</summary>
    public int LastQIdx;

    /// <summary>Last delta loop filter values (4 components).</summary>
    public int[] LastDeltaLf = new int[4];

    /// <summary>Pre-computed loop filter level values per segment/direction/ref/mode.
    /// Dimensions: [segment][plane/dir 0-3][ref 0-7][mode 0-1].
    /// dav1d: ts->lflvl or f->lf.lvl.</summary>
    public byte[,,,] LfLvl = new byte[8, 4, 8, 2];

    // ──── Tile Data for MSAC Initialization ────

    /// <summary>Raw tile data bytes for MSAC initialization.</summary>
    public byte[]? TileData;

    /// <summary>Offset into TileData where this tile's bitstream starts.</summary>
    public int TileDataOffset;

    /// <summary>Length of this tile's bitstream data in bytes.</summary>
    public int TileDataLength;

    /// <summary>Whether this tile's MSAC has been initialized for the current SB row.</summary>
    public bool MsacInitialized;

    /// <summary>Saved MSAC state between SB rows (since Av1Msac is a ref struct).</summary>
    public Av1Msac.SavedState MsacState;

    /// <summary>
    /// Per-plane reference restoration unit for sub-exponential delta coding.
    /// Maps to dav1d ts->lr_ref[3]. Initialized at tile start, updated after each LR unit decode.
    /// </summary>
    public Av1RestorationUnit[] LrRef = new Av1RestorationUnit[3];

    /// <summary>Initialize LR reference values for delta coding (dav1d defaults).</summary>
    public void InitLrRef()
    {
        for (int p = 0; p < 3; p++)
        {
            LrRef[p].FilterV0 = 3;
            LrRef[p].FilterV1 = -7;
            LrRef[p].FilterV2 = 15;
            LrRef[p].FilterH0 = 3;
            LrRef[p].FilterH1 = -7;
            LrRef[p].FilterH2 = 15;
            LrRef[p].SgrWeight0 = -32;
            LrRef[p].SgrWeight1 = 31;
        }
    }
}

/// <summary>
/// Reference frame slot — holds a decoded picture and associated metadata.
/// AV1 maintains up to 8 reference frame slots.
/// </summary>
public sealed class Av1ReferenceFrame
{
    /// <summary>Whether this slot contains a valid reference frame.</summary>
    public bool Valid;

    /// <summary>Frame width in pixels.</summary>
    public int Width;

    /// <summary>Frame height in pixels.</summary>
    public int Height;

    /// <summary>Render width.</summary>
    public int RenderWidth;

    /// <summary>Render height.</summary>
    public int RenderHeight;

    /// <summary>Frame type when this reference was captured.</summary>
    public Av1FrameType FrameType;

    /// <summary>Order hint of the reference frame.</summary>
    public int OrderHint;

    /// <summary>Segmentation map for this reference frame (if any).</summary>
    public byte[]? SegmentMap;

    /// <summary>
    /// Decoded pixel data per plane. Format depends on bit depth:
    /// 8-bit: byte[], 10/12-bit: ushort[] (stored as byte[] with 2 bytes per sample).
    /// </summary>
    public byte[]?[] Planes = new byte[3][];

    /// <summary>Stride in bytes for each plane.</summary>
    public int[] Strides = new int[3];

    /// <summary>CDF context snapshot from this reference frame (for CDF update).</summary>
    public Av1CdfContext? CdfSnapshot;

    /// <summary>Temporal MV projection from this reference frame (for inter prediction).</summary>
    public Av1RefMvsTemporalBlock[]? TemporalMvs;

    public void Reset()
    {
        Valid = false;
        Width = Height = 0;
        SegmentMap = null;
        Planes[0] = Planes[1] = Planes[2] = null;
        CdfSnapshot = null;
        TemporalMvs = null;
    }
}

/// <summary>
/// Top-level AV1 decoder context. Maintains the decoder state across frames:
/// sequence parameters, reference frame buffer pool, CDF probability contexts,
/// and per-frame/per-tile working state.
/// </summary>
public sealed class Av1DecoderContext : IDisposable
{
    // ──── Sequence and Frame Headers ────

    /// <summary>Current sequence header (set on first OBU_SEQUENCE_HEADER).</summary>
    public Av1DecoderSequenceHeader? SequenceHeader;

    /// <summary>Whether a valid sequence header has been received.</summary>
    public bool HasSequenceHeader;

    /// <summary>Current frame header (set per frame).</summary>
    public Av1DecoderFrameHeader? FrameHeader;

    // ──── Reference Frame Buffer ────

    /// <summary>Reference frame slots (8 total, per AV1 spec).</summary>
    public readonly Av1ReferenceFrame[] RefFrames = CreateRefFrames();

    // ──── CDF State ────

    /// <summary>CDF contexts for each reference slot (used for CDF updates across frames).</summary>
    public readonly Av1CdfContext[] CdfSlots = CreateCdfSlots();

    // ──── Current Frame State ────

    /// <summary>Tile states for the current frame being decoded.</summary>
    public Av1TileState[]? TileStates;

    /// <summary>Number of tile columns in the current frame.</summary>
    public int TileCols;

    /// <summary>Number of tile rows in the current frame.</summary>
    public int TileRows;

    /// <summary>Frame width in 4-pixel units.</summary>
    public int Width4;

    /// <summary>Frame height in 4-pixel units.</summary>
    public int Height4;

    /// <summary>Frame width in superblock units.</summary>
    public int SuperBlockCols;

    /// <summary>Frame height in superblock units.</summary>
    public int SuperBlockRows;

    /// <summary>Whether the current frame uses 128×128 superblocks (vs 64×64).</summary>
    public bool UseSuperBlock128;

    /// <summary>Bit depth of the current sequence (8, 10, or 12).</summary>
    public int BitDepth;

    /// <summary>Maximum pixel value for the current bit depth (255, 1023, or 4095).</summary>
    public int BitDepthMax;

    // ──── Frame Geometry (4-pixel units) ────

    /// <summary>Frame width in 4-pixel block units (same as Width4, dav1d: f->bw).</summary>
    public int Bw;

    /// <summary>Frame height in 4-pixel block units (same as Height4, dav1d: f->bh).</summary>
    public int Bh;

    /// <summary>Superblock step size in 4-pixel units (16 for SB64, 32 for SB128).</summary>
    public int SbStep;

    /// <summary>Superblock shift (log2 of SbStep): 4 for SB64, 5 for SB128.</summary>
    public int SbShift;

    /// <summary>Frame width in 128-pixel superblock units (rounded up).</summary>
    public int Sb128w;

    // ──── Intra Prediction Edge Buffers ────

    /// <summary>Y plane edge buffer for intra prediction across SB boundaries.</summary>
    public byte[] IpredEdgeY = Array.Empty<byte>();

    /// <summary>U plane edge buffer for intra prediction across SB boundaries.</summary>
    public byte[] IpredEdgeU = Array.Empty<byte>();

    /// <summary>V plane edge buffer for intra prediction across SB boundaries.</summary>
    public byte[] IpredEdgeV = Array.Empty<byte>();

    // ──── Inter Prediction State ────

    /// <summary>Pixel layout (I400/I420/I422/I444) of the current frame.</summary>
    public Av1PixelLayout PixelLayout;

    /// <summary>Per-reference global motion warp allowed flags (dav1d: f->gmv_warp_allowed[7]).</summary>
    public bool[] GmvWarpAllowed = new bool[7];

    /// <summary>Joint compound weight table [ref0][ref1] (dav1d: f->jnt_weights[7][7]).</summary>
    public byte[,] JntWeights = new byte[7, 7];

    /// <summary>Scaling parameters per reference [ref][xy] (dav1d: f->svc[7][2]).</summary>
    public Av1ScalingParams[,] Svc = new Av1ScalingParams[7, 2];

    /// <summary>Frame-level reference MV state (dav1d: f->rf).</summary>
    public Av1RefMvsFrame RefMvs = new Av1RefMvsFrame();

    /// <summary>Temporal MV blocks from previous frame (dav1d: f->cur.rp, stored between frames).</summary>
    public Av1RefMvsTemporalBlock[]? PrevRp;

    /// <summary>Block stride for the block grid (dav1d: f->b4_stride).</summary>
    public int B4Stride;

    /// <summary>Block array for frame threading / sub8×8 chroma lookups.</summary>
    public Av1Block[]? Blocks;

    // ──── Loop Filter State ────

    /// <summary>SB128 columns in frame (dav1d: f->sb128w).</summary>
    public int Sb128W;

    /// <summary>Frame width in 4px blocks (dav1d: f->bw, also called w4).</summary>
    public int W4;

    /// <summary>Frame height in 4px blocks (dav1d: f->bh, also called h4).</summary>
    public int H4;

    /// <summary>Per-4x4-block loop filter levels: [b4index, plane 0-3]. dav1d: f->lf.level.</summary>
    public byte[,] LfLevel = new byte[0, 4];

    /// <summary>Per-SB128 column filter mask array for the current SB row.
    /// dav1d: f->lf.mask (indexed as mask[sb128_col]).</summary>
    public Av1FilterMask[]? LfMasks;
    /// <summary>Backup noskip+CDEF data per SB128 row×col for CDEF/LR (avoids overwrite from row processing).</summary>
    public Av1FilterMask[]? LfMasksRows;

    /// <summary>Bitmask of which planes have restoration enabled.
    /// Bit 0 = Y, bit 1 = U, bit 2 = V. dav1d: f->lf.restore_planes.</summary>
    public int RestorePlanes;

    /// <summary>Per-SB128 restoration info array for the entire frame.
    /// dav1d: f->lf.lr_mask (indexed as lr_mask[sb128y * sb128w + sb128x]).</summary>
    public Av1RestorationInfo[]? LrMasks;

    /// <summary>Pre-computed loop filter level values (frame-level, no delta_lf).
    /// Used when delta_lf is disabled. dav1d: f->lf.lvl.</summary>
    public byte[,,,] LfLvl = new byte[8, 4, 8, 2];

    /// <summary>E/I/H lookup table for loop filter. dav1d: f->lf.lim_lut.</summary>
    public Av1FilterLut LfLimLut = new();

    /// <summary>Loop restoration LPF line buffers (post-deblock, pre-CDEF boundary rows).
    /// One buffer per plane. Used as context for LR filter stripe boundaries.
    /// dav1d: f->lf.lr_lpf_line[3].</summary>
    public byte[]?[] LrLpfLine = new byte[3][];

    /// <summary>Width of the sr_sb128 row for LR mask indexing.
    /// dav1d: f->sr_sb128w.</summary>
    public int SrSb128W;

    /// <summary>Luma plane stride (convenience alias for CurrentStrides[0]).</summary>
    public int YStride;

    /// <summary>Chroma plane stride (convenience alias for CurrentStrides[1]).</summary>
    public int UvStride;

    // ──── Decoded Frame Output ────

    /// <summary>Current frame pixel buffers (one per plane: Y, U, V).</summary>
    public byte[]?[] CurrentPlanes = new byte[3][];

    /// <summary>Stride in bytes per plane for the current frame.</summary>
    public int[] CurrentStrides = new int[3];

    // ──── Methods ────

    /// <summary>
    /// Allocate tile states for the current frame based on tile grid dimensions.
    /// </summary>
    public void AllocateTileStates(int tileCols, int tileRows)
    {
        TileCols = tileCols;
        TileRows = tileRows;
        int count = tileCols * tileRows;
        if (TileStates == null || TileStates.Length < count)
        {
            TileStates = new Av1TileState[count];
            for (int i = 0; i < count; i++)
                TileStates[i] = new Av1TileState();
        }
    }

    /// <summary>
    /// Initialize CDF contexts for all tiles from defaults or a reference frame's CDFs.
    /// </summary>
    public void InitializeTileCdfs(int qIdx, Av1CdfContext? referenceCdf)
    {
        if (TileStates == null) return;
        int count = TileCols * TileRows;
        int qcat = (qIdx > 20 ? 1 : 0) + (qIdx > 60 ? 1 : 0) + (qIdx > 120 ? 1 : 0);
        AvDbg.W($"[Q-INFO] qIdx={qIdx} qcat={qcat}");

        for (int i = 0; i < count; i++)
        {
            var ts = TileStates[i];
            if (referenceCdf != null)
            {
                ts.Cdf.CopyFrom(referenceCdf);
            }
            else
            {
                Av1CdfDefaults.InitializeDefault(ts.Cdf, qcat);
            }
        }
    }

    public void Dispose()
    {
        // Return any pooled buffers
        for (int i = 0; i < 3; i++)
        {
            if (CurrentPlanes[i] != null)
            {
                ArrayPool<byte>.Shared.Return(CurrentPlanes[i]);
                CurrentPlanes[i] = null;
            }
        }
        for (int i = 0; i < 8; i++)
            RefFrames[i].Reset();
    }

    private static Av1ReferenceFrame[] CreateRefFrames()
    {
        var refs = new Av1ReferenceFrame[8];
        for (int i = 0; i < 8; i++)
            refs[i] = new Av1ReferenceFrame();
        return refs;
    }

    private static Av1CdfContext[] CreateCdfSlots()
    {
        var slots = new Av1CdfContext[8];
        for (int i = 0; i < 8; i++)
            slots[i] = new Av1CdfContext();
        return slots;
    }
}
