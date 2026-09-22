using System;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Formats;

// AVIF colour description (CICP, ITU-T H.273): written to both the 'colr' nclx box and the AV1 sequence header,
// honoured on decode (colr first, then the sequence header — libavif's precedence) for every matrix libavif can
// convert, limited/full range, and exposed/round-tripped via ImageFrame.Metadata.Cicp. Pixel exactness against
// libavif is covered by Av1ConformanceDecodeTests; here: signalling, validation and round-trip fidelity.
public sealed class AvifCicpTests
{
    private static ImageFrame Gradient(int w, int h, bool alpha = false, bool gray = false)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                double t = (x + 0.37 * y) / (w + 0.37 * h);
                double r = t, g = 0.5 + 0.45 * Math.Sin(6.0 * t + y * 0.02), b = 1.0 - 0.8 * t;
                if (gray) g = b = r;
                row[x * ch] = (ushort)Math.Round(r * 65535);
                row[x * ch + 1] = (ushort)Math.Round(g * 65535);
                row[x * ch + 2] = (ushort)Math.Round(b * 65535);
                if (alpha) row[x * ch + 3] = (ushort)Math.Round((0.2 + 0.8 * y / (h - 1.0)) * 65535);
            }
        }
        return f;
    }

    private static double Psnr(ImageFrame a, ImageFrame b)
    {
        double sse = 0; long n = 0;
        for (long y = 0; y < a.Rows; y++)
            for (long x = 0; x < a.Columns; x++)
                for (int c = 0; c < 3; c++) { double d = a.GetPixelChannel(x, y, c) - b.GetPixelChannel(x, y, c); sse += d * d; n++; }
        return sse == 0 ? 99 : 10 * Math.Log10(65535.0 * 65535.0 / (sse / n));
    }

    // (cp, tc, mc, full) of the 'colr' nclx box.
    private static (int, int, int, bool) Nclx(byte[] avif)
    {
        for (int i = 0; i + 15 < avif.Length; i++)
            if (avif[i] == 'c' && avif[i + 1] == 'o' && avif[i + 2] == 'l' && avif[i + 3] == 'r' && avif[i + 4] == 'n' && avif[i + 5] == 'c')
                return ((avif[i + 8] << 8) | avif[i + 9], (avif[i + 10] << 8) | avif[i + 11], (avif[i + 12] << 8) | avif[i + 13], (avif[i + 14] & 0x80) != 0);
        throw new InvalidOperationException("no colr nclx");
    }

    [Test]
    [Arguments(8, 1, 13, 6, true)]      // sRGB (the default)
    [Arguments(8, 1, 1, 1, false)]      // BT.709 studio range
    [Arguments(10, 9, 16, 9, true)]     // BT.2100 PQ
    [Arguments(12, 9, 18, 9, false)]    // BT.2100 HLG, studio
    [Arguments(8, 4, 4, 4, true)]       // FCC
    [Arguments(10, 7, 7, 7, false)]     // SMPTE 240M
    [Arguments(8, 12, 13, 12, true)]    // chromaticity-derived NCL on P3 primaries
    [Arguments(8, 1, 13, 0, true)]      // identity (GBR, 4:4:4)
    [Arguments(10, 1, 13, 0, true)]
    [Arguments(8, 2, 2, 8, true)]       // YCgCo
    [Arguments(10, 2, 2, 16, true)]     // YCgCo-Re (8-bit RGB in 10-bit)
    [Arguments(10, 2, 2, 17, true)]     // YCgCo-Ro (9-bit RGB in 10-bit)
    public async Task Cicp_IsSignalledDecodedAndRoundTrips(int bd, int cp, int tc, int mc, bool full)
    {
        var src = Gradient(131, 70);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        {
            Qp = 4, BitDepth = bd, ColorPrimaries = cp, TransferCharacteristics = tc, MatrixCoefficients = mc, FullRange = full,
        });
        await Assert.That(Nclx(avif)).IsEqualTo((cp, tc, mc, full));

        var dec = HeifCoder.Decode(avif);
        await Assert.That(dec.Metadata.Cicp).IsEqualTo(new CicpInfo(cp, tc, mc, full));
        double psnr = Psnr(src, dec);
        Console.WriteLine($"[CICP] {cp}/{tc}/{mc}/{(full ? "full" : "lim")} bd={bd} psnr={psnr:F2}");
        // YCgCo-Re quantises RGB to bd-2 bits first; 8-bit studio BT.709 decodes (like libavif) through libyuv, whose
        // default build clamps the blue coefficient UB to 128 (BT.709 needs 135) — a reference-exact loss.
        await Assert.That(psnr).IsGreaterThan(mc == 16 ? 44 : bd == 8 && !full ? 40 : 45);
    }

    [Test]
    public async Task Default_IsSrgb_AndIccKeepsPrimariesUnspecified()
    {
        var src = Gradient(64, 40);
        await Assert.That(Nclx(HeifCoder.EncodeAvif(src))).IsEqualTo((1, 13, 6, true));
        src.IccProfile = new byte[] { 1, 2, 3 };   // any attached ICC: avifenc leaves CP/TC unspecified (the ICC rules)
        await Assert.That(Nclx(HeifCoder.EncodeAvif(src))).IsEqualTo((2, 2, 6, true));
    }

    [Test]
    public async Task DecodedCicp_IsPreservedOnReencode()
    {
        var src = Gradient(96, 64);
        var hdr = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        { BitDepth = 10, ColorPrimaries = 9, TransferCharacteristics = 16, MatrixCoefficients = 9 }));
        byte[] again = HeifCoder.EncodeAvif(hdr, new AvifEncodeOptions { BitDepth = 10 });
        await Assert.That(Nclx(again)).IsEqualTo((9, 16, 9, true));
    }

    [Test]
    public async Task Validation_MatchesLibavifLimits()
    {
        var src = Gradient(64, 40);
        await Assert.That(() => HeifCoder.EncodeAvif(src, new AvifEncodeOptions { MatrixCoefficients = 0, ChromaSubsampling = AvifChromaSubsampling.Yuv420 }))
            .Throws<ArgumentException>();
        await Assert.That(() => HeifCoder.EncodeAvif(src, new AvifEncodeOptions { MatrixCoefficients = 8, FullRange = false }))
            .Throws<NotSupportedException>();
        await Assert.That(() => HeifCoder.EncodeAvif(src, new AvifEncodeOptions { MatrixCoefficients = 10 }))
            .Throws<NotSupportedException>();
        await Assert.That(() => HeifCoder.EncodeAvif(src, new AvifEncodeOptions { MatrixCoefficients = 16, BitDepth = 8 }))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WithoutColr_SequenceHeaderCicpIsUsed()
    {
        var src = Gradient(96, 64);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { ColorPrimaries = 1, TransferCharacteristics = 1, MatrixCoefficients = 1, FullRange = false });
        var withColr = HeifCoder.Decode(avif);
        // Hide the colr property (rename its type) — the AV1 sequence header carries the same description.
        for (int i = 0; i + 8 < avif.Length; i++)
            if (avif[i] == 'c' && avif[i + 1] == 'o' && avif[i + 2] == 'l' && avif[i + 3] == 'r' && avif[i + 4] == 'n')
            { avif[i] = (byte)'x'; break; }
        var noColr = HeifCoder.Decode(avif);
        await Assert.That(noColr.Metadata.Cicp).IsEqualTo(new CicpInfo(1, 1, 1, false));
        await Assert.That(Psnr(withColr, noColr)).IsEqualTo(99.0);
    }

    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task LimitedRangeAlpha_IsExpandedToFull(int bd)
    {
        // AVIF 1.0.0 allowed studio-range alpha; libavif expands it (LIMITED_TO_FULL). Ramp 16..235 (<< bd-8) → 0..max.
        int w = 160, h = 32, max = (1 << bd) - 1;
        var yv = new ushort[w * h]; var uv = new ushort[(w / 2) * (h / 2)]; var vv = new ushort[uv.Length]; var av = new ushort[w * h];
        for (int i = 0; i < yv.Length; i++) { yv[i] = (ushort)(max / 2); av[i] = (ushort)((16 << (bd - 8)) + (i % w) * (219 << (bd - 8)) / (w - 1)); }
        Array.Fill(uv, (ushort)(1 << (bd - 1))); Array.Fill(vv, (ushort)(1 << (bd - 1)));
        (byte[] cs, byte[] cf) = Av1StillImageEncoder.BuildColorObus(yv, uv, vv, w, h, 40, bd);
        (byte[] asq, byte[] af) = Av1StillImageEncoder.BuildMonochromeObus(av, w, h, 4, bd, new Av1ObuWriter.Av1ColorDesc(2, 2, 2, false));
        var img = HeifCoder.Decode(Av1AvifWriter.BuildAvifWithAlpha(cs, cf, asq, af, w, h, colorMonochrome: false, bd));
        int ach = img.NumberOfChannels - 1;
        await Assert.That((int)img.GetPixelChannel(0, 5, ach)).IsLessThanOrEqualTo(300);
        await Assert.That((int)img.GetPixelChannel(w - 1, 5, ach)).IsGreaterThanOrEqualTo(65235);
    }

    [Test]
    public async Task LimitedRangeGray_RoundTrips()
    {
        var src = Gradient(80, 48, gray: true);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { FullRange = false, Qp = 2 });
        var dec = HeifCoder.Decode(avif);
        await Assert.That(dec.Metadata.Cicp!.FullRange).IsFalse();
        await Assert.That(Psnr(src, dec)).IsGreaterThan(45);
    }
}
