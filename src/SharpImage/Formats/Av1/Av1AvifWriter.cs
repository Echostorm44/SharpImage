// AVIF (AV1 Image File Format) ISOBMFF container writer. AVIF is a HEIF/MIAF file whose primary item is an
// AV1-coded image: an `av01` item described by an `av1C` config box (carrying the sequence-header OBU) with the
// frame OBUs in `mdat`. Mirrors the box structure of the HEIC encoder, swapping hvc1/hvcC for av01/av1C.
// Verified: files open in ffmpeg/libdav1d (and standard AVIF viewers).
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

internal static class Av1AvifWriter
{
    /// <summary>Builds a complete .avif file. <paramref name="seqObu"/> is the sequence-header OBU (size-field
    /// form); it is placed both in av1C configOBUs (for decoders that configure from it) and in-band at the head
    /// of the mdat item data (for decoders that decode the item as a self-contained temporal unit — including
    /// SharpImage's own). <paramref name="frameObu"/> is the OBU_FRAME.</summary>
    internal static byte[] BuildAvif(byte[] seqObu, byte[] frameObu, int width, int height, bool monochrome, int bitDepth = 8)
    {
        byte[] av1C = BuildAv1C(monochrome, bitDepth);
        // Sequence header + frame OBUs go in-band in mdat (matches libaom/ffmpeg AVIF output and lets any AV1
        // decoder treat the item as a self-contained temporal unit).
        var mdat = new byte[seqObu.Length + frameObu.Length];
        seqObu.CopyTo(mdat, 0);
        frameObu.CopyTo(mdat, seqObu.Length);
        return BuildIsoBmff(width, height, av1C, mdat, monochrome, bitDepth);
    }

