using System;

namespace SharpImage.Formats.Av1;

// libaom's histogram-of-gradients intra mode pruning (intra_mode_search_utils.h prune_intra_mode_with_hog): a Sobel
// gradient-direction histogram of the source block (32 bins) feeds a linear model scoring the 8 directional modes;
// modes scoring at or below the speed's threshold are not searched.
internal static partial class Av1StillImageEncoder
{
// Generated from libaom av1/encoder/intra_mode_search_utils.h (av1_intra_hog_model_*).
    private static readonly float[] HogBias = { 0.450578f, 0.695518f, -0.717944f, -0.639894f, -0.602019f, -0.453454f, 0.055857f, -0.465480f };
    private static readonly float[] HogWeights =
    {
        -3.076402f, -3.757063f, -3.275266f, -3.180665f, -3.452105f, -3.216593f, -2.871212f, -3.134296f,
        -1.822324f, -2.401411f, -1.541016f, -1.195322f, -0.434156f, 0.322868f, 2.260546f, 3.368715f,
        3.989290f, 3.308487f, 2.277893f, 0.923793f, 0.026412f, -0.385174f, -0.718622f, -1.408867f,
        -1.050558f, -2.323941f, -2.225827f, -2.585453f, -3.054283f, -2.875087f, -2.985709f, -3.447155f,
        3.758139f, 3.204353f, 2.170998f, 0.826587f, -0.269665f, -0.702068f, -1.085776f, -2.175249f,
        -1.623180f, -2.975142f, -2.779629f, -3.190799f, -3.521900f, -3.375480f, -3.319355f, -3.897389f,
        -3.172334f, -3.594528f, -2.879132f, -2.547777f, -2.921023f, -2.281844f, -1.818988f, -2.041771f,
        -0.618268f, -1.396458f, -0.567153f, -0.285868f, -0.088058f, 0.753494f, 2.092413f, 3.215266f,
        -3.300277f, -2.748658f, -2.315784f, -2.423671f, -2.257283f, -2.269583f, -2.196660f, -2.301076f,
        -2.646516f, -2.271319f, -2.254366f, -2.300102f, -2.217960f, -2.473300f, -2.116866f, -2.528246f,
        -3.314712f, -1.701010f, -0.589040f, -0.088077f, 0.813112f, 1.702213f, 2.653045f, 3.351749f,
        3.243554f, 3.199409f, 2.437856f, 1.468854f, 0.533039f, -0.099065f, -0.622643f, -2.200732f,
        -4.228861f, -2.875263f, -1.273956f, -0.433280f, 0.803771f, 1.975043f, 3.179528f, 3.939064f,
        3.454379f, 3.689386f, 3.116411f, 1.970991f, 0.798406f, -0.628514f, -1.252546f, -2.825176f,
        -4.090178f, -3.777448f, -3.227314f, -3.479403f, -3.320569f, -3.159372f, -2.729202f, -2.722341f,
        -3.054913f, -2.742923f, -2.612703f, -2.662632f, -2.907314f, -3.117794f, -3.102660f, -3.970972f,
        -4.891357f, -3.935582f, -3.347758f, -2.721924f, -2.219011f, -1.702391f, -0.866529f, -0.153743f,
        0.107733f, 1.416882f, 2.572884f, 3.607755f, 3.974820f, 3.997783f, 2.970459f, 0.791687f,
        -1.478921f, -1.228154f, -1.216955f, -1.765932f, -1.951003f, -1.985301f, -1.975881f, -1.985593f,
        -2.422371f, -2.419978f, -2.531288f, -2.951853f, -3.071380f, -3.277027f, -3.373539f, -4.462010f,
        -0.967888f, 0.805524f, 2.794130f, 3.685984f, 3.745195f, 3.252444f, 2.316108f, 1.399146f,
        -0.136519f, -0.162811f, -1.004357f, -1.667911f, -1.964662f, -2.937579f, -3.019533f, -3.942766f,
        -5.102767f, -3.882073f, -3.532027f, -3.451956f, -2.944015f, -2.643064f, -2.529872f, -2.077290f,
        -2.809965f, -1.803734f, -1.783593f, -1.662585f, -1.415484f, -1.392673f, -0.788794f, -1.204819f,
        -1.998864f, -1.182102f, -0.892110f, -1.317415f, -1.359112f, -1.522867f, -1.468552f, -1.779072f,
        -2.332959f, -2.160346f, -2.329387f, -2.631259f, -2.744936f, -3.052494f, -2.787363f, -3.442548f,
        -4.245075f, -3.032172f, -2.061609f, -1.768116f, -1.286072f, -0.706587f, -0.192413f, 0.386938f,
        0.716997f, 1.481393f, 2.216702f, 2.737986f, 3.109809f, 3.226084f, 2.490098f, -0.095827f,
        -3.864816f, -3.507248f, -3.128925f, -2.908251f, -2.883836f, -2.881411f, -2.524377f, -2.624478f,
        -2.399573f, -2.367718f, -1.918255f, -1.926277f, -1.694584f, -1.723790f, -0.966491f, -1.183115f,
        -1.430687f, 0.872896f, 2.766550f, 3.610080f, 3.578041f, 3.334928f, 2.586680f, 1.895721f,
        1.122195f, 0.488519f, -0.140689f, -0.799076f, -1.222860f, -1.502437f, -1.900969f, -3.206816f,
    };

