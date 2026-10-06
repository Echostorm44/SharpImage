using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>The Wiener convolve's SSE kernel (no AVX2: the default Native AOT instruction set) against the AVX2 one,
/// including taps far outside the Wiener ranges so the saturating 16-bit steps are reached: identical bytes.</summary>
public sealed class AomRestorationSseTests
{
    [Test]
    public async Task WienerSse_MatchesAvx2_AnyTaps()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) return;   // nothing to compare against
        var rng = new Random(41);
        int bad = 0;
        for (int iter = 0; iter < 300; iter++)
        {
            int w = 8 * rng.Next(1, 17), h = rng.Next(1, 65);
            var hf = new AomTaps8();
            var vf = new AomTaps8();
            bool wild = iter % 2 == 1;
            for (int k = 0; k < 8; k++)
            {
                hf[k] = (short)(wild ? rng.Next(-128, 128) : rng.Next(-20, 21));
                vf[k] = (short)(wild ? rng.Next(-1000, 1000) : rng.Next(-20, 21));
            }
            int stride = w + 16, rows = h + 8;
            var src = new byte[stride * rows];
            rng.NextBytes(src);
            if (iter % 7 == 0) Array.Fill(src, (byte)255);
            int s0 = 3 * stride + 4;
            var a = new byte[w * h];
            var b = new byte[w * h];
            AomRestoration.WienerConvolveAddSrcAvx2(src, s0, stride, a, 0, w, hf, vf, w, h);
            AomRestoration.WienerConvolveAddSrcSse(src, s0, stride, b, 0, w, hf, vf, w, h);
            if (!a.AsSpan().SequenceEqual(b)) bad++;
        }
        await Assert.That(bad).IsEqualTo(0);
    }
}
