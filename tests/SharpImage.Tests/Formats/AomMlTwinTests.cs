using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Twins of the libaom ML port (src/SharpImage/Formats/Av1/Aom/AomMl*.cs, AomMlModels.cs) against libaom 3.14.1's own
// functions through aomtwin_ml.dll (built against libaom.a, run-time dispatch initialised, so the AVX2 / SSE3 kernels
// the encoder uses are the ones compared; see scratchpad aomtwin_ml/). Every float is compared bit for bit.
// Opt-in: point SHARPIMAGE_AOMTWIN_ML at aomtwin_ml.dll; without it the tests pass without checking.
// SHARPIMAGE_AOMTWIN_ML_EXHAUSTIVE=1 also runs expf over every float in the softmax's [-10, 0] domain.
[NotInParallel]
public sealed class AomMlTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_ML");
    private static readonly bool Available = DllPath != null && (File.Exists(DllPath)
        ? Load() : throw new FileNotFoundException("SHARPIMAGE_AOMTWIN_ML is set but the DLL does not exist", DllPath));

    private static bool Load()
    {
        NativeLibrary.SetDllImportResolver(typeof(AomMlTwinTests).Assembly, (name, _, _) =>
            name == "aomtwin_ml" ? NativeLibrary.Load(DllPath!) : IntPtr.Zero);
        Native.twin_init();
        return true;
    }

    private static class Native
    {
        private const string L = "aomtwin_ml";
        [DllImport(L)] public static extern void twin_init();
        [DllImport(L)] public static extern int twin_num_cfgs();
        [DllImport(L)] public static extern void twin_cfg_info(int id, [Out] int[] info);
        [DllImport(L)] public static extern void twin_cfg_layer(int id, int layer, [Out] float[] w, [Out] float[] b);
        [DllImport(L)] public static extern void twin_nn_predict(int id, float[] input, int reducePrec, [Out] float[] output);
        [DllImport(L)] public static extern void twin_nn_predict_custom(int numInputs, int numOutputs, int numHidden, int[] nodes,
            float[] w, float[] b, float[] input, int reducePrec, [Out] float[] output);
        [DllImport(L)] public static extern void twin_nn_softmax(float[] input, [Out] float[] output, int n);
        [DllImport(L)] public static extern void twin_nn_fast_softmax_16(float[] input, [Out] float[] output);
        [DllImport(L)] public static extern void twin_nn_output_prec_reduce([In, Out] float[] output, int n);
        [DllImport(L)] public static extern void twin_expf_bits(uint start, uint count, [Out] float[] output);
        [DllImport(L)] public static extern void twin_log1pf_many(float[] input, [Out] float[] output, int n);
        [DllImport(L)] public static extern int twin_fpcw();
        [DllImport(L)] public static extern void twin_set_fpcw_target(int cw);
        [DllImport(L)] public static extern void twin_log1pf_bits(uint start, uint step, uint count, [Out] float[] output);
        [DllImport(L)] public static extern void twin_horver(short[] diff, int stride, int w, int h, [Out] float[] hv);
        [DllImport(L)] public static extern void twin_blk_sse_sum(short[] data, int stride, int bw, int bh, out int xSum, out long x2Sum);
        [DllImport(L)] public static extern int twin_dc_q(int qindex, int bd);
        [DllImport(L)] public static extern void twin_energy_finer(short[] diff, int stride, int bw, int bh, [Out] float[] hor, [Out] float[] ver);
        [DllImport(L)] public static extern int twin_mean_dev(short[] data, int stride, int bw, int bh, [Out] float[] features);
        [DllImport(L)] public static extern void twin_prune_tx_2d(short[] srcDiff, int bsize, int txSize, int blkRow, int blkCol,
            int txSetType, int pruneMode, [Out] int[] txkMap, ref int allowedMask);
        [DllImport(L)] public static extern int twin_tx_split(short[] srcDiff, int bsize, int blkRow, int blkCol, int txSize);
        [DllImport(L)] public static extern int twin_intra_tx_depth(short[] srcDiff, int blkRow, int blkCol, int bsize, int txSize,
            int lossless, int bd, uint sourceVariance, int qindex);
        [DllImport(L)] public static extern uint twin_perpixel_variance(byte[] pix, int stride, int bsize, int bd, int hbd);
        [DllImport(L, EntryPoint = "twin_perpixel_variance")] public static extern uint twin_perpixel_variance16(ushort[] pix, int stride, int bsize, int bd, int hbd);
        [DllImport(L)] public static extern void twin_prune_4_partition(byte[] pix, int stride, int bsize, int bd, int hbd, int partCtx,
            long bestRd, long[] rd, uint pbSourceVariance, int frameW, int frameH, int levelIndex, [In, Out] int[] part4Allowed);
        [DllImport(L, EntryPoint = "twin_prune_4_partition")] public static extern void twin_prune_4_partition16(ushort[] pix, int stride,
            int bsize, int bd, int hbd, int partCtx, long bestRd, long[] rd, uint pbSourceVariance, int frameW, int frameH,
            int levelIndex, [In, Out] int[] part4Allowed);
        [DllImport(L)] public static extern void twin_prune_ab_partition(int bsize, int partCtx, int varCtx, long bestRd, long[] rd,
            [In, Out] int[] ab);
        [DllImport(L)] public static extern void twin_prune_rect_partition(byte[] pix, int stride, int bsize, int bd, int hbd,
            long bestRd, long noneRd, long[] splitRd, [In, Out] int[] prune);
        [DllImport(L, EntryPoint = "twin_prune_rect_partition")] public static extern void twin_prune_rect_partition16(ushort[] pix,
            int stride, int bsize, int bd, int hbd, long bestRd, long noneRd, long[] splitRd, [In, Out] int[] prune);
        [DllImport(L)] public static extern void twin_collect_hog(byte[] src, int stride, int bsize, int mbToRightEdge, int mbToBottomEdge,
            int ssX, int ssY, int plane, int hbd, [Out] float[] hog);
        [DllImport(L, EntryPoint = "twin_collect_hog")] public static extern void twin_collect_hog16(ushort[] src, int stride, int bsize,
            int mbToRightEdge, int mbToBottomEdge, int ssX, int ssY, int plane, int hbd, [Out] float[] hog);
        [DllImport(L)] public static extern void twin_collect_hog_cached(byte[] sbSrc, int stride, int sbSize, int miRow, int miCol,
            int bsize, int mbToRightEdge, int mbToBottomEdge, int ssX, int ssY, int plane, int hbd, [Out] float[] hog);
        [DllImport(L, EntryPoint = "twin_collect_hog_cached")] public static extern void twin_collect_hog_cached16(ushort[] sbSrc, int stride,
            int sbSize, int miRow, int miCol, int bsize, int mbToRightEdge, int mbToBottomEdge, int ssX, int ssY, int plane, int hbd,
            [Out] float[] hog);
        [DllImport(L)] public static extern void twin_prune_hog(byte[] src, int stride, int bsize, int mbToRightEdge, int mbToBottomEdge,
            int ssX, int ssY, int isChroma, int hbd, float th, [In, Out] byte[] mask);
        [DllImport(L, EntryPoint = "twin_prune_hog")] public static extern void twin_prune_hog16(ushort[] src, int stride, int bsize,
            int mbToRightEdge, int mbToBottomEdge, int ssX, int ssY, int isChroma, int hbd, float th, [In, Out] byte[] mask);
        [DllImport(L)] public static extern int twin_early_term_after_split(int bsize, int frameW, int frameH, int level, int qindex,
            int bd, long bestRd, long partNoneRd, long partSplitRd, long[] splitBlockRd, int[] children, uint[] sms);
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    // the first mismatching element of two float arrays, bit-exact
    private static string? DiffFloats(string what, ReadOnlySpan<float> ours, ReadOnlySpan<float> theirs, int n)
    {
        for (int i = 0; i < n; i++)
            if (Bits(ours[i]) != Bits(theirs[i]))
                return $"{what} [{i}]: ours {ours[i]:R} ({Bits(ours[i]):X8}) libaom {theirs[i]:R} ({Bits(theirs[i]):X8})";
        return null;
    }

    // a feature value: mixed signs and magnitudes, with some exact zeros and small integers
    private static float RandomFeature(Random rng) => rng.Next(12) switch
    {
        0 => 0f,
        1 => rng.Next(-8, 9),
        _ => (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.NextDouble() * 5 - 3)),
    };

    private static float[] RandomFeatures(Random rng, int n)
    {
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = RandomFeature(rng);
        return f;
    }

    private static short[] RandomResidual(Random rng, int n)
    {
        int kind = rng.Next(6);
        int range = kind switch { 0 => 4, 1 => 32, 2 => 255, 3 => 1023, 4 => 4095, _ => 32767 };
        var d = new short[n];
        if (rng.Next(20) == 0) return d;   // all zero
        for (int i = 0; i < n; i++) d[i] = (short)rng.Next(-range, range + 1);
        return d;
    }

    // a smooth-ish residual (the correlation / energy features see realistic structure too)
    private static short[] SmoothResidual(Random rng, int w, int h, int stride)
    {
        var d = new short[stride * h];
        double a = rng.NextDouble() * 60 - 30, bx = rng.NextDouble() * 8 - 4, by = rng.NextDouble() * 8 - 4;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                d[y * stride + x] = (short)Math.Clamp(Math.Round(a + bx * x + by * y + (rng.NextDouble() - 0.5) * 6), -255, 255);
        return d;
    }

    [Test]
    public async Task Models_MatchLibaomBitExactly()
    {
        if (!Available) return;
        int n = Native.twin_num_cfgs();
        await Assert.That(AomMlModels.All.Length).IsEqualTo(n);
        string? bad = null;
        var info = new int[13];
        for (int id = 0; id < n && bad == null; id++)
        {
            var c = AomMlModels.All[id];
            Native.twin_cfg_info(id, info);
            if (info[0] != c.NumInputs || info[1] != c.NumOutputs || info[2] != c.NumHiddenLayers ||
                !info.AsSpan(3, c.NumHiddenLayers).SequenceEqual(c.NumHiddenNodes))
            { bad = $"{c.Name}: shape"; break; }
            int nin = c.NumInputs;
            for (int l = 0; l <= c.NumHiddenLayers && bad == null; l++)
            {
                int nout = l == c.NumHiddenLayers ? c.NumOutputs : c.NumHiddenNodes[l];
                var w = new float[nin * nout];
                var b = new float[nout];
                Native.twin_cfg_layer(id, l, w, b);
                if (c.Weights[l].Length != w.Length || c.Bias[l].Length != b.Length) bad = $"{c.Name} layer {l}: sizes";
                else bad = DiffFloats($"{c.Name} layer {l} weights", c.Weights[l], w, w.Length)
                    ?? DiffFloats($"{c.Name} layer {l} bias", c.Bias[l], b, b.Length);
                nin = nout;
            }
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task NnPredict_AllModels_RandomFeatures()
    {
        if (!Available) return;
        var rng = new Random(11);
        string? bad = null;
        for (int id = 0; id < AomMlModels.All.Length && bad == null; id++)
        {
            var c = AomMlModels.All[id];
            var ours = new float[c.NumOutputs];
            var theirs = new float[c.NumOutputs];
            for (int it = 0; it < 3000 && bad == null; it++)
            {
                var f = RandomFeatures(rng, c.NumInputs);
                int rp = it & 1;
                AomMl.NnPredict(f, c, rp != 0, ours);
                Native.twin_nn_predict(id, f, rp, theirs);
                bad = DiffFloats($"{c.Name} it {it} reduce {rp}", ours, theirs, c.NumOutputs);
            }
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task NnPredict_EveryLayerShape()
    {
        if (!Available) return;
        var rng = new Random(12);
        string? bad = null;
        for (int it = 0; it < 20000 && bad == null; it++)
        {
            int nh = rng.Next(4);
            int nin = rng.Next(1, 49);
            int nout = rng.Next(1, 49);
            var nodes = new int[Math.Max(nh, 1)];
            for (int l = 0; l < nh; l++) nodes[l] = rng.Next(1, 49);
            var ws = new List<float[]>();
            var bs = new List<float[]>();
            int a = nin;
            for (int l = 0; l <= nh; l++)
            {
                int o = l == nh ? nout : nodes[l];
                ws.Add(RandomFeatures(rng, a * o));
                bs.Add(RandomFeatures(rng, o));
                a = o;
            }
            var cfg = new AomNnConfig("custom", nin, nout, nodes[..nh], ws.ToArray(), bs.ToArray());
            var f = RandomFeatures(rng, nin);
            var ours = new float[nout];
            var theirs = new float[nout];
            int rp = rng.Next(2);
            AomMl.NnPredict(f, cfg, rp != 0, ours);
            Native.twin_nn_predict_custom(nin, nout, nh, nodes, ws.SelectMany(x => x).ToArray(), bs.SelectMany(x => x).ToArray(), f, rp, theirs);
            bad = DiffFloats($"shape {nin}-[{string.Join(",", nodes[..nh])}]-{nout}", ours, theirs, nout);
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task NnOutputPrecReduce_Random()
    {
        if (!Available) return;
        var rng = new Random(13);
        var v = new float[1000];
        for (int i = 0; i < v.Length; i++)
            v[i] = rng.Next(10) == 0 ? BitConverter.Int32BitsToSingle(rng.Next()) * (rng.Next(2) == 0 ? 1 : -1)
                : (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.NextDouble() * 8 - 4));
        var ours = (float[])v.Clone();
        var theirs = (float[])v.Clone();
        AomMl.NnOutputPrecReduce(ours, ours.Length);
        Native.twin_nn_output_prec_reduce(theirs, theirs.Length);
        await Assert.That(DiffFloats("prec reduce", ours, theirs, v.Length) ?? "").IsEqualTo("");
    }

    [Test]
    public async Task NnSoftmax_Random()
    {
        if (!Available) return;
        var rng = new Random(14);
        string? bad = null;
        for (int it = 0; it < 100000 && bad == null; it++)
        {
            int n = rng.Next(1, 17);
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.NextDouble() * 3 - 1));
            var ours = new float[n];
            var theirs = new float[n];
            AomMl.NnSoftmax(f, ours, n);
            Native.twin_nn_softmax(f, theirs, n);
            bad = DiffFloats($"softmax n {n}", ours, theirs, n);
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task NnFastSoftmax16_Random()
    {
        if (!Available) return;
        var rng = new Random(15);
        string? bad = null;
        for (int it = 0; it < 100000 && bad == null; it++)
        {
            var f = new float[16];
            for (int i = 0; i < 16; i++)
                f[i] = rng.Next(8) == 0 ? 0f : (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.NextDouble() * 3 - 2));
            var ours = new float[16];
            var theirs = new float[16];
            AomMl.NnFastSoftmax16(f, ours);
            Native.twin_nn_fast_softmax_16(f, theirs);
            bad = DiffFloats("fast softmax", ours, theirs, 16);
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    // expf on a stride through every float in the softmax's clamped domain [-10, 0] (all of it with
    // SHARPIMAGE_AOMTWIN_ML_EXHAUSTIVE=1), plus positive arguments
    [Test]
    public async Task Expf_SoftmaxDomain()
    {
        if (!Available) return;
        bool all = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_ML_EXHAUSTIVE") == "1";
        uint lo = 0x80000000u, hi = BitConverter.SingleToUInt32Bits(-10f);
        const int chunk = 1 << 20;
        var theirs = new float[chunk];
        string? bad = null;
        // exhaustive, or 64Ki consecutive floats out of every 4Mi (about 17M values)
        ulong step = all ? (ulong)chunk : 1UL << 22;
        uint count = all ? (uint)chunk : 1u << 16;
        for (ulong start = lo; start <= hi && bad == null; start += step)
        {
            uint cnt = (uint)Math.Min(count, hi - start + 1);
            Native.twin_expf_bits((uint)start, cnt, theirs);
            for (uint i = 0; i < cnt; i++)
            {
                float x = BitConverter.UInt32BitsToSingle((uint)start + i);
                if (Bits(AomMl.Expf(x)) != Bits(theirs[i])) { bad = $"expf({x:R})"; break; }
            }
        }
        // and positive arguments / the rest of the range, sparsely
        for (uint u = 0; u < 0xFFFFFFFFu - 99991u && bad == null; u += 99991u)
        {
            Native.twin_expf_bits(u, 1, theirs);
            float x = BitConverter.UInt32BitsToSingle(u);
            if (!float.IsNaN(x) && Bits(AomMl.Expf(x)) != Bits(theirs[0])) bad = $"expf({x:R})";
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    // log1pf over every integer to 2^24 (the ML code's arguments are mostly converted integers) and random floats, under
    // the x87 precision avifenc.exe (a mingw-w64 executable) runs with: 0x37F, extended, the twin's default for every
    // libm-calling export (the .NET host's own is 0x27F, double, under which x87 log1pf double-rounds).
    // SHARPIMAGE_AOMTWIN_ML_EXHAUSTIVE=1: every non-negative float.
    [Test]
    public async Task Log1pf_IntegersAndRandom()
    {
        if (!Available) return;
        const int chunk = 1 << 20;
        var input = new float[chunk];
        var theirs = new float[chunk];
        string? bad = null;
        try
        {
            foreach (int cw in new[] { 0x37F })
            {
                Native.twin_set_fpcw_target(cw);
                for (int start = 0; start < (1 << 24) && bad == null; start += chunk)
                {
                    for (int i = 0; i < chunk; i++) input[i] = start + i;
                    Native.twin_log1pf_many(input, theirs, chunk);
                    for (int i = 0; i < chunk; i++)
                        if (Bits(AomMl.Log1pf(input[i])) != Bits(theirs[i])) { bad = $"log1pf({input[i]:R}) fpcw {cw:X}"; break; }
                }
                var rng = new Random(16);
                for (int round = 0; round < 8 && bad == null; round++)
                {
                    for (int i = 0; i < chunk; i++)
                        input[i] = rng.Next(3) switch
                        {
                            0 => BitConverter.UInt32BitsToSingle((uint)rng.Next(0, 0x7f800000)),     // any non-negative float
                            1 => (float)rng.Next() / rng.Next(1, 1 << 20),
                            _ => (float)(rng.NextDouble() * 2 - 0.5),
                        };
                    Native.twin_log1pf_many(input, theirs, chunk);
                    for (int i = 0; i < chunk; i++)
                        if (Bits(AomMl.Log1pf(input[i])) != Bits(theirs[i])) { bad = $"log1pf({input[i]:R}) fpcw {cw:X}"; break; }
                }
            }
            Native.twin_set_fpcw_target(0x37F);
            if (Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_ML_EXHAUSTIVE") == "1")
                for (ulong start = 0; start < 0x7F800000UL && bad == null; start += chunk)
                {
                    uint cnt = (uint)Math.Min(chunk, 0x7F800000UL - start);
                    Native.twin_log1pf_bits((uint)start, 1, cnt, theirs);
                    for (uint i = 0; i < cnt; i++)
                    {
                        float x = BitConverter.UInt32BitsToSingle((uint)start + i);
                        if (Bits(AomMl.Log1pf(x)) != Bits(theirs[i])) { bad = $"log1pf({x:R} = {Bits(x):X8}) exhaustive: ours {Bits(AomMl.Log1pf(x)):X8} libaom {Bits(theirs[i]):X8}"; break; }
                    }
                }
        }
        finally { Native.twin_set_fpcw_target(0x37F); }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    private static readonly int[] TxSizesUpTo32 = Enumerable.Range(0, AomTables.TX_SIZES_ALL)
        .Where(t => AomTables.TxSizeWide[t] <= 32 && AomTables.TxSizeHigh[t] <= 32).ToArray();

    [Test]
    public async Task HorverCorrelation_AllSizes()
    {
        if (!Available) return;
        var rng = new Random(17);
        string? bad = null;
        var hv = new float[2];
        for (int it = 0; it < 30000 && bad == null; it++)
        {
            int w = 4 << rng.Next(5), h = 4 << rng.Next(5);
            int stride = w + rng.Next(3) * 4;
            var d = rng.Next(3) == 0 ? SmoothResidual(rng, w, h, stride) : RandomResidual(rng, stride * h);
            AomMl.GetHorverCorrelationFull(d, stride, w, h, out float hc, out float vc);
            Native.twin_horver(d, stride, w, h, hv);
            bad = DiffFloats($"horver {w}x{h}", [hc, vc], hv, 2);
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task EnergyDistributionFiner_AndMeanDev_AllTxSizes()
    {
        if (!Available) return;
        var rng = new Random(18);
        string? bad = null;
        for (int it = 0; it < 30000 && bad == null; it++)
        {
            int tx = TxSizesUpTo32[rng.Next(TxSizesUpTo32.Length)];
            int w = AomTables.TxSizeWide[tx], h = AomTables.TxSizeHigh[tx];
            int stride = w + rng.Next(3) * 4;
            var d = rng.Next(3) == 0 ? SmoothResidual(rng, w, h, stride) : RandomResidual(rng, stride * h);
            if (rng.Next(2) == 0) for (int i = 0; i < d.Length; i++) d[i] = (short)Math.Clamp((int)d[i], -4095, 4095);
            var ho = new float[16]; var vo = new float[16]; var ht = new float[16]; var vt = new float[16];
            AomMl.GetEnergyDistributionFiner(d, stride, w, h, ho, vo);
            Native.twin_energy_finer(d, stride, w, h, ht, vt);
            int ew = w <= 8 ? w : w / 2, eh = h <= 8 ? h : h / 2;
            bad = DiffFloats($"energy {w}x{h} hor", ho, ht, ew - 1) ?? DiffFloats($"energy {w}x{h} ver", vo, vt, eh - 1);
            if (bad != null) break;
            var fo = new float[16]; var ft = new float[16];
            int no = AomMl.GetMeanDevFeatures(d, stride, w, h, fo);
            int nt = Native.twin_mean_dev(d, stride, w, h, ft);
            if (no != nt) bad = $"mean dev {w}x{h}: count {no} vs {nt}";
            else bad = DiffFloats($"mean dev {w}x{h}", fo, ft, no);
            AomMl.GetBlkSseSum(d, stride, w, h, out int xs, out long x2s);
            Native.twin_blk_sse_sum(d, stride, w, h, out int xs2, out long x2s2);
            if (bad == null && (xs != xs2 || x2s != x2s2)) bad = $"blk sse sum {w}x{h}";
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    // a plane block size holding the tx block at (blkRow, blkCol) (in 4-sample units)
    private static (int bsize, int blkRow, int blkCol) BlockFor(Random rng, int tx)
    {
        int tw = AomTables.TxSizeWide[tx], th = AomTables.TxSizeHigh[tx];
        var fits = Enumerable.Range(0, AomTables.BLOCK_SIZES_ALL)
            .Where(b => AomTables.BlockSizeWide[b] >= tw && AomTables.BlockSizeHigh[b] >= th).ToArray();
        int bs = fits[rng.Next(fits.Length)];
        int bw = AomTables.BlockSizeWide[bs], bh = AomTables.BlockSizeHigh[bs];
        return (bs, rng.Next((bh - th) / th + 1) * th / 4, rng.Next((bw - tw) / tw + 1) * tw / 4);
    }

    [Test]
    public async Task PredictTxSplit_AllTxSizes()
    {
        if (!Available) return;
        var rng = new Random(19);
        string? bad = null;
        for (int it = 0; it < 20000 && bad == null; it++)
        {
            int tx = rng.Next(AomTables.TX_SIZES_ALL);
            var (bs, br, bc) = BlockFor(rng, tx);
            int stride = AomTables.BlockSizeWide[bs];
            var d = rng.Next(2) == 0 ? SmoothResidual(rng, stride, AomTables.BlockSizeHigh[bs], stride)
                : RandomResidual(rng, stride * AomTables.BlockSizeHigh[bs]);
            int ours = AomMl.PredictTxSplit(d.AsSpan(4 * br * stride + 4 * bc), stride, tx);
            int theirs = Native.twin_tx_split(d, bs, br, bc, tx);
            if (ours != theirs) bad = $"tx split tx {tx} bsize {bs}: {ours} vs {theirs}";
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task PredictIntraTxDepthPrune_Random()
    {
        if (!Available) return;
        var rng = new Random(20);
        string? bad = null;
        int[] counts = new int[4];
        for (int it = 0; it < 40000 && bad == null; it++)
        {
            int tx = rng.Next(4) != 0 ? AomTables.TX_8X8 : rng.Next(AomTables.TX_SIZES_ALL);
            int bs = rng.Next(4) != 0 ? AomTables.TxsizeToBsize[tx] : rng.Next(AomTables.BLOCK_SIZES_ALL);
            if (AomTables.BlockSizeWide[bs] < AomTables.TxSizeWide[tx] || AomTables.BlockSizeHigh[bs] < AomTables.TxSizeHigh[tx]) continue;
            int bd = rng.Next(5) == 0 ? 10 : 8;
            bool lossless = rng.Next(10) == 0;
            int stride = AomTables.BlockSizeWide[bs];
            var d = rng.Next(2) == 0 ? SmoothResidual(rng, stride, AomTables.BlockSizeHigh[bs], stride)
                : RandomResidual(rng, stride * AomTables.BlockSizeHigh[bs]);
            for (int i = 0; i < d.Length; i++) d[i] = (short)Math.Clamp((int)d[i], -255, 255);
            uint sv = rng.Next(3) == 0 ? (uint)rng.Next(0, 100) : (uint)rng.Next(0, 70000);
            int q = rng.Next(256);
            int ours = AomMl.PredictIntraTxDepthPrune(d, stride, bs, tx, lossless, bd, sv, Native.twin_dc_q(q, bd));
            int theirs = Native.twin_intra_tx_depth(d, 0, 0, bs, tx, lossless ? 1 : 0, bd, sv, q);
            if (theirs == 99) theirs = -1;
            counts[ours + 1]++;
            if (ours != theirs) bad = $"intra tx depth tx {tx} bs {bs} bd {bd} sv {sv} q {q}: {ours} vs {theirs}";
        }
        await Assert.That(bad ?? "").IsEqualTo("");
        await Assert.That(counts[AomMl.TX_PRUNE_SPLIT + 1] > 100 && counts[AomMl.TX_PRUNE_LARGEST + 1] > 100).IsTrue();
    }

    [Test]
    public async Task PruneTx2D_Random()
    {
        if (!Available) return;
        var rng = new Random(21);
        string? bad = null;
        int[] setTypes = [AomTables.EXT_TX_SET_ALL16, AomTables.EXT_TX_SET_DTT9_IDTX_1DDCT, AomTables.EXT_TX_SET_DTT4_IDTX_1DDCT];
        for (int it = 0; it < 40000 && bad == null; it++)
        {
            int tx = rng.Next(AomTables.TX_SIZES_ALL);
            var (bs, br, bc) = BlockFor(rng, tx);
            int stride = AomTables.BlockSizeWide[bs];
            var d = rng.Next(2) == 0 ? SmoothResidual(rng, stride, AomTables.BlockSizeHigh[bs], stride)
                : RandomResidual(rng, stride * AomTables.BlockSizeHigh[bs]);
            // the inter set av1_get_ext_tx_set_type gives (ALL16 below 16x16 squares, DTT9_IDTX_1DDCT at 16x16), or the
            // reduced set prune_tx_2D ignores
            int set = rng.Next(8) == 0 ? AomTables.EXT_TX_SET_DTT4_IDTX_1DDCT
                : AomTables.TxsizeSqrMap[tx] == AomTables.TX_16X16 ? AomTables.EXT_TX_SET_DTT9_IDTX_1DDCT
                : setTypes[rng.Next(2)];
            int mode = rng.Next(1, 6);
            int mask = rng.Next(3) == 0 ? 0xFFFF : rng.Next(1, 0x10000);
            var mapO = new int[16]; var mapT = new int[16];
            Array.Fill(mapO, 77); Array.Fill(mapT, 77);
            ushort maskO = (ushort)mask;
            int maskT = mask;
            AomMl.PruneTx2D(d.AsSpan(4 * br * stride + 4 * bc), stride, tx, set, mode, mapO, ref maskO);
            Native.twin_prune_tx_2d(d, bs, tx, br, bc, set, mode, mapT, ref maskT);
            if (maskO != maskT || !mapO.SequenceEqual(mapT))
                bad = $"prune_tx_2D tx {tx} set {set} mode {mode} mask {mask:X4}: {maskO:X4} [{string.Join(",", mapO)}] vs {maskT:X4} [{string.Join(",", mapT)}]";
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    private static byte[] RandomPixels(Random rng, int n)
    {
        var p = new byte[n];
        int kind = rng.Next(3);
        int b = rng.Next(256), r = rng.Next(1, 128);
        for (int i = 0; i < n; i++) p[i] = kind == 0 ? (byte)rng.Next(256) : (byte)Math.Clamp(b + rng.Next(-r, r + 1), 0, 255);
        return p;
    }

    private static ushort[] RandomPixels16(Random rng, int n, int bd)
    {
        var p = new ushort[n];
        int max = (1 << bd) - 1;
        int kind = rng.Next(3);
        int b = rng.Next(max + 1), r = rng.Next(1, max / 2);
        for (int i = 0; i < n; i++) p[i] = kind == 0 ? (ushort)rng.Next(max + 1) : (ushort)Math.Clamp(b + rng.Next(-r, r + 1), 0, max);
        return p;
    }

    [Test]
    public async Task PerPixelVariance_AllBlockSizes()
    {
        if (!Available) return;
        var rng = new Random(22);
        string? bad = null;
        for (int it = 0; it < 6000 && bad == null; it++)
        {
            int bs = rng.Next(AomTables.BLOCK_SIZES_ALL);
            int w = AomTables.BlockSizeWide[bs], h = AomTables.BlockSizeHigh[bs];
            int stride = w + rng.Next(3) * 8;
            int kind = rng.Next(4);
            if (kind == 0)
            {
                var p = RandomPixels(rng, stride * h);
                uint o = AomMl.GetPerPixelVariance(p, stride, bs), t = Native.twin_perpixel_variance(p, stride, bs, 8, 0);
                if (o != t) bad = $"variance lowbd bsize {bs}: {o} vs {t}";
            }
            else
            {
                int bd = kind == 1 ? 8 : kind == 2 ? 10 : 12;
                var p = RandomPixels16(rng, stride * h, bd);
                uint o = AomMl.GetPerPixelVariance(p, stride, bs, bd), t = Native.twin_perpixel_variance16(p, stride, bs, bd, 1);
                if (o != t) bad = $"variance bd {bd} bsize {bs}: {o} vs {t}";
            }
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    private static long RandomRd(Random rng, long best) => rng.Next(12) switch
    {
        0 => 0,
        1 => long.MaxValue,
        2 => -rng.Next(1000),
        3 => rng.NextInt64(1000000000, 3000000000),
        _ => (long)(best * (0.2 + rng.NextDouble() * 1.6)),
    };

    private static long RandomBestRd(Random rng) => rng.Next(20) switch
    {
        0 => long.MaxValue,
        1 => rng.NextInt64(1000000000, 5000000000),
        2 => rng.Next(0, 3),
        _ => (long)Math.Pow(10, 2 + rng.NextDouble() * 6.9),
    };

    private static readonly int[] SquareBsizes =
        [AomTables.BLOCK_4X4, AomTables.BLOCK_8X8, AomTables.BLOCK_16X16, AomTables.BLOCK_32X32, AomTables.BLOCK_64X64, AomTables.BLOCK_128X128];

    private static (int w, int h) RandomFrame(Random rng) => rng.Next(4) switch
    {
        0 => (rng.Next(64, 480), rng.Next(64, 480)),
        1 => (rng.Next(480, 1920), rng.Next(480, 720)),
        2 => (rng.Next(720, 4000), rng.Next(720, 3000)),
        _ => (rng.Next(64, 4000), rng.Next(64, 3000)),
    };

    [Test]
    public async Task Prune4Partition_Random()
    {
        if (!Available) return;
        var rng = new Random(23);
        string? bad = null;
        int changed = 0;
        for (int it = 0; it < 20000 && bad == null; it++)
        {
            int bs = rng.Next(5) == 0 ? SquareBsizes[rng.Next(SquareBsizes.Length)]
                : new[] { AomTables.BLOCK_16X16, AomTables.BLOCK_32X32, AomTables.BLOCK_64X64 }[rng.Next(3)];
            int w = AomTables.BlockSizeWide[bs], h = AomTables.BlockSizeHigh[bs];
            int stride = w + rng.Next(3) * 8;
            long best = RandomBestRd(rng);
            var rd = new long[8];
            for (int i = 0; i < 8; i++) rd[i] = RandomRd(rng, best / 2);
            int partCtx = rng.Next(10);
            uint pbVar = rng.Next(4) == 0 ? (uint)rng.Next(0, 50) : (uint)rng.Next(0, 20000);
            var (fw, fh) = RandomFrame(rng);
            int level = rng.Next(6);
            var init = new[] { rng.Next(2), rng.Next(2) };
            var o = (int[])init.Clone(); var t = (int[])init.Clone();
            int kind = rng.Next(3);
            if (kind == 0)
            {
                var p = RandomPixels(rng, stride * h);
                AomMl.Prune4Partition(p, stride, bs, partCtx, best, rd.AsSpan(0, 2), rd.AsSpan(2, 2), rd.AsSpan(4, 4), pbVar, fw, fh, level, o);
                Native.twin_prune_4_partition(p, stride, bs, 8, 0, partCtx, best, rd, pbVar, fw, fh, level, t);
            }
            else
            {
                int bd = kind == 1 ? 10 : 12;
                var p = RandomPixels16(rng, stride * h, bd);
                AomMl.Prune4Partition(p, stride, bd, bs, partCtx, best, rd.AsSpan(0, 2), rd.AsSpan(2, 2), rd.AsSpan(4, 4), pbVar, fw, fh, level, o);
                Native.twin_prune_4_partition16(p, stride, bs, bd, 1, partCtx, best, rd, pbVar, fw, fh, level, t);
            }
            if (!o.SequenceEqual(t)) bad = $"prune 4 bsize {bs} level {level} best {best}: [{o[0]},{o[1]}] vs [{t[0]},{t[1]}]";
            if (!t.SequenceEqual(init)) changed++;
        }
        await Assert.That(bad ?? "").IsEqualTo("");
        await Assert.That(changed).IsGreaterThan(1000);
    }

    [Test]
    public async Task PruneAbPartition_Random()
    {
        if (!Available) return;
        var rng = new Random(24);
        string? bad = null;
        for (int it = 0; it < 30000 && bad == null; it++)
        {
            int bs = SquareBsizes[rng.Next(SquareBsizes.Length)];
            long best = RandomBestRd(rng);
            var rd = new long[8];
            for (int i = 0; i < 8; i++) rd[i] = RandomRd(rng, best / 2);
            int partCtx = rng.Next(10), varCtx = rng.Next(17);
            var init = new[] { rng.Next(2), rng.Next(2), rng.Next(2), rng.Next(2) };
            var o = (int[])init.Clone(); var t = (int[])init.Clone();
            AomMl.PruneAbPartition(bs, partCtx, varCtx, best, rd.AsSpan(0, 2), rd.AsSpan(2, 2), rd.AsSpan(4, 4), o);
            Native.twin_prune_ab_partition(bs, partCtx, varCtx, best, rd, t);
            if (!o.SequenceEqual(t)) bad = $"prune ab bsize {bs}: [{string.Join(",", o)}] vs [{string.Join(",", t)}]";
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }

    [Test]
    public async Task PruneRectPartition_Random()
    {
        if (!Available) return;
        var rng = new Random(25);
        string? bad = null;
        int pruned = 0;
        for (int it = 0; it < 20000 && bad == null; it++)
        {
            int bs = SquareBsizes[rng.Next(SquareBsizes.Length)];
            int w = AomTables.BlockSizeWide[bs], h = AomTables.BlockSizeHigh[bs];
            int stride = w + rng.Next(3) * 8;
            long best = RandomBestRd(rng);
            long none = RandomRd(rng, best);
            var split = new long[4];
            for (int i = 0; i < 4; i++) split[i] = RandomRd(rng, best / 4);
            var init = new[] { rng.Next(4) == 0 ? 1 : 0, rng.Next(4) == 0 ? 1 : 0 };
            var o = (int[])init.Clone(); var t = (int[])init.Clone();
            if (rng.Next(3) == 0)
            {
                var p = RandomPixels(rng, stride * h);
                AomMl.PruneRectPartition(p, stride, bs, best, none, split, o);
                Native.twin_prune_rect_partition(p, stride, bs, 8, 0, best, none, split, t);
            }
            else
            {
                int bd = rng.Next(2) == 0 ? 10 : 12;
                var p = RandomPixels16(rng, stride * h, bd);
                AomMl.PruneRectPartition(p, stride, bd, bs, best, none, split, o);
                Native.twin_prune_rect_partition16(p, stride, bs, bd, 1, best, none, split, t);
            }
            if (!o.SequenceEqual(t)) bad = $"prune rect bsize {bs}: [{o[0]},{o[1]}] vs [{t[0]},{t[1]}]";
            if (!t.SequenceEqual(init)) pruned++;
        }
        await Assert.That(bad ?? "").IsEqualTo("");
        await Assert.That(pruned).IsGreaterThan(500);
    }

    [Test]
    public async Task EarlyTermAfterSplit_Random()
    {
        if (!Available) return;
        var rng = new Random(26);
        string? bad = null;
        int terminated = 0;
        int[] parts = [AomTables.PARTITION_NONE, AomTables.PARTITION_HORZ, AomTables.PARTITION_VERT, AomTables.PARTITION_SPLIT,
            AomTables.PARTITION_HORZ_A, AomTables.PARTITION_HORZ_B, AomTables.PARTITION_VERT_A, AomTables.PARTITION_VERT_B,
            AomTables.PARTITION_HORZ_4, AomTables.PARTITION_VERT_4, AomTables.PARTITION_INVALID];
        for (int it = 0; it < 30000 && bad == null; it++)
        {
            int bs = SquareBsizes[rng.Next(SquareBsizes.Length)];
            var (fw, fh) = RandomFrame(rng);
            int level = rng.Next(1, 3);
            int bd = rng.Next(4) == 0 ? 10 : 8;
            int q = rng.Next(256);
            long best = RandomBestRd(rng);
            long none = RandomRd(rng, best), splitRd = RandomRd(rng, best);
            var sbr = new long[4];
            for (int i = 0; i < 4; i++) sbr[i] = RandomRd(rng, best / 4);
            var children = new int[8];
            var minBwBh = new int[8];
            for (int i = 0; i < 4; i++)
            {
                int cb = rng.Next(2) == 0 ? AomMl.GetPartitionSubsize(bs, AomTables.PARTITION_SPLIT) : SquareBsizes[rng.Next(SquareBsizes.Length)];
                if (cb == AomTables.BLOCK_INVALID) cb = AomTables.BLOCK_4X4;
                int cp = parts[rng.Next(parts.Length)];
                children[2 * i] = cb; children[2 * i + 1] = cp;
                int mbw = AomMl.MAX_SB_SIZE_LOG2, mbh = AomMl.MAX_SB_SIZE_LOG2;
                AomMl.GetMinBsizeNode(cb, cp, ref mbw, ref mbh);   // the children have no split subtrees here
                minBwBh[2 * i] = mbw; minBwBh[2 * i + 1] = mbh;
            }
            var sms = new uint[13];
            for (int i = 0; i < 13; i++) sms[i] = rng.Next(4) == 0 ? (uint)rng.Next(0, 10) : (uint)rng.Next(0, 5000000);
            int dcq = Native.twin_dc_q(q, bd) >> (bd - 8);
            bool o = AomMl.EarlyTermAfterSplit(bs, fw, fh, level, dcq, best, none, splitRd, sbr, minBwBh, sms[0], sms.AsSpan(1, 4),
                sms.AsSpan(5, 8), false);
            bool t = Native.twin_early_term_after_split(bs, fw, fh, level, q, bd, best, none, splitRd, sbr, children, sms) != 0;
            if (o != t) bad = $"early term bsize {bs} best {best}: {o} vs {t}";
            if (t) terminated++;
        }
        await Assert.That(bad ?? "").IsEqualTo("");
        await Assert.That(terminated).IsGreaterThan(500);
    }

    // an image-like plane: a gradient, an edge in a random direction, noise
    private static int[] RandomPlane(Random rng, int w, int h, int max)
    {
        var p = new int[w * h];
        double ang = rng.NextDouble() * Math.PI, gx = Math.Cos(ang), gy = Math.Sin(ang);
        double slope = rng.NextDouble() * 6, noise = rng.Next(4) == 0 ? 128 : rng.NextDouble() * 12;
        double b = rng.NextDouble() * max, edge = rng.NextDouble() * max / 2;
        double off = rng.NextDouble() * (w + h) - (w + h) / 2.0;
        bool flat = rng.Next(12) == 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = x * gx + y * gy;
                double v = flat ? b : b + slope * t * (max / 255.0) + (t > off ? edge : 0) + (rng.NextDouble() - 0.5) * noise * (max / 255.0);
                p[y * w + x] = (int)Math.Clamp(Math.Round(v), 0, max);
            }
        return p;
    }

    [Test]
    public async Task IntraHog_CollectAndPrune_Random()
    {
        if (!Available) return;
        var rng = new Random(27);
        string? bad = null;
        int pruned = 0;
        for (int it = 0; it < 30000 && bad == null; it++)
        {
            int bs = rng.Next(AomTables.BLOCK_SIZES_ALL);
            int bw = AomTables.BlockSizeWide[bs], bh = AomTables.BlockSizeHigh[bs];
            bool chroma = rng.Next(2) == 0;
            int ssX = chroma ? rng.Next(2) : 0, ssY = chroma ? (ssX == 1 ? rng.Next(2) : 0) : 0;
            int plane = chroma ? 1 : 0;
            int outR = rng.Next(3) == 0 ? rng.Next(bw / 4) * 4 : 0, outB = rng.Next(3) == 0 ? rng.Next(bh / 4) * 4 : 0;
            int edgeR = outR > 0 ? -outR * 8 : rng.Next(3) * 64, edgeB = outB > 0 ? -outB * 8 : rng.Next(3) * 64;
            int rows = (edgeB >= 0 ? bh : (edgeB >> 3) + bh) >> ssY, cols = (edgeR >= 0 ? bw : (edgeR >> 3) + bw) >> ssX;
            int pw = bw >> ssX, ph = bh >> ssY;
            int stride = pw + rng.Next(3) * 8;
            int bd = rng.Next(3) switch { 0 => 8, 1 => 10, _ => 12 };
            bool hbd = bd > 8 || rng.Next(4) == 0;
            var vals = RandomPlane(rng, stride, ph, (1 << bd) - 1);
            float th = (float)(rng.NextDouble() * 6 - 4);
            var maskO = new byte[AomTables.UV_INTRA_MODES];
            for (int i = 0; i < maskO.Length; i++) maskO[i] = (byte)(rng.Next(6) == 0 ? 1 : 0);
            var maskT = (byte[])maskO.Clone();
            var hogO = new float[32];
            var hogT = new float[32];
            if (!hbd)
            {
                var src = vals.Select(v => (byte)v).ToArray();
                AomMl.CollectHogData(src, stride, rows, cols, ssX, ssY, hogO);
                Native.twin_collect_hog(src, stride, bs, edgeR, edgeB, ssX, ssY, plane, 0, hogT);
                AomMl.PruneIntraModeWithHog(src, stride, rows, cols, ssX, ssY, th, maskO);
                Native.twin_prune_hog(src, stride, bs, edgeR, edgeB, ssX, ssY, chroma ? 1 : 0, 0, th, maskT);
            }
            else
            {
                var src = vals.Select(v => (ushort)v).ToArray();
                AomMl.CollectHogData(src, stride, rows, cols, ssX, ssY, hogO);
                Native.twin_collect_hog16(src, stride, bs, edgeR, edgeB, ssX, ssY, plane, 1, hogT);
                AomMl.PruneIntraModeWithHog(src, stride, rows, cols, ssX, ssY, th, maskO);
                Native.twin_prune_hog16(src, stride, bs, edgeR, edgeB, ssX, ssY, chroma ? 1 : 0, 1, th, maskT);
            }
            bad = DiffFloats($"hog bsize {bs} ss {ssX}{ssY} rows {rows} cols {cols} bd {bd}", hogO, hogT, 32);
            if (bad == null && !maskO.SequenceEqual(maskT))
                bad = $"hog prune bsize {bs} th {th}: [{string.Join(",", maskO)}] vs [{string.Join(",", maskT)}]";
            if (maskT.Skip(1).Take(8).Any(m => m != 0)) pruned++;
        }
        await Assert.That(bad ?? "").IsEqualTo("");
        await Assert.That(pruned).IsGreaterThan(3000);
    }

    // the superblock gradient-cache path gives the same histogram as the direct one ported
    [Test]
    public async Task IntraHog_GradientCachePath_MatchesDirect()
    {
        if (!Available) return;
        var rng = new Random(28);
        string? bad = null;
        for (int it = 0; it < 6000 && bad == null; it++)
        {
            int sb = rng.Next(2) == 0 ? AomTables.BLOCK_64X64 : AomTables.BLOCK_128X128;
            var fits = Enumerable.Range(0, AomTables.BLOCK_SIZES_ALL)
                .Where(b => AomTables.BlockSizeWide[b] <= AomTables.BlockSizeWide[sb] && AomTables.BlockSizeHigh[b] <= AomTables.BlockSizeHigh[sb]).ToArray();
            int bs = fits[rng.Next(fits.Length)];
            int bw = AomTables.BlockSizeWide[bs], bh = AomTables.BlockSizeHigh[bs];
            int miRow = rng.Next(AomTables.BlockSizeHigh[sb] / bh) * AomTables.MiSizeHigh[bs];
            int miCol = rng.Next(AomTables.BlockSizeWide[sb] / bw) * AomTables.MiSizeWide[bs];
            bool chroma = rng.Next(2) == 0;
            int ssX = chroma ? rng.Next(2) : 0, ssY = chroma ? (ssX == 1 ? rng.Next(2) : 0) : 0;
            int plane = chroma ? 1 : 0;
            int sbw = AomTables.BlockSizeWide[sb] >> ssX, sbh = AomTables.BlockSizeHigh[sb] >> ssY;
            int stride = sbw + rng.Next(3) * 8;
            int rows = bh >> ssY, cols = bw >> ssX;
            int bd = rng.Next(3) switch { 0 => 8, 1 => 10, _ => 12 };
            bool hbd = bd > 8;
            var vals = RandomPlane(rng, stride, sbh, (1 << bd) - 1);
            int off = ((miRow * 4) >> ssY) * stride + ((miCol * 4) >> ssX);
            var hogO = new float[32];
            var hogT = new float[32];
            if (!hbd)
            {
                var src = vals.Select(v => (byte)v).ToArray();
                AomMl.CollectHogData(src.AsSpan(off), stride, rows, cols, ssX, ssY, hogO);
                Native.twin_collect_hog_cached(src, stride, sb, miRow, miCol, bs, 0, 0, ssX, ssY, plane, 0, hogT);
            }
            else
            {
                var src = vals.Select(v => (ushort)v).ToArray();
                AomMl.CollectHogData(src.AsSpan(off), stride, rows, cols, ssX, ssY, hogO);
                Native.twin_collect_hog_cached16(src, stride, sb, miRow, miCol, bs, 0, 0, ssX, ssY, plane, 1, hogT);
            }
            bad = DiffFloats($"hog cached sb {sb} bsize {bs} at {miRow},{miCol} ss {ssX}{ssY}", hogO, hogT, 32);
        }
        await Assert.That(bad ?? "").IsEqualTo("");
    }
}
