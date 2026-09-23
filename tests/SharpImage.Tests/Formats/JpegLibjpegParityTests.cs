using System;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// JpegCoder.Read decodes exactly as libjpeg-turbo 3.1 does by default (islow IDCT, fancy upsampling per its upsampler
// selection, fixed-point YCbCr -> RGB). Expected: SHA-256 of the 8-bit RGB samples of djpeg -ppm (the cjpeg-made files:
// 4:4:0, 4:1:1, 4:1:0, mixed per-component sampling, RGB transform, SOF1 with 16-bit quantisation tables, progressive,
// restart markers, tiny widths that take the replicating upsamplers, greyscale) or of Pillow's decode (the other
// assets). The dev harness matched 272 files (Pillow and cjpeg corpora).
public sealed class JpegLibjpegParityTests
{
    private static string Hash8(ImageFrame f)
    {
        int w = (int)f.Columns, h = (int)f.Rows, n = f.NumberOfChannels;
        var buf = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++) buf[(y * w + x) * 3 + k] = Quantum.ScaleToByte(row[x * n + (n >= 3 ? k : 0)]);
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    [Test]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_s440.jpg", "1d5713419a251a8b")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_s12x.jpg", "1d5713419a251a8b")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_s411.jpg", "9a136581aa1e8599")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_s410.jpg", "d10e4e1b7ca820b3")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_s22_21.jpg", "b2c10a25bd4e5a1f")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_rgb420.jpg", "cb8475a09ec238b1")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_q20.jpg", "381a15e88374f474")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_prog420.jpg", "e431ab75f71f4323")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_rst.jpg", "e431ab75f71f4323")]
    [Arguments("jpeg_libjpeg/cjpeg_3x2_s420.jpg", "ca693a7316a576c9")]
    [Arguments("jpeg_libjpeg/cjpeg_2x3_s422.jpg", "2a855f9252390909")]
    [Arguments("jpeg_libjpeg/cjpeg_7x3_s420.jpg", "636eae255584ef9d")]
    [Arguments("jpeg_libjpeg/cjpeg_31x33_grey.jpg", "7c342d293ceaaef8")]
    [Arguments("landscape.jpg", "8b9ceadc43e2d3d4")]
    [Arguments("landscape_progressive.jpg", "8b9ceadc43e2d3d4")]
    [Arguments("peppers.jpg", "26b106b22fe64b1b")]
    [Arguments("photo_small.jpg", "8047845b51449394")]
    [Arguments("progressive_yuv420.jpg", "fd02f8d76ea75f44")]
    [Arguments("exif_sample.jpg", "6577abef190a2998")]
    public async Task Read_MatchesLibjpegTurbo(string asset, string sha16)
    {
        var img = JpegCoder.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", asset));
        await Assert.That(Hash8(img)).IsEqualTo(sha16);
    }
}
