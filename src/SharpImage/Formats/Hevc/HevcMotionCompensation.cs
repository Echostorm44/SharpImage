using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// Motion compensation for HEVC inter-prediction.
    /// Implements quarter-pixel interpolation with 8-tap luma and 4-tap chroma filters.
    /// </summary>
    /// <remarks>
    /// HEVC motion compensation differences from H.264:
    /// - 8-tap interpolation filter for luma (vs 6-tap in H.264)
    /// - 4-tap filter for chroma (vs bilinear in H.264)
    /// - Variable block sizes up to 64x64 (vs 16x16 in H.264)
    /// - Same quarter-pixel precision for both luma and chroma
    /// </remarks>
    public static class HevcMotionCompensation
    {
        #region Filter Coefficients

        /// <summary>
        /// HEVC luma interpolation filter coefficients (8-tap, DCT-based).
        /// Each row corresponds to a fractional position (0-3).
        /// Position 0 is integer (delta impulse), positions 1-3 are fractional.
        /// </summary>
        private static ReadOnlySpan<short> LumaFilter => new short[]
        {
            // Position 0 (integer): passthrough
              0,   0,   0,  64,   0,   0,   0,   0,
            // Position 1 (1/4 pixel)
             -1,   4, -10,  58,  17,  -5,   1,   0,
            // Position 2 (1/2 pixel)
             -1,   4, -11,  40,  40, -11,   4,  -1,
            // Position 3 (3/4 pixel)
              0,   1,  -5,  17,  58, -10,   4,  -1
        };

        /// <summary>
        /// HEVC chroma interpolation filter coefficients (4-tap).
        /// Each row corresponds to a fractional position (0-7).
        /// </summary>
        private static ReadOnlySpan<short> ChromaFilter => new short[]
        {
            // Position 0 (integer)
              0,  64,   0,   0,
            // Position 1 (1/8 pixel)
             -2,  58,  10,  -2,
            // Position 2 (2/8 pixel)
             -4,  54,  16,  -2,
            // Position 3 (3/8 pixel)
             -6,  46,  28,  -4,
            // Position 4 (4/8 pixel = 1/2)
             -4,  36,  36,  -4,
            // Position 5 (5/8 pixel)
             -4,  28,  46,  -6,
            // Position 6 (6/8 pixel)
             -2,  16,  54,  -4,
            // Position 7 (7/8 pixel)
             -2,  10,  58,  -2
        };

        #endregion

        #region Luma Motion Compensation

        /// <summary>
        /// Performs motion compensation for a variable-size luma block.
        /// </summary>
        /// <param name="reference">Reference frame luma plane.</param>
        /// <param name="refStride">Stride of reference frame.</param>
        /// <param name="output">Output prediction block.</param>
        /// <param name="outStride">Stride of output buffer.</param>
        /// <param name="width">Block width (4, 8, 16, 32, or 64).</param>
        /// <param name="height">Block height.</param>
        /// <param name="mvX">Horizontal motion vector in quarter-pixel units.</param>
        /// <param name="mvY">Vertical motion vector in quarter-pixel units.</param>
        /// <param name="blockX">Block X position in pixels.</param>
        /// <param name="blockY">Block Y position in pixels.</param>
        /// <param name="frameWidth">Reference frame width.</param>
        /// <param name="frameHeight">Reference frame height.</param>
        public static void CompensateLuma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight)
        {
            // Calculate full-pixel and fractional parts
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            if (fracX == 0 && fracY == 0)
            {
                // Integer position - direct copy
                CopyBlock(reference, refStride, output, outStride, width, height, fullX, fullY, frameWidth, frameHeight);
            }
            else if (fracX == 0)
            {
                // Vertical interpolation only
                InterpolateLumaVertical(reference, refStride, output, outStride, width, height,
                    fullX, fullY, fracY, frameWidth, frameHeight);
            }
            else if (fracY == 0)
            {
                // Horizontal interpolation only
                InterpolateLumaHorizontal(reference, refStride, output, outStride, width, height,
                    fullX, fullY, fracX, frameWidth, frameHeight);
            }
            else
            {
                // Both horizontal and vertical interpolation
                InterpolateLumaDiagonal(reference, refStride, output, outStride, width, height,
                    fullX, fullY, fracX, fracY, frameWidth, frameHeight);
            }
        }

        /// <summary>
        /// Horizontal luma interpolation using 8-tap filter.
        /// </summary>
        private static void InterpolateLumaHorizontal(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int fracX,
            int frameWidth,
            int frameHeight)
        {
            // Get filter for this fractional position
            int filterOffset = fracX * 8;

            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(y + row, 0, frameHeight - 1);

                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int srcX = Math.Clamp(x + col - 3 + k, 0, frameWidth - 1);
                        sum += LumaFilter[filterOffset + k] * reference[srcY * refStride + srcX];
                    }
                    // Round and clip (filter sum is 64, so shift by 6)
                    output[row * outStride + col] = (byte)Math.Clamp((sum + 32) >> 6, 0, 255);
                }
            }
        }

        /// <summary>
        /// Vertical luma interpolation using 8-tap filter.
        /// </summary>
        private static void InterpolateLumaVertical(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int fracY,
            int frameWidth,
            int frameHeight)
        {
            int filterOffset = fracY * 8;

            for (int col = 0; col < width; col++)
            {
                int srcX = Math.Clamp(x + col, 0, frameWidth - 1);

                for (int row = 0; row < height; row++)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int srcY = Math.Clamp(y + row - 3 + k, 0, frameHeight - 1);
                        sum += LumaFilter[filterOffset + k] * reference[srcY * refStride + srcX];
                    }
                    output[row * outStride + col] = (byte)Math.Clamp((sum + 32) >> 6, 0, 255);
                }
            }
        }

        /// <summary>
        /// Diagonal luma interpolation (horizontal then vertical).
        /// Matches ffmpeg put_hevc_qpel_uni_hv: first pass keeps full precision,
        /// second pass applies combined normalization.
        /// </summary>
        private static void InterpolateLumaDiagonal(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int fracX,
            int fracY,
            int frameWidth,
            int frameHeight)
        {
            // Temporary buffer for horizontal pass results
            // Need extra rows for vertical filter margin (7 extra rows: 3 before, 4 after)
            int tempHeight = height + 7;
            Span<short> temp = stackalloc short[width * tempHeight];

            int filterOffsetH = fracX * 8;
            int filterOffsetV = fracY * 8;

            // First pass: horizontal interpolation — keep full precision (no normalization).
            // FFmpeg: tmp[x] = QPEL_FILTER(src, 1) >> (BIT_DEPTH - 8); for 8-bit: >> 0.
            for (int row = 0; row < tempHeight; row++)
            {
                int srcY = Math.Clamp(y + row - 3, 0, frameHeight - 1);

                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int srcX = Math.Clamp(x + col - 3 + k, 0, frameWidth - 1);
                        sum += LumaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                    }
                    temp[row * width + col] = (short)sum;
                }
            }

            // Second pass: vertical interpolation with combined normalization.
            // FFmpeg: dst = av_clip_pixel(((QPEL_FILTER(tmp, stride) >> 6) + offset) >> shift)
            // For 8-bit: shift = 14 - 8 = 6, offset = 1 << 5 = 32.
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        sum += LumaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                    }
                    output[row * outStride + col] = (byte)Math.Clamp(((sum >> 6) + 32) >> 6, 0, 255);
                }
            }
        }

        #endregion

        #region Chroma Motion Compensation

        /// <summary>
        /// Performs motion compensation for chroma (Cb or Cr).
        /// Uses 4-tap filter with 1/8 pixel precision.
        /// </summary>
        public static void CompensateChroma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int planeWidth,
            int planeHeight)
        {
            // Chroma MVs in 4:2:0 are same precision as luma (quarter-pel for 4:2:0)
            // but applied to half-resolution plane, giving eighth-pel effective
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            if (fracX == 0 && fracY == 0)
            {
                CopyBlock(reference, refStride, output, outStride, width, height,
                    fullX, fullY, planeWidth, planeHeight);
            }
            else if (fracX == 0)
            {
                InterpolateChromaVertical(reference, refStride, output, outStride, width, height,
                    fullX, fullY, fracY, planeWidth, planeHeight);
            }
            else if (fracY == 0)
            {
                InterpolateChromaHorizontal(reference, refStride, output, outStride, width, height,
                    fullX, fullY, fracX, planeWidth, planeHeight);
            }
            else
            {
                InterpolateChromaDiagonal(reference, refStride, output, outStride, width, height,
                    fullX, fullY, fracX, fracY, planeWidth, planeHeight);
            }
        }

        /// <summary>
        /// Horizontal chroma interpolation using 4-tap filter.
        /// </summary>
        private static void InterpolateChromaHorizontal(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int fracX,
            int planeWidth,
            int planeHeight)
        {
            int filterOffset = fracX * 4;

            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(y + row, 0, planeHeight - 1);

                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int srcX = Math.Clamp(x + col - 1 + k, 0, planeWidth - 1);
                        sum += ChromaFilter[filterOffset + k] * reference[srcY * refStride + srcX];
                    }
                    output[row * outStride + col] = (byte)Math.Clamp((sum + 32) >> 6, 0, 255);
                }
            }
        }

        /// <summary>
        /// Vertical chroma interpolation using 4-tap filter.
        /// </summary>
        private static void InterpolateChromaVertical(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int fracY,
            int planeWidth,
            int planeHeight)
        {
            int filterOffset = fracY * 4;

            for (int col = 0; col < width; col++)
            {
                int srcX = Math.Clamp(x + col, 0, planeWidth - 1);

                for (int row = 0; row < height; row++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int srcY = Math.Clamp(y + row - 1 + k, 0, planeHeight - 1);
                        sum += ChromaFilter[filterOffset + k] * reference[srcY * refStride + srcX];
                    }
                    output[row * outStride + col] = (byte)Math.Clamp((sum + 32) >> 6, 0, 255);
                }
            }
        }

        /// <summary>
        /// Diagonal chroma interpolation.
        /// Matches ffmpeg put_hevc_epel_uni_hv: first pass full precision, second combined normalization.
        /// </summary>
        private static void InterpolateChromaDiagonal(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int fracX,
            int fracY,
            int planeWidth,
            int planeHeight)
        {
            int tempHeight = height + 3;
            Span<short> temp = stackalloc short[width * tempHeight];

            int filterOffsetH = fracX * 4;
            int filterOffsetV = fracY * 4;

            // First pass: horizontal — keep full precision (no normalization).
            // FFmpeg: tmp[x] = EPEL_FILTER(src, 1) >> (BIT_DEPTH - 8); for 8-bit: >> 0.
            for (int row = 0; row < tempHeight; row++)
            {
                int srcY = Math.Clamp(y + row - 1, 0, planeHeight - 1);

                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int srcX = Math.Clamp(x + col - 1 + k, 0, planeWidth - 1);
                        sum += ChromaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                    }
                    temp[row * width + col] = (short)sum;
                }
            }

            // Second pass: vertical with combined normalization.
            // For 8-bit: shift = 14 - 8 = 6, offset = 32.
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        sum += ChromaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                    }
                    output[row * outStride + col] = (byte)Math.Clamp(((sum >> 6) + 32) >> 6, 0, 255);
                }
            }
        }

        #endregion

        #region Bi-Prediction

        /// <summary>
        /// Bi-directional prediction (average of L0 and L1 predictions).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BiPrediction(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int stride,
            int width,
            int height)
        {
            int count = width * height;

            if (Sse2.IsSupported && count >= 16)
            {
                BiPredictionSse2(predL0, predL1, output, count);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    output[i] = (byte)((predL0[i] + predL1[i] + 1) >> 1);
                }
            }
        }

        private static unsafe void BiPredictionSse2(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int count)
        {
            fixed (byte* pL0 = predL0)
            fixed (byte* pL1 = predL1)
            fixed (byte* pOut = output)
            {
                int i = 0;
                for (; i + 16 <= count; i += 16)
                {
                    var l0 = Sse2.LoadVector128(pL0 + i);
                    var l1 = Sse2.LoadVector128(pL1 + i);
                    var avg = Sse2.Average(l0, l1);
                    Sse2.Store(pOut + i, avg);
                }

                // Handle remaining
                for (; i < count; i++)
                {
                    pOut[i] = (byte)((pL0[i] + pL1[i] + 1) >> 1);
                }
            }
        }

        #endregion

        #region Weighted Prediction

        /// <summary>
        /// Weighted uni-prediction.
        /// </summary>
        public static void WeightedPrediction(
            ReadOnlySpan<byte> pred,
            Span<byte> output,
            int width,
            int height,
            int weight,
            int offset,
            int logWeightDenom,
            int bitDepth = 8)
        {
            int maxValue = (1 << bitDepth) - 1;
            int shift = logWeightDenom;
            int round = shift > 0 ? 1 << (shift - 1) : 0;

            int count = width * height;
            for (int i = 0; i < count; i++)
            {
                int result = ((pred[i] * weight + round) >> shift) + offset;
                output[i] = (byte)Math.Clamp(result, 0, maxValue);
            }
        }

        /// <summary>
        /// Weighted bi-prediction.
        /// </summary>
        public static void WeightedBiPrediction(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int width,
            int height,
            int weightL0,
            int weightL1,
            int offsetL0,
            int offsetL1,
            int logWeightDenom,
            int bitDepth = 8)
        {
            int maxValue = (1 << bitDepth) - 1;
            int shift = logWeightDenom + 1;
            int round = 1 << logWeightDenom;
            int offsetSum = (offsetL0 + offsetL1 + 1) >> 1;

            int count = width * height;
            for (int i = 0; i < count; i++)
            {
                int result = ((predL0[i] * weightL0 + predL1[i] * weightL1 + round) >> shift) + offsetSum;
                output[i] = (byte)Math.Clamp(result, 0, maxValue);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Copies a block at integer position with boundary handling.
        /// </summary>
        private static void CopyBlock(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int frameWidth,
            int frameHeight)
        {
            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(y + row, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    int srcX = Math.Clamp(x + col, 0, frameWidth - 1);
                    output[row * outStride + col] = reference[srcY * refStride + srcX];
                }
            }
        }

        /// <summary>
        /// Performs motion compensation for high bit-depth (10-12 bit) luma.
        /// </summary>
        public static void CompensateLumaHighBitDepth(
            ReadOnlySpan<ushort> reference,
            int refStride,
            Span<ushort> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            if (fracX == 0 && fracY == 0)
            {
                // Integer copy
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[row * outStride + col] = reference[srcY * refStride + srcX];
                    }
                }
            }
            else
            {
                // Fractional - horizontal then vertical
                // Matches ffmpeg put_hevc_qpel_uni_hv for high bit depth
                int filterOffsetH = fracX * 8;
                int filterOffsetV = fracY * 8;
                int shift = 14 - bitDepth;
                int offset = 1 << (shift - 1);

                Span<int> temp = stackalloc int[width * (height + 7)];

                // Horizontal pass — partial normalization matching ffmpeg:
                // tmp[x] = QPEL_FILTER(src, 1) >> (BIT_DEPTH - 8)
                int hShift = bitDepth - 8;
                for (int row = 0; row < height + 7; row++)
                {
                    int srcY = Math.Clamp(fullY + row - 3, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 3 + k, 0, frameWidth - 1);
                            sum += LumaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                        }
                        temp[row * width + col] = sum >> hShift;
                    }
                }

                // Vertical pass — combined normalization:
                // dst = av_clip_pixel(((QPEL_FILTER(tmp, stride) >> 6) + offset) >> shift)
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            sum += LumaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                        }
                        output[row * outStride + col] = (ushort)Math.Clamp(((sum >> 6) + offset) >> shift, 0, maxValue);
                    }
                }
            }
        }

        /// <summary>
        /// Luma MC producing intermediate ~14-bit precision output for bi-prediction.
        /// Output values are NOT clipped to pixel range; they must be combined with the
        /// other reference's intermediate values before final clipping.
        /// </summary>
        public static void CompensateLumaHighBitDepthIntermediate(
            ReadOnlySpan<ushort> reference,
            int refStride,
            Span<int> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight,
            int bitDepth)
        {
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;
            int shift14 = 14 - bitDepth;

            if (fracX == 0 && fracY == 0)
            {
                // Integer: pixel << (14 - bitDepth) to reach ~14-bit precision
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[row * outStride + col] = reference[srcY * refStride + srcX] << shift14;
                    }
                }
            }
            else
            {
                int filterOffsetH = fracX * 8;
                int filterOffsetV = fracY * 8;
                int hShift = bitDepth - 8;

                Span<int> temp = stackalloc int[width * (height + 7)];

                // Horizontal pass: same as uni
                for (int row = 0; row < height + 7; row++)
                {
                    int srcY = Math.Clamp(fullY + row - 3, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 3 + k, 0, frameWidth - 1);
                            sum += LumaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                        }
                        temp[row * width + col] = sum >> hShift;
                    }
                }

                // Vertical pass: output intermediate (sum >> 6) without clip
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            sum += LumaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                        }
                        output[row * outStride + col] = sum >> 6;
                    }
                }
            }
        }

        /// <summary>
        /// 8-bit luma intermediate MC for bi-prediction. Outputs ~14-bit precision values (Span&lt;int&gt;)
        /// instead of clipped 8-bit. Uses the two-pass (horizontal + vertical) approach:
        /// integer-pel → pixel &lt;&lt; 6; fractional → H-pass then V-pass with &gt;&gt; 6.
        /// </summary>
        public static void CompensateLumaIntermediate(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<int> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight)
        {
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            if (fracX == 0 && fracY == 0)
            {
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[row * outStride + col] = reference[srcY * refStride + srcX] << 6;
                    }
                }
            }
            else
            {
                int filterOffsetH = fracX * 8;
                int filterOffsetV = fracY * 8;

                Span<int> temp = stackalloc int[width * (height + 7)];

                // Horizontal pass: no shift for 8-bit (hShift = bitDepth - 8 = 0)
                for (int row = 0; row < height + 7; row++)
                {
                    int srcY = Math.Clamp(fullY + row - 3, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 3 + k, 0, frameWidth - 1);
                            sum += LumaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                        }
                        temp[row * width + col] = sum;
                    }
                }

                // Vertical pass: output at intermediate precision (sum >> 6)
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            sum += LumaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                        }
                        output[row * outStride + col] = sum >> 6;
                    }
                }
            }
        }

        /// <summary>
        /// 8-bit chroma intermediate MC for bi-prediction. Outputs ~14-bit precision values.
        /// Uses 4-tap chroma filter with fractional positions in 1/8-pel units.
        /// </summary>
        public static void CompensateChromaIntermediate(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<int> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight)
        {
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            if (fracX == 0 && fracY == 0)
            {
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[row * outStride + col] = reference[srcY * refStride + srcX] << 6;
                    }
                }
            }
            else
            {
                int filterOffsetH = fracX * 4;
                int filterOffsetV = fracY * 4;

                Span<int> temp = stackalloc int[width * (height + 3)];

                // Horizontal pass (4-tap chroma, no shift for 8-bit)
                for (int row = 0; row < height + 3; row++)
                {
                    int srcY = Math.Clamp(fullY + row - 1, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 1 + k, 0, frameWidth - 1);
                            sum += ChromaFilter[filterOffsetH + k] * reference[srcY * refStride + srcX];
                        }
                        temp[row * width + col] = sum;
                    }
                }

                // Vertical pass: output at intermediate precision (sum >> 6)
                for (int row = 0; row < height; row++)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            sum += ChromaFilter[filterOffsetV + k] * temp[(row + k) * width + col];
                        }
                        output[row * outStride + col] = sum >> 6;
                    }
                }
            }
        }

        #endregion
    }
}
