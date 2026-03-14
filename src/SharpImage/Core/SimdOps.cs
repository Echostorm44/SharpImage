using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Core;

/// <summary>
/// SIMD-accelerated pixel operations for hot-path processing. All methods operate on contiguous ushort spans (our 16-
/// bit quantum pixel data). Automatically selects best available instruction set: AVX2 (256-bit) → SSE2 (128-bit) →
/// scalar.
/// </summary>
public static class SimdOps
{
    /// <summary>
    /// Accumulates weighted sum: for each element, accum[i] += weight * source[i]. Used in convolution and resize
    /// kernels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AccumulateWeighted(ReadOnlySpan<ushort> source, Span<float> accum, float weight)
    {
        int length = Math.Min(source.Length, accum.Length);
        int i = 0;

        if (Avx2.IsSupported && length >= 16)
        {
            var vWeight = Vector256.Create(weight);
            ref ushort srcRef = ref MemoryMarshal.GetReference(source);
            ref float accRef = ref MemoryMarshal.GetReference(accum);

            for (;i + 15 < length;i += 16)
            {
                // Load 16 ushorts, convert to two groups of 8 floats
                var src16 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var lo = Avx2.UnpackLow(src16, Vector256<ushort>.Zero);
                var hi = Avx2.UnpackHigh(src16, Vector256<ushort>.Zero);

                // Reinterpret as uint and convert to float
                var loF = Avx.ConvertToVector256Single(lo.AsInt32());
                var hiF = Avx.ConvertToVector256Single(hi.AsInt32());

                // Load existing accumulators
                var acc0 = Vector256.LoadUnsafe(ref accRef, (nuint)i);
                var acc1 = Vector256.LoadUnsafe(ref accRef, (nuint)(i + 8));

                // FMA: accum += weight * source
                if (Fma.IsSupported)
                {
                    acc0 = Fma.MultiplyAdd(loF, vWeight, acc0);
                    acc1 = Fma.MultiplyAdd(hiF, vWeight, acc1);
                }
                else
                {
                    acc0 = Avx.Add(acc0, Avx.Multiply(loF, vWeight));
                    acc1 = Avx.Add(acc1, Avx.Multiply(hiF, vWeight));
                }

                acc0.StoreUnsafe(ref accRef, (nuint)i);
                acc1.StoreUnsafe(ref accRef, (nuint)(i + 8));
            }
        }
        else if (Sse2.IsSupported && length >= 8)
        {
            var vWeight = Vector128.Create(weight);
            ref ushort srcRef = ref MemoryMarshal.GetReference(source);
            ref float accRef = ref MemoryMarshal.GetReference(accum);

            for (;i + 7 < length;i += 8)
            {
                var src8 = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                var lo = Sse2.UnpackLow(src8, Vector128<ushort>.Zero);
                var hi = Sse2.UnpackHigh(src8, Vector128<ushort>.Zero);

                var loF = Sse2.ConvertToVector128Single(lo.AsInt32());
                var hiF = Sse2.ConvertToVector128Single(hi.AsInt32());

                var acc0 = Vector128.LoadUnsafe(ref accRef, (nuint)i);
                var acc1 = Vector128.LoadUnsafe(ref accRef, (nuint)(i + 4));

                acc0 = Sse.Add(acc0, Sse.Multiply(loF, vWeight));
                acc1 = Sse.Add(acc1, Sse.Multiply(hiF, vWeight));

                acc0.StoreUnsafe(ref accRef, (nuint)i);
                acc1.StoreUnsafe(ref accRef, (nuint)(i + 4));
            }
        }

        // Scalar tail
        for (;i < length;i++)
        {
            accum[i] += weight * source[i];
        }
    }

