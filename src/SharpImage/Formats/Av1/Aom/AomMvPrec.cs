using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>MV_STATS (encoder.h): the previous frame's motion vector statistics get_smart_mv_prec reads.</summary>
internal sealed class AomMvStats
{
    public int Q, Order, InterCount, IntraCount, DefaultMvs, LastBitZero, LastBitNonzero, TotalMvRate, HpTotalMvRate, LpTotalMvRate,
        HorzText, VertText, DiagText;
    public readonly int[] MvJointCount = new int[4];
    public bool Valid;

    /// <summary>av1_zero(cpi->mv_stats).</summary>
    public void Clear()
    {
        Q = Order = InterCount = IntraCount = DefaultMvs = LastBitZero = LastBitNonzero = TotalMvRate = HpTotalMvRate = LpTotalMvRate = 0;
        HorzText = VertText = DiagText = 0;
        Array.Clear(MvJointCount);
        Valid = false;
    }
}

internal sealed partial class AomEncodeInput
{
    /// <summary>cpi->mv_stats (copied from ppi->mv_stats at the start of the frame).</summary>
    public AomMvStats? MvStats;
}

// Port of libaom 3.14.1 av1/encoder/mv_prec.c: av1_collect_mv_stats, get_smart_mv_prec, av1_frame_allows_smart_mv.
internal static class AomMvPrec
{
    private const int EC_MIN_PROB = 4;

    /// <summary>av1_frame_allows_smart_mv.</summary>
    internal static bool FrameAllowsSmartMv(AomComp cpi)
        => !cpi.Cm.FrameIsIntraOnly && !(cpi.UpdateType == INTNL_OVERLAY_UPDATE || cpi.UpdateType == OVERLAY_UPDATE);

    /// <summary>get_symbol_cost (the C# CDFs hold nsymbs - 1 probabilities and the counter: no terminal entry).</summary>
    private static int GetSymbolCost(ushort[] cdf, int symbol, int nsymbs)
    {
        int curCdf = symbol == nsymbs - 1 ? 32768 : 32768 - cdf[symbol];
        int prevCdf = symbol != 0 ? 32768 - cdf[symbol - 1] : 0;
        int p15 = Math.Max(curCdf - prevCdf, EC_MIN_PROB);
        return AomCost.CostSymbol(p15);
    }

    /// <summary>keep_one_comp_stat.</summary>
    private static int KeepOneCompStat(AomMvStats s, int compVal, Av1CdfMvComponent comp, bool useHp, Span<int> rates)
    {
        int sign = compVal < 0 ? 1 : 0;
        int mag = sign != 0 ? -compVal : compVal;
        int magMinus1 = mag - 1;
        int mvClass = AomMvCost.GetMvClass(magMinus1, out int offset);
        int intPart = offset >> 3, fracPart = (offset >> 1) & 3, highPart = offset & 1;
        int rIdx = 0;
        ushort[] fracPartCdf = mvClass != 0 ? comp.ClassNFp : comp.Class0Fp[intPart];
        ushort[] highPartCdf = mvClass != 0 ? comp.ClassNHp : comp.Class0Hp;

        int signRate = GetSymbolCost(comp.Sign, sign, 2);
        rates[rIdx++] = signRate;
        AomCdf.Update(comp.Sign, sign, 2);

        int classRate = GetSymbolCost(comp.Classes, mvClass, AomMvCost.MvClasses);
        rates[rIdx++] = classRate;
        AomCdf.Update(comp.Classes, mvClass, AomMvCost.MvClasses);

        int intBitRate = 0;
        if (mvClass == 0)
        {
            intBitRate = GetSymbolCost(comp.Class0, intPart, AomMvCost.Class0Size);
            AomCdf.Update(comp.Class0, intPart, AomMvCost.Class0Size);
        }
        else
        {
            int n = mvClass + AomMvCost.Class0Bits - 1;
            for (int i = 0; i < n; ++i)
            {
                intBitRate += GetSymbolCost(comp.ClassN[i], (intPart >> i) & 1, 2);
                AomCdf.Update(comp.ClassN[i], (intPart >> i) & 1, 2);
            }
        }
        rates[rIdx++] = intBitRate;
        int fracPartRate = GetSymbolCost(fracPartCdf, fracPart, AomMvCost.MvFpSize);
        rates[rIdx++] = fracPartRate;
        AomCdf.Update(fracPartCdf, fracPart, AomMvCost.MvFpSize);
        int highPartRate = useHp ? GetSymbolCost(highPartCdf, highPart, 2) : 0;
        if (useHp) AomCdf.Update(highPartCdf, highPart, 2);
        rates[rIdx++] = highPartRate;

        s.LastBitZero += highPart == 0 ? 1 : 0;
        s.LastBitNonzero += highPart;
        return signRate + classRate + intBitRate + fracPartRate + highPartRate;
    }

