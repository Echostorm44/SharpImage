using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>blockd.h / enums.h / mvref_common.h / mv.h inter-mode helpers.</summary>
internal static class AomInter
{
    public static bool IsCompRefAllowed(int bsize) => Math.Min(BlockSizeWide[bsize], BlockSizeHigh[bsize]) >= 8;
    public static bool IsInterMode(int mode) => mode >= INTER_MODE_START && mode < INTER_MODE_END;
    public static bool IsInterSinglerefMode(int mode) => mode >= SINGLE_INTER_MODE_START && mode < SINGLE_INTER_MODE_END;
    public static bool IsInterCompoundMode(int mode) => mode >= COMP_INTER_MODE_START && mode < COMP_INTER_MODE_END;

    private static readonly int[] CompoundRef0Lut =
    {
        DC_PRED, V_PRED, H_PRED, D45_PRED, D135_PRED, D113_PRED, D157_PRED, D203_PRED, D67_PRED, SMOOTH_PRED, SMOOTH_V_PRED,
        SMOOTH_H_PRED, PAETH_PRED, NEARESTMV, NEARMV, GLOBALMV, NEWMV, NEARESTMV, NEARMV, NEARESTMV, NEWMV, NEARMV, NEWMV, GLOBALMV, NEWMV,
    };
    private static readonly int[] CompoundRef1Lut =
    {
        MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT,
        MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT, MB_MODE_COUNT,
        NEARESTMV, NEARMV, NEWMV, NEARESTMV, NEWMV, NEARMV, GLOBALMV, NEWMV,
    };
    public static int CompoundRef0Mode(int mode) => CompoundRef0Lut[mode];
    public static int CompoundRef1Mode(int mode) => CompoundRef1Lut[mode];
    /// <summary>get_single_mode (rdopt.c).</summary>
    public static int GetSingleMode(int mode, int refIdx) => refIdx != 0 ? CompoundRef1Mode(mode) : CompoundRef0Mode(mode);
    public static bool HaveNearmvInInterMode(int mode) => mode == NEARMV || mode == NEAR_NEARMV || mode == NEAR_NEWMV || mode == NEW_NEARMV;
    public static bool HaveNewmvInInterMode(int mode)
        => mode == NEWMV || mode == NEW_NEWMV || mode == NEAREST_NEWMV || mode == NEW_NEARESTMV || mode == NEAR_NEWMV || mode == NEW_NEARMV;
    public static bool IsMaskedCompoundType(int type) => type == COMPOUND_WEDGE || type == COMPOUND_DIFFWTD;

    public static bool HasUniCompRefs(AomMbModeInfo m) => m.HasSecondRef && !((m.RefFrame0 >= BWDREF_FRAME) ^ (m.RefFrame1 >= BWDREF_FRAME));
    private static readonly int[] CompRef0Lut = { LAST_FRAME, LAST_FRAME, LAST_FRAME, BWDREF_FRAME, LAST2_FRAME, LAST2_FRAME, LAST3_FRAME, BWDREF_FRAME, ALTREF2_FRAME };
    private static readonly int[] CompRef1Lut = { LAST2_FRAME, LAST3_FRAME, GOLDEN_FRAME, ALTREF_FRAME, LAST3_FRAME, GOLDEN_FRAME, GOLDEN_FRAME, ALTREF2_FRAME, ALTREF_FRAME };
    public static int CompRef0(int refIdx) => CompRef0Lut[refIdx];
    public static int CompRef1(int refIdx) => CompRef1Lut[refIdx];

    public static bool IsGlobalMvBlock(AomMbModeInfo m, int type)
        => (m.Mode == GLOBALMV || m.Mode == GLOBAL_GLOBALMV) && type > TRANSLATION && Math.Min(BlockSizeWide[m.Bsize], BlockSizeHigh[m.Bsize]) >= 8;

