// Perceptual heuristics for the VarDCT encoder, ported from libjxl (lib/jxl/enc_adaptive_quantization.cc
// and lib/jxl/enc_ac_strategy.cc). These replace the ad-hoc nonzero-count block selection with libjxl's
// actual model: a masking-based adaptive quant field and an EstimateEntropy cost (rate + masking-weighted
// L8 spatial distortion) that drives a hierarchical block-size search. All magic numbers are libjxl's.
//
// Scalar port (no SIMD): correctness over speed. The XYB opsin space, forward DCT (JxlDct.Dct2D) and
// dequant matrices are the same as the decoder's, so libjxl's constants apply directly.

using System;

namespace SharpImage.Formats.Jxl;

internal static class JxlEncoderPerceptual
{
    private const float kInvLog2e = 0.6931471805599453f; // ln(2); libjxl's kInvLog2e

    // --- gamma / masking primitives (enc_adaptive_quantization.cc) ---

    private const float kSGmul = 226.77216153508914f;
    private const float kSGmul2 = 1.0f / 73.377132366608819f;
    private static readonly float kSGRetMul = kSGmul2 * 18.6580932135f * kInvLog2e;
    private const float kSGVOffset = 7.7825991679894591f;

    // Ratio of derivatives of cubic-root to SimpleGamma: moves quantization from opsin space to
    // butteraugli's log-gamma space.
    private static float RatioOfDerivatives(float v, bool invert)
    {
        const float kEpsilon = 1e-2f;
        if (v < 0)
        {
            v = 0;
        }

        float kNumMul = kSGRetMul * 3 * kSGmul;
        float kVOffset = (kSGVOffset * kInvLog2e) + kEpsilon;
        float kDenMul = kInvLog2e * kSGmul;
        float v2 = v * v;
        float num = (kNumMul * v2) + kEpsilon;
        float den = (kDenMul * v * v2) + kVOffset;
        return invert ? num / den : den / num;
    }

    private static float MaskingSqrt(float v)
    {
        const float kLogOffset = 27.505837037000106f;
        const float kMul = 211.66567973503678f;
        return 0.25f * MathF.Sqrt((v * MathF.Sqrt(kMul * 1e8f)) + kLogOffset);
    }

    // Rational masking function of the (aggregated) local activity.
    private static float ComputeMask(float outVal)
    {
        const float kBase = -0.7647f;
        const float kMul4 = 9.4708735624378946f;
        const float kMul2 = 17.35036561631863f;
        const float kOffset2 = 302.59587815579727f;
        const float kMul3 = 6.7943250517376494f;
        const float kOffset3 = 3.7179635626140772f;
        const float kOffset4 = 0.25f * kOffset3;
        const float kMul0 = 0.80061762862741759f;
        float v1 = MathF.Max(outVal * kMul0, 1e-3f);
        float v2 = 1.0f / (v1 + kOffset2);
        float v3 = 1.0f / ((v1 * v1) + kOffset3);
        float v4 = 1.0f / ((v1 * v1) + kOffset4);
        return kBase + (kMul4 * v4) + (kMul2 * v2) + (kMul3 * v3);
    }

    private static float ComputeMaskForAcStrategyUse(float outVal) => 1.0f / (outVal + 0.001f);

    // --- the per-8x8 quant field + per-pixel 1x1 masking image ---

    // Returns (quantField[bw*bh], mask1x1[w*h]) in libjxl's normalised quant units. The caller calibrates
    // quantField to per-block hf_mul. mask1x1 is the blurred |Laplacian| used to weight EstimateEntropy's
    // information-loss term. `scale` = kAcQuant / distance.
    internal static (float[] QuantField, float[] Mask1x1) ComputeAdaptiveQuantField(
        float[][] xyb, int stride, int w, int h, int bw, int bh, float butteraugliTarget)
    {
        const float kAcQuant = 0.765f;
        float scale = kAcQuant / butteraugliTarget;
        float[] Y = xyb[1], X = xyb[0], B = xyb[2];

        // 1x1 masking image: 1 / (log1p(|gammac * (Y - localMean)|) + 0.01), then blurred.
        const float matchGammaOffset = 0.019f;
        var mask1x1 = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int y1 = y > 0 ? y - 1 : y;
            int y2 = y + 1 < h ? y + 1 : y;
            for (int x = 0; x < w; x++)
            {
                int x1 = x > 0 ? x - 1 : x;
                int x2 = x + 1 < w ? x + 1 : x;
                float c = Y[(y * stride) + x];
                float bse = 0.25f * (Y[(y2 * stride) + x] + Y[(y1 * stride) + x] + Y[(y * stride) + x1] + Y[(y * stride) + x2]);
                float gammac = RatioOfDerivatives(c + matchGammaOffset, false);
                float diff = MathF.Abs(gammac * (c - bse));
                diff = MathF.Log(1.0f + diff);
                mask1x1[(y * w) + x] = 1.0f / (diff + 0.01f);
            }
        }

