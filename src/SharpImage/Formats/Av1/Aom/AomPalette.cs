using System;
using System.Numerics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMacroblock
{
    // x->palette_buffer->kmeans_data_buf. Persistent across blocks (libaom allocates it once per thread, uninitialised):
    // the AVX2 k-means kernels process 16 points at a time and read up to 15 stale points past a block's data, which
    // count in the k-means distance (see AomPalette.CalcIndicesDim2).
    public readonly short[] KmeansDataBuf = new short[2 * AomPalette.MAX_PALETTE_SQUARE];
    // x->color_palette_thresh (encodeframe.c sets 64; the nonrd path lowers it)
    public int ColorPaletteThresh = 64;
    // x->color_sensitivity[2], x->min_dist_inter_uv (nonrd pick mode only)
    public readonly byte[] ColorSensitivity = new byte[2];
    public long MinDistInterUv;
}

internal sealed partial class AomComp
{
    // cm->seq_params->bit_depth / use_highbitdepth
    public int BitDepth = 8;
    public bool UseHighbitdepth;
    // cpi->rc.high_source_sad
    public int RcHighSourceSad;
}

// Port of libaom 3.14.1 av1/encoder/palette.c (the parts that do not need the tx search: see AomPalette.Search.cs for
// palette_rd_y and the two search entry points), av1/encoder/k_means_template.h with the AVX2 kernels libaom
// dispatches (av1/encoder/x86/av1_k_means_avx2.c), the palette pieces of pred_common.c (av1_get_palette_cache),
// entropymode.c (av1_get_palette_color_index_context), tokenize.c (av1_cost_color_map and the encoder's fast colour
// context) and intra_mode_search.c (av1_count_colors*). 8-bit; the high-bitdepth branches are marked.
internal static partial class AomPalette
{
    public const int PALETTE_MIN_SIZE = 2, PALETTE_MAX_SIZE = 8;
    public const int MAX_PALETTE_SQUARE = 64 * 64;
    public const int MAX_PALETTE_BLOCK_WIDTH = 64, MAX_PALETTE_BLOCK_HEIGHT = 64;
    public const int PALETTE_COLOR_INDEX_CONTEXTS = 5, NUM_PALETTE_NEIGHBORS = 3, MAX_COLOR_CONTEXT_HASH = 8;
    private const int MIN_SB_SIZE_LOG2 = 6;

    // ---- msvcrt qsort ----------------------------------------------------------------------------------------------
    // libaom sorts with the C library's qsort; avifenc (mingw-w64) links msvcrt.dll's. qsort is not stable, but every
    // palette.c call sorts at most PALETTE_MAX_SIZE elements, and msvcrt's qsort hands any range of <= 8 elements
    // (CUTOFF) straight to its shortsort: repeatedly select the maximum (the first of equal maxima, as it replaces the
    // running max only on comp(p, max) > 0) and swap it to the end. That is reproduced here exactly, comparator call
    // for comparator call (AomPaletteTwinTests.MsvcrtQsort_* check it through the real msvcrt.dll, ties included).

    internal delegate int QsortComparer<T>(in T a, in T b);

    /// <summary>msvcrt qsort for num &lt;= 8 (its shortsort; larger ranges use a quicksort libaom never reaches here).</summary>
    internal static void MsvcrtQsort<T>(Span<T> a, QsortComparer<T> comp)
    {
        int num = a.Length;
        if (num < 2) return;
        if (num > 8) throw new NotSupportedException("msvcrt qsort emulation covers the shortsort range (<= 8 elements)");
        // shortsort(lo, hi)
        int hi = num - 1;
        while (hi > 0)
        {
            int max = 0;
            for (int p = 1; p <= hi; p++)
                if (comp(in a[p], in a[max]) > 0) max = p;
            if (max != hi) (a[max], a[hi]) = (a[hi], a[max]);
            hi--;
        }
    }

    private static int Int16Comparer(in short a, in short b) => a - b;
    private static readonly QsortComparer<short> Int16ComparerFn = Int16Comparer;

    // ---- palette.c leaves ------------------------------------------------------------------------------------------

    /// <summary>remove_duplicates: sort the (integer) centroids and drop repeats; returns the unique count.</summary>
    internal static int RemoveDuplicates(Span<short> centroids, int numCentroids)
    {
        MsvcrtQsort(centroids.Slice(0, numCentroids), Int16ComparerFn);
        int numUnique = 1;
        for (int i = 1; i < numCentroids; ++i)
            if (centroids[i] != centroids[i - 1]) centroids[numUnique++] = centroids[i];
        return numUnique;
    }

    /// <summary>aom_ceil_log2.</summary>
    internal static int CeilLog2(int n) => n < 2 ? 0 : BitOperations.Log2((uint)(n - 1)) + 1;

    /// <summary>delta_encode_cost.</summary>
    internal static int DeltaEncodeCost(ReadOnlySpan<int> colors, int num, int bitDepth, int minVal)
    {
        if (num <= 0) return 0;
        int bitsCost = bitDepth;
        if (num == 1) return bitsCost;
        bitsCost += 2;
        int maxDelta = 0;
        Span<int> deltas = stackalloc int[PALETTE_MAX_SIZE];
        int minBits = bitDepth - 3;
        for (int i = 1; i < num; ++i)
        {
            int delta = colors[i] - colors[i - 1];
            deltas[i - 1] = delta;
            if (delta > maxDelta) maxDelta = delta;
        }
        int bitsPerDelta = Math.Max(CeilLog2(maxDelta + 1 - minVal), minBits);
        int range = (1 << bitDepth) - colors[0] - minVal;
        for (int i = 0; i < num - 1; ++i)
        {
            bitsCost += bitsPerDelta;
            range -= deltas[i];
            bitsPerDelta = Math.Min(bitsPerDelta, CeilLog2(range));
        }
        return bitsCost;
    }

