// JPEG XL VarDCT dequantization matrix parameters and weight generation. Ported from jxl-oxide
// jxl-vardct dequant.rs (DequantMatrixParams / into_matrix) and libjxl.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

internal sealed class DequantMatrixParams
{
    private enum Kind { Hornuss, Dct2, Dct4, Dct4x8, Afv, Dct, Raw }

    private readonly TransformType dctSelect;
    private Kind kind;
    private float[][] fixedParams = Array.Empty<float[]>(); // [3][N]
    private float[][] dctParams = Array.Empty<float[]>();    // [3][]
    private float[][] dct4x4Params = Array.Empty<float[]>();
    private float denominator;
    private List<JxlChannel>? rawChannels;

    private DequantMatrixParams(TransformType t) => dctSelect = t;

    private static readonly float[] SeqA = { -1.025f, -0.78f, -0.65012f, -0.19041574f, -0.20819396f, -0.421064f, -0.32733846f };
    private static readonly float[] SeqB = { -0.30419582f, -0.36330363f, -0.3566038f, -0.34430745f, -0.33699593f, -0.30180866f, -0.27321684f };
    private static readonly float[] SeqC = { -1.2f, -1.2f, -0.8f, -0.7f, -0.7f, -0.4f, -0.5f };
    private static readonly float[][] Dct4x8ParamsC =
    {
        new[] { 2198.0505f, -0.96269625f, -0.7619425f, -0.65511405f },
        new[] { 764.36554f, -0.926302f, -0.967523f, -0.2784529f },
        new[] { 527.10754f, -1.4594386f, -1.4500821f, -1.5843723f },
    };
    private static readonly float[][] Dct4ParamsC =
    {
        new[] { 2200.0f, 0.0f, 0.0f, 0.0f },
        new[] { 392.0f, 0.0f, 0.0f, 0.0f },
        new[] { 112.0f, -0.25f, -0.25f, -0.5f },
    };

    private static float[] Seq(float first, float[] rest)
    {
        var v = new float[1 + rest.Length];
        v[0] = first;
        Array.Copy(rest, 0, v, 1, rest.Length);
        return v;
    }

    private static DequantMatrixParams MakeDctCommonSeq(TransformType t, float a, float b, float c)
    {
        var p = new DequantMatrixParams(t) { kind = Kind.Dct };
        p.dctParams = new[] { Seq(a, SeqA), Seq(b, SeqB), Seq(c, SeqC) };
        return p;
    }

