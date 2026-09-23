using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Formats;

/// <summary>A JPEG's decoded component planes before chroma upsampling and colour conversion (libjpeg raw_data_out),
/// reconstructed with libjpeg-turbo's accurate integer IDCT (jpeg_idct_islow). Plane 0 is Y (or grey), 1 Cb, 2 Cr, each
/// at its downsampled size.</summary>
internal sealed class JpegRawYuv
{
    public required byte[][] Planes;
    public required int Width;
    public required int Height;
    /// <summary>4:4:4 / 4:2:2 / 4:2:0 for YCbCr, 4:0:0 for greyscale.</summary>
    public required Av1.Av1PixelLayout Layout;
}

public static partial class JpegCoder
{
    private const string XmpNsRdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmpNsGainMap = "http://ns.adobe.com/hdr-gain-map/1.0/";
    private const string XmpNsAppleGainMap = "http://ns.apple.com/HDRGainMap/1.0/";

    /// <summary>
    /// Reads the gain map of an Ultra HDR / ISO gain map JPEG (Adobe hdrgm XMP) or an Apple HDR JPEG (HDRGainMap XMP,
    /// headroom from XMP or the Exif maker notes), as libavif's JPEG reader does: the Multi-Picture Format (MPF) index
    /// is walked for a secondary image whose XMP carries gain map metadata. The alternate (HDR) rendition is assumed
    /// to share the base image's primaries (and ICC profile) with a PQ transfer, 8 bits. Returns null when the file has
    /// no gain map or its metadata is invalid.
    /// </summary>
    public static AvifGainMap? ReadGainMap(byte[] jpeg) => ReadGainMap(jpeg, null);

    internal static AvifGainMap? ReadGainMap(byte[] jpeg, Action<ImageFrame, JpegRawYuv>? attachRaw)
    {
        var baseSegs = ScanSegments(jpeg, 0);
        byte[]? mpf = null;
        int mpfOffset = 0;
        foreach (var (marker, off, len) in baseSegs)
        {
            if (marker == APP2 && len > 4 && jpeg.AsSpan(off, 4).SequenceEqual("MPF\0"u8))
            {
                mpf = jpeg.AsSpan(off + 4, len - 4).ToArray();
                mpfOffset = off + 4;
                break;   // libavif only looks at the first MPF segment
            }
        }
        if (mpf == null) return null;

        foreach (int imageOffset in MpfImageOffsets(mpf))
        {
            int start = mpfOffset + imageOffset;
            if (start < 0 || start + 4 > jpeg.Length || jpeg[start] != 0xFF || jpeg[start + 1] != SOI) continue;
            string? xmp = StandardXmp(jpeg, ScanSegments(jpeg, start));
            if (xmp == null || FindGainMapDescription(xmp, out _) == null) continue;

            var gm = new AvifGainMap();
            if (!ParseGainMapXmp(xmp, gm, out bool apple)) return null;
            if (apple && gm.AlternateHdrHeadroom.Numerator == 0)
            {
                // Older Apple files keep the headroom only in the base image's Exif maker notes.
                byte[]? exif = ExifTiff(jpeg, baseSegs);
                if (exif == null || !TryAppleExifHeadroom(exif, out double headroom) || headroom <= 0) return null;
                gm.AlternateHdrHeadroom = HeifCoder.ToUFraction(headroom);
                var max = HeifCoder.ToSFraction(headroom);
                for (int c = 0; c < 3; c++) gm.Max[c] = max;
            }

            ImageFrame img;
            try { img = Read(new MemoryStream(jpeg, start, jpeg.Length - start)); }
            catch (Exception) { continue; }
            img.Depth = 8;
            img.Metadata.Cicp = new CicpInfo(2, 2, 6, true);   // libavif copies the JPEG's YCbCr: BT.601 coefficients
            img.IccProfile = null;
            img.Metadata.IccProfile = null;
            img.Metadata.ExifProfile = null;
            img.Metadata.Xmp = null;
            if (attachRaw != null && ReadRawYuv(jpeg, start) is { } raw) attachRaw(img, raw);
            gm.Image = img;
            gm.AlternateDepth = 8;
            gm.AlternatePlaneCount = IsGreyJpeg(jpeg, 0) && IsGreyJpeg(jpeg, start) ? 1 : 3;
            return gm;
        }
        return null;
    }

