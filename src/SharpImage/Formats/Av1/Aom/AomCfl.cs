using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>
/// libaom 3.14.1 av1/common/cfl.c + cfl.h (lowbd): chroma-from-luma. Luma is stored subsampled (Q3) into
/// cfl.recon_buf_q3 (cfl_store_tx / cfl_store_block), the AC contribution (recon - average) is built into
/// cfl.ac_buf_q3 (cfl_compute_parameters), and the chroma prediction is DC_PRED + alpha * AC (av1_cfl_predict_block).
/// libaom dispatches SSSE3/AVX2 versions of subsample / subtract_average / predict; they equal the C (twin-verified).
/// </summary>
[SkipLocalsInit]
internal static unsafe partial class AomCfl
{
    public const int CFL_BUF_LINE = AomCflCtx.CflBufLine, CFL_BUF_SQUARE = AomCflCtx.CflBufSquare;
    private const int MI_SIZE_LOG2 = 2;
    private const int CFL_SIGN_ZERO = 0, CFL_SIGN_NEG = 1, CFL_SIGN_POS = 2, CFL_SIGNS = 3;
    private const int CFL_ALPHABET_SIZE_LOG2 = 4, CFL_ALPHABET_SIZE = 1 << CFL_ALPHABET_SIZE_LOG2;
    private const int CFL_DISALLOWED = 0, CFL_ALLOWED = 1;

    /// <summary>CFL_SIGN_U(js) = (js + 1) / 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CflSignU(int js) => ((js + 1) * 11) >> 5;

    /// <summary>CFL_SIGN_V(js) = (js + 1) % 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CflSignV(int js) => (js + 1) - CFL_SIGNS * CflSignU(js);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CflIdxU(int idx) => idx >> CFL_ALPHABET_SIZE_LOG2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CflIdxV(int idx) => idx & (CFL_ALPHABET_SIZE - 1);

    /// <summary>get_cfl_pred_type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCflPredType(int plane) => plane - 1;

    /// <summary>is_cfl_allowed (cfl.h).</summary>
    public static int IsCflAllowed(AomMacroblockD xd)
    {
        AomMbModeInfo mbmi = xd.Mi0;
        int bsize = mbmi.Bsize;
        if (xd.Lossless[mbmi.SegmentId] != 0)
        {
            // In lossless, CfL is available when the partition size is equal to the transform size.
            int ssx = xd.Plane[1].SubsamplingX;
            int ssy = xd.Plane[1].SubsamplingY;
            int planeBsize = GetPlaneBlockSize(bsize, ssx, ssy);
            return planeBsize == BLOCK_4X4 ? CFL_ALLOWED : CFL_DISALLOWED;
        }
        // Spec: CfL is available to luma partitions lesser than or equal to 32x32
        return BlockSizeWide[bsize] <= 32 && BlockSizeHigh[bsize] <= 32 ? CFL_ALLOWED : CFL_DISALLOWED;
    }

    /// <summary>store_cfl_required (cfl.h); <paramref name="monochrome"/> is cm->seq_params->monochrome.</summary>
    public static int StoreCflRequired(bool monochrome, AomMacroblockD xd)
    {
        AomMbModeInfo mbmi = xd.Mi0;
        if (monochrome) return CFL_DISALLOWED;
        // For non-chroma-reference blocks, we should always store the luma pixels, in case the corresponding
        // chroma-reference block uses CfL.
        if (!xd.IsChromaRef) return CFL_ALLOWED;
        // If this block has chroma information, we know whether we're actually going to perform a CfL prediction
        return !AomReconIntra.IsInterBlock(mbmi) && mbmi.UvMode == UV_CFL_PRED ? CFL_ALLOWED : CFL_DISALLOWED;
    }

