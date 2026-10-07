using System;
using static SharpImage.Formats.Av1.AomTables;
using SharpImage.Core;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomEncodeInput
{
    /// <summary>ppi->tpl_data (null: no TPL model), cpi->rd.r0, oxcf deltaq_mode == DELTA_Q_OBJECTIVE with the TPL model.</summary>
    public AomTplData? Tpl;
    public double R0;
    public bool DeltaqObjective;
}

internal sealed partial class AomComp
{
    public AomTplData? Tpl;
    public int GfFrameIndex;
    public double R0;
    public bool DeltaqObjective;
}

internal sealed partial class AomMacroblock
{
    /// <summary>x->rb (the superblock's TPL ratio set by av1_get_q_for_deltaq_objective).</summary>
    public double Rb;
}

// Port of libaom 3.14.1's encoder-side TPL consumers: av1_get_q_for_deltaq_objective / allow_deltaq_mode
// (encodeframe_utils.c, encodeframe.c) and av1_get_cb_rdmult.
internal static partial class AomEncoder
{
    private const int DEFAULT_DELTA_Q_RES_OBJECTIVE = 4;

    private static bool TplStatsReady(AomComp cpi) => cpi.Tpl != null && cpi.Tpl.StatsReady(cpi.GfFrameIndex);

    /// <summary>av1_get_q_for_deltaq_objective.</summary>
    internal static int GetQForDeltaqObjective(AomComp cpi, AomMacroblock x, ref long deltaDist, bool wantDelta, int bsize, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        int baseQindex = cm.BaseQindex;
        var tpl = cpi.Tpl;
        if (tpl == null || cpi.GfFrameIndex >= AomTplData.MAX_TPL_FRAME_IDX) return baseQindex;
        var tf = tpl.Frame(cpi.GfFrameIndex);
        if (!tf.IsValid) return baseQindex;
        double intraCost = 0, mcDepReg = 0, mcDepCost = 0, cbcmpBase = 1, srcrfDist = 0, srcrfSse = 0, srcrfRate = 0;
        int miWide = MiSizeWide[bsize], miHigh = MiSizeHigh[bsize];
        for (int row = miRow; row < miRow + miHigh; row += 4)
            for (int col = miCol; col < miCol + miWide; col += 4)
            {
                if (row >= cm.MiRows || col >= cm.MiCols) continue;
                var s = tf.Stats![AomTplData.PtrPos(row, col, tf.Stride)];
                double cbcmp = s.SrcrfDist;
                long mcDepDelta = AomRd.RdCost(tf.BaseRdmult, s.McDepRate, s.McDepDist);
                double distScaled = s.RecrfDist << 7;
                intraCost += PortableMathD.Log(distScaled) * cbcmp;
                mcDepCost += PortableMathD.Log(distScaled + mcDepDelta) * cbcmp;
                mcDepReg += PortableMathD.Log(3 * distScaled + mcDepDelta) * cbcmp;
                srcrfDist += s.SrcrfDist << 7;
                srcrfSse += s.SrcrfSse << 7;
                srcrfRate += s.SrcrfRate << AomTplData.TPL_DEP_COST_SCALE_LOG2;
                cbcmpBase += cbcmp;
            }
        double beta, rk;
        if (mcDepCost > 0 && intraCost > 0)
        {
            rk = PortableMathD.Exp((intraCost - mcDepCost) / cbcmpBase);
            x.Rb = PortableMathD.Exp((intraCost - mcDepReg) / cbcmpBase);
            beta = cpi.R0 / rk;
        }
        else return baseQindex;
        int offset = GetDeltaqOffset(cm.BitDepth, baseQindex, beta);
        int dqRes = cpi.DeltaQRes;
        offset = Math.Min(offset, dqRes * 9 - 1);
        offset = Math.Max(offset, -dqRes * 9 + 1);
        int qindex = cm.BaseQindex + offset;
        qindex = Math.Clamp(qindex, 0, 255);
        int frmQstep = AomComp.DcQuantQtx(baseQindex, 0, cm.BitDepth);
        int sbsQstep = AomComp.DcQuantQtx(baseQindex, offset, cm.BitDepth);
        if (wantDelta)
        {
            double sbsDist = srcrfDist * PortableMathD.Pow((double)sbsQstep / frmQstep, 2.0);
            double sbsRate = srcrfRate * ((double)frmQstep / sbsQstep);
            sbsDist = Math.Min(sbsDist, srcrfSse);
            deltaDist = (long)((sbsDist - srcrfDist) / rk);
            deltaDist += AomRd.RdCost(tf.BaseRdmult, 4 * 256, 0);
            deltaDist += AomRd.RdCost(tf.BaseRdmult, (long)(sbsRate - srcrfRate), 0);
        }
        return qindex;
    }

