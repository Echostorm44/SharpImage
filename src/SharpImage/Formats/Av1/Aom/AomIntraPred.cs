using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>
/// libaom 3.14.1 aom_dsp/intrapred.c (lowbd): the non-directional intra predictors (dc / dc_top / dc_left / dc_128, v,
/// h, smooth, smooth_v, smooth_h, paeth) for every transform size. libaom dispatches SSE2/SSSE3/AVX2 versions of these;
/// they are bit-exact with the C (verified by the AomPredTwinTests twins), so this is a port of the C.
/// Buffers are raw pointers exactly as in C: <c>above[-1]</c> is the top-left sample (read by paeth).
/// A highbd (ushort) version can be added alongside with the same shape.
/// </summary>
[SkipLocalsInit]
internal static unsafe class AomIntraPred
{
    // PREDICTION_MODE values used here (blockd.h / enums.h)
    private const int SMOOTH_V_PRED = 10, SMOOTH_H_PRED = 11;

    /// <summary>SMOOTH_WEIGHT_LOG2_SCALE (intrapred_common.h).</summary>
    internal const int SMOOTH_WEIGHT_LOG2_SCALE = 8;

    /// <summary>smooth_weights[] (intrapred_common.h): quadratic weights for bs = 4, 8, 16, 32, 64, the block of size
    /// bs starting at index bs - 4.</summary>
    internal static ReadOnlySpan<byte> SmoothWeights => new byte[]
    {
        // bs = 4
        255, 149, 85, 64,
        // bs = 8
        255, 197, 146, 105, 73, 50, 37, 32,
        // bs = 16
        255, 225, 196, 170, 145, 123, 102, 84, 68, 54, 43, 33, 26, 20, 17, 16,
        // bs = 32
        255, 240, 225, 210, 196, 182, 169, 157, 145, 133, 122, 111, 101, 92, 83, 74,
        66, 59, 52, 45, 39, 34, 29, 25, 21, 17, 14, 12, 10, 9, 8, 8,
        // bs = 64
        255, 248, 240, 233, 225, 218, 210, 203, 196, 189, 182, 176, 169, 163, 156,
        150, 144, 138, 133, 127, 121, 116, 111, 106, 101, 96, 91, 86, 82, 77, 73, 69,
        65, 61, 57, 54, 50, 47, 44, 41, 38, 35, 32, 29, 27, 25, 22, 20, 18, 16, 15,
        13, 12, 10, 9, 8, 7, 6, 6, 5, 5, 4, 4, 4,
    };