    public static DequantMatrixParams DefaultWith(TransformType t)
    {
        switch (t)
        {
            case TransformType.Dct8:
                return new DequantMatrixParams(t) { kind = Kind.Dct, dctParams = new[]
                {
                    new[] { 3150.0f, 0.0f, -0.4f, -0.4f, -0.4f, -2.0f },
                    new[] { 560.0f, 0.0f, -0.3f, -0.3f, -0.3f, -0.3f },
                    new[] { 512.0f, -2.0f, -1.0f, 0.0f, -1.0f, -2.0f },
                } };
            case TransformType.Hornuss:
                return new DequantMatrixParams(t) { kind = Kind.Hornuss, fixedParams = new[]
                {
                    new[] { 280.0f, 3160.0f, 3160.0f },
                    new[] { 60.0f, 864.0f, 864.0f },
                    new[] { 18.0f, 200.0f, 200.0f },
                } };
            case TransformType.Dct2:
                return new DequantMatrixParams(t) { kind = Kind.Dct2, fixedParams = new[]
                {
                    new[] { 3840.0f, 2560.0f, 1280.0f, 640.0f, 480.0f, 300.0f },
                    new[] { 960.0f, 640.0f, 320.0f, 180.0f, 140.0f, 120.0f },
                    new[] { 640.0f, 320.0f, 128.0f, 64.0f, 32.0f, 16.0f },
                } };
            case TransformType.Dct4:
                return new DequantMatrixParams(t) { kind = Kind.Dct4, fixedParams = new[] { new[] { 1f, 1f }, new[] { 1f, 1f }, new[] { 1f, 1f } }, dctParams = Clone(Dct4ParamsC) };
            case TransformType.Dct16:
                return new DequantMatrixParams(t) { kind = Kind.Dct, dctParams = new[]
                {
                    new[] { 8996.873f, -1.3000778f, -0.4942453f, -0.43909377f, -0.6350102f, -0.9017726f, -1.6162099f },
                    new[] { 3191.4836f, -0.67424583f, -0.80745816f, -0.4492584f, -0.3586544f, -0.3132239f, -0.37615025f },
                    new[] { 1157.504f, -2.0531423f, -1.4f, -0.5068713f, -0.4270873f, -1.4856834f, -4.920914f },
                } };
            case TransformType.Dct32:
                return new DequantMatrixParams(t) { kind = Kind.Dct, dctParams = new[]
                {
                    new[] { 15718.408f, -1.025f, -0.98f, -0.9012f, -0.4f, -0.48819396f, -0.421064f, -0.27f },
                    new[] { 7305.7637f, -0.8041958f, -0.76330364f, -0.5566038f, -0.49785304f, -0.43699592f, -0.40180868f, -0.27321684f },
                    new[] { 3803.5317f, -3.0607336f, -2.041327f, -2.023565f, -0.54953897f, -0.4f, -0.4f, -0.3f },
                } };
            case TransformType.Dct16x8:
            case TransformType.Dct8x16:
                return new DequantMatrixParams(t) { kind = Kind.Dct, dctParams = new[]
                {
                    new[] { 7240.7734f, -0.7f, -0.7f, -0.2f, -0.2f, -0.2f, -0.5f },
                    new[] { 1448.1547f, -0.5f, -0.5f, -0.5f, -0.2f, -0.2f, -0.2f },
                    new[] { 506.85413f, -1.4f, -0.2f, -0.5f, -0.5f, -1.5f, -3.6f },
                } };
            case TransformType.Dct32x8:
            case TransformType.Dct8x32:
                return new DequantMatrixParams(t) { kind = Kind.Dct, dctParams = new[]
                {
                    new[] { 16283.249f, -1.7812846f, -1.6309059f, -1.0382179f, -0.85f, -0.7f, -0.9f, -1.2360638f },
                    new[] { 5089.1577f, -0.3200494f, -0.3536285f, -0.3034f, -0.61f, -0.5f, -0.5f, -0.6f },
                    new[] { 3397.7761f, -0.32132736f, -0.3450762f, -0.7034f, -0.9f, -1.0f, -1.0f, -1.1754606f },
                } };
            case TransformType.Dct16x32:
            case TransformType.Dct32x16:
                return new DequantMatrixParams(t) { kind = Kind.Dct, dctParams = new[]
                {
                    new[] { 13844.971f, -0.971138f, -0.658f, -0.42026f, -0.22712f, -0.2206f, -0.226f, -0.6f },
                    new[] { 4798.964f, -0.6112531f, -0.8377079f, -0.7901486f, -0.26927274f, -0.38272768f, -0.22924222f, -0.20719099f },
                    new[] { 1807.2369f, -1.2f, -1.2f, -0.7f, -0.7f, -0.7f, -0.4f, -0.5f },
                } };
            case TransformType.Dct4x8:
            case TransformType.Dct8x4:
                return new DequantMatrixParams(t) { kind = Kind.Dct4x8, fixedParams = new[] { new[] { 1f }, new[] { 1f }, new[] { 1f } }, dctParams = Clone(Dct4x8ParamsC) };
            case TransformType.Afv0:
            case TransformType.Afv1:
            case TransformType.Afv2:
            case TransformType.Afv3:
                return new DequantMatrixParams(t) { kind = Kind.Afv, fixedParams = new[]
                {
                    new[] { 3072.0f, 3072.0f, 256.0f, 256.0f, 256.0f, 414.0f, 0.0f, 0.0f, 0.0f },
                    new[] { 1024.0f, 1024.0f, 50.0f, 50.0f, 50.0f, 58.0f, 0.0f, 0.0f, 0.0f },
                    new[] { 384.0f, 384.0f, 12.0f, 12.0f, 12.0f, 22.0f, -0.25f, -0.25f, -0.25f },
                }, dctParams = Clone(Dct4x8ParamsC), dct4x4Params = Clone(Dct4ParamsC) };
            case TransformType.Dct64: return MakeDctCommonSeq(t, 23966.166f, 8380.191f, 4493.024f);
            case TransformType.Dct32x64:
            case TransformType.Dct64x32: return MakeDctCommonSeq(t, 15358.898f, 5597.3604f, 2919.9617f);
            case TransformType.Dct128: return MakeDctCommonSeq(t, 47932.332f, 16760.383f, 8986.048f);
            case TransformType.Dct64x128:
            case TransformType.Dct128x64: return MakeDctCommonSeq(t, 30717.797f, 11194.721f, 5839.9233f);
            case TransformType.Dct256: return MakeDctCommonSeq(t, 95864.664f, 33520.766f, 17972.096f);
            case TransformType.Dct128x256:
            case TransformType.Dct256x128: return MakeDctCommonSeq(t, 61435.594f, 24209.441f, 12979.847f);
            default: return MakeDctCommonSeq(t, 3150f, 560f, 512f);
        }
    }

    private static float[][] Clone(float[][] a)
    {
        var r = new float[a.Length][];
        for (int i = 0; i < a.Length; i++)
        {
            r[i] = (float[])a[i].Clone();
        }

        return r;
    }