    /// <summary>av1_get_deltaq_offset.</summary>
    private static int GetDeltaqOffset(int bd, int qindex, double beta)
    {
        int q = AomComp.DcQuantQtx(qindex, 0, bd);
        int newq = (int)Math.Round(q / Math.Sqrt(beta), MidpointRounding.ToEven);   // rint
        int origQindex = qindex;
        if (newq == q) return 0;
        if (newq < q)
        {
            while (qindex > 0)
            {
                qindex--;
                q = AomComp.DcQuantQtx(qindex, 0, bd);
                if (newq >= q) break;
            }
        }
        else
        {
            while (qindex < 255)
            {
                qindex++;
                q = AomComp.DcQuantQtx(qindex, 0, bd);
                if (newq <= q) break;
            }
        }
        return qindex - origQindex;
    }

    /// <summary>allow_deltaq_mode (on cpi->td.mb: its rb is left by the last superblock).</summary>
    private static bool AllowDeltaqMode(AomComp cpi, AomMacroblock x)
    {
        var cm = cpi.Cm;
        int sbsWide = MiSizeWide[cm.SbSize], sbsHigh = MiSizeHigh[cm.SbSize];
        long deltaRdcost = 0;
        for (int miRow = 0; miRow < cm.MiRows; miRow += sbsHigh)
            for (int miCol = 0; miCol < cm.MiCols; miCol += sbsWide)
            {
                long thisDelta = 0;
                GetQForDeltaqObjective(cpi, x, ref thisDelta, true, cm.SbSize, miRow, miCol);
                deltaRdcost += thisDelta;
            }
        return deltaRdcost < 0;
    }

    /// <summary>av1_get_cb_rdmult.</summary>
    internal static int GetCbRdmult(AomComp cpi, AomMacroblock x, int bsize, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        int deltaqRdmult = cpi.SetRdmultDeltaQ(x);
        if (!TplStatsReady(cpi)) return deltaqRdmult;
        if (x.Rb == 0) return deltaqRdmult;
        var tf = cpi.Tpl!.Frame(cpi.GfFrameIndex);
        int miWide = MiSizeWide[bsize], miHigh = MiSizeHigh[bsize];
        double intraCostBase = 0, mcDepCostBase = 0, cbcmpBase = 0;
        for (int row = miRow; row < miRow + miHigh; row += 4)
            for (int col = miCol; col < miCol + miWide; col += 4)
            {
                if (row >= cm.MiRows || col >= cm.MiCols) continue;
                var s = tf.Stats![AomTplData.PtrPos(row, col, tf.Stride)];
                double cbcmp = s.SrcrfDist;
                long mcDepDelta = AomRd.RdCost(tf.BaseRdmult, s.McDepRate, s.McDepDist);
                double distScaled = s.RecrfDist << 7;
                intraCostBase += PortableMathD.Log(distScaled) * cbcmp;
                mcDepCostBase += PortableMathD.Log(3 * distScaled + mcDepDelta) * cbcmp;
                cbcmpBase += cbcmp;
            }
        if (cbcmpBase == 0) return deltaqRdmult;
        double rk = PortableMathD.Exp((intraCostBase - mcDepCostBase) / cbcmpBase);
        deltaqRdmult = (int)(deltaqRdmult * (rk / x.Rb));
        return Math.Max(deltaqRdmult, 1);
    }
}
