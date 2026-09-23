using System;
using System.Security.Cryptography;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// AVIF film grain encode (the decode side is Av1ConformanceDecodeTests.FilmGrain_ByteExactVsDav1d): parameters are
// signalled on the colour item only, in libaom's conventions (test vectors, grain tables). Cross-checked in the dev
// harness (Av1HbdVerify.FilmGrainEncode): all 16 test vectors plus 10/12-bit, 4:2:2/4:4:4, grey, limited range,
// odd sizes and alpha decode byte-exact in libdav1d with and without grain, and libavif's RGBA matches ours.
public sealed class AvifFilmGrainTests
{
    private static ImageFrame Photo(int w, int h, bool alpha = false)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = (ushort)(20000 + 30000 * Math.Sin(x * 0.05) * Math.Cos(y * 0.04) + 10000);
                row[x * ch + 1] = (ushort)(x * 65535 / (w - 1));
                row[x * ch + 2] = (ushort)(y * 65535 / (h - 1));
                if (alpha) row[x * ch + 3] = (ushort)(30000 + x * 200);
            }
        }
        return f;
    }

    private static string Md5(DecodedVideoFrame f) => Convert.ToHexString(MD5.HashData(f.YPlane.Span[..(f.Width * f.Height)]));

    [Test]
    public async Task Grain_IsSignalledOnTheColourItemOnly()
    {
        byte[] avif = HeifCoder.EncodeAvif(Photo(96, 64, alpha: true), new AvifEncodeOptions { FilmGrain = AvifFilmGrain.TestVector(1) });
        var box = HeifContainer.Parse(avif);
        byte[] colour = box.ItemData(box.PrimaryId)!;
        int alphaId = -1;
        foreach (var kv in box.Items) if (kv.Key != box.PrimaryId && kv.Value.Type == "av01") alphaId = kv.Key;
        byte[] alpha = box.ItemData(alphaId)!;

        using (var g = new Av1Decoder().Decode(colour, 0, true))
        using (var n = new Av1Decoder { ApplyFilmGrain = false }.Decode(colour, 0, true))
            await Assert.That(Md5(g!)).IsNotEqualTo(Md5(n!));
        using (var g = new Av1Decoder().Decode(alpha, 0, true))
        using (var n = new Av1Decoder { ApplyFilmGrain = false }.Decode(alpha, 0, true))
            await Assert.That(Md5(g!)).IsEqualTo(Md5(n!));
    }

    [Test]
    public async Task NoGrain_LeavesStreamsUnchanged()
    {
        // FilmGrain = null must not perturb anything: the grain-free decode of a grain stream equals a plain encode.
        var img = Photo(96, 64);
        byte[] plain = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { BitDepth = 10 });
        byte[] grain = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { BitDepth = 10, FilmGrain = AvifFilmGrain.TestVector(2) });
        var pb = HeifContainer.Parse(plain); var gb = HeifContainer.Parse(grain);
        using var p = new Av1Decoder().Decode(pb.ItemData(pb.PrimaryId)!, 0, true);
        using var g = new Av1Decoder { ApplyFilmGrain = false }.Decode(gb.ItemData(gb.PrimaryId)!, 0, true);
        await Assert.That(p!.YPlane16.ToArray()).IsEquivalentTo(g!.YPlane16.ToArray());
    }

    [Test]
    public async Task Table_RoundTrips_AllTestVectors()
    {
        for (int v = 1; v <= 16; v++)
        {
            var a = AvifFilmGrain.TestVector(v);
            a.ClipToRestrictedRange = false;   // not carried by grain tables
            var b = AvifFilmGrain.ParseTable(a.ToTable());
            await Assert.That(b.ToTable()).IsEqualTo(a.ToTable());
            await Assert.That(b.ScalingPointsY).IsEquivalentTo(a.ScalingPointsY);
            await Assert.That(b.ArCoeffsCr).IsEquivalentTo(a.ArCoeffsCr);
        }
    }

    [Test]
    public async Task ParseTable_ReadsLibaomFormat()
    {
        // As written by libaom's aom_film_grain_table_write (noise model output / aomenc --film-grain-table input).
        const string table = "filmgrn1\nE 0 9223372036854775807 1 7391 1\n\tp 1 7 0 11 0 1 128 192 256 128 192 256\n" +
                             "\tsY 2  0 20 255 40\n\tsCb 1 128 10\n\tsCr 0\n\tcY 3 -5 8 12\n\tcCb 1 2 3 4 5\n\tcCr 0 0 0 0 0\n";
        var g = AvifFilmGrain.ParseTable(table);
        await Assert.That((int)g.RandomSeed).IsEqualTo(7391);
        await Assert.That(g.ArCoeffLag).IsEqualTo(1);
        await Assert.That(g.ScalingShift).IsEqualTo(11);
        await Assert.That(g.OverlapFlag).IsTrue();
        await Assert.That(g.ScalingPointsY).IsEquivalentTo(new[] { (0, 20), (255, 40) });
        await Assert.That(g.ScalingPointsCb).IsEquivalentTo(new[] { (128, 10) });
        await Assert.That(g.ArCoeffsY[..4]).IsEquivalentTo(new[] { 3, -5, 8, 12 });
        await Assert.That(g.ArCoeffsCb[..5]).IsEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    public async Task InvalidParameters_AreRejected()
    {
        var img = Photo(64, 48);
        var decreasing = new AvifFilmGrain { ScalingPointsY = [(100, 20), (50, 30)] };
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { FilmGrain = decreasing })).Throws<ArgumentException>();
        // 4:2:0 needs both chroma planes or neither.
        var oneChroma = new AvifFilmGrain { ScalingPointsY = [(0, 20)], ScalingPointsCb = [(0, 20)] };
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { FilmGrain = oneChroma })).Throws<ArgumentException>();
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { FilmGrain = AvifFilmGrain.TestVector(1), Lossless = true }))
            .Throws<ArgumentException>();
        await Assert.That(() => AvifFilmGrain.TestVector(17)).Throws<ArgumentOutOfRangeException>();
        // ...but 4:4:4 may carry a single chroma plane's grain.
        byte[] ok = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { FilmGrain = oneChroma, ChromaSubsampling = AvifChromaSubsampling.Yuv444 });
        await Assert.That(HeifCoder.Decode(ok).Columns).IsEqualTo(64u);
    }
}
