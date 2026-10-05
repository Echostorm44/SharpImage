using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>LV_MAP_COEFF_COST (av1/encoder/block.h): one tx-size class / plane type's coefficient symbol costs.</summary>
internal sealed class AomLvMapCoeffCost
{
    public const int TxbSkipContexts = 13, SigCoefContextsEob = 4, SigCoefContexts = 42, EobCoefContexts = 9,
        DcSignContexts = 3, LevelContexts = 21, LpsStride = AomTxb.CoeffBaseRange + 1 + AomTxb.CoeffBaseRange + 1;
    public readonly int[] TxbSkip = new int[TxbSkipContexts * 2];        // [ctx][2]
    public readonly int[] BaseEob = new int[SigCoefContextsEob * 3];     // [ctx][3]
    public readonly int[] Base = new int[SigCoefContexts * 8];           // [ctx][8]
    public readonly int[] EobExtra = new int[EobCoefContexts * 2];       // [ctx][2]
    public readonly int[] DcSign = new int[DcSignContexts * 2];          // [ctx][2]
    public readonly int[] Lps = new int[LevelContexts * LpsStride];      // [ctx][28]
}

/// <summary>CoeffCosts (av1/encoder/block.h) and av1_fill_coeff_costs (rd.c) from the tile's coefficient CDFs.</summary>
internal sealed class AomCoeffCosts
{
    public readonly AomLvMapCoeffCost[] Coeff = new AomLvMapCoeffCost[5 * 2];   // [txs_ctx][plane_type]
    public readonly int[][] Eob = new int[7 * 2][];                               // [eob_multi_size][plane_type] -> [2][11]

    public AomCoeffCosts()
    {
        for (int i = 0; i < Coeff.Length; i++) Coeff[i] = new AomLvMapCoeffCost();
        for (int i = 0; i < Eob.Length; i++) Eob[i] = new int[2 * 11];
    }

    public AomLvMapCoeffCost Get(int txsCtx, int planeType) => Coeff[txsCtx * 2 + planeType];
    public int[] GetEob(int eobMultiSize, int planeType) => Eob[eobMultiSize * 2 + planeType];

    /// <summary>av1_fill_coeff_costs.</summary>
    public void Fill(Av1CdfCoefContext fc, int numPlanes)
    {
        int nplanes = Math.Min(numPlanes, 2);
        Span<int> tmp = stackalloc int[16];
        for (int ems = 0; ems < 7; ems++)
            for (int plane = 0; plane < nplanes; plane++)
            {
                var pcost = GetEob(ems, plane);
                for (int ctx = 0; ctx < 2; ctx++)
                {
                    ushort[] cdf = ems switch
                    {
                        0 => fc.EobBin16[plane * 2 + ctx], 1 => fc.EobBin32[plane * 2 + ctx], 2 => fc.EobBin64[plane * 2 + ctx],
                        3 => fc.EobBin128[plane * 2 + ctx], 4 => fc.EobBin256[plane * 2 + ctx], 5 => fc.EobBin512[plane], _ => fc.EobBin1024[plane],
                    };
                    // eob_flag_cdf512 / 1024 have no 1D context in libaom's layout either (ctx 1 = the same CDF)
                    AomCost.CostTokensFromCdf(pcost.AsSpan(ctx * 11), cdf, ems + 5);
                }
            }
        for (int txSize = 0; txSize < 5; txSize++)
            for (int plane = 0; plane < nplanes; plane++)
            {
                var pcost = Get(txSize, plane);
                for (int ctx = 0; ctx < AomLvMapCoeffCost.TxbSkipContexts; ctx++)
                    AomCost.CostTokensFromCdf(pcost.TxbSkip.AsSpan(ctx * 2), fc.CoefSkip[txSize * 13 + ctx], 2);
                for (int ctx = 0; ctx < AomLvMapCoeffCost.SigCoefContextsEob; ctx++)
                    AomCost.CostTokensFromCdf(pcost.BaseEob.AsSpan(ctx * 3), fc.EobBaseTok[(txSize * 2 + plane) * 4 + ctx], 3);
                for (int ctx = 0; ctx < AomLvMapCoeffCost.SigCoefContexts; ctx++)
                {
                    // libaom's SIG_COEF_CONTEXTS = 42 contexts; the last is never used (the decoder tables stop at 41)
                    if (ctx < 41) AomCost.CostTokensFromCdf(pcost.Base.AsSpan(ctx * 8), fc.BaseTok[(txSize * 2 + plane) * 41 + ctx], 4);
                    else pcost.Base.AsSpan(ctx * 8, 4).Clear();
                }
                for (int ctx = 0; ctx < AomLvMapCoeffCost.SigCoefContexts; ctx++)
                {
                    var b = pcost.Base.AsSpan(ctx * 8);
                    b[4] = 0;
                    b[5] = b[1] + AomCost.CostLiteral(1) - b[0];
                    b[6] = b[2] - b[1];
                    b[7] = b[3] - b[2];
                }
                for (int ctx = 0; ctx < AomLvMapCoeffCost.EobCoefContexts; ctx++)
                    AomCost.CostTokensFromCdf(pcost.EobExtra.AsSpan(ctx * 2), fc.EobHiBit[(txSize * 2 + plane) * 9 + ctx], 2);
                for (int ctx = 0; ctx < AomLvMapCoeffCost.DcSignContexts; ctx++)
                    AomCost.CostTokensFromCdf(pcost.DcSign.AsSpan(ctx * 2), fc.DcSign[plane * 3 + ctx], 2);
                const int stride = AomLvMapCoeffCost.LpsStride;
                for (int ctx = 0; ctx < AomLvMapCoeffCost.LevelContexts; ctx++)
                {
                    Span<int> brRate = tmp.Slice(0, 4);
                    AomCost.CostTokensFromCdf(brRate, fc.BrTok[(Math.Min(txSize, 3) * 2 + plane) * 21 + ctx], 4);
                    var lps = pcost.Lps.AsSpan(ctx * stride);
                    int prevCost = 0, i, j = 0;
                    for (i = 0; i < AomTxb.CoeffBaseRange; i += 4 - 1)
                    {
                        for (j = 0; j < 4 - 1; j++) lps[i + j] = prevCost + brRate[j];
                        prevCost += brRate[j];
                    }
                    lps[i] = prevCost;
                }
                for (int ctx = 0; ctx < AomLvMapCoeffCost.LevelContexts; ctx++)
                {
                    var lps = pcost.Lps.AsSpan(ctx * stride);
                    lps[AomTxb.CoeffBaseRange + 1] = lps[0];
                    for (int i = 1; i <= AomTxb.CoeffBaseRange; i++)
                        lps[i + AomTxb.CoeffBaseRange + 1] = lps[i] - lps[i - 1];
                }
            }
    }
}

/// <summary>TXB_CTX (av1/common/txb_common.h).</summary>
internal struct AomTxbCtx
{
    public int TxbSkipCtx, DcSignCtx;
}

