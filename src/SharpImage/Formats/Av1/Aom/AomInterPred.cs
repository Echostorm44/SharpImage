using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMbdPlane
{
    /// <summary>pd->pre[1] (the second reference of compound prediction).</summary>
    public AomBuf2d Pre1;
    public ref AomBuf2d Pre(int i) => ref i == 0 ? ref Pre0 : ref Pre1;
}

internal sealed partial class AomMacroblockD
{
    /// <summary>xd->block_ref_scale_factors.</summary>
    public readonly AomScaleFactors?[] BlockRefScaleFactors = new AomScaleFactors?[2];
    /// <summary>xd->tmp_conv_dst [MAX_SB_SIZE * MAX_SB_SIZE] (CONV_BUF_TYPE).</summary>
    public readonly ushort[] TmpConvDst = new ushort[128 * 128];
    /// <summary>xd->seg_mask [2 * MAX_SB_SQUARE].</summary>
    public readonly byte[] SegMask = new byte[2 * 128 * 128];
    /// <summary>xd->tmp_obmc_bufs[2] (3 planes of MAX_SB_SQUARE each).</summary>
    public readonly byte[][] TmpObmcBufs = { new byte[3 * 128 * 128], new byte[3 * 128 * 128] };
    public readonly ushort[][] TmpObmcBufs16 = { new ushort[3 * 128 * 128], new ushort[3 * 128 * 128] };
    /// <summary>xd->global_motion (cm->global_motion).</summary>
    public AomWarpedMotionParams[] GlobalMotion = AomWarpedMotionParams.NewIdentitySet();
    public bool IsHbd => Bd > 8;
}

/// <summary>InterPredParams.</summary>
internal sealed class AomInterPredParams
{
    public const int TRANSLATION_PRED = 0, WARP_PRED = 1;
    public const int UNIFORM_SINGLE = 0, UNIFORM_COMP = 1, MASK_COMP = 2;
    public int Mode, CompMode;
    public readonly AomWarpedMotionParams WarpParams = new();
    public AomConvParams ConvParams;
    public AomFilter.Params FilterX, FilterY;   // interp_filter_params[0] / [1]
    public int BlockWidth, BlockHeight, PixRow, PixCol;
    public AomBuf2d RefFrameBuf;
    public int SubsamplingX, SubsamplingY;
    public AomScaleFactors Sf = null!;
    public int BitDepth;
    public bool UseHbdBuf;
    public AomInterinterCompound MaskComp;
    public int SbType;
    public bool IsIntrabc;
    public int Top, Left;
}

/// <summary>SubpelParams.</summary>
internal struct AomSubpelParams
{
    public int Xs, Ys, SubpelX, SubpelY, PosX, PosY;
}

// Port of libaom 3.14.1 av1/common/reconinter.{c,h}, reconinter_template.inc (IS_DEC 0) and
// av1/encoder/reconinter_enc.c: the encoder's inter predictor (single / compound / masked, scaled references,
// sub-8x8 chroma), OBMC, inter-intra and the wedge / smooth masks; aom_dsp's aom_blend_a64_{mask,vmask,hmask}.
internal static partial class AomInterPred
{
    private const int SUBPEL_BITS = 4, SCALE_SUBPEL_BITS = 10, SCALE_EXTRA_BITS = 6, SCALE_EXTRA_OFF = 32,
        SCALE_SUBPEL_MASK = (1 << SCALE_SUBPEL_BITS) - 1, AOM_INTERP_EXTEND = 4, AOM_BORDER_IN_PIXELS = 288,
        SCALE_SUBPEL_SHIFTS = 1 << SCALE_SUBPEL_BITS, MAX_SB_SIZE = 128, MAX_SB_SQUARE = 128 * 128;

    /// <summary>The identity scale factors (cm->sf_identity).</summary>
    public static readonly AomScaleFactors Identity = AomScaleFactors.ForFrame(16, 16, 16, 16);

    private static int LeftTopMarginScaled(int ss) => ((AOM_BORDER_IN_PIXELS >> ss) - AOM_INTERP_EXTEND) << SCALE_SUBPEL_BITS;

