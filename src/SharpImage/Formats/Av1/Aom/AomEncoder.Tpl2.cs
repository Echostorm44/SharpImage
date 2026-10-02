using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMacroblock
{
    /// <summary>x->sb_enc TPL data (av1_get_tpl_stats_sb): tpl_stride, tpl_inter_cost / tpl_intra_cost and tpl_mv per
    /// 16x16 of the superblock (MAX_TPL_BLK_IN_SB squared).</summary>
    public int TplStride;
    public readonly long[] TplInterCost = new long[64], TplIntraCost = new long[64];
    public readonly AomMv[] TplMv = new AomMv[64 * INTER_REFS_PER_FRAME];
}

// libaom 3.14.1 encodeframe.c init_ref_frame_space / check_to_disable_ref_frame_mvs and encodeframe_utils.c
// av1_get_tpl_stats_sb.
internal static partial class AomEncoder
{
    private static bool IsFrameTplEligibleCpi(AomComp cpi) => cpi.UpdateType == ARF_UPDATE || cpi.UpdateType == GF_UPDATE || cpi.UpdateType == KF_UPDATE;

    /// <summary>init_ref_frame_space (x->tpl_keep_ref_frame).</summary>
    internal static void InitRefFrameSpace(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        Array.Clear(x.TplKeepRefFrame);
        if (!TplStatsReady(cpi)) return;
        if (!IsFrameTplEligibleCpi(cpi)) return;
        if (cpi.UpdateType == OVERLAY_UPDATE)
        {
            Array.Fill(x.TplKeepRefFrame, true);
            return;
        }
        var tf = cpi.Tpl!.Frame(cpi.GfFrameIndex);
        var interCost = new long[INTER_REFS_PER_FRAME];
        int miRowEnd = Math.Min(MiSizeHigh[cm.SbSize] + miRow, cm.MiRows);
        int miColEnd = Math.Min(miCol + MiSizeWide[cm.SbSize], cm.MiCols);
        var tplPredError = new long[INTER_REFS_PER_FRAME];
        for (int row = miRow; row < miRowEnd; row += 4)
            for (int col = miCol; col < miColEnd; col += 4)
            {
                var s = tf.Stats![AomTplData.PtrPos(row, col, tf.Stride)];
                Array.Clear(tplPredError);
                long bestInterCost = s.PredError[0];
                int bestRfIdx = 0;
                for (int idx = 1; idx < INTER_REFS_PER_FRAME; ++idx)
                    if (s.PredError[idx] < bestInterCost && s.PredError[idx] != 0)
                    {
                        bestInterCost = s.PredError[idx];
                        bestRfIdx = idx;
                    }
                tplPredError[bestRfIdx] = s.PredError[bestRfIdx] - s.PredError[0];
                for (int rfIdx = 1; rfIdx < INTER_REFS_PER_FRAME; ++rfIdx) interCost[rfIdx] += tplPredError[rfIdx];
            }
        var rankIndex = new int[INTER_REFS_PER_FRAME - 1];
        for (int idx = 0; idx < INTER_REFS_PER_FRAME - 1; ++idx)
        {
            rankIndex[idx] = idx + 1;
            for (int i = idx; i > 0; --i)
                if (interCost[rankIndex[i - 1]] > interCost[rankIndex[i]]) (rankIndex[i - 1], rankIndex[i]) = (rankIndex[i], rankIndex[i - 1]);
        }
        x.TplKeepRefFrame[INTRA_FRAME] = true;
        x.TplKeepRefFrame[LAST_FRAME] = true;
        bool cutoffRef = false;
        for (int idx = 0; idx < INTER_REFS_PER_FRAME - 1; ++idx)
        {
            x.TplKeepRefFrame[rankIndex[idx] + LAST_FRAME] = true;
            if (idx > 2)
            {
                if (!cutoffRef)
                    if (Math.Abs(interCost[rankIndex[idx]]) < Math.Abs(interCost[rankIndex[idx - 1]]) / 8 || interCost[rankIndex[idx]] == 0) cutoffRef = true;
                if (cutoffRef) x.TplKeepRefFrame[rankIndex[idx] + LAST_FRAME] = false;
            }
        }
    }

