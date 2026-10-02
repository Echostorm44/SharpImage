using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// av1_compute_stats_highbd in AVX2 lanes. M and H are exact integer sums, so the summation order is free: the window
// samples are taken row-major (one 8-lane int16 row per window row, lanes past the window zeroed), two horizontally
// adjacent pixels interleaved so one vpmaddwd adds both pixels' products, the int32 sums flushed into 64-bit totals
// before they could overflow ((2^bd - 1)^2 per product, two per lane per pixel pair).
internal sealed partial class AomPickRst
{
    [ThreadStatic] private static Vector256<int>[]? _statsAccH;
    [ThreadStatic] private static long[]? _statsH64;

    [SkipLocalsInit]
    internal static void ComputeStatsHbdAvx2(int wienerWin, AomYv12Plane dgd, AomYv12Plane src, int hStart, int hEnd, int vStart, int vEnd,
        long[] M, long[] H, int bd)
    {
        int n = wienerWin * wienerWin, halfwin = wienerWin >> 1;
        ushort avg = FindAverageHbd(dgd, hStart, hEnd, vStart, vEnd);
        int divider = bd == 12 ? 16 : bd == 10 ? 4 : 1;
        // accH[(row * 8 + col) * 8 + v]: the sums of y(row, col) * y(v, lane) for window rows v >= row
        var accH = _statsAccH ??= new Vector256<int>[64 * 8];
        var h64 = _statsH64 ??= new long[64 * 64 + 64];
        Array.Clear(accH);
        Array.Clear(h64);
        Unsafe.SkipInit(out StackArr8<Vector256<int>> accM);
        Unsafe.SkipInit(out StackArr8<Vector256<int>> pairs);
        for (int v = 0; v < 8; v++) accM[v] = Vector256<int>.Zero;
        long maxV = (1L << bd) - 1;
        int maxPairs = (int)Math.Min(int.MaxValue / (2 * maxV * maxV), 1 << 20);
        var mask = Vector128.LessThan(Vector128.Create((short)0, 1, 2, 3, 4, 5, 6, 7), Vector128.Create((short)wienerWin));
        var avgV = Vector128.Create((short)avg);
        ushort[] d = dgd.Buf16, s = src.Buf16;
        ref ushort d0 = ref MemoryMarshal.GetArrayDataReference(d);
        ref Vector256<int> acc0 = ref MemoryMarshal.GetArrayDataReference(accH);
        Unsafe.SkipInit(out StackArr8<int> rowBase);
        int count = 0;
        for (int i = vStart; i < vEnd; i++)
        {
            int srow = src.At(0, i);
            for (int l = 0; l < wienerWin; l++) rowBase[l] = dgd.At(0, i + l - halfwin) - halfwin;
            for (int j = hStart; j < hEnd; j += 2)
            {
                bool two = j + 1 < hEnd;
                for (int l = 0; l < wienerWin; l++)
                {
                    ref ushort p = ref Unsafe.Add(ref d0, rowBase[l] + j);
                    var a = (Vector128.LoadUnsafe(ref p).AsInt16() - avgV) & mask;
                    var b = two ? (Vector128.LoadUnsafe(ref p, 1).AsInt16() - avgV) & mask : Vector128<short>.Zero;
                    pairs[l] = Vector256.Create(Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b)).AsInt32();
                }
                int xa = s[srow + j] - avg, xb = two ? s[srow + j + 1] - avg : 0;
                var xp = Vector256.Create((xa & 0xffff) | (xb << 16)).AsInt16();
                for (int v = 0; v < wienerWin; v++) accM[v] += Avx2.MultiplyAddAdjacent(xp, pairs[v].AsInt16());
                for (int lk = 0; lk < wienerWin; lk++)
                {
                    var pk = pairs[lk];
                    for (int ck = 0; ck < wienerWin; ck++)
                    {
                        var bc = Avx2.PermuteVar8x32(pk, Vector256.Create(ck)).AsInt16();
                        ref Vector256<int> acc = ref Unsafe.Add(ref acc0, (lk * 8 + ck) * 8);
                        for (int v = lk; v < wienerWin; v++)
                            Unsafe.Add(ref acc, v) += Avx2.MultiplyAddAdjacent(bc, pairs[v].AsInt16());
                    }
                }
                if (++count == maxPairs) { FlushStatsHbd(wienerWin, accH, h64, ref accM[0]); count = 0; }
            }
        }
        FlushStatsHbd(wienerWin, accH, h64, ref accM[0]);
        // back to libaom's window order: index c * win + l is the sample of window column c, row l
        for (int o1 = 0; o1 < n; o1++)
        {
            int c1 = o1 / wienerWin, l1 = o1 % wienerWin, r1 = l1 * 8 + c1;
            M[o1] = h64[64 * 64 + r1];
            for (int o2 = o1; o2 < n; o2++)
            {
                int c2 = o2 / wienerWin, l2 = o2 % wienerWin, r2 = l2 * 8 + c2;
                H[o1 * n + o2] = l1 <= l2 ? h64[r1 * 64 + r2] : h64[r2 * 64 + r1];
            }
        }
        for (int k = 0; k < n; ++k)
        {
            M[k] /= divider;
            for (int l = k; l < n; ++l) H[k * n + l] /= divider;
            for (int l = k + 1; l < n; ++l) H[l * n + k] = H[k * n + l];
        }
    }

    // the int32 lane sums into the 64-bit totals (h64[r1 * 64 + r2], M at h64[64 * 64 + r]), the lanes zeroed
    private static void FlushStatsHbd(int wienerWin, Vector256<int>[] accH, long[] h64, ref Vector256<int> accM)
    {
        for (int lk = 0; lk < wienerWin; lk++)
            for (int ck = 0; ck < wienerWin; ck++)
            {
                int r1 = lk * 8 + ck;
                for (int v = lk; v < wienerWin; v++)
                {
                    ref Vector256<int> a = ref accH[r1 * 8 + v];
                    for (int c = 0; c < wienerWin; c++) h64[r1 * 64 + v * 8 + c] += a.GetElement(c);
                    a = Vector256<int>.Zero;
                }
            }
        for (int v = 0; v < wienerWin; v++)
        {
            ref Vector256<int> a = ref Unsafe.Add(ref accM, v);
            for (int c = 0; c < wienerWin; c++) h64[64 * 64 + v * 8 + c] += a.GetElement(c);
            a = Vector256<int>.Zero;
        }
    }
}