    /// <summary>keep_one_mv_stat.</summary>
    private static void KeepOneMvStat(AomMvStats s, AomMv refMv, AomMv curMv, Av1CdfMvContext nmvc, bool useHp)
    {
        int diffRow = curMv.Row - refMv.Row, diffCol = curMv.Col - refMv.Col;
        var diff = new AomMv(diffRow, diffCol);
        int mvJoint = AomMvCost.GetMvJoint(diff);
        int hpMvJoint = mvJoint;
        var lpDiff = useHp ? new AomMv(diffRow / 2 * 2, diffCol / 2 * 2) : diff;
        int lpMvJoint = AomMvCost.GetMvJoint(lpDiff);
        int mvJointRate = GetSymbolCost(nmvc.Joint, mvJoint, AomMvCost.MvJoints);
        int hpMvJointRate = GetSymbolCost(nmvc.Joint, hpMvJoint, AomMvCost.MvJoints);
        int lpMvJointRate = GetSymbolCost(nmvc.Joint, lpMvJoint, AomMvCost.MvJoints);
        AomCdf.Update(nmvc.Joint, mvJoint, AomMvCost.MvJoints);
        s.TotalMvRate += mvJointRate;
        s.HpTotalMvRate += hpMvJointRate;
        s.LpTotalMvRate += lpMvJointRate;
        s.MvJointCount[mvJoint]++;
        Span<int> rates = stackalloc int[5];
        for (int compIdx = 0; compIdx < 2; compIdx++)
        {
            int compVal = compIdx != 0 ? diffCol : diffRow;
            int hpCompVal = compVal;
            int lpCompVal = compIdx != 0 ? lpDiff.Col : lpDiff.Row;
            rates.Clear();
            int compRate = compVal != 0 ? KeepOneCompStat(s, compVal, compIdx != 0 ? nmvc.Comp1 : nmvc.Comp0, useHp, rates) : 0;
            int hpRate = hpCompVal != 0 ? rates[0] + rates[1] + rates[2] + rates[3] + rates[4] : 0;
            int lpRate = lpCompVal != 0 ? rates[0] + rates[1] + rates[2] + rates[3] : 0;
            s.TotalMvRate += compRate;
            s.HpTotalMvRate += hpRate;
            s.LpTotalMvRate += lpRate;
        }
    }

    /// <summary>get_ref_mv_for_mv_stats.</summary>
    private static AomMv GetRefMvForMvStats(AomMbModeInfo mbmi, AomMbmiExtFrameInter ext, int refIdx)
    {
        int refMvIdx = mbmi.RefMvIdx;
        if (mbmi.Mode == NEAR_NEWMV || mbmi.Mode == NEW_NEARMV) refMvIdx += 1;
        if (mbmi.RefFrame1 > INTRA_FRAME) return refIdx != 0 ? ext.RefMvStack[refMvIdx].CompMv : ext.RefMvStack[refMvIdx].ThisMv;
        int refFrameType = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
        return refMvIdx < ext.RefMvCount ? ext.RefMvStack[refMvIdx].ThisMv : ext.GlobalMvs[refFrameType];
    }

