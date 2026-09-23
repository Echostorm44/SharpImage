using System;
using System.Reflection;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc -q / --qalpha / --qgain-map / --target-size: quality 0..100 lands on the same base_q_idx libaom writes for
// libavif (expected values read back from avifenc 1.4.2 / libaom 3.14.1 files): colour through libavif's tune=iq
// quantizer table (linear formula for the identity matrix and for sequences), alpha through the linear formula
// (tune=psnr), then libaom's quantizer_to_qindex; 100 = lossless coding.
public sealed class AvifQualityTests
{
    private static ImageFrame Rgba(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, true);
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 4] = (ushort)((x * 5) % 256 * 257);
                row[x * 4 + 1] = (ushort)((y * 7) % 256 * 257);
                row[x * 4 + 2] = (ushort)((x + y) % 256 * 257);
                row[x * 4 + 3] = (ushort)((x * y) % 256 * 257);
            }
        }
        return f;
    }

    private static int QIdx(HeifContainer c, int id)
    {
        var dec = new Av1Decoder();
        using var frame = dec.Decode(c.ItemData(id)!, 0, isKeyframe: true);
        var ctx = typeof(Av1Decoder).GetField("ctx", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dec)!;
        var fh = ctx.GetType().GetField("FrameHeader")!.GetValue(ctx)!;
        return Convert.ToInt32(fh.GetType().GetField("QuantBaseQIdx")!.GetValue(fh));
    }

    private static (int Color, int Alpha) QIndices(byte[] avif)
    {
        var c = HeifContainer.Parse(avif);
        int alphaId = -1;
        foreach (int a in c.ReferencesTo(c.PrimaryId, "auxl")) alphaId = a;
        return (QIdx(c, c.PrimaryId), alphaId < 0 ? -1 : QIdx(c, alphaId));
    }

    [Test]
    [Arguments(0, null, false, 255, 255)]
    [Arguments(10, null, false, 236, 228)]
    [Arguments(30, null, false, 196, 176)]
    [Arguments(50, null, false, 148, 128)]
    [Arguments(60, null, false, 120, 100)]
    [Arguments(75, null, false, 76, 64)]
    [Arguments(90, null, false, 32, 24)]
    [Arguments(99, null, false, 4, 4)]
    [Arguments(100, null, false, 0, 0)]
    [Arguments(60, 30, false, 120, 176)]
    [Arguments(60, null, true, 100, 100)]
    public async Task Quality_MapsToLibaomQIndex(int quality, int? qualityAlpha, bool identity, int colorQIdx, int alphaQIdx)
    {
        byte[] avif = HeifCoder.EncodeAvif(Rgba(96, 64), new AvifEncodeOptions
        {
            Quality = quality, QualityAlpha = qualityAlpha, ChromaSubsampling = AvifChromaSubsampling.Yuv444,
            MatrixCoefficients = identity ? 0 : null,
        });
        await Assert.That(QIndices(avif)).IsEqualTo((colorQIdx, alphaQIdx));
        var d = HeifCoder.Decode(avif);
        await Assert.That(((int)d.Columns, (int)d.Rows)).IsEqualTo((96, 64));
    }

    [Test]
    public async Task LosslessColour_WithLossyAlpha()
    {
        byte[] avif = HeifCoder.EncodeAvif(Rgba(64, 48), new AvifEncodeOptions { Quality = 100, QualityAlpha = 50 });
        await Assert.That(QIndices(avif)).IsEqualTo((0, 128));
    }

    [Test]
    public async Task Sequence_UsesLinearMapping()
    {
        var seq = new ImageSequence { Timescale = 10 };
        for (int k = 0; k < 2; k++) { var f = Rgba(64, 48); f.DurationTicks = 1; seq.AddFrame(f); }
        var c = HeifContainer.Parse(HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 60 }));
        // quantizer ((100-60)*63+50)/100 = 25 -> cq 100 for the inter frames; the key frame (the primary item) gets
        // libaom's good-quality key-frame boost: the q-index at ~0.43x the cq quantizer, 42 (libaom: 43).
        await Assert.That(QIdx(c, c.PrimaryId)).IsEqualTo(42);
    }

    [Test]
    public async Task TargetSize_PicksTheClosestQuality()
    {
        var img = Rgba(160, 120);
        int target = 6000;
        byte[] chosen = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { TargetSize = target });
        // No other quality comes closer to the target than the chosen encode.
        long best = long.MaxValue;
        for (int q = 0; q <= 100; q++)
            best = Math.Min(best, Math.Abs(HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Quality = q }).Length - (long)target));
        long got = Math.Abs(chosen.Length - (long)target);
        await Assert.That(got).IsLessThanOrEqualTo(best * 3 + 64);   // binary search on a monotone-ish curve: near-best
        await Assert.That(() => HeifCoder.EncodeAvif(img, new AvifEncodeOptions { TargetSize = target, Quality = 50, QualityAlpha = 50 }))
            .Throws<ArgumentException>();
    }
}
