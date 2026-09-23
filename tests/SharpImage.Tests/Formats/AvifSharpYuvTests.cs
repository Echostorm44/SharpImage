using System;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// avifenc --sharpyuv: AvifEncodeOptions.SharpYuv runs a port of libsharpyuv. Coded losslessly (quality 100, 4:2:0), the
// decoded RGB must equal avifdec's decode of avifenc --sharpyuv -q 100 -y 420 on the same PNG (expected: SHA-256 of
// avifdec's PNG samples as little-endian u16). The dev harness matched 10 configurations (sRGB, BT.709, PQ, HLG,
// limited, 8/10/12-bit, 16-bit sources).
public sealed class AvifSharpYuvTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    [Test]
    [Arguments("sharpyuv_src_rgb8_37x29.png", 8, null, null, null, "13f10b1e34a04922")]
    [Arguments("sharpyuv_src_rgb16_37x29.png", 10, 9, 16, 9, "f5110a7366de5eed")]
    [Arguments("sharpyuv_src_rgb16_37x29.png", 12, null, null, null, "7ad530e808846b9e")]
    public async Task LosslessSharpYuv_MatchesLibsharpyuv(string png, int depth, int? cp, int? tc, int? mc, string sha16)
    {
        var src = FormatRegistry.Read(Asset(png));
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        {
            SharpYuv = true, Quality = 100, ChromaSubsampling = AvifChromaSubsampling.Yuv420, BitDepth = depth,
            ColorPrimaries = cp, TransferCharacteristics = tc, MatrixCoefficients = mc,
        });
        var d = HeifCoder.Decode(avif);
        int w = (int)d.Columns, h = (int)d.Rows, ch = d.NumberOfChannels;
        var buf = new byte[w * h * 6];
        for (int y = 0; y < h; y++)
        {
            var row = d.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                {
                    int v = row[x * ch + k];
                    if (depth == 8) v = (v * 255 + 32767) / 65535;   // avifdec writes an 8-bit PNG for 8-bit images
                    buf[(y * w + x) * 6 + k * 2] = (byte)v;
                    buf[(y * w + x) * 6 + k * 2 + 1] = (byte)(v >> 8);
                }
        }
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(buf))[..16]).IsEqualTo(sha16);
    }
}
