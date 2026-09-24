using System;
using System.IO;
using System.Linq;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifdec -u: every mode matched avifdec (libavif 1.4.2) pixel-for-pixel on 8/10/12-bit 4:2:0 / 4:2:2 / 4:4:4 files
// (avifall probe + layerc.py). Here: the modes' relationships on a 4:2:0 file.
public sealed class AvifChromaUpsamplingTests
{
    private static ushort[] Pixels(byte[] avif, AvifChromaUpsampling u)
    {
        using var f = HeifCoder.Decode(avif, new AvifDecodeOptions { ChromaUpsampling = u });
        return Enumerable.Range(0, (int)f.Rows).SelectMany(y => f.GetPixelRow(y).ToArray()).ToArray();
    }

    [Test]
    public async Task ModesBehaveLikeLibavif()
    {
        byte[] avif = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", "libavif_10bit_420_alpha.avif"));
        var auto = Pixels(avif, AvifChromaUpsampling.Automatic);
        var nearest = Pixels(avif, AvifChromaUpsampling.Nearest);
        await Assert.That(nearest.SequenceEqual(auto)).IsFalse();
        await Assert.That(Pixels(avif, AvifChromaUpsampling.Fastest).SequenceEqual(nearest)).IsTrue();
        await Assert.That(Pixels(avif, AvifChromaUpsampling.Bilinear).SequenceEqual(auto)).IsTrue();
        await Assert.That(Pixels(avif, AvifChromaUpsampling.BestQuality).SequenceEqual(auto)).IsTrue();
    }
}
