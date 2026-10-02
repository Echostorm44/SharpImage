using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    /// <summary>cpi->enc_quant_dequant_params (av1_init_quantizer's tables for every qindex).</summary>
    public AomQuants Quants = null!;
}

internal sealed partial class AomMbdPlane
{
    /// <summary>The quantization matrix level of xd->plane[p].seg_qmatrix / seg_iqmatrix (set_qmatrix; 15 = flat).</summary>
    public int QmLevel = 15;
}

// Port of libaom 3.14.1 av1/encoder/av1_quantize.c's av1_set_quantizer / av1_init_plane_quantizers / set_q_index /
// set_qmatrix, encodeframe.c's setup_delta_q with allintra_vis.c's av1_get_sbq_variance_boost and aq_variance.c's
// av1_get_variance_boost_block_variance (DELTA_Q_VARIANCE_BOOST), rd.c's av1_adjust_q_from_delta_q_res.
internal static class AomQuantSetup
{
    private const int VarBoostMaxDeltaqRange = 80;   // VAR_BOOST_MAX_DELTAQ_RANGE
    private const double VarBoostMaxBoost = 8.0;     // VAR_BOOST_MAX_BOOST

    /// <summary>av1_set_quantizer (ALLINTRA, 8-bit, no HDR delta q): the chroma delta q values and the QM levels.</summary>
    internal static void SetQuantizer(AomComp cpi, int q)
    {
        var cm = cpi.Cm;
        cm.YDcDeltaQ = 0;
        if (cpi.EnableChromaDeltaq && q != 0)
        {
            if (cpi.TuneIq)
            {
                int chromaDc = 0, chromaAc = 0;
                if (cm.SsX == 1 && cm.SsY == 1)
                {
                    chromaDc = -Math.Clamp(cm.BaseQindex / 2 - 14, 0, 16);
                    chromaAc = chromaDc;
                }
                else if (cm.SsX == 1 && cm.SsY == 0)
                {
                    chromaDc = 0;
                    chromaAc = Math.Clamp(cm.BaseQindex / 2, 0, 6);
                }
                else if (cm.SsX == 0 && cm.SsY == 0)
                {
                    chromaDc = 0;
                    chromaAc = Math.Clamp(cm.BaseQindex / 2, 0, 24);
                }
                cm.UDcDeltaQ = chromaDc; cm.UAcDeltaQ = chromaAc;
                cm.VDcDeltaQ = chromaDc; cm.VAcDeltaQ = chromaAc;
            }
            else
            {
                cm.UDcDeltaQ = cm.UAcDeltaQ = cm.VDcDeltaQ = cm.VAcDeltaQ = 2;
            }
        }
        else
        {
            cm.UDcDeltaQ = cm.UAcDeltaQ = cm.VDcDeltaQ = cm.VAcDeltaQ = 0;
        }

        // the QM formulas: tune IQ uses aom_get_qmlevel_allintra for luma and aom_get_qmlevel_444_chroma for 4:4:4 chroma
        Func<int, int, int, int> luma = cpi.TuneIq || cpi.AllIntra ? QmLevelAllintra : QmLevel;
        Func<int, int, int, int> chroma = cpi.TuneIq && cm.SsX == 0 && cm.SsY == 0 ? QmLevel444Chroma : cpi.TuneIq || cpi.AllIntra ? QmLevelAllintra : QmLevel;
        cm.QmLevelY = luma(cm.BaseQindex, cpi.QmMinLevel, cpi.QmMaxLevel);
        cm.QmLevelU = chroma(cm.BaseQindex + cm.UAcDeltaQ, cpi.QmMinLevel, cpi.QmMaxLevel);
        cm.QmLevelV = cm.QmLevelU;   // separate_uv_delta_q is 0
        // encode_strategy.c: cm->quant_params.using_qmatrix = oxcf->q_cfg.using_qm
        cm.UsingQmatrix = cpi.UsingQm;
    }

    /// <summary>aom_get_qmlevel.</summary>
    internal static int QmLevel(int qindex, int first, int last) => first + (qindex * (last + 1 - first)) / 256;

    /// <summary>aom_get_qmlevel_allintra.</summary>
    internal static int QmLevelAllintra(int qindex, int first, int last)
    {
        int l = qindex <= 40 ? 10 : qindex <= 100 ? 9 : qindex <= 160 ? 8 : qindex <= 200 ? 7 : qindex <= 220 ? 6 : qindex <= 240 ? 5 : 4;
        return Math.Clamp(l, first, last);
    }

    /// <summary>aom_get_qmlevel_444_chroma.</summary>
    internal static int QmLevel444Chroma(int qindex, int first, int last)
    {
        int l = qindex <= 12 ? 10 : qindex <= 24 ? 9 : qindex <= 32 ? 8 : qindex <= 36 ? 7 : qindex <= 44 ? 6 : qindex <= 48 ? 5
            : qindex <= 56 ? 4 : qindex <= 88 ? 3 : 2;
        return Math.Clamp(l, first, last);
    }

