using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Decoder conformance against real third-party AVIFs (TestAssets/avif_conformance): current libavif (libaom) defaults
// use quantizer matrices + per-superblock delta-q, and libaom's delta-lf-mode adds per-superblock loop-filter deltas.
// Each stream's native-depth YUV planes must hash identically to ffmpeg/libdav1d's decode (golden MD5s computed
// with `ffmpeg -pix_fmt <native>`); the alpha asset's alpha plane must match libavif's decode exactly.
public sealed class Av1ConformanceDecodeTests
{
    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    [Test]
    [Arguments("libavif_8bit_444_qm_dq.avif", 384, 256, "2d05d5dc8e76405a2da4a71829ec84de")]
    [Arguments("libavif_10bit_420_qm_dq.avif", 384, 256, "e067e8a640ab96cb615d7400964bce49")]
    [Arguments("libavif_10bit_422_qm_dq.avif", 384, 256, "e0d43bdb2886357183ff1f678a3bfb06")]
    [Arguments("libavif_12bit_444_qm_dq.avif", 384, 256, "e6ce1414e1898f4c3ed2be6684a6f94d")]
    [Arguments("libaom_10bit_420_qm_dq_dlf.avif", 384, 256, "1f22d8cb02a9555a91d74b93fd269e64")]
    [Arguments("libavif_10bit_420_alpha.avif", 160, 96, "b70e1446b1e8656c5605b74ef678785b")]
    // Odd picture sizes with deblock + CDEF: the loop filter must use ceil(dim/4) (dav1d w4/h4), not the MI grid.
    [Arguments("libavif_10bit_420_257x131_lf_cdef.avif", 257, 131, "ee60cf6338c4bf402de5237263d49b94")]
    [Arguments("sharpimage_8bit_420_257x131_lf_cdef.avif", 257, 131, "7d0e70c2a59725c98f7746ca477e7b98")]
    [Arguments("sharpimage_8bit_420_129x67_lf_cdef.avif", 129, 67, "db65513a81a3faf76dcc1ffcf3fb2028")]
    // Our own 4:4:4 / 4:2:2 encodes (High / Professional profile), odd sizes included.
    [Arguments("sharpimage_8bit_444_257x131.avif", 257, 131, "613673def1183cbe89c433b293cca902")]
    [Arguments("sharpimage_8bit_422_384x256.avif", 384, 256, "0276804a28f4241f102ec42867a23495")]
    [Arguments("sharpimage_10bit_422_257x131.avif", 257, 131, "5488799f8f03fb637ed61970e93efc7b")]
    [Arguments("sharpimage_12bit_422_384x256.avif", 384, 256, "f68aaa6429308459eb317ab9b8832a78")]
    // Multi-tile frames (libaom via libavif, tilelog2): deblock strength is fixed up at tile-column / tile-row starts
    // from the saved right-edge / previous-row tx contexts (dav1d tx_lpf_right_edge, start_of_tile_row).
    [Arguments("libavif_8bit_420_517x333_tiles2x2.avif", 517, 333, "a6833bdbc249ed72b20ef4e6534cd97e")]
    [Arguments("libavif_8bit_420_1300x300_tiles2x4.avif", 1300, 300, "3cfdf31cba06724a2e0ae1371a2791f6")]
    [Arguments("libavif_10bit_420_700x420_tiles2x2.avif", 700, 420, "a9a105e5d1abb7ec158e182c20c3dea5")]
    // Progressive (libavif --progressive: key frame at half size + an inter enhancement layer with spatial_id 1 that
    // predicts from it through a scaled reference): the temporal unit's top spatial layer, as dav1d outputs it.
    [Arguments("libavif_draw_points_idat_progressive.avif", 33, 11, "1a5c39ef86fd744ec3039a96939fb39c")]
    // avifenc --progressive / --layered (libavif 1.4.2 + aom 3.14): half- / quarter-size base layers predicted
    // through scaled references (svc scale/step, scaled 8-tap MC), inter refmvs across SB rows, 8/10/12-bit.
    [Arguments("libavif_prog_8_444.avif", 384, 256, "3185d322368ccc9affb1ba318a4c3eea")]
    [Arguments("libavif_prog_10_444_odd.avif", 257, 131, "e5f4e85159d522c393f098ae64ee88f2")]
    [Arguments("libavif_prog_12_420_odd.avif", 257, 131, "b415524172d7eeaf5fe3f4b7bb687b11")]
    [Arguments("libavif_prog_8_420_alpha.avif", 161, 97, "7ddeeb444b0ef83681bd4b24abb3d4cc")]
    [Arguments("libavif_layered3_8_444.avif", 300, 200, "be1a3510a9a06d261282eb6f2590a969")]
    public async Task DecodesByteExactVsDav1d(string file, int w, int h, string md5)
    {
        byte[] item = PrimaryItemData(File.ReadAllBytes(Asset(file)));
        var dec = new Av1Decoder();
        using var yuv = dec.Decode(item, 0, isKeyframe: true);
        await Assert.That(yuv).IsNotNull();
        await Assert.That(yuv!.Width).IsEqualTo(w);
        await Assert.That(Hex(MD5.HashData(NativePlanes(yuv)))).IsEqualTo(md5);
    }

