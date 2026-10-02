using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/common/warped_motion.c: av1_get_shear_params, av1_warp_affine_c / av1_highbd_warp_affine_c
// (the SIMD versions are bit-exact), av1_warp_plane, find_affine_int / av1_find_projection.
internal static partial class AomWarp
{
    private const int DIV_LUT_PREC_BITS = 14, DIV_LUT_BITS = 8;
    private const int WARPEDMODEL_TRANS_CLAMP = 128 << WARPEDMODEL_PREC_BITS;
    private const int WARPEDMODEL_NONDIAGAFFINE_CLAMP = 1 << (WARPEDMODEL_PREC_BITS - 3);
    private const int WARPEDPIXEL_PREC_SHIFTS = 64, WARP_PARAM_REDUCE_BITS = 6, WARPEDDIFF_PREC_BITS = WARPEDMODEL_PREC_BITS - 6;

    private static int GetMsb(ulong v) => 63 - System.Numerics.BitOperations.LeadingZeroCount(v);

    private static short ResolveDivisor64(ulong d, out short shift)
    {
        shift = (short)GetMsb(d);
        long e = (long)(d - (1UL << shift));
        long f = shift > DIV_LUT_BITS ? ((e + (1L << (shift - DIV_LUT_BITS - 1))) >> (shift - DIV_LUT_BITS)) : e << (DIV_LUT_BITS - shift);
        shift += DIV_LUT_PREC_BITS;
        return (short)DivLut[f];
    }

    private static short ResolveDivisor32(uint d, out short shift)
    {
        shift = (short)GetMsb(d);
        int e = (int)(d - (1u << shift));
        int f = shift > DIV_LUT_BITS ? ((e + (1 << (shift - DIV_LUT_BITS - 1))) >> (shift - DIV_LUT_BITS)) : e << (DIV_LUT_BITS - shift);
        shift += DIV_LUT_PREC_BITS;
        return (short)DivLut[f];
    }

    private static long RoundSigned64(long v, int n) => v < 0 ? -((-v + ((1L << n) >> 1)) >> n) : (v + ((1L << n) >> 1)) >> n;
    private static int RoundSigned(int v, int n) => v < 0 ? -((-v + ((1 << n) >> 1)) >> n) : (v + ((1 << n) >> 1)) >> n;

    /// <summary>av1_get_shear_params.</summary>
    public static bool GetShearParams(AomWarpedMotionParams wm)
    {
        var mat = wm.WmMat;
        if (!(mat[2] > 0)) return false;
        int alpha = Math.Clamp(mat[2] - (1 << WARPEDMODEL_PREC_BITS), short.MinValue, short.MaxValue);
        int beta = Math.Clamp(mat[3], short.MinValue, short.MaxValue);
        short y = (short)(ResolveDivisor32((uint)Math.Abs(mat[2]), out short shift) * (mat[2] < 0 ? -1 : 1));
        long v = ((long)mat[4] * (1 << WARPEDMODEL_PREC_BITS)) * y;
        int gamma = Math.Clamp((int)RoundSigned64(v, shift), short.MinValue, short.MaxValue);
        v = ((long)mat[3] * mat[4]) * y;
        int delta = Math.Clamp(mat[5] - (int)RoundSigned64(v, shift) - (1 << WARPEDMODEL_PREC_BITS), short.MinValue, short.MaxValue);
        wm.Alpha = (short)(RoundSigned(alpha, WARP_PARAM_REDUCE_BITS) * (1 << WARP_PARAM_REDUCE_BITS));
        wm.Beta = (short)(RoundSigned(beta, WARP_PARAM_REDUCE_BITS) * (1 << WARP_PARAM_REDUCE_BITS));
        wm.Gamma = (short)(RoundSigned(gamma, WARP_PARAM_REDUCE_BITS) * (1 << WARP_PARAM_REDUCE_BITS));
        wm.Delta = (short)(RoundSigned(delta, WARP_PARAM_REDUCE_BITS) * (1 << WARP_PARAM_REDUCE_BITS));
        if (4 * Math.Abs((int)wm.Alpha) + 7 * Math.Abs((int)wm.Beta) >= (1 << WARPEDMODEL_PREC_BITS) ||
            4 * Math.Abs((int)wm.Gamma) + 4 * Math.Abs((int)wm.Delta) >= (1 << WARPEDMODEL_PREC_BITS))
            return false;
        return true;
    }

