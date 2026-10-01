using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twins of the libaom port's high bit depth kernels (AomHbd*.cs, AomQuant.Hbd.cs, AomEncodeMb.Hbd.cs, ...) against
// libaom 3.14.1's RTCD-dispatched ones through aomtwin_hbd.dll (scratchpad aomtwin_hbd/twin_hbd.c). Opt-in: point
// SHARPIMAGE_AOMTWIN_HBD at aomtwin_hbd.dll; without it the tests pass without checking.
[NotInParallel]
public sealed partial class AomHbdTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_HBD");
    private static readonly bool Available = DllPath != null && (File.Exists(DllPath)
        ? Load() : throw new FileNotFoundException("SHARPIMAGE_AOMTWIN_HBD is set but the DLL does not exist", DllPath));

    private static bool Load()
    {
        NativeLibrary.SetDllImportResolver(typeof(AomHbdTwinTests).Assembly, (name, _, _) =>
            name == "aomtwin_hbd" ? NativeLibrary.Load(DllPath!) : IntPtr.Zero);
        Native.twin_init();
        return true;
    }

    private static unsafe partial class Native
    {
        private const string D = "aomtwin_hbd";
        [DllImport(D)] public static extern void twin_init();
        [DllImport(D)] public static extern void twin_fwd_txfm(short* diff, int stride, int* coeff, int txSize, int txType, int bd, int lossless);
        [DllImport(D)] public static extern void twin_inv_txfm_add(int* dq, ushort* dst, int stride, int txSize, int txType, int eob, int bd,
            int lossless, int isInter);
        [DllImport(D)] public static extern int twin_quantize_fp(int* coeff, int n, int txSize, int txType, short* quant2, short* dequant2,
            short* round2, int logScale, int* qcoeff, int* dqcoeff);
        [DllImport(D)] public static extern int twin_quantize_b(int* coeff, int n, int txSize, int txType, short* zbin2, short* round2,
            short* quant2, short* shift2, short* dequant2, int logScale, int* qcoeff, int* dqcoeff);
        [DllImport(D)] public static extern uint twin_variance(int bd, int bsize, ushort* a, int astr, ushort* b, int bstr, uint* sse);
        [DllImport(D)] public static extern long twin_sse(ushort* a, int astr, ushort* b, int bstr, int w, int h);
        [DllImport(D)] public static extern long twin_block_error(int* coeff, int* dq, int n, long* ssz, int bd);
        [DllImport(D)] public static extern void twin_subtract(int rows, int cols, short* diff, int ds, ushort* src, int ss, ushort* pred, int ps);
        [DllImport(D)] public static extern void twin_pred(int kind, int txSize, ushort* dst, int stride, ushort* above, ushort* left, int bd);
        [DllImport(D)] public static extern void twin_dr(int zone, ushort* dst, int stride, int bw, int bh, ushort* above, ushort* left,
            int upAbove, int upLeft, int dx, int dy, int bd);
        [DllImport(D)] public static extern void twin_filter_edge(ushort* p, int sz, int strength);
        [DllImport(D)] public static extern void twin_upsample_edge(ushort* p, int sz, int bd);
    }

    private static int RandomTxType(Random rng, int tx)
    {
        int w = TxSizeWide[tx], h = TxSizeHigh[tx];
        if (Math.Max(w, h) <= 16) return rng.Next(16);
        if (Math.Max(w, h) == 32) return rng.Next(2) == 0 ? DCT_DCT : IDTX;
        return DCT_DCT;
    }

    private static short[] Residual(Random rng, int w, int h, int bd, int mode)
    {
        int max = (1 << bd) - 1;
        var d = new short[w * h];
        for (int i = 0; i < d.Length; i++)
            d[i] = (short)(mode switch
            {
                0 => rng.Next(-max, max + 1),
                1 => rng.Next(2) == 0 ? max : -max,
                2 => (i % 7 == 0 ? 1 : -1) * rng.Next(max / 8),
                _ => rng.Next(-16, 17),
            });
        return d;
    }

    [Test]
    public async Task FwdTxfm_AllSizes_MatchLibaom()
    {
        if (!Available) return;
        var rng = new Random(11);
        int bad = 0;
        string first = "";
        foreach (int bd in new[] { 10, 12 })
            for (int tx = 0; tx < 19; tx++)
                for (int it = 0; it < 60; it++)
                {
                    int w = TxSizeWide[tx], h = TxSizeHigh[tx];
                    int txType = RandomTxType(rng, tx);
                    var diff = Residual(rng, w, h, bd, it % 4);
                    int n = AomEncodeMb.MaxEob(tx);
                    var a = new int[n];
                    var o = new int[n];
                    unsafe { fixed (short* pd = diff) fixed (int* pa = a) Native.twin_fwd_txfm(pd, w, pa, tx, txType, bd, 0); }
                    AomEncodeMb.TxTypeKinds(txType, out int hk, out int vk, out bool fu, out bool fl);
                    Av1FwdTxfmAom.ForwardRawRef(diff, w, w, h, tx, hk, vk, fu, fl, o);
                    if (!a.AsSpan().SequenceEqual(o)) { bad++; if (first == "") first = $"bd {bd} tx {tx} type {txType} mode {it % 4}"; }
                }
        await Assert.That(first).IsEqualTo("");
    }

    [Test]
    public async Task InvTxfm_AllSizes_MatchLibaom()
    {
        if (!Available) return;
        var rng = new Random(12);
        string first = "";
        int cases = 0;
        foreach (int bd in new[] { 10, 12 })
            for (int tx = 0; tx < 19 && first == ""; tx++)
                for (int it = 0; it < 200; it++)
                {
                    int w = TxSizeWide[tx], h = TxSizeHigh[tx];
                    int txType = RandomTxType(rng, tx);
                    int n = AomEncodeMb.MaxEob(tx);
                    // coefficients: a forward transform of a residual, quantised coarsely (realistic), or random extremes
                    var coeff = new int[n];
                    var diff = Residual(rng, w, h, bd, it % 4);
                    AomEncodeMb.TxTypeKinds(txType, out int hk, out int vk, out bool fu, out bool fl);
                    Av1FwdTxfmAom.ForwardRawRef(diff, w, w, h, tx, hk, vk, fu, fl, coeff);
                    int step = 1 << rng.Next(0, 8);
                    for (int i = 0; i < n; i++) coeff[i] = coeff[i] / step * step;
                    // (random coefficients over the whole spec range: intermediate overflows clamp differently in libaom's SIMD
                    // and in the dav1d-style kernels; the encoder's coefficients never get there)
                    // eob: one past the last nonzero in scan order (zero the tail beyond a random cut)
                    var scan = AomEncodeMb.ScanOf(tx, txType);
                    int cut = it % 3 == 0 ? rng.Next(1, n + 1) : n;
                    for (int i = cut; i < n; i++) coeff[scan[i]] = 0;
                    int eob = 0;
                    for (int i = 0; i < n; i++) if (coeff[scan[i]] != 0) eob = i + 1;
                    if (eob == 0) continue;
                    int stride = w + 8;
                    var dst0 = new ushort[stride * h];
                    for (int i = 0; i < dst0.Length; i++) dst0[i] = (ushort)rng.Next(1 << bd);
                    var a = (ushort[])dst0.Clone();
                    var o = (ushort[])dst0.Clone();
                    var ca = (int[])coeff.Clone();
                    unsafe { fixed (int* pc = ca) fixed (ushort* pa = a) Native.twin_inv_txfm_add(pc, pa, stride, tx, txType, eob, bd, 0, 0); }
                    AomEncodeMb.InverseTransformBlock(coeff, 0, txType, tx, o, 0, stride, eob, bd, false);
                    cases++;
                    if (!a.AsSpan().SequenceEqual(o)) { first = $"bd {bd} tx {tx} type {txType} it {it} eob {eob}"; break; }
                }
        // lossless 4x4
        foreach (int bd in new[] { 10, 12 })
            for (int it = 0; it < 200 && first == ""; it++)
            {
                var coeff = new int[16];
                for (int i = 0; i < 16; i++) coeff[i] = rng.Next(-(1 << (bd + 2)), 1 << (bd + 2)) >> rng.Next(0, 10);
                int eob = it % 4 == 0 ? 1 : 16;
                if (eob == 1) for (int i = 1; i < 16; i++) coeff[i] = 0;
                var dst0 = new ushort[8 * 4];
                for (int i = 0; i < dst0.Length; i++) dst0[i] = (ushort)rng.Next(1 << bd);
                var a = (ushort[])dst0.Clone();
                var o = (ushort[])dst0.Clone();
                unsafe { fixed (int* pc = coeff) fixed (ushort* pa = a) Native.twin_inv_txfm_add(pc, pa, 8, TX_4X4, DCT_DCT, eob, bd, 1, 0); }
                AomEncodeMb.InverseTransformBlock(coeff, 0, DCT_DCT, TX_4X4, o, 0, 8, eob, bd, true);
                if (!a.AsSpan().SequenceEqual(o)) first = $"lossless bd {bd} it {it}";
            }
        await Assert.That(first).IsEqualTo("");
        await Assert.That(cases).IsGreaterThan(1000);
    }

    [Test]
    public async Task Quantizers_MatchLibaom()
    {
        if (!Available) return;
        var rng = new Random(13);
        string first = "";
        foreach (int bd in new[] { 10, 12 })
        {
            var quants = new AomQuants(bd, 0, 0, 0, 0, 0, rng.Next(2) * 3);
            for (int it = 0; it < 3000 && first == ""; it++)
            {
                int tx = rng.Next(19);
                int txType = RandomTxType(rng, tx);
                int n = AomEncodeMb.MaxEob(tx);
                int q = rng.Next(256), pl = rng.Next(3);
                var coeff = new int[n];
                int mag = rng.Next(4, bd + 10);
                for (int i = 0; i < n; i++) coeff[i] = rng.Next(4) == 0 ? 0 : rng.Next(-(1 << mag), 1 << mag) >> rng.Next(0, mag);
                int ls = AomQuantize.TxScale(tx);
                short[] z = { quants.Zbin[pl, q, 0], quants.Zbin[pl, q, 1] }, r = { quants.Round[pl, q, 0], quants.Round[pl, q, 1] },
                    qq = { quants.Quant[pl, q, 0], quants.Quant[pl, q, 1] }, sh = { quants.QuantShift[pl, q, 0], quants.QuantShift[pl, q, 1] },
                    dq = { quants.Dequant[pl, q, 0], quants.Dequant[pl, q, 1] }, qf = { quants.QuantFp[pl, q, 0], quants.QuantFp[pl, q, 1] },
                    rf = { quants.RoundFp[pl, q, 0], quants.RoundFp[pl, q, 1] };
                var iscan = AomEncodeMb.IScanOf(tx, txType);
                var qa = new int[n]; var dqa = new int[n]; var qo = new int[n]; var dqo = new int[n];
                int ea, eo;
                unsafe
                {
                    fixed (int* pc = coeff) fixed (int* pq = qa) fixed (int* pdq = dqa) fixed (short* pqf = qf) fixed (short* pdqv = dq) fixed (short* prf = rf)
                        ea = Native.twin_quantize_fp(pc, n, tx, txType, pqf, pdqv, prf, ls, pq, pdq);
                }
                eo = AomQuantizeHbd.QuantizeFp(coeff, n, iscan, rf[0], rf[1], qf[0], qf[1], dq[0], dq[1], ls, qo, dqo);
                if (ea != eo || !qa.AsSpan().SequenceEqual(qo) || !dqa.AsSpan().SequenceEqual(dqo)) { first = $"fp bd {bd} tx {tx} q {q} eob {ea}/{eo}"; break; }
                unsafe
                {
                    fixed (int* pc = coeff) fixed (int* pq = qa) fixed (int* pdq = dqa) fixed (short* pz = z) fixed (short* pr = r) fixed (short* pqq = qq)
                    fixed (short* psh = sh) fixed (short* pdqv = dq)
                        ea = Native.twin_quantize_b(pc, n, tx, txType, pz, pr, pqq, psh, pdqv, ls, pq, pdq);
                }
                eo = AomQuantizeHbd.QuantizeB(coeff, n, iscan, z[0], z[1], r[0], r[1], qq[0], qq[1], sh[0], sh[1], dq[0], dq[1], ls, qo, dqo);
                if (ea != eo || !qa.AsSpan().SequenceEqual(qo) || !dqa.AsSpan().SequenceEqual(dqo)) { first = $"b bd {bd} tx {tx} q {q} eob {ea}/{eo}"; break; }
            }
        }
        await Assert.That(first).IsEqualTo("");
    }

    [Test]
    public async Task Distortion_MatchLibaom()
    {
        if (!Available) return;
        var rng = new Random(14);
        string first = "";
        foreach (int bd in new[] { 10, 12 })
            for (int it = 0; it < 4000 && first == ""; it++)
            {
                int bs = rng.Next(22);
                int w = BlockSizeWide[bs], h = BlockSizeHigh[bs];
                int stride = 136;
                var a = new ushort[stride * 130];
                var b = new ushort[stride * 130];
                int mode = it % 4;
                for (int i = 0; i < a.Length; i++)
                {
                    a[i] = (ushort)(mode == 0 ? rng.Next(1 << bd) : mode == 1 ? ((1 << bd) - 1) * rng.Next(2) : 512 + rng.Next(-20, 21));
                    b[i] = (ushort)(mode == 3 ? 0 : mode == 0 ? rng.Next(1 << bd) : (1 << bd) - 1 - a[i]);
                }
                uint sa, so;
                uint va;
                unsafe { fixed (ushort* pa = a) fixed (ushort* pb = b) va = Native.twin_variance(bd, bs, pa, stride, pb, stride, &sa); }
                uint vo = AomHbd.Variance(a, 0, stride, b, 0, stride, 0, w, h, bd, out so);
                if (va != vo || sa != so) { first = $"var bd {bd} bs {bs} mode {mode}: {va}/{sa} vs {vo}/{so}"; break; }
                int sw = rng.Next(1, 33) * 4 > 128 ? 128 : new[] { 4, 8, 16, 32, 64, 128, 12, 24, 20 }[rng.Next(9)], shh = rng.Next(1, 33) * 4;
                long ea, eo;
                unsafe { fixed (ushort* pa = a) fixed (ushort* pb = b) ea = Native.twin_sse(pa, stride, pb, stride, sw, shh); }
                eo = AomHbd.Sse(a, 0, stride, b, 0, stride, sw, shh);
                if (ea != eo) { first = $"sse bd {bd} {sw}x{shh} mode {mode}: {ea} vs {eo}"; break; }
                int n = 16 << rng.Next(0, 7);
                var c = new int[n]; var d = new int[n];
                for (int i = 0; i < n; i++) { c[i] = rng.Next(-(1 << (bd + 9)), 1 << (bd + 9)); d[i] = c[i] + rng.Next(-5000, 5000); }
                long za, zo;
                unsafe { fixed (int* pc = c) fixed (int* pd = d) ea = Native.twin_block_error(pc, pd, n, &za, bd); }
                eo = AomHbd.BlockError(c, d, n, out zo, bd);
                if (ea != eo || za != zo) { first = $"block error bd {bd} n {n}"; break; }
                var da = new short[64 * 64]; var dd = new short[64 * 64];
                int sbs = rng.Next(22);
                if (BlockSizeWide[sbs] > 64 || BlockSizeHigh[sbs] > 64) sbs = BLOCK_64X64;
                int rows = BlockSizeHigh[sbs], cols = BlockSizeWide[sbs];   // (the SSE2 kernel only has the block sizes)
                unsafe { fixed (short* pd = da) fixed (ushort* pa = a) fixed (ushort* pb = b) Native.twin_subtract(rows, cols, pd, 64, pa, stride, pb, stride); }
                AomHbd.SubtractBlock(rows, cols, dd, 0, 64, a, 0, stride, b, 0, stride);
                if (!da.AsSpan().SequenceEqual(dd)) { first = $"subtract {rows}x{cols}"; break; }
            }
        await Assert.That(first).IsEqualTo("");
    }

    private static ushort[] Edge(Random rng, int bd, int mode)
    {
        var e = new ushort[512];
        int max = (1 << bd) - 1;
        int b = rng.Next(1 << bd);
        for (int i = 0; i < e.Length; i++)
            e[i] = (ushort)(mode switch { 0 => rng.Next(1 << bd), 1 => rng.Next(2) * max, _ => Math.Clamp(b + rng.Next(-30, 31), 0, max) });
        return e;
    }

    [Test]
    public unsafe Task IntraPred_MatchLibaom()
    {
        if (!Available) return Task.CompletedTask;
        var rng = new Random(15);
        foreach (int bd in new[] { 10, 12 })
            for (int it = 0; it < 3000; it++)
            {
                int tx = rng.Next(19);
                int bw = TxSizeWide[tx], bh = TxSizeHigh[tx];
                var above = Edge(rng, bd, it % 3);
                var left = Edge(rng, bd, it % 3);
                int kind = rng.Next(10);
                var a = new ushort[64 * 64];
                var o = new ushort[64 * 64];
                fixed (ushort* pa = a) fixed (ushort* po = o) fixed (ushort* ab = above) fixed (ushort* le = left)
                {
                    Native.twin_pred(kind, tx, pa, 64, ab + 64, le + 64, bd);
                    if (kind < 4) AomIntraPredHbd.DcPred(kind >> 1, kind & 1, tx, po, 64, ab + 64, le + 64, bd);
                    else AomIntraPredHbd.Pred(new[] { V_PRED, H_PRED, SMOOTH_PRED, 10, 11, PAETH_PRED }[kind - 4], tx, po, 64, ab + 64, le + 64, bd);
                }
                if (!a.AsSpan().SequenceEqual(o)) throw new Exception($"pred kind {kind} tx {tx} bd {bd}");
                // directional zones (upsampled edges only where libaom upsamples: w + h <= 16)
                int angle = rng.Next(1, 270);
                if (angle % 90 == 0) angle++;
                int zone = angle < 90 ? 1 : angle < 180 ? 2 : 3;
                bool small = bw + bh <= 16;
                int upA = small && zone != 3 && rng.Next(2) == 0 ? 1 : 0, upL = small && zone != 1 && rng.Next(2) == 0 ? 1 : 0;
                int dx = AomReconIntra.GetDx(angle), dy = AomReconIntra.GetDy(angle);
                Array.Clear(a); Array.Clear(o);
                fixed (ushort* pa = a) fixed (ushort* po = o) fixed (ushort* ab = above) fixed (ushort* le = left)
                {
                    Native.twin_dr(zone, pa, 64, bw, bh, ab + 64, le + 64, upA, upL, dx, dy, bd);
                    if (zone == 1) AomReconIntra.HighbdDrPredictionZ1(po, 64, bw, bh, ab + 64, upA, dx);
                    else if (zone == 2) AomReconIntra.HighbdDrPredictionZ2(po, 64, bw, bh, ab + 64, le + 64, upA, upL, dx, dy);
                    else AomReconIntra.HighbdDrPredictionZ3(po, 64, bw, bh, le + 64, upL, dy);
                }
                if (!a.AsSpan().SequenceEqual(o)) throw new Exception($"dr zone {zone} angle {angle} {bw}x{bh} up {upA}{upL} bd {bd}");
                // edge filter / upsample with their side effects
                int sz = rng.Next(1, 130);
                int strength = rng.Next(4);
                var ea = Edge(rng, bd, it % 3);
                var eo = (ushort[])ea.Clone();
                fixed (ushort* pa = ea) fixed (ushort* po = eo) { Native.twin_filter_edge(pa + 64, sz, strength); AomReconIntra.HighbdFilterIntraEdge(po + 64, sz, strength); }
                if (!ea.AsSpan().SequenceEqual(eo)) throw new Exception($"filter edge sz {sz} strength {strength} bd {bd}");
                int usz = new[] { 4, 8, 12, 16 }[rng.Next(4)];
                ea = Edge(rng, bd, it % 3);
                eo = (ushort[])ea.Clone();
                fixed (ushort* pa = ea) fixed (ushort* po = eo) { Native.twin_upsample_edge(pa + 64, usz, bd); AomReconIntra.HighbdUpsampleIntraEdge(po + 64, usz, bd); }
                if (!ea.AsSpan().SequenceEqual(eo)) throw new Exception($"upsample sz {usz} bd {bd}");
            }
        return Task.CompletedTask;
    }
}
