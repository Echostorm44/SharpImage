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

        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleSuperblockTile(baseQIdx, dcLevel, dcNegative);

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

    /// <summary>Codes the single-block tile with the same CDFs and contexts the decoder uses. With
    /// disable_cdf_update=1 the decoder reads with static default CDFs and does not adapt, so we use the
    /// non-adaptive Encode* calls against the same default CDF context.</summary>
    private static byte[] EncodeSingleSuperblockTile(int baseQIdx, int dcLevel, bool dcNegative)
    {
        // qcat selects the coefficient CDF set; must match the decoder's derivation from the segment q index.
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);

        var w = new Av1MsacWriter();

        // Partition at BLOCK_64X64 (bl=Bl64x64), ctx=0 (no neighbours; reset_context fills Partition=0).
        // PARTITION_NONE = 0. Decoder: DecodeSymbolAdapt16(partCdf, PartitionTypeCount[Bl64x64]=9).
        w.EncodeSymbol(cdf.GetPartitionCdf(Av1BlockLevel.Bl64x64, 0), 0, 9);

        // Skip flag, ctx=0 (above/left skip = 0). skip=0 ⇒ residual coded; skip=1 ⇒ flat plane.
        // Decoder: DecodeBoolAdapt(Skip[0]). With CDEF disabled, skip=0 reads no CDEF bits (CdefBits=0).
        int skip = dcLevel == 0 ? 1 : 0;
        w.EncodeBool((uint)skip, cdf.GetSkipCdf(0)[0]);

        // Keyframe Y mode, contexts from neighbour modes (DC ⇒ IntraModeContext[DC]=0 both). DC_PRED = 0.
        // Decoder: DecodeSymbolAdapt16(kfYModeCdf(0,0), NumIntraPredModes-1 = 12).
        w.EncodeSymbol(cdf.GetKfYModeCdf(0, 0), 0, 12);

        if (skip == 0)
        {
            EncodeDcOnlyLumaCoeffs(w, cdf.Coef, dcLevel, dcNegative);
        }

        return w.Finish();
    }

    /// <summary>Codes a single-DC-coefficient luma transform block for the 64x64 intra block (TX_64X64, DctDct).
    /// Mirrors Av1CoeffDecode.DecodeCoefs for the eob-bin-0 (DC-only) path. Contexts at the first block: skip
    /// ctx 0 (block size == tx size), dc-sign ctx 0 (neighbour LCoef all 0x40). TX_64X64: Ctx=4, tx2dSzCtx=6
    /// (EobBin1024), eobBaseTokIdx=32.</summary>
    private static void EncodeDcOnlyLumaCoeffs(Av1MsacWriter w, Av1CdfCoefContext coef, int dcLevel, bool dcNegative)
    {
        const int txCtx = 4;              // TxfmDimensions[TX_64X64].Ctx
        const int chroma = 0;
        int cdfIdx = txCtx * 13 + 0;      // skip ctx 0

        // all_zero (txb_skip) = 0 ⇒ block has coefficients. Decoder: DecodeBoolAdapt(CoefSkip[cdfIdx]).
        w.EncodeBool(0, coef.CoefSkip[cdfIdx][0]);

        // Transform type is DctDct with no symbol (tDim.Max + intra >= TX_64X64).

        // EOB bin = 0 ⇒ DC-only (eob position 0). Decoder: DecodeSymbolAdapt16(EobBin1024[chroma], 10).
        w.EncodeSymbol(coef.EobBin1024[chroma], 0, 10);

        // Base token for the DC-only path: tokBr = dcLevel-1 ∈ {0,1}; dcTok = 1+tokBr.
        // Decoder: DecodeSymbolAdapt4(EobBaseTok[eobBaseTokIdx+0], 2). tokBr==2 would trigger HiTok (unsupported).
        int eobBaseTokIdx = txCtx * 2 * 4 + chroma * 4; // = 32
        w.EncodeSymbol(coef.EobBaseTok[eobBaseTokIdx + 0], dcLevel - 1, 2);

        // DC sign. Decoder: DecodeBoolAdapt(DcSign[chroma*3 + dcSignCtx=0]).
        w.EncodeBool(dcNegative ? 1u : 0u, coef.DcSign[chroma * 3 + 0][0]);
    }
}
