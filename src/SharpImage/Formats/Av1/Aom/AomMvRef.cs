using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMacroblockD
{
    // xd->ref_mv_stack[INTRA_FRAME] / xd->weight[INTRA_FRAME]: persist across blocks like libaom's (entries past the
    // count keep earlier blocks' values)
    public readonly AomCandidateMv[] RefMvStack = new AomCandidateMv[AomMbmiExt.MaxRefMvStackSize];
    public readonly ushort[] RefMvWeight = new ushort[AomMbmiExt.MaxRefMvStackSize];
    public bool IsLastVerticalRect, IsFirstHorizontalRect;
}

// Port of libaom 3.14.1 av1/common/mvref_common.{c,h} for the INTRA_FRAME reference (the intrabc DV candidates of a
// key frame: no temporal / global motion, no ref-frame sign bias): av1_find_mv_refs / setup_ref_mv_list with
// scan_row_mbmi / scan_col_mbmi / scan_blk_mbmi / has_top_right / add_ref_mv_candidate, clamp_mv_ref,
// av1_find_best_ref_mvs_from_stack, av1_find_ref_dv and av1_is_dv_valid; plus rdopt_utils.h's
// av1_copy_usable_ref_mv_stack_and_weight.
internal static class AomMvRef
{
    private const int MVREF_ROW_COLS = 3, REF_CAT_LEVEL = 640, MV_BORDER = 16 << 3;
    private const int REFMV_OFFSET = 4, GLOBALMV_OFFSET = 3;
    public const int INTRABC_DELAY_PIXELS = 256, INTRABC_DELAY_SB64 = INTRABC_DELAY_PIXELS / 64;

    /// <summary>is_inter_block.</summary>
    private static bool IsInterBlock(AomMbModeInfo m) => m.UseIntrabc != 0 || m.RefFrame0 > 0;

    /// <summary>add_ref_mv_candidate (single reference rf[0] = INTRA_FRAME; a key frame's global motion is identity).</summary>
    private static void AddRefMvCandidate(AomMbModeInfo candidate, int rf0, ref byte refmvCount, ref byte refMatchCount,
        ref byte newmvCount, AomCandidateMv[] stack, ushort[] weights, int weight)
    {
        if (!IsInterBlock(candidate)) return;
        for (int r = 0; r < 2; ++r)
        {
            int candRef = r == 0 ? candidate.RefFrame0 : candidate.RefFrame1;
            if (candRef != rf0) continue;
            var thisRefmv = r == 0 ? candidate.Mv0 : candidate.Mv1;   // get_block_mv (no global mv block on intra refs)
            int index;
            for (index = 0; index < refmvCount; ++index)
                if (stack[index].ThisMv.AsInt == thisRefmv.AsInt) { weights[index] += (ushort)weight; break; }
            if (index == refmvCount && refmvCount < AomMbmiExt.MaxRefMvStackSize)
            {
                stack[index].ThisMv = thisRefmv;
                weights[index] = (ushort)weight;
                ++refmvCount;
            }
            if (HaveNewmvInInterMode(candidate.Mode)) ++newmvCount;
            ++refMatchCount;
        }
    }

    // have_newmv_in_inter_mode: NEWMV, NEW_NEWMV, NEAREST_NEWMV, NEW_NEARESTMV, NEAR_NEWMV, NEW_NEARMV
    private static bool HaveNewmvInInterMode(int mode) => mode == 16 || mode == 24 || mode == 19 || mode == 20 || mode == 21 || mode == 22;

    private static void ScanRowMbmi(AomCommon cm, AomMacroblockD xd, int miCol, int rf0, int rowOffset, AomCandidateMv[] stack,
        ushort[] weights, ref byte refmvCount, ref byte refMatchCount, ref byte newmvCount, int maxRowOffset, ref int processedRows)
    {
        int endMi = Math.Min(xd.Width, cm.MiCols - miCol);
        endMi = Math.Min(endMi, MiSizeWide[BLOCK_64X64]);
        int width8x8 = MiSizeWide[BLOCK_8X8], width16x16 = MiSizeWide[BLOCK_16X16];
        int colOffset = 0;
        if (Math.Abs(rowOffset) > 1)
        {
            colOffset = 1;
            if ((miCol & 1) != 0 && xd.Width < width8x8) --colOffset;
        }
        bool useStep16 = xd.Width >= 16;
        int baseIdx = xd.MiOffset + rowOffset * xd.MiStride;
        for (int i = 0; i < endMi;)
        {
            var candidate = xd.MiGrid[baseIdx + colOffset + i]!;
            int candidateBsize = candidate.Bsize;
            int n4W = MiSizeWide[candidateBsize];
            int len = Math.Min(xd.Width, n4W);
            if (useStep16) len = Math.Max(width16x16, len);
            else if (Math.Abs(rowOffset) > 1) len = Math.Max(len, width8x8);
            int weight = 2;
            if (xd.Width >= width8x8 && xd.Width <= n4W)
            {
                int inc = Math.Min(-maxRowOffset + rowOffset + 1, MiSizeHigh[candidateBsize]);
                weight = Math.Max(weight, inc);
                processedRows = inc - rowOffset - 1;
            }
            AddRefMvCandidate(candidate, rf0, ref refmvCount, ref refMatchCount, ref newmvCount, stack, weights, (ushort)(len * weight));
            i += len;
        }
    }

