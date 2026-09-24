using System;
using System.IO;
using System.Linq;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// YUV4MPEG2 like libavif: planes read / written at their coded precision (avifdec -o x.y4m output matches libavif
// byte-for-byte over the libavif test data, checked with the y4mdec probe), and avifenc's Y4M input behaviour — the
// planes are coded as they are, their layout and depth adopted when the options say Auto / 0.
public sealed class Y4mTests
{
    private static YuvImage Synthetic(int w, int h, int depth, AvifChromaSubsampling sub, bool full, bool alpha = false, int seed = 1)
    {
        var rnd = new Random(seed);
        int max = (1 << depth) - 1;
        int cw = sub is AvifChromaSubsampling.Yuv420 or AvifChromaSubsampling.Yuv422 ? (w + 1) / 2 : w;
        int ch = sub == AvifChromaSubsampling.Yuv420 ? (h + 1) / 2 : h;
        ushort[] Plane(int pw, int ph, int bias)
        {
            var p = new ushort[pw * ph];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                    p[y * pw + x] = (ushort)Math.Clamp(bias + (x * 37 + y * 23) % (max / 2) + rnd.Next(0, max / 16 + 1), 0, max);
            return p;
        }
        bool mono = sub == AvifChromaSubsampling.Yuv400;
        return new YuvImage
        {
            Width = w, Height = h, Depth = depth, Subsampling = sub, FullRange = full,
            Y = Plane(w, h, max / 8), U = mono ? null : Plane(cw, ch, max / 4), V = mono ? null : Plane(cw, ch, max / 3),
            Alpha = alpha ? Plane(w, h, max / 5) : null,
        };
    }

    private static byte[] ToY4m(params YuvImage[] frames)
    {
        using var ms = new MemoryStream();
        Y4mCoder.WriteYuv(ms, frames, 30, 1);
        return ms.ToArray();
    }

    [Test]
    [Arguments(8, AvifChromaSubsampling.Yuv420, false, false)]
    [Arguments(10, AvifChromaSubsampling.Yuv422, true, false)]
    [Arguments(12, AvifChromaSubsampling.Yuv444, false, false)]
    [Arguments(10, AvifChromaSubsampling.Yuv400, true, false)]
    [Arguments(8, AvifChromaSubsampling.Yuv444, true, true)]
    public async Task WriteThenReadIsExact(int depth, AvifChromaSubsampling sub, bool full, bool alpha)
    {
        var src = Synthetic(37, 21, depth, sub, full, alpha);
        var s = Y4mCoder.ReadYuv(ToY4m(src, Synthetic(37, 21, depth, sub, full, alpha, seed: 2)));
        await Assert.That(s.Frames.Count).IsEqualTo(2);
        await Assert.That(s.FrameRateNumerator).IsEqualTo(30);
        var f = s.Frames[0];
        await Assert.That((f.Width, f.Height, f.Depth, f.Subsampling, f.FullRange)).IsEqualTo((37, 21, depth, sub, full));
        await Assert.That(f.Y.SequenceEqual(src.Y)).IsTrue();
        if (src.U != null) await Assert.That(f.U!.SequenceEqual(src.U) && f.V!.SequenceEqual(src.V!)).IsTrue();
        await Assert.That(alpha ? f.Alpha!.SequenceEqual(src.Alpha!) : f.Alpha == null).IsTrue();
        await Assert.That(FormatRegistry.DetectFormat(ToY4m(src))).IsEqualTo(ImageFileFormat.Y4m);
    }

    [Test]
    [Arguments(8, AvifChromaSubsampling.Yuv420, false)]
    [Arguments(10, AvifChromaSubsampling.Yuv444, true)]
    [Arguments(12, AvifChromaSubsampling.Yuv422, false)]
    [Arguments(8, AvifChromaSubsampling.Yuv400, true)]
    public async Task LosslessAvifFromY4mKeepsThePlanes(int depth, AvifChromaSubsampling sub, bool full)
    {
        // avifenc -q 100 of a Y4M: the planes are coded as they are, in their own layout / depth / range.
        var src = Synthetic(70, 45, depth, sub, full);
        var image = Y4mCoder.Read(ToY4m(src));
        byte[] avif = HeifCoder.EncodeAvif(image, new AvifEncodeOptions { Quality = 100 });
        var back = HeifCoder.DecodeYuv(avif);
        await Assert.That((back.Depth, back.Subsampling, back.FullRange)).IsEqualTo((depth, sub, full));
        await Assert.That(back.Y.SequenceEqual(src.Y)).IsTrue();
        if (src.U != null) await Assert.That(back.U!.SequenceEqual(src.U) && back.V!.SequenceEqual(src.V!)).IsTrue();
    }

    [Test]
    public async Task Mpeg2SitingIsKept()
    {
        // C420mpeg2 planes coded as they are signal chroma_sample_position = vertical (sequence header and av1C).
        var src = Synthetic(40, 30, 8, AvifChromaSubsampling.Yuv420, false);
        var sited = new YuvImage { Width = src.Width, Height = src.Height, Depth = 8, Subsampling = src.Subsampling, FullRange = false,
            ChromaSamplePosition = 1, Y = src.Y, U = src.U, V = src.V };
        byte[] y4m = ToY4m(sited);
        await Assert.That(System.Text.Encoding.ASCII.GetString(y4m, 0, 60)).Contains("C420mpeg2");
        byte[] avif = HeifCoder.EncodeAvif(Y4mCoder.Read(y4m), new AvifEncodeOptions { Quality = 80 });
        await Assert.That(HeifCoder.DecodeYuv(avif).ChromaSamplePosition).IsEqualTo(1);
    }

    [Test]
    public async Task Y4mSequenceEncodesAsAvifSequence()
    {
        byte[] y4m = ToY4m(Synthetic(48, 32, 8, AvifChromaSubsampling.Yuv420, false, seed: 3),
            Synthetic(48, 32, 8, AvifChromaSubsampling.Yuv420, false, seed: 4), Synthetic(48, 32, 8, AvifChromaSubsampling.Yuv420, false, seed: 5));
        using var seq = Y4mCoder.ReadSequence(y4m);
        await Assert.That(seq.Count).IsEqualTo(3);
        await Assert.That(seq.Timescale).IsEqualTo(30L);
        byte[] avif = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 70 });
        using var back = HeifCoder.DecodeSequence(avif);
        await Assert.That(back.Count).IsEqualTo(3);
    }
}
