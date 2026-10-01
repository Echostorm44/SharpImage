using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twins of the search kernels ported for speed (forward transform, quantizers, distortion sums) against libaom 3.14.1's
// RTCD-dispatched kernels through aomtwin_sp.dll (scratchpad aomtwin_sp/: twin_sp.c, build.sh). Point
// SHARPIMAGE_AOMTWIN_SP at aomtwin_sp.dll; without it the tests fail. SHARPIMAGE_AOMTWIN_SP_BENCH=1 also prints ns per
// call of both sides on the same inputs.
[NotInParallel]
public sealed class AomSearchPerfTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_SP");
    private static readonly bool Bench = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_SP_BENCH") == "1";
    private static bool loaded;

    private static void Load()
    {
        if (loaded) return;
        if (DllPath == null) throw new InvalidOperationException("SHARPIMAGE_AOMTWIN_SP is not set (path of aomtwin_sp.dll)");
        if (!File.Exists(DllPath)) throw new FileNotFoundException("SHARPIMAGE_AOMTWIN_SP is set but the DLL does not exist", DllPath);
        NativeLibrary.SetDllImportResolver(typeof(AomSearchPerfTwinTests).Assembly, (name, _, _) =>
            name == "aomtwin_sp" ? NativeLibrary.Load(DllPath) : IntPtr.Zero);
        Native.twin_init();
        loaded = true;
    }

    internal static unsafe class Native
    {
        private const string D = "aomtwin_sp";
        [DllImport(D)] public static extern void twin_init();
        [DllImport(D)] public static extern double twin_fwd(short* diff, int stride, int* coeff, int txSize, int txType, int iters);
        [DllImport(D)] public static extern double twin_quant_fp(int* coeff, int n, short* zbin, short* round, short* quant, short* qshift,
            int* qcoeff, int* dqcoeff, short* dequant, ushort* eob, short* scan, short* iscan, int logScale, int iters);
        [DllImport(D)] public static extern double twin_sum_squares_2d_i16(short* src, int stride, int w, int h, ulong* result, int iters);
        [DllImport(D)] public static extern double twin_txb_init_levels(int* coeff, int w, int h, byte* levels, int iters);
        [DllImport(D)] public static extern double twin_satd(int* coeff, int n, int* result, int iters);
        [DllImport(D)] public static extern double twin_cnn_partition_bench(byte* src, int stride, float* buf, int iters);
        [DllImport(D)] public static extern double twin_optimize_txb_file(int* costs, int* rec, int nrec, int* work, long* rateSum, long* eobSum);
        [DllImport(D)] public static extern double twin_block_error(int* coeff, int* dqcoeff, int n, long* sse, long* result, int iters);
    }

    private static readonly string[] SizeNames = { "4x4", "8x8", "16x16", "32x32", "64x64", "4x8", "8x4", "8x16", "16x8",
        "16x32", "32x16", "32x64", "64x32", "4x16", "16x4", "8x32", "32x8", "16x64", "64x16" };

    private static int[] TypesFor(int txSize)
    {
        int m = Math.Max(TxSizeWide[txSize], TxSizeHigh[txSize]);
        return m <= 16 ? Enumerable.Range(0, 16).ToArray() : m == 32 ? new[] { 0, 9 } : new[] { 0 };
    }

    private static short[] Residual(Random rng, int stride, int h, int mode) => Enumerable.Range(0, stride * h).Select(i => mode switch
    {
        0 => (short)rng.Next(-255, 256),
        1 => (short)(rng.Next(2) == 0 ? -255 : 255),
        2 => (short)255,
        3 => (short)-255,
        _ => (short)rng.Next(-20, 21),
    }).ToArray();

    [Test]
    public async Task FwdTxfm_AllSizesTypes_MatchLibaom()
    {
        Load();
        var rng = new Random(77);
        int cases = 0, bad = 0;
        string first = "";
        for (int txSize = 0; txSize < 19; txSize++)
        {
            int w = TxSizeWide[txSize], h = TxSizeHigh[txSize];
            int n = Math.Min(w, 32) * Math.Min(h, 32);
            foreach (int txType in TypesFor(txSize))
            {
                AomEncodeMb.TxTypeKinds(txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr);
                for (int trial = 0; trial < 25; trial++)
                {
                    int stride = w + 8 * (trial % 3);
                    var diff = Residual(rng, stride, h, trial % 5);
                    var a = new int[64 * 64]; var b = new int[64 * 64];
                    unsafe { fixed (short* d = diff) fixed (int* pa = a) Native.twin_fwd(d, stride, pa, txSize, txType, 0); }
                    Av1FwdTxfmAom.ForwardRaw(diff, stride, w, h, txSize, hKind, vKind, flipUd, flipLr, b);
                    cases++;
                    if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n)) && bad++ == 0) first = $"{SizeNames[txSize]} type {txType} trial {trial}";
                }
            }
        }
        Console.WriteLine($"fwd txfm: {cases} cases, {bad} mismatches {first}");
        await Assert.That(bad).IsEqualTo(0);
        if (Bench) { BenchFwd(false); Thread.Sleep(600); BenchFwd(false); Thread.Sleep(600); BenchFwd(true); }
    }

    private static unsafe void BenchFwd(bool print)
    {
        var rng = new Random(5);
        if (print) Console.WriteLine($"{"size",-6} {"type",4} {"ours ns",8} {"aom ns",8} {"ratio",6}");
        var only = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_SP_SIZES")?.Split(',');
        for (int txSize = 0; txSize < 19; txSize++)
        {
            if (only != null && !only.Contains(SizeNames[txSize])) continue;
            int w = TxSizeWide[txSize], h = TxSizeHigh[txSize];
            int iters = print ? Math.Max(2000, 2_000_000 / (w * h)) : 50;
            foreach (int txType in new[] { 0, 1, 9, 4 })
            {
                if (!TypesFor(txSize).Contains(txType)) continue;
                AomEncodeMb.TxTypeKinds(txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr);
                var diff = Residual(rng, w, h, 0);
                var a = new int[64 * 64]; var b = new int[64 * 64];
                double best = double.MaxValue, bestA = double.MaxValue;
                for (int rep = 0; rep < (print ? 15 : 1); rep++)
                {
                    fixed (short* d = diff) fixed (int* pa = a) bestA = Math.Min(bestA, Native.twin_fwd(d, w, pa, txSize, txType, iters));
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < iters; i++) Av1FwdTxfmAom.ForwardRaw(diff, w, w, h, txSize, hKind, vKind, flipUd, flipLr, b);
                    best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalNanoseconds);
                }
                if (print) Console.WriteLine($"{SizeNames[txSize],-6} {txType,4} {best / iters,8:F1} {bestA / iters,8:F1} {best / bestA,6:F2}");
            }
        }
    }

    // CoeffCosts in libaom's struct layout (coeff_costs[5][2] then eob_costs[7][2])
    private static int[] Flatten(AomCoeffCosts c)
    {
        var l = new List<int>();
        foreach (var k in c.Coeff) { l.AddRange(k.TxbSkip); l.AddRange(k.BaseEob); l.AddRange(k.Base); l.AddRange(k.EobExtra); l.AddRange(k.DcSign); l.AddRange(k.Lps); }
        foreach (var e in c.Eob) l.AddRange(e);
        return l.ToArray();
    }

    /// <summary>The trellis (av1_optimize_txb) over captured search blocks (SHARPIMAGE_AOMTWIN_SP_TXB: records of tx, type,
    /// plane, skip ctx, dc ctx, eob, dq0, dq1, rdmult, sharpness, n, coeff, qcoeff, dqcoeff as int32), with the default
    /// coefficient costs of qindex 120: same eobs / rates as libaom, and both sides timed.</summary>
    [Test]
    public async Task OptimizeTxb_Captured_MatchLibaom()
    {
        Load();
        string? path = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_SP_TXB");
        if (path == null || !File.Exists(path)) return;
        var raw = File.ReadAllBytes(path);
        var rec = new int[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, rec, 0, rec.Length * 4);
        int nrec = 0;
        for (int o = 0; o < rec.Length; o += 11 + 3 * rec[o + 10]) nrec++;
        var fc = new Av1CdfCoefContext();
        Av1CdfDefaults.InitializeCoef(fc, 2);
        var costs = new AomCoeffCosts();
        costs.Fill(fc, 3);
        var flat = Flatten(costs);
        var work = new int[rec.Length];
        long rateA = 0, eobA = 0, rateB = 0, eobB = 0;
        double bestA = double.MaxValue, bestB = double.MaxValue;
        for (int rep = 0; rep < (Bench ? 12 : 1); rep++)
        {
            Array.Copy(rec, work, rec.Length);
            unsafe { fixed (int* c = flat) fixed (int* r = rec) fixed (int* w = work) bestA = Math.Min(bestA, Native.twin_optimize_txb_file(c, r, nrec, w, &rateA, &eobA)); }
            Array.Copy(rec, work, rec.Length);
            rateB = eobB = 0;
            long t0 = Stopwatch.GetTimestamp();
            for (int o = 0; o < rec.Length; o += 11 + 3 * rec[o + 10])
            {
                int n = rec[o + 10];
                eobB += AomTxb.OptimizeTxb(costs, rec[o], rec[o + 1], rec[o + 2] == 0 ? 0 : 1, false,
                    new AomTxbCtx { TxbSkipCtx = rec[o + 3], DcSignCtx = rec[o + 4] }, rec.AsSpan(o + 11, n), work.AsSpan(o + 11 + n, n),
                    work.AsSpan(o + 11 + 2 * n, n), rec[o + 5], (short)rec[o + 6], (short)rec[o + 7], rec[o + 8], 8, rec[o + 9], false, false, 0,
                    AomEncodeMb.ScanOf(rec[o], rec[o + 1]), out int rate);
                rateB += rate;
            }
            bestB = Math.Min(bestB, Stopwatch.GetElapsedTime(t0).TotalNanoseconds);
        }
        Console.WriteLine($"optimize_txb: {nrec} blocks, ours {bestB / nrec:F1} ns aom {bestA / nrec:F1} ns ratio {bestB / bestA:F2}");
        await Assert.That($"{eobB} {rateB}").IsEqualTo($"{eobA} {rateA}");
    }

    /// <summary>Timing only (SHARPIMAGE_AOMTWIN_SP_BENCH=1): the partition CNN on a 65x65 noise block.</summary>
    [Test]
    public async Task CnnPartition_Bench()
    {
        if (!Bench) return;
        var rng = new Random(9);
        var src = new byte[80 * 70];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(128 + rng.Next(-40, 41));
        var buf = new float[AomMl.CnnOutBufSize];
        for (int i = 0; i < 300; i++) AomMl.CnnPartitionPredict(src, 80, buf);
        Thread.Sleep(300);
        double best = double.MaxValue;
        for (int rep = 0; rep < 7; rep++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < (Environment.GetEnvironmentVariable("CNN_LONG") != null ? 20000 : 200); i++) AomMl.CnnPartitionPredict(src, 80, buf);
            best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMicroseconds / 200);
        }
        Load();
        var theirs = new float[AomMl.CnnOutBufSize];
        double bestA = double.MaxValue;
        for (int rep = 0; rep < 7; rep++)
            unsafe { fixed (byte* s = src) fixed (float* b = theirs) bestA = Math.Min(bestA, Native.twin_cnn_partition_bench(s, 80, b, 200) / 1000 / 200); }
        Console.WriteLine($"cnn partition predict: ours {best:F1} us aom {bestA:F1} us");
        await Assert.That(buf.SequenceEqual(theirs)).IsTrue();
    }

    /// <summary>The vector QM quantizers against the scalar ports of quantize_fp_helper_c / aom_quantize_b_helper_c: every
    /// tx size, QM level, plane and 2D type, random and boundary coefficients, both log scales.</summary>
    [Test]
    public async Task QmQuantizers_Vector_MatchScalar()
    {
        var rng = new Random(31);
        var quants = new AomQuants(8, 0, 0, 0, 0, 0, 0);
        int cases = 0, bad = 0;
        for (int txSize = 0; txSize < 19; txSize++)
        {
            int n = AomEncodeMb.MaxEob(txSize);
            for (int level = 0; level < 15; level += 2)
                for (int plane = 0; plane < 3; plane++)
                    foreach (int txType in new[] { 0, 1, 2, 3 })
                    {
                        if (Math.Max(TxSizeWide[txSize], TxSizeHigh[txSize]) > 16 && txType != 0) continue;
                        var qm = AomQm.Qmatrix(level, plane, txSize, txType); var iqm = AomQm.Iqmatrix(level, plane, txSize, txType);
                        if (qm == null || iqm == null) continue;
                        var scan = AomEncodeMb.ScanOf(txSize, txType); var iscan = AomEncodeMb.IScanOf(txSize, txType);
                        int logScale = AomQuantize.TxScale(txSize);
                        for (int trial = 0; trial < 6; trial++)
                        {
                            int q = rng.Next(1, 256);
                            int amp = trial % 3 == 0 ? 40 : trial % 3 == 1 ? 2000 : 1 << 18;
                            var c = new int[n];
                            for (int i = 0; i < n; i++) c[i] = rng.Next(4) == 0 ? 0 : rng.Next(-amp, amp + 1);
                            var a1 = new int[n]; var a2 = new int[n]; var b1 = new int[n]; var b2 = new int[n];
                            int e1 = AomQm.QuantizeFpHelper(c, n, scan, quants.RoundFp[0, q, 0], quants.RoundFp[0, q, 1], quants.QuantFp[0, q, 0], quants.QuantFp[0, q, 1],
                                quants.Dequant[0, q, 0], quants.Dequant[0, q, 1], qm, iqm, logScale, a1, b1);
                            int e2 = AomQm.QuantizeFpHelperAvx2(c, n, iscan, quants.RoundFp[0, q, 0], quants.RoundFp[0, q, 1], quants.QuantFp[0, q, 0], quants.QuantFp[0, q, 1],
                                quants.Dequant[0, q, 0], quants.Dequant[0, q, 1], qm, iqm, logScale, a2, b2);
                            cases++;
                            if (e1 != e2 || !a1.AsSpan().SequenceEqual(a2) || !b1.AsSpan().SequenceEqual(b2)) bad++;
                            int f1 = AomQm.QuantizeBHelper(c, n, scan, quants.Zbin[0, q, 0], quants.Zbin[0, q, 1], quants.Round[0, q, 0], quants.Round[0, q, 1],
                                quants.Quant[0, q, 0], quants.Quant[0, q, 1], quants.QuantShift[0, q, 0], quants.QuantShift[0, q, 1], quants.Dequant[0, q, 0], quants.Dequant[0, q, 1],
                                qm, iqm, logScale, a1, b1);
                            int f2 = AomQm.QuantizeBHelperAvx2(c, n, iscan, quants.Zbin[0, q, 0], quants.Zbin[0, q, 1], quants.Round[0, q, 0], quants.Round[0, q, 1],
                                quants.Quant[0, q, 0], quants.Quant[0, q, 1], quants.QuantShift[0, q, 0], quants.QuantShift[0, q, 1], quants.Dequant[0, q, 0], quants.Dequant[0, q, 1],
                                qm, iqm, logScale, a2, b2);
                            cases++;
                            if (f1 != f2 || !a1.AsSpan().SequenceEqual(a2) || !b1.AsSpan().SequenceEqual(b2)) bad++;
                        }
                    }
        }
        Console.WriteLine($"qm quantizers: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }
    [Test]
    public async Task HbdQuantizers_Vector_MatchScalar()
    {
        var rng = new Random(37);
        int cases = 0, bad = 0;
        foreach (int bd in new[] { 10, 12 })
        {
            var quants = new AomQuants(bd, 0, 0, 0, 0, 0, 0);
            for (int txSize = 0; txSize < 19; txSize++)
            {
                int n = AomEncodeMb.MaxEob(txSize);
                var iscan = AomEncodeMb.IScanOf(txSize, 0);
                int logScale = AomQuantize.TxScale(txSize);
                for (int trial = 0; trial < 40; trial++)
                {
                    int q = rng.Next(0, 256);
                    int amp = trial % 4 == 0 ? 40 : trial % 4 == 1 ? 2000 : trial % 4 == 2 ? 1 << 16 : 1 << (bd + 8);
                    var c = new int[n];
                    for (int i = 0; i < n; i++) c[i] = rng.Next(4) == 0 ? 0 : rng.Next(-amp, amp + 1);
                    var a1 = new int[n]; var a2 = new int[n]; var b1 = new int[n]; var b2 = new int[n];
                    int e1 = AomQuantizeHbd.QuantizeFpScalar(c, n, iscan, quants.RoundFp[0, q, 0], quants.RoundFp[0, q, 1], quants.QuantFp[0, q, 0], quants.QuantFp[0, q, 1],
                        quants.Dequant[0, q, 0], quants.Dequant[0, q, 1], logScale, a1, b1);
                    int e2 = AomQuantizeHbdSimd.QuantizeFp(c, n, iscan, quants.RoundFp[0, q, 0], quants.RoundFp[0, q, 1], quants.QuantFp[0, q, 0], quants.QuantFp[0, q, 1],
                        quants.Dequant[0, q, 0], quants.Dequant[0, q, 1], logScale, a2, b2);
                    cases++;
                    if (e1 != e2 || !a1.AsSpan().SequenceEqual(a2) || !b1.AsSpan().SequenceEqual(b2)) bad++;
                    int f1 = AomQuantizeHbd.QuantizeBScalar(c, n, iscan, quants.Zbin[0, q, 0], quants.Zbin[0, q, 1], quants.Round[0, q, 0], quants.Round[0, q, 1],
                        quants.Quant[0, q, 0], quants.Quant[0, q, 1], quants.QuantShift[0, q, 0], quants.QuantShift[0, q, 1], quants.Dequant[0, q, 0], quants.Dequant[0, q, 1],
                        logScale, a1, b1);
                    int f2 = AomQuantizeHbdSimd.QuantizeB(c, n, iscan, quants.Zbin[0, q, 0], quants.Zbin[0, q, 1], quants.Round[0, q, 0], quants.Round[0, q, 1],
                        quants.Quant[0, q, 0], quants.Quant[0, q, 1], quants.QuantShift[0, q, 0], quants.QuantShift[0, q, 1], quants.Dequant[0, q, 0], quants.Dequant[0, q, 1],
                        logScale, a2, b2);
                    cases++;
                    if (f1 != f2 || !a1.AsSpan().SequenceEqual(a2) || !b1.AsSpan().SequenceEqual(b2)) bad++;
                }
            }
        }
        Console.WriteLine($"hbd quantizers: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }
    [Test]
    public async Task HbdComputeStats_Vector_MatchScalar()
    {
        var rng = new Random(41);
        int cases = 0, bad = 0;
        foreach (int bd in new[] { 10, 12 })
            foreach (int win in new[] { 7, 5 })
                for (int trial = 0; trial < 6; trial++)
                {
                    var dgd = new AomYv12Plane(96, 80, 96, 80, 16, true); var src = new AomYv12Plane(96, 80, 96, 80, 16, true);
                    int max = (1 << bd) - 1;
                    for (int i = 0; i < dgd.Buf16.Length; i++) dgd.Buf16[i] = (ushort)(trial % 2 == 0 ? rng.Next(max + 1) : (rng.Next(2) == 0 ? 0 : max));
                    for (int i = 0; i < src.Buf16.Length; i++) src.Buf16[i] = (ushort)rng.Next(max + 1);
                    int hs = rng.Next(0, 8), he = hs + rng.Next(1, 88 - hs), vs = rng.Next(0, 8), ve = vs + rng.Next(1, 72 - vs);
                    int n = win * win;
                    long[] m1 = new long[n], h1 = new long[n * n], m2 = new long[n], h2 = new long[n * n];
                    AomPickRst.ComputeStatsHbdScalar(win, dgd, src, hs, he, vs, ve, m1, h1, bd);
                    AomPickRst.ComputeStatsHbdAvx2(win, dgd, src, hs, he, vs, ve, m2, h2, bd);
                    cases++;
                    if (!m1.AsSpan().SequenceEqual(m2) || !h1.AsSpan().SequenceEqual(h2)) bad++;
                }
        Console.WriteLine($"hbd compute stats: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }
    [Test]
    public async Task HbdDsp_Vector_MatchScalar()
    {
        var rng = new Random(43);
        int bad = 0, cases = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            int bd = trial % 2 == 0 ? 10 : 12, max = (1 << bd) - 1;
            int w = 4 * rng.Next(1, 33), h = rng.Next(1, 65), stride = 136;
            var a = new ushort[stride * 66]; var b = new ushort[stride * 66];
            for (int k = 0; k < a.Length; k++) { a[k] = (ushort)(trial % 3 == 0 ? (rng.Next(2) * max) : rng.Next(max + 1)); b[k] = (ushort)(trial % 3 == 0 ? max - a[k] : rng.Next(max + 1)); }
            int off = rng.Next(0, 4);
            cases++;
            if (AomHbd.Sse(a, off, stride, b, off + 1, stride, w, h) != AomHbd.SseScalar(a, off, stride, b, off + 1, stride, w, h)) bad++;
            // subtract
            var d1 = new short[64 * 64];
            AomHbd.SubtractBlock(Math.Min(h, 64), Math.Min(w, 64), d1, 0, 64, a, off, stride, b, off, stride);
            for (int r = 0; r < Math.Min(h, 64); r++)
                for (int c = 0; c < Math.Min(w, 64); c++)
                    if (d1[r * 64 + c] != (short)(a[off + r * stride + c] - b[off + r * stride + c])) { bad++; r = 99; break; }
            // block error
            int n = 16 << rng.Next(0, 7);
            var co = new int[n]; var dq = new int[n];
            for (int k = 0; k < n; k++) { co[k] = rng.Next(-(1 << 22), 1 << 22); dq[k] = co[k] + rng.Next(-(1 << 20), 1 << 20); }
            long e = AomHbd.BlockError(co, dq, n, out long ssz, bd);
            long re = 0, rs = 0;
            for (int k = 0; k < n; k++) { long df = co[k] - dq[k]; re += df * df; rs += (long)co[k] * co[k]; }
            int sh = 2 * (bd - 8), rnd = (1 << sh) >> 1;
            cases++;
            if (e != (re + rnd) >> sh || ssz != (rs + rnd) >> sh) bad++;
            // hadamard 8x8: the same coefficients up to the order
            var diff = new short[8 * 72];
            for (int k = 0; k < diff.Length; k++) diff[k] = (short)(trial % 3 == 0 ? (rng.Next(2) == 0 ? -max : max) : rng.Next(-max, max + 1));
            var h1 = new int[64]; var h2 = new int[64];
            AomHbd.Hadamard8x8(diff, 72, h1); AomHbd.Hadamard8x8Scalar(diff, 72, h2);
            for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++) if (h1[c * 8 + r] != h2[r * 8 + c]) { bad++; r = 9; break; }
            cases++;
        }
        Console.WriteLine($"hbd dsp: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }
}
