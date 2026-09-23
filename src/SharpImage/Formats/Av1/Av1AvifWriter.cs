// AVIF (AV1 Image File Format) ISOBMFF container writer. AVIF is a HEIF/MIAF file whose primary item is an
// AV1-coded image: an `av01` item described by an `av1C` config box (carrying the sequence-header OBU) with the
// frame OBUs in `mdat`. Mirrors the box structure of the HEIC encoder, swapping hvc1/hvcC for av01/av1C.
// Optional: a monochrome alpha auxiliary item (auxl), Exif / XMP metadata items (cdsc), and primary-item
// properties for ICC (colr prof), transforms (clap/irot/imir, essential), pasp and HDR light levels (clli/mdcv),
// laid out the way libavif writes them. Verified: files open in ffmpeg/libdav1d and libavif.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

/// <summary>Optional AVIF container content beyond the coded image(s): colour profile, metadata items, transforms
/// and HDR properties of the primary item.</summary>
internal sealed class AvifContainerExtras
{
    public byte[]? Icc;                      // colr 'prof' (raw ICC profile)
    public byte[]? Exif;                     // Exif item payload (TIFF header onward, or with a leading "Exif\0\0")
    public byte[]? Xmp;                      // XMP packet (mime item, application/rdf+xml)
    public int? IrotAngle;                   // irot: anti-clockwise rotation in 90° units (0..3)
    public int? ImirAxis;                    // imir (ISO/IEC 23008-12:2022): 0 = top/bottom exchanged, 1 = left/right exchanged
    public uint[]? Clap;                     // clap: widthN,widthD,heightN,heightD,horizOffN,horizOffD,vertOffN,vertOffD
    public (uint H, uint V)? Pasp;           // pasp: pixel aspect ratio hSpacing:vSpacing
    public (ushort MaxCll, ushort MaxPall)? Clli;
    public byte[]? Mdcv;                     // mdcv payload (24 bytes: display primaries, white point, max/min luminance)

    internal bool HasItems => Exif != null || Xmp != null;
}

internal static class Av1AvifWriter
{
    /// <summary>Builds a complete .avif file. <paramref name="seqObu"/> is the sequence-header OBU (size-field
    /// form); it is placed in-band at the head of the mdat item data (the item decodes as a self-contained temporal
    /// unit, as libaom/ffmpeg AVIF output does). <paramref name="frameObu"/> is the OBU_FRAME.</summary>
    internal static byte[] BuildAvif(byte[] seqObu, byte[] frameObu, int width, int height, bool monochrome, int bitDepth = 8,
        Av1PixelLayout layout = Av1PixelLayout.I420, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (monochrome) layout = Av1PixelLayout.I400;
        return BuildContainer(Concat(seqObu, frameObu), null, width, height, bitDepth, layout, color, extras);
    }

    /// <summary>Builds a 2-item AVIF: a primary colour `av01` item (item 1) and a monochrome alpha auxiliary
    /// `av01` item (item 2) linked by an `auxl` item reference (item 2 → item 1) with the standard alpha aux URN.
    /// Both items' OBUs share one mdat (colour first, then alpha) as two extents. Verified in ffmpeg/libavif.</summary>
    internal static byte[] BuildAvifWithAlpha(byte[] colorSeq, byte[] colorFrame, byte[] alphaSeq, byte[] alphaFrame,
        int width, int height, bool colorMonochrome, int bitDepth = 8, Av1PixelLayout layout = Av1PixelLayout.I420,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (colorMonochrome) layout = Av1PixelLayout.I400;
        return BuildContainer(Concat(colorSeq, colorFrame), Concat(alphaSeq, alphaFrame), width, height, bitDepth, layout, color, extras);
    }

