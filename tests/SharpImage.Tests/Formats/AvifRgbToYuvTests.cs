using System;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// RGB -> YUV as libavif's avifImageRGBToYUV does it: libyuv's fixed-point converters for 8-bit BT.601 (RGB: J/I 444,
// 422, 420; RGBA: J422/J420 and every limited-range layout), libavif's float path otherwise (RGBA 4:4:4 full range,
// other matrices and depths, YCgCo, premultiplied alpha). Coded losslessly, the decoded image must equal avifdec's
// decode of avifenc -q 100 on the same PNG (expected: SHA-256 of RGBA samples as 16-bit little-endian, 8-bit x 257,
// alpha 65535 when absent). The dev harness matched 268 configurations (RGB8 / RGBA8 / grey / RGB16 sources, 4:4:4 /
// 4:2:2 / 4:2:0 / 4:0:0, full / limited, matrices 0/1/6/8/9, 8/10/12-bit, premultiplied).
public sealed class AvifRgbToYuvTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    // depth8: our decoder keeps 16-bit precision where libavif has no libyuv route (e.g. YCgCo); avifdec writes 8-bit
    // PNGs for 8-bit images, so compare at 8 bits.
    internal static string Hash16Rgba(ImageFrame d, bool depth8 = false)
    {
        int w = (int)d.Columns, h = (int)d.Rows, ch = d.NumberOfChannels;
        var buf = new byte[w * h * 8];
        for (int y = 0; y < h; y++)
        {
            var row = d.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                {
                    int v = k < 3 ? row[x * ch + (ch >= 3 ? k : 0)] : d.HasAlpha ? row[x * ch + ch - 1] : 65535;
                    if (depth8) v = (v * 255 + 32767) / 65535 * 257;
                    buf[(y * w + x) * 8 + k * 2] = (byte)v;
                    buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    [Test]
    [Arguments("rgb2yuv_src_rgb8_37x29.png", 8, AvifChromaSubsampling.Yuv420, true, 6, false, "a2bdf5a57e3ae5e3")]
    [Arguments("rgb2yuv_src_rgb8_37x29.png", 8, AvifChromaSubsampling.Yuv444, true, 6, false, "1248463f91168850")]
    [Arguments("rgb2yuv_src_rgb8_37x29.png", 8, AvifChromaSubsampling.Yuv422, false, 6, false, "6e26f0be8db83a1e")]
    [Arguments("rgb2yuv_src_rgb8_37x29.png", 10, AvifChromaSubsampling.Yuv420, true, 9, false, "be8791f11ee932c5")]
    [Arguments("rgb2yuv_src_rgb8_37x29.png", 8, AvifChromaSubsampling.Yuv420, true, 8, false, "87268b5a2769aa64")]
    [Arguments("rgb2yuv_src_rgba8_37x29.png", 8, AvifChromaSubsampling.Yuv444, true, 6, false, "436ea3294360b6e0")]
    [Arguments("rgb2yuv_src_rgba8_37x29.png", 8, AvifChromaSubsampling.Yuv420, true, 6, false, "c02de2399671e8b4")]
    [Arguments("rgb2yuv_src_rgba8_37x29.png", 8, AvifChromaSubsampling.Yuv420, true, 6, true, "15e78880498e7a69")]
    [Arguments("rgb2yuv_src_rgba8_37x29.png", 8, AvifChromaSubsampling.Yuv444, false, 6, false, "a1336b44cdc7aea8")]
    public async Task Lossless_MatchesAvifenc(string png, int depth, AvifChromaSubsampling cs, bool full, int mc, bool prem, string sha16)
    {
        var src = FormatRegistry.Read(Asset(png));
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        {
            Quality = 100, BitDepth = depth, ChromaSubsampling = cs, FullRange = full, MatrixCoefficients = mc,
            ColorPrimaries = 1, TransferCharacteristics = 13, PremultiplyAlpha = prem,
        });
        await Assert.That(Hash16Rgba(HeifCoder.Decode(avif), depth == 8)).IsEqualTo(sha16);
    }

    // avifRGBImage.avoidLibYUV: the float path instead of libyuv (not avifenc's output any more, but more precise).
    [Test]
    public async Task AvoidLibyuv_UsesFloatPath()
    {
        var src = FormatRegistry.Read(Asset("rgb2yuv_src_rgb8_37x29.png"));
        var o = new AvifEncodeOptions { Quality = 100, BitDepth = 8, ChromaSubsampling = AvifChromaSubsampling.Yuv420 };
        string libyuv = Hash16Rgba(HeifCoder.Decode(HeifCoder.EncodeAvif(src, o)));
        o.AvoidLibyuv = true;
        string flt = Hash16Rgba(HeifCoder.Decode(HeifCoder.EncodeAvif(src, o)));
        await Assert.That(libyuv).IsEqualTo("a2bdf5a57e3ae5e3");
        await Assert.That(flt).IsNotEqualTo(libyuv);
    }

    // avifgainmaputil swapbase --ignore-profile -q 100 --qgain-map 100 on libavif's seine SDR + gain map file.
    [Test]
    public async Task SwapGainMapBase_MatchesAvifgainmaputil()
    {
        byte[] avif = HeifCoder.SwapGainMapBase(File.ReadAllBytes(Asset("libavif_seine_sdr_gainmap_srgb.avif")),
            new AvifEncodeOptions { Quality = 100, QualityGainMap = 100 }, ignoreIccProfile: true);
        await Assert.That(Hash16Rgba(HeifCoder.Decode(avif))).IsEqualTo("79293bf002126eea");
    }
}
