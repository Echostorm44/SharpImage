// AV1 decoder — main entry point implementing IVideoDecoder
// Ported from dav1d: src/obu.c (dav1d_parse_obus), src/decode.c (dav1d_decode_frame)
// Reference: VideoLAN dav1d, BSD-2-Clause

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 software video decoder. Parses OBU temporal units from IVF frames,
/// decodes tiles, applies in-loop filters, and outputs visible frames.
/// </summary>
internal sealed class Av1Decoder
{
    private readonly Av1DecoderContext ctx = new();
    private readonly Av1DecoderSequenceHeader seqHdr = new();
    private readonly Av1DecoderFrameHeader frameHdr = new();
    private bool hasSequenceHeader;
    private bool isReady;
    private int _dbgFilterDump;

    // Enable MSAC tracing for debugging
    public static bool EnableMsacTrace;
    public static bool EnableDav1dTrace;
    private static bool _traceAlreadyStarted;

    // Global verbose debug flag (set to false for fast tests)
    public static bool VerboseDbg = false;

    // CDF override: load dav1d's adapted CDF values from a dump file
    public static string? Dav1dCdfSnapshotPath;

    // Enable pre-deblocking Y plane dump (writes to debug file)
    public static bool DumpPreDeblockY;
    private static bool _preDeblockDumped;
    private static bool _postDeblockDumped;

    // CDEF per-block decision dump (frame 1), for diffing against dav1d
    public static bool DumpCdefDecisions;
    public static System.IO.StreamWriter? CdefDecisionWriter;

    // Tile data collected during OBU parsing for the current frame
    [ThreadStatic] internal static string? LastDecodeError;   // per thread: concurrent decodes must not clobber it
    private readonly TileGroup[] tileGroups = new TileGroup[Av1Constants.MaxTileCols * Av1Constants.MaxTileRows];   // one tile per group at most
    private int tileGroupCount;
    private int tilesCollected;

    // Persistent above-context arrays (one per sb128 column per tile row)
    private Av1BlockContextManaged[]? aboveCtx;

    // Persistent task context (reused across SB rows to avoid alloc)
    private readonly Av1TaskContext taskCtx = new();
    // One task context per tile column when tile columns decode in parallel (index 0 is taskCtx).
    private Av1TaskContext[] tileTaskCtx = [];

    /// <summary>Threads for decoding the tile columns of an SB row in parallel (1 = serial). The output is
    /// identical for any value.</summary>
    public int MaxThreads { get; set; } = 1;

    private static readonly bool CdfDump = Environment.GetEnvironmentVariable("AV1_CDFDUMP") == "1";

    private struct TileGroup
    {
        public int StartTile;
        public int EndTile;
        public byte[] Data;
        public int Offset;
        public int Length;
    }

    // IVideoDecoder implementation
    public string CodecId => "av1";
    public bool IsHardwareAccelerated => false;
    public int Width => frameHdr.SuperResUpscaledWidth;
    public int Height => frameHdr.Height;
    public PixelFormat OutputFormat => PixelFormat.Yuv420P;
    public bool IsReady => isReady;

    /// <summary>True when the decoded sequence is monochrome (I400) — chroma planes are not meaningful.</summary>
    public bool Monochrome => seqHdr.Monochrome;