    // colr nclx. Legacy (no description given): unspecified primaries/transfer, matrix Identity(0) for mono /
    // BT.601(6) for colour, full range. Otherwise the exact CICP + full_range_flag (must match the sequence header).
    private static byte[] ColrNclx(Av1ObuWriter.Av1ColorDesc? color, bool monochrome)
        => color is { } c
            ? Box("colr", Concat(Fourcc("nclx"), U16((ushort)c.Primaries), U16((ushort)c.Transfer), U16((ushort)c.Matrix),
                new byte[] { (byte)(c.FullRange ? 0x80 : 0x00) }))
            : Box("colr", Concat(Fourcc("nclx"), U16(2), U16(2), U16(monochrome ? 0 : 6), new byte[] { 0x80 }));

    // AV1CodecConfigurationRecord (av1C payload): the fixed 4-byte record only (configOBUs omitted, as libaom's
    // AVIF output does — the sequence header travels in-band in mdat).
    private static byte[] BuildAv1C(Av1PixelLayout layout, int bitDepth, int width, int height)
    {
        // seq_level_idx_0 = 0 (matches Av1ObuWriter's reduced-still header), tier 0. seq_profile / high_bitdepth /
        // twelve_bit / subsampling mirror the sequence header's color_config.
        int profile = Av1ObuWriter.SeqProfile(bitDepth, layout);
        byte b0 = 0x81;                       // marker(1)=1 | version(7)=1
        byte b1 = (byte)((profile << 5) | Av1ObuWriter.SeqLevelIdx(width, height));   // seq_profile(3) | seq_level_idx_0(5)
        int monoBit = layout == Av1PixelLayout.I400 ? 1 : 0;
        // AV1 sets subsampling 1,1 for monochrome (I400) and 4:2:0; 4:2:2 is 1,0; 4:4:4 is 0,0.
        int cssX = layout == Av1PixelLayout.I444 ? 0 : 1;
        int cssY = layout == Av1PixelLayout.I422 || layout == Av1PixelLayout.I444 ? 0 : 1;
        byte b2 = (byte)(
            (0 << 7) |                        // seq_tier_0
            ((bitDepth > 8 ? 1 : 0) << 6) |   // high_bitdepth
            ((bitDepth == 12 ? 1 : 0) << 5) | // twelve_bit
            (monoBit << 4) |                  // monochrome
            (cssX << 3) |                     // chroma_subsampling_x
            (cssY << 2) |                     // chroma_subsampling_y
            0);                               // chroma_sample_position (2 bits) = 0
        byte b3 = 0x00;                       // reserved(3)=0 | initial_presentation_delay_present(1)=0 | reserved(4)=0

        return new[] { b0, b1, b2, b3 };
    }

    // ftyp: major brand avif; compatible avif/mif1/miaf plus the AVIF profile brand the stream qualifies for —
    // MA1B (Baseline = AV1 Main profile), MA1A (Advanced = AV1 High profile, i.e. 8/10-bit 4:4:4); AV1
    // Professional streams (4:2:2, 12-bit) fit no AVIF profile brand, so none is claimed (as libavif does).
    private static byte[] Ftyp(int bitDepth, Av1PixelLayout layout)
    {
        int profile = Av1ObuWriter.SeqProfile(bitDepth, layout);
        var brands = Concat(Fourcc("avif"), U32(0), Fourcc("avif"), Fourcc("mif1"), Fourcc("miaf"));
        return Box("ftyp", profile switch
        {
            0 => Concat(brands, Fourcc("MA1B")),
            1 => Concat(brands, Fourcc("MA1A")),
            _ => brands,
        });
    }