    // av1_ss_size_lookup[BLOCK_SIZES_ALL][ss_x][ss_y] (common_data.c) for get_plane_block_size
    private static ReadOnlySpan<byte> SsSizeLookup => new byte[]
    {
        BLOCK_4X4, BLOCK_4X4, BLOCK_4X4, BLOCK_4X4,   // 4X4
        BLOCK_4X8, BLOCK_4X4, BLOCK_INVALID, BLOCK_4X4,   // 4X8
        BLOCK_8X4, BLOCK_INVALID, BLOCK_4X4, BLOCK_4X4,   // 8X4
        BLOCK_8X8, BLOCK_8X4, BLOCK_4X8, BLOCK_4X4,   // 8X8
        BLOCK_8X16, BLOCK_8X8, BLOCK_INVALID, BLOCK_4X8,   // 8X16
        BLOCK_16X8, BLOCK_INVALID, BLOCK_8X8, BLOCK_8X4,   // 16X8
        BLOCK_16X16, BLOCK_16X8, BLOCK_8X16, BLOCK_8X8,   // 16X16
        BLOCK_16X32, BLOCK_16X16, BLOCK_INVALID, BLOCK_8X16,   // 16X32
        BLOCK_32X16, BLOCK_INVALID, BLOCK_16X16, BLOCK_16X8,   // 32X16
        BLOCK_32X32, BLOCK_32X16, BLOCK_16X32, BLOCK_16X16,   // 32X32
        BLOCK_32X64, BLOCK_32X32, BLOCK_INVALID, BLOCK_16X32,   // 32X64
        BLOCK_64X32, BLOCK_INVALID, BLOCK_32X32, BLOCK_32X16,   // 64X32
        BLOCK_64X64, BLOCK_64X32, BLOCK_32X64, BLOCK_32X32,   // 64X64
        BLOCK_64X128, BLOCK_64X64, BLOCK_INVALID, BLOCK_32X64,   // 64X128
        BLOCK_128X64, BLOCK_INVALID, BLOCK_64X64, BLOCK_64X32,   // 128X64
        BLOCK_128X128, BLOCK_128X64, BLOCK_64X128, BLOCK_64X64,   // 128X128
        BLOCK_4X16, BLOCK_4X8, BLOCK_INVALID, BLOCK_4X8,   // 4X16
        BLOCK_16X4, BLOCK_INVALID, BLOCK_8X4, BLOCK_8X4,   // 16X4
        BLOCK_8X32, BLOCK_8X16, BLOCK_INVALID, BLOCK_4X16,   // 8X32
        BLOCK_32X8, BLOCK_INVALID, BLOCK_16X8, BLOCK_16X4,   // 32X8
        BLOCK_16X64, BLOCK_16X32, BLOCK_INVALID, BLOCK_8X32,   // 16X64
        BLOCK_64X16, BLOCK_INVALID, BLOCK_32X16, BLOCK_32X8,   // 64X16
    };

    /// <summary>get_plane_block_size (blockd.h).</summary>
    public static int GetPlaneBlockSize(int bsize, int subsamplingX, int subsamplingY) =>
        bsize == BLOCK_INVALID ? BLOCK_INVALID : SsSizeLookup[bsize * 4 + subsamplingX * 2 + subsamplingY];

    /// <summary>cfl_init.</summary>
    public static void CflInit(AomCflCtx cfl, int subsamplingX, int subsamplingY)
    {
        Array.Clear(cfl.ReconBufQ3);
        Array.Clear(cfl.AcBufQ3);
        cfl.SubsamplingX = subsamplingX;
        cfl.SubsamplingY = subsamplingY;
        cfl.AreParametersComputed = false;
        cfl.StoreY = 0;
        // The DC_PRED cache is disabled by default and is only enabled in cfl_rd_pick_alpha
        ClearCflDcPredCacheFlags(cfl);
    }

    /// <summary>clear_cfl_dc_pred_cache_flags.</summary>
    public static void ClearCflDcPredCacheFlags(AomCflCtx cfl)
    {
        cfl.UseDcPredCache = false;
        cfl.DcPredIsCached[0] = false;
        cfl.DcPredIsCached[1] = false;
    }

    // libaom's dc_pred_cache is int16_t[2][CFL_BUF_LINE] that lowbd memcpy()s the first DC_PRED row into as bytes;
    // only cfl_load_dc_pred reads it back, so one sample per element is equivalent (and matches the hbd layout).
    private static ushort[] DcPredCache(AomCflCtx cfl, int predPlane) => predPlane == 0 ? cfl.DcPredCache0 : cfl.DcPredCache1;

    /// <summary>cfl_store_dc_pred: caches the first row of the DC_PRED.</summary>
    public static void CflStoreDcPred(AomMacroblockD xd, byte* input, int predPlane, int width)
    {
        ushort[] cache = DcPredCache(xd.Cfl, predPlane);
        for (int i = 0; i < width; i++) cache[i] = input[i];
    }

