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

    /// <summary>The frame's coded (pre-super-resolution) width, dav1d ref_coded_width: what motion-field and segment-map
    /// reuse compare against (Width is the upscaled picture width).</summary>
    public int CodedWidth;

    /// <summary>Segmentation map for this reference frame (if any): dav1d refs[].segmap, b4_stride-wide.</summary>
    public byte[]? SegmentMap;

    /// <summary>The segment features of the frame stored here (frames with segmentation_update_data = 0 inherit them).</summary>
    public Av1SegmentationDataSet SegmentationData;

    /// <summary>The frame's global motion parameters: later frames code theirs relative to these (dav1d
    /// refs[primary].frame_hdr->gmv).</summary>
    public Av1WarpedMotionParams[] Gmv = new Av1WarpedMotionParams[Av1Constants.RefsPerFrame];

    /// <summary>
    /// Decoded pixel data per plane. Samples are stored as ushort[] for all bit depths
    /// (8-bit values occupy 0..255, 10-bit 0..1023, 12-bit 0..4095) so one code path
    /// serves every bit depth. Output extraction downshifts high-bit-depth to 8-bit.
    /// </summary>
    public ushort[]?[] Planes = new ushort[3][];

    /// <summary>Stride in bytes for each plane.</summary>
    public int[] Strides = new int[3];

    /// <summary>Film grain parameters of the frame stored here (load_grain_params / show_existing_frame).</summary>
    public Av1FilmGrainData FilmGrain;
    public bool FilmGrainPresent;

    /// <summary>CDF context snapshot from this reference frame (for CDF update).</summary>
    public Av1CdfContext? CdfSnapshot;

    /// <summary>
    /// The saved 8x8 motion field of the (inter) frame stored here (dav1d refs[].refmvs; null for intra frames), and
    /// that frame's own reference order hints (refs[].refpoc) for temporal MV projection.
    /// </summary>
    public Av1RefMvsTemporalBlock[]? TemporalMvs;
    public byte[] RefPoc = new byte[7];

    /// <summary>The frame's loop filter mode / reference deltas (inherited through primary_ref_frame).</summary>
    public Av1LoopfilterModeRefDeltas LfModeRefDeltas;

    /// <summary>An independent copy (planes, CDFs, order hints, global motion); the segment map and motion field
    /// are shared (never written after the frame that produced them).</summary>
    public Av1ReferenceFrame DeepCopy()
    {
        var c = (Av1ReferenceFrame)MemberwiseClone();
        c.Gmv = (Av1WarpedMotionParams[])Gmv.Clone();
        c.Planes = new ushort[3][];
        for (int p = 0; p < 3; p++) c.Planes[p] = (ushort[]?)Planes[p]?.Clone();
        c.Strides = (int[])Strides.Clone();
        c.RefPoc = (byte[])RefPoc.Clone();
        if (CdfSnapshot != null) { c.CdfSnapshot = new Av1CdfContext(); c.CdfSnapshot.CopyFrom(CdfSnapshot); }
        return c;
    }

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
    public ushort[] IpredEdgeY = Array.Empty<ushort>();

    /// <summary>U plane edge buffer for intra prediction across SB boundaries.</summary>
    public ushort[] IpredEdgeU = Array.Empty<ushort>();

    /// <summary>V plane edge buffer for intra prediction across SB boundaries.</summary>
    public ushort[] IpredEdgeV = Array.Empty<ushort>();

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

    /// <summary>This frame's motion field (dav1d f->mvs; null for intra frames) and reference order hints (f->refpoc).</summary>
    public Av1RefMvsTemporalBlock[]? CurrentRp;
    public byte[] CurrentRefPoc = new byte[7];

    /// <summary>
    /// This frame's segment ids (dav1d f->cur_segmap, B4Stride x 32 * sb128 rows; null without segmentation) and the
    /// primary reference frame's map it may predict from (f->prev_segmap).
    /// </summary>
    public byte[]? CurSegMap, PrevSegMap;

    /// <summary>The CDFs the current frame started from (dav1d f->in_cdf).</summary>
    public Av1CdfContext? InCdf;

    /// <summary>Block stride for the block grid (dav1d: f->b4_stride).</summary>
    public int B4Stride;

    /// <summary>Block array for frame threading / sub8×8 chroma lookups.</summary>
    public Av1Block[]? Blocks;

    // ──── Loop Filter State ────

    /// <summary>SB128 columns in frame (dav1d: f->sb128w).</summary>
    public int Sb128W;

    /// <summary>dav1d f->lf.tx_lpf_right_edge: per tile column, the left-context tx_lpf_y/uv at the right edge of
    /// each SB row (4-unit rows, stride alignH / alignH >> ss_ver) — used to fix deblock strength at tile-column starts.</summary>
    public byte[] TxLpfRightEdgeY = [], TxLpfRightEdgeUv = [];
    /// <summary>dav1d f->lf.start_of_tile_row: per SB row, the tile-row index when it starts a tile row (else 0).</summary>
    public int[] StartOfTileRow = [];
    /// <summary>The per-tile-row above block contexts (dav1d f->a), for the tile-row deblock fix-up.</summary>
    public Av1BlockContextManaged[]? AboveCtx;

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
    public ushort[]?[] LrLpfLine = new ushort[3][];

    /// <summary>Per-SB-row snapshot of the LPF line buffer. The whole-frame CopyLpf pass
    /// fills the rolling LrLpfLine which is then overwritten each SB row; LR runs as a
    /// later whole-frame pass, so each SB row's boundary rows are snapshotted here (indexed
    /// by sby*NumLines*stride) and read back by ApplyLoopRestoration.</summary>
    public ushort[]?[] LrLpfSnap = new ushort[3][];
    public int LrLpfNumLines;

    /// <summary>Width of the sr_sb128 row for LR mask indexing.
    /// dav1d: f->sr_sb128w.</summary>
    public int SrSb128W;

    /// <summary>
    /// Super-resolution (frame header width[0] != width[1]): loop restoration, its line buffers, the output and the
    /// references work on the upscaled picture. LrYStride/LrUvStride are the strides of those upscaled planes (the plain
    /// strides without super-res); ResizeStep/ResizeStart are dav1d f->resize_step/resize_start for luma [0] and
    /// subsampled chroma [1].
    /// </summary>
    public bool SuperRes;
    public int LrYStride, LrUvStride;
    public int[] ResizeStep = new int[2], ResizeStart = new int[2];

    /// <summary>Luma plane stride (convenience alias for CurrentStrides[0]).</summary>
    public int YStride;

    /// <summary>Chroma plane stride (convenience alias for CurrentStrides[1]).</summary>
    public int UvStride;

    // ──── Decoded Frame Output ────

    /// <summary>Current frame pixel buffers (one per plane: Y, U, V). ushort samples for all bit depths.</summary>
    public ushort[]?[] CurrentPlanes = new ushort[3][];

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
                ArrayPool<ushort>.Shared.Return(CurrentPlanes[i]);
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