    /// <summary>
    /// Normalizes float accumulators and writes clamped ushort output. dst[i] = clamp(accum[i] * invWeight + 0.5, 0,
    /// 65535)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NormalizeAndClamp(ReadOnlySpan<float> accum, Span<ushort> dest, float invWeight)
    {
        int length = Math.Min(accum.Length, dest.Length);
        int i = 0;

        if (Avx2.IsSupported && length >= 16)
        {
            var vInvW = Vector256.Create(invWeight);
            var vHalf = Vector256.Create(0.5f);
            var vMax = Vector256.Create((float)ushort.MaxValue);
            var vZero = Vector256<float>.Zero;
            ref float accRef = ref MemoryMarshal.GetReference(accum);
            ref ushort dstRef = ref MemoryMarshal.GetReference(dest);

            for (;i + 15 < length;i += 16)
            {
                var a0 = Vector256.LoadUnsafe(ref accRef, (nuint)i);
                var a1 = Vector256.LoadUnsafe(ref accRef, (nuint)(i + 8));

                // val = clamp(accum * invWeight + 0.5, 0, 65535)
                if (Fma.IsSupported)
                {
                    a0 = Fma.MultiplyAdd(a0, vInvW, vHalf);
                    a1 = Fma.MultiplyAdd(a1, vInvW, vHalf);
                }
                else
                {
                    a0 = Avx.Add(Avx.Multiply(a0, vInvW), vHalf);
                    a1 = Avx.Add(Avx.Multiply(a1, vInvW), vHalf);
                }

                a0 = Avx.Min(Avx.Max(a0, vZero), vMax);
                a1 = Avx.Min(Avx.Max(a1, vZero), vMax);

                // Convert float→int→ushort
                var i0 = Avx.ConvertToVector256Int32(a0);
                var i1 = Avx.ConvertToVector256Int32(a1);
                var packed = Avx2.PackUnsignedSaturate(i0, i1);
                // PackUnsignedSaturate interleaves lanes, fix with permute
                packed = Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsUInt16();
                packed.StoreUnsafe(ref dstRef, (nuint)i);
            }
        }
        else if (Sse41.IsSupported && length >= 8)
        {
            var vInvW = Vector128.Create(invWeight);
            var vHalf = Vector128.Create(0.5f);
            var vMax = Vector128.Create((float)ushort.MaxValue);
            var vZero = Vector128<float>.Zero;
            ref float accRef = ref MemoryMarshal.GetReference(accum);
            ref ushort dstRef = ref MemoryMarshal.GetReference(dest);

            for (;i + 7 < length;i += 8)
            {
                var a0 = Vector128.LoadUnsafe(ref accRef, (nuint)i);
                var a1 = Vector128.LoadUnsafe(ref accRef, (nuint)(i + 4));

                a0 = Sse.Add(Sse.Multiply(a0, vInvW), vHalf);
                a1 = Sse.Add(Sse.Multiply(a1, vInvW), vHalf);

                a0 = Sse.Min(Sse.Max(a0, vZero), vMax);
                a1 = Sse.Min(Sse.Max(a1, vZero), vMax);

                var i0 = Sse2.ConvertToVector128Int32(a0);
                var i1 = Sse2.ConvertToVector128Int32(a1);
                // SSE4.1 PackUS on int32 → ushort
                var packed = Sse41.PackUnsignedSaturate(i0, i1);
                packed.StoreUnsafe(ref dstRef, (nuint)i);
            }
        }

        // Scalar tail
        for (;i < length;i++)
        {
            float val = accum[i] * invWeight + 0.5f;
            dest[i] = (ushort)Math.Clamp(val, 0, ushort.MaxValue);
        }
    }

