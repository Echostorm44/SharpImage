namespace SharpImage.Formats.Av1;

/// <summary>
/// Per-superblock CDEF strength search (after libaom's av1_cdef_search): every non-skip 8x8 block of the deblocked
/// picture is filtered with each of the 64 luma and 64 chroma strength codes exactly as the decoder applies CDEF
/// (direction and variance from the pre-CDEF luma, variance-adjusted luma primary strength, chroma with the luma
/// direction and damping - 1, pre-CDEF neighbours), accumulating the SSE per 64x64 superblock and strength. Then the
/// strength set — 1, 2, 4 or 8 (luma, chroma) pairs, greedily built and refined — and each superblock's index are
/// chosen for the lowest SSE + λ·bits.
/// </summary>
internal static class Av1CdefSearch
{
    internal readonly record struct Result(Av1ObuWriter.CdefParams Params, sbyte[] SbIdx, double Cost);

    /// <summary>The strength codes (pri &lt;&lt; 2 | sec index) searched at a libaom CDEF_FAST_SEARCH level: 0 = all 64,
    /// 1 = pri {0,1,2,3,5,7,10,13} x all sec, 2 = pri {0,2,4,8,14} x all sec, 3 = those pri x sec {0,2},
    /// 4 = pri {0,11} x sec {0,2} (pickcdef.c priconv_lvl1/2/4, secconv_lvl3).</summary>
    internal static int[] Codes(int level)
    {
        int[] pri = level switch { 0 => Enumerable.Range(0, 16).ToArray(), 1 => [0, 1, 2, 3, 5, 7, 10, 13], 2 or 3 => [0, 2, 4, 8, 14], _ => [0, 11] };
        int[] sec = level >= 3 ? [0, 2] : [0, 1, 2, 3];
        return pri.SelectMany(p => sec.Select(q => p << 2 | q)).ToArray();
    }

