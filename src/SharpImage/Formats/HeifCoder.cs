// AVIF/HEIC format coder — read and write.
// Pure C# implementation of AVIF (AV1 Still Image) and HEIC (HEVC Still Image).
// Uses ISOBMFF (ISO Base Media File Format) container with intra-frame encoding.
// AVIF: ftyp=avif/avis, coding=av01 (AV1 intra)
// HEIC: ftyp=heic/heix, coding=hvc1 (HEVC intra)
// Reference: ISO/IEC 14496-12 (ISOBMFF), AOM AV1 spec, ImageMagick coders/heic.c

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Text;

namespace SharpImage.Formats;

/// <summary>
/// Distinguishes AVIF from HEIC container type.
/// </summary>
public enum HeifContainerType
{
    Avif,
    Heic
}

public static class HeifCoder
{
    // AVIF ftypes
    private static readonly string[] AvifBrands = [ "avif", "avis", "avio" ];
    // HEIC ftypes
    private static readonly string[] HeicBrands = [ "heic", "heix", "hevc", "hevx", "heim", "heis" ];

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data[4..8]) != "ftyp")
        {
            return false;
        }

        // The major brand of a real HEIC/AVIF is often the generic HEIF brand 'mif1'/'msf1',
        // with 'heic'/'avif' listed only among the compatible brands — so check every brand
        // in the ftyp box, and accept the generic HEIF brands too.
        foreach (string brand in FtypBrands(data))
        {
            if (IsAvifBrand(brand) || IsHeicBrand(brand) || brand is "mif1" or "msf1" or "mif1")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAvif(ReadOnlySpan<byte> data)
    {
        // An AVIF carries AV1 configuration ('av1C'); a HEIC carries 'hvcC'. Prefer that over
        // the brand, since the major brand is frequently the generic 'mif1'.
        if (ContainsFourCc(data, "av1C"))
        {
            return true;
        }

        if (ContainsFourCc(data, "hvcC"))
        {
            return false;
        }

        foreach (string brand in FtypBrands(data))
        {
            if (IsAvifBrand(brand))
            {
                return true;
            }
        }

        return false;
    }

    // Enumerates the major + compatible brands in the ftyp box.
    private static System.Collections.Generic.IEnumerable<string> FtypBrands(ReadOnlySpan<byte> data)
    {
        var brands = new System.Collections.Generic.List<string>();
        if (data.Length < 16 || Encoding.ASCII.GetString(data[4..8]) != "ftyp")
        {
            return brands;
        }

        int boxSize = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        int end = Math.Min(boxSize, data.Length);
        brands.Add(Encoding.ASCII.GetString(data[8..12]).TrimEnd('\0')); // major brand
        for (int p = 16; p + 4 <= end; p += 4)                            // compatible brands
        {
            brands.Add(Encoding.ASCII.GetString(data[p..(p + 4)]).TrimEnd('\0'));
        }

        return brands;
    }

    private static bool ContainsFourCc(ReadOnlySpan<byte> data, string fourcc)
    {
        byte a = (byte)fourcc[0], b = (byte)fourcc[1], c = (byte)fourcc[2], d = (byte)fourcc[3];
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d)
            {
                return true;
            }
        }

        return false;
    }

    public static ImageFrame Decode(byte[] data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid AVIF/HEIC file");
        }

        bool isAvif = IsAvif(data);

        // Parse ISOBMFF boxes
        var boxes = ParseBoxes(data, 0, data.Length);

        // Find meta box for item info
        int primaryItemId = 1;
        int imageWidth = 0, imageHeight = 0;
        int itemDataOffset = -1, itemDataLength = 0;

        // Parse meta box hierarchy
        if (boxes.TryGetValue("meta", out var metaBox))
        {
            int metaStart = metaBox.DataOffset;
            // Skip version + flags (4 bytes) in meta box
            var metaChildren = ParseBoxes(data, metaStart + 4, metaBox.DataLength - 4);

            // Primary item reference
            if (metaChildren.TryGetValue("pitm", out var pitmBox))
            {
                int pitmPos = pitmBox.DataOffset;
                byte version = data[pitmPos];
                if (version == 0)
                {
                    primaryItemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pitmPos + 4));
                }
                else
                {
                    primaryItemId = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pitmPos + 4));
                }
            }

            // Image spatial extents from item properties
            if (metaChildren.TryGetValue("iprp", out var iprpBox))
            {
                var iprpChildren = ParseBoxes(data, iprpBox.DataOffset, iprpBox.DataLength);
                if (iprpChildren.TryGetValue("ipco", out var ipcoBox))
                {
                    // Scan for ispe (image spatial extents)
                    int scanPos = ipcoBox.DataOffset;
                    int scanEnd = scanPos + ipcoBox.DataLength;
                    while (scanPos + 8 <= scanEnd)
                    {
                        uint sLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(scanPos));
                        string sType = Encoding.ASCII.GetString(data, scanPos + 4, 4);
                        if (sType == "ispe" && scanPos + 16 <= scanEnd)
                        {
                            // version(4) + width(4) + height(4)
                            imageWidth = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(scanPos + 12));
                            imageHeight = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(scanPos + 16));
                            break;
                        }
                        scanPos += (int)(sLen > 0 ? sLen : 8);
                    }
                }
            }

            // Item location (iloc)
            if (metaChildren.TryGetValue("iloc", out var ilocBox))
            {
                int ilocPos = ilocBox.DataOffset;
                byte ilocVersion = data[ilocPos];
                int offsetSize = (data[ilocPos + 4] >> 4) & 0xF;
                int lengthSize = data[ilocPos + 4] & 0xF;
                int baseOffsetSize = (data[ilocPos + 5] >> 4) & 0xF;
                int indexSize = ilocVersion >= 1 ? (data[ilocPos + 5] & 0xF) : 0;

                int itemCount;
                int itemPos;
                if (ilocVersion < 2)
                {
                    itemCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(ilocPos + 6));
                    itemPos = ilocPos + 8;
                }
                else
                {
                    itemCount = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(ilocPos + 6));
                    itemPos = ilocPos + 10;
                }

                for (int i = 0;i < itemCount && itemPos < data.Length;i++)
                {
                    int itemId = ilocVersion < 2
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(itemPos))
                        : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(itemPos));
                    itemPos += ilocVersion < 2 ? 2 : 4;

                    if (ilocVersion >= 1)
                    {
                        itemPos += 2; // construction_method
                    }
                    itemPos += 2; // data_reference_index

                    long baseOffset = ReadVarInt(data, itemPos, baseOffsetSize);
                    itemPos += baseOffsetSize;

                    int extentCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(itemPos));
                    itemPos += 2;

                    for (int e = 0;e < extentCount;e++)
                    {
                        if (ilocVersion >= 1)
                        {
                            itemPos += indexSize; // extent_index
                        }

                        long extentOffset = ReadVarInt(data, itemPos, offsetSize);
                        itemPos += offsetSize;
                        long extentLength = ReadVarInt(data, itemPos, lengthSize);
                        itemPos += lengthSize;

                        if (itemId == primaryItemId && itemDataOffset < 0)
                        {
                            itemDataOffset = (int)(baseOffset + extentOffset);
                            itemDataLength = (int)extentLength;
                        }
                    }
                }
            }
        }

        // Fallback: find mdat box for raw pixel data
        if (itemDataOffset < 0 && boxes.TryGetValue("mdat", out var mdatBox))
        {
            itemDataOffset = mdatBox.DataOffset;
            itemDataLength = mdatBox.DataLength;
        }

        if (itemDataOffset < 0 || itemDataLength <= 0)
        {
            throw new InvalidDataException("Could not locate image data in AVIF/HEIC file");
        }

        // Decode the coded image data
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            // Try to infer dimensions from coded data
            if (isAvif)
            {
                InferAv1Dimensions(data.AsSpan(itemDataOffset, Math.Min(itemDataLength, data.Length - itemDataOffset)),
                                out imageWidth, out imageHeight);
            }
            else
            {
                throw new InvalidDataException("Cannot determine image dimensions");
            }
        }

        var frame = new ImageFrame();
        frame.Initialize(imageWidth, imageHeight, ColorspaceType.SRGB, false);

        // Colour matrix + range from the nclx colour box (defaults: BT.601, full range).
        ParseNclxColour(data, out int matrixCoeffs, out bool fullRange);

        if (isAvif)
        {
            DecodeAv1IntraFrame(data.AsSpan(itemDataOffset, Math.Min(itemDataLength, data.Length - itemDataOffset)),
                        frame, matrixCoeffs, fullRange);
        }
        else
        {
            byte[] hvcC = FindConfigBox(data, "hvcC");
            DecodeHevcIntraFrame(data.AsSpan(itemDataOffset, Math.Min(itemDataLength, data.Length - itemDataOffset)),
                        frame, hvcC, matrixCoeffs, fullRange);
        }

        return frame;
    }

    // Finds a codec configuration box (e.g. 'hvcC') in the ISOBMFF stream and returns its
    // payload (the decoder configuration record). Returns an empty array if not present.
    private static byte[] FindConfigBox(byte[] data, string fourcc)
    {
        byte a = (byte)fourcc[0], b = (byte)fourcc[1], c = (byte)fourcc[2], d = (byte)fourcc[3];
        for (int i = 4; i + 4 <= data.Length; i++)
        {
            if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d)
            {
                int boxStart = i - 4;
                int boxSize = (data[boxStart] << 24) | (data[boxStart + 1] << 16) | (data[boxStart + 2] << 8) | data[boxStart + 3];
                int payloadStart = i + 4;
                int payloadLen = boxSize - 8;
                if (payloadLen > 0 && payloadStart + payloadLen <= data.Length)
                {
                    return data[payloadStart..(payloadStart + payloadLen)];
                }
            }
        }

        return [];
    }

    // Reads the colour matrix coefficients + full-range flag from the ISOBMFF 'colr'/'nclx'
    // box so YUV→RGB uses the right matrix. Defaults to BT.601 full range when absent.
    private static void ParseNclxColour(byte[] data, out int matrixCoeffs, out bool fullRange)
    {
        // Defaults when no colour box is present: BT.709, limited (video) range — the
        // convention decoders assume for HEVC/HEIC with unspecified colour.
        matrixCoeffs = 1;
        fullRange = false;
        for (int i = 0; i + 19 < data.Length; i++)
        {
            if (data[i] == 'c' && data[i + 1] == 'o' && data[i + 2] == 'l' && data[i + 3] == 'r'
                && data[i + 4] == 'n' && data[i + 5] == 'c' && data[i + 6] == 'l' && data[i + 7] == 'x')
            {
                // colour_primaries(2) transfer(2) matrix(2) full_range_flag(1 bit, high)
                matrixCoeffs = (data[i + 12] << 8) | data[i + 13];
                fullRange = (data[i + 14] & 0x80) != 0;
                return;
            }
        }
    }

    public static byte[] Encode(ImageFrame image, HeifContainerType containerType = HeifContainerType.Avif)
        => Encode(image, containerType, 20);

    /// <summary>
    /// Encodes an image to HEIC (HEVC still image) at the given quantization parameter (0 = highest
    /// quality/largest, ~51 = lowest). AVIF encoding is not supported (no AV1 encoder).
    /// </summary>
    public static byte[] Encode(ImageFrame image, HeifContainerType containerType, int qp)
    {
        if (containerType != HeifContainerType.Heic)
        {
            throw new NotSupportedException(
                "AVIF encoding is not implemented (no AV1 encoder). HEIC encoding and AVIF/HEIC " +
                "decoding are supported.");
        }

        int w = (int)image.Columns;
        int h = (int)image.Rows;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            int ch = image.NumberOfChannels;
            for (int x = 0; x < w; x++)
            {
                int o = x * ch;
                int d = ((y * w) + x) * 3;
                rgb[d] = Quantum.ScaleToByte(row[o]);
                rgb[d + 1] = Quantum.ScaleToByte(ch > 1 ? row[o + 1] : row[o]);
                rgb[d + 2] = Quantum.ScaleToByte(ch > 2 ? row[o + 2] : row[o]);
            }
        }

        return Hevc.HeicEncoder.Encode(rgb, w, h, 3, Math.Clamp(qp, 0, 51), signDataHiding: true);
    }

    #region AV1 Intra Frame Codec

    private static void InferAv1Dimensions(ReadOnlySpan<byte> obu, out int width, out int height)
    {
        width = height = 0;
        // AV1 OBU (Open Bitstream Unit) parsing
        // First OBU should be sequence header
        if (obu.Length < 4)
        {
            return;
        }

        int pos = 0;
        while (pos < obu.Length)
        {
            byte header = obu[pos++];
            int obuType = (header >> 3) & 0xF;
            bool hasSize = (header & 0x02) != 0;
            bool hasExtension = (header & 0x04) != 0;
            if (hasExtension && pos < obu.Length)
            {
                pos++; // skip extension
            }

            int obuSize = 0;
            if (hasSize)
            {
                // LEB128 size
                obuSize = ReadLeb128(obu, ref pos);
            }

            if (obuType == 1) // OBU_SEQUENCE_HEADER
            {
                // Parse sequence header for dimensions
                if (pos + 8 <= obu.Length)
                {
                    // Simplified: read frame width/height from fixed positions
                    var bitReader = new SimpleBitReader(obu[pos..].ToArray());
                    int seqProfile = (int)bitReader.Read(3);
                    bitReader.Read(1); // still_picture
                    bitReader.Read(1); // reduced_still_picture_header

                    // In reduced still picture header mode:
                    bitReader.Read(5); // seq_level_idx
                    int maxFrameWidthMinus1Bits = (int)bitReader.Read(4) + 1;
                    int maxFrameHeightMinus1Bits = (int)bitReader.Read(4) + 1;
                    width = (int)bitReader.Read(maxFrameWidthMinus1Bits) + 1;
                    height = (int)bitReader.Read(maxFrameHeightMinus1Bits) + 1;
                    return;
                }
            }

            if (hasSize)
            {
                pos += obuSize;
            }
            else
            {
                break;
            }
        }
    }

    private static void DecodeAv1IntraFrame(ReadOnlySpan<byte> codedData, ImageFrame frame, int matrixCoeffs, bool fullRange)
    {
        // Decode the AV1 keyframe with the vendored AV1 intra decoder (pixel-exact vs dav1d),
        // then convert its YUV planes to RGB. AVIF stores the whole temporal unit (sequence
        // header + frame OBUs) in the item's coded data.
        var decoder = new Av1.Av1Decoder();
        using var yuv = decoder.Decode(codedData, 0, isKeyframe: true)
            ?? throw new InvalidDataException("AV1 decode produced no frame.");

        int w = (int)frame.Columns;
        int h = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        bool tenBit = yuv.Format is Av1.PixelFormat.Yuv420P10 or Av1.PixelFormat.Yuv420P12;
        int shift = tenBit ? (yuv.Format == Av1.PixelFormat.Yuv420P12 ? 4 : 2) : 0;
        ConvertYuvToRgb(yuv.YPlane.Span, yuv.UPlane.Span, yuv.VPlane.Span, yuv.YStride, yuv.UStride, yuv.VStride,
            frame, w, h, channels, tenBit, shift, matrixCoeffs == 1, fullRange);
    }

    // Converts a decoded 8/10/12-bit planar YUV 4:2:0 frame to RGB, honouring the colour
    // matrix (BT.709 vs BT.601) and range (full vs limited) signalled by the container.
    // Shared by the AVIF (AV1) and HEIC (HEVC) paths.
    private static void ConvertYuvToRgb(ReadOnlySpan<byte> y0, ReadOnlySpan<byte> u0, ReadOnlySpan<byte> v0, int yStride, int uStride, int vStride, ImageFrame frame, int w, int h, int channels, bool tenBit, int shift, bool bt709, bool fullRange)
    {
        int Sample(ReadOnlySpan<byte> plane, int stride, int x, int y)
        {
            if (!tenBit)
            {
                return plane[y * stride + x];
            }

            int idx = (y * stride + x) * 2;
            int v = plane[idx] | (plane[idx + 1] << 8);
            return v >> shift;
        }

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int cy = y >> 1;
            for (int x = 0; x < w; x++)
            {
                int cx = x >> 1;
                int yv = Sample(y0, yStride, x, y);
                int d = Sample(u0, uStride, cx, cy) - 128;
                int e = Sample(v0, vStride, cx, cy) - 128;
                byte r, g, b;
                if (fullRange)
                {
                    // Full range: no Y offset, 16.16 fixed-point coefficients.
                    (int cr, int cgU, int cgV, int cb) = bt709
                        ? (103206, 12276, 30679, 121609)   // BT.709
                        : (91881, 22554, 46802, 116130);   // BT.601 (JPEG)
                    r = ClampByte(yv + ((cr * e + 32768) >> 16));
                    g = ClampByte(yv - ((cgU * d + cgV * e + 32768) >> 16));
                    b = ClampByte(yv + ((cb * d + 32768) >> 16));
                }
                else
                {
                    // Limited (video) range: Y scaled by 1.164 from 16, 8.8 fixed-point.
                    int c = 298 * (yv - 16);
                    (int cr, int cgU, int cgV, int cb) = bt709
                        ? (459, 55, 136, 541)              // BT.709
                        : (409, 100, 208, 516);            // BT.601
                    r = ClampByte((c + cr * e + 128) >> 8);
                    g = ClampByte((c - cgU * d - cgV * e + 128) >> 8);
                    b = ClampByte((c + cb * d + 128) >> 8);
                }

                int off = x * channels;
                row[off] = Quantum.ScaleFromByte(r);
                if (channels > 1)
                {
                    row[off + 1] = Quantum.ScaleFromByte(g);
                }

                if (channels > 2)
                {
                    row[off + 2] = Quantum.ScaleFromByte(b);
                }
            }
        }
    }

    private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static byte[] EncodeAv1IntraFrame(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;

        var obus = new List<byte>();

        // OBU: Sequence Header
        var seqHeader = new List<byte>();
        var shBits = new SimpleBitWriter();
        shBits.Write(3, 0); // seq_profile = 0 (main)
        shBits.Write(1, 1); // still_picture = true
        shBits.Write(1, 1); // reduced_still_picture_header = true
        shBits.Write(5, 0); // seq_level_idx = 0

        int wBits = BitsNeeded(w - 1);
        int hBits = BitsNeeded(h - 1);
        shBits.Write(4, (uint)(wBits - 1));
        shBits.Write(4, (uint)(hBits - 1));
        shBits.Write(wBits, (uint)(w - 1));
        shBits.Write(hBits, (uint)(h - 1));

        shBits.Write(1, 0); // use_128_intra_default = false
        shBits.Write(1, 0); // enable_filter_intra = false
        shBits.Write(1, 0); // enable_intra_edge_filter = false
        shBits.Write(1, 0); // enable_superres = false
        shBits.Write(1, 0); // enable_cdef = false
        shBits.Write(1, 0); // enable_restoration = false
        // Color config
        shBits.Write(1, 0); // high_bitdepth = false (8-bit)
        shBits.Write(1, 0); // mono_chrome = false
        shBits.Write(1, 0); // color_description_present = false
        shBits.Write(1, 0); // color_range = studio
        shBits.Write(2, 0); // subsampling_x, subsampling_y = 0,0 (4:4:4)
        shBits.Write(1, 0); // film_grain_params_present = false
        shBits.Flush();

        byte[] seqData = shBits.GetBytes();
        WriteObu(obus, 1, seqData); // OBU_SEQUENCE_HEADER

        // OBU: Frame (simplified intra-only with DC prediction)
        int blockW = (w + 7) / 8;
        int blockH = (h + 7) / 8;

        // Convert to YUV and encode DC values per 8x8 block
        var frameBytes = new List<byte>();
        byte[][] blockDc = new byte[3][];
        for (int plane = 0;plane < 3;plane++)
        {
            blockDc[plane] = new byte[blockW * blockH];
        }

        for (int by = 0;by < blockH;by++)
        {
            for (int bx = 0;bx < blockW;bx++)
            {
                double sumY = 0, sumU = 0, sumV = 0;
                int count = 0;
                for (int dy = 0;dy < 8 && by * 8 + dy < h;dy++)
                {
                    var row = image.GetPixelRow(by * 8 + dy);
                    for (int dx = 0;dx < 8 && bx * 8 + dx < w;dx++)
                    {
                        int x = bx * 8 + dx;
                        int off = x * imgChannels;
                        byte r = Quantum.ScaleToByte(row[off]);
                        byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[off + 1]) : r;
                        byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[off + 2]) : r;

                        sumY += 0.299 * r + 0.587 * g + 0.114 * b;
                        sumU += -0.169 * r - 0.331 * g + 0.500 * b + 128;
                        sumV += 0.500 * r - 0.419 * g - 0.081 * b + 128;
                        count++;
                    }
                }
                int idx = by * blockW + bx;
                blockDc[0][idx] = (byte)Math.Clamp(sumY / count, 0, 255);
                blockDc[1][idx] = (byte)Math.Clamp(sumU / count, 0, 255);
                blockDc[2][idx] = (byte)Math.Clamp(sumV / count, 0, 255);
            }
        }

        for (int plane = 0;plane < 3;plane++)
        {
            frameBytes.AddRange(blockDc[plane]);
        }

        WriteObu(obus, 6, frameBytes.ToArray()); // OBU_FRAME

        return obus.ToArray();
    }

    #endregion

    #region HEVC Intra Frame Codec

    private static void DecodeHevcIntraFrame(ReadOnlySpan<byte> codedData, ImageFrame frame, byte[] hvcC, int matrixCoeffs, bool fullRange)
    {
        // Decode the HEVC keyframe with the vendored HEVC decoder. HEIC keeps the parameter
        // sets (VPS/SPS/PPS) in the hvcC configuration box and the coded slice NALs in the
        // item's data, so configure from hvcC first, then decode the slice.
        var decoder = new Hevc.HevcDecoder();
        decoder.Initialize(hvcC);
        using var yuv = decoder.Decode(codedData, 0, isKeyframe: true)
            ?? throw new InvalidDataException("HEVC decode produced no frame.");

        int w = (int)frame.Columns;
        int h = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        bool tenBit = yuv.Format is Hevc.PixelFormat.Yuv420P10 or Hevc.PixelFormat.Yuv420P12;
        int shift = tenBit ? (yuv.Format == Hevc.PixelFormat.Yuv420P12 ? 4 : 2) : 0;
        ConvertYuvToRgb(yuv.YPlane.Span, yuv.UPlane.Span, yuv.VPlane.Span, yuv.YStride, yuv.UStride, yuv.VStride,
            frame, w, h, channels, tenBit, shift, matrixCoeffs == 1, fullRange);
    }

    private static byte[] EncodeHevcIntraFrame(ImageFrame image)
    {
        // Simplified HEVC intra encoding: DC-only prediction per 8x8 CTU
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        int blockW = (w + 7) / 8;
        int blockH = (h + 7) / 8;

        var output = new List<byte>();

        // VPS NAL unit (minimal)
        byte[] vps = [ 0x40, 0x01, 0x0C, 0x01, 0xFF, 0xFF, 0x01, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 ];
        WriteNalUnit(output, vps);

        // SPS NAL unit (minimal with dimensions)
        var sps = new List<byte>();
        sps.AddRange(new byte[] { 0x42, 0x01, 0x01 }); // NAL header + profile
        sps.Add(0x01); // general_profile_space
        // Encode width/height in SPS (simplified)
        sps.Add((byte)(w >> 8));
        sps.Add((byte)(w & 0xFF));
        sps.Add((byte)(h >> 8));
        sps.Add((byte)(h & 0xFF));
        WriteNalUnit(output, sps.ToArray());

        // PPS NAL unit (minimal)
        byte[] pps = [ 0x44, 0x01, 0xC0 ];
        WriteNalUnit(output, pps);

        // IDR slice with DC-coded blocks
        var slice = new List<byte>();
        slice.AddRange(new byte[] { 0x26, 0x01 }); // NAL header (IDR_W_RADL)

        // Encode Y, U, V DC blocks
        for (int plane = 0;plane < 3;plane++)
        {
            for (int by = 0;by < blockH;by++)
            {
                for (int bx = 0;bx < blockW;bx++)
                {
                    double sum = 0;
                    int count = 0;
                    for (int dy = 0;dy < 8 && by * 8 + dy < h;dy++)
                    {
                        var row = image.GetPixelRow(by * 8 + dy);
                        for (int dx = 0;dx < 8 && bx * 8 + dx < w;dx++)
                        {
                            int x = bx * 8 + dx;
                            int off = x * imgChannels;
                            byte r = Quantum.ScaleToByte(row[off]);
                            byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[off + 1]) : r;
                            byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[off + 2]) : r;

                            sum += plane switch
                            {
                                0 => 0.299 * r + 0.587 * g + 0.114 * b,
                                1 => -0.169 * r - 0.331 * g + 0.500 * b + 128,
                                _ => 0.500 * r - 0.419 * g - 0.081 * b + 128
                            };
                            count++;
                        }
                    }
                    slice.Add((byte)Math.Clamp(sum / count, 0, 255));
                }
            }
        }

        WriteNalUnit(output, slice.ToArray());

        return output.ToArray();
    }

    #endregion

    #region ISOBMFF Helpers

    private readonly record struct BoxInfo(int DataOffset, int DataLength);

    private static Dictionary<string, BoxInfo> ParseBoxes(byte[] data, int start, int length)
    {
        var result = new Dictionary<string, BoxInfo>();
        int pos = start;
        int end = start + length;

        while (pos + 8 <= end)
        {
            uint boxLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (pos + 4 > data.Length - 4)
            {
                break;
            }

            string boxType = Encoding.ASCII.GetString(data, pos + 4, 4);

            int headerSize = 8;
            long actualLen = boxLen;
            if (boxLen == 1 && pos + 16 <= end)
            {
                actualLen = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                headerSize = 16;
            }
            else if (boxLen == 0)
            {
                actualLen = end - pos;
            }

            if (actualLen < headerSize)
            {
                break;
            }

            result[boxType] = new BoxInfo(pos + headerSize, (int)(actualLen - headerSize));
            pos += (int)actualLen;
        }

        return result;
    }

    private static void WriteFtypBox(List<byte> output, string brand)
    {
        byte[] data = new byte[8];
        Encoding.ASCII.GetBytes(brand, data.AsSpan(0, 4)); // major_brand
        // minor_version = 0
        Encoding.ASCII.GetBytes(brand, data.AsSpan(4, 4)); // compatible_brand
        WriteBox(output, "ftyp", data);
    }

    private static void WriteMetaBox(List<byte> output, int w, int h, int dataLength,
        HeifContainerType containerType)
    {
        var meta = new List<byte>();
        meta.AddRange(new byte[4]); // version + flags

        // hdlr (handler) box
        var hdlr = new List<byte>();
        hdlr.AddRange(new byte[4]); // version + flags
        hdlr.AddRange(new byte[4]); // pre_defined
        hdlr.AddRange(Encoding.ASCII.GetBytes("pict")); // handler_type
        hdlr.AddRange(new byte[12]); // reserved
        hdlr.Add(0); // name (null terminated)
        WriteBoxTo(meta, "hdlr", hdlr.ToArray());

        // pitm (primary item) box
        var pitm = new List<byte>();
        pitm.AddRange(new byte[4]); // version + flags
        pitm.Add(0);
        pitm.Add(1); // item_ID = 1
        WriteBoxTo(meta, "pitm", pitm.ToArray());

        // iprp (item properties) box
        var iprp = new List<byte>();
        var ipco = new List<byte>();

        // ispe (image spatial extents)
        byte[] ispe = new byte[12];
        // version + flags = 0
        BinaryPrimitives.WriteUInt32BigEndian(ispe.AsSpan(4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ispe.AsSpan(8), (uint)h);
        WriteBoxTo(ipco, "ispe", ispe);

        WriteBoxTo(iprp, "ipco", ipco.ToArray());

        // ipma (item property association)
        byte[] ipma = [ 0, 0, 0, 0, 0, 1, 0, 1, 1, 0x81 ]; // item 1, 1 association, property 1
        WriteBoxTo(iprp, "ipma", ipma);

        WriteBoxTo(meta, "iprp", iprp.ToArray());

        // iloc (item location) box
        var iloc = new List<byte>();
        iloc.AddRange(new byte[] { 0, 0, 0, 0 }); // version + flags
        iloc.Add(0x44); // offset_size=4, length_size=4
        iloc.Add(0x00); // base_offset_size=0, index_size=0
        iloc.Add(0);
        iloc.Add(1); // item_count = 1
        iloc.Add(0);
        iloc.Add(1); // item_ID = 1
        iloc.Add(0);
        iloc.Add(0); // data_reference_index = 0
        iloc.Add(0);
        iloc.Add(1); // extent_count = 1
        // extent_offset (4 bytes) — offset within mdat data
        byte[] offBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(offBytes, 0);
        iloc.AddRange(offBytes);
        // extent_length
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)dataLength);
        iloc.AddRange(lenBytes);
        WriteBoxTo(meta, "iloc", iloc.ToArray());

        WriteBox(output, "meta", meta.ToArray());
    }

    private static void WriteBox(List<byte> output, string type, byte[] data)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + data.Length));
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        output.AddRange(header);
        output.AddRange(data);
    }

    private static void WriteBoxTo(List<byte> target, string type, byte[] data)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + data.Length));
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        target.AddRange(header);
        target.AddRange(data);
    }

    private static void WriteNalUnit(List<byte> output, byte[] nal)
    {
        byte[] len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)nal.Length);
        output.AddRange(len);
        output.AddRange(nal);
    }

    private static void WriteObu(List<byte> output, int obuType, byte[] data)
    {
        // OBU header: type(4 bits) | has_extension(1) | has_size(1) | reserved(1)
        byte header = (byte)((obuType << 3) | 0x02); // has_size = true
        output.Add(header);
        // LEB128 size
        WriteLeb128(output, data.Length);
        output.AddRange(data);
    }

    private static void WriteLeb128(List<byte> output, int value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                b |= 0x80;
            }

            output.Add(b);
        }
        while (value > 0);
    }

    private static long ReadVarInt(byte[] data, int offset, int size)
    {
        if (size == 0)
        {
            return 0;
        }

        if (size == 2 && offset + 2 <= data.Length)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        }

        if (size == 4 && offset + 4 <= data.Length)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        }

        if (size == 8 && offset + 8 <= data.Length)
        {
            return (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
        }

        return 0;
    }

    private static int ReadLeb128(ReadOnlySpan<byte> data, ref int pos)
    {
        int result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }
        return result;
    }

    private static bool IsAvifBrand(string brand) => AvifBrands.Any(b => brand.StartsWith(b));

    private static bool IsHeicBrand(string brand) => HeicBrands.Any(b => brand.StartsWith(b));

    private static int BitsNeeded(int value)
    {
        int bits = 1;
        while ((1 << bits) <= value)
        {
            bits++;
        }

        return bits;
    }

    #endregion

    #region Simple Bit I/O

    private sealed class SimpleBitReader
    {
        private readonly byte[] data;
        private int pos;
        private int bitPos;

        public SimpleBitReader(byte[] data)
        {
            this.data = data;
            pos = 0;
            bitPos = 7;
        }

        public uint Read(int numBits)
        {
            uint result = 0;
            for (int i = 0;i < numBits;i++)
            {
                if (pos < data.Length)
                {
                    result |= (uint)((data[pos] >> bitPos) & 1) << (numBits - 1 - i);
                    bitPos--;
                    if (bitPos < 0)
                    {
                        bitPos = 7;
                        pos++;
                    }
                }
            }
            return result;
        }
    }

    private sealed class SimpleBitWriter
    {
        private readonly List<byte> buffer = new();
        private byte current;
        private int bitPos = 7;

        public void Write(int numBits, uint value)
        {
            for (int i = numBits - 1;i >= 0;i--)
            {
                if (((value >> i) & 1) != 0)
                {
                    current |= (byte)(1 << bitPos);
                }

                bitPos--;
                if (bitPos < 0)
                {
                    buffer.Add(current);
                    current = 0;
                    bitPos = 7;
                }
            }
        }

        public void Flush()
        {
            if (bitPos < 7)
            {
                buffer.Add(current);
            }
        }

        public byte[] GetBytes() => buffer.ToArray();
    }

    #endregion
}

