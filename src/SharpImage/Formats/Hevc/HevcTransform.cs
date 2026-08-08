using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// Integer transforms for HEVC/H.265 video decoding.
    /// Implements HEVC-specific DCT-II and DST-VII transforms.
    /// </summary>
    /// <remarks>
    /// HEVC uses different transforms than H.264:
    /// - 4x4 DST-VII for intra prediction (ITU-T H.265 Table 8-7)
    /// - 4x4, 8x8, 16x16, 32x32 DCT-II for all other cases (ITU-T H.265 Table 8-6)
    /// - Transform Skip mode for specific blocks
    /// </remarks>
    public static class HevcTransform
    {
        #region DCT Coefficient Tables

        /// <summary>
        /// 4x4 DCT-II transform matrix coefficients.
        /// Row i contains the coefficients for frequency i.
        /// </summary>
        private static ReadOnlySpan<short> Dct4x4 => new short[]
        {
            64,  64,  64,  64,   // k=0 (DC)
            83,  36, -36, -83,   // k=1
            64, -64, -64,  64,   // k=2
            36, -83,  83, -36    // k=3
        };

        /// <summary>
        /// 4x4 DST-VII transform matrix coefficients for intra blocks.
        /// </summary>
        private static ReadOnlySpan<short> Dst4x4 => new short[]
        {
            29,  55,  74,  84,   // k=0
            74,  74,   0, -74,   // k=1
            84, -29, -74,  55,   // k=2
            55, -84,  74, -29    // k=3
        };

        /// <summary>
        /// 8x8 DCT-II transform matrix coefficients.
        /// </summary>
        private static ReadOnlySpan<short> Dct8x8 => new short[]
        {
            64,  64,  64,  64,  64,  64,  64,  64,   // k=0
            89,  75,  50,  18, -18, -50, -75, -89,   // k=1
            83,  36, -36, -83, -83, -36,  36,  83,   // k=2
            75, -18, -89, -50,  50,  89,  18, -75,   // k=3
            64, -64, -64,  64,  64, -64, -64,  64,   // k=4
            50, -89,  18,  75, -75, -18,  89, -50,   // k=5
            36, -83,  83, -36, -36,  83, -83,  36,   // k=6
            18, -50,  75, -89,  89, -75,  50, -18    // k=7
        };

        /// <summary>
        /// 16x16 DCT-II transform matrix coefficients.
        /// </summary>
        private static ReadOnlySpan<short> Dct16x16 => new short[]
        {
            // Row 0 (DC)
            64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
            // Row 1
            90, 87, 80, 70, 57, 43, 25, 9, -9, -25, -43, -57, -70, -80, -87, -90,
            // Row 2
            89, 75, 50, 18, -18, -50, -75, -89, -89, -75, -50, -18, 18, 50, 75, 89,
            // Row 3
            87, 57, 9, -43, -80, -90, -70, -25, 25, 70, 90, 80, 43, -9, -57, -87,
            // Row 4
            83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83,
            // Row 5
            80, 9, -70, -87, -25, 57, 90, 43, -43, -90, -57, 25, 87, 70, -9, -80,
            // Row 6
            75, -18, -89, -50, 50, 89, 18, -75, -75, 18, 89, 50, -50, -89, -18, 75,
            // Row 7
            70, -43, -87, 9, 90, 25, -80, -57, 57, 80, -25, -90, -9, 87, 43, -70,
            // Row 8
            64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64,
            // Row 9
            57, -80, -25, 90, -9, -87, 43, 70, -70, -43, 87, 9, -90, 25, 80, -57,
            // Row 10
            50, -89, 18, 75, -75, -18, 89, -50, -50, 89, -18, -75, 75, 18, -89, 50,
            // Row 11
            43, -90, 57, 25, -87, 70, 9, -80, 80, -9, -70, 87, -25, -57, 90, -43,
            // Row 12
            36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36,
            // Row 13
            25, -70, 90, -80, 43, 9, -57, 87, -87, 57, -9, -43, 80, -90, 70, -25,
            // Row 14
            18, -50, 75, -89, 89, -75, 50, -18, -18, 50, -75, 89, -89, 75, -50, 18,
            // Row 15
            9, -25, 43, -57, 70, -80, 87, -90, 90, -87, 80, -70, 57, -43, 25, -9
        };

        /// <summary>
        /// Full 32x32 DCT-II transform matrix (ITU-T H.265 Table 8-6).
        /// Row k contains coefficients for frequency k: T[k][n] for n=0..31.
        /// </summary>
        private static ReadOnlySpan<short> Dct32x32 => new short[]
        {
            // Row 0 (DC)
            64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
            64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
            // Row 1
            90, 90, 88, 85, 82, 78, 73, 67, 61, 54, 46, 38, 31, 22, 13, 4,
            -4,-13,-22,-31,-38,-46,-54,-61,-67,-73,-78,-82,-85,-88,-90,-90,
            // Row 2
            90, 87, 80, 70, 57, 43, 25, 9, -9,-25,-43,-57,-70,-80,-87,-90,
           -90,-87,-80,-70,-57,-43,-25, -9,  9, 25, 43, 57, 70, 80, 87, 90,
            // Row 3
            90, 82, 67, 46, 22, -4,-31,-54,-73,-85,-90,-88,-78,-61,-38,-13,
            13, 38, 61, 78, 88, 90, 85, 73, 54, 31,  4,-22,-46,-67,-82,-90,
            // Row 4
            89, 75, 50, 18,-18,-50,-75,-89,-89,-75,-50,-18, 18, 50, 75, 89,
            89, 75, 50, 18,-18,-50,-75,-89,-89,-75,-50,-18, 18, 50, 75, 89,
            // Row 5
            88, 67, 31,-13,-54,-82,-90,-78,-46, -4, 38, 73, 90, 85, 61, 22,
           -22,-61,-85,-90,-73,-38,  4, 46, 78, 90, 82, 54, 13,-31,-67,-88,
            // Row 6
            87, 57,  9,-43,-80,-90,-70,-25, 25, 70, 90, 80, 43, -9,-57,-87,
           -87,-57, -9, 43, 80, 90, 70, 25,-25,-70,-90,-80,-43,  9, 57, 87,
            // Row 7
            85, 46,-13,-67,-90,-73,-22, 38, 82, 88, 54, -4,-61,-90,-78,-31,
            31, 78, 90, 61,  4,-54,-88,-82,-38, 22, 73, 90, 67, 13,-46,-85,
            // Row 8
            83, 36,-36,-83,-83,-36, 36, 83, 83, 36,-36,-83,-83,-36, 36, 83,
            83, 36,-36,-83,-83,-36, 36, 83, 83, 36,-36,-83,-83,-36, 36, 83,
            // Row 9
            82, 22,-54,-90,-61, 13, 78, 85, 31,-46,-90,-67,  4, 73, 88, 38,
           -38,-88,-73, -4, 67, 90, 46,-31,-85,-78,-13, 61, 90, 54,-22,-82,
            // Row 10
            80,  9,-70,-87,-25, 57, 90, 43,-43,-90,-57, 25, 87, 70, -9,-80,
           -80, -9, 70, 87, 25,-57,-90,-43, 43, 90, 57,-25,-87,-70,  9, 80,
            // Row 11
            78, -4,-82,-73, 13, 85, 67,-22,-88,-61, 31, 90, 54,-38,-90,-46,
            46, 90, 38,-54,-90,-31, 61, 88, 22,-67,-85,-13, 73, 82,  4,-78,
            // Row 12
            75,-18,-89,-50, 50, 89, 18,-75,-75, 18, 89, 50,-50,-89,-18, 75,
            75,-18,-89,-50, 50, 89, 18,-75,-75, 18, 89, 50,-50,-89,-18, 75,
            // Row 13
            73,-31,-90,-22, 78, 67,-38,-90,-13, 82, 61,-46,-88, -4, 85, 54,
           -54,-85,  4, 88, 46,-61,-82, 13, 90, 38,-67,-78, 22, 90, 31,-73,
            // Row 14
            70,-43,-87,  9, 90, 25,-80,-57, 57, 80,-25,-90, -9, 87, 43,-70,
           -70, 43, 87, -9,-90,-25, 80, 57,-57,-80, 25, 90,  9,-87,-43, 70,
            // Row 15
            67,-54,-78, 38, 85,-22,-90,  4, 90, 13,-88,-31, 82, 46,-73,-61,
            61, 73,-46,-82, 31, 88,-13,-90, -4, 90, 22,-85,-38, 78, 54,-67,
            // Row 16
            64,-64,-64, 64, 64,-64,-64, 64, 64,-64,-64, 64, 64,-64,-64, 64,
            64,-64,-64, 64, 64,-64,-64, 64, 64,-64,-64, 64, 64,-64,-64, 64,
            // Row 17
            61,-73,-46, 82, 31,-88,-13, 90, -4,-90, 22, 85,-38,-78, 54, 67,
           -67,-54, 78, 38,-85,-22, 90,  4,-90, 13, 88,-31,-82, 46, 73,-61,
            // Row 18
            57,-80,-25, 90, -9,-87, 43, 70,-70,-43, 87,  9,-90, 25, 80,-57,
           -57, 80, 25,-90,  9, 87,-43,-70, 70, 43,-87, -9, 90,-25,-80, 57,
            // Row 19
            54,-85, -4, 88,-46,-61, 82, 13,-90, 38, 67,-78,-22, 90,-31,-73,
            73, 31,-90, 22, 78,-67,-38, 90,-13,-82, 61, 46,-88,  4, 85,-54,
            // Row 20
            50,-89, 18, 75,-75,-18, 89,-50,-50, 89,-18,-75, 75, 18,-89, 50,
            50,-89, 18, 75,-75,-18, 89,-50,-50, 89,-18,-75, 75, 18,-89, 50,
            // Row 21
            46,-90, 38, 54,-90, 31, 61,-88, 22, 67,-85, 13, 73,-82,  4, 78,
           -78, -4, 82,-73,-13, 85,-67,-22, 88,-61,-31, 90,-54,-38, 90,-46,
            // Row 22
            43,-90, 57, 25,-87, 70,  9,-80, 80, -9,-70, 87,-25,-57, 90,-43,
           -43, 90,-57,-25, 87,-70, -9, 80,-80,  9, 70,-87, 25, 57,-90, 43,
            // Row 23
            38,-88, 73, -4,-67, 90,-46,-31, 85,-78, 13, 61,-90, 54, 22,-82,
            82,-22,-54, 90,-61,-13, 78,-85, 31, 46,-90, 67,  4,-73, 88,-38,
            // Row 24
            36,-83, 83,-36,-36, 83,-83, 36, 36,-83, 83,-36,-36, 83,-83, 36,
            36,-83, 83,-36,-36, 83,-83, 36, 36,-83, 83,-36,-36, 83,-83, 36,
            // Row 25
            31,-78, 90,-61,  4, 54,-88, 82,-38,-22, 73,-90, 67,-13,-46, 85,
           -85, 46, 13,-67, 90,-73, 22, 38,-82, 88,-54, -4, 61,-90, 78,-31,
            // Row 26
            25,-70, 90,-80, 43,  9,-57, 87,-87, 57, -9,-43, 80,-90, 70,-25,
           -25, 70,-90, 80,-43, -9, 57,-87, 87,-57,  9, 43,-80, 90,-70, 25,
            // Row 27
            22,-61, 85,-90, 73,-38, -4, 46,-78, 90,-82, 54,-13,-31, 67,-88,
            88,-67, 31, 13,-54, 82,-90, 78,-46,  4, 38,-73, 90,-85, 61,-22,
            // Row 28
            18,-50, 75,-89, 89,-75, 50,-18,-18, 50,-75, 89,-89, 75,-50, 18,
            18,-50, 75,-89, 89,-75, 50,-18,-18, 50,-75, 89,-89, 75,-50, 18,
            // Row 29
            13,-38, 61,-78, 88,-90, 85,-73, 54,-31,  4, 22,-46, 67,-82, 90,
           -90, 82,-67, 46,-22, -4, 31,-54, 73,-85, 90,-88, 78,-61, 38,-13,
            // Row 30
             9,-25, 43,-57, 70,-80, 87,-90, 90,-87, 80,-70, 57,-43, 25, -9,
            -9, 25,-43, 57,-70, 80,-87, 90,-90, 87,-80, 70,-57, 43,-25,  9,
            // Row 31
             4,-13, 22,-31, 38,-46, 54,-61, 67,-73, 78,-82, 85,-88, 90,-90,
            90,-90, 88,-85, 82,-78, 73,-67, 61,-54, 46,-38, 31,-22, 13, -4
        };

        #endregion

        #region Inverse Transform Methods

        /// <summary>
        /// Performs inverse transform on HEVC coefficients.
        /// Automatically selects appropriate transform size and type.
        /// </summary>
        /// <param name="coefficients">Input quantized coefficients.</param>
        /// <param name="output">Output residual samples.</param>
        /// <param name="size">Transform size (4, 8, 16, or 32).</param>
        /// <param name="isIntra">True for intra prediction (uses DST for 4x4).</param>
        /// <param name="bitDepth">Bit depth (affects 2nd pass shift: 20 - bitDepth).</param>
        public static void InverseTransform(
            ReadOnlySpan<short> coefficients,
            Span<short> output,
            int size,
            bool isIntra = false,
            int bitDepth = 8)
        {
            // HEVC spec 8.6.4.2: 2nd pass shift = 20 - BitDepth
            int shift2 = 20 - bitDepth;
            int add2 = 1 << (shift2 - 1);

            switch (size)
            {
                case 4:
                    if (isIntra)
                        InverseTransformDst4x4(coefficients, output, shift2, add2);
                    else
                        InverseTransformDct4x4(coefficients, output, shift2, add2);
                    break;
                case 8:
                    InverseTransformDct8x8(coefficients, output, shift2, add2);
                    break;
                case 16:
                    InverseTransformDct16x16(coefficients, output, shift2, add2);
                    break;
                case 32:
                    InverseTransformDct32x32(coefficients, output, shift2, add2);
                    break;
                default:
                    throw new ArgumentException($"Unsupported transform size: {size}", nameof(size));
            }
        }

        /// <summary>
        /// 4x4 inverse DCT-II transform — delegates to shared DSP.
        /// </summary>
        public static void InverseTransformDct4x4(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2 = 12, int add2 = 2048)
        {
            HevcTransformDsp.InverseTransformDct4x4(coefficients, output, shift2, add2);
        }

        /// <summary>
        /// 4x4 inverse DST-VII transform for intra blocks — delegates to shared DSP.
        /// </summary>
        public static void InverseTransformDst4x4(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2 = 12, int add2 = 2048)
        {
            HevcTransformDsp.InverseTransformDst4x4(coefficients, output, shift2, add2);
        }

        /// <summary>
        /// 8x8 inverse DCT-II transform — delegates to shared DSP (SSE2 pass 2).
        /// </summary>
        public static void InverseTransformDct8x8(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2 = 12, int add2 = 2048)
        {
            HevcTransformDsp.InverseTransformDct8x8(coefficients, output, shift2, add2);
        }

        private static void InverseTransformDct8x8Scalar(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2, int add2)
        {
            Span<int> temp = stackalloc int[64];

            // First pass: column transform
            for (int j = 0; j < 8; j++)
            {
                for (int i = 0; i < 8; i++)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                        sum += Dct8x8[k * 8 + i] * coefficients[k * 8 + j];
                    temp[i * 8 + j] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
                }
            }

            // Second pass: row transform
            for (int i = 0; i < 8; i++)
            {
                int rowOffset = i * 8;
                for (int j = 0; j < 8; j++)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                        sum += Dct8x8[k * 8 + j] * temp[rowOffset + k];
                    output[rowOffset + j] = (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
                }
            }
        }

        private static void InverseTransformDct8x8Avx2(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2, int add2)
        {
            InverseTransformDct8x8Scalar(coefficients, output, shift2, add2);
        }

        /// <summary>
        /// 16x16 inverse DCT-II transform — delegates to shared DSP (SSE2 pass 2).
        /// </summary>
        public static void InverseTransformDct16x16(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2 = 12, int add2 = 2048)
        {
            HevcTransformDsp.InverseTransform(coefficients, output, Dct16x16, 16, shift2, add2);
        }

        private static void InverseTransformDct16x16Scalar(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2, int add2)
        {
            Span<int> temp = stackalloc int[256];

            // First pass: column transform
            for (int j = 0; j < 16; j++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int sum = 0;
                    for (int k = 0; k < 16; k++)
                        sum += Dct16x16[k * 16 + i] * coefficients[k * 16 + j];
                    temp[i * 16 + j] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
                }
            }

            // Second pass: row transform
            for (int i = 0; i < 16; i++)
            {
                int rowOffset = i * 16;
                for (int j = 0; j < 16; j++)
                {
                    int sum = 0;
                    for (int k = 0; k < 16; k++)
                        sum += Dct16x16[k * 16 + j] * temp[rowOffset + k];
                    output[rowOffset + j] = (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
                }
            }
        }

        private static void InverseTransformDct16x16Avx2(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2, int add2)
        {
            InverseTransformDct16x16Scalar(coefficients, output, shift2, add2);
        }

        /// <summary>
        /// 32x32 inverse DCT-II transform — delegates to shared DSP (SSE2 pass 2).
        /// </summary>
        public static void InverseTransformDct32x32(ReadOnlySpan<short> coefficients, Span<short> output,
            int shift2 = 12, int add2 = 2048)
        {
            HevcTransformDsp.InverseTransform(coefficients, output, Dct32x32, 32, shift2, add2);
        }

        private static void InverseTransformDct32x32Scalar(
            ReadOnlySpan<short> coefficients, Span<short> output, int shift2, int add2)
        {
            ReadOnlySpan<short> dct = Dct32x32;

            Span<int> temp = stackalloc int[1024];

            // First pass: column transform
            for (int j = 0; j < 32; j++)
            {
                for (int i = 0; i < 32; i++)
                {
                    int sum = 0;
                    for (int k = 0; k < 32; k++)
                        sum += dct[k * 32 + i] * coefficients[k * 32 + j];
                    temp[i * 32 + j] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
                }
            }

            // Second pass: row transform
            for (int i = 0; i < 32; i++)
            {
                int rowOffset = i * 32;
                for (int j = 0; j < 32; j++)
                {
                    int sum = 0;
                    for (int k = 0; k < 32; k++)
                        sum += dct[k * 32 + j] * temp[rowOffset + k];
                    output[rowOffset + j] = (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
                }
            }
        }

        #endregion

        #region Transform Skip

        /// <summary>
        /// Applies transform skip (identity transform) for a block.
        /// Simply copies coefficients to output with appropriate shift.
        /// </summary>
        /// <param name="coefficients">Input dequantized coefficients.</param>
        /// <param name="output">Output residual samples.</param>
        /// <param name="log2TrafoSize">Log2 of the transform block size.</param>
        /// <param name="bitDepth">Bit depth of samples.</param>
        public static void TransformSkip(
            ReadOnlySpan<short> coefficients,
            Span<short> output,
            int log2TrafoSize,
            int bitDepth = 8)
        {
            // FFmpeg dsp_template.c dequant(): shift = 15 - BIT_DEPTH - log2_size
            // For 12-bit + large TU, shift can be 0 or negative
            int size = 1 << log2TrafoSize;
            int shift = 15 - bitDepth - log2TrafoSize;
            int count = size * size;

            if (shift > 0)
            {
                int add = 1 << (shift - 1);
                for (int i = 0; i < count; i++)
                    output[i] = (short)((coefficients[i] + add) >> shift);
            }
            else if (shift < 0)
            {
                // 12-bit + 16x16 or 32x32: left-shift
                int negShift = -shift;
                for (int i = 0; i < count; i++)
                    output[i] = (short)((ushort)coefficients[i] << negShift);
            }
            else
            {
                // shift == 0: identity (12-bit + 8x8)
                coefficients.Slice(0, count).CopyTo(output);
            }
        }

        #endregion

        #region Dequantization

        /// <summary>
        /// Dequantizes HEVC coefficients using flat scaling.
        /// </summary>
        /// <param name="coefficients">Quantized coefficients (modified in place).</param>
        /// <param name="qp">Quantization parameter (0-51).</param>
        /// <param name="size">Block size (4, 8, 16, or 32).</param>
        /// <param name="bitDepth">Bit depth of samples.</param>
        public static void Dequantize(
            Span<short> coefficients,
            int qp,
            int size,
            int bitDepth = 8)
        {
            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            // HEVC uses fixed scale factors
            ReadOnlySpan<int> scaleFactors = stackalloc int[] { 40, 45, 51, 57, 64, 72 };
            int scale = scaleFactors[qpMod6];

            // Compute shift based on block size and bit depth
            int log2Size = size switch
            {
                4 => 2,
                8 => 3,
                16 => 4,
                32 => 5,
                _ => 2
            };
            int shift = bitDepth - 9 + log2Size;
            int offset = 1 << (shift - 1);

            int count = size * size;
            for (int i = 0; i < count; i++)
            {
                int level = coefficients[i];
                // Apply scale and shift
                coefficients[i] = (short)((level * scale << qpDiv6 + offset) >> shift);
            }
        }

        /// <summary>
        /// Dequantizes using custom scaling matrix.
        /// </summary>
        public static void DequantizeWithMatrix(
            Span<short> coefficients,
            ReadOnlySpan<int> scalingMatrix,
            int qp,
            int size,
            int bitDepth = 8)
        {
            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            ReadOnlySpan<int> scaleFactors = stackalloc int[] { 40, 45, 51, 57, 64, 72 };
            int qpScale = scaleFactors[qpMod6];

            int log2Size = size switch
            {
                4 => 2,
                8 => 3,
                16 => 4,
                32 => 5,
                _ => 2
            };
            int shift = bitDepth - 9 + log2Size;
            int offset = 1 << (shift - 1);

            int count = size * size;
            for (int i = 0; i < count; i++)
            {
                int level = coefficients[i];
                int matrixScale = scalingMatrix[i];
                coefficients[i] = (short)((level * matrixScale * qpScale << qpDiv6 + offset) >> shift);
            }
        }

        #endregion

        #region Reconstruction

        /// <summary>
        /// Reconstructs samples by adding residual to prediction.
        /// </summary>
        /// <param name="prediction">Predicted samples.</param>
        /// <param name="residual">Residual after inverse transform.</param>
        /// <param name="output">Reconstructed samples.</param>
        /// <param name="size">Block size.</param>
        /// <param name="bitDepth">Bit depth of samples.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reconstruct(
            ReadOnlySpan<byte> prediction,
            ReadOnlySpan<short> residual,
            Span<byte> output,
            int size,
            int bitDepth = 8)
        {
            int maxValue = (1 << bitDepth) - 1;
            int count = size * size;

            if (Sse2.IsSupported && count >= 16)
            {
                ReconstructSse2(prediction, residual, output, count, maxValue);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int value = prediction[i] + residual[i];
                    output[i] = (byte)Math.Clamp(value, 0, maxValue);
                }
            }
        }

        private static unsafe void ReconstructSse2(
            ReadOnlySpan<byte> prediction,
            ReadOnlySpan<short> residual,
            Span<byte> output,
            int count,
            int maxValue)
        {
            fixed (byte* pPred = prediction)
            fixed (short* pRes = residual)
            fixed (byte* pOut = output)
            {
                int i = 0;
                for (; i + 16 <= count; i += 16)
                {
                    // Load 16 prediction bytes and zero-extend to shorts
                    var pred = Sse2.LoadVector128(pPred + i);
                    var predLo = Sse2.UnpackLow(pred, Vector128<byte>.Zero).AsInt16();
                    var predHi = Sse2.UnpackHigh(pred, Vector128<byte>.Zero).AsInt16();

                    // Load 16 residuals
                    var resLo = Sse2.LoadVector128(pRes + i);
                    var resHi = Sse2.LoadVector128(pRes + i + 8);

                    // Add
                    var sumLo = Sse2.Add(predLo, resLo);
                    var sumHi = Sse2.Add(predHi, resHi);

                    // Pack with unsigned saturation
                    var result = Sse2.PackUnsignedSaturate(sumLo, sumHi);

                    // Store
                    Sse2.Store(pOut + i, result);
                }

                // Handle remaining elements
                for (; i < count; i++)
                {
                    int value = pPred[i] + pRes[i];
                    pOut[i] = (byte)Math.Clamp(value, 0, maxValue);
                }
            }
        }

        /// <summary>
        /// Reconstructs high bit-depth samples (10-12 bit).
        /// </summary>
        public static void ReconstructHighBitDepth(
            ReadOnlySpan<ushort> prediction,
            ReadOnlySpan<short> residual,
            Span<ushort> output,
            int size,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int count = size * size;

            for (int i = 0; i < count; i++)
            {
                int value = prediction[i] + residual[i];
                output[i] = (ushort)Math.Clamp(value, 0, maxValue);
            }
        }

        #endregion
    }
}
