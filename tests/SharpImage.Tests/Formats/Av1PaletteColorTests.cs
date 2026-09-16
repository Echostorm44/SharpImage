using System;
using System.Collections.Generic;
using SharpImage.Formats.Av1;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

// Round-trips the luma palette COLOUR coding: Av1CoeffEncode.EncodeLumaPaletteColors must produce a bitstream
// that Av1CoeffDecode.DecodeLumaPalette reads back to the exact same palette — both the pure delta-coded path
// (no neighbours) and the neighbour-cache-reuse path. This verifies the first piece of the palette encoder in
// isolation against the real decoder before it is wired into the block-level RD search.
public sealed class Av1PaletteColorTests
{
    private static Av1CdfContext DefaultCdf()
    {
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, 0);
        return cdf;
    }

    // Encode `colors` at (bx4,by4) with the given encoder neighbour state, decode, return the decoded palette.
    private static ushort[] RoundTrip(ushort[] colors, int palSz, int bx4, int by4,
        Action<Av1TaskContext>? seedNeighbours, int bitDepth = 8)
    {
        var tEnc = new Av1TaskContext();
        seedNeighbours?.Invoke(tEnc);
        var bEnc = new Av1Block { BlockSize = (byte)Av1BlockSize.Bs16x16, PalSzY = (byte)palSz };
        var w = new Av1MsacWriter();
        Av1CoeffEncode.EncodeLumaPaletteColors(w, tEnc, ref bEnc, colors, palSz, bx4, by4, bitDepth);
        byte[] bytes = w.Finish();

        var tDec = new Av1TaskContext();
        seedNeighbours?.Invoke(tDec);
        var bDec = new Av1Block { BlockSize = (byte)Av1BlockSize.Bs16x16, PalSzY = (byte)palSz };
        var msac = new Av1Msac(bytes, disableCdfUpdate: true);
        Av1CoeffDecode.DecodeLumaPalette(ref msac, DefaultCdf().Mode, tDec, ref bDec, 0, bx4, by4, bitDepth);
        var outColors = new ushort[palSz];
        Array.Copy(tDec.PalColorsY, outColors, palSz);
        return outColors;
    }

    // Seeds one neighbour palette (left at row `pos`, and above at col `pos` when useAbove) so the cache path is
    // exercised — mirrors DecodeLumaPalette's read of Left/Above.PalSz + PalPrevY.
    private static Action<Av1TaskContext> SeedPalette(int pos, ushort[] pal, bool left, bool above) => t =>
    {
        if (left)
        {
            t.Left.PalSz[pos] = (byte)pal.Length;
            for (int i = 0; i < pal.Length; i++) t.PalPrevY[1, pos, i] = pal[i];
        }
        if (above)
        {
            t.Above.PalSz[pos] = (byte)pal.Length;
            for (int i = 0; i < pal.Length; i++) t.PalPrevY[0, pos, i] = pal[i];
        }
    };

    [Test]
    public async Task DeltaPath_NoNeighbours_RoundTrips()
    {
        // No neighbour palettes ⇒ empty cache ⇒ every colour is delta-coded (the "new" path).
        var cases = new List<ushort[]>
        {
            new ushort[] { 10, 200 },
            new ushort[] { 0, 1, 2 },                 // minimal deltas
            new ushort[] { 5, 40, 41, 200, 255 },     // incl. adjacent + max value
            new ushort[] { 0, 64, 128, 192, 255 },
            new ushort[] { 3, 9, 17, 33, 65, 129, 200, 255 }, // full 8-colour palette
        };
        foreach (var colors in cases)
        {
            var outC = RoundTrip(colors, colors.Length, bx4: 4, by4: 4, seedNeighbours: null);
            await Assert.That(outC).IsEquivalentTo(colors);
        }
    }

    [Test]
    public async Task CachePath_WithNeighbours_RoundTrips()
    {
        // Left neighbour palette feeds the cache; some target colours are reused from it, some are new.
        var neighbour = new ushort[] { 20, 60, 100, 140 };
        // Target reuses 60 and 140 from cache, adds 10 and 200 as new.
        var colors = new ushort[] { 10, 60, 140, 200 };
        var outC = RoundTrip(colors, colors.Length, bx4: 4, by4: 4, SeedPalette(4, neighbour, left: true, above: false));
        await Assert.That(outC).IsEquivalentTo(colors);
    }

    [Test]
    public async Task CachePath_AboveAndLeft_RoundTrips()
    {
        var leftPal = new ushort[] { 15, 45, 90 };
        var abovePal = new ushort[] { 30, 90, 150 };   // 90 shared → dedup in merged cache
        // Reuse a mix from the merged cache {15,30,45,90,150} plus new colours.
        var colors = new ushort[] { 15, 45, 90, 150, 220 };
        var outC = RoundTrip(colors, colors.Length, bx4: 6, by4: 6,
            t => { SeedPalette(6, leftPal, left: true, above: false)(t); SeedPalette(6, abovePal, left: false, above: true)(t); });
        await Assert.That(outC).IsEquivalentTo(colors);
    }

    [Test]
    public async Task Random_RoundTrips()
    {
        var rng = new Random(12345);
        for (int iter = 0; iter < 200; iter++)
        {
            int palSz = 2 + rng.Next(7);
            var set = new SortedSet<ushort>();
            while (set.Count < palSz) set.Add((ushort)rng.Next(256));
            var colors = new ushort[palSz];
            set.CopyTo(colors);

            Action<Av1TaskContext>? seed = null;
            if (rng.Next(2) == 0)
            {
                // Random neighbour palette, sometimes overlapping the target.
                int nsz = 2 + rng.Next(7);
                var nset = new SortedSet<ushort>();
                while (nset.Count < nsz) nset.Add((ushort)rng.Next(256));
                if (rng.Next(2) == 0) foreach (var c in colors) if (nset.Count < 8) nset.Add(c);
                var npal = new ushort[nset.Count]; nset.CopyTo(npal);
                seed = SeedPalette(4, npal, left: true, above: rng.Next(2) == 0);
            }
            var outC = RoundTrip(colors, palSz, bx4: 4, by4: 4, seed);
            await Assert.That(outC).IsEquivalentTo(colors);
        }
    }
}
