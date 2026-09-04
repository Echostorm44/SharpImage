// JPEG XL (JXL) format coder.
//
// DECODE: a real, from-scratch pure-C# JPEG XL decoder for the lossless Modular sub-codec
// (ISO/IEC 18181). Codestream signature 0xFF0A, or the ISOBMFF-style container with a jxlc/jxlp
// box. The decode path lives in the Formats.Jxl namespace: JxlBitReader (LSB-first field reader),
// JxlEntropy/JxlAnsReader/JxlHuffman (ANS + prefix entropy, hybrid-uint, LZ77, context maps),
// JxlModular (MA decision tree, self-correcting weighted + gradient predictors, inverse RCT and
// Palette transforms) and JxlFrame (SizeHeader/ImageMetadata + frame-body location). It is validated
// bit-exactly against libjxl-produced lossless files (see SharpImage.Tests). VarDCT (lossy) frames
// are detected and rejected — not yet implemented.
//
// ENCODE: a real, from-scratch pure-C# lossless encoder (see Formats.Jxl.JxlEncoder). It emits a
// standard bare codestream — 8-bit sRGB, a Modular frame with the YCoCg RCT, the self-correcting
// weighted predictor and prefix (Huffman) entropy coding, tiled into groups for large images. Output
// is verified pixel-exact against the reference decoders (libjxl and jxl-oxide), not just round-tripped.
// Lossy VarDCT encoding is not yet implemented; EncodeLossy falls back to lossless.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class JxlCoder
{
    // Container signature (ISOBMFF-style).
    private static ReadOnlySpan<byte> ContainerSignature => [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        if (data[0] == 0xFF && data[1] == 0x0A)
        {
            return true; // bare codestream
        }

        return data.Length >= 12 && data[..12].SequenceEqual(ContainerSignature);
    }

    public static ImageFrame Decode(byte[] data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid JPEG XL file");
        }

        byte[] cs = FindCodestream(data);
        Jxl.JxlModularResult result = Jxl.JxlFrame.DecodeModularCodestream(cs);
        return BuildFrame(result);
    }

    /// <summary>
    /// Encodes an image as a lossless JPEG XL codestream (Modular mode: clamped-gradient prediction +
    /// prefix entropy coding). Produces a standard bare codestream that libjxl / jxl-oxide decode.
    /// </summary>
    public static byte[] Encode(ImageFrame image) => Jxl.JxlEncoder.EncodeLossless(image);

    /// <summary>
    /// Encodes an animated image sequence as a single multi-frame lossless JPEG XL codestream. Each frame
    /// carries its own duration (from <see cref="ImageFrame.Delay"/>, centiseconds); the metadata holds the
    /// loop count. A one-frame sequence produces an ordinary still image.
    /// </summary>
    public static byte[] EncodeAnimation(ImageSequence sequence) => Jxl.JxlEncoder.EncodeSequence(sequence);

    /// <summary>Decodes a (possibly animated) JPEG XL codestream into an image sequence, one frame per JXL
    /// frame with its per-frame <see cref="ImageFrame.Delay"/> (centiseconds) and the loop count.</summary>
    public static ImageSequence DecodeAnimation(byte[] data)
    {
        byte[] cs = FindCodestream(data);
        (var frames, int numLoops, uint tpsNum, uint tpsDenom) = Jxl.JxlFrame.DecodeSequence(cs);
        var seq = new ImageSequence { LoopCount = numLoops };
        foreach ((Jxl.JxlModularResult res, int durationTicks) in frames)
        {
            ImageFrame frame = BuildFrame(res);
            frame.Delay = tpsNum > 0 ? (int)((long)durationTicks * 100 * tpsDenom / tpsNum) : durationTicks;
            seq.AddFrame(frame);
        }

        return seq;
    }

    /// <summary>
    /// Encodes an image as a lossy JPEG XL codestream (XYB VarDCT). <paramref name="quality"/> is a
    /// JPEG-style value in [1,100] (higher = better). <paramref name="effort"/> in [1,9] trades speed for
    /// quality/size (1-3 = fast single-pass at any size; 7 = default; higher runs more SSIMULACRA2-guided
    /// block-refinement roundtrips over a larger pixel budget). <paramref name="bits"/> is the output bit
    /// depth (8 default; 16 keeps full precision for a 16-bit source, avoiding 8-bit banding). Any positive
    /// size is supported: the frame is tiled into 2048x2048 LF groups and 256px coding groups.
    /// </summary>
    public static byte[] EncodeLossy(ImageFrame image, int quality = 75, int effort = 7, int bits = 8)
    {
        if (!Jxl.JxlEncoder.CanEncodeVarDct(image))
        {
            return Jxl.JxlEncoder.EncodeLossless(image);
        }

        float distance = Jxl.JxlEncoder.DistanceFromQuality(quality);
        effort = Math.Clamp(effort, 1, 9);

        // 16-bit output (opt-in): the XYB pipeline is float, so this just keeps full input precision + declares
        // 16-bit. Uses the fast single-pass path (the block-refined roundtrip's metric assumes 8-bit).
        if (bits >= 16)
        {
            return Jxl.JxlEncoder.EncodeVarDct(image, distance, null, 16);
        }

        // Wide-gamut (non-sRGB primaries): the XYB transform converts to/from sRGB primaries, but the
        // block-refined roundtrip's SSIMULACRA2 metric assumes an sRGB pixel space, so use single-pass.
        if (image.Colorspace != ColorspaceType.SRGB)
        {
            return Jxl.JxlEncoder.EncodeVarDct(image, distance);
        }

        // Effort/speed dial. The block-refined path runs several encode->decode->SSIMULACRA2 roundtrips to
        // close the coarse-quant "cliff"; its cost scales with pixels x iterations, so higher effort buys
        // more refinement iterations and a larger pixel budget below which it's used (single-pass above it,
        // which is fast at any size). effort<=3 is always the fast single-pass path.
        long pixels = (long)image.Columns * image.Rows;
        long budget = effort switch
        {
            <= 3 => 0,
            4 => 500_000,
            5 => 1_000_000,
            6 => 1_500_000,
            7 => 2_000_000,   // default: block-refined up to ~2 MP (matches the prior default)
            8 => 4_000_000,
            _ => long.MaxValue,
        };

        if (pixels > budget)
        {
            return Jxl.JxlEncoder.EncodeVarDct(image, distance);
        }

        int iters = Math.Clamp(effort - 2, 2, 7); // effort 4 -> 2 refinement iterations, effort 9 -> 7
        return Jxl.JxlEncoder.EncodeVarDctBlockRefined(image, distance, iters);
    }

    /// <summary>
    /// Decodes a possibly-truncated JPEG XL codestream into a best-effort preview. For a progressive /
    /// multi-section VarDCT frame, any section not yet fully arrived is treated as zero (the guarantee is
    /// section-granular — an ANS stream can't be safely decoded from a partial tail), so a prefix renders a
    /// DC-only, then partial-pass, image that sharpens as whole sections arrive, and the complete buffer
    /// reconstructs exactly. The signature, header and TOC must be present. A single-section (non-progressive)
    /// frame can only be previewed once fully received.
    /// </summary>
    public static ImageFrame DecodePreview(byte[] partialData)
    {
        if (partialData.Length < 2 || partialData[0] != 0xFF || partialData[1] != 0x0A)
        {
            // Only bare codestreams (0xFF 0x0A) are supported for preview; a truncated container box header
            // may not even locate the codestream.
            throw new InvalidDataException("Preview decode requires a bare JPEG XL codestream (0xFF 0x0A).");
        }

        Jxl.JxlModularResult result = Jxl.JxlFrame.DecodeModularCodestream(partialData, allowTruncated: true);
        return BuildFrame(result);
    }

    private static ImageFrame BuildFrame(Jxl.JxlModularResult r)
    {
        int w = r.Width;
        int h = r.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, r.Colorspace, r.HasAlpha);
        frame.IccProfile = r.IccProfile;
        int frameChannels = frame.NumberOfChannels;
        int nb = r.NumChannels;

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int pix = (y * w) + x;
                int off = x * frameChannels;
                int maxV = (1 << r.Bps) - 1;
                if (nb == 1)
                {
                    ushort g = Quantum.ScaleFromDepth((uint)Math.Clamp(r.Channels[0].Px[pix], 0, maxV), r.Bps);
                    row[off] = g;
                    if (frameChannels >= 3)
                    {
                        row[off + 1] = g;
                        row[off + 2] = g;
                    }
                }
                else
                {
                    int m = Math.Min(nb, frameChannels);
                    for (int c = 0; c < m; c++)
                    {
                        row[off + c] = Quantum.ScaleFromDepth((uint)Math.Clamp(r.Channels[c].Px[pix], 0, maxV), r.Bps);
                    }
                }
            }
        }

        return frame;
    }

    private static byte[] FindCodestream(byte[] data)
    {
        if (data[0] == 0xFF && data[1] == 0x0A)
        {
            return data;
        }

        // Parse container boxes to find the jxlc (codestream) box.
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            uint boxLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string boxType = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int headerSize = 8;

            if (boxLen == 1 && pos + 16 <= data.Length)
            {
                headerSize = 16;
                boxLen = (uint)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
            }
            else if (boxLen == 0)
            {
                boxLen = (uint)(data.Length - pos);
            }

            if (boxType == "jxlc" || boxType == "jxlp")
            {
                return data.AsSpan(pos + headerSize, (int)boxLen - headerSize).ToArray();
            }

            pos += (int)boxLen;
        }

        throw new InvalidDataException("No codestream found in JXL container");
    }
}