    /// <summary>av1_init_inter_params.</summary>
    public static void InitInterParams(AomInterPredParams p, int blockWidth, int blockHeight, int pixRow, int pixCol, int ssX, int ssY,
        int bd, bool useHbd, bool isIntrabc, AomScaleFactors sf, in AomBuf2d refBuf, uint interpFilters)
    {
        p.BlockWidth = blockWidth; p.BlockHeight = blockHeight; p.PixRow = pixRow; p.PixCol = pixCol;
        p.SubsamplingX = ssX; p.SubsamplingY = ssY; p.BitDepth = bd; p.UseHbdBuf = useHbd; p.IsIntrabc = isIntrabc;
        p.Mode = AomInterPredParams.TRANSLATION_PRED;
        p.CompMode = AomInterPredParams.UNIFORM_SINGLE;
        p.Top = -LeftTopMarginScaled(ssY);
        p.Left = -LeftTopMarginScaled(ssX);
        if (isIntrabc) throw new NotSupportedException("intrabc prediction goes through AomReconInter");
        p.FilterX = AomFilter.WithBlockSize((int)(interpFilters >> 16), blockWidth);     // x_filter
        p.FilterY = AomFilter.WithBlockSize((int)(interpFilters & 0xffff), blockHeight); // y_filter
        p.Sf = sf;
        p.RefFrameBuf = refBuf;
        p.MaskComp = default;
    }

    /// <summary>init_subpel_params.</summary>
    private static void InitSubpelParams(AomMv srcMv, AomInterPredParams p, out AomSubpelParams sp, int width, int height)
    {
        var sf = p.Sf;
        int ssx = p.SubsamplingX, ssy = p.SubsamplingY;
        int origPosY = (p.PixRow << SUBPEL_BITS) + srcMv.Row * (1 << (1 - ssy));
        int origPosX = (p.PixCol << SUBPEL_BITS) + srcMv.Col * (1 << (1 - ssx));
        int posX, posY;
        if (!sf.IsScaled)
        {
            posY = origPosY * (1 << SCALE_EXTRA_BITS);
            posX = origPosX * (1 << SCALE_EXTRA_BITS);
        }
        else
        {
            posY = sf.ScaledY(origPosY);
            posX = sf.ScaledX(origPosX);
        }
        posX += SCALE_EXTRA_OFF;
        posY += SCALE_EXTRA_OFF;
        int bottom = (height + AOM_INTERP_EXTEND) << SCALE_SUBPEL_BITS;
        int right = (width + AOM_INTERP_EXTEND) << SCALE_SUBPEL_BITS;
        posY = Math.Clamp(posY, p.Top, bottom);
        posX = Math.Clamp(posX, p.Left, right);
        sp = new AomSubpelParams
        {
            PosX = posX, PosY = posY, SubpelX = posX & SCALE_SUBPEL_MASK, SubpelY = posY & SCALE_SUBPEL_MASK, Xs = sf.XStepQ4, Ys = sf.YStepQ4,
        };
    }

    /// <summary>av1_make_inter_predictor (TRANSLATION_PRED; WARP_PRED through AomWarp).</summary>
    public static void MakeInterPredictor(AomInterPredParams p, in AomBuf2d src, int srcOff, byte[]? dst8, ushort[]? dst16, int dstOff, int dstStride,
        in AomSubpelParams sp)
    {
        if (p.Mode == AomInterPredParams.WARP_PRED)
        {
            AomWarp.WarpPlane(p, dst8, dst16, dstOff, dstStride);
            return;
        }
        bool isScaled = sp.Xs != SCALE_SUBPEL_SHIFTS || sp.Ys != SCALE_SUBPEL_SHIFTS;
        int subX = sp.SubpelX, subY = sp.SubpelY, xs = sp.Xs, ys = sp.Ys;
        if (!isScaled)
        {
            subX >>= SCALE_EXTRA_BITS; subY >>= SCALE_EXTRA_BITS; xs >>= SCALE_EXTRA_BITS; ys >>= SCALE_EXTRA_BITS;
        }
        if (p.UseHbdBuf)
            AomConvolve.FacadeHbd(src.Buf16, srcOff, src.Stride, dst16!, dstOff, dstStride, p.BlockWidth, p.BlockHeight, p.FilterX, p.FilterY,
                subX, xs, subY, ys, isScaled, ref p.ConvParams, p.BitDepth);
        else
            AomConvolve.Facade(src.Buf, srcOff, src.Stride, dst8!, dstOff, dstStride, p.BlockWidth, p.BlockHeight, p.FilterX, p.FilterY,
                subX, xs, subY, ys, isScaled, ref p.ConvParams);
    }