    /// <summary>av1_index_color_cache: flags the cache entries among colors; outputs the colors not in the cache.</summary>
    internal static int IndexColorCache(ReadOnlySpan<ushort> colorCache, int nCache, ReadOnlySpan<ushort> colors, int nColors,
        Span<byte> cacheColorFound, Span<int> outCacheColors)
    {
        if (nCache <= 0)
        {
            for (int i = 0; i < nColors; ++i) outCacheColors[i] = colors[i];
            return nColors;
        }
        cacheColorFound.Slice(0, nCache).Clear();
        int nInCache = 0;
        Span<int> inCacheFlags = stackalloc int[PALETTE_MAX_SIZE];
        inCacheFlags.Clear();
        for (int i = 0; i < nCache && nInCache < nColors; ++i)
        {
            for (int jj = 0; jj < nColors; ++jj)
            {
                if (colors[jj] == colorCache[i])
                {
                    inCacheFlags[jj] = 1;
                    cacheColorFound[i] = 1;
                    ++nInCache;
                    break;
                }
            }
        }
        int j = 0;
        for (int i = 0; i < nColors; ++i)
            if (inCacheFlags[i] == 0) outCacheColors[j++] = colors[i];
        return j;
    }

    /// <summary>av1_get_palette_delta_bits_v.</summary>
    internal static int GetPaletteDeltaBitsV(in AomPaletteModeInfo pmi, int bitDepth, out int zeroCount, out int minBits)
    {
        int n = pmi.PaletteSize1;
        int maxVal = 1 << bitDepth;
        int maxD = 0;
        minBits = bitDepth - 4;
        zeroCount = 0;
        for (int i = 1; i < n; ++i)
        {
            int delta = pmi.PaletteColors[2 * PALETTE_MAX_SIZE + i] - pmi.PaletteColors[2 * PALETTE_MAX_SIZE + i - 1];
            int v = Math.Abs(delta);
            int d = Math.Min(v, maxVal - v);
            if (d > maxD) maxD = d;
            if (d == 0) ++zeroCount;
        }
        return Math.Max(CeilLog2(maxD + 1), minBits);
    }

    /// <summary>av1_palette_color_cost_y.</summary>
    internal static int PaletteColorCostY(in AomPaletteModeInfo pmi, ReadOnlySpan<ushort> colorCache, int nCache, int bitDepth)
    {
        int n = pmi.PaletteSize0;
        Span<int> outCacheColors = stackalloc int[PALETTE_MAX_SIZE];
        Span<byte> cacheColorFound = stackalloc byte[2 * PALETTE_MAX_SIZE];
        int nOutCache = IndexColorCache(colorCache, nCache, pmi.PaletteColors, n, cacheColorFound, outCacheColors);
        int totalBits = nCache + DeltaEncodeCost(outCacheColors, nOutCache, bitDepth, 1);
        return AomCost.CostLiteral(totalBits);
    }

    /// <summary>av1_palette_color_cost_uv.</summary>
    internal static int PaletteColorCostUv(in AomPaletteModeInfo pmi, ReadOnlySpan<ushort> colorCache, int nCache, int bitDepth)
    {
        int n = pmi.PaletteSize1;
        int totalBits = 0;
        // U channel palette color cost.
        Span<int> outCacheColors = stackalloc int[PALETTE_MAX_SIZE];
        Span<byte> cacheColorFound = stackalloc byte[2 * PALETTE_MAX_SIZE];
        int nOutCache = IndexColorCache(colorCache, nCache, pmi.PaletteColors.AsSpan(PALETTE_MAX_SIZE), n, cacheColorFound, outCacheColors);
        totalBits += nCache + DeltaEncodeCost(outCacheColors, nOutCache, bitDepth, 0);

        // V channel palette color cost.
        int bitsV = GetPaletteDeltaBitsV(pmi, bitDepth, out int zeroCount, out _);
        int bitsUsingDelta = 2 + bitDepth + (bitsV + 1) * (n - 1) - zeroCount;
        int bitsUsingRaw = bitDepth * n;
        totalBits += 1 + Math.Min(bitsUsingDelta, bitsUsingRaw);
        return AomCost.CostLiteral(totalBits);
    }

    /// <summary>extend_palette_color_map: grow the orig_width x orig_height map to new_width x new_height in place,
    /// repeating the last column / row.</summary>
    internal static void ExtendPaletteColorMap(byte[] colorMap, int origWidth, int origHeight, int newWidth, int newHeight)
    {
        if (newWidth == origWidth && newHeight == origHeight) return;
        for (int j = origHeight - 1; j >= 0; --j)
        {
            Buffer.BlockCopy(colorMap, j * origWidth, colorMap, j * newWidth, origWidth);   // memmove
            // Copy last column to extra columns.
            colorMap.AsSpan(j * newWidth + origWidth, newWidth - origWidth).Fill(colorMap[j * newWidth + origWidth - 1]);
        }
        // Copy last row to extra rows.
        for (int j = origHeight; j < newHeight; ++j)
            Buffer.BlockCopy(colorMap, (origHeight - 1) * newWidth, colorMap, j * newWidth, newWidth);
    }

