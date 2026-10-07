// SSIMULACRA2 (cloudinary/ssimulacra2, src/ssimulacra2.cc) ported for the VarDCT encoder: a whole-image
// Score() (validated against ssimulacra2_rs) and a per-8x8-tile distortion map (for the iterative quant
// refinement). Faithful to the reference: input is LINEAR RGB, downsampled by 2x2 per octave in linear
// space and re-converted to opsin XYB each scale, MakePositiveXYB, Gaussian blur (sigma 1.5), the SSIM'
// error map (num_m = 1-(mu1-mu2)^2, no gamma denominator) and the ringing/blur edge-difference maps, with
// both the 1-norm and 4-norm and the tuned 108-weight combination + final polynomial. The per-tile map uses
// the 1-norm weighted maps distributed to the 8x8 blocks each scale-pixel covers.

using System;
using SharpImage.Core;

namespace SharpImage.Formats.Jxl;

internal static class JxlEncoderSsimulacra
{
    private const float kC2 = 0.0009f;

    private static readonly double[] W =
    {
        0.0, 0.0007376606707406586, 0.0, 0.0, 0.0007793481682867309, 0.0, 0.0, 0.0004371155730107379, 0.0,
        1.1041726426657346, 0.00066284834129271, 0.00015231632783718752, 0.0, 0.0016406437456599754, 0.0,
        1.8422455520539298, 11.441172603757666, 0.0, 0.0007989109436015163, 0.000176816438078653, 0.0,
        1.8787594979546387, 10.94906990605142, 0.0, 0.0007289346991508072, 0.9677937080626833, 0.0,
        0.00014003424285435884, 0.9981766977854967, 0.00031949755934435053, 0.0004550992113792063, 0.0, 0.0,
        0.0013648766163243398, 0.0, 0.0, 0.0, 0.0, 0.0, 7.466890328078848, 0.0, 17.445833984131262,
        0.0006235601634041466, 0.0, 0.0, 6.683678146179332, 0.00037724407979611296, 1.027889937768264,
        225.20515300849274, 0.0, 0.0, 19.213238186143016, 0.0011401524586618361, 0.001237755635509985,
        176.39317598450694, 0.0, 0.0, 24.43300999870476, 0.28520802612117757, 0.0004485436923833408, 0.0, 0.0,
        0.0, 34.77906344483772, 44.835625328877896, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
        0.0008680556573291698, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0005313191874358747, 0.0, 0.00016533814161379112,
        0.0, 0.0, 0.0, 0.0, 0.0, 0.0004179171803251336, 0.0017290828234722833, 0.0, 0.0020827005846636437, 0.0,
        0.0, 8.826982764996862, 23.19243343998926, 0.0, 95.1080498811086, 0.9863978034400682,
        0.9834382792465353, 0.0012286405048278493, 171.2667255897307, 0.9807858872435379, 0.0, 0.0, 0.0,
        0.0005130064588990679, 0.0, 0.00010854057858411537,
    };

    // Per-scale whole-image averages (avg_ssim[c*2+n], avg_edgediff[c*4+n]).
    private sealed class ScaleAvg
    {
        public double[] Ssim = new double[6];
        public double[] Edge = new double[12];
    }

    // Compute both the SSIMULACRA2 score and the per-8x8-block distortion for two LINEAR RGB images.
    internal static (double Score, double[] BlockDist) Compute(float[][] lin0, float[][] lin1, int w, int h, int bw, int bh, VarDctFrameParams fp)
    {
        var blockErr = new double[bw * bh];
        float[][] a = lin0, b = lin1;
        int cw = w, ch = h;
        var scales = new ScaleAvg[6];

        for (int scale = 0; scale < 6 && cw > 0 && ch > 0; scale++)
        {
            var sa = new ScaleAvg();
            scales[scale] = sa;
            int len = cw * ch;
            int step = 1 << scale;

            float[][] x1 = JxlVarDctEncoder.LinearToXyb(a, len, fp);
            float[][] x2 = JxlVarDctEncoder.LinearToXyb(b, len, fp);
            MakePositive(x1, len);
            MakePositive(x2, len);

            var mu1 = Blur3(x1, cw, ch);
            var mu2 = Blur3(x2, cw, ch);
            var s11 = Blur3(Mul(x1, x1, len), cw, ch);
            var s22 = Blur3(Mul(x2, x2, len), cw, ch);
            var s12 = Blur3(Mul(x1, x2, len), cw, ch);

            double onePer = 1.0 / len;
            for (int c = 0; c < 3; c++)
            {
                double ssim1 = 0, ssim4 = 0, ring1 = 0, ring4 = 0, blur1 = 0, blur4 = 0;
                double wSsim = W[(((c * 6) + scale) * 6) + 0], wRing = W[(((c * 6) + scale) * 6) + 1], wBlur = W[(((c * 6) + scale) * 6) + 2];
                bool contribute = wSsim != 0 || wRing != 0 || wBlur != 0;
                for (int i = 0; i < len; i++)
                {
                    float m1 = mu1[c][i], m2 = mu2[c][i];
                    double numM = 1.0 - ((m1 - m2) * (m1 - m2));
                    double numS = (2 * (s12[c][i] - (m1 * m2))) + kC2;
                    double denomS = (s11[c][i] - (m1 * m1)) + (s22[c][i] - (m2 * m2)) + kC2;
                    double d = Math.Max(1.0 - (numM * numS / denomS), 0.0);
                    ssim1 += d; ssim4 += P4(d);

                    double d1 = ((1.0 + Math.Abs(x2[c][i] - m2)) / (1.0 + Math.Abs(x1[c][i] - m1))) - 1.0;
                    double artifact = Math.Max(d1, 0.0), detail = Math.Max(-d1, 0.0);
                    ring1 += artifact; ring4 += P4(artifact);
                    blur1 += detail; blur4 += P4(detail);

                    if (contribute)
                    {
                        double e = (wSsim * d) + (wRing * artifact) + (wBlur * detail);
                        if (e != 0)
                        {
                            int x = i % cw, y = i / cw;
                            int ox0 = x * step, oy0 = y * step;
                            int bx0 = ox0 / 8, by0 = oy0 / 8;
                            int bx1 = Math.Min((ox0 + step - 1) / 8, bw - 1);
                            int by1 = Math.Min((oy0 + step - 1) / 8, bh - 1);
                            double share = e / ((bx1 - bx0 + 1) * (by1 - by0 + 1));
                            for (int byy = by0; byy <= by1; byy++)
                            {
                                for (int bxx = bx0; bxx <= bx1; bxx++)
                                {
                                    blockErr[(byy * bw) + bxx] += share;
                                }
                            }
                        }
                    }
                }

                sa.Ssim[(c * 2) + 0] = onePer * ssim1;
                sa.Ssim[(c * 2) + 1] = Math.Sqrt(Math.Sqrt(onePer * ssim4));
                sa.Edge[(c * 4) + 0] = onePer * ring1;
                sa.Edge[(c * 4) + 1] = Math.Sqrt(Math.Sqrt(onePer * ring4));
                sa.Edge[(c * 4) + 2] = onePer * blur1;
                sa.Edge[(c * 4) + 3] = Math.Sqrt(Math.Sqrt(onePer * blur4));
            }

            if (scale == 5)
            {
                break;
            }

            a = Downsample2(a, cw, ch);
            b = Downsample2(b, cw, ch);
            cw = (cw + 1) / 2;
            ch = (ch + 1) / 2;
        }

        return (ScoreFrom(scales), blockErr);
    }

