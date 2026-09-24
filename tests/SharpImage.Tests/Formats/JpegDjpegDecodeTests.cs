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
