using System;
using System.Runtime.CompilerServices;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>
/// libaom 3.14.1 av1/common/reconintra.c (CONFIG_AV1_HIGHBITDEPTH): high bit depth intra prediction of one transform
/// block on 16-bit samples: highbd_build_directional_and_filter_intra_predictors / highbd_build_non_directional_intra_
/// predictors, the directional predictors (av1_highbd_dr_prediction_z1/z2/z3), highbd_filter_intra_predictor, the edge
/// filter / upsampler as libaom's dispatched SSE4.1 kernels run them (their extension writes reproduced), palette and
/// the CfL branch of av1_predict_intra_block_facade.
/// </summary>
internal static unsafe partial class AomReconIntra
{
    // ---- directional predictors (av1_highbd_dr_prediction_z1/z2/z3: the AVX2 kernels compute the C's values) ----

    public static void HighbdDrPredictionZ1(ushort* dst, nint stride, int bw, int bh, ushort* above, int upsampleAbove, int dx)
    {
        int maxBaseX = ((bw + bh) - 1) << upsampleAbove;
        int fracBits = 6 - upsampleAbove;
        int baseInc = 1 << upsampleAbove;
        int x = dx;
        for (int r = 0; r < bh; ++r, dst += stride, x += dx)
        {
            int b = x >> fracBits;
            int shift = ((x << upsampleAbove) & 0x3F) >> 1;
            if (b >= maxBaseX)
            {
                for (int i = r; i < bh; ++i, dst += stride) new Span<ushort>(dst, bw).Fill(above[maxBaseX]);
                return;
            }
            for (int c = 0; c < bw; ++c, b += baseInc)
            {
                if (b < maxBaseX)
                {
                    int val = above[b] * (32 - shift) + above[b + 1] * shift;
                    dst[c] = (ushort)((val + 16) >> 5);
                }
                else dst[c] = above[maxBaseX];
            }
        }
    }

    public static void HighbdDrPredictionZ2(ushort* dst, nint stride, int bw, int bh, ushort* above, ushort* left,
        int upsampleAbove, int upsampleLeft, int dx, int dy)
    {
        int minBaseX = -(1 << upsampleAbove);
        int fracBitsX = 6 - upsampleAbove, fracBitsY = 6 - upsampleLeft;
        for (int r = 0; r < bh; ++r, dst += stride)
            for (int c = 0; c < bw; ++c)
            {
                int val;
                int y = r + 1;
                int x = (c << 6) - y * dx;
                int baseX = x >> fracBitsX;
                if (baseX >= minBaseX)
                {
                    int shift = ((x * (1 << upsampleAbove)) & 0x3F) >> 1;
                    val = above[baseX] * (32 - shift) + above[baseX + 1] * shift;
                }
                else
                {
                    x = c + 1;
                    y = (r << 6) - x * dy;
                    int baseY = y >> fracBitsY;
                    int shift = ((y * (1 << upsampleLeft)) & 0x3F) >> 1;
                    val = left[baseY] * (32 - shift) + left[baseY + 1] * shift;
                }
                dst[c] = (ushort)((val + 16) >> 5);
            }
    }

    public static void HighbdDrPredictionZ3(ushort* dst, nint stride, int bw, int bh, ushort* left, int upsampleLeft, int dy)
    {
        int maxBaseY = (bw + bh - 1) << upsampleLeft;
        int fracBits = 6 - upsampleLeft;
        int baseInc = 1 << upsampleLeft;
        int y = dy;
        for (int c = 0; c < bw; ++c, y += dy)
        {
            int b = y >> fracBits;
            int shift = ((y << upsampleLeft) & 0x3F) >> 1;
            for (int r = 0; r < bh; ++r, b += baseInc)
            {
                if (b < maxBaseY)
                {
                    int val = left[b] * (32 - shift) + left[b + 1] * shift;
                    dst[r * stride + c] = (ushort)((val + 16) >> 5);
                }
                else
                {
                    for (; r < bh; ++r) dst[r * stride + c] = left[maxBaseY];
                    break;
                }
            }
        }
    }

