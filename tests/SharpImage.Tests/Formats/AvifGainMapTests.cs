using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// AVIF gain maps ('tmap' derived item, ISO 21496-1) against libavif's own corpus (tests/data, BSD): metadata as the tmap
// payload carries it, gain map pixels byte-exact vs avifgainmaputil extractgainmap, and the same files accepted,
// ignored or rejected as in libavif's avifgainmaptest.cc.
public sealed class AvifGainMapTests
{
    private static byte[] Asset(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name));

    [Test]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", 400, 300, "a243c604a7cd37e9f60fdc3ec3a5a4786a6661d2e5a3db7e2bf95e1c2754b037")]
    [Arguments("libavif_seine_hdr_gainmap_srgb.avif", 400, 300, "a1cb26083e4441c8fbcac501a4ee4f73d3565db4080133bcff950f9f8f95a008")]
    [Arguments("libavif_color_grid_gainmap_different_grid.avif", 128, 160, "91036e59867d1ec8ff1312be28f3e497e3520261b13b2d29f104d7a13f6d3aad")]
    [Arguments("libavif_color_nogrid_alpha_nogrid_gainmap_grid.avif", 128, 160, "91036e59867d1ec8ff1312be28f3e497e3520261b13b2d29f104d7a13f6d3aad")]
    [Arguments("libavif_color_grid_alpha_grid_gainmap_nogrid.avif", 64, 80, "eb871ef7b99ddedaa56efb7c15d52e2ccb5e59d26e5d3293d46eed1181d0adea")]
    public async Task GainMapImage_MatchesLibavifExactly(string file, int w, int h, string sha256)
    {
        var gm = HeifCoder.DecodeGainMap(Asset(file));
        await Assert.That(gm).IsNotNull();
        var img = gm!.Image!;
        await Assert.That((int)img.Columns).IsEqualTo(w);
        await Assert.That((int)img.Rows).IsEqualTo(h);
        int ch = img.NumberOfChannels;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                    rgb[(y * w + x) * 3 + k] = (byte)((row[x * ch + Math.Min(k, ch - 1)] * 255 + 32767) / 65535);
        }
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(rgb))).IsEqualTo(sha256);
    }

    // Tone mapping vs libavif (avifgainmaputil tonemap to Y4M with an identity matrix, i.e. its exact RGB; CLLI from
    // avifImageApplyGainMap): SDR->HDR and HDR->SDR, 10-bit bases, grids, gain maps up- and downscaled (libyuv box
    // filter), PQ / HLG / sRGB / BT.470BG outputs, BT.709 -> BT.2020 / P3 conversion, 8/10/12-bit output. cicp "" =
    // the tool's defaults. SHA-256 of the native-depth RGB as little-endian u16.
    [Test]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", 1.3f, "", 0, 8, "1/16", 492, 177, "e7c501b131fcfcffeeda7b10bd49f2257f96f058b6441062881d23199e25b847")]
    [Arguments("libavif_seine_hdr_gainmap_srgb.avif", 0f, "", 0, 8, "1/13", 313, 99, "44bc5bd266a2f49e33aa79ee385507c42f17265c725714159cfef286b144096b")]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", 1f, "9/16", 12, 12, "9/16", 401, 154, "436ed894b70c28bf60a12ccb1d5ef1548672df3d72f4870fd851d1d3a613064f")]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", 1.3f, "12/18", 10, 10, "12/18", 492, 177, "18ad39b2dbd5a5409c9c12b24eb83261db07bdaed2fead1637105e36b034ece8")]
    [Arguments("libavif_seine_hdr_gainmap_srgb.avif", 0.7f, "9/1", 8, 8, "9/1", 504, 134, "d1ea4c1ed570e18e244f5020c5dfefa295cc04126a57adee21c418e34b0ee0de")]
    [Arguments("libavif_color_grid_gainmap_different_grid.avif", 2f, "", 0, 10, "2/16", 12599, 2059, "9d6781eaca5e3d5c8846f1785de8a32a6abcf38ea57b9ddbbd0439204b26cbca")]
    [Arguments("libavif_color_nogrid_alpha_nogrid_gainmap_grid.avif", 0.5f, "", 0, 10, "2/16", -1, -1, "59ab98eb105a71a1c8bd86b04be7b048de4291d7b68553453f6dc7361ff19ffe")]
    [Arguments("libavif_seine_sdr_gainmap_big_srgb.avif", 1.3f, "", 0, 8, "1/16", 456, 177, "d682dcf7ed3c8547d2229f441365bc267deec580dbb83917aebf206fe1d232c7")]
    [Arguments("libavif_seine_hdr_gainmap_small_srgb.avif", 0.5f, "", 0, 10, "1/16", -1, -1, "ac9c3ebe7ba0982bc262ea710b7f35839e1f86f29bd3dab06e6dd9fdbfa1a8f1")]
    public async Task ToneMapped_MatchesLibavifExactly(string file, float headroom, string cicp, int depth, int wantDepth, string wantCicp,
        int maxCll, int maxPall, string sha256)
    {
        SharpImage.Metadata.CicpInfo? output = null;
        if (cicp.Length > 0) { var t = cicp.Split('/'); output = new(int.Parse(t[0]), int.Parse(t[1]), 0, true); }
        var img = HeifCoder.DecodeToneMapped(Asset(file), headroom, output, depth);
        await Assert.That(img.Depth).IsEqualTo(wantDepth);
        await Assert.That($"{img.Metadata.Cicp!.ColorPrimaries}/{img.Metadata.Cicp.TransferCharacteristics}").IsEqualTo(wantCicp);
        if (maxCll >= 0)
        {
            await Assert.That((int)img.Metadata.ContentLightLevel!.MaxContentLightLevel).IsEqualTo(maxCll);
            await Assert.That((int)img.Metadata.ContentLightLevel.MaxFrameAverageLightLevel).IsEqualTo(maxPall);
        }
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        uint max = (1u << img.Depth) - 1;
        var buf = new byte[w * h * 6];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                {
                    ushort v = (ushort)((row[x * ch + k] * max + 32767u) / 65535u);
                    buf[(y * w + x) * 6 + k * 2] = (byte)v;
                    buf[(y * w + x) * 6 + k * 2 + 1] = (byte)(v >> 8);
                }
        }
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(buf))).IsEqualTo(sha256);
    }

    // ComputeGainMap + EncodeAvif(GainMap) = avifgainmaputil combine: the written metadata and the (lossless) gain map
    // image equal what libavif's combine writes for the same inputs (expected values read from libavif's files):
    // plain, YUV-space downscaling x2 / x3, single channel, 10-bit 4:2:0, a BT.2020 PQ alternate (math in the
    // alternate's space), an HDR base with an SDR alternate (sign flip), and a BT.2020 base with an sRGB alternate.
    [Test]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", "libavif_seine_tonemapped_pq_h1.3.avif", 1, 8, false, "444", "c571b399a6c36f30",
        "base=0/1 alt=5558157/4194304 min=-5683895/16777216 -11500805/33554432 -6038545/16777216 max=5541417/4194304 2783957/2097152 11329927/8388608 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=True alt=1/16/6 altpixi=3x8")]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", "libavif_seine_tonemapped_pq_h1.3.avif", 2, 8, false, "444", "96bafb2ec9a7f1f2",
        "base=0/1 alt=5558157/4194304 min=-5683895/16777216 -11500805/33554432 -6038545/16777216 max=5541417/4194304 2783957/2097152 11329927/8388608 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=True alt=1/16/6 altpixi=3x8")]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", "libavif_seine_tonemapped_pq_h1.3.avif", 3, 8, true, "444", "6cc6a17f0ae28706",
        "base=0/1 alt=10676315/8388608 min=-1361431/4194304 -1361431/4194304 -1361431/4194304 max=11058799/8388608 11058799/8388608 11058799/8388608 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=True alt=1/16/6 altpixi=3x8")]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", "libavif_seine_tonemapped_pq_h1.3.avif", 1, 10, false, "420", "295d7123d74bd4ee",
        "base=0/1 alt=5558157/4194304 min=-5683895/16777216 -11500805/33554432 -6038545/16777216 max=5541417/4194304 2783957/2097152 11329927/8388608 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=True alt=1/16/6 altpixi=3x8")]
    [Arguments("libavif_seine_sdr_gainmap_srgb.avif", "libavif_seine_tonemapped_bt2020pq10_h1.3.avif", 1, 8, false, "444", "0cd45fc8b4ae0c2f",
        "base=0/1 alt=2668317/2097152 min=-9368687/33554432 -9445223/33554432 -4966425/16777216 max=675277/524288 10803081/8388608 10862281/8388608 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=False alt=9/16/9 altpixi=3x10")]
    [Arguments("libavif_seine_hdr_gainmap_srgb.avif", "libavif_seine_tonemapped_srgb_h0.avif", 1, 8, false, "444", "125368a8dc0874f6",
        "base=3979117/2097152 alt=0/1 min=-1240767/4194304 -4953885/16777216 -10624599/33554432 max=1353623/1048576 5586753/4194304 710767/524288 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=True alt=1/13/6 altpixi=3x8")]
    [Arguments("libavif_seine_tonemapped_bt2020pq10_h1.3.avif", "libavif_seine_sdr_gainmap_srgb.avif", 1, 8, true, "444", "bcabce01be697127",
        "base=2646033/2097152 alt=0/1 min=-9196415/33554432 -9196415/33554432 -9196415/33554432 max=5384513/4194304 5384513/4194304 5384513/4194304 boff=1/64 1/64 1/64 aoff=1/64 1/64 1/64 gamma=1/1 1/1 1/1 usebase=True alt=1/13/6 altpixi=3x8")]
    public async Task EncodedGainMap_MatchesLibavifCombine(string baseFile, string altFile, int downscaling, int depth, bool single,
        string subsampling, string gainMapSha, string metadata)
    {
        var b = HeifCoder.DecodeNativeDepth(Asset(baseFile));
        var a = HeifCoder.DecodeNativeDepth(Asset(altFile));
        var computed = HeifCoder.ComputeGainMap(b, a, depth, single);
        byte[] file = HeifCoder.EncodeAvif(b, new AvifEncodeOptions
        {
            Lossless = true, GainMap = computed, GainMapLossless = true, GainMapDownscaling = downscaling,
            GainMapChromaSubsampling = subsampling == "420" ? AvifChromaSubsampling.Yuv420 : AvifChromaSubsampling.Yuv444,
        });
        var gm = HeifCoder.DecodeGainMap(file)!;
        string F(GainMapFraction[] f) => string.Join(" ", f.Select(x => $"{x.Numerator}/{x.Denominator}"));
        string G(GainMapUFraction[] f) => string.Join(" ", f.Select(x => $"{x.Numerator}/{x.Denominator}"));
        string meta = $"base={gm.BaseHdrHeadroom.Numerator}/{gm.BaseHdrHeadroom.Denominator} alt={gm.AlternateHdrHeadroom.Numerator}/{gm.AlternateHdrHeadroom.Denominator}" +
            $" min={F(gm.Min)} max={F(gm.Max)} boff={F(gm.BaseOffset)} aoff={F(gm.AlternateOffset)} gamma={G(gm.Gamma)} usebase={gm.UseBaseColorSpace}" +
            $" alt={gm.AlternateCicp!.ColorPrimaries}/{gm.AlternateCicp.TransferCharacteristics}/{gm.AlternateCicp.MatrixCoefficients} altpixi={gm.AlternatePlaneCount}x{gm.AlternateDepth}";
        await Assert.That(meta).IsEqualTo(metadata);

        var img = gm.Image!;
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        uint max = (1u << img.Depth) - 1;
        var buf = new byte[w * h * 6];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                {
                    ushort v = (ushort)((row[x * ch + Math.Min(k, ch - 1)] * max + 32767u) / 65535u);
                    buf[(y * w + x) * 6 + k * 2] = (byte)v;
                    buf[(y * w + x) * 6 + k * 2 + 1] = (byte)(v >> 8);
                }
        }
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(buf))[..16]).IsEqualTo(gainMapSha);
    }

    [Test]
    public async Task EncodedGainMap_HasLibavifLayout()
    {
        // ftyp 'tmap'; tmap item preferred in 'altr'; dimg tmap -> [colour, gain map]; gain map item hidden.
        var b = HeifCoder.DecodeNativeDepth(Asset("libavif_seine_sdr_gainmap_srgb.avif"));
        var a = HeifCoder.DecodeNativeDepth(Asset("libavif_seine_tonemapped_pq_h1.3.avif"));
        byte[] file = HeifCoder.EncodeAvif(b, new AvifEncodeOptions { Qp = 30, GainMap = HeifCoder.ComputeGainMap(b, a), GainMapQp = 30 });
        var c = HeifContainer.Parse(file);
        await Assert.That(c.Brands).Contains("tmap");
        var tmap = c.Items.Values.Single(i => i.Type == "tmap");
        var dimg = c.ReferencesFrom(tmap.Id, "dimg");
        await Assert.That(dimg.Count).IsEqualTo(2);
        await Assert.That(dimg[0]).IsEqualTo(c.PrimaryId);
        await Assert.That(c.Groups.Single().Type).IsEqualTo("altr");
        await Assert.That(c.Groups.Single().Entities).IsEquivalentTo(new uint[] { (uint)tmap.Id, (uint)c.PrimaryId });
        // A lossy gain map still decodes and tone maps (the gain map's quality only changes its precision).
        var hdr = HeifCoder.DecodeToneMapped(file, 1.3f);
        await Assert.That(hdr.Metadata.Cicp!.TransferCharacteristics).IsEqualTo(16);
    }

    [Test]
    public async Task Metadata_IsReadPerChannel()
    {
        // seine_sdr_gainmap_srgb: is_multichannel = 1, use_base_colour_space = 1, alternate image PQ (colr 1/16/6).
        var gm = HeifCoder.DecodeGainMap(Asset("libavif_seine_sdr_gainmap_srgb.avif"))!;
        await Assert.That(gm.IsMultichannel).IsTrue();
        await Assert.That(gm.UseBaseColorSpace).IsTrue();
        await Assert.That(gm.BaseHdrHeadroom).IsEqualTo(new GainMapUFraction(0, 1));
        await Assert.That(gm.AlternateHdrHeadroom).IsEqualTo(new GainMapUFraction(13, 10));
        await Assert.That(gm.Min[0]).IsEqualTo(new GainMapFraction(-256907, 1000000));
        await Assert.That(gm.Min[1]).IsEqualTo(new GainMapFraction(-52273, 200000));
        await Assert.That(gm.Min[2]).IsEqualTo(new GainMapFraction(-70071, 250000));
        await Assert.That(gm.Max[2]).IsEqualTo(new GainMapFraction(1277969, 1000000));
        await Assert.That(gm.Gamma[1]).IsEqualTo(new GainMapUFraction(188219, 200000));
        await Assert.That(gm.BaseOffset[0]).IsEqualTo(new GainMapFraction(1, 64));
        await Assert.That(gm.AlternateOffset[2]).IsEqualTo(new GainMapFraction(1, 64));
        await Assert.That(gm.AlternateCicp).IsEqualTo(new SharpImage.Metadata.CicpInfo(1, 16, 6, true));
        await Assert.That(gm.AlternatePlaneCount).IsEqualTo(3);
        await Assert.That(gm.AlternateDepth).IsEqualTo(8);
        await Assert.That(gm.Image!.Depth).IsEqualTo(8);
    }

    [Test]
    [Arguments("libavif_seine_sdr_gainmap_notmapbrand.avif")]           // no 'tmap' brand in ftyp
    [Arguments("libavif_seine_hdr_gainmap_wrongaltr.avif")]             // tmap after the base in the 'altr' group
    [Arguments("libavif_unsupported_gainmap_version.avif")]
    [Arguments("libavif_unsupported_gainmap_minimum_version.avif")]
    public async Task IgnoredGainMap_DecodesBaseOnly(string file)
    {
        byte[] data = Asset(file);
        await Assert.That(HeifCoder.DecodeGainMap(data)).IsNull();
        var img = HeifCoder.Decode(data);
        await Assert.That(img.Columns).IsGreaterThan(0u);
    }

    [Test]
    public async Task UnsupportedWriterVersion_AllowsTrailingBytes()
    {
        var gm = HeifCoder.DecodeGainMap(Asset("libavif_unsupported_gainmap_writer_version_with_extra_bytes.avif"));
        await Assert.That(gm).IsNotNull();
    }

    [Test]
    [Arguments("libavif_seine_sdr_gainmap_gammazero.avif")]
    [Arguments("libavif_supported_gainmap_writer_version_with_extra_bytes.avif")]
    public async Task InvalidGainMap_FailsTheDecode(string file)
    {
        // libavif: AVIF_RESULT_INVALID_TONE_MAPPED_IMAGE, also for a plain decode of the base image.
        byte[] data = Asset(file);
        await Assert.That(() => HeifCoder.DecodeGainMap(data)).Throws<InvalidDataException>();
        await Assert.That(() => HeifCoder.Decode(data)).Throws<InvalidDataException>();
    }
}