    /// <summary>av1_get_tpl_stats_sb.</summary>
    internal static void GetTplStatsSb(AomComp cpi, AomMacroblock x, int bsize, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        x.TplDataCount = 0;
        if (cpi.Tpl == null) return;
        if (cm.FrameType == KEY_FRAME) return;
        if (cpi.UpdateType == INTNL_OVERLAY_UPDATE || cpi.UpdateType == OVERLAY_UPDATE) return;
        if (!TplStatsReady(cpi)) return;
        int miWide = MiSizeWide[bsize], miHigh = MiSizeHigh[bsize];
        var tf = cpi.Tpl.Frame(cpi.GfFrameIndex);
        int miColEnd = miCol + miWide;
        x.TplStride = (miColEnd - miCol) / 4;
        int miCount = 0, count = 0;
        for (int row = miRow; row < miRow + miHigh; row += 4)
            for (int col = miCol; col < miColEnd; col += 4)
            {
                if (row >= cm.MiRows || col >= cm.MiCols)
                {
                    x.TplInterCost[count] = long.MaxValue;
                    x.TplIntraCost[count] = long.MaxValue;
                    for (int i = 0; i < INTER_REFS_PER_FRAME; ++i) x.TplMv[count * INTER_REFS_PER_FRAME + i] = AomMv.Invalid;
                    count++;
                    continue;
                }
                var s = tf.Stats![AomTplData.PtrPos(row, col, tf.Stride)];
                x.TplInterCost[count] = s.InterCost << AomTplData.TPL_DEP_COST_SCALE_LOG2;
                x.TplIntraCost[count] = s.IntraCost << AomTplData.TPL_DEP_COST_SCALE_LOG2;
                for (int i = 0; i < INTER_REFS_PER_FRAME; ++i) x.TplMv[count * INTER_REFS_PER_FRAME + i] = s.Mv[i];
                miCount++;
                count++;
            }
        x.TplDataCount = miCount;
    }

    /// <summary>check_to_disable_ref_frame_mvs.</summary>
    internal static void CheckToDisableRefFrameMvs(AomComp cpi, AomEncodeInput input)
    {
        var cm = cpi.Cm;
        if (!cm.AllowRefFrameMvs || cpi.Sf.hl_sf.ref_frame_mvs_lvl != 1) return;
        if (!TplStatsReady(cpi)) return;
        int tplSubpel = cpi.Sf.tpl_sf.subpel_force_stop;
        bool allowHp = tplSubpel == AomSubpel.EIGHTH_PEL && cm.AllowHighPrecisionMv;
        bool forceInt = tplSubpel == AomSubpel.FULL_PEL || cm.CurFrameForceIntegerMv;
        var tf = cpi.Tpl!.Frame(cpi.GfFrameIndex);
        ulong accumSpatial = 0, accumBest = 0;
        for (int miRow = 0; miRow < tf.MiRows; miRow += 4)
            for (int miCol = 0; miCol < tf.MiCols; miCol += 4)
            {
                var s = tf.Stats![AomTplData.PtrPos(miRow, miCol, tf.Stride)];
                int curBestRefIdx = s.RefFrameIndex[0];
                if (curBestRefIdx == NONE_FRAME) continue;
                var curMv = AomInter.LowerMvPrecision(s.Mv[curBestRefIdx], allowHp, forceInt);
                int spatial = SpatialMvpredErr(cpi, tf, miRow, miCol, curBestRefIdx, curMv, allowHp, forceInt);
                int temporal = TemporalMvpredErr(cm, input, miRow, miCol, tf.MiRows, tf.MiCols, curBestRefIdx, curMv, allowHp, forceInt);
                int best = Math.Min(spatial, temporal);
                accumSpatial += (ulong)(long)spatial;
                accumBest += (ulong)(long)best;
            }
        float threshold = ThreshBasedOnQ(cm.BaseQindex, cpi.Speed);
        float mvErrReduction = (float)(accumSpatial - accumBest);
        if (mvErrReduction <= threshold * accumSpatial) cm.AllowRefFrameMvs = false;
    }