    /// <summary>highbd_dr_predictor.</summary>
    public static void HighbdDrPredictor(ushort* dst, nint stride, int txSize, ushort* above, ushort* left, int upsampleAbove,
        int upsampleLeft, int angle, int bd)
    {
        int dx = GetDx(angle), dy = GetDy(angle);
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        if (angle > 0 && angle < 90) HighbdDrPredictionZ1(dst, stride, bw, bh, above, upsampleAbove, dx);
        else if (angle > 90 && angle < 180) HighbdDrPredictionZ2(dst, stride, bw, bh, above, left, upsampleAbove, upsampleLeft, dx, dy);
        else if (angle > 180 && angle < 270) HighbdDrPredictionZ3(dst, stride, bw, bh, left, upsampleLeft, dy);
        else if (angle == 90) AomIntraPredHbd.Pred(V_PRED, txSize, dst, stride, above, left, bd);
        else if (angle == 180) AomIntraPredHbd.Pred(H_PRED, txSize, dst, stride, above, left, bd);
    }

    /// <summary>highbd_filter_intra_predictor.</summary>
    public static void HighbdFilterIntraPredictor(ushort* dst, nint stride, int txSize, ushort* above, ushort* left, int mode, int bd)
    {
        const int B = 33;
        ushort* buffer = stackalloc ushort[B * B];
        int bw = TxSizeWide[txSize], bh = TxSizeHigh[txSize];
        for (int r = 0; r < bh; ++r) buffer[(r + 1) * B] = left[r];
        Buffer.MemoryCopy(above - 1, buffer, (bw + 1) * 2, (bw + 1) * 2);
        ReadOnlySpan<sbyte> taps = FilterIntraTaps.Slice(mode * 64, 64);
        int max = (1 << bd) - 1;
        for (int r = 1; r < bh + 1; r += 2)
            for (int c = 1; c < bw + 1; c += 4)
            {
                ushort* row0 = buffer + (r - 1) * B;
                int p0 = row0[c - 1], p1 = row0[c], p2 = row0[c + 1], p3 = row0[c + 2], p4 = row0[c + 3];
                int p5 = buffer[r * B + c - 1], p6 = buffer[(r + 1) * B + c - 1];
                for (int k = 0; k < 8; ++k)
                {
                    ReadOnlySpan<sbyte> t = taps.Slice(k * 8, 8);
                    int pr = t[0] * p0 + t[1] * p1 + t[2] * p2 + t[3] * p3 + t[4] * p4 + t[5] * p5 + t[6] * p6;
                    int v = (pr + (1 << (FILTER_INTRA_SCALE_BITS - 1))) >> FILTER_INTRA_SCALE_BITS;
                    buffer[(r + (k >> 2)) * B + c + (k & 3)] = (ushort)(v < 0 ? 0 : v > max ? max : v);
                }
            }
        for (int r = 0; r < bh; ++r, dst += stride) Buffer.MemoryCopy(buffer + (r + 1) * B + 1, dst, bw * 2, bw * 2);
    }

    /// <summary>av1_highbd_filter_intra_edge as libaom's dispatched SSE4.1 kernel runs it: the filtered samples equal
    /// av1_highbd_filter_intra_edge_c (exact in its 16-bit lanes), and it writes p[-1] = p[0], p[sz .. sz + 7] = p[sz - 1].</summary>
    public static void HighbdFilterIntraEdge(ushort* p, int sz, int strength)
    {
        if (strength == 0) return;
        ReadOnlySpan<byte> kernel = EdgeKernel.Slice((strength - 1) * INTRA_EDGE_TAPS, INTRA_EDGE_TAPS);
        ushort* edge = stackalloc ushort[129];
        Buffer.MemoryCopy(p, edge, 129 * 2, sz * 2);
        for (int i = 1; i < sz; i++)
        {
            int s = 0;
            for (int j = 0; j < INTRA_EDGE_TAPS; j++)
            {
                int k = i - 2 + j;
                k = k < 0 ? 0 : k;
                k = k > sz - 1 ? sz - 1 : k;
                s += edge[k] * kernel[j];
            }
            p[i] = (ushort)((s + 8) >> 4);
        }
        p[-1] = edge[0];
        new Span<ushort>(p + sz, 8).Fill(edge[sz - 1]);
    }

