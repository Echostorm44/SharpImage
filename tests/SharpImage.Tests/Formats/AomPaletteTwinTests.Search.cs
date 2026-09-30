using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// The palette search entry points (av1_rd_pick_palette_intra_sby / _sbuv) against a copy of libaom's palette.c in
// aomtwin_palsearch.dll, both with the same mock tx search (twin_palsearch.c's mock_pick_uniform_tx_size_type_yrd /
// mock_txfm_uvrd, reproduced here): everything else (the searches' control flow and speed-feature gating, k-means, the
// mode info costs through intra_mode_info_cost_y / uv, colour map costs, palette cache, winner mode stats) is real on
// both sides. Every mock call is logged (palette size, a hash of the colour map + palette, SSE, the ref_best_rd it was
// given, the rate) and the logs must match call for call; then all outputs and every buffer the search touches.
public sealed partial class AomPaletteTwinTests
{
    private static List<int> s_mockLog = new();

    private static uint Fnv(uint h, uint v) => (h ^ v) * 16777619u;

    private static void MockLog(int kind, int n, uint h, long sse, long refBestRd, int rate)
    {
        s_mockLog.AddRange(new[] { kind, n, (int)h, (int)sse, (int)refBestRd, (int)(refBestRd >> 32), rate, 0x5eed });
    }