    private static float ThreshBasedOnQ(int qindex, int speed)
    {
        ReadOnlySpan<float> minArr = stackalloc float[] { 0.084f, 0.087f, 0.126f };
        ReadOnlySpan<float> maxArr = stackalloc float[] { 0.140f, 0.150f, 0.182f };
        int idx = speed >= 3 ? 2 : speed - 1;
        float mn = minArr[idx], mx = maxArr[idx];
        return mn + (mx - mn) * ((float)255 - (float)qindex) / (float)255;
    }

    private static void CheckMvErrAndUpdate(AomMv cur, AomMv refMv, ref int mvErr)
    {
        int err = Math.Abs(cur.Row - refMv.Row) + Math.Abs(cur.Col - refMv.Col);
        if (err < mvErr) mvErr = err;
    }

    private static bool IsInsideFrameBorder(int miRow, int miCol, int rowOff, int colOff, int numMiRows, int numMiCols)
        => miRow + rowOff >= 0 && miRow + rowOff < numMiRows && miCol + colOff >= 0 && miCol + colOff < numMiCols;

    private static int SpatialMvpredErr(AomComp cpi, AomTplDepFrame tf, int miRow, int miCol, int refIdx, AomMv curMv, bool allowHp, bool isInteger)
    {
        var cm = cpi.Cm;
        int mvErr = int.MaxValue;
        const int step = 4;
        ReadOnlySpan<int> pos = stackalloc int[] { -step, 0, 0, -step, -step, step, -step, -step, -2 * step, 0, 0, -2 * step, -3 * step, 0, 0, -3 * step };
        for (int i = 0; i < 8; i++)
        {
            int ro = pos[2 * i], co = pos[2 * i + 1];
            if (!IsInsideFrameBorder(miRow, miCol, ro, co, tf.MiRows, tf.MiCols)) continue;
            var s = tf.Stats![AomTplData.PtrPos(miRow + ro, miCol + co, tf.Stride)];
            var r = AomInter.LowerMvPrecision(s.Mv[refIdx], allowHp, isInteger);
            CheckMvErrAndUpdate(curMv, r, ref mvErr);
        }
        var gmMv = default(AomMv);
        if (cm.GlobalMotion[refIdx + LAST_FRAME].WmType > TRANSLATION)
            gmMv = AomInter.GmGetMotionVector(cm.GlobalMotion[refIdx + LAST_FRAME], allowHp, BLOCK_16X16, miCol, miRow, isInteger);
        CheckMvErrAndUpdate(curMv, gmMv, ref mvErr);
        return mvErr;
    }

    private static int TemporalMvpredErr(AomCommon cm, AomEncodeInput input, int miRow, int miCol, int numMiRows, int numMiCols, int refIdx, AomMv curMv,
        bool allowHp, bool isInteger)
    {
        var refBuf = cm.RefBufs[refIdx + LAST_FRAME];
        if (refBuf == null) return int.MaxValue;
        int refOrderHint = refBuf.OrderHint;
        int curToRefDist = cm.RelativeDist(cm.OrderHint, refOrderHint);
        int mvErr = int.MaxValue;
        ReadOnlySpan<int> pos = stackalloc int[] { 0, 0, 0, 2, 2, 0, 2, 2, 4, -2, 4, 4, 2, 4 };
        for (int i = 0; i < 7; i++)
        {
            int ro = pos[2 * i], co = pos[2 * i + 1];
            if (!IsInsideFrameBorder(miRow, miCol, ro, co, numMiRows, numMiCols)) continue;
            var t = cm.TplMvs![((miRow + ro) >> 1) * (cm.MiStride >> 1) + ((miCol + co) >> 1)];
            if (t.Mfmv0.AsInt == AomMv.Invalid.AsInt) continue;
            var r = AomInter.LowerMvPrecision(AomMvPred.GetMvProjection(t.Mfmv0, curToRefDist, t.RefFrameOffset), allowHp, isInteger);
            CheckMvErrAndUpdate(curMv, r, ref mvErr);
        }
        return mvErr;
    }
}
