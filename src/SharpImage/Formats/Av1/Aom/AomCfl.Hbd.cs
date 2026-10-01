using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>
/// libaom 3.14.1 av1/common/cfl.c (CONFIG_AV1_HIGHBITDEPTH): the high bit depth CfL paths (cfl_store on 16-bit luma via
/// cfl_subsampling_hbd, cfl_predict_hbd, the uint16 DC_PRED cache). libaom's SSSE3 / AVX2 kernels compute the C's values
/// (cfl_predict_hbd adds the block's first DC_PRED sample, like the lowbd kernels).
/// </summary>
internal static unsafe partial class AomCfl
{
    /// <summary>cfl_store_dc_pred (high bit depth).</summary>
    public static void CflStoreDcPredHbd(AomMacroblockD xd, ushort* input, int predPlane, int width)
    {
        ushort[] cache = DcPredCache(xd.Cfl, predPlane);
        for (int i = 0; i < width; i++) cache[i] = input[i];
    }

    /// <summary>cfl_load_dc_pred (high bit depth).</summary>
    public static void CflLoadDcPredHbd(AomMacroblockD xd, ushort* dst, int dstStride, int txSize, int predPlane)
    {
        int width = TxSizeWide[txSize], height = TxSizeHigh[txSize];
        ushort[] cache = DcPredCache(xd.Cfl, predPlane);
        for (int j = 0; j < height; j++, dst += dstStride)
            for (int i = 0; i < width; i++) dst[i] = cache[i];
    }

    /// <summary>cfl_predict_hbd as libaom's dispatched kernels compute it: dst = clip(dc + alpha * ac, 0, (1 &lt;&lt; bd) - 1)
    /// with dc the block's first DC_PRED sample.</summary>
    public static void CflPredictHbd(short* acBufQ3, ushort* dst, int dstStride, int alphaQ3, int bd, int width, int height)
    {
        int dc = dst[0];
        int max = (1 << bd) - 1;
        for (int j = 0; j < height; j++, dst += dstStride, acBufQ3 += CFL_BUF_LINE)
            for (int i = 0; i < width; i++)
            {
                int v = GetScaledLumaQ0(alphaQ3, acBufQ3[i]) + dc;
                dst[i] = (ushort)(v < 0 ? 0 : v > max ? max : v);
            }
    }

    /// <summary>av1_cfl_predict_block (high bit depth): dst holds the DC_PRED; adds alpha * AC.</summary>
    public static void CflPredictBlockHbd(AomMacroblockD xd, ushort* dst, int dstStride, int txSize, int plane)
    {
        AomCflCtx cfl = xd.Cfl;
        AomMbModeInfo mbmi = xd.Mi0;
        if (!cfl.AreParametersComputed) CflComputeParameters(xd, txSize);
        int alphaQ3 = CflIdxToAlpha(mbmi.CflAlphaIdx, mbmi.CflAlphaSigns, plane - 1);
        fixed (short* a = cfl.AcBufQ3)
            CflPredictHbd(a, dst, dstStride, alphaQ3, xd.Bd, TxSizeWide[txSize], TxSizeHigh[txSize]);
    }

    /// <summary>av1_cfl_predict_block (high bit depth, array form).</summary>
    public static void CflPredictBlockHbd(AomMacroblockD xd, ushort[] dstBuf, int dstOffset, int dstStride, int txSize, int plane)
    {
        fixed (ushort* d = dstBuf) CflPredictBlockHbd(xd, d + dstOffset, dstStride, txSize, plane);
    }

    /// <summary>cfl_subsampling_hbd(tx_size, sub_x, sub_y)(input, input_stride, output_q3) (the _hbd_c kernels).</summary>
    public static void CflSubsamplingHbd(int txSize, int subX, int subY, ushort* input, int inputStride, ushort* outputQ3)
    {
        int width = TxSizeWide[txSize], height = TxSizeHigh[txSize];
        if (subX == 1 && subY == 1)
        {
            for (int j = 0; j < height; j += 2, input += inputStride << 1, outputQ3 += CFL_BUF_LINE)
                for (int i = 0; i < width; i += 2)
                {
                    int bot = i + inputStride;
                    outputQ3[i >> 1] = (ushort)((input[i] + input[i + 1] + input[bot] + input[bot + 1]) << 1);
                }
        }
        else if (subX == 1)
        {
            for (int j = 0; j < height; j++, input += inputStride, outputQ3 += CFL_BUF_LINE)
                for (int i = 0; i < width; i += 2) outputQ3[i >> 1] = (ushort)((input[i] + input[i + 1]) << 2);
        }
        else
        {
            for (int j = 0; j < height; j++, input += inputStride, outputQ3 += CFL_BUF_LINE)
                for (int i = 0; i < width; i++) outputQ3[i] = (ushort)(input[i] << 3);
        }
    }

    /// <summary>cfl_store with a high bit depth input.</summary>
    private static void CflStoreHbd(AomCflCtx cfl, ushort* input, int inputStride, int row, int col, int txSize)
    {
        int width = TxSizeWide[txSize], height = TxSizeHigh[txSize];
        int subX = cfl.SubsamplingX, subY = cfl.SubsamplingY;
        int storeRow = row << (MI_SIZE_LOG2 - subY);
        int storeCol = col << (MI_SIZE_LOG2 - subX);
        int storeHeight = height >> subY, storeWidth = width >> subX;
        cfl.AreParametersComputed = false;
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
        fixed (ushort* r = cfl.ReconBufQ3)
            CflSubsamplingHbd(txSize, subX, subY, input, inputStride, r + (storeRow * CFL_BUF_LINE + storeCol));
    }
}
