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
    // Coding bit depth (8/10/12) for the current encode, set by BitDepthScope at every multi-superblock entry.
    // Thread-static: the encoder runs single-threaded per call but callers may encode different images on
    // different threads. The whole pixel pipeline runs in ushort through the decoder's own 16-bit twins
    // (Predict16 / InvTxfmAdd16 / PrepareIntraEdges<ushort>), which Av1HbdTwinTests pin to the 8-bit results at
    // bd=8 — so 8-bit streams are unchanged — and which the decoder itself uses for every bit depth.
    [ThreadStatic] private static int t_bd;
    private static int Bd => t_bd == 0 ? 8 : t_bd;
    private static int PixMax => (1 << Bd) - 1;
    private static int PixMid => 1 << (Bd - 1);
    private static int BdIdx => Bd == 8 ? 0 : Bd == 10 ? 1 : 2;
    // Dequantized-coefficient saturation, exactly the decoder's cf_max = ~(~127 << bpc) (32767 at 8-bit).
    private static int CfMax => ~(~127 << Bd);

    private readonly struct BitDepthScope : IDisposable
    {
        private readonly int prev;
        public BitDepthScope(int bd)
        {
            if (bd is not (8 or 10 or 12)) throw new ArgumentOutOfRangeException(nameof(bd), "AV1 bit depth must be 8, 10 or 12.");
            prev = t_bd;
            t_bd = bd;
        }
        public void Dispose() => t_bd = prev;
    }

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
        int width, int height, int baseQIdx, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (width % 2 != 0 || height % 2 != 0 || !TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new NotSupportedException(
                $"Colour AVIF encode requires an even near-square size mapping to one square block (got {width}x{height}).");
        }

        byte[] tile = EncodeColorTile(luma, u, v, width, height, plan, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: false, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, 1, 1, monochrome: false);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false, color: color, extras: extras);
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
    internal static byte[] EncodeAvifMonochromeMultiSb(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
        => EncodeAvifMonochromeMultiSb(Widen(luma), width, height, baseQIdx, 8, color, extras);

    /// <summary>High-bit-depth monochrome entry: <paramref name="luma"/> holds samples in [0, 2^bitDepth).</summary>
    internal static byte[] EncodeAvifMonochromeMultiSb(ReadOnlySpan<ushort> luma, int width, int height, int baseQIdx, int bitDepth,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] seqObu, byte[] frameObu) = BuildMonochromeObus(luma, width, height, baseQIdx, bitDepth, color);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true, bitDepth, color: color, extras: extras);
    }

    // Widens 8-bit samples to the encoder's ushort pixel type (identity values).
    private static ushort[] Widen(ReadOnlySpan<byte> src)
    {
        var d = new ushort[src.Length];
        for (int i = 0; i < src.Length; i++) d[i] = src[i];
        return d;
    }

    /// <summary>Builds the sequence-header + OBU_FRAME for a monochrome (I400) multi-superblock key frame — the
    /// shared core used both for a standalone grayscale AVIF and for an AVIF alpha auxiliary item.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildMonochromeObus(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx)
        => BuildMonochromeObus(Widen(luma), width, height, baseQIdx, 8);

    internal static (byte[] SeqObu, byte[] FrameObu) BuildMonochromeObus(ReadOnlySpan<ushort> luma, int width, int height, int baseQIdx, int bitDepth,
        Av1ObuWriter.Av1ColorDesc? color = null)
    {
        using var bdScope = new BitDepthScope(bitDepth);
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        ushort[] padded = PadPlane(luma, width, height, pw, ph);
        byte[] tile = EncodeMultiSbTile(padded, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true, bitDepth: bitDepth, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));

        // CDEF strength search: the tile is CDEF-independent (cdef_bits=0), so try candidate strengths by decoding
        // each and keeping the one with the lowest reconstruction SSE vs source (always incl. the no-op, so it can
        // never hurt). The decoder applies CDEF as an output filter; intra prediction used pre-CDEF recon.
        ushort[] srcCopy = luma.ToArray();
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
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, monochrome, txModeSelect: monochrome || (!monochrome && UseColorTxDepth), cdef, lfLevel, screenContentTools: UsePalette && !monochrome, reducedTxSet: !(!monochrome && UseFullIntraTxSet));
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        return Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
    }

    // Searches a single global CDEF strength set (cdef_bits=0) that minimises reconstruction SSE. Decodes each
    // candidate through our own decoder (which matches libdav1d's CDEF), comparing the decoded planes to the
    // source. srcY is width*height; srcU/srcV (cw*ch) are only used when !monochrome.
    private static Av1ObuWriter.CdefParams SearchCdef(byte[] seqObu, byte[] tileRef, int baseQIdx, int sbCols, int sbRows,
        int width, int height, bool monochrome, ushort[] srcY, ushort[]? srcU, ushort[]? srcV, int cw, int ch, int lfLevel = 0)
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
            var dec = new Av1Decoder { ApplyFilmGrain = false };   // the filter search measures the grain-free recon
            using var yuv = dec.Decode(tu, 0, isKeyframe: true);
            if (yuv == null) return long.MaxValue;
            long sse = DecodedSse(yuv, srcY, srcU, srcV, width, height, cw, ch, monochrome);

            return sse;
        }

        // CDEF's still-image payoff is modest (~1% RMSE) and its verification costs a full re-decode, so it is
        // applied only where that trade is worth it: lossy quality (baseQIdx >= 64, below which CDEF risks
        // blurring fine detail) and images small enough that 1-2 decodes are cheap. Large frames — where the
        // re-decode is expensive and CDEF's gain on detailed content is near zero — skip it. Within that gate we
        // evaluate the no-op plus one q-scaled heuristic strength and keep whichever decodes closer to the
        // source, so it can never regress vs no CDEF.
        if (!UseCdefSearch || baseQIdx < 64 || (long)width * height > 512 * 512) return Av1ObuWriter.CdefParams.None;

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
    internal static bool UseCdefSearch = true;      // toggle the CDEF strength search (A/B, conformance isolation)

    // Palette mode for colour (screen-content). When on, the frame enables screen_content_tools and eligible
    // DC luma blocks may be coded as palette; rect partitions are disabled to keep palette to the square leaf.
    internal static bool UsePalette = false;
    internal static int PaletteMaxColors = 8;   // AV1 caps luma palette at 8
    // Filter-intra: the recursive 4x2 filter predictor (5 modes) for DC-eligible luma blocks <=32x32. Colour-only.
    // A filter block codes y_mode=DC + use_filter_intra + filter_mode. THREE distinct "mode" values result (all
    // verified against dav1d): the coded y_mode SYMBOL = DC; the tx-type coefficient context = FilterModeToYMode[fm]
    // (dav1d recon_tmpl.c: filter_mode_to_y_mode); the NEIGHBOUR mode context = DC (dav1d decode.c: FILTER_PRED->DC).
    // -0.33% BD-rate (clean on 5/6 corpus images), byte-exact vs ffmpeg/libdav1d.
    internal static bool UseFilterIntra = true;

    private static int SearchDeblock(byte[] seqObu, byte[] tileRef, int baseQIdx, int sbCols, int sbRows,
        int width, int height, bool monochrome, ushort[] srcY, ushort[]? srcU, ushort[]? srcV, int cw, int ch)
    {
        // Deblocking's still-image payoff is largest at coarse quantisation; skip the extra decodes when tiny.
        if (!UseDeblockSearch || (long)width * height > 512 * 512) return 0;

        long Evaluate(int lvl)
        {
            byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome, tileRef, Av1ObuWriter.CdefParams.None, lvl);
            var tu = new byte[seqObu.Length + frameObu.Length];
            seqObu.CopyTo(tu, 0);
            frameObu.CopyTo(tu, seqObu.Length);
            var dec = new Av1Decoder { ApplyFilmGrain = false };   // the filter search measures the grain-free recon
            using var yuv = dec.Decode(tu, 0, isKeyframe: true);
            if (yuv == null) return long.MaxValue;
            long sse = DecodedSse(yuv, srcY, srcU, srcV, width, height, cw, ch, monochrome);
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

    // Reconstruction SSE of a decoded frame vs the source planes, at the coded precision (native 10/12-bit
    // samples for high-bit-depth streams, the 8-bit planes otherwise).
    private static long DecodedSse(DecodedVideoFrame yuv, ushort[] srcY, ushort[]? srcU, ushort[]? srcV,
        int width, int height, int cw, int ch, bool monochrome)
    {
        bool hbd = yuv.BitDepth > 8;
        long sse = hbd ? PlaneSse(yuv.YPlane16.Span, yuv.YStride, srcY, width, height)
                       : PlaneSse(yuv.YPlane.Span, yuv.YStride, srcY, width, height);
        if (!monochrome && srcU != null && srcV != null)
        {
            sse += hbd ? PlaneSse(yuv.UPlane16.Span, yuv.UStride, srcU, cw, ch) : PlaneSse(yuv.UPlane.Span, yuv.UStride, srcU, cw, ch);
            sse += hbd ? PlaneSse(yuv.VPlane16.Span, yuv.VStride, srcV, cw, ch) : PlaneSse(yuv.VPlane.Span, yuv.VStride, srcV, cw, ch);
        }
        return sse;
    }

    private static long PlaneSse(ReadOnlySpan<byte> dec, int stride, ushort[] src, int w, int h)
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

    private static long PlaneSse(ReadOnlySpan<ushort> dec, int stride, ushort[] src, int w, int h)
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
        int width, int height, int baseQIdx, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
        => EncodeAvifColorMultiSb(Widen(luma), Widen(u), Widen(v), width, height, baseQIdx, 8, color: color, extras: extras);

    /// <summary>High-bit-depth I420 colour entry: planes hold samples in [0, 2^bitDepth).</summary>
    internal static byte[] EncodeAvifColorMultiSb(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        int width, int height, int baseQIdx, int bitDepth, Av1PixelLayout layout = Av1PixelLayout.I420,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] seqObu, byte[] frameObu) = BuildColorObus(luma, u, v, width, height, baseQIdx, bitDepth, layout, color);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false, bitDepth, layout, color, extras);
    }

    /// <summary>Builds the sequence-header + OBU_FRAME for an I420 colour multi-superblock key frame.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildColorObus(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx)
        => BuildColorObus(Widen(luma), Widen(u), Widen(v), width, height, baseQIdx, 8);

    /// <summary>Builds the sequence header + OBU_FRAME for a colour key frame in the given chroma layout. Chroma planes
    /// are ceil(width >> ssX) x ceil(height >> ssY) (ssX = 0 for 4:4:4, ssY = 1 only for 4:2:0).</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildColorObus(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        int width, int height, int baseQIdx, int bitDepth, Av1PixelLayout layout = Av1PixelLayout.I420,
        Av1ObuWriter.Av1ColorDesc? color = null)
    {
        if (layout is not (Av1PixelLayout.I420 or Av1PixelLayout.I422 or Av1PixelLayout.I444))
            throw new ArgumentOutOfRangeException(nameof(layout));
        using var bdScope = new BitDepthScope(bitDepth);
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 ? 1 : 0;
        int cwIn = (width + ssX) >> ssX, chIn = (height + ssY) >> ssY;   // ceil — odd dims keep a partial edge sample
        ushort[] padY = PadPlane(luma, width, height, pw, ph);
        ushort[] padU = PadPlane(u, cwIn, chIn, pw >> ssX, ph >> ssY);
        ushort[] padV = PadPlane(v, cwIn, chIn, pw >> ssX, ph >> ssY);
        byte[] tile = EncodeMultiSbColorTile(padY, padU, padV, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx, layout);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: false, enableFilterIntra: UseFilterIntra, bitDepth: bitDepth, layout: layout, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));

        // In-loop filter search (deblock first, then CDEF with the chosen level; see BuildMonochromeObus).
        ushort[] srcY = luma.ToArray(), srcU = u.ToArray(), srcV = v.ToArray();
        int lfLevel = SearchDeblock(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: false, srcY, srcU, srcV, cwIn, chIn);
        Av1ObuWriter.CdefParams best = SearchCdef(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: false, srcY, srcU, srcV, cwIn, chIn, lfLevel);
        byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome: false, tile, best, lfLevel);
        return (seqObu, frameObu);
    }

    /// <summary>Lossless key frame (base_q_idx 0, 4x4 WHT, no in-loop filters) for a colour image in any chroma layout
    /// (<paramref name="u"/>/<paramref name="v"/> null = monochrome, e.g. an alpha plane). The decoded samples equal the
    /// input exactly.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildLosslessObus(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u,
        ReadOnlySpan<ushort> v, bool monochrome, int width, int height, int bitDepth, Av1PixelLayout layout,
        Av1ObuWriter.Av1ColorDesc? color)
    {
        using var bdScope = new BitDepthScope(bitDepth);
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        ushort[] padY = PadPlane(luma, width, height, pw, ph);
        ushort[]? padU = null, padV = null;
        if (!monochrome)
        {
            int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 ? 1 : 0;
            int cwIn = (width + ssX) >> ssX, chIn = (height + ssY) >> ssY;
            padU = PadPlane(u, cwIn, chIn, pw >> ssX, ph >> ssY);
            padV = PadPlane(v, cwIn, chIn, pw >> ssX, ph >> ssY);
        }
        byte[] tile = Av1LosslessEncoder.EncodeTiles(padY, padU, padV, pw, bw4, bh4, sbCols, sbRows,
            monochrome ? Av1PixelLayout.I400 : layout, bitDepth, UseIntraEdgeFilter);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome, enableFilterIntra: false, bitDepth: bitDepth,
            layout: layout, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(0, isObuFrame: true, sbCols, sbRows, monochrome,
            txModeSelect: false, Av1ObuWriter.CdefParams.None, 0, screenContentTools: false, reducedTxSet: true);
        var payload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(payload, 0);
        tile.CopyTo(payload.AsSpan(frameHdr.Length));
        return (seqObu, Av1ObuWriter.WrapObu(Av1ObuType.Frame, payload));
    }

    /// <summary>Lossless AVIF: colour (any layout) or monochrome, plus an optional lossless alpha item.</summary>
    internal static byte[] EncodeAvifLossless(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        bool monochrome, ReadOnlySpan<ushort> alpha, bool hasAlpha, int width, int height, int bitDepth, Av1PixelLayout layout,
        Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? extras)
    {
        (byte[] cSeq, byte[] cFrame) = BuildLosslessObus(luma, u, v, monochrome, width, height, bitDepth, layout, color);
        if (!hasAlpha)
            return Av1AvifWriter.BuildAvif(cSeq, cFrame, width, height, monochrome, bitDepth, layout, color, extras);
        byte[] aSeq, aFrame;
        using (new Av1ObuWriter.SuppressFilmGrain(true))   // grain is signalled on the colour item only
            (aSeq, aFrame) = BuildLosslessObus(alpha, default, default, true, width, height, bitDepth, Av1PixelLayout.I400, null);
        return Av1AvifWriter.BuildAvifWithAlpha(cSeq, cFrame, aSeq, aFrame, width, height, monochrome, bitDepth, layout, color, extras);
    }

    /// <summary>Encodes an I420 colour image plus an 8-bit alpha plane into a 2-item AVIF: a primary colour
    /// `av01` item and a monochrome alpha auxiliary item, linked by an `auxl` item reference. Alpha is coded as a
    /// full-range monochrome AV1 image (the standard AVIF alpha representation).</summary>
    internal static byte[] EncodeAvifColorWithAlpha(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha, int width, int height, int baseQIdx, int alphaQIdx, Av1ObuWriter.Av1ColorDesc? color = null,
        AvifContainerExtras? extras = null)
        => EncodeAvifColorWithAlpha(Widen(luma), Widen(u), Widen(v), Widen(alpha), width, height, baseQIdx, alphaQIdx, 8, color: color, extras: extras);

    /// <summary>High-bit-depth colour + alpha (alpha coded at the same depth, as libavif does).</summary>
    internal static byte[] EncodeAvifColorWithAlpha(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        ReadOnlySpan<ushort> alpha, int width, int height, int baseQIdx, int alphaQIdx, int bitDepth,
        Av1PixelLayout layout = Av1PixelLayout.I420, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] cSeq, byte[] cFrame) = BuildColorObus(luma, u, v, width, height, baseQIdx, bitDepth, layout, color);
        // Alpha: no colour description, always full range (AVIF forbids limited-range alpha).
        byte[] aSeq, aFrame;
        using (new Av1ObuWriter.SuppressFilmGrain(true))   // grain is signalled on the colour item only
            (aSeq, aFrame) = BuildMonochromeObus(alpha, width, height, alphaQIdx, bitDepth);
        return Av1AvifWriter.BuildAvifWithAlpha(cSeq, cFrame, aSeq, aFrame, width, height, colorMonochrome: false, bitDepth, layout, color, extras);
    }

    /// <summary>One layer of a layered (progressive) still image: planes at the layer's size and its quantizers.</summary>
    internal readonly record struct LayerInput(ushort[] Y, ushort[]? U, ushort[]? V, ushort[]? Alpha, int Width, int Height,
        int QIdx, int AlphaQIdx);

    /// <summary>
    /// Layered AVIF (avifenc --progressive / --layered): each layer is coded as its own frame (a key frame for the base,
    /// intra-only frames above it) in one temporal unit with spatial-id extensions; the item's payload concatenates them
    /// and 'a1lx' records the per-layer sizes. Alpha, when present, is layered the same way.
    /// </summary>
    internal static byte[] EncodeAvifLayered(IReadOnlyList<LayerInput> layers, bool monochrome, int bitDepth,
        Av1PixelLayout layout, Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? extras)
    {
        var ls = new Av1ObuWriter.LayeredStream
        {
            Layers = layers.Count, MaxWidth = layers[^1].Width, MaxHeight = layers[^1].Height,
            Widths = layers.Select(l => l.Width).ToArray(), Heights = layers.Select(l => l.Height).ToArray(),
        };
        (byte[] Data, long[] Sizes) Build(bool alpha)
        {
            byte[]? seq = null;
            var frames = new List<byte[]>();
            using (Av1ObuWriter.UseLayers(ls))
            using (new Av1ObuWriter.SuppressFilmGrain(alpha))
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    ls.Current = i;
                    var l = layers[i];
                    var (s, f) = alpha ? BuildMonochromeObus(l.Alpha!, l.Width, l.Height, l.AlphaQIdx, bitDepth)
                        : monochrome ? BuildMonochromeObus(l.Y, l.Width, l.Height, l.QIdx, bitDepth, color)
                        : BuildColorObus(l.Y, l.U!, l.V!, l.Width, l.Height, l.QIdx, bitDepth, layout, color);
                    seq ??= s;
                    frames.Add(f);
                }
            }
            var sizes = new long[layers.Count];
            sizes[0] = seq!.Length + frames[0].Length;
            for (int i = 1; i < frames.Count; i++) sizes[i] = frames[i].Length;
            var data = new byte[sizes.Sum()];
            int o = 0;
            foreach (var part in new[] { seq }.Concat(frames)) { part.CopyTo(data, o); o += part.Length; }
            return (data, sizes);
        }

        extras ??= new AvifContainerExtras();
        var (cData, cSizes) = Build(false);
        extras.ColorLayerSizes = cSizes;
        if (layers[0].Alpha == null)
            return Av1AvifWriter.BuildAvif(cData, [], ls.MaxWidth, ls.MaxHeight, monochrome, bitDepth, layout, color, extras);
        var (aData, aSizes) = Build(true);
        extras.AlphaLayerSizes = aSizes;
        return Av1AvifWriter.BuildAvifWithAlpha(cData, [], aData, [], ls.MaxWidth, ls.MaxHeight, monochrome, bitDepth, layout, color, extras);
    }

    // Per-superblock recursive-partition state for I420 colour. Extends the grayscale scheme with two chroma
    // planes (half resolution): chroma follows the luma partition tree, each leaf coding U/V at half the luma
    // block size (down to 4x4 chroma for an 8x8 luma leaf).
    private sealed class ColorPartCtx
    {
        public Av1MsacWriter Msac = null!;
        public Av1CdfContext Cdf = null!;
        public ushort[] Luma = null!, U = null!, V = null!;
        public ushort[] ReconY = null!, ReconU = null!, ReconV = null!;
        public int W, Cw, Chh;         // luma stride, chroma stride, chroma height
        // Chroma layout: I420 (SsX=SsY=1, the tuned path), I422 (1,0) or I444 (0,0). Non-4:2:0 leaves go through
        // the layout-generic rect leaf (EncodeRectLeafColor), which sizes chroma as (w>>SsX) x (h>>SsY).
        public Av1PixelLayout Layout = Av1PixelLayout.I420;
        public int SsX = 1, SsY = 1;
        public int Bw4, Bh4;           // REAL luma frame dims in 4-units
        public int DcDq, AcDq;
        public byte[] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!;
        public byte[] LeftPart = null!, LLY = null!, LCU = null!, LCV = null!, LModeY = null!, LSkip = null!;
        // Neighbour UV-mode context (chroma 4-unit indexed, like ACU): the intra-edge smooth-neighbour filter for a
        // directional chroma prediction reads the adjacent blocks' UV modes (dav1d SmUvFlag). Stores the uv_mode
        // symbol (DC=0 .. Paeth=12, CfL=13); only Smooth/SmoothV/SmoothH trip the filter bit, so CfL/DC read as 0.
        public byte[] AModeUv = null!, LModeUv = null!;
        public sbyte[] ATxY = null!, LTxY = null!;   // neighbour luma tx log-size, for the tx-depth context (UseColorTxDepth)
        // Palette neighbour state (SB128-relative, mirrors the decoder's Above/Left.PalSz + PalPrevY): per 4-unit
        // position the covering block's luma palette size and (when >0) its colours, used for the has_palette
        // context and the colour-prediction cache. APal* are per-SB128-column (like AModeY); LPal* reset per SB row.
        public byte[] APalSz = null!, LPalSz = null!;         // [32] palette size at each position (0 = none)
        public ushort[] APalCol = null!, LPalCol = null!;     // [32*8] palette colours at each position
        public ushort[] Pred = new ushort[64 * 64];
        public ushort[] EstScratch = new ushort[64 * 64];
    }

    private static byte[] EncodeMultiSbColorTile(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> uPlane, ReadOnlySpan<ushort> vPlane,
        int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx, Av1PixelLayout layout = Av1PixelLayout.I420)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 ? 1 : 0;
        int cw = w >> ssX, chh = h >> ssY;

        int sb128Cols = (sbCols + 1) >> 1;
        var abovePart = new byte[sb128Cols][];
        var aLY = FilledArray(sb128Cols); var aCU = FilledArray(sb128Cols); var aCV = FilledArray(sb128Cols);
        var aModeY = new byte[sb128Cols][]; var aSkip = new byte[sb128Cols][]; var aTxY = new sbyte[sb128Cols][];
        var aModeUv = new byte[sb128Cols][];
        var aPalSz = new byte[sb128Cols][]; var aPalCol = new ushort[sb128Cols][];
        for (int i = 0; i < sb128Cols; i++) { abovePart[i] = new byte[16]; aModeY[i] = new byte[32]; aSkip[i] = new byte[32]; aTxY[i] = FilledSbyte(32, -1); aModeUv[i] = new byte[32]; aPalSz[i] = new byte[32]; aPalCol[i] = new ushort[32 * 8]; }

        ushort[] lumaArr = new ushort[w * h]; luma.CopyTo(lumaArr);
        ushort[] uArr = new ushort[cw * chh]; uPlane.CopyTo(uArr);
        ushort[] vArr = new ushort[cw * chh]; vPlane.CopyTo(vArr);

        var c = new ColorPartCtx
        {
            Msac = new Av1MsacWriter(), Cdf = cdf, Luma = lumaArr, U = uArr, V = vArr,
            ReconY = new ushort[w * h], ReconU = new ushort[cw * chh], ReconV = new ushort[cw * chh],
            W = w, Cw = cw, Chh = chh, Bw4 = bw4, Bh4 = bh4, Layout = layout, SsX = ssX, SsY = ssY,
            DcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 0], AcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1],
        };

        // One entropy-coded tile per TileLayout tile: fresh default CDFs, writer and above contexts, and intra edges
        // confined to the tile (SetTileWindow). Tiles never predict from each other, so their order is free.
        var (_, _, colStart, rowStart) = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var tiles = new List<byte[]>();
        try
        {
            for (int tr = 0; tr + 1 < rowStart.Length; tr++)
                for (int tc = 0; tc + 1 < colStart.Length; tc++)
                {
                    if (tiles.Count > 0)
                    {
                        c.Cdf = new Av1CdfContext();
                        Av1CdfDefaults.InitializeDefault(c.Cdf, qcat);
                        c.Msac = new Av1MsacWriter();
                        for (int i = 0; i < sb128Cols; i++)
                        {
                            abovePart[i] = new byte[16]; aLY[i] = Filled(32); aCU[i] = Filled(32); aCV[i] = Filled(32);
                            aModeY[i] = new byte[32]; aSkip[i] = new byte[32]; aTxY[i] = FilledSbyte(32, -1); aModeUv[i] = new byte[32];
                            aPalSz[i] = new byte[32]; aPalCol[i] = new ushort[32 * 8];
                        }
                    }
                    SetTileWindow(colStart[tc] * 16, rowStart[tr] * 16, Math.Min(colStart[tc + 1] * 16, bw4),
                        Math.Min(rowStart[tr + 1] * 16, bh4), w, ssX, ssY);
                    for (int sby = rowStart[tr]; sby < rowStart[tr + 1]; sby++)
                    {
                        c.LeftPart = new byte[16]; c.LLY = Filled(32); c.LCU = Filled(32); c.LCV = Filled(32);
                        c.LModeY = new byte[32]; c.LSkip = new byte[32]; c.LTxY = FilledSbyte(32, -1);
                        c.LModeUv = new byte[32];
                        c.LPalSz = new byte[32]; c.LPalCol = new ushort[32 * 8];
                        for (int sbx = colStart[tc]; sbx < colStart[tc + 1]; sbx++)
                        {
                            int col = sbx >> 1;
                            c.AbovePart = abovePart[col]; c.ALY = aLY[col]; c.ACU = aCU[col]; c.ACV = aCV[col];
                            c.AModeY = aModeY[col]; c.ASkip = aSkip[col]; c.ATxY = aTxY[col];
                            c.AModeUv = aModeUv[col];
                            c.APalSz = aPalSz[col]; c.APalCol = aPalCol[col];
                            EncodePartitionColor(c, 1, sbx * 16, sby * 16);
                        }
                    }
                    tiles.Add(c.Msac.Finish());
                }
        }
        finally
        {
            ClearTileWindow();
        }

        return AssembleTileGroup(tiles);
    }

    // tile_group_obu payload after the frame header: a single tile is its bare data; with several tiles, one byte
    // for tile_start_and_end_present_flag = 0 (+ byte alignment), then every tile but the last prefixed by
    // tile_size_minus_1 as a 4-byte little-endian value (tile_size_bytes_minus_1 = 3 in tile_info).
    internal static byte[] AssembleTileGroup(List<byte[]> tiles)
    {
        if (tiles.Count == 1) return tiles[0];
        var o = new System.IO.MemoryStream();
        o.WriteByte(0);
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i < tiles.Count - 1)
            {
                uint sz = (uint)(tiles[i].Length - 1);
                o.WriteByte((byte)sz); o.WriteByte((byte)(sz >> 8)); o.WriteByte((byte)(sz >> 16)); o.WriteByte((byte)(sz >> 24));
            }
            o.Write(tiles[i]);
        }
        return o.ToArray();
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
        if (UseTrueRd && (long)c.Bw4 * c.Bh4 * 16 <= TrueRdPixelBudget && bl >= 1 && (bl < 4 || (bl == 4 && UseSub8Partition && fullyInside)))
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
            if ((bl == 2 || bl == 3) && UseRectPartition && !UsePalette && c.Layout == Av1PixelLayout.I420)
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

        if (choice == 1) // PARTITION_HORZ: two stacked leaves
        {
            if (bl == 4) { EncodeSub8Pair(c, horz: true, bx4, by4, partCdf, nPart, bx8, by8, node); return; }
            var rp = RectLeafParams(bl);
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Horizontal, nPart);
            EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4, blk4, hsz, node.H0);
            EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4 + hsz, blk4, hsz, node.H1);
            FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Horizontal);
            return;
        }

        if (choice == 2) // PARTITION_VERT: two side-by-side leaves
        {
            if (bl == 4) { EncodeSub8Pair(c, horz: false, bx4, by4, partCdf, nPart, bx8, by8, node); return; }
            var rp = RectLeafParams(bl);
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Vertical, nPart);
            EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4, by4, hsz, blk4, node.V0);
            EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4 + hsz, by4, hsz, blk4, node.V1);
            FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Vertical);
            return;
        }

        // Extended partitions (T-shapes): two quarter-square leaves (bl+1) + one half-rect leaf, in the EXACT
        // sub-block order + edge availability our decoder uses (Av1Decode TopSplit/BottomSplit/LeftSplit/RightSplit).
        if (choice >= 4)
        {
            var rp = RectLeafParams(bl);
            if (choice == 4) // PARTITION_HORZ_A: two top quarters, then bottom half
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.TopSplit, nPart);
                EncodeLeafBlockColor(c, bl + 1, bx4, by4, hsz, Av1EdgeFlags.AllTrAndBl);
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4, hsz, node.V1);
                EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4 + hsz, blk4, hsz, node.H1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.TopSplit);
            }
            else if (choice == 5) // PARTITION_HORZ_B: top half, then two bottom quarters
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.BottomSplit, nPart);
                EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4, blk4, hsz, node.H0);
                EncodeLeafBlockColor(c, bl + 1, bx4, by4 + hsz, hsz, node.V0);
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4 + hsz, hsz, Av1EdgeFlags.None);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.BottomSplit);
            }
            else if (choice == 6) // PARTITION_VERT_A: two left quarters, then right half
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.LeftSplit, nPart);
                EncodeLeafBlockColor(c, bl + 1, bx4, by4, hsz, Av1EdgeFlags.AllTrAndBl);
                EncodeLeafBlockColor(c, bl + 1, bx4, by4 + hsz, hsz, node.H1);
                EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4 + hsz, by4, hsz, blk4, node.V1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.LeftSplit);
            }
            else if (choice == 7) // PARTITION_VERT_B: left half, then two right quarters
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.RightSplit, nPart);
                EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4, by4, hsz, blk4, node.V0);
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4, hsz, node.H0);
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4 + hsz, hsz, Av1EdgeFlags.None);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.RightSplit);
            }
            else if (choice == 8) // PARTITION_HORZ_4: four 32x8 strips (bl==2 only; edge flags per decoder)
            {
                int q = hsz >> 1;   // quarter-height step in 4-units
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Horizontal4, nPart);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs32x8, (int)Av1RectTxSize.Rtx32x8, (int)Av1RectTxSize.Rtx16x4, bx4, by4, blk4, q, node.H0);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs32x8, (int)Av1RectTxSize.Rtx32x8, (int)Av1RectTxSize.Rtx16x4, bx4, by4 + q, blk4, q, node.H4);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs32x8, (int)Av1RectTxSize.Rtx32x8, (int)Av1RectTxSize.Rtx16x4, bx4, by4 + 2 * q, blk4, q, Av1EdgeFlags.AllLeftHasBottom);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs32x8, (int)Av1RectTxSize.Rtx32x8, (int)Av1RectTxSize.Rtx16x4, bx4, by4 + 3 * q, blk4, q, node.H1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Horizontal4);
            }
            else // choice == 9, PARTITION_VERT_4: four 8x32 strips
            {
                int q = hsz >> 1;
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Vertical4, nPart);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x32, (int)Av1RectTxSize.Rtx8x32, (int)Av1RectTxSize.Rtx4x16, bx4, by4, q, blk4, node.V0);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x32, (int)Av1RectTxSize.Rtx8x32, (int)Av1RectTxSize.Rtx4x16, bx4 + q, by4, q, blk4, node.V4);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x32, (int)Av1RectTxSize.Rtx8x32, (int)Av1RectTxSize.Rtx4x16, bx4 + 2 * q, by4, q, blk4, Av1EdgeFlags.AllTopHasRight);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x32, (int)Av1RectTxSize.Rtx8x32, (int)Av1RectTxSize.Rtx4x16, bx4 + 3 * q, by4, q, blk4, node.V1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Vertical4);
            }
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
        public ushort[] ReconY = null!, ReconU = null!, ReconV = null!;   // block regions
        public byte[] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!;
        public byte[] LeftPart = null!, LLY = null!, LCU = null!, LCV = null!, LModeY = null!, LSkip = null!;
        public sbyte[] ATxY = null!, LTxY = null!;
        public byte[] APalSz = null!, LPalSz = null!; public ushort[] APalCol = null!, LPalCol = null!;
    }

    private static ushort[] CopyRegion(ushort[] plane, int stride, int px, int py, int w, int h)
    {
        var r = new ushort[w * h];
        for (int y = 0; y < h; y++) Array.Copy(plane, (py + y) * stride + px, r, y * w, w);
        return r;
    }

    private static void PasteRegion(ushort[] region, ushort[] plane, int stride, int px, int py, int w, int h)
    {
        for (int y = 0; y < h; y++) Array.Copy(region, y * w, plane, (py + y) * stride + px, w);
    }

    private static RdSnapshot SnapshotRd(ColorPartCtx c, int bx4, int by4, int blk4)
    {
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        int cpx = (bx4 * 4) >> c.SsX, cpy = (by4 * 4) >> c.SsY, cnw = (blk4 * 4) >> c.SsX, cnh = (blk4 * 4) >> c.SsY;
        var s = new RdSnapshot { Msac = c.Msac.Save() };
        s.Cdf.CopyFrom(c.Cdf);
        s.ReconY = CopyRegion(c.ReconY, c.W, lpx, lpy, ln, ln);
        s.ReconU = CopyRegion(c.ReconU, c.Cw, cpx, cpy, cnw, cnh);
        s.ReconV = CopyRegion(c.ReconV, c.Cw, cpx, cpy, cnw, cnh);
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
        int cpx = (bx4 * 4) >> c.SsX, cpy = (by4 * 4) >> c.SsY, cnw = (blk4 * 4) >> c.SsX, cnh = (blk4 * 4) >> c.SsY;
        c.Msac.Restore(s.Msac);
        c.Cdf.CopyFrom(s.Cdf);
        PasteRegion(s.ReconY, c.ReconY, c.W, lpx, lpy, ln, ln);
        PasteRegion(s.ReconU, c.ReconU, c.Cw, cpx, cpy, cnw, cnh);
        PasteRegion(s.ReconV, c.ReconV, c.Cw, cpx, cpy, cnw, cnh);
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
        int cpx = (bx4 * 4) >> c.SsX, cpy = (by4 * 4) >> c.SsY, cnw = (blk4 * 4) >> c.SsX, cnh = (blk4 * 4) >> c.SsY;
        for (int y = 0; y < cnh; y++)
            for (int x = 0; x < cnw; x++)
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
        Span<int> cands = stackalloc int[10];
        int nc = 0;
        // Outside 4:2:0 a 64x64 NONE leaf would need several chroma tx blocks (not yet coded) — always split there.
        bool i420 = c.Layout == Av1PixelLayout.I420;
        cands[nc++] = 0;
        if (bl < 4) cands[nc++] = 3;   // SPLIT — not at 8x8 (that would be 4x4, unsupported); 8x8 offers NONE/HORZ/VERT
        // Sub-8x8 HORZ/VERT use 4:2:0 shared chroma (EncodeSub8Pair), so they stay 4:2:0-only for now.
        bool rectHere = fullyInside && !UsePalette && (((bl == 2 || bl == 3) && UseRectPartition) || (bl == 4 && UseSub8Partition && i420));
        // 4:2:2 forbids every tall (h = 2w) leaf: its chroma would be 1:4 (get_plane_residual_size == BLOCK_INVALID),
        // so VERT, VERT_A/B and VERT_4 are never emitted there (spec conformance requirement; dav1d table has 0).
        bool vertOk = c.Layout != Av1PixelLayout.I422;
        if (rectHere) { cands[nc++] = 1; if (vertOk) cands[nc++] = 2; }
        // Extended T-shape partitions (HORZ_A/B, VERT_A/B) at 32x32/16x16 — quarter squares + half rects, all
        // block sizes we already code. Same in-frame + rect gate; 8x8 has no extended types.
        if (UseExtPartition && (bl == 2 || bl == 3) && rectHere)
        { cands[nc++] = 4; cands[nc++] = 5; if (vertOk) { cands[nc++] = 6; cands[nc++] = 7; } }
        // HORZ_4/VERT_4 at 32x32 → 32x8/8x32 strips (normal 16x4 chroma). Only bl==2: the 16x16→16x4 case needs
        // sub-8x8-style shared chroma. The decoder still reads the symbol wherever we choose not to emit it.
        if (UseExtPartition && bl == 2 && rectHere)
        { cands[nc++] = 8; if (vertOk) cands[nc++] = 9; }

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
    private static void ComputeCflAcEnc(ushort[] reconY, int yStride, int bx, int by, int cn, short[] ac)
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

    private static ushort[] BuildCflPred(int dcPred, short[] ac, int cn, int alpha)
    {
        var pred = new ushort[cn * cn];
        for (int i = 0; i < cn * cn; i++)
        {
            int diff = ac[i] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
            pred[i] = (ushort)Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax);
        }

        return pred;
    }

    private static int BestCflAlpha(ushort[] plane, int planeW, int cbx, int cby, int cn, int dcPred, short[] ac)
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
                    int e = plane[row + x] - Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax);
                    sse += (long)e * e;
                }
            }

            if (sse < bestSse) { bestSse = sse; bestAlpha = alpha; }
        }

        return bestAlpha;
    }

    // Rect (cw x ch) versions of the CfL helpers — mirror the decoder's ComputeCflAc (I420: sum the 2x2 luma,
    // <<1, subtract DC with log2Sz = log2(cw)+log2(ch)). Used to add CfL chroma to the rect and sub-8x8 leaves,
    // which previously coded DC-only chroma (the profiled +722B chroma-coef gap vs libaom on piechart).
    private static void ComputeCflAcEncRect(ushort[] reconY, int yStride, int bx, int by, int cw, int ch, short[] ac)
    {
        int idx = 0;
        for (int y = 0; y < ch; y++)
        {
            int yOff = (by + 2 * y) * yStride + bx;
            for (int x = 0; x < cw; x++)
            { int p = yOff + 2 * x; ac[idx + x] = (short)((reconY[p] + reconY[p + 1] + reconY[p + yStride] + reconY[p + 1 + yStride]) << 1); }
            idx += cw;
        }
        int log2Sz = System.Numerics.BitOperations.TrailingZeroCount(cw) + System.Numerics.BitOperations.TrailingZeroCount(ch);
        int dc = (1 << log2Sz) >> 1;
        for (int i = 0; i < cw * ch; i++) dc += ac[i];
        dc >>= log2Sz;
        for (int i = 0; i < cw * ch; i++) ac[i] = (short)(ac[i] - dc);
    }

    private static ushort[] BuildCflPredRect(int dcPred, short[] ac, int cw, int ch, int alpha)
    {
        var pred = new ushort[cw * ch];
        for (int i = 0; i < cw * ch; i++)
        { int diff = ac[i] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6; pred[i] = (ushort)Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax); }
        return pred;
    }

    private static int BestCflAlphaRect(ushort[] plane, int planeW, int cbx, int cby, int cw, int ch, int dcPred, short[] ac)
    {
        int bestAlpha = 0; long bestSse = long.MaxValue;
        for (int alpha = -16; alpha <= 16; alpha++)
        {
            long sse = 0;
            for (int y = 0; y < ch; y++)
            {
                int row = (cby + y) * planeW + cbx;
                for (int x = 0; x < cw; x++)
                {
                    int diff = ac[y * cw + x] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
                    int e = plane[row + x] - Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax);
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

    private static void CopyPlaneBlock(ushort[] src, int cn, ushort[] recon, int reconW, int cbx, int cby)
    {
        for (int y = 0; y < cn; y++) Array.Copy(src, y * cn, recon, (cby + y) * reconW + cbx, cn);
    }

    private static ushort[] FlatPlane(int value, int cn)
    {
        var p = new ushort[cn * cn]; Array.Fill(p, (ushort)Math.Clamp(value, 0, PixMax)); return p;
    }

    // Reconstruction SSE of a chroma block coded with `coeffs` on prediction `predPlane` (cn x cn), vs the source.
    private static long ChromaReconSse(int[] coeffs, int tx, int cn, int dcDq, int acDq, ushort[] predPlane,
        ushort[] src, int srcW, int cbx, int cby)
    {
        var tmp = new ushort[cn * cn];
        DequantAndReconstructPredRect(coeffs, tx, cn, cn, dcDq, acDq, predPlane, tmp, cn, 0, 0);
        long sse = 0;
        for (int y = 0; y < cn; y++)
            for (int x = 0; x < cn; x++) { int d = tmp[y * cn + x] - src[(cby + y) * srcW + cbx + x]; sse += (long)d * d; }
        return sse;
    }

    private static void EncodeLeafBlockColor(ColorPartCtx c, int bl, int bx4, int by4, int blk4, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        if (c.Layout != Av1PixelLayout.I420)
        {
            // 4:4:4 / 4:2:2: the rect leaf is generic in its chroma dimensions, so square leaves use it too (w4 == h4).
            // (It codes luma at the max transform with no tx-depth / filter-intra selection — a later efficiency pass.)
            int sqBs = BlToBs(bl);
            EncodeRectLeafColor(c, sqBs, BlToTx(bl), -1, bx4, by4, blk4, blk4, edgeFlags);
            return;
        }

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

        // Filter-intra: signalled for DC-eligible luma blocks <= 32x32 (max block dim <= 3 in log2-of-4units). The
        // decoder reads use_filter_intra for EVERY such DC block, so the flag is emitted for all of them (0 when
        // filter is not chosen). This is the emission gate; selection uses the same set here.
        bool filterEligible = UseFilterIntra && Math.Max(Av1Tables.BlockDimensions[bs, 2], Av1Tables.BlockDimensions[bs, 3]) <= 3;

        // Luma: rate-distortion mode + tx-type decision (from reconstruction). Writes prediction into c.Pred.
        var rd = ChooseLeafRdCore(c.ReconY, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AModeY[bxR], c.LModeY[byR], c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR), c.Pred, edgeFlags, filterEligible, bs, fullSet: UseFullIntraTxSet);
        Av1IntraPredMode yMode = rd.Mode; int yDelta = rd.Delta;
        int[] yC = rd.Coeffs; Av1TxType yInv = rd.Inv; int yTxIdx = rd.Idx;
        // A filter winner is coded as y_mode=DC; the Filter predictor uses (yMode==Filter, yDelta==filter mode).
        // THREE mode values (all verified vs dav1d): coded y_mode SYMBOL + uv context + NEIGHBOUR mode ctx = DC
        // (yModeSym); tx-type coefficient context = FilterModeToYMode (yModeNoFilt).
        bool isFilter = yMode == Av1IntraPredMode.Filter;
        int yModeSym = isFilter ? (int)Av1IntraPredMode.Dc : (int)yMode;
        int yModeNoFilt = isFilter ? Av1Tables.FilterModeToYMode[yDelta] : (int)yMode;

        // Transform-size (tx_depth) TRUE-RD search for luma. Only when residual is coded (depth-0 not all-zero) and
        // the block is fully inside the frame. Each depth is scored by its ACTUAL reconstruction (ReconstructLumaAtDepth
        // — depth 0 = the RDOQ'd single transform, depth>0 = the raster recon cascade) under SnapshotRd/RestoreRd:
        // J = real recon SSE + λ·(real coef bits + tx_size symbol bits). The winner is then reconstructed for keeps.
        // (Replaces the old source-predicted estimate, which mismatched the recon-predicted coding path.)
        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        int maxDepth = (UseColorTxDepth && HasNonZero(yC) && fullyInside) ? Math.Min((int)maxTDim.Max, 2) : 0;
        int depth = 0;
        if (maxDepth > 0)
        {
            double lambda = RdLambdaK * c.AcDq * c.AcDq;
            int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
            var txSzCdf = c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx);
            int txNsym = Math.Min((int)maxTDim.Max, 2);
            var snap = SnapshotRd(c, bx4, by4, blk4);
            long txBestJ = long.MaxValue;
            for (int d = 0; d <= maxDepth; d++)
            {
                var (_, _, bitsT) = ReconstructLumaAtDepth(c, bx4, by4, blk4, n, tx, ReduceTx(tx, d), d,
                    yMode, yDelta, yModeNoFilt, yC, yInv, yTxIdx, edgeFlags, bs);
                double txSizeBits = Av1CoeffEncode.SymBits(txSzCdf, Math.Min(d, txNsym));
                long j = LumaBlockSse(c, bx, by, n) + (long)(lambda * (bitsT + txSizeBits));
                if (j < txBestJ) { txBestJ = j; depth = d; }
                RestoreRd(c, snap, bx4, by4, blk4);
            }
        }
        int lumaTx = ReduceTx(tx, depth);
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];

        // Reconstruct luma at the chosen depth into ReconY for keeps (CfL needs the reconstructed luma AC; depth>0
        // records the per-tx-block coeffs for emission after the tx_size symbol).
        var (cfY, lumaTxb, _) = ReconstructLumaAtDepth(c, bx4, by4, blk4, n, tx, lumaTx, depth,
            yMode, yDelta, yModeNoFilt, yC, yInv, yTxIdx, edgeFlags, bs);
        bool lumaAllZero = depth == 0 ? !HasNonZero(yC) : lumaTxb!.TrueForAll(t => !HasNonZero(t.Cf));

        // --- Palette (screen content): code this block as a luma palette when it wins RD. Eligible DC-sized
        // blocks fully inside the frame with 2..8 distinct source colours are palette-representable LOSSLESSLY
        // (skip, no residual), which beats lossy transform coding for flat/graphics content. Compare luma J:
        // palette (SSE 0 + λ·palBits) vs the transform mode just chosen (its SSE + λ·coeff/mode bits).
        if (UsePalette && blk4 <= 16 && fullyInside)
        {
            Span<byte> present = stackalloc byte[1 << Bd];
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
                for (int v = 0; v < (1 << Bd); v++) if (present[v] != 0) palColors[pi++] = (ushort)v;
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
        double clam0 = RdLambdaK * c.AcDq * c.AcDq;
        var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, yModeSym);
        var dcuP = FlatPlane(dcU, cn); var dcvP = FlatPlane(dcV, cn);

        // Winner state across DC / directional-UV / CfL. predU/predV are the reconstruction prediction planes.
        int uvMode = 0, uvDelta = 0; bool useCfl = false;
        ushort[] predU = dcuP, predV = dcvP; int alphaU = 0, alphaV = 0;
        double bestJ = ChromaReconSse(uC, ctx0, cn, c.DcDq, c.AcDq, dcuP, c.U, c.Cw, cbx, cby)
                     + ChromaReconSse(vC, ctx0, cn, c.DcDq, c.AcDq, dcvP, c.V, c.Cw, cbx, cby)
                     + clam0 * (CoeffCost(uC) + CoeffCost(vC) + Av1CoeffEncode.SymBits(uvModeCdf, 0));

        // Directional / Smooth / Paeth UV modes. AV1 lets chroma pick any of the 13 intra modes; we only coded
        // DC/CfL before, so sharp colour boundaries (e.g. piechart slices) paid full chroma residual. Byte-exact:
        // the decoder reconstructs every UV mode, and we mirror its chroma edge prep exactly — single chroma tx
        // block (square leaf), so uvSbHasTr/Bl reduce to the block's I420 top-right/bottom-left availability, and
        // the smooth-neighbour edge filter reads the stored UV-mode context.
        if (UseUvModeSearch)
        {
            var chromaEdge =
                ((edgeFlags & Av1EdgeFlags.I420TopHasRight) != 0 ? Av1EdgeFlags.I444TopHasRight : 0) |
                ((edgeFlags & Av1EdgeFlags.I420LeftHasBottom) != 0 ? Av1EdgeFlags.I444LeftHasBottom : 0);
            int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
            int cbw4 = c.Bw4 >> 1, cbh4 = c.Bh4 >> 1, cbx4 = bx4 >> 1, cby4 = by4 >> 1;
            int bDimW = Av1Tables.BlockDimensions[bs, 2], bDimH = Av1Tables.BlockDimensions[bs, 3];
            bool uvAngleOk = bDimW + bDimH >= 2;
            var pu = new ushort[cn * cn]; var pv = new ushort[cn * cn];

            // SAD prescreen on U (SATD's 8x8 tiling overflows the 4x4 chroma case), then full RD on the best few.
            Span<int> topIdx = stackalloc int[RdUvCandidates];
            Span<long> topCost = stackalloc long[RdUvCandidates];
            topCost.Fill(long.MaxValue);
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (mode == Av1IntraPredMode.Dc) continue;      // DC is the baseline above
                if (delta != 0 && !uvAngleOk) continue;         // no uv angle_delta at tiny sizes
                PredictIntra(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cn, mode, delta, pu, chromaEdge, cIntraFlags);
                long sad = SadBlock(c.U, c.Cw, cbx, cby, pu, cn);
                for (int k = 0; k < RdUvCandidates; k++)
                    if (sad < topCost[k]) { for (int j = RdUvCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = sad; topIdx[k] = ci; break; }
            }
            for (int t = 0; t < RdUvCandidates; t++)
            {
                if (topCost[t] == long.MaxValue) break;
                (Av1IntraPredMode mode, int delta) = CandidateModes[topIdx[t]];
                PredictIntra(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cn, mode, delta, pu, chromaEdge, cIntraFlags);
                PredictIntra(c.ReconV, c.Cw, cbw4, cbh4, cbx4, cby4, cn, mode, delta, pv, chromaEdge, cIntraFlags);
                // Chroma tx-type is DERIVED from the UV mode (TxTypeFromUvMode) — no symbol coded. All map to
                // TX_CLASS_2D so the scan/coeff-coding is unchanged, but the transform KERNEL differs, so the
                // forward transform and the reconstruction SSE MUST use it (coding DctDct here desyncs vs libdav1d).
                var uvTx = (Av1TxType)Av1Tables.TxTypeFromUvMode[(int)mode];
                var uvFwd = FwdTypeForTxType(uvTx);
                var qfu2 = new double[scanLenC]; var qfv2 = new double[scanLenC];
                int[] uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pu, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfu2, uvFwd);
                int[] vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pv, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfv2, uvFwd);
                double modeBits = Av1CoeffEncode.SymBits(uvModeCdf, (int)mode)
                    + (IsDirectional(mode) && uvAngleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
                double j = ReconSseCandRect(uu, ctx0, cn, cn, c.DcDq, c.AcDq, pu, c.U, c.Cw, cbx, cby, uvTx)
                         + ReconSseCandRect(vv, ctx0, cn, cn, c.DcDq, c.AcDq, pv, c.V, c.Cw, cbx, cby, uvTx)
                         + clam0 * (CoeffCost(uu) + CoeffCost(vv) + modeBits);
                if (j < bestJ)
                {
                    bestJ = j; uvMode = (int)mode; uvDelta = delta; useCfl = false;
                    uC = uu; vC = vv; qfU = qfu2; qfV = qfv2;
                    predU = (ushort[])pu.Clone(); predV = (ushort[])pv.Clone();
                }
            }
        }

        // Chroma-from-luma: predict chroma AC from reconstructed luma AC scaled by a signed per-plane alpha; keep
        // CfL when it codes cheaper (incl. the alpha signalling). CfL-allowed sizes only.
        if (cflAllowed && UseCfl)
        {
            var ac = new short[cn * cn];
            ComputeCflAcEnc(c.ReconY, c.W, bx, by, cn, ac);
            int aU = BestCflAlpha(c.U, c.Cw, cbx, cby, cn, dcU, ac);
            int aV = BestCflAlpha(c.V, c.Cw, cbx, cby, cn, dcV, ac);
            if (aU != 0 || aV != 0)
            {
                var pcflU = BuildCflPred(dcU, ac, cn, aU);
                var pcflV = BuildCflPred(dcV, ac, cn, aV);
                var qfUcfl = new double[scanLenC]; var qfVcfl = new double[scanLenC];
                int[] uCcfl = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pcflU, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfUcfl);
                int[] vCcfl = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pcflV, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfVcfl);
                double cflJ = ChromaReconSse(uCcfl, ctx0, cn, c.DcDq, c.AcDq, pcflU, c.U, c.Cw, cbx, cby)
                            + ChromaReconSse(vCcfl, ctx0, cn, c.DcDq, c.AcDq, pcflV, c.V, c.Cw, cbx, cby)
                            + clam0 * (CoeffCost(uCcfl) + CoeffCost(vCcfl) + Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma) + (aU != 0 ? 5 : 0) + (aV != 0 ? 5 : 0));
                if (cflJ < bestJ)
                {
                    bestJ = cflJ; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                    alphaU = aU; alphaV = aV;
                    uC = uCcfl; vC = vCcfl; qfU = qfUcfl; qfV = qfVcfl; predU = pcflU; predV = pcflV;
                }
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
        // Filter blocks code the Y-mode SYMBOL as DC (real mode Filter is signalled by use_filter_intra below); the
        // uv-mode context also sees DC. DC/Filter carry no angle_delta.
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(yAboveCtx, yLeftCtx), yModeSym, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        int uvSym = useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : uvMode;
        c.Msac.EncodeSymbolAdapt(uvModeCdf, uvSym, uvNsym);
        if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);
        else if (IsDirectional((Av1IntraPredMode)uvMode) &&
                 Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] >= 2)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf(uvMode - (int)Av1IntraPredMode.Vertical), uvDelta + 3, 6);

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
            if (!useCfl && uvMode == 0) c.Msac.EncodeBoolAdapt(c.Cdf.GetPalUvCdf(0), 0);   // has_palette_uv only when UvMode==DC
        }

        // filter_intra: for DC-coded eligible blocks (no palette — always true on this non-palette leaf), emit
        // use_filter_intra + the filter mode, in the exact decode_b position (after palette flags, before tx_size).
        if (filterEligible && (yMode == Av1IntraPredMode.Dc || isFilter))
        {
            c.Msac.EncodeBoolAdapt(c.Cdf.GetFilterIntraCdf((Av1BlockSize)bs), (uint)(isFilter ? 1 : 0));
            if (isFilter) c.Msac.EncodeSymbolAdapt(c.Cdf.GetFilterIntraModeCdf(), yDelta, 4);
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
                if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                    Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, yModeNoFilt, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
                else
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, 0, yModeNoFilt, yC, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            }
            else
            {
                foreach (var t in lumaTxb!)
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, t.Cf,
                        skipCtx: t.SkipCtx, dcSignCtx: t.SignCtx, txTypeIdx: t.Idx, fullSet: UseFullIntraTxSet);
            }
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);

            {
                var uvTxR = (Av1TxType)Av1Tables.TxTypeFromUvMode[uvSym];
                cfU = DequantAndReconstructPredRect(uC, ctx0, cn, cn, c.DcDq, c.AcDq, predU, c.ReconU, c.Cw, cbx, cby, uvTxR);
                cfV = DequantAndReconstructPredRect(vC, ctx0, cn, cn, c.DcDq, c.AcDq, predV, c.ReconV, c.Cw, cbx, cby, uvTxR);
            }
        }
        else
        {
            CopyPlaneBlock(predU, cn, c.ReconU, c.Cw, cbx, cby);
            CopyPlaneBlock(predV, cn, c.ReconV, c.Cw, cbx, cby);
        }

        int yW = Math.Min(blk4, c.Bw4 - bx4), yH = Math.Min(blk4, c.Bh4 - by4);
        int cW = Math.Min(cblk4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(cblk4, (c.Bh4 - by4 + 1) >> 1);
        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        // Luma LCoef context: for depth>0 it was already filled per-tx-block during reconstruction, so only fill it
        // here (with the single-transform cfY) at depth 0. Mode/skip/tx-size context is filled over the whole block.
        // Neighbour mode context stores DC for filter blocks (yModeSym), NOT FilterModeToYMode — matches the decoder
        // (dav1d decode.c: FILTER_PRED -> DC_PRED). This is distinct from the tx-type context (yModeNoFilt) above.
        for (int i = 0; i < yW && bxR + i < 32; i++) { if (depth == 0) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yModeSym; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = 0; }
        for (int j = 0; j < yH && byR + j < 32; j++) { if (depth == 0) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yModeSym; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = 0; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = cfU; c.ACV[cxR + i] = cfV; c.AModeUv[cxR + i] = (byte)uvSym; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = cfU; c.LCV[cyR + j] = cfV; c.LModeUv[cyR + j] = (byte)uvSym; }
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
        int bitDepth = Bd;

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
                c.ReconY[(by + yy) * c.W + bx + xx] = (ushort)palColors[palIdx[yy * stride + xx]];
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
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = 0x40; c.ACV[cxR + i] = 0x40; c.AModeUv[cxR + i] = 0; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = 0x40; c.LCV[cyR + j] = 0x40; c.LModeUv[cyR + j] = 0; }
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
    // Reconstructs luma at a given tx_depth EXACTLY as the emit path does (depth 0 = the single block-size transform
    // from the RDOQ'd yC; depth>0 = the quadtree cascade, each sub-block predicted from reconstruction in raster
    // order, tx-type chosen + RDOQ'd), writing pixels into c.ReconY and, for depth>0, the per-tx neighbour coef
    // context into c.ALY/c.LLY. Returns the coef-context byte (depth 0), the per-tx records (depth>0), and the total
    // coefficient-bit estimate. Used both to TRIAL each depth (under SnapshotRd/RestoreRd) and to commit the winner,
    // so the tx_depth decision is true-RD: it compares the real reconstruction + real coded cost of each depth.
    private static (byte CfY, List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? LumaTxb, double CoefBits)
        ReconstructLumaAtDepth(ColorPartCtx c, int bx4, int by4, int blk4, int n, int tx, int lumaTx, int depth,
            Av1IntraPredMode yMode, int yDelta, int yModeNoFilt, int[] yC, Av1TxType yInv, int yTxIdx,
            Av1EdgeFlags edgeFlags, int bs)
    {
        int bxR = bx4 & 31, byR = by4 & 31, bx = bx4 * 4, by = by4 * 4;
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (depth == 0)
        {
            int ySign = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
            byte cfY = DequantAndReconstructPred(yC, tx, n, c.DcDq, c.AcDq, c.Pred, c.ReconY, c.W, bx, by, yInv);
            double bits = (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, tx, yModeNoFilt, yInv, yC, 0, ySign)
                : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, tx, 0, yModeNoFilt, yC, 0, ySign, yTxIdx, fullSet: UseFullIntraTxSet);
            return (cfY, null, bits);
        }
        var lumaTxb = new List<(int[], Av1TxType, int, int, int, int, int)>();
        double coefBits = 0;
        int txN = lTDim.W * 4, txW4 = lTDim.W;
        var predBuf = new ushort[txN * txN];
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
                if (HasNonZero(cf))
                {
                    var qfTx = new double[Av1Tables.Scans[lumaTx].Length];
                    Av1FwdTransform.ForwardQuantTyped(res, txN, c.DcDq, c.AcDq, qfTx.Length, FwdTypeForIdx(idx), qfTx);
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, cf, qfTx,
                        c.DcDq, c.AcDq, skc, snc, idx, RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);
                }
                coefBits += Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, cf, skc, snc, idx, fullSet: UseFullIntraTxSet);
                lumaTxb.Add((cf, inv, idx, skc, snc, cbx4 * 4, cby4 * 4));
                byte txCfCtx = DequantAndReconstructPred(cf, lumaTx, txN, c.DcDq, c.AcDq, predBuf, c.ReconY, c.W, cbx4 * 4, cby4 * 4, inv);
                int tcw = Math.Min(txW4, c.Bw4 - cbx4), tch = Math.Min(txW4, c.Bh4 - cby4);
                for (int i = 0; i < tcw && cbxR + i < 32; i++) c.ALY[cbxR + i] = txCfCtx;
                for (int j = 0; j < tch && cbyR + j < 32; j++) c.LLY[cbyR + j] = txCfCtx;
            }
        return (0x40, lumaTxb, coefBits);
    }

    // Luma block SSE (reconstruction vs source) over an n x n region at pixel (bx,by).
    private static long LumaBlockSse(ColorPartCtx c, int bx, int by, int n)
    {
        long sse = 0;
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++) { int d = c.ReconY[(by + yy) * c.W + bx + xx] - c.Luma[(by + yy) * c.W + bx + xx]; sse += (long)d * d; }
        return sse;
    }

    // Estimates the luma coding cost J = SSE + λ·bits of a single rectangular leaf, predicting from the SOURCE
    // plane and reconstructing through the decoder's inverse — the same methodology as EstimateBlockCost, so the
    // PARTITION_HORZ / PARTITION_VERT costs compare fairly against NONE / SPLIT.
    private static long EstimateRectCostColor(ColorPartCtx c, int lumaTx, int bx4, int by4, int w4, int h4)
    {
        int w = w4 * 4, h = h4 * 4;
        int lScan = Av1Tables.Scans[lumaTx].Length;
        var pred = new ushort[h * w];
        var bestPred = new ushort[h * w];
        int[] bestCf = null!;
        long bestBits = long.MaxValue;
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntraRect(c.Luma, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred);
            int[] cf = ForwardResidualPredRect(c.Luma, c.W, bx4 * 4, by4 * 4, pred, w, h, lumaTx, c.DcDq, c.AcDq, lScan);
            long bits = CoeffCost(cf);
            if (bits < bestBits) { bestBits = bits; bestCf = cf; Array.Copy(pred, bestPred, h * w); }
        }

        var reconTmp = new ushort[h * w];
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
    // Per-thread tile window (luma 4-units) for multi-tile encodes: intra edges never cross a tile boundary and
    // top-right / bottom-left availability ends at the tile's right / bottom edge (dav1d prepare_intra_edges uses
    // ts->tiling.col_start/col_end/row_start/row_end). Inactive => the whole frame is one tile.
    [ThreadStatic] private static bool t_tileOn;
    [ThreadStatic] private static int t_tileX4, t_tileY4, t_tileEndX4, t_tileEndY4, t_tileLumaW, t_tileSsX, t_tileSsY;

    // The active tile's bounds in the 4-unit grid of the plane whose stride is reconW (chroma bounds are the luma
    // bounds shifted by the subsampling, as dav1d's col_start >> ss_hor).
    private static (int X0, int Y0, int X1, int Y1) TileBounds4(int reconW, int bw4, int bh4)
    {
        if (!t_tileOn) return (0, 0, bw4, bh4);
        bool luma = reconW == t_tileLumaW;
        int sx = luma ? 0 : t_tileSsX, sy = luma ? 0 : t_tileSsY;
        return (t_tileX4 >> sx, t_tileY4 >> sy, Math.Min(bw4, t_tileEndX4 >> sx), Math.Min(bh4, t_tileEndY4 >> sy));
    }

    private static void SetTileWindow(int x4, int y4, int endX4, int endY4, int lumaW, int ssX, int ssY)
    {
        t_tileOn = true; t_tileX4 = x4; t_tileY4 = y4; t_tileEndX4 = endX4; t_tileEndY4 = endY4;
        t_tileLumaW = lumaW; t_tileSsX = ssX; t_tileSsY = ssY;
    }

    private static void ClearTileWindow() => t_tileOn = false;

    private static int DcPredictRect(ushort[] recon, int reconW, int bx, int by, int w, int h)
    {
        var tb = TileBounds4(reconW, int.MaxValue, int.MaxValue);
        bool haveTop = by > tb.Y0 * 4, haveLeft = bx > tb.X0 * 4;
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

        return PixMid;
    }

    private static void FillFlatRect(ushort[] recon, int reconW, int bx, int by, int w, int h, int value)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                recon[(by + y) * reconW + (bx + x)] = (ushort)value;
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

    private static int[] ForwardResidualRectDc(ReadOnlySpan<ushort> plane, int planeW, int bx, int by, int w, int h,
        int dc, int txIdx, int dcDq, int acDq, int rcCount)
    {
        var residual = new int[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) residual[y * w + x] = plane[(by + y) * planeW + (bx + x)] - dc;
        return Av1FwdTransform.ForwardQuantRect(residual, w, h, txIdx, dcDq, acDq, rcCount, Av1FwdTransform.FwdTxType.DctDct);
    }

    // Reconstructs a rect w x h block on top of a flat DC prediction, via the decoder's InvTxfmAdd.
    private static byte DequantAndReconstructRectDc(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        int dc, ushort[] recon, int reconW, int bx, int by)
    {
        var pred = new ushort[h * w];
        Array.Fill(pred, (ushort)Math.Clamp(dc, 0, PixMax));
        return DequantAndReconstructPredRect(levels, txIdx, w, h, dcDq, acDq, pred, recon, reconW, bx, by);
    }

    // Codes one rectangular luma leaf (PARTITION_HORZ/VERT half) plus its I420 chroma: skip, Y mode (+angle),
    // UV mode (DC), then Y/U/V coefficients (rect transforms), and reconstructs all three planes. Chroma is DC-
    // predicted (no CfL for rect yet). Mirrors EncodeLeafBlockColor for a w4 x h4 (in 4-units) rectangle.
    private static void EncodeRectLeafColor(ColorPartCtx c, int lumaBs, int lumaTx, int chromaTx,
        int bx4, int by4, int w4, int h4, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        // Layout-generic chroma geometry: (w >> SsX) x (h >> SsY); for 4:2:0 these are exactly the old w/2, h/2.
        int ssX = c.SsX, ssY = c.SsY;
        if (c.Layout != Av1PixelLayout.I420) chromaTx = Av1Tables.MaxTxfmSizeForBlockSize[lumaBs, (int)c.Layout];
        int w = w4 * 4, h = h4 * 4, cw = w >> ssX, ch = h >> ssY;
        int bx = bx4 * 4, by = by4 * 4, cbx = bx >> ssX, cby = by >> ssY;
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> ssX, cyR = byR >> ssY;
        int cw4 = Math.Max(1, w4 >> ssX), ch4 = Math.Max(1, h4 >> ssY);
        ref readonly var cTDim = ref Av1Tables.TxfmDimensions[chromaTx];
        int lScan = Av1Tables.Scans[lumaTx].Length, cScan = Av1Tables.Scans[chromaTx].Length;
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);

        // Luma: rate-based mode search (safe modes, DCT_DCT rect transform), writing the best prediction.
        int aboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]];
        int leftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        var ymCdf = c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        var pred = new ushort[h * w];
        var bestPred = new ushort[h * w];
        var resBuf = new int[h * w];
        var qfCand = new double[lScan];
        var qfWin = new double[lScan];
        int[] yC = null!;
        Av1IntraPredMode yMode = Av1IntraPredMode.Dc; int yDelta = 0;
        Av1TxType yInv = Av1TxType.DctDct; int yTxIdx = 1;
        double best = double.MaxValue;
        double rectLambda = RdLambdaK * c.AcDq * c.AcDq;   // true RD: D + λ·rate (rect edges want IDTX; DctDct-only misranks)
        // The tx-type symbol is coded for rect luma only when max tx dim <= 16 (16x8/8x16); 32x16/16x32 force DctDct.
        bool rectSymbolCoded = Av1Tables.TxfmDimensions[lumaTx].Max <= (byte)Av1TxSize.Tx16x16;
        // The full intra set (with V_DCT/H_DCT) exists only while the min tx dim < 16 (square 16x16 uses the reduced set).
        bool fullHere = UseFullIntraTxSet && Av1Tables.TxfmDimensions[lumaTx].Min < (byte)Av1TxSize.Tx16x16;
        var txSet = (fullHere && rectSymbolCoded) ? IntraTxTypesFull
                  : (rectSymbolCoded ? IntraTxTypes : DctOnly);
        int intraFlags = IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]);

        // Prescreen modes by cheap SATD (as the square leaf does) and RD-evaluate only the best few — the full
        // tx-type search is the hot loop; SATD tracks coded cost closely enough that the top handful holds the winner.
        Span<int> topIdx = stackalloc int[RdModeCandidates];
        Span<long> topCost = stackalloc long[RdModeCandidates];
        topCost.Fill(long.MaxValue);
        double satdLambda = Math.Sqrt(RdLambdaK) * c.AcDq;
        for (int ci = 0; ci < CandidateModes.Length; ci++)
        {
            (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
            if (DbgLumaModeFilter != null && !DbgLumaModeFilter(mode, delta)) continue;
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred, edgeFlags, intraFlags);
            long satd = Satd8x8Rect(c.Luma, c.W, bx, by, pred, w, h);
            long mb = (long)(satdLambda * (Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0)));
            long cost = satd + mb;
            for (int k = 0; k < RdModeCandidates; k++)
                if (cost < topCost[k]) { for (int j = RdModeCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = cost; topIdx[k] = ci; break; }
        }

        for (int t = 0; t < RdModeCandidates; t++)
        {
            if (topCost[t] == long.MaxValue) break;
            (Av1IntraPredMode mode, int delta) = CandidateModes[topIdx[t]];
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred, edgeFlags, intraFlags);
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++) resBuf[yy * w + xx] = c.Luma[(by + yy) * c.W + (bx + xx)] - pred[yy * w + xx];
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            foreach (var (fwd, inv, idx) in txSet)
            {
                int[] cf = Av1FwdTransform.ForwardQuantRect(resBuf, w, h, lumaTx, c.DcDq, c.AcDq, lScan, fwd, qfCand);
                bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                double rate = (oneD
                    ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)mode, inv, cf, 0, ySign)
                    : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet)) + modeBits;
                long sse = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv);
                double j = sse + rectLambda * rate;
                if (j < best) { best = j; yC = cf; yMode = mode; yDelta = delta; yInv = inv; yTxIdx = idx; Array.Copy(pred, bestPred, h * w); Array.Copy(qfCand, qfWin, lScan); }
            }
        }

        // RDOQ-refine the winning luma coefficients (skipped for V_DCT/H_DCT: RdoqOptimize assumes the 2D scan).
        if (yInv != Av1TxType.VDct && yInv != Av1TxType.HDct)
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, qfWin, c.DcDq, c.AcDq, 0, ySign, yTxIdx,
                RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);

        // Reconstruct luma into ReconY now — CfL chroma prediction reads it (encoder-internal; independent of the
        // symbol emission order below, which the decoder does luma-then-chroma too).
        byte cfY = DequantAndReconstructPredRect(yC, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, c.ReconY, c.W, bx, by, yInv);

        // Chroma larger than its max transform (64x64 leaves outside 4:2:0): several chroma tx blocks, each predicted
        // from its own edges in the decoder's order (see RectLeafChromaMultiTx).
        if (cw > cTDim.W * 4 || ch > cTDim.H * 4)
        {
            RectLeafChromaMultiTx(c, lumaBs, lumaTx, chromaTx, bx4, by4, w4, h4, edgeFlags, ymCdf,
                yC, yMode, yDelta, yInv, yTxIdx, ySign, cfY);
            return;
        }

        int dcU = DcPredictRect(c.ReconU, c.Cw, cbx, cby, cw, ch);
        int dcV = DcPredictRect(c.ReconV, c.Cw, cbx, cby, cw, ch);
        int[] uC = ForwardResidualRectDc(c.U, c.Cw, cbx, cby, cw, ch, dcU, chromaTx, c.DcDq, c.AcDq, cScan);
        int[] vC = ForwardResidualRectDc(c.V, c.Cw, cbx, cby, cw, ch, dcV, chromaTx, c.DcDq, c.AcDq, cScan);
        double clam0 = RdLambdaK * c.AcDq * c.AcDq;
        var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode);
        var dcuP = new ushort[cw * ch]; Array.Fill(dcuP, (ushort)Math.Clamp(dcU, 0, PixMax));
        var dcvP = new ushort[cw * ch]; Array.Fill(dcvP, (ushort)Math.Clamp(dcV, 0, PixMax));

        int uvMode = 0, uvDelta = 0; bool useCfl = false;
        ushort[] predU = dcuP, predV = dcvP; int alphaU = 0, alphaV = 0;
        double bestJ = ReconSseCandRect(uC, chromaTx, cw, ch, c.DcDq, c.AcDq, dcuP, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                     + ReconSseCandRect(vC, chromaTx, cw, ch, c.DcDq, c.AcDq, dcvP, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                     + clam0 * (CoeffCost(uC) + CoeffCost(vC) + Av1CoeffEncode.SymBits(uvModeCdf, 0));

        // Directional/Smooth/Paeth UV chroma modes (see EncodeLeafBlockColor): rect chroma is a single tx block too,
        // so the same edge derivation + UV-mode-derived tx-type (TxTypeFromUvMode) applies.
        if (UseUvModeSearch)
        {
            // The layout's own availability bits (dav1d: I420TopHasRight >> (layout - 1)), passed as I444 bits.
            int lsh = (int)c.Layout - 1;
            var chromaEdge =
                ((edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420TopHasRight >> lsh)) != 0 ? Av1EdgeFlags.I444TopHasRight : 0) |
                ((edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420LeftHasBottom >> lsh)) != 0 ? Av1EdgeFlags.I444LeftHasBottom : 0);
            int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
            int cbw4 = c.Bw4 >> ssX, cbh4 = c.Bh4 >> ssY, cbx4 = bx4 >> ssX, cby4 = by4 >> ssY;
            int bDimW = Av1Tables.BlockDimensions[lumaBs, 2], bDimH = Av1Tables.BlockDimensions[lumaBs, 3];
            bool uvAngleOk = bDimW + bDimH >= 2;
            var pu = new ushort[cw * ch]; var pv = new ushort[cw * ch];
            Span<int> uvTopIdx = stackalloc int[RdUvCandidates];
            Span<long> uvTopCost = stackalloc long[RdUvCandidates];
            uvTopCost.Fill(long.MaxValue);
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (mode == Av1IntraPredMode.Dc) continue;
                if (delta != 0 && !uvAngleOk) continue;
                PredictIntraRect(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cw, ch, mode, delta, pu, chromaEdge, cIntraFlags);
                long sad = 0;
                for (int yy = 0; yy < ch; yy++) { int r = (cby + yy) * c.Cw + cbx; for (int xx = 0; xx < cw; xx++) sad += Math.Abs(c.U[r + xx] - pu[yy * cw + xx]); }
                for (int k = 0; k < RdUvCandidates; k++)
                    if (sad < uvTopCost[k]) { for (int j = RdUvCandidates - 1; j > k; j--) { uvTopCost[j] = uvTopCost[j - 1]; uvTopIdx[j] = uvTopIdx[j - 1]; } uvTopCost[k] = sad; uvTopIdx[k] = ci; break; }
            }
            for (int t = 0; t < RdUvCandidates; t++)
            {
                if (uvTopCost[t] == long.MaxValue) break;
                (Av1IntraPredMode mode, int delta) = CandidateModes[uvTopIdx[t]];
                PredictIntraRect(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cw, ch, mode, delta, pu, chromaEdge, cIntraFlags);
                PredictIntraRect(c.ReconV, c.Cw, cbw4, cbh4, cbx4, cby4, cw, ch, mode, delta, pv, chromaEdge, cIntraFlags);
                var uvTx = UvIntraTxType(chromaTx, (int)mode);
                var uvFwd = FwdTypeForTxType(uvTx);
                int[] uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pu, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, null, uvFwd);
                int[] vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pv, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, null, uvFwd);
                double modeBits = Av1CoeffEncode.SymBits(uvModeCdf, (int)mode)
                    + (IsDirectional(mode) && uvAngleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
                double j = ReconSseCandRect(uu, chromaTx, cw, ch, c.DcDq, c.AcDq, pu, c.U, c.Cw, cbx, cby, uvTx)
                         + ReconSseCandRect(vv, chromaTx, cw, ch, c.DcDq, c.AcDq, pv, c.V, c.Cw, cbx, cby, uvTx)
                         + clam0 * (CoeffCost(uu) + CoeffCost(vv) + modeBits);
                if (j < bestJ)
                {
                    bestJ = j; uvMode = (int)mode; uvDelta = delta; useCfl = false;
                    uC = uu; vC = vv; predU = (ushort[])pu.Clone(); predV = (ushort[])pv.Clone();
                }
            }
        }

        // Chroma-from-luma vs the running best.
        if (cflAllowed && UseCfl)
        {
            var acc = new short[cw * ch];
            if (c.Layout == Av1PixelLayout.I420) ComputeCflAcEncRect(c.ReconY, c.W, bx, by, cw, ch, acc);
            else CflAcAnyLayout(c, lumaBs, lumaTx, bx4, by4, acc);
            int aU = BestCflAlphaRect(c.U, c.Cw, cbx, cby, cw, ch, dcU, acc);
            int aV = BestCflAlphaRect(c.V, c.Cw, cbx, cby, cw, ch, dcV, acc);
            if (aU != 0 || aV != 0)
            {
                var pcflU = BuildCflPredRect(dcU, acc, cw, ch, aU);
                var pcflV = BuildCflPredRect(dcV, acc, cw, ch, aV);
                int[] uCc = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pcflU, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan);
                int[] vCc = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pcflV, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan);
                double cflJ = ReconSseCandRect(uCc, chromaTx, cw, ch, c.DcDq, c.AcDq, pcflU, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                            + ReconSseCandRect(vCc, chromaTx, cw, ch, c.DcDq, c.AcDq, pcflV, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                            + clam0 * (CoeffCost(uCc) + CoeffCost(vCc) + Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma) + (aU != 0 ? 5 : 0) + (aV != 0 ? 5 : 0));
                if (cflJ < bestJ)
                {
                    bestJ = cflJ; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                    alphaU = aU; alphaV = aV; uC = uCc; vC = vCc; predU = pcflU; predV = pcflV;
                }
            }
        }
        int skip = (HasNonZero(yC) || HasNonZero(uC) || HasNonZero(vC)) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        c.Msac.EncodeSymbolAdapt(ymCdf, (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        int uvSym = useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : uvMode;
        c.Msac.EncodeSymbolAdapt(uvModeCdf, uvSym, uvNsym);
        if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);
        else if (IsDirectional((Av1IntraPredMode)uvMode) &&
                 Av1Tables.BlockDimensions[lumaBs, 2] + Av1Tables.BlockDimensions[lumaBs, 3] >= 2)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf(uvMode - (int)Av1IntraPredMode.Vertical), uvDelta + 3, 6);

        // filter_intra: rect leaves are all <=32x32, so a DC rect block must emit use_filter_intra (0 here).
        if (UseFilterIntra && yMode == Av1IntraPredMode.Dc &&
            Math.Max(Av1Tables.BlockDimensions[lumaBs, 2], Av1Tables.BlockDimensions[lumaBs, 3]) <= 3)
            c.Msac.EncodeBoolAdapt(c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs), 0);

        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), 0, Math.Min((int)lTDim.Max, 2));
        }

        byte cfU = 0x40, cfV = 0x40;
        if (skip == 0)
        {
            int uSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)c.Layout);
            int vSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)c.Layout);
            int uSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int vSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
            if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)yMode, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);
            var uvTxR = UvIntraTxType(chromaTx, uvSym);
            cfU = DequantAndReconstructPredRect(uC, chromaTx, cw, ch, c.DcDq, c.AcDq, predU, c.ReconU, c.Cw, cbx, cby, uvTxR);
            cfV = DequantAndReconstructPredRect(vC, chromaTx, cw, ch, c.DcDq, c.AcDq, predV, c.ReconV, c.Cw, cbx, cby, uvTxR);
        }
        else
        {
            for (int yy = 0; yy < ch; yy++) Array.Copy(predU, yy * cw, c.ReconU, (cby + yy) * c.Cw + cbx, cw);
            for (int yy = 0; yy < ch; yy++) Array.Copy(predV, yy * cw, c.ReconV, (cby + yy) * c.Cw + cbx, cw);
        }

        int yW = Math.Min(w4, c.Bw4 - bx4), yH = Math.Min(h4, c.Bh4 - by4);
        int cW = Math.Min(cw4, (c.Bw4 - bx4 + ssX) >> ssX), cH = Math.Min(ch4, (c.Bh4 - by4 + ssY) >> ssY);
        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++) { c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; }
        for (int j = 0; j < yH && byR + j < 32; j++) { c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = cfU; c.ACV[cxR + i] = cfV; c.AModeUv[cxR + i] = (byte)uvSym; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = cfU; c.LCV[cyR + j] = cfV; c.LModeUv[cyR + j] = (byte)uvSym; }
    }

    // Intra chroma transform type: derived from the UV mode, except DCT_DCT once the chroma tx reaches 32 in either
    // dimension (dav1d decode_coefs: t_dim->max + intra >= TX_64X64).
    private static Av1TxType UvIntraTxType(int chromaTx, int uvMode)
        => Av1Tables.TxfmDimensions[chromaTx].Max >= (byte)Av1TxSize.Tx32x32
            ? Av1TxType.DctDct : (Av1TxType)Av1Tables.TxTypeFromUvMode[uvMode];

    // Chroma for a leaf whose chroma block exceeds its max transform (4:4:4 / 4:2:2 64x64: 64x64 / 32x64 chroma on
    // TX_32X32). Mirrors dav1d recon_b_intra: tx blocks in raster order (one 64x64 luma chunk), each predicted from
    // its own edges — earlier tx blocks of this leaf included — with the per-tx top-right/bottom-left rules, tx
    // blocks past the visible frame edge not coded, and per-tx coefficient contexts. Luma is already decided and
    // reconstructed by the caller; this picks the UV mode by exact per-tx RD, then emits the whole block.
    private static void RectLeafChromaMultiTx(ColorPartCtx c, int lumaBs, int lumaTx, int chromaTx, int bx4, int by4,
        int w4, int h4, Av1EdgeFlags edgeFlags, Span<ushort> ymCdf, int[] yC, Av1IntraPredMode yMode, int yDelta,
        Av1TxType yInv, int yTxIdx, int ySign, byte cfY)
    {
        int ssX = c.SsX, ssY = c.SsY;
        ref readonly var ct = ref Av1Tables.TxfmDimensions[chromaTx];
        int ctW = ct.W, ctH = ct.H;
        int tw = ctW * 4, th = ctH * 4, cScan = Av1Tables.Scans[chromaTx].Length;
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> ssX, cyR = byR >> ssY;
        int cbx4 = bx4 >> ssX, cby4 = by4 >> ssY, pbw4 = c.Bw4 >> ssX, pbh4 = c.Bh4 >> ssY;
        int vw4 = Math.Min(w4, c.Bw4 - bx4), vh4 = Math.Min(h4, c.Bh4 - by4);
        int cw4v = (vw4 + ssX) >> ssX, ch4v = (vh4 + ssY) >> ssY;
        int subCw4 = Math.Min(cw4v, 16 >> ssX), subCh4 = Math.Min(ch4v, 16 >> ssY);
        int lsh = (int)c.Layout - 1;
        bool sbHasTr = (16 >> ssX) < cw4v || (edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420TopHasRight >> lsh)) != 0;
        bool sbHasBl = (16 >> ssY) < ch4v || (edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420LeftHasBottom >> lsh)) != 0;
        var txs = new List<(int X, int Y, Av1EdgeFlags Edge)>();
        for (int y = 0; y < subCh4; y += ctH)
            for (int x = 0; x < subCw4; x += ctW)
            {
                var e = (((y > 0 || !sbHasTr) && x + ctW >= subCw4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                        ((x > 0 || (!sbHasBl && y + ctH >= subCh4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                txs.Add((x, y, e));
            }
        int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
        int cpx = cbx4 * 4, cpy = cby4 * 4;
        int rw = Math.Min((w4 * 4) >> ssX, c.Cw - cpx), rh = Math.Min((h4 * 4) >> ssY, c.Chh - cpy);
        ushort[] saveU = CopyRegion(c.ReconU, c.Cw, cpx, cpy, rw, rh), saveV = CopyRegion(c.ReconV, c.Cw, cpx, cpy, rw, rh);
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;   // never at 64 — CfL is <=32x32
        var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode);
        int bDimW = Av1Tables.BlockDimensions[lumaBs, 2], bDimH = Av1Tables.BlockDimensions[lumaBs, 3];
        bool uvAngleOk = bDimW + bDimH >= 2;
        double lambda = RdLambdaK * c.AcDq * c.AcDq;
        var pred = new ushort[tw * th];

        // Codes one plane with (mode, delta) through every tx block, reconstructing in place; returns SSE + λ·bits.
        double RunPlane(ushort[] src, ushort[] recon, Av1IntraPredMode mode, int delta, List<(int[] Lv, byte Cf)> outLv)
        {
            var uvTx = UvIntraTxType(chromaTx, (int)mode);
            var fwd = FwdTypeForTxType(uvTx);
            double j = 0;
            foreach (var (x, y, e) in txs)
            {
                PredictIntraRect(recon, c.Cw, pbw4, pbh4, cbx4 + x, cby4 + y, tw, th, mode, delta, pred, e, cIntraFlags);
                int px = cpx + x * 4, py = cpy + y * 4;
                int[] lv = ForwardResidualPredRect(src, c.Cw, px, py, pred, tw, th, chromaTx, c.DcDq, c.AcDq, cScan, null, fwd);
                byte cf = DequantAndReconstructPredRect(lv, chromaTx, tw, th, c.DcDq, c.AcDq, pred, recon, c.Cw, px, py, uvTx);
                long sse = 0;
                for (int yy = 0; yy < th; yy++)
                    for (int xx = 0; xx < tw; xx++)
                    { int d = recon[(py + yy) * c.Cw + px + xx] - src[(py + yy) * c.Cw + px + xx]; sse += (long)d * d; }
                j += sse + lambda * CoeffCost(lv);
                outLv.Add((lv, cf));
            }
            return j;
        }

        // Candidates: DC plus the best few non-DC modes by SAD of their per-tx prediction off the pre-leaf recon.
        var cands = new List<(Av1IntraPredMode Mode, int Delta)> { (Av1IntraPredMode.Dc, 0) };
        if (UseUvModeSearch)
        {
            var topIdx = new int[RdUvCandidates];
            var topCost = new long[RdUvCandidates];
            Array.Fill(topCost, long.MaxValue);
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (mode == Av1IntraPredMode.Dc || (delta != 0 && !uvAngleOk)) continue;
                long sad = 0;
                foreach (var (x, y, e) in txs)
                {
                    PredictIntraRect(c.ReconU, c.Cw, pbw4, pbh4, cbx4 + x, cby4 + y, tw, th, mode, delta, pred, e, cIntraFlags);
                    int px = cpx + x * 4, py = cpy + y * 4;
                    for (int yy = 0; yy < th; yy++)
                        for (int xx = 0; xx < tw; xx++) sad += Math.Abs(c.U[(py + yy) * c.Cw + px + xx] - pred[yy * tw + xx]);
                }
                for (int k = 0; k < RdUvCandidates; k++)
                    if (sad < topCost[k]) { for (int q = RdUvCandidates - 1; q > k; q--) { topCost[q] = topCost[q - 1]; topIdx[q] = topIdx[q - 1]; } topCost[k] = sad; topIdx[k] = ci; break; }
            }
            for (int k = 0; k < RdUvCandidates && topCost[k] != long.MaxValue; k++) cands.Add(CandidateModes[topIdx[k]]);
        }

        double bestJ = double.MaxValue;
        (Av1IntraPredMode Mode, int Delta) best = cands[0];
        List<(int[] Lv, byte Cf)> bestU = null!, bestV = null!;
        ushort[] bestRU = null!, bestRV = null!;
        foreach (var (mode, delta) in cands)
        {
            PasteRegion(saveU, c.ReconU, c.Cw, cpx, cpy, rw, rh);
            PasteRegion(saveV, c.ReconV, c.Cw, cpx, cpy, rw, rh);
            var lu = new List<(int[] Lv, byte Cf)>(); var lvv = new List<(int[] Lv, byte Cf)>();
            double modeBits = Av1CoeffEncode.SymBits(uvModeCdf, (int)mode)
                + (IsDirectional(mode) && uvAngleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            double j = RunPlane(c.U, c.ReconU, mode, delta, lu) + RunPlane(c.V, c.ReconV, mode, delta, lvv) + lambda * modeBits;
            if (j < bestJ)
            {
                bestJ = j; best = (mode, delta); bestU = lu; bestV = lvv;
                bestRU = CopyRegion(c.ReconU, c.Cw, cpx, cpy, rw, rh); bestRV = CopyRegion(c.ReconV, c.Cw, cpx, cpy, rw, rh);
            }
        }
        PasteRegion(bestRU, c.ReconU, c.Cw, cpx, cpy, rw, rh);
        PasteRegion(bestRV, c.ReconV, c.Cw, cpx, cpy, rw, rh);
        int uvMode = (int)best.Mode, uvDelta = best.Delta;

        bool anyC = false;
        foreach (var l in bestU) anyC |= HasNonZero(l.Lv);
        foreach (var l in bestV) anyC |= HasNonZero(l.Lv);
        int skip = (HasNonZero(yC) || anyC) ? 0 : 1;

        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);
        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        c.Msac.EncodeSymbolAdapt(ymCdf, (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        c.Msac.EncodeSymbolAdapt(uvModeCdf, uvMode, uvNsym);
        if (IsDirectional((Av1IntraPredMode)uvMode) && uvAngleOk)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf(uvMode - (int)Av1IntraPredMode.Vertical), uvDelta + 3, 6);
        // (no filter_intra symbol: these leaves are 64 wide, above the 32x32 filter-intra limit)

        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), 0, Math.Min((int)lTDim.Max, 2));
        }

        if (skip == 0)
        {
            if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)yMode, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            for (int pl = 0; pl < 2; pl++)
            {
                byte[] ac = pl == 0 ? c.ACU : c.ACV, lc = pl == 0 ? c.LCU : c.LCV;
                var lvs = pl == 0 ? bestU : bestV;
                for (int k = 0; k < txs.Count; k++)
                {
                    var (x, y, _) = txs[k];
                    int sk = Av1CoeffDecode.GetSkipCtx(in ct, lumaBs, ac.AsSpan(cxR + x), lc.AsSpan(cyR + y), 1, (int)c.Layout);
                    int sg = Av1CoeffDecode.GetDcSignCtx(chromaTx, ac.AsSpan(cxR + x), lc.AsSpan(cyR + y));
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, lvs[k].Lv, skipCtx: sk, dcSignCtx: sg);
                    // dav1d: per-tx ctx fill, clipped to the visible frame
                    int ctw = Math.Min(ctW, (c.Bw4 - (bx4 + (x << ssX)) + ssX) >> ssX);
                    int cth = Math.Min(ctH, (c.Bh4 - (by4 + (y << ssY)) + ssY) >> ssY);
                    for (int i = 0; i < ctw && cxR + x + i < 32; i++) ac[cxR + x + i] = lvs[k].Cf;
                    for (int i = 0; i < cth && cyR + y + i < 32; i++) lc[cyR + y + i] = lvs[k].Cf;
                }
            }
        }

        int yW = Math.Min(w4, c.Bw4 - bx4), yH = Math.Min(h4, c.Bh4 - by4);
        int cW = Math.Min(Math.Max(1, w4 >> ssX), (c.Bw4 - bx4 + ssX) >> ssX), cH = Math.Min(Math.Max(1, h4 >> ssY), (c.Bh4 - by4 + ssY) >> ssY);
        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++) { c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; }
        for (int j = 0; j < yH && byR + j < 32; j++) { c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { if (skip != 0) { c.ACU[cxR + i] = 0x40; c.ACV[cxR + i] = 0x40; } c.AModeUv[cxR + i] = (byte)uvMode; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { if (skip != 0) { c.LCU[cyR + j] = 0x40; c.LCV[cyR + j] = 0x40; } c.LModeUv[cyR + j] = (byte)uvMode; }
    }

    // CfL luma AC for 4:2:2 / 4:4:4 via the decoder's own Av1Reconstruction.ComputeCflAc, with the decoder's exact
    // edge padding (recon_tmpl cfl: furthest_r/b from the visible luma extent rounded to the luma tx size).
    private static void CflAcAnyLayout(ColorPartCtx c, int lumaBs, int lumaTx, int bx4, int by4, short[] ac)
    {
        int ssX = c.SsX, ssY = c.SsY;
        int bw4 = Av1Tables.BlockDimensions[lumaBs, 0], bh4 = Av1Tables.BlockDimensions[lumaBs, 1];
        int w4 = Math.Min(bw4, c.Bw4 - bx4), h4 = Math.Min(bh4, c.Bh4 - by4);
        int cw4 = (w4 + ssX) >> ssX, ch4 = (h4 + ssY) >> ssY;
        int cbw4 = (bw4 + ssX) >> ssX, cbh4 = (bh4 + ssY) >> ssY;
        ref readonly var td = ref Av1Tables.TxfmDimensions[lumaTx];
        int furthestR = ((cw4 << ssX) + td.W - 1) & ~(td.W - 1);
        int furthestB = ((ch4 << ssY) + td.H - 1) & ~(td.H - 1);
        int wPad = cbw4 - (furthestR >> ssX), hPad = cbh4 - (furthestB >> ssY);
        int yOff = 4 * ((bx4 & ~ssX) + (by4 & ~ssY) * c.W);
        Av1Reconstruction.ComputeCflAc(ac, c.ReconY.AsSpan(yOff), c.W, cbw4 * 4, cbh4 * 4, ssX, ssY, wPad, hPad);
    }

    // Codes PARTITION_HORZ/VERT at the 8x8 level -> two 8x4 (horz) or 4x8 (vert) luma sub-blocks. AV1 shared-chroma:
    // the chroma (4x4, covering the 8x8) is coded once on the SECOND (odd-position) sub-block. partCdf/nPart are the
    // 8x8 partition CDF (PartitionTypeCount[4]=3). Mirrors the decoder's HORZ/VERT recursion at Bl8x8.
    private static void EncodeSub8Pair(ColorPartCtx c, bool horz, int bx4, int by4, Span<ushort> partCdf, int nPart, int bx8, int by8, Av1EdgeNode node)
    {
        c.Msac.EncodeSymbolAdapt(partCdf, (int)(horz ? Av1BlockPartition.Horizontal : Av1BlockPartition.Vertical), nPart);
        if (horz)
        {
            // two 8x4: top (by4, no chroma), bottom (by4+1, chroma over the 8x8)
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, bx4, by4, 2, 1, false, node.H0);
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, bx4, by4 + 1, 2, 1, true, node.H1);
        }
        else
        {
            // two 4x8: left (bx4, no chroma), right (bx4+1, chroma over the 8x8)
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, bx4, by4, 1, 2, false, node.V0);
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, bx4 + 1, by4, 1, 2, true, node.V1);
        }
        FillPartCtx(c, 4, bx8, by8, 1, horz ? Av1BlockPartition.Horizontal : Av1BlockPartition.Vertical);
    }

    // One sub-8x8 luma leaf (8x4 or 4x8): skip, y_mode (NO angle_delta for these sizes), use_filter_intra (eligible,
    // always 0 here), tx_size, luma coeffs. When hasChroma (the odd-position sub-block) it additionally codes uv_mode
    // (DC) + a 4x4 chroma over the 8x8-aligned region. Luma tx-type is Dct_Dct (the symbol is still coded = idx 1).
    private static void EncodeSub8Leaf(ColorPartCtx c, int lumaBs, int lumaTx, int bx4, int by4, int w4, int h4, bool hasChroma, Av1EdgeFlags edge)
    {
        int w = w4 * 4, h = h4 * 4, bx = bx4 * 4, by = by4 * 4;
        int bxR = bx4 & 31, byR = by4 & 31;
        int lScan = Av1Tables.Scans[lumaTx].Length;
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        int aboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]], leftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        var ymCdf = c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        int intraFlags = IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]);

        // Luma mode search (no angle_delta at these sizes, so only the ~13 base modes — all RD-evaluated directly,
        // no SATD prescreen). Full reduced tx-type set (IDTX+DCT/ADST): the symbol is coded for 8x4/4x8 (max tx
        // dim <= 16), and IDTX helps these tiny edge sub-blocks — same win as the square/rect leaves.
        var pred = new ushort[h * w]; var bestPred = new ushort[h * w]; var resBuf = new int[h * w];
        var qfCand = new double[lScan]; var qfWin = new double[lScan];
        double rectLambda = RdLambdaK * c.AcDq * c.AcDq;
        int[] yC = null!; Av1IntraPredMode yMode = Av1IntraPredMode.Dc; Av1TxType yInv = Av1TxType.DctDct; int yTxIdx = 1;
        double best = double.MaxValue;
        for (int ci = 0; ci < CandidateModes.Length; ci++)
        {
            (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
            if (DbgLumaModeFilter != null && !DbgLumaModeFilter(mode, delta)) continue;
            if (delta != 0) continue;   // no angle_delta at 8x4/4x8: only the base (delta 0) mode
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, 0, pred, edge, intraFlags);
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) resBuf[yy * w + xx] = c.Luma[(by + yy) * c.W + (bx + xx)] - pred[yy * w + xx];
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode);
            foreach (var (fwd, inv, idx) in (UseFullIntraTxSet ? IntraTxTypesFull : IntraTxTypes))
            {
                int[] cf = Av1FwdTransform.ForwardQuantRect(resBuf, w, h, lumaTx, c.DcDq, c.AcDq, lScan, fwd, qfCand);
                bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                double rate = oneD
                    ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)mode, inv, cf, 0, ySign)
                    : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet);
                double j = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv)
                         + rectLambda * (rate + modeBits);
                if (j < best) { best = j; yC = cf; yMode = mode; yInv = inv; yTxIdx = idx; Array.Copy(pred, bestPred, h * w); Array.Copy(qfCand, qfWin, lScan); }
            }
        }
        if (yInv != Av1TxType.VDct && yInv != Av1TxType.HDct)
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, qfWin, c.DcDq, c.AcDq, 0, ySign, yTxIdx,
                RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);

        // Reconstruct this sub-block's luma into ReconY now — the has_chroma sub-block's CfL reads the full 8x8 luma
        // (both sub-blocks are reconstructed by the time we reach it). Encoder-internal; emission order is unaffected.
        byte cfY = DequantAndReconstructPredRect(yC, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, c.ReconY, c.W, bx, by, yInv);

        // Chroma (deferred to the has_chroma sub-block): 4x4 over the 8x8-aligned region. DC vs CfL by RD.
        int c8x4 = bx4 & ~1, c8y4 = by4 & ~1, cbx = c8x4 * 2, cby = c8y4 * 2, cxR = (c8x4 & 31) >> 1, cyR = (c8y4 & 31) >> 1;
        const int chromaTx = (int)Av1TxSize.Tx4x4; int cScan = Av1Tables.Scans[chromaTx].Length;
        int dcU = 0, dcV = 0; int[] uC = System.Array.Empty<int>(), vC = System.Array.Empty<int>();
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);
        bool useCfl = false; int alphaU = 0, alphaV = 0; ushort[]? cflU = null, cflV = null;
        if (hasChroma)
        {
            dcU = DcPredictRect(c.ReconU, c.Cw, cbx, cby, 4, 4);
            dcV = DcPredictRect(c.ReconV, c.Cw, cbx, cby, 4, 4);
            uC = ForwardResidualRectDc(c.U, c.Cw, cbx, cby, 4, 4, dcU, chromaTx, c.DcDq, c.AcDq, cScan);
            vC = ForwardResidualRectDc(c.V, c.Cw, cbx, cby, 4, 4, dcV, chromaTx, c.DcDq, c.AcDq, cScan);
            if (cflAllowed && UseCfl)
            {
                var acc = new short[16];
                ComputeCflAcEncRect(c.ReconY, c.W, c8x4 * 4, c8y4 * 4, 4, 4, acc);
                alphaU = BestCflAlphaRect(c.U, c.Cw, cbx, cby, 4, 4, dcU, acc);
                alphaV = BestCflAlphaRect(c.V, c.Cw, cbx, cby, 4, 4, dcV, acc);
                cflU = BuildCflPredRect(dcU, acc, 4, 4, alphaU);
                cflV = BuildCflPredRect(dcV, acc, 4, 4, alphaV);
                int[] uCc = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, cflU, 4, 4, chromaTx, c.DcDq, c.AcDq, cScan);
                int[] vCc = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, cflV, 4, 4, chromaTx, c.DcDq, c.AcDq, cScan);
                var dcuP = new ushort[16]; Array.Fill(dcuP, (ushort)Math.Clamp(dcU, 0, PixMax));
                var dcvP = new ushort[16]; Array.Fill(dcvP, (ushort)Math.Clamp(dcV, 0, PixMax));
                double lam = RdLambdaK * c.AcDq * c.AcDq;
                double dcJ = ReconSseCandRect(uC, chromaTx, 4, 4, c.DcDq, c.AcDq, dcuP, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                           + ReconSseCandRect(vC, chromaTx, 4, 4, c.DcDq, c.AcDq, dcvP, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                           + lam * (CoeffCost(uC) + CoeffCost(vC));
                double cflJ = ReconSseCandRect(uCc, chromaTx, 4, 4, c.DcDq, c.AcDq, cflU, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                            + ReconSseCandRect(vCc, chromaTx, 4, 4, c.DcDq, c.AcDq, cflV, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                            + lam * (CoeffCost(uCc) + CoeffCost(vCc) + 8 + (alphaU != 0 ? 5 : 0) + (alphaV != 0 ? 5 : 0));
                if (cflJ < dcJ) { useCfl = true; uC = uCc; vC = vCc; }
            }
        }
        int skip = (HasNonZero(yC) || (hasChroma && (HasNonZero(uC) || HasNonZero(vC)))) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        c.Msac.EncodeSymbolAdapt(ymCdf, (int)yMode, 12);   // no angle_delta for 8x4/4x8
        if (hasChroma)
        {
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode), useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : 0, uvNsym);
            if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);
        }
        if (UseFilterIntra && yMode == Av1IntraPredMode.Dc &&
            Math.Max(Av1Tables.BlockDimensions[lumaBs, 2], Av1Tables.BlockDimensions[lumaBs, 3]) <= 3)
            c.Msac.EncodeBoolAdapt(c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs), 0);
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), 0, Math.Min((int)lTDim.Max, 2));
        }

        byte cfU = 0x40, cfV = 0x40;
        ref readonly var cTDim = ref Av1Tables.TxfmDimensions[chromaTx];
        if (skip == 0)
        {
            if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)yMode, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            if (hasChroma)
            {
                int uSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
                int vSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
                int uSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
                int vSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);
            }
            if (hasChroma)
            {
                if (useCfl)
                {
                    cfU = DequantAndReconstructPredRect(uC, chromaTx, 4, 4, c.DcDq, c.AcDq, cflU!, c.ReconU, c.Cw, cbx, cby);
                    cfV = DequantAndReconstructPredRect(vC, chromaTx, 4, 4, c.DcDq, c.AcDq, cflV!, c.ReconV, c.Cw, cbx, cby);
                }
                else
                {
                    cfU = DequantAndReconstructRectDc(uC, chromaTx, 4, 4, c.DcDq, c.AcDq, dcU, c.ReconU, c.Cw, cbx, cby);
                    cfV = DequantAndReconstructRectDc(vC, chromaTx, 4, 4, c.DcDq, c.AcDq, dcV, c.ReconV, c.Cw, cbx, cby);
                }
            }
        }
        else if (hasChroma)
        {
            if (useCfl)
            {
                for (int yy = 0; yy < 4; yy++) Array.Copy(cflU!, yy * 4, c.ReconU, (cby + yy) * c.Cw + cbx, 4);
                for (int yy = 0; yy < 4; yy++) Array.Copy(cflV!, yy * 4, c.ReconV, (cby + yy) * c.Cw + cbx, 4);
            }
            else { FillFlatRect(c.ReconU, c.Cw, cbx, cby, 4, 4, dcU); FillFlatRect(c.ReconV, c.Cw, cbx, cby, 4, 4, dcV); }
        }

        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        for (int i = 0; i < w4 && bxR + i < 32; i++) { c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = 0; }
        for (int j = 0; j < h4 && byR + j < 32; j++) { c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = 0; }
        if (hasChroma) { byte s8u = (byte)(useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : 0); c.ACU[cxR] = cfU; c.ACV[cxR] = cfV; c.LCU[cyR] = cfU; c.LCV[cyR] = cfV; c.AModeUv[cxR] = s8u; c.LModeUv[cyR] = s8u; }
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

    private static int[] ForwardResidual(ReadOnlySpan<ushort> plane, int planeW, int bx, int by, int n, int dcPred,
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
        if (w < 1 || h < 1 || w > 65536 || h > 65536)
        {
            throw new NotSupportedException($"AVIF encode supports 1..65536 per dimension (got {w}x{h}).");
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
    private static ushort[] PadPlane(ReadOnlySpan<ushort> src, int w, int h, int pw, int ph)
    {
        var padded = new ushort[pw * ph];
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

    private static void FillFlat(ushort[] recon, int reconW, int bx, int by, int n, int value)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = (ushort)value;
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
        public ushort[] Luma = null!;   // padded plane
        public ushort[] Recon = null!;  // padded plane (SB-aligned), reconstructed as-we-go
        public int W;                 // padded stride
        public int Bw4, Bh4;          // REAL frame dims in 4-units (partition decisions use these)
        public int DcDq, AcDq;
        public long SplitLambda;      // rate bias for the split decision, in SATD units

        // Above (per SB128 column), Left (per SB row).
        public byte[] AbovePart = null!, AboveLCoef = null!, AboveMode = null!, AboveSkip = null!;
        public byte[] LeftPart = null!, LeftLCoef = null!, LeftMode = null!, LeftSkip = null!;
        public sbyte[] AboveTxIntra = null!, LeftTxIntra = null!; // neighbour tx log-size, for the tx-depth context
        public ushort[] Pred = new ushort[64 * 64];
        public ushort[] EstScratch = new ushort[64 * 64];
    }

    private static sbyte[] FilledSbyte(int n, sbyte v) { var a = new sbyte[n]; Array.Fill(a, v); return a; }

    private static int BlToTx(int bl) => bl switch { 1 => 4, 2 => 3, 3 => 2, _ => 1 };            // TX size ordinal
    private static int BlToBs(int bl) => bl switch { 1 => 3, 2 => 7, 3 => 12, _ => 17 };          // Av1BlockSize ordinal

    // Codes the whole tile with a recursive partition tree (PARTITION_NONE / PARTITION_SPLIT down to 8x8),
    // reconstructing each leaf so later blocks predict from the pixels the decoder will produce. Only fully-inside
    // blocks are split; edge superblocks (non-multiple-of-64 frames) stay a single PARTITION_NONE 64x64 block, so
    // every partition decision is full-range and never needs the partial-edge bool path.
    private static byte[] EncodeMultiSbTile(ReadOnlySpan<ushort> luma, int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int acDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];

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
            Msac = new Av1MsacWriter(), Cdf = cdf, Luma = new ushort[0], Recon = new ushort[w * h], W = w,
            Bw4 = bw4, Bh4 = bh4, DcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 0], AcDq = acDq,
            // Extra split bias beyond the per-block header cost already in EstimateCost. Zero works well because
            // that header term already penalises the four sub-block headers a split introduces.
            SplitLambda = 0L,
        };
        // `Luma` is a ReadOnlySpan param; copy to a field-friendly array once.
        var lumaArr = new ushort[w * h];
        luma.CopyTo(lumaArr);
        ctx.Luma = lumaArr;

        var (_, _, colStart, rowStart) = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var tiles = new List<byte[]>();
        try
        {
            for (int tr = 0; tr + 1 < rowStart.Length; tr++)
                for (int tc = 0; tc + 1 < colStart.Length; tc++)
                {
                    if (tiles.Count > 0)
                    {
                        ctx.Cdf = new Av1CdfContext();
                        Av1CdfDefaults.InitializeDefault(ctx.Cdf, qcat);
                        ctx.Msac = new Av1MsacWriter();
                        for (int i = 0; i < sb128Cols; i++)
                        {
                            abovePart[i] = new byte[16]; aboveLCoef[i] = Filled(32); aboveMode[i] = new byte[32];
                            aboveSkip[i] = new byte[32]; aboveTxIntra[i] = FilledSbyte(32, -1);
                        }
                    }
                    SetTileWindow(colStart[tc] * 16, rowStart[tr] * 16, Math.Min(colStart[tc + 1] * 16, bw4),
                        Math.Min(rowStart[tr + 1] * 16, bh4), w, 0, 0);
                    for (int sby = rowStart[tr]; sby < rowStart[tr + 1]; sby++)
                    {
                        ctx.LeftPart = new byte[16];
                        ctx.LeftLCoef = Filled(32);
                        ctx.LeftMode = new byte[32];
                        ctx.LeftSkip = new byte[32];
                        ctx.LeftTxIntra = FilledSbyte(32, -1);
                        for (int sbx = colStart[tc]; sbx < colStart[tc + 1]; sbx++)
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
                    tiles.Add(ctx.Msac.Finish());
                }
        }
        finally
        {
            ClearTileWindow();
        }

        return AssembleTileGroup(tiles);
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
            var predBuf = new ushort[txN * txN];
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
    // Re-swept AGAIN 2026-09-21 after this session's prediction gains (CfL, directional UV modes, full intra tx set):
    // the residual statistics shifted the base-λ optimum DOWN, 0.002->0.00125 = -1.02% BD-rate (clean minimum:
    // 0.0011->+7.84, 0.00125->+7.59, 0.00135->+8.01; RdoqLambdaScale 50 re-confirmed, 65/80 worse). Better
    // prediction ⇒ keeping more residual detail (lower λ) wins.
    internal static double RdLambdaK = 0.00125;

    // Extra multiplier on the RDOQ lambda relative to the partition lambda. The partition lambda is tuned for
    // whole-block decisions; coefficient RDOQ needs a larger effective lambda to trade a marginal coefficient's
    // small distortion against its (EOB-inclusive) coding rate. Retuned to 20 (from 30) in the 2026-09 corpus
    // sweep jointly with RdLambdaK=0.004 / DeadzoneBias=0.04; then raised 20->25 in the tx_depth-on re-sweep
    // (jointly with RdLambdaK=0.003). Chroma uses the same scale (ChromaRdoqLambdaScale). Raised 25->40 in the
    // filter-intra re-sweep (2026-09-16): filter improves prediction, so residuals are smaller and more aggressive
    // coefficient trimming wins (-0.46% BD-rate, clean 5/6; the knee — 36=-0.44, 40=-0.46, 45=-0.41 vs 25).
    // RdLambdaK stayed 0.002 and DeadzoneBias stayed 0.04 (both re-confirmed optimal in the same sweep).
    internal static double RdoqLambdaScale = 50.0;

    // Chroma coefficient RDOQ in the square colour leaf (EncodeLeafBlockColor). Chroma was previously coded at
    // round-to-nearest with no rate-distortion trimming, running measurably richer than luma at matched rate;
    // this applies the same RDOQ to U/V. Scale kept equal to luma initially, tuned against the RD benchmark.
    internal static bool UseChromaRdoq = true;
    internal static double ChromaRdoqLambdaScale = 50.0;   // 25->40 (filter-intra re-sweep) ->50 (re-swept after UV modes + full tx set: gap -0.85%)

    // Enables PARTITION_HORZ / PARTITION_VERT rectangular leaves at 16x16 (colour path). Toggle for A/B testing.
    internal static bool UseRectPartition = true;

    // Sub-8x8 rectangular partitions: PARTITION_HORZ/VERT at the 8x8 level -> two 8x4 or 4x8 luma sub-blocks with
    // AV1 shared-chroma (chroma coded once per 8x8, on the odd-position sub-block, over 4x4). -0.93% BD-rate (clean
    // on all 6 corpus images), byte-exact vs ffmpeg/libdav1d. Directly attacks piechart over-splitting (the profile
    // showed libaom fits wedge edges with 8x4/4x8 where we full-SPLIT to 8x8). Adds encode cost (every 8x8 RD-trials
    // NONE/HORZ/VERT with sub-block coding).
    internal static bool UseSub8Partition = true;

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
    internal static double EarlyTermBits = 8.0;

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
    private static long EstimateBlockCost(ushort[] luma, int w, int bw4, int bh4, int dcDq, int acDq, ushort[] scratch,
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
        var reconTmp = new ushort[n * n];
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


    // How many SATD-best modes the RD leaf search fully rate-evaluates (of ~61 candidates: DC/Smooth/SmoothV/
    // SmoothH/Paeth + 8 directional × 7 angle_deltas). The SATD prescreen is an imperfect proxy, so it discards
    // true-RD winners at small counts: a corpus sweep (2026-09-16) found 4→8 = -0.31%, 8→16 = -0.74%, 16→32 =
    // -0.79% (saturated) BD-rate. 16 is the knee — essentially all the quality for ~1/4 the RD cost of an
    // exhaustive search. Higher trades encode time for <0.1%.
    internal static int RdModeCandidates = 16;

    // Dev/conformance isolation: when set, only luma intra candidates passing the filter are considered (square,
    // rect and sub-8x8 leaves). Null in production.
    internal static Func<Av1IntraPredMode, int, bool>? DbgLumaModeFilter;

    // Chroma UV-mode search: try directional/Smooth/Paeth UV predictions (not just DC/CfL) so sharp colour
    // boundaries stop paying full chroma residual. SAD-prescreen to this many candidates for the full chroma RD.
    internal static bool UseUvModeSearch = true;
    internal static int RdUvCandidates = 6;

    // Full intra transform set (reduced_tx_set=0): adds V_DCT/H_DCT (1D DCT) for sub-16x16 luma, fitting sharp
    // horizontal/vertical edges (piechart wedges, logo edges) with less residual. When on, the colour path's seq
    // header codes reduced_tx_set=0 and every sub-16x16 luma tx codes the 7-type Intra1 symbol.
    internal static bool UseFullIntraTxSet = true;

    // Extended T-shape partitions (HORZ_A/B, VERT_A/B) at 32x32/16x16 — libaom uses the full 10-type partition set;
    // we default to the 4 basic ones. These add candidates to the true-RD partition search (quarter squares + half
    // rects). Byte-exact: the sub-block order + edge availability mirror our decoder's Av1Decode recursion.
    internal static bool UseExtPartition = true;

    // Full-set (Intra1) tx candidates for the square depth-0 luma leaf: the 5 reduced types + V_DCT/H_DCT.
    // Idx is the Intra2 index (mapped to Intra1 at emit time); V/H_DCT carry -1 and route through EncodeCoefs1D.
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] IntraTxTypesFull =
    {
        (Av1FwdTransform.FwdTxType.Identity, Av1TxType.Identity, 0),
        (Av1FwdTransform.FwdTxType.DctDct,   Av1TxType.DctDct,   1),
        (Av1FwdTransform.FwdTxType.AdstAdst, Av1TxType.AdstAdst, 2),
        (Av1FwdTransform.FwdTxType.AdstDct,  Av1TxType.AdstDct,  3),
        (Av1FwdTransform.FwdTxType.DctAdst,  Av1TxType.DctAdst,  4),
        (Av1FwdTransform.FwdTxType.VDct,     Av1TxType.VDct,    -1),
        (Av1FwdTransform.FwdTxType.HDct,     Av1TxType.HDct,    -1),
    };

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
    private static void PredictIntra(ushort[] recon, int reconW, int bw4, int bh4, int bx4, int by4, int n,
        Av1IntraPredMode mode, int delta, ushort[] dst, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int intraFlags = 0)
    {
        Span<ushort> edge = stackalloc ushort[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = n >> 2;
        var tb = TileBounds4(reconW, bw4, bh4);
        bool haveTop = by4 > tb.Y0;
        bool haveLeft = bx4 > tb.X0;
        int angle = delta; // PrepareIntraEdges folds this into the base angle for directional modes
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, tb.X1, tb.Y1, edgeFlags,
            recon, dstOff, reconW, default, mode, ref angle, tw4, tw4,
            filterEdge: (intraFlags & EdgeFilterEnableBit) != 0, edge, edgeCenter, Bd);
        Av1IntraPred.Predict16(m, dst, n, edge, edgeCenter, n, n, angle | intraFlags,
            4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4, Bd);
    }

    // Chooses the intra mode with the lowest residual SATD (sum of absolute Hadamard-transformed differences) —
    // a frequency-domain cost proxy that tracks DCT coding cost far better than raw SAD, so smooth ramps and
    // directional edges are scored the way the transform will actually code them. Returns the winning (mode,
    // angle_delta) and writes its prediction into predOut (n x n). Purely an encoder decision: any candidate is
    // a valid mode, so this can never desync the decoder.
    private static (Av1IntraPredMode Mode, int Delta, long Cost) ChooseIntraMode(ushort[] recon, int reconW, int bw4, int bh4,
        int bx4, int by4, int n, ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] predOut,
        Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        long best = long.MaxValue;
        (Av1IntraPredMode Mode, int Delta) bestCand = (Av1IntraPredMode.Dc, 0);
        var tmp = new ushort[n * n];
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
    private static long Satd8x8(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int n)
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

    // Sum of absolute differences over a cn x cn block (pred stride = cn). Works at any size (unlike the 8x8-tiled
    // SATD), so it prescreens chroma UV modes down to 4x4. A coarse proxy — good enough to pick the RD shortlist.
    private static long SadBlock(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int cn)
    {
        long total = 0;
        for (int y = 0; y < cn; y++)
        {
            int row = (srcBy + y) * srcW + srcBx;
            for (int x = 0; x < cn; x++) total += Math.Abs(src[row + x] - pred[y * cn + x]);
        }
        return total;
    }

    // SATD over a w x h rectangular block (pred stride = w). Rect leaf sizes are all multiples of 8, so the 8x8
    // Hadamard tiling is exact. Used to prescreen rect intra modes cheaply before the full RD tx-type search.
    private static long Satd8x8Rect(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int w, int h)
    {
        long total = 0;
        var d = new int[64];
        for (int by = 0; by < h; by += 8)
            for (int bx = 0; bx < w; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        d[y * 8 + x] = src[(srcBy + by + y) * srcW + (srcBx + bx + x)] - pred[(by + y) * w + (bx + x)];
                total += Hadamard8x8Abs(d);
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
    private static int DcPredict(ushort[] recon, int w, int h, int bx, int by, int bw, int bh)
    {
        var tb = TileBounds4(w, int.MaxValue, int.MaxValue);
        bool haveTop = by > tb.Y0 * 4;
        bool haveLeft = bx > tb.X0 * 4;
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

        return PixMid;
    }

    // Dequantizes the quantized levels the way the decoder does, inverse-transforms onto the DC prediction (via
    // the decoder's own InvTxfmAdd) to reconstruct the 64x64 block into `recon`, and returns the coefficient
    // context byte (cul_level | dc-sign) that neighbours read.
    private static byte DequantAndReconstruct(int[] levels, int dcDq, int acDq, int dcPred, ushort[] recon, int w, int bx, int by)
        => DequantAndReconstruct(levels, Tx64x64, 64, dcDq, acDq, dcPred, recon, w, bx, by);

    // Dequantizes quantized levels (decoder-exact), inverse-transforms onto the DC prediction (via the decoder's
    // own InvTxfmAdd) to reconstruct an n x n block of transform <paramref name="tx"/> into <paramref
    // name="recon"/> (stride <paramref name="reconW"/>), and returns the block's coefficient context byte.
    private static byte DequantAndReconstruct(int[] levels, int tx, int n, int dcDq, int acDq, int dcPred,
        ushort[] recon, int reconW, int bx, int by)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        int cfMax = CfMax;
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

        var block = new ushort[n * n];
        Array.Fill(block, (ushort)dcPred);
        Av1InvTransform.InvTxfmAdd16(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], Av1TxType.DctDct, Bd);
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
        ushort[] predBlock, ushort[] recon, int reconW, int bx, int by, Av1TxType txType = Av1TxType.DctDct)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        int cfMax = CfMax;
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

        var block = (ushort[])predBlock.Clone();
        Av1InvTransform.InvTxfmAdd16(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], txType, Bd);
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
    private static long ReconSseCand(int[] levels, int tx, int n, int dcDq, int acDq, ushort[] predBlock,
        ushort[] src, int srcW, int srcBx, int srcBy, Av1TxType txType)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        int cfMax = CfMax;
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
        var block = (ushort[])predBlock.Clone();
        Av1InvTransform.InvTxfmAdd16(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], txType, Bd);
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

    // Forward-transform kernel matching a derived Av1TxType (used for chroma, whose tx-type comes from the UV mode).
    private static Av1FwdTransform.FwdTxType FwdTypeForTxType(Av1TxType t) => t switch
    {
        Av1TxType.Identity => Av1FwdTransform.FwdTxType.Identity,
        Av1TxType.AdstAdst => Av1FwdTransform.FwdTxType.AdstAdst,
        Av1TxType.AdstDct => Av1FwdTransform.FwdTxType.AdstDct,
        Av1TxType.DctAdst => Av1FwdTransform.FwdTxType.DctAdst,
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
        ChooseLeafRdCore(ushort[] recon, int reconW, int bw4, int bh4, ushort[] luma, int lumaW, int bx4, int by4,
            int n, int tx, int dcDq, int acDq, Av1CdfContext cdf, byte aboveMode, byte leftMode,
            ReadOnlySpan<byte> aboveLCoef, ReadOnlySpan<byte> leftLCoef, ushort[] predOut,
            Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, bool filterEligible = false, int bs = 0, bool fullSet = false)
    {
        // Full intra set adds V_DCT/H_DCT for sub-16x16 luma (n in {4,8}); 16x16 and up stay on the reduced set.
        var txSet = (fullSet && n <= 8) ? IntraTxTypesFull : (n <= 16 ? IntraTxTypes : DctOnly);
        int aboveCtx = Av1Tables.IntraModeContext[aboveMode];
        int leftCtx = Av1Tables.IntraModeContext[leftMode];
        Span<ushort> ymCdf = cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, aboveLCoef, leftLCoef);
        int intraFlags = IntraEdgeFlags(aboveMode, leftMode);
        int scanLen = Av1Tables.Scans[tx].Length;
        var predBuf = new ushort[n * n];
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
            if (DbgLumaModeFilter != null && !DbgLumaModeFilter(mode, delta)) continue;
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
            // A DC-coded block also emits use_filter_intra=0 when filter is enabled — charge that bit for fairness.
            if (filterEligible && mode == Av1IntraPredMode.Dc)
                modeBits += Av1CoeffEncode.SymBits(cdf.GetFilterIntraCdf((Av1BlockSize)bs), 0);
            foreach (var (fwd, inv, idx) in txSet)
            {
                int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd, qfCand);
                bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                double rate = (oneD
                    ? Av1CoeffEncode.EstimateCoefBits1D(cdf.Coef, cdf.Mode, tx, (int)mode, inv, cf, 0, dcSignCtx)
                    : Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, (int)mode, cf, 0, dcSignCtx, idx, fullSet: fullSet)) + modeBits;
                long sse = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv);
                double j = sse + rdLambda * rate;   // true RD: distortion + λ·rate (IDTX changes distortion, so rate alone misranks it)
                if (j < best) { best = j; bestCand = (mode, delta, cf, inv, idx); Array.Copy(predBuf, predOut, n * n); Array.Copy(qfCand, qfWin, scanLen); }
            }
        }

        // Filter-intra candidates: the 5 recursive-filter predictors, coded as y_mode=DC + use_filter_intra=1 +
        // filter_mode. The tx-type coefficient context uses FilterModeToYMode (dav1d recon_tmpl); a filter winner
        // sets Mode=Filter and the caller stores DC for the neighbour mode context (dav1d decode.c).
        if (filterEligible)
        {
            Span<ushort> fiCdf = cdf.GetFilterIntraCdf((Av1BlockSize)bs);
            double flagBits = Av1CoeffEncode.SymBits(fiCdf, 1) + Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc);
            for (int fm = 0; fm < 5; fm++)
            {
                PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, Av1IntraPredMode.Filter, fm, predBuf, edgeFlags, intraFlags);
                int[] residual = ComputeResidualPred(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
                int ymnf = Av1Tables.FilterModeToYMode[fm];
                double modeBits = flagBits + Av1CoeffEncode.SymBits(cdf.GetFilterIntraModeCdf(), fm);
                foreach (var (fwd, inv, idx) in txSet)
                {
                    int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd, qfCand);
                    bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                    double rate = (oneD
                        ? Av1CoeffEncode.EstimateCoefBits1D(cdf.Coef, cdf.Mode, tx, ymnf, inv, cf, 0, dcSignCtx)
                        : Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, ymnf, cf, 0, dcSignCtx, idx, fullSet: fullSet)) + modeBits;
                    long sse = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv);
                    double j = sse + rdLambda * rate;
                    if (j < best) { best = j; bestCand = (Av1IntraPredMode.Filter, fm, cf, inv, idx); Array.Copy(predBuf, predOut, n * n); Array.Copy(qfCand, qfWin, scanLen); }
                }
            }
        }

        // RDOQ-refine the winning coefficients (encoder-only; decoder reconstructs from these same levels).
        // Skipped for V_DCT/H_DCT winners: RdoqOptimize assumes the 2D scan/contexts (EncodeCoefs1D codes the
        // 1D scan), so its rate model doesn't apply — the deadzone-quantized 1D levels are coded as-is.
        if (bestCand.Coeffs != null && bestCand.Inv != Av1TxType.VDct && bestCand.Inv != Av1TxType.HDct)
        {
            double lambda = RdoqLambdaScale * RdLambdaK * acDq * acDq;
            int rdoqMode = bestCand.Mode == Av1IntraPredMode.Filter ? Av1Tables.FilterModeToYMode[bestCand.Delta] : (int)bestCand.Mode;
            Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, 0, rdoqMode, bestCand.Coeffs, qfWin,
                dcDq, acDq, 0, dcSignCtx, bestCand.Idx, lambda);
        }

        return bestCand;
    }


    // === Rectangular block helpers (w x h, w != h) — mirror the square versions but keep width/height separate.
    // Used by PARTITION_HORZ / PARTITION_VERT leaves. Prediction/reconstruction reuse the decoder's own
    // PrepareIntraEdges + Av1IntraPred.Predict + InvTxfmAdd, so they are conformant by construction. ===

    // Intra prediction for a w x h block into dst (h rows x w cols, stride w).
    private static void PredictIntraRect(ushort[] recon, int reconW, int bw4, int bh4, int bx4, int by4,
        int w, int h, Av1IntraPredMode mode, int delta, ushort[] dst, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int intraFlags = 0)
    {
        Span<ushort> edge = stackalloc ushort[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = w >> 2, th4 = h >> 2;
        var tb = TileBounds4(reconW, bw4, bh4);
        bool haveTop = by4 > tb.Y0, haveLeft = bx4 > tb.X0;
        int angle = delta;
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, tb.X1, tb.Y1, edgeFlags,
            recon, dstOff, reconW, default, mode, ref angle, tw4, th4,
            filterEdge: (intraFlags & EdgeFilterEnableBit) != 0, edge, edgeCenter, Bd);
        Av1IntraPred.Predict16(m, dst, w, edge, edgeCenter, w, h, angle | intraFlags, 4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4, Bd);
    }

    // Forward+quant of (src - pred) for a w x h block. pred is h x w row-major.
    private static int[] ForwardResidualPredRect(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy,
        ushort[] pred, int w, int h, int txIdx, int dcDq, int acDq, int rcCount, double[]? qfOut = null,
        Av1FwdTransform.FwdTxType fwd = Av1FwdTransform.FwdTxType.DctDct)
    {
        var residual = new int[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                residual[y * w + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * w + x];
        return Av1FwdTransform.ForwardQuantRect(residual, w, h, txIdx, dcDq, acDq, rcCount, fwd, qfOut);
    }

    // Dequantizes rect levels and reconstructs a w x h block onto predBlock (h x w) via the decoder's InvTxfmAdd,
    // into recon. Returns the coefficient-context byte.
    private static byte DequantAndReconstructPredRect(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        ushort[] predBlock, ushort[] recon, int reconW, int bx, int by, Av1TxType txType = Av1TxType.DctDct)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[txIdx].Ctx - 2);
        int cfMax = CfMax;
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

        var block = (ushort[])predBlock.Clone();
        Av1InvTransform.InvTxfmAdd16(block, w, cf, eob, txIdx, Av1InvTransform.TxShift[txIdx], txType, Bd);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                recon[(by + y) * reconW + (bx + x)] = block[y * w + x];

        return cfCtx;
    }

    // Reconstruction SSE of a w x h rectangular candidate (dequant + inverse onto predBlock) vs the source — the
    // distortion term for true-RD rect leaf selection (mirrors ReconSseCand for the square path).
    private static long ReconSseCandRect(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        ushort[] predBlock, ushort[] src, int srcW, int srcBx, int srcBy, Av1TxType txType)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[txIdx].Ctx - 2);
        int cfMax = CfMax;
        var scan = Av1Tables.Scans[txIdx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--) if (levels[scan[i]] != 0) { eob = i; break; }
        var cf = new int[32 * 32];
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i], lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl), sign = lvl < 0 ? 1 : 0;
            int dq = Math.Min(((rc == 0 ? dcDq : acDq) * mag) >> dqShift, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
        }
        var block = (ushort[])predBlock.Clone();
        Av1InvTransform.InvTxfmAdd16(block, w, cf, eob, txIdx, Av1InvTransform.TxShift[txIdx], txType, Bd);
        long sse = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int d = block[y * w + x] - src[(srcBy + y) * srcW + (srcBx + x)]; sse += (long)d * d; }
        return sse;
    }

    // Residual (src - prediction) for an n x n block.
    private static int[] ComputeResidualPred(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int n)
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
        ushort[] predBlock, ushort[] src, int srcW, int srcBx, int srcBy, double rdLambda)
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

    private static int[] ForwardResidualPred(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy,
        ushort[] pred, int n, int dcDq, int acDq, int scanLen)
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
    internal static (byte[] SeqObu, byte[] FrameObu) BuildObus(int width, int height, int baseQIdx, int[]? coeffs,
        Av1ObuWriter.Av1ColorDesc? color = null)
    {
        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                $"Frame {width}x{height} does not map to a single square block (rectangular/multi-block not yet supported).");
        }

        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true, color: color);
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
    internal static byte[] EncodeAvifMonochrome(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new NotSupportedException(
                $"Frame {width}x{height} does not map to a single square block (only near-square sizes with both " +
                "dimensions in 5..8, 9..16, 17..32 or 33..64 are supported).");
        }

        int[]? coeffs = QuantizeBlock(luma, width, height, plan, baseQIdx);
        (byte[] seqObu, byte[] frameObu) = BuildObus(width, height, baseQIdx, coeffs, color);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true, color: color, extras: extras);
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
