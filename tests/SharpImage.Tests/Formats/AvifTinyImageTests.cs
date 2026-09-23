using System;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Frames below 8 px (libavif encodes down to 1x1): every layout / depth / alpha / lossless mode must encode and decode
// back at the right size — lossless exactly. The streams were checked against dav1d and avifdec (libaom + libavif RGB,
// pixel-identical to our decoder) by the dev harness Av1HbdVerify.TinyProbe.
public sealed class AvifTinyImageTests
{
    private static ImageFrame Pattern(int w, int h, bool alpha)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int v = (x * 37 + y * 91 + 13) % 256;
                row[x * ch] = (ushort)(v * 257);
                row[x * ch + 1] = (ushort)((255 - v) * 257);
                row[x * ch + 2] = (ushort)((v * 3) % 256 * 257);
                if (alpha) row[x * ch + 3] = (ushort)(((x + y) * 50 + 30) % 256 * 257);
            }
        }
        return f;
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 2)]
    [Arguments(4, 4)]
    [Arguments(3, 5)]
    [Arguments(7, 1)]
    [Arguments(1, 9)]
    [Arguments(6, 13)]
    public async Task TinyFrames_EncodeAndDecode(int w, int h)
    {
        var modes = new (AvifEncodeOptions Opt, bool Alpha)[]
        {
            (new AvifEncodeOptions { Qp = 20 }, false),
            (new AvifEncodeOptions { Qp = 20, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }, false),
            (new AvifEncodeOptions { Qp = 20, ChromaSubsampling = AvifChromaSubsampling.Yuv422 }, false),
            (new AvifEncodeOptions { Qp = 20, BitDepth = 10 }, true),
            (new AvifEncodeOptions { Qp = 20, BitDepth = 12, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }, false),
        };
        foreach (var (opt, alpha) in modes)
        {
            var d = HeifCoder.Decode(HeifCoder.EncodeAvif(Pattern(w, h, alpha), opt));
            await Assert.That((int)d.Columns).IsEqualTo(w);
            await Assert.That((int)d.Rows).IsEqualTo(h);
            await Assert.That(d.HasAlpha).IsEqualTo(alpha);
        }

        // Lossless: exact.
        var src = Pattern(w, h, true);
        var ll = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Lossless = true }));
        long diff = 0;
        for (int y = 0; y < h; y++)
        {
            var a = src.GetPixelRow(y);
            var b = ll.GetPixelRow(y);
            for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        }
        await Assert.That(diff).IsEqualTo(0L);
    }
}