    private static void ScanColMbmi(AomCommon cm, AomMacroblockD xd, int miRow, int rf0, int colOffset, AomCandidateMv[] stack,
        ushort[] weights, ref byte refmvCount, ref byte refMatchCount, ref byte newmvCount, int maxColOffset, ref int processedCols)
    {
        int endMi = Math.Min(xd.Height, cm.MiRows - miRow);
        endMi = Math.Min(endMi, MiSizeHigh[BLOCK_64X64]);
        int n8H8 = MiSizeHigh[BLOCK_8X8], n8H16 = MiSizeHigh[BLOCK_16X16];
        int rowOffset = 0;
        if (Math.Abs(colOffset) > 1)
        {
            rowOffset = 1;
            if ((miRow & 1) != 0 && xd.Height < n8H8) --rowOffset;
        }
        bool useStep16 = xd.Height >= 16;
        for (int i = 0; i < endMi;)
        {
            var candidate = xd.MiGrid[xd.MiOffset + (rowOffset + i) * xd.MiStride + colOffset]!;
            int candidateBsize = candidate.Bsize;
            int n4H = MiSizeHigh[candidateBsize];
            int len = Math.Min(xd.Height, n4H);
            if (useStep16) len = Math.Max(n8H16, len);
            else if (Math.Abs(colOffset) > 1) len = Math.Max(len, n8H8);
            int weight = 2;
            if (xd.Height >= n8H8 && xd.Height <= n4H)
            {
                int inc = Math.Min(-maxColOffset + colOffset + 1, MiSizeWide[candidateBsize]);
                weight = Math.Max(weight, inc);
                processedCols = inc - colOffset - 1;
            }
            AddRefMvCandidate(candidate, rf0, ref refmvCount, ref refMatchCount, ref newmvCount, stack, weights, (ushort)(len * weight));
            i += len;
        }
    }

    /// <summary>is_inside (the tile is the frame's single tile).</summary>
    internal static bool IsInside(AomMacroblockD xd, int miCol, int miRow, int dr, int dc)
        => !(miRow + dr < xd.TileMiRowStart || miCol + dc < xd.TileMiColStart || miRow + dr >= xd.TileMiRowEnd || miCol + dc >= xd.TileMiColEnd);

    private static void ScanBlkMbmi(AomMacroblockD xd, int miRow, int miCol, int rf0, int rowOffset, int colOffset, AomCandidateMv[] stack,
        ushort[] weights, ref byte refMatchCount, ref byte newmvCount, ref byte refmvCount)
    {
        if (!IsInside(xd, miCol, miRow, rowOffset, colOffset)) return;
        var candidate = xd.MiGrid[xd.MiOffset + rowOffset * xd.MiStride + colOffset]!;
        int len = MiSizeWide[BLOCK_8X8];
        AddRefMvCandidate(candidate, rf0, ref refmvCount, ref refMatchCount, ref newmvCount, stack, weights, 2 * len);
    }