    /// <summary>highbd_filter_intra_edge_corner.</summary>
    public static void HighbdFilterIntraEdgeCorner(ushort* pAbove, ushort* pLeft)
    {
        int s = (pLeft[0] * 5 + pAbove[-1] * 6 + pAbove[0] * 5 + 8) >> 4;
        pAbove[-1] = (ushort)s;
        pLeft[-1] = (ushort)s;
    }

    /// <summary>av1_highbd_upsample_intra_edge as libaom's dispatched SSE4.1 kernel runs it: the half-sample positions of
    /// p[-1 .. sz-1] into p[-2 .. 2sz-2] (equal to the C there), written in whole 8-sample chunks (p[-2 .. 16k - 3] for k
    /// chunks of n = sz + 1), the tail computed from the 32 samples p[-2 .. 29] loaded up front and zeros after them.</summary>
    public static void HighbdUpsampleIntraEdge(ushort* p, int sz, int bd)
    {
        p[-2] = p[-1];
        p[sz] = p[sz - 1];
        ushort* inp = stackalloc ushort[48];
        Buffer.MemoryCopy(p - 2, inp, 64, 64);
        new Span<ushort>(inp + 32, 16).Clear();
        int max = (1 << bd) - 1;
        int chunks = (sz + 1 + 7) >> 3;
        for (int k = 0; k < chunks * 8; k++)
        {
            int s = -(inp[k] + inp[k + 3]) + 9 * (inp[k + 1] + inp[k + 2]);
            s = (s + 8) >> 4;
            s = s < 0 ? 0 : s > max ? max : s;
            p[2 * k - 2] = inp[k + 1];
            p[2 * k - 1] = (ushort)s;
        }
    }

