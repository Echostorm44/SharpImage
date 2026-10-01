using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

using V = Vector256<short>;

/// <summary>The vectorized deblocking kernels: the dispatched aom_lpf_{horizontal,vertical}_{4,6,8,14}{,_dual,_quad}
/// (libaom's SSE2 / AVX2 versions, all bit-exact with aom_dsp/loopfilter.c). One call filters 4 (single), 8 (dual) or
/// 16 (quad) lines of an edge with one set of thresholds; every line is a 16-bit lane of an AVX2 register and the C
/// arithmetic (signed_char_clamp, the masks, the 4/6/8/14-tap sums) is evaluated exactly per lane. Vertical edges are
/// transposed in and out (16x8 bytes for the 4/6/8 filters, 16x16 for the 14-tap one). Twin-verified against the
/// dispatched kernels.</summary>
internal static partial class AomLpf
{
    public static bool SimdSupported => Avx2.IsSupported;

    // ---- per-lane C arithmetic ----------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V Ad(V a, V b) => Avx2.Abs(Avx2.Subtract(a, b)).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V Max(V a, V b) => Avx2.Max(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V Clamp8(V x) => Avx2.Min(Avx2.Max(x, Vector256.Create((short)-128)), Vector256.Create((short)127));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V Sel(V m, V ifSet, V ifClear) => Avx2.BlendVariable(ifClear.AsByte(), ifSet.AsByte(), m.AsByte()).AsInt16();

