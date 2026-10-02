using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// The CDEF kernels libaom 3.14.1 dispatches on an AVX2 machine, ported line by line:
//   cdef_find_dir_dual_avx2 (cdef_block_avx2.c; the odd block's cdef_find_dir_avx2 computes the same per-lane arithmetic,
//     so it runs as the dual kernel's first lane), cdef_filter_8_{0,1,2,3}_avx2 (cdef_block_simd.h filter_block_8x8 /
//     filter_block_4x4 with is_lowbd = 1: int16 lanes, the saturating constrain16, the u8 max trick that ignores
//     CDEF_VERY_LARGE, packus to 8 bits), cdef_copy_rect8_8bit_to_16bit_avx2.
// FilterBlockV is the portable Vector128 form of the same arithmetic for machines without AVX2.
internal static partial class AomCdef
{
    // ---- cdef_find_dir_dual_avx2 -------------------------------------------------------------------------------------

    private static readonly Vector256<byte> FoldShuffle = Vector256.Create(
        (byte)12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1, 14, 15, 12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1, 14, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> FoldMulAndSum(Vector256<short> partiala, Vector256<short> partialb, Vector256<int> const1, Vector256<int> const2)
    {
        partialb = Avx2.Shuffle(partialb.AsByte(), FoldShuffle).AsInt16();
        var tmp = partiala;
        partiala = Avx2.UnpackLow(partiala, partialb);
        partialb = Avx2.UnpackHigh(tmp, partialb);
        var a = Avx2.MultiplyAddAdjacent(partiala, partiala);
        var b = Avx2.MultiplyAddAdjacent(partialb, partialb);
        a = Avx2.MultiplyLow(a, const1);
        b = Avx2.MultiplyLow(b, const2);
        return Avx2.Add(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Hsum4(Vector256<int> x0, Vector256<int> x1, Vector256<int> x2, Vector256<int> x3)
    {
        var t0 = Avx2.UnpackLow(x0, x1);
        var t1 = Avx2.UnpackLow(x2, x3);
        var t2 = Avx2.UnpackHigh(x0, x1);
        var t3 = Avx2.UnpackHigh(x2, x3);
        x0 = Avx2.UnpackLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
        x1 = Avx2.UnpackHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
        x2 = Avx2.UnpackLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
        x3 = Avx2.UnpackHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
        return Avx2.Add(Avx2.Add(x0, x1), Avx2.Add(x2, x3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Sl(Vector256<short> v, byte n) => Avx2.ShiftLeftLogical128BitLane(v, n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Sr(Vector256<short> v, byte n) => Avx2.ShiftRightLogical128BitLane(v, n);

    /// <summary>compute_directions_avx2: the costs of directions 4..7 (or 0..3 on the transposed lines), per 128-bit lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ComputeDirections(Vector256<short> l0, Vector256<short> l1, Vector256<short> l2, Vector256<short> l3,
        Vector256<short> l4, Vector256<short> l5, Vector256<short> l6, Vector256<short> l7)
    {
        var partial4a = Sl(l0, 14);
        var partial4b = Sr(l0, 2);
        partial4a = Avx2.Add(partial4a, Sl(l1, 12));
        partial4b = Avx2.Add(partial4b, Sr(l1, 4));
        var tmp = Avx2.Add(l0, l1);
        var partial5a = Sl(tmp, 10);
        var partial5b = Sr(tmp, 6);
        var partial7a = Sl(tmp, 4);
        var partial7b = Sr(tmp, 12);
        var partial6 = tmp;

        partial4a = Avx2.Add(partial4a, Sl(l2, 10));
        partial4b = Avx2.Add(partial4b, Sr(l2, 6));
        partial4a = Avx2.Add(partial4a, Sl(l3, 8));
        partial4b = Avx2.Add(partial4b, Sr(l3, 8));
        tmp = Avx2.Add(l2, l3);
        partial5a = Avx2.Add(partial5a, Sl(tmp, 8));
        partial5b = Avx2.Add(partial5b, Sr(tmp, 8));
        partial7a = Avx2.Add(partial7a, Sl(tmp, 6));
        partial7b = Avx2.Add(partial7b, Sr(tmp, 10));
        partial6 = Avx2.Add(partial6, tmp);

        partial4a = Avx2.Add(partial4a, Sl(l4, 6));
        partial4b = Avx2.Add(partial4b, Sr(l4, 10));
        partial4a = Avx2.Add(partial4a, Sl(l5, 4));
        partial4b = Avx2.Add(partial4b, Sr(l5, 12));
        tmp = Avx2.Add(l4, l5);
        partial5a = Avx2.Add(partial5a, Sl(tmp, 6));
        partial5b = Avx2.Add(partial5b, Sr(tmp, 10));
        partial7a = Avx2.Add(partial7a, Sl(tmp, 8));
        partial7b = Avx2.Add(partial7b, Sr(tmp, 8));
        partial6 = Avx2.Add(partial6, tmp);

        partial4a = Avx2.Add(partial4a, Sl(l6, 2));
        partial4b = Avx2.Add(partial4b, Sr(l6, 14));
        partial4a = Avx2.Add(partial4a, l7);
        tmp = Avx2.Add(l6, l7);
        partial5a = Avx2.Add(partial5a, Sl(tmp, 4));
        partial5b = Avx2.Add(partial5b, Sr(tmp, 12));
        partial7a = Avx2.Add(partial7a, Sl(tmp, 10));
        partial7b = Avx2.Add(partial7b, Sr(tmp, 6));
        partial6 = Avx2.Add(partial6, tmp);

        var c1 = Vector256.Create(840, 420, 280, 210, 840, 420, 280, 210);
        var c2 = Vector256.Create(168, 140, 120, 105, 168, 140, 120, 105);
        var c3 = Vector256.Create(0, 0, 420, 210, 0, 0, 420, 210);
        var c4 = Vector256.Create(140, 105, 105, 105, 140, 105, 105, 105);
        var p4 = FoldMulAndSum(partial4a, partial4b, c1, c2);
        var p7 = FoldMulAndSum(partial7a, partial7b, c3, c4);
        var p5 = FoldMulAndSum(partial5a, partial5b, c3, c4);
        var p6 = Avx2.MultiplyLow(Avx2.MultiplyAddAdjacent(partial6, partial6), Vector256.Create(105));
        return Hsum4(p4, p5, p6, p7);
    }

    /// <summary>array_reverse_transpose_8x8_avx2 (per 128-bit lane).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReverseTranspose8x8(ref Vector256<short> v0, ref Vector256<short> v1, ref Vector256<short> v2, ref Vector256<short> v3,
        ref Vector256<short> v4, ref Vector256<short> v5, ref Vector256<short> v6, ref Vector256<short> v7)
    {
        var tr0_0 = Avx2.UnpackLow(v0, v1);
        var tr0_1 = Avx2.UnpackLow(v2, v3);
        var tr0_2 = Avx2.UnpackHigh(v0, v1);
        var tr0_3 = Avx2.UnpackHigh(v2, v3);
        var tr0_4 = Avx2.UnpackLow(v4, v5);
        var tr0_5 = Avx2.UnpackLow(v6, v7);
        var tr0_6 = Avx2.UnpackHigh(v4, v5);
        var tr0_7 = Avx2.UnpackHigh(v6, v7);

        var tr1_0 = Avx2.UnpackLow(tr0_0.AsInt32(), tr0_1.AsInt32()).AsInt64();
        var tr1_1 = Avx2.UnpackLow(tr0_4.AsInt32(), tr0_5.AsInt32()).AsInt64();
        var tr1_2 = Avx2.UnpackHigh(tr0_0.AsInt32(), tr0_1.AsInt32()).AsInt64();
        var tr1_3 = Avx2.UnpackHigh(tr0_4.AsInt32(), tr0_5.AsInt32()).AsInt64();
        var tr1_4 = Avx2.UnpackLow(tr0_2.AsInt32(), tr0_3.AsInt32()).AsInt64();
        var tr1_5 = Avx2.UnpackLow(tr0_6.AsInt32(), tr0_7.AsInt32()).AsInt64();
        var tr1_6 = Avx2.UnpackHigh(tr0_2.AsInt32(), tr0_3.AsInt32()).AsInt64();
        var tr1_7 = Avx2.UnpackHigh(tr0_6.AsInt32(), tr0_7.AsInt32()).AsInt64();

        v7 = Avx2.UnpackLow(tr1_0, tr1_1).AsInt16();
        v6 = Avx2.UnpackHigh(tr1_0, tr1_1).AsInt16();
        v5 = Avx2.UnpackLow(tr1_2, tr1_3).AsInt16();
        v4 = Avx2.UnpackHigh(tr1_2, tr1_3).AsInt16();
        v3 = Avx2.UnpackLow(tr1_4, tr1_5).AsInt16();
        v2 = Avx2.UnpackHigh(tr1_4, tr1_5).AsInt16();
        v1 = Avx2.UnpackLow(tr1_6, tr1_7).AsInt16();
        v0 = Avx2.UnpackHigh(tr1_6, tr1_7).AsInt16();
    }

    /// <summary>cdef_find_dir_dual_avx2: the directions and directional variances of two 8x8 blocks (16-bit samples,
    /// stride in samples).</summary>
    internal static unsafe void FindDirDualAvx2(ushort* img1, ushort* img2, int stride, out int var1, out int var2, int coeffShift,
        out int dir1, out int dir2)
    {
        var shift = Vector128.CreateScalar(coeffShift).AsInt16();
        var c128 = Vector256.Create((short)128);
        short* p1 = (short*)img1, p2 = (short*)img2;
        var l0 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1), Vector128.Load(p2)), shift), c128);
        var l1 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + stride), Vector128.Load(p2 + stride)), shift), c128);
        var l2 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + 2 * stride), Vector128.Load(p2 + 2 * stride)), shift), c128);
        var l3 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + 3 * stride), Vector128.Load(p2 + 3 * stride)), shift), c128);
        var l4 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + 4 * stride), Vector128.Load(p2 + 4 * stride)), shift), c128);
        var l5 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + 5 * stride), Vector128.Load(p2 + 5 * stride)), shift), c128);
        var l6 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + 6 * stride), Vector128.Load(p2 + 6 * stride)), shift), c128);
        var l7 = Avx2.Subtract(Avx2.ShiftRightArithmetic(Vector256.Create(Vector128.Load(p1 + 7 * stride), Vector128.Load(p2 + 7 * stride)), shift), c128);
        var dir47 = ComputeDirections(l0, l1, l2, l3, l4, l5, l6, l7);
        ReverseTranspose8x8(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7);
        var dir03 = ComputeDirections(l0, l1, l2, l3, l4, l5, l6, l7);

        var max = Avx2.Max(dir03, dir47);
        max = Avx2.Max(max, Avx2.Or(Avx2.ShiftRightLogical128BitLane(max, 8), Avx2.ShiftLeftLogical128BitLane(max, 8)));
        max = Avx2.Max(max, Avx2.Or(Avx2.ShiftRightLogical128BitLane(max, 4), Avx2.ShiftLeftLogical128BitLane(max, 12)));

        var first = max.GetLower();
        var second = max.GetUpper();
        var t1 = Sse2.PackSignedSaturate(Sse2.CompareEqual(first, dir03.GetLower()), Sse2.CompareEqual(first, dir47.GetLower()));
        var t2 = Sse2.PackSignedSaturate(Sse2.CompareEqual(second, dir03.GetUpper()), Sse2.CompareEqual(second, dir47.GetUpper()));
        int bestCost0 = first.ToScalar(), bestCost1 = second.ToScalar();
        int bd0 = BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.PackSignedSaturate(t1, t1)));
        int bd1 = BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.PackSignedSaturate(t2, t2)));
        // cost_first_8x8 / cost_second_8x8 (the compute_directions stores)
        int* cost1 = stackalloc int[8];
        int* cost2 = stackalloc int[8];
        dir03.GetLower().Store(cost1);
        dir47.GetLower().Store(cost1 + 4);
        dir03.GetUpper().Store(cost2);
        dir47.GetUpper().Store(cost2 + 4);
        int c0 = cost1[(bd0 + 4) & 7], c1v = cost2[(bd1 + 4) & 7];
        var1 = (bestCost0 - c0) >> 10;
        var2 = (bestCost1 - c1v) >> 10;
        dir1 = bd0;
        dir2 = bd1;
    }

    // ---- cdef_filter_8_*_avx2 ----------------------------------------------------------------------------------------

    /// <summary>constrain16: sign(a - b) * min(|a - b|, max(0, threshold - (|a - b| >> adjdamp))).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Constrain16(Vector256<short> a, Vector256<short> b, Vector256<ushort> threshold, Vector128<short> adjdamp)
    {
        var diff = Avx2.Subtract(a, b);
        var sign = Avx2.ShiftRightArithmetic(diff, 15);
        diff = Avx2.Abs(diff).AsInt16();
        var s = Avx2.SubtractSaturate(threshold, Avx2.ShiftRightLogical(diff, adjdamp).AsUInt16());
        return Avx2.Xor(Avx2.Add(sign, Avx2.Min(diff, s.AsInt16())), sign);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<short> Load2(short* p) => Vector256.Create(Vector128.Load(p), Vector128.Load(p + BStride));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<short> Load4(short* p) => Vector256.Create(*(ulong*)p, *(ulong*)(p + BStride),
        *(ulong*)(p + 2 * BStride), *(ulong*)(p + 3 * BStride)).AsInt16();

    /// <summary>filter_block_8x8 (is_lowbd = 1): two rows of 8 per 256-bit vector. bh4: filter_block_4x4 (four rows of 4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void FilterBlockAvx2(byte* dst8, int dstride, short* inp, int priStrength, int secStrength, int dir,
        int priDamping, int secDamping, int coeffShift, int height, bool w4, bool enablePrimary, bool enableSecondary,
        bool hbd = false, ushort* dst16 = null)
    {
        // hbd (is_lowbd = 0): the max skips the CDEF_VERY_LARGE padding by a compare (the byte max only works below 256)
        // and the result is stored as 16 bits
        bool clippingRequired = enablePrimary && enableSecondary;
        var largeMask = Vector256.Create(unchecked((short)~VeryLarge));
        int po1 = Direction(dir, 0), po2 = Direction(dir, 1);
        int s1o1 = Direction(dir + 2, 0), s1o2 = Direction(dir + 2, 1);
        int s2o1 = Direction(dir - 2, 0), s2o2 = Direction(dir - 2, 1);
        int tapSet = ((priStrength >> coeffShift) & 1) * 2;
        var priTap0 = Vector256.Create((short)PriTaps[tapSet]);
        var priTap1 = Vector256.Create((short)PriTaps[tapSet + 1]);
        var secTap0 = Vector256.Create((short)2);
        var secTap1 = Vector256.Create((short)1);
        if (enablePrimary && priStrength != 0) priDamping = Math.Max(0, priDamping - BitOperations.Log2((uint)priStrength));
        if (enableSecondary && secStrength != 0) secDamping = Math.Max(0, secDamping - BitOperations.Log2((uint)secStrength));
        var priT = Vector256.Create((ushort)priStrength);
        var secT = Vector256.Create((ushort)secStrength);
        var priD = Vector128.CreateScalar(priDamping).AsInt16();
        var secD = Vector128.CreateScalar(secDamping).AsInt16();
        int step = w4 ? 4 : 2;
        for (int i = 0; i < height; i += step)
        {
            short* r = inp + i * BStride;
            var sum = Vector256<short>.Zero;
            var row = w4 ? Load4(r) : Load2(r);
            var max = row;
            var min = row;
            if (enablePrimary)
            {
                var t0 = w4 ? Load4(r + po1) : Load2(r + po1);
                var t1 = w4 ? Load4(r - po1) : Load2(r - po1);
                var p0 = Constrain16(t0, row, priT, priD);
                var p1 = Constrain16(t1, row, priT, priD);
                sum = Avx2.Add(sum, Avx2.MultiplyLow(priTap0, Avx2.Add(p0, p1)));
                var t2 = w4 ? Load4(r + po2) : Load2(r + po2);
                var t3 = w4 ? Load4(r - po2) : Load2(r - po2);
                p0 = Constrain16(t2, row, priT, priD);
                p1 = Constrain16(t3, row, priT, priD);
                sum = Avx2.Add(sum, Avx2.MultiplyLow(priTap1, Avx2.Add(p0, p1)));
                if (clippingRequired)
                {
                    if (hbd)
                    {
                        max = MaxNotLarge(max, t0); max = MaxNotLarge(max, t1); max = MaxNotLarge(max, t2); max = MaxNotLarge(max, t3);
                    }
                    else
                    {
                        var mu8 = Avx2.Max(Avx2.Max(t0.AsByte(), t1.AsByte()), Avx2.Max(t2.AsByte(), t3.AsByte()));
                        max = Avx2.Max(max, Avx2.And(mu8.AsInt16(), largeMask));
                    }
                    min = Avx2.Min(min, t0);
                    min = Avx2.Min(min, t1);
                    min = Avx2.Min(min, t2);
                    min = Avx2.Min(min, t3);
                }
            }
            if (enableSecondary)
            {
                var t0 = w4 ? Load4(r + s1o1) : Load2(r + s1o1);
                var t1 = w4 ? Load4(r - s1o1) : Load2(r - s1o1);
                var t2 = w4 ? Load4(r + s2o1) : Load2(r + s2o1);
                var t3 = w4 ? Load4(r - s2o1) : Load2(r - s2o1);
                var p0 = Constrain16(t0, row, secT, secD);
                var p1 = Constrain16(t1, row, secT, secD);
                var p2 = Constrain16(t2, row, secT, secD);
                var p3 = Constrain16(t3, row, secT, secD);
                sum = Avx2.Add(sum, Avx2.MultiplyLow(secTap0, Avx2.Add(Avx2.Add(p0, p1), Avx2.Add(p2, p3))));
                var t4 = w4 ? Load4(r + s1o2) : Load2(r + s1o2);
                var t5 = w4 ? Load4(r - s1o2) : Load2(r - s1o2);
                var t6 = w4 ? Load4(r + s2o2) : Load2(r + s2o2);
                var t7 = w4 ? Load4(r - s2o2) : Load2(r - s2o2);
                p0 = Constrain16(t4, row, secT, secD);
                p1 = Constrain16(t5, row, secT, secD);
                p2 = Constrain16(t6, row, secT, secD);
                p3 = Constrain16(t7, row, secT, secD);
                sum = Avx2.Add(sum, Avx2.MultiplyLow(secTap1, Avx2.Add(Avx2.Add(p0, p1), Avx2.Add(p2, p3))));
                if (clippingRequired)
                {
                    if (hbd)
                    {
                        max = MaxNotLarge(max, t0); max = MaxNotLarge(max, t1); max = MaxNotLarge(max, t2); max = MaxNotLarge(max, t3);
                        max = MaxNotLarge(max, t4); max = MaxNotLarge(max, t5); max = MaxNotLarge(max, t6); max = MaxNotLarge(max, t7);
                    }
                    else
                    {
                        var mu8 = Avx2.Max(Avx2.Max(Avx2.Max(t0.AsByte(), t1.AsByte()), Avx2.Max(t2.AsByte(), t3.AsByte())),
                            Avx2.Max(Avx2.Max(t4.AsByte(), t5.AsByte()), Avx2.Max(t6.AsByte(), t7.AsByte())));
                        max = Avx2.Max(max, Avx2.And(mu8.AsInt16(), largeMask));
                    }
                    min = Avx2.Min(min, t0);
                    min = Avx2.Min(min, t1);
                    min = Avx2.Min(min, t2);
                    min = Avx2.Min(min, t3);
                    min = Avx2.Min(min, t4);
                    min = Avx2.Min(min, t5);
                    min = Avx2.Min(min, t6);
                    min = Avx2.Min(min, t7);
                }
            }
            // res = row + ((sum - (sum < 0) + 8) >> 4)
            sum = Avx2.Add(sum, Avx2.CompareGreaterThan(Vector256<short>.Zero, sum));
            var res = Avx2.ShiftRightArithmetic(Avx2.Add(sum, Vector256.Create((short)8)), 4);
            res = Avx2.Add(row, res);
            if (clippingRequired) res = Avx2.Min(Avx2.Max(res, min), max);
            if (hbd)
            {
                if (w4)
                {
                    var q = res.AsUInt64();
                    *(ulong*)(dst16 + i * dstride) = q.GetElement(0);
                    *(ulong*)(dst16 + (i + 1) * dstride) = q.GetElement(1);
                    *(ulong*)(dst16 + (i + 2) * dstride) = q.GetElement(2);
                    *(ulong*)(dst16 + (i + 3) * dstride) = q.GetElement(3);
                }
                else
                {
                    res.GetLower().AsUInt16().Store(dst16 + i * dstride);
                    res.GetUpper().AsUInt16().Store(dst16 + (i + 1) * dstride);
                }
                continue;
            }
            var packed = Avx2.PackUnsignedSaturate(res, res).AsUInt64();   // lane k: its rows' bytes, twice
            if (w4)
            {
                ulong lo = packed.GetElement(0), hi = packed.GetElement(2);
                *(uint*)(dst8 + i * dstride) = (uint)lo;
                *(uint*)(dst8 + (i + 1) * dstride) = (uint)(lo >> 32);
                *(uint*)(dst8 + (i + 2) * dstride) = (uint)hi;
                *(uint*)(dst8 + (i + 3) * dstride) = (uint)(hi >> 32);
            }
            else
            {
                *(ulong*)(dst8 + i * dstride) = packed.GetElement(0);
                *(ulong*)(dst8 + (i + 1) * dstride) = packed.GetElement(2);
            }
        }
    }

    /// <summary>max(m, t) over the lanes of t that are not the CDEF_VERY_LARGE padding (is_lowbd = 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> MaxNotLarge(Vector256<short> m, Vector256<short> t)
        => Avx2.Max(m, Avx2.AndNot(Avx2.CompareEqual(t, Vector256.Create((short)VeryLarge)), t));

    /// <summary>cdef_filter_16_{0,1,2}_avx2 (high bit depth, 16-bit output); the copy case is the caller's.</summary>
    internal static unsafe void FilterBlock16Avx2(ushort* dst16, int dstride, short* inp, int priStrength, int secStrength, int dir,
        int priDamping, int secDamping, int coeffShift, int bw, int bh, bool enablePrimary, bool enableSecondary)
    {
        bool w4 = bw != 8;
        if (enablePrimary && enableSecondary)
        {
            if (w4) FilterBlockAvx2(null, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, true, true, true, true, dst16);
            else FilterBlockAvx2(null, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, false, true, true, true, dst16);
        }
        else if (enablePrimary)
        {
            if (w4) FilterBlockAvx2(null, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, true, true, false, true, dst16);
            else FilterBlockAvx2(null, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, false, true, false, true, dst16);
        }
        else
        {
            if (w4) FilterBlockAvx2(null, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, true, false, true, true, dst16);
            else FilterBlockAvx2(null, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, false, false, true, true, dst16);
        }
    }

    /// <summary>cdef_filter_8_{strength_index}_avx2: 0 primary + secondary, 1 primary, 2 secondary, 3 copy.</summary>
    internal static unsafe void FilterBlock8Avx2(int strengthIndex, byte* dst8, int dstride, short* inp, int priStrength, int secStrength,
        int dir, int priDamping, int secDamping, int coeffShift, int bw, int bh)
    {
        bool w4 = bw != 8;
        switch (strengthIndex)
        {
            case 0:
                if (w4) FilterBlockAvx2(dst8, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, true, true, true);
                else FilterBlockAvx2(dst8, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, false, true, true);
                break;
            case 1:
                if (w4) FilterBlockAvx2(dst8, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, true, true, false);
                else FilterBlockAvx2(dst8, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, false, true, false);
                break;
            case 2:
                if (w4) FilterBlockAvx2(dst8, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, true, false, true);
                else FilterBlockAvx2(dst8, dstride, inp, priStrength, secStrength, dir, priDamping, secDamping, coeffShift, bh, false, false, true);
                break;
            default:
                // copy_block_4xh / copy_block_8xh (packus of the 16-bit input)
                if (w4)
                    for (int i = 0; i < bh; i++)
                    {
                        var v = Vector128.CreateScalar(*(ulong*)(inp + i * BStride)).AsInt16();
                        *(uint*)(dst8 + i * dstride) = Sse2.PackUnsignedSaturate(v, v).AsUInt32().ToScalar();
                    }
                else
                    for (int i = 0; i < bh; i++)
                    {
                        var v = Vector128.Load(inp + i * BStride);
                        *(ulong*)(dst8 + i * dstride) = Sse2.PackUnsignedSaturate(v, v).AsUInt64().ToScalar();
                    }
                break;
        }
    }

    // ---- cdef_copy_rect8_8bit_to_16bit_avx2 -------------------------------------------------------------------------

    /// <summary>cdef_copy_rect8_8bit_to_16bit: zero-extends a width x height 8-bit rectangle into the 16-bit buffer.</summary>
    internal static unsafe void CopyRect8To16(ushort* dst, int dstride, byte* src, int sstride, int width, int height)
    {
        if (!Avx2.IsSupported)
        {
            for (int i = 0; i < height; i++)
                for (int j = 0; j < width; j++) dst[i * dstride + j] = src[i * sstride + j];
            return;
        }
        for (int i = 0; i < height; i++)
        {
            byte* s = src + i * sstride;
            ushort* d = dst + i * dstride;
            int j = 0;
            for (; j + 16 <= width; j += 16) Avx2.ConvertToVector256Int16(Vector128.Load(s + j)).AsUInt16().Store(d + j);
            if (j + 8 <= width) { Sse41.ConvertToVector128Int16(Vector128.CreateScalar(*(ulong*)(s + j)).AsByte()).AsUInt16().Store(d + j); j += 8; }
            if (j + 4 <= width)
            {
                *(ulong*)(d + j) = Sse41.ConvertToVector128Int16(Vector128.CreateScalar(*(uint*)(s + j)).AsByte()).AsUInt64().ToScalar();
                j += 4;
            }
            for (; j < width; j++) d[j] = s[j];
        }
    }

    // ---- portable fallback ---------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ConstrainV(Vector128<short> diff, Vector128<short> threshold, int shift)
    {
        var ad = Vector128.Abs(diff);
        var t = Vector128.Max(Vector128<short>.Zero, threshold - Vector128.ShiftRightArithmetic(ad, shift));
        var v = Vector128.Min(ad, t);
        return Vector128.ConditionalSelect(Vector128.LessThan(diff, Vector128<short>.Zero), -v, v);
    }

    /// <summary>The block filter a row of 8 (or 4) samples at a time in int16 lanes, exactly the C arithmetic (int16 tap
    /// sums, constrain() with the per-call shift, the CDEF_VERY_LARGE-aware clipping range).</summary>
    internal static void FilterBlockV(byte[] dst, int dstOff, int dstride, ushort[] inb, int inOff, int priStrength, int secStrength, int dir,
        int priDamping, int secDamping, int coeffShift, int bw, int bh, bool enablePrimary, bool enableSecondary)
    {
        bool clippingRequired = enablePrimary && enableSecondary;
        int tapSet = ((priStrength >> coeffShift) & 1) * 2;
        int priShift = priStrength != 0 ? Math.Max(0, priDamping - BitOperations.Log2((uint)priStrength)) : 0;
        int secShift = secStrength != 0 ? Math.Max(0, secDamping - BitOperations.Log2((uint)secStrength)) : 0;
        var priT = Vector128.Create((short)priStrength);
        var secT = Vector128.Create((short)secStrength);
        var veryLarge = Vector128.Create((short)VeryLarge);
        int dp0 = Direction(dir, 0), dp1 = Direction(dir, 1);
        int ds0a = Direction(dir + 2, 0), ds1a = Direction(dir + 2, 1), ds0b = Direction(dir - 2, 0), ds1b = Direction(dir - 2, 1);
        short pt0 = (short)PriTaps[tapSet], pt1 = (short)PriTaps[tapSet + 1];
        ref short src = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetArrayDataReference(inb));
        for (int i = 0; i < bh; i++)
        {
            int p = inOff + i * BStride;
            var x = Vector128.LoadUnsafe(ref src, (nuint)p);
            var sum = Vector128<short>.Zero;
            var max = x;
            var min = x;
            if (enablePrimary)
            {
                for (int k = 0; k < 2; k++)
                {
                    int d = k == 0 ? dp0 : dp1;
                    var tap = Vector128.Create(k == 0 ? pt0 : pt1);
                    var p0 = Vector128.LoadUnsafe(ref src, (nuint)(p + d));
                    var p1 = Vector128.LoadUnsafe(ref src, (nuint)(p - d));
                    sum += tap * ConstrainV(p0 - x, priT, priShift);
                    sum += tap * ConstrainV(p1 - x, priT, priShift);
                    if (clippingRequired)
                    {
                        max = Vector128.Max(max, Vector128.ConditionalSelect(Vector128.Equals(p0, veryLarge), max, p0));
                        max = Vector128.Max(max, Vector128.ConditionalSelect(Vector128.Equals(p1, veryLarge), max, p1));
                        min = Vector128.Min(min, Vector128.Min(p0, p1));
                    }
                }
            }
            if (enableSecondary)
            {
                for (int k = 0; k < 2; k++)
                {
                    int da = k == 0 ? ds0a : ds1a, db = k == 0 ? ds0b : ds1b;
                    var tap = Vector128.Create((short)SecTaps[k]);
                    var s0 = Vector128.LoadUnsafe(ref src, (nuint)(p + da));
                    var s1 = Vector128.LoadUnsafe(ref src, (nuint)(p - da));
                    var s2 = Vector128.LoadUnsafe(ref src, (nuint)(p + db));
                    var s3 = Vector128.LoadUnsafe(ref src, (nuint)(p - db));
                    if (clippingRequired)
                    {
                        max = Vector128.Max(max, Vector128.ConditionalSelect(Vector128.Equals(s0, veryLarge), max, s0));
                        max = Vector128.Max(max, Vector128.ConditionalSelect(Vector128.Equals(s1, veryLarge), max, s1));
                        max = Vector128.Max(max, Vector128.ConditionalSelect(Vector128.Equals(s2, veryLarge), max, s2));
                        max = Vector128.Max(max, Vector128.ConditionalSelect(Vector128.Equals(s3, veryLarge), max, s3));
                        min = Vector128.Min(min, Vector128.Min(Vector128.Min(s0, s1), Vector128.Min(s2, s3)));
                    }
                    sum += tap * ConstrainV(s0 - x, secT, secShift);
                    sum += tap * ConstrainV(s1 - x, secT, secShift);
                    sum += tap * ConstrainV(s2 - x, secT, secShift);
                    sum += tap * ConstrainV(s3 - x, secT, secShift);
                }
            }
            var neg = Vector128.LessThan(sum, Vector128<short>.Zero);   // all ones = -1
            var y = x + Vector128.ShiftRightArithmetic(Vector128.Create((short)8) + sum + neg, 4);
            if (clippingRequired) y = Vector128.Min(Vector128.Max(y, min), max);
            var b = Vector128.Narrow(y.AsUInt16(), y.AsUInt16());   // low bytes ((uint8_t)y; y is 0..255 here)
            if (bw == 8) Unsafe.WriteUnaligned(ref dst[dstOff + i * dstride], b.AsUInt64().ToScalar());
            else Unsafe.WriteUnaligned(ref dst[dstOff + i * dstride], b.AsUInt32().ToScalar());
        }
    }
}
