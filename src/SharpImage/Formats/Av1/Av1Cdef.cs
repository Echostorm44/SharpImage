// AV1 CDEF (Constrained Directional Enhancement Filter)
// Ported from dav1d cdef_tmpl.c (filter kernel + direction finding) and
// cdef_apply_tmpl.c (per-SB-row application). All 8-bit.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 CDEF filter — direction finding, filter kernel, and per-SB-row application.
/// </summary>
public static class Av1Cdef
{
    // ========================================================================
    // Edge flags (cdef.h: CdefEdgeFlags)
    // ========================================================================

    [Flags]
    public enum EdgeFlags
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Top = 1 << 2,
        Bottom = 1 << 3,
        All = Left | Right | Top | Bottom
    }

    // ========================================================================
    // Direction offsets (tables.c: dav1d_cdef_directions)
    // Indexed as [dir + 2][pass], 12 entries (dir -2 to dir+9 wrapped).
    // Offsets are relative to tmp buffer with stride 12.
    // ========================================================================

    private static readonly sbyte[,] Directions = new sbyte[12, 2]
    {
        {  1 * 12 + 0,  2 * 12 + 0 }, // dir 6 (wrap-around for dir-2)
        {  1 * 12 + 0,  2 * 12 - 1 }, // dir 7
        { -1 * 12 + 1, -2 * 12 + 2 }, // dir 0
        {  0 * 12 + 1, -1 * 12 + 2 }, // dir 1
        {  0 * 12 + 1,  0 * 12 + 2 }, // dir 2
        {  0 * 12 + 1,  1 * 12 + 2 }, // dir 3
        {  1 * 12 + 1,  2 * 12 + 2 }, // dir 4
        {  1 * 12 + 0,  2 * 12 + 1 }, // dir 5
        {  1 * 12 + 0,  2 * 12 + 0 }, // dir 6
        {  1 * 12 + 0,  2 * 12 - 1 }, // dir 7
        { -1 * 12 + 1, -2 * 12 + 2 }, // dir 0 (wrap-around for dir+2)
        {  0 * 12 + 1, -1 * 12 + 2 }, // dir 1
    };

    /// <summary>
    /// UV direction remapping for 4:2:2 chroma subsampling.
    /// </summary>
    private static readonly byte[,] UvDirs = new byte[2, 8]
    {
        { 0, 1, 2, 3, 4, 5, 6, 7 }, // 4:4:4 / 4:2:0
        { 7, 0, 2, 4, 5, 6, 6, 6 }, // 4:2:2
    };

    private static readonly ushort[] DivTable = [840, 420, 280, 210, 168, 140, 120];

    // ========================================================================
    // Constrain function (cdef_tmpl.c: constrain)
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Constrain(int diff, int threshold, int shift)
    {
        int adiff = Math.Abs(diff);
        int val = Math.Min(adiff, Math.Max(0, threshold - (adiff >> shift)));
        return diff < 0 ? -val : val;
    }

    // ========================================================================
    // Direction Finding (cdef_tmpl.c: cdef_find_dir_c)
    // ========================================================================

    /// <summary>
    /// Find the dominant edge direction for an 8×8 block.
    /// Returns direction index (0-7) and sets variance.
    /// </summary>
    public static int FindDirection(ReadOnlySpan<ushort> img, int imgOffset, int stride, out uint variance, int bitDepth = 8)
    {
        int bdMin8 = bitDepth - 8;
        Span<int> partialSumHv0 = stackalloc int[8];
        Span<int> partialSumHv1 = stackalloc int[8];
        Span<int> partialSumDiag0 = stackalloc int[15];
        Span<int> partialSumDiag1 = stackalloc int[15];
        Span<int> partialSumAlt0 = stackalloc int[11];
        Span<int> partialSumAlt1 = stackalloc int[11];
        Span<int> partialSumAlt2 = stackalloc int[11];
        Span<int> partialSumAlt3 = stackalloc int[11];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int px = (img[imgOffset + x] >> bdMin8) - 128;
                partialSumDiag0[y + x] += px;
                partialSumAlt0[y + (x >> 1)] += px;
                partialSumHv0[y] += px;
                partialSumAlt1[3 + y - (x >> 1)] += px;
                partialSumDiag1[7 + y - x] += px;
                partialSumAlt2[3 - (y >> 1) + x] += px;
                partialSumHv1[x] += px;
                partialSumAlt3[(y >> 1) + x] += px;
            }
            imgOffset += stride;
        }

        Span<uint> cost = stackalloc uint[8];
        for (int n = 0; n < 8; n++)
        {
            cost[2] += (uint)(partialSumHv0[n] * partialSumHv0[n]);
            cost[6] += (uint)(partialSumHv1[n] * partialSumHv1[n]);
        }
        cost[2] *= 105;
        cost[6] *= 105;

        for (int n = 0; n < 7; n++)
        {
            uint d = DivTable[n];
            cost[0] += (uint)(partialSumDiag0[n] * partialSumDiag0[n] +
                              partialSumDiag0[14 - n] * partialSumDiag0[14 - n]) * d;
            cost[4] += (uint)(partialSumDiag1[n] * partialSumDiag1[n] +
                              partialSumDiag1[14 - n] * partialSumDiag1[14 - n]) * d;
        }
        cost[0] += (uint)(partialSumDiag0[7] * partialSumDiag0[7]) * 105;
        cost[4] += (uint)(partialSumDiag1[7] * partialSumDiag1[7]) * 105;

        cost[1] = ComputeAltCost(partialSumAlt0);
        cost[3] = ComputeAltCost(partialSumAlt1);
        cost[5] = ComputeAltCost(partialSumAlt2);
        cost[7] = ComputeAltCost(partialSumAlt3);

        int bestDir = 0;
        uint bestCost = cost[0];
        for (int n = 1; n < 8; n++)
        {
            if (cost[n] > bestCost)
            {
                bestCost = cost[n];
                bestDir = n;
            }
        }

        variance = (bestCost - cost[bestDir ^ 4]) >> 10;
        return bestDir;
    }

    // ========================================================================
    // Padding (cdef_tmpl.c: padding)
    // ========================================================================

    /// <summary>
    /// Fill extended input buffer (int16) for the filter kernel.
    /// tmp points to offset [2*stride+2] in a (h+4)×12 buffer.
    /// </summary>
    /// <summary>Encoder CDEF search: the 12x12 padded neighbourhood FilterBlock builds (block, 2-pixel border), built
    /// once so every strength can then be filtered from it with <see cref="FilterPadded"/>.</summary>
    internal static void PadBlock(Span<short> tmp144, ReadOnlySpan<ushort> src, int srcOffset, int srcStride,
        ReadOnlySpan<ushort> left, int leftOffset, int leftStride, ReadOnlySpan<ushort> top, int topOffset,
        ReadOnlySpan<ushort> bottom, int bottomOffset, int w, int h, EdgeFlags edges, int tbStride)
        => Padding(tmp144, 2 * 12 + 2, 12, src, srcOffset, srcStride, left, leftOffset, leftStride, top, topOffset,
            bottom, bottomOffset, w, h, edges, tbStride);

    /// <summary>Filters a block from its <see cref="PadBlock"/> neighbourhood into dst (AVX2 only; false otherwise).</summary>
    internal static bool FilterPadded(ReadOnlySpan<short> tmp144, Span<ushort> dst, int dstOffset, int dstStride,
        int priStrength, int secStrength, int dir, int damping, int w, int h, int bitDepth)
    {
        if (!Avx2.IsSupported) return false;
        if (dstStride == w && (w == 8 && (h & 1) == 0 || w == 4 && (h & 3) == 0) && !Cdef32)
        {
            FilterRows16(dst, dstOffset, tmp144, priStrength, secStrength, dir, damping, w, h, bitDepth - 8);
            if (CdefCheck)
            {
                var d2 = new ushort[w * h];
                FilterRowsV(d2, 0, w, tmp144, 2 * 12 + 2, priStrength, secStrength, dir, damping, w, h, bitDepth - 8);
                for (int i = 0; i < w * h; i++)
                    if (d2[i] != dst[dstOffset + i])
                        throw new InvalidOperationException($"cdef16 {w}x{h} pri {priStrength} sec {secStrength} dir {dir} damp {damping} bd {bitDepth} at {i}: {dst[dstOffset + i]} vs {d2[i]}; tmp [{string.Join(",", tmp144.ToArray())}]");
            }
        }
        else
            FilterRowsV(dst, dstOffset, dstStride, tmp144, 2 * 12 + 2, priStrength, secStrength, dir, damping, w, h, bitDepth - 8);
        return true;
    }

    private static readonly bool Cdef32 = Environment.GetEnvironmentVariable("AV1_CDEF32") == "1";
    private static readonly bool CdefCheck = Environment.GetEnvironmentVariable("AV1_CDEFCHECK") == "1";

    // FilterRowsV in 16-bit lanes (dav1d's cdef_filter_*_16bpc layout): 16 pixels per vector — two rows of an 8-wide block
    // or four of a 4-wide one — into a contiguous w-stride dst. Tap differences wrap in 16 bits; with |diff| read unsigned
    // the INT16_MIN padding sentinel still constrains to 0, the min clamp excludes it (unsigned min) and the max clamp
    // too (signed max). Every value in range fits 16 bits (|sum| <= 3648), so the results equal the 32-bit kernel's.
    private static void FilterRows16(Span<ushort> dst, int dstOffset, ReadOnlySpan<short> tmp,
        int priStrength, int secStrength, int dir, int damping, int w, int h, int bdMin8)
    {
        const int ts = 12, center = 2 * 12 + 2;
        bool pri = priStrength != 0, sec = secStrength != 0;
        short priTap0 = (short)(4 - ((priStrength >> bdMin8) & 1)), priTap1 = (short)((priTap0 & 3) | 2);
        int priShift = pri ? Math.Max(0, damping - Log2(priStrength)) : 0;
        int secShift = sec ? damping - Log2(secStrength) : 0;
        var priThr = Vector256.Create((short)priStrength); var secThr = Vector256.Create((short)secStrength);
        var priSh = Vector128.CreateScalar((ushort)priShift); var secSh = Vector128.CreateScalar((ushort)secShift);
        int po0 = Directions[dir + 2, 0], po1 = Directions[dir + 2, 1];
        int sa0 = Directions[dir + 4, 0], sa1 = Directions[dir + 4, 1];
        int sb0 = Directions[dir + 0, 0], sb1 = Directions[dir + 0, 1];
        int rowsPer = w == 8 ? 2 : 4;
        for (int r = 0; r < h; r += rowsPer)
        {
            int o = center + r * ts;
            var px = L(tmp, o, w);
            var sum = Vector256<short>.Zero;
            var mn = px.AsUInt16(); var mx = px;
            if (pri)
            {
                var p0 = L(tmp, o + po0, w); var p1 = L(tmp, o - po0, w);
                var q0 = L(tmp, o + po1, w); var q1 = L(tmp, o - po1, w);
                sum += (C(p0 - px, priThr, priSh) + C(p1 - px, priThr, priSh)) * Vector256.Create(priTap0);
                sum += (C(q0 - px, priThr, priSh) + C(q1 - px, priThr, priSh)) * Vector256.Create(priTap1);
                if (sec)
                {
                    mn = Vector256.Min(Vector256.Min(mn, p0.AsUInt16()), Vector256.Min(p1.AsUInt16(), Vector256.Min(q0.AsUInt16(), q1.AsUInt16())));
                    mx = Vector256.Max(Vector256.Max(mx, p0), Vector256.Max(p1, Vector256.Max(q0, q1)));
                }
            }
            if (sec)
            {
                for (int k = 0; k < 2; k++)
                {
                    int oa = k == 0 ? sa0 : sa1, ob = k == 0 ? sb0 : sb1;
                    var s0 = L(tmp, o + oa, w); var s1 = L(tmp, o - oa, w);
                    var s2 = L(tmp, o + ob, w); var s3 = L(tmp, o - ob, w);
                    var t = C(s0 - px, secThr, secSh) + C(s1 - px, secThr, secSh) + C(s2 - px, secThr, secSh) + C(s3 - px, secThr, secSh);
                    sum += k == 0 ? t + t : t;
                    if (pri)
                    {
                        mn = Vector256.Min(Vector256.Min(mn, s0.AsUInt16()), Vector256.Min(s1.AsUInt16(), Vector256.Min(s2.AsUInt16(), s3.AsUInt16())));
                        mx = Vector256.Max(Vector256.Max(mx, s0), Vector256.Max(s1, Vector256.Max(s2, s3)));
                    }
                }
            }
            var res = px + Vector256.ShiftRightArithmetic(sum + Vector256.ShiftRightArithmetic(sum, 15) + Vector256.Create((short)8), 4);
            if (pri && sec) res = Vector256.Min(Vector256.Max(res, mn.AsInt16()), mx);
            res.AsUInt16().CopyTo(dst.Slice(dstOffset + r * w, 16));
        }

        // 16 lanes: rows at o, o + 12, ... (8 samples each for w 8, 4 each for w 4)
        static Vector256<short> L(ReadOnlySpan<short> t, int o, int w)
        {
            if (w == 8) return Vector256.Create(Vector128.Create(t.Slice(o, 8)), Vector128.Create(t.Slice(o + ts, 8)));
            var b = MemoryMarshal.AsBytes(t);
            return Vector256.Create(MemoryMarshal.Read<long>(b.Slice(o * 2)), MemoryMarshal.Read<long>(b.Slice((o + ts) * 2)),
                MemoryMarshal.Read<long>(b.Slice((o + 2 * ts) * 2)), MemoryMarshal.Read<long>(b.Slice((o + 3 * ts) * 2))).AsInt16();
        }
        // constrain(diff, threshold, shift) with |diff| unsigned
        static Vector256<short> C(Vector256<short> diff, Vector256<short> thr, Vector128<ushort> sh)
        {
            var ad = Vector256.Abs(diff).AsUInt16();
            var lim = Vector256.Max(thr - Avx2.ShiftRightLogical(ad, sh).AsInt16(), Vector256<short>.Zero).AsUInt16();
            var val = Vector256.Min(ad, lim).AsInt16();
            return Vector256.ConditionalSelect(Vector256.LessThan(diff, Vector256<short>.Zero), -val, val);
        }
    }

    private static void Padding(Span<short> tmp, int tmpOffset, int tmpStride,
        ReadOnlySpan<ushort> src, int srcOffset, int srcStride,
        ReadOnlySpan<ushort> left, int leftOffset, int leftStride,
        ReadOnlySpan<ushort> top, int topOffset,
        ReadOnlySpan<ushort> bottom, int bottomOffset,
        int w, int h, EdgeFlags edges, int tbStride)
    {
        int xStart = -2, xEnd = w + 2, yStart = -2, yEnd = h + 2;

        if ((edges & EdgeFlags.Top) == 0)
        {
            FillShort(tmp, tmpOffset - 2 - 2 * tmpStride, tmpStride, w + 4, 2);
            yStart = 0;
        }
        if ((edges & EdgeFlags.Bottom) == 0)
        {
            FillShort(tmp, tmpOffset + h * tmpStride - 2, tmpStride, w + 4, 2);
            yEnd -= 2;
        }
        if ((edges & EdgeFlags.Left) == 0)
        {
            FillShort(tmp, tmpOffset + yStart * tmpStride - 2, tmpStride, 2, yEnd - yStart);
            xStart = 0;
        }
        if ((edges & EdgeFlags.Right) == 0)
        {
            FillShort(tmp, tmpOffset + yStart * tmpStride + w, tmpStride, 2, yEnd - yStart);
            xEnd -= 2;
        }

        int topOff = topOffset;
        for (int y = yStart; y < 0; y++)
        {
            for (int x = xStart; x < xEnd; x++)
                tmp[tmpOffset + x + y * tmpStride] = (short)top[topOff + x];
            topOff += tbStride;
        }

        for (int y = 0; y < h; y++)
            for (int x = xStart; x < 0; x++)
                tmp[tmpOffset + x + y * tmpStride] = (short)left[leftOffset + y * leftStride + (2 + x)];

        int sOff = srcOffset;
        int tOff = tmpOffset;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < xEnd; x++)
                tmp[tOff + x] = (short)src[sOff + x];
            sOff += srcStride;
            tOff += tmpStride;
        }

        int bOff = bottomOffset;
        for (int y = h; y < yEnd; y++)
        {
            for (int x = xStart; x < xEnd; x++)
                tmp[tOff + x] = (short)bottom[bOff + x];
            bOff += tbStride;
            tOff += tmpStride;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillShort(Span<short> buf, int offset, int stride, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                buf[offset + x] = short.MinValue;
            offset += stride;
        }
    }

    // ========================================================================
    // Filter Kernel (cdef_tmpl.c: cdef_filter_block_c)
    // ========================================================================

    /// <summary>
    /// Apply CDEF filter to a single block (w×h, where w,h ∈ {4,8}).
    /// dst is filtered in-place. left[y][2] provides the 2 left context pixels per row.
    /// top/bottom provide the 2 rows above/below for padding.
    /// </summary>
    public static void FilterBlock(Span<ushort> dst, int dstOffset, int dstStride,
        ReadOnlySpan<ushort> left, int leftOffset, int leftStride,
        ReadOnlySpan<ushort> top, int topOffset,
        ReadOnlySpan<ushort> bottom, int bottomOffset,
        int priStrength, int secStrength, int dir, int damping,
        int w, int h, EdgeFlags edges, int bitDepth = 8, int topBottomStride = 0)
    {
        int bdMin8 = bitDepth - 8;
        // Strengths and damping arrive already scaled for the bit depth by the caller
        // (dav1d cdef_apply: strengths <<= bitdepth_min_8, damping += bitdepth_min_8, and
        // adjust_strength is applied to the *shifted* luma strength). The filter itself does
        // no further scaling; the pri_tap parity below recovers the original low bit.
        const int tmpStride = 12;
        Span<short> tmpBuf = stackalloc short[144]; // 12*12
        int tmpCenter = 2 * tmpStride + 2;

        Padding(tmpBuf, tmpCenter, tmpStride,
            dst, dstOffset, dstStride,
            left, leftOffset, leftStride,
            top, topOffset, bottom, bottomOffset,
            w, h, edges, topBottomStride > 0 ? topBottomStride : dstStride);

        if (Avx2.IsSupported)
        {
            FilterRowsV(dst, dstOffset, dstStride, tmpBuf, tmpCenter, priStrength, secStrength, dir, damping, w, h, bdMin8);
            return;
        }

        int dOff = dstOffset;
        int tOff = tmpCenter;

        if (priStrength != 0)
        {
            int priTap = 4 - ((priStrength >> bdMin8) & 1); // dav1d: pri_tap = 4 - ((pri_strength >> bitdepth_min_8) & 1)
            int priShift = Math.Max(0, damping - Log2(priStrength));

            if (secStrength != 0)
            {
                int secShift = damping - Log2(secStrength);
                for (int row = 0; row < h; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int px = dst[dOff + x];
                        int sum = 0;
                        int max = px, min = px;
                        int priTapK = priTap;
                        for (int k = 0; k < 2; k++)
                        {
                            int off1 = Directions[dir + 2, k];
                            int p0 = tmpBuf[tOff + x + off1];
                            int p1 = tmpBuf[tOff + x - off1];
                            sum += priTapK * Constrain(p0 - px, priStrength, priShift);
                            sum += priTapK * Constrain(p1 - px, priStrength, priShift);
                            priTapK = (priTapK & 3) | 2;
                            min = MinU16(p0, min); max = Math.Max(p0, max);
                            min = MinU16(p1, min); max = Math.Max(p1, max);

                            int off2 = Directions[dir + 4, k];
                            int off3 = Directions[dir + 0, k];
                            int s0 = tmpBuf[tOff + x + off2];
                            int s1 = tmpBuf[tOff + x - off2];
                            int s2 = tmpBuf[tOff + x + off3];
                            int s3 = tmpBuf[tOff + x - off3];
                            int secTap = 2 - k;
                            sum += secTap * Constrain(s0 - px, secStrength, secShift);
                            sum += secTap * Constrain(s1 - px, secStrength, secShift);
                            sum += secTap * Constrain(s2 - px, secStrength, secShift);
                            sum += secTap * Constrain(s3 - px, secStrength, secShift);
                            min = MinU16(s0, min); max = Math.Max(s0, max);
                            min = MinU16(s1, min); max = Math.Max(s1, max);
                            min = MinU16(s2, min); max = Math.Max(s2, max);
                            min = MinU16(s3, min); max = Math.Max(s3, max);
                        }
                        dst[dOff + x] = (ushort)Math.Clamp(
                            px + ((sum - (sum < 0 ? 1 : 0) + 8) >> 4), min, max);
                    }
                    dOff += dstStride;
                    tOff += tmpStride;
                }
            }
            else // pri_strength only
            {
                for (int row = 0; row < h; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int px = dst[dOff + x];
                        int sum = 0;
                        int priTapK = priTap;
                        for (int k = 0; k < 2; k++)
                        {
                            int off = Directions[dir + 2, k];
                            int p0 = tmpBuf[tOff + x + off];
                            int p1 = tmpBuf[tOff + x - off];
                            sum += priTapK * Constrain(p0 - px, priStrength, priShift);
                            sum += priTapK * Constrain(p1 - px, priStrength, priShift);
                            priTapK = (priTapK & 3) | 2;
                        }
                        dst[dOff + x] = (ushort)(px + ((sum - (sum < 0 ? 1 : 0) + 8) >> 4));
                    }
                    dOff += dstStride;
                    tOff += tmpStride;
                }
            }
        }
        else // sec_strength only
        {
            int secShift = damping - Log2(secStrength);
            for (int row = 0; row < h; row++)
            {
                for (int x = 0; x < w; x++)
                {
                    int px = dst[dOff + x];
                    int sum = 0;
                    for (int k = 0; k < 2; k++)
                    {
                        int off1 = Directions[dir + 4, k];
                        int off2 = Directions[dir + 0, k];
                        int s0 = tmpBuf[tOff + x + off1];
                        int s1 = tmpBuf[tOff + x - off1];
                        int s2 = tmpBuf[tOff + x + off2];
                        int s3 = tmpBuf[tOff + x - off2];
                        int secTap = 2 - k;
                        sum += secTap * Constrain(s0 - px, secStrength, secShift);
                        sum += secTap * Constrain(s1 - px, secStrength, secShift);
                        sum += secTap * Constrain(s2 - px, secStrength, secShift);
                        sum += secTap * Constrain(s3 - px, secStrength, secShift);
                    }
                    dst[dOff + x] = (ushort)(px + ((sum - (sum < 0 ? 1 : 0) + 8) >> 4));
                }
                dOff += dstStride;
                tOff += tmpStride;
            }
        }
    }

    // cdef_filter_block_c on a padded block, one row (w = 4 or 8 pixels) per Vector256<int>: the same integer
    // operations per pixel (MinU16 as a min over p & 0xFFFF — the INT16_MIN padding maps above every pixel and the
    // running min starts at a pixel; sum - (sum < 0) as sum + (sum >> 31); the unclamped (ushort) cast as a 16-bit
    // narrowing), so the output is identical. 8-sample loads stay inside the 12 x 12 padded buffer.
    private static void FilterRowsV(Span<ushort> dst, int dstOffset, int dstStride, ReadOnlySpan<short> tmp, int tmpCenter,
        int priStrength, int secStrength, int dir, int damping, int w, int h, int bdMin8)
    {
        const int tmpStride = 12;
        var zero = Vector256<int>.Zero;
        var m16 = Vector256.Create(0xFFFF);
        var eight = Vector256.Create(8);
        bool pri = priStrength != 0, sec = secStrength != 0;
        int priTap0 = 4 - ((priStrength >> bdMin8) & 1), priTap1 = (priTap0 & 3) | 2;
        int priShift = pri ? Math.Max(0, damping - Log2(priStrength)) : 0;
        int secShift = sec ? damping - Log2(secStrength) : 0;
        var priThr = Vector256.Create(priStrength);
        var secThr = Vector256.Create(secStrength);
        int po0 = Directions[dir + 2, 0], po1 = Directions[dir + 2, 1];
        int sa0 = Directions[dir + 4, 0], sa1 = Directions[dir + 4, 1];
        int sb0 = Directions[dir + 0, 0], sb1 = Directions[dir + 0, 1];
        Span<int> lanes = stackalloc int[8];

        int dOff = dstOffset, tOff = tmpCenter;
        for (int row = 0; row < h; row++, dOff += dstStride, tOff += tmpStride)
        {
            var px = Load(tmp, tOff);
            var sum = zero;
            var min = px; var max = px;
            if (pri)
            {
                var p0 = Load(tmp, tOff + po0); var p1 = Load(tmp, tOff - po0);
                sum += Constrain(p0 - px, priThr, priShift) * priTap0;
                sum += Constrain(p1 - px, priThr, priShift) * priTap0;
                var q0 = Load(tmp, tOff + po1); var q1 = Load(tmp, tOff - po1);
                sum += Constrain(q0 - px, priThr, priShift) * priTap1;
                sum += Constrain(q1 - px, priThr, priShift) * priTap1;
                if (sec)
                {
                    min = Vector256.Min(min, p0 & m16); max = Vector256.Max(max, p0);
                    min = Vector256.Min(min, p1 & m16); max = Vector256.Max(max, p1);
                    min = Vector256.Min(min, q0 & m16); max = Vector256.Max(max, q0);
                    min = Vector256.Min(min, q1 & m16); max = Vector256.Max(max, q1);
                }
            }
            if (sec)
            {
                for (int k = 0; k < 2; k++)
                {
                    int oa = k == 0 ? sa0 : sa1, ob = k == 0 ? sb0 : sb1, tap = 2 - k;
                    var s0 = Load(tmp, tOff + oa); var s1 = Load(tmp, tOff - oa);
                    var s2 = Load(tmp, tOff + ob); var s3 = Load(tmp, tOff - ob);
                    sum += Constrain(s0 - px, secThr, secShift) * tap;
                    sum += Constrain(s1 - px, secThr, secShift) * tap;
                    sum += Constrain(s2 - px, secThr, secShift) * tap;
                    sum += Constrain(s3 - px, secThr, secShift) * tap;
                    if (pri)
                    {
                        min = Vector256.Min(min, s0 & m16); max = Vector256.Max(max, s0);
                        min = Vector256.Min(min, s1 & m16); max = Vector256.Max(max, s1);
                        min = Vector256.Min(min, s2 & m16); max = Vector256.Max(max, s2);
                        min = Vector256.Min(min, s3 & m16); max = Vector256.Max(max, s3);
                    }
                }
            }
            var res = px + Vector256.ShiftRightArithmetic(sum + Vector256.ShiftRightArithmetic(sum, 31) + eight, 4);
            if (pri && sec) res = Vector256.Min(Vector256.Max(res, min), max);
            if (w == 8)
                Vector256.Narrow(res.AsUInt32(), Vector256<uint>.Zero).GetLower().CopyTo(dst.Slice(dOff, 8));
            else
            {
                res.CopyTo(lanes);
                for (int x = 0; x < w; x++) dst[dOff + x] = (ushort)lanes[x];
            }
        }

        static Vector256<int> Load(ReadOnlySpan<short> t, int off) => Avx2.ConvertToVector256Int32(Vector128.Create(t.Slice(off, 8)));
        static Vector256<int> Constrain(Vector256<int> diff, Vector256<int> thr, int shift)
        {
            var adiff = Vector256.Abs(diff);
            var val = Vector256.Min(adiff, Vector256.Max(Vector256<int>.Zero, thr - Vector256.ShiftRightArithmetic(adiff, shift)));
            return Vector256.ConditionalSelect(Vector256.LessThan(diff, Vector256<int>.Zero), -val, val);
        }
    }

    // ========================================================================
    // Strength Adjustment (cdef_apply_tmpl.c: adjust_strength)
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AdjustStrength(int strength, uint variance)
    {
        if (variance == 0) return 0;
        int i = variance >> 6 != 0 ? Math.Min(Log2(variance >> 6), 12) : 0;
        return (strength * (4 + i) + 8) >> 4;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeAltCost(Span<int> alt)
    {
        uint c = 0;
        for (int m = 0; m < 5; m++)
            c += (uint)(alt[3 + m] * alt[3 + m]);
        c *= 105;
        for (int m = 0; m < 3; m++)
        {
            uint d = DivTable[2 * m + 1];
            c += (uint)(alt[m] * alt[m] + alt[10 - m] * alt[10 - m]) * d;
        }
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(int v)
    {
        int r = 0;
        while (v > 1) { v >>= 1; r++; }
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(uint v) => Log2((int)v);

    /// <summary>Min treating short.MinValue as "large" (unavailable pixel sentinel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinU16(int a, int b)
    {
        // In dav1d, INT16_MIN (0x8000) is used as sentinel for unavailable pixels.
        // When interpreted as unsigned 16-bit, it's 32768 (very large), so umin skips it.
        if (a == short.MinValue) return b;
        if (b == short.MinValue) return a;
        return a < b ? a : b;
    }
}
