// Assembles a complete HEIC (HEVC still image in ISOBMFF/HEIF) from an RGB frame: pads to the CTU
// grid, converts RGB->YUV 4:2:0 (full-range BT.601), runs the HEVC intra encoder, and muxes the
// coded slice + VPS/SPS/PPS into ftyp/meta/mdat boxes that SharpImage's own HeifCoder (and other
// HEIF readers) can decode.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Hevc;

internal static class HeicEncoder
{
    /// <summary>Encodes 8-bit RGB (row-major, channels: r,g,b[,a]) into a HEIC file.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, int channels, int qp, bool signDataHiding)
    {
        int pw = (width + 31) & ~31;
        int ph = (height + 31) & ~31;

        // RGB -> YUV 4:2:0, full-range BT.601 (JPEG), edge-replicated into the padded area.
        var luma = new byte[pw * ph];
        var cbFull = new int[pw * ph];
        var crFull = new int[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(y, height - 1);
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(x, width - 1);
                int o = ((sy * width) + sx) * channels;
                int r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                luma[(y * pw) + x] = (byte)Clamp8(((19595 * r) + (38470 * g) + (7471 * b) + 32768) >> 16);
                cbFull[(y * pw) + x] = Clamp8((((-11056 * r) - (21712 * g) + (32768 * b) + 8388608) >> 16));
                crFull[(y * pw) + x] = Clamp8((((32768 * r) - (27440 * g) - (5328 * b) + 8388608) >> 16));
            }
        }

