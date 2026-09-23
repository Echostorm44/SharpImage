// AV1 block/partition decoder
// Ported from dav1d: src/decode.c (VideoLAN dav1d, BSD-2-Clause)
// Implements superblock partition tree decoding and block-level syntax parsing.
// The partition decoder (DecodeSuperblock) recursively splits superblocks into
// coding blocks, then DecodeBlock reads intra/inter mode decisions and coefficients.

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Per-block task context for AV1 decoding. Tracks current position,
/// above/left neighbor contexts, and scratch buffers during decode.
/// Maps to dav1d Dav1dTaskContext (internal.h).
/// </summary>
public sealed class Av1TaskContext
{
    /// <summary>Current block position in 4-pixel units.</summary>
    public int Bx, By;

    /// <summary>Left neighbor block context (32 entries for 128-pixel SB edge).</summary>
    public Av1BlockContextManaged Left = new();

    /// <summary>Above neighbor block context (pointer into row-wide array).</summary>
    public Av1BlockContextManaged Above = new();

    /// <summary>Tile state for the current tile.</summary>
    public Av1TileState? TileState;

    /// <summary>MSAC arithmetic coder is passed by ref to decode methods (ref struct).</summary>
    // Note: Av1Msac is a ref struct and cannot be stored as a class field.

    /// <summary>Palette sizes for UV plane (above/left × 32 positions).</summary>
    public byte[,] PalSzUv = new byte[2, 32];

    /// <summary>CDEF index values for current superblock (up to 4 for 128×128).</summary>
    public sbyte[] CurSbCdefIdx = new sbyte[4];

    /// <summary>Chroma filter type for top-left 4×4 sub-block (for sub8×8 chroma).</summary>
    public byte Tl4x4Filter;

    /// <summary>Warped motion parameters for current block.</summary>
    public Av1WarpedMotionParams WarpMv;

    // Scratch buffers for coefficient decoding
    public int[] CfBuf = new int[32 * 32];
    public short[] AcBuf = new short[32 * 32];
    public ushort[] DqBuf = new ushort[2];
    public byte[] PalIdxY = new byte[128 * 128];
    public byte[] PalIdxUv = new byte[128 * 128];
    public byte[] Levels = new byte[32 * 34];
    public byte[,] PalOrder = new byte[64, 8];
    public byte[] PalCtx = new byte[64];

    // Palette color storage (current block)
    public ushort[] PalColorsY = new ushort[8];   // Up to 8 palette colors for Y (ushort: 10/12-bit)
    public ushort[] PalColorsU = new ushort[8];   // Up to 8 palette colors for U
    public ushort[] PalColorsV = new ushort[8];   // Up to 8 palette colors for V

    // Previous frame palette colors for prediction (dav1d: al_pal[2][32][3][8])
    // [dir][pos][plane][color_idx]: dir=0 above (indexed by bx4), dir=1 left (indexed by by4)
    public ushort[,,] PalPrevY = new ushort[2, 32, 3 * 8]; // flattened plane+color (ushort: 10/12-bit)
    public byte[,] PalPrevSz = new byte[2, 32]; // [dir][pos]: palette sizes (dav1d: pal_sz_uv[2][32])

    // Scratch buffers for inter prediction
    /// <summary>Edge buffer for intra-in-inter (interintra) prediction.</summary>
    public ushort[] EdgeBuf = new ushort[257];

    /// <summary>Temporary buffer for interintra prediction.</summary>
    public ushort[] InterIntraBuf = new ushort[128 * 128];

    /// <summary>Compound prediction temporary buffers (2 × 128×128 int16).</summary>
    public short[][] CompInterBuf = { new short[128 * 128], new short[128 * 128] };

    /// <summary>Segment mask buffer for compound SEG mode.</summary>
    public byte[] SegMask = new byte[128 * 128];

    /// <summary>Emulated edge buffer for out-of-frame motion compensation.</summary>
    public ushort[] EmuEdgeBuf = new ushort[320 * 320];

    /// <summary>Transform type map for inter blocks (32×32 entries for SB128).</summary>
    public byte[] TxtpMap = new byte[32 * 32];

    /// <summary>Reference MV tile context for inter prediction.</summary>
    public Av1RefMvsTile Rt = new();

    /// <summary>Current superblock's loop filter mask (set per-SB during decode).</summary>
    public Av1FilterMask? LfMask;
}

/// <summary>
/// Managed version of Av1BlockContext — uses regular arrays instead of
/// fixed-size buffers, avoiding unsafe code. Same layout as Av1BlockContext.
/// </summary>
public sealed class Av1BlockContextManaged
{
    public readonly byte[] Mode = new byte[32];
    public readonly byte[] LCoef = new byte[32];
    public readonly byte[] CCoef0 = new byte[32];
    public readonly byte[] CCoef1 = new byte[32];
    public readonly byte[] SegPred = new byte[32];
    public readonly byte[] Skip = new byte[32];
    public readonly byte[] SkipMode = new byte[32];
    public readonly byte[] Intra = new byte[32];
    public readonly byte[] CompType = new byte[32];
    public readonly sbyte[] Ref0 = new sbyte[32];
    public readonly sbyte[] Ref1 = new sbyte[32];
    public readonly byte[] Filter0 = new byte[32];
    public readonly byte[] Filter1 = new byte[32];
    public readonly sbyte[] TxIntra = new sbyte[32];
    public readonly sbyte[] Tx = new sbyte[32];
    public readonly byte[] TxLpfY = new byte[32];
    public readonly byte[] TxLpfUv = new byte[32];
    public readonly byte[] Partition = new byte[16];
    public readonly byte[] UvMode = new byte[32];
    public readonly byte[] PalSz = new byte[32];

    public void Reset(bool keyFrame)
    {
        Array.Fill(Intra, keyFrame ? (byte)1 : (byte)0);
        Array.Fill(UvMode, (byte)Av1IntraPredMode.Dc);
        if (keyFrame)
            Array.Fill(Mode, (byte)Av1IntraPredMode.Dc);
        else
            Array.Fill(Mode, (byte)Av1InterPredMode.NearestMv);
        Array.Fill(LCoef, (byte)0x40);
        Array.Fill(CCoef0, (byte)0x40);
        Array.Fill(CCoef1, (byte)0x40);
        Array.Fill(Ref0, (sbyte)-1);
        Array.Fill(Ref1, (sbyte)-1);
        Array.Fill(Filter0, (byte)Av1Tables.NSwitchableFilters);
        Array.Fill(Filter1, (byte)Av1Tables.NSwitchableFilters);
        Array.Fill(TxIntra, (sbyte)-1);
        Array.Fill(Tx, (sbyte)4); // TX_64X64 — matches dav1d reset_context
        Array.Fill(TxLpfY, (byte)2); // matches dav1d reset_context
        Array.Fill(TxLpfUv, (byte)1); // matches dav1d reset_context
        Array.Fill(Partition, (byte)0);
        Array.Fill(PalSz, (byte)0);
        Array.Fill(SegPred, (byte)0);
        Array.Fill(Skip, (byte)0);
        Array.Fill(SkipMode, (byte)0);
        Array.Fill(CompType, (byte)0);
    }

    /// <summary>Set n entries starting at offset to the given value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fill(byte[] arr, int offset, int count, byte value)
    {
        if (count <= 0 || offset >= arr.Length) return;
        int safeCount = Math.Min(count, arr.Length - offset);
        arr.AsSpan(offset, safeCount).Fill(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fill(sbyte[] arr, int offset, int count, sbyte value)
    {
        if (count <= 0 || offset >= arr.Length) return;
        int safeCount = Math.Min(count, arr.Length - offset);
        arr.AsSpan(offset, safeCount).Fill(value);
    }
}

/// <summary>
/// AV1 block and partition decoder. Decodes the recursive partition tree
/// for each superblock, then decodes individual coding blocks (intra modes,
/// transform sizes, coefficients for intra; inter modes, MVs for inter).
/// </summary>
public static class Av1Decode
{
    // ========================================================================
    // Partition Context Helpers
    // ========================================================================

    /// <summary>
    /// Get partition context from above and left neighbor state.
    /// Maps to dav1d get_partition_ctx (env.h).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPartitionCtx(Av1BlockContextManaged above, Av1BlockContextManaged left,
        Av1BlockLevel bl, int by8, int bx8)
    {
        return ((above.Partition[bx8] >> (4 - (int)bl)) & 1) +
               (((left.Partition[by8] >> (4 - (int)bl)) & 1) << 1);
    }

    /// <summary>
    /// Get intra context from above and left neighbor state.
    /// Maps to dav1d get_intra_ctx (env.h).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIntraCtx(Av1BlockContextManaged above, Av1BlockContextManaged left,
        int by4, int bx4, bool haveTop, bool haveLeft)
    {
        if (haveLeft)
        {
            if (haveTop)
            {
                int ctx = left.Intra[by4] + above.Intra[bx4];
                return ctx + (ctx == 2 ? 1 : 0);
            }
            return left.Intra[by4] * 2;
        }
        return haveTop ? above.Intra[bx4] * 2 : 0;
    }

