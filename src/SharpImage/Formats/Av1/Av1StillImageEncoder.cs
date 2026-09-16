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
        (byte[] seqObu, byte[] frameObu) = BuildMonochromeObus(luma, width, height, baseQIdx);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true);
    }

    /// <summary>Builds the sequence-header + OBU_FRAME for a monochrome (I400) multi-superblock key frame — the
    /// shared core used both for a standalone grayscale AVIF and for an AVIF alpha auxiliary item.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildMonochromeObus(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx)
    {
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        byte[] padded = PadPlane(luma, width, height, pw, ph);
        byte[] tile = EncodeMultiSbTile(padded, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));

        // CDEF strength search: the tile is CDEF-independent (cdef_bits=0), so try candidate strengths by decoding
        // each and keeping the one with the lowest reconstruction SSE vs source (always incl. the no-op, so it can
        // never hurt). The decoder applies CDEF as an output filter; intra prediction used pre-CDEF recon.
        byte[] srcCopy = luma.ToArray();
        int lfLevel = SearchDeblock(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: true, srcCopy, null, null, 0, 0);
        Av1ObuWriter.CdefParams best = SearchCdef(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: true, srcCopy, null, null, 0, 0, lfLevel);
        byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome: true, tile, best, lfLevel);
        return (seqObu, frameObu);
    }

    // Assembles the OBU_FRAME (frame header with the given CDEF params + tile) for a multi-SB key frame.
    private static byte[] BuildFrameObu(int baseQIdx, int sbCols, int sbRows, bool monochrome, byte[] tile, Av1ObuWriter.CdefParams cdef, int lfLevel = 0)
    {
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, monochrome, txModeSelect: monochrome || (!monochrome && UseColorTxDepth), cdef, lfLevel, screenContentTools: UsePalette && !monochrome);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        return Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
    }

    // Searches a single global CDEF strength set (cdef_bits=0) that minimises reconstruction SSE. Decodes each
    // candidate through our own decoder (which matches libdav1d's CDEF), comparing the decoded planes to the
    // source. srcY is width*height; srcU/srcV (cw*ch) are only used when !monochrome.
    private static Av1ObuWriter.CdefParams SearchCdef(byte[] seqObu, byte[] tileRef, int baseQIdx, int sbCols, int sbRows,
        int width, int height, bool monochrome, byte[] srcY, byte[]? srcU, byte[]? srcV, int cw, int ch, int lfLevel = 0)
    {
        int damping = Math.Clamp(3 + (baseQIdx >> 6), 3, 6);
        long BestSse = long.MaxValue;
        Av1ObuWriter.CdefParams bestParams = Av1ObuWriter.CdefParams.None;

        long Evaluate(int yLvl, int uvLvl)
        {
            var cdef = new Av1ObuWriter.CdefParams(damping, 0, new[] { (byte)yLvl }, new[] { (byte)uvLvl });
            byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome, tileRef, cdef, lfLevel);
            var tu = new byte[seqObu.Length + frameObu.Length];
            seqObu.CopyTo(tu, 0);
            frameObu.CopyTo(tu, seqObu.Length);
            var dec = new Av1Decoder();
            using var yuv = dec.Decode(tu, 0, isKeyframe: true);
            if (yuv == null) return long.MaxValue;
            long sse = PlaneSse(yuv.YPlane.Span, yuv.YStride, srcY, width, height);
            if (!monochrome && srcU != null && srcV != null)
            {
                sse += PlaneSse(yuv.UPlane.Span, yuv.UStride, srcU, cw, ch);
                sse += PlaneSse(yuv.VPlane.Span, yuv.VStride, srcV, cw, ch);
            }

            return sse;
        }

        // CDEF's still-image payoff is modest (~1% RMSE) and its verification costs a full re-decode, so it is
        // applied only where that trade is worth it: lossy quality (baseQIdx >= 64, below which CDEF risks
        // blurring fine detail) and images small enough that 1-2 decodes are cheap. Large frames — where the
        // re-decode is expensive and CDEF's gain on detailed content is near zero — skip it. Within that gate we
        // evaluate the no-op plus one q-scaled heuristic strength and keep whichever decodes closer to the
        // source, so it can never regress vs no CDEF.
        if (baseQIdx < 64 || (long)width * height > 512 * 512) return Av1ObuWriter.CdefParams.None;

        long noopSse = Evaluate(0, 0);
        BestSse = noopSse;

        int yPri = Math.Clamp(baseQIdx / 16, 1, 12);   // stronger deringing as quantisation coarsens
        int ySec = baseQIdx >= 128 ? 2 : 1;
        int yLvl = (yPri << 2) | ySec;
        int uvLvl = monochrome ? 0 : ((Math.Clamp(baseQIdx / 24, 1, 8) << 2) | (baseQIdx >= 160 ? 1 : 0));
        long sse = Evaluate(yLvl, uvLvl);
        if (sse < BestSse) { BestSse = sse; bestParams = new Av1ObuWriter.CdefParams(damping, 0, new[] { (byte)yLvl }, new[] { (byte)uvLvl }); }

        return bestParams;
    }

    // Searches a single global deblocking loop_filter_level that minimises reconstruction SSE, the same
    // decode-based way as SearchCdef. Deblocking is post-reconstruction (it does not feed intra prediction), so
    // the coded tile is unchanged across candidates — only the frame-header level differs. Searched with CDEF off
    // (SearchCdef then runs with the chosen level), mirroring libaom's deblock-before-CDEF ordering. Returns the
    // level (0 = off) that decoded closest to the source, so it can never regress vs no deblocking.
    internal static bool UseDeblockSearch = true;   // toggle the deblock loop_filter_level RD search (A/B)

    // Palette mode for colour (screen-content). When on, the frame enables screen_content_tools and eligible
    // DC luma blocks may be coded as palette; rect partitions are disabled to keep palette to the square leaf.
    internal static bool UsePalette = false;
    internal static int PaletteMaxColors = 8;   // AV1 caps luma palette at 8

    private static int SearchDeblock(byte[] seqObu, byte[] tileRef, int baseQIdx, int sbCols, int sbRows,
        int width, int height, bool monochrome, byte[] srcY, byte[]? srcU, byte[]? srcV, int cw, int ch)
    {
        // Deblocking's still-image payoff is largest at coarse quantisation; skip the extra decodes when tiny.
        if (!UseDeblockSearch || (long)width * height > 512 * 512) return 0;

        long Evaluate(int lvl)
        {
            byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome, tileRef, Av1ObuWriter.CdefParams.None, lvl);
            var tu = new byte[seqObu.Length + frameObu.Length];
            seqObu.CopyTo(tu, 0);
            frameObu.CopyTo(tu, seqObu.Length);
            var dec = new Av1Decoder();
            using var yuv = dec.Decode(tu, 0, isKeyframe: true);
            if (yuv == null) return long.MaxValue;
            long sse = PlaneSse(yuv.YPlane.Span, yuv.YStride, srcY, width, height);
            if (!monochrome && srcU != null && srcV != null)
            {
                sse += PlaneSse(yuv.UPlane.Span, yuv.UStride, srcU, cw, ch);
                sse += PlaneSse(yuv.VPlane.Span, yuv.VStride, srcV, cw, ch);
            }
            return sse;
        }

        int bestLvl = 0;
        long bestSse = Evaluate(0);   // no deblocking baseline
        // Candidate levels around a q-scaled guess (AV1 levels are 0..63; deblock strength grows with q).
        int guess = Math.Clamp(baseQIdx / 8, 1, 40);
        Span<int> cands = stackalloc int[] { guess / 2, guess, Math.Min(guess * 3 / 2, 63) };
        foreach (int lvl in cands)
        {
            if (lvl <= 0) continue;
            long sse = Evaluate(lvl);
            if (sse < bestSse) { bestSse = sse; bestLvl = lvl; }
        }
        return bestLvl;
    }

    private static long PlaneSse(ReadOnlySpan<byte> dec, int stride, byte[] src, int w, int h)
    {
        long sse = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = dec[y * stride + x] - src[y * w + x];
                sse += (long)d * d;
            }

        return sse;
    }

    /// <summary>Multi-superblock I420 COLOUR: a 1..2 x 1..2 grid of full 64x64 superblocks (64 or 128 each side),
    /// coding luma + subsampled chroma with cross-block DC prediction and reconstruct-as-you-go on all three
    /// planes.</summary>
    internal static byte[] EncodeAvifColorMultiSb(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx)
    {
        (byte[] seqObu, byte[] frameObu) = BuildColorObus(luma, u, v, width, height, baseQIdx);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false);
    }

    /// <summary>Builds the sequence-header + OBU_FRAME for an I420 colour multi-superblock key frame.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildColorObus(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
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

        // In-loop filter search (deblock first, then CDEF with the chosen level; see BuildMonochromeObus).
        byte[] srcY = luma.ToArray(), srcU = u.ToArray(), srcV = v.ToArray();
        int lfLevel = SearchDeblock(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: false, srcY, srcU, srcV, cwIn, chIn);
        Av1ObuWriter.CdefParams best = SearchCdef(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: false, srcY, srcU, srcV, cwIn, chIn, lfLevel);
        byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome: false, tile, best, lfLevel);
        return (seqObu, frameObu);
    }

    /// <summary>Encodes an I420 colour image plus an 8-bit alpha plane into a 2-item AVIF: a primary colour
    /// `av01` item and a monochrome alpha auxiliary item, linked by an `auxl` item reference. Alpha is coded as a
    /// full-range monochrome AV1 image (the standard AVIF alpha representation).</summary>
    internal static byte[] EncodeAvifColorWithAlpha(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha, int width, int height, int baseQIdx, int alphaQIdx)
    {
        (byte[] cSeq, byte[] cFrame) = BuildColorObus(luma, u, v, width, height, baseQIdx);
        (byte[] aSeq, byte[] aFrame) = BuildMonochromeObus(alpha, width, height, alphaQIdx);
        return Av1AvifWriter.BuildAvifWithAlpha(cSeq, cFrame, aSeq, aFrame, width, height, colorMonochrome: false);
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
        public sbyte[] ATxY = null!, LTxY = null!;   // neighbour luma tx log-size, for the tx-depth context (UseColorTxDepth)
        // Palette neighbour state (SB128-relative, mirrors the decoder's Above/Left.PalSz + PalPrevY): per 4-unit
        // position the covering block's luma palette size and (when >0) its colours, used for the has_palette
        // context and the colour-prediction cache. APal* are per-SB128-column (like AModeY); LPal* reset per SB row.
        public byte[] APalSz = null!, LPalSz = null!;         // [32] palette size at each position (0 = none)
        public ushort[] APalCol = null!, LPalCol = null!;     // [32*8] palette colours at each position
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
        var aModeY = new byte[sb128Cols][]; var aSkip = new byte[sb128Cols][]; var aTxY = new sbyte[sb128Cols][];
        var aPalSz = new byte[sb128Cols][]; var aPalCol = new ushort[sb128Cols][];
        for (int i = 0; i < sb128Cols; i++) { abovePart[i] = new byte[16]; aModeY[i] = new byte[32]; aSkip[i] = new byte[32]; aTxY[i] = FilledSbyte(32, -1); aPalSz[i] = new byte[32]; aPalCol[i] = new ushort[32 * 8]; }

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
            c.LModeY = new byte[32]; c.LSkip = new byte[32]; c.LTxY = FilledSbyte(32, -1);
            c.LPalSz = new byte[32]; c.LPalCol = new ushort[32 * 8];
            for (int sbx = 0; sbx < sbCols; sbx++)
            {
                int col = sbx >> 1;
                c.AbovePart = abovePart[col]; c.ALY = aLY[col]; c.ACU = aCU[col]; c.ACV = aCV[col];
                c.AModeY = aModeY[col]; c.ASkip = aSkip[col]; c.ATxY = aTxY[col];
                c.APalSz = aPalSz[col]; c.APalCol = aPalCol[col];
                EncodePartitionColor(c, 1, sbx * 16, sby * 16);
            }
        }

        return c.Msac.Finish();
    }

    private static void EncodePartitionColor(ColorPartCtx c, int bl, int bx4, int by4, int edgeIdx = 0)
    {
        int hsz = 16 >> bl, blk4 = 32 >> bl;
        bool haveH = c.Bw4 > bx4 + hsz;
        bool haveV = c.Bh4 > by4 + hsz;

        int bx8 = (bx4 & 31) >> 1, by8 = (by4 & 31) >> 1;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);
        Span<ushort> partCdf = c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
        int nPart = Av1Tables.PartitionTypeCount[bl];

        // Edge handling mirrors the gray path (force SPLIT at edges; the tree is luma-driven, chroma follows). The
        // forced-split children take the same intra-edge tree children as a coded SPLIT (0=TL,1=TR,2=BL,3=BR).
        if (!haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            EncodePartitionColor(c, bl + 1, bx4, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 0));
            return;
        }
        if (haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherTopPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartitionColor(c, bl + 1, bx4, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 0));
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 1));
            return;
        }
        if (!haveH && haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherLeftPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartitionColor(c, bl + 1, bx4, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 0));
            EncodePartitionColor(c, bl + 1, bx4, by4 + hsz, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 2));
            return;
        }

        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;

        // True trial-encode RD: encode each candidate partition for real, measure its actual coded bits + SSE,
        // and commit the one with the lowest J = SSE + λ·bits. Unlike the estimate path this uses the live CDFs
        // and real reconstruction (incl. adaptation and recursive sub-decisions), so it picks partitions optimally.
        // Also runs for PARTIAL blocks (extend past the frame but their centre is in-frame, i.e. haveH && haveV):
        // those were previously forced to a single large NONE, wasting bits on the padded region — RD now splits
        // them. The SPLIT recursion terminates cleanly at 8x8 (bw4/bh4 are always even, so 8x8 tiles edges exactly;
        // the 4x4 forced-split throw is unreachable). Rect HORZ/VERT candidates are only offered when fully inside.
        if (UseTrueRd && (long)c.Bw4 * c.Bh4 * 16 <= TrueRdPixelBudget && bl >= 1 && bl < 4)
        {
            EncodePartitionColorTrueRd(c, bl, bx4, by4, hsz, blk4, partCdf, nPart, bx8, by8, edgeIdx, fullyInside);
            return;
        }

        // Partition choice: 0=NONE, 1=HORZ, 2=VERT, 3=SPLIT (rectangular HORZ/VERT only at 16x16, which yields
        // 16x8/8x16 luma + 8x4/4x8 chroma — both ≤16 per axis, so the matched rect transform applies and there is
        // no sub-8x8 chroma corner case).
        int choice = 0;
        if (bl < 4 && fullyInside)
        {
            long costNone = EstimateBlockCost(c.Luma, c.W, c.Bw4, c.Bh4, c.DcDq, c.AcDq, c.EstScratch, bl, bx4, by4);
            long costSplit = 0;
            foreach ((int dx, int dy) in new[] { (0, 0), (hsz, 0), (0, hsz), (hsz, hsz) })
                costSplit += EstimateBlockCost(c.Luma, c.W, c.Bw4, c.Bh4, c.DcDq, c.AcDq, c.EstScratch, bl + 1, bx4 + dx, by4 + dy);
            long costHorz = long.MaxValue, costVert = long.MaxValue;
            if ((bl == 2 || bl == 3) && UseRectPartition && !UsePalette)
            {
                // Rectangular HORZ/VERT at 32x32 (→32x16/16x32) and 16x16 (→16x8/8x16). Chroma-aware: a partition
                // codes a chroma block per luma leaf, so include chroma coeff cost (λ·bits) in every candidate —
                // otherwise HORZ/VERT (2 chroma blocks) look artificially cheap vs NONE (1).
                double lambda = RdLambdaK * c.AcDq * c.AcDq;
                int cbx = bx4 * 2, cby = by4 * 2;
                int cBlk = blk4 * 2, cH = cBlk >> 1;    // chroma NONE size (px) and half (px)
                // Per-level tx / block-size ordinals for the rect leaves and the chroma cost blocks.
                (int lumaTxH, int lumaTxV, int chTxH, int chTxV, int lumaBsH, int lumaBsV, int noneChTx, int splitChTx) = bl == 2
                    ? (TxIdx32x16, TxIdx16x32, TxIdx16x8, TxIdx8x16, (int)Av1BlockSize.Bs32x16, (int)Av1BlockSize.Bs16x32, 2, 1)
                    : (TxIdx16x8, TxIdx8x16, TxIdx8x4, TxIdx4x8, (int)Av1BlockSize.Bs16x8, (int)Av1BlockSize.Bs8x16, 1, 0);

                costNone += (long)(lambda * ChromaCostDc(c, cbx, cby, cBlk, cBlk, noneChTx));
                costSplit += (long)(lambda * (ChromaCostDc(c, cbx, cby, cH, cH, splitChTx)
                          + ChromaCostDc(c, cbx + cH, cby, cH, cH, splitChTx) + ChromaCostDc(c, cbx, cby + cH, cH, cH, splitChTx)
                          + ChromaCostDc(c, cbx + cH, cby + cH, cH, cH, splitChTx)));
                costHorz = EstimateRectCostColor(c, lumaTxH, bx4, by4, blk4, hsz)
                         + EstimateRectCostColor(c, lumaTxH, bx4, by4 + hsz, blk4, hsz)
                         + (long)(lambda * (ChromaCostDc(c, cbx, cby, cBlk, cH, chTxH) + ChromaCostDc(c, cbx, cby + cH, cBlk, cH, chTxH)));
                costVert = EstimateRectCostColor(c, lumaTxV, bx4, by4, hsz, blk4)
                         + EstimateRectCostColor(c, lumaTxV, bx4 + hsz, by4, hsz, blk4)
                         + (long)(lambda * (ChromaCostDc(c, cbx, cby, cH, cBlk, chTxV) + ChromaCostDc(c, cbx + cH, cby, cH, cBlk, chTxV)));
            }

            // Square NONE/SPLIT decide normally; rect is only taken when it beats the best square option by a
            // margin — the estimate (coeff-cost proxy, source prediction) is optimistic about rect, so a plain
            // argmin over-picks it and loses at matched quality. The margin keeps rect to its clear wins.
            long sqBest = Math.Min(costNone, costSplit);
            int sqChoice = costNone <= costSplit ? 0 : 3;
            long rectBest = Math.Min(costHorz, costVert);
            int rectChoice = costHorz <= costVert ? 1 : 2;
            choice = (rectBest < (long)(sqBest * RectCostMargin)) ? rectChoice : sqChoice;
        }

        EncodeChoiceColor(c, choice, bl, bx4, by4, hsz, blk4, partCdf, nPart, bx8, by8, edgeIdx);
    }

    // Encodes one specific partition choice (0=NONE,1=HORZ,2=VERT,3=SPLIT): the partition symbol, the leaf(s) or
    // recursive children, and the partition-context fill. SPLIT recurses into EncodePartitionColor (which itself
    // applies whatever decision mode is active).
    private static void EncodeChoiceColor(ColorPartCtx c, int choice, int bl, int bx4, int by4, int hsz, int blk4,
        Span<ushort> partCdf, int nPart, int bx8, int by8, int edgeIdx)
    {
        ref readonly var node = ref Av1IntraEdgeTree.Tree64[edgeIdx];
        if (choice == 3)
        {
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
            int c0 = Av1IntraEdgeTree.GetSplitChild(node, 0), c1 = Av1IntraEdgeTree.GetSplitChild(node, 1);
            int c2 = Av1IntraEdgeTree.GetSplitChild(node, 2), c3 = Av1IntraEdgeTree.GetSplitChild(node, 3);
            EncodePartitionColor(c, bl + 1, bx4, by4, c0);
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4, c1);
            EncodePartitionColor(c, bl + 1, bx4, by4 + hsz, c2);
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4 + hsz, c3);
            return;
        }

        if (choice == 1) // PARTITION_HORZ: two stacked (blk4 x hsz) leaves
        {
            var rp = RectLeafParams(bl);
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Horizontal, nPart);
            EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4, blk4, hsz, node.H0);
            EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4 + hsz, blk4, hsz, node.H1);
            FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Horizontal);
            return;
        }

        if (choice == 2) // PARTITION_VERT: two side-by-side (hsz x blk4) leaves
        {
            var rp = RectLeafParams(bl);
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Vertical, nPart);
            EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4, by4, hsz, blk4, node.V0);
            EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4 + hsz, by4, hsz, blk4, node.V1);
            FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Vertical);
            return;
        }

        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
        EncodeLeafBlockColor(c, bl, bx4, by4, blk4, node.O);
        FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.None);
    }

    // Snapshot of all mutable encoder state a partition subtree touches, so trial encodes can be rolled back.
    private sealed class RdSnapshot
    {
        public Av1MsacWriter.State Msac;
        public Av1CdfContext Cdf = new();
        public byte[] ReconY = null!, ReconU = null!, ReconV = null!;   // block regions
        public byte[] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!;
        public byte[] LeftPart = null!, LLY = null!, LCU = null!, LCV = null!, LModeY = null!, LSkip = null!;
        public sbyte[] ATxY = null!, LTxY = null!;
        public byte[] APalSz = null!, LPalSz = null!; public ushort[] APalCol = null!, LPalCol = null!;
    }

    private static byte[] CopyRegion(byte[] plane, int stride, int px, int py, int w, int h)
    {
        var r = new byte[w * h];
        for (int y = 0; y < h; y++) Array.Copy(plane, (py + y) * stride + px, r, y * w, w);
        return r;
    }

    private static void PasteRegion(byte[] region, byte[] plane, int stride, int px, int py, int w, int h)
    {
        for (int y = 0; y < h; y++) Array.Copy(region, y * w, plane, (py + y) * stride + px, w);
    }

    private static RdSnapshot SnapshotRd(ColorPartCtx c, int bx4, int by4, int blk4)
    {
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        int cpx = bx4 * 2, cpy = by4 * 2, cn = blk4 * 2;
        var s = new RdSnapshot { Msac = c.Msac.Save() };
        s.Cdf.CopyFrom(c.Cdf);
        s.ReconY = CopyRegion(c.ReconY, c.W, lpx, lpy, ln, ln);
        s.ReconU = CopyRegion(c.ReconU, c.Cw, cpx, cpy, cn, cn);
        s.ReconV = CopyRegion(c.ReconV, c.Cw, cpx, cpy, cn, cn);
        s.AbovePart = (byte[])c.AbovePart.Clone(); s.LeftPart = (byte[])c.LeftPart.Clone();
        s.ALY = (byte[])c.ALY.Clone(); s.LLY = (byte[])c.LLY.Clone();
        s.ACU = (byte[])c.ACU.Clone(); s.LCU = (byte[])c.LCU.Clone();
        s.ACV = (byte[])c.ACV.Clone(); s.LCV = (byte[])c.LCV.Clone();
        s.AModeY = (byte[])c.AModeY.Clone(); s.LModeY = (byte[])c.LModeY.Clone();
        s.ASkip = (byte[])c.ASkip.Clone(); s.LSkip = (byte[])c.LSkip.Clone();
        s.ATxY = (sbyte[])c.ATxY.Clone(); s.LTxY = (sbyte[])c.LTxY.Clone();
        s.APalSz = (byte[])c.APalSz.Clone(); s.LPalSz = (byte[])c.LPalSz.Clone();
        s.APalCol = (ushort[])c.APalCol.Clone(); s.LPalCol = (ushort[])c.LPalCol.Clone();
        return s;
    }

    private static void RestoreRd(ColorPartCtx c, RdSnapshot s, int bx4, int by4, int blk4)
    {
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        int cpx = bx4 * 2, cpy = by4 * 2, cn = blk4 * 2;
        c.Msac.Restore(s.Msac);
        c.Cdf.CopyFrom(s.Cdf);
        PasteRegion(s.ReconY, c.ReconY, c.W, lpx, lpy, ln, ln);
        PasteRegion(s.ReconU, c.ReconU, c.Cw, cpx, cpy, cn, cn);
        PasteRegion(s.ReconV, c.ReconV, c.Cw, cpx, cpy, cn, cn);
        Array.Copy(s.AbovePart, c.AbovePart, s.AbovePart.Length); Array.Copy(s.LeftPart, c.LeftPart, s.LeftPart.Length);
        Array.Copy(s.ALY, c.ALY, s.ALY.Length); Array.Copy(s.LLY, c.LLY, s.LLY.Length);
        Array.Copy(s.ACU, c.ACU, s.ACU.Length); Array.Copy(s.LCU, c.LCU, s.LCU.Length);
        Array.Copy(s.ACV, c.ACV, s.ACV.Length); Array.Copy(s.LCV, c.LCV, s.LCV.Length);
        Array.Copy(s.AModeY, c.AModeY, s.AModeY.Length); Array.Copy(s.LModeY, c.LModeY, s.LModeY.Length);
        Array.Copy(s.ASkip, c.ASkip, s.ASkip.Length); Array.Copy(s.LSkip, c.LSkip, s.LSkip.Length);
        Array.Copy(s.ATxY, c.ATxY, s.ATxY.Length); Array.Copy(s.LTxY, c.LTxY, s.LTxY.Length);
        Array.Copy(s.APalSz, c.APalSz, s.APalSz.Length); Array.Copy(s.LPalSz, c.LPalSz, s.LPalSz.Length);
        Array.Copy(s.APalCol, c.APalCol, s.APalCol.Length); Array.Copy(s.LPalCol, c.LPalCol, s.LPalCol.Length);
    }

    // SSE of the reconstructed block (luma + chroma) vs the source planes — the distortion term for true-RD.
    private static long BlockSseColor(ColorPartCtx c, int bx4, int by4, int blk4)
    {
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        long sse = 0;
        for (int y = 0; y < ln; y++)
            for (int x = 0; x < ln; x++)
            { int d = c.ReconY[(lpy + y) * c.W + lpx + x] - c.Luma[(lpy + y) * c.W + lpx + x]; sse += (long)d * d; }
        int cpx = bx4 * 2, cpy = by4 * 2, cn = blk4 * 2;
        for (int y = 0; y < cn; y++)
            for (int x = 0; x < cn; x++)
            {
                int du = c.ReconU[(cpy + y) * c.Cw + cpx + x] - c.U[(cpy + y) * c.Cw + cpx + x];
                int dv = c.ReconV[(cpy + y) * c.Cw + cpx + x] - c.V[(cpy + y) * c.Cw + cpx + x];
                sse += (long)du * du + (long)dv * dv;
            }

        return sse;
    }

    private static void EncodePartitionColorTrueRd(ColorPartCtx c, int bl, int bx4, int by4, int hsz, int blk4,
        Span<ushort> partCdf, int nPart, int bx8, int by8, int edgeIdx, bool fullyInside)
    {
        // Candidates: NONE and SPLIT always; HORZ/VERT at 32x32/16x16 when rect is enabled AND the block is fully
        // inside the frame (the rect leaves assume in-frame dimensions). For partial blocks only NONE vs SPLIT.
        Span<int> cands = stackalloc int[4];
        int nc = 0; cands[nc++] = 0; cands[nc++] = 3;
        if (fullyInside && (bl == 2 || bl == 3) && UseRectPartition && !UsePalette) { cands[nc++] = 1; cands[nc++] = 2; }

        double lambda = RdLambdaK * c.AcDq * c.AcDq;
        var snap0 = SnapshotRd(c, bx4, by4, blk4);
        int baseCount = snap0.Msac.PrecarryCount;
        bool measureWas = c.Msac.Measure;
        c.Msac.Measure = true;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);

        // Encode each candidate once, capture its full post-state (recon/contexts/cdf + MSAC scalars + the coded
        // byte tail), and commit the lowest-J one by restoring that post-state — no re-encode, so a SPLIT winner's
        // subtree is not redone (that is where the exponential blow-up would otherwise come from).
        double bestJ = double.MaxValue;
        RdSnapshot? bestSnap = null;
        int[] bestTail = System.Array.Empty<int>();
        for (int i = 0; i < nc; i++)
        {
            if (i > 0) RestoreRd(c, snap0, bx4, by4, blk4);
            double b0 = c.Msac.MeasuredBits;
            EncodeChoiceColor(c, cands[i], bl, bx4, by4, hsz, blk4, c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx), nPart, bx8, by8, edgeIdx);
            double bits = c.Msac.MeasuredBits - b0;
            double j = BlockSseColor(c, bx4, by4, blk4) + lambda * bits;
            if (j < bestJ)
            {
                bestJ = j;
                bestTail = c.Msac.PrecarryFrom(baseCount);
                bestSnap = SnapshotRd(c, bx4, by4, blk4);
            }

            // Early termination: NONE (cand 0) coded in very few bits ⇒ a flat/skip block, which SPLIT/rect can
            // only make more expensive (more partition + header bits) for no distortion gain. Commit NONE and
            // skip the costly subtree recursion — the dominant speedup on the smooth regions that fill photos.
            if (i == 0 && bits <= EarlyTermBits) break;
        }

        // Commit the winner: restore its recon/contexts/CDF, then re-apply its coded bytes onto the base stream.
        RestoreRd(c, snap0, bx4, by4, blk4);
        RestoreRd(c, bestSnap!, bx4, by4, blk4);
        c.Msac.AppendPrecarry(bestTail);
        c.Msac.Measure = measureWas;
    }

    // Rectangular tx-size ordinals (Av1TxSize / RectTxfmSize).
    private const int TxIdx8x16 = 7, TxIdx16x8 = 8, TxIdx8x4 = 6, TxIdx4x8 = 5, TxIdx32x16 = 10, TxIdx16x32 = 9;

    // Rect leaf tx/block-size ordinals per partition level (bl==2: 32x32→32x16/16x32; bl==3: 16x16→16x8/8x16).
    private static (int LumaTxH, int LumaTxV, int ChTxH, int ChTxV, int BsH, int BsV) RectLeafParams(int bl) => bl == 2
        ? (TxIdx32x16, TxIdx16x32, TxIdx16x8, TxIdx8x16, (int)Av1BlockSize.Bs32x16, (int)Av1BlockSize.Bs16x32)
        : (TxIdx16x8, TxIdx8x16, TxIdx8x4, TxIdx4x8, (int)Av1BlockSize.Bs16x8, (int)Av1BlockSize.Bs8x16);

    private static void FillPartCtx(ColorPartCtx c, int bl, int bx8, int by8, int hsz, Av1BlockPartition part)
    {
        byte aboveVal = Av1Tables.AboveLeftPartCtx[0, bl, (int)part];
        byte leftVal = Av1Tables.AboveLeftPartCtx[1, bl, (int)part];
        for (int i = 0; i < hsz && bx8 + i < 16; i++) c.AbovePart[bx8 + i] = aboveVal;
        for (int j = 0; j < hsz && by8 + j < 16; j++) c.LeftPart[by8 + j] = leftVal;
    }

    // === Chroma-from-luma (CfL) — mirrors Av1Reconstruction.ComputeCflAc / ApplyCflAlpha exactly (I420). ===
    private static void ComputeCflAcEnc(byte[] reconY, int yStride, int bx, int by, int cn, short[] ac)
    {
        int idx = 0;
        for (int y = 0; y < cn; y++)
        {
            int yOff = (by + 2 * y) * yStride + bx;
            for (int x = 0; x < cn; x++)
            { int p = yOff + 2 * x; ac[idx + x] = (short)((reconY[p] + reconY[p + 1] + reconY[p + yStride] + reconY[p + 1 + yStride]) << 1); }
            idx += cn;
        }

        int log2Sz = System.Numerics.BitOperations.TrailingZeroCount(cn) * 2;
        int dc = (1 << log2Sz) >> 1;
        for (int i = 0; i < cn * cn; i++) dc += ac[i];
        dc >>= log2Sz;
        for (int i = 0; i < cn * cn; i++) ac[i] = (short)(ac[i] - dc);
    }

    private static byte[] BuildCflPred(int dcPred, short[] ac, int cn, int alpha)
    {
        var pred = new byte[cn * cn];
        for (int i = 0; i < cn * cn; i++)
        {
            int diff = ac[i] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
            pred[i] = (byte)Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, 255);
        }

        return pred;
    }

    private static int BestCflAlpha(byte[] plane, int planeW, int cbx, int cby, int cn, int dcPred, short[] ac)
    {
        int bestAlpha = 0; long bestSse = long.MaxValue;
        for (int alpha = -16; alpha <= 16; alpha++)
        {
            long sse = 0;
            for (int y = 0; y < cn; y++)
            {
                int row = (cby + y) * planeW + cbx;
                for (int x = 0; x < cn; x++)
                {
                    int diff = ac[y * cn + x] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
                    int e = plane[row + x] - Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, 255);
                    sse += (long)e * e;
                }
            }

            if (sse < bestSse) { bestSse = sse; bestAlpha = alpha; }
        }

        return bestAlpha;
    }

    private static void EncodeCflAlphas(Av1MsacWriter w, Av1CdfContext cdf, int alphaU, int alphaV)
    {
        int signU = alphaU == 0 ? 0 : (alphaU < 0 ? 1 : 2);
        int signV = alphaV == 0 ? 0 : (alphaV < 0 ? 1 : 2);
        w.EncodeSymbolAdapt(cdf.GetCflSignCdf(), signU * 3 + signV - 1, 7);
        if (signU != 0) w.EncodeSymbolAdapt(cdf.GetCflAlphaCdf((signU == 2 ? 3 : 0) + signV), Math.Abs(alphaU) - 1, 15);
        if (signV != 0) w.EncodeSymbolAdapt(cdf.GetCflAlphaCdf((signV == 2 ? 3 : 0) + signU), Math.Abs(alphaV) - 1, 15);
    }

    private static void CopyPlaneBlock(byte[] src, int cn, byte[] recon, int reconW, int cbx, int cby)
    {
        for (int y = 0; y < cn; y++) Array.Copy(src, y * cn, recon, (cby + y) * reconW + cbx, cn);
    }

    private static byte[] FlatPlane(int value, int cn)
    {
        var p = new byte[cn * cn]; Array.Fill(p, (byte)Math.Clamp(value, 0, 255)); return p;
    }

    // Reconstruction SSE of a chroma block coded with `coeffs` on prediction `predPlane` (cn x cn), vs the source.
    private static long ChromaReconSse(int[] coeffs, int tx, int cn, int dcDq, int acDq, byte[] predPlane,
        byte[] src, int srcW, int cbx, int cby)
    {
        var tmp = new byte[cn * cn];
        DequantAndReconstructPredRect(coeffs, tx, cn, cn, dcDq, acDq, predPlane, tmp, cn, 0, 0);
        long sse = 0;
        for (int y = 0; y < cn; y++)
            for (int x = 0; x < cn; x++) { int d = tmp[y * cn + x] - src[(cby + y) * srcW + cbx + x]; sse += (long)d * d; }
        return sse;
    }

    private static void EncodeLeafBlockColor(ColorPartCtx c, int bl, int bx4, int by4, int blk4, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
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
        ref readonly var maxTDim = ref Av1Tables.TxfmDimensions[tx];

        // Luma: rate-distortion mode + tx-type decision (from reconstruction). Writes prediction into c.Pred.
        var rd = ChooseLeafRdCore(c.ReconY, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AModeY[bxR], c.LModeY[byR], c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR), c.Pred, edgeFlags);
        Av1IntraPredMode yMode = rd.Mode; int yDelta = rd.Delta;
        int[] yC = rd.Coeffs; Av1TxType yInv = rd.Inv; int yTxIdx = rd.Idx;

        // Transform-size (tx_depth) search for luma. Only when residual is coded (depth-0 not all-zero) and the block
        // is fully inside the frame. depth 0 keeps rd's single block-size transform; depth>0 splits into a quadtree
        // of smaller transforms, each predicted from reconstruction in raster order (mirrors the grayscale path).
        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        int maxDepth = (UseColorTxDepth && HasNonZero(yC) && fullyInside) ? Math.Min((int)maxTDim.Max, 2) : 0;
        int depth = 0;
        if (maxDepth > 0)
        {
            long best = EstimateTxDepthCostColor(c, bx4, by4, blk4, tx, yMode, yDelta);
            for (int d = 1; d <= maxDepth; d++)
            {
                long cost = EstimateTxDepthCostColor(c, bx4, by4, blk4, ReduceTx(tx, d), yMode, yDelta);
                if (cost < best) { best = cost; depth = d; }
            }
        }
        int lumaTx = ReduceTx(tx, depth);
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];

        // Reconstruct luma at the chosen depth into ReconY (CfL needs the reconstructed luma AC). For depth 0 this is
        // the single transform; for depth>0 the per-tx-block records are captured for emission after the tx_size symbol.
        byte cfY = 0x40;
        List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? lumaTxb = null;
        if (depth == 0)
        {
            cfY = DequantAndReconstructPred(yC, tx, n, c.DcDq, c.AcDq, c.Pred, c.ReconY, c.W, bx, by, yInv);
        }
        else
        {
            lumaTxb = new();
            int txN = lTDim.W * 4, txW4 = lTDim.W;
            var predBuf = new byte[txN * txN];
            // Per-tx-block intra-edge availability within this coding block (blk4 <= 16, so one 64-region: initX=
            // initY=0, subW4=blk4, subH4=blk4). Mirrors Av1Reconstruction's localEdgeFlags for tx_depth > 0.
            int sbHasTr = (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0 ? 1 : 0;
            int sbHasBl = (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0 ? 1 : 0;
            for (int iy = 0; iy < blk4; iy += txW4)
                for (int ix = 0; ix < blk4; ix += txW4)
                {
                    int cbx4 = bx4 + ix, cby4 = by4 + iy, cbxR = cbx4 & 31, cbyR = cby4 & 31;
                    var localEdge =
                        (((iy > 0 || sbHasTr == 0) && (ix + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                        ((ix > 0 || (sbHasBl == 0 && iy + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                    PredictIntra(c.ReconY, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, predBuf, localEdge, IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]));
                    int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, predBuf, txN);
                    (int[] cf, Av1TxType inv, int idx) = ChooseTxType(res, txN, lumaTx, c.DcDq, c.AcDq,
                        predBuf, c.Luma, c.W, cbx4 * 4, cby4 * 4, RdLambdaK * c.AcDq * c.AcDq);
                    int skc = Av1CoeffDecode.GetSkipCtx(in lTDim, bs, c.ALY.AsSpan(cbxR), c.LLY.AsSpan(cbyR), 0, 0);
                    int snc = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(cbxR), c.LLY.AsSpan(cbyR));
                    // RDOQ this tx-split block (depth-0 luma is RDOQ'd in ChooseLeafRdCore; the quadtree path was
                    // round-to-nearest). Re-derive qf for the picked tx type, then trim with the real neighbour
                    // contexts. Reconstruction below uses the trimmed cf, matching the decoder + feeding later blocks.
                    if (HasNonZero(cf))
                    {
                        var qfTx = new double[Av1Tables.Scans[lumaTx].Length];
                        Av1FwdTransform.ForwardQuantTyped(res, txN, c.DcDq, c.AcDq, qfTx.Length, FwdTypeForIdx(idx), qfTx);
                        Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, cf, qfTx,
                            c.DcDq, c.AcDq, skc, snc, idx, RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);
                    }
                    lumaTxb.Add((cf, inv, idx, skc, snc, cbx4 * 4, cby4 * 4));
                    byte txCfCtx = DequantAndReconstructPred(cf, lumaTx, txN, c.DcDq, c.AcDq, predBuf, c.ReconY, c.W, cbx4 * 4, cby4 * 4, inv);
                    int tcw = Math.Min(txW4, c.Bw4 - cbx4), tch = Math.Min(txW4, c.Bh4 - cby4);
                    for (int i = 0; i < tcw && cbxR + i < 32; i++) c.ALY[cbxR + i] = txCfCtx;
                    for (int j = 0; j < tch && cbyR + j < 32; j++) c.LLY[cbyR + j] = txCfCtx;
                }
        }
        bool lumaAllZero = depth == 0 ? !HasNonZero(yC) : lumaTxb!.TrueForAll(t => !HasNonZero(t.Cf));

        // --- Palette (screen content): code this block as a luma palette when it wins RD. Eligible DC-sized
        // blocks fully inside the frame with 2..8 distinct source colours are palette-representable LOSSLESSLY
        // (skip, no residual), which beats lossy transform coding for flat/graphics content. Compare luma J:
        // palette (SSE 0 + λ·palBits) vs the transform mode just chosen (its SSE + λ·coeff/mode bits).
        if (UsePalette && blk4 <= 16 && fullyInside)
        {
            Span<byte> present = stackalloc byte[256];
            int palSz = 0; bool tooMany = false;
            for (int yy = 0; yy < n && !tooMany; yy++)
                for (int xx = 0; xx < n; xx++)
                {
                    int px = c.Luma[(by + yy) * c.W + bx + xx];
                    if (present[px] == 0) { if (palSz == PaletteMaxColors) { tooMany = true; break; } present[px] = 1; palSz++; }
                }
            if (!tooMany && palSz >= 2)
            {
                var palColors = new ushort[8]; int pi = 0;
                for (int v = 0; v < 256; v++) if (present[v] != 0) palColors[pi++] = (ushort)v;
                int stride = blk4 * 4;
                var palIdx = new byte[stride * n];
                for (int yy = 0; yy < n; yy++)
                    for (int xx = 0; xx < n; xx++)
                    {
                        int px = c.Luma[(by + yy) * c.W + bx + xx];
                        int k = 0; while (palColors[k] != px) k++;
                        palIdx[yy * stride + xx] = (byte)k;
                    }

                long txSSE = 0;
                for (int yy = 0; yy < n; yy++)
                    for (int xx = 0; xx < n; xx++)
                    { int d = c.ReconY[(by + yy) * c.W + bx + xx] - c.Luma[(by + yy) * c.W + bx + xx]; txSSE += (long)d * d; }
                double txBits;
                if (depth == 0)
                {
                    int ySign0 = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
                    txBits = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, tx, 0, (int)yMode, yC, 0, ySign0, yTxIdx);
                }
                else { txBits = 0; foreach (var tb in lumaTxb!) txBits += Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, tb.Cf, tb.SkipCtx, tb.SignCtx, tb.Idx); }
                txBits += Av1CoeffEncode.SymBits(c.Cdf.GetKfYModeCdf(Av1Tables.IntraModeContext[c.AModeY[bxR]], Av1Tables.IntraModeContext[c.LModeY[byR]]), (int)yMode);

                int szCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
                int palCtx = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
                double palBits = Av1CoeffEncode.SymBits(c.Cdf.GetPalYCdf(szCtx, palCtx), 1)
                    + Av1CoeffEncode.SymBits(c.Cdf.GetPalSzCdf(0, szCtx), palSz - 2)
                    + palSz * 8
                    + EstimatePaletteIndexBits(c.Cdf.Mode, palIdx, palSz, n, n, blk4, blk4);
                double lambda = RdLambdaK * c.AcDq * c.AcDq;
                if (palBits * lambda < txSSE + txBits * lambda)
                {
                    EmitPaletteBlock(c, bx4, by4, blk4, bs, tx, n, palColors, palSz, palIdx, szCtx, palCtx, in maxTDim, cflAllowed, uvNsym);
                    return;
                }
            }
        }

        int dcU = DcPredict(c.ReconU, c.Cw, c.Chh, cbx, cby, cn, cn);
        int dcV = DcPredict(c.ReconV, c.Cw, c.Chh, cbx, cby, cn, cn);
        int scanLenC = Av1Tables.Scans[ctx0].Length;
        var qfU = new double[scanLenC]; var qfV = new double[scanLenC];
        int[] uC = ForwardResidual(c.U, c.Cw, cbx, cby, cn, dcU, c.DcDq, c.AcDq, scanLenC, qfU);
        int[] vC = ForwardResidual(c.V, c.Cw, cbx, cby, cn, dcV, c.DcDq, c.AcDq, scanLenC, qfV);

        // Chroma-from-luma: predict chroma AC from reconstructed luma AC scaled by a signed per-plane alpha; keep
        // CfL over DC only when it codes cheaper (incl. the alpha signalling). CfL-allowed sizes only.
        bool useCfl = false; int alphaU = 0, alphaV = 0; byte[]? cflU = null, cflV = null;
        if (cflAllowed && UseCfl)
        {
            var ac = new short[cn * cn];
            ComputeCflAcEnc(c.ReconY, c.W, bx, by, cn, ac);
            alphaU = BestCflAlpha(c.U, c.Cw, cbx, cby, cn, dcU, ac);
            alphaV = BestCflAlpha(c.V, c.Cw, cbx, cby, cn, dcV, ac);
            if (alphaU != 0 || alphaV != 0)
            {
                cflU = BuildCflPred(dcU, ac, cn, alphaU);
                cflV = BuildCflPred(dcV, ac, cn, alphaV);
                var qfUcfl = new double[scanLenC]; var qfVcfl = new double[scanLenC];
                int[] uCcfl = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, cflU, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfUcfl);
                int[] vCcfl = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, cflV, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfVcfl);
                // RD choice (SSE + λ·bits), not bits alone: CfL trades chroma distortion for fewer bits, so a
                // bits-only choice over-picks it and can raise chroma error. Reconstruct both and compare J.
                double lam = RdLambdaK * c.AcDq * c.AcDq;
                var dcuP = FlatPlane(dcU, cn); var dcvP = FlatPlane(dcV, cn);
                double dcJ = ChromaReconSse(uC, ctx0, cn, c.DcDq, c.AcDq, dcuP, c.U, c.Cw, cbx, cby)
                           + ChromaReconSse(vC, ctx0, cn, c.DcDq, c.AcDq, dcvP, c.V, c.Cw, cbx, cby)
                           + lam * (CoeffCost(uC) + CoeffCost(vC));
                double cflJ = ChromaReconSse(uCcfl, ctx0, cn, c.DcDq, c.AcDq, cflU, c.U, c.Cw, cbx, cby)
                            + ChromaReconSse(vCcfl, ctx0, cn, c.DcDq, c.AcDq, cflV, c.V, c.Cw, cbx, cby)
                            + lam * (CoeffCost(uCcfl) + CoeffCost(vCcfl) + 8 + (alphaU != 0 ? 5 : 0) + (alphaV != 0 ? 5 : 0));
                if (cflJ < dcJ) { useCfl = true; uC = uCcfl; vC = vCcfl; qfU = qfUcfl; qfV = qfVcfl; }
            }
        }

        // Chroma RDOQ: trim coefficients whose coding rate outweighs their (dq-scaled) distortion, the same
        // rate-distortion coefficient optimisation luma gets in the rect leaf. Chroma was previously left at
        // round-to-nearest quantisation, which over-codes it (measured 2-6 dB above luma at matched rate). Must
        // run before the skip/txb_skip decision below so an all-zeroed plane is coded as skipped.
        if (UseChromaRdoq)
        {
            double clam = ChromaRdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq;
            ref readonly var uvtd0 = ref Av1Tables.TxfmDimensions[ctx0];
            int ruSkip = Av1CoeffDecode.GetSkipCtx(in uvtd0, bs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int rvSkip = Av1CoeffDecode.GetSkipCtx(in uvtd0, bs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int ruSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int rvSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, uC, qfU, c.DcDq, c.AcDq, ruSkip, ruSign, 0, clam);
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, vC, qfV, c.DcDq, c.AcDq, rvSkip, rvSign, 0, clam);
        }

        int skip = (!lumaAllZero || HasNonZero(uC) || HasNonZero(vC)) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        int yAboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]];
        int yLeftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(yAboveCtx, yLeftCtx), (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        int uvSym = useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : 0;
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode), uvSym, uvNsym);
        if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);

        // has_palette flags (this non-palette block emits 0). When screen-content tools are on the decoder reads
        // has_palette_y for every size-eligible block whose Y mode is DC, and — INDEPENDENTLY — has_palette_uv for
        // every such block whose UV mode is DC (not CfL). The size gate max(bw4,bh4)<=16 && bw4+bh4>=4 is always
        // true for the square leaf's 8x8..64x64, so only the per-plane DC condition matters.
        if (UsePalette && blk4 <= 16)
        {
            int pSzCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
            if (yMode == Av1IntraPredMode.Dc)
            {
                int pCtx = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
                c.Msac.EncodeBoolAdapt(c.Cdf.GetPalYCdf(pSzCtx, pCtx), 0);
            }
            if (!useCfl) c.Msac.EncodeBoolAdapt(c.Cdf.GetPalUvCdf(0), 0);   // UvMode==DC
        }

        // tx_size (read_tx_size): coded for every intra block > 4x4 when tx_mode=SELECT — including skip blocks
        // (depth 0). Comes after all mode info, before residual, mirroring the decoder + grayscale path.
        if (UseColorTxDepth && maxTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), depth, Math.Min((int)maxTDim.Max, 2));
        }

        byte cfU = 0x40, cfV = 0x40;
        if (skip == 0)
        {
            ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[ctx0];
            int uSkip = Av1CoeffDecode.GetSkipCtx(in uvtDim, bs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int vSkip = Av1CoeffDecode.GetSkipCtx(in uvtDim, bs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int uSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int vSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));

            if (depth == 0)
            {
                int ySign = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, 0, (int)yMode, yC, dcSignCtx: ySign, txTypeIdx: yTxIdx);
            }
            else
            {
                foreach (var t in lumaTxb!)
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, t.Cf,
                        skipCtx: t.SkipCtx, dcSignCtx: t.SignCtx, txTypeIdx: t.Idx);
            }
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);

            if (useCfl)
            {
                cfU = DequantAndReconstructPredRect(uC, ctx0, cn, cn, c.DcDq, c.AcDq, cflU!, c.ReconU, c.Cw, cbx, cby);
                cfV = DequantAndReconstructPredRect(vC, ctx0, cn, cn, c.DcDq, c.AcDq, cflV!, c.ReconV, c.Cw, cbx, cby);
            }
            else
            {
                cfU = DequantAndReconstruct(uC, ctx0, cn, c.DcDq, c.AcDq, dcU, c.ReconU, c.Cw, cbx, cby);
                cfV = DequantAndReconstruct(vC, ctx0, cn, c.DcDq, c.AcDq, dcV, c.ReconV, c.Cw, cbx, cby);
            }
        }
        else if (useCfl)
        {
            CopyPlaneBlock(cflU!, cn, c.ReconU, c.Cw, cbx, cby);
            CopyPlaneBlock(cflV!, cn, c.ReconV, c.Cw, cbx, cby);
        }
        else
        {
            FillFlat(c.ReconU, c.Cw, cbx, cby, cn, dcU);
            FillFlat(c.ReconV, c.Cw, cbx, cby, cn, dcV);
        }

        int yW = Math.Min(blk4, c.Bw4 - bx4), yH = Math.Min(blk4, c.Bh4 - by4);
        int cW = Math.Min(cblk4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(cblk4, (c.Bh4 - by4 + 1) >> 1);
        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        // Luma LCoef context: for depth>0 it was already filled per-tx-block during reconstruction, so only fill it
        // here (with the single-transform cfY) at depth 0. Mode/skip/tx-size context is filled over the whole block.
        for (int i = 0; i < yW && bxR + i < 32; i++) { if (depth == 0) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = 0; }
        for (int j = 0; j < yH && byR + j < 32; j++) { if (depth == 0) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = 0; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = cfU; c.ACV[cxR + i] = cfV; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = cfU; c.LCV[cyR + j] = cfV; }
    }

    // Emits a fully-inside DC luma block as a palette block, in the decoder's exact order: skip=1, y_mode=DC,
    // uv_mode=DC, has_palette_y=1 + size + colours, has_palette_uv=0, palette indices, tx_size(depth 0), no
    // coeffs. Then reconstructs luma from the palette (chroma = DC pred) and updates neighbour state.
    private static void EmitPaletteBlock(ColorPartCtx c, int bx4, int by4, int blk4, int bs, int tx, int n,
        ushort[] palColors, int palSz, byte[] palIdx, int szCtx, int palCtx, in Av1TxfmInfo maxTDim, bool cflAllowed, int uvNsym)
    {
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> 1, cyR = byR >> 1;
        int bx = bx4 * 4, by = by4 * 4, cbx = bx4 * 2, cby = by4 * 2;
        int cn = n / 2, cblk4 = Math.Max(1, blk4 >> 1), stride = blk4 * 4;
        const int bitDepth = 8;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), 1);
        int yAbove = Av1Tables.IntraModeContext[c.AModeY[bxR]], yLeft = Av1Tables.IntraModeContext[c.LModeY[byR]];
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(yAbove, yLeft), 0, 12);         // DC
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, 0), 0, uvNsym);      // UV DC
        c.Msac.EncodeBoolAdapt(c.Cdf.GetPalYCdf(szCtx, palCtx), 1);
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetPalSzCdf(0, szCtx), palSz - 2, 6);

        int leftPalSz = c.LPalSz[byR]; if (leftPalSz > 8) leftPalSz = 0;
        int abovePalSz = (by4 & 15) != 0 ? c.APalSz[bxR] : 0; if (abovePalSz > 8) abovePalSz = 0;
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        for (int ci = 0; ci < leftPalSz; ci++) lCol[ci] = c.LPalCol[byR * 8 + ci];
        for (int ci = 0; ci < abovePalSz; ci++) aCol[ci] = c.APalCol[bxR * 8 + ci];
        Av1CoeffEncode.EncodeLumaPaletteColorsCore(c.Msac, palColors, palSz, lCol, leftPalSz, aCol, abovePalSz, bitDepth);

        c.Msac.EncodeBoolAdapt(c.Cdf.GetPalUvCdf(1), 0);                             // has_palette_uv = 0
        Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, palIdx, palSz, n, n, blk4, blk4, isLuma: true);

        if (UseColorTxDepth && maxTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), 0, Math.Min((int)maxTDim.Max, 2));
        }

        // Reconstruct luma from palette; chroma from DC prediction (skip ⇒ no residual on either).
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++)
                c.ReconY[(by + yy) * c.W + bx + xx] = (byte)palColors[palIdx[yy * stride + xx]];
        int dcU = DcPredict(c.ReconU, c.Cw, c.Chh, cbx, cby, cn, cn);
        int dcV = DcPredict(c.ReconV, c.Cw, c.Chh, cbx, cby, cn, cn);
        FillFlat(c.ReconU, c.Cw, cbx, cby, cn, dcU);
        FillFlat(c.ReconV, c.Cw, cbx, cby, cn, dcV);

        int yW = Math.Min(blk4, c.Bw4 - bx4), yH = Math.Min(blk4, c.Bh4 - by4);
        int cW = Math.Min(cblk4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(cblk4, (c.Bh4 - by4 + 1) >> 1);
        sbyte txLw = (sbyte)maxTDim.Lw, txLh = (sbyte)maxTDim.Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++)
        {
            c.ALY[bxR + i] = 0x40; c.AModeY[bxR + i] = 0; c.ASkip[bxR + i] = 1; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = (byte)palSz;
            for (int ci = 0; ci < palSz; ci++) c.APalCol[(bxR + i) * 8 + ci] = palColors[ci];
        }
        for (int j = 0; j < yH && byR + j < 32; j++)
        {
            c.LLY[byR + j] = 0x40; c.LModeY[byR + j] = 0; c.LSkip[byR + j] = 1; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = (byte)palSz;
            for (int ci = 0; ci < palSz; ci++) c.LPalCol[(byR + j) * 8 + ci] = palColors[ci];
        }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = 0x40; c.ACV[cxR + i] = 0x40; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = 0x40; c.LCV[cyR + j] = 0x40; }
    }

    // Estimates palette colour-index-map bits (uniform top-left + wavefront ranks under the current ColorMap CDF,
    // no adaptation) for the palette-vs-transform RD decision. Mirrors EncodePaletteIndices with SymBits.
    private static double EstimatePaletteIndexBits(Av1CdfModeContext modeCdf, byte[] idxMap, int palSize, int width, int height, int bw4, int bh4)
    {
        int stride = bw4 * 4;
        double bits = Math.Log2(palSize);   // top-left uniform
        Span<byte> order = stackalloc byte[8];
        int maxDiag = 4 * (bw4 + bh4) - 1;
        for (int diag = 1; diag < maxDiag; diag++)
        {
            int first = Math.Min(diag, width - 1), last = Math.Max(0, diag - height + 1);
            for (int x = first; x >= last; x--)
            {
                int y = diag - x;
                int l = x > 0 ? idxMap[y * stride + x - 1] : 0xFF;
                int tt = y > 0 ? idxMap[(y - 1) * stride + x] : 0xFF;
                int tl = (x > 0 && y > 0) ? idxMap[(y - 1) * stride + x - 1] : 0xFF;
                int ctx = Av1CoeffDecode.BuildColorOrder(order, palSize, l, tt, tl);
                int target = idxMap[y * stride + x], colorIdx = 0; while (order[colorIdx] != target) colorIdx++;
                bits += Av1CoeffEncode.SymBits(modeCdf.ColorMap[(palSize - 2) * 5 + ctx], colorIdx);
            }
        }
        return bits;
    }

    // Rate-DISTORTION cost J = SSE + λ·bits of a colour leaf's luma coded at a given (uniform) tx size. Each tx
    // block is predicted from the SOURCE plane (≈ reconstruction) and reconstructed through the decoder's inverse,
    // so the distortion term reflects what the transform can actually represent. Used for the colour tx-depth
    // decision: a pure rate estimate over-splits smooth content (splitting barely changes SSE but the estimate
    // undercounts the per-tx-block overhead), so the SSE term is essential to keep large blocks whole.
    private static long EstimateTxDepthCostColor(ColorPartCtx c, int bx4, int by4, int blk4, int tx,
        Av1IntraPredMode yMode, int yDelta)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
        int txN = tDim.W * 4, txW4 = tDim.W;
        double lambda = RdLambdaK * c.AcDq * c.AcDq;
        long sse = 0; double rate = 0;
        var predBuf = new byte[txN * txN];
        var reconBuf = new byte[txN * txN];
        for (int iy = 0; iy < blk4; iy += txW4)
            for (int ix = 0; ix < blk4; ix += txW4)
            {
                int cbx4 = bx4 + ix, cby4 = by4 + iy, px = cbx4 * 4, py = cby4 * 4;
                PredictIntra(c.Luma, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, predBuf);
                int[] res = ComputeResidualPred(c.Luma, c.W, px, py, predBuf, txN);
                (int[] cf, Av1TxType inv, int idx) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq, predBuf, c.Luma, c.W, px, py, RdLambdaK * c.AcDq * c.AcDq);
                // RDOQ the coefficients so this per-depth cost matches the actual RDOQ'd encode (both the depth-0
                // ChooseLeafRdCore path and the depth>0 emit RDOQ). Without this the estimate under-credits the
                // large transform, over-splitting smooth/natural content.
                if (HasNonZero(cf))
                {
                    var qfTx = new double[Av1Tables.Scans[tx].Length];
                    Av1FwdTransform.ForwardQuantTyped(res, txN, c.DcDq, c.AcDq, qfTx.Length, FwdTypeForIdx(idx), qfTx);
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, tx, 0, (int)yMode, cf, qfTx,
                        c.DcDq, c.AcDq, 0, 0, idx, RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);
                }
                DequantAndReconstructPred(cf, tx, txN, c.DcDq, c.AcDq, predBuf, reconBuf, txN, 0, 0, inv);
                for (int yy = 0; yy < txN; yy++)
                    for (int xx = 0; xx < txN; xx++)
                    { int d = reconBuf[yy * txN + xx] - c.Luma[(py + yy) * c.W + px + xx]; sse += (long)d * d; }
                rate += Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, tx, 0, (int)yMode, cf, 0, 0, idx) + 2;
            }

        return sse + (long)(lambda * rate);
    }

    // Estimates the luma coding cost J = SSE + λ·bits of a single rectangular leaf, predicting from the SOURCE
    // plane and reconstructing through the decoder's inverse — the same methodology as EstimateBlockCost, so the
    // PARTITION_HORZ / PARTITION_VERT costs compare fairly against NONE / SPLIT.
    private static long EstimateRectCostColor(ColorPartCtx c, int lumaTx, int bx4, int by4, int w4, int h4)
    {
        int w = w4 * 4, h = h4 * 4;
        int lScan = Av1Tables.Scans[lumaTx].Length;
        var pred = new byte[h * w];
        var bestPred = new byte[h * w];
        int[] bestCf = null!;
        long bestBits = long.MaxValue;
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntraRect(c.Luma, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred);
            int[] cf = ForwardResidualPredRect(c.Luma, c.W, bx4 * 4, by4 * 4, pred, w, h, lumaTx, c.DcDq, c.AcDq, lScan);
            long bits = CoeffCost(cf);
            if (bits < bestBits) { bestBits = bits; bestCf = cf; Array.Copy(pred, bestPred, h * w); }
        }

        var reconTmp = new byte[h * w];
        DequantAndReconstructPredRect(bestCf, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, reconTmp, w, 0, 0);
        long sse = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = reconTmp[y * w + x] - c.Luma[(by4 * 4 + y) * c.W + (bx4 * 4 + x)];
                sse += (long)d * d;
            }

        double lambda = RdLambdaK * c.AcDq * c.AcDq;
        return sse + (long)(lambda * (bestBits + HeaderCostBits));
    }

    // DC intra prediction for a w x h block from reconstructed neighbours — matches the decoder's DcGenBoth/Top/
    // Left (Av1IntraPred), INCLUDING the non-square reciprocal-multiplier correction (0x5556 for 1:2, 0x3334 for
    // 1:4). Square DcPredict cannot be used for rect blocks (w+h isn't a power of two).
    private static int DcPredictRect(byte[] recon, int reconW, int bx, int by, int w, int h)
    {
        bool haveTop = by > 0, haveLeft = bx > 0;
        if (haveTop && haveLeft)
        {
            int dc = (w + h) >> 1;
            for (int x = 0; x < w; x++) dc += recon[(by - 1) * reconW + bx + x];
            for (int y = 0; y < h; y++) dc += recon[(by + y) * reconW + bx - 1];
            dc >>= System.Numerics.BitOperations.TrailingZeroCount((uint)(w + h));
            if (w != h)
            {
                int mult = (w > h * 2 || h > w * 2) ? 0x3334 : 0x5556;
                dc = (int)(((uint)dc * (uint)mult) >> 16);
            }

            return dc;
        }

        if (haveTop)
        {
            int dc = w >> 1;
            for (int x = 0; x < w; x++) dc += recon[(by - 1) * reconW + bx + x];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)w);
        }

        if (haveLeft)
        {
            int dc = h >> 1;
            for (int y = 0; y < h; y++) dc += recon[(by + y) * reconW + bx - 1];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)h);
        }

        return 128;
    }

    private static void FillFlatRect(byte[] recon, int reconW, int bx, int by, int w, int h, int value)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                recon[(by + y) * reconW + (bx + x)] = (byte)value;
    }

    // Estimated chroma coding cost (both planes, DC-predicted from the block mean) of an I420 chroma block of
    // cw x ch pixels at chroma pixel position (cbx,cby), coded with transform chromaTx. Used to make the 16x16
    // partition decision chroma-aware (a HORZ/VERT split codes two chroma blocks vs NONE's one).
    private static long ChromaCostDc(ColorPartCtx c, int cbx, int cby, int cw, int ch, int chromaTx)
    {
        int scan = Av1Tables.Scans[chromaTx].Length;
        long sU = 0, sV = 0;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++) { sU += c.U[(cby + y) * c.Cw + cbx + x]; sV += c.V[(cby + y) * c.Cw + cbx + x]; }
        int n = cw * ch, dcU = (int)((sU + n / 2) / n), dcV = (int)((sV + n / 2) / n);
        return CoeffCost(ForwardResidualRectDc(c.U, c.Cw, cbx, cby, cw, ch, dcU, chromaTx, c.DcDq, c.AcDq, scan))
             + CoeffCost(ForwardResidualRectDc(c.V, c.Cw, cbx, cby, cw, ch, dcV, chromaTx, c.DcDq, c.AcDq, scan));
    }

    private static int[] ForwardResidualRectDc(ReadOnlySpan<byte> plane, int planeW, int bx, int by, int w, int h,
        int dc, int txIdx, int dcDq, int acDq, int rcCount)
    {
        var residual = new int[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) residual[y * w + x] = plane[(by + y) * planeW + (bx + x)] - dc;
        return Av1FwdTransform.ForwardQuantRect(residual, w, h, txIdx, dcDq, acDq, rcCount, Av1FwdTransform.FwdTxType.DctDct);
    }

    // Reconstructs a rect w x h block on top of a flat DC prediction, via the decoder's InvTxfmAdd.
    private static byte DequantAndReconstructRectDc(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        int dc, byte[] recon, int reconW, int bx, int by)
    {
        var pred = new byte[h * w];
        Array.Fill(pred, (byte)Math.Clamp(dc, 0, 255));
        return DequantAndReconstructPredRect(levels, txIdx, w, h, dcDq, acDq, pred, recon, reconW, bx, by);
    }

    // Codes one rectangular luma leaf (PARTITION_HORZ/VERT half) plus its I420 chroma: skip, Y mode (+angle),
    // UV mode (DC), then Y/U/V coefficients (rect transforms), and reconstructs all three planes. Chroma is DC-
    // predicted (no CfL for rect yet). Mirrors EncodeLeafBlockColor for a w4 x h4 (in 4-units) rectangle.
    private static void EncodeRectLeafColor(ColorPartCtx c, int lumaBs, int lumaTx, int chromaTx,
        int bx4, int by4, int w4, int h4, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        int w = w4 * 4, h = h4 * 4, cw = w >> 1, ch = h >> 1;
        int bx = bx4 * 4, by = by4 * 4, cbx = bx4 * 2, cby = by4 * 2;
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> 1, cyR = byR >> 1;
        int cw4 = Math.Max(1, w4 >> 1), ch4 = Math.Max(1, h4 >> 1);
        ref readonly var cTDim = ref Av1Tables.TxfmDimensions[chromaTx];
        int lScan = Av1Tables.Scans[lumaTx].Length, cScan = Av1Tables.Scans[chromaTx].Length;
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);

        // Luma: rate-based mode search (safe modes, DCT_DCT rect transform), writing the best prediction.
        int aboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]];
        int leftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        var ymCdf = c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        var pred = new byte[h * w];
        var bestPred = new byte[h * w];
        int[] yC = null!;
        Av1IntraPredMode yMode = Av1IntraPredMode.Dc; int yDelta = 0;
        double best = double.MaxValue;
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred, edgeFlags, IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]));
            int[] cf = ForwardResidualPredRect(c.Luma, c.W, bx, by, pred, w, h, lumaTx, c.DcDq, c.AcDq, lScan);
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            double rate = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, 1) + modeBits;
            if (rate < best) { best = rate; yC = cf; yMode = mode; yDelta = delta; Array.Copy(pred, bestPred, h * w); }
        }

        // RDOQ-refine the winning luma coefficients (recompute with pre-quant floats, then optimise).
        var qfWin = new double[lScan];
        var resWin = new int[h * w];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
                resWin[yy * w + xx] = c.Luma[(by + yy) * c.W + (bx + xx)] - bestPred[yy * w + xx];
        yC = Av1FwdTransform.ForwardQuantRect(resWin, w, h, lumaTx, c.DcDq, c.AcDq, lScan, Av1FwdTransform.FwdTxType.DctDct, qfWin);
        Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, qfWin, c.DcDq, c.AcDq, 0, ySign, 1,
            RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);

        int dcU = DcPredictRect(c.ReconU, c.Cw, cbx, cby, cw, ch);
        int dcV = DcPredictRect(c.ReconV, c.Cw, cbx, cby, cw, ch);
        int[] uC = ForwardResidualRectDc(c.U, c.Cw, cbx, cby, cw, ch, dcU, chromaTx, c.DcDq, c.AcDq, cScan);
        int[] vC = ForwardResidualRectDc(c.V, c.Cw, cbx, cby, cw, ch, dcV, chromaTx, c.DcDq, c.AcDq, cScan);
        int skip = (HasNonZero(yC) || HasNonZero(uC) || HasNonZero(vC)) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        c.Msac.EncodeSymbolAdapt(ymCdf, (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode), 0, uvNsym); // UV DC

        // tx_size: a rect leaf keeps its single rect transform (depth 0), but the symbol is still coded under
        // tx_mode=SELECT for every intra block > 4x4 — using the rect tx's own dimension table, matching the decoder.
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), 0, Math.Min((int)lTDim.Max, 2));
        }

        byte cfY = 0x40, cfU = 0x40, cfV = 0x40;
        if (skip == 0)
        {
            int uSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int vSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int uSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int vSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: 1);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);
            cfY = DequantAndReconstructPredRect(yC, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, c.ReconY, c.W, bx, by);
            cfU = DequantAndReconstructRectDc(uC, chromaTx, cw, ch, c.DcDq, c.AcDq, dcU, c.ReconU, c.Cw, cbx, cby);
            cfV = DequantAndReconstructRectDc(vC, chromaTx, cw, ch, c.DcDq, c.AcDq, dcV, c.ReconV, c.Cw, cbx, cby);
        }
        else
        {
            for (int yy = 0; yy < h; yy++) Array.Copy(bestPred, yy * w, c.ReconY, (by + yy) * c.W + bx, w);
            FillFlatRect(c.ReconU, c.Cw, cbx, cby, cw, ch, dcU);
            FillFlatRect(c.ReconV, c.Cw, cbx, cby, cw, ch, dcV);
        }

        int yW = Math.Min(w4, c.Bw4 - bx4), yH = Math.Min(h4, c.Bh4 - by4);
        int cW = Math.Min(cw4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(ch4, (c.Bh4 - by4 + 1) >> 1);
        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++) { c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; }
        for (int j = 0; j < yH && byR + j < 32; j++) { c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; }
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
        int dcDq, int acDq, int scanLen, double[]? qfOut = null)
    {
        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                residual[y * n + x] = plane[(by + y) * planeW + (bx + x)] - dcPred;
            }
        }

        return qfOut == null
            ? Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen)
            : Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, Av1FwdTransform.FwdTxType.DctDct, qfOut);
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
    private static void EncodePartition(GrayPartCtx c, int bl, int bx4, int by4, int edgeIdx = 0)
    {
        int hsz = 16 >> bl;          // half block in 4-units
        int blk4 = 32 >> bl;         // full block in 4-units
        int n = blk4 * 4;            // block pixels
        int SplitCh(int q) => Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], q);
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
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            return;
        }

        // Bottom edge (room across, none below): split_or_horz — we always force SPLIT (avoids rectangular blocks).
        if (haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherTopPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            EncodePartition(c, bl + 1, bx4 + hsz, by4, SplitCh(1));
            return;
        }
        // Right edge (room below, none across): split_or_vert — force SPLIT.
        if (!haveH && haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBool(1, Av1Decode.GatherLeftPartitionProb(partCdf, (Av1BlockLevel)bl));
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            EncodePartition(c, bl + 1, bx4, by4 + hsz, SplitCh(2));
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
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            EncodePartition(c, bl + 1, bx4 + hsz, by4, SplitCh(1));
            EncodePartition(c, bl + 1, bx4, by4 + hsz, SplitCh(2));
            EncodePartition(c, bl + 1, bx4 + hsz, by4 + hsz, SplitCh(3));
            return; // SPLIT nodes (bl<8x8) do not update partition context
        }

        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
        // Actual leaf: pick the best mode predicting from the real reconstruction, then code + reconstruct.
        Av1EdgeFlags leafEdge = Av1IntraEdgeTree.Tree64[edgeIdx].O;
        (Av1IntraPredMode yMode, int yDelta, _) =
            ChooseIntraMode(c.Recon, c.W, c.Bw4, c.Bh4, bx4, by4, n, c.Luma, c.W, bx4 * 4, by4 * 4, c.Pred, leafEdge);
        EncodeLeafBlock(c, bl, bx4, by4, blk4, n, yMode, yDelta, leafEdge);

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
        Av1IntraPredMode yMode, int yDelta, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        int maxTx = BlToTx(bl);
        int bxR = bx4 & 31, byR = by4 & 31;
        ref readonly var maxTDim = ref Av1Tables.TxfmDimensions[maxTx];

        // Rate-distortion mode + tx-type decision at the block-size transform (also fixes the skip flag and the
        // prediction in c.Pred). Overrides the SATD mode passed from the partition search.
        int[] coeffs0; Av1TxType invTx0; int txIdx0;
        if (UseRd)
        {
            var rd = ChooseLeafRd(c, bx4, by4, n, maxTx, edgeFlags);
            yMode = rd.Mode; yDelta = rd.Delta;
            coeffs0 = rd.Coeffs; invTx0 = rd.Inv; txIdx0 = rd.Idx;
        }
        else
        {
            PredictIntra(c.Recon, c.W, c.Bw4, c.Bh4, bx4, by4, n, yMode, yDelta, c.Pred, edgeFlags, IntraEdgeFlags(c.AboveMode[bxR], c.LeftMode[byR]));
            int[] res = ComputeResidualPred(c.Luma, c.W, bx4 * 4, by4 * 4, c.Pred, n);
            (coeffs0, invTx0, txIdx0) = ChooseTxType(res, n, maxTx, c.DcDq, c.AcDq, c.Pred, c.Luma, c.W, bx4 * 4, by4 * 4, RdLambdaK * c.AcDq * c.AcDq);
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
            int sbHasTr = (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0 ? 1 : 0;
            int sbHasBl = (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0 ? 1 : 0;
            // Per-tx-block: predict from reconstruction, code coeffs, reconstruct — in raster order.
            for (int iy = 0; iy < blk4; iy += txW4)
                for (int ix = 0; ix < blk4; ix += txW4)
                {
                    int cbx4 = bx4 + ix, cby4 = by4 + iy;
                    int cbxR = cbx4 & 31, cbyR = cby4 & 31;
                    var localEdge =
                        (((iy > 0 || sbHasTr == 0) && (ix + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                        ((ix > 0 || (sbHasBl == 0 && iy + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                    PredictIntra(c.Recon, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, predBuf, localEdge, IntraEdgeFlags(c.AboveMode[bxR], c.LeftMode[byR]));
                    int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, predBuf, txN);
                    (int[] cf, Av1TxType inv, int idx) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq, predBuf, c.Luma, c.W, cbx4 * 4, cby4 * 4, RdLambdaK * c.AcDq * c.AcDq);
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
                (int[] cf, _, _) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq, c.EstScratch, c.Luma, c.W, cbx4 * 4, cby4 * 4, RdLambdaK * c.AcDq * c.AcDq);
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

    // RD lagrangian weight: J = SSE + λ·bits, with λ ∝ quant-step². Drives the NONE/SPLIT/mode decisions AND (via
    // RdoqLambdaScale·RdLambdaK·Q²) the RDOQ coefficient trimming. Retuned 2026-09 on a 6-image BD-rate corpus
    // (photo/graphics/text/texture) jointly with RdoqLambdaScale=20 and DeadzoneBias=0.04 after chroma RDOQ was
    // added: 0.004 wins over the prior 0.008 (which had been tuned before chroma RDOQ existed) — the combined
    // retune is -6.2% BD-rate, improving on all six images. Re-swept AFTER tx_depth was enabled (which shifts the
    // joint optimum): 0.004->0.003 with RdoqLambdaScale 20->25 adds a further -1.15% BD-rate (clean across the
    // corpus). Note DeadzoneBias then wants to STAY at 0.04 — with tx_depth on, dropping it to 0.02 regresses all
    // six images (+3.7%; many small transforms over-keep coefficients at a low deadzone).
    // Re-swept AGAIN 2026-09-16 after tx/mode selection became full RD (D+λR, which added a λ=RdLambdaK·Q² term to
    // the candidate decision, so RdLambdaK now serves triple duty: SATD prescreen √λ, candidate selection λ, and
    // RDOQ scale·λ): 0.003->0.002 adds -0.39% BD-rate (piechart/mountains/logo win; granite +1.6% but we already
    // beat libaom there by 2.6%). RdoqLambdaScale stays 25 (dropping to 20 regressed +0.64%).
    internal static double RdLambdaK = 0.002;

    // Extra multiplier on the RDOQ lambda relative to the partition lambda. The partition lambda is tuned for
    // whole-block decisions; coefficient RDOQ needs a larger effective lambda to trade a marginal coefficient's
    // small distortion against its (EOB-inclusive) coding rate. Retuned to 20 (from 30) in the 2026-09 corpus
    // sweep jointly with RdLambdaK=0.004 / DeadzoneBias=0.04; then raised 20->25 in the tx_depth-on re-sweep
    // (jointly with RdLambdaK=0.003). Chroma uses the same scale (ChromaRdoqLambdaScale).
    internal static double RdoqLambdaScale = 25.0;

    // Chroma coefficient RDOQ in the square colour leaf (EncodeLeafBlockColor). Chroma was previously coded at
    // round-to-nearest with no rate-distortion trimming, running measurably richer than luma at matched rate;
    // this applies the same RDOQ to U/V. Scale kept equal to luma initially, tuned against the RD benchmark.
    internal static bool UseChromaRdoq = true;
    internal static double ChromaRdoqLambdaScale = 25.0;

    // Enables PARTITION_HORZ / PARTITION_VERT rectangular leaves at 16x16 (colour path). Toggle for A/B testing.
    internal static bool UseRectPartition = true;

    // Rect is chosen only when its estimated cost is below this fraction of the best square (NONE/SPLIT) cost.
    internal static double RectCostMargin = 0.95;

    // Enables true trial-encode RD for the colour partition decision (measure actual coded bits + SSE per
    // candidate). Much more accurate than the cost estimate (e.g. peppers qp15 RMSE 7.6→6.2 at −2.7% size), but
    // several times slower. Gated to TrueRdPixelBudget so very large frames keep the fast estimate path. The
    // budget covers typical photos (~2.5 MP): true-RD is a measured −1 to −2.8% on the 1.5 MP landscape test at
    // the cost of ~15 s, and the payoff (partitions the estimate over-splits) grows with frame detail. The path
    // is byte-identical to the small-frame one (ffmpeg/libdav1d-verified), just applied to more blocks.
    internal static bool UseTrueRd = true;
    internal static bool UseCfl = true;

    // Transform-size selection (tx_depth) in the COLOUR luma path — the grayscale path already does this. When on,
    // the colour frame header sets tx_mode=SELECT and every colour luma block codes a tx_size symbol (square leaves
    // search depth 0..2 by SSE+λ·bits; rect leaves keep their single rect transform = depth 0). Verified byte-exact
    // in dav1d/ffmpeg (peppers qp10, 1300+ depth>0 blocks). DEFAULT OFF: measured net-neutral-to-slightly-negative
    // overhead. ENABLED 2026-09 after two fixes made it a net win: (1) the depth>0 tx-split blocks are now RDOQ'd
    // (was round-to-nearest), and (2) the tx-depth decision (EstimateTxDepthCostColor) RDOQs its per-depth trial
    // coefficients so it compares fairly against the RDOQ'd large transform instead of over-splitting. On the
    // 6-image corpus (with the retuned lambdas): -3.4% BD-rate average — big on graphics/screen content
    // (logo/piechart ~-8.8%, wizard -3%), neutral on natural photos (mountains -0.2%, bluebells/granite ~+0.1%
    // noise). tx_size bitstream verified byte-exact in dav1d/ffmpeg.
    internal static bool UseColorTxDepth = true;
    internal static long TrueRdPixelBudget = 1600 * 1600;
    internal static double EarlyTermBits = 24.0;

    // Intra edge filtering + upsampling for directional prediction (AV1 enable_intra_edge_filter). The decoder
    // already implements it fully (Av1IntraPred.PredZ1/Z2/Z3 do the filter/upsample, gated on bit 10 of `angle`;
    // the Z2 corner filter on PrepareIntraEdges' filterEdge). Enabling it: set the seq-header flag and have the
    // encoder OR the intra flags (bit 10 = enable, bit 9 = smooth-neighbour, mirroring the decoder's SmFlag) into
    // the resolved angle + pass filterEdge:true. Improves the directional modes' prediction quality.
    internal static bool UseIntraEdgeFilter = true;
    private const int EdgeFilterEnableBit = 1 << 10;
    private const int SmoothNeighbourBit = 1 << 9;

    // Per-block intra flags OR'd into the prediction angle: the edge-filter-enable bit (when the seq flag is on)
    // plus the smooth-neighbour bit if either the above or left neighbour used a SMOOTH mode (mirrors the decoder's
    // SmFlag, which drives the filter-strength / upsample decision). Neighbours are intra on a key frame.
    private static int IntraEdgeFlags(int aboveMode, int leftMode)
    {
        if (!UseIntraEdgeFilter) return 0;
        int f = EdgeFilterEnableBit;
        if (IsSmoothMode(aboveMode) || IsSmoothMode(leftMode)) f |= SmoothNeighbourBit;
        return f;
    }
    private static bool IsSmoothMode(int m) =>
        m == (int)Av1IntraPredMode.Smooth || m == (int)Av1IntraPredMode.SmoothV || m == (int)Av1IntraPredMode.SmoothH;

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


    // How many SATD-best modes the RD leaf search fully rate-evaluates (of ~34 candidates). 4 keeps essentially
    // all of the quality while cutting the hot rate-search ~8x, which is what makes true-RD affordable.
    private const int RdModeCandidates = 4;

    private static (Av1IntraPredMode, int)[] BuildCandidates()
    {
        var list = new List<(Av1IntraPredMode, int)>
        {
            (Av1IntraPredMode.Dc, 0), (Av1IntraPredMode.Smooth, 0),
            (Av1IntraPredMode.SmoothV, 0), (Av1IntraPredMode.SmoothH, 0), (Av1IntraPredMode.Paeth, 0),
        };
        // Full directional set: all 8 directional modes with the complete angle_delta range. Modes whose angle
        // needs the top-right / bottom-left edge are now correct because the leaf threads the real intra-edge
        // availability flags (from Av1IntraEdgeTree, mirroring the decoder) into PrepareIntraEdges.
        for (int m = (int)Av1IntraPredMode.Vertical; m <= (int)Av1IntraPredMode.VerticalLeft; m++)
            for (int d = -3; d <= 3; d++) list.Add(((Av1IntraPredMode)m, d));
        return list.ToArray();
    }

    // True for the 8 directional intra modes (Vertical..VerticalLeft) that carry an angle_delta symbol.
    private static bool IsDirectional(Av1IntraPredMode m) =>
        m >= Av1IntraPredMode.Vertical && m <= Av1IntraPredMode.VerticalLeft;

    // Predicts an n x n luma block with the given intra mode into dst (stride n), reusing the decoder's own edge
    // preparation + prediction so encoder and decoder agree bit-for-bit. recon is the reconstruction plane
    // (stride reconW), bx4/by4 the block position in 4-unit units, bw4/bh4 the frame size in 4-unit units.
    private static void PredictIntra(byte[] recon, int reconW, int bw4, int bh4, int bx4, int by4, int n,
        Av1IntraPredMode mode, int delta, byte[] dst, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int intraFlags = 0)
    {
        Span<byte> edge = stackalloc byte[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = n >> 2;
        bool haveTop = by4 > 0;
        bool haveLeft = bx4 > 0;
        int angle = delta; // PrepareIntraEdges folds this into the base angle for directional modes
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, bw4, bh4, edgeFlags,
            recon, dstOff, reconW, default, mode, ref angle, tw4, tw4,
            filterEdge: (intraFlags & EdgeFilterEnableBit) != 0, edge, edgeCenter, 8);
        Av1IntraPred.Predict(m, dst, n, edge, edgeCenter, n, n, angle | intraFlags,
            4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4);
    }

    // Chooses the intra mode with the lowest residual SATD (sum of absolute Hadamard-transformed differences) —
    // a frequency-domain cost proxy that tracks DCT coding cost far better than raw SAD, so smooth ramps and
    // directional edges are scored the way the transform will actually code them. Returns the winning (mode,
    // angle_delta) and writes its prediction into predOut (n x n). Purely an encoder decision: any candidate is
    // a valid mode, so this can never desync the decoder.
    private static (Av1IntraPredMode Mode, int Delta, long Cost) ChooseIntraMode(byte[] recon, int reconW, int bw4, int bh4,
        int bx4, int by4, int n, ReadOnlySpan<byte> src, int srcW, int srcBx, int srcBy, byte[] predOut,
        Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        long best = long.MaxValue;
        (Av1IntraPredMode Mode, int Delta) bestCand = (Av1IntraPredMode.Dc, 0);
        var tmp = new byte[n * n];
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, tmp, edgeFlags);
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

    // Reconstruction SSE of an n x n luma candidate: dequant `levels` (mirrors the decoder), inverse-transform onto
    // `predBlock`, and sum squared error vs the source. Used to score tx-type / mode candidates by true RD (D + λR)
    // rather than rate alone — necessary because IDTX changes the reconstruction distortion at a matched quantizer
    // (unlike the DCT/ADST family), so a rate-only comparison over-selects it on smooth content.
    private static long ReconSseCand(int[] levels, int tx, int n, int dcDq, int acDq, byte[] predBlock,
        byte[] src, int srcW, int srcBx, int srcBy, Av1TxType txType)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        const int cfMax = 32767;
        var scan = Av1Tables.Scans[tx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--) if (levels[scan[i]] != 0) { eob = i; break; }
        var cf = new int[Math.Max(n * n, 32 * 32)];
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i], lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl), sign = lvl < 0 ? 1 : 0;
            int dq = Math.Min(((rc == 0 ? dcDq : acDq) * mag) >> dqShift, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
        }
        var block = (byte[])predBlock.Clone();
        Av1InvTransform.InvTxfmAdd(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], txType, 8);
        long sse = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) { int d = block[y * n + x] - src[(srcBy + y) * srcW + (srcBx + x)]; sse += (long)d * d; }
        return sse;
    }

    // Forward-transforms and quantizes (src block - prediction) for an n x n block.
    // The reduced intra tx set (Intra2) types searched for luma tx ≤ 16x16 (where the type is signalled), as
    // (forward type, inverse type, symbol index in TxTypesPerSet Intra2). DctDct is idx 1; ADST combos 2/3/4.
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] IntraTxTypes =
    {
        (Av1FwdTransform.FwdTxType.Identity, Av1TxType.Identity, 0),
        (Av1FwdTransform.FwdTxType.DctDct,   Av1TxType.DctDct,   1),
        (Av1FwdTransform.FwdTxType.AdstAdst, Av1TxType.AdstAdst, 2),
        (Av1FwdTransform.FwdTxType.AdstDct,  Av1TxType.AdstDct,  3),
        (Av1FwdTransform.FwdTxType.DctAdst,  Av1TxType.DctAdst,  4),
    };
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] DctOnly =
        { (Av1FwdTransform.FwdTxType.DctDct, Av1TxType.DctDct, 1) };

    // Forward transform type for a chosen intra tx-type index (1=Dct_Dct, 2=Adst_Adst, 3=Adst_Dct, 4=Dct_Adst),
    // used to re-derive the pre-quant floats (qf) for RDOQ of a tx-block whose type ChooseTxType already picked.
    private static Av1FwdTransform.FwdTxType FwdTypeForIdx(int idx) => idx switch
    {
        0 => Av1FwdTransform.FwdTxType.Identity,
        2 => Av1FwdTransform.FwdTxType.AdstAdst,
        3 => Av1FwdTransform.FwdTxType.AdstDct,
        4 => Av1FwdTransform.FwdTxType.DctAdst,
        _ => Av1FwdTransform.FwdTxType.DctDct,
    };

    // Gray wrapper for the primitive-arg RD leaf decision.
    private static (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx)
        ChooseLeafRd(GrayPartCtx c, int bx4, int by4, int n, int tx, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        int bxR = bx4 & 31, byR = by4 & 31;
        return ChooseLeafRdCore(c.Recon, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AboveMode[bxR], c.LeftMode[byR], c.AboveLCoef.AsSpan(bxR), c.LeftLCoef.AsSpan(byR), c.Pred, edgeFlags);
    }

    // Rate-distortion leaf decision: over all candidate (intra mode, tx type) pairs, pick the one with the lowest
    // actual coded rate — coefficient bits (EstimateCoefBits, from the live CDFs) plus the mode/angle signalling
    // bits. This replaces the SATD proxy: it directly minimises what the bitstream costs and lets the tx-type
    // (ADST/DCT) choice compound with the mode choice. Writes the winning prediction into predOut.
    private static (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx)
        ChooseLeafRdCore(byte[] recon, int reconW, int bw4, int bh4, byte[] luma, int lumaW, int bx4, int by4,
            int n, int tx, int dcDq, int acDq, Av1CdfContext cdf, byte aboveMode, byte leftMode,
            ReadOnlySpan<byte> aboveLCoef, ReadOnlySpan<byte> leftLCoef, byte[] predOut,
            Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        int aboveCtx = Av1Tables.IntraModeContext[aboveMode];
        int leftCtx = Av1Tables.IntraModeContext[leftMode];
        Span<ushort> ymCdf = cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, aboveLCoef, leftLCoef);
        int intraFlags = IntraEdgeFlags(aboveMode, leftMode);
        int scanLen = Av1Tables.Scans[tx].Length;
        var predBuf = new byte[n * n];
        var qfCand = new double[scanLen];
        var qfWin = new double[scanLen];

        // Pre-screen all candidate modes by cheap SATD and RD-evaluate only the best few — the full rate search
        // (forward transform + EstimateCoefBits over every tx-type) is the encoder's hot loop, and SATD tracks the
        // eventual coded cost closely enough that the top handful almost always contains the RD winner.
        Span<int> topIdx = stackalloc int[RdModeCandidates];
        Span<long> topCost = stackalloc long[RdModeCandidates];
        topCost.Fill(long.MaxValue);
        double satdLambda = Math.Sqrt(RdLambdaK) * acDq; // ~rate weight in SATD units
        for (int ci = 0; ci < CandidateModes.Length; ci++)
        {
            (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, predBuf, edgeFlags, intraFlags);
            long satd = Satd8x8(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
            long mb = (long)(satdLambda * (Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0)));
            long cost = satd + mb;
            for (int k = 0; k < RdModeCandidates; k++)
                if (cost < topCost[k]) { for (int j = RdModeCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = cost; topIdx[k] = ci; break; }
        }

        double best = double.MaxValue;
        double rdLambda = RdLambdaK * acDq * acDq;   // pixel-SSE units per bit (same λ as the palette RD gate)
        (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx) bestCand = default;
        for (int t = 0; t < RdModeCandidates; t++)
        {
            if (topCost[t] == long.MaxValue) break;
            (Av1IntraPredMode mode, int delta) = CandidateModes[topIdx[t]];
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, predBuf, edgeFlags, intraFlags);
            int[] residual = ComputeResidualPred(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            foreach (var (fwd, inv, idx) in n <= 16 ? IntraTxTypes : DctOnly)
            {
                int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd, qfCand);
                double rate = Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, (int)mode, cf, 0, dcSignCtx, idx) + modeBits;
                long sse = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv);
                double j = sse + rdLambda * rate;   // true RD: distortion + λ·rate (IDTX changes distortion, so rate alone misranks it)
                if (j < best) { best = j; bestCand = (mode, delta, cf, inv, idx); Array.Copy(predBuf, predOut, n * n); Array.Copy(qfCand, qfWin, scanLen); }
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


    // === Rectangular block helpers (w x h, w != h) — mirror the square versions but keep width/height separate.
    // Used by PARTITION_HORZ / PARTITION_VERT leaves. Prediction/reconstruction reuse the decoder's own
    // PrepareIntraEdges + Av1IntraPred.Predict + InvTxfmAdd, so they are conformant by construction. ===

    // Intra prediction for a w x h block into dst (h rows x w cols, stride w).
    private static void PredictIntraRect(byte[] recon, int reconW, int bw4, int bh4, int bx4, int by4,
        int w, int h, Av1IntraPredMode mode, int delta, byte[] dst, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int intraFlags = 0)
    {
        Span<byte> edge = stackalloc byte[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = w >> 2, th4 = h >> 2;
        bool haveTop = by4 > 0, haveLeft = bx4 > 0;
        int angle = delta;
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, bw4, bh4, edgeFlags,
            recon, dstOff, reconW, default, mode, ref angle, tw4, th4,
            filterEdge: (intraFlags & EdgeFilterEnableBit) != 0, edge, edgeCenter, 8);
        Av1IntraPred.Predict(m, dst, w, edge, edgeCenter, w, h, angle | intraFlags, 4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4);
    }

    // Forward+quant of (src - pred) for a w x h block. pred is h x w row-major.
    private static int[] ForwardResidualPredRect(ReadOnlySpan<byte> src, int srcW, int srcBx, int srcBy,
        byte[] pred, int w, int h, int txIdx, int dcDq, int acDq, int rcCount, double[]? qfOut = null)
    {
        var residual = new int[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                residual[y * w + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * w + x];
        return Av1FwdTransform.ForwardQuantRect(residual, w, h, txIdx, dcDq, acDq, rcCount, Av1FwdTransform.FwdTxType.DctDct, qfOut);
    }

    // Dequantizes rect levels and reconstructs a w x h block onto predBlock (h x w) via the decoder's InvTxfmAdd,
    // into recon. Returns the coefficient-context byte.
    private static byte DequantAndReconstructPredRect(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        byte[] predBlock, byte[] recon, int reconW, int bx, int by)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[txIdx].Ctx - 2);
        const int cfMax = 32767;
        var scan = Av1Tables.Scans[txIdx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
            if (levels[scan[i]] != 0) { eob = i; break; }

        var cf = new int[32 * 32];
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
        Av1InvTransform.InvTxfmAdd(block, w, cf, eob, txIdx, Av1InvTransform.TxShift[txIdx], Av1TxType.DctDct, 8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                recon[(by + y) * reconW + (bx + x)] = block[y * w + x];

        return cfCtx;
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
    private static (int[] Coeffs, Av1TxType Inv, int Idx) ChooseTxType(int[] residual, int n, int tx, int dcDq, int acDq,
        byte[] predBlock, byte[] src, int srcW, int srcBx, int srcBy, double rdLambda)
    {
        int scanLen = Av1Tables.Scans[tx].Length;
        if (n > 16)
            return (Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen), Av1TxType.DctDct, 1);

        double best = double.MaxValue;
        (int[], Av1TxType, int) bestCand = default;
        foreach (var (fwd, inv, idx) in IntraTxTypes)
        {
            int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd);
            // True RD: distortion + λ·rate. Rate alone over-selects IDTX on smooth content (see ChooseLeafRdCore).
            double j = ReconSseCand(cf, tx, n, dcDq, acDq, predBlock, src, srcW, srcBx, srcBy, inv) + rdLambda * CoeffCost(cf);
            if (j < best) { best = j; bestCand = (cf, inv, idx); }
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