    /// <summary>Sequence-header colour description (CICP; 2 = unspecified when absent) and color_range.</summary>
    public int ColorPrimaries => (int)seqHdr.ColorPrimaries;
    public int TransferCharacteristics => (int)seqHdr.TransferCharacteristics;
    public int MatrixCoefficients => (int)seqHdr.MatrixCoefficients;
    public bool FullColorRange => seqHdr.ColorRange != 0;

    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // AV1 config is in-band (sequence header OBU) — no codec private needed
        isReady = true;
        return true;
    }

    public void Flush()
    {
        hasSequenceHeader = false;
        for (int i = 0; i < 8; i++)
            ctx.RefFrames[i].Reset();
    }

    public void DrainPendingFrames()
    {
        // AV1 doesn't use B-frame reorder — no pending frames
    }

    public void Dispose()
    {
        ctx.Dispose();
    }

    /// <summary>
    /// Decode one IVF frame (temporal unit). A temporal unit may contain
    /// multiple OBUs: temporal delimiter, sequence header, frame header,
    /// tile group(s). Returns the visible frame, or null if the frame is
    /// not visible (e.g., altref).
    /// </summary>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        // One output per temporal unit: the shown frame with the highest spatial id (dav1d with all_layers = 0 outputs
        // only the operating point's top spatial layer; a plain stream has one shown frame per TU anyway).
        var frames = DecodeTemporalUnit(data, presentationTimeTicks);
        if (frames.Count == 0) return null;
        int best = 0;
        for (int k = 1; k < frames.Count; k++)
            if (frames[k].SpatialId >= frames[best].SpatialId) best = k;
        for (int k = 0; k < frames.Count; k++)
            if (k != best) frames[k].Frame.Dispose();
        return frames[best].Frame;
    }

    /// <summary>dav1d operating_point: OBUs with an extension header whose temporal / spatial layer is not in this
    /// operating point's operating_point_idc are dropped (AVIF 'a1op').</summary>
    public int OperatingPoint { get; set; }

    /// <summary>dav1d frame_size_limit: a frame header whose upscaled width x height exceeds this many pixels fails the
    /// decode before anything is allocated (0: no limit). libavif passes its imageSizeLimit here.</summary>
    public long FrameSizeLimit { get; set; }

    /// <summary>
    /// Decodes one temporal unit and returns every shown frame in decode order with its spatial id (dav1d all_layers:
    /// a layered / progressive AVIF item yields one frame per layer). Each frame is decoded as soon as its tiles are
    /// complete, so later frames of the unit (e.g. an enhancement layer) can reference earlier ones.
    /// </summary>
    internal List<(DecodedVideoFrame Frame, int SpatialId)> DecodeTemporalUnit(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        var outputs = new List<(DecodedVideoFrame Frame, int SpatialId)>();
        if (data.Length == 0)
            return outputs;

        int offset = 0;
        tileGroupCount = 0;
        tilesCollected = 0;
        bool haveFrameHeader = false;

        bool FinishFrame()
        {
            int totalTiles = frameHdr.TileCols * frameHdr.TileRows;
            if (!haveFrameHeader || tilesCollected < totalTiles) return true;
            haveFrameHeader = false;
            try { DecodeFrame(ReadOnlySpan<byte>.Empty); }   // tile data was copied by ParseTileGroupObu
            catch (Exception ex) { LastDecodeError = ex.ToString(); AvDbg.W($"[DECODE-FRAME-ERROR] {ex.GetType().Name}: {ex.Message}"); return false; }
            try { UpdateReferenceFrames(); }
            catch (Exception ex) { LastDecodeError = $"UpdateReferenceFrames: {ex}"; return false; }
            DumpDecodedFrame();
            if (frameHdr.ShowFrame)
            {
                isReady = true;
                var frame = ExtractOutputFrame(presentationTimeTicks);
                if (frame != null) outputs.Add((frame, frameHdr.SpatialId));
            }
            return true;
        }

        while (offset < data.Length)
        {
            // Parse OBU header (§5.3.1)
            var gb = new Av1GetBits(data.Slice(offset));
            gb.GetBit(); // obu_forbidden_bit
            int obuType = (int)gb.GetBits(4);
            bool hasExtension = gb.GetBool();
            bool hasLengthField = gb.GetBool();
            gb.GetBit(); // obu_reserved_1bit

            int temporalId = 0, spatialId = 0;
            if (hasExtension)
            {
                temporalId = (int)gb.GetBits(3);
                spatialId = (int)gb.GetBits(2);
                gb.GetBits(3); // extension_header_reserved_3bits
            }

            int headerBytes = gb.BytePosition;
            long obuSizeLong;
            if (hasLengthField)
            {
                obuSizeLong = gb.GetUleb128();
                headerBytes = gb.BytePosition;
            }
            else
            {
                // Without length field, OBU extends to end of temporal unit
                obuSizeLong = data.Length - offset - headerBytes;
            }

            // dav1d_parse_obus: an OBU (header or payload) running past the data is an error, not a silent stop.
            if (headerBytes > data.Length - offset || obuSizeLong > data.Length - offset - headerBytes)
                throw new InvalidDataException("AV1 OBU extends past the end of the data.");
            int obuSize = (int)obuSizeLong;

            var obuPayload = data.Slice(offset + headerBytes, obuSize);
            offset += headerBytes + obuSize;

            // Operating point selection (dav1d_parse_obus): drop layers outside the chosen operating point.
            if (obuType != 1 && obuType != 2 && hasExtension && hasSequenceHeader && seqHdr.NumOperatingPoints > 0)
            {
                int idc = seqHdr.OperatingPoints[Math.Clamp(OperatingPoint, 0, seqHdr.NumOperatingPoints - 1)].Idc;
                if (idc != 0 && (((idc >> temporalId) & 1) == 0 || ((idc >> (spatialId + 8)) & 1) == 0))
                    continue;
            }

            switch (obuType)
            {
                case 1: // OBU_SEQUENCE_HEADER
                    ParseSequenceHeaderObu(obuPayload);
                    break;

                case 2: // OBU_TEMPORAL_DELIMITER
                    break;

                case 3: // OBU_FRAME_HEADER
                case 6: // OBU_FRAME (frame header + tile group combined)
                    if (!hasSequenceHeader)
                        break;
                    haveFrameHeader = ParseFrameHeaderObu(obuPayload, temporalId, spatialId, out int fhBytesConsumed, isObuFrame: obuType == 6);
                    if (haveFrameHeader && frameHdr.ShowExistingFrame)
                    {
                        haveFrameHeader = false;
                        var shown = HandleShowExistingFrame(presentationTimeTicks);
                        if (shown != null) outputs.Add((shown, spatialId));
                        break;
                    }
                    if (haveFrameHeader && obuType == 6)
                    {
                        // OBU_FRAME: tile data starts immediately after frame header
                        int tileDataOffset = fhBytesConsumed;
                        if (tileDataOffset < obuSize)
                            ParseTileGroupObu(obuPayload.Slice(tileDataOffset), offset - obuSize + tileDataOffset, isObuFrame: true);
                        if (!FinishFrame()) return Fail(outputs);
                    }
                    break;

                case 4: // OBU_TILE_GROUP
                    if (!hasSequenceHeader || !haveFrameHeader)
                        break;
                    ParseTileGroupObu(obuPayload, offset - obuSize);
                    if (!FinishFrame()) return Fail(outputs);
                    break;

                case 5: // OBU_METADATA — skip for now
                case 7: // OBU_REDUNDANT_FRAME_HEADER
                case 15: // OBU_PADDING
                default:
                    break;
            }
        }
        return outputs;
    }

    // A frame failed to decode: nothing from this temporal unit is returned (the caller reports LastDecodeError).
    private static List<(DecodedVideoFrame Frame, int SpatialId)> Fail(List<(DecodedVideoFrame Frame, int SpatialId)> outputs)
    {
        foreach (var o in outputs) o.Frame.Dispose();
        outputs.Clear();
        return outputs;
    }

    // ======================================================================
    // OBU Parsing
    // ======================================================================

    private void ParseSequenceHeaderObu(ReadOnlySpan<byte> payload)
    {
        var result = Av1ObuParser.ParseSequenceHeader(seqHdr, payload);
        if (result == Av1ObuParser.ParseResult.Ok)
        {
            hasSequenceHeader = true;
            ctx.SequenceHeader = seqHdr;
            ctx.HasSequenceHeader = true;

            ctx.BitDepth = seqHdr.BitDepth;
            ctx.BitDepthMax = (1 << seqHdr.BitDepth) - 1;
        }
    }

    private bool ParseFrameHeaderObu(ReadOnlySpan<byte> payload, int temporalId, int spatialId, out int bytesConsumed, bool isObuFrame = false)
    {
        frameHdr.Reset();   // the header object is reused: nothing may leak from the previous frame (e.g. force_integer_mv)
        frameHdr.TemporalId = (byte)temporalId;
        frameHdr.SpatialId = (byte)spatialId;

        var result = Av1ObuParser.ParseFrameHeader(frameHdr, seqHdr, ctx.RefFrames, payload, out bytesConsumed, isObuFrame);
        if (result != Av1ObuParser.ParseResult.Ok)
            return false;
        if (FrameSizeLimit > 0 && (long)frameHdr.SuperResUpscaledWidth * frameHdr.Height > FrameSizeLimit)
            throw new InvalidDataException($"AV1 frame size {frameHdr.SuperResUpscaledWidth}x{frameHdr.Height} exceeds the limit of {FrameSizeLimit} pixels.");

        // Propagate sequence header fields that the frame header needs
        frameHdr.PixelLayout = seqHdr.Layout;

        ctx.FrameHeader = frameHdr;

        // Reset tile data for new frame
        tileGroupCount = 0;
        tilesCollected = 0;

        return true;
    }

    private void ParseTileGroupObu(ReadOnlySpan<byte> payload, int dataOffsetInFrame, bool isObuFrame = false)
    {
        // Parse tile group header (§5.11.1)
        // NOTE: For OBU_FRAME, there is NO tile_group_obu() wrapper —
        // tile data (tile_size fields) starts immediately after frame header bytes.
        // For BOTH OBU_TILE_GROUP and OBU_FRAME, the tile_group_obu() carries a header when NumTiles > 1:
        // tile_start_and_end_present_flag (1 bit), optional tg_start/tg_end, then byte_alignment before the tile
        // data. Only single-tile (NumTiles==1) has no such flag. Previously this was gated on !isObuFrame, so a
        // multi-tile OBU_FRAME (what libaom emits for larger frames) skipped the flag + byte-alignment and read the
        // tile data one byte early -> MSAC desync -> whole-frame garbage. Read it whenever there are multiple tiles.
        int totalTiles = frameHdr.TileCols * frameHdr.TileRows;
        int tileStart = 0;
        int tileEnd = totalTiles - 1;
        int headerBits = 0;

        if (totalTiles > 1)
        {
            var gb = new Av1GetBits(payload);
            bool haveTilePos = gb.GetBool();
            if (haveTilePos)
            {
                int tileBits = frameHdr.TileLog2Cols + frameHdr.TileLog2Rows;
                tileStart = (int)gb.GetBits(tileBits);
                tileEnd = (int)gb.GetBits(tileBits);
            }
            gb.ByteAlign();
            headerBits = gb.BytePosition;
        }

        if (tileGroupCount < tileGroups.Length)
        {
            // Copy tile data to persistent buffer (payload is from a Span that will go out of scope)
            int tileDataLen = payload.Length - headerBits;
            byte[] tileData = ArrayPool<byte>.Shared.Rent(tileDataLen);
            payload.Slice(headerBits).CopyTo(tileData);

            tileGroups[tileGroupCount] = new TileGroup
            {
                StartTile = tileStart,
                EndTile = tileEnd,
                Data = tileData,
                Offset = 0,
                Length = tileDataLen,
            };
            tileGroupCount++;
            tilesCollected += tileEnd - tileStart + 1;
        }
    }

    // ======================================================================
    // Frame Decoding
    // ======================================================================

    private int _lrRestored, _lrSkipped;

    private void DecodeFrame(ReadOnlySpan<byte> temporalUnit)
    {
        var fh = frameHdr;
        var sh = seqHdr;

        // Compute frame geometry
        bool useSb128 = sh.Sb128;
        int sbSize = useSb128 ? 128 : 64;
        int sbShift = useSb128 ? 5 : 4;
        int sbStep = 1 << sbShift;

        // MI grid dims: MiCols = 2*ceil(w/8) (always even), matching dav1d's f->bw. (Previously (w+3)>>2 = ceil(w/4),
        // which is odd for non-multiple-of-8 sizes and disagrees with the bitstream's partition edge logic.)
        int bw = ((fh.CodedWidth + 7) >> 3) << 1;   // width in 4px units (MiCols)
        int bh = ((fh.Height + 7) >> 3) << 1;       // height in 4px units (MiRows)
        int sbw = (bw + sbStep - 1) >> sbShift;
        int sbh = (bh + sbStep - 1) >> sbShift;
        int sb128w = (bw + 31) >> 5;

        ctx.UseSuperBlock128 = useSb128;
        ctx.Bw = bw;
        ctx.Bh = bh;
        ctx.Width4 = bw;
        ctx.Height4 = bh;
        ctx.SuperBlockCols = sbw;
        ctx.SuperBlockRows = sbh;
        ctx.SbStep = sbStep;
        ctx.SbShift = sbShift;
        ctx.Sb128w = sb128w;
        ctx.Sb128W = sb128w;
        // dav1d keeps two frame sizes: bw/bh (MI grid, 2*ceil(dim/8)) for block/CDEF logic, and w4/h4
        // (ceil(dim/4)) for the loop-filter masks and row/column ranges. Using the MI grid for deblocking filtered an
        // extra 4px row/column past odd picture edges that dav1d leaves alone; invisible by itself, but CDEF reads
        // those hidden pixels as neighbours and carried the difference into visible edge rows.
        ctx.W4 = (fh.CodedWidth + 3) >> 2;
        ctx.H4 = (fh.Height + 3) >> 2;
        ctx.B4Stride = sb128w * 32;

        // Pixel layout from sequence header
        ctx.PixelLayout = sh.Layout;

        int ssHor = ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int ssVer = ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        bool hasChroma = ctx.PixelLayout != Av1PixelLayout.I400;

        // Compute restoration planes bitmask early — AllocateFrameBuffers needs it
        ctx.RestorePlanes =
            ((fh.GetLrType(0) != Av1RestorationType.None ? 1 : 0) << 0) |
            ((fh.GetLrType(1) != Av1RestorationType.None ? 1 : 0) << 1) |
            ((fh.GetLrType(2) != Av1RestorationType.None ? 1 : 0) << 2);

        // Allocate frame buffers
        AllocateFrameBuffers(fh.CodedWidth, fh.Height, ssHor, ssVer, hasChroma);

        // Allocate tile states
        ctx.AllocateTileStates(fh.TileCols, fh.TileRows);

        // Motion compensation clips / scales intermediates by the stream's bit depth (thread-static, like the
        // restoration filter's): set it for every frame, not only on the IntraBC path.
        Av1MotionComp.McBitDepth = ctx.BitDepth;

        // Reference scaling + global-motion warp eligibility per reference (dav1d decode.c, dav1d_submit_frame):
        // svc scale / step (14-bit / 10-bit fixed point) for refs of another size; a global warp needs valid shear
        // parameters (computed into the header's gmv), integer-mv off and an unscaled reference.
        for (int i = 0; i < 7; i++)
        {
            ctx.Svc[i, 0] = default;
            ctx.Svc[i, 1] = default;
            ctx.GmvWarpAllowed[i] = false;
            if (!fh.IsInterOrSwitch) continue;
            int refIdx = fh.GetRefIdx(i);
            if (refIdx < 0 || refIdx > 7) continue;
            var rf = ctx.RefFrames[refIdx];
            if (!rf.Valid || fh.CodedWidth * 2 < rf.Width || fh.Height * 2 < rf.Height
                || fh.CodedWidth > rf.Width * 16 || fh.Height > rf.Height * 16)
                throw new System.IO.InvalidDataException("AV1 reference frame has an unsupported size for this frame.");
            if (fh.CodedWidth != rf.Width || fh.Height != rf.Height)
            {
                int sx = ((rf.Width << 14) + (fh.CodedWidth >> 1)) / fh.CodedWidth;
                int sy = ((rf.Height << 14) + (fh.Height >> 1)) / fh.Height;
                ctx.Svc[i, 0] = new Av1ScalingParams { Scale = sx, Step = (sx + 8) >> 4 };
                ctx.Svc[i, 1] = new Av1ScalingParams { Scale = sy, Step = (sy + 8) >> 4 };
            }
            ctx.GmvWarpAllowed[i] = fh.Gmv[i].Type > Av1WarpedMotionType.Translation && !fh.ForceIntegerMv
                && !Av1WarpMv.GetShearParams(ref fh.Gmv[i]) && ctx.Svc[i, 0].Scale == 0;
        }

        // Distance weights for COMP_INTER_WEIGHTED_AVG (dav1d decode_frame_init "setup jnt_comp weights").
        if (fh.SwitchableCompRefs)
        {
            ReadOnlySpan<byte> quantDistWeight = [2, 3, 2, 5, 2, 7];
            ReadOnlySpan<byte> quantDistLookup = [9, 7, 11, 5, 12, 4, 13, 3];
            for (int i = 0; i < 7; i++)
            {
                int ref0Poc = ctx.RefFrames[fh.GetRefIdx(i)].OrderHint;
                for (int j = i + 1; j < 7; j++)
                {
                    int ref1Poc = ctx.RefFrames[fh.GetRefIdx(j)].OrderHint;
                    int d1 = Math.Min(Math.Abs(Av1ObuParser.GetPocDiff(seqHdr.OrderHintNBits, ref0Poc, fh.FrameOffset)), 31);
                    int d0 = Math.Min(Math.Abs(Av1ObuParser.GetPocDiff(seqHdr.OrderHintNBits, ref1Poc, fh.FrameOffset)), 31);
                    int order = d0 <= d1 ? 1 : 0;
                    int k;
                    for (k = 0; k < 3; k++)
                    {
                        int c0 = quantDistWeight[k * 2 + order], c1 = quantDistWeight[k * 2 + (1 - order)];
                        int d0c0 = d0 * c0, d1c1 = d1 * c1;
                        if ((d0 > d1 && d0c0 < d1c1) || (d0 <= d1 && d0c0 > d1c1)) break;
                    }
                    ctx.JntWeights[i, j] = quantDistLookup[k * 2 + order];
                }
            }
        }

        // Initialize CDF contexts
        Av1CdfContext? refCdf = null;
        if (fh.PrimaryRefFrame != 7)
        {
            int refIdx = fh.GetRefIdx(fh.PrimaryRefFrame);
            if (refIdx >= 0 && refIdx < ctx.RefFrames.Length &&
                ctx.RefFrames[refIdx].CdfSnapshot != null)
            {
                refCdf = ctx.RefFrames[refIdx].CdfSnapshot;
            }
        }
        AvDbg.W($"[CDF-INIT] PrimaryRefFrame={fh.PrimaryRefFrame} RefreshContext={fh.RefreshContext} refCdfNull={refCdf == null} QuantBaseQIdx={fh.QuantBaseQIdx}");
        ctx.InitializeTileCdfs(fh.QuantBaseQIdx, refCdf);
        // dav1d in_cdf: what this frame leaves for its references when it does not refresh the context, and the base
        // of the frame-end save when it does.
        ctx.InCdf ??= new Av1CdfContext();
        ctx.InCdf.CopyFrom(ctx.TileStates![0].Cdf);

        // Apply dav1d CDF override after init (for testing/debugging)
        // Only for inter frames with a valid primary reference (dav1d: primary_ref_frame != NONE)
        // When PrimaryRefFrame==7 (NONE), dav1d uses defaults — snapshot loading would mask bugs.
        if (Dav1dCdfSnapshotPath != null && fh.IsInterOrSwitch && fh.PrimaryRefFrame != 7)
        {
            int priRef = fh.GetRefIdx(fh.PrimaryRefFrame);
            string specificPath = Dav1dCdfSnapshotPath + $"_f{priRef}.txt";
            if (File.Exists(specificPath))
            {
                foreach (var ts in ctx.TileStates!)
                    ts.Cdf.LoadCoefCdfsFromDav1dDump(specificPath);
                AvDbg.W($"[CDF-DAV1D] Loaded CDF snapshot from {specificPath} (pri ref idx {priRef})");
                // DEBUG: verify loaded values
                var checkCdf = ctx.TileStates![0].Cdf;
                AvDbg.W($"[CDF-CHECK] Partition[4][0]={checkCdf.Mode.Partition[4][0]}, Partition[8][0]={checkCdf.Mode.Partition[8][0]}, Partition[12][0]={checkCdf.Mode.Partition[12][0]}");
                AvDbg.W($"[CDF-CHECK] NewmvMode[3][0]={checkCdf.Mode.NewmvMode[3][0]}");
            }
        }

        // Allocate above context arrays
        int aboveCtxCount = sb128w * fh.TileRows;
        if (aboveCtx == null || aboveCtx.Length < aboveCtxCount)
        {
            aboveCtx = new Av1BlockContextManaged[aboveCtxCount];
            for (int i = 0; i < aboveCtxCount; i++)
                aboveCtx[i] = new Av1BlockContextManaged();
        }

        // Initialize dequant for each tile
        InitializeTileDequant();

        // Allocate block grid
        int blockGridSize = ctx.B4Stride * (bh + 32);
        if (ctx.Blocks == null || ctx.Blocks.Length < blockGridSize)
            ctx.Blocks = new Av1Block[blockGridSize];

        // Initialize LF level array
        ctx.LfLevel = new byte[ctx.B4Stride * bh, 4];

        // Allocate per-SB loop filter masks
        int sb128h = (ctx.Bh + 31) >> 5;
        if (ctx.LfMasks == null || ctx.LfMasks.Length < sb128w)
        {
            ctx.LfMasks = new Av1FilterMask[sb128w];
            for (int i = 0; i < sb128w; i++)
                ctx.LfMasks[i] = new Av1FilterMask();
        }
        // Allocate backup for CDEF/LR per-row data
        if (ctx.LfMasksRows == null || ctx.LfMasksRows.Length < sb128w * sb128h)
        {
            ctx.LfMasksRows = new Av1FilterMask[sb128w * sb128h];
            for (int i = 0; i < ctx.LfMasksRows.Length; i++)
                ctx.LfMasksRows[i] = new Av1FilterMask();
        }

        // Compute loop filter EIH lookup table
        Av1LoopFilter.CalcEih(ctx.LfLimLut, fh.LfSharpness);

        // Compute frame-level loop filter level values
        Av1LoopFilter.CalcLfValues(ctx.LfLvl, fh, new ReadOnlySpan<sbyte>(new sbyte[4]));
        // Copy frame-level LfLvl to each tile state (will be overridden if delta_lf is used)
        for (int tileIdx = 0; tileIdx < fh.TileCols * fh.TileRows; tileIdx++)
            Array.Copy(ctx.LfLvl, ctx.TileStates![tileIdx].LfLvl, ctx.LfLvl.Length);

        // Allocate LR masks (uses RestorePlanes computed above)
        if (ctx.RestorePlanes != 0)
        {
            int sb128h2 = (bh + 31) >> 5;
            int lrMaskCount = sb128h2 * ((fh.SuperResUpscaledWidth + 127) >> 7);
            if (ctx.LrMasks == null || ctx.LrMasks.Length < lrMaskCount)
            {
                ctx.LrMasks = new Av1RestorationInfo[lrMaskCount];
                for (int i = 0; i < lrMaskCount; i++)
                    ctx.LrMasks[i] = new Av1RestorationInfo();
            }
            else
            {
                // Reset existing LR masks
                for (int i = 0; i < lrMaskCount; i++)
                    Array.Clear(ctx.LrMasks[i].Lr);
            }
        }
        ctx.SrSb128W = (fh.SuperResUpscaledWidth + 127) >> 7;   // dav1d sr_sb128w

        // Parse tile sizes from tile groups and assign per-tile data ranges
        SetupTileData();

        // Decode all tile rows and superblock rows
        DecodeFrameMain(ssHor, ssVer, hasChroma);

        // Dump frame 0 end-state CDFs for comparison with dav1d
        if (fh.FrameOffset == 0 && ctx.TileStates != null)
        {
            var c = ctx.TileStates[0].Cdf;
            AvDbg.W($"[OUR-F0-CDF] Partition[4][0]={c.Mode.Partition[4][0]} cnt={c.Mode.Partition[4][9]}");
            AvDbg.W($"[OUR-F0-CDF] Partition[8][0]={c.Mode.Partition[8][0]} cnt={c.Mode.Partition[8][9]}");
            AvDbg.W($"[OUR-F0-CDF] Partition[12][0]={c.Mode.Partition[12][0]} cnt={c.Mode.Partition[12][9]}");
            AvDbg.W($"[OUR-F0-CDF] Skip[0][0]={c.Mode.Skip[0][0]} cnt={c.Mode.Skip[0][1]}");
            AvDbg.W($"[OUR-F0-CDF] Intra[0][0]={c.Mode.Intra[0][0]} cnt={c.Mode.Intra[0][1]}");
            AvDbg.W($"[OUR-F0-CDF] CoefSkip[0][0]={c.Coef.CoefSkip[0][0]} cnt={c.Coef.CoefSkip[0][1]}");
            AvDbg.W($"[OUR-F0-CDF] CoefSkip[13][0]={c.Coef.CoefSkip[13][0]} cnt={c.Coef.CoefSkip[13][1]}");
            AvDbg.W($"[OUR-F0-CDF] CoefSkip[14][0]={c.Coef.CoefSkip[14][0]} cnt={c.Coef.CoefSkip[14][1]}");
            AvDbg.W($"[OUR-F0-CDF] CoefSkip[26][0]={c.Coef.CoefSkip[26][0]} cnt={c.Coef.CoefSkip[26][1]}");
            AvDbg.W($"[OUR-F0-CDF] DcSign[0][0]={c.Coef.DcSign[0][0]} cnt={c.Coef.DcSign[0][1]}");
            AvDbg.W($"[OUR-F0-CDF] EobBaseTok[0][0]={c.Coef.EobBaseTok[0][0]} cnt={c.Coef.EobBaseTok[0][2]}");
        }
    }

    private void AllocateFrameBuffers(int width, int height, int ssHor, int ssVer, bool hasChroma)
    {
        // Align strides to 64 for cache friendliness
        int yStride = (width + 63) & ~63;
        // Chroma stride must cover the SB-aligned luma width (blocks reconstruct out to the MI grid edge, beyond
        // the displayed width). Deriving it from floor(width/2) under-allocated odd widths just past a multiple of
        // 128 (e.g. 257: stride 128 but chroma runs to 132) — rows overlapped and the reference copy threw.
        int uvStride = hasChroma ? (((yStride >> ssHor) + 63) & ~63) : 0;
        // Superblocks tile the MI grid (MiRows*4 rows), which for non-multiple-of-8 heights exceeds the displayed
        // height — allocate to the SB-aligned height so edge blocks writing past `height` stay in-bounds. The
        // output still reports/reads only `height` rows.
        int allocHeight = (height + 63) & ~63;
        int uvHeight = hasChroma ? ((allocHeight + (1 << ssVer) - 1) >> ssVer) : 0;

        int ySize = yStride * allocHeight;
        int uvSize = uvStride * uvHeight;

        // Return old buffers
        for (int i = 0; i < 3; i++)
        {
            if (ctx.CurrentPlanes[i] != null)
            {
                ArrayPool<ushort>.Shared.Return(ctx.CurrentPlanes[i]);
                ctx.CurrentPlanes[i] = null;
            }
        }

        ctx.CurrentPlanes[0] = ArrayPool<ushort>.Shared.Rent(ySize);
        ctx.CurrentStrides[0] = yStride;
        ctx.YStride = yStride;

        // Super-resolution: LR, its line buffers and the output run on the upscaled picture (dav1d sr_cur).
        int srW = frameHdr.SuperResUpscaledWidth;
        ctx.SuperRes = srW != width;
        ctx.LrYStride = ctx.SuperRes ? (srW + 63) & ~63 : yStride;
        ctx.LrUvStride = hasChroma ? (ctx.SuperRes ? ((ctx.LrYStride >> ssHor) + 63) & ~63 : uvStride) : 0;
        if (ctx.SuperRes)
        {
            // dav1d scale_fac / get_upscale_x0 on the coded (cur.p.w) and upscaled widths, luma then chroma.
            static int ScaleFac(int refSz, int thisSz) => ((refSz << 14) + (thisSz >> 1)) / thisSz;
            static int UpscaleX0(int inW, int outW, int step)
            {
                int err = outW * step - (inW << 14);
                int x0 = (-((outW - inW) << 13) + (outW >> 1)) / outW + 128 - (err / 2);
                return x0 & 0x3fff;
            }
            int inCw = (width + ssHor) >> ssHor, outCw = (srW + ssHor) >> ssHor;
            ctx.ResizeStep[0] = ScaleFac(width, srW);
            ctx.ResizeStep[1] = ScaleFac(inCw, outCw);
            ctx.ResizeStart[0] = UpscaleX0(width, srW, ctx.ResizeStep[0]);
            ctx.ResizeStart[1] = UpscaleX0(inCw, outCw, ctx.ResizeStep[1]);
        }

        if (hasChroma)
        {
            ctx.CurrentPlanes[1] = ArrayPool<ushort>.Shared.Rent(uvSize);
            ctx.CurrentPlanes[2] = ArrayPool<ushort>.Shared.Rent(uvSize);
            ctx.CurrentStrides[1] = uvStride;
            ctx.CurrentStrides[2] = uvStride;
            ctx.UvStride = uvStride;
        }

        // Allocate LR LPF line buffers (post-deblock, pre-CDEF boundary rows)
        // dav1d: decode.c:2973 — for single-threaded: num_lines = 12
        if (ctx.RestorePlanes != 0)
        {
            // 12 rows per SB row; the per-SB-row snapshot below copies 24 for 128x128 superblocks.
            int numLines = seqHdr.Sb128 ? 24 : 12;
            int yLpfSize = ctx.LrYStride * numLines;
            int uvLpfSize = ctx.LrUvStride * numLines;
            if (ctx.LrLpfLine[0] == null || ctx.LrLpfLine[0].Length < yLpfSize)
                ctx.LrLpfLine[0] = new ushort[yLpfSize];
            if (hasChroma)
            {
                if (ctx.LrLpfLine[1] == null || ctx.LrLpfLine[1].Length < uvLpfSize)
                    ctx.LrLpfLine[1] = new ushort[uvLpfSize];
                if (ctx.LrLpfLine[2] == null || ctx.LrLpfLine[2].Length < uvLpfSize)
                    ctx.LrLpfLine[2] = new ushort[uvLpfSize];
            }
        }

        // Zero-fill
        Array.Clear(ctx.CurrentPlanes[0], 0, ySize);
        if (hasChroma)
        {
            Array.Clear(ctx.CurrentPlanes[1]!, 0, uvSize);
            Array.Clear(ctx.CurrentPlanes[2]!, 0, uvSize);
        }
    }

    private void InitializeTileDequant()
    {
        var fh = frameHdr;
        var sh = seqHdr;

        for (int tileIdx = 0; tileIdx < fh.TileCols * fh.TileRows; tileIdx++)
        {
            var ts = ctx.TileStates![tileIdx];
            ts.LastQIdx = fh.QuantBaseQIdx;
            Array.Clear(ts.LastDeltaLf);   // dav1d setup_tile: last_delta_lf = 0

            for (int seg = 0; seg < 8; seg++)
            {
                int qIdx = fh.QuantBaseQIdx;
                if (fh.SegmentationEnabled)
                {
                    int segDelta = fh.SegmentationData.Segments[seg].DeltaQ;
                    qIdx = Math.Clamp(qIdx + segDelta, 0, 255);
                }
                fh.SegmentationQIdx[seg] = (byte)qIdx;   // frame-level (delta-q ignored, as the spec's tx-set rule wants)
            }
            FillDequant(ts, fh, fh.QuantBaseQIdx, ctx.BitDepth);
        }
    }

    /// <summary>dav1d init_quant_tables: per-segment Y/U/V DC+AC dequantizers for a (possibly delta-q adjusted)
    /// base qindex. Called at tile start and again whenever a superblock's delta_q changes the qindex.</summary>
    internal static void FillDequant(Av1TileState ts, Av1DecoderFrameHeader fh, int baseQIdx, int bitDepth)
    {
        for (int seg = 0; seg < 8; seg++)
        {
            int yac = fh.SegmentationEnabled
                ? Math.Clamp(baseQIdx + fh.SegmentationData.Segments[seg].DeltaQ, 0, 255)
                : baseQIdx;
            ts.Dq[seg, 0, 0] = (ushort)GetDcDequant(yac + fh.QuantYDcDelta, bitDepth);
            ts.Dq[seg, 0, 1] = (ushort)GetAcDequant(yac, bitDepth);
            ts.Dq[seg, 1, 0] = (ushort)GetDcDequant(yac + fh.QuantUDcDelta, bitDepth);
            ts.Dq[seg, 1, 1] = (ushort)GetAcDequant(yac + fh.QuantUAcDelta, bitDepth);
            ts.Dq[seg, 2, 0] = (ushort)GetDcDequant(yac + fh.QuantVDcDelta, bitDepth);
            ts.Dq[seg, 2, 1] = (ushort)GetAcDequant(yac + fh.QuantVAcDelta, bitDepth);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetDcDequant(int qIdx, int bitDepth)
    {
        qIdx = Math.Clamp(qIdx, 0, 255);
        int bdIdx = bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2;
        return Av1Tables.DequantTable[bdIdx, qIdx, 0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetAcDequant(int qIdx, int bitDepth)
    {
        qIdx = Math.Clamp(qIdx, 0, 255);
        int bdIdx = bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2;
        return Av1Tables.DequantTable[bdIdx, qIdx, 1];
    }

    private int _decodeFrameCount;

    private void DecodeFrameMain(int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;
        int sbShift = ctx.SbShift;
        int sbStep = ctx.SbStep;
        bool isIntra = fh.IsIntra;

        int frameIdx = _decodeFrameCount++;
        Av1IntraPred.DbgCurFrame = frameIdx;
        Av1CoeffDecode.DbgCoefDecCount = 0;
        if (frameIdx >= 17 && frameIdx <= 20)
        {
            AvDbg.W($"[SB-INFO] Frame#{frameIdx} TileRows={fh.TileRows} TileCols={fh.TileCols} SuperBlockRows={ctx.SuperBlockRows} TileRowStartSb=[");
            for (int i = 0; i <= fh.TileRows; i++)
                AvDbg.W($" {fh.TileRowStartSb[i]}");
            AvDbg.W(" ]");
        }

        // Reset above contexts for each tile row
        int sb128w = ctx.Sb128w;
        for (int i = 0; i < sb128w * fh.TileRows; i++)
            aboveCtx![i].Reset(isIntra);
        ctx.AboveCtx = aboveCtx;

        // Tile-boundary deblock bookkeeping (dav1d decode.c): right-edge tx contexts per tile column, and which SB
        // rows start a tile row.
        {
            int alignH = (ctx.Bh + 31) & ~31;
            int reSz = alignH * fh.TileCols;
            if (ctx.TxLpfRightEdgeY.Length < reSz) { ctx.TxLpfRightEdgeY = new byte[reSz]; ctx.TxLpfRightEdgeUv = new byte[reSz]; }
            if (ctx.StartOfTileRow.Length < ctx.SuperBlockRows) ctx.StartOfTileRow = new int[ctx.SuperBlockRows];
            int sbyI = 0;
            for (int tr = 0; tr < fh.TileRows && sbyI < ctx.SuperBlockRows; tr++)
            {
                ctx.StartOfTileRow[sbyI++] = tr;
                while (sbyI < Math.Min((int)fh.TileRowStartSb[tr + 1], ctx.SuperBlockRows)) ctx.StartOfTileRow[sbyI++] = 0;
            }
        }

        // Allocate the ipred-edge backup buffers (pre-deblock bottom row of each SB row,
        // used as the top reference when predicting the first block-row of the next SB row).
        // dav1d: f->ipred_edge, sized sbh * sb128w * 128 pixels per plane.
        {
            int ipredSz = ctx.SuperBlockRows * sb128w * 128;
            if (ctx.IpredEdgeY.Length < ipredSz)
            {
                ctx.IpredEdgeY = new ushort[ipredSz];
                ctx.IpredEdgeU = new ushort[ipredSz];
                ctx.IpredEdgeV = new ushort[ipredSz];
            }
        }

        // Segmentation maps (dav1d decode_frame_init): the primary reference's map when this frame predicts from it
        // (temporal update or no update) and it has the same size, then either a fresh map to write, that reference map
        // reused, or a zeroed one.
        ctx.PrevSegMap = null;
        ctx.CurSegMap = null;
        if (fh.SegmentationEnabled)
        {
            if (fh.SegmentationTemporal || !fh.SegmentationUpdateMap)
            {
                var pri = ctx.RefFrames[fh.GetRefIdx(fh.PrimaryRefFrame)];
                if (((pri.CodedWidth + 7) >> 3) == ((fh.CodedWidth + 7) >> 3) && ((pri.Height + 7) >> 3) == ((fh.Height + 7) >> 3))
                    ctx.PrevSegMap = pri.SegmentMap;
            }
            int segSize = ctx.B4Stride * (((ctx.Bh + 31) >> 5) << 5);   // dav1d b4_stride * 32 * sb128h
            if (fh.SegmentationUpdateMap)
                ctx.CurSegMap = new byte[segSize];
            else
                ctx.CurSegMap = ctx.PrevSegMap ?? new byte[segSize];
        }

        // Initialize refmvs frame for inter prediction (dav1d: dav1d_submit_frame ref_mvs setup +
        // dav1d_refmvs_init_frame): the reference order hints, a fresh motion field for this frame (kept by the
        // reference slots it refreshes), and the saved motion fields of same-size inter references.
        bool interOrSwitch = fh.IsInterOrSwitch;
        byte[,]? refRefPoc = null;
        ctx.CurrentRp = null;
        if (interOrSwitch || fh.AllowIntraBc)
        {
            int rpStride = ((fh.CodedWidth + 127) & ~127) >> 3;
            ctx.CurrentRp = new Av1RefMvsTemporalBlock[rpStride * ctx.SuperBlockRows * (seqHdr.Sb128 ? 16 : 8)];
            if (!fh.AllowIntraBc)
                for (int i = 0; i < 7; i++) ctx.CurrentRefPoc[i] = (byte)ctx.RefFrames[fh.GetRefIdx(i)].OrderHint;
            else
                Array.Clear(ctx.CurrentRefPoc);
            if (fh.UseRefFrameMvs)
            {
                var rpRefs = new Av1RefMvsTemporalBlock[]?[7];
                refRefPoc = new byte[7, 7];
                for (int i = 0; i < 7; i++)
                {
                    var rfr = ctx.RefFrames[fh.GetRefIdx(i)];
                    if (rfr.TemporalMvs != null && ((rfr.CodedWidth + 7) >> 3) == ((fh.CodedWidth + 7) >> 3)
                        && ((rfr.Height + 7) >> 3) == ((fh.Height + 7) >> 3))
                        rpRefs[i] = rfr.TemporalMvs;
                    for (int m = 0; m < 7; m++) refRefPoc[i, m] = rfr.RefPoc[m];
                }
                Av1RefMvs.InitFrame(ctx.RefMvs, seqHdr, fh, ctx.CurrentRefPoc, ctx.CurrentRp, refRefPoc, rpRefs);
            }
            else Av1RefMvs.InitFrame(ctx.RefMvs, seqHdr, fh, ctx.CurrentRefPoc, ctx.CurrentRp, null, null);
        }
        else Av1RefMvs.InitFrame(ctx.RefMvs, seqHdr, fh, ctx.CurrentRefPoc, null, null, null);
        Av1Decode.BlockTrace?.WriteLine($"F type={fh.FrameType} show={fh.ShowFrame} off={fh.FrameOffset} urfm={fh.UseRefFrameMvs} ohb={seqHdr.OrderHintNBits} n={ctx.RefMvs.MfmvCount} refpoc={string.Join(',', ctx.CurrentRefPoc)} refidx={string.Join(',', Enumerable.Range(0, 7).Select(i => fh.GetRefIdx(i)))} tm={string.Join(',', ctx.RefFrames.Select(r => r.TemporalMvs != null ? 1 : 0))} refresh={fh.RefreshFrameFlags} lf={fh.LfLevelY0},{fh.LfLevelY1},{fh.LfLevelU},{fh.LfLevelV} mrd={fh.LfModeRefDeltaEnabled} cdef={fh.CdefNBits}/{fh.CdefYStrength0} lr={fh.GetLrType(0)},{fh.GetLrType(1)},{fh.GetLrType(2)} dlf={fh.DeltaLfPresent} cw={fh.CodedWidth}/{fh.SuperResUpscaledWidth} seg={(fh.SegmentationEnabled ? 1 : 0)}{(fh.SegmentationUpdateMap ? 1 : 0)}{(fh.SegmentationTemporal ? 1 : 0)}{(fh.SegmentationUpdateData ? 1 : 0)} slotY0={string.Join(',', ctx.RefFrames.Select(r => r.Planes[0] == null ? -1 : r.Planes[0]![0]))} oh={string.Join(',', ctx.RefFrames.Select(r => r.OrderHint))}");

        // Tile columns decode in parallel when allowed and every tile column starts on a 128-pixel column (the SB128
        // above-context / loop-filter-mask entries are then never shared between two tiles' first blocks).
        bool parallelTiles = MaxThreads > 1 && fh.TileCols > 1;
        for (int tc = 0; tc < fh.TileCols && parallelTiles; tc++)
            if (!seqHdr.Sb128 && (fh.TileColStartSb[tc] & 1) != 0) parallelTiles = false;
        if (parallelTiles && tileTaskCtx.Length < fh.TileCols)
        {
            var arr = new Av1TaskContext[fh.TileCols];
            arr[0] = taskCtx;
            for (int i = 1; i < arr.Length; i++) arr[i] = i < tileTaskCtx.Length ? tileTaskCtx[i] : new Av1TaskContext();
            tileTaskCtx = arr;
        }

        // Process tile rows by superblock rows
        for (int tileRow = 0; tileRow < fh.TileRows; tileRow++)
        {
            int sbhStart = fh.TileRowStartSb[tileRow];
            int sbhEnd = Math.Min((int)fh.TileRowStartSb[tileRow + 1], ctx.SuperBlockRows);

            for (int sby = sbhStart; sby < sbhEnd; sby++)
            {
                int by = sby << sbShift;

                // Load temporal MVs for this SB row (dav1d: load_tmvs)
                if (fh.UseRefFrameMvs)
                {
                    int byEnd8 = (by + sbStep) >> 1;
                    Av1RefMvs.LoadTemporalMvs(ctx.RefMvs,
                        0, ctx.Bw >> 1, by >> 1, Math.Min(byEnd8, ctx.Bh >> 1));
                }

                void DecodeTileColumn(int tileCol, Av1TaskContext t)
                {
                    int tileIdx = tileRow * fh.TileCols + tileCol;
                    var ts = ctx.TileStates![tileIdx];

                    // Set tile boundaries in 4px units (first SB row of tile only)
                    if (sby == sbhStart)
                    {
                        ts.ColStart = fh.TileColStartSb[tileCol] << ctx.SbShift;
                        ts.ColEnd = Math.Min(fh.TileColStartSb[tileCol + 1] << ctx.SbShift, ctx.Bw);
                        ts.RowStart = fh.TileRowStartSb[tileRow] << ctx.SbShift;
                        ts.RowEnd = Math.Min(fh.TileRowStartSb[tileRow + 1] << ctx.SbShift, ctx.Bh);
                        ts.TileCol = tileCol;
                        ts.TileRow = tileRow;
                    }

                    // Decode all superblocks in this tile column for this row
                    DecodeTileSuperblockRow(t, ts, tileIdx, by, tileCol, tileRow);
                }
                if (parallelTiles)
                {
                    // Tile columns are independent within an SB row (their own entropy coder, contexts and intra edges);
                    // the per-column above / loop-filter state they touch is disjoint when every tile column starts on
                    // a 128-pixel boundary (checked above).
                    try
                    {
                        System.Threading.Tasks.Parallel.For(0, fh.TileCols,
                            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = MaxThreads },
                            tc =>
                            {
                                // Motion compensation reads its bit depth from thread-static state.
                                Av1MotionComp.McBitDepth = ctx.BitDepth;
                                DecodeTileColumn(tc, tileTaskCtx[tc]);
                            });
                    }
                    catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
                    }
                }
                else
                    for (int tileCol = 0; tileCol < fh.TileCols; tileCol++) DecodeTileColumn(tileCol, taskCtx);

                // Save temporal MVs for this SB row (dav1d: dav1d_refmvs_save_tmvs)
                if (interOrSwitch)
                {
                    var rf = ctx.RefMvs;
                    int byEnd8 = Math.Min((by + sbStep) >> 1, rf.Ih8);
                    Av1RefMvs.SaveTemporalMvs(
                        rf.Rp!, (by >> 1) * rf.RpStride, rf.RpStride,
                        taskCtx.Rt.R,
                        rf.MfmvSign,
                        Math.Min(ctx.Bw >> 1, rf.Iw8), byEnd8,
                        0, by >> 1);
                }

                // Back up pre-loopfilter pixels for intra prediction of the next SB row.
                // MUST run before ApplyInLoopFilters (which deblocks in place).
                BackupIpredEdge(sby, by, ssHor, ssVer, hasChroma);

                // Apply in-loop filters for this superblock row
                ApplyInLoopFilters(sby, ssHor, ssVer, hasChroma);
                // Save lfMask data for CDEF/LR (backup before next row overwrites)
                if (ctx.LfMasksRows != null && ctx.LfMasks != null)
                {
                    // Back up per SB128 row (by>>5), matching ApplyCdef's index. For Sb128=0 the two SB64 rows of
                    // an SB128 both map here; the later (bottom) row's copy carries the complete accumulated mask.
                    int sb128Row = by >> 5;
                    for (int col = 0; col < sb128w && col < ctx.LfMasks.Length; col++)
                    {
                        int idx = sb128Row * sb128w + col;
                        if (idx < ctx.LfMasksRows.Length)
                            ctx.LfMasksRows[idx].CopyFrom(ctx.LfMasks[col]);
                    }
                }
        AvDbg.W($"[TILE-INFO] Frame#{frameIdx} TileRows={fh.TileRows} TileCols={fh.TileCols} TileLog2Cols={fh.TileLog2Cols} TileLog2Rows={fh.TileLog2Rows} TileColStartSb[0]={fh.TileColStartSb[0]} [1]={fh.TileColStartSb[1]} [2]={fh.TileColStartSb[2]}");
        if (frameIdx >= 17 && frameIdx <= 20)
                    AvDbg.W($"[SB-ROW] Frame#{frameIdx} sby={sby}/{sbhEnd} done");
            }
        }

        // End-of-frame CDF snapshot for comparison with dav1d's (dev only: AV1_CDFDUMP=1 writes cdf_ours_f<N>.txt to
        // the working directory; never on by default).
        AvDbg.W($"[CDF-DUMP-CHECK] FrameOffset={fh.FrameOffset} TileStatesNull={ctx.TileStates == null} Len={ctx.TileStates?.Length ?? -1}");
        if (CdfDump && ctx.TileStates != null && ctx.TileStates.Length > 0)
        {
            try
            {
                string cdfPath = $"cdf_ours_f{fh.FrameOffset}.txt";
                using var sw = new StreamWriter(cdfPath);
                DumpCdfSnapshot(sw, ctx.TileStates[0].Cdf);
                AvDbg.W($"[CDF-DUMP] Wrote our end-of-frame CDFs to {cdfPath}");
            }
            catch (Exception ex) { AvDbg.W($"[CDF-DUMP] Error: {ex.Message}"); }
        }

        // DEBUG: check pixel at error location after all reconstruction + deblocking
        if (_dbgFilterDump == 0)
        {
            int errOff = 32 * ctx.CurrentStrides[0] + 104;
            var yBuf = ctx.CurrentPlanes[0]!;
            AvDbg.W($"[POST-RECON] pix@(32,104): {yBuf[errOff]:x2} {yBuf[errOff+1]:x2} {yBuf[errOff+2]:x2} {yBuf[errOff+3]:x2} {yBuf[errOff+4]:x2} {yBuf[errOff+5]:x2} {yBuf[errOff+6]:x2} {yBuf[errOff+7]:x2}");
        }

        // === Copy LPF lines (post-deblock, pre-CDEF boundary rows for LR) ===
        // Pre-filter full-plane checksum for debugging
        if (_dbgFilterDump == 0)
        {
            int yW = ctx.Bw * 4, yH = ctx.Bh * 4;
            for (int plane = 0; plane < 3; plane++)
            {
                var buf = ctx.CurrentPlanes[plane];
                if (buf == null) continue;
                int stride = ctx.CurrentStrides[plane];
                int pw = plane == 0 ? yW : yW >> 1;
                int ph = plane == 0 ? yH : yH >> 1;
                uint sum = 0;
                for (int r = 0; r < ph; r++)
                    for (int c = 0; c < pw; c++)
                        sum += buf[r * stride + c];
                string pname = plane == 0 ? "Y" : plane == 1 ? "U" : "V";
                AvDbg.W($"[PIX-DBG] pre-CDEF {pname} {pw}x{ph} sum={sum}");
                // First 8 pixels of first 4 rows
                var sb = new System.Text.StringBuilder($"[PIX-DBG] pre-CDEF {pname} rows:");
                for (int r = 0; r < Math.Min(4, ph); r++)
                {
                    sb.Append(" |");
                    for (int c = 0; c < Math.Min(8, pw); c++)
                        sb.Append($" {buf[r * stride + c]:x2}");
                }
                AvDbg.W(sb);
            }
        }

        if (ctx.RestorePlanes != 0)
        {
            int sbh = seqHdr.Sb128 ? (ctx.Bh + 31) >> 5 : (ctx.Bh + 15) >> 4;
            // The rolling LrLpfLine buffer is overwritten each SB row, but LR runs as a
            // later whole-frame pass. Snapshot each SB row's boundary rows so LR reads the
            // correct per-SB-row lpf (dav1d does copy_lpf(sby) then lr(sby) back to back).
            int numLines = seqHdr.Sb128 ? 24 : 12;
            ctx.LrLpfNumLines = numLines;
            for (int p = 0; p < 3; p++)
            {
                var line = ctx.LrLpfLine[p];
                if (line == null) { ctx.LrLpfSnap[p] = null; continue; }
                int snapSize = sbh * numLines * (p == 0 ? ctx.LrYStride : ctx.LrUvStride);
                if (ctx.LrLpfSnap[p] == null || ctx.LrLpfSnap[p]!.Length < snapSize)
                    ctx.LrLpfSnap[p] = new ushort[snapSize];
            }
            for (int sby = 0; sby < sbh; sby++)
            {
                CopyLpf(sby, ssHor, ssVer, hasChroma);
                for (int p = 0; p < 3; p++)
                {
                    var line = ctx.LrLpfLine[p];
                    var snap = ctx.LrLpfSnap[p];
                    if (line == null || snap == null) continue;
                    int stride = p == 0 ? ctx.LrYStride : ctx.LrUvStride;
                    Array.Copy(line, 0, snap, sby * numLines * stride, numLines * stride);
                }
            }
        }

        // === CDEF (Constrained Directional Enhancement Filter) ===
        // Applied once after all deblocking is complete
        AvDbg.W($"[CDEF-ENTRY] CdefBits={fh.CdefBits} LfMasksNull={ctx.LfMasks == null} Damping={fh.CdefDamping} y0={fh.CdefYStrength0}");
        // dav1d runs CDEF whenever the sequence enables it; blocks whose luma and chroma strengths are both 0 are
        // skipped inside (a frame with only a chroma strength still filters chroma).
        if ((fh.CdefNBits > 0 || fh.CdefYStrength0 != 0 || fh.GetCdefUvStrength(0) != 0)
            && System.Environment.GetEnvironmentVariable("AV1_NOCDEF") != "1")
        {
            ApplyCdef(ssHor, ssVer, hasChroma);

            // Debug: dump post-CDEF Y plane
            if (DumpPreDeblockY)
            {
                string dumpPath = @"F:\Code\MediaKernel\post_cdef_y.dump";
                using var fs = new System.IO.FileStream(dumpPath, System.IO.FileMode.Create);
                var yPlane = ctx.CurrentPlanes[0]!;
                int w = Math.Min(ctx.Width4 * 4, 64);
                int h = Math.Min(ctx.Height4 * 4, 64);
                for (int yy = 0; yy < h; yy++)
                {
                    byte[] row = new byte[w];
                    for (int c = 0; c < w; c++) row[c] = (byte)yPlane[yy * ctx.YStride + c];
                    fs.Write(row);
                }
                AvDbg.W($"[POST-CDEF] Dumped {w}x{h} Y plane to {dumpPath}");
            }
        }

        // === Super-resolution upscale (dav1d filter_sbrow_resize over the whole frame, after deblock + CDEF) ===
        if (ctx.SuperRes) UpscaleSuperRes(ssHor, ssVer, hasChroma);

        // === Loop Restoration ===
        if (ctx.RestorePlanes != 0 && System.Environment.GetEnvironmentVariable("AV1_NOLR") == "1") { AvDbg.W("[LR] skipped (probe)"); }
        else if (ctx.RestorePlanes != 0) {
            AvDbg.W($"[LR-INFO] RestorePlanes={ctx.RestorePlanes} LRtypes=({fh.GetLrType(0)},{fh.GetLrType(1)},{fh.GetLrType(2)}) unitSizes=({fh.LrUnitSizeY},{fh.LrUnitSizeUv})");
            _lrRestored = 0;
            _lrSkipped = 0;
            int sbh = seqHdr.Sb128 ? (ctx.Bh + 31) >> 5 : (ctx.Bh + 15) >> 4;
            for (int sby = 0; sby < sbh; sby++)
                ApplyLoopRestoration(sby, ssHor, ssVer, hasChroma);
            AvDbg.W($"[LR-DONE] restored={_lrRestored} skipped={_lrSkipped}");

            // Debug: dump post-LR Y plane
            if (DumpPreDeblockY)
            {
                string dumpPath = @"F:\Code\MediaKernel\post_lr_y.dump";
                using var fs = new System.IO.FileStream(dumpPath, System.IO.FileMode.Create);
                var yPlane = ctx.CurrentPlanes[0]!;
                int w = Math.Min(ctx.Width4 * 4, 64);
                int h = Math.Min(ctx.Height4 * 4, 64);
                for (int yy = 0; yy < h; yy++)
                {
                    byte[] row = new byte[w];
                    for (int c = 0; c < w; c++) row[c] = (byte)yPlane[yy * ctx.YStride + c];
                    fs.Write(row);
                }
                AvDbg.W($"[POST-LR] Dumped {w}x{h} Y plane to {dumpPath}");
            }
        }

        // CDF update: save the CDF from the designated update tile
        if (!fh.DisableCdfUpdate)
        {
            int updateTileIdx = fh.TileUpdate;
            if (updateTileIdx < fh.TileCols * fh.TileRows && ctx.TileStates != null)
            {
                // Save CDF for reference frame refresh
                ctx.FrameHeader = fh;
            }
        }
    }

    /// <summary>
    /// Parse tile sizes from tile group data and assign per-tile data ranges.
    /// Called once per frame before DecodeFrameMain.
    /// Mirrors dav1d dav1d_decode_frame_init_cdf (decode.c:3142).
    /// </summary>
    private void SetupTileData()
    {
        var fh = frameHdr;
        int tileRow = 0, tileCol = 0;

        for (int g = 0; g < tileGroupCount; g++)
        {
            var tg = tileGroups[g];
            int dataOff = tg.Offset;
            int remaining = tg.Length;

            for (int j = tg.StartTile; j <= tg.EndTile; j++)
            {
                int tileSz;
                if (j == tg.EndTile)
                {
                    // Last tile in group gets the remainder
                    tileSz = remaining;
                }
                else
                {
                    // Read LE tile_size (TileNBytes bytes, little-endian) + 1
                    int nBytes = fh.TileNBytes;
                    if (nBytes > remaining) break;
                    tileSz = 0;
                    for (int k = 0; k < nBytes; k++)
                        tileSz |= tg.Data[dataOff + k] << (k * 8);
                    tileSz++;
                    dataOff += nBytes;
                    remaining -= nBytes;
                    if (tileSz > remaining) tileSz = remaining;
                }

                var ts = ctx.TileStates![j];
                ts.TileData = tg.Data;
                ts.TileDataOffset = dataOff;
                ts.TileDataLength = tileSz;
                ts.MsacInitialized = false;

                dataOff += tileSz;
                remaining -= tileSz;

                tileCol++;
                if (tileCol == fh.TileCols)
                {
                    tileCol = 0;
                    tileRow++;
                }
            }
        }
    }

    private void DecodeTileSuperblockRow(Av1TaskContext t, Av1TileState ts, int tileIdx, int by,
        int tileCol, int tileRow)
    {
        var fh = frameHdr;
        var sh = seqHdr;
        bool isIntra = fh.IsIntra;
        var rootBl = sh.Sb128 ? Av1BlockLevel.Bl128x128 : Av1BlockLevel.Bl64x64;
        var edgeTree = sh.Sb128 ? Av1IntraEdgeTree.Tree128 : Av1IntraEdgeTree.Tree64;
        int sbStep = ctx.SbStep;
        int sbShift = ctx.SbShift;
        int sb128w = ctx.Sb128w;

        // Get tile data span for MSAC
        if (ts.TileData == null || ts.TileDataLength == 0)
            return;

        ReadOnlySpan<byte> tileSpan = ts.TileData.AsSpan(ts.TileDataOffset, ts.TileDataLength);

        // Create or restore MSAC

        // Create or restore MSAC
        Av1Msac msac;
        if (!ts.MsacInitialized)
        {
            bool doTrace = Av1Decoder.EnableMsacTrace;
            msac = new Av1Msac(tileSpan, fh.DisableCdfUpdate, doTrace);
            ts.MsacInitialized = true;
            AvDbg.W($"[MSAC-INIT] rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} dif_hi={(uint)(msac.DebugDif>>32):X8} cnt={msac.Cnt} len={tileSpan.Length}");
            if (fh.FrameOffset > 0 && tileSpan.Length > 0)
                AvDbg.W($"[MSAC-DATA] First bytes: {tileSpan[0]:x2} {tileSpan[1]:x2} {tileSpan[2]:x2} {tileSpan[3]:x2} {tileSpan[4]:x2} {tileSpan[5]:x2} {tileSpan[6]:x2} {tileSpan[7]:x2}");

            if (Av1Decoder.EnableDav1dTrace)
            {
                msac.EnableDav1dTrace();
                msac.SetDav1dTraceFrame(fh.FrameOffset);
            }
            ts.InitLrRef();
        }
        else
        {
            msac = new Av1Msac(tileSpan, ts.MsacState);
        }

        // Reset left context for this tile SB row
        t.TileState = ts;
        t.Left.Reset(isIntra);
        t.By = by;

        // Link tile refmvs to frame refmvs (dav1d: dav1d_refmvs_tile_sbrow_init)
        t.Rt.Rf = ctx.RefMvs;
        t.Rt.TileColStart = ts.ColStart;
        t.Rt.TileColEnd = ts.ColEnd;
        t.Rt.TileRowStart = ts.RowStart;
        t.Rt.TileRowEnd = ts.RowEnd;

        // Link tile R rows to frame R rows (dav1d_refmvs_tile_sbrow_init). The frame keeps sbsz + 3 rows that every SB
        // row reuses; for odd SB rows the three rows above are exchanged so the previous SB row's bottom rows become
        // this row's "above" candidates. Also required on key/intra frames that allow intra block copy, so intraBC DV
        // prediction can read the spatial MV grid.
        if (!isIntra || fh.AllowIntraBc)
            Av1RefMvs.TileSbRowInit(t.Rt, ctx.RefMvs, ts.ColStart, ts.ColEnd, ts.RowStart, ts.RowEnd, by >> sbShift);

        // Reset palette UV context (clear the "left" row)
        Array.Clear(t.PalSzUv, 0, t.PalSzUv.GetLength(0) * t.PalSzUv.GetLength(1));

        int colSb128Start = (fh.TileColStartSb[tileCol]) >> (sh.Sb128 ? 0 : 1);

        // Decode each superblock in this tile column for this SB row
        int aboveIdx = colSb128Start + tileRow * sb128w;
        AvDbg.W($"[TILE-SB] tileCol={tileCol} tileRow={tileRow} idx={tileIdx} ColStart={ts.ColStart} ColEnd={ts.ColEnd} RowStart={ts.RowStart} RowEnd={ts.RowEnd} by={by} sbStep={sbStep} UseRefFrameMvs={fh.UseRefFrameMvs}");
        for (t.Bx = ts.ColStart; t.Bx < ts.ColEnd; t.Bx += sbStep)
        {
            // Point to the correct above context
            if (aboveIdx < aboveCtx!.Length)
                t.Above = aboveCtx[aboveIdx];

            // Point to the loop filter mask for this SB128 column. For 64x64 superblocks (Sb128=0) up to four
            // SB64s share one SB128 mask, so reset the mask + CDEF indices only at the first SB64 of the SB128
            // (top-left). Resetting per-SB64 would wipe an earlier SB64's noskip/cdef data before CDEF reads it.
            int sb128Col = t.Bx >> 5;
            bool firstOfSb128 = (t.Bx & 16) == 0 && (t.By & 16) == 0;
            // cdef_idx sub-slot of this SB64 within its shared SB128 mask (0=TL,1=TR,2=BL,3=BR).
            int curCdefSlot = ((t.By & 16) >> 3) + ((t.Bx & 16) >> 4);
            if (firstOfSb128)
            {
                // Start of a fresh SB128 (also every SB for Sb128=1): clear all four cdef_idx slots.
                t.CurSbCdefIdx[0] = -1;
                t.CurSbCdefIdx[1] = -1;
                t.CurSbCdefIdx[2] = -1;
                t.CurSbCdefIdx[3] = -1;
            }
            else if (!sh.Sb128)
            {
                // Sb128=0: each SB64 is its own cdef unit and must re-read its own cdef_idx. The array is shared
                // across SB128 columns and reset only at the top-left SB64, so the other three slots persist stale
                // (and, for By&16==16 rows, were never reset) — clear this SB64's slot so its cdef_idx IS read.
                // Without this the per-block cdef_idx read (L(cdef_bits)) is skipped and the MSAC stream desyncs on
                // any stream that uses cdef_bits>0 (multiple CDEF strengths — what libaom emits for larger frames).
                t.CurSbCdefIdx[curCdefSlot] = -1;
            }

            if (ctx.LfMasks != null && sb128Col < ctx.LfMasks.Length)
            {
                t.LfMask = ctx.LfMasks[sb128Col];
                if (firstOfSb128)
                    t.LfMask.Reset();
            }

            // Read restoration info from MSAC before partition decode (dav1d order).
            // Do NOT save/restore MSAC — restoration bits are part of the same
            // MSAC stream as partition/data. Consuming them here affects CDF adaptation
            // just like dav1d does.
            
            // Read restoration unit info from MSAC (dav1d reads before partition)
        if (ctx.RestorePlanes != 0)  // Restore CopyLpf + LR
            {
                ReadRestorationInfoForSb(t, ref msac, ctx, fh, sh, by, sbStep);
            }

            // Decode the superblock partition tree + blocks
            int err;
            try
            {
                err = Av1Decode.DecodeSuperblock(t, ref msac, ctx, edgeTree, 0, rootBl);
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                // A malformed stream must fail the decode, not silently leave the rest of the frame blank.
                throw new InvalidDataException($"AV1 superblock decode failed at bx={t.Bx} by={t.By}: {ex.GetType().Name}: {ex.Message}", ex);
            }
            if (err != 0)
            {
                ts.MsacState = msac.Save();
                throw new InvalidDataException($"AV1 superblock decode failed at bx={t.Bx} by={t.By} (error {err}).");
            }

            // Store CDEF indices from this SB into the per-SB128 filter mask. For Sb128=1 the SB owns all four
            // units; for Sb128=0 store only this SB64's slot (the other slots hold other columns' stale values,
            // so storing all four would corrupt this column's mask).
            if (t.LfMask != null)
            {
                if (sh.Sb128)
                    for (int ci = 0; ci < 4; ci++) t.LfMask.SetCdefIdx(ci, t.CurSbCdefIdx[ci]);
                else
                    t.LfMask.SetCdefIdx(curCdefSlot, t.CurSbCdefIdx[curCdefSlot]);
            }

            // Advance above context (every 128px = every SB128, or every other SB64)
            if ((t.Bx & 16) != 0 || sh.Sb128)
                aboveIdx++;
        }

        // Back up the left tx_lpf contexts at this tile's right edge (dav1d decode_tile_sbrow): the loop filter uses
        // them to fix the deblock strength of the next tile column's left edge.
        {
            int stepRows = sh.Sb128 ? 32 : 16;
            int alignH = (ctx.Bh + 31) & ~31;
            Array.Copy(t.Left.TxLpfY, t.By & 16, ctx.TxLpfRightEdgeY, alignH * tileCol + t.By, stepRows);
            if (fh.PixelLayout != Av1PixelLayout.I400)
            {
                int ssV = fh.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
                Array.Copy(t.Left.TxLpfUv, (t.By & 16) >> ssV, ctx.TxLpfRightEdgeUv, (alignH >> ssV) * tileCol + (t.By >> ssV), stepRows >> ssV);
            }
        }

        // Save MSAC state for next SB row
        ts.MsacState = msac.Save();
    }

    /// <summary>
    /// Read restoration unit info from the MSAC bitstream for the current superblock.
    /// Called once per SB column, before partition decode, for each plane with restoration.
    /// Maps to dav1d decode.c:2674-2724.
    /// </summary>
    private void ReadRestorationInfoForSb(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx,
        Av1DecoderFrameHeader fh, Av1DecoderSequenceHeader sh, int by, int sbStep)
    {
        var ts = t.TileState!;
        var layout = ctx.PixelLayout;

        for (int p = 0; p < 3; p++)
        {
            if (((ctx.RestorePlanes >> p) & 1) == 0)
                continue;

            int ssVer = (p != 0 && layout == Av1PixelLayout.I420) ? 1 : 0;
            int ssHor = (p != 0 && layout != Av1PixelLayout.I444) ? 1 : 0;
            int unitSizeLog2 = p != 0 ? fh.LrUnitSizeUv : fh.LrUnitSizeY;
            int y = t.By * 4 >> ssVer;
            int h = (fh.Height + ssVer) >> ssVer;

            int unitSize = 1 << unitSizeLog2;
            int mask = unitSize - 1;
            if ((y & mask) != 0) continue;
            int halfUnit = unitSize >> 1;
            // Skip if at non-first row and remaining height < half unit
            if (y != 0 && y + halfUnit > h) continue;

            var frameType = fh.GetLrType(p);

            if (ctx.SuperRes)
            {
                // Units live in the upscaled picture: read those whose left edge maps into this superblock.
                int srW = (fh.SuperResUpscaledWidth + ssHor) >> ssHor;
                int nUnits = Math.Max(1, (srW + halfUnit) >> unitSizeLog2);
                int d = fh.SuperResScaleDenominator;
                int rnd = unitSize * 8 - 1, shift = unitSizeLog2 + 3;
                int x0 = ((4 * t.Bx * d >> ssHor) + rnd) >> shift;
                int x1 = ((4 * (t.Bx + sbStep) * d >> ssHor) + rnd) >> shift;
                for (int ux = x0; ux < Math.Min(x1, nUnits); ux++)
                {
                    int pxX = ux << (unitSizeLog2 + ssHor);
                    int srSbIdx = (t.By >> 5) * ctx.SrSb128W + (pxX >> 7);
                    int srUnitIdx = ((t.By & 16) >> 3) + ((pxX & 64) >> 6);
                    Av1Decode.ReadRestorationInfo(ref msac, ts, ref ctx.LrMasks![srSbIdx].Lr[p, srUnitIdx], p, frameType);
                }
                continue;
            }

            int x = 4 * t.Bx >> ssHor;
            if ((x & mask) != 0) continue;
            int w = (fh.CodedWidth + ssHor) >> ssHor;
            if (x != 0 && x + halfUnit > w) continue;

            int sbIdx = (t.By >> 5) * ctx.SrSb128W + (t.Bx >> 5);
            int unitIdx = ((t.By & 16) >> 3) + ((t.Bx & 16) >> 4);

            if (ctx.LrMasks != null && sbIdx < ctx.LrMasks.Length)
            {
                Av1Decode.ReadRestorationInfo(ref msac, ts,
                    ref ctx.LrMasks[sbIdx].Lr[p, unitIdx], p, frameType);
            }
        }
    }

    // Copy the last (pre-deblock) reconstructed pixel row of this superblock row into the
    // ipred-edge buffers, so the first block-row of the next SB row predicts from unfiltered
    // pixels. Mirrors dav1d's dav1d_backup_ipred_edge (src/recon_tmpl.c).
    private void BackupIpredEdge(int sby, int by, int ssHor, int ssVer, bool hasChroma)
    {
        if (ctx.IpredEdgeY.Length == 0) return;

        int sbStep = ctx.SbStep;
        int sb128w = ctx.Sb128w;
        int sbyOff = sb128w * 128 * sby;

        int hPix = ctx.Height4 * 4;
        int yStride = ctx.YStride;
        Span<ushort> yPlane = ctx.CurrentPlanes[0]!.AsSpan();

        // dav1d reads row ((by + sb_step) * 4 - 1); clamp to the last real row (the final,
        // partial SB row's edge is never consumed, so clamping is safe).
        int yRow = Math.Min((by + sbStep) * 4 - 1, hPix - 1);

        Span<ushort> edgeY = ctx.IpredEdgeY.AsSpan();

        // dav1d backs up per tile after that tile's SB row: only the tiles of the tile row containing sby (the other
        // tile rows' states may still hold a previous frame's layout).
        int tileRow = 0;
        while (tileRow + 1 < frameHdr.TileRows && frameHdr.TileRowStartSb[tileRow + 1] <= sby) tileRow++;
        for (int tileCol = 0; tileCol < frameHdr.TileCols; tileCol++)
        {
            var ts = ctx.TileStates![tileRow * frameHdr.TileCols + tileCol];
            int xOff = ts.ColStart;                 // 4px units
            int nPix = 4 * (ts.ColEnd - xOff);       // luma pixels
            if (nPix <= 0) continue;

            int src = yRow * yStride + xOff * 4;
            int dst = sbyOff + xOff * 4;
            yPlane.Slice(src, nPix).CopyTo(edgeY.Slice(dst, nPix));

            if (hasChroma)
            {
                int uvStride = ctx.UvStride;
                int uvRow = Math.Min(((by + sbStep) * 4 >> ssVer) - 1, (hPix >> ssVer) - 1);
                int uvXOff = (xOff * 4) >> ssHor;    // chroma pixels
                int uvN = nPix >> ssHor;
                int uvSrc = uvRow * uvStride + uvXOff;
                int uvDst = sbyOff + uvXOff;
                ctx.CurrentPlanes[1]!.AsSpan().Slice(uvSrc, uvN)
                    .CopyTo(ctx.IpredEdgeU.AsSpan().Slice(uvDst, uvN));
                ctx.CurrentPlanes[2]!.AsSpan().Slice(uvSrc, uvN)
                    .CopyTo(ctx.IpredEdgeV.AsSpan().Slice(uvDst, uvN));
            }
        }
    }

    private void ApplyInLoopFilters(int sby, int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;

        // === Deblocking Loop Filter ===
        AvDbg.W($"[LF-CHECK] sby={sby} LfLevelY0={fh.LfLevelY0} LfLevelY1={fh.LfLevelY1} LfLevelU={fh.LfLevelU} LfLevelV={fh.LfLevelV} LfMasksNull={ctx.LfMasks == null}");
        // Dump first few LfLevel values
        if (sby == 0)
        {
            for (int di = 0; di < 4; di++)
                AvDbg.W($"[LF-LEVEL] LfLevel[{di}] col0={ctx.LfLevel[di, 0]} col1={ctx.LfLevel[di, 1]} col2={ctx.LfLevel[di, 2]} col3={ctx.LfLevel[di, 3]}");
        }
        if ((fh.LfLevelY0 != 0 || fh.LfLevelY1 != 0) && ctx.LfMasks != null && Environment.GetEnvironmentVariable("AV1_NODEBLOCK") == null)
        {
            int sbSz = seqHdr.Sb128 ? 32 : 16;
            int yPixelRow = sby * sbSz * 4;
            int yOff = yPixelRow * ctx.YStride;
            int uvOff = (yPixelRow >> ssVer) * ctx.UvStride;

            Span<ushort> yPlane = ctx.CurrentPlanes[0]!.AsSpan();
            Span<ushort> uPlane = hasChroma ? ctx.CurrentPlanes[1]!.AsSpan() : default;
            Span<ushort> vPlane = hasChroma ? ctx.CurrentPlanes[2]!.AsSpan() : default;

            // Debug: dump pre-deblocking Y plane (first SB row of each frame)
            AvDbg.W($"[PRE-DEBLOCK-CHK] DumpPreDeblockY={DumpPreDeblockY} sby={sby} fhFrameOffset={fh.FrameOffset}");
            if (DumpPreDeblockY && sby == 0)
            {
                string dumpPath = @$"F:\Code\MediaKernel\pre_deblock_y_f{fh.FrameOffset}.dump";
                using var fs = new System.IO.FileStream(dumpPath, System.IO.FileMode.Create);
                int w = ctx.Width4 * 4;
                int h = Math.Min(ctx.Height4 * 4, 64);
                for (int yy = 0; yy < h; yy++)
                {
                    byte[] row = new byte[w];
                    for (int c = 0; c < w; c++) row[c] = (byte)yPlane[yy * ctx.YStride + c];
                    fs.Write(row);
                }
                AvDbg.W($"[PRE-DEBLOCK] Dumped F{fh.FrameOffset} {w}x{h} Y plane to {dumpPath}");
            }

            Av1LoopFilter.LoopFilterSbRowCols(ctx, yPlane, uPlane, vPlane,
                yOff, uvOff, uvOff, ctx.LfMasks, sby, ctx.StartOfTileRow.Length > sby ? ctx.StartOfTileRow[sby] : 0);
            Av1LoopFilter.LoopFilterSbRowRows(ctx, yPlane, uPlane, vPlane,
                yOff, uvOff, uvOff, ctx.LfMasks, sby);

            // Debug: dump post-deblocking full YUV
            if (DumpPreDeblockY && sby == ctx.SuperBlockRows - 1)
            {
                string dumpPath = @"F:\Code\MediaKernel\post_deblock_full.yuv";
                using var fs = new System.IO.FileStream(dumpPath, System.IO.FileMode.Create);
                int w = ctx.Width4 * 4;
                int h = Math.Min(ctx.Height4 * 4, 64);
                for (int yy = 0; yy < h; yy++) { byte[] row = new byte[w]; for (int c = 0; c < w; c++) row[c] = (byte)yPlane[yy * ctx.YStride + c]; fs.Write(row); }
                AvDbg.W($"[POST-DEBLOCK] Dumped Y {w}x{h} to {dumpPath}");
            }
        }

        // CDEF and loop restoration are applied as whole-frame passes after deblocking
    }

    /// <summary>
    /// Apply CDEF to the entire frame. Uses a pre-CDEF frame copy for correct border data.
    /// Simplified single-threaded implementation (dav1d uses SB-row-level with backup lines).
    /// </summary>
    private void ApplyCdef(int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;
        int damping = fh.CdefDamping + (ctx.BitDepth - 8); // dav1d: cdef.damping + bitdepth_min_8
        int yStride = ctx.YStride;
        int uvStride = ctx.UvStride;
        int w4 = ctx.Bw;   // CDEF walks the MI grid (dav1d f->bw/f->bh), unlike the loop filter's w4/h4
        int h4 = ctx.Bh;
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int sbsz = 16; // 64x64 in 4x4 units

        Span<ushort> yPlane = ctx.CurrentPlanes[0]!.AsSpan();
        Span<ushort> uPlane = hasChroma ? ctx.CurrentPlanes[1]!.AsSpan() : default;
        Span<ushort> vPlane = hasChroma ? ctx.CurrentPlanes[2]!.AsSpan() : default;

        // Pre-CDEF copy for border reference (CDEF filter needs pre-filter neighbors)
        ushort[] yBak = ArrayPool<ushort>.Shared.Rent(yPlane.Length);
        yPlane.CopyTo(yBak);
        ushort[]? uBak = null, vBak = null;
        if (hasChroma)
        {
            uBak = ArrayPool<ushort>.Shared.Rent(uPlane.Length);
            uPlane.CopyTo(uBak);
            vBak = ArrayPool<ushort>.Shared.Rent(vPlane.Length);
            vPlane.CopyTo(vBak);
        }


        ushort[] yArr = ctx.CurrentPlanes[0]!;
        ushort[]? uArr = hasChroma ? ctx.CurrentPlanes[1] : null, vArr = hasChroma ? ctx.CurrentPlanes[2] : null;

        try
        {
            int sb64w = (w4 + 15) >> 4; // number of SB64 columns

            // Every 8x8 block reads only the pre-CDEF copies and writes its own pixels, so the 8-pixel rows are
            // filtered in parallel (identical output for any thread count; serial when tracing decisions).
            void CdefRow(int by)
            {
                Span<ushort> yPlane = yArr;
                Span<ushort> uPlane = uArr, vPlane = vArr;
                // Scratch buffers for left border (2 bytes per row, up to 8 rows)
                Span<ushort> leftBuf = stackalloc ushort[16]; // 8 rows * 2 cols
                ReadOnlySpan<byte> uvDirI422 = [7, 0, 2, 4, 5, 6, 6, 6];
                var edges = Av1Cdef.EdgeFlags.Bottom | (by > 0 ? Av1Cdef.EdgeFlags.Top : 0);
                if (by + 2 >= h4) edges &= ~Av1Cdef.EdgeFlags.Bottom;

                for (int sbx = 0; sbx < sb64w; sbx++)
                {
                    int sb128x = sbx >> 1;
                    int sb128y = by >> 5; // SB128 row index (by is in 4x4 units, 32 per SB128 row)
                    int sb128W = ctx.Sb128w;
                    if (sb128x >= ctx.LfMasks!.Length) continue;
                    // Use backup array indexed by SB128 row+col
                    int maskIdx = sb128y * sb128W + sb128x;
                    Av1FilterMask lfMask;
                    if (ctx.LfMasksRows != null && maskIdx < ctx.LfMasksRows.Length)
                        lfMask = ctx.LfMasksRows[maskIdx];
                    else
                        lfMask = ctx.LfMasks[sb128x];

                    int sb64_idx = ((by & sbsz) >> 3) + (sbx & 1);
                    int cdefIdx = lfMask.GetCdefIdx(sb64_idx);
                    bool dbgCdef = DumpCdefDecisions && frameHdr.FrameOffset == 1;
                    if (cdefIdx == -1)
                    {
                        if (dbgCdef) CdefDecisionWriter?.WriteLine($"by={by} sbx={sbx} sb64_idx={sb64_idx} SKIP cdefIdx=-1");
                        continue;
                    }

                    int yLvl = fh.GetCdefYStrength(cdefIdx);
                    int uvLvl = fh.GetCdefUvStrength(cdefIdx);
                    if (yLvl == 0 && uvLvl == 0)
                    {
                        if (dbgCdef) CdefDecisionWriter?.WriteLine($"by={by} sbx={sbx} sb64_idx={sb64_idx} cdefIdx={cdefIdx} SKIP yLvl=0 uvLvl=0");
                        continue;
                    }

                    // dav1d cdef_apply: strengths are scaled by bitdepth_min_8 HERE, and
                    // adjust_strength (luma only) then operates on the shifted value —
                    // scaling after adjust would differ because adjust_strength is nonlinear.
                    int bdMin8 = ctx.BitDepth - 8;
                    int yPriLvl = (yLvl >> 2) << bdMin8;
                    int ySecLvl = yLvl & 3;
                    ySecLvl += ySecLvl == 3 ? 1 : 0;
                    ySecLvl <<= bdMin8;

                    int uvPriLvl = (uvLvl >> 2) << bdMin8;
                    int uvSecLvl = uvLvl & 3;
                    uvSecLvl += uvSecLvl == 3 ? 1 : 0;
                    uvSecLvl <<= bdMin8;

                    // Noskip mask for this 8x8 row pair
                    // by is the global row; mask row within this SB128 = (by & 31) >> 1
                    int byIdx = (by & 31) >> 1;
                    uint noskipMask = byIdx < 16 ?
                        (uint)lfMask.NoskipMask[byIdx, 1] << 16 | lfMask.NoskipMask[byIdx, 0] : 0;

                    for (int bx = sbx * sbsz; bx < Math.Min((sbx + 1) * sbsz, w4); bx += 2)
                    {
                        var blockEdges = edges;
                        if (bx > 0) blockEdges |= Av1Cdef.EdgeFlags.Left;
                        if (bx + 2 < w4) blockEdges |= Av1Cdef.EdgeFlags.Right;

                        // Check noskip: if block was all skip, don't apply CDEF
                        uint bxMask = 3u << (bx & 30);
                        if ((noskipMask & bxMask) == 0)
                        {
                            if (dbgCdef) CdefDecisionWriter?.WriteLine($"by={by} bx={bx} SKIP noskip=0 (noskipMask={noskipMask:X8} bxMask={bxMask:X8} yLvl={yLvl} uvLvl={uvLvl})");
                            continue;
                        }
                        if (dbgCdef) CdefDecisionWriter?.WriteLine($"by={by} bx={bx} FILTER yPri={yPriLvl} ySec={ySecLvl} uvPri={uvPriLvl} uvSec={uvSecLvl} (noskipMask={noskipMask:X8})");

                        int px = bx * 4; // pixel x
                        int py = by * 4; // pixel y

                        // Find direction (on luma 8x8 block)
                        int dir = 0;
                        uint variance = 0;
                        if (yPriLvl != 0 || uvPriLvl != 0)
                        {
                            int yOff = py * yStride + px;
                            dir = Av1Cdef.FindDirection(yBak, yOff, yStride, out variance, ctx.BitDepth);
                        }
                        if (dbgCdef) CdefDecisionWriter?.WriteLine($"by={by} bx={bx} DIR={dir} var={variance} yPri={yPriLvl} ySec={ySecLvl} damping={damping}");

                        int frameHeight = fh.Height;

                        // === Luma ===
                        // dav1d cdef_apply_tmpl.c:237-246 — the computed direction is only
                        // used when there is a primary strength; with pri==0 the filter is
                        // called with dir=0.
                        if (yPriLvl != 0 || ySecLvl != 0)
                        {
                            int adjYPriLvl = yPriLvl != 0 ? Av1Cdef.AdjustStrength(yPriLvl, variance) : 0;
                            if (adjYPriLvl != 0 || ySecLvl != 0)
                            {
                                int yOff = py * yStride + px;
                                PrepareCdefLeft(leftBuf, yBak, yOff, yStride, 8, blockEdges);
                                int topOff = py >= 2 ? (py - 2) * yStride + px : yOff;
                                // dav1d reads the two rows below whenever CDEF_HAVE_BOTTOM is set: they are reconstructed
                                // (blocks cover the MI grid) even past the displayed height.
                                int botOff = (blockEdges & Av1Cdef.EdgeFlags.Bottom) != 0 ? (py + 8) * yStride + px : yOff + 7 * yStride;
                                int lumaDir = yPriLvl != 0 ? dir : 0;

                                Av1Cdef.FilterBlock(
                                    yPlane, yOff, yStride,
                                    leftBuf, 0, 2,
                                    yBak, topOff, yBak, botOff,
                                    adjYPriLvl, ySecLvl, lumaDir, damping,
                                    8, 8, blockEdges, ctx.BitDepth);
                            }
                        }

                        // === Chroma ===
                        if (hasChroma && (uvPriLvl != 0 || uvSecLvl != 0))
                        {
                            if (by == 0 && bx <= 4)
                                AvDbg.W($"[CDEF-CHROMA] by={by} bx={bx} uvPri={uvPriLvl} uvSec={uvSecLvl} chW={8>>ssHor} chH={8>>ssVer} uvStride={uvStride}");
                            int uvDir = uvPriLvl != 0 ?
                                (fh.PixelLayout == Av1PixelLayout.I422 ? uvDirI422[dir] : dir) : 0;
                            int chW = 8 >> ssHor;
                            int chH = 8 >> ssVer;
                            int cpx = px >> ssHor;
                            int cpy = py >> ssVer;
                            int chromaHeight = frameHeight >> ssVer;

                            for (int pl = 0; pl < 2; pl++)
                            {
                                var uvSrc = pl == 0 ? uBak! : vBak!;
                                var uvDst = pl == 0 ? uPlane : vPlane;
                                int uvOff = cpy * uvStride + cpx;
                                PrepareCdefLeft(leftBuf, uvSrc, uvOff, uvStride, chH, blockEdges);
                                int topOff = cpy >= 2 ? (cpy - 2) * uvStride + cpx : uvOff;
                                int botOff = (blockEdges & Av1Cdef.EdgeFlags.Bottom) != 0 ?
                                    (cpy + chH) * uvStride + cpx : uvOff + (chH - 1) * uvStride;

                                Av1Cdef.FilterBlock(
                                    uvDst, uvOff, uvStride,
                                    leftBuf, 0, 2,
                                    uvSrc, topOff, uvSrc, botOff,
                                    uvPriLvl, uvSecLvl, uvDir, damping - 1,
                                    chW, chH, blockEdges, ctx.BitDepth);
                            }
                        }
                    }
                }
            }
            int rows = (h4 + 1) >> 1;
            if (MaxThreads > 1 && !(DumpCdefDecisions && frameHdr.FrameOffset == 1))
                System.Threading.Tasks.Parallel.For(0, rows, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = MaxThreads },
                    r => CdefRow(r * 2));
            else
                for (int r = 0; r < rows; r++) CdefRow(r * 2);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(yBak);
            if (uBak != null) ArrayPool<ushort>.Shared.Return(uBak);
            if (vBak != null) ArrayPool<ushort>.Shared.Return(vBak);
        }
    }

    /// <summary>
    /// Prepare 2-column left border buffer for CDEF from the pre-filter copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrepareCdefLeft(Span<ushort> leftBuf, ushort[] src, int srcOffset, int stride,
        int h, Av1Cdef.EdgeFlags edges)
    {
        if ((edges & Av1Cdef.EdgeFlags.Left) != 0)
        {
            int off = srcOffset - 2;
            for (int y = 0; y < h; y++)
            {
                leftBuf[y * 2 + 0] = src[off];
                leftBuf[y * 2 + 1] = src[off + 1];
                off += stride;
            }
        }
    }

    // ======================================================================
    // Loop Restoration — CopyLpf + Apply
    // ======================================================================

    /// <summary>
    /// Save post-deblock, pre-CDEF boundary rows for loop restoration.
    /// Ported from dav1d backup_lpf / dav1d_copy_lpf (lf_apply_tmpl.c:40-101, 104-166).
    /// Single-threaded variant: num_lines=12, no tile-threading offset.
    /// </summary>
    private void CopyLpf(int sby, int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int offset = 8 * (sby > 0 ? 1 : 0);
        int yStride = ctx.YStride;
        int uvStride = ctx.UvStride;
        int restorePlanes = ctx.RestorePlanes;

        if ((restorePlanes & 1) != 0)
        {
            int h = fh.Height;
            int w = ctx.Bw << 2;
            int rowH = Math.Min((sby + 1) << (6 + sb128), h - 1);
            int yStripe = (sby << (6 + sb128)) - offset;

            // dav1d copy_lpf src = plane advanced to SB-row top, minus offset = y_stripe.
            BackupLpf(ctx.LrLpfLine[0]!, ctx.LrYStride,
                ctx.CurrentPlanes[0]!, yStripe * yStride, yStride,
                ssVer: 0, sb128, yStripe, rowH, w, h,
                ctx.SuperRes ? fh.SuperResUpscaledWidth : 0, ctx.ResizeStep[0], ctx.ResizeStart[0], ctx.BitDepth);
        }

        if (hasChroma && (restorePlanes & 6) != 0)
        {
            int h = (fh.Height + ssVer) >> ssVer;
            int w = ctx.Bw << (2 - ssHor);
            int rowH = Math.Min((sby + 1) << ((6 - ssVer) + sb128), h - 1);
            int offsetUv = offset >> ssVer;
            int yStripe = (sby << ((6 - ssVer) + sb128)) - offsetUv;

            if ((restorePlanes & 2) != 0)
                BackupLpf(ctx.LrLpfLine[1]!, ctx.LrUvStride,
                    ctx.CurrentPlanes[1]!, yStripe * uvStride, uvStride,
                    ssVer, sb128, yStripe, rowH, w, h,
                    ctx.SuperRes ? (fh.SuperResUpscaledWidth + ssHor) >> ssHor : 0, ctx.ResizeStep[ssHor], ctx.ResizeStart[ssHor], ctx.BitDepth);
            if ((restorePlanes & 4) != 0)
                BackupLpf(ctx.LrLpfLine[2]!, ctx.LrUvStride,
                    ctx.CurrentPlanes[2]!, yStripe * uvStride, uvStride,
                    ssVer, sb128, yStripe, rowH, w, h,
                    ctx.SuperRes ? (fh.SuperResUpscaledWidth + ssHor) >> ssHor : 0, ctx.ResizeStep[ssHor], ctx.ResizeStart[ssHor], ctx.BitDepth);
        }
    }

    /// <summary>
    /// Copy specific rows from source plane to LR LPF buffer at stripe boundaries.
    /// dav1d: backup_lpf (lf_apply_tmpl.c:41-101), single-threaded (super-res rows are upscaled below).
    /// </summary>
    private static void BackupLpf(ushort[] dst, int dstStride,
        ushort[] src, int srcOffset, int srcStride,
        int ssVer, int sb128, int row, int rowH, int srcW, int h,
        int resizeDstW = 0, int resizeStep = 0, int resizeStart = 0, int bitDepth = 8)
    {
        int dstOff = 0;

        // Single-threaded: shift previous bottom→top and advance. NOTE: these are ushort[]
        // arrays and all offsets/counts are in ELEMENTS, so use Array.Copy (element-based) —
        // Buffer.BlockCopy takes byte units and silently corrupts the layout (leaving the
        // lpf_bottom rows at 6*stride zero, which desyncs the loop-restoration bottom edge).
        if (row > 0)
        {
            int top = (4 << sb128) * dstStride;
            Array.Copy(dst, top, dst, 0, dstStride);
            Array.Copy(dst, top + dstStride, dst, dstStride, dstStride);
            Array.Copy(dst, top + dstStride * 2, dst, dstStride * 2, dstStride);
            Array.Copy(dst, top + dstStride * 3, dst, dstStride * 3, dstStride);
        }
        dstOff = 4 * dstStride;

        // Loop-restoration stripes are 64 luma rows whatever the superblock size (dav1d: 64 << (cdef_backup & sb128)
        // with cdef_backup = 0 here); the first stripe is shorter by 8 luma rows (fewer for chroma). A 128x128
        // superblock row therefore holds two stripes.
        int stripeH = (64 - 8 * (row == 0 ? 1 : 0)) >> ssVer;
        // Advance src to stripe_h - 2 rows in (the last 2 rows of the stripe)
        int srcOff = srcOffset + (stripeH - 2) * srcStride;

        if (resizeDstW > 0)
        {
            // Super-resolution: the saved rows are upscaled like the picture they border (dav1d mc.resize path; a
            // 3-line stripe end repeats its last upscaled row).
            while (row + stripeH <= rowH)
            {
                int nLines = 4 - (row + stripeH + 1 == h ? 1 : 0);
                Av1MotionComp.Resize(dst.AsSpan(dstOff), dstStride, src.AsSpan(srcOff), srcStride,
                    resizeDstW, nLines, srcW, resizeStep, resizeStart, bitDepth);
                row += stripeH;
                stripeH = 64 >> ssVer;
                srcOff += stripeH * srcStride;
                dstOff += nLines * dstStride;
                if (nLines == 3)
                {
                    Array.Copy(dst, dstOff - dstStride, dst, dstOff, resizeDstW);
                    dstOff += dstStride;
                }
            }
            return;
        }

        while (row + stripeH <= rowH)
        {
            int nLines = 4 - (row + stripeH + 1 == h ? 1 : 0);
            for (int i = 0; i < 4; i++)
            {
                if (i == nLines)
                {
                    // Duplicate previous row
                    Array.Copy(dst, dstOff - dstStride, dst, dstOff, srcW);
                }
                else
                {
                    Array.Copy(src, srcOff, dst, dstOff, srcW);
                    srcOff += srcStride;
                }
                dstOff += dstStride;
            }
            row += stripeH;
            stripeH = 64 >> ssVer;
            srcOff += (stripeH - 4) * srcStride;
        }
    }

    /// <summary>
    /// Apply loop restoration for one SB row.
    /// Ported from dav1d dav1d_lr_sbrow / lr_sbrow / lr_stripe (lr_apply_tmpl.c).
    /// </summary>
    // Upscales the deblocked + CDEF-filtered coded-width planes to the super-resolution width and makes them the
    // frame's planes, so loop restoration, the output and the references see the upscaled picture (dav1d sr_cur).
    private void UpscaleSuperRes(int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;
        int srW = fh.SuperResUpscaledWidth;
        for (int p = 0; p < (hasChroma ? 3 : 1); p++)
        {
            int sh = p == 0 ? 0 : ssHor, sv = p == 0 ? 0 : ssVer;
            int dstStride = p == 0 ? ctx.LrYStride : ctx.LrUvStride;
            int rows = (fh.Height + sv) >> sv;
            int allocRows = ((((fh.Height + 63) & ~63) + (1 << sv) - 1) >> sv);
            var dst = ArrayPool<ushort>.Shared.Rent(dstStride * allocRows);
            Av1MotionComp.Resize(dst, dstStride, ctx.CurrentPlanes[p], ctx.CurrentStrides[p],
                (srW + sh) >> sh, rows, (4 * ctx.Bw + sh) >> sh, ctx.ResizeStep[sh], ctx.ResizeStart[sh], ctx.BitDepth);
            ArrayPool<ushort>.Shared.Return(ctx.CurrentPlanes[p]!);
            ctx.CurrentPlanes[p] = dst;
            ctx.CurrentStrides[p] = dstStride;
        }
        ctx.YStride = ctx.LrYStride;
        if (hasChroma) ctx.UvStride = ctx.LrUvStride;
    }

    private void ApplyLoopRestoration(int sby, int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int restorePlanes = ctx.RestorePlanes;
        int sbStep = seqHdr.Sb128 ? 32 : 16;
        int notLast = sby + 1 < ((ctx.Bh + sbStep - 1) / sbStep) ? 1 : 0;
        int offsetY = 8 * (sby > 0 ? 1 : 0);
        int numLines = ctx.LrLpfNumLines;

        if ((restorePlanes & 1) != 0)
        {
            int h = fh.Height;
            int w = fh.SuperResUpscaledWidth;
            int nextRowY = (sby + 1) << (6 + sb128);
            int rowH = Math.Min(nextRowY - 8 * notLast, h);
            int yStripe = (sby << (6 + sb128)) - offsetY;

            // Plane pointer must be at the stripe's first row (dav1d: dst - offset_y,
            // where dst is already advanced to the SB-row top → net y_stripe*stride).
            LrSbRow(ctx.CurrentPlanes[0]!, yStripe * ctx.YStride, ctx.YStride,
                ctx.LrLpfSnap[0]!, sby * numLines * ctx.YStride, yStripe, w, h, rowH, 0, 0, sby);
        }

        if (hasChroma && (restorePlanes & 6) != 0)
        {
            int h = (fh.Height + ssVer) >> ssVer;
            int w = (fh.SuperResUpscaledWidth + ssHor) >> ssHor;
            int nextRowY = (sby + 1) << ((6 - ssVer) + sb128);
            int rowH = Math.Min(nextRowY - ((8 >> ssVer) * notLast), h);
            int offsetUv = offsetY >> ssVer;
            int yStripe = (sby << ((6 - ssVer) + sb128)) - offsetUv;

            if ((restorePlanes & 2) != 0)
                LrSbRow(ctx.CurrentPlanes[1]!, yStripe * ctx.UvStride, ctx.UvStride,
                    ctx.LrLpfSnap[1]!, sby * numLines * ctx.UvStride, yStripe, w, h, rowH, 1, ssHor, sby);

            if ((restorePlanes & 4) != 0)
                LrSbRow(ctx.CurrentPlanes[2]!, yStripe * ctx.UvStride, ctx.UvStride,
                    ctx.LrLpfSnap[2]!, sby * numLines * ctx.UvStride, yStripe, w, h, rowH, 2, ssHor, sby);
        }
    }

    /// <summary>
    /// Apply LR to one plane for one SB row, iterating left→right over restoration units.
    /// Ported from dav1d lr_sbrow (lr_apply_tmpl.c:107-166).
    /// </summary>
    private void LrSbRow(ushort[] plane, int pOff, int stride,
        ushort[] lpf, int lpfBase, int y, int w, int h, int rowH,
        int planeIdx, int ssHor, int sby)
    {
        var fh = frameHdr;
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int ssVer = (planeIdx != 0 && ctx.PixelLayout == Av1PixelLayout.I420) ? 1 : 0;

        int unitSizeLog2 = planeIdx != 0 ? fh.LrUnitSizeUv : fh.LrUnitSizeY;
        int unitSize = 1 << unitSizeLog2;
        int halfUnitSize = unitSize >> 1;
        int maxUnitSize = unitSize + halfUnitSize;

        int rowY = y + ((8 >> ssVer) * (y > 0 ? 1 : 0));
        int shiftHor = 7 - ssHor;

        // Pre-LR left border backup: alternating pair
        int borderH = rowH - y;
        var preLrBorder = new ushort[2][];
        preLrBorder[0] = new ushort[borderH * 4];
        preLrBorder[1] = new ushort[borderH * 4];

        // Find the restoration unit indices
        int alignedUnitPos = rowY & ~(unitSize - 1);
        if (alignedUnitPos != 0 && alignedUnitPos + halfUnitSize > h)
            alignedUnitPos -= unitSize;
        alignedUnitPos <<= ssVer;
        int sbIdx = (alignedUnitPos >> 7) * ctx.SrSb128W;
        int unitIdx = ((alignedUnitPos >> 6) & 1) << 1;

        // Track current/next LR unit via sb/unit indices (alternating in lr[2])
        int curSbIdx = sbIdx, curUnitIdx = unitIdx;
        bool restore = ctx.LrMasks![curSbIdx].Lr[planeIdx, curUnitIdx].Type != Av1RestorationType.None;
        if (restore) System.Threading.Interlocked.Increment(ref _lrRestored);
        else System.Threading.Interlocked.Increment(ref _lrSkipped);
        int x = 0;
        int bit = 0;

        var edges = (y > 0 ? Av1LoopRestoration.LrEdgeFlags.Top : 0)
                  | Av1LoopRestoration.LrEdgeFlags.Right;

        int pBase = pOff;

        while (x + maxUnitSize <= w)
        {
            int nextX = x + unitSize;
            int nextUIdx = unitIdx + ((nextX >> (shiftHor - 1)) & 1);
            int nextSbIdx = sbIdx + (nextX >> shiftHor);
            if (nextSbIdx >= ctx.LrMasks.Length) break;

            bool restoreNext = ctx.LrMasks[nextSbIdx].Lr[planeIdx, nextUIdx].Type != Av1RestorationType.None;

            if (restoreNext)
                Backup4xU(preLrBorder[bit], plane, pBase + unitSize - 4, stride, borderH);

            if (restore)
                LrStripe(plane, pBase, stride, preLrBorder[1 - bit], lpf, lpfBase,
                    x, y, planeIdx, unitSize, rowH,
                    ref ctx.LrMasks[curSbIdx].Lr[planeIdx, curUnitIdx], edges, sby, ssVer);

            x = nextX;
            pBase += unitSize;
            edges |= Av1LoopRestoration.LrEdgeFlags.Left;
            bit ^= 1;

            curSbIdx = nextSbIdx;
            curUnitIdx = nextUIdx;
            restore = restoreNext;
        }

        // Last partial unit
        if (restore)
        {
            edges &= ~Av1LoopRestoration.LrEdgeFlags.Right;
            int unitW = w - x;
            LrStripe(plane, pBase, stride, preLrBorder[1 - bit], lpf, lpfBase,
                x, y, planeIdx, unitW, rowH,
                ref ctx.LrMasks[curSbIdx].Lr[planeIdx, curUnitIdx], edges, sby, ssVer);
        }
    }

    /// <summary>
    /// Backup 4 left-border columns for LR.
    /// dav1d: backup4xU (lr_apply_tmpl.c:100-105).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Backup4xU(ushort[] dst, ushort[] src, int srcOff, int stride, int rows)
    {
        for (int i = 0; i < rows; i++)
        {
            dst[i * 4 + 0] = src[srcOff + 0];
            dst[i * 4 + 1] = src[srcOff + 1];
            dst[i * 4 + 2] = src[srcOff + 2];
            dst[i * 4 + 3] = src[srcOff + 3];
            srcOff += stride;
        }
    }

    /// <summary>
    /// Apply LR filter to one restoration unit across vertical stripes.
    /// Ported from dav1d lr_stripe (lr_apply_tmpl.c:36-98).
    /// </summary>
    private void LrStripe(ushort[] p, int pOff, int stride,
        ushort[] left, ushort[] lpf, int lpfBase,
        int x, int y, int plane, int unitW, int rowH,
        ref Av1RestorationUnit lr, Av1LoopRestoration.LrEdgeFlags edges,
        int sby, int ssVer)
    {
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int sbh = (ctx.Bh + (seqHdr.Sb128 ? 31 : 15)) >> (seqHdr.Sb128 ? 5 : 4);
        // Bit depth for the Wiener/SGR kernels (round bits, clip limits, box-sum downscale).
        Av1LoopRestoration.WienerBitDepth = ctx.BitDepth;

        // lpf offset: base into this SB row's snapshot, then add x (single-threaded).
        int lpfOff = lpfBase + x;

        // First stripe is shorter by 8 luma rows (→ fewer for chroma)
        int stripeH = Math.Min((64 - 8 * (y == 0 ? 1 : 0)) >> ssVer, rowH - y);

        // Build filter params and pick the filter function
        Span<ushort> pSpan = p.AsSpan();
        ReadOnlySpan<ushort> lpfSpan = lpf.AsSpan();
        ReadOnlySpan<ushort> leftSpan = left.AsSpan();

        int leftOff = 0;

        while (y + stripeH <= rowH)
        {
            // Update HAVE_BOTTOM: true unless this is the last stripe in the frame
            var eBot = ((sby + 1 != sbh || y + stripeH != rowH)
                ? Av1LoopRestoration.LrEdgeFlags.Bottom : 0);
            var curEdges = (edges & ~Av1LoopRestoration.LrEdgeFlags.Bottom) | eBot;

            if (lr.Type == Av1RestorationType.Wiener)
            {
                // Build 7-tap Wiener filter coefficients
                Span<short> filterH = stackalloc short[8];
                Span<short> filterV = stackalloc short[8];

                filterH[0] = filterH[6] = lr.FilterH0;
                filterH[1] = filterH[5] = lr.FilterH1;
                filterH[2] = filterH[4] = lr.FilterH2;
                // The +128 DC term is added separately as src[x]*128 inside the horizontal
                // Wiener pass ONLY for 8-bit, so the centre tap excludes it at 8-bit and
                // folds it in for high bit depth (dav1d lr_apply_tmpl.c: filter[0][3] =
                // -(h0+h1+h2)*2, then += 128 for BITDEPTH != 8).
                filterH[3] = (short)(-(lr.FilterH0 + lr.FilterH1 + lr.FilterH2) * 2
                                     + (ctx.BitDepth == 8 ? 0 : 128));
                filterH[7] = 0;

                filterV[0] = filterV[6] = lr.FilterV0;
                filterV[1] = filterV[5] = lr.FilterV1;
                filterV[2] = filterV[4] = lr.FilterV2;
                filterV[3] = (short)(128 - (lr.FilterV0 + lr.FilterV1 + lr.FilterV2) * 2);
                filterV[7] = 0;

                Av1LoopRestoration.Wiener(pSpan, pOff, stride,
                    leftSpan, leftOff, 4,
                    lpfSpan, lpfOff,
                    unitW, stripeH, filterH, filterV, curEdges);
            }
            else
            {
                // SGR (SelfGuided) — dav1d: params.sgr.w1 = 128 - (ew0 + ew1)
                int sgrIdx = (int)lr.Type - (int)Av1RestorationType.SelfGuided;
                int s0 = Av1LoopRestoration.SgrParams[sgrIdx, 0];
                int s1 = Av1LoopRestoration.SgrParams[sgrIdx, 1];
                int ew0 = lr.SgrWeight0;
                int ew1 = lr.SgrWeight1;
                // dav1d (lr_apply): sgr.w0 = weights[0]; sgr.w1 = 128 - (weights[0]+weights[1]).
                // sgr_5x5 uses sgr.w0, sgr_3x3 uses sgr.w1, sgr_mix uses (w0, w1).
                int w = 128 - ew0 - ew1;

                if (s0 != 0 && s1 != 0)
                {
                    Av1LoopRestoration.SgrMix(pSpan, pOff, stride,
                        leftSpan, leftOff, 4,
                        lpfSpan, lpfOff,
                        unitW, stripeH, s0, s1, ew0, w, curEdges);
                }
                else if (s0 != 0)
                {
                    Av1LoopRestoration.Sgr5x5(pSpan, pOff, stride,
                        leftSpan, leftOff, 4,
                        lpfSpan, lpfOff,
                        unitW, stripeH, s0, ew0, curEdges);
                }
                else
                {
                    Av1LoopRestoration.Sgr3x3(pSpan, pOff, stride,
                        leftSpan, leftOff, 4,
                        lpfSpan, lpfOff,
                        unitW, stripeH, s1, w, curEdges);
                }
            }

            leftOff += stripeH * 4;
            y += stripeH;
            pOff += stripeH * stride;
            edges |= Av1LoopRestoration.LrEdgeFlags.Top;
            stripeH = Math.Min(64 >> ssVer, rowH - y);
            if (stripeH == 0) break;
            lpfOff += 4 * stride;
        }
    }

    // ======================================================================
    // Reference Frame Management
    // ======================================================================

    // Debug (env AV1_DUMPALL = file): appends every decoded frame's native planes (u16 LE, Y then U then V), hidden
    // frames included, in decode order - the counterpart of dav1d --outputinvisible 1.
    private void DumpDecodedFrame()
    {
        string? path = Environment.GetEnvironmentVariable("AV1_DUMPALL");
        if (string.IsNullOrEmpty(path)) return;
        int w = frameHdr.SuperResUpscaledWidth, h = frameHdr.Height;
        int ssHor = ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0, ssVer = ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        File.AppendAllText(path + ".info", $"D {w} {h} {ctx.BitDepth} {(ctx.CurrentPlanes[1] == null ? 1 : 0)} {ssHor} {ssVer} show{(frameHdr.ShowFrame ? 1 : 0)}\n");
        using var fs = new FileStream(path, FileMode.Append);
        using var bw = new BinaryWriter(fs);
        for (int p = 0; p < 3; p++)
        {
            var pl = ctx.CurrentPlanes[p];
            if (pl == null) continue;
            int pw = p == 0 ? w : (w + ssHor) >> ssHor, ph = p == 0 ? h : (h + ssVer) >> ssVer;
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++) bw.Write(pl[y * ctx.CurrentStrides[p] + x]);
        }
    }

    private void UpdateReferenceFrames()
    {
        // Reference planes are never written in place, so the refreshed slots share one copy (as dav1d refcounts).
        ushort[]?[]? sharedPlanes = null;
        var fh = frameHdr;
        byte refreshFlags = fh.RefreshFrameFlags;

        for (int i = 0; i < 8; i++)
        {
            if ((refreshFlags & (1 << i)) != 0)
            {
                var refFrame = ctx.RefFrames[i];
                refFrame.Width = fh.SuperResUpscaledWidth;
                refFrame.CodedWidth = fh.CodedWidth;
                refFrame.Height = fh.Height;
                refFrame.RenderWidth = fh.RenderWidth;
                refFrame.RenderHeight = fh.RenderHeight;
                refFrame.FrameType = fh.FrameType;
                refFrame.OrderHint = fh.FrameOffset;
                refFrame.FilmGrain = fh.FilmGrain;
                refFrame.FilmGrainPresent = fh.FilmGrainPresent;
                refFrame.SegmentMap = ctx.CurSegMap;
                refFrame.SegmentationData = fh.SegmentationData;
                refFrame.LfModeRefDeltas = fh.LfModeRefDeltas;
                Array.Copy(fh.Gmv, refFrame.Gmv, refFrame.Gmv.Length);
                refFrame.Valid = true;

                // Copy current frame planes to reference (one copy shared by every refreshed slot)
                CopyFrameToReference(refFrame, ref sharedPlanes);

                // The CDFs later frames load from this slot (dav1d refs[].cdf = out_cdf): the frame-end update of the
                // context_update_tile_id tile when refresh_context is set, else the frame's input CDFs.
                refFrame.CdfSnapshot ??= new Av1CdfContext();
                int updateTile = fh.TileUpdate;
                if (fh.RefreshContext && ctx.TileStates != null && updateTile < fh.TileCols * fh.TileRows)
                    refFrame.CdfSnapshot.SaveFrameEnd(ctx.InCdf!, ctx.TileStates[updateTile].Cdf, fh.IsInterOrSwitch);
                else
                    refFrame.CdfSnapshot.CopyFrom(ctx.InCdf!);

                // The frame's motion field (shared, never written again) and its reference order hints. An intrabc
                // frame's block vectors are not a motion field: dav1d leaves such a slot's refmvs empty.
                refFrame.TemporalMvs = fh.AllowIntraBc ? null : ctx.CurrentRp;
                ctx.CurrentRefPoc.CopyTo(refFrame.RefPoc, 0);
            }
        }
    }

    private void CopyFrameToReference(Av1ReferenceFrame refFrame, ref ushort[]?[]? shared)
    {
        if (shared != null)
        {
            for (int plane = 0; plane < 3; plane++) { refFrame.Planes[plane] = shared[plane]; refFrame.Strides[plane] = ctx.CurrentStrides[plane]; }
            return;
        }
        shared = new ushort[3][];
        for (int plane = 0; plane < 3; plane++)
        {
            var src = ctx.CurrentPlanes[plane];
            // A monochrome frame has no chroma: drop the slot's old planes (a new sequence may switch layouts).
            if (src == null) { refFrame.Planes[plane] = null; continue; }

            int stride = ctx.CurrentStrides[plane];
            int height = plane == 0 ? frameHdr.Height :
                (ctx.PixelLayout == Av1PixelLayout.I420 ? (frameHdr.Height + 1) >> 1 : frameHdr.Height);
            int width = plane == 0 ? frameHdr.SuperResUpscaledWidth :
                (ctx.PixelLayout == Av1PixelLayout.I444 ? frameHdr.SuperResUpscaledWidth :
                 (frameHdr.SuperResUpscaledWidth + 1) >> 1);

            int bufSize = stride * height;
            // Always a fresh array: the slot's previous one may be shared with other slots.
            refFrame.Planes[plane] = shared[plane] = new ushort[bufSize];

            refFrame.Strides[plane] = stride;

            for (int y = 0; y < height; y++)
            {
                src.AsSpan(y * stride, width).CopyTo(refFrame.Planes[plane].AsSpan(y * stride));
            }
        }
    }

    // ======================================================================
    // Show Existing Frame
    // ======================================================================

    private DecodedVideoFrame? HandleShowExistingFrame(long presentationTimeTicks)
    {
        var fh = frameHdr;
        int refIdx = fh.ExistingFrameIdx;
        var refFrame = ctx.RefFrames[refIdx];

        if (!refFrame.Valid || refFrame.Planes[0] == null)
            return null;
        if (Environment.GetEnvironmentVariable("AV1_DUMPALL") is { Length: > 0 } dumpPath)
            File.AppendAllText(dumpPath + ".info", $"E {refFrame.Width} {refFrame.Height}\n");

        // Showing an existing key frame refreshes all reference slots. The type is the stored frame's (dav1d
        // refs[existing_frame_idx].frame_type): a show_existing_frame header carries none.
        if (refFrame.FrameType == Av1FrameType.Key)
        {
            for (int i = 0; i < 8; i++)
            {
                if (i == refIdx) continue;
                var dst = ctx.RefFrames[i];
                dst.Width = refFrame.Width;
                dst.CodedWidth = refFrame.CodedWidth;
                dst.Height = refFrame.Height;
                dst.RenderWidth = refFrame.RenderWidth;
                dst.RenderHeight = refFrame.RenderHeight;
                dst.FrameType = refFrame.FrameType;
                dst.OrderHint = refFrame.OrderHint;
                dst.FilmGrain = refFrame.FilmGrain;
                dst.FilmGrainPresent = refFrame.FilmGrainPresent;
                dst.SegmentMap = refFrame.SegmentMap;
                dst.SegmentationData = refFrame.SegmentationData;
                dst.LfModeRefDeltas = refFrame.LfModeRefDeltas;
                Array.Copy(refFrame.Gmv, dst.Gmv, dst.Gmv.Length);
                dst.TemporalMvs = null;   // dav1d drops the other slots' refmvs
                dst.Valid = true;

                // Shared (reference planes are read-only); a monochrome source leaves no stale chroma behind.
                for (int p = 0; p < 3; p++)
                {
                    dst.Planes[p] = refFrame.Planes[p];
                    dst.Strides[p] = refFrame.Strides[p];
                }

                if (refFrame.CdfSnapshot != null)
                {
                    if (dst.CdfSnapshot == null) dst.CdfSnapshot = new Av1CdfContext();
                    dst.CdfSnapshot.CopyFrom(refFrame.CdfSnapshot);
                }
            }
        }

        // Output the reference frame directly
        return ExtractReferenceFrame(refFrame, presentationTimeTicks);
    }

    /// <summary>Whether film grain signalled in the stream is synthesized onto output frames (default on, as dav1d and
    /// libavif do). Off returns the grain-free reconstruction.</summary>
    public bool ApplyFilmGrain { get; set; } = true;

    /// <summary>Film grain parameters of the last parsed frame header (null when none).</summary>
    internal Av1FilmGrainData? LastFilmGrain => frameHdr.FilmGrainPresent ? frameHdr.FilmGrain : null;

    /// <summary>The last parsed frame header (diagnostics).</summary>
    internal Av1DecoderFrameHeader CurrentFrameHeader => frameHdr;

    /// <summary>The active sequence header's intra tools (enable_filter_intra, enable_intra_edge_filter).</summary>
    internal (bool FilterIntra, bool EdgeFilter) SequenceIntraTools => (seqHdr.FilterIntra, seqHdr.IntraEdgeFilter);

    /// <summary>A deep copy of reference slot <paramref name="slot"/> (encoder trial decodes restore it afterwards).</summary>
    internal Av1ReferenceFrame SnapshotSlot(int slot) => ctx.RefFrames[slot].DeepCopy();

    internal void RestoreSlot(int slot, Av1ReferenceFrame snapshot) => ctx.RefFrames[slot] = snapshot;

    /// <summary>Reference slot <paramref name="slot"/> (post-filter reconstruction, before film grain) as a tightly
    /// packed picture — the image sequence encoder predicts its inter frames from exactly what the decoder holds.</summary>
    internal Av1InterEncoder.Picture ReferencePicture(int slot, bool mono, int ssX, int ssY)
    {
        var r = ctx.RefFrames[slot];
        if (!r.Valid || r.Planes[0] == null) throw new InvalidOperationException($"Reference slot {slot} is empty.");
        int w = r.Width, h = r.Height, cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY;
        ushort[] Plane(int p, int pw, int ph)
        {
            var o = new ushort[pw * ph];
            for (int y = 0; y < ph; y++) r.Planes[p]!.AsSpan(y * r.Strides[p], pw).CopyTo(o.AsSpan(y * pw));
            return o;
        }
        return new Av1InterEncoder.Picture
        {
            Y = Plane(0, w, h), U = mono ? null : Plane(1, cw, ch), V = mono ? null : Plane(2, cw, ch), Width = w, Height = h,
        };
    }

    private (ushort[], ushort[]?, ushort[]?) WithFilmGrain(in Av1FilmGrainData fg, int w, int h, int ssHor, int ssVer,
        ushort[] y, int strideY, ushort[]? u, ushort[]? v, int strideUv)
    {
        var gy = (ushort[])y.Clone();
        var gu = u == null ? null : (ushort[])u.Clone();
        var gv = v == null ? null : (ushort[])v.Clone();
        Av1FilmGrain.Apply(fg, ctx.BitDepth, ssHor, ssVer, seqHdr.MatrixCoefficients == Av1MatrixCoefficients.Identity,
            w, h, y, strideY, u, v, strideUv, gy, gu, gv);
        return (gy, gu, gv);
    }

    private DecodedVideoFrame? ExtractReferenceFrame(Av1ReferenceFrame refFrame, long presentationTimeTicks)
    {
        isReady = true;
        return BuildOutputFrame(refFrame.Width, refFrame.Height, refFrame.Planes[0], refFrame.Planes[1], refFrame.Planes[2],
            refFrame.Strides, refFrame.FilmGrainPresent, refFrame.FilmGrain, presentationTimeTicks);
    }

    // ======================================================================
    // Output Frame Extraction
    // ======================================================================

    private DecodedVideoFrame? ExtractOutputFrame(long presentationTimeTicks)
    {
        int w = frameHdr.SuperResUpscaledWidth;
        int h = frameHdr.Height;
        if (w == 0 || h == 0) {
            AvDbg.W("[EXTRACT] w or h is 0");
            return null;
        }
        return BuildOutputFrame(w, h, ctx.CurrentPlanes[0], ctx.CurrentPlanes[1], ctx.CurrentPlanes[2], ctx.CurrentStrides,
            frameHdr.FilmGrainPresent, frameHdr.FilmGrain, presentationTimeTicks);
    }

    private DecodedVideoFrame? BuildOutputFrame(int w, int h, ushort[]? yPlane, ushort[]? uPlane, ushort[]? vPlane,
        int[] strides, bool filmGrain, in Av1FilmGrainData fg, long presentationTimeTicks)
    {

        // Chroma subsampling from the actual pixel layout (was hardcoded to 4:2:0, corrupting 4:4:4 / 4:2:2 output).
        int ssHor = ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int ssVer = ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ySize = w * h;
        int uvW = (w + ssHor) >> ssHor;
        int uvH = (h + ssVer) >> ssVer;
        int uvSize = uvW * uvH;
        int totalSize = ySize + uvSize * 2;

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int yOff = 0, uOff = ySize, vOff = ySize + uvSize;

        if (yPlane == null) {
            AvDbg.W("[EXTRACT] yPlane is NULL - skipping frame");
            ArrayPool<byte>.Shared.Return(outputBuffer);
            return null;
        }

        // Film grain is synthesized on the output copy only; the reference planes stay grain-free (spec 7.18.3).
        if (filmGrain && ApplyFilmGrain)
            (yPlane, uPlane, vPlane) = WithFilmGrain(fg, w, h, ssHor, ssVer, yPlane, strides[0], uPlane, vPlane, strides[1]);

        // High-bit-depth samples are downshifted to 8-bit for the byte output buffer.
        int bdShift = ctx.BitDepth - 8;
        int bdRound = bdShift > 0 ? (1 << (bdShift - 1)) : 0; // round on high-bit-depth->8 downshift (matches reference)

        if (yPlane != null)
        {
            for (int y = 0; y < h; y++)
            {
                int so = y * strides[0], doff = yOff + y * w;
                for (int x = 0; x < w; x++) outputBuffer[doff + x] = (byte)Math.Min(255, (yPlane[so + x] + bdRound) >> bdShift);
            }
        }

        if (uPlane != null)
        {
            for (int y = 0; y < uvH; y++)
            {
                int so = y * strides[1], doff = uOff + y * uvW;
                for (int x = 0; x < uvW; x++) outputBuffer[doff + x] = (byte)Math.Min(255, (uPlane[so + x] + bdRound) >> bdShift);
            }
        }

        if (vPlane != null)
        {
            for (int y = 0; y < uvH; y++)
            {
                int so = y * strides[2], doff = vOff + y * uvW;
                for (int x = 0; x < uvW; x++) outputBuffer[doff + x] = (byte)Math.Min(255, (vPlane[so + x] + bdRound) >> bdShift);
            }
        }
        if (uPlane == null) outputBuffer.AsSpan(uOff, 2 * uvSize).Fill(128);   // monochrome: neutral chroma, not pool leftovers

        {
            string? dumpPath = System.Environment.GetEnvironmentVariable("AV1_DUMPYUV");
            if (!string.IsNullOrEmpty(dumpPath))
            {
                using var fs = new System.IO.FileStream(dumpPath, System.IO.FileMode.Create);
                fs.Write(outputBuffer, yOff, ySize);
                fs.Write(outputBuffer, uOff, uvSize);
                fs.Write(outputBuffer, vOff, uvSize);
            }
            // Raw native-bit-depth planes (little-endian u16), for byte-exact 10/12-bit checks.
            string? dump10 = System.Environment.GetEnvironmentVariable("AV1_DUMP10");
            if (!string.IsNullOrEmpty(dump10))
            {
                using var fs = new System.IO.FileStream(dump10, System.IO.FileMode.Create);
                using var bw = new System.IO.BinaryWriter(fs);
                void WritePlane(ushort[]? pl, int stride, int pw, int ph)
                {
                    if (pl == null) return;
                    for (int yy = 0; yy < ph; yy++)
                        for (int xx = 0; xx < pw; xx++) bw.Write(pl[yy * stride + xx]);
                }
                WritePlane(yPlane, strides[0], w, h);
                WritePlane(uPlane, strides[1], uvW, uvH);
                WritePlane(vPlane, strides[2], uvW, uvH);
            }
        }

        var outFmt = ctx.PixelLayout switch
        {
            Av1PixelLayout.I444 => PixelFormat.Yuv444P,
            Av1PixelLayout.I422 => PixelFormat.Yuv422P,
            _ => PixelFormat.Yuv420P,
        };

        // High-bit-depth streams additionally expose their native-precision samples (tightly packed, same
        // strides as the 8-bit planes) so callers can keep full 10/12-bit precision.
        ReadOnlyMemory<ushort> y16 = default, u16 = default, v16 = default;
        if (ctx.BitDepth > 8)
        {
            var native = new ushort[totalSize];
            void CopyPlane(ushort[]? pl, int stride, int off, int pw, int ph)
            {
                if (pl == null) return;
                for (int yy = 0; yy < ph; yy++) Array.Copy(pl, yy * stride, native, off + yy * pw, pw);
            }
            CopyPlane(yPlane, strides[0], yOff, w, h);
            CopyPlane(uPlane, strides[1], uOff, uvW, uvH);
            CopyPlane(vPlane, strides[2], vOff, uvW, uvH);
            y16 = new ReadOnlyMemory<ushort>(native, yOff, ySize);
            if (uPlane != null) u16 = new ReadOnlyMemory<ushort>(native, uOff, uvSize);
            if (vPlane != null) v16 = new ReadOnlyMemory<ushort>(native, vOff, uvSize);
        }

        return new DecodedVideoFrame(
            w, h, outFmt, presentationTimeTicks,
            outputBuffer,
            yOff, w,
            uOff, uvW,
            vOff, uvW)
        {
            BitDepth = ctx.BitDepth,
            YPlane16 = y16,
            UPlane16 = u16,
            VPlane16 = v16,
        };
    }

    /// <summary>
    /// Dump coefficient CDF tables in the same format as dav1d's cdf_snapshot_f*.txt.
    /// Used for comparison: diff our end-of-frame-0 CDFs against dav1d's.
    /// </summary>
    private static void DumpCdfSnapshot(StreamWriter sw, Av1CdfContext cdf)
    {
        var coef = cdf.Coef;
        var mode = cdf.Mode;
        var mv = cdf.Mv;
        // CoefSkip: 5*13=65 entries, 2 u16s each
        for (int tx = 0; tx < 5; tx++)
            for (int s = 0; s < 13; s++)
            {
                sw.WriteLine($"skip[{tx * 13 + s}].0={coef.CoefSkip[tx * 13 + s][0]}");
                sw.WriteLine($"skip[{tx * 13 + s}].1={coef.CoefSkip[tx * 13 + s][1]}");
            }
        // EobBin arrays (2*2 flat inner dims)
        for (int ij = 0; ij < 4; ij++)
            for (int k = 0; k < 8; k++)
            {
                sw.WriteLine($"eob16[{ij / 2}][{ij % 2}][{k}]={coef.EobBin16[ij][k]}");
                sw.WriteLine($"eob32[{ij / 2}][{ij % 2}][{k}]={coef.EobBin32[ij][k]}");
                sw.WriteLine($"eob64[{ij / 2}][{ij % 2}][{k}]={coef.EobBin64[ij][k]}");
                sw.WriteLine($"eob128[{ij / 2}][{ij % 2}][{k}]={coef.EobBin128[ij][k]}");
            }
        for (int ij = 0; ij < 4; ij++)
            for (int k = 0; k < 16; k++)
                sw.WriteLine($"eob256[{ij / 2}][{ij % 2}][{k}]={coef.EobBin256[ij][k]}");
        for (int c = 0; c < 2; c++)
            for (int k = 0; k < 16; k++)
            {
                sw.WriteLine($"eob512[{c}][{k}]={coef.EobBin512[c][k]}");
                sw.WriteLine($"eob1024[{c}][{k}]={coef.EobBin1024[c][k]}");
            }
        // Partition: 5*4=20 flat contexts, 16 values each
        for (int bl = 0; bl < 5; bl++)
            for (int ctx = 0; ctx < 4; ctx++)
                for (int v = 0; v < 16; v++)
                    sw.WriteLine($"mpart[{bl}][{ctx}][{v}]={mode.Partition[bl * 4 + ctx][v]}");
        // Skip: 3*2=6
        for (int ctx = 0; ctx < 3; ctx++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mskip[{ctx}][{v}]={mode.Skip[ctx][v]}");
        // Intra: 4*2=8
        for (int ctx = 0; ctx < 4; ctx++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mintra[{ctx}][{v}]={mode.Intra[ctx][v]}");
        // NewmvMode: 6*2
        for (int ctx = 0; ctx < 6; ctx++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mnewmv[{ctx}][{v}]={mode.NewmvMode[ctx][v]}");
        // GlobalmvMode: 2*2
        for (int ctx = 0; ctx < 2; ctx++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mgmv[{ctx}][{v}]={mode.GlobalmvMode[ctx][v]}");
        // RefmvMode: 6*2
        for (int ctx = 0; ctx < 6; ctx++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mrefmv[{ctx}][{v}]={mode.RefmvMode[ctx][v]}");
        // MotionMode: 22*4 → dump as 2 per entry (prob + counter)
        for (int bs = 0; bs < 22; bs++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mmot[{bs}][{v}]={mode.MotionMode[bs][v]}");
        // Obmc: 22*2
        for (int bs = 0; bs < 22; bs++)
            for (int v = 0; v < 2; v++)
                sw.WriteLine($"mobmc[{bs}][{v}]={mode.Obmc[bs][v]}");
        // MV joint: 8 values → dav1d uses 4
        for (int v = 0; v < 4; v++)
            sw.WriteLine($"mvjoint[{v}]={mv.Joint[v]}");
        // MV comp classes (Comp0, Comp1)
        for (int v = 0; v < 11; v++)
        {
            sw.WriteLine($"mvcomp0_classes[{v}]={mv.Comp0.Classes[v]}");
            sw.WriteLine($"mvcomp1_classes[{v}]={mv.Comp1.Classes[v]}");
        }
        // MV comp sign
        sw.WriteLine($"mvcomp0_sign={mv.Comp0.Sign[0]}");
        sw.WriteLine($"mvcomp1_sign={mv.Comp1.Sign[0]}");
        // MSAC end-state
        sw.WriteLine($"msac_end_rng=0x8000");
        sw.WriteLine($"msac_end_dif=0");
        sw.WriteLine($"msac_end_cnt=0");
    }
}
