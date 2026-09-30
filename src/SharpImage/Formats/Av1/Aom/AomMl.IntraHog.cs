using System;
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
        int temp = Math.Abs(dx) + Math.Abs(dy);
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