    public static DequantMatrixParams Parse(JxlBitReader br, TransformType dctSelect, int bitDepth, int streamIndex, List<MaNode>? globalTree, JxlAnsCode? globalCode)
    {
        int mode = (int)br.ReadBits(3);
        switch (mode)
        {
            case 0:
                return DefaultWith(dctSelect);
            case 1:
                return new DequantMatrixParams(dctSelect) { kind = Kind.Hornuss, fixedParams = ReadFixed(br, 3) };
            case 2:
                return new DequantMatrixParams(dctSelect) { kind = Kind.Dct2, fixedParams = ReadFixed(br, 6) };
            case 3:
                return new DequantMatrixParams(dctSelect) { kind = Kind.Dct4, fixedParams = ReadFixed(br, 2), dctParams = ReadDctParams(br) };
            case 4:
                return new DequantMatrixParams(dctSelect) { kind = Kind.Dct4x8, fixedParams = ReadFixed(br, 1), dctParams = ReadDctParams(br) };
            case 5:
            {
                float[][] pr = ReadFixed(br, 9);
                foreach (float[] row in pr)
                {
                    for (int j = 0; j < 6; j++)
                    {
                        row[j] *= 64.0f;
                    }
                }

                return new DequantMatrixParams(dctSelect) { kind = Kind.Afv, fixedParams = pr, dctParams = ReadDctParams(br), dct4x4Params = ReadDctParams(br) };
            }
            case 6:
                return new DequantMatrixParams(dctSelect) { kind = Kind.Dct, dctParams = ReadDctParams(br) };
            case 7:
            {
                var (w, h) = JxlDct.DequantMatrixSize(dctSelect);
                float denom = br.ReadF16();
                var chans = new List<JxlChannel> { new(w, h), new(w, h), new(w, h) };
                JxlModular.DecodeSubModular(br, chans, globalTree, globalCode, streamIndex, bitDepth);
                return new DequantMatrixParams(dctSelect) { kind = Kind.Raw, denominator = denom, rawChannels = chans };
            }
            default:
                throw new InvalidOperationException("Invalid dequant encoding mode.");
        }
    }

    private static float[][] ReadFixed(JxlBitReader br, int n)
    {
        var outp = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            outp[c] = new float[n];
            for (int i = 0; i < n; i++)
            {
                outp[c][i] = br.ReadF16();
            }
        }

