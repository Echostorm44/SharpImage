using System;
using System.Collections.Generic;
using System.Linq;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Image sequences with inter frames (as libavif / libaom code them): the first frame (and every KeyframeInterval-th,
// and scene cuts) is a key frame, the others INTER_FRAMEs predicted from the previous frame. The dev harness verified
// every colour and alpha track of a 12-configuration matrix (4:2:0/4:2:2/4:4:4, alpha, 8/10/12-bit, odd and tiny
// sizes, tiles, keyframe intervals) decodes identically in dav1d and our decoder, and every frame in avifdec (libaom).
public sealed class AvifSequenceInterTests
{
    // A textured gradient panning 2 px right and 1 px down per frame (inter-friendly), optional alpha ramp.
    private static ImageSequence Panning(int w, int h, int frames, bool alpha, int cutAt = -1)
    {
        var seq = new ImageSequence { Timescale = 10 };
        for (int f = 0; f < frames; f++)
        {
            var img = new ImageFrame();
            img.Initialize(w, h, ColorspaceType.SRGB, alpha);
            int n = img.NumberOfChannels;
            bool second = cutAt >= 0 && f >= cutAt;
            for (int y = 0; y < h; y++)
            {
                var row = img.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    int sx = x - 2 * f, sy = y - f;
                    int r = second ? (x * 7) ^ (y * 13) : (sx * 3 + sy) & 255;
                    int g = second ? 255 - ((x + y) & 255) : ((sx ^ sy) * 5) & 255;
                    int b = second ? (x * y) & 255 : ((sx * sx + sy * 3) >> 3) & 255;
                    row[x * n] = (ushort)(r * 257);
                    row[x * n + 1] = (ushort)(g * 257);
                    row[x * n + 2] = (ushort)(b * 257);
                    if (alpha) row[x * n + 3] = (ushort)(((x + y) * 2 & 255) * 257);
                }
            }
            img.DurationTicks = 1;
            seq.AddFrame(img);
        }
        return seq;
    }

    // The sync sample numbers of each track ('stss' entries; null when the box is absent = all sync).
    private static List<List<int>?> SyncSamples(byte[] avif)
    {
        var result = new List<List<int>?>();
        int pos = 0;
        while ((pos = IndexOf(avif, "trak"u8, pos)) >= 0)
        {
            int end = IndexOf(avif, "trak"u8, pos + 4);
            if (end < 0) end = avif.Length;
            int s = IndexOf(avif, "stss"u8, pos);
            if (s >= 0 && s < end)
            {
                int count = (avif[s + 8] << 24) | (avif[s + 9] << 16) | (avif[s + 10] << 8) | avif[s + 11];
                var list = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    int o = s + 12 + i * 4;
                    list.Add((avif[o] << 24) | (avif[o + 1] << 16) | (avif[o + 2] << 8) | avif[o + 3]);
                }
                result.Add(list);
            }
            else result.Add(null);
            pos = end;
        }
        return result;
    }

    private static int IndexOf(byte[] a, ReadOnlySpan<byte> pat, int from)
    {
        int i = a.AsSpan(from).IndexOf(pat);
        return i < 0 ? -1 : from + i;
    }

    private static double Psnr(ImageFrame a, ImageFrame b)
    {
        double se = 0;
        long n = 0;
        for (int y = 0; y < (int)a.Rows; y++)
        {
            var ra = a.GetPixelRow(y);
            var rb = b.GetPixelRow(y);
            for (int x = 0; x < (int)a.Columns; x++)
                for (int k = 0; k < 3; k++)
                {
                    double d = (ra[x * a.NumberOfChannels + k] - rb[x * b.NumberOfChannels + k]) / 257.0;
                    se += d * d;
                    n++;
                }
        }
        return 10 * Math.Log10(255.0 * 255.0 * n / Math.Max(se, 1e-9));
    }

    [Test]
    public async Task InterFrames_SmallerThanAllIntra_SameQuality()
    {
        using var seq = Panning(96, 64, 5, false);
        byte[] inter = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 70 });
        byte[] intra = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 70, KeyframeInterval = 1 });
        await Assert.That(inter.Length).IsLessThan(intra.Length * 2 / 3);
        var sync = SyncSamples(inter);
        await Assert.That(sync[0]!).IsEquivalentTo(new List<int> { 1 });
        await Assert.That(SyncSamples(intra)[0]).IsNull();
        using var dec = HeifCoder.DecodeSequence(inter);
        using var decIntra = HeifCoder.DecodeSequence(intra);
        await Assert.That(dec.Count).IsEqualTo(5);
        // Inter frames reach (at least nearly) the all-intra quality at a fraction of the size.
        for (int i = 0; i < 5; i++) await Assert.That(Psnr(dec[i], seq[i])).IsGreaterThan(Psnr(decIntra[i], seq[i]) - 1.0);
        // 'avio' (a track made only of sync samples) only for the all-intra file, as libavif writes it.
        await Assert.That(System.Text.Encoding.ASCII.GetString(inter, 0, 64).Contains("avio")).IsFalse();
        await Assert.That(System.Text.Encoding.ASCII.GetString(intra, 0, 64).Contains("avio")).IsTrue();
    }

    [Test]
    public async Task KeyframeInterval_AndSceneCut()
    {
        using var seq = Panning(64, 48, 7, false);
        var sync = SyncSamples(HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 60, KeyframeInterval = 3 }));
        await Assert.That(sync[0]!).IsEquivalentTo(new List<int> { 1, 4, 7 });

        // libaom (as avifenc 1.4.2) keeps this short cut inside the group: one key frame, the rest inter frames
        using var cut = Panning(64, 48, 5, false, cutAt: 3);
        var cutSync = SyncSamples(HeifCoder.EncodeAvifSequence(cut, new AvifEncodeOptions { Quality = 60 }));
        await Assert.That(cutSync[0]!).IsEquivalentTo(new List<int> { 1 });
    }

    // Lossless sequences code inter frames too (coded-lossless: 4x4 Walsh-Hadamard residuals on the motion-compensated
    // prediction), reproducing exactly what all-intra lossless does at a fraction of the size. The dev harness verified
    // dav1d == ours (colour + alpha) and that the result is ~9% smaller than avifenc --lossless on a photo pan.
    [Test]
    [Arguments(8, AvifChromaSubsampling.Yuv444, false)]
    [Arguments(10, AvifChromaSubsampling.Yuv420, true)]
    public async Task Lossless_InterFrames_Exact(int depth, AvifChromaSubsampling cs, bool alpha)
    {
        using var seq = Panning(72, 40, 5, alpha);
        int? matrix = cs == AvifChromaSubsampling.Yuv444 ? null : 1;
        byte[] inter = HeifCoder.EncodeAvifSequence(seq,
            new AvifEncodeOptions { Lossless = true, BitDepth = depth, ChromaSubsampling = cs, MatrixCoefficients = matrix });
        byte[] intra = HeifCoder.EncodeAvifSequence(seq,
            new AvifEncodeOptions { Lossless = true, BitDepth = depth, ChromaSubsampling = cs, MatrixCoefficients = matrix, KeyframeInterval = 1 });
        await Assert.That(inter.Length).IsLessThan(intra.Length * 2 / 3);
        await Assert.That(SyncSamples(inter)[0]!).IsEquivalentTo(new List<int> { 1 });
        using var a = HeifCoder.DecodeSequence(inter);
        using var b = HeifCoder.DecodeSequence(intra);
        for (int i = 0; i < 5; i++)
            for (int y = 0; y < 40; y++)
                await Assert.That(a[i].GetPixelRow(y).SequenceEqual(b[i].GetPixelRow(y))).IsTrue();
    }

    [Test]
    [Arguments(8, AvifChromaSubsampling.Yuv420)]
    [Arguments(10, AvifChromaSubsampling.Yuv444)]
    [Arguments(12, AvifChromaSubsampling.Yuv422)]
    public async Task AlphaAndDepths_DecodeClose(int depth, AvifChromaSubsampling cs)
    {
        using var seq = Panning(72, 40, 4, true);
        byte[] avif = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 80, BitDepth = depth, ChromaSubsampling = cs });
        byte[] intraFile = HeifCoder.EncodeAvifSequence(seq,
            new AvifEncodeOptions { Quality = 80, BitDepth = depth, ChromaSubsampling = cs, KeyframeInterval = 1 });
        await Assert.That(avif.Length).IsLessThan(intraFile.Length);
        using var intra = HeifCoder.DecodeSequence(intraFile);
        var sync = SyncSamples(avif);
        await Assert.That(sync.Count).IsEqualTo(2);
        await Assert.That(sync[1]!).IsEquivalentTo(sync[0]!);   // the alpha track keeps the colour key frames
        using var dec = HeifCoder.DecodeSequence(avif);
        for (int i = 0; i < 4; i++)
        {
            // All-intra frames are each coded at the boosted key-frame q-index (0.43x the cq quantizer, as libaom's
            // key frames: up to ~7 dB finer), inter frames at the cq level itself — so they trail the all-intra
            // frames by part of that q difference, at a fraction of the size.
            await Assert.That(Psnr(dec[i], seq[i])).IsGreaterThan(Psnr(intra[i], seq[i]) - 5.0);
            double aErr = 0;
            for (int y = 0; y < 40; y++)
            {
                var ra = dec[i].GetPixelRow(y);
                var rb = seq[i].GetPixelRow(y);
                for (int x = 0; x < 72; x++) aErr += Math.Abs(ra[x * 4 + 3] - rb[x * 4 + 3]) / 257.0;
            }
            await Assert.That(aErr / (72 * 40)).IsLessThan(3);
        }
    }
}