    /// <summary>av1_warp_affine_c / av1_highbd_warp_affine_c (ref8 or ref16; pred8 or pred16).</summary>
    public static void WarpAffine(int[] mat, byte[]? ref8, ushort[]? ref16, int refOff, int width, int height, int stride,
        byte[]? pred8, ushort[]? pred16, int predOff, int pCol, int pRow, int pWidth, int pHeight, int pStride, int ssX, int ssY, int bd,
        in AomConvParams cp, short alpha, short beta, short gamma, short delta)
    {
        Span<int> tmp = stackalloc int[15 * 8];
        int reduceBitsHoriz = cp.Round0;
        int reduceBitsVert = cp.IsCompound != 0 ? cp.Round1 : 2 * FILTER_BITS - reduceBitsHoriz;
        int offsetBitsHoriz = bd + FILTER_BITS - 1;
        int offsetBitsVert = bd + 2 * FILTER_BITS - reduceBitsHoriz;
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int maxPx = (1 << bd) - 1;
        var wf = WarpedFilter;
        for (int i = pRow; i < pRow + pHeight; i += 8)
            for (int j = pCol; j < pCol + pWidth; j += 8)
            {
                int srcX = (j + 4) << ssX, srcY = (i + 4) << ssY;
                long dstX = (long)mat[2] * srcX + (long)mat[3] * srcY + mat[0];
                long dstY = (long)mat[4] * srcX + (long)mat[5] * srcY + mat[1];
                long x4 = dstX >> ssX, y4 = dstY >> ssY;
                int ix4 = (int)(x4 >> WARPEDMODEL_PREC_BITS);
                int sx4 = (int)(x4 & ((1 << WARPEDMODEL_PREC_BITS) - 1));
                int iy4 = (int)(y4 >> WARPEDMODEL_PREC_BITS);
                int sy4 = (int)(y4 & ((1 << WARPEDMODEL_PREC_BITS) - 1));
                sx4 += alpha * -4 + beta * -4;
                sy4 += gamma * -4 + delta * -4;
                sx4 &= ~((1 << WARP_PARAM_REDUCE_BITS) - 1);
                sy4 &= ~((1 << WARP_PARAM_REDUCE_BITS) - 1);
                for (int k = -7; k < 8; ++k)
                {
                    int iy = Math.Clamp(iy4 + k, 0, height - 1);
                    int sx = sx4 + beta * (k + 4);
                    int rowOff = refOff + iy * stride;
                    for (int l = -4; l < 4; ++l)
                    {
                        int ix = ix4 + l - 3;
                        int offs = ((sx + (1 << (WARPEDDIFF_PREC_BITS - 1))) >> WARPEDDIFF_PREC_BITS) + WARPEDPIXEL_PREC_SHIFTS;
                        int co = offs * 8;
                        int sum = 1 << offsetBitsHoriz;
                        if (ref16 != null)
                            for (int m = 0; m < 8; ++m) sum += ref16[rowOff + Math.Clamp(ix + m, 0, width - 1)] * wf[co + m];
                        else
                            for (int m = 0; m < 8; ++m) sum += ref8![rowOff + Math.Clamp(ix + m, 0, width - 1)] * wf[co + m];
                        sum = (sum + (1 << (reduceBitsHoriz - 1))) >> reduceBitsHoriz;
                        tmp[(k + 7) * 8 + (l + 4)] = sum;
                        sx += alpha;
                    }
                }
                for (int k = -4; k < Math.Min(4, pRow + pHeight - i - 4); ++k)
                {
                    int sy = sy4 + delta * (k + 4);
                    for (int l = -4; l < Math.Min(4, pCol + pWidth - j - 4); ++l)
                    {
                        int offs = ((sy + (1 << (WARPEDDIFF_PREC_BITS - 1))) >> WARPEDDIFF_PREC_BITS) + WARPEDPIXEL_PREC_SHIFTS;
                        int co = offs * 8;
                        int sum = 1 << offsetBitsVert;
                        for (int m = 0; m < 8; ++m) sum += tmp[(k + m + 4) * 8 + (l + 4)] * wf[co + m];
                        int pIdx = predOff + (i - pRow + k + 4) * pStride + (j - pCol + l + 4);
                        if (cp.IsCompound != 0)
                        {
                            int dIdx = cp.DstOffset + (i - pRow + k + 4) * cp.DstStride + (j - pCol + l + 4);
                            sum = (sum + (1 << (reduceBitsVert - 1))) >> reduceBitsVert;
                            if (cp.DoAverage != 0)
                            {
                                int tmp32 = cp.Dst![dIdx];
                                if (cp.UseDistWtdCompAvg != 0)
                                {
                                    tmp32 = tmp32 * cp.FwdOffset + sum * cp.BckOffset;
                                    tmp32 >>= 4;   // DIST_PRECISION_BITS
                                }
                                else { tmp32 += sum; tmp32 >>= 1; }
                                tmp32 = tmp32 - (1 << (offsetBits - cp.Round1)) - (1 << (offsetBits - cp.Round1 - 1));
                                int px = Math.Clamp((tmp32 + (1 << (roundBits - 1))) >> roundBits, 0, maxPx);
                                if (pred16 != null) pred16[pIdx] = (ushort)px; else pred8![pIdx] = (byte)px;
                            }
                            else cp.Dst![dIdx] = (ushort)sum;
                        }
                        else
                        {
                            sum = (sum + (1 << (reduceBitsVert - 1))) >> reduceBitsVert;
                            int px = Math.Clamp(sum - (1 << (bd - 1)) - (1 << bd), 0, maxPx);
                            if (pred16 != null) pred16[pIdx] = (ushort)px; else pred8![pIdx] = (byte)px;
                        }
                        sy += gamma;
                    }
                }
            }
    }