    /// <summary>
    /// Computes sum of squared differences between two ushort spans. Returns sum as double for precision. Used in MSE
    /// calculation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SumSquaredDifferences(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b,
        int channelsA, int channelsB, int channels, int width)
    {
        double sum = 0;

        // If both have same channel count and it's contiguous, use fast path
        if (channelsA == channelsB && channelsA == channels)
        {
            int total = width * channels;
            int i = 0;

            if (Avx2.IsSupported && total >= 16)
            {
                ref ushort aRef = ref MemoryMarshal.GetReference(a);
                ref ushort bRef = ref MemoryMarshal.GetReference(b);
                var vSum0 = Vector256<double>.Zero;
                var vSum1 = Vector256<double>.Zero;

                for (;i + 15 < total;i += 16)
                {
                    var va = Vector256.LoadUnsafe(ref aRef, (nuint)i);
                    var vb = Vector256.LoadUnsafe(ref bRef, (nuint)i);

                    // Unpack to int32 and compute difference
                    var aLo = Avx2.UnpackLow(va, Vector256<ushort>.Zero).AsInt32();
                    var aHi = Avx2.UnpackHigh(va, Vector256<ushort>.Zero).AsInt32();
                    var bLo = Avx2.UnpackLow(vb, Vector256<ushort>.Zero).AsInt32();
                    var bHi = Avx2.UnpackHigh(vb, Vector256<ushort>.Zero).AsInt32();

                    var diffLo = Avx2.Subtract(aLo, bLo);
                    var diffHi = Avx2.Subtract(aHi, bHi);

                    // Square: multiply int32 as float for precision
                    var fLo = Avx.ConvertToVector256Single(diffLo);
                    var fHi = Avx.ConvertToVector256Single(diffHi);
                    var sqLo = Avx.Multiply(fLo, fLo);
                    var sqHi = Avx.Multiply(fHi, fHi);

                    // Accumulate into double (sum pairs of floats)
                    var dLo0 = Avx.ConvertToVector256Double(sqLo.GetLower());
                    var dLo1 = Avx.ConvertToVector256Double(sqLo.GetUpper());
                    var dHi0 = Avx.ConvertToVector256Double(sqHi.GetLower());
                    var dHi1 = Avx.ConvertToVector256Double(sqHi.GetUpper());

                    vSum0 = Avx.Add(vSum0, Avx.Add(dLo0, dLo1));
                    vSum1 = Avx.Add(vSum1, Avx.Add(dHi0, dHi1));
                }

                // Horizontal sum
                var combined = Avx.Add(vSum0, vSum1);
                sum = combined[0] + combined[1] + combined[2] + combined[3];
            }

            for (;i < total;i++)
            {
                double diff = (double)a[i] - b[i];
                sum += diff * diff;
            }
        }
        else
        {
            // Slow path for different channel counts
            for (int x = 0;x < width;x++)
            {
                for (int c = 0;c < channels;c++)
                {
                    double va = a[x * channelsA + c];
                    double vb = b[x * channelsB + c];
                    double diff = va - vb;
                    sum += diff * diff;
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// Copies channels from source row to destination row using SIMD when possible. Handles same-stride memcpy-style
    /// copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyRow(ReadOnlySpan<ushort> source, Span<ushort> dest, int count)
    {
        source[..count].CopyTo(dest);
    }

    /// <summary>
    /// Applies unsharp mask: result[i] = clamp(original[i] + amount * (original[i] - blurred[i]))
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnsharpMask(ReadOnlySpan<ushort> original, ReadOnlySpan<ushort> blurred,
        Span<ushort> result, float amount, int count)
    {
        int i = 0;

        if (Avx2.IsSupported && count >= 16)
        {
            var vAmount = Vector256.Create(amount);
            var vHalf = Vector256.Create(0.5f);
            var vMax = Vector256.Create((float)ushort.MaxValue);
            var vZero = Vector256<float>.Zero;
            ref ushort origRef = ref MemoryMarshal.GetReference(original);
            ref ushort blurRef = ref MemoryMarshal.GetReference(blurred);
            ref ushort resRef = ref MemoryMarshal.GetReference(result);

            for (;i + 15 < count;i += 16)
            {
                var vOrig = Vector256.LoadUnsafe(ref origRef, (nuint)i);
                var vBlur = Vector256.LoadUnsafe(ref blurRef, (nuint)i);

                // Process low 8 and high 8 separately (ushort→float)
                var origLo = Avx.ConvertToVector256Single(Avx2.UnpackLow(vOrig, Vector256<ushort>.Zero).AsInt32());
                var origHi = Avx.ConvertToVector256Single(Avx2.UnpackHigh(vOrig, Vector256<ushort>.Zero).AsInt32());
                var blurLo = Avx.ConvertToVector256Single(Avx2.UnpackLow(vBlur, Vector256<ushort>.Zero).AsInt32());
                var blurHi = Avx.ConvertToVector256Single(Avx2.UnpackHigh(vBlur, Vector256<ushort>.Zero).AsInt32());

                // result = original + amount * (original - blurred) + 0.5
                var diffLo = Avx.Subtract(origLo, blurLo);
                var diffHi = Avx.Subtract(origHi, blurHi);
                Vector256<float> resLo, resHi;
                if (Fma.IsSupported)
                {
                    resLo = Avx.Add(Fma.MultiplyAdd(diffLo, vAmount, origLo), vHalf);
                    resHi = Avx.Add(Fma.MultiplyAdd(diffHi, vAmount, origHi), vHalf);
                }
                else
                {
                    resLo = Avx.Add(Avx.Add(origLo, Avx.Multiply(diffLo, vAmount)), vHalf);
                    resHi = Avx.Add(Avx.Add(origHi, Avx.Multiply(diffHi, vAmount)), vHalf);
                }

                resLo = Avx.Min(Avx.Max(resLo, vZero), vMax);
                resHi = Avx.Min(Avx.Max(resHi, vZero), vMax);

                var iLo = Avx.ConvertToVector256Int32(resLo);
                var iHi = Avx.ConvertToVector256Int32(resHi);
                var packed = Avx2.PackUnsignedSaturate(iLo, iHi);
                packed = Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsUInt16();
                packed.StoreUnsafe(ref resRef, (nuint)i);
            }
        }

        for (;i < count;i++)
        {
            float orig = original[i];
            float blur = blurred[i];
            float val = orig + amount * (orig - blur) + 0.5f;
            result[i] = (ushort)Math.Clamp(val, 0, ushort.MaxValue);
        }
    }

    /// <summary>
    /// Extracts BT.709 luminance from interleaved RGB(A) pixel row. Writes normalized [0..1] doubles to luminance span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExtractLuminanceRow(ReadOnlySpan<ushort> row, Span<double> luminance,
        int width, int channels)
    {
        const double scale = 1.0 / 65535.0;
        if (channels >= 3)
        {
            int x = 0;

            if (Avx2.IsSupported && channels == 3 && width >= 8)
            {
                var vScale = Vector256.Create((float)scale);
                var vR = Vector256.Create(0.2126f);
                var vG = Vector256.Create(0.7152f);
                var vB = Vector256.Create(0.0722f);
                ref double lumRef = ref MemoryMarshal.GetReference(luminance);

                // Process 8 pixels at a time: deinterleave RGB, compute weighted sum
                Span<float> rBuf = stackalloc float[8];
                Span<float> gBuf = stackalloc float[8];
                Span<float> bBuf = stackalloc float[8];
                for (; x + 7 < width; x += 8)
                {
                    int baseOff = x * 3;

                    for (int i = 0; i < 8; i++)
                    {
                        int off = baseOff + i * 3;
                        rBuf[i] = row[off];
                        gBuf[i] = row[off + 1];
                        bBuf[i] = row[off + 2];
                    }

                    var vRvals = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(rBuf));
                    var vGvals = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(gBuf));
                    var vBvals = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(bBuf));

                    Vector256<float> lum;
                    if (Fma.IsSupported)
                    {
                        lum = Fma.MultiplyAdd(vRvals, vR, Fma.MultiplyAdd(vGvals, vG, Avx.Multiply(vBvals, vB)));
                    }
                    else
                    {
                        lum = Avx.Add(Avx.Add(Avx.Multiply(vRvals, vR), Avx.Multiply(vGvals, vG)), Avx.Multiply(vBvals, vB));
                    }
                    lum = Avx.Multiply(lum, vScale);

                    var dLo = Avx.ConvertToVector256Double(lum.GetLower());
                    var dHi = Avx.ConvertToVector256Double(lum.GetUpper());
                    dLo.StoreUnsafe(ref lumRef, (nuint)x);
                    dHi.StoreUnsafe(ref lumRef, (nuint)(x + 4));
                }
            }

            for (; x < width; x++)
            {
                int off = x * channels;
                luminance[x] = 0.2126 * row[off] * scale +
                               0.7152 * row[off + 1] * scale +
                               0.0722 * row[off + 2] * scale;
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                luminance[x] = row[x * channels] * scale;
            }
        }
    }

    /// <summary>
    /// Applies linear stretch to a pixel row: dst[i] = clamp((src[i] - min) * scale, 0, maxVal).
    /// Used by Normalize, AutoLevel, and similar enhancement operations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LinearStretchRow(ReadOnlySpan<ushort> src, Span<ushort> dst,
        int colorChannels, int totalChannels, int width, float min, float scale)
    {
        int total = width * totalChannels;
        int i = 0;

        if (Avx2.IsSupported && totalChannels == colorChannels && total >= 16)
        {
            var vMin = Vector256.Create(min);
            var vScale = Vector256.Create(scale);
            var vHalf = Vector256.Create(0.5f);
            var vMax = Vector256.Create((float)ushort.MaxValue);
            var vZero = Vector256<float>.Zero;
            ref ushort srcRef = ref MemoryMarshal.GetReference(src);
            ref ushort dstRef = ref MemoryMarshal.GetReference(dst);

            for (; i + 15 < total; i += 16)
            {
                var s16 = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                var lo = Avx.ConvertToVector256Single(Avx2.UnpackLow(s16, Vector256<ushort>.Zero).AsInt32());
                var hi = Avx.ConvertToVector256Single(Avx2.UnpackHigh(s16, Vector256<ushort>.Zero).AsInt32());

                // (src - min) * scale + 0.5
                Vector256<float> rLo, rHi;
                if (Fma.IsSupported)
                {
                    rLo = Fma.MultiplyAdd(Avx.Subtract(lo, vMin), vScale, vHalf);
                    rHi = Fma.MultiplyAdd(Avx.Subtract(hi, vMin), vScale, vHalf);
                }
                else
                {
                    rLo = Avx.Add(Avx.Multiply(Avx.Subtract(lo, vMin), vScale), vHalf);
                    rHi = Avx.Add(Avx.Multiply(Avx.Subtract(hi, vMin), vScale), vHalf);
                }

                rLo = Avx.Min(Avx.Max(rLo, vZero), vMax);
                rHi = Avx.Min(Avx.Max(rHi, vZero), vMax);

                var iLo = Avx.ConvertToVector256Int32(rLo);
                var iHi = Avx.ConvertToVector256Int32(rHi);
                var packed = Avx2.PackUnsignedSaturate(iLo, iHi);
                packed = Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsUInt16();
                packed.StoreUnsafe(ref dstRef, (nuint)i);
            }
        }

        for (; i < total; i++)
        {
            // Skip alpha channels in interleaved data
            if (totalChannels > colorChannels && (i % totalChannels) >= colorChannels)
            {
                dst[i] = src[i];
                continue;
            }
            float val = (src[i] - min) * scale + 0.5f;
            dst[i] = (ushort)Math.Clamp(val, 0, ushort.MaxValue);
        }
    }

    /// <summary>
    /// Applies a lookup table to color channels of a pixel row. Alpha is copied unchanged.
    /// Each channel has its own 256-entry LUT (indexed by ScaleToByte).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyLutRow(ReadOnlySpan<ushort> src, Span<ushort> dst,
        ushort[][] luts, int colorChannels, int totalChannels, int width)
    {
        for (int x = 0; x < width; x++)
        {
            int off = x * totalChannels;
            for (int c = 0; c < colorChannels; c++)
            {
                int bin = Quantum.ScaleToByte(src[off + c]);
                dst[off + c] = luts[c][bin];
            }
            // Copy alpha unchanged
            if (totalChannels > colorChannels)
                dst[off + colorChannels] = src[off + colorChannels];
        }
    }

}
