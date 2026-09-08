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

        // The coefficients are coded with the DCT-aware AC context model (non-zero-count prediction from
        // neighbouring blocks + per-frequency-band contexts) and ANS — the same machinery the lossy VarDCT
        // encoder uses — which beats JPEG's per-block Huffman by exploiting inter-block correlation.
        WriteBlob(w, Jxl.JxlEncoder.EncodeJpegCoefficients(d));
        return ms.ToArray();
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
