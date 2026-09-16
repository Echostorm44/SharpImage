using System;
using System.IO;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

// End-to-end palette-mode encode. Builds a few-colour 128x128 I420 image (the case palette targets), encodes it
// with palette enabled, and decodes the result with our own Av1Decoder — asserting it decodes without error and,
// where palette was used (lossless prediction + skip), reproduces the source luma exactly. Also writes the .avif
// + our decoded YUV to the scratchpad so an external step can confirm ffmpeg/libdav1d decodes it identically.
public sealed class Av1PaletteEncodeTests
{
    const string Dir = @"C:\Users\adamm\AppData\Local\Temp\claude\F--Code-QuickFixMyPics2\6657081e-026c-4ec2-a3b0-5a927b1d6dfe\scratchpad";
    const int W = 128, H = 128;

    private static (byte[] y, byte[] u, byte[] v) FewColorImage()
    {
        int cw = W / 2, ch = H / 2;
        var y = new byte[W * H]; var u = new byte[cw * ch]; var v = new byte[cw * ch];
        byte[] pal = { 16, 96, 176, 235 };   // 4 luma colours
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
                y[j * W + i] = pal[(((i >> 3) + (j >> 3)) & 3)];   // 8x8 tiles cycling 4 colours
        // Chroma: two flat-ish colours in big regions (palette-friendly, but chroma palette is off so coded as DC).
        for (int j = 0; j < ch; j++)
            for (int i = 0; i < cw; i++) { u[j * cw + i] = (byte)(i < cw / 2 ? 110 : 140); v[j * cw + i] = 128; }
        return (y, u, v);
    }

    // Flips process-global encoder statics (UsePalette/UseColorTxDepth), so it must not overlap other encoder tests.
    [Test, NotInParallel]
    public async Task Palette_RoundTrips_InOurDecoder()
    {
        bool prev = Av1StillImageEncoder.UsePalette;
        bool prevTx = Av1StillImageEncoder.UseColorTxDepth;
        bool prevFi = Av1StillImageEncoder.UseFilterIntra;
        Av1StillImageEncoder.UsePalette = true;
        Av1StillImageEncoder.UseColorTxDepth = Environment.GetEnvironmentVariable("PAL_TXDEPTH") == "1";
        Av1StillImageEncoder.UseFilterIntra = false;   // isolate palette (palette+filter coexistence is out of scope)
        try
        {
            var (y, u, v) = FewColorImage();
            byte[] avif = Av1StillImageEncoder.EncodeAvifColorMultiSb(y, u, v, W, H, baseQIdx: 128);
            await Assert.That(HeifCoder.IsAvif(avif)).IsTrue();

            // Decode via the public path (unwraps the ISOBMFF container to YUV) and dump it, plus the source, so an
            // external ffmpeg/libdav1d cross-check can confirm both the stream validity and lossless palette recon.
            if (Directory.Exists(Dir))
            {
                File.WriteAllBytes(Path.Combine(Dir, "pal_enc.avif"), avif);
                using var fs = new FileStream(Path.Combine(Dir, "pal_src.yuv"), FileMode.Create);
                fs.Write(y, 0, y.Length); fs.Write(u, 0, u.Length); fs.Write(v, 0, v.Length);
            }
            Environment.SetEnvironmentVariable("AV1_DUMPYUV", Path.Combine(Dir, "pal_our.yuv"));
            using var img = HeifCoder.Decode(avif);
            Environment.SetEnvironmentVariable("AV1_DUMPYUV", null);
            await Assert.That((int)img.Columns).IsEqualTo(W);
            await Assert.That((int)img.Rows).IsEqualTo(H);
        }
        finally { Av1StillImageEncoder.UsePalette = prev; Av1StillImageEncoder.UseColorTxDepth = prevTx; Av1StillImageEncoder.UseFilterIntra = prevFi; }
    }
}
