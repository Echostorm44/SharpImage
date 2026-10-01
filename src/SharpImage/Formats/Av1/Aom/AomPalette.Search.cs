using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/palette.c: palette_rd_y, the dominant-color and k-means palette size searches and
// the two entry points av1_rd_pick_palette_intra_sby / av1_rd_pick_palette_intra_sbuv (8-bit). Staged with
// AomTxSearch / AomIntraModeSearch (it calls their tx search and mode costs).
internal static partial class AomPalette
{
    // Test seam: AomPaletteTwinTests replaces the tx search with a mock shared with its C harness, so the search's
    // control flow, mode costs and outputs can be twinned against libaom's palette.c without a full tx search. Both
    // null in the encoder (the real av1_pick_uniform_tx_size_type_yrd / av1_txfm_uvrd).
    internal delegate void PickUniformTxSizeTypeYrdFn(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd);
    internal delegate bool TxfmUvrdFn(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd);
    internal static PickUniformTxSizeTypeYrdFn? TestPickUniformTxSizeTypeYrd;
    internal static TxfmUvrdFn? TestTxfmUvrd;

    private static void PickUniformTxSizeTypeYrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd)
    {
        if (TestPickUniformTxSizeTypeYrd != null) TestPickUniformTxSizeTypeYrd(cpi, x, ref rdStats, bsize, refBestRd);
        else AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref rdStats, bsize, refBestRd);
    }

    private static bool TxfmUvrd(AomComp cpi, AomMacroblock x, ref AomRdStats rdStats, int bsize, long refBestRd)
        => TestTxfmUvrd != null ? TestTxfmUvrd(cpi, x, ref rdStats, bsize, refBestRd) : AomTxSearch.TxfmUvrd(cpi, x, ref rdStats, bsize, refBestRd);

    /// <summary>palette_rd_y: the RD cost of luma palette mode with the given base colors.</summary>
    private static void PaletteRdY(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize, int dcModeCost, short[] data, Span<short> centroids,
        int n, ReadOnlySpan<ushort> colorCache, int nCache, bool doHeaderRdBasedGating, AomMbModeInfo bestMbmi, byte[] bestPaletteColorMap,
        ref long bestRd, ref int rate, ref int rateTokenonly, ref long distortion, ref byte skippable, ref bool beatBestRd, AomPickModeContext ctx,
        byte[] txTypeMap, ref bool beatBestPaletteRd, ref bool doHeaderRdBasedBreakout, bool discountColorCost)
    {
        doHeaderRdBasedBreakout = false;
        OptimizePaletteColors(colorCache, nCache, n, 1, centroids, cpi.BitDepth);
        int numUniqueColors = RemoveDuplicates(centroids, n);
        if (numUniqueColors < PALETTE_MIN_SIZE)
        {
            // Too few unique colors to create a palette. And DC_PRED will work well for that case anyway. So skip.
            return;
        }
        ref AomPaletteModeInfo pmi = ref mbmi.Palette;
        int maxPix = (1 << cpi.BitDepth) - 1;   // clip_pixel_highbd / clip_pixel
        for (int i = 0; i < numUniqueColors; ++i) pmi.PaletteColors[i] = (ushort)Math.Clamp((int)centroids[i], 0, maxPix);
        pmi.PaletteSize0 = (byte)numUniqueColors;
        var xd = x.E;
        byte[] colorMap = xd.Plane[0].ColorIndexMap;
        AomRdoptUtils.GetBlockDimensions(bsize, 0, xd, out int blockWidth, out int blockHeight, out int rows, out int cols);
        CalcIndices(data, centroids, colorMap, rows * cols, numUniqueColors, 1);
        ExtendPaletteColorMap(colorMap, cols, rows, blockWidth, blockHeight);

        AomRdStats tokenonlyRdStats = default;
        int thisRate;

        if (doHeaderRdBasedGating)
        {
            int paletteModeRate = AomIntraModeSearch.IntraModeInfoCostY(cpi, x, mbmi, bsize, dcModeCost, discountColorCost);
            long headerRd = AomRd.RdCost(x.Rdmult, paletteModeRate, 0);
            // Less aggressive pruning when prune_luma_palette_size_search_level == 1.
            int headerRdShift = cpi.Sf.intra_sf.prune_luma_palette_size_search_level == 1 ? 1 : 0;
            // Terminate further palette_size search, if the header cost corresponding to lower palette_size is more
            // than *best_rd << header_rd_shift (a right shift on the left side to avoid overflow).
            if ((headerRd >> headerRdShift) > bestRd)
            {
                doHeaderRdBasedBreakout = true;
                return;
            }
            PickUniformTxSizeTypeYrd(cpi, x, ref tokenonlyRdStats, bsize, bestRd);
            if (tokenonlyRdStats.Rate == int.MaxValue) return;
            thisRate = tokenonlyRdStats.Rate + paletteModeRate;
        }
        else
        {
            PickUniformTxSizeTypeYrd(cpi, x, ref tokenonlyRdStats, bsize, bestRd);
            if (tokenonlyRdStats.Rate == int.MaxValue) return;
            thisRate = tokenonlyRdStats.Rate + AomIntraModeSearch.IntraModeInfoCostY(cpi, x, mbmi, bsize, dcModeCost, discountColorCost);
        }

        long thisRd = AomRd.RdCost(x.Rdmult, thisRate, tokenonlyRdStats.Dist);
        if (xd.Lossless[mbmi.SegmentId] == 0 && AomTxSearch.BlockSignalsTxsize(mbmi.Bsize))
            tokenonlyRdStats.Rate -= AomTxSearch.TxSizeCost(x, bsize, mbmi.TxSize);
        // Collect mode stats for multiwinner mode processing (store_winner_mode_stats with THR_DC, txfm_search_done)
        AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, colorMap, bsize, thisRd, cpi.Sf.winner_mode_sf.multi_winner_mode_type);
        if (thisRd < bestRd)
        {
            bestRd = thisRd;
            // Setting beat_best_rd flag because current mode rd is better than best_rd.
            beatBestRd = true;
            Array.Copy(colorMap, bestPaletteColorMap, blockWidth * blockHeight);
            bestMbmi.CopyFrom(mbmi);
            xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(txTypeMap);
            rate = thisRate;
            rateTokenonly = tokenonlyRdStats.Rate;
            distortion = tokenonlyRdStats.Dist;
            skippable = tokenonlyRdStats.SkipTxfm;
            beatBestPaletteRd = true;
        }
    }

    /// <summary>is_iter_over.</summary>
    private static bool IsIterOver(int currIdx, int endIdx, int stepSize) => stepSize > 0 ? currIdx >= endIdx : currIdx <= endIdx;

    /// <summary>perform_top_color_palette_search: the dominant colors, palette sizes start_n, start_n + step, ...
    /// (before end_n). Returns the best size found (end_n if none beat the best rd).</summary>
    private static int PerformTopColorPaletteSearch(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize, int dcModeCost, short[] data,
        ReadOnlySpan<short> topColors, int startN, int endN, int stepSize, bool doHeaderRdBasedGating, ref int lastNSearched,
        ReadOnlySpan<ushort> colorCache, int nCache, AomMbModeInfo bestMbmi, byte[] bestPaletteColorMap, ref long bestRd, ref int rate,
        ref int rateTokenonly, ref long distortion, ref byte skippable, ref bool beatBestRd, AomPickModeContext ctx, byte[] txTypeMap,
        bool discountColorCost)
    {
        Span<short> centroids = stackalloc short[PALETTE_MAX_SIZE];
        int n = startN;
        int topColorWinner = endN;
        while (!IsIterOver(n, endN, stepSize))
        {
            bool beatBestPaletteRd = false;
            bool doHeaderRdBasedBreakout = false;
            topColors.Slice(0, n).CopyTo(centroids);
            PaletteRdY(cpi, x, mbmi, bsize, dcModeCost, data, centroids, n, colorCache, nCache, doHeaderRdBasedGating, bestMbmi,
                bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap,
                ref beatBestPaletteRd, ref doHeaderRdBasedBreakout, discountColorCost);
            lastNSearched = n;
            if (doHeaderRdBasedBreakout)
            {
                // Terminate palette_size search by setting last_n_searched to end_n.
                lastNSearched = endN;
                break;
            }
            if (beatBestPaletteRd) topColorWinner = n;
            else if (cpi.Sf.intra_sf.prune_palette_search_level == 2) return topColorWinner;   // no improvement: stop at level 2
            n += stepSize;
        }
        return topColorWinner;
    }

    /// <summary>perform_k_means_palette_search: k-means from evenly spread centroids, palette sizes start_n, start_n +
    /// step, ... (before end_n). Returns the best size found (end_n if none beat the best rd).</summary>
    private static int PerformKMeansPaletteSearch(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize, int dcModeCost, short[] data,
        int lowerBound, int upperBound, int startN, int endN, int stepSize, bool doHeaderRdBasedGating, ref int lastNSearched,
        ReadOnlySpan<ushort> colorCache, int nCache, AomMbModeInfo bestMbmi, byte[] bestPaletteColorMap, ref long bestRd, ref int rate,
        ref int rateTokenonly, ref long distortion, ref byte skippable, ref bool beatBestRd, AomPickModeContext ctx, byte[] txTypeMap,
        byte[] colorMap, int dataPoints, bool discountColorCost)
    {
        Span<short> centroids = stackalloc short[PALETTE_MAX_SIZE];
        const int maxItr = 50;
        int n = startN;
        int topColorWinner = endN;
        while (!IsIterOver(n, endN, stepSize))
        {
            bool beatBestPaletteRd = false;
            bool doHeaderRdBasedBreakout = false;
            for (int i = 0; i < n; ++i) centroids[i] = (short)(lowerBound + (2 * i + 1) * (upperBound - lowerBound) / n / 2);
            KMeans(data, centroids, colorMap, dataPoints, n, 1, maxItr);
            PaletteRdY(cpi, x, mbmi, bsize, dcModeCost, data, centroids, n, colorCache, nCache, doHeaderRdBasedGating, bestMbmi,
                bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap,
                ref beatBestPaletteRd, ref doHeaderRdBasedBreakout, discountColorCost);
            lastNSearched = n;
            if (doHeaderRdBasedBreakout)
            {
                lastNSearched = endN;
                break;
            }
            if (beatBestPaletteRd) topColorWinner = n;
            else if (cpi.Sf.intra_sf.prune_palette_search_level == 2) return topColorWinner;
            n += stepSize;
        }
        return topColorWinner;
    }

    // Start index / step size of the coarse palette size search (prune_palette_search_level 1), by number of colors
    private static readonly byte[] StartNLookupTable = { 0, 0, 0, 3, 3, 2, 3, 3, 2 };
    private static readonly byte[] StepSizeLookupTable = { 0, 0, 0, 3, 3, 3, 3, 3, 3 };

    [ThreadStatic] private static int[]? t_countBuf;

    /// <summary>av1_rd_pick_palette_intra_sby: the best luma palette (dominant colors and k-means, over the palette
    /// sizes the speed features allow); updates best_mbmi / best_rd / the rates when it beats best_rd.</summary>
    internal static void RdPickPaletteIntraSby(AomComp cpi, AomMacroblock x, int bsize, int dcModeCost, AomMbModeInfo bestMbmi,
        byte[] bestPaletteColorMap, ref long bestRd, ref int rate, ref int rateTokenonly, ref long distortion, ref byte skippable,
        ref bool beatBestRd, AomPickModeContext ctx, byte[] txTypeMap)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int srcStride = x.Plane[0].Src.Stride;
        byte[] src = x.Plane[0].Src.Buf;
        int srcOff = x.Plane[0].Src.Offset;
        AomRdoptUtils.GetBlockDimensions(bsize, 0, xd, out int blockWidth, out int blockHeight, out int rows, out int cols);
        bool isHbd = cpi.UseHighbitdepth;
        int bitDepth = cpi.BitDepth;
        bool discountColorCost = cpi.Sf.rt_sf.discount_color_cost != 0;
        int unused = 0;

        int[] countBuf = t_countBuf ??= new int[1 << 12];   // Maximum (1 << 12) color levels.
        int colors, colorsThreshold;
        ushort[]? src16 = x.Plane[0].Src.Buf16;
        if (isHbd)
        {
            Span<int> countBuf8 = stackalloc int[1 << 8];   // Maximum (1 << 8) bins for hbd path.
            colors = CountColorsHighbd(src16!, srcOff, srcStride, rows, cols, bitDepth, countBuf, countBuf8, out colorsThreshold);
        }
        else
        {
            colors = CountColors(src, srcOff, srcStride, rows, cols, countBuf);
            colorsThreshold = colors;
        }

        byte[] colorMap = xd.Plane[0].ColorIndexMap;
        int colorThreshPalette = x.ColorPaletteThresh;
        // Allow for larger color_threshold for palette search, based on color, scene_change, and block source
        // variance. Since palette is Y based, only allow larger threshold if block color_dist is below threshold.
        if (cpi.Sf.rt_sf.use_nonrd_pick_mode != 0 && cpi.Sf.rt_sf.increase_color_thresh_palette && cpi.RcHighSourceSad != 0 &&
            x.SourceVariance > 50)
        {
            long normColorDist = 0;
            if (x.ColorSensitivity[0] != 0 || x.ColorSensitivity[1] != 0)
            {
                normColorDist = x.MinDistInterUv >> (MiSizeWideLog2[bsize] + MiSizeHighLog2[bsize]);
                if (x.ColorSensitivity[0] != 0 && x.ColorSensitivity[1] != 0) normColorDist >>= 1;
            }
            if (normColorDist < 8000) colorThreshPalette += 20;
        }
        if (colorsThreshold > 1 && colorsThreshold <= colorThreshPalette)
        {
            short[] data = x.KmeansDataBuf;
            Span<short> centroids = stackalloc short[PALETTE_MAX_SIZE];
            int lowerBound, upperBound;
            if (isHbd) FillDataAndGetBounds(src16!, srcOff, srcStride, rows, cols, data, out lowerBound, out upperBound);
            else FillDataAndGetBounds(src, srcOff, srcStride, rows, cols, isHbd, data, out lowerBound, out upperBound);

            mbmi.Mode = DC_PRED;
            mbmi.UseFilterIntra = 0;

            Span<ushort> colorCache = stackalloc ushort[2 * PALETTE_MAX_SIZE];
            int nCache = GetPaletteCache(xd, 0, colorCache);

            // Find the dominant colors, stored in top_colors[].
            Span<short> topColors = stackalloc short[PALETTE_MAX_SIZE];
            topColors.Clear();
            FindTopColors(countBuf, bitDepth, Math.Min(colors, PALETTE_MAX_SIZE), topColors);

            // Header rdcost based gating for early termination (see palette.c for the per-level details).
            bool doHeaderRdBasedGating = cpi.Sf.intra_sf.prune_luma_palette_size_search_level != 0;

            if (cpi.Sf.intra_sf.prune_palette_search_level == 1 && colors > PALETTE_MIN_SIZE)
            {
                // Coarse search over sizes start_n, start_n + step, ... then the winner's neighbours.
                int maxN = Math.Min(colors, PALETTE_MAX_SIZE);
                int minN = StartNLookupTable[maxN];
                int stepSize = StepSizeLookupTable[maxN];
                // Perform top color coarse palette search to find the winner candidate
                int topColorWinner = PerformTopColorPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, topColors, minN, maxN + 1, stepSize,
                    doHeaderRdBasedGating, ref unused, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly,
                    ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, discountColorCost);
                // Evaluate neighbors for the winner color (if winner is found) in the above coarse search
                if (topColorWinner <= maxN)
                {
                    SetStage2Params(out int stage2MinN, out int stage2MaxN, out int stage2StepSize, topColorWinner, maxN);
                    PerformTopColorPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, topColors, stage2MinN, stage2MaxN + 1, stage2StepSize,
                        false, ref unused, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly,
                        ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, discountColorCost);
                }
                // K-means clustering: coarse search to find the winner candidate
                int kMeansWinner = PerformKMeansPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, lowerBound, upperBound, minN, maxN + 1,
                    stepSize, doHeaderRdBasedGating, ref unused, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate,
                    ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, colorMap, rows * cols, discountColorCost);
                // Evaluate neighbors for the winner color (if winner is found) in the above coarse search for k-means
                if (kMeansWinner <= maxN)
                {
                    SetStage2Params(out int startNStage2, out int endNStage2, out int stepSizeStage2, kMeansWinner, maxN);
                    PerformKMeansPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, lowerBound, upperBound, startNStage2, endNStage2 + 1,
                        stepSizeStage2, false, ref unused, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate,
                        ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, colorMap, rows * cols, discountColorCost);
                }
            }
            else
            {
                int maxN = Math.Min(colors, PALETTE_MAX_SIZE), minN = PALETTE_MIN_SIZE;
                // Perform top color palette search in ascending order
                int lastNSearched = minN;
                PerformTopColorPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, topColors, minN, maxN + 1, 1, doHeaderRdBasedGating,
                    ref lastNSearched, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly,
                    ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, discountColorCost);
                if (lastNSearched < maxN)
                {
                    // Search in descending order until we get to the previous best
                    PerformTopColorPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, topColors, maxN, lastNSearched, -1, false,
                        ref unused, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly,
                        ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, discountColorCost);
                }
                // K-means clustering.
                if (colors == PALETTE_MIN_SIZE)
                {
                    // Special case: These colors automatically become the centroids.
                    centroids[0] = (short)lowerBound;
                    centroids[1] = (short)upperBound;
                    bool unusedBeat = false, unusedBreakout = false;   // palette_rd_y(..., NULL, NULL, ...)
                    PaletteRdY(cpi, x, mbmi, bsize, dcModeCost, data, centroids, colors, colorCache, nCache, false, bestMbmi,
                        bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx,
                        txTypeMap, ref unusedBeat, ref unusedBreakout, discountColorCost);
                }
                else
                {
                    // Perform k-means palette search in ascending order
                    lastNSearched = minN;
                    PerformKMeansPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, lowerBound, upperBound, minN, maxN + 1, 1,
                        doHeaderRdBasedGating, ref lastNSearched, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate,
                        ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, colorMap, rows * cols, discountColorCost);
                    if (lastNSearched < maxN)
                    {
                        // Search in descending order until we get to the previous best
                        PerformKMeansPaletteSearch(cpi, x, mbmi, bsize, dcModeCost, data, lowerBound, upperBound, maxN, lastNSearched, -1,
                            false, ref unused, colorCache, nCache, bestMbmi, bestPaletteColorMap, ref bestRd, ref rate, ref rateTokenonly,
                            ref distortion, ref skippable, ref beatBestRd, ctx, txTypeMap, colorMap, rows * cols, discountColorCost);
                    }
                }
            }
        }

        if (bestMbmi.Palette.PaletteSize0 > 0) Array.Copy(bestPaletteColorMap, colorMap, blockWidth * blockHeight);
        mbmi.CopyFrom(bestMbmi);
    }

    /// <summary>av1_rd_pick_palette_intra_sbuv: the best chroma palette (2-D k-means over (u, v), sizes 2..8);
    /// updates best_mbmi / best_rd / the rates when it beats best_rd.</summary>
    internal static void RdPickPaletteIntraSbuv(AomComp cpi, AomMacroblock x, int dcModeCost, byte[] bestPaletteColorMap, AomMbModeInfo bestMbmi,
        ref long bestRd, ref int rate, ref int rateTokenonly, ref long distortion, ref byte skippable)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        ref AomPaletteModeInfo pmi = ref mbmi.Palette;
        int bsize = mbmi.Bsize;
        int thisRate;
        long thisRd;
        int colorsU, colorsV;
        int colorsThresholdU, colorsThresholdV, colorsThreshold;
        int srcStride = x.Plane[1].Src.Stride;
        byte[] srcU = x.Plane[1].Src.Buf, srcV = x.Plane[2].Src.Buf;
        int srcUOff = x.Plane[1].Src.Offset, srcVOff = x.Plane[2].Src.Offset;
        byte[] colorMap = xd.Plane[1].ColorIndexMap;
        AomRdStats tokenonlyRdStats = default;
        AomRdoptUtils.GetBlockDimensions(bsize, 1, xd, out int planeBlockWidth, out int planeBlockHeight, out int rows, out int cols);

        mbmi.UvMode = UV_DC_PRED;
        ushort[]? srcU16 = x.Plane[1].Src.Buf16, srcV16 = x.Plane[2].Src.Buf16;
        bool hbd = cpi.UseHighbitdepth;
        if (hbd)
        {
            int[] countBufHbd = t_countBuf ??= new int[1 << 12];   // Maximum (1 << 12) color levels.
            Span<int> countBuf8 = stackalloc int[1 << 8];         // Maximum (1 << 8) bins for hbd path.
            colorsU = CountColorsHighbd(srcU16!, srcUOff, srcStride, rows, cols, cpi.BitDepth, countBufHbd, countBuf8, out colorsThresholdU);
            colorsV = CountColorsHighbd(srcV16!, srcVOff, srcStride, rows, cols, cpi.BitDepth, countBufHbd, countBuf8, out colorsThresholdV);
        }
        else
        {
            Span<int> countBuf = stackalloc int[1 << 8];
            colorsU = CountColors(srcU, srcUOff, srcStride, rows, cols, countBuf);
            colorsV = CountColors(srcV, srcVOff, srcStride, rows, cols, countBuf);
            colorsThresholdU = colorsU;
            colorsThresholdV = colorsV;
        }

        Span<ushort> colorCache = stackalloc ushort[2 * PALETTE_MAX_SIZE];
        int nCache = GetPaletteCache(xd, 1, colorCache);

        colorsThreshold = colorsThresholdU > colorsThresholdV ? colorsThresholdU : colorsThresholdV;
        if (colorsThreshold > 1 && colorsThreshold <= 64)
        {
            const int maxItr = 50;
            short[] data = x.KmeansDataBuf;
            Span<short> centroids = stackalloc short[2 * PALETTE_MAX_SIZE];

            int lbU = hbd ? srcU16![srcUOff] : srcU[srcUOff], ubU = lbU;
            int lbV = hbd ? srcV16![srcVOff] : srcV[srcVOff], ubV = lbV;
            for (int r = 0; r < rows; ++r)
            {
                for (int c = 0; c < cols; ++c)
                {
                    int valU = hbd ? srcU16![srcUOff + r * srcStride + c] : srcU[srcUOff + r * srcStride + c];
                    int valV = hbd ? srcV16![srcVOff + r * srcStride + c] : srcV[srcVOff + r * srcStride + c];
                    data[(r * cols + c) * 2] = (short)valU;
                    data[(r * cols + c) * 2 + 1] = (short)valV;
                    if (valU < lbU) lbU = valU;
                    else if (valU > ubU) ubU = valU;
                    if (valV < lbV) lbV = valV;
                    else if (valV > ubV) ubV = valV;
                }
            }

            int colors = colorsU > colorsV ? colorsU : colorsV;
            int maxColors = colors > PALETTE_MAX_SIZE ? PALETTE_MAX_SIZE : colors;
            for (int n = PALETTE_MIN_SIZE; n <= maxColors; ++n)
            {
                for (int i = 0; i < n; ++i)
                {
                    centroids[i * 2] = (short)(lbU + (2 * i + 1) * (ubU - lbU) / n / 2);
                    centroids[i * 2 + 1] = (short)(lbV + (2 * i + 1) * (ubV - lbV) / n / 2);
                }
                KMeans(data, centroids, colorMap, rows * cols, n, 2, maxItr);
                OptimizePaletteColors(colorCache, nCache, n, 2, centroids, cpi.BitDepth);
                // Sort the U channel colors in ascending order.
                for (int i = 0; i < 2 * (n - 1); i += 2)
                {
                    int minIdx = i;
                    int minVal = centroids[i];
                    for (int j = i + 2; j < 2 * n; j += 2)
                        if (centroids[j] < minVal) { minVal = centroids[j]; minIdx = j; }
                    if (minIdx != i)
                    {
                        short tempU = centroids[i], tempV = centroids[i + 1];
                        centroids[i] = centroids[minIdx];
                        centroids[i + 1] = centroids[minIdx + 1];
                        centroids[minIdx] = tempU;
                        centroids[minIdx + 1] = tempV;
                    }
                }
                CalcIndices(data, centroids, colorMap, rows * cols, n, 2);
                ExtendPaletteColorMap(colorMap, cols, rows, planeBlockWidth, planeBlockHeight);
                pmi.PaletteSize1 = (byte)n;
                for (int i = 1; i < 3; ++i)
                    for (int j = 0; j < n; ++j)
                        pmi.PaletteColors[i * PALETTE_MAX_SIZE + j] = (ushort)Math.Clamp((int)centroids[j * 2 + i - 1], 0, (1 << cpi.BitDepth) - 1);

                if (cpi.Sf.intra_sf.early_term_chroma_palette_size_search != 0)
                {
                    int paletteModeRate = AomIntraModeSearch.IntraModeInfoCostUv(cpi, x, mbmi, bsize, dcModeCost);
                    long headerRd = AomRd.RdCost(x.Rdmult, paletteModeRate, 0);
                    // Terminate further palette_size search, if header cost corresponding to lower palette_size is more
                    // than the best_rd.
                    if (headerRd >= bestRd) break;
                    TxfmUvrd(cpi, x, ref tokenonlyRdStats, bsize, bestRd);
                    if (tokenonlyRdStats.Rate == int.MaxValue) continue;
                    thisRate = tokenonlyRdStats.Rate + paletteModeRate;
                }
                else
                {
                    TxfmUvrd(cpi, x, ref tokenonlyRdStats, bsize, bestRd);
                    if (tokenonlyRdStats.Rate == int.MaxValue) continue;
                    thisRate = tokenonlyRdStats.Rate + AomIntraModeSearch.IntraModeInfoCostUv(cpi, x, mbmi, bsize, dcModeCost);
                }

                thisRd = AomRd.RdCost(x.Rdmult, thisRate, tokenonlyRdStats.Dist);
                if (thisRd < bestRd)
                {
                    bestRd = thisRd;
                    bestMbmi.CopyFrom(mbmi);
                    Array.Copy(colorMap, bestPaletteColorMap, planeBlockWidth * planeBlockHeight);
                    rate = thisRate;
                    distortion = tokenonlyRdStats.Dist;
                    rateTokenonly = tokenonlyRdStats.Rate;
                    skippable = tokenonlyRdStats.SkipTxfm;
                }
            }
        }
        if (bestMbmi.Palette.PaletteSize1 > 0) Array.Copy(bestPaletteColorMap, colorMap, planeBlockWidth * planeBlockHeight);
    }
}