    /// <summary>av1_make_inter_predictor's WARP_PRED: av1_warp_plane.</summary>
    public static void WarpPlane(AomInterPredParams p, byte[]? dst8, ushort[]? dst16, int dstOff, int dstStride)
    {
        var wm = p.WarpParams;
        var rb = p.RefFrameBuf;
        WarpAffine(wm.WmMat, p.UseHbdBuf ? null : rb.Buf, p.UseHbdBuf ? rb.Buf16 : null, rb.Offset0, rb.Width, rb.Height,
            rb.Stride, dst8, dst16, dstOff, p.PixCol, p.PixRow, p.BlockWidth, p.BlockHeight, dstStride, p.SubsamplingX, p.SubsamplingY,
            p.BitDepth, p.ConvParams, wm.Alpha, wm.Beta, wm.Gamma, wm.Delta);
    }

    private const int LS_MV_MAX = 256, LS_STEP = 8, LS_MAT_DOWN_BITS = 2;
    private static int LsSquare(int a) => (a * a * 4 + a * 4 * LS_STEP + LS_STEP * LS_STEP * 2) >> (2 + LS_MAT_DOWN_BITS);
    private static int LsProduct1(int a, int b) => (a * b * 4 + (a + b) * 2 * LS_STEP + LS_STEP * LS_STEP) >> (2 + LS_MAT_DOWN_BITS);
    private static int LsProduct2(int a, int b) => (a * b * 4 + (a + b) * 2 * LS_STEP + LS_STEP * LS_STEP * 2) >> (2 + LS_MAT_DOWN_BITS);

    private static int MultShiftNdiag(long px, short iDet, int shift)
        => (int)Math.Clamp(RoundSigned64(px * iDet, shift), -WARPEDMODEL_NONDIAGAFFINE_CLAMP + 1, WARPEDMODEL_NONDIAGAFFINE_CLAMP - 1);

    private static int MultShiftDiag(long px, short iDet, int shift)
        => (int)Math.Clamp(RoundSigned64(px * iDet, shift), (1 << WARPEDMODEL_PREC_BITS) - WARPEDMODEL_NONDIAGAFFINE_CLAMP + 1,
            (1 << WARPEDMODEL_PREC_BITS) + WARPEDMODEL_NONDIAGAFFINE_CLAMP - 1);

