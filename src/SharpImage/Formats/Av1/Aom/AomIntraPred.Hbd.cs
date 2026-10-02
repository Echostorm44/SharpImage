using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>
/// libaom 3.14.1 aom_dsp/intrapred.c (CONFIG_AV1_HIGHBITDEPTH): the high bit depth non-directional intra predictors
/// (aom_highbd_{dc,dc_top,dc_left,dc_128,v,h,smooth,smooth_v,smooth_h,paeth}_predictor_WxH). libaom dispatches SSE2 /
/// SSSE3 versions of some; they are bit-exact with the C (twin-verified), so this ports the C.
/// </summary>
[SkipLocalsInit]
internal static unsafe class AomIntraPredHbd
{
    private const int SMOOTH_V_PRED = 10, SMOOTH_H_PRED = 11;
    private const int SMOOTH_WEIGHT_LOG2_SCALE = AomIntraPred.SMOOTH_WEIGHT_LOG2_SCALE;

    /// <summary>pred_high[mode][tx_size]: V, H, SMOOTH, SMOOTH_V, SMOOTH_H and PAETH.</summary>
    public static void Pred(int mode, int txSize, ushort* dst, nint stride, ushort* above, ushort* left, int bd)
    {
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        switch (mode)
        {
            case V_PRED:
                AomIntraPred.ReplicateRow((byte*)above, (byte*)dst, stride * 2, bw * 2, bh);
                break;
            case H_PRED:
                if (bw == 4) for (int r = 0; r < bh; r++, dst += stride) *(ulong*)dst = left[r] * 0x0001000100010001UL;
                else if (bw == 8) for (int r = 0; r < bh; r++, dst += stride) System.Runtime.Intrinsics.Vector128.Create(left[r]).Store(dst);
                else
                    for (int r = 0; r < bh; r++, dst += stride)
                    {
                        var v = System.Runtime.Intrinsics.Vector256.Create(left[r]);
                        for (int c = 0; c < bw; c += 16) v.Store(dst + c);
                    }
                break;
            case SMOOTH_PRED: SmoothPredictor(dst, stride, bw, bh, above, left); break;
            case SMOOTH_V_PRED: SmoothVPredictor(dst, stride, bw, bh, above, left); break;
            case SMOOTH_H_PRED: SmoothHPredictor(dst, stride, bw, bh, above, left); break;
            case PAETH_PRED:
            {
                int ytopLeft = above[-1];
                for (int r = 0; r < bh; r++, dst += stride)
                {
                    int l = left[r];
                    for (int c = 0; c < bw; c++) dst[c] = (ushort)AomIntraPred.PaethPredictorSingle(l, above[c], ytopLeft);
                }
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>dc_pred_high[have_left][have_above][tx_size]: dc_128 / dc_top / dc_left / dc.</summary>
    public static void DcPred(int haveLeft, int haveAbove, int txSize, ushort* dst, nint stride, ushort* above, ushort* left, int bd)
    {
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        int dc;
        if (haveLeft != 0 && haveAbove != 0)
        {
            int sum = 0;
            for (int i = 0; i < bw; i++) sum += above[i];
            for (int i = 0; i < bh; i++) sum += left[i];
            if (bw == bh) dc = (sum + ((bw + bh) >> 1)) / (bw + bh);
            else
            {
                // highbd_dc_predictor_rect: divide_using_multiply_shift with HIGHBD_DC_MULTIPLIER_1X2 / 1X4, shift 17
                int mn = Math.Min(bw, bh), mx = Math.Max(bw, bh);
                int shift1 = System.Numerics.BitOperations.Log2((uint)mn);
                uint multiplier = mx == 2 * mn ? 0xAAABu : 0x6667u;
                int interm = (sum + ((bw + bh) >> 1)) >> shift1;
                dc = (int)(((uint)interm * multiplier) >> 17);
            }
        }
        else if (haveLeft != 0)
        {
            int sum = 0;
            for (int i = 0; i < bh; i++) sum += left[i];
            dc = (sum + (bh >> 1)) / bh;
        }
        else if (haveAbove != 0)
        {
            int sum = 0;
            for (int i = 0; i < bw; i++) sum += above[i];
            dc = (sum + (bw >> 1)) / bw;
        }
        else dc = 128 << (bd - 8);
        for (int r = 0; r < bh; r++, dst += stride) new Span<ushort>(dst, bw).Fill((ushort)dc);
    }

    private static void SmoothPredictor(ushort* dst, nint stride, int bw, int bh, ushort* above, ushort* left)
    {
        int belowPred = left[bh - 1], rightPred = above[bw - 1];
        ReadOnlySpan<byte> wW = AomIntraPred.SmoothWeights.Slice(bw - 4, bw);
        ReadOnlySpan<byte> wH = AomIntraPred.SmoothWeights.Slice(bh - 4, bh);
        const int log2Scale = 1 + SMOOTH_WEIGHT_LOG2_SCALE;
        const int scale = 1 << SMOOTH_WEIGHT_LOG2_SCALE;
        for (int r = 0; r < bh; ++r, dst += stride)
            for (int c = 0; c < bw; ++c)
            {
                uint thisPred = (uint)(wH[r] * above[c] + (scale - wH[r]) * belowPred + wW[c] * left[r] + (scale - wW[c]) * rightPred);
                dst[c] = (ushort)((thisPred + (1u << (log2Scale - 1))) >> log2Scale);
            }
    }

    private static void SmoothVPredictor(ushort* dst, nint stride, int bw, int bh, ushort* above, ushort* left)
    {
        int belowPred = left[bh - 1];
        ReadOnlySpan<byte> w = AomIntraPred.SmoothWeights.Slice(bh - 4, bh);
        const int log2Scale = SMOOTH_WEIGHT_LOG2_SCALE;
        const int scale = 1 << SMOOTH_WEIGHT_LOG2_SCALE;
        for (int r = 0; r < bh; r++, dst += stride)
            for (int c = 0; c < bw; ++c)
            {
                uint thisPred = (uint)(w[r] * above[c] + (scale - w[r]) * belowPred);
                dst[c] = (ushort)((thisPred + (1u << (log2Scale - 1))) >> log2Scale);
            }
    }

    private static void SmoothHPredictor(ushort* dst, nint stride, int bw, int bh, ushort* above, ushort* left)
    {
        int rightPred = above[bw - 1];
        ReadOnlySpan<byte> w = AomIntraPred.SmoothWeights.Slice(bw - 4, bw);
        const int log2Scale = SMOOTH_WEIGHT_LOG2_SCALE;
        const int scale = 1 << SMOOTH_WEIGHT_LOG2_SCALE;
        for (int r = 0; r < bh; r++, dst += stride)
            for (int c = 0; c < bw; ++c)
            {
                uint thisPred = (uint)(w[c] * left[r] + (scale - w[c]) * rightPred);
                dst[c] = (ushort)((thisPred + (1u << (log2Scale - 1))) >> log2Scale);
            }
    }
}
