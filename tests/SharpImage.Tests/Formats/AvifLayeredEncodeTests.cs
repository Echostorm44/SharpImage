using System;
using System.Collections.Generic;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc --layered: AvifEncodeOptions.Layers codes 2..4 spatial layers (libaom scaling modes, per-layer quality, own
// images) in one item with an 'a1lx' index. The dev harness checked every layer against avifdec --progressive --index
// (identical) and the streams against dav1d --alllayers for 2/3/4 layers, scales 1/2 1/4 1/8 3/4 3/5 4/5, alpha,
// 10-bit and odd sizes.
public sealed class AvifLayeredEncodeTests
{
    private static ImageFrame Pattern(int w, int h, bool alpha, int shift = 0)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = (ushort)((x * 3 + shift) % 256 * 257);
                row[x * ch + 1] = (ushort)((y * 5) % 256 * 257);
                row[x * ch + 2] = (ushort)((x + y) % 256 * 257);
                if (alpha) row[x * ch + 3] = (ushort)((x * y + 40) % 256 * 257);
            }
        }
        return f;
    }

    [Test]
    public async Task ThreeLayers_DecodeProgressively()
    {
        var img = Pattern(300, 200, alpha: true);
        byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions
        {
            Layers =
            [
                new AvifLayer { ScaleNumerator = 1, ScaleDenominator = 4, Quality = 10 },
                new AvifLayer { ScaleNumerator = 1, ScaleDenominator = 2, Quality = 40 },
                new AvifLayer { Quality = 80 },
            ],
        });
        var c = HeifContainer.Parse(avif);
        await Assert.That(c.Property(c.PrimaryId, "a1lx")).IsNotNull();
        var layers = HeifCoder.DecodeProgressive(avif);
        await Assert.That(layers.Count).IsEqualTo(3);
        foreach (var l in layers)
        {
            await Assert.That(((int)l.Columns, (int)l.Rows)).IsEqualTo((300, 200));
            await Assert.That(l.HasAlpha).IsTrue();
        }
        // The plain decode is the last (full) layer.
        var full = HeifCoder.Decode(avif);
        long diff = 0;
        for (int y = 0; y < 200; y++)
        {
            var a = full.GetPixelRow(y);
            var b = layers[2].GetPixelRow(y);
            for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        }
        await Assert.That(diff).IsEqualTo(0L);
    }

    [Test]
    public async Task LayersFromDifferentImages()
    {
        var img = Pattern(160, 120, alpha: false);
        byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions
        {
            Layers = [new AvifLayer { Image = Pattern(160, 120, false, shift: 90), ScaleNumerator = 3, ScaleDenominator = 5, Quality = 30 }, new AvifLayer { Quality = 70 }],
        });
        await Assert.That(HeifCoder.DecodeProgressive(avif).Count).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidLayers_AreRejected()
    {
        var img = Pattern(64, 64, false);
        AvifEncodeOptions With(List<AvifLayer> l) => new() { Layers = l };
        await Assert.That(() => HeifCoder.EncodeAvif(img, With([new AvifLayer { ScaleNumerator = 2, ScaleDenominator = 3 }, new AvifLayer()])))
            .Throws<ArgumentException>();   // 2/3 is not a libaom scaling mode
        await Assert.That(() => HeifCoder.EncodeAvif(img, With([new AvifLayer(), new AvifLayer { ScaleNumerator = 1, ScaleDenominator = 2 }])))
            .Throws<ArgumentException>();   // the last layer must be full size
        await Assert.That(() => HeifCoder.EncodeAvif(img, With([new AvifLayer(), new AvifLayer(), new AvifLayer(), new AvifLayer(), new AvifLayer()])))
            .Throws<ArgumentException>();   // at most 4 layers
        await Assert.That(() => HeifCoder.EncodeAvif(img, With([new AvifLayer { Image = Pattern(32, 32, false) }, new AvifLayer()])))
            .Throws<ArgumentException>();   // layer images must match the image size
    }
}