    /// <summary>av1_find_projection (find_affine_int + av1_get_shear_params): true on failure.</summary>
    public static bool FindProjection(int np, int[] pts1, int[] pts2, int bsize, int mvy, int mvx, AomWarpedMotionParams wm, int miRow, int miCol)
    {
        int a00 = 0, a01 = 0, a11 = 0, bx0 = 0, bx1 = 0, by0 = 0, by1 = 0;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int rsuy = bh / 2 - 1, rsux = bw / 2 - 1;
        int suy = rsuy * 8, sux = rsux * 8;
        int duy = suy + mvy, dux = sux + mvx;
        for (int i = 0; i < np; i++)
        {
            int dx = pts2[i * 2] - dux, dy = pts2[i * 2 + 1] - duy;
            int sx = pts1[i * 2] - sux, sy = pts1[i * 2 + 1] - suy;
            if (Math.Abs(sx - dx) < LS_MV_MAX && Math.Abs(sy - dy) < LS_MV_MAX)
            {
                a00 += LsSquare(sx);
                a01 += LsProduct1(sx, sy);
                a11 += LsSquare(sy);
                bx0 += LsProduct2(sx, dx);
                bx1 += LsProduct1(sy, dx);
                by0 += LsProduct1(sx, dy);
                by1 += LsProduct2(sy, dy);
            }
        }
        long det = (long)a00 * a11 - (long)a01 * a01;
        if (det == 0) return true;
        short iDet = (short)(ResolveDivisor64((ulong)Math.Abs(det), out short shift) * (det < 0 ? -1 : 1));
        shift -= WARPEDMODEL_PREC_BITS;
        if (shift < 0)
        {
            iDet = (short)(iDet << (-shift));
            shift = 0;
        }
        long px0 = (long)a11 * bx0 - (long)a01 * bx1;
        long px1 = -(long)a01 * bx0 + (long)a00 * bx1;
        long py0 = (long)a11 * by0 - (long)a01 * by1;
        long py1 = -(long)a01 * by0 + (long)a00 * by1;
        var m = wm.WmMat;
        m[2] = MultShiftDiag(px0, iDet, shift);
        m[3] = MultShiftNdiag(px1, iDet, shift);
        m[4] = MultShiftNdiag(py0, iDet, shift);
        m[5] = MultShiftDiag(py1, iDet, shift);
        int isuy = miRow * 4 + rsuy, isux = miCol * 4 + rsux;
        int vx = mvx * (1 << (WARPEDMODEL_PREC_BITS - 3)) - (isux * (m[2] - (1 << WARPEDMODEL_PREC_BITS)) + isuy * m[3]);
        int vy = mvy * (1 << (WARPEDMODEL_PREC_BITS - 3)) - (isux * m[4] + isuy * (m[5] - (1 << WARPEDMODEL_PREC_BITS)));
        m[0] = Math.Clamp(vx, -WARPEDMODEL_TRANS_CLAMP, WARPEDMODEL_TRANS_CLAMP - 1);
        m[1] = Math.Clamp(vy, -WARPEDMODEL_TRANS_CLAMP, WARPEDMODEL_TRANS_CLAMP - 1);
        return !GetShearParams(wm);
    }