    /// <summary>build_one_inter_predictor (encoder: enc_calc_subpel_params).</summary>
    public static void BuildOneInterPredictor(byte[]? dst8, ushort[]? dst16, int dstOff, int dstStride, AomMv srcMv, AomInterPredParams p)
    {
        ref var pre = ref p.RefFrameBuf;
        InitSubpelParams(srcMv, p, out var sp, pre.Width, pre.Height);
        int srcOff = pre.Offset0 + (sp.PosY >> SCALE_SUBPEL_BITS) * pre.Stride + (sp.PosX >> SCALE_SUBPEL_BITS);
        if (p.CompMode == AomInterPredParams.UNIFORM_SINGLE || p.CompMode == AomInterPredParams.UNIFORM_COMP)
            MakeInterPredictor(p, pre, srcOff, dst8, dst16, dstOff, dstStride, sp);
        else MakeMaskedInterPredictor(p, pre, srcOff, dst8, dst16, dstOff, dstStride, sp);
    }

    /// <summary>is_sub8x8_inter.</summary>
    private static bool IsSub8x8Inter(AomMacroblockD xd, int plane, int bsize, bool isIntrabc, bool buildForObmc)
    {
        if (isIntrabc || buildForObmc) return false;
        var pd = xd.Plane[plane];
        bool isSub4X = BlockSizeWide[bsize] == 4 && pd.SubsamplingX != 0;
        bool isSub4Y = BlockSizeHigh[bsize] == 4 && pd.SubsamplingY != 0;
        if (!isSub4X && !isSub4Y) return false;
        int rowStart = isSub4Y ? -1 : 0, colStart = isSub4X ? -1 : 0;
        for (int row = rowStart; row <= 0; ++row)
            for (int col = colStart; col <= 0; ++col)
            {
                var m = xd.MiAt(row, col)!;
                if (!m.IsInterBlock) return false;
                if (m.UseIntrabc != 0) return false;
            }
        return true;
    }

    /// <summary>build_inter_predictors_sub8x8.</summary>
    private static void BuildInterPredictorsSub8x8(AomCommon cm, AomMacroblockD xd, int plane, AomMbModeInfo mi, int miX, int miY)
    {
        int bsize = mi.Bsize;
        var pd = xd.Plane[plane];
        int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
        int b4W = BlockSizeWide[bsize] >> ssX, b4H = BlockSizeHigh[bsize] >> ssY;
        int planeBsize = AomCfl.GetPlaneBlockSize(bsize, ssX, ssY);
        int b8W = BlockSizeWide[planeBsize], b8H = BlockSizeHigh[planeBsize];
        int rowStart = BlockSizeHigh[bsize] == 4 && ssY != 0 ? -1 : 0;
        int colStart = BlockSizeWide[bsize] == 4 && ssX != 0 ? -1 : 0;
        int preX = (miX + 4 * colStart) >> ssX, preY = (miY + 4 * rowStart) >> ssY;
        var p = new AomInterPredParams();
        int row = rowStart;
        for (int y = 0; y < b8H; y += b4H)
        {
            int col = colStart;
            for (int x = 0; x < b8W; x += b4W)
            {
                var thisMbmi = xd.MiAt(row, col)!;
                ref var dstBuf = ref pd.Dst;
                int dstOff = dstBuf.Offset + dstBuf.Stride * y + x;
                var refBuf = cm.RefBufs[thisMbmi.RefFrame0]!;
                var sf = cm.RefScaleFactors[thisMbmi.RefFrame0]!;
                var fb = refBuf.Buf;
                var preBuf = new AomBuf2d
                {
                    Buf = fb.Buffers[plane], Buf16 = fb.Buffers16[plane], Offset0 = fb.Offsets[plane], Offset = fb.Offsets[plane],
                    Width = fb.CropWidths[1], Height = fb.CropHeights[1], Stride = fb.Strides[plane],
                };
                var mv = thisMbmi.Mv0;
                InitInterParams(p, b4W, b4H, preY + y, preX + x, ssX, ssY, xd.Bd, xd.IsHbd, mi.UseIntrabc != 0, sf, preBuf, thisMbmi.InterpFilters);
                p.ConvParams = AomConvParams.NoRound(0, plane, null, 0, false, xd.Bd);
                BuildOneInterPredictor(dstBuf.Buf, dstBuf.Buf16, dstOff, dstBuf.Stride, mv, p);
                ++col;
            }
            ++row;
        }
    }

