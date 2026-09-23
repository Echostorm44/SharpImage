using System;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// AVIF lossless (AV1 base_q_idx 0: 4x4 Walsh-Hadamard, no in-loop filters), as avifenc --lossless: identity matrix
// at 4:4:4 (or YCgCo-Re) for colour, 4:0:0 for grey, lossless alpha. Decoded samples must equal the source exactly at
// the coded depth. (Cross-checked in the dev harness: dav1d planes == ours, libavif RGB == source, and file sizes
// within ~1% of libaom's own lossless mode.)
public sealed class AvifLosslessTests
{
    private static ImageFrame Noisy(int w, int h, bool alpha, bool gray, int bits, int seed)
    {
        var rng = new Random(seed);
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels, max = (1 << bits) - 1;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int baseV = (x * 7 + y * 3) % (max + 1);
                for (int c = 0; c < ch; c++)
                {
                    int v = gray && c > 0 && c < 3 ? -1 : Math.Clamp(baseV + rng.Next(-40, 41) * (max / 255 + 1) + c * 13, 0, max);
                    if (v >= 0) row[x * ch + c] = (ushort)Math.Round(v * 65535.0 / max);
                }
                if (gray) { row[x * ch + 1] = row[x * ch]; row[x * ch + 2] = row[x * ch]; }
            }
        }
        return f;
    }

    // Samples compared at the coded depth (the 16-bit quantum carries them scaled).
    private static long Mismatches(ImageFrame a, ImageFrame b, int bits)
    {
        long n = 0; int max = (1 << bits) - 1, ch = a.NumberOfChannels;
        for (int y = 0; y < a.Rows; y++)
        {
            var r0 = a.GetPixelRow(y); var r1 = b.GetPixelRow(y);
            for (int i = 0; i < a.Columns * ch; i++)
                if (Math.Round(r0[i] * (double)max / 65535) != Math.Round(r1[i] * (double)max / 65535)) n++;
        }
        return n;
    }

    [Test]
    [Arguments(257, 131, false, false, 8, null)]
    [Arguments(160, 96, true, false, 8, null)]
    [Arguments(130, 70, false, false, 10, null)]
    [Arguments(96, 64, false, false, 12, null)]
    [Arguments(99, 67, false, true, 8, null)]
    [Arguments(120, 80, false, false, 10, 16)]   // YCgCo-Re: 8-bit RGB carried exactly in 10-bit YCgCo
    public async Task Lossless_IsBitExact(int w, int h, bool alpha, bool gray, int bd, int? mc)
    {
        int srcBits = mc == 16 ? bd - 2 : bd;
        var src = Noisy(w, h, alpha, gray, srcBits, w * 31 + h);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Lossless = true, BitDepth = bd, MatrixCoefficients = mc });
        var dec = HeifCoder.Decode(avif);
        await Assert.That(dec.HasAlpha).IsEqualTo(alpha);
        await Assert.That(Mismatches(src, dec, srcBits)).IsEqualTo(0L);
    }

    [Test]
    public async Task Lossless_MultiTileFrame_IsBitExact()
    {
        var src = Noisy(4160, 24, false, false, 8, 5);   // wider than 4096 px: two tile columns
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Lossless = true }));
        await Assert.That(Mismatches(src, dec, 8)).IsEqualTo(0L);
    }

    [Test]
    public async Task LosslessGray_IsMonochrome()
    {
        // Grey content is coded as a single 4:0:0 plane (as libavif does), not three identical identity planes.
        var gray = Noisy(99, 67, false, true, 8, 3);
        byte[] g = HeifCoder.EncodeAvif(gray, new AvifEncodeOptions { Lossless = true });
        int av1C = -1;
        for (int i = 0; i + 8 < g.Length && av1C < 0; i++)
            if (g[i] == 'a' && g[i + 1] == 'v' && g[i + 2] == '1' && g[i + 3] == 'C') av1C = i;
        await Assert.That((g[av1C + 6] & 0x10) != 0).IsTrue();   // av1C monochrome bit
        await Assert.That(Mismatches(gray, HeifCoder.Decode(g), 8)).IsEqualTo(0L);
    }

    [Test]
    public async Task Lossless_DefaultsTo444()
    {
        // Auto subsampling must not pick 4:2:0 for lossless (YCgCo-Re here) — chroma subsampling is lossy.
        var src = Noisy(64, 40, false, false, 8, 7);
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Lossless = true, BitDepth = 10, MatrixCoefficients = 16 }));
        await Assert.That(Mismatches(src, dec, 8)).IsEqualTo(0L);
    }
}
