using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>AV1_XFORM_QUANT (av1/encoder/encodemb.h).</summary>
internal static class AomXformQuant
{
    public const int Fp = 0, B = 1, Dc = 2, SkipQuant = 3;
}

/// <summary>QUANT_PARAM (av1/encoder/av1_quantize.h), without matrices (libaom's all-intra configuration).</summary>
internal struct AomQuantParam
{
    public int LogScale;
    public int TxSize;
    public int UseQuantBAdapt;
    public bool UseOptimizeB;
    public int XformQuantIdx;
}

// Port of libaom 3.14.1 av1/encoder/encodemb.{c,h} (subtract, xform, quant, optimize_b, set_txb_context), the
// transform-block iterator, av1_inverse_transform_block's use, and the 8-bit distortion kernels the search uses
// (aom_sum_squares_2d_i16, aom_sum_sse_2d_i16, aom_sse, av1_block_error as AVX2 runs it, aom_satd).
internal static class AomEncodeMb
{
    /// <summary>BLOCK_OFFSET: a tx block's coefficient offset (block counts 4x4 units).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int BlockOffset(int block) => block << 4;

    /// <summary>av1_get_max_eob.</summary>
    internal static int MaxEob(int txSize)
    {
        if (txSize == TX_64X64 || txSize == TX_64X32 || txSize == TX_32X64) return 1024;
        if (txSize == TX_16X64 || txSize == TX_64X16) return 512;
        return TxSize2d[txSize];
    }

    /// <summary>get_plane_block_size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PlaneBlockSize(int bsize, int ssX, int ssY) => SsSizeLookup[(bsize * 2 + ssX) * 2 + ssY];

    /// <summary>av1_get_ext_tx_set_type.</summary>
    internal static int ExtTxSetType(int txSize, bool isInter, bool useReducedSet)
    {
        int sqrUp = TxsizeSqrUpMap[txSize];
        if (sqrUp > TX_32X32) return EXT_TX_SET_DCTONLY;
        if (sqrUp == TX_32X32) return isInter ? EXT_TX_SET_DCT_IDTX : EXT_TX_SET_DCTONLY;
        if (useReducedSet) return isInter ? EXT_TX_SET_DCT_IDTX : EXT_TX_SET_DTT4_IDTX;
        int sqr = TxsizeSqrMap[txSize];
        return ExtTxSetLookup[(isInter ? 2 : 0) + (sqr == TX_16X16 ? 1 : 0)];
    }

    /// <summary>av1_get_tx_size.</summary>
    internal static int GetTxSize(int plane, AomMacroblockD xd)
    {
        var mbmi = xd.Mi0;
        if (xd.Lossless[mbmi.SegmentId] != 0) return TX_4X4;
        if (plane == 0) return mbmi.TxSize;
        var pd = xd.Plane[plane];
        return MaxUvTxsize(mbmi.Bsize, pd.SubsamplingX, pd.SubsamplingY);
    }

    /// <summary>av1_get_max_uv_txsize.</summary>
    internal static int MaxUvTxsize(int bsize, int ssX, int ssY)
        => AomTxb.AdjustedTxSize(MaxTxsizeRectLookup[PlaneBlockSize(bsize, ssX, ssY)]);

    /// <summary>get_uv_mode (UV_PREDICTION_MODE -> PREDICTION_MODE; UV_CFL_PRED -> DC_PRED).</summary>
    internal static int UvModeToIntra(int uvMode) => uvMode == UV_CFL_PRED ? DC_PRED : uvMode;

    /// <summary>intra_mode_to_tx_type.</summary>
    internal static int IntraModeToTxTypeOf(AomMbModeInfo mbmi, int planeType)
        => IntraModeToTxType[planeType == 0 ? mbmi.Mode : UvModeToIntra(mbmi.UvMode)];