    /// <summary>av1_findSamples.</summary>
    public static int FindSamples(AomCommon cm, AomMacroblockD xd, int[] pts, int[] ptsInref)
    {
        var mbmi0 = xd.MiAt(0, 0)!;
        int refFrame = mbmi0.RefFrame0;
        int np = 0, k = 0;
        bool doTl = true, doTr = true;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        void Record(AomMbModeInfo m, int rowOffset, int signR, int colOffset, int signC)
        {
            int bw = BlockSizeWide[m.Bsize], bh = BlockSizeHigh[m.Bsize];
            int x = colOffset * 4 + signC * bw / 2 - 1;
            int y = rowOffset * 4 + signR * bh / 2 - 1;
            pts[k] = x * 8;
            pts[k + 1] = y * 8;
            ptsInref[k] = pts[k] + m.Mv0.Col;
            ptsInref[k + 1] = pts[k + 1] + m.Mv0.Row;
            k += 2;
        }
        const int MAX = 8;
        if (xd.UpAvailable)
        {
            var m = xd.MiAt(-1, 0)!;
            int sbw = MiSizeWide[m.Bsize];
            if (xd.Width <= sbw)
            {
                int colOffset = -miCol % sbw;
                if (colOffset < 0) doTl = false;
                if (colOffset + sbw > xd.Width) doTr = false;
                if (m.RefFrame0 == refFrame && m.RefFrame1 == NONE_FRAME)
                {
                    Record(m, 0, -1, colOffset, 1);
                    if (++np >= MAX) return MAX;
                }
            }
            else
                for (int i = 0; i < Math.Min(xd.Width, cm.MiCols - miCol); i += sbw)
                {
                    m = xd.MiAt(-1, i)!;
                    sbw = MiSizeWide[m.Bsize];
                    if (m.RefFrame0 == refFrame && m.RefFrame1 == NONE_FRAME)
                    {
                        Record(m, 0, -1, i, 1);
                        if (++np >= MAX) return MAX;
                    }
                }
        }
        if (xd.LeftAvailable)
        {
            var m = xd.MiAt(0, -1)!;
            int sbh = MiSizeHigh[m.Bsize];
            if (xd.Height <= sbh)
            {
                int rowOffset = -miRow % sbh;
                if (rowOffset < 0) doTl = false;
                if (m.RefFrame0 == refFrame && m.RefFrame1 == NONE_FRAME)
                {
                    Record(m, rowOffset, 1, 0, -1);
                    if (++np >= MAX) return MAX;
                }
            }
            else
                for (int i = 0; i < Math.Min(xd.Height, cm.MiRows - miRow); i += sbh)
                {
                    m = xd.MiAt(i, -1)!;
                    sbh = MiSizeHigh[m.Bsize];
                    if (m.RefFrame0 == refFrame && m.RefFrame1 == NONE_FRAME)
                    {
                        Record(m, i, 1, 0, -1);
                        if (++np >= MAX) return MAX;
                    }
                }
        }
        if (doTl && xd.LeftAvailable && xd.UpAvailable)
        {
            var m = xd.MiAt(-1, -1)!;
            if (m.RefFrame0 == refFrame && m.RefFrame1 == NONE_FRAME)
            {
                Record(m, 0, -1, 0, -1);
                if (++np >= MAX) return MAX;
            }
        }
        if (doTr && AomMvRef.HasTopRight(cm, xd, miRow, miCol, Math.Max(xd.Width, xd.Height)) &&
            AomMvRef.IsInside(xd, miCol, miRow, -1, xd.Width))
        {
            var m = xd.MiAt(-1, xd.Width)!;
            if (m.RefFrame0 == refFrame && m.RefFrame1 == NONE_FRAME)
            {
                Record(m, 0, -1, xd.Width, 1);
                if (++np >= MAX) return MAX;
            }
        }
        return np;
    }

    private static readonly (int row, int col)[][] WarpNeighbors =
    {
        new[] { (0, -1), (1, 0), (0, 1), (-1, 0) },
        new[] { (0, -1), (1, 0), (0, 1), (-1, 0), (1, -1), (1, 1), (-1, -1), (-1, 1) },
    };
    private static int Dia(int l, int d, int r, int u) => l | (d << 1) | (r << 2) | (u << 3);
    private static int Sqr(int l, int d, int r, int u, int dl, int dr, int ul, int ur) => l | (d << 1) | (r << 2) | (u << 3) | (dl << 4) | (dr << 5) | (ul << 6) | (ur << 7);
    private static readonly int[][] WarpNeighborMask =
    {
        new[] { Dia(1, 1, 0, 1), Dia(1, 1, 1, 0), Dia(0, 1, 1, 1), Dia(1, 0, 1, 1) },
        new[] { Sqr(1, 0, 0, 0, 1, 0, 1, 0), Sqr(0, 1, 0, 0, 1, 1, 0, 0), Sqr(0, 0, 1, 0, 0, 1, 0, 1), Sqr(0, 0, 0, 1, 0, 0, 1, 1),
                Sqr(1, 1, 0, 0, 1, 1, 1, 0), Sqr(0, 1, 1, 0, 1, 1, 0, 1), Sqr(1, 0, 0, 1, 1, 0, 1, 1), Sqr(0, 0, 1, 1, 0, 1, 1, 1) },
    };

