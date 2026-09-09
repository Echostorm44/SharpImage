// Lossless JPEG <-> JXL transcoding (JPEG recompression).
//
// A baseline JPEG is parsed into its quantized DCT coefficients + the byte-exact reconstruction data
// (jbrd: verbatim header/trailer, Huffman tables, sampling, restart interval). The coefficients are stored
// losslessly using this repo's JXL Modular encoder (frequency-transposed so each DCT position forms its own
// spatially-coherent sub-image — the DC sub-image is smooth and compresses far better than the raster form),
// and Decode reverses the process to reproduce the ORIGINAL JPEG byte-for-byte via JpegCoder.RebuildJpeg.
//
// This is a self-contained SharpImage container (magic "SJXL"): the coefficients are a real JXL codestream,
// but djxl/jxl-oxide can't reconstruct the JPEG from it — that needs the standard jpeg-mode VarDCT frame +
// jbrd box (a much larger, external-verify-only subsystem). Byte-exactness here is verified end-to-end locally.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

public static class JpegXlLossless
{
    private const uint Magic = 0x534A584C; // "SJXL"

    /// <summary>Detects the SharpImage lossless-JPEG container.</summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    /// <summary>Effort presets for <see cref="Encode(byte[], int)"/> (speed vs compression; all byte-exact).</summary>
    /// <summary>Single-pass mode: fastest (roughly libjxl's speed), at some compression cost.</summary>
    public const int EffortTurbo = Jxl.JxlEncoder.JpegEffortTurbo;
    public const int EffortFast = Jxl.JxlEncoder.JpegEffortFast;
    public const int EffortDefault = Jxl.JxlEncoder.JpegEffortDefault;
    public const int EffortMax = Jxl.JxlEncoder.JpegEffortMax;

    /// <summary>Losslessly transcodes a baseline JPEG to the SharpImage JXL-based recompression container
    /// at the default effort. Use <see cref="Encode(byte[], int)"/> to trade speed against compression.</summary>
    public static byte[] Encode(byte[] jpeg) => Encode(jpeg, EffortDefault);

    /// <summary>Losslessly transcodes a baseline JPEG to the SharpImage JXL-based recompression container.</summary>
    /// <param name="jpeg">The source baseline/progressive JPEG bytes.</param>
    /// <param name="effort">Compression effort 1-9 (see <see cref="EffortFast"/>/<see cref="EffortDefault"/>/
    /// <see cref="EffortMax"/>): higher searches more coding candidates for a smaller container at more CPU.
    /// Every effort level reconstructs the source byte-for-byte.</param>
    public static byte[] Encode(byte[] jpeg, int effort)
    {
        JpegDctData d = JpegCoder.ReadDctData(new MemoryStream(jpeg));

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(BinaryPrimitives.ReverseEndianness(Magic));

        // Header/trailer are the verbatim JPEG marker segments (Huffman + quant tables, APPn) — highly
        // structured and compressible; Brotli them so tiny JPEGs aren't dominated by a raw ~300-500B header.
        // All the geometry/sampling/table-index/block-grid metadata is DERIVABLE from the header, so it is not
        // stored here — Decode re-parses it via JpegCoder.PopulateDctMetadata (saves ~66 B per file).
        WriteCompressedBlob(w, d.HeaderBytes);

        // Trailer is almost always just the 2-byte EOI marker; flag that common case in 1 byte instead of
        // spending ~10 bytes framing a compressed blob (matters for the near-tie tiny files).
        bool bareEoi = d.TrailingBytes.Length == 2 && d.TrailingBytes[0] == 0xFF && d.TrailingBytes[1] == 0xD9;
        w.Write(bareEoi);
        if (!bareEoi)
        {
            WriteCompressedBlob(w, d.TrailingBytes);
        }

        // Progressive scan script: each scan's band + approximation + restart interval, its participating
        // components (with their Huffman selectors), and the verbatim inter-scan bytes preceding its entropy.
        w.Write(d.Progressive);
        if (d.Progressive)
        {
            w.Write(d.Scans.Count);
            foreach (JpegScan s in d.Scans)
            {
                w.Write(s.Ss);
                w.Write(s.Se);
                w.Write(s.Ah);
                w.Write(s.Al);
                w.Write(s.RestartInterval);
                w.Write(s.ComponentIndices.Length);
                for (int i = 0; i < s.ComponentIndices.Length; i++)
                {
                    w.Write(s.ComponentIndices[i]);
                    w.Write(s.CompDcTable[i]);
                    w.Write(s.CompAcTable[i]);
                }

                WriteCompressedBlob(w, s.Prefix);
            }
        }

        // The coefficients are coded with the DCT-aware AC context model (non-zero-count prediction from
        // neighbouring blocks + per-frequency-band contexts) and ANS — the same machinery the lossy VarDCT
        // encoder uses — which beats JPEG's per-block Huffman by exploiting inter-block correlation. It is the
        // last element, so it is written without a length prefix (Decode reads to end of stream).
        byte[] coeff = Jxl.JxlEncoder.EncodeJpegCoefficients(d, effort);
        w.Write(coeff);
        byte[] container = ms.ToArray();

        // Self-certify: only emit a container that reconstructs the source JPEG byte-for-byte. A source this
        // path cannot reproduce (an atypical progressive encoder, say) is refused cleanly rather than corrupted.
        byte[] check = Decode(container);
        if (!check.AsSpan().SequenceEqual(jpeg))
        {
            throw new NotSupportedException("JPEG could not be losslessly recompressed (byte-exact reconstruction failed).");
        }

        return container;
    }