    /// <summary>Planes are the deblocked (pre-CDEF) reconstruction with strides; their rows must cover the MI grid
    /// (8-aligned) plus two below. noskip is the decoder's 8x8 map (w8 x h8). 4:2:0 only.</summary>
    internal static Result? Search(ushort[] yP, int ys, ushort[] uP, ushort[] vP, int cs, int planeRowsY, int planeRowsC,
        byte[] noskip, int w8, int h8, ushort[] srcY, ushort[] srcU, ushort[] srcV, int width, int height, int cw, int ch,
        int sbCols, int sbRows, int baseQIdx, int bitDepth, double lambda, int threads, int level = 0)
    {
        int[] codes = Codes(level);
        int damping = Math.Clamp(3 + (baseQIdx >> 6), 3, 6);
        int bdMin8 = bitDepth - 8;
        int w4 = w8 * 2, h4 = h8 * 2;
        int nSb = sbCols * sbRows;
        var sseY = new long[nSb * 64];
        var sseC = new long[nSb * 64];
        var has = new bool[nSb];

        void SbRow(int sby)
        {
            Span<ushort> scratch = stackalloc ushort[8 * 10];
            Span<ushort> left = stackalloc ushort[16];
            Span<long> accY = stackalloc long[64];
            Span<long> accC = stackalloc long[64];
            for (int sbx = 0; sbx < sbCols; sbx++)
            {
                int sb = sby * sbCols + sbx;
                accY.Clear(); accC.Clear();
                bool any = false;
                for (int by8 = sby * 8; by8 < Math.Min(sby * 8 + 8, h8); by8++)
                    for (int bx8 = sbx * 8; bx8 < Math.Min(sbx * 8 + 8, w8); bx8++)
                    {
                        if (noskip[by8 * w8 + bx8] == 0) continue;
                        any = true;
                        int bx = bx8 * 2, by = by8 * 2, px = bx8 * 8, py = by8 * 8;
                        var edges = Av1Cdef.EdgeFlags.None;
                        if (by > 0) edges |= Av1Cdef.EdgeFlags.Top;
                        if (by + 2 < h4) edges |= Av1Cdef.EdgeFlags.Bottom;
                        if (bx > 0) edges |= Av1Cdef.EdgeFlags.Left;
                        if (bx + 2 < w4) edges |= Av1Cdef.EdgeFlags.Right;
                        int dir = Av1Cdef.FindDirection(yP, py * ys + px, ys, out uint variance, bitDepth);

                        // luma
                        BlockSse(yP, ys, planeRowsY, srcY, width, width, height, px, py, 8, edges, scratch, left, dir, variance,
                            damping, bitDepth, bdMin8, luma: true, accY, codes);
                        // chroma (4x4 of each plane, luma direction, damping - 1)
                        BlockSse(uP, cs, planeRowsC, srcU, cw, cw, ch, px >> 1, py >> 1, 4, edges, scratch, left, dir, variance,
                            damping - 1, bitDepth, bdMin8, luma: false, accC, codes);
                        BlockSse(vP, cs, planeRowsC, srcV, cw, cw, ch, px >> 1, py >> 1, 4, edges, scratch, left, dir, variance,
                            damping - 1, bitDepth, bdMin8, luma: false, accC, codes);
                    }
                has[sb] = any;
                for (int k = 0; k < 64; k++) { sseY[sb * 64 + k] = accY[k]; sseC[sb * 64 + k] = accC[k]; }
            }
        }
        if (threads > 1)
            System.Threading.Tasks.Parallel.For(0, sbRows, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, SbRow);
        else
            for (int r = 0; r < sbRows; r++) SbRow(r);

        var sbs = Enumerable.Range(0, nSb).Where(i => has[i]).ToArray();
        if (sbs.Length == 0) return null;

        // Strength-set selection per cdef_bits (1, 2, 4, 8 pairs): greedy additions then refinement passes.
        double bestCost = double.MaxValue;
        (int Y, int C)[] bestSet = [];
        foreach (int bits in new[] { 0, 1, 2, 3 })
        {
            int nb = 1 << bits;
            var set = new List<(int Y, int C)>();
            var cur = new long[sbs.Length];
            Array.Fill(cur, long.MaxValue);
            for (int k = 0; k < nb; k++)
            {
                var (py, pc, _) = BestPair(sbs, sseY, sseC, cur, codes);
                set.Add((py, pc));
                for (int i = 0; i < sbs.Length; i++) cur[i] = Math.Min(cur[i], sseY[sbs[i] * 64 + py] + sseC[sbs[i] * 64 + pc]);
            }
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < nb; k++)
                {
                    // the best replacement for entry k given the others
                    var others = new long[sbs.Length];
                    for (int i = 0; i < sbs.Length; i++)
                    {
                        long m = long.MaxValue;
                        for (int j = 0; j < nb; j++) if (j != k) m = Math.Min(m, sseY[sbs[i] * 64 + set[j].Y] + sseC[sbs[i] * 64 + set[j].C]);
                        others[i] = m;
                    }
                    var (py, pc, _) = BestPair(sbs, sseY, sseC, others, codes);
                    set[k] = (py, pc);
                }
            long total = 0;
            foreach (int sb in sbs)
            {
                long m = long.MaxValue;
                foreach (var (y, c) in set) m = Math.Min(m, sseY[sb * 64 + y] + sseC[sb * 64 + c]);
                total += m;
            }
            double cost = total + lambda * (bits * sbs.Length + 12.0 * nb);
            if (cost < bestCost) { bestCost = cost; bestSet = set.ToArray(); }
        }

