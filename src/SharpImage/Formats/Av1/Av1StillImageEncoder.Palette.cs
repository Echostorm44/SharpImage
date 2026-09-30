using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

// Palette mode for screen content (libaom av1_rd_pick_palette_intra_sby / _sbuv): screen-content detection per
// frame, then per eligible block a luma palette (the colour map is the predictor; the residual is transform coded as
// for a DC block) and a chroma palette (one map for (u, v) pairs), each chosen by full RD against the regular modes.
internal static partial class Av1StillImageEncoder
{
    /// <summary>libaom estimate_screen_content_antialiasing_aware (the detection avifenc's all-intra mode uses): 16x16
    /// luma blocks are classed palettizable (2..4 values, or up to 40 values that dilate by their dominant value to at most
    /// 6), photo-like (over 40 values) or solid; screen content when (palettizable - photo/16) blocks cover over a tenth of
    /// the frame. Complex blocks count only with per-pixel variance above 5. fast = every other block (checkerboard),
    /// libaom's speed >= 3.</summary>
    internal static bool DetectScreenContent<TP>(ReadOnlySpan<TP> luma, int stride, int width, int height, int bitDepth, bool fast) where TP : unmanaged
    {
        const int blk = 16, simpleThresh = 4, complexInitial = 40, complexFinal = 6, varThresh = 5;
        long countPalette = 0, countPhoto = 0;
        int mult = fast ? 2 : 1, shift = bitDepth - 8;
        Span<byte> b8 = stackalloc byte[blk * blk];
        Span<byte> dil = stackalloc byte[blk * blk];
        for (int r = 0; r + blk <= height; r += blk)
        {
            int c0 = fast && (r / blk) % 2 != 0 ? blk : 0;
            for (int c = c0; c + blk <= width; c += blk * mult)
            {
                for (int y = 0; y < blk; y++)
                    for (int x = 0; x < blk; x++) b8[y * blk + x] = (byte)(Px.I(luma[(r + y) * stride + c + x]) >> shift);
                bool under = CountColorsWithThreshold(b8, complexInitial, out int nColors);
                if (nColors > 1 && under)
                {
                    if (nColors <= simpleThresh) countPalette++;
                    else
                    {
                        DilateBlock(b8, dil, blk);
                        if (CountColorsWithThreshold(dil, complexFinal, out _) &&
                            PerPixelVariance16(luma, stride, c, r, bitDepth) > varThresh) countPalette++;
                    }
                }
                else if (nColors > complexInitial) countPhoto++;
            }
        }
        countPalette *= mult; countPhoto *= mult;
        return (countPalette - countPhoto / 16) * blk * blk * 10 > (long)width * height;
    }

    // av1_count_colors_with_threshold on an 8-bit 16x16 block.
    private static bool CountColorsWithThreshold(ReadOnlySpan<byte> blk, int threshold, out int n)
    {
        Span<bool> has = stackalloc bool[256];
        n = 0;
        foreach (byte v in blk)
            if (!has[v]) { has[v] = true; if (++n > threshold) return false; }
        return true;
    }

