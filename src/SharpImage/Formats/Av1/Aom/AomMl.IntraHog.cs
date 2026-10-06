using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// The intra directional-mode prune from a histogram of oriented gradients (av1/encoder/intra_mode_search_utils.h:
// prune_intra_mode_with_hog and its helpers). src is the plane's source block (x->plane[plane].src.buf), rows / cols
// collect_hog_data's visible block dimensions: ((mb_to_bottom_edge >= 0 ? bh : (mb_to_bottom_edge >> 3) + bh) >> ss_y)
// and the same for the width. The superblock gradient-cache path (generate_hog_using_gradient_cache) produces the
// same floats as the direct one ported here.
internal static partial class AomMl
{
    internal const int HogBins = 32;

    private static readonly int[] HistBinThresholds =
    [
        -1334015, -441798, -261605, -183158, -138560, -109331, -88359, -72303,
        -59392, -48579, -39272, -30982, -23445, -16400, -9715, -3194,
        3227, 9748, 16433, 23478, 31015, 39305, 48611, 59425,
        72336, 88392, 109364, 138593, 183191, 261638, 441831, int.MaxValue,
    ];

    /// <summary>get_hist_bin_idx: the gradient-direction bin of (dx, dy), dx != 0.</summary>
    internal static int GetHistBinIdx(int dx, int dy)
    {
        int ratio = unchecked(dy * (1 << 16)) / dx;
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            // the first bin whose threshold is >= ratio = the count of thresholds below it (they ascend, the last is
            // INT32_MAX): libaom's bisection result, without its data-dependent branches
            var r = System.Runtime.Intrinsics.Vector256.Create(ratio);
            ref int t0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(HistBinThresholds);
            uint m0 = System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.LessThan(System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref t0), r));
            uint m1 = System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.LessThan(System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref t0, 8), r));
            uint m2 = System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.LessThan(System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref t0, 16), r));
            uint m3 = System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.LessThan(System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref t0, 24), r));
            return System.Numerics.BitOperations.PopCount(m0 | (m1 << 8) | (m2 << 16) | (m3 << 24));
        }
        int lo, hi;
        if (ratio <= HistBinThresholds[7]) { lo = 0; hi = 7; }
        else if (ratio <= HistBinThresholds[15]) { lo = 8; hi = 15; }
        else if (ratio <= HistBinThresholds[23]) { lo = 16; hi = 23; }
        else { lo = 24; hi = 31; }
        for (int idx = lo; idx <= hi; idx++)
            if (ratio <= HistBinThresholds[idx]) return idx;
        return HogBins - 1;
    }

    // the Sobel-gradient accumulation of lowbd_generate_hog / highbd_generate_hog for one interior sample
    private static void HogAccumulate(int dx, int dy, ref float total, Span<float> hist)
    {
        if (dx == 0 && dy == 0) return;
        int temp = AbsI(dx) + AbsI(dy);
        if (temp == 0) return;
        total += temp;
        if (dx == 0)
        {
            hist[0] += temp / 2;
            hist[HogBins - 1] += temp / 2;
        }
        else hist[GetHistBinIdx(dx, dy)] += temp;
    }

    /// <summary>lowbd_generate_hog: adds the block's gradient histogram into hist[32] and normalises it
    /// (normalize_hog, by a total that starts at 0.1f).</summary>
    internal static void GenerateHog(ReadOnlySpan<byte> src, int stride, int rows, int cols, Span<float> hist)
    {
        float total = 0.1f;
        for (int r = 1; r < rows - 1; ++r)
        {
            int o = r * stride;
            for (int c = 1; c < cols - 1; ++c)
            {
                int p = o + c;
                int dx = (src[p + 1 - stride] + 2 * src[p + 1] + src[p + 1 + stride]) -
                         (src[p - 1 - stride] + 2 * src[p - 1] + src[p - 1 + stride]);
                int dy = (src[p + stride - 1] + 2 * src[p + stride] + src[p + stride + 1]) -
                         (src[p - stride - 1] + 2 * src[p - stride] + src[p - stride + 1]);
                HogAccumulate(dx, dy, ref total, hist);
            }
        }
        for (int i = 0; i < HogBins; ++i) hist[i] /= total;
    }

    /// <summary>highbd_generate_hog.</summary>
    internal static void GenerateHog(ReadOnlySpan<ushort> src, int stride, int rows, int cols, Span<float> hist)
    {
        float total = 0.1f;
        for (int r = 1; r < rows - 1; ++r)
        {
            int o = r * stride;
            for (int c = 1; c < cols - 1; ++c)
            {
                int p = o + c;
                int dx = (src[p + 1 - stride] + 2 * src[p + 1] + src[p + 1 + stride]) -
                         (src[p - 1 - stride] + 2 * src[p - 1] + src[p - 1 + stride]);
                int dy = (src[p + stride - 1] + 2 * src[p + stride] + src[p + stride + 1]) -
                         (src[p - stride - 1] + 2 * src[p - stride] + src[p - stride + 1]);
                HogAccumulate(dx, dy, ref total, hist);
            }
        }
        for (int i = 0; i < HogBins; ++i) hist[i] /= total;
    }

    /// <summary>collect_hog_data (lowbd source): the normalised histogram, scaled by (1 + ss_x) * (1 + ss_y).
    /// hog[32] must start zeroed.</summary>
    internal static void CollectHogData(ReadOnlySpan<byte> src, int srcStride, int rows, int cols, int ssX, int ssY,
        Span<float> hog)
    {
        GenerateHog(src, srcStride, rows, cols, hog);
        for (int b = 0; b < HogBins; ++b) hog[b] *= (1 + ssX) * (1 + ssY);
    }

    /// <summary>collect_hog_data (high-bitdepth source).</summary>
    internal static void CollectHogData(ReadOnlySpan<ushort> src, int srcStride, int rows, int cols, int ssX, int ssY,
        Span<float> hog)
    {
        GenerateHog(src, srcStride, rows, cols, hog);
        for (int b = 0; b < HogBins; ++b) hog[b] *= (1 + ssX) * (1 + ssY);
    }

    /// <summary>lowbd_compute_gradient_info_sb over rows / cols [1, vis - 1) of a superblock (row stride sbW in the cache).</summary>
    internal static void ComputeGradientInfoSb(ReadOnlySpan<byte> src, int stride, int sbW, int visW, int visH, ushort[] absSum, sbyte[] bin, int baseOff)
    {
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && visW > 2 && visH > 2 && src.Length >= (visH - 1) * stride + visW + 1
            && baseOff >= 0 && baseOff + (visH - 1) * sbW + visW <= Math.Min(absSum.Length, bin.Length))
        {
            ComputeGradientInfoSbAvx2(src, stride, sbW, visW, visH, absSum, bin, baseOff);
            return;
        }
        if (System.Runtime.Intrinsics.X86.Sse41.IsSupported && visW > 2 && visH > 2 && src.Length >= (visH - 1) * stride + visW + 1
            && baseOff >= 0 && baseOff + (visH - 1) * sbW + visW <= Math.Min(absSum.Length, bin.Length))
        {
            ComputeGradientInfoSbSse41(src, stride, sbW, visW, visH, absSum, bin, baseOff);
            return;
        }
        for (int r = 1; r < visH - 1; ++r)
        {
            int o = r * stride;
            for (int c = 1; c < visW - 1; ++c)
            {
                int p = o + c;
                int dx = (src[p + 1 - stride] + 2 * src[p + 1] + src[p + 1 + stride]) -
                         (src[p - 1 - stride] + 2 * src[p - 1] + src[p - 1 + stride]);
                int dy = (src[p + stride - 1] + 2 * src[p + stride] + src[p + stride + 1]) -
                         (src[p - stride - 1] + 2 * src[p - stride] + src[p - stride + 1]);
                int i = baseOff + r * sbW + c;
                absSum[i] = (ushort)(AbsI(dx) + AbsI(dy));
                bin[i] = (sbyte)(dx != 0 ? GetHistBinIdx(dx, dy) : -1);
            }
        }
    }

    /// <summary>lowbd_compute_gradient_info_sb, eight samples at a time: the Sobel sums in int16 lanes, get_hist_bin_idx's
    /// ratio (dy &lt;&lt; 16) / dx through a double division truncated to int (|dy &lt;&lt; 16| &lt; 2^27, |dx| &lt;= 1020: the true
    /// quotient is at least 1/1020 from any integer, far beyond the division's rounding, so the truncation is exact), the
    /// bin as the count of thresholds below it; the columns past the last full group of eight one by one.</summary>
    private static void ComputeGradientInfoSbAvx2(ReadOnlySpan<byte> src, int stride, int sbW, int visW, int visH, ushort[] absSum, sbyte[] bin, int baseOff)
    {
        ref byte s0 = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(src);
        ref int thr = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(HistBinThresholds);
        ref ushort a0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(absSum);
        ref sbyte b0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(bin);
        for (int r = 1; r < visH - 1; ++r)
        {
            int o = r * stride;
            int c = 1;
            for (; c + 8 <= visW - 1; c += 8)
            {
                int p = o + c;
                var um = L8(ref s0, p - stride - 1); var u0 = L8(ref s0, p - stride); var up = L8(ref s0, p - stride + 1);
                var mm = L8(ref s0, p - 1); var mp = L8(ref s0, p + 1);
                var dm = L8(ref s0, p + stride - 1); var d0 = L8(ref s0, p + stride); var dp = L8(ref s0, p + stride + 1);
                var dx = (up + mp + mp + dp) - (um + mm + mm + dm);
                var dy = (dm + d0 + d0 + dp) - (um + u0 + u0 + up);
                int i = baseOff + r * sbW + c;
                (System.Runtime.Intrinsics.Vector128.Abs(dx) + System.Runtime.Intrinsics.Vector128.Abs(dy)).AsUInt16()
                    .StoreUnsafe(ref a0, (nuint)i);
                var dx32 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(dx);
                var num = System.Runtime.Intrinsics.Vector256.ShiftLeft(System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(dy), 16);
                var qLo = System.Runtime.Intrinsics.X86.Avx.ConvertToVector128Int32WithTruncation(System.Runtime.Intrinsics.X86.Avx.Divide(
                    System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Double(num.GetLower()), System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Double(dx32.GetLower())));
                var qHi = System.Runtime.Intrinsics.X86.Avx.ConvertToVector128Int32WithTruncation(System.Runtime.Intrinsics.X86.Avx.Divide(
                    System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Double(num.GetUpper()), System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Double(dx32.GetUpper())));
                var ratio = System.Runtime.Intrinsics.Vector256.Create(qLo, qHi);
                var cnt = System.Runtime.Intrinsics.Vector256<int>.Zero;
                for (int k = 0; k < HogBins; k++)
                    cnt -= System.Runtime.Intrinsics.Vector256.LessThan(System.Runtime.Intrinsics.Vector256.Create(Unsafe.Add(ref thr, k)), ratio);
                var res = System.Runtime.Intrinsics.Vector256.ConditionalSelect(
                    System.Runtime.Intrinsics.Vector256.Equals(dx32, System.Runtime.Intrinsics.Vector256<int>.Zero),
                    System.Runtime.Intrinsics.Vector256.Create(-1), cnt);
                var w = System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(res.GetLower(), res.GetUpper());
                Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref b0, i)),
                    System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(w, w).AsUInt64().ToScalar());
            }
            for (; c < visW - 1; ++c)
            {
                int p = o + c;
                int dx = (src[p + 1 - stride] + 2 * src[p + 1] + src[p + 1 + stride]) -
                         (src[p - 1 - stride] + 2 * src[p - 1] + src[p - 1 + stride]);
                int dy = (src[p + stride - 1] + 2 * src[p + stride] + src[p + stride + 1]) -
                         (src[p - stride - 1] + 2 * src[p - stride] + src[p - stride + 1]);
                int i = baseOff + r * sbW + c;
                absSum[i] = (ushort)(AbsI(dx) + AbsI(dy));
                bin[i] = (sbyte)(dx != 0 ? GetHistBinIdx(dx, dy) : -1);
            }
        }
    }

    // (dy << 16) / dx truncated, two lanes through a double division (see ComputeGradientInfoSbAvx2: exact)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static System.Runtime.Intrinsics.Vector128<int> Ratio4(System.Runtime.Intrinsics.Vector128<int> num, System.Runtime.Intrinsics.Vector128<int> den)
    {
        var lo = System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Int32WithTruncation(System.Runtime.Intrinsics.X86.Sse2.Divide(
            System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Double(num), System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Double(den)));
        var nh = System.Runtime.Intrinsics.X86.Sse2.ShiftRightLogical128BitLane(num, 8);
        var dh = System.Runtime.Intrinsics.X86.Sse2.ShiftRightLogical128BitLane(den, 8);
        var hi = System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Int32WithTruncation(System.Runtime.Intrinsics.X86.Sse2.Divide(
            System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Double(nh), System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Double(dh)));
        return System.Runtime.Intrinsics.X86.Sse2.UnpackLow(lo.AsInt64(), hi.AsInt64()).AsInt32();
    }

    /// <summary><see cref="ComputeGradientInfoSbAvx2"/> in SSE4.1 (no AVX2): the same values.</summary>
    private static void ComputeGradientInfoSbSse41(ReadOnlySpan<byte> src, int stride, int sbW, int visW, int visH, ushort[] absSum, sbyte[] bin, int baseOff)
    {
        ref byte s0 = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(src);
        ref int thr = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(HistBinThresholds);
        ref ushort a0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(absSum);
        ref sbyte b0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(bin);
        for (int r = 1; r < visH - 1; ++r)
        {
            int o = r * stride;
            int c = 1;
            for (; c + 8 <= visW - 1; c += 8)
            {
                int p = o + c;
                var um = L8(ref s0, p - stride - 1); var u0 = L8(ref s0, p - stride); var up = L8(ref s0, p - stride + 1);
                var mm = L8(ref s0, p - 1); var mp = L8(ref s0, p + 1);
                var dm = L8(ref s0, p + stride - 1); var d0 = L8(ref s0, p + stride); var dp = L8(ref s0, p + stride + 1);
                var dx = (up + mp + mp + dp) - (um + mm + mm + dm);
                var dy = (dm + d0 + d0 + dp) - (um + u0 + u0 + up);
                int i = baseOff + r * sbW + c;
                (System.Runtime.Intrinsics.Vector128.Abs(dx) + System.Runtime.Intrinsics.Vector128.Abs(dy)).AsUInt16()
                    .StoreUnsafe(ref a0, (nuint)i);
                var dxL = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int32(dx);
                var dxH = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int32(System.Runtime.Intrinsics.X86.Sse2.ShiftRightLogical128BitLane(dx, 8));
                var dyL = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int32(dy);
                var dyH = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int32(System.Runtime.Intrinsics.X86.Sse2.ShiftRightLogical128BitLane(dy, 8));
                var ratioL = Ratio4(System.Runtime.Intrinsics.Vector128.ShiftLeft(dyL, 16), dxL);
                var ratioH = Ratio4(System.Runtime.Intrinsics.Vector128.ShiftLeft(dyH, 16), dxH);
                var cntL = System.Runtime.Intrinsics.Vector128<int>.Zero;
                var cntH = System.Runtime.Intrinsics.Vector128<int>.Zero;
                for (int k = 0; k < HogBins; k++)
                {
                    var t = System.Runtime.Intrinsics.Vector128.Create(Unsafe.Add(ref thr, k));
                    cntL -= System.Runtime.Intrinsics.Vector128.LessThan(t, ratioL);
                    cntH -= System.Runtime.Intrinsics.Vector128.LessThan(t, ratioH);
                }
                var resL = System.Runtime.Intrinsics.Vector128.ConditionalSelect(System.Runtime.Intrinsics.Vector128.Equals(dxL, System.Runtime.Intrinsics.Vector128<int>.Zero),
                    System.Runtime.Intrinsics.Vector128.Create(-1), cntL);
                var resH = System.Runtime.Intrinsics.Vector128.ConditionalSelect(System.Runtime.Intrinsics.Vector128.Equals(dxH, System.Runtime.Intrinsics.Vector128<int>.Zero),
                    System.Runtime.Intrinsics.Vector128.Create(-1), cntH);
                var w = System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(resL, resH);
                Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref b0, i)),
                    System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(w, w).AsUInt64().ToScalar());
            }
            for (; c < visW - 1; ++c)
            {
                int p = o + c;
                int dx = (src[p + 1 - stride] + 2 * src[p + 1] + src[p + 1 + stride]) -
                         (src[p - 1 - stride] + 2 * src[p - 1] + src[p - 1 + stride]);
                int dy = (src[p + stride - 1] + 2 * src[p + stride] + src[p + stride + 1]) -
                         (src[p - stride - 1] + 2 * src[p - stride] + src[p - stride + 1]);
                int i = baseOff + r * sbW + c;
                absSum[i] = (ushort)(AbsI(dx) + AbsI(dy));
                bin[i] = (sbyte)(dx != 0 ? GetHistBinIdx(dx, dy) : -1);
            }
        }
    }

    // 8 samples zero-extended to int16 lanes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static System.Runtime.Intrinsics.Vector128<short> L8(ref byte s, int off) =>
        System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int16(
            System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, off))).AsByte());

    /// <summary>highbd_compute_gradient_info_sb.</summary>
    internal static void ComputeGradientInfoSb(ReadOnlySpan<ushort> src, int stride, int sbW, int visW, int visH, ushort[] absSum, sbyte[] bin, int baseOff)
    {
        for (int r = 1; r < visH - 1; ++r)
        {
            int o = r * stride;
            for (int c = 1; c < visW - 1; ++c)
            {
                int p = o + c;
                int dx = (src[p + 1 - stride] + 2 * src[p + 1] + src[p + 1 + stride]) -
                         (src[p - 1 - stride] + 2 * src[p - 1] + src[p - 1 + stride]);
                int dy = (src[p + stride - 1] + 2 * src[p + stride] + src[p + stride + 1]) -
                         (src[p - stride - 1] + 2 * src[p - stride] + src[p - stride + 1]);
                int i = baseOff + r * sbW + c;
                absSum[i] = (ushort)(AbsI(dx) + AbsI(dy));
                bin[i] = (sbyte)(dx != 0 ? GetHistBinIdx(dx, dy) : -1);
            }
        }
    }

    /// <summary>prune_intra_mode_with_hog through generate_hog_using_gradient_cache (the superblock cache at off, row
    /// stride sbW): the same histogram, in the same float order, as the direct path.</summary>
    internal static void PruneIntraModeWithHogCached(ushort[] absSum, sbyte[] bin, int off, int sbW, int rows, int cols, int ssX, int ssY,
        float th, Span<byte> directionalModeSkipMask)
    {
        Span<float> hist = stackalloc float[HogBins];
        hist.Clear();
        float total = 0.1f;
        for (int r = 1; r < rows - 1; ++r)
        {
            int o = off + r * sbW;
            for (int c = 1; c < cols - 1; ++c)
            {
                int s = absSum[o + c];
                if (s == 0) continue;
                total += s;
                int idx = bin[o + c];
                if (idx < 0)
                {
                    hist[0] += s >> 1;
                    hist[HogBins - 1] += s >> 1;
                }
                else hist[idx] += s;
            }
        }
        for (int i = 0; i < HogBins; ++i) hist[i] /= total;
        for (int b = 0; b < HogBins; ++b) hist[b] *= (1 + ssX) * (1 + ssY);
        PruneIntraModeFromHog(hist, th, directionalModeSkipMask);
    }

    // the scoring half of prune_intra_mode_with_hog
    private static void PruneIntraModeFromHog(ReadOnlySpan<float> hist, float th, Span<byte> directionalModeSkipMask)
    {
        Span<float> scores = stackalloc float[8];
        scores.Clear();
        NnPredict(hist, AomMlModels.IntraHogModelNnconfig, true, scores);
        for (int uvMode = UV_V_PRED; uvMode <= UV_D67_PRED; uvMode++)
            if (scores[uvMode - UV_V_PRED] <= th) directionalModeSkipMask[uvMode] = 1;
    }

    /// <summary>prune_intra_mode_with_hog (lowbd source): sets directionalModeSkipMask[uv_mode] = 1 for the directional
    /// modes UV_V_PRED..UV_D67_PRED (1..8) whose HOG-model score is at most th (never clears).</summary>
    internal static void PruneIntraModeWithHog(ReadOnlySpan<byte> src, int srcStride, int rows, int cols, int ssX, int ssY,
        float th, Span<byte> directionalModeSkipMask)
    {
        Span<float> hist = stackalloc float[HogBins];
        hist.Clear();
        CollectHogData(src, srcStride, rows, cols, ssX, ssY, hist);
        PruneIntraModeFromHog(hist, th, directionalModeSkipMask);
    }

    /// <summary>prune_intra_mode_with_hog (high-bitdepth source).</summary>
    internal static void PruneIntraModeWithHog(ReadOnlySpan<ushort> src, int srcStride, int rows, int cols, int ssX, int ssY,
        float th, Span<byte> directionalModeSkipMask)
    {
        Span<float> hist = stackalloc float[HogBins];
        hist.Clear();
        CollectHogData(src, srcStride, rows, cols, ssX, ssY, hist);
        PruneIntraModeFromHog(hist, th, directionalModeSkipMask);
    }
}
