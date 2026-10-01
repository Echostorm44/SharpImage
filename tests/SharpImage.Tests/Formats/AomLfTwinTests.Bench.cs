using System.Diagnostics;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

public sealed partial class AomLfTwinTests
{
    // Kernel timing against libaom's dispatched kernels (opt-in: SHARPIMAGE_AOMTWIN_BENCH=1 with the twin DLL).
    private static readonly bool Bench = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_BENCH") == "1";

    private static double TimeMs(int reps, Action a)
    {
        // let the tiered JIT reach tier 1
        for (int k = 0; k < 3; k++)
        {
            for (int i = 0; i < 100; i++) a();
            Thread.Sleep(150);
        }
        double best = double.MaxValue;
        for (int t = 0; t < 5; t++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Test]
    public async Task Bench_Kernels()
    {
        if (!Available || !Bench) return;
        var rng = new Random(1);
        const int w = 64, h = 64;
        var dgd = new AomYv12Plane(w, h, w, h, 16);
        var src = new AomYv12Plane(w, h, w, h, 16);
        FillPlane(rng, dgd, 20);
        FillPlane(rng, src, 20);
        int fs = w + 8;
        var f0 = new int[fs * h + 64];
        var f1 = new int[fs * h + 64];
        var sc = new AomRestoration.SgrScratch();
        int reps = 2000;
        foreach (int ep in new[] { 0, 10, 14 })
        {
            double ours = TimeMs(reps, () => AomRestoration.SelfguidedRestoration(dgd.Buf, dgd.Origin, w, h, dgd.Stride, f0, 0, f1, 0, fs, ep, sc));
            double theirs = TimeMs(reps, () =>
            {
                unsafe
                {
                    fixed (byte* d = dgd.Buf)
                    fixed (int* p0 = f0)
                    fixed (int* p1 = f1)
                        Native.twin_selfguided(d + dgd.Origin, w, h, dgd.Stride, p0, p1, fs, ep);
                }
            });
            Console.WriteLine($"selfguided 64x64 ep {ep}: ours {ours * 1000 / reps:F2} us, libaom {theirs * 1000 / reps:F2} us");
            double oursE = TimeMs(reps, () => AomPickRst.LowbdPixelProjError(src.Buf, src.Origin, w, h, src.Stride, dgd.Buf,
                dgd.Origin, dgd.Stride, f0, 0, fs, f1, 0, fs, -20, 60, ep));
            double theirsE = TimeMs(reps, () =>
            {
                unsafe
                {
                    fixed (byte* d = dgd.Buf)
                    fixed (byte* s = src.Buf)
                    fixed (int* p0 = f0)
                    fixed (int* p1 = f1)
                        Native.twin_pixel_proj_error(s + src.Origin, w, h, src.Stride, d + dgd.Origin, dgd.Stride, p0, fs, p1, fs, -20, 60, ep);
                }
            });
            Console.WriteLine($"pixel_proj_error 64x64 ep {ep}: ours {oursE * 1000 / reps:F2} us, libaom {theirsE * 1000 / reps:F2} us");
        }
        {
            var hf = new AomTaps8();
            var vf = new AomTaps8();
            AomWienerInfo wi = default;
            AomRestoration.SetDefaultWiener(ref wi);
            hf = wi.H;
            vf = wi.V;
            var dst = new AomYv12Plane(w, h, w, h, 16);
            var temp = new ushort[135 * 128];
            double ours = TimeMs(reps, () => AomRestoration.WienerConvolveAddSrc(src.Buf, src.Origin, src.Stride, dst.Buf, dst.Origin,
                dst.Stride, hf, vf, w, h, temp));
            var ha = new short[8];
            var va = new short[8];
            for (int i = 0; i < 8; i++) { ha[i] = hf[i]; va[i] = vf[i]; }
            double theirs = TimeMs(reps, () =>
            {
                unsafe
                {
                    fixed (byte* s = src.Buf)
                    fixed (byte* o = dst.Buf)
                    fixed (short* ph = ha)
                    fixed (short* pv = va)
                        Native.twin_wiener_convolve(s + src.Origin, src.Stride, o + dst.Origin, dst.Stride, ph, pv, w, h);
                }
            });
            Console.WriteLine($"wiener 64x64: ours {ours * 1000 / reps:F2} us, libaom {theirs * 1000 / reps:F2} us");
        }
        {
            const int uw = 256, uh = 256;
            var d2 = new AomYv12Plane(uw, uh, uw, uh, 16);
            var s2 = new AomYv12Plane(uw, uh, uw, uh, 16);
            FillPlane(rng, d2, 30);
            FillPlane(rng, s2, 30);
            var M = new long[49];
            var H = new long[49 * 49];
            foreach (int win in new[] { 7, 5 })
            {
                double ours = TimeMs(20, () => AomPickRst.ComputeStats(win, d2, s2, 0, uw, 0, uh, M, H, 0));
                double theirs = TimeMs(20, () =>
                {
                    unsafe
                    {
                        fixed (byte* d = d2.Buf)
                        fixed (byte* s = s2.Buf)
                        fixed (long* pm = M)
                        fixed (long* ph = H)
                            Native.twin_compute_stats(win, d + d2.Origin, s + s2.Origin, 0, uw, 0, uh, d2.Stride, s2.Stride, pm, ph, 0);
                    }
                });
                Console.WriteLine($"compute_stats 256x256 win {win}: ours {ours / 20:F3} ms, libaom {theirs / 20:F3} ms");
            }
        }
        await Assert.That(reps).IsGreaterThan(0);
    }
}
