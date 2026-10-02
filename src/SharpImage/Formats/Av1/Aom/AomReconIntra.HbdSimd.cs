using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// The high bit depth directional predictors without edge upsampling in 8-sample 32-bit lanes (libaom dispatches its
// AVX2 highbd_dr_prediction_z* kernels here, computing the C's values): every sample is (a * (32 - shift) + b * shift
// + 16) >> 5 of two edge samples, as the scalar code; 12-bit products need the 32-bit lanes. Edge reads stay inside
// the NUM_INTRA_NEIGHBOUR_PIXELS buffers as in the 8-bit kernels.
internal static unsafe partial class AomReconIntra
{
    /// <summary>count samples dst[c] = interpolate(src[c], src[c + 1], shift) for c &lt; n, fill after.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HighbdDrRun(ushort* dst, ushort* src, int count, int n, int shift, ushort fill)
    {
        var w0 = Vector256.Create(32 - shift);
        var w1 = Vector256.Create(shift);
        var r16 = Vector256.Create(16);
        var fillV = Vector128.Create(fill);
        for (int c = 0; c < count; c += 8)
        {
            Vector128<ushort> res;
            if (c >= n) res = fillV;
            else
            {
                var a = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(src + c));
                var b = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(src + c + 1));
                var v = Vector256.ShiftRightLogical(a * w0 + b * w1 + r16, 5);
                res = Sse41.PackUnsignedSaturate(v.GetLower(), v.GetUpper());
                if (n - c < 8) res = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7), Vector128.Create((ushort)(n - c))), res, fillV);
            }
            int rem = count - c;
            if (rem >= 8) Sse2.Store(dst + c, res);
            else if (rem == 4) *(ulong*)(dst + c) = res.AsUInt64().ToScalar();
            else Unsafe.CopyBlockUnaligned(dst + c, &res, (uint)(rem * 2));
        }
    }

    /// <summary>Zone 1 without upsampling.</summary>
    internal static void HighbdDrPredictionZ1Simd(ushort* dst, nint stride, int bw, int bh, ushort* above, int dx)
    {
        int maxBaseX = bw + bh - 1;
        ushort fill = above[maxBaseX];
        int x = dx;
        for (int r = 0; r < bh; ++r, dst += stride, x += dx)
        {
            int b = x >> 6;
            int valid = maxBaseX - b;
            if (valid <= 0)
            {
                for (int i = r; i < bh; ++i, dst += stride) new Span<ushort>(dst, bw).Fill(fill);
                return;
            }
            HighbdDrRun(dst, above + b, bw, Math.Min(valid, bw), (x & 0x3F) >> 1, fill);
        }
    }

    /// <summary>Zone 3 without upsampling: zone 1 down the left edge, one column per run, transposed.</summary>
    internal static void HighbdDrPredictionZ3Simd(ushort* dst, nint stride, int bw, int bh, ushort* left, int dy)
    {
        int maxBaseY = bw + bh - 1;
        ushort fill = left[maxBaseY];
        ushort* t = stackalloc ushort[bw * 64];   // column c at t + c * 64
        int y = dy;
        for (int c = 0; c < bw; ++c, y += dy)
        {
            int b = y >> 6;
            int valid = maxBaseY - b;
            if (valid <= 0) new Span<ushort>(t + c * 64, bh).Fill(fill);
            else HighbdDrRun(t + c * 64, left + b, bh, Math.Min(valid, bh), (y & 0x3F) >> 1, fill);
        }
        if ((bw & 7) == 0 && (bh & 7) == 0)
        {
            for (int c0 = 0; c0 < bw; c0 += 8)
                for (int r0 = 0; r0 < bh; r0 += 8)
                    Transpose8x8U16(t + c0 * 64 + r0, 64, dst + r0 * stride + c0, stride);
        }
        else
        {
            for (int r = 0; r < bh; ++r)
                for (int c = 0; c < bw; ++c) dst[r * stride + c] = t[c * 64 + r];
        }
    }

    /// <summary>Zone 2 without upsampling: each row's above-edge suffix as one run, the left-edge prefix gathered.</summary>
    internal static void HighbdDrPredictionZ2Simd(ushort* dst, nint stride, int bw, int bh, ushort* above, ushort* left, int dx, int dy)
    {
        for (int r = 0; r < bh; ++r, dst += stride)
        {
            int t = -(r + 1) * dx;
            int baseOff = t >> 6;
            int c0 = Math.Max(0, -1 - baseOff);
            int nl = Math.Min(c0, bw);
            for (int c = 0; c < nl; c += 8)
            {
                var yy = Vector256.Create(r << 6) - (Vector256.Create(c + 1) + Lane8) * Vector256.Create(dy);
                var baseY = Vector256.Max(Vector256.ShiftRightArithmetic(yy, 6), Vector256.Create(-1));
                var shift = Vector256.ShiftRightLogical(yy & Vector256.Create(0x3F), 1);
                var g = Avx2.GatherVector256((int*)left, baseY, 2);
                var l0 = g & Vector256.Create(0xFFFF);
                var l1 = Vector256.ShiftRightLogical(g, 16);
                var v = Vector256.ShiftRightLogical(l0 * (Vector256.Create(32) - shift) + l1 * shift + Vector256.Create(16), 5);
                var w16 = Sse41.PackUnsignedSaturate(v.GetLower(), v.GetUpper());
                int cnt = Math.Min(8, nl - c);
                if (cnt == 8) Sse2.Store(dst + c, w16);
                else Unsafe.CopyBlockUnaligned(dst + c, &w16, (uint)(cnt * 2));
            }
            if (c0 < bw) HighbdDrRun(dst + c0, above + baseOff + c0, bw - c0, bw - c0, (t & 0x3F) >> 1, 0);
        }
    }

    /// <summary>dst (8 rows of 8) = the transpose of the 8 x 8 16-bit samples at src.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8U16(ushort* src, nint srcStride, ushort* dst, nint dstStride)
    {
        var r0 = Sse2.LoadVector128(src); var r1 = Sse2.LoadVector128(src + srcStride);
        var r2 = Sse2.LoadVector128(src + 2 * srcStride); var r3 = Sse2.LoadVector128(src + 3 * srcStride);
        var r4 = Sse2.LoadVector128(src + 4 * srcStride); var r5 = Sse2.LoadVector128(src + 5 * srcStride);
        var r6 = Sse2.LoadVector128(src + 6 * srcStride); var r7 = Sse2.LoadVector128(src + 7 * srcStride);
        var a0 = Sse2.UnpackLow(r0, r1).AsUInt32(); var a1 = Sse2.UnpackHigh(r0, r1).AsUInt32();
        var a2 = Sse2.UnpackLow(r2, r3).AsUInt32(); var a3 = Sse2.UnpackHigh(r2, r3).AsUInt32();
        var a4 = Sse2.UnpackLow(r4, r5).AsUInt32(); var a5 = Sse2.UnpackHigh(r4, r5).AsUInt32();
        var a6 = Sse2.UnpackLow(r6, r7).AsUInt32(); var a7 = Sse2.UnpackHigh(r6, r7).AsUInt32();
        var b0 = Sse2.UnpackLow(a0, a2).AsUInt64(); var b1 = Sse2.UnpackHigh(a0, a2).AsUInt64();
        var b2 = Sse2.UnpackLow(a1, a3).AsUInt64(); var b3 = Sse2.UnpackHigh(a1, a3).AsUInt64();
        var b4 = Sse2.UnpackLow(a4, a6).AsUInt64(); var b5 = Sse2.UnpackHigh(a4, a6).AsUInt64();
        var b6 = Sse2.UnpackLow(a5, a7).AsUInt64(); var b7 = Sse2.UnpackHigh(a5, a7).AsUInt64();
        Sse2.Store(dst, Sse2.UnpackLow(b0, b4).AsUInt16()); Sse2.Store(dst + dstStride, Sse2.UnpackHigh(b0, b4).AsUInt16());
        Sse2.Store(dst + 2 * dstStride, Sse2.UnpackLow(b1, b5).AsUInt16()); Sse2.Store(dst + 3 * dstStride, Sse2.UnpackHigh(b1, b5).AsUInt16());
        Sse2.Store(dst + 4 * dstStride, Sse2.UnpackLow(b2, b6).AsUInt16()); Sse2.Store(dst + 5 * dstStride, Sse2.UnpackHigh(b2, b6).AsUInt16());
        Sse2.Store(dst + 6 * dstStride, Sse2.UnpackLow(b3, b7).AsUInt16()); Sse2.Store(dst + 7 * dstStride, Sse2.UnpackHigh(b3, b7).AsUInt16());
    }
}