    private static double ScoreFrom(ScaleAvg[] scales)
    {
        double ssim = 0;
        int i = 0;
        for (int c = 0; c < 3; c++)
        {
            for (int scale = 0; scale < 6; scale++)
            {
                var sa = scales[scale];
                for (int n = 0; n < 2; n++)
                {
                    ssim += W[i++] * Math.Abs(sa.Ssim[(c * 2) + n]);
                    ssim += W[i++] * Math.Abs(sa.Edge[(c * 4) + n]);
                    ssim += W[i++] * Math.Abs(sa.Edge[(c * 4) + n + 2]);
                }
            }
        }

        ssim *= 0.9562382616834844;
        ssim = (2.326765642916932 * ssim) - (0.020884521182843837 * ssim * ssim) + (6.248496625763138e-05 * ssim * ssim * ssim);
        return ssim > 0 ? 100.0 - (10.0 * PortableMathD.Pow(ssim, 0.6276336467831387)) : 100.0;
    }

    private static double P4(double x)
    {
        x *= x;
        return x * x;
    }

    private static void MakePositive(float[][] xyb, int len)
    {
        for (int i = 0; i < len; i++)
        {
            float X = xyb[0][i], Y = xyb[1][i], B = xyb[2][i];
            xyb[0][i] = (X * 14f) + 0.42f;
            xyb[1][i] = Y + 0.01f;
            xyb[2][i] = (B - Y) + 0.55f;
        }
    }

    private static float[][] Mul(float[][] a, float[][] b, int len)
    {
        var o = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            o[c] = new float[len];
            for (int i = 0; i < len; i++)
            {
                o[c][i] = a[c][i] * b[c][i];
            }
        }

        return o;
    }

    private static float[][] Downsample2(float[][] img, int w, int h)
    {
        int ow = (w + 1) / 2, oh = (h + 1) / 2;
        var o = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            o[c] = new float[ow * oh];
            for (int oy = 0; oy < oh; oy++)
            {
                for (int ox = 0; ox < ow; ox++)
                {
                    float sum = 0;
                    for (int iy = 0; iy < 2; iy++)
                    {
                        for (int ix = 0; ix < 2; ix++)
                        {
                            int x = Math.Min((ox * 2) + ix, w - 1);
                            int y = Math.Min((oy * 2) + iy, h - 1);
                            sum += img[c][(y * w) + x];
                        }
                    }

                    o[c][(oy * ow) + ox] = sum * 0.25f;
                }
            }
        }

        return o;
    }

    private static readonly float[] Kernel = MakeKernel(1.5f, 5);

    private static float[] MakeKernel(float sigma, int radius)
    {
        var k = new float[(2 * radius) + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            float v = PortableMath.Exp(-(i * i) / (2 * sigma * sigma));
            k[i + radius] = v;
            sum += v;
        }

        for (int i = 0; i < k.Length; i++)
        {
            k[i] /= sum;
        }

        return k;
    }

    private static float[][] Blur3(float[][] img, int w, int h)
    {
        var o = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            o[c] = BlurPlane(img[c], w, h);
        }

        return o;
    }

    private static float[] BlurPlane(float[] img, int w, int h)
    {
        int radius = Kernel.Length / 2;
        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    acc += Kernel[k + radius] * img[(y * w) + Math.Clamp(x + k, 0, w - 1)];
                }

                tmp[(y * w) + x] = acc;
            }
        }

        var o = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    acc += Kernel[k + radius] * tmp[(Math.Clamp(y + k, 0, h - 1) * w) + x];
                }

                o[(y * w) + x] = acc;
            }
        }

        return o;
    }
}
