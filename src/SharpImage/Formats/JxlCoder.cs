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
// ENCODE: NOT IMPLEMENTED. A JPEG XL encoder (Modular entropy coding / VarDCT) is a separate, large
// piece of work; rather than emit a non-standard bitstream that only this library could read, the
// encode entry points throw NotSupportedException.

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

    /// <summary>JPEG XL encoding is not implemented; a real encoder is future work.</summary>
    public static byte[] Encode(ImageFrame image)
        => throw new NotSupportedException("JPEG XL encoding is not implemented. SharpImage can decode lossless Modular JPEG XL; encoding is future work.");

    /// <summary>JPEG XL (lossy/VarDCT) encoding is not implemented.</summary>
    public static byte[] EncodeLossy(ImageFrame image, int quality = 75)
        => throw new NotSupportedException("JPEG XL encoding is not implemented. SharpImage can decode lossless Modular JPEG XL; encoding is future work.");

    private static ImageFrame BuildFrame(Jxl.JxlModularResult r)
    {
        int w = r.Width;
        int h = r.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, r.HasAlpha);
        int frameChannels = frame.NumberOfChannels;
        int nb = r.NumChannels;

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int pix = (y * w) + x;
                int off = x * frameChannels;
                if (nb == 1)
                {
                    ushort g = Quantum.ScaleFromByte((byte)Math.Clamp(r.Channels[0].Px[pix], 0, 255));
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
                        row[off + c] = Quantum.ScaleFromByte((byte)Math.Clamp(r.Channels[c].Px[pix], 0, 255));
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