    // AV1CodecConfigurationRecord (av1C payload): the fixed 4-byte record only (configOBUs omitted, as libaom's
    // AVIF output does — the sequence header travels in-band in mdat).
    private static byte[] BuildAv1C(bool monochrome, int bitDepth)
    {
        // seq_level_idx_0 = 0 (matches Av1ObuWriter's reduced-still header), tier 0. seq_profile / high_bitdepth /
        // twelve_bit mirror the sequence header's color_config (profile 2 for 12-bit, else 0).
        int profile = bitDepth == 12 ? 2 : 0;
        byte b0 = 0x81;                       // marker(1)=1 | version(7)=1
        byte b1 = (byte)(profile << 5);       // seq_profile(3) | seq_level_idx_0(5)=0
        int monoBit = monochrome ? 1 : 0;
        // AV1 sets subsampling 1,1 for monochrome (I400); colour here is I420 (also 1,1).
        int cssX = 1;
        int cssY = 1;
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

    /// <summary>Builds a 2-item AVIF: a primary colour `av01` item (item 1) and a monochrome alpha auxiliary
    /// `av01` item (item 2) linked by an `auxl` item reference (item 2 → item 1) with the standard alpha aux URN.
    /// Both items' OBUs share one mdat (colour first, then alpha) as two extents. Verified in ffmpeg/libavif.</summary>
    internal static byte[] BuildAvifWithAlpha(byte[] colorSeq, byte[] colorFrame, byte[] alphaSeq, byte[] alphaFrame,
        int width, int height, bool colorMonochrome, int bitDepth = 8)
    {
        var colorMdat = new byte[colorSeq.Length + colorFrame.Length];
        colorSeq.CopyTo(colorMdat, 0);
        colorFrame.CopyTo(colorMdat, colorSeq.Length);
        var alphaMdat = new byte[alphaSeq.Length + alphaFrame.Length];
        alphaSeq.CopyTo(alphaMdat, 0);
        alphaFrame.CopyTo(alphaMdat, alphaSeq.Length);

        byte[] av1CColor = Box("av1C", BuildAv1C(colorMonochrome, bitDepth));
        byte[] av1CAlpha = Box("av1C", BuildAv1C(true, bitDepth));

        // ipco properties (1-indexed): 1 ispe (shared), 2 pixi(colour), 3 av1C(colour), 4 colr,
        // 5 av1C(alpha), 6 auxC(alpha URN), 7 pixi(alpha, 1ch).
        byte[] ispe = FullBox("ispe", 0, 0, Concat(U32((uint)width), U32((uint)height)));
        int cch = colorMonochrome ? 1 : 3;
        var pixiC = new List<byte> { (byte)cch };
        for (int i = 0; i < cch; i++) pixiC.Add((byte)bitDepth);
        byte[] pixiColor = FullBox("pixi", 0, 0, pixiC.ToArray());
        byte[] colr = Box("colr", Concat(Fourcc("nclx"), U16(2), U16(2), U16(colorMonochrome ? 0 : 6), new byte[] { 0x80 }));
        byte[] pixiAlpha = FullBox("pixi", 0, 0, new byte[] { 1, (byte)bitDepth });
        // auxC: aux_type is a null-terminated URN string identifying the alpha plane.
        byte[] auxUrn = System.Text.Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0");
        byte[] auxC = FullBox("auxC", 0, 0, auxUrn);
        byte[] ipco = Box("ipco", Concat(ispe, pixiColor, av1CColor, colr, av1CAlpha, auxC, pixiAlpha));

        // ipma: item 1 → ispe(1), pixi(2), av1C essential(3), colr(4); item 2 → ispe(1), av1C essential(5),
        // auxC essential(6), pixi(7).
        byte[] ipmaPayload = Concat(
            U32(2),                              // entry_count
            U16(1), new byte[] { 4 },            // item 1, 4 associations
            new byte[] { 0x01, 0x02, 0x83, 0x04 },
            U16(2), new byte[] { 4 },            // item 2, 4 associations
            new byte[] { 0x01, 0x85, 0x86, 0x07 });
        byte[] ipma = FullBox("ipma", 0, 0, ipmaPayload);
        byte[] iprp = Box("iprp", Concat(ipco, ipma));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0),
            System.Text.Encoding.ASCII.GetBytes("PictureHandler\0")));
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));

        byte[] infeColor = FullBox("infe", 2, 0, Concat(U16(1), U16(0), Fourcc("av01"), new byte[] { 0 }));
        byte[] infeAlpha = FullBox("infe", 2, 0, Concat(U16(2), U16(0), Fourcc("av01"), new byte[] { 0 }));
        byte[] iinf = FullBox("iinf", 0, 0, Concat(U16(2), infeColor, infeAlpha));

        // iref (version 0): auxl from item 2 → item 1 (alpha references its master image).
        byte[] auxl = Box("auxl", Concat(U16(2), U16(1), U16(1))); // from_ID, ref_count, to_ID
        byte[] iref = FullBox("iref", 0, 0, auxl);

        // iloc: version 0, offset_size=4/length_size=4/base_offset_size=0; two items, one extent each. Offsets are
        // patched after the meta layout is known (meta length is invariant to the offset values).
        byte[] BuildIloc(uint colorOff, uint alphaOff) => FullBox("iloc", 0, 0, Concat(
            new byte[] { 0x44 }, new byte[] { 0x00 }, U16(2),
            U16(1), U16(0), U16(1), U32(colorOff), U32((uint)colorMdat.Length),
            U16(2), U16(0), U16(1), U32(alphaOff), U32((uint)alphaMdat.Length)));

        byte[] MetaWith(uint colorOff, uint alphaOff)
        {
            byte[] metaPayload = Concat(hdlr, pitm, BuildIloc(colorOff, alphaOff), iinf, iref, iprp);
            return FullBox("meta", 0, 0, metaPayload);
        }

        byte[] ftyp = Ftyp(bitDepth);

        int metaLen = MetaWith(0, 0).Length;               // invariant to offset values
        int mdatPayloadOffset = ftyp.Length + metaLen + 8; // +8 mdat box header
        byte[] meta = MetaWith((uint)mdatPayloadOffset, (uint)(mdatPayloadOffset + colorMdat.Length));

        var mdatPayload = new byte[colorMdat.Length + alphaMdat.Length];
        colorMdat.CopyTo(mdatPayload, 0);
        alphaMdat.CopyTo(mdatPayload, colorMdat.Length);
        byte[] mdat = Box("mdat", mdatPayload);

        return Concat(ftyp, meta, mdat);
    }

    // ftyp: major brand avif; compatible avif/mif1/miaf plus the AVIF profile brand the stream qualifies for —
    // MA1B (Baseline = AV1 Main profile) for 8/10-bit; 12-bit (AV1 Professional) fits no AVIF profile brand, so
    // none is claimed (as libavif does).
    private static byte[] Ftyp(int bitDepth) => bitDepth == 12
        ? Box("ftyp", Concat(Fourcc("avif"), U32(0), Fourcc("avif"), Fourcc("mif1"), Fourcc("miaf")))
        : Box("ftyp", Concat(Fourcc("avif"), U32(0), Fourcc("avif"), Fourcc("mif1"), Fourcc("miaf"), Fourcc("MA1B")));

    private static byte[] BuildIsoBmff(int width, int height, byte[] av1C, byte[] mdatPayload, bool monochrome, int bitDepth)
    {
        byte[] ftyp = Ftyp(bitDepth);

        // Property container ipco { ispe, pixi, av1C, colr } — matching libaom/ffmpeg AVIF order so av1C is the
        // 3rd (essential) property.
        byte[] av1CBox = Box("av1C", av1C);
        byte[] ispe = FullBox("ispe", 0, 0, Concat(U32((uint)width), U32((uint)height)));
        int channels = monochrome ? 1 : 3;
        var pixiPayload = new List<byte> { (byte)channels };
        for (int i = 0; i < channels; i++)
        {
            pixiPayload.Add((byte)bitDepth); // bits per channel
        }

        byte[] pixi = FullBox("pixi", 0, 0, pixiPayload.ToArray());
        // colr nclx: unspecified primaries/transfer, matrix Identity(0)/BT.601(6), full range.
        byte[] colr = Box("colr", Concat(Fourcc("nclx"), U16(2), U16(2), U16(monochrome ? 0 : 6), new byte[] { 0x80 }));
        byte[] ipco = Box("ipco", Concat(ispe, pixi, av1CBox, colr));

        // ipma: item 1 → properties 1..4 (ispe, pixi, av1C essential = index 3, colr).
        byte[] ipmaPayload = Concat(
            U32(1),                 // entry_count
            U16(1),                 // item_ID = 1
            new byte[] { 4 },       // association_count
            new byte[] { 0x01 },    // property_index 1 (ispe)
            new byte[] { 0x02 },    // property_index 2 (pixi)
            new byte[] { 0x83 },    // essential | property_index 3 (av1C)
            new byte[] { 0x04 });   // property_index 4 (colr)
        byte[] ipma = FullBox("ipma", 0, 0, ipmaPayload);
        byte[] iprp = Box("iprp", Concat(ipco, ipma));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0),
            System.Text.Encoding.ASCII.GetBytes("PictureHandler\0")));
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));
        // iinf { infe (item 1, type av01) }
        byte[] infe = FullBox("infe", 2, 0, Concat(U16(1), U16(0), Fourcc("av01"), new byte[] { 0 }));
        byte[] iinf = FullBox("iinf", 0, 0, Concat(U16(1), infe));

        // iloc: version 0 (no construction_method field), offset_size=4, length_size=4, base_offset_size=0;
        // one item, one extent.
        byte[] ilocPayload = Concat(
            new byte[] { 0x44 },              // offset_size(4) | length_size(4)
            new byte[] { 0x00 },              // base_offset_size(0) | reserved(0)
            U16(1),                           // item_count
            U16(1),                           // item_ID
            U16(0),                           // data_reference_index
            U16(1),                           // extent_count
            U32(0),                           // extent_offset (patched after layout)
            U32((uint)mdatPayload.Length));   // extent_length
        byte[] iloc = FullBox("iloc", 0, 0, ilocPayload);

        // Box order matches the reference: hdlr, pitm, iloc, iinf, iprp.
        byte[] metaPayload = Concat(hdlr, pitm, iloc, iinf, iprp);
        byte[] meta = FullBox("meta", 0, 0, metaPayload);

        byte[] mdat = Box("mdat", mdatPayload);

        int mdatBoxOffset = ftyp.Length + meta.Length;
        int mdatPayloadOffset = mdatBoxOffset + 8; // box header (size + type)

        // Patch the iloc extent_offset inside meta. iloc is now the 3rd child; locate it by offset from the start
        // of metaPayload: after hdlr + pitm, then its own 8(box header)+4(fullbox)+10(to extent_offset).
        int ilocStartInMeta = 8 /*meta box hdr*/ + 4 /*meta fullbox*/ + hdlr.Length + pitm.Length;
        int ilocOffsetInMeta = ilocStartInMeta + 8 + 4 + 10;
        WriteU32(meta, ilocOffsetInMeta, (uint)mdatPayloadOffset);

        return Concat(ftyp, meta, mdat);
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
