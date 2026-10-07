using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twins of the libaom intra prediction port (AomIntraPred / AomReconIntra / AomCfl) against libaom 3.14.1 through
// aomtwin_pred.dll (tests/native/aomtwin: twin_pred.c exports the RTCD-dispatched SIMD kernels, twin_recon.c
// compiles a copy of reconintra.c for its statics and drives av1_predict_intra_block_facade on a synthetic MACROBLOCKD).
// Opt-in: point SHARPIMAGE_AOMTWIN_PRED at aomtwin_pred.dll; without it the tests report Skipped.
[NotInParallel]
public sealed class AomPredTwinTests
{
    private const string EnvVar = "SHARPIMAGE_AOMTWIN_PRED";
    private static readonly string? DllPath = AomTwinNative.PathFromEnv(EnvVar);
    private static readonly bool Available = DllPath != null && Load();

    private static bool Load()
    {
        AomTwinNative.Register("aomtwin_pred", DllPath!);
        Native.twin_init();
        return true;
    }

    private static unsafe class Native
    {
        private const string D = "aomtwin_pred";
        [DllImport(D)] public static extern void twin_init();
        [DllImport(D)] public static extern void twin_pred(int kind, int txSize, byte* dst, int stride, byte* above, byte* left);
        [DllImport(D)] public static extern void twin_dr_z1(byte* dst, int stride, int bw, int bh, byte* above, byte* left, int upA, int dx, int dy);
        [DllImport(D)] public static extern void twin_dr_z2(byte* dst, int stride, int bw, int bh, byte* above, byte* left, int upA, int upL, int dx, int dy);
        [DllImport(D)] public static extern void twin_dr_z3(byte* dst, int stride, int bw, int bh, byte* above, byte* left, int upL, int dx, int dy);
        [DllImport(D)] public static extern void twin_filter_intra(byte* dst, int stride, int txSize, byte* above, byte* left, int mode);
        [DllImport(D)] public static extern void twin_filter_edge(byte* p, int sz, int strength);
        [DllImport(D)] public static extern void twin_upsample_edge(byte* p, int sz);
        [DllImport(D)] public static extern int twin_get_dx(int angle);
        [DllImport(D)] public static extern int twin_get_dy(int angle);
        [DllImport(D)] public static extern int twin_use_upsample(int bs0, int bs1, int delta, int type);
        [DllImport(D)] public static extern void twin_cfl_subsample(int sub, int txSize, byte* input, int stride, ushort* outQ3);
        [DllImport(D)] public static extern void twin_cfl_subtract_average(int txSize, ushort* src, short* dst);
        [DllImport(D)] public static extern void twin_cfl_predict(int txSize, short* ac, byte* dst, int stride, int alphaQ3);
        [DllImport(D)] public static extern int twin_has_top_right(int sbSize, int bsize, int miRow, int miCol, int top, int right,
            int partition, int txsz, int rowOff, int colOff, int ssX, int ssY);
        [DllImport(D)] public static extern int twin_has_bottom_left(int sbSize, int bsize, int miRow, int miCol, int bottom, int left,
            int partition, int txsz, int rowOff, int colOff, int ssX, int ssY);
        [DllImport(D)] public static extern int twin_edge_strength(int bs0, int bs1, int delta, int type);
        [DllImport(D)] public static extern void twin_edge_corner(byte* pAbove, byte* pLeft);
        [DllImport(D)] public static extern int twin_scale_chroma_bsize(int bsize, int ssx, int ssy);
        [DllImport(D)] public static extern void twin_e2e_setup(int miRows, int miCols, int* cellinfo, int tileR0, int tileR1, int tileC0,
            int tileC1, int ssx, int ssy, int sbSize, int enableEdgeFilter, int miRow, int miCol, int* curinfo, ushort* palette,
            byte** planes, int* strides, int* rows, int* dstOff, byte* cmap0, byte* cmap1, int* derived);
        [DllImport(D)] public static extern void twin_e2e_facade(int plane, int blkCol, int blkRow, int txSize);
        [DllImport(D)] public static extern void twin_e2e_set_cfl(int useDcPredCache, int alphaIdx, int alphaSigns);
        [DllImport(D)] public static extern void twin_e2e_clear_cfl_cache();
        [DllImport(D)] public static extern void twin_e2e_cfl_store_block(int bsize, int txSize);
        [DllImport(D)] public static extern void twin_e2e_cfl_store_tx(int row, int col, int txSize, int bsize);
        [DllImport(D)] public static extern void twin_e2e_predict(int plane, int blkCol, int blkRow, int txSize, int mode, int angleDelta,
            int usePalette, int filterIntraMode);
        [DllImport(D)] public static extern void twin_e2e_get_plane(int p, byte* outp, int n);
        [DllImport(D)] public static extern void twin_e2e_get_cfl(ushort* recon, short* ac, int* ints);
    }

    private const int SMOOTH_V_PRED = 10, SMOOTH_H_PRED = 11;

    private static void RandomEdge(Random rng, byte[] b)
    {
        int kind = rng.Next(6);
        for (int i = 0; i < b.Length; i++)
            b[i] = kind switch
            {
                0 => (byte)rng.Next(256),
                1 => (byte)(rng.Next(2) * 255),
                2 => (byte)(128 + rng.Next(-4, 5)),
                3 => (byte)Math.Clamp(i * 3 + rng.Next(-10, 11), 0, 255),
                4 => (byte)rng.Next(240, 256),
                _ => (byte)rng.Next(0, 16),
            };
    }

