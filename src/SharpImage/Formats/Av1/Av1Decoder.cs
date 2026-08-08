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
    private readonly TileGroup[] tileGroups = new TileGroup[256];
    private int tileGroupCount;
    private int tilesCollected;

    // Persistent above-context arrays (one per sb128 column per tile row)
    private Av1BlockContextManaged[]? aboveCtx;

    // Persistent task context (reused across SB rows to avoid alloc)
    private readonly Av1TaskContext taskCtx = new();

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
        if (data.Length == 0)
            return null;

        // Parse all OBUs in this temporal unit
        int offset = 0;
        tileGroupCount = 0;
        tilesCollected = 0;
        bool haveFrameHeader = false;

        while (offset < data.Length)
        {
            int obuStart = offset;

            // Parse OBU header (§5.3.1)
            var gb = new Av1GetBits(data.Slice(offset));
            int forbiddenBit = (int)gb.GetBit();
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
            int obuSize;
            if (hasLengthField)
            {
                obuSize = (int)gb.GetUleb128();
                headerBytes = gb.BytePosition;
            }
            else
            {
                // Without length field, OBU extends to end of temporal unit
                obuSize = data.Length - offset - headerBytes;
            }

            if (offset + headerBytes + obuSize > data.Length)
                break; // truncated

            var obuPayload = data.Slice(offset + headerBytes, obuSize);
            offset += headerBytes + obuSize;

            switch (obuType)
            {
                case 1: // OBU_SEQUENCE_HEADER
                    ParseSequenceHeaderObu(obuPayload);
                    break;

                case 2: // OBU_TEMPORAL_DELIMITER
                    // Nothing to do — we already reset per temporal unit
                    break;

                case 3: // OBU_FRAME_HEADER
                case 6: // OBU_FRAME (frame header + tile group combined)
                    if (!hasSequenceHeader)
                        break;
                    haveFrameHeader = ParseFrameHeaderObu(obuPayload, temporalId, spatialId, out int fhBytesConsumed, isObuFrame: obuType == 6);
                    if (haveFrameHeader && obuType == 6)
                    {
                        // OBU_FRAME: tile data starts immediately after frame header
                        // No tile_group_obu() header — direct tile_size fields follow
                        int tileDataOffset = fhBytesConsumed;
                        if (tileDataOffset < obuSize)
                        {
                            ParseTileGroupObu(obuPayload.Slice(tileDataOffset), offset + headerBytes + tileDataOffset, isObuFrame: true);
                        }
                    }
                    break;

                case 4: // OBU_TILE_GROUP
                    if (!hasSequenceHeader || !haveFrameHeader)
                        break;
                    ParseTileGroupObu(obuPayload, offset - obuSize);
                    break;

                case 5: // OBU_METADATA — skip for now
                case 7: // OBU_REDUNDANT_FRAME_HEADER
                case 15: // OBU_PADDING
                default:
                    break;
            }
        }

        // Handle show_existing_frame
        if (haveFrameHeader && frameHdr.ShowExistingFrame)
        {
            return HandleShowExistingFrame(presentationTimeTicks);
        }

        // Check if we have all tiles to decode
        int totalTiles = frameHdr.TileCols * frameHdr.TileRows;
        if (haveFrameHeader && tilesCollected >= totalTiles)
        {
            AvDbg.W($"[FRAME-DECODE] FrameOffset={frameHdr.FrameOffset} ShowFrame={frameHdr.ShowFrame} ShowExistingFrame={frameHdr.ShowExistingFrame} IsIntra={frameHdr.IsIntra} IsInterOrSwitch={frameHdr.IsInterOrSwitch}");
            try { DecodeFrame(data); }
            catch (Exception ex) { AvDbg.W($"[DECODE-FRAME-ERROR] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return null; }

            // Update reference frame slots
            try { UpdateReferenceFrames(); }
            catch (Exception ex) { AvDbg.W($"[UPDATE-REF-ERROR] {ex.GetType().Name}: {ex.Message}"); return null; }

            // Extract visible frame
            AvDbg.W($"[DECODE-CHECK] Pre-showframe check: ShowFrame={frameHdr.ShowFrame} ShowExistingFrame={frameHdr.ShowExistingFrame}");
            if (frameHdr.ShowFrame)
            {
                AvDbg.W($"[DECODE-RETURN] About to extract frame {frameHdr.FrameOffset}");
                isReady = true;
                var frame = ExtractOutputFrame(presentationTimeTicks);
                AvDbg.W($"[DECODE-RETURN] Extracted frame {frameHdr.FrameOffset}: {(frame == null ? "NULL" : "OK")}");
                return frame;
            }
        }

        return null;
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
        frameHdr.TemporalId = (byte)temporalId;
        frameHdr.SpatialId = (byte)spatialId;

        var result = Av1ObuParser.ParseFrameHeader(frameHdr, seqHdr, ctx.RefFrames, payload, out bytesConsumed, isObuFrame);
        if (result != Av1ObuParser.ParseResult.Ok)
            return false;

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
        int totalTiles = frameHdr.TileCols * frameHdr.TileRows;
        int tileStart = 0;
        int tileEnd = totalTiles - 1;
        int headerBits = 0;

        if (!isObuFrame && totalTiles > 1)
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

        int bw = (fh.CodedWidth + 3) >> 2;   // width in 4px units
        int bh = (fh.Height + 3) >> 2;       // height in 4px units
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
        ctx.W4 = bw;
        ctx.H4 = bh;
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
            int lrMaskCount = sb128h2 * sb128w;
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
        ctx.SrSb128W = sb128w;

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
        int uvStride = hasChroma ? (((width >> ssHor) + 63) & ~63) : 0;
        int uvHeight = hasChroma ? ((height + (1 << ssVer) - 1) >> ssVer) : 0;

        int ySize = yStride * height;
        int uvSize = uvStride * uvHeight;

        // Return old buffers
        for (int i = 0; i < 3; i++)
        {
            if (ctx.CurrentPlanes[i] != null)
            {
                ArrayPool<byte>.Shared.Return(ctx.CurrentPlanes[i]);
                ctx.CurrentPlanes[i] = null;
            }
        }

        ctx.CurrentPlanes[0] = ArrayPool<byte>.Shared.Rent(ySize);
        ctx.CurrentStrides[0] = yStride;
        ctx.YStride = yStride;

        if (hasChroma)
        {
            ctx.CurrentPlanes[1] = ArrayPool<byte>.Shared.Rent(uvSize);
            ctx.CurrentPlanes[2] = ArrayPool<byte>.Shared.Rent(uvSize);
            ctx.CurrentStrides[1] = uvStride;
            ctx.CurrentStrides[2] = uvStride;
            ctx.UvStride = uvStride;
        }

        // Allocate LR LPF line buffers (post-deblock, pre-CDEF boundary rows)
        // dav1d: decode.c:2973 — for single-threaded: num_lines = 12
        if (ctx.RestorePlanes != 0)
        {
            const int numLines = 12;
            int yLpfSize = yStride * numLines;
            int uvLpfSize = uvStride * numLines;
            if (ctx.LrLpfLine[0] == null || ctx.LrLpfLine[0].Length < yLpfSize)
                ctx.LrLpfLine[0] = new byte[yLpfSize];
            if (hasChroma)
            {
                if (ctx.LrLpfLine[1] == null || ctx.LrLpfLine[1].Length < uvLpfSize)
                    ctx.LrLpfLine[1] = new byte[uvLpfSize];
                if (ctx.LrLpfLine[2] == null || ctx.LrLpfLine[2].Length < uvLpfSize)
                    ctx.LrLpfLine[2] = new byte[uvLpfSize];
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

            for (int seg = 0; seg < 8; seg++)
            {
                int qIdx = fh.QuantBaseQIdx;
                if (fh.SegmentationEnabled)
                {
                    int segDelta = fh.SegmentationData.Segments[seg].DeltaQ;
                    qIdx = Math.Clamp(qIdx + segDelta, 0, 255);
                }
                fh.SegmentationQIdx[seg] = (byte)qIdx;

                // Compute dequant values for each plane
                // dav1d: dq[seg][0][0] = dc, dq[seg][0][1] = ac for luma
                int yDcDelta = fh.QuantYDcDelta;
                int uDcDelta = fh.QuantUDcDelta;
                int uAcDelta = fh.QuantUAcDelta;
                int vDcDelta = fh.QuantVDcDelta;
                int vAcDelta = fh.QuantVAcDelta;

                // Y plane
                ts.Dq[seg, 0, 0] = (ushort)GetDcDequant(qIdx + yDcDelta, ctx.BitDepth);
                ts.Dq[seg, 0, 1] = (ushort)GetAcDequant(qIdx, ctx.BitDepth);
                // U plane
                ts.Dq[seg, 1, 0] = (ushort)GetDcDequant(qIdx + uDcDelta, ctx.BitDepth);
                ts.Dq[seg, 1, 1] = (ushort)GetAcDequant(qIdx + uAcDelta, ctx.BitDepth);
                // V plane
                ts.Dq[seg, 2, 0] = (ushort)GetDcDequant(qIdx + vDcDelta, ctx.BitDepth);
                ts.Dq[seg, 2, 1] = (ushort)GetAcDequant(qIdx + vAcDelta, ctx.BitDepth);
            }
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

        // Initialize refmvs frame for inter prediction (dav1d: dav1d_refmvs_init_frame)
        Span<byte> refPoc = stackalloc byte[7];
        Av1RefMvs.InitFrame(ctx.RefMvs, seqHdr, fh, refPoc,
            ctx.PrevRp, refRefPoc: null, rpRef: null);
        ctx.PrevRp = null; // consume temporal projection from previous frame

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

                for (int tileCol = 0; tileCol < fh.TileCols; tileCol++)
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
                    DecodeTileSuperblockRow(ts, tileIdx, by, tileCol, tileRow);
                }

                // Save temporal MVs for this SB row (dav1d: save_tmvs)
                // DISABLED: IndexOutOfRangeException in SaveTemporalMvs — needs debugging
                // if (fh.IsInterOrSwitch)
                // {
                //     var rf = ctx.RefMvs;
                //     if (rf.Rp != null)
                //     {
                //         int byEnd8 = Math.Min((by + sbStep) >> 1, ctx.Bh >> 1);
                //         Av1RefMvs.SaveTemporalMvs(
                //             rf.Rp, (by >> 1) * rf.RpStride, rf.RpStride,
                //             taskCtx.Rt.R,
                //             rf.MfmvSign,
                //             Math.Min(ctx.Bw >> 1, rf.Iw8), byEnd8,
                //             0, by >> 1);
                //     }
                // }

                // Apply in-loop filters for this superblock row
                ApplyInLoopFilters(sby, ssHor, ssVer, hasChroma);
                // Save lfMask data for CDEF/LR (backup before next row overwrites)
                if (ctx.LfMasksRows != null && ctx.LfMasks != null)
                {
                    for (int col = 0; col < sb128w && col < ctx.LfMasks.Length; col++)
                    {
                        int idx = sby * sb128w + col;
                        if (idx < ctx.LfMasksRows.Length)
                            ctx.LfMasksRows[idx].CopyFrom(ctx.LfMasks[col]);
                    }
                }
        AvDbg.W($"[TILE-INFO] Frame#{frameIdx} TileRows={fh.TileRows} TileCols={fh.TileCols} TileLog2Cols={fh.TileLog2Cols} TileLog2Rows={fh.TileLog2Rows} TileColStartSb[0]={fh.TileColStartSb[0]} [1]={fh.TileColStartSb[1]} [2]={fh.TileColStartSb[2]}");
        if (frameIdx >= 17 && frameIdx <= 20)
                    AvDbg.W($"[SB-ROW] Frame#{frameIdx} sby={sby}/{sbhEnd} done");
            }
        }

        // Write our end-of-frame CDF snapshot for comparison with dav1d's snapshot
        AvDbg.W($"[CDF-DUMP-CHECK] FrameOffset={fh.FrameOffset} TileStatesNull={ctx.TileStates == null} Len={ctx.TileStates?.Length ?? -1}");
        if (ctx.TileStates != null && ctx.TileStates.Length > 0)
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
            for (int sby = 0; sby < sbh; sby++)
                CopyLpf(sby, ssHor, ssVer, hasChroma);
        }

        // === CDEF (Constrained Directional Enhancement Filter) ===
        // Applied once after all deblocking is complete
        AvDbg.W($"[CDEF-ENTRY] CdefBits={fh.CdefBits} LfMasksNull={ctx.LfMasks == null} Damping={fh.CdefDamping} y0={fh.CdefYStrength0}");
        if (fh.CdefNBits > 0 || fh.CdefYStrength0 != 0)  // CDEF enabled if any strength > 0
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
                    yPlane.AsSpan(yy * ctx.YStride, w).CopyTo(row);
                    fs.Write(row);
                }
                AvDbg.W($"[POST-CDEF] Dumped {w}x{h} Y plane to {dumpPath}");
            }
        }

        // === Loop Restoration ===
        if (ctx.RestorePlanes != 0) {
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
                    yPlane.AsSpan(yy * ctx.YStride, w).CopyTo(row);
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

    private void DecodeTileSuperblockRow(Av1TileState ts, int tileIdx, int by,
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
        var t = taskCtx;
        t.TileState = ts;
        t.Left.Reset(isIntra);
        t.By = by;

        // Link tile refmvs to frame refmvs (dav1d: dav1d_refmvs_tile_sbrow_init)
        t.Rt.Rf = ctx.RefMvs;
        t.Rt.TileColStart = ts.ColStart;
        t.Rt.TileColEnd = ts.ColEnd;
        t.Rt.TileRowStart = ts.RowStart;
        t.Rt.TileRowEnd = ts.RowEnd;

        // Link tile R rows to frame R rows (dav1d: refmvs_init_tile_row)
        if (!isIntra)
        {
            int sbSize = sbStep; // dav1d sbsz: 16 for SB64, 32 for SB128 (in 4x4 units)
            int sby = by >> sbShift;
            int off = (sbSize * sby) & 16;
            int rowBase = sby * sbSize;
            var rf = ctx.RefMvs;
            bool anyNull = false;
            for (int i = 0; i < sbSize; i++)
            {
                int rIdx = rowBase + i;
                if (rIdx < rf.R.Length)
                {
                    t.Rt.R[off + 5 + i] = rf.R[rIdx];
                    if (rf.R[rIdx] == null) anyNull = true;
                }
                else
                    t.Rt.R[off + 5 + i] = null;
            }
            if (rowBase + sbSize < rf.R.Length)
            {
                t.Rt.R[off + 0] = rf.R[rowBase + sbSize];
                t.Rt.R[off + 1] = null;
                t.Rt.R[off + 2] = rf.R[rowBase + sbSize + 2];
                t.Rt.R[off + 3] = null;
                t.Rt.R[off + 4] = rf.R[rowBase + sbSize + 4];
            }
            if (anyNull)
                AvDbg.W($"[RFMVS-NULL] sby={sby} rowBase={rowBase} sbSize={sbSize} RfLen={rf.R.Length}");
            if (ctx.FrameHeader?.FrameOffset == 1 && sby == 0)
                AvDbg.W($"[RFMVS-LINK] sby={sby} sbSize={sbSize} off={off} rfR4null={rf.R[4] == null} rfR0len={(rf.R[0]?.Length ?? -1)} rtR9null={t.Rt.R[off + 9] == null}");
        }

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

            // Reset CDEF indices for this superblock
            t.CurSbCdefIdx[0] = -1;
            t.CurSbCdefIdx[1] = -1;
            t.CurSbCdefIdx[2] = -1;
            t.CurSbCdefIdx[3] = -1;

            // Point to the loop filter mask for this SB128 column
            int sb128Col = t.Bx >> 5;
            if (ctx.LfMasks != null && sb128Col < ctx.LfMasks.Length)
            {
                t.LfMask = ctx.LfMasks[sb128Col];
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
            catch (Exception ex)
            {
                AvDbg.W($"[DECODE-CRASH] bx={t.Bx} by={t.By} exception={ex.GetType().Name}: {ex.Message}");
                AvDbg.W($"[DECODE-CRASH] stack: {ex.StackTrace}");
                err = 1;
            }
            if (err != 0)
            {
                // Decode error — save MSAC state and bail
                ts.MsacState = msac.Save();
                return;
            }

            // Store CDEF indices from this SB into the per-SB128 filter mask
            if (t.LfMask != null)
            {
                for (int ci = 0; ci < 4; ci++)
                    t.LfMask.SetCdefIdx(ci, t.CurSbCdefIdx[ci]);
            }

            // Advance above context (every 128px = every SB128, or every other SB64)
            if ((t.Bx & 16) != 0 || sh.Sb128)
                aboveIdx++;
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

            // No super-resolution path (fh.Width[0] == fh.Width[1])
            int x = 4 * t.Bx >> ssHor;
            if ((x & mask) != 0) continue;
            int w = (fh.CodedWidth + ssHor) >> ssHor;
            if (x != 0 && x + halfUnit > w) continue;

            int sbIdx = (t.By >> 5) * ctx.Sb128W + (t.Bx >> 5);
            int unitIdx = ((t.By & 16) >> 3) + ((t.Bx & 16) >> 4);

            if (ctx.LrMasks != null && sbIdx < ctx.LrMasks.Length)
            {
                Av1Decode.ReadRestorationInfo(ref msac, ts,
                    ref ctx.LrMasks[sbIdx].Lr[p, unitIdx], p, frameType);
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
        if ((fh.LfLevelY0 != 0 || fh.LfLevelY1 != 0) && ctx.LfMasks != null)
        {
            int sbSz = seqHdr.Sb128 ? 32 : 16;
            int yPixelRow = sby * sbSz * 4;
            int yOff = yPixelRow * ctx.YStride;
            int uvOff = (yPixelRow >> ssVer) * ctx.UvStride;

            Span<byte> yPlane = ctx.CurrentPlanes[0]!.AsSpan();
            Span<byte> uPlane = hasChroma ? ctx.CurrentPlanes[1]!.AsSpan() : default;
            Span<byte> vPlane = hasChroma ? ctx.CurrentPlanes[2]!.AsSpan() : default;

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
                    yPlane.Slice(yy * ctx.YStride, w).CopyTo(row);
                    fs.Write(row);
                }
                AvDbg.W($"[PRE-DEBLOCK] Dumped F{fh.FrameOffset} {w}x{h} Y plane to {dumpPath}");
            }

            bool startOfTileRow = true; // Simplified: we do one tile at a time
            Av1LoopFilter.LoopFilterSbRowCols(ctx, yPlane, uPlane, vPlane,
                yOff, uvOff, uvOff, ctx.LfMasks, sby, startOfTileRow);
            Av1LoopFilter.LoopFilterSbRowRows(ctx, yPlane, uPlane, vPlane,
                yOff, uvOff, uvOff, ctx.LfMasks, sby);

            // Debug: dump post-deblocking full YUV
            if (DumpPreDeblockY && sby == ctx.SuperBlockRows - 1)
            {
                string dumpPath = @"F:\Code\MediaKernel\post_deblock_full.yuv";
                using var fs = new System.IO.FileStream(dumpPath, System.IO.FileMode.Create);
                int w = ctx.Width4 * 4;
                int h = Math.Min(ctx.Height4 * 4, 64);
                for (int yy = 0; yy < h; yy++) { byte[] row = new byte[w]; yPlane.Slice(yy * ctx.YStride, w).CopyTo(row); fs.Write(row); }
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
        int damping = fh.CdefDamping; // bitdepth_min_8 = 0 for 8-bit
        int yStride = ctx.YStride;
        int uvStride = ctx.UvStride;
        int w4 = ctx.W4;
        int h4 = ctx.H4;
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int sbsz = 16; // 64x64 in 4x4 units

        Span<byte> yPlane = ctx.CurrentPlanes[0]!.AsSpan();
        Span<byte> uPlane = hasChroma ? ctx.CurrentPlanes[1]!.AsSpan() : default;
        Span<byte> vPlane = hasChroma ? ctx.CurrentPlanes[2]!.AsSpan() : default;

        // Pre-CDEF copy for border reference (CDEF filter needs pre-filter neighbors)
        byte[] yBak = ArrayPool<byte>.Shared.Rent(yPlane.Length);
        yPlane.CopyTo(yBak);
        byte[]? uBak = null, vBak = null;
        if (hasChroma)
        {
            uBak = ArrayPool<byte>.Shared.Rent(uPlane.Length);
            uPlane.CopyTo(uBak);
            vBak = ArrayPool<byte>.Shared.Rent(vPlane.Length);
            vPlane.CopyTo(vBak);
        }

        // UV direction mapping for I422 (chroma is 2:1 vertical)
        ReadOnlySpan<byte> uvDirI422 = stackalloc byte[] { 7, 0, 2, 4, 5, 6, 6, 6 };

        // Scratch buffers for left border (2 bytes per row, up to 8 rows)
        Span<byte> leftBuf = stackalloc byte[16]; // 8 rows * 2 cols

        try
        {
            int sb64w = (w4 + 15) >> 4; // number of SB64 columns

            for (int by = 0; by < h4; by += 2)
            {
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

                    int yPriLvl = yLvl >> 2;
                    int ySecLvl = yLvl & 3;
                    ySecLvl += ySecLvl == 3 ? 1 : 0;

                    int uvPriLvl = uvLvl >> 2;
                    int uvSecLvl = uvLvl & 3;
                    uvSecLvl += uvSecLvl == 3 ? 1 : 0;

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
                            dir = Av1Cdef.FindDirection(yBak, yOff, yStride, out variance);
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
                                int botOff = py + 8 < frameHeight ? (py + 8) * yStride + px : yOff + 7 * yStride;
                                int lumaDir = yPriLvl != 0 ? dir : 0;

                                Av1Cdef.FilterBlock(
                                    yPlane, yOff, yStride,
                                    leftBuf, 0, 2,
                                    yBak, topOff, yBak, botOff,
                                    adjYPriLvl, ySecLvl, lumaDir, damping,
                                    8, 8, blockEdges);
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
                                int botOff = cpy + chH < chromaHeight ?
                                    (cpy + chH) * uvStride + cpx : uvOff + (chH - 1) * uvStride;

                                Av1Cdef.FilterBlock(
                                    uvDst, uvOff, uvStride,
                                    leftBuf, 0, 2,
                                    uvSrc, topOff, uvSrc, botOff,
                                    uvPriLvl, uvSecLvl, uvDir, damping - 1,
                                    chW, chH, blockEdges);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(yBak);
            if (uBak != null) ArrayPool<byte>.Shared.Return(uBak);
            if (vBak != null) ArrayPool<byte>.Shared.Return(vBak);
        }
    }

    /// <summary>
    /// Prepare 2-column left border buffer for CDEF from the pre-filter copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrepareCdefLeft(Span<byte> leftBuf, byte[] src, int srcOffset, int stride,
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

            BackupLpf(ctx.LrLpfLine[0]!, yStride,
                ctx.CurrentPlanes[0]!, offset * yStride, yStride,
                ssVer: 0, sb128, yStripe, rowH, w, h);
        }

        if (hasChroma && (restorePlanes & 6) != 0)
        {
            int h = (fh.Height + ssVer) >> ssVer;
            int w = ctx.Bw << (2 - ssHor);
            int rowH = Math.Min((sby + 1) << ((6 - ssVer) + sb128), h - 1);
            int offsetUv = offset >> ssVer;
            int yStripe = (sby << ((6 - ssVer) + sb128)) - offsetUv;

            if ((restorePlanes & 2) != 0)
                BackupLpf(ctx.LrLpfLine[1]!, uvStride,
                    ctx.CurrentPlanes[1]!, offsetUv * uvStride, uvStride,
                    ssVer, sb128, yStripe, rowH, w, h);
            if ((restorePlanes & 4) != 0)
                BackupLpf(ctx.LrLpfLine[2]!, uvStride,
                    ctx.CurrentPlanes[2]!, offsetUv * uvStride, uvStride,
                    ssVer, sb128, yStripe, rowH, w, h);
        }
    }

    /// <summary>
    /// Copy specific rows from source plane to LR LPF buffer at stripe boundaries.
    /// dav1d: backup_lpf (lf_apply_tmpl.c:41-101), simplified for no super-res, single-threaded.
    /// </summary>
    private static void BackupLpf(byte[] dst, int dstStride,
        byte[] src, int srcOffset, int srcStride,
        int ssVer, int sb128, int row, int rowH, int srcW, int h)
    {
        int dstOff = 0;

        // Single-threaded: shift previous bottom→top and advance
        if (row > 0)
        {
            int top = (4 << sb128) * dstStride;
            Buffer.BlockCopy(dst, top, dst, 0, dstStride);
            Buffer.BlockCopy(dst, top + dstStride, dst, dstStride, dstStride);
            Buffer.BlockCopy(dst, top + dstStride * 2, dst, dstStride * 2, dstStride);
            Buffer.BlockCopy(dst, top + dstStride * 3, dst, dstStride * 3, dstStride);
        }
        dstOff = 4 * dstStride;

        // The first stripe is shorter by 8 luma rows (→ fewer for chroma)
        int stripeH = ((64 << sb128) - 8 * (row == 0 ? 1 : 0)) >> ssVer;
        // Advance src to stripe_h - 2 rows in (the last 2 rows of the stripe)
        int srcOff = srcOffset + (stripeH - 2) * srcStride;

        while (row + stripeH <= rowH)
        {
            int nLines = 4 - (row + stripeH + 1 == h ? 1 : 0);
            for (int i = 0; i < 4; i++)
            {
                if (i == nLines)
                {
                    // Duplicate previous row
                    Buffer.BlockCopy(dst, dstOff - dstStride, dst, dstOff, srcW);
                }
                else
                {
                    Buffer.BlockCopy(src, srcOff, dst, dstOff, srcW);
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
    private void ApplyLoopRestoration(int sby, int ssHor, int ssVer, bool hasChroma)
    {
        var fh = frameHdr;
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int restorePlanes = ctx.RestorePlanes;
        int sbStep = seqHdr.Sb128 ? 32 : 16;
        int notLast = sby + 1 < ((ctx.Bh + sbStep - 1) / sbStep) ? 1 : 0;
        int offsetY = 8 * (sby > 0 ? 1 : 0);

        if ((restorePlanes & 1) != 0)
        {
            int h = fh.Height;
            int w = fh.CodedWidth;
            int nextRowY = (sby + 1) << (6 + sb128);
            int rowH = Math.Min(nextRowY - 8 * notLast, h);
            int yStripe = (sby << (6 + sb128)) - offsetY;

            LrSbRow(ctx.CurrentPlanes[0]!, offsetY * ctx.YStride, ctx.YStride,
                ctx.LrLpfLine[0]!, yStripe, w, h, rowH, 0, 0, sby);
        }

        if (hasChroma && (restorePlanes & 6) != 0)
        {
            int h = (fh.Height + ssVer) >> ssVer;
            int w = (fh.CodedWidth + ssHor) >> ssHor;
            int nextRowY = (sby + 1) << ((6 - ssVer) + sb128);
            int rowH = Math.Min(nextRowY - ((8 >> ssVer) * notLast), h);
            int offsetUv = offsetY >> ssVer;
            int yStripe = (sby << ((6 - ssVer) + sb128)) - offsetUv;

            if ((restorePlanes & 2) != 0)
                LrSbRow(ctx.CurrentPlanes[1]!, offsetUv * ctx.UvStride, ctx.UvStride,
                    ctx.LrLpfLine[1]!, yStripe, w, h, rowH, 1, ssHor, sby);

            if ((restorePlanes & 4) != 0)
                LrSbRow(ctx.CurrentPlanes[2]!, offsetUv * ctx.UvStride, ctx.UvStride,
                    ctx.LrLpfLine[2]!, yStripe, w, h, rowH, 2, ssHor, sby);
        }
    }

    /// <summary>
    /// Apply LR to one plane for one SB row, iterating left→right over restoration units.
    /// Ported from dav1d lr_sbrow (lr_apply_tmpl.c:107-166).
    /// </summary>
    private void LrSbRow(byte[] plane, int pOff, int stride,
        byte[] lpf, int y, int w, int h, int rowH,
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
        var preLrBorder = new byte[2][];
        preLrBorder[0] = new byte[borderH * 4];
        preLrBorder[1] = new byte[borderH * 4];

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
                LrStripe(plane, pBase, stride, preLrBorder[1 - bit], lpf,
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
            LrStripe(plane, pBase, stride, preLrBorder[1 - bit], lpf,
                x, y, planeIdx, unitW, rowH,
                ref ctx.LrMasks[curSbIdx].Lr[planeIdx, curUnitIdx], edges, sby, ssVer);
        }
    }

    /// <summary>
    /// Backup 4 left-border columns for LR.
    /// dav1d: backup4xU (lr_apply_tmpl.c:100-105).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Backup4xU(byte[] dst, byte[] src, int srcOff, int stride, int rows)
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
    private void LrStripe(byte[] p, int pOff, int stride,
        byte[] left, byte[] lpf,
        int x, int y, int plane, int unitW, int rowH,
        ref Av1RestorationUnit lr, Av1LoopRestoration.LrEdgeFlags edges,
        int sby, int ssVer)
    {
        int sb128 = seqHdr.Sb128 ? 1 : 0;
        int sbh = (ctx.Bh + (seqHdr.Sb128 ? 31 : 15)) >> (seqHdr.Sb128 ? 5 : 4);

        // lpf offset: for single-threaded (have_tt=0), just add x
        int lpfOff = x;

        // First stripe is shorter by 8 luma rows (→ fewer for chroma)
        int stripeH = Math.Min((64 - 8 * (y == 0 ? 1 : 0)) >> ssVer, rowH - y);

        // Build filter params and pick the filter function
        Span<byte> pSpan = p.AsSpan();
        ReadOnlySpan<byte> lpfSpan = lpf.AsSpan();
        ReadOnlySpan<byte> leftSpan = left.AsSpan();

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
                filterH[3] = (short)(128 - (lr.FilterH0 + lr.FilterH1 + lr.FilterH2) * 2);
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
                int w = 128 - ew0 - ew1;  // dav1d: params.sgr.w1 used by both sgr_5x5_c and sgr_3x3_c

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
                        unitW, stripeH, s0, w, curEdges);
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

    private void UpdateReferenceFrames()
    {
        var fh = frameHdr;
        byte refreshFlags = fh.RefreshFrameFlags;

        for (int i = 0; i < 8; i++)
        {
            if ((refreshFlags & (1 << i)) != 0)
            {
                var refFrame = ctx.RefFrames[i];
                refFrame.Width = fh.CodedWidth;
                refFrame.Height = fh.Height;
                refFrame.RenderWidth = fh.RenderWidth;
                refFrame.RenderHeight = fh.RenderHeight;
                refFrame.FrameType = fh.FrameType;
                refFrame.OrderHint = fh.FrameOffset;
                refFrame.Valid = true;

                // Copy current frame planes to reference
                CopyFrameToReference(refFrame);

                // Snapshot CDF if not disabled
                if (!fh.DisableCdfUpdate)
                {
                    int updateTile = fh.TileUpdate;
                    if (updateTile < fh.TileCols * fh.TileRows && ctx.TileStates != null)
                    {
                        if (refFrame.CdfSnapshot == null)
                            refFrame.CdfSnapshot = new Av1CdfContext();
                        refFrame.CdfSnapshot.CopyFrom(ctx.TileStates[updateTile].Cdf);
                    }
                }

                // Snapshot temporal MVs for inter prediction
                if (ctx.RefMvs.Rp != null)
                {
                    int rpLen = ctx.RefMvs.Rp.Length;
                    if (refFrame.TemporalMvs == null || refFrame.TemporalMvs.Length < rpLen)
                        refFrame.TemporalMvs = new Av1RefMvsTemporalBlock[rpLen];
                    ctx.RefMvs.Rp.CopyTo(refFrame.TemporalMvs, 0);
                }
            }
        }
    }

    private void CopyFrameToReference(Av1ReferenceFrame refFrame)
    {
        for (int plane = 0; plane < 3; plane++)
        {
            var src = ctx.CurrentPlanes[plane];
            if (src == null) continue;

            int stride = ctx.CurrentStrides[plane];
            int height = plane == 0 ? frameHdr.Height :
                (ctx.PixelLayout == Av1PixelLayout.I420 ? (frameHdr.Height + 1) >> 1 : frameHdr.Height);
            int width = plane == 0 ? frameHdr.CodedWidth :
                (ctx.PixelLayout == Av1PixelLayout.I444 ? frameHdr.CodedWidth :
                 (frameHdr.CodedWidth + 1) >> 1);

            int bufSize = stride * height;
            if (refFrame.Planes[plane] == null || refFrame.Planes[plane]!.Length < bufSize)
                refFrame.Planes[plane] = new byte[bufSize];

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

        // For key frames, show_existing_frame refreshes all reference slots
        if (fh.FrameType == Av1FrameType.Key)
        {
            for (int i = 0; i < 8; i++)
            {
                if (i == refIdx) continue;
                var dst = ctx.RefFrames[i];
                dst.Width = refFrame.Width;
                dst.Height = refFrame.Height;
                dst.RenderWidth = refFrame.RenderWidth;
                dst.RenderHeight = refFrame.RenderHeight;
                dst.FrameType = refFrame.FrameType;
                dst.OrderHint = refFrame.OrderHint;
                dst.Valid = true;

                for (int p = 0; p < 3; p++)
                {
                    if (refFrame.Planes[p] != null)
                    {
                        int sz = refFrame.Strides[p] *
                            (p == 0 ? refFrame.Height : (refFrame.Height + 1) >> 1);
                        if (dst.Planes[p] == null || dst.Planes[p]!.Length < sz)
                            dst.Planes[p] = new byte[sz];
                        refFrame.Planes[p].AsSpan(0, sz).CopyTo(dst.Planes[p]);
                        dst.Strides[p] = refFrame.Strides[p];
                    }
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

    private DecodedVideoFrame? ExtractReferenceFrame(Av1ReferenceFrame refFrame, long presentationTimeTicks)
    {
        int w = refFrame.Width;
        int h = refFrame.Height;
        int ySize = w * h;
        int uvW = (w + 1) >> 1;
        int uvH = (h + 1) >> 1;
        int uvSize = uvW * uvH;
        int totalSize = ySize + uvSize * 2;

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int yOff = 0, uOff = ySize, vOff = ySize + uvSize;

        // Copy Y
        if (refFrame.Planes[0] != null)
        {
            for (int y = 0; y < h; y++)
                refFrame.Planes[0].AsSpan(y * refFrame.Strides[0], w)
                    .CopyTo(outputBuffer.AsSpan(yOff + y * w));
        }

        // Copy U
        if (refFrame.Planes[1] != null)
        {
            for (int y = 0; y < uvH; y++)
                refFrame.Planes[1].AsSpan(y * refFrame.Strides[1], uvW)
                    .CopyTo(outputBuffer.AsSpan(uOff + y * uvW));
        }

        // Copy V
        if (refFrame.Planes[2] != null)
        {
            for (int y = 0; y < uvH; y++)
                refFrame.Planes[2].AsSpan(y * refFrame.Strides[2], uvW)
                    .CopyTo(outputBuffer.AsSpan(vOff + y * uvW));
        }

        isReady = true;
        return new DecodedVideoFrame(
            w, h, PixelFormat.Yuv420P, presentationTimeTicks,
            outputBuffer,
            yOff, w,
            uOff, uvW,
            vOff, uvW);
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

        int ySize = w * h;
        int uvW = (w + 1) >> 1;
        int uvH = (h + 1) >> 1;
        int uvSize = uvW * uvH;
        int totalSize = ySize + uvSize * 2;

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int yOff = 0, uOff = ySize, vOff = ySize + uvSize;

        var yPlane = ctx.CurrentPlanes[0];
        var uPlane = ctx.CurrentPlanes[1];
        var vPlane = ctx.CurrentPlanes[2];

        if (yPlane == null) {
            AvDbg.W("[EXTRACT] yPlane is NULL - skipping frame");
            ArrayPool<byte>.Shared.Return(outputBuffer);
            return null;
        }

        if (yPlane != null)
        {
            for (int y = 0; y < h; y++)
                yPlane.AsSpan(y * ctx.CurrentStrides[0], w)
                    .CopyTo(outputBuffer.AsSpan(yOff + y * w));
        }

        if (uPlane != null)
        {
            for (int y = 0; y < uvH; y++)
                uPlane.AsSpan(y * ctx.CurrentStrides[1], uvW)
                    .CopyTo(outputBuffer.AsSpan(uOff + y * uvW));
        }

        if (vPlane != null)
        {
            for (int y = 0; y < uvH; y++)
                vPlane.AsSpan(y * ctx.CurrentStrides[2], uvW)
                    .CopyTo(outputBuffer.AsSpan(vOff + y * uvW));
        }

        return new DecodedVideoFrame(
            w, h, PixelFormat.Yuv420P, presentationTimeTicks,
            outputBuffer,
            yOff, w,
            uOff, uvW,
            vOff, uvW);
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