    /// <summary>optimize_palette_colors: snap centroids within 4 (8-bit) of a cache color to it.</summary>
    internal static void OptimizePaletteColors(ReadOnlySpan<ushort> colorCache, int nCache, int nColors, int stride, Span<short> centroids, int bitDepth)
    {
        if (nCache <= 0) return;
        for (int i = 0; i < nColors * stride; i += stride)
        {
            int minDiff = Math.Abs(centroids[i] - colorCache[0]);
            int idx = 0;
            for (int j = 1; j < nCache; ++j)
            {
                int thisDiff = Math.Abs(centroids[i] - colorCache[j]);
                if (thisDiff < minDiff)
                {
                    minDiff = thisDiff;
                    idx = j;
                }
            }
            int minThreshold = 4 << (bitDepth - 8);
            if (minDiff <= minThreshold) centroids[i] = (short)colorCache[idx];
        }
    }

    /// <summary>set_stage2_params: search the winner's neighbours (+- 1).</summary>
    internal static void SetStage2Params(out int minN, out int maxN, out int stepSize, int winner, int endN)
    {
        minN = winner == PALETTE_MIN_SIZE ? PALETTE_MIN_SIZE + 1 : Math.Max(winner - 1, PALETTE_MIN_SIZE);
        maxN = winner == endN ? winner - 1 : Math.Min(winner + 1, PALETTE_MAX_SIZE);
        stepSize = Math.Max(1, maxN - minN);
    }

    /// <summary>fill_data_and_get_bounds (8-bit).</summary>
    internal static void FillDataAndGetBounds(byte[] src, int srcOffset, int srcStride, int rows, int cols, bool isHighBitdepth, short[] data,
        out int lowerBound, out int upperBound)
    {
        if (isHighBitdepth) throw new NotImplementedException("high-bitdepth palette: fill_data_and_get_bounds");
        int s = srcOffset, d = 0;
        lowerBound = upperBound = src[s];
        for (int r = 0; r < rows; ++r)
        {
            for (int c = 0; c < cols; ++c)
            {
                int val = src[s + c];
                data[d + c] = (short)val;
                lowerBound = Math.Min(lowerBound, val);
                upperBound = Math.Max(upperBound, val);
            }
            s += srcStride;
            d += cols;
        }
    }

    /// <summary>struct ColorCount.</summary>
    internal struct ColorCount
    {
        public int Index, Count;
    }

    /// <summary>color_count_comp: count descending, then index ascending (never 0 for distinct entries).</summary>
    internal static int ColorCountComp(in ColorCount c1, in ColorCount c2)
    {
        if (c1.Count > c2.Count) return -1;
        if (c1.Count < c2.Count) return 1;
        if (c1.Index < c2.Index) return -1;
        return 1;
    }
    private static readonly QsortComparer<ColorCount> ColorCountCompFn = ColorCountComp;

    /// <summary>find_top_colors: the n_colors most frequent colors (count descending, index ascending on ties).</summary>
    internal static void FindTopColors(ReadOnlySpan<int> countBuf, int bitDepth, int nColors, Span<short> topColors)
    {
        Span<ColorCount> topColorCounts = stackalloc ColorCount[PALETTE_MAX_SIZE];
        topColorCounts.Clear();
        int nColorCount = 0;
        for (int i = 0; i < (1 << bitDepth); ++i)
        {
            if (countBuf[i] > 0)
            {
                if (nColorCount < nColors)
                {
                    // Keep adding to the top colors.
                    topColorCounts[nColorCount].Index = i;
                    topColorCounts[nColorCount].Count = countBuf[i];
                    ++nColorCount;
                    if (nColorCount == nColors) MsvcrtQsort(topColorCounts.Slice(0, nColors), ColorCountCompFn);
                }
                else
                {
                    // Check the worst in the sorted top.
                    if (countBuf[i] > topColorCounts[nColors - 1].Count)
                    {
                        int j = nColors - 1;
                        // Move up to the best one.
                        while (j >= 1 && countBuf[i] > topColorCounts[j - 1].Count) --j;
                        topColorCounts.Slice(j, nColors - j - 1).CopyTo(topColorCounts.Slice(j + 1));   // memmove
                        topColorCounts[j].Index = i;
                        topColorCounts[j].Count = countBuf[i];
                    }
                }
            }
        }
        for (int i = 0; i < nColors; ++i) topColors[i] = (short)topColorCounts[i].Index;
    }

    /// <summary>av1_restore_uv_color_map: recompute the chroma index map from the chosen palette (8-bit).</summary>
    internal static void RestoreUvColorMap(AomComp cpi, AomMacroblock x)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        ref readonly AomPaletteModeInfo pmi = ref mbmi.Palette;
        int bsize = mbmi.Bsize;
        int srcStride = x.Plane[1].Src.Stride;
        byte[] srcU = x.Plane[1].Src.Buf, srcV = x.Plane[2].Src.Buf;
        int srcUOff = x.Plane[1].Src.Offset, srcVOff = x.Plane[2].Src.Offset;
        short[] data = x.KmeansDataBuf;
        Span<short> centroids = stackalloc short[2 * PALETTE_MAX_SIZE];
        byte[] colorMap = xd.Plane[1].ColorIndexMap;
        if (cpi.UseHighbitdepth) throw new NotImplementedException("high-bitdepth palette: av1_restore_uv_color_map");
        AomRdoptUtils.GetBlockDimensions(bsize, 1, xd, out int planeBlockWidth, out int planeBlockHeight, out int rows, out int cols);