// Port of libaom av1/common/txb_common.{h,c}, av1/encoder/encodetxb.c (the level map, contexts, eob tokens, entropy
// context) and av1/encoder/txb_rdopt.{c,h} (av1_cost_coeffs_txb, av1_cost_coeffs_txb_laplacian, av1_optimize_txb).
// Coefficient arrays in libaom's layout: index = col * height + row (column-major, height = the adjusted tx's).
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static class AomTxb
{
    internal interface ITxClassConst { static abstract int Cls { get; } }
    internal struct TxClass2D : ITxClassConst { public static int Cls => TX_CLASS_2D; }
    internal struct TxClassHoriz : ITxClassConst { public static int Cls => TX_CLASS_HORIZ; }
    internal struct TxClassVert : ITxClassConst { public static int Cls => TX_CLASS_VERT; }

    internal const int NumBaseLevels = 2, CoeffBaseRange = 12, MaxBaseBrRange = CoeffBaseRange + NumBaseLevels + 1;
    internal const int CoeffContextBits = 3, CoeffContextMask = 7;
    internal const int TxPadHorLog2 = 2, TxPadHor = 4, TxPadTop = 0, TxPadBottom = 4, TxPadEnd = 16;
    internal const int TxPad2d = (32 + TxPadHor) * (32 + TxPadTop + TxPadBottom) + TxPadEnd;
    private const int NzMapCtx0 = 26, NzMapCtx5 = NzMapCtx0 + 5, NzMapCtx10 = NzMapCtx0 + 10;

    // tx_type_to_class
    internal static readonly byte[] TxTypeToClass =
    {
        TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_2D,
        TX_CLASS_2D, TX_CLASS_2D, TX_CLASS_VERT, TX_CLASS_HORIZ, TX_CLASS_VERT, TX_CLASS_HORIZ, TX_CLASS_VERT, TX_CLASS_HORIZ,
    };

    // plane_rd_mult / plane_rd_mult_chroma [is_inter][plane_type]
    private static readonly int[] PlaneRdMult = { 17, 20, 16, 20 };
    private static readonly int[] PlaneRdMultChroma = { 17, 13, 16, 10 };

    private static readonly sbyte[][] NzMapCtxOffset =
    {
        NzMapCtxOffset4x4, NzMapCtxOffset8x8, NzMapCtxOffset16x16, NzMapCtxOffset32x32, NzMapCtxOffset32x32,
        NzMapCtxOffset4x8, NzMapCtxOffset16x4, NzMapCtxOffset8x16, NzMapCtxOffset32x8, NzMapCtxOffset16x32,
        NzMapCtxOffset32x16, NzMapCtxOffset32x64, NzMapCtxOffset64x32, NzMapCtxOffset4x16, NzMapCtxOffset16x4,
        NzMapCtxOffset8x32, NzMapCtxOffset32x8, NzMapCtxOffset32x64, NzMapCtxOffset32x16,
    };

    private static readonly int[] NzMapCtxOffset1d =
    {
        NzMapCtx0, NzMapCtx5, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10,
        NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10,
        NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10, NzMapCtx10,
        NzMapCtx10, NzMapCtx10,
    };

    private static readonly int[] GolombBitsCost =
    {
        0, 512, 512 * 3, 512 * 3, 512 * 5, 512 * 5, 512 * 5, 512 * 5, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7,
        512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9,
    };
    private static readonly int[] GolombCostDiff =
    {
        0, 512, 512 * 2, 0, 512 * 2, 0, 0, 0, 512 * 2, 0, 0, 0, 0, 0, 0, 0,
        512 * 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };
    // costLUT / const_term / loge_par (txb_rdopt_utils.h)
    private static readonly int[] CostLut = { -1143, 53, 545, 825, 1031, 1209, 1393, 1577, 1763, 1947, 2132, 2317, 2501, 2686, 2871 };
    private const int ConstTerm = 1 << AomCost.ProbCostShift;
    private const int LogePar = ((14427 << AomCost.ProbCostShift) + 5000) / 10000;

    // clip_max3
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static int ClipMax3(int v) { int d = v - 3; return v - (d & ~(d >> 31)); }

    /// <summary>av1_get_adjusted_tx_size: 64-point axes as 32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AdjustedTxSize(int txSize) => txSize switch
    {
        TX_64X64 or TX_64X32 or TX_32X64 => TX_32X32,
        TX_16X64 => TX_16X32,
        TX_64X16 => TX_32X16,
        _ => txSize,
    };
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static int TxbBhl(int txSize) => TxSizeHighLog2[AdjustedTxSize(txSize)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static int TxbWide(int txSize) => TxSizeWide[AdjustedTxSize(txSize)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static int TxbHigh(int txSize) => TxSizeHigh[AdjustedTxSize(txSize)];
    /// <summary>get_txsize_entropy_ctx.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TxsizeEntropyCtx(int txSize) => (TxsizeSqrMap[txSize] + TxsizeSqrUpMap[txSize] + 1) >> 1;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PaddedIdx(int idx, int bhl) => idx + ((idx >> bhl) << TxPadHorLog2);

    /// <summary>av1_txb_init_levels: |coeff| clipped to 127 into the column-padded map (stride height + 4), the rows
    /// past the last column and the tail zeroed.</summary>
    internal static void InitLevels(ReadOnlySpan<int> coeff, int width, int height, Span<byte> levels)
    {
        int stride = height + TxPadHor;
        {
            // the padding past the last column (48..160 bytes): overlapping vector stores, not a Clear call
            var tail = levels.Slice(stride * width, TxPadBottom * stride + TxPadEnd);
            ref byte t0 = ref MemoryMarshal.GetReference(tail);
            int n = tail.Length;
            for (int k = 0; k + 32 < n; k += 32) Vector256<byte>.Zero.StoreUnsafe(ref t0, (nuint)k);
            Vector256<byte>.Zero.StoreUnsafe(ref t0, (nuint)(n - 32));
        }
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref byte l0 = ref MemoryMarshal.GetReference(levels);
        var max = Vector256.Create(127);
        if (height >= 8)
        {
            for (int i = 0; i < width; i++)
            {
                ref int ci = ref Unsafe.Add(ref c0, i * height);
                ref byte li = ref Unsafe.Add(ref l0, i * stride);
                for (int j = 0; j < height; j += 8)
                {
                    // min(|c|, 127) in 8 int lanes, narrowed to 8 bytes (values fit, so truncation is exact)
                    var a = Vector256.Min(Vector256.Abs(Vector256.LoadUnsafe(ref ci, (nuint)j)), max);
                    var s16 = Vector256.Narrow(a, Vector256<int>.Zero);
                    var s8 = Vector256.Narrow(s16, Vector256<short>.Zero);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref li, j), s8.AsUInt64().GetElement(0));
                }
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref li, height), 0u);   // TX_PAD_HOR
            }
            return;
        }
        // height 4: two columns (8 coefficients) per vector, each column's 4 levels followed by its 4 pad zeros
        if ((uint)(width * 4) > (uint)coeff.Length || (uint)(width * 8) > (uint)levels.Length) throw new ArgumentException("levels buffer");
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    int c = Unsafe.Add(ref c0, i * 4 + j);
                    Unsafe.Add(ref l0, i * 8 + j) = (byte)Math.Min(c < 0 ? -c : c, 127);
                }
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref l0, i * 8 + 4), 0u);
            }
            return;
        }
        var zero = Vector256<int>.Zero;
        for (int i = 0; i < width; i += 2)
        {
            var a = Vector256.Min(Vector256.Abs(Vector256.LoadUnsafe(ref c0, (nuint)(i * 4))), max);
            // lanes 0-3 -> bytes 0-3, lanes 4-7 -> bytes 8-11 (pads zero)
            var s16 = System.Runtime.Intrinsics.X86.Avx2.PackSignedSaturate(a, zero);                    // per 128-bit half: 4 shorts, 4 zeros
            var s8 = System.Runtime.Intrinsics.X86.Avx2.PackUnsignedSaturate(s16, Vector256<short>.Zero); // per half: 4 bytes, 12 zeros
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref l0, i * 8), s8.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref l0, i * 8 + 8), s8.AsUInt64().GetElement(2));
        }
    }

    /// <summary>av1_get_eob_pos_token.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int EobPosToken(int eob, out int extra)
    {
        int t = eob < 33 ? EobToPosSmall[eob] : EobToPosLarge[Math.Min((eob - 1) >> 5, 16)];
        extra = eob - EobGroupStart[t];
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NzMag(ReadOnlySpan<byte> levels, int p, int bhl, int txClass)
        => NzMag(ref Unsafe.Add(ref MemoryMarshal.GetReference(levels), p), bhl, txClass);

    /// <summary>The neighbour magnitude sum at l (levels is the padded TxPad2d map: every neighbour of an in-block
    /// position lies inside it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NzMag(ref byte l, int bhl, int txClass)
    {
        int stride = (1 << bhl) + TxPadHor;
        int mag = ClipMax3(Unsafe.Add(ref l, stride)) + ClipMax3(Unsafe.Add(ref l, 1));
        if (txClass == TX_CLASS_2D)
        {
            mag += ClipMax3(Unsafe.Add(ref l, stride + 1));
            mag += ClipMax3(Unsafe.Add(ref l, 2 * stride));
            mag += ClipMax3(Unsafe.Add(ref l, 2));
        }
        else if (txClass == TX_CLASS_VERT)
        {
            mag += ClipMax3(Unsafe.Add(ref l, 2)) + ClipMax3(Unsafe.Add(ref l, 3)) + ClipMax3(Unsafe.Add(ref l, 4));
        }
        else
        {
            mag += ClipMax3(Unsafe.Add(ref l, 2 * stride));
            mag += ClipMax3(Unsafe.Add(ref l, 3 * stride));
            mag += ClipMax3(Unsafe.Add(ref l, 4 * stride));
        }
        return mag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NzMapCtxFromStats(int stats, int coeffIdx, int bhl, int txSize, int txClass)
    {
        if ((txClass | coeffIdx) == 0) return 0;
        int ctx = Math.Min((stats + 1) >> 1, 4);
        switch (txClass)
        {
            case TX_CLASS_2D: return ctx + NzMapCtxOffset[txSize][coeffIdx];
            case TX_CLASS_HORIZ: return ctx + NzMapCtxOffset1d[coeffIdx >> bhl];
            default:
                {
                    int col = coeffIdx >> bhl;
                    return ctx + NzMapCtxOffset1d[coeffIdx - (col << bhl)];
                }
        }
    }

    /// <summary>get_lower_levels_ctx_eob.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LowerLevelsCtxEob(int bhl, int width, int scanIdx)
    {
        if (scanIdx == 0) return 0;
        if (scanIdx <= (width << bhl) / 8) return 1;
        if (scanIdx <= (width << bhl) / 4) return 2;
        return 3;
    }

    /// <summary>get_lower_levels_ctx.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LowerLevelsCtx(ReadOnlySpan<byte> levels, int coeffIdx, int bhl, int txSize, int txClass)
        => NzMapCtxFromStats(NzMag(levels, PaddedIdx(coeffIdx, bhl), bhl, txClass), coeffIdx, bhl, txSize, txClass);

    /// <summary>get_br_ctx_eob.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BrCtxEob(int c, int bhl, int txClass)
    {
        int col = c >> bhl, row = c - (col << bhl);
        if (c == 0) return 0;
        if ((txClass == TX_CLASS_2D && row < 2 && col < 2) || (txClass == TX_CLASS_HORIZ && col == 0) || (txClass == TX_CLASS_VERT && row == 0))
            return 7;
        return 14;
    }

    /// <summary>get_br_ctx.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int BrCtx(ReadOnlySpan<byte> levels, int c, int bhl, int txClass)
        => BrCtx(ref MemoryMarshal.GetReference(levels), c, bhl, txClass);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BrCtx(ref byte levels, int c, int bhl, int txClass)
    {
        int col = c >> bhl, row = c - (col << bhl);
        int stride = (1 << bhl) + TxPadHor;
        ref byte l = ref Unsafe.Add(ref levels, col * stride + row);
        int mag = Unsafe.Add(ref l, 1) + Unsafe.Add(ref l, stride);
        switch (txClass)
        {
            case TX_CLASS_2D:
                mag += Unsafe.Add(ref l, stride + 1);
                mag = Math.Min((mag + 1) >> 1, 6);
                if (c == 0) return mag;
                if (row < 2 && col < 2) return mag + 7;
                break;
            case TX_CLASS_HORIZ:
                mag += Unsafe.Add(ref l, stride << 1);
                mag = Math.Min((mag + 1) >> 1, 6);
                if (c == 0) return mag;
                if (col == 0) return mag + 7;
                break;
            default:
                mag += Unsafe.Add(ref l, 2);
                mag = Math.Min((mag + 1) >> 1, 6);
                if (c == 0) return mag;
                if (row == 0) return mag + 7;
                break;
        }
        return mag + 14;
    }

    /// <summary>get_eob_cost.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EobCost(int eob, int[] txbEobCosts, AomLvMapCoeffCost txbCosts, int txClass)
    {
        int eobPt = EobPosToken(eob, out int eobExtra);
        int eobMultiCtx = txClass == TX_CLASS_2D ? 0 : 1;
        int cost = txbEobCosts[eobMultiCtx * 11 + eobPt - 1];
        if (EobOffsetBits[eobPt] > 0)
        {
            int eobCtx = eobPt - 3, eobShift = EobOffsetBits[eobPt] - 1;
            int bit = (eobExtra & (1 << eobShift)) != 0 ? 1 : 0;
            cost += txbCosts.EobExtra[eobCtx * 2 + bit];
            int offsetBits = EobOffsetBits[eobPt];
            if (offsetBits > 1) cost += AomCost.CostLiteral(offsetBits - 1);
        }
        return cost;
    }

    /// <summary>get_golomb_cost.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GolombCost(int absQc)
    {
        if (absQc >= 1 + NumBaseLevels + CoeffBaseRange)
        {
            int r = absQc - CoeffBaseRange - NumBaseLevels;
            int length = BitOperations.Log2((uint)r) + 1;
            return AomCost.CostLiteral(2 * length - 1);
        }
        return 0;
    }

    /// <summary>get_br_cost.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BrCost(int level, ReadOnlySpan<int> lps) => lps[Math.Min(level - 1 - NumBaseLevels, CoeffBaseRange)] + GolombCost(level);

    /// <summary>av1_cost_coeffs_txb (warehouse_efficients_txb): the coded bits (1/512) of a tx block's levels with the
    /// given contexts; txTypeCost = get_tx_type_cost's value for this block.</summary>
    internal static int CostCoeffsTxb(AomCoeffCosts costs, int txSize, int txType, int planeType, AomTxbCtx txbCtx,
        ReadOnlySpan<int> qcoeff, int eob, int txTypeCost, ReadOnlySpan<ushort> scan)
    {
        int bhl0 = TxbBhl(txSize), width0 = TxbWide(txSize), height0 = TxbHigh(txSize);
        if (eob == 0 || qcoeff.Length < width0 * height0 || scan.Length < width0 * height0 || (uint)eob > (uint)(width0 * height0))
            return CostCoeffsTxbRef(costs, txSize, txType, planeType, txbCtx, qcoeff, eob, txTypeCost, scan);
        return TxTypeToClass[txType] switch
        {
            TX_CLASS_2D => CostCoeffsTxbT<TxClass2D>(costs, txSize, txType, planeType, txbCtx, qcoeff, eob, txTypeCost, scan),
            TX_CLASS_HORIZ => CostCoeffsTxbT<TxClassHoriz>(costs, txSize, txType, planeType, txbCtx, qcoeff, eob, txTypeCost, scan),
            _ => CostCoeffsTxbT<TxClassVert>(costs, txSize, txType, planeType, txbCtx, qcoeff, eob, txTypeCost, scan),
        };
    }

    /// <summary>warehouse_efficients_txb specialised per tx class: the nz-map contexts computed where they are used (the
    /// levels are fixed while costing, so av1_get_nz_map_contexts' array is not needed), unchecked reads (the caller
    /// checked eob and the buffers against the tx size).</summary>
    [SkipLocalsInit]
    private static int CostCoeffsTxbT<TC>(AomCoeffCosts costs, int txSize, int txType, int planeType, AomTxbCtx txbCtx,
        ReadOnlySpan<int> qcoeff, int eob, int txTypeCost, ReadOnlySpan<ushort> scan) where TC : struct, ITxClassConst
    {
        int txClass = TC.Cls;
        var coeffCosts = costs.Get(TxsizeEntropyCtx(txSize), planeType);
        int bhl = TxbBhl(txSize), width = TxbWide(txSize), height = TxbHigh(txSize);
        Unsafe.SkipInit(out LevelsBuf levelsBuf);
        Span<byte> levels = levelsBuf;
        var eobCosts = costs.GetEob(TxsizeLog2Minus4[txSize], planeType);
        int cost = coeffCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 0];
        if (eob > 1) InitLevels(qcoeff, width, height, levels);
        cost += txTypeCost;
        cost += EobCost(eob, eobCosts, coeffCosts, txClass);
        ref ushort sc = ref MemoryMarshal.GetReference(scan);
        ref int q = ref MemoryMarshal.GetReference(qcoeff);
        ref int baseC = ref MemoryMarshal.GetArrayDataReference(coeffCosts.Base);
        ref int lps = ref MemoryMarshal.GetArrayDataReference(coeffCosts.Lps);
        ref byte lv = ref MemoryMarshal.GetReference(levels);
        const int lpsStride = AomLvMapCoeffCost.LpsStride;
        int c = eob - 1;
        {
            int pos = Unsafe.Add(ref sc, c);
            int v = Unsafe.Add(ref q, pos);
            if (v != 0)
            {
                int sign = v >> 31, level = (v ^ sign) - sign;
                int coeffCtx = LowerLevelsCtxEob(bhl, width, c);
                cost += coeffCosts.BaseEob[coeffCtx * 3 + Math.Min(level, 3) - 1];
                if (level > NumBaseLevels)
                    cost += Unsafe.Add(ref lps, BrCtxEob(pos, bhl, txClass) * lpsStride + Math.Min(level - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(level);
                if (c != 0) cost += AomCost.CostLiteral(1);
                else
                {
                    int sign01 = (sign ^ sign) - sign;
                    cost += coeffCosts.DcSign[txbCtx.DcSignCtx * 2 + sign01];
                    return cost;
                }
            }
        }
        for (c = eob - 2; c >= 1; --c)
        {
            int pos = Unsafe.Add(ref sc, c);
            int coeffCtx = LowerLevelsCtx(levels, pos, bhl, txSize, txClass);
            int v = Unsafe.Add(ref q, pos);
            if (v == 0) { cost += Unsafe.Add(ref baseC, coeffCtx * 8); continue; }
            int level = AbsI(v);
            cost += Unsafe.Add(ref baseC, coeffCtx * 8 + Math.Min(level, 3));
            cost += AomCost.CostLiteral(1);
            if (level > NumBaseLevels)
                cost += Unsafe.Add(ref lps, BrCtx(ref lv, pos, bhl, txClass) * lpsStride + Math.Min(level - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(level);
        }
        {
            int pos = Unsafe.Add(ref sc, c);
            int v = Unsafe.Add(ref q, pos);
            int coeffCtx = c == eob - 1 ? LowerLevelsCtxEob(bhl, width, c) : LowerLevelsCtx(levels, pos, bhl, txSize, txClass);
            if (v == 0) cost += Unsafe.Add(ref baseC, coeffCtx * 8);
            else
            {
                int sign = v >> 31, level = (v ^ sign) - sign;
                cost += Unsafe.Add(ref baseC, coeffCtx * 8 + Math.Min(level, 3));
                int sign01 = (sign ^ sign) - sign;
                cost += coeffCosts.DcSign[txbCtx.DcSignCtx * 2 + sign01];
                if (level > NumBaseLevels)
                    cost += Unsafe.Add(ref lps, BrCtx(ref lv, pos, bhl, txClass) * lpsStride + Math.Min(level - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(level);
            }
        }
        return cost;
    }

    /// <summary>The reference warehouse_efficients_txb (eob 0, or buffers not checked by the fast path).</summary>
    [SkipLocalsInit]   // libaom's levels_buf / coeff_contexts are uninitialized stack arrays filled before use
    private static int CostCoeffsTxbRef(AomCoeffCosts costs, int txSize, int txType, int planeType, AomTxbCtx txbCtx,
        ReadOnlySpan<int> qcoeff, int eob, int txTypeCost, ReadOnlySpan<ushort> scan)
    {
        int txsCtx = TxsizeEntropyCtx(txSize);
        var coeffCosts = costs.Get(txsCtx, planeType);
        if (eob == 0) return coeffCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 1];
        int txClass = TxTypeToClass[txType];
        int bhl = TxbBhl(txSize), width = TxbWide(txSize), height = TxbHigh(txSize);
        Unsafe.SkipInit(out LevelsBuf levelsBuf);
        Span<byte> levels = levelsBuf;
        Unsafe.SkipInit(out StackArr1024<sbyte> ctxBuf);
        Span<sbyte> coeffContexts = ctxBuf;
        var eobCosts = costs.GetEob(TxsizeLog2Minus4[txSize], planeType);
        int cost = coeffCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 0];
        if (eob > 1) InitLevels(qcoeff, width, height, levels);
        cost += txTypeCost;
        cost += EobCost(eob, eobCosts, coeffCosts, txClass);
        // av1_get_nz_map_contexts
        for (int i = 0; i < eob; i++)
        {
            int pos = scan[i];
            coeffContexts[pos] = (sbyte)(i == eob - 1 ? LowerLevelsCtxEob(bhl, width, i) : LowerLevelsCtx(levels, pos, bhl, txSize, txClass));
        }
        const int lpsStride = AomLvMapCoeffCost.LpsStride;
        int c = eob - 1;
        {
            int pos = scan[c];
            int v = qcoeff[pos];
            if (v != 0)
            {
                int sign = v >> 31, level = (v ^ sign) - sign;
                int coeffCtx = coeffContexts[pos];
                cost += coeffCosts.BaseEob[coeffCtx * 3 + Math.Min(level, 3) - 1];
                if (level > NumBaseLevels)
                    cost += BrCost(level, coeffCosts.Lps.AsSpan(BrCtxEob(pos, bhl, txClass) * lpsStride));
                if (c != 0) cost += AomCost.CostLiteral(1);
                else
                {
                    int sign01 = (sign ^ sign) - sign;
                    cost += coeffCosts.DcSign[txbCtx.DcSignCtx * 2 + sign01];
                    return cost;
                }
            }
        }
        for (c = eob - 2; c >= 1; --c)
        {
            int pos = scan[c];
            int coeffCtx = coeffContexts[pos];
            int v = qcoeff[pos];
            if (v == 0) { cost += coeffCosts.Base[coeffCtx * 8]; continue; }
            int level = AbsI(v);
            cost += coeffCosts.Base[coeffCtx * 8 + Math.Min(level, 3)];
            cost += AomCost.CostLiteral(1);
            if (level > NumBaseLevels)
                cost += BrCost(level, coeffCosts.Lps.AsSpan(BrCtx(levels, pos, bhl, txClass) * lpsStride));
        }
        {
            int pos = scan[c];
            int v = qcoeff[pos];
            int coeffCtx = coeffContexts[pos];
            if (v == 0) cost += coeffCosts.Base[coeffCtx * 8];
            else
            {
                int sign = v >> 31, level = (v ^ sign) - sign;
                cost += coeffCosts.Base[coeffCtx * 8 + Math.Min(level, 3)];
                int sign01 = (sign ^ sign) - sign;
                cost += coeffCosts.DcSign[txbCtx.DcSignCtx * 2 + sign01];
                if (level > NumBaseLevels)
                    cost += BrCost(level, coeffCosts.Lps.AsSpan(BrCtx(levels, pos, bhl, txClass) * lpsStride));
            }
        }
        return cost;
    }

    /// <summary>update_coeff_eob_fast (txb_rdopt_utils.h): trailing levels whose coefficient is inside the enlarged zbin
    /// dropped. Returns the new eob.</summary>
    internal static int UpdateCoeffEobFast(int eob, int shift, short dequant0, short dequant1, ReadOnlySpan<ushort> scan,
        ReadOnlySpan<int> coeff, Span<int> qcoeff, Span<int> dqcoeff)
    {
        int eobOut = eob;
        int zbin0 = dequant0 + ((dequant0 * 70 + 64) >> 7), zbin1 = dequant1 + ((dequant1 * 70 + 64) >> 7);
        for (int i = eob - 1; i >= 0; i--)
        {
            int rc = scan[i];
            int qc = qcoeff[rc], c = coeff[rc];
            int sign = c >> 31;
            long abs = (c ^ sign) - sign;
            if ((abs << (1 + shift)) < (rc != 0 ? zbin1 : zbin0) || qc == 0)
            {
                eobOut--;
                qcoeff[rc] = 0;
                dqcoeff[rc] = 0;
            }
            else break;
        }
        return eobOut;
    }

    /// <summary>av1_cost_coeffs_txb_laplacian: the tx-type and eob costs plus av1_cost_coeffs_txb_estimate (luma only);
    /// with adjustEob the trailing near-zero levels are dropped first (update_coeff_eob_fast). Returns the cost and the
    /// (possibly reduced) eob.</summary>
    internal static int CostCoeffsTxbLaplacian(AomCoeffCosts costs, int txSize, int txType, int planeType, AomTxbCtx txbCtx,
        ReadOnlySpan<int> coeff, Span<int> qcoeff, Span<int> dqcoeff, ref int eob, int txTypeCost, ReadOnlySpan<ushort> scan,
        bool adjustEob, short dequant0, short dequant1)
    {
        if (adjustEob) eob = UpdateCoeffEobFast(eob, AomQuantize.TxScale(txSize), dequant0, dequant1, scan, coeff, qcoeff, dqcoeff);
        var coeffCosts = costs.Get(TxsizeEntropyCtx(txSize), planeType);
        if (eob == 0) return coeffCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 1];
        int txClass = TxTypeToClass[txType];
        int cost = coeffCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 0];
        cost += txTypeCost;
        cost += EobCost(eob, costs.GetEob(TxsizeLog2Minus4[txSize], planeType), coeffCosts, txClass);
        // av1_cost_coeffs_txb_estimate
        int est = 0;
        int c = eob - 1;
        est += (AbsI(qcoeff[scan[c]]) - 1) << (AomCost.ProbCostShift + 2);
        for (c = eob - 2; c >= 0; c--) est += CostLut[Math.Min(AbsI(qcoeff[scan[c]]), 14)];
        est += (ConstTerm + LogePar) * (eob - 1);
        return cost + est;
    }

    /// <summary>av1_optimize_txb: libaom's trellis on one tx block's levels (qcoeff / dqcoeff updated in place, tcoeff
    /// the transform coefficients). rdmult: x->rdmult; txTypeCost: get_tx_type_cost. Returns the new eob; rate: the
    /// block's coded bits (1/512).</summary>
    internal static int OptimizeTxb(AomCoeffCosts costs, int txSize, int txType, int planeType, bool isInter, AomTxbCtx txbCtx,
        ReadOnlySpan<int> tcoeff, Span<int> qcoeff, Span<int> dqcoeff, int eob, short dequant0, short dequant1,
        int rdmultIn, int bitDepth, int sharpness, bool useChromaTrellisRdMult, bool tuneIq, int txTypeCost,
        ReadOnlySpan<ushort> scan, out int rateCost, byte[]? iqmatrix = null, byte[]? qmatrix = null)
    {
        var a = new ExplicitTrellisArgs
        {
            Costs = costs, TxSize = txSize, TxType = txType, PlaneType = planeType, IsInter = isInter, TxbCtx = txbCtx,
            Tcoeff = tcoeff, Qcoeff = qcoeff, Dqcoeff = dqcoeff, Eob = eob, Dequant0 = dequant0, Dequant1 = dequant1,
            RdmultIn = rdmultIn, BitDepth = bitDepth, Sharpness = sharpness, UseChromaTrellisRdMult = useChromaTrellisRdMult,
            TuneIq = tuneIq, TxTypeCost = txTypeCost, Scan = scan, Iqmatrix = iqmatrix, Qmatrix = qmatrix,
        };
        return OptimizeTxbCore(ref a, out rateCost);
    }

    /// <summary>av1_optimize_txb's arguments. The encoder passes a view of the MACROBLOCK whose members compute each
    /// argument where the trellis reads it (a 22-argument call spilled and copied its spans on every block); the
    /// explicit form serves the twin tests.</summary>
    internal interface ITrellisArgs
    {
        AomCoeffCosts Costs { get; }
        int TxSize { get; }
        int TxType { get; }
        int PlaneType { get; }
        bool IsInter { get; }
        AomTxbCtx TxbCtx { get; }
        ReadOnlySpan<int> Tcoeff { get; }
        Span<int> Qcoeff { get; }
        Span<int> Dqcoeff { get; }
        int Eob { get; }
        short Dequant0 { get; }
        short Dequant1 { get; }
        int RdmultIn { get; }
        int BitDepth { get; }
        int Sharpness { get; }
        bool UseChromaTrellisRdMult { get; }
        bool TuneIq { get; }
        int TxTypeCost { get; }
        ReadOnlySpan<ushort> Scan { get; }
        byte[]? Iqmatrix { get; }
        byte[]? Qmatrix { get; }
    }

    internal ref struct ExplicitTrellisArgs : ITrellisArgs
    {
        public AomCoeffCosts Costs { get; set; }
        public int TxSize { get; set; }
        public int TxType { get; set; }
        public int PlaneType { get; set; }
        public bool IsInter { get; set; }
        public AomTxbCtx TxbCtx { get; set; }
        public ReadOnlySpan<int> Tcoeff { get; set; }
        public Span<int> Qcoeff { get; set; }
        public Span<int> Dqcoeff { get; set; }
        public int Eob { get; set; }
        public short Dequant0 { get; set; }
        public short Dequant1 { get; set; }
        public int RdmultIn { get; set; }
        public int BitDepth { get; set; }
        public int Sharpness { get; set; }
        public bool UseChromaTrellisRdMult { get; set; }
        public bool TuneIq { get; set; }
        public int TxTypeCost { get; set; }
        public ReadOnlySpan<ushort> Scan { get; set; }
        public byte[]? Iqmatrix { get; set; }
        public byte[]? Qmatrix { get; set; }
    }

    [SkipLocalsInit]   // libaom's levels_buf / coeff_contexts are uninitialized stack arrays filled before use
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int OptimizeTxbCore<TA>(scoped ref TA a, out int rateCost) where TA : ITrellisArgs, allows ref struct
    {
        var costs = a.Costs;
        int txSize = a.TxSize, txType = a.TxType, planeType = a.PlaneType;
        bool isInter = a.IsInter;
        var txbCtx = a.TxbCtx;
        ReadOnlySpan<int> tcoeff = a.Tcoeff;
        Span<int> qcoeff = a.Qcoeff, dqcoeff = a.Dqcoeff;
        int eob = a.Eob;
        short dequant0 = a.Dequant0, dequant1 = a.Dequant1;
        int rdmultIn = a.RdmultIn, bitDepth = a.BitDepth, sharpness = a.Sharpness;
        bool useChromaTrellisRdMult = a.UseChromaTrellisRdMult, tuneIq = a.TuneIq;
        int txTypeCost = a.TxTypeCost;
        ReadOnlySpan<ushort> scan = a.Scan;
        byte[]? iqmatrix = a.Iqmatrix, qmatrix = a.Qmatrix;
        int shift = AomQuantize.TxScale(txSize);
        int txsCtx = TxsizeEntropyCtx(txSize);
        int txClass = TxTypeToClass[txType];
        int bhl = TxbBhl(txSize), width = TxbWide(txSize), height = TxbHigh(txSize);
        // every scan position indexes the retained width x height coefficients (the unchecked reads below rely on it)
        if (tcoeff.Length < width * height || qcoeff.Length < width * height || dqcoeff.Length < width * height || scan.Length < width * height
            || (uint)eob > (uint)(width * height))
            throw new ArgumentException("tx block buffers smaller than the tx size");
        var txbCosts = costs.Get(txsCtx, planeType);
        var txbEobCosts = costs.GetEob(TxsizeLog2Minus4[txSize], planeType);
        int rshift = tuneIq ? 7 : 5;
        var trellisRdMult = useChromaTrellisRdMult ? PlaneRdMultChroma : PlaneRdMult;
        long rdmult = (((long)rdmultIn * (8 - sharpness) * (trellisRdMult[(isInter ? 2 : 0) + planeType] << (2 * (bitDepth - 8)))) + (1L << (rshift - 1))) >> rshift;

        Unsafe.SkipInit(out LevelsBuf levelsBuf);   // a struct local, not stackalloc: keeps the method tier-able (OSR / PGO)
        Span<byte> levels = levelsBuf;
        if (eob > 1) InitLevels(qcoeff, width, height, levels);
        var t = new Trellis
        {
            Tcoeff = ref MemoryMarshal.GetReference(tcoeff), Qcoeff = ref MemoryMarshal.GetReference(qcoeff),
            Dqcoeff = ref MemoryMarshal.GetReference(dqcoeff), Levels = ref MemoryMarshal.GetReference(levels),
            Scan = ref MemoryMarshal.GetReference(scan),
            Base = ref MemoryMarshal.GetArrayDataReference(txbCosts.Base), BaseEob = ref MemoryMarshal.GetArrayDataReference(txbCosts.BaseEob),
            DcSign = ref MemoryMarshal.GetArrayDataReference(txbCosts.DcSign), Lps = ref MemoryMarshal.GetArrayDataReference(txbCosts.Lps),
            NzOffset = ref MemoryMarshal.GetArrayDataReference(NzMapCtxOffset[txSize]),
            EobCosts = txbEobCosts, Costs = txbCosts, Rdmult = rdmult,
            Shift = shift, Bhl = bhl, Width = width, TxClass = txClass, DcSignCtx = txbCtx.DcSignCtx, Sharpness = sharpness,
            Dq0 = dequant0, Dq1 = dequant1, Iqm = iqmatrix, Qm = qmatrix,
        };
        int nonSkipCost = txbCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 0];
        int skipCost = txbCosts.TxbSkip[txbCtx.TxbSkipCtx * 2 + 1];
        int accuRate = EobCost(eob, txbEobCosts, txbCosts, txClass);
        long accuDist = 0;
        int si = eob - 1;
        int ci = scan[si];
        int qc = qcoeff[ci];
        int absQc = AbsI(qc);
        int sign = qc < 0 ? 1 : 0;
        const int maxNzNum = 2;
        int nzNum = 1;
        Unsafe.SkipInit(out NzBuf nzBuf);
        Span<int> nzCi = nzBuf;
        nzCi[0] = ci; nzCi[1] = 0; nzCi[2] = 0;
        if (absQc >= 2)
        {
            UpdateCoeffGeneral(ref t, ref accuRate, ref accuDist, si, eob);
            --si;
        }
        else
        {
            int coeffCtx = LowerLevelsCtxEob(bhl, width, si);
            accuRate += CoeffCostEob(ref t, ci, absQc, sign, coeffCtx);
            int tqc = tcoeff[ci], dqc = dqcoeff[ci];
            accuDist += DistDiff(ref t, tqc, dqc, ci);
            --si;
        }
        // update_coeff_eob_facade
        ref int nz0 = ref MemoryMarshal.GetReference(nzCi);
        si = UpdateCoeffEobLoop(ref t, ref accuRate, ref accuDist, ref eob, ref nzNum, ref nz0, si, maxNzNum);
        if (si == -1 && nzNum <= maxNzNum && sharpness == 0)
        {
            // update_skip
            long rd = AomRd.RdCost64(rdmult, accuRate + nonSkipCost, accuDist);
            long rdNewEob = AomRd.RdCost64(rdmult, skipCost, 0);
            if (rdNewEob < rd)
            {
                for (int i = 0; i < nzNum; i++) { int c2 = nzCi[i]; qcoeff[c2] = 0; dqcoeff[c2] = 0; }
                accuRate = 0;
                eob = 0;
            }
        }
        // update_coeff_simple_facade
        if (si >= 1) si = UpdateCoeffSimpleLoop(ref t, ref accuRate, si);
        if (si == 0)
        {
            long dummyDist = 0;
            UpdateCoeffGeneral(ref t, ref accuRate, ref dummyDist, si, eob);
        }
        accuRate += eob == 0 ? skipCost : nonSkipCost + txTypeCost;
        rateCost = accuRate;
        return eob;
    }

    [InlineArray(TxPad2d)] private struct LevelsBuf { private byte e; }
    [InlineArray(3)] private struct NzBuf { private int e; }

    /// <summary>The trellis's per-block state (av1_optimize_txb's locals and arguments), passed to the per-coefficient
    /// updates by reference: unchecked views of the block's coefficients, levels, scan and cost tables.</summary>
    private ref struct Trellis
    {
        public ref int Tcoeff, Qcoeff, Dqcoeff;
        public ref byte Levels;
        public ref ushort Scan;
        public ref int Base, BaseEob, DcSign, Lps;
        public ref sbyte NzOffset;
        public int[] EobCosts;
        public AomLvMapCoeffCost Costs;
        public long Rdmult;
        public int Shift, Bhl, Width, TxClass, DcSignCtx, Sharpness, Dq0, Dq1;
        // the inverse quantization matrix (get_dqv) and, with the QM-PSNR metric, the weighting matrix (get_coeff_dist)
        public byte[]? Iqm, Qm;
    }

    /// <summary>get_dqv: the coefficient's dequantizer, matrix-weighted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Dqv(ref Trellis t, int ci)
    {
        int dqv = ci != 0 ? t.Dq1 : t.Dq0;
        var iqm = t.Iqm;
        if (iqm != null) dqv = (iqm[ci] * dqv + (1 << 4)) >> 5;
        return dqv;
    }

    /// <summary>get_coeff_dist(tqc, dqc) - get_coeff_dist(tqc, 0): without a matrix (tqc - dqc)^2 - tqc^2 = dqc (dqc - 2 tqc),
    /// scaled by 2^(2 shift); with the QM-PSNR matrix both weighted and rounded as libaom computes them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DistDiff(ref Trellis t, int tqc, int dqc, int ci)
    {
        var qm = t.Qm;
        if (qm == null) return ((long)dqc * (dqc - (long)tqc * 2)) * (1L << (2 * t.Shift));
        return AomQm.CoeffDistQm(tqc, dqc, t.Shift, qm, ci) - AomQm.CoeffDistQm(tqc, 0, t.Shift, qm, ci);
    }

    /// <summary>get_lower_levels_ctx over the trellis's levels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LowerLevelsCtx(ref Trellis t, int ci)
    {
        int bhl = t.Bhl, txClass = t.TxClass;
        int stats = NzMag(ref Unsafe.Add(ref t.Levels, PaddedIdx(ci, bhl)), bhl, txClass);
        if ((txClass | ci) == 0) return 0;
        int ctx = Math.Min((stats + 1) >> 1, 4);
        ref int off1d = ref MemoryMarshal.GetArrayDataReference(NzMapCtxOffset1d);
        switch (txClass)
        {
            case TX_CLASS_2D: return ctx + Unsafe.Add(ref t.NzOffset, ci);
            case TX_CLASS_HORIZ: return ctx + Unsafe.Add(ref off1d, ci >> bhl);
            default: return ctx + Unsafe.Add(ref off1d, ci - ((ci >> bhl) << bhl));
        }
    }

    /// <summary>get_br_cost from the br context's lps row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BrCost(ref Trellis t, int brCtx, int level)
        => Unsafe.Add(ref t.Lps, brCtx * AomLvMapCoeffCost.LpsStride + Math.Min(level - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(level);

    /// <summary>get_coeff_cost_eob.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CoeffCostEob(ref Trellis t, int ci, int absQc, int sign, int coeffCtx)
    {
        int cost = Unsafe.Add(ref t.BaseEob, coeffCtx * 3 + Math.Min(absQc, 3) - 1);
        if (absQc != 0)
        {
            cost += ci == 0 ? Unsafe.Add(ref t.DcSign, t.DcSignCtx * 2 + sign) : AomCost.CostLiteral(1);
            if (absQc > NumBaseLevels) cost += BrCost(ref t, BrCtxEob(ci, t.Bhl, t.TxClass), absQc);
        }
        return cost;
    }

    /// <summary>get_coeff_cost_general with is_last = 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CoeffCostNotLast(ref Trellis t, int ci, int absQc, int sign, int coeffCtx)
    {
        int cost = Unsafe.Add(ref t.Base, coeffCtx * 8 + Math.Min(absQc, 3));
        if (absQc != 0)
        {
            cost += ci == 0 ? Unsafe.Add(ref t.DcSign, t.DcSignCtx * 2 + sign) : AomCost.CostLiteral(1);
            if (absQc > NumBaseLevels) cost += BrCost(ref t, BrCtx(ref t.Levels, ci, t.Bhl, t.TxClass), absQc);
        }
        return cost;
    }

    /// <summary>get_two_coeff_cost_simple.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TwoCoeffCostSimple(ref Trellis t, int ci, int absQc, int coeffCtx, out int costLow)
    {
        ref int b = ref Unsafe.Add(ref t.Base, coeffCtx * 8);
        int cost = Unsafe.Add(ref b, Math.Min(absQc, 3));
        int diff = 0;
        if (absQc <= 3) diff = Unsafe.Add(ref b, absQc + 4);
        if (absQc != 0)
        {
            cost += AomCost.CostLiteral(1);
            if (absQc > NumBaseLevels)
            {
                // get_br_cost_with_diff
                ref int lps = ref Unsafe.Add(ref t.Lps, BrCtx(ref t.Levels, ci, t.Bhl, t.TxClass) * AomLvMapCoeffCost.LpsStride);
                int baseRange = Math.Min(absQc - 1 - NumBaseLevels, CoeffBaseRange);
                int golombBits = 0;
                if (absQc <= CoeffBaseRange + 1 + NumBaseLevels) diff += Unsafe.Add(ref lps, baseRange + CoeffBaseRange + 1);
                if (absQc >= CoeffBaseRange + 1 + NumBaseLevels)
                {
                    int r = absQc - CoeffBaseRange - NumBaseLevels;
                    if (r < 32)
                    {
                        golombBits = GolombBitsCost[r];
                        diff += GolombCostDiff[r];
                    }
                    else
                    {
                        golombBits = GolombCost(absQc);
                        diff += (r & (r - 1)) == 0 ? 1024 : 0;
                    }
                }
                cost += Unsafe.Add(ref lps, baseRange) + golombBits;
            }
        }
        costLow = cost - diff;
        return cost;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QcDqcLow(int absQc, int sign, int dqv, int shift, out int qcLow, out int dqcLow)
    {
        int absQcLow = absQc - 1;
        qcLow = (-sign ^ absQcLow) + sign;
        int absDqcLow = (absQcLow * dqv) >> shift;
        dqcLow = (-sign ^ absDqcLow) + sign;
    }

    /// <summary>update_coeff_general.</summary>
    private static void UpdateCoeffGeneral(ref Trellis t, ref int accuRate, ref long accuDist, int si, int eob)
    {
        int ci = Unsafe.Add(ref t.Scan, si);
        int qc = Unsafe.Add(ref t.Qcoeff, ci);
        bool isLast = si == eob - 1;
        int coeffCtx = isLast ? LowerLevelsCtxEob(t.Bhl, t.Width, si) : LowerLevelsCtx(ref t, ci);
        if (qc == 0) { accuRate += Unsafe.Add(ref t.Base, coeffCtx * 8); return; }
        int sign = qc < 0 ? 1 : 0;
        int absQc = AbsI(qc);
        int tqc = Unsafe.Add(ref t.Tcoeff, ci), dqc = Unsafe.Add(ref t.Dqcoeff, ci);
        int rate = isLast ? CoeffCostEob(ref t, ci, absQc, sign, coeffCtx) : CoeffCostNotLast(ref t, ci, absQc, sign, coeffCtx);
        int qcLow = 0, dqcLow = 0, absQcLow = 0, rateLow;
        if (absQc == 1) rateLow = Unsafe.Add(ref t.Base, coeffCtx * 8);
        else
        {
            int dqv = Dqv(ref t, ci);
            QcDqcLow(absQc, sign, dqv, t.Shift, out qcLow, out dqcLow);
            absQcLow = absQc - 1;
            rateLow = isLast ? CoeffCostEob(ref t, ci, absQcLow, sign, coeffCtx) : CoeffCostNotLast(ref t, ci, absQcLow, sign, coeffCtx);
        }
        long distDiff0 = DistDiff(ref t, tqc, dqc, ci);
        long distDiffLow0 = absQc == 1 ? 0 : DistDiff(ref t, tqc, dqcLow, ci);
        long rd = AomRd.RdCost64(t.Rdmult, rate, distDiff0);
        long rdLow = AomRd.RdCost64(t.Rdmult, rateLow, distDiffLow0);
        if (rdLow < rd)
        {
            Unsafe.Add(ref t.Qcoeff, ci) = qcLow;
            Unsafe.Add(ref t.Dqcoeff, ci) = dqcLow;
            Unsafe.Add(ref t.Levels, PaddedIdx(ci, t.Bhl)) = (byte)Math.Min(absQcLow, 127);
            accuRate += rateLow;
            accuDist += distDiffLow0;
        }
        else
        {
            accuRate += rate;
            accuDist += distDiff0;
        }
    }

    /// <summary>update_coeff_simple_facade's loop: update_coeff_simple for si down to 1 (returns 0). The trellis state is
    /// read into locals once, so the per-coefficient body works on registers rather than on the address-taken struct.</summary>
    private static int UpdateCoeffSimpleLoop(ref Trellis t, ref int accuRateRef, int si) => t.TxClass switch
    {
        TX_CLASS_2D => UpdateCoeffSimpleLoopT<TxClass2D>(ref t, ref accuRateRef, si),
        TX_CLASS_HORIZ => UpdateCoeffSimpleLoopT<TxClassHoriz>(ref t, ref accuRateRef, si),
        _ => UpdateCoeffSimpleLoopT<TxClassVert>(ref t, ref accuRateRef, si),
    };

    private static int UpdateCoeffSimpleLoopT<TC>(ref Trellis t, ref int accuRateRef, int si) where TC : struct, ITxClassConst
    {
        ref ushort scan = ref t.Scan;
        ref int qcoeff = ref t.Qcoeff, tcoeff = ref t.Tcoeff, dqcoeff = ref t.Dqcoeff;
        ref byte levels = ref t.Levels;
        ref int baseCost = ref t.Base, lps = ref t.Lps;
        ref sbyte nzOffset = ref t.NzOffset;
        ref int off1d = ref MemoryMarshal.GetArrayDataReference(NzMapCtxOffset1d);
        ref int golBits = ref MemoryMarshal.GetArrayDataReference(GolombBitsCost);
        ref int golDiff = ref MemoryMarshal.GetArrayDataReference(GolombCostDiff);
        int bhl = t.Bhl, txClass = TC.Cls, shift = t.Shift, sharpness = t.Sharpness;
        int stride = (1 << bhl) + TxPadHor;
        long rdmult = t.Rdmult;
        bool plain = t.Iqm == null && t.Qm == null;
        int dq1 = t.Dq1;
        int accuRate = accuRateRef;
        for (; si >= 1; --si)
        {
            int ci = Unsafe.Add(ref scan, si);
            int qc = Unsafe.Add(ref qcoeff, ci);
            // get_lower_levels_ctx (ci > 0 here)
            int col = ci >> bhl;
            ref byte l = ref Unsafe.Add(ref levels, ci + (col << TxPadHorLog2));
            int mag = ClipMax3(Unsafe.Add(ref l, stride)) + ClipMax3(Unsafe.Add(ref l, 1));
            int coeffCtx;
            if (txClass == TX_CLASS_2D)
            {
                mag += ClipMax3(Unsafe.Add(ref l, stride + 1)) + ClipMax3(Unsafe.Add(ref l, 2 * stride)) + ClipMax3(Unsafe.Add(ref l, 2));
                coeffCtx = Math.Min((mag + 1) >> 1, 4) + Unsafe.Add(ref nzOffset, ci);
            }
            else if (txClass == TX_CLASS_VERT)
            {
                mag += ClipMax3(Unsafe.Add(ref l, 2)) + ClipMax3(Unsafe.Add(ref l, 3)) + ClipMax3(Unsafe.Add(ref l, 4));
                coeffCtx = Math.Min((mag + 1) >> 1, 4) + Unsafe.Add(ref off1d, ci - (col << bhl));
            }
            else
            {
                mag += ClipMax3(Unsafe.Add(ref l, 2 * stride)) + ClipMax3(Unsafe.Add(ref l, 3 * stride)) + ClipMax3(Unsafe.Add(ref l, 4 * stride));
                coeffCtx = Math.Min((mag + 1) >> 1, 4) + Unsafe.Add(ref off1d, col);
            }
            ref int b = ref Unsafe.Add(ref baseCost, coeffCtx * 8);
            if (qc == 0) { accuRate += b; continue; }
            int absQc = AbsI(qc), absTqc = AbsI(Unsafe.Add(ref tcoeff, ci)), absDqc = AbsI(Unsafe.Add(ref dqcoeff, ci));
            if (absQc == 1)
            {
                int rate = Unsafe.Add(ref b, 1) + AomCost.CostLiteral(1);
                if (absDqc < absTqc || sharpness != 0) { accuRate += rate; continue; }
                long distDiff0 = plain ? ((long)absDqc * (absDqc - (long)absTqc * 2)) * (1L << (2 * shift)) : DistDiff(ref t, absTqc, absDqc, ci);
                int rateLow = rate - Unsafe.Add(ref b, 5);
                if (AomRd.RdCost64(rdmult, rateLow, 0) < AomRd.RdCost64(rdmult, rate, distDiff0))
                {
                    Unsafe.Add(ref qcoeff, ci) = 0;
                    Unsafe.Add(ref dqcoeff, ci) = 0;
                    l = 0;
                    accuRate += rateLow;
                    continue;
                }
                accuRate += rate;
            }
            else
            {
                // get_two_coeff_cost_simple
                int cost = Unsafe.Add(ref b, Math.Min(absQc, 3));
                int diff = absQc <= 3 ? Unsafe.Add(ref b, absQc + 4) : 0;
                cost += AomCost.CostLiteral(1);
                if (absQc > NumBaseLevels)
                {
                    // get_br_cost_with_diff (get_br_ctx inline; ci > 0)
                    int row = ci - (col << bhl);
                    int bm = Unsafe.Add(ref l, 1) + Unsafe.Add(ref l, stride);
                    int brCtx;
                    if (txClass == TX_CLASS_2D)
                    {
                        bm = Math.Min((bm + Unsafe.Add(ref l, stride + 1) + 1) >> 1, 6);
                        brCtx = (row < 2 && col < 2) ? bm + 7 : bm + 14;
                    }
                    else if (txClass == TX_CLASS_HORIZ)
                    {
                        bm = Math.Min((bm + Unsafe.Add(ref l, stride << 1) + 1) >> 1, 6);
                        brCtx = col == 0 ? bm + 7 : bm + 14;
                    }
                    else
                    {
                        bm = Math.Min((bm + Unsafe.Add(ref l, 2) + 1) >> 1, 6);
                        brCtx = row == 0 ? bm + 7 : bm + 14;
                    }
                    ref int lp = ref Unsafe.Add(ref lps, brCtx * AomLvMapCoeffCost.LpsStride);
                    int baseRange = Math.Min(absQc - 1 - NumBaseLevels, CoeffBaseRange);
                    int golombBits = 0;
                    if (absQc <= CoeffBaseRange + 1 + NumBaseLevels) diff += Unsafe.Add(ref lp, baseRange + CoeffBaseRange + 1);
                    if (absQc >= CoeffBaseRange + 1 + NumBaseLevels)
                    {
                        int r = absQc - CoeffBaseRange - NumBaseLevels;
                        if (r < 32)
                        {
                            golombBits = Unsafe.Add(ref golBits, r);
                            diff += Unsafe.Add(ref golDiff, r);
                        }
                        else
                        {
                            golombBits = GolombCost(absQc);
                            diff += (r & (r - 1)) == 0 ? 1024 : 0;
                        }
                    }
                    cost += Unsafe.Add(ref lp, baseRange) + golombBits;
                }
                int rate = cost, rateLow = cost - diff;
                if (absDqc < absTqc) { accuRate += rate; continue; }
                // allow_lower_qc = sharpness == 0 || abs_qc > 1: always, abs_qc being at least 2 here
                int dqv = plain ? dq1 : Dqv(ref t, ci);
                int absQcLow = absQc - 1;
                int absDqcLow = (absQcLow * dqv) >> shift;
                long distDiff0, distDiffLow0;
                if (plain)
                {
                    distDiff0 = ((long)absDqc * (absDqc - (long)absTqc * 2)) * (1L << (2 * shift));
                    distDiffLow0 = ((long)absDqcLow * (absDqcLow - (long)absTqc * 2)) * (1L << (2 * shift));
                }
                else
                {
                    distDiff0 = DistDiff(ref t, absTqc, absDqc, ci);
                    distDiffLow0 = DistDiff(ref t, absTqc, absDqcLow, ci);
                }
                if (AomRd.RdCost64(rdmult, rateLow, distDiffLow0) < AomRd.RdCost64(rdmult, rate, distDiff0))
                {
                    int sign = qc < 0 ? 1 : 0;
                    Unsafe.Add(ref qcoeff, ci) = (-sign ^ absQcLow) + sign;
                    Unsafe.Add(ref dqcoeff, ci) = (-sign ^ absDqcLow) + sign;
                    l = (byte)Math.Min(absQcLow, 127);
                    accuRate += rateLow;
                    continue;
                }
                accuRate += rate;
            }
        }
        accuRateRef = accuRate;
        return si;
    }

    /// <summary>update_coeff_eob.</summary>
    /// <summary>update_coeff_eob_facade's loop: update_coeff_eob per coefficient while at most maxNzNum nonzero levels
    /// were kept. Without quantization matrices it runs on locals (the trellis state read once, the br contexts and the
    /// eob position cost computed once per coefficient); with them, update_coeff_eob as is. Returns the next si.</summary>
    private static int UpdateCoeffEobLoop(ref Trellis t, ref int accuRate, ref long accuDist, ref int eob, ref int nzNum, ref int nzCi, int si,
        int maxNzNum)
    {
        if (t.Iqm != null || t.Qm != null)
        {
            int ar0 = accuRate, e0 = eob, nn0 = nzNum;
            long ad0 = accuDist;
            for (; si >= 0 && nn0 <= maxNzNum; --si) UpdateCoeffEob(ref t, ref ar0, ref ad0, ref e0, ref nn0, ref nzCi, si);
            accuRate = ar0; accuDist = ad0; eob = e0; nzNum = nn0;
            return si;
        }
        return t.TxClass switch
        {
            TX_CLASS_2D => UpdateCoeffEobLoopT<TxClass2D>(ref t, ref accuRate, ref accuDist, ref eob, ref nzNum, ref nzCi, si, maxNzNum),
            TX_CLASS_HORIZ => UpdateCoeffEobLoopT<TxClassHoriz>(ref t, ref accuRate, ref accuDist, ref eob, ref nzNum, ref nzCi, si, maxNzNum),
            _ => UpdateCoeffEobLoopT<TxClassVert>(ref t, ref accuRate, ref accuDist, ref eob, ref nzNum, ref nzCi, si, maxNzNum),
        };
    }

    private static int UpdateCoeffEobLoopT<TC>(ref Trellis t, ref int accuRate, ref long accuDist, ref int eob, ref int nzNum, ref int nzCi, int si,
        int maxNzNum) where TC : struct, ITxClassConst
    {
        ref ushort scan = ref t.Scan;
        ref int qcoeff = ref t.Qcoeff, tcoeff = ref t.Tcoeff, dqcoeff = ref t.Dqcoeff;
        ref byte levels = ref t.Levels;
        ref int baseCost = ref t.Base, baseEob = ref t.BaseEob, lps = ref t.Lps;
        ref sbyte nzOffset = ref t.NzOffset;
        ref int off1d = ref MemoryMarshal.GetArrayDataReference(NzMapCtxOffset1d);
        int bhl = t.Bhl, txClass = TC.Cls, shift = t.Shift, sharpness = t.Sharpness, width = t.Width;
        int stride = (1 << bhl) + TxPadHor;
        long rdmult = t.Rdmult;
        int dq0 = t.Dq0, dq1 = t.Dq1;
        int dcSignCost0 = Unsafe.Add(ref t.DcSign, t.DcSignCtx * 2), dcSignCost1 = Unsafe.Add(ref t.DcSign, t.DcSignCtx * 2 + 1);
        int[] eobCosts = t.EobCosts;
        var costs = t.Costs;
        long distScale = 1L << (2 * shift);
        int ar = accuRate, e = eob, nn = nzNum;
        long ad = accuDist;
        for (; si >= 0 && nn <= maxNzNum; --si)
        {
            int ci = Unsafe.Add(ref scan, si);
            int qc = Unsafe.Add(ref qcoeff, ci);
            int col = ci >> bhl, row = ci - (col << bhl);
            ref byte l = ref Unsafe.Add(ref levels, ci + (col << TxPadHorLog2));
            // get_lower_levels_ctx
            int coeffCtx;
            {
                int mag = ClipMax3(Unsafe.Add(ref l, stride)) + ClipMax3(Unsafe.Add(ref l, 1));
                if (txClass == TX_CLASS_2D)
                {
                    mag += ClipMax3(Unsafe.Add(ref l, stride + 1)) + ClipMax3(Unsafe.Add(ref l, 2 * stride)) + ClipMax3(Unsafe.Add(ref l, 2));
                    coeffCtx = ci == 0 ? 0 : Math.Min((mag + 1) >> 1, 4) + Unsafe.Add(ref nzOffset, ci);
                }
                else if (txClass == TX_CLASS_VERT)
                {
                    mag += ClipMax3(Unsafe.Add(ref l, 2)) + ClipMax3(Unsafe.Add(ref l, 3)) + ClipMax3(Unsafe.Add(ref l, 4));
                    coeffCtx = Math.Min((mag + 1) >> 1, 4) + Unsafe.Add(ref off1d, row);
                }
                else
                {
                    mag += ClipMax3(Unsafe.Add(ref l, 2 * stride)) + ClipMax3(Unsafe.Add(ref l, 3 * stride)) + ClipMax3(Unsafe.Add(ref l, 4 * stride));
                    coeffCtx = Math.Min((mag + 1) >> 1, 4) + Unsafe.Add(ref off1d, col);
                }
            }
            ref int b = ref Unsafe.Add(ref baseCost, coeffCtx * 8);
            if (qc == 0) { ar += b; continue; }
            int dqv = ci != 0 ? dq1 : dq0;
            int absQc = AbsI(qc);
            int tqc = Unsafe.Add(ref tcoeff, ci), dqc = Unsafe.Add(ref dqcoeff, ci);
            int sign = qc < 0 ? 1 : 0;
            int signCost = ci == 0 ? (sign != 0 ? dcSignCost1 : dcSignCost0) : AomCost.CostLiteral(1);
            // the br context (get_br_ctx, from the neighbours' final levels) and the eob variant (get_br_ctx_eob)
            int brCtx = 0, brCtxEob = 0;
            if (absQc > NumBaseLevels)
            {
                int bm = Unsafe.Add(ref l, 1) + Unsafe.Add(ref l, stride);
                if (txClass == TX_CLASS_2D)
                {
                    bm = Math.Min((bm + Unsafe.Add(ref l, stride + 1) + 1) >> 1, 6);
                    brCtx = ci == 0 ? bm : (row < 2 && col < 2) ? bm + 7 : bm + 14;
                    brCtxEob = ci == 0 ? 0 : (row < 2 && col < 2) ? 7 : 14;
                }
                else if (txClass == TX_CLASS_HORIZ)
                {
                    bm = Math.Min((bm + Unsafe.Add(ref l, stride << 1) + 1) >> 1, 6);
                    brCtx = ci == 0 ? bm : col == 0 ? bm + 7 : bm + 14;
                    brCtxEob = ci == 0 ? 0 : col == 0 ? 7 : 14;
                }
                else
                {
                    bm = Math.Min((bm + Unsafe.Add(ref l, 2) + 1) >> 1, 6);
                    brCtx = ci == 0 ? bm : row == 0 ? bm + 7 : bm + 14;
                    brCtxEob = ci == 0 ? 0 : row == 0 ? 7 : 14;
                }
            }
            // get_coeff_cost_general (is_last 0) at abs_qc
            int rate = Unsafe.Add(ref b, Math.Min(absQc, 3)) + signCost;
            if (absQc > NumBaseLevels)
                rate += Unsafe.Add(ref lps, brCtx * AomLvMapCoeffCost.LpsStride + Math.Min(absQc - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(absQc);
            int qcLow = 0, dqcLow = 0, absQcLow = absQc - 1, rateLow;
            long distLow;
            long dist = ((long)dqc * (dqc - (long)tqc * 2)) * distScale;
            if (absQc == 1)
            {
                rateLow = b;
                distLow = 0;
            }
            else
            {
                qcLow = (-sign ^ absQcLow) + sign;
                int absDqcLow = (absQcLow * dqv) >> shift;
                dqcLow = (-sign ^ absDqcLow) + sign;
                rateLow = Unsafe.Add(ref b, Math.Min(absQcLow, 3)) + signCost;
                if (absQcLow > NumBaseLevels)
                    rateLow += Unsafe.Add(ref lps, brCtx * AomLvMapCoeffCost.LpsStride + Math.Min(absQcLow - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(absQcLow);
                distLow = ((long)dqcLow * (dqcLow - (long)tqc * 2)) * distScale;
            }
            long rd = AomRd.RdCost64(rdmult, ar + rate, ad + dist);
            long rdLow = AomRd.RdCost64(rdmult, ar + rateLow, ad + distLow);

            bool lowerLevelNewEob = false, lowerLevel = false;
            int newEob = si + 1;
            int coeffCtxNewEob = LowerLevelsCtxEob(bhl, width, si);
            int newEobCost = EobCost(newEob, eobCosts, costs, txClass);
            ref int be = ref Unsafe.Add(ref baseEob, coeffCtxNewEob * 3);
            // get_coeff_cost_eob at abs_qc / abs_qc_low
            int rateCoeffEob = newEobCost + Unsafe.Add(ref be, Math.Min(absQc, 3) - 1) + signCost;
            if (absQc > NumBaseLevels)
                rateCoeffEob += Unsafe.Add(ref lps, brCtxEob * AomLvMapCoeffCost.LpsStride + Math.Min(absQc - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(absQc);
            long distNewEob = dist;
            long rdNewEob = AomRd.RdCost64(rdmult, rateCoeffEob, distNewEob);
            if (absQcLow > 0)
            {
                int rateCoeffEobLow = newEobCost + Unsafe.Add(ref be, Math.Min(absQcLow, 3) - 1) + signCost;
                if (absQcLow > NumBaseLevels)
                    rateCoeffEobLow += Unsafe.Add(ref lps, brCtxEob * AomLvMapCoeffCost.LpsStride + Math.Min(absQcLow - 1 - NumBaseLevels, CoeffBaseRange)) + GolombCost(absQcLow);
                long rdNewEobLow = AomRd.RdCost64(rdmult, rateCoeffEobLow, distLow);
                if (rdNewEobLow < rdNewEob)
                {
                    lowerLevelNewEob = true;
                    rdNewEob = rdNewEobLow;
                    rateCoeffEob = rateCoeffEobLow;
                    distNewEob = distLow;
                }
            }
            int qcThreshold = si <= 5 ? 2 : 1;
            bool allowLowerQc = sharpness == 0 || absQc > qcThreshold;
            if (allowLowerQc && rdLow < rd)
            {
                lowerLevel = true;
                rd = rdLow;
                rate = rateLow;
                dist = distLow;
            }
            if ((sharpness == 0 || newEob >= 5) && rdNewEob < rd)
            {
                for (int ni = 0; ni < nn; ni++)
                {
                    int lastCi = Unsafe.Add(ref nzCi, ni);
                    Unsafe.Add(ref levels, PaddedIdx(lastCi, bhl)) = 0;
                    Unsafe.Add(ref qcoeff, lastCi) = 0;
                    Unsafe.Add(ref dqcoeff, lastCi) = 0;
                }
                e = newEob;
                nn = 0;
                ar = rateCoeffEob;
                ad = distNewEob;
                lowerLevel = lowerLevelNewEob;
            }
            else
            {
                ar += rate;
                ad += dist;
            }
            if (lowerLevel)
            {
                Unsafe.Add(ref qcoeff, ci) = qcLow;
                Unsafe.Add(ref dqcoeff, ci) = dqcLow;
                l = (byte)Math.Min(absQcLow, 127);
            }
            if (Unsafe.Add(ref qcoeff, ci) != 0) Unsafe.Add(ref nzCi, nn++) = ci;
        }
        accuRate = ar; accuDist = ad; eob = e; nzNum = nn;
        return si;
    }

    private static void UpdateCoeffEob(ref Trellis t, ref int accuRate, ref long accuDist, ref int eob, ref int nzNum, ref int nzCi, int si)
    {
        int ci = Unsafe.Add(ref t.Scan, si);
        int qc = Unsafe.Add(ref t.Qcoeff, ci);
        int coeffCtx = LowerLevelsCtx(ref t, ci);
        if (qc == 0) { accuRate += Unsafe.Add(ref t.Base, coeffCtx * 8); return; }
        int shift = t.Shift, bhl = t.Bhl, sharpness = t.Sharpness;
        long rdmult = t.Rdmult;
        int dqv = Dqv(ref t, ci);
        bool lowerLevel = false;
        int absQc = AbsI(qc);
        int tqc = Unsafe.Add(ref t.Tcoeff, ci), dqc = Unsafe.Add(ref t.Dqcoeff, ci);
        int sign = qc < 0 ? 1 : 0;
        int qcLow = 0, dqcLow = 0, absQcLow = 0, rateLow;
        if (absQc == 1) rateLow = Unsafe.Add(ref t.Base, coeffCtx * 8);
        else
        {
            QcDqcLow(absQc, sign, dqv, shift, out qcLow, out dqcLow);
            absQcLow = absQc - 1;
            rateLow = CoeffCostNotLast(ref t, ci, absQcLow, sign, coeffCtx);
        }
        long dist = DistDiff(ref t, tqc, dqc, ci);
        long distLow = absQc == 1 ? 0 : DistDiff(ref t, tqc, dqcLow, ci);
        int rate = CoeffCostNotLast(ref t, ci, absQc, sign, coeffCtx);
        long rd = AomRd.RdCost64(rdmult, accuRate + rate, accuDist + dist);
        long rdLow = AomRd.RdCost64(rdmult, accuRate + rateLow, accuDist + distLow);

        bool lowerLevelNewEob = false;
        int newEob = si + 1;
        int coeffCtxNewEob = LowerLevelsCtxEob(bhl, t.Width, si);
        int newEobCost = EobCost(newEob, t.EobCosts, t.Costs, t.TxClass);
        int rateCoeffEob = newEobCost + CoeffCostEob(ref t, ci, absQc, sign, coeffCtxNewEob);
        long distNewEob = dist;
        long rdNewEob = AomRd.RdCost64(rdmult, rateCoeffEob, distNewEob);
        if (absQcLow > 0)
        {
            int rateCoeffEobLow = newEobCost + CoeffCostEob(ref t, ci, absQcLow, sign, coeffCtxNewEob);
            long distNewEobLow = distLow;
            long rdNewEobLow = AomRd.RdCost64(rdmult, rateCoeffEobLow, distNewEobLow);
            if (rdNewEobLow < rdNewEob)
            {
                lowerLevelNewEob = true;
                rdNewEob = rdNewEobLow;
                rateCoeffEob = rateCoeffEobLow;
                distNewEob = distNewEobLow;
            }
        }
        int qcThreshold = si <= 5 ? 2 : 1;
        bool allowLowerQc = sharpness == 0 || absQc > qcThreshold;
        if (allowLowerQc && rdLow < rd)
        {
            lowerLevel = true;
            rd = rdLow;
            rate = rateLow;
            dist = distLow;
        }
        if ((sharpness == 0 || newEob >= 5) && rdNewEob < rd)
        {
            for (int ni = 0; ni < nzNum; ni++)
            {
                int lastCi = Unsafe.Add(ref nzCi, ni);
                Unsafe.Add(ref t.Levels, PaddedIdx(lastCi, bhl)) = 0;
                Unsafe.Add(ref t.Qcoeff, lastCi) = 0;
                Unsafe.Add(ref t.Dqcoeff, lastCi) = 0;
            }
            eob = newEob;
            nzNum = 0;
            accuRate = rateCoeffEob;
            accuDist = distNewEob;
            lowerLevel = lowerLevelNewEob;
        }
        else
        {
            accuRate += rate;
            accuDist += dist;
        }
        if (lowerLevel)
        {
            Unsafe.Add(ref t.Qcoeff, ci) = qcLow;
            Unsafe.Add(ref t.Dqcoeff, ci) = dqcLow;
            Unsafe.Add(ref t.Levels, PaddedIdx(ci, bhl)) = (byte)Math.Min(absQcLow, 127);
        }
        if (Unsafe.Add(ref t.Qcoeff, ci) != 0) Unsafe.Add(ref nzCi, nzNum++) = ci;
    }

    /// <summary>av1_get_txb_entropy_context: min(sum |level|, 7) with the DC sign in bits 3-4.</summary>
    internal static byte TxbEntropyContext(ReadOnlySpan<int> qcoeff, ReadOnlySpan<ushort> scan, int eob)
    {
        if (eob == 0) return 0;
        if ((uint)eob > (uint)scan.Length) throw new ArgumentOutOfRangeException(nameof(eob));
        // min(sum |level|, 7) without the per-coefficient zero test (data-random): each term clamped to 8 keeps the
        // minimum and the sum small; the early exit as soon as it passes 7
        int culLevel = 0;
        ref int q0 = ref MemoryMarshal.GetReference(qcoeff);
        ref ushort s0 = ref MemoryMarshal.GetReference(scan);
        int n = qcoeff.Length;
        for (int c = 0; c < eob; c++)
        {
            int pos = Unsafe.Add(ref s0, c);
            if ((uint)pos >= (uint)n) throw new ArgumentOutOfRangeException(nameof(scan));
            culLevel += Math.Min(AbsI(Unsafe.Add(ref q0, pos)), 8);
            if (culLevel > CoeffContextMask) break;
        }
        culLevel = Math.Min(CoeffContextMask, culLevel);
        // set_dc_sign
        if (qcoeff[0] < 0) culLevel |= 1 << CoeffContextBits;
        else if (qcoeff[0] > 0) culLevel += 2 << CoeffContextBits;
        return (byte)culLevel;
    }

    private static readonly sbyte[] TxbCtxSigns = { 0, -1, 1 };
    private static readonly sbyte[] TxbCtxDcSignContexts =
    {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    };
    private static readonly byte[] TxbCtxSkipContexts = { 1, 2, 2, 2, 3, 2, 4, 4, 4, 5, 2, 4, 4, 4, 5, 2, 4, 4, 4, 5, 3, 5, 5, 5, 6 };

    /// <summary>get_entropy_context (av1/common/entropy.h): whether any above / left entropy byte of the tx is nonzero,
    /// summed (combine_entropy_contexts).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int EntropyContext(int txSize, ReadOnlySpan<byte> a, ReadOnlySpan<byte> l)
    {
        int above = 0, left = 0;
        int w = TxSizeWideUnit[txSize], h = TxSizeHighUnit[txSize];
        for (int k = 0; k < w; k++) above |= a[k];
        for (int k = 0; k < h; k++) left |= l[k];
        return (above != 0 ? 1 : 0) + (left != 0 ? 1 : 0);
    }

    /// <summary>get_txb_ctx (all specialisations): the skip and DC-sign contexts of a tx block from its above / left
    /// entropy contexts (one byte per 4 samples of the tx's width / height).</summary>
    internal static AomTxbCtx TxbCtx(int planeBsize, int txSize, int plane, ReadOnlySpan<byte> a, ReadOnlySpan<byte> l)
    {
        int txbWUnit = TxSizeWideUnit[txSize], txbHUnit = TxSizeHighUnit[txSize];
        int dcSign = 0;
        for (int k = 0; k < txbWUnit; k++) dcSign += TxbCtxSigns[a[k] >> CoeffContextBits];
        for (int k = 0; k < txbHUnit; k++) dcSign += TxbCtxSigns[l[k] >> CoeffContextBits];
        var ctx = new AomTxbCtx { DcSignCtx = TxbCtxDcSignContexts[dcSign + 2 * 16] };
        if (plane == 0)
        {
            if (planeBsize == TxsizeToBsize[txSize]) ctx.TxbSkipCtx = 0;
            else
            {
                int top = 0, left = 0;
                for (int k = 0; k < txbWUnit; k++) top |= a[k];
                top &= CoeffContextMask; top = Math.Min(top, 4);
                for (int k = 0; k < txbHUnit; k++) left |= l[k];
                left &= CoeffContextMask; left = Math.Min(left, 4);
                ctx.TxbSkipCtx = TxbCtxSkipContexts[top * 5 + left];
            }
        }
        else
        {
            int ctxBase = EntropyContext(txSize, a, l);
            int ctxOffset = NumPelsLog2Lookup[planeBsize] > NumPelsLog2Lookup[TxsizeToBsize[txSize]] ? 10 : 7;
            ctx.TxbSkipCtx = ctxBase + ctxOffset;
        }
        return ctx;
    }
}
