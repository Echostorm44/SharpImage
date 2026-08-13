// JPEG XL VarDCT tables and dequantization matrices: the AFV basis, the natural (coefficient) order
// per block size, and the DequantMatrixSet (parse + weight generation). Ported from jxl-oxide
// (jxl-vardct dequant.rs, hf_pass.rs) and libjxl.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

internal static class JxlVarDctTables
{
    public static readonly float[][] AfvBasis =
    {
        new[] { 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f },
        new[] { 0.876902929799142f, 0.2206518106944235f, -0.10140050393753763f, -0.1014005039375375f, 0.2206518106944236f, -0.10140050393753777f, -0.10140050393753772f, -0.10140050393753763f, -0.10140050393753758f, -0.10140050393753769f, -0.1014005039375375f, -0.10140050393753768f, -0.10140050393753768f, -0.10140050393753759f, -0.10140050393753763f, -0.10140050393753741f },
        new[] { 0.0f, 0.0f, 0.40670075830260755f, 0.44444816619734445f, 0.0f, 0.0f, 0.19574399372042936f, 0.2929100136981264f, -0.40670075830260716f, -0.19574399372042872f, 0.0f, 0.11379074460448091f, -0.44444816619734384f, -0.29291001369812636f, -0.1137907446044814f, 0.0f },
        new[] { 0.0f, 0.0f, -0.21255748058288748f, 0.3085497062849767f, 0.0f, 0.4706702258572536f, -0.1621205195722993f, 0.0f, -0.21255748058287047f, -0.16212051957228327f, -0.47067022585725277f, -0.1464291867126764f, 0.3085497062849487f, 0.0f, -0.14642918671266536f, 0.4251149611657548f },
        new[] { 0.0f, -0.7071067811865474f, 0.0f, 0.0f, 0.7071067811865475f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f },
        new[] { -0.4105377591765233f, 0.6235485373547691f, -0.06435071657946274f, -0.06435071657946266f, 0.6235485373547694f, -0.06435071657946284f, -0.0643507165794628f, -0.06435071657946274f, -0.06435071657946272f, -0.06435071657946279f, -0.06435071657946266f, -0.06435071657946277f, -0.06435071657946277f, -0.06435071657946273f, -0.06435071657946274f, -0.0643507165794626f },
        new[] { 0.0f, 0.0f, -0.4517556589999482f, 0.15854503551840063f, 0.0f, -0.04038515160822202f, 0.0074182263792423875f, 0.39351034269210167f, -0.45175565899994635f, 0.007418226379244351f, 0.1107416575309343f, 0.08298163094882051f, 0.15854503551839705f, 0.3935103426921022f, 0.0829816309488214f, -0.45175565899994796f },
        new[] { 0.0f, 0.0f, -0.304684750724869f, 0.5112616136591823f, 0.0f, 0.0f, -0.290480129728998f, -0.06578701549142804f, 0.304684750724884f, 0.2904801297290076f, 0.0f, -0.23889773523344604f, -0.5112616136592012f, 0.06578701549142545f, 0.23889773523345467f, 0.0f },
        new[] { 0.0f, 0.0f, 0.3017929516615495f, 0.25792362796341184f, 0.0f, 0.16272340142866204f, 0.09520022653475037f, 0.0f, 0.3017929516615503f, 0.09520022653475055f, -0.16272340142866173f, -0.35312385449816297f, 0.25792362796341295f, 0.0f, -0.3531238544981624f, -0.6035859033230976f },
        new[] { 0.0f, 0.0f, 0.40824829046386274f, 0.0f, 0.0f, 0.0f, 0.0f, -0.4082482904638628f, -0.4082482904638635f, 0.0f, 0.0f, -0.40824829046386296f, 0.0f, 0.4082482904638634f, 0.408248290463863f, 0.0f },
        new[] { 0.0f, 0.0f, 0.1747866975480809f, 0.0812611176717539f, 0.0f, 0.0f, -0.3675398009862027f, -0.307882213957909f, -0.17478669754808135f, 0.3675398009862011f, 0.0f, 0.4826689115059883f, -0.08126111767175039f, 0.30788221395790305f, -0.48266891150598584f, 0.0f },
        new[] { 0.0f, 0.0f, -0.21105601049335784f, 0.18567180916109802f, 0.0f, 0.0f, 0.49215859013738733f, -0.38525013709251915f, 0.21105601049335806f, -0.49215859013738905f, 0.0f, 0.17419412659916217f, -0.18567180916109904f, 0.3852501370925211f, -0.1741941265991621f, 0.0f },
        new[] { 0.0f, 0.0f, -0.14266084808807264f, -0.3416446842253372f, 0.0f, 0.7367497537172237f, 0.24627107722075148f, -0.08574019035519306f, -0.14266084808807344f, 0.24627107722075137f, 0.14883399227113567f, -0.04768680350229251f, -0.3416446842253373f, -0.08574019035519267f, -0.047686803502292804f, -0.14266084808807242f },
        new[] { 0.0f, 0.0f, -0.13813540350758585f, 0.3302282550303788f, 0.0f, 0.08755115000587084f, -0.07946706605909573f, -0.4613374887461511f, -0.13813540350758294f, -0.07946706605910261f, 0.49724647109535086f, 0.12538059448563663f, 0.3302282550303805f, -0.4613374887461554f, 0.12538059448564315f, -0.13813540350758452f },
        new[] { 0.0f, 0.0f, -0.17437602599651067f, 0.0702790691196284f, 0.0f, -0.2921026642334881f, 0.3623817333531167f, 0.0f, -0.1743760259965108f, 0.36238173335311646f, 0.29210266423348785f, -0.4326608024727445f, 0.07027906911962818f, 0.0f, -0.4326608024727457f, 0.34875205199302267f },
        new[] { 0.0f, 0.0f, 0.11354987314994337f, -0.07417504595810355f, 0.0f, 0.19402893032594343f, -0.435190496523228f, 0.21918684838857466f, 0.11354987314994257f, -0.4351904965232251f, 0.5550443808910661f, -0.25468277124066463f, -0.07417504595810233f, 0.2191868483885728f, -0.25468277124066413f, 0.1135498731499429f },
    };

