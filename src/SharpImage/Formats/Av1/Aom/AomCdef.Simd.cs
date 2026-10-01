using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpImage.Formats.Av1;

// The CDEF block filter (cdef_filter_block_internal) a row of 8 (or 4) samples at a time in int16 lanes, exactly the C
// arithmetic: int16 tap sums, constrain() with the per-call shift, the CDEF_VERY_LARGE-aware clipping range.
internal static partial class AomCdef
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ConstrainV(Vector128<short> diff, Vector128<short> threshold, int shift)
    {
        var ad = Vector128.Abs(diff);
        var t = Vector128.Max(Vector128<short>.Zero, threshold - Vector128.ShiftRightArithmetic(ad, shift));
        var v = Vector128.Min(ad, t);
        return Vector128.ConditionalSelect(Vector128.LessThan(diff, Vector128<short>.Zero), -v, v);
    }

    private static void FilterBlockV(byte[] dst, int dstOff, int dstride, ushort[] inb, int inOff, int priStrength, int secStrength, int dir,
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
        Span<byte> tmp = stackalloc byte[16];
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
            // y = x + ((8 + sum - (sum < 0)) >> 4)
            var neg = Vector128.LessThan(sum, Vector128<short>.Zero);   // all ones = -1
            var y = x + Vector128.ShiftRightArithmetic(Vector128.Create((short)8) + sum + neg, 4);
            if (clippingRequired) y = Vector128.Min(Vector128.Max(y, min), max);
            var b = Vector128.Narrow(y.AsUInt16(), y.AsUInt16());   // low bytes ((uint8_t)y; y is 0..255 here)
            if (bw == 8) Unsafe.WriteUnaligned(ref dst[dstOff + i * dstride], b.AsUInt64().ToScalar());
            else Unsafe.WriteUnaligned(ref dst[dstOff + i * dstride], b.AsUInt32().ToScalar());
        }
    }
}
