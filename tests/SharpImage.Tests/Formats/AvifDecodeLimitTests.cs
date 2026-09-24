using System;
using System.IO;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// Decode limits with libavif's semantics (avifdec --size-limit / --dimension-limit, imageCountLimit): a file over a
// limit fails with InvalidDataException before its images are decoded; the options validate like libavif's.
public sealed class AvifDecodeLimitTests
{
    private static byte[] Asset(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name));

    // Decoding must not touch the file system (a leftover dav1d-comparison hook once wrote cdf_ours_f<N>.txt per frame
    // into the working directory).
    [Test]
    public async Task Decode_WritesNoFiles()
    {
        string cwd = Environment.CurrentDirectory;
        foreach (var f in Directory.GetFiles(cwd, "cdf_ours_f*.txt")) File.Delete(f);
        using (var seq = HeifCoder.DecodeSequence(Asset("colors-animated-8bpc.avif"))) { }
        HeifCoder.Decode(Asset("libavif_10bit_420_alpha.avif"));
        await Assert.That(Directory.GetFiles(cwd, "cdf_ours_f*.txt").Length).IsEqualTo(0);
    }

    [Test]
    public async Task Strict_RequiresPixiLikeLibavif()
    {
        // avifdec (strict by default) rejects an AV1 item without 'pixi'; --no-strict (our default) decodes it.
        byte[] data = Asset("libavif_10bit_420_alpha.avif");
        int at = data.AsSpan().IndexOf("pixi"u8);
        await Assert.That(at).IsGreaterThan(0);
        var noPixi = (byte[])data.Clone();
        "zzzz"u8.CopyTo(noPixi.AsSpan(at));   // an unknown, non-essential property instead
        using (var lenient = HeifCoder.Decode(noPixi)) await Assert.That(lenient.Columns).IsGreaterThan(0u);
        await Assert.That(() => HeifCoder.Decode(noPixi, new AvifDecodeOptions { Strict = true })).Throws<InvalidDataException>();
        using (var ok = HeifCoder.Decode(data, new AvifDecodeOptions { Strict = true })) await Assert.That(ok.Columns).IsGreaterThan(0u);
    }

    [Test]
    public async Task ImageSizeLimit_RejectsLargerImages()
    {
        byte[] data = Asset("libavif_10bit_420_alpha.avif");
        var full = HeifCoder.Decode(data);
        long pixels = full.Columns * full.Rows;
        await Assert.That(HeifCoder.Decode(data, new AvifDecodeOptions { ImageSizeLimit = pixels }).Columns).IsEqualTo(full.Columns);
        await Assert.That(() => HeifCoder.Decode(data, new AvifDecodeOptions { ImageSizeLimit = pixels - 1 })).Throws<InvalidDataException>();
        await Assert.That(() => HeifCoder.Decode(data, new AvifDecodeOptions { ImageDimensionLimit = (int)Math.Max(full.Columns, full.Rows) - 1 }))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task ImageCountLimit_RejectsLongerSequences()
    {
        byte[] data = Asset("colors-animated-8bpc.avif");   // 5 frames
        using (var all = HeifCoder.DecodeSequence(data, new AvifDecodeOptions { ImageCountLimit = 5 }))
            await Assert.That(all.Count).IsEqualTo(5);
        await Assert.That(() => HeifCoder.DecodeSequence(data, new AvifDecodeOptions { ImageCountLimit = 4 })).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Options_ValidateLikeLibavif()
    {
        // libavif: an imageSizeLimit of 0 or above AVIF_DEFAULT_IMAGE_SIZE_LIMIT is not supported.
        await Assert.That(() => new AvifDecodeOptions { ImageSizeLimit = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new AvifDecodeOptions { ImageSizeLimit = AvifDecodeOptions.DefaultImageSizeLimit + 1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new AvifDecodeOptions { ImageDimensionLimit = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new AvifDecodeOptions { ImageDimensionLimit = 0, ImageCountLimit = 0 }.ImageDimensionLimit).IsEqualTo(0);
    }
}
