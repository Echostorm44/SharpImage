using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Twins of the CDEF kernels (AomCdef: cdef_find_dir / cdef_find_dir_dual, cdef_filter_8_*, cdef_copy_rect8_8bit_to_16bit,
// aom_sse, av1_cdef_filter_fb) against libaom 3.14.1's RTCD-dispatched (AVX2) kernels through aomtwin_cdef.dll
// (scratchpad aomtwin_cdef/: twin_cdef.c, build.sh). Without the DLL the tests still cross-check the AVX2 ports against
// the C-reference ports. Opt-in: SHARPIMAGE_AOMTWIN_CDEF=<path to aomtwin_cdef.dll>.
[NotInParallel]
public sealed class AomCdefTwinTests
{
    private static readonly string? DllPath = Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_CDEF");
    private static readonly bool Available = DllPath != null && (File.Exists(DllPath)
        ? Load() : throw new FileNotFoundException("SHARPIMAGE_AOMTWIN_CDEF is set but the DLL does not exist", DllPath));

    private static bool Load()
    {
        NativeLibrary.SetDllImportResolver(typeof(AomCdefTwinTests).Assembly, (name, asm, path) =>
            name == "aomtwin_cdef" ? NativeLibrary.Load(DllPath!) : IntPtr.Zero);
        Native.twin_init();
        return true;
    }

    private static unsafe class Native
    {
        private const string D = "aomtwin_cdef";
        [DllImport(D)] public static extern void twin_init();
        [DllImport(D)] public static extern int twin_cdef_find_dir(ushort* img, int stride, int* var, int coeffShift);
        [DllImport(D)] public static extern void twin_cdef_find_dir_dual(ushort* img1, ushort* img2, int stride, int* var1, int* var2,
            int coeffShift, int* out1, int* out2);
        [DllImport(D)] public static extern void twin_cdef_filter_8(int idx, byte* dst, int dstride, ushort* inp, int pri, int sec, int dir,
            int priDamping, int secDamping, int coeffShift, int bw, int bh);
        [DllImport(D)] public static extern void twin_cdef_copy_rect8(ushort* dst, int dstride, byte* src, int sstride, int w, int h);
        [DllImport(D)] public static extern long twin_aom_sse(byte* a, int astr, byte* b, int bstr, int w, int h);
        [DllImport(D)] public static extern void twin_cdef_filter_fb(byte* dst8, int dstride, ushort* inp, int xdec, int ydec, int* dir,
            int useDirinit, int* dirinitIo, int* var, int pli, byte* dlist, int count, int level, int sec, int damping, int coeffShift);
    }

    private const int BStride = AomCdef.BStride, InOff = AomCdef.InOff, Size = AomCdef.InbufSize;

    /// <summary>A CDEF input buffer: smooth-ish / noisy / flat / extreme 8-bit content (or 10-bit with coeffShift 2),
    /// optionally CDEF_VERY_LARGE in frame-boundary bands as fill_borders_for_fbs_on_frame_boundary leaves them.</summary>
    private static ushort[] RandomInbuf(Random rng, int coeffShift, bool borders)
    {
        var b = new ushort[Size];
        int maxv = (256 << coeffShift) - 1;
        int mode = rng.Next(6);
        int basev = rng.Next(maxv + 1), noise = rng.Next(1, 40) << coeffShift;
        for (int i = 0; i < Size; i++)
        {
            int v = mode switch
            {
                0 => rng.Next(maxv + 1),
                1 => basev + rng.Next(-noise, noise + 1),
                2 => basev,
                3 => rng.Next(2) == 0 ? 0 : maxv,
                4 => ((i % BStride) * 3 + (i / BStride) * 2) << coeffShift,
                _ => basev + ((i / BStride) % 8 < 4 ? noise : -noise) + rng.Next(-2, 3),
            };
            b[i] = (ushort)Math.Clamp(v, 0, maxv);
        }
        if (borders)
        {
            int rows = Size / BStride;
            if (rng.Next(2) == 0) for (int r = 0; r < 2; r++) for (int c = 0; c < BStride; c++) b[r * BStride + c] = AomCdef.VeryLarge;
            if (rng.Next(2) == 0) for (int r = 0; r < rows; r++) for (int c = 0; c < 8; c++) b[r * BStride + c] = AomCdef.VeryLarge;
            int bottom = rng.Next(2 + 64, rows), right = rng.Next(8 + 64, BStride);   // outside the 64x64 filter area
            if (rng.Next(2) == 0) for (int r = bottom; r < rows; r++) for (int c = 0; c < BStride; c++) b[r * BStride + c] = AomCdef.VeryLarge;
            if (rng.Next(2) == 0) for (int r = 0; r < rows; r++) for (int c = right; c < BStride; c++) b[r * BStride + c] = AomCdef.VeryLarge;
        }
        return b;
    }

    private static unsafe T* Aligned<T>(int count) where T : unmanaged => (T*)NativeMemory.AlignedAlloc((nuint)(count * sizeof(T)), 64);

    [Test]
    public async Task FindDir_MatchesLibaomDispatched() => await Assert.That(FindDir_MatchesLibaomDispatchedImpl()).IsEqualTo(0);

    private static unsafe int FindDir_MatchesLibaomDispatchedImpl()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) return 0;
        var rng = new Random(5);
        ushort* nat = Aligned<ushort>(Size);
        int mismatches = 0;
        try
        {
            for (int iter = 0; iter < 20000; iter++)
            {
                int cs = iter % 5 == 0 ? 2 : 0;
                var b = RandomInbuf(rng, cs, false);
                fixed (ushort* bp = b)
                {
                    int o1 = InOff + rng.Next(8) * 8 * BStride + rng.Next(8) * 8;
                    int o2 = InOff + rng.Next(8) * 8 * BStride + rng.Next(8) * 8;
                    int cref1 = AomCdef.FindDir(b, o1, BStride, out int vref1, cs);
                    int cref2 = AomCdef.FindDir(b, o2, BStride, out int vref2, cs);
                    AomCdef.FindDirDualAvx2(bp + o1, bp + o2, BStride, out int va1, out int va2, cs, out int da1, out int da2);
                    AomCdef.FindDirDualAvx2(bp + o1, bp + o1, BStride, out int vs1, out _, cs, out int ds1, out _);
                    if (cref1 != da1 || vref1 != va1 || cref2 != da2 || vref2 != va2 || ds1 != cref1 || vs1 != vref1) mismatches++;
                    if (Available)
                    {
                        new Span<ushort>(b).CopyTo(new Span<ushort>(nat, Size));
                        int tv1, tv2, td1, td2, tvs;
                        Native.twin_cdef_find_dir_dual(nat + o1, nat + o2, BStride, &tv1, &tv2, cs, &td1, &td2);
                        int tds = Native.twin_cdef_find_dir(nat + o1, BStride, &tvs, cs);
                        if (td1 != da1 || tv1 != va1 || td2 != da2 || tv2 != va2 || tds != ds1 || tvs != vs1) mismatches++;
                    }
                }
            }
        }
        finally { NativeMemory.AlignedFree(nat); }
        return mismatches;
    }

    [Test]
    public async Task Filter8_AllVariants_MatchLibaomDispatched() => await Assert.That(Filter8_AllVariants_MatchLibaomDispatchedImpl()).IsEqualTo(0);

    private static unsafe int Filter8_AllVariants_MatchLibaomDispatchedImpl()
    {
        var rng = new Random(9);
        ushort* nat = Aligned<ushort>(Size);
        byte* natDst = Aligned<byte>(16 * 16);
        int mismatches = 0, runs = 0;
        (int bw, int bh)[] shapes = { (8, 8), (4, 4), (4, 8), (8, 4) };
        try
        {
            for (int iter = 0; iter < 60000; iter++)
            {
                const int cs = 0;   // the 8-bit output kernels run on 8-bit input only
                var b = RandomInbuf(rng, cs, rng.Next(2) == 0);
                var (bw, bh) = shapes[rng.Next(4)];
                int idx = rng.Next(4);
                int pri = idx < 2 ? rng.Next(1, 16) << cs : 0;
                if (idx < 2 && rng.Next(4) == 0) pri = (rng.Next(1, 16) * (4 + rng.Next(13)) + 8) >> 4 << cs;   // adjust_strength results
                if (idx < 2 && pri == 0) pri = 1 << cs;
                int[] secs = { 1, 2, 4 };
                int sec = idx == 0 || idx == 2 ? secs[rng.Next(3)] << cs : 0;
                int dir = rng.Next(8);
                int damping = rng.Next(3, 7) + cs - rng.Next(2);
                if (iter % 50 == 0) { pri = 15 << cs; damping = 2; }
                // the block somewhere inside the 64x64 filter area (8-aligned like the encoder's)
                int off = InOff + rng.Next(0, 64 / bh) * bh * BStride + rng.Next(0, 64 / bw) * bw;
                var ours = new byte[16 * 16];
                var vref = new byte[16 * 16];
                var cref = new byte[16 * 16];
                if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                    fixed (ushort* bp = b) fixed (byte* op = ours)
                        AomCdef.FilterBlock8Avx2(idx, op, 16, (short*)bp + off, pri, sec, dir, damping, damping, cs, bw, bh);
                else
                    AomCdef.FilterBlockV(ours, 0, 16, b, off, pri, sec, dir, damping, damping, cs, bw, bh, pri != 0, sec != 0);
                AomCdef.FilterBlock(cref, 0, 16, b, off, pri, sec, dir, damping, damping, cs, bw, bh, idx == 0 || idx == 1, idx == 0 || idx == 2);
                runs++;
                bool bad = false;
                for (int r = 0; r < bh; r++)
                    for (int c = 0; c < bw; c++)
                        if (ours[r * 16 + c] != cref[r * 16 + c]) bad = true;
                if (Available)
                {
                    new Span<ushort>(b).CopyTo(new Span<ushort>(nat, Size));
                    new Span<byte>(natDst, 256).Clear();
                    Native.twin_cdef_filter_8(idx, natDst, 16, nat + off, pri, sec, dir, damping, damping, cs, bw, bh);
                    for (int r = 0; r < bh; r++)
                        for (int c = 0; c < bw; c++)
                            if (ours[r * 16 + c] != natDst[r * 16 + c]) bad = true;
                }
                if (bad) mismatches++;
            }
        }
        finally { NativeMemory.AlignedFree(nat); NativeMemory.AlignedFree(natDst); }
        return mismatches + (runs == 60000 ? 0 : 1);
    }

    [Test]
    public async Task CopyRect8To16_MatchesLibaomDispatched() => await Assert.That(CopyRect8To16_MatchesLibaomDispatchedImpl()).IsEqualTo(0);

    private static unsafe int CopyRect8To16_MatchesLibaomDispatchedImpl()
    {
        var rng = new Random(13);
        int mismatches = 0;
        const int sstride = 200;
        byte* src = Aligned<byte>(sstride * 140);
        ushort* a = Aligned<ushort>(Size), t = Aligned<ushort>(Size);
        try
        {
            for (int iter = 0; iter < 5000; iter++)
            {
                for (int i = 0; i < sstride * 140; i++) src[i] = (byte)rng.Next(256);
                int w = rng.Next(1, BStride - 8 + 1), h = rng.Next(1, 66) * 2;
                int so = rng.Next(0, 40);
                for (int i = 0; i < Size; i++) a[i] = t[i] = 0x1234;
                AomCdef.CopyRect8To16(a + 3, BStride, src + so, sstride, w, h);
                if (Available) Native.twin_cdef_copy_rect8(t + 3, BStride, src + so, sstride, w, h);
                else for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) t[3 + r * BStride + c] = src[so + r * sstride + c];
                if (!new Span<ushort>(a, Size).SequenceEqual(new Span<ushort>(t, Size))) mismatches++;
            }
        }
        finally { NativeMemory.AlignedFree(src); NativeMemory.AlignedFree(a); NativeMemory.AlignedFree(t); }
        return mismatches;
    }

    [Test]
    public async Task Sse_MatchesLibaomDispatched() => await Assert.That(Sse_MatchesLibaomDispatchedImpl()).IsEqualTo(0);

    private static unsafe int Sse_MatchesLibaomDispatchedImpl()
    {
        if (!Available) return 0;
        var rng = new Random(17);
        int mismatches = 0;
        var a = new byte[160 * 140];
        var b = new byte[160 * 140];
        for (int iter = 0; iter < 5000; iter++)
        {
            bool extreme = iter % 4 == 0;
            for (int i = 0; i < a.Length; i++) { a[i] = (byte)(extreme ? 255 : rng.Next(256)); b[i] = (byte)(extreme ? 0 : rng.Next(256)); }
            int w = new[] { 4, 8, 16, 32, 64, 128, 12, 24, 48 }[rng.Next(9)], h = new[] { 4, 8, 16, 32, 64, 128 }[rng.Next(6)];
            long ours = AomEncodeMb.Sse(a, 3, 160, b, 5, 150, w, h);
            long theirs;
            fixed (byte* ap = a) fixed (byte* bp = b) theirs = Native.twin_aom_sse(ap + 3, 160, bp + 5, 150, w, h);
            if (ours != theirs) mismatches++;
        }
        return mismatches;
    }

    [Test]
    public async Task FilterFb_MatchesLibaomDispatched() => await Assert.That(FilterFb_MatchesLibaomDispatchedImpl()).IsEqualTo(0);

    private static unsafe int FilterFb_MatchesLibaomDispatchedImpl()
    {
        var rng = new Random(21);
        ushort* nat = Aligned<ushort>(Size);
        byte* natDst = Aligned<byte>(128 * 128);
        int* tdir = Aligned<int>(256), tvar = Aligned<int>(256);
        byte* tl = Aligned<byte>(2 * 1024);
        int mismatches = 0;
        (int xd, int yd)[] decs = { (0, 0), (1, 1), (1, 0), (0, 1) };
        try
        {
            for (int iter = 0; iter < 4000; iter++)
            {
                int pli = rng.Next(3);
                var (xdec, ydec) = pli == 0 ? (0, 0) : decs[rng.Next(4)];
                var b = RandomInbuf(rng, 0, rng.Next(2) == 0);
                // a random subset of the 8x8 (or subsampled) units of a 64x64 filter block, in raster order
                var dl = new List<(byte, byte)>();
                int density = rng.Next(1, 11);
                for (int by = 0; by < 8; by++)
                    for (int bx = 0; bx < 8; bx++)
                        if (rng.Next(10) < density) dl.Add(((byte)by, (byte)bx));
                if (dl.Count == 0) dl.Add((0, 0));
                var dlist = dl.ToArray();
                int level = rng.Next(16), sec = new[] { 0, 1, 2, 4 }[rng.Next(4)];
                bool useDirinit = rng.Next(2) == 0;
                if (useDirinit && level == 0 && sec == 0) level = 1;
                int damping = rng.Next(3, 7);
                var dir = new int[256];
                var var = new int[256];
                // chroma reuses the luma directions / variances of an earlier luma call
                if (pli != 0) for (int i = 0; i < 256; i++) { dir[i] = rng.Next(8); var[i] = rng.Next(4) == 0 ? 0 : rng.Next(1 << rng.Next(1, 16)); }
                for (int i = 0; i < 256; i++) { tdir[i] = dir[i]; tvar[i] = var[i]; }
                var ours = new byte[128 * 128];
                bool dirinit = false;
                AomCdef.FilterFb(ours, 0, 128, b, InOff, xdec, ydec, dir, ref dirinit, useDirinit, var, pli, dlist, dlist.Length, level, sec, damping, 0);
                if (!Available) continue;
                new Span<ushort>(b).CopyTo(new Span<ushort>(nat, Size));
                new Span<byte>(natDst, 128 * 128).Clear();
                for (int i = 0; i < dlist.Length; i++) { tl[2 * i] = dlist[i].Item1; tl[2 * i + 1] = dlist[i].Item2; }
                int tdirinit = 0;
                Native.twin_cdef_filter_fb(natDst, 128, nat + InOff, xdec, ydec, tdir, useDirinit ? 1 : 0, &tdirinit, tvar, pli, tl,
                    dlist.Length, level, sec, damping, 0);
                bool bad = !new Span<byte>(natDst, 128 * 128).SequenceEqual(ours) || (useDirinit && pli == 0 && (tdirinit != 0) != dirinit);
                for (int i = 0; i < 256; i++) if (tdir[i] != dir[i] || tvar[i] != var[i]) bad = true;
                if (bad) mismatches++;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(nat); NativeMemory.AlignedFree(natDst); NativeMemory.AlignedFree(tdir);
            NativeMemory.AlignedFree(tvar); NativeMemory.AlignedFree(tl);
        }
        return mismatches;
    }
}
