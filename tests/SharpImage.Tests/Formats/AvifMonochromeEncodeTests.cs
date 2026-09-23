using System;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc -y 400: a colour source coded as 4:0:0, luma computed exactly as libavif's avifImageRGBToYUV does —
// libyuv J400 ((77r + 150g + 29b + 128) >> 8) for 8-bit full-range BT.601 sources, else the float path's
// avifRoundf(kr R + kg G + kb B) at the coded depth. Lossless coding makes the decoded grey the computed luma; the
// dev harness (Av1HbdVerify.EncodeProbe) confirmed identical avifdec output vs avifenc --lossless -y 400 for
// 8/10/12-bit, 16-bit sources, limited range, BT.709 and alpha.
public sealed class AvifMonochromeEncodeTests
{
    private static ImageFrame Colour(int w, int h, bool alpha)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = (ushort)((x * 29 + y * 7) % 256 * 257);
                row[x * ch + 1] = (ushort)((x * 3 + y * 41 + 90) % 256 * 257);
                row[x * ch + 2] = (ushort)((x * 17 + y * 13 + 200) % 256 * 257);
                if (alpha) row[x * ch + 3] = (ushort)((x * 5 + y * 11) % 256 * 257);
            }
        }
        return f;
    }

    [Test]
    public async Task Lossless8Bit_IsLibyuvJ400()
    {
        var src = Colour(37, 29, alpha: true);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Lossless = true, ChromaSubsampling = AvifChromaSubsampling.Yuv400 });
        var c = HeifContainer.Parse(avif);
        await Assert.That(c.Items[c.PrimaryId].Type).IsEqualTo("av01");
        var d = HeifCoder.Decode(avif);
        await Assert.That(d.HasAlpha).IsTrue();
        int bad = 0;
        for (int y = 0; y < 29; y++)
        {
            var s = src.GetPixelRow(y);
            var o = d.GetPixelRow(y);
            for (int x = 0; x < 37; x++)
            {
                int r = s[x * 4] / 257, g = s[x * 4 + 1] / 257, b = s[x * 4 + 2] / 257;
                int want = (77 * r + 150 * g + 29 * b + 128) >> 8;
                int dch = d.NumberOfChannels;
                for (int k = 0; k < 3; k++) if (o[x * dch + k] / 257 != want) bad++;
                if (o[x * dch + dch - 1] != s[x * 4 + 3]) bad++;
            }
        }
        await Assert.That(bad).IsEqualTo(0);
    }

    [Test]
    public async Task Lossless10Bit_IsLibavifFloatLuma()
    {
        var src = Colour(20, 12, alpha: false);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        {
            Lossless = true, BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv400, MatrixCoefficients = 1,
        });
        var d = HeifCoder.Decode(avif);
        await Assert.That(d.Metadata.Cicp!.MatrixCoefficients).IsEqualTo(1);
        const float kr = 0.2126f, kb = 0.0722f, kg = 1.0f - kr - kb;
        int bad = 0;
        for (int y = 0; y < 12; y++)
        {
            var s = src.GetPixelRow(y);
            var o = d.GetPixelRow(y);
            for (int x = 0; x < 20; x++)
            {
                float R = s[x * 3] / 65535.0f, G = s[x * 3 + 1] / 65535.0f, B = s[x * 3 + 2] / 65535.0f;
                int want = (int)MathF.Floor((kr * R + kg * G + kb * B) * 1023.0f + 0.5f);
                if ((o[x * d.NumberOfChannels] * 1023 + 32767) / 65535 != want) bad++;
            }
        }
        await Assert.That(bad).IsEqualTo(0);
    }

    [Test]
    public async Task Lossy_IsCodedMonochrome()
    {
        byte[] avif = HeifCoder.EncodeAvif(Colour(64, 48, alpha: false), new AvifEncodeOptions { Qp = 25, ChromaSubsampling = AvifChromaSubsampling.Yuv400 });
        var c = HeifContainer.Parse(avif);
        // av1C byte 2 bit 4 = monochrome.
        var p = c.Property(c.PrimaryId, "av1C")!.Value;
        await Assert.That((c.Data[p.Off + 2] & 0x10) != 0).IsTrue();
        await Assert.That(() => HeifCoder.EncodeAvif(Colour(8, 8, false),
            new AvifEncodeOptions { ChromaSubsampling = AvifChromaSubsampling.Yuv400, MatrixCoefficients = 0 })).ThrowsNothing();
    }
}