    /// <summary>highbd_build_directional_and_filter_intra_predictors.</summary>
    public static void HighbdBuildDirectionalAndFilterIntraPredictors(ushort* refp, nint refStride, ushort* dst, nint dstStride,
        int mode, int pAngle, int filterIntraMode, int txSize, int disableEdgeFilter, int nTopPx, int nTopRightPx,
        int nLeftPx, int nBottomLeftPx, int intraEdgeFilterType, int bd)
    {
        int i;
        ushort* aboveRef = refp - refStride;
        ushort* leftRef = refp - 1;
        ushort* leftData = stackalloc ushort[NUM_INTRA_NEIGHBOUR_PIXELS];
        ushort* aboveData = stackalloc ushort[NUM_INTRA_NEIGHBOUR_PIXELS];
        ushort* aboveRow = aboveData + 16;
        ushort* leftCol = leftData + 16;
        int txwpx = TxSizeWide[txSize], txhpx = TxSizeHigh[txSize];
        int needLeft = ExtendModes[mode] & NEED_LEFT;
        int needAbove = ExtendModes[mode] & NEED_ABOVE;
        int needAboveLeft = ExtendModes[mode] & NEED_ABOVELEFT;
        bool isDrMode = IsDirectionalMode(mode);
        bool useFilterIntra = filterIntraMode != FILTER_INTRA_MODES;
        int baseV = 128 << (bd - 8);
        new Span<ushort>(leftData, NUM_INTRA_NEIGHBOUR_PIXELS).Fill((ushort)(baseV + 1));
        new Span<ushort>(aboveData, NUM_INTRA_NEIGHBOUR_PIXELS).Fill((ushort)(baseV - 1));

        if (isDrMode)
        {
            if (pAngle <= 90) { needAbove = 1; needLeft = 0; needAboveLeft = 1; }
            else if (pAngle < 180) { needAbove = 1; needLeft = 1; needAboveLeft = 1; }
            else { needAbove = 0; needLeft = 1; needAboveLeft = 1; }
        }
        if (useFilterIntra) needLeft = needAbove = needAboveLeft = 1;

        if ((needAbove == 0 && nLeftPx == 0) || (needLeft == 0 && nTopPx == 0))
        {
            int val;
            if (needLeft != 0) val = nTopPx > 0 ? aboveRef[0] : baseV + 1;
            else val = nLeftPx > 0 ? leftRef[0] : baseV - 1;
            for (i = 0; i < txhpx; ++i, dst += dstStride) new Span<ushort>(dst, txwpx).Fill((ushort)val);
            return;
        }

        if (needLeft != 0)
        {
            int numLeftPixelsNeeded = txhpx + (nBottomLeftPx >= 0 ? txwpx : 0);
            i = 0;
            if (nLeftPx > 0)
            {
                for (; i < nLeftPx; i++) leftCol[i] = leftRef[i * refStride];
                if (nBottomLeftPx > 0)
                    for (; i < txhpx + nBottomLeftPx; i++) leftCol[i] = leftRef[i * refStride];
                if (i < numLeftPixelsNeeded) new Span<ushort>(leftCol + i, numLeftPixelsNeeded - i).Fill(leftCol[i - 1]);
            }
            else if (nTopPx > 0) new Span<ushort>(leftCol, numLeftPixelsNeeded).Fill(aboveRef[0]);
        }

        if (needAbove != 0)
        {
            int numTopPixelsNeeded = txwpx + (nTopRightPx >= 0 ? txhpx : 0);
            if (nTopPx > 0)
            {
                Buffer.MemoryCopy(aboveRef, aboveRow, nTopPx * 2, nTopPx * 2);
                i = nTopPx;
                if (nTopRightPx > 0)
                {
                    Buffer.MemoryCopy(aboveRef + txwpx, aboveRow + txwpx, nTopRightPx * 2, nTopRightPx * 2);
                    i += nTopRightPx;
                }
                if (i < numTopPixelsNeeded) new Span<ushort>(aboveRow + i, numTopPixelsNeeded - i).Fill(aboveRow[i - 1]);
            }
            else if (nLeftPx > 0) new Span<ushort>(aboveRow, numTopPixelsNeeded).Fill(leftRef[0]);
        }

        if (needAboveLeft != 0)
        {
            if (nTopPx > 0 && nLeftPx > 0) aboveRow[-1] = aboveRef[-1];
            else if (nTopPx > 0) aboveRow[-1] = aboveRef[0];
            else if (nLeftPx > 0) aboveRow[-1] = leftRef[0];
            else aboveRow[-1] = (ushort)baseV;
            leftCol[-1] = aboveRow[-1];
        }

        if (useFilterIntra)
        {
            HighbdFilterIntraPredictor(dst, dstStride, txSize, aboveRow, leftCol, filterIntraMode, bd);
            return;
        }

        int upsampleAbove = 0, upsampleLeft = 0;
        if (disableEdgeFilter == 0)
        {
            bool needRight = pAngle < 90, needBottom = pAngle > 180;
            if (pAngle != 90 && pAngle != 180)
            {
                const int abLe = 1;
                if (needAbove != 0 && needLeft != 0 && (txwpx + txhpx >= 24)) HighbdFilterIntraEdgeCorner(aboveRow, leftCol);
                if (needAbove != 0 && nTopPx > 0)
                {
                    int strength = IntraEdgeFilterStrength(txwpx, txhpx, pAngle - 90, intraEdgeFilterType);
                    HighbdFilterIntraEdge(aboveRow - abLe, nTopPx + abLe + (needRight ? txhpx : 0), strength);
                }
                if (needLeft != 0 && nLeftPx > 0)
                {
                    int strength = IntraEdgeFilterStrength(txhpx, txwpx, pAngle - 180, intraEdgeFilterType);
                    HighbdFilterIntraEdge(leftCol - abLe, nLeftPx + abLe + (needBottom ? txwpx : 0), strength);
                }
            }
            upsampleAbove = UseIntraEdgeUpsample(txwpx, txhpx, pAngle - 90, intraEdgeFilterType);
            if (needAbove != 0 && upsampleAbove != 0) HighbdUpsampleIntraEdge(aboveRow, txwpx + (needRight ? txhpx : 0), bd);
            upsampleLeft = UseIntraEdgeUpsample(txhpx, txwpx, pAngle - 180, intraEdgeFilterType);
            if (needLeft != 0 && upsampleLeft != 0) HighbdUpsampleIntraEdge(leftCol, txhpx + (needBottom ? txwpx : 0), bd);
        }
        HighbdDrPredictor(dst, dstStride, txSize, aboveRow, leftCol, upsampleAbove, upsampleLeft, pAngle, bd);
    }

