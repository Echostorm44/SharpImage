// JPEG XL VarDCT (lossy) encoder — forward pipeline: the exact inverse of the JxlVarDct decoder's
// reconstruction (sRGB->XYB, forward DCT, quantise), so that dequantise + inverse-DCT + XYB->sRGB
// reproduces the source within quantisation error. This file is the mathematical core; the codestream
// writer (frame header, LfGlobal, LfGroup DC-modular, HfGlobal, PassGroup HF coefficient coding) builds
// on top of it. WIP — only the 8x8 DCT path is implemented so far, verified by round-tripping through the
// decoder's own inverse operations (JxlVarDctEncoder.ReconstructPsnr).
using System;
using SharpImage.Core;

namespace SharpImage.Formats.Jxl;

internal static class JxlVarDctEncoder
{
    // sRGB [0,1] -> XYB, the exact inverse of JxlVarDct.XybToSrgb. srgb has 3 channels of length `len`.
    public static float[][] SrgbToXyb(float[][] srgb, int len, VarDctFrameParams fp)
    {
        float itscale = 255.0f / fp.IntensityTarget;
        float[] ob = fp.OpsinBias;
        float[] cbrtOb = { PortableMath.Cbrt(ob[0]), PortableMath.Cbrt(ob[1]), PortableMath.Cbrt(ob[2]) };
        float[] minv = Invert3x3(fp.OpsinInv); // forward opsin absorbance matrix

        var xyb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            xyb[c] = new float[len];
        }

        float[]? pm = fp.PrimFwd; // target-linear -> sRGB-linear (null == sRGB, identity)
        for (int i = 0; i < len; i++)
        {
            float rl = SrgbToLinear(srgb[0][i]);
            float gl = SrgbToLinear(srgb[1][i]);
            float bl = SrgbToLinear(srgb[2][i]);

            if (pm != null)
            {
                float rr = (pm[0] * rl) + (pm[1] * gl) + (pm[2] * bl);
                float gg = (pm[3] * rl) + (pm[4] * gl) + (pm[5] * bl);
                float bb = (pm[6] * rl) + (pm[7] * gl) + (pm[8] * bl);
                rl = rr; gl = gg; bl = bb;
            }

            float lms0 = (minv[0] * rl) + (minv[1] * gl) + (minv[2] * bl);
            float lms1 = (minv[3] * rl) + (minv[4] * gl) + (minv[5] * bl);
            float lms2 = (minv[6] * rl) + (minv[7] * gl) + (minv[8] * bl);

            float g0 = PortableMath.Cbrt((lms0 / itscale) - ob[0]);
            float g1 = PortableMath.Cbrt((lms1 / itscale) - ob[1]);
            float g2 = PortableMath.Cbrt((lms2 / itscale) - ob[2]);

            float yPlusX = g0 + cbrtOb[0];
            float yMinusX = g1 + cbrtOb[1];
            xyb[0][i] = (yPlusX - yMinusX) * 0.5f;       // X
            xyb[1][i] = (yPlusX + yMinusX) * 0.5f;       // Y
            xyb[2][i] = g2 + cbrtOb[2];                  // B
        }