        for (int r = 0; r < rows; ++r)
        {
            for (int c = 0; c < cols; ++c)
            {
                data[(r * cols + c) * 2] = srcU[srcUOff + r * srcStride + c];
                data[(r * cols + c) * 2 + 1] = srcV[srcVOff + r * srcStride + c];
            }
        }
        for (int r = 1; r < 3; ++r)
            for (int c = 0; c < pmi.PaletteSize1; ++c)
                centroids[c * 2 + r - 1] = (short)pmi.PaletteColors[r * PALETTE_MAX_SIZE + c];

        CalcIndices(data, centroids, colorMap, rows * cols, pmi.PaletteSize1, 2);
        ExtendPaletteColorMap(colorMap, cols, rows, planeBlockWidth, planeBlockHeight);
    }

    // ---- k_means_template.h + av1_k_means_avx2.c ---------------------------------------------------------------------
    // libaom's RTCD picks av1_calc_indices_dim{1,2}_avx2 on an AVX2 machine. They match the C kernels on the points
    // they are asked about, but they work in blocks of 16 points: they read (and index) up to 15 points PAST n, write
    // those indices past n, and add those points' distances to total_dist, which av1_k_means uses to stop. The
    // arithmetic is 16-bit (dim 1: _mm256_sub_epi16 / abs_epi16 / min_epi16; the squared distances pair-summed by
    // madd_epi16 into int32) or int32 (dim 2: madd_epi16 of the 16-bit differences), each partial zero-extended into
    // the 64-bit sum. The emulation below keeps all of it; the stale points are whatever the caller's buffer
    // (x->palette_buffer->kmeans_data_buf) holds there.

    /// <summary>_mm256_abs_epi16 on one lane (abs(-32768) stays -32768).</summary>
    private static short Abs16(short v) => v < 0 ? unchecked((short)-v) : v;

    /// <summary>av1_calc_indices_dim1 (the AVX2 kernel). Returns total_dist (0 when !wantDist).</summary>
    internal static long CalcIndicesDim1(ReadOnlySpan<short> data, ReadOnlySpan<short> centroids, Span<byte> indices, bool wantDist, int n, int k)
    {
        ulong sum = 0;
        Span<short> distMin = stackalloc short[16];
        for (int i = 0; i < n; i += 16)
        {
            for (int e = 0; e < 16; e++)
            {
                short v = data[i + e];
                short dmin = Abs16(unchecked((short)(v - centroids[0])));
                int ind = 0;
                for (int j = 1; j < k; ++j)
                {
                    short dist = Abs16(unchecked((short)(v - centroids[j])));
                    if (dmin > dist) ind = j;               // _mm256_cmpgt_epi16(dist_min, dist)
                    if (dist < dmin) dmin = dist;           // _mm256_min_epi16
                }
                indices[i + e] = (byte)ind;                 // _mm256_packus_epi16 (ind < 8)
                distMin[e] = dmin;
            }
            if (wantDist)
                for (int e = 0; e < 16; e += 2)   // _mm256_madd_epi16(dist_min, dist_min), zero-extended to 64 bits
                    sum += unchecked((uint)(distMin[e] * distMin[e] + distMin[e + 1] * distMin[e + 1]));
        }
        return wantDist ? unchecked((long)sum) : 0;
    }

    /// <summary>av1_calc_indices_dim2 (the AVX2 kernel): data / centroids interleaved (u, v). Returns total_dist.</summary>
    internal static long CalcIndicesDim2(ReadOnlySpan<short> data, ReadOnlySpan<short> centroids, Span<byte> indices, bool wantDist, int n, int k)
    {
        ulong sum = 0;
        for (int i = 0; i < n; i += 16)
        {
            for (int e = 0; e < 16; e++)
            {
                short px = data[2 * (i + e)], py = data[2 * (i + e) + 1];
                short dx = unchecked((short)(px - centroids[0])), dy = unchecked((short)(py - centroids[1]));
                int dmin = unchecked(dx * dx + dy * dy);   // _mm256_madd_epi16(d1, d1)
                int ind = 0;
                for (int j = 1; j < k; ++j)
                {
                    dx = unchecked((short)(px - centroids[2 * j]));
                    dy = unchecked((short)(py - centroids[2 * j + 1]));
                    int dist = unchecked(dx * dx + dy * dy);
                    if (dmin > dist) ind = j;               // _mm256_cmpgt_epi32
                    if (dist < dmin) dmin = dist;           // _mm256_min_epi32
                }
                indices[i + e] = (byte)ind;                 // packus_epi32 / packus_epi16 / permutevar8x32: in order
                if (wantDist) sum += unchecked((uint)dmin);
            }
        }
        return wantDist ? unchecked((long)sum) : 0;
    }

    /// <summary>av1_calc_indices (palette.h).</summary>
    internal static void CalcIndices(ReadOnlySpan<short> data, ReadOnlySpan<short> centroids, Span<byte> indices, int n, int k, int dim)
    {
        if (dim == 1) CalcIndicesDim1(data, centroids, indices, false, n, k);
        else if (dim == 2) CalcIndicesDim2(data, centroids, indices, false, n, k);
        else throw new ArgumentOutOfRangeException(nameof(dim), "Untemplated k means dimension");
    }

    /// <summary>lcg_rand16 (av1/encoder/random.h).</summary>
    private static uint LcgRand16(ref uint state)
    {
        state = unchecked((uint)(state * 1103515245UL + 12345));
        return state / 65536 % 32768;
    }

    /// <summary>calc_centroids: the mean of each cluster (rounded); an empty cluster takes a pseudo-random point.</summary>
    internal static void CalcCentroids(ReadOnlySpan<short> data, Span<short> centroids, ReadOnlySpan<byte> indices, int n, int k, int dim)
    {
        Span<int> count = stackalloc int[PALETTE_MAX_SIZE];
        count.Clear();
        Span<int> centroidsSum = stackalloc int[2 * PALETTE_MAX_SIZE];
        uint randState = unchecked((uint)data[0]);
        centroidsSum.Slice(0, k * dim).Clear();

        for (int i = 0; i < n; ++i)
        {
            int index = indices[i];
            ++count[index];
            for (int j = 0; j < dim; ++j) centroidsSum[index * dim + j] += data[i * dim + j];
        }
        for (int i = 0; i < k; ++i)
        {
            if (count[i] == 0)
            {
                int src = (int)(LcgRand16(ref randState) % (uint)n) * dim;
                data.Slice(src, dim).CopyTo(centroids.Slice(i * dim, dim));
            }
            else
            {
                for (int j = 0; j < dim; ++j)
                    centroids[i * dim + j] = unchecked((short)((centroidsSum[i * dim + j] + (count[i] >> 1)) / count[i]));   // DIVIDE_AND_ROUND
            }
        }
    }

    [ThreadStatic] private static byte[]? t_indicesTmp;

    /// <summary>av1_k_means_dim{1,2}: Lloyd iterations from the given centroids until they stop moving, the distance
    /// grows (keeping the previous solution) or max_itr.</summary>
    internal static void KMeans(ReadOnlySpan<short> data, Span<short> centroids, Span<byte> indices, int n, int k, int dim, int maxItr)
    {
        if (dim != 1 && dim != 2) throw new ArgumentOutOfRangeException(nameof(dim), "Untemplated k means dimension");
        Span<short> centroidsTmp = stackalloc short[2 * PALETTE_MAX_SIZE];
        // uint8_t indices_tmp[MAX_PALETTE_BLOCK_WIDTH * MAX_PALETTE_BLOCK_HEIGHT] (stack: its content past n is never read)
        Span<byte> indicesTmp = t_indicesTmp ??= new byte[MAX_PALETTE_BLOCK_WIDTH * MAX_PALETTE_BLOCK_HEIGHT];
        int i, l = 0, prevL, bestL = 0;
        long thisDist = dim == 1 ? CalcIndicesDim1(data, centroids, indices, true, n, k) : CalcIndicesDim2(data, centroids, indices, true, n, k);

        for (i = 0; i < maxItr; ++i)
        {
            long prevDist = thisDist;
            prevL = l;
            l = l == 1 ? 0 : 1;

            Span<short> metaCentroidsL = l == 0 ? centroids : centroidsTmp, metaCentroidsPrev = prevL == 0 ? centroids : centroidsTmp;
            Span<byte> metaIndicesL = l == 0 ? indices : indicesTmp, metaIndicesPrev = prevL == 0 ? indices : indicesTmp;
            CalcCentroids(data, metaCentroidsL, metaIndicesPrev, n, k, dim);
            if (metaCentroidsL.Slice(0, k * dim).SequenceEqual(metaCentroidsPrev.Slice(0, k * dim))) break;
            thisDist = dim == 1 ? CalcIndicesDim1(data, metaCentroidsL, metaIndicesL, true, n, k) : CalcIndicesDim2(data, metaCentroidsL, metaIndicesL, true, n, k);
            if (thisDist > prevDist)
            {
                bestL = prevL;
                break;
            }
        }
        if (i == maxItr) bestL = l;
        if (bestL != 0)
        {
            centroidsTmp.Slice(0, k * dim).CopyTo(centroids);
            indicesTmp.Slice(0, n).CopyTo(indices);
        }
    }

    // ---- intra_mode_search.c: colour counting ------------------------------------------------------------------------

    /// <summary>av1_count_colors (8-bit): histogram into valCount[256]; returns the number of distinct values.</summary>
    internal static int CountColors(byte[] src, int srcOffset, int stride, int rows, int cols, Span<int> valCount)
    {
        const int maxPixVal = 1 << 8;
        valCount.Slice(0, maxPixVal).Clear();
        for (int r = 0; r < rows; ++r)
            for (int c = 0; c < cols; ++c)
                ++valCount[src[srcOffset + r * stride + c]];
        int n = 0;
        for (int i = 0; i < maxPixVal; ++i)
            if (valCount[i] != 0) ++n;
        return n;
    }

    /// <summary>av1_count_colors_with_threshold: false (with the running count) as soon as more than the threshold.</summary>
    internal static bool CountColorsWithThreshold(byte[] src, int srcOffset, int stride, int rows, int cols, int numColorsThreshold, out int numColors)
    {
        Span<bool> hasColor = stackalloc bool[1 << 8];
        hasColor.Clear();
        numColors = 0;
        for (int r = 0; r < rows; ++r)
        {
            for (int c = 0; c < cols; ++c)
            {
                int thisVal = src[srcOffset + r * stride + c];
                if (!hasColor[thisVal])
                {
                    hasColor[thisVal] = true;
                    numColors++;
                    if (numColors > numColorsThreshold) return false;
                }
            }
        }
        return true;
    }

    // ---- pred_common.c / entropymode.c / tokenize.c --------------------------------------------------------------

    /// <summary>av1_get_palette_cache: the merged, sorted, de-duplicated palette colors of the above (not across a
    /// 64-pixel row boundary) and left blocks.</summary>
    internal static int GetPaletteCache(AomMacroblockD xd, int plane, Span<ushort> cache)
    {
        int row = -xd.MbToTopEdge >> 3;
        // Do not refer to above SB row when on SB boundary.
        AomMbModeInfo? aboveMi = (row % (1 << MIN_SB_SIZE_LOG2)) != 0 ? xd.AboveMbmi : null;
        AomMbModeInfo? leftMi = xd.LeftMbmi;
        int aboveN = 0, leftN = 0;
        if (aboveMi != null) aboveN = plane != 0 ? aboveMi.Palette.PaletteSize1 : aboveMi.Palette.PaletteSize0;
        if (leftMi != null) leftN = plane != 0 ? leftMi.Palette.PaletteSize1 : leftMi.Palette.PaletteSize0;
        if (aboveN == 0 && leftN == 0) return 0;
        int aboveIdx = plane * PALETTE_MAX_SIZE;
        int leftIdx = plane * PALETTE_MAX_SIZE;
        int n = 0;
        ushort[]? aboveColors = aboveMi?.Palette.PaletteColors;
        ushort[]? leftColors = leftMi?.Palette.PaletteColors;
        // Merge the sorted lists of base colors from above and left to get combined sorted color cache.
        while (aboveN > 0 && leftN > 0)
        {
            ushort vAbove = aboveColors![aboveIdx];
            ushort vLeft = leftColors![leftIdx];
            if (vLeft < vAbove)
            {
                PaletteAddToCache(cache, ref n, vLeft);
                ++leftIdx; --leftN;
            }
            else
            {
                PaletteAddToCache(cache, ref n, vAbove);
                ++aboveIdx; --aboveN;
                if (vLeft == vAbove) { ++leftIdx; --leftN; }
            }
        }
        while (aboveN-- > 0) PaletteAddToCache(cache, ref n, aboveColors![aboveIdx++]);
        while (leftN-- > 0) PaletteAddToCache(cache, ref n, leftColors![leftIdx++]);
        return n;
    }

    /// <summary>palette_add_to_cache.</summary>
    private static void PaletteAddToCache(Span<ushort> cache, ref int n, ushort val)
    {
        // Do not add an already existing value
        if (n > 0 && val == cache[n - 1]) return;
        cache[n++] = val;
    }

    // av1_palette_color_index_context_lookup (negative values are invalid)
    internal static readonly int[] PaletteColorIndexContextLookup = { -1, -1, 0, -1, -1, 4, 3, 2, 1 };

    /// <summary>av1_get_palette_color_index_context (the decoder's / bitstream's version, with the color order).</summary>
    internal static int GetPaletteColorIndexContext(ReadOnlySpan<byte> colorMap, int stride, int r, int c, int paletteSize,
        Span<byte> colorOrder, out int colorIdx)
    {
        Span<int> colorNeighbors = stackalloc int[NUM_PALETTE_NEIGHBORS];
        colorNeighbors[0] = c - 1 >= 0 ? colorMap[r * stride + c - 1] : -1;
        colorNeighbors[1] = c - 1 >= 0 && r - 1 >= 0 ? colorMap[(r - 1) * stride + c - 1] : -1;
        colorNeighbors[2] = r - 1 >= 0 ? colorMap[(r - 1) * stride + c] : -1;

        Span<int> scores = stackalloc int[PALETTE_MAX_SIZE + 10];
        scores.Clear();
        ReadOnlySpan<int> weights = stackalloc int[] { 2, 1, 2 };
        int i;
        for (i = 0; i < NUM_PALETTE_NEIGHBORS; ++i)
            if (colorNeighbors[i] >= 0) scores[colorNeighbors[i]] += weights[i];

        Span<int> inverseColorOrder = stackalloc int[PALETTE_MAX_SIZE];
        for (i = 0; i < PALETTE_MAX_SIZE; ++i)
        {
            colorOrder[i] = (byte)i;
            inverseColorOrder[i] = i;
        }

        // Get the top NUM_PALETTE_NEIGHBORS scores (sorted from large to small).
        for (i = 0; i < NUM_PALETTE_NEIGHBORS; ++i)
        {
            int max = scores[i];
            int maxIdx = i;
            for (int j = i + 1; j < paletteSize; ++j)
            {
                if (scores[j] > max)
                {
                    max = scores[j];
                    maxIdx = j;
                }
            }
            if (maxIdx != i)
            {
                // Move the score at index 'max_idx' to index 'i', and shift the scores from 'i' to 'max_idx - 1' by 1.
                int maxScore = scores[maxIdx];
                byte maxColorOrder = colorOrder[maxIdx];
                for (int k = maxIdx; k > i; --k)
                {
                    scores[k] = scores[k - 1];
                    colorOrder[k] = colorOrder[k - 1];
                    inverseColorOrder[colorOrder[k]] = k;
                }
                scores[i] = maxScore;
                colorOrder[i] = maxColorOrder;
                inverseColorOrder[colorOrder[i]] = i;
            }
        }

        colorIdx = inverseColorOrder[colorMap[r * stride + c]];

        // Get hash value of context.
        int colorIndexCtxHash = 0;
        ReadOnlySpan<int> hashMultipliers = stackalloc int[] { 1, 2, 2 };
        for (i = 0; i < NUM_PALETTE_NEIGHBORS; ++i) colorIndexCtxHash += scores[i] * hashMultipliers[i];
        return PaletteColorIndexContextLookup[colorIndexCtxHash];
    }

    /// <summary>av1_fast_palette_color_index_context_on_edge (tokenize.c).</summary>
    private static int FastPaletteColorIndexContextOnEdge(ReadOnlySpan<byte> colorMap, int stride, int r, int c, out int colorIdx)
    {
        bool hasAbove = r - 1 >= 0;
        byte colorNeighbor = hasAbove ? colorMap[(r - 1) * stride + c] : colorMap[r * stride + (c - 1)];
        // If the neighbor color has higher index than current color index, then we move up by 1.
        byte currentColor = colorMap[r * stride + c];
        colorIdx = currentColor;
        if (colorNeighbor > currentColor) colorIdx++;
        else if (colorNeighbor == currentColor) colorIdx = 0;
        // The non-diagonal neighbors get a weight of 2: hash 2 -> context 0.
        return 0;
    }

    /// <summary>av1_fast_palette_color_index_context (tokenize.c): the encoder's context + remapped index.</summary>
    internal static int FastPaletteColorIndexContext(ReadOnlySpan<byte> colorMap, int stride, int r, int c, out int colorIdx)
    {
        bool hasAbove = r - 1 >= 0;
        bool hasLeft = c - 1 >= 0;
        if (hasAbove ^ hasLeft) return FastPaletteColorIndexContextOnEdge(colorMap, stride, r, c, out colorIdx);

        // Left, top, top-left: already sorted unless something is duplicated / invalid.
        Span<byte> colorNeighbors = stackalloc byte[NUM_PALETTE_NEIGHBORS];
        colorNeighbors[0] = colorMap[r * stride + (c - 1)];
        colorNeighbors[1] = colorMap[(r - 1) * stride + c];
        colorNeighbors[2] = colorMap[(r - 1) * stride + (c - 1)];

        // Aggregate duplicated values.
        Span<byte> scores = stackalloc byte[] { 2, 2, 1 };
        int numInvalidColors = 0;
        const byte INVALID_COLOR_IDX = byte.MaxValue;
        if (colorNeighbors[0] == colorNeighbors[1])
        {
            scores[0] += scores[1];
            colorNeighbors[1] = INVALID_COLOR_IDX;
            numInvalidColors += 1;
            if (colorNeighbors[0] == colorNeighbors[2])
            {
                scores[0] += scores[2];
                numInvalidColors += 1;
            }
        }
        else if (colorNeighbors[0] == colorNeighbors[2])
        {
            scores[0] += scores[2];
            numInvalidColors += 1;
        }
        else if (colorNeighbors[1] == colorNeighbors[2])
        {
            scores[1] += scores[2];
            numInvalidColors += 1;
        }

        int numValidColors = NUM_PALETTE_NEIGHBORS - numInvalidColors;
        Span<byte> colorRank = colorNeighbors;
        Span<byte> scoreRank = scores;

        // Sort everything
        if (numValidColors > 1)
        {
            if (colorNeighbors[1] == INVALID_COLOR_IDX)
            {
                scores[1] = scores[2];
                colorNeighbors[1] = colorNeighbors[2];
            }
            // Swap the first two if they have the same score but the color indices are not in the right order
            if (scoreRank[0] < scoreRank[1] || (scoreRank[0] == scoreRank[1] && colorRank[0] > colorRank[1])) Swap(scoreRank, colorRank, 0, 1);
            if (numValidColors > 2)
            {
                if (scoreRank[0] < scoreRank[2]) Swap(scoreRank, colorRank, 0, 2);
                if (scoreRank[1] < scoreRank[2]) Swap(scoreRank, colorRank, 1, 2);
            }
        }

        // If any of the neighbor colors has higher index than current color index, then we move up by 1 unless the
        // current color is the same as one of the neighbors.
        byte currentColor = colorMap[r * stride + c];
        colorIdx = currentColor;
        for (int idx = 0; idx < numValidColors; idx++)
        {
            if (colorRank[idx] > currentColor) colorIdx++;
            else if (colorRank[idx] == currentColor)
            {
                colorIdx = idx;
                break;
            }
        }

        // Get hash value of context.
        byte colorIndexCtxHash = 0;
        ReadOnlySpan<byte> hashMultipliers = stackalloc byte[] { 1, 2, 2 };
        for (int idx = 0; idx < numValidColors; ++idx) colorIndexCtxHash += (byte)(scoreRank[idx] * hashMultipliers[idx]);
        return 9 - colorIndexCtxHash;
    }

    private static void Swap(Span<byte> scoreRank, Span<byte> colorRank, int i, int j)
    {
        (scoreRank[i], scoreRank[j]) = (scoreRank[j], scoreRank[i]);
        (colorRank[i], colorRank[j]) = (colorRank[j], colorRank[i]);
    }

    /// <summary>av1_cost_color_map (PALETTE_MAP): cost_and_tokenize_map's rate over the wavefront order.</summary>
    internal static int CostColorMap(AomMacroblock x, int plane, int bsize, int txSize, int type)
    {
        if (type != PALETTE_MAP) throw new ArgumentOutOfRangeException(nameof(type), "Invalid color map type");
        // get_palette_params
        var xd = x.E;
        var mbmi = xd.Mi0;
        byte[] colorMap = xd.Plane[plane].ColorIndexMap;
        int[] colorCost = plane != 0 ? x.ModeCosts.PaletteUvColorCost : x.ModeCosts.PaletteYColorCost;
        int n = plane != 0 ? mbmi.Palette.PaletteSize1 : mbmi.Palette.PaletteSize0;
        AomRdoptUtils.GetBlockDimensions(bsize, plane, xd, out int planeBlockWidth, out _, out int rows, out int cols);
        return CostAndTokenizeMapRate(colorMap, colorCost, planeBlockWidth, rows, cols, n);
    }

    /// <summary>cost_and_tokenize_map (calc_rate).</summary>
    internal static int CostAndTokenizeMapRate(byte[] colorMap, int[] colorCost, int planeBlockWidth, int rows, int cols, int n)
    {
        int paletteSizeIdx = n - PALETTE_MIN_SIZE;
        int thisRate = 0;
        for (int k = 1; k < rows + cols - 1; ++k)
        {
            for (int j = Math.Min(k, cols - 1); j >= Math.Max(0, k - rows + 1); --j)
            {
                int i = k - j;
                int colorCtx = FastPaletteColorIndexContext(colorMap, planeBlockWidth, i, j, out int colorNewIdx);
                thisRate += colorCost[(paletteSizeIdx * PALETTE_COLOR_INDEX_CONTEXTS + colorCtx) * AomModeCosts.PaletteColors + colorNewIdx];
            }
        }
        return thisRate;
    }
}