    // One container builder for every layout. Item IDs: 1 = colour (primary), 2 = alpha (if any), then Exif, XMP.
    // Property order: ispe, pixi, av1C, [colr prof], colr nclx, [pasp, clli, mdcv, clap, irot, imir], then the alpha
    // item's av1C / auxC / pixi. With no extras this reproduces the original single/2-item byte layout exactly.
    private static byte[] BuildContainer(byte[] colorData, byte[]? alphaData, int width, int height, int bitDepth,
        Av1PixelLayout layout, Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? x)
    {
        bool monochrome = layout == Av1PixelLayout.I400;
        byte[] ftyp = Ftyp(bitDepth, layout);

        var props = new List<byte[]>();
        int Add(byte[] box) { props.Add(box); return props.Count; }
        var assoc1 = new List<(int Index, bool Essential)>();
        int ispeIdx = Add(FullBox("ispe", 0, 0, Concat(U32((uint)width), U32((uint)height))));
        assoc1.Add((ispeIdx, false));
        int ch = monochrome ? 1 : 3;
        var pixi = new byte[1 + ch];
        pixi[0] = (byte)ch;
        for (int i = 1; i <= ch; i++) pixi[i] = (byte)bitDepth;
        assoc1.Add((Add(FullBox("pixi", 0, 0, pixi)), false));
        assoc1.Add((Add(Box("av1C", BuildAv1C(layout, bitDepth, width, height))), true));
        if (x?.Icc is { Length: > 0 } icc) assoc1.Add((Add(Box("colr", Concat(Fourcc("prof"), icc))), false));
        assoc1.Add((Add(ColrNclx(color, monochrome)), false));
        if (x?.Pasp is { } pasp) assoc1.Add((Add(Box("pasp", Concat(U32(pasp.H), U32(pasp.V)))), false));
        if (x?.Clli is { } clli) assoc1.Add((Add(Box("clli", Concat(U16(clli.MaxCll), U16(clli.MaxPall)))), false));
        if (x?.Mdcv is { Length: 24 } mdcv) assoc1.Add((Add(Box("mdcv", mdcv)), false));
        var transforms = new List<(int Index, bool Essential)>();   // shared with the alpha item (libavif >= 1.3)
        if (x?.Clap is { Length: 8 } clap)
        {
            var cb = new byte[32];
            for (int i = 0; i < 8; i++) WriteU32(cb, i * 4, clap[i]);
            transforms.Add((Add(Box("clap", cb)), true));
        }
        if (x?.IrotAngle is { } angle) transforms.Add((Add(Box("irot", new[] { (byte)(angle & 3) })), true));
        if (x?.ImirAxis is { } axis) transforms.Add((Add(Box("imir", new[] { (byte)(axis & 1) })), true));
        assoc1.AddRange(transforms);

        var assoc2 = new List<(int Index, bool Essential)>();
        if (alphaData != null)
        {
            assoc2.Add((ispeIdx, false));
            assoc2.Add((Add(Box("av1C", BuildAv1C(Av1PixelLayout.I400, bitDepth, width, height))), true));
            // auxC: aux_type is a null-terminated URN string identifying the alpha plane.
            byte[] auxUrn = System.Text.Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0");
            assoc2.Add((Add(FullBox("auxC", 0, 0, auxUrn)), true));
            assoc2.Add((Add(FullBox("pixi", 0, 0, new byte[] { 1, (byte)bitDepth })), false));
            assoc2.AddRange(transforms);   // the alpha plane is transformed exactly like the colour image
        }

        // Items: (id, type, name, content type, payload).
        var items = new List<(int Id, string Type, byte[] Payload, byte[] InfeExtra)>
        {
            (1, "av01", colorData, new byte[] { 0 }),
        };
        if (alphaData != null) items.Add((2, "av01", alphaData, new byte[] { 0 }));
        int nextId = items.Count + 1;
        int exifId = 0, xmpId = 0;
        if (x?.Exif is { Length: > 0 } exif)
        {
            exifId = nextId++;
            items.Add((exifId, "Exif", ExifItemPayload(exif), System.Text.Encoding.ASCII.GetBytes("Exif\0")));
        }
        if (x?.Xmp is { Length: > 0 } xmp)
        {
            xmpId = nextId++;
            items.Add((xmpId, "mime", xmp, System.Text.Encoding.ASCII.GetBytes("XMP\0application/rdf+xml\0")));
        }

        byte[] ipco = Box("ipco", Concat(props.ToArray()));
        var ipmaBody = new List<byte[]> { U32((uint)(alphaData != null ? 2 : 1)) };
        void Assoc(int id, List<(int Index, bool Essential)> a)
        {
            ipmaBody.Add(U16(id));
            ipmaBody.Add(new[] { (byte)a.Count });
            foreach (var (idx, ess) in a) ipmaBody.Add(new[] { (byte)((ess ? 0x80 : 0) | idx) });
        }
        Assoc(1, assoc1);
        if (alphaData != null) Assoc(2, assoc2);
        byte[] iprp = Box("iprp", Concat(ipco, FullBox("ipma", 0, 0, Concat(ipmaBody.ToArray()))));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0),
            System.Text.Encoding.ASCII.GetBytes("PictureHandler\0")));
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));
        var infes = new List<byte[]> { U16(items.Count) };
        foreach (var it in items) infes.Add(FullBox("infe", 2, 0, Concat(U16(it.Id), U16(0), Fourcc(it.Type), it.InfeExtra)));
        byte[] iinf = FullBox("iinf", 0, 0, Concat(infes.ToArray()));

        // iref (version 0): auxl alpha → colour; cdsc metadata → colour.
        var refs = new List<byte[]>();
        if (alphaData != null) refs.Add(Box("auxl", Concat(U16(2), U16(1), U16(1))));   // from_ID, ref_count, to_ID
        if (exifId != 0) refs.Add(Box("cdsc", Concat(U16(exifId), U16(1), U16(1))));
        if (xmpId != 0) refs.Add(Box("cdsc", Concat(U16(xmpId), U16(1), U16(1))));
        byte[]? iref = refs.Count > 0 ? FullBox("iref", 0, 0, Concat(refs.ToArray())) : null;

        // iloc: version 0, offset_size=4/length_size=4/base_offset_size=0; one extent per item. Offsets are patched
        // once the meta length (invariant to the offset values) is known.
        byte[] Iloc(uint firstOffset)
        {
            var body = new List<byte[]> { new byte[] { 0x44, 0x00 }, U16(items.Count) };
            uint off = firstOffset;
            foreach (var it in items)
            {
                body.Add(Concat(U16(it.Id), U16(0), U16(1), U32(off), U32((uint)it.Payload.Length)));
                off += (uint)it.Payload.Length;
            }
            return FullBox("iloc", 0, 0, Concat(body.ToArray()));
        }

        byte[] Meta(uint firstOffset) => FullBox("meta", 0, 0, iref != null
            ? Concat(hdlr, pitm, Iloc(firstOffset), iinf, iref, iprp)
            : Concat(hdlr, pitm, Iloc(firstOffset), iinf, iprp));

        int metaLen = Meta(0).Length;
        byte[] meta = Meta((uint)(ftyp.Length + metaLen + 8));   // + 8: mdat box header
        var payloads = new byte[items.Count][];
        for (int i = 0; i < items.Count; i++) payloads[i] = items[i].Payload;
        return Concat(ftyp, meta, Box("mdat", Concat(payloads)));
    }

    // HEIF Exif item: unsigned int(32) exif_tiff_header_offset, then the Exif block. The offset is the distance from
    // the block start to the TIFF header ("II*\0" / "MM\0*"), e.g. 6 when the block starts with "Exif\0\0".
    private static byte[] ExifItemPayload(byte[] exif)
    {
        int off = 0;
        for (int i = 0; i + 4 <= exif.Length; i++)
            if ((exif[i] == 'I' && exif[i + 1] == 'I' && exif[i + 2] == 42 && exif[i + 3] == 0) ||
                (exif[i] == 'M' && exif[i + 1] == 'M' && exif[i + 2] == 0 && exif[i + 3] == 42)) { off = i; break; }
        return Concat(U32((uint)off), exif);
    }

    private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(payload.Length + 8)), Fourcc(type), payload);

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
        => Box(type, Concat(new byte[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, payload));

    private static byte[] Fourcc(string s) => new[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };

    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static void WriteU32(byte[] buf, int pos, uint v)
    {
        buf[pos] = (byte)(v >> 24);
        buf[pos + 1] = (byte)(v >> 16);
        buf[pos + 2] = (byte)(v >> 8);
        buf[pos + 3] = (byte)v;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (byte[] p in parts)
        {
            len += p.Length;
        }

        var outb = new byte[len];
        int o = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, outb, o, p.Length);
            o += p.Length;
        }

        return outb;
    }
}
