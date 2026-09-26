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
    internal static bool DetectScreenContent(ReadOnlySpan<ushort> luma, int stride, int width, int height, int bitDepth, bool fast)
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
                    for (int x = 0; x < blk; x++) b8[y * blk + x] = (byte)(luma[(r + y) * stride + c + x] >> shift);
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
    private static uint PerPixelVariance16(ReadOnlySpan<ushort> luma, int stride, int x0, int y0, int bitDepth)
    {
        long sum = 0, sse = 0;
        int off = 128 << (bitDepth - 8);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++) { int d = luma[(y0 + y) * stride + x0 + x] - off; sum += d; sse += (long)d * d; }
        if (bitDepth == 10) { sse = (sse + 8) >> 4; sum = (sum + 2) >> 2; }
        else if (bitDepth == 12) { sse = (sse + 128) >> 8; sum = (sum + 8) >> 4; }
        long var = sse - sum * sum / 256;
        if (var < 0) var = 0;
        return (uint)((var + 128) >> 8);
    }

    private sealed class LumaPal
    {
        public ushort[] Colors = new ushort[8];
        public int Size;
        public byte[] Map = null!;       // stride = block width
        public ushort[] Pred = null!;
        public int[] Coeffs = null!;
        public Av1TxType Inv;
        public int Idx;
        public double J;
    }

    private sealed class UvPal
    {
        public ushort[] U = new ushort[8], V = new ushort[8];
        public int Size;
        public byte[] Map = null!;       // stride = chroma block width
        public ushort[] PredU = null!, PredV = null!;
    }

    [ThreadStatic] private static int[]? t_palHist;

    // Distinct values of a w x h region (ascending) with their counts; returns the count, or -1 when above max.
    private static int CountColors(ReadOnlySpan<ushort> plane, int stride, int x0, int y0, int w, int h, int max,
        Span<ushort> vals, Span<int> cnts)
    {
        var hist = t_palHist ??= new int[1 << 12];
        int n = 0;
        for (int y = 0; y < h; y++)
        {
            int o = (y0 + y) * stride + x0;
            for (int x = 0; x < w; x++) if (hist[plane[o + x]]++ == 0) n++;
        }
        int k = 0;
        bool over = n > max;
        for (int y = 0; y < h; y++)
        {
            int o = (y0 + y) * stride + x0;
            for (int x = 0; x < w; x++)
            {
                int v = plane[o + x];
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
    private static void PaletteMap(ReadOnlySpan<ushort> plane, int stride, int x0, int y0, int w, int h,
        ushort[] colors, int size, byte[] map, ushort[] pred)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = plane[(y0 + y) * stride + x0 + x], bi = 0, bd = int.MaxValue;
                for (int c = 0; c < size; c++) { int d = Math.Abs(v - colors[c]); if (d < bd) { bd = d; bi = c; } }
                map[y * w + x] = (byte)bi; pred[y * w + x] = colors[bi];
            }
    }

    // Luma palette cache of the block's neighbours (the decoder's get_palette_cache inputs).
    private static (int LeftSz, int AboveSz) PaletteNeighbours(ColorPartCtx c, int bx4, int by4, Span<ushort> lCol, Span<ushort> aCol, bool uv)
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
    private static LumaPal? SearchLumaPalette(ColorPartCtx c, int bs, int lumaTx, int bx4, int by4, int w, int h, Span<ushort> ymCdf)
    {
        int bx = bx4 * 4, by = by4 * 4, bxR = bx4 & 31, byR = by4 & 31;
        Span<ushort> vals = stackalloc ushort[64]; Span<int> cnts = stackalloc int[64];
        int nv = CountColors(c.Luma, c.W, bx, by, w, h, 64, vals, cnts);
        if (nv < 2) return null;
        int bw4 = w >> 2, bh4 = h >> 2;
        int szCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
        int palCtx = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: false);
        double lambda = RdLambdaK * c.AcDq * c.AcDq;
        int scanLen = Av1Tables.Scans[lumaTx].Length;
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        var txSet = LumaTxSet(lumaTx);
        double baseBits = Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc)
            + Av1CoeffEncode.BoolBits(c.Cdf.GetPalYCdf(szCtx, palCtx)[0], 1);
        int maxVal = (1 << Bd) - 1;
        LumaPal? best = null;
        var colors = new ushort[8];
        var qf = new double[scanLen];
        var res = new int[w * h];
        for (int k = Math.Min(nv, 8); k >= 2; k--)
        {
            int size;
            if (nv <= 8 && k == nv) { for (int i = 0; i < nv; i++) colors[i] = vals[i]; size = nv; }
            else size = KMeans1D(vals, cnts, nv, k, maxVal, colors);
            if (size < 2) continue;
            var cand = new LumaPal { Size = size, Map = new byte[w * h], Pred = new ushort[w * h] };
            Array.Copy(colors, cand.Colors, size);
            PaletteMap(c.Luma, c.W, bx, by, w, h, cand.Colors, size, cand.Map, cand.Pred);
            double palBits = baseBits + Av1CoeffEncode.SymBits(c.Cdf.GetPalSzCdf(0, szCtx), size - 2)
                + Av1CoeffEncode.LumaPaletteColorBits(cand.Colors, size, lCol, lSz, aCol, aSz, Bd)
                + EstimatePaletteIndexBits(c.Cdf.Mode, cand.Map, size, w, h, bw4, bh4);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) res[y * w + x] = c.Luma[(by + y) * c.W + bx + x] - cand.Pred[y * w + x];
            double bestJ = double.MaxValue;
            foreach (var (fwd, inv, idx) in txSet)
            {
                if (inv == Av1TxType.VDct || inv == Av1TxType.HDct) continue;   // 2D types only (RDOQ / estimate)
                int[] cf = Av1FwdTransform.ForwardQuantRect(res, w, h, lumaTx, c.DcDq, c.AcDq, scanLen, fwd, qf);
                double rate = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)Av1IntraPredMode.Dc, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet);
                double j = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, cand.Pred, c.Luma, c.W, bx, by, inv) + lambda * (rate + palBits);
                if (j < bestJ) { bestJ = j; cand.Coeffs = cf; cand.Inv = inv; cand.Idx = idx; }
            }
            cand.J = bestJ;
            if (best == null || cand.J < best.J) best = cand;
        }
        if (best != null && Sp.UseRdoq)
        {
            // RDOQ the winner (as the regular leaf does after its choice)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) res[y * w + x] = c.Luma[(by + y) * c.W + bx + x] - best.Pred[y * w + x];
            Av1FwdTransform.ForwardQuantRect(res, w, h, lumaTx, c.DcDq, c.AcDq, scanLen, FwdTypeForTxType(best.Inv), qf);
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)Av1IntraPredMode.Dc, best.Coeffs, qf,
                c.DcDq, c.AcDq, 0, ySign, best.Idx, RdoqLambdaScale * RdLambdaK * c.AcDq * c.AcDq);
        }
        return best;
    }

    /// <summary>Chroma palette candidates for a fully-inside cw x ch chroma block: 2-D k-means over (u, v) pairs,
    /// sizes min(colours, 8) down to 2, pairs ordered by U (the U palette must ascend).</summary>
    private static List<UvPal> UvPaletteCandidates(ColorPartCtx c, int cbx, int cby, int cw, int ch)
    {
        var list = new List<UvPal>();
        var pairs = new Dictionary<int, int>();
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int o = (cby + y) * c.Cw + cbx + x, key = (c.U[o] << 16) | c.V[o];
                pairs[key] = pairs.TryGetValue(key, out int n) ? n + 1 : 1;
                if (pairs.Count > 64) return list;
            }
        if (pairs.Count < 2) return list;
        var pts = new List<(int U, int V, int N)>();
        foreach (var kv in pairs) pts.Add((kv.Key >> 16, kv.Key & 0xFFFF, kv.Value));
        pts.Sort((a, b) => b.N.CompareTo(a.N));
        int maxVal = (1 << Bd) - 1;
        for (int k = Math.Min(pts.Count, 8); k >= 2; k--)
        {
            Span<double> cu = stackalloc double[8], cv = stackalloc double[8];
            for (int i = 0; i < k; i++) { cu[i] = pts[i].U; cv[i] = pts[i].V; }
            if (k < pts.Count)
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
            var cols = new List<(ushort U, ushort V)>();
            for (int q = 0; q < k; q++)
            {
                var pr = ((ushort)Math.Clamp((int)Math.Round(cu[q]), 0, maxVal), (ushort)Math.Clamp((int)Math.Round(cv[q]), 0, maxVal));
                if (!cols.Contains(pr)) cols.Add(pr);
            }
            if (cols.Count < 2) continue;
            cols.Sort((a, b) => a.U != b.U ? a.U.CompareTo(b.U) : a.V.CompareTo(b.V));
            var cand = new UvPal { Size = cols.Count, Map = new byte[cw * ch], PredU = new ushort[cw * ch], PredV = new ushort[cw * ch] };
            for (int i = 0; i < cols.Count; i++) { cand.U[i] = cols[i].U; cand.V[i] = cols[i].V; }
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int o = (cby + y) * c.Cw + cbx + x, bi = 0; long bd = long.MaxValue;
                    for (int q = 0; q < cand.Size; q++)
                    {
                        long du = c.U[o] - cand.U[q], dv = c.V[o] - cand.V[q], d = du * du + dv * dv;
                        if (d < bd) { bd = d; bi = q; }
                    }
                    cand.Map[y * cw + x] = (byte)bi; cand.PredU[y * cw + x] = cand.U[bi]; cand.PredV[y * cw + x] = cand.V[bi];
                }
            list.Add(cand);
        }
        return list;
    }

    /// <summary>Bits of a chroma palette beyond the DC uv_mode symbol: has_palette_uv=1, size, colours, index map.</summary>
    private static double UvPaletteBits(ColorPartCtx c, UvPal p, int bx4, int by4, int bs, bool lumaPal, int cw, int ch)
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
    private static void EmitLumaPaletteColors(ColorPartCtx c, LumaPal p, int bx4, int by4, int szCtx)
    {
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetPalSzCdf(0, szCtx), p.Size - 2, 6);
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: false);
        Av1CoeffEncode.EncodeLumaPaletteColorsCore(c.Msac, p.Colors, p.Size, lCol, lSz, aCol, aSz, Bd);
    }

    private static void EmitUvPaletteColors(ColorPartCtx c, UvPal p, int bx4, int by4, int szCtx)
    {
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetPalSzCdf(1, szCtx), p.Size - 2, 6);
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        var (lSz, aSz) = PaletteNeighbours(c, bx4, by4, lCol, aCol, uv: true);
        Av1CoeffEncode.EncodeChromaPaletteColors(c.Msac, p.U, p.V, p.Size, lCol, lSz, aCol, aSz, Bd);
    }

    /// <summary>Neighbour palette state after a block (the decoder's pal_sz / pal cache updates over the block's
    /// 4-unit extent): luma size + colours, chroma size + U colours.</summary>
    private static void FillPaletteCtx(ColorPartCtx c, int bx4, int by4, int w4, int h4, LumaPal? yp, UvPal? uvp)
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