    /// <summary>build_inter_predictors_8x8_and_bigger.</summary>
    private static void BuildInterPredictors8x8AndBigger(AomCommon cm, AomMacroblockD xd, int plane, AomMbModeInfo mi, bool buildForObmc,
        int bw, int bh, int miX, int miY)
    {
        bool isCompound = mi.HasSecondRef;
        var pd = xd.Plane[plane];
        ref var dstBuf = ref pd.Dst;
        Span<bool> isGlobal = stackalloc bool[2];
        for (int r = 0; r < 1 + (isCompound ? 1 : 0); ++r)
            isGlobal[r] = AomInter.IsGlobalMvBlock(mi, xd.GlobalMotion[r == 0 ? mi.RefFrame0 : mi.RefFrame1].WmType);
        int bsize = mi.Bsize;
        int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
        int rowStart = BlockSizeHigh[bsize] == 4 && ssY != 0 && !buildForObmc ? -1 : 0;
        int colStart = BlockSizeWide[bsize] == 4 && ssX != 0 && !buildForObmc ? -1 : 0;
        int preX = (miX + 4 * colStart) >> ssX, preY = (miY + 4 * rowStart) >> ssY;
        var p = new AomInterPredParams();
        for (int r = 0; r < 1 + (isCompound ? 1 : 0); ++r)
        {
            var sf = xd.BlockRefScaleFactors[r]!;
            var preBuf = pd.Pre(r);
            var mv = r == 0 ? mi.Mv0 : mi.Mv1;
            InitInterParams(p, bw, bh, preY, preX, ssX, ssY, xd.Bd, xd.IsHbd, false, sf, preBuf, mi.InterpFilters);
            if (isCompound) p.CompMode = AomInterPredParams.UNIFORM_COMP;
            p.ConvParams = AomConvParams.NoRound(r, plane, xd.TmpConvDst, MAX_SB_SIZE, isCompound, xd.Bd);
            DistWtdCompWeightAssign(cm, mi, ref p.ConvParams, isCompound);
            if (!buildForObmc) InitWarpParams(p, isGlobal[r], mi.MotionMode == WARPED_CAUSAL, r, xd, mi);
            if (AomInter.IsMaskedCompoundType(mi.InterinterComp.Type))
            {
                p.SbType = mi.Bsize;
                p.MaskComp = mi.InterinterComp;
                if (r == 1)
                {
                    p.ConvParams.DoAverage = 0;
                    p.CompMode = AomInterPredParams.MASK_COMP;
                }
                p.MaskComp.SegMask = xd.SegMask;
            }
            BuildOneInterPredictor(dstBuf.Buf, dstBuf.Buf16, dstBuf.Offset, dstBuf.Stride, mv, p);
        }
    }

    /// <summary>build_inter_predictors.</summary>
    public static void BuildInterPredictors(AomCommon cm, AomMacroblockD xd, int plane, AomMbModeInfo mi, bool buildForObmc, int bw, int bh,
        int miX, int miY)
    {
        if (IsSub8x8Inter(xd, plane, mi.Bsize, mi.UseIntrabc != 0, buildForObmc)) BuildInterPredictorsSub8x8(cm, xd, plane, mi, miX, miY);
        else BuildInterPredictors8x8AndBigger(cm, xd, plane, mi, buildForObmc, bw, bh, miX, miY);
    }