    /// <summary>compute_motion_cost: the warped luma prediction's variance plus the mv cost.</summary>
    private static uint ComputeMotionCost(AomMacroblockD xd, AomCommon cm, AomSubpelMsParams ms, int bsize, AomMv mv, bool edgeFilter)
    {
        AomInterPred.EncBuildInterPredictor(cm, xd, xd.MiRow, xd.MiCol, null, bsize, 0, 0, edgeFilter);
        var d = xd.Plane[0].Dst;
        var src = ms.Src;
        uint mse;
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        if (d.Buf16 != null) mse = AomHbd.Variance(d.Buf16, d.Offset, d.Stride, src.Buf16, src.Offset, src.Stride, 0, w, h, ms.Bd, out _);
        else mse = AomSad.Variance(d.Buf, d.Offset, d.Stride, src.Buf, src.Offset, src.Stride, w, h, out _);
        return mse + (uint)AomMcomp.MvErrCost(ms.MvCostType, mv, ms.RefMv, ms.MvJCost, ms.MvCost, ms.ErrorPerBit);
    }

    /// <summary>av1_refine_warped_mv.</summary>
    public static uint RefineWarpedMv(AomMacroblockD xd, AomCommon cm, AomSubpelMsParams ms, int bsize, int[] pts0, int[] ptsInref0,
        int totalSamples, int method, int numIterations, bool edgeFilter)
    {
        var mbmi = xd.MiAt(0, 0)!;
        var neighbors = WarpNeighbors[method];
        var mask = WarpNeighborMask[method];
        var bestWm = mbmi.WmParams.Clone();
        byte bestNumProjRef = mbmi.NumProjRef;
        int mvShift = ms.AllowHp ? 0 : 1;
        uint bestmse = ComputeMotionCost(xd, cm, ms, bsize, mbmi.Mv0, edgeFilter);
        var pts = new int[16];
        var ptsInref = new int[16];
        int validNeighbors = 0xff;
        for (int ite = 0; ite < numIterations; ++ite)
        {
            int bestIdx = -1;
            for (int idx = 0; idx < neighbors.Length; ++idx)
            {
                if ((validNeighbors & (1 << idx)) == 0) continue;
                var thisMv = new AomMv(mbmi.Mv0.Row + neighbors[idx].row * (1 << mvShift), mbmi.Mv0.Col + neighbors[idx].col * (1 << mvShift));
                if (AomSubpel.IsSubpelmvInRange(ms.MvLimits, thisMv))
                {
                    Array.Copy(pts0, pts, totalSamples * 2);
                    Array.Copy(ptsInref0, ptsInref, totalSamples * 2);
                    if (totalSamples > 1) mbmi.NumProjRef = (byte)SelectSamples(thisMv, pts, ptsInref, totalSamples, bsize);
                    if (!FindProjection(mbmi.NumProjRef, pts, ptsInref, bsize, thisMv.Row, thisMv.Col, mbmi.WmParams, xd.MiRow, xd.MiCol))
                    {
                        uint thismse = ComputeMotionCost(xd, cm, ms, bsize, thisMv, edgeFilter);
                        if (thismse < bestmse)
                        {
                            bestIdx = idx;
                            bestWm.CopyFrom(mbmi.WmParams);
                            bestNumProjRef = mbmi.NumProjRef;
                            bestmse = thismse;
                        }
                    }
                }
            }
            if (bestIdx == -1) break;
            mbmi.Mv0 = new AomMv(mbmi.Mv0.Row + neighbors[bestIdx].row * (1 << mvShift), mbmi.Mv0.Col + neighbors[bestIdx].col * (1 << mvShift));
            validNeighbors = mask[bestIdx];
        }
        mbmi.WmParams.CopyFrom(bestWm);
        mbmi.NumProjRef = bestNumProjRef;
        return bestmse;
    }

    /// <summary>av1_selectSamples.</summary>
    public static int SelectSamples(AomMv mv, int[] pts, int[] ptsInref, int len, int bsize)
    {
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int thresh = Math.Clamp(Math.Max(bw, bh), 16, 112);
        int ret = 0;
        for (int i = 0; i < len; ++i)
        {
            int diff = Math.Abs(ptsInref[2 * i] - pts[2 * i] - mv.Col) + Math.Abs(ptsInref[2 * i + 1] - pts[2 * i + 1] - mv.Row);
            if (diff > thresh) continue;
            if (ret != i)
            {
                pts[2 * ret] = pts[2 * i]; pts[2 * ret + 1] = pts[2 * i + 1];
                ptsInref[2 * ret] = ptsInref[2 * i]; ptsInref[2 * ret + 1] = ptsInref[2 * i + 1];
            }
            ++ret;
        }
        return Math.Max(ret, 1);
    }
}
