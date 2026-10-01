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
}