    public static bool IsInterintraMode(AomMbModeInfo m) => m.RefFrame0 > INTRA_FRAME && m.RefFrame1 == INTRA_FRAME;
    public static bool IsInterintraAllowedBsize(int bsize) => bsize >= BLOCK_8X8 && bsize <= BLOCK_32X32;
    public static bool IsInterintraAllowedMode(int mode) => mode >= SINGLE_INTER_MODE_START && mode < SINGLE_INTER_MODE_END;
    public static bool IsInterintraAllowedRef(int rf0, int rf1) => rf0 > INTRA_FRAME && rf1 <= INTRA_FRAME;
    public static bool IsInterintraAllowed(AomMbModeInfo m)
        => IsInterintraAllowedBsize(m.Bsize) && IsInterintraAllowedMode(m.Mode) && IsInterintraAllowedRef(m.RefFrame0, m.RefFrame1);
    public static bool IsInterintraPred(AomMbModeInfo m) => m.RefFrame0 > INTRA_FRAME && m.RefFrame1 == INTRA_FRAME && IsInterintraAllowed(m);
    public static bool IsMotionVariationAllowedBsize(int bsize) => Math.Min(BlockSizeWide[bsize], BlockSizeHigh[bsize]) >= 8;
    public static readonly int[] MaxNeighborObmc = { 0, 1, 2, 3, 4, 4 };

    /// <summary>av1_ref_frame_type.</summary>
    public static int RefFrameType(int rf0, int rf1)
    {
        if (rf1 > INTRA_FRAME)
        {
            int uni = GetUniCompRefIdx(rf0, rf1);
            if (uni >= 0) return REF_FRAMES + FWD_REFS * BWD_REFS + uni;
            return REF_FRAMES + (rf0 - LAST_FRAME) + (rf1 - BWDREF_FRAME) * FWD_REFS;   // FWD_RF_OFFSET / BWD_RF_OFFSET
        }
        return rf0;
    }

    /// <summary>get_uni_comp_ref_idx.</summary>
    public static int GetUniCompRefIdx(int rf0, int rf1)
    {
        if (rf1 <= INTRA_FRAME) return -1;
        if (rf0 < BWDREF_FRAME && rf1 >= BWDREF_FRAME) return -1;
        for (int refIdx = 0; refIdx < TOTAL_UNIDIR_COMP_REFS; ++refIdx)
            if (rf0 == CompRef0(refIdx) && rf1 == CompRef1(refIdx)) return refIdx;
        return -1;
    }

    private static readonly int[,] RefFrameMapComp =
    {
        { LAST_FRAME, BWDREF_FRAME }, { LAST2_FRAME, BWDREF_FRAME }, { LAST3_FRAME, BWDREF_FRAME }, { GOLDEN_FRAME, BWDREF_FRAME },
        { LAST_FRAME, ALTREF2_FRAME }, { LAST2_FRAME, ALTREF2_FRAME }, { LAST3_FRAME, ALTREF2_FRAME }, { GOLDEN_FRAME, ALTREF2_FRAME },
        { LAST_FRAME, ALTREF_FRAME }, { LAST2_FRAME, ALTREF_FRAME }, { LAST3_FRAME, ALTREF_FRAME }, { GOLDEN_FRAME, ALTREF_FRAME },
        { LAST_FRAME, LAST2_FRAME }, { LAST_FRAME, LAST3_FRAME }, { LAST_FRAME, GOLDEN_FRAME }, { BWDREF_FRAME, ALTREF_FRAME },
        { LAST2_FRAME, LAST3_FRAME }, { LAST2_FRAME, GOLDEN_FRAME }, { LAST3_FRAME, GOLDEN_FRAME }, { BWDREF_FRAME, ALTREF2_FRAME },
        { ALTREF2_FRAME, ALTREF_FRAME },
    };

    /// <summary>av1_set_ref_frame.</summary>
    public static (int Rf0, int Rf1) SetRefFrame(int refFrameType)
        => refFrameType >= REF_FRAMES ? (RefFrameMapComp[refFrameType - REF_FRAMES, 0], RefFrameMapComp[refFrameType - REF_FRAMES, 1])
            : (refFrameType, NONE_FRAME);

    private static readonly int[,] CompoundModeCtxMap = { { 0, 1, 1, 1, 1 }, { 1, 2, 3, 4, 4 }, { 4, 4, 5, 6, 7 } };

