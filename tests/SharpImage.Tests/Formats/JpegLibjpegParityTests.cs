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

    // The formats libjpeg-turbo 3.1 decodes beyond 8-bit Huffman DCT: CMYK / YCCK (Adobe APP14 inverted samples come
    // back as ink amounts in a CMYK frame), 12-bit DCT, lossless (SOF3: precisions 6 / 12 / 16, predictors, point
    // transform) and arithmetic coding (SOF9 / SOF10, restarts). Expected: SHA-256 of the 16-bit samples of every
    // channel, from libjpeg-turbo's raw output (the dev harness ljdump) scaled v * 65535 / max rounded. The dev harness
    // matched all 519 files of a cjpeg / libjpeg-made corpus (sizes 1x1..67x45, all samplings, predictors 1-7).
    private static string Hash16(ImageFrame f)
    {
        int w = (int)f.Columns, h = (int)f.Rows, n = f.NumberOfChannels;
        var buf = new byte[w * h * n * 2];
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRow(y);
            for (int k = 0; k < w * n; k++) { buf[(y * w * n + k) * 2] = (byte)row[k]; buf[(y * w * n + k) * 2 + 1] = (byte)(row[k] >> 8); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    [Test]
    [Arguments("jpeg_libjpeg/ljt_ycck_prog.jpg", "e34b32216aacd353")]
    [Arguments("jpeg_libjpeg/ljt_cmyk_arith.jpg", "f06fe4463056dada")]
    [Arguments("jpeg_libjpeg/ljt_cmyk_noadobe.jpg", "82c408362031476f")]
    [Arguments("jpeg_libjpeg/ljt_p12_420.jpg", "2ecf50e1a37c8242")]
    [Arguments("jpeg_libjpeg/ljt_p12_arith_prog.jpg", "2ecf50e1a37c8242")]
    [Arguments("jpeg_libjpeg/ljt_ll16_psv7.jpg", "3cbaa7c08b851920")]
    [Arguments("jpeg_libjpeg/ljt_ll6_psv4_pt1.jpg", "6eb9265703f9fae0")]
    [Arguments("jpeg_libjpeg/ljt_ll12_psv5.jpg", "d7f8e8aeca0faef5")]
    [Arguments("jpeg_libjpeg/ljt_arith_prog.jpg", "26262238397e96b1")]
    [Arguments("jpeg_libjpeg/ljt_arith_rst.jpg", "26262238397e96b1")]
    public async Task Read_ExtendedFormats_MatchLibjpegTurbo(string asset, string sha16)
    {
        var img = JpegCoder.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", asset));
        await Assert.That(img.Colorspace).IsEqualTo(asset.Contains("cmyk") || asset.Contains("ycck") ? ColorspaceType.CMYK : ColorspaceType.SRGB);
        await Assert.That(Hash16(img)).IsEqualTo(sha16);
    }

    // Damaged files decode as libjpeg-turbo recovers them (its warnings, not errors): a progressive file cut short
    // (block smoothing estimates the missing AC bands), a restart marker out of sequence (jpeg_resync_to_restart leaves
    // the next interval empty) and a Motion-JPEG frame without DHT (the default Annex K tables). Expected: djpeg-style
    // 8-bit RGB from libjpeg-turbo 3.1 (ljdump). The differential fuzzer (4832 mutated files) matched every output.
    [Test]
    [Arguments("jpeg_libjpeg/corrupt_prog_truncated.jpg", "faf472cedb802c3a")]
    [Arguments("jpeg_libjpeg/corrupt_rst_sequence.jpg", "8c4c2eba757305c0")]
    [Arguments("jpeg_libjpeg/mjpeg_no_dht.jpg", "381a15e88374f474")]
    public async Task Read_DamagedFiles_RecoverLikeLibjpegTurbo(string asset, string sha16)
    {
        var img = JpegCoder.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", asset));
        await Assert.That(Hash8(img)).IsEqualTo(sha16);
    }

    // What libjpeg-turbo rejects fails with InvalidDataException (its error text); what it cannot decode either is
    // NotSupportedException; frames beyond the pixel limit fail before allocation.
    [Test]
    public async Task Read_Errors_AsLibjpegTurbo()
    {
        byte[] good = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "jpeg_libjpeg", "cjpeg_31x33_q20.jpg"));
        int dqt = FindMarker(good, 0xDB);
        var badDqt = (byte[])good.Clone();
        badDqt[dqt + 4] = 0x05;   // table index 5
        await Assert.That(() => JpegCoder.Read(new MemoryStream(badDqt))).Throws<InvalidDataException>().WithMessage("Bogus DQT index 5");
        var hierarchical = (byte[])good.Clone();
        hierarchical[FindMarker(good, 0xC0, 0xC1) + 1] = 0xC5;
        await Assert.That(() => JpegCoder.Read(new MemoryStream(hierarchical))).Throws<NotSupportedException>();
        await Assert.That(() => JpegCoder.Read(new MemoryStream(good), maxPixels: 31 * 33 - 1)).Throws<InvalidDataException>();
        await Assert.That(JpegCoder.Read(new MemoryStream(good), maxPixels: 31 * 33).Columns).IsEqualTo(31L);
        await Assert.That(() => JpegCoder.Read(new MemoryStream([0x89, 0x50, 0x4E, 0x47]))).Throws<InvalidDataException>();
    }

    private static int FindMarker(byte[] d, params int[] markers)
    {
        for (int i = 2; i + 1 < d.Length; i++) if (d[i] == 0xFF && Array.IndexOf(markers, (int)d[i + 1]) >= 0) return i;
        throw new InvalidOperationException();
    }
}
