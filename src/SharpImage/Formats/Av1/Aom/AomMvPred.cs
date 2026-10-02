using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>MB_MODE_INFO_EXT (all reference frame types).</summary>
internal sealed class AomMbmiExtInter
{
    public readonly AomCandidateMv[][] RefMvStack = NewStacks();
    public readonly ushort[][] Weight = NewWeights();
    public readonly byte[] RefMvCount = new byte[MODE_CTX_REF_FRAMES];
    public readonly AomMv[] GlobalMvs = new AomMv[REF_FRAMES];
    public readonly short[] ModeContext = new short[MODE_CTX_REF_FRAMES];

    private static AomCandidateMv[][] NewStacks()
    {
        var a = new AomCandidateMv[MODE_CTX_REF_FRAMES][];
        for (int i = 0; i < a.Length; i++) a[i] = new AomCandidateMv[USABLE_REF_MV_STACK_SIZE];
        return a;
    }

    private static ushort[][] NewWeights()
    {
        var a = new ushort[MODE_CTX_REF_FRAMES][];
        for (int i = 0; i < a.Length; i++) a[i] = new ushort[USABLE_REF_MV_STACK_SIZE];
        return a;
    }

    public void CopyFrom(AomMbmiExtInter s)
    {
        for (int i = 0; i < MODE_CTX_REF_FRAMES; i++)
        {
            Array.Copy(s.RefMvStack[i], RefMvStack[i], USABLE_REF_MV_STACK_SIZE);
            Array.Copy(s.Weight[i], Weight[i], USABLE_REF_MV_STACK_SIZE);
        }
        Array.Copy(s.RefMvCount, RefMvCount, MODE_CTX_REF_FRAMES);
        Array.Copy(s.GlobalMvs, GlobalMvs, REF_FRAMES);
        Array.Copy(s.ModeContext, ModeContext, MODE_CTX_REF_FRAMES);
    }
}

/// <summary>MB_MODE_INFO_EXT_FRAME of an inter block (one reference type).</summary>
internal sealed class AomMbmiExtFrameInter
{
    public readonly AomCandidateMv[] RefMvStack = new AomCandidateMv[USABLE_REF_MV_STACK_SIZE];
    public readonly ushort[] Weight = new ushort[USABLE_REF_MV_STACK_SIZE];
    public byte RefMvCount;
    public readonly AomMv[] GlobalMvs = new AomMv[REF_FRAMES];
    public short ModeContext;
    public readonly ushort[] CbOffset = new ushort[2];

    /// <summary>av1_copy_mbmi_ext_to_mbmi_ext_frame.</summary>
    public void CopyFrom(AomMbmiExtInter e, int refFrameType)
    {
        Array.Copy(e.RefMvStack[refFrameType], RefMvStack, USABLE_REF_MV_STACK_SIZE);
        Array.Copy(e.Weight[refFrameType], Weight, USABLE_REF_MV_STACK_SIZE);
        ModeContext = e.ModeContext[refFrameType];
        RefMvCount = e.RefMvCount[refFrameType];
        Array.Copy(e.GlobalMvs, GlobalMvs, REF_FRAMES);
    }

    /// <summary>copy_mbmi_ext_frame_to_mbmi_ext.</summary>
    public void CopyTo(AomMbmiExtInter e, int refFrameType)
    {
        Array.Copy(RefMvStack, e.RefMvStack[refFrameType], USABLE_REF_MV_STACK_SIZE);
        Array.Copy(Weight, e.Weight[refFrameType], USABLE_REF_MV_STACK_SIZE);
        e.ModeContext[refFrameType] = ModeContext;
        e.RefMvCount[refFrameType] = RefMvCount;
        Array.Copy(GlobalMvs, e.GlobalMvs, REF_FRAMES);
    }

    public void CopyFrom(AomMbmiExtFrameInter s)
    {
        Array.Copy(s.RefMvStack, RefMvStack, USABLE_REF_MV_STACK_SIZE);
        Array.Copy(s.Weight, Weight, USABLE_REF_MV_STACK_SIZE);
        RefMvCount = s.RefMvCount;
        Array.Copy(s.GlobalMvs, GlobalMvs, REF_FRAMES);
        ModeContext = s.ModeContext;
        CbOffset[0] = s.CbOffset[0];
        CbOffset[1] = s.CbOffset[1];
    }
}

internal sealed partial class AomMacroblockD
{
    // xd->ref_mv_stack / xd->weight [MODE_CTX_REF_FRAMES][MAX_REF_MV_STACK_SIZE] (persist across blocks)
    public readonly AomCandidateMv[][] RefMvStacks = NewStacks();
    public readonly ushort[][] RefMvWeights = NewWeights();
    public byte[] NeighborsRefCounts = new byte[REF_FRAMES];
    public bool CurFrameForceIntegerMv;

    private static AomCandidateMv[][] NewStacks()
    {
        var a = new AomCandidateMv[MODE_CTX_REF_FRAMES][];
        for (int i = 0; i < a.Length; i++) a[i] = new AomCandidateMv[MAX_REF_MV_STACK_SIZE];
        return a;
    }

    private static ushort[][] NewWeights()
    {
        var a = new ushort[MODE_CTX_REF_FRAMES][];
        for (int i = 0; i < a.Length; i++) a[i] = new ushort[MAX_REF_MV_STACK_SIZE];
        return a;
    }
}

// Port of libaom 3.14.1 av1/common/mvref_common.c: av1_find_mv_refs (setup_ref_mv_list with the spatial scans, the
// temporal candidates from the motion field and the extra compound / single candidates), av1_setup_motion_field,
// av1_copy_frame_mvs, av1_calculate_ref_frame_side, av1_setup_frame_sign_bias; rdopt_utils.h's
// av1_copy_usable_ref_mv_stack_and_weight.
internal static class AomMvPred
{
    private const int MVREF_ROW_COLS = 3, MV_BORDER = 16 << 3, MAX_OFFSET_WIDTH = 64, MAX_OFFSET_HEIGHT = 0;