    /// <summary>av1_mode_context_analyzer.</summary>
    public static int ModeContextAnalyzer(short[] modeContext, int rf0, int rf1)
    {
        int refFrame = RefFrameType(rf0, rf1);
        if (rf1 <= INTRA_FRAME) return modeContext[refFrame];
        int newmvCtx = modeContext[refFrame] & NEWMV_CTX_MASK;
        int refmvCtx = (modeContext[refFrame] >> REFMV_OFFSET) & REFMV_CTX_MASK;
        return CompoundModeCtxMap[refmvCtx >> 1, Math.Min(newmvCtx, COMP_NEWMV_CTXS - 1)];
    }

    /// <summary>av1_drl_ctx.</summary>
    public static int DrlCtx(ushort[] refMvWeight, int refIdx)
    {
        if (refMvWeight[refIdx] >= REF_CAT_LEVEL && refMvWeight[refIdx + 1] >= REF_CAT_LEVEL) return 0;
        if (refMvWeight[refIdx] >= REF_CAT_LEVEL && refMvWeight[refIdx + 1] < REF_CAT_LEVEL) return 1;
        if (refMvWeight[refIdx] < REF_CAT_LEVEL && refMvWeight[refIdx + 1] < REF_CAT_LEVEL) return 2;
        return 0;
    }

    /// <summary>get_relative_dist.</summary>
    public static int GetRelativeDist(bool enableOrderHint, int orderHintBits, int a, int b)
    {
        if (!enableOrderHint) return 0;
        int diff = a - b;
        int m = 1 << (orderHintBits - 1);
        diff = (diff & (m - 1)) - (diff & m);
        return diff;
    }

    /// <summary>integer_mv_precision.</summary>
    public static AomMv IntegerMvPrecision(AomMv mv)
    {
        int row = mv.Row, col = mv.Col;
        int mod = row % 8;
        if (mod != 0)
        {
            row -= mod;
            if (Math.Abs(mod) > 4) row += mod > 0 ? 8 : -8;
        }
        mod = col % 8;
        if (mod != 0)
        {
            col -= mod;
            if (Math.Abs(mod) > 4) col += mod > 0 ? 8 : -8;
        }
        return new AomMv(row, col);
    }

    /// <summary>lower_mv_precision.</summary>
    public static AomMv LowerMvPrecision(AomMv mv, bool allowHp, bool isInteger)
    {
        if (isInteger) return IntegerMvPrecision(mv);
        if (!allowHp)
        {
            int row = mv.Row, col = mv.Col;
            if ((row & 1) != 0) row += row > 0 ? -1 : 1;
            if ((col & 1) != 0) col += col > 0 ? -1 : 1;
            return new AomMv(row, col);
        }
        return mv;
    }

    public static int RoundPowerOfTwoSigned(int value, int n) => value < 0 ? -((-value + (1 << (n - 1))) >> n) : (value + (1 << (n - 1))) >> n;

    /// <summary>convert_to_trans_prec.</summary>
    private static int ConvertToTransPrec(bool allowHp, int coor)
        => allowHp ? RoundPowerOfTwoSigned(coor, WARPEDMODEL_PREC_BITS - 3) : RoundPowerOfTwoSigned(coor, WARPEDMODEL_PREC_BITS - 2) * 2;

    /// <summary>gm_get_motion_vector.</summary>
    public static AomMv GmGetMotionVector(AomWarpedMotionParams gm, bool allowHp, int bsize, int miCol, int miRow, bool isInteger)
    {
        if (gm.WmType == IDENTITY) return default;
        var mat = gm.WmMat;
        if (gm.WmType == TRANSLATION)
        {
            var r = new AomMv(mat[0] >> GM_TRANS_ONLY_PREC_DIFF, mat[1] >> GM_TRANS_ONLY_PREC_DIFF);
            return isInteger ? IntegerMvPrecision(r) : r;
        }
        int x = miCol * 4 + BlockSizeWide[bsize] / 2 - 1;   // block_center_x
        int y = miRow * 4 + BlockSizeHigh[bsize] / 2 - 1;   // block_center_y
        int xc = (mat[2] - (1 << WARPEDMODEL_PREC_BITS)) * x + mat[3] * y + mat[0];
        int yc = mat[4] * x + (mat[5] - (1 << WARPEDMODEL_PREC_BITS)) * y + mat[1];
        var res = new AomMv(ConvertToTransPrec(allowHp, yc), ConvertToTransPrec(allowHp, xc));
        return isInteger ? IntegerMvPrecision(res) : res;
    }
}
