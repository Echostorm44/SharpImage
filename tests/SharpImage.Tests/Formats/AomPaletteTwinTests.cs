using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twins of the libaom palette search port (src/SharpImage/Formats/Av1/Aom/AomPalette*.cs) against libaom 3.14.1:
// aomtwin_pal.dll exports libaom.a's own palette.c / pred_common.c / entropymode.c / tokenize.c / intra_mode_search.c
// functions, the RTCD-dispatched (AVX2) k-means kernels and msvcrt's qsort; aomtwin_palsearch.dll (next to it) compiles
// a copy of palette.c and tokenize.c for their statics and runs the palette search on a synthetic MACROBLOCK with a
// mock tx search (scratchpad aomtwin_pal/). Opt-in: point SHARPIMAGE_AOMTWIN_PAL at aomtwin_pal.dll; without it the
// tests pass without checking.
[NotInParallel]
public sealed partial class AomPaletteTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_PAL");
    private static readonly bool Available = DllPath != null && (File.Exists(DllPath)
        ? Load() : throw new FileNotFoundException("SHARPIMAGE_AOMTWIN_PAL is set but the DLL does not exist", DllPath));

    private static nint s_lib, s_search;

    private static unsafe bool Load()
    {
        string searchPath = Path.Combine(Path.GetDirectoryName(DllPath)!, "aomtwin_palsearch.dll");
        if (!File.Exists(searchPath)) throw new FileNotFoundException("aomtwin_palsearch.dll must sit next to aomtwin_pal.dll", searchPath);
        s_lib = NativeLibrary.Load(DllPath!);
        s_search = NativeLibrary.Load(searchPath);
        ((delegate* unmanaged<void>)NativeLibrary.GetExport(s_lib, "twin_init"))();
        ((delegate* unmanaged<void>)NativeLibrary.GetExport(s_search, "twin_init"))();
        return true;
    }

    private static nint L(string name) => NativeLibrary.GetExport(s_lib, name);
    private static nint S(string name) => NativeLibrary.GetExport(s_search, name);

    private const int PMax = AomPalette.PALETTE_MAX_SIZE;
    private static int Round16(int n) => (n + 15) & ~15;

    // block sizes palette allows (av1_allow_palette: >= BLOCK_8X8, both sides <= 64): includes 4x16 / 16x4
    private static readonly int[] PaletteBsizes = Enumerable.Range(BLOCK_8X8, 22 - BLOCK_8X8)
        .Where(b => BlockSizeWide[b] <= 64 && BlockSizeHigh[b] <= 64).ToArray();

    // ---- content generators -------------------------------------------------------------------------------------

    /// <summary>A w x h block of kind 0 few colors (runs), 1 gradient, 2 noise, 3 few colors + +-1 noise, 4 constant,
    /// 5 two levels, 6 many colors (up to 64 levels).</summary>
    private static void FillBlock(Random rng, byte[] buf, int off, int stride, int w, int h, int kind)
    {
        int nc = rng.Next(2, 11);
        var pal = Enumerable.Range(0, nc).Select(_ => (byte)rng.Next(256)).ToArray();
        int gx = rng.Next(-6, 7), gy = rng.Next(-6, 7), g0 = rng.Next(256);
        int cur = pal[0];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                if (rng.Next(6) == 0) cur = pal[rng.Next(nc)];
                buf[off + r * stride + c] = kind switch
                {
                    0 => (byte)cur,
                    1 => (byte)Math.Clamp(g0 + gx * c + gy * r, 0, 255),
                    2 => (byte)rng.Next(256),
                    3 => (byte)Math.Clamp(cur + rng.Next(-1, 2), 0, 255),
                    4 => pal[0],
                    5 => ((r * 7 + c * 3) / 5 & 1) == 0 ? pal[0] : pal[1],
                    _ => (byte)(g0 / 4 + rng.Next(64)),
                };
            }
    }

    private static ushort[] SortedUnique(Random rng, int n, int max = 256)
    {
        var s = new SortedSet<int>();
        while (s.Count < n) s.Add(rng.Next(max));
        return s.Select(v => (ushort)v).ToArray();
    }

    private static string? First(List<string> f, int cases) => f.Count == 0 ? null : $"{f.Count} of {cases} cases differ; first: {f[0]}";

    private static string Diff<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : IEquatable<T>
    {
        if (a.Length != b.Length) return $"length {a.Length} vs {b.Length}";
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return $"at {i}: ours {a[i]} libaom {b[i]}";
        return "equal";
    }

    // ---- msvcrt qsort -----------------------------------------------------------------------------------------------

    private struct QE { public int Key, Id; }

    [Test]
    public async Task MsvcrtQsort_ShortsortCallForCall()
    {
        if (!Available) return;
        var rng = new Random(1);
        var f = new List<string>();
        int cases = 0;
        int[] log = new int[2 * 4096], ids = new int[16];
        for (int it = 0; it < 40000; it++)
        {
            int n = rng.Next(0, 9);
            int range = rng.Next(1, 6);   // heavy ties
            int[] keys = Enumerable.Range(0, n).Select(_ => rng.Next(range)).ToArray();
            int calls;
            unsafe
            {
                fixed (int* k = keys) fixed (int* i = ids) fixed (int* l = log)
                    calls = ((delegate* unmanaged<int*, int*, int, int*, int>)L("twin_qsort"))(k, i, n, l);
            }
            var arr = keys.Select((k, i) => new QE { Key = k, Id = i }).ToArray();
            var ours = new List<int>();
            AomPalette.MsvcrtQsort<QE>(arr, (in QE a, in QE b) =>
            {
                ours.Add(SlotOf(arr, in a)); ours.Add(SlotOf(arr, in b));
                return a.Key.CompareTo(b.Key) switch { < 0 => -1, > 0 => 1, _ => 0 };
            });
            cases++;
            if (ours.Count != 2 * calls || !ours.SequenceEqual(log.Take(2 * calls)))
                f.Add($"n {n} keys [{string.Join(",", keys)}]: calls ours {ours.Count / 2} msvcrt {calls}");
            else if (!arr.Select(e => e.Id).SequenceEqual(ids.Take(n)))
                f.Add($"n {n} keys [{string.Join(",", keys)}]: order ours [{string.Join(",", arr.Select(e => e.Id))}] msvcrt [{string.Join(",", ids.Take(n))}]");
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    private static int SlotOf(QE[] arr, in QE e) => (int)(Unsafe.ByteOffset(ref arr[0], ref Unsafe.AsRef(in e)) / Unsafe.SizeOf<QE>());

    // ---- k-means kernels --------------------------------------------------------------------------------------------

    private static unsafe long NativeCalcIndices(int dim, int which, short[] data, short[] cents, byte[] ind, bool wantDist, int n, int k)
    {
        fixed (short* d = data) fixed (short* c = cents) fixed (byte* i = ind)
            return ((delegate* unmanaged<int, int, short*, short*, byte*, int, int, int, long>)L("twin_calc_indices"))(dim, which, d, c, i, wantDist ? 1 : 0, n, k);
    }

    [Test]
    public async Task CalcIndices_Avx2_Dim1Dim2_TailsAndWrap()
    {
        if (!Available) return;
        int isAvx2;
        unsafe { isAvx2 = ((delegate* unmanaged<int>)L("twin_calc_indices_is_avx2"))(); }
        await Assert.That(isAvx2).IsEqualTo(1);
        var rng = new Random(2);
        var f = new List<string>();
        int cases = 0, cDiffers = 0;
        short[] data = new short[2 * 4096];
        short[] cents = new short[16];
        byte[] ind0 = new byte[4096 + 64], ind1 = new byte[4096 + 64];
        for (int it = 0; it < 6000; it++)
        {
            int dim = 1 + (it & 1);
            int n = 4 * rng.Next(1, 1025);
            int k = rng.Next(1, 9);
            bool adversarial = rng.Next(4) == 0;
            for (int i = 0; i < data.Length; i++)
                data[i] = i < dim * n ? (adversarial ? (short)rng.Next(-32768, 32768) : (short)rng.Next(256))
                                      : rng.Next(3) == 0 ? (short)rng.Next(-32768, 32768) : (short)rng.Next(256);
            for (int j = 0; j < 16; j++) cents[j] = adversarial ? (short)rng.Next(-32768, 32768) : (short)rng.Next(256);
            if (rng.Next(3) == 0) cents[dim * rng.Next(k)] = cents[0];   // duplicate centroids: tie-breaking
            rng.NextBytes(ind0); Array.Copy(ind0, ind1, ind0.Length);
            bool wantDist = rng.Next(4) != 0;
            long theirs = NativeCalcIndices(dim, 0, data, cents, ind1, wantDist, n, k);
            long ours = dim == 1 ? AomPalette.CalcIndicesDim1(data, cents, ind0, wantDist, n, k) : AomPalette.CalcIndicesDim2(data, cents, ind0, wantDist, n, k);
            cases++;
            if (wantDist && ours != theirs) f.Add($"dim {dim} n {n} k {k} adv {adversarial}: dist ours {ours} libaom {theirs}");
            else if (!ind0.AsSpan().SequenceEqual(ind1)) f.Add($"dim {dim} n {n} k {k} adv {adversarial}: indices {Diff<byte>(ind0, ind1)}");
            if (wantDist && !adversarial && n % 16 != 0)
            {
                byte[] indc = new byte[ind0.Length];
                if (NativeCalcIndices(dim, 1, data, cents, indc, true, n, k) != theirs) cDiffers++;
            }
        }
        await Assert.That(First(f, cases)).IsNull();
        // the C kernel differs from the AVX2 one on blocks whose point count is not a multiple of 16 (the padded tail)
        await Assert.That(cDiffers).IsGreaterThan(0);
    }

    // a realistic n: rows x cols of a (possibly cropped) luma or chroma block
    private static (int rows, int cols) RandomDims(Random rng, int unit)
    {
        int rows = unit * rng.Next(1, 64 / unit + 1), cols = unit * rng.Next(1, 64 / unit + 1);
        return (rows, cols);
    }

    [Test]
    public async Task KMeans_Dim1Dim2_AgainstLibaom()
    {
        if (!Available) return;
        var rng = new Random(3);
        var f = new List<string>();
        int cases = 0;
        short[] data0 = new short[2 * 4096];
        byte[] blk = new byte[64 * 64], blk2 = new byte[64 * 64];
        byte[] ind0 = new byte[4096 + 64], ind1 = new byte[4096 + 64];
        for (int it = 0; it < 6000; it++)
        {
            int dim = 1 + (it & 1);
            var (rows, cols) = RandomDims(rng, dim == 1 ? 4 : 2);
            int n = rows * cols;
            // stale content first (a previous block's data), then this block's
            for (int i = 0; i < data0.Length; i++) data0[i] = rng.Next(8) == 0 ? (short)rng.Next(-300, 600) : (short)rng.Next(256);
            FillBlock(rng, blk, 0, cols, cols, rows, rng.Next(7));
            FillBlock(rng, blk2, 0, cols, cols, rows, rng.Next(7));
            int lb = 255, ub = 0, lb2 = 255, ub2 = 0;
            for (int i = 0; i < n; i++)
            {
                if (dim == 1) data0[i] = blk[i];
                else { data0[2 * i] = blk[i]; data0[2 * i + 1] = blk2[i]; }
                lb = Math.Min(lb, blk[i]); ub = Math.Max(ub, blk[i]); lb2 = Math.Min(lb2, blk2[i]); ub2 = Math.Max(ub2, blk2[i]);
            }
            int k = rng.Next(2, 9);
            short[] c0 = new short[16];
            bool evenly = rng.Next(4) != 0;
            for (int i = 0; i < k; i++)
            {
                if (dim == 1) c0[i] = evenly ? (short)(lb + (2 * i + 1) * (ub - lb) / k / 2) : (short)rng.Next(256);
                else
                {
                    c0[2 * i] = evenly ? (short)(lb + (2 * i + 1) * (ub - lb) / k / 2) : (short)rng.Next(256);
                    c0[2 * i + 1] = evenly ? (short)(lb2 + (2 * i + 1) * (ub2 - lb2) / k / 2) : (short)rng.Next(256);
                }
            }
            short[] c1 = (short[])c0.Clone();
            short[] data1 = (short[])data0.Clone();
            int maxItr = rng.Next(4) == 0 ? rng.Next(1, 6) : 50;
            rng.NextBytes(ind0); Array.Copy(ind0, ind1, ind0.Length);
            unsafe
            {
                fixed (short* d = data1) fixed (short* c = c1) fixed (byte* i = ind1)
                    ((delegate* unmanaged<int, short*, short*, byte*, int, int, int, void>)L("twin_k_means"))(dim, d, c, i, n, k, maxItr);
            }
            AomPalette.KMeans(data0, c0, ind0, n, k, dim, maxItr);
            cases++;
            if (!c0.AsSpan(0, k * dim).SequenceEqual(c1.AsSpan(0, k * dim)))
                f.Add($"dim {dim} {rows}x{cols} k {k} itr {maxItr}: centroids {Diff<short>(c0.AsSpan(0, k * dim), c1.AsSpan(0, k * dim))}");
            else if (!ind0.AsSpan().SequenceEqual(ind1))
                f.Add($"dim {dim} {rows}x{cols} k {k} itr {maxItr}: indices {Diff<byte>(ind0, ind1)}");
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    // ---- palette color costs / cache --------------------------------------------------------------------------------

    private static AomPaletteModeInfo Pmi(ushort[] colors24, int s0, int s1)
        => new() { PaletteColors = (ushort[])colors24.Clone(), PaletteSize0 = (byte)s0, PaletteSize1 = (byte)s1 };

    private static ushort[] RandomColors24(Random rng)
    {
        var c = new ushort[3 * PMax];
        for (int p = 0; p < 3; p++)
        {
            int n = rng.Next(2, 9);
            ushort[] v = p < 2 && rng.Next(5) != 0 ? SortedUnique(rng, n, rng.Next(4) == 0 ? 24 : 256)
                                                   : Enumerable.Range(0, n).Select(_ => (ushort)rng.Next(256)).ToArray();
            if (p == 2 && rng.Next(3) == 0) for (int i = 1; i < n; i++) if (rng.Next(2) == 0) v[i] = v[i - 1];   // v deltas of 0
            if (p == 2 && rng.Next(3) == 0) for (int i = 0; i < n; i++) v[i] = (ushort)(rng.Next(2) == 0 ? rng.Next(4) : 252 + rng.Next(4));   // wrap
            v.CopyTo(c, p * PMax);
            for (int i = n; i < PMax; i++) c[p * PMax + i] = (ushort)rng.Next(256);
        }
        return c;
    }

    [Test]
    public async Task ColorCosts_IndexCacheDeltaBitsCostYUv()
    {
        if (!Available) return;
        var rng = new Random(4);
        var f = new List<string>();
        int cases = 0;
        for (int it = 0; it < 30000; it++)
        {
            ushort[] colors = RandomColors24(rng);
            int s0 = rng.Next(2, 9), s1 = rng.Next(2, 9);
            // the Y / U palettes are ascending and unique in the encoder
            SortedUnique(rng, s0).CopyTo(colors, 0);
            SortedUnique(rng, s1).CopyTo(colors, PMax);
            int nCache = rng.Next(0, 17);
            ushort[] cache = SortedUnique(rng, nCache).Concat(new ushort[16]).Take(16).ToArray();
            // make some palette colors hit the cache
            for (int i = 0; i < s0 && nCache > 0; i++) if (rng.Next(3) == 0) colors[i] = cache[rng.Next(nCache)];
            Array.Sort(colors, 0, s0);
            for (int i = 0; i < s1 && nCache > 0; i++) if (rng.Next(3) == 0) colors[PMax + i] = cache[rng.Next(nCache)];
            Array.Sort(colors, PMax, s1);
            var pmi = Pmi(colors, s0, s1);
            int y0, y1, uv0, uv1, dbv0, dbv1, zc0, zc1, mb0, mb1, ic0, ic1;
            byte[] found0 = new byte[16], found1 = new byte[16];
            int[] out0 = new int[8], out1 = new int[8];
            int nColors = rng.Next(1, 9);
            unsafe
            {
                fixed (ushort* c = colors) fixed (ushort* ca = cache) fixed (byte* fo = found1) fixed (int* o = out1)
                {
                    y1 = ((delegate* unmanaged<ushort*, int, ushort*, int, int, int>)L("twin_color_cost_y"))(c, s0, ca, nCache, 8);
                    uv1 = ((delegate* unmanaged<ushort*, int, ushort*, int, int, int>)L("twin_color_cost_uv"))(c, s1, ca, nCache, 8);
                    dbv1 = ((delegate* unmanaged<ushort*, int, int, int*, int*, int>)L("twin_delta_bits_v"))(c, s1, 8, &zc1, &mb1);
                    ic1 = ((delegate* unmanaged<ushort*, int, ushort*, int, byte*, int*, int>)L("twin_index_color_cache"))(ca, nCache, c, nColors, fo, o);
                }
            }
            y0 = AomPalette.PaletteColorCostY(pmi, cache, nCache, 8);
            uv0 = AomPalette.PaletteColorCostUv(pmi, cache, nCache, 8);
            dbv0 = AomPalette.GetPaletteDeltaBitsV(pmi, 8, out zc0, out mb0);
            ic0 = AomPalette.IndexColorCache(cache, nCache, colors, nColors, found0, out0);
            cases++;
            if (y0 != y1) f.Add($"cost_y ours {y0} libaom {y1}");
            else if (uv0 != uv1) f.Add($"cost_uv ours {uv0} libaom {uv1}");
            else if (dbv0 != dbv1 || zc0 != zc1 || mb0 != mb1) f.Add($"delta_bits_v ours {dbv0}/{zc0}/{mb0} libaom {dbv1}/{zc1}/{mb1}");
            else if (ic0 != ic1 || !out0.AsSpan(0, ic0).SequenceEqual(out1.AsSpan(0, ic1)) || !found0.AsSpan(0, nCache).SequenceEqual(found1.AsSpan(0, nCache)))
                f.Add($"index_color_cache ours {ic0} libaom {ic1}");
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    [Test]
    public async Task PaletteCache_AboveLeftMerge()
    {
        if (!Available) return;
        var rng = new Random(5);
        var f = new List<string>();
        int cases = 0;
        for (int it = 0; it < 30000; it++)
        {
            bool hasAbove = rng.Next(4) != 0, hasLeft = rng.Next(4) != 0;
            ushort[] a = RandomColors24(rng), l = RandomColors24(rng);
            int as0 = rng.Next(0, 9), as1 = rng.Next(0, 9), ls0 = rng.Next(0, 9), ls1 = rng.Next(0, 9);
            for (int p = 0; p < 2; p++)
            {
                SortedUnique(rng, 8, rng.Next(2) == 0 ? 20 : 256).CopyTo(a, p * PMax);
                SortedUnique(rng, 8, rng.Next(2) == 0 ? 20 : 256).CopyTo(l, p * PMax);
            }
            int plane = rng.Next(3);
            int mbToTop = -8 * 4 * rng.Next(0, 64);
            ushort[] c1 = new ushort[16];
            int n1;
            unsafe
            {
                fixed (ushort* pa = a) fixed (ushort* pl = l) fixed (ushort* pc = c1)
                    n1 = ((delegate* unmanaged<int, ushort*, int, int, ushort*, int, int, int, ushort*, int>)L("twin_palette_cache"))(
                        mbToTop, hasAbove ? pa : null, as0, as1, hasLeft ? pl : null, ls0, ls1, plane, pc);
            }
            var xd = new AomMacroblockD { MbToTopEdge = mbToTop };
            if (hasAbove) { xd.AboveMbmi = new AomMbModeInfo(); a.CopyTo(xd.AboveMbmi.Palette.PaletteColors, 0); xd.AboveMbmi.Palette.PaletteSize0 = (byte)as0; xd.AboveMbmi.Palette.PaletteSize1 = (byte)as1; }
            if (hasLeft) { xd.LeftMbmi = new AomMbModeInfo(); l.CopyTo(xd.LeftMbmi.Palette.PaletteColors, 0); xd.LeftMbmi.Palette.PaletteSize0 = (byte)ls0; xd.LeftMbmi.Palette.PaletteSize1 = (byte)ls1; }
            ushort[] c0 = new ushort[16];
            int n0 = AomPalette.GetPaletteCache(xd, plane, c0);
            cases++;
            if (n0 != n1 || !c0.AsSpan(0, n0).SequenceEqual(c1.AsSpan(0, n1))) f.Add($"plane {plane} top {mbToTop}: n ours {n0} libaom {n1}");
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    // ---- colour index contexts / colour map cost --------------------------------------------------------------------

    [Test]
    public async Task ColorIndexContext_DecoderAndFastEncoder()
    {
        if (!Available) return;
        var rng = new Random(6);
        var f = new List<string>();
        int cases = 0;
        byte[] map = new byte[64 * 64];
        byte[] o0 = new byte[8], o1 = new byte[8];
        for (int it = 0; it < 60000; it++)
        {
            int w = 4 * rng.Next(1, 17), h = 4 * rng.Next(1, 17), n = rng.Next(2, 9);
            int levels = rng.Next(2) == 0 ? Math.Min(n, rng.Next(1, 4)) : n;
            for (int i = 0; i < w * h; i++) map[i] = (byte)rng.Next(levels);
            int r, c;
            do { r = rng.Next(h); c = rng.Next(w); } while (r == 0 && c == 0);
            int ctx1, idx1, fctx1, fidx1;
            unsafe
            {
                fixed (byte* m = map) fixed (byte* o = o1)
                {
                    ctx1 = ((delegate* unmanaged<byte*, int, int, int, int, byte*, int*, int>)L("twin_color_index_context"))(m, w, r, c, n, o, &idx1);
                    fctx1 = ((delegate* unmanaged<byte*, int, int, int, int*, int>)S("twin_fast_ctx"))(m, w, r, c, &fidx1);
                }
            }
            int ctx0 = AomPalette.GetPaletteColorIndexContext(map, w, r, c, n, o0, out int idx0);
            int fctx0 = AomPalette.FastPaletteColorIndexContext(map, w, r, c, out int fidx0);
            cases++;
            if (ctx0 != ctx1 || idx0 != idx1 || !o0.AsSpan().SequenceEqual(o1)) f.Add($"decoder ctx ours {ctx0}/{idx0} libaom {ctx1}/{idx1}");
            else if (fctx0 != fctx1 || fidx0 != fidx1) f.Add($"fast ctx ({r},{c}) ours {fctx0}/{fidx0} libaom {fctx1}/{fidx1}");
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    private static readonly (int ssx, int ssy)[] Subsamplings = { (1, 1), (0, 0), (1, 0) };

    private static (int right, int bottom) RandomCrop(Random rng, int bsize)
    {
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int right = rng.Next(3) == 0 ? -8 * 4 * rng.Next(0, bw / 4) : 8 * 4 * rng.Next(0, 8);
        int bottom = rng.Next(3) == 0 ? -8 * 4 * rng.Next(0, bh / 4) : 8 * 4 * rng.Next(0, 8);
        return (right, bottom);
    }

    private static int[] RandomCosts(Random rng, int n) => Enumerable.Range(0, n).Select(_ => rng.Next(0, 6000)).ToArray();

    [Test]
    public async Task CostColorMap_AllBsizesPlanesCrops()
    {
        if (!Available) return;
        var rng = new Random(7);
        var f = new List<string>();
        int cases = 0;
        byte[] map = new byte[128 * 128];
        foreach (int bsize in PaletteBsizes)
            for (int it = 0; it < 800; it++)
            {
                int plane = it & 1;
                var (ssx, ssy) = Subsamplings[rng.Next(3)];
                var (right, bottom) = RandomCrop(rng, bsize);
                int n = rng.Next(2, 9);
                int levels = rng.Next(3) == 0 ? rng.Next(1, n + 1) : n;
                for (int i = 0; i < map.Length; i++) map[i] = (byte)(rng.Next(4) == 0 ? rng.Next(levels) : i / 7 % levels);
                int[] costs = RandomCosts(rng, 7 * 5 * 8);
                int theirs;
                unsafe
                {
                    fixed (byte* m = map) fixed (int* cc = costs)
                        theirs = ((delegate* unmanaged<int, int, int, int, int, int, int, byte*, int*, int>)L("twin_cost_color_map"))(
                            plane, bsize, ssx, ssy, right, bottom, n, m, cc);
                }
                var x = new AomMacroblock();
                var xd = x.E;
                xd.Mi0 = new AomMbModeInfo { Bsize = bsize };
                if (plane == 0) xd.Mi0.Palette.PaletteSize0 = (byte)n; else xd.Mi0.Palette.PaletteSize1 = (byte)n;
                xd.MbToRightEdge = right; xd.MbToBottomEdge = bottom;
                for (int p = 1; p < 3; p++) { xd.Plane[p].SubsamplingX = ssx; xd.Plane[p].SubsamplingY = ssy; }
                xd.Plane[plane].ColorIndexMap = map;
                costs.CopyTo(plane != 0 ? x.ModeCosts.PaletteUvColorCost : x.ModeCosts.PaletteYColorCost, 0);
                int ours = AomPalette.CostColorMap(x, plane, bsize, TX_4X4, PALETTE_MAP);
                cases++;
                if (ours != theirs) f.Add($"bsize {bsize} plane {plane} ss {ssx}{ssy} crop {right},{bottom} n {n}: ours {ours} libaom {theirs}");
            }
        await Assert.That(First(f, cases)).IsNull();
    }

    // ---- colour counting ---------------------------------------------------------------------------------------------

    [Test]
    public async Task CountColors_AndWithThreshold()
    {
        if (!Available) return;
        var rng = new Random(8);
        var f = new List<string>();
        int cases = 0;
        byte[] src = new byte[80 * 64];
        int[] v0 = new int[256], v1 = new int[256];
        for (int it = 0; it < 20000; it++)
        {
            int rows = rng.Next(1, 65), cols = rng.Next(1, 65), stride = 80;
            FillBlock(rng, src, 0, stride, cols, rows, rng.Next(7));
            int thr = rng.Next(0, 80);
            int n1, t1, num1;
            unsafe
            {
                fixed (byte* s = src) fixed (int* v = v1)
                {
                    n1 = ((delegate* unmanaged<byte*, int, int, int, int*, int>)L("twin_count_colors"))(s, stride, rows, cols, v);
                    t1 = ((delegate* unmanaged<byte*, int, int, int, int, int*, int>)L("twin_count_colors_thr"))(s, stride, rows, cols, thr, &num1);
                }
            }
            int n0 = AomPalette.CountColors(src, 0, stride, rows, cols, v0);
            bool t0 = AomPalette.CountColorsWithThreshold(src, 0, stride, rows, cols, thr, out int num0);
            cases++;
            if (n0 != n1 || !v0.AsSpan().SequenceEqual(v1)) f.Add($"count_colors ours {n0} libaom {n1}");
            else if ((t0 ? 1 : 0) != (t1 & 0xff) || num0 != num1) f.Add($"with_threshold {thr}: ours {t0}/{num0} libaom {t1 & 0xff}/{num1}");
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    // ---- av1_restore_uv_color_map -----------------------------------------------------------------------------------

    [Test]
    public async Task RestoreUvColorMap_AllBsizesSubsamplingsCrops()
    {
        if (!Available) return;
        var rng = new Random(9);
        var f = new List<string>();
        int cases = 0;
        const int stride = 72;
        byte[] u = new byte[stride * 72], v = new byte[stride * 72];
        foreach (int bsize in PaletteBsizes)
            for (int it = 0; it < 300; it++)
            {
                var (ssx, ssy) = Subsamplings[rng.Next(3)];
                var (right, bottom) = RandomCrop(rng, bsize);
                FillBlock(rng, u, 0, stride, 68, 68, rng.Next(7));
                FillBlock(rng, v, 0, stride, 68, 68, rng.Next(7));
                ushort[] colors = RandomColors24(rng);
                int s1 = rng.Next(2, 9);
                short[] k0 = new short[8192];
                for (int i = 0; i < k0.Length; i++) k0[i] = (short)rng.Next(-200, 400);
                short[] k1 = (short[])k0.Clone();
                byte[] m0 = new byte[128 * 128];
                rng.NextBytes(m0);
                byte[] m1 = (byte[])m0.Clone();
                unsafe
                {
                    fixed (byte* pu = u) fixed (byte* pv = v) fixed (ushort* c = colors) fixed (short* kb = k1) fixed (byte* m = m1)
                        ((delegate* unmanaged<int, int, int, int, int, byte*, byte*, int, ushort*, int, short*, byte*, void>)L("twin_restore_uv"))(
                            bsize, ssx, ssy, right, bottom, pu, pv, stride, c, s1, kb, m);
                }
                var cpi = new AomComp();
                var x = new AomMacroblock();
                var xd = x.E;
                xd.Mi0 = new AomMbModeInfo { Bsize = bsize };
                colors.CopyTo(xd.Mi0.Palette.PaletteColors, 0);
                xd.Mi0.Palette.PaletteSize1 = (byte)s1;
                xd.MbToRightEdge = right; xd.MbToBottomEdge = bottom;
                for (int p = 1; p < 3; p++) { xd.Plane[p].SubsamplingX = ssx; xd.Plane[p].SubsamplingY = ssy; }
                xd.Plane[1].ColorIndexMap = m0;
                x.Plane[1].Src = new AomBuf2d { Buf = u, Offset = 0, Stride = stride };
                x.Plane[2].Src = new AomBuf2d { Buf = v, Offset = 0, Stride = stride };
                k0.CopyTo(x.KmeansDataBuf, 0);
                AomPalette.RestoreUvColorMap(cpi, x);
                cases++;
                if (!x.KmeansDataBuf.AsSpan().SequenceEqual(k1)) f.Add($"bsize {bsize} ss {ssx}{ssy}: kmeans buf {Diff<short>(x.KmeansDataBuf, k1)}");
                else if (!m0.AsSpan().SequenceEqual(m1)) f.Add($"bsize {bsize} ss {ssx}{ssy} crop {right},{bottom} n {s1}: map {Diff<byte>(m0, m1)}");
            }
        await Assert.That(First(f, cases)).IsNull();
    }

    // ---- palette.c statics ------------------------------------------------------------------------------------------

    [Test]
    public async Task Statics_RemoveDuplicatesDeltaEncodeOptimizeStage2()
    {
        if (!Available) return;
        var rng = new Random(10);
        var f = new List<string>();
        int cases = 0;
        for (int it = 0; it < 40000; it++)
        {
            // remove_duplicates
            int n = rng.Next(1, 9);
            short[] c0 = Enumerable.Range(0, 8).Select(_ => (short)(rng.Next(3) == 0 ? rng.Next(-40, 300) : rng.Next(6))).ToArray();
            short[] c1 = (short[])c0.Clone();
            int u1;
            unsafe { fixed (short* p = c1) u1 = ((delegate* unmanaged<short*, int, int>)S("twin_remove_duplicates"))(p, n); }
            int u0 = AomPalette.RemoveDuplicates(c0, n);
            if (u0 != u1 || !c0.AsSpan().SequenceEqual(c1)) f.Add($"remove_duplicates n {n}: ours {u0} libaom {u1}");

            // delta_encode_cost (ascending colors, min_val 0 / 1)
            int num = rng.Next(0, 9), minVal = rng.Next(2);
            int[] cols = SortedUnique(rng, num, rng.Next(2) == 0 ? 16 : 256).Select(x => (int)x).Concat(new int[8]).Take(8).ToArray();
            if (minVal == 0 && num > 1 && rng.Next(2) == 0) cols[1] = cols[0];
            if (num > 1) Array.Sort(cols, 0, num);
            int d1;
            unsafe { fixed (int* p = cols) d1 = ((delegate* unmanaged<int*, int, int, int, int>)S("twin_delta_encode_cost"))(p, num, 8, minVal); }
            int d0 = AomPalette.DeltaEncodeCost(cols, num, 8, minVal);
            if (d0 != d1) f.Add($"delta_encode_cost num {num} min {minVal}: ours {d0} libaom {d1}");

            // optimize_palette_colors
            int nCache = rng.Next(0, 17), nColors = rng.Next(1, 9), stride = rng.Next(1, 3);
            ushort[] cache = SortedUnique(rng, nCache).Concat(new ushort[16]).Take(16).ToArray();
            short[] o0 = Enumerable.Range(0, 16).Select(_ => (short)rng.Next(-10, 270)).ToArray();
            for (int i = 0; i < nColors * stride && nCache > 0; i += stride) if (rng.Next(2) == 0) o0[i] = (short)(cache[rng.Next(nCache)] + rng.Next(-6, 7));
            short[] o1 = (short[])o0.Clone();
            unsafe { fixed (ushort* ca = cache) fixed (short* p = o1) ((delegate* unmanaged<ushort*, int, int, int, short*, int, void>)S("twin_optimize_colors"))(ca, nCache, nColors, stride, p, 8); }
            AomPalette.OptimizePaletteColors(cache, nCache, nColors, stride, o0, 8);
            if (!o0.AsSpan().SequenceEqual(o1)) f.Add($"optimize_palette_colors: {Diff<short>(o0, o1)}");

            // set_stage2_params
            int endN = rng.Next(2, 9), winner = rng.Next(2, endN + 1);
            int[] s1 = new int[3];
            unsafe { fixed (int* p = s1) ((delegate* unmanaged<int, int, int*, void>)S("twin_set_stage2"))(winner, endN, p); }
            AomPalette.SetStage2Params(out int a0, out int b0, out int st0, winner, endN);
            if (a0 != s1[0] || b0 != s1[1] || st0 != s1[2]) f.Add($"set_stage2 winner {winner} end {endN}");
            cases++;
        }
        await Assert.That(First(f, cases)).IsNull();
    }

    [Test]
    public async Task Statics_ExtendMapFillDataFindTopColors()
    {
        if (!Available) return;
        var rng = new Random(11);
        var f = new List<string>();
        int cases = 0;
        byte[] src = new byte[80 * 64];
        int[] countBuf = new int[4096];
        for (int it = 0; it < 20000; it++)
        {
            // extend_palette_color_map
            int nw = 4 * rng.Next(1, 17), nh = 4 * rng.Next(1, 17);
            int ow = rng.Next(3) == 0 ? nw : 2 * rng.Next(1, nw / 2 + 1), oh = rng.Next(3) == 0 ? nh : 2 * rng.Next(1, nh / 2 + 1);
            byte[] m0 = new byte[64 * 64 + 64];
            rng.NextBytes(m0);
            byte[] m1 = (byte[])m0.Clone();
            unsafe { fixed (byte* p = m1) ((delegate* unmanaged<byte*, int, int, int, int, void>)S("twin_extend_map"))(p, ow, oh, nw, nh); }
            AomPalette.ExtendPaletteColorMap(m0, ow, oh, nw, nh);
            if (!m0.AsSpan().SequenceEqual(m1)) f.Add($"extend {ow}x{oh} -> {nw}x{nh}: {Diff<byte>(m0, m1)}");

            // fill_data_and_get_bounds
            int rows = rng.Next(1, 65), cols = rng.Next(1, 65);
            FillBlock(rng, src, 0, 80, cols, rows, rng.Next(7));
            short[] d0 = new short[4096], d1 = new short[4096];
            int lb1, ub1;
            unsafe { fixed (byte* s = src) fixed (short* d = d1) ((delegate* unmanaged<byte*, int, int, int, short*, int*, int*, void>)S("twin_fill_data"))(s, 80, rows, cols, d, &lb1, &ub1); }
            AomPalette.FillDataAndGetBounds(src, 0, 80, rows, cols, false, d0, out int lb0, out int ub0);
            if (lb0 != lb1 || ub0 != ub1 || !d0.AsSpan().SequenceEqual(d1)) f.Add($"fill_data {rows}x{cols}");

            // find_top_colors (count ties broken by index)
            Array.Clear(countBuf);
            int distinct = rng.Next(1, 40);
            int maxCount = rng.Next(1, 5);
            for (int i = 0; i < distinct; i++) countBuf[rng.Next(256)] = rng.Next(1, maxCount + 1);
            int colors = countBuf.Take(256).Count(c => c > 0);
            int nColors = Math.Min(colors, PMax);
            short[] t0 = new short[8], t1 = new short[8];
            unsafe { fixed (int* cb = countBuf) fixed (short* t = t1) ((delegate* unmanaged<int*, int, int, short*, void>)S("twin_find_top_colors"))(cb, 8, nColors, t); }
            AomPalette.FindTopColors(countBuf, 8, nColors, t0);
            if (!t0.AsSpan().SequenceEqual(t1)) f.Add($"find_top_colors {colors} colors: {Diff<short>(t0, t1)}");
            cases++;
        }
        await Assert.That(First(f, cases)).IsNull();
    }
}
