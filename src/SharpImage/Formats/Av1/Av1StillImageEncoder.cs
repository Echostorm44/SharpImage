// Minimal AV1 still-image (AVIF codestream) encoder — step 2 of the AV1 encoder marathon. Produces a single
// key frame of one 64x64 monochrome superblock: PARTITION_NONE, DC intra prediction, skip=1 (zero residual).
// The decoded result is a flat DC-predicted plane (128 for 8-bit with no neighbors). This proves the full
// pipeline — OBU framing, uncompressed headers (Av1ObuWriter), and MSAC tile coding (Av1MsacWriter) driven by
// the real default CDFs and context derivation — round-trips through our Av1Decoder. Larger frames, real
// residual, and mode decisions come in later steps.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

internal static class Av1StillImageEncoder
{
    // A/B toggle: rate-distortion leaf decision (mode + tx-type via EstimateCoefBits) vs the SATD-only baseline.
    internal static bool UseRd = true;

    /// <summary>Encodes a flat DC-only monochrome key frame at <paramref name="width"/>x<paramref name="height"/>
    /// (must fit in a single 64x64 superblock). Returns the AV1 temporal unit (temporal delimiter + sequence
    /// header + OBU_FRAME). <paramref name="dcLevel"/> is the quantized DC coefficient level: 0 codes skip=1
    /// (flat 128 plane); 1 or 2 codes a single DC coefficient (uniform non-128 plane). Larger levels need the
    /// base-range (HiTok) path, not yet implemented.</summary>
    internal static byte[] EncodeFlatMonochrome(int width, int height, int baseQIdx = 40, int dcLevel = 0, bool dcNegative = false)
    {
        if (width < 1 || width > 64 || height < 1 || height > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Step-2/3 encoder supports a single 64x64 superblock (1..64).");
        }

        if (dcLevel < 0 || dcLevel > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(dcLevel), "dcLevel must be 0 (skip), 1, or 2 (HiTok path not implemented).");
        }

        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true);
        byte[] seqPayload = Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, seqPayload);

        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Frame {width}x{height} is not a single square block.");
        }

        // Build the coefficient array (rc-indexed for the block's transform) for a single DC coefficient.
        int[]? coeffs = null;
        if (dcLevel > 0)
        {
            coeffs = new int[Av1Tables.Scans[plan.Tx].Length];
            coeffs[0] = dcNegative ? -dcLevel : dcLevel;
        }

        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleBlockTile(baseQIdx, coeffs, plan);

        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);

        byte[] tdObu = Av1ObuWriter.WrapObu(Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);

        var outBytes = new byte[tdObu.Length + seqObu.Length + frameObu.Length];
        int o = 0;
        tdObu.CopyTo(outBytes, o); o += tdObu.Length;
        seqObu.CopyTo(outBytes, o); o += seqObu.Length;
        frameObu.CopyTo(outBytes, o);
        return outBytes;
    }

    /// <summary>Encodes a tightly-packed 64x64 monochrome luma image as a raw AV1 temporal unit (TD + seq +
    /// OBU_FRAME) — DC prediction, forward transform, quant, coefficient coding. Lossy.</summary>
    internal static byte[] EncodeMonochromeImage64(ReadOnlySpan<byte> pixels, int baseQIdx)
    {
        TryResolveSingleBlock(64, 64, out BlockPlan plan);
        return EncodeMonochromeWithCoeffs(64, 64, baseQIdx, QuantizeBlock(pixels, 64, 64, plan, baseQIdx));
    }

    // A single square intra block covering the frame, reached from the 64x64 superblock via forced partition
    // splits (which emit no symbols). Only PARTITION_NONE at the target level is coded.
    private readonly struct BlockPlan
    {
        public readonly Av1BlockLevel Bl;   // partition level of the coded block
        public readonly int Bs;             // block size ordinal (Av1BlockSize)
        public readonly int Tx;             // luma transform size ordinal
        public readonly int NPart;          // partition symbol count at this level (PartitionTypeCount[bl])
        public readonly int BlockPx;        // block dimension in pixels (8/16/32/64)

        public BlockPlan(Av1BlockLevel bl, int bs, int tx, int nPart, int blockPx)
        {
            Bl = bl;
            Bs = bs;
            Tx = tx;
            NPart = nPart;
            BlockPx = blockPx;
        }

        // Chroma transform ordinal for I420 (subsampled) — the largest chroma tx for this block size.
        public int ChromaTxI420 => Av1Tables.MaxTxfmSizeForBlockSize[Bs, (int)Av1PixelLayout.I420];

        // CfL is allowed for blocks <= 32x32.
        public bool CflAllowed => ((Av1Tables.CflAllowedMask >> Bs) & 1) != 0;
    }

    // Resolves the single square block that covers a width x height frame, or false if the frame needs a
    // rectangular / multi-block partition we don't yet emit. Mirrors the decoder's forced-split rule at the
    // superblock root: while neither dimension exceeds hsz the block is force-split to the next level.
    private static bool TryResolveSingleBlock(int width, int height, out BlockPlan plan)
    {
        plan = default;
        int width4 = (width + 3) >> 2;
        int height4 = (height + 3) >> 2;

        for (int bl = 1; bl <= 4; bl++)
        {
            int hsz = 16 >> bl;
            bool haveH = width4 > hsz;
            bool haveV = height4 > hsz;
            if (!haveH && !haveV)
            {
                continue; // forced split to bl+1 (no symbol)
            }

            // First level with a real partition decision. We can code PARTITION_NONE only when the full range is
            // available (both splits) and the block covers the whole frame.
            if (!haveH || !haveV)
            {
                return false;
            }

            int blockPx = 64 >> (bl - 1);
            if (width > blockPx || height > blockPx)
            {
                return false;
            }

            (int bs, int tx) = bl switch
            {
                1 => ((int)Av1BlockSize.Bs64x64, 4),
                2 => (7 /*Bs32x32*/, 3),
                3 => (12 /*Bs16x16*/, 2),
                _ => (17 /*Bs8x8*/, 1),
            };
            plan = new BlockPlan((Av1BlockLevel)bl, bs, tx, Av1Tables.PartitionTypeCount[bl], blockPx);
            return true;
        }

        return false;
    }

    private static byte[] EncodeSingleBlockTile(int baseQIdx, int[]? coeffs, in BlockPlan plan)
    {
        // qcat selects the coefficient CDF set; must match the decoder's derivation from the segment q index.
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);

        var w = new Av1MsacWriter();

        // Forced splits from the 64x64 root down to plan.Bl emit NO symbols. At plan.Bl, ctx=0 (no neighbours;
        // reset_context fills Partition=0). PARTITION_NONE = 0. Decoder: DecodeSymbolAdapt(partCdf, NPart).
        w.EncodeSymbolAdapt(cdf.GetPartitionCdf(plan.Bl, 0), 0, plan.NPart);

        // Skip flag, ctx=0 (above/left skip = 0). skip=0 ⇒ residual coded. CDEF disabled ⇒ no CDEF bits.
        int skip = coeffs == null ? 1 : 0;
        w.EncodeBoolAdapt(cdf.GetSkipCdf(0), (uint)skip);

        // Keyframe Y mode, contexts 0/0 (neighbour modes DC). DC_PRED = 0.
        w.EncodeSymbolAdapt(cdf.GetKfYModeCdf(0, 0), 0, 12);

        if (skip == 0)
        {
            // Single square luma transform (DctDct), DC intra mode. First block ⇒ skip/dc-sign contexts are 0.
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, plan.Tx, chroma: 0, yMode: 0, coeffs!);
        }

        return w.Finish();
    }

    private const int Tx64x64 = 4;
    private const int Tx32x32 = 3;

    /// <summary>Encodes a single-block I420 colour image (near-square even size, mapping to one square luma block
    /// of 8/16/32/64) as a complete .avif. <paramref name="luma"/> is w x h; <paramref name="u"/>/<paramref
    /// name="v"/> are (w/2) x (h/2) subsampled chroma. DC intra for luma and chroma.</summary>
    internal static byte[] EncodeAvifColor(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx)
    {
        if (width % 2 != 0 || height % 2 != 0 || !TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new NotSupportedException(
                $"Colour AVIF encode requires an even near-square size mapping to one square block (got {width}x{height}).");
        }

        byte[] tile = EncodeColorTile(luma, u, v, width, height, plan, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: false);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, 1, 1, monochrome: false);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false);
    }

    private static byte[] EncodeColorTile(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, in BlockPlan plan, int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int dcDq = Av1Tables.DequantTable[0, baseQIdx, 0];
        int acDq = Av1Tables.DequantTable[0, baseQIdx, 1]; // U/V share Y's dq (no separate_uv_delta_q)

        int n = plan.BlockPx;
        int cn = n / 2;
        int lumaTx = plan.Tx;
        int chromaTx = plan.ChromaTxI420;
        int cw = width / 2;
        int ch = height / 2;

        int[] yCoeffs = QuantizeResidualBlock(luma, width, height, n, lumaTx, dcDq, acDq);
        int[] uCoeffs = QuantizeResidualBlock(u, cw, ch, cn, chromaTx, dcDq, acDq);
        int[] vCoeffs = QuantizeResidualBlock(v, cw, ch, cn, chromaTx, dcDq, acDq);
        bool anyNz = HasNonZero(yCoeffs) || HasNonZero(uCoeffs) || HasNonZero(vCoeffs);
        int skip = anyNz ? 0 : 1;

        int uvMaxSym = Av1Constants.NumUvIntraPredModes - 1 - (plan.CflAllowed ? 0 : 1);

        var w = new Av1MsacWriter();
        w.EncodeSymbolAdapt(cdf.GetPartitionCdf(plan.Bl, 0), 0, plan.NPart);        // PARTITION_NONE
        w.EncodeBoolAdapt(cdf.GetSkipCdf(0), (uint)skip);                        // skip, ctx 0
        w.EncodeSymbolAdapt(cdf.GetKfYModeCdf(0, 0), 0, 12);                        // Y mode = DC
        w.EncodeSymbolAdapt(cdf.GetUvModeCdf(plan.CflAllowed, 0), 0, uvMaxSym);     // UV mode = DC

        if (skip == 0)
        {
            var neutral = new byte[32];
            Array.Fill(neutral, (byte)0x40);
            ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[chromaTx];
            int chromaSkipCtx = Av1CoeffDecode.GetSkipCtx(in uvtDim, plan.Bs,
                neutral, neutral, chroma: 1, layout: (int)Av1PixelLayout.I420);

            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, lumaTx, chroma: 0, yMode: 0, yCoeffs);
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, chromaTx, chroma: 1, yMode: 0, uCoeffs, skipCtx: chromaSkipCtx);
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, chromaTx, chroma: 1, yMode: 0, vCoeffs, skipCtx: chromaSkipCtx);
        }

        return w.Finish();
    }

    // Forward-transforms and quantizes an n x n block (DC prediction 128) built from a srcW x srcH plane with
    // edge replication beyond the frame.
    private static int[] QuantizeResidualBlock(ReadOnlySpan<byte> src, int srcW, int srcH, int n, int tx, int dcDq, int acDq)
    {
        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(y, srcH - 1);
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(x, srcW - 1);
                residual[y * n + x] = src[sy * srcW + sx] - 128;
            }
        }

        return Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, Av1Tables.Scans[tx].Length);
    }

    private static bool HasNonZero(int[] a)
    {
        foreach (int c in a)
        {
            if (c != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Encodes a monochrome image whose frame is a 1..2 by 1..2 grid of full 64x64 superblocks (width
    /// and height each 64 or 128) as a complete .avif. Unlike the single-block path this codes each superblock as
    /// PARTITION_NONE with real DC prediction from reconstructed neighbours and neighbour DC-sign contexts,
    /// reconstructing as it goes. Capped at 2x2 SBs because the decoder's above context holds only two SBs.</summary>
    internal static byte[] EncodeAvifMonochromeMultiSb(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx)
    {
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        byte[] padded = PadPlane(luma, width, height, pw, ph);
        byte[] tile = EncodeMultiSbTile(padded, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, monochrome: true, txModeSelect: true);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true);
    }

    /// <summary>Multi-superblock I420 COLOUR: a 1..2 x 1..2 grid of full 64x64 superblocks (64 or 128 each side),
    /// coding luma + subsampled chroma with cross-block DC prediction and reconstruct-as-you-go on all three
    /// planes.</summary>
    internal static byte[] EncodeAvifColorMultiSb(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx)
    {
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        int cwIn = (width + 1) >> 1, chIn = (height + 1) >> 1;   // ceil — matches RgbToI420's chroma size (odd dims)
        byte[] padY = PadPlane(luma, width, height, pw, ph);
        byte[] padU = PadPlane(u, cwIn, chIn, pw / 2, ph / 2);
        byte[] padV = PadPlane(v, cwIn, chIn, pw / 2, ph / 2);
        byte[] tile = EncodeMultiSbColorTile(padY, padU, padV, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: false);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, monochrome: false);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false);
    }

    // Per-superblock recursive-partition state for I420 colour. Extends the grayscale scheme with two chroma
    // planes (half resolution): chroma follows the luma partition tree, each leaf coding U/V at half the luma
    // block size (down to 4x4 chroma for an 8x8 luma leaf).
    private sealed class ColorPartCtx
    {
        public Av1MsacWriter Msac = null!;
        public Av1CdfContext Cdf = null!;
        public byte[] Luma = null!, U = null!, V = null!;
        public byte[] ReconY = null!, ReconU = null!, ReconV = null!;
        public int W, Cw, Chh;         // luma stride, chroma stride, chroma height
        public int Bw4, Bh4;           // REAL luma frame dims in 4-units
        public int DcDq, AcDq;
        public byte[] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!;
        public byte[] LeftPart = null!, LLY = null!, LCU = null!, LCV = null!, LModeY = null!, LSkip = null!;
        public byte[] Pred = new byte[64 * 64];
        public byte[] EstScratch = new byte[64 * 64];
    }

    private static byte[] EncodeMultiSbColorTile(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> uPlane, ReadOnlySpan<byte> vPlane,
        int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int cw = w / 2, chh = h / 2;

        int sb128Cols = (sbCols + 1) >> 1;
        var abovePart = new byte[sb128Cols][];
        var aLY = FilledArray(sb128Cols); var aCU = FilledArray(sb128Cols); var aCV = FilledArray(sb128Cols);
        var aModeY = new byte[sb128Cols][]; var aSkip = new byte[sb128Cols][];
        for (int i = 0; i < sb128Cols; i++) { abovePart[i] = new byte[16]; aModeY[i] = new byte[32]; aSkip[i] = new byte[32]; }

        byte[] lumaArr = new byte[w * h]; luma.CopyTo(lumaArr);
        byte[] uArr = new byte[cw * chh]; uPlane.CopyTo(uArr);
        byte[] vArr = new byte[cw * chh]; vPlane.CopyTo(vArr);

        var c = new ColorPartCtx
        {
            Msac = new Av1MsacWriter(), Cdf = cdf, Luma = lumaArr, U = uArr, V = vArr,
            ReconY = new byte[w * h], ReconU = new byte[cw * chh], ReconV = new byte[cw * chh],
            W = w, Cw = cw, Chh = chh, Bw4 = bw4, Bh4 = bh4,
            DcDq = Av1Tables.DequantTable[0, baseQIdx, 0], AcDq = Av1Tables.DequantTable[0, baseQIdx, 1],
        };

        for (int sby = 0; sby < sbRows; sby++)
        {
            c.LeftPart = new byte[16]; c.LLY = Filled(32); c.LCU = Filled(32); c.LCV = Filled(32);
            c.LModeY = new byte[32]; c.LSkip = new byte[32];
            for (int sbx = 0; sbx < sbCols; sbx++)
            {
                int col = sbx >> 1;
                c.AbovePart = abovePart[col]; c.ALY = aLY[col]; c.ACU = aCU[col]; c.ACV = aCV[col];
                c.AModeY = aModeY[col]; c.ASkip = aSkip[col];
                EncodePartitionColor(c, 1, sbx * 16, sby * 16);
            }
        }

        return c.Msac.Finish();
    }

    private static void EncodePartitionColor(ColorPartCtx c, int bl, int bx4, int by4)
    {
        int hsz = 16 >> bl, blk4 = 32 >> bl;
        bool haveH = c.Bw4 > bx4 + hsz;
        bool haveV = c.Bh4 > by4 + hsz;

        int bx8 = (bx4 & 31) >> 1, by8 = (by4 & 31) >> 1;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);
        Span<ushort> partCdf = c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
        int nPart = Av1Tables.PartitionTypeCount[bl];

        // Edge handling mirrors the gray path (force SPLIT at edges; the tree is luma-driven, chroma follows).
        if (!haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            EncodePartitionColor(c, bl + 1, bx4, by4);
            return;
        }
        if (haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherTopPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartitionColor(c, bl + 1, bx4, by4);
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4);
            return;
        }
        if (!haveH && haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherLeftPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartitionColor(c, bl + 1, bx4, by4);
            EncodePartitionColor(c, bl + 1, bx4, by4 + hsz);
            return;
        }

        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        bool doSplit = false;
        if (bl < 4 && fullyInside)
        {
            long costNone = EstimateBlockCost(c.Luma, c.W, c.Bw4, c.Bh4, c.DcDq, c.AcDq, c.EstScratch, bl, bx4, by4);
            long costSplit = 0;
            foreach ((int dx, int dy) in new[] { (0, 0), (hsz, 0), (0, hsz), (hsz, hsz) })
                costSplit += EstimateBlockCost(c.Luma, c.W, c.Bw4, c.Bh4, c.DcDq, c.AcDq, c.EstScratch, bl + 1, bx4 + dx, by4 + dy);
            doSplit = costSplit < costNone;
        }

        if (doSplit)
        {
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
            EncodePartitionColor(c, bl + 1, bx4, by4);
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4);
            EncodePartitionColor(c, bl + 1, bx4, by4 + hsz);
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4 + hsz);
            return;
        }

        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
        EncodeLeafBlockColor(c, bl, bx4, by4, blk4);

        byte aboveVal = Av1Tables.AboveLeftPartCtx[0, bl, (int)Av1BlockPartition.None];
        byte leftVal = Av1Tables.AboveLeftPartCtx[1, bl, (int)Av1BlockPartition.None];
        for (int i = 0; i < hsz && bx8 + i < 16; i++) c.AbovePart[bx8 + i] = aboveVal;
        for (int j = 0; j < hsz && by8 + j < 16; j++) c.LeftPart[by8 + j] = leftVal;
    }

    private static void EncodeLeafBlockColor(ColorPartCtx c, int bl, int bx4, int by4, int blk4)
    {
        int n = blk4 * 4, cn = n / 2;
        int tx = BlToTx(bl), ctx0 = tx - 1; // chroma tx = one size smaller (I420)
        int bs = BlToBs(bl);
        int bxR = bx4 & 31, byR = by4 & 31;
        int cxR = bxR >> 1, cyR = byR >> 1;      // chroma context index (4-unit)
        int cblk4 = Math.Max(1, blk4 >> 1);
        int bx = bx4 * 4, by = by4 * 4, cbx = bx4 * 2, cby = by4 * 2; // pixel positions
        bool cflAllowed = bl >= 2;               // blocks <= 32x32
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);

        // Luma: rate-distortion mode + tx-type decision (from reconstruction). Writes prediction into c.Pred.
        var rd = ChooseLeafRdCore(c.ReconY, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AModeY[bxR], c.LModeY[byR], c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR), c.Pred);
        Av1IntraPredMode yMode = rd.Mode; int yDelta = rd.Delta;
        int[] yC = rd.Coeffs; Av1TxType yInv = rd.Inv; int yTxIdx = rd.Idx;
        int dcU = DcPredict(c.ReconU, c.Cw, c.Chh, cbx, cby, cn, cn);
        int dcV = DcPredict(c.ReconV, c.Cw, c.Chh, cbx, cby, cn, cn);
        int[] uC = ForwardResidual(c.U, c.Cw, cbx, cby, cn, dcU, c.DcDq, c.AcDq, Av1Tables.Scans[ctx0].Length);
        int[] vC = ForwardResidual(c.V, c.Cw, cbx, cby, cn, dcV, c.DcDq, c.AcDq, Av1Tables.Scans[ctx0].Length);
        int skip = (HasNonZero(yC) || HasNonZero(uC) || HasNonZero(vC)) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        int yAboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]];
        int yLeftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(yAboveCtx, yLeftCtx), (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode), 0, uvNsym); // UV DC

        byte cfY = 0x40, cfU = 0x40, cfV = 0x40;
        if (skip == 0)
        {
            ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[ctx0];
            int uSkip = Av1CoeffDecode.GetSkipCtx(in uvtDim, bs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int vSkip = Av1CoeffDecode.GetSkipCtx(in uvtDim, bs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int ySign = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
            int uSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int vSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));

            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, 0, (int)yMode, yC, dcSignCtx: ySign, txTypeIdx: yTxIdx);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);

            cfY = DequantAndReconstructPred(yC, tx, n, c.DcDq, c.AcDq, c.Pred, c.ReconY, c.W, bx, by, yInv);
            cfU = DequantAndReconstruct(uC, ctx0, cn, c.DcDq, c.AcDq, dcU, c.ReconU, c.Cw, cbx, cby);
            cfV = DequantAndReconstruct(vC, ctx0, cn, c.DcDq, c.AcDq, dcV, c.ReconV, c.Cw, cbx, cby);
        }
        else
        {
            for (int yy = 0; yy < n; yy++) Array.Copy(c.Pred, yy * n, c.ReconY, (by + yy) * c.W + bx, n);
            FillFlat(c.ReconU, c.Cw, cbx, cby, cn, dcU);
            FillFlat(c.ReconV, c.Cw, cbx, cby, cn, dcV);
        }

        int yW = Math.Min(blk4, c.Bw4 - bx4), yH = Math.Min(blk4, c.Bh4 - by4);
        int cW = Math.Min(cblk4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(cblk4, (c.Bh4 - by4 + 1) >> 1);
        for (int i = 0; i < yW && bxR + i < 32; i++) { c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; }
        for (int j = 0; j < yH && byR + j < 32; j++) { c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = cfU; c.ACV[cxR + i] = cfV; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = cfU; c.LCV[cyR + j] = cfV; }
    }

    private static byte[] Filled(int n)
    {
        var a = new byte[n];
        Array.Fill(a, (byte)0x40);
        return a;
    }

    private static byte[][] FilledArray(int count)
    {
        var a = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            a[i] = Filled(32);
        }

        return a;
    }

    private static int[] ForwardResidual(ReadOnlySpan<byte> plane, int planeW, int bx, int by, int n, int dcPred,
        int dcDq, int acDq, int scanLen)
    {
        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                residual[y * n + x] = plane[(by + y) * planeW + (bx + x)] - dcPred;
            }
        }

        return Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen);
    }

    // Validates a multi-SB frame and returns the SB grid, real 4-unit dims (bw4/bh4, for context clipping) and
    // padded pixel dims (pw/ph = SB-aligned). Every superblock must permit PARTITION_NONE (the edge SB's
    // in-frame remainder must exceed 32px), i.e. a dimension's remainder mod 64 is 0 or >32.
    private static void ValidateMultiSb(int w, int h, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph)
    {
        if (w < 8 || h < 8 || w > 4096 || h > 4096)
        {
            throw new NotSupportedException($"AVIF encode supports 8..4096 per dimension (got {w}x{h}).");
        }

        // Frame dims in 4-unit MI units: MiCols = 2*ceil(w/8) (always even), matching dav1d's f->bw. Using ceil(w/4)
        // instead would be odd for non-multiple-of-8 sizes and disagree with dav1d — the even MI grid lets 8x8
        // blocks tile the edges (no 4x4 needed) and is exactly what a conformant decoder derives from the header.
        bw4 = ((w + 7) >> 3) << 1;
        bh4 = ((h + 7) >> 3) << 1;
        sbCols = (bw4 + 15) >> 4;
        sbRows = (bh4 + 15) >> 4;
        pw = sbCols * 64;
        ph = sbRows * 64;
    }

    // Pads a plane to pw x ph by replicating the right/bottom edge.
    private static byte[] PadPlane(ReadOnlySpan<byte> src, int w, int h, int pw, int ph)
    {
        var padded = new byte[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(y, h - 1);
            for (int x = 0; x < pw; x++)
            {
                padded[y * pw + x] = src[sy * w + Math.Min(x, w - 1)];
            }
        }

        return padded;
    }

    private static void FillFlat(byte[] recon, int reconW, int bx, int by, int n, int value)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = (byte)value;
            }
        }
    }

    // Per-superblock recursive-partition encoder state. All context arrays mirror the decoder's Above/Left
    // block-context arrays: Above arrays are per-SB128 column (persist across SB rows), Left arrays reset per
    // SB row. 4-unit arrays are indexed by Bx4&31 / By4&31; the 8-unit partition array by (Bx4&31)>>1.
    private sealed class GrayPartCtx
    {
        public Av1MsacWriter Msac = null!;
        public Av1CdfContext Cdf = null!;
        public byte[] Luma = null!;   // padded plane
        public byte[] Recon = null!;  // padded plane (SB-aligned), reconstructed as-we-go
        public int W;                 // padded stride
        public int Bw4, Bh4;          // REAL frame dims in 4-units (partition decisions use these)
        public int DcDq, AcDq;
        public long SplitLambda;      // rate bias for the split decision, in SATD units

        // Above (per SB128 column), Left (per SB row).
        public byte[] AbovePart = null!, AboveLCoef = null!, AboveMode = null!, AboveSkip = null!;
        public byte[] LeftPart = null!, LeftLCoef = null!, LeftMode = null!, LeftSkip = null!;
        public sbyte[] AboveTxIntra = null!, LeftTxIntra = null!; // neighbour tx log-size, for the tx-depth context
        public byte[] Pred = new byte[64 * 64];
        public byte[] EstScratch = new byte[64 * 64];
    }

    private static sbyte[] FilledSbyte(int n, sbyte v) { var a = new sbyte[n]; Array.Fill(a, v); return a; }

    private static int BlToTx(int bl) => bl switch { 1 => 4, 2 => 3, 3 => 2, _ => 1 };            // TX size ordinal
    private static int BlToBs(int bl) => bl switch { 1 => 3, 2 => 7, 3 => 12, _ => 17 };          // Av1BlockSize ordinal

    // Codes the whole tile with a recursive partition tree (PARTITION_NONE / PARTITION_SPLIT down to 8x8),
    // reconstructing each leaf so later blocks predict from the pixels the decoder will produce. Only fully-inside
    // blocks are split; edge superblocks (non-multiple-of-64 frames) stay a single PARTITION_NONE 64x64 block, so
    // every partition decision is full-range and never needs the partial-edge bool path.
    private static byte[] EncodeMultiSbTile(ReadOnlySpan<byte> luma, int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int acDq = Av1Tables.DequantTable[0, baseQIdx, 1];

        int sb128Cols = (sbCols + 1) >> 1;
        var abovePart = new byte[sb128Cols][];
        var aboveLCoef = new byte[sb128Cols][];
        var aboveMode = new byte[sb128Cols][];
        var aboveSkip = new byte[sb128Cols][];
        var aboveTxIntra = new sbyte[sb128Cols][];
        for (int i = 0; i < sb128Cols; i++)
        {
            abovePart[i] = new byte[16];
            aboveLCoef[i] = Filled(32);
            aboveMode[i] = new byte[32];
            aboveSkip[i] = new byte[32];
            aboveTxIntra[i] = FilledSbyte(32, -1);
        }

        var ctx = new GrayPartCtx
        {
            Msac = new Av1MsacWriter(), Cdf = cdf, Luma = new byte[0], Recon = new byte[w * h], W = w,
            Bw4 = bw4, Bh4 = bh4, DcDq = Av1Tables.DequantTable[0, baseQIdx, 0], AcDq = acDq,
            // Extra split bias beyond the per-block header cost already in EstimateCost. Zero works well because
            // that header term already penalises the four sub-block headers a split introduces.
            SplitLambda = 0L,
        };
        // `Luma` is a ReadOnlySpan param; copy to a field-friendly array once.
        var lumaArr = new byte[w * h];
        luma.CopyTo(lumaArr);
        ctx.Luma = lumaArr;

        for (int sby = 0; sby < sbRows; sby++)
        {
            ctx.LeftPart = new byte[16];
            ctx.LeftLCoef = Filled(32);
            ctx.LeftMode = new byte[32];
            ctx.LeftSkip = new byte[32];
            ctx.LeftTxIntra = FilledSbyte(32, -1);
            for (int sbx = 0; sbx < sbCols; sbx++)
            {
                int col = sbx >> 1;
                ctx.AbovePart = abovePart[col];
                ctx.AboveLCoef = aboveLCoef[col];
                ctx.AboveMode = aboveMode[col];
                ctx.AboveSkip = aboveSkip[col];
                ctx.AboveTxIntra = aboveTxIntra[col];
                EncodePartition(ctx, 1 /*Bl64x64*/, sbx * 16, sby * 16);
            }
        }

        return ctx.Msac.Finish();
    }

    // Recursively encodes the partition tree for one block. bl is the Av1BlockLevel (1=64x64..4=8x8); bx4/by4 are
    // the block's absolute 4-unit position. Chooses PARTITION_NONE vs PARTITION_SPLIT by comparing the whole-block
    // residual SATD against the sum of the four quadrants' SATD (plus a rate bias).
    private static void EncodePartition(GrayPartCtx c, int bl, int bx4, int by4)
    {
        int hsz = 16 >> bl;          // half block in 4-units
        int blk4 = 32 >> bl;         // full block in 4-units
        int n = blk4 * 4;            // block pixels
        // Edge logic mirrors the decoder: haveH/haveV = there is room for the right/bottom half inside the frame.
        bool haveH = c.Bw4 > bx4 + hsz;
        bool haveV = c.Bh4 > by4 + hsz;

        int bx8 = (bx4 & 31) >> 1, by8 = (by4 & 31) >> 1;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);
        Span<ushort> partCdf = c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
        int nPart = Av1Tables.PartitionTypeCount[bl];

        // Both halves off-frame: forced SPLIT (no symbol), only the top-left child has in-frame content.
        if (!haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size in 4-units) — not yet implemented.");
            EncodePartition(c, bl + 1, bx4, by4);
            return;
        }

        // Bottom edge (room across, none below): split_or_horz — we always force SPLIT (avoids rectangular blocks).
        if (haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherTopPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartition(c, bl + 1, bx4, by4);
            EncodePartition(c, bl + 1, bx4 + hsz, by4);
            return;
        }
        // Right edge (room below, none across): split_or_vert — force SPLIT.
        if (!haveH && haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherLeftPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartition(c, bl + 1, bx4, by4);
            EncodePartition(c, bl + 1, bx4, by4 + hsz);
            return;
        }

        // Interior: full partition symbol. RD NONE-vs-SPLIT only for fully-inside blocks (a block that merely
        // extends past the frame with its midpoint inside stays a single NONE, matching prior behaviour).
        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        bool doSplit = false;
        if (bl < 4 && fullyInside)
        {
            long costNone = EstimateCost(c, bl, bx4, by4);
            long costSplit = c.SplitLambda;
            foreach ((int dx, int dy) in new[] { (0, 0), (hsz, 0), (0, hsz), (hsz, hsz) })
                costSplit += EstimateCost(c, bl + 1, bx4 + dx, by4 + dy);
            doSplit = costSplit < costNone;
        }

        if (doSplit)
        {
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
            EncodePartition(c, bl + 1, bx4, by4);
            EncodePartition(c, bl + 1, bx4 + hsz, by4);
            EncodePartition(c, bl + 1, bx4, by4 + hsz);
            EncodePartition(c, bl + 1, bx4 + hsz, by4 + hsz);
            return; // SPLIT nodes (bl<8x8) do not update partition context
        }

        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
        // Actual leaf: pick the best mode predicting from the real reconstruction, then code + reconstruct.
        (Av1IntraPredMode yMode, int yDelta, _) =
            ChooseIntraMode(c.Recon, c.W, c.Bw4, c.Bh4, bx4, by4, n, c.Luma, c.W, bx4 * 4, by4 * 4, c.Pred);
        EncodeLeafBlock(c, bl, bx4, by4, blk4, n, yMode, yDelta);

        // Partition context fill for the NONE leaf (mirrors DecodeSuperblock's AboveLeftPartCtx update).
        byte aboveVal = Av1Tables.AboveLeftPartCtx[0, bl, (int)Av1BlockPartition.None];
        byte leftVal = Av1Tables.AboveLeftPartCtx[1, bl, (int)Av1BlockPartition.None];
        int pcount = hsz; // = 1<<Ulog2(hsz), block width in 8-units
        for (int i = 0; i < pcount && bx8 + i < 16; i++) c.AbovePart[bx8 + i] = aboveVal;
        for (int j = 0; j < pcount && by8 + j < 16; j++) c.LeftPart[by8 + j] = leftVal;
    }

    // Reduces a square tx size `depth` times (each step to the next-smaller square, via TxfmDimensions.Sub).
    private static int ReduceTx(int tx, int depth) { for (int i = 0; i < depth; i++) tx = Av1Tables.TxfmDimensions[tx].Sub; return tx; }

    // Encodes one PARTITION_NONE leaf: skip flag, Y mode (+ angle_delta), tx size (TX_MODE_SELECT), then the
    // coefficients of each sub-transform block (with per-tx-block intra prediction + reconstruction), and the
    // above/left context fills. Chooses a tx depth (0/1/2) that minimizes estimated coding cost.
    private static void EncodeLeafBlock(GrayPartCtx c, int bl, int bx4, int by4, int blk4, int n,
        Av1IntraPredMode yMode, int yDelta)
    {
        int maxTx = BlToTx(bl);
        int bxR = bx4 & 31, byR = by4 & 31;
        ref readonly var maxTDim = ref Av1Tables.TxfmDimensions[maxTx];

        // Rate-distortion mode + tx-type decision at the block-size transform (also fixes the skip flag and the
        // prediction in c.Pred). Overrides the SATD mode passed from the partition search.
        int[] coeffs0; Av1TxType invTx0; int txIdx0;
        if (UseRd)
        {
            var rd = ChooseLeafRd(c, bx4, by4, n, maxTx);
            yMode = rd.Mode; yDelta = rd.Delta;
            coeffs0 = rd.Coeffs; invTx0 = rd.Inv; txIdx0 = rd.Idx;
        }
        else
        {
            PredictIntra(c.Recon, c.W, c.Bw4, c.Bh4, bx4, by4, n, yMode, yDelta, c.Pred);
            int[] res = ComputeResidualPred(c.Luma, c.W, bx4 * 4, by4 * 4, c.Pred, n);
            (coeffs0, invTx0, txIdx0) = ChooseTxType(res, n, maxTx, c.DcDq, c.AcDq);
        }
        int skip = HasNonZero(coeffs0) ? 0 : 1;

        // Choose tx depth (only when coding residual, block > 4x4, and fully inside the frame so every sub-tx block
        // is in-bounds — edge blocks in non-multiple-of-64 frames keep the single block-size transform).
        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        int maxDepth = (skip == 0 && fullyInside) ? Math.Min((int)maxTDim.Max, 2) : 0;
        int depth = 0;
        if (maxDepth > 0)
        {
            long bestCost = EstimateTxDepthCost(c, bx4, by4, blk4, maxTx, yMode, yDelta);
            for (int d = 1; d <= maxDepth; d++)
            {
                long cost = EstimateTxDepthCost(c, bx4, by4, blk4, ReduceTx(maxTx, d), yMode, yDelta);
                if (cost < bestCost) { bestCost = cost; depth = d; }
            }
        }
        int tx = ReduceTx(maxTx, depth);
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];

        int skipCtx = c.AboveSkip[bxR] + c.LeftSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);

        int aboveCtx = Av1Tables.IntraModeContext[c.AboveMode[bxR]];
        int leftCtx = Av1Tables.IntraModeContext[c.LeftMode[byR]];
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx), (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);

        // tx_depth is signalled for every intra block > 4x4 (read_tx_size allowSelect = !skip || !is_inter,
        // and is_inter is false), so it is coded for skip blocks too (depth 0). Gating on skip==0 desynced dav1d.
        if (maxTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LeftTxIntra[byR] >= maxTDim.Lh ? 1 : 0) + (c.AboveTxIntra[bxR] >= maxTDim.Lw ? 1 : 0);
            int nSym = Math.Min((int)maxTDim.Max, 2);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), depth, nSym);
        }

        if (skip == 0 && depth == 0)
        {
            // Single transform (block size): use the RD-chosen coefficients/tx-type directly.
            int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(maxTx, c.AboveLCoef.AsSpan(bxR), c.LeftLCoef.AsSpan(byR));
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, maxTx, chroma: 0, yMode: (int)yMode, coeffs0,
                skipCtx: 0, dcSignCtx: dcSignCtx, txTypeIdx: txIdx0);
            byte cfCtx0 = DequantAndReconstructPred(coeffs0, maxTx, n, c.DcDq, c.AcDq, c.Pred, c.Recon, c.W, bx4 * 4, by4 * 4, invTx0);
            int cwl = Math.Min(blk4, c.Bw4 - bx4), chl = Math.Min(blk4, c.Bh4 - by4);
            for (int i = 0; i < cwl && bxR + i < 32; i++) c.AboveLCoef[bxR + i] = cfCtx0;
            for (int j = 0; j < chl && byR + j < 32; j++) c.LeftLCoef[byR + j] = cfCtx0;
        }
        else if (skip == 0)
        {
            int txN = tDim.W * 4;          // tx pixel size
            int txW4 = tDim.W;             // tx 4-unit size
            var predBuf = new byte[txN * txN];
            // Per-tx-block: predict from reconstruction, code coeffs, reconstruct — in raster order.
            for (int iy = 0; iy < blk4; iy += txW4)
                for (int ix = 0; ix < blk4; ix += txW4)
                {
                    int cbx4 = bx4 + ix, cby4 = by4 + iy;
                    int cbxR = cbx4 & 31, cbyR = cby4 & 31;
                    PredictIntra(c.Recon, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, predBuf);
                    int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, predBuf, txN);
                    (int[] cf, Av1TxType inv, int idx) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq);
                    // Coeff-skip context is neighbour-based for sub-block transforms (0 only when tx == block size).
                    int coefSkipCtx = Av1CoeffDecode.GetSkipCtx(in tDim, BlToBs(bl), c.AboveLCoef.AsSpan(cbxR), c.LeftLCoef.AsSpan(cbyR), 0, 0);
                    int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, c.AboveLCoef.AsSpan(cbxR), c.LeftLCoef.AsSpan(cbyR));
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, chroma: 0, yMode: (int)yMode, cf,
                        skipCtx: coefSkipCtx, dcSignCtx: dcSignCtx, txTypeIdx: idx);
                    byte txCfCtx = DequantAndReconstructPred(cf, tx, txN, c.DcDq, c.AcDq, predBuf, c.Recon, c.W, cbx4 * 4, cby4 * 4, inv);
                    // LCoef context fill per tx block (clip to frame + 32-wide array).
                    int tcw = Math.Min(txW4, c.Bw4 - cbx4), tch = Math.Min(txW4, c.Bh4 - cby4);
                    for (int i = 0; i < tcw && cbxR + i < 32; i++) c.AboveLCoef[cbxR + i] = txCfCtx;
                    for (int j = 0; j < tch && cbyR + j < 32; j++) c.LeftLCoef[cbyR + j] = txCfCtx;
                }
        }
        else
        {
            for (int y = 0; y < n; y++)
                Array.Copy(c.Pred, y * n, c.Recon, (by4 * 4 + y) * c.W + bx4 * 4, n);
            int cwz = Math.Min(blk4, c.Bw4 - bx4), chz = Math.Min(blk4, c.Bh4 - by4);
            for (int i = 0; i < cwz && bxR + i < 32; i++) c.AboveLCoef[bxR + i] = 0x40;
            for (int j = 0; j < chz && byR + j < 32; j++) c.LeftLCoef[byR + j] = 0x40;
        }

        // Mode/skip/tx-size context fills over the whole coding block.
        int cw = Math.Min(blk4, c.Bw4 - bx4);
        int ch = Math.Min(blk4, c.Bh4 - by4);
        sbyte txLw = (sbyte)tDim.Lw, txLh = (sbyte)tDim.Lh;
        for (int i = 0; i < cw && bxR + i < 32; i++) { c.AboveMode[bxR + i] = (byte)yMode; c.AboveSkip[bxR + i] = (byte)skip; c.AboveTxIntra[bxR + i] = txLw; }
        for (int j = 0; j < ch && byR + j < 32; j++) { c.LeftMode[byR + j] = (byte)yMode; c.LeftSkip[byR + j] = (byte)skip; c.LeftTxIntra[byR + j] = txLh; }
    }

    // Estimates the coding cost of a leaf at a given (uniform) tx size, predicting each tx block from the SOURCE
    // plane (≈ what reconstruction will be). Used only for the tx-depth decision.
    private static long EstimateTxDepthCost(GrayPartCtx c, int bx4, int by4, int blk4, int tx, Av1IntraPredMode yMode, int yDelta)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
        int txN = tDim.W * 4, txW4 = tDim.W;
        long cost = 0;
        for (int iy = 0; iy < blk4; iy += txW4)
            for (int ix = 0; ix < blk4; ix += txW4)
            {
                int cbx4 = bx4 + ix, cby4 = by4 + iy;
                PredictIntra(c.Luma, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, c.EstScratch);
                int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, c.EstScratch, txN);
                (int[] cf, _, _) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq);
                cost += CoeffCost(cf) + 6; // per-tx-block overhead (all_zero + tx-type + eob)
            }

        return cost;
    }

    // Estimates a block's best-mode residual SATD for the split decision, predicting from the SOURCE plane so a
    // quadrant is scored the way it would predict after splitting (its reconstructed neighbours ≈ source). Decision
    // only — never affects the coded bitstream.
    // Estimates the coding cost (in bit-like units) of a block coded PARTITION_NONE with its best mode, predicting
    // from the SOURCE plane. Cost = a fixed per-block header + a coefficient term, so splitting is only chosen when
    // the sum of quadrant costs (each carrying its own header) beats coding the parent whole. Unlike raw SATD this
    // reflects that a smooth block, though it has residual energy, quantizes to very few coefficients and is cheap.
    private const int HeaderCostBits = 22;   // partition + skip + y-mode (+ angle) symbols, amortized
    private static long EstimateCost(GrayPartCtx c, int bl, int bx4, int by4)
        => EstimateBlockCost(c.Luma, c.W, c.Bw4, c.Bh4, c.DcDq, c.AcDq, c.EstScratch, bl, bx4, by4);

    // RD lagrangian weight: J = SSE + λ·bits, with λ ∝ quant-step². Tunable for A/B; scaled so the NONE/SPLIT
    // decision splits blocks whose transform cannot represent the detail (high distortion) but keeps large blocks
    // for smooth content (splitting only adds rate for no distortion gain).
    internal static double RdLambdaK = 0.02;

    // Extra multiplier on the RDOQ lambda relative to the partition lambda. The partition lambda is tuned for
    // whole-block decisions; coefficient RDOQ needs a larger effective lambda to trade a marginal coefficient's
    // small distortion against its (EOB-inclusive) coding rate. 20 was the sweet spot on real-photo luma: it
    // improves the RD curve (2-7% fewer bytes at matched RMSE, more at low quality) without hurting the floor.
    internal static double RdoqLambdaScale = 20.0;

    // Rate-DISTORTION coding-cost estimate for a luma block (see EncodePartition). Reconstructs the block through
    // the decoder's own inverse and returns J = SSE + λ·rate — so a 64x64 (or 32x32) transform that drops the
    // high-frequency detail of a sharp block is penalised by its reconstruction error, not just its (small) rate.
    // Used for the NONE/SPLIT decision in both the grayscale and colour encoders (the tree is luma-driven).
    private static long EstimateBlockCost(byte[] luma, int w, int bw4, int bh4, int dcDq, int acDq, byte[] scratch,
        int bl, int bx4, int by4)
    {
        int n = (32 >> bl) * 4;
        int tx = BlToTx(bl);
        ChooseIntraMode(luma, w, bw4, bh4, bx4, by4, n, luma, w, bx4 * 4, by4 * 4, scratch);
        int[] coeffs = ForwardResidualPred(luma, w, bx4 * 4, by4 * 4, scratch, n, dcDq, acDq, Av1Tables.Scans[tx].Length);
        long bits = HeaderCostBits;
        foreach (int v in coeffs)
            if (v != 0) { int a = Math.Abs(v); bits += 5 + (a >= 15 ? 8 : a >> 1); } // base+sign ~5b, magnitude tail

        // Reconstruct through the decoder's inverse (from the same source-plane prediction) and measure SSE.
        var reconTmp = new byte[n * n];
        DequantAndReconstructPred(coeffs, tx, n, dcDq, acDq, scratch, reconTmp, n, 0, 0);
        long sse = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int d = reconTmp[y * n + x] - luma[(by4 * 4 + y) * w + (bx4 * 4 + x)];
                sse += (long)d * d;
            }

        double lambda = RdLambdaK * acDq * acDq;
        return sse + (long)(lambda * bits);
    }

    // Intra modes tried per block, each as (mode, angle_delta). All are verified against libdav1d/ffmpeg.
    // The kf-y-mode symbol is coded with nsym = NumIntraPredModes - 1 = 12 (dav1d convention for a 13-symbol
    // alphabet, values 0..12), so Paeth (mode 12) round-trips like any other mode.
    //
    // Directional candidates are restricted to a "safe" subset whose prediction angle stays in [90,180], so the
    // implementation mode is ImplVert / ImplHor / ImplZ2 — all of which need only top/left/top-left edges, never
    // top-right or bottom-left. That makes the block's edge-availability flags (sbHasTr / sbHasBl, which depend on
    // frame-level superblock ordering) irrelevant to the prediction, so the encoder reproduces the decoder's
    // output bit-for-bit without replicating that logic. Directional prediction reuses the decoder's own
    // PrepareIntraEdges (which folds angle_delta into the base angle) + Av1IntraPred.Predict. angle_delta ∈ [-3,3]
    // is coded via AngleDeltaCdf[mode-Vertical], symbol = delta + 3, nsym 6 (7 symbols), for blocks ≥ 8x8.
    private static readonly (Av1IntraPredMode Mode, int Delta)[] CandidateModes = BuildCandidates();

    private static (Av1IntraPredMode, int)[] BuildCandidates()
    {
        var list = new List<(Av1IntraPredMode, int)>
        {
            (Av1IntraPredMode.Dc, 0), (Av1IntraPredMode.Smooth, 0),
            (Av1IntraPredMode.SmoothV, 0), (Av1IntraPredMode.SmoothH, 0), (Av1IntraPredMode.Paeth, 0),
        };
        // Z2-base directional modes: full angle_delta range keeps angle in (90,180).
        foreach (var m in new[] { Av1IntraPredMode.DiagDownRight, Av1IntraPredMode.VerticalRight, Av1IntraPredMode.HorizontalDown })
            for (int d = -3; d <= 3; d++) list.Add((m, d));
        for (int d = 0; d <= 3; d++) list.Add((Av1IntraPredMode.Vertical, d));    // angle 90..99 (delta≥0)
        for (int d = -3; d <= 0; d++) list.Add((Av1IntraPredMode.Horizontal, d));  // angle 171..180 (delta≤0)
        return list.ToArray();
    }

    // True for the 8 directional intra modes (Vertical..VerticalLeft) that carry an angle_delta symbol.
    private static bool IsDirectional(Av1IntraPredMode m) =>
        m >= Av1IntraPredMode.Vertical && m <= Av1IntraPredMode.VerticalLeft;

    // Predicts an n x n luma block with the given intra mode into dst (stride n), reusing the decoder's own edge
    // preparation + prediction so encoder and decoder agree bit-for-bit. recon is the reconstruction plane
    // (stride reconW), bx4/by4 the block position in 4-unit units, bw4/bh4 the frame size in 4-unit units.
    private static void PredictIntra(byte[] recon, int reconW, int bw4, int bh4, int bx4, int by4, int n,
        Av1IntraPredMode mode, int delta, byte[] dst)
    {
        Span<byte> edge = stackalloc byte[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = n >> 2;
        bool haveTop = by4 > 0;
        bool haveLeft = bx4 > 0;
        int angle = delta; // PrepareIntraEdges folds this into the base angle for directional modes
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, bw4, bh4, Av1EdgeFlags.None,
            recon, dstOff, reconW, default, mode, ref angle, tw4, tw4, filterEdge: false, edge, edgeCenter, 8);
        Av1IntraPred.Predict(m, dst, n, edge, edgeCenter, n, n, angle,
            4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4);
    }

    // Chooses the intra mode with the lowest residual SATD (sum of absolute Hadamard-transformed differences) —
    // a frequency-domain cost proxy that tracks DCT coding cost far better than raw SAD, so smooth ramps and
    // directional edges are scored the way the transform will actually code them. Returns the winning (mode,
    // angle_delta) and writes its prediction into predOut (n x n). Purely an encoder decision: any candidate is
    // a valid mode, so this can never desync the decoder.
    private static (Av1IntraPredMode Mode, int Delta, long Cost) ChooseIntraMode(byte[] recon, int reconW, int bw4, int bh4,
        int bx4, int by4, int n, ReadOnlySpan<byte> src, int srcW, int srcBx, int srcBy, byte[] predOut)
    {
        long best = long.MaxValue;
        (Av1IntraPredMode Mode, int Delta) bestCand = (Av1IntraPredMode.Dc, 0);
        var tmp = new byte[n * n];
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, tmp);
            long cost = Satd8x8(src, srcW, srcBx, srcBy, tmp, n);
            if (cost < best)
            {
                best = cost;
                bestCand = (mode, delta);
                Array.Copy(tmp, predOut, n * n);
            }
        }

        return (bestCand.Mode, bestCand.Delta, best);
    }

    // Sum of 8x8 Hadamard-transformed absolute residuals (src - pred) tiled over an n x n block. SATD is the
    // standard cheap frequency-domain proxy for transform coding cost.
    private static long Satd8x8(ReadOnlySpan<byte> src, int srcW, int srcBx, int srcBy, byte[] pred, int n)
    {
        long total = 0;
        var d = new int[64];
        for (int by = 0; by < n; by += 8)
        {
            for (int bx = 0; bx < n; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        d[y * 8 + x] = src[(srcBy + by + y) * srcW + (srcBx + bx + x)] - pred[(by + y) * n + (bx + x)];
                total += Hadamard8x8Abs(d);
            }
        }

        return total;
    }

    // In-place 8x8 Walsh–Hadamard transform (rows then columns) of d, returning the sum of absolute outputs.
    private static long Hadamard8x8Abs(int[] d)
    {
        Span<int> t = stackalloc int[64];
        for (int i = 0; i < 8; i++) Hadamard8(d, i * 8, 1, t, i * 8, 1);
        for (int i = 0; i < 8; i++) Hadamard8(t, i, 8, d, i, 8);
        long s = 0;
        for (int i = 0; i < 64; i++) s += Math.Abs(d[i]);
        return s;
    }

    // One 8-point Walsh–Hadamard butterfly from in[inOff + k*inStride] to out[outOff + k*outStride].
    private static void Hadamard8(Span<int> input, int inOff, int inStride, Span<int> output, int outOff, int outStride)
    {
        int a0 = input[inOff], a1 = input[inOff + inStride], a2 = input[inOff + 2 * inStride], a3 = input[inOff + 3 * inStride];
        int a4 = input[inOff + 4 * inStride], a5 = input[inOff + 5 * inStride], a6 = input[inOff + 6 * inStride], a7 = input[inOff + 7 * inStride];
        int b0 = a0 + a4, b1 = a1 + a5, b2 = a2 + a6, b3 = a3 + a7;
        int b4 = a0 - a4, b5 = a1 - a5, b6 = a2 - a6, b7 = a3 - a7;
        int c0 = b0 + b2, c1 = b1 + b3, c2 = b0 - b2, c3 = b1 - b3;
        int c4 = b4 + b6, c5 = b5 + b7, c6 = b4 - b6, c7 = b5 - b7;
        output[outOff] = c0 + c1;
        output[outOff + outStride] = c0 - c1;
        output[outOff + 2 * outStride] = c2 + c3;
        output[outOff + 3 * outStride] = c2 - c3;
        output[outOff + 4 * outStride] = c4 + c5;
        output[outOff + 5 * outStride] = c4 - c5;
        output[outOff + 6 * outStride] = c6 + c7;
        output[outOff + 7 * outStride] = c6 - c7;
    }

    // DC prediction for a 64x64 block from reconstructed neighbours, mirroring Av1IntraPred DC modes.
    private static int DcPredict(byte[] recon, int w, int h, int bx, int by, int bw, int bh)
    {
        bool haveTop = by > 0;
        bool haveLeft = bx > 0;
        if (haveTop && haveLeft)
        {
            int dc = (bw + bh) >> 1;
            for (int x = 0; x < bw; x++) dc += recon[(by - 1) * w + bx + x];
            for (int y = 0; y < bh; y++) dc += recon[(by + y) * w + bx - 1];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)(bw + bh));
        }

        if (haveTop)
        {
            int dc = bw >> 1;
            for (int x = 0; x < bw; x++) dc += recon[(by - 1) * w + bx + x];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)bw);
        }

        if (haveLeft)
        {
            int dc = bh >> 1;
            for (int y = 0; y < bh; y++) dc += recon[(by + y) * w + bx - 1];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)bh);
        }

        return 128;
    }

    // Dequantizes the quantized levels the way the decoder does, inverse-transforms onto the DC prediction (via
    // the decoder's own InvTxfmAdd) to reconstruct the 64x64 block into `recon`, and returns the coefficient
    // context byte (cul_level | dc-sign) that neighbours read.
    private static byte DequantAndReconstruct(int[] levels, int dcDq, int acDq, int dcPred, byte[] recon, int w, int bx, int by)
        => DequantAndReconstruct(levels, Tx64x64, 64, dcDq, acDq, dcPred, recon, w, bx, by);

    // Dequantizes quantized levels (decoder-exact), inverse-transforms onto the DC prediction (via the decoder's
    // own InvTxfmAdd) to reconstruct an n x n block of transform <paramref name="tx"/> into <paramref
    // name="recon"/> (stride <paramref name="reconW"/>), and returns the block's coefficient context byte.
    private static byte DequantAndReconstruct(int[] levels, int tx, int n, int dcDq, int acDq, int dcPred,
        byte[] recon, int reconW, int bx, int by)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        const int cfMax = 32767; // ~(~127 << 8), 8-bit
        var scan = Av1Tables.Scans[tx];

        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
        {
            if (levels[scan[i]] != 0) { eob = i; break; }
        }

        var cf = new int[Math.Max(n * n, 32 * 32)];
        int culLevel = 0;
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i];
            int lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl);
            int sign = lvl < 0 ? 1 : 0;
            int dq = ((rc == 0 ? dcDq : acDq) * mag) >> dqShift;
            dq = Math.Min(dq, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
            culLevel += mag;
        }

        int dcSignLevel = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
        byte cfCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);

        var block = new byte[n * n];
        Array.Fill(block, (byte)dcPred);
        Av1InvTransform.InvTxfmAdd(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], Av1TxType.DctDct, 8);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = block[y * n + x];
            }
        }

        return cfCtx;
    }

    // Dequantizes and reconstructs an n x n block on top of an arbitrary intra prediction (predBlock, n x n),
    // via the decoder's InvTxfmAdd, into recon. Returns the coefficient-context byte.
    private static byte DequantAndReconstructPred(int[] levels, int tx, int n, int dcDq, int acDq,
        byte[] predBlock, byte[] recon, int reconW, int bx, int by, Av1TxType txType = Av1TxType.DctDct)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        const int cfMax = 32767;
        var scan = Av1Tables.Scans[tx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
        {
            if (levels[scan[i]] != 0) { eob = i; break; }
        }

        var cf = new int[Math.Max(n * n, 32 * 32)];
        int culLevel = 0;
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i];
            int lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl);
            int sign = lvl < 0 ? 1 : 0;
            int dq = ((rc == 0 ? dcDq : acDq) * mag) >> dqShift;
            dq = Math.Min(dq, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
            culLevel += mag;
        }

        int dcSignLevel = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
        byte cfCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);

        var block = (byte[])predBlock.Clone();
        Av1InvTransform.InvTxfmAdd(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], txType, 8);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = block[y * n + x];
            }
        }

        return cfCtx;
    }

    // Forward-transforms and quantizes (src block - prediction) for an n x n block.
    // The reduced intra tx set (Intra2) types searched for luma tx ≤ 16x16 (where the type is signalled), as
    // (forward type, inverse type, symbol index in TxTypesPerSet Intra2). DctDct is idx 1; ADST combos 2/3/4.
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] IntraTxTypes =
    {
        (Av1FwdTransform.FwdTxType.DctDct,   Av1TxType.DctDct,   1),
        (Av1FwdTransform.FwdTxType.AdstAdst, Av1TxType.AdstAdst, 2),
        (Av1FwdTransform.FwdTxType.AdstDct,  Av1TxType.AdstDct,  3),
        (Av1FwdTransform.FwdTxType.DctAdst,  Av1TxType.DctAdst,  4),
    };
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] DctOnly =
        { (Av1FwdTransform.FwdTxType.DctDct, Av1TxType.DctDct, 1) };

    // Gray wrapper for the primitive-arg RD leaf decision.
    private static (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx)
        ChooseLeafRd(GrayPartCtx c, int bx4, int by4, int n, int tx)
    {
        int bxR = bx4 & 31, byR = by4 & 31;
        return ChooseLeafRdCore(c.Recon, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AboveMode[bxR], c.LeftMode[byR], c.AboveLCoef.AsSpan(bxR), c.LeftLCoef.AsSpan(byR), c.Pred);
    }

    // Rate-distortion leaf decision: over all candidate (intra mode, tx type) pairs, pick the one with the lowest
    // actual coded rate — coefficient bits (EstimateCoefBits, from the live CDFs) plus the mode/angle signalling
    // bits. This replaces the SATD proxy: it directly minimises what the bitstream costs and lets the tx-type
    // (ADST/DCT) choice compound with the mode choice. Writes the winning prediction into predOut.
    private static (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx)
        ChooseLeafRdCore(byte[] recon, int reconW, int bw4, int bh4, byte[] luma, int lumaW, int bx4, int by4,
            int n, int tx, int dcDq, int acDq, Av1CdfContext cdf, byte aboveMode, byte leftMode,
            ReadOnlySpan<byte> aboveLCoef, ReadOnlySpan<byte> leftLCoef, byte[] predOut)
    {
        int aboveCtx = Av1Tables.IntraModeContext[aboveMode];
        int leftCtx = Av1Tables.IntraModeContext[leftMode];
        Span<ushort> ymCdf = cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, aboveLCoef, leftLCoef);
        int scanLen = Av1Tables.Scans[tx].Length;
        var predBuf = new byte[n * n];
        var qfCand = new double[scanLen];
        var qfWin = new double[scanLen];
        double best = double.MaxValue;
        (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx) bestCand = default;
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, predBuf);
            int[] residual = ComputeResidualPred(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            foreach (var (fwd, inv, idx) in n <= 16 ? IntraTxTypes : DctOnly)
            {
                int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd, qfCand);
                double rate = Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, (int)mode, cf, 0, dcSignCtx, idx) + modeBits;
                if (rate < best) { best = rate; bestCand = (mode, delta, cf, inv, idx); Array.Copy(predBuf, predOut, n * n); Array.Copy(qfCand, qfWin, scanLen); }
            }
        }

        // RDOQ-refine the winning coefficients (encoder-only; decoder reconstructs from these same levels).
        if (bestCand.Coeffs != null)
        {
            double lambda = RdoqLambdaScale * RdLambdaK * acDq * acDq;
            Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, 0, (int)bestCand.Mode, bestCand.Coeffs, qfWin,
                dcDq, acDq, 0, dcSignCtx, bestCand.Idx, lambda);
        }

        return bestCand;
    }


    // Residual (src - prediction) for an n x n block.
    private static int[] ComputeResidualPred(ReadOnlySpan<byte> src, int srcW, int srcBx, int srcBy, byte[] pred, int n)
    {
        var r = new int[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                r[y * n + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * n + x];
        return r;
    }

    // Coefficient coding-cost proxy (bit-ish): per nonzero base+sign + magnitude tail.
    private static long CoeffCost(int[] coeffs)
    {
        long bits = 0;
        foreach (int v in coeffs) if (v != 0) { int a = Math.Abs(v); bits += 5 + (a >= 15 ? 8 : a >> 1); }
        return bits;
    }

    // Chooses the intra transform type minimizing coefficient cost for a residual. For luma tx ≤ 16x16 the type is
    // signalled (Intra2 set: DctDct + ADST combos); for tx ≥ 32x32 DctDct is forced. Returns the quantized coeffs,
    // the inverse type for reconstruction, and the Intra2 symbol index to code.
    private static (int[] Coeffs, Av1TxType Inv, int Idx) ChooseTxType(int[] residual, int n, int tx, int dcDq, int acDq)
    {
        int scanLen = Av1Tables.Scans[tx].Length;
        if (n > 16)
            return (Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen), Av1TxType.DctDct, 1);

        long best = long.MaxValue;
        (int[], Av1TxType, int) bestCand = default;
        foreach (var (fwd, inv, idx) in IntraTxTypes)
        {
            int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd);
            long cost = CoeffCost(cf);
            if (cost < best) { best = cost; bestCand = (cf, inv, idx); }
        }

        return bestCand;
    }

    private static int[] ForwardResidualPred(ReadOnlySpan<byte> src, int srcW, int srcBx, int srcBy,
        byte[] pred, int n, int dcDq, int acDq, int scanLen)
    {
        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                residual[y * n + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * n + x];
            }
        }

        return Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen);
    }

    /// <summary>Builds the sequence-header OBU and the OBU_FRAME (frame header + tile) for a monochrome key frame
    /// carrying the given coefficients (null ⇒ skip). The two OBUs are the building blocks for both a raw
    /// temporal unit and an AVIF container (seq OBU → av1C configOBUs, frame OBU → mdat).</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildObus(int width, int height, int baseQIdx, int[]? coeffs)
    {
        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                $"Frame {width}x{height} does not map to a single square block (rectangular/multi-block not yet supported).");
        }

        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleBlockTile(baseQIdx, coeffs, plan);

        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return (seqObu, frameObu);
    }

    /// <summary>Encodes a monochrome key frame whose single 64x64 luma block carries the given quantized
    /// coefficients (rc-indexed, TX_64X64 layout). Returns a raw AV1 temporal unit (TD + seq + OBU_FRAME).</summary>
    internal static byte[] EncodeMonochromeWithCoeffs(int width, int height, int baseQIdx, int[]? coeffs)
    {
        (byte[] seqObu, byte[] frameObu) = BuildObus(width, height, baseQIdx, coeffs);
        byte[] tdObu = Av1ObuWriter.WrapObu(Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);

        var outBytes = new byte[tdObu.Length + seqObu.Length + frameObu.Length];
        int o = 0;
        tdObu.CopyTo(outBytes, o); o += tdObu.Length;
        seqObu.CopyTo(outBytes, o); o += seqObu.Length;
        frameObu.CopyTo(outBytes, o);
        return outBytes;
    }

    /// <summary>Encodes a tightly-packed 64x64 monochrome luma image as a complete .avif. Lossy.</summary>
    internal static byte[] EncodeAvifMonochrome64(ReadOnlySpan<byte> pixels, int baseQIdx)
        => EncodeAvifMonochrome(pixels, 64, 64, baseQIdx);

    /// <summary>Encodes a monochrome image (<paramref name="luma"/> tightly packed, <paramref name="width"/> x
    /// <paramref name="height"/>) as a complete .avif. The frame must map to a single square block (both
    /// dimensions within one of 5..8, 9..16, 17..32, 33..64 — coded at block sizes 8/16/32/64). The residual
    /// block is the frame content in its top-left, edge-replicated to the block size.</summary>
    internal static byte[] EncodeAvifMonochrome(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx)
    {
        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new NotSupportedException(
                $"Frame {width}x{height} does not map to a single square block (only near-square sizes with both " +
                "dimensions in 5..8, 9..16, 17..32 or 33..64 are supported).");
        }

        int[]? coeffs = QuantizeBlock(luma, width, height, plan, baseQIdx);
        (byte[] seqObu, byte[] frameObu) = BuildObus(width, height, baseQIdx, coeffs);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true);
    }

    /// <summary>Forward-transforms and quantizes a frame into its single block's coefficients (DC prediction =
    /// 128), or null when quantization zeroes everything (⇒ skip / flat plane). The n x n residual block is built
    /// from the frame luma with edge replication beyond the frame.</summary>
    private static int[]? QuantizeBlock(ReadOnlySpan<byte> luma, int width, int height, in BlockPlan plan, int baseQIdx)
    {
        int n = plan.BlockPx;
        int dcDq = Av1Tables.DequantTable[0, baseQIdx, 0];
        int acDq = Av1Tables.DequantTable[0, baseQIdx, 1];

        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(y, height - 1);
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(x, width - 1);
                residual[y * n + x] = luma[sy * width + sx] - 128; // DC prediction (first block, no neighbours)
            }
        }

        int[] coeffs = Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, Av1Tables.Scans[plan.Tx].Length);
        foreach (int c in coeffs)
        {
            if (c != 0)
            {
                return coeffs;
            }
        }

        return null;
    }
}