    private static readonly (int W, int H)[] BlockSizes =
    {
        (8, 8), (8, 8), (16, 16), (32, 32), (16, 8), (32, 8), (32, 16), (64, 64), (64, 32),
        (128, 128), (128, 64), (256, 256), (256, 128),
    };

    private static readonly (ushort X, ushort Y)[]?[] NaturalOrderCache = new (ushort, ushort)[]?[13];

    public static (ushort X, ushort Y)[] NaturalOrder(int orderId)
    {
        if (NaturalOrderCache[orderId] is { } cached)
        {
            return cached;
        }

        var (bw, bh) = BlockSizes[orderId];
        var outp = new (ushort, ushort)[bw * bh];
        int yScale = bw / bh;
        int idx = 0;
        int lbw = bw / 8;
        int lbh = bh / 8;
        while (idx < lbw * lbh)
        {
            int x = idx % lbw;
            int y = idx / lbw;
            outp[idx] = ((ushort)x, (ushort)y);
            idx++;
        }

        for (int dist = 1; dist < 2 * bw; dist++)
        {
            int margin = Math.Max(0, dist - bw);
            for (int order = margin; order < dist - margin; order++)
            {
                int x, y;
                if (dist % 2 == 1)
                {
                    x = order;
                    y = dist - 1 - order;
                }
                else
                {
                    x = dist - 1 - order;
                    y = order;
                }

                if (x < lbw && y < lbw)
                {
                    continue;
                }

                if (y % yScale != 0)
                {
                    continue;
                }

                outp[idx] = ((ushort)x, (ushort)(y / yScale));
                idx++;
            }
        }

        NaturalOrderCache[orderId] = outp;
        return outp;
    }
}

/// <summary>A set of dequantization matrices for the 17 transform parameter groups.</summary>
internal sealed class DequantMatrixSet
{
    // matrices[paramIdx][channel] = weights in raster order (already reciprocated).
    private readonly float[][][] matrices = new float[17][][];
    private readonly float[][][] matricesTr = new float[17][][];

    private static readonly TransformType[] DctSelectList =
    {
        TransformType.Dct8, TransformType.Hornuss, TransformType.Dct2, TransformType.Dct4, TransformType.Dct16,
        TransformType.Dct32, TransformType.Dct8x16, TransformType.Dct8x32, TransformType.Dct16x32, TransformType.Dct4x8,
        TransformType.Afv0, TransformType.Dct64, TransformType.Dct32x64, TransformType.Dct128, TransformType.Dct64x128,
        TransformType.Dct256, TransformType.Dct128x256,
    };

    private static int ParamIndex(TransformType t) => t switch
    {
        TransformType.Dct8 => 0,
        TransformType.Hornuss => 1,
        TransformType.Dct2 => 2,
        TransformType.Dct4 => 3,
        TransformType.Dct16 => 4,
        TransformType.Dct32 => 5,
        TransformType.Dct16x8 or TransformType.Dct8x16 => 6,
        TransformType.Dct32x8 or TransformType.Dct8x32 => 7,
        TransformType.Dct16x32 or TransformType.Dct32x16 => 8,
        TransformType.Dct4x8 or TransformType.Dct8x4 => 9,
        TransformType.Afv0 or TransformType.Afv1 or TransformType.Afv2 or TransformType.Afv3 => 10,
        TransformType.Dct64 => 11,
        TransformType.Dct32x64 or TransformType.Dct64x32 => 12,
        TransformType.Dct128 => 13,
        TransformType.Dct64x128 or TransformType.Dct128x64 => 14,
        TransformType.Dct256 => 15,
        TransformType.Dct128x256 or TransformType.Dct256x128 => 16,
        _ => 0,
    };

    public float[] Get(int channel, TransformType t) => matrices[ParamIndex(t)][channel];

    public float[] GetTransposed(int channel, TransformType t) => matricesTr[ParamIndex(t)][channel];

    public static DequantMatrixSet Parse(JxlBitReader br, int bitDepth, int numLfGroups, List<MaNode>? globalTree, JxlAnsCode? globalCode)
    {
        var set = new DequantMatrixSet();
        int streamBase = 1 + (numLfGroups * 3);
        bool allDefault = br.ReadBool();
        for (int i = 0; i < 17; i++)
        {
            TransformType dctSelect = DctSelectList[i];
            float[][] mat = allDefault
                ? DequantMatrixParams.DefaultWith(dctSelect).IntoMatrix()
                : DequantMatrixParams.Parse(br, dctSelect, bitDepth, streamBase + i, globalTree, globalCode).IntoMatrix();
            set.matrices[i] = mat;

            var (w, h) = JxlDct.DequantMatrixSize(dctSelect);
            var tr = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                var m = mat[c];
                var outp = new float[m.Length];
                for (int idx = 0; idx < m.Length; idx++)
                {
                    int mx = idx % h;
                    int my = idx / h;
                    outp[idx] = m[(mx * w) + my];
                }

                tr[c] = outp;
            }

            set.matricesTr[i] = tr;
        }

        return set;
    }
}
