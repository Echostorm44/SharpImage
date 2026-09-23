using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc --grid: AvifEncodeOptions.Grid splits the image into cells sized like avifenc (ceil, >= 64 px, even along
// subsampled axes) and writes a 'grid' item over hidden cells (+ an alpha grid), as libavif lays it out. The dev harness
// (Av1HbdVerify.EncodeProbe) checked every configuration below against avifdec (pixel-identical to our decode).
// Also: premultiplied alpha at high bit depth is un-premultiplied where libavif does it (4:4:4 fast path: on the
// quantised RGB; subsampled / grey: in float before quantisation), for single items and grids.
public sealed class AvifGridEncodeTests
{
    private static byte[] Asset(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name));

    private static ImageFrame Pattern(int w, int h, bool alpha, bool gray = false)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int v = (x * 2 + y) % 256;
                row[x * ch] = (ushort)(v * 257);
                row[x * ch + 1] = (ushort)((gray ? v : (y * 3) % 256) * 257);
                row[x * ch + 2] = (ushort)((gray ? v : (x + y * 5) % 256) * 257);
                if (alpha) row[x * ch + 3] = (ushort)(x < w / 2 ? 65535 : (x * y) % 256 * 257);
            }
        }
        return f;
    }

    [Test]
    public async Task LosslessGrid_RoundTripsWithLibavifLayout()
    {
        var src = Pattern(300, 200, alpha: true);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Lossless = true, Grid = (2, 2), Rotation = 1 });
        var c = HeifContainer.Parse(avif);
        await Assert.That(c.Items[c.PrimaryId].Type).IsEqualTo("grid");
        var cells = c.ReferencesFrom(c.PrimaryId, "dimg");
        await Assert.That(cells.Count).IsEqualTo(4);
        await Assert.That(c.Ispe(cells[0])).IsEqualTo((150, 100));
        int alphaGrid = c.ReferencesTo(c.PrimaryId, "auxl").Single();
        await Assert.That(c.Items[alphaGrid].Type).IsEqualTo("grid");
        await Assert.That(c.ReferencesFrom(alphaGrid, "dimg").Count).IsEqualTo(4);
        // Transforms only on the grid items, not on the cells.
        await Assert.That(c.Property(c.PrimaryId, "irot")).IsNotNull();
        await Assert.That(c.Property(cells[0], "irot")).IsNull();

        var d = HeifCoder.Decode(avif);   // irot 1 = 90 degrees anti-clockwise
        await Assert.That((int)d.Columns).IsEqualTo(200);
        long diff = 0;
        for (int y = 0; y < 300; y++)
        {
            var row = d.GetPixelRow(y);
            for (int x = 0; x < 200; x++)
            {
                var s = src.GetPixelRow(x);
                int sx = 299 - y;
                for (int k = 0; k < 4; k++) diff += Math.Abs(row[x * 4 + k] - s[sx * 4 + k]);
            }
        }
        await Assert.That(diff).IsEqualTo(0L);
    }

    [Test]
    [Arguments(257, 131, 2, 2, AvifChromaSubsampling.Yuv444, 129, 66)]
    [Arguments(300, 200, 3, 2, AvifChromaSubsampling.Yuv420, 100, 100)]
    [Arguments(300, 200, 5, 1, AvifChromaSubsampling.Yuv420, 64, 200)]
    [Arguments(250, 200, 3, 2, AvifChromaSubsampling.Yuv422, 84, 100)]
    public async Task CellSizes_FollowAvifenc(int w, int h, int cols, int rows, AvifChromaSubsampling ss, int cellW, int cellH)
    {
        byte[] avif = HeifCoder.EncodeAvif(Pattern(w, h, false), new AvifEncodeOptions { Qp = 25, Grid = (cols, rows), ChromaSubsampling = ss });
        var c = HeifContainer.Parse(avif);
        var cells = c.ReferencesFrom(c.PrimaryId, "dimg");
        await Assert.That(cells.Count).IsEqualTo(cols * rows);
        await Assert.That(c.Ispe(cells[0])).IsEqualTo((cellW, cellH));
        var d = HeifCoder.Decode(avif);
        await Assert.That(((int)d.Columns, (int)d.Rows)).IsEqualTo((w, h));
    }

    [Test]
    public async Task GreyGrid_CellsAreMonochrome()
    {
        byte[] avif = HeifCoder.EncodeAvif(Pattern(300, 200, false, gray: true), new AvifEncodeOptions { Qp = 25, Grid = (2, 2) });
        var c = HeifContainer.Parse(avif);
        int cell = c.ReferencesFrom(c.PrimaryId, "dimg")[0];
        var p = c.Property(cell, "av1C")!.Value;
        await Assert.That((c.Data[p.Off + 2] & 0x10) != 0).IsTrue();
    }

    [Test]
    public async Task InvalidGrids_AreRejectedLikeAvifenc()
    {
        // 4:2:0 needs an even output width (MIAF 7.3.11.4.2); 6 cells across 300 px cannot all be >= 64 px.
        await Assert.That(() => HeifCoder.EncodeAvif(Pattern(257, 131, false), new AvifEncodeOptions { Grid = (2, 2), ChromaSubsampling = AvifChromaSubsampling.Yuv420 }))
            .Throws<ArgumentException>();
        await Assert.That(() => HeifCoder.EncodeAvif(Pattern(300, 200, false), new AvifEncodeOptions { Grid = (6, 1) }))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("libavif_prem_10bit_444.avif", "431bd49fdda394f0")]
    [Arguments("libavif_prem_12bit_444.avif", "5c5a5919734ee545")]
    [Arguments("libavif_prem_10bit_444_grid3x2.avif", "720147ad9122a9ec")]
    public async Task PremultipliedHighBitDepth444_MatchesAvifdec(string file, string sha16)
    {
        // Expected: SHA-256 (first 16 hex) of avifdec's 16-bit RGBA PNG samples (little-endian u16).
        var d = HeifCoder.Decode(Asset(file));
        int w = (int)d.Columns, h = (int)d.Rows;
        var buf = new byte[w * h * 8];
        for (int y = 0; y < h; y++)
        {
            var row = d.GetPixelRow(y);
            for (int i = 0; i < w * 4; i++)
            {
                buf[(y * w * 4 + i) * 2] = (byte)row[i];
                buf[(y * w * 4 + i) * 2 + 1] = (byte)(row[i] >> 8);
            }
        }
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(buf))[..16]).IsEqualTo(sha16);
    }
}
