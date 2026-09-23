using System;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Large frames: AV1 caps a tile at 4096 px wide and 4096*2304 px in area, and tile_info() derives the minimum tile
// count from the frame size — so frames wider than 4096 px or over ~9.4 MP must be coded as several tiles (a 4000x3000
// photo coded as one tile is rejected by dav1d). Also checks the signalled seq_level_idx (Annex A size limits, as
// libaom picks it). Cross-decoder exactness (dav1d/libavif) is verified by the dev harness Av1HbdVerify.Large.
public sealed class AvifLargeImageTests
{
    private static ImageFrame Smooth(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 3] = (ushort)(x * 65535L / (w - 1));
                row[x * 3 + 1] = (ushort)(y * 65535L / (h - 1));
                row[x * 3 + 2] = (ushort)(32768 + 20000 * Math.Sin(x / 300.0 + y / 200.0));
            }
        }
        return f;
    }

    private static int LevelOf(byte[] avif)
    {
        for (int i = 0; i + 8 < avif.Length; i++)
            if (avif[i] == 'a' && avif[i + 1] == 'v' && avif[i + 2] == '1' && avif[i + 3] == 'C') return avif[i + 5] & 31;
        throw new InvalidOperationException("no av1C");
    }

    private static double Psnr(ImageFrame a, ImageFrame b)
    {
        double se = 0; long n = 0;
        for (int y = 0; y < a.Rows; y += 5)
        {
            var r0 = a.GetPixelRow(y); var r1 = b.GetPixelRow(y);
            for (int k = 0; k < r0.Length; k += 7) { double d = r0[k] - r1[k]; se += d * d; n++; }
        }
        return 10 * Math.Log10(65535.0 * 65535.0 / (se / n));
    }

    [Test]
    [Arguments(4160, 96, 4)]     // wider than 4096 px: two tile columns; level 3.0 (MaxHSize 4352)
    [Arguments(3072, 3100, 16)]  // 48x49 = 2352 superblocks > MAX_TILE_AREA: two tile rows; 9.5 MP > 5.x: level 6.0
    public async Task MultiTileFrames_RoundTrip_WithCorrectLevel(int w, int h, int level)
    {
        var src = Smooth(w, h);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 30 });
        await Assert.That(LevelOf(avif)).IsEqualTo(level);
        var dec = HeifCoder.Decode(avif);
        await Assert.That((dec.Columns, dec.Rows)).IsEqualTo(((long)w, (long)h));
        await Assert.That(Psnr(src, dec)).IsGreaterThan(40);
    }

    [Test]
    [Arguments(384, 256, 0)]
    [Arguments(517, 333, 1)]
    [Arguments(700, 420, 4)]
    [Arguments(4608, 96, 5)]
    [Arguments(3840, 2160, 12)]
    [Arguments(9000, 260, 16)]
    [Arguments(20000, 10, 31)]
    public async Task SeqLevelIdx_MatchesAnnexALimits(int w, int h, int expected)
        => await Assert.That(SharpImage.Formats.Av1.Av1ObuWriter.SeqLevelIdx(w, h)).IsEqualTo(expected);
}
