using System;
using System.Buffers.Binary;
using System.IO;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// HEIF container parity with libavif's own test corpus (tests/data, BSD): image grids (dimg order, stitched YUV so
// chroma upsampling crosses tile seams), per-tile alpha without an alpha grid, idat-stored items (construction_method
// 1, including meta size 0), multi-extent items, and invalid grids rejected like libavif. Reference pixels are
// libavif 1.3's own RGBA output (via Pillow 12.1), zero tolerance.
public sealed class AvifContainerTests
{
    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    [Test]
    [Arguments("libavif_sofa_grid1x5_420.avif", 1024, 770, "(0,0,187,187,189,255), (769,1023,33,34,36,255), (0,1023,25,29,30,255), (769,0,101,109,112,255), (463,953,15,32,58,255), (462,388,26,29,38,255), (189,974,57,16,4,255), (644,381,5,22,64,255), (96,914,102,58,21,255), (310,290,110,124,151,255), (92,85,185,185,185,255), (609,811,56,58,55,255), (463,322,94,104,114,255), (638,30,136,141,144,255), (541,129,28,30,27,255), (60,73,185,185,183,255), (194,495,52,64,64,255), (614,61,125,129,130,255), (475,668,49,56,83,255), (451,400,43,46,55,255), (531,478,14,19,47,255), (655,602,141,133,120,255), (511,9,147,143,132,255), (678,174,106,115,122,255), (468,569,30,35,63,255), (416,170,69,86,96,255), (724,520,122,121,116,255), (322,470,158,145,152,255), (525,591,28,37,68,255), (30,143,161,161,161,255), (576,221,79,86,94,255), (410,220,167,154,112,255), (153,500,64,64,64,255), (154,500,63,65,64,255), (155,500,60,62,61,255), (307,20,186,178,165,255), (308,20,183,177,165,255), (461,999,66,83,93,255), (462,999,68,86,98,255), (616,3,115,120,123,255), (615,3,117,121,122,255)")]
    [Arguments("libavif_sofa_grid1x5_420_reversed_dimg_order.avif", 1024, 770, "(0,0,115,120,123,255), (769,1023,86,40,14,255), (0,1023,14,13,8,255), (769,0,247,244,213,255), (463,953,95,45,20,255), (462,388,64,64,66,255), (189,974,11,22,40,255), (644,381,105,106,110,255), (96,914,137,126,98,255), (310,290,110,124,151,255), (92,85,112,118,118,255), (609,811,23,33,45,255), (463,322,116,111,107,255), (638,30,189,189,191,255), (541,129,255,254,247,255), (60,73,122,126,127,255), (194,495,11,17,43,255), (614,61,179,189,188,255), (475,668,214,147,32,255), (451,400,43,46,55,255), (531,478,100,110,102,255), (655,602,43,35,24,255), (511,9,166,164,143,255), (678,174,193,194,178,255), (468,569,100,53,35,255), (416,170,69,86,96,255), (724,520,135,70,20,255), (322,470,158,145,152,255), (525,591,165,164,170,255), (30,143,101,110,117,255), (576,221,89,93,92,255), (410,220,167,154,112,255), (153,500,108,109,111,255), (154,500,48,51,70,255), (155,500,46,50,77,255), (307,20,121,123,122,255), (308,20,180,177,168,255), (461,999,79,79,79,255), (462,999,83,52,33,255), (616,3,188,187,185,255), (615,3,181,174,164,255)")]
    [Arguments("libavif_draw_points_idat.avif", 33, 11, "(0,0,255,0,0,255), (10,32,0,0,253,136), (0,32,0,0,253,136), (10,0,255,0,0,255), (7,29,0,0,253,136), (7,32,0,0,253,136), (9,12,118,0,136,255), (2,32,0,0,253,136), (7,11,118,0,136,255), (1,28,0,0,253,136), (4,9,255,0,0,255), (1,2,255,0,0,255), (9,25,0,0,253,136), (7,10,255,0,0,255), (9,0,255,0,0,255), (8,4,255,0,0,255), (0,2,255,0,0,255), (3,15,118,0,136,255), (9,1,255,0,0,255), (7,20,118,0,136,255), (7,12,118,0,136,255), (8,14,118,0,136,255), (10,18,118,0,136,255), (7,0,255,0,0,255), (10,5,255,0,0,255), (7,17,118,0,136,255), (6,5,255,0,0,255), (4,20,118,0,136,255), (3,32,0,0,253,136), (4,1,255,0,0,255), (1,6,255,0,0,255), (6,6,255,0,0,255)")]
    [Arguments("libavif_draw_points_idat_metasize0.avif", 33, 11, "(0,0,255,0,0,255), (10,32,0,0,253,136), (0,32,0,0,253,136), (10,0,255,0,0,255), (7,29,0,0,253,136), (7,32,0,0,253,136), (9,12,118,0,136,255), (2,32,0,0,253,136), (7,11,118,0,136,255), (1,28,0,0,253,136), (4,9,255,0,0,255), (1,2,255,0,0,255), (9,25,0,0,253,136), (7,10,255,0,0,255), (9,0,255,0,0,255), (8,4,255,0,0,255), (0,2,255,0,0,255), (3,15,118,0,136,255), (9,1,255,0,0,255), (7,20,118,0,136,255), (7,12,118,0,136,255), (8,14,118,0,136,255), (10,18,118,0,136,255), (7,0,255,0,0,255), (10,5,255,0,0,255), (7,17,118,0,136,255), (6,5,255,0,0,255), (4,20,118,0,136,255), (3,32,0,0,253,136), (4,1,255,0,0,255), (1,6,255,0,0,255), (6,6,255,0,0,255)")]
    [Arguments("libavif_color_grid_alpha_nogrid.avif", 80, 80, "(0,0,0,0,0,0), (79,79,0,0,0,0), (0,79,0,0,0,0), (79,0,0,1,0,0), (57,71,77,154,6,255), (59,57,79,156,7,255), (65,75,20,38,0,134), (24,23,128,214,45,255), (65,60,78,156,8,255), (78,23,0,0,0,0), (12,57,255,246,82,234), (38,18,116,202,35,255), (11,68,255,248,11,54), (5,76,0,0,0,0), (50,57,86,163,12,255), (78,20,0,0,0,0), (79,1,0,1,0,0), (67,8,109,186,46,255), (7,4,0,0,0,0), (24,30,123,209,40,255), (76,3,0,1,0,0), (59,41,86,165,15,255), (56,75,21,48,5,140), (25,66,138,197,21,255), (29,37,115,201,34,255), (63,0,0,1,4,0), (10,58,255,243,38,216), (35,52,104,182,24,255), (70,10,60,110,12,255), (32,40,55,106,3,255), (29,65,105,185,25,255), (36,3,0,0,2,39)")]
    [Arguments("libavif_paris_icc_exif_xmp.avif", 403, 302, "(0,0,113,163,214,255), (301,402,45,49,48,255), (0,402,126,171,219,255), (301,0,43,46,27,255), (231,286,65,70,48,255), (238,231,66,72,36,255), (260,300,43,51,36,255), (97,94,138,186,222,255), (262,243,42,44,20,255), (95,48,142,186,223,255), (228,155,77,65,53,255), (72,46,117,155,192,255), (275,355,126,115,109,255), (21,304,119,167,216,255), (202,231,193,196,177,255), (80,319,128,178,227,255), (7,270,125,160,192,255), (32,30,121,171,222,255), (18,97,106,130,156,255), (123,307,191,180,184,255), (15,398,122,168,218,255), (237,167,60,52,39,255), (225,302,91,83,78,255), (100,265,136,182,224,255), (119,327,147,185,216,255), (150,255,183,157,140,255), (2,339,111,161,212,255), (43,234,181,184,201,255), (142,208,205,173,150,255), (282,42,45,49,34,255), (130,161,187,168,164,255), (117,262,222,203,188,255)")]
    // Premultiplied alpha ('prem'): colour is un-premultiplied after libyuv's conversion by ARGBUnattenuate (8-bit).
    [Arguments("sharpimage_8bit_420_premultiplied.avif", 96, 64, "(0,0,0,138,242,52), (63,95,250,216,58,255), (0,95,201,10,85,51), (63,0,54,201,214,254), (33,37,104,130,173,157), (23,83,195,16,102,125), (29,85,205,38,95,145), (18,28,72,228,193,110), (23,16,51,246,217,125), (9,68,154,64,134,80), (27,95,228,70,79,138), (37,3,34,243,226,171), (55,16,78,153,195,229), (1,35,76,237,204,54), (18,10,34,233,221,110), (33,57,150,31,135,157), (55,17,80,146,191,229), (32,45,120,89,163,155), (29,62,153,26,132,145), (54,85,222,133,75,226), (46,55,155,15,132,200), (40,83,210,63,88,181), (15,44,107,169,169,100), (33,88,214,64,86,158), (57,70,195,65,101,236), (17,56,135,92,150,106), (56,68,191,51,103,232), (22,37,96,176,178,122), (25,22,66,231,206,132), (45,32,102,105,171,197), (47,58,162,12,126,203), (33,77,191,24,107,158)")]
    public async Task Rgba_MatchesLibavifExactly(string file, int w, int h, string refs)
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset(file)));
        await Assert.That((int)img.Columns).IsEqualTo(w);
        await Assert.That((int)img.Rows).IsEqualTo(h);
        int ch = img.NumberOfChannels, worst = 0;
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(refs, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)"))
        {
            var g = ((System.Text.RegularExpressions.Match)m).Groups;
            int y = int.Parse(g[1].Value), x = int.Parse(g[2].Value);
            var row = img.GetPixelRow(y);
            for (int c = 0; c < 4; c++)
            {
                int ours = c < 3 ? (int)Math.Round(row[x * ch + c] * 255.0 / 65535.0)
                                 : img.HasAlpha ? (int)Math.Round(row[x * ch + ch - 1] * 255.0 / 65535.0) : 255;
                worst = Math.Max(worst, Math.Abs(ours - int.Parse(g[3 + c].Value)));
            }
        }
        await Assert.That(worst).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentDecodes_AreIdentical()
    {
        // Regression: a leftover debug file write in the palette path made concurrent decodes of palette/lossless
        // (screen-content) streams fail mid-frame and silently return blank images; decoder bit-depth state was
        // process-global. Every parallel decode must equal the sequential one.
        byte[] data = File.ReadAllBytes(Asset("libavif_draw_points_idat.avif"));
        var reference = HeifCoder.Decode(data);
        var results = new ImageFrame[16];
        System.Threading.Tasks.Parallel.For(0, results.Length, i => results[i] = HeifCoder.Decode(data));
        int mismatches = 0;
        foreach (var r in results)
            for (int y = 0; y < reference.Rows; y++)
                if (!r.GetPixelRow(y).SequenceEqual(reference.GetPixelRow(y))) { mismatches++; break; }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task Grid_WithRepeatedTile_IsRejected()
    {
        // dimg 2,3,4,5,5 — libavif: AVIF_RESULT_INVALID_IMAGE_GRID.
        await Assert.That(() => HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_sofa_grid1x5_420_dimg_repeat.avif"))))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task MultiExtentItem_WithGap_DecodesLikeSingleExtent()
    {
        // The libavif arc_triomphe case, rebuilt from our own file: the colour item split into two extents with a stray
        // byte between them in mdat. Must decode identically to the original.
        var src = new ImageFrame();
        src.Initialize(96, 64, ColorspaceType.SRGB, false);
        for (int y = 0; y < 64; y++)
        {
            var r = src.GetPixelRowForWrite(y);
            for (int x = 0; x < 96; x++) { r[x * 3] = (ushort)(x * 600); r[x * 3 + 1] = (ushort)(y * 900); r[x * 3 + 2] = 20000; }
        }
        byte[] one = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 10 });
        byte[] two = SplitPrimaryExtent(one, 100);
        var a = HeifCoder.Decode(one);
        var b = HeifCoder.Decode(two);
        long diff = 0;
        for (int y = 0; y < 64; y++)
        {
            var ra = a.GetPixelRow(y);
            var rb = b.GetPixelRow(y);
            for (int i = 0; i < ra.Length; i++) diff += Math.Abs(ra[i] - rb[i]);
        }
        await Assert.That(two.Length).IsEqualTo(one.Length + 9);
        await Assert.That(diff).IsEqualTo(0L);
    }

    // Rewrites our single-item file (iloc v0, 4-byte offsets/lengths, one extent) so the item has two extents — the
    // first `split` bytes, then the rest after one inserted 0 byte in mdat. meta grows by 8 bytes, mdat by 1.
    private static byte[] SplitPrimaryExtent(byte[] f, int split)
    {
        int iloc = IndexOf(f, "iloc") - 4;
        int extCountPos = iloc + 8 + 4 + 2 + 2 + 2 + 2;   // box header, fullbox, sizes, item_count, item_ID, data_ref
        uint off = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(extCountPos + 2));
        uint len = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(extCountPos + 6));
        var o = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.Write(b); }
        o.Write(f, 0, extCountPos);
        o.Write([0, 2]);
        U32(off + 8); U32((uint)split); U32(off + 8 + (uint)split + 1); U32(len - (uint)split);
        int after = extCountPos + 10;
        int mdat = IndexOf(f, "mdat") - 4;
        o.Write(f, after, mdat - after);
        U32(BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(mdat)) + 1);
        o.Write(f, mdat + 4, 4);
        o.Write(f, mdat + 8, split);
        o.WriteByte(0);
        o.Write(f, mdat + 8 + split, f.Length - (mdat + 8 + split));
        byte[] r = o.ToArray();
        foreach (string box in new[] { "meta", "iloc" })   // grow the enclosing box sizes by the extra extent
        {
            int p = IndexOf(r, box) - 4;
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(p), BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(p)) + 8);
        }
        return r;
    }

    private static int IndexOf(byte[] d, string four)
    {
        for (int i = 0; i + 4 <= d.Length; i++)
            if (d[i] == four[0] && d[i + 1] == four[1] && d[i + 2] == four[2] && d[i + 3] == four[3]) return i;
        throw new InvalidOperationException(four);
    }
}