// The palette parts of intra_mode_info_cost_y / intra_mode_info_cost_uv (av1/encoder/intra_mode_search_utils.h) for
// a block that uses palette: size cost + write_uniform_cost(first index) + palette color cost (+ the color map cost).
internal static class AomPaletteCost
{
    /// <summary>write_uniform_cost (intra_mode_search_utils.h).</summary>
    internal static int WriteUniformCost(int n, int v)
    {
        int l = n > 0 ? 32 - BitOperations.LeadingZeroCount((uint)n) : 0;   // get_unsigned_bits
        int m = (1 << l) - n;
        if (l == 0) return 0;
        return v < m ? AomCost.CostLiteral(l - 1) : AomCost.CostLiteral(l);
    }

    /// <summary>The use_palette branch of intra_mode_info_cost_y (the palette_y_mode_cost flag is the caller's).</summary>
    internal static int PaletteModeCostY(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize, bool discountColorCost)
    {
        var xd = x.E;
        var mc = x.ModeCosts;
        byte[] colorMap = xd.Plane[0].ColorIndexMap;
        int bsizeCtx = NumPelsLog2Lookup[bsize] - NumPelsLog2Lookup[BLOCK_8X8];   // av1_get_palette_bsize_ctx
        int pltSize = mbmi.Palette.PaletteSize0;
        int paletteModeCost = mc.PaletteYSizeCost[bsizeCtx * AomModeCosts.PaletteSizes + pltSize - AomPalette.PALETTE_MIN_SIZE] +
                              WriteUniformCost(pltSize, colorMap[0]);
        Span<ushort> colorCache = stackalloc ushort[2 * AomPalette.PALETTE_MAX_SIZE];
        int nCache = AomPalette.GetPaletteCache(xd, 0, colorCache);
        paletteModeCost += AomPalette.PaletteColorCostY(mbmi.Palette, colorCache, nCache, cpi.BitDepth);
        if (!discountColorCost) paletteModeCost += AomPalette.CostColorMap(x, 0, bsize, mbmi.TxSize, PALETTE_MAP);
        return paletteModeCost;
    }