    private static AomMbModeInfo Mi(AomMacroblockD xd, int dr, int dc) => xd.MiGrid[xd.MiOffset + dr * xd.MiStride + dc]!;

    private static void AddRefMvCandidate(AomMbModeInfo candidate, int rf0, int rf1, ref byte refmvCount, ref byte refMatchCount,
        ref byte newmvCount, AomCandidateMv[] stack, ushort[] weights, AomMv gm0, AomMv gm1, AomWarpedMotionParams[] gmParams, int weight)
    {
        if (!candidate.IsInterBlock) return;
        int index;
        if (rf1 == NONE_FRAME)
        {
            for (int r = 0; r < 2; ++r)
            {
                if ((r == 0 ? candidate.RefFrame0 : candidate.RefFrame1) != rf0) continue;
                bool isGmBlock = AomInter.IsGlobalMvBlock(candidate, gmParams[rf0].WmType);
                var thisRefmv = isGmBlock ? gm0 : (r == 0 ? candidate.Mv0 : candidate.Mv1);
                for (index = 0; index < refmvCount; ++index)
                    if (stack[index].ThisMv.AsInt == thisRefmv.AsInt) { weights[index] += (ushort)weight; break; }
                if (index == refmvCount && refmvCount < MAX_REF_MV_STACK_SIZE)
                {
                    stack[index].ThisMv = thisRefmv;
                    weights[index] = (ushort)weight;
                    ++refmvCount;
                }
                if (AomInter.HaveNewmvInInterMode(candidate.Mode)) ++newmvCount;
                ++refMatchCount;
            }
        }
        else
        {
            if (candidate.RefFrame0 == rf0 && candidate.RefFrame1 == rf1)
            {
                var m0 = AomInter.IsGlobalMvBlock(candidate, gmParams[rf0].WmType) ? gm0 : candidate.Mv0;
                var m1 = AomInter.IsGlobalMvBlock(candidate, gmParams[rf1].WmType) ? gm1 : candidate.Mv1;
                for (index = 0; index < refmvCount; ++index)
                    if (stack[index].ThisMv.AsInt == m0.AsInt && stack[index].CompMv.AsInt == m1.AsInt)
                    {
                        weights[index] += (ushort)weight;
                        break;
                    }
                if (index == refmvCount && refmvCount < MAX_REF_MV_STACK_SIZE)
                {
                    stack[index].ThisMv = m0;
                    stack[index].CompMv = m1;
                    weights[index] = (ushort)weight;
                    ++refmvCount;
                }
                if (AomInter.HaveNewmvInInterMode(candidate.Mode)) ++newmvCount;
                ++refMatchCount;
            }
        }
    }

    private static void ScanRowMbmi(AomCommon cm, AomMacroblockD xd, int miCol, int rf0, int rf1, int rowOffset, AomCandidateMv[] stack,
        ushort[] weights, ref byte refmvCount, ref byte refMatchCount, ref byte newmvCount, AomMv gm0, AomMv gm1, int maxRowOffset,
        ref int processedRows)
    {
        int endMi = Math.Min(xd.Width, cm.MiCols - miCol);
        endMi = Math.Min(endMi, MiSizeWide[BLOCK_64X64]);
        int width8x8 = MiSizeWide[BLOCK_8X8], width16x16 = MiSizeWide[BLOCK_16X16];
        int colOffset = 0;
        if (Math.Abs(rowOffset) > 1)
        {
            colOffset = 1;
            if ((miCol & 0x01) != 0 && xd.Width < width8x8) --colOffset;
        }
        bool useStep16 = xd.Width >= 16;
        for (int i = 0; i < endMi;)
        {
            var candidate = Mi(xd, rowOffset, colOffset + i);
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
            AddRefMvCandidate(candidate, rf0, rf1, ref refmvCount, ref refMatchCount, ref newmvCount, stack, weights, gm0, gm1, cm.GlobalMotion,
                (ushort)(len * weight));
            i += len;
        }
    }

    private static void ScanColMbmi(AomCommon cm, AomMacroblockD xd, int miRow, int rf0, int rf1, int colOffset, AomCandidateMv[] stack,
        ushort[] weights, ref byte refmvCount, ref byte refMatchCount, ref byte newmvCount, AomMv gm0, AomMv gm1, int maxColOffset,
        ref int processedCols)
    {
        int endMi = Math.Min(xd.Height, cm.MiRows - miRow);
        endMi = Math.Min(endMi, MiSizeHigh[BLOCK_64X64]);
        int n8h8 = MiSizeHigh[BLOCK_8X8], n8h16 = MiSizeHigh[BLOCK_16X16];
        int rowOffset = 0;
        if (Math.Abs(colOffset) > 1)
        {
            rowOffset = 1;
            if ((miRow & 0x01) != 0 && xd.Height < n8h8) --rowOffset;
        }
        bool useStep16 = xd.Height >= 16;
        for (int i = 0; i < endMi;)
        {
            var candidate = Mi(xd, rowOffset + i, colOffset);
            int candidateBsize = candidate.Bsize;
            int n4H = MiSizeHigh[candidateBsize];
            int len = Math.Min(xd.Height, n4H);
            if (useStep16) len = Math.Max(n8h16, len);
            else if (Math.Abs(colOffset) > 1) len = Math.Max(len, n8h8);
            int weight = 2;
            if (xd.Height >= n8h8 && xd.Height <= n4H)
            {
                int inc = Math.Min(-maxColOffset + colOffset + 1, MiSizeWide[candidateBsize]);
                weight = Math.Max(weight, inc);
                processedCols = inc - colOffset - 1;
            }
            AddRefMvCandidate(candidate, rf0, rf1, ref refmvCount, ref refMatchCount, ref newmvCount, stack, weights, gm0, gm1, cm.GlobalMotion,
                (ushort)(len * weight));
            i += len;
        }
    }