        int cw = pw / 2, chh = ph / 2;
        var cb = new byte[cw * chh];
        var cr = new byte[cw * chh];
        for (int y = 0; y < chh; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int x2 = x * 2, y2 = y * 2;
                int sumCb = cbFull[(y2 * pw) + x2] + cbFull[(y2 * pw) + x2 + 1] + cbFull[((y2 + 1) * pw) + x2] + cbFull[((y2 + 1) * pw) + x2 + 1];
                int sumCr = crFull[(y2 * pw) + x2] + crFull[(y2 * pw) + x2 + 1] + crFull[((y2 + 1) * pw) + x2] + crFull[((y2 + 1) * pw) + x2 + 1];
                cb[(y * cw) + x] = (byte)((sumCb + 2) >> 2);
                cr[(y * cw) + x] = (byte)((sumCr + 2) >> 2);
            }
        }

        // Encode the slice.
        var enc = new HevcIntraFrameEncoder(luma, cb, cr, pw, ph, qp, signDataHiding);
        byte[] cabac = enc.EncodeSliceData();

        var hdr = new HevcBitWriter();
        HevcStreamWriter.WriteSliceHeader(hdr);
        byte[] sliceRbsp = Concat(hdr.ToArray(), cabac);
        byte[] sliceNal = HevcStreamWriter.WrapNal(HevcStreamWriter.NalIdrWRadl, sliceRbsp);

        // Parameter sets (NAL units, for the hvcC arrays).
        byte[] vps = HevcStreamWriter.WrapNal(HevcStreamWriter.NalVps, HevcStreamWriter.BuildVps());
        byte[] sps = HevcStreamWriter.WrapNal(HevcStreamWriter.NalSps, HevcStreamWriter.BuildSps(pw, ph, pw - width, ph - height));
        byte[] pps = HevcStreamWriter.WrapNal(HevcStreamWriter.NalPps, HevcStreamWriter.BuildPps(qp - 26, signDataHiding));

        byte[] hvcC = BuildHvcC(vps, sps, pps);

        // mdat = 4-byte-length-prefixed slice NAL.
        var mdatPayload = new List<byte>();
        AppendU32(mdatPayload, (uint)sliceNal.Length);
        mdatPayload.AddRange(sliceNal);

        return BuildIsoBmff(width, height, hvcC, mdatPayload.ToArray());
    }

    private static byte[] BuildHvcC(byte[] vps, byte[] sps, byte[] pps)
    {
        var c = new List<byte>();
        c.Add(1);           // configurationVersion
        c.Add(0x01);        // profile_space(0)|tier(0)|profile_idc(1)
        AppendU32(c, 0x60000000); // general_profile_compatibility_flags
        for (int i = 0; i < 6; i++) c.Add(0); // general_constraint_indicator_flags (48 bits)
        c.Add(153);         // general_level_idc = 5.1
        c.Add(0xF0); c.Add(0x00); // reserved(4=1)|min_spatial_segmentation_idc(12)=0
        c.Add(0xFC);        // reserved(6=1)|parallelismType(2)=0
        c.Add(0xFD);        // reserved(6=1)|chromaFormat(2)=1 (4:2:0)
        c.Add(0xF8);        // reserved(5=1)|bitDepthLumaMinus8(3)=0
        c.Add(0xF8);        // reserved(5=1)|bitDepthChromaMinus8(3)=0
        c.Add(0); c.Add(0); // avgFrameRate
        c.Add(0x0F);        // constantFrameRate(0)|numTemporalLayers(1)|temporalIdNested(1)|lengthSizeMinusOne(3)
        c.Add(3);           // numOfArrays

        AppendHvcCArray(c, HevcStreamWriter.NalVps, vps);
        AppendHvcCArray(c, HevcStreamWriter.NalSps, sps);
        AppendHvcCArray(c, HevcStreamWriter.NalPps, pps);
        return c.ToArray();
    }

    private static void AppendHvcCArray(List<byte> c, int nalType, byte[] nal)
    {
        c.Add((byte)(0x80 | (nalType & 0x3F))); // array_completeness=1 | reserved | NAL_unit_type
        c.Add(0); c.Add(1);                     // numNalus = 1
        c.Add((byte)(nal.Length >> 8)); c.Add((byte)(nal.Length & 0xFF));
        c.AddRange(nal);
    }

    private static byte[] BuildIsoBmff(int width, int height, byte[] hvcC, byte[] mdatPayload)
    {
        byte[] ftyp = Box("ftyp", Concat(Fourcc("heic"), U32(0), Fourcc("mif1"), Fourcc("heic")));

        // property container: ipco { hvcC, ispe, colr }
        byte[] hvcCBox = Box("hvcC", hvcC);
        byte[] ispe = FullBox("ispe", 0, 0, Concat(U32((uint)width), U32((uint)height)));
        byte[] colr = Box("colr", Concat(Fourcc("nclx"), U16(1), U16(13), U16(6), new byte[] { 0x80 })); // BT.601, full range
        byte[] ipco = Box("ipco", Concat(hvcCBox, ispe, colr));

        // ipma: item 1 -> properties 1,2,3 (hvcC, ispe, colr), hvcC marked essential.
        byte[] ipmaPayload = Concat(
            U32(1),                 // entry_count
            U16(1),                 // item_id = 1
            new byte[] { 3 },       // association_count
            new byte[] { 0x81 },    // essential | property_index 1 (hvcC)
            new byte[] { 0x02 },    // property_index 2 (ispe)
            new byte[] { 0x03 });   // property_index 3 (colr)
        byte[] ipma = FullBox("ipma", 0, 0, ipmaPayload);
        byte[] iprp = Box("iprp", Concat(ipco, ipma));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0), new byte[] { 0 }));
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));
        // iinf { infe (item 1, type hvc1) }
        byte[] infe = FullBox("infe", 2, 0, Concat(U16(1), U16(0), Fourcc("hvc1"), new byte[] { 0 }));
        byte[] iinf = FullBox("iinf", 0, 0, Concat(U16(1), infe));

        // iloc: version 1, offset_size=4, length_size=4, base_offset_size=0; one item, one extent.
        // The extent offset points at the start of the mdat payload (filled in after layout).
        // Build meta with a placeholder offset, then patch.
        int mdatOffsetPlaceholder = 0;
        byte[] ilocPayload = Concat(
            new byte[] { 0x44 },    // offset_size(4)=4 | length_size(4)=4
            new byte[] { 0x00 },    // base_offset_size(4)=0 | reserved(4)
            U16(1),                 // item_count
            U16(1),                 // item_id
            U16(0),                 // construction_method(4)+reserved(12)=0 (v1)
            U16(0),                 // data_reference_index
            U16(1),                 // extent_count
            U32((uint)mdatOffsetPlaceholder), // extent_offset (patched)
            U32((uint)mdatPayload.Length));   // extent_length
        // iloc offset field position within ilocPayload: after [0x44][0x00][item_count:2][item_id:2]
        // [cm:2][dri:2][ec:2] = 1+1+2+2+2+2+2 = 12 bytes, then extent_offset(4).
        byte[] iloc = FullBox("iloc", 1, 0, ilocPayload);

        byte[] metaPayload = Concat(hdlr, pitm, iinf, iprp, iloc);
        byte[] meta = FullBox("meta", 0, 0, metaPayload);

        byte[] mdat = Box("mdat", mdatPayload);

        // Layout: ftyp | meta | mdat. Compute the absolute offset of mdatPayload start.
        int mdatBoxOffset = ftyp.Length + meta.Length;
        int mdatPayloadOffset = mdatBoxOffset + 8; // box header (size+type)

        // Patch the iloc extent_offset inside `meta`.
        // Find the iloc box within meta and patch its extent_offset. Its layout offset is stable:
        int ilocOffsetInMeta = meta.Length - iloc.Length + 8 /*box header*/ + 4 /*fullbox ver+flags*/ + 12 /*to extent_offset*/;
        WriteU32(meta, ilocOffsetInMeta, (uint)mdatPayloadOffset);

        return Concat(ftyp, meta, mdat);
    }

    // ---- box helpers ----
    private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(payload.Length + 8)), Fourcc(type), payload);

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
        => Box(type, Concat(new byte[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, payload));

    private static byte[] Fourcc(string s) => new[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };

    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static void AppendU32(List<byte> l, uint v)
    {
        l.Add((byte)(v >> 24));
        l.Add((byte)(v >> 16));
        l.Add((byte)(v >> 8));
        l.Add((byte)v);
    }

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
        foreach (byte[] p in parts) len += p.Length;
        var outp = new byte[len];
        int o = 0;
        foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }

    private static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