    [Test]
    public async Task AlphaMatchesLibavif()
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_10bit_420_alpha.avif")));
        await Assert.That(img.HasAlpha).IsTrue();
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        var a = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                ushort v10 = (ushort)Math.Round(row[x * ch + ch - 1] * 1023.0 / 65535.0);   // back to the coded depth
                BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan((y * w + x) * 2), v10);
            }
        }
        await Assert.That(Hex(MD5.HashData(a))).IsEqualTo("c88079b009940171afbf84e8b48fcfd1");
    }

    // RGB conversion parity with libavif (avifImageYUVToRGB, default AUTOMATIC upsampling): sampled reference pixels
    // from libavif 1.4.2's own decode, at the coded depth. HeifCoder ports libavif's reference YUV->RGB path
    // (bilinear 9/3/3/1 chroma, unorm float tables, matrix table), so it must land within one code.
    [Test]
    [Arguments("libavif_10bit_420_qm_dq.avif", 10, "(130,379,727,706,564), (183,353,874,834,618), (14,238,564,556,506), (127,332,741,721,580), (26,80,708,690,580), (57,190,770,752,618), (240,126,916,870,586), (194,278,1019,988,715), (52,293,733,713,591), (127,6,952,916,691), (110,208,646,632,534), (143,93,1022,1023,824), (199,81,1023,1023,1022), (36,71,714,695,585), (227,64,1021,1022,777), (67,0,715,705,599), (2,107,626,618,545), (110,84,943,908,717), (85,148,754,727,602), (160,101,1021,1021,1021), (104,93,1021,1020,885), (100,196,653,636,549), (152,11,1023,1022,888), (184,212,1023,1023,1015), (0,0,797,774,636), (255,383,554,529,385), (255,0,1015,1023,757), (0,383,462,463,457)")]
    [Arguments("libavif_10bit_420_257x131_lf_cdef.avif", 10, "(65,183,694,146,472), (7,238,804,57,376), (63,26,162,877,884), (40,57,239,881,831), (95,240,926,904,287), (63,194,729,199,441), (26,127,457,487,656), (3,110,371,876,726), (104,143,605,238,539), (46,199,723,99,443), (40,36,171,961,886), (35,227,806,161,381), (32,67,266,886,812), (0,2,0,534,1013), (53,110,435,308,676), (42,85,336,660,754), (74,160,626,99,518), (50,104,410,392,696), (46,100,391,482,713), (98,152,630,241,522), (5,184,622,249,524), (106,84,409,58,690), (37,135,499,285,624), (16,169,584,246,554), (0,0,0,521,1015), (130,256,1015,867,214), (130,0,160,329,884), (0,256,855,76,338)")]
    [Arguments("libavif_12bit_444_qm_dq.avif", 12, "(130,379,2919,2834,2279), (183,353,3490,3338,2454), (14,238,2250,2223,2032), (127,332,2975,2893,2325), (26,80,2831,2769,2350), (57,190,3105,3020,2484), (240,126,3729,3497,2389), (194,278,4063,3971,2864), (52,293,2951,2878,2381), (127,6,3816,3661,2757), (110,208,2568,2517,2140), (143,93,4095,4091,3295), (199,81,4092,4094,4095), (36,71,2857,2788,2353), (227,64,4093,4088,3102), (67,0,2858,2796,2389), (2,107,2509,2456,2178), (110,84,3744,3614,2854), (85,148,3050,2949,2467), (160,101,4095,4094,4059), (104,93,4095,4095,3491), (100,196,2589,2527,2172), (152,11,4092,4085,3575), (184,212,4095,4093,4095), (0,0,3170,3074,2524), (255,383,2194,2100,1546), (255,0,4095,4095,2983), (0,383,1831,1840,1819)")]
    public async Task RgbMatchesLibavif(string file, int bd, string refs)
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset(file)));
        int ch = img.NumberOfChannels, max = (1 << bd) - 1, worst = 0;
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(refs, @"\((\d+),(\d+),(\d+),(\d+),(\d+)\)"))
        {
            var g = ((System.Text.RegularExpressions.Match)m).Groups;
            int y = int.Parse(g[1].Value), x = int.Parse(g[2].Value);
            var row = img.GetPixelRow(y);
            for (int c = 0; c < 3; c++)
            {
                int ours = (int)Math.Round(row[x * ch + c] * (double)max / 65535.0);
                worst = Math.Max(worst, Math.Abs(ours - int.Parse(g[3 + c].Value)));
            }
        }
        await Assert.That(worst).IsLessThanOrEqualTo(1);
    }

    // 8-bit colour: libavif's default build converts through libyuv (fixed-point, integer bilinear chroma), which
    // HeifCoder ports exactly — sampled pixels from libavif 1.4.2's own decode must match with zero tolerance.
    [Test]
    [Arguments("sharpimage_8bit_420_257x131_lf_cdef.avif", "(0,0,1,137,248), (130,256,250,219,48), (0,256,213,20,81), (130,0,42,89,222), (1,1,2,138,249), (129,255,248,222,48), (82,77,90,60,180), (101,24,55,117,218), (18,48,46,243,218), (93,29,53,129,213), (129,109,132,56,155), (9,44,41,230,226), (111,214,214,218,79), (17,123,107,158,169), (23,217,189,13,109), (15,63,57,241,208), (57,31,44,223,218), (101,25,57,114,218), (56,23,37,232,228), (34,148,133,56,145), (107,73,94,19,182), (30,157,140,51,143), (46,52,60,213,209), (48,190,174,17,118), (24,32,36,239,228), (15,105,92,199,181), (127,218,220,242,79), (80,238,225,189,76), (116,185,190,183,105), (76,127,127,15,149), (46,124,120,66,160), (20,153,136,82,150)")]
    [Arguments("sharpimage_8bit_420_129x67_lf_cdef.avif", "(0,0,3,141,242), (66,128,254,217,60), (0,128,214,23,83), (66,0,40,218,223), (1,1,5,143,244), (65,127,252,214,61), (41,38,88,156,185), (50,12,48,220,214), (9,24,46,235,221), (46,14,49,224,211), (64,54,127,27,150), (4,22,39,223,223), (55,107,210,103,84), (8,61,105,183,172), (11,108,186,13,101), (7,31,55,240,216), (28,15,44,241,222), (50,12,48,220,214), (28,11,36,241,229), (17,74,133,92,148), (53,36,93,117,181), (15,78,138,82,143), (23,26,57,239,207), (24,95,175,14,116), (12,16,35,226,227), (7,52,90,218,183), (63,109,221,138,78), (40,119,223,99,79), (58,92,186,54,109), (38,63,127,60,150), (23,62,118,119,163), (10,76,132,110,147)")]
    [Arguments("libavif_8bit_444_qm_dq.avif", "(0,0,198,192,156), (255,383,139,133,97), (0,383,115,117,114), (255,0,255,255,188), (1,1,205,200,162), (254,382,140,135,97), (165,77,255,255,255), (202,333,211,201,149), (24,37,209,207,160), (48,187,180,177,146), (29,259,167,165,140), (109,19,231,223,174), (44,222,182,179,148), (214,35,253,255,208), (123,46,246,237,180), (217,30,254,255,190), (63,114,163,161,138), (31,295,157,156,135), (203,25,254,255,208), (113,23,233,226,174), (68,148,165,163,138), (214,73,255,255,247), (60,292,186,181,149), (157,286,231,221,167), (92,52,255,255,255), (96,190,157,155,132), (49,280,182,179,148), (32,288,163,161,138), (30,316,146,145,127), (105,254,151,149,128), (218,160,254,255,187), (238,299,177,171,121)")]
    [Arguments("sharpimage_8bit_444_257x131.avif", "(0,0,0,130,255), (130,256,253,215,53), (0,256,214,19,85), (130,0,41,84,223), (1,1,0,132,254), (129,255,254,218,55), (82,77,91,63,184), (101,24,52,119,216), (18,48,45,241,219), (93,29,52,131,212), (129,109,130,61,150), (9,44,38,230,223), (111,214,214,220,85), (17,123,108,156,169), (23,217,188,15,105), (15,63,56,243,209), (57,31,44,223,220), (101,25,53,117,217), (56,23,35,233,224), (34,148,134,55,147), (107,73,94,19,180), (30,157,142,48,143), (46,52,59,214,209), (48,190,175,19,116), (24,32,34,237,226), (15,105,92,199,181), (127,218,220,241,78), (80,238,224,190,78), (116,185,190,185,105), (76,127,130,13,151), (46,124,117,68,160), (20,153,135,82,147)")]
    [Arguments("sharpimage_8bit_422_384x256.avif", "(0,0,201,192,161), (255,383,139,133,101), (0,383,115,115,113), (255,0,255,254,190), (1,1,208,198,163), (254,382,140,134,100), (165,77,255,255,255), (202,333,209,202,149), (24,37,208,200,161), (48,187,180,175,147), (29,259,169,165,140), (109,19,229,221,172), (44,222,184,179,151), (214,35,255,255,208), (123,46,247,237,183), (217,30,254,254,190), (63,114,162,160,139), (31,295,159,156,137), (203,25,254,255,209), (113,23,233,226,174), (68,148,167,162,140), (214,73,255,255,247), (60,292,187,182,150), (157,286,231,219,168), (92,52,255,255,255), (96,190,157,155,134), (49,280,182,177,147), (32,288,162,160,137), (30,316,145,143,130), (105,254,150,147,130), (218,160,254,255,185), (238,299,178,170,121)")]
    // Studio range (libyuv I601 / H709 constants, UB clamped to 128) and full-range BT.2020 (V2020).
    [Arguments("sharpimage_8bit_420_lim709.avif", "(0,0,0,130,246), (130,256,255,216,60), (0,256,214,19,83), (130,0,41,82,214), (1,1,2,131,245), (129,255,251,218,61), (82,77,91,61,179), (101,24,51,117,209), (18,48,45,241,218), (93,29,53,132,207), (129,109,132,60,145), (9,44,41,230,222), (111,214,215,221,94), (17,123,108,157,168), (23,217,188,15,102), (15,63,56,242,208), (57,31,44,222,218), (101,25,52,115,209), (56,23,36,233,223), (34,148,134,54,144), (107,73,96,20,172), (30,157,142,49,142), (46,52,58,215,210), (48,190,174,19,115), (24,32,34,238,228), (15,105,92,198,181), (127,218,221,241,87), (80,238,225,190,83), (116,185,189,183,106), (76,127,130,14,147), (46,124,117,68,157), (20,153,136,81,146)")]
    [Arguments("sharpimage_8bit_420_lim601.avif", "(0,0,1,131,250), (130,256,253,214,53), (0,256,211,21,85), (130,0,41,84,223), (1,1,2,133,251), (129,255,254,217,54), (82,77,90,61,184), (101,24,52,118,213), (18,48,47,241,218), (93,29,54,131,214), (129,109,132,60,152), (9,44,38,231,222), (111,214,214,220,85), (17,123,107,157,169), (23,217,188,14,105), (15,63,57,242,209), (57,31,44,222,219), (101,25,52,116,214), (56,23,36,234,223), (34,148,133,54,148), (107,73,95,22,180), (30,157,141,49,143), (46,52,56,214,208), (48,190,176,18,119), (24,32,34,238,230), (15,105,92,198,180), (127,218,221,241,81), (80,238,224,190,79), (116,185,190,183,102), (76,127,129,14,150), (46,124,116,66,160), (20,153,133,82,147)")]
    [Arguments("sharpimage_8bit_420_full2020.avif", "(0,0,0,130,251), (130,256,254,216,51), (0,256,212,19,83), (130,0,40,83,221), (1,1,2,132,251), (129,255,251,219,52), (82,77,89,63,184), (101,24,52,117,215), (18,48,47,241,218), (93,29,54,131,213), (129,109,131,59,150), (9,44,40,231,223), (111,214,214,221,87), (17,123,110,157,169), (23,217,188,14,105), (15,63,58,243,211), (57,31,44,224,221), (101,25,52,116,214), (56,23,37,234,226), (34,148,134,54,148), (107,73,95,19,178), (30,157,141,47,144), (46,52,59,216,210), (48,190,174,17,117), (24,32,36,238,229), (15,105,94,199,181), (127,218,222,241,78), (80,238,224,190,76), (116,185,190,183,103), (76,127,129,14,150), (46,124,118,66,161), (20,153,133,81,148)")]
    public async Task Rgb8MatchesLibavifExactly(string file, string refs)
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset(file)));
        int ch = img.NumberOfChannels, worst = 0;
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(refs, @"\((\d+),(\d+),(\d+),(\d+),(\d+)\)"))
        {
            var g = ((System.Text.RegularExpressions.Match)m).Groups;
            int y = int.Parse(g[1].Value), x = int.Parse(g[2].Value);
            var row = img.GetPixelRow(y);
            for (int c = 0; c < 3; c++)
            {
                int ours = (int)Math.Round(row[x * ch + c] * 255.0 / 65535.0);
                worst = Math.Max(worst, Math.Abs(ours - int.Parse(g[3 + c].Value)));
            }
        }
        await Assert.That(worst).IsEqualTo(0);
    }

    // Film grain synthesis (libaom film-grain-test vectors and denoise-estimated grain; 8/10/12-bit, 4:2:0/4:2:2/4:4:4/
    // mono, overlap, chroma-from-luma, restricted-range clipping). Golden MD5s are libdav1d with grain applied (its
    // default, as in libavif) and with -filmgrain 0.
    [Test]
    [Arguments("libaom_fg_8bit_420_t1.avif", "6cb70caebbe965ce864fa53ce336540a", "cc123a27dca6cda8fa63a9a07eb7dce2")]
    [Arguments("libaom_fg_8bit_420_t2.avif", "c5baccfaccab622c2355eee2391f33a9", "38dd472a45988b46b0218df6d738aff7")]
    [Arguments("libaom_fg_8bit_420_t10.avif", "e67b4757c814f69b801c7ef7c4599c10", "2c5094fc5e65da9d7b2d6c3e83c83a9b")]
    [Arguments("libaom_fg_8bit_420_t16.avif", "f6e6cc4767a3b6646de1d6c9d6b13dc7", "2c5094fc5e65da9d7b2d6c3e83c83a9b")]
    [Arguments("libaom_fg_8bit_444_t4.avif", "8b709bbb38d6ed37d1f53a7c6ef5b0b9", "8c47ef8746b021fdbd51fa4ca2c7d116")]
    [Arguments("libaom_fg_8bit_gray_t7.avif", "386322dc3c21bba0630aad03c8906518", "a6bcb75b67562f15b1271bbc4862b47b")]
    [Arguments("libaom_fg_10bit_420_t3.avif", "21b95fd1ad75b5ce25d4355086511a8c", "e8daa98656d71913a004f6a8bc7d87cd")]
    [Arguments("libaom_fg_10bit_422_t5.avif", "6026b74253fb2d86b1f1397496f1e223", "07e5f116e8fc3271e0ae4faacf56c72f")]
    [Arguments("libaom_fg_12bit_444_t6.avif", "84a3bd7be6558581dd92ba69e92a72df", "04ee575d9a691887bb0386e032aa4863")]
    [Arguments("libaom_fg_8bit_420_denoise.avif", "f0016b78fb479da761ff9be5ad0cb088", "f65dd6be5efcb92f90913727498570a0")]
    [Arguments("libaom_fg_10bit_420_denoise.avif", "195dd23512a9ad30e9aee10e38450888", "39c0837a3d8ef2f8148a00f2e9aaff0a")]
    // Our own encoder's film grain signalling (libaom test vectors 1 and 5; odd-width 4:2:0 exercises the luma padding
    // for chroma, 10-bit 4:2:2; the alpha item carries no grain).
    [Arguments("sharpimage_fg_8bit_420_257x131_v1.avif", "fc8b31d3d2437c666a42618dfb8f8b62", "e82427f436d42b08c4e44a57a71a208e")]
    [Arguments("sharpimage_fg_10bit_422_257x131_v5.avif", "8ac509a50671abf227a78244f072343d", "96409538c4d7e97a1aac83d499db4b23")]
    [Arguments("sharpimage_fg_8bit_420_alpha_v1.avif", "deee3f3fa25659dd33a614eb7cb7974c", "918a326116695311b254ec70bece6117")]
    public async Task FilmGrain_ByteExactVsDav1d(string file, string md5, string md5NoGrain)
    {
        byte[] item = PrimaryItemData(File.ReadAllBytes(Asset(file)));
        // Monochrome streams hash the Y plane only (ffmpeg pix_fmt gray).
        static byte[] Planes(Av1Decoder d, DecodedVideoFrame f) =>
            d.Monochrome ? NativePlanes(f).AsSpan(0, f.Width * f.Height * (f.BitDepth > 8 ? 2 : 1)).ToArray() : NativePlanes(f);
        var dg = new Av1Decoder();
        using (var yuv = dg.Decode(item, 0, isKeyframe: true))
            await Assert.That(Hex(MD5.HashData(Planes(dg, yuv!)))).IsEqualTo(md5);
        var dn = new Av1Decoder { ApplyFilmGrain = false };
        using (var plain = dn.Decode(item, 0, isKeyframe: true))
            await Assert.That(Hex(MD5.HashData(Planes(dn, plain!)))).IsEqualTo(md5NoGrain);
    }

    // Planes as ffmpeg writes raw video: Y, U, V tightly packed; u16 LE for >8-bit, u8 otherwise.
    private static byte[] NativePlanes(DecodedVideoFrame f)
    {
        bool hbd = f.BitDepth > 8;
        int ssx = f.Format == PixelFormat.Yuv444P ? 0 : 1;
        int ssy = f.Format == PixelFormat.Yuv420P ? 1 : 0;
        int cw = (f.Width + ssx) >> ssx, chh = (f.Height + ssy) >> ssy;
        int bps = hbd ? 2 : 1;
        var o = new byte[(f.Width * f.Height + 2 * cw * chh) * bps];
        int p = 0;
        void Put(int pw, int ph, int stride, ReadOnlySpan<byte> b8, ReadOnlySpan<ushort> b16)
        {
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    if (hbd) { BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(p), b16[y * stride + x]); p += 2; }
                    else o[p++] = b8[y * stride + x];
                }
        }
        Put(f.Width, f.Height, f.YStride, f.YPlane.Span, f.YPlane16.Span);
        Put(cw, chh, f.UStride, f.UPlane.Span, f.UPlane16.Span);
        Put(cw, chh, f.VStride, f.VPlane.Span, f.VPlane16.Span);
        return o;
    }

    private static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();

    // The primary item's payload.
    private static byte[] PrimaryItemData(byte[] d)
    {
        var box = HeifContainer.Parse(d);   // handles idat / multi-extent items too
        return box.ItemData(box.PrimaryId)!;
    }
}