    // av1_intra_hog thresholds per pruning level (luma and chroma of intra frames).
    private static readonly float[] HogThresh = { -1.2f, -1.2f, -0.6f, 0.4f };

    private static readonly int[] HogBinThresholds =
    {
        -1334015, -441798, -261605, -183158, -138560, -109331, -88359, -72303,
        -59392, -48579, -39272, -30982, -23445, -16400, -9715, -3194,
        3227, 9748, 16433, 23478, 31015, 39305, 48611, 59425,
        72336, 88392, 109364, 138593, 183191, 261638, 441831, int.MaxValue,
    };

    private static int HogBin(int dx, int dy)
    {
        int ratio = dy * (1 << 16) / dx;
        int lo = ratio <= HogBinThresholds[7] ? 0 : ratio <= HogBinThresholds[15] ? 8 : ratio <= HogBinThresholds[23] ? 16 : 24;
        for (int i = lo; i < lo + 8; i++) if (ratio <= HogBinThresholds[i]) return i;
        return 31;
    }

    /// <summary>Bit mask (by mode value, Vertical..VerticalLeft) of the directional modes libaom's HOG model prunes for
    /// a w x h source block (visible part), at pruning level 1..4 (0 = none). scale = (1+ssX)(1+ssY) for chroma.</summary>
    internal static uint HogSkipMask(ReadOnlySpan<ushort> plane, int stride, int x0, int y0, int w, int h, int visW, int visH,
        int level, int scale = 1)
    {
        if (level <= 0) return 0;
        int rows = Math.Min(h, visH - y0), cols = Math.Min(w, visW - x0);
        Span<float> hist = stackalloc float[32];
        float total = 0.1f;
        for (int r = 1; r < rows - 1; r++)
        {
            int o = (y0 + r) * stride + x0;
            for (int c = 1; c < cols - 1; c++)
            {
                int p = o + c;
                int dx = (plane[p - stride + 1] + 2 * plane[p + 1] + plane[p + stride + 1])
                       - (plane[p - stride - 1] + 2 * plane[p - 1] + plane[p + stride - 1]);
                int dy = (plane[p + stride - 1] + 2 * plane[p + stride] + plane[p + stride + 1])
                       - (plane[p - stride - 1] + 2 * plane[p - stride] + plane[p - stride + 1]);
                if (dx == 0 && dy == 0) continue;
                int temp = Math.Abs(dx) + Math.Abs(dy);
                total += temp;
                if (dx == 0) { hist[0] += temp / 2; hist[31] += temp / 2; }
                else hist[HogBin(dx, dy)] += temp;
            }
        }
        for (int i = 0; i < 32; i++) hist[i] = hist[i] / total * scale;
        float th = HogThresh[Math.Min(level, 4) - 1];
        uint mask = 0;
        for (int m = 0; m < 8; m++)
        {
            float v = HogBias[m];
            for (int i = 0; i < 32; i++) v += HogWeights[m * 32 + i] * hist[i];
            v = (int)(v * 512 + 0.5f) * (float)(1.0 / 512);   // av1_nn_output_prec_reduce
            if (v <= th) mask |= 1u << (m + 1);
        }
        return mask;
    }

    /// <summary>libaom av1_calc_normalized_variance on a 4x4 luma block: the variance at 8-bit scale (high bit depth
    /// sums rounded down like aom_highbd_10/12_variance4x4).</summary>
    private static int Var4x4Norm(ushort[] luma, int stride, int x0, int y0)
    {
        long sum = 0, sse = 0;
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) { int v = luma[(y0 + y) * stride + x0 + x]; sum += v; sse += v * v; }
        int sh = Bd - 8;
        if (sh > 0) { sse = (sse + (1L << (2 * sh - 1))) >> (2 * sh); sum = (sum + (1L << (sh - 1))) >> sh; }
        long var = sse - sum * sum / 16;
        return (int)Math.Max(0, var);
    }
}
