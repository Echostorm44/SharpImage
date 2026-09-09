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

        // Build the TX_64X64 coefficient array (rc-indexed) for a single DC coefficient.
        int[]? coeffs = null;
        if (dcLevel > 0)
        {
            coeffs = new int[Av1Tables.Scans[Tx64x64].Length];
            coeffs[0] = dcNegative ? -dcLevel : dcLevel;
        }

        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleSuperblockTile(baseQIdx, coeffs);

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
    private const int Tx64x64 = 4;

    /// <summary>Encodes a real 64x64 monochrome image: DC-predicts the single block (128, no neighbours),
    /// forward-transforms and quantizes the residual, and codes the coefficients. Lossy — reconstruction fidelity
    /// depends on <paramref name="baseQIdx"/>. Returns the AV1 temporal unit.</summary>
    internal static byte[] EncodeMonochromeImage64(ReadOnlySpan<byte> pixels, int baseQIdx)
        => EncodeMonochromeWithCoeffs(64, 64, baseQIdx, QuantizeImage64(pixels, baseQIdx));

    private static byte[] EncodeSingleSuperblockTile(int baseQIdx, int[]? coeffs)
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
        int skip = coeffs == null ? 1 : 0;
        w.EncodeBool((uint)skip, cdf.GetSkipCdf(0)[0]);

        // Keyframe Y mode, contexts from neighbour modes (DC ⇒ IntraModeContext[DC]=0 both). DC_PRED = 0.
        // Decoder: DecodeSymbolAdapt16(kfYModeCdf(0,0), NumIntraPredModes-1 = 12).
        w.EncodeSymbol(cdf.GetKfYModeCdf(0, 0), 0, 12);

        if (skip == 0)
        {
            // Single 64x64 luma transform (TX_64X64, DctDct). First block ⇒ skip/dc-sign contexts are 0.
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, Tx64x64, chroma: 0, coeffs!);
        }

        return w.Finish();
    }

    /// <summary>Builds the sequence-header OBU and the OBU_FRAME (frame header + tile) for a monochrome key frame
    /// carrying the given coefficients (null ⇒ skip). The two OBUs are the building blocks for both a raw
    /// temporal unit and an AVIF container (seq OBU → av1C configOBUs, frame OBU → mdat).</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildObus(int width, int height, int baseQIdx, int[]? coeffs)
    {
        if (width < 1 || width > 64 || height < 1 || height > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Single 64x64 superblock only (1..64).");
        }

        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleSuperblockTile(baseQIdx, coeffs);

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

    /// <summary>Encodes a real 64x64 monochrome image as a complete .avif file. Lossy — fidelity depends on
    /// <paramref name="baseQIdx"/>.</summary>
    internal static byte[] EncodeAvifMonochrome64(ReadOnlySpan<byte> pixels, int baseQIdx)
        => EncodeAvifMonochrome(pixels, 64, 64, baseQIdx);

    /// <summary>Encodes a monochrome image of <paramref name="width"/>x<paramref name="height"/> (each in 33..64
    /// — one 64x64 superblock coded as PARTITION_NONE; dims ≤32 would force a partition split we don't yet emit)
    /// as a complete .avif. <paramref name="block64"/> is the full 64x64 luma superblock (frame content in the
    /// top-left, edge-replicated beyond the frame).</summary>
    internal static byte[] EncodeAvifMonochrome(ReadOnlySpan<byte> block64, int width, int height, int baseQIdx)
    {
        if (width < 33 || width > 64 || height < 33 || height > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                $"Single-superblock AVIF encode requires width and height in 33..64 (got {width}x{height}).");
        }

        int[]? coeffs = QuantizeImage64(block64, baseQIdx);
        (byte[] seqObu, byte[] frameObu) = BuildObus(width, height, baseQIdx, coeffs);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true);
    }

    /// <summary>Forward-transforms and quantizes a 64x64 monochrome image (DC prediction = 128) into the
    /// coefficient array, or null when quantization zeroes everything (⇒ skip / flat plane).</summary>
    private static int[]? QuantizeImage64(ReadOnlySpan<byte> pixels, int baseQIdx)
    {
        const int n = 64;
        int dcDq = Av1Tables.DequantTable[0, baseQIdx, 0];
        int acDq = Av1Tables.DequantTable[0, baseQIdx, 1];

        var residual = new int[n * n];
        for (int i = 0; i < n * n; i++)
        {
            residual[i] = pixels[i] - 128; // DC prediction for the first block (no neighbours)
        }

        int[] coeffs = Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, Av1Tables.Scans[Tx64x64].Length);
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