    private static bool IsInside(AomMacroblockD xd, int miCol, int miRow, int dr, int dc)
        => !(miRow + dr < xd.TileMiRowStart || miCol + dc < xd.TileMiColStart || miRow + dr >= xd.TileMiRowEnd || miCol + dc >= xd.TileMiColEnd);

    private static void ScanBlkMbmi(AomCommon cm, AomMacroblockD xd, int miRow, int miCol, int rf0, int rf1, int rowOffset, int colOffset,
        AomCandidateMv[] stack, ushort[] weights, ref byte refMatchCount, ref byte newmvCount, AomMv gm0, AomMv gm1, ref byte refmvCount)
    {
        if (IsInside(xd, miCol, miRow, rowOffset, colOffset))
        {
            var candidate = Mi(xd, rowOffset, colOffset);
            int len = MiSizeWide[BLOCK_8X8];
            AddRefMvCandidate(candidate, rf0, rf1, ref refmvCount, ref refMatchCount, ref newmvCount, stack, weights, gm0, gm1, cm.GlobalMotion,
                2 * len);
        }
    }

    private static bool HasTopRight(AomCommon cm, AomMacroblockD xd, int miRow, int miCol, int bs)
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

    private static bool CheckSbBorder(int miRow, int miCol, int rowOffset, int colOffset)
    {
        int sbMiSize = MiSizeWide[BLOCK_64X64];
        int row = miRow & (sbMiSize - 1), col = miCol & (sbMiSize - 1);
        return !(row + rowOffset < 0 || row + rowOffset >= sbMiSize || col + colOffset < 0 || col + colOffset >= sbMiSize);
    }

    private static readonly int[] DivMult =
    {
        0, 16384, 8192, 5461, 4096, 3276, 2730, 2340, 2048, 1820, 1638, 1489, 1365, 1260, 1170, 1092,
        1024, 963, 910, 862, 819, 780, 744, 712, 682, 655, 630, 606, 585, 564, 546, 528,
    };

    /// <summary>av1_get_mv_projection.</summary>
    public static AomMv GetMvProjection(AomMv reff, int num, int den)
    {
        den = Math.Min(den, MAX_FRAME_DISTANCE);
        num = num > 0 ? Math.Min(num, MAX_FRAME_DISTANCE) : Math.Max(num, -MAX_FRAME_DISTANCE);
        int mvRow = AomInter.RoundPowerOfTwoSigned(reff.Row * num * DivMult[den], 14);
        int mvCol = AomInter.RoundPowerOfTwoSigned(reff.Col * num * DivMult[den], 14);
        int clampMax = MV_UPP - 1, clampMin = MV_LOW + 1;
        return new AomMv(Math.Clamp(mvRow, clampMin, clampMax), Math.Clamp(mvCol, clampMin, clampMax));
    }

    private static bool AddTplRefMv(AomCommon cm, AomMacroblockD xd, int miRow, int miCol, int refFrame, int blkRow, int blkCol,
        AomMv gm0, AomMv gm1, ref byte refmvCount, AomCandidateMv[] stack, ushort[] weights, short[] modeContext)
    {
        int posRow = (miRow & 0x01) != 0 ? blkRow : blkRow + 1;
        int posCol = (miCol & 0x01) != 0 ? blkCol : blkCol + 1;
        if (!IsInside(xd, miCol, miRow, posRow, posCol)) return false;
        var prev = cm.TplMvs![((miRow + posRow) >> 1) * (cm.MiStride >> 1) + ((miCol + posCol) >> 1)];
        if (prev.Mfmv0.AsInt == INVALID_MV) return false;
        var (rf0, rf1) = AomInter.SetRefFrame(refFrame);
        const int weightUnit = 1;
        int curFrameIndex = cm.OrderHint;
        int frame0Index = cm.RefBufs[rf0]!.OrderHint;
        int curOffset0 = cm.RelativeDist(curFrameIndex, frame0Index);
        var thisRefmv = AomInter.LowerMvPrecision(GetMvProjection(prev.Mfmv0, curOffset0, prev.RefFrameOffset), cm.AllowHighPrecisionMv,
            cm.CurFrameForceIntegerMv);
        int idx;
        if (rf1 == NONE_FRAME)
        {
            if (blkRow == 0 && blkCol == 0)
                if (Math.Abs(thisRefmv.Row - gm0.Row) >= 16 || Math.Abs(thisRefmv.Col - gm0.Col) >= 16)
                    modeContext[refFrame] |= 1 << GLOBALMV_OFFSET;
            for (idx = 0; idx < refmvCount; ++idx)
                if (thisRefmv.AsInt == stack[idx].ThisMv.AsInt) break;
            if (idx < refmvCount) weights[idx] += 2 * weightUnit;
            if (idx == refmvCount && refmvCount < MAX_REF_MV_STACK_SIZE)
            {
                stack[idx].ThisMv = thisRefmv;
                weights[idx] = 2 * weightUnit;
                ++refmvCount;
            }
        }
        else
        {
            int frame1Index = cm.RefBufs[rf1]!.OrderHint;
            int curOffset1 = cm.RelativeDist(curFrameIndex, frame1Index);
            var compRefmv = AomInter.LowerMvPrecision(GetMvProjection(prev.Mfmv0, curOffset1, prev.RefFrameOffset), cm.AllowHighPrecisionMv,
                cm.CurFrameForceIntegerMv);
            if (blkRow == 0 && blkCol == 0)
                if (Math.Abs(thisRefmv.Row - gm0.Row) >= 16 || Math.Abs(thisRefmv.Col - gm0.Col) >= 16 ||
                    Math.Abs(compRefmv.Row - gm1.Row) >= 16 || Math.Abs(compRefmv.Col - gm1.Col) >= 16)
                    modeContext[refFrame] |= 1 << GLOBALMV_OFFSET;
            for (idx = 0; idx < refmvCount; ++idx)
                if (thisRefmv.AsInt == stack[idx].ThisMv.AsInt && compRefmv.AsInt == stack[idx].CompMv.AsInt) break;
            if (idx < refmvCount) weights[idx] += 2 * weightUnit;
            if (idx == refmvCount && refmvCount < MAX_REF_MV_STACK_SIZE)
            {
                stack[idx].ThisMv = thisRefmv;
                stack[idx].CompMv = compRefmv;
                weights[idx] = 2 * weightUnit;
                ++refmvCount;
            }
        }
        return true;
    }