    /// <summary>has_top_right.</summary>
    internal static bool HasTopRight(AomCommon cm, AomMacroblockD xd, int miRow, int miCol, int bs)
    {
        int sbMiSize = MiSizeWide[cm.SbSize];
        int maskRow = miRow & (sbMiSize - 1), maskCol = miCol & (sbMiSize - 1);
        if (bs > MiSizeWide[BLOCK_64X64]) return false;
        bool hasTr = !((maskRow & bs) != 0 && (maskCol & bs) != 0);
        while (bs < sbMiSize)
        {
            if ((maskCol & bs) != 0)
            {
                if ((maskCol & (2 * bs)) != 0 && (maskRow & (2 * bs)) != 0) { hasTr = false; break; }
            }
            else break;
            bs <<= 1;
        }
        if (xd.Width < xd.Height && !xd.IsLastVerticalRect) hasTr = true;
        if (xd.Width > xd.Height && !xd.IsFirstHorizontalRect) hasTr = false;
        if (xd.Mi0.Partition == PARTITION_VERT_A && xd.Width == xd.Height && (maskRow & bs) != 0) hasTr = false;
        return hasTr;
    }

    /// <summary>clamp_mv_ref.</summary>
    private static AomMv ClampMvRef(AomMv mv, int bw, int bh, AomMacroblockD xd)
    {
        int colMin = xd.MbToLeftEdge - bw * 8 - MV_BORDER, colMax = xd.MbToRightEdge + bw * 8 + MV_BORDER;
        int rowMin = xd.MbToTopEdge - bh * 8 - MV_BORDER, rowMax = xd.MbToBottomEdge + bh * 8 + MV_BORDER;
        return new AomMv(Math.Clamp((int)mv.Row, rowMin, rowMax), Math.Clamp((int)mv.Col, colMin, colMax));
    }

