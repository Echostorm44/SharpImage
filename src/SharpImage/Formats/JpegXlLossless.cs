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
    private const int CoeffOffset = 32768;  // map signed coefficients into the 16-bit unsigned range

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

        // All components' coefficients share ONE JXL codestream (avoids per-component metadata/tree/histogram
        // overhead, which dominates small chroma planes): each is frequency-transposed and stacked vertically,
        // padded to the widest component's block width.
        WriteBlob(w, Jxl.JxlEncoder.EncodeLossless(PackAll(d)));
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

        UnpackAll(d, JxlCoder.Decode(ReadBlob(r)));
        return JpegCoder.RebuildJpeg(d);
    }

    // Packs all components' coefficients into ONE 16-bit grayscale image. Each component is frequency-
    // transposed (DCT position k occupies rows [k*bpc, (k+1)*bpc), so position 0 (DC) is a smooth low-res
    // image and the sparse high positions cluster together) and the components are stacked vertically, padded
    // to the widest component's block width (the pad is the zero-coefficient value, ~free to compress).
    private static ImageFrame PackAll(JpegDctData d)
    {
        int maxBpr = 0, totalRows = 0;
        foreach (JpegDctComponent c in d.Components)
        {
            maxBpr = Math.Max(maxBpr, c.BlocksPerRow);
            totalRows += c.BlocksPerCol * 64;
        }

        var img = new ImageFrame();
        img.Initialize(maxBpr, totalRows, ColorspaceType.Gray, false);
        int nch = img.NumberOfChannels;
        int rowBase = 0;
        foreach (JpegDctComponent c in d.Components)
        {
            int bpr = c.BlocksPerRow, bpc = c.BlocksPerCol;
            for (int k = 0; k < 64; k++)
            {
                for (int by = 0; by < bpc; by++)
                {
                    var row = img.GetPixelRowForWrite(rowBase + (k * bpc) + by);
                    int baseIdx = by * bpr;
                    for (int bx = 0; bx < maxBpr; bx++)
                    {
                        int coeff = bx < bpr ? c.Blocks[baseIdx + bx][k] : 0; // pad wider region with 0
                        var v = (ushort)(coeff + CoeffOffset);
                        int o = bx * nch;
                        for (int ch = 0; ch < nch; ch++)
                        {
                            row[o + ch] = v; // fill all channels so the frame stays truly grayscale
                        }
                    }
                }
            }

            rowBase += bpc * 64;
        }

        return img;
    }

    private static void UnpackAll(JpegDctData d, ImageFrame img)
    {
        int nch = img.NumberOfChannels;
        int rowBase = 0;
        foreach (JpegDctComponent c in d.Components)
        {
            int bpr = c.BlocksPerRow, bpc = c.BlocksPerCol;
            c.Blocks = new int[bpr * bpc][];
            for (int i = 0; i < c.Blocks.Length; i++)
            {
                c.Blocks[i] = new int[64];
            }

            for (int k = 0; k < 64; k++)
            {
                for (int by = 0; by < bpc; by++)
                {
                    var row = img.GetPixelRow(rowBase + (k * bpc) + by);
                    int baseIdx = by * bpr;
                    for (int bx = 0; bx < bpr; bx++)
                    {
                        c.Blocks[baseIdx + bx][k] = row[bx * nch] - CoeffOffset;
                    }
                }
            }

            rowBase += bpc * 64;
        }
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