    /// <summary>pred[mode][tx_size] (reconintra.c): V, H, SMOOTH, SMOOTH_V, SMOOTH_H and PAETH.</summary>
    public static void Pred(int mode, int txSize, byte* dst, nint stride, byte* above, byte* left)
    {
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        switch (mode)
        {
            case V_PRED: VPredictor(dst, stride, bw, bh, above); break;
            case H_PRED: HPredictor(dst, stride, bw, bh, left); break;
            case SMOOTH_PRED: SmoothPredictor(dst, stride, bw, bh, above, left); break;
            case SMOOTH_V_PRED: SmoothVPredictor(dst, stride, bw, bh, above, left); break;
            case SMOOTH_H_PRED: SmoothHPredictor(dst, stride, bw, bh, above, left); break;
            case PAETH_PRED: PaethPredictor(dst, stride, bw, bh, above, left); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>dc_pred[have_left][have_above][tx_size] (reconintra.c): dc_128 / dc_top / dc_left / dc.</summary>
    public static void DcPred(int haveLeft, int haveAbove, int txSize, byte* dst, nint stride, byte* above, byte* left)
    {
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        if (haveLeft != 0)
        {
            if (haveAbove != 0) Dc(dst, stride, bw, bh, above, left);
            else DcLeftPredictor(dst, stride, bw, bh, left);
        }
        else
        {
            if (haveAbove != 0) DcTopPredictor(dst, stride, bw, bh, above);
            else Dc128Predictor(dst, stride, bw, bh);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill(byte* dst, nint stride, int bw, int bh, byte v)
    {
        for (int r = 0; r < bh; r++, dst += stride) Unsafe.InitBlockUnaligned(dst, v, (uint)bw);
    }

    public static void VPredictor(byte* dst, nint stride, int bw, int bh, byte* above)
    {
        for (int r = 0; r < bh; r++, dst += stride) Buffer.MemoryCopy(above, dst, bw, bw);
    }

    public static void HPredictor(byte* dst, nint stride, int bw, int bh, byte* left)
    {
        for (int r = 0; r < bh; r++, dst += stride) Unsafe.InitBlockUnaligned(dst, left[r], (uint)bw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AbsDiff(int a, int b) => a > b ? a - b : b - a;

    /// <summary>paeth_predictor_single.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PaethPredictorSingle(int left, int top, int topLeft)
    {
        int b = top + left - topLeft;
        int pLeft = AbsDiff(b, left), pTop = AbsDiff(b, top), pTopLeft = AbsDiff(b, topLeft);
        // Return nearest to base of left, top and top_left.
        return (pLeft <= pTop && pLeft <= pTopLeft) ? left : (pTop <= pTopLeft) ? top : topLeft;
    }

    public static void PaethPredictor(byte* dst, nint stride, int bw, int bh, byte* above, byte* left)
    {
        int ytopLeft = above[-1];
        if (Avx2.IsSupported && bw >= 8)
        {
            // per lane: base = top + left - topleft; |base - left| = |top - topleft|, |base - top| = |left - topleft|
            var tl = Vector128.Create((short)ytopLeft);
            for (int r = 0; r < bh; r++, dst += stride)
            {
                var l = Vector128.Create((short)left[r]);
                for (int c = 0; c < bw; c += 8)
                {
                    var top = Sse41.ConvertToVector128Int16(Vector128.CreateScalar(*(ulong*)(above + c)).AsByte());
                    var pLeft = Vector128.Abs(top - tl);
                    var pTop = Vector128.Abs(l - tl);
                    var pTopLeft = Vector128.Abs(top + l - tl - tl);
                    var useLeft = Vector128.LessThanOrEqual(pLeft, pTop) & Vector128.LessThanOrEqual(pLeft, pTopLeft);
                    var v = Vector128.ConditionalSelect(useLeft, l, Vector128.ConditionalSelect(Vector128.LessThanOrEqual(pTop, pTopLeft), top, tl));
                    *(ulong*)(dst + c) = Sse2.PackUnsignedSaturate(v, v).AsUInt64().ToScalar();
                }
            }
            return;
        }
        for (int r = 0; r < bh; r++, dst += stride)
        {
            int l = left[r];
            for (int c = 0; c < bw; c++) dst[c] = (byte)PaethPredictorSingle(l, above[c], ytopLeft);
        }
    }

    public static void SmoothPredictor(byte* dst, nint stride, int bw, int bh, byte* above, byte* left)
    {
        int belowPred = left[bh - 1];   // estimated by bottom-left pixel
        int rightPred = above[bw - 1];  // estimated by top-right pixel
        ReadOnlySpan<byte> smWeightsW = SmoothWeights.Slice(bw - 4, bw);
        ReadOnlySpan<byte> smWeightsH = SmoothWeights.Slice(bh - 4, bh);
        // scale = 2 * 2^SMOOTH_WEIGHT_LOG2_SCALE
        const int log2Scale = 1 + SMOOTH_WEIGHT_LOG2_SCALE;
        const int scale = 1 << SMOOTH_WEIGHT_LOG2_SCALE;
        if (Avx2.IsSupported && bw >= 8)
        {
            fixed (byte* ws = SmoothWeights)
            {
                byte* wW = ws + bw - 4;
                var rnd = Vector256.Create(1 << (log2Scale - 1));
                var sc = Vector256.Create(scale);
                var right = Vector256.Create(rightPred);
                for (int r = 0; r < bh; ++r, dst += stride)
                {
                    int wh = smWeightsH[r];
                    var whV = Vector256.Create(wh);
                    var rowPart = Vector256.Create((scale - wh) * belowPred + 0);
                    var l = Vector256.Create((int)left[r]);
                    for (int c = 0; c < bw; c += 8)
                    {
                        var a = Avx2.ConvertToVector256Int32(above + c);
                        var ww = Avx2.ConvertToVector256Int32(wW + c);
                        var v = Vector256.ShiftRightLogical(whV * a + rowPart + ww * l + (sc - ww) * right + rnd, log2Scale);
                        StoreInt8(dst + c, v);
                    }
                }
            }
            return;
        }
        for (int r = 0; r < bh; ++r, dst += stride)
        {
            int wh = smWeightsH[r], l = left[r];
            int rowPart = (scale - wh) * belowPred;
            for (int c = 0; c < bw; ++c)
            {
                int ww = smWeightsW[c];
                uint thisPred = (uint)(wh * above[c] + rowPart + ww * l + (scale - ww) * rightPred);
                dst[c] = (byte)((thisPred + (1u << (log2Scale - 1))) >> log2Scale);
            }
        }
    }

    public static void SmoothVPredictor(byte* dst, nint stride, int bw, int bh, byte* above, byte* left)
    {
        int belowPred = left[bh - 1];  // estimated by bottom-left pixel
        ReadOnlySpan<byte> smWeights = SmoothWeights.Slice(bh - 4, bh);
        const int log2Scale = SMOOTH_WEIGHT_LOG2_SCALE;
        const int scale = 1 << SMOOTH_WEIGHT_LOG2_SCALE;
        if (Avx2.IsSupported && bw >= 8)
        {
            var rnd = Vector256.Create(1 << (log2Scale - 1));
            for (int r = 0; r < bh; r++, dst += stride)
            {
                int w = smWeights[r];
                var wV = Vector256.Create(w);
                var b = Vector256.Create((scale - w) * belowPred) + rnd;
                for (int c = 0; c < bw; c += 8)
                    StoreInt8(dst + c, Vector256.ShiftRightLogical(wV * Avx2.ConvertToVector256Int32(above + c) + b, log2Scale));
            }
            return;
        }
        for (int r = 0; r < bh; r++, dst += stride)
        {
            int w = smWeights[r];
            int b = (scale - w) * belowPred;
            for (int c = 0; c < bw; ++c)
            {
                uint thisPred = (uint)(w * above[c] + b);
                dst[c] = (byte)((thisPred + (1u << (log2Scale - 1))) >> log2Scale);
            }
        }
    }

    public static void SmoothHPredictor(byte* dst, nint stride, int bw, int bh, byte* above, byte* left)
    {
        int rightPred = above[bw - 1];  // estimated by top-right pixel
        ReadOnlySpan<byte> smWeights = SmoothWeights.Slice(bw - 4, bw);
        const int log2Scale = SMOOTH_WEIGHT_LOG2_SCALE;
        const int scale = 1 << SMOOTH_WEIGHT_LOG2_SCALE;
        if (Avx2.IsSupported && bw >= 8)
        {
            fixed (byte* ws = SmoothWeights)
            {
                byte* wW = ws + bw - 4;
                var rnd = Vector256.Create(1 << (log2Scale - 1));
                var sc = Vector256.Create(scale);
                var right = Vector256.Create(rightPred);
                for (int r = 0; r < bh; r++, dst += stride)
                {
                    var l = Vector256.Create((int)left[r]);
                    for (int c = 0; c < bw; c += 8)
                    {
                        var ww = Avx2.ConvertToVector256Int32(wW + c);
                        StoreInt8(dst + c, Vector256.ShiftRightLogical(ww * l + (sc - ww) * right + rnd, log2Scale));
                    }
                }
            }
            return;
        }
        for (int r = 0; r < bh; r++, dst += stride)
        {
            int l = left[r];
            for (int c = 0; c < bw; ++c)
            {
                int w = smWeights[c];
                uint thisPred = (uint)(w * l + (scale - w) * rightPred);
                dst[c] = (byte)((thisPred + (1u << (log2Scale - 1))) >> log2Scale);
            }
        }
    }

    // 8 int lanes holding 0 .. 255 stored as 8 bytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreInt8(byte* dst, Vector256<int> v)
    {
        var w = Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper());
        *(ulong*)dst = Sse2.PackUnsignedSaturate(w, w).AsUInt64().ToScalar();
    }

    public static void Dc128Predictor(byte* dst, nint stride, int bw, int bh) => Fill(dst, stride, bw, bh, 128);

    public static void DcLeftPredictor(byte* dst, nint stride, int bw, int bh, byte* left)
    {
        int sum = 0;
        for (int i = 0; i < bh; i++) sum += left[i];
        int expectedDc = (sum + (bh >> 1)) / bh;
        Fill(dst, stride, bw, bh, (byte)expectedDc);
    }

    public static void DcTopPredictor(byte* dst, nint stride, int bw, int bh, byte* above)
    {
        int sum = 0;
        for (int i = 0; i < bw; i++) sum += above[i];
        int expectedDc = (sum + (bw >> 1)) / bw;
        Fill(dst, stride, bw, bh, (byte)expectedDc);
    }

    /// <summary>aom_dc_predictor_WxH_c: dc_predictor for squares, dc_predictor_rect for rectangles.</summary>
    public static void Dc(byte* dst, nint stride, int bw, int bh, byte* above, byte* left)
    {
        if (bw == bh) DcPredictor(dst, stride, bw, bh, above, left);
        else
        {
            // shift1 = log2(min(bw, bh)); multiplier: 1:2 -> DC_MULTIPLIER_1X2, 1:4 -> DC_MULTIPLIER_1X4
            int mn = Math.Min(bw, bh), mx = Math.Max(bw, bh);
            int shift1 = System.Numerics.BitOperations.Log2((uint)mn);
            int multiplier = mx == 2 * mn ? DC_MULTIPLIER_1X2 : DC_MULTIPLIER_1X4;
            DcPredictorRect(dst, stride, bw, bh, above, left, shift1, multiplier);
        }
    }

    public static void DcPredictor(byte* dst, nint stride, int bw, int bh, byte* above, byte* left)
    {
        int sum = 0, count = bw + bh;
        for (int i = 0; i < bw; i++) sum += above[i];
        for (int i = 0; i < bh; i++) sum += left[i];
        int expectedDc = (sum + (count >> 1)) / count;
        Fill(dst, stride, bw, bh, (byte)expectedDc);
    }

    private const int DC_MULTIPLIER_1X2 = 0x5556, DC_MULTIPLIER_1X4 = 0x3334, DC_SHIFT2 = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DivideUsingMultiplyShift(int num, int shift1, int multiplier, int shift2)
    {
        int interm = num >> shift1;
        return interm * multiplier >> shift2;
    }

    public static void DcPredictorRect(byte* dst, nint stride, int bw, int bh, byte* above, byte* left, int shift1,
        int multiplier)
    {
        int sum = 0;
        for (int i = 0; i < bw; i++) sum += above[i];
        for (int i = 0; i < bh; i++) sum += left[i];
        int expectedDc = DivideUsingMultiplyShift(sum + ((bw + bh) >> 1), shift1, multiplier, DC_SHIFT2);
        Fill(dst, stride, bw, bh, (byte)expectedDc);
    }
}
