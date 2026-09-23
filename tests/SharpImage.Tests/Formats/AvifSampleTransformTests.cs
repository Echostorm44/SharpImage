using System;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc -d 12,4 / 12,8 / 8,8: 16-bit AVIF through a 'sato' Sample Transform derived item. Decodes must equal avifdec
// --sato's 16-bit PNG (expected: SHA-256 of RGBA samples as little-endian u16, alpha 65535 when opaque); lossless
// encodes of the same PNG through AvifEncodeOptions.BitDepthExtension must decode to libavif's lossless result.
public sealed class AvifSampleTransformTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    private static string Hash16(ImageFrame d)
    {
        int w = (int)d.Columns, h = (int)d.Rows, ch = d.NumberOfChannels;
        var buf = new byte[w * h * 8];
        for (int y = 0; y < h; y++)
        {
            var row = d.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                {
                    int v = k < ch ? row[x * ch + k] : 65535;
                    buf[(y * w + x) * 8 + k * 2] = (byte)v;
                    buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    [Test]
    [Arguments("libavif_sato_12_4_alpha.avif", "433bae057fca3642")]
    [Arguments("libavif_sato_12_8_lossy.avif", "5b361f80bfe4b6b6")]
    [Arguments("libavif_sato_8_8_420.avif", "72f67bcc05ca170f")]
    public async Task Decode_MatchesAvifdecSato(string file, string sha16)
    {
        var d = HeifCoder.Decode(File.ReadAllBytes(Asset(file)));
        await Assert.That(Hash16(d)).IsEqualTo(sha16);
    }

    [Test]
    [Arguments(AvifBitDepthExtension.Bits12Plus4, AvifChromaSubsampling.Yuv444, "433bae057fca3642")]
    [Arguments(AvifBitDepthExtension.Bits12Plus8Overlap4, AvifChromaSubsampling.Yuv444, "433bae057fca3642")]
    [Arguments(AvifBitDepthExtension.Bits8Plus8, AvifChromaSubsampling.Yuv444, "433bae057fca3642")]
    [Arguments(AvifBitDepthExtension.Bits8Plus8, AvifChromaSubsampling.Yuv420, "72f67bcc05ca170f")]
    public async Task LosslessEncode_MatchesLibavif(AvifBitDepthExtension ext, AvifChromaSubsampling cs, string sha16)
    {
        var src = FormatRegistry.Read(Asset("sato_src_rgba16_64x48.png"));
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { BitDepthExtension = ext, Quality = 100, ChromaSubsampling = cs });
        var s = System.Text.Encoding.ASCII.GetString(avif);
        await Assert.That(s.Contains("sato")).IsTrue();
        await Assert.That(s.Contains("altr")).IsTrue();
        await Assert.That(Hash16(HeifCoder.Decode(avif))).IsEqualTo(sha16);
    }

    [Test]
    public async Task LossyEncode_RoundTripsClose()
    {
        var src = FormatRegistry.Read(Asset("sato_src_rgba16_64x48.png"));
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { BitDepthExtension = AvifBitDepthExtension.Bits12Plus8Overlap4, Quality = 60, ChromaSubsampling = AvifChromaSubsampling.Yuv444 });
        var d = HeifCoder.Decode(avif);
        double se = 0;
        long n = 0;
        for (int y = 0; y < (int)src.Rows; y++)
        {
            var a = src.GetPixelRow(y);
            var b = d.GetPixelRow(y);
            for (int i = 0; i < (int)src.Columns * 3; i++)
            {
                int x = i / 3, k = i % 3;
                double e = a[x * src.NumberOfChannels + k] - b[x * d.NumberOfChannels + k];
                se += e * e;
                n++;
            }
        }
        double psnr = 10 * Math.Log10(65535.0 * 65535.0 * n / Math.Max(se, 1));
        await Assert.That(psnr).IsGreaterThan(35);
    }
}