    /// <summary>The blimit test shared by every filter mask: abs(p0 - q0) * 2 + abs(p1 - q1) / 2 &gt; blimit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V EdgeOver(V p1, V p0, V q0, V q1, V blimit)
        => Avx2.CompareGreaterThan(Avx2.Add(Avx2.ShiftLeftLogical(Ad(p0, q0), 1), Avx2.ShiftRightLogical(Ad(p1, q1), 1)), blimit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V Hev(V p1, V p0, V q0, V q1, V thresh) => Avx2.CompareGreaterThan(Max(Ad(p1, p0), Ad(q1, q0)), thresh);

    /// <summary>filter4: mask / hev are all-ones lanes where set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter4(V mask, V hev, V p1, V p0, V q0, V q1, out V op1, out V op0, out V oq0, out V oq1)
    {
        V c128 = Vector256.Create((short)128);
        V ps1 = Avx2.Subtract(p1, c128), ps0 = Avx2.Subtract(p0, c128);
        V qs0 = Avx2.Subtract(q0, c128), qs1 = Avx2.Subtract(q1, c128);
        // add outer taps if we have high edge variance
        V filter = Avx2.And(Clamp8(Avx2.Subtract(ps1, qs1)), hev);
        // inner taps
        V d = Avx2.Subtract(qs0, ps0);
        filter = Avx2.And(Clamp8(Avx2.Add(filter, Avx2.Add(Avx2.Add(d, d), d))), mask);
        // save bottom 3 bits so that we round one side +4 and the other +3
        V filter1 = Avx2.ShiftRightArithmetic(Clamp8(Avx2.Add(filter, Vector256.Create((short)4))), 3);
        V filter2 = Avx2.ShiftRightArithmetic(Clamp8(Avx2.Add(filter, Vector256.Create((short)3))), 3);
        oq0 = Avx2.Add(Clamp8(Avx2.Subtract(qs0, filter1)), c128);
        op0 = Avx2.Add(Clamp8(Avx2.Add(ps0, filter2)), c128);
        // outer tap adjustments
        filter = Avx2.AndNot(hev, Avx2.ShiftRightArithmetic(Avx2.Add(filter1, Vector256.Create((short)1)), 1));
        oq1 = Avx2.Add(Clamp8(Avx2.Subtract(qs1, filter)), c128);
        op1 = Avx2.Add(Clamp8(Avx2.Add(ps1, filter)), c128);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V R3(V sum) => Avx2.ShiftRightLogical(Avx2.Add(sum, Vector256.Create((short)4)), 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V R4(V sum) => Avx2.ShiftRightLogical(Avx2.Add(sum, Vector256.Create((short)8)), 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V X2(V a) => Avx2.ShiftLeftLogical(a, 1);

    /// <summary>lpf_4 on registers (filter_mask2 + filter4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Core4(ref V p1, ref V p0, ref V q0, ref V q1, V blimit, V limit, V thresh)
    {
        V over = Avx2.Or(Avx2.CompareGreaterThan(Max(Ad(p1, p0), Ad(q1, q0)), limit), EdgeOver(p1, p0, q0, q1, blimit));
        V mask = Avx2.Xor(over, Vector256<short>.AllBitsSet);
        V hev = Hev(p1, p0, q0, q1, thresh);
        Filter4(mask, hev, p1, p0, q0, q1, out p1, out p0, out q0, out q1);
    }

    /// <summary>lpf_6 (chroma): filter_mask3_chroma, flat_mask3_chroma, filter6.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Core6(V p2, ref V p1, ref V p0, ref V q0, ref V q1, V q2, V blimit, V limit, V thresh)
    {
        V dp10 = Ad(p1, p0), dq10 = Ad(q1, q0);
        V m = Max(Max(Ad(p2, p1), dp10), Max(dq10, Ad(q2, q1)));
        V over = Avx2.Or(Avx2.CompareGreaterThan(m, limit), EdgeOver(p1, p0, q0, q1, blimit));
        V mask = Avx2.Xor(over, Vector256<short>.AllBitsSet);
        V one = Vector256.Create((short)1);
        V flatOver = Avx2.CompareGreaterThan(Max(Max(dp10, dq10), Max(Ad(p2, p0), Ad(q2, q0))), one);
        V flatMask = Avx2.AndNot(flatOver, mask);   // flat && mask
        V hev = Avx2.CompareGreaterThan(Max(dp10, dq10), thresh);
        Filter4(mask, hev, p1, p0, q0, q1, out V f1, out V f0, out V g0, out V g1);
        if (Avx2.TestZ(flatMask, flatMask))
        {
            p1 = f1; p0 = f0; q0 = g0; q1 = g1;
            return;
        }
        // 5-tap filter [1, 2, 2, 2, 1]
        V w1 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p2, X2(p2)), X2(p1)), Avx2.Add(X2(p0), q0)));
        V w0 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p2, X2(p1)), X2(p0)), Avx2.Add(X2(q0), q1)));
        V v0 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p1, X2(p0)), X2(q0)), Avx2.Add(X2(q1), q2)));
        V v1 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p0, X2(q0)), X2(q1)), Avx2.Add(q2, X2(q2))));
        p1 = Sel(flatMask, w1, f1);
        p0 = Sel(flatMask, w0, f0);
        q0 = Sel(flatMask, v0, g0);
        q1 = Sel(flatMask, v1, g1);
    }

    /// <summary>The filter_mask of the 8 / 14 filters and flat_mask4(1, p3..q3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Masks8(V p3, V p2, V p1, V p0, V q0, V q1, V q2, V q3, V blimit, V limit, V thresh,
        out V mask, out V flatMask, out V hev)
    {
        V dp10 = Ad(p1, p0), dq10 = Ad(q1, q0);
        V m = Max(Max(Max(Ad(p3, p2), Ad(p2, p1)), Max(dp10, dq10)), Max(Ad(q2, q1), Ad(q3, q2)));
        V over = Avx2.Or(Avx2.CompareGreaterThan(m, limit), EdgeOver(p1, p0, q0, q1, blimit));
        mask = Avx2.Xor(over, Vector256<short>.AllBitsSet);
        V f = Max(Max(Max(dp10, dq10), Max(Ad(p2, p0), Ad(q2, q0))), Max(Ad(p3, p0), Ad(q3, q0)));
        flatMask = Avx2.AndNot(Avx2.CompareGreaterThan(f, Vector256.Create((short)1)), mask);   // flat && mask
        hev = Avx2.CompareGreaterThan(Max(dp10, dq10), thresh);
    }

    /// <summary>The 7-tap filter [1, 1, 1, 2, 1, 1, 1] of filter8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Wide8(V p3, V p2, V p1, V p0, V q0, V q1, V q2, V q3,
        out V w2, out V w1, out V w0, out V v0, out V v1, out V v2)
    {
        V p3x2 = X2(p3), q3x2 = X2(q3);
        w2 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p3x2, p3), X2(p2)), Avx2.Add(Avx2.Add(p1, p0), q0)));
        w1 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p3x2, p2), X2(p1)), Avx2.Add(Avx2.Add(p0, q0), q1)));
        w0 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p3, p2), Avx2.Add(p1, X2(p0))), Avx2.Add(Avx2.Add(q0, q1), q2)));
        v0 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p2, p1), Avx2.Add(p0, X2(q0))), Avx2.Add(Avx2.Add(q1, q2), q3)));
        v1 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p1, p0), Avx2.Add(q0, X2(q1))), Avx2.Add(q2, q3x2)));
        v2 = R3(Avx2.Add(Avx2.Add(Avx2.Add(p0, q0), Avx2.Add(q1, X2(q2))), Avx2.Add(q3x2, q3)));
    }

    /// <summary>lpf_8: filter_mask, flat_mask4, filter8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Core8(V p3, ref V p2, ref V p1, ref V p0, ref V q0, ref V q1, ref V q2, V q3, V blimit, V limit,
        V thresh)
    {
        Masks8(p3, p2, p1, p0, q0, q1, q2, q3, blimit, limit, thresh, out V mask, out V flatMask, out V hev);
        Filter4(mask, hev, p1, p0, q0, q1, out V f1, out V f0, out V g0, out V g1);
        if (Avx2.TestZ(flatMask, flatMask))
        {
            p1 = f1; p0 = f0; q0 = g0; q1 = g1;
            return;
        }
        Wide8(p3, p2, p1, p0, q0, q1, q2, q3, out V w2, out V w1, out V w0, out V v0, out V v1, out V v2);
        p2 = Sel(flatMask, w2, p2);
        p1 = Sel(flatMask, w1, f1);
        p0 = Sel(flatMask, w0, f0);
        q0 = Sel(flatMask, v0, g0);
        q1 = Sel(flatMask, v1, g1);
        q2 = Sel(flatMask, v2, q2);
    }

    /// <summary>lpf_14 (mb_lpf): filter_mask, flat_mask4 on p3..q3 and on p6, p5, p4, p0, q0, q4, q5, q6, filter14.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Core14(V p6, ref V p5, ref V p4, ref V p3, ref V p2, ref V p1, ref V p0, ref V q0, ref V q1,
        ref V q2, ref V q3, ref V q4, ref V q5, V q6, V blimit, V limit, V thresh)
    {
        Masks8(p3, p2, p1, p0, q0, q1, q2, q3, blimit, limit, thresh, out V mask, out V flatMask, out V hev);
        Filter4(mask, hev, p1, p0, q0, q1, out V f1, out V f0, out V g0, out V g1);
        if (Avx2.TestZ(flatMask, flatMask))
        {
            p1 = f1; p0 = f0; q0 = g0; q1 = g1;
            return;
        }
        Wide8(p3, p2, p1, p0, q0, q1, q2, q3, out V w2, out V w1, out V w0, out V v0, out V v1, out V v2);
        V f2 = Max(Max(Max(Ad(p6, p0), Ad(q6, q0)), Max(Ad(p5, p0), Ad(q5, q0))), Max(Ad(p4, p0), Ad(q4, q0)));
        V flat2Mask = Avx2.AndNot(Avx2.CompareGreaterThan(f2, Vector256.Create((short)1)), flatMask);
        if (Avx2.TestZ(flat2Mask, flat2Mask))
        {
            p2 = Sel(flatMask, w2, p2);
            p1 = Sel(flatMask, w1, f1);
            p0 = Sel(flatMask, w0, f0);
            q0 = Sel(flatMask, v0, g0);
            q1 = Sel(flatMask, v1, g1);
            q2 = Sel(flatMask, v2, q2);
            return;
        }
        // 13-tap filter [1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1]
        V p6x2 = X2(p6), q6x2 = X2(q6);
        V p6x4 = X2(p6x2), q6x4 = X2(q6x2);
        V p5x2 = X2(p5), p4x2 = X2(p4), p3x2 = X2(p3), p2x2 = X2(p2), p1x2 = X2(p1), p0x2 = X2(p0);
        V q0x2 = X2(q0), q1x2 = X2(q1), q2x2 = X2(q2), q3x2 = X2(q3), q4x2 = X2(q4), q5x2 = X2(q5);
        V s0 = Avx2.Add(Avx2.Add(p3, p2), Avx2.Add(p1, p0));   // p3 + p2 + p1 + p0
        V t0 = Avx2.Add(Avx2.Add(q0, q1), Avx2.Add(q2, q3));   // q0 + q1 + q2 + q3
        // p6 * 7 + p5 * 2 + p4 * 2 + p3 + p2 + p1 + p0 + q0
        V x5 = R4(Avx2.Add(Avx2.Add(Avx2.Subtract(Avx2.ShiftLeftLogical(p6, 3), p6), Avx2.Add(p5x2, p4x2)), Avx2.Add(s0, q0)));
        // p6 * 5 + p5 * 2 + p4 * 2 + p3 * 2 + p2 + p1 + p0 + q0 + q1
        V x4 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p6x4, p6), Avx2.Add(p5x2, p4x2)),
            Avx2.Add(Avx2.Add(s0, p3), Avx2.Add(q0, q1))));
        // p6 * 4 + p5 + p4 * 2 + p3 * 2 + p2 * 2 + p1 + p0 + q0 + q1 + q2
        V x3 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p6x4, p5), Avx2.Add(p4x2, p3x2)),
            Avx2.Add(Avx2.Add(p2x2, Avx2.Add(p1, p0)), Avx2.Add(Avx2.Add(q0, q1), q2))));
        // p6 * 3 + p5 + p4 + p3 * 2 + p2 * 2 + p1 * 2 + p0 + q0 + q1 + q2 + q3
        V x2 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p6x2, p6), Avx2.Add(p5, p4)),
            Avx2.Add(Avx2.Add(p3x2, p2x2), Avx2.Add(Avx2.Add(p1x2, p0), t0))));
        // p6 * 2 + p5 + p4 + p3 + p2 * 2 + p1 * 2 + p0 * 2 + q0 + q1 + q2 + q3 + q4
        V x1 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p6x2, p5), Avx2.Add(p4, p3)),
            Avx2.Add(Avx2.Add(p2x2, p1x2), Avx2.Add(Avx2.Add(p0x2, t0), q4))));
        // p6 + p5 + p4 + p3 + p2 + p1 * 2 + p0 * 2 + q0 * 2 + q1 + q2 + q3 + q4 + q5
        V x0 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p6, p5), Avx2.Add(p4, p3)),
            Avx2.Add(Avx2.Add(p2, p1x2), Avx2.Add(Avx2.Add(p0x2, q0x2), Avx2.Add(Avx2.Add(q1, q2), Avx2.Add(Avx2.Add(q3, q4), q5))))));
        // p5 + p4 + p3 + p2 + p1 + p0 * 2 + q0 * 2 + q1 * 2 + q2 + q3 + q4 + q5 + q6
        V y0 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p5, p4), Avx2.Add(p3, p2)),
            Avx2.Add(Avx2.Add(p1, p0x2), Avx2.Add(Avx2.Add(q0x2, q1x2), Avx2.Add(Avx2.Add(q2, q3), Avx2.Add(Avx2.Add(q4, q5), q6))))));
        // p4 + p3 + p2 + p1 + p0 + q0 * 2 + q1 * 2 + q2 * 2 + q3 + q4 + q5 + q6 * 2
        V y1 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p4, s0), Avx2.Add(q0x2, q1x2)),
            Avx2.Add(Avx2.Add(q2x2, q3), Avx2.Add(Avx2.Add(q4, q5), q6x2))));
        // p3 + p2 + p1 + p0 + q0 + q1 * 2 + q2 * 2 + q3 * 2 + q4 + q5 + q6 * 3
        V y2 = R4(Avx2.Add(Avx2.Add(s0, q0), Avx2.Add(Avx2.Add(q1x2, q2x2), Avx2.Add(Avx2.Add(q3x2, q4), Avx2.Add(q5, Avx2.Add(q6x2, q6))))));
        // p2 + p1 + p0 + q0 + q1 + q2 * 2 + q3 * 2 + q4 * 2 + q5 + q6 * 4
        V y3 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p2, p1), Avx2.Add(p0, q0)),
            Avx2.Add(Avx2.Add(q1, q2x2), Avx2.Add(Avx2.Add(q3x2, q4x2), Avx2.Add(q5, q6x4)))));
        // p1 + p0 + q0 + q1 + q2 + q3 * 2 + q4 * 2 + q5 * 2 + q6 * 5
        V y4 = R4(Avx2.Add(Avx2.Add(Avx2.Add(p1, p0), Avx2.Add(q0, q1)),
            Avx2.Add(Avx2.Add(q2, q3x2), Avx2.Add(Avx2.Add(q4x2, q5x2), Avx2.Add(q6x4, q6)))));
        // p0 + q0 + q1 + q2 + q3 + q4 * 2 + q5 * 2 + q6 * 7
        V y5 = R4(Avx2.Add(Avx2.Add(p0, t0), Avx2.Add(Avx2.Add(q4x2, q5x2), Avx2.Subtract(Avx2.ShiftLeftLogical(q6, 3), q6))));

        p5 = Sel(flat2Mask, x5, p5);
        p4 = Sel(flat2Mask, x4, p4);
        p3 = Sel(flat2Mask, x3, p3);
        p2 = Sel(flat2Mask, x2, Sel(flatMask, w2, p2));
        p1 = Sel(flat2Mask, x1, Sel(flatMask, w1, f1));
        p0 = Sel(flat2Mask, x0, Sel(flatMask, w0, f0));
        q0 = Sel(flat2Mask, y0, Sel(flatMask, v0, g0));
        q1 = Sel(flat2Mask, y1, Sel(flatMask, v1, g1));
        q2 = Sel(flat2Mask, y2, Sel(flatMask, v2, q2));
        q3 = Sel(flat2Mask, y3, q3);
        q4 = Sel(flat2Mask, y4, q4);
        q5 = Sel(flat2Mask, y5, q5);
    }

    // ---- loads / stores -----------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V Ld16(ref byte b, int off) => Avx2.ConvertToVector256Int16(Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, off)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Narrow(V v) => Sse2.PackUnsignedSaturate(v.GetLower(), v.GetUpper());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StN(ref byte b, int off, Vector128<byte> v, int n)
    {
        ref byte d = ref Unsafe.Add(ref b, off);
        if (n == 16) Unsafe.WriteUnaligned(ref d, v);
        else if (n == 8) Unsafe.WriteUnaligned(ref d, v.AsUInt64().ToScalar());
        else Unsafe.WriteUnaligned(ref d, v.AsUInt32().ToScalar());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StH(ref byte b, int off, V v, int n) => StN(ref b, off, Narrow(v), n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Ld8(ref byte b, int off) => Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, off))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Ld16B(ref byte b, int off) => Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, off));

    // ---- transposes ---------------------------------------------------------------------------------------------------

    /// <summary>Stages 2-4 of a 16-row byte transpose: a[k] = interleaved bytes of rows 2k and 2k + 1 (8 columns);
    /// returns the 8 columns, lane = row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tr16x8Tail(Vector128<byte> a0, Vector128<byte> a1, Vector128<byte> a2, Vector128<byte> a3,
        Vector128<byte> a4, Vector128<byte> a5, Vector128<byte> a6, Vector128<byte> a7,
        out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
        out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7)
    {
        // rows 0-3 / 4-7 / 8-11 / 12-15, columns 0-3 (lo) and 4-7 (hi), 4 bytes per column
        var b0 = Sse2.UnpackLow(a0.AsInt16(), a1.AsInt16()).AsInt32();
        var b1 = Sse2.UnpackHigh(a0.AsInt16(), a1.AsInt16()).AsInt32();
        var b2 = Sse2.UnpackLow(a2.AsInt16(), a3.AsInt16()).AsInt32();
        var b3 = Sse2.UnpackHigh(a2.AsInt16(), a3.AsInt16()).AsInt32();
        var b4 = Sse2.UnpackLow(a4.AsInt16(), a5.AsInt16()).AsInt32();
        var b5 = Sse2.UnpackHigh(a4.AsInt16(), a5.AsInt16()).AsInt32();
        var b6 = Sse2.UnpackLow(a6.AsInt16(), a7.AsInt16()).AsInt32();
        var b7 = Sse2.UnpackHigh(a6.AsInt16(), a7.AsInt16()).AsInt32();
        // rows 0-7 (d) and 8-15 (e), two columns each
        var d0 = Sse2.UnpackLow(b0, b2).AsInt64();
        var d1 = Sse2.UnpackHigh(b0, b2).AsInt64();
        var d2 = Sse2.UnpackLow(b1, b3).AsInt64();
        var d3 = Sse2.UnpackHigh(b1, b3).AsInt64();
        var e0 = Sse2.UnpackLow(b4, b6).AsInt64();
        var e1 = Sse2.UnpackHigh(b4, b6).AsInt64();
        var e2 = Sse2.UnpackLow(b5, b7).AsInt64();
        var e3 = Sse2.UnpackHigh(b5, b7).AsInt64();
        c0 = Sse2.UnpackLow(d0, e0).AsByte();
        c1 = Sse2.UnpackHigh(d0, e0).AsByte();
        c2 = Sse2.UnpackLow(d1, e1).AsByte();
        c3 = Sse2.UnpackHigh(d1, e1).AsByte();
        c4 = Sse2.UnpackLow(d2, e2).AsByte();
        c5 = Sse2.UnpackHigh(d2, e2).AsByte();
        c6 = Sse2.UnpackLow(d3, e3).AsByte();
        c7 = Sse2.UnpackHigh(d3, e3).AsByte();
    }

    /// <summary>8 columns of 16 lanes back to 16 rows of 8 bytes: g[m] holds rows 2m (low 8 bytes) and 2m + 1 (high).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tr8x16(Vector128<byte> c0, Vector128<byte> c1, Vector128<byte> c2, Vector128<byte> c3,
        Vector128<byte> c4, Vector128<byte> c5, Vector128<byte> c6, Vector128<byte> c7,
        out Vector128<long> g0, out Vector128<long> g1, out Vector128<long> g2, out Vector128<long> g3,
        out Vector128<long> g4, out Vector128<long> g5, out Vector128<long> g6, out Vector128<long> g7)
    {
        // column pairs, rows 0-7 (lo) and 8-15 (hi)
        var e0 = Sse2.UnpackLow(c0, c1).AsInt16();
        var e1 = Sse2.UnpackLow(c2, c3).AsInt16();
        var e2 = Sse2.UnpackLow(c4, c5).AsInt16();
        var e3 = Sse2.UnpackLow(c6, c7).AsInt16();
        var h0 = Sse2.UnpackHigh(c0, c1).AsInt16();
        var h1 = Sse2.UnpackHigh(c2, c3).AsInt16();
        var h2 = Sse2.UnpackHigh(c4, c5).AsInt16();
        var h3 = Sse2.UnpackHigh(c6, c7).AsInt16();
        // 4 rows x 4 columns
        var f0 = Sse2.UnpackLow(e0, e1).AsInt32();    // rows 0-3, cols 0-3
        var f1 = Sse2.UnpackHigh(e0, e1).AsInt32();   // rows 4-7, cols 0-3
        var f2 = Sse2.UnpackLow(e2, e3).AsInt32();    // rows 0-3, cols 4-7
        var f3 = Sse2.UnpackHigh(e2, e3).AsInt32();   // rows 4-7, cols 4-7
        var k0 = Sse2.UnpackLow(h0, h1).AsInt32();
        var k1 = Sse2.UnpackHigh(h0, h1).AsInt32();
        var k2 = Sse2.UnpackLow(h2, h3).AsInt32();
        var k3 = Sse2.UnpackHigh(h2, h3).AsInt32();
        g0 = Sse2.UnpackLow(f0, f2).AsInt64();
        g1 = Sse2.UnpackHigh(f0, f2).AsInt64();
        g2 = Sse2.UnpackLow(f1, f3).AsInt64();
        g3 = Sse2.UnpackHigh(f1, f3).AsInt64();
        g4 = Sse2.UnpackLow(k0, k2).AsInt64();
        g5 = Sse2.UnpackHigh(k0, k2).AsInt64();
        g6 = Sse2.UnpackLow(k1, k3).AsInt64();
        g7 = Sse2.UnpackHigh(k1, k3).AsInt64();
    }

    // ---- horizontal edges (lines along x) -----------------------------------------------------------------------------

    /// <summary>aom_lpf_horizontal_{len}{,_dual,_quad}: lines = 4, 8 or 16 columns starting at s; pitch = the stride.</summary>
    public static void Horizontal(int len, byte[] buf, int s, int pitch, int lines, int blimit, int limit, int thresh)
    {
        ref byte b = ref MemoryMarshal.GetArrayDataReference(buf);
        V bl = Vector256.Create((short)blimit), li = Vector256.Create((short)limit), th = Vector256.Create((short)thresh);
        switch (len)
        {
            case 4:
            {
                V p1 = Ld16(ref b, s - 2 * pitch), p0 = Ld16(ref b, s - pitch), q0 = Ld16(ref b, s), q1 = Ld16(ref b, s + pitch);
                Core4(ref p1, ref p0, ref q0, ref q1, bl, li, th);
                StH(ref b, s - 2 * pitch, p1, lines);
                StH(ref b, s - pitch, p0, lines);
                StH(ref b, s, q0, lines);
                StH(ref b, s + pitch, q1, lines);
                break;
            }
            case 6:
            {
                V p2 = Ld16(ref b, s - 3 * pitch), p1 = Ld16(ref b, s - 2 * pitch), p0 = Ld16(ref b, s - pitch);
                V q0 = Ld16(ref b, s), q1 = Ld16(ref b, s + pitch), q2 = Ld16(ref b, s + 2 * pitch);
                Core6(p2, ref p1, ref p0, ref q0, ref q1, q2, bl, li, th);
                StH(ref b, s - 2 * pitch, p1, lines);
                StH(ref b, s - pitch, p0, lines);
                StH(ref b, s, q0, lines);
                StH(ref b, s + pitch, q1, lines);
                break;
            }
            case 8:
            {
                V p3 = Ld16(ref b, s - 4 * pitch), p2 = Ld16(ref b, s - 3 * pitch), p1 = Ld16(ref b, s - 2 * pitch);
                V p0 = Ld16(ref b, s - pitch), q0 = Ld16(ref b, s), q1 = Ld16(ref b, s + pitch);
                V q2 = Ld16(ref b, s + 2 * pitch), q3 = Ld16(ref b, s + 3 * pitch);
                Core8(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, bl, li, th);
                StH(ref b, s - 3 * pitch, p2, lines);
                StH(ref b, s - 2 * pitch, p1, lines);
                StH(ref b, s - pitch, p0, lines);
                StH(ref b, s, q0, lines);
                StH(ref b, s + pitch, q1, lines);
                StH(ref b, s + 2 * pitch, q2, lines);
                break;
            }
            case 14:
            {
                V p6 = Ld16(ref b, s - 7 * pitch), p5 = Ld16(ref b, s - 6 * pitch), p4 = Ld16(ref b, s - 5 * pitch);
                V p3 = Ld16(ref b, s - 4 * pitch), p2 = Ld16(ref b, s - 3 * pitch), p1 = Ld16(ref b, s - 2 * pitch);
                V p0 = Ld16(ref b, s - pitch), q0 = Ld16(ref b, s), q1 = Ld16(ref b, s + pitch);
                V q2 = Ld16(ref b, s + 2 * pitch), q3 = Ld16(ref b, s + 3 * pitch), q4 = Ld16(ref b, s + 4 * pitch);
                V q5 = Ld16(ref b, s + 5 * pitch), q6 = Ld16(ref b, s + 6 * pitch);
                Core14(p6, ref p5, ref p4, ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, ref q4, ref q5, q6,
                    bl, li, th);
                StH(ref b, s - 6 * pitch, p5, lines);
                StH(ref b, s - 5 * pitch, p4, lines);
                StH(ref b, s - 4 * pitch, p3, lines);
                StH(ref b, s - 3 * pitch, p2, lines);
                StH(ref b, s - 2 * pitch, p1, lines);
                StH(ref b, s - pitch, p0, lines);
                StH(ref b, s, q0, lines);
                StH(ref b, s + pitch, q1, lines);
                StH(ref b, s + 2 * pitch, q2, lines);
                StH(ref b, s + 3 * pitch, q3, lines);
                StH(ref b, s + 4 * pitch, q4, lines);
                StH(ref b, s + 5 * pitch, q5, lines);
                break;
            }
        }
    }

    // ---- vertical edges (lines along y) -------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> RowOr0(ref byte b, int off, bool present, bool wide)
        => !present ? Vector128<byte>.Zero : wide ? Ld16B(ref b, off) : Ld8(ref b, off);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V W(Vector128<byte> c) => Avx2.ConvertToVector256Int16(c);

    /// <summary>aom_lpf_vertical_{len}{,_dual,_quad}: lines = 4, 8 or 16 rows starting at s; pitch = the stride.</summary>
    public static void Vertical(int len, byte[] buf, int s, int pitch, int lines, int blimit, int limit, int thresh)
    {
        ref byte b = ref MemoryMarshal.GetArrayDataReference(buf);
        V bl = Vector256.Create((short)blimit), li = Vector256.Create((short)limit), th = Vector256.Create((short)thresh);
        bool wide = len == 14;
        int x0 = wide ? s - 8 : s - 4;
        bool h8 = lines >= 8, h16 = lines >= 16;
        var r0 = RowOr0(ref b, x0, true, wide);
        var r1 = RowOr0(ref b, x0 + pitch, true, wide);
        var r2 = RowOr0(ref b, x0 + 2 * pitch, true, wide);
        var r3 = RowOr0(ref b, x0 + 3 * pitch, true, wide);
        var r4 = RowOr0(ref b, x0 + 4 * pitch, h8, wide);
        var r5 = RowOr0(ref b, x0 + 5 * pitch, h8, wide);
        var r6 = RowOr0(ref b, x0 + 6 * pitch, h8, wide);
        var r7 = RowOr0(ref b, x0 + 7 * pitch, h8, wide);
        var r8 = RowOr0(ref b, x0 + 8 * pitch, h16, wide);
        var r9 = RowOr0(ref b, x0 + 9 * pitch, h16, wide);
        var r10 = RowOr0(ref b, x0 + 10 * pitch, h16, wide);
        var r11 = RowOr0(ref b, x0 + 11 * pitch, h16, wide);
        var r12 = RowOr0(ref b, x0 + 12 * pitch, h16, wide);
        var r13 = RowOr0(ref b, x0 + 13 * pitch, h16, wide);
        var r14 = RowOr0(ref b, x0 + 14 * pitch, h16, wide);
        var r15 = RowOr0(ref b, x0 + 15 * pitch, h16, wide);
        // columns 0-7 of the 16 rows
        Tr16x8Tail(Sse2.UnpackLow(r0, r1), Sse2.UnpackLow(r2, r3), Sse2.UnpackLow(r4, r5), Sse2.UnpackLow(r6, r7),
            Sse2.UnpackLow(r8, r9), Sse2.UnpackLow(r10, r11), Sse2.UnpackLow(r12, r13), Sse2.UnpackLow(r14, r15),
            out var c0, out var c1, out var c2, out var c3, out var c4, out var c5, out var c6, out var c7);
        if (!wide)
        {
            // c0..c7 = p3, p2, p1, p0, q0, q1, q2, q3
            V p3 = W(c0), p2 = W(c1), p1 = W(c2), p0 = W(c3), q0 = W(c4), q1 = W(c5), q2 = W(c6), q3 = W(c7);
            switch (len)
            {
                case 4: Core4(ref p1, ref p0, ref q0, ref q1, bl, li, th); break;
                case 6: Core6(p2, ref p1, ref p0, ref q0, ref q1, q2, bl, li, th); break;
                default: Core8(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, bl, li, th); break;
            }
            Tr8x16(c0, Narrow(p2), Narrow(p1), Narrow(p0), Narrow(q0), Narrow(q1), Narrow(q2), c7,
                out var g0, out var g1, out var g2, out var g3, out var g4, out var g5, out var g6, out var g7);
            StRows8(ref b, x0, pitch, lines, g0, g1, g2, g3, g4, g5, g6, g7);
            return;
        }
        // columns 8-15
        Tr16x8Tail(Sse2.UnpackHigh(r0, r1), Sse2.UnpackHigh(r2, r3), Sse2.UnpackHigh(r4, r5), Sse2.UnpackHigh(r6, r7),
            Sse2.UnpackHigh(r8, r9), Sse2.UnpackHigh(r10, r11), Sse2.UnpackHigh(r12, r13), Sse2.UnpackHigh(r14, r15),
            out var c8, out var c9, out var c10, out var c11, out var c12, out var c13, out var c14, out var c15);
        {
            // c1..c14 = p6 .. q6 (c0 = p7, c15 = q7 untouched)
            V p6 = W(c1), p5 = W(c2), p4 = W(c3), p3 = W(c4), p2 = W(c5), p1 = W(c6), p0 = W(c7);
            V q0 = W(c8), q1 = W(c9), q2 = W(c10), q3 = W(c11), q4 = W(c12), q5 = W(c13), q6 = W(c14);
            Core14(p6, ref p5, ref p4, ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, ref q4, ref q5, q6,
                bl, li, th);
            Tr8x16(c0, c1, Narrow(p5), Narrow(p4), Narrow(p3), Narrow(p2), Narrow(p1), Narrow(p0),
                out var g0, out var g1, out var g2, out var g3, out var g4, out var g5, out var g6, out var g7);
            Tr8x16(Narrow(q0), Narrow(q1), Narrow(q2), Narrow(q3), Narrow(q4), Narrow(q5), c14, c15,
                out var k0, out var k1, out var k2, out var k3, out var k4, out var k5, out var k6, out var k7);
            StRows16(ref b, x0, pitch, lines, g0, g1, g2, g3, g4, g5, g6, g7, k0, k1, k2, k3, k4, k5, k6, k7);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void St8Pair(ref byte b, int off, int pitch, Vector128<long> g)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, off), g.ToScalar());
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, off + pitch), g.GetElement(1));
    }

    private static void StRows8(ref byte b, int x0, int pitch, int lines, Vector128<long> g0, Vector128<long> g1,
        Vector128<long> g2, Vector128<long> g3, Vector128<long> g4, Vector128<long> g5, Vector128<long> g6, Vector128<long> g7)
    {
        St8Pair(ref b, x0, pitch, g0);
        St8Pair(ref b, x0 + 2 * pitch, pitch, g1);
        if (lines < 8) return;
        St8Pair(ref b, x0 + 4 * pitch, pitch, g2);
        St8Pair(ref b, x0 + 6 * pitch, pitch, g3);
        if (lines < 16) return;
        St8Pair(ref b, x0 + 8 * pitch, pitch, g4);
        St8Pair(ref b, x0 + 10 * pitch, pitch, g5);
        St8Pair(ref b, x0 + 12 * pitch, pitch, g6);
        St8Pair(ref b, x0 + 14 * pitch, pitch, g7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void St16Pair(ref byte b, int off, int pitch, Vector128<long> lo, Vector128<long> hi)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, off), Sse2.UnpackLow(lo, hi));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, off + pitch), Sse2.UnpackHigh(lo, hi));
    }

    private static void StRows16(ref byte b, int x0, int pitch, int lines, Vector128<long> g0, Vector128<long> g1,
        Vector128<long> g2, Vector128<long> g3, Vector128<long> g4, Vector128<long> g5, Vector128<long> g6,
        Vector128<long> g7, Vector128<long> k0, Vector128<long> k1, Vector128<long> k2, Vector128<long> k3,
        Vector128<long> k4, Vector128<long> k5, Vector128<long> k6, Vector128<long> k7)
    {
        St16Pair(ref b, x0, pitch, g0, k0);
        St16Pair(ref b, x0 + 2 * pitch, pitch, g1, k1);
        if (lines < 8) return;
        St16Pair(ref b, x0 + 4 * pitch, pitch, g2, k2);
        St16Pair(ref b, x0 + 6 * pitch, pitch, g3, k3);
        if (lines < 16) return;
        St16Pair(ref b, x0 + 8 * pitch, pitch, g4, k4);
        St16Pair(ref b, x0 + 10 * pitch, pitch, g5, k5);
        St16Pair(ref b, x0 + 12 * pitch, pitch, g6, k6);
        St16Pair(ref b, x0 + 14 * pitch, pitch, g7, k7);
    }
}
