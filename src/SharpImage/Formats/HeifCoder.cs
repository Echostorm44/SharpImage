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

/// <summary>
/// Options that control AVIF encoding.
/// </summary>
public sealed class AvifEncodeOptions
{
    /// <summary>Quantization parameter 0..51 (0 = highest quality / largest file). Default 20.</summary>
    public int Qp { get; set; } = 20;

    /// <summary>Coded bit depth: 8, 10 or 12 — or 0 (default) to choose automatically: 8 when every source sample
    /// is exactly representable at 8 bits (e.g. images decoded from 8-bit formats), otherwise 10. 12-bit is coded
    /// with AV1 Professional profile (seq_profile 2).</summary>
    public int BitDepth { get; set; }
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
        var itemExtents = new Dictionary<int, (int Off, int Len)>(); // all items' first extent
        int alphaItemId = -1;

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

                        int absOff = (int)(baseOffset + extentOffset);
                        if (!itemExtents.ContainsKey(itemId))
                        {
                            itemExtents[itemId] = (absOff, (int)extentLength);
                        }

                        if (itemId == primaryItemId && itemDataOffset < 0)
                        {
                            itemDataOffset = absOff;
                            itemDataLength = (int)extentLength;
                        }
                    }
                }
            }

            // iref → auxl: an auxiliary item (alpha) referencing the primary. from_item is the alpha item.
            if (metaChildren.TryGetValue("iref", out var irefBox))
            {
                byte irefVersion = data[irefBox.DataOffset];
                var irefChildren = ParseBoxes(data, irefBox.DataOffset + 4, irefBox.DataLength - 4);
                if (irefChildren.TryGetValue("auxl", out var auxlBox))
                {
                    int p = auxlBox.DataOffset;
                    int fromId = irefVersion == 0
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p))
                        : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
                    p += irefVersion == 0 ? 2 : 4;
                    int refCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                    p += 2;
                    for (int r = 0; r < refCount; r++)
                    {
                        int toId = irefVersion == 0
                            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p))
                            : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
                        p += irefVersion == 0 ? 2 : 4;
                        if (toId == primaryItemId)
                        {
                            alphaItemId = fromId;
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

            // Auxiliary alpha item (monochrome AV1): decode it and merge into the frame's alpha channel.
            if (alphaItemId >= 0 && itemExtents.TryGetValue(alphaItemId, out var ax) && ax.Len > 0
                && ax.Off >= 0 && ax.Off + ax.Len <= data.Length)
            {
                ApplyAv1Alpha(data.AsSpan(ax.Off, ax.Len), frame, imageWidth, imageHeight);
            }
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
    /// Encodes an image to HEIC (HEVC) or AVIF (AV1) still image at the given quantization parameter (0 = highest
    /// quality/largest, ~51 = lowest). AVIF uses SharpImage's from-scratch AV1 intra encoder with automatic bit
    /// depth (see <see cref="AvifEncodeOptions.BitDepth"/>); use <see cref="EncodeAvif(ImageFrame, AvifEncodeOptions?)"/>
    /// for full control.
    /// </summary>
    public static byte[] Encode(ImageFrame image, HeifContainerType containerType, int qp)
    {
        if (containerType == HeifContainerType.Avif)
        {
            return EncodeAvif(image, new AvifEncodeOptions { Qp = qp });
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

    // AVIF encode via the from-scratch AV1 intra encoder. Current scope: grayscale, up to one 64x64 superblock.
    /// <summary>Encodes an image as AVIF (AV1 intra) with the given options.</summary>
    public static byte[] EncodeAvif(ImageFrame image, AvifEncodeOptions? options = null)
    {
        options ??= new AvifEncodeOptions();
        int bd = options.BitDepth == 0 ? (HasSubByteDetail(image) ? 10 : 8) : options.BitDepth;
        if (bd is not (8 or 10 or 12))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AVIF bit depth must be 0 (auto), 8, 10 or 12.");
        }

        return bd == 8 ? EncodeAvif8(image, options.Qp) : EncodeAvifHbd(image, options.Qp, bd);
    }

    // True when any sample carries more than 8 bits of precision (an 8-bit-origin sample is exactly v8 * 257).
    private static bool HasSubByteDetail(ImageFrame image)
    {
        int w = (int)image.Columns, h = (int)image.Rows, n = w * image.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (int i = 0; i < n; i++)
            {
                if (row[i] % 257 != 0) return true;
            }
        }

        return false;
    }

    // High-bit-depth AVIF encode (10/12-bit): samples are taken straight from the 16-bit quantum at full
    // precision and coded through the multi-superblock encoder (which handles every size 8..4096).
    private static byte[] EncodeAvifHbd(ImageFrame image, int qp, int bd)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w > 4096 || h > 4096 || w < 8 || h < 8)
        {
            throw new NotSupportedException($"AVIF encoding supports 8..4096 per dimension (got {w}x{h}).");
        }

        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int alphaOff = channels - 1;
        double scale = ((1 << bd) - 1) / 65535.0;
        var r = new double[w * h];
        var g = new double[w * h];
        var b = new double[w * h];
        var alpha = hasAlpha ? new ushort[w * h] : null;
        bool colour = false, nonOpaque = false;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * channels;
                ushort r16 = row[o];
                ushort g16 = channels >= 3 ? row[o + 1] : r16;
                ushort b16 = channels >= 3 ? row[o + 2] : r16;
                if (r16 != g16 || g16 != b16) colour = true;
                int i = y * w + x;
                r[i] = r16 * scale;
                g[i] = g16 * scale;
                b[i] = b16 * scale;
                if (alpha != null)
                {
                    ushort a16 = row[o + alphaOff];
                    alpha[i] = (ushort)Math.Round(a16 * scale);
                    if (a16 != ushort.MaxValue) nonOpaque = true;
                }
            }
        }

        int baseQIdx = Math.Clamp((int)Math.Round(Math.Clamp(qp, 0, 51) * (255.0 / 51.0)), 4, 255);
        if (hasAlpha && nonOpaque && alpha != null)
        {
            int alphaQIdx = Math.Clamp(baseQIdx / 2, 4, 255);
            RgbToI420Hbd(r, g, b, w, h, bd, out ushort[] yA, out ushort[] uA, out ushort[] vA);
            return Av1.Av1StillImageEncoder.EncodeAvifColorWithAlpha(yA, uA, vA, alpha, w, h, baseQIdx, alphaQIdx, bd);
        }

        if (colour)
        {
            RgbToI420Hbd(r, g, b, w, h, bd, out ushort[] yP, out ushort[] uP, out ushort[] vP);
            return Av1.Av1StillImageEncoder.EncodeAvifColorMultiSb(yP, uP, vP, w, h, baseQIdx, bd);
        }

        int max = (1 << bd) - 1;
        var luma = new ushort[w * h];
        for (int i = 0; i < luma.Length; i++) luma[i] = (ushort)Math.Clamp((int)Math.Round(r[i]), 0, max);
        return Av1.Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(luma, w, h, baseQIdx, bd);
    }

    // BT.601 full-range RGB -> I420 at an arbitrary bit depth (the same transform as RgbToI420, scaled): chroma is
    // the rounded mean of each (possibly partial, for odd dims) 2x2 group, offset by 2^(bd-1).
    private static void RgbToI420Hbd(double[] r, double[] g, double[] b, int w, int h, int bd,
        out ushort[] y, out ushort[] u, out ushort[] v)
    {
        int max = (1 << bd) - 1;
        double mid = 1 << (bd - 1);
        int cw = (w + 1) >> 1, chh = (h + 1) >> 1;
        y = new ushort[w * h];
        u = new ushort[cw * chh];
        v = new ushort[cw * chh];
        var uf = new double[cw * chh];
        var vf = new double[cw * chh];
        var cnt = new int[cw * chh];
        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                int i = yy * w + xx;
                double rr = r[i], gg = g[i], bb = b[i];
                y[i] = (ushort)Math.Clamp((int)Math.Round(0.299 * rr + 0.587 * gg + 0.114 * bb), 0, max);
                int ci = (yy >> 1) * cw + (xx >> 1);
                uf[ci] += -0.168736 * rr - 0.331264 * gg + 0.5 * bb + mid;
                vf[ci] += 0.5 * rr - 0.418688 * gg - 0.081312 * bb + mid;
                cnt[ci]++;
            }
        }

        for (int i = 0; i < cw * chh; i++)
        {
            int n = cnt[i] > 0 ? cnt[i] : 1;
            u[i] = (ushort)Math.Clamp((int)Math.Round(uf[i] / n), 0, max);
            v[i] = (ushort)Math.Clamp((int)Math.Round(vf[i] / n), 0, max);
        }
    }

    private static byte[] EncodeAvif8(ImageFrame image, int qp)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w > 4096 || h > 4096 || w < 8 || h < 8)
        {
            throw new NotSupportedException($"AVIF encoding supports 8..4096 per dimension (got {w}x{h}).");
        }

        // The single-block path is a fast path for an even, square frame <=64px; the multi-superblock path (a grid
        // of 64x64 superblocks with edge force-split partitioning) handles everything else — larger, non-square,
        // odd, or mixed small/large dimensions.
        bool multiSb = w > 64 || h > 64 || w != h || (w & 1) != 0 || (h & 1) != 0;

        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int alphaOff = channels - 1; // alpha is the last channel (idx 1 for gray+A, 3 for RGBA)

        // Extract tightly-packed RGB and luma; detect whether the image has real colour and non-opaque alpha.
        var rgb = new byte[w * h * 3];
        var luma = new byte[w * h];
        var alpha = hasAlpha ? new byte[w * h] : null;
        bool colour = false;
        bool nonOpaque = false;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * channels;
                int r = Quantum.ScaleToByte(row[o]);
                int g = channels >= 3 ? Quantum.ScaleToByte(row[o + 1]) : r;
                int b = channels >= 3 ? Quantum.ScaleToByte(row[o + 2]) : r;
                if (r != g || g != b)
                {
                    colour = true;
                }

                int d = (y * w + x) * 3;
                rgb[d] = (byte)r;
                rgb[d + 1] = (byte)g;
                rgb[d + 2] = (byte)b;
                luma[y * w + x] = (byte)r;
                if (alpha != null)
                {
                    byte a = Quantum.ScaleToByte(row[o + alphaOff]);
                    alpha[y * w + x] = a;
                    if (a != 255) nonOpaque = true;
                }
            }
        }

        // Map the HEVC-style qp (0..51, lower = better) to an AV1 base_q_idx (1..255, lower = better).
        int baseQIdx = Math.Clamp((int)Math.Round(Math.Clamp(qp, 0, 51) * (255.0 / 51.0)), 4, 255);

        // Non-opaque alpha ⇒ 2-item AVIF (colour primary + monochrome alpha aux). Alpha is coded at higher
        // quality than colour (half the base_q_idx) since matte edges are visually unforgiving.
        if (hasAlpha && nonOpaque && alpha != null)
        {
            int alphaQIdx = Math.Clamp(baseQIdx / 2, 4, 255);
            RgbToI420(rgb, w, h, out byte[] yA, out byte[] uA, out byte[] vA);
            return Av1.Av1StillImageEncoder.EncodeAvifColorWithAlpha(yA, uA, vA, alpha, w, h, baseQIdx, alphaQIdx);
        }

        if (colour)
        {
            // Colour: single-block I420 for <=64px, multi-superblock I420 for larger frames. I420 chroma is
            // ceil(w/2) x ceil(h/2) — odd luma dimensions are supported (the last chroma sample averages the
            // partial 2x2 group at the edge).
            RgbToI420(rgb, w, h, out byte[] yP, out byte[] uP, out byte[] vP);
            if (multiSb)
                return Av1.Av1StillImageEncoder.EncodeAvifColorMultiSb(yP, uP, vP, w, h, baseQIdx);
            if ((w & 1) != 0 || (h & 1) != 0)
                throw new NotSupportedException($"AVIF single-block colour needs even dimensions (got {w}x{h}); larger frames support odd.");
            return Av1.Av1StillImageEncoder.EncodeAvifColor(yP, uP, vP, w, h, baseQIdx);
        }

        return multiSb
            ? Av1.Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(luma, w, h, baseQIdx)
            : Av1.Av1StillImageEncoder.EncodeAvifMonochrome(luma, w, h, baseQIdx);
    }

    // BT.601 full-range RGB→YUV (the inverse of ConvertYuvToRgb's full-range BT.601 path) with I420 chroma
    // subsampling: w x h luma, (w/2) x (h/2) U and V (2x2 box average). Requires even dimensions.
    private static void RgbToI420(byte[] rgb, int w, int h, out byte[] y, out byte[] u, out byte[] v)
    {
        int cw = (w + 1) >> 1, chh = (h + 1) >> 1;   // ceil — odd dims keep a partial edge chroma sample
        y = new byte[w * h];
        u = new byte[cw * chh];
        v = new byte[cw * chh];
        var uf = new double[cw * chh];
        var vf = new double[cw * chh];
        var cnt = new int[cw * chh];

        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                int o = (yy * w + xx) * 3;
                double r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                double cb = -0.168736 * r - 0.331264 * g + 0.5 * b + 128.0;
                double cr = 0.5 * r - 0.418688 * g - 0.081312 * b + 128.0;
                y[yy * w + xx] = (byte)Math.Clamp((int)Math.Round(luma), 0, 255);
                int ci = (yy >> 1) * cw + (xx >> 1);
                uf[ci] += cb;
                vf[ci] += cr;
                cnt[ci]++;
            }
        }

        for (int i = 0; i < cw * chh; i++)
        {
            int n = cnt[i] > 0 ? cnt[i] : 1;   // edge groups may have 1 or 2 samples for odd dims
            u[i] = (byte)Math.Clamp((int)Math.Round(uf[i] / n), 0, 255);
            v[i] = (byte)Math.Clamp((int)Math.Round(vf[i] / n), 0, 255);
        }
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

    // Decodes a monochrome AV1 alpha auxiliary item and writes its luma samples into the frame's alpha channel
    // (enabling alpha if needed). Full-range 8-bit is the standard AVIF alpha representation.
    private static void ApplyAv1Alpha(ReadOnlySpan<byte> codedData, ImageFrame frame, int w, int h)
    {
        var decoder = new Av1.Av1Decoder();
        using var yuv = decoder.Decode(codedData, 0, isKeyframe: true) ?? throw new InvalidDataException("AV1 alpha decode produced no frame.");
        if (yuv.BitDepth > 8)
        {
            // Native-precision alpha: map [0, 2^bd) onto the full 16-bit quantum range.
            if (!frame.HasAlpha) frame.SetAlpha(true);
            ReadOnlySpan<ushort> a16 = yuv.YPlane16.Span;
            double s = 65535.0 / ((1 << yuv.BitDepth) - 1);
            int aOff = frame.NumberOfChannels - 1, nch = frame.NumberOfChannels;
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++) row[x * nch + aOff] = (ushort)Math.Clamp((int)Math.Round(a16[y * yuv.YStride + x] * s), 0, 65535);
            }
            return;
        }

        bool tenBit = yuv.Format is Av1.PixelFormat.Yuv420P10 or Av1.PixelFormat.Yuv420P12;
        int shift = tenBit ? (yuv.Format == Av1.PixelFormat.Yuv420P12 ? 4 : 2) : 0;
        ReadOnlySpan<byte> y0 = yuv.YPlane.Span;
        int stride = yuv.YStride;

        if (!frame.HasAlpha)
        {
            frame.SetAlpha(true);
        }

        int alphaOff = frame.NumberOfChannels - 1;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < w; x++)
            {
                int a = tenBit ? ((y0[(y * stride + x) * 2] | (y0[(y * stride + x) * 2 + 1] << 8)) >> shift) : y0[y * stride + x];
                row[x * ch + alphaOff] = Quantum.ScaleFromByte((byte)Math.Clamp(a, 0, 255));
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

        // YUV -> RGB exactly as libavif's reference path does it (avifImageYUVToRGB, AUTOMATIC/BEST_QUALITY
        // upsampling): per-depth unorm float tables, bilinear 9/3/3/1 chroma upsampling, libavif's matrix table.
        bool is444 = yuv.Format == Av1.PixelFormat.Yuv444P, is422 = yuv.Format == Av1.PixelFormat.Yuv422P;
        ConvertYuvToRgbLibavif(yuv, frame, (int)frame.Columns, (int)frame.Rows, frame.NumberOfChannels,
            decoder.Monochrome, matrixCoeffs, fullRange, is444 ? 0 : 1, (is444 || is422) ? 0 : 1);
    }

    // Converts a decoded 8/10/12-bit planar YUV 4:2:0 frame to RGB, honouring the colour
    // matrix (BT.709 vs BT.601) and range (full vs limited) signalled by the container.
    // Shared by the AVIF (AV1) and HEIC (HEVC) paths.
    private static void ConvertYuvToRgb(ReadOnlySpan<byte> y0, ReadOnlySpan<byte> u0, ReadOnlySpan<byte> v0, int yStride, int uStride, int vStride, ImageFrame frame, int w, int h, int channels, bool tenBit, int shift, bool bt709, bool fullRange, int ssHor = 1, int ssVer = 1)
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
            int cy = y >> ssVer;
            for (int x = 0; x < w; x++)
            {
                int cx = x >> ssHor;
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

    // Monochrome (I400): the luma plane IS the image. Replicate it into each output channel. Luma is treated as
    // full-range gray (no BT.601/709 chroma matrix applies).
    private static void ConvertGrayToRgb(ReadOnlySpan<byte> y0, int yStride, ImageFrame frame, int w, int h, int channels, bool tenBit, int shift)
    {
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int yv;
                if (!tenBit)
                {
                    yv = y0[y * yStride + x];
                }
                else
                {
                    int idx = (y * yStride + x) * 2;
                    yv = (y0[idx] | (y0[idx + 1] << 8)) >> shift;
                }

                ushort q = Quantum.ScaleFromByte(ClampByte(yv));
                int off = x * channels;
                row[off] = q;
                if (channels > 1)
                {
                    row[off + 1] = q;
                }

                if (channels > 2)
                {
                    row[off + 2] = q;
                }
            }
        }
    }

    private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // libavif's matrix table (colr.c matrixCoefficientsTables): (kr, kb) per CICP matrix_coefficients. Anything else
    // (incl. unspecified) falls back to the MIAF default BT.601, as libavif does.
    private static (float Kr, float Kb) MatrixKrKb(int mc) => mc switch
    {
        1 => (0.2126f, 0.0722f),   // BT.709
        4 => (0.30f, 0.11f),       // FCC
        5 or 6 => (0.299f, 0.114f), // BT.470BG / BT.601
        7 => (0.212f, 0.087f),     // SMPTE 240
        9 => (0.2627f, 0.0593f),   // BT.2020 NCL
        _ => (0.299f, 0.114f),
    };

    // A port of libavif's avifImageYUVAnyToRGBAnySlow for the YUV->RGB step (8/10/12-bit, 4:0:0/4:2:0/4:2:2/4:4:4):
    // unorm float tables (limited range: Y (v-16s)/219s, UV (v-2^(bd-1))/224s; full: v/max, (v-2^(bd-1))/max),
    // bilinear chroma with weights 9/16, 3/16, 3/16, 1/16 where the second tap is the neighbouring chroma sample
    // toward the luma sample's side (none at the picture edge; 4:2:2 is horizontal only), clamp to [0,1], and
    // (uint16)(0.5f + v * 65535) into the 16-bit quantum. 32-bit float throughout, like libavif.
    private static void ConvertYuvToRgbLibavif(Av1.DecodedVideoFrame yuv, ImageFrame frame, int w, int h, int channels,
        bool monochrome, int matrixCoeffs, bool fullRange, int ssHor, int ssVer)
    {
        int bd = yuv.BitDepth, maxCh = (1 << bd) - 1;
        bool hbd = bd > 8;
        float rangeY = fullRange ? maxCh : 219 << (bd - 8), biasY = fullRange ? 0 : 16 << (bd - 8);
        float rangeUV = fullRange ? maxCh : 224 << (bd - 8), biasUV = 1 << (bd - 1);
        var tabY = new float[maxCh + 1];
        var tabUV = new float[maxCh + 1];
        for (int cp = 0; cp <= maxCh; cp++) { tabY[cp] = (cp - biasY) / rangeY; tabUV[cp] = (cp - biasUV) / rangeUV; }
        (float kr, float kb) = MatrixKrKb(matrixCoeffs);
        float kg = 1.0f - kr - kb;

        ReadOnlySpan<byte> y8 = yuv.YPlane.Span, u8 = yuv.UPlane.Span, v8 = yuv.VPlane.Span;
        ReadOnlySpan<ushort> y16 = yuv.YPlane16.Span, u16 = yuv.UPlane16.Span, v16 = yuv.VPlane16.Span;
        int ys = yuv.YStride, us = yuv.UStride, vs = yuv.VStride;
        bool is420 = ssVer == 1, is444 = ssHor == 0;

        for (int j = 0; j < h; j++)
        {
            var row = frame.GetPixelRowForWrite(j);
            int uvJ = j >> ssVer;
            int adjRow = (j == 0 || (j == h - 1 && (j & 1) != 0) || !is420) ? 0 : ((j & 1) != 0 ? 1 : -1);
            for (int i = 0; i < w; i++)
            {
                float Y = tabY[Math.Min(hbd ? y16[j * ys + i] : y8[j * ys + i], maxCh)];
                float R, G, B;
                if (monochrome)
                {
                    R = G = B = Y;
                }
                else
                {
                    float Cb, Cr;
                    int uvI = i >> ssHor;
                    if (is444)
                    {
                        Cb = tabUV[Math.Min(hbd ? u16[uvJ * us + uvI] : u8[uvJ * us + uvI], maxCh)];
                        Cr = tabUV[Math.Min(hbd ? v16[uvJ * vs + uvI] : v8[uvJ * vs + uvI], maxCh)];
                    }
                    else
                    {
                        int adjCol = (i == 0 || (i == w - 1 && (i & 1) != 0)) ? 0 : ((i & 1) != 0 ? 1 : -1);
                        int c0 = uvJ * us + uvI, c1 = c0 + adjCol, c2 = c0 + adjRow * us, c3 = c2 + adjCol;
                        int d0 = uvJ * vs + uvI, d1 = d0 + adjCol, d2 = d0 + adjRow * vs, d3 = d2 + adjCol;
                        Cb = S(u8, u16, hbd, c0, maxCh, tabUV) * (9.0f / 16.0f) + S(u8, u16, hbd, c1, maxCh, tabUV) * (3.0f / 16.0f)
                           + S(u8, u16, hbd, c2, maxCh, tabUV) * (3.0f / 16.0f) + S(u8, u16, hbd, c3, maxCh, tabUV) * (1.0f / 16.0f);
                        Cr = S(v8, v16, hbd, d0, maxCh, tabUV) * (9.0f / 16.0f) + S(v8, v16, hbd, d1, maxCh, tabUV) * (3.0f / 16.0f)
                           + S(v8, v16, hbd, d2, maxCh, tabUV) * (3.0f / 16.0f) + S(v8, v16, hbd, d3, maxCh, tabUV) * (1.0f / 16.0f);
                    }

                    R = Y + (2 * (1 - kr)) * Cr;
                    B = Y + (2 * (1 - kb)) * Cb;
                    G = Y - ((2 * ((kr * (1 - kr) * Cr) + (kb * (1 - kb) * Cb))) / kg);
                }

                int off = i * channels;
                row[off] = Q16f(R);
                if (channels >= 3)
                {
                    row[off + 1] = Q16f(G);
                    row[off + 2] = Q16f(B);
                }
            }
        }
    }

    private static float S(ReadOnlySpan<byte> p8, ReadOnlySpan<ushort> p16, bool hbd, int k, int maxCh, float[] tab)
        => tab[Math.Min(hbd ? p16[k] : p8[k], maxCh)];

    private static ushort Q16f(float v) => (ushort)(0.5f + Math.Clamp(v, 0.0f, 1.0f) * 65535.0f);

    private static ushort Q16(double n) => (ushort)Math.Clamp((int)Math.Round(n * 65535.0), 0, 65535);

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

