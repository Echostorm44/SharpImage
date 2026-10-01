using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>
/// libaom 3.14.1 av1/common/reconintra.c (lowbd): intra prediction of one transform block: edge availability
/// (has_top_right / has_bottom_left), edge preparation (filter / upsample / corner), the directional predictors
/// (zones 1-3), the filter-intra predictor, the non-directional predictors (AomIntraPred), palette, and the CfL branch
/// of av1_predict_intra_block_facade (AomCfl). Pointer-based like the C; a highbd (ushort) twin of the builders can be
/// added with the same shape.
/// </summary>
[SkipLocalsInit]
internal static unsafe partial class AomReconIntra
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>av1_is_directional_mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDirectionalMode(int mode) => mode >= V_PRED && mode <= D67_PRED;

    /// <summary>av1_is_diagonal_mode.</summary>
    public static bool IsDiagonalMode(int mode) => mode >= D45_PRED && mode <= D67_PRED;

    /// <summary>av1_use_angle_delta.</summary>
    public static bool UseAngleDelta(int bsize) => bsize >= BLOCK_8X8;

    /// <summary>get_uv_mode (blockd.h).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetUvMode(int uvMode) => Uv2y[uvMode];

    /// <summary>is_inter_block (blockd.h).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInterBlock(AomMbModeInfo mbmi) => mbmi.UseIntrabc != 0 || mbmi.RefFrame0 > INTRA_FRAME;

    /// <summary>av1_get_dx.</summary>
    public static int GetDx(int angle)
    {
        if (angle > 0 && angle < 90) return DrIntraDerivative[angle];
        if (angle > 90 && angle < 180) return DrIntraDerivative[180 - angle];
        // In this case, we are not really going to use dx. We may return any value.
        return 1;
    }

    /// <summary>av1_get_dy.</summary>
    public static int GetDy(int angle)
    {
        if (angle > 90 && angle < 180) return DrIntraDerivative[angle - 90];
        if (angle > 180 && angle < 270) return DrIntraDerivative[270 - angle];
        // In this case, we are not really going to use dy. We may return any value.
        return 1;
    }

    /// <summary>av1_use_intra_edge_upsample.</summary>
    public static int UseIntraEdgeUpsample(int bs0, int bs1, int delta, int type)
    {
        int d = AbsI(delta);
        int blkWh = bs0 + bs1;
        if (d == 0 || d >= 40) return 0;
        return type != 0 ? (blkWh <= 8 ? 1 : 0) : (blkWh <= 16 ? 1 : 0);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Edge availability
    // ---------------------------------------------------------------------------------------------------------------

    private static byte[] GetHasTrTable(int partition, int bsize) =>
        partition == PARTITION_VERT_A || partition == PARTITION_VERT_B ? has_tr_vert_tables[bsize]! : has_tr_tables[bsize];

    /// <summary>has_top_right (reconintra.c, static).</summary>
    public static int HasTopRight(int sbSize, int bsize, int miRow, int miCol, int topAvailable, int rightAvailable,
        int partition, int txsz, int rowOff, int colOff, int ssX, int ssY)
    {
        if (topAvailable == 0 || rightAvailable == 0) return 0;

        int bwUnit = MiSizeWide[bsize];
        int planeBwUnit = Math.Max(bwUnit >> ssX, 1);
        int topRightCountUnit = TxSizeWideUnit[txsz];

        if (rowOff > 0)
        {
            // Just need to check if enough pixels on the right.
            if (BlockSizeWide[bsize] > BlockSizeWide[BLOCK_64X64])
            {
                // Special case: For 128x128 blocks, the transform unit whose top-right corner is at the center of the
                // block does in fact have pixels available at its top-right corner.
                if (rowOff == MiSizeHigh[BLOCK_64X64] >> ssY &&
                    colOff + topRightCountUnit == MiSizeWide[BLOCK_64X64] >> ssX)
                    return 1;
                int planeBwUnit64 = MiSizeWide[BLOCK_64X64] >> ssX;
                int colOff64 = colOff % planeBwUnit64;
                return colOff64 + topRightCountUnit < planeBwUnit64 ? 1 : 0;
            }
            return colOff + topRightCountUnit < planeBwUnit ? 1 : 0;
        }
        else
        {
            // All top-right pixels are in the block above, which is already available.
            if (colOff + topRightCountUnit < planeBwUnit) return 1;

            int bwInMiLog2 = MiSizeWideLog2[bsize];
            int bhInMiLog2 = MiSizeHighLog2[bsize];
            int sbMiSize = MiSizeHigh[sbSize];
            int blkRowInSb = (miRow & (sbMiSize - 1)) >> bhInMiLog2;
            int blkColInSb = (miCol & (sbMiSize - 1)) >> bwInMiLog2;

            // Top row of superblock: so top-right pixels are in the top and/or top-right superblocks, both of which
            // are already available.
            if (blkRowInSb == 0) return 1;

            // Rightmost column of superblock (and not the top row): so top-right pixels fall in the right superblock,
            // which is not available yet.
            if (((blkColInSb + 1) << bwInMiLog2) >= sbMiSize) return 0;

            // General case (neither top row nor rightmost column): check if the top-right block is coded before the
            // current block.
            int thisBlkIndex = ((blkRowInSb + 0) << (MAX_MIB_SIZE_LOG2 - bwInMiLog2)) + blkColInSb + 0;
            int idx1 = thisBlkIndex / 8;
            int idx2 = thisBlkIndex % 8;
            byte[] hasTrTable = GetHasTrTable(partition, bsize);
            return (hasTrTable[idx1] >> idx2) & 1;
        }
    }

    private static byte[] GetHasBlTable(int partition, int bsize) =>
        partition == PARTITION_VERT_A || partition == PARTITION_VERT_B ? has_bl_vert_tables[bsize]! : has_bl_tables[bsize];

    /// <summary>has_bottom_left (reconintra.c, static).</summary>
    public static int HasBottomLeft(int sbSize, int bsize, int miRow, int miCol, int bottomAvailable, int leftAvailable,
        int partition, int txsz, int rowOff, int colOff, int ssX, int ssY)
    {
        if (bottomAvailable == 0 || leftAvailable == 0) return 0;

        // Special case for 128x* blocks, when col_off is half the block width. This is needed because 128x*
        // superblocks are divided into 64x* blocks in raster order
        if (BlockSizeWide[bsize] > BlockSizeWide[BLOCK_64X64] && colOff > 0)
        {
            int planeBwUnit64 = MiSizeWide[BLOCK_64X64] >> ssX;
            int colOff64 = colOff % planeBwUnit64;
            if (colOff64 == 0)
            {
                // We are at the left edge of top-right or bottom-right 64x* block.
                int planeBhUnit64 = MiSizeHigh[BLOCK_64X64] >> ssY;
                int rowOff64 = rowOff % planeBhUnit64;
                int planeBhUnit = Math.Min(MiSizeHigh[bsize] >> ssY, planeBhUnit64);
                // Check if all bottom-left pixels are in the left 64x* block (which is already coded).
                return rowOff64 + TxSizeHighUnit[txsz] < planeBhUnit ? 1 : 0;
            }
        }

        if (colOff > 0)
        {
            // Bottom-left pixels are in the bottom-left block, which is not available.
            return 0;
        }
        else
        {
            int bhUnit = MiSizeHigh[bsize];
            int planeBhUnit = Math.Max(bhUnit >> ssY, 1);
            int bottomLeftCountUnit = TxSizeHighUnit[txsz];

            // All bottom-left pixels are in the left block, which is already available.
            if (rowOff + bottomLeftCountUnit < planeBhUnit) return 1;

            int bwInMiLog2 = MiSizeWideLog2[bsize];
            int bhInMiLog2 = MiSizeHighLog2[bsize];
            int sbMiSize = MiSizeHigh[sbSize];
            int blkRowInSb = (miRow & (sbMiSize - 1)) >> bhInMiLog2;
            int blkColInSb = (miCol & (sbMiSize - 1)) >> bwInMiLog2;

            // Leftmost column of superblock: so bottom-left pixels maybe in the left and/or bottom-left superblocks.
            // But only the left superblock is available, so check if all required pixels fall in that superblock.
            if (blkColInSb == 0)
            {
                int blkStartRowOff = blkRowInSb << (bhInMiLog2 + MI_SIZE_LOG2 - MI_SIZE_LOG2) >> ssY;
                int rowOffInSb = blkStartRowOff + rowOff;
                int sbHeightUnit = sbMiSize >> ssY;
                return rowOffInSb + bottomLeftCountUnit < sbHeightUnit ? 1 : 0;
            }

            // Bottom row of superblock (and not the leftmost column): so bottom-left pixels fall in the bottom
            // superblock, which is not available yet.
            if (((blkRowInSb + 1) << bhInMiLog2) >= sbMiSize) return 0;

            // General case (neither leftmost column nor bottom row): check if the bottom-left block is coded before
            // the current block.
            int thisBlkIndex = ((blkRowInSb + 0) << (MAX_MIB_SIZE_LOG2 - bwInMiLog2)) + blkColInSb + 0;
            int idx1 = thisBlkIndex / 8;
            int idx2 = thisBlkIndex % 8;
            byte[] hasBlTable = GetHasBlTable(partition, bsize);
            return (hasBlTable[idx1] >> idx2) & 1;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Directional predictors
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>av1_dr_prediction_z1 (0 &lt; angle &lt; 90) as libaom's dispatched AVX2 kernel computes it. It equals
    /// av1_dr_prediction_z1_c except with an upsampled edge (bw + bh &lt;= 16): dr_prediction_z1_HxW_internal_avx2 takes
    /// (max_base_x - base) &gt;&gt; upsample_above columns from the interpolation (the C takes every column with
    /// base + 2c &lt; max_base_x, one more when max_base_x - base is odd) and fills the row with above[max_base_x] as
    /// soon as that count is &lt;= 0.</summary>
    public static void DrPredictionZ1(byte* dst, nint stride, int bw, int bh, byte* above, byte* left,
        int upsampleAbove, int dx, int dy)
    {
        int maxBaseX = ((bw + bh) - 1) << upsampleAbove;
        int fracBits = 6 - upsampleAbove;
        int baseInc = 1 << upsampleAbove;
        byte fill = above[maxBaseX];
        int x = dx;
        for (int r = 0; r < bh; ++r, dst += stride, x += dx)
        {
            int b = x >> fracBits;
            int shift = ((x << upsampleAbove) & 0x3F) >> 1;
            int valid = (maxBaseX - b) >> upsampleAbove;

            if (valid <= 0)
            {
                for (int i = r; i < bh; ++i)
                {
                    Unsafe.InitBlockUnaligned(dst, fill, (uint)bw);
                    dst += stride;
                }
                return;
            }

            int n = Math.Min(valid, bw);
            for (int c = 0; c < n; ++c, b += baseInc)
            {
                int val = above[b] * (32 - shift) + above[b + 1] * shift;
                dst[c] = (byte)((val + 16) >> 5);
            }
            for (int c = n; c < bw; ++c) dst[c] = fill;
        }
    }

    /// <summary>av1_dr_prediction_z2_c: 90 &lt; angle &lt; 180.</summary>
    public static void DrPredictionZ2(byte* dst, nint stride, int bw, int bh, byte* above, byte* left,
        int upsampleAbove, int upsampleLeft, int dx, int dy)
    {
        int minBaseX = -(1 << upsampleAbove);
        int fracBitsX = 6 - upsampleAbove;
        int fracBitsY = 6 - upsampleLeft;

        for (int r = 0; r < bh; ++r)
        {
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
                    val = (val + 16) >> 5;
                }
                else
                {
                    x = c + 1;
                    y = (r << 6) - x * dy;
                    int baseY = y >> fracBitsY;
                    int shift = ((y * (1 << upsampleLeft)) & 0x3F) >> 1;
                    val = left[baseY] * (32 - shift) + left[baseY + 1] * shift;
                    val = (val + 16) >> 5;
                }
                dst[c] = (byte)val;
            }
            dst += stride;
        }
    }

    /// <summary>av1_dr_prediction_z3 (180 &lt; angle &lt; 270) as libaom's dispatched AVX2 kernel computes it (zone 1 on the
    /// left edge, transposed): like av1_dr_prediction_z3_c except that with an upsampled edge only
    /// (max_base_y - base) &gt;&gt; upsample_left rows of a column are interpolated (see DrPredictionZ1).</summary>
    public static void DrPredictionZ3(byte* dst, nint stride, int bw, int bh, byte* above, byte* left,
        int upsampleLeft, int dx, int dy)
    {
        int maxBaseY = (bw + bh - 1) << upsampleLeft;
        int fracBits = 6 - upsampleLeft;
        int baseInc = 1 << upsampleLeft;
        byte fill = left[maxBaseY];
        int y = dy;
        for (int c = 0; c < bw; ++c, y += dy)
        {
            int b = y >> fracBits;
            int shift = ((y << upsampleLeft) & 0x3F) >> 1;
            int valid = (maxBaseY - b) >> upsampleLeft;
            int n = valid <= 0 ? 0 : Math.Min(valid, bh);
            int r = 0;
            for (; r < n; ++r, b += baseInc)
            {
                int val = left[b] * (32 - shift) + left[b + 1] * shift;
                dst[r * stride + c] = (byte)((val + 16) >> 5);
            }
            for (; r < bh; ++r) dst[r * stride + c] = fill;
        }
    }

    /// <summary>dr_predictor (reconintra.c, static).</summary>
    public static void DrPredictor(byte* dst, nint stride, int txSize, byte* above, byte* left, int upsampleAbove,
        int upsampleLeft, int angle)
    {
        int dx = GetDx(angle);
        int dy = GetDy(angle);
        int bw = TxSizeWide[txSize];
        int bh = TxSizeHigh[txSize];

        bool simd = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        if (angle > 0 && angle < 90)
        {
            if (simd && upsampleAbove == 0) DrPredictionZ1Simd(dst, stride, bw, bh, above, dx);
            else if (simd && bw <= 8 && bh <= 8) DrPredictionZ1UpSimd(dst, stride, bw, bh, above, dx);
            else DrPredictionZ1(dst, stride, bw, bh, above, left, upsampleAbove, dx, dy);
        }
        else if (angle > 90 && angle < 180)
        {
            if (simd && upsampleAbove == 0 && upsampleLeft == 0) DrPredictionZ2Simd(dst, stride, bw, bh, above, left, dx, dy);
            else if (simd && bw <= 8) DrPredictionZ2UpSimd(dst, stride, bw, bh, above, left, upsampleAbove, upsampleLeft, dx, dy);
            else DrPredictionZ2(dst, stride, bw, bh, above, left, upsampleAbove, upsampleLeft, dx, dy);
        }
        else if (angle > 180 && angle < 270)
        {
            if (simd && upsampleLeft == 0) DrPredictionZ3Simd(dst, stride, bw, bh, left, dy);
            else if (simd && bw <= 8 && bh <= 8) DrPredictionZ3UpSimd(dst, stride, bw, bh, left, dy);
            else DrPredictionZ3(dst, stride, bw, bh, above, left, upsampleLeft, dx, dy);
        }
        else if (angle == 90)
            AomIntraPred.Pred(V_PRED, txSize, dst, stride, above, left);
        else if (angle == 180)
            AomIntraPred.Pred(H_PRED, txSize, dst, stride, above, left);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Filter intra
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>av1_filter_intra_predictor_c.</summary>
    public static void FilterIntraPredictor(byte* dst, nint stride, int txSize, byte* above, byte* left, int mode)
    {
        const int B = 33;
        Unsafe.SkipInit(out StackArr1089<byte> bufferBuf); byte* buffer = (byte*)Unsafe.AsPointer(ref bufferBuf[0]);
        int bw = TxSizeWide[txSize];
        int bh = TxSizeHigh[txSize];

        for (int r = 0; r < bh; ++r) buffer[(r + 1) * B] = left[r];
        Buffer.MemoryCopy(above - 1, buffer, bw + 1, bw + 1);

        ReadOnlySpan<sbyte> taps = FilterIntraTaps.Slice(mode * 64, 64);
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            // the 8 outputs of a 4x2 cell in 8 int lanes: sum over the 7 neighbours p_j of p_j * taps[k][j]
            Unsafe.SkipInit(out StackArr7<Vector256<int>> tcBuf); Span<Vector256<int>> tc = tcBuf;
            for (int j = 0; j < 7; j++)
                tc[j] = Vector256.Create(taps[j], taps[8 + j], taps[16 + j], taps[24 + j], taps[32 + j], taps[40 + j], taps[48 + j], taps[56 + j]);
            var rnd = Vector256.Create(1 << (FILTER_INTRA_SCALE_BITS - 1));
            for (int r = 1; r < bh + 1; r += 2)
                for (int c = 1; c < bw + 1; c += 4)
                {
                    byte* row0 = buffer + (r - 1) * B;
                    var pr = Vector256.Create((int)row0[c - 1]) * tc[0] + Vector256.Create((int)row0[c]) * tc[1]
                        + Vector256.Create((int)row0[c + 1]) * tc[2] + Vector256.Create((int)row0[c + 2]) * tc[3]
                        + Vector256.Create((int)row0[c + 3]) * tc[4] + Vector256.Create((int)buffer[r * B + c - 1]) * tc[5]
                        + Vector256.Create((int)buffer[(r + 1) * B + c - 1]) * tc[6];
                    var v = Vector256.ShiftRightArithmetic(pr + rnd, FILTER_INTRA_SCALE_BITS);
                    var w = System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper());
                    var b8 = System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(w, w).AsUInt32();
                    *(uint*)(buffer + r * B + c) = b8.GetElement(0);
                    *(uint*)(buffer + (r + 1) * B + c) = b8.GetElement(1);
                }
        }
        else
        for (int r = 1; r < bh + 1; r += 2)
            for (int c = 1; c < bw + 1; c += 4)
            {
                byte* row0 = buffer + (r - 1) * B;
                int p0 = row0[c - 1];
                int p1 = row0[c];
                int p2 = row0[c + 1];
                int p3 = row0[c + 2];
                int p4 = row0[c + 3];
                int p5 = buffer[r * B + c - 1];
                int p6 = buffer[(r + 1) * B + c - 1];
                for (int k = 0; k < 8; ++k)
                {
                    int rOffset = k >> 2;
                    int cOffset = k & 0x03;
                    ReadOnlySpan<sbyte> t = taps.Slice(k * 8, 8);
                    int pr = t[0] * p0 + t[1] * p1 + t[2] * p2 + t[3] * p3 + t[4] * p4 + t[5] * p5 + t[6] * p6;
                    // Section 7.11.2.3: Clip1(Round2Signed(pr, INTRA_FILTER_SCALE_BITS)); Clip1() clips a negative
                    // value to 0, so Round2Signed() can be Round2().
                    buffer[(r + rOffset) * B + c + cOffset] =
                        ClipPixel((pr + (1 << (FILTER_INTRA_SCALE_BITS - 1))) >> FILTER_INTRA_SCALE_BITS);
                }
            }

        for (int r = 0; r < bh; ++r)
        {
            Buffer.MemoryCopy(buffer + (r + 1) * B + 1, dst, bw, bw);
            dst += stride;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Intra edge
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>is_smooth (reconintra.c, static).</summary>
    public static bool IsSmooth(AomMbModeInfo mbmi, int plane)
    {
        if (plane == 0)
        {
            int mode = mbmi.Mode;
            return mode == SMOOTH_PRED || mode == SMOOTH_V_PRED || mode == SMOOTH_H_PRED;
        }
        // uv_mode is not set for inter blocks, so need to explicitly detect that case.
        if (IsInterBlock(mbmi)) return false;
        int uvMode = mbmi.UvMode;
        return uvMode == UV_SMOOTH_PRED || uvMode == UV_SMOOTH_V_PRED || uvMode == UV_SMOOTH_H_PRED;
    }

    /// <summary>get_intra_edge_filter_type (reconintra.c, static).</summary>
    public static int GetIntraEdgeFilterType(AomMacroblockD xd, int plane)
    {
        AomMbModeInfo? above, left;
        if (plane == 0) { above = xd.AboveMbmi; left = xd.LeftMbmi; }
        else { above = xd.ChromaAboveMbmi; left = xd.ChromaLeftMbmi; }
        return (above != null && IsSmooth(above, plane)) || (left != null && IsSmooth(left, plane)) ? 1 : 0;
    }

    /// <summary>intra_edge_filter_strength (reconintra.c, static).</summary>
    public static int IntraEdgeFilterStrength(int bs0, int bs1, int delta, int type)
    {
        int d = AbsI(delta);
        int strength = 0;
        int blkWh = bs0 + bs1;
        if (type == 0)
        {
            if (blkWh <= 8) { if (d >= 56) strength = 1; }
            else if (blkWh <= 12) { if (d >= 40) strength = 1; }
            else if (blkWh <= 16) { if (d >= 40) strength = 1; }
            else if (blkWh <= 24)
            {
                if (d >= 8) strength = 1;
                if (d >= 16) strength = 2;
                if (d >= 32) strength = 3;
            }
            else if (blkWh <= 32)
            {
                if (d >= 1) strength = 1;
                if (d >= 4) strength = 2;
                if (d >= 32) strength = 3;
            }
            else { if (d >= 1) strength = 3; }
        }
        else
        {
            if (blkWh <= 8)
            {
                if (d >= 40) strength = 1;
                if (d >= 64) strength = 2;
            }
            else if (blkWh <= 16)
            {
                if (d >= 20) strength = 1;
                if (d >= 48) strength = 2;
            }
            else if (blkWh <= 24) { if (d >= 4) strength = 3; }
            else { if (d >= 1) strength = 3; }
        }
        return strength;
    }

    // kernel[INTRA_EDGE_FILT][INTRA_EDGE_TAPS]
    private static ReadOnlySpan<byte> EdgeKernel => new byte[] { 0, 4, 8, 4, 0, 0, 5, 6, 5, 0, 2, 4, 4, 4, 2 };

    /// <summary>av1_filter_intra_edge as libaom's dispatched SSE4.1 kernel runs it: the filtered samples equal
    /// av1_filter_intra_edge_c, and the kernel also writes p[-1] = p[0] and p[sz .. sz + 15] = p[sz - 1] (its edge
    /// extension). The side effects are reproduced so the edge buffers hold exactly what libaom's do (the twins find no
    /// prediction that depends on them, but later stages read these buffers).</summary>
    public static void FilterIntraEdge(byte* p, int sz, int strength)
    {
        if (strength == 0) return;

        ReadOnlySpan<byte> kernel = EdgeKernel.Slice((strength - 1) * INTRA_EDGE_TAPS, INTRA_EDGE_TAPS);
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            // the edge with its clamped neighbours materialised (e[-2], e[-1] = p[0]; e[sz ..] = p[sz - 1]), then 16 taps
            // sums at a time; the stores past p[sz - 1] are overwritten by the extension below
            Unsafe.SkipInit(out StackArr163<byte> bufBuf); byte* buf = (byte*)Unsafe.AsPointer(ref bufBuf[0]);
            byte* e = buf + 2;
            Buffer.MemoryCopy(p, e, 129, sz);
            byte first = e[0], last = e[sz - 1];
            e[-2] = first; e[-1] = first;
            Unsafe.InitBlockUnaligned(e + sz, last, 32);
            var k0 = System.Runtime.Intrinsics.Vector256.Create((short)kernel[0]);
            var k1 = System.Runtime.Intrinsics.Vector256.Create((short)kernel[1]);
            var k2 = System.Runtime.Intrinsics.Vector256.Create((short)kernel[2]);
            var eight = System.Runtime.Intrinsics.Vector256.Create((short)8);
            for (int i = 1; i < sz; i += 16)
            {
                var a0 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.X86.Sse2.LoadVector128(e + i - 2));
                var a1 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.X86.Sse2.LoadVector128(e + i - 1));
                var a2 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.X86.Sse2.LoadVector128(e + i));
                var a3 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.X86.Sse2.LoadVector128(e + i + 1));
                var a4 = System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int16(System.Runtime.Intrinsics.X86.Sse2.LoadVector128(e + i + 2));
                // symmetric taps: k0 (e[i-2] + e[i+2]) + k1 (e[i-1] + e[i+1]) + k2 e[i]
                var s = System.Runtime.Intrinsics.Vector256.ShiftRightLogical((a0 + a4) * k0 + (a1 + a3) * k1 + a2 * k2 + eight, 4);
                System.Runtime.Intrinsics.X86.Sse2.Store(p + i, System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(s.GetLower(), s.GetUpper()));
            }
            p[-1] = first;
            Unsafe.InitBlockUnaligned(p + sz, last, 16);
            return;
        }
        Unsafe.SkipInit(out StackArr129<byte> edgeBuf); byte* edge = (byte*)Unsafe.AsPointer(ref edgeBuf[0]);
        Buffer.MemoryCopy(p, edge, 129, sz);
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
            s = (s + 8) >> 4;
            p[i] = (byte)s;
        }
        // av1_filter_intra_edge_sse4_1's first / last sample extension
        p[-1] = edge[0];
        Unsafe.InitBlockUnaligned(p + sz, edge[sz - 1], 16);
    }

    /// <summary>filter_intra_edge_corner (reconintra.c, static).</summary>
    public static void FilterIntraEdgeCorner(byte* pAbove, byte* pLeft)
    {
        int s = pLeft[0] * 5 + pAbove[-1] * 6 + pAbove[0] * 5;
        s = (s + 8) >> 4;
        pAbove[-1] = (byte)s;
        pLeft[-1] = (byte)s;
    }

    /// <summary>av1_upsample_intra_edge as libaom's dispatched SSE4.1 kernel runs it: interpolates the half-sample
    /// positions of p[-1 .. sz-1] into p[-2 .. 2sz-2] (equal to av1_upsample_intra_edge_c there), and like the kernel
    /// writes whole 16-sample chunks: p[-2 .. 29] (p[-2 .. 61] for sz = 16), the tail computed from the samples that
    /// follow p[sz] (zeros past the 32 loaded for the second chunk).</summary>
    public static void UpsampleIntraEdge(byte* p, int sz)
    {
        // Extend first/last samples (upper-left p[-1], last p[sz-1]) to support 4-tap filter
        p[-2] = p[-1];
        p[sz] = p[sz - 1];
        Unsafe.SkipInit(out StackArr48<byte> inpBuf); byte* inp = (byte*)Unsafe.AsPointer(ref inpBuf[0]);
        Buffer.MemoryCopy(p - 2, inp, 32, 32);
        Unsafe.InitBlockUnaligned(inp + 32, 0, 16);
        int chunks = (sz + 1 + 15) >> 4;
        for (int k = 0; k < chunks * 16; k++)
        {
            int s = -inp[k] + 9 * inp[k + 1] + 9 * inp[k + 2] - inp[k + 3];
            p[2 * k - 2] = inp[k + 1];
            p[2 * k - 1] = ClipPixel((s + 8) >> 4);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Builders
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>build_directional_and_filter_intra_predictors (reconintra.c, static).</summary>
    public static void BuildDirectionalAndFilterIntraPredictors(byte* refp, nint refStride, byte* dst, nint dstStride,
        int mode, int pAngle, int filterIntraMode, int txSize, int disableEdgeFilter, int nTopPx, int nTopRightPx,
        int nLeftPx, int nBottomLeftPx, int intraEdgeFilterType)
    {
        int i;
        byte* aboveRef = refp - refStride;
        byte* leftRef = refp - 1;
        Unsafe.SkipInit(out StackArr160<byte> leftBuf); byte* leftData = (byte*)Unsafe.AsPointer(ref leftBuf[0]);
        Unsafe.SkipInit(out StackArr160<byte> aboveBuf); byte* aboveData = (byte*)Unsafe.AsPointer(ref aboveBuf[0]);
        byte* aboveRow = aboveData + 16;
        byte* leftCol = leftData + 16;
        int txwpx = TxSizeWide[txSize];
        int txhpx = TxSizeHigh[txSize];
        int needLeft = ExtendModes[mode] & NEED_LEFT;
        int needAbove = ExtendModes[mode] & NEED_ABOVE;
        int needAboveLeft = ExtendModes[mode] & NEED_ABOVELEFT;
        bool isDrMode = IsDirectionalMode(mode);
        bool useFilterIntra = filterIntraMode != FILTER_INTRA_MODES;
        Unsafe.InitBlockUnaligned(leftData, 129, NUM_INTRA_NEIGHBOUR_PIXELS);
        Unsafe.InitBlockUnaligned(aboveData, 127, NUM_INTRA_NEIGHBOUR_PIXELS);

        // The default values if ref pixels are not available:
        // 128 127 127 .. 127 127 127 127 127 127
        // 129  A   B  ..  Y   Z
        // 129  C   D  ..  W   X
        // 129  E   F  ..  U   V
        // 129  G   H  ..  S   T   T   T   T   T
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
            if (needLeft != 0) val = nTopPx > 0 ? aboveRef[0] : 129;
            else val = nLeftPx > 0 ? leftRef[0] : 127;
            for (i = 0; i < txhpx; ++i)
            {
                Unsafe.InitBlockUnaligned(dst, (byte)val, (uint)txwpx);
                dst += dstStride;
            }
            return;
        }

        // NEED_LEFT
        if (needLeft != 0)
        {
            int numLeftPixelsNeeded = txhpx + (nBottomLeftPx >= 0 ? txwpx : 0);
            i = 0;
            if (nLeftPx > 0)
            {
                GatherColumn(leftCol, leftRef, refStride, nBottomLeftPx > 0 ? txhpx + nBottomLeftPx : nLeftPx);
                i = nBottomLeftPx > 0 ? txhpx + nBottomLeftPx : nLeftPx;
                if (i < numLeftPixelsNeeded)
                    Unsafe.InitBlockUnaligned(leftCol + i, leftCol[i - 1], (uint)(numLeftPixelsNeeded - i));
            }
            else if (nTopPx > 0)
            {
                Unsafe.InitBlockUnaligned(leftCol, aboveRef[0], (uint)numLeftPixelsNeeded);
            }
        }

        // NEED_ABOVE
        if (needAbove != 0)
        {
            int numTopPixelsNeeded = txwpx + (nTopRightPx >= 0 ? txhpx : 0);
            if (nTopPx > 0)
            {
                Buffer.MemoryCopy(aboveRef, aboveRow, nTopPx, nTopPx);
                i = nTopPx;
                if (nTopRightPx > 0)
                {
                    Buffer.MemoryCopy(aboveRef + txwpx, aboveRow + txwpx, nTopRightPx, nTopRightPx);
                    i += nTopRightPx;
                }
                if (i < numTopPixelsNeeded)
                    Unsafe.InitBlockUnaligned(aboveRow + i, aboveRow[i - 1], (uint)(numTopPixelsNeeded - i));
            }
            else if (nLeftPx > 0)
            {
                Unsafe.InitBlockUnaligned(aboveRow, leftRef[0], (uint)numTopPixelsNeeded);
            }
        }

        if (needAboveLeft != 0)
        {
            if (nTopPx > 0 && nLeftPx > 0) aboveRow[-1] = aboveRef[-1];
            else if (nTopPx > 0) aboveRow[-1] = aboveRef[0];
            else if (nLeftPx > 0) aboveRow[-1] = leftRef[0];
            else aboveRow[-1] = 128;
            leftCol[-1] = aboveRow[-1];
        }

        if (useFilterIntra)
        {
            FilterIntraPredictor(dst, dstStride, txSize, aboveRow, leftCol, filterIntraMode);
            return;
        }

        int upsampleAbove = 0;
        int upsampleLeft = 0;
        if (disableEdgeFilter == 0)
        {
            bool needRight = pAngle < 90;
            bool needBottom = pAngle > 180;
            if (pAngle != 90 && pAngle != 180)
            {
                const int abLe = 1;
                if (needAbove != 0 && needLeft != 0 && (txwpx + txhpx >= 24))
                    FilterIntraEdgeCorner(aboveRow, leftCol);
                if (needAbove != 0 && nTopPx > 0)
                {
                    int strength = IntraEdgeFilterStrength(txwpx, txhpx, pAngle - 90, intraEdgeFilterType);
                    int nPx = nTopPx + abLe + (needRight ? txhpx : 0);
                    FilterIntraEdge(aboveRow - abLe, nPx, strength);
                }
                if (needLeft != 0 && nLeftPx > 0)
                {
                    int strength = IntraEdgeFilterStrength(txhpx, txwpx, pAngle - 180, intraEdgeFilterType);
                    int nPx = nLeftPx + abLe + (needBottom ? txwpx : 0);
                    FilterIntraEdge(leftCol - abLe, nPx, strength);
                }
            }
            upsampleAbove = UseIntraEdgeUpsample(txwpx, txhpx, pAngle - 90, intraEdgeFilterType);
            if (needAbove != 0 && upsampleAbove != 0)
            {
                int nPx = txwpx + (needRight ? txhpx : 0);
                UpsampleIntraEdge(aboveRow, nPx);
            }
            upsampleLeft = UseIntraEdgeUpsample(txhpx, txwpx, pAngle - 180, intraEdgeFilterType);
            if (needLeft != 0 && upsampleLeft != 0)
            {
                int nPx = txhpx + (needBottom ? txwpx : 0);
                UpsampleIntraEdge(leftCol, nPx);
            }
        }
        DrPredictor(dst, dstStride, txSize, aboveRow, leftCol, upsampleAbove, upsampleLeft, pAngle);
    }

    /// <summary>dst[i] = src[i * stride] for i &lt; n (the left edge column), four at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GatherColumn(byte* dst, byte* src, nint stride, int n)
    {
        int i = 0;
        for (; i + 4 <= n; i += 4, src += 4 * stride)
            *(uint*)(dst + i) = (uint)(src[0] | src[stride] << 8 | src[2 * stride] << 16 | src[3 * stride] << 24);
        for (; i < n; i++, src += stride) dst[i] = *src;
    }

    /// <summary>build_non_directional_intra_predictors (reconintra.c, static): DC, SMOOTH*, PAETH.</summary>
    public static void BuildNonDirectionalIntraPredictors(byte* refp, nint refStride, byte* dst, nint dstStride,
        int mode, int txSize, int nTopPx, int nLeftPx)
    {
        byte* aboveRef = refp - refStride;
        byte* leftRef = refp - 1;
        int txwpx = TxSizeWide[txSize];
        int txhpx = TxSizeHigh[txSize];
        int needLeft = ExtendModes[mode] & NEED_LEFT;
        int needAbove = ExtendModes[mode] & NEED_ABOVE;
        int needAboveLeft = ExtendModes[mode] & NEED_ABOVELEFT;
        int i;

        if ((needAbove == 0 && nLeftPx == 0) || (needLeft == 0 && nTopPx == 0))
        {
            int val;
            if (needLeft != 0) val = nTopPx > 0 ? aboveRef[0] : 129;
            else val = nLeftPx > 0 ? leftRef[0] : 127;
            for (i = 0; i < txhpx; ++i)
            {
                Unsafe.InitBlockUnaligned(dst, (byte)val, (uint)txwpx);
                dst += dstStride;
            }
            return;
        }

        Unsafe.SkipInit(out StackArr160<byte> leftBuf); byte* leftData = (byte*)Unsafe.AsPointer(ref leftBuf[0]);
        Unsafe.SkipInit(out StackArr160<byte> aboveBuf); byte* aboveData = (byte*)Unsafe.AsPointer(ref aboveBuf[0]);
        byte* aboveRow = aboveData + 16;
        byte* leftCol = leftData + 16;

        if (needLeft != 0)
        {
            Unsafe.InitBlockUnaligned(leftData, 129, NUM_INTRA_NEIGHBOUR_PIXELS);
            if (nLeftPx > 0)
            {
                GatherColumn(leftCol, leftRef, refStride, nLeftPx);
                i = nLeftPx;
                if (i < txhpx) Unsafe.InitBlockUnaligned(leftCol + i, leftCol[i - 1], (uint)(txhpx - i));
            }
            else if (nTopPx > 0)
            {
                Unsafe.InitBlockUnaligned(leftCol, aboveRef[0], (uint)txhpx);
            }
        }

        if (needAbove != 0)
        {
            Unsafe.InitBlockUnaligned(aboveData, 127, NUM_INTRA_NEIGHBOUR_PIXELS);
            if (nTopPx > 0)
            {
                Buffer.MemoryCopy(aboveRef, aboveRow, nTopPx, nTopPx);
                i = nTopPx;
                if (i < txwpx) Unsafe.InitBlockUnaligned(aboveRow + i, aboveRow[i - 1], (uint)(txwpx - i));
            }
            else if (nLeftPx > 0)
            {
                Unsafe.InitBlockUnaligned(aboveRow, leftRef[0], (uint)txwpx);
            }
        }

        if (needAboveLeft != 0)
        {
            if (nTopPx > 0 && nLeftPx > 0) aboveRow[-1] = aboveRef[-1];
            else if (nTopPx > 0) aboveRow[-1] = aboveRef[0];
            else if (nLeftPx > 0) aboveRow[-1] = leftRef[0];
            else aboveRow[-1] = 128;
            leftCol[-1] = aboveRow[-1];
        }

        if (mode == DC_PRED)
            AomIntraPred.DcPred(nLeftPx > 0 ? 1 : 0, nTopPx > 0 ? 1 : 0, txSize, dst, dstStride, aboveRow, leftCol);
        else
            AomIntraPred.Pred(mode, txSize, dst, dstStride, aboveRow, leftCol);
    }

    /// <summary>scale_chroma_bsize (reconintra.c, static).</summary>
    public static int ScaleChromaBsize(int bsize, int subsamplingX, int subsamplingY)
    {
        int bs = bsize;
        switch (bsize)
        {
            case BLOCK_4X4:
                if (subsamplingX == 1 && subsamplingY == 1) bs = BLOCK_8X8;
                else if (subsamplingX == 1) bs = BLOCK_8X4;
                else if (subsamplingY == 1) bs = BLOCK_4X8;
                break;
            case BLOCK_4X8:
                if (subsamplingX == 1 && subsamplingY == 1) bs = BLOCK_8X8;
                else if (subsamplingX == 1) bs = BLOCK_8X8;
                else if (subsamplingY == 1) bs = BLOCK_4X8;
                break;
            case BLOCK_8X4:
                if (subsamplingX == 1 && subsamplingY == 1) bs = BLOCK_8X8;
                else if (subsamplingX == 1) bs = BLOCK_8X4;
                else if (subsamplingY == 1) bs = BLOCK_8X8;
                break;
            case BLOCK_4X16:
                if (subsamplingX == 1 && subsamplingY == 1) bs = BLOCK_8X16;
                else if (subsamplingX == 1) bs = BLOCK_8X16;
                else if (subsamplingY == 1) bs = BLOCK_4X16;
                break;
            case BLOCK_16X4:
                if (subsamplingX == 1 && subsamplingY == 1) bs = BLOCK_16X8;
                else if (subsamplingX == 1) bs = BLOCK_16X4;
                else if (subsamplingY == 1) bs = BLOCK_16X8;
                break;
        }
        return bs;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // av1_predict_intra_block / facade
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>av1_predict_intra_block (array form: ref / dst are (array, offset) into the frame planes).</summary>
    public static void PredictIntraBlock(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int wpx, int hpx,
        int txSize, int mode, int angleDelta, bool usePalette, int filterIntraMode, byte[] refBuf, int refOffset,
        int refStride, byte[] dstBuf, int dstOffset, int dstStride, int colOff, int rowOff, int plane)
    {
        fixed (byte* r = refBuf)
        fixed (byte* d = dstBuf)
            PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, wpx, hpx, txSize, mode, angleDelta, usePalette,
                filterIntraMode, r + refOffset, refStride, d + dstOffset, dstStride, colOff, rowOff, plane);
    }

    /// <summary>av1_predict_intra_block.</summary>
    public static void PredictIntraBlock(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int wpx, int hpx,
        int txSize, int mode, int angleDelta, bool usePalette, int filterIntraMode, byte* refp, int refStride,
        byte* dst, int dstStride, int colOff, int rowOff, int plane)
    {
        AomMbModeInfo mbmi = xd.Mi0;
        int txwpx = TxSizeWide[txSize];
        int txhpx = TxSizeHigh[txSize];
        int x = colOff << MI_SIZE_LOG2;
        int y = rowOff << MI_SIZE_LOG2;

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
                byte* d = dst + r * dstStride;
                for (int c = 0; c < txwpx; ++c) d[c] = (byte)palette[palOff + mapArr[m + c]];
            }
            return;
        }

        AomMbdPlane pd = xd.Plane[plane];
        int ssX = pd.SubsamplingX;
        int ssY = pd.SubsamplingY;
        int haveTop = rowOff != 0 || (ssY != 0 ? xd.ChromaUpAvailable : xd.UpAvailable) ? 1 : 0;
        int haveLeft = colOff != 0 || (ssX != 0 ? xd.ChromaLeftAvailable : xd.LeftAvailable) ? 1 : 0;

        // Distance between the right edge of this prediction block to the frame right edge
        int xr = (xd.MbToRightEdge >> (3 + ssX)) + wpx - x - txwpx;
        // Distance between the bottom edge of this prediction block to the frame bottom edge
        int yd = (xd.MbToBottomEdge >> (3 + ssY)) + hpx - y - txhpx;
        bool useFilterIntra = filterIntraMode != FILTER_INTRA_MODES;
        bool isDrMode = IsDirectionalMode(mode);

        int nTopPx = haveTop != 0 ? Math.Min(txwpx, xr + txwpx) : 0;
        int nLeftPx = haveLeft != 0 ? Math.Min(txhpx, yd + txhpx) : 0;
        if (!useFilterIntra && !isDrMode)
        {
            BuildNonDirectionalIntraPredictors(refp, refStride, dst, dstStride, mode, txSize, nTopPx, nLeftPx);
            return;
        }

        int txw = TxSizeWideUnit[txSize];
        int txh = TxSizeHighUnit[txSize];
        int miRow = -xd.MbToTopEdge >> (3 + MI_SIZE_LOG2);
        int miCol = -xd.MbToLeftEdge >> (3 + MI_SIZE_LOG2);
        int rightAvailable = miCol + ((colOff + txw) << ssX) < xd.TileMiColEnd ? 1 : 0;
        int bottomAvailable = (yd > 0) && (miRow + ((rowOff + txh) << ssY) < xd.TileMiRowEnd) ? 1 : 0;

        int partition = mbmi.Partition;

        int bsize = mbmi.Bsize;
        // force 4x4 chroma component block size.
        if (ssX != 0 || ssY != 0) bsize = ScaleChromaBsize(bsize, ssX, ssY);

        int pAngle = 0;
        int needTopRight = ExtendModes[mode] & NEED_ABOVERIGHT;
        int needBottomLeft = ExtendModes[mode] & NEED_BOTTOMLEFT;

        if (useFilterIntra)
        {
            needTopRight = 0;
            needBottomLeft = 0;
        }
        if (isDrMode)
        {
            pAngle = ModeToAngleMap[mode] + angleDelta;
            needTopRight = pAngle < 90 ? 1 : 0;
            needBottomLeft = pAngle > 180 ? 1 : 0;
        }

        // Possible states for have_top_right(TR) and have_bottom_left(BL)
        // -1 : TR and BL are not needed
        //  0 : TR and BL are needed but not available
        // > 0 : TR and BL are needed and pixels are available
        int haveTopRight = needTopRight != 0
            ? HasTopRight(sbSize, bsize, miRow, miCol, haveTop, rightAvailable, partition, txSize, rowOff, colOff, ssX, ssY)
            : -1;
        int haveBottomLeft = needBottomLeft != 0
            ? HasBottomLeft(sbSize, bsize, miRow, miCol, bottomAvailable, haveLeft, partition, txSize, rowOff, colOff, ssX, ssY)
            : -1;

        int disableEdgeFilter = enableIntraEdgeFilter ? 0 : 1;
        int intraEdgeFilterType = GetIntraEdgeFilterType(xd, plane);
        int nTopRightPx = haveTopRight > 0 ? Math.Min(txwpx, xr) : haveTopRight;
        int nBottomLeftPx = haveBottomLeft > 0 ? Math.Min(txhpx, yd) : haveBottomLeft;
        BuildDirectionalAndFilterIntraPredictors(refp, refStride, dst, dstStride, mode, pAngle, filterIntraMode, txSize,
            disableEdgeFilter, nTopPx, nTopRightPx, nLeftPx, nBottomLeftPx, intraEdgeFilterType);
    }

    /// <summary>av1_predict_intra_block_facade: predicts the transform block at (blk_row, blk_col) (4x4 units within
    /// the plane block) into xd->plane[plane].dst. <paramref name="sbSize"/> / <paramref name="enableIntraEdgeFilter"/>
    /// are cm->seq_params->sb_size / enable_intra_edge_filter.</summary>
    public static void PredictIntraBlockFacade(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int plane,
        int blkCol, int blkRow, int txSize)
    {
        if (xd.Plane[plane].Dst.Buf16 != null)
        {
            PredictIntraBlockFacadeHbd(xd, sbSize, enableIntraEdgeFilter, plane, blkCol, blkRow, txSize);
            return;
        }
        AomMbModeInfo mbmi = xd.Mi0;
        AomMbdPlane pd = xd.Plane[plane];
        int dstStride = pd.Dst.Stride;
        int mode = plane == 0 ? mbmi.Mode : GetUvMode(mbmi.UvMode);
        bool usePalette = (plane != 0 ? mbmi.Palette.PaletteSize1 : mbmi.Palette.PaletteSize0) > 0;
        int filterIntraMode = plane == 0 && mbmi.UseFilterIntra != 0 ? mbmi.FilterIntraMode : FILTER_INTRA_MODES;
        int angleDelta = mbmi.AngleDelta[plane != 0 ? 1 : 0] * ANGLE_STEP;

        fixed (byte* buf = pd.Dst.Buf)
        {
            byte* dst = buf + pd.Dst.Offset + ((blkRow * dstStride + blkCol) << MI_SIZE_LOG2);
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
                        AomCfl.CflStoreDcPred(xd, dst, predPlane, TxSizeWide[txSize]);
                        cfl.DcPredIsCached[predPlane] = true;
                    }
                }
                else
                {
                    AomCfl.CflLoadDcPred(xd, dst, dstStride, txSize, predPlane);
                }
                AomCfl.CflPredictBlock(xd, dst, dstStride, txSize, plane);
                return;
            }
            PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, pd.Width, pd.Height, txSize, mode, angleDelta,
                usePalette, filterIntraMode, dst, dstStride, dst, dstStride, blkCol, blkRow, plane);
        }
    }
}