    private static void ProcessCompoundRefMvCandidate(AomMbModeInfo candidate, AomCommon cm, int rf0, int rf1, AomMv[,] refId, int[] refIdCount,
        AomMv[,] refDiff, int[] refDiffCount)
    {
        for (int rfIdx = 0; rfIdx < 2; ++rfIdx)
        {
            int canRf = rfIdx == 0 ? candidate.RefFrame0 : candidate.RefFrame1;
            var canMv = rfIdx == 0 ? candidate.Mv0 : candidate.Mv1;
            for (int cmpIdx = 0; cmpIdx < 2; ++cmpIdx)
            {
                int rfc = cmpIdx == 0 ? rf0 : rf1;
                if (canRf == rfc && refIdCount[cmpIdx] < 2)
                {
                    refId[cmpIdx, refIdCount[cmpIdx]] = canMv;
                    ++refIdCount[cmpIdx];
                }
                else if (canRf > INTRA_FRAME && refDiffCount[cmpIdx] < 2)
                {
                    var thisMv = canMv;
                    if (cm.RefFrameSignBias[canRf] != cm.RefFrameSignBias[rfc]) thisMv = new AomMv(-thisMv.Row, -thisMv.Col);
                    refDiff[cmpIdx, refDiffCount[cmpIdx]] = thisMv;
                    ++refDiffCount[cmpIdx];
                }
            }
        }
    }

    private static void ProcessSingleRefMvCandidate(AomMbModeInfo candidate, AomCommon cm, int refFrame, ref byte refmvCount,
        AomCandidateMv[] stack, ushort[] weights)
    {
        for (int rfIdx = 0; rfIdx < 2; ++rfIdx)
        {
            int candRf = rfIdx == 0 ? candidate.RefFrame0 : candidate.RefFrame1;
            if (candRf > INTRA_FRAME)
            {
                var thisMv = rfIdx == 0 ? candidate.Mv0 : candidate.Mv1;
                if (cm.RefFrameSignBias[candRf] != cm.RefFrameSignBias[refFrame]) thisMv = new AomMv(-thisMv.Row, -thisMv.Col);
                int stackIdx;
                for (stackIdx = 0; stackIdx < refmvCount; ++stackIdx)
                    if (thisMv.AsInt == stack[stackIdx].ThisMv.AsInt) break;
                if (stackIdx == refmvCount)
                {
                    stack[stackIdx].ThisMv = thisMv;
                    weights[stackIdx] = 2;
                    ++refmvCount;
                }
            }
        }
    }

    /// <summary>clamp_mv_ref.</summary>
    private static AomMv ClampMvRef(AomMv mv, int bw, int bh, AomMacroblockD xd)
    {
        int colMin = xd.MbToLeftEdge - bw * 8 - MV_BORDER, colMax = xd.MbToRightEdge + bw * 8 + MV_BORDER;
        int rowMin = xd.MbToTopEdge - bh * 8 - MV_BORDER, rowMax = xd.MbToBottomEdge + bh * 8 + MV_BORDER;
        return new AomMv(Math.Clamp((int)mv.Row, rowMin, rowMax), Math.Clamp((int)mv.Col, colMin, colMax));
    }