    private static string Diff(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return $"first diff at {i}: ours {a[i]} libaom {b[i]}";
        return "equal";
    }

    [Test]
    public async Task Predictors_AllKindsAllSizes()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(11);
        const int stride = 80;
        byte[] aboveBuf = new byte[256], leftBuf = new byte[256];
        byte[] ours = new byte[stride * 64], theirs = new byte[stride * 64];
        for (int kind = 0; kind < 10; kind++)
            for (int tx = 0; tx < TX_SIZES_ALL; tx++)
                for (int it = 0; it < 150; it++)
                {
                    RandomEdge(rng, aboveBuf); RandomEdge(rng, leftBuf);
                    rng.NextBytes(ours); Array.Copy(ours, theirs, ours.Length);
                    unsafe
                    {
                        fixed (byte* a = aboveBuf) fixed (byte* l = leftBuf) fixed (byte* d0 = ours) fixed (byte* d1 = theirs)
                        {
                            Native.twin_pred(kind, tx, d1, stride, a + 32, l + 32);
                            switch (kind)
                            {
                                case 0: AomIntraPred.DcPred(0, 0, tx, d0, stride, a + 32, l + 32); break;
                                case 1: AomIntraPred.DcPred(0, 1, tx, d0, stride, a + 32, l + 32); break;
                                case 2: AomIntraPred.DcPred(1, 0, tx, d0, stride, a + 32, l + 32); break;
                                case 3: AomIntraPred.DcPred(1, 1, tx, d0, stride, a + 32, l + 32); break;
                                default:
                                    int mode = new[] { V_PRED, H_PRED, SMOOTH_PRED, SMOOTH_V_PRED, SMOOTH_H_PRED, PAETH_PRED }[kind - 4];
                                    AomIntraPred.Pred(mode, tx, d0, stride, a + 32, l + 32);
                                    break;
                            }
                        }
                    }
                    if (!ours.AsSpan().SequenceEqual(theirs))
                    {
                        await Assert.That($"kind {kind} tx {tx}: {Diff(ours, theirs)}").IsEqualTo("equal");
                        return;
                    }
                }
    }

    // every angle av1 can use: base angles of the directional modes +- 0..3 * ANGLE_STEP
    private static IEnumerable<int> AllAngles()
    {
        var s = new SortedSet<int>();
        foreach (int b in new[] { 45, 67, 90, 113, 135, 157, 180, 203 })
            for (int d = -3; d <= 3; d++) s.Add(b + d * 3);
        return s;
    }

    [Test]
    public async Task Directional_Z1Z2Z3_AllAnglesSizesUpsample()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(12);
        const int stride = 80;
        byte[] aboveBuf = new byte[256], leftBuf = new byte[256];
        byte[] ours = new byte[stride * 64], theirs = new byte[stride * 64];
        for (int a = 1; a < 270; a++)
        {
            if (AomReconIntra.GetDx(a) != Native.twin_get_dx(a) || AomReconIntra.GetDy(a) != Native.twin_get_dy(a))
                await Assert.That($"dx/dy angle {a}").IsEqualTo("equal");
        }
        int n = 0;
        foreach (int angle in AllAngles())
        {
            if (angle == 90 || angle == 180) continue;
            int dx = AomReconIntra.GetDx(angle), dy = AomReconIntra.GetDy(angle);
            for (int tx = 0; tx < TX_SIZES_ALL; tx++)
            {
                int bw = TxSizeWide[tx], bh = TxSizeHigh[tx];
                for (int upA = 0; upA < 2; upA++)
                    for (int upL = 0; upL < 2; upL++)
                    {
                        // upsampling is only ever used for bw + bh <= 16
                        if ((upA != 0 || upL != 0) && bw + bh > 16) continue;
                        for (int it = 0; it < 12; it++, n++)
                        {
                            RandomEdge(rng, aboveBuf); RandomEdge(rng, leftBuf);
                            rng.NextBytes(ours); Array.Copy(ours, theirs, ours.Length);
                            unsafe
                            {
                                fixed (byte* ab = aboveBuf) fixed (byte* lb = leftBuf) fixed (byte* d0 = ours) fixed (byte* d1 = theirs)
                                {
                                    byte* above = ab + 32, left = lb + 32;
                                    if (angle < 90)
                                    {
                                        Native.twin_dr_z1(d1, stride, bw, bh, above, left, upA, dx, dy);
                                        AomReconIntra.DrPredictionZ1(d0, stride, bw, bh, above, left, upA, dx, dy);
                                    }
                                    else if (angle < 180)
                                    {
                                        Native.twin_dr_z2(d1, stride, bw, bh, above, left, upA, upL, dx, dy);
                                        AomReconIntra.DrPredictionZ2(d0, stride, bw, bh, above, left, upA, upL, dx, dy);
                                    }
                                    else
                                    {
                                        Native.twin_dr_z3(d1, stride, bw, bh, above, left, upL, dx, dy);
                                        AomReconIntra.DrPredictionZ3(d0, stride, bw, bh, above, left, upL, dx, dy);
                                    }
                                }
                            }
                            if (!ours.AsSpan().SequenceEqual(theirs))
                            {
                                await Assert.That($"angle {angle} tx {tx} up {upA}{upL}: {Diff(ours, theirs)}").IsEqualTo("equal");
                                return;
                            }
                        }
                    }
            }
        }
        await Assert.That(n).IsGreaterThan(1000);
    }

    [Test]
    public async Task FilterIntra_AllModesSizes()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(13);
        const int stride = 48;
        byte[] aboveBuf = new byte[256], leftBuf = new byte[256];
        byte[] ours = new byte[stride * 40], theirs = new byte[stride * 40];
        for (int mode = 0; mode < FILTER_INTRA_MODES; mode++)
            for (int tx = 0; tx < TX_SIZES_ALL; tx++)
            {
                if (TxSizeWide[tx] > 32 || TxSizeHigh[tx] > 32) continue;
                for (int it = 0; it < 300; it++)
                {
                    RandomEdge(rng, aboveBuf); RandomEdge(rng, leftBuf);
                    rng.NextBytes(ours); Array.Copy(ours, theirs, ours.Length);
                    unsafe
                    {
                        fixed (byte* ab = aboveBuf) fixed (byte* lb = leftBuf) fixed (byte* d0 = ours) fixed (byte* d1 = theirs)
                        {
                            Native.twin_filter_intra(d1, stride, tx, ab + 32, lb + 32, mode);
                            AomReconIntra.FilterIntraPredictor(d0, stride, tx, ab + 32, lb + 32, mode);
                        }
                    }
                    if (!ours.AsSpan().SequenceEqual(theirs))
                    {
                        await Assert.That($"filter intra mode {mode} tx {tx}: {Diff(ours, theirs)}").IsEqualTo("equal");
                        return;
                    }
                }
            }
    }

    [Test]
    public async Task IntraEdge_FilterUpsampleCornerStrength()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(14);
        byte[] a = new byte[200], b = new byte[200];
        for (int sz = 1; sz <= 129; sz++)
            for (int strength = 0; strength <= 3; strength++)
                for (int it = 0; it < 40; it++)
                {
                    RandomEdge(rng, a); Array.Copy(a, b, a.Length);
                    unsafe
                    {
                        fixed (byte* pa = a) fixed (byte* pb = b)
                        {
                            Native.twin_filter_edge(pb + 16, sz, strength);
                            AomReconIntra.FilterIntraEdge(pa + 16, sz, strength);
                        }
                    }
                    if (!a.AsSpan().SequenceEqual(b)) { await Assert.That($"filter edge sz {sz} s {strength}: {Diff(a, b)}").IsEqualTo("equal"); return; }
                }
        for (int sz = 1; sz <= 16; sz++)
            for (int it = 0; it < 400; it++)
            {
                RandomEdge(rng, a); Array.Copy(a, b, a.Length);
                unsafe
                {
                    fixed (byte* pa = a) fixed (byte* pb = b)
                    {
                        Native.twin_upsample_edge(pb + 16, sz);
                        AomReconIntra.UpsampleIntraEdge(pa + 16, sz);
                    }
                }
                if (!a.AsSpan().SequenceEqual(b)) { await Assert.That($"upsample sz {sz}: {Diff(a, b)}").IsEqualTo("equal"); return; }
            }
        for (int it = 0; it < 2000; it++)
        {
            RandomEdge(rng, a); Array.Copy(a, b, a.Length);
            unsafe
            {
                fixed (byte* pa = a) fixed (byte* pb = b)
                {
                    Native.twin_edge_corner(pb + 20, pb + 100);
                    AomReconIntra.FilterIntraEdgeCorner(pa + 20, pa + 100);
                }
            }
            if (!a.AsSpan().SequenceEqual(b)) { await Assert.That($"corner: {Diff(a, b)}").IsEqualTo("equal"); return; }
        }
        foreach (int bs0 in new[] { 4, 8, 16, 32, 64 })
            foreach (int bs1 in new[] { 4, 8, 16, 32, 64 })
                for (int delta = -100; delta <= 100; delta++)
                    for (int type = 0; type < 2; type++)
                    {
                        if (AomReconIntra.IntraEdgeFilterStrength(bs0, bs1, delta, type) != Native.twin_edge_strength(bs0, bs1, delta, type)
                            || AomReconIntra.UseIntraEdgeUpsample(bs0, bs1, delta, type) != Native.twin_use_upsample(bs0, bs1, delta, type))
                        {
                            await Assert.That($"strength/upsample {bs0} {bs1} {delta} {type}").IsEqualTo("equal");
                            return;
                        }
                    }
        for (int bs = 0; bs < BLOCK_SIZES_ALL; bs++)
            for (int sx = 0; sx < 2; sx++)
                for (int sy = 0; sy < 2; sy++)
                    await Assert.That(AomReconIntra.ScaleChromaBsize(bs, sx, sy)).IsEqualTo(Native.twin_scale_chroma_bsize(bs, sx, sy));
    }

    private static bool VertTableExists(int bsize) =>
        bsize < BLOCK_SIZES && bsize != BLOCK_4X4 && BlockSizeWide[bsize] <= BlockSizeHigh[bsize];

    [Test]
    public async Task HasTopRight_HasBottomLeft_Random()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(15);
        int checkedN = 0;
        for (int it = 0; it < 400000; it++)
        {
            int sb = rng.Next(3) == 0 ? BLOCK_128X128 : BLOCK_64X64;
            int bsize = rng.Next(BLOCK_SIZES_ALL);
            if (BlockSizeWide[bsize] > BlockSizeWide[sb] || BlockSizeHigh[bsize] > BlockSizeHigh[sb]) continue;
            int partition = rng.Next(10);
            if ((partition == PARTITION_VERT_A || partition == PARTITION_VERT_B) && !VertTableExists(bsize)) partition = PARTITION_NONE;
            int ssx = rng.Next(2), ssy = ssx == 1 ? rng.Next(2) : 0;
            int bw = MiSizeWide[bsize], bh = MiSizeHigh[bsize];
            int miRow = rng.Next(0, 3 * MiSizeHigh[sb] / bh) * bh, miCol = rng.Next(0, 3 * MiSizeWide[sb] / bw) * bw;
            int tx = rng.Next(TX_SIZES_ALL);
            int pbw = Math.Max(bw >> ssx, 1), pbh = Math.Max(bh >> ssy, 1);
            int txw = TxSizeWideUnit[tx], txh = TxSizeHighUnit[tx];
            if (txw > pbw || txh > pbh) continue;
            int colOff = rng.Next(pbw / txw) * txw, rowOff = rng.Next(pbh / txh) * txh;
            int a1 = rng.Next(4) == 0 ? 0 : 1, a2 = rng.Next(4) == 0 ? 0 : 1;
            int ours = AomReconIntra.HasTopRight(sb, bsize, miRow, miCol, a1, a2, partition, tx, rowOff, colOff, ssx, ssy);
            int theirs = Native.twin_has_top_right(sb, bsize, miRow, miCol, a1, a2, partition, tx, rowOff, colOff, ssx, ssy);
            int oursB = AomReconIntra.HasBottomLeft(sb, bsize, miRow, miCol, a1, a2, partition, tx, rowOff, colOff, ssx, ssy);
            int theirsB = Native.twin_has_bottom_left(sb, bsize, miRow, miCol, a1, a2, partition, tx, rowOff, colOff, ssx, ssy);
            if (ours != theirs || oursB != theirsB)
            {
                await Assert.That($"sb {sb} bs {bsize} ({miRow},{miCol}) part {partition} tx {tx} off ({rowOff},{colOff}) ss {ssx}{ssy}: tr {ours}/{theirs} bl {oursB}/{theirsB}")
                    .IsEqualTo("equal");
                return;
            }
            checkedN++;
        }
        await Assert.That(checkedN).IsGreaterThan(50000);
    }

    private static readonly int[] CflTxSizes =
    {
        TX_4X4, TX_8X8, TX_16X16, TX_32X32, TX_4X8, TX_8X4, TX_8X16, TX_16X8, TX_16X32, TX_32X16, TX_4X16, TX_16X4, TX_8X32, TX_32X8,
    };

    [Test]
    public async Task Cfl_SubsampleSubtractAveragePredict()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(16);
        const int inStride = 72;
        byte[] input = new byte[inStride * 64];
        ushort[] o0 = new ushort[AomCfl.CFL_BUF_SQUARE], o1 = new ushort[AomCfl.CFL_BUF_SQUARE];
        short[] ac0 = new short[AomCfl.CFL_BUF_SQUARE], ac1 = new short[AomCfl.CFL_BUF_SQUARE];
        byte[] d0 = new byte[inStride * 40], d1 = new byte[inStride * 40];
        foreach (int tx in CflTxSizes)
        {
            for (int sub = 0; sub < 3; sub++)
                for (int it = 0; it < 200; it++)
                {
                    RandomEdge(rng, input);
                    for (int i = 0; i < o0.Length; i++) o0[i] = o1[i] = (ushort)rng.Next(65536);
                    unsafe
                    {
                        fixed (byte* pi = input) fixed (ushort* p0 = o0) fixed (ushort* p1 = o1)
                        {
                            Native.twin_cfl_subsample(sub, tx, pi, inStride, p1);
                            AomCfl.CflSubsamplingLbd(tx, sub == 2 ? 0 : 1, sub == 0 ? 1 : 0, pi, inStride, p0);
                        }
                    }
                    if (!o0.AsSpan().SequenceEqual(o1)) { await Assert.That($"subsample {sub} tx {tx}").IsEqualTo("equal"); return; }
                }
            for (int it = 0; it < 500; it++)
            {
                int max = rng.Next(3) switch { 0 => 2041, 1 => 64, _ => 16 };
                for (int i = 0; i < o0.Length; i++) o0[i] = (ushort)rng.Next(max);
                for (int i = 0; i < ac0.Length; i++) ac0[i] = ac1[i] = (short)rng.Next(-30000, 30000);
                unsafe
                {
                    fixed (ushort* p0 = o0) fixed (short* a0 = ac0) fixed (short* a1 = ac1)
                    {
                        Native.twin_cfl_subtract_average(tx, p0, a1);
                        AomCfl.SubtractAverage(p0, a0, TxSizeWide[tx], TxSizeHigh[tx]);
                    }
                }
                if (!ac0.AsSpan().SequenceEqual(ac1)) { await Assert.That($"subtract average tx {tx}").IsEqualTo("equal"); return; }
            }
            for (int it = 0; it < 500; it++)
            {
                int range = rng.Next(3) switch { 0 => 2040, 1 => 300, _ => 30 };
                for (int i = 0; i < ac0.Length; i++) ac0[i] = (short)rng.Next(-range, range + 1);
                rng.NextBytes(d0); Array.Copy(d0, d1, d0.Length);
                int alpha = rng.Next(-16, 17);
                unsafe
                {
                    fixed (short* a0 = ac0) fixed (byte* p0 = d0) fixed (byte* p1 = d1)
                    {
                        Native.twin_cfl_predict(tx, a0, p1, inStride, alpha);
                        AomCfl.CflPredictLbd(a0, p0, inStride, alpha, TxSizeWide[tx], TxSizeHigh[tx]);
                    }
                }
                if (!d0.AsSpan().SequenceEqual(d1)) { await Assert.That($"cfl predict tx {tx} alpha {alpha}: {Diff(d0, d1)}").IsEqualTo("equal"); return; }
            }
        }
        for (int idx = 0; idx < 256; idx++)
            for (int js = 0; js < 8; js++)
            {
                // cfl_idx_to_alpha is exercised end to end; check its sign decomposition invariants here
                int su = AomCfl.CflSignU(js), sv = AomCfl.CflSignV(js);
                await Assert.That(su * 3 + sv).IsEqualTo(js + 1);
            }
    }

    // ---- end-to-end: av1_predict_intra_block_facade on a synthetic frame --------------------------------------------

    private sealed class Frame
    {
        public const int B = 96;   // libaom frame rows are 32-aligned (its SIMD predictors store aligned)
        public readonly byte[][] Planes = new byte[3][];
        public readonly int[] Stride = new int[3], Rows = new int[3];
    }

    private static int AdjustUvTx(int tx) => tx switch
    {
        TX_64X64 or TX_32X64 or TX_64X32 => TX_32X32,
        TX_16X64 => TX_16X32,
        TX_64X16 => TX_32X16,
        _ => tx,
    };

    private static async Task<bool> ComparePlanes(Frame f, string what)
    {
        for (int p = 0; p < 3; p++)
        {
            var theirs = new byte[f.Planes[p].Length];
            unsafe { fixed (byte* t = theirs) Native.twin_e2e_get_plane(p, t, theirs.Length); }
            if (!f.Planes[p].AsSpan().SequenceEqual(theirs))
            {
                await Assert.That($"{what} plane {p}: {Diff(f.Planes[p], theirs)}").IsEqualTo("equal");
                return false;
            }
        }
        return true;
    }

    private static async Task<bool> CompareCfl(AomCflCtx cfl, string what)
    {
        var recon = new ushort[AomCfl.CFL_BUF_SQUARE];
        var ac = new short[AomCfl.CFL_BUF_SQUARE];
        var ints = new int[5];
        unsafe { fixed (ushort* r = recon) fixed (short* a = ac) fixed (int* i = ints) Native.twin_e2e_get_cfl(r, a, i); }
        string ours = $"{cfl.BufWidth} {cfl.BufHeight} {(cfl.AreParametersComputed ? 1 : 0)} {(cfl.DcPredIsCached[0] ? 1 : 0)} {(cfl.DcPredIsCached[1] ? 1 : 0)}";
        string theirs = $"{ints[0]} {ints[1]} {ints[2]} {ints[3]} {ints[4]}";
        if (ours != theirs || !recon.AsSpan().SequenceEqual(cfl.ReconBufQ3) || !ac.AsSpan().SequenceEqual(cfl.AcBufQ3))
        {
            await Assert.That($"{what}: cfl state {ours} recon/ac equal {recon.AsSpan().SequenceEqual(cfl.ReconBufQ3)}/{ac.AsSpan().SequenceEqual(cfl.AcBufQ3)}")
                .IsEqualTo($"{what}: cfl state {theirs} recon/ac equal True/True");
            return false;
        }
        return true;
    }

    [Test]
    public async Task PredictIntraBlockFacade_RandomFramesAndBlocks()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        try { await FacadeBody(); }
        catch (Exception e) { await Assert.That(e.ToString()).IsEqualTo("no exception"); }
    }

    private static async Task FacadeBody()
    {
        var rng = new Random(17);
        int blocks = 0, txBlocks = 0, cflBlocks = 0;
        for (int iter = 0; iter < 20000; iter++)
        {
            // ---- frame, tiles, superblock
            int sb = rng.Next(5) == 0 ? BLOCK_128X128 : BLOCK_64X64;
            int sbMi = MiSizeWide[sb];
            int miRows = rng.Next(2, 49), miCols = rng.Next(2, 49);
            int ssx = rng.Next(3) == 0 ? 0 : 1, ssy = ssx == 1 && rng.Next(4) != 0 ? 1 : 0;
            int tileR0 = 0, tileC0 = 0, tileR1 = miRows, tileC1 = miCols;
            if (rng.Next(2) == 0 && miRows > sbMi) { tileR0 = sbMi * rng.Next(0, (miRows - 1) / sbMi + 1); tileR1 = Math.Min(miRows, tileR0 + sbMi * rng.Next(1, 3)); }
            if (rng.Next(2) == 0 && miCols > sbMi) { tileC0 = sbMi * rng.Next(0, (miCols - 1) / sbMi + 1); tileC1 = Math.Min(miCols, tileC0 + sbMi * rng.Next(1, 3)); }
            bool edgeFilter = rng.Next(5) != 0;

            // ---- the block
            int bsize;
            do bsize = rng.Next(BLOCK_SIZES_ALL);
            while (BlockSizeWide[bsize] > BlockSizeWide[sb] || BlockSizeHigh[bsize] > BlockSizeHigh[sb]
                   || AomCfl.GetPlaneBlockSize(bsize, ssx, ssy) == BLOCK_INVALID);
            int bw = MiSizeWide[bsize], bh = MiSizeHigh[bsize];
            int nr = (tileR1 - tileR0 + bh - 1) / bh, nc = (tileC1 - tileC0 + bw - 1) / bw;
            // block origin aligned to its own size inside the tile (the tile start is SB aligned)
            int miRow = tileR0 + rng.Next(nr) * bh, miCol = tileC0 + rng.Next(nc) * bw;
            if (miRow >= tileR1 || miCol >= tileC1) continue;
            int partition = rng.Next(10);
            if ((partition == PARTITION_VERT_A || partition == PARTITION_VERT_B) && !VertTableExists(bsize)) partition = PARTITION_NONE;
            bool small = BlockSizeWide[bsize] <= 32 && BlockSizeHigh[bsize] <= 32;
            var cur = new AomMbModeInfo
            {
                Bsize = bsize, Partition = partition, Mode = rng.Next(13), UvMode = rng.Next(14), RefFrame0 = 0, RefFrame1 = -1,
            };
            cur.AngleDelta[0] = (sbyte)rng.Next(-3, 4); cur.AngleDelta[1] = (sbyte)rng.Next(-3, 4);
            if (small && rng.Next(3) == 0) { cur.UseFilterIntra = 1; cur.FilterIntraMode = (byte)rng.Next(5); }
            if (rng.Next(8) == 0) cur.Palette.PaletteSize0 = (byte)rng.Next(2, 9);
            if (rng.Next(8) == 0) cur.Palette.PaletteSize1 = (byte)rng.Next(2, 9);
            if (cur.UvMode == UV_CFL_PRED && !small) cur.UvMode = rng.Next(13);
            if (rng.Next(4) == 0 && small) { cur.UvMode = UV_CFL_PRED; cur.Palette.PaletteSize1 = 0; }
            cur.CflAlphaIdx = (byte)rng.Next(256); cur.CflAlphaSigns = (sbyte)rng.Next(8);
            for (int i = 0; i < 24; i++) cur.Palette.PaletteColors[i] = (ushort)rng.Next(256);

            // ---- the mi grid (random neighbours)
            var cellinfo = new int[miRows * miCols * 4];
            // libaom's grid: stride / rows aligned to 128x128, entries outside the frame NULL
            int miStride = (miCols + 31) & ~31, gridRows = (miRows + 31) & ~31;
            var cells = new AomMbModeInfo[miRows * miCols];
            var xd = new AomMacroblockD { MiStride = miStride, MiGrid = new AomMbModeInfo?[miStride * gridRows] };
            for (int r = 0; r < miRows; r++)
                for (int c = 0; c < miCols; c++)
                {
                    int i = r * miCols + c;
                    int mode = rng.Next(13), uv = rng.Next(14), rf = rng.Next(8) == 0 ? 1 : 0, ibc = rng.Next(16) == 0 ? 1 : 0;
                    cellinfo[i * 4] = mode; cellinfo[i * 4 + 1] = uv; cellinfo[i * 4 + 2] = rf; cellinfo[i * 4 + 3] = ibc;
                    cells[i] = new AomMbModeInfo { Mode = mode, UvMode = uv, RefFrame0 = rf, UseIntrabc = (byte)ibc };
                    xd.MiGrid[r * miStride + c] = (r >= miRow && r < miRow + bh && c >= miCol && c < miCol + bw) ? cur : cells[i];
                }
            xd.MiOffset = miRow * miStride + miCol;
            xd.Mi0 = cur;
            xd.MiRow = miRow; xd.MiCol = miCol;
            xd.TileMiRowStart = tileR0; xd.TileMiRowEnd = tileR1; xd.TileMiColStart = tileC0; xd.TileMiColEnd = tileC1;

            // ---- planes (random "reconstruction", with a border)
            var f = new Frame();
            var dstOff = new int[3];
            for (int p = 0; p < 3; p++)
            {
                int sx = p == 0 ? 0 : ssx, sy = p == 0 ? 0 : ssy;
                int w = (miCols * 4) >> sx, h = (miRows * 4) >> sy;
                f.Stride[p] = (w + 2 * Frame.B + 31) & ~31; f.Rows[p] = h + 2 * Frame.B;
                f.Planes[p] = new byte[f.Stride[p] * f.Rows[p]];
                RandomEdge(rng, f.Planes[p]);
                if (rng.Next(2) == 0) for (int i = 0; i < f.Planes[p].Length; i++) f.Planes[p][i] = (byte)rng.Next(256);
                // setup_pred_plane: sub8x8 chroma blocks start at the even mi position
                int mr = miRow, mc = miCol;
                if (sy != 0 && (mr & 1) != 0 && bh == 1) mr--;
                if (sx != 0 && (mc & 1) != 0 && bw == 1) mc--;
                dstOff[p] = (Frame.B + ((4 * mr) >> sy)) * f.Stride[p] + Frame.B + ((4 * mc) >> sx);
                xd.Plane[p].SubsamplingX = sx; xd.Plane[p].SubsamplingY = sy;
                xd.Plane[p].Dst = new AomBuf2d { Buf = f.Planes[p], Offset = dstOff[p], Stride = f.Stride[p] };
                // set_plane_n4 (pixels)
                xd.Plane[p].Width = Math.Max((bw * 4) >> sx, 4);
                xd.Plane[p].Height = Math.Max((bh * 4) >> sy, 4);
            }
            for (int i = 0; i < xd.Plane[0].ColorIndexMap.Length; i++) xd.Plane[0].ColorIndexMap[i] = (byte)rng.Next(Math.Max((int)cur.Palette.PaletteSize0, 1));
            for (int i = 0; i < xd.Plane[1].ColorIndexMap.Length; i++) xd.Plane[1].ColorIndexMap[i] = (byte)rng.Next(Math.Max((int)cur.Palette.PaletteSize1, 1));
            AomCfl.CflInit(xd.Cfl, ssx, ssy);

            var derived = new int[17];
            var curinfo = new[]
            {
                bsize, partition, cur.Mode, cur.UvMode, cur.AngleDelta[0], cur.AngleDelta[1], cur.UseFilterIntra, cur.FilterIntraMode,
                cur.Palette.PaletteSize0, cur.Palette.PaletteSize1, cur.CflAlphaIdx, cur.CflAlphaSigns,
            };
            unsafe
            {
                fixed (int* ci = cellinfo) fixed (int* cu = curinfo) fixed (ushort* pal = cur.Palette.PaletteColors)
                fixed (byte* p0 = f.Planes[0]) fixed (byte* p1 = f.Planes[1]) fixed (byte* p2 = f.Planes[2])
                fixed (int* st = f.Stride) fixed (int* ro = f.Rows) fixed (int* dof = dstOff)
                fixed (byte* m0 = xd.Plane[0].ColorIndexMap) fixed (byte* m1 = xd.Plane[1].ColorIndexMap) fixed (int* de = derived)
                {
                    byte** planes = stackalloc byte*[3];
                    planes[0] = p0; planes[1] = p1; planes[2] = p2;
                    Native.twin_e2e_setup(miRows, miCols, ci, tileR0, tileR1, tileC0, tileC1, ssx, ssy, sb, edgeFilter ? 1 : 0,
                        miRow, miCol, cu, pal, planes, st, ro, dof, m0, m1, de);
                }
            }
            xd.UpAvailable = derived[0] != 0; xd.LeftAvailable = derived[1] != 0;
            xd.ChromaUpAvailable = derived[2] != 0; xd.ChromaLeftAvailable = derived[3] != 0;
            xd.IsChromaRef = derived[4] != 0;
            xd.MbToTopEdge = derived[5]; xd.MbToBottomEdge = derived[6]; xd.MbToLeftEdge = derived[7]; xd.MbToRightEdge = derived[8];
            AomMbModeInfo? Mi(int idx) => idx == -1 ? null : idx == -2 ? cur : cells[idx];
            xd.AboveMbmi = Mi(derived[9]); xd.LeftMbmi = Mi(derived[10]);
            xd.ChromaAboveMbmi = Mi(derived[11]); xd.ChromaLeftMbmi = Mi(derived[12]);
            if (xd.Plane[0].Width != derived[13] || xd.Plane[0].Height != derived[14] || xd.Plane[1].Width != derived[15] || xd.Plane[1].Height != derived[16])
            {
                await Assert.That("plane width/height").IsEqualTo("set_plane_n4");
                return;
            }
            string ctx = $"iter {iter} bs {bsize} ({miRow},{miCol}) of ({miRows},{miCols}) tile r[{tileR0},{tileR1}) c[{tileC0},{tileC1}) ss {ssx}{ssy} sb {sb} part {partition} mode {cur.Mode}/{cur.UvMode} ad {cur.AngleDelta[0]}/{cur.AngleDelta[1]} fi {cur.UseFilterIntra}:{cur.FilterIntraMode} pal {cur.Palette.PaletteSize0}/{cur.Palette.PaletteSize1}";
            blocks++;

            // ---- luma: every transform block of one of the block's tx sizes, in raster order
            int lumaTx = MaxTxsizeRectLookup[bsize];
            for (int d = rng.Next(3); d > 0 && lumaTx != TX_4X4; d--) lumaTx = SubTxSizeMap[lumaTx];
            if (cur.UseFilterIntra != 0 && (TxSizeWide[lumaTx] > 32 || TxSizeHigh[lumaTx] > 32)) lumaTx = TX_32X32;
            int maxW = AomCfl.MaxBlockWide(xd, bsize, 0), maxH = AomCfl.MaxBlockHigh(xd, bsize, 0);
            bool direct = rng.Next(4) == 0;
            int dMode = rng.Next(13), dAngle = rng.Next(-3, 4) * 3;
            int dFi = rng.Next(3) == 0 && TxSizeWide[lumaTx] <= 32 && TxSizeHigh[lumaTx] <= 32 ? rng.Next(5) : FILTER_INTRA_MODES;
            bool dPal = rng.Next(6) == 0;
            for (int r = 0; r < maxH; r += TxSizeHighUnit[lumaTx])
                for (int c = 0; c < maxW; c += TxSizeWideUnit[lumaTx])
                {
                    if (direct)
                    {
                        Native.twin_e2e_predict(0, c, r, lumaTx, dMode, dAngle, dPal ? 1 : 0, dFi);
                        var pd = xd.Plane[0];
                        int off = pd.Dst.Offset + ((r * pd.Dst.Stride + c) << 2);
                        AomReconIntra.PredictIntraBlock(xd, sb, edgeFilter, pd.Width, pd.Height, lumaTx, dMode, dAngle, dPal, dFi,
                            pd.Dst.Buf, off, pd.Dst.Stride, pd.Dst.Buf, off, pd.Dst.Stride, c, r, 0);
                    }
                    else
                    {
                        Native.twin_e2e_facade(0, c, r, lumaTx);
                        AomReconIntra.PredictIntraBlockFacade(xd, sb, edgeFilter, 0, c, r, lumaTx);
                    }
                    txBlocks++;
                    if (!await ComparePlanes(f, $"{ctx} luma tx {lumaTx} at ({r},{c}) direct {direct}:{dMode}/{dAngle}/{dFi}/{dPal}")) return;
                }

            // ---- CfL luma store (for every block: chroma-less blocks store too)
            if (small)
            {
                // cfl_store_block (libaom uses it for inter blocks) stores get_tx_size(visible w, h): only a CfL size
                // is valid (libaom has no subsample kernel for the others)
                int storeW = (maxW * 4 + TxSizeWide[lumaTx] - 1) & ~(TxSizeWide[lumaTx] - 1);
                int storeH = (maxH * 4 + TxSizeHigh[lumaTx] - 1) & ~(TxSizeHigh[lumaTx] - 1);
                int storeTx = AomCfl.GetTxSize(storeW, storeH);
                if (rng.Next(2) == 0 && TxSizeWide[storeTx] <= 32 && TxSizeHigh[storeTx] <= 32)
                {
                    Native.twin_e2e_cfl_store_block(bsize, lumaTx);
                    AomCfl.CflStoreBlock(xd, bsize, lumaTx);
                }
                else
                {
                    for (int r = 0; r < maxH; r += TxSizeHighUnit[lumaTx])
                        for (int c = 0; c < maxW; c += TxSizeWideUnit[lumaTx])
                        {
                            Native.twin_e2e_cfl_store_tx(r, c, lumaTx, bsize);
                            AomCfl.CflStoreTx(xd, r, c, lumaTx, bsize);
                        }
                }
                if (!await CompareCfl(xd.Cfl, $"{ctx} cfl store tx {lumaTx}")) return;
            }

            // ---- chroma
            if (!xd.IsChromaRef) continue;
            int planeBsize = AomCfl.GetPlaneBlockSize(bsize, ssx, ssy);
            int uvTx = AdjustUvTx(MaxTxsizeRectLookup[planeBsize]);
            int maxWc = AomCfl.MaxBlockWide(xd, planeBsize, 1), maxHc = AomCfl.MaxBlockHigh(xd, planeBsize, 1);
            if (cur.UvMode == UV_CFL_PRED)
            {
                cflBlocks++;
                for (int round = 0; round < 1 + rng.Next(3); round++)
                {
                    int useCache = rng.Next(2), idx = rng.Next(256), js = rng.Next(8);
                    if (round == 0 || rng.Next(3) == 0) { Native.twin_e2e_clear_cfl_cache(); AomCfl.ClearCflDcPredCacheFlags(xd.Cfl); }
                    Native.twin_e2e_set_cfl(useCache, idx, js);
                    xd.Cfl.UseDcPredCache = useCache != 0; cur.CflAlphaIdx = (byte)idx; cur.CflAlphaSigns = (sbyte)js;
                    for (int p = 1; p < 3; p++)
                    {
                        Native.twin_e2e_facade(p, 0, 0, uvTx);
                        AomReconIntra.PredictIntraBlockFacade(xd, sb, edgeFilter, p, 0, 0, uvTx);
                        if (!await ComparePlanes(f, $"{ctx} cfl plane {p} tx {uvTx} round {round} cache {useCache} alpha {idx}/{js}")) return;
                        if (!await CompareCfl(xd.Cfl, $"{ctx} cfl plane {p} round {round}")) return;
                    }
                }
            }
            else
            {
                for (int p = 1; p < 3; p++)
                    for (int r = 0; r < maxHc; r += TxSizeHighUnit[uvTx])
                        for (int c = 0; c < maxWc; c += TxSizeWideUnit[uvTx])
                        {
                            Native.twin_e2e_facade(p, c, r, uvTx);
                            AomReconIntra.PredictIntraBlockFacade(xd, sb, edgeFilter, p, c, r, uvTx);
                            txBlocks++;
                            if (!await ComparePlanes(f, $"{ctx} chroma plane {p} tx {uvTx} at ({r},{c})")) return;
                        }
            }
        }
        await Assert.That(blocks).IsGreaterThan(12000);
        await Assert.That(cflBlocks).IsGreaterThan(1000);
        Console.WriteLine($"facade twin: {blocks} blocks, {txBlocks} tx blocks, {cflBlocks} cfl blocks");
    }
}