    /// <summary>set_q_index: the quantizer / dequantizer values of qindex into x->plane[].</summary>
    internal static void SetQIndex(AomComp cpi, AomMacroblock x, int q)
    {
        var quants = cpi.Quants;
        x.Qindex = q;
        for (int p = 0; p < 3; p++)
        {
            var mp = x.Plane[p];
            mp.QuantFp0 = quants.QuantFp[p, q, 0]; mp.QuantFp1 = quants.QuantFp[p, q, 1];
            mp.RoundFp0 = quants.RoundFp[p, q, 0]; mp.RoundFp1 = quants.RoundFp[p, q, 1];
            mp.Quant0 = quants.Quant[p, q, 0]; mp.Quant1 = quants.Quant[p, q, 1];
            mp.QuantShift0 = quants.QuantShift[p, q, 0]; mp.QuantShift1 = quants.QuantShift[p, q, 1];
            mp.Zbin0 = quants.Zbin[p, q, 0]; mp.Zbin1 = quants.Zbin[p, q, 1];
            mp.Round0 = quants.Round[p, q, 0]; mp.Round1 = quants.Round[p, q, 1];
            mp.Dequant0 = quants.Dequant[p, q, 0]; mp.Dequant1 = quants.Dequant[p, q, 1];
        }
        SetQmatrix(cpi, x);
    }

    /// <summary>set_qmatrix (segment 0): the planes' matrix levels, flat (15) for a lossless segment or without QMs.</summary>
    internal static void SetQmatrix(AomComp cpi, AomMacroblock x)
    {
        var cm = cpi.Cm;
        bool useQmatrix = cm.UsingQmatrix && x.E.Lossless[0] == 0;   // av1_use_qmatrix
        x.E.Plane[0].QmLevel = useQmatrix ? cm.QmLevelY : 15;
        x.E.Plane[1].QmLevel = useQmatrix ? cm.QmLevelU : 15;
        x.E.Plane[2].QmLevel = useQmatrix ? cm.QmLevelV : 15;
    }

    /// <summary>av1_init_plane_quantizers (segment 0, no sb_qp_sweep).</summary>
    internal static void InitPlaneQuantizers(AomComp cpi, AomMacroblock x, bool doUpdate)
    {
        var cm = cpi.Cm;
        int currentQindex = Math.Clamp(cpi.DeltaQPresentFlag ? cm.BaseQindex + x.DeltaQindex : cm.BaseQindex, 0, 255);
        int qindex = currentQindex;   // av1_get_qindex (segmentation off)
        int qindexRd = qindex;
        int rdmult = AomRd.RdMultKeyFrame(qindexRd + cm.YDcDeltaQ, cm.BitDepth, cpi.TuneIq);
        if (x.Qindex != qindex || doUpdate) SetQIndex(cpi, x, qindex);
        else SetQmatrix(cpi, x);   // (segment unchanged; av1_use_qmatrix re-applies set_qmatrix)
        x.Errorperbit = AomRd.ErrorPerBit(rdmult);
        x.SadPerBit = AomEncodeFrame.SadPerBit(qindexRd, cm.BitDepth);
    }

    /// <summary>setup_delta_q (DELTA_Q_VARIANCE_BOOST, no delta lf): the superblock's qindex.</summary>
    internal static void SetupDeltaQ(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        int sbSize = cm.SbSize;
        AomEncodeFrame.SetupSrcPlanes(cpi, x, miRow, miCol, cm.NumPlanes, sbSize);
        int deltaQRes = cpi.DeltaQRes;
        int currentQindex = cm.BaseQindex;
        if (cpi.DeltaqVarianceBoost) currentQindex = GetSbqVarianceBoost(cpi, x);
        x.RdmultCurQindex = currentQindex;
        var xd = x.E;
        currentQindex = AdjustQFromDeltaQRes(deltaQRes, xd.CurrentBaseQindex, currentQindex);
        x.DeltaQindex = currentQindex - cm.BaseQindex;
        x.RdmultDeltaQindex = x.DeltaQindex;
        AomEncodeFrame.SetOffsets(cpi, x, miRow, miCol, sbSize);
        xd.Mi0.CurrentQindex = currentQindex;
        InitPlaneQuantizers(cpi, x, false);
        if (x.DeltaQindex != 0) cpi.DeltaqUsed = true;   // (td->deltaq_used, OR-ed over the threads)
    }

