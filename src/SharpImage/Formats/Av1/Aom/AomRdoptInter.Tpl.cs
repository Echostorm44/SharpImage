using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 rdopt.c's TPL-based pruning: get_block_level_tpl_stats, prune_modes_based_on_tpl_stats
// (with find_ref_match_in_above_nbs / find_ref_match_in_left_nbs) and calculate_cost_from_tpl_data.
internal static partial class AomRdoptInter
{
    /// <summary>get_block_level_tpl_stats.</summary>
    private static void GetBlockLevelTplStats(AomComp cpi, int bsize, int miRow, int miCol, bool[] validRefs, AomHandleInterModeArgs info)
    {
        var cm = cpi.Cm;
        if (cpi.Tpl == null || !cpi.Tpl.StatsReady(cpi.GfFrameIndex)) return;
        var tf = cpi.Tpl.Frame(cpi.GfFrameIndex);
        int miWide = MiSizeWide[bsize], miHigh = MiSizeHigh[bsize];
        for (int row = miRow; row < Math.Min(miRow + miHigh, cm.MiRows); row += 4)
            for (int col = miCol; col < Math.Min(miCol + miWide, cm.MiCols); col += 4)
            {
                var s = tf.Stats![AomTplData.PtrPos(row, col, tf.Stride)];
                for (int r = 0; r < INTER_REFS_PER_FRAME; r++) info.TplRefInterCost[r] += s.PredError[r];
            }
        long best = long.MaxValue;
        for (int r = 0; r < INTER_REFS_PER_FRAME; r++)
        {
            long cur = info.TplRefInterCost[r];
            if (cur != 0 && cur < best && validRefs[r]) best = cur;
        }
        info.TplBestInterCost = best;
    }

    private static readonly int[,] TplInterModePruneMulFactor = { { 6, 6, 6, 4 }, { 6, 4, 4, 4 }, { 5, 4, 4, 4 } };

    /// <summary>prune_modes_based_on_tpl_stats.</summary>
    private static bool PruneModesBasedOnTplStats(AomHandleInterModeArgs info, int ref0, int ref1, int refMvIdx, int thisMode, int pruneModeLevel)
    {
        bool isRefLast2 = ref0 == LAST2_FRAME || ref1 == LAST2_FRAME;
        if (pruneModeLevel == 1 && !isRefLast2) return false;
        if (pruneModeLevel == 2 && AomInter.HaveNewmvInInterMode(thisMode)) return false;
        long best = info.TplBestInterCost;
        if (best == long.MaxValue) return false;
        long cur = ref1 <= INTRA_FRAME ? info.TplRefInterCost[ref0 - 1] : Math.Max(info.TplRefInterCost[ref0 - 1], info.TplRefInterCost[ref1 - 1]);
        if (isRefLast2) return cur > best;
        bool isGlobalmv = thisMode == GLOBALMV || thisMode == GLOBAL_GLOBALMV;
        int pruneIndex = isGlobalmv ? MAX_REF_MV_SEARCH : refMvIdx;
        int pruneLevel = pruneModeLevel - 2;
        return cur > ((TplInterModePruneMulFactor[pruneLevel, pruneIndex] * best) >> 2);
    }

    private static bool RefMatchFoundInNbBlocks(AomMbModeInfo cur, AomMbModeInfo nb)
    {
        bool match = false;
        int n = cur.HasSecondRef ? 2 : 1;
        for (int i = 0; i < n; i++)
        {
            int rf = i == 0 ? cur.RefFrame0 : cur.RefFrame1;
            if (rf == nb.RefFrame0 || rf == nb.RefFrame1) match = true;
        }
        return match;
    }

    /// <summary>find_ref_match_in_above_nbs.</summary>
    private static bool FindRefMatchInAboveNbs(int totalMiCols, AomMacroblockD xd)
    {
        if (!xd.UpAvailable) return true;
        int miCol = xd.MiCol;
        var cur = xd.Mi0;
        int prevRow = xd.MiOffset - miCol - xd.MiStride;
        int endCol = Math.Min(miCol + xd.Width, totalMiCols);
        for (int aboveMiCol = miCol; aboveMiCol < endCol;)
        {
            var above = xd.MiGrid[prevRow + aboveMiCol]!;
            int step = MiSizeWide[above.Bsize];
            if (above.IsInterBlock && RefMatchFoundInNbBlocks(cur, above)) return true;
            aboveMiCol += step;
        }
        return false;
    }

    /// <summary>find_ref_match_in_left_nbs.</summary>
    private static bool FindRefMatchInLeftNbs(int totalMiRows, AomMacroblockD xd)
    {
        if (!xd.LeftAvailable) return true;
        int miRow = xd.MiRow;
        var cur = xd.Mi0;
        int prevCol = xd.MiOffset - 1 - miRow * xd.MiStride;
        int endRow = Math.Min(miRow + xd.Height, totalMiRows);
        for (int leftMiRow = miRow; leftMiRow < endRow;)
        {
            var left = xd.MiGrid[prevCol + leftMiRow * xd.MiStride]!;
            int step = MiSizeHigh[left.Bsize];
            if (left.IsInterBlock && RefMatchFoundInNbBlocks(cur, left)) return true;
            leftMiRow += step;
        }
        return false;
    }

    /// <summary>calculate_cost_from_tpl_data (adds to the -1 initial costs, as libaom does).</summary>
    private static void CalculateCostFromTplData(AomComp cpi, AomMacroblock x, int bsize, int miRow, int miCol, ref long interCost, ref long intraCost)
    {
        var cm = cpi.Cm;
        int sbSize = cm.SbSize;
        int len = (BlockSizeWide[sbSize] / AomTplData.TplBsize1d) * (BlockSizeHigh[sbSize] / AomTplData.TplBsize1d);
        if (x.TplDataCount != len) return;
        int tplStride = x.TplStride;
        int nw = MiSizeWide[bsize] / 4, nh = MiSizeHigh[bsize] / 4;
        if (nw < 1 || nh < 1) return;
        int ofH = miRow % MiSizeHigh[sbSize], ofW = miCol % MiSizeWide[sbSize];
        int start = ofH / 4 * tplStride + ofW / 4;
        for (int k = 0; k < nh; k++)
            for (int l = 0; l < nw; l++)
            {
                interCost += x.TplInterCost[start + k * tplStride + l];
                intraCost += x.TplIntraCost[start + k * tplStride + l];
            }
        interCost /= nw * nh;
        intraCost /= nw * nh;
    }
}
