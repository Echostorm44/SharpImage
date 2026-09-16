using System;
using System.IO;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using TUnit.Core;

// End-to-end filter-intra encode. Encodes a smooth-gradient 256x256 image at a coarse quantizer (where the RD
// search selects the recursive filter-intra predictor for many DC blocks) and decodes it with our own Av1Decoder,
// asserting it decodes to the right dimensions without error. Flips process-global encoder statics, so NotInParallel.
// (Cross-conformance vs ffmpeg/libdav1d is verified out-of-band on the BD-rate corpus.)
namespace SharpImage.Tests.Formats;

public sealed class Av1FilterIntraEncodeTests
{
    const int W = 256, H = 256;

    [Test, NotInParallel]
    public async Task FilterIntra_RoundTrips_InOurDecoder()
    {
        bool prev = Av1StillImageEncoder.UseFilterIntra;
        Av1StillImageEncoder.UseFilterIntra = true;
        try
        {
            int cw = W / 2, ch = H / 2;
            var y = new byte[W * H]; var u = new byte[cw * ch]; var v = new byte[cw * ch];
            // Smooth ramps + soft blocks: coarse-quantized DC blocks where filter-intra tends to win.
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                    y[j * W + i] = (byte)Math.Clamp(40 + (i * 160 / W) + (j * 40 / H) + ((i >> 5) + (j >> 5)) * 3, 0, 255);
            for (int j = 0; j < ch; j++)
                for (int i = 0; i < cw; i++) { u[j * cw + i] = (byte)(110 + (i * 30 / cw)); v[j * cw + i] = (byte)(128 + (j * 20 / ch)); }

            byte[] avif = Av1StillImageEncoder.EncodeAvifColorMultiSb(y, u, v, W, H, baseQIdx: 200);
            await Assert.That(HeifCoder.IsAvif(avif)).IsTrue();
            using var img = HeifCoder.Decode(avif);
            await Assert.That((int)img.Columns).IsEqualTo(W);
            await Assert.That((int)img.Rows).IsEqualTo(H);
        }
        finally { Av1StillImageEncoder.UseFilterIntra = prev; }
    }
}
