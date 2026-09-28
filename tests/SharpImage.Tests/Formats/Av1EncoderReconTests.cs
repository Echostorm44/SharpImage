using System;
using System.Threading.Tasks;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The encoder must predict from exactly what a decoder reconstructs: after coding, the tile is decoded without in-loop
/// filters and compared with the encoder's own reconstruction sample for sample (Av1StillImageEncoder.t_verifyRecon).
/// Conformance tests compare decoders with each other and cannot see an encoder whose internal prediction drifts from
/// the bitstream (it still produces valid streams, just worse ones) — e.g. a DC predictor reading past the frame edge.
/// </summary>
public sealed class Av1EncoderReconTests
{
    // Deterministic content: smooth gradients + texture + edges ("photo"), or a few flat colours with glyph-like
    // strokes ("screen", which turns on palette mode).
    private static (ushort[] Y, ushort[] U, ushort[] V) Content(int w, int h, int cw, int ch, int bitDepth, bool screen)
    {
        var rng = new Random(w * 131 + h * 7 + (screen ? 1 : 0));
        int max = (1 << bitDepth) - 1, sh = bitDepth - 8;
        var y = new ushort[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int v = screen
                    ? (((i / 6) ^ (j / 5)) % 3 == 0 ? 30 : (i % 11 < 2 || j % 9 < 2 ? 220 : 128))
                    : (int)(128 + 60 * Math.Sin(i * 0.11) * Math.Cos(j * 0.07) + ((i + 2 * j) % 37 < 3 ? 50 : 0) + rng.Next(-6, 7));
                y[j * w + i] = (ushort)Math.Clamp(v << sh, 0, max);
            }
        ushort[] Plane(int phase)
        {
            var p = new ushort[cw * ch];
            for (int j = 0; j < ch; j++)
                for (int i = 0; i < cw; i++)
                {
                    int v = screen ? (((i / 4) + (j / 3) + phase) % 4 == 0 ? 60 : 180)
                                   : (int)(128 + 40 * Math.Sin((i + phase * 13) * 0.09) + rng.Next(-4, 5));
                    p[j * cw + i] = (ushort)Math.Clamp(v << sh, 0, max);
                }
            return p;
        }
        return (y, Plane(0), Plane(1));
    }

    [Test]
    [Arguments(Av1PixelLayout.I420, 8, 6, false)]
    [Arguments(Av1PixelLayout.I420, 8, 6, true)]
    [Arguments(Av1PixelLayout.I420, 10, 2, false)]
    [Arguments(Av1PixelLayout.I420, 8, 8, true)]
    [Arguments(Av1PixelLayout.I422, 8, 6, false)]
    [Arguments(Av1PixelLayout.I422, 10, 4, true)]
    [Arguments(Av1PixelLayout.I444, 8, 6, false)]
    [Arguments(Av1PixelLayout.I444, 8, 4, true)]
    [Arguments(Av1PixelLayout.I444, 10, 2, false)]
    [Arguments(Av1PixelLayout.I400, 8, 6, true)]
    [Arguments(Av1PixelLayout.I400, 10, 2, false)]
    public async Task EncoderReconstruction_MatchesDecoder(Av1PixelLayout layout, int bitDepth, int speed, bool screen)
        => await Run(layout, bitDepth, speed, screen, null);

    // Optional tools off in the presets: the intra edge filter and the tx-split searches (square depth + rect leaf).
    [Test]
    [Arguments(Av1PixelLayout.I420, 8, 0, false)]
    [Arguments(Av1PixelLayout.I420, 8, 2, true)]
    [Arguments(Av1PixelLayout.I420, 10, 6, false)]
    [Arguments(Av1PixelLayout.I444, 8, 2, false)]
    [Arguments(Av1PixelLayout.I422, 8, 6, false)]
    [Arguments(Av1PixelLayout.I400, 8, 2, false)]
    public async Task EncoderReconstruction_EdgeFilterAndTxSplit_MatchesDecoder(Av1PixelLayout layout, int bitDepth, int speed, bool screen)
        => await Run(layout, bitDepth, speed, screen, sp => { sp.UseIntraEdgeFilter = true; sp.UseColorTxDepth = true; sp.RectTxDepth = true; sp.RectTxDepthAlt = 2; });

    // libaom's slow-speed luma search in the rect leaf (joint mode x tx size x tx type, tx depths down to 2).
    [Test]
    [Arguments(Av1PixelLayout.I444, 8, 0, false)]
    [Arguments(Av1PixelLayout.I444, 10, 2, true)]
    [Arguments(Av1PixelLayout.I422, 8, 1, false)]
    [Arguments(Av1PixelLayout.I420, 8, 2, false)]
    [Arguments(Av1PixelLayout.I400, 8, 0, false)]
    public async Task EncoderReconstruction_LibaomLuma_MatchesDecoder(Av1PixelLayout layout, int bitDepth, int speed, bool screen)
        => await Run(layout, bitDepth, speed, screen, sp => { sp.LibaomLuma = true; sp.UseColorTxDepth = true; sp.AomTxInitDepthRect = 0; sp.AomTxInitDepthSqr = 0; sp.AomLuma64 = true; sp.AomSub8Rect = true; sp.AomChroma = true; sp.AomTrellis = true; sp.AomTrellisFirst = true; sp.AomTxDomainDist = true; sp.AomPartAbReuse = true; sp.AomPartAbort = true; sp.AomPruneAb = true; sp.AomLessRectCheck = true; sp.UseIntraEdgeFilter = true; });

    private static async Task Run(Av1PixelLayout layout, int bitDepth, int speed, bool screen, Action<Av1EncodeSpeed>? tweak)
    {
        foreach (var (w, h) in new[] { (97, 71), (130, 66) })
        {
            int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout is Av1PixelLayout.I420 or Av1PixelLayout.I400 ? 1 : 0;
            bool mono = layout == Av1PixelLayout.I400;
            int cw = mono ? 0 : (w + ssX) >> ssX, ch = mono ? 0 : (h + ssY) >> ssY;
            var (y, u, v) = Content(w, h, Math.Max(cw, 1), Math.Max(ch, 1), bitDepth, screen);
            foreach (int q in new[] { 60, 150 })
            {
                var prevSpeed = Av1StillImageEncoder.t_speed;
                var sp = Av1EncodeSpeed.ForSpeed(speed);
                tweak?.Invoke(sp);
                Av1StillImageEncoder.t_speed = sp;
                Av1StillImageEncoder.t_verifyRecon = true;
                try
                {
                    var (seq, frame) = Av1StillImageEncoder.BuildColorObus(y, mono ? default : u, mono ? default : v, w, h, q, bitDepth, layout);
                    await Assert.That(seq.Length + frame.Length).IsGreaterThan(0);
                }
                finally
                {
                    Av1StillImageEncoder.t_verifyRecon = false;
                    Av1StillImageEncoder.t_speed = prevSpeed;
                }
            }
        }
    }
}