    /// <summary>Reconstructs the exact original JPEG bytes from a SharpImage lossless-JPEG container.</summary>
    public static byte[] Decode(byte[] container)
    {
        var ms = new MemoryStream(container);
        var r = new BinaryReader(ms);
        if (BinaryPrimitives.ReverseEndianness(r.ReadUInt32()) != Magic)
        {
            throw new InvalidDataException("Not a SharpImage lossless-JPEG container.");
        }

        var d = new JpegDctData
        {
            HeaderBytes = ReadCompressedBlob(r),
        };
        d.TrailingBytes = r.ReadBoolean() ? new byte[] { 0xFF, 0xD9 } : ReadCompressedBlob(r);

        // Re-derive geometry / sampling / per-component sampling+table indices / block grid from the header.
        JpegCoder.PopulateDctMetadata(d);

        d.Progressive = r.ReadBoolean();
        if (d.Progressive)
        {
            int scanCount = r.ReadInt32();
            d.Scans = new System.Collections.Generic.List<JpegScan>(scanCount);
            for (int si = 0; si < scanCount; si++)
            {
                var s = new JpegScan
                {
                    Ss = r.ReadInt32(),
                    Se = r.ReadInt32(),
                    Ah = r.ReadInt32(),
                    Al = r.ReadInt32(),
                    RestartInterval = r.ReadInt32(),
                };
                int compCount = r.ReadInt32();
                s.ComponentIndices = new int[compCount];
                s.CompDcTable = new int[compCount];
                s.CompAcTable = new int[compCount];
                for (int i = 0; i < compCount; i++)
                {
                    s.ComponentIndices[i] = r.ReadInt32();
                    s.CompDcTable[i] = r.ReadInt32();
                    s.CompAcTable[i] = r.ReadInt32();
                }

                s.Prefix = ReadCompressedBlob(r);
                d.Scans.Add(s);
            }
        }

        byte[] coeff = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position)); // rest of the stream
        Jxl.JxlEncoder.DecodeJpegCoefficients(coeff, d);
        return JpegCoder.RebuildJpeg(d);
    }

    // LEB128 varint — lengths are small (headers ~300 B, prefixes a few KB), so 1-2 bytes instead of 4.
    private static void WriteVarint(BinaryWriter w, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            w.Write((byte)(v | 0x80));
            v >>= 7;
        }

        w.Write((byte)v);
    }

    private static int ReadVarint(BinaryReader r)
    {
        int v = 0, shift = 0;
        while (true)
        {
            byte b = r.ReadByte();
            v |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }

            shift += 7;
        }
    }

    private static void WriteBlob(BinaryWriter w, byte[] data)
    {
        WriteVarint(w, data.Length);
        w.Write(data);
    }

    private static byte[] ReadBlob(BinaryReader r) => r.ReadBytes(ReadVarint(r));

    // Brotli-compressed blob: [flag][origLen][compLen][payload] for brotli, or [flag=0][len][data] raw when
    // compression doesn't help — all lengths varint-coded.
    private static void WriteCompressedBlob(BinaryWriter w, byte[] data)
    {
        var buf = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(data.Length)];
        bool ok = System.IO.Compression.BrotliEncoder.TryCompress(data, buf, out int written, quality: 11, window: 22);
        if (ok && written < data.Length)
        {
            w.Write((byte)1); // brotli
            WriteVarint(w, data.Length);
            WriteVarint(w, written);
            w.Write(buf, 0, written);
        }
        else
        {
            w.Write((byte)0); // raw
            WriteVarint(w, data.Length);
            w.Write(data);
        }
    }

    private static byte[] ReadCompressedBlob(BinaryReader r)
    {
        byte flag = r.ReadByte();
        if (flag == 0)
        {
            return r.ReadBytes(ReadVarint(r)); // stored raw
        }

        int origLen = ReadVarint(r);
        int payloadLen = ReadVarint(r);
        byte[] payload = r.ReadBytes(payloadLen);
        var outBuf = new byte[origLen];
        System.IO.Compression.BrotliDecoder.TryDecompress(payload, outBuf, out int decoded);
        if (decoded != origLen)
        {
            throw new InvalidDataException("Corrupt compressed blob in SharpImage lossless-JPEG container.");
        }

        return outBuf;
    }
}