    /// <summary>av1_get_tx_type (intra blocks).</summary>
    internal static int GetTxType(AomMacroblockD xd, int planeType, int blkRow, int blkCol, int txSize, bool reducedTxSet)
    {
        var mbmi = xd.Mi0;
        if (xd.Lossless[mbmi.SegmentId] != 0 || TxsizeSqrUpMap[txSize] > TX_32X32) return DCT_DCT;
        int txType;
        if (planeType == 0) txType = xd.TxTypeMap[xd.TxTypeMapOffset + blkRow * xd.TxTypeMapStride + blkCol];
        else
        {
            txType = IntraModeToTxTypeOf(mbmi, 1);
            int setType = ExtTxSetType(txSize, false, reducedTxSet);
            if (ExtTxUsed[setType * 16 + txType] == 0) txType = DCT_DCT;
        }
        return txType;
    }

    /// <summary>update_txk_array.</summary>
    internal static void UpdateTxkArray(AomMacroblockD xd, int blkRow, int blkCol, int txSize, int txType)
    {
        int stride = xd.TxTypeMapStride, o = xd.TxTypeMapOffset;
        xd.TxTypeMap[o + blkRow * stride + blkCol] = (byte)txType;
        int txw = TxSizeWideUnit[txSize], txh = TxSizeHighUnit[txSize];
        if (txw == TxSizeWideUnit[TX_64X64] || txh == TxSizeHighUnit[TX_64X64])
        {
            int unit = TxSizeWideUnit[TX_16X16];
            for (int idy = 0; idy < txh; idy += unit)
                for (int idx = 0; idx < txw; idx += unit)
                    xd.TxTypeMap[o + (blkRow + idy) * stride + blkCol + idx] = (byte)txType;
        }
    }

    /// <summary>max_block_wide / max_block_high (in 4x4 units, cropped at the frame edge).</summary>
    internal static int MaxBlockWide(AomMacroblockD xd, int bsize, int plane)
    {
        int w = BlockSizeWide[bsize];
        if (xd.MbToRightEdge < 0) w += xd.MbToRightEdge >> (3 + xd.Plane[plane].SubsamplingX);
        return w >> 2;
    }
    internal static int MaxBlockHigh(AomMacroblockD xd, int bsize, int plane)
    {
        int h = BlockSizeHigh[bsize];
        if (xd.MbToBottomEdge < 0) h += xd.MbToBottomEdge >> (3 + xd.Plane[plane].SubsamplingY);
        return h >> 2;
    }

    /// <summary>get_txb_dimensions (rdopt_utils.h): the tx block's visible width / height inside the frame.</summary>
    internal static void TxbDimensions(AomMacroblockD xd, int plane, int planeBsize, int blkRow, int blkCol, int txBsize,
        out int width, out int height, out int visibleWidth, out int visibleHeight)
    {
        int txbHeight = BlockSizeHigh[txBsize], txbWidth = BlockSizeWide[txBsize];
        var pd = xd.Plane[plane];
        if (xd.MbToBottomEdge >= 0) visibleHeight = txbHeight;
        else
        {
            int blockRows = (xd.MbToBottomEdge >> (3 + pd.SubsamplingY)) + BlockSizeHigh[planeBsize];
            visibleHeight = Math.Clamp(blockRows - (blkRow << 2), 0, txbHeight);
        }
        height = txbHeight;
        if (xd.MbToRightEdge >= 0) visibleWidth = txbWidth;
        else
        {
            int blockCols = (xd.MbToRightEdge >> (3 + pd.SubsamplingX)) + BlockSizeWide[planeBsize];
            visibleWidth = Math.Clamp(blockCols - (blkCol << 2), 0, txbWidth);
        }
        width = txbWidth;
    }