        // Quarter-resolution activity: diff = MaskingSqrt(min(0.2, (gammac*(Y-mean))^2)), accumulated over
        // 4 rows and averaged over 4 columns.
        int qw = 2 * bw, qh = 2 * bh; // quarter-res of the 8*bw x 8*bh padded block region
        var pre = new float[qw * qh];
        const float limit = 0.2f;
        var rowAcc = new float[8 * bw];
        for (int y = 0; y < 8 * bh; y++)
        {
            int yc = Math.Min(y, h - 1);
            int y1 = yc > 0 ? yc - 1 : yc;
            int y2 = yc + 1 < h ? yc + 1 : yc;
            for (int x = 0; x < 8 * bw; x++)
            {
                int xc = Math.Min(x, w - 1);
                int x1 = xc > 0 ? xc - 1 : xc;
                int x2 = xc + 1 < w ? xc + 1 : xc;
                float c = Y[(yc * stride) + xc];
                float bse = 0.25f * (Y[(y2 * stride) + xc] + Y[(y1 * stride) + xc] + Y[(yc * stride) + x1] + Y[(yc * stride) + x2]);
                float gammac = RatioOfDerivatives(c + matchGammaOffset, false);
                float diff = gammac * (c - bse);
                diff *= diff;
                if (diff >= limit)
                {
                    diff = limit;
                }

                diff = MaskingSqrt(diff);
                if ((y & 3) != 0)
                {
                    rowAcc[x] += diff;
                }
                else
                {
                    rowAcc[x] = diff;
                }
            }

            if ((y & 3) == 3)
            {
                int qy = y / 4;
                for (int qx = 0; qx < qw; qx++)
                {
                    pre[(qy * qw) + qx] = (rowAcc[qx * 4] + rowAcc[(qx * 4) + 1] + rowAcc[(qx * 4) + 2] + rowAcc[(qx * 4) + 3]) * 0.25f;
                }
            }
        }

        // FuzzyErosion: weighted mean of the four smallest of a 3x3 neighbourhood, subsampled 2:1 -> per block.
        var aq = FuzzyErosion(pre, qw, qh, bw, bh, butteraugliTarget);

        // PerBlockModulations: ComputeMask + gamma + hf + blue -> multiplicative quant field.
        float baseLevel = 0.48f * scale;
        const float kDampenRampStart = 2.0f, kDampenRampEnd = 14.0f;
        float dampen = 1.0f;
        if (butteraugliTarget >= kDampenRampStart)
        {
            dampen = 1.0f - ((butteraugliTarget - kDampenRampStart) / (kDampenRampEnd - kDampenRampStart));
            if (dampen < 0)
            {
                dampen = 0;
            }
        }