    // av1_dilate_block: the dominant value (first to reach the highest count in raster order) grows into all 8
    // neighbours of each of its source pixels.
    private static void DilateBlock(ReadOnlySpan<byte> src, Span<byte> dst, int n)
    {
        Span<int> cnt = stackalloc int[256];
        int dom = 0, domCount = 0;
        foreach (byte v in src) if (++cnt[v] > domCount) { dom = v; domCount = cnt[v]; }
        src.CopyTo(dst);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                if (src[r * n + c] != dom) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int rr = r + dy, cc = c + dx;
                        if ((uint)rr < (uint)n && (uint)cc < (uint)n) dst[rr * n + cc] = (byte)dom;
                    }
            }
    }

    // av1_get_perpixel_variance for a 16x16 luma block against the flat 128 << (bd - 8) reference, with libaom's
    // high-bit-depth sse / sum rounding, then ROUND_POWER_OF_TWO(var, 8).
    private static uint PerPixelVariance16<TP>(ReadOnlySpan<TP> luma, int stride, int x0, int y0, int bitDepth) where TP : unmanaged
    {
        long sum = 0, sse = 0;
        int off = 128 << (bitDepth - 8);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++) { int d = Px.I(luma[(y0 + y) * stride + x0 + x]) - off; sum += d; sse += (long)d * d; }
        if (bitDepth == 10) { sse = (sse + 8) >> 4; sum = (sum + 2) >> 2; }
        else if (bitDepth == 12) { sse = (sse + 128) >> 8; sum = (sum + 8) >> 4; }
        long var = sse - sum * sum / 256;
        if (var < 0) var = 0;
        return (uint)((var + 128) >> 8);
    }

    private sealed class LumaPal<TP> where TP : unmanaged
    {
        public ushort[] Colors = new ushort[8];
        public int Size;
        public byte[] Map = null!;       // stride = block width
        public TP[] Pred = null!;
        public int[] Coeffs = null!;
        public Av1TxType Inv;
        public int Idx;
        public double J;
    }

    private sealed class UvPal<TP> where TP : unmanaged
    {
        public ushort[] U = new ushort[8], V = new ushort[8];
        public int Size;
        public byte[] Map = null!;       // stride = chroma block width
        public TP[] PredU = null!, PredV = null!;
    }

    [ThreadStatic] private static int[]? t_palHist;

    // Distinct values of a w x h region (ascending) with their counts; returns the count, or -1 when above max.
    private static int CountColors<TP>(ReadOnlySpan<TP> plane, int stride, int x0, int y0, int w, int h, int max,
        Span<ushort> vals, Span<int> cnts) where TP : unmanaged
    {
        var hist = t_palHist ??= new int[1 << 12];
        int n = 0;
        for (int y = 0; y < h; y++)
        {
            int o = (y0 + y) * stride + x0;
            for (int x = 0; x < w; x++) if (hist[Px.I(plane[o + x])]++ == 0) n++;
        }
        int k = 0;
        bool over = n > max;
        for (int y = 0; y < h; y++)
        {
            int o = (y0 + y) * stride + x0;
            for (int x = 0; x < w; x++)
            {
                int v = Px.I(plane[o + x]);
                if (hist[v] == 0) continue;
                if (!over) { vals[k] = (ushort)v; cnts[k] = hist[v]; k++; }
                hist[v] = 0;
            }
        }
        if (over) return -1;
        // ascending by value
        for (int i = 1; i < k; i++)
        {
            ushort v = vals[i]; int cn = cnts[i], j = i - 1;
            while (j >= 0 && vals[j] > v) { vals[j + 1] = vals[j]; cnts[j + 1] = cnts[j]; j--; }
            vals[j + 1] = v; cnts[j + 1] = cn;
        }
        return k;
    }

    // 1-D k-means over a value histogram, seeded with the k most frequent values; returns distinct rounded centroids
    // ascending (count <= k).
    private static int KMeans1D(ReadOnlySpan<ushort> vals, ReadOnlySpan<int> cnts, int nv, int k, int maxVal, Span<ushort> outColors)
    {
        Span<double> cent = stackalloc double[8];
        Span<int> order = stackalloc int[64];
        for (int i = 0; i < nv; i++) order[i] = i;
        for (int i = 1; i < nv; i++)
        {
            int t = order[i], j = i - 1;
            while (j >= 0 && cnts[order[j]] < cnts[t]) { order[j + 1] = order[j]; j--; }
            order[j + 1] = t;
        }
        for (int i = 0; i < k; i++) cent[i] = vals[order[i]];
        Span<double> sum = stackalloc double[8];
        Span<long> num = stackalloc long[8];
        for (int it = 0; it < 30; it++)
        {
            sum.Clear(); num.Clear();
            for (int i = 0; i < nv; i++)
            {
                int bi = 0; double bd = double.MaxValue;
                for (int c = 0; c < k; c++) { double d = Math.Abs(vals[i] - cent[c]); if (d < bd) { bd = d; bi = c; } }
                sum[bi] += (double)vals[i] * cnts[i]; num[bi] += cnts[i];
            }
            bool moved = false;
            for (int c = 0; c < k; c++)
                if (num[c] > 0) { double nc = sum[c] / num[c]; if (Math.Abs(nc - cent[c]) > 1e-9) moved = true; cent[c] = nc; }
            if (!moved) break;
        }
        int m = 0;
        Span<ushort> tmp = stackalloc ushort[8];
        for (int c = 0; c < k; c++) tmp[m++] = (ushort)Math.Clamp((int)Math.Round(cent[c]), 0, maxVal);
        tmp[..m].Sort();
        int d2 = 0;
        for (int i = 0; i < m; i++) if (d2 == 0 || outColors[d2 - 1] != tmp[i]) outColors[d2++] = tmp[i];
        return d2;
    }

    // Nearest palette index (lowest on ties) per pixel, the prediction it gives, and the map (stride w).
    private static void PaletteMap<TP>(ReadOnlySpan<TP> plane, int stride, int x0, int y0, int w, int h,
        ushort[] colors, int size, byte[] map, TP[] pred) where TP : unmanaged
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = Px.I(plane[(y0 + y) * stride + x0 + x]), bi = 0, bd = int.MaxValue;
                for (int c = 0; c < size; c++) { int d = Math.Abs(v - colors[c]); if (d < bd) { bd = d; bi = c; } }
                map[y * w + x] = (byte)bi; pred[y * w + x] = Px.T<TP>(colors[bi]);
            }
    }

    // Luma palette cache of the block's neighbours (the decoder's get_palette_cache inputs).
    private static (int LeftSz, int AboveSz) PaletteNeighbours<TP>(ColorPartCtx<TP> c, int bx4, int by4, Span<ushort> lCol, Span<ushort> aCol, bool uv) where TP : unmanaged
    {
        int bxR = bx4 & 31, byR = by4 & 31;
        byte[] lSzA = uv ? c.LPalSzUv : c.LPalSz, aSzA = uv ? c.APalSzUv : c.APalSz;
        ushort[] lColA = uv ? c.LPalColU : c.LPalCol, aColA = uv ? c.APalColU : c.APalCol;
        int l = lSzA[byR]; if (l > 8) l = 0;
        int a = (by4 & 15) != 0 ? aSzA[bxR] : 0; if (a > 8) a = 0;
        for (int i = 0; i < l; i++) lCol[i] = lColA[byR * 8 + i];
        for (int i = 0; i < a; i++) aCol[i] = aColA[bxR * 8 + i];
        return (l, a);
    }

    // Luma tx types searched for a block's transform (as the regular leaf searches).
    private static (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] LumaTxSet(int lumaTx)
    {
        ref readonly var td = ref Av1Tables.TxfmDimensions[lumaTx];
        if (!Sp.UseTxTypeSearch || td.Max > (byte)Av1TxSize.Tx16x16) return DctOnly;
        return UseFullIntraTxSet && td.Min < (byte)Av1TxSize.Tx16x16 ? IntraTxTypesFull : IntraTxTypes;
    }

    /// <summary>The best luma palette for a fully-inside w x h block (8x8..64x64) by RD, or null when the block has
    /// fewer than 2 or more than 64 colours. J = recon SSE + λ·(DC mode + palette flag/size/colours/map + coefficient
    /// bits), directly comparable with the regular leaf's J.</summary>
    /// <summary>... bound: the J the palette must beat (the regular winner's); a candidate whose side information alone
    /// costs that much is not evaluated.</summary>
    private static LumaPal<TP>? SearchLumaPalette<TP>(ColorPartCtx<TP> c, int bs, int lumaTx, int bx4, int by4, int w, int h, Span<ushort> ymCdf,
        double bound = double.MaxValue) where TP : unmanaged
    {
        int bx = bx4 * 4, by = by4 * 4, bxR = bx4 & 31, byR = by4 & 31;
        var vals = new ushort[64]; var cnts = new int[64];   // arrays: captured by Eval below
        int nv = CountColors(c.Luma, c.W, bx, by, w, h, 64, vals, cnts);
        if (nv < 2) return null;
        if (StatsOn) System.Threading.Interlocked.Increment(ref StatPal);
        int bw4 = w >> 2, bh4 = h >> 2;
        int szCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
        int palCtx = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
        var lCol = new ushort[8]; var aCol = new ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: false);
        double lambda = LamK * c.AcDq * c.AcDq;
        int scanLen = Av1Tables.Scans[lumaTx].Length;
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        var txSet = LumaTxSet(lumaTx);
        double baseBits = Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc)
            + Av1CoeffEncode.BoolBits(c.Cdf.GetPalYCdf(szCtx, palCtx)[0], 1);
        int maxVal = (1 << Bd) - 1;
        LumaPal<TP>? best = null;
        var colors = new ushort[8];
        var qf = new double[scanLen];
        var res = new int[w * h];
        double prevJ = double.MaxValue;
        // Evaluates the k-colour palette; returns 1 when it beat the best so far (bound included), 0 when not, -1 when
        // its side information alone already lost (libaom's header-rd gating)
        int Eval(int k)
        {
            double before = Math.Min(bound, best?.J ?? double.MaxValue);
            int size;
            if (nv <= 8 && k == nv) { for (int i = 0; i < nv; i++) colors[i] = vals[i]; size = nv; }
            else size = KMeans1D(vals, cnts, nv, k, maxVal, colors);
            if (size < 2) return 0;
            var cand = new LumaPal<TP> { Size = size, Map = new byte[w * h], Pred = new TP[w * h] };
            Array.Copy(colors, cand.Colors, size);
            PaletteMap(c.Luma, c.W, bx, by, w, h, cand.Colors, size, cand.Map, cand.Pred);
            double palBits = baseBits + Av1CoeffEncode.SymBits(c.Cdf.GetPalSzCdf(0, szCtx), size - 2)
                + Av1CoeffEncode.LumaPaletteColorBits(cand.Colors, size, lCol, lSz, aCol, aSz, Bd)
                + EstimatePaletteIndexBits(c.Cdf.Mode, cand.Map, size, w, h, bw4, bh4);
            double limit = Math.Min(bound, best?.J ?? double.MaxValue);
            if (lambda * palBits >= limit) return -1;   // the side information alone already loses (exact)
            if (StatsOn) System.Threading.Interlocked.Increment(ref StatPalCand);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) res[y * w + x] = Px.I(c.Luma[(by + y) * c.W + bx + x]) - Px.I(cand.Pred[y * w + x]);
            double bestJ = double.MaxValue;
            foreach (var (fwd, inv, idx) in txSet)
            {
                if (inv == Av1TxType.VDct || inv == Av1TxType.HDct) continue;   // 2D types only (RDOQ / estimate)
                int[] cf = Av1FwdTransform.ForwardQuantRect(res, w, h, lumaTx, c.DcDq, c.AcDq, scanLen, fwd, qf);
                double rate = -1;
                // under the libaom luma search its rivals are trellised: so is this candidate (fair comparison)
                if (Sp.LibaomLuma && Sp.AomTrellis && Sp.UseRdoq && HasNonZero(cf))
                    rate = Av1CoeffEncode.TrellisOptimize(c.Cdf.Coef, lumaTx, 0, cf, qf, c.DcDq, c.AcDq, 0, ySign, LumaTrellisLambda(c),
                        Av1CoeffEncode.IntraTxTypeBits(c.Cdf.Mode, lumaTx, (int)Av1IntraPredMode.Dc, idx, UseFullIntraTxSet));
                long sse = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, cand.Pred, c.Luma, c.W, bx, by, inv);
                if (sse + lambda * palBits >= Math.Min(bestJ, limit)) continue;   // distortion alone loses (exact)
                if (rate < 0) rate = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)Av1IntraPredMode.Dc, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet);
                double j = sse + lambda * (rate + palBits);
                if (j < bestJ) { bestJ = j; cand.Coeffs = cf; cand.Inv = inv; cand.Idx = idx; }
            }
            if (cand.Coeffs == null) return 0;
            cand.J = bestJ;
            if (best == null || cand.J < best.J) best = cand;
            return bestJ < before ? 1 : 0;
        }
        int maxN = Math.Min(nv, 8);
        if (Sp.PaletteSearchLevel >= 2)
        {
            // libaom prune_palette_search_level 2 (+ header-rd gating, prune_luma_palette_size_search_level): sizes
            // ascending until one fails to beat the block's best so far, then descending from the largest down to it
            int last = 2;
            for (int k = 2; k <= maxN; k++)
            {
                last = k;
                int r = Eval(k);
                if (r < 0) { last = maxN; break; }
                if (r == 0) break;
            }
            for (int k = maxN; k > last; k--) if (Eval(k) <= 0) break;
        }
        else
            for (int k = maxN; k >= 2; k--)
            {
                int r = Eval(k);
                // libaom prune_palette_search_level: sizes descend; stop once a smaller palette no longer improves
                double j = best?.J ?? double.MaxValue;
                if (Sp.PaletteEarlyStop && r >= 0 && j > prevJ) break;
                prevJ = j;
            }
        if (best != null && Sp.UseRdoq && !(Sp.LibaomLuma && Sp.AomTrellis))
        {
            // RDOQ the winner (as the regular leaf does after its choice)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) res[y * w + x] = Px.I(c.Luma[(by + y) * c.W + bx + x]) - Px.I(best.Pred[y * w + x]);
            Av1FwdTransform.ForwardQuantRect(res, w, h, lumaTx, c.DcDq, c.AcDq, scanLen, FwdTypeForTxType(best.Inv), qf);
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)Av1IntraPredMode.Dc, best.Coeffs, qf,
                c.DcDq, c.AcDq, 0, ySign, best.Idx, RdoqScale * LamK * c.AcDq * c.AcDq);
        }
        return best;
    }

    /// <summary>Chroma palette candidates for a fully-inside cw x ch chroma block: 2-D k-means over (u, v) pairs,
    /// sizes min(colours, 8) down to 2, pairs ordered by U (the U palette must ascend).</summary>
    private static List<UvPal<TP>> UvPaletteCandidates<TP>(ColorPartCtx<TP> c, int cbx, int cby, int cw, int ch) where TP : unmanaged
    {
        var list = new List<UvPal<TP>>(7);
        // the distinct (u, v) pairs in first-occurrence order with their counts (an open-addressed table of 128 slots),
        // giving up past 64
        Span<int> slot = stackalloc int[128];   // 1 + index into pts, 0 = empty
        Span<int> keys = stackalloc int[65];
        Span<(int U, int V, int N)> ptsBuf = stackalloc (int, int, int)[65];
        int np = 0;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int o = (cby + y) * c.Cw + cbx + x, key = (Px.I(c.U[o]) << 16) | Px.I(c.V[o]);
                int h = (int)(((uint)key * 2654435761u) >> 25);
                while (slot[h] != 0 && keys[slot[h] - 1] != key) h = (h + 1) & 127;
                if (slot[h] != 0) { ptsBuf[slot[h] - 1].N++; continue; }
                if (np == 64) return list;
                keys[np] = key; ptsBuf[np] = (key >> 16, key & 0xFFFF, 1); slot[h] = ++np;
            }
        if (np < 2) return list;
        var pts = ptsBuf.Slice(0, np);
        pts.Sort((a, b) => b.N.CompareTo(a.N));
        int maxVal = (1 << Bd) - 1;
        for (int k = Math.Min(np, 8); k >= 2; k--)
        {
            Span<double> cu = stackalloc double[8], cv = stackalloc double[8];
            for (int i = 0; i < k; i++) { cu[i] = pts[i].U; cv[i] = pts[i].V; }
            if (k < np)
            {
                Span<double> su = stackalloc double[8], sv = stackalloc double[8];
                Span<long> sn = stackalloc long[8];
                for (int it = 0; it < 30; it++)
                {
                    su.Clear(); sv.Clear(); sn.Clear();
                    foreach (var p in pts)
                    {
                        int bi = 0; double bd = double.MaxValue;
                        for (int q = 0; q < k; q++) { double du = p.U - cu[q], dv = p.V - cv[q], d = du * du + dv * dv; if (d < bd) { bd = d; bi = q; } }
                        su[bi] += (double)p.U * p.N; sv[bi] += (double)p.V * p.N; sn[bi] += p.N;
                    }
                    bool moved = false;
                    for (int q = 0; q < k; q++)
                        if (sn[q] > 0)
                        {
                            double nu = su[q] / sn[q], nvv = sv[q] / sn[q];
                            if (Math.Abs(nu - cu[q]) + Math.Abs(nvv - cv[q]) > 1e-9) moved = true;
                            cu[q] = nu; cv[q] = nvv;
                        }
                    if (!moved) break;
                }
            }
            // the distinct rounded centroids, ordered by (U, V) (distinct keys: any sort gives the same order)
            Span<int> cols = stackalloc int[8];
            int nc = 0;
            for (int q = 0; q < k; q++)
            {
                int pr = (Math.Clamp((int)Math.Round(cu[q]), 0, maxVal) << 16) | Math.Clamp((int)Math.Round(cv[q]), 0, maxVal);
                if (!cols.Slice(0, nc).Contains(pr)) cols[nc++] = pr;
            }
            if (nc < 2) continue;
            cols.Slice(0, nc).Sort();
            var cand = new UvPal<TP> { Size = nc, Map = new byte[cw * ch], PredU = new TP[cw * ch], PredV = new TP[cw * ch] };
            for (int i = 0; i < nc; i++) { cand.U[i] = (ushort)(cols[i] >> 16); cand.V[i] = (ushort)(cols[i] & 0xFFFF); }
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int o = (cby + y) * c.Cw + cbx + x, bi = 0; long bd = long.MaxValue;
                    for (int q = 0; q < cand.Size; q++)
                    {
                        long du = Px.I(c.U[o]) - cand.U[q], dv = Px.I(c.V[o]) - cand.V[q], d = du * du + dv * dv;
                        if (d < bd) { bd = d; bi = q; }
                    }
                    cand.Map[y * cw + x] = (byte)bi; cand.PredU[y * cw + x] = Px.T<TP>(cand.U[bi]); cand.PredV[y * cw + x] = Px.T<TP>(cand.V[bi]);
                }
            list.Add(cand);
        }
        return list;
    }

    /// <summary>Bits of a chroma palette beyond the DC uv_mode symbol: has_palette_uv=1, size, colours, index map.</summary>
    private static double UvPaletteBits<TP>(ColorPartCtx<TP> c, UvPal<TP> p, int bx4, int by4, int bs, bool lumaPal, int cw, int ch) where TP : unmanaged
    {
        int szCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: true);
        return Av1CoeffEncode.BoolBits(c.Cdf.GetPalUvCdf(lumaPal ? 1 : 0)[0], 1)
            + Av1CoeffEncode.SymBits(c.Cdf.GetPalSzCdf(1, szCtx), p.Size - 2)
            + Av1CoeffEncode.ChromaPaletteColorBits(p.U, p.V, p.Size, lCol, lSz, aCol, aSz, Bd)
            + EstimatePaletteIndexBits(c.Cdf.Mode, p.Map, p.Size, cw, ch, cw >> 2, ch >> 2);
    }

    /// <summary>Palette syntax of a block (flags in decode_b order are the caller's): the luma palette size + colours.</summary>
    private static void EmitLumaPaletteColors<TP>(ColorPartCtx<TP> c, LumaPal<TP> p, int bx4, int by4, int szCtx) where TP : unmanaged
    {
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetPalSzCdf(0, szCtx), p.Size - 2, 6);
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: false);
        Av1CoeffEncode.EncodeLumaPaletteColorsCore(c.Msac, p.Colors, p.Size, lCol, lSz, aCol, aSz, Bd);
    }

    private static void EmitUvPaletteColors<TP>(ColorPartCtx<TP> c, UvPal<TP> p, int bx4, int by4, int szCtx) where TP : unmanaged
    {
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetPalSzCdf(1, szCtx), p.Size - 2, 6);
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: true);
        Av1CoeffEncode.EncodeChromaPaletteColors(c.Msac, p.U, p.V, p.Size, lCol, lSz, aCol, aSz, Bd);
    }

    /// <summary>Neighbour palette state after a block (the decoder's pal_sz / pal cache updates over the block's
    /// 4-unit extent): luma size + colours, chroma size + U colours.</summary>
    private static void FillPaletteCtx<TP>(ColorPartCtx<TP> c, int bx4, int by4, int w4, int h4, LumaPal<TP>? yp, UvPal<TP>? uvp) where TP : unmanaged
    {
        int bxR = bx4 & 31, byR = by4 & 31;
        byte ys = (byte)(yp?.Size ?? 0), us = (byte)(uvp?.Size ?? 0);
        for (int i = 0; i < w4 && bxR + i < 32; i++)
        {
            c.APalSz[bxR + i] = ys; c.APalSzUv[bxR + i] = us;
            if (yp != null) for (int k = 0; k < yp.Size; k++) c.APalCol[(bxR + i) * 8 + k] = yp.Colors[k];
            if (uvp != null) for (int k = 0; k < uvp.Size; k++) c.APalColU[(bxR + i) * 8 + k] = uvp.U[k];
        }
        for (int j = 0; j < h4 && byR + j < 32; j++)
        {
            c.LPalSz[byR + j] = ys; c.LPalSzUv[byR + j] = us;
            if (yp != null) for (int k = 0; k < yp.Size; k++) c.LPalCol[(byR + j) * 8 + k] = yp.Colors[k];
            if (uvp != null) for (int k = 0; k < uvp.Size; k++) c.LPalColU[(byR + j) * 8 + k] = uvp.U[k];
        }
    }
}
