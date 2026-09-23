using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// JPEG -> AVIF as avifenc reads JPEG input (HeifCoder.EncodeAvifFromJpeg): raw YCbCr planes (libjpeg-turbo islow IDCT)
// coded without colour conversion, extended XMP merged like libavif (libxml2 serialization), Ultra HDR / Apple gain maps
// carried over. Expected values come from libavif 1.4.2 (avifenc -q 100 --qgain-map 100): base RGB and gain map hashes
// are SHA-256 of avifdec / avifgainmaputil extractgainmap PNG samples as 16-bit little-endian (8-bit x 257), the tmap
// payload is the reference file's, the XMP hash is of avifdec's XMP.
public sealed class AvifJpegGainMapTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    private static string Hash(ImageFrame f, int channels)
    {
        int w = (int)f.Columns, h = (int)f.Rows, n = f.NumberOfChannels;
        var buf = new byte[w * h * channels * 2];
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < channels; k++)
                {
                    int v = row[x * n + k], o = ((y * w + x) * channels + k) * 2;
                    buf[o] = (byte)v;
                    buf[o + 1] = (byte)(v >> 8);
                }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    // The ISO 21496-1 fields of a gain map, as fractions.
    private static string Describe(AvifGainMap g) =>
        $"{g.BaseHdrHeadroom} {g.AlternateHdrHeadroom} {g.UseBaseColorSpace} {g.IsMultichannel} " +
        string.Join(" ", new[] { 0, 1, 2 }.Select(c => $"{g.Min[c]} {g.Max[c]} {g.Gamma[c]} {g.BaseOffset[c]} {g.AlternateOffset[c]}"));

    // The same fields of a reference tmap item payload (version 0).
    private static string DescribePayload(string hex)
    {
        var p = Convert.FromHexString(hex);
        int pos = 6;
        uint U()
        {
            uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos));
            pos += 4;
            return v;
        }
        var g = new AvifGainMap { UseBaseColorSpace = (p[5] & 0x40) != 0 };
        g.BaseHdrHeadroom = new(U(), U());
        g.AlternateHdrHeadroom = new(U(), U());
        int channels = (p[5] & 0x80) != 0 ? 3 : 1;
        for (int c = 0; c < channels; c++)
        {
            g.Min[c] = new((int)U(), U());
            g.Max[c] = new((int)U(), U());
            g.Gamma[c] = new(U(), U());
            g.BaseOffset[c] = new((int)U(), U());
            g.AlternateOffset[c] = new((int)U(), U());
        }
        for (int c = channels; c < 3; c++)
        {
            g.Min[c] = g.Min[0]; g.Max[c] = g.Max[0]; g.Gamma[c] = g.Gamma[0];
            g.BaseOffset[c] = g.BaseOffset[0]; g.AlternateOffset[c] = g.AlternateOffset[0];
        }
        return Describe(g);
    }

    [Test]
    [Arguments("libavif_paris_exif_xmp_gainmap_littleendian.jpg", "296f92324938fb83", "386d4507ee944f92", 1,
        "0000000000c00000000000000001000000070000000200000000000000010000000700000002000000010000000100000000000000010000000000000001000000000000000100000012000000050000000100000001000000000000000100000000000000010000000000000001000000250000000a000000010000000100000000000000010000000000000001")]
    [Arguments("libavif_apple_gainmap_old.jpg", "71afa306f20dd69a", "368522ea6d125922", 1,
        "0000000000400000000000000001000000030000000100000000000000010000000300000001000000010000000100000000000000010000000000000001")]
    [Arguments("libavif_seine_sdr_different_gainmap_srgb.jpg", "501ef683fcad5d9d", "a46adf96110c7fcf", 3,
        "0000000000c000000000000000010000000d0000000afffc1475000f424000137cf9000f42400001d1b70001e84800000001000000400000000100000040ffff33cf00030d4000137d13000f42400002df3b00030d4000000001000000400000000100000040fffeee490003d09000138011000f4240000703bf0007a12000000001000000400000000100000040")]
    public async Task LosslessFromJpeg_MatchesAvifenc(string jpg, string baseSha, string gainMapSha, int gmChannels, string tmap)
    {
        byte[] avif = HeifCoder.EncodeAvifFromJpeg(File.ReadAllBytes(Asset(jpg)), new AvifEncodeOptions { Quality = 100, QualityGainMap = 100 });
        await Assert.That(Hash(HeifCoder.Decode(avif), 3)).IsEqualTo(baseSha);
        var gm = HeifCoder.DecodeGainMap(avif)!;
        await Assert.That(Hash(gm.Image!, gmChannels)).IsEqualTo(gainMapSha);
        await Assert.That(Describe(gm)).IsEqualTo(DescribePayload(tmap));
        await Assert.That(gm.AlternateCicp!.TransferCharacteristics).IsEqualTo(16);
    }

    [Test]
    public async Task ExtendedXmp_MergedLikeLibavif()
    {
        var img = JpegCoder.Read(Asset("libavif_paris_exif_xmp_gainmap_littleendian.jpg"));
        byte[] xmp = System.Text.Encoding.UTF8.GetBytes(img.Metadata.Xmp!);
        await Assert.That(xmp.Length).IsEqualTo(2709);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(xmp))[..16]).IsEqualTo("463eba23bfab6c12");
    }

    [Test]
    public async Task ReadGainMap_AppleExifHeadroom()
    {
        var gm = JpegCoder.ReadGainMap(File.ReadAllBytes(Asset("libavif_apple_gainmap_old.jpg")))!;
        await Assert.That(gm.AlternateHdrHeadroom).IsEqualTo(new GainMapUFraction(3, 1));
        await Assert.That(gm.Max[0]).IsEqualTo(new GainMapFraction(3, 1));
        await Assert.That(JpegCoder.ReadGainMap(File.ReadAllBytes(Asset("sharpyuv_src_rgb8_37x29.png")))).IsNull();
    }

    [Test]
    public async Task IgnoreGainMap_WritesNoGainMap()
    {
        byte[] avif = HeifCoder.EncodeAvifFromJpeg(File.ReadAllBytes(Asset("libavif_apple_gainmap_old.jpg")),
            new AvifEncodeOptions { Quality = 100 }, ignoreGainMap: true);
        await Assert.That(HeifCoder.DecodeGainMap(avif)).IsNull();
        await Assert.That(Hash(HeifCoder.Decode(avif), 3)).IsEqualTo("71afa306f20dd69a");
    }

    // avifgainmaputil convert --swap-base: colour properties, content light level and gain map metadata match libavif.
    [Test]
    [Arguments("libavif_paris_exif_xmp_gainmap_littleendian.jpg", 1414, 285,
        "0000000000800000000700000002000000000000000100000000000000010000000700000002000000010000000100000000000000010000000000000001000000000000000100000012000000050000000100000001000000000000000100000000000000010000000000000001000000250000000a000000010000000100000000000000010000000000000001")]
    [Arguments("libavif_seine_sdr_different_gainmap_srgb.jpg", 492, 172,
        "0000000000800000000d0000000a0000000000000001fffc1475000f424000137cf9000f42400001d1b70001e84800000001000000400000000100000040ffff33cf00030d4000137d13000f42400002df3b00030d4000000001000000400000000100000040fffeee490003d09000138011000f4240000703bf0007a12000000001000000400000000100000040")]
    public async Task SwapBaseFromJpeg_MatchesAvifgainmaputil(string jpg, int maxCll, int maxPall, string tmap)
    {
        byte[] avif = HeifCoder.EncodeAvifFromJpeg(File.ReadAllBytes(Asset(jpg)), new AvifEncodeOptions { Quality = 100, QualityGainMap = 100 },
            swapBase: true, ignoreIccProfile: true);
        var d = HeifCoder.Decode(avif);
        await Assert.That(d.Metadata.Cicp).IsEqualTo(new SharpImage.Metadata.CicpInfo(1, 16, 6, true));
        await Assert.That(d.Metadata.ContentLightLevel).IsEqualTo(new SharpImage.Metadata.ContentLightLevel((ushort)maxCll, (ushort)maxPall));
        var gm = HeifCoder.DecodeGainMap(avif)!;
        await Assert.That(Describe(gm)).IsEqualTo(DescribePayload(tmap));
        await Assert.That(gm.AlternateCicp).IsEqualTo(new SharpImage.Metadata.CicpInfo(1, 13, 6, true));
        await Assert.That(gm.AlternateDepth).IsEqualTo(8);
    }
}