    /// <summary>collect_mv_stats_b.</summary>
    private static void CollectMvStatsB(AomMvStats s, AomComp cpi, Av1CdfMvContext nmvc, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return;
        var mbmi = cm.MiGridBase[miRow * cm.MiStride + miCol]!;
        if (!AomEncodeMb.IsInterBlock(mbmi))
        {
            s.IntraCount++;
            return;
        }
        s.InterCount++;
        var ext = cpi.MbmiExtFrameInterBase![miRow * cm.MiStride + miCol]!;
        int mode = mbmi.Mode;
        bool isCompound = mbmi.HasSecondRef;
        bool useHp = cm.AllowHighPrecisionMv;
        if (mode == NEWMV || mode == NEW_NEWMV)
        {
            for (int refIdx = 0; refIdx < 1 + (isCompound ? 1 : 0); ++refIdx)
                KeepOneMvStat(s, GetRefMvForMvStats(mbmi, ext, refIdx), refIdx == 0 ? mbmi.Mv0 : mbmi.Mv1, nmvc, useHp);
        }
        else if (mode == NEAREST_NEWMV || mode == NEAR_NEWMV || mode == NEW_NEARESTMV || mode == NEW_NEARMV)
        {
            s.DefaultMvs += 1;
            int refIdx = mode == NEAREST_NEWMV || mode == NEAR_NEWMV ? 1 : 0;
            KeepOneMvStat(s, GetRefMvForMvStats(mbmi, ext, refIdx), refIdx == 0 ? mbmi.Mv0 : mbmi.Mv1, nmvc, useHp);
        }
        else s.DefaultMvs += 1 + (isCompound ? 1 : 0);

        // texture information
        int bsize = mbmi.Bsize;
        int numRows = BlockSizeHigh[bsize], numCols = BlockSizeWide[bsize];
        var src = cpi.Source!;
        int stride = src.Strides[0];
        int pxRow = 4 * miRow, pxCol = 4 * miCol;
        int bd = cm.BitDepth;
        if (src.Hbd)
        {
            var b = src.Buffers16[0];
            int o = src.Offsets[0] + pxRow * stride + pxCol;
            for (int row = 0; row < numRows - 1; row++)
                for (int col = 0; col < numCols - 1; col++)
                {
                    int off = o + row * stride + col;
                    int horzDiff = Math.Abs(b[off + 1] - b[off]) >> (bd - 8);
                    int vertDiff = Math.Abs(b[off + stride] - b[off]) >> (bd - 8);
                    s.HorzText += horzDiff;
                    s.VertText += vertDiff;
                    s.DiagText += horzDiff * vertDiff;
                }
        }
        else
        {
            var b = src.Buffers[0];
            int o = src.Offsets[0] + pxRow * stride + pxCol;
            for (int row = 0; row < numRows - 1; row++)
                for (int col = 0; col < numCols - 1; col++)
                {
                    int off = o + row * stride + col;
                    int horzDiff = Math.Abs(b[off + 1] - b[off]);
                    int vertDiff = Math.Abs(b[off + stride] - b[off]);
                    s.HorzText += horzDiff;
                    s.VertText += vertDiff;
                    s.DiagText += horzDiff * vertDiff;
                }
        }
    }