    /// <summary>setup_delta_q_nonrd (DELTA_Q_VARIANCE_BOOST).</summary>
    internal static void SetupDeltaQNonrd(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        int sbSize = cm.SbSize;
        AomEncodeFrame.SetupSrcPlanes(cpi, x, miRow, miCol, cm.NumPlanes, sbSize);
        int currentQindex = cm.BaseQindex;
        if (cpi.DeltaqVarianceBoost) currentQindex = GetSbqVarianceBoost(cpi, x);
        x.RdmultCurQindex = currentQindex;
        var xd = x.E;
        currentQindex = AdjustQFromDeltaQRes(cpi.DeltaQRes, xd.CurrentBaseQindex, currentQindex);
        x.DeltaQindex = currentQindex - cm.BaseQindex;
        x.RdmultDeltaQindex = x.DeltaQindex;
        AomEncodeFrame.SetOffsets(cpi, x, miRow, miCol, sbSize);
        xd.Mi0.CurrentQindex = currentQindex;
        InitPlaneQuantizers(cpi, x, false);
        cpi.DeltaqUsed |= x.DeltaQindex != 0;
    }

    /// <summary>av1_adjust_q_from_delta_q_res.</summary>
    internal static int AdjustQFromDeltaQRes(int deltaQRes, int prevQindex, int currQindex)
    {
        currQindex = Math.Clamp(currQindex, deltaQRes, 256 - deltaQRes);
        int sign = currQindex - prevQindex >= 0 ? 1 : -1;
        int deadzone = deltaQRes / 4;
        int qmask = ~(deltaQRes - 1);
        int abs = AbsI(currQindex - prevQindex);
        abs = (abs + deadzone) & qmask;
        int adjust = prevQindex + sign * abs;
        return Math.Max(adjust, 1);   // MINQ + 1
    }

    /// <summary>av1_get_sbq_variance_boost.</summary>
    internal static int GetSbqVarianceBoost(AomComp cpi, AomMacroblock x)
    {
        int baseQindex = cpi.Cm.BaseQindex;
        uint variance = GetVarianceBoostBlockVariance(x);
        int bd = cpi.Cm.BitDepth;
        double strength = (cpi.DeltaqStrength / 100.0) * 3.0;
        strength = Math.Clamp(strength, 0.0, 6.0);   // fclamp
        if (variance == 0) variance = 1;
        double qstepRatio = 0.15 * strength * (-Math.Log2((double)variance) + 10.0) + 1.0;
        qstepRatio = Math.Clamp(qstepRatio, 1.0, VarBoostMaxBoost);
        double baseQ = QindexToQ(baseQindex, bd);   // av1_convert_qindex_to_q
        double targetQ = baseQ / qstepRatio;
        int targetQindex = ConvertQToQindex(targetQ, bd);
        int boost = (int)Math.Round((baseQindex + 544.0) * (baseQindex - targetQindex) / 1279.0, MidpointRounding.AwayFromZero);
        boost = Math.Min(VarBoostMaxDeltaqRange, boost);
        return Math.Max(baseQindex - boost, 1);   // MINQ + 1
    }

    /// <summary>av1_convert_qindex_to_q: the AC dequantizer / 4 (/ 16 at 10 bits, / 64 at 12).</summary>
    private static double QindexToQ(int qindex, int bd)
        => bd == 8 ? Av1Tables.DequantTable[0, qindex, 1] / 4.0 : bd == 10 ? Av1Tables.DequantTable[1, qindex, 1] / 16.0
            : Av1Tables.DequantTable[2, qindex, 1] / 64.0;

    /// <summary>av1_convert_q_to_qindex: the first qindex whose q matches or exceeds q.</summary>
    private static int ConvertQToQindex(double q, int bd)
    {
        int qindex = 0;
        while (qindex < 255 && QindexToQ(qindex, bd) < q) qindex++;
        return qindex;
    }

    /// <summary>av1_get_variance_boost_block_variance: the 1:2:1 weighted 4th / 5th / 6th octile of the superblock's
    /// 64 8x8 luma variances (each truncated per pixel).</summary>
    internal static uint GetVarianceBoostBlockVariance(AomMacroblock x)
    {
        Span<uint> variances = stackalloc uint[64];
        var src = x.Plane[0].Src;
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
                variances[i * 8 + j] = (src.Buf16 != null
                    ? AomHbd.Variance(src.Buf16, src.Offset + i * 8 * src.Stride + j * 8, src.Stride, null, 0, 0, 0, 8, 8, x.E.Bd, out _)
                    : AomIntraModeSearch.VarianceVsZero(src.Buf, src.Offset + i * 8 * src.Stride + j * 8, src.Stride, 8, 8, out _)) / 64;
        variances.Sort();
        const int octile = 5, inOctile = 8;
        int middle = octile * inOctile - 1;
        int lower = Math.Max(inOctile - 1, middle - inOctile);
        int upper = Math.Min(64 - 1, middle + inOctile);
        return (variances[lower] + variances[middle] * 2 + variances[upper] + 2) / 4;
    }
}
