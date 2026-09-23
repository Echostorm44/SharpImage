using System;
using System.IO;
using System.Linq;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Layered (progressive) grids: libavif's avifEncoderAddImageGrid with extraLayerCount, where every grid cell is a
// layered AV1 item with its own 'a1lx' and one iloc extent per layer. Decoding libavif's file must equal avifdec
// --progressive per layer (SHA-256 of RGBA as 16-bit LE, 8-bit x 257, alpha 65535); our encodes of Progressive / Layers
// grids must decode to every layer at full size. The dev harness also matched dav1d per cell item and avifdec per layer
// on our files (colour, alpha, custom layers).
public sealed class AvifLayeredGridTests
{
    private static ImageFrame Gradient(int w, int h, bool alpha)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int n = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * n] = Quantum.ScaleFromByte((byte)(x * 3 + y));
                row[x * n + 1] = Quantum.ScaleFromByte((byte)(x + y * 2));
                row[x * n + 2] = Quantum.ScaleFromByte((byte)((x * y) >> 4));
                if (alpha) row[x * n + 3] = Quantum.ScaleFromByte((byte)(x + y));
            }
        }
        return f;
    }

    [Test]
    public async Task LibavifLayeredGrid_DecodesEveryLayer()
    {
        var layers = HeifCoder.DecodeProgressive(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", "libavif_layered_grid_2x2.avif")));
        await Assert.That(layers.Count).IsEqualTo(2);
        await Assert.That(AvifRgbToYuvTests.Hash16Rgba(layers[0], true)).IsEqualTo("49c155dcbcee98ca");
        await Assert.That(AvifRgbToYuvTests.Hash16Rgba(layers[1], true)).IsEqualTo("78a2d0d1dde28a56");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProgressiveGrid_LayeredCells(bool alpha)
    {
        var src = Gradient(256, 192, alpha);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Grid = (2, 2), Progressive = true, Quality = 60 });
        // Every colour (and alpha) cell carries its own a1lx.
        int a1lx = 0;
        for (int i = 0; i + 4 <= avif.Length; i++) if (avif[i] == 'a' && avif[i + 1] == '1' && avif[i + 2] == 'l' && avif[i + 3] == 'x') a1lx++;
        await Assert.That(a1lx).IsGreaterThanOrEqualTo(alpha ? 5 : 4);
        var layers = HeifCoder.DecodeProgressive(avif);
        await Assert.That(layers.Count).IsEqualTo(2);
        foreach (var l in layers)
        {
            await Assert.That((int)l.Columns).IsEqualTo(256);
            await Assert.That((int)l.Rows).IsEqualTo(192);
            await Assert.That(l.HasAlpha).IsEqualTo(alpha);
        }
        await Assert.That(Error(layers[1], src)).IsLessThan(Error(layers[0], src));
    }

    [Test]
    public async Task LayersGrid_CustomLayers()
    {
        var src = Gradient(256, 192, false);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        {
            Grid = (2, 1),
            Layers = [new AvifLayer { ScaleNumerator = 1, ScaleDenominator = 2, Quality = 20 }, new AvifLayer { Quality = 70 }],
        });
        var layers = HeifCoder.DecodeProgressive(avif);
        await Assert.That(layers.Count).IsEqualTo(2);
        await Assert.That(Error(layers[1], src)).IsLessThan(Error(layers[0], src));
    }

    private static double Error(ImageFrame a, ImageFrame b)
    {
        double se = 0;
        for (int y = 0; y < (int)a.Rows; y++)
        {
            var ra = a.GetPixelRow(y);
            var rb = b.GetPixelRow(y);
            for (int x = 0; x < (int)a.Columns; x++)
                for (int k = 0; k < 3; k++)
                {
                    double d = ra[x * a.NumberOfChannels + k] - rb[x * b.NumberOfChannels + k];
                    se += d * d;
                }
        }
        return se;
    }
}