        return xyb;
    }

    // Linear-RGB [0,1] planes -> opsin XYB (the ToXYB SSIMULACRA2 uses; no sRGB gamma step). rl/gl/bl are
    // three planes of length len.
    public static float[][] LinearToXyb(float[][] lin, int len, VarDctFrameParams fp)
    {
        float itscale = 255.0f / fp.IntensityTarget;
        float[] ob = fp.OpsinBias;
        float[] cbrtOb = { PortableMath.Cbrt(ob[0]), PortableMath.Cbrt(ob[1]), PortableMath.Cbrt(ob[2]) };
        float[] minv = Invert3x3(fp.OpsinInv);
        var xyb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            xyb[c] = new float[len];
        }

        for (int i = 0; i < len; i++)
        {
            float rl = lin[0][i], gl = lin[1][i], bl = lin[2][i];
            float lms0 = (minv[0] * rl) + (minv[1] * gl) + (minv[2] * bl);
            float lms1 = (minv[3] * rl) + (minv[4] * gl) + (minv[5] * bl);
            float lms2 = (minv[6] * rl) + (minv[7] * gl) + (minv[8] * bl);
            float g0 = PortableMath.Cbrt((lms0 / itscale) - ob[0]);
            float g1 = PortableMath.Cbrt((lms1 / itscale) - ob[1]);
            float g2 = PortableMath.Cbrt((lms2 / itscale) - ob[2]);
            float yPlusX = g0 + cbrtOb[0];
            float yMinusX = g1 + cbrtOb[1];
            xyb[0][i] = (yPlusX - yMinusX) * 0.5f;
            xyb[1][i] = (yPlusX + yMinusX) * 0.5f;
            xyb[2][i] = g2 + cbrtOb[2];
        }

        return xyb;
    }

    public static float SrgbToLinearPublic(float s) => SrgbToLinear(s);

    private static float SrgbToLinear(float s)
    {
        if (s <= 0f)
        {
            return 0f;
        }

        if (s >= 1f)
        {
            return 1f;
        }

        return s <= 0.04045f ? s / 12.92f : PortableMath.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    // Inverse of a row-major 3x3 matrix (Cramer's rule).
    private static float[] Invert3x3(float[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        double id = 1.0 / det;
        return new[]
        {
            (float)(((e * i) - (f * h)) * id), (float)(((c * h) - (b * i)) * id), (float)(((b * f) - (c * e)) * id),
            (float)(((f * g) - (d * i)) * id), (float)(((a * i) - (c * g)) * id), (float)(((c * d) - (a * f)) * id),
            (float)(((d * h) - (e * g)) * id), (float)(((b * g) - (a * h)) * id), (float)(((a * e) - (b * d)) * id),
        };
    }

    // The LF (DC) dequant scale for XYB channel c (inverse of JxlVarDct.CopyLfDequant, extraPrecision 0).
    private static float DcScale(int c, VarDctFrameParams fp, uint globalScale, uint quantLf)
    {
        float[] mlf = { 1f / 32f, 1f / 4f, 1f / 2f }; // default lf_channel_dequant {m_x, m_y, m_b}
        double scaleInv = (double)globalScale * quantLf;
        return (float)(mlf[c] * 512.0 / scaleInv);
    }

    // The HF (AC) dequant multiplier (inverse of JxlVarDct.DequantHf, X/BQmScale 2 => qmScale 1).
    // globalScale is the frame-global quantiser field; blockHfMul is the per-block integer multiplier (>=1).
    private static float HfMul(uint globalScale, uint blockHfMul) => 65536.0f / (globalScale * (float)blockHfMul);

    // Forward-quantises then dequantises the whole image the way the decoder would, runs the inverse
    // transform + XYB->sRGB, and returns the reconstruction PSNR (dB) against the source. This validates
    // that the forward math is the exact inverse of the decoder before the codestream writer is built.
    // All blocks use the 8x8 DCT; chroma-from-luma, adaptive quant, gaborish and EPF are off.
    public static double ReconstructPsnr(float[][] srgb, int w, int h, VarDctFrameParams fp, uint globalScale, uint quantLf, uint blockHfMul = 1)
    {
        int stride = ((w + 7) / 8) * 8;
        int strideH = ((h + 7) / 8) * 8;
        int bw = stride / 8, bh = strideH / 8;

        // Pad the source to whole blocks (edge-replicate), then convert to XYB.
        var padded = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            padded[c] = new float[stride * strideH];
            for (int y = 0; y < strideH; y++)
            {
                int sy = Math.Min(y, h - 1);
                for (int x = 0; x < stride; x++)
                {
                    int sx = Math.Min(x, w - 1);
                    padded[c][(y * stride) + x] = srgb[c][(sy * w) + sx];
                }
            }
        }

        float[][] xyb = SrgbToXyb(padded, stride * strideH, fp);

        DequantMatrixSet dm = DequantMatrixSet.Default();
        float[] hfMul = { HfMul(globalScale, blockHfMul), HfMul(globalScale, blockHfMul), HfMul(globalScale, blockHfMul) };
        float[] dcScale = { DcScale(0, fp, globalScale, quantLf), DcScale(1, fp, globalScale, quantLf), DcScale(2, fp, globalScale, quantLf) };

        var recon = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            recon[c] = new float[stride * strideH];
            float[] matrix = dm.Get(c, TransformType.Dct8);
            float quantBias = fp.QuantBias[c];

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    // Forward DCT of the 8x8 block.
                    var block = new JxlDct.Grid(xyb[c], (by * 8 * stride) + (bx * 8), stride, 8, 8);
                    var coeff = new float[64];
                    var g = new JxlDct.Grid(coeff, 0, 8, 8, 8);
                    for (int yy = 0; yy < 8; yy++)
                    {
                        for (int xx = 0; xx < 8; xx++)
                        {
                            g.Set(xx, yy, block.Get(xx, yy));
                        }
                    }

                    JxlDct.Dct2D(g, false);

                    // Quantise + dequantise each coefficient exactly as the codec would.
                    for (int k = 0; k < 64; k++)
                    {
                        if (k == 0)
                        {
                            int dcInt = (int)MathF.Round(coeff[0] / dcScale[c]);
                            coeff[0] = dcInt * dcScale[c];
                        }
                        else
                        {
                            float step = matrix[k] * hfMul[c];
                            int q = (int)MathF.Round(coeff[k] / step);
                            float qf = q;
                            if (MathF.Abs(qf) <= 1.0f)
                            {
                                qf *= quantBias;
                            }
                            else
                            {
                                qf -= fp.QuantBiasNumerator / qf;
                            }

                            coeff[k] = qf * matrix[k] * hfMul[c];
                        }
                    }

                    // Inverse DCT back to pixels.
                    JxlDct.Dct2D(g, true);
                    for (int yy = 0; yy < 8; yy++)
                    {
                        for (int xx = 0; xx < 8; xx++)
                        {
                            recon[c][((by * 8 + yy) * stride) + (bx * 8) + xx] = g.Get(xx, yy);
                        }
                    }
                }
            }
        }

        JxlVarDct.XybToSrgb(recon, stride * strideH, fp);

        // PSNR over the actual (unpadded) region, averaged over channels, in 8-bit units.
        double mse = 0;
        long count = 0;
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double a = srgb[c][(y * w) + x] * 255.0;
                    double b = recon[c][(y * stride) + x] * 255.0;
                    double d = a - b;
                    mse += d * d;
                    count++;
                }
            }
        }

        mse /= count;
        return mse <= 1e-9 ? 99.0 : 10.0 * PortableMathD.Log10((255.0 * 255.0) / mse);
    }
}