    private static void SetupRefMvList(AomCommon cm, AomMacroblockD xd, int refFrame, ref byte refmvCount, AomCandidateMv[] stack,
        ushort[] weights, AomMv[]? mvRefList, AomMv gm0, AomMv gm1, int miRow, int miCol, short[] modeContext)
    {
        int bs = Math.Max(xd.Width, xd.Height);
        bool hasTr = HasTopRight(cm, xd, miRow, miCol, bs);
        int maxRowOffset = 0, maxColOffset = 0;
        int rowAdj = xd.Height < MiSizeHigh[BLOCK_8X8] && (miRow & 0x01) != 0 ? 1 : 0;
        int colAdj = xd.Width < MiSizeWide[BLOCK_8X8] && (miCol & 0x01) != 0 ? 1 : 0;
        int processedRows = 0, processedCols = 0;
        var (rf0, rf1) = AomInter.SetRefFrame(refFrame);
        modeContext[refFrame] = 0;
        refmvCount = 0;
        if (xd.UpAvailable)
        {
            maxRowOffset = -(MVREF_ROW_COLS << 1) + rowAdj;
            if (xd.Height < MiSizeHigh[BLOCK_8X8]) maxRowOffset = -(2 << 1) + rowAdj;
            maxRowOffset = Math.Clamp(maxRowOffset, xd.TileMiRowStart - miRow, xd.TileMiRowEnd - miRow - 1);
        }
        if (xd.LeftAvailable)
        {
            maxColOffset = -(MVREF_ROW_COLS << 1) + colAdj;
            if (xd.Width < MiSizeWide[BLOCK_8X8]) maxColOffset = -(2 << 1) + colAdj;
            maxColOffset = Math.Clamp(maxColOffset, xd.TileMiColStart - miCol, xd.TileMiColEnd - miCol - 1);
        }
        byte colMatchCount = 0, rowMatchCount = 0, newmvCount = 0;
        if (Math.Abs(maxRowOffset) >= 1)
            ScanRowMbmi(cm, xd, miCol, rf0, rf1, -1, stack, weights, ref refmvCount, ref rowMatchCount, ref newmvCount, gm0, gm1, maxRowOffset,
                ref processedRows);
        if (Math.Abs(maxColOffset) >= 1)
            ScanColMbmi(cm, xd, miRow, rf0, rf1, -1, stack, weights, ref refmvCount, ref colMatchCount, ref newmvCount, gm0, gm1, maxColOffset,
                ref processedCols);
        if (hasTr)
            ScanBlkMbmi(cm, xd, miRow, miCol, rf0, rf1, -1, xd.Width, stack, weights, ref rowMatchCount, ref newmvCount, gm0, gm1, ref refmvCount);
        int nearestMatch = (rowMatchCount > 0 ? 1 : 0) + (colMatchCount > 0 ? 1 : 0);
        int nearestRefmvCount = refmvCount;
        for (int idx = 0; idx < nearestRefmvCount; ++idx) weights[idx] += REF_CAT_LEVEL;

        if (cm.AllowRefFrameMvs)
        {
            bool isAvailable = false;
            int voffset = Math.Max(MiSizeHigh[BLOCK_8X8], xd.Height), hoffset = Math.Max(MiSizeWide[BLOCK_8X8], xd.Width);
            int blkRowEnd = Math.Min(xd.Height, MiSizeHigh[BLOCK_64X64]), blkColEnd = Math.Min(xd.Width, MiSizeWide[BLOCK_64X64]);
            Span<int> tplSamplePos = stackalloc int[] { voffset, -2, voffset, hoffset, voffset - 2, hoffset };
            bool allowExtension = xd.Height >= MiSizeHigh[BLOCK_8X8] && xd.Height < MiSizeHigh[BLOCK_64X64] &&
                xd.Width >= MiSizeWide[BLOCK_8X8] && xd.Width < MiSizeWide[BLOCK_64X64];
            int stepH = xd.Height >= MiSizeHigh[BLOCK_64X64] ? MiSizeHigh[BLOCK_16X16] : MiSizeHigh[BLOCK_8X8];
            int stepW = xd.Width >= MiSizeWide[BLOCK_64X64] ? MiSizeWide[BLOCK_16X16] : MiSizeWide[BLOCK_8X8];
            for (int blkRow = 0; blkRow < blkRowEnd; blkRow += stepH)
                for (int blkCol = 0; blkCol < blkColEnd; blkCol += stepW)
                {
                    bool ret = AddTplRefMv(cm, xd, miRow, miCol, refFrame, blkRow, blkCol, gm0, gm1, ref refmvCount, stack, weights, modeContext);
                    if (blkRow == 0 && blkCol == 0) isAvailable = ret;
                }
            if (!isAvailable) modeContext[refFrame] |= 1 << GLOBALMV_OFFSET;
            for (int i = 0; i < 3 && allowExtension; ++i)
            {
                int blkRow = tplSamplePos[i * 2], blkCol = tplSamplePos[i * 2 + 1];
                if (!CheckSbBorder(miRow, miCol, blkRow, blkCol)) continue;
                AddTplRefMv(cm, xd, miRow, miCol, refFrame, blkRow, blkCol, gm0, gm1, ref refmvCount, stack, weights, modeContext);
            }
        }

        byte dummyNewmvCount = 0;
        ScanBlkMbmi(cm, xd, miRow, miCol, rf0, rf1, -1, -1, stack, weights, ref rowMatchCount, ref dummyNewmvCount, gm0, gm1, ref refmvCount);
        for (int idx = 2; idx <= MVREF_ROW_COLS; ++idx)
        {
            int rowOffset = -(idx << 1) + 1 + rowAdj, colOffset = -(idx << 1) + 1 + colAdj;
            if (Math.Abs(rowOffset) <= Math.Abs(maxRowOffset) && Math.Abs(rowOffset) > processedRows)
                ScanRowMbmi(cm, xd, miCol, rf0, rf1, rowOffset, stack, weights, ref refmvCount, ref rowMatchCount, ref dummyNewmvCount, gm0, gm1,
                    maxRowOffset, ref processedRows);
            if (Math.Abs(colOffset) <= Math.Abs(maxColOffset) && Math.Abs(colOffset) > processedCols)
                ScanColMbmi(cm, xd, miRow, rf0, rf1, colOffset, stack, weights, ref refmvCount, ref colMatchCount, ref dummyNewmvCount, gm0, gm1,
                    maxColOffset, ref processedCols);
        }
        int refMatchCount = (rowMatchCount > 0 ? 1 : 0) + (colMatchCount > 0 ? 1 : 0);
        switch (nearestMatch)
        {
            case 0:
                if (refMatchCount >= 1) modeContext[refFrame] |= 1;
                if (refMatchCount == 1) modeContext[refFrame] |= 1 << REFMV_OFFSET;
                else if (refMatchCount >= 2) modeContext[refFrame] |= 2 << REFMV_OFFSET;
                break;
            case 1:
                modeContext[refFrame] |= (short)(newmvCount > 0 ? 2 : 3);
                if (refMatchCount == 1) modeContext[refFrame] |= 3 << REFMV_OFFSET;
                else if (refMatchCount >= 2) modeContext[refFrame] |= 4 << REFMV_OFFSET;
                break;
            default:
                if (newmvCount >= 1) modeContext[refFrame] |= 4;
                else modeContext[refFrame] |= 5;
                modeContext[refFrame] |= 5 << REFMV_OFFSET;
                break;
        }

        // rank the likelihood and assign nearest and near mvs
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

        int miWidth = Math.Min(MiSizeWide[BLOCK_64X64], xd.Width);
        miWidth = Math.Min(miWidth, cm.MiCols - miCol);
        int miHeight = Math.Min(MiSizeHigh[BLOCK_64X64], xd.Height);
        miHeight = Math.Min(miHeight, cm.MiRows - miRow);
        int miSize = Math.Min(miWidth, miHeight);
        if (rf1 > NONE_FRAME)
        {
            if (refmvCount < MAX_MV_REF_CANDIDATES)
            {
                var refId = new AomMv[2, 2];
                var refDiff = new AomMv[2, 2];
                int[] refIdCount = new int[2], refDiffCount = new int[2];
                for (int idx = 0; Math.Abs(maxRowOffset) >= 1 && idx < miSize;)
                {
                    var candidate = Mi(xd, -1, idx);
                    ProcessCompoundRefMvCandidate(candidate, cm, rf0, rf1, refId, refIdCount, refDiff, refDiffCount);
                    idx += MiSizeWide[candidate.Bsize];
                }
                for (int idx = 0; Math.Abs(maxColOffset) >= 1 && idx < miSize;)
                {
                    var candidate = Mi(xd, idx, -1);
                    ProcessCompoundRefMvCandidate(candidate, cm, rf0, rf1, refId, refIdCount, refDiff, refDiffCount);
                    idx += MiSizeHigh[candidate.Bsize];
                }
                var compList = new AomMv[MAX_MV_REF_CANDIDATES, 2];
                for (int idx = 0; idx < 2; ++idx)
                {
                    int compIdx = 0;
                    for (int listIdx = 0; listIdx < refIdCount[idx] && compIdx < MAX_MV_REF_CANDIDATES; ++listIdx, ++compIdx)
                        compList[compIdx, idx] = refId[idx, listIdx];
                    for (int listIdx = 0; listIdx < refDiffCount[idx] && compIdx < MAX_MV_REF_CANDIDATES; ++listIdx, ++compIdx)
                        compList[compIdx, idx] = refDiff[idx, listIdx];
                    for (; compIdx < MAX_MV_REF_CANDIDATES; ++compIdx) compList[compIdx, idx] = idx == 0 ? gm0 : gm1;
                }
                if (refmvCount != 0)
                {
                    if (compList[0, 0].AsInt == stack[0].ThisMv.AsInt && compList[0, 1].AsInt == stack[0].CompMv.AsInt)
                    {
                        stack[refmvCount].ThisMv = compList[1, 0];
                        stack[refmvCount].CompMv = compList[1, 1];
                    }
                    else
                    {
                        stack[refmvCount].ThisMv = compList[0, 0];
                        stack[refmvCount].CompMv = compList[0, 1];
                    }
                    weights[refmvCount] = 2;
                    ++refmvCount;
                }
                else
                {
                    for (int idx = 0; idx < MAX_MV_REF_CANDIDATES; ++idx)
                    {
                        stack[refmvCount].ThisMv = compList[idx, 0];
                        stack[refmvCount].CompMv = compList[idx, 1];
                        weights[refmvCount] = 2;
                        ++refmvCount;
                    }
                }
            }
            for (int idx = 0; idx < refmvCount; ++idx)
            {
                stack[idx].ThisMv = ClampMvRef(stack[idx].ThisMv, xd.Width << 2, xd.Height << 2, xd);
                stack[idx].CompMv = ClampMvRef(stack[idx].CompMv, xd.Width << 2, xd.Height << 2, xd);
            }
        }
        else
        {
            for (int idx = 0; Math.Abs(maxRowOffset) >= 1 && idx < miSize && refmvCount < MAX_MV_REF_CANDIDATES;)
            {
                var candidate = Mi(xd, -1, idx);
                ProcessSingleRefMvCandidate(candidate, cm, refFrame, ref refmvCount, stack, weights);
                idx += MiSizeWide[candidate.Bsize];
            }
            for (int idx = 0; Math.Abs(maxColOffset) >= 1 && idx < miSize && refmvCount < MAX_MV_REF_CANDIDATES;)
            {
                var candidate = Mi(xd, idx, -1);
                ProcessSingleRefMvCandidate(candidate, cm, refFrame, ref refmvCount, stack, weights);
                idx += MiSizeHigh[candidate.Bsize];
            }
            for (int idx = 0; idx < refmvCount; ++idx)
                stack[idx].ThisMv = ClampMvRef(stack[idx].ThisMv, xd.Width << 2, xd.Height << 2, xd);
            if (mvRefList != null)
            {
                for (int idx = refmvCount; idx < MAX_MV_REF_CANDIDATES; ++idx) mvRefList[idx] = gm0;
                for (int idx = 0; idx < Math.Min(MAX_MV_REF_CANDIDATES, (int)refmvCount); ++idx) mvRefList[idx] = stack[idx].ThisMv;
            }
        }
    }