    /// <summary>av1_find_mv_refs for ref_frame INTRA_FRAME (setup_ref_mv_list with rf = { INTRA_FRAME, NONE_FRAME },
    /// gm_mv = 0, allow_ref_frame_mvs off): fills xd's stack / weights and ext's count, global mv and mode context.</summary>
    public static void FindMvRefsIntra(AomCommon cm, AomMacroblockD xd, AomMbmiExt ext)
    {
        const int rf0 = 0;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        ext.GlobalMv = AomMv.Invalid;   // global_mvs[INTRA_FRAME] = INVALID_MV
        var stack = xd.RefMvStack;
        var weights = xd.RefMvWeight;

        int bs = Math.Max(xd.Width, xd.Height);
        bool hasTr = HasTopRight(cm, xd, miRow, miCol, bs);
        int maxRowOffset = 0, maxColOffset = 0;
        int rowAdj = xd.Height < MiSizeHigh[BLOCK_8X8] && (miRow & 1) != 0 ? 1 : 0;
        int colAdj = xd.Width < MiSizeWide[BLOCK_8X8] && (miCol & 1) != 0 ? 1 : 0;
        int processedRows = 0, processedCols = 0;
        short modeContext = 0;
        byte refmvCount = 0;

        if (xd.UpAvailable)
        {
            maxRowOffset = -(MVREF_ROW_COLS << 1) + rowAdj;
            if (xd.Height < MiSizeHigh[BLOCK_8X8]) maxRowOffset = -(2 << 1) + rowAdj;
            maxRowOffset = Math.Clamp(maxRowOffset, xd.TileMiRowStart - miRow, xd.TileMiRowEnd - miRow - 1);   // find_valid_row_offset
        }
        if (xd.LeftAvailable)
        {
            maxColOffset = -(MVREF_ROW_COLS << 1) + colAdj;
            if (xd.Width < MiSizeWide[BLOCK_8X8]) maxColOffset = -(2 << 1) + colAdj;
            maxColOffset = Math.Clamp(maxColOffset, xd.TileMiColStart - miCol, xd.TileMiColEnd - miCol - 1);
        }

        byte colMatchCount = 0, rowMatchCount = 0, newmvCount = 0;
        if (Math.Abs(maxRowOffset) >= 1)
            ScanRowMbmi(cm, xd, miCol, rf0, -1, stack, weights, ref refmvCount, ref rowMatchCount, ref newmvCount, maxRowOffset, ref processedRows);
        if (Math.Abs(maxColOffset) >= 1)
            ScanColMbmi(cm, xd, miRow, rf0, -1, stack, weights, ref refmvCount, ref colMatchCount, ref newmvCount, maxColOffset, ref processedCols);
        if (hasTr)
            ScanBlkMbmi(xd, miRow, miCol, rf0, -1, xd.Width, stack, weights, ref rowMatchCount, ref newmvCount, ref refmvCount);

        int nearestMatch = (rowMatchCount > 0 ? 1 : 0) + (colMatchCount > 0 ? 1 : 0);
        int nearestRefmvCount = refmvCount;
        for (int idx = 0; idx < nearestRefmvCount; ++idx) weights[idx] += REF_CAT_LEVEL;

        // (cm->features.allow_ref_frame_mvs is off on key frames: no temporal candidates)
        byte dummyNewmvCount = 0;
        ScanBlkMbmi(xd, miRow, miCol, rf0, -1, -1, stack, weights, ref rowMatchCount, ref dummyNewmvCount, ref refmvCount);
        for (int idx = 2; idx <= MVREF_ROW_COLS; ++idx)
        {
            int rowOffset = -(idx << 1) + 1 + rowAdj;
            int colOffset = -(idx << 1) + 1 + colAdj;
            if (Math.Abs(rowOffset) <= Math.Abs(maxRowOffset) && Math.Abs(rowOffset) > processedRows)
                ScanRowMbmi(cm, xd, miCol, rf0, rowOffset, stack, weights, ref refmvCount, ref rowMatchCount, ref dummyNewmvCount, maxRowOffset,
                    ref processedRows);
            if (Math.Abs(colOffset) <= Math.Abs(maxColOffset) && Math.Abs(colOffset) > processedCols)
                ScanColMbmi(cm, xd, miRow, rf0, colOffset, stack, weights, ref refmvCount, ref colMatchCount, ref dummyNewmvCount, maxColOffset,
                    ref processedCols);
        }

        int refMatchCount = (rowMatchCount > 0 ? 1 : 0) + (colMatchCount > 0 ? 1 : 0);
        switch (nearestMatch)
        {
            case 0:
                if (refMatchCount >= 1) modeContext |= 1;
                if (refMatchCount == 1) modeContext |= 1 << REFMV_OFFSET;
                else if (refMatchCount >= 2) modeContext |= 2 << REFMV_OFFSET;
                break;
            case 1:
                modeContext |= (short)(newmvCount > 0 ? 2 : 3);
                if (refMatchCount == 1) modeContext |= 3 << REFMV_OFFSET;
                else if (refMatchCount >= 2) modeContext |= 4 << REFMV_OFFSET;
                break;
            default:
                modeContext |= (short)(newmvCount >= 1 ? 4 : 5);
                modeContext |= 5 << REFMV_OFFSET;
                break;
        }

        // rank the likelihood (bubble sorts of the nearest and the outer candidates)
        int len = nearestRefmvCount;
        while (len > 0)
        {
            int nrLen = 0;
            for (int idx = 1; idx < len; ++idx)
                if (weights[idx - 1] < weights[idx])
                {
                    (stack[idx - 1], stack[idx]) = (stack[idx], stack[idx - 1]);
                    (weights[idx - 1], weights[idx]) = (weights[idx], weights[idx - 1]);
                    nrLen = idx;
                }
            len = nrLen;
        }
        len = refmvCount;
        while (len > nearestRefmvCount)
        {
            int nrLen = nearestRefmvCount;
            for (int idx = nearestRefmvCount + 1; idx < len; ++idx)
                if (weights[idx - 1] < weights[idx])
                {
                    (stack[idx - 1], stack[idx]) = (stack[idx], stack[idx - 1]);
                    (weights[idx - 1], weights[idx]) = (weights[idx], weights[idx - 1]);
                    nrLen = idx;
                }
            len = nrLen;
        }

        // single reference extension: process_single_ref_mv_candidate takes only candidates with ref_frame > INTRA_FRAME
        // (none on a key frame); it still walks the neighbours by their sizes, which has no other effect
        for (int idx = 0; idx < refmvCount; ++idx)
            stack[idx].ThisMv = ClampMvRef(stack[idx].ThisMv, xd.Width << 2, xd.Height << 2, xd);

        ext.RefMvCount = refmvCount;
        ext.ModeContext = modeContext;
    }

    /// <summary>av1_copy_usable_ref_mv_stack_and_weight (INTRA_FRAME).</summary>
    public static void CopyUsableRefMvStackAndWeight(AomMacroblockD xd, AomMbmiExt ext)
    {
        Array.Copy(xd.RefMvWeight, ext.Weight, AomMbmiExt.UsableRefMvStackSize);
        Array.Copy(xd.RefMvStack, ext.RefMvStack, AomMbmiExt.UsableRefMvStackSize);
    }

    /// <summary>lower_mv_precision (not integer; allow_hp off).</summary>
    private static AomMv LowerMvPrecision(AomMv mv, bool allowHp)
    {
        if (allowHp) return mv;
        int r = mv.Row, c = mv.Col;
        if ((r & 1) != 0) r += r > 0 ? -1 : 1;
        if ((c & 1) != 0) c += c > 0 ? -1 : 1;
        return new AomMv(r, c);
    }