        return outp;
    }

    private static float[][] ReadDctParams(JxlBitReader br)
    {
        int num = (int)br.ReadBits(4) + 1;
        var outp = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            outp[c] = new float[num];
            for (int i = 0; i < num; i++)
            {
                outp[c][i] = br.ReadF16();
            }
        }

        for (int c = 0; c < 3; c++)
        {
            outp[c][0] *= 64.0f;
        }

        return outp;
    }

    private static float Mult(float x) => x > 0f ? 1f + x : 1f / (1f - x);

    private static float Interpolate(float pos, float max, float[] bands)
    {
        int len = bands.Length;
        if (len == 1)
        {
            return bands[0];
        }

        float scaledPos = pos * (len - 1) / max;
        int scaledIndex = (int)scaledPos;
        float frac = scaledPos - scaledIndex;
        float a = bands[scaledIndex];
        float b = bands[scaledIndex + 1];
        return a * MathF.Pow(b / a, frac);
    }

    private static float[] DctQuantWeights(float[] p, int width, int height)
    {
        var bands = new float[p.Length];
        bands[0] = p[0];
        float last = p[0];
        for (int i = 1; i < p.Length; i++)
        {
            float band = last * Mult(p[i]);
            bands[i] = band;
            last = band;
        }

        var ret = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x / (float)(width - 1);
                float dy = y / (float)(height - 1);
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                ret[(y * width) + x] = Interpolate(distance, MathF.Sqrt(2f) + 1e-6f, bands);
            }
        }

        return ret;
    }

    public float[][] IntoMatrix()
    {
        bool needRecip = kind != Kind.Raw;
        float[][] weights;
        var (mw, mh) = JxlDct.DequantMatrixSize(dctSelect);
        switch (kind)
        {
            case Kind.Dct:
                weights = new[] { DctQuantWeights(dctParams[0], mw, mh), DctQuantWeights(dctParams[1], mw, mh), DctQuantWeights(dctParams[2], mw, mh) };
                break;
            case Kind.Hornuss:
                weights = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    var r = new float[64];
                    for (int i = 0; i < 64; i++)
                    {
                        r[i] = fixedParams[c][0];
                    }

                    r[0] = 1.0f;
                    r[1] = fixedParams[c][1];
                    r[8] = fixedParams[c][1];
                    r[9] = fixedParams[c][2];
                    weights[c] = r;
                }

                break;
            case Kind.Dct2:
                weights = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    var r = new float[64];
                    r[0] = 1.0f;
                    for (int idx = 0; idx < 6; idx++)
                    {
                        float val = fixedParams[c][idx];
                        int shift = idx / 2;
                        int dim = 1 << shift;
                        if (idx % 2 == 0)
                        {
                            for (int y = 0; y < dim; y++)
                            {
                                for (int x = dim; x < dim * 2; x++)
                                {
                                    r[(y * 8) + x] = val;
                                    r[(x * 8) + y] = val;
                                }
                            }
                        }
                        else
                        {
                            for (int y = dim; y < dim * 2; y++)
                            {
                                for (int x = dim; x < dim * 2; x++)
                                {
                                    r[(y * 8) + x] = val;
                                }
                            }
                        }
                    }

                    weights[c] = r;
                }

                break;
            case Kind.Dct4:
                weights = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    float[] mat = DctQuantWeights(dctParams[c], 4, 4);
                    var r = new float[64];
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            float v = mat[(y * 4) + x];
                            r[(y * 16) + (x * 2)] = v;
                            r[(y * 16) + (x * 2) + 1] = v;
                            r[(((y * 2) + 1) * 8) + (x * 2)] = v;
                            r[(((y * 2) + 1) * 8) + (x * 2) + 1] = v;
                        }
                    }

                    r[1] /= fixedParams[c][0];
                    r[8] /= fixedParams[c][0];
                    r[9] /= fixedParams[c][1];
                    weights[c] = r;
                }

                break;
            case Kind.Dct4x8:
                weights = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    float[] mat = DctQuantWeights(dctParams[c], 8, 4);
                    var r = new float[64];
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            r[(((y * 2) + 0) * 8) + x] = mat[(y * 8) + x];
                            r[(((y * 2) + 1) * 8) + x] = mat[(y * 8) + x];
                        }
                    }

                    r[8] /= fixedParams[c][0];
                    weights[c] = r;
                }

                break;
            case Kind.Afv:
                weights = new float[3][];
                float[] freqs = { 0.0f, 0.0f, 0.8517779f, 5.3777843f, 0.0f, 0.0f, 4.734748f, 5.4492455f, 1.659827f, 4.0f, 7.275749f, 10.423227f, 2.6629324f, 7.6306577f, 8.962389f, 12.971662f };
                float freqLo = freqs[2];
                float freqHi = freqs[15];
                for (int c = 0; c < 3; c++)
                {
                    float[] weights4x8 = DctQuantWeights(dctParams[c], 8, 4);
                    float[] weights4x4 = DctQuantWeights(dct4x4Params[c], 4, 4);
                    float[] p = fixedParams[c];
                    float[] bands = { p[5], 0f, 0f, 0f };
                    float prev = bands[0];
                    for (int i = 1; i < 4; i++)
                    {
                        bands[i] = prev * Mult(p[5 + i]);
                        prev = bands[i];
                    }

                    var r = new float[64];
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            float v;
                            if (x == 0 && y == 0)
                            {
                                v = 1.0f;
                            }
                            else if (x == 0 && y == 1)
                            {
                                v = p[2];
                            }
                            else if (x == 1 && y == 0)
                            {
                                v = p[3];
                            }
                            else if (x == 1 && y == 1)
                            {
                                v = p[4];
                            }
                            else
                            {
                                v = Interpolate(freqs[(y * 4) + x] - freqLo, freqHi - freqLo + 1e-6f, bands);
                            }

                            r[(16 * y) + (2 * x)] = v;
                        }
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        // row1 (odd row) from weights_4x8; row0 even positions from weights_4x4.
                        for (int x = 0; x < 8; x++)
                        {
                            float dctW = weights4x8[(y * 8) + x];
                            r[(16 * y) + 8 + x] = (y == 0 && x == 0) ? p[0] : dctW;
                        }

                        for (int x = 0; x < 4; x++)
                        {
                            float dctW = weights4x4[(y * 4) + x];
                            r[(16 * y) + (x * 2) + 1] = (y == 0 && x == 0) ? p[1] : dctW;
                        }
                    }

                    weights[c] = r;
                }

                break;
            case Kind.Raw:
                weights = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    int[] px = rawChannels![c].Px;
                    var r = new float[mw * mh];
                    for (int i = 0; i < r.Length; i++)
                    {
                        r[i] = px[i] * denominator;
                    }

                    weights[c] = r;
                }

                break;
            default:
                throw new InvalidOperationException();
        }

        if (needRecip)
        {
            foreach (float[] w in weights)
            {
                for (int i = 0; i < w.Length; i++)
                {
                    w[i] = 1.0f / w[i];
                }
            }
        }

        return weights;
    }
}