    /// <summary>av1_find_mv_refs into x->mbmi_ext's count / global mvs / mode context and xd's stacks.</summary>
    public static void FindMvRefs(AomCommon cm, AomMacroblockD xd, AomMbModeInfo mi, int refFrame, AomMbmiExtInter ext, AomMv[]? mvRefList = null,
        bool storeGlobalMvs = true)
    {
        int miRow = xd.MiRow, miCol = xd.MiCol;
        AomMv gm0 = default, gm1 = default;
        if (refFrame == INTRA_FRAME)
        {
            if (storeGlobalMvs) ext.GlobalMvs[refFrame] = new AomMv(-32768, -32768);   // INVALID_MV (as_int 0x80008000)
        }
        else
        {
            int bsize = mi.Bsize;
            if (refFrame < REF_FRAMES)
            {
                gm0 = AomInter.GmGetMotionVector(cm.GlobalMotion[refFrame], cm.AllowHighPrecisionMv, bsize, miCol, miRow, cm.CurFrameForceIntegerMv);
                if (storeGlobalMvs) ext.GlobalMvs[refFrame] = gm0;
            }
            else
            {
                var (rf0, rf1) = AomInter.SetRefFrame(refFrame);
                gm0 = AomInter.GmGetMotionVector(cm.GlobalMotion[rf0], cm.AllowHighPrecisionMv, bsize, miCol, miRow, cm.CurFrameForceIntegerMv);
                gm1 = AomInter.GmGetMotionVector(cm.GlobalMotion[rf1], cm.AllowHighPrecisionMv, bsize, miCol, miRow, cm.CurFrameForceIntegerMv);
            }
        }
        byte count = ext.RefMvCount[refFrame];
        SetupRefMvList(cm, xd, refFrame, ref count, xd.RefMvStacks[refFrame], xd.RefMvWeights[refFrame], mvRefList, gm0, gm1, miRow, miCol,
            ext.ModeContext);
        ext.RefMvCount[refFrame] = count;
    }