    /// <summary>
    /// Get transform size context from above and left.
    /// Maps to dav1d get_tx_ctx (env.h).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTxCtx(Av1BlockContextManaged above, Av1BlockContextManaged left,
        in Av1TxfmInfo maxTx, int by4, int bx4)
    {
        return (left.TxIntra[by4] >= maxTx.Lh ? 1 : 0) +
               (above.TxIntra[bx4] >= maxTx.Lw ? 1 : 0);
    }

    /// <summary>
    /// Gather probability for "only horizontal or split" from the partition CDF.
    /// Used when only horizontal split is possible (at bottom edge).
    /// Maps to dav1d gather_top_partition_prob (env.h).
    /// </summary>
    public static ushort GatherTopPartitionProb(ReadOnlySpan<ushort> pc, Av1BlockLevel bl)
    {
        uint o = (uint)(pc[(int)Av1BlockPartition.Vertical - 1] - pc[(int)Av1BlockPartition.TopSplit]);
        o += pc[(int)Av1BlockPartition.LeftSplit - 1];
        if (bl != Av1BlockLevel.Bl128x128)
            o += (uint)(pc[(int)Av1BlockPartition.Vertical4 - 1] - pc[(int)Av1BlockPartition.RightSplit]);
        return (ushort)o;
    }

    /// <summary>
    /// Gather probability for "only vertical or split" from the partition CDF.
    /// Used when only vertical split is possible (at right edge).
    /// Maps to dav1d gather_left_partition_prob (env.h).
    /// </summary>
    public static ushort GatherLeftPartitionProb(ReadOnlySpan<ushort> pc, Av1BlockLevel bl)
    {
        uint o = (uint)(pc[(int)Av1BlockPartition.Horizontal - 1] - pc[(int)Av1BlockPartition.Horizontal]);
        o += (uint)(pc[(int)Av1BlockPartition.Split - 1] - pc[(int)Av1BlockPartition.LeftSplit]);
        if (bl != Av1BlockLevel.Bl128x128)
            o += (uint)(pc[(int)Av1BlockPartition.Horizontal4 - 1] - pc[(int)Av1BlockPartition.Horizontal4]);
        return (ushort)o;
    }

    // ========================================================================
    // Restoration Info — Read LR Unit Parameters
    // ========================================================================

    /// <summary>
    /// Read restoration unit info from the MSAC bitstream for one LR unit.
    /// Called before each superblock decode to advance MSAC past restoration data.
    /// Maps to dav1d read_restoration_info (decode.c:2523).
    /// </summary>
    public static void ReadRestorationInfo(
        ref Av1Msac msac,
        Av1TileState ts,
        ref Av1RestorationUnit lr,
        int plane,
        Av1RestorationType frameType)
    {
        if (frameType == Av1RestorationType.Switchable)
        {
            int filter = (int)msac.DecodeSymbolAdapt4(ts.Cdf.Mode.RestoreSwitchable, 2);
            lr.Type = (Av1RestorationType)(filter + (filter != 0 ? 1 : 0));
        }
        else
        {
            var cdf = frameType == Av1RestorationType.Wiener
                ? ts.Cdf.Mode.RestoreWiener
                : ts.Cdf.Mode.RestoreSgrproj;
            uint type = msac.DecodeBoolAdapt(cdf);
            lr.Type = type != 0 ? frameType : Av1RestorationType.None;
        }

        if (lr.Type == Av1RestorationType.Wiener)
        {
            ref var lrRef = ref ts.LrRef[plane];
            lr.FilterV0 = plane != 0 ? (sbyte)0 :
                (sbyte)(msac.DecodeSubexp(lrRef.FilterV0 + 5, 16, 1) - 5);
            lr.FilterV1 = (sbyte)(msac.DecodeSubexp(lrRef.FilterV1 + 23, 32, 2) - 23);
            lr.FilterV2 = (sbyte)(msac.DecodeSubexp(lrRef.FilterV2 + 17, 64, 3) - 17);

            lr.FilterH0 = plane != 0 ? (sbyte)0 :
                (sbyte)(msac.DecodeSubexp(lrRef.FilterH0 + 5, 16, 1) - 5);
            lr.FilterH1 = (sbyte)(msac.DecodeSubexp(lrRef.FilterH1 + 23, 32, 2) - 23);
            lr.FilterH2 = (sbyte)(msac.DecodeSubexp(lrRef.FilterH2 + 17, 64, 3) - 17);

            lr.SgrWeight0 = lrRef.SgrWeight0;
            lr.SgrWeight1 = lrRef.SgrWeight1;
            ts.LrRef[plane] = lr;
        }
        else if (lr.Type >= Av1RestorationType.SelfGuided)
        {
            ref var lrRef = ref ts.LrRef[plane];
            uint idx = msac.DecodeBools(4);
            ushort param0 = Av1Tables.SgrParams[idx, 0];
            ushort param1 = Av1Tables.SgrParams[idx, 1];
            lr.Type = (Av1RestorationType)((int)Av1RestorationType.SelfGuided + (int)idx);
            lr.SgrWeight0 = param0 != 0
                ? (sbyte)(msac.DecodeSubexp(lrRef.SgrWeight0 + 96, 128, 4) - 96)
                : (sbyte)0;
            lr.SgrWeight1 = param1 != 0
                ? (sbyte)(msac.DecodeSubexp(lrRef.SgrWeight1 + 32, 128, 4) - 32)
                : (sbyte)95;
            lr.FilterV0 = lrRef.FilterV0;
            lr.FilterV1 = lrRef.FilterV1;
            lr.FilterV2 = lrRef.FilterV2;
            lr.FilterH0 = lrRef.FilterH0;
            lr.FilterH1 = lrRef.FilterH1;
            lr.FilterH2 = lrRef.FilterH2;
            ts.LrRef[plane] = lr;
        }
    }

    // ========================================================================
    // Superblock Decode — Partition Tree
    // ========================================================================

    /// <summary>
    /// Recursively decode the partition tree for a superblock or sub-block.
    /// Reads partition decisions from the bitstream via MSAC, then calls
    /// <see cref="DecodeBlock"/> for each leaf block.
    /// Maps to dav1d decode_sb (decode.c).
    /// </summary>
    /// <returns>0 on success, non-zero on error.</returns>
    public static int DbgPartCount;

    public static int DecodeSuperblock(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx,
        Av1EdgeNode[] edgeTree, int edgeIdx, Av1BlockLevel bl)
    {
        if (bl == Av1BlockLevel.Bl128x128 && Av1Reconstruction.DbgBlockCount < 10)
            AvDbg.W($"[SB-DECODE] bx={t.Bx} by={t.By} bl={bl} blockCnt={Av1Reconstruction.DbgBlockCount}");
        var fh = ctx.FrameHeader!;
        var ts = t.TileState!;
        int hsz = 16 >> (int)bl;
        bool haveHSplit = ctx.Width4 > t.Bx + hsz;
        bool haveVSplit = ctx.Height4 > t.By + hsz;

        if (!haveHSplit && !haveVSplit)
        {
            // Force split when block extends beyond both edges
            return DecodeSuperblock(t, ref msac, ctx, edgeTree,
                Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 0), bl + 1);
        }

        int bx8 = (t.Bx & 31) >> 1;
        int by8 = (t.By & 31) >> 1;
        int partCtx = GetPartitionCtx(t.Above, t.Left, bl, by8, bx8);
        var partCdf = ts.Cdf.GetPartitionCdf(bl, partCtx);

        Av1BlockPartition bp;

        if (haveHSplit && haveVSplit)
        {
            // Full range of partition types available
            if (DbgPartCount < 30)
            {
                // Dump MSAC state and CDF BEFORE decode
                uint dbgC = (uint)(msac.DebugDif >> (64 - 16));
                AvDbg.W($"[PART-PRE] #{DbgPartCount+1}: bl={bl} ctx={partCtx} nSym={Av1Tables.PartitionTypeCount[(int)bl]}");
                AvDbg.W($"  msac: dif={msac.DebugDif:X16} rng={msac.DebugRng:X4} cnt={msac.Cnt}");
                AvDbg.W("  cdf:");
                for (int ci = 0; ci <= Av1Tables.PartitionTypeCount[(int)bl]; ci++)
                    AvDbg.W($" {partCdf[ci]}");
                AvDbg.W();
                // For first partition of any frame, print key CDF values directly from context
                if (DbgPartCount == 1)
                    AvDbg.W($"[PART-CDF-VERIFY] Partition[4][0]={ts.Cdf.Mode.Partition[4][0]} cnt={ts.Cdf.Mode.Partition[4][9]} Partition[8][0]={ts.Cdf.Mode.Partition[8][0]} Partition[12][0]={ts.Cdf.Mode.Partition[12][0]}");
            }

            Av1Msac.Phase = 0;
            bp = (Av1BlockPartition)msac.DecodeSymbolAdapt16(
                partCdf, Av1Tables.PartitionTypeCount[(int)bl]);

            // Clear stale DbgLabel so trace labels reflect current operation
            Av1Msac.DbgLabel = null;

            if (DbgPartCount < 30)
            {
                DbgPartCount++;
                AvDbg.W($"[PART] #{DbgPartCount}: bx={t.Bx} by={t.By} bl={bl} bp={bp} ctx={partCtx}");
                AvDbg.W($"  msac-post: dif={msac.DebugDif:X16} rng={msac.DebugRng:X4} cnt={msac.Cnt}");
                AvDbg.W($"  cdf-vals:");
                for (int ci = 0; ci <= Av1Tables.PartitionTypeCount[(int)bl]; ci++)
                    AvDbg.W($" {partCdf[ci]}");
                AvDbg.W();
                // Inline verification: print cdf[0] directly from the array
                AvDbg.W($"  PART-DIRECT: Partition[{partCtx}]={(Av1BlockLevel)bl} ctx={partCtx} cdf[0]={ts.Cdf.Mode.Partition[(int)bl * 4 + partCtx][0]}");
            }

            ref readonly var bsizes = ref Av1Tables.BlockSizesPerPartition;

            switch (bp)
            {
                case Av1BlockPartition.None:
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bsizes[(int)bl, 0, 0], bp, edgeTree[edgeIdx].O) != 0)
                        return -1;
                    break;

                case Av1BlockPartition.Horizontal:
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bsizes[(int)bl, 1, 0], bp, edgeTree[edgeIdx].H0) != 0)
                        return -1;
                    t.By += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bsizes[(int)bl, 1, 0], bp, edgeTree[edgeIdx].H1) != 0)
                        return -1;
                    t.By -= hsz;
                    break;

                case Av1BlockPartition.Vertical:
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bsizes[(int)bl, 2, 0], bp, edgeTree[edgeIdx].V0) != 0)
                        return -1;
                    t.Bx += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bsizes[(int)bl, 2, 0], bp, edgeTree[edgeIdx].V1) != 0)
                        return -1;
                    t.Bx -= hsz;
                    break;

                case Av1BlockPartition.Split:
                    if (bl == Av1BlockLevel.Bl8x8)
                    {
                        // 4×4 split at 8×8 level — 4 sub-blocks
                        ref readonly var tip = ref edgeTree[edgeIdx];
                        if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                            Av1BlockSize.Bs4x4, bp, Av1EdgeFlags.AllTrAndBl) != 0)
                            return -1;
                        byte savedFilter = t.Tl4x4Filter;
                        t.Bx++;
                        if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                            Av1BlockSize.Bs4x4, bp, tip.Split0) != 0)
                            return -1;
                        t.Bx--;
                        t.By++;
                        if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                            Av1BlockSize.Bs4x4, bp, tip.Split1) != 0)
                            return -1;
                        t.Bx++;
                        t.Tl4x4Filter = savedFilter;
                        if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                            Av1BlockSize.Bs4x4, bp, tip.Split2) != 0)
                            return -1;
                        t.Bx--;
                        t.By--;
                    }
                    else
                    {
                        // Recursive 4-way split
                        int c0 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 0);
                        int c1 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 1);
                        int c2 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 2);
                        int c3 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 3);

                        if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c0, bl + 1) != 0) return 1;
                        t.Bx += hsz;
                        if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c1, bl + 1) != 0) return 1;
                        t.Bx -= hsz;
                        t.By += hsz;
                        if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c2, bl + 1) != 0) return 1;
                        t.Bx += hsz;
                        if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c3, bl + 1) != 0) return 1;
                        t.Bx -= hsz;
                        t.By -= hsz;
                    }
                    break;

                case Av1BlockPartition.TopSplit:
                {
                    byte bs0 = bsizes[(int)bl, (int)Av1BlockPartition.TopSplit, 0];
                    byte bs1 = bsizes[(int)bl, (int)Av1BlockPartition.TopSplit, 1];
                    // Diagnostic: MSAC state right before first leaf block
                    if (t.Bx == 0 && t.By == 0 && ctx.FrameHeader!.FrameOffset == 1)
                        AvDbg.W($"[TOPSPLIT-PREBLK] rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} dif={(uint)(msac.DebugDif>>32):X8} cnt={msac.Cnt}");
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs0, bp, Av1EdgeFlags.AllTrAndBl) != 0)
                        return -1;
                    t.Bx += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs0, bp, edgeTree[edgeIdx].V1) != 0)
                        return -1;
                    t.Bx -= hsz;
                    t.By += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs1, bp, edgeTree[edgeIdx].H1) != 0)
                        return -1;
                    t.By -= hsz;
                    break;
                }

                case Av1BlockPartition.BottomSplit:
                {
                    byte bs0 = bsizes[(int)bl, (int)Av1BlockPartition.BottomSplit, 0];
                    byte bs1 = bsizes[(int)bl, (int)Av1BlockPartition.BottomSplit, 1];
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs0, bp, edgeTree[edgeIdx].H0) != 0)
                        return -1;
                    t.By += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs1, bp, edgeTree[edgeIdx].V0) != 0)
                        return -1;
                    t.Bx += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs1, bp, Av1EdgeFlags.None) != 0)
                        return -1;
                    t.Bx -= hsz;
                    t.By -= hsz;
                    break;
                }

                case Av1BlockPartition.LeftSplit:
                {
                    byte bs0 = bsizes[(int)bl, (int)Av1BlockPartition.LeftSplit, 0];
                    byte bs1 = bsizes[(int)bl, (int)Av1BlockPartition.LeftSplit, 1];
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs0, bp, Av1EdgeFlags.AllTrAndBl) != 0)
                        return -1;
                    t.By += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs0, bp, edgeTree[edgeIdx].H1) != 0)
                        return -1;
                    t.By -= hsz;
                    t.Bx += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs1, bp, edgeTree[edgeIdx].V1) != 0)
                        return -1;
                    t.Bx -= hsz;
                    break;
                }

                case Av1BlockPartition.RightSplit:
                {
                    byte bs0 = bsizes[(int)bl, (int)Av1BlockPartition.RightSplit, 0];
                    byte bs1 = bsizes[(int)bl, (int)Av1BlockPartition.RightSplit, 1];
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs0, bp, edgeTree[edgeIdx].V0) != 0)
                        return -1;
                    t.Bx += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs1, bp, edgeTree[edgeIdx].H0) != 0)
                        return -1;
                    t.By += hsz;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs1, bp, Av1EdgeFlags.None) != 0)
                        return -1;
                    t.By -= hsz;
                    t.Bx -= hsz;
                    break;
                }

                case Av1BlockPartition.Horizontal4:
                {
                    byte bs = bsizes[(int)bl, (int)Av1BlockPartition.Horizontal4, 0];
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs, bp, edgeTree[edgeIdx].H0) != 0)
                        return -1;
                    t.By += hsz >> 1;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs, bp, edgeTree[edgeIdx].H4) != 0)
                        return -1;
                    t.By += hsz >> 1;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs, bp, Av1EdgeFlags.AllLeftHasBottom) != 0)
                        return -1;
                    t.By += hsz >> 1;
                    if (t.By < ctx.Height4)
                    {
                        if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                            (Av1BlockSize)bs, bp, edgeTree[edgeIdx].H1) != 0)
                            return -1;
                    }
                    t.By -= (hsz * 3) >> 1;
                    break;
                }

                case Av1BlockPartition.Vertical4:
                {
                    byte bs = bsizes[(int)bl, (int)Av1BlockPartition.Vertical4, 0];
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs, bp, edgeTree[edgeIdx].V0) != 0)
                        return -1;
                    t.Bx += hsz >> 1;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs, bp, edgeTree[edgeIdx].V4) != 0)
                        return -1;
                    t.Bx += hsz >> 1;
                    if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                        (Av1BlockSize)bs, bp, Av1EdgeFlags.AllTopHasRight) != 0)
                        return -1;
                    t.Bx += hsz >> 1;
                    if (t.Bx < ctx.Width4)
                    {
                        if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                            (Av1BlockSize)bs, bp, edgeTree[edgeIdx].V1) != 0)
                            return -1;
                    }
                    t.Bx -= (hsz * 3) >> 1;
                    break;
                }

                default:
                    return -1;
            }
        }
        else if (haveHSplit)
        {
            // At bottom edge — only horizontal split or recursive split possible
            bool isSplit = msac.DecodeBool(GatherTopPartitionProb(partCdf, bl)) != 0;
            if (isSplit)
            {
                bp = Av1BlockPartition.Split;
                int c0 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 0);
                int c1 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 1);
                if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c0, bl + 1) != 0) return 1;
                t.Bx += hsz;
                if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c1, bl + 1) != 0) return 1;
                t.Bx -= hsz;
            }
            else
            {
                bp = Av1BlockPartition.Horizontal;
                byte bs = Av1Tables.BlockSizesPerPartition[(int)bl, (int)Av1BlockPartition.Horizontal, 0];
                if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                    (Av1BlockSize)bs, bp, edgeTree[edgeIdx].H0) != 0)
                    return -1;
            }
        }
        else
        {
            // At right edge — only vertical split or recursive split possible
            bool isSplit = msac.DecodeBool(GatherLeftPartitionProb(partCdf, bl)) != 0;
            if (isSplit)
            {
                bp = Av1BlockPartition.Split;
                int c0 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 0);
                int c2 = Av1IntraEdgeTree.GetSplitChild(edgeTree[edgeIdx], 2);
                if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c0, bl + 1) != 0) return 1;
                t.By += hsz;
                if (DecodeSuperblock(t, ref msac, ctx, edgeTree, c2, bl + 1) != 0) return 1;
                t.By -= hsz;
            }
            else
            {
                bp = Av1BlockPartition.Vertical;
                byte bs = Av1Tables.BlockSizesPerPartition[(int)bl, (int)Av1BlockPartition.Vertical, 0];
                if (DecodeBlock(t, ref msac, ctx, edgeTree, edgeIdx, bl,
                    (Av1BlockSize)bs, bp, edgeTree[edgeIdx].V0) != 0)
                    return -1;
            }
        }

        // Update above/left partition context
        if (bp != Av1BlockPartition.Split || bl == Av1BlockLevel.Bl8x8)
        {
            byte aboveVal = Av1Tables.AboveLeftPartCtx[0, (int)bl, (int)bp];
            byte leftVal = Av1Tables.AboveLeftPartCtx[1, (int)bl, (int)bp];
            int ulog = Ulog2(hsz);
            int count = 1 << ulog;

            // Clamp to avoid out-of-bounds writes
            int aboveCount = Math.Min(count, t.Above.Partition.Length - bx8);
            int leftCount = Math.Min(count, t.Left.Partition.Length - by8);

            if (aboveCount > 0)
                Av1BlockContextManaged.Fill(t.Above.Partition, bx8, aboveCount, aboveVal);
            if (leftCount > 0)
                Av1BlockContextManaged.Fill(t.Left.Partition, by8, leftCount, leftVal);
        }

        return 0;
    }

    // ========================================================================
    // Block Decode — Intra Mode Parsing
    // ========================================================================

    /// <summary>
    /// Decode a single coding block. Reads segmentation, skip, intra/inter decision,
    /// intra mode parameters (Y mode, UV mode, angle delta, CFL, palette, filter intra,
    /// tx size), and updates above/left context arrays.
    /// Maps to dav1d decode_b (decode.c) — intra path only for now.
    /// </summary>
    /// <returns>0 on success, -1 on error.</returns>
    private static bool DbgFirstBlock = true;

    /// <summary>Diagnostics: when set, every non-key-frame block writes "B by bx bs r=rng" (as a patched dav1d does).</summary>
    [ThreadStatic] internal static System.IO.TextWriter? BlockTrace;

    public static int DecodeBlock(
        Av1TaskContext t,
        ref Av1Msac msac,
        Av1DecoderContext ctx,
        Av1EdgeNode[] edgeTree,
        int edgeIdx,
        Av1BlockLevel bl,
        Av1BlockSize bs,
        Av1BlockPartition bp,
        Av1EdgeFlags intraEdgeFlags)
    {
        var ts = t.TileState!;
        var fh = ctx.FrameHeader!;
        if (BlockTrace != null && fh.FrameType != Av1FrameType.Key)
            BlockTrace.WriteLine($"B {t.By} {t.Bx} {(int)bs} r={msac.DebugRng}");
        var seqHdr = ctx.SequenceHeader!;
        int bx4 = t.Bx & 31;
        int by4 = t.By & 31;
        int ssVer = fh.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = fh.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int cbx4 = bx4 >> ssHor;
        int cby4 = by4 >> ssVer;

        int bw4 = Av1Tables.BlockDimensions[(int)bs, 0];
        int bh4 = Av1Tables.BlockDimensions[(int)bs, 1];
        int w4 = Math.Min(bw4, ctx.Width4 - t.Bx);
        int h4 = Math.Min(bh4, ctx.Height4 - t.By);
        int cbw4 = (bw4 + ssHor) >> ssHor;
        int cbh4 = (bh4 + ssVer) >> ssVer;
        bool haveLeft = t.Bx > ts.ColStart;
        bool haveTop = t.By > ts.RowStart;
        bool hasChroma = fh.PixelLayout != Av1PixelLayout.I400 &&
                        (bw4 > ssHor || (t.Bx & 1) != 0) &&
                        (bh4 > ssVer || (t.By & 1) != 0);

        // Block storage
        Av1Block b = default;
        b.BlockLevel = (byte)bl;
        b.Partition = (byte)bp;
        b.BlockSize = (byte)bs;

        // === Segment ID, before skip (dav1d decode_b: no map update, or preskip features) ===
        b.SegId = 0;
        b.SegPred = 0;
        if (fh.SegmentationEnabled)
        {
            if (!fh.SegmentationUpdateMap)
            {
                b.SegId = ctx.PrevSegMap != null ? PrevFrameSegId(ctx, t.By, t.Bx, w4, h4) : (byte)0;
            }
            else if (fh.SegmentationData.Preskip != 0)
            {
                if (fh.SegmentationTemporal &&
                    msac.DecodeBoolAdapt(ts.Cdf.Mode.SegPred[t.Above.SegPred[bx4] + t.Left.SegPred[by4]]) != 0)
                {
                    b.SegPred = 1;
                    b.SegId = ctx.PrevSegMap != null ? PrevFrameSegId(ctx, t.By, t.Bx, w4, h4) : (byte)0;
                }
                else
                {
                    b.SegId = ReadSegId(ref msac, ts, ctx, fh, t.By, t.Bx, haveTop, haveLeft);
                }
            }
        }

        // === Skip Mode ===
        b.SkipMode = 0;
        var segF = fh.SegmentationEnabled ? fh.SegmentationData.Segments[b.SegId] : default;
        bool segOnF = fh.SegmentationEnabled;
        if ((!segOnF || (segF.GlobalMv == 0 && segF.Ref == -1 && segF.Skip == 0)) &&
            fh.SkipModeEnabled && Math.Min(bw4, bh4) > 1)
        {
            int smctx = t.Above.SkipMode[bx4] + t.Left.SkipMode[by4];
            b.SkipMode = (byte)(msac.DecodeBoolAdapt(ts.Cdf.Mode.SkipMode[smctx]) != 0 ? 1 : 0);
            if (t.Bx == 0 && t.By == 0 && fh.FrameType != Av1FrameType.Key)
                AvDbg.W($"[BLK-SKIPMODE] skip_mode={b.SkipMode} smctx={smctx} cdf0={ts.Cdf.Mode.SkipMode[smctx][0]} rng={msac.DebugRng:X4}");
        }

        // === Skip ===
        // dav1d: skip_mode blocks are always skipped (no MSAC read)
        if (b.SkipMode != 0 || (segOnF && segF.Skip != 0))
        {
            b.Skip = 1;
        }
        else if (fh.IsInterOrSwitch)
        {
            // Inter frames: read skip from bitstream
            int sctx = t.Above.Skip[bx4] + t.Left.Skip[by4];
            if (t.Bx == 0 && t.By == 0 && fh.FrameType != Av1FrameType.Key) {
                AvDbg.W($"[BLK-SKIP] pre rng={msac.DebugRng:X4}({msac.DebugRng}) dif_lo={(uint)msac.DebugDif:X8} cnt={msac.Cnt} sctx={sctx}");
            }
            b.Skip = (byte)(msac.DecodeBoolAdapt(ts.Cdf.GetSkipCdf(sctx)) != 0 ? 1 : 0);
            if (t.Bx == 0 && t.By == 0 && fh.FrameType != Av1FrameType.Key) {
                AvDbg.W($"[BLK-SKIP] post rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} dif_hi={(uint)(msac.DebugDif>>32):X8} cnt={msac.Cnt} skip={b.Skip} cdf0_post={ts.Cdf.GetSkipCdf(sctx)[0]} cdf1_post={ts.Cdf.GetSkipCdf(sctx)[1]}");
            }
        }
        else
        {
            // Key/intra frames: read skip
            int sctx = t.Above.Skip[bx4] + t.Left.Skip[by4];
            Av1Msac.TraceLabel = "b-skip";
            Av1Msac.Phase = 2;
            b.Skip = (byte)(msac.DecodeBoolAdapt(ts.Cdf.GetSkipCdf(sctx)) != 0 ? 1 : 0);
            Av1Msac.TraceLabel = null;
            if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                AvDbg.W($"[DBG-BLK] Post-skip[{b.Skip}]: dif={msac.DebugDif:X16} rng={msac.DebugRng:X4} sctx={sctx}");
        }

        // === Segment ID, after skip (map update without preskip features) ===
        if (fh.SegmentationEnabled && fh.SegmentationUpdateMap && fh.SegmentationData.Preskip == 0)
        {
            if (b.Skip == 0 && fh.SegmentationTemporal &&
                msac.DecodeBoolAdapt(ts.Cdf.Mode.SegPred[t.Above.SegPred[bx4] + t.Left.SegPred[by4]]) != 0)
            {
                b.SegPred = 1;
                b.SegId = ctx.PrevSegMap != null ? PrevFrameSegId(ctx, t.By, t.Bx, w4, h4) : (byte)0;
            }
            else if (b.Skip != 0)
            {
                b.SegId = CurFrameSegIdPred(ctx, t.By, t.Bx, haveTop, haveLeft, out _);
            }
            else
            {
                b.SegId = ReadSegId(ref msac, ts, ctx, fh, t.By, t.Bx, haveTop, haveLeft);
            }
            segF = fh.SegmentationData.Segments[b.SegId];
        }

        // === CDEF index ===
        if (!Convert.ToBoolean(b.Skip))
        {
            // Sub-index of this 64x64 within its SB128 lf-mask. For 64x64 superblocks (Sb128=0) two SB64
            // columns/rows still share one SB128 mask, so the position must be encoded here (not forced to 0),
            // matching the apply-side sb64_idx = ((by&16)>>3) + (sbx&1).
            int idx = ((t.By & 16) >> 3) + ((t.Bx & 16) >> 4);
            if (t.CurSbCdefIdx[idx] == -1)
            {
                Av1Msac.TraceLabel = "cdef";
                int v = (int)msac.DecodeBools(fh.CdefBits);
                Av1Msac.TraceLabel = null;
                t.CurSbCdefIdx[idx] = (sbyte)v;
                if (t.Bx == 0 && t.By == 0 && fh.FrameType != Av1FrameType.Key)
                    AvDbg.W($"[BLK-CDEF] post rng={msac.DebugRng:X4} cnt={msac.Cnt} val={v}");
                if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                    AvDbg.W($"[DBG-BLK] Post-cdef_idx[{v}]: dif={msac.DebugDif:X16} rng={msac.DebugRng:X4} bits={fh.CdefBits}");
                if (bw4 > 16) t.CurSbCdefIdx[idx + 1] = (sbyte)v;
                if (bh4 > 16) t.CurSbCdefIdx[idx + 2] = (sbyte)v;
                if (bw4 == 32 && bh4 == 32) t.CurSbCdefIdx[idx + 3] = (sbyte)v;
            }
        }

        // === Delta-Q / Delta-LF ===
        if (((t.Bx | t.By) & (31 >> (seqHdr.Sb128 ? 0 : 1))) == 0)
        {
            if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                AvDbg.W($"[DBG-BLK] Pre-deltaQ: rng={msac.DebugRng:X4} DeltaQPresent={fh.DeltaQPresent} DeltaLfPresent={fh.DeltaLfPresent}");
            if (fh.DeltaQPresent &&
                (bs != (seqHdr.Sb128 ? Av1BlockSize.Bs128x128 : Av1BlockSize.Bs64x64) || b.Skip == 0))
            {
                Av1Msac.TraceLabel = "dQ-sym";
                int deltaQ = (int)msac.DecodeSymbolAdapt4(ts.Cdf.GetDeltaQCdf(), 3);
                if (deltaQ == 3)
                {
                    Av1Msac.TraceLabel = "dQ-ext";
                    int nBits = 1 + (int)msac.DecodeBools(3);
                    deltaQ = (int)msac.DecodeBools(nBits) + 1 + (1 << nBits);
                }
                if (deltaQ != 0)
                {
                    Av1Msac.TraceLabel = "dQ-sgn";
                    if (msac.DecodeBoolEqui() != 0) deltaQ = -deltaQ;
                    deltaQ *= 1 << fh.DeltaQResLog2;
                }
                Av1Msac.TraceLabel = null;
                int prevQIdx = ts.LastQIdx;
                ts.LastQIdx = Math.Clamp(ts.LastQIdx + deltaQ, 1, 255);
                // dav1d decode.c: a changed qindex re-derives this superblock's dequantizers (init_quant_tables).
                if (ts.LastQIdx != prevQIdx)
                    Av1Decoder.FillDequant(ts, fh, ts.LastQIdx, ctx.BitDepth);

                // Delta LF: per-superblock loop-filter level deltas (dav1d decode.c).
                Span<int> prevDeltaLf = stackalloc int[4];
                ts.LastDeltaLf.AsSpan().CopyTo(prevDeltaLf);
                if (fh.DeltaLfPresent)
                {
                    int nLfs = fh.DeltaLfMulti
                        ? (fh.PixelLayout != Av1PixelLayout.I400 ? 4 : 2)
                        : 1;
                    for (int i = 0; i < nLfs; i++)
                    {
                        Av1Msac.TraceLabel = "dLF";
                        int idx = i + (fh.DeltaLfMulti ? 1 : 0);
                        int deltaLf = (int)msac.DecodeSymbolAdapt4(ts.Cdf.GetDeltaLfCdf(idx), 3);
                        if (deltaLf == 3)
                        {
                            Av1Msac.TraceLabel = "dLF-ext";
                            int nBits = 1 + (int)msac.DecodeBools(3);
                            deltaLf = (int)msac.DecodeBools(nBits) + 1 + (1 << nBits);
                        }
                        if (deltaLf != 0)
                        {
                            Av1Msac.TraceLabel = "dLF-sgn";
                            if (msac.DecodeBoolEqui() != 0) deltaLf = -deltaLf;
                            deltaLf *= 1 << fh.DeltaLfResLog2;
                        }
                        Av1Msac.TraceLabel = null;
                        ts.LastDeltaLf[i] = Math.Clamp(ts.LastDeltaLf[i] + deltaLf, -63, 63);
                    }
                }

                // All-zero deltas use the frame-wide levels; a change re-derives this superblock's levels
                // (dav1d: ts->lflvl = f->lf.lvl, else dav1d_calc_lf_values(ts->lflvlmem, hdr, last_delta_lf)).
                var dl = ts.LastDeltaLf;
                if (dl[0] == 0 && dl[1] == 0 && dl[2] == 0 && dl[3] == 0)
                    Array.Copy(ctx.LfLvl, ts.LfLvl, ctx.LfLvl.Length);
                else if (!prevDeltaLf.SequenceEqual(dl))
                    Av1LoopFilter.CalcLfValues(ts.LfLvl, fh, stackalloc sbyte[] { (sbyte)dl[0], (sbyte)dl[1], (sbyte)dl[2], (sbyte)dl[3] });
            }
        }

        // === Intra/Inter Decision ===
        // dav1d: skip_mode blocks are always inter (no MSAC read)
        if (b.SkipMode != 0)
        {
            b.Intra = 0;
        }
        else if (fh.IsInterOrSwitch && segOnF && (segF.Ref >= 0 || segF.GlobalMv != 0))
        {
            b.Intra = (byte)(segF.Ref == 0 ? 1 : 0);
        }
        else if (fh.IsInterOrSwitch)
        {
            int ictx = GetIntraCtx(t.Above, t.Left, by4, bx4, haveTop, haveLeft);
            if (t.Bx == 0 && t.By == 0)
                AvDbg.W($"[BLK-INTRA] pre rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} cnt={msac.Cnt} ictx={ictx}");
            b.Intra = (byte)(msac.DecodeBoolAdapt(ts.Cdf.GetIntraCdf(ictx)) != 0 ? 0 : 1);
            if (t.Bx == 0 && t.By == 0)
                AvDbg.W($"[BLK-INTRA] post rng={msac.DebugRng:X4} cnt={msac.Cnt} intra={b.Intra} skip={b.Skip} bs={(int)bs} bx4={bx4} by4={by4}");
        }
        else if (fh.AllowIntraBc)
        {
            b.Intra = (byte)(msac.DecodeBoolAdapt(ts.Cdf.Mode.Intrabc) != 0 ? 0 : 1);
            if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                AvDbg.W($"[DBG-BLK] Post-intrabc[intra={b.Intra}]: rng={msac.DebugRng:X4}");
        }
        else
        {
            b.Intra = 1; // Key/intra-only frames are always intra
        }

        // === Intra Block ===
        if (b.Intra != 0)
        {
            DecodeBlockIntra(t, ref msac, ctx, ref b, bl, bs, bx4, by4, bw4, bh4, w4, h4,
                cbx4, cby4, cbw4, cbh4, ssHor, ssVer,
                haveLeft, haveTop, hasChroma, intraEdgeFlags);

            // === Reconstruction ===
            // Call intra reconstruction which does: prediction + coefficient decode + IDCT + residual add
            if (ctx.CurrentPlanes[0] != null)
            {
                Span<ushort> yPlane = ctx.CurrentPlanes[0].AsSpan();

                Span<ushort> uPlane = default;
                Span<ushort> vPlane = default;
                if (hasChroma && ctx.CurrentPlanes[1] != null && ctx.CurrentPlanes[2] != null)
                {
                    uPlane = ctx.CurrentPlanes[1].AsSpan();
                    vPlane = ctx.CurrentPlanes[2].AsSpan();
                }

                BlockTrace?.WriteLine($"I {t.By} {t.Bx} y{b.YMode} uv{b.UvMode} a{b.YAngle},{b.UvAngle} tx{b.Tx} uvtx{b.UvTx} pal{b.PalSzY},{b.PalSzUv} cfl{b.CflAlpha0},{b.CflAlpha1} fi{(b.YMode == 13 ? 1 : 0)},{(b.YMode == 13 ? b.YAngle : 0)} r={msac.DebugRng}");
                if (t.Bx == 0 && t.By == 0)
                    AvDbg.W($"[RECON-DBG] Calling ReconBlockIntra for bx=0 by=0, yPlane.Length={yPlane.Length}");

                Av1Reconstruction.ReconBlockIntra(
                    t, ref msac, ctx, bs, intraEdgeFlags, ref b,
                    yPlane, ctx.YStride,
                    uPlane, vPlane, ctx.UvStride);
                if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                {
                    AvDbg.W($"[DBG-BLK] Post-recon(0,0): rng={msac.DebugRng:X4} dif={msac.DebugDif:X16}");
                    // Check if any pixels were written
                    bool anyNonZero = false;
                    for (int i = 0; i < 64; i++)
                    {
                        if (yPlane[i] != 0) { anyNonZero = true; break; }
                    }
                    AvDbg.W($"[DBG-BLK] First 64 pixels non-zero: {anyNonZero}");
                    DbgFirstBlock = false;
                }
            }

            // Create loop filter mask for this intra block
            var fh2 = ctx.FrameHeader!;
            if ((fh2.LfLevelY0 != 0 || fh2.LfLevelY1 != 0) && t.LfMask != null)
            {
                var ts2 = t.TileState!;
                Av1LoopFilter.CreateLfMaskIntra(
                    t.LfMask, ctx.LfLevel, ctx.B4Stride,
                    ts2.LfLvl, b.SegId,
                    t.Bx, t.By, ctx.W4, ctx.H4, bs,
                    b.Tx, b.UvTx, (int)fh2.PixelLayout,
                    t.Above.TxLpfY, bx4,
                    t.Left.TxLpfY, by4,
                    hasChroma ? t.Above.TxLpfUv : null,
                    hasChroma ? cbx4 : 0,
                    hasChroma ? t.Left.TxLpfUv : null,
                    hasChroma ? cby4 : 0);
            }

            // Update above/left contexts AFTER reconstruction (matches dav1d ordering)
            UpdateIntraBlockContext(t, ctx, bs, ref b, bx4, by4, cbx4, cby4, cbw4, cbh4,
                ssHor, ssVer, hasChroma, bp);

            // dav1d decode.c: splat the intra-ref marker for intra blocks on inter frames AND on
            // key/intra frames that allow intra block copy (so intraBC DV prediction sees them).
            if (ctx.FrameHeader!.IsInterOrSwitch || ctx.FrameHeader!.AllowIntraBc)
            {
                var intraTmpl = new Av1RefMvsBlock
                {
                    Ref = new Av1RefMvsRefPair { Ref0 = 0, Ref1 = -1 },
                    Mv = new Av1RefMvsMvPair { Mv0 = new Av1MotionVector { Raw = 0x80008000 } },
                    Bs = (byte)bs,
                    Mf = 0,
                };
                Av1RefMvs.SplatMv(t.Rt.R, (t.By & 31) + 5, in intraTmpl, t.Bx, bw4, bh4);
            }
        }
        else if (fh.IsIntra)
        {
            // Intra block copy (screen content on a key/intra frame): decode the displacement
            // vector + transform tree, then reconstruct via the self-reference copy path.
            DecodeIntraBc(t, ref msac, ctx, ref b, bs, bx4, by4, bw4, bh4,
                hasChroma, ssHor, ssVer, intraEdgeFlags);

            if (ctx.CurrentPlanes[0] != null)
            {
                var yPlane = ctx.CurrentPlanes[0]!;
                var uPlane = hasChroma ? ctx.CurrentPlanes[1]! : Array.Empty<ushort>();
                var vPlane = hasChroma ? ctx.CurrentPlanes[2]! : Array.Empty<ushort>();
                Av1Reconstruction.ReconBlockInter(t, ref msac, ctx, (int)bs, ref b,
                    yPlane, ctx.YStride, uPlane, vPlane, ctx.UvStride);
            }

            // Splat this block's DV into the refmvs grid (dav1d splat_intrabc_mv) so later
            // intraBC blocks can predict from it.
            var ibcTmpl = new Av1RefMvsBlock
            {
                Ref = new Av1RefMvsRefPair { Ref0 = 0, Ref1 = -1 },
                Mv = new Av1RefMvsMvPair { Mv0 = b.Mv0 },
                Bs = (byte)bs,
                Mf = 0,
            };
            Av1RefMvs.SplatMv(t.Rt.R, (t.By & 31) + 5, in ibcTmpl, t.Bx, bw4, bh4);

            UpdateIntraBcContext(t, bs, ref b, bx4, by4, cbx4, cby4, cbw4, cbh4, bw4, bh4, hasChroma, bp);
        }
        else
        {
            // Inter block — decode inter-specific syntax
            DecodeBlockInter(t, ref msac, ctx, ref b, bl, bs, bx4, by4, bw4, bh4, w4, h4,
                cbx4, cby4, cbw4, cbh4, ssHor, ssVer,
                haveLeft, haveTop, hasChroma, intraEdgeFlags);

            // Reconstruct inter block
            BlockTrace?.WriteLine($"M {t.By} {t.Bx} r{b.Ref0},{(sbyte)b.Ref1} mv{b.Mv0.Y},{b.Mv0.X}/{b.Mv1.Y},{b.Mv1.X} mm{b.Motion} f{b.Filter} c{b.CompType}");
            var yPlane = ctx.CurrentPlanes[0]!;
            var uPlane = hasChroma ? ctx.CurrentPlanes[1]! : Array.Empty<ushort>();
            var vPlane = hasChroma ? ctx.CurrentPlanes[2]! : Array.Empty<ushort>();
            Av1Reconstruction.ReconBlockInter(t, ref msac, ctx, (int)bs, ref b,
                yPlane, ctx.YStride, uPlane, vPlane, ctx.UvStride);

            // Create loop filter mask for this inter block (dav1d decode.c:1928-1947)
            var fhI = ctx.FrameHeader!;
            if ((fhI.LfLevelY0 != 0 || fhI.LfLevelY1 != 0) && t.LfMask != null)
            {
                var tsI = t.TileState!;
                bool isComp = b.CompType != (byte)Av1CompInterType.None;
                bool isGlobalMv = isComp
                    ? b.InterMode == (byte)Av1CompInterPredMode.GlobalGlobal
                    : b.InterMode == (byte)Av1InterPredMode.GlobalMv;
                int refIdx = b.Ref0 + 1;
                int modeIdx = isGlobalMv ? 0 : 1;
                int ytx = b.MaxYTx, uvtx = b.UvTx;
                if (fhI.SegmentationLossless[b.SegId])
                {
                    ytx = (int)Av1TxSize.Tx4x4;
                    uvtx = (int)Av1TxSize.Tx4x4;
                }
                ushort[] txMasks = { b.TxSplit0, b.TxSplit1 };
                Av1LoopFilter.CreateLfMaskInter(
                    t.LfMask, ctx.LfLevel, ctx.B4Stride,
                    tsI.LfLvl, b.SegId, refIdx, modeIdx,
                    t.Bx, t.By, ctx.W4, ctx.H4,
                    b.Skip != 0, bs, ytx, txMasks, uvtx,
                    (Av1PixelLayout)fhI.PixelLayout,
                    new Span<byte>(t.Above.TxLpfY, bx4, 32 - bx4),
                    new Span<byte>(t.Left.TxLpfY, by4, 32 - by4),
                    hasChroma ? new Span<byte>(t.Above.TxLpfUv, cbx4, 32 - cbx4) : default,
                    hasChroma ? new Span<byte>(t.Left.TxLpfUv, cby4, 32 - cby4) : default,
                    hasChroma);
            }

            // Update above/left contexts for inter block
            for (int i = 0; i < bw4 >> 1; i++) t.Above.Partition[(bx4 >> 1) + i] = (byte)bp;
            for (int j = 0; j < bh4 >> 1; j++) t.Left.Partition[(by4 >> 1) + j] = (byte)bp;
            byte interModeByte = b.InterMode;
            byte compTypeByte = (byte)b.CompType;
            sbyte ref0Byte = b.Ref0 < 0 ? (sbyte)(-1) : b.Ref0;
            sbyte ref1Byte = b.Ref1 < 0 ? (sbyte)(-1) : b.Ref1;
            for (int i = 0; i < bw4; i++) t.Above.Mode[bx4 + i] = interModeByte;
            for (int i = 0; i < bw4; i++) t.Above.CompType[bx4 + i] = compTypeByte;
            for (int i = 0; i < bw4; i++) t.Above.Ref0[bx4 + i] = ref0Byte;
            for (int i = 0; i < bw4; i++) t.Above.Ref1[bx4 + i] = ref1Byte;
            for (int i = 0; i < bw4; i++) t.Above.Filter0[bx4 + i] = Av1Tables.FilterDir[b.Filter, 0];
            for (int i = 0; i < bw4; i++) t.Above.Filter1[bx4 + i] = Av1Tables.FilterDir[b.Filter, 1];
            for (int i = 0; i < bw4; i++) t.Above.Intra[bx4 + i] = 0;
            for (int i = 0; i < bw4; i++) t.Above.Skip[bx4 + i] = b.Skip;
            for (int i = 0; i < bw4; i++) t.Above.SkipMode[bx4 + i] = b.SkipMode;
            for (int i = 0; i < bw4; i++) t.Above.SegPred[bx4 + i] = b.SegPred;
            for (int i = 0; i < bw4; i++) t.Above.PalSz[bx4 + i] = 0;
            for (int i = 0; i < bw4; i++) t.PalSzUv[0, bx4 + i] = 0;   // dav1d t->pal_sz_uv (palette cache)
            for (int i = 0; i < bw4; i++) t.Above.TxIntra[bx4 + i] = (sbyte)Av1Tables.BlockDimensions[(int)bs, 2];
            for (int j = 0; j < bh4; j++) t.Left.Mode[by4 + j] = interModeByte;
            for (int j = 0; j < bh4; j++) t.Left.CompType[by4 + j] = compTypeByte;
            for (int j = 0; j < bh4; j++) t.Left.Ref0[by4 + j] = ref0Byte;
            for (int j = 0; j < bh4; j++) t.Left.Ref1[by4 + j] = ref1Byte;
            for (int j = 0; j < bh4; j++) t.Left.Filter0[by4 + j] = Av1Tables.FilterDir[b.Filter, 0];
            for (int j = 0; j < bh4; j++) t.Left.Filter1[by4 + j] = Av1Tables.FilterDir[b.Filter, 1];
            for (int j = 0; j < bh4; j++) t.Left.Intra[by4 + j] = 0;
            for (int j = 0; j < bh4; j++) t.Left.Skip[by4 + j] = b.Skip;
            for (int j = 0; j < bh4; j++) t.Left.SkipMode[by4 + j] = b.SkipMode;
            for (int j = 0; j < bh4; j++) t.Left.SegPred[by4 + j] = b.SegPred;
            for (int j = 0; j < bh4; j++) t.Left.PalSz[by4 + j] = 0;
            for (int j = 0; j < bh4; j++) t.PalSzUv[1, by4 + j] = 0;
            for (int j = 0; j < bh4; j++) t.Left.TxIntra[by4 + j] = (sbyte)Av1Tables.BlockDimensions[(int)bs, 3];
            if (hasChroma)
            {
                // dav1d decode_b: inter blocks leave DC_PRED as the neighbouring uv mode (the chroma intra-edge
                // filter's smooth-neighbour test reads it).
                for (int i = 0; i < cbw4; i++) t.Above.UvMode[cbx4 + i] = (byte)Av1IntraPredMode.Dc;
                for (int j = 0; j < cbh4; j++) t.Left.UvMode[cby4 + j] = (byte)Av1IntraPredMode.Dc;
            }

            // Update refmvs spatial grid for inter blocks (dav1d: splat_oneref_mv + edge updates)
            // dav1d: ref[1] = interintra_type ? 0 : -1 (interintra blocks excluded from warp candidates)
            var tmpl = b.CompType != (byte)Av1CompInterType.None
                ? new Av1RefMvsBlock   // dav1d splat_tworef_mv
                {
                    Ref = new Av1RefMvsRefPair { Ref0 = (sbyte)(b.Ref0 + 1), Ref1 = (sbyte)(b.Ref1 + 1) },
                    Mv = new Av1RefMvsMvPair { Mv0 = b.Mv0, Mv1 = b.Mv1 },
                    Bs = (byte)bs,
                    // GLOBALMV_GLOBALMV, and any mode with a NEWMV half (mask 0xbc)
                    Mf = (byte)((b.InterMode == (byte)Av1CompInterPredMode.GlobalGlobal ? 1 : 0) |
                                (((1 << b.InterMode) & 0xbc) != 0 ? 2 : 0)),
                }
                : new Av1RefMvsBlock   // dav1d splat_oneref_mv
                {
                    Ref = new Av1RefMvsRefPair { Ref0 = (sbyte)(b.Ref0 + 1), Ref1 = b.InterIntraTypeField != 0 ? (sbyte)0 : (sbyte)(-1) },
                    Mv = new Av1RefMvsMvPair { Mv0 = b.Mv0 },
                    Bs = (byte)bs,
                    Mf = (byte)((b.InterMode == (byte)Av1InterPredMode.GlobalMv && Math.Min(bw4, bh4) >= 2 ? 1 : 0) |
                                ((b.InterMode == (byte)Av1InterPredMode.NewMv) ? 2 : 0)),
                };
            int r0 = (t.By & 31) + 5;
            Av1RefMvs.SplatMv(t.Rt.R, r0, in tmpl, t.Bx, bw4, bh4);

            // Bottom-row edge update (for blocks below)
            var bottomRow = t.Rt.R[r0 + bh4 - 1];
            if (bottomRow != null)
                for (int x = 0; x < bw4; x++)
                    bottomRow[t.Bx + x] = tmpl;
            // Right-column edge update (for blocks to the right)
            for (int y = 0; y < bh4 - 1; y++)
            {
                var rrow = t.Rt.R[r0 + y];
                if (rrow != null)
                    rrow[t.Bx + bw4 - 1] = tmpl;
            }
        }

        // This block's segment id into the frame's segment map (dav1d: over the whole bw4 x bh4 block).
        if (fh.SegmentationEnabled && fh.SegmentationUpdateMap && ctx.CurSegMap != null)
            for (int y = 0; y < bh4; y++)
                ctx.CurSegMap.AsSpan((t.By + y) * ctx.B4Stride + t.Bx, bw4).Fill(b.SegId);

        // Fill CDEF noskip_mask for non-skip blocks (dav1d decode.c:1993-1999).
        // Common to intra and inter blocks — gates which 8x8 blocks CDEF filters. The mask covers one SB128
        // (16 8x8-row-pairs x 2 64px halves), so the row index is taken relative to the SB128 row (by4 & 31).
        if (b.Skip == 0 && t.LfMask != null)
        {
            uint mask = (0xFFFFFFFFu >> (32 - bw4)) << (bx4 & 15);
            int bxIdx = (bx4 & 16) >> 4;
            for (int y = 0; y < bh4; y += 2)
            {
                int maskRow = ((by4 + y) & 31) >> 1;
                if (maskRow < 16)
                {
                    t.LfMask.NoskipMask[maskRow, bxIdx] |= (ushort)mask;
                    if (bw4 == 32)
                        t.LfMask.NoskipMask[maskRow, 1] |= (ushort)mask;
                }
            }
        }

        // PER-BLOCK DIAGNOSTIC for frame 1 (MediaKernel comparison with dav1d)
        if (fh.FrameOffset == 1 && t.Bx < 32 && t.By < 32) {
            AvDbg.W($"[OUR-BLK] bx={t.Bx} by={t.By} bs={(int)bs} bw4={bw4} bh4={bh4} w4={w4} h4={h4} skip={b.Skip} intra={b.Intra}" +
                (b.Intra != 0
                    ? $" y_mode={b.YMode} uv_mode={b.UvMode} tx={b.Tx} uvtx={b.UvTx}"
                    : $" inter_mode={b.InterMode} comp_type={b.CompType} motion={b.Motion} ref0={b.Ref0} ref1={b.Ref1} mv0=({b.Mv0.Y},{b.Mv0.X}) mv1=({b.Mv1.Y},{b.Mv1.X})") +
                $" msac_rng={msac.DebugRng:X4} msac_dif_lo={(uint)msac.DebugDif:X8} msac_cnt={msac.Cnt}");
        }

        return 0;
    }

    /// <summary>
    /// Decode intra-specific block syntax: Y mode, UV mode, angle deltas,
    /// CFL parameters, palette, filter intra, and transform size.
    /// Context update is deferred to UpdateIntraBlockContext (called after reconstruction).
    /// </summary>
    private static void DecodeBlockIntra(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx, ref Av1Block b,
        Av1BlockLevel bl, Av1BlockSize bs,
        int bx4, int by4, int bw4, int bh4, int w4, int h4,
        int cbx4, int cby4, int cbw4, int cbh4, int ssHor, int ssVer,
        bool haveLeft, bool haveTop, bool hasChroma,
        Av1EdgeFlags intraEdgeFlags)
    {
        var ts = t.TileState!;
        var fh = ctx.FrameHeader!;
        var seqHdr = ctx.SequenceHeader!;

        int bDimW = Av1Tables.BlockDimensions[(int)bs, 2]; // log2 width
        int bDimH = Av1Tables.BlockDimensions[(int)bs, 3]; // log2 height

        // === Y luma mode ===
        if (t.Bx < 8 && t.By < 4)
            AvDbg.W($"[DEC-DBG] pre-ymode bs={(int)bs} bx={t.Bx} by={t.By} rng={msac.DebugRng}");
        Span<ushort> ymodeCdf;
        if (fh.IsInterOrSwitch)
        {
            ymodeCdf = ts.Cdf.GetYModeCdf(Av1Tables.YmodeSizeContext[(int)bs]);
        }
        else
        {
            // Keyframe: context from above and left neighbor modes
            int aboveCtx = Av1Tables.IntraModeContext[t.Above.Mode[bx4]];
            int leftCtx = Av1Tables.IntraModeContext[t.Left.Mode[by4]];
            ymodeCdf = ts.Cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        }
        Av1Msac.Phase = 1;
        b.YMode = (byte)msac.DecodeSymbolAdapt16(ymodeCdf, Av1Constants.NumIntraPredModes - 1);

        // === Angle delta ===
        if (bDimW + bDimH >= 2 &&
            b.YMode >= (byte)Av1IntraPredMode.Vertical &&
            b.YMode <= (byte)Av1IntraPredMode.VerticalLeft)
        {
            var acdf = ts.Cdf.GetAngleDeltaCdf(b.YMode - (int)Av1IntraPredMode.Vertical);
            int angle = (int)msac.DecodeSymbolAdapt8(acdf, 6);
            b.YAngle = (sbyte)(angle - 3);
        }
        else
        {
            b.YAngle = 0;
        }

        // === UV chroma mode ===
        if (hasChroma)
        {
            bool cflAllowed = fh.IsLossless(b.SegId)
                ? (cbw4 == 1 && cbh4 == 1)
                : ((Av1Tables.CflAllowedMask >> (int)bs) & 1) != 0;

            var uvmodeCdf = ts.Cdf.GetUvModeCdf(cflAllowed, b.YMode);
            int maxSym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);
            b.UvMode = (byte)msac.DecodeSymbolAdapt16(uvmodeCdf, maxSym);
            if (t.Bx < 8 && t.By < 4)
                AvDbg.W($"[DEC-DBG] post-uvmode={b.UvMode} cfl={(cflAllowed ? 1 : 0)} has_chroma={(hasChroma ? 1 : 0)} rng={msac.DebugRng}");
            if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                AvDbg.W($"[DBG-BLK] Post-uvmode[{b.UvMode}={((Av1IntraPredMode)b.UvMode)}]: dif={msac.DebugDif:X16} rng={msac.DebugRng:X4} cflAllowed={cflAllowed}");

            b.UvAngle = 0;
            if (b.UvMode == (byte)Av1IntraPredMode.ChromaFromLuma)
            {
                // CFL alpha parameters
                int sign = (int)msac.DecodeSymbolAdapt8(ts.Cdf.GetCflSignCdf(), 7) + 1;
                int signU = sign * 0x56 >> 8;
                int signV = sign - signU * 3;
                if (t.Bx < 8 && t.By < 4)
                    AvDbg.W($"[DEC-DBG] CFL sign={sign} signU={signU} signV={signV} rng={msac.DebugRng}");
                if (signU != 0)
                {
                    int cflCtx = (signU == 2 ? 3 : 0) + signV;
                    var cflCdf = ts.Cdf.GetCflAlphaCdf(cflCtx);
                    if (t.Bx == 2 && t.By == 0)
                    {
                        AvDbg.W($"[DEC-DBG] CFL alphaU CDF ctx={cflCtx} pre-decode:");
                        for (int ci = 0; ci < 16; ci++) AvDbg.W($" {cflCdf[ci]}");
                        AvDbg.W($" rng={msac.DebugRng} dif={msac.DebugDif}");
                    }
                    b.CflAlpha0 = (sbyte)((int)msac.DecodeSymbolAdapt16(cflCdf, 15) + 1);
                    if (signU == 1) b.CflAlpha0 = (sbyte)(-b.CflAlpha0);
                    if (t.Bx < 8 && t.By < 4)
                        AvDbg.W($"[DEC-DBG] CFL alphaU ctx={cflCtx} val={b.CflAlpha0} rng={msac.DebugRng}");
                }
                else
                {
                    b.CflAlpha0 = 0;
                }
                if (signV != 0)
                {
                    int cflCtx = (signV == 2 ? 3 : 0) + signU;
                    b.CflAlpha1 = (sbyte)((int)msac.DecodeSymbolAdapt16(
                        ts.Cdf.GetCflAlphaCdf(cflCtx), 15) + 1);
                    if (signV == 1) b.CflAlpha1 = (sbyte)(-b.CflAlpha1);
                    if (t.Bx < 8 && t.By < 4)
                        AvDbg.W($"[DEC-DBG] CFL alphaV ctx={cflCtx} val={b.CflAlpha1} rng={msac.DebugRng}");
                }
                else
                {
                    b.CflAlpha1 = 0;
                }
            }
            else if (bDimW + bDimH >= 2 &&
                     b.UvMode >= (byte)Av1IntraPredMode.Vertical &&
                     b.UvMode <= (byte)Av1IntraPredMode.VerticalLeft)
            {
                var acdf = ts.Cdf.GetAngleDeltaCdf(b.UvMode - (int)Av1IntraPredMode.Vertical);
                int angle = (int)msac.DecodeSymbolAdapt8(acdf, 6);
                b.UvAngle = (sbyte)(angle - 3);
            }
        }

        // === Palette ===
        b.PalSzY = 0;
        b.PalSzUv = 0;
        if (fh.AllowScreenContentTools && Math.Max(bw4, bh4) <= 16 && bw4 + bh4 >= 4)
        {
            int szCtx = bDimW + bDimH - 2;
            if (b.YMode == (byte)Av1IntraPredMode.Dc)
            {
                int palCtx = (t.Above.PalSz[bx4] > 0 ? 1 : 0) + (t.Left.PalSz[by4] > 0 ? 1 : 0);
                bool useYPal = msac.DecodeBoolAdapt(ts.Cdf.GetPalYCdf(szCtx, palCtx)) != 0;
                if (useYPal)
                {
                    b.PalSzY = (byte)(2 + (int)msac.DecodeSymbolAdapt8(
                        ts.Cdf.GetPalSzCdf(0, szCtx), 6));
                    if (Av1CoeffDecode.DbgReconCount <= 3)
                        AvDbg.W($"[PAL-DBG] Y palette szCtx={szCtx} palCtx={palCtx} palSz={b.PalSzY} bx={t.Bx} by={t.By}");
                    // Decode palette colors with neighbor prediction
                    // Note: palette indices are decoded AFTER UV palette, per AV1 spec ordering
                    Av1CoeffDecode.DecodeLumaPalette(ref msac, ts.Cdf.Mode, t, ref b,
                        szCtx, bx4, by4, ctx.BitDepth);
                }
            }
            if (hasChroma && b.UvMode == (byte)Av1IntraPredMode.Dc)
            {
                int palCtx = b.PalSzY > 0 ? 1 : 0;
                bool useUvPal = msac.DecodeBoolAdapt(ts.Cdf.GetPalUvCdf(palCtx)) != 0;
                if (useUvPal)
                {
                    b.PalSzUv = (byte)(2 + (int)msac.DecodeSymbolAdapt8(
                        ts.Cdf.GetPalSzCdf(1, szCtx), 6));
                    // Decode chroma palette colors
                    Av1CoeffDecode.DecodeChromaPalette(ref msac, ts.Cdf.Mode, t, ref b,
                        ctx.BitDepth);
                    if (Av1CoeffDecode.DbgReconCount <= 3)
                        AvDbg.W($"[PAL-DBG] UV palette palSzUv={b.PalSzUv}");
                }
            }
        }

        // === Filter intra ===
        // Decoded AFTER palette colors but BEFORE palette indices (dav1d decode_b order).
        if (b.YMode == (byte)Av1IntraPredMode.Dc && b.PalSzY == 0 &&
            Math.Max(bDimW, bDimH) <= 3 && seqHdr.FilterIntra)
        {
            Av1Msac.Phase = 4;
            bool isFilter = msac.DecodeBoolAdapt(ts.Cdf.GetFilterIntraCdf(bs)) != 0;
            if (isFilter)
            {
                b.YMode = (byte)Av1IntraPredMode.Filter;
                b.YAngle = (sbyte)msac.DecodeSymbolAdapt8(ts.Cdf.GetFilterIntraModeCdf(), 4);
            }
        }

        // === Palette indices === (after palette colors AND filter_intra, per dav1d)
        // The wavefront covers only the VISIBLE region (clipped to the frame), while the
        // index buffer stride is the full block width — dav1d read_pal_indices(w4,h4,bw4,bh4)
        // with w4/h4 = imin(bw4/bh4, frame - pos). Using the full block for edge-clipped
        // blocks over-decodes color indices and desyncs MSAC.
        int palW4 = Math.Min(bw4, ctx.Bw - t.Bx);
        int palH4 = Math.Min(bh4, ctx.Bh - t.By);
        if (b.PalSzY > 0)
        {
            Av1CoeffDecode.DecodePaletteIndices(ref msac, ts.Cdf.Mode, t,
                b.PalSzY, palW4 * 4, palH4 * 4, bw4, bh4, isLuma: true);
        }
        if (hasChroma && b.PalSzUv > 0)
        {
            // Chroma palette index map spans the CHROMA reference block (cbw4/cbh4 in 4-sample
            // units) for stride; the wavefront is the clipped visible chroma region.
            int cw4 = (palW4 + ssHor) >> ssHor;
            int ch4 = (palH4 + ssVer) >> ssVer;
            Av1CoeffDecode.DecodePaletteIndices(ref msac, ts.Cdf.Mode, t,
                b.PalSzUv, cw4 * 4, ch4 * 4, cbw4, cbh4, isLuma: false);
        }

        // === Transform size ===
        if (t.Bx < 8 && t.By < 4)
            AvDbg.W($"[DEC-DBG] pre-tx bs={(int)bs} rng={msac.DebugRng}");
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[0]; // will be reassigned
        if (fh.IsLossless(b.SegId))
        {
            b.Tx = (byte)Av1TxSize.Tx4x4;
            b.UvTx = (byte)Av1TxSize.Tx4x4;
            tDim = ref Av1Tables.TxfmDimensions[(int)Av1TxSize.Tx4x4];
        }
        else
        {
             b.Tx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, 0];
            b.UvTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, (int)fh.PixelLayout];
            tDim = ref Av1Tables.TxfmDimensions[b.Tx];
            // For intra blocks tx_depth is signalled regardless of skip: read_tx_size is called with
            // allowSelect = (!skip || !is_inter), and is_inter is false here, so allowSelect is always true.
            // (Gating this on skip==0 desynced against libaom/dav1d, which read the symbol for skip intra blocks.)
            if (fh.TxMode == Av1TxfmMode.Switchable && tDim.Max > (byte)Av1TxSize.Tx4x4)
            {
                int tctx = GetTxCtx(t.Above, t.Left, tDim, by4, bx4);
                Av1Msac.Phase = 3;
                var txCdf = ts.Cdf.GetTxSzCdf(tDim.Max - 1, tctx);
                int nSym = Math.Min((int)tDim.Max, 2);
                if (t.Bx < 8 && t.By < 4)
                    AvDbg.W($"[DEC-DBG] tx-decode initTx={b.Tx} max={tDim.Max} tctx={tctx} nSym={nSym} rng={msac.DebugRng}");
                int depth = (int)msac.DecodeSymbolAdapt4(txCdf, nSym);
                if (t.Bx < 8 && t.By < 4)
                    AvDbg.W($"[DEC-DBG] tx-decode depth={depth} rng={msac.DebugRng}");
                while (depth-- > 0)
                {
                    b.Tx = tDim.Sub;
                    tDim = ref Av1Tables.TxfmDimensions[b.Tx];
                }
                if (DbgFirstBlock && t.Bx == 0 && t.By == 0)
                    AvDbg.W($"[DBG-BLK] Post-tx: dif={msac.DebugDif:X16} rng={msac.DebugRng:X4} cnt={msac.Cnt} pos={msac.DebugPos} tx={b.Tx}");
            }
            if (t.Bx < 8 && t.By < 4)
                AvDbg.W($"[DEC-DBG] post-tx={b.Tx} uvtx={b.UvTx} maxTx={tDim.Max} switchable={(fh.TxMode == Av1TxfmMode.Switchable ? 1 : 0)} rng={msac.DebugRng}");
        }

        // Context update is deferred — called from DecodeBlock AFTER reconstruction
        // (dav1d: decode.c:1253 — context update happens after recon_b_intra)
    }

    // ========================================================================
    // Reference frame context helpers (port of dav1d env.h)
    // ========================================================================

    /// <summary>Context for single-ref reference frame selection (dav1d: av1_get_ref_ctx).</summary>
    private static int GetRefCtx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int cnt0 = 0, cnt1 = 0;

        if (haveTop && above.Intra[bx4] == 0) {
            if (above.Ref0[bx4] >= 4) cnt1++; else cnt0++;
            if (above.CompType[bx4] != 0) { if (above.Ref1[bx4] >= 4) cnt1++; else cnt0++; }
        }
        if (haveLeft && left.Intra[by4] == 0) {
            if (left.Ref0[by4] >= 4) cnt1++; else cnt0++;
            if (left.CompType[by4] != 0) { if (left.Ref1[by4] >= 4) cnt1++; else cnt0++; }
        }
        return cnt0 == cnt1 ? 1 : cnt0 < cnt1 ? 0 : 2;
    }

    /// <summary>Context for compound prediction flag (dav1d: get_comp_ctx in env.h:156).</summary>
    private static int GetCompCtx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        if (haveTop) {
            if (haveLeft) {
                if (above.CompType[bx4] != 0) {
                    if (left.CompType[by4] != 0) return 4;
                    else return 2 + ((uint)left.Ref0[by4] >= 4U ? 1 : 0);
                } else if (left.CompType[by4] != 0) {
                    return 2 + ((uint)above.Ref0[bx4] >= 4U ? 1 : 0);
                } else {
                    return (left.Ref0[by4] >= 4 ? 1 : 0) ^ (above.Ref0[bx4] >= 4 ? 1 : 0);
                }
            } else {
                return above.CompType[bx4] != 0 ? 3 : (above.Ref0[bx4] >= 4 ? 1 : 0);
            }
        } else if (haveLeft) {
            return left.CompType[by4] != 0 ? 3 : (left.Ref0[by4] >= 4 ? 1 : 0);
        } else {
            return 1;
        }
    }

    /// <summary>Forward ref context for single-ref (dav1d: av1_get_fwd_ref_ctx = av1_get_ref_3_ctx).</summary>
    private static int GetFwdRefCtx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int c0 = 0, c1 = 0, c2 = 0, c3 = 0;
        if (haveTop && above.Intra[bx4] == 0) {
            if (above.Ref0[bx4] < 4) { int r = above.Ref0[bx4]; if (r == 0) c0++; else if (r == 1) c1++; else if (r == 2) c2++; else c3++; }
            if (above.CompType[bx4] != 0 && above.Ref1[bx4] < 4) { int r = above.Ref1[bx4]; if (r == 0) c0++; else if (r == 1) c1++; else if (r == 2) c2++; else c3++; }
        }
        if (haveLeft && left.Intra[by4] == 0) {
            if (left.Ref0[by4] < 4) { int r = left.Ref0[by4]; if (r == 0) c0++; else if (r == 1) c1++; else if (r == 2) c2++; else c3++; }
            if (left.CompType[by4] != 0 && left.Ref1[by4] < 4) { int r = left.Ref1[by4]; if (r == 0) c0++; else if (r == 1) c1++; else if (r == 2) c2++; else c3++; }
        }
        c0 += c1;
        c2 += c3;
        return c0 == c2 ? 1 : c0 < c2 ? 0 : 2;
    }

    /// <summary>Forward ref 1 context (dav1d: av1_get_fwd_ref_1_ctx = av1_get_ref_4_ctx).</summary>
    private static int GetFwdRef1Ctx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int c0 = 0, c1 = 0;
        if (haveTop && above.Intra[bx4] == 0) {
            if (above.Ref0[bx4] < 2) { if (above.Ref0[bx4] == 0) c0++; else c1++; }
            if (above.CompType[bx4] != 0 && above.Ref1[bx4] < 2) { if (above.Ref1[bx4] == 0) c0++; else c1++; }
        }
        if (haveLeft && left.Intra[by4] == 0) {
            if (left.Ref0[by4] < 2) { if (left.Ref0[by4] == 0) c0++; else c1++; }
            if (left.CompType[by4] != 0 && left.Ref1[by4] < 2) { if (left.Ref1[by4] == 0) c0++; else c1++; }
        }
        return c0 == c1 ? 1 : c0 < c1 ? 0 : 2;
    }

    /// <summary>Forward ref 2 context (dav1d: av1_get_fwd_ref_2_ctx = av1_get_ref_5_ctx).</summary>
    private static int GetFwdRef2Ctx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int c2 = 0, c3 = 0;
        if (haveTop && above.Intra[bx4] == 0) {
            if ((above.Ref0[bx4] ^ 2U) < 2) { if (above.Ref0[bx4] == 2) c2++; else c3++; }
            if (above.CompType[bx4] != 0 && (above.Ref1[bx4] ^ 2U) < 2) { if (above.Ref1[bx4] == 2) c2++; else c3++; }
        }
        if (haveLeft && left.Intra[by4] == 0) {
            if ((left.Ref0[by4] ^ 2U) < 2) { if (left.Ref0[by4] == 2) c2++; else c3++; }
            if (left.CompType[by4] != 0 && (left.Ref1[by4] ^ 2U) < 2) { if (left.Ref1[by4] == 2) c2++; else c3++; }
        }
        return c2 == c3 ? 1 : c2 < c3 ? 0 : 2;
    }

    /// <summary>Backward ref context (dav1d: av1_get_bwd_ref_ctx = av1_get_ref_2_ctx).</summary>
    private static int GetBwdRefCtx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int c4 = 0, c5 = 0, c6 = 0;
        if (haveTop && above.Intra[bx4] == 0) {
            if (above.Ref0[bx4] >= 4) { int r = above.Ref0[bx4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
            if (above.CompType[bx4] != 0 && above.Ref1[bx4] >= 4) { int r = above.Ref1[bx4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
        }
        if (haveLeft && left.Intra[by4] == 0) {
            if (left.Ref0[by4] >= 4) { int r = left.Ref0[by4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
            if (left.CompType[by4] != 0 && left.Ref1[by4] >= 4) { int r = left.Ref1[by4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
        }
        c5 += c4;
        return c6 == c5 ? 1 : c5 < c6 ? 0 : 2;
    }

    /// <summary>Backward ref 1 context (dav1d: av1_get_bwd_ref_1_ctx = av1_get_ref_6_ctx).</summary>
    private static int GetBwdRef1Ctx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int c4 = 0, c5 = 0, c6 = 0;
        if (haveTop && above.Intra[bx4] == 0) {
            if (above.Ref0[bx4] >= 4) { int r = above.Ref0[bx4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
            if (above.CompType[bx4] != 0 && above.Ref1[bx4] >= 4) { int r = above.Ref1[bx4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
        }
        if (haveLeft && left.Intra[by4] == 0) {
            if (left.Ref0[by4] >= 4) { int r = left.Ref0[by4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
            if (left.CompType[by4] != 0 && left.Ref1[by4] >= 4) { int r = left.Ref1[by4] - 4; if (r == 0) c4++; else if (r == 1) c5++; else c6++; }
        }
        return c4 == c5 ? 1 : c4 < c5 ? 0 : 2;
    }

    /// <summary>Decode an intra block-copy (intraBC) block on a key/intra frame:
    /// a displacement vector into the already-decoded part of the current frame, then the
    /// variable-transform tree. Port of dav1d decode.c's `allow_intrabc` branch. Reconstruction
    /// (bilinear self-reference copy + residual) is handled by ReconBlockInter's isKeyOrIntra
    /// path. No inter mode/ref/filter/motion syntax is coded.</summary>
    private static void DecodeIntraBc(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx, ref Av1Block b,
        Av1BlockSize bs, int bx4, int by4, int bw4, int bh4,
        bool hasChroma, int ssHor, int ssVer, Av1EdgeFlags intraEdgeFlags)
    {
        var ts = t.TileState!;
        var fh = ctx.FrameHeader!;
        int sb128 = ctx.SequenceHeader!.Sb128 ? 1 : 0;

        // DV predictor candidates (ref = {0, -1}).
        Span<Av1RefMvsCandidate> mvstack = stackalloc Av1RefMvsCandidate[8];
        Av1RefMvs.FindRefMvs(t.Rt, mvstack, out int _, out int _, out int _,
            new Av1RefMvsRefPair { Ref0 = 0, Ref1 = -1 },
            (int)bs, intraEdgeFlags, t.By, t.Bx);

        Av1MotionVector dv;
        if (mvstack[0].Mv.Mv0.Raw != 0) dv = mvstack[0].Mv.Mv0;
        else if (mvstack[1].Mv.Mv0.Raw != 0) dv = mvstack[1].Mv.Mv0;
        else
        {
            dv = default;
            if (t.By - (16 << sb128) < ts.RowStart)
            { dv.Y = 0; dv.X = (short)(-(512 << sb128) - 2048); }
            else
            { dv.Y = (short)(-(512 << sb128)); dv.X = 0; }
        }

        ReadMvResidual(ts, ref msac, ref dv, -1); // integer displacement vectors

        // Clip the DV to the decoded region of the current tile (dav1d decode.c).
        int borderLeft = ts.ColStart * 4;
        int borderTop = ts.RowStart * 4;
        if (hasChroma)
        {
            if (bw4 < 2 && ssHor != 0) borderLeft += 4;
            if (bh4 < 2 && ssVer != 0) borderTop += 4;
        }
        int srcLeft = t.Bx * 4 + (dv.X >> 3);
        int srcTop = t.By * 4 + (dv.Y >> 3);
        int srcRight = srcLeft + bw4 * 4;
        int srcBottom = srcTop + bh4 * 4;
        int borderRight = ((ts.ColEnd + (bw4 - 1)) & ~(bw4 - 1)) * 4;

        if (srcLeft < borderLeft) { srcRight += borderLeft - srcLeft; srcLeft += borderLeft - srcLeft; }
        else if (srcRight > borderRight) { srcLeft -= srcRight - borderRight; srcRight -= srcRight - borderRight; }
        if (srcTop < borderTop) { srcBottom += borderTop - srcTop; srcTop += borderTop - srcTop; }

        int sbx = (t.Bx >> (4 + sb128)) << (6 + sb128);
        int sby = (t.By >> (4 + sb128)) << (6 + sb128);
        int sbSize = 1 << (6 + sb128);
        if (srcBottom > sby && srcRight > sbx)
        {
            if (srcTop - borderTop >= srcBottom - sby) { srcTop -= srcBottom - sby; srcBottom -= srcBottom - sby; }
            else if (srcLeft - borderLeft >= srcRight - sbx) { srcLeft -= srcRight - sbx; srcRight -= srcRight - sbx; }
        }
        if (srcBottom > sby + sbSize) { srcTop -= srcBottom - (sby + sbSize); srcBottom -= srcBottom - (sby + sbSize); }

        dv.X = (short)((srcLeft - t.Bx * 4) * 8);
        dv.Y = (short)((srcTop - t.By * 4) * 8);
        b.Mv0 = dv;


        b.CompType = (byte)Av1CompInterType.None;
        b.Ref0 = 0; b.Ref1 = -1;
        b.InterMode = (byte)Av1InterPredMode.NewMv;
        b.Motion = (byte)Av1MotionMode.Translation;
        b.InterIntraTypeField = 0;
        b.Filter = Av1Tables.Filter2d[0, 0];

        // Variable transform tree (dav1d read_vartx_tree) — identical to the inter path.
        b.MaxYTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, 0];
        bool lossless = fh.SegmentationLossless[b.SegId];
        if (b.Skip == 0 && (lossless || b.MaxYTx == (byte)Av1TxSize.Tx4x4))
        {
            b.MaxYTx = (byte)Av1TxSize.Tx4x4;
            b.UvTx = (byte)Av1TxSize.Tx4x4;
            if (fh.TxfmMode == Av1TxfmMode.Switchable)
            {
                Av1BlockContextManaged.Fill(t.Above.Tx, bx4, Av1Tables.BlockDimensions[(int)bs, 0], 0);
                Av1BlockContextManaged.Fill(t.Left.Tx, by4, Av1Tables.BlockDimensions[(int)bs, 1], 0);
            }
        }
        else if (fh.TxfmMode != Av1TxfmMode.Switchable || b.Skip != 0)
        {
            if (fh.TxfmMode == Av1TxfmMode.Switchable)
            {
                Av1BlockContextManaged.Fill(t.Above.Tx, bx4, Av1Tables.BlockDimensions[(int)bs, 0], (sbyte)Av1Tables.BlockDimensions[(int)bs, 2]);
                Av1BlockContextManaged.Fill(t.Left.Tx, by4, Av1Tables.BlockDimensions[(int)bs, 1], (sbyte)Av1Tables.BlockDimensions[(int)bs, 3]);
            }
            b.UvTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, (int)fh.PixelLayout];
        }
        else
        {
            ReadVarTxTree(t, ctx, ref msac, ref b, bs, b.MaxYTx, bx4, by4, 0);
            b.UvTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, (int)fh.PixelLayout];
        }
        b.Tx = b.MaxYTx;
    }

    /// <summary>Update above/left neighbour contexts for an intraBC block (dav1d set_ctx in the
    /// allow_intrabc branch): mode=DC, intra=0, pal_sz=0, tx_intra=block dims, skip=b.Skip.</summary>
    private static void UpdateIntraBcContext(
        Av1TaskContext t, Av1BlockSize bs, ref Av1Block b,
        int bx4, int by4, int cbx4, int cby4, int cbw4, int cbh4,
        int bw4, int bh4, bool hasChroma, Av1BlockPartition bp)
    {
        for (int i = 0; i < bw4 >> 1; i++) t.Above.Partition[(bx4 >> 1) + i] = (byte)bp;
        for (int j = 0; j < bh4 >> 1; j++) t.Left.Partition[(by4 >> 1) + j] = (byte)bp;
        for (int i = 0; i < bw4; i++)
        {
            t.Above.Mode[bx4 + i] = (byte)Av1IntraPredMode.Dc;
            t.Above.Intra[bx4 + i] = 0;
            t.Above.Skip[bx4 + i] = b.Skip;
            t.Above.SkipMode[bx4 + i] = 0;
            t.Above.SegPred[bx4 + i] = b.SegPred;
            t.Above.PalSz[bx4 + i] = 0;
            t.PalSzUv[0, bx4 + i] = 0;
            t.Above.TxIntra[bx4 + i] = (sbyte)Av1Tables.BlockDimensions[(int)bs, 2];
        }
        for (int j = 0; j < bh4; j++)
        {
            t.Left.Mode[by4 + j] = (byte)Av1IntraPredMode.Dc;
            t.Left.Intra[by4 + j] = 0;
            t.Left.Skip[by4 + j] = b.Skip;
            t.Left.SkipMode[by4 + j] = 0;
            t.Left.SegPred[by4 + j] = b.SegPred;
            t.Left.PalSz[by4 + j] = 0;
            t.PalSzUv[1, by4 + j] = 0;
            t.Left.TxIntra[by4 + j] = (sbyte)Av1Tables.BlockDimensions[(int)bs, 3];
        }
        if (hasChroma)
        {
            for (int i = 0; i < cbw4; i++) t.Above.UvMode[cbx4 + i] = (byte)Av1IntraPredMode.Dc;
            for (int j = 0; j < cbh4; j++) t.Left.UvMode[cby4 + j] = (byte)Av1IntraPredMode.Dc;
        }
    }

    private static void DecodeBlockInter(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx, ref Av1Block b,
        Av1BlockLevel bl, Av1BlockSize bs,
        int bx4, int by4, int bw4, int bh4, int w4, int h4,
        int cbx4, int cby4, int cbw4, int cbh4,
        int ssHor, int ssVer,
        bool haveLeft, bool haveTop, bool hasChroma, Av1EdgeFlags intraEdgeFlags)
    {
        var ts = t.TileState!;
        var fh = ctx.FrameHeader!;
        var above = t.Above;
        var left = t.Left;
        var mode = ts.Cdf.Mode;
        bool hasSubpelFilter = false;  // dav1d: has_subpel_filter

        if (t.Bx == 0 && t.By == 0)
        {
            AvDbg.W($"[INTER-CDF] Ref[1]={mode.Ref[1][0]} Ref[7]={mode.Ref[7][0]} Ref[10]={mode.Ref[10][0]} Comp[1]={mode.Comp[1][0]}");
            AvDbg.W($"[INTER-CDF] NewmvMode[0]={mode.NewmvMode[0][0]} MotionMode[17]={mode.MotionMode[17][0]}");
            AvDbg.W($"[INTER-STATE] pre-ref rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} dif_hi={(uint)(msac.DebugDif>>32):X8} cnt={msac.Cnt}");
        }

        var seqHdr = ctx.SequenceHeader!;
        // Segment features that pin the reference / mode (dav1d `seg`, null when segmentation is off).
        bool segOn = fh.SegmentationEnabled;
        var seg = segOn ? fh.SegmentationData.Segments[b.SegId] : default;
        bool segSkipOrGmv = segOn && (seg.Skip != 0 || seg.GlobalMv != 0);

        // ── Compound flag (dav1d decode_b) ──
        bool isComp;
        if (b.SkipMode != 0)
            isComp = true;
        else if ((!segOn || (seg.Ref == -1 && seg.GlobalMv == 0 && seg.Skip == 0)) &&
                 fh.SwitchableCompRefs && Math.Min(bw4, bh4) > 1)
            isComp = msac.DecodeBoolAdapt(mode.Comp[GetCompCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0;
        else
            isComp = false;

        Span<Av1RefMvsCandidate> mvstack = stackalloc Av1RefMvsCandidate[8];
        if (b.SkipMode != 0)
        {
            // Skip mode: the frame's two skip-mode references, NEAREST/NEAREST, plain average, no residual.
            BlockTrace?.WriteLine($"K {t.By} {t.Bx}");
            b.Ref0 = fh.SkipModeRef0;
            b.Ref1 = fh.SkipModeRef1;
            b.CompType = (byte)Av1CompInterType.Average;
            b.InterMode = (byte)Av1CompInterPredMode.NearestNearest;
            b.DrlIdx = 0;
            hasSubpelFilter = false;
            Av1RefMvs.FindRefMvs(t.Rt, mvstack, out _, out _, out _,
                new Av1RefMvsRefPair { Ref0 = (sbyte)(b.Ref0 + 1), Ref1 = (sbyte)(b.Ref1 + 1) },
                (int)bs, intraEdgeFlags, t.By, t.Bx);
            b.Mv0 = mvstack[0].Mv.Mv0;
            b.Mv1 = mvstack[0].Mv.Mv1;
            Av1RefMvs.FixMvPrecision(fh, ref b.Mv0);
            Av1RefMvs.FixMvPrecision(fh, ref b.Mv1);
        }
        else if (isComp)
        {
            // ── Compound references ──
            if (msac.DecodeBoolAdapt(mode.CompDir[GetCompDirCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
            {
                // bidirectional: a forward (LAST..GOLDEN) then a backward (BWDREF..ALTREF) reference
                if (msac.DecodeBoolAdapt(mode.CompFwdRef[0 * 3 + GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
                    b.Ref0 = (sbyte)(2 + (int)msac.DecodeBoolAdapt(
                        mode.CompFwdRef[2 * 3 + GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)]));
                else
                    b.Ref0 = (sbyte)msac.DecodeBoolAdapt(
                        mode.CompFwdRef[1 * 3 + GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)]);

                if (msac.DecodeBoolAdapt(mode.CompBwdRef[0 * 3 + GetBwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
                    b.Ref1 = 6;
                else
                    b.Ref1 = (sbyte)(4 + (int)msac.DecodeBoolAdapt(
                        mode.CompBwdRef[1 * 3 + GetBwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)]));
            }
            else
            {
                // unidirectional: BWDREF+ALTREF, or LAST with LAST2/LAST3/GOLDEN
                if (msac.DecodeBoolAdapt(mode.CompUniRef[0 * 3 + GetRefCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
                {
                    b.Ref0 = 4;
                    b.Ref1 = 6;
                }
                else
                {
                    b.Ref0 = 0;
                    b.Ref1 = (sbyte)(1 + (int)msac.DecodeBoolAdapt(
                        mode.CompUniRef[1 * 3 + GetUniP1Ctx(above, left, by4, bx4, haveTop, haveLeft)]));
                    if (b.Ref1 == 2)
                        b.Ref1 += (sbyte)msac.DecodeBoolAdapt(
                            mode.CompUniRef[2 * 3 + GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)]);
                }
            }

            Av1RefMvs.FindRefMvs(t.Rt, mvstack, out int nMvs, out int cctx, out _,
                new Av1RefMvsRefPair { Ref0 = (sbyte)(b.Ref0 + 1), Ref1 = (sbyte)(b.Ref1 + 1) },
                (int)bs, intraEdgeFlags, t.By, t.Bx);

            // ── Compound inter mode + DRL ──
            b.InterMode = (byte)msac.DecodeSymbolAdapt8(mode.CompInterMode[cctx], (int)Av1CompInterPredMode.Count - 1);
            int im0 = Av1Tables.CompInterPredModes[b.InterMode, 0];
            int im1 = Av1Tables.CompInterPredModes[b.InterMode, 1];
            const int nearMv = (int)Av1InterPredMode.NearMv;
            b.DrlIdx = 0;
            if (b.InterMode == (byte)Av1CompInterPredMode.NewNew)
            {
                if (nMvs > 1)
                {
                    b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 0)]);
                    if (b.DrlIdx == 1 && nMvs > 2)
                        b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 1)]);
                }
            }
            else if (im0 == nearMv || im1 == nearMv)
            {
                b.DrlIdx = 1;
                if (nMvs > 2)
                {
                    b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 1)]);
                    if (b.DrlIdx == 2 && nMvs > 3)
                        b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 2)]);
                }
            }

            hasSubpelFilter = Math.Min(bw4, bh4) == 1 || b.InterMode != (byte)Av1CompInterPredMode.GlobalGlobal;
            b.Mv0 = AssignCompMv(ts, ref msac, fh, mvstack, im0, 0, b.Ref0, b.DrlIdx, t.Bx, t.By, bw4, bh4, ref hasSubpelFilter);
            b.Mv1 = AssignCompMv(ts, ref msac, fh, mvstack, im1, 1, b.Ref1, b.DrlIdx, t.Bx, t.By, bw4, bh4, ref hasSubpelFilter);

            // ── Compound type: distance-weighted / average vs wedge / difference-weighted segment mask ──
            bool isSegWedge = false;
            if (seqHdr.MaskedCompound)
                isSegWedge = msac.DecodeBoolAdapt(mode.MaskComp[GetMaskCompCtx(above, left, by4, bx4)]) != 0;
            if (!isSegWedge)
            {
                if (seqHdr.JntComp)
                {
                    int jctx = GetJntCompCtx(seqHdr.OrderHintNBits, fh.FrameOffset,
                        ctx.RefFrames[fh.GetRefIdx(b.Ref0)].OrderHint, ctx.RefFrames[fh.GetRefIdx(b.Ref1)].OrderHint,
                        above, left, by4, bx4);
                    b.CompType = (byte)((int)Av1CompInterType.WeightedAvg + (int)msac.DecodeBoolAdapt(mode.JntComp[jctx]));
                }
                else
                {
                    b.CompType = (byte)Av1CompInterType.Average;
                }
            }
            else
            {
                if (((Av1Tables.WedgeAllowedMask >> (int)bs) & 1) != 0)
                {
                    int wctx = Av1Tables.WedgeCtxLut[(int)bs];
                    b.CompType = (byte)((int)Av1CompInterType.Wedge - (int)msac.DecodeBoolAdapt(mode.WedgeComp[wctx]));
                    if (b.CompType == (byte)Av1CompInterType.Wedge)
                        b.WedgeIdx = (byte)msac.DecodeSymbolAdapt16(mode.WedgeIdx[wctx], 15);
                }
                else
                {
                    b.CompType = (byte)Av1CompInterType.Seg;
                }
                b.MaskSign = (byte)msac.DecodeBoolEqui();
            }
        }
        else
        {
            b.CompType = (byte)Av1CompInterType.None;

            // ── Single reference ──
            if (segOn && seg.Ref > 0)
            {
                b.Ref0 = (sbyte)(seg.Ref - 1);
            }
            else if (segSkipOrGmv)
            {
                b.Ref0 = 0;
            }
            else if (msac.DecodeBoolAdapt(mode.Ref[GetRefCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
            {
                if (msac.DecodeBoolAdapt(mode.Ref[1 * 3 + GetBwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
                    b.Ref0 = 6;
                else
                    b.Ref0 = (sbyte)(4 + (int)msac.DecodeBoolAdapt(
                        mode.Ref[5 * 3 + GetBwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)]));
            }
            else
            {
                if (msac.DecodeBoolAdapt(mode.Ref[2 * 3 + GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)]) != 0)
                    b.Ref0 = (sbyte)(2 + (int)msac.DecodeBoolAdapt(
                        mode.Ref[4 * 3 + GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)]));
                else
                    b.Ref0 = (sbyte)msac.DecodeBoolAdapt(
                        mode.Ref[3 * 3 + GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)]);
            }
            b.Ref1 = -1;

            Av1RefMvs.FindRefMvs(t.Rt, mvstack, out int nCand, out int modeCtx, out int _,
                new Av1RefMvsRefPair { Ref0 = (sbyte)(b.Ref0 + 1), Ref1 = -1 },
                (int)bs, intraEdgeFlags, t.By, t.Bx);
            if (BlockTrace != null)
            {
                var sb = new System.Text.StringBuilder($"S {t.By} {t.Bx} n{nCand} c{modeCtx}");
                for (int q = 0; q < nCand; q++) sb.Append($" {mvstack[q].Mv.Mv0.Y},{mvstack[q].Mv.Mv0.X}:{mvstack[q].Weight}");
                BlockTrace.WriteLine(sb.ToString());
            }

            // ── Single-reference mode + MV (dav1d: newmv / globalmv / refmv flags, then DRL) ──
            if (segSkipOrGmv || msac.DecodeBoolAdapt(mode.NewmvMode[modeCtx & 7]) != 0)
            {
                if (segSkipOrGmv || msac.DecodeBoolAdapt(mode.GlobalmvMode[(modeCtx >> 3) & 1]) == 0)
                {
                    b.InterMode = (byte)Av1InterPredMode.GlobalMv;
                    b.Mv0 = Av1RefMvs.GetGmv2d(in fh.Gmv[b.Ref0], t.Bx, t.By, bw4, bh4, fh);
                    hasSubpelFilter = Math.Min(bw4, bh4) == 1 ||
                        fh.Gmv[b.Ref0].Type == Av1WarpedMotionType.Translation;
                }
                else
                {
                    hasSubpelFilter = true;
                    if (msac.DecodeBoolAdapt(mode.RefmvMode[(modeCtx >> 4) & 15]) != 0)
                    {
                        b.InterMode = (byte)Av1InterPredMode.NearMv;
                        b.DrlIdx = 1;
                        if (nCand > 2)
                        {
                            b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 1)]);
                            if (b.DrlIdx == 2 && nCand > 3)
                                b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 2)]);
                        }
                    }
                    else
                    {
                        b.InterMode = (byte)Av1InterPredMode.NearestMv;
                        b.DrlIdx = 0;
                    }
                    // FindRefMvs pads the stack to two entries with the global MV, so mvstack[1] is valid even with
                    // one candidate (dav1d reads mvstack[drl_idx] unconditionally).
                    b.Mv0 = mvstack[b.DrlIdx].Mv.Mv0;
                    if (b.DrlIdx < 2)
                        Av1RefMvs.FixMvPrecision(fh, ref b.Mv0);
                }
            }
            else
            {
                hasSubpelFilter = true;
                b.InterMode = (byte)Av1InterPredMode.NewMv;
                b.DrlIdx = 0;
                if (nCand > 1)
                {
                    b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 0)]);
                    if (b.DrlIdx == 1 && nCand > 2)
                        b.DrlIdx += (byte)msac.DecodeBoolAdapt(mode.DrlBit[Av1RefMvs.GetDrlContext(mvstack, 1)]);
                }
                if (nCand > 1)
                {
                    b.Mv0 = mvstack[b.DrlIdx].Mv.Mv0;
                }
                else
                {
                    b.Mv0 = mvstack[0].Mv.Mv0;   // the padded global MV when there are no candidates
                    Av1RefMvs.FixMvPrecision(fh, ref b.Mv0);
                }
                ReadMvResidual(ts, ref msac, ref b.Mv0, fh);
            }
        }

        // Inter-intra and motion modes exist only for single-reference blocks.
        if (!isComp)
        {
            // ── Interintra decode ──
            int iiSzGrp = Av1Tables.YmodeSizeContext[(int)bs];
            if (seqHdr.InterIntra &&
                (Av1Tables.InterIntraAllowedMask & (1u << (int)bs)) != 0)
            {
                if (msac.DecodeBoolAdapt(ts.Cdf.Mode.Interintra[iiSzGrp]) != 0)
                {
                    b.InterIntraMode = (byte)msac.DecodeSymbolAdapt4(
                        ts.Cdf.Mode.InterintraMode[iiSzGrp], 3);
                    byte wedgeCtx = Av1Tables.WedgeCtxLut[(int)bs];
                    int wedgeFlag = (int)msac.DecodeBoolAdapt(ts.Cdf.Mode.InterintraWedge[wedgeCtx]);
                    b.InterIntraTypeField = (byte)((int)Av1InterIntraType.Blend + wedgeFlag);
                    if (b.InterIntraTypeField == (byte)Av1InterIntraType.Wedge)
                        b.WedgeIdx = (byte)msac.DecodeSymbolAdapt16(ts.Cdf.Mode.WedgeIdx[wedgeCtx], 15);
                }
                else
                {
                    b.InterIntraTypeField = (byte)Av1InterIntraType.None;
                }
                if (t.Bx == 0 && t.By == 0) {
                    AvDbg.W($"[INTER-INTRA] ii_sz_grp={iiSzGrp} mode={b.InterIntraMode} type={b.InterIntraTypeField} wedge={b.WedgeIdx} rng={msac.DebugRng:X4}");
                    // Print the dif at this point too
                    AvDbg.W($"[INTER-INTRA-DIF] dif_lo={(uint)msac.DebugDif:X8} dif_hi={(uint)(msac.DebugDif>>32):X8} c48={(ushort)(msac.DebugDif>>48):X4}");
                }
            }

            // ── Motion mode (dav1d: decode.c ~1795) ──
            // Gate conditions (dav1d: switchable_motion_mode && interintra_type==NONE
            // && imin(bw4,bh4)>=2 && not_warped_globalmv && overlappable neighbours)
            // NOTE: b.Skip has NO effect on motion mode decode — skipped blocks still code motion mode.
            if (fh.SwitchableMotionMode &&
                b.InterIntraTypeField == (byte)Av1InterIntraType.None &&
                Math.Min(bw4, bh4) >= 2 &&
                // Not warped global motion (dav1d: !(!force_integer_mv && GLOBALMV && gmv.type > TRANSLATION))
                !(!fh.ForceIntegerMv && b.InterMode == (byte)Av1InterPredMode.GlobalMv &&
                  fh.Gmv[b.Ref0].Type > Av1WarpedMotionType.Translation) &&
                // Has overlappable neighbours (dav1d: findoddzero on intra flags)
                ((haveLeft && FindOddZero(t.Left.Intra, by4 + 1, h4 >> 1)) ||
                 (haveTop && FindOddZero(t.Above.Intra, bx4 + 1, w4 >> 1))))
            {
                Span<long> mask = stackalloc long[2]; mask[0] = 0; mask[1] = 0;
                FindMatchingRef(t, intraEdgeFlags, bw4, bh4, w4, h4,
                                haveLeft, haveTop, b.Ref0, mask);
                bool allowWarp = ctx.Svc[b.Ref0, 0].Scale == 0 &&
                                 !fh.ForceIntegerMv && fh.WarpMotion &&
                                 (mask[0] | mask[1]) != 0;
                if (t.Bx <= 4 && t.By <= 16)
                    AvDbg.W($"[MOTION-GATE] bx={t.Bx} by={t.By} bs={(int)bs} interMode={b.InterMode} allowWarp={allowWarp} mask0={mask[0]:X} mask1={mask[1]:X} warpMotion={fh.WarpMotion} svcScale={ctx.Svc[b.Ref0,0].Scale} forceInt={fh.ForceIntegerMv}");

                if (allowWarp)
                {
                    b.Motion = (byte)msac.DecodeSymbolAdapt4(ts.Cdf.Mode.MotionMode[(int)bs], 2);
                    if (t.Bx <= 2 && t.By == 0)
                        AvDbg.W($"[MOTION-MODE] bx={t.Bx} by={t.By} bs={(int)bs} motion={b.Motion} cdf=MotionMode[{bs}] allowWarp=1 mask0={mask[0]:X} mask1={mask[1]:X}");
                    if (b.Motion == (byte)Av1MotionMode.Warp)
                    {
                        hasSubpelFilter = false;
                        DeriveWarpmv(t, bw4, bh4, mask, b.Mv0);
                    }
                }
                else
                {
                    b.Motion = (byte)msac.DecodeBoolAdapt(ts.Cdf.Mode.Obmc[(int)bs]);
                    if (t.Bx <= 2 && t.By == 0)
                        AvDbg.W($"[MOTION-MODE] bx={t.Bx} by={t.By} bs={(int)bs} motion={b.Motion} cdf=Obmc[{bs}] allowWarp=0 mask0={mask[0]:X} mask1={mask[1]:X}");
                }
            }
            else
            {
                b.Motion = (byte)Av1MotionMode.Translation;
                if (t.Bx <= 4 && t.By == 0)
                    AvDbg.W($"[MOTION-GATE-SKIP] bx={t.Bx} by={t.By} interIntra={b.InterIntraTypeField} minBW={Math.Min(bw4,bh4)} interMode={b.InterMode} gmvType={fh.Gmv[b.Ref0].Type} haveL={haveLeft} haveT={haveTop}");
            }
        }
        else
        {
            b.InterIntraTypeField = (byte)Av1InterIntraType.None;
            b.Motion = (byte)Av1MotionMode.Translation;
        }

        if (b.InterIntraTypeField != 0) BlockTrace?.WriteLine($"II {t.By} {t.Bx} t{b.InterIntraTypeField} m{b.InterIntraMode}");
        // ── Subpel filters (dav1d: filter[0] then, with dual_filter, filter[1]; filter2d = filter_2d[f1][f0]) ──
        int filter0 = (int)fh.SubpelFilterMode, filter1 = (int)fh.SubpelFilterMode;
        if (fh.SubpelFilterMode == Av1FilterMode.Switchable)
        {
            if (hasSubpelFilter)
            {
                bool comp = b.CompType != (byte)Av1CompInterType.None;
                filter0 = (int)msac.DecodeSymbolAdapt4(mode.Filter[GetFilterCtx(above, left, comp, 0, b.Ref0, by4, bx4)], 2);
                filter1 = seqHdr.DualFilter
                    ? (int)msac.DecodeSymbolAdapt4(mode.Filter[8 + GetFilterCtx(above, left, comp, 1, b.Ref0, by4, bx4)], 2)
                    : filter0;
            }
            else
            {
                filter0 = filter1 = 0; // EIGHTTAP_REGULAR
            }
        }
        b.Filter = Av1Tables.Filter2d[filter1, filter0];

        // ── Variable transform tree ──
        b.MaxYTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, 0];
        bool lossless = fh.SegmentationLossless[b.SegId];
        if (b.Skip == 0 && (lossless || b.MaxYTx == (byte)Av1TxSize.Tx4x4))
        {
            b.MaxYTx = (byte)Av1TxSize.Tx4x4;
            b.UvTx = (byte)Av1TxSize.Tx4x4;
            if (fh.TxfmMode == Av1TxfmMode.Switchable)
            {
                // dav1d decode.c:463-465 — fill bw4/bh4 entries with TX_4X4 (0)
                int fillLen = Av1Tables.BlockDimensions[(int)bs, 0];
                Av1BlockContextManaged.Fill(t.Above.Tx, bx4, fillLen, 0);
                fillLen = Av1Tables.BlockDimensions[(int)bs, 1];
                Av1BlockContextManaged.Fill(t.Left.Tx, by4, fillLen, 0);
            }
        }
        else if (fh.TxfmMode != Av1TxfmMode.Switchable || b.Skip != 0)
        {
            if (fh.TxfmMode == Av1TxfmMode.Switchable)
            {
                // dav1d decode.c:467-470 — fill bw4/bh4 entries with log2(block dims)
                int fillLen = Av1Tables.BlockDimensions[(int)bs, 0];
                Av1BlockContextManaged.Fill(t.Above.Tx, bx4, fillLen, (sbyte)Av1Tables.BlockDimensions[(int)bs, 2]);
                fillLen = Av1Tables.BlockDimensions[(int)bs, 1];
                Av1BlockContextManaged.Fill(t.Left.Tx, by4, fillLen, (sbyte)Av1Tables.BlockDimensions[(int)bs, 3]);
            }
            b.UvTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, (int)fh.PixelLayout];
        }
        else
        {
            // Switchable transform mode — read split decisions from MSAC
            ReadVarTxTree(t, ctx, ref msac, ref b, bs, b.MaxYTx, bx4, by4, 0);
            b.UvTx = Av1Tables.MaxTxfmSizeForBlockSize[(int)bs, (int)fh.PixelLayout];
        }
        b.Tx = b.MaxYTx;

        if (t.Bx <= 2 && t.By == 0)
            AvDbg.W($"[INTER-DECODE] bx={t.Bx} by={t.By} bs={(int)bs} bw4={bw4} bh4={bh4} w4={w4} h4={h4} ref0={b.Ref0} mv0=({b.Mv0.Y},{b.Mv0.X}) interMode={b.InterMode} compType={b.CompType} filter={b.Filter} motion={b.Motion} tx={b.Tx} RefFrameMvs={fh.UseRefFrameMvs} TxfmMode={fh.TxfmMode} SwitchableMotionMode={fh.SwitchableMotionMode} SubpelFilterMode={fh.SubpelFilterMode} ForceIntegerMv={fh.ForceIntegerMv} SwitchableCompRefs={fh.SwitchableCompRefs} MotionModeCdf0={ts.Cdf.Mode.MotionMode[(int)bs][0]} ObmcCdf0={ts.Cdf.Mode.Obmc[(int)bs][0]} SvcScale0={ctx.Svc[b.Ref0, 0].Scale} SegEnabled={fh.SegmentationEnabled} SegRef={fh.SegmentationData.Segments[b.SegId].Ref}");
    }

    // VarTx partition context table (dav1d: dav1d_tx_partition_ctx)
    private static readonly byte[,] TxPartitionCtx = {
        { 0, 0, 0, 0 },  // TX_4X4
        { 1, 1, 0, 0 },  // TX_8X8
        { 2, 2, 1, 0 },  // TX_16X16
        { 3, 3, 2, 1 },  // TX_32X32
        { 4, 4, 3, 2 },  // TX_64X64
    };

    // dav1d read_vartx_tree (switchable branch): one transform tree per maximum-size transform tiling the block
    // (two for 64x128/128x64, four for 128x128), each read by read_tx_tree.
    private static void ReadVarTxTree(Av1TaskContext t, Av1DecoderContext ctx, ref Av1Msac msac, ref Av1Block b,
        Av1BlockSize bs, byte maxTx, int bx4, int by4, int depth)
    {
        ref readonly var ytx = ref Av1Tables.TxfmDimensions[maxTx];
        int bw4 = Av1Tables.BlockDimensions[(int)bs, 0], bh4 = Av1Tables.BlockDimensions[(int)bs, 1];
        for (int y = 0, yOff = 0; y < bh4; y += ytx.H, yOff++)
            for (int x = 0, xOff = 0; x < bw4; x += ytx.W, xOff++)
                ReadTxTree(t, ctx, ref msac, ref b, maxTx, depth, xOff, yOff, t.Bx + x, t.By + y);
    }

    // dav1d read_tx_tree: a split flag below depth 2, recursion into the sub-transforms that start inside the frame
    // (positions tracked per sub-tree), and the leaf's log2 size into the above/left tx contexts.
    private static void ReadTxTree(Av1TaskContext t, Av1DecoderContext ctx, ref Av1Msac msac, ref Av1Block b,
        byte from, int depth, int xOff, int yOff, int bx, int by)
    {
        int bx4 = bx & 31, by4 = by & 31;
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[from];
        int txw = tDim.Lw, txh = tDim.Lh;
        bool isSplit = false;
        if (depth < 2 && from > (byte)Av1TxSize.Tx4x4)
        {
            int cat = 2 * ((int)Av1TxSize.Tx64x64 - tDim.Max) - depth;
            int a = t.Above.Tx[bx4] < txw ? 1 : 0;
            int l = t.Left.Tx[by4] < txh ? 1 : 0;
            isSplit = msac.DecodeBoolAdapt(t.TileState!.Cdf.GetTxPartCdf(cat, a + l)) != 0;
            if (isSplit)
            {
                if (depth == 0) b.TxSplit0 |= (byte)(1 << (yOff * 4 + xOff));
                else b.TxSplit1 |= (ushort)(1 << (yOff * 4 + xOff));
            }
        }

        if (isSplit && tDim.Max > (int)Av1TxSize.Tx8x8)
        {
            byte sub = tDim.Sub;
            ref readonly var subDim = ref Av1Tables.TxfmDimensions[sub];
            int txsw = subDim.W, txsh = subDim.H;
            ReadTxTree(t, ctx, ref msac, ref b, sub, depth + 1, xOff * 2, yOff * 2, bx, by);
            if (txw >= txh && bx + txsw < ctx.Bw)
                ReadTxTree(t, ctx, ref msac, ref b, sub, depth + 1, xOff * 2 + 1, yOff * 2, bx + txsw, by);
            if (txh >= txw && by + txsh < ctx.Bh)
            {
                ReadTxTree(t, ctx, ref msac, ref b, sub, depth + 1, xOff * 2, yOff * 2 + 1, bx, by + txsh);
                if (txw >= txh && bx + txsw < ctx.Bw)
                    ReadTxTree(t, ctx, ref msac, ref b, sub, depth + 1, xOff * 2 + 1, yOff * 2 + 1, bx + txsw, by + txsh);
            }
        }
        else
        {
            // An 8x8 (or smaller) split codes 4x4 transforms without further symbols.
            Av1BlockContextManaged.Fill(t.Above.Tx, bx4, tDim.W, (sbyte)(isSplit ? 0 : txw));
            Av1BlockContextManaged.Fill(t.Left.Tx, by4, tDim.H, (sbyte)(isSplit ? 0 : txh));
        }
    }

    /// <summary>Decode MV residual (port of dav1d read_mv_residual).</summary>
    private static void ReadMvResidual(Av1TileState ts, ref Av1Msac msac, ref Av1MotionVector refMv, Av1DecoderFrameHeader fh)
        => ReadMvResidual(ts, ref msac, ref refMv, fh.ForceIntegerMv ? -1 : (fh.Hp ? 1 : 0));

    /// <summary>Decode MV residual with an explicit precision (dav1d read_mv_residual);
    /// intra block copy passes mvPrec = -1 (integer displacement vectors).</summary>
    private static void ReadMvResidual(Av1TileState ts, ref Av1Msac msac, ref Av1MotionVector refMv, int mvPrec)
    {
        int mvJoint = (int)msac.DecodeSymbolAdapt4(ts.Cdf.Mv.Joint, 3);
        int baseY = refMv.Y, baseX = refMv.X;
        // dav1d: MV_JOINT_V(2) → Y += comp[0]; MV_JOINT_H(1) → X += comp[1]
        if ((mvJoint & 2) != 0)
            refMv.Y = (short)(refMv.Y + ReadMvComponentDiff(ref msac, ts.Cdf.Mv.Comp0, mvPrec));
        if ((mvJoint & 1) != 0)
            refMv.X = (short)(refMv.X + ReadMvComponentDiff(ref msac, ts.Cdf.Mv.Comp1, mvPrec));
        if (refMv.X != 0 || refMv.Y != 0)
            AvDbg.W($"[MV-RESIDUAL] mvPrec={mvPrec} joint={mvJoint} base=({baseX},{baseY}) delta=({refMv.X-baseX},{refMv.Y-baseY}) final=({refMv.X},{refMv.Y}) rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} cnt={msac.Cnt}");
    }

    /// <summary>Decode a single MV component difference (port of dav1d read_mv_component_diff).</summary>
    private static int ReadMvComponentDiff(ref Av1Msac msac, Av1CdfMvComponent mvComp, int mvPrec)
    {
        bool sign = msac.DecodeBoolAdapt(mvComp.Sign) != 0;
        int cl = (int)msac.DecodeSymbolAdapt16(mvComp.Classes, 10);
        int up, fp = 3, hp = 1;

        if (cl == 0)
        {
            up = (int)msac.DecodeBoolAdapt(mvComp.Class0);
            if (mvPrec >= 0)
            {
                fp = (int)msac.DecodeSymbolAdapt4(mvComp.Class0Fp[up], 3);
                if (mvPrec > 0)
                    hp = (int)msac.DecodeBoolAdapt(mvComp.Class0Hp);
            }
        }
        else
        {
            up = 1 << cl;
            for (int n = 0; n < cl; n++)
                up |= (int)msac.DecodeBoolAdapt(mvComp.ClassN[n]) << n;
            if (mvPrec >= 0)
            {
                fp = (int)msac.DecodeSymbolAdapt4(mvComp.ClassNFp, 3);
                if (mvPrec > 0)
                    hp = (int)msac.DecodeBoolAdapt(mvComp.ClassNHp);
            }
        }

        int diff = ((up << 3) | (fp << 1) | hp) + 1;
        return sign ? -diff : diff;
    }

    /// <summary>
    /// Update above/left neighbor contexts after reconstruction completes.
    /// Must be called AFTER recon_b_intra, matching dav1d's ordering (decode.c:1253).
    /// </summary>
    // dav1d get_prev_frame_segid: the smallest id the reference map holds under the (frame-clipped) block.
    private static byte PrevFrameSegId(Av1DecoderContext ctx, int by, int bx, int w4, int h4)
    {
        var map = ctx.PrevSegMap!;
        int segId = 8, off = by * ctx.B4Stride + bx;
        do
        {
            for (int x = 0; x < w4; x++) segId = Math.Min(segId, map[off + x]);
            off += ctx.B4Stride;
        } while (--h4 > 0 && segId != 0);
        if (segId >= 8) throw new InvalidDataException("AV1 reference segmentation map holds an invalid segment id.");
        return (byte)segId;
    }

    // dav1d get_cur_frame_segid: the spatial prediction from the left / above / above-left ids and its context.
    private static byte CurFrameSegIdPred(Av1DecoderContext ctx, int by, int bx, bool haveTop, bool haveLeft, out int segCtx)
    {
        var map = ctx.CurSegMap!;
        int stride = ctx.B4Stride, off = by * stride + bx;
        if (haveLeft && haveTop)
        {
            int l = map[off - 1], a = map[off - stride], al = map[off - stride - 1];
            segCtx = l == a && al == l ? 2 : l == a || al == l || a == al ? 1 : 0;
            return (byte)(a == al ? a : l);
        }
        segCtx = 0;
        return haveLeft ? map[off - 1] : haveTop ? map[off - stride] : (byte)0;
    }

    // Coded segment id: a symbol relative to the spatial prediction (dav1d neg_deinterleave), capped as dav1d does.
    private static byte ReadSegId(ref Av1Msac msac, Av1TileState ts, Av1DecoderContext ctx, Av1DecoderFrameHeader fh,
        int by, int bx, bool haveTop, bool haveLeft)
    {
        int pred = CurFrameSegIdPred(ctx, by, bx, haveTop, haveLeft, out int segCtx);
        int diff = (int)msac.DecodeSymbolAdapt8(ts.Cdf.Mode.SegId[segCtx], Av1Constants.MaxSegments - 1);
        int last = fh.SegmentationData.LastActiveSegId;
        int id = NegDeinterleave(diff, pred, last + 1);
        if (id > last) id = 0;
        if (id >= Av1Constants.MaxSegments) id = 0;
        return (byte)id;
    }

    private static int NegDeinterleave(int diff, int reference, int max)
    {
        if (reference == 0) return diff;
        if (reference >= max - 1) return max - diff - 1;
        if (2 * reference < max)
        {
            if (diff <= 2 * reference)
                return (diff & 1) != 0 ? reference + ((diff + 1) >> 1) : reference - (diff >> 1);
            return diff;
        }
        if (diff <= 2 * (max - reference - 1))
            return (diff & 1) != 0 ? reference + ((diff + 1) >> 1) : reference - (diff >> 1);
        return max - (diff + 1);
    }

    public static void UpdateIntraBlockContext(
        Av1TaskContext t, Av1DecoderContext ctx, Av1BlockSize bs, ref Av1Block b,
        int bx4, int by4, int cbx4, int cby4, int cbw4, int cbh4,
        int ssHor, int ssVer, bool hasChroma, Av1BlockPartition bp)
    {
        var fh = ctx.FrameHeader!;
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[b.Tx];

        var yModeNoFilt = b.YMode == (byte)Av1IntraPredMode.Filter
            ? (byte)Av1IntraPredMode.Dc
            : b.YMode;

        int bw4 = Av1Tables.BlockDimensions[(int)bs, 0];
        int bh4 = Av1Tables.BlockDimensions[(int)bs, 1];

        // Above context
        {
            int off = bx4;
            int count = Math.Min(bw4, 32 - off);
            if (count > 0)
            {
                Av1BlockContextManaged.Fill(t.Above.TxIntra, off, count, (sbyte)tDim.Lw);
                Av1BlockContextManaged.Fill(t.Above.Tx, off, count, (sbyte)tDim.Lw);
                Av1BlockContextManaged.Fill(t.Above.Mode, off, count, yModeNoFilt);
                Av1BlockContextManaged.Fill(t.Above.PalSz, off, count, b.PalSzY);
                Av1BlockContextManaged.Fill(t.Above.SegPred, off, count, b.SegPred);
                Av1BlockContextManaged.Fill(t.Above.SkipMode, off, count, 0);
                Av1BlockContextManaged.Fill(t.Above.Intra, off, count, 1);
                Av1BlockContextManaged.Fill(t.Above.Skip, off, count, b.Skip);
                if (fh.IsInterOrSwitch)
                {
                    Av1BlockContextManaged.Fill(t.Above.CompType, off, count, 0);
                    Av1BlockContextManaged.Fill(t.Above.Ref0, off, count, unchecked((sbyte)0xFF));
                    Av1BlockContextManaged.Fill(t.Above.Ref1, off, count, unchecked((sbyte)0xFF));
                    Av1BlockContextManaged.Fill(t.Above.Filter0, off, count, Av1Tables.NSwitchableFilters);
                    Av1BlockContextManaged.Fill(t.Above.Filter1, off, count, Av1Tables.NSwitchableFilters);
                }
            }
        }

        // Left context
        {
            int off = by4;
            int count = Math.Min(bh4, 32 - off);
            if (count > 0)
            {
                Av1BlockContextManaged.Fill(t.Left.TxIntra, off, count, (sbyte)tDim.Lh);
                Av1BlockContextManaged.Fill(t.Left.Tx, off, count, (sbyte)tDim.Lh);
                Av1BlockContextManaged.Fill(t.Left.Mode, off, count, yModeNoFilt);
                Av1BlockContextManaged.Fill(t.Left.PalSz, off, count, b.PalSzY);
                Av1BlockContextManaged.Fill(t.Left.SegPred, off, count, b.SegPred);
                Av1BlockContextManaged.Fill(t.Left.SkipMode, off, count, 0);
                Av1BlockContextManaged.Fill(t.Left.Intra, off, count, 1);
                Av1BlockContextManaged.Fill(t.Left.Skip, off, count, b.Skip);
                if (fh.IsInterOrSwitch)
                {
                    Av1BlockContextManaged.Fill(t.Left.CompType, off, count, 0);
                    Av1BlockContextManaged.Fill(t.Left.Ref0, off, count, unchecked((sbyte)0xFF));
                    Av1BlockContextManaged.Fill(t.Left.Ref1, off, count, unchecked((sbyte)0xFF));
                    Av1BlockContextManaged.Fill(t.Left.Filter0, off, count, Av1Tables.NSwitchableFilters);
                    Av1BlockContextManaged.Fill(t.Left.Filter1, off, count, Av1Tables.NSwitchableFilters);
                }
            }
        }

        // UV mode context
        if (hasChroma)
        {
            int cw = Math.Min(cbw4, 32 - cbx4);
            int ch = Math.Min(cbh4, 32 - cby4);
            if (cw > 0) Av1BlockContextManaged.Fill(t.Above.UvMode, cbx4, cw, b.UvMode);
            if (ch > 0) Av1BlockContextManaged.Fill(t.Left.UvMode, cby4, ch, b.UvMode);
        }

        // Palette UV context (per-b4 unit) — dav1d: pal_sz_uv update
        // t.PalSzUv[0][off] = above, t.PalSzUv[1][off] = left
        // For the above row: fill bw4 entries at bx4 with has_chroma ? b.PalSzUv : 0
        int palUvVal = hasChroma ? b.PalSzUv : 0;
        {
            int off = bx4;
            int count = Math.Min(bw4, 32 - off);
            for (int i = 0; i < count; i++)
                t.PalSzUv[0, off + i] = (byte)palUvVal;
        }
        {
            int off = by4;
            int count = Math.Min(bh4, 32 - off);
            for (int i = 0; i < count; i++)
                t.PalSzUv[1, off + i] = (byte)palUvVal;
        }
    }

    // ========================================================================
    // Inter block helpers (motion mode, filter, warp) — ported from dav1d decode.c
    // ========================================================================

    /// <summary>
    /// Dav1d's findoddzero: checks if any byte in the span of length n
    /// starting from offset is nonzero (odd, i.e. not INTRA=0x40).
    /// Returns true if any found.
    /// </summary>
    private static bool FindOddZero(ReadOnlySpan<byte> buf, int offset, int n)
    {
        for (int i = 0; i < n; i++)
            if (buf[offset + i * 2] == 0)
                return true;
        return false;
    }

    /// <summary>
    /// Port of dav1d's find_matching_ref (decode.c:193-264).
    /// Scans above/left refmvs for blocks sharing the same reference frame,
    /// building a bitmask of matching blocks. Used to determine warp eligibility.
    /// </summary>
    private static void FindMatchingRef(Av1TaskContext t, Av1EdgeFlags intraEdgeFlags,
        int bw4, int bh4, int w4, int h4,
        bool haveLeft, bool haveTop, int refIdx,
        Span<long> masks)
    {
        var rt = t.Rt;
        int by = t.By;
        int bx = t.Bx;
        var r = rt.R;
        int rowIdx = (by & 31) + 5;
        int count = 0;
        bool haveTopLeft = haveTop && haveLeft;
        bool haveTopRight = Math.Max(bw4, bh4) < 32 &&
                             haveTop && bx + bw4 < t.TileState!.ColEnd &&
                             (intraEdgeFlags & Av1EdgeFlags.I444TopHasRight) != 0;

        bool Matches(Av1RefMvsBlock rp) =>
            rp.Ref.Ref0 == refIdx + 1 && rp.Ref.Ref1 == -1;

        if (haveTop)
        {
            var topRow = r[rowIdx - 1];
            if (topRow == null) return;
            int rx = bx;
            var r2 = topRow[rx];
            if (Matches(r2))
            {
                masks[0] |= 1;
                count = 1;
            }
            int aw4 = Av1Tables.BlockDimensions[r2.Bs, 0];
            if (aw4 >= bw4)
            {
                int off = bx & (aw4 - 1);
                if (off != 0) haveTopLeft = false;
                if (aw4 - off > bw4) haveTopRight = false;
            }
            else
            {
                long mask = 1L << aw4;
                for (int x = aw4; x < w4; x += aw4)
                {
                    rx += aw4;
                    r2 = topRow[rx];
                    if (Matches(r2))
                    {
                        masks[0] |= mask;
                        if (++count >= 8) return;
                    }
                    aw4 = Av1Tables.BlockDimensions[r2.Bs, 0];
                    mask <<= aw4;
                }
            }
        }
        if (haveLeft)
        {
            var leftRow = r[rowIdx];
            if (leftRow == null) return;
            int ry = rowIdx;
            var r2 = leftRow[bx - 1];
            if (Matches(r2))
            {
                masks[1] |= 1;
                if (++count >= 8) return;
            }
            int lh4 = Av1Tables.BlockDimensions[r2.Bs, 1];
            if (lh4 >= bh4)
            {
                if ((by & (lh4 - 1)) != 0) haveTopLeft = false;
            }
            else
            {
                long mask = 1L << lh4;
                for (int y = lh4; y < h4; y += lh4)
                {
                    ry += lh4;
                    var ryRow = r[ry];
                    if (ryRow == null) break;
                    r2 = ryRow[bx - 1];
                    if (Matches(r2))
                    {
                        masks[1] |= mask;
                        if (++count >= 8) return;
                    }
                    lh4 = Av1Tables.BlockDimensions[r2.Bs, 1];
                    mask <<= lh4;
                }
            }
        }
        if (haveTopLeft)
        {
            var tlRow = r[rowIdx - 1];
            if (tlRow != null && Matches(tlRow[bx - 1]))
            {
                masks[1] |= 1L << 32;
                if (++count >= 8) return;
            }
        }
        if (haveTopRight)
        {
            var trRow = r[rowIdx - 1];
            if (trRow != null && Matches(trRow[bx + bw4]))
                masks[0] |= 1L << 32;
        }
    }

    /// <summary>
    /// Port of dav1d's get_filter_ctx (env.h:135-154).
    /// Computes switchable filter context from above/left neighbors.
    /// </summary>
    // dav1d assign_comp_mv: NEAREST/NEAR take the stack entry (precision-fixed), GLOBAL the global motion vector at
    // this block, NEW the stack entry plus a coded residual.
    private static Av1MotionVector AssignCompMv(Av1TileState ts, ref Av1Msac msac, Av1DecoderFrameHeader fh,
        scoped ReadOnlySpan<Av1RefMvsCandidate> mvstack, int im, int idx, int refFrame, int drl,
        int bx, int by, int bw4, int bh4, ref bool hasSubpelFilter)
    {
        Av1MotionVector mv;
        switch ((Av1InterPredMode)im)
        {
            case Av1InterPredMode.NearMv:
            case Av1InterPredMode.NearestMv:
                mv = idx == 0 ? mvstack[drl].Mv.Mv0 : mvstack[drl].Mv.Mv1;
                Av1RefMvs.FixMvPrecision(fh, ref mv);
                break;
            case Av1InterPredMode.GlobalMv:
                hasSubpelFilter |= fh.Gmv[refFrame].Type == Av1WarpedMotionType.Translation;
                mv = Av1RefMvs.GetGmv2d(in fh.Gmv[refFrame], bx, by, bw4, bh4, fh);
                break;
            default: // NEWMV
                mv = idx == 0 ? mvstack[drl].Mv.Mv0 : mvstack[drl].Mv.Mv1;
                ReadMvResidual(ts, ref msac, ref mv, fh);
                break;
        }
        return mv;
    }

    private static bool HasUniComp(Av1BlockContextManaged e, int off) => (e.Ref0[off] < 4) == (e.Ref1[off] < 4);

    /// <summary>Compound direction context (dav1d get_comp_dir_ctx).</summary>
    private static int GetCompDirCtx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        if (haveTop && haveLeft)
        {
            bool aIntra = above.Intra[bx4] != 0, lIntra = left.Intra[by4] != 0;
            if (aIntra && lIntra) return 2;
            if (aIntra || lIntra)
            {
                var edge = aIntra ? left : above;
                int off = aIntra ? by4 : bx4;
                if (edge.CompType[off] == 0) return 2;
                return 1 + 2 * (HasUniComp(edge, off) ? 1 : 0);
            }
            bool aComp = above.CompType[bx4] != 0, lComp = left.CompType[by4] != 0;
            int aRef0 = above.Ref0[bx4], lRef0 = left.Ref0[by4];
            if (!aComp && !lComp)
                return 1 + 2 * ((aRef0 >= 4) == (lRef0 >= 4) ? 1 : 0);
            if (!aComp || !lComp)
            {
                var edge = aComp ? above : left;
                int off = aComp ? bx4 : by4;
                if (!HasUniComp(edge, off)) return 1;
                return 3 + ((aRef0 >= 4) == (lRef0 >= 4) ? 1 : 0);
            }
            bool aUni = HasUniComp(above, bx4), lUni = HasUniComp(left, by4);
            if (!aUni && !lUni) return 0;
            if (!aUni || !lUni) return 2;
            return 3 + ((aRef0 == 4) == (lRef0 == 4) ? 1 : 0);
        }
        if (haveTop || haveLeft)
        {
            var edge = haveLeft ? left : above;
            int off = haveLeft ? by4 : bx4;
            if (edge.Intra[off] != 0) return 2;
            if (edge.CompType[off] == 0) return 2;
            return 4 * (HasUniComp(edge, off) ? 1 : 0);
        }
        return 2;
    }

    /// <summary>Distance-weighted compound context (dav1d get_jnt_comp_ctx).</summary>
    private static int GetJntCompCtx(int orderHintNBits, int poc, int ref0Poc, int ref1Poc,
        Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4)
    {
        int d0 = Math.Abs(Av1ObuParser.GetPocDiff(orderHintNBits, ref0Poc, poc));
        int d1 = Math.Abs(Av1ObuParser.GetPocDiff(orderHintNBits, poc, ref1Poc));
        int offset = d0 == d1 ? 1 : 0;
        int aCtx = above.CompType[bx4] >= (byte)Av1CompInterType.Average || above.Ref0[bx4] == 6 ? 1 : 0;
        int lCtx = left.CompType[by4] >= (byte)Av1CompInterType.Average || left.Ref0[by4] == 6 ? 1 : 0;
        return 3 * offset + aCtx + lCtx;
    }

    /// <summary>Masked-compound context (dav1d get_mask_comp_ctx).</summary>
    private static int GetMaskCompCtx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4)
    {
        int aCtx = above.CompType[bx4] >= (byte)Av1CompInterType.Seg ? 1 : above.Ref0[bx4] == 6 ? 3 : 0;
        int lCtx = left.CompType[by4] >= (byte)Av1CompInterType.Seg ? 1 : left.Ref0[by4] == 6 ? 3 : 0;
        return Math.Min(aCtx + lCtx, 5);
    }

    /// <summary>Unidirectional compound LAST2 vs LAST3/GOLDEN context (dav1d av1_get_uni_p1_ctx).</summary>
    private static int GetUniP1Ctx(Av1BlockContextManaged above, Av1BlockContextManaged left, int by4, int bx4, bool haveTop, bool haveLeft)
    {
        int c0 = 0, c1 = 0, c2 = 0;
        void Count(int r) { if (r == 1) c0++; else if (r == 2) c1++; else if (r == 3) c2++; }
        if (haveTop && above.Intra[bx4] == 0)
        {
            Count(above.Ref0[bx4]);
            if (above.CompType[bx4] != 0) Count(above.Ref1[bx4]);
        }
        if (haveLeft && left.Intra[by4] == 0)
        {
            Count(left.Ref0[by4]);
            if (left.CompType[by4] != 0) Count(left.Ref1[by4]);
        }
        c1 += c2;
        return c0 == c1 ? 1 : c0 < c1 ? 0 : 2;
    }

    private static int GetFilterCtx(Av1BlockContextManaged above, Av1BlockContextManaged left,
        bool comp, int dir, int refIdx, int yb4, int xb4)
    {
        int nFilters = Av1Tables.NSwitchableFilters;
        int aFilter = (above.Ref0[xb4] == refIdx || above.Ref1[xb4] == refIdx)
            ? (dir == 0 ? above.Filter0[xb4] : above.Filter1[xb4]) : nFilters;
        int lFilter = (left.Ref0[yb4] == refIdx || left.Ref1[yb4] == refIdx)
            ? (dir == 0 ? left.Filter0[yb4] : left.Filter1[yb4]) : nFilters;

        int baseIdx = comp ? 4 : 0;
        if (aFilter == lFilter)
            return baseIdx + aFilter;
        if (aFilter == nFilters)
            return baseIdx + lFilter;
        if (lFilter == nFilters)
            return baseIdx + aFilter;
        return baseIdx + nFilters;
    }

    /// <summary>
    /// Port of dav1d's derive_warpmv (decode.c): collects up to 8 projected motion samples from the neighbouring
    /// blocks that share this block's reference (the masks from FindMatchingRef), drops outliers against a size-based
    /// threshold, then fits the local affine warp (find_affine_int) and its shear parameters.
    /// </summary>
    private static void DeriveWarpmv(Av1TaskContext t, int bw4, int bh4,
        Span<long> masks, Av1MotionVector mv)
    {
        var r = t.Rt.R;
        int ri = (t.By & 31) + 5;
        var pts = new int[8 * 4];
        int np = 0;
        void AddSample(int dx, int dy, int sx, int sy, in Av1RefMvsBlock rp)
        {
            int w4 = Av1Tables.BlockDimensions[rp.Bs, 0], h4 = Av1Tables.BlockDimensions[rp.Bs, 1];
            pts[np * 4 + 0] = 16 * (2 * dx + sx * w4) - 8;
            pts[np * 4 + 1] = 16 * (2 * dy + sy * h4) - 8;
            pts[np * 4 + 2] = pts[np * 4 + 0] + rp.Mv.Mv0.X;
            pts[np * 4 + 3] = pts[np * 4 + 1] + rp.Mv.Mv0.Y;
            np++;
        }
        ulong m0 = (ulong)masks[0], m1 = (ulong)masks[1];
        if ((uint)m0 == 1 && (m1 >> 32) == 0)
        {
            ref var above = ref r[ri - 1]![t.Bx];
            int off = t.Bx & (Av1Tables.BlockDimensions[above.Bs, 0] - 1);
            AddSample(-off, 0, 1, -1, above);
        }
        else
        {
            uint xmask = (uint)m0;
            for (int off = 0; np < 8 && xmask != 0;)
            {
                int tz = System.Numerics.BitOperations.TrailingZeroCount(xmask);
                off += tz;
                xmask >>= tz;
                AddSample(off, 0, 1, -1, r[ri - 1]![t.Bx + off]);
                xmask &= ~1u;
            }
        }
        if (np < 8 && m1 == 1)
        {
            int off = t.By & (Av1Tables.BlockDimensions[r[ri]![t.Bx - 1].Bs, 1] - 1);
            AddSample(0, -off, -1, 1, r[ri - off]![t.Bx - 1]);
        }
        else
        {
            uint ymask = (uint)m1;
            for (int off = 0; np < 8 && ymask != 0;)
            {
                int tz = System.Numerics.BitOperations.TrailingZeroCount(ymask);
                off += tz;
                ymask >>= tz;
                AddSample(0, off, -1, 1, r[ri + off]![t.Bx - 1]);
                ymask &= ~1u;
            }
        }
        if (np < 8 && (m1 >> 32) != 0) AddSample(0, 0, -1, -1, r[ri - 1]![t.Bx - 1]);        // top-left
        if (np < 8 && (m0 >> 32) != 0) AddSample(bw4, 0, 1, -1, r[ri - 1]![t.Bx + bw4]);     // top-right

        // Keep the samples whose motion is close to this block's (threshold by block size).
        Span<int> mvd = stackalloc int[8];
        int ret = 0, thresh = 4 * Math.Clamp(Math.Max(bw4, bh4), 4, 28);
        for (int i = 0; i < np; i++)
        {
            mvd[i] = Math.Abs(pts[i * 4 + 2] - pts[i * 4 + 0] - mv.X) + Math.Abs(pts[i * 4 + 3] - pts[i * 4 + 1] - mv.Y);
            if (mvd[i] > thresh) mvd[i] = -1;
            else ret++;
        }
        if (ret == 0) ret = 1;
        else
        {
            for (int i = 0, j = np - 1, k = 0; k < np - ret; k++, i++, j--)
            {
                while (mvd[i] != -1) i++;
                while (mvd[j] == -1) j--;
                if (i > j) break;
                mvd[i] = mvd[j];
                for (int q = 0; q < 4; q++) pts[i * 4 + q] = pts[j * 4 + q];
            }
        }

        var wm = t.WarpMv;
        if (!Av1WarpMv.FindAffineInt(pts, ret, bw4, bh4, mv, ref wm, t.Bx, t.By) && !Av1WarpMv.GetShearParams(ref wm))
            wm.Type = Av1WarpedMotionType.Affine;
        else
            wm.Type = Av1WarpedMotionType.Identity;
        t.WarpMv = wm;
    }

    // ========================================================================
    // Utility
    // ========================================================================

    /// <summary>Unsigned integer log2 (floor). Returns 0 for input 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ulog2(int v)
    {
        int r = 0;
        while (v > 1) { v >>= 1; r++; }
        return r;
    }
}