    /// <summary>av1_find_best_ref_mvs_from_stack (INTRA_FRAME, is_integer 0): nearest / near from the stack or the
    /// global mv past the count.</summary>
    public static void FindBestRefMvsFromStack(bool allowHp, AomMbmiExt ext, out AomMv nearest, out AomMv near)
    {
        nearest = LowerMvPrecision(0 < ext.RefMvCount ? ext.RefMvStack[0].ThisMv : ext.GlobalMv, allowHp);
        near = LowerMvPrecision(1 < ext.RefMvCount ? ext.RefMvStack[1].ThisMv : ext.GlobalMv, allowHp);
    }

    /// <summary>av1_find_ref_dv.</summary>
    public static AomMv FindRefDv(AomMacroblockD xd, int mibSize, int miRow)
    {
        if (miRow - mibSize < xd.TileMiRowStart) return new AomMv(0, -4 * mibSize - INTRABC_DELAY_PIXELS).ToMv();
        return new AomMv(-4 * mibSize, 0).ToMv();
    }

    /// <summary>av1_is_dv_valid.</summary>
    public static bool IsDvValid(AomMv dv, AomCommon cm, AomMacroblockD xd, int miRow, int miCol, int bsize, int mibSizeLog2)
    {
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        const int SCALE_PX_TO_MV = 8;
        if ((dv.Row & (SCALE_PX_TO_MV - 1)) != 0 || (dv.Col & (SCALE_PX_TO_MV - 1)) != 0) return false;
        int srcTopEdge = miRow * 4 * SCALE_PX_TO_MV + dv.Row;
        int tileTopEdge = xd.TileMiRowStart * 4 * SCALE_PX_TO_MV;
        if (srcTopEdge < tileTopEdge) return false;
        int srcLeftEdge = miCol * 4 * SCALE_PX_TO_MV + dv.Col;
        int tileLeftEdge = xd.TileMiColStart * 4 * SCALE_PX_TO_MV;
        if (srcLeftEdge < tileLeftEdge) return false;
        int srcBottomEdge = (miRow * 4 + bh) * SCALE_PX_TO_MV + dv.Row;
        int tileBottomEdge = xd.TileMiRowEnd * 4 * SCALE_PX_TO_MV;
        if (srcBottomEdge > tileBottomEdge) return false;
        int srcRightEdge = (miCol * 4 + bw) * SCALE_PX_TO_MV + dv.Col;
        int tileRightEdge = xd.TileMiColEnd * 4 * SCALE_PX_TO_MV;
        if (srcRightEdge > tileRightEdge) return false;

        // sub 8x8 chroma: no chroma pixels outside the tile
        if (xd.IsChromaRef && cm.NumPlanes > 1)
        {
            var pd = xd.Plane[1];
            if (bw < 8 && pd.SubsamplingX != 0 && srcLeftEdge < tileLeftEdge + 4 * SCALE_PX_TO_MV) return false;
            if (bh < 8 && pd.SubsamplingY != 0 && srcTopEdge < tileTopEdge + 4 * SCALE_PX_TO_MV) return false;
        }

        // the bottom right inside an already coded SB, with the delay / wavefront constraints for hardware decoders
        int maxMibSize = 1 << mibSizeLog2;
        int activeSbRow = miRow >> mibSizeLog2;
        int activeSb64Col = (miCol * 4) >> 6;
        int sbSize = maxMibSize * 4;
        int srcSbRow = ((srcBottomEdge >> 3) - 1) / sbSize;
        int srcSb64Col = ((srcRightEdge >> 3) - 1) >> 6;
        int totalSb64PerRow = ((xd.TileMiColEnd - xd.TileMiColStart - 1) >> 4) + 1;
        int activeSb64 = activeSbRow * totalSb64PerRow + activeSb64Col;
        int srcSb64 = srcSbRow * totalSb64PerRow + srcSb64Col;
        if (srcSb64 >= activeSb64 - INTRABC_DELAY_SB64) return false;

        int gradient = 1 + INTRABC_DELAY_SB64 + (sbSize > 64 ? 1 : 0);
        int wfOffset = gradient * (activeSbRow - srcSbRow);
        if (srcSbRow > activeSbRow || srcSb64Col >= activeSb64Col - INTRABC_DELAY_SB64 + wfOffset) return false;
        return true;
    }
}
