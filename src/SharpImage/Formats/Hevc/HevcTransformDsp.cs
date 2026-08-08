// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// HEVC/H.265 inverse transform DSP with SIMD acceleration.
/// Implements DCT-II (4/8/16/32) and DST-VII (4x4 intra) as 2-pass separable transforms.
/// Pass 2 inner dot products use SSE2 pmaddwd for speedup on sizes >= 8.
/// </summary>
public static class HevcTransformDsp
{
    // ──────────────────────────────────────────────
    //  Coefficient Tables (ITU-T H.265 Tables 8-6, 8-7)
    // ──────────────────────────────────────────────

    private static ReadOnlySpan<short> Dct4x4 => new short[]
    {
        64,  64,  64,  64,
        83,  36, -36, -83,
        64, -64, -64,  64,
        36, -83,  83, -36
    };

    private static ReadOnlySpan<short> Dst4x4 => new short[]
    {
        29,  55,  74,  84,
        74,  74,   0, -74,
        84, -29, -74,  55,
        55, -84,  74, -29
    };

    private static ReadOnlySpan<short> Dct8x8 => new short[]
    {
        64,  64,  64,  64,  64,  64,  64,  64,
        89,  75,  50,  18, -18, -50, -75, -89,
        83,  36, -36, -83, -83, -36,  36,  83,
        75, -18, -89, -50,  50,  89,  18, -75,
        64, -64, -64,  64,  64, -64, -64,  64,
        50, -89,  18,  75, -75, -18,  89, -50,
        36, -83,  83, -36, -36,  83, -83,  36,
        18, -50,  75, -89,  89, -75,  50, -18
    };

    // 16x16 and 32x32 tables accessed via delegation to HevcTransform (too large to duplicate).

    // ──────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────

    /// <summary>
    /// 4x4 inverse DCT-II (inter/skip blocks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransformDct4x4(
        ReadOnlySpan<short> coefficients, Span<short> output, int shift2 = 12, int add2 = 2048)
    {
        InverseTransformScalar(coefficients, output, Dct4x4, 4, shift2, add2);
    }

    /// <summary>
    /// 4x4 inverse DST-VII (intra blocks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransformDst4x4(
        ReadOnlySpan<short> coefficients, Span<short> output, int shift2 = 12, int add2 = 2048)
    {
        InverseTransformScalar(coefficients, output, Dst4x4, 4, shift2, add2);
    }

    /// <summary>
    /// 8x8 inverse DCT-II with SSE2-accelerated pass 2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransformDct8x8(
        ReadOnlySpan<short> coefficients, Span<short> output, int shift2 = 12, int add2 = 2048)
    {
        InverseTransform(coefficients, output, Dct8x8, 8, shift2, add2);
    }

    /// <summary>
    /// Generic NxN inverse transform — dispatches SIMD for pass 2 on sizes >= 8.
    /// Used for 16x16 and 32x32 where coefficient tables are passed from the codec.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform(
        ReadOnlySpan<short> coefficients, Span<short> output,
        ReadOnlySpan<short> matrix, int size, int shift2, int add2)
    {
        if (Sse2.IsSupported && size >= 8)
            InverseTransformSse2(coefficients, output, matrix, size, shift2, add2);
        else
            InverseTransformScalar(coefficients, output, matrix, size, shift2, add2);
    }

    // ──────────────────────────────────────────────
    //  Scalar Reference
    // ──────────────────────────────────────────────

    /// <summary>
    /// Scalar NxN inverse transform. Two-pass separable: column then row.
    /// </summary>
    public static void InverseTransformScalar(
        ReadOnlySpan<short> coefficients, Span<short> output,
        ReadOnlySpan<short> matrix, int size, int shift2, int add2)
    {
        int n2 = size * size;
        Span<int> temp = n2 <= 256 ? stackalloc int[n2] : new int[n2];

        // Pass 1: column transform
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int sum = 0;
                for (int k = 0; k < size; k++)
                    sum += matrix[k * size + i] * coefficients[k * size + j];
                temp[i * size + j] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Pass 2: row transform
        for (int i = 0; i < size; i++)
        {
            int rowOffset = i * size;
            for (int j = 0; j < size; j++)
            {
                int sum = 0;
                for (int k = 0; k < size; k++)
                    sum += matrix[k * size + j] * temp[rowOffset + k];
                output[rowOffset + j] = (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
            }
        }
    }

    // ──────────────────────────────────────────────
    //  SSE2 Accelerated (sizes >= 8)
    // ──────────────────────────────────────────────

    /// <summary>
    /// SSE2-accelerated NxN inverse transform.
    /// Pass 1 (column) is scalar (strided access pattern).
    /// Pass 2 (row) uses pmaddwd for 8 multiply-adds per instruction.
    /// </summary>
    public static void InverseTransformSse2(
        ReadOnlySpan<short> coefficients, Span<short> output,
        ReadOnlySpan<short> matrix, int size, int shift2, int add2)
    {
        int n2 = size * size;
        Span<int> temp = n2 <= 256 ? stackalloc int[n2] : new int[n2];

        // Pass 1: column transform (scalar — strided memory access)
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int sum = 0;
                for (int k = 0; k < size; k++)
                    sum += matrix[k * size + i] * coefficients[k * size + j];
                temp[i * size + j] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Pass 2: row transform with SSE2 pmaddwd inner dot products
        int padSize = (size + 7) & ~7; // Round up to multiple of 8
        Span<short> matColBuf = padSize <= 32 ? stackalloc short[padSize] : new short[padSize];
        Span<short> tempRowBuf = padSize <= 32 ? stackalloc short[padSize] : new short[padSize];
        matColBuf.Clear();
        tempRowBuf.Clear();

        for (int i = 0; i < size; i++)
        {
            int rowOffset = i * size;

            // Copy temp row to short buffer for SIMD consumption
            for (int k = 0; k < size; k++)
                tempRowBuf[k] = (short)temp[rowOffset + k];

            ref short tempBufRef = ref MemoryMarshal.GetReference(tempRowBuf);

            for (int j = 0; j < size; j++)
            {
                // Gather matrix column j into contiguous buffer
                for (int k = 0; k < size; k++)
                    matColBuf[k] = matrix[k * size + j];

                ref short matBufRef = ref MemoryMarshal.GetReference(matColBuf);

                // SIMD dot product: 8 multiply-adds per pmaddwd
                var acc = Vector128<int>.Zero;
                int k2 = 0;
                for (; k2 + 8 <= size; k2 += 8)
                {
                    var mv = Vector128.LoadUnsafe(ref matBufRef, (nuint)k2);
                    var tv = Vector128.LoadUnsafe(ref tempBufRef, (nuint)k2);
                    acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(mv, tv));
                }

                int sum = HorizontalSum(acc);

                // Handle remaining elements (sizes not multiple of 8)
                for (; k2 < size; k2++)
                    sum += matColBuf[k2] * tempRowBuf[k2];

                output[rowOffset + j] = (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
            }
        }
    }

    /// <summary>Horizontal sum of 4 ints in a Vector128.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalSum(Vector128<int> v)
    {
        var hi64 = Sse2.Shuffle(v, 0x0E); // [c, d, a, b]
        var sum64 = Sse2.Add(v, hi64);    // [a+c, b+d, ...]
        var hi32 = Sse2.Shuffle(sum64, 0x01); // [b+d, ...]
        return Sse2.Add(sum64, hi32).ToScalar();
    }
}