        float mul = scale * dampen;
        float add = (1.0f - dampen) * baseLevel;
        var quantField = new float[bw * bh];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                float maskVal = ComputeMask(aq[(by * bw) + bx]);
                float outVal = GammaModulation(bx * 8, by * 8, X, Y, stride, w, h, maskVal);
                outVal = HfModulation(bx * 8, by * 8, Y, stride, w, h, outVal);
                outVal = MathF.Min(outVal, BlueModulation(bx * 8, by * 8, X, Y, B, stride, w, h, maskVal));
                quantField[(by * bw) + bx] = (FastPow2(outVal * 1.442695041f) * mul) + add;
            }
        }

        Blur1x1(mask1x1, w, h);
        return (quantField, mask1x1);
    }

    private static float FastPow2(float x) => MathF.Pow(2.0f, x);

    private static float[] FuzzyErosion(float[] from, int fw, int fh, int bw, int bh, float butteraugliTarget)
    {
        // Weighted mean of the four smallest values in a 3x3 neighbourhood, then 2:1 subsample (2x2 -> 1).
        float[] kMulBase = { 0.125f, 0.1f, 0.09f, 0.06f };
        float[] kMulAdd = { 0.0f, -0.1f, -0.09f, -0.06f };
        float m = 0.0f;
        if (butteraugliTarget < 2.0f)
        {
            m = (2.0f - butteraugliTarget) * 0.5f;
        }

        var kMul = new float[4];
        float normSum = 0;
        for (int i = 0; i < 4; i++)
        {
            kMul[i] = kMulBase[i] + (m * kMulAdd[i]);
            normSum += kMul[i];
        }

        const float kTotal = 0.29959705784054957f;
        for (int i = 0; i < 4; i++)
        {
            kMul[i] *= kTotal / normSum;
        }

        var to = new float[bw * bh];
        for (int fy = 0; fy < fh; fy++)
        {
            int ym1 = fy >= 1 ? fy - 1 : fy;
            int yp1 = fy + 1 < fh ? fy + 1 : fy;
            for (int fx = 0; fx < fw; fx++)
            {
                int xm1 = fx >= 1 ? fx - 1 : fx;
                int xp1 = fx + 1 < fw ? fx + 1 : fx;
                Span<float> min = stackalloc float[4];
                min[0] = from[(fy * fw) + fx];
                min[1] = from[(fy * fw) + xm1];
                min[2] = from[(fy * fw) + xp1];
                min[3] = from[(ym1 * fw) + xm1];
                Sort4(min);
                StoreMin4(from[(ym1 * fw) + fx], min);
                StoreMin4(from[(ym1 * fw) + xp1], min);
                StoreMin4(from[(yp1 * fw) + xm1], min);
                StoreMin4(from[(yp1 * fw) + fx], min);
                StoreMin4(from[(yp1 * fw) + xp1], min);
                float v = (kMul[0] * min[0]) + (kMul[1] * min[1]) + (kMul[2] * min[2]) + (kMul[3] * min[3]);
                int tx = fx / 2, ty = fy / 2;
                if (fx % 2 == 0 && fy % 2 == 0)
                {
                    to[(ty * bw) + tx] = v;
                }
                else
                {
                    to[(ty * bw) + tx] += v;
                }
            }
        }

        return to;
    }

    private static void Sort4(Span<float> a)
    {
        if (a[0] > a[1])
        {
            (a[0], a[1]) = (a[1], a[0]);
        }

        if (a[0] > a[2])
        {
            (a[0], a[2]) = (a[2], a[0]);
        }

        if (a[0] > a[3])
        {
            (a[0], a[3]) = (a[3], a[0]);
        }

        if (a[1] > a[2])
        {
            (a[1], a[2]) = (a[2], a[1]);
        }

        if (a[1] > a[3])
        {
            (a[1], a[3]) = (a[3], a[1]);
        }

        if (a[2] > a[3])
        {
            (a[2], a[3]) = (a[3], a[2]);
        }
    }

    // Insert v into the sorted-ascending list of the four running minima (libjxl's StoreMin4).
    private static void StoreMin4(float v, Span<float> min)
    {
        if (v < min[3])
        {
            if (v < min[2])
            {
                if (v < min[1])
                {
                    if (v < min[0])
                    {
                        min[3] = min[2];
                        min[2] = min[1];
                        min[1] = min[0];
                        min[0] = v;
                    }
                    else
                    {
                        min[3] = min[2];
                        min[2] = min[1];
                        min[1] = v;
                    }
                }
                else
                {
                    min[3] = min[2];
                    min[2] = v;
                }
            }
            else
            {
                min[3] = v;
            }
        }
    }

    private static float GammaModulation(int x, int y, float[] X, float[] Y, int stride, int w, int h, float outVal)
    {
        const float kBias = 0.16f;
        float overallRatio = 0;
        for (int dy = 0; dy < 8; dy++)
        {
            int yy = Math.Min(y + dy, h - 1);
            for (int dx = 0; dx < 8; dx++)
            {
                int xx = Math.Min(x + dx, w - 1);
                float iny = Y[(yy * stride) + xx] + kBias;
                float inx = X[(yy * stride) + xx];
                overallRatio += RatioOfDerivatives(iny - inx, true);
                overallRatio += RatioOfDerivatives(iny + inx, true);
            }
        }

        overallRatio *= 0.5f / 64.0f;
        const float kGamma = 0.1005613337192697f;
        return (kGamma * Log2(overallRatio)) + outVal;
    }

    private static float HfModulation(int x, int y, float[] Y, int stride, int w, int h, float outVal)
    {
        const float valmin = 0.0206f;
        float sum = 0;
        for (int dy = 0; dy < 8; dy++)
        {
            int yy = Math.Min(y + dy, h - 1);
            int yn = Math.Min(y + dy + 1, h - 1);
            for (int dx = 0; dx < 8; dx++)
            {
                int xx = Math.Min(x + dx, w - 1);
                float py = Y[(yy * stride) + xx];
                if (dx < 7)
                {
                    int xr = Math.Min(x + dx + 1, w - 1);
                    sum += MathF.Min(valmin, MathF.Abs(py - Y[(yy * stride) + xr]));
                }

                sum += MathF.Min(valmin, MathF.Abs(py - Y[(yn * stride) + xx]));
            }
        }

        const float kMul = -0.38f;
        const float kOffset = 0.42f;
        return ((sum * kMul) + kOffset) + outVal;
    }

    private static float BlueModulation(int x, int y, float[] X, float[] Y, float[] B, int stride, int w, int h, float outVal)
    {
        const float kLimit = 0.010474084867598155f;
        const float kOffset = 0.0031994768654636393f;
        float sum = 0;
        for (int dy = 0; dy < 8; dy++)
        {
            int yy = Math.Min(y + dy, h - 1);
            for (int dx = 0; dx < 8; dx++)
            {
                int xx = Math.Min(x + dx, w - 1);
                float px = X[(yy * stride) + xx];
                float pb = B[(yy * stride) + xx];
                float pyEff = Y[(yy * stride) + xx] + kOffset + MathF.Abs(px);
                if (pb > pyEff)
                {
                    sum += MathF.Min(pb - pyEff, kLimit);
                }
            }
        }

        if (sum >= 32 * kLimit)
        {
            sum = (64 * kLimit) - sum;
        }

        const float kMaxLimit = 15.463398341612438f;
        if (sum >= kMaxLimit * kLimit)
        {
            sum = kMaxLimit * kLimit;
        }

        const float kMul = 0.90590804735610064f;
        return (sum * kMul) + outVal;
    }

    private static float Log2(float v) => v <= 0 ? -128f : MathF.Log2(v);

    // Symmetric 5x5 convolution (libjxl's Symmetric5). Weight layout: c=centre, r=orthogonal dist 1,
    // R=orthogonal dist 2, d=diagonal (1,1), D=diagonal (2,2), L=(2,1)/(1,2). Edge pixels clamped.
    private static float[] Symmetric5(float[] img, int w, int h, float c, float r, float R, float d, float D, float L)
    {
        var outp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = c * At(img, w, h, x, y);
                acc += r * (At(img, w, h, x - 1, y) + At(img, w, h, x + 1, y) + At(img, w, h, x, y - 1) + At(img, w, h, x, y + 1));
                acc += R * (At(img, w, h, x - 2, y) + At(img, w, h, x + 2, y) + At(img, w, h, x, y - 2) + At(img, w, h, x, y + 2));
                acc += d * (At(img, w, h, x - 1, y - 1) + At(img, w, h, x + 1, y - 1) + At(img, w, h, x - 1, y + 1) + At(img, w, h, x + 1, y + 1));
                acc += D * (At(img, w, h, x - 2, y - 2) + At(img, w, h, x + 2, y - 2) + At(img, w, h, x - 2, y + 2) + At(img, w, h, x + 2, y + 2));
                acc += L * (At(img, w, h, x - 2, y - 1) + At(img, w, h, x + 2, y - 1) + At(img, w, h, x - 2, y + 1) + At(img, w, h, x + 2, y + 1)
                    + At(img, w, h, x - 1, y - 2) + At(img, w, h, x + 1, y - 2) + At(img, w, h, x - 1, y + 2) + At(img, w, h, x + 1, y + 2));
                outp[(y * w) + x] = acc;
            }
        }

        return outp;
    }

    // 1x1 masking image blur (Blur1x1Masking).
    private static void Blur1x1(float[] mask, int w, int h)
    {
        float[] k = { 0.364911248f, 0.05f, 0.1688888021f, 0.221069183f, 0.306563504f };
        double sum = 1.0 + (4 * (k[0] + k[1] + k[2] + k[4] + (2 * k[3])));
        if (sum < 1e-5)
        {
            sum = 1e-5;
        }

        float n = (float)(1.0 / sum);
        // WeightsSymmetric5{c, r, R, d, D, L} = {n, n*k[0], n*k[2], n*k[1], n*k[4], n*k[3]}.
        float[] outp = Symmetric5(mask, w, h, n, n * k[0], n * k[2], n * k[1], n * k[4], n * k[3]);
        Array.Copy(outp, mask, w * h);
    }

    // GaborishInverse (enc_gaborish.cc): the encoder pre-sharpens the opsin image so the decoder's Gaborish
    // 3x3 blur reconstructs it. Applied per channel in place. mul = 0.9908511 (libjxl's constant).
    internal static void GaborishInverse(float[][] xyb, int stride, int strideH)
    {
        const float mul = 0.9908511000000001f;
        float[] kg = { -0.09495815671340026f, -0.041031725066768575f, 0.013710004822696948f, 0.006510206083837737f, -0.0014789063378272242f };
        double sum = 1.0 + (mul * 4 * (kg[0] + kg[1] + kg[2] + kg[4] + (2 * kg[3])));
        if (sum < 1e-5)
        {
            sum = 1e-5;
        }

        float normalize = (float)(1.0 / sum);
        float nm = mul * normalize;
        // WeightsSymmetric5{c, r, R, d, D, L} = {normalize, nm*kg[0], nm*kg[2], nm*kg[1], nm*kg[4], nm*kg[3]}.
        for (int ch = 0; ch < 3; ch++)
        {
            xyb[ch] = Symmetric5(xyb[ch], stride, strideH, normalize, nm * kg[0], nm * kg[2], nm * kg[1], nm * kg[4], nm * kg[3]);
        }
    }

    private static float At(float[] img, int w, int h, int x, int y)
    {
        if (x < 0)
        {
            x = 0;
        }

        if (y < 0)
        {
            y = 0;
        }

        if (x >= w)
        {
            x = w - 1;
        }

        if (y >= h)
        {
            y = h - 1;
        }

        return img[(y * w) + x];
    }

    // --- EstimateEntropy + hierarchical block-size search (enc_ac_strategy.cc) ---

    // Shared state for the AC-strategy cost estimate.
    internal sealed class AcsConfig
    {
        public float[][] Xyb = null!;      // opsin planes (padded, stride)
        public int Stride;
        public int W;
        public int H;                       // actual image size (for mask1x1 bounds)
        public float[] QuantField = null!;  // per-8x8 quant (libjxl units), size bw*bh
        public int Bw;
        public float[] Mask1x1 = null!;     // per-pixel blurred |Laplacian|, size W*H
        public float[] Cmap = { 0f, 0f, 1f }; // YtoX, (unused), YtoB CfL factors
        public DequantMatrixSet Dm = null!;
        public float CostDelta;
        public float ZerosMul;
        public float InfoLossMul;

        public float Quant(int bx, int by) => QuantField[(by * Bw) + bx];
    }

    // Config constants (ACSConfig::Init), scaled by the distance ratio.
    internal static AcsConfig MakeConfig(float[][] xyb, int stride, int w, int h, int bw,
        float[] quantField, float[] mask1x1, float[] cmap, DequantMatrixSet dm, float distance)
    {
        const float kBias = 0.13731742964354549f;
        float ratio = (distance + kBias) / (1.0f + kBias);
        return new AcsConfig
        {
            Xyb = xyb, Stride = stride, W = w, H = h, QuantField = quantField, Bw = bw,
            Mask1x1 = mask1x1, Cmap = cmap, Dm = dm,
            InfoLossMul = 1.2f * MathF.Pow(ratio, 0.33677806662454718f),
            ZerosMul = 9.3089059022677905f * MathF.Pow(ratio, 0.50990926717963703f),
            CostDelta = 10.833273317067883f * MathF.Pow(ratio, 0.36702940662370243f),
        };
    }

    private static readonly double[] KChannelMul = { Math.Pow(8.2, 8.0), Math.Pow(1.0, 8.0), Math.Pow(1.03, 8.0) };
    private static readonly float[] MaskuLut = { 12.0f, 0.0f, 4.0f };

    // EstimateEntropy: rate (cost_delta*Σsqrt|q| + zeros_mul*nonzero-bits) + info-loss (masking-weighted L8
    // spatial distortion). Lower is better. px,py in pixels; t the candidate transform.
    internal static float EstimateEntropy(AcsConfig c, TransformType t, float entropyMul, int px, int py)
    {
        var (dw, dh) = JxlDct.DctSelectSize(t);
        int pw = dw * 8, ph = dh * 8;
        int numBlocks = dw * dh;
        int size = numBlocks * 64;

        // Forward transform each channel (natural raster).
        var block = new float[3][];
        for (int ch = 0; ch < 3; ch++)
        {
            block[ch] = new float[pw * ph];
            var g = new JxlDct.Grid(block[ch], 0, pw, pw, ph);
            for (int yy = 0; yy < ph; yy++)
            {
                for (int xx = 0; xx < pw; xx++)
                {
                    g.Set(xx, yy, c.Xyb[ch][((py + yy) * c.Stride) + px + xx]);
                }
            }

            JxlDct.Dct2D(g, false);
        }

        // Aggregate the quant field over the covered blocks (quant_norm16).
        int bx0 = px / 8, by0 = py / 8;
        float quantNorm;
        if (numBlocks == 1)
        {
            quantNorm = c.Quant(bx0, by0);
        }
        else if (numBlocks == 2)
        {
            quantNorm = dh == 2
                ? MathF.Max(c.Quant(bx0, by0), c.Quant(bx0, by0 + 1))
                : MathF.Max(c.Quant(bx0, by0), c.Quant(bx0 + 1, by0));
        }
        else
        {
            float acc = 0;
            for (int iy = 0; iy < dh; iy++)
            {
                for (int ix = 0; ix < dw; ix++)
                {
                    float q = c.Quant(bx0 + ix, by0 + iy);
                    q *= q; q *= q; q *= q; // q^8
                    acc += q * q;           // q^16
                }
            }

            acc /= numBlocks;
            quantNorm = MathF.Pow(acc, 1.0f / 16.0f);
        }

        double loss = 0;
        float entropy = 0;
        var mem = new float[size];
        for (int ch = 0; ch < 3; ch++)
        {
            float[] mat = c.Dm.GetTransposed(ch, t); // raster orientation matching the actual encode's quantiser
            float cmapFactor = c.Cmap[ch];
            float entropyV = 0;
            int nzeros = 0;
            for (int i = 0; i < size; i++)
            {
                float inY = block[1][i] * cmapFactor;
                float im = 1.0f / mat[i];
                float val = (block[ch][i] - inY) * im * quantNorm;
                float rval = MathF.Round(val);
                mem[i] = mat[i] * (val - rval);
                float q = MathF.Abs(rval);
                entropyV += MathF.Sqrt(q);
                if (q != 0)
                {
                    nzeros++;
                }
            }

            // Inverse transform the (matrix-scaled) quant error back to pixels, weight by masking, L8.
            var err = new float[size];
            var g = new JxlDct.Grid(err, 0, pw, pw, ph);
            Array.Copy(mem, err, size);
            JxlDct.Dct2D(g, true);
            float masku_off = MaskuLut[ch];
            double lossc = 0;
            for (int iy = 0; iy < dh; iy++)
            {
                for (int ix = 0; ix < dw; ix++)
                {
                    for (int dy = 0; dy < 8; dy++)
                    {
                        for (int dx = 0; dx < 8; dx++)
                        {
                            int gx = px + (ix * 8) + dx, gy = py + (iy * 8) + dy;
                            if (gx >= c.W || gy >= c.H)
                            {
                                continue;
                            }

                            float e = err[((iy * 8 + dy) * pw) + (ix * 8) + dx];
                            float masku = c.Mask1x1[(gy * c.W) + gx] + masku_off;
                            float inv = masku * e;
                            inv *= inv; inv *= inv; inv *= inv; // ^8
                            lossc += inv;
                        }
                    }
                }
            }

            lossc *= KChannelMul[ch];
            loss += lossc;

            entropy += c.CostDelta * entropyV;
            int nbits = CeilLog2(nzeros + 1) + 1;
            entropy += c.ZerosMul * (CeilLog2(nbits + 17) + nbits);
            if (ch == 0 && numBlocks >= 2)
            {
                // X (red-green) ringing in large blocks: punish more.
                float wpun = 1.0f + MathF.Min(3.0f, numBlocks / 8.0f);
                entropy *= wpun;
                loss *= wpun;
            }
        }

        float lossScalar = (float)(Math.Pow(loss / size, 1.0 / 8.0) * size / quantNorm);
        entropy *= entropyMul;
        entropy += c.InfoLossMul * lossScalar;
        return entropy;
    }

    private static int CeilLog2(int v)
    {
        if (v <= 1)
        {
            return 0;
        }

        int r = 0, x = v - 1;
        while (x > 0)
        {
            r++;
            x >>= 1;
        }

        return r;
    }

    // Block-size layout via libjxl's hierarchical merge. Returns sizeAt[bw*bh] = (int)TransformType for a
    // data block's top-left, -1 for a covered position. Transforms never cross 64x64 (8-block) tiles.
    internal static int[] ProcessImage(AcsConfig c, int bw, int bh, float distance)
    {
        var sizeAt = new int[bw * bh];
        for (int i = 0; i < sizeAt.Length; i++)
        {
            sizeAt[i] = (int)TransformType.Dct8;
        }

        var est = new float[bw * bh]; // per-8x8 entropy of the currently-assigned transform
        float mul8x8 = 1.0f + (-0.4f / (distance + 1.4f));

        // Per-8x8 best transform (only DCT8 available at 8x8 in our transform set).
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                est[(by * bw) + bx] = EstimateEntropy(c, TransformType.Dct8, 1.0f, bx * 8, by * 8) * mul8x8;
            }
        }

        const float mul16X8 = 1.21f, mul16X16 = 1.34f, mul32X32 = 1.48f, mul64X64 = 2.25f;

        // Level 1: 16x16 squares + 16x8/8x16 rectangles (aligned to 2-block grid).
        for (int by = 0; by + 1 < bh; by += 2)
        {
            for (int bx = 0; bx + 1 < bw; bx += 2)
            {
                FirstLevelDivision(c, sizeAt, est, bw, 2, bx, by, mul16X8, mul16X16, true);
            }
        }

        // Level 2: 32x32 squares (no 32x16/16x32 rectangles in our set), aligned to 4-block grid.
        for (int by = 0; by + 3 < bh; by += 4)
        {
            for (int bx = 0; bx + 3 < bw; bx += 4)
            {
                FirstLevelDivision(c, sizeAt, est, bw, 4, bx, by, 0f, mul32X32, false);
            }
        }

        // Level 3: 64x64 squares, aligned to 8-block (tile) grid.
        for (int by = 0; by + 7 < bh; by += 8)
        {
            for (int bx = 0; bx + 7 < bw; bx += 8)
            {
                FirstLevelDivision(c, sizeAt, est, bw, 8, bx, by, 0f, mul64X64, false);
            }
        }

        return sizeAt;
    }

    private static AcStrategyTypes SquareType(int blocks) =>
        blocks == 2 ? AcStrategyTypes.Dct16 : blocks == 4 ? AcStrategyTypes.Dct32 : AcStrategyTypes.Dct64;

    // Ports FindBestFirstLevelDivisionForSquare: compares keeping the four sub-quadrants vs merging into a
    // JxJ square vs splitting into two JxK / KxJ rectangles (rectangles only when allowRects, i.e. blocks==2
    // where we have Dct16x8/Dct8x16). Updates sizeAt + est in place.
    private static void FirstLevelDivision(AcsConfig c, int[] sizeAt, float[] est, int bw, int blocks,
        int bx, int by, float mulJXK, float mulJXJ, bool allowRects)
    {
        int half = blocks / 2;

        // Current entropy aggregated to 2x2 quadrants.
        float[,] q = new float[2, 2];
        for (int dy = 0; dy < blocks; dy++)
        {
            for (int dx = 0; dx < blocks; dx++)
            {
                q[dy / half, dx / half] += est[((by + dy) * bw) + bx + dx];
            }
        }

        float inf = float.MaxValue;
        float eJXKleft = inf, eJXKright = inf, eKXJtop = inf, eKXJbottom = inf, eJXJ = inf;
        if (allowRects)
        {
            // Vertical split -> two Dct16x8 (1 col x 2 rows) at left/right columns.
            eJXKleft = EstimateEntropy(c, TransformType.Dct16x8, mulJXK, bx * 8, by * 8);
            eJXKright = EstimateEntropy(c, TransformType.Dct16x8, mulJXK, (bx + half) * 8, by * 8);
            // Horizontal split -> two Dct8x16 (2 cols x 1 row) at top/bottom rows.
            eKXJtop = EstimateEntropy(c, TransformType.Dct8x16, mulJXK, bx * 8, by * 8);
            eKXJbottom = EstimateEntropy(c, TransformType.Dct8x16, mulJXK, bx * 8, (by + half) * 8);
        }

        eJXJ = EstimateEntropy(c, SizeToTransform(SquareType(blocks)), mulJXJ, bx * 8, by * 8);

        float costJxN = MathF.Min(eJXKleft, q[0, 0] + q[1, 0]) + MathF.Min(eJXKright, q[0, 1] + q[1, 1]);
        float costNxJ = MathF.Min(eKXJtop, q[0, 0] + q[0, 1]) + MathF.Min(eKXJbottom, q[1, 0] + q[1, 1]);

        if (eJXJ < costJxN && eJXJ < costNxJ)
        {
            AssignBlock(sizeAt, est, bw, bx, by, blocks, blocks, SizeToTransform(SquareType(blocks)), eJXJ);
        }
        else if (costJxN < costNxJ)
        {
            if (eJXKleft < q[0, 0] + q[1, 0])
            {
                AssignBlock(sizeAt, est, bw, bx, by, 1, 2, TransformType.Dct16x8, eJXKleft);
            }

            if (eJXKright < q[0, 1] + q[1, 1])
            {
                AssignBlock(sizeAt, est, bw, bx + half, by, 1, 2, TransformType.Dct16x8, eJXKright);
            }
        }
        else
        {
            if (eKXJtop < q[0, 0] + q[0, 1])
            {
                AssignBlock(sizeAt, est, bw, bx, by, 2, 1, TransformType.Dct8x16, eKXJtop);
            }

            if (eKXJbottom < q[1, 0] + q[1, 1])
            {
                AssignBlock(sizeAt, est, bw, bx, by + half, 2, 1, TransformType.Dct8x16, eKXJbottom);
            }
        }
    }

    private enum AcStrategyTypes { Dct16, Dct32, Dct64 }

    private static TransformType SizeToTransform(AcStrategyTypes t) => t switch
    {
        AcStrategyTypes.Dct16 => TransformType.Dct16,
        AcStrategyTypes.Dct32 => TransformType.Dct32,
        _ => TransformType.Dct64,
    };

    // Mark a dwBlocks x dhBlocks block: top-left gets the transform + its entropy, the rest -1 / 0.
    private static void AssignBlock(int[] sizeAt, float[] est, int bw, int bx, int by, int dwBlocks, int dhBlocks, TransformType t, float entropy)
    {
        for (int dy = 0; dy < dhBlocks; dy++)
        {
            for (int dx = 0; dx < dwBlocks; dx++)
            {
                sizeAt[((by + dy) * bw) + bx + dx] = (dx == 0 && dy == 0) ? (int)t : -1;
                est[((by + dy) * bw) + bx + dx] = 0;
            }
        }

        est[(by * bw) + bx] = entropy;
    }
}
