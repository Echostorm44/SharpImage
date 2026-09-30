using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Twins of the libaom encoder port (src/SharpImage/Formats/Av1/Aom): each ported function against libaom 3.14.1's own,
// through aomtwin.dll (a thin export layer built against libaom.a; see scratchpad aomtwin/aomtwin.c). Opt-in: point
// SHARPIMAGE_AOMTWIN at aomtwin.dll; without it the tests pass without checking.
public sealed class AomTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN");
    private static readonly bool Available = DllPath != null && File.Exists(DllPath) && Load();

    private static bool Load()
    {
        NativeLibrary.SetDllImportResolver(typeof(AomTwinTests).Assembly, (name, _, _) =>
            name == "aomtwin" ? NativeLibrary.Load(DllPath!) : IntPtr.Zero);
        Native.twin_init();
        return true;
    }

    private static unsafe class Native
    {
        [DllImport("aomtwin")] public static extern void twin_init();
        [DllImport("aomtwin")] public static extern int twin_cost_symbol(int p15);
        [DllImport("aomtwin")] public static extern void twin_cost_tokens_from_cdf(int* costs, ushort* cdf);
        [DllImport("aomtwin")] public static extern int twin_rdmult_kf(int qindex, int bd);
        [DllImport("aomtwin")] public static extern void twin_build_quantizer(int bd, int ydc, int udc, int uac, int vdc, int vac, int sharpness, short* outp);
        [DllImport("aomtwin")] public static extern int twin_quantize_fp(int* coeff, int n, int txSize, int txType, short* quant2,
            short* dequant2, short* round2, int logScale, int* qcoeff, int* dqcoeff);
        [DllImport("aomtwin")] public static extern int twin_quantize_b(int* coeff, int n, int txSize, int txType, short* zbin2, short* round2,
            short* quant2, short* shift2, short* dequant2, int logScale, int* qcoeff, int* dqcoeff);
        [DllImport("aomtwin")] public static extern void twin_scan(int txSize, int txType, short* outp, int n);
    }

    [Test]
    public async Task CostSymbol_AllProbabilities()
    {
        if (!Available) return;
        for (int p = 0; p <= 32768; p++)
            if (AomCost.CostSymbol(p) != Native.twin_cost_symbol(p))
                await Assert.That(AomCost.CostSymbol(p)).IsEqualTo(Native.twin_cost_symbol(p));
    }

    [Test]
    public async Task CostTokensFromCdf_RandomCdfs()
    {
        if (!Available) return;
        var rng = new Random(1);
        for (int iter = 0; iter < 20000; iter++)
        {
            int n = rng.Next(2, 17);
            // a random CDF with some tiny probabilities (below EC_MIN_PROB)
            var cuts = new SortedSet<int>();
            while (cuts.Count < n - 1) cuts.Add(rng.Next(1, 32768));
            var cum = cuts.ToArray();
            if (rng.Next(4) == 0) cum[0] = Math.Min(cum[0], 2);
            var libaom = new ushort[n + 1];
            var ours = new ushort[n];
            for (int i = 0; i < n - 1; i++) { libaom[i] = (ushort)(32768 - cum[i]); ours[i] = libaom[i]; }
            libaom[n - 1] = 0; ours[n - 1] = 0;
            int[] a = new int[n], b = new int[n];
            unsafe { fixed (int* pa = a) fixed (ushort* pc = libaom) Native.twin_cost_tokens_from_cdf(pa, pc); }
            AomCost.CostTokensFromCdf(b, ours, n);
            await Assert.That(b).IsEquivalentTo(a);
        }
    }

    [Test]
    public async Task RdMultKeyFrame_AllQ()
    {
        if (!Available) return;
        foreach (int bd in new[] { 8, 10, 12 })
            for (int q = 0; q < 256; q++)
                await Assert.That(AomRd.RdMultKeyFrame(q, bd)).IsEqualTo(Native.twin_rdmult_kf(q, bd));
    }

    [Test]
    public async Task BuildQuantizer_AllConfigs()
    {
        if (!Available) return;
        foreach (int bd in new[] { 8, 10, 12 })
            foreach (int sharp in new[] { 0, 3, 7 })
                foreach (var (ydc, udc, uac, vdc, vac) in new[] { (0, 0, 0, 0, 0), (-5, 3, -2, 4, 7) })
                {
                    var outp = new short[7 * 3 * 256 * 2];
                    unsafe { fixed (short* p = outp) Native.twin_build_quantizer(bd, ydc, udc, uac, vdc, vac, sharp, p); }
                    var q = new AomQuants(bd, ydc, udc, uac, vdc, vac, sharp);
                    var tables = new[] { q.Quant, q.QuantShift, q.Zbin, q.Round, q.QuantFp, q.RoundFp, q.Dequant };
                    for (int k = 0; k < 7; k++)
                        for (int p = 0; p < 3; p++)
                            for (int qi = 0; qi < 256; qi++)
                                for (int i = 0; i < 2; i++)
                                    if (tables[k][p, qi, i] != outp[((k * 3 + p) * 256 + qi) * 2 + i])
                                        await Assert.That($"table {k} plane {p} q {qi} {i}: {tables[k][p, qi, i]}")
                                            .IsEqualTo($"table {k} plane {p} q {qi} {i}: {outp[((k * 3 + p) * 256 + qi) * 2 + i]}");
                }
    }

    // DCT_DCT and IDTX scans (the intra tx types use the default scan of the tx size for 2D types)
    [Test]
    public async Task Quantizers_RandomCoefficients()
    {
        if (!Available) return;
        var rng = new Random(2);
        var q = new AomQuants(8, 0, 0, 0, 0, 0, 0);
        for (int iter = 0; iter < 40000; iter++)
        {
            int tx = rng.Next(19), qi = rng.Next(256), txType = 0;
            int n = Av1Tables.Scans[tx].Length, logScale = AomQuantize.TxScale(tx);
            var coeff = new int[n];
            int range = rng.Next(4) switch { 0 => 64, 1 => 1024, 2 => 8192, _ => 60000 };
            for (int i = 0; i < n; i++) coeff[i] = rng.Next(3) == 0 ? rng.Next(-range, range) : rng.Next(-8, 8);
            var scan = new short[n];
            unsafe { fixed (short* s = scan) Native.twin_scan(tx, txType, s, n); }
            for (int i = 0; i < n; i++) if ((ushort)scan[i] != Av1Tables.Scans[tx][i]) { await Assert.That($"scan tx {tx} [{i}]").IsEqualTo("same"); break; }
            int[] aq = new int[n], adq = new int[n], bq = new int[n], bdq = new int[n];
            short[] quant = { q.QuantFp[0, qi, 0], q.QuantFp[0, qi, 1] }, deq = { q.Dequant[0, qi, 0], q.Dequant[0, qi, 1] },
                rnd = { q.RoundFp[0, qi, 0], q.RoundFp[0, qi, 1] };
            int ea, eb;
            unsafe
            {
                fixed (int* c = coeff) fixed (int* pq = aq) fixed (int* pdq = adq) fixed (short* a1 = quant) fixed (short* a2 = deq) fixed (short* a3 = rnd)
                    ea = Native.twin_quantize_fp(c, n, tx, txType, a1, a2, a3, logScale, pq, pdq);
            }
            eb = AomQuantize.QuantizeFpAvx2(coeff, n, AomQuantize.IScan(tx), rnd[0], rnd[1], quant[0], quant[1], deq[0], deq[1], logScale, bq, bdq);
            if (eb != ea || !bq.AsSpan().SequenceEqual(aq) || !bdq.AsSpan().SequenceEqual(adq))
                await Assert.That($"quantize_fp tx {tx} q {qi}: eob {eb} vs {ea}").IsEqualTo("equal");
            short[] zb = { q.Zbin[0, qi, 0], q.Zbin[0, qi, 1] }, rb = { q.Round[0, qi, 0], q.Round[0, qi, 1] },
                qb = { q.Quant[0, qi, 0], q.Quant[0, qi, 1] }, sb = { q.QuantShift[0, qi, 0], q.QuantShift[0, qi, 1] };
            unsafe
            {
                fixed (int* c = coeff) fixed (int* pq = aq) fixed (int* pdq = adq) fixed (short* a1 = zb) fixed (short* a2 = rb) fixed (short* a3 = qb)
                fixed (short* a4 = sb) fixed (short* a5 = deq)
                    ea = Native.twin_quantize_b(c, n, tx, txType, a1, a2, a3, a4, a5, logScale, pq, pdq);
            }
            eb = AomQuantize.QuantizeBAvx2(coeff, n, AomQuantize.IScan(tx), zb[0], zb[1], rb[0], rb[1], qb[0], qb[1], sb[0], sb[1], deq[0], deq[1], logScale, bq, bdq);
            if (eb != ea || !bq.AsSpan().SequenceEqual(aq) || !bdq.AsSpan().SequenceEqual(adq))
                await Assert.That($"quantize_b tx {tx} q {qi}: eob {eb} vs {ea}").IsEqualTo("equal");
        }
    }
}
