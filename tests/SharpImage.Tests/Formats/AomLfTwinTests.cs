using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twins of the libaom deblocking / loop-restoration port (AomLpf, AomLoopFilter, AomPickLpf, AomRestoration, AomPickRst)
// against libaom 3.14.1 through aomtwin_lf.dll (scratchpad aomtwin_lf/: twin_kern.c exports the RTCD-dispatched SIMD
// kernels and drives av1_loop_filter_frame_mt on synthetic frames; twin_pickrst.c is a renamed copy of pickrst.c for its
// statics; twin_capture.c encodes through libaom's API and dumps libaom's own post-filter inputs and decisions).
// Opt-in: point SHARPIMAGE_AOMTWIN_LF at aomtwin_lf.dll; without it the tests pass without checking.
[NotInParallel]
public sealed partial class AomLfTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_LF");
    private static readonly bool Available = DllPath != null && (File.Exists(DllPath)
        ? Load() : throw new FileNotFoundException("SHARPIMAGE_AOMTWIN_LF is set but the DLL does not exist", DllPath));

    private static bool Load()
    {
        AomTwinNative.Register("aomtwin_lf", DllPath!);
        Native.twin_init();
        return true;
    }

    private static unsafe class Native
    {
        private const string D = "aomtwin_lf";
        [DllImport(D)] public static extern void twin_init();
        [DllImport(D)] public static extern int twin_lpf(int kind, int vert, int variant, byte* s, int pitch, byte* b0, byte* l0,
            byte* t0, byte* b1, byte* l1, byte* t1);
        [DllImport(D)] public static extern long twin_get_y_sse_part(byte* a, int astr, byte* b, int bstr, int hs, int w, int vs, int h);
        [DllImport(D)] public static extern ulong twin_get_y_var(byte* a, int astr, int hs, int w, int vs, int h);
        [DllImport(D)] public static extern void twin_compute_stats(int win, byte* dgd, byte* src, int hStart, int hEnd, int vStart,
            int vEnd, int dgdStride, int srcStride, long* M, long* H, int ds);
        [DllImport(D)] public static extern void twin_calc_proj_params(byte* src, int w, int h, int ss, byte* dat, int ds, int* flt0,
            int f0s, int* flt1, int f1s, long* H4, long* C2, int ep);
        [DllImport(D)] public static extern long twin_pixel_proj_error(byte* src, int w, int h, int ss, byte* dat, int ds, int* flt0,
            int f0s, int* flt1, int f1s, int xq0, int xq1, int ep);
        [DllImport(D)] public static extern int twin_selfguided(byte* dgd, int w, int h, int stride, int* flt0, int* flt1,
            int fltStride, int ep);
        [DllImport(D)] public static extern int twin_apply_sgr(byte* dat, int w, int h, int stride, int ep, int xqd0, int xqd1,
            byte* dst, int dstStride);
        [DllImport(D)] public static extern void twin_wiener_convolve(byte* src, int ss, byte* dst, int ds, short* hf, short* vf,
            int w, int h);
        [DllImport(D)] public static extern int twin_count_refsubexpfin(int n, int k, int r, int v);
        [DllImport(D)] public static extern void twin_lf_frame(byte* y, byte* u, byte* v, int ystride, int uvstride, int cw, int ch,
            int ssX, int ssY, int mono, int miRows, int miCols, int* blkOfMi, int nblk, byte* bsize, byte* txSize, byte* skip,
            sbyte* ref0, byte* mode, byte* seg, int lvl0, int lvl1, int lvlu, int lvlv, int sharp, int planeStart,
            int planeEnd, int partial, int lpfOptLevel, int sbSize);
        [DllImport(D)] public static extern void twin_wiener_decompose(int win, long* M, long* H, int* a, int* b);
        [DllImport(D)] public static extern void twin_finalize_sym_filter(int win, int* f, short* out8);
        [DllImport(D)] public static extern long twin_compute_score(int win, long* M, long* H, short* vf, short* hf);
        [DllImport(D)] public static extern int twin_linsolve(int n, long* A, int stride, long* b, long* x);
        [DllImport(D)] public static extern void twin_search_sgr(byte* dat, int w, int h, int dstride, byte* src, int sstride,
            int puW, int puH, int pruning, int* out3);
        [DllImport(D)] public static extern int twin_count_wiener_bits(int win, short* vf, short* hf, short* rvf, short* rhf);
        [DllImport(D)] public static extern int twin_count_sgrproj_bits(int ep, int x0, int x1, int rx0, int rx1);
        [DllImport(D, CharSet = CharSet.Ansi)] public static extern int twin_capture(byte* y, byte* u, byte* v, int w, int h,
            int ss444, int speed, int quantizer, int skipPostproc, string outPath);
        [DllImport(D, CharSet = CharSet.Ansi)] public static extern int twin_capture2(byte* y, byte* u, byte* v, int w, int h,
            int ss444, int speed, int quantizer, int skipPostproc, int tileColsLog2, int tileRowsLog2, int sharpness,
            string outPath);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A smooth-ish random plane (gradients + noise + occasional steps) so the flat / mask branches all fire.</summary>
    private static void FillPlane(Random rng, AomYv12Plane p, int noise)
    {
        int bx = rng.Next(256), gx = rng.Next(-3, 4), gy = rng.Next(-3, 4);
        for (int y = -p.Border; y < p.Height + p.Border; y++)
            for (int x = -p.Border; x < p.Width + p.Border; x++)
            {
                int v = bx + ((gx * x + gy * y) >> 2) + rng.Next(-noise, noise + 1);
                if (((x >> 3) + (y >> 3)) % 7 == 0) v += 40;
                p.Buf[p.At(x, y)] = (byte)Math.Clamp(v, 0, 255);
            }
    }

    private static byte[] RandomThresh(Random rng, out int lvl)
    {
        lvl = rng.Next(64);
        int sharp = rng.Next(8);
        int lim = lvl >> ((sharp > 0 ? 1 : 0) + (sharp > 4 ? 1 : 0));
        if (sharp > 0 && lim > 9 - sharp) lim = 9 - sharp;
        if (lim < 1) lim = 1;
        var t = new byte[48];
        for (int i = 0; i < 16; i++)
        {
            t[i] = (byte)(2 * (lvl + 2) + lim);
            t[16 + i] = (byte)lim;
            t[32 + i] = (byte)(lvl >> 4);
        }
        return t;
    }

    // ---- leaf kernels -------------------------------------------------------------------------------------------------

    [Test]
    public async Task Lpf_AllKernels_Random()
    {
        if (!Available) return;
        var rng = new Random(11);
        int mismatches = 0, runs = 0, simdMismatches = 0, simdRuns = 0;
        foreach (int kind in new[] { 4, 6, 8, 14 })
            for (int vert = 0; vert < 2; vert++)
                for (int variant = 0; variant < 3; variant++)
                {
                    if (variant == 2 && kind == 6 && vert == 0) { } // aom_lpf_horizontal_6_quad exists
                    for (int iter = 0; iter < 3000; iter++)
                    {
                        var ours = new AomYv12Plane(32, 32, 32, 32, 16);
                        FillPlane(rng, ours, rng.Next(4) == 0 ? 20 : rng.Next(4));
                        if (iter % 16 == 0) rng.NextBytes(ours.Buf);
                        var theirs = new AomYv12Plane(32, 32, 32, 32, 16);
                        theirs.CopyFrom(ours);
                        var simd = new AomYv12Plane(32, 32, 32, 32, 16);
                        simd.CopyFrom(ours);
                        byte[] t0 = RandomThresh(rng, out _), t1 = RandomThresh(rng, out _);
                        if (variant == 1 && iter % 2 == 0) t1 = t0;   // the deblocker's dual calls pass one set twice
                        int s = ours.At(8, 8);
                        int lines = variant == 0 ? 1 : variant == 1 ? 2 : 4;
                        int across = vert == 1 ? 1 : ours.Stride, along = vert == 1 ? ours.Stride : 1;
                        for (int k = 0; k < lines; k++)
                        {
                            byte[] t = variant == 1 && k == 1 ? t1 : t0;
                            AomLpf.Apply(kind, ours.Buf, s + 4 * k * along, across, along, t[0], t[16], t[32]);
                        }
                        bool simdComparable = AomLpf.SimdSupported && (variant != 1 || ReferenceEquals(t0, t1));
                        if (simdComparable)
                        {
                            if (vert == 1) AomLpf.Vertical(kind, simd.Buf, s, simd.Stride, 4 * lines, t0[0], t0[16], t0[32]);
                            else AomLpf.Horizontal(kind, simd.Buf, s, simd.Stride, 4 * lines, t0[0], t0[16], t0[32]);
                        }
                        unsafe
                        {
                            // libaom's loop_filter_thresh vectors are 16-byte aligned (the SSE2 kernels load them aligned)
                            byte* al = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(96, 32);
                            try
                            {
                                for (int i = 0; i < 48; i++) { al[i] = t0[i]; al[48 + i] = t1[i]; }
                                fixed (byte* b = theirs.Buf)
                                    Native.twin_lpf(kind, vert, variant, b + s, theirs.Stride, al, al + 16, al + 32, al + 48,
                                        al + 64, al + 80);
                            }
                            finally
                            {
                                System.Runtime.InteropServices.NativeMemory.AlignedFree(al);
                            }
                        }
                        if (simdComparable)
                        {
                            simdRuns++;
                            if (!simd.Buf.AsSpan().SequenceEqual(theirs.Buf)) simdMismatches++;
                        }
                        runs++;
                        if (!ours.Buf.AsSpan().SequenceEqual(theirs.Buf)) mismatches++;
                    }
                }
        await Assert.That(runs).IsGreaterThan(0);
        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(simdRuns).IsGreaterThan(AomLpf.SimdSupported ? 0 : -1);
        await Assert.That(simdMismatches).IsEqualTo(0);
    }

    [Test]
    public async Task SseAndVar_Random()
    {
        if (!Available) return;
        var rng = new Random(3);
        for (int iter = 0; iter < 300; iter++)
        {
            int w = rng.Next(1, 200), h = rng.Next(1, 200);
            var a = new AomYv12Plane(w, h, w, h, 8);
            var b = new AomYv12Plane(w, h, w, h, 8);
            rng.NextBytes(a.Buf);
            rng.NextBytes(b.Buf);
            int hs = rng.Next(w), vs = rng.Next(h), ww = rng.Next(1, w - hs + 1), hh = rng.Next(1, h - vs + 1);
            long ours = AomSse.SsePart(a, b, hs, ww, vs, hh);
            ulong ov = AomSse.VarPart(a, hs, ww, vs, hh);
            long theirs;
            ulong tv;
            unsafe
            {
                fixed (byte* pa = a.Buf)
                fixed (byte* pb = b.Buf)
                {
                    theirs = Native.twin_get_y_sse_part(pa + a.Origin, a.Stride, pb + b.Origin, b.Stride, hs, ww, vs, hh);
                    tv = Native.twin_get_y_var(pa + a.Origin, a.Stride, hs, ww, vs, hh);
                }
            }
            if (ours != theirs) await Assert.That(ours).IsEqualTo(theirs);
            if (ov != tv) await Assert.That(ov).IsEqualTo(tv);
        }
    }

    [Test]
    public async Task ComputeStats_Random()
    {
        if (!Available) return;
        var rng = new Random(5);
        long area = 0;
        for (int iter = 0; iter < 60; iter++)
        {
            int win = rng.Next(2) == 0 ? 7 : 5;
            int w = rng.Next(8, 385), h = rng.Next(8, 385);
            var dgd = new AomYv12Plane(w, h, w, h, 8);
            var src = new AomYv12Plane(w, h, w, h, 8);
            FillPlane(rng, dgd, rng.Next(1, 60));
            FillPlane(rng, src, rng.Next(1, 60));
            if (iter % 7 == 0) { rng.NextBytes(dgd.Buf); rng.NextBytes(src.Buf); }   // adversarial: full-range noise
            int hs = rng.Next(0, w / 2), vs = rng.Next(0, h / 2), he = rng.Next(hs + 1, w + 1), ve = rng.Next(vs + 1, h + 1);
            if (iter < 6)
            {
                // the largest unit (1.5 x 256 square), full-range noise and a sparse-peak image (avg near 0, |Y| near 255)
                w = h = 384;
                dgd = new AomYv12Plane(w, h, w, h, 8);
                src = new AomYv12Plane(w, h, w, h, 8);
                if (iter < 3) { rng.NextBytes(dgd.Buf); rng.NextBytes(src.Buf); }
                else
                    for (int i = 0; i < dgd.Buf.Length; i++)
                    {
                        dgd.Buf[i] = (byte)(rng.Next(9) == 0 ? 255 : 0);
                        src.Buf[i] = (byte)(rng.Next(9) == 0 ? 255 : 0);
                    }
                hs = vs = 0;
                he = ve = 384;
            }
            var M = new long[49];
            var H = new long[49 * 49];
            int ds = iter >= 6 && iter % 2 == 1 ? 1 : 0;   // use_downsampled_wiener_stats (not set by the all-intra speeds)
            AomPickRst.ComputeStats(win, dgd, src, hs, he, vs, ve, M, H, ds);
            var tM = new long[49];
            var tH = new long[49 * 49];
            unsafe
            {
                fixed (byte* d = dgd.Buf)
                fixed (byte* s = src.Buf)
                fixed (long* pm = tM)
                fixed (long* ph = tH)
                    Native.twin_compute_stats(win, d + dgd.Origin, s + src.Origin, hs, he, vs, ve, dgd.Stride, src.Stride, pm, ph, ds);
            }
            int n2 = win * win;
            area += (long)(he - hs) * (ve - vs);
            await Assert.That(M.AsSpan(0, n2).SequenceEqual(tM.AsSpan(0, n2))).IsTrue();
            await Assert.That(H.AsSpan(0, n2 * n2).SequenceEqual(tH.AsSpan(0, n2 * n2))).IsTrue();

            // the Wiener solve on these statistics
            var a = new int[7];
            var b = new int[7];
            var ta = new int[7];
            var tb = new int[7];
            AomPickRst.WienerDecomposeSepSym(win, M, H, a, b);
            unsafe
            {
                fixed (long* pm = tM)
                fixed (long* ph = tH)
                fixed (int* pa = ta)
                fixed (int* pb = tb)
                    Native.twin_wiener_decompose(win, pm, ph, pa, pb);
            }
            await Assert.That(a.AsSpan(0, win).SequenceEqual(ta.AsSpan(0, win))).IsTrue();
            await Assert.That(b.AsSpan(0, win).SequenceEqual(tb.AsSpan(0, win))).IsTrue();
            var vf = new AomTaps8();
            var hf = new AomTaps8();
            AomPickRst.FinalizeSymFilter(win, a, ref vf);
            AomPickRst.FinalizeSymFilter(win, b, ref hf);
            var tvf = new short[8];
            var thf = new short[8];
            unsafe
            {
                fixed (int* pa = ta)
                fixed (int* pb = tb)
                fixed (short* pv = tvf)
                fixed (short* phh = thf)
                {
                    Native.twin_finalize_sym_filter(win, pa, pv);
                    Native.twin_finalize_sym_filter(win, pb, phh);
                }
            }
            for (int i = 0; i < 8; i++)
            {
                await Assert.That(vf[i]).IsEqualTo(tvf[i]);
                await Assert.That(hf[i]).IsEqualTo(thf[i]);
            }
            long score = AomPickRst.ComputeScore(win, M, H, vf, hf);
            long tscore;
            unsafe
            {
                fixed (long* pm = tM)
                fixed (long* ph = tH)
                fixed (short* pv = tvf)
                fixed (short* phh = thf)
                    tscore = Native.twin_compute_score(win, pm, ph, pv, phh);
            }
            await Assert.That(score).IsEqualTo(tscore);
        }
        Console.WriteLine($"compute_stats area {area}");
        await Assert.That(area).IsGreaterThan(100000);
    }

    [Test]
    public async Task Linsolve_Random()
    {
        if (!Available) return;
        var rng = new Random(9);
        for (int iter = 0; iter < 5000; iter++)
        {
            int n = rng.Next(1, 4), stride = 4;
            var A = new long[16];
            var b = new long[4];
            long scale = iter % 3 == 0 ? 1L << 40 : iter % 3 == 1 ? 1L << 24 : 1000;
            for (int i = 0; i < 16; i++) A[i] = (long)((rng.NextDouble() * 2 - 1) * scale);
            for (int i = 0; i < 4; i++) b[i] = (long)((rng.NextDouble() * 2 - 1) * scale);
            if (iter % 50 == 0) A[0] = 0;
            var A2 = (long[])A.Clone();
            var b2 = (long[])b.Clone();
            var x = new long[4];
            var x2 = new long[4];
            bool ok = AomPickRst.LinsolveWiener(n, A, stride, b, x);
            int ok2;
            unsafe
            {
                fixed (long* pa = A2)
                fixed (long* pb = b2)
                fixed (long* px = x2)
                    ok2 = Native.twin_linsolve(n, pa, stride, pb, px);
            }
            await Assert.That(ok ? 1 : 0).IsEqualTo(ok2);
            if (ok) await Assert.That(x.AsSpan(0, n).SequenceEqual(x2.AsSpan(0, n))).IsTrue();
        }
    }

    [Test]
    public async Task Selfguided_And_Projection_Random()
    {
        if (!Available) return;
        var rng = new Random(7);
        var sc = new AomRestoration.SgrScratch();
        for (int iter = 0; iter < 400; iter++)
        {
            int w = rng.Next(1, 65), h = rng.Next(1, 65), ep = rng.Next(16);
            var dgd = new AomYv12Plane(w, h, w, h, 8);
            var src = new AomYv12Plane(w, h, w, h, 8);
            FillPlane(rng, dgd, rng.Next(0, 40));
            FillPlane(rng, src, rng.Next(0, 40));
            if (iter % 9 == 0) rng.NextBytes(dgd.Buf);
            if (iter % 13 == 0) Array.Fill(dgd.Buf, (byte)(iter % 26 == 0 ? 255 : 0));
            int fs = ((w + 7) & ~7) + 8;
            var f0 = new int[fs * h];
            var f1 = new int[fs * h];
            AomRestoration.SelfguidedRestoration(dgd.Buf, dgd.Origin, w, h, dgd.Stride, f0, 0, f1, 0, fs, ep, sc);
            var c0 = new int[fs * h];
            var c1 = new int[fs * h];
            AomRestoration.SelfguidedRestorationC(dgd.Buf, dgd.Origin, w, h, dgd.Stride, c0, 0, c1, 0, fs, ep, sc);
            for (int r = 0; r < h; r++)
            {
                if (AomRestoration.SgrR0[ep] > 0) await Assert.That(f0.AsSpan(r * fs, w).SequenceEqual(c0.AsSpan(r * fs, w))).IsTrue();
                if (AomRestoration.SgrR1[ep] > 0) await Assert.That(f1.AsSpan(r * fs, w).SequenceEqual(c1.AsSpan(r * fs, w))).IsTrue();
            }
            var t0 = new int[fs * h + 64];
            var t1 = new int[fs * h + 64];
            unsafe
            {
                fixed (byte* d = dgd.Buf)
                fixed (int* p0 = t0)
                fixed (int* p1 = t1)
                    Native.twin_selfguided(d + dgd.Origin, w, h, dgd.Stride, p0, p1, fs, ep);
            }
            for (int r = 0; r < h; r++)
            {
                if (AomRestoration.SgrR0[ep] > 0)
                    await Assert.That(f0.AsSpan(r * fs, w).SequenceEqual(t0.AsSpan(r * fs, w))).IsTrue();
                if (AomRestoration.SgrR1[ep] > 0)
                    await Assert.That(f1.AsSpan(r * fs, w).SequenceEqual(t1.AsSpan(r * fs, w))).IsTrue();
            }
            // projection statistics and error on these filtered outputs (the SIMD needs width % 8 == 0 for the stats)
            int xq0 = rng.Next(-96, 32), xq1 = rng.Next(-32, 161);
            long ours = AomPickRst.LowbdPixelProjError(src.Buf, src.Origin, w, h, src.Stride, dgd.Buf, dgd.Origin,
                dgd.Stride, f0, 0, fs, f1, 0, fs, xq0, xq1, ep);
            var H = new long[4];
            var C = new long[2];
            AomPickRst.CalcProjParams(src.Buf, src.Origin, w, h, src.Stride, dgd.Buf, dgd.Origin, dgd.Stride, f0, 0, fs,
                f1, 0, fs, H, C, ep);
            var Hc = new long[4];
            var Cc = new long[2];
            AomPickRst.CalcProjParamsC(src.Buf, src.Origin, w, h, src.Stride, dgd.Buf, dgd.Origin, dgd.Stride, f0, 0, fs,
                f1, 0, fs, Hc, Cc, ep);
            await Assert.That(H.AsSpan().SequenceEqual(Hc)).IsTrue();
            await Assert.That(C.AsSpan().SequenceEqual(Cc)).IsTrue();
            await Assert.That(ours).IsEqualTo(AomPickRst.LowbdPixelProjErrorC(src.Buf, src.Origin, w, h, src.Stride, dgd.Buf,
                dgd.Origin, dgd.Stride, f0, 0, fs, f1, 0, fs, xq0, xq1, ep));
            var tH = new long[4];
            var tC = new long[2];
            long theirs;
            unsafe
            {
                fixed (byte* d = dgd.Buf)
                fixed (byte* s = src.Buf)
                fixed (int* p0 = f0)
                fixed (int* p1 = f1)
                fixed (long* ph = tH)
                fixed (long* pc = tC)
                {
                    theirs = Native.twin_pixel_proj_error(s + src.Origin, w, h, src.Stride, d + dgd.Origin, dgd.Stride, p0,
                        fs, p1, fs, xq0, xq1, ep);
                    if ((w & 7) == 0)
                        Native.twin_calc_proj_params(s + src.Origin, w, h, src.Stride, d + dgd.Origin, dgd.Stride, p0, fs,
                            p1, fs, ph, pc, ep);
                }
            }
            await Assert.That(ours).IsEqualTo(theirs);
            if ((w & 7) == 0)
            {
                await Assert.That(H.AsSpan().SequenceEqual(tH)).IsTrue();
                await Assert.That(C.AsSpan().SequenceEqual(tC)).IsTrue();
            }
        }
    }

    [Test]
    public async Task ApplySelfguided_Random()
    {
        if (!Available) return;
        var rng = new Random(8);
        var sc = new AomRestoration.SgrScratch();
        var f0 = new int[AomRestoration.RestorationUnitPelsMax];
        var f1 = new int[AomRestoration.RestorationUnitPelsMax];
        for (int iter = 0; iter < 600; iter++)
        {
            int w = rng.Next(1, 65), h = rng.Next(1, 65), ep = rng.Next(16);
            int xqd0 = rng.Next(AomRestoration.SgrprojPrjMin0, AomRestoration.SgrprojPrjMax0 + 1);
            int xqd1 = rng.Next(AomRestoration.SgrprojPrjMin1, AomRestoration.SgrprojPrjMax1 + 1);
            if (iter % 10 == 0) { xqd0 = rng.Next(2) == 0 ? AomRestoration.SgrprojPrjMin0 : AomRestoration.SgrprojPrjMax0; }
            var dat = new AomYv12Plane(w, h, w, h, 8);
            FillPlane(rng, dat, rng.Next(0, 60));
            if (iter % 5 == 0) rng.NextBytes(dat.Buf);
            if (iter % 13 == 0) Array.Fill(dat.Buf, (byte)(iter % 26 == 0 ? 255 : 0));
            var ours = new AomYv12Plane(w, h, w, h, 8);
            var theirs = new AomYv12Plane(w, h, w, h, 8);
            AomRestoration.ApplySelfguided(dat.Buf, dat.Origin, w, h, dat.Stride, ep, xqd0, xqd1, ours.Buf, ours.Origin,
                ours.Stride, f0, f1, sc);
            unsafe
            {
                fixed (byte* d = dat.Buf)
                fixed (byte* o = theirs.Buf)
                    Native.twin_apply_sgr(d + dat.Origin, w, h, dat.Stride, ep, xqd0, xqd1, o + theirs.Origin, theirs.Stride);
            }
            for (int r = 0; r < h; r++)
                await Assert.That(ours.Buf.AsSpan(ours.At(0, r), w).SequenceEqual(theirs.Buf.AsSpan(theirs.At(0, r), w))).IsTrue();
        }
    }

    [Test]
    public async Task WienerConvolve_Random()
    {
        if (!Available) return;
        var rng = new Random(4);
        var temp = new ushort[135 * 128];
        for (int iter = 0; iter < 1500; iter++)
        {
            int w = (iter & 1) == 0 ? 16 * rng.Next(1, 5) : 8 * rng.Next(1, 9), h = rng.Next(1, 65);
            var hf = new AomTaps8();
            var vf = new AomTaps8();
            bool extreme = iter % 8 == 0;
            int P(int lo, int hi) => extreme ? (rng.Next(2) == 0 ? lo : hi) : rng.Next(lo, hi + 1);
            for (int d = 0; d < 2; d++)
            {
                ref AomTaps8 f = ref d == 0 ? ref hf : ref vf;
                bool chroma = rng.Next(3) == 0;
                f[0] = (short)(chroma ? 0 : P(AomRestoration.WienerFiltTap0Minv, AomRestoration.WienerFiltTap0Maxv));
                f[1] = (short)P(AomRestoration.WienerFiltTap1Minv, AomRestoration.WienerFiltTap1Maxv);
                f[2] = (short)P(AomRestoration.WienerFiltTap2Minv, AomRestoration.WienerFiltTap2Maxv);
                f[6] = f[0];
                f[5] = f[1];
                f[4] = f[2];
                f[3] = (short)(-2 * (f[0] + f[1] + f[2]));
                f[7] = 0;
            }
            var src = new AomYv12Plane(w, h, w, h, 16);
            FillPlane(rng, src, rng.Next(0, 80));
            if (iter % 4 == 0) rng.NextBytes(src.Buf);
            if (iter % 13 == 0) Array.Fill(src.Buf, (byte)(iter % 26 == 0 ? 255 : 0));
            var ours = new AomYv12Plane(w, h, w, h, 8);
            var theirs = new AomYv12Plane(w, h, w, h, 8);
            AomRestoration.WienerConvolveAddSrc(src.Buf, src.Origin, src.Stride, ours.Buf, ours.Origin, ours.Stride, hf, vf,
                w, h, temp);
            var oursC = new AomYv12Plane(w, h, w, h, 8);
            AomRestoration.WienerConvolveAddSrcC(src.Buf, src.Origin, src.Stride, oursC.Buf, oursC.Origin, oursC.Stride, hf, vf,
                w, h, temp);
            for (int r = 0; r < h; r++)
                await Assert.That(ours.Buf.AsSpan(ours.At(0, r), w).SequenceEqual(oursC.Buf.AsSpan(oursC.At(0, r), w))).IsTrue();
            var ha = new short[8];
            var va = new short[8];
            for (int i = 0; i < 8; i++) { ha[i] = hf[i]; va[i] = vf[i]; }
            unsafe
            {
                fixed (byte* s = src.Buf)
                fixed (byte* o = theirs.Buf)
                fixed (short* ph = ha)
                fixed (short* pv = va)
                    Native.twin_wiener_convolve(s + src.Origin, src.Stride, o + theirs.Origin, theirs.Stride, ph, pv, w, h);
            }
            for (int r = 0; r < h; r++)
                await Assert.That(ours.Buf.AsSpan(ours.At(0, r), w).SequenceEqual(theirs.Buf.AsSpan(theirs.At(0, r), w))).IsTrue();
        }
    }

    [Test]
    public async Task BitCounts_Random()
    {
        if (!Available) return;
        var rng = new Random(2);
        for (int iter = 0; iter < 20000; iter++)
        {
            int n = rng.Next(2, 300), k = rng.Next(0, 5), r = rng.Next(n), v = rng.Next(n);
            await Assert.That(AomPickRst.CountRefSubexpfin(n, k, r, v)).IsEqualTo(Native.twin_count_refsubexpfin(n, k, r, v));
        }
        for (int iter = 0; iter < 5000; iter++)
        {
            var s = new AomSgrprojInfo
            {
                Ep = rng.Next(16), Xqd0 = rng.Next(AomRestoration.SgrprojPrjMin0, AomRestoration.SgrprojPrjMax0 + 1),
                Xqd1 = rng.Next(AomRestoration.SgrprojPrjMin1, AomRestoration.SgrprojPrjMax1 + 1),
            };
            var rf = new AomSgrprojInfo
            {
                Xqd0 = rng.Next(AomRestoration.SgrprojPrjMin0, AomRestoration.SgrprojPrjMax0 + 1),
                Xqd1 = rng.Next(AomRestoration.SgrprojPrjMin1, AomRestoration.SgrprojPrjMax1 + 1),
            };
            await Assert.That(AomPickRst.CountSgrprojBits(s, rf))
                .IsEqualTo(Native.twin_count_sgrproj_bits(s.Ep, s.Xqd0, s.Xqd1, rf.Xqd0, rf.Xqd1));
            var a = RandomWiener(rng);
            var b = RandomWiener(rng);
            int win = rng.Next(2) == 0 ? 7 : 5;
            short[] av = new short[8], ah = new short[8], bv = new short[8], bh = new short[8];
            for (int i = 0; i < 8; i++) { av[i] = a.V[i]; ah[i] = a.H[i]; bv[i] = b.V[i]; bh[i] = b.H[i]; }
            int theirs;
            unsafe
            {
                fixed (short* p0 = av)
                fixed (short* p1 = ah)
                fixed (short* p2 = bv)
                fixed (short* p3 = bh)
                    theirs = Native.twin_count_wiener_bits(win, p0, p1, p2, p3);
            }
            await Assert.That(AomPickRst.CountWienerBits(win, a, b)).IsEqualTo(theirs);
        }
    }

    private static AomWienerInfo RandomWiener(Random rng)
    {
        var w = new AomWienerInfo();
        for (int d = 0; d < 2; d++)
        {
            ref AomTaps8 f = ref d == 0 ? ref w.V : ref w.H;
            f[0] = (short)rng.Next(AomRestoration.WienerFiltTap0Minv, AomRestoration.WienerFiltTap0Maxv + 1);
            f[1] = (short)rng.Next(AomRestoration.WienerFiltTap1Minv, AomRestoration.WienerFiltTap1Maxv + 1);
            f[2] = (short)rng.Next(AomRestoration.WienerFiltTap2Minv, AomRestoration.WienerFiltTap2Maxv + 1);
        }
        return w;
    }

    [Test]
    public async Task SearchSelfguided_Random()
    {
        if (!Available) return;
        var rng = new Random(12);
        var sc = new AomRestoration.SgrScratch();
        var f0 = new int[AomRestoration.RestorationUnitPelsMax];
        var f1 = new int[AomRestoration.RestorationUnitPelsMax];
        for (int iter = 0; iter < 60; iter++)
        {
            int w = rng.Next(8, 200), h = rng.Next(8, 200), pruning = rng.Next(3);
            int pu = rng.Next(2) == 0 ? 64 : 32;
            var dat = new AomYv12Plane(w, h, w, h, 8);
            var src = new AomYv12Plane(w, h, w, h, 8);
            FillPlane(rng, src, rng.Next(0, 30));
            // dat = src + noise (a "reconstruction")
            for (int i = 0; i < dat.Buf.Length; i++) dat.Buf[i] = (byte)Math.Clamp(src.Buf[i] + rng.Next(-12, 13), 0, 255);
            var ours = AomPickRst.SearchSelfguided(dat.Buf, dat.Origin, w, h, dat.Stride, src.Buf, src.Origin, src.Stride,
                pu, pu, f0, f1, pruning, sc);
            var t = new int[3];
            unsafe
            {
                fixed (byte* d = dat.Buf)
                fixed (byte* s = src.Buf)
                fixed (int* o = t)
                    Native.twin_search_sgr(d + dat.Origin, w, h, dat.Stride, s + src.Origin, src.Stride, pu, pu, pruning, o);
            }
            await Assert.That(ours.Ep).IsEqualTo(t[0]);
            await Assert.That(ours.Xqd0).IsEqualTo(t[1]);
            await Assert.That(ours.Xqd1).IsEqualTo(t[2]);
        }
    }
}