    private static readonly int[,] QuantDistWeight = { { 2, 3 }, { 2, 5 }, { 2, 7 }, { 1, 16 } };   // quant_dist_weight [4][2] (MAX_FRAME_DISTANCE)
    private static readonly int[,] QuantDistLookupTable = { { 9, 7 }, { 11, 5 }, { 12, 4 }, { 13, 3 } };

    /// <summary>av1_dist_wtd_comp_weight_assign.</summary>
    public static void DistWtdCompWeightAssign(AomCommon cm, AomMbModeInfo mbmi, ref AomConvParams cp, bool isCompound)
    {
        if (!isCompound || mbmi.CompoundIdx != 0)
        {
            cp.FwdOffset = 8;
            cp.BckOffset = 8;
            cp.UseDistWtdCompAvg = 0;
            return;
        }
        cp.UseDistWtdCompAvg = 1;
        var bckBuf = cm.RefBufs[mbmi.RefFrame0];
        var fwdBuf = cm.RefBufs[mbmi.RefFrame1];
        int curFrameIndex = cm.OrderHint;
        int bckFrameIndex = bckBuf != null ? bckBuf.OrderHint : 0, fwdFrameIndex = fwdBuf != null ? fwdBuf.OrderHint : 0;
        int d0 = Math.Clamp(Math.Abs(cm.RelativeDist(fwdFrameIndex, curFrameIndex)), 0, MAX_FRAME_DISTANCE);
        int d1 = Math.Clamp(Math.Abs(cm.RelativeDist(curFrameIndex, bckFrameIndex)), 0, MAX_FRAME_DISTANCE);
        int order = d0 <= d1 ? 1 : 0;
        if (d0 == 0 || d1 == 0)
        {
            cp.FwdOffset = QuantDistLookupTable[3, order];
            cp.BckOffset = QuantDistLookupTable[3, 1 - order];
            return;
        }
        int i;
        for (i = 0; i < 3; ++i)
        {
            int c0 = QuantDistWeight[i, order], c1 = QuantDistWeight[i, 1 - order];
            int d0c0 = d0 * c0, d1c1 = d1 * c1;
            if ((d0 > d1 && d0c0 < d1c1) || (d0 <= d1 && d0c0 > d1c1)) break;
        }
        cp.FwdOffset = QuantDistLookupTable[i, order];
        cp.BckOffset = QuantDistLookupTable[i, 1 - order];
    }

    /// <summary>allow_warp / av1_init_warp_params.</summary>
    private static void InitWarpParams(AomInterPredParams p, bool globalWarpAllowed, bool localWarpAllowed, int r, AomMacroblockD xd, AomMbModeInfo mi)
    {
        if (p.BlockHeight < 8 || p.BlockWidth < 8) return;
        if (xd.CurFrameForceIntegerMv) return;
        if (p.Sf.IsScaled) return;
        p.WarpParams.CopyFrom(AomWarpedMotionParams.Default);
        var gm = xd.GlobalMotion[r == 0 ? mi.RefFrame0 : mi.RefFrame1];
        if (localWarpAllowed && !mi.WmParams.Invalid)
        {
            p.WarpParams.CopyFrom(mi.WmParams);
            p.Mode = AomInterPredParams.WARP_PRED;
        }
        else if (globalWarpAllowed && !gm.Invalid)
        {
            p.WarpParams.CopyFrom(gm);
            p.Mode = AomInterPredParams.WARP_PRED;
        }
    }