    private static bool IsGreyJpeg(byte[] jpeg, int start)
    {
        foreach (var (marker, off, len) in ScanSegments(jpeg, start))
            if (marker is SOF0 or SOF1 or SOF2 && len >= 6) return jpeg[off + 5] == 1;
        return false;
    }

    // Marker segments from SOI up to the first SOS: (marker, payload offset, payload length).
    private static List<(int Marker, int Offset, int Length)> ScanSegments(byte[] d, int start)
    {
        var list = new List<(int, int, int)>();
        int p = start + 2;
        for (int n = 0; n < 1000 && p + 4 <= d.Length; n++)
        {
            if (d[p] != 0xFF) break;
            int marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker == EOI || marker == 0xDA) break;
            int len = (d[p + 2] << 8) | d[p + 3];
            if (len < 2 || p + 2 + len > d.Length) break;
            list.Add((marker, p + 4, len - 2));
            p += 2 + len;
        }
        return list;
    }

    private static string? StandardXmp(byte[] d, List<(int Marker, int Offset, int Length)> segs)
    {
        ReadOnlySpan<byte> tag = "http://ns.adobe.com/xap/1.0/\0"u8;
        foreach (var (marker, off, len) in segs)
        {
            if (marker != APP1 || len <= tag.Length || !d.AsSpan(off, tag.Length).SequenceEqual(tag)) continue;
            int n = len - tag.Length;
            if (n > 0 && d[off + tag.Length + n - 1] == 0) n--;   // avifImageFixXMP: one trailing null
            return Encoding.UTF8.GetString(d, off + tag.Length, n);
        }
        return null;
    }

    // The base image's Exif payload starting at its TIFF header.
    private static byte[]? ExifTiff(byte[] d, List<(int Marker, int Offset, int Length)> segs)
    {
        foreach (var (marker, off, len) in segs)
            if (marker == APP1 && len > 6 && d.AsSpan(off, 6).SequenceEqual("Exif\0\0"u8))
                return d.AsSpan(off + 6, len - 6).ToArray();
        return null;
    }

    // CIPA DC-007 MP Index IFD: the data offsets (relative to the MPF segment's TIFF header) of the non-first images.
    private static List<int> MpfImageOffsets(byte[] s)
    {
        var result = new List<int>();
        if (s.Length < 8) return result;
        bool be;
        if (s.AsSpan(0, 4).SequenceEqual("MM\0*"u8)) be = true;
        else if (s.AsSpan(0, 4).SequenceEqual("II*\0"u8)) be = false;
        else return result;
        uint U32(int o) => o + 4 > s.Length ? throw new IndexOutOfRangeException()
            : be ? BinaryPrimitives.ReadUInt32BigEndian(s.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(o));
        int U16(int o) => o + 2 > s.Length ? throw new IndexOutOfRangeException()
            : be ? BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(o));
        try
        {
            uint ifd = U32(4);
            if (ifd < 8) return result;
            int o = (int)ifd;
            int count = U16(o);
            o += 2;
            uint numImages = 0, entryOffset = 0;
            for (int i = 0; i < count; i++)
            {
                int tag = U16(o);
                uint value = U32(o + 8);
                if (tag == 45056 && !s.AsSpan(o + 8, 4).SequenceEqual("0100"u8)) return result;   // MPFVersion
                if (tag == 45057) numImages = value;
                if (tag == 45058) entryOffset = value;
                o += 12;
            }
            if (numImages < 2 || entryOffset < o) return result;
            o = (int)entryOffset;
            for (uint i = 0; i < numImages; i++)
            {
                uint dataOffset = U32(o + 8);
                o += 16;
                if (dataOffset != 0) result.Add((int)dataOffset);   // 0 = the first (base) image
            }
        }
        catch (IndexOutOfRangeException) { }
        return result;
    }

    private static XmlDocument? LoadXmp(string xmp)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xmp),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(reader);
            return doc;
        }
        catch (XmlException) { return null; }
    }

    // The rdf:Description holding gain map metadata: ISO (hdrgm:Version="1.0" attribute) first, then Apple
    // (a HDRGainMap:HDRGainMapVersion child).
    private static XmlElement? FindGainMapDescription(string xmp, out bool apple)
    {
        apple = false;
        var doc = LoadXmp(xmp);
        return doc == null ? null : FindGainMapDescription(doc, out apple);
    }

    private static XmlElement? FindGainMapDescription(XmlDocument doc, out bool apple)
    {
        apple = false;
        var rdf = doc.GetElementsByTagName("RDF", XmpNsRdf);
        if (rdf.Count == 0) return null;
        var descs = new List<XmlElement>();
        foreach (XmlNode n in rdf[0]!.ChildNodes)
            if (n is XmlElement e && e.NamespaceURI == XmpNsRdf && e.LocalName == "Description") descs.Add(e);
        foreach (var e in descs)
            foreach (XmlAttribute a in e.Attributes)
                if (a.NamespaceURI == XmpNsGainMap && a.LocalName == "Version" && a.Value == "1.0") return e;
        foreach (var e in descs)
            foreach (XmlNode c in e.ChildNodes)
                if (c is XmlElement ce && ce.NamespaceURI == XmpNsAppleGainMap && ce.LocalName == "HDRGainMapVersion")
                {
                    apple = true;
                    return e;
                }
        return null;
    }

    // avifJPEGFindGainMapProperty: an attribute of the description, or a child element holding text or an rdf:Seq.
    // Returns null when absent, an empty list when present without usable content.
    private static List<string>? GainMapProperty(XmlElement desc, string name, string ns)
    {
        foreach (XmlAttribute a in desc.Attributes)
            if (a.NamespaceURI == ns && a.LocalName == name) return [a.Value];
        foreach (XmlNode n in desc.ChildNodes)
        {
            if (n is not XmlElement e || e.NamespaceURI != ns || e.LocalName != name || !e.HasChildNodes) continue;
            XmlElement? seq = null;
            foreach (XmlNode c in e.ChildNodes)
                if (c is XmlElement ce && ce.NamespaceURI == XmpNsRdf && ce.LocalName == "Seq") { seq = ce; break; }
            if (seq != null)
            {
                var values = new List<string>();
                foreach (XmlNode li in seq.ChildNodes)
                    if (li is XmlElement le && le.LocalName == "li" && le.FirstChild is XmlText t) values.Add(t.Value ?? "");
                return values;
            }
            if (e.ChildNodes.Count == 1 && e.FirstChild is XmlText text) return [text.Value ?? ""];
            return [];
        }
        return null;
    }

    // avifJPEGFindGainMapPropertyDoubles: absent keeps the defaults; one value fills every channel; otherwise exactly
    // values.Length values; each must parse fully as a double (trailing whitespace allowed).
    private static bool GainMapDoubles(XmlElement desc, string name, double[] values, string ns)
    {
        var text = GainMapProperty(desc, name, ns);
        if (text == null) return true;
        if (text.Count != 1 && text.Count != values.Length) return false;
        for (int i = 0; i < values.Length; i++)
        {
            if (i >= text.Count) { values[i] = values[i - 1]; continue; }
            string s = text[i].TrimEnd(' ', '\t', '\n', '\v', '\f', '\r');
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
        }
        return true;
    }

    private static bool ParseGainMapXmp(string xmp, AvifGainMap gm, out bool apple)
    {
        apple = false;
        var doc = LoadXmp(xmp);
        var desc = doc == null ? null : FindGainMapDescription(doc, out apple);
        if (desc == null) return false;
        try
        {
            if (apple)
            {
                var headroomLinear = new[] { 1.0 };
                if (!GainMapDoubles(desc, "HDRGainMapHeadroom", headroomLinear, XmpNsAppleGainMap) || headroomLinear[0] <= 0) return false;
                double headroom = Math.Log2(headroomLinear[0]);
                var h = HeifCoder.ToSFraction(headroom);
                for (int c = 0; c < 3; c++)
                {
                    gm.Min[c] = new GainMapFraction(0, 1);
                    gm.Max[c] = h;
                    gm.Gamma[c] = new GainMapUFraction(1, 1);
                    gm.BaseOffset[c] = new GainMapFraction(0, 1);
                    gm.AlternateOffset[c] = new GainMapFraction(0, 1);
                }
                gm.BaseHdrHeadroom = new GainMapUFraction(0, 1);
                gm.AlternateHdrHeadroom = HeifCoder.ToUFraction(headroom);
                return true;
            }

            var baseH = new[] { 0.0 };
            var altH = new[] { 1.0 };
            double[] min = [0, 0, 0], max = [1, 1, 1], gamma = [1, 1, 1];
            double[] baseOff = [1.0 / 64, 1.0 / 64, 1.0 / 64], altOff = [1.0 / 64, 1.0 / 64, 1.0 / 64];
            if (!GainMapDoubles(desc, "HDRCapacityMin", baseH, XmpNsGainMap) || !GainMapDoubles(desc, "HDRCapacityMax", altH, XmpNsGainMap)
                || !GainMapDoubles(desc, "OffsetSDR", baseOff, XmpNsGainMap) || !GainMapDoubles(desc, "OffsetHDR", altOff, XmpNsGainMap)
                || !GainMapDoubles(desc, "GainMapMin", min, XmpNsGainMap) || !GainMapDoubles(desc, "GainMapMax", max, XmpNsGainMap)
                || !GainMapDoubles(desc, "Gamma", gamma, XmpNsGainMap))
                return false;
            if (!(altH[0] > baseH[0]) || baseH[0] < 0) return false;
            for (int c = 0; c < 3; c++)
                if (!(max[c] >= min[c]) || baseOff[c] < 0 || altOff[c] < 0 || !(gamma[c] > 0)) return false;
            if (GainMapProperty(desc, "BaseRenditionIsHDR", XmpNsGainMap) is { Count: > 0 } isHdr)
            {
                if (isHdr[0] == "True")
                {
                    (baseH[0], altH[0]) = (altH[0], baseH[0]);
                    (baseOff, altOff) = (altOff, baseOff);
                }
                else if (isHdr[0] != "False") return false;
            }
            for (int c = 0; c < 3; c++)
            {
                gm.Min[c] = HeifCoder.ToSFraction(min[c]);
                gm.Max[c] = HeifCoder.ToSFraction(max[c]);
                gm.Gamma[c] = HeifCoder.ToUFraction(gamma[c]);
                gm.BaseOffset[c] = HeifCoder.ToSFraction(baseOff[c]);
                gm.AlternateOffset[c] = HeifCoder.ToSFraction(altOff[c]);
            }
            gm.BaseHdrHeadroom = HeifCoder.ToUFraction(baseH[0]);
            gm.AlternateHdrHeadroom = HeifCoder.ToUFraction(altH[0]);
            gm.UseBaseColorSpace = true;
            return true;
        }
        catch (ArgumentException) { return false; }   // a value no fraction can hold
    }

    // avifGetExifAppleHeadroom: Apple maker note tags 33 and 48 (rationals) -> HDR headroom in stops.
    private static bool TryAppleExifHeadroom(byte[] exif, out double headroom)
    {
        headroom = 0;
        int tiff = -1;
        for (int i = 0; i + 4 <= exif.Length; i++)
            if (exif.AsSpan(i, 4).SequenceEqual("II*\0"u8) || exif.AsSpan(i, 4).SequenceEqual("MM\0*"u8)) { tiff = i; break; }
        if (tiff < 0) return false;
        bool be = exif[tiff] == 'M';
        ReadOnlySpan<byte> appleHeader = "Apple iOS\0\0\u0001MM"u8;
        try
        {
            uint U32(long o) => o < 0 || o + 4 > exif.Length ? throw new IndexOutOfRangeException()
                : be ? BinaryPrimitives.ReadUInt32BigEndian(exif.AsSpan((int)o)) : BinaryPrimitives.ReadUInt32LittleEndian(exif.AsSpan((int)o));
            ushort U16(long o) => o < 0 || o + 2 > exif.Length ? throw new IndexOutOfRangeException()
                : be ? BinaryPrimitives.ReadUInt16BigEndian(exif.AsSpan((int)o)) : BinaryPrimitives.ReadUInt16LittleEndian(exif.AsSpan((int)o));
            long offset = tiff + 4;
            uint offsetToIfd = U32(offset);
            offset += 4;
            bool inApple = false, found = false;
            double maker33 = 0, maker48 = 0;
            for (int numIfds = 0; offsetToIfd != 0 && numIfds < 3; numIfds++)
            {
                offset = offsetToIfd;
                bool nextSet = false;
                ushort fields = U16(offset);
                offset += 2;
                for (int f = 0; f < fields; f++)
                {
                    ushort tag = U16(offset), format = U16(offset + 2);
                    uint data = U32(offset + 8);
                    offset += 12;
                    if (tag == 0x8769) { offset -= 4; break; }   // Exif IFD pointer: read it as the next IFD
                    if (tag == 0x927c)
                    {
                        long mn = data;
                        if (mn + appleHeader.Length <= exif.Length && exif.AsSpan((int)mn, appleHeader.Length).SequenceEqual(appleHeader))
                        {
                            offsetToIfd = (uint)(mn + appleHeader.Length);
                            inApple = true;
                            nextSet = true;
                            be = true;   // Apple maker notes are always big endian
                            break;
                        }
                    }
                    else if (inApple && (tag == 33 || tag == 48) && format == 10)
                    {
                        if (offsetToIfd < appleHeader.Length) return false;
                        long t = (long)offsetToIfd - appleHeader.Length + data;
                        int num = (int)U32(t);
                        uint den = U32(t + 4);
                        if (den == 0) return false;
                        double v = (double)num / den;
                        if (tag == 33) maker33 = v; else maker48 = v;
                        found = true;
                    }
                }
                if (!nextSet) offsetToIfd = U32(offset);
            }
            if (!found) return false;
            headroom = maker33 < 1.0
                ? (maker48 <= 0.01 ? -20.0 * maker48 + 1.8 : -0.101 * maker48 + 1.601)
                : (maker48 <= 0.01 ? -70.0 * maker48 + 3.0 : -0.303 * maker48 + 2.303);
            return true;
        }
        catch (IndexOutOfRangeException) { return false; }
    }

    /// <summary>The JPEG starting at <paramref name="start"/> as raw component planes (libjpeg-turbo islow IDCT), or
    /// null when it is not a greyscale or a 4:4:4 / 4:2:2 / 4:2:0 YCbCr JPEG (e.g. Adobe RGB-transform or CMYK files,
    /// or unusual sampling factors).</summary>
    internal static JpegRawYuv? ReadRawYuv(byte[] jpeg, int start = 0)
    {
        var c = ReadComponents(jpeg, start);
        if (c == null) return null;
        Av1.Av1PixelLayout layout;
        if (c.Planes.Length == 1) layout = Av1.Av1PixelLayout.I400;
        else
        {
            if (c.IsRgb || c.HSamp[1] != 1 || c.VSamp[1] != 1 || c.HSamp[2] != 1 || c.VSamp[2] != 1) return null;
            Av1.Av1PixelLayout? l = (c.HSamp[0], c.VSamp[0]) switch
            {
                (1, 1) => Av1.Av1PixelLayout.I444,
                (2, 1) => Av1.Av1PixelLayout.I422,
                (2, 2) => Av1.Av1PixelLayout.I420,
                _ => null,
            };
            if (l == null) return null;
            layout = l.Value;
        }
        return new JpegRawYuv { Planes = c.Planes, Width = c.Width, Height = c.Height, Layout = layout };
    }

    // libjpeg default_decompress_parms for 3 components: JFIF -> YCbCr; Adobe APP14 transform 0 -> RGB (else YCbCr);
    // otherwise component ids 'R','G','B' -> RGB, anything else YCbCr.
    private static bool IsYCbCr(byte[] jpeg, int start, JpegDctData d)
    {
        bool jfif = false;
        int adobeTransform = -1;
        foreach (var (marker, off, len) in ScanSegments(jpeg, start))
        {
            if (marker == 0xE0 && len >= 5 && jpeg.AsSpan(off, 5).SequenceEqual("JFIF\0"u8)) jfif = true;
            if (marker == 0xEE && len >= 12 && jpeg.AsSpan(off, 5).SequenceEqual("Adobe"u8)) adobeTransform = jpeg[off + 11];
        }
        if (jfif) return true;
        if (adobeTransform >= 0) return adobeTransform != 0;
        return !(d.Components[0].Id == 'R' && d.Components[1].Id == 'G' && d.Components[2].Id == 'B');
    }

    // libjpeg-turbo jpeg_idct_islow (jidctint.c): the accurate 8x8 integer IDCT (CONST_BITS 13, PASS1_BITS 2) with its
    // post-IDCT range-limit table (values wrap through a 1024-entry mask before clamping).
    private static void IdctIslow(int[] coef, int[] qt, Span<byte> output)
    {
        const int CB = 13, P1 = 2;
        const int F0_298 = 2446, F0_390 = 3196, F0_541 = 4433, F0_765 = 6270, F0_899 = 7373, F1_175 = 9633,
            F1_501 = 12299, F1_847 = 15137, F1_961 = 16069, F2_053 = 16819, F2_562 = 20995, F3_072 = 25172;
        Span<int> ws = stackalloc int[64];
        for (int col = 0; col < 8; col++)
        {
            if (coef[8 + col] == 0 && coef[16 + col] == 0 && coef[24 + col] == 0 && coef[32 + col] == 0 &&
                coef[40 + col] == 0 && coef[48 + col] == 0 && coef[56 + col] == 0)
            {
                int dc = (coef[col] * qt[col]) << P1;
                for (int r = 0; r < 8; r++) ws[r * 8 + col] = dc;
                continue;
            }
            int z2 = coef[16 + col] * qt[16 + col], z3 = coef[48 + col] * qt[48 + col];
            int z1 = (z2 + z3) * F0_541;
            int tmp2 = z1 + z3 * -F1_847, tmp3 = z1 + z2 * F0_765;
            z2 = coef[col] * qt[col];
            z3 = coef[32 + col] * qt[32 + col];
            int tmp0 = (z2 + z3) << CB, tmp1 = (z2 - z3) << CB;
            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
            tmp0 = coef[56 + col] * qt[56 + col];
            tmp1 = coef[40 + col] * qt[40 + col];
            tmp2 = coef[24 + col] * qt[24 + col];
            tmp3 = coef[8 + col] * qt[8 + col];
            OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);
            const int s = CB - P1;
            ws[col] = Descale(tmp10 + tmp3, s);
            ws[56 + col] = Descale(tmp10 - tmp3, s);
            ws[8 + col] = Descale(tmp11 + tmp2, s);
            ws[48 + col] = Descale(tmp11 - tmp2, s);
            ws[16 + col] = Descale(tmp12 + tmp1, s);
            ws[40 + col] = Descale(tmp12 - tmp1, s);
            ws[24 + col] = Descale(tmp13 + tmp0, s);
            ws[32 + col] = Descale(tmp13 - tmp0, s);
        }
        for (int row = 0; row < 8; row++)
        {
            int o = row * 8;
            const int s = CB + P1 + 3;
            int z2 = ws[o + 2], z3 = ws[o + 6];
            int z1 = (z2 + z3) * F0_541;
            int tmp2 = z1 + z3 * -F1_847, tmp3 = z1 + z2 * F0_765;
            int tmp0 = (ws[o] + ws[o + 4]) << CB, tmp1 = (ws[o] - ws[o + 4]) << CB;
            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
            tmp0 = ws[o + 7]; tmp1 = ws[o + 5]; tmp2 = ws[o + 3]; tmp3 = ws[o + 1];
            OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);
            output[o] = RangeLimit(Descale(tmp10 + tmp3, s));
            output[o + 7] = RangeLimit(Descale(tmp10 - tmp3, s));
            output[o + 1] = RangeLimit(Descale(tmp11 + tmp2, s));
            output[o + 6] = RangeLimit(Descale(tmp11 - tmp2, s));
            output[o + 2] = RangeLimit(Descale(tmp12 + tmp1, s));
            output[o + 5] = RangeLimit(Descale(tmp12 - tmp1, s));
            output[o + 3] = RangeLimit(Descale(tmp13 + tmp0, s));
            output[o + 4] = RangeLimit(Descale(tmp13 - tmp0, s));
        }

        static void OddPart(ref int t0, ref int t1, ref int t2, ref int t3)
        {
            int z1 = t0 + t3, z2 = t1 + t2, z3 = t0 + t2, z4 = t1 + t3;
            int z5 = (z3 + z4) * F1_175;
            t0 *= F0_298; t1 *= F2_053; t2 *= F3_072; t3 *= F1_501;
            z1 *= -F0_899; z2 *= -F2_562; z3 *= -F1_961; z4 *= -F0_390;
            z3 += z5; z4 += z5;
            t0 += z1 + z3; t1 += z2 + z4; t2 += z2 + z3; t3 += z1 + z4;
        }
        static int Descale(int x, int n) => (x + (1 << (n - 1))) >> n;
        static byte RangeLimit(int v)
        {
            int x = v & 1023;
            return x < 128 ? (byte)(x + 128) : x < 512 ? (byte)255 : x < 896 ? (byte)0 : (byte)(x - 896);
        }
    }
}
