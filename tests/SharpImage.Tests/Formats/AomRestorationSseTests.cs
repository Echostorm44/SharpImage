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

    [Test]
    public async Task SelfguidedSse_MatchesAvx2_FltAndApply()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) return;
        var rng = new Random(43);
        var sc = new AomRestoration.SgrScratch();
        int bad = 0;
        for (int iter = 0; iter < 400; iter++)
        {
            int w = rng.Next(1, 65), h = rng.Next(1, 65), ep = rng.Next(16);
            var dgd = new AomYv12Plane(w, h, w, h, 8);
            if (iter % 3 == 0) rng.NextBytes(dgd.Buf);
            else for (int i = 0; i < dgd.Buf.Length; i++) dgd.Buf[i] = (byte)(128 + rng.Next(-30, 31));
            if (iter % 17 == 0) Array.Fill(dgd.Buf, (byte)255);
            // whole flt buffers (the 8-group overhang past width included) must match
            int fs = ((w + 7) & ~7) + 8;
            var a0 = new int[fs * h + 64]; var a1 = new int[fs * h + 64];
            var b0 = new int[fs * h + 64]; var b1 = new int[fs * h + 64];
            AomRestoration.SelfguidedRestorationAvx2(dgd.Buf, dgd.Origin, w, h, dgd.Stride, a0, 0, a1, 0, fs, ep, sc);
            AomRestoration.SelfguidedRestorationSse(dgd.Buf, dgd.Origin, w, h, dgd.Stride, b0, 0, b1, 0, fs, ep, sc);
            if (!a0.AsSpan().SequenceEqual(b0) || !a1.AsSpan().SequenceEqual(b1)) bad++;
            int xqd0 = rng.Next(AomRestoration.SgrprojPrjMin0, AomRestoration.SgrprojPrjMax0 + 1);
            int xqd1 = rng.Next(AomRestoration.SgrprojPrjMin1, AomRestoration.SgrprojPrjMax1 + 1);
            var oa = new AomYv12Plane(w, h, w, h, 8);
            var ob = new AomYv12Plane(w, h, w, h, 8);
            var f0 = new int[AomRestoration.RestorationUnitPelsMax];
            var f1 = new int[AomRestoration.RestorationUnitPelsMax];
            AomRestoration.ApplySelfguidedAvx2(dgd.Buf, dgd.Origin, w, h, dgd.Stride, ep, xqd0, xqd1, oa.Buf, oa.Origin, oa.Stride, f0, f1, sc);
            AomRestoration.ApplySelfguidedSse(dgd.Buf, dgd.Origin, w, h, dgd.Stride, ep, xqd0, xqd1, ob.Buf, ob.Origin, ob.Stride, f0, f1, sc);
            if (!oa.Buf.AsSpan().SequenceEqual(ob.Buf)) bad++;
        }
        await Assert.That(bad).IsEqualTo(0);
    }
}
