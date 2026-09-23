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
// Denoise + grain estimation (DenoiseNoiseLevel) ports libaom's noise model: in Av1HbdVerify.GrainEstimate the grain
// tables and denoised planes equal libaom 3.15's C exactly on real photos (8/10-bit, 4:2:0/4:4:4, grey, 4:2:2 = none,
// all-intra estimated and requested levels); ffmpeg's bundled libaom 3.12.1 differs only by 3.15's dither and y_corr
// fixes (its own 3.12.1 C reproduces it exactly).
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

    // Deterministic noisy YUV (xorshift32): a smooth ramp plus uniform noise in luma, noisy flat chroma.
    private static (ushort[] Y, ushort[] U, ushort[] V) SynthYuv(int w, int h, int bd, int ssX, int ssY)
    {
        uint s = 2463534242;
        uint Rng() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
        int mx = (1 << bd) - 1, k = 1 << (bd - 8);
        var y = new ushort[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int x = 0; x < w; x++)
            {
                int b = (60 + x * 100 / w + yy * 50 / h) * k;
                int n = ((int)((Rng() >> 24) % 21) - 10) * k;
                y[yy * w + x] = (ushort)Math.Clamp(b + n, 0, mx);
            }
        int cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY;
        ushort[] Chroma()
        {
            var c = new ushort[cw * ch];
            for (int i = 0; i < c.Length; i++) c[i] = (ushort)Math.Clamp(128 * k + ((int)((Rng() >> 24) % 9) - 4) * k, 0, mx);
            return c;
        }
        var u = Chroma();
        return (y, u, Chroma());
    }

    // The noise model port against libaom 3.15's own C (aom_denoise_and_model_run built from aom_dsp/noise_model.c,
    // noise_util.c, fft.c; level 2.5, block 32): identical grain table and byte-identical denoised planes.
    [Test]
    [Arguments(128, 96, 8, 1, 1, "402c058d25f31df811a67cfb188c3e1f",
        "\tp 3 9 0 11 0 1 128 192 256 128 192 256\n\tsY 6  0 211 54 210 107 207 161 171 201 173 255 175\n\tsCb 5 0 138 54 137 107 135 174 137 255 139\n\tsCr 7 0 148 54 147 107 145 161 135 201 137 215 137 255 138\n\tcY -1 3 14 -3 6 -1 -7 4 9 -4 -15 4 -2 5 8 0 -8 -27 -4 10 -1 -9 -16 -31\n\tcCb -4 8 -9 33 6 -15 13 24 74 2 -41 4 -16 -18 45 8 -51 -38 -15 -39 -33 -24 35 -10 -125\n\tcCr 14 -14 49 -52 -19 -30 35 45 13 -36 -51 13 -16 30 -26 -52 47 -46 -6 -4 18 38 -12 -14 15\n")]
    [Arguments(160, 128, 10, 0, 0, "f7e5cb411aa7e297e7ac2967199697b2",
        "\tp 3 9 0 11 0 1 128 192 256 128 192 256\n\tsY 2  0 194 255 187\n\tsCb 2 0 152 255 145\n\tsCr 4 0 147 162 142 215 145 255 145\n\tcY -13 3 15 -12 14 1 -8 -6 -4 0 -19 15 -2 6 1 0 -11 -15 -5 7 -17 -10 -24 -12\n\tcCb -2 6 1 -29 8 -28 14 -4 18 5 -13 -22 16 -18 -14 3 -4 4 3 8 3 -14 -11 9 -1\n\tcCr -5 -5 -26 -15 -16 -34 -9 43 17 -5 -11 -5 -3 -6 -4 8 -1 -8 6 17 15 -15 12 -9 12\n")]
    public async Task NoiseModel_MatchesLibaomExactly(int w, int h, int bd, int ssX, int ssY, string denoisedMd5, string tableBody)
    {
        var (y, u, v) = SynthYuv(w, h, bd, ssX, ssY);
        var g = Av1NoiseModel.DenoiseAndModel(y, u, v, w, h, ssX, ssY, bd, 2.5f, 32, out var den);
        await Assert.That(g).IsNotNull();
        await Assert.That(g!.ToTable()).IsEqualTo("filmgrn1\nE 0 9223372036854775807 1 7391 1\n" + tableBody);
        using var ms = new System.IO.MemoryStream();
        foreach (var pl in den)
            foreach (var s in pl!)
            {
                ms.WriteByte((byte)s);
                if (bd > 8) ms.WriteByte((byte)(s >> 8));
            }
        await Assert.That(Convert.ToHexString(MD5.HashData(ms.ToArray())).ToLowerInvariant()).IsEqualTo(denoisedMd5);
    }

    private static ImageFrame Noisy(int w, int h, bool alpha)
    {
        var f = Photo(w, h, alpha);
        var rng = new Random(5);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                    row[x * ch + c] = (ushort)Math.Clamp(row[x * ch + c] + rng.Next(-2500, 2500), 0, 65535);
        }
        return f;
    }

    [Test]
    public async Task Denoise_SignalsEstimatedGrain_OnTheColourItem()
    {
        byte[] avif = HeifCoder.EncodeAvif(Noisy(192, 128, alpha: true), new AvifEncodeOptions { DenoiseNoiseLevel = 25 });
        var box = HeifContainer.Parse(avif);
        var dec = new Av1Decoder();
        using (dec.Decode(box.ItemData(box.PrimaryId)!, 0, true)) { }
        await Assert.That(dec.LastFilmGrain).IsNotNull();
        await Assert.That(dec.LastFilmGrain!.Value.NumYPoints).IsGreaterThan(0);
        await Assert.That((int)dec.LastFilmGrain!.Value.Seed).IsEqualTo(7391);   // libaom's seed for the first frame
        foreach (var kv in box.Items)
            if (kv.Key != box.PrimaryId && kv.Value.Type == "av01")
            {
                var ad = new Av1Decoder();
                using (ad.Decode(box.ItemData(kv.Key)!, 0, true)) { }
                await Assert.That(ad.LastFilmGrain).IsNull();
            }
        await Assert.That(HeifCoder.Decode(avif).Columns).IsEqualTo(192u);
    }

    [Test]
    public async Task Denoise_422_LeavesTheImageAlone()
    {
        // libaom's Wiener denoiser rejects unequal chroma subsampling: no grain, source planes coded unchanged.
        var img = Noisy(128, 96, alpha: false);
        var opts = new AvifEncodeOptions { ChromaSubsampling = AvifChromaSubsampling.Yuv422, BitDepth = 8 };
        byte[] plain = HeifCoder.EncodeAvif(img, opts);
        opts.DenoiseNoiseLevel = 25;
        byte[] dn = HeifCoder.EncodeAvif(img, opts);
        await Assert.That(dn).IsEquivalentTo(plain);
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
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { DenoiseNoiseLevel = 25, Lossless = true }))
            .Throws<ArgumentException>();
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { DenoiseNoiseLevel = 25, FilmGrain = AvifFilmGrain.TestVector(1) }))
            .Throws<ArgumentException>();
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { DenoiseNoiseLevel = 25, DenoiseBlockSize = 64 }))
            .Throws<ArgumentOutOfRangeException>();
        // ...but 4:4:4 may carry a single chroma plane's grain.
        byte[] ok = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { FilmGrain = oneChroma, ChromaSubsampling = AvifChromaSubsampling.Yuv444 });
        await Assert.That(HeifCoder.Decode(ok).Columns).IsEqualTo(64u);
    }
}
