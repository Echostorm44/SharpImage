using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using static SharpImage.Formats.Av1.AomTables;
using SharpImage.Core;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMacroblock
{
    // x->src_var_info_of_4x4_sub_blocks (per SB, -1 = not computed)
    public readonly int[] SrcVar4x4 = new int[32 * 32];
    public readonly double[] SrcLogVar4x4 = new double[32 * 32];
    // x->palette_buffer->best_palette_color_map
    public readonly byte[] BestPaletteColorMap = new byte[128 * 128];

    /// <summary>init_src_var_info_of_4x4_sub_blocks.</summary>
    public void InitSrcVarInfo(int sbSize)
    {
        int n = MiSizeWide[sbSize] * MiSizeHigh[sbSize];
        for (int i = 0; i < n; i++) { SrcVar4x4[i] = -1; SrcLogVar4x4[i] = -1.0; }
    }
}

// Port of libaom 3.14.1 av1/encoder/intra_mode_search.c (+ intra_mode_search_utils.h) for intra frames:
// av1_rd_pick_intra_sby_mode (mode / angle loop, model-rd pruning, the ALLINTRA variance factor, filter intra, winner
// mode processing) and av1_rd_pick_intra_sbuv_mode (chroma modes, angle search, CfL alpha search), plus
// av1_encode_intra_block_plane (encodemb.c) that the CfL search re-runs for the luma reconstruction.
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static class AomIntraModeSearch
{
    private const int MAX_ANGLE_DELTA = 3, INTRA_MODE_END = 13, INTRA_MODE_START = 0;
    private const int LUMA_MODE_COUNT = INTRA_MODE_END + 8 * 2 * MAX_ANGLE_DELTA - 0;   // set below
    private const int SIZE_OF_ANGLE_DELTA_RD_COST_ARRAY = 2 * MAX_ANGLE_DELTA + 3;
    private const int CFL_SIGN_ZERO = 0, CFL_SIGN_NEG = 1, CFL_SIGN_POS = 2, CFL_SIGNS = 3;
    private const int CFL_PRED_U = 0, CFL_PRED_V = 1, CFL_ALPHABET_SIZE_LOG2 = 4;
    private const int CFL_MAGS_SIZE = 2 * 16 + 1, CFL_INDEX_ZERO = 16;
    private const int FILTER_INTRA_MODES = 5;
    private const int DRY_RUN_NORMAL = 1;

    private static readonly sbyte[] LumaDeltaAnglesOrder = { -2, 2, -3, -1, 1, 3 };
    private static readonly int[] IntraRdSearchModeOrder =
    {
        DC_PRED, H_PRED, V_PRED, SMOOTH_PRED, PAETH_PRED, SMOOTH_V_PRED, SMOOTH_H_PRED, D135_PRED, D203_PRED, D157_PRED, D67_PRED, D113_PRED, D45_PRED,
    };
    private static readonly int[] UvRdSearchModeOrder =
    {
        UV_DC_PRED, UV_CFL_PRED, UV_H_PRED, UV_V_PRED, UV_SMOOTH_PRED, UV_PAETH_PRED, UV_SMOOTH_V_PRED, UV_SMOOTH_H_PRED,
        UV_D135_PRED, UV_D203_PRED, UV_D157_PRED, UV_D67_PRED, UV_D113_PRED, UV_D45_PRED,
    };
    private static readonly byte[] DerivedFilterIntraModeUsedFlag = { 0x01, 0x03, 0x05, 0x01, 0x01, 0x01, 0x09, 0x01, 0x01, 0x01, 0x01, 0x01, 0x11 };
    private static readonly ushort[] DerivedChromaIntraModeUsedFlag =
        { 0x2201, 0x2203, 0x2205, 0x2209, 0x2211, 0x2221, 0x2241, 0x2281, 0x2301, 0x2201, 0x2601, 0x2a01, 0x3201 };

    private static bool IsDirectionalMode(int mode) => mode >= V_PRED && mode <= D67_PRED;
    private static bool IsDiagonalMode(int mode) => mode >= D45_PRED && mode <= D67_PRED;
    private static bool UseAngleDelta(int bsize) => bsize >= BLOCK_8X8;
    private static int GetUvMode(int uvMode) => uvMode == UV_CFL_PRED ? DC_PRED : uvMode;

    /// <summary>av1_allow_palette.</summary>
    internal static bool AllowPalette(bool allowScreenContentTools, int bsize)
        => allowScreenContentTools && BlockSizeWide[bsize] <= 64 && BlockSizeHigh[bsize] <= 64 && bsize >= BLOCK_8X8;

    /// <summary>av1_filter_intra_allowed_bsize.</summary>
    internal static bool FilterIntraAllowedBsize(AomComp cpi, int bs)
        => cpi.EnableFilterIntra && bs != 255 && BlockSizeWide[bs] <= 32 && BlockSizeHigh[bs] <= 32;

    /// <summary>av1_filter_intra_allowed.</summary>
    internal static bool FilterIntraAllowed(AomComp cpi, AomMbModeInfo mbmi)
        => mbmi.Mode == DC_PRED && mbmi.Palette.PaletteSize0 == 0 && FilterIntraAllowedBsize(cpi, mbmi.Bsize);

    /// <summary>write_uniform_cost.</summary>
    internal static int WriteUniformCost(int n, int v)
    {
        int l = n > 0 ? 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)n) : 0;
        int m = (1 << l) - n;
        if (l == 0) return 0;
        return v < m ? AomCost.CostLiteral(l - 1) : AomCost.CostLiteral(l);
    }

    /// <summary>intra_mode_info_cost_y.</summary>
    internal static int IntraModeInfoCostY(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize, int modeCost, bool discountColorCost)
    {
        int totalRate = modeCost;
        var mc = x.ModeCosts;
        bool usePalette = mbmi.Palette.PaletteSize0 > 0;
        int useFilterIntra = mbmi.UseFilterIntra;
        int useIntrabc = mbmi.UseIntrabc;
        bool tryPalette = AllowPalette(cpi.AllowScreenContentTools, mbmi.Bsize);
        if (tryPalette && mbmi.Mode == DC_PRED)
        {
            var xd = x.E;
            int bsizeCtx = NumPelsLog2Lookup[bsize] - NumPelsLog2Lookup[BLOCK_8X8];
            int modeCtx = PaletteModeCtx(xd);
            totalRate += mc.PaletteYModeCost[(bsizeCtx * AomModeCosts.PaletteYModeContexts + modeCtx) * 2 + (usePalette ? 1 : 0)];
            if (usePalette) totalRate += AomPaletteCost.PaletteModeCostY(cpi, x, mbmi, bsize, discountColorCost);
        }
        if (FilterIntraAllowed(cpi, mbmi))
        {
            totalRate += mc.FilterIntraCost[mbmi.Bsize * 2 + useFilterIntra];
            if (useFilterIntra != 0) totalRate += mc.FilterIntraModeCost[mbmi.FilterIntraMode];
        }
        if (IsDirectionalMode(mbmi.Mode) && UseAngleDelta(bsize))
            totalRate += mc.AngleDeltaCost[(mbmi.Mode - V_PRED) * (2 * MAX_ANGLE_DELTA + 1) + MAX_ANGLE_DELTA + mbmi.AngleDelta[0]];
        if (cpi.FrameIsIntraOnly && cpi.AllowScreenContentTools && cpi.AllowIntrabc) totalRate += mc.IntrabcCost[useIntrabc];
        return totalRate;
    }

    /// <summary>av1_get_palette_mode_ctx.</summary>
    internal static int PaletteModeCtx(AomMacroblockD xd)
    {
        int ctx = 0;
        if (xd.AboveMbmi != null && xd.AboveMbmi.Palette.PaletteSize0 > 0) ctx++;
        if (xd.LeftMbmi != null && xd.LeftMbmi.Palette.PaletteSize0 > 0) ctx++;
        return ctx;
    }

    /// <summary>intra_mode_info_cost_uv.</summary>
    internal static int IntraModeInfoCostUv(AomComp cpi, AomMacroblock x, AomMbModeInfo mbmi, int bsize, int modeCost)
    {
        int totalRate = modeCost;
        var mc = x.ModeCosts;
        bool usePalette = mbmi.Palette.PaletteSize1 > 0;
        int uvMode = mbmi.UvMode;
        bool tryPalette = AllowPalette(cpi.AllowScreenContentTools, mbmi.Bsize);
        if (tryPalette && uvMode == UV_DC_PRED)
        {
            totalRate += mc.PaletteUvModeCost[(mbmi.Palette.PaletteSize0 > 0 ? 1 : 0) * 2 + (usePalette ? 1 : 0)];
            if (usePalette) totalRate += AomPaletteCost.PaletteModeCostUv(cpi, x, mbmi, bsize);
        }
        int intraMode = GetUvMode(uvMode);
        if (IsDirectionalMode(intraMode) && UseAngleDelta(bsize))
            totalRate += mc.AngleDeltaCost[(intraMode - V_PRED) * (2 * MAX_ANGLE_DELTA + 1) + mbmi.AngleDelta[1] + MAX_ANGLE_DELTA];
        return totalRate;
    }


    /// <summary>intra_model_rd: a quick prediction and SATD estimate of the plane without the tx pipeline.</summary>
    internal static long IntraModelRd(AomComp cpi, AomMacroblock x, int plane, int planeBsize, int txSize, bool useHadamard)
    {
        var xd = x.E;
        int stepr = TxSizeHighUnit[txSize], stepc = TxSizeWideUnit[txSize];
        int txbw = TxSizeWide[txSize], txbh = TxSizeHigh[txSize];
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane);
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane);
        long satdCost = 0;
        var p = x.Plane[plane];
        var pd = xd.Plane[plane];
        int diffStride = BlockSizeWide[planeBsize];
        var coeff = x.ScratchCoeff;
        for (int row = 0; row < maxBlocksHigh; row += stepr)
            for (int col = 0; col < maxBlocksWide; col += stepc)
            {
                AomReconIntra.PredictIntraBlockFacade(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, plane, col, row, txSize);
                // src_diff / coeff are scratch here: written at offset 0 for every tx block
                if (p.Src.Buf16 != null)
                {
                    AomHbd.SubtractBlock(txbh, txbw, p.SrcDiff, 0, diffStride,
                        p.Src.Buf16, p.Src.Offset + ((row * p.Src.Stride + col) << 2), p.Src.Stride,
                        pd.Dst.Buf16, pd.Dst.Offset + ((row * pd.Dst.Stride + col) << 2), pd.Dst.Stride);
                    if (useHadamard) AomHbd.WhtFwdTxfm(txSize, p.SrcDiff, diffStride, coeff);
                    else Av1FwdTxfmAom.ForwardRawRef(p.SrcDiff, diffStride, txbw, txbh, txSize, Av1InvTransform.Type1dDct, Av1InvTransform.Type1dDct,
                        false, false, coeff.AsSpan(0, AomEncodeMb.MaxEob(txSize)));
                    satdCost += AomEncodeMb.Satd(coeff, TxSize2d[txSize]);
                    continue;
                }
                AomEncodeMb.SubtractBlock(txbh, txbw, p.SrcDiff, 0, diffStride,
                    p.Src.Buf, p.Src.Offset + ((row * p.Src.Stride + col) << 2), p.Src.Stride,
                    pd.Dst.Buf, pd.Dst.Offset + ((row * pd.Dst.Stride + col) << 2), pd.Dst.Stride);
                if (useHadamard) AomHadamard.WhtFwdTxfm(txSize, p.SrcDiff, diffStride, coeff);
                else Av1FwdTxfmAom.ForwardRaw(p.SrcDiff, diffStride, txbw, txbh, txSize, Av1InvTransform.Type1dDct, Av1InvTransform.Type1dDct,
                    false, false, coeff.AsSpan(0, AomEncodeMb.MaxEob(txSize)));
                satdCost += AomEncodeMb.Satd(coeff, TxSize2d[txSize]);
            }
        return satdCost;
    }

    /// <summary>aom_variance WxH against zeros (av1_calc_normalized_variance): sse - sum^2 / n.</summary>
    internal static uint VarianceVsZero(byte[] buf, int off, int stride, int w, int h, out uint sse)
    {
        long sum = 0; ulong ss = 0;
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && (w & 3) == 0 && w > 0 && h > 0 && off >= 0 && off + (long)(h - 1) * stride + w <= buf.Length)
        {
            // sums of the samples and their squares in int32 lanes (at most 128 x 128 x 255^2 < 2^31 per lane group)
            ref byte b0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(buf);
            var sv = System.Runtime.Intrinsics.Vector256<int>.Zero;
            var qv = System.Runtime.Intrinsics.Vector256<int>.Zero;
            for (int r = 0; r < h; r++)
            {
                ref byte row = ref Unsafe.Add(ref b0, off + r * stride);
                int c = 0;
                for (; c + 16 <= w; c += 16)
                {
                    var v = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref row, (nuint)c));
                    sv += System.Runtime.Intrinsics.X86.Avx2.MultiplyAddAdjacent(v, System.Runtime.Intrinsics.Vector256.Create((short)1));
                    qv += System.Runtime.Intrinsics.X86.Avx2.MultiplyAddAdjacent(v, v);
                }
                for (; c < w; c += 4)
                {
                    var v = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref row, c))).AsByte());
                    sv += v & System.Runtime.Intrinsics.Vector256.Create(-1, -1, -1, -1, 0, 0, 0, 0);
                    qv += (v * v) & System.Runtime.Intrinsics.Vector256.Create(-1, -1, -1, -1, 0, 0, 0, 0);
                }
            }
            sum = System.Runtime.Intrinsics.Vector256.Sum(sv);
            ss = (ulong)(uint)System.Runtime.Intrinsics.Vector256.Sum(qv);
            sse = (uint)ss;
            return (uint)(ss - (ulong)(sum * sum / (w * h)));
        }
        if (System.Runtime.Intrinsics.X86.Sse41.IsSupported && (w & 3) == 0 && w > 0 && h > 0 && off >= 0 && off + (long)(h - 1) * stride + w <= buf.Length)
        {
            // no AVX2: 8 (or 4) samples at a time, the sums and squares in int32 lanes (exact: see above)
            ref byte b0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(buf);
            var sv = System.Runtime.Intrinsics.Vector128<int>.Zero;
            var qv = System.Runtime.Intrinsics.Vector128<int>.Zero;
            var ones = System.Runtime.Intrinsics.Vector128.Create((short)1);
            for (int r = 0; r < h; r++)
            {
                ref byte row = ref Unsafe.Add(ref b0, off + r * stride);
                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    var v = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int16(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref row, c))).AsByte());
                    sv += System.Runtime.Intrinsics.X86.Sse2.MultiplyAddAdjacent(v, ones);
                    qv += System.Runtime.Intrinsics.X86.Sse2.MultiplyAddAdjacent(v, v);
                }
                if (c < w)
                {
                    // the last 4 samples (the upper lanes zero)
                    var v = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int16(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref row, c))).AsByte());
                    sv += System.Runtime.Intrinsics.X86.Sse2.MultiplyAddAdjacent(v, ones);
                    qv += System.Runtime.Intrinsics.X86.Sse2.MultiplyAddAdjacent(v, v);
                }
            }
            sum = System.Runtime.Intrinsics.Vector128.Sum(sv);
            ss = (ulong)(uint)System.Runtime.Intrinsics.Vector128.Sum(qv);
            sse = (uint)ss;
            return (uint)(ss - (ulong)(sum * sum / (w * h)));
        }
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++) { int v = buf[off + r * stride + c]; sum += v; ss += (ulong)(v * v); }
        sse = (uint)ss;
        return (uint)(ss - (ulong)(sum * sum / (w * h)));
    }

    /// <summary>compute_avg_log_variance.</summary>
    private static void ComputeAvgLogVariance(AomComp cpi, AomMacroblock x, int bs, out double avgLogSrcVariance, out double avgLogReconVariance)
    {
        var xd = x.E;
        int sbSize = cpi.SbSize;
        int miRowInSb = xd.MiRow & (MiSizeHigh[sbSize] - 1);
        int miColInSb = xd.MiCol & (MiSizeWide[sbSize] - 1);
        int rightOverflow = xd.MbToRightEdge < 0 ? (-xd.MbToRightEdge) >> 3 : 0;
        int bottomOverflow = xd.MbToBottomEdge < 0 ? (-xd.MbToBottomEdge) >> 3 : 0;
        int bw = 4 * MiSizeWide[bs] - rightOverflow;
        int bh = 4 * MiSizeHigh[bs] - bottomOverflow;
        avgLogSrcVariance = 0; avgLogReconVariance = 0;
        var src = x.Plane[0].Src;
        var dst = xd.Plane[0].Dst;
        Unsafe.SkipInit(out StackArr4<uint> fourBuf);
        ref uint four0 = ref fourBuf[0];
        bool fourValid = false;
        bool fourOk = dst.Buf16 == null && Avx2.IsSupported && dst.Offset >= 0
            && dst.Offset + (long)(bh - 1) * dst.Stride + bw <= dst.Buf.Length;
        for (int i = 0; i < bh; i += 4)
        {
            int r = miRowInSb + (i >> 2);
            for (int j = 0; j < bw; j += 4)
            {
                int c = miColInSb + (j >> 2);
                int miOffset = r * MiSizeWide[sbSize] + c;
                int srcVar = x.SrcVar4x4[miOffset];
                double logSrcVar = x.SrcLogVar4x4[miOffset];
                if (srcVar < 0)
                {
                    srcVar = src.Buf16 != null ? (int)AomHbd.Variance(src.Buf16, src.Offset + i * src.Stride + j, src.Stride, null, 0, 0, 0, 4, 4, xd.Bd, out _)
                        : (int)VarianceVsZero(src.Buf, src.Offset + i * src.Stride + j, src.Stride, 4, 4, out _);
                    x.SrcVar4x4[miOffset] = srcVar;
                    logSrcVar = Log1p(srcVar / 16.0);
                    x.SrcLogVar4x4[miOffset] = logSrcVar;
                }
                else if (logSrcVar < 0)
                {
                    logSrcVar = Log1p(srcVar / 16.0);
                    x.SrcLogVar4x4[miOffset] = logSrcVar;
                }
                avgLogSrcVariance += logSrcVar;
                int reconVar;
                if (dst.Buf16 != null)
                    reconVar = (int)AomHbd.Variance(dst.Buf16, dst.Offset + i * dst.Stride + j, dst.Stride, null, 0, 0, 0, 4, 4, xd.Bd, out _);
                else
                {
                    // the recon variances four 4x4 blocks at a time (one pass), consumed in order
                    if ((j & 15) == 0 && j + 16 <= bw && fourOk)
                    {
                        Var4x4x4(dst.Buf, dst.Offset + i * dst.Stride + j, dst.Stride, ref four0);
                        fourValid = true;
                    }
                    if (fourValid) reconVar = (int)Unsafe.Add(ref four0, (j >> 2) & 3);
                    else reconVar = (int)VarianceVsZero(dst.Buf, dst.Offset + i * dst.Stride + j, dst.Stride, 4, 4, out _);
                    if (((j >> 2) & 3) == 3) fourValid = false;
                }
                avgLogReconVariance += Log1p(reconVar / 16.0);
            }
            fourValid = false;
        }
        int blocks = (bw * bh) / 16;
        avgLogSrcVariance /= blocks;
        avgLogReconVariance /= blocks;
    }

    /// <summary>aom_variance4x4 against zeros (sse - (sum^2 &gt;&gt; 4)) of the four 4x4 blocks at buf[off .. off + 16) x 4 rows:
    /// each row's 16 samples widened, pmaddwd against themselves (squares) and ones (sums), one hadd per block.</summary>
    internal static void Var4x4x4(byte[] buf, int off, int stride, ref uint var4)
    {
        ref byte r0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), off);
        var ones = Vector256.Create((short)1);
        var sum = Vector256<int>.Zero;
        var sq = Vector256<int>.Zero;
        for (int r = 0; r < 4; r++)
        {
            var v = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref Unsafe.Add(ref r0, r * stride)));
            sum += Avx2.MultiplyAddAdjacent(v, ones);
            sq += Avx2.MultiplyAddAdjacent(v, v);
        }
        // lanes: [sum b0, sum b1, sse b0, sse b1 | sum b2, sum b3, sse b2, sse b3]
        var h = Avx2.HorizontalAdd(sum, sq);
        for (int k = 0; k < 4; k++)
        {
            int lane = (k >> 1) * 4 + (k & 1);
            int sm = h.GetElement(lane);
            Unsafe.Add(ref var4, k) = (uint)h.GetElement(lane + 2) - (uint)(((long)sm * sm) >> 4);
        }
    }

    // libaom's log1p is mingw-w64's x87 fyl2xp1 / fyl2x at 64-bit precision; log(1 + x) with 1 + x exact (x = k / 16)
    // matches it on all but ~0.02% of the inputs (1 ulp), below the resolution of the (int64) rd truncation it feeds
    private static double Log1p(double x) => PortableMathD.Log(1 + x);

    /// <summary>INTRA_RD_VAR_THRESH.</summary>
    private static double IntraRdVarThresh(int speed) => 1.0 - 0.25 * speed;

    /// <summary>intra_rd_variance_factor.</summary>
    internal static double IntraRdVarianceFactor(AomComp cpi, AomMacroblock x, int bs)
    {
        double threshold = IntraRdVarThresh(cpi.Speed);
        if (threshold <= 0) return 1.0;
        double varianceRdFactor = 1.0;
        ComputeAvgLogVariance(cpi, x, bs, out double avgLogSrcVariance, out double avgLogReconVariance);
        avgLogSrcVariance += 0.000001;
        avgLogReconVariance += 0.000001;
        if (avgLogSrcVariance >= avgLogReconVariance)
        {
            double varDiff = avgLogSrcVariance - avgLogReconVariance;
            if (varDiff > 0.5 && avgLogReconVariance < threshold) varianceRdFactor = 1.0 + (varDiff * 2) / avgLogSrcVariance;
        }
        else
        {
            double varDiff = avgLogReconVariance - avgLogSrcVariance;
            if (varDiff > 0.5 && avgLogSrcVariance < threshold) varianceRdFactor = 1.0 + varDiff / (2 * avgLogSrcVariance);
        }
        return Math.Min(3.0, varianceRdFactor);
    }

    /// <summary>model_intra_yrd_and_prune.</summary>
    private static bool ModelIntraYrdAndPrune(AomComp cpi, AomMacroblock x, int bsize, ref long bestModelRd)
    {
        int txSize = Math.Min(TX_32X32, (int)MaxTxsizeLookup[bsize]);
        long thisModelRd = IntraModelRd(cpi, x, 0, bsize, txSize, true);
        if (bestModelRd != long.MaxValue && thisModelRd > bestModelRd + (bestModelRd >> 2)) return true;
        if (thisModelRd < bestModelRd) bestModelRd = thisModelRd;
        return false;
    }

    /// <summary>rd_pick_filter_intra_sby.</summary>
    private static bool RdPickFilterIntraSby(AomComp cpi, AomMacroblock x, ref int rate, ref int rateTokenonly, ref long distortion,
        ref byte skippable, int bsize, int modeCost, int bestModeSoFar, ref long bestRd, ref long bestModelRd, AomPickModeContext ctx)
    {
        var sf = cpi.Sf;
        if (sf.intra_sf.prune_filter_intra_level == 2) return false;
        var xd = x.E;
        var mbmi = xd.Mi0;
        bool filterIntraSelectedFlag = false;
        int bestTxSize = TX_8X8;
        byte bestUse = 0, bestFiMode = 0;
        System.Runtime.CompilerServices.Unsafe.SkipInit(out StackArr1024<byte> bestTxTypeMapBuf15); Span<byte> bestTxTypeMap = bestTxTypeMapBuf15;   // written before read (uninitialized in libaom)
        mbmi.UseFilterIntra = 1;
        mbmi.Mode = DC_PRED;
        mbmi.Palette.PaletteSize0 = 0;

        // skip filter intra when the cached MB_MODE_INFO's winner is not filter intra
        if (x.UseMbModeCache && x.MbModeCache!.UseFilterIntra == 0) return false;

        for (int mode = 0; mode < FILTER_INTRA_MODES; ++mode)
        {
            mbmi.FilterIntraMode = (byte)mode;
            if (sf.intra_sf.prune_filter_intra_level == 1 && (DerivedFilterIntraModeUsedFlag[bestModeSoFar] & (1 << mode)) == 0) continue;
            // only the cached winner's filter intra mode
            if (x.UseMbModeCache && mode != x.MbModeCache!.FilterIntraMode) continue;
            if (ModelIntraYrdAndPrune(cpi, x, bsize, ref bestModelRd)) continue;
            AomRdStats tokenonlyRdStats = default;
            AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref tokenonlyRdStats, bsize, bestRd);
            if (tokenonlyRdStats.Rate == int.MaxValue) continue;
            int thisRate = tokenonlyRdStats.Rate + IntraModeInfoCostY(cpi, x, mbmi, bsize, modeCost, false);
            long thisRd = AomRd.RdCost(x.Rdmult, thisRate, tokenonlyRdStats.Dist);
            // Visual quality adjustment based on recon vs source variance.
            if (cpi.AllIntra && thisRd != long.MaxValue) thisRd = (long)(thisRd * IntraRdVarianceFactor(cpi, x, bsize));
            AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, null, bsize, thisRd, sf.winner_mode_sf.multi_winner_mode_type);
            if (thisRd < bestRd)
            {
                bestRd = thisRd;
                bestTxSize = mbmi.TxSize;
                bestUse = mbmi.UseFilterIntra; bestFiMode = mbmi.FilterIntraMode;
                xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(bestTxTypeMap);
                rate = thisRate;
                rateTokenonly = tokenonlyRdStats.Rate;
                distortion = tokenonlyRdStats.Dist;
                skippable = tokenonlyRdStats.SkipTxfm;
                filterIntraSelectedFlag = true;
            }
        }

        if (filterIntraSelectedFlag)
        {
            mbmi.Mode = DC_PRED;
            mbmi.TxSize = bestTxSize;
            mbmi.UseFilterIntra = bestUse; mbmi.FilterIntraMode = bestFiMode;
            bestTxTypeMap.Slice(0, ctx.NumFourByFourBlk).CopyTo(ctx.TxTypeMap);
            return true;
        }
        return false;
    }

    /// <summary>set_y_mode_and_delta_angle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetYModeAndDeltaAngle(int modeIdx, AomMbModeInfo mbmi, bool reorderDeltaAngleEval)
    {
        if (modeIdx < INTRA_MODE_END)
        {
            mbmi.Mode = IntraRdSearchModeOrder[modeIdx];
            mbmi.AngleDelta[0] = 0;
        }
        else
        {
            mbmi.Mode = (modeIdx - INTRA_MODE_END) / (MAX_ANGLE_DELTA * 2) + V_PRED;
            int deltaAngleEvalIdx = (modeIdx - INTRA_MODE_END) % (MAX_ANGLE_DELTA * 2);
            mbmi.AngleDelta[0] = reorderDeltaAngleEval
                ? LumaDeltaAnglesOrder[deltaAngleEvalIdx]
                : (sbyte)(deltaAngleEvalIdx < 3 ? deltaAngleEvalIdx - 3 : deltaAngleEvalIdx - 2);
        }
    }

    /// <summary>get_model_rd_index_for_pruning.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetModelRdIndexForPruning(AomMacroblock x, AomSpeedFeatures sf)
    {
        int topAllowed = sf.intra_sf.top_intra_model_count_allowed;
        if (sf.intra_sf.adapt_top_model_rd_count_using_neighbors == 0) return topAllowed - 1;
        var xd = x.E;
        int mode = xd.Mi0.Mode;
        int idx = topAllowed - 1;
        bool leftNeq = xd.LeftAvailable && xd.LeftMbmi!.Mode != mode;
        bool aboveNeq = xd.UpAvailable && xd.AboveMbmi!.Mode != mode;
        if (x.Qindex <= 127) { if (leftNeq || aboveNeq) idx = Math.Max(idx - 1, 0); }
        else if (leftNeq && aboveNeq) idx = Math.Max(idx - 1, 0);
        return idx;
    }

    /// <summary>prune_intra_y_mode.</summary>
    internal static bool PruneIntraYMode(long thisModelRd, ref long bestModelRd, Span<long> topIntraModelRd, int maxModelCntAllowed, int modelRdIndexForPruning)
    {
        const double threshBest = 1.50, threshTop = 1.00;
        for (int i = 0; i < maxModelCntAllowed; i++)
            if (thisModelRd < topIntraModelRd[i])
            {
                for (int j = maxModelCntAllowed - 1; j > i; j--) topIntraModelRd[j] = topIntraModelRd[j - 1];
                topIntraModelRd[i] = thisModelRd;
                break;
            }
        if (topIntraModelRd[modelRdIndexForPruning] != long.MaxValue && thisModelRd > threshTop * topIntraModelRd[modelRdIndexForPruning]) return true;
        if (thisModelRd != long.MaxValue && thisModelRd > threshBest * bestModelRd) return true;
        if (thisModelRd < bestModelRd) bestModelRd = thisModelRd;
        return false;
    }

    /// <summary>prune_luma_odd_delta_angles_using_rd_cost.</summary>
    private static bool PruneLumaOddDeltaAnglesUsingRdCost(AomMbModeInfo mbmi, ReadOnlySpan<long> intraModesRdCost, long bestRd, int level)
    {
        int lda = mbmi.AngleDelta[0];
        if (level == 0 || !IsDirectionalMode(mbmi.Mode) || (AbsI(lda) & 1) == 0 || bestRd == long.MaxValue) return false;
        long rdThresh = bestRd + (bestRd >> 3);
        return intraModesRdCost[lda + MAX_ANGLE_DELTA] > rdThresh && intraModesRdCost[lda + MAX_ANGLE_DELTA + 2] > rdThresh;
    }

    /// <summary>intra_block_yrd (winner mode processing).</summary>
    private static bool IntraBlockYrd(AomComp cpi, AomMacroblock x, int bsize, int bmodeCostsOff, ref long bestRd, ref int rate, ref int rateTokenonly,
        ref long distortion, ref byte skippable, AomMbModeInfo bestMbmi, AomPickModeContext ctx)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        AomRdStats rdStats = default;
        long refBestRd = cpi.Sf.tx_sf.use_rd_based_breakout_for_intra_tx_search ? bestRd : long.MaxValue;
        AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref rdStats, bsize, refBestRd);
        if (rdStats.Rate == int.MaxValue) return false;
        int thisRateTokenonly = rdStats.Rate;
        if (xd.Lossless[mbmi.SegmentId] == 0 && AomTxSearch.BlockSignalsTxsize(mbmi.Bsize))
            thisRateTokenonly -= AomTxSearch.TxSizeCost(x, bsize, mbmi.TxSize);
        int thisRate = rdStats.Rate + IntraModeInfoCostY(cpi, x, mbmi, bsize, x.ModeCosts.YModeCosts[bmodeCostsOff + mbmi.Mode], false);
        long thisRd = AomRd.RdCost(x.Rdmult, thisRate, rdStats.Dist);
        if (thisRd < bestRd)
        {
            bestMbmi.CopyFrom(mbmi);
            bestRd = thisRd;
            rate = thisRate;
            rateTokenonly = thisRateTokenonly;
            distortion = rdStats.Dist;
            skippable = rdStats.SkipTxfm;
            xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(ctx.TxTypeMap);
            return true;
        }
        return false;
    }


    /// <summary>av1_rd_pick_intra_sby_mode: the best non-intrabc luma mode of an intra-frame block.</summary>
    internal static long RdPickIntraSbyMode(AomComp cpi, AomMacroblock x, ref int rate, ref int rateTokenonly, ref long distortion,
        ref byte skippable, int bsize, long bestRd, AomPickModeContext ctx)
    {
        long r = RdPickIntraSbyModeImpl(cpi, x, ref rate, ref rateTokenonly, ref distortion, ref skippable, bsize, bestRd, ctx);
        if (AomTrace.Out != null)
        {
            var m = x.E.Mi0;
            AomTrace.Out.Write($"sby {x.E.MiRow} {x.E.MiCol} bs {bsize} in {bestRd} -> {r} rate {rate} tok {rateTokenonly} dist {distortion} y {m.Mode} ad {m.AngleDelta[0]} fi {m.UseFilterIntra} {m.FilterIntraMode} tx {m.TxSize} rdmult {x.Rdmult}\n");
        }
        return r;
    }

    private static long RdPickIntraSbyModeImpl(AomComp cpi, AomMacroblock x, ref int rate, ref int rateTokenonly, ref long distortion,
        ref byte skippable, int bsize, long bestRd, AomPickModeContext ctx)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var sf = cpi.Sf;
        long bestModelRd = long.MaxValue;
        System.Runtime.CompilerServices.Unsafe.SkipInit(out StackArr13<byte> directionalModeSkipMaskBuf16); Span<byte> directionalModeSkipMask = directionalModeSkipMaskBuf16;
        directionalModeSkipMask.Clear();
        bool beatBestRd = false;
        bool tryPalette = cpi.EnablePalette && AllowPalette(cpi.AllowScreenContentTools, mbmi.Bsize);
        int a = xd.AboveMbmi?.Mode ?? DC_PRED;   // av1_above_block_mode
        int l = xd.LeftMbmi?.Mode ?? DC_PRED;    // av1_left_block_mode
        int aboveCtx = IntraModeContext[a], leftCtx = IntraModeContext[l];
        int bmodeCostsOff = (aboveCtx * 13 + leftCtx) * 13;

        mbmi.AngleDelta[0] = 0;
        if (sf.intra_sf.intra_pruning_with_hog != 0)
        {
            // Less aggressive thresholds than inter frames: key / intra frames want higher quality
            ReadOnlySpan<float> thresh = new float[] { -1.2f, -1.2f, -0.6f, 0.4f };
            PruneIntraModeWithHog(cpi, x, bsize, thresh[sf.intra_sf.intra_pruning_with_hog - 1], directionalModeSkipMask, false);
        }
        mbmi.UseFilterIntra = 0;
        mbmi.Palette.PaletteSize0 = 0;

        AomRdoptUtils.SetModeEvalParams(cpi, x, MODE_EVAL);

        var bestMbmi = x.ScratchBestMbmi ??= new AomMbModeInfo();
        bestMbmi.CopyFrom(mbmi);
        int maxWinnerModeCount = AomRdoptUtils.WinnerModeCountAllowed[sf.winner_mode_sf.multi_winner_mode_type];
        AomRdoptUtils.ZeroWinnerModeStats(bsize, maxWinnerModeCount, x.WinnerModeStats);
        x.WinnerModeCount = 0;

        System.Runtime.CompilerServices.Unsafe.SkipInit(out StackArr4<long> topIntraModelRdBuf17); Span<long> topIntraModelRd = topIntraModelRdBuf17;
        topIntraModelRd.Fill(long.MaxValue);
        System.Runtime.CompilerServices.Unsafe.SkipInit(out StackArr117<long> intraModesRdCostBuf18); Span<long> intraModesRdCost = intraModesRdCostBuf18;   // filled below
        intraModesRdCost.Fill(long.MaxValue);

        const int LumaModeCount = INTRA_MODE_END + 8 * 2 * MAX_ANGLE_DELTA;   // LUMA_MODE_COUNT: 13 + 48
        bool reorder = sf.intra_sf.prune_luma_odd_delta_angles_in_intra != 0;
        for (int modeIdx = INTRA_MODE_START; modeIdx < LumaModeCount; ++modeIdx)
        {
            SetYModeAndDeltaAngle(modeIdx, mbmi, reorder);
            int lumaDeltaAngle = mbmi.AngleDelta[0];

            if (IsDiagonalMode(mbmi.Mode) && !cpi.EnableDiagonalIntra) continue;
            if (IsDirectionalMode(mbmi.Mode) && !cpi.EnableDirectionalIntra) continue;
            // smooth is picked more often than smooth h / v: treated differently by the speed features
            if ((!cpi.EnableSmoothIntra || sf.intra_sf.disable_smooth_intra != 0) && (mbmi.Mode == SMOOTH_H_PRED || mbmi.Mode == SMOOTH_V_PRED)) continue;
            if (!cpi.EnableSmoothIntra && mbmi.Mode == SMOOTH_PRED) continue;
            // filter intra overlaps smooth: smooth pruned only when all filter intra modes are enabled
            if (sf.intra_sf.disable_smooth_intra != 0 && sf.intra_sf.prune_filter_intra_level == 0 && mbmi.Mode == SMOOTH_PRED) continue;
            if (!cpi.EnablePaethIntra && mbmi.Mode == PAETH_PRED) continue;

            // only the winner mode of x->mb_mode_cache
            if (x.UseMbModeCache && mbmi.Mode != x.MbModeCache!.Mode) continue;

            bool isDirectionalMode = IsDirectionalMode(mbmi.Mode);
            if (isDirectionalMode && directionalModeSkipMask[mbmi.Mode] != 0) continue;
            if (isDirectionalMode && !(UseAngleDelta(bsize) && cpi.EnableAngleDelta) && lumaDeltaAngle != 0) continue;

            if ((sf.intra_sf.intra_y_mode_mask[MaxTxsizeLookup[bsize]] & (1 << mbmi.Mode)) == 0) continue;

            if (PruneLumaOddDeltaAnglesUsingRdCost(mbmi, intraModesRdCost.Slice(mbmi.Mode * SIZE_OF_ANGLE_DELTA_RD_COST_ARRAY, SIZE_OF_ANGLE_DELTA_RD_COST_ARRAY),
                    bestRd, sf.intra_sf.prune_luma_odd_delta_angles_in_intra))
                continue;

            int txSize = Math.Min(TX_32X32, (int)MaxTxsizeLookup[bsize]);
            long thisModelRd = IntraModelRd(cpi, x, 0, bsize, txSize, true);
            int modelRdIndexForPruning = GetModelRdIndexForPruning(x, sf);
            if (PruneIntraYMode(thisModelRd, ref bestModelRd, topIntraModelRd, sf.intra_sf.top_intra_model_count_allowed, modelRdIndexForPruning))
                continue;

            // the real prediction and transform search (the model was only an estimate)
            AomRdStats thisRdStats = default;
            AomTxSearch.PickUniformTxSizeTypeYrd(cpi, x, ref thisRdStats, bsize, bestRd);
            int thisRateTokenonly = thisRdStats.Rate;
            long thisDistortion = thisRdStats.Dist;
            byte s = thisRdStats.SkipTxfm;
            if (thisRateTokenonly == int.MaxValue) continue;

            // intra blocks always code tx_size (prediction granularity): in the full rate, not the token-only rate
            if (xd.Lossless[mbmi.SegmentId] == 0 && AomTxSearch.BlockSignalsTxsize(mbmi.Bsize))
                thisRateTokenonly -= AomTxSearch.TxSizeCost(x, bsize, mbmi.TxSize);
            int thisRate = thisRdStats.Rate + IntraModeInfoCostY(cpi, x, mbmi, bsize, x.ModeCosts.YModeCosts[bmodeCostsOff + mbmi.Mode], false);
            long thisRd = AomRd.RdCost(x.Rdmult, thisRate, thisDistortion);

            // Visual quality adjustment based on recon vs source variance.
            if (cpi.AllIntra && thisRd != long.MaxValue) thisRd = (long)(thisRd * IntraRdVarianceFactor(cpi, x, bsize));

            intraModesRdCost[mbmi.Mode * SIZE_OF_ANGLE_DELTA_RD_COST_ARRAY + lumaDeltaAngle + MAX_ANGLE_DELTA + 1] = thisRd;

            AomRdoptUtils.StoreWinnerModeStats(cpi, x, mbmi, null, bsize, thisRd, sf.winner_mode_sf.multi_winner_mode_type);
            if (thisRd < bestRd)
            {
                bestMbmi.CopyFrom(mbmi);
                bestRd = thisRd;
                beatBestRd = true;
                rate = thisRate;
                rateTokenonly = thisRateTokenonly;
                distortion = thisDistortion;
                skippable = s;
                xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(ctx.TxTypeMap);
            }
        }

        if (tryPalette)
            AomPalette.RdPickPaletteIntraSby(cpi, x, bsize, x.ModeCosts.YModeCosts[bmodeCostsOff + DC_PRED], bestMbmi, x.BestPaletteColorMap,
                ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable, ref beatBestRd, ctx, ctx.TxTypeMap);

        if (beatBestRd && FilterIntraAllowedBsize(cpi, bsize))
            if (RdPickFilterIntraSby(cpi, x, ref rate, ref rateTokenonly, ref distortion, ref skippable, bsize,
                    x.ModeCosts.YModeCosts[bmodeCostsOff + DC_PRED], bestMbmi.Mode, ref bestRd, ref bestModelRd, ctx))
                bestMbmi.CopyFrom(mbmi);

        // no mode beat the incoming best_rd: no winner mode processing, INT64_MAX
        if (!beatBestRd) return long.MaxValue;

        // winner mode processing: the best tx configuration for the few best modes
        if (sf.winner_mode_sf.multi_winner_mode_type != 0)
        {
            int bestModeIdx = 0;
            var colorMapDst = xd.Plane[0].ColorIndexMap;
            AomRdoptUtils.GetBlockDimensions(bsize, 0, xd, out int blockWidth, out int blockHeight, out _, out _);
            for (int modeIdx = 0; modeIdx < x.WinnerModeCount; modeIdx++)
            {
                mbmi.CopyFrom(x.WinnerModeStats[modeIdx].Mbmi);
                if (AomRdoptUtils.IsWinnerModeProcessingEnabled(cpi, x, mbmi, false))
                {
                    if (mbmi.Palette.PaletteSize0 > 0)
                        Array.Copy(x.WinnerModeStats[modeIdx].ColorIndexMap, colorMapDst, blockWidth * blockHeight);
                    AomRdoptUtils.SetModeEvalParams(cpi, x, WINNER_MODE_EVAL);
                    if (IntraBlockYrd(cpi, x, bsize, bmodeCostsOff, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable, bestMbmi, ctx))
                        bestModeIdx = modeIdx;
                }
            }
            if (bestMbmi.Palette.PaletteSize0 > 0)
                Array.Copy(x.WinnerModeStats[bestModeIdx].ColorIndexMap, colorMapDst, blockWidth * blockHeight);
        }
        else if (AomRdoptUtils.IsWinnerModeProcessingEnabled(cpi, x, mbmi, false))
        {
            AomRdoptUtils.SetModeEvalParams(cpi, x, WINNER_MODE_EVAL);
            mbmi.CopyFrom(bestMbmi);
            IntraBlockYrd(cpi, x, bsize, bmodeCostsOff, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable, bestMbmi, ctx);
        }
        mbmi.CopyFrom(bestMbmi);
        ctx.TxTypeMap.AsSpan(0, ctx.NumFourByFourBlk).CopyTo(xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset));
        return bestRd;
    }

    /// <summary>produce_gradients_for_sb: with is_gradient_caching_for_hog_enabled, the superblock's per-sample Sobel
    /// gradients of the planes the HOG prunes use (compute_gradient_info_sb), for the blocks' histograms.</summary>
    internal static void ProduceGradientsForSb(AomComp cpi, AomMacroblock x, int sbSize, int miRow, int miCol)
    {
        x.SbGradientCached[0] = x.SbGradientCached[1] = false;
        var sf = cpi.Sf;
        if (!(cpi.FrameIsIntraOnly && sf.rt_sf.use_nonrd_pick_mode == 0 && sf.part_sf.partition_search_type == SEARCH_PARTITION &&
              (sf.intra_sf.intra_pruning_with_hog != 0 || sf.intra_sf.chroma_intra_pruning_with_hog != 0)))
            return;
        int numPlanes = cpi.Cm.NumPlanes;
        AomEncodeFrame.SetupSrcPlanes(cpi, x, miRow, miCol, numPlanes, sbSize);
        if (sf.intra_sf.intra_pruning_with_hog != 0) { ComputeGradientInfoSb(cpi, x, sbSize, 0, miRow, miCol); x.SbGradientCached[0] = true; }
        if (sf.intra_sf.chroma_intra_pruning_with_hog != 0 && numPlanes > 1) { ComputeGradientInfoSb(cpi, x, sbSize, 1, miRow, miCol); x.SbGradientCached[1] = true; }
    }

    // compute_gradient_info_sb over the superblock's samples that a block's histogram can read (the interior of the
    // mi-aligned frame area; libaom computes the whole superblock, the rest never being read)
    private static void ComputeGradientInfoSb(AomComp cpi, AomMacroblock x, int sbSize, int plane, int miRow, int miCol)
    {
        var pd = x.E.Plane[plane];
        int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
        int sbH = BlockSizeHigh[sbSize] >> ssY, sbW = BlockSizeWide[sbSize] >> ssX;
        int visH = Math.Min(sbH, ((cpi.Cm.MiRows - miRow) * 4) >> ssY), visW = Math.Min(sbW, ((cpi.Cm.MiCols - miCol) * 4) >> ssX);
        var src = x.Plane[plane].Src;
        if (src.Buf16 != null)
            AomMl.ComputeGradientInfoSb(src.Buf16.AsSpan(src.Offset), src.Stride, sbW, visW, visH, x.GradAbsSum, x.GradBin, plane * AomMbPlane.MaxSbSquare);
        else
            AomMl.ComputeGradientInfoSb(src.Buf.AsSpan(src.Offset), src.Stride, sbW, visW, visH, x.GradAbsSum, x.GradBin, plane * AomMbPlane.MaxSbSquare);
    }

    /// <summary>prune_intra_mode_with_hog (collect_hog_data over the block's visible source).</summary>
    internal static void PruneIntraModeWithHog(AomComp cpi, AomMacroblock x, int bsize, float th, Span<byte> directionalModeSkipMask, bool isChroma)
    {
        var xd = x.E;
        int plane = isChroma ? 1 : 0;
        var pd = xd.Plane[plane];
        int bh = BlockSizeHigh[bsize], bw = BlockSizeWide[bsize];
        int rows = (xd.MbToBottomEdge >= 0 ? bh : (xd.MbToBottomEdge >> 3) + bh) >> pd.SubsamplingY;
        int cols = (xd.MbToRightEdge >= 0 ? bw : (xd.MbToRightEdge >> 3) + bw) >> pd.SubsamplingX;
        if (x.SbGradientCached[plane])
        {
            // generate_hog_using_gradient_cache
            int sbSize = cpi.Cm.SbSize;
            int sbWidth = BlockSizeWide[sbSize] >> pd.SubsamplingX;
            int miRowInSb = xd.MiRow & (MiSizeHigh[sbSize] - 1), miColInSb = xd.MiCol & (MiSizeWide[sbSize] - 1);
            int off = plane * AomMbPlane.MaxSbSquare + sbWidth * (miRowInSb << (2 - pd.SubsamplingY)) + (miColInSb << (2 - pd.SubsamplingX));
            AomMl.PruneIntraModeWithHogCached(x.GradAbsSum, x.GradBin, off, sbWidth, rows, cols, pd.SubsamplingX, pd.SubsamplingY, th, directionalModeSkipMask);
            return;
        }
        var src = x.Plane[plane].Src;
        if (src.Buf16 != null)
            AomMl.PruneIntraModeWithHog(src.Buf16.AsSpan(src.Offset), src.Stride, rows, cols, pd.SubsamplingX, pd.SubsamplingY, th, directionalModeSkipMask);
        else
            AomMl.PruneIntraModeWithHog(src.Buf.AsSpan(src.Offset), src.Stride, rows, cols, pd.SubsamplingX, pd.SubsamplingY, th, directionalModeSkipMask);
    }

    // ---- chroma ----

    /// <summary>pick_intra_angle_routine_sbuv.</summary>
    private static long PickIntraAngleRoutineSbuv(AomComp cpi, AomMacroblock x, int bsize, int rateOverhead, long bestRdIn, ref int rate,
        ref AomRdStats rdStats, ref int bestAngleDelta, ref long bestRd)
    {
        var mbmi = x.E.Mi0;
        AomRdStats tokenonlyRdStats = default;
        if (!AomTxSearch.TxfmUvrd(cpi, x, ref tokenonlyRdStats, bsize, bestRdIn)) return long.MaxValue;
        int thisRate = tokenonlyRdStats.Rate + IntraModeInfoCostUv(cpi, x, mbmi, bsize, rateOverhead);
        long thisRd = AomRd.RdCost(x.Rdmult, thisRate, tokenonlyRdStats.Dist);
        if (thisRd < bestRd)
        {
            bestRd = thisRd;
            bestAngleDelta = mbmi.AngleDelta[1];
            rate = thisRate;
            rdStats.Rate = tokenonlyRdStats.Rate;
            rdStats.Dist = tokenonlyRdStats.Dist;
            rdStats.SkipTxfm = tokenonlyRdStats.SkipTxfm;
        }
        return thisRd;
    }

    /// <summary>rd_pick_intra_angle_sbuv.</summary>
    private static bool RdPickIntraAngleSbuv(AomComp cpi, AomMacroblock x, int bsize, int rateOverhead, long bestRd, ref int rate, ref AomRdStats rdStats)
    {
        var mbmi = x.E.Mi0;
        int bestAngleDelta = 0;
        var rdCostBuf19 = new StackArr10<long>(); Span<long> rdCost = rdCostBuf19;
        rdStats.Rate = int.MaxValue;
        rdStats.SkipTxfm = 0;
        rdStats.Dist = long.MaxValue;
        rdCost.Fill(long.MaxValue);

        for (int angleDelta = 0; angleDelta <= MAX_ANGLE_DELTA; angleDelta += 2)
            for (int i = 0; i < 2; ++i)
            {
                long bestRdIn = bestRd == long.MaxValue ? long.MaxValue : bestRd + (bestRd >> (angleDelta == 0 ? 3 : 5));
                mbmi.AngleDelta[1] = (sbyte)((1 - 2 * i) * angleDelta);
                long thisRd = PickIntraAngleRoutineSbuv(cpi, x, bsize, rateOverhead, bestRdIn, ref rate, ref rdStats, ref bestAngleDelta, ref bestRd);
                rdCost[2 * angleDelta + i] = thisRd;
                if (angleDelta == 0)
                {
                    if (thisRd == long.MaxValue) return false;
                    rdCost[1] = thisRd;
                    break;
                }
            }

        for (int angleDelta = 1; angleDelta <= MAX_ANGLE_DELTA; angleDelta += 2)
            for (int i = 0; i < 2; ++i)
            {
                long rdThresh = bestRd + (bestRd >> 5);
                bool skipSearch = rdCost[2 * (angleDelta + 1) + i] > rdThresh && rdCost[2 * (angleDelta - 1) + i] > rdThresh;
                if (!skipSearch)
                {
                    mbmi.AngleDelta[1] = (sbyte)((1 - 2 * i) * angleDelta);
                    PickIntraAngleRoutineSbuv(cpi, x, bsize, rateOverhead, bestRd, ref rate, ref rdStats, ref bestAngleDelta, ref bestRd);
                }
            }

        mbmi.AngleDelta[1] = (sbyte)bestAngleDelta;
        return rdStats.Rate != int.MaxValue;
    }

    private static int PlaneSignToJointSign(int plane, int a, int b) => plane == CFL_PRED_U ? a * CFL_SIGNS + b - 1 : b * CFL_SIGNS + a - 1;

    /// <summary>cfl_idx_to_sign_and_alpha.</summary>
    private static void CflIdxToSignAndAlpha(int cflIdx, out int cflSign, out int cflAlpha)
    {
        int lin = cflIdx - CFL_INDEX_ZERO;
        if (lin == 0) { cflSign = CFL_SIGN_ZERO; cflAlpha = 0; }
        else { cflSign = lin > 0 ? CFL_SIGN_POS : CFL_SIGN_NEG; cflAlpha = AbsI(lin) - 1; }
    }

    /// <summary>cfl_compute_rd.</summary>
    private static long CflComputeRd(AomComp cpi, AomMacroblock x, int plane, int txSize, int planeBsize, int cflIdx, bool fastMode, ref AomRdStats rdStats)
    {
        var mbmi = x.E.Mi0;
        int cflPlane = plane - 1;
        CflIdxToSignAndAlpha(cflIdx, out int cflSign, out int cflAlpha);
        // only this plane's CfL is built; the other plane's sign is a dummy
        const int dummySign = CFL_SIGN_NEG;
        sbyte origSigns = mbmi.CflAlphaSigns;
        byte origIdx = mbmi.CflAlphaIdx;
        mbmi.CflAlphaSigns = (sbyte)PlaneSignToJointSign(cflPlane, cflSign, dummySign);
        mbmi.CflAlphaIdx = (byte)((cflAlpha << CFL_ALPHABET_SIZE_LOG2) + cflAlpha);
        long cflCost;
        if (fastMode) cflCost = IntraModelRd(cpi, x, plane, planeBsize, txSize, false);
        else
        {
            rdStats.Init();
            AomTxSearch.TxfmRdInPlane(x, cpi, ref rdStats, long.MaxValue, 0, plane, planeBsize, txSize, AomTxSearch.FTXS_NONE);
            rdStats.CostUpdate(x.Rdmult);
            cflCost = rdStats.Rdcost;
        }
        mbmi.CflAlphaSigns = origSigns;
        mbmi.CflAlphaIdx = origIdx;
        return cflCost;
    }

    private static readonly int[] CflDirLs = { 1, -1 };

    /// <summary>cfl_pick_plane_parameter.</summary>
    private static int CflPickPlaneParameter(AomComp cpi, AomMacroblock x, int plane, int txSize, int cflSearchRange)
    {
        if (cflSearchRange == CFL_MAGS_SIZE) return CFL_INDEX_ZERO;
        var xd = x.E;
        var pd = xd.Plane[plane];
        int planeBsize = AomEncodeMb.PlaneBlockSize(xd.Mi0.Bsize, pd.SubsamplingX, pd.SubsamplingY);
        int estBestCflIdx = CFL_INDEX_ZERO;
        const int startCflIdx = CFL_INDEX_ZERO;
        AomRdStats dummy = default;
        long bestCflCost = CflComputeRd(cpi, x, plane, txSize, planeBsize, startCflIdx, true, ref dummy);
        for (int si = 0; si < 2; ++si)
        {
            int dir = CflDirLs[si];
            for (int i = 1; i < CFL_MAGS_SIZE; ++i)
            {
                int cflIdx = startCflIdx + dir * i;
                if (cflIdx < 0 || cflIdx >= CFL_MAGS_SIZE) break;
                long cflCost = CflComputeRd(cpi, x, plane, txSize, planeBsize, cflIdx, true, ref dummy);
                if (cflCost < bestCflCost) { bestCflCost = cflCost; estBestCflIdx = cflIdx; }
                else break;
            }
        }
        return estBestCflIdx;
    }

    /// <summary>cfl_pick_plane_rd.</summary>
    private static void CflPickPlaneRd(AomComp cpi, AomMacroblock x, int plane, int txSize, int cflSearchRange, AomRdStats[] cflRdArr, int estBestCflIdx)
    {
        var xd = x.E;
        var pd = xd.Plane[plane];
        int planeBsize = AomEncodeMb.PlaneBlockSize(xd.Mi0.Bsize, pd.SubsamplingX, pd.SubsamplingY);
        for (int i = 0; i < CFL_MAGS_SIZE; ++i) cflRdArr[i].Invalidate();
        int startCflIdx = estBestCflIdx;
        CflComputeRd(cpi, x, plane, txSize, planeBsize, startCflIdx, false, ref cflRdArr[startCflIdx]);
        if (cflSearchRange == 1) return;
        for (int si = 0; si < 2; ++si)
        {
            int dir = CflDirLs[si];
            for (int i = 1; i < cflSearchRange; ++i)
            {
                int cflIdx = startCflIdx + dir * i;
                if (cflIdx < 0 || cflIdx >= CFL_MAGS_SIZE) break;
                CflComputeRd(cpi, x, plane, txSize, planeBsize, cflIdx, false, ref cflRdArr[cflIdx]);
            }
        }
    }


    /// <summary>cfl_rd_pick_alpha.</summary>
    private static bool CflRdPickAlpha(AomMacroblock x, AomComp cpi, int txSize, long refBestRd, int cflSearchRange, ref AomRdStats bestRdStats,
        ref byte bestCflAlphaIdx, ref sbyte bestCflAlphaSigns)
    {
        var mc = x.ModeCosts;
        var cflRdArrU = x.ScratchCflU ??= new AomRdStats[CFL_MAGS_SIZE];
        var cflRdArrV = x.ScratchCflV ??= new AomRdStats[CFL_MAGS_SIZE];
        var xd = x.E;
        bestRdStats.Invalidate();

        // the dc prediction is the same for every alpha: cached until clear_cfl_dc_pred_cache_flags
        xd.Cfl.UseDcPredCache = true;
        int estBestCflIdxU = CflPickPlaneParameter(cpi, x, 1, txSize, cflSearchRange);
        int estBestCflIdxV = CflPickPlaneParameter(cpi, x, 2, txSize, cflSearchRange);

        if (cflSearchRange == 1)
        {
            // no refinement: index 0 in both planes means no valid CfL mode
            if (estBestCflIdxU == CFL_INDEX_ZERO && estBestCflIdxV == CFL_INDEX_ZERO)
            {
                bestCflAlphaIdx = 0; bestCflAlphaSigns = 0;
                AomCfl.ClearCflDcPredCacheFlags(xd.Cfl);
                return false;
            }
            CflIdxToSignAndAlpha(estBestCflIdxU, out int cflSignU, out int cflAlphaU);
            CflIdxToSignAndAlpha(estBestCflIdxV, out int cflSignV, out int cflAlphaV);
            int jointSign = cflSignU * CFL_SIGNS + cflSignV - 1;
            int rateOverhead = mc.CflCost[(jointSign * 2 + CFL_PRED_U) * 16 + cflAlphaU] + mc.CflCost[(jointSign * 2 + CFL_PRED_V) * 16 + cflAlphaV] +
                mc.IntraUvModeCost[(AomCfl.IsCflAllowed(xd) * 13 + xd.Mi0.Mode) * 14 + UV_CFL_PRED];
            if (AomRd.RdCost(x.Rdmult, rateOverhead, 0) > refBestRd)
            {
                bestCflAlphaIdx = 0; bestCflAlphaSigns = 0;
                AomCfl.ClearCflDcPredCacheFlags(xd.Cfl);
                return false;
            }
        }

        CflPickPlaneRd(cpi, x, 1, txSize, cflSearchRange, cflRdArrU, estBestCflIdxU);
        CflPickPlaneRd(cpi, x, 2, txSize, cflSearchRange, cflRdArrV, estBestCflIdxV);
        AomCfl.ClearCflDcPredCacheFlags(xd.Cfl);

        for (int ui = 0; ui < CFL_MAGS_SIZE; ++ui)
        {
            if (cflRdArrU[ui].Rate == int.MaxValue) continue;
            CflIdxToSignAndAlpha(ui, out int cflSignU, out int cflAlphaU);
            for (int vi = 0; vi < CFL_MAGS_SIZE; ++vi)
            {
                if (cflRdArrV[vi].Rate == int.MaxValue) continue;
                CflIdxToSignAndAlpha(vi, out int cflSignV, out int cflAlphaV);
                if (cflSignU == CFL_SIGN_ZERO && cflSignV == CFL_SIGN_ZERO) continue;
                int jointSign = cflSignU * CFL_SIGNS + cflSignV - 1;
                AomRdStats rdStats = cflRdArrU[ui];
                rdStats.Merge(cflRdArrV[vi]);
                if (rdStats.Rate != int.MaxValue)
                {
                    rdStats.Rate += mc.CflCost[(jointSign * 2 + CFL_PRED_U) * 16 + cflAlphaU];
                    rdStats.Rate += mc.CflCost[(jointSign * 2 + CFL_PRED_V) * 16 + cflAlphaV];
                }
                rdStats.CostUpdate(x.Rdmult);
                if (rdStats.Rdcost < bestRdStats.Rdcost)
                {
                    bestRdStats = rdStats;
                    bestCflAlphaIdx = (byte)((cflAlphaU << CFL_ALPHABET_SIZE_LOG2) + cflAlphaV);
                    bestCflAlphaSigns = (sbyte)jointSign;
                }
            }
        }
        if (bestRdStats.Rdcost >= refBestRd)
        {
            bestRdStats.Invalidate();
            bestCflAlphaIdx = 0; bestCflAlphaSigns = 0;
            return false;
        }
        return true;
    }

    /// <summary>should_prune_chroma_smooth_pred_based_on_source_variance.</summary>
    private static bool ShouldPruneChromaSmoothPred(AomComp cpi, AomMacroblock x, int bsize)
    {
        if (!cpi.Sf.intra_sf.prune_smooth_intra_mode_for_chroma) return false;
        for (int i = 1; i < (cpi.Monochrome ? 1 : 3); i++)
        {
            var pd = x.E.Plane[i];
            int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            var src = x.Plane[i].Src;
            if (src.Buf16 != null)
            {
                if (AomEncodeFrame.PerpixelVariance(x, bsize, i) >= 20) return false;
                continue;
            }
            uint var = VarianceVsZero(src.Buf, src.Offset, src.Stride, BlockSizeWide[planeBsize], BlockSizeHigh[planeBsize], out _);
            int sh = NumPelsLog2Lookup[planeBsize];
            uint variance = (var + ((1u << sh) >> 1)) >> sh;
            if (variance >= 20) return false;
        }
        return true;
    }


    /// <summary>av1_rd_pick_intra_sbuv_mode.</summary>
    internal static long RdPickIntraSbuvMode(AomComp cpi, AomMacroblock x, ref int rate, ref int rateTokenonly, ref long distortion,
        ref byte skippable, int bsize, int maxTxSize)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var bestMbmi = x.ScratchBestUvMbmi ??= new AomMbModeInfo();
        bestMbmi.CopyFrom(mbmi);
        long bestRd = long.MaxValue;
        var mc = x.ModeCosts;
        var sf = cpi.Sf;

        // init_sbuv_mode
        mbmi.UvMode = UV_DC_PRED;
        mbmi.Palette.PaletteSize1 = 0;

        if (!xd.IsChromaRef)
        {
            rate = 0; rateTokenonly = 0; distortion = 0; skippable = 1;
            return long.MaxValue;
        }

        // the reconstructed luma is stored only with chroma RDO (else in encode_superblock)
        xd.Cfl.StoreY = StoreCflRequiredRdo(cpi, xd) ? 1 : 0;
        if (xd.Cfl.StoreY != 0)
        {
            EncodeIntraBlockPlane(cpi, x, mbmi.Bsize, 0, DRY_RUN_NORMAL, cpi.OptimizeSegArr[mbmi.SegmentId]);
            xd.Cfl.StoreY = 0;
        }
        bool dirModeSkipMaskReady = false;
        System.Runtime.CompilerServices.Unsafe.SkipInit(out StackArr14<byte> directionalModeSkipMaskBuf20); Span<byte> directionalModeSkipMask = directionalModeSkipMaskBuf20;
        directionalModeSkipMask.Clear();
        int cflAllowed = AomCfl.IsCflAllowed(xd);

        for (int modeIdx = 0; modeIdx < 14; ++modeIdx)
        {
            int thisRate = 0;
            AomRdStats tokenonlyRdStats = default;
            int uvMode = UvRdSearchModeOrder[modeIdx];

            // skip when the mode signalling alone exceeds best_rd
            int modeRate = mc.IntraUvModeCost[(cflAllowed * 13 + mbmi.Mode) * 14 + uvMode];
            if (AomRd.RdCost(x.Rdmult, modeRate, 0) > bestRd) continue;

            int intraMode = GetUvMode(uvMode);
            bool isDiagonalMode = IsDiagonalMode(intraMode);
            bool isDirectionalMode = IsDirectionalMode(intraMode);
            if (isDiagonalMode && !cpi.EnableDiagonalIntra) continue;
            if (isDirectionalMode && !cpi.EnableDirectionalIntra) continue;
            if ((sf.intra_sf.intra_uv_mode_mask[TxsizeSqrUpMap[maxTxSize]] & (1 << uvMode)) == 0) continue;
            if (!cpi.EnableSmoothIntra && uvMode >= UV_SMOOTH_PRED && uvMode <= UV_SMOOTH_H_PRED) continue;
            if (!cpi.EnablePaethIntra && uvMode == UV_PAETH_PRED) continue;
            if (sf.intra_sf.prune_chroma_modes_using_luma_winner != 0 && (DerivedChromaIntraModeUsedFlag[mbmi.Mode] & (1 << uvMode)) == 0) continue;

            mbmi.UvMode = uvMode;
            mbmi.AngleDelta[1] = 0;
            if (uvMode == UV_CFL_PRED)
            {
                if (cflAllowed == 0 || !cpi.EnableCflIntra) continue;
                int uvTxSize = AomEncodeMb.GetTxSize(1, xd);
                byte idx = mbmi.CflAlphaIdx; sbyte signs = mbmi.CflAlphaSigns;
                bool ok = CflRdPickAlpha(x, cpi, uvTxSize, bestRd, sf.intra_sf.cfl_search_range, ref tokenonlyRdStats, ref idx, ref signs);
                mbmi.CflAlphaIdx = idx; mbmi.CflAlphaSigns = signs;
                if (!ok) continue;
            }
            else if (isDirectionalMode && UseAngleDelta(mbmi.Bsize) && cpi.EnableAngleDelta)
            {
                if (sf.intra_sf.chroma_intra_pruning_with_hog != 0 && !dirModeSkipMaskReady)
                {
                    ReadOnlySpan<float> thresh = new float[] { -1.2f, 0.0f, 0.0f, 1.2f, -1.2f, -1.2f, -0.6f, 0.4f };   // [inter, intra][level]
                    PruneIntraModeWithHog(cpi, x, bsize, thresh[(cpi.FrameIsIntraOnly ? 4 : 0) + sf.intra_sf.chroma_intra_pruning_with_hog - 1],
                        directionalModeSkipMask, true);
                    dirModeSkipMaskReady = true;
                }
                if (directionalModeSkipMask[uvMode] != 0) continue;
                // Search through angle delta
                int rateOverhead = mc.IntraUvModeCost[(cflAllowed * 13 + mbmi.Mode) * 14 + uvMode];
                if (!RdPickIntraAngleSbuv(cpi, x, bsize, rateOverhead, bestRd, ref thisRate, ref tokenonlyRdStats)) continue;
            }
            else
            {
                if (uvMode == UV_SMOOTH_PRED && ShouldPruneChromaSmoothPred(cpi, x, bsize)) continue;
                // Predict directly if we don't need to search for angle delta.
                if (!AomTxSearch.TxfmUvrd(cpi, x, ref tokenonlyRdStats, bsize, bestRd)) continue;
            }
            int modeCost = mc.IntraUvModeCost[(cflAllowed * 13 + mbmi.Mode) * 14 + uvMode];
            thisRate = tokenonlyRdStats.Rate + IntraModeInfoCostUv(cpi, x, mbmi, bsize, modeCost);
            long thisRd = AomRd.RdCost(x.Rdmult, thisRate, tokenonlyRdStats.Dist);
            if (thisRd < bestRd)
            {
                bestMbmi.CopyFrom(mbmi);
                bestRd = thisRd;
                rate = thisRate;
                rateTokenonly = tokenonlyRdStats.Rate;
                distortion = tokenonlyRdStats.Dist;
                skippable = tokenonlyRdStats.SkipTxfm;
            }
        }

        bool tryPalette = cpi.EnablePalette && AllowPalette(cpi.AllowScreenContentTools, mbmi.Bsize);
        if (tryPalette)
            AomPalette.RdPickPaletteIntraSbuv(cpi, x, mc.IntraUvModeCost[(cflAllowed * 13 + mbmi.Mode) * 14 + UV_DC_PRED], x.BestPaletteColorMap,
                bestMbmi, ref bestRd, ref rate, ref rateTokenonly, ref distortion, ref skippable);

        mbmi.CopyFrom(bestMbmi);
        return bestRd;
    }

    /// <summary>store_cfl_required_rdo.</summary>
    internal static bool StoreCflRequiredRdo(AomComp cpi, AomMacroblockD xd)
    {
        if (cpi.Monochrome || !xd.IsChromaRef) return false;
        // chroma reference blocks store the luma iff CfL may be tried
        return AomCfl.IsCflAllowed(xd) != 0;
    }

    // ---- encodemb.c: av1_encode_intra_block_plane ----


    /// <summary>av1_encode_intra_block_plane: predict, transform, quantise (and trellis), reconstruct every tx block.</summary>
    internal static void EncodeIntraBlockPlane(AomComp cpi, AomMacroblock x, int bsize, int plane, int dryRun, int enableOptimizeB)
    {
        var xd = x.E;
        if (plane != 0 && !xd.IsChromaRef) return;
        var pd = xd.Plane[plane];
        var ta = x.ScratchTa;
        var tl = x.ScratchTl;
        Array.Clear(ta); Array.Clear(tl);
        int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        if (enableOptimizeB != 0) AomTxSearch.GetEntropyContexts(planeBsize, pd, ta, tl);

        int txSize = AomEncodeMb.GetTxSize(plane, xd);
        int txBsize = TxsizeToBsize[txSize];
        if (planeBsize == txBsize) { EncodeBlockIntraAndSetContext(cpi, x, plane, 0, 0, 0, planeBsize, txSize, ta, tl, dryRun, enableOptimizeB); return; }
        int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize], step = txwUnit * txhUnit;
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane);
        int maxUnitBsize = AomEncodeMb.PlaneBlockSize(BLOCK_64X64, pd.SubsamplingX, pd.SubsamplingY);
        int muBlocksWide = Math.Min(MiSizeWide[maxUnitBsize], maxBlocksWide);
        int muBlocksHigh = Math.Min(MiSizeHigh[maxUnitBsize], maxBlocksHigh);
        int i = 0;
        for (int r = 0; r < maxBlocksHigh; r += muBlocksHigh)
        {
            int unitHeight = Math.Min(muBlocksHigh + r, maxBlocksHigh);
            for (int c = 0; c < maxBlocksWide; c += muBlocksWide)
            {
                int unitWidth = Math.Min(muBlocksWide + c, maxBlocksWide);
                for (int blkRow = r; blkRow < unitHeight; blkRow += txhUnit)
                    for (int blkCol = c; blkCol < unitWidth; blkCol += txwUnit)
                    {
                        EncodeBlockIntraAndSetContext(cpi, x, plane, i, blkRow, blkCol, planeBsize, txSize, ta, tl, dryRun, enableOptimizeB);
                        i += step;
                    }
            }
        }
    }

    /// <summary>encode_block_intra + av1_set_txb_context.</summary>
    private static void EncodeBlockIntraAndSetContext(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize,
        int txSize, byte[] ta, byte[] tl, int dryRun, int enableOptimizeB)
    {
        var xd = x.E;
        var p = x.Plane[plane];
        var pd = xd.Plane[plane];
        int dstStride = pd.Dst.Stride;
        int dstOff = pd.Dst.Offset + ((blkRow * dstStride + blkCol) << 2);

        AomReconIntra.PredictIntraBlockFacade(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, plane, blkCol, blkRow, txSize);

        int txType = DCT_DCT;
        if (xd.Mi0.SkipTxfm != 0)
        {
            p.Eobs[block] = 0;
            p.TxbEntropyCtx[block] = 0;
        }
        else
        {
            AomEncodeMb.SubtractTxb(x, plane, planeBsize, blkCol, blkRow, txSize);
            txType = AomEncodeMb.GetTxType(xd, plane == 0 ? 0 : 1, blkRow, blkCol, txSize, cpi.ReducedTxSetUsed != 0);
            bool useTrellis = AomTxSearch.IsTrellisUsed(enableOptimizeB, dryRun);
            int quantIdx = useTrellis ? AomXformQuant.Fp : AomXformQuant.B;   // USE_B_QUANT_NO_TRELLIS
            var qp = AomEncodeMb.SetupQuant(txSize, useTrellis, quantIdx, cpi.QuantBAdapt);
            AomEncodeMb.SetupQmatrix(x, plane, txSize, txType, ref qp);
            AomEncodeMb.Xform(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType);
            AomEncodeMb.Quant(x, plane, block, txSize, txType, qp);
            if (useTrellis)
            {
                var txbCtx = AomTxb.TxbCtx(planeBsize, txSize, plane, ta.AsSpan(blkCol), tl.AsSpan(blkRow));
                AomEncodeMb.OptimizeB(cpi, x, plane, block, txSize, txType, txbCtx, out _);
            }
        }

        int eob = p.Eobs[block];
        if (eob != 0)
            AomEncodeMb.InverseTransformBlockDst(p.Dqcoeff, AomEncodeMb.BlockOffset(block), txType, txSize, pd.Dst, dstOff, dstStride, eob, xd.Bd, xd.Lossless[xd.Mi0.SegmentId] != 0);

        if (eob == 0 && plane == 0) AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, DCT_DCT);

        if (plane == 0 && xd.Cfl.StoreY != 0) AomCfl.CflStoreTx(xd, blkRow, blkCol, txSize, planeBsize);

        AomEncodeMb.SetTxbContext(x, plane, block, txSize, ta.AsSpan(blkCol), tl.AsSpan(blkRow));
    }
}
