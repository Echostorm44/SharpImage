using System;
using System.Reflection;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc --tilecolslog2 / --tilerowslog2 / --autotiling: the tile layout written equals what libaom (avifenc 1.4.2)
// writes for the same image and options — requests clamped like av1_set_tile_info, autotiling like
// avifSetTileConfiguration. Expected counts were read back from libaom-made files; the dev harness (Av1HbdVerify
// EncodeProbe + TileProbe) also confirmed our tiled files decode identically in avifdec and dav1d.
public sealed class AvifTilingTests
{
    private static ImageFrame Noise(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        var rng = new Random(5);
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w * 3; x++) row[x] = (ushort)(Math.Clamp((x / 3 + y) % 256 + rng.Next(-10, 11), 0, 255) * 257);
        }
        return f;
    }

    private static (int Cols, int Rows) Tiles(byte[] avif)
    {
        var c = HeifContainer.Parse(avif);
        int id = c.PrimaryId;
        if (c.Items[id].Type == "grid") id = c.ReferencesFrom(id, "dimg")[0];
        var dec = new Av1Decoder();
        using var frame = dec.Decode(c.ItemData(id)!, 0, isKeyframe: true);
        var ctx = typeof(Av1Decoder).GetField("ctx", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dec)!;
        return ((int)ctx.GetType().GetField("TileCols")!.GetValue(ctx)!, (int)ctx.GetType().GetField("TileRows")!.GetValue(ctx)!);
    }

    [Test]
    [Arguments(1000, 700, 1, 0, false, 2, 1)]
    [Arguments(1000, 700, 2, 1, false, 4, 2)]
    [Arguments(1000, 700, 6, 6, false, 16, 11)]
    [Arguments(300, 200, 0, 3, false, 1, 4)]
    [Arguments(1920, 1080, 0, 0, true, 4, 2)]
    [Arguments(700, 1500, 0, 0, true, 1, 4)]
    [Arguments(300, 200, 0, 0, true, 1, 1)]
    public async Task TileLayout_MatchesLibaom(int w, int h, int colsLog2, int rowsLog2, bool auto, int cols, int rows)
    {
        var img = Noise(w, h);
        byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Qp = 30, TileColumnsLog2 = colsLog2, TileRowsLog2 = rowsLog2, AutoTiling = auto });
        await Assert.That(Tiles(avif)).IsEqualTo((cols, rows));
        var d = HeifCoder.Decode(avif);
        await Assert.That(((int)d.Columns, (int)d.Rows)).IsEqualTo((w, h));
    }

    [Test]
    public async Task Lossless_Tiled_IsExact()
    {
        var img = Noise(400, 300);
        var d = HeifCoder.Decode(HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Lossless = true, TileColumnsLog2 = 2, TileRowsLog2 = 1 }));
        long diff = 0;
        for (int y = 0; y < 300; y++)
        {
            var a = img.GetPixelRow(y);
            var b = d.GetPixelRow(y);
            for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        }
        await Assert.That(diff).IsEqualTo(0L);
    }

    [Test]
    public async Task Sequence_TilingAndMonochrome()
    {
        var seq = new ImageSequence { Timescale = 100 };
        for (int k = 0; k < 3; k++)
        {
            var f = Noise(600, 400);
            f.DurationTicks = 10;
            seq.AddFrame(f);
        }
        byte[] avif = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions
        {
            Qp = 30, TileColumnsLog2 = 1, ChromaSubsampling = AvifChromaSubsampling.Yuv400,
        });
        using var back = HeifCoder.DecodeSequence(avif);
        await Assert.That(back.Count).IsEqualTo(3);
        var row = back[1].GetPixelRow(123);
        bool grey = true;
        for (int x = 0; x < 600; x++) grey &= row[x * 3] == row[x * 3 + 1] && row[x * 3 + 1] == row[x * 3 + 2];
        await Assert.That(grey).IsTrue();
    }
}