    /// <summary>av1_copy_usable_ref_mv_stack_and_weight.</summary>
    public static void CopyUsableRefMvStackAndWeight(AomMacroblockD xd, AomMbmiExtInter ext, int refFrame)
    {
        Array.Copy(xd.RefMvWeights[refFrame], ext.Weight[refFrame], USABLE_REF_MV_STACK_SIZE);
        Array.Copy(xd.RefMvStacks[refFrame], ext.RefMvStack[refFrame], USABLE_REF_MV_STACK_SIZE);
    }

    /// <summary>av1_find_best_ref_mvs.</summary>
    public static void FindBestRefMvs(bool allowHp, AomMv[] mvlist, out AomMv nearest, out AomMv near, bool isInteger)
    {
        for (int i = 0; i < MAX_MV_REF_CANDIDATES; ++i) mvlist[i] = AomInter.LowerMvPrecision(mvlist[i], allowHp, isInteger);
        nearest = mvlist[0];
        near = mvlist[1];
    }

    /// <summary>av1_copy_frame_mvs: the block's motion into cm->cur_frame->mvs (8x8 units).</summary>
    public static void CopyFrameMvs(AomCommon cm, AomMbModeInfo mi, int miRow, int miCol, int xMis, int yMis)
    {
        int stride = (cm.MiCols + 1) >> 1;
        var mvs = cm.CurFrameMvs!;
        int baseIdx = (miRow >> 1) * stride + (miCol >> 1);
        xMis = (xMis + 1) >> 1;
        yMis = (yMis + 1) >> 1;
        for (int h = 0; h < yMis; h++)
        {
            for (int w = 0; w < xMis; w++)
            {
                ref var mv = ref mvs[baseIdx + h * stride + w];
                mv.RefFrame = NONE_FRAME;
                mv.Mv = default;
                for (int idx = 0; idx < 2; ++idx)
                {
                    int refFrame = idx == 0 ? mi.RefFrame0 : mi.RefFrame1;
                    if (refFrame > INTRA_FRAME)
                    {
                        if (cm.RefFrameSide[refFrame] != 0) continue;
                        var m = idx == 0 ? mi.Mv0 : mi.Mv1;
                        if (Math.Abs(m.Row) > REFMVS_LIMIT || Math.Abs(m.Col) > REFMVS_LIMIT) continue;
                        mv.RefFrame = (sbyte)refFrame;
                        mv.Mv = m;
                    }
                }
            }
        }
    }

    /// <summary>av1_setup_frame_sign_bias.</summary>
    public static void SetupFrameSignBias(AomCommon cm)
    {
        for (int refFrame = LAST_FRAME; refFrame <= ALTREF_FRAME; ++refFrame)
        {
            var buf = cm.RefBufs[refFrame];
            if (cm.EnableOrderHint && buf != null)
                cm.RefFrameSignBias[refFrame] = cm.RelativeDist(buf.OrderHint, cm.OrderHint) <= 0 ? 0 : 1;
            else cm.RefFrameSignBias[refFrame] = 0;
        }
    }

    /// <summary>av1_calculate_ref_frame_side.</summary>
    public static void CalculateRefFrameSide(AomCommon cm)
    {
        Array.Clear(cm.RefFrameSide);
        if (!cm.EnableOrderHint) return;
        int cur = cm.OrderHint;
        for (int refFrame = LAST_FRAME; refFrame <= ALTREF_FRAME; refFrame++)
        {
            var buf = cm.RefBufs[refFrame];
            int orderHint = buf != null ? buf.OrderHint : 0;
            if (cm.RelativeDist(orderHint, cur) > 0) cm.RefFrameSide[refFrame] = 1;
            else if (orderHint == cur) cm.RefFrameSide[refFrame] = -1;
        }
    }

