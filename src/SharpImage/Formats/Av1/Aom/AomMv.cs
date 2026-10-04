using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>MV / FULLPEL_MV / int_mv (av1/common/mv.h): a (row, col) pair of int16 (1/8 pel for MV, pels for
/// FULLPEL_MV); AsInt is the union's as_int (row in the low half, as on the little-endian targets libaom runs on).</summary>
internal struct AomMv : IEquatable<AomMv>
{
    public short Row, Col;

    public AomMv(int row, int col) { Row = (short)row; Col = (short)col; }

    /// <summary>INVALID_MV (0x80008000).</summary>
    public static readonly AomMv Invalid = new(-32768, -32768);

    public readonly uint AsInt => (ushort)Row | ((uint)(ushort)Col << 16);
    public readonly bool Equals(AomMv o) => Row == o.Row && Col == o.Col;
    public override readonly bool Equals(object? obj) => obj is AomMv m && Equals(m);
    public override readonly int GetHashCode() => (int)AsInt;
    public override readonly string ToString() => $"{Row} {Col}";

    /// <summary>get_fullmv_from_mv: GET_MV_RAWPEL (rounds half away from zero).</summary>
    public readonly AomMv ToFullMv() => new(GetMvRawpel(Row), GetMvRawpel(Col));
    /// <summary>get_mv_from_fullmv: GET_MV_SUBPEL.</summary>
    public readonly AomMv ToMv() => new(Row * 8, Col * 8);

    /// <summary>GET_MV_RAWPEL(x) = (x + 3 + (x >= 0)) >> 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMvRawpel(int x) => (x + 3 + (x >= 0 ? 1 : 0)) >> 3;
}

/// <summary>CANDIDATE_MV.</summary>
internal struct AomCandidateMv
{
    public AomMv ThisMv, CompMv;
}

/// <summary>FullMvLimits.</summary>
internal struct AomFullMvLimits
{
    public int ColMin, ColMax, RowMin, RowMax;
    public override readonly string ToString() => $"{ColMin} {ColMax} {RowMin} {RowMax}";
}

/// <summary>MB_MODE_INFO_EXT for the reference the all-intra encoder searches (INTRA_FRAME, i.e. intrabc): the
/// candidate stack, weights, count, global mv and mode context of ref_frame 0.</summary>
internal sealed class AomMbmiExt
{
    public const int MaxRefMvStackSize = 8, UsableRefMvStackSize = 4;
    public readonly AomCandidateMv[] RefMvStack = new AomCandidateMv[MaxRefMvStackSize];
    public readonly ushort[] Weight = new ushort[MaxRefMvStackSize];
    public byte RefMvCount;
    public AomMv GlobalMv;        // global_mvs[INTRA_FRAME]
    public short ModeContext;     // mode_context[INTRA_FRAME]
}

/// <summary>MB_MODE_INFO_EXT_FRAME: the frame-level copy of a block's MB_MODE_INFO_EXT for its reference type (the
/// bitstream writer reads ref_mv_stack[0].this_mv as an intrabc block's DV reference).</summary>
internal sealed class AomMbmiExtFrame
{
    public readonly AomCandidateMv[] RefMvStack = new AomCandidateMv[AomMbmiExt.UsableRefMvStackSize];
    public readonly ushort[] Weight = new ushort[AomMbmiExt.UsableRefMvStackSize];
    public byte RefMvCount;
    public AomMv GlobalMv;        // global_mvs[INTRA_FRAME] (the other references are never searched)
    public short ModeContext;
    public readonly ushort[] CbOffset = new ushort[2];

    /// <summary>The zero state of a new one.</summary>
    public void Reset()
    {
        Array.Clear(RefMvStack); Array.Clear(Weight); Array.Clear(CbOffset);
        RefMvCount = 0; GlobalMv = default; ModeContext = 0;
    }

    /// <summary>av1_copy_mbmi_ext_to_mbmi_ext_frame (ref frame type INTRA_FRAME).</summary>
    public void CopyFrom(AomMbmiExt e)
    {
        Array.Copy(e.RefMvStack, RefMvStack, AomMbmiExt.UsableRefMvStackSize);
        Array.Copy(e.Weight, Weight, AomMbmiExt.UsableRefMvStackSize);
        ModeContext = e.ModeContext;
        RefMvCount = e.RefMvCount;
        GlobalMv = e.GlobalMv;
    }

    /// <summary>av1_copy_tree_context's mbmi_ext_best copy (struct assignment, cb_offset included).</summary>
    public void CopyFrom(AomMbmiExtFrame s)
    {
        Array.Copy(s.RefMvStack, RefMvStack, RefMvStack.Length);
        Array.Copy(s.Weight, Weight, Weight.Length);
        RefMvCount = s.RefMvCount; GlobalMv = s.GlobalMv; ModeContext = s.ModeContext;
        CbOffset[0] = s.CbOffset[0]; CbOffset[1] = s.CbOffset[1];
    }

    /// <summary>copy_mbmi_ext_frame_to_mbmi_ext (ref frame type INTRA_FRAME).</summary>
    public void CopyTo(AomMbmiExt e)
    {
        Array.Copy(RefMvStack, e.RefMvStack, AomMbmiExt.UsableRefMvStackSize);
        Array.Copy(Weight, e.Weight, AomMbmiExt.UsableRefMvStackSize);
        e.ModeContext = ModeContext;
        e.RefMvCount = RefMvCount;
        e.GlobalMv = GlobalMv;
    }
}

/// <summary>IntraBCMVCosts: the DV joint and per-component costs (av1_fill_dv_costs from the ndvc CDFs,
/// MV_SUBPEL_NONE). Comp[c][MvMax + v] is the cost of component value v.</summary>
internal sealed class AomDvCosts
{
    public readonly int[] JointMv = new int[AomMvCost.MvJoints];
    public readonly int[][] DvCosts = { new int[AomMvCost.MvVals], new int[AomMvCost.MvVals] };
}

// Port of libaom 3.14.1 av1/encoder/encodemv.c (av1_build_nmv_cost_table, av1_update_mv_stats) and mcomp.c's mv
// cost functions (mv_cost, av1_mv_bit_cost, mv_err_cost, mvsad_err_cost) and entropymv.h helpers.
internal static class AomMvCost
{
    public const int MvJoints = 4, MvClasses = 11, Class0Bits = 1, Class0Size = 1 << Class0Bits, MvOffsetBits = MvClasses + Class0Bits - 2,
        MvFpSize = 4, MvMaxBits = MvClasses + Class0Bits + 2, MvMax = (1 << MvMaxBits) - 1, MvVals = (MvMax << 1) + 1,
        MvInUseBits = 14, MvUpp = 1 << MvInUseBits, MvLow = -(1 << MvInUseBits);
    public const int MV_JOINT_ZERO = 0, MV_JOINT_HNZVZ = 1, MV_JOINT_HZVNZ = 2, MV_JOINT_HNZVNZ = 3;
    public const int MV_SUBPEL_NONE = -1, MV_SUBPEL_LOW_PRECISION = 0, MV_SUBPEL_HIGH_PRECISION = 1;
    public const int MV_COST_WEIGHT_SUB = 120;
    private const int PixelTransformErrorScale = 4;

    /// <summary>av1_get_mv_joint.</summary>
    public static int GetMvJoint(AomMv mv)
    {
        if (mv.Row == 0) return mv.Col == 0 ? MV_JOINT_ZERO : MV_JOINT_HNZVZ;
        return mv.Col == 0 ? MV_JOINT_HZVNZ : MV_JOINT_HNZVNZ;
    }

    public static bool MvJointVertical(int j) => j == MV_JOINT_HZVNZ || j == MV_JOINT_HNZVNZ;
    public static bool MvJointHorizontal(int j) => j == MV_JOINT_HNZVZ || j == MV_JOINT_HNZVNZ;

    /// <summary>av1_get_mv_class: the class of z (&gt;= 0) and its offset from the class base.</summary>
    public static int GetMvClass(int z, out int offset)
    {
        int zz = z >> 3;
        int c = zz == 0 ? 0 : BitOperations.Log2((uint)zz);   // av1_log_in_base_2
        offset = z - (c != 0 ? Class0Size << (c + 2) : 0);    // av1_mv_class_base
        return c;
    }

    /// <summary>av1_build_nmv_component_cost_table; mvcost is centred at center (MV_MAX).</summary>
    public static void BuildComponentCostTable(int[] mvcost, int center, Av1CdfMvComponent mvcomp, int precision)
    {
        Span<int> signCost = stackalloc int[2], classCost = stackalloc int[MvClasses], class0Cost = stackalloc int[Class0Size];
        Span<int> bitsCost = stackalloc int[MvOffsetBits * 2];
        Span<int> class0FpCost = stackalloc int[Class0Size * MvFpSize], fpCost = stackalloc int[MvFpSize];
        Span<int> class0HpCost = stackalloc int[2], hpCost = stackalloc int[2];
        class0FpCost.Clear(); fpCost.Clear(); class0HpCost.Clear(); hpCost.Clear();
        AomCost.CostTokensFromCdf(signCost, mvcomp.Sign, 2);
        AomCost.CostTokensFromCdf(classCost, mvcomp.Classes, MvClasses);
        AomCost.CostTokensFromCdf(class0Cost, mvcomp.Class0, Class0Size);
        for (int i = 0; i < MvOffsetBits; ++i) AomCost.CostTokensFromCdf(bitsCost.Slice(i * 2, 2), mvcomp.ClassN[i], 2);
        if (precision > MV_SUBPEL_NONE)
        {
            for (int i = 0; i < Class0Size; ++i) AomCost.CostTokensFromCdf(class0FpCost.Slice(i * MvFpSize, MvFpSize), mvcomp.Class0Fp[i], MvFpSize);
            AomCost.CostTokensFromCdf(fpCost, mvcomp.ClassNFp, MvFpSize);
        }
        if (precision > MV_SUBPEL_LOW_PRECISION)
        {
            AomCost.CostTokensFromCdf(class0HpCost, mvcomp.Class0Hp, 2);
            AomCost.CostTokensFromCdf(hpCost, mvcomp.ClassNHp, 2);
        }

        Span<int> costSwap = stackalloc int[MvOffsetBits];
        costSwap.Clear();
        int negateSign = signCost[1] - signCost[0];
        for (int i = 1; i < MvOffsetBits; ++i)
        {
            costSwap[i] = bitsCost[(i - 1) * 2 + 1];
            if (i > Class0Bits) costSwap[i] -= classCost[i - Class0Bits];
        }
        int v;
        for (int o = 0; o < MvFpSize; ++o)
            for (int hp = 0; hp < 2; ++hp)
            {
                v = 2 * o + hp + 1;
                mvcost[center + v] = fpCost[o] + hpCost[hp] + signCost[0];
            }
        mvcost[center] = 0;
        int mantissa;
        for (int i = 0; i < MvOffsetBits; ++i)
        {
            int exponent = (2 * MvFpSize) << i;
            int cls = 0;
            if (i >= Class0Bits) cls = classCost[i - Class0Bits + 1];
            mantissa = 0;
            for (int j = 0; j <= i; ++j)
            {
                for (; mantissa < (2 * MvFpSize) << j; ++mantissa)
                {
                    int cost = mvcost[center + mantissa + 1] + cls + costSwap[j];
                    v = exponent + mantissa + 1;
                    mvcost[center + v] = cost;
                    mvcost[center - v] = cost + negateSign;
                }
                costSwap[j] += bitsCost[i * 2 + 0];
            }
        }
        {
            int exponent = (2 * MvFpSize) << MvOffsetBits;
            int cls = classCost[MvClasses - 1];
            mantissa = 0;
            for (int j = 0; j < MvOffsetBits; ++j)
                for (; mantissa < (2 * MvFpSize) << j; ++mantissa)
                {
                    int cost = mvcost[center + mantissa + 1] + cls + costSwap[j];
                    v = exponent + mantissa + 1;
                    mvcost[center + v] = cost;
                    mvcost[center - v] = cost + negateSign;
                }
            int costSwapHi = bitsCost[(MvOffsetBits - 1) * 2 + 1] - classCost[MvClasses - 2];
            for (; mantissa < exponent - 1; ++mantissa)
            {
                int cost = mvcost[center + mantissa + 1] + cls + costSwapHi;
                v = exponent + mantissa + 1;
                mvcost[center + v] = cost;
                mvcost[center - v] = cost + negateSign;
            }
        }
        for (int i = 0; i < Class0Size; ++i)
        {
            int top = i * 2 * MvFpSize;
            for (int o = 0; o < MvFpSize; ++o)
            {
                int cost = class0FpCost[i * MvFpSize + o] + classCost[0] + class0Cost[i];
                for (int hp = 0; hp < 2; ++hp)
                {
                    v = top + 2 * o + hp + 1;
                    mvcost[center + v] = cost + class0HpCost[hp] + signCost[0];
                    mvcost[center - v] = cost + class0HpCost[hp] + signCost[1];
                }
            }
        }
    }

    /// <summary>av1_fill_dv_costs (av1_build_nmv_cost_table with MV_SUBPEL_NONE).</summary>
    public static void FillDvCosts(Av1CdfMvContext ndvc, AomDvCosts dv)
    {
        AomCost.CostTokensFromCdf(dv.JointMv, ndvc.Joint, MvJoints);
        BuildComponentCostTable(dv.DvCosts[0], MvMax, ndvc.Comp0, MV_SUBPEL_NONE);
        BuildComponentCostTable(dv.DvCosts[1], MvMax, ndvc.Comp1, MV_SUBPEL_NONE);
    }

    /// <summary>mv_cost: joint cost plus both component costs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MvCostOf(AomMv mv, int[] jointCost, int[][] compCost)
        => jointCost[GetMvJoint(mv)] + compCost[0][MvMax + mv.Row] + compCost[1][MvMax + mv.Col];

    /// <summary>av1_mv_bit_cost.</summary>
    public static int MvBitCost(AomMv mv, AomMv refMv, int[] mvjcost, int[][] mvcost, int weight)
    {
        var diff = new AomMv(mv.Row - refMv.Row, mv.Col - refMv.Col);
        return (MvCostOf(diff, mvjcost, mvcost) * weight + 64) >> 7;
    }

    /// <summary>mv_err_cost (MV_COST_ENTROPY with a cost table).</summary>
    public static int MvErrCost(AomMv mv, AomMv refMv, int[] mvjcost, int[][] mvcost, int errorPerBit)
    {
        var diff = new AomMv(mv.Row - refMv.Row, mv.Col - refMv.Col);
        const int shift = AomRd.RdDivBits + AomCost.ProbCostShift - AomRd.RdEpbShift + PixelTransformErrorScale;
        return (int)(((long)MvCostOf(diff, mvjcost, mvcost) * errorPerBit + (1L << (shift - 1))) >> shift);
    }

    /// <summary>mvsad_err_cost (MV_COST_ENTROPY) of full-pel mv against full-pel ref.</summary>
    public static int MvSadErrCost(AomMv mv, AomMv fullRefMv, int[] mvjcost, int[][] mvcost, int sadPerBit)
    {
        var diff = new AomMv((mv.Row - fullRefMv.Row) * 8, (mv.Col - fullRefMv.Col) * 8);
        return (int)(((uint)MvCostOf(diff, mvjcost, mvcost) * (uint)sadPerBit + (1u << (AomCost.ProbCostShift - 1))) >> AomCost.ProbCostShift);
    }

    // update_mv_component_stats
    private static void UpdateMvComponentStats(int comp, Av1CdfMvComponent mvcomp, int precision)
    {
        int sign = comp < 0 ? 1 : 0;
        int mag = sign != 0 ? -comp : comp;
        int mvClass = GetMvClass(mag - 1, out int offset);
        int d = offset >> 3, fr = (offset >> 1) & 3, hp = offset & 1;
        AomCdf.Update(mvcomp.Sign, sign, 2);
        AomCdf.Update(mvcomp.Classes, mvClass, MvClasses);
        if (mvClass == 0) AomCdf.Update(mvcomp.Class0, d, Class0Size);
        else
        {
            int n = mvClass + Class0Bits - 1;
            for (int i = 0; i < n; ++i) AomCdf.Update(mvcomp.ClassN[i], (d >> i) & 1, 2);
        }
        if (precision > MV_SUBPEL_NONE) AomCdf.Update(mvClass == 0 ? mvcomp.Class0Fp[d] : mvcomp.ClassNFp, fr, MvFpSize);
        if (precision > MV_SUBPEL_LOW_PRECISION) AomCdf.Update(mvClass == 0 ? mvcomp.Class0Hp : mvcomp.ClassNHp, hp, 2);
    }

    /// <summary>av1_update_mv_stats.</summary>
    public static void UpdateMvStats(AomMv mv, AomMv refMv, Av1CdfMvContext mvctx, int precision)
    {
        var diff = new AomMv(mv.Row - refMv.Row, mv.Col - refMv.Col);
        int j = GetMvJoint(diff);
        AomCdf.Update(mvctx.Joint, j, MvJoints);
        if (MvJointVertical(j)) UpdateMvComponentStats(diff.Row, mvctx.Comp0, precision);
        if (MvJointHorizontal(j)) UpdateMvComponentStats(diff.Col, mvctx.Comp1, precision);
    }
}
