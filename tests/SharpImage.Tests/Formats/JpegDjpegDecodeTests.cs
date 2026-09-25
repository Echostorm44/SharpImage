using System;
using System.IO;
using System.Text;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// JpegDecodeOptions reproduce djpeg's switches sample for sample: scaled output through libjpeg's scaled IDCTs, the fast
// and float IDCTs, replication upsampling, greyscale output. References: djpeg 3.1.4 -pnm (TestAssets/jpeg_djpeg); the
// full sweep (16 scales, IDCTs, upsampling and output modes over ~200 files, 8 / 12-bit, lossless) ran with the djpeg
// probe.
public sealed class JpegDjpegDecodeTests
{
    private static string Dir(string sub) => Path.Combine(AppContext.BaseDirectory, "TestAssets", sub);

    private static (int W, int H, int Max, int[] Rgb) ReadPnm(byte[] b)
    {
        int pos = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)b[pos])) pos++;
            int s = pos;
            while (!char.IsWhiteSpace((char)b[pos])) pos++;
            return Encoding.ASCII.GetString(b, s, pos - s);
        }
        string kind = Token();
        int w = int.Parse(Token()), h = int.Parse(Token()), max = int.Parse(Token());
        pos++;
        int ch = kind == "P6" ? 3 : 1, bpp = max > 255 ? 2 : 1;
        var rgb = new int[w * h * 3];
        for (int i = 0; i < w * h; i++)
            for (int c = 0; c < 3; c++)
            {
                int k = pos + (i * ch + (ch == 3 ? c : 0)) * bpp;
                rgb[i * 3 + c] = bpp == 2 ? (b[k] << 8) | b[k + 1] : b[k];
            }
        return (w, h, max, rgb);
    }

    [Test]
    [Arguments("cjpeg_31x33_prog420", "scale1_8")]
    [Arguments("cjpeg_31x33_prog420", "scale3_8")]
    [Arguments("cjpeg_31x33_prog420", "scale1_2")]
    [Arguments("cjpeg_31x33_s411", "scale5_8")]
    [Arguments("cjpeg_31x33_s22_21", "scale13_8")]
    [Arguments("cjpeg_31x33_prog420", "scale2")]
    [Arguments("cjpeg_31x33_q20", "fast")]
    [Arguments("cjpeg_31x33_q20", "float")]
    [Arguments("cjpeg_31x33_prog420", "nosmooth")]
    [Arguments("cjpeg_31x33_prog420", "grey")]
    [Arguments("cjpeg_31x33_rgb420", "rgbgrey")]
    [Arguments("ljt_p12_420", "p12_scale3_4")]
    [Arguments("ljt_p12_420", "p12_float")]
    [Arguments("cjpeg_31x33_prog420", "crop")]
    [Arguments("cjpeg_31x33_s22_21", "crop_scale")]
    [Arguments("corrupt_prog_truncated", "crop_smooth")]
    [Arguments("cjpeg_31x33_prog420", "skip")]
    [Arguments("ljt_p12_420", "p12_skip")]
    [Arguments("cjpeg_31x33_prog420", "colors16")]
    [Arguments("cjpeg_31x33_prog420", "colors9_none")]
    [Arguments("cjpeg_31x33_q20", "onepass27_ordered")]
    [Arguments("cjpeg_31x33_q20", "onepass40")]
    [Arguments("cjpeg_31x33_grey", "grey6")]
    [Arguments("cjpeg_31x33_s411", "map")]
    [Arguments("cjpeg_31x33_prog420", "rgb565")]
    [Arguments("cjpeg_31x33_s22_21", "rgb565_nodither")]
    public async Task MatchesDjpeg(string file, string mode)
    {
        var o = mode switch
        {
            "scale1_8" => new JpegDecodeOptions { ScaleNumerator = 1, ScaleDenominator = 8 },
            "scale3_8" => new JpegDecodeOptions { ScaleNumerator = 3, ScaleDenominator = 8 },
            "scale1_2" => new JpegDecodeOptions { ScaleNumerator = 1, ScaleDenominator = 2 },
            "scale5_8" => new JpegDecodeOptions { ScaleNumerator = 5, ScaleDenominator = 8 },
            "scale13_8" => new JpegDecodeOptions { ScaleNumerator = 13, ScaleDenominator = 8 },
            "scale2" => new JpegDecodeOptions { ScaleNumerator = 2, ScaleDenominator = 1 },
            "fast" => new JpegDecodeOptions { Dct = JpegDctMethod.IntegerFast, FancyUpsampling = false },
            "float" => new JpegDecodeOptions { Dct = JpegDctMethod.Float },
            "nosmooth" => new JpegDecodeOptions { FancyUpsampling = false },
            "grey" or "rgbgrey" => new JpegDecodeOptions { Grayscale = true },
            "p12_scale3_4" => new JpegDecodeOptions { ScaleNumerator = 3, ScaleDenominator = 4 },
            "p12_float" => new JpegDecodeOptions { Dct = JpegDctMethod.Float },
            // the region's edges upsample (and block-smooth) like image edges; X moves down to the iMCU boundary 16
            "crop" => new JpegDecodeOptions { Crop = (17, 8, 13, 9) },
            "crop_scale" => new JpegDecodeOptions { Crop = (9, 3, 7, 5), ScaleNumerator = 3, ScaleDenominator = 4 },
            "crop_smooth" => new JpegDecodeOptions { Crop = (16, 8, 17, 9) },
            "skip" => new JpegDecodeOptions { SkipRows = (5, 20) },
            "p12_skip" => new JpegDecodeOptions { SkipRows = (1, 7), FancyUpsampling = false },
            // djpeg -colors / -dither / -onepass / -map (jquant1.c, jquant2.c)
            "colors16" => new JpegDecodeOptions { QuantizeColors = 16 },
            "colors9_none" => new JpegDecodeOptions { QuantizeColors = 9, Dither = JpegDitherMode.None },
            "onepass27_ordered" => new JpegDecodeOptions { QuantizeColors = 27, TwoPassQuantize = false, Dither = JpegDitherMode.Ordered },
            "onepass40" => new JpegDecodeOptions { QuantizeColors = 40, TwoPassQuantize = false },
            "grey6" => new JpegDecodeOptions { QuantizeColors = 6 },
            "map" => new JpegDecodeOptions { QuantizeColormap = MapColors() },
            // djpeg -rgb565 (compared with its BMP output)
            "rgb565" => new JpegDecodeOptions { Rgb565 = true },
            "rgb565_nodither" => new JpegDecodeOptions { Rgb565 = true, Dither = JpegDitherMode.None, FancyUpsampling = false },
            _ => throw new ArgumentException(mode),
        };
        using var img = JpegCoder.Read(new MemoryStream(File.ReadAllBytes(Path.Combine(Dir("jpeg_libjpeg"), file + ".jpg"))), o);
        var (w, h, max, rgb) = ReadPnm(File.ReadAllBytes(Path.Combine(Dir("jpeg_djpeg"), file + "_" + mode + ".pnm")));
        await Assert.That(((int)img.Columns, (int)img.Rows)).IsEqualTo((w, h));
        int mismatches = 0;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    int v = (int)(((long)row[x * img.NumberOfChannels + c] * max + 32767) / 65535);
                    if (v != rgb[(y * w + x) * 3 + c]) mismatches++;
                }
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    [Arguments("corrupt_prog_truncated", "Premature end of JPEG file")]
    [Arguments("corrupt_rst_sequence", "Corrupt JPEG data: found marker 0xd2 instead of RST1")]
    public async Task StrictFailsWhereDjpegStrictDoes(string file, string message)
    {
        // djpeg recovers from these with a warning; djpeg -strict makes the warning fatal (checked against djpeg over
        // 4000 mutated files with the djstrict probe run: identical accept / reject decisions).
        byte[] jpg = File.ReadAllBytes(Path.Combine(Dir("jpeg_libjpeg"), file + ".jpg"));
        using (var lenient = JpegCoder.Read(new MemoryStream(jpg))) await Assert.That(lenient.Columns).IsGreaterThan(0u);
        await Assert.That(() => JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { Strict = true }))
            .Throws<InvalidDataException>().WithMessage(message);
    }

    [Test]
    public async Task MaxScansLimitsProgressiveFiles()
    {
        byte[] prog = File.ReadAllBytes(Path.Combine(Dir("jpeg_libjpeg"), "cjpeg_31x33_prog420.jpg"));   // 10 scans
        using (var ok = JpegCoder.Read(new MemoryStream(prog), new JpegDecodeOptions { MaxScans = 10 })) await Assert.That(ok.Columns).IsEqualTo(31u);
        await Assert.That(() => JpegCoder.Read(new MemoryStream(prog), new JpegDecodeOptions { MaxScans = 9 }))
            .Throws<InvalidDataException>().WithMessage("Scan number 10 exceeds maximum scans (9)");
    }

    private static (int, int, int)[] MapColors()
    {
        var (w, h, _, rgb) = ReadPnm(File.ReadAllBytes(Path.Combine(Dir("jpeg_djpeg"), "map12.ppm")));
        return Enumerable.Range(0, w * h).Select(i => (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2])).Distinct().ToArray();
    }

    [Test]
    public async Task QuantizedFramesCarryTheirColormap()
    {
        byte[] jpg = File.ReadAllBytes(Path.Combine(Dir("jpeg_libjpeg"), "cjpeg_31x33_prog420.jpg"));
        using var img = JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { QuantizeColors = 16 });
        await Assert.That(img.ColormapSize).IsLessThanOrEqualTo(16);
        var pal = img.Colormap!.Take(img.ColormapSize).Select(p => ((int)p.Red, (int)p.Green, (int)p.Blue)).ToHashSet();
        int outside = 0;
        for (int y = 0; y < (int)img.Rows; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < (int)img.Columns; x++)
                if (!pal.Contains((row[x * 3], row[x * 3 + 1], row[x * 3 + 2]))) outside++;
        }
        await Assert.That(outside).IsEqualTo(0);
        // libjpeg: the two-pass quantizer needs 8 colors or more; skipping rows needs one pass
        await Assert.That(() => JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { QuantizeColors = 7 })).Throws<ArgumentException>();
        await Assert.That(() => JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { QuantizeColors = 16, SkipRows = (1, 2) }))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task CropReportsItsAlignedOrigin()
    {
        byte[] jpg = File.ReadAllBytes(Path.Combine(Dir("jpeg_libjpeg"), "cjpeg_31x33_prog420.jpg"));
        using var img = JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { Crop = (17, 8, 13, 9) });
        await Assert.That((img.Page.X, img.Page.Y, (int)img.Columns, (int)img.Rows)).IsEqualTo((16, 8, 14, 9));
        await Assert.That(() => JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { Crop = (20, 0, 12, 5) })).Throws<ArgumentException>();
    }

    [Test]
    public async Task ScaleIsTheSmallestEighthAtOrAboveTheRequest()
    {
        // jdmaster.c: 1/3 -> 3/8 (8 * 1 <= 3 * 3), 9/8 -> 9/8, 3/1 -> 16/8 (the largest)
        byte[] jpg = File.ReadAllBytes(Path.Combine(Dir("jpeg_libjpeg"), "cjpeg_31x33_q20.jpg"));
        using var third = JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { ScaleNumerator = 1, ScaleDenominator = 3 });
        await Assert.That(((int)third.Columns, (int)third.Rows)).IsEqualTo((12, 13));
        using var triple = JpegCoder.Read(new MemoryStream(jpg), new JpegDecodeOptions { ScaleNumerator = 3, ScaleDenominator = 1 });
        await Assert.That(((int)triple.Columns, (int)triple.Rows)).IsEqualTo((62, 66));
    }
}