    /// <summary>cfl_load_dc_pred.</summary>
    public static void CflLoadDcPred(AomMacroblockD xd, byte* dst, int dstStride, int txSize, int predPlane)
    {
        int width = TxSizeWide[txSize];
        int height = TxSizeHigh[txSize];
        ushort[] cache = DcPredCache(xd.Cfl, predPlane);
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && width <= 32 && cache.Length >= 32)
        {
            // the row as bytes (the cache holds the DC_PRED bytes widened), stored width bytes per row
            fixed (ushort* c = cache)
            {
                var lo = System.Runtime.Intrinsics.Vector256.Load(c);
                var hi = System.Runtime.Intrinsics.Vector256.Load(c + 16);
                var row = System.Runtime.Intrinsics.X86.Avx2.Permute4x64(
                    System.Runtime.Intrinsics.X86.Avx2.PackUnsignedSaturate(lo.AsInt16(), hi.AsInt16()).AsUInt64(), 0xD8).AsByte();
                for (int j = 0; j < height; j++, dst += dstStride)
                {
                    if (width == 32) System.Runtime.Intrinsics.Vector256.Store(row, dst);
                    else if (width == 16) System.Runtime.Intrinsics.Vector128.Store(row.GetLower(), dst);
                    else if (width == 8) *(ulong*)dst = row.AsUInt64().ToScalar();
                    else *(uint*)dst = row.AsUInt32().ToScalar();
                }
            }
            return;
        }
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++) dst[i] = (byte)cache[i];
            dst += dstStride;
        }
    }

    /// <summary>cfl_pad: due to frame boundary issues, the area covered by chroma may exceed that of luma; the missing
    /// pixels repeat the last columns and/or rows.</summary>
    private static void CflPad(AomCflCtx cfl, int width, int height)
    {
        int diffWidth = width - cfl.BufWidth;
        int diffHeight = height - cfl.BufHeight;
        ushort[] buf = cfl.ReconBufQ3;

        if (diffWidth > 0)
        {
            int minHeight = height - diffHeight;
            int p = width - diffWidth;
            for (int j = 0; j < minHeight; j++)
            {
                ushort lastPixel = buf[p - 1];
                for (int i = 0; i < diffWidth; i++) buf[p + i] = lastPixel;
                p += CFL_BUF_LINE;
            }
            cfl.BufWidth = width;
        }
        if (diffHeight > 0)
        {
            int p = (height - diffHeight) * CFL_BUF_LINE;
            for (int j = 0; j < diffHeight; j++)
            {
                int last = p - CFL_BUF_LINE;
                for (int i = 0; i < width; i++) buf[p + i] = buf[last + i];
                p += CFL_BUF_LINE;
            }
            cfl.BufHeight = height;
        }
    }

    /// <summary>subtract_average_c (cfl_subtract_average_WxH): dst = src - round(avg(src)), CFL_BUF_LINE strides.</summary>
    public static void SubtractAverage(ushort* src, short* dst, int width, int height)
    {
        int numPelLog2 = System.Numerics.BitOperations.Log2((uint)(width * height));
        int roundOffset = (width * height) >> 1;
        int sum = roundOffset;
        ushort* recon = src;
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && width >= 8)
        {
            var acc = System.Runtime.Intrinsics.Vector256<int>.Zero;
            for (int j = 0; j < height; j++, recon += CFL_BUF_LINE)
                for (int i = 0; i < width; i += 8)
                    acc += System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(System.Runtime.Intrinsics.Vector128.Load(recon + i));
            int avgV = (sum + System.Runtime.Intrinsics.Vector256.Sum(acc)) >> numPelLog2;
            var a16 = System.Runtime.Intrinsics.Vector128.Create((short)avgV);
            for (int j = 0; j < height; j++, src += CFL_BUF_LINE, dst += CFL_BUF_LINE)
                for (int i = 0; i < width; i += 8)
                    System.Runtime.Intrinsics.Vector128.Store(System.Runtime.Intrinsics.X86.Sse2.Subtract(System.Runtime.Intrinsics.Vector128.Load(src + i).AsInt16(), a16), dst + i);
            return;
        }
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++) sum += recon[i];
            recon += CFL_BUF_LINE;
        }
        int avg = sum >> numPelLog2;
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++) dst[i] = (short)(src[i] - avg);
            src += CFL_BUF_LINE;
            dst += CFL_BUF_LINE;
        }
    }

    /// <summary>cfl_idx_to_alpha.</summary>
    public static int CflIdxToAlpha(int alphaIdx, int jointSign, int predType)
    {
        int alphaSign = predType == 0 ? CflSignU(jointSign) : CflSignV(jointSign);
        if (alphaSign == CFL_SIGN_ZERO) return 0;
        int absAlphaQ3 = predType == 0 ? CflIdxU(alphaIdx) : CflIdxV(alphaIdx);
        return alphaSign == CFL_SIGN_POS ? absAlphaQ3 + 1 : -absAlphaQ3 - 1;
    }

    /// <summary>get_scaled_luma_q0: ROUND_POWER_OF_TWO_SIGNED(alpha_q3 * pred_buf_q3, 6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetScaledLumaQ0(int alphaQ3, short predBufQ3)
    {
        int scaledLumaQ6 = alphaQ3 * predBufQ3;
        return scaledLumaQ6 < 0 ? -((-scaledLumaQ6 + 32) >> 6) : (scaledLumaQ6 + 32) >> 6;
    }

    /// <summary>cfl_predict_lbd as libaom's dispatched SSSE3 / AVX2 kernels compute it: dst = clip(dc + alpha * ac)
    /// where dc is the block's first DC_PRED sample dst[0] (the C adds each dst[i]; identical for the flat DC_PRED
    /// the kernels are always given).</summary>
    public static void CflPredictLbd(short* acBufQ3, byte* dst, int dstStride, int alphaQ3, int width, int height)
    {
        int dc = dst[0];
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && width >= 8)
        {
            // libaom's kernel: mulhrs(|ac|, |alpha| << 9) = (|alpha * ac| + 32) >> 6, the sign of alpha * ac put back,
            // dc added, packed with unsigned saturation (the clip)
            var aq12 = System.Runtime.Intrinsics.Vector128.Create((short)(AbsI(alphaQ3) << 9));
            var dcV = System.Runtime.Intrinsics.Vector128.Create((short)dc);
            var negAlpha = System.Runtime.Intrinsics.Vector128.Create((short)(alphaQ3 < 0 ? -1 : 1));
            for (int j = 0; j < height; j++, dst += dstStride, acBufQ3 += CFL_BUF_LINE)
                for (int i = 0; i < width; i += 8)
                {
                    var ac = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(acBufQ3 + i);
                    var s = System.Runtime.Intrinsics.X86.Ssse3.MultiplyHighRoundScale(System.Runtime.Intrinsics.X86.Ssse3.Abs(ac).AsInt16(), aq12);
                    s = System.Runtime.Intrinsics.X86.Ssse3.Sign(System.Runtime.Intrinsics.X86.Ssse3.Sign(s, ac), negAlpha);
                    var b = System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(System.Runtime.Intrinsics.X86.Sse2.Add(s, dcV), dcV);
                    *(ulong*)(dst + i) = System.Runtime.Intrinsics.Vector128.AsUInt64(b).ToScalar();
                }
            return;
        }
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported && width == 4)
        {
            var aq12 = System.Runtime.Intrinsics.Vector128.Create((short)(AbsI(alphaQ3) << 9));
            var dcV = System.Runtime.Intrinsics.Vector128.Create((short)dc);
            var negAlpha = System.Runtime.Intrinsics.Vector128.Create((short)(alphaQ3 < 0 ? -1 : 1));
            for (int j = 0; j < height; j++, dst += dstStride, acBufQ3 += CFL_BUF_LINE)
            {
                var ac = System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(*(ulong*)acBufQ3).AsInt16();
                var s = System.Runtime.Intrinsics.X86.Ssse3.MultiplyHighRoundScale(System.Runtime.Intrinsics.X86.Ssse3.Abs(ac).AsInt16(), aq12);
                s = System.Runtime.Intrinsics.X86.Ssse3.Sign(System.Runtime.Intrinsics.X86.Ssse3.Sign(s, ac), negAlpha);
                var b = System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(System.Runtime.Intrinsics.X86.Sse2.Add(s, dcV), dcV);
                *(uint*)dst = System.Runtime.Intrinsics.Vector128.AsUInt32(b).ToScalar();
            }
            return;
        }
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int v = GetScaledLumaQ0(alphaQ3, acBufQ3[i]) + dc;
                dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
            dst += dstStride;
            acBufQ3 += CFL_BUF_LINE;
        }
    }

    /// <summary>cfl_compute_parameters (static).</summary>
    public static void CflComputeParameters(AomMacroblockD xd, int txSize)
    {
        AomCflCtx cfl = xd.Cfl;
        int w = TxSizeWide[txSize], h = TxSizeHigh[txSize];
        CflPad(cfl, w, h);
        fixed (ushort* r = cfl.ReconBufQ3)
        fixed (short* a = cfl.AcBufQ3)
            SubtractAverage(r, a, w, h);
        cfl.AreParametersComputed = true;
    }

    /// <summary>av1_cfl_predict_block: dst holds the DC_PRED; adds alpha * AC.</summary>
    public static void CflPredictBlock(AomMacroblockD xd, byte* dst, int dstStride, int txSize, int plane)
    {
        AomCflCtx cfl = xd.Cfl;
        AomMbModeInfo mbmi = xd.Mi0;
        if (!cfl.AreParametersComputed) CflComputeParameters(xd, txSize);
        int alphaQ3 = CflIdxToAlpha(mbmi.CflAlphaIdx, mbmi.CflAlphaSigns, plane - 1);
        fixed (short* a = cfl.AcBufQ3)
            CflPredictLbd(a, dst, dstStride, alphaQ3, TxSizeWide[txSize], TxSizeHigh[txSize]);
    }

    /// <summary>av1_cfl_predict_block (array form).</summary>
    public static void CflPredictBlock(AomMacroblockD xd, byte[] dstBuf, int dstOffset, int dstStride, int txSize, int plane)
    {
        fixed (byte* d = dstBuf) CflPredictBlock(xd, d + dstOffset, dstStride, txSize, plane);
    }

    /// <summary>cfl_luma_subsampling_420_lbd_c.</summary>
    public static void LumaSubsampling420(byte* input, int inputStride, ushort* outputQ3, int width, int height)
    {
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported && width >= 8)
        {
            var ones = System.Runtime.Intrinsics.Vector128.Create((sbyte)1);
            for (int j = 0; j < height; j += 2, input += inputStride << 1, outputQ3 += CFL_BUF_LINE)
                for (int i = 0; i < width; i += 16)
                {
                    System.Runtime.Intrinsics.Vector128<short> s;
                    if (width - i >= 16)
                    {
                        var t = System.Runtime.Intrinsics.X86.Ssse3.MultiplyAddAdjacent(System.Runtime.Intrinsics.Vector128.Load(input + i), ones);
                        var b = System.Runtime.Intrinsics.X86.Ssse3.MultiplyAddAdjacent(System.Runtime.Intrinsics.Vector128.Load(input + i + inputStride), ones);
                        s = System.Runtime.Intrinsics.Vector128.ShiftLeft(System.Runtime.Intrinsics.X86.Sse2.Add(t, b), 1);
                        System.Runtime.Intrinsics.Vector128.Store(s.AsUInt16(), outputQ3 + (i >> 1));
                    }
                    else
                    {
                        var t = System.Runtime.Intrinsics.X86.Ssse3.MultiplyAddAdjacent(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(*(ulong*)(input + i)).AsByte(), ones);
                        var b = System.Runtime.Intrinsics.X86.Ssse3.MultiplyAddAdjacent(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(*(ulong*)(input + i + inputStride)).AsByte(), ones);
                        s = System.Runtime.Intrinsics.Vector128.ShiftLeft(System.Runtime.Intrinsics.X86.Sse2.Add(t, b), 1);
                        *(ulong*)(outputQ3 + (i >> 1)) = s.AsUInt64().ToScalar();
                    }
                }
            return;
        }
        for (int j = 0; j < height; j += 2)
        {
            for (int i = 0; i < width; i += 2)
            {
                int bot = i + inputStride;
                outputQ3[i >> 1] = (ushort)((input[i] + input[i + 1] + input[bot] + input[bot + 1]) << 1);
            }
            input += inputStride << 1;
            outputQ3 += CFL_BUF_LINE;
        }
    }

    /// <summary>cfl_luma_subsampling_422_lbd_c.</summary>
    public static void LumaSubsampling422(byte* input, int inputStride, ushort* outputQ3, int width, int height)
    {
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i += 2) outputQ3[i >> 1] = (ushort)((input[i] + input[i + 1]) << 2);
            input += inputStride;
            outputQ3 += CFL_BUF_LINE;
        }
    }

    /// <summary>cfl_luma_subsampling_444_lbd_c.</summary>
    public static void LumaSubsampling444(byte* input, int inputStride, ushort* outputQ3, int width, int height)
    {
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++) outputQ3[i] = (ushort)(input[i] << 3);
            input += inputStride;
            outputQ3 += CFL_BUF_LINE;
        }
    }

    /// <summary>cfl_subsampling_lbd(tx_size, sub_x, sub_y)(input, input_stride, output_q3).</summary>
    public static void CflSubsamplingLbd(int txSize, int subX, int subY, byte* input, int inputStride, ushort* outputQ3)
    {
        int w = TxSizeWide[txSize], h = TxSizeHigh[txSize];
        if (subX == 1)
        {
            if (subY == 1) LumaSubsampling420(input, inputStride, outputQ3, w, h);
            else LumaSubsampling422(input, inputStride, outputQ3, w, h);
        }
        else LumaSubsampling444(input, inputStride, outputQ3, w, h);
    }

    /// <summary>cfl_store (static).</summary>
    private static void CflStore(AomCflCtx cfl, byte* input, int inputStride, int row, int col, int txSize)
    {
        int width = TxSizeWide[txSize];
        int height = TxSizeHigh[txSize];
        const int txOffLog2 = MI_SIZE_LOG2;
        int subX = cfl.SubsamplingX;
        int subY = cfl.SubsamplingY;
        int storeRow = row << (txOffLog2 - subY);
        int storeCol = col << (txOffLog2 - subX);
        int storeHeight = height >> subY;
        int storeWidth = width >> subX;

        // Invalidate current parameters
        cfl.AreParametersComputed = false;

        // Store the surface of the pixel buffer that was written to, this way we can manage chroma overrun (e.g. when
        // the chroma surfaces goes beyond the frame boundary)
        if (col == 0 && row == 0)
        {
            cfl.BufWidth = storeWidth;
            cfl.BufHeight = storeHeight;
        }
        else
        {
            cfl.BufWidth = Math.Max(storeCol + storeWidth, cfl.BufWidth);
            cfl.BufHeight = Math.Max(storeRow + storeHeight, cfl.BufHeight);
        }

        // Store the input into the CfL pixel buffer
        fixed (ushort* r = cfl.ReconBufQ3)
            CflSubsamplingLbd(txSize, subX, subY, input, inputStride, r + (storeRow * CFL_BUF_LINE + storeCol));
    }

    /// <summary>sub8x8_adjust_offset: blocks smaller than 8x8 store their chroma-referenced and
    /// non-chroma-referenced luma together in the CfL buffer.</summary>
    private static void Sub8x8AdjustOffset(AomCflCtx cfl, int miRow, int miCol, ref int rowOut, ref int colOut)
    {
        // Increment row index for bottom: 8x4, 16x4 or both bottom 4x4s.
        if ((miRow & 0x01) != 0 && cfl.SubsamplingY != 0) rowOut++;
        // Increment col index for right: 4x8, 4x16 or both right 4x4s.
        if ((miCol & 0x01) != 0 && cfl.SubsamplingX != 0) colOut++;
    }

    /// <summary>cfl_store_tx: stores the reconstructed luma of the transform block at (row, col) (4x4 units).</summary>
    public static void CflStoreTx(AomMacroblockD xd, int row, int col, int txSize, int bsize)
    {
        AomCflCtx cfl = xd.Cfl;
        AomMbdPlane pd = xd.Plane[0];
        int stride = pd.Dst.Stride;
        int offset = pd.Dst.Offset + ((row * stride + col) << MI_SIZE_LOG2);
        if (BlockSizeHigh[bsize] == 4 || BlockSizeWide[bsize] == 4)
        {
            // Only dimensions of size 4 can have an odd offset.
            Sub8x8AdjustOffset(cfl, xd.MiRow, xd.MiCol, ref row, ref col);
        }
        if (pd.Dst.Buf16 != null)
        {
            fixed (ushort* buf16 = pd.Dst.Buf16) CflStoreHbd(cfl, buf16 + offset, stride, row, col, txSize);
            return;
        }
        fixed (byte* buf = pd.Dst.Buf) CflStore(cfl, buf + offset, stride, row, col, txSize);
    }

    /// <summary>max_block_wide (av1_common_int.h), in 4x4 units.</summary>
    public static int MaxBlockWide(AomMacroblockD xd, int bsize, int plane)
    {
        int maxBlocksWide = BlockSizeWide[bsize];
        if (xd.MbToRightEdge < 0) maxBlocksWide += xd.MbToRightEdge >> (3 + xd.Plane[plane].SubsamplingX);
        // Scale the width in the transform block unit.
        return maxBlocksWide >> MI_SIZE_LOG2;
    }

    /// <summary>max_block_high (av1_common_int.h), in 4x4 units.</summary>
    public static int MaxBlockHigh(AomMacroblockD xd, int bsize, int plane)
    {
        int maxBlocksHigh = BlockSizeHigh[bsize];
        if (xd.MbToBottomEdge < 0) maxBlocksHigh += xd.MbToBottomEdge >> (3 + xd.Plane[plane].SubsamplingY);
        return maxBlocksHigh >> MI_SIZE_LOG2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignPowerOfTwo(int value, int n) => (value + ((1 << n) - 1)) & ~((1 << n) - 1);

    private static int MaxIntraBlockWidth(AomMacroblockD xd, int planeBsize, int plane, int txSize) =>
        AlignPowerOfTwo(MaxBlockWide(xd, planeBsize, plane) << MI_SIZE_LOG2, TxSizeWideLog2[txSize]);

    private static int MaxIntraBlockHeight(AomMacroblockD xd, int planeBsize, int plane, int txSize) =>
        AlignPowerOfTwo(MaxBlockHigh(xd, planeBsize, plane) << MI_SIZE_LOG2, TxSizeHighLog2[txSize]);

    /// <summary>get_sqr_tx_size (av1_common_int.h).</summary>
    private static int GetSqrTxSize(int txDim) => txDim switch
    {
        128 or 64 => TX_64X64,
        32 => TX_32X32,
        16 => TX_16X16,
        8 => TX_8X8,
        _ => TX_4X4,
    };

    /// <summary>get_tx_size (av1_common_int.h): the transform size of a width x height area.</summary>
    public static int GetTxSize(int width, int height)
    {
        if (width == height) return GetSqrTxSize(width);
        if (width < height)
        {
            if (width + width == height)
            {
                switch (width)
                {
                    case 4: return TX_4X8;
                    case 8: return TX_8X16;
                    case 16: return TX_16X32;
                    case 32: return TX_32X64;
                }
            }
            else
            {
                switch (width)
                {
                    case 4: return TX_4X16;
                    case 8: return TX_8X32;
                    case 16: return TX_16X64;
                }
            }
        }
        else
        {
            if (height + height == width)
            {
                switch (height)
                {
                    case 4: return TX_8X4;
                    case 8: return TX_16X8;
                    case 16: return TX_32X16;
                    case 32: return TX_64X32;
                }
            }
            else
            {
                switch (height)
                {
                    case 4: return TX_16X4;
                    case 8: return TX_32X8;
                    case 16: return TX_64X16;
                }
            }
        }
        return TX_4X4;
    }

    /// <summary>cfl_store_block: stores the whole (visible) reconstructed luma block.</summary>
    public static void CflStoreBlock(AomMacroblockD xd, int bsize, int txSize)
    {
        AomCflCtx cfl = xd.Cfl;
        AomMbdPlane pd = xd.Plane[0];
        int row = 0;
        int col = 0;

        if (BlockSizeHigh[bsize] == 4 || BlockSizeWide[bsize] == 4)
            Sub8x8AdjustOffset(cfl, xd.MiRow, xd.MiCol, ref row, ref col);
        int width = MaxIntraBlockWidth(xd, bsize, 0, txSize);
        int height = MaxIntraBlockHeight(xd, bsize, 0, txSize);
        txSize = GetTxSize(width, height);
        if (pd.Dst.Buf16 != null)
        {
            fixed (ushort* buf16 = pd.Dst.Buf16) CflStoreHbd(cfl, buf16 + pd.Dst.Offset, pd.Dst.Stride, row, col, txSize);
            return;
        }
        fixed (byte* buf = pd.Dst.Buf) CflStore(cfl, buf + pd.Dst.Offset, pd.Dst.Stride, row, col, txSize);
    }
}