    private static bool GetBlockPosition(AomCommon cm, out int miR, out int miC, int blkRow, int blkCol, AomMv mv, int signBias)
    {
        miR = miC = 0;
        int baseBlkRow = (blkRow >> 3) << 3, baseBlkCol = (blkCol >> 3) << 3;
        int rowOffset = mv.Row >= 0 ? mv.Row >> (4 + 2) : -((-mv.Row) >> (4 + 2));
        int colOffset = mv.Col >= 0 ? mv.Col >> (4 + 2) : -((-mv.Col) >> (4 + 2));
        int row = signBias == 1 ? blkRow - rowOffset : blkRow + rowOffset;
        int col = signBias == 1 ? blkCol - colOffset : blkCol + colOffset;
        if (row < 0 || row >= (cm.MiRows >> 1) || col < 0 || col >= (cm.MiCols >> 1)) return false;
        if (row < baseBlkRow - (MAX_OFFSET_HEIGHT >> 3) || row >= baseBlkRow + 8 + (MAX_OFFSET_HEIGHT >> 3) ||
            col < baseBlkCol - (MAX_OFFSET_WIDTH >> 3) || col >= baseBlkCol + 8 + (MAX_OFFSET_WIDTH >> 3))
            return false;
        miR = row;
        miC = col;
        return true;
    }

    private static bool MotionFieldProjection(AomCommon cm, int startFrame, int dir)
    {
        var tplMvsBase = cm.TplMvs!;
        Span<int> refOffset = stackalloc int[REF_FRAMES];
        var startBuf = cm.RefBufs[startFrame];
        if (startBuf == null) return false;
        if (startBuf.FrameType == KEY_FRAME || startBuf.FrameType == INTRA_ONLY_FRAME) return false;
        if (startBuf.MiRows != cm.MiRows || startBuf.MiCols != cm.MiCols) return false;
        int startOrderHint = startBuf.OrderHint;
        int curOrderHint = cm.OrderHint;
        int startToCurrent = cm.RelativeDist(startOrderHint, curOrderHint);
        for (int rf = LAST_FRAME; rf <= INTER_REFS_PER_FRAME; ++rf)
            refOffset[rf] = cm.RelativeDist(startOrderHint, startBuf.RefOrderHints[rf - LAST_FRAME]);
        if (dir == 2) startToCurrent = -startToCurrent;
        var mvRefBase = startBuf.Mvs!;
        int mvsRows = (cm.MiRows + 1) >> 1, mvsCols = (cm.MiCols + 1) >> 1;
        for (int blkRow = 0; blkRow < mvsRows; ++blkRow)
            for (int blkCol = 0; blkCol < mvsCols; ++blkCol)
            {
                var mvRef = mvRefBase[blkRow * mvsCols + blkCol];
                var fwdMv = mvRef.Mv;
                if (mvRef.RefFrame > INTRA_FRAME)
                {
                    int refFrameOffset = refOffset[mvRef.RefFrame];
                    bool posValid = Math.Abs(refFrameOffset) <= MAX_FRAME_DISTANCE && refFrameOffset > 0 &&
                        Math.Abs(startToCurrent) <= MAX_FRAME_DISTANCE;
                    int miR = 0, miC = 0;
                    if (posValid)
                    {
                        var thisMv = GetMvProjection(fwdMv, startToCurrent, refFrameOffset);
                        posValid = GetBlockPosition(cm, out miR, out miC, blkRow, blkCol, thisMv, dir >> 1);
                    }
                    if (posValid)
                    {
                        int miOffset = miR * (cm.MiStride >> 1) + miC;
                        tplMvsBase[miOffset].Mfmv0 = fwdMv;
                        tplMvsBase[miOffset].RefFrameOffset = (sbyte)refFrameOffset;
                    }
                }
            }
        return true;
    }

    /// <summary>av1_setup_motion_field.</summary>
    public static void SetupMotionField(AomCommon cm)
    {
        if (!cm.EnableOrderHint) return;
        int size = ((cm.MiRows + MAX_MIB_SIZE) >> 1) * (cm.MiStride >> 1);
        if (cm.TplMvs == null || cm.TplMvs.Length < size) cm.TplMvs = new AomTplMvRef[size];
        var tpl = cm.TplMvs;
        for (int idx = 0; idx < size; ++idx)
        {
            tpl[idx].Mfmv0 = new AomMv(-32768, -32768);   // INVALID_MV
            tpl[idx].RefFrameOffset = 0;
        }
        int curOrderHint = cm.OrderHint;
        Span<int> refOrderHint = stackalloc int[INTER_REFS_PER_FRAME];
        for (int refFrame = LAST_FRAME; refFrame <= ALTREF_FRAME; refFrame++)
        {
            var buf = cm.RefBufs[refFrame];
            refOrderHint[refFrame - LAST_FRAME] = buf != null ? buf.OrderHint : 0;
        }
        int refStamp = MFMV_STACK_SIZE - 1;
        if (cm.RefBufs[LAST_FRAME] != null)
        {
            int altOfLstOrderHint = cm.RefBufs[LAST_FRAME]!.RefOrderHints[ALTREF_FRAME - LAST_FRAME];
            bool isLstOverlay = altOfLstOrderHint == refOrderHint[GOLDEN_FRAME - LAST_FRAME];
            if (!isLstOverlay) MotionFieldProjection(cm, LAST_FRAME, 2);
            --refStamp;
        }
        if (cm.RelativeDist(refOrderHint[BWDREF_FRAME - LAST_FRAME], curOrderHint) > 0)
            if (MotionFieldProjection(cm, BWDREF_FRAME, 0)) --refStamp;
        if (cm.RelativeDist(refOrderHint[ALTREF2_FRAME - LAST_FRAME], curOrderHint) > 0)
            if (MotionFieldProjection(cm, ALTREF2_FRAME, 0)) --refStamp;
        if (cm.RelativeDist(refOrderHint[ALTREF_FRAME - LAST_FRAME], curOrderHint) > 0 && refStamp >= 0)
            if (MotionFieldProjection(cm, ALTREF_FRAME, 0)) --refStamp;
        if (refStamp >= 0) MotionFieldProjection(cm, LAST2_FRAME, 2);
    }
}
