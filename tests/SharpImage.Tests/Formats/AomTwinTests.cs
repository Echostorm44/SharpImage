using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Twins of the libaom encoder port (src/SharpImage/Formats/Av1/Aom): each ported function against libaom 3.14.1's own,
// through aomtwin.dll (a thin export layer built against libaom.a; tests/native/aomtwin/aomtwin.c). Opt-in: point
// SHARPIMAGE_AOMTWIN at aomtwin.dll; without it the tests report Skipped.
[NotInParallel]
public sealed partial class AomTwinTests
{
    private const string EnvVar = "SHARPIMAGE_AOMTWIN";
    private static readonly string? DllPath = AomTwinNative.PathFromEnv(EnvVar);
    private static readonly bool Available = DllPath != null && Load();

    private static bool Load()
    {
        AomTwinNative.Register("aomtwin", DllPath!);
        Native.twin_init();
        return true;
    }

    private static unsafe partial class Native
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
        [DllImport("aomtwin")] public static extern int twin_default_coeff_costs(int baseQindex, int* outp, int outLen);
        [DllImport("aomtwin")] public static extern int twin_default_mode_costs(int enableFilterIntra, int* outp, int outLen, int* offs);
        [DllImport("aomtwin")] public static extern int twin_cost_coeffs_txb(int* costs, int plane, int txSize, int txType, int txbSkipCtx,
            int dcSignCtx, int* qcoeff, int eob);
        [DllImport("aomtwin")] public static extern int twin_optimize_txb(int* costs, int plane, int txSize, int txType, int txbSkipCtx,
            int dcSignCtx, int* coeff, int* qcoeff, int* dqcoeff, int eob, short* dequant2, int rdmult, int sharpness,
            int chromaTrellisMult, int* rate);
    }

    // CoeffCosts in libaom's struct layout (coeff_costs[5][2] then eob_costs[7][2])
    private static int[] Flatten(AomCoeffCosts c)
    {
        var l = new List<int>();
        foreach (var k in c.Coeff) { l.AddRange(k.TxbSkip); l.AddRange(k.BaseEob); l.AddRange(k.Base); l.AddRange(k.EobExtra); l.AddRange(k.DcSign); l.AddRange(k.Lps); }
        foreach (var e in c.Eob) l.AddRange(e);
        return l.ToArray();
    }

    private static int QCat(int q) => q <= 20 ? 0 : q <= 60 ? 1 : q <= 120 ? 2 : 3;

    private static AomCoeffCosts OurDefaultCosts(int q)
    {
        var fc = new Av1CdfCoefContext();
        Av1CdfDefaults.InitializeCoef(fc, QCat(q));
        var c = new AomCoeffCosts();
        c.Fill(fc, 3);
        return c;
    }

    private static ushort[] LibaomScan(int tx, int txType)
    {
        int n = Math.Min(AomTables.TxSizeWide[tx], 32) * Math.Min(AomTables.TxSizeHigh[tx], 32);
        var s = new short[n];
        unsafe { fixed (short* p = s) Native.twin_scan(tx, txType, p, n); }
        return s.Select(v => (ushort)v).ToArray();
    }

    // the tx types a tx size can use (2D everywhere it is defined, the 1D classes up to 16 points)
    private static int RandomTxType(Random rng, int tx)
    {
        int w = AomTables.TxSizeWide[tx], h = AomTables.TxSizeHigh[tx];
        if (Math.Max(w, h) <= 16) return rng.Next(16);
        if (Math.Max(w, h) == 32) return rng.Next(2) == 0 ? AomTables.DCT_DCT : AomTables.IDTX;
        return AomTables.DCT_DCT;
    }

    private static short[] IScanOf(ushort[] scan)
    {
        var a = new short[scan.Length];
        for (int i = 0; i < scan.Length; i++) a[scan[i]] = (short)i;
        return a;
    }

    private static int[] RandomLevels(Random rng, ushort[] scan, int eob, int n)
    {
        var q = new int[n];
        for (int i = 0; i < eob; i++)
        {
            int v = rng.Next(10) switch { < 5 => 0, < 8 => rng.Next(1, 3), 8 => rng.Next(3, 16), _ => rng.Next(16, 300) };
            if (i == eob - 1 && v == 0) v = 1;
            q[scan[i]] = rng.Next(2) == 0 ? v : -v;
        }
        return q;
    }

    [Test]
    public async Task CostSymbol_AllProbabilities()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        for (int p = 0; p <= 32768; p++)
            if (AomCost.CostSymbol(p) != Native.twin_cost_symbol(p))
                await Assert.That(AomCost.CostSymbol(p)).IsEqualTo(Native.twin_cost_symbol(p));
    }

    [Test]
    public async Task CostTokensFromCdf_RandomCdfs()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
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
        AomTwinNative.SkipUnless(Available, EnvVar);
        foreach (int bd in new[] { 8, 10, 12 })
            for (int q = 0; q < 256; q++)
                await Assert.That(AomRd.RdMultKeyFrame(q, bd)).IsEqualTo(Native.twin_rdmult_kf(q, bd));
    }

    [Test]
    public async Task BuildQuantizer_AllConfigs()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
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
        AomTwinNative.SkipUnless(Available, EnvVar);
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

    [Test]
    public async Task DefaultCoeffCosts_AllQContexts()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        foreach (int q in new[] { 0, 20, 21, 60, 61, 120, 121, 255 })
        {
            var ours = Flatten(OurDefaultCosts(q));
            var theirs = new int[ours.Length];
            int n;
            unsafe { fixed (int* p = theirs) n = Native.twin_default_coeff_costs(q, p, theirs.Length); }
            await Assert.That(n).IsEqualTo(ours.Length);
            // base_cost context 41 (libaom's SIG_COEF_CONTEXTS = 42) is allocated but never used
            int per = 13 * 2 + 4 * 3 + 42 * 8 + 9 * 2 + 3 * 2 + 21 * 26, baseOff = 13 * 2 + 4 * 3;
            for (int i = 0; i < ours.Length; i++)
            {
                int k = i % per;
                if (i < 10 * per && k >= baseOff + 41 * 8 && k < baseOff + 42 * 8) continue;
                // eob costs of the 512 / 1024 sizes in the 1D context: libaom keeps a separate (never used) CDF there
                if (i >= 10 * per && (i - 10 * per) / 22 >= 10 && (i - 10 * per) % 22 >= 11) continue;
                if (ours[i] != theirs[i])
                {
                    await Assert.That($"q {q} index {i} (block {i / per} offset {k}): {ours[i]}").IsEqualTo($"q {q} index {i} (block {i / per} offset {k}): {theirs[i]}");
                    break;
                }
            }
        }
    }

    [Test]
    public async Task CostCoeffsTxb_Random()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(3);
        var costsByQ = new[] { 10, 40, 100, 200 }.Select(OurDefaultCosts).ToArray();
        var flat = costsByQ.Select(Flatten).ToArray();
        for (int iter = 0; iter < 20000; iter++)
        {
            int qi = rng.Next(4), tx = rng.Next(19), txType = RandomTxType(rng, tx), plane = rng.Next(3);
            var scan = LibaomScan(tx, txType);
            int n = scan.Length, eob = rng.Next(4) == 0 ? rng.Next(1, 4) : rng.Next(1, n + 1);
            var q = RandomLevels(rng, scan, eob, n);
            int skipCtx = plane == 0 ? rng.Next(7) : rng.Next(7, 13), dcCtx = rng.Next(3);
            int ours = AomTxb.CostCoeffsTxb(costsByQ[qi], tx, txType, plane == 0 ? 0 : 1, new AomTxbCtx { TxbSkipCtx = skipCtx, DcSignCtx = dcCtx },
                q, eob, 0, scan);
            int theirs;
            var qq = (int[])q.Clone();
            unsafe { fixed (int* c = flat[qi]) fixed (int* pq = qq) theirs = Native.twin_cost_coeffs_txb(c, plane, tx, txType, skipCtx, dcCtx, pq, eob); }
            if (ours != theirs)
            {
                await Assert.That($"tx {tx} type {txType} eob {eob}: {ours}").IsEqualTo($"tx {tx} type {txType} eob {eob}: {theirs}");
                break;
            }
        }
    }

    [Test]
    public async Task OptimizeTxb_Random()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(4);
        var quants = new AomQuants(8, 0, 0, 0, 0, 0, 0);
        var costsByQ = new[] { 10, 40, 100, 200 }.Select(OurDefaultCosts).ToArray();
        var flat = costsByQ.Select(Flatten).ToArray();
        int checkedN = 0;
        var fails = new Dictionary<string, int>();
        string? firstDump = null;
        for (int iter = 0; iter < 30000; iter++)
        {
            int qi = rng.Next(4), q = rng.Next(1, 256), tx = rng.Next(19), txType = RandomTxType(rng, tx), plane = rng.Next(3);
            int sharp = rng.Next(4) == 0 ? rng.Next(1, 8) : 0;
            bool chromaMult = rng.Next(2) == 0;
            var scan = LibaomScan(tx, txType);
            int n = scan.Length, logScale = AomQuantize.TxScale(tx);
            int dq1 = quants.Dequant[0, q, 1];
            // coefficients spread around the quantizer step so levels land near the rounding boundaries
            var coeff = new int[n];
            int active = rng.Next(1, n + 1);
            for (int i = 0; i < active; i++)
                coeff[scan[i]] = (rng.Next(2) == 0 ? 1 : -1) * (int)(rng.NextDouble() * rng.NextDouble() * (rng.Next(3) == 0 ? 40 : 4) * dq1 * (1 << logScale));
            var qc = new int[n]; var dqc = new int[n];
            int eob = AomQuantize.QuantizeFpAvx2(coeff, n, IScanOf(scan), quants.RoundFp[0, q, 0], quants.RoundFp[0, q, 1],
                quants.QuantFp[0, q, 0], quants.QuantFp[0, q, 1], quants.Dequant[0, q, 0], quants.Dequant[0, q, 1], logScale, qc, dqc);
            if (eob == 0) continue;
            int rdmult = AomRd.RdMultKeyFrame(q, 8);
            int skipCtx = plane == 0 ? rng.Next(7) : rng.Next(7, 13), dcCtx = rng.Next(3);
            var qcA = (int[])qc.Clone(); var dqcA = (int[])dqc.Clone(); var cA = (int[])coeff.Clone(); var qcIn = (int[])qc.Clone(); var dqcIn = (int[])dqc.Clone();
            short[] deq = { quants.Dequant[0, q, 0], quants.Dequant[0, q, 1] };
            int rateA, eobA;
            unsafe
            {
                fixed (int* c = flat[qi]) fixed (int* pc = cA) fixed (int* pq = qcA) fixed (int* pd = dqcA) fixed (short* pdq = deq)
                    eobA = Native.twin_optimize_txb(c, plane, tx, txType, skipCtx, dcCtx, pc, pq, pd, eob, pdq, rdmult, sharp, chromaMult ? 1 : 0, &rateA);
            }
            int eobB = AomTxb.OptimizeTxb(costsByQ[qi], tx, txType, plane == 0 ? 0 : 1, false, new AomTxbCtx { TxbSkipCtx = skipCtx, DcSignCtx = dcCtx },
                coeff, qc, dqc, eob, deq[0], deq[1], rdmult, 8, sharp, chromaMult, false, 0, scan, out int rateB);
            checkedN++;
            if (eobA != eobB || rateA != rateB || !qc.AsSpan().SequenceEqual(qcA) || !dqc.AsSpan().SequenceEqual(dqcA))
            {
                string key = $"tx{tx} sharp{(sharp > 0 ? 1 : 0)} cls{AomTxb.TxTypeToClass[txType]} " + (eobA != eobB ? "eob" : rateA != rateB ? "rate" : "levels");
                fails[key] = fails.GetValueOrDefault(key) + 1;
                if (firstDump == null && Environment.GetEnvironmentVariable("AOMTWIN_CASE") is { } casePath)
                    File.WriteAllLines(casePath, new[] {
                        $"{qi} {plane} {tx} {txType} {skipCtx} {dcCtx} {eob} {deq[0]} {deq[1]} {rdmult} {sharp} {(chromaMult ? 1 : 0)}",
                        string.Join(" ", coeff), string.Join(" ", qcIn), string.Join(" ", dqcIn), string.Join(" ", qcA), string.Join(" ", dqcA), $"{rateA} {eobA}" });
                if (tx == 0 && firstDump == null)
                    firstDump = $"type {txType} plane {plane} q {q} rdmult {rdmult} eob {eob}->{eobB}/{eobA} rate {rateB}/{rateA} coeff [{string.Join(",", coeff)}] " +
                        $"qin [{string.Join(",", qcIn)}] ours [{string.Join(",", qc)}] libaom [{string.Join(",", qcA)}] scan [{string.Join(",", scan)}] ctx {skipCtx}/{dcCtx}";
            }
        }
        if (firstDump != null && Environment.GetEnvironmentVariable("AOMTWIN_DUMP") is { } dumpPath) File.WriteAllText(dumpPath, firstDump);
        await Assert.That(firstDump ?? "").IsEqualTo("");
        await Assert.That(string.Join("; ", fails.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value}"))).IsEqualTo("");
        await Assert.That(checkedN).IsGreaterThan(10000);
    }

    [Test]
    public async Task DefaultModeCosts()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        foreach (bool fi in new[] { true, false })
        {
            var buf = new int[20000]; var offs = new int[17];
            unsafe { fixed (int* p = buf) fixed (int* o = offs) Native.twin_default_mode_costs(fi ? 1 : 0, p, buf.Length, o); }
            var fc = new Av1CdfContext();
            Av1CdfDefaults.InitializeMode(fc.Mode);
            Av1CdfDefaults.InitializeKfym(fc.Kfym);
            var mc = new AomModeCosts();
            AomModeCostFill.Fill(mc, fc, fi);
            var errors = new List<string>();
            void Cmp(string name, int field, int[] ours, int count, Func<int, bool>? used = null)
            {
                for (int k = 0; k < count; k++)
                {
                    if (used != null && !used(k)) continue;
                    if (ours[k] != buf[offs[field] + k]) { errors.Add($"{name}[{k}] ours {ours[k]} libaom {buf[offs[field] + k]}"); return; }
                }
            }
            Cmp("partition", 0, mc.PartitionCost, 200, k => (k % 10) < ((k / 10) >> 2 == 0 ? 4 : (k / 10) >> 2 == 4 ? 8 : 10));
            Cmp("skip_txfm", 1, mc.SkipTxfmCost, 6);
            Cmp("y_mode", 2, mc.YModeCosts, 13 * 13 * 13, k => k / 169 < 5 && (k / 13) % 13 < 5);
            Cmp("intra_uv_mode", 3, mc.IntraUvModeCost, 2 * 13 * 14, k => k / (13 * 14) == 1 || k % 14 < 13);
            Cmp("filter_intra_mode", 4, mc.FilterIntraModeCost, 5);
            Cmp("filter_intra", 5, mc.FilterIntraCost, 44, k => AomModeCostFill.FilterIntraAllowedBsize(fi, k / 2));
            Cmp("palette_y_size", 6, mc.PaletteYSizeCost, 49);
            Cmp("palette_uv_size", 7, mc.PaletteUvSizeCost, 49);
            Cmp("palette_y_mode", 8, mc.PaletteYModeCost, 42);
            Cmp("palette_uv_mode", 9, mc.PaletteUvModeCost, 4);
            Cmp("palette_y_color", 10, mc.PaletteYColorCost, 280, k => k % 8 < k / 40 + 2);
            Cmp("palette_uv_color", 11, mc.PaletteUvColorCost, 280, k => k % 8 < k / 40 + 2);
            Cmp("cfl", 12, mc.CflCost, 256);
            Cmp("tx_size", 13, mc.TxSizeCost, 60, k => k % 5 < (k / 15 == 0 ? 2 : 3));
            Cmp("intra_tx_type", 14, mc.IntraTxTypeCosts, 3 * 4 * 13 * 16, k => AomTables.UseIntraExtTxForTxsize[(k / (13 * 16 * 4)) * 4 + (k / (13 * 16)) % 4] != 0);
            Cmp("angle_delta", 15, mc.AngleDeltaCost, 56);
            Cmp("intrabc", 16, mc.IntrabcCost, 2);
            await Assert.That(string.Join("; ", errors)).IsEqualTo("");
        }
    }
}