    /// <summary>highbd_build_non_directional_intra_predictors: DC, SMOOTH*, PAETH.</summary>
    public static void HighbdBuildNonDirectionalIntraPredictors(ushort* refp, nint refStride, ushort* dst, nint dstStride,
        int mode, int txSize, int nTopPx, int nLeftPx, int bd)
    {
        ushort* aboveRef = refp - refStride;
        ushort* leftRef = refp - 1;
        int txwpx = TxSizeWide[txSize], txhpx = TxSizeHigh[txSize];
        int needLeft = ExtendModes[mode] & NEED_LEFT;
        int needAbove = ExtendModes[mode] & NEED_ABOVE;
        int needAboveLeft = ExtendModes[mode] & NEED_ABOVELEFT;
        int baseV = 128 << (bd - 8);
        int i;
        if ((needAbove == 0 && nLeftPx == 0) || (needLeft == 0 && nTopPx == 0))
        {
            int val;
            if (needLeft != 0) val = nTopPx > 0 ? aboveRef[0] : baseV + 1;
            else val = nLeftPx > 0 ? leftRef[0] : baseV - 1;
            for (i = 0; i < txhpx; ++i, dst += dstStride) new Span<ushort>(dst, txwpx).Fill((ushort)val);
            return;
        }

        ushort* leftData = stackalloc ushort[NUM_INTRA_NEIGHBOUR_PIXELS];
        ushort* aboveData = stackalloc ushort[NUM_INTRA_NEIGHBOUR_PIXELS];
        ushort* aboveRow = aboveData + 16;
        ushort* leftCol = leftData + 16;

        if (needLeft != 0)
        {
            new Span<ushort>(leftData, NUM_INTRA_NEIGHBOUR_PIXELS).Fill((ushort)(baseV + 1));
            if (nLeftPx > 0)
            {
                for (i = 0; i < nLeftPx; i++) leftCol[i] = leftRef[i * refStride];
                if (i < txhpx) new Span<ushort>(leftCol + i, txhpx - i).Fill(leftCol[i - 1]);
            }
            else if (nTopPx > 0) new Span<ushort>(leftCol, txhpx).Fill(aboveRef[0]);
        }

        if (needAbove != 0)
        {
            new Span<ushort>(aboveData, NUM_INTRA_NEIGHBOUR_PIXELS).Fill((ushort)(baseV - 1));
            if (nTopPx > 0)
            {
                Buffer.MemoryCopy(aboveRef, aboveRow, nTopPx * 2, nTopPx * 2);
                i = nTopPx;
                if (i < txwpx) new Span<ushort>(aboveRow + i, txwpx - i).Fill(aboveRow[i - 1]);
            }
            else if (nLeftPx > 0) new Span<ushort>(aboveRow, txwpx).Fill(leftRef[0]);
        }

        if (needAboveLeft != 0)
        {
            if (nTopPx > 0 && nLeftPx > 0) aboveRow[-1] = aboveRef[-1];
            else if (nTopPx > 0) aboveRow[-1] = aboveRef[0];
            else if (nLeftPx > 0) aboveRow[-1] = leftRef[0];
            else aboveRow[-1] = (ushort)baseV;
            leftCol[-1] = aboveRow[-1];
        }

        if (mode == DC_PRED)
            AomIntraPredHbd.DcPred(nLeftPx > 0 ? 1 : 0, nTopPx > 0 ? 1 : 0, txSize, dst, dstStride, aboveRow, leftCol, bd);
        else
            AomIntraPredHbd.Pred(mode, txSize, dst, dstStride, aboveRow, leftCol, bd);
    }