    private static void MockYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rd, int bs, long refBestRd)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        rd.Init();
        AomRdoptUtils.GetBlockDimensions(bs, 0, xd, out int bw, out int bh, out int rows, out int cols);
        var pmi = mbmi.Palette;
        int n = pmi.PaletteSize0;
        byte[] map = xd.Plane[0].ColorIndexMap;
        var src = x.Plane[0].Src;
        long sse = 0;
        uint h = 2166136261u;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int d = src.Buf[src.Offset + r * src.Stride + c] - pmi.PaletteColors[map[r * bw + c]];
                sse += d * d;
            }
        for (int i = 0; i < bw * bh; i++) h = Fnv(h, map[i]);
        for (int i = 0; i < n; i++) h = Fnv(h, pmi.PaletteColors[i]);
        h = Fnv(h, unchecked((uint)sse));
        mbmi.TxSize = (int)(h % 5);
        int nb = MiSizeWide[bs] * MiSizeHigh[bs];
        for (int i = 0; i < nb; i++) xd.TxTypeMap[xd.TxTypeMapOffset + i] = (byte)((h >> (i & 15)) & 15);
        rd.Rate = (int)(h % 3000) + 40 * n;
        rd.Dist = sse * 16;
        rd.Sse = sse * 16 + 7;
        rd.SkipTxfm = (byte)(h % 5 == 0 ? 1 : 0);
        if (h % 13 == 4 || (AomRd.RdCost(x.Rdmult, rd.Rate, rd.Dist) > refBestRd && (h & 2) != 0)) rd.Invalidate();
        MockLog(0, n, h, sse, refBestRd, rd.Rate);
    }

    private static bool MockUvrd(AomComp cpi, AomMacroblock x, ref AomRdStats rd, int bsize, long refBestRd)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        rd.Init();
        AomRdoptUtils.GetBlockDimensions(bsize, 1, xd, out int bw, out int bh, out int rows, out int cols);
        var pmi = mbmi.Palette;
        int n = pmi.PaletteSize1;
        byte[] map = xd.Plane[1].ColorIndexMap;
        long sse = 0;
        uint h = 2166136261u;
        for (int p = 1; p < 3; p++)
        {
            var src = x.Plane[p].Src;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int d = src.Buf[src.Offset + r * src.Stride + c] - pmi.PaletteColors[p * PMax + map[r * bw + c]];
                    sse += d * d;
                }
        }
        for (int i = 0; i < bw * bh; i++) h = Fnv(h, map[i]);
        for (int i = 0; i < n; i++) h = Fnv(Fnv(h, pmi.PaletteColors[8 + i]), pmi.PaletteColors[16 + i]);
        h = Fnv(h, unchecked((uint)sse));
        rd.Rate = (int)(h % 2000) + 30 * n;
        rd.Dist = sse * 16;
        rd.Sse = sse * 16 + 3;
        rd.SkipTxfm = (byte)(h % 7 == 0 ? 1 : 0);
        if (h % 11 == 4 || (AomRd.RdCost(x.Rdmult, rd.Rate, rd.Dist) > refBestRd && (h & 4) != 0)) rd.Invalidate();
        MockLog(1, n, h, sse, refBestRd, rd.Rate);
        return rd.Rate != int.MaxValue;
    }

    // MB_MODE_INFO <-> int[32] (twin_palsearch.c mi_in / mi_out)
    private static void MiIn(AomMbModeInfo m, int[] s, int o)
    {
        m.CopyFrom(AomMbModeInfo.Zero);
        m.Bsize = s[o]; m.Mode = s[o + 1]; m.UvMode = s[o + 2]; m.TxSize = s[o + 3]; m.UseFilterIntra = (byte)s[o + 4];
        m.Palette.PaletteSize0 = (byte)s[o + 5]; m.Palette.PaletteSize1 = (byte)s[o + 6];
        for (int i = 0; i < 24; i++) m.Palette.PaletteColors[i] = (ushort)s[o + 7 + i];
        m.SegmentId = (byte)s[o + 31];
    }

    private static void MiOut(AomMbModeInfo m, int[] s, int o)
    {
        s[o] = m.Bsize; s[o + 1] = m.Mode; s[o + 2] = m.UvMode; s[o + 3] = m.TxSize; s[o + 4] = m.UseFilterIntra;
        s[o + 5] = m.Palette.PaletteSize0; s[o + 6] = m.Palette.PaletteSize1;
        for (int i = 0; i < 24; i++) s[o + 7 + i] = m.Palette.PaletteColors[i];
        s[o + 31] = m.SegmentId;
    }

    private static void RandomMi(Random rng, int[] s, int o, int bsize, bool paletteY)
    {
        s[o] = bsize; s[o + 1] = rng.Next(13); s[o + 2] = rng.Next(14); s[o + 3] = rng.Next(5); s[o + 4] = rng.Next(2);
        int s0 = paletteY ? rng.Next(2, 9) : 0, s1 = paletteY && rng.Next(2) == 0 ? rng.Next(2, 9) : 0;
        s[o + 5] = s0; s[o + 6] = s1;
        for (int p = 0; p < 3; p++)
        {
            ushort[] c = p < 2 ? SortedUnique(rng, 8, rng.Next(3) == 0 ? 40 : 256) : Enumerable.Range(0, 8).Select(_ => (ushort)rng.Next(256)).ToArray();
            for (int i = 0; i < 8; i++) s[o + 7 + p * 8 + i] = c[i];
        }
        s[o + 31] = rng.Next(8);
    }

    private sealed class SearchCase
    {
        public int[] Cfg = new int[23];
        public int[] Costs = null!;
        public int[] State = new int[128];
        public byte[] Cmap0 = new byte[128 * 128], Cmap1 = new byte[128 * 128], BestMap = new byte[4096], WMaps = new byte[3 * 4096], TxOut = new byte[1024];
        public short[] Kbuf = new short[8192];
        public int[] WStat = new int[3 * 34];
        public int WCount;
        public long[] Io64 = new long[2];
        public int[] Io32 = new int[4];
        public byte[][] Planes = new byte[3][];
        public int[] Strides = new int[3], Offsets = new int[3];

        public SearchCase Clone()
        {
            var c = (SearchCase)MemberwiseClone();
            c.Cfg = (int[])Cfg.Clone(); c.State = (int[])State.Clone(); c.Cmap0 = (byte[])Cmap0.Clone(); c.Cmap1 = (byte[])Cmap1.Clone();
            c.BestMap = (byte[])BestMap.Clone(); c.WMaps = (byte[])WMaps.Clone(); c.TxOut = (byte[])TxOut.Clone(); c.Kbuf = (short[])Kbuf.Clone();
            c.WStat = (int[])WStat.Clone(); c.Io64 = (long[])Io64.Clone(); c.Io32 = (int[])Io32.Clone();
            return c;
        }
    }

    private static SearchCase RandomSearchCase(Random rng, bool luma, int bsize)
    {
        var sc = new SearchCase();
        var (ssx, ssy) = Subsamplings[rng.Next(3)];
        var (right, bottom) = RandomCrop(rng, bsize);
        int[] cfg = sc.Cfg;
        cfg[0] = bsize; cfg[1] = right; cfg[2] = bottom; cfg[3] = -8 * 4 * rng.Next(0, 48); cfg[4] = ssx; cfg[5] = ssy;
        cfg[6] = rng.Next(3); cfg[7] = rng.Next(3); cfg[8] = rng.Next(2); cfg[9] = rng.Next(4) == 0 ? 1 : 0;
        cfg[10] = rng.Next(3) == 0 ? 1 : 0; cfg[11] = rng.Next(2); cfg[12] = rng.Next(2); cfg[13] = rng.Next(0, 120); cfg[14] = rng.Next(4);
        cfg[15] = rng.Next(3); cfg[16] = rng.Next(2); cfg[17] = rng.Next(20, 20000); cfg[18] = rng.Next(4) == 0 ? rng.Next(2, 70) : 64;
        cfg[19] = rng.Next(8) == 0 ? 1 : 0; cfg[20] = rng.Next(2); cfg[21] = rng.Next(2) == 0 ? rng.Next(0, 40) : rng.Next(0, 20000); cfg[22] = rng.Next(0, 3000);
        sc.Costs = RandomCosts(rng, 49 + 49 + 280 + 280 + 42 + 4 + 2 + 44 + 5);

        RandomMi(rng, sc.State, 0, bsize, !luma && rng.Next(2) == 0);
        sc.State[5] = luma ? 0 : sc.State[5];
        if (luma) sc.State[6] = 0;
        if (rng.Next(4) != 0) RandomMi(rng, sc.State, 32, rng.Next(BLOCK_8X8, 13), rng.Next(2) == 0); else sc.State[32] = -1;
        if (rng.Next(4) != 0) RandomMi(rng, sc.State, 64, rng.Next(BLOCK_8X8, 13), rng.Next(2) == 0); else sc.State[64] = -1;
        Array.Copy(sc.State, 0, sc.State, 96, 32);   // best_mbmi starts as the current block's
        if (rng.Next(3) == 0) RandomMi(rng, sc.State, 96, bsize, rng.Next(2) == 0);

        rng.NextBytes(sc.Cmap0); rng.NextBytes(sc.Cmap1); rng.NextBytes(sc.BestMap); rng.NextBytes(sc.WMaps); rng.NextBytes(sc.TxOut);
        for (int i = 0; i < sc.Kbuf.Length; i++) sc.Kbuf[i] = rng.Next(10) == 0 ? (short)rng.Next(-500, 800) : (short)rng.Next(256);
        int maxW = AomRdoptUtils.WinnerModeCountAllowed[cfg[15]];
        sc.WCount = rng.Next(0, maxW + 1);
        long wrd = rng.Next(1000, 100000);
        for (int i = 0; i < 3; i++)
        {
            wrd += rng.Next(0, 3) == 0 ? 0 : rng.Next(1, 1 << 30);
            sc.WStat[i * 34] = (int)wrd; sc.WStat[i * 34 + 1] = (int)(wrd >> 32);
            RandomMi(rng, sc.WStat, i * 34 + 2, bsize, rng.Next(2) == 0);
        }
        sc.Io64[0] = rng.Next(3) switch { 0 => long.MaxValue, 1 => rng.NextInt64(1, 1L << 40), _ => rng.NextInt64(1, 200000) };
        sc.Io64[1] = rng.NextInt64(0, 1L << 30);
        sc.Io32[0] = rng.Next(); sc.Io32[1] = rng.Next(); sc.Io32[2] = rng.Next(2); sc.Io32[3] = rng.Next(2);

        // planes: 80 x 80 per plane, the block at (8, 8); luma kinds biased towards few colours
        int kindY = rng.Next(4) == 0 ? rng.Next(7) : new[] { 0, 3, 4, 5, 0, 5 }[rng.Next(6)];
        for (int p = 0; p < 3; p++)
        {
            sc.Planes[p] = new byte[80 * 80];
            sc.Strides[p] = 80;
            sc.Offsets[p] = 8 * 80 + 8;
            FillBlock(rng, sc.Planes[p], 0, 80, 80, 80, p == 0 ? kindY : rng.Next(3) == 0 ? rng.Next(7) : new[] { 0, 3, 4, 5 }[rng.Next(4)]);
        }
        // exactly N colours around the colour-count thresholds (64, 64 + 20 for the nonrd increase, the random base)
        if (rng.Next(4) == 0)
        {
            int p = luma ? 0 : 1 + rng.Next(2);
            int sx = p == 0 ? 0 : ssx, sy = p == 0 ? 0 : ssy;
            AomRdoptUtils.GetBlockDimensions(bsize, p, new AomMacroblockD { MbToRightEdge = right, MbToBottomEdge = bottom,
                Plane = { [1] = { SubsamplingX = ssx, SubsamplingY = ssy }, [2] = { SubsamplingX = ssx, SubsamplingY = ssy } } },
                out _, out _, out int rows, out int cols);
            int nExact = Math.Min(rows * cols, Math.Max(2, cfg[18] + new[] { -1, 0, 1, 2, 19, 20, 21, 22 }[rng.Next(8)]));
            FillExactColors(rng, sc.Planes[p], sc.Offsets[p], 80, cols, rows, nExact);
        }
        // exact header_rd == best_rd ties (the chroma early termination breaks on >=): RDCOST(0 or 1, small rate, 0) = 0
        if (rng.Next(8) == 0)
        {
            cfg[17] = rng.Next(0, 2);
            sc.Io64[0] = rng.Next(0, 3);
        }
        return sc;
    }

    private static void FillExactColors(Random rng, byte[] buf, int off, int stride, int w, int h, int n)
    {
        var vals = SortedUnique(rng, n).OrderBy(_ => rng.Next()).ToArray();
        var pos = Enumerable.Range(0, w * h).OrderBy(_ => rng.Next()).ToArray();
        for (int i = 0; i < pos.Length; i++) buf[off + pos[i] / w * stride + pos[i] % w] = (byte)vals[i < n ? i : rng.Next(n)];
    }

    private static unsafe int NativeSearch(bool luma, SearchCase sc, int[] log)
    {
        fixed (int* cfg = sc.Cfg) fixed (int* costs = sc.Costs) fixed (int* state = sc.State) fixed (byte* c0 = sc.Cmap0) fixed (byte* c1 = sc.Cmap1)
        fixed (byte* bm = sc.BestMap) fixed (short* kb = sc.Kbuf) fixed (byte* wm = sc.WMaps) fixed (int* ws = sc.WStat) fixed (int* wc = &sc.WCount)
        fixed (long* io64 = sc.Io64) fixed (int* io32 = sc.Io32) fixed (byte* tx = sc.TxOut) fixed (byte* p0 = sc.Planes[0]) fixed (byte* p1 = sc.Planes[1])
        fixed (byte* p2 = sc.Planes[2]) fixed (int* st = sc.Strides) fixed (int* of = sc.Offsets) fixed (int* lg = log)
        {
            byte** planes = stackalloc byte*[3];
            planes[0] = p0; planes[1] = p1; planes[2] = p2;
            return ((delegate* unmanaged<int, int*, int*, int*, byte*, byte*, byte*, short*, byte*, int*, int*, long*, int*, byte*, byte**, int*, int*, int*, int, int>)
                S("twin_search"))(luma ? 1 : 0, cfg, costs, state, c0, c1, bm, kb, wm, ws, wc, io64, io32, tx, planes, st, of, lg, log.Length);
        }
    }

    private static void OurSearch(bool luma, SearchCase sc)
    {
        int[] cfg = sc.Cfg;
        int bsize = cfg[0];
        var cpi = new AomComp { AllowScreenContentTools = true, AllowIntrabc = cfg[16] != 0, EnableFilterIntra = cfg[20] != 0, FrameIsIntraOnly = true };
        cpi.Sf.intra_sf.prune_palette_search_level = cfg[6];
        cpi.Sf.intra_sf.prune_luma_palette_size_search_level = cfg[7];
        cpi.Sf.intra_sf.early_term_chroma_palette_size_search = cfg[8];
        cpi.Sf.rt_sf.discount_color_cost = cfg[9];
        cpi.Sf.rt_sf.use_nonrd_pick_mode = cfg[10];
        cpi.Sf.rt_sf.increase_color_thresh_palette = cfg[11] != 0;
        cpi.RcHighSourceSad = cfg[12];
        cpi.Sf.winner_mode_sf.multi_winner_mode_type = cfg[15];

        var x = new AomMacroblock();
        var xd = x.E;
        var cur = new AomMbModeInfo();
        MiIn(cur, sc.State, 0);
        var best = new AomMbModeInfo();
        MiIn(best, sc.State, 96);
        xd.Mi0 = cur;
        if (sc.State[32] >= 0) { xd.AboveMbmi = new AomMbModeInfo(); MiIn(xd.AboveMbmi, sc.State, 32); xd.UpAvailable = true; }
        if (sc.State[64] >= 0) { xd.LeftMbmi = new AomMbModeInfo(); MiIn(xd.LeftMbmi, sc.State, 64); xd.LeftAvailable = true; }
        xd.MbToRightEdge = cfg[1]; xd.MbToBottomEdge = cfg[2]; xd.MbToTopEdge = cfg[3]; xd.MbToLeftEdge = 0;
        for (int p = 1; p < 3; p++) { xd.Plane[p].SubsamplingX = cfg[4]; xd.Plane[p].SubsamplingY = cfg[5]; }
        xd.Plane[0].ColorIndexMap = sc.Cmap0;
        xd.Plane[1].ColorIndexMap = sc.Cmap1;
        xd.IsChromaRef = true;
        xd.Lossless[cur.SegmentId] = cfg[19];
        xd.TxTypeMap = new byte[1024];
        xd.TxTypeMapOffset = 0;
        xd.TxTypeMapStride = MiSizeWide[bsize];
        for (int p = 0; p < 3; p++) x.Plane[p].Src = new AomBuf2d { Buf = sc.Planes[p], Offset = sc.Offsets[p], Stride = sc.Strides[p] };
        sc.Kbuf.CopyTo(x.KmeansDataBuf, 0);
        x.Rdmult = cfg[17];
        x.ColorPaletteThresh = cfg[18];
        x.SourceVariance = (uint)cfg[13];
        x.ColorSensitivity[0] = (byte)(cfg[14] & 1);
        x.ColorSensitivity[1] = (byte)((cfg[14] >> 1) & 1);
        x.MinDistInterUv = cfg[21] * 1000L;
        x.TxfmSearchParams.TxModeSearchType = TX_MODE_LARGEST;
        x.WinnerModeStats = new[] { new AomWinnerModeStats(), new AomWinnerModeStats(), new AomWinnerModeStats() };
        x.WinnerModeCount = sc.WCount;
        for (int i = 0; i < 3; i++)
        {
            var w = x.WinnerModeStats[i];
            w.Rd = (long)(((ulong)(uint)sc.WStat[i * 34 + 1] << 32) | (uint)sc.WStat[i * 34]);
            MiIn(w.Mbmi, sc.WStat, i * 34 + 2);
            Array.Copy(sc.WMaps, i * 4096, w.ColorIndexMap, 0, 4096);
        }
        var mc = x.ModeCosts;
        int o = 0;
        foreach (int[] a in new[] { mc.PaletteYSizeCost, mc.PaletteUvSizeCost, mc.PaletteYColorCost, mc.PaletteUvColorCost, mc.PaletteYModeCost,
                     mc.PaletteUvModeCost, mc.IntrabcCost, mc.FilterIntraCost, mc.FilterIntraModeCost })
        {
            Array.Copy(sc.Costs, o, a, 0, a.Length);
            o += a.Length;
        }
        var ctx = new AomPickModeContext(bsize, true);

        int rate = sc.Io32[0], rateTokenonly = sc.Io32[1];
        byte skippable = (byte)sc.Io32[2];
        bool beat = sc.Io32[3] != 0;
        long bestRd = sc.Io64[0], distortion = sc.Io64[1];
        AomPalette.TestPickUniformTxSizeTypeYrd = MockYrd;
        AomPalette.TestTxfmUvrd = MockUvrd;
        try
        {
            if (luma)
                AomPalette.RdPickPaletteIntraSby(cpi, x, bsize, cfg[22], best, sc.BestMap, ref bestRd, ref rate, ref rateTokenonly, ref distortion,
                    ref skippable, ref beat, ctx, sc.TxOut);
            else
                AomPalette.RdPickPaletteIntraSbuv(cpi, x, cfg[22], sc.BestMap, best, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable);
        }
        finally
        {
            AomPalette.TestPickUniformTxSizeTypeYrd = null;
            AomPalette.TestTxfmUvrd = null;
        }
        sc.Io32[0] = rate; sc.Io32[1] = rateTokenonly; sc.Io32[2] = skippable; sc.Io32[3] = beat ? 1 : 0;
        sc.Io64[0] = bestRd; sc.Io64[1] = distortion;
        MiOut(cur, sc.State, 0);
        MiOut(best, sc.State, 96);
        x.KmeansDataBuf.CopyTo(sc.Kbuf, 0);
        sc.WCount = x.WinnerModeCount;
        for (int i = 0; i < 3; i++)
        {
            var w = x.WinnerModeStats[i];
            sc.WStat[i * 34] = (int)w.Rd; sc.WStat[i * 34 + 1] = (int)(w.Rd >> 32);
            MiOut(w.Mbmi, sc.WStat, i * 34 + 2);
            Array.Copy(w.ColorIndexMap, 0, sc.WMaps, i * 4096, 4096);
        }
    }

    private static string? CompareSearch(SearchCase a, SearchCase b, int[] log, int nlog)
    {
        var ours = s_mockLog;
        if (ours.Count != 8 * nlog || !ours.SequenceEqual(log.Take(8 * nlog)))
        {
            int i = 0;
            while (i < Math.Min(ours.Count, 8 * nlog) && ours[i] == log[i]) i++;
            return $"mock call log: ours {ours.Count / 8} calls, libaom {nlog}; first difference in call {i / 8} field {i % 8}";
        }
        if (!a.Io64.AsSpan().SequenceEqual(b.Io64)) return $"best_rd / distortion ours {a.Io64[0]}/{a.Io64[1]} libaom {b.Io64[0]}/{b.Io64[1]}";
        if (!a.Io32.AsSpan().SequenceEqual(b.Io32)) return $"rate / tokenonly / skip / beat: {Diff<int>(a.Io32, b.Io32)}";
        if (!a.State.AsSpan(0, 32).SequenceEqual(b.State.AsSpan(0, 32))) return $"mbmi {Diff<int>(a.State.AsSpan(0, 32), b.State.AsSpan(0, 32))}";
        if (!a.State.AsSpan(96, 32).SequenceEqual(b.State.AsSpan(96, 32))) return $"best_mbmi {Diff<int>(a.State.AsSpan(96, 32), b.State.AsSpan(96, 32))}";
        if (!a.Cmap0.AsSpan().SequenceEqual(b.Cmap0)) return $"color map 0 {Diff<byte>(a.Cmap0, b.Cmap0)}";
        if (!a.Cmap1.AsSpan().SequenceEqual(b.Cmap1)) return $"color map 1 {Diff<byte>(a.Cmap1, b.Cmap1)}";
        if (!a.BestMap.AsSpan().SequenceEqual(b.BestMap)) return $"best palette color map {Diff<byte>(a.BestMap, b.BestMap)}";
        if (!a.Kbuf.AsSpan().SequenceEqual(b.Kbuf)) return $"kmeans_data_buf {Diff<short>(a.Kbuf, b.Kbuf)}";
        if (!a.TxOut.AsSpan().SequenceEqual(b.TxOut)) return $"tx_type_map {Diff<byte>(a.TxOut, b.TxOut)}";
        if (a.WCount != b.WCount) return $"winner_mode_count ours {a.WCount} libaom {b.WCount}";
        if (!a.WStat.AsSpan().SequenceEqual(b.WStat)) return $"winner mode stats {Diff<int>(a.WStat, b.WStat)}";
        if (!a.WMaps.AsSpan().SequenceEqual(b.WMaps)) return $"winner mode color maps {Diff<byte>(a.WMaps, b.WMaps)}";
        return null;
    }

    private static string? RunSearchTwins(bool luma, int seed, int perBsize, out int cases, out int calls, out int palettes)
    {
        var rng = new Random(seed);
        var f = new List<string>();
        int[] log = new int[8 * 1024];
        cases = calls = palettes = 0;
        foreach (int bsize in PaletteBsizes)
            for (int it = 0; it < perBsize; it++)
            {
                var sc = RandomSearchCase(rng, luma, bsize);
                long rd0 = sc.Io64[0];
                var theirs = sc.Clone();
                int nlog = NativeSearch(luma, theirs, log);
                s_mockLog = new List<int>();
                OurSearch(luma, sc);
                cases++;
                calls += nlog;
                if (sc.Io64[0] < rd0) palettes++;
                string? d = CompareSearch(sc, theirs, log, nlog);
                if (d != null) f.Add($"bsize {bsize} cfg [{string.Join(",", sc.Cfg)}]: {d}");
            }
        return First(f, cases);
    }

    [Test]
    public async Task SearchY_AgainstLibaomPaletteC_MockTxSearch()
    {
        if (!Available) return;
        string? r = RunSearchTwins(true, 21, 400, out int cases, out int calls, out int palettes);
        Console.WriteLine($"sby: {cases} searches, {calls} mock tx searches, {palettes} improved best_rd with a luma palette");
        await Assert.That(r).IsNull();
        await Assert.That(calls).IsGreaterThan(cases);
        await Assert.That(palettes).IsGreaterThan(cases / 10);
    }

    [Test]
    public async Task SearchUv_AgainstLibaomPaletteC_MockTxSearch()
    {
        if (!Available) return;
        string? r = RunSearchTwins(false, 22, 400, out int cases, out int calls, out int palettes);
        Console.WriteLine($"sbuv: {cases} searches, {calls} mock tx searches, {palettes} improved best_rd with a chroma palette");
        await Assert.That(r).IsNull();
        await Assert.That(calls).IsGreaterThan(cases);
        await Assert.That(palettes).IsGreaterThan(cases / 10);
    }
}
