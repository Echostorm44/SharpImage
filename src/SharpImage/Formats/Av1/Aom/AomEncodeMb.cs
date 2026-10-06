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

/// <summary>QUANT_PARAM (av1/encoder/av1_quantize.h).</summary>
internal struct AomQuantParam
{
    public byte[]? Qmatrix, Iqmatrix;
    public int LogScale;
    public int TxSize;
    public int UseQuantBAdapt;
    public bool UseOptimizeB;
    public int XformQuantIdx;
}

// Port of libaom 3.14.1 av1/encoder/encodemb.{c,h} (subtract, xform, quant, optimize_b, set_txb_context), the
// transform-block iterator, av1_inverse_transform_block's use, and the 8-bit distortion kernels the search uses
// (aom_sum_squares_2d_i16, aom_sum_sse_2d_i16, aom_sse, av1_block_error as AVX2 runs it, aom_satd).
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static partial class AomEncodeMb
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    /// <summary>is_inter_block.</summary>
    internal static bool IsInterBlock(AomMbModeInfo mbmi) => mbmi.UseIntrabc != 0 || mbmi.RefFrame0 > 0;

    /// <summary>av1_get_tx_type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetTxType(AomMacroblockD xd, int planeType, int blkRow, int blkCol, int txSize, bool reducedTxSet)
    {
        var mbmi = xd.Mi0;
        if (xd.Lossless[mbmi.SegmentId] != 0 || TxsizeSqrUpMap[txSize] > TX_32X32) return DCT_DCT;
        int txType;
        if (planeType == 0) txType = xd.TxTypeMap[xd.TxTypeMapOffset + blkRow * xd.TxTypeMapStride + blkCol];
        else
        {
            bool isInter = IsInterBlock(mbmi);
            if (isInter)
            {
                // the luma tx type at the co-located position
                var pd = xd.Plane[planeType];
                blkRow <<= pd.SubsamplingY;
                blkCol <<= pd.SubsamplingX;
                txType = xd.TxTypeMap[xd.TxTypeMapOffset + blkRow * xd.TxTypeMapStride + blkCol];
            }
            else txType = IntraModeToTxTypeOf(mbmi, 1);
            int setType = ExtTxSetType(txSize, isInter, reducedTxSet);
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MaxBlockWide(AomMacroblockD xd, int bsize, int plane)
    {
        int w = BlockSizeWide[bsize];
        if (xd.MbToRightEdge < 0) w += xd.MbToRightEdge >> (3 + xd.Plane[plane].SubsamplingX);
        return w >> 2;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MaxBlockHigh(AomMacroblockD xd, int bsize, int plane)
    {
        int h = BlockSizeHigh[bsize];
        if (xd.MbToBottomEdge < 0) h += xd.MbToBottomEdge >> (3 + xd.Plane[plane].SubsamplingY);
        return h >> 2;
    }

    /// <summary>get_txb_dimensions (rdopt_utils.h): the tx block's visible width / height inside the frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        if (cols >= 8 && Avx2.IsSupported && rows > 0 && diffOff >= 0 && srcOff >= 0 && predOff >= 0
            && diffOff + (long)(rows - 1) * diffStride + cols <= diff.Length && srcOff + (long)(rows - 1) * srcStride + cols <= src.Length
            && predOff + (long)(rows - 1) * predStride + cols <= pred.Length)
        {
            // running row references (no per-row stride products); 8-wide blocks two rows per 256-bit subtract
            ref byte s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), srcOff);
            ref byte p0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pred), predOff);
            ref short d0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(diff), diffOff);
            if (cols == 8)
            {
                int r = 0;
                for (; r + 2 <= rows; r += 2)
                {
                    var a = Avx2.ConvertToVector256Int16(Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref s0),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s0, srcStride))).AsByte());
                    var b = Avx2.ConvertToVector256Int16(Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref p0),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, predStride))).AsByte());
                    var dv = a - b;
                    dv.GetLower().StoreUnsafe(ref d0);
                    dv.GetUpper().StoreUnsafe(ref Unsafe.Add(ref d0, diffStride));
                    s0 = ref Unsafe.Add(ref s0, 2 * srcStride);
                    p0 = ref Unsafe.Add(ref p0, 2 * predStride);
                    d0 = ref Unsafe.Add(ref d0, 2 * diffStride);
                }
                if (r < rows)
                {
                    var a = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref s0)).AsByte());
                    var b = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref p0)).AsByte());
                    (a - b).StoreUnsafe(ref d0);
                }
                return;
            }
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c += 16)
                {
                    var a = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref s0, (nuint)c));
                    var b = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p0, (nuint)c));
                    (a - b).StoreUnsafe(ref d0, (nuint)c);
                }
                s0 = ref Unsafe.Add(ref s0, srcStride);
                p0 = ref Unsafe.Add(ref p0, predStride);
                d0 = ref Unsafe.Add(ref d0, diffStride);
            }
            return;
        }
        if (cols >= 8 && (cols & 7) == 0 && !Avx2.IsSupported && Sse41.IsSupported && rows > 0 && diffOff >= 0 && srcOff >= 0 && predOff >= 0
            && diffOff + (long)(rows - 1) * diffStride + cols <= diff.Length && srcOff + (long)(rows - 1) * srcStride + cols <= src.Length
            && predOff + (long)(rows - 1) * predStride + cols <= pred.Length)
        {
            // no AVX2: 8 samples per subtract
            ref byte s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), srcOff);
            ref byte p0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pred), predOff);
            ref short d0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(diff), diffOff);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c += 8)
                {
                    var a = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s0, c))).AsByte());
                    var b = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, c))).AsByte());
                    (a - b).StoreUnsafe(ref d0, (nuint)c);
                }
                s0 = ref Unsafe.Add(ref s0, srcStride);
                p0 = ref Unsafe.Add(ref p0, predStride);
                d0 = ref Unsafe.Add(ref d0, diffStride);
            }
            return;
        }
        if (cols == 4 && Sse41.IsSupported && rows > 0 && diffOff >= 0 && srcOff >= 0 && predOff >= 0
            && diffOff + (long)(rows - 1) * diffStride + 4 <= diff.Length && srcOff + (long)(rows - 1) * srcStride + 4 <= src.Length
            && predOff + (long)(rows - 1) * predStride + 4 <= pred.Length)
        {
            ref byte s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), srcOff);
            ref byte p0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pred), predOff);
            ref short d0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(diff), diffOff);
            for (int r = 0; r < rows; r++)
            {
                var a = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref s0)).AsByte());
                var b = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref p0)).AsByte());
                Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref d0), (a - b).AsUInt64().ToScalar());
                s0 = ref Unsafe.Add(ref s0, srcStride);
                p0 = ref Unsafe.Add(ref p0, predStride);
                d0 = ref Unsafe.Add(ref d0, diffStride);
            }
            return;
        }
        for (int r = 0; r < rows; r++)
        {
            int d = diffOff + r * diffStride, s = srcOff + r * srcStride, p = predOff + r * predStride;
            for (int c = 0; c < cols; c++) diff[d + c] = (short)(src[s + c] - pred[p + c]);
        }
    }

    /// <summary>av1_subtract_txb.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SubtractTxb(AomMacroblock x, int plane, int planeBsize, int blkCol, int blkRow, int txSize)
    {
        var p = x.Plane[plane];
        var pd = x.E.Plane[plane];
        int diffStride = BlockSizeWide[planeBsize];
        if (p.Src.Buf16 != null)
        {
            AomHbd.SubtractBlock(TxSizeHigh[txSize], TxSizeWide[txSize], p.SrcDiff, (blkRow * diffStride + blkCol) << 2, diffStride,
                p.Src.Buf16, p.Src.Offset + ((blkRow * p.Src.Stride + blkCol) << 2), p.Src.Stride,
                pd.Dst.Buf16, pd.Dst.Offset + ((blkRow * pd.Dst.Stride + blkCol) << 2), pd.Dst.Stride);
            return;
        }
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
        if (p.Src.Buf16 != null)
        {
            AomHbd.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, p.Src.Buf16, p.Src.Offset, p.Src.Stride, pd.Dst.Buf16, pd.Dst.Offset, pd.Dst.Stride);
            return;
        }
        SubtractBlock(bh, bw, p.SrcDiff, 0, bw, p.Src.Buf, p.Src.Offset, p.Src.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride);
    }

    /// <summary>The (horizontal, vertical) 1D kinds and flips of a TX_TYPE (htx_tab / vtx_tab; FLIPADST = ADST + flip).</summary>
    // (hKind, vKind, flipUd, flipLr) of each TX_TYPE packed as hKind | vKind << 4 | flipUd << 8 | flipLr << 9
    private static readonly short[] TxTypeKindTab = BuildTxTypeKindTab();
    private static short[] BuildTxTypeKindTab()
    {
        var t = new short[16];
        for (int i = 0; i < 16; i++)
        {
            TxTypeKindsSlow(i, out int h, out int v, out bool ud, out bool lr);
            t[i] = (short)(h | v << 4 | (ud ? 1 << 8 : 0) | (lr ? 1 << 9 : 0));
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TxTypeKinds(int txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr)
    {
        int k = TxTypeKindTab[txType & 15];
        hKind = k & 15; vKind = (k >> 4) & 15; flipUd = (k & (1 << 8)) != 0; flipLr = (k & (1 << 9)) != 0;
    }

    private static void TxTypeKindsSlow(int txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr)
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Xform(AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize, int txType)
    {
        var p = x.Plane[plane];
        int diffStride = BlockSizeWide[planeBsize];
        // av1_lowbd_fwd_txfm: lossless 4x4 goes through av1_highbd_fwd_txfm -> highbd_fwd_txfm_4x4 -> av1_fwht4x4
        if (x.E.Lossless[x.E.Mi0.SegmentId] != 0 && txSize == TX_4X4)
        {
            Fwht4x4(p.SrcDiff.AsSpan((blkRow * diffStride + blkCol) << 2), diffStride, p.Coeff.AsSpan(BlockOffset(block), 16));
            return;
        }
        TxTypeKinds(txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr);
        // high bit depth: av1_highbd_fwd_txfm (av1_fwd_txfm2d_* in 32-bit lanes, every size)
        if (x.E.Bd > 8)
            Av1FwdTxfmAom.ForwardRawRef(p.SrcDiff.AsSpan((blkRow * diffStride + blkCol) << 2), diffStride, TxSizeWide[txSize], TxSizeHigh[txSize],
                txSize, hKind, vKind, flipUd, flipLr, p.Coeff.AsSpan(BlockOffset(block), MaxEob(txSize)));
        else
            Av1FwdTxfmAom.ForwardRaw(p.SrcDiff.AsSpan((blkRow * diffStride + blkCol) << 2), diffStride, TxSizeWide[txSize], TxSizeHigh[txSize],
                txSize, hKind, vKind, flipUd, flipLr, p.Coeff.AsSpan(BlockOffset(block), MaxEob(txSize)));
    }

    private const int UnitQuantShift = 2, UnitQuantFactor = 1 << UnitQuantShift;

    /// <summary>av1_fwht4x4_c: the 4-point reversible Walsh-Hadamard forward transform (lossless), scaled by UNIT_QUANT_FACTOR.</summary>
    internal static void Fwht4x4(ReadOnlySpan<short> input, int stride, Span<int> output)
    {
        for (int i = 0; i < 4; i++)
        {
            long a1 = input[i], b1 = input[stride + i], c1 = input[2 * stride + i], d1 = input[3 * stride + i];
            a1 += b1;
            d1 = d1 - c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;
            output[i * 4 + 0] = (int)a1;
            output[i * 4 + 1] = (int)c1;
            output[i * 4 + 2] = (int)d1;
            output[i * 4 + 3] = (int)b1;
        }
        for (int i = 0; i < 4; i++)
        {
            long a1 = output[i], b1 = output[4 + i], c1 = output[8 + i], d1 = output[12 + i];
            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;
            output[i] = (int)(a1 * UnitQuantFactor);
            output[4 + i] = (int)(c1 * UnitQuantFactor);
            output[8 + i] = (int)(d1 * UnitQuantFactor);
            output[12 + i] = (int)(b1 * UnitQuantFactor);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixelAdd(byte dest, int trans) => (byte)Math.Clamp(dest + trans, 0, 255);

    /// <summary>av1_highbd_iwht4x4_add (8-bit samples): eob &gt; 1 runs av1_highbd_iwht4x4_16_add, else av1_highbd_iwht4x4_1_add.</summary>
    internal static void IwhtAdd4x4(int[] input, int inOff, byte[] dest, int destOff, int stride, int eob)
    {
        if (eob > 1)
        {
            Span<int> output = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a1 = input[inOff + i] >> UnitQuantShift;
                int c1 = input[inOff + 4 + i] >> UnitQuantShift;
                int d1 = input[inOff + 8 + i] >> UnitQuantShift;
                int b1 = input[inOff + 12 + i] >> UnitQuantShift;
                a1 += c1;
                d1 -= b1;
                int e1 = (a1 - d1) >> 1;
                b1 = e1 - b1;
                c1 = e1 - c1;
                a1 -= b1;
                d1 += c1;
                output[i] = a1; output[4 + i] = b1; output[8 + i] = c1; output[12 + i] = d1;
            }
            for (int i = 0; i < 4; i++)
            {
                int a1 = output[i * 4], c1 = output[i * 4 + 1], d1 = output[i * 4 + 2], b1 = output[i * 4 + 3];
                a1 += c1;
                d1 -= b1;
                int e1 = (a1 - d1) >> 1;
                b1 = e1 - b1;
                c1 = e1 - c1;
                a1 -= b1;
                d1 += c1;
                int o = destOff + i;
                dest[o] = ClipPixelAdd(dest[o], a1);
                dest[o + stride] = ClipPixelAdd(dest[o + stride], b1);
                dest[o + 2 * stride] = ClipPixelAdd(dest[o + 2 * stride], c1);
                dest[o + 3 * stride] = ClipPixelAdd(dest[o + 3 * stride], d1);
            }
        }
        else
        {
            Span<int> tmp = stackalloc int[4];
            int a1 = input[inOff] >> UnitQuantShift;
            int e1 = a1 >> 1;
            a1 -= e1;
            tmp[0] = a1;
            tmp[1] = tmp[2] = tmp[3] = e1;
            for (int i = 0; i < 4; i++)
            {
                e1 = tmp[i] >> 1;
                a1 = tmp[i] - e1;
                int o = destOff + i;
                dest[o] = ClipPixelAdd(dest[o], a1);
                dest[o + stride] = ClipPixelAdd(dest[o + stride], e1);
                dest[o + 2 * stride] = ClipPixelAdd(dest[o + 2 * stride], e1);
                dest[o + 3 * stride] = ClipPixelAdd(dest[o + 3 * stride], e1);
            }
        }
    }

    /// <summary>av1_xform_dc_only.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void XformDcOnly(AomMacroblock x, int plane, int block, int txSize, long perPxMean)
    {
        var coeff = x.Plane[plane].Coeff.AsSpan(BlockOffset(block), MaxEob(txSize));
        coeff.Clear();
        coeff[0] = (int)((perPxMean * DcCoeffScale[txSize]) >> 12);
    }

    /// <summary>av1_setup_qmatrix: the plane's matrices for the tx size / type (flat for 1D and identity transforms).</summary>
    internal static void SetupQmatrix(AomMacroblock x, int plane, int txSize, int txType, ref AomQuantParam qp)
    {
        int level = x.E.Plane[plane].QmLevel;
        qp.Qmatrix = AomQm.Qmatrix(level, plane, txSize, txType);
        qp.Iqmatrix = AomQm.Iqmatrix(level, plane, txSize, txType);
    }

    /// <summary>av1_setup_quant (the matrices are reset to none).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static AomQuantParam SetupQuant(int txSize, bool useOptimizeB, int xformQuantIdx, int useQuantBAdapt)
        => new() { LogScale = AomQuantize.TxScale(txSize), TxSize = txSize, UseQuantBAdapt = useQuantBAdapt, UseOptimizeB = useOptimizeB, XformQuantIdx = xformQuantIdx };

    /// <summary>av1_quant: quantises the tx block (the quantizer libaom's 8-bit encoder dispatches), sets eobs[block] and,
    /// without the trellis to follow, txb_entropy_ctx[block].</summary>
    internal static void Quant(AomMacroblock x, int plane, int block, int txSize, int txType, in AomQuantParam qp)
    {
        // the 8-bit, matrix-free fp / b quantizers inline; the high bit depth, QM and skip paths out of line (their
        // locals made every call pay a 760-byte frame and ten saved vector registers)
        int idx = qp.XformQuantIdx;
        if ((idx == AomXformQuant.Fp || idx == AomXformQuant.B) && x.E.Bd == 8 && (qp.Qmatrix == null || qp.Iqmatrix == null))
        {
            var p8 = x.Plane[plane];
            int off8 = BlockOffset(block), n8 = MaxEob(txSize);
            short[] iscan8 = IScanOf(txSize, txType);
            var q8 = p8.Qcoeff.AsSpan(off8, n8);
            int eob8 = idx == AomXformQuant.Fp
                ? AomQuantize.QuantizeFpAvx2(p8.Coeff.AsSpan(off8, n8), n8, iscan8, p8.RoundFp0, p8.RoundFp1, p8.QuantFp0, p8.QuantFp1,
                    p8.Dequant0, p8.Dequant1, qp.LogScale, q8, p8.Dqcoeff.AsSpan(off8, n8))
                : AomQuantize.QuantizeBAvx2(p8.Coeff.AsSpan(off8, n8), n8, iscan8, p8.Zbin0, p8.Zbin1, p8.Round0, p8.Round1, p8.Quant0, p8.Quant1,
                    p8.QuantShift0, p8.QuantShift1, p8.Dequant0, p8.Dequant1, qp.LogScale, q8, p8.Dqcoeff.AsSpan(off8, n8));
            p8.Eobs[block] = (ushort)eob8;
            p8.TxbEntropyCtx[block] = qp.UseOptimizeB ? (byte)0 : AomTxb.TxbEntropyContext(q8, ScanOf(txSize, txType), eob8);
            return;
        }
        QuantGeneral(x, plane, block, txSize, txType, qp);
    }

    /// <summary>Quant's high bit depth, quantization matrix and skip-quant paths.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void QuantGeneral(AomMacroblock x, int plane, int block, int txSize, int txType, in AomQuantParam qp)
    {
        var p = x.Plane[plane];
        int off = BlockOffset(block), n = MaxEob(txSize);
        var scan = ScanOf(txSize, txType);
        var coeff = p.Coeff.AsSpan(off, n);
        var q = p.Qcoeff.AsSpan(off, n);
        var dq = p.Dqcoeff.AsSpan(off, n);
        if (qp.XformQuantIdx != AomXformQuant.SkipQuant && x.E.Bd > 8)
        {
            // quant_func_list[...][is_hbd]: av1_highbd_quantize_fp_facade / av1_highbd_quantize_b_facade
            short[] iscanH = IScanOf(txSize, txType);
            p.Eobs[block] = (ushort)(qp.Qmatrix != null && qp.Iqmatrix != null ? AomQuantizeHbdSimd.Supported && (n & 7) == 0 ? qp.XformQuantIdx switch
            {
                AomXformQuant.Fp => AomQuantizeHbdSimd.QuantizeFpHelperQm(coeff, n, iscanH, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1,
                    p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                AomXformQuant.B => AomQuantizeHbdSimd.QuantizeBHelperQm(coeff, n, iscanH, p.Zbin0, p.Zbin1, p.Round0, p.Round1, p.Quant0, p.Quant1,
                    p.QuantShift0, p.QuantShift1, p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                _ => throw new NotSupportedException("AV1_XFORM_QUANT_DC is not used on the all-intra path"),
            } : qp.XformQuantIdx switch
            {
                AomXformQuant.Fp => AomQuantizeHbd.QuantizeFpHelperQm(coeff, n, scan, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1,
                    p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                AomXformQuant.B => AomQuantizeHbd.QuantizeBHelperQm(coeff, n, scan, p.Zbin0, p.Zbin1, p.Round0, p.Round1, p.Quant0, p.Quant1,
                    p.QuantShift0, p.QuantShift1, p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                _ => throw new NotSupportedException("AV1_XFORM_QUANT_DC is not used on the all-intra path"),
            } : qp.XformQuantIdx switch
            {
                AomXformQuant.Fp => AomQuantizeHbd.QuantizeFp(coeff, n, iscanH, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1,
                    p.Dequant0, p.Dequant1, qp.LogScale, q, dq),
                AomXformQuant.B => AomQuantizeHbd.QuantizeB(coeff, n, iscanH, p.Zbin0, p.Zbin1, p.Round0, p.Round1, p.Quant0, p.Quant1,
                    p.QuantShift0, p.QuantShift1, p.Dequant0, p.Dequant1, qp.LogScale, q, dq),
                _ => throw new NotSupportedException("AV1_XFORM_QUANT_DC is not used on the all-intra path"),
            });
        }
        else if (qp.XformQuantIdx != AomXformQuant.SkipQuant)
        {
            short[] iscan = IScanOf(txSize, txType);
            int eob = qp.Qmatrix != null && qp.Iqmatrix != null ? AomQm.QmSimdSupported ? qp.XformQuantIdx switch
            {
                AomXformQuant.Fp => AomQm.QuantizeFpHelperAvx2(coeff, n, iscan, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1,
                    p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                AomXformQuant.B => AomQm.QuantizeBHelperAvx2(coeff, n, iscan, p.Zbin0, p.Zbin1, p.Round0, p.Round1, p.Quant0, p.Quant1,
                    p.QuantShift0, p.QuantShift1, p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                _ => throw new NotSupportedException("AV1_XFORM_QUANT_DC is not used on the all-intra path"),
            } : qp.XformQuantIdx switch
            {
                AomXformQuant.Fp => AomQm.QuantizeFpHelper(coeff, n, scan, p.RoundFp0, p.RoundFp1, p.QuantFp0, p.QuantFp1,
                    p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                AomXformQuant.B => AomQm.QuantizeBHelper(coeff, n, scan, p.Zbin0, p.Zbin1, p.Round0, p.Round1, p.Quant0, p.Quant1,
                    p.QuantShift0, p.QuantShift1, p.Dequant0, p.Dequant1, qp.Qmatrix, qp.Iqmatrix, qp.LogScale, q, dq),
                _ => throw new NotSupportedException("AV1_XFORM_QUANT_DC is not used on the all-intra path"),
            } : qp.XformQuantIdx switch
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
    private static readonly short[][] IScans = BuildIScans();
    private static short[][] BuildIScans()
    {
        var all = new short[ScanArrays.Length][];
        for (int k = 0; k < all.Length; k++)
        {
            var scan = ScanArrays[k];
            var a = new short[scan.Length];
            for (int i = 0; i < scan.Length; i++) a[scan[i]] = (short)i;
            all[k] = a;
        }
        return all;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort[] ScanOf(int txSize, int txType) => ScanArrays[ScanOrderIdx[txSize * 16 + txType]];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short[] IScanOf(int txSize, int txType) => IScans[ScanOrderIdx[txSize * 16 + txType]];

    /// <summary>get_tx_type_cost (txb_rdopt.c): the tx type's symbol cost for luma (0 for chroma).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TxTypeCost(AomMacroblock x, int plane, int txSize, int txType, int reducedTxSetUsed)
    {
        if (plane > 0) return 0;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int sqr = TxsizeSqrMap[txSize];
        bool isInter = IsInterBlock(mbmi);
        int setType = ExtTxSetType(txSize, isInter, reducedTxSetUsed != 0);
        if (NumExtTxSet[setType] > 1 && xd.Lossless[mbmi.SegmentId] == 0)
        {
            int extTxSet = ExtTxSetIndex[(isInter ? 6 : 0) + setType];
            if (isInter)
            {
                if (extTxSet > 0) return x.ModeCosts.InterTxTypeCosts[(extTxSet * 4 + sqr) * 16 + txType];
            }
            else if (extTxSet > 0)
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
    // out of line: inlined into search_tx_type its argument view's GC slots grew that frame's per-call zeroing
    [MethodImpl(MethodImplOptions.NoInlining)]
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
        var args = new MbTrellisArgs(cpi, x, p, plane, block, txSize, txType, txbCtx, eob, off, n);
        eob = AomTxb.OptimizeTxbCore(ref args, out rateCost);
        p.Eobs[block] = (ushort)eob;
        p.TxbEntropyCtx[block] = AomTxb.TxbEntropyContext(q, scan, eob);
        return eob;
    }

    /// <summary>av1_optimize_b's arguments to av1_optimize_txb, read from the MACROBLOCK where the trellis uses them.</summary>
    private readonly ref struct MbTrellisArgs(AomComp cpi, AomMacroblock x, AomMbPlane p, int plane, int block, int txSize, int txType,
        AomTxbCtx txbCtx, int eob, int off, int n) : AomTxb.ITrellisArgs
    {
        public AomCoeffCosts Costs => x.CoeffCosts;
        public int TxSize => txSize;
        public int TxType => txType;
        public int PlaneType => plane == 0 ? 0 : 1;
        public bool IsInter => IsInterBlock(x.E.Mi0);
        public AomTxbCtx TxbCtx => txbCtx;
        public ReadOnlySpan<int> Tcoeff => p.Coeff.AsSpan(off, n);
        public Span<int> Qcoeff => p.Qcoeff.AsSpan(off, n);
        public Span<int> Dqcoeff => p.Dqcoeff.AsSpan(off, n);
        public int Eob => eob;
        public short Dequant0 => p.Dequant0;
        public short Dequant1 => p.Dequant1;
        public int RdmultIn => x.Rdmult;
        public int BitDepth => x.E.Bd;
        public int Sharpness => cpi.Sharpness;
        public bool UseChromaTrellisRdMult => cpi.Sf.tx_sf.use_chroma_trellis_rd_mult != 0;
        public bool TuneIq => cpi.TuneIq;
        public int TxTypeCost => AomEncodeMb.TxTypeCost(x, plane, txSize, txType, cpi.ReducedTxSetUsed);
        public ReadOnlySpan<ushort> Scan => ScanOf(txSize, txType);
        public byte[]? Iqmatrix => AomQm.Iqmatrix(x.E.Plane[plane].QmLevel, plane, txSize, txType);
        public byte[]? Qmatrix => cpi.QmPsnrDistMetric ? AomQm.Qmatrix(x.E.Plane[plane].QmLevel, plane, txSize, txType) : null;
    }

    /// <summary>cost_coeffs / av1_cost_coeffs_txb.</summary>
    [SkipLocalsInit]   // as the trellis / cost kernels it inlines (a localsinit mismatch blocks the inlining)
    internal static int CostCoeffs(AomMacroblock x, int plane, int block, int txSize, int txType, AomTxbCtx txbCtx, int reducedTxSetUsed)
    {
        var p = x.Plane[plane];
        int off = BlockOffset(block);
        return AomTxb.CostCoeffsTxb(x.CoeffCosts, txSize, txType, plane == 0 ? 0 : 1, txbCtx, p.Qcoeff.AsSpan(off, MaxEob(txSize)),
            p.Eobs[block], TxTypeCost(x, plane, txSize, txType, reducedTxSetUsed), ScanOf(txSize, txType));
    }

    /// <summary>av1_set_txb_context.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetTxbContext(AomMacroblock x, int plane, int block, int txSize, Span<byte> a, Span<byte> l)
    {
        byte ctx = x.Plane[plane].TxbEntropyCtx[block];
        a.Slice(0, TxSizeWideUnit[txSize]).Fill(ctx);
        l.Slice(0, TxSizeHighUnit[txSize]).Fill(ctx);
    }


    /// <summary>av1_inverse_transform_block (8-bit): the dequantised coefficients' inverse added into dst (the coefficients
    /// are left untouched, as libaom's). eob: libaom's count (0 = none).</summary>
    internal static void InverseTransformBlock(int[] dqcoeff, int dqOff, int txType, int txSize, byte[] dst, int dstOff, int dstStride, int eob,
        bool lossless = false)
    {
        if (eob == 0) return;
        // av1_inv_txfm_add (lossless -> av1_inv_txfm_add_c -> highbd_inv_txfm_add_4x4_c -> av1_highbd_iwht4x4_add)
        if (lossless && txSize == TX_4X4) { IwhtAdd4x4(dqcoeff, dqOff, dst, dstOff, dstStride, eob); return; }
        if (AomInvTxfmLbd.Supported && AomInvTxfmLbd.SupportsSize(txSize))
        {
            // av1_lowbd_inv_txfm2d_add_avx2: reads at most the tx block's min(w, 32) x min(h, 32) coefficients and
            // writes its w x h pixels (rows of exactly w bytes)
            int w = TxSizeWide[txSize], h = TxSizeHigh[txSize];
            if ((uint)dqOff > (uint)dqcoeff.Length || dqcoeff.Length - dqOff < MaxEob(txSize) || dstOff < 0
                || (long)dstOff + (long)(h - 1) * dstStride + w > dst.Length)
                throw new ArgumentOutOfRangeException(nameof(dstOff));
            unsafe
            {
                fixed (int* pIn = &dqcoeff[dqOff])
                fixed (byte* pOut = &dst[dstOff])
                    AomInvTxfmLbd.InvTxfm2dAdd(pIn, pOut, dstStride, txType, txSize, eob);
            }
            return;
        }
        Av1InvTransform.InvTxfmAdd16(dst.AsSpan(dstOff), dstStride, dqcoeff.AsSpan(dqOff, MaxEob(txSize)), eob - 1, txSize,
            Av1InvTransform.TxShift[txSize], (Av1TxType)txType, 8, preserveCoeffs: true);
    }

    /// <summary>aom_sum_squares_2d_i16: the madd'ed square pairs (non-negative, below 2^31) zero-extended into 64-bit lanes
    /// accumulated over the whole block and reduced once; 4-wide blocks two rows per register.</summary>
    internal static ulong SumSquares2dI16(short[] src, int off, int stride, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        if (off < 0 || (long)off + (long)(height - 1) * stride + width > src.Length) throw new ArgumentOutOfRangeException(nameof(height));
        ref short s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), off);
        if (!Avx2.IsSupported)
        {
            // madd pairs zero-extended into 64-bit sums, 8 (or 4) values at a time
            ulong ss = 0;
            var acc128 = Vector128<long>.Zero;
            var zero = Vector128<int>.Zero;
            for (int r = 0; r < height; r++)
            {
                ref short row = ref Unsafe.Add(ref s0, r * stride);
                int c = 0;
                for (; c + 8 <= width; c += 8)
                {
                    var m = Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref row, (nuint)c), Vector128.LoadUnsafe(ref row, (nuint)c));
                    acc128 = Sse2.Add(acc128, Sse2.Add(Sse2.UnpackLow(m, zero).AsInt64(), Sse2.UnpackHigh(m, zero).AsInt64()));
                }
                for (; c < width; c++) { int v = Unsafe.Add(ref row, c); ss += (ulong)(v * v); }
            }
            return ss + (ulong)(acc128.GetElement(0) + acc128.GetElement(1));
        }
        var acc = Vector256<ulong>.Zero;
        ulong tail = 0;
        if (width == 4)
        {
            int r = 0;
            for (; r + 2 <= height; r += 2)
            {
                var v = Vector128.Create(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref s0)),
                    Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref s0, stride)))).AsInt16();
                acc += Avx2.ConvertToVector256Int64(Sse2.MultiplyAddAdjacent(v, v).AsUInt32()).AsUInt64();
                s0 = ref Unsafe.Add(ref s0, 2 * stride);
            }
            if (r < height)
            {
                var v = Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref s0))).AsInt16();
                acc += Avx2.ConvertToVector256Int64(Sse2.MultiplyAddAdjacent(v, v).AsUInt32()).AsUInt64();
            }
            return Vector256.Sum(acc);
        }
        for (int r = 0; r < height; r++)
        {
            int c = 0;
            for (; c + 16 <= width; c += 16)
            {
                var v = Vector256.LoadUnsafe(ref s0, (nuint)c);
                var m = Avx2.MultiplyAddAdjacent(v, v).AsUInt32();
                acc += Avx2.ConvertToVector256Int64(m.GetLower()).AsUInt64() + Avx2.ConvertToVector256Int64(m.GetUpper()).AsUInt64();
            }
            for (; c + 8 <= width; c += 8)
            {
                var v = Vector128.LoadUnsafe(ref s0, (nuint)c);
                acc += Avx2.ConvertToVector256Int64(Sse2.MultiplyAddAdjacent(v, v).AsUInt32()).AsUInt64();
            }
            for (; c + 4 <= width; c += 4)
            {
                var v = Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref s0, c)))).AsInt16();
                acc += Avx2.ConvertToVector256Int64(Sse2.MultiplyAddAdjacent(v, v).AsUInt32()).AsUInt64();
            }
            for (; c < width; c++) { int v = Unsafe.Add(ref s0, c); tail += (ulong)(v * v); }
            s0 = ref Unsafe.Add(ref s0, stride);
        }
        return Vector256.Sum(acc) + tail;
    }

    /// <summary>aom_sum_sse_2d_i16: the squares' sum and (added to sum) the values' sum.</summary>
    internal static ulong SumSse2dI16(short[] src, int off, int stride, int width, int height, ref int sum)
    {
        long ss = 0;
        if (width <= 0 || height <= 0) return 0;
        if (off < 0 || (long)off + (long)(height - 1) * stride + width > src.Length) throw new ArgumentOutOfRangeException(nameof(height));
        ref short s0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), off);
        if (!Avx2.IsSupported)
        {
            // the squares as madd pairs zero-extended into 64-bit sums, the values' sum via madd with ones
            var acc = Vector128<long>.Zero;
            var sv = Vector128<int>.Zero;
            var zero = Vector128<int>.Zero;
            var ones = Vector128.Create((short)1);
            for (int r = 0; r < height; r++)
            {
                ref short row = ref Unsafe.Add(ref s0, r * stride);
                int c = 0;
                for (; c + 8 <= width; c += 8)
                {
                    var v = Vector128.LoadUnsafe(ref row, (nuint)c);
                    var m = Sse2.MultiplyAddAdjacent(v, v);
                    acc = Sse2.Add(acc, Sse2.Add(Sse2.UnpackLow(m, zero).AsInt64(), Sse2.UnpackHigh(m, zero).AsInt64()));
                    sv = Sse2.Add(sv, Sse2.MultiplyAddAdjacent(v, ones));
                }
                for (; c < width; c++) { int v = Unsafe.Add(ref row, c); ss += v * v; sum += v; }
            }
            sum += sv.GetElement(0) + sv.GetElement(1) + sv.GetElement(2) + sv.GetElement(3);
            return (ulong)(ss + acc.GetElement(0) + acc.GetElement(1));
        }
        var one = Vector256.Create((short)1);
        var sumV = Vector256<int>.Zero;
        for (int r = 0; r < height; r++)
        {
            ref short row = ref Unsafe.Add(ref s0, r * stride);
            int c = 0;
            var acc = Vector256<long>.Zero;
            for (; c + 16 <= width; c += 16)
            {
                var v = Vector256.LoadUnsafe(ref row, (nuint)c);
                var m = Avx2.MultiplyAddAdjacent(v, v);
                acc += Avx2.ConvertToVector256Int64(m.GetLower().AsUInt32()).AsInt64() + Avx2.ConvertToVector256Int64(m.GetUpper().AsUInt32()).AsInt64();
                sumV += Avx2.MultiplyAddAdjacent(v, one);
            }
            for (; c + 8 <= width; c += 8)
            {
                var v = Vector128.LoadUnsafe(ref row, (nuint)c);
                acc += Avx2.ConvertToVector256Int64(Sse2.MultiplyAddAdjacent(v, v).AsUInt32()).AsInt64();
                sumV += Vector256.Create(Sse2.MultiplyAddAdjacent(v, one.GetLower()), Vector128<int>.Zero);
            }
            for (; c + 4 <= width; c += 4)
            {
                var v = Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref row, c)))).AsInt16();
                acc += Avx2.ConvertToVector256Int64(Sse2.MultiplyAddAdjacent(v, v).AsUInt32()).AsInt64();
                sumV += Vector256.Create(Sse2.MultiplyAddAdjacent(v, one.GetLower()), Vector128<int>.Zero);
            }
            ss += Vector256.Sum(acc);
            for (; c < width; c++) { int v = Unsafe.Add(ref row, c); ss += v * v; sum += v; }
        }
        sum += Vector256.Sum(sumV);
        return (ulong)ss;
    }

    /// <summary>A w x h byte block copy (w one of 4 / 8 / 16 / 32 / 64, the tx widths; other widths row by row).</summary>
    internal static void CopyBlock(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h)
    {
        if (srcOff < 0 || srcOff + (h - 1) * srcStride + w > src.Length || dstOff < 0 || dstOff + (h - 1) * dstStride + w > dst.Length)
            throw new ArgumentOutOfRangeException(nameof(h));
        ref byte s = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), srcOff);
        ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dst), dstOff);
        switch (w)
        {
            case 4:
                for (int r = 0; r < h; r++) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, r * dstStride), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, r * srcStride)));
                break;
            case 8:
                for (int r = 0; r < h; r++) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, r * dstStride), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, r * srcStride)));
                break;
            case 16:
                for (int r = 0; r < h; r++) Vector128.LoadUnsafe(ref Unsafe.Add(ref s, r * srcStride)).StoreUnsafe(ref Unsafe.Add(ref d, r * dstStride));
                break;
            case 32:
                for (int r = 0; r < h; r++) Vector256.LoadUnsafe(ref Unsafe.Add(ref s, r * srcStride)).StoreUnsafe(ref Unsafe.Add(ref d, r * dstStride));
                break;
            case 64:
                for (int r = 0; r < h; r++)
                {
                    ref byte sr = ref Unsafe.Add(ref s, r * srcStride);
                    ref byte dr = ref Unsafe.Add(ref d, r * dstStride);
                    Vector256.LoadUnsafe(ref sr).StoreUnsafe(ref dr);
                    Vector256.LoadUnsafe(ref sr, 32).StoreUnsafe(ref dr, 32);
                }
                break;
            default:
                for (int r = 0; r < h; r++) Array.Copy(src, srcOff + r * srcStride, dst, dstOff + r * dstStride, w);
                break;
        }
    }

    /// <summary>aom_sse (8-bit): the SSE of two w x h sample blocks.</summary>
    internal static long Sse(byte[] a, int aOff, int aStride, byte[] b, int bOff, int bStride, int width, int height)
    {
        long sse = 0;
        if (Avx2.IsSupported && width > 0 && height > 0 && height <= 64 && (width == 4 || width == 8 || width == 16) && aOff >= 0 && bOff >= 0
            && aOff + (long)(height - 1) * aStride + width <= a.Length && bOff + (long)(height - 1) * bStride + width <= b.Length)
        {
            // the tx widths up to 16 (at most 64 rows: 64 x 16 x 255^2 fits the int32 lanes): one accumulator, one reduction
            ref byte pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), aOff);
            ref byte pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), bOff);
            var acc = Vector256<int>.Zero;
            if (width == 16)
                for (int y = 0; y < height; y++)
                {
                    var d = Avx2.Subtract(Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref pa, (nuint)(y * aStride))),
                        Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref pb, (nuint)(y * bStride))));
                    acc = Avx2.Add(acc, Avx2.MultiplyAddAdjacent(d, d));
                }
            else if (width == 8)
                for (int y = 0; y < height; y += 2)
                {
                    // two rows of 8 per 256-bit register (height is even for the 8-wide tx sizes; an odd last row alone)
                    ulong r0a = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pa, y * aStride)), r0b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pb, y * bStride));
                    ulong r1a = y + 1 < height ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pa, (y + 1) * aStride)) : 0;
                    ulong r1b = y + 1 < height ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pb, (y + 1) * bStride)) : 0;
                    var d = Avx2.Subtract(Avx2.ConvertToVector256Int16(Vector128.Create(r0a, r1a).AsByte()), Avx2.ConvertToVector256Int16(Vector128.Create(r0b, r1b).AsByte()));
                    acc = Avx2.Add(acc, Avx2.MultiplyAddAdjacent(d, d));
                }
            else
                for (int y = 0; y < height; y += 4)
                {
                    // four rows of 4
                    int n = Math.Min(4, height - y);
                    uint a0 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pa, y * aStride)), b0 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pb, y * bStride));
                    uint a1 = n > 1 ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pa, (y + 1) * aStride)) : 0, b1 = n > 1 ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pb, (y + 1) * bStride)) : 0;
                    uint a2 = n > 2 ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pa, (y + 2) * aStride)) : 0, b2 = n > 2 ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pb, (y + 2) * bStride)) : 0;
                    uint a3 = n > 3 ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pa, (y + 3) * aStride)) : 0, b3 = n > 3 ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref pb, (y + 3) * bStride)) : 0;
                    var d = Avx2.Subtract(Avx2.ConvertToVector256Int16(Vector128.Create(a0, a1, a2, a3).AsByte()), Avx2.ConvertToVector256Int16(Vector128.Create(b0, b1, b2, b3).AsByte()));
                    acc = Avx2.Add(acc, Avx2.MultiplyAddAdjacent(d, d));
                }
            return (long)(uint)Vector256.Sum(acc.AsUInt32());
        }
        if (Avx2.IsSupported && width > 0 && height > 0 && aOff >= 0 && bOff >= 0
            && aOff + (long)(height - 1) * aStride + width <= a.Length && bOff + (long)(height - 1) * bStride + width <= b.Length)
        {
            // exact integer sums: the int32 lanes gather at most 8 rows (any width up to 4096) of 255^2 before being flushed to 64 bits
            ref byte a0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), aOff);
            ref byte b0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), bOff);
            var acc = Vector256<int>.Zero;
            var acc128 = Vector128<int>.Zero;
            for (int y = 0; y < height; y++)
            {
                ref byte ar = ref Unsafe.Add(ref a0, y * aStride);
                ref byte br = ref Unsafe.Add(ref b0, y * bStride);
                int x = 0;
                for (; x + 16 <= width; x += 16)
                {
                    var d = Avx2.Subtract(Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref ar, (nuint)x)),
                        Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref br, (nuint)x)));
                    acc = Avx2.Add(acc, Avx2.MultiplyAddAdjacent(d, d));
                }
                for (; x + 8 <= width; x += 8)
                {
                    var d = Sse2.Subtract(Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ar, x))).AsByte()),
                        Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref br, x))).AsByte()));
                    acc128 = Sse2.Add(acc128, Sse2.MultiplyAddAdjacent(d, d));
                }
                for (; x + 4 <= width; x += 4)
                {
                    var d = Sse2.Subtract(Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ar, x))).AsByte()),
                        Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref br, x))).AsByte()));
                    acc128 = Sse2.Add(acc128, Sse2.MultiplyAddAdjacent(d, d));
                }
                for (; x < width; x++)
                {
                    int d = Unsafe.Add(ref ar, x) - Unsafe.Add(ref br, x);
                    sse += d * d;
                }
                if ((y & 7) == 7 || y == height - 1)
                {
                    sse += (long)(uint)Vector256.Sum(acc.AsUInt32()) + (uint)Vector128.Sum(acc128.AsUInt32());
                    acc = Vector256<int>.Zero;
                    acc128 = Vector128<int>.Zero;
                }
            }
            return sse;
        }
        if (!Avx2.IsSupported && Sse41.IsSupported && width > 0 && height > 0 && aOff >= 0 && bOff >= 0
            && aOff + (long)(height - 1) * aStride + width <= a.Length && bOff + (long)(height - 1) * bStride + width <= b.Length)
        {
            // SSE4.1: the same exact integer sums, int32 lanes flushed every 8 rows
            ref byte a0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), aOff);
            ref byte b0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), bOff);
            var acc = Vector128<int>.Zero;
            for (int y = 0; y < height; y++)
            {
                ref byte ar = ref Unsafe.Add(ref a0, y * aStride);
                ref byte br = ref Unsafe.Add(ref b0, y * bStride);
                int x = 0;
                for (; x + 16 <= width; x += 16)
                {
                    var va = Vector128.LoadUnsafe(ref ar, (nuint)x);
                    var vb = Vector128.LoadUnsafe(ref br, (nuint)x);
                    var dl = Sse2.Subtract(Sse2.UnpackLow(va, Vector128<byte>.Zero).AsInt16(), Sse2.UnpackLow(vb, Vector128<byte>.Zero).AsInt16());
                    var dh = Sse2.Subtract(Sse2.UnpackHigh(va, Vector128<byte>.Zero).AsInt16(), Sse2.UnpackHigh(vb, Vector128<byte>.Zero).AsInt16());
                    acc = Sse2.Add(acc, Sse2.Add(Sse2.MultiplyAddAdjacent(dl, dl), Sse2.MultiplyAddAdjacent(dh, dh)));
                }
                for (; x + 8 <= width; x += 8)
                {
                    var d = Sse2.Subtract(Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ar, x))).AsByte()),
                        Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref br, x))).AsByte()));
                    acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(d, d));
                }
                for (; x + 4 <= width; x += 4)
                {
                    var d = Sse2.Subtract(Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ar, x))).AsByte()),
                        Sse41.ConvertToVector128Int16(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref br, x))).AsByte()));
                    acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(d, d));
                }
                for (; x < width; x++)
                {
                    int d = Unsafe.Add(ref ar, x) - Unsafe.Add(ref br, x);
                    sse += d * d;
                }
                if ((y & 7) == 7 || y == height - 1)
                {
                    sse += (uint)Vector128.Sum(acc.AsUInt32());
                    acc = Vector128<int>.Zero;
                }
            }
            return sse;
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { int d = a[aOff + y * aStride + x] - b[bOff + y * bStride + x]; sse += d * d; }
        return sse;
    }

    /// <summary>aom_satd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Satd(ReadOnlySpan<int> coeff, int length)
    {
        int satd = 0, i = 0;
        if (length >= 8)
        {
            ref int c0 = ref MemoryMarshal.GetReference(coeff);
            var acc = Vector256<int>.Zero;
            for (; i + 8 <= length; i += 8) acc += Vector256.Abs(Vector256.LoadUnsafe(ref c0, (nuint)i));
            satd = Vector256.Sum(acc);
        }
        for (; i < length; i++) satd += AbsI(coeff[i]);
        return satd;
    }

    /// <summary>av1_block_error as libaom's AVX2 kernel computes it (error_intrin_avx2.c): coefficients saturated to int16,
    /// the difference in 16-bit lanes, madd pairs zero-extended to 64 bits. Returns the error; ssz: the coefficients'.</summary>
    internal static long BlockErrorAvx2(ReadOnlySpan<int> coeff, ReadOnlySpan<int> dqcoeff, int blockSize, out long ssz)
    {
        if (!Avx2.IsSupported && Sse2.IsSupported && (blockSize & 7) == 0)
        {
            // the kernel in 128-bit lanes: int16-saturated values, the 16-bit wrapping difference, madd pairs
            // zero-extended into 64-bit sums
            var se = Vector128<long>.Zero;
            var sz2 = Vector128<long>.Zero;
            var zero = Vector128<int>.Zero;
            ref int cc0 = ref MemoryMarshal.GetReference(coeff);
            ref int dd0 = ref MemoryMarshal.GetReference(dqcoeff);
            for (int i = 0; i < blockSize; i += 8)
            {
                var c = Sse2.PackSignedSaturate(Vector128.LoadUnsafe(ref cc0, (nuint)i), Vector128.LoadUnsafe(ref cc0, (nuint)(i + 4)));
                var d = Sse2.PackSignedSaturate(Vector128.LoadUnsafe(ref dd0, (nuint)i), Vector128.LoadUnsafe(ref dd0, (nuint)(i + 4)));
                var diff = Sse2.Subtract(d, c);
                var dm = Sse2.MultiplyAddAdjacent(diff, diff);
                var cm = Sse2.MultiplyAddAdjacent(c, c);
                se = Sse2.Add(se, Sse2.Add(Sse2.UnpackLow(dm, zero).AsInt64(), Sse2.UnpackHigh(dm, zero).AsInt64()));
                sz2 = Sse2.Add(sz2, Sse2.Add(Sse2.UnpackLow(cm, zero).AsInt64(), Sse2.UnpackHigh(cm, zero).AsInt64()));
            }
            ssz = sz2.GetElement(0) + sz2.GetElement(1);
            return se.GetElement(0) + se.GetElement(1);
        }
        if (!Avx2.IsSupported)
        {
            // the kernel lane by lane: int16-saturated values, the difference wrapping in 16 bits (madd pair sums
            // zero-extended: the exact sums of squares)
            long e = 0, z = 0;
            for (int i = 0; i < blockSize; i++)
            {
                short cs = (short)Math.Clamp(coeff[i], short.MinValue, short.MaxValue);
                short ds = (short)Math.Clamp(dqcoeff[i], short.MinValue, short.MaxValue);
                int df = (short)(ds - cs);
                e += df * df; z += cs * cs;
            }
            ssz = z;
            return e;
        }
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