    /// <summary>av1_foreach_transformed_block_in_plane.</summary>
    internal delegate void Visit(int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize);
    internal static void ForeachTransformedBlockInPlane(AomMacroblockD xd, int planeBsize, int plane, Visit visit)
    {
        var pd = xd.Plane[plane];
        int txSize = GetTxSize(plane, xd);
        int txBsize = TxsizeToBsize[txSize];
        if (planeBsize == txBsize) { visit(plane, 0, 0, 0, planeBsize, txSize); return; }
        int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize], step = txwUnit * txhUnit;
        int maxBlocksWide = MaxBlockWide(xd, planeBsize, plane), maxBlocksHigh = MaxBlockHigh(xd, planeBsize, plane);
        int maxUnitBsize = PlaneBlockSize(BLOCK_64X64, pd.SubsamplingX, pd.SubsamplingY);
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
                        visit(plane, i, blkRow, blkCol, planeBsize, txSize);
                        i += step;
                    }
            }
        }
    }

    /// <summary>aom_subtract_block (8-bit): diff = src - pred over rows x cols.</summary>
    internal static void SubtractBlock(int rows, int cols, short[] diff, int diffOff, int diffStride,
        byte[] src, int srcOff, int srcStride, byte[] pred, int predOff, int predStride)
    {
        for (int r = 0; r < rows; r++)
        {
            int d = diffOff + r * diffStride, s = srcOff + r * srcStride, p = predOff + r * predStride;
            for (int c = 0; c < cols; c++) diff[d + c] = (short)(src[s + c] - pred[p + c]);
        }
    }

    /// <summary>av1_subtract_txb.</summary>
    internal static void SubtractTxb(AomMacroblock x, int plane, int planeBsize, int blkCol, int blkRow, int txSize)
    {
        var p = x.Plane[plane];
        var pd = x.E.Plane[plane];
        int diffStride = BlockSizeWide[planeBsize];
        SubtractBlock(TxSizeHigh[txSize], TxSizeWide[txSize], p.SrcDiff, (blkRow * diffStride + blkCol) << 2, diffStride,
            p.Src.Buf, p.Src.Offset + ((blkRow * p.Src.Stride + blkCol) << 2), p.Src.Stride,
            pd.Dst.Buf, pd.Dst.Offset + ((blkRow * pd.Dst.Stride + blkCol) << 2), pd.Dst.Stride);
    }

    /// <summary>av1_subtract_plane: the whole plane block.</summary>
    internal static void SubtractPlane(AomMacroblock x, int planeBsize, int plane)
    {
        var p = x.Plane[plane];
        var pd = x.E.Plane[plane];
        int bw = BlockSizeWide[planeBsize], bh = BlockSizeHigh[planeBsize];
        SubtractBlock(bh, bw, p.SrcDiff, 0, bw, p.Src.Buf, p.Src.Offset, p.Src.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride);
    }

    /// <summary>The (horizontal, vertical) 1D kinds and flips of a TX_TYPE (htx_tab / vtx_tab; FLIPADST = ADST + flip).</summary>
    internal static void TxTypeKinds(int txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr)
    {
        static int Kind(int t1d) => t1d switch
        {
            0 => Av1InvTransform.Type1dDct, 1 or 2 => Av1InvTransform.Type1dAdst, _ => Av1InvTransform.Type1dIdentity,
        };
        int h1 = HtxTab[txType], v1 = VtxTab[txType];
        hKind = Kind(h1); vKind = Kind(v1);
        flipLr = h1 == 2; flipUd = v1 == 2;
    }

    /// <summary>av1_xform: the forward transform of the tx block's residual into x->plane[plane].coeff.</summary>
    internal static void Xform(AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize, int txType)
    {
        var p = x.Plane[plane];
        int diffStride = BlockSizeWide[planeBsize];
        TxTypeKinds(txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr);
        Av1FwdTxfmAom.ForwardRaw(p.SrcDiff.AsSpan((blkRow * diffStride + blkCol) << 2), diffStride, TxSizeWide[txSize], TxSizeHigh[txSize],
            txSize, hKind, vKind, flipUd, flipLr, p.Coeff.AsSpan(BlockOffset(block), MaxEob(txSize)));
    }

    /// <summary>av1_xform_dc_only.</summary>
    internal static void XformDcOnly(AomMacroblock x, int plane, int block, int txSize, long perPxMean)
    {
        var coeff = x.Plane[plane].Coeff.AsSpan(BlockOffset(block), MaxEob(txSize));
        coeff.Clear();
        coeff[0] = (int)((perPxMean * DcCoeffScale[txSize]) >> 12);
    }

    /// <summary>av1_setup_quant.</summary>
    internal static AomQuantParam SetupQuant(int txSize, bool useOptimizeB, int xformQuantIdx, int useQuantBAdapt)
        => new() { LogScale = AomQuantize.TxScale(txSize), TxSize = txSize, UseQuantBAdapt = useQuantBAdapt, UseOptimizeB = useOptimizeB, XformQuantIdx = xformQuantIdx };

    /// <summary>av1_quant: quantises the tx block (the quantizer libaom's 8-bit encoder dispatches), sets eobs[block] and,
    /// without the trellis to follow, txb_entropy_ctx[block].</summary>
    internal static void Quant(AomMacroblock x, int plane, int block, int txSize, int txType, in AomQuantParam qp)
    {
        var p = x.Plane[plane];
        int off = BlockOffset(block), n = MaxEob(txSize);
        var scan = ScanOf(txSize, txType);
        var coeff = p.Coeff.AsSpan(off, n);
        var q = p.Qcoeff.AsSpan(off, n);
        var dq = p.Dqcoeff.AsSpan(off, n);
        if (qp.XformQuantIdx != AomXformQuant.SkipQuant)
        {
            short[] iscan = IScanOf(txSize, txType);
            int eob = qp.XformQuantIdx switch
            {
                AomXformQuant.Fp => AomQuantize.QuantizeFpAvx2(coeff, n, iscan, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1,
                    p.Dequant0, p.Dequant1, qp.LogScale, q, dq),
                AomXformQuant.B => AomQuantize.QuantizeBAvx2(coeff, n, iscan, p.Zbin0, p.Zbin1, p.Round0, p.Round1, p.Quant0, p.Quant1,
                    p.QuantShift0, p.QuantShift1, p.Dequant0, p.Dequant1, qp.LogScale, q, dq),
                _ => throw new NotSupportedException("AV1_XFORM_QUANT_DC is not used on the all-intra path"),
            };
            p.Eobs[block] = (ushort)eob;
        }
        p.TxbEntropyCtx[block] = qp.UseOptimizeB ? (byte)0 : AomTxb.TxbEntropyContext(q, scan, p.Eobs[block]);
    }

    // scans per (tx size, tx type) (get_scan) and their inverses
    private static readonly short[]?[] IScanCache = new short[]?[ScanArrays.Length];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort[] ScanOf(int txSize, int txType) => ScanArrays[ScanOrderIdx[txSize * 16 + txType]];
    internal static short[] IScanOf(int txSize, int txType)
    {
        int k = ScanOrderIdx[txSize * 16 + txType];
        var a = IScanCache[k];
        if (a != null) return a;
        var scan = ScanArrays[k];
        a = new short[scan.Length];
        for (int i = 0; i < scan.Length; i++) a[scan[i]] = (short)i;
        return IScanCache[k] = a;
    }

    /// <summary>get_tx_type_cost (txb_rdopt.c): the tx type's symbol cost for luma (0 for chroma).</summary>
    internal static int TxTypeCost(AomMacroblock x, int plane, int txSize, int txType, int reducedTxSetUsed)
    {
        if (plane > 0) return 0;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int sqr = TxsizeSqrMap[txSize];
        int setType = ExtTxSetType(txSize, false, reducedTxSetUsed != 0);
        if (NumExtTxSet[setType] > 1 && xd.Lossless[mbmi.SegmentId] == 0)
        {
            int extTxSet = ExtTxSetIndex[0 * 16 + setType];
            if (extTxSet > 0)
            {
                int intraDir = mbmi.UseFilterIntra != 0 ? FimodeToIntradir[mbmi.FilterIntraMode] : mbmi.Mode;
                return x.ModeCosts.IntraTxTypeCosts[((extTxSet * 4 + sqr) * 13 + intraDir) * 16 + txType];
            }
        }
        return 0;
    }

    /// <summary>av1_cost_skip_txb.</summary>
    internal static int CostSkipTxb(AomCoeffCosts costs, AomTxbCtx ctx, int plane, int txSize)
        => costs.Get(AomTxb.TxsizeEntropyCtx(txSize), plane == 0 ? 0 : 1).TxbSkip[ctx.TxbSkipCtx * 2 + 1];

    /// <summary>av1_optimize_b: the trellis (or the skip cost when there is nothing to optimise). Returns the eob.</summary>
    internal static int OptimizeB(AomComp cpi, AomMacroblock x, int plane, int block, int txSize, int txType, AomTxbCtx txbCtx, out int rateCost)
    {
        var xd = x.E;
        var p = x.Plane[plane];
        int eob = p.Eobs[block];
        int segmentId = xd.Mi0.SegmentId;
        if (eob == 0 || cpi.OptimizeSegArr[segmentId] == 0 || xd.Lossless[segmentId] != 0)
        {
            rateCost = CostSkipTxb(x.CoeffCosts, txbCtx, plane, txSize);
            return eob;
        }
        int off = BlockOffset(block), n = MaxEob(txSize);
        var scan = ScanOf(txSize, txType);
        var q = p.Qcoeff.AsSpan(off, n);
        eob = AomTxb.OptimizeTxb(x.CoeffCosts, txSize, txType, plane == 0 ? 0 : 1, false, txbCtx, p.Coeff.AsSpan(off, n), q,
            p.Dqcoeff.AsSpan(off, n), eob, p.Dequant0, p.Dequant1, x.Rdmult, xd.Bd, cpi.Sharpness, cpi.Sf.tx_sf.use_chroma_trellis_rd_mult != 0,
            cpi.TuneIq, TxTypeCost(x, plane, txSize, txType, cpi.ReducedTxSetUsed), scan, out rateCost);
        p.Eobs[block] = (ushort)eob;
        p.TxbEntropyCtx[block] = AomTxb.TxbEntropyContext(q, scan, eob);
        return eob;
    }

    /// <summary>cost_coeffs / av1_cost_coeffs_txb.</summary>
    internal static int CostCoeffs(AomMacroblock x, int plane, int block, int txSize, int txType, AomTxbCtx txbCtx, int reducedTxSetUsed)
    {
        var p = x.Plane[plane];
        int off = BlockOffset(block);
        return AomTxb.CostCoeffsTxb(x.CoeffCosts, txSize, txType, plane == 0 ? 0 : 1, txbCtx, p.Qcoeff.AsSpan(off, MaxEob(txSize)),
            p.Eobs[block], TxTypeCost(x, plane, txSize, txType, reducedTxSetUsed), ScanOf(txSize, txType));
    }

    /// <summary>av1_set_txb_context.</summary>
    internal static void SetTxbContext(AomMacroblock x, int plane, int block, int txSize, Span<byte> a, Span<byte> l)
    {
        byte ctx = x.Plane[plane].TxbEntropyCtx[block];
        a.Slice(0, TxSizeWideUnit[txSize]).Fill(ctx);
        l.Slice(0, TxSizeHighUnit[txSize]).Fill(ctx);
    }

    [ThreadStatic] private static int[]? t_invScratch;

    /// <summary>av1_inverse_transform_block (8-bit): the dequantised coefficients' inverse added into dst (the coefficients
    /// are left untouched, as libaom's). eob: libaom's count (0 = none).</summary>
    internal static void InverseTransformBlock(int[] dqcoeff, int dqOff, int txType, int txSize, byte[] dst, int dstOff, int dstStride, int eob)
    {
        if (eob == 0) return;
        int n = MaxEob(txSize);
        var tmp = t_invScratch ??= new int[64 * 64];
        Array.Copy(dqcoeff, dqOff, tmp, 0, n);
        Av1InvTransform.InvTxfmAdd16(dst.AsSpan(dstOff), dstStride, tmp.AsSpan(0, n), eob - 1, txSize, Av1InvTransform.TxShift[txSize],
            (Av1TxType)txType, 8);
    }

    /// <summary>aom_sum_squares_2d_i16.</summary>
    internal static ulong SumSquares2dI16(short[] src, int off, int stride, int width, int height)
    {
        ulong ss = 0;
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++) { int v = src[off + r * stride + c]; ss += (ulong)(v * v); }
        return ss;
    }

    /// <summary>aom_sum_sse_2d_i16: the squares' sum and (added to sum) the values' sum.</summary>
    internal static ulong SumSse2dI16(short[] src, int off, int stride, int width, int height, ref int sum)
    {
        long ss = 0;
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++) { int v = src[off + r * stride + c]; ss += v * v; sum += v; }
        return (ulong)ss;
    }

    /// <summary>aom_sse (8-bit): the SSE of two w x h sample blocks.</summary>
    internal static long Sse(byte[] a, int aOff, int aStride, byte[] b, int bOff, int bStride, int width, int height)
    {
        long sse = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { int d = a[aOff + y * aStride + x] - b[bOff + y * bStride + x]; sse += d * d; }
        return sse;
    }

    /// <summary>aom_satd.</summary>
    internal static int Satd(ReadOnlySpan<int> coeff, int length)
    {
        int satd = 0;
        for (int i = 0; i < length; i++) satd += Math.Abs(coeff[i]);
        return satd;
    }

    /// <summary>av1_block_error as libaom's AVX2 kernel computes it (error_intrin_avx2.c): coefficients saturated to int16,
    /// the difference in 16-bit lanes, madd pairs zero-extended to 64 bits. Returns the error; ssz: the coefficients'.</summary>
    internal static long BlockErrorAvx2(ReadOnlySpan<int> coeff, ReadOnlySpan<int> dqcoeff, int blockSize, out long ssz)
    {
        var sse = Vector256<long>.Zero;
        var sz = Vector256<long>.Zero;
        ref int c0 = ref MemoryMarshal.GetReference(coeff);
        ref int d0 = ref MemoryMarshal.GetReference(dqcoeff);
        for (int i = 0; i < blockSize; i += 16)
        {
            var c = Avx2.Permute4x64(Avx2.PackSignedSaturate(Vector256.LoadUnsafe(ref c0, (nuint)i), Vector256.LoadUnsafe(ref c0, (nuint)(i + 8))).AsInt64(), 0xD8).AsInt16();
            var d = Avx2.Permute4x64(Avx2.PackSignedSaturate(Vector256.LoadUnsafe(ref d0, (nuint)i), Vector256.LoadUnsafe(ref d0, (nuint)(i + 8))).AsInt64(), 0xD8).AsInt16();
            var diff = Avx2.Subtract(d, c);
            var dm = Avx2.MultiplyAddAdjacent(diff, diff);
            var cm = Avx2.MultiplyAddAdjacent(c, c);
            var zero = Vector256<int>.Zero;
            sse = Avx2.Add(sse, Avx2.UnpackLow(dm, zero).AsInt64());
            sz = Avx2.Add(sz, Avx2.UnpackLow(cm, zero).AsInt64());
            sse = Avx2.Add(sse, Avx2.UnpackHigh(dm, zero).AsInt64());
            sz = Avx2.Add(sz, Avx2.UnpackHigh(cm, zero).AsInt64());
        }
        sse = Avx2.Add(sse, Avx2.ShiftRightLogical128BitLane(sse.AsByte(), 8).AsInt64());
        sz = Avx2.Add(sz, Avx2.ShiftRightLogical128BitLane(sz.AsByte(), 8).AsInt64());
        ssz = sz.GetLower().ToScalar() + sz.GetUpper().ToScalar();
        return sse.GetLower().ToScalar() + sse.GetUpper().ToScalar();
    }
}