    /// <summary>The use_palette branch of intra_mode_info_cost_uv (the palette_uv_mode_cost flag is the caller's).</summary>
    internal static int PaletteModeCostUv(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize)
    {
        var xd = x.E;
        var mc = x.ModeCosts;
        byte[] colorMap = xd.Plane[1].ColorIndexMap;
        int bsizeCtx = NumPelsLog2Lookup[bsize] - NumPelsLog2Lookup[BLOCK_8X8];
        int pltSize = mbmi.Palette.PaletteSize1;
        int paletteModeCost = mc.PaletteUvSizeCost[bsizeCtx * AomModeCosts.PaletteSizes + pltSize - AomPalette.PALETTE_MIN_SIZE] +
                              WriteUniformCost(pltSize, colorMap[0]);
        Span<ushort> colorCache = stackalloc ushort[2 * AomPalette.PALETTE_MAX_SIZE];
        int nCache = AomPalette.GetPaletteCache(xd, 1, colorCache);
        paletteModeCost += AomPalette.PaletteColorCostUv(mbmi.Palette, colorCache, nCache, cpi.BitDepth);
        paletteModeCost += AomPalette.CostColorMap(x, 1, bsize, mbmi.TxSize, PALETTE_MAP);
        return paletteModeCost;
    }
}
