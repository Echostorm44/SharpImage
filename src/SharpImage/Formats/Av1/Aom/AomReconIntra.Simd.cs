using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// The directional predictors without edge upsampling in 16-sample vectors (libaom dispatches its AVX2 dr_prediction_z*
// kernels here): every predicted sample is the same (a * (32 - shift) + b * shift + 16) >> 5 of two edge samples as the
// scalar code, a row of zone 1 / a column of zone 3 / the above-edge part of a zone-2 row being two contiguous edge runs
// with one shift. Edge reads stay inside the NUM_INTRA_NEIGHBOUR_PIXELS buffers (the run starts at most at
// max_base - 1, plus 16).
internal static unsafe partial class AomReconIntra
{
    private static readonly Vector256<int> Lane8 = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
    private static readonly Vector128<byte> Iota16 = Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

    /// <summary>count samples dst[c] = interpolate(src[c], src[c + 1], shift) for c &lt; n, fill after.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DrRun(byte* dst, byte* src, int count, int n, int shift, byte fill)
    {
        var w0 = Vector256.Create((short)(32 - shift));
        var w1 = Vector256.Create((short)shift);
        var r16 = Vector256.Create((short)16);
        var fillV = Vector128.Create(fill);
        for (int c = 0; c < count; c += 16)
        {
            Vector128<byte> res;
            if (c >= n) res = fillV;
            else
            {
                var a = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(src + c));
                var b = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(src + c + 1));
                var v = Vector256.ShiftRightLogical(a * w0 + b * w1 + r16, 5);
                res = Sse2.PackUnsignedSaturate(v.GetLower(), v.GetUpper());
                if (n - c < 16) res = Vector128.ConditionalSelect(Vector128.LessThan(Iota16, Vector128.Create((byte)(n - c))), res, fillV);
            }
            int rem = count - c;
            if (rem >= 16) Sse2.Store(dst + c, res);
            else if (rem == 8) *(ulong*)(dst + c) = res.AsUInt64().ToScalar();
            else if (rem == 4) *(uint*)(dst + c) = res.AsUInt32().ToScalar();
            else Unsafe.CopyBlockUnaligned(dst + c, &res, (uint)rem);
        }
    }

    /// <summary>Zone 1 without upsampling.</summary>
    internal static void DrPredictionZ1Simd(byte* dst, nint stride, int bw, int bh, byte* above, int dx)
    {
        if (bw == 32) { DrPredictionZ1_32Avx2(dst, stride, bh, above, dx); return; }
        int maxBaseX = bw + bh - 1;
        byte fill = above[maxBaseX];
        int x = dx;
        for (int r = 0; r < bh; ++r, dst += stride, x += dx)
        {
            int b = x >> 6;
            int valid = maxBaseX - b;
            if (valid <= 0)
            {
                for (int i = r; i < bh; ++i, dst += stride) Unsafe.InitBlockUnaligned(dst, fill, (uint)bw);
                return;
            }
            DrRun(dst, above + b, bw, Math.Min(valid, bw), (x & 0x3F) >> 1, fill);
        }
    }

    /// <summary>Zone 3 without upsampling: zone 1 down the left edge, one column per run, transposed.</summary>
    internal static void DrPredictionZ3Simd(byte* dst, nint stride, int bw, int bh, byte* left, int dy)
    {
        if (bh <= 16) { DrPredictionZ3SmallAvx2(dst, stride, bw, bh, left, dy); return; }
        if (bh == 32 && bw <= 32) { DrPredictionZ3Tall32Avx2(dst, stride, bw, left, dy); return; }
        int maxBaseY = bw + bh - 1;
        byte fill = left[maxBaseY];
        Unsafe.SkipInit(out StackArr4096<byte> tSA); byte* t = (byte*)Unsafe.AsPointer(ref tSA[0]);   // column c at t + c * 64
        int y = dy;
        for (int c = 0; c < bw; ++c, y += dy)
        {
            int b = y >> 6;
            int valid = maxBaseY - b;
            if (valid <= 0) Unsafe.InitBlockUnaligned(t + c * 64, fill, (uint)bh);
            else DrRun(t + c * 64, left + b, bh, Math.Min(valid, bh), (y & 0x3F) >> 1, fill);
        }
        if ((bw & 7) == 0 && (bh & 7) == 0)
        {
            for (int c0 = 0; c0 < bw; c0 += 8)
                for (int r0 = 0; r0 < bh; r0 += 8)
                    Transpose8x8Bytes(t + c0 * 64 + r0, 64, dst + r0 * stride + c0, stride);
        }
        else
        {
            for (int r = 0; r < bh; ++r)
                for (int c = 0; c < bw; ++c) dst[r * stride + c] = t[c * 64 + r];
        }
    }

    /// <summary>Zone 2 without upsampling: each row's above-edge suffix as one run, the left-edge prefix per sample.</summary>
    internal static void DrPredictionZ2Simd(byte* dst, nint stride, int bw, int bh, byte* above, byte* left, int dx, int dy)
    {
        if (bw >= 16) { DrPredictionZ2HxWAvx2(dst, stride, bw, bh, above, left, dx, dy); return; }
        for (int r = 0; r < bh; ++r, dst += stride)
        {
            int t = -(r + 1) * dx;
            int baseOff = t >> 6;
            // base_x = c + baseOff >= min_base_x (-1) from column c0 on
            int c0 = Math.Max(0, -1 - baseOff);
            int nl = Math.Min(c0, bw);
            for (int c = 0; c < nl; c += 8)
            {
                // columns c .. c + 7: y = (r << 6) - (c + 1) * dy, two left samples gathered per lane (lanes past the
                // prefix, whose base would run below the edge, clamped to it and not stored)
                var yy = Vector256.Create(r << 6) - (Vector256.Create(c + 1) + Lane8) * Vector256.Create(dy);
                var baseY = Vector256.Max(Vector256.ShiftRightArithmetic(yy, 6), Vector256.Create(-1));
                var shift = Vector256.ShiftRightLogical(yy & Vector256.Create(0x3F), 1);
                var g = Avx2.GatherVector256((int*)left, baseY, 1);
                var l0 = g & Vector256.Create(0xFF);
                var l1 = Vector256.ShiftRightLogical(g, 8) & Vector256.Create(0xFF);
                var v = Vector256.ShiftRightLogical(l0 * (Vector256.Create(32) - shift) + l1 * shift + Vector256.Create(16), 5);
                var w16 = Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper());
                var b8 = Sse2.PackUnsignedSaturate(w16, w16);
                int cnt = Math.Min(8, nl - c);
                if (cnt == 8) *(ulong*)(dst + c) = b8.AsUInt64().ToScalar();
                else Unsafe.CopyBlockUnaligned(dst + c, &b8, (uint)cnt);
            }
            if (c0 < bw) DrRun(dst + c0, above + baseOff + c0, bw - c0, bw - c0, (t & 0x3F) >> 1, 0);
        }
    }

    // (a * (32 - shift) + b * shift + 16) >> 5 per lane from 8 gathered edge pairs (the low two bytes of each lane)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> DrInterp(byte* edge, Vector256<int> idx, Vector256<int> shift)
    {
        var g = Avx2.GatherVector256((int*)edge, idx, 1);
        var e0 = g & Vector256.Create(0xFF);
        var e1 = Vector256.ShiftRightLogical(g, 8) & Vector256.Create(0xFF);
        return Vector256.ShiftRightLogical(e0 * (Vector256.Create(32) - shift) + e1 * shift + Vector256.Create(16), 5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreLanes(byte* dst, Vector256<int> v, int n)
    {
        var w16 = Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper());
        var b8 = Sse2.PackUnsignedSaturate(w16, w16);
        if (n == 8) *(ulong*)dst = b8.AsUInt64().ToScalar();
        else *(uint*)dst = b8.AsUInt32().ToScalar();
    }

    /// <summary>Zone 1 with an upsampled above edge (bw, bh &lt;= 8 there), lane per column: DrPredictionZ1's samples.</summary>
    internal static void DrPredictionZ1UpSimd(byte* dst, nint stride, int bw, int bh, byte* above, int dx)
    {
        const int upsampleAbove = 1;
        int maxBaseX = ((bw + bh) - 1) << upsampleAbove;
        const int fracBits = 6 - upsampleAbove;
        byte fill = above[maxBaseX];
        var fillV = Vector256.Create((int)fill);
        int x = dx;
        for (int r = 0; r < bh; ++r, dst += stride, x += dx)
        {
            int b = x >> fracBits;
            int valid = (maxBaseX - b) >> upsampleAbove;
            if (valid <= 0)
            {
                for (int i = r; i < bh; ++i, dst += stride) Unsafe.InitBlockUnaligned(dst, fill, (uint)bw);
                return;
            }
            int shift = ((x << upsampleAbove) & 0x3F) >> 1;
            // lanes past min(valid, bw) take the fill; their (clamped) edge reads are discarded
            var idx = Vector256.Min(Vector256.Create(b) + Lane8 * 2, Vector256.Create(maxBaseX));
            var v = DrInterp(above, idx, Vector256.Create(shift));
            v = Vector256.ConditionalSelect(Vector256.LessThan(Lane8, Vector256.Create(valid)), v, fillV);
            StoreLanes(dst, v, bw);
        }
    }

    /// <summary>Zone 3 with an upsampled left edge (bw, bh &lt;= 8): zone 1 down the left edge per column, transposed.</summary>
    internal static void DrPredictionZ3UpSimd(byte* dst, nint stride, int bw, int bh, byte* left, int dy)
    {
        const int upsampleLeft = 1;
        int maxBaseY = (bw + bh - 1) << upsampleLeft;
        const int fracBits = 6 - upsampleLeft;
        byte fill = left[maxBaseY];
        var fillV = Vector256.Create((int)fill);
        Unsafe.SkipInit(out StackArr8<ulong> tSA); ulong* t = (ulong*)Unsafe.AsPointer(ref tSA[0]);   // column c's rows at t[c]
        int y = dy;
        for (int c = 0; c < bw; ++c, y += dy)
        {
            int b = y >> fracBits;
            int valid = (maxBaseY - b) >> upsampleLeft;
            int shift = ((y << upsampleLeft) & 0x3F) >> 1;
            var idx = Vector256.Min(Vector256.Max(Vector256.Create(b) + Lane8 * 2, Vector256<int>.Zero), Vector256.Create(maxBaseY));
            var v = valid <= 0 ? fillV : Vector256.ConditionalSelect(Vector256.LessThan(Lane8, Vector256.Create(valid)), DrInterp(left, idx, Vector256.Create(shift)), fillV);
            var w16 = Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper());
            t[c] = Sse2.PackUnsignedSaturate(w16, w16).AsUInt64().ToScalar();
        }
        byte* tb = (byte*)t;
        for (int r = 0; r < bh; ++r, dst += stride)
            for (int c = 0; c < bw; ++c) dst[c] = tb[c * 8 + r];
    }

    /// <summary>Zone 2 with either edge upsampled (bw &lt;= 8): per lane the above sample when its base reaches the above
    /// edge, else the left one, as av1_dr_prediction_z2_c.</summary>
    internal static void DrPredictionZ2UpSimd(byte* dst, nint stride, int bw, int bh, byte* above, byte* left,
        int upsampleAbove, int upsampleLeft, int dx, int dy)
    {
        int minBaseX = -(1 << upsampleAbove);
        int fracBitsX = 6 - upsampleAbove, fracBitsY = 6 - upsampleLeft;
        var c6 = Lane8 * 64;            // c << 6
        var c1dy = (Lane8 + Vector256.Create(1)) * Vector256.Create(dy);
        var m3f = Vector256.Create(0x3F);
        for (int r = 0; r < bh; ++r, dst += stride)
        {
            var xv = c6 - Vector256.Create((r + 1) * dx);
            var baseX = Vector256.ShiftRightArithmetic(xv, fracBitsX);
            var useAbove = Vector256.GreaterThanOrEqual(baseX, Vector256.Create(minBaseX));
            var sA = Vector256.ShiftRightLogical(Vector256.ShiftLeft(xv, upsampleAbove) & m3f, 1);
            var a = DrInterp(above, Vector256.Max(baseX, Vector256.Create(minBaseX)), sA);
            var yv = Vector256.Create(r << 6) - c1dy;
            var baseY = Vector256.ShiftRightArithmetic(yv, fracBitsY);
            var sL = Vector256.ShiftRightLogical(Vector256.ShiftLeft(yv, upsampleLeft) & m3f, 1);
            var l = DrInterp(left, Vector256.Max(baseY, Vector256.Create(-2)), sL);
            StoreLanes(dst, Vector256.ConditionalSelect(useAbove, a, l), bw);
        }
    }

    /// <summary>dst (8 rows of 8) = the transpose of the 8 x 8 bytes at src.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8Bytes(byte* src, nint srcStride, byte* dst, nint dstStride)
    {
        var r0 = Vector128.CreateScalar(*(ulong*)src).AsByte();
        var r1 = Vector128.CreateScalar(*(ulong*)(src + srcStride)).AsByte();
        var r2 = Vector128.CreateScalar(*(ulong*)(src + 2 * srcStride)).AsByte();
        var r3 = Vector128.CreateScalar(*(ulong*)(src + 3 * srcStride)).AsByte();
        var r4 = Vector128.CreateScalar(*(ulong*)(src + 4 * srcStride)).AsByte();
        var r5 = Vector128.CreateScalar(*(ulong*)(src + 5 * srcStride)).AsByte();
        var r6 = Vector128.CreateScalar(*(ulong*)(src + 6 * srcStride)).AsByte();
        var r7 = Vector128.CreateScalar(*(ulong*)(src + 7 * srcStride)).AsByte();
        var a0 = Sse2.UnpackLow(r0, r1).AsUInt16(); var a1 = Sse2.UnpackLow(r2, r3).AsUInt16();
        var a2 = Sse2.UnpackLow(r4, r5).AsUInt16(); var a3 = Sse2.UnpackLow(r6, r7).AsUInt16();
        var b0 = Sse2.UnpackLow(a0, a1).AsUInt32(); var b1 = Sse2.UnpackHigh(a0, a1).AsUInt32();
        var b2 = Sse2.UnpackLow(a2, a3).AsUInt32(); var b3 = Sse2.UnpackHigh(a2, a3).AsUInt32();
        var c0 = Sse2.UnpackLow(b0, b2).AsUInt64(); var c1 = Sse2.UnpackHigh(b0, b2).AsUInt64();
        var c2 = Sse2.UnpackLow(b1, b3).AsUInt64(); var c3 = Sse2.UnpackHigh(b1, b3).AsUInt64();
        *(ulong*)dst = c0.GetElement(0); *(ulong*)(dst + dstStride) = c0.GetElement(1);
        *(ulong*)(dst + 2 * dstStride) = c1.GetElement(0); *(ulong*)(dst + 3 * dstStride) = c1.GetElement(1);
        *(ulong*)(dst + 4 * dstStride) = c2.GetElement(0); *(ulong*)(dst + 5 * dstStride) = c2.GetElement(1);
        *(ulong*)(dst + 6 * dstStride) = c3.GetElement(0); *(ulong*)(dst + 7 * dstStride) = c3.GetElement(1);
    }
}