    /// <summary>collect_mv_stats_sb.</summary>
    private static void CollectMvStatsSb(AomMvStats s, AomComp cpi, Av1CdfMvContext nmvc, int miRow, int miCol, int bsize)
    {
        var cm = cpi.Cm;
        if (miRow >= cm.MiRows || miCol >= cm.MiCols) return;
        int partition = AomVarBasedPart.GetPartition(cm, miRow, miCol, bsize);
        int subsize = AomEncodeFrame.PartitionSubsize(bsize, partition);
        int hbs = MiSizeWide[bsize] / 2, qbs = MiSizeWide[bsize] / 4;
        switch (partition)
        {
            case PARTITION_NONE:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                break;
            case PARTITION_HORZ:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow + hbs, miCol);
                break;
            case PARTITION_VERT:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol + hbs);
                break;
            case PARTITION_SPLIT:
                CollectMvStatsSb(s, cpi, nmvc, miRow, miCol, subsize);
                CollectMvStatsSb(s, cpi, nmvc, miRow, miCol + hbs, subsize);
                CollectMvStatsSb(s, cpi, nmvc, miRow + hbs, miCol, subsize);
                CollectMvStatsSb(s, cpi, nmvc, miRow + hbs, miCol + hbs, subsize);
                break;
            case PARTITION_HORZ_A:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol + hbs);
                CollectMvStatsB(s, cpi, nmvc, miRow + hbs, miCol);
                break;
            case PARTITION_HORZ_B:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow + hbs, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow + hbs, miCol + hbs);
                break;
            case PARTITION_VERT_A:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow + hbs, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol + hbs);
                break;
            case PARTITION_VERT_B:
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol);
                CollectMvStatsB(s, cpi, nmvc, miRow, miCol + hbs);
                CollectMvStatsB(s, cpi, nmvc, miRow + hbs, miCol + hbs);
                break;
            case PARTITION_HORZ_4:
                for (int i = 0; i < 4; ++i) CollectMvStatsB(s, cpi, nmvc, miRow + i * qbs, miCol);
                break;
            case PARTITION_VERT_4:
                for (int i = 0; i < 4; ++i) CollectMvStatsB(s, cpi, nmvc, miRow, miCol + i * qbs);
                break;
        }
    }

    /// <summary>av1_collect_mv_stats (each tile's CDFs restart from cm->fc).</summary>
    internal static void CollectMvStats(AomComp cpi, AomMvStats s, int currentQ)
    {
        var cm = cpi.Cm;
        int sbSizeMi = MiSizeWide[cm.SbSize];
        for (int tileRow = 0; tileRow < cm.TileRows; tileRow++)
            for (int tileCol = 0; tileCol < cm.TileCols; tileCol++)
            {
                var tile = cm.TileInit(tileRow, tileCol);
                var tctx = new Av1CdfContext();
                tctx.CopyFrom(cm.Fc!);
                for (int miRow = tile.MiRowStart; miRow < tile.MiRowEnd; miRow += sbSizeMi)
                    for (int miCol = tile.MiColStart; miCol < tile.MiColEnd; miCol += sbSizeMi)
                        CollectMvStatsSb(s, cpi, tctx.Mv, miRow, miCol, cm.SbSize);
            }
        s.Q = currentQ;
        s.Order = cm.OrderHint;
        s.Valid = true;
    }

    /// <summary>get_smart_mv_prec.</summary>
    internal static bool GetSmartMvPrec(AomComp cpi, AomMvStats s, int currentQ)
    {
        var cm = cpi.Cm;
        int orderDiff = cm.OrderHint - s.Order;
        float area = cm.Width * cm.Height;
        Span<float> features = stackalloc float[AomMvPrecNn.FeatureSize];
        features[0] = currentQ;
        features[1] = s.Q;
        features[2] = orderDiff;
        features[3] = s.InterCount / area;
        features[4] = s.IntraCount / area;
        features[5] = s.DefaultMvs / area;
        features[6] = s.MvJointCount[0] / area;
        features[7] = s.MvJointCount[1] / area;
        features[8] = s.MvJointCount[2] / area;
        features[9] = s.MvJointCount[3] / area;
        features[10] = s.LastBitZero / area;
        features[11] = s.LastBitNonzero / area;
        features[12] = s.TotalMvRate / area;
        features[13] = s.HpTotalMvRate / area;
        features[14] = s.LpTotalMvRate / area;
        features[15] = s.HorzText / area;
        features[16] = s.VertText / area;
        features[17] = s.DiagText / area;
        for (int f = 0; f < AomMvPrecNn.FeatureSize; f++) features[f] = (features[f] - AomMvPrecNn.Mean[f]) / AomMvPrecNn.Std[f];
        Span<float> score = stackalloc float[1];
        AomMl.NnPredict(features, AomMvPrecNn.Config, true, score);
        return score[0] >= 0.0f;
    }
}
