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

    /// <summary>Losslessly transcodes a baseline JPEG to the SharpImage JXL-based recompression container.</summary>
    public static byte[] Encode(byte[] jpeg)
    {
        JpegDctData d = JpegCoder.ReadDctData(new MemoryStream(jpeg));

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(BinaryPrimitives.ReverseEndianness(Magic));

        WriteBlob(w, d.HeaderBytes);
        WriteBlob(w, d.TrailingBytes);
        w.Write(d.Width);
        w.Write(d.Height);
        w.Write(d.MaxHSample);
        w.Write(d.MaxVSample);
        w.Write(d.RestartInterval);
        w.Write(d.ComponentCount);

        foreach (JpegDctComponent c in d.Components)
        {
            w.Write((byte)c.Id);
            w.Write((byte)c.HSample);
            w.Write((byte)c.VSample);
            w.Write((byte)c.QuantTableIndex);
            w.Write((byte)c.DcTableIndex);
            w.Write((byte)c.AcTableIndex);
            w.Write(c.BlocksPerRow);
            w.Write(c.BlocksPerCol);
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

                WriteBlob(w, s.Prefix);
            }
        }

        // The coefficients are coded with the DCT-aware AC context model (non-zero-count prediction from
        // neighbouring blocks + per-frequency-band contexts) and ANS — the same machinery the lossy VarDCT
        // encoder uses — which beats JPEG's per-block Huffman by exploiting inter-block correlation.
        WriteBlob(w, Jxl.JxlEncoder.EncodeJpegCoefficients(d));
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
            HeaderBytes = ReadBlob(r),
            TrailingBytes = ReadBlob(r),
        };
        d.Width = r.ReadInt32();
        d.Height = r.ReadInt32();
        d.MaxHSample = r.ReadInt32();
        d.MaxVSample = r.ReadInt32();
        d.RestartInterval = r.ReadInt32();
        d.ComponentCount = r.ReadInt32();
        d.Components = new JpegDctComponent[d.ComponentCount];

        for (int i = 0; i < d.ComponentCount; i++)
        {
            d.Components[i] = new JpegDctComponent
            {
                Id = r.ReadByte(),
                HSample = r.ReadByte(),
                VSample = r.ReadByte(),
                QuantTableIndex = r.ReadByte(),
                DcTableIndex = r.ReadByte(),
                AcTableIndex = r.ReadByte(),
                BlocksPerRow = r.ReadInt32(),
                BlocksPerCol = r.ReadInt32(),
            };
        }

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

                s.Prefix = ReadBlob(r);
                d.Scans.Add(s);
            }
        }

        Jxl.JxlEncoder.DecodeJpegCoefficients(ReadBlob(r), d);
        return JpegCoder.RebuildJpeg(d);
    }

    private static void WriteBlob(BinaryWriter w, byte[] data)
    {
        w.Write(data.Length);
        w.Write(data);
    }

    private static byte[] ReadBlob(BinaryReader r)
    {
        int len = r.ReadInt32();
        return r.ReadBytes(len);
    }
}