        int nbBest = bestSet.Length, bitsBest = nbBest == 1 ? 0 : nbBest == 2 ? 1 : nbBest == 4 ? 2 : 3;
        var sbIdx = new sbyte[nSb];
        Array.Fill(sbIdx, (sbyte)-1);
        foreach (int sb in sbs)
        {
            long m = long.MaxValue; int bi = 0;
            for (int k = 0; k < nbBest; k++)
            {
                long v = sseY[sb * 64 + bestSet[k].Y] + sseC[sb * 64 + bestSet[k].C];
                if (v < m) { m = v; bi = k; }
            }
            sbIdx[sb] = (sbyte)bi;
        }
        var p = new Av1ObuWriter.CdefParams(damping, bitsBest,
            bestSet.Select(s => (byte)s.Y).ToArray(), bestSet.Select(s => (byte)s.C).ToArray());
        return new Result(p, sbIdx, bestCost);
    }

    /// <summary>The CDEF-filtered picture for a search result, as the decoder produces it from the same deblocked planes
    /// (visible width x the planes' rows; skip blocks and superblocks without an index unfiltered) — the loop-restoration
    /// search's input without another decode.</summary>
    internal static ushort[][] Apply(Result r, ushort[] yP, int ys, ushort[] uP, ushort[] vP, int cs, int planeRowsY, int planeRowsC,
        byte[] noskip, int w8, int h8, int width, int height, int cw, int ch, int sbCols, int sbRows, int bitDepth, int threads)
    {
        var outY = (ushort[])yP.Clone(); var outU = (ushort[])uP.Clone(); var outV = (ushort[])vP.Clone();
        int damping = r.Params.Damping, bdMin8 = bitDepth - 8, w4 = w8 * 2, h4 = h8 * 2;
        void SbRow(int sby)
        {
            Span<ushort> scratch = stackalloc ushort[8 * 10];
            Span<ushort> left = stackalloc ushort[16];
            for (int sbx = 0; sbx < sbCols; sbx++)
            {
                int idx = r.SbIdx[sby * sbCols + sbx];
                if (idx < 0) continue;
                int yCode = r.Params.YStrengths[idx], cCode = r.Params.UvStrengths[idx];
                if (yCode == 0 && cCode == 0) continue;
                for (int by8 = sby * 8; by8 < Math.Min(sby * 8 + 8, h8); by8++)
                    for (int bx8 = sbx * 8; bx8 < Math.Min(sbx * 8 + 8, w8); bx8++)
                    {
                        if (noskip[by8 * w8 + bx8] == 0) continue;
                        int bx = bx8 * 2, by = by8 * 2, px = bx8 * 8, py = by8 * 8;
                        var edges = Av1Cdef.EdgeFlags.None;
                        if (by > 0) edges |= Av1Cdef.EdgeFlags.Top;
                        if (by + 2 < h4) edges |= Av1Cdef.EdgeFlags.Bottom;
                        if (bx > 0) edges |= Av1Cdef.EdgeFlags.Left;
                        if (bx + 2 < w4) edges |= Av1Cdef.EdgeFlags.Right;
                        int dir = Av1Cdef.FindDirection(yP, py * ys + px, ys, out uint variance, bitDepth);
                        FilterTo(yP, ys, planeRowsY, outY, width, height, px, py, 8, edges, scratch, left, dir, variance, damping, bitDepth, bdMin8, true, yCode);
                        FilterTo(uP, cs, planeRowsC, outU, cw, ch, px >> 1, py >> 1, 4, edges, scratch, left, dir, variance, damping - 1, bitDepth, bdMin8, false, cCode);
                        FilterTo(vP, cs, planeRowsC, outV, cw, ch, px >> 1, py >> 1, 4, edges, scratch, left, dir, variance, damping - 1, bitDepth, bdMin8, false, cCode);
                    }
            }
        }
        if (threads > 1)
            System.Threading.Tasks.Parallel.For(0, sbRows, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, SbRow);
        else
            for (int y = 0; y < sbRows; y++) SbRow(y);
        return [outY, outU, outV];
    }

    // Filters one block of a plane with one strength code (as BlockSse does) and writes its visible part to dst.
    private static void FilterTo(ushort[] plane, int stride, int planeRows, ushort[] dst, int visW, int visH, int px, int py, int n,
        Av1Cdef.EdgeFlags edges, Span<ushort> scratch, Span<ushort> left, int dir, uint variance, int damping, int bitDepth, int bdMin8,
        bool luma, int code)
    {
        int w = Math.Min(n, visW - px), h = Math.Min(n, visH - py);
        if (w <= 0 || h <= 0) return;
        int pri = (code >> 2) << bdMin8;
        int sec = code & 3;
        sec += sec == 3 ? 1 : 0;
        sec <<= bdMin8;
        int adjPri = luma ? (pri != 0 ? Av1Cdef.AdjustStrength(pri, variance) : 0) : pri;
        if (adjPri == 0 && sec == 0) return;
        int off = py * stride + px;
        if ((edges & Av1Cdef.EdgeFlags.Left) != 0)
            for (int y = 0; y < n; y++) { left[y * 2] = plane[off + y * stride - 2]; left[y * 2 + 1] = plane[off + y * stride - 1]; }
        int topOff = py >= 2 ? (py - 2) * stride + px : off;
        int botOff = (edges & Av1Cdef.EdgeFlags.Bottom) != 0 ? (py + n) * stride + px : off + (n - 1) * stride;
        if ((edges & Av1Cdef.EdgeFlags.Bottom) != 0 && (py + n + 2) > planeRows) edges &= ~Av1Cdef.EdgeFlags.Bottom;
        int sw = n + 2;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < sw; x++)
                scratch[y * sw + x] = px + x < stride ? plane[off + y * stride + x] : (ushort)0;
        Av1Cdef.FilterBlock(scratch, 0, sw, left, 0, 2, plane, topOff, plane, botOff,
            adjPri, sec, pri != 0 ? dir : 0, damping, n, n, edges, bitDepth, stride);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) dst[off + y * stride + x] = scratch[y * sw + x];
    }

    // The (luma, chroma) pair minimising sum_i min(cur[i], sse(pair)) (luma and chroma are independent given cur).
    private static (int Y, int C, long Total) BestPair(int[] sbs, long[] sseY, long[] sseC, long[] cur, int[] codes)
    {
        long best = long.MaxValue; int by = 0, bc = 0;
        foreach (int y in codes)
            foreach (int c in codes)
            {
                long t = 0;
                for (int i = 0; i < sbs.Length && t < best; i++)
                {
                    long v = sseY[sbs[i] * 64 + y] + sseC[sbs[i] * 64 + c];
                    t += v < cur[i] ? v : cur[i];
                }
                if (t < best) { best = t; by = y; bc = c; }
            }
        return (by, bc, best);
    }

    // Accumulates, for each of the 64 strength codes (pri << 2 | sec), the SSE of this block after CDEF vs the source.
    private static void BlockSse(ushort[] plane, int stride, int planeRows, ushort[] src, int srcStride, int visW, int visH,
        int px, int py, int n, Av1Cdef.EdgeFlags edges, Span<ushort> scratch, Span<ushort> left, int dir, uint variance,
        int damping, int bitDepth, int bdMin8, bool luma, Span<long> acc, int[] codes)
    {
        int w = Math.Min(n, visW - px), h = Math.Min(n, visH - py);
        if (w <= 0 || h <= 0) return;
        int off = py * stride + px;
        // left context (2 columns of pre-CDEF pixels) and the unfiltered SSE
        if ((edges & Av1Cdef.EdgeFlags.Left) != 0)
            for (int y = 0; y < n; y++) { left[y * 2] = plane[off + y * stride - 2]; left[y * 2 + 1] = plane[off + y * stride - 1]; }
        long sse0 = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int d = plane[off + y * stride + x] - src[(py + y) * srcStride + px + x]; sse0 += d * d; }
        int topOff = py >= 2 ? (py - 2) * stride + px : off;
        int botOff = (edges & Av1Cdef.EdgeFlags.Bottom) != 0 ? (py + n) * stride + px : off + (n - 1) * stride;
        if ((edges & Av1Cdef.EdgeFlags.Bottom) != 0 && (py + n + 2) > planeRows) edges &= ~Av1Cdef.EdgeFlags.Bottom;
        int sw = n + 2;
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            // The padded neighbourhood does not depend on the strength: build it once, filter every code from it.
            Span<short> tmp = stackalloc short[144];
            Av1Cdef.PadBlock(tmp, plane, off, stride, left, 0, 2, plane, topOff, plane, botOff, n, n, edges, stride);
            foreach (int code in codes)
            {
                int pri = (code >> 2) << bdMin8;
                int sec = code & 3;
                sec += sec == 3 ? 1 : 0;
                sec <<= bdMin8;
                int adjPri = luma ? (pri != 0 ? Av1Cdef.AdjustStrength(pri, variance) : 0) : pri;
                if (adjPri == 0 && sec == 0) { acc[code] += sse0; continue; }
                Av1Cdef.FilterPadded(tmp, scratch, 0, n, adjPri, sec, pri != 0 ? dir : 0, damping, n, n, bitDepth);
                long sse = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) { int d = scratch[y * n + x] - src[(py + y) * srcStride + px + x]; sse += d * d; }
                acc[code] += sse;
            }
            return;
        }
        foreach (int code in codes)
        {
            int pri = (code >> 2) << bdMin8;
            int sec = code & 3;
            sec += sec == 3 ? 1 : 0;
            sec <<= bdMin8;
            int adjPri = luma ? (pri != 0 ? Av1Cdef.AdjustStrength(pri, variance) : 0) : pri;
            if (adjPri == 0 && sec == 0) { acc[code] += sse0; continue; }
            // the block plus its two pre-CDEF right-hand columns (the filter reads them from the block's own buffer)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < sw; x++)
                {
                    int sx = px + x;
                    scratch[y * sw + x] = sx < stride ? plane[off + y * stride + x] : (ushort)0;
                }
            Av1Cdef.FilterBlock(scratch, 0, sw, left, 0, 2, plane, topOff, plane, botOff,
                adjPri, sec, pri != 0 ? dir : 0, damping, n, n, edges, bitDepth, stride);
            long sse = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) { int d = scratch[y * sw + x] - src[(py + y) * srcStride + px + x]; sse += d * d; }
            acc[code] += sse;
        }
    }
}
