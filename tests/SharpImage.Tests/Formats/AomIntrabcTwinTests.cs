using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twins of the intrabc pieces of the libaom encoder port (AomHashMotion, AomMvCost, AomMvRef.IsDvValid, AomSad,
// AomMcomp.FullPixelSearch, the tx partition / inter tx type mode costs) against libaom 3.14.1's own functions through
// aomtwin_ibc.dll (built against libaom.a with the run-time dispatch initialised: the kernels compared are the SIMD
// ones libaom's encoder runs; see scratchpad aomtwin_ibc/). Opt-in: point SHARPIMAGE_AOMTWIN_IBC at the DLL.
[NotInParallel]
public sealed class AomIntrabcTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_IBC");
    private static readonly IntPtr Lib = DllPath != null ? NativeLibrary.Load(DllPath) : IntPtr.Zero;
    private static bool Available => Lib != IntPtr.Zero && Init();
    private static bool s_init;

    private static unsafe bool Init()
    {
        if (!s_init) { ((delegate* unmanaged<void>)Fn("twin_init"))(); s_init = true; }
        return true;
    }

    private static IntPtr Fn(string name) => NativeLibrary.GetExport(Lib, name);

    [Test]
    public async Task Crc32c_RandomBuffers()
    {
        if (!Available) return;
        await Assert.That(Crc32c_RandomBuffersImpl()).IsNull();
    }

    private static unsafe string? Crc32c_RandomBuffersImpl()
    {
        var f = (delegate* unmanaged<byte*, int, uint>)Fn("twin_crc32c");
        var rng = new Random(1);
        for (int t = 0; t < 2000; t++)
        {
            int len = t % 3 == 0 ? 16 : rng.Next(0, 200);
            var b = new byte[len];
            rng.NextBytes(b);
            uint want;
            fixed (byte* p = b) want = f(p, len);
            if (!Equals(AomHashMotion.Crc32c(b), want)) return $"AomHashMotion.Crc32c(b): {AomHashMotion.Crc32c(b)} != {want}";
            if (len == 16)
            {
                uint got = AomHashMotion.Crc32c4(BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 8),
                    BitConverter.ToUInt32(b, 12));
                if (!Equals(got, want)) return $"got: {got} != {want}";
            }
        }
        return null;
    }

    [ThreadStatic] private static unsafe byte* t_aligned;
    [ThreadStatic] private static int t_alignedLen;

    // a 64-byte aligned native copy (libaom's SIMD source loads assume 16-byte aligned rows)
    private static unsafe byte* Aligned(byte[] src)
    {
        if (t_alignedLen < src.Length)
        {
            if (t_aligned != null) NativeMemory.AlignedFree(t_aligned);
            t_aligned = (byte*)NativeMemory.AlignedAlloc((nuint)src.Length, 64);
            t_alignedLen = src.Length;
        }
        src.AsSpan().CopyTo(new Span<byte>(t_aligned, src.Length));
        return t_aligned;
    }

    private static byte[] ScreenLike(Random rng, int w, int h)
    {
        var y = new byte[w * h];
        byte[] colors = { 235, 30, 90, 170 };
        var glyph = new byte[8 * 8];
        for (int i = 0; i < glyph.Length; i++) glyph[i] = (byte)(rng.Next(3) == 0 ? rng.Next(1, 4) : 0);
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int g = ((r / 8) + (c / 8)) % 3 == 0 ? glyph[(r % 8) * 8 + c % 8] : 0;
                y[r * w + c] = colors[g];
            }
        for (int k = 0; k < w * h / 50; k++) y[rng.Next(w * h)] = (byte)rng.Next(256);
        return y;
    }

    [Test]
    public async Task BlockHash_AllSizes()
    {
        if (!Available) return;
        await Assert.That(BlockHash_AllSizesImpl()).IsNull();
    }

    private static unsafe string? BlockHash_AllSizesImpl()
    {
        var f = (delegate* unmanaged<byte*, int, int, uint*, uint*, void>)Fn("twin_block_hash");
        var rng = new Random(2);
        var info = new AomIntrabcHashInfo();
        for (int t = 0; t < 300; t++)
        {
            int size = 4 << (t % 6);
            var img = t % 2 == 0 ? ScreenLike(rng, 160, 160) : Enumerable.Range(0, 160 * 160).Select(_ => (byte)rng.Next(256)).ToArray();
            int off = rng.Next(0, 160 - size) * 160 + rng.Next(0, 160 - size);
            uint a1, a2;
            fixed (byte* p = img) f(p + off, 160, size, &a1, &a2);
            AomHashMotion.GetBlockHashValue(info, img, null, off, 160, size, out uint b1, out uint b2);
            if (!Equals(b1, a1)) return $"b1: {b1} != {a1}";
            if (!Equals(b2, a2)) return $"b2: {b2} != {a2}";
        }
        return null;
    }

    [Test]
    public async Task HashTable_FrameBuild()
    {
        if (!Available) return;
        await Assert.That(HashTable_FrameBuildImpl()).IsNull();
    }

    private static unsafe string? HashTable_FrameBuildImpl()
    {
        var f = (delegate* unmanaged<byte*, int, int, int, int, int, int*, ulong>)Fn("twin_hash_table");
        var rng = new Random(3);
        foreach (var (w, h, mib, max8) in new[] { (96, 80, 4, 0), (100, 72, 5, 0), (64, 64, 4, 1), (130, 66, 5, 1) })
        {
            var img = ScreenLike(rng, w, h);
            int total;
            ulong want;
            fixed (byte* p = img) want = f(p, w, w, h, mib, max8, &total);
            var fb = new AomFrameBuffer(w, h, 1, 1, true);
            for (int r = 0; r < h; r++) Array.Copy(img, r * w, fb.Buffers[0], fb.Offsets[0] + r * fb.Strides[0], w);
            var info = AomHashMotion.BuildFrameTable(fb, mib, max8 != 0, 4);
            ulong acc = 1469598103934665603UL;
            int tot = 0;
            for (uint k = 0; k < AomIntrabcHashInfo.MaxAddr; k++)
            {
                var v = info.LookupTable[k];
                if (v == null || v.Count == 0) continue;
                acc = (acc ^ k) * 1099511628211UL;
                foreach (var b in v)
                {
                    acc = (acc ^ (uint)b.X) * 1099511628211UL;
                    acc = (acc ^ (uint)b.Y) * 1099511628211UL;
                    acc = (acc ^ b.HashValue2) * 1099511628211UL;
                }
                tot += v.Count;
            }
            if (!Equals(tot, total)) return $"tot: {tot} != {total}";
            if (!Equals(acc, want)) return $"acc: {acc} != {want}";
        }
        return null;
    }

    [Test]
    public async Task DvCosts_AndMvBitCost()
    {
        if (!Available) return;
        await Assert.That(DvCosts_AndMvBitCostImpl()).IsNull();
    }

    private static unsafe string? DvCosts_AndMvBitCostImpl()
    {
        var fc = (delegate* unmanaged<int*, int*, int*, void>)Fn("twin_dv_costs");
        var fb = (delegate* unmanaged<int, int, int, int, int*, int*, int*, int, int>)Fn("twin_mv_bit_cost");
        int[] joint = new int[4], c0 = new int[AomMvCost.MvVals], c1 = new int[AomMvCost.MvVals];
        fixed (int* j = joint) fixed (int* a = c0) fixed (int* b = c1) fc(j, a, b);
        var ctx = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(ctx, 0);
        var dv = new AomDvCosts();
        AomMvCost.FillDvCosts(ctx.Mv, dv);
        if (!(dv.JointMv.SequenceEqual(joint))) return "false: dv.JointMv.SequenceEqual(joint)";
        if (!(dv.DvCosts[0].SequenceEqual(c0))) return "false: dv.DvCosts[0].SequenceEqual(c0)";
        if (!(dv.DvCosts[1].SequenceEqual(c1))) return "false: dv.DvCosts[1].SequenceEqual(c1)";
        var rng = new Random(4);
        for (int t = 0; t < 5000; t++)
        {
            var mv = new AomMv(rng.Next(-1000, 1000) * 8, rng.Next(-1000, 1000) * 8);
            var rf = new AomMv(rng.Next(-300, 300) * 8, rng.Next(-300, 300) * 8);
            int want;
            fixed (int* j = joint) fixed (int* a = c0) fixed (int* b = c1)
                want = fb(mv.Row, mv.Col, rf.Row, rf.Col, j, a + AomMvCost.MvMax, b + AomMvCost.MvMax, AomMvCost.MV_COST_WEIGHT_SUB);
            if (!Equals(AomMvCost.MvBitCost(mv, rf, dv.JointMv, dv.DvCosts, AomMvCost.MV_COST_WEIGHT_SUB), want)) return $"AomMvCost.MvBitCost(mv, rf, dv.JointMv, dv.DvCosts, AomMvCost.MV_COST_WEIGHT_SUB): {AomMvCost.MvBitCost(mv, rf, dv.JointMv, dv.DvCosts, AomMvCost.MV_COST_WEIGHT_SUB)} != {want}";
        }
        return null;
    }

    [Test]
    public async Task IsDvValid_Random()
    {
        if (!Available) return;
        await Assert.That(IsDvValid_RandomImpl()).IsNull();
    }

    private static unsafe string? IsDvValid_RandomImpl()
    {
        var f = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, int, int, int>)Fn("twin_is_dv_valid");
        var rng = new Random(5);
        var cm = new AomCommon(16, 16, 1, 1, false);   // only NumPlanes is read
        for (int t = 0; t < 20000; t++)
        {
            int miRows = rng.Next(8, 200), miCols = rng.Next(8, 200);
            int bsize = rng.Next(BLOCK_SIZES_ALL);
            int miRow = rng.Next(miRows), miCol = rng.Next(miCols);
            int mibLog2 = rng.Next(2) == 0 ? 4 : 5;
            bool chromaRef = rng.Next(2) == 0;
            int dvr = (rng.Next(-miRow * 4 - 40, 40)) * (rng.Next(8) == 0 ? 1 : 8);
            int dvc = (rng.Next(-miCol * 4 - 40, miCols * 4)) * (rng.Next(8) == 0 ? 1 : 8);
            int want = f(dvr, dvc, miRows, miCols, miRow, miCol, bsize, mibLog2, chromaRef ? 1 : 0, 1, 1, 3);
            var xd = new AomMacroblockD { TileMiRowStart = 0, TileMiRowEnd = miRows, TileMiColStart = 0, TileMiColEnd = miCols, IsChromaRef = chromaRef };
            xd.Plane[1].SubsamplingX = 1; xd.Plane[1].SubsamplingY = 1;
            bool got = AomMvRef.IsDvValid(new AomMv(dvr, dvc), cm, xd, miRow, miCol, bsize, mibLog2);
            if (!Equals(got ? 1 : 0, want)) return $"is_dv_valid {dvr} {dvc} at {miRow} {miCol} bs {bsize}: {got} != {want}";
        }
        return null;
    }

    [Test]
    public async Task SadVariance_AllBlockSizes()
    {
        if (!Available) return;
        await Assert.That(SadVariance_AllBlockSizesImpl()).IsNull();
    }

    private static unsafe string? SadVariance_AllBlockSizesImpl()
    {
        var fs = (delegate* unmanaged<int, byte*, int, byte*, int, uint>)Fn("twin_sad");
        var fk = (delegate* unmanaged<int, byte*, int, byte*, int, uint>)Fn("twin_sad_skip");
        var fv = (delegate* unmanaged<int, byte*, int, byte*, int, uint*, uint>)Fn("twin_variance");
        var rng = new Random(6);
        var a = new byte[320 * 300];
        var b = new byte[300 * 300];
        for (int t = 0; t < 400; t++)
        {
            rng.NextBytes(a);
            if (t % 2 == 0) rng.NextBytes(b); else for (int i = 0; i < b.Length; i++) b[i] = (byte)Math.Clamp(a[i] + rng.Next(-3, 4), 0, 255);
            int bs = t % BLOCK_SIZES_ALL;
            int w = BlockSizeWide[bs], h = BlockSizeHigh[bs];
            int ao = rng.Next(0, (300 - w) / 16) * 16, bo = rng.Next(0, 300 - h) * 300 + rng.Next(0, 300 - w);
            uint wantSad, wantVar, wantSse;
            byte* na = Aligned(a);
            fixed (byte* pb = b)
            {
                wantSad = fs(bs, na + ao, 320, pb + bo, 300);
                wantVar = fv(bs, na + ao, 320, pb + bo, 300, &wantSse);
            }
            if (!Equals(AomSad.Sad(a, ao, 320, b, bo, 300, w, h), wantSad)) return $"AomSad.Sad(a, ao, 300, b, bo, 300, w, h): {AomSad.Sad(a, ao, 300, b, bo, 300, w, h)} != {wantSad}";
            uint v = AomSad.Variance(a, ao, 320, b, bo, 300, w, h, out uint sse);
            if (!Equals(v, wantVar)) return $"v: {v} != {wantVar}";
            if (!Equals(sse, wantSse)) return $"sse: {sse} != {wantSse}";
            if (h >= 16)
            {
                uint wantSkip;
                fixed (byte* pb = b) wantSkip = fk(bs, na + ao, 320, pb + bo, 300);
                if (!Equals(AomSad.SadSkip(a, ao, 320, b, bo, 300, w, h), wantSkip)) return $"AomSad.SadSkip(a, ao, 300, b, bo, 300, w, h): {AomSad.SadSkip(a, ao, 300, b, bo, 300, w, h)} != {wantSkip}";
            }
        }
        return null;
    }

    [Test]
    public async Task FullPixelSearch_RandomFrames()
    {
        if (!Available) return;
        await Assert.That(FullPixelSearch_RandomFramesImpl()).IsNull();
    }

    private static unsafe string? FullPixelSearch_RandomFramesImpl()
    {
        var f = (delegate* unmanaged<byte*, int, byte*, int, int, int, int, int, int, int, int, int*, int, int*, int, int, int, int, int*, int*, int*, int,
            int*, int*, int>)Fn("twin_full_pixel_search");
        var ctx = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(ctx, 0);
        var dv = new AomDvCosts();
        AomMvCost.FillDvCosts(ctx.Mv, dv);
        var sites = AomMcomp.InitSearchSites();
        var rng = new Random(7);
        const int W = 320, H = 256, S = 448;
        int[] methods = { DIAMOND, NSTEP, NSTEP_8PT, CLAMPED_DIAMOND, HEX, BIGDIA, FAST_DIAMOND, FAST_BIGDIA, VFAST_DIAMOND };
        int[] bsizes = { BLOCK_4X4, BLOCK_8X8, BLOCK_16X16, BLOCK_8X16, BLOCK_16X8, BLOCK_32X32, BLOCK_32X16, BLOCK_64X64, BLOCK_4X16 };
        int mismatches = 0, meshRuns = 0;
        for (int t = 0; t < 600; t++)
        {
            var img = ScreenLike(rng, W, H);
            var buf = new byte[S * (H + 128)];
            for (int r = 0; r < H; r++) Array.Copy(img, r * W, buf, (r + 64) * S + 64, W);
            int bs = bsizes[t % bsizes.Length];
            int bw = BlockSizeWide[bs], bh = BlockSizeHigh[bs];
            int by = rng.Next(0, H - bh) & ~3, bx = rng.Next(0, W - bw) & ~(Math.Min(bw, 16) - 1);
            int blk = (by + 64) * S + bx + 64;
            var lim = new AomFullMvLimits { ColMin = -bx, ColMax = W - bw - bx, RowMin = -by, RowMax = Math.Max(-by, -bh) };
            if (rng.Next(2) == 0) lim.RowMax = H - bh - by;
            var refMv = new AomMv(rng.Next(-by, 1) * 8, rng.Next(-bx, W - bw - bx) * 8);
            AomMcomp.SetMvSearchRange(ref lim, refMv);
            int method = methods[t % methods.Length];
            int stepParam = rng.Next(0, 4);
            int thresh = rng.Next(3) == 0 ? 0 : 1 << 20;
            var meshIbc = t % 2 == 0 ? new[] { (64, 4), (16, 1) } : new[] { (64, 1), (64, 1) };
            var mesh = new int[16];
            mesh[0] = 64; mesh[1] = 8; mesh[2] = 28; mesh[3] = 4; mesh[4] = 15; mesh[5] = 1; mesh[6] = 7; mesh[7] = 1;
            mesh[8] = meshIbc[0].Item1; mesh[9] = meshIbc[0].Item2; mesh[10] = meshIbc[1].Item1; mesh[11] = meshIbc[1].Item2;
            bool skip = bh >= 16 && rng.Next(3) == 0;
            int epb = rng.Next(20, 4000), spb = rng.Next(2, 30);
            var start = refMv.ToFullMv();
            int wantVar, br, bc;
            int[] limArr = { lim.ColMin, lim.ColMax, lim.RowMin, lim.RowMax };
            byte* p = Aligned(buf);
            fixed (int* l = limArr) fixed (int* m = mesh) fixed (int* j = dv.JointMv) fixed (int* c0 = dv.DvCosts[0])
            fixed (int* c1 = dv.DvCosts[1])
                wantVar = f(p + blk, S, p + blk, S, bs, method, stepParam, start.Row, start.Col, refMv.Row, refMv.Col, l, thresh, m, 0, 1, epb, spb,
                    j, c0 + AomMvCost.MvMax, c1 + AomMvCost.MvMax, skip ? 1 : 0, &br, &bc);

            var p2 = new AomFullPelMsParams
            {
                Bsize = bs, SearchMethod = method, SearchSites = sites[new[] { DIAMOND, NSTEP, NSTEP_8PT, CLAMPED_DIAMOND, HEX, BIGDIA, BIGDIA, BIGDIA, BIGDIA }[method]],
                MvLimits = lim, ForceMeshThresh = thresh, MeshSearchMvDiffThreshold = 4, IsIntraMode = true, RefMv = refMv, FullRefMv = refMv.ToFullMv(),
                MvJCost = dv.JointMv, MvCost = dv.DvCosts, ErrorPerBit = epb, SadPerBit = spb, SkipSad = skip,
                Src = new AomBuf2d { Buf = buf, Offset = blk, Stride = S }, Ref = new AomBuf2d { Buf = buf, Offset = blk, Stride = S },
            };
            p2.MeshPatterns[0] = Enumerable.Range(0, 4).Select(i => new AomMeshPattern { range = mesh[i * 2], interval = mesh[i * 2 + 1] }).ToArray();
            p2.MeshPatterns[1] = Enumerable.Range(0, 4).Select(i => new AomMeshPattern { range = mesh[8 + i * 2], interval = mesh[8 + i * 2 + 1] }).ToArray();
            int got = AomMcomp.FullPixelSearch(start, p2, stepParam, out AomMv best);
            if (got != wantVar || best.Row != br || best.Col != bc) mismatches++;
            if (thresh == 0 && (method == NSTEP || method == NSTEP_8PT)) meshRuns++;
            if (!Equals((got, (int)best.Row, (int)best.Col), (wantVar, br, bc))) return $"t {t} method {method} bs {bs} skip {skip} thresh {thresh} step {stepParam} start {start} lim {lim} mesh {mesh[8]},{mesh[9]}: {(got, (int)best.Row, (int)best.Col)} != {(wantVar, br, bc)}";
        }
        if (!(meshRuns > 0)) return "not greater";
        if (!Equals(mismatches, 0)) return $"mismatches: {mismatches} != {0}";
        return null;
    }

    [Test]
    public async Task InterModeCosts_Defaults()
    {
        if (!Available) return;
        await Assert.That(InterModeCosts_DefaultsImpl()).IsNull();
    }

    private static unsafe string? InterModeCosts_DefaultsImpl()
    {
        var f = (delegate* unmanaged<int*, void>)Fn("twin_inter_mode_costs");
        var want = new int[21 * 2 + 4 * 4 * 16];
        fixed (int* p = want) f(p);
        var fc = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(fc, 0);
        var mc = new AomModeCosts();
        AomModeCostFill.Fill(mc, fc, true);
        if (!(mc.TxfmPartitionCost.SequenceEqual(want.Take(42)))) return "false: mc.TxfmPartitionCost.SequenceEqual(want.Take(42))";
        // libaom fills only the (set, size) pairs use_inter_ext_tx_for_txsize marks; the rest stay 0 in both
        if (!(mc.InterTxTypeCosts.SequenceEqual(want.Skip(42)))) return "false: mc.InterTxTypeCosts.SequenceEqual(want.Skip(42))";
        return null;
    }

    /// <summary>Negative control: a twin that is fed a deliberately different input must disagree (the comparison
    /// path is live).</summary>
    [Test]
    public async Task NegativeControl_PerturbedCostTableDisagrees()
    {
        if (!Available) return;
        await Assert.That(NegativeControl_PerturbedCostTableDisagreesImpl()).IsNull();
    }

    private static unsafe string? NegativeControl_PerturbedCostTableDisagreesImpl()
    {
        var fb = (delegate* unmanaged<int, int, int, int, int*, int*, int*, int, int>)Fn("twin_mv_bit_cost");
        var ctx = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(ctx, 0);
        var dv = new AomDvCosts();
        AomMvCost.FillDvCosts(ctx.Mv, dv);
        var bad = (int[])dv.DvCosts[0].Clone();
        for (int i = 0; i < bad.Length; i++) bad[i] += 1;
        var mv = new AomMv(-64, 0);
        int want;
        fixed (int* j = dv.JointMv) fixed (int* a = bad) fixed (int* b = dv.DvCosts[1])
            want = fb(mv.Row, mv.Col, 0, 0, j, a + AomMvCost.MvMax, b + AomMvCost.MvMax, 512);   // weight 512: +1 per table step survives the rounding
        if (Equals(AomMvCost.MvBitCost(mv, default, dv.JointMv, dv.DvCosts, 512), want)) return "negative control did not disagree";
        return null;
    }
}