    /// <summary>av1_predict_intra_block (high bit depth; array form).</summary>
    public static void PredictIntraBlock(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int wpx, int hpx,
        int txSize, int mode, int angleDelta, bool usePalette, int filterIntraMode, ushort[] refBuf, int refOffset,
        int refStride, ushort[] dstBuf, int dstOffset, int dstStride, int colOff, int rowOff, int plane)
    {
        fixed (ushort* r = refBuf)
        fixed (ushort* d = dstBuf)
            PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, wpx, hpx, txSize, mode, angleDelta, usePalette,
                filterIntraMode, r + refOffset, refStride, d + dstOffset, dstStride, colOff, rowOff, plane);
    }

    /// <summary>av1_predict_intra_block (high bit depth).</summary>
    public static void PredictIntraBlock(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int wpx, int hpx,
        int txSize, int mode, int angleDelta, bool usePalette, int filterIntraMode, ushort* refp, int refStride,
        ushort* dst, int dstStride, int colOff, int rowOff, int plane)
    {
        AomMbModeInfo mbmi = xd.Mi0;
        int txwpx = TxSizeWide[txSize], txhpx = TxSizeHigh[txSize];
        int x = colOff << MI_SIZE_LOG2, y = rowOff << MI_SIZE_LOG2;
        int bd = xd.Bd;

        if (usePalette)
        {
            int p1 = plane != 0 ? 1 : 0;
            byte[] mapArr = xd.Plane[p1].ColorIndexMap;
            int mapOff = xd.ColorIndexMapOffset[p1];
            ushort[] palette = mbmi.Palette.PaletteColors;
            int palOff = plane * AomPaletteModeInfo.PaletteMaxSize;
            for (int r = 0; r < txhpx; ++r)
            {
                int m = mapOff + (r + y) * wpx + x;
                ushort* d = dst + r * dstStride;
                for (int c = 0; c < txwpx; ++c) d[c] = palette[palOff + mapArr[m + c]];
            }
            return;
        }

        AomMbdPlane pd = xd.Plane[plane];
        int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
        int haveTop = rowOff != 0 || (ssY != 0 ? xd.ChromaUpAvailable : xd.UpAvailable) ? 1 : 0;
        int haveLeft = colOff != 0 || (ssX != 0 ? xd.ChromaLeftAvailable : xd.LeftAvailable) ? 1 : 0;
        int xr = (xd.MbToRightEdge >> (3 + ssX)) + wpx - x - txwpx;
        int yd = (xd.MbToBottomEdge >> (3 + ssY)) + hpx - y - txhpx;
        bool useFilterIntra = filterIntraMode != FILTER_INTRA_MODES;
        bool isDrMode = IsDirectionalMode(mode);
        int nTopPx = haveTop != 0 ? Math.Min(txwpx, xr + txwpx) : 0;
        int nLeftPx = haveLeft != 0 ? Math.Min(txhpx, yd + txhpx) : 0;
        if (!useFilterIntra && !isDrMode)
        {
            HighbdBuildNonDirectionalIntraPredictors(refp, refStride, dst, dstStride, mode, txSize, nTopPx, nLeftPx, bd);
            return;
        }

        int txw = TxSizeWideUnit[txSize], txh = TxSizeHighUnit[txSize];
        int miRow = -xd.MbToTopEdge >> (3 + MI_SIZE_LOG2);
        int miCol = -xd.MbToLeftEdge >> (3 + MI_SIZE_LOG2);
        int rightAvailable = miCol + ((colOff + txw) << ssX) < xd.TileMiColEnd ? 1 : 0;
        int bottomAvailable = (yd > 0) && (miRow + ((rowOff + txh) << ssY) < xd.TileMiRowEnd) ? 1 : 0;
        int partition = mbmi.Partition;
        int bsize = mbmi.Bsize;
        if (ssX != 0 || ssY != 0) bsize = ScaleChromaBsize(bsize, ssX, ssY);

        int pAngle = 0;
        int needTopRight = ExtendModes[mode] & NEED_ABOVERIGHT;
        int needBottomLeft = ExtendModes[mode] & NEED_BOTTOMLEFT;
        if (useFilterIntra) { needTopRight = 0; needBottomLeft = 0; }
        if (isDrMode)
        {
            pAngle = ModeToAngleMap[mode] + angleDelta;
            needTopRight = pAngle < 90 ? 1 : 0;
            needBottomLeft = pAngle > 180 ? 1 : 0;
        }
        int haveTopRight = needTopRight != 0
            ? HasTopRight(sbSize, bsize, miRow, miCol, haveTop, rightAvailable, partition, txSize, rowOff, colOff, ssX, ssY) : -1;
        int haveBottomLeft = needBottomLeft != 0
            ? HasBottomLeft(sbSize, bsize, miRow, miCol, bottomAvailable, haveLeft, partition, txSize, rowOff, colOff, ssX, ssY) : -1;
        int disableEdgeFilter = enableIntraEdgeFilter ? 0 : 1;
        int intraEdgeFilterType = GetIntraEdgeFilterType(xd, plane);
        int nTopRightPx = haveTopRight > 0 ? Math.Min(txwpx, xr) : haveTopRight;
        int nBottomLeftPx = haveBottomLeft > 0 ? Math.Min(txhpx, yd) : haveBottomLeft;
        HighbdBuildDirectionalAndFilterIntraPredictors(refp, refStride, dst, dstStride, mode, pAngle, filterIntraMode, txSize,
            disableEdgeFilter, nTopPx, nTopRightPx, nLeftPx, nBottomLeftPx, intraEdgeFilterType, bd);
    }

    /// <summary>av1_predict_intra_block_facade (high bit depth dst).</summary>
    private static void PredictIntraBlockFacadeHbd(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int plane,
        int blkCol, int blkRow, int txSize)
    {
        AomMbModeInfo mbmi = xd.Mi0;
        AomMbdPlane pd = xd.Plane[plane];
        int dstStride = pd.Dst.Stride;
        int mode = plane == 0 ? mbmi.Mode : GetUvMode(mbmi.UvMode);
        bool usePalette = (plane != 0 ? mbmi.Palette.PaletteSize1 : mbmi.Palette.PaletteSize0) > 0;
        int filterIntraMode = plane == 0 && mbmi.UseFilterIntra != 0 ? mbmi.FilterIntraMode : FILTER_INTRA_MODES;
        int angleDelta = mbmi.AngleDelta[plane != 0 ? 1 : 0] * ANGLE_STEP;
        fixed (ushort* buf = pd.Dst.Buf16)
        {
            ushort* dst = buf + pd.Dst.Offset + ((blkRow * dstStride + blkCol) << MI_SIZE_LOG2);
            if (plane != 0 && mbmi.UvMode == UV_CFL_PRED)
            {
                AomCflCtx cfl = xd.Cfl;
                int predPlane = AomCfl.GetCflPredType(plane);
                if (!cfl.DcPredIsCached[predPlane])
                {
                    PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, pd.Width, pd.Height, txSize, mode, angleDelta,
                        usePalette, filterIntraMode, dst, dstStride, dst, dstStride, blkCol, blkRow, plane);
                    if (cfl.UseDcPredCache)
                    {
                        AomCfl.CflStoreDcPredHbd(xd, dst, predPlane, TxSizeWide[txSize]);
                        cfl.DcPredIsCached[predPlane] = true;
                    }
                }
                else AomCfl.CflLoadDcPredHbd(xd, dst, dstStride, txSize, predPlane);
                AomCfl.CflPredictBlockHbd(xd, dst, dstStride, txSize, plane);
                return;
            }
            PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, pd.Width, pd.Height, txSize, mode, angleDelta,
                usePalette, filterIntraMode, dst, dstStride, dst, dstStride, blkCol, blkRow, plane);
        }
    }
}
