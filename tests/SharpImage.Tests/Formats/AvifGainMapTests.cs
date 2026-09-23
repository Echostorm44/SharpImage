using System;
using System.IO;
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
        await Assert.That(gm.ImageDepth).IsEqualTo(8);
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