    /// <summary>setup_pred_plane (scaled_buffer_offset).</summary>
    public static void SetupPredPlane(ref AomBuf2d dst, int bsize, AomFrameBuffer fb, int plane, int miRow, int miCol, AomScaleFactors? scale,
        int ssX, int ssY)
    {
        int isUv = plane > 0 ? 1 : 0;
        if (ssY != 0 && (miRow & 1) != 0 && MiSizeHigh[bsize] == 1) miRow -= 1;
        if (ssX != 0 && (miCol & 1) != 0 && MiSizeWide[bsize] == 1) miCol -= 1;
        int x = (4 * miCol) >> ssX, y = (4 * miRow) >> ssY;
        int sx, sy;
        if (scale == null) { sx = x; sy = y; }
        else if (scale.IsScaled) { sx = scale.ScaledX(x) >> SCALE_EXTRA_BITS; sy = scale.ScaledY(y) >> SCALE_EXTRA_BITS; }
        else { sx = (x * (1 << SCALE_EXTRA_BITS)) >> SCALE_EXTRA_BITS; sy = (y * (1 << SCALE_EXTRA_BITS)) >> SCALE_EXTRA_BITS; }
        dst.Buf = fb.Buffers[plane];
        dst.Buf16 = fb.Buffers16[plane];
        dst.Offset0 = fb.Offsets[plane];
        dst.Offset = fb.Offsets[plane] + sy * fb.Strides[plane] + sx;
        dst.Width = fb.CropWidths[isUv];
        dst.Height = fb.CropHeights[isUv];
        dst.Stride = fb.Strides[plane];
    }

    /// <summary>av1_setup_pre_planes.</summary>
    public static void SetupPrePlanes(AomMacroblockD xd, int idx, AomFrameBuffer src, int miRow, int miCol, AomScaleFactors sf, int numPlanes)
    {
        for (int i = 0; i < Math.Min(numPlanes, 3); ++i)
        {
            var pd = xd.Plane[i];
            SetupPredPlane(ref pd.Pre(idx), xd.Mi0.Bsize, src, i, miRow, miCol, sf, pd.SubsamplingX, pd.SubsamplingY);
        }
    }

    /// <summary>av1_enc_build_inter_predictor.</summary>
    public static void EncBuildInterPredictor(AomCommon cm, AomMacroblockD xd, int miRow, int miCol, AomBufferSet? ctx, int bsize, int planeFrom,
        int planeTo, bool enableIntraEdgeFilter)
    {
        for (int plane = planeFrom; plane <= planeTo; ++plane)
        {
            if (plane != 0 && !xd.IsChromaRef) break;
            int miX = miCol * 4, miY = miRow * 4;
            BuildInterPredictors(cm, xd, plane, xd.Mi0, false, xd.Plane[plane].Width, xd.Plane[plane].Height, miX, miY);
            if (xd.Mi0.IsInterintraPred)
            {
                ctx ??= AomBufferSet.FromDst(xd);
                BuildInterintraPredictor(cm, xd, xd.Plane[plane].Dst, ctx, plane, bsize, enableIntraEdgeFilter);
            }
        }
    }

    /// <summary>av1_enc_build_inter_predictor_y.</summary>
    public static void EncBuildInterPredictorY(AomMacroblockD xd, int miRow, int miCol)
    {
        int miX = miCol * 4, miY = miRow * 4;
        var pd = xd.Plane[0];
        var p = new AomInterPredParams();
        ref var dstBuf = ref pd.Dst;
        var mv = xd.Mi0.Mv0;
        var sf = xd.BlockRefScaleFactors[0]!;
        InitInterParams(p, pd.Width, pd.Height, miY, miX, pd.SubsamplingX, pd.SubsamplingY, xd.Bd, xd.IsHbd, false, sf, pd.Pre0,
            xd.Mi0.InterpFilters);
        p.ConvParams = AomConvParams.NoRound(0, 0, xd.TmpConvDst, MAX_SB_SIZE, false, xd.Bd);
        p.ConvParams.UseDistWtdCompAvg = 0;
        BuildOneInterPredictor(dstBuf.Buf, dstBuf.Buf16, dstBuf.Offset, dstBuf.Stride, mv, p);
    }
}

/// <summary>BUFFER_SET: per-plane sample buffers and strides (the intra reference of inter-intra prediction).</summary>
internal sealed class AomBufferSet
{
    public readonly AomBuf2d[] Plane = new AomBuf2d[3];

    /// <summary>default_ctx: the current destination planes.</summary>
    public static AomBufferSet FromDst(AomMacroblockD xd)
    {
        var s = new AomBufferSet();
        for (int i = 0; i < 3; i++) s.Plane[i] = xd.Plane[i].Dst;
        return s;
    }
}
